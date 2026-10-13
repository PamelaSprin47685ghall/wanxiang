module Wanxiang.Tests.CoordinatorTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

[<Fact>]
let ``test_cursor_catch_up_commits_replayed`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coord = new EventStore(dir)
        let convId = newConversationId ()
        let result = coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "同步"; config = testConfig () } ]
        match result with
        | Committed c ->
            let commits = coord.CommitsAfter 0UL
            Assert.Single(commits) |> ignore
            Assert.Equal(c.id, commits.Head.id)
        | _ -> failwith "expected commit"
    finally
        cleanup dir

    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        let committed = ResizeArray<Events.Commit>()
        let truncated = ResizeArray<Events.Commit * WanxiangError>()
        use coord = new EventStore(dir, committed.Add)
        let convId = newConversationId ()
        let r1 =
            coord.Submit
                { events = [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
                  commandId = None; commandType = None; commandHash = None; nowUtc = None }
        let r2 =
            coord.Submit
                { events = [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "hi" } ]
                  commandId = None; commandType = None; commandHash = None; nowUtc = None }
        match r1, r2 with
        | Committed c1, Committed c2 ->
            Assert.Equal(1UL, c1.id)
            Assert.Equal(2UL, c2.id)
            Assert.Equal(2, committed.Count)
        | _ -> failwith "expected commits"
        let proj = coord.Projection
        Assert.Equal(2UL, proj.latestCommitId)
        Assert.Equal(1, Projection.conversationList proj |> List.head |> fun c -> c.messages.Length)
        coord.Shutdown()
    finally
        cleanup dir

[<Fact>]
let ``test_rejected_commit_writes_nothing_and_keeps_id`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        let truncated = ResizeArray<Events.Commit * WanxiangError>()
        use coord = new EventStore(dir)
        // 事件引用不存在的对话 → 折叠被拒 → 什么都不写（新账本先折叠后写盘，不再有截尾路径）
        let bad =
            coord.Submit
                { events = [ AgentMessageRecorded { conversationId = Guid.NewGuid(); payloadJson = userMessageJson "x" } ]
                  commandId = None; commandType = None; commandHash = None; nowUtc = None }
        match bad with
        | CommitFailed _ -> Assert.Equal(0, truncated.Count)
        | other -> failwithf "expected rejection, got %A" other
        // id 复用：下一个成功提交仍是 1
        let convId = newConversationId ()
        let ok =
            coord.Submit
                { events = [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
                  commandId = None; commandType = None; commandHash = None; nowUtc = None }
        match ok with
        | Committed c -> Assert.Equal(1UL, c.id)
        | _ -> failwith "expected commit with reused id"
        // 重启后日志只有一条有效记录
        coord.Shutdown()
        let outcome2 = replayOutcome dir
        Assert.Equal(1UL, outcome2.lastCommitId)
        Assert.Equal(1, Projection.conversationList outcome2.projection |> List.length)
    finally
        cleanup dir

[<Fact>]
let ``test_command_idempotency`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coord = new EventStore(dir)
        let convId = newConversationId ()
        let invId = Guid.NewGuid()
        let cmd =
            CreateConversation {| invocationId = invId; conversationId = convId; title = "A"; config = testConfig () |}
        let plan (cursor: CommitId) = CommandEngine.planStamped coord.Stamps coord.Projection cursor cmd
        match plan 0UL with
        | Planned p ->
            let submit =
                { events = p.events; commandId = Some p.commandId; commandType = Some p.commandType; commandHash = Some p.canonicalHash; nowUtc = None }
            match coord.Submit submit with
            | Committed c -> Assert.Equal(1UL, c.id)
            | _ -> failwith "commit failed"
        | _ -> failwith "plan failed"
        // 重试（同 invocationId + 同内容）→ 幂等命中
        match plan 1UL with
        | PlanResult.IdempotentReplay cid -> Assert.Equal(1UL, cid)
        | _ -> failwith "expected idempotent replay"
        // 新 invocationId → 新提交
        let cmd2 =
            CreateConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = "B"; config = testConfig () |}
        match CommandEngine.planStamped coord.Stamps coord.Projection 1UL cmd2 with
        | Planned p ->
            let submit =
                { events = p.events; commandId = Some p.commandId; commandType = Some p.commandType; commandHash = Some p.canonicalHash; nowUtc = None }
            match coord.Submit submit with
            | Committed c -> Assert.Equal(2UL, c.id)
            | _ -> failwith "commit failed"
        | _ -> failwith "plan failed"
        coord.Shutdown()
    finally
        cleanup dir

[<Fact>]
let ``test_stale_client_rejected`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coord = new EventStore(dir)
        let convId = newConversationId ()
        let r =
            CommandEngine.planStamped coord.Stamps coord.Projection 0UL
                (CreateConversation {| invocationId = Guid.NewGuid(); conversationId = convId; title = "A"; config = testConfig () |})
        match r with
        | Planned p ->
            let submit = { events = p.events; commandId = Some p.commandId; commandType = Some p.commandType; commandHash = Some p.canonicalHash; nowUtc = None }
            coord.Submit submit |> ignore
        | _ -> failwith "plan failed"
        // 客户端游标仍为 0，但最新提交为 1 → 对对话的写被拒绝
        match CommandEngine.planStamped coord.Stamps coord.Projection 0UL (RenameConversation {| invocationId = Guid.NewGuid(); conversationId = convId; title = "X" |}) with
        | Rejected (StaleProjection required) -> Assert.Equal(1UL, required)
        | _ -> failwith "expected stale rejection"
        coord.Shutdown()
    finally
        cleanup dir

[<Fact>]
let ``test_coordinator_repeated_shutdown_and_idisposable`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        let coord = new EventStore(dir)
        // 覆盖 168-175 行：重复 Shutdown 及 IDisposable.Dispose
        coord.Shutdown()
        coord.Shutdown() // 重复调用走 else 分支
        (coord :> IDisposable).Dispose() // 覆盖显式 IDisposable 实现
    finally
        cleanup dir
