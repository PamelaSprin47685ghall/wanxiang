module Wanxiang.Tests.SnapshotTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// S-003：快照（算法 S / 算法 L）。
/// - 快照 = 冻结投影副本 + checksum；不删除任何事件日志。
/// - 启动优先快照（L7 只重放 throughCommitId 之后）；损坏/超前回退完整重放（L8/L9）。
module Snapshot =

    let private writeCommits (dir: string) (day: DateTime) (commits: Events.Commit list) =
        let path = DataPaths.logFile dir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let bytes = commits |> List.map (fun c -> Wanxiang.Store.Assembly.frame (c)) |> Array.concat
        File.WriteAllBytes(path, bytes)
        ()

    [<Fact>]
    let ``快照写读回环：投影完整恢复`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "快照"; config = testConfig () } ]
            let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddHours 1.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "你好" } ]
            writeCommits dir day [ c1; c2 ]
            // 完整重放得到投影
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            // 写快照（锚点 = 当前文件 + 末尾偏移）
            match Snapshot.write dir outcome.checkpoint with
            | Error e -> failwith e
            | Ok path ->
                Assert.True(File.Exists path)
                // R5：写快照不得残留 `.tmp-*` 临时文件（失败路径清理 + 成功路径 Move 走）
                let tmpLeftovers =
                    Directory.GetFiles(Path.Combine(dir, "snapshots"), "*.tmp")
                Assert.Empty(tmpLeftovers)
                // 读回
                match Snapshot.findLatest dir with
                | SnapshotStartup.SnapshotUsed info -> 
                    Assert.Equal(2UL, info.throughCommitId)
                    Assert.Equal(1, info.projection.conversations.Count)
                    let conv = Projection.tryConversation info.projection convId
                    Assert.True conv.IsSome
                    Assert.Equal(1, conv.Value.messages.Length)
                    // 快照不删除任何事件日志
                    Assert.True(File.Exists(DataPaths.logFile dir))
                | other -> failwithf "expected SnapshotUsed, got %A" other
        finally
            cleanup dir

    [<Fact>]
    let ``快照校验和被篡改时拒绝加载`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            DataPaths.ensureDataDirs dir |> ignore
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            writeCommits dir day [ c1 ]
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            match Snapshot.write dir outcome.checkpoint with
            | Error e -> failwith e
            | Ok path ->
                // 篡改投影内容
                let text = File.ReadAllText path
                let tampered = text.Replace("\"title\":\"A\"", "\"title\":\"B\"")
                Assert.NotEqual<string>(text, tampered)
                File.WriteAllText(path, tampered)
                match Snapshot.findLatest dir with
                | SnapshotStartup.SnapshotCorrupt reasons -> Assert.Contains("checksum", String.concat "; " reasons)
                | other -> failwithf "tampered snapshot must be rejected, got %A" other
        finally
            cleanup dir

    [<Fact>]
    let ``全部快照损坏时返回 SnapshotCorrupt`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            DataPaths.ensureDataDirs dir |> ignore
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            writeCommits dir day [ c1 ]
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            match Snapshot.write dir outcome.checkpoint with
            | Error e -> failwith e
            | Ok path ->
                File.WriteAllText(path, "garbage")
                match Snapshot.findLatest dir with
                | SnapshotCorrupt reasons -> Assert.NotEmpty reasons
                | other -> failwithf "expected SnapshotCorrupt, got %A" other
        finally
            cleanup dir

    [<Fact>]
    let ``快照启动：只重放 throughCommitId 之后的日志，投影与完整重放一致`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            DataPaths.ensureDataDirs dir |> ignore
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "快照启动"; config = testConfig () } ]
            let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddHours 1.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "你好" } ]
            writeCommits dir day [ c1; c2 ]
            // 完整重放（基准）
            let fullOutcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            Assert.Equal(2UL, fullOutcome.lastCommitId)
            // 模拟：c1 之后创建快照（throughCommitId=1）
            let proj1 =
                // 快照时刻 = c1 之后（对话已创建，尚无消息）
                let conv =
                    { conversationId = convId
                      title = "快照启动"
                      createdAtUtc = DateTimeOffset(day)
                      lastActivityAtUtc = DateTimeOffset(day)
                      deleted = false
                      pinned = false
                      archived = false
                      parent = None
                      forkBaseCommitId = None
                      config = testConfig ()
                      messages = []
                      lastCommitId = Some 1UL }
                { conversations = Map.ofList [ convId, conv ]
                  latestCommitId = 1UL }
            // throughOffset = c1 帧末尾 = c2 起点（c1 已折叠进快照，c2 待续播）
            let c1FrameLen = int64 (Wanxiang.Store.Assembly.frame (c1)).Length
            match Snapshot.write dir ({ state = proj1; throughId = proj1.latestCommitId; stamps = Map.empty; positions = Wanxiang.Store.Assembly.read (DataPaths.logFile dir) 0L (Some(c1FrameLen)) |> List.map (fun r -> r.startOffset, r.endOffset) }: Wanxiang.Store.Checkpoint) with
            | Error e -> failwith e
            | Ok _ ->
                // 快照启动：从 throughCommitId+1=2 续播
                let snap =
                    match Snapshot.findLatest dir with
                    | SnapshotUsed info -> info
                    | other -> failwithf "expected SnapshotUsed, got %A" other
                match Replay.inspectFrom dir snap.checkpoint with
                | Healthy outcome ->
                    Assert.Equal(2UL, outcome.lastCommitId)
                    // 投影与完整重放一致（场景 A）
                    let fullConv = Projection.conversationList fullOutcome.projection |> List.head
                    let snapConv = Projection.conversationList outcome.projection |> List.head
                    Assert.Equal(fullConv.messages.Length, snapConv.messages.Length)
                    Assert.Equal(1, snapConv.messages.Length)
                | _ -> failwith "snapshot-based replay must be healthy"
        finally
            cleanup dir

    [<Fact>]
    let ``快照启动续播时 id 空洞仍被检测`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            DataPaths.ensureDataDirs dir |> ignore
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            writeCommits dir day [ c1 ]
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            let proj1 =
                let conv =
                    match Projection.tryConversation outcome.projection convId with
                    | Some c -> c
                    | None -> failwith "missing"
                { conversations = Map.ofList [ convId, conv ]
                  latestCommitId = 1UL }
            // throughOffset = c1 帧末尾（c1 已折叠进快照；后续追加内容待续播）
            let c1FrameLen = int64 (Wanxiang.Store.Assembly.frame (c1)).Length
            match Snapshot.write dir ({ state = proj1; throughId = proj1.latestCommitId; stamps = Map.empty; positions = Wanxiang.Store.Assembly.read (DataPaths.logFile dir) 0L (Some(c1FrameLen)) |> List.map (fun r -> r.startOffset, r.endOffset) }: Wanxiang.Store.Checkpoint) with
            | Error e -> failwith e
            | Ok _ ->
                // 续播文件：id 3（缺 2）
                let c3 = Events.Commit.create 3UL (DateTimeOffset(day.AddHours 2.0)) [ ConversationCreated { conversationId = newConversationId (); title = "C"; config = testConfig () } ]
                File.AppendAllText(DataPaths.logFile dir, Wanxiang.Store.Assembly.frame (c3) |> Array.map byte |> fun b -> System.Text.Encoding.UTF8.GetString b)
                let snap =
                    match Snapshot.findLatest dir with
                    | SnapshotUsed info -> info
                    | other -> failwithf "expected SnapshotUsed, got %A" other
                match Replay.inspectFrom dir snap.checkpoint with
                | RepairRequired (outcome, _) -> Assert.Equal(1UL, outcome.lastCommitId)
                | _ -> failwith "id gap after snapshot must be detected"
        finally
            cleanup dir
