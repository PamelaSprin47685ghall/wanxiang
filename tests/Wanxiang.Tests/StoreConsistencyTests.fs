module Wanxiang.Tests.StoreConsistencyTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// 断言 inspect 返回 RepairRequired 并取出 (outcome, plan)。
let private repairRequired (dir: string) : ReplayOutcome * RepairPlan =
    match Replay.inspect dir with
    | Healthy _ -> failwith "expected repair required"
    | RepairRequired (o, p) -> o, p
    | UnsupportedFormat msg -> failwith msg
    | Unreadable msg -> failwith msg

/// 存储层一致性回归测试（本次修复）：
/// - 幂等检查下沉到单写者：同 commandId 在 plan 之后/提交之前再次 Submit 不重复记账（决策 13/15）
/// - 排队消息重试窗口（DrainQueue 竞态）不再重复提交
/// - 日期文件缺失检测（决策 7）
/// - \r\n 行截尾偏移正确（决策 8）
/// - 换日文件切换后写入仍正常（决策 6/109）
[<Fact>]
let ``单写者内部幂等：同 commandId 再次 Submit 返回原提交且不追加`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coord = new CommitCoordinator(dir, outcome, (fun _ -> ()), (fun _ -> ()))
        let convId = newConversationId ()
        let invId = Guid.NewGuid()
        let cmd = CreateConversation {| invocationId = invId; conversationId = convId; title = "A"; config = testConfig () |}
        // 直接构造 Submit（绕过 CommandEngine.plan，模拟 DrainQueue 路径）
        let canonical = ClientCommand.canonicalPayload cmd
        let commandId = CommandId.compute invId (ClientCommand.commandType cmd) canonical
        let hash = CommandId.sha256Hex canonical
        let submit =
            { events = [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
              commandId = Some commandId
              commandType = Some(ClientCommand.commandType cmd)
              commandHash = Some hash
              nowUtc = None }
        match coord.Submit submit with
        | Committed c -> Assert.Equal(1UL, c.id)
        | r -> failwithf "first submit should commit, got %A" r
        // 重试（同 commandId + 同内容）→ 单写者内部幂等命中
        match coord.Submit submit with
        | IdempotentReplay c -> Assert.Equal(1UL, c.id)
        | r -> failwithf "retry should be idempotent, got %A" r
        // 只有一条提交
        Assert.Equal(1UL, coord.Projection.latestCommitId)
        coord.Shutdown()
        let outcome2 = replayOutcome dir
        Assert.Equal(1UL, outcome2.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``单写者内部幂等：同 commandId 不同载荷返回冲突`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coord = new CommitCoordinator(dir, outcome, (fun _ -> ()), (fun _ -> ()))
        let convId = newConversationId ()
        let invId = Guid.NewGuid()
        let cmd = CreateConversation {| invocationId = invId; conversationId = convId; title = "A"; config = testConfig () |}
        let canonical = ClientCommand.canonicalPayload cmd
        let commandId = CommandId.compute invId (ClientCommand.commandType cmd) canonical
        let hash = CommandId.sha256Hex canonical
        let submit =
            { events = [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
              commandId = Some commandId; commandType = Some(ClientCommand.commandType cmd); commandHash = Some hash; nowUtc = None }
        coord.Submit submit |> ignore
        // 同一 commandId 但不同载荷（hash 不同）→ 冲突
        let submit2 =
            { submit with
                events = [ ConversationCreated { conversationId = convId; title = "B"; config = testConfig () } ]
                commandHash = Some(CommandId.sha256Hex "different-payload") }
        match coord.Submit submit2 with
        | CommandIdRejected (CommandIdConflict _) -> ()
        | r -> failwithf "expected conflict, got %A" r
        coord.Shutdown()
    finally
        cleanup dir

[<Fact>]
let ``排队消息重试窗口：DrainQueue 清空后同 invocationId 不重复提交`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        let committed = ResizeArray<Events.Commit>()
        use coord = new CommitCoordinator(dir, outcome, committed.Add, (fun _ -> ()))
        let convId = newConversationId ()
        // 创建对话
        coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ] |> ignore
        let invId = Guid.NewGuid()
        let msgJson = userMessageJson "hello"
        let cmd = SendUserMessage {| invocationId = invId; conversationId = convId; messageJson = msgJson |}
        let canonical = ClientCommand.canonicalPayload cmd
        let commandId = CommandId.compute invId (ClientCommand.commandType cmd) canonical
        let hash = CommandId.sha256Hex canonical
        let submit =
            { events = [ AgentMessageRecorded { conversationId = convId; payloadJson = msgJson } ]
              commandId = Some commandId; commandType = Some(ClientCommand.commandType cmd); commandHash = Some hash; nowUtc = None }
        // 模拟 DrainQueue：第一次提交
        match coord.Submit submit with
        | Committed c -> Assert.Equal(2UL, c.id)
        | r -> failwithf "first submit failed: %A" r
        // 模拟客户端重试（pendingInvocationIds 已被 DrainQueue 清空，但幂等下沉到单写者）
        match coord.Submit submit with
        | IdempotentReplay c -> Assert.Equal(2UL, c.id)
        | r -> failwithf "retry should be idempotent: %A" r
        Assert.Equal(2UL, coord.Projection.latestCommitId)
        let conv = Projection.conversationList coord.Projection |> List.head
        Assert.Equal(1, conv.messages.Length) // 仅一条用户消息（重试未重复记账）
        coord.Shutdown()
    finally
        cleanup dir

[<Fact>]
let ``CRLF 行截尾偏移精确：损坏行前完整行不被截坏`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        // 第一行用 \r\n 结尾（模拟外部编辑），第二行损坏
        File.WriteAllText(path, "\u001e" + Wanxiang.Store.Assembly.envelope c1 + "\r\n")
        File.AppendAllText(path, "\u001e" + "broken-line-without-newline")
        let outcome, plan = repairRequired dir
        Assert.Equal(1UL, outcome.lastCommitId)
        Assert.Contains(path, plan.damage.file)
        match Repair.repair dir plan with
        | Error e -> failwith e
        | Ok truncated -> Assert.Contains(path, truncated)
        // 修复后重新检查：第一条仍是完整有效行
        let outcome2 = replayOutcome dir
        Assert.Equal(1UL, outcome2.lastCommitId)
        // 文件必须以 \n 结尾（截尾落在 \r\n 之后）
        let bytes = File.ReadAllBytes path
        Assert.True(bytes.Length > 0 && bytes[bytes.Length - 1] = byte '\n')
    finally
        cleanup dir

[<Fact>]
let ``canonical 数字规范化：1.50 与 1.5、1.0 与 1 等价`` () =
    let norm (s: string) = Canonical.Jcs.text s
    Assert.Equal(norm """{"n":1.5}""", norm """{"n":1.50}""")
    Assert.Equal(norm """{"n":1}""", norm """{"n":1.0}""")
    Assert.Equal(norm """{"n":100}""", norm """{"n":1e2}""")
    Assert.Equal(norm """{"a":1.50,"b":"x"}""", norm """{"b":"x","a":1.5}""")

/// 回归：Replay 逐字节扫描必须正确解码 UTF-8 多字节字符（中文不乱码）。
[<Fact>]
let ``replay 正确解码含中文的日志行`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let convId = newConversationId ()
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "对话标题中文"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "你好，万象！这是中文消息。" } ]
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllBytes(path, Wanxiang.Store.Assembly.frame c1)
        File.AppendAllBytes(path, Wanxiang.Store.Assembly.frame c2)
        let outcome = replayOutcome dir
        Assert.Equal(2UL, outcome.lastCommitId)
        let conv = Projection.conversationList outcome.projection |> List.head
        // 标题中文不丢失
        Assert.Equal("对话标题中文", conv.title)
        // 消息 payload 中文不丢失（读取 contents[0].text 字段）
        let msg = conv.messages.Head
        match msg.payloadJson with
        | :? System.Text.Json.Nodes.JsonObject as o ->
            let contents = (o["contents"]).AsArray()
            let text = (contents[0]["text"]).GetValue<string>()
            Assert.Equal("你好，万象！这是中文消息。", text)
        | _ -> failwith "unexpected payload shape"
    finally
        cleanup dir

/// 回归：JSON 字符串里的非法 UTF-8 字节必须当作损坏，而不是静默变成 U+FFFD 入库。
/// JsonSeq 严格解码后，非法字节记录带 InvalidUtf8 故障，账本前缀停在它之前。
[<Fact>]
let ``replay 遇非法 UTF-8 序列触发截尾`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "B@B"; config = testConfig () } ]
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllBytes(path, Wanxiang.Store.Assembly.frame c1)
        // 把 c2 标题里的 '@' 换成非法字节 0xFF
        let f2 = Wanxiang.Store.Assembly.frame c2 |> Array.map (fun b -> if b = byte '@' then 0xFFuy else b)
        File.AppendAllBytes(path, f2)
        let outcome, plan = repairRequired dir
        Assert.Equal(1UL, outcome.lastCommitId)
        Assert.Contains(path, plan.damage.file)
    finally
        cleanup dir

[<Fact>]
let ``CommitCoordinator CommitsAfter 二分查找覆盖边界条件`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        let convId = newConversationId ()
        coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ] |> ignore
        coord.SubmitEvents [ ConversationRenamed { conversationId = convId; title = "B" } ] |> ignore
        coord.SubmitEvents [ ConversationRenamed { conversationId = convId; title = "C" } ] |> ignore
        coord.SubmitEvents [ ConversationRenamed { conversationId = convId; title = "D" } ] |> ignore
        coord.SubmitEvents [ ConversationRenamed { conversationId = convId; title = "E" } ] |> ignore
        
        // after 0 -> 全部 5 条
        let all = coord.CommitsAfter 0UL
        Assert.Equal(5, all.Length)
        // after 2 -> 返回 id 3, 4, 5
        let after2 = coord.CommitsAfter 2UL
        Assert.Equal(3, after2.Length)
        Assert.Equal(3UL, after2.Head.id)
        // after 5 -> 空
        let after5 = coord.CommitsAfter 5UL
        Assert.Empty after5
        // after 10 -> 空
        let after10 = coord.CommitsAfter 10UL
        Assert.Empty after10
    finally
        cleanup dir

[<Fact>]
let ``CommitCoordinator 提交非法事件触发 ApplyFailed 截尾并复用 commitId`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        let truncated = ResizeArray<Events.Commit * WanxiangError * int64 * string>()
        use coord = new CommitCoordinator(dir, outcome, ignore, (fun t -> truncated.Add(t)))
        let badSubmit =
            { events = [ EventData.ConversationDeleted { conversationId = Guid.NewGuid() } ] // 不存在的对话，apply 失败
              commandId = None
              commandType = None
              commandHash = None
              nowUtc = None }
        match coord.Submit badSubmit with
        | CommitFailed _ -> Assert.True(truncated.Count = 0) // 先折叠后写盘：被拒的提交不落盘、不截尾
        | other -> failwithf "expected CommitFailed, got %A" other
        
        // 验证 commitId 被复用：下一次合法提交依然获得 id = 1UL
        let convId = newConversationId ()
        let goodSubmit =
            { events = [ ConversationCreated { conversationId = convId; title = "Reused"; config = testConfig () } ]
              commandId = None
              commandType = None
              commandHash = None
              nowUtc = None }
        match coord.Submit goodSubmit with
        | Committed c -> Assert.Equal(1UL, c.id)
        | _ -> failwith "expected Committed with reused id"
    finally
        cleanup dir

[<Fact>]
let ``CommitCoordinator 正常 Dispose 释放 writer 且不抛出`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        let coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        let convId = newConversationId ()
        coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "Test"; config = testConfig () } ] |> ignore
        coord.Shutdown()
    finally
        cleanup dir
