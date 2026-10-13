module Wanxiang.Tests.ReplayTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// 断言 inspect 返回 Healthy 并取出 outcome。
let private healthy (dir: string) : ReplayOutcome =
    match Replay.inspect dir with
    | Healthy o -> o
    | RepairRequired _ -> failwith "expected healthy"
    | UnsupportedFormat msg -> failwith msg
    | Unreadable msg -> failwith msg

/// 断言 inspect 返回 RepairRequired 并取出 (outcome, plan)。
let private repairRequired (dir: string) : ReplayOutcome * RepairPlan =
    match Replay.inspect dir with
    | Healthy _ -> failwith "expected repair required"
    | RepairRequired (o, p) -> o, p
    | UnsupportedFormat msg -> failwith msg
    | Unreadable msg -> failwith msg

[<Fact>]
let ``test_replay_tail_truncate`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        // 手工写两条提交
        let commit1 = Events.Commit.create 1UL (DateTimeOffset.UtcNow) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let convId =
            match commit1.events.Head with
            | ConversationCreated d -> d.conversationId
            | _ -> Guid.Empty
        let commit2 = Events.Commit.create 2UL (DateTimeOffset.UtcNow) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "你好" } ]
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllBytes(path, Wanxiang.Store.Assembly.frame commit1)
        File.AppendAllBytes(path, Wanxiang.Store.Assembly.frame commit2)

        let outcome = healthy dir
        Assert.Equal(2UL, outcome.lastCommitId)
        Assert.Equal(1, Projection.conversationList outcome.projection |> List.length)
        let conv = Projection.conversationList outcome.projection |> List.head
        Assert.Equal(1, conv.messages.Length)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_id_gap`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllBytes(path, Wanxiang.Store.Assembly.frame c1)
        File.AppendAllText(path, "\u001e" + """{"formatVersion":1,"id":2,"broken""")
        let outcome, plan = repairRequired dir
        Assert.Equal(1UL, outcome.lastCommitId)
        Assert.True(plan.affectedFiles |> List.exists (fun f -> f = path))
        Assert.Contains(path, plan.damage.file)
        // 只读检查绝不修改文件：原文件字节不变
        Assert.Contains("broken", File.ReadAllText path)
        // 显式修复：截尾到有效前缀
        match Repair.repair dir plan with
        | Error e -> failwith e
        | Ok truncated ->
            Assert.Contains(path, truncated)
        // 修复后重新检查应健康
        let outcome2 = healthy dir
        Assert.Equal(1UL, outcome2.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``id 空洞触发截尾`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c3 = Events.Commit.create 3UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "C"; config = testConfig () } ]
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllBytes(path, Wanxiang.Store.Assembly.frame c1)
        File.AppendAllBytes(path, Wanxiang.Store.Assembly.frame c3)
        let outcome, _ = repairRequired dir
        Assert.Equal(1UL, outcome.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``doctor 只读模式不修改损坏日志`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllBytes(path, Wanxiang.Store.Assembly.frame c1)
        File.AppendAllText(path, "\u001e" + "garbage\n")
        let _, plan = repairRequired dir
        // 文件未被修改
        Assert.Contains("garbage", File.ReadAllText path)
        // 损坏点定位
        Assert.Contains(path, plan.damage.file)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_nonexistent_events_dir`` () =
    let dir = Path.Combine(Path.GetTempPath(), "wanxiang-nonexistent-" + Guid.NewGuid().ToString("N"))
    try
        let outcome = healthy dir
        Assert.Equal(0UL, outcome.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_empty_events_dir`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = healthy dir
        Assert.Equal(0UL, outcome.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_zero_byte_event_file`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllBytes(path, [||])
        let outcome = healthy dir
        Assert.Equal(0UL, outcome.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_projection_apply_error_fix_false`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        // commit 1 删除了一个根本不存在的对话 -> Projection.applyCommit 会返回 Error
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ EventData.ConversationDeleted { conversationId = Guid.NewGuid() } ]
        let frame = Wanxiang.Store.Assembly.frame c1
        File.WriteAllBytes(path, frame)
        let _, plan = repairRequired dir
        Assert.Contains(path, plan.damage.file)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_projection_apply_error_fix_true_truncates_at_commit`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let convId = newConversationId ()
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL (DateTimeOffset(day)) [ EventData.ConversationDeleted { conversationId = Guid.NewGuid() } ] // 不存在，apply 失败
        let frame1 = Wanxiang.Store.Assembly.frame (c1)
        let frame2 = Wanxiang.Store.Assembly.frame (c2)
        File.WriteAllBytes(path, Array.append frame1 frame2)

        let outcome, plan = repairRequired dir
        Assert.Equal(1UL, outcome.lastCommitId)
        Assert.Contains(path, plan.damage.file)
        match Repair.repair dir plan with
        | Error e -> failwith e
        | Ok truncated ->
            Assert.Contains(path, truncated)
            // 文件长度被截断到 frame1 之后
            Assert.Equal(int64 frame1.Length, FileInfo(path).Length)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_commit_id_gap_fix_false`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c3 = Events.Commit.create 3UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ] // 缺少 2
        let frame1 = Wanxiang.Store.Assembly.frame (c1)
        let frame3 = Wanxiang.Store.Assembly.frame (c3)
        File.WriteAllBytes(path, Array.append frame1 frame3)

        let _, plan = repairRequired dir
        Assert.Contains(path, plan.damage.file)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_commit_id_gap_fix_true_truncates_at_gap`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c3 = Events.Commit.create 3UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
        let frame1 = Wanxiang.Store.Assembly.frame (c1)
        let frame3 = Wanxiang.Store.Assembly.frame (c3)
        File.WriteAllBytes(path, Array.append frame1 frame3)

        let outcome, plan = repairRequired dir
        Assert.Equal(1UL, outcome.lastCommitId)
        match Repair.repair dir plan with
        | Error e -> failwith e
        | Ok _ -> ()
        Assert.Equal(int64 frame1.Length, FileInfo(path).Length)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_whitespace_only_record_is_damage`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let json1 = Wanxiang.Store.Assembly.envelope c1
        // 构造：RS + RS + "   \n" + RS + json1 + "\n"
        use ms = new MemoryStream()
        ms.WriteByte(0x1Euy)
        ms.WriteByte(0x1Euy)
        let ws = System.Text.Encoding.UTF8.GetBytes("   \n")
        ms.Write(ws, 0, ws.Length)
        ms.WriteByte(0x1Euy)
        let payload = System.Text.Encoding.UTF8.GetBytes(json1 + "\n")
        ms.Write(payload, 0, payload.Length)
        File.WriteAllBytes(path, ms.ToArray())

        // 只含空白的记录不是 JSON 文本：账本报告损坏，而不是静默跳过（旧实现跳过）
        let outcome, _ = repairRequired dir
        Assert.Equal(0UL, outcome.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_crlf_record_ending`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "CRLF"; config = testConfig () } ]
        let json1 = Wanxiang.Store.Assembly.envelope c1
        // 构造带 \r\n 的记录：RS + json + \r\n
        use ms = new MemoryStream()
        ms.WriteByte(0x1Euy)
        let payload = System.Text.Encoding.UTF8.GetBytes(json1 + "\r\n")
        ms.Write(payload, 0, payload.Length)
        File.WriteAllBytes(path, ms.ToArray())

        let outcome = healthy dir
        Assert.Equal(1UL, outcome.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_record_without_lf_at_eof_is_torn_tail`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "NoLF"; config = testConfig () } ]
        let json1 = Wanxiang.Store.Assembly.envelope c1
        // 末尾没有 \n：RS + json (无换行)
        use ms = new MemoryStream()
        ms.WriteByte(0x1Euy)
        let payload = System.Text.Encoding.UTF8.GetBytes(json1)
        ms.Write(payload, 0, payload.Length)
        File.WriteAllBytes(path, ms.ToArray())

        // RFC 7464：缺 LF 的末尾记录就是被截断的记录，不能当作已提交（旧实现接受它）
        let outcome, plan = repairRequired dir
        Assert.Equal(0UL, outcome.lastCommitId)
        Assert.Equal(0L, plan.damage.byteOffset)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_record_without_lf_followed_immediately_by_new_rs_triggers_truncation`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let p = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let frame1 = Wanxiang.Store.Assembly.frame (c1)
        use ms = new MemoryStream()
        ms.Write(frame1, 0, frame1.Length)
        ms.WriteByte 0x1Euy
        let badBytes = System.Text.Encoding.UTF8.GetBytes "{\"incomplete\":"
        ms.Write(badBytes, 0, badBytes.Length)
        ms.WriteByte 0x1Euy
        let extra = System.Text.Encoding.UTF8.GetBytes "{\"extra\":true}\n"
        ms.Write(extra, 0, extra.Length)
        File.WriteAllBytes(p, ms.ToArray())
        let outcome, plan = repairRequired dir
        Assert.Equal(1UL, outcome.lastCommitId)
        match Repair.repair dir plan with
        | Error e -> failwith e
        | Ok truncated ->
            Assert.Contains(p, truncated)
            Assert.Equal(int64 frame1.Length, FileInfo(p).Length)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_truncation_failure_returns_error`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let p = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
        // 损坏记录
        File.WriteAllBytes(p, [| 0x1Euy; byte '{'; byte 'b'; byte 'a'; byte 'd'; byte '\n' |])
        // POSIX / Linux 语义：FileShare.Read 不阻止同用户写打开，因此用文件系统只读权限模拟截尾失败。
        // scanFile 以只读方式打开能正常扫描到损坏点；但 truncateFile 以 FileAccess.Write 打开时
        // 必因缺少写权限抛出 UnauthorizedAccessException，走 truncateFile 的 with e -> false 分支！
        try File.SetUnixFileMode(p, UnixFileMode.UserRead) with _ -> ()
        try
            let _, plan = repairRequired dir
            match Repair.repair dir plan with
            | Ok _ -> failwith "expected error because file truncation should fail"
            | Error e ->
                // O-004：截尾失败必须带文件路径 + 偏移 + 原因（不再吞异常）
                Assert.Contains("repair failed", e)
                Assert.Contains("log.jsonseq", e)
        finally
            // 恢复写权限，确保 cleanup 能顺利删除临时目录与文件
            try File.SetUnixFileMode(p, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
    finally
        cleanup dir

[<Fact>]
let ``inspect 绝不修改文件（含级联删除候选）`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day1 = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let day2 = DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)
        let p1 = DataPaths.logFile dir
        let p2 = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName p1) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day1)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL (DateTimeOffset(day2)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
        File.WriteAllBytes(p1, Wanxiang.Store.Assembly.frame (c1))
        File.WriteAllBytes(p2, Wanxiang.Store.Assembly.frame (c2))
        // 在 p2 尾部追加坏记录 → p2 是损坏点，p2 自身会被截尾
        File.AppendAllText(p2, "\u001e" + "garbage-no-lf")
        let bytesBefore1 = File.ReadAllBytes p1
        let bytesBefore2 = File.ReadAllBytes p2
        let _, plan = repairRequired dir
        Assert.Contains(p2, plan.damage.file)
        // 只读检查后文件字节完全不变
        Assert.Equal<byte[]>(bytesBefore1, File.ReadAllBytes p1)
        Assert.Equal<byte[]>(bytesBefore2, File.ReadAllBytes p2)
    finally
        cleanup dir

[<Fact>]
let ``recovery bundle 建立后修复才执行（算法 B）`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day1 = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let day2 = DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)
        let p1 = DataPaths.logFile dir
        let p2 = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName p1) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day1)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL (DateTimeOffset(day2)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
        File.WriteAllBytes(p1, Wanxiang.Store.Assembly.frame (c1))
        File.AppendAllBytes(p2, Wanxiang.Store.Assembly.frame (c2))
        File.AppendAllText(p2, "\u001e" + "garbage-no-lf")
        let _, plan = repairRequired dir
        // 建立证据包
        match RecoveryBundle.create dir plan with
        | Error e -> failwith e
        | Ok bundleDir ->
            Assert.True(File.Exists(Path.Combine(bundleDir, "manifest.json")))
            Assert.True(File.Exists(Path.Combine(bundleDir, "diagnosis.json")))
            // original/ 含全部受影响文件
            for f in plan.affectedFiles do
                Assert.True(File.Exists(Path.Combine(bundleDir, "original", Path.GetFileName f)), sprintf "missing backup of %s" f)
        // 修复
        match Repair.repair dir plan with
        | Error e -> failwith e
        | Ok truncated -> Assert.NotEmpty truncated
        // 修复后健康
        let outcome = healthy dir
        Assert.Equal(2UL, outcome.lastCommitId)
    finally
        cleanup dir
