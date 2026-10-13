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

// ---------------------------------------------------------------- 对话标题

[<Fact>]
let ``auto title keeps only the first sentence and strips markdown`` () =
    let title = TitleGenerator.sanitize "**万象存储模型**。第二句应当被丢掉。" "原始提问"
    Assert.Equal("万象存储模型", title)

[<Fact>]
let ``auto title falls back to the user question when the model returns nothing`` () =
    Assert.Equal("解释一下投影", TitleGenerator.sanitize "   " "解释一下投影")

[<Fact>]
let ``only system placeholder titles are auto-renamed`` () =
    Assert.True(TitleGenerator.isPlaceholder "新对话")
    Assert.True(TitleGenerator.isPlaceholder "对话 3f8a1c22")
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
      extraJson = None; visionOverrides = Map.empty }

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
        let outcome = replayOutcome dir
        use coordinator = new EventStore(dir)
        let convId = newConversationId ()
        coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
        |> ignore
        coordinator.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "只问不答" } ]
        |> ignore
        let command = RegenerateResponse {| invocationId = Guid.NewGuid(); conversationId = convId |}
        match CommandEngine.planStamped coordinator.Stamps coordinator.Projection coordinator.Projection.latestCommitId command with
        | Rejected (ValidationError message) -> Assert.Contains("regenerate", message)
        | other -> failwithf "expected a validation rejection, got %A" other
    finally
        cleanup dir

[<Fact>]
let ``pin and archive flags are projected and survive replay`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        let convId = newConversationId ()
        (using (new EventStore(dir)) (fun coordinator ->
            coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
            |> ignore
            coordinator.SubmitEvents
                [ ConversationFlagsChanged { conversationId = convId; pinned = true; archived = false } ]
            |> ignore
            let conv = Projection.tryConversation coordinator.Projection convId |> Option.get
            Assert.True conv.pinned
            Assert.False conv.archived))
        let replayed = replayOutcome dir
        use reopened = new EventStore(dir)
        let conv = Projection.tryConversation reopened.Projection convId |> Option.get
        Assert.True conv.pinned
    finally
        cleanup dir

[<Fact>]
let ``pinned conversations sort ahead of more recent ones`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coordinator = new EventStore(dir)
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

// ---------------------------------------------------------------- 对话配置校验

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
    for result in [ Committed commit; IdempotentReplay commit ] do
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
