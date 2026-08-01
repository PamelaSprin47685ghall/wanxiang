module Wanxiang.Tests.SessionAndForkTests

open System
open Xunit
open Wanxiang.Core
open Wanxiang.Server
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// 会话列表摘要应包含决策 125 要求的字段（最后消息摘要、运行状态、配置摘要）。
[<Fact>]
let ``conversation list summary includes last message and runtime state`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let convId = newConversationId ()
        let commits =
            [ Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "摘要"; config = testConfig () } ]
              Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = assistantMessageJson "最后一条消息内容" } ] ]
        let proj =
            commits
            |> List.fold (fun p c -> Projection.applyCommit p c |> function Ok p -> p | Error e -> failwith (WanxiangError.message e)) Projection.empty
        let items = ServerModel.conversationListItems proj (fun _ -> "idle")
        Assert.Single items
        let item = items[0].AsObject()
        // 决策 125：最后可见消息摘要
        Assert.Contains("最后一条消息", item["lastMessage"].GetValue<string>())
        Assert.Equal("idle", item["runtimeState"].GetValue<string>())
        // 有效配置摘要
        let cfgObj = item["config"].AsObject()
        Assert.Equal("openai", cfgObj["provider"].GetValue<string>())
    finally
        cleanup dir

/// 快照消息数组每条应携带全局 commitId 作为消息标识（决策 79），供 fork 点定位。
[<Fact>]
let ``snapshot messages carry commitId per message`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let convId = newConversationId ()
        let commits =
            [ Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
              Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m1" } ]
              Events.Commit.create 3UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = assistantMessageJson "m2" } ] ]
        let proj =
            commits
            |> List.fold (fun p c -> Projection.applyCommit p c |> function Ok p -> p | Error e -> failwith (WanxiangError.message e)) Projection.empty
        let conv = Projection.tryConversation proj convId |> Option.get
        let msgs = ServerModel.conversationMessages proj conv
        Assert.Equal(2, msgs.Count)
        let m0 = msgs[0].AsObject()
        let m1 = msgs[1].AsObject()
        Assert.Equal(2UL, m0["commitId"].GetValue<uint64>())
        Assert.Equal(3UL, m1["commitId"].GetValue<uint64>())
        // payload 内嵌完整消息
        Assert.NotNull m0["payload"]
    finally
        cleanup dir

/// fork 规划：forkAfterId 必须是父会话可见消息的 commitId（决策 75/79）。
[<Fact>]
let ``fork plan requires fork point to be a message commit id`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        let convId = newConversationId ()
        // 创建会话 + 一条消息
        let c1 =
            coord.Submit
                { events = [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
                  commandId = None; commandType = None; commandHash = None; nowUtc = None }
        let c2 =
            coord.Submit
                { events = [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m1" } ]
                  commandId = None; commandType = None; commandHash = None; nowUtc = None }
        match c1, c2 with
        | Committed _, Committed _ -> ()
        | _ -> failwith "setup commits failed"
        let forkId = newConversationId ()
        // 合法：forkAfterId = 消息 commitId (2)
        let okPlan =
            CommandEngine.plan coord.Projection 2UL
                (ForkConversation {| invocationId = Guid.NewGuid(); conversationId = forkId; parentConversationId = convId; forkAfterId = Some 2UL; config = testConfig (); editedMessageJson = userMessageJson "编辑后" |})
        match okPlan with
        | Planned p -> Assert.Equal(2, p.events.Length) // forked + message
        | r -> failwithf "valid fork should plan, got %A" r
        // 非法：forkAfterId = 会话 lastCommitId（2 之后无消息提交，用 999 模拟非消息 id）
        let badPlan =
            CommandEngine.plan coord.Projection 2UL
                (ForkConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); parentConversationId = convId; forkAfterId = Some 999UL; config = testConfig (); editedMessageJson = userMessageJson "x" |})
        match badPlan with
        | Rejected (ForkPointInvalid _) -> ()
        | r -> failwithf "invalid fork point should be rejected, got %A" r
        coord.Shutdown()
    finally
        cleanup dir

/// 幂等与冲突（决策 15/Q145）：同 commandId 命中幂等；
/// 不同载荷命令不可能产生相同 commandId（commandId 由载荷确定性派生），
/// 因此 CommandIdConflict 是哈希碰撞/实现错误的防御分支。
[<Fact>]
let ``command id idempotency and conversation id collision`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        let convId = newConversationId ()
        let invId = Guid.NewGuid()
        // 提交第一次
        let cmd1 = CreateConversation {| invocationId = invId; conversationId = convId; title = "A"; config = testConfig () |}
        match CommandEngine.plan coord.Projection 0UL cmd1 with
        | Planned p ->
            coord.Submit { events = p.events; commandId = Some p.commandId; commandType = Some p.commandType; commandHash = Some p.canonicalHash; nowUtc = None } |> ignore
        | r -> failwithf "plan failed: %A" r
        // 同 invocationId + 同载荷 → 幂等命中（网络重试，决策 13/37）
        let cmd3 = CreateConversation {| invocationId = invId; conversationId = convId; title = "A"; config = testConfig () |}
        match CommandEngine.plan coord.Projection 1UL cmd3 with
        | IdempotentReplay cid -> Assert.Equal(1UL, cid)
        | r -> failwithf "expected idempotent replay, got %A" r
        // 不同 invocationId + 同 conversationId → 正常拒绝（id 已占用）
        let cmd2 = CreateConversation {| invocationId = Guid.NewGuid(); conversationId = convId; title = "B"; config = testConfig () |}
        match CommandEngine.plan coord.Projection 1UL cmd2 with
        | Rejected (ConversationIdTaken _) -> ()
        | r -> failwithf "expected conversation id taken, got %A" r
        coord.Shutdown()
    finally
        cleanup dir
