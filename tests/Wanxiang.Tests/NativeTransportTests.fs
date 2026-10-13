module Wanxiang.Tests.NativeTransportTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json.Nodes
open System.Threading
open Microsoft.Extensions.AI
open Xunit
open Wanxiang.Agent
open Wanxiang.Config
open Wanxiang.UI

/// 只回放固定 SSE 的假上游，同时把收到的请求留下来做断言。
///
/// 用进程内的 HttpListener 而不是 tools/mock_openai.py：请求映射与流式解析
/// 都要逐字节断言，喂固定字节比起一个会变的外部进程可靠得多。
type private FakeUpstream(sse: string) =
    let port =
        let probe = new TcpListener(IPAddress.Loopback, 0)
        probe.Start()
        let chosen = (probe.LocalEndpoint :?> IPEndPoint).Port
        probe.Stop()
        chosen

    let listener = new HttpListener()
    let mutable requestBody = ""
    let mutable requestPath = ""
    let mutable requestHeaders: Map<string, string> = Map.empty

    do
        listener.Prefixes.Add $"http://127.0.0.1:{port}/"
        listener.Start()
        async {
            try
                while listener.IsListening do
                    let! context = listener.GetContextAsync() |> Async.AwaitTask
                    use reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)
                    requestBody <- reader.ReadToEnd()
                    requestPath <- context.Request.Url.PathAndQuery
                    requestHeaders <-
                        context.Request.Headers.AllKeys
                        |> Seq.choose (fun key ->
                            if isNull key then None else Some(key, context.Request.Headers[key]))
                        |> Map.ofSeq
                    context.Response.StatusCode <- 200
                    context.Response.ContentType <- "text/event-stream"
                    let bytes = Encoding.UTF8.GetBytes sse
                    context.Response.OutputStream.Write(bytes, 0, bytes.Length)
                    context.Response.OutputStream.Flush()
                    context.Response.Close()
            with _ -> ()
        }
        |> Async.Start

    member _.BaseUrl = $"http://127.0.0.1:{port}"
    member _.RequestBody = requestBody
    member _.RequestPath = requestPath
    member _.RequestHeaders = requestHeaders

    interface IDisposable with
        member _.Dispose() =
            try
                listener.Stop()
                listener.Close()
            with _ -> ()

let private providerOf (kind: string) (baseUrl: string) : ProviderConfig =
    { id = "test"
      kind = kind
      label = "Test"
      baseUrl = baseUrl
      apiKey = Some "secret-key"
      models = [ "m" ]
      defaultModel = "m"
      timeoutSeconds = 30
      maxRetries = 0
      enabled = true
      promptCaching = false
      headers = Map.empty
      extraJson = None; visionOverrides = Map.empty }

/// 一轮真实的工具往返：系统指令、用户提问、模型的工具调用、工具结果。
/// 这个形状能同时压到「system 提到顶层」「相邻同角色合并」「结果配回调用」三条规则。
let private toolRoundTrip (callId: string) =
    [ ChatMessage(ChatRole.System, "be brief")
      ChatMessage(ChatRole.User, "请调用 echo")
      ChatMessage(
          ChatRole.Assistant,
          [| FunctionCallContent(callId, "echo", dict [ "text", box "hi" ]) :> AIContent |])
      ChatMessage(ChatRole.Tool, [| FunctionResultContent(callId, "echoed") :> AIContent |]) ]

let private optionsWith (instructions: string option) =
    let options = ChatOptions()
    options.MaxOutputTokens <- Nullable 256
    options.Temperature <- Nullable 0.5f
    match instructions with
    | Some text -> options.Instructions <- text
    | None -> ()
    options

let private runOnce (client: IChatClient) (messages: ChatMessage list) (options: ChatOptions) =
    client.GetResponseAsync(messages, options, CancellationToken.None).GetAwaiter().GetResult()

let private functionCalls (response: ChatResponse) =
    response.Messages
    |> Seq.collect (fun m -> m.Contents)
    |> Seq.choose (function :? FunctionCallContent as c -> Some c | _ -> None)
    |> List.ofSeq

// ---------------------------------------------------------------- Anthropic

let private anthropicSse =
    String.concat
        "\n"
        [ "event: message_start"
          """data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":11,"output_tokens":0}}}"""
          ""
          "event: content_block_start"
          """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""
          ""
          "event: content_block_delta"
          """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"你好"}}"""
          ""
          "event: content_block_delta"
          """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"世界"}}"""
          ""
          "event: content_block_stop"
          """data: {"type":"content_block_stop","index":0}"""
          ""
          "event: content_block_start"
          """data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_9","name":"echo","input":{}}}"""
          ""
          "event: content_block_delta"
          """data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"text\""}}"""
          ""
          "event: content_block_delta"
          """data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":":\"hi\"}"}}"""
          ""
          "event: content_block_stop"
          """data: {"type":"content_block_stop","index":1}"""
          ""
          "event: message_delta"
          """data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}"""
          ""
          "event: message_stop"
          """data: {"type":"message_stop"}"""
          ""
          "" ]

let private anthropicRunWith (sse: string) (thinkingBudget: int) (promptCaching: bool) =
    let upstream = new FakeUpstream(sse)
    let provider = { providerOf "anthropic" upstream.BaseUrl with promptCaching = promptCaching }
    use http = new HttpClient()
    let client = new AnthropicChatClient(provider, "claude-test", http, thinkingBudget) :> IChatClient
    let response = runOnce client (toolRoundTrip "toolu_prev") (optionsWith (Some "be terse"))
    let body = JsonNode.Parse upstream.RequestBody
    let path = upstream.RequestPath
    let headers = upstream.RequestHeaders
    (upstream :> IDisposable).Dispose()
    response, body, path, headers

let private anthropicRun () =
    let upstream = new FakeUpstream(anthropicSse)
    let provider = providerOf "anthropic" upstream.BaseUrl
    use http = new HttpClient()
    let client = new AnthropicChatClient(provider, "claude-test", http, 0) :> IChatClient
    let response = runOnce client (toolRoundTrip "toolu_prev") (optionsWith (Some "be terse"))
    let body = JsonNode.Parse upstream.RequestBody
    let path = upstream.RequestPath
    let headers = upstream.RequestHeaders
    (upstream :> IDisposable).Dispose()
    response, body, path, headers

// ---------------------------------------------------------------- Gemini

let private geminiSse =
    String.concat
        "\n"
        [ """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"你好"}]}}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":1,"totalTokenCount":6}}"""
          ""
          """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"世界"}]}}]}"""
          ""
          """data: {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"echo","args":{"text":"hi"}}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":9,"totalTokenCount":14}}"""
          ""
          "" ]

let private geminiRunWith (sse: string) (thinkingBudget: int) =
    let upstream = new FakeUpstream(sse)
    let provider = providerOf "gemini" upstream.BaseUrl
    use http = new HttpClient()
    let client = new GeminiChatClient(provider, "gemini-test", http, thinkingBudget) :> IChatClient
    let response = runOnce client (toolRoundTrip "echo@0") (optionsWith (Some "be terse"))
    let body = JsonNode.Parse upstream.RequestBody
    (upstream :> IDisposable).Dispose()
    response, body

let private geminiRun () =
    let upstream = new FakeUpstream(geminiSse)
    let provider = providerOf "gemini" upstream.BaseUrl
    use http = new HttpClient()
    let client = new GeminiChatClient(provider, "gemini-test", http, 0) :> IChatClient
    let response = runOnce client (toolRoundTrip "echo@0") (optionsWith (Some "be terse"))
    let body = JsonNode.Parse upstream.RequestBody
    let path = upstream.RequestPath
    let headers = upstream.RequestHeaders
    (upstream :> IDisposable).Dispose()
    response, body, path, headers

// ---------------------------------------------------------------- 探活与预设

[<Fact>]
let ``探活能读 OpenAI 与 Anthropic 的 data 形状`` () =
    match Wanxiang.Server.ProviderCatalog.modelIdsFromProbe """{"data":[{"id":"b"},{"id":"a"}]}""" with
    | Ok ids -> Assert.Equal<string list>([ "a"; "b" ], ids)
    | Error reason -> failwith reason

[<Fact>]
let ``探活能读 Gemini 的 models 形状并去掉前缀`` () =
    // Gemini 的 name 形如 models/gemini-2.5-pro；不剥前缀就会把错的模型名填回列表
    let body = """{"models":[{"name":"models/gemini-2.5-pro"},{"name":"models/gemini-2.5-flash"}]}"""
    match Wanxiang.Server.ProviderCatalog.modelIdsFromProbe body with
    | Ok ids -> Assert.Equal<string list>([ "gemini-2.5-flash"; "gemini-2.5-pro" ], ids)
    | Error reason -> failwith reason

[<Fact>]
let ``探活响应没有模型清单时给出可读原因`` () =
    Assert.True(
        (match Wanxiang.Server.ProviderCatalog.modelIdsFromProbe """{"ok":true}""" with
         | Error _ -> true
         | Ok _ -> false),
        "既无 data 也无 models 时应当报错")
    Assert.True(
        (match Wanxiang.Server.ProviderCatalog.modelIdsFromProbe "[1,2]" with
         | Error _ -> true
         | Ok _ -> false),
        "顶层不是对象时应当报错")

[<Fact>]
let ``预设表里存在原生传输的选项`` () =
    // 界面只能从预设里选传输类型；少了原生预设，两个原生传输就是写了没人能用
    let kinds = ProviderPresets.all |> List.map (fun p -> p.kind) |> List.distinct |> List.sort
    Assert.Contains("anthropic", kinds)
    Assert.Contains("gemini", kinds)
    Assert.Contains("openai", kinds)

[<Fact>]
let ``每个预设的传输类型都是配置层认得的`` () =
    for preset in ProviderPresets.all do
        Assert.True(
            TomlCodec.supportedProviderKinds.Contains preset.kind,
            $"预设 {preset.key} 的 kind={preset.kind} 不被配置层接受")

// ---------------------------------------------------------------- 思维链 / 缓存 / 附件

let private reasoningOf (response: ChatResponse) =
    response.Messages
    |> Seq.collect (fun m -> m.Contents)
    |> Seq.choose (function :? TextReasoningContent as r -> Some r.Text | _ -> None)
    |> String.concat ""

let private anthropicThinkingSse =
    String.concat
        "\n"
        [ "event: message_start"
          """data: {"type":"message_start","message":{"id":"msg_t","usage":{"input_tokens":3,"output_tokens":0}}}"""
          ""
          "event: content_block_start"
          """data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}"""
          ""
          "event: content_block_delta"
          """data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"先拆解"}}"""
          ""
          "event: content_block_delta"
          """data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"再作答"}}"""
          ""
          "event: content_block_delta"
          """data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig"}}"""
          ""
          "event: content_block_stop"
          """data: {"type":"content_block_stop","index":0}"""
          ""
          "event: content_block_start"
          """data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""
          ""
          "event: content_block_delta"
          """data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"答案"}}"""
          ""
          "event: message_stop"
          """data: {"type":"message_stop"}"""
          ""
          "" ]

let private geminiThinkingSse =
    String.concat
        "\n"
        [ """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"先拆解","thought":true}]}}]}"""
          ""
          """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"答案"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5}}"""
          ""
          "" ]

// ---------------------------------------------------------------- 请求映射（直接构造，不走 HTTP）

let private withAttachment (mediaType: string) (bytes: byte[]) =
    [ ChatMessage(
          ChatRole.User,
          [| TextContent "看这个" :> AIContent
             DataContent(ReadOnlyMemory bytes, mediaType) :> AIContent |]) ]

// ---------------------------------------------------------------- 重试零语义（D4）

/// 只计数的桩传输：永远回固定状态码，让 sendWithRetry 的尝试次数可观测。
/// 两个原生传输（Anthropic/Gemini）都经它发请求；OpenAI 兼容路径由 SDK 的
/// ClientRetryPolicy 控重试——按官方文档 ClientRetryPolicy(0) 即关闭重试，
/// 不装多余包装。
type private CountingHandler(status: HttpStatusCode, count: int ref) =
    inherit HttpMessageHandler()
    override _.SendAsync(_, _) =
        count.Value <- count.Value + 1
        task { return new HttpResponseMessage(status) }
