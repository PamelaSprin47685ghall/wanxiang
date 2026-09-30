module Wanxiang.Tests.ReplayTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

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
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, CommitCodec.commitToJsonLine commit1 + "\n")
        File.AppendAllText(path, CommitCodec.commitToJsonLine commit2 + "\n")

        match Replay.replay dir false with
        | Error e -> failwith e
        | Ok outcome ->
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
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, CommitCodec.commitToJsonLine c1 + "\n")
        File.AppendAllText(path, """{"formatVersion":1,"id":2,"broken""")
        match Replay.replay dir true with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(1UL, outcome.lastCommitId)
            Assert.True(outcome.truncatedFiles |> List.exists (fun f -> f = path))
            // 文件已被截断：重新 replay 应无损坏
            match Replay.replay dir false with
            | Ok outcome2 -> Assert.Equal(1UL, outcome2.lastCommitId)
            | Error e -> failwith e
    finally
        cleanup dir

[<Fact>]
let ``id 空洞触发截尾`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c3 = Events.Commit.create 3UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "C"; config = testConfig () } ]
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, CommitCodec.commitToJsonLine c1 + "\n")
        File.AppendAllText(path, CommitCodec.commitToJsonLine c3 + "\n")
        match Replay.replay dir true with
        | Error e -> failwith e
        | Ok outcome -> Assert.Equal(1UL, outcome.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``跨 UTC 日期文件按序重放且 id 不归零`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day1 = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let day2 = DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day1)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL (DateTimeOffset(day2)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
        let p1 = DataPaths.eventFilePath dir day1
        let p2 = DataPaths.eventFilePath dir day2
        Directory.CreateDirectory(Path.GetDirectoryName p1) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName p2) |> ignore
        File.AppendAllText(p1, CommitCodec.commitToJsonLine c1 + "\n")
        File.AppendAllText(p2, CommitCodec.commitToJsonLine c2 + "\n")
        match Replay.replay dir false with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(2UL, outcome.lastCommitId)
            Assert.Equal(2, Projection.conversationList outcome.projection |> List.length)
    finally
        cleanup dir

[<Fact>]
let ``doctor 只读模式不修改损坏日志`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, CommitCodec.commitToJsonLine c1 + "\n")
        File.AppendAllText(path, "garbage\n")
        match Replay.replay dir false with
        | Ok _ -> failwith "should have reported damage"
        | Error e -> Assert.Contains("damaged", e)
        // 文件未被修改
        Assert.Contains("garbage", File.ReadAllText path)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_nonexistent_events_dir`` () =
    let dir = Path.Combine(Path.GetTempPath(), "wanxiang-nonexistent-" + Guid.NewGuid().ToString("N"))
    try
        match Replay.replay dir false with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(0UL, outcome.lastCommitId)
            Assert.Empty outcome.commits
            Assert.Empty outcome.truncatedFiles
    finally
        cleanup dir

[<Fact>]
let ``test_replay_empty_events_dir`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        match Replay.replay dir false with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(0UL, outcome.lastCommitId)
            Assert.Empty outcome.commits
    finally
        cleanup dir

[<Fact>]
let ``test_replay_zero_byte_event_file`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllBytes(path, [||])
        match Replay.replay dir false with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(0UL, outcome.lastCommitId)
            Assert.Empty outcome.commits
    finally
        cleanup dir

[<Fact>]
let ``test_replay_unparseable_filename_fix_false`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let badPath = Path.Combine(DataPaths.eventsDir dir, "not-a-date.jsonseq")
        File.WriteAllText(badPath, "some content")
        match Replay.replay dir false with
        | Ok _ -> failwith "expected unparseable file name error"
        | Error e -> Assert.Contains("damaged log at", e)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_unparseable_filename_fix_true_deletes_file_and_later`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let badPath = Path.Combine(DataPaths.eventsDir dir, "2026-08-01-bad.jsonseq")
        let laterDay = DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)
        let laterPath = DataPaths.eventFilePath dir laterDay
        File.WriteAllText(badPath, "invalid")
        File.WriteAllText(laterPath, "later")
        match Replay.replay dir true with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.False(File.Exists badPath)
            Assert.False(File.Exists laterPath)
            Assert.Contains(badPath, outcome.truncatedFiles)
            Assert.Contains(laterPath, outcome.truncatedFiles)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_projection_apply_error_fix_false`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        // commit 1 删除了一个根本不存在的会话 -> Projection.applyCommit 会返回 Error
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ EventData.ConversationDeleted { conversationId = Guid.NewGuid() } ]
        let line = CommitCodec.commitToJsonLine c1
        let frame = Wanxiang.Core.Ledger.Frame.encode line
        File.WriteAllBytes(path, frame)
        match Replay.replay dir false with
        | Ok _ -> failwith "expected projection apply failure"
        | Error e -> Assert.Contains("damaged log at", e)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_projection_apply_error_fix_true_truncates_at_commit`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let convId = newConversationId ()
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL (DateTimeOffset(day)) [ EventData.ConversationDeleted { conversationId = Guid.NewGuid() } ] // 不存在，apply 失败
        let frame1 = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)
        let frame2 = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c2)
        File.WriteAllBytes(path, Array.append frame1 frame2)

        match Replay.replay dir true with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(1UL, outcome.lastCommitId)
            Assert.Single outcome.commits |> ignore
            Assert.Contains(path, outcome.truncatedFiles)
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
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c3 = Events.Commit.create 3UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ] // 缺少 2
        let frame1 = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)
        let frame3 = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c3)
        File.WriteAllBytes(path, Array.append frame1 frame3)

        match Replay.replay dir false with
        | Ok _ -> failwith "expected commit id gap error"
        | Error e -> Assert.Contains("damaged log at", e)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_commit_id_gap_fix_true_truncates_at_gap`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c3 = Events.Commit.create 3UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
        let frame1 = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)
        let frame3 = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c3)
        File.WriteAllBytes(path, Array.append frame1 frame3)

        match Replay.replay dir true with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(1UL, outcome.lastCommitId)
            Assert.Single outcome.commits |> ignore
            Assert.Equal(int64 frame1.Length, FileInfo(path).Length)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_consecutive_rs_and_whitespace_records`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let json1 = CommitCodec.commitToJsonLine c1
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

        match Replay.replay dir false with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(1UL, outcome.lastCommitId)
            Assert.Single outcome.commits |> ignore
    finally
        cleanup dir

[<Fact>]
let ``test_replay_crlf_record_ending`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "CRLF"; config = testConfig () } ]
        let json1 = CommitCodec.commitToJsonLine c1
        // 构造带 \r\n 的记录：RS + json + \r\n
        use ms = new MemoryStream()
        ms.WriteByte(0x1Euy)
        let payload = System.Text.Encoding.UTF8.GetBytes(json1 + "\r\n")
        ms.Write(payload, 0, payload.Length)
        File.WriteAllBytes(path, ms.ToArray())

        match Replay.replay dir false with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(1UL, outcome.lastCommitId)
            Assert.Single outcome.commits |> ignore
    finally
        cleanup dir

[<Fact>]
let ``test_replay_record_without_lf_at_eof_parsed`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "NoLF"; config = testConfig () } ]
        let json1 = CommitCodec.commitToJsonLine c1
        // 末尾没有 \n：RS + json (无换行)
        use ms = new MemoryStream()
        ms.WriteByte(0x1Euy)
        let payload = System.Text.Encoding.UTF8.GetBytes(json1)
        ms.Write(payload, 0, payload.Length)
        File.WriteAllBytes(path, ms.ToArray())

        match Replay.replay dir false with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(1UL, outcome.lastCommitId)
            Assert.Single outcome.commits |> ignore
    finally
        cleanup dir

[<Fact>]
let ``test_replay_damage_at_offset_zero_deletes_file_in_fix`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        // 第一条记录就是坏 JSON，offset 0L 损坏
        File.WriteAllBytes(path, [| 0x1Euy; byte '{'; byte 'x'; byte '\n' |])
        match Replay.replay dir true with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(0UL, outcome.lastCommitId)
            Assert.Equal(0L, FileInfo(path).Length)
            Assert.Contains(path, outcome.truncatedFiles)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_fix_deletes_damaged_file_and_cascades_to_all_later_date_files`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day1 = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let day2 = DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)
        let day3 = DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc)
        let p1 = DataPaths.eventFilePath dir day1
        let p2 = DataPaths.eventFilePath dir day2
        let p3 = DataPaths.eventFilePath dir day3
        Directory.CreateDirectory(Path.GetDirectoryName p1) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day1)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL (DateTimeOffset(day2)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
        let c3 = Events.Commit.create 3UL (DateTimeOffset(day3)) [ ConversationCreated { conversationId = newConversationId (); title = "C"; config = testConfig () } ]
        File.WriteAllBytes(p1, Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1))
        let frame2 = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c2)
        let badFrame = [| 0x1Euy; byte '{'; byte 'b'; byte 'a'; byte 'd'; byte '\n' |]
        File.WriteAllBytes(p2, Array.append frame2 badFrame)
        File.WriteAllBytes(p3, Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c3))
        match Replay.replay dir true with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(2UL, outcome.lastCommitId)
            Assert.Contains(p2, outcome.truncatedFiles)
            Assert.Equal(int64 frame2.Length, FileInfo(p2).Length)
            Assert.Contains(p3, outcome.truncatedFiles)
            Assert.False(File.Exists p3)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_record_without_lf_followed_immediately_by_new_rs_triggers_truncation`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let p = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let frame1 = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)
        use ms = new MemoryStream()
        ms.Write(frame1, 0, frame1.Length)
        ms.WriteByte 0x1Euy
        let badBytes = System.Text.Encoding.UTF8.GetBytes "{\"incomplete\":"
        ms.Write(badBytes, 0, badBytes.Length)
        ms.WriteByte 0x1Euy
        let extra = System.Text.Encoding.UTF8.GetBytes "{\"extra\":true}\n"
        ms.Write(extra, 0, extra.Length)
        File.WriteAllBytes(p, ms.ToArray())
        match Replay.replay dir true with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(1UL, outcome.lastCommitId)
            Assert.Contains(p, outcome.truncatedFiles)
            Assert.Equal(int64 frame1.Length, FileInfo(p).Length)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_truncation_failure_returns_error`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let p = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
        // 损坏记录
        File.WriteAllBytes(p, [| 0x1Euy; byte '{'; byte 'b'; byte 'a'; byte 'd'; byte '\n' |])
        // POSIX / Linux 语义：FileShare.Read 不阻止同用户写打开，因此用文件系统只读权限模拟截尾失败。
        // scanFile 以只读方式打开能正常扫描到损坏点；但 truncateFile 以 FileAccess.Write 打开时
        // 必因缺少写权限抛出 UnauthorizedAccessException，走 truncateFile 的 with e -> false 分支！
        try File.SetUnixFileMode(p, UnixFileMode.UserRead) with _ -> ()
        try
            match Replay.replay dir true with
            | Ok _ -> failwith "expected error because file truncation should fail"
            | Error e ->
                Assert.Contains("and truncation failed", e)
        finally
            // 恢复写权限，确保 cleanup 能顺利删除临时目录与文件
            try File.SetUnixFileMode(p, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
    finally
        cleanup dir
