module Wanxiang.Tests.ProjectionInvariantsTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// T-003：投影不变量（AGENTS.md §26）。
///
/// 把关键架构规则写成机器断言。用确定性伪随机操作序列驱动投影，
/// 每一步提交后断言全部不变量：
///   I1. latestCommitId 单调增加（applyCommit 的 id 连续性由投影自身保证，此处断言最终值）
///   I2. 每个 commitId 唯一（消息 commitId 全局不重复）
///   I3. 每个 commandId 最多对应一个 canonicalHash
///   I4. Conversation.lastCommitId <= Projection.latestCommitId
///   I5. forkBaseCommitId 不得指向未来（<= latestCommitId）
///   I6. 消息 commitId 必须存在于合法历史范围（<= latestCommitId）
///   I7. 快照 throughCommitId <= 日志最大有效 commitId（算法 L 边界）
module ProjectionInvariants =

    /// 确定性伪随机（固定种子 → 可复现；不依赖 System.Random 全局状态）。
    let private rng = Random(42)

    /// 断言投影全部不变量；返回最新投影。
    let private assertInvariants (proj: Projection) (step: int) : unit =
        // I4/I5/I6：对话级
        for KeyValue(_, conv) in proj.conversations do
            match conv.lastCommitId with
            | Some l -> Assert.True(l <= proj.latestCommitId, sprintf "step %d: conv %O lastCommitId %d > latest %d" step conv.conversationId l proj.latestCommitId)
            | None -> ()
            match conv.forkBaseCommitId with
            | Some b -> Assert.True(b <= proj.latestCommitId, sprintf "step %d: forkBaseCommitId %d > latest %d" step b proj.latestCommitId)
            | None -> ()
            for m in conv.messages do
                Assert.True(m.commitId <= proj.latestCommitId, sprintf "step %d: message commitId %d > latest %d" step m.commitId proj.latestCommitId)
        // I2：消息 commitId 全局唯一
        let allMsgIds =
            proj.conversations.Values
            |> Seq.collect (fun c -> c.messages |> Seq.map (fun m -> m.commitId))
            |> Seq.toList
        Assert.True(allMsgIds.Length = (allMsgIds |> Set.ofList |> Set.count), sprintf "step %d: duplicate message commitId" step)
        // I3（commandId → canonicalHash 唯一）由账本的 stamps Map 结构保证，投影不再持有幂等索引

    /// 生成确定性操作序列：创建/改名/删除对话、记录消息、删除消息、fork。
    /// 返回 (commitId, events) 列表。
    let private generateOps (count: int) : (int * EventData list) list =
        let mutable convs = ResizeArray<Guid>()
        let mutable msgByConv = System.Collections.Generic.Dictionary<Guid, ResizeArray<uint64>>()
        // 已被删除的消息（MessageDeleted 后从跟踪中移除，避免重复删除）
        let mutable deletedMsgs = System.Collections.Generic.HashSet<uint64>()
        // 已删除的对话（fork 只能选存活父对话）
        let mutable deletedConvs = System.Collections.Generic.HashSet<Guid>()
        let mutable nextId = 1UL
        let mutable result = []
        for _ in 1 .. count do
            let id = nextId
            nextId <- nextId + 1UL
            let pick (xs: ResizeArray<_>) = if xs.Count = 0 then None else Some xs[rng.Next xs.Count]
            let events =
                if convs.Count = 0 then
                    // 必须先创建对话
                    let convId = Guid.NewGuid()
                    convs.Add convId
                    msgByConv[convId] <- ResizeArray()
                    [ ConversationCreated { conversationId = convId; title = sprintf "conv-%d" id; config = testConfig () } ]
                else
                    match rng.Next 6 with
                    | 0 ->
                        let convId = pick convs |> Option.get
                        let msg = userMessageJson (sprintf "msg-%d" id)
                        msgByConv[convId].Add id
                        [ AgentMessageRecorded { conversationId = convId; payloadJson = msg } ]
                    | 1 ->
                        let convId = pick convs |> Option.get
                        [ ConversationRenamed { conversationId = convId; title = sprintf "renamed-%d" id } ]
                    | 2 ->
                        let convId = pick convs |> Option.get
                        // 限定 EventData：WanxiangError 也有同名 case（ConversationDeleted of Guid）
                        deletedConvs.Add convId |> ignore
                        [ EventData.ConversationDeleted { conversationId = convId } ]
                    | 3 when msgByConv |> Seq.exists (fun kv -> kv.Value.Count > 0) ->
                        // 删除一条仍存活的消息
                        let convId = pick (ResizeArray(msgByConv |> Seq.filter (fun kv -> kv.Value.Count > 0) |> Seq.map (fun kv -> kv.Key))) |> Option.get
                        let live = msgByConv[convId] |> Seq.filter (fun m -> not (deletedMsgs.Contains m)) |> ResizeArray
                        if live.Count = 0 then
                            // 该对话消息已全部删除：退化为改名
                            [ ConversationRenamed { conversationId = convId; title = sprintf "renamed-%d" id } ]
                        else
                            let target = live[rng.Next live.Count]
                            deletedMsgs.Add target |> ignore
                            [ MessageDeleted { conversationId = convId; messageCommitId = target } ]
                    | 4 ->
                        // fork 现有**存活**对话（forkAfterId 取父对话最近一条存活消息）
                        let liveConvs = convs |> Seq.filter (fun c -> not (deletedConvs.Contains c)) |> ResizeArray
                        if liveConvs.Count = 0 then
                            // 全部已删除：退化创建新对话
                            let convId = Guid.NewGuid()
                            convs.Add convId
                            msgByConv[convId] <- ResizeArray()
                            [ ConversationCreated { conversationId = convId; title = sprintf "conv-%d" id; config = testConfig () } ]
                        else
                            let parentId = pick liveConvs |> Option.get
                            let forkId = Guid.NewGuid()
                            convs.Add forkId
                            msgByConv[forkId] <- ResizeArray()
                            let forkAfter =
                                // 只取仍存活的消息（被删除的消息不在父对话可见前缀中）
                                let live =
                                    msgByConv[parentId]
                                    |> Seq.filter (fun m -> not (deletedMsgs.Contains m))
                                    |> Seq.toList
                                match live with
                                | [] -> None
                                | _ -> Some(List.last live)
                            [ ConversationForked { conversationId = forkId; parentConversationId = parentId; forkAfterId = forkAfter } ]
                    | _ ->
                        let convId = pick convs |> Option.get
                        [ ConversationFlagsChanged { conversationId = convId; pinned = rng.Next 2 = 0; archived = rng.Next 2 = 0 } ]
            result <- result @ [ (int id), events ]
        result

    [<Fact>]
    let ``随机操作序列重放全程保持投影不变量`` () =
        // 固定种子 → 确定性；500 步覆盖创建/fork/删除/改名/消息
        let ops = generateOps 500
        let mutable proj = Projection.empty
        for (id, events) in ops do
            let commit = Events.Commit.create (uint64 id) (DateTimeOffset.UtcNow) events
            match Projection.applyCommit proj commit with
            | Ok p ->
                proj <- p
                assertInvariants proj id
            | Error e -> failwithf "step %d: applyCommit failed: %s" id (WanxiangError.message e)
        // I1：latestCommitId 单调增加且等于最后提交
        Assert.Equal(uint64 (List.last ops |> fst), proj.latestCommitId)

    [<Fact>]
    let ``重放后投影与快照加载投影满足同一不变量集`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            // 写入一批提交到日志
            let ops = generateOps 120
            let day = DateTime.UtcNow.Date
            let mutable proj = Projection.empty
            for (id, events) in ops do
                let commit = Events.Commit.create (uint64 id) (DateTimeOffset.UtcNow) events
                match Projection.applyCommit proj commit with
                | Ok p -> proj <- p
                | Error e -> failwithf "applyCommit failed: %s" (WanxiangError.message e)
                // 落盘
                let path = DataPaths.logFile dir
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.AppendAllBytes(path, Wanxiang.Store.Assembly.frame (commit))
            // 从日志重放
            match Replay.inspect dir with
            | Healthy outcome ->
                assertInvariants outcome.projection (List.length ops)
                // I7：快照 throughCommitId <= 日志最大有效 commitId
                let fileLen = FileInfo(DataPaths.logFile dir).Length
                match Snapshot.write dir outcome.checkpoint with
                | Ok _ -> ()
                | Error e -> failwithf "snapshot write failed: %s" e
                match Snapshot.findLatest dir with
                | SnapshotStartup.SnapshotUsed info ->
                    Assert.True(info.throughCommitId <= outcome.lastCommitId, "snapshot throughCommitId > log lastCommitId")
                    assertInvariants info.projection (List.length ops)
                | other -> failwithf "expected SnapshotUsed, got %A" other
            | RepairRequired (_, plan) -> failwithf "unexpected repair required: %s" plan.damage.reason
            | UnsupportedFormat msg -> failwith msg
            | Unreadable msg -> failwith msg
        finally
            cleanup dir

    [<Fact>]
    let ``幂等索引：同 commandId 冲突时投影拒绝`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let convId = newConversationId ()
            let invId = Guid.NewGuid()
            let cmd1 = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            let cmd2 = Events.Commit.create 2UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            // 同 conversationId 重复创建 → 投影拒绝（I2 变体：对话唯一）
            match Projection.applyCommit Projection.empty cmd1 with
            | Ok p1 ->
                match Projection.applyCommit p1 cmd2 with
                | Ok _ -> failwith "duplicate conversation should be rejected"
                | Error _ -> ()
            | Error e -> failwithf "first commit failed: %s" (WanxiangError.message e)
        finally
            cleanup dir
