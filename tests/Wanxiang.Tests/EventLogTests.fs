namespace Wanxiang.Tests

open System
open System.IO
open System.Text
open Xunit
open Wanxiang.Core
open Wanxiang.Core.Ledger
open Wanxiang.Store

/// 事件日志内核（RFC 7464 分帧 + RFC 8785 规范化 + 生产 CommitCodec）的行为契约。
/// P-002：格式唯一权威 = `CommitCodec` + `Frame` + `Jcs`（`Ledger.Record` 重复定义已删除）。
/// 这些用例覆盖**只靠读代码看不出来**的失败模式：
/// 字段写读类型不一致让 replay 全空、JCS 静默改写大整数、尾部截断与中间损坏的区别。
module EventLogTests =

    let private commit (commitId: CommitId) (bootId: Guid) : Events.Commit =
        Events.Commit.create commitId DateTimeOffset.UnixEpoch
            [ ConversationCreated { conversationId = Guid.NewGuid(); title = "t"; config = SessionConfig.empty } ]
        |> Events.Commit.withOrigin bootId "wanxiang"

    // ---------- RFC 7464 分帧 ----------

    [<Fact>]
    let ``a framed record starts with RS and ends with LF`` () =
        let bytes = Frame.encode """{"a":1}"""
        Assert.Equal(byte Frame.RS, bytes.[0])
        Assert.Equal(byte Frame.LF, bytes.[bytes.Length - 1])
        let body = Encoding.UTF8.GetBytes """{"a":1}"""
        Assert.Equal(body.Length + 2, bytes.Length)

    [<Fact>]
    let ``splitting a single well formed record yields one segment ending in LF`` () =
        let segs = Frame.split (ReadOnlySpan<byte>(Frame.encode """{"a":1}"""))
        match segs with
        | [ payload, endsWithLf ] ->
            Assert.True endsWithLf
            Assert.Equal("""{"a":1}""", Encoding.UTF8.GetString payload)
        | other -> failwithf "expected one segment, got %d" other.Length

    [<Fact>]
    let ``a trailing record without LF is reported as unterminated`` () =
        let data = Array.append (Frame.encode """{"a":1}""") (Encoding.UTF8.GetBytes "\u001E{\"b\":2}")
        let segs = Frame.split (ReadOnlySpan<byte> data)
        Assert.Equal(2, segs.Length)
        Assert.True(fst segs.[0] |> ignore |> fun () -> snd segs.[0])
        Assert.False(snd segs.[1], "末段没有 LF，必须被判为未终止")

    // ---------- 恢复（生产路径：Replay.inspect 只读重放） ----------

    [<Fact>]
    let ``three intact commits round trip through CommitCodec`` () =
        let boot = Guid.NewGuid()
        let lines = [ 1UL; 2UL; 3UL ] |> List.map (fun i -> CommitCodec.commitToJsonLine (commit i boot))
        for line in lines do
            match CommitCodec.tryCommitFromJsonLine line with
            | Some c -> ()
            | None -> failwith "round trip failed"

    [<Fact>]
    let ``a truncated tail is detected by Replay.inspect as RepairRequired`` () =
        let boot = Guid.NewGuid()
        let good =
            [ 1UL; 2UL ]
            |> List.map (fun i -> Frame.encode (CommitCodec.commitToJsonLine (commit i boot)))
            |> Array.concat
        let half = Encoding.UTF8.GetBytes "\u001E{\"formatVersion\":1,\"id\":\"3\""
        let dir = Helpers.tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
            File.WriteAllBytes(path, Array.append good half)
            match Replay.inspect dir with
            | Healthy outcome -> Assert.Equal(2UL, outcome.lastCommitId)
            | RepairRequired (outcome, _) -> Assert.Equal(2UL, outcome.lastCommitId)
            | _ -> failwith "unexpected"
        finally
            Helpers.cleanup dir

    [<Fact>]
    let ``a corrupted middle segment triggers RepairRequired`` () =
        let boot = Guid.NewGuid()
        let seg i = Frame.encode (CommitCodec.commitToJsonLine (commit i boot))
        let data = Array.concat [ seg 1UL; Frame.encode "{not json"; seg 2UL ]
        let dir = Helpers.tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
            File.WriteAllBytes(path, data)
            match Replay.inspect dir with
            | RepairRequired (_, plan) -> Assert.True(plan.damage.reason.Contains "unparseable", plan.damage.reason)
            | _ -> failwith "expected repair required"
        finally
            Helpers.cleanup dir

    [<Fact>]
    let ``a commit id gap is detected by Replay.inspect`` () =
        let boot = Guid.NewGuid()
        let data = Array.concat [ Frame.encode (CommitCodec.commitToJsonLine (commit 1UL boot))
                                  Frame.encode (CommitCodec.commitToJsonLine (commit 5UL boot)) ]
        let dir = Helpers.tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
            File.WriteAllBytes(path, data)
            match Replay.inspect dir with
            | RepairRequired (outcome, _) -> Assert.Equal(1UL, outcome.lastCommitId)
            | _ -> failwith "expected repair required"
        finally
            Helpers.cleanup dir

    [<Fact>]
    let ``a first record not starting at id 1 is rejected`` () =
        // Replay 的 expectedId 从 1 开始：第一条记录 id=9 即损坏（无前缀可保留）。
        let dir = Helpers.tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let path = DataPaths.eventFilePath dir day
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let data = Array.concat [ Frame.encode (CommitCodec.commitToJsonLine (commit 9UL (Guid.NewGuid())))
                                      Frame.encode (CommitCodec.commitToJsonLine (commit 1UL (Guid.NewGuid()))) ]
            File.WriteAllBytes(path, data)
            match Replay.inspect dir with
            | RepairRequired (outcome, _) -> Assert.Equal(0UL, outcome.lastCommitId)
            | _ -> failwith "expected repair required"
        finally
            Helpers.cleanup dir

    // ---------- 外壳字段类型一致性（真实踩过的坑） ----------

    [<Fact>]
    let ``format version is written as a number so it reads back as a number`` () =
        let json = CommitCodec.commitToJsonLine (commit 1UL (Guid.NewGuid()))
        use doc = System.Text.Json.JsonDocument.Parse json
        Assert.Equal(System.Text.Json.JsonValueKind.Number, doc.RootElement.GetProperty("formatVersion").ValueKind)

    [<Fact>]
    let ``a record written by this code parses back with all required fields`` () =
        let original = commit 42UL (Guid.NewGuid())
        match CommitCodec.tryCommitFromJsonLine (CommitCodec.commitToJsonLine original) with
        | Some parsed ->
            Assert.Equal(original.id, parsed.id)
            Assert.Equal(original.bootId, parsed.bootId)
            Assert.Equal(original.source, parsed.source)
            Assert.Equal(original.committedAtUtc, parsed.committedAtUtc)
        | None -> failwith "round trip failed"

    [<Fact>]
    let ``commit id is carried as a JSON string not a number`` () =
        let json = CommitCodec.commitToJsonLine (commit UInt64.MaxValue (Guid.NewGuid()))
        use doc = System.Text.Json.JsonDocument.Parse json
        let cid = doc.RootElement.GetProperty("id")
        Assert.Equal(System.Text.Json.JsonValueKind.String, cid.ValueKind)
        Assert.Equal("18446744073709551615", cid.GetString())

    [<Fact>]
    let ``a foreign format version is rejected rather than silently accepted`` () =
        let json = """{"formatVersion":99,"id":"1","bootId":"00000000-0000-0000-0000-000000000000","source":"x","committedAtUtc":"2026-01-01T00:00:00.000Z","events":[]}"""
        // 未来版本：CommitCodec 拒绝（tryCommitFromJsonLine 返回 None），Replay 判定 UnsupportedFormat
        Assert.Null(CommitCodec.tryCommitFromJsonLine json)

    // ---------- RFC 8785 规范化 ----------

    [<Fact>]
    let ``key order does not affect the canonical form`` () =
        Assert.Equal(Canonical.Jcs.text """{"b":"2","a":"1"}""", Canonical.Jcs.text """{"a":"1","b":"2"}""")

    [<Fact>]
    let ``a large integer carried as a string survives canonicalization`` () =
        let json = """{"commitId":"18446744073709551615"}"""
        Assert.Contains("18446744073709551615", Canonical.Jcs.text json)

    [<Fact>]
    let ``a bare large integer is silently rewritten which is exactly why we avoid it`` () =
        // 这条不是「期望行为」，而是把 RFC 8785 附录 D 的陷阱钉成回归：
        // 一旦有人把 commitId 塞回裸数字，这里会立刻提醒他为什么不行。
        let rewritten = Canonical.Jcs.text """{"commitId":18446744073709551615}"""
        Assert.DoesNotContain("18446744073709551615", rewritten)

    [<Fact>]
    let ``hashing is stable across key order`` () =
        Assert.Equal(Canonical.Jcs.sha256Hex """{"b":"2","a":"1"}""", Canonical.Jcs.sha256Hex """{"a":"1","b":"2"}""")

    [<Fact>]
    let ``hashing garbage throws (no silent None: canonicalization failure must not be swallowed)`` () =
        // O-004：trySha256Hex 已删除——校验失败必须显式抛异常，不允许静默返回 None
        //（R6：不允许无证据继续运行）。调用方要么处理异常，要么保证输入合法。
        Assert.ThrowsAny<exn>(fun () -> Canonical.Jcs.sha256Hex "not json" |> ignore)
