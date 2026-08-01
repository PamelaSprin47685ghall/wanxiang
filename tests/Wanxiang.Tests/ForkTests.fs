module Wanxiang.Tests.ForkTests

open System
open Xunit
open Wanxiang.Core
open Wanxiang.Tests.Helpers

let private projWith (commits: Events.Commit list) : Projection =
    commits
    |> List.fold (fun p c -> Projection.applyCommit p c |> function Ok p -> p | Error e -> failwith (WanxiangError.message e)) Projection.empty

[<Fact>]
let ``test_41581`` () =
    let convId = newConversationId ()
    let msg1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
    let msg2 = Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m1" } ]
    let msg3 = Events.Commit.create 3UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m2" } ]
    // fork：从 convId 继承到 id=2（forkAfterId=2），新消息 m3'
    let forkId = newConversationId ()
    let fork = Events.Commit.create 4UL DateTimeOffset.UtcNow [ ConversationForked { conversationId = forkId; parentConversationId = convId; forkAfterId = Some 2UL } ]
    let forkMsg = Events.Commit.create 5UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = forkId; payloadJson = userMessageJson "m3'" } ]
    // 父分支继续：m4
    let parentMsg4 = Events.Commit.create 6UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m4" } ]
    // 父分支删除 m2
    let parentDel = Events.Commit.create 7UL DateTimeOffset.UtcNow [ MessageDeleted { conversationId = convId; messageCommitId = 3UL } ]

    let proj = projWith [ msg1; msg2; msg3; fork; forkMsg; parentMsg4; parentDel ]

    // 父分支当前可见：m1, m4（m2 已删除）
    let parent = Projection.tryConversation proj convId |> Option.get
    let parentMsgs = Projection.effectiveMessages proj parent |> List.map (fun m -> m.commitId)
    Assert.Equal<CommitId list>([ 2UL; 6UL ], parentMsgs)

    // 子分支：m1, m2（fork 点之前）, m3' —— 父分支之后的 m4 和删除不影响子分支
    let forkConv = Projection.tryConversation proj forkId |> Option.get
    let forkMsgs = Projection.effectiveMessages proj forkConv |> List.map (fun m -> m.commitId)
    Assert.Equal<CommitId list>([ 2UL; 3UL; 5UL ], forkMsgs)

[<Fact>]
let ``test_14283`` () =
    let a = newConversationId ()
    let b = newConversationId ()
    let c = newConversationId ()
    let commits =
        [ Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = a; title = "A"; config = testConfig () } ]
          Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = a; payloadJson = userMessageJson "a1" } ]
          Events.Commit.create 3UL DateTimeOffset.UtcNow [ ConversationForked { conversationId = b; parentConversationId = a; forkAfterId = Some 2UL } ]
          Events.Commit.create 4UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = b; payloadJson = userMessageJson "b1" } ]
          Events.Commit.create 5UL DateTimeOffset.UtcNow [ ConversationForked { conversationId = c; parentConversationId = b; forkAfterId = Some 4UL } ]
          Events.Commit.create 6UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = c; payloadJson = userMessageJson "c1" } ] ]
    let proj = projWith commits
    let cConv = Projection.tryConversation proj c |> Option.get
    let msgs = Projection.effectiveMessages proj cConv |> List.map (fun m -> m.commitId)
    // c: a1(2) + b1(4) + c1(6)
    Assert.Equal<CommitId list>([ 2UL; 4UL; 6UL ], msgs)

[<Fact>]
let ``test_86547`` () =
    let parentId = newConversationId ()
    let commits =
        [ Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = parentId; title = "A"; config = testConfig () } ]
          Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = parentId; payloadJson = userMessageJson "m1" } ] ]
    let proj = projWith commits
    // 无效 fork 点
    let badForkId = newConversationId ()
    let result =
        Projection.applyCommit proj
            (Events.Commit.create 3UL DateTimeOffset.UtcNow [ ConversationForked { conversationId = badForkId; parentConversationId = parentId; forkAfterId = Some 99UL } ])
    match result with
    | Error _ -> ()
    | Ok _ -> failwith "invalid fork point should be rejected"

[<Fact>]
let ``test_9264`` () =
    let a = newConversationId ()
    let b = newConversationId ()
    let commits =
        [ Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = a; title = "A"; config = testConfig () } ]
          Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = a; payloadJson = userMessageJson "a1" } ]
          Events.Commit.create 3UL DateTimeOffset.UtcNow [ ConversationForked { conversationId = b; parentConversationId = a; forkAfterId = Some 2UL } ]
          Events.Commit.create 4UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = b; payloadJson = userMessageJson "b1" } ]
          Events.Commit.create 5UL DateTimeOffset.UtcNow [ EventData.ConversationDeleted { conversationId = a } ] ]
    let proj = projWith commits
    let bConv = Projection.tryConversation proj b |> Option.get
    let msgs = Projection.effectiveMessages proj bConv |> List.map (fun m -> m.commitId)
    Assert.Equal<CommitId list>([ 2UL; 4UL ], msgs)
