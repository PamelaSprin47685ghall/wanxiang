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
      extraJson = None }

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

[<Fact>]
let ``anthropic 把分片的文本增量拼回完整回答`` () =
    let response, _, _, _ = anthropicRun ()
    Assert.Contains("你好世界", response.Text)

[<Fact>]
let ``anthropic 把分片下发的工具入参拼完整再解析`` () =
    // 入参是 input_json_delta 一片片来的，中途解析必然失败
    let response, _, _, _ = anthropicRun ()
    match functionCalls response with
    | [ call ] ->
        Assert.Equal("toolu_9", call.CallId)
        Assert.Equal("echo", call.Name)
        Assert.True(call.Arguments.ContainsKey "text", "入参里应当有 text")
        Assert.Equal("hi", string call.Arguments["text"])
    | other -> failwith $"应当恰好一次工具调用，实际 {other.Length} 次"

[<Fact>]
let ``anthropic 汇总输入与输出用量`` () =
    let response, _, _, _ = anthropicRun ()
    Assert.NotNull response.Usage
    Assert.Equal(Nullable 11L, response.Usage.InputTokenCount)
    Assert.Equal(Nullable 7L, response.Usage.OutputTokenCount)

[<Fact>]
let ``anthropic 请求把 system 提到顶层且必带 max_tokens`` () =
    let _, body, path, _ = anthropicRun ()
    // max_tokens 缺失时真实 API 直接 400
    Assert.Equal(Some 256, ProviderSse.tryInt body "max_tokens")
    // system 现在恒为数组形态：字符串形态挂不上 cache_control
    let system =
        ProviderSse.tryArray body "system"
        |> Option.map (fun blocks -> blocks.ToJsonString())
        |> Option.defaultValue ""
    Assert.Contains("be terse", system)
    Assert.Contains("be brief", system)
    Assert.EndsWith("/v1/messages", path)

[<Fact>]
let ``anthropic 请求里相邻同角色被合并且工具结果算 user`` () =
    // Anthropic 要求 user/assistant 交替；工具结果紧跟用户消息时最容易撞 400
    let _, body, _, _ = anthropicRun ()
    let roles =
        ProviderSse.tryArray body "messages"
        |> Option.map (fun a ->
            a |> Seq.map (fun m -> ProviderSse.tryString m "role" |> Option.defaultValue "") |> List.ofSeq)
        |> Option.defaultValue []
    Assert.Equal<string list>([ "user"; "assistant"; "user" ], roles)
    Assert.DoesNotContain("\"role\":\"system\"", body.ToJsonString())

[<Fact>]
let ``anthropic 用请求头带密钥与协议版本`` () =
    let _, _, _, headers = anthropicRun ()
    Assert.Equal(Some "secret-key", headers.TryFind "x-api-key")
    Assert.Equal(Some AnthropicRequest.ApiVersion, headers.TryFind "anthropic-version")

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

[<Fact>]
let ``gemini 把多帧文本拼回完整回答`` () =
    let response, _, _, _ = geminiRun ()
    Assert.Contains("你好世界", response.Text)

[<Fact>]
let ``gemini 的 functionCall 被造出可回配的调用 id`` () =
    // Gemini 的 functionCall 不带 id，而编排层要靠 id 把结果配回调用
    let response, _, _, _ = geminiRun ()
    match functionCalls response with
    | [ call ] ->
        Assert.Equal("echo", call.Name)
        Assert.StartsWith("echo@", call.CallId)
        Assert.Equal("echo", GeminiRequest.nameOfCallId call.CallId)
    | other -> failwith $"应当恰好一次工具调用，实际 {other.Length} 次"

[<Fact>]
let ``gemini 汇总用量并取最后一帧`` () =
    let response, _, _, _ = geminiRun ()
    Assert.NotNull response.Usage
    Assert.Equal(Nullable 5L, response.Usage.InputTokenCount)
    Assert.Equal(Nullable 9L, response.Usage.OutputTokenCount)
    Assert.Equal(Nullable 14L, response.Usage.TotalTokenCount)

[<Fact>]
let ``gemini 请求用 systemInstruction 与 model 角色`` () =
    let _, body, path, _ = geminiRun ()
    let system =
        ProviderSse.tryObject body "systemInstruction"
        |> Option.bind (fun node -> ProviderSse.tryArray node "parts")
        |> Option.map (fun parts -> parts.ToJsonString())
        |> Option.defaultValue ""
    Assert.Contains("be terse", system)
    Assert.Contains("be brief", system)
    let roles =
        ProviderSse.tryArray body "contents"
        |> Option.map (fun a ->
            a |> Seq.map (fun m -> ProviderSse.tryString m "role" |> Option.defaultValue "") |> List.ofSeq)
        |> Option.defaultValue []
    // Gemini 管助手叫 model
    Assert.Equal<string list>([ "user"; "model"; "user" ], roles)
    Assert.Contains(":streamGenerateContent", path)
    Assert.Contains("alt=sse", path)

[<Fact>]
let ``gemini 回传工具结果时从调用 id 还原函数名`` () =
    let _, body, _, _ = geminiRun ()
    let json = body.ToJsonString()
    Assert.Contains("functionResponse", json)
    // 名字必须是 echo，而不是带序号的 echo@0
    Assert.Contains("\"name\":\"echo\"", json)
    Assert.DoesNotContain("echo@0", json)

[<Fact>]
let ``gemini 用请求头带密钥而不放进 URL`` () =
    // URL 会进日志与错误详情，密钥不该出现在那里
    let _, _, path, headers = geminiRun ()
    Assert.Equal(Some "secret-key", headers.TryFind "x-goog-api-key")
    Assert.DoesNotContain("secret-key", path)

[<Fact>]
let ``两个原生传输都被配置层接受`` () =
    for kind in [ "openai"; "anthropic"; "gemini" ] do
        Assert.True(TomlCodec.supportedProviderKinds.Contains kind, $"{kind} 应当被接受")

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

[<Fact>]
let ``工具调用 id 能穿过序列化往返`` () =
    // Gemini 的调用 id 是自己造的 `name@序号`，函数名要从 id 里拆回来。
    // 消息要落 NDJSON 再重放，id 在这一路上被改写或丢掉，工具结果就配不回调用了。
    let original =
        ChatMessage(
            ChatRole.Assistant,
            [| FunctionCallContent("echo@0", "echo", dict [ "text", box "hi" ]) :> AIContent |])
    let json = MessageSerde.serialize original
    match MessageSerde.deserialize json with
    | None -> failwith $"重放失败：{json}"
    | Some restored ->
        match restored.Contents |> Seq.choose (function :? FunctionCallContent as c -> Some c | _ -> None) |> List.ofSeq with
        | [ call ] ->
            Assert.Equal("echo@0", call.CallId)
            Assert.Equal("echo", call.Name)
            Assert.Equal("echo", GeminiRequest.nameOfCallId call.CallId)
        | other -> failwith $"重放后应当仍有一次工具调用，实际 {other.Length} 次"

[<Fact>]
let ``工具结果的调用 id 同样能穿过序列化往返`` () =
    let call = FunctionCallContent("echo@3", "echo", dict [ "text", box "x" ])
    let message = MessageSerde.toolResultMessage call "{\"ok\":true}"
    match MessageSerde.deserialize (MessageSerde.serialize message) with
    | None -> failwith "工具结果消息重放失败"
    | Some restored ->
        let ids =
            restored.Contents
            |> Seq.choose (function :? FunctionResultContent as r -> Some r.CallId | _ -> None)
            |> List.ofSeq
        Assert.Equal<string list>([ "echo@3" ], ids)

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

[<Fact>]
let ``anthropic 的思维链与正式回答分开呈现`` () =
    let response, _, _, _ = anthropicRunWith anthropicThinkingSse 1024 false
    Assert.Equal("先拆解再作答", reasoningOf response)
    Assert.Contains("答案", response.Text)
    // signature_delta 不是给人看的内容，不该混进思维链
    Assert.DoesNotContain("sig", reasoningOf response)

[<Fact>]
let ``anthropic 开思维链时请求带 thinking 且 max_tokens 大于预算`` () =
    // Anthropic 要求 max_tokens > budget_tokens，冲突时应当抬高上限而不是抛 400
    let _, body, _, _ = anthropicRunWith anthropicThinkingSse 1024 false
    match ProviderSse.tryObject body "thinking" with
    | None -> failwith "请求里没有 thinking"
    | Some thinking ->
        Assert.Equal(Some "enabled", ProviderSse.tryString thinking "type")
        Assert.Equal(Some 1024, ProviderSse.tryInt thinking "budget_tokens")
    let maxTokens = ProviderSse.tryInt body "max_tokens" |> Option.defaultValue 0
    Assert.True(maxTokens > 1024, $"max_tokens={maxTokens} 应当大于思维链预算 1024")

[<Fact>]
let ``anthropic 不开思维链时请求里没有 thinking`` () =
    let _, body, _, _ = anthropicRun ()
    Assert.True((ProviderSse.tryObject body "thinking").IsNone)

[<Fact>]
let ``anthropic 开提示词缓存时 system 带 ephemeral 标记`` () =
    let _, body, _, _ = anthropicRunWith anthropicSse 0 true
    let json = body.ToJsonString()
    Assert.Contains("cache_control", json)
    Assert.Contains("ephemeral", json)

[<Fact>]
let ``anthropic 默认不打缓存标记`` () =
    // 缓存写入比普通请求贵，是计费决定，不该由程序替用户做
    let _, body, _, _ = anthropicRun ()
    Assert.DoesNotContain("cache_control", body.ToJsonString())

let private geminiThinkingSse =
    String.concat
        "\n"
        [ """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"先拆解","thought":true}]}}]}"""
          ""
          """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"答案"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5}}"""
          ""
          "" ]

[<Fact>]
let ``gemini 的 thought 片段进思维链而非正文`` () =
    let response, _ = geminiRunWith geminiThinkingSse 512
    Assert.Equal("先拆解", reasoningOf response)
    Assert.Contains("答案", response.Text)
    Assert.DoesNotContain("先拆解", response.Text)

[<Fact>]
let ``gemini 开思维链时 generationConfig 带 thinkingConfig`` () =
    let _, body = geminiRunWith geminiThinkingSse 512
    match ProviderSse.tryObject body "generationConfig" with
    | None -> failwith "请求里没有 generationConfig"
    | Some config ->
        match ProviderSse.tryObject config "thinkingConfig" with
        | None -> failwith "generationConfig 里没有 thinkingConfig"
        | Some thinking ->
            Assert.Equal(Some 512, ProviderSse.tryInt thinking "thinkingBudget")
            // 只给预算不要求回传，等于只计费不给看
            Assert.Equal(Some true, ProviderSse.tryBool thinking "includeThoughts")

[<Fact>]
let ``gemini 不开思维链时没有 thinkingConfig`` () =
    let _, body = geminiRunWith geminiSse 0
    let hasThinking =
        ProviderSse.tryObject body "generationConfig"
        |> Option.bind (fun c -> ProviderSse.tryObject c "thinkingConfig")
    Assert.True(hasThinking.IsNone)

// ---------------------------------------------------------------- 请求映射（直接构造，不走 HTTP）

let private withAttachment (mediaType: string) (bytes: byte[]) =
    [ ChatMessage(
          ChatRole.User,
          [| TextContent "看这个" :> AIContent
             DataContent(ReadOnlyMemory bytes, mediaType) :> AIContent |]) ]

[<Fact>]
let ``anthropic 把 PDF 映成 document 块`` () =
    let body = AnthropicRequest.build "m" (withAttachment "application/pdf" (Array.init 8 byte)) null true 0 false
    let json = body.ToJsonString()
    Assert.Contains("\"type\":\"document\"", json)
    Assert.Contains("application/pdf", json)
    Assert.Contains("\"type\":\"base64\"", json)

[<Fact>]
let ``anthropic 把图片映成 image 块`` () =
    let body = AnthropicRequest.build "m" (withAttachment "image/png" (Array.init 8 byte)) null true 0 false
    let json = body.ToJsonString()
    Assert.Contains("\"type\":\"image\"", json)
    Assert.DoesNotContain("\"type\":\"document\"", json)

[<Fact>]
let ``gemini 把图片 PDF 音频一律映成 inlineData`` () =
    for media in [ "image/png"; "application/pdf"; "audio/mpeg" ] do
        let body = GeminiRequest.build (withAttachment media (Array.init 8 byte)) null 0
        let json = body.ToJsonString()
        Assert.Contains("inlineData", json)
        Assert.Contains(media, json)

[<Fact>]
let ``会话 extraJson 在两种原生传输上都不被丢掉`` () =
    // 兼容传输靠 ChatOptions.AdditionalProperties 透传；原生传输若不读它，
    // 用户在会话里配的模型专属参数会静默消失
    let options = ChatOptions()
    options.AdditionalProperties <-
        AdditionalPropertiesDictionary(dict [ "service_tier", box (JsonValue.Create "priority" :> JsonNode) ])
    let anthropic = AnthropicRequest.build "m" [ ChatMessage(ChatRole.User, "hi") ] options true 0 false
    Assert.Equal(Some "priority", ProviderSse.tryString anthropic "service_tier")

    let gemini = GeminiRequest.build [ ChatMessage(ChatRole.User, "hi") ] options 0
    let config = ProviderSse.tryObject gemini "generationConfig" |> Option.get
    Assert.Equal(Some "priority", ProviderSse.tryString config "service_tier")
