namespace Wanxiang.Tests

open System
open System.Text
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core.Ledger

/// 账本内核（RFC 7464 分帧 + RFC 8785 规范化 + 恢复）的行为契约。
///
/// 这些用例覆盖的是**只靠读代码看不出来**的失败模式：
/// 字段写读类型不一致让 replay 全空、JCS 静默改写大整数、尾部截断与中间损坏的区别。
module EventLogTests =

    let private record (commitId: uint64) (bootId: Guid) : LedgerRecord =
        { formatVersion = Record.FormatVersion
          commitId = commitId
          bootId = bootId
          source = "wanxiang"
          at = DateTimeOffset.UnixEpoch
          commandId = Some "cmd-1"
          commandType = Some "conversation.create"
          commandHash = Some "abc"
          events = JsonArray() }

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

    // ---------- 恢复 ----------

    [<Fact>]
    let ``three intact records round trip without warnings`` () =
        let boot = Guid.NewGuid()
        let data =
            [ 1UL .. 3UL ]
            |> List.map (fun i -> Record.toJson (record i boot))
            |> List.map Frame.encode
            |> Array.concat
        let outcome = Recovery.recover data
        Assert.Equal(3, outcome.records.Length)
        Assert.Empty outcome.warnings
        Assert.Equal<int list>([ 1; 2; 3 ], outcome.records |> List.map (fun r -> int r.commitId))

    [<Fact>]
    let ``a truncated tail is dropped and everything before it survives`` () =
        let boot = Guid.NewGuid()
        let good =
            [ 1UL; 2UL ]
            |> List.map (fun i -> Record.toJson (record i boot))
            |> List.map Frame.encode
            |> Array.concat
        // 第三条写一半就断电：无 LF、JSON 不完整。
        let half = Encoding.UTF8.GetBytes "\u001E{\"formatVersion\":2,\"commitId\":\"3\""
        let outcome = Recovery.recover (Array.append good half)
        Assert.Equal(2, outcome.records.Length)
        Assert.True(outcome.truncatedBytes > 0, "尾部截断必须被记账")
        Assert.Equal(0, outcome.skippedSegments)

    [<Fact>]
    let ``a corrupted middle segment is skipped and parsing continues`` () =
        let boot = Guid.NewGuid()
        let segment i = Record.toJson (record i boot) |> Frame.encode
        let data = Array.concat [ segment 1UL; Frame.encode "{not json"; segment 2UL ]
        let outcome = Recovery.recover data
        Assert.Equal(2, outcome.records.Length)
        Assert.True((outcome.skippedSegments = 1), "中间损坏段应跳过而非终止恢复")
        Assert.True((outcome.truncatedBytes = 0), "它后面还有完好记录，不算尾部截断")

    [<Fact>]
    let ``a commit id gap inside one boot is reported`` () =
        let boot = Guid.NewGuid()
        let data = Array.concat [ Frame.encode (Record.toJson (record 1UL boot))
                                  Frame.encode (Record.toJson (record 5UL boot)) ]
        let outcome = Recovery.recover data
        Assert.Equal(2, outcome.records.Length)
        Assert.Contains(outcome.warnings, fun w -> w.Contains "commitId gap")

    [<Fact>]
    let ``a commit id regression across a reboot is reported`` () =
        let data = Array.concat [ Frame.encode (Record.toJson (record 9UL (Guid.NewGuid())))
                                  Frame.encode (Record.toJson (record 1UL (Guid.NewGuid()))) ]
        let outcome = Recovery.recover data
        Assert.Contains(outcome.warnings, fun w -> w.Contains "commitId regression")

    // ---------- 外壳字段类型一致性（真实踩过的坑） ----------

    [<Fact>]
    let ``format version is written as a number so it reads back as a number`` () =
        let json = Record.toJson (record 1UL (Guid.NewGuid()))
        use doc = System.Text.Json.JsonDocument.Parse json
        Assert.Equal(System.Text.Json.JsonValueKind.Number, doc.RootElement.GetProperty("formatVersion").ValueKind)

    [<Fact>]
    let ``a record written by this code parses back with all required fields`` () =
        let original = record 42UL (Guid.NewGuid())
        match Record.tryFromJson (Record.toJson original) with
        | Ok parsed ->
            Assert.Equal(original.commitId, parsed.commitId)
            Assert.Equal(original.bootId, parsed.bootId)
            Assert.Equal(original.source, parsed.source)
            Assert.Equal(original.at, parsed.at)
            Assert.Equal(original.commandId, parsed.commandId)
        | Error e -> failwithf "round trip failed: %s" e

    [<Fact>]
    let ``commit id is carried as a JSON string not a number`` () =
        let json = Record.toJson (record UInt64.MaxValue (Guid.NewGuid()))
        use doc = System.Text.Json.JsonDocument.Parse json
        let cid = doc.RootElement.GetProperty("commitId")
        Assert.Equal(System.Text.Json.JsonValueKind.String, cid.ValueKind)
        Assert.Equal("18446744073709551615", cid.GetString())

    [<Fact>]
    let ``a foreign format version is rejected rather than silently accepted`` () =
        let json = """{"formatVersion":1,"commitId":"1","bootId":"00000000-0000-0000-0000-000000000000","source":"x","at":"2026-01-01T00:00:00.000Z","events":[]}"""
        match Record.tryFromJson json with
        | Error e -> Assert.Contains("formatVersion", e)
        | Ok _ -> failwith "旧版本外壳必须被明确拒绝"

    [<Fact>]
    let ``a top level array is rejected because records must be objects`` () =
        match Record.tryFromJson "[]" with
        | Error _ -> ()
        | Ok _ -> failwith "顶层非对象必须拒绝（RFC 7464 自定界陷阱）"

    // ---------- RFC 8785 规范化 ----------

    [<Fact>]
    let ``key order does not affect the canonical form`` () =
        Assert.Equal(Jcs.canonicalize """{"b":"2","a":"1"}""", Jcs.canonicalize """{"a":"1","b":"2"}""")

    [<Fact>]
    let ``a large integer carried as a string survives canonicalization`` () =
        let json = """{"commitId":"18446744073709551615"}"""
        Assert.Contains("18446744073709551615", Jcs.canonicalize json)

    [<Fact>]
    let ``a bare large integer is silently rewritten which is exactly why we avoid it`` () =
        // 这条不是「期望行为」，而是把 RFC 8785 附录 D 的陷阱钉成回归：
        // 一旦有人把 commitId 塞回裸数字，这里会立刻提醒他为什么不行。
        let rewritten = Jcs.canonicalize """{"commitId":18446744073709551615}"""
        Assert.DoesNotContain("18446744073709551615", rewritten)

    [<Fact>]
    let ``hashing is stable across key order`` () =
        Assert.Equal(Jcs.sha256Hex """{"b":"2","a":"1"}""", Jcs.sha256Hex """{"a":"1","b":"2"}""")

    [<Fact>]
    let ``tolerant hashing returns none instead of throwing on garbage`` () =
        Assert.True(Jcs.trySha256Hex "not json" |> Option.isNone)
