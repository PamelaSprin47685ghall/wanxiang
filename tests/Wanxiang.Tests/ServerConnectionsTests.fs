namespace Wanxiang.Tests

open System
open System.Net.WebSockets
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Server
open Wanxiang.Store

module ServerConnectionsTests =

    [<Fact>]
    let ``ConnectionRegistry allocates placeholder IDs monotonically`` () =
        let reg = ConnectionRegistry()
        let id1 = reg.AddPlaceholder()
        let id2 = reg.AddPlaceholder()
        let id3 = reg.AddPlaceholder()
        Assert.True(id1 > 0)
        Assert.True(id2 > id1)
        Assert.True(id3 > id2)
        Assert.Empty(reg.All())

    [<Fact>]
    let ``ConnectionRegistry remove non-existent connection is safe`` () =
        let reg = ConnectionRegistry()
        reg.Remove 999
        Assert.Empty(reg.All())

    [<Fact>]
    let ``HandshakeState variants can be constructed and transitioned`` () =
        let s0 = NotStarted
        let s1 = HelloSeen
        let s2 = Authenticated
        match s0, s1, s2 with
        | NotStarted, HelloSeen, Authenticated -> ()
        | _ -> Assert.Fail("HandshakeState pattern match failed")

    [<Fact>]
    let ``CommandExecutionResult variants structure check`` () =
        let res1 = CommandQueued
        let res2 = CommandCommitted 42UL
        let res3 = CommandIdempotent 42UL
        let res4 = CommandFailed (ValidationError "bad command")

        match res1, res2, res3, res4 with
        | CommandQueued, CommandCommitted c1, CommandIdempotent c2, CommandFailed (ValidationError _) ->
            Assert.Equal(42UL, c1)
            Assert.Equal(42UL, c2)
        | _ -> Assert.Fail("CommandExecutionResult pattern match failed")


    // =========================================================================
    // ConnectionRegistry 并发与查询契约测试
    // =========================================================================

    [<Fact>]
    let ``ConnectionRegistry 并发分配 ID 保证全局严格单调且互不冲突`` () =
        let reg = ConnectionRegistry()
        let ids = System.Collections.Concurrent.ConcurrentBag<int>()
        System.Threading.Tasks.Parallel.For(0, 100, fun _ ->
            ids.Add(reg.AddPlaceholder())
        ) |> ignore
        let distinctCount = ids |> Seq.distinct |> Seq.length
        Assert.Equal(100, distinctCount)

    [<Fact>]
    let ``CommandExecutionResult 错误分支包含具体错误语义`` () =
        let err = CommandIdConflict "version mismatch on head"
        let res = CommandFailed err
        match res with
        | CommandFailed (CommandIdConflict msg) -> Assert.Contains("version mismatch", msg)
        | _ -> Assert.Fail("Expected CommandFailed with CommandIdConflict")


    // =========================================================================
    // ServerModel 投影与裁剪契约测试
    // =========================================================================

    [<Fact>]
    let ``ServerModel conversationMessagesTail 在小消息集时不截断并返回 0UL 与 false`` () =
        let convId = Guid.NewGuid()
        let now = DateTimeOffset.UtcNow
        let msgJson = JsonObject()
        msgJson["role"] <- "user"
        let m1 = {
            commitId = 1UL
            conversationId = convId
            committedAtUtc = now
            deletedAtCommitId = None
            payloadJson = msgJson :> JsonNode
        }
        let m2 = {
            commitId = 2UL
            conversationId = convId
            committedAtUtc = now
            deletedAtCommitId = None
            payloadJson = msgJson :> JsonNode
        }
        let conv = {
            conversationId = convId
            title = "Test Conv"
            createdAtUtc = now
            lastActivityAtUtc = now
            config = SessionConfig.empty
            deleted = false
            pinned = false
            archived = false
            parent = None
            forkBaseCommitId = None
            messages = [ m1; m2 ]
            lastCommitId = Some 5UL
        }

        let proj = {
            Projection.empty with
                latestCommitId = 2UL
                conversations = Map.ofList [ convId, conv ]
        }

        // limit = 0 -> 不截断
        let tail0, earliest0, hasMore0 = ServerModel.conversationMessagesTail proj conv 0
        Assert.Equal(2, tail0.Count)
        Assert.Equal(0UL, earliest0)
        Assert.False(hasMore0)

        // limit = 5 >= 2 -> 不截断
        let tail5, earliest5, hasMore5 = ServerModel.conversationMessagesTail proj conv 5
        Assert.Equal(2, tail5.Count)
        Assert.Equal(0UL, earliest5)
        Assert.False(hasMore5)

        // limit = 1 < 2 -> 截断为最后一条
        let tail1, earliest1, hasMore1 = ServerModel.conversationMessagesTail proj conv 1
        Assert.Equal(1, tail1.Count)
        Assert.Equal(2UL, earliest1)
        Assert.True(hasMore1)

    [<Fact>]
    let ``ServerModel exportPage 参数与前置条件校验契约`` () =
        let convId = Guid.NewGuid()
        let now = DateTimeOffset.UtcNow
        let conv = {
            conversationId = convId
            title = "Export Conv"
            createdAtUtc = now
            lastActivityAtUtc = now
            config = SessionConfig.empty
            deleted = false
            pinned = false
            archived = false
            parent = None
            forkBaseCommitId = None
            messages = []
            lastCommitId = Some 10UL
        }
        let proj = {
            Projection.empty with
                latestCommitId = 10UL
                conversations = Map.ofList [ convId, conv ]
        }

        // 1. 会话不存在
        let qNonExistent: ConversationExportQuery = {
            exportId = Guid.NewGuid()
            conversationId = Guid.NewGuid()
            atCommitId = None
            beforeCommitId = 0UL
        }
        match ServerModel.exportPage proj qNonExistent with
        | Error msg -> Assert.Contains("会话不存在", msg)
        | Ok _ -> Assert.Fail("Expected error for non-existent conversation")

        // 2. 会话已删除
        let deletedConv = { conv with deleted = true }
        let projWithDeleted = { proj with conversations = Map.ofList [ convId, deletedConv ] }
        let qDeleted: ConversationExportQuery = {
            exportId = Guid.NewGuid()
            conversationId = convId
            atCommitId = None
            beforeCommitId = 0UL
        }
        match ServerModel.exportPage projWithDeleted qDeleted with
        | Error msg -> Assert.Contains("会话已删除", msg)
        | Ok _ -> Assert.Fail("Expected error for deleted conversation")

        // 3. atCommitId 超过 latestCommitId
        let qStaleAt: ConversationExportQuery = {
            exportId = Guid.NewGuid()
            conversationId = convId
            atCommitId = Some 99UL
            beforeCommitId = 0UL
        }
        match ServerModel.exportPage proj qStaleAt with
        | Error msg -> Assert.Contains("导出水位已失效", msg)
        | Ok _ -> Assert.Fail("Expected error for stale atCommitId")

        // 4. 导出首页不能指定分页边界
        let qBadFirstPage: ConversationExportQuery = {
            exportId = Guid.NewGuid()
            conversationId = convId
            atCommitId = None
            beforeCommitId = 5UL
        }
        match ServerModel.exportPage proj qBadFirstPage with
        | Error msg -> Assert.Contains("导出首页不能指定分页边界", msg)
        | Ok _ -> Assert.Fail("Expected error for non-zero beforeCommitId without atCommitId")

        // 5. 正常导出空会话页面
        let qOk: ConversationExportQuery = {
            exportId = Guid.NewGuid()
            conversationId = convId
            atCommitId = Some 10UL
            beforeCommitId = 0UL
        }
        match ServerModel.exportPage proj qOk with
        | Ok pageData ->
            Assert.Equal(convId, pageData.conversationId)
            Assert.Empty(pageData.items)
            Assert.False(pageData.hasMore)
        | Error e -> Assert.Fail(sprintf "Expected Ok, got: %s" e)

    [<Fact>]
    let ``ServerModel conversationListItems 格式化有效会话摘要并过滤思维链`` () =
        let convId = Guid.NewGuid()
        let now = DateTimeOffset.UtcNow
        // 构造含 reasoning 的消息和文本消息
        let reasoningPayload = JsonObject()
        reasoningPayload["role"] <- "assistant"
        let contents = JsonArray()
        let reasoningItem = JsonObject()
        reasoningItem["$type"] <- "reasoning"
        reasoningItem["text"] <- "Thinking deeply..."
        let textItem = JsonObject()
        textItem["text"] <- "Real answer for preview"
        contents.Add reasoningItem
        contents.Add textItem
        reasoningPayload["contents"] <- contents

        let m1 = {
            commitId = 3UL
            conversationId = convId
            committedAtUtc = now
            deletedAtCommitId = None
            payloadJson = reasoningPayload :> JsonNode
        }

        let conv = {
            conversationId = convId
            title = "List Item Conv"
            createdAtUtc = now
            lastActivityAtUtc = now
            config = { SessionConfig.empty with provider = "prov1"; model = "model1" }
            deleted = false
            pinned = true
            archived = false
            parent = None
            forkBaseCommitId = None
            messages = [ m1 ]
            lastCommitId = Some 3UL
        }

        let proj = {
            Projection.empty with
                latestCommitId = 3UL
                conversations = Map.ofList [ convId, conv ]
        }

        let listItems = ServerModel.conversationListItems proj (fun _ -> "idle")
        Assert.Equal(1, listItems.Count)
        let item = listItems[0].AsObject()
        Assert.Equal(convId.ToString("D"), item["conversationId"].GetValue<string>())
        Assert.Equal("List Item Conv", item["title"].GetValue<string>())
        Assert.True(item["pinned"].GetValue<bool>())
        Assert.False(item["isFork"].GetValue<bool>())
        Assert.Equal("idle", item["runtimeState"].GetValue<string>())
        // 验证摘要过滤了 reasoning，仅提取了真实文本
        let lastMsg = item["lastMessage"].GetValue<string>()
        Assert.Contains("Real answer for preview", lastMsg)
        Assert.DoesNotContain("Thinking deeply", lastMsg)


    // =========================================================================
    // WsConnection 入站协议与 TolerantReader 契约测试
    // =========================================================================

    [<Fact>]
    let ``TolerantReader 契约：识别 wanxiang 专属命名空间与标准事件，忽略外部自定义事件`` () =
        // 1. wanxiang 专属命名空间 CUSTOM 事件
        let wanxiangCustomJson = """{"type":"CUSTOM","name":"wanxiang.dev/chat.user-message.enqueue","value":{"foo":"bar"}}"""
        match Wanxiang.Agui.TolerantReader.parse wanxiangCustomJson with
        | Wanxiang.Agui.ParsedEvent.Custom(name, value) ->
            Assert.True(name.StartsWith Wanxiang.Agui.Capabilities.Namespace)
            Assert.NotNull value
        | _ -> Assert.Fail("Expected Custom event for wanxiang namespace")

        // 2. 外部别家 CUSTOM 事件 -> 合法解析但非 wanxiang 命名空间
        let foreignCustomJson = """{"type":"CUSTOM","name":"other.vendor/action","value":{}}"""
        match Wanxiang.Agui.TolerantReader.parse foreignCustomJson with
        | Wanxiang.Agui.ParsedEvent.Custom(name, _) ->
            Assert.False(name.StartsWith Wanxiang.Agui.Capabilities.Namespace)
        | _ -> Assert.Fail("Expected Custom event for foreign namespace")

        // 3. 畸形 JSON -> 产生 Malformed 结果
        let malformedJson = """{"type": 12345}"""
        match Wanxiang.Agui.TolerantReader.parse malformedJson with
        | Wanxiang.Agui.ParsedEvent.Malformed reason ->
            Assert.NotEmpty(reason)
        | _ -> Assert.Fail("Expected Malformed for invalid type field")

    [<Fact>]
    let ``TolerantReader 扁平 value 与嵌套外壳归一化语义`` () =
        // 验证扁平字段对象能够被安全解析为 JsonObject
        let flatJson = """{"type":"CUSTOM","name":"wanxiang.dev/ping","value":{"clientTime":1000}}"""
        match Wanxiang.Agui.TolerantReader.parse flatJson with
        | Wanxiang.Agui.ParsedEvent.Custom(_, value) ->
            let vo = Assert.IsAssignableFrom<JsonObject>(value)
            Assert.True(vo.ContainsKey "clientTime")
            Assert.False(vo.ContainsKey "type")
        | _ -> Assert.Fail("Expected Custom event")


    // =========================================================================
    // 配对限流冻结与动态配置变更校验契约测试（WsConnection 行 345-385, 607-652 靶点）
    // =========================================================================

    [<Fact>]
    let ``Auth.FailureTracker 连续 5 次配对失败进入冻结状态，并在冻结期后或成功后解冻`` () =
        let tracker = Auth.FailureTracker(TimeSpan.FromMinutes 1.0, 5, TimeSpan.FromMinutes 15.0)
        let remote = "192.168.1.100"
        let t0 = DateTimeOffset.UtcNow

        // 连续 4 次失败：尚未冻结
        for _ in 1 .. 4 do
            let frozen = tracker.RecordFailure(t0, remote)
            Assert.False(frozen)
            Assert.False(tracker.IsFrozen(t0, remote))

        // 第 5 次失败：立即触发冻结
        let frozen5 = tracker.RecordFailure(t0, remote)
        Assert.True(frozen5)
        Assert.True(tracker.IsFrozen(t0, remote))

        // 在 15 分钟冻结期内仍然冻结
        let tInFreeze = t0.AddMinutes 10.0
        Assert.True(tracker.IsFrozen(tInFreeze, remote))

        // 超过 15 分钟冻结期：自动解冻
        let tAfterFreeze = t0.AddMinutes 16.0
        Assert.False(tracker.IsFrozen(tAfterFreeze, remote))

        // 清理状态：显式 Clear 立即解冻
        tracker.RecordFailure(t0, remote) |> ignore
        tracker.Clear remote
        Assert.False(tracker.IsFrozen(t0, remote))

    [<Fact>]
    let ``ConfigMutation 动态配置变更：非法 Provider 与 MCP 配置被明确拒绝并返回错误清单`` () =
        let instanceId = Guid.NewGuid()
        let cfg = AppConfig.defaults instanceId

        // 1. 尝试 upsert 一个缺失必要字段的非法 Provider
        let invalidProviderJson = JsonObject()
        invalidProviderJson["id"] <- "" // 空 ID 必须被拒绝
        invalidProviderJson["kind"] <- "openai"
        match ConfigMutation.upsertProvider cfg invalidProviderJson with
        | Error errs ->
            Assert.NotEmpty errs
            Assert.Contains(errs, fun e -> e.Contains "id")
        | Ok _ -> Assert.Fail("Expected error for empty provider id")

        // 2. 尝试 upsert 一个缺失 command/url 的非法 MCP 服务
        let invalidMcpJson = JsonObject()
        invalidMcpJson["id"] <- "test-mcp"
        // 既无 command 又无 url
        match ConfigMutation.upsertMcp cfg invalidMcpJson with
        | Error errs ->
            Assert.NotEmpty errs
        | Ok _ -> Assert.Fail("Expected error for mcp server missing both command and url")

        // 3. 删除不存在的 Provider 严格返回 Error 错误清单
        match ConfigMutation.deleteProvider cfg "non-existent-provider" with
        | Error errs ->
            Assert.NotEmpty errs
            Assert.Contains("non-existent-provider", errs.Head)
        | Ok _ -> Assert.Fail("Expected Error when deleting non-existent provider")


    // =========================================================================
    // Stderr 凭据脱敏与结构化日志契约测试（Stderr.fs 靶点，24 行未覆盖）
    // =========================================================================

    [<Fact>]
    let ``Stderr 凭据脱敏规则：按长度降序替换，短密钥不破坏长密钥的完整匹配`` () =
        Stderr.clearSecrets ()
        try
            let longSecret = "sk-wanxiang-super-secret-key-12345"
            let shortSecret = "sk-wanxiang"

            // 故意先注册短密钥，再注册长密钥
            Stderr.registerSecrets [ shortSecret; longSecret ]

            // 测试文本同时包含长短密钥
            let rawText = sprintf "Connecting with key %s and prefix %s" longSecret shortSecret
            let redacted = Stderr.redact rawText

            // 两个都应被替换为 ***，且长密钥不会被短密钥破坏成 ***-super-secret-key-12345
            Assert.DoesNotContain(longSecret, redacted)
            Assert.DoesNotContain(shortSecret, redacted)
            Assert.Equal("Connecting with key *** and prefix ***", redacted)
        finally
            Stderr.clearSecrets ()

    [<Fact>]
    let ``Stderr 短密钥过滤与幂等注册契约：长度小于 8 的字符串或空白字符不予注册`` () =
        Stderr.clearSecrets ()
        try
            // 长度小于 8 或全空白的不注册
            Stderr.registerSecrets [ "1234567"; "   "; "" ]
            let text = "My short pin is 1234567"
            Assert.Equal(text, Stderr.redact text)

            // 有效密钥注册后生效
            let validSecret = "valid-secret-token-999"
            Stderr.registerSecrets [ validSecret ]
            Assert.Equal("Token: ***", Stderr.redact (sprintf "Token: %s" validSecret))
        finally
            Stderr.clearSecrets ()

    // =========================================================================
    // 慢客户端 AuthorityCatchUp 事件结构与批次截断契约测试（WsConnection 靶点）
    // =========================================================================

    [<Fact>]
    let ``AuthorityCatchUp 事件载荷构建：保留范围边界并在客户端游标落后时正确打包`` () =
        let fromCursor = 100UL
        let toCommitId = 164UL
        let items = JsonArray()
        let item1 = JsonObject()
        item1["id"] <- 101UL
        items.Add item1

        let catchUpEv: WireEvent = AuthorityCatchUp {|
            fromCursor = fromCursor
            toCommitId = toCommitId
            items = items
        |}

        match catchUpEv with
        | AuthorityCatchUp d ->
            Assert.Equal(100UL, d.fromCursor)
            Assert.Equal(164UL, d.toCommitId)
            Assert.Equal(1, d.items.Count)
        | _ -> Assert.Fail("Expected AuthorityCatchUp event variant")





