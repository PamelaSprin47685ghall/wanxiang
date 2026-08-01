module Wanxiang.Tests.ReplayTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

[<Fact>]
let ``test_replay_tail_truncate`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        // 手工写两条提交
        let commit1 = Events.Commit.create 1UL (DateTimeOffset.UtcNow) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let convId =
            match commit1.events.Head with
            | ConversationCreated d -> d.conversationId
            | _ -> Guid.Empty
        let commit2 = Events.Commit.create 2UL (DateTimeOffset.UtcNow) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "你好" } ]
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, CommitCodec.commitToJsonLine commit1 + "\n")
        File.AppendAllText(path, CommitCodec.commitToJsonLine commit2 + "\n")

        match Replay.replay dir false with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(2UL, outcome.lastCommitId)
            Assert.Equal(1, Projection.conversationList outcome.projection |> List.length)
            let conv = Projection.conversationList outcome.projection |> List.head
            Assert.Equal(1, conv.messages.Length)
    finally
        cleanup dir

[<Fact>]
let ``test_replay_id_gap`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, CommitCodec.commitToJsonLine c1 + "\n")
        File.AppendAllText(path, """{"formatVersion":1,"id":2,"broken""")
        match Replay.replay dir true with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(1UL, outcome.lastCommitId)
            Assert.True(outcome.truncatedFiles |> List.exists (fun f -> f = path))
            // 文件已被截断：重新 replay 应无损坏
            match Replay.replay dir false with
            | Ok outcome2 -> Assert.Equal(1UL, outcome2.lastCommitId)
            | Error e -> failwith e
    finally
        cleanup dir

[<Fact>]
let ``id 空洞触发截尾`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c3 = Events.Commit.create 3UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "C"; config = testConfig () } ]
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, CommitCodec.commitToJsonLine c1 + "\n")
        File.AppendAllText(path, CommitCodec.commitToJsonLine c3 + "\n")
        match Replay.replay dir true with
        | Error e -> failwith e
        | Ok outcome -> Assert.Equal(1UL, outcome.lastCommitId)
    finally
        cleanup dir

[<Fact>]
let ``跨 UTC 日期文件按序重放且 id 不归零`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let day1 = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let day2 = DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day1)) [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL (DateTimeOffset(day2)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
        let p1 = DataPaths.eventFilePath dir day1
        let p2 = DataPaths.eventFilePath dir day2
        Directory.CreateDirectory(Path.GetDirectoryName p1) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName p2) |> ignore
        File.AppendAllText(p1, CommitCodec.commitToJsonLine c1 + "\n")
        File.AppendAllText(p2, CommitCodec.commitToJsonLine c2 + "\n")
        match Replay.replay dir false with
        | Error e -> failwith e
        | Ok outcome ->
            Assert.Equal(2UL, outcome.lastCommitId)
            Assert.Equal(2, Projection.conversationList outcome.projection |> List.length)
    finally
        cleanup dir

[<Fact>]
let ``doctor 只读模式不修改损坏日志`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let c1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = newConversationId (); title = "A"; config = testConfig () } ]
        let path = DataPaths.eventFilePath dir DateTime.UtcNow.Date
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.AppendAllText(path, CommitCodec.commitToJsonLine c1 + "\n")
        File.AppendAllText(path, "garbage\n")
        match Replay.replay dir false with
        | Ok _ -> failwith "should have reported damage"
        | Error e -> Assert.Contains("damaged", e)
        // 文件未被修改
        Assert.Contains("garbage", File.ReadAllText path)
    finally
        cleanup dir
