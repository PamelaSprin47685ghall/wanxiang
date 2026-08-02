module Wanxiang.Tests.StoreConsistencyTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

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
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
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
        let outcome2 = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        Assert.Equal(1UL, outcome2.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``单写者内部幂等：同 commandId 不同载荷返回冲突`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
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
        | TruncatedAndReused (_, CommandIdConflict _) -> ()
        | r -> failwithf "expected conflict, got %A" r
        coord.Shutdown()
    finally
        cleanup dir

[<Fact>]
let ``排队消息重试窗口：DrainQueue 清空后同 invocationId 不重复提交`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        let committed = ResizeArray<Events.Commit>()
        use coord = new CommitCoordinator(dir, outcome, committed.Add, (fun _ -> ()))
        let convId = newConversationId ()
        // 创建会话
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
let ``停机跨天（中间日期无日志）正常恢复不损坏`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day1 = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let day3 = DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc)
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day1)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL (DateTimeOffset(day3)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
        let p1 = DataPaths.eventFilePath dir day1
        let p3 = DataPaths.eventFilePath dir day3
        Directory.CreateDirectory(Path.GetDirectoryName p1) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName p3) |> ignore
        File.AppendAllText(p1, CommitCodec.commitToJsonLine c1 + "\n")
        File.AppendAllText(p3, CommitCodec.commitToJsonLine c2 + "\n")
        // id 连续（1,2）且日期不降序：08-02 无日志是正常停机，不应报告损坏
        match Replay.replay dir false with
        | Ok outcome ->
            Assert.Equal(2UL, outcome.lastCommitId)
            Assert.Equal(2, Projection.conversationList outcome.projection |> List.length)
        | Error e -> failwith e
        // fix 模式同样不删除任何文件
        match Replay.replay dir true with
        | Ok outcome ->
            Assert.Equal(2UL, outcome.lastCommitId)
            Assert.Empty outcome.truncatedFiles
            Assert.True(File.Exists p1)
            Assert.True(File.Exists p3)
        | Error e -> failwith e
    finally
        cleanup dir

[<Fact>]
let ``CRLF 行截尾偏移精确：损坏行前完整行不被截坏`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        // 第一行用 \r\n 结尾（模拟外部编辑），第二行损坏
        File.WriteAllText(path, CommitCodec.commitToJsonLine c1 + "\r\n")
        File.AppendAllText(path, "broken-line-without-newline")
        match Replay.replay dir true with
        | Ok outcome ->
            Assert.Equal(1UL, outcome.lastCommitId)
            Assert.Contains(path, outcome.truncatedFiles)
        | Error e -> failwith e
        // 修复后重新 replay：第一条仍是完整有效行
        match Replay.replay dir false with
        | Ok outcome2 -> Assert.Equal(1UL, outcome2.lastCommitId)
        | Error e -> failwith e
        // 文件必须以 \n 结尾（截尾落在 \r\n 之后）
        let bytes = File.ReadAllBytes path
        Assert.True(bytes.Length > 0 && bytes[bytes.Length - 1] = byte '\n')
    finally
        cleanup dir

[<Fact>]
let ``换日切换文件后继续写入且 id 连续`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coord = new CommitCoordinator(dir, outcome, (fun _ -> ()), (fun _ -> ()))
        let convId = newConversationId ()
        // 用相对日期（今天 23:59:59 → 明天 00:00:01，显式 UTC）而非写死日期，避免时钟回退与时区隐式转换误判
        let todayUtc = DateTimeOffset.UtcNow.Date
        let day1 = DateTimeOffset(todayUtc.Add(TimeSpan(23, 59, 59)), TimeSpan.Zero)
        let day2 = DateTimeOffset(todayUtc.AddDays(1.0).Add(TimeSpan(0, 0, 1)), TimeSpan.Zero)
        let r1 =
            coord.Submit
                { events = [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
                  commandId = None; commandType = None; commandHash = None; nowUtc = Some day1 }
        match r1 with
        | Committed c1 -> Assert.Equal(1UL, c1.id)
        | r -> failwithf "commit 1 failed: %A" r
        let r2 =
            coord.Submit
                { events = [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "跨日" } ]
                  commandId = None; commandType = None; commandHash = None; nowUtc = Some day2 }
        match r2 with
        | Committed c2 -> Assert.Equal(2UL, c2.id)
        | r -> failwithf "commit 2 failed: %A" r
        // 两个文件都存在
        Assert.True(File.Exists(DataPaths.eventFilePath dir todayUtc))
        Assert.True(File.Exists(DataPaths.eventFilePath dir (todayUtc.AddDays(1.0))))
        coord.Shutdown()
        // 重启重放
        let outcome2 = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        Assert.Equal(2UL, outcome2.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``canonical 数字规范化：1.50 与 1.5、1.0 与 1 等价`` () =
    let norm (s: string) = CanonicalJson.tryNormalize s |> Option.defaultValue ""
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
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "会话标题中文"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "你好，万象！这是中文消息。" } ]
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, CommitCodec.commitToJsonLine c1 + "\n")
        File.AppendAllText(path, CommitCodec.commitToJsonLine c2 + "\n")
        match Replay.replay dir false with
        | Ok outcome ->
            Assert.Equal(2UL, outcome.lastCommitId)
            let conv = Projection.conversationList outcome.projection |> List.head
            // 标题中文不丢失
            Assert.Equal("会话标题中文", conv.title)
            // 消息 payload 中文不丢失（读取 contents[0].text 字段）
            let msg = conv.messages.Head
            match msg.payloadJson with
            | :? System.Text.Json.Nodes.JsonObject as o ->
                let contents = (o["contents"]).AsArray()
                let text = (contents[0]["text"]).GetValue<string>()
                Assert.Equal("你好，万象！这是中文消息。", text)
            | _ -> failwith "unexpected payload shape"
        | Error e -> failwith e
    finally
        cleanup dir

/// 回归：非法 UTF-8 字节序列应触发截尾（决策 8/10），而非静默 U+FFFD 入库。
[<Fact>]
let ``replay 遇非法 UTF-8 序列触发截尾`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, CommitCodec.commitToJsonLine c1 + "\n")
        // 行首是合法 JSON 起始，但行尾是非法 UTF-8 字节（0xFF 非起始字节）→ 整行严格解码失败
        // 用二进制写：合法的 JSON 字符串前缀 + 0xFF + 换行
        do
            use fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
            let prefix = Text.Encoding.UTF8.GetBytes "{\"formatVersion\":1,\"id\":2,\"events\":[]}\"x\""
            fs.Write(prefix, 0, prefix.Length)
            fs.WriteByte 0xFFuy
            fs.WriteByte 0xFEuy
            fs.WriteByte 10uy // \n
            fs.Flush()
        match Replay.replay dir false with
        | Ok _ -> failwith "should have reported invalid utf8"
        | Error e ->
            // 可能命中 unparseable（JSON 层）或 invalid UTF-8（解码层），两者都是"损坏→截尾"语义
            Assert.True(e.Contains("UTF-8") || e.Contains("unparseable"), e)
        // fix 模式截尾：保留 id=1
        match Replay.replay dir true with
        | Ok outcome ->
            Assert.Equal(1UL, outcome.lastCommitId)
            Assert.Contains(path, outcome.truncatedFiles)
        | Error e -> failwith e
    finally
        cleanup dir

/// 非法事件文件名：fix 时删除文件自身并保留后续文件（不级联误删；id 连续性兜底）。
[<Fact>]
let ``replay fix deletes invalid-named file but preserves later files`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        // "111-bad.ndjson" 字典序最前（'1' < '2'）→ 其后全部文件（含合法日期文件）都应被删除
        let badPath = Path.Combine(DataPaths.eventsDir dir, "111-bad.ndjson")
        let day1 = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day1)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let p1 = DataPaths.eventFilePath dir day1
        Directory.CreateDirectory(Path.GetDirectoryName p1) |> ignore
        File.WriteAllText(badPath, "garbage\n")
        File.AppendAllText(p1, CommitCodec.commitToJsonLine c1 + "\n")
        // fix 模式：删除非法文件自身（offset=0 无可保留）；后续文件保留（id 连续性检测兜底，不级联误删）
        match Replay.replay dir true with
        | Ok outcome ->
            // badPath 在最前，扫描 stop 早于 p1 → 本次投影为空
            Assert.Equal(0UL, outcome.lastCommitId)
            // 非法文件自身被删除（避免每次启动 fix 重复命中并写 stderr）
            Assert.False(File.Exists badPath)
            // 后续合法文件不被级联删除（安全：字典序下非法名可能排在合法文件之前）
            Assert.True(File.Exists p1)
        | Error e -> failwith e
        // 再次 replay：p1 完整重放（一次 fix 清干净，不重复命中）
        match Replay.replay dir false with
        | Ok outcome2 -> Assert.Equal(1UL, outcome2.lastCommitId)
        | Error e -> failwith e
    finally
        cleanup dir
