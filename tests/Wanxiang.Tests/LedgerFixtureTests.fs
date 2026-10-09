module Wanxiang.Tests.LedgerFixtureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// T-001：黄金样本消费测试。
///
/// 规则（算法 F）：
/// - fixture 由 tools/gen-ledger-fixtures.sh **一次性**生成并提交（真实编码器）；
/// - 测试**只消费**，绝不调用当前 serializer 重建这些文件；
/// - 每个文件按 fixture.manifest 校验 SHA-256——实现漂移立即红。
module LedgerFixture =

    let private fixtureRoot () =
        let dir = System.Reflection.Assembly.GetExecutingAssembly().Location |> Path.GetDirectoryName
        Path.Combine(dir, "fixtures", "Ledger")

    let private manifest () : Map<string, string> =
        let path = Path.Combine(fixtureRoot (), "fixture.manifest")
        Assert.True(File.Exists path, "fixture.manifest missing")
        File.ReadAllLines path
        |> Array.choose (fun line ->
            match line.Trim().Split([| ' ' |], 2) with
            | [| sha; rel |] when sha.Length = 64 -> Some(rel.Trim(), sha)
            | _ -> None)
        |> Map.ofArray

    let private sha256 (path: string) : string =
        use fs = File.OpenRead path
        use sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        let buf = Array.zeroCreate<byte> (64 * 1024)
        let rec loop () =
            let n = fs.Read(buf, 0, buf.Length)
            if n > 0 then
                sha.AppendData(buf, 0, n)
                loop ()
        loop ()
        Convert.ToHexStringLower(sha.GetHashAndReset())

    let private fixturePath (case: string) (file: string) =
        Path.Combine(fixtureRoot (), case, file)

    [<Fact>]
    let ``manifest 覆盖全部黄金样本且哈希一致`` () =
        let m = manifest ()
        Assert.True(m.Count >= 6, sprintf "manifest 应有 6 个文件，实际 %d" m.Count)
        for KeyValue(rel, expected) in m do
            let full = Path.Combine(fixtureRoot (), rel)
            Assert.True(File.Exists full, sprintf "%s missing" rel)
            Assert.Equal(expected, sha256 full)

    /// 场景 E：旧版本/当前版本数据 → 当前实现必须读取成功，投影与预期一致。
    [<Fact>]
    let ``current 黄金样本：健康重放，投影正确`` () =
        let dir = tempDir ()
        try
            // fixture 是历史数据：复制到数据目录后按只读检查消费（不重建）
            let eventsDir = DataPaths.eventsDir dir
            Directory.CreateDirectory eventsDir |> ignore
            File.Copy(fixturePath "current" "2026-08-01.jsonseq", Path.Combine(eventsDir, "2026-08-01.jsonseq"))
            File.Copy(fixturePath "current" "2026-08-02.jsonseq", Path.Combine(eventsDir, "2026-08-02.jsonseq"))
            match Replay.inspect dir with
            | Healthy outcome ->
                Assert.Equal(3UL, outcome.lastCommitId)
                Assert.Equal(2, Projection.conversationList outcome.projection |> List.length)
                // 跨 UTC 日期文件 id 连续（1,2,3）
                Assert.Equal(3, outcome.commits.Length)
                // 场景 E：投影内容与固定输入的确定性投影一致（黄金样本输入固定 → 投影固定）
                // 会话 1：固定 convId，标题「黄金样本」，含 1 条用户消息「你好，万象！」
                let conv1 = Guid.Parse "11111111-2222-3333-4444-555555555555"
                match Projection.tryConversation outcome.projection conv1 with
                | Some c ->
                    Assert.Equal("黄金样本", c.title)
                    Assert.Equal(Some 2UL, c.lastCommitId)
                    // 消息内容（AG-UI 消息载荷）
                    Assert.Equal(1, c.messages.Length)
                    let payload = c.messages.[0].payloadJson
                    let contents = payload["contents"]
                    let first = contents[0]
                    let text = first["text"].GetValue<string>()
                    Assert.Equal("你好，万象！", text)
                | None -> failwith "golden conversation 1 missing from projection"
                // 会话 2：次日会话，固定 convId
                let conv2 = Guid.Parse "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
                match Projection.tryConversation outcome.projection conv2 with
                | Some c -> Assert.Equal("次日会话", c.title)
                | None -> failwith "golden conversation 2 missing from projection"
            | _ -> failwith "current fixture must be healthy"
        finally
            cleanup dir

    /// 场景 B：尾部截断 → 只读检查报告损坏，不修改文件；有效水位 = 前缀。
    [<Fact>]
    let ``damaged-tail 黄金样本：只读检查报告损坏且不修改`` () =
        let dir = tempDir ()
        try
            let eventsDir = DataPaths.eventsDir dir
            Directory.CreateDirectory eventsDir |> ignore
            File.Copy(fixturePath "damaged-tail" "2026-08-01.jsonseq", Path.Combine(eventsDir, "2026-08-01.jsonseq"))
            File.Copy(fixturePath "damaged-tail" "2026-08-02.jsonseq", Path.Combine(eventsDir, "2026-08-02.jsonseq"))
            let bytesBefore = File.ReadAllBytes(Path.Combine(eventsDir, "2026-08-02.jsonseq"))
            match Replay.inspect dir with
            | RepairRequired (outcome, _) ->
                Assert.Equal(3UL, outcome.lastCommitId) // 有效前缀 = id 1..3（尾部半条 id 4 未完成）
                // 只读检查绝不修改
                Assert.Equal<byte[]>(bytesBefore, File.ReadAllBytes(Path.Combine(eventsDir, "2026-08-02.jsonseq")))
            | _ -> failwith "damaged-tail fixture must require repair"
        finally
            cleanup dir

    /// 场景：中间损坏 → 报告损坏，不静默跳过。
    [<Fact>]
    let ``malformed-record 黄金样本：报告损坏`` () =
        let dir = tempDir ()
        try
            let eventsDir = DataPaths.eventsDir dir
            Directory.CreateDirectory eventsDir |> ignore
            File.Copy(fixturePath "malformed-record" "2026-08-02.jsonseq", Path.Combine(eventsDir, "2026-08-02.jsonseq"))
            match Replay.inspect dir with
            | RepairRequired _ -> ()
            | _ -> failwith "malformed-record fixture must require repair"
        finally
            cleanup dir

    /// 场景：id 空洞 → 报告损坏。
    [<Fact>]
    let ``id-gap 黄金样本：报告损坏`` () =
        let dir = tempDir ()
        try
            let eventsDir = DataPaths.eventsDir dir
            Directory.CreateDirectory eventsDir |> ignore
            File.Copy(fixturePath "id-gap" "2026-08-01.jsonseq", Path.Combine(eventsDir, "2026-08-01.jsonseq"))
            match Replay.inspect dir with
            | RepairRequired (outcome, _) -> Assert.Equal(1UL, outcome.lastCommitId)
            | _ -> failwith "id-gap fixture must require repair"
        finally
            cleanup dir

    /// 场景：非法 UTF-8 → 报告损坏，不静默 U+FFFD 入库。
    [<Fact>]
    let ``invalid-utf8 黄金样本：报告损坏`` () =
        let dir = tempDir ()
        try
            let eventsDir = DataPaths.eventsDir dir
            Directory.CreateDirectory eventsDir |> ignore
            File.Copy(fixturePath "invalid-utf8" "2026-08-01.jsonseq", Path.Combine(eventsDir, "2026-08-01.jsonseq"))
            match Replay.inspect dir with
            | RepairRequired (_, plan) ->
                Assert.True(plan.damage.reason.Contains("UTF-8") || plan.damage.reason.Contains("unparseable"), plan.damage.reason)
            | _ -> failwith "invalid-utf8 fixture must require repair"
        finally
            cleanup dir
