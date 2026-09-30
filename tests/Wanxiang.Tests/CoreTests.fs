namespace Wanxiang.Tests

open System
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core

module CoreTests =

    let private dummyConfig: SessionConfig =
        { SessionConfig.empty with provider = "openai"; model = "gpt-4o" }

    let private createTestConv (convId: Guid) (title: string) : Conversation =
        { conversationId = convId
          title = title
          createdAtUtc = DateTimeOffset.UtcNow
          lastActivityAtUtc = DateTimeOffset.UtcNow
          deleted = false
          pinned = false
          archived = false
          parent = None
          forkBaseCommitId = None
          config = dummyConfig
          messages = []
          lastCommitId = Some 1UL }

    // =========================================================================
    // 1. Types.fs 领域类型、DU变体与边界测试
    // =========================================================================

    [<Fact>]
    let ``GenerationUsage all fields some and none`` () =
        let u1: GenerationUsage =
            { promptTokens = Some 10
              completionTokens = Some 20
              cachedTokens = Some 5
              totalTokens = Some 35
              durationMs = Some 1500L }
        Assert.Equal(Some 10, u1.promptTokens)
        Assert.Equal(Some 20, u1.completionTokens)
        Assert.Equal(Some 5, u1.cachedTokens)
        Assert.Equal(Some 35, u1.totalTokens)
        Assert.Equal(Some 1500L, u1.durationMs)

        // formatSummary
        let sum1 = GenerationUsage.formatSummary u1
        Assert.True(sum1.IsSome)
        Assert.Contains("35 tok", sum1.Value)
        Assert.Contains("1.5s", sum1.Value)

        // cachedCount
        Assert.Equal(Some 5, GenerationUsage.cachedCount u1)

        // formatDetail
        let det1 = GenerationUsage.formatDetail u1
        Assert.Contains("输入 10", det1)
        Assert.Contains("输出 20", det1)
        Assert.Contains("缓存 5", det1)
        Assert.Contains("合计 35", det1)
        Assert.Contains("耗时 1.5s", det1)

        let uEmpty = GenerationUsage.empty
        Assert.True(uEmpty.promptTokens.IsNone)
        Assert.True(uEmpty.totalTokens.IsNone)
        Assert.True(GenerationUsage.formatSummary uEmpty |> Option.isNone)
        Assert.True(GenerationUsage.cachedCount uEmpty |> Option.isNone)
        Assert.Equal("暂无 token 数据", GenerationUsage.formatDetail uEmpty)

    [<Fact>]
    let ``GenerationErrorKind all 11 variants wireName and parse roundtrip`` () =
        let allKinds =
            [ ProviderAuthFailed
              ProviderRateLimited
              ProviderTimeout
              ProviderUnavailable
              ProviderBadRequest
              ContextTooLong
              ModelNotFound
              ContentFiltered
              ToolFailed
              ConfigInvalid
              UnknownFailure ]

        for k in allKinds do
            let code = GenerationErrorKind.code k
            Assert.False(String.IsNullOrWhiteSpace code)
            let parsed = GenerationErrorKind.ofCode code
            Assert.Equal(k, parsed)
            let hintText = GenerationErrorKind.hint k
            Assert.False(String.IsNullOrWhiteSpace hintText)
            let _ = GenerationError.isRetryable k
            ()

        // 未知字符串解析回退到 UnknownFailure
        Assert.Equal(UnknownFailure, GenerationErrorKind.ofCode null)
        Assert.Equal(UnknownFailure, GenerationErrorKind.ofCode "")
        Assert.Equal(UnknownFailure, GenerationErrorKind.ofCode "some_unrecognized_error_kind")

        // 针对服务商不可用的细分建议
        Assert.Contains("维护", GenerationErrorKind.hintUnavailable true)
        Assert.Contains("网络", GenerationErrorKind.hintUnavailable false)

    [<Fact>]
    let ``GenerationError create withDetail and display`` () =
        let errWithDetails =
            GenerationError.create ProviderAuthFailed "Invalid API key"
            |> GenerationError.withDetail "HTTP 401 Unauthorized"

        Assert.Equal(ProviderAuthFailed, errWithDetails.kind)
        Assert.Equal("Invalid API key", errWithDetails.message)
        Assert.Equal(Some "HTTP 401 Unauthorized", errWithDetails.detail)
        Assert.False(errWithDetails.retryable)

        let disp1 = GenerationError.display errWithDetails
        Assert.Contains("Invalid API key", disp1)
        Assert.Contains("API Key", disp1)

        let errWithoutDetails = GenerationError.create ProviderTimeout "Gateway timeout"
        Assert.True(errWithoutDetails.retryable)
        let disp2 = GenerationError.display errWithoutDetails
        Assert.Contains("Gateway timeout", disp2)
        Assert.Contains("重试", disp2)

        // 细分网络错误 vs 5xx
        let netErr =
            GenerationError.create ProviderUnavailable "Failed"
            |> GenerationError.withDetail "SocketException connection refused"
        Assert.Contains("网络", GenerationError.hint netErr)

        let srvErr =
            GenerationError.create ProviderUnavailable "Failed"
            |> GenerationError.withDetail "502 Bad Gateway"
        Assert.Contains("维护", GenerationError.hint srvErr)

    [<Fact>]
    let ``WanxiangError all variants code and message`` () =
        let testGuid = Guid.NewGuid()
        let errs: WanxiangError list =
            [ ValidationError "invalid input"
              StaleProjection 42UL
              CommandIdConflict "conflict"
              ConversationNotFound testGuid
              WanxiangError.ConversationDeleted testGuid
              ConversationIdTaken testGuid
              ForkParentNotFound testGuid
              ForkPointInvalid "bad fork point"
              GenerationNotFound (testGuid, Guid.NewGuid())
              GenerationBusy testGuid
              NotAuthenticated
              ProtocolMismatch (2, 1)
              UnknownEventType "test.event"
              Poisoned "poison"
              AttachmentTooLarge (1024L, 2048L)
              AttachmentHashMismatch ("abc", "def")
              AttachmentIncomplete testGuid
              ConfigRejected "cfg"
              AuthRejected "auth"
              Cancelled "user cancel" ]

        Assert.Equal(20, errs.Length)
        for e in errs do
            let c = WanxiangError.code e
            Assert.False(String.IsNullOrWhiteSpace c)
            let m = WanxiangError.message e
            Assert.False(String.IsNullOrWhiteSpace m)

    [<Fact>]
    let ``SessionConfig validation and isValid`` () =
        let validCfg: SessionConfig =
            { SessionConfig.empty with
                provider = "anthropic"
                model = "claude-3-5-sonnet"
                temperature = Some 0.7
                topP = Some 0.95
                maxTokens = Some 4096 }
        Assert.True(SessionConfig.isValid validCfg)
        Assert.Empty(SessionConfig.validate validCfg)

        // 边界错误校验
        let emptyP = { validCfg with provider = "" }
        Assert.False(SessionConfig.isValid emptyP)
        Assert.Contains("provider: 必填", SessionConfig.validate emptyP)

        let emptyM = { validCfg with model = "   " }
        Assert.False(SessionConfig.isValid emptyM)
        Assert.Contains("model: 必填", SessionConfig.validate emptyM)

        let badTemp = { validCfg with temperature = Some -0.1 }
        Assert.False(SessionConfig.isValid badTemp)
        Assert.Contains("temperature: 需在 0–2 之间", SessionConfig.validate badTemp)

        let badTopP = { validCfg with topP = Some 1.5 }
        Assert.False(SessionConfig.isValid badTopP)
        Assert.Contains("topP: 需在 0–1 之间", SessionConfig.validate badTopP)

        let badTokens = { validCfg with maxTokens = Some 0 }
        Assert.False(SessionConfig.isValid badTokens)
        Assert.Contains("maxTokens: 需为正整数", SessionConfig.validate badTokens)

    // =========================================================================
    // 2. CommandEngine.fs 命令规划与验证测试
    // =========================================================================

    [<Fact>]
    let ``CreateConversation success plan and validations`` () =
        let convId = Guid.NewGuid()
        let cmd =
            CreateConversation
                {| invocationId = Guid.NewGuid()
                   conversationId = convId
                   title = "New Conversation"
                   config = dummyConfig |}

        // 1. 成功规划
        match CommandEngine.plan Projection.empty 0UL cmd with
        | Planned plan ->
            Assert.Equal("conversation.create", plan.commandType)
            Assert.Equal(0UL, plan.watermark)
            Assert.Single(plan.events) |> ignore
            match plan.events.Head with
            | ConversationCreated ev ->
                Assert.Equal(convId, ev.conversationId)
                Assert.Equal("New Conversation", ev.title)
                Assert.Equal("openai", ev.config.provider)
            | _ -> Assert.Fail("Expected ConversationCreated event")
        | r -> Assert.Fail(sprintf "Expected Planned, got %A" r)

        // 2. 配置无效
        let invalidCmd =
            CreateConversation
                {| invocationId = Guid.NewGuid()
                   conversationId = Guid.NewGuid()
                   title = "New"
                   config = SessionConfig.empty |}
        match CommandEngine.plan Projection.empty 0UL invalidCmd with
        | Rejected (ValidationError msg) -> Assert.Contains("invalid session config", msg)
        | r -> Assert.Fail(sprintf "Expected ValidationError, got %A" r)

        // 3. ID 已被占用
        let projWithConv = { Projection.empty with conversations = Map.ofList [ (convId, createTestConv convId "Ex") ] }
        match CommandEngine.plan projWithConv 0UL cmd with
        | Rejected (ConversationIdTaken id) -> Assert.Equal(convId, id)
        | r -> Assert.Fail(sprintf "Expected ConversationIdTaken, got %A" r)

        // 4. 游标陈旧
        let projAdvanced = { Projection.empty with latestCommitId = 10UL }
        match CommandEngine.plan projAdvanced 5UL cmd with
        | Rejected (StaleProjection wm) -> Assert.Equal(10UL, wm)
        | r -> Assert.Fail(sprintf "Expected StaleProjection, got %A" r)

    [<Fact>]
    let ``ForkConversation validation and success branches`` () =
        let parentId = Guid.NewGuid()
        let parentConv = { (createTestConv parentId "Parent") with lastCommitId = Some 10UL }
        let msgRecord: MessageRecord =
            { commitId = 8UL
              conversationId = parentId
              payloadJson = JsonObject()
              committedAtUtc = DateTimeOffset.UtcNow
              deletedAtCommitId = None }
        let parentWithMsg = { parentConv with messages = [ msgRecord ] }
        let proj = { Projection.empty with conversations = Map.ofList [ (parentId, parentWithMsg) ]; latestCommitId = 10UL }

        let newConvId = Guid.NewGuid()

        // 1. 父会话不存在
        let cmdMissingParent =
            ForkConversation
                {| invocationId = Guid.NewGuid()
                   conversationId = newConvId
                   parentConversationId = Guid.NewGuid()
                   forkAfterId = None
                   config = dummyConfig
                   editedMessageJson = JsonObject() |}
        match CommandEngine.plan proj 10UL cmdMissingParent with
        | Rejected (ConversationNotFound _) -> ()
        | r -> Assert.Fail(sprintf "Expected ConversationNotFound, got %A" r)

        // 2. 父会话已删除
        let deletedParent = { parentWithMsg with deleted = true }
        let projDeleted = { proj with conversations = Map.ofList [ (parentId, deletedParent) ] }
        match CommandEngine.plan projDeleted 10UL cmdMissingParent with
        | Rejected (ConversationNotFound _) -> ()
        | r -> Assert.Fail(sprintf "Expected ConversationNotFound, got %A" r)

        // 3. forkAfterId 不在父会话中
        let cmdBadForkPoint =
            ForkConversation
                {| invocationId = Guid.NewGuid()
                   conversationId = newConvId
                   parentConversationId = parentId
                   forkAfterId = Some 999UL
                   config = dummyConfig
                   editedMessageJson = JsonObject() |}
        match CommandEngine.plan proj 10UL cmdBadForkPoint with
        | Rejected (ForkPointInvalid msg) -> Assert.Contains("fork point", msg)
        | r -> Assert.Fail(sprintf "Expected ForkPointInvalid, got %A" r)

        // 4. 成功 fork（带合法 forkAfterId）
        let cmdOk =
            ForkConversation
                {| invocationId = Guid.NewGuid()
                   conversationId = newConvId
                   parentConversationId = parentId
                   forkAfterId = Some 8UL
                   config = dummyConfig
                   editedMessageJson = JsonObject() |}
        match CommandEngine.plan proj 10UL cmdOk with
        | Planned plan ->
            Assert.Equal(10UL, plan.watermark)
            Assert.Equal(2, plan.events.Length)
            match plan.events[0] with
            | ConversationForked ev ->
                Assert.Equal(newConvId, ev.conversationId)
                Assert.Equal(parentId, ev.parentConversationId)
                Assert.Equal(Some 8UL, ev.forkAfterId)
            | _ -> Assert.Fail("Expected ConversationForked event")
        | r -> Assert.Fail(sprintf "Expected Planned, got %A" r)

    [<Fact>]
    let ``RenameConversation validation and success branches`` () =
        let convId = Guid.NewGuid()
        let conv = { (createTestConv convId "Old Title") with lastCommitId = Some 3UL }
        let proj = { Projection.empty with conversations = Map.ofList [ (convId, conv) ]; latestCommitId = 15UL }

        // 1. 标题为空
        let cmdEmpty =
            RenameConversation
                {| invocationId = Guid.NewGuid(); conversationId = convId; title = "   " |}
        match CommandEngine.plan proj 3UL cmdEmpty with
        | Rejected (ValidationError msg) -> Assert.Contains("characters", msg)
        | r -> Assert.Fail(sprintf "Expected ValidationError, got %A" r)

        // 2. 会话不存在
        let cmdNotFound =
            RenameConversation
                {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = "New" |}
        match CommandEngine.plan proj 3UL cmdNotFound with
        | Rejected (ConversationNotFound _) -> ()
        | r -> Assert.Fail(sprintf "Expected ConversationNotFound, got %A" r)

        // 3. 成功重命名
        let cmdOk =
            RenameConversation
                {| invocationId = Guid.NewGuid(); conversationId = convId; title = "New Title" |}
        match CommandEngine.plan proj 3UL cmdOk with
        | Planned plan ->
            Assert.Equal(3UL, plan.watermark)
            match plan.events.Head with
            | ConversationRenamed ev -> Assert.Equal("New Title", ev.title)
            | _ -> Assert.Fail("Expected ConversationRenamed")
        | r -> Assert.Fail(sprintf "Expected Planned, got %A" r)

    [<Fact>]
    let ``UpdateConversationConfig validation and success branches`` () =
        let convId = Guid.NewGuid()
        let conv = { (createTestConv convId "Test") with lastCommitId = Some 5UL }
        let proj = { Projection.empty with conversations = Map.ofList [ (convId, conv) ]; latestCommitId = 15UL }

        // 1. 配置非法
        let cmdBadCfg =
            UpdateConversationConfig
                {| invocationId = Guid.NewGuid(); conversationId = convId; config = SessionConfig.empty |}
        match CommandEngine.plan proj 5UL cmdBadCfg with
        | Rejected (ValidationError _) -> ()
        | r -> Assert.Fail(sprintf "Expected ValidationError, got %A" r)

        // 2. 成功更新
        let newCfg = { dummyConfig with temperature = Some 0.2 }
        let cmdOk =
            UpdateConversationConfig
                {| invocationId = Guid.NewGuid(); conversationId = convId; config = newCfg |}
        match CommandEngine.plan proj 5UL cmdOk with
        | Planned plan ->
            match plan.events.Head with
            | ConversationConfigUpdated ev ->
                Assert.Equal(convId, ev.conversationId)
                Assert.Equal(Some 0.2, ev.config.temperature)
            | _ -> Assert.Fail("Expected ConversationConfigUpdated")
        | r -> Assert.Fail(sprintf "Expected Planned, got %A" r)

    [<Fact>]
    let ``DeleteConversation validation and success branches`` () =
        let convId = Guid.NewGuid()
        let conv = { (createTestConv convId "Test") with lastCommitId = Some 7UL }
        let proj = { Projection.empty with conversations = Map.ofList [ (convId, conv) ]; latestCommitId = 15UL }

        let cmd = DeleteConversation {| invocationId = Guid.NewGuid(); conversationId = convId |}

        // 1. 成功删除
        match CommandEngine.plan proj 7UL cmd with
        | Planned plan ->
            Assert.Equal(7UL, plan.watermark)
            match plan.events.Head with
            | EventData.ConversationDeleted ev -> Assert.Equal(convId, ev.conversationId)
            | _ -> Assert.Fail("Expected ConversationDeleted")
        | r -> Assert.Fail(sprintf "Expected Planned, got %A" r)

        // 2. 重复删除
        let projDeleted = { proj with conversations = Map.ofList [ (convId, { conv with deleted = true }) ] }
        match CommandEngine.plan projDeleted 7UL cmd with
        | Rejected (ValidationError msg) -> Assert.Contains("already deleted", msg)
        | r -> Assert.Fail(sprintf "Expected ValidationError, got %A" r)

    [<Fact>]
    let ``SetConversationFlags validation and success branches`` () =
        let convId = Guid.NewGuid()
        let conv = { (createTestConv convId "Test") with lastCommitId = Some 8UL; pinned = false; archived = false }
        let proj = { Projection.empty with conversations = Map.ofList [ (convId, conv) ]; latestCommitId = 15UL }

        // 1. 标志无变动报错
        let cmdUnchanged =
            SetConversationFlags
                {| invocationId = Guid.NewGuid(); conversationId = convId; pinned = false; archived = false |}
        match CommandEngine.plan proj 8UL cmdUnchanged with
        | Rejected (ValidationError msg) -> Assert.Contains("unchanged", msg)
        | r -> Assert.Fail(sprintf "Expected ValidationError, got %A" r)

        // 2. 正常修改标志
        let cmdOk =
            SetConversationFlags
                {| invocationId = Guid.NewGuid(); conversationId = convId; pinned = true; archived = false |}
        match CommandEngine.plan proj 8UL cmdOk with
        | Planned plan ->
            match plan.events.Head with
            | ConversationFlagsChanged ev ->
                Assert.True(ev.pinned)
                Assert.False(ev.archived)
            | _ -> Assert.Fail("Expected ConversationFlagsChanged")
        | r -> Assert.Fail(sprintf "Expected Planned, got %A" r)

    [<Fact>]
    let ``DeleteMessage validation and success branches`` () =
        let convId = Guid.NewGuid()
        let targetMsgId = 15UL
        let msgRecord: MessageRecord =
            { commitId = targetMsgId
              conversationId = convId
              payloadJson = JsonObject()
              committedAtUtc = DateTimeOffset.UtcNow
              deletedAtCommitId = None }
        let conv = { (createTestConv convId "Test") with messages = [ msgRecord ]; lastCommitId = Some 15UL }
        let proj = { Projection.empty with conversations = Map.ofList [ (convId, conv) ]; latestCommitId = 15UL }

        // 1. 消息不存在报错
        let cmdNotFound =
            DeleteMessage {| invocationId = Guid.NewGuid(); conversationId = convId; messageCommitId = 9999UL |}
        match CommandEngine.plan proj 15UL cmdNotFound with
        | Rejected (ValidationError msg) -> Assert.Contains("not visible", msg)
        | r -> Assert.Fail(sprintf "Expected ValidationError, got %A" r)

        // 2. 成功删除
        let cmdOk =
            DeleteMessage {| invocationId = Guid.NewGuid(); conversationId = convId; messageCommitId = targetMsgId |}
        match CommandEngine.plan proj 15UL cmdOk with
        | Planned plan ->
            Assert.Equal(15UL, plan.watermark)
            match plan.events.Head with
            | MessageDeleted ev ->
                Assert.Equal(convId, ev.conversationId)
                Assert.Equal(targetMsgId, ev.messageCommitId)
            | _ -> Assert.Fail("Expected MessageDeleted")
        | r -> Assert.Fail(sprintf "Expected Planned, got %A" r)

    [<Fact>]
    let ``Idempotent replay and hash conflict detection`` () =
        let convId = Guid.NewGuid()
        let invId = Guid.NewGuid()
        let cmd =
            CreateConversation
                {| invocationId = invId
                   conversationId = convId
                   title = "Title"
                   config = dummyConfig |}

        let canonicalPayload = ClientCommand.canonicalPayload cmd
        let canonicalHash = CommandId.sha256Hex canonicalPayload
        let ctype = ClientCommand.commandType cmd
        let commandId = CommandId.compute invId ctype canonicalPayload

        let idemRec: IdemRecord =
            { commandId = commandId
              invocationId = invId
              commandType = ctype
              canonicalHash = canonicalHash
              commitId = 100UL }

        let projWithIdem = { Projection.empty with idempotency = Map.ofList [ (commandId, idemRec) ] }

        // 1. 相同载荷幂等重放
        match CommandEngine.plan projWithIdem 100UL cmd with
        | IdempotentReplay commitId -> Assert.Equal(100UL, commitId)
        | r -> Assert.Fail(sprintf "Expected IdempotentReplay, got %A" r)

        // 2. 相同 commandId 不同载荷报错
        let tamperedRec = { idemRec with canonicalHash = "different-hash" }
        let projTampered = { Projection.empty with idempotency = Map.ofList [ (commandId, tamperedRec) ] }
        match CommandEngine.plan projTampered 100UL cmd with
        | Rejected (CommandIdConflict msg) -> Assert.Contains("reused with different payload", msg)
        | r -> Assert.Fail(sprintf "Expected CommandIdConflict, got %A" r)

    // =========================================================================
    // 3. CommitCodec.fs 编解码与畸形容错测试
    // =========================================================================

    [<Fact>]
    let ``commitToJsonLine and tryCommitFromJsonLine roundtrip with all 8 event types`` () =
        let convId = Guid.NewGuid()
        let parentId = Guid.NewGuid()
        let msgPayload = JsonObject()
        msgPayload["text"] <- JsonValue.Create("Sample agent response")

        let allEvents =
            [ ConversationCreated { conversationId = convId; title = "AllEventsConv"; config = dummyConfig }
              ConversationForked { conversationId = Guid.NewGuid(); parentConversationId = parentId; forkAfterId = Some 42UL }
              ConversationForked { conversationId = Guid.NewGuid(); parentConversationId = parentId; forkAfterId = None }
              ConversationRenamed { conversationId = convId; title = "RenamedTitle" }
              ConversationConfigUpdated { conversationId = convId; config = { dummyConfig with temperature = Some 0.5 } }
              EventData.ConversationDeleted { conversationId = convId }
              ConversationFlagsChanged { conversationId = convId; pinned = true; archived = true }
              AgentMessageRecorded { conversationId = convId; payloadJson = msgPayload }
              MessageDeleted { conversationId = convId; messageCommitId = 101UL } ]

        let commit: Events.Commit =
            { formatVersion = Constants.FormatVersion
              id = 1001UL
              bootId = Constants.DefaultBootId
              source = Constants.DefaultSource
              committedAtUtc = DateTimeOffset.Parse("2026-09-30T12:00:00+00:00")
              commandId = Some "cmd-roundtrip"
              commandType = Some "batch.action"
              commandHash = Some "sha256-hash"
              events = allEvents }

        let json = CommitCodec.commitToJsonLine commit
        Assert.False(String.IsNullOrWhiteSpace json)

        let decodedOpt = CommitCodec.tryCommitFromJsonLine json
        match decodedOpt with
        | Some (dec: Events.Commit) ->
            Assert.Equal(commit.id, dec.id)
            Assert.Equal(commit.commandId, dec.commandId)
            Assert.Equal(commit.commandType, dec.commandType)
            Assert.Equal(commit.commandHash, dec.commandHash)
            Assert.Equal(commit.events.Length, dec.events.Length)
        | None -> Assert.Fail("tryCommitFromJsonLine failed to parse valid line")

    [<Fact>]
    let ``commitToJsonLine with empty events and None option fields`` () =
        let commit: Events.Commit =
            { formatVersion = Constants.FormatVersion
              id = 1UL
              bootId = Constants.DefaultBootId
              source = Constants.DefaultSource
              committedAtUtc = DateTimeOffset.UtcNow
              commandId = None
              commandType = None
              commandHash = None
              events = [] }
        let json = CommitCodec.commitToJsonLine commit
        match CommitCodec.tryCommitFromJsonLine json with
        | Some (dec: Events.Commit) ->
            Assert.Equal(1UL, dec.id)
            Assert.True(dec.commandId.IsNone)
            Assert.True(dec.commandType.IsNone)
            Assert.True(dec.commandHash.IsNone)
            Assert.Empty(dec.events)
        | None -> Assert.Fail("Failed to decode empty commit")

    [<Fact>]
    let ``tryCommitFromJsonLine malformed JSON top-level branches`` () =
        // 1. Not a JSON string -> None
        Assert.True(CommitCodec.tryCommitFromJsonLine "not valid json {{" |> Option.isNone)

        // 2. Not a JSON Object (e.g. array or primitive) -> None
        Assert.True(CommitCodec.tryCommitFromJsonLine "[1, 2, 3]" |> Option.isNone)

        // 3. Missing id -> None
        Assert.True(CommitCodec.tryCommitFromJsonLine "{\"committedAtUtc\": \"2026-09-30T12:00:00Z\", \"events\": []}" |> Option.isNone)

        // 4. Missing committedAtUtc -> None
        Assert.True(CommitCodec.tryCommitFromJsonLine "{\"formatVersion\": 1, \"id\": \"1\", \"events\": []}" |> Option.isNone)

        // 5. Missing events -> None
        Assert.True(CommitCodec.tryCommitFromJsonLine "{\"formatVersion\": 1, \"id\": \"1\", \"committedAtUtc\": \"2026-09-30T12:00:00Z\"}" |> Option.isNone)

        // 6. Unknown event type -> None
        let unknownType = "{\"formatVersion\": 1, \"id\": \"1\", \"committedAtUtc\": \"2026-09-30T12:00:00Z\", \"events\": [{\"type\": \"unknown.event\", \"data\": {}}]}"
        Assert.True(CommitCodec.tryCommitFromJsonLine unknownType |> Option.isNone)

    [<Fact>]
    let ``tryCommitFromJsonLine config parsing with optional fields`` () =
        let jsonWithOptional =
            "{\n" +
            "  \"formatVersion\": 1,\n" +
            "  \"id\": \"50\",\n" +
            "  \"committedAtUtc\": \"2026-09-30T12:00:00Z\",\n" +
            "  \"events\": [\n" +
            "    {\n" +
            "      \"type\": \"conversation.created\",\n" +
            "      \"version\": 1,\n" +
            "      \"data\": {\n" +
            "        \"conversationId\": \"00000000-0000-0000-0000-000000000001\",\n" +
            "        \"title\": \"FallbackTitle\",\n" +
            "        \"config\": {\n" +
            "          \"provider\": \"anthropic\",\n" +
            "          \"model\": \"claude-3-opus\",\n" +
            "          \"temperature\": 0.8,\n" +
            "          \"tools\": [\"builtin:clock\"]\n" +
            "        }\n" +
            "      }\n" +
            "    }\n" +
            "  ]\n" +
            "}"
        match CommitCodec.tryCommitFromJsonLine jsonWithOptional with
        | Some (dec: Events.Commit) ->
            Assert.Equal(50UL, dec.id)
            match dec.events.Head with
            | ConversationCreated ev ->
                Assert.Equal("anthropic", ev.config.provider)
                Assert.Equal(Some 0.8, ev.config.temperature)
            | _ -> Assert.Fail("Expected ConversationCreated")
        | None -> Assert.Fail("Failed decode with optional config")

    // =========================================================================
    // 4. Projection.fs 状态投影与提交应用测试
    // =========================================================================

    [<Fact>]
    let ``applyCommit rejects out of order commit id`` () =
        let commit: Events.Commit =
            { formatVersion = Constants.FormatVersion
              id = 5UL
              bootId = Constants.DefaultBootId
              source = Constants.DefaultSource
              committedAtUtc = DateTimeOffset.UtcNow
              commandId = None
              commandType = None
              commandHash = None
              events = [] }
        // Projection.empty.latestCommitId = 0, next expected = 1, giving 5 should fail
        match Projection.applyCommit Projection.empty commit with
        | Error (ValidationError msg) -> Assert.Contains("out of order", msg)
        | r -> Assert.Fail(sprintf "Expected Poisoned discontinuous commit id, got %A" r)

    [<Fact>]
    let ``applyCommit handles duplicate and invalid state transitions`` () =
        let convId = Guid.NewGuid()
        let c1: Events.Commit =
            { formatVersion = Constants.FormatVersion
              id = 1UL
              bootId = Constants.DefaultBootId
              source = Constants.DefaultSource
              committedAtUtc = DateTimeOffset.UtcNow
              commandId = None
              commandType = None
              commandHash = None
              events = [ ConversationCreated { conversationId = convId; title = "Initial Title"; config = dummyConfig } ] }

        let proj1 =
            match Projection.applyCommit Projection.empty c1 with
            | Ok p -> p
            | Error e -> failwithf "Failed to apply c1: %A" e

        Assert.Equal(1UL, proj1.latestCommitId)
        Assert.True(proj1.conversations.ContainsKey convId)

        // 1. 重复 ConversationCreated -> Poisoned
        let c2: Events.Commit =
            { formatVersion = Constants.FormatVersion
              id = 2UL
              bootId = Constants.DefaultBootId
              source = Constants.DefaultSource
              committedAtUtc = DateTimeOffset.UtcNow
              commandId = None
              commandType = None
              commandHash = None
              events = [ ConversationCreated { conversationId = convId; title = "Duplicate"; config = dummyConfig } ] }
        match Projection.applyCommit proj1 c2 with
        | Error (ValidationError msg) -> Assert.Contains("duplicate conversation", msg)
        | _ -> Assert.Fail("Expected Poisoned duplicate conversation")

        // 2. 对不存在的对话执行重命名 -> Poisoned
        let c3: Events.Commit =
            { formatVersion = Constants.FormatVersion
              id = 2UL
              bootId = Constants.DefaultBootId
              source = Constants.DefaultSource
              committedAtUtc = DateTimeOffset.UtcNow
              commandId = None
              commandType = None
              commandHash = None
              events = [ ConversationRenamed { conversationId = Guid.NewGuid(); title = "NonExistent" } ] }
        match Projection.applyCommit proj1 c3 with
        | Error (ValidationError msg) -> Assert.Contains("not found", msg)
        | _ -> Assert.Fail("Expected Poisoned not found")

        // 3. 对不存在的消息执行删除 -> Poisoned
        let c4: Events.Commit =
            { formatVersion = Constants.FormatVersion
              id = 2UL
              bootId = Constants.DefaultBootId
              source = Constants.DefaultSource
              committedAtUtc = DateTimeOffset.UtcNow
              commandId = None
              commandType = None
              commandHash = None
              events = [ MessageDeleted { conversationId = convId; messageCommitId = 999UL } ] }
        match Projection.applyCommit proj1 c4 with
        | Error (ValidationError msg) -> Assert.Contains("not found", msg)
        | _ -> Assert.Fail("Expected Poisoned message not found")

    [<Fact>]
    let ``conversationList sorts pinned conversations first`` () =
        let cNormalId = Guid.NewGuid()
        let cPinnedId = Guid.NewGuid()
        let t0 = DateTimeOffset.UtcNow
        let convNormal =
            { createTestConv cNormalId "Normal" with
                lastActivityAtUtc = t0.AddMinutes 10.0
                pinned = false }
        let convPinned =
            { createTestConv cPinnedId "Pinned" with
                lastActivityAtUtc = t0.AddMinutes 1.0
                pinned = true }

        let proj =
            { Projection.empty with
                conversations = Map.ofList [ (cNormalId, convNormal); (cPinnedId, convPinned) ] }

        let sorted = Projection.conversationList proj
        Assert.Equal(2, sorted.Length)
        Assert.Equal(cPinnedId, sorted[0].conversationId)
        Assert.Equal(cNormalId, sorted[1].conversationId)
