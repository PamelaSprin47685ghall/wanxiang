module Wanxiang.Tests.CatchUpSafetyTests

open System
open System.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// 回归测试（review 阻塞级发现）：
/// WsConnection.EnterSnapshotMode 绝不能在 coordinator mailbox 线程内同步调用 getCommitsAfter
/// （PostAndReply 会自锁，阻塞所有提交与广播）。修复后 catch-up 经 Task.Run 调度。
/// 本测试验证协调器的不变量：onCommitted 回调执行非阻塞广播工作后，后续提交仍能正常完成。
[<Fact>]
let ``coordinator survives broadcast work in onCommitted callback`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        // onCommitted：模拟 BroadcastCommit 的非阻塞广播工作（真实路径：ConnectionRegistry.BroadcastCommit
        // 只做 TrySend，不读取投影/不 PostAndReply；catch-up 已由 WsConnection 调度到独立 Task）
        let committed = ResizeArray<Events.Commit>()
        use coord = new CommitCoordinator(dir, outcome, committed.Add, ignore)
        // 多次提交（含消息事件），验证回调不被阻塞、id 严格连续
        let convId = newConversationId ()
        let r1 =
            coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
        let r2 =
            coord.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m1" } ]
        let r3 =
            coord.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = assistantMessageJson "m2" } ]
        match r1, r2, r3 with
        | Committed c1, Committed c2, Committed c3 ->
            Assert.Equal(1UL, c1.id)
            Assert.Equal(2UL, c2.id)
            Assert.Equal(3UL, c3.id)
            Assert.Equal(3, committed.Count)
        | _ -> failwith "commits failed"
        coord.Shutdown()
    finally
        cleanup dir

/// §13/§22：catch-up 单批有限。CommitsAfter(cursor, limit) 只返回最多 limit 条，
/// 慢客户端追赶长历史时避免每批 O(n) 复制剩余全部（O(n²)）并阻塞单写者 mailbox。
[<Fact>]
let ``CommitsAfter paging returns at most limit commits and advances cursor`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        let convId = newConversationId ()
        coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ] |> ignore
        for i in 1..9 do
            coord.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson (sprintf "m%d" i) } ] |> ignore
        // 10 条提交（id 1..10）
        Assert.Equal(10UL, coord.Projection.latestCommitId)
        // limit=4：只返回 4 条，且是游标后**最前**的 4 条（升序）
        let page1 = coord.CommitsAfter(0UL, 4)
        Assert.Equal(4, page1.Length)
        Assert.Equal<CommitId>([ 1UL; 2UL; 3UL; 4UL ], page1 |> List.map (fun c -> c.id))
        // 游标推进到批尾后取下一页：4..8
        let page2 = coord.CommitsAfter(4UL, 4)
        Assert.Equal(4, page2.Length)
        Assert.Equal<CommitId>([ 5UL; 6UL; 7UL; 8UL ], page2 |> List.map (fun c -> c.id))
        // 最后一页不足 limit
        let page3 = coord.CommitsAfter(8UL, 4)
        Assert.Equal(2, page3.Length)
        Assert.Equal<CommitId>([ 9UL; 10UL ], page3 |> List.map (fun c -> c.id))
        // 分页遍历与全量结果一致（无丢、无重）
        let paged = page1 @ page2 @ page3
        Assert.Equal<CommitId>(coord.CommitsAfter 0UL |> List.map (fun c -> c.id), paged |> List.map (fun c -> c.id))
        // limit=0 → 空（不返回任何条）
        Assert.Empty(coord.CommitsAfter(0UL, 0))
        // limit 超过剩余 → 返回剩余全部（不越界）
        Assert.Equal(10, (coord.CommitsAfter(0UL, 100)).Length)
        // 游标超过最新 + 任意 limit → 空
        Assert.Empty(coord.CommitsAfter(10UL, 4))
        coord.Shutdown()
    finally
        cleanup dir

/// 并发提交竞争（42.md 第 15 条发布阻塞项）：多个线程同时提交，
/// 单写者 mailbox 串行化，id 必须严格连续、无空洞。
[<Fact>]
let ``concurrent submits keep ids strictly contiguous`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        let convId = newConversationId ()
        // 先建对话（并发提交会以对话不存在失败——先建立）
        coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ] |> ignore
        let threads =
            [ for i in 1..8 ->
                Thread(fun () ->
                    let r =
                        coord.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson (sprintf "m%d" i) } ]
                    match r with
                    | Committed _ -> ()
                    | _ -> failwith (sprintf "concurrent submit %d failed" i)) ]
        for t in threads do t.Start()
        for t in threads do t.Join()
        let proj = coord.Projection
        // 1 (created) + 8 (messages) = 9
        Assert.Equal(9UL, proj.latestCommitId)
        let conv = Projection.tryConversation proj convId |> Option.get
        Assert.Equal(8, conv.messages.Length)
        coord.Shutdown()
    finally
        cleanup dir

/// CommitsAfter 二分定位：游标后提交必须按 id 升序完整返回（性能优化后正确性回归）。
[<Fact>]
let ``CommitsAfter returns commits after cursor in ascending order`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        let convId = newConversationId ()
        coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ] |> ignore
        for i in 1..5 do
            coord.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson (sprintf "m%d" i) } ] |> ignore
        // 全部 6 条提交（id 1..6）
        Assert.Equal(6UL, coord.Projection.latestCommitId)
        // 游标 0 → 全部
        let all = coord.CommitsAfter 0UL
        Assert.Equal(6, all.Length)
        Assert.Equal<CommitId>([ 1UL; 2UL; 3UL; 4UL; 5UL; 6UL ], all |> List.map (fun c -> c.id))
        // 游标 2 → 3..6
        let after2 = coord.CommitsAfter 2UL
        Assert.Equal<CommitId>([ 3UL; 4UL; 5UL; 6UL ], after2 |> List.map (fun c -> c.id))
        // 游标 6 → 空
        Assert.Empty(coord.CommitsAfter 6UL)
        // 游标 100（超过最新）→ 空
        Assert.Empty(coord.CommitsAfter 100UL)
        coord.Shutdown()
    finally
        cleanup dir
