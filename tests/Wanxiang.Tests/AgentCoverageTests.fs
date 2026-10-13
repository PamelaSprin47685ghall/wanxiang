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
