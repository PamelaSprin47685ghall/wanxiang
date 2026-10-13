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
