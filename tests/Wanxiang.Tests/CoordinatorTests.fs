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
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
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
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        let committed = ResizeArray<Events.Commit>()
        let truncated = ResizeArray<Events.Commit * WanxiangError>()
        use coord = new CommitCoordinator(dir, outcome, committed.Add, (fun (c, e, _, _) -> truncated.Add(c, e)))
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
let ``test_truncate_and_reuse_id`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        let truncated = ResizeArray<Events.Commit * WanxiangError>()
        use coord = new CommitCoordinator(dir, outcome, (fun _ -> ()), (fun (c, e, _, _) -> truncated.Add(c, e)))
        // 事件引用不存在的会话 → 投影失败 → 截尾
        let bad =
            coord.Submit
                { events = [ AgentMessageRecorded { conversationId = Guid.NewGuid(); payloadJson = userMessageJson "x" } ]
                  commandId = None; commandType = None; commandHash = None; nowUtc = None }
        match bad with
        | TruncatedAndReused (commit, _) ->
            Assert.Equal(1UL, commit.id)
            Assert.Equal(1, truncated.Count)
        | _ -> failwith "expected truncation"
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
        let outcome2 = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        Assert.Equal(1UL, outcome2.lastCommitId)
        Assert.Equal(1, Projection.conversationList outcome2.projection |> List.length)
    finally
        cleanup dir

[<Fact>]
let ``test_command_idempotency`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coord = new CommitCoordinator(dir, outcome, (fun _ -> ()), (fun _ -> ()))
        let convId = newConversationId ()
        let invId = Guid.NewGuid()
        let cmd =
            CreateConversation {| invocationId = invId; conversationId = convId; title = "A"; config = testConfig () |}
        let plan (cursor: CommitId) = CommandEngine.plan coord.Projection cursor cmd
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
        match CommandEngine.plan coord.Projection 1UL cmd2 with
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
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coord = new CommitCoordinator(dir, outcome, (fun _ -> ()), (fun _ -> ()))
        let convId = newConversationId ()
        let r =
            CommandEngine.plan coord.Projection 0UL
                (CreateConversation {| invocationId = Guid.NewGuid(); conversationId = convId; title = "A"; config = testConfig () |})
        match r with
        | Planned p ->
            let submit = { events = p.events; commandId = Some p.commandId; commandType = Some p.commandType; commandHash = Some p.canonicalHash; nowUtc = None }
            coord.Submit submit |> ignore
        | _ -> failwith "plan failed"
        // 客户端游标仍为 0，但最新提交为 1 → 对会话的写被拒绝
        match CommandEngine.plan coord.Projection 0UL (RenameConversation {| invocationId = Guid.NewGuid(); conversationId = convId; title = "X" |}) with
        | Rejected (StaleProjection required) -> Assert.Equal(1UL, required)
        | _ -> failwith "expected stale rejection"
        coord.Shutdown()
    finally
        cleanup dir

[<Fact>]
let ``test_coordinator_idempotency_binary_search_branches`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        // 注意：每个命令必须使用独立的 conversationId，避免因重复创建相同会话导致投影失败 (DuplicateConversation)
        let mkCmd (title: string) =
            let convId = newConversationId ()
            let invId = Guid.NewGuid()
            let cmd = CreateConversation {| invocationId = invId; conversationId = convId; title = title; config = testConfig () |}
            let canonical = ClientCommand.canonicalPayload cmd
            let cid = CommandId.compute invId (ClientCommand.commandType cmd) canonical
            let ch = CommandId.sha256Hex canonical
            let submit =
                { events = [ ConversationCreated { conversationId = convId; title = title; config = testConfig () } ]
                  commandId = Some cid
                  commandType = Some(ClientCommand.commandType cmd)
                  commandHash = Some ch
                  nowUtc = None }
            cid, ch, submit
        
        let cid1, ch1, sub1 = mkCmd "Title1"
        let cid2, ch2, sub2 = mkCmd "Title2"
        let cid3, ch3, sub3 = mkCmd "Title3"
        
        // 3 条合法提交依次写入：id 1, 2, 3，全部 Committed
        match coord.Submit sub1 with Committed c -> Assert.Equal(1UL, c.id) | r -> failwithf "sub1 failed: %A" r
        match coord.Submit sub2 with Committed c -> Assert.Equal(2UL, c.id) | r -> failwithf "sub2 failed: %A" r
        match coord.Submit sub3 with Committed c -> Assert.Equal(3UL, c.id) | r -> failwithf "sub3 failed: %A" r
        
        // 此时 commits 为 [c1; c2; c3]，mid = 1 (c2.id = 2)
        // 1. 重放 sub1：target=1 < 2，走 hi <- mid - 1 分支
        match coord.Submit sub1 with
        | IdempotentReplay c -> Assert.Equal(1UL, c.id)
        | r -> failwithf "expected IdempotentReplay 1, got %A" r
        
        // 2. 重放 sub3：target=3 > 2，走 lo <- mid + 1 分支
        match coord.Submit sub3 with
        | IdempotentReplay c -> Assert.Equal(3UL, c.id)
        | r -> failwithf "expected IdempotentReplay 3, got %A" r
        
        coord.Shutdown()
    finally
        cleanup dir

[<Fact>]
let ``test_coordinator_idempotency_record_missing_from_commits_rejected`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        // 构造投影中有 idempotency 记录，但 outcome.commits 列表为空的边界状态
        let cid = "test-cid-orphan"
        let ch = "test-hash"
        let customOutcome =
            let p = Projection.empty
            let idemRec: IdemRecord =
                { commandId = cid
                  invocationId = Guid.NewGuid()
                  commandType = "test"
                  canonicalHash = ch
                  commitId = 99UL }
            { projection = { p with idempotency = Map.add cid idemRec p.idempotency }
              commits = []
              truncatedFiles = []
              lastCommitId = 99UL
              lastDateUtc = DateTime.UtcNow.Date }
        use coord = new CommitCoordinator(dir, customOutcome, ignore, ignore)
        let submit =
            { events = []
              commandId = Some cid
              commandType = Some "test"
              commandHash = Some ch
              nowUtc = None }
        // 命中 if commits.Count = 0 then None 以及 72-74 行的 CommandIdRejected Poisoned
        match coord.Submit submit with
        | CommandIdRejected (Poisoned msg) ->
            Assert.Contains("references missing commit", msg)
        | r -> failwithf "expected CommandIdRejected Poisoned, got %A" r
        coord.Shutdown()
    finally
        cleanup dir

[<Fact>]
let ``test_coordinator_append_commit_failure_triggers_onTruncated_and_CommitFailed`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        let truncated = ResizeArray<Events.Commit * WanxiangError * int64 * string>()
        let coord = new CommitCoordinator(dir, outcome, ignore, (fun t -> truncated.Add(t)))
        
        // 优雅模拟 AppendCommit 失败（避免反射私有字段产生 NullReferenceException）：
        // JsonSeqWriter 在跨日写入时会调用 openFile 打开次日文件。
        // 我们提前将次日文件以 FileShare.None 排他占用，当 Coordinator 提交次日事件时，
        // AppendCommit 中的 openFile 必然抛出 IOException，完美触发协调器的 append 异常处理逻辑（95-100行）。
        let tomorrow = DateTime.UtcNow.Date.AddDays(1.0)
        let tomorrowFile = DataPaths.eventFilePath dir tomorrow
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName tomorrowFile) |> ignore
        use locker = new FileStream(tomorrowFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        
        let convId = newConversationId ()
        let submit =
            { events = [ ConversationCreated { conversationId = convId; title = "Tomorrow"; config = testConfig () } ]
              commandId = None
              commandType = None
              commandHash = None
              nowUtc = Some (DateTimeOffset(tomorrow.AddHours(1.0), TimeSpan.Zero)) }
        
        // 命中 CommitCoordinator.fs 95-100 行异常处理
        match coord.Submit submit with
        | CommitFailed (Poisoned msg) ->
            Assert.Contains("append failed", msg)
            Assert.True(truncated.Count = 1)
            let (commit, err, off, path) = truncated.[0]
            Assert.Equal(1UL, commit.id)
            Assert.Equal(-1L, off)
            Assert.True(WanxiangError.message err |> fun m -> m.Contains("append failed"))
        | r -> failwithf "expected CommitFailed, got %A" r
        
        coord.Shutdown()
    finally
        cleanup dir

[<Fact>]
let ``test_coordinator_repeated_shutdown_and_idisposable`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        let coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        // 覆盖 168-175 行：重复 Shutdown 及 IDisposable.Dispose
        coord.Shutdown()
        coord.Shutdown() // 重复调用走 else 分支
        (coord :> IDisposable).Dispose() // 覆盖显式 IDisposable 实现
    finally
        cleanup dir
