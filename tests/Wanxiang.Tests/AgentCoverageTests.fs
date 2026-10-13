namespace Wanxiang.Tests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open System.ClientModel
open Xunit
open Wanxiang.Agent
open Wanxiang.Config
open Wanxiang.Core

module AgentCoverageTests =

    // =========================================================================
    // 辅助工具与 Mock 实现
    // =========================================================================

    type FakeHttpMessageHandler(handler: HttpRequestMessage -> HttpResponseMessage) =
        inherit HttpMessageHandler()
        override _.SendAsync(request, cancellationToken) =
            Task.FromResult(handler request)

    type AsyncFakeHttpMessageHandler(handler: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage>) =
        inherit HttpMessageHandler()
        override _.SendAsync(request, cancellationToken) =
            handler request cancellationToken

    let dummyProvider kind : ProviderConfig = {
        id = "test-provider"
        kind = kind
        label = "Test Provider"
        baseUrl = "https://api.example.com/v1"
        apiKey = Some "test-key"
        defaultModel = "default-model"
        models = [ "model-a"; "model-b" ]
        timeoutSeconds = 30
        maxRetries = 2
        enabled = true
        promptCaching = false
        headers = Map.empty
        extraJson = None; visionOverrides = Map.empty
    }

    let dummySession : SessionConfig = {
        provider = "test-provider"
        model = "model-a"
        instructions = Some "System prompt instructions"
        tools = []
        temperature = Some 0.7
        topP = Some 0.9
        maxTokens = Some 1024
        thinkingBudget = Some 0
        extraJson = None
    }

    // =========================================================================
    // 1. ProviderFailure 错误分类与 Retry-After 解析测试
    // =========================================================================

    [<Fact>]
    let ``ProviderFailure retryAfterOfResponse parses delta, date, and string headers`` () =
        // 1. null response
        Assert.Equal(None, ProviderFailure.retryAfterOfResponse null)

        // 2. empty headers
        use respEmpty = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        Assert.Equal(None, ProviderFailure.retryAfterOfResponse respEmpty)

        // 3. RetryConditionHeaderValue with Delta (TimeSpan)
        use respDelta = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        respDelta.Headers.RetryAfter <- RetryConditionHeaderValue(TimeSpan.FromSeconds 42.0)
        Assert.Equal(Some 42, ProviderFailure.retryAfterOfResponse respDelta)

        // 4. RetryConditionHeaderValue with Date (DateTimeOffset)
        use respDate = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        let future = DateTimeOffset.UtcNow.AddSeconds 30.0
        respDate.Headers.RetryAfter <- RetryConditionHeaderValue(future)
        let parsed = ProviderFailure.retryAfterOfResponse respDate
        Assert.True(parsed.IsSome && parsed.Value >= 28 && parsed.Value <= 32)

    [<Fact>]
    let ``ProviderFailure classify HttpRequestException status codes and network errors`` () =
        let pLabel = "TestProvider"
        let model = "test-model"

        // 400 Bad Request
        let hre400 = HttpRequestException("Bad request", null, HttpStatusCode.BadRequest)
        let f400 = ProviderFailure.classify pLabel model hre400
        Assert.Equal(ProviderBadRequest, f400.kind)
        Assert.False(f400.retryable)

        // 401 Unauthorized
        let hre401 = HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized)
        let f401 = ProviderFailure.classify pLabel model hre401
        Assert.Equal(ProviderAuthFailed, f401.kind)
        Assert.False(f401.retryable)

        // 404 Not Found
        let hre404 = HttpRequestException("Not found", null, HttpStatusCode.NotFound)
        let f404 = ProviderFailure.classify pLabel model hre404
        Assert.Equal(ModelNotFound, f404.kind)
        Assert.False(f404.retryable)

        // 408 Request Timeout
        let hre408 = HttpRequestException("Timeout", null, HttpStatusCode.RequestTimeout)
        let f408 = ProviderFailure.classify pLabel model hre408
        Assert.Equal(ProviderTimeout, f408.kind)
        Assert.True(f408.retryable)

        // 429 Rate Limited with RetryAfterSeconds in Data
        let hre429 = HttpRequestException("Rate limited", null, HttpStatusCode.TooManyRequests)
        hre429.Data.["RetryAfterSeconds"] <- 60
        let f429 = ProviderFailure.classify pLabel model hre429
        Assert.Equal(ProviderRateLimited, f429.kind)
        Assert.True(f429.retryable)
        Assert.Equal(Some 60, f429.retryAfterSeconds)

        // 500 Internal Server Error
        let hre500 = HttpRequestException("Internal server error", null, HttpStatusCode.InternalServerError)
        let f500 = ProviderFailure.classify pLabel model hre500
        Assert.Equal(ProviderUnavailable, f500.kind)
        Assert.True(f500.retryable)

        // Without status code (network failure)
        let hreNetwork = HttpRequestException("Connection refused")
        let fNet = ProviderFailure.classify pLabel model hreNetwork
        Assert.Equal(ProviderUnavailable, fNet.kind)
        Assert.True(fNet.retryable)

    [<Fact>]
    let ``ProviderFailure classify SocketException and generic exn patterns`` () =
        let pLabel = "TestProvider"
        let model = "test-model"

        // SocketException
        let se = SocketException(int SocketError.ConnectionRefused)
        let fSe = ProviderFailure.classify pLabel model se
        Assert.Equal(ProviderUnavailable, fSe.kind)
        Assert.True(fSe.retryable)

        // exn with inner SocketException
        let wrappedSe = Exception("Transport failure", se)
        let fWrapped = ProviderFailure.classify pLabel model wrappedSe
        Assert.Equal(ProviderUnavailable, fWrapped.kind)
        Assert.True(fWrapped.retryable)

        // exn with body signals
        let exContext = Exception("Error: context_length_exceeded in prompt")
        Assert.Equal(ContextTooLong, (ProviderFailure.classify pLabel model exContext).kind)

        let exFilter = Exception("Error: content_filter triggered by safety system")
        Assert.Equal(ContentFiltered, (ProviderFailure.classify pLabel model exFilter).kind)

        let exModel = Exception("Error: model_not_found requested model does not exist")
        Assert.Equal(ModelNotFound, (ProviderFailure.classify pLabel model exModel).kind)

        let exAuth = Exception("Error: invalid_api_key provided")
        Assert.Equal(ProviderAuthFailed, (ProviderFailure.classify pLabel model exAuth).kind)

        // HTTP status phrases in ex.Message
        let ex502 = Exception("Received 502 Bad Gateway from upstream")
        let f502 = ProviderFailure.classify pLabel model ex502
        Assert.Equal(ProviderUnavailable, f502.kind)
        Assert.True(f502.retryable)

        let ex503 = Exception("Received 503 Service Unavailable")
        let f503 = ProviderFailure.classify pLabel model ex503
        Assert.Equal(ProviderUnavailable, f503.kind)
        Assert.True(f503.retryable)

        let ex504 = Exception("504 Gateway Timeout")
        let f504 = ProviderFailure.classify pLabel model ex504
        Assert.Equal(ProviderUnavailable, f504.kind)
        Assert.True(f504.retryable)

        // Unknown generic exn
        let exUnknown = Exception("Something completely unexpected happened")
        let fUnk = ProviderFailure.classify pLabel model exUnknown
        Assert.Equal(UnknownFailure, fUnk.kind)
        Assert.True(fUnk.retryable)
        Assert.Equal(Some "Something completely unexpected happened", fUnk.detail)

    // =========================================================================
    // 3. GeminiRequest 构建测试
    // =========================================================================

    [<Fact>]
    let ``GeminiRequest build correctly transforms contents, system instructions, and tools`` () =
        let options = ChatOptions()
        options.Instructions <- "You are a Gemini assistant."
        options.Temperature <- Nullable 0.8f
        options.TopP <- Nullable 0.95f
        options.MaxOutputTokens <- Nullable 2048

        let addProps = AdditionalPropertiesDictionary()
        addProps["custom_flag"] <- box true
        options.AdditionalProperties <- addProps

        let aiFunc = AIFunctionFactory.Create(Func<string, string>(fun (q: string) -> "result for " + q), "web_search", "Search the web")
        options.Tools <- [| aiFunc :> AITool |]

        let msgSystem = ChatMessage(ChatRole.System, "Additional system prompt.")
        let msgUser1 = ChatMessage(ChatRole.User, [|
            TextContent("First user query") :> AIContent
            DataContent(ReadOnlyMemory<byte>([| 1uy; 2uy; 3uy |]), "image/jpeg") :> AIContent
        |])
        let msgUser2 = ChatMessage(ChatRole.User, "Second adjacent user message")

        let fnCall = FunctionCallContent("web_search@0", "web_search", dict [ ("q", box "F# language") ])
        let msgModel = ChatMessage(ChatRole.Assistant, [| fnCall :> AIContent |])

        let fnResult = FunctionResultContent("web_search@0", """{"snippets":["F# is great"]}""")
        let msgTool = ChatMessage(ChatRole.Tool, [| fnResult :> AIContent |])

        let allMessages = [ msgSystem; msgUser1; msgUser2; msgModel; msgTool ]
        let reqJson = GeminiRequest.build allMessages options 500

        Assert.NotNull reqJson
        Assert.NotNull reqJson.["systemInstruction"]

    // =========================================================================
    // AttachmentContent 边界与契约测试
    // =========================================================================

    [<Fact>]
    let ``MediaSupport ofProviderKind assigns correct capabilities`` () =
        let anthropic = MediaSupport.ofProviderKind "anthropic"
        Assert.True(anthropic.image)
        Assert.True(anthropic.pdf)
        Assert.False(anthropic.audio)

        let gemini = MediaSupport.ofProviderKind "gemini"
        Assert.True(gemini.image)
        Assert.True(gemini.pdf)
        Assert.True(gemini.audio)

        let openai = MediaSupport.ofProviderKind "openai"
        Assert.True(openai.image)
        Assert.False(openai.pdf)
        Assert.False(openai.audio)

        let other = MediaSupport.ofProviderKind "custom-llm"
        Assert.True(other.image)
        Assert.False(other.pdf)
        Assert.False(other.audio)

    // =========================================================================
    // WanxiangHistoryProvider 契约测试
    // =========================================================================

    [<Fact>]
    let ``WanxiangHistoryProvider ConversationIdOf extracts valid GUID and handles invalid`` () =
        let provider = dummyProvider "anthropic"
        let sessionConfig = dummySession
        let historyProvider = WanxiangHistoryProvider((fun _ -> []), (fun _ _ -> ()), (fun _ _ -> ()))
        let runtime = AgentRuntime(provider, sessionConfig, [], historyProvider, 0)
        let cid = Guid.NewGuid()
        let session = runtime.CreateSession(cid)
        Assert.Equal(Some cid, WanxiangHistoryProvider.ConversationIdOf session)

        session.StateBag.SetValue<string>(WanxiangHistoryProvider.ConversationIdKey, "not-a-guid")
        Assert.True(WanxiangHistoryProvider.ConversationIdOf session |> Option.isNone)

    // =========================================================================
    // ProviderFailure.classify 分支全覆盖
    // =========================================================================

    [<Fact>]
    let ``ProviderFailure classify classifies ClientResultException across statuses`` () =
        let testStatus (status: int) (expectedKind: GenerationErrorKind) (expectedRetryable: bool) =
            let ex =
                if status <= 0 then
                    HttpRequestException("api error")
                else
                    HttpRequestException("api error", null, enum<HttpStatusCode> status)
            let failure = ProviderFailure.classify "test" "model-a" ex
            Assert.Equal(expectedKind, failure.kind)
            Assert.Equal(expectedRetryable, failure.retryable)

        testStatus 0 GenerationErrorKind.ProviderUnavailable true
        testStatus 400 GenerationErrorKind.ProviderBadRequest false
        testStatus 401 GenerationErrorKind.ProviderAuthFailed false
        testStatus 403 GenerationErrorKind.ProviderAuthFailed false
        testStatus 404 GenerationErrorKind.ModelNotFound false
        testStatus 408 GenerationErrorKind.ProviderTimeout true
        testStatus 429 GenerationErrorKind.ProviderRateLimited true
        testStatus 500 GenerationErrorKind.ProviderUnavailable true
        testStatus 502 GenerationErrorKind.ProviderUnavailable true
        testStatus 503 GenerationErrorKind.ProviderUnavailable true
        testStatus 504 GenerationErrorKind.ProviderUnavailable true

    [<Fact>]
    let ``ProviderFailure classify recognizes body signals in ClientResultException`` () =
        let ex1 = HttpRequestException("error: context_length_exceeded error detail", null, HttpStatusCode.BadRequest)
        let f1 = ProviderFailure.classify "test" "model-a" ex1
        Assert.Equal(GenerationErrorKind.ContextTooLong, f1.kind)
        Assert.False(f1.retryable)

        let ex2 = HttpRequestException("error: content_filter violation", null, HttpStatusCode.BadRequest)
        let f2 = ProviderFailure.classify "test" "model-a" ex2
        Assert.Equal(GenerationErrorKind.ContentFiltered, f2.kind)
        Assert.False(f2.retryable)

    [<Fact>]
    let ``ProviderFailure classify classifies timeouts and cancellations`` () =
        let f1 = ProviderFailure.classify "anthropic" "model-a" (TimeoutException("time out"))
        Assert.Equal(GenerationErrorKind.ProviderTimeout, f1.kind)
        Assert.True(f1.retryable)
        Assert.True(f1.detail.IsSome)
        Assert.Contains("time out", f1.detail.Value)

        let f2 = ProviderFailure.classify "anthropic" "model-a" (TaskCanceledException("cancelled"))
        Assert.Equal(GenerationErrorKind.ProviderTimeout, f2.kind)
        Assert.True(f2.retryable)

    [<Fact>]
    let ``ProviderFailure classify classifies HttpRequestException with retryAfter and statuses`` () =
        let exNoStatus = HttpRequestException("network down")
        let f0 = ProviderFailure.classify "test" "model-a" exNoStatus
        Assert.Equal(GenerationErrorKind.ProviderUnavailable, f0.kind)
        Assert.True(f0.retryable)

        let ex429 = HttpRequestException("rate limit", null, System.Net.HttpStatusCode.TooManyRequests)
        ex429.Data.["RetryAfterSeconds"] <- 45
        let f429 = ProviderFailure.classify "test" "model-a" ex429
        Assert.Equal(GenerationErrorKind.ProviderRateLimited, f429.kind)
        Assert.True(f429.retryable)
        Assert.Equal(Some 45, f429.retryAfterSeconds)

        let ex500 = HttpRequestException("internal", null, System.Net.HttpStatusCode.InternalServerError)
        let f500 = ProviderFailure.classify "test" "model-a" ex500
        Assert.Equal(GenerationErrorKind.ProviderUnavailable, f500.kind)
        Assert.True(f500.retryable)

        let ex401 = HttpRequestException("unauth", null, System.Net.HttpStatusCode.Unauthorized)
        let f401 = ProviderFailure.classify "test" "model-a" ex401
        Assert.Equal(GenerationErrorKind.ProviderAuthFailed, f401.kind)
        Assert.False(f401.retryable)

    [<Fact>]
    let ``ProviderFailure classify classifies SocketException and network keywords`` () =
        let sockEx = SocketException()
        let f1 = ProviderFailure.classify "test" "model-a" sockEx
        Assert.Equal(GenerationErrorKind.ProviderUnavailable, f1.kind)
        Assert.True(f1.retryable)

        let netEx = Exception("connection reset by peer")
        let f2 = ProviderFailure.classify "test" "model-a" netEx
        Assert.Equal(GenerationErrorKind.ProviderUnavailable, f2.kind)
        Assert.True(f2.retryable)

        let gatewayEx = Exception("HTTP 502 Bad Gateway")
        let f3 = ProviderFailure.classify "test" "model-a" gatewayEx
        Assert.Equal(GenerationErrorKind.ProviderUnavailable, f3.kind)
        Assert.True(f3.retryable)

    [<Fact>]
    let ``ProviderFailure classify recognizes message body signals`` () =
        let ex1 = Exception("api: context_length_exceeded")
        let f1 = ProviderFailure.classify "test" "model-a" ex1
        Assert.Equal(GenerationErrorKind.ContextTooLong, f1.kind)
        Assert.False(f1.retryable)

        let ex2 = Exception("api: model_not_found")
        let f2 = ProviderFailure.classify "test" "model-a" ex2
        Assert.Equal(GenerationErrorKind.ModelNotFound, f2.kind)
        Assert.False(f2.retryable)

        let ex3 = Exception("api: invalid_api_key")
        let f3 = ProviderFailure.classify "test" "model-a" ex3
        Assert.Equal(GenerationErrorKind.ProviderAuthFailed, f3.kind)
        Assert.False(f3.retryable)

    // =========================================================================
    // GeminiRequest 扩展测试（基于公开 API）
    // =========================================================================

    [<Fact>]
    let ``GeminiRequest build produces valid payload with merged adjacent roles and tools`` () =
        let options = ChatOptions()
        options.Instructions <- "You are a helpful assistant."
        options.Temperature <- Nullable 0.7f
        options.TopP <- Nullable 0.9f
        options.MaxOutputTokens <- Nullable 2048

        let extra = Dictionary<string, obj>()
        extra.["seed"] <- 42
        extra.["customNode"] <- JsonObject()
        extra.["nullValue"] <- null
        options.AdditionalProperties <- AdditionalPropertiesDictionary extra

        let toolFn = AIFunctionFactory.Create(Func<string, string>(fun query -> "result"), "searchTool", "Search the web")
        options.Tools <- ResizeArray<AITool>([ toolFn :> AITool ])

        let msgSystem = ChatMessage(ChatRole.System, "Be concise.")
        let msgUser1 = ChatMessage(ChatRole.User, "Hello")
        let msgUser2 = ChatMessage(ChatRole.User, "Second line")

        let callContent = FunctionCallContent("searchTool@1", "searchTool", Dictionary<string, obj>(dict [ ("query", box "fsharp") ]))
        let msgAssistant = ChatMessage(ChatRole.Assistant, "")
        msgAssistant.Contents.Add callContent

        let resContent = FunctionResultContent("searchTool@1", "search completed")
        let msgTool = ChatMessage(ChatRole.Tool, "")
        msgTool.Contents.Add resContent

        let dataBytes = [| 1uy; 2uy; 3uy |]
        let dataContent = DataContent(ReadOnlyMemory<byte>(dataBytes), "image/png")
        let msgData = ChatMessage(ChatRole.User, "")
        msgData.Contents.Add dataContent

        let messages = [ msgSystem; msgUser1; msgUser2; msgAssistant; msgTool; msgData ]

        let req = GeminiRequest.build messages options 1024

        Assert.NotNull(req.["systemInstruction"])
        let sysParts = req.["systemInstruction"].["parts"].AsArray()
        Assert.Single(sysParts) |> ignore
        let p0 = sysParts.[0]
        let sysText = p0.["text"].GetValue<string>()
        Assert.Contains("You are a helpful assistant.", sysText)
        Assert.Contains("Be concise.", sysText)

        let contents = req.["contents"].AsArray()
        Assert.True(contents.Count >= 3)
        let firstUser = contents.[0].AsObject()
        Assert.Equal("user", firstUser.["role"].GetValue<string>())
        let firstParts = firstUser.["parts"].AsArray()
        Assert.Equal(2, firstParts.Count)
        let fp0 = firstParts.[0]
        let fp1 = firstParts.[1]
        Assert.Equal("Hello", fp0.["text"].GetValue<string>())
        Assert.Equal("Second line", fp1.["text"].GetValue<string>())

        let genConfig = req.["generationConfig"].AsObject()
        Assert.Equal(0.7, genConfig.["temperature"].GetValue<double>(), 3)
        Assert.Equal(0.9, genConfig.["topP"].GetValue<double>(), 3)
        Assert.Equal(2048, genConfig.["maxOutputTokens"].GetValue<int>())
        Assert.Equal("42", genConfig.["seed"].GetValue<string>())
        Assert.NotNull(genConfig.["thinkingConfig"])
        Assert.Equal(1024, genConfig.["thinkingConfig"].["thinkingBudget"].GetValue<int>())
        Assert.True(genConfig.["thinkingConfig"].["includeThoughts"].GetValue<bool>())

        Assert.NotNull(req.["tools"])
        let toolsArray = req.["tools"].AsArray()
        Assert.Single(toolsArray) |> ignore

    // =========================================================================
    // AgentRuntime 扩展与边界测试
    // =========================================================================

    [<Fact>]
    let ``AgentRuntime properties and session creation initializes conversation id`` () =
        let provider = dummyProvider "anthropic"
        let session = dummySession
        let runtime = AgentRuntime(provider, session, [], null, 0)
        Assert.Equal("model-a", runtime.Model)
        Assert.Equal("test-provider", runtime.ProviderId)

        let cid = Guid.NewGuid()
        let agentSession = runtime.CreateSession(cid)
        Assert.NotNull(agentSession)
        Assert.NotNull(agentSession.StateBag)
        Assert.Equal(Some cid, WanxiangHistoryProvider.ConversationIdOf agentSession)

    [<Fact>]
    let ``AgentRuntime CompleteOnce handles cancellation as ProviderTimeout`` () =
        task {
            let provider = dummyProvider "anthropic"
            let session = dummySession
            let runtime = AgentRuntime(provider, session, [], null, 0)
            use cts = new CancellationTokenSource()
            cts.Cancel()
            let msgs = [ ChatMessage(ChatRole.User, "prompt") ]
            let! result = runtime.CompleteOnce(msgs, 100, cts.Token)
            match result with
            | Error err ->
                Assert.Equal(GenerationErrorKind.ProviderTimeout, err.kind)
                Assert.True(err.retryable)
            | Ok _ -> Assert.Fail("Expected cancellation to return Error")
        }

    [<Fact>]
    let ``AgentRuntime RunStreaming handles cancellation`` () =
        task {
            let provider = dummyProvider "anthropic"
            let session = dummySession
            let runtime = AgentRuntime(provider, session, [], null, 0)
            let agentSession = runtime.CreateSession(Guid.NewGuid())
            use cts = new CancellationTokenSource()
            cts.Cancel()
            let! res = runtime.RunStreaming(agentSession, [ ChatMessage(ChatRole.User, "hi") ], (fun _ -> ()), cts.Token)
            match res with
            | AgentCallResult.Cancelled -> ()
            | _ -> Assert.Fail("Expected Cancelled")
        }

    [<Fact>]
    let ``AgentRuntime Run handles cancellation`` () =
        task {
            let provider = dummyProvider "anthropic"
            let session = dummySession
            let runtime = AgentRuntime(provider, session, [], null, 0)
            let agentSession = runtime.CreateSession(Guid.NewGuid())
            use cts = new CancellationTokenSource()
            cts.Cancel()
            let! res = runtime.Run(agentSession, ChatMessage(ChatRole.User, "hi"), cts.Token)
            match res with
            | AgentCallResult.Cancelled -> ()
            | _ -> Assert.Fail("Expected Cancelled")
        }
