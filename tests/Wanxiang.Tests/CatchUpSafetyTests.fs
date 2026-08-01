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
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
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

/// 并发提交竞争（42.md 第 15 条发布阻塞项）：多个线程同时提交，
/// 单写者 mailbox 串行化，id 必须严格连续、无空洞。
[<Fact>]
let ``concurrent submits keep ids strictly contiguous`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        let convId = newConversationId ()
        // 先建会话（并发提交会以会话不存在失败——先建立）
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
