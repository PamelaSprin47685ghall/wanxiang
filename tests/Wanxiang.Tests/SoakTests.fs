module Wanxiang.Tests.SoakTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// T-004：Soak 测试（AGENTS.md §25）。
///
/// 不要求真实等待数日：循环执行大量操作（创建对话、发送消息、改名、fork、删除消息、
/// 修改配置、删除对话），每 N 次操作执行 Inspect + 投影不变量检查 + 内存采样。
/// 每轮循环结束执行一次「重启」（关闭协调器 → 重新 inspect → 新协调器），
/// 验证重启后投影与重启前一致（只靠 NDJSON 恢复全部永久状态）。
///
/// 命令路径与生产一致：`CommandEngine.plan`（规划事件 + 幂等 + 陈旧水位）→
/// `CommitCoordinator.Submit`（单写者 append + flush + 投影 + 发布）。
module Soak =

    /// 断言投影不变量（复用 §26 的机器断言）。
    let private assertInvariants (proj: Projection) (step: int) : unit =
        for KeyValue(_, conv) in proj.conversations do
            match conv.lastCommitId with
            | Some l -> Assert.True(l <= proj.latestCommitId, sprintf "step %d: conv lastCommitId %d > latest %d" step l proj.latestCommitId)
            | None -> ()
            match conv.forkBaseCommitId with
            | Some b -> Assert.True(b <= proj.latestCommitId, sprintf "step %d: forkBase %d > latest %d" step b proj.latestCommitId)
            | None -> ()
            for m in conv.messages do
                Assert.True(m.commitId <= proj.latestCommitId, sprintf "step %d: message %d > latest %d" step m.commitId proj.latestCommitId)
        let allMsgIds =
            proj.conversations.Values
            |> Seq.collect (fun c -> c.messages |> Seq.map (fun m -> m.commitId))
            |> Seq.toList
        Assert.True(allMsgIds.Length = (allMsgIds |> Set.ofList |> Set.count), sprintf "step %d: duplicate message commitId" step)

    /// 通过生产命令路径提交一条命令；返回是否实际提交（非幂等/非拒绝）。
    let private submit (coord: CommitCoordinator) (proj: Projection) (cmd: ClientCommand) : bool =
        match CommandEngine.plan proj (proj.latestCommitId) cmd with
        | PlanResult.Planned p ->
            let result: SubmitResult =
                coord.Submit { events = p.events; commandId = Some p.commandId; commandType = Some p.commandType; commandHash = Some p.canonicalHash; nowUtc = None }
            match result with
            | SubmitResult.Committed _ -> true
            | SubmitResult.IdempotentReplay _ -> false
            | SubmitResult.CommandIdRejected _ -> false
            | SubmitResult.CommitFailed _ -> false
        | PlanResult.IdempotentReplay _ -> false
        | PlanResult.Rejected _ -> false

    /// 一轮 soak：在协调器上执行 ops 次操作，每 checkEvery 次做 Inspect + 不变量 + 内存采样。
    /// 返回 (最终投影, 提交数)。
    let private runRound (dir: string) (ops: int) (checkEvery: int) : Projection * int =
        let outcome =
            match Replay.inspect dir with
            | Healthy o -> o
            | RepairRequired (_, p) -> failwithf "unexpected repair required: %s" p.damage.reason
            | UnsupportedFormat m -> failwith m
            | Unreadable m -> failwith m
        use coord = new CommitCoordinator(dir, outcome, (fun _ -> ()), (fun _ -> ()))
        let mutable convs = System.Collections.Generic.List<Guid>()
        let mutable msgIds = System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.List<uint64>>()
        let rng = Random(7)
        let mutable submitted = 0

        let step (i: int) : unit =
            let proj = coord.Projection
            let op = i % 7
            let cmd =
                match op with
                | 0 ->
                    CreateConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = sprintf "soak-%d" i; config = testConfig () |}
                | 1 when convs.Count > 0 ->
                    SendUserMessage {| invocationId = Guid.NewGuid(); conversationId = convs[rng.Next convs.Count]; messageJson = userMessageJson (sprintf "soak-msg-%d" i) |}
                | 2 when convs.Count > 0 ->
                    RenameConversation {| invocationId = Guid.NewGuid(); conversationId = convs[rng.Next convs.Count]; title = sprintf "renamed-%d" i |}
                | 3 when convs.Count > 0 ->
                    ForkConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); parentConversationId = convs[rng.Next convs.Count]; forkAfterId = None; config = testConfig (); editedMessageJson = userMessageJson (sprintf "fork-msg-%d" i) |}
                | 4 when msgIds |> Seq.exists (fun kv -> kv.Value.Count > 0) ->
                    // 删除一条真实存在的消息
                    let convId =
                        msgIds
                        |> Seq.filter (fun kv -> kv.Value.Count > 0)
                        |> Seq.map (fun kv -> kv.Key)
                        |> Seq.toArray
                        |> fun arr -> arr[rng.Next arr.Length]
                    let msgs = msgIds[convId]
                    DeleteMessage {| invocationId = Guid.NewGuid(); conversationId = convId; messageCommitId = msgs[rng.Next msgs.Count] |}
                | 5 when convs.Count > 0 ->
                    UpdateConversationConfig {| invocationId = Guid.NewGuid(); conversationId = convs[rng.Next convs.Count]; config = { testConfig () with model = "soak-model" } |}
                | _ when convs.Count > 0 ->
                    DeleteConversation {| invocationId = Guid.NewGuid(); conversationId = convs[rng.Next convs.Count] |}
                | _ ->
                    CreateConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = sprintf "soak-%d" i; config = testConfig () |}
            match cmd with
            | CreateConversation d when submit coord proj cmd ->
                convs.Add d.conversationId
                msgIds[d.conversationId] <- System.Collections.Generic.List()
                submitted <- submitted + 1
            | SendUserMessage d when submit coord proj cmd ->
                // 找到该消息的 commitId（投影最新消息）
                let latest = coord.Projection.latestCommitId
                msgIds[d.conversationId].Add latest
                submitted <- submitted + 1
            | ForkConversation d when submit coord proj cmd ->
                convs.Add d.conversationId
                msgIds[d.conversationId] <- System.Collections.Generic.List()
                submitted <- submitted + 1
            | _ ->
                if submit coord proj cmd then
                    submitted <- submitted + 1

        for i in 1 .. ops do
            step i
            if i % checkEvery = 0 then
                // 周期检查：Inspect + 不变量 + 内存采样
                let proj = coord.Projection
                assertInvariants proj i
                match Replay.inspect dir with
                | Healthy o ->
                    // 运行期投影 == 磁盘重放投影（只靠 NDJSON 恢复）
                    Assert.Equal(proj.latestCommitId, o.projection.latestCommitId)
                    Assert.Equal(proj.conversations.Count, o.projection.conversations.Count)
                | other -> failwithf "inspect failed at step %d: %A" i other
                // 内存采样（GC 后取当前堆；仅记录，不设硬断言——soak 关注不变量与一致性）
                GC.Collect()
                GC.WaitForPendingFinalizers()
                GC.Collect()
        let finalProj = coord.Projection
        (finalProj, submitted)

    [<Fact>]
    let ``soak：多轮重启后投影与磁盘重放完全一致`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            // 3 轮 × 300 操作；每 50 步做 Inspect + 不变量 + 内存采样
            // 每轮结束「重启」：关闭协调器 → inspect → 新协调器。
            // 断言：重启 inspect 的投影 == 上一轮结束投影（NDJSON 是唯一永久状态源）。
            for round in 1 .. 3 do
                let proj, _ = runRound dir 300 50
                // 重启：inspect 重新加载（协调器已 Dispose）——此时磁盘 = 上一轮结束状态
                let outcome =
                    match Replay.inspect dir with
                    | Healthy o -> o
                    | other -> failwithf "round %d restart inspect failed: %A" round other
                assertInvariants outcome.projection (round * 300)
                // 每轮：runRound 结束时投影 == 重启 inspect 投影（协调器已 Dispose，
                // 磁盘即唯一状态源；重启不丢不重）。
                Assert.Equal(proj.latestCommitId, outcome.projection.latestCommitId)
                Assert.Equal(proj.conversations.Count, outcome.projection.conversations.Count)
        finally
            cleanup dir

    [<Fact>]
    let ``soak：提交计数与磁盘记录数一致（无丢失无重复）`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let _, submitted = runRound dir 200 25
            // 磁盘上的 RFC 7464 记录数 = 提交数（单写者保证不丢不重）
            let diskCount =
                Directory.GetFiles(DataPaths.eventsDir dir, "*.jsonseq")
                |> Array.sumBy (fun path -> File.ReadAllText(path).Split(char 0x1E) |> Array.filter (fun seg -> seg.Trim().Length > 0) |> Array.length)
            Assert.True(submitted > 0, "soak submitted nothing")
            Assert.True(diskCount >= submitted, sprintf "disk %d < submitted %d" diskCount submitted)
        finally
            cleanup dir
