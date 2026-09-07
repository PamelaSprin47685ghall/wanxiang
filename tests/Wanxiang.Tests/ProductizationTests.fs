module Wanxiang.Tests.ProductizationTests

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open Microsoft.Extensions.AI
open Xunit
open Wanxiang.Agent
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Server
open Wanxiang.Store
open Wanxiang.Tests.Helpers

// ---------------------------------------------------------------- 纯文本摘要

[<Fact>]
let ``markdown is flattened for plain-text surfaces`` () =
    let raw =
        "### 关于排版\n\n这是 **加粗** 与 `行内代码`，还有[链接](https://example.com)。\n\n- 项目一\n- 项目二\n\n```fsharp\nlet x = 1\n```\n"
    let plain = PlainText.ofMarkdown raw
    Assert.DoesNotContain("#", plain)
    Assert.DoesNotContain("**", plain)
    Assert.DoesNotContain("`", plain)
    Assert.DoesNotContain("https://example.com", plain)
    Assert.DoesNotContain("let x = 1", plain)
    Assert.Contains("关于排版", plain)
    Assert.Contains("加粗", plain)
    Assert.Contains("链接", plain)
    Assert.Contains("项目一", plain)
    Assert.False(plain.Contains "\n")

[<Fact>]
let ``plain-text summary truncates with an ellipsis`` () =
    let plain = PlainText.summarize 8 "## 一二三四五六七八九十"
    Assert.Equal("一二三四五六七八…", plain)

[<Fact>]
let ``image syntax leaves no stray exclamation mark`` () =
    Assert.Equal("示意图", PlainText.ofMarkdown "![示意图](a.png)")

// ---------------------------------------------------------------- 会话标题

[<Fact>]
let ``auto title keeps only the first sentence and strips markdown`` () =
    let title = TitleGenerator.sanitize "**万象存储模型**。第二句应当被丢掉。" "原始提问"
    Assert.Equal("万象存储模型", title)

[<Fact>]
let ``auto title falls back to the user question when the model returns nothing`` () =
    Assert.Equal("解释一下投影", TitleGenerator.sanitize "   " "解释一下投影")

[<Fact>]
let ``only system placeholder titles are auto-renamed`` () =
    Assert.True(TitleGenerator.isPlaceholder "新会话")
    Assert.True(TitleGenerator.isPlaceholder "会话 3f8a1c22")
    Assert.False(TitleGenerator.isPlaceholder "我给它起的名字")

// ---------------------------------------------------------------- Provider 模型解析

let private provider (models: string list) (defaultModel: string) : ProviderConfig =
    { id = "p"
      kind = "openai"
      label = "P"
      baseUrl = "https://example.com/v1"
      apiKey = Some "k"
      models = models
      defaultModel = defaultModel
      timeoutSeconds = 30
      maxRetries = 1
      enabled = true
      promptCaching = false
      headers = Map.empty
      extraJson = None }

[<Fact>]
let ``session model wins when the provider offers it`` () =
    let p = provider [ "a"; "b" ] "a"
    Assert.Equal("b", ProviderConfig.resolveModel "b" p)

[<Fact>]
let ``unknown session model falls back to the provider default`` () =
    let p = provider [ "a"; "b" ] "a"
    Assert.Equal("a", ProviderConfig.resolveModel "zzz" p)
    Assert.Equal("a", ProviderConfig.resolveModel "" p)

// ---------------------------------------------------------------- 生成失败分类

[<Fact>]
let ``timeouts classify as retryable provider timeouts`` () =
    let error = ProviderFailure.classify "Mock" "m" (TimeoutException "slow")
    Assert.Equal(ProviderTimeout, error.kind)
    Assert.True error.retryable

[<Fact>]
let ``context-length signals are detected from the message body`` () =
    let error = ProviderFailure.classify "Mock" "m" (exn "This model's maximum context length is 8192 tokens")
    Assert.Equal(ContextTooLong, error.kind)
    Assert.False error.retryable

[<Fact>]
let ``every failure kind has an actionable hint and a stable code`` () =
    let kinds =
        [ ProviderAuthFailed; ProviderRateLimited; ProviderTimeout; ProviderUnavailable
          ProviderBadRequest; ContextTooLong; ModelNotFound; ContentFiltered; ToolFailed
          ConfigInvalid; UnknownFailure ]
    for kind in kinds do
        Assert.False(String.IsNullOrWhiteSpace(GenerationErrorKind.hint kind))
        let code = GenerationErrorKind.code kind
        Assert.False(String.IsNullOrWhiteSpace code)
        Assert.Equal(kind, GenerationErrorKind.ofCode code)

[<Fact>]
let ``generation error survives a wire roundtrip`` () =
    let error =
        { GenerationError.create ProviderRateLimited "被限流了。" with
            detail = Some "HTTP 429"
            retryAfterSeconds = Some 12 }
    let encoded =
        Wanxiang.Protocol.WireCodec.encode (
            Wanxiang.Protocol.GenerationFinished
                {| conversationId = Guid.NewGuid()
                   generationId = Guid.NewGuid()
                   status = "failed"
                   error = Some error
                   usage = None |})
    match Wanxiang.Protocol.WireCodec.tryDecode encoded with
    | Ok (Wanxiang.Protocol.GenerationFinished d) ->
        let decoded = Option.get d.error
        Assert.Equal(error.kind, decoded.kind)
        Assert.Equal(error.message, decoded.message)
        Assert.Equal(error.detail, decoded.detail)
        Assert.Equal(error.retryAfterSeconds, decoded.retryAfterSeconds)
        Assert.True decoded.retryable
    | other -> failwithf "unexpected decode result: %A" other

// ---------------------------------------------------------------- 消息解析

let private mixedUserMessage () : JsonNode =
    let o = JsonObject()
    o["role"] <- "user"
    let contents = JsonArray()
    let text = JsonObject()
    text["text"] <- "看看这张图"
    contents.Add text
    let attachment = JsonObject()
    attachment["type"] <- "attachment"
    attachment["sha256"] <- "ab12"
    attachment["size"] <- 42L
    attachment["mediaType"] <- "image/png"
    attachment["fileName"] <- "shot.png"
    contents.Add attachment
    o["contents"] <- contents
    o

[<Fact>]
let ``mixed message keeps its text while exposing attachment references`` () =
    let node = mixedUserMessage ()
    let refs = MessageSerde.attachmentRefs node
    Assert.Single refs |> ignore
    Assert.Equal("ab12", refs.Head.sha256)
    Assert.Equal("image/png", refs.Head.mediaType)
    let message = MessageSerde.fromJsonNode node |> Option.get
    Assert.Equal("看看这张图", MessageSerde.textOf message)

[<Fact>]
let ``attachment-only message parses when empty contents are allowed`` () =
    let o = JsonObject()
    o["role"] <- "user"
    let contents = JsonArray()
    let attachment = JsonObject()
    attachment["type"] <- "attachment"
    attachment["sha256"] <- "ff00"
    contents.Add attachment
    o["contents"] <- contents
    Assert.True((MessageSerde.fromJsonNodeWith true o).IsSome)
    Assert.True((MessageSerde.fromJsonNodeWith false o).IsNone)

// ---------------------------------------------------------------- 附件送入模型

let private attachment (mediaType: string) (fileName: string) (size: int64) : AttachmentReference =
    { sha256 = "ab12"; mediaType = mediaType; fileName = fileName; size = size }

let private resolveWith (support: MediaSupport) (bytes: byte[] option) (reference: AttachmentReference) =
    AttachmentContent.resolve support (fun _ -> bytes) reference

let private textOf (contents: AIContent list) =
    contents |> List.pick (function :? TextContent as t -> Some t.Text | _ -> None)

let private binaries (contents: AIContent list) =
    contents |> List.choose (function :? DataContent as d -> Some d | _ -> None)

[<Fact>]
let ``images reach the model as binary content`` () =
    let bytes = Array.init 64 byte
    let contents = resolveWith MediaSupport.openAiCompatible (Some bytes) (attachment "image/png" "shot.png" 64L)
    Assert.Contains(contents, fun c -> c :? DataContent)

[<Fact>]
let ``text attachments are inlined as a fenced block`` () =
    let bytes = Encoding.UTF8.GetBytes "let x = 1"
    let contents =
        resolveWith MediaSupport.openAiCompatible (Some bytes) (attachment "text/plain" "a.fs" (int64 bytes.Length))
    let text = textOf contents
    Assert.Contains("```fsharp", text)
    Assert.Contains("let x = 1", text)

[<Fact>]
let ``a lost blob degrades to an explanation instead of failing`` () =
    let contents = resolveWith MediaSupport.openAiCompatible None (attachment "image/png" "gone.png" 10L)
    Assert.Contains("丢失", textOf contents)
    Assert.DoesNotContain(contents, fun c -> c :? DataContent)

[<Fact>]
let ``PDF 在支持的传输上作为二进制递过去`` () =
    // 兼容层只吃图片，原生 Anthropic / Gemini 才吃 PDF
    let bytes = Array.init 128 byte
    let file = attachment "application/pdf" "spec.pdf" 128L
    Assert.Empty(binaries (resolveWith MediaSupport.openAiCompatible (Some bytes) file))
    Assert.Single(binaries (resolveWith MediaSupport.anthropic (Some bytes) file)) |> ignore
    Assert.Single(binaries (resolveWith MediaSupport.gemini (Some bytes) file)) |> ignore

[<Fact>]
let ``音频只在 Gemini 上作为二进制递过去`` () =
    let bytes = Array.init 128 byte
    let file = attachment "audio/mpeg" "note.mp3" 128L
    Assert.Empty(binaries (resolveWith MediaSupport.openAiCompatible (Some bytes) file))
    Assert.Empty(binaries (resolveWith MediaSupport.anthropic (Some bytes) file))
    Assert.Single(binaries (resolveWith MediaSupport.gemini (Some bytes) file)) |> ignore

[<Fact>]
let ``传输吃不下的类型要说清原因而不是默默省略`` () =
    // 「悄悄退化」是最坏的结果：用户以为模型看过那份文件
    let contents = resolveWith MediaSupport.openAiCompatible (Some(Array.init 32 byte)) (attachment "application/pdf" "a.pdf" 32L)
    Assert.Contains("不接受该类型", textOf contents)

[<Fact>]
let ``超过内联上限的二进制附件给出上限说明`` () =
    let tooBig = int64 AttachmentContent.maxInlineBinaryBytes + 1L
    let bytes = Array.zeroCreate<byte> (int tooBig)
    let contents = resolveWith MediaSupport.gemini (Some bytes) (attachment "application/pdf" "huge.pdf" tooBig)
    Assert.Empty(binaries contents)
    Assert.Contains("内联上限", textOf contents)

// ---------------------------------------------------------------- 上下文裁剪

let private textMessage (role: ChatRole) (text: string) =
    MessageSerde.textMessage role text

[<Fact>]
let ``context trimming keeps the most recent messages`` () =
    let messages = [ for i in 1 .. 30 -> textMessage ChatRole.User (string i) ]
    let trimmed = GenerationContext.trim 10 0 messages
    Assert.Equal(10, List.length trimmed)
    Assert.Equal("30", MessageSerde.textOf (List.last trimmed))

[<Fact>]
let ``trimming never leaves an orphan tool result at the front`` () =
    let messages =
        [ textMessage ChatRole.User "问题"
          textMessage ChatRole.Tool "工具结果"
          textMessage ChatRole.Assistant "回答" ]
    let trimmed = GenerationContext.trim 2 0 messages
    Assert.DoesNotContain(ChatRole.Tool, trimmed |> List.map (fun m -> m.Role))

[<Fact>]
let ``zero means no message limit`` () =
    let messages = [ for i in 1 .. 5 -> textMessage ChatRole.User (string i) ]
    Assert.Equal(5, GenerationContext.trim 0 0 messages |> List.length)

// ---------------------------------------------------------------- 内建工具沙箱

[<Fact>]
let ``file tools are unavailable without a configured sandbox`` () =
    Assert.Empty(BuiltinTools.all { ToolsConfig.defaults with fileReadRoots = [] }
                 |> List.filter (fun t -> t.Name.StartsWith "builtin_file"))

[<Fact>]
let ``file tools appear once a sandbox root is configured`` () =
    let dir = tempDir ()
    try
        let tools = BuiltinTools.all { ToolsConfig.defaults with fileReadRoots = [ dir ] }
        Assert.Equal(2, tools |> List.filter (fun t -> t.Name.StartsWith "builtin_file") |> List.length)
    finally
        cleanup dir

[<Fact>]
let ``sandbox rejects paths outside every configured root`` () =
    let dir = tempDir ()
    try
        let roots = [ dir ]
        match BuiltinTools.resolveInRoots roots (Path.Combine(dir, "ok.txt")) with
        | Ok _ -> ()
        | Error e -> failwithf "expected the in-sandbox path to be accepted: %s" e
        for escape in [ "/etc/passwd"; Path.Combine(dir, "..", "escape.txt") ] do
            match BuiltinTools.resolveInRoots roots escape with
            | Ok _ -> failwithf "expected %s to be rejected" escape
            | Error _ -> ()
    finally
        cleanup dir

[<Fact>]
let ``stable tool ids round-trip to function names`` () =
    Assert.Equal("builtin_file_read", BuiltinTools.functionName "builtin:file.read")
    Assert.Equal("builtin:file.read", BuiltinTools.stableId "builtin_file_read")
    Assert.Equal("builtin:echo", BuiltinTools.stableId (BuiltinTools.functionName "builtin:echo"))

// ---------------------------------------------------------------- 配置写入

let private baseConfig () = AppConfig.defaults (Guid.NewGuid())

let private providerPayload () =
    let o = JsonObject()
    o["id"] <- "openai"
    o["label"] <- "OpenAI"
    o["baseUrl"] <- "https://api.openai.com/v1"
    o["apiKey"] <- "sk-secret"
    o["models"] <- JsonArray([| JsonNode.op_Implicit "gpt-4o"; JsonNode.op_Implicit "gpt-4o-mini" |])
    o["defaultModel"] <- "gpt-4o"
    o

[<Fact>]
let ``a provider can be added over the protocol`` () =
    match ConfigMutation.upsertProvider (baseConfig ()) (providerPayload ()) with
    | Error errors -> failwithf "unexpected rejection: %s" (String.Join("; ", errors))
    | Ok cfg ->
        let p = cfg.providers["openai"]
        Assert.Equal<string list>([ "gpt-4o"; "gpt-4o-mini" ], p.models)
        Assert.Equal("gpt-4o", p.defaultModel)
        Assert.Equal(Some "sk-secret", p.apiKey)

[<Fact>]
let ``omitting the api key preserves the stored one`` () =
    let withKey =
        ConfigMutation.upsertProvider (baseConfig ()) (providerPayload ())
        |> function Ok c -> c | Error e -> failwithf "%A" e
    let payload = providerPayload ()
    payload.Remove "apiKey" |> ignore
    payload["label"] <- "改名了"
    match ConfigMutation.upsertProvider withKey payload with
    | Error errors -> failwithf "unexpected rejection: %s" (String.Join("; ", errors))
    | Ok cfg ->
        Assert.Equal(Some "sk-secret", cfg.providers["openai"].apiKey)
        Assert.Equal("改名了", cfg.providers["openai"].label)

[<Fact>]
let ``an invalid provider is rejected with one reason per problem`` () =
    let payload = JsonObject()
    payload["id"] <- "broken"
    payload["baseUrl"] <- "not-a-url"
    payload["models"] <- JsonArray()
    match ConfigMutation.upsertProvider (baseConfig ()) payload with
    | Ok _ -> failwith "expected rejection"
    | Error errors ->
        Assert.Contains(errors, fun e -> e.Contains "baseUrl")
        Assert.Contains(errors, fun e -> e.Contains "models")

[<Fact>]
let ``an mcp server needs exactly one transport`` () =
    let neither = JsonObject()
    neither["id"] <- "fs"
    match ConfigMutation.upsertMcp (baseConfig ()) neither with
    | Ok _ -> failwith "expected rejection when no transport is configured"
    | Error errors -> Assert.Contains(errors, fun e -> e.Contains "command")
    let both = JsonObject()
    both["id"] <- "fs"
    both["command"] <- "npx"
    both["url"] <- "https://example.com/mcp"
    match ConfigMutation.upsertMcp (baseConfig ()) both with
    | Ok _ -> failwith "expected rejection when both transports are configured"
    | Error errors -> Assert.Contains(errors, fun e -> e.Contains "只能填一个")

[<Fact>]
let ``generation defaults are validated before they are stored`` () =
    let payload = JsonObject()
    payload["temperature"] <- 9.0
    match ConfigMutation.updateGeneration (baseConfig ()) payload with
    | Ok _ -> failwith "expected rejection"
    | Error errors -> Assert.Contains(errors, fun e -> e.Contains "temperature")

[<Fact>]
let ``clearing an optional generation parameter is possible`` () =
    let cfg = { baseConfig () with generation = { GenerationDefaults.defaults with temperature = Some 0.7 } }
    let payload = JsonObject()
    payload["temperature"] <- null
    match ConfigMutation.updateGeneration cfg payload with
    | Error errors -> failwithf "unexpected rejection: %s" (String.Join("; ", errors))
    | Ok next -> Assert.Equal(None, next.generation.temperature)

// ---------------------------------------------------------------- 目录快照

[<Fact>]
let ``the catalog never exposes api keys`` () =
    let cfg =
        ConfigMutation.upsertProvider (baseConfig ()) (providerPayload ())
        |> function Ok c -> c | Error e -> failwithf "%A" e
    let json = (ProviderCatalog.providers cfg).ToJsonString()
    Assert.DoesNotContain("sk-secret", json)
    Assert.Contains("\"hasApiKey\":true", json)

// ---------------------------------------------------------------- 重新生成与标记

[<Fact>]
let ``regenerate targets the trailing model messages only`` () =
    let record commitId payload : MessageRecord =
        { commitId = commitId
          conversationId = Guid.Empty
          payloadJson = payload
          committedAtUtc = DateTimeOffset.UtcNow
          deletedAtCommitId = None }
    let visible =
        [ record 1UL (userMessageJson "问题一")
          record 2UL (assistantMessageJson "回答一")
          record 3UL (userMessageJson "问题二")
          record 4UL (assistantMessageJson "回答二")
          record 5UL (assistantMessageJson "补充") ]
    Assert.Equal<CommitId list>([ 4UL; 5UL ], CommandEngine.trailingModelMessages visible)

[<Fact>]
let ``regenerate is rejected when the last message is the user's`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coordinator = new CommitCoordinator(dir, outcome, ignore, ignore)
        let convId = newConversationId ()
        coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
        |> ignore
        coordinator.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "只问不答" } ]
        |> ignore
        let command = RegenerateResponse {| invocationId = Guid.NewGuid(); conversationId = convId |}
        match CommandEngine.plan coordinator.Projection coordinator.Projection.latestCommitId command with
        | Rejected (ValidationError message) -> Assert.Contains("regenerate", message)
        | other -> failwithf "expected a validation rejection, got %A" other
    finally
        cleanup dir

[<Fact>]
let ``pin and archive flags are projected and survive replay`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        let convId = newConversationId ()
        (using (new CommitCoordinator(dir, outcome, ignore, ignore)) (fun coordinator ->
            coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
            |> ignore
            coordinator.SubmitEvents
                [ ConversationFlagsChanged { conversationId = convId; pinned = true; archived = false } ]
            |> ignore
            let conv = Projection.tryConversation coordinator.Projection convId |> Option.get
            Assert.True conv.pinned
            Assert.False conv.archived))
        let replayed = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use reopened = new CommitCoordinator(dir, replayed, ignore, ignore)
        let conv = Projection.tryConversation reopened.Projection convId |> Option.get
        Assert.True conv.pinned
    finally
        cleanup dir

[<Fact>]
let ``pinned conversations sort ahead of more recent ones`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coordinator = new CommitCoordinator(dir, outcome, ignore, ignore)
        let older = newConversationId ()
        let newer = newConversationId ()
        coordinator.SubmitEvents [ ConversationCreated { conversationId = older; title = "旧"; config = testConfig () } ]
        |> ignore
        coordinator.SubmitEvents [ ConversationCreated { conversationId = newer; title = "新"; config = testConfig () } ]
        |> ignore
        coordinator.SubmitEvents [ ConversationFlagsChanged { conversationId = older; pinned = true; archived = false } ]
        |> ignore
        let ordered = Projection.conversationList coordinator.Projection |> List.map (fun c -> c.conversationId)
        Assert.Equal(older, List.head ordered)
    finally
        cleanup dir

// ---------------------------------------------------------------- 会话配置校验

[<Fact>]
let ``session config reports every invalid field`` () =
    let cfg =
        { SessionConfig.empty with
            provider = ""
            model = ""
            temperature = Some 5.0
            topP = Some 2.0
            maxTokens = Some 0 }
    let problems = SessionConfig.validate cfg
    Assert.Equal(5, List.length problems)
    Assert.False(SessionConfig.isValid cfg)

[<Fact>]
let ``session config survives a commit-codec roundtrip including topP`` () =
    let cfg =
        { SessionConfig.empty with
            provider = "openai"
            model = "gpt-4o"
            instructions = Some "保持简洁"
            tools = [ "builtin:echo" ]
            temperature = Some 0.4
            topP = Some 0.9
            maxTokens = Some 512 }
    let restored = CommitCodec.configFromJson (CommitCodec.configToJson cfg)
    Assert.Equal(cfg, restored)

// ---------------------------------------------------------------- 编排记账与结束决策（D1/D2/N2 回归）

[<Fact>]
let ``queued submit outcomes always resolve to committed or visible error`` () =
    let convId = newConversationId ()
    let commit =
        Events.Commit.create
            7UL
            DateTimeOffset.UtcNow
            [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
    let failure = ValidationError "boom"
    // committed 系列 → Ok（拿到 log id，可广播 CommandCommitted）
    for result in [ Committed commit; IdempotentReplay commit; TruncatedAndReused (commit, failure) ] do
        match SubmitOutcome.ofSubmitResult result with
        | Ok c -> Assert.Equal(7UL, c.id)
        | Error e -> failwithf "should resolve to committed, got %s" (WanxiangError.message e)
    // 失败系列 → Error（调用方经 RecordSubmitOutcome 广播 ServerError，不静默）
    for result in [ CommandIdRejected failure; CommitFailed failure ] do
        match SubmitOutcome.ofSubmitResult result with
        | Ok _ -> failwith "should resolve to a visible error"
        | Error e -> Assert.Equal(failure, e)

[<Fact>]
let ``empty built context with a drained batch fails retryably, never completes`` () =
    let status, err = EmptyContextFinish.decide true
    Assert.Equal("failed", status)
    match err with
    | None -> failwith "non-empty batch must carry an error card"
    | Some e ->
        Assert.True e.retryable
        Assert.False(String.IsNullOrWhiteSpace e.message)
    let idleStatus, idleErr = EmptyContextFinish.decide false
    Assert.Equal("completed", idleStatus)
    Assert.Equal(None, idleErr)

[<Fact>]
let ``tool round limit error is retryable and names maxToolRounds`` () =
    let err = ToolRoundLimit.error 12
    Assert.Equal(ToolFailed, err.kind)
    Assert.True err.retryable
    Assert.Contains("maxToolRounds", err.message)
    Assert.Contains("12", err.message)
