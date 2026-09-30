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
        extraJson = None
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
    // 2. MessageSerde 序列化、反序列化与边界输入测试
    // =========================================================================

    [<Fact>]
    let ``MessageSerde roleName converts all ChatRole cases`` () =
        Assert.Equal("user", MessageSerde.roleName (ChatMessage(ChatRole.User, "u")))
        Assert.Equal("assistant", MessageSerde.roleName (ChatMessage(ChatRole.Assistant, "a")))
        Assert.Equal("system", MessageSerde.roleName (ChatMessage(ChatRole.System, "s")))
        Assert.Equal("tool", MessageSerde.roleName (ChatMessage(ChatRole.Tool, "t")))
        let customRole = ChatRole("custom")
        Assert.Equal("custom", MessageSerde.roleName (ChatMessage(customRole, "c")))

    [<Fact>]
    let ``MessageSerde textMessage and toolResultMessage helper creation`` () =
        let userMsg = MessageSerde.textMessage ChatRole.User "Hello, world!"
        Assert.Equal(ChatRole.User, userMsg.Role)
        Assert.Equal("Hello, world!", MessageSerde.textOf userMsg)

        let fnCall = FunctionCallContent("call_123", "calc", dict [ ("x", box 1) ])
        let toolMsg = MessageSerde.toolResultMessage fnCall """{"result":"ok"}"""
        Assert.Equal(ChatRole.Tool, toolMsg.Role)
        Assert.Equal(1, toolMsg.Contents.Count)
        let resContent = toolMsg.Contents.[0] :?> FunctionResultContent
        Assert.Equal("call_123", resContent.CallId)
        Assert.Equal("""{"result":"ok"}""", resContent.Result :?> string)

    [<Fact>]
    let ``MessageSerde hasToolCall and toolCalls detection`` () =
        let plainMsg = MessageSerde.textMessage ChatRole.Assistant "I have no tools."
        Assert.False(MessageSerde.hasToolCall plainMsg)
        Assert.Empty(MessageSerde.toolCalls plainMsg)

        let toolCall = FunctionCallContent("call_abc", "calc", dict [ ("x", box 42) ])
        let toolCallMsg = ChatMessage(ChatRole.Assistant, [| toolCall :> AIContent |])
        Assert.True(MessageSerde.hasToolCall toolCallMsg)
        let calls = MessageSerde.toolCalls toolCallMsg
        Assert.Equal(1, calls.Length)
        Assert.Equal("call_abc", calls.[0].CallId)
        Assert.Equal("calc", calls.[0].Name)

    [<Fact>]
    let ``MessageSerde attachmentRefs extraction with various object structures`` () =
        // 1. None if no contents
        let emptyNode = JsonObject()
        Assert.Empty(MessageSerde.attachmentRefs emptyNode)

        // 2. Array of contents with valid and invalid attachments
        let contentsArr = JsonArray()

        let validAtt = JsonObject()
        validAtt.["type"] <- "attachment"
        validAtt.["sha256"] <- "ABCD1234EF"
        validAtt.["mediaType"] <- "image/png"
        validAtt.["fileName"] <- "test.png"
        validAtt.["size"] <- 1024L
        contentsArr.Add validAtt

        let nonAtt = JsonObject()
        nonAtt.["type"] <- "text"
        nonAtt.["text"] <- "not an attachment"
        contentsArr.Add nonAtt

        let nodeWithContents = JsonObject()
        nodeWithContents.["contents"] <- contentsArr

        let refs = MessageSerde.attachmentRefs nodeWithContents
        Assert.Equal(1, refs.Length)
        Assert.Equal("abcd1234ef", refs.[0].sha256)
        Assert.Equal("image/png", refs.[0].mediaType)
        Assert.Equal("test.png", refs.[0].fileName)
        Assert.Equal(1024L, refs.[0].size)

    [<Fact>]
    let ``MessageSerde fromJsonNode and fromJsonNodeWith loose parsing and empty contents behavior`` () =
        // 1. Standard serialized ChatMessage
        let orig = ChatMessage(ChatRole.Assistant, "Direct assistant message")
        let serialized = MessageSerde.serialize orig
        let deserialized = MessageSerde.deserialize serialized |> Option.get
        Assert.Equal("Direct assistant message", MessageSerde.textOf deserialized)

        // 2. Loose JSON from client
        let clientJson = """
        {
            "role": "assistant",
            "contents": [
                { "text": "Loose assistant content" }
            ]
        }
        """
        let node = JsonNode.Parse clientJson
        let parsedOpt = MessageSerde.fromJsonNodeWith true node
        Assert.True(parsedOpt.IsSome)
        let parsed = parsedOpt.Value
        Assert.Equal(ChatRole.Assistant, parsed.Role)
        Assert.Equal("Loose assistant content", MessageSerde.textOf parsed)

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
    // 4. ProviderSse 流式分帧与 JSON 提取辅助测试
    // =========================================================================

    [<Fact>]
    let ``ProviderSse read parses SSE frames correctly`` () =
        task {
            let sseStreamText =
                "event: custom\ndata: first line\ndata: second line without space\n\n" +
                "data: solo event\n\n" +
                "data: trailing data without final newline"

            use stream = new MemoryStream(Encoding.UTF8.GetBytes(sseStreamText))
            let events = ResizeArray<SseEvent>()

            let asyncSeq = ProviderSse.read stream CancellationToken.None
            let enumerator = asyncSeq.GetAsyncEnumerator(CancellationToken.None)
            let mutable hasMore = true
            while hasMore do
                let! next = enumerator.MoveNextAsync()
                if next then
                    events.Add enumerator.Current
                else
                    hasMore <- false

            Assert.Equal(3, events.Count)
            Assert.Equal("custom", events.[0].name)
            Assert.Equal("first line\nsecond line without space", events.[0].data)
            Assert.Equal("", events.[1].name)
            Assert.Equal("solo event", events.[1].data)
            Assert.Equal("", events.[2].name)
            Assert.Equal("trailing data without final newline", events.[2].data)
        }

    [<Fact>]
    let ``ProviderSse raiseForStatus passes on 2xx and throws HttpRequestException on error`` () =
        use respOk = new HttpResponseMessage(HttpStatusCode.OK)
        ProviderSse.raiseForStatus respOk ""

        use resp400 = new HttpResponseMessage(HttpStatusCode.BadRequest)
        Assert.Throws<HttpRequestException>(fun () -> ProviderSse.raiseForStatus resp400 """{"error":"bad"}""") |> ignore

    [<Fact>]
    let ``ProviderSse JSON helper functions extraction coverage`` () =
        let jsonStr = """
        {
            "str": "sample",
            "numberInt": 123,
            "flag": true,
            "nestedObj": { "key": "value" },
            "items": [ 1, 2, 3 ]
        }
        """
        let node = JsonNode.Parse jsonStr :?> JsonObject

        Assert.Equal(Some "sample", ProviderSse.tryString node "str")
        Assert.Equal(None, ProviderSse.tryString node "non_existent")

        Assert.Equal(Some 123, ProviderSse.tryInt node "numberInt")
        Assert.Equal(None, ProviderSse.tryInt node "str")

        Assert.Equal(Some true, ProviderSse.tryBool node "flag")
        Assert.Equal(None, ProviderSse.tryBool node "str")

        Assert.True((ProviderSse.tryObject node "nestedObj").IsSome)
        Assert.Equal(None, ProviderSse.tryObject node "flag")

        Assert.True((ProviderSse.tryArray node "items").IsSome)
        Assert.Equal(None, ProviderSse.tryArray node "str")

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

    [<Theory>]
    [<InlineData("image/png", true)>]
    [<InlineData("IMAGE/JPEG", true)>]
    [<InlineData("image/webp", true)>]
    [<InlineData("image/gif", true)>]
    [<InlineData("text/plain", false)>]
    let ``AttachmentContent isImage correctly classifies media`` (mediaType: string, expected: bool) =
        Assert.Equal(expected, AttachmentContent.isImage mediaType)

    [<Theory>]
    [<InlineData("application/pdf", true)>]
    [<InlineData("APPLICATION/PDF", true)>]
    [<InlineData("text/plain", false)>]
    let ``AttachmentContent isPdf correctly classifies media`` (mediaType: string, expected: bool) =
        Assert.Equal(expected, AttachmentContent.isPdf mediaType)

    [<Theory>]
    [<InlineData("audio/mp3", true)>]
    [<InlineData("audio/wav", true)>]
    [<InlineData("audio/ogg", true)>]
    [<InlineData("video/mp4", false)>]
    let ``AttachmentContent isAudio correctly classifies media`` (mediaType: string, expected: bool) =
        Assert.Equal(expected, AttachmentContent.isAudio mediaType)

    [<Theory>]
    [<InlineData("text/plain", true)>]
    [<InlineData("application/json", true)>]
    [<InlineData("application/xml", true)>]
    [<InlineData("application/x-yaml", true)>]
    [<InlineData("application/javascript", true)>]
    [<InlineData("image/png", false)>]
    let ``AttachmentContent isText correctly classifies media`` (mediaType: string, expected: bool) =
        Assert.Equal(expected, AttachmentContent.isText mediaType)

    [<Fact>]
    let ``AttachmentContent resolve handles missing blob`` () =
        let ref = { sha256 = "abc"; mediaType = "text/plain"; fileName = "hello.txt"; size = 10L }
        let support = MediaSupport.ofProviderKind "anthropic"
        let contents = AttachmentContent.resolve support (fun _ -> None) ref
        Assert.Single(contents) |> ignore
        match contents.[0] with
        | :? TextContent as tc -> Assert.Contains("内容已丢失", tc.Text)
        | _ -> Assert.Fail("Expected TextContent")

    [<Fact>]
    let ``AttachmentContent resolve handles empty blob`` () =
        let ref = { sha256 = "abc"; mediaType = "text/plain"; fileName = "empty.txt"; size = 0L }
        let support = MediaSupport.ofProviderKind "anthropic"
        let contents = AttachmentContent.resolve support (fun _ -> Some [||]) ref
        Assert.Single(contents) |> ignore
        match contents.[0] with
        | :? TextContent as tc -> Assert.Contains("空文件", tc.Text)
        | _ -> Assert.Fail("Expected TextContent")

    [<Fact>]
    let ``AttachmentContent resolve handles normal text file`` () =
        let textBytes = Encoding.UTF8.GetBytes "let x = 42"
        let ref = { sha256 = "abc"; mediaType = "text/plain"; fileName = "test.fs"; size = int64 textBytes.Length }
        let support = MediaSupport.ofProviderKind "anthropic"
        let contents = AttachmentContent.resolve support (fun _ -> Some textBytes) ref
        Assert.Single(contents) |> ignore
        match contents.[0] with
        | :? TextContent as tc ->
            Assert.Contains("```fsharp", tc.Text)
            Assert.Contains("let x = 42", tc.Text)
        | _ -> Assert.Fail("Expected TextContent")

    [<Fact>]
    let ``AttachmentContent resolve handles truncated text file`` () =
        let largeText = String('a', 130 * 1024)
        let textBytes = Encoding.UTF8.GetBytes largeText
        let ref = { sha256 = "abc"; mediaType = "text/plain"; fileName = "large.txt"; size = int64 textBytes.Length }
        let support = MediaSupport.ofProviderKind "anthropic"
        let contents = AttachmentContent.resolve support (fun _ -> Some textBytes) ref
        Assert.Single(contents) |> ignore
        match contents.[0] with
        | :? TextContent as tc -> Assert.Contains("已截断", tc.Text)
        | _ -> Assert.Fail("Expected TextContent")

    [<Fact>]
    let ``AttachmentContent resolve handles supported image`` () =
        let imgBytes = [| 0uy; 1uy; 2uy; 3uy |]
        let ref = { sha256 = "abc"; mediaType = "image/png"; fileName = "pic.png"; size = 4L }
        let support = MediaSupport.ofProviderKind "anthropic"
        let contents = AttachmentContent.resolve support (fun _ -> Some imgBytes) ref
        Assert.Equal(2, contents.Length)
        match contents.[0], contents.[1] with
        | (:? TextContent as tc), (:? DataContent as dc) ->
            Assert.Contains("附件图片", tc.Text)
            Assert.Equal("image/png", dc.MediaType)
            Assert.Equal<byte>(imgBytes, dc.Data.ToArray())
        | _ -> Assert.Fail("Expected TextContent then DataContent")

    [<Fact>]
    let ``AttachmentContent resolve handles unsupported or oversize media`` () =
        let imgBytes = [| 1uy; 2uy |]
        let refImg = { sha256 = "abc"; mediaType = "image/png"; fileName = "pic.png"; size = 2L }
        let noSupport = { MediaSupport.image = false; pdf = false; audio = false }
        let contents = AttachmentContent.resolve noSupport (fun _ -> Some imgBytes) refImg
        Assert.Single(contents) |> ignore
        match contents.[0] with
        | :? TextContent as tc -> Assert.Contains("当前服务商的传输不接受该类型", tc.Text)
        | _ -> Assert.Fail("Expected TextContent")

        // 超过大小限制
        let hugeImgBytes = Array.zeroCreate (9 * 1024 * 1024)
        let refHuge = { sha256 = "huge"; mediaType = "image/png"; fileName = "huge.png"; size = int64 hugeImgBytes.Length }
        let contentsHuge = AttachmentContent.resolve (MediaSupport.ofProviderKind "anthropic") (fun _ -> Some hugeImgBytes) refHuge
        Assert.Single(contentsHuge) |> ignore
        match contentsHuge.[0] with
        | :? TextContent as tc -> Assert.Contains("超过", tc.Text)
        | _ -> Assert.Fail("Expected TextContent")

        // 未知二进制
        let binBytes = [| 0xCAuy; 0xFEuy; 0xBAuy; 0xBEuy |]
        let refBin = { sha256 = "bin"; mediaType = "application/octet-stream"; fileName = "app.exe"; size = 4L }
        let contentsBin = AttachmentContent.resolve (MediaSupport.ofProviderKind "anthropic") (fun _ -> Some binBytes) refBin
        Assert.Single(contentsBin) |> ignore
        match contentsBin.[0] with
        | :? TextContent as tc -> Assert.Contains("二进制内容不进上下文", tc.Text)
        | _ -> Assert.Fail("Expected TextContent")

    [<Fact>]
    let ``AttachmentContent appendTo correctly adds to chat message`` () =
        let msg = ChatMessage(ChatRole.User, "initial prompt")
        let imgBytes = [| 9uy; 8uy |]
        let ref = { sha256 = "abc"; mediaType = "image/png"; fileName = "pic.png"; size = 2L }
        let support = MediaSupport.ofProviderKind "anthropic"
        let resMsg = AttachmentContent.appendTo support (fun _ -> Some imgBytes) [ref] msg
        Assert.Equal(3, resMsg.Contents.Count)
        Assert.Equal("initial prompt", (resMsg.Contents.[0] :?> TextContent).Text)

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
    // MessageSerde 扩展分支测试（基于公开 API）
    // =========================================================================

    [<Fact>]
    let ``MessageSerde attachmentRefs extracts attachment items and parses sizes`` () =
        let root = JsonObject()
        let arr = JsonArray()

        let item1 = JsonObject()
        item1.["type"] <- "attachment"
        item1.["sha256"] <- "ABCDEF1234567890"
        item1.["mediaType"] <- "image/png"
        item1.["fileName"] <- "pic.png"
        item1.["size"] <- 1024L
        arr.Add item1

        let item2 = JsonObject()
        item2.["type"] <- "attachment"
        item2.["sha256"] <- "FEEDCAFE"
        arr.Add item2

        let item3 = JsonObject()
        item3.["type"] <- "attachment"
        item3.["sha256"] <- "aabbccdd"
        item3.["size"] <- "not-a-number"
        arr.Add item3

        let item4 = JsonObject()
        item4.["type"] <- "attachment"
        item4.["sha256"] <- "   "
        arr.Add item4

        let item5 = JsonObject()
        item5.["type"] <- "text"
        item5.["text"] <- "not an attachment"
        arr.Add item5

        arr.Add(JsonValue.Create "plain-string")

        root.["contents"] <- arr

        let refs = MessageSerde.attachmentRefs root
        Assert.Equal(3, refs.Length)

        Assert.Equal("abcdef1234567890", refs.[0].sha256)
        Assert.Equal("image/png", refs.[0].mediaType)
        Assert.Equal("pic.png", refs.[0].fileName)
        Assert.Equal(1024L, refs.[0].size)

        Assert.Equal("feedcafe", refs.[1].sha256)
        Assert.Equal("application/octet-stream", refs.[1].mediaType)
        Assert.Equal("FEEDCAFE", refs.[1].fileName)
        Assert.Equal(0L, refs.[1].size)

        Assert.Equal("aabbccdd", refs.[2].sha256)
        Assert.Equal(0L, refs.[2].size)

        Assert.Empty(MessageSerde.attachmentRefs (JsonObject()))
        Assert.Empty(MessageSerde.attachmentRefs (JsonValue.Create "scalar"))

    [<Fact>]
    let ``MessageSerde roleName and message helpers`` () =
        Assert.Equal("system", MessageSerde.roleName (ChatMessage(ChatRole.System, "test")))
        Assert.Equal("tool", MessageSerde.roleName (ChatMessage(ChatRole.Tool, "test")))
        Assert.Equal("assistant", MessageSerde.roleName (ChatMessage(ChatRole.Assistant, "test")))
        Assert.Equal("user", MessageSerde.roleName (ChatMessage(ChatRole.User, "test")))
        Assert.Equal("customRole", MessageSerde.roleName (ChatMessage(ChatRole "customRole", "test")))

        let call = FunctionCallContent("call_123", "calculator", Dictionary<string, obj>())
        let toolMsg = MessageSerde.toolResultMessage call "{\"result\": 42}"
        Assert.Equal(ChatRole.Tool, toolMsg.Role)
        let resContent = Assert.IsType<FunctionResultContent>(Assert.Single(toolMsg.Contents))
        Assert.Equal("call_123", resContent.CallId)
        Assert.Equal("{\"result\": 42}", resContent.Result.ToString())

        let textMsg = MessageSerde.textMessage ChatRole.Assistant "hello assistant"
        Assert.Equal(ChatRole.Assistant, textMsg.Role)
        Assert.Equal("hello assistant", MessageSerde.textOf textMsg)

    [<Fact>]
    let ``MessageSerde hasToolCall and toolCalls detect function calls`` () =
        let msg = ChatMessage(ChatRole.Assistant, "")
        msg.Contents.Add(FunctionCallContent("fn_1", "calc", Dictionary<string, obj>()))
        msg.Contents.Add(TextContent("calling fn"))

        Assert.True(MessageSerde.hasToolCall msg)
        let calls = MessageSerde.toolCalls msg
        Assert.Single(calls) |> ignore
        Assert.Equal("fn_1", calls.[0].CallId)
        Assert.Equal("calc", calls.[0].Name)

    [<Fact>]
    let ``MessageSerde fromJsonNodeWith parses untagged, tagged, and attachment contents`` () =
        let makeUntaggedMsg (role: string) (contents: JsonNode list) =
            let o = JsonObject()
            if not (String.IsNullOrEmpty role) then o.["role"] <- role
            let arr = JsonArray()
            for c in contents do arr.Add c
            o.["contents"] <- arr
            o :> JsonNode

        let msgAssistant = MessageSerde.fromJsonNodeWith true (makeUntaggedMsg "assistant" [])
        Assert.Equal(ChatRole.Assistant, msgAssistant.Value.Role)

        let msgSystem = MessageSerde.fromJsonNodeWith true (makeUntaggedMsg "system" [])
        Assert.Equal(ChatRole.System, msgSystem.Value.Role)

        let msgTool = MessageSerde.fromJsonNodeWith true (makeUntaggedMsg "tool" [])
        Assert.Equal(ChatRole.Tool, msgTool.Value.Role)

        let msgUser = MessageSerde.fromJsonNodeWith true (makeUntaggedMsg "user" [])
        Assert.Equal(ChatRole.User, msgUser.Value.Role)

        let msgUnknown = MessageSerde.fromJsonNodeWith true (makeUntaggedMsg "other" [])
        Assert.Equal(ChatRole "other", msgUnknown.Value.Role)

        let msgNoRole = MessageSerde.fromJsonNodeWith true (makeUntaggedMsg null [])
        Assert.Equal(ChatRole.User, msgNoRole.Value.Role)

        let textItem = JsonObject()
        textItem.["text"] <- "some text"

        let emptyTextItem = JsonObject()
        emptyTextItem.["text"] <- ""

        let attItem = JsonObject()
        attItem.["type"] <- "attachment"
        attItem.["sha256"] <- "123"

        let nonObjItem = JsonValue.Create 42

        let complexMsgNode = makeUntaggedMsg "user" [ textItem :> JsonNode; emptyTextItem :> JsonNode; attItem :> JsonNode; nonObjItem :> JsonNode ]
        let parsed = MessageSerde.fromJsonNodeWith false complexMsgNode
        Assert.True(parsed.IsSome)
        Assert.Single(parsed.Value.Contents) |> ignore
        Assert.Equal("some text", MessageSerde.textOf parsed.Value)

        let emptyNode = makeUntaggedMsg "user" []
        Assert.True(MessageSerde.fromJsonNodeWith false emptyNode |> Option.isNone)
        Assert.True(MessageSerde.fromJsonNodeWith true emptyNode |> Option.isSome)
        Assert.True(MessageSerde.fromJsonNode (JsonValue.Create "scalar") |> Option.isNone)

    // =========================================================================
    // GeminiRequest 扩展测试（基于公开 API）
    // =========================================================================

    [<Fact>]
    let ``GeminiRequest CallId helpers split and format correctly`` () =
        let id = GeminiRequest.makeCallId "weatherTool" 3
        Assert.Equal("weatherTool@3", id)
        Assert.Equal("weatherTool", GeminiRequest.nameOfCallId id)
        Assert.Equal("custom", GeminiRequest.nameOfCallId "custom")
        Assert.Equal("", GeminiRequest.nameOfCallId "")
        Assert.Equal("", GeminiRequest.nameOfCallId null)

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
    // ProviderSse 扩展测试（基于公开 API）
    // =========================================================================

    [<Fact>]
    let ``ProviderSse raiseForStatus passes 2xx and throws on error`` () =
        use respOk = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        ProviderSse.raiseForStatus respOk ""

        use resp400 = new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        let ex400 = Assert.Throws<HttpRequestException>(fun () -> ProviderSse.raiseForStatus resp400 "bad request body")
        Assert.Contains("bad request body", ex400.Message)

        use resp429 = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        resp429.Headers.RetryAfter <- System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30.0))
        let ex429 = Assert.Throws<HttpRequestException>(fun () -> ProviderSse.raiseForStatus resp429 "")
        Assert.Equal(30, unbox<int> ex429.Data.["RetryAfterSeconds"])

        use resp429Date = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        resp429Date.Headers.RetryAfter <- System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(60.0))
        let ex429Date = Assert.Throws<HttpRequestException>(fun () -> ProviderSse.raiseForStatus resp429Date "")
        Assert.NotNull(ex429Date.Data.["RetryAfterSeconds"])

    [<Fact>]
    let ``ProviderSse read parses varied SSE lines and comments`` () =
        task {
            let sseContent =
                ": keepalive\n" +
                "event: update\n" +
                "data: line 1\n" +
                "data: line 2\n" +
                "\n" +
                "data: final\n" +
                "\n"
            let bytes = Encoding.UTF8.GetBytes sseContent
            use stream = new MemoryStream(bytes)
            let asyncSeq = ProviderSse.read stream CancellationToken.None
            let enumerator = asyncSeq.GetAsyncEnumerator(CancellationToken.None)
            let events = ResizeArray<SseEvent>()
            let mutable running = true
            while running do
                let! hasNext = enumerator.MoveNextAsync().AsTask()
                if hasNext then
                    events.Add enumerator.Current
                else
                    running <- false

            Assert.Equal(2, events.Count)
            Assert.Equal("update", events.[0].name)
            Assert.Equal("line 1\nline 2", events.[0].data)
            Assert.Equal("", events.[1].name)
            Assert.Equal("final", events.[1].data)
        }

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
