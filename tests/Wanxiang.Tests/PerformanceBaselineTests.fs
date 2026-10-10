module Wanxiang.Tests.PerformanceBaselineTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// T-005：性能基线（AGENTS.md §21.6）。
///
/// 固定规模：10³ / 10⁴ commits。测量：
///   cold-start       空目录 → inspect 完整重放
///   snapshot-start   快照存在 → inspectFrom 续播
///   memory-after-start 启动后 GC 托管堆（仅记录，不硬断言——CI 环境波动大）
///   catch-up latency CommitsAfter(cursor) 延迟
///   snapshot creation 快照写延迟
///
/// 断言用**宽松上界**（防回归，不 flaky）：10³ 冷启动 < 5s、快照写 < 5s 等。
/// 精确基线记录在下方注释（本机实测，2026-10-10）。
///
/// 10⁵/10⁶ 属于 large-history 分级（CI 定期跑，不进 PR gate）——见 CI 分级 §24。
module PerformanceBaseline =

    /// 生成 n 条提交（创建 1 会话 + n-1 条消息），落盘为 RFC 7464 日志。
    /// 返回 (dataDir, 日志字节数)。
    let private seedLog (n: int) : string =
        let dir = tempDir ()
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let convId = newConversationId ()
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        // 10 万级小写入：直接 FileStream.Write 是系统调用瓶颈，用 BufferedStream 聚合
        use fs = new BufferedStream(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), 1 <<< 20)
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "perf"; config = testConfig () } ]
        let b1 = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)
        fs.Write(b1, 0, b1.Length)
        for i in 2 .. n do
            let c = Events.Commit.create (uint64 i) (DateTimeOffset(day)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson (sprintf "msg-%d" i) } ]
            let b = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c)
            fs.Write(b, 0, b.Length)
        fs.Flush()
        dir

    let private timed (f: unit -> 'a) : 'a * TimeSpan =
        let sw = Stopwatch.StartNew()
        let r = f ()
        sw.Stop()
        r, sw.Elapsed

    [<Fact>]
    let ``10^3 commits：冷启动重放 < 5s`` () =
        let dir = seedLog 1000
        try
            let _, elapsed = timed (fun () -> Replay.inspect dir)
            Assert.True(elapsed < TimeSpan.FromSeconds 5.0, sprintf "cold start took %A" elapsed)
        finally
            cleanup dir

    [<Fact>]
    let ``10^4 commits：冷启动重放 < 10s`` () =
        let dir = seedLog 10000
        try
            let _, elapsed = timed (fun () -> Replay.inspect dir)
            Assert.True(elapsed < TimeSpan.FromSeconds 10.0, sprintf "cold start took %A" elapsed)
        finally
            cleanup dir

    [<Fact>]
    let ``快照启动：10^4 commits 续播 < 冷启动`` () =
        // 10^3 规模太小：快照 JSON 解析开销（投影 + commits 反序列化）占比大，
        // 快照启动反而不占优（实测 10^3：快照 46ms vs 冷启动 26ms）。
        // 10^4 消息密集数据（1 会话 + 9999 消息）实测：快照 299ms vs 冷启动 759ms——
        // 快照跳过投影构建（消息记账/fork 展开），收益随投影复杂度增长。
        let dir = seedLog 10000
        try
            let outcome = replayOutcome dir
            let fileLen = FileInfo(DataPaths.eventFilePath dir (DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc))).Length
            match Snapshot.write dir outcome.projection (Some "2026-08-01.jsonseq") fileLen outcome.commits with
            | Error e -> failwith e
            | Ok _ ->
                let _, coldElapsed = timed (fun () -> Replay.inspect dir)
                let snapshotStart () =
                    match Snapshot.findLatest dir with
                    | SnapshotStartup.SnapshotUsed info ->
                        Replay.inspectFrom
                            dir
                            { projection = info.projection
                              throughCommitId = info.throughCommitId
                              throughFile = info.throughFile
                              throughOffset = info.throughOffset
                              commits = info.commits }
                    | other -> failwithf "expected SnapshotUsed, got %A" other
                let _, snapElapsed = timed snapshotStart
                // 续播只重放 throughCommitId 之后（此处 0 条），应明显快于完整重放
                Assert.True(snapElapsed < coldElapsed, sprintf "snapshot start %A >= cold start %A" snapElapsed coldElapsed)
        finally
            cleanup dir

    [<Fact>]
    let ``快照创建：10^3 commits < 5s`` () =
        let dir = seedLog 1000
        try
            let outcome = replayOutcome dir
            let fileLen = FileInfo(DataPaths.eventFilePath dir (DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc))).Length
            let _, elapsed = timed (fun () -> Snapshot.write dir outcome.projection (Some "2026-08-01.jsonseq") fileLen outcome.commits)
            Assert.True(elapsed < TimeSpan.FromSeconds 5.0, sprintf "snapshot creation took %A" elapsed)
        finally
            cleanup dir

    [<Fact>]
    let ``catch-up 延迟：10^4 commits 中段游标 < 100ms`` () =
        let dir = seedLog 10000
        try
            let outcome = replayOutcome dir
            use coord = new CommitCoordinator(dir, outcome, (fun _ -> ()), (fun _ -> ()))
            let _, elapsed = timed (fun () -> coord.CommitsAfter(5000UL))
            Assert.True(elapsed < TimeSpan.FromMilliseconds 100.0, sprintf "catch-up took %A" elapsed)
        finally
            cleanup dir

    [<Fact>]
    let ``内存采样：10^4 commits 启动后托管堆 < 256 MiB`` () =
        let dir = seedLog 10000
        try
            let outcome = replayOutcome dir
            // 强制 GC 后采样当前托管堆（仅记录 + 宽松上界）
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()
            let heapBefore = GC.GetTotalMemory(true)
            let _ = outcome.projection
            let heapAfter = GC.GetTotalMemory(true)
            // 10^4 条消息的投影应远小于 256 MiB（宽松上界防回归）
            Assert.True(heapBefore < 256L * 1024L * 1024L, sprintf "heap before %d" heapBefore)
            Assert.True(heapAfter < 256L * 1024L * 1024L, sprintf "heap after %d" heapAfter)
        finally
            cleanup dir

    // -------------------------------------------------------------------------
    // Large-history 分级（§24：定期运行，不进 PR gate）
    // -------------------------------------------------------------------------

    [<Fact>]
    [<Trait("Category", "LargeHistory")>]
    // 实测基线（2026-10-10，本机）：10^5 纯 inspect（不含 seed 装置）~102s。
    // 这是当前实现的真实冷启动成本——正是快照要解决的问题（§12：第一版只优化启动）。
    // 上界 150s：宽松余量覆盖 CI 机器差异，防 flaky。
    let ``10^5 commits：冷启动重放 < 150s`` () =
        let dir = seedLog 100000
        try
            let _, elapsed = timed (fun () -> Replay.inspect dir)
            Assert.True(elapsed < TimeSpan.FromSeconds 150.0, sprintf "cold start took %A" elapsed)
        finally
            cleanup dir

    [<Fact(Skip = "10^6 生成+重放 >300s，CI 不可行；保留为手动基准（§21.6），待第二版 compaction 后重新评估")>]
    [<Trait("Category", "LargeHistory")>]
    let ``10^6 commits：冷启动重放 < 300s（手动基准）`` () =
        let dir = seedLog 1000000
        try
            let _, elapsed = timed (fun () -> Replay.inspect dir)
            Assert.True(elapsed < TimeSpan.FromSeconds 300.0, sprintf "cold start took %A" elapsed)
        finally
            cleanup dir
