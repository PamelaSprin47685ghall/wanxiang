namespace Wanxiang.Store

open System
open System.IO
open Wanxiang.Core

/// 提交请求（由 Server 层规划后送入单写者）。
type CommitSubmit = {
    events: EventData list
    commandId: string option
    commandType: string option
    commandHash: string option
    /// 测试可注入时间；None = UtcNow
    nowUtc: DateTimeOffset option
}

type SubmitResult =
    /// 提交成功（已 append + flush + 投影更新）
    | Committed of Events.Commit
    /// 写入失败（未落盘，不分配 id）
    | CommitFailed of WanxiangError
    /// 已落盘但投影失败：运行时截尾该行、复用 id（决策 40）；提交对象用于 stderr 忠实记录
    | TruncatedAndReused of Events.Commit * WanxiangError
    /// 同一 commandId 已提交过：幂等命中，返回原提交 id（决策 13/15）
    | IdempotentReplay of Events.Commit
    /// 命令标识冲突/幂等记录异常：未写盘、未截尾，拒绝（Q145；不借用截尾语义）
    | CommandIdRejected of WanxiangError

/// Commit Coordinator 与 NDJSON 单写者是同一进程内组件（决策 39）。
/// 独占：分配连续 id、选择 UTC 日期文件、序列化原子提交、append+flush、
/// 按日志顺序更新内存投影、向观察者发布权威增量。
/// 所有永久状态变化必须经由此单写者。
/// 幂等边界（决策 13/15）：命令入口（CommandEngine.plan）与单写者内部双重检查。
/// 单写者内部检查覆盖"plan 之后、提交之前"的竞态窗口（如排队消息 DrainQueue），
/// 命中时返回 IdempotentReplay，不追加事件。
type CommitCoordinator(dataDir: string, outcome: ReplayOutcome, onCommitted: Events.Commit -> unit, onTruncated: Events.Commit * WanxiangError * int64 * string -> unit) =

    let writer = new JsonSeqWriter(dataDir, outcome.lastDateUtc)

    // 以下状态只在 mailbox 处理线程内访问
    let mutable projection: Projection = outcome.projection
    let mutable nextId: CommitId = outcome.lastCommitId + 1UL
    /// 已提交序列（按 id 升序；ResizeArray 避免 list 追加 O(n²)，只在 mailbox 线程访问）
    let commits = System.Collections.Generic.List<Events.Commit>(outcome.commits)
    let mutable stopped = false

    let mailbox = new MailboxProcessor<_>(fun inbox ->
        let doSubmit (submit: CommitSubmit) : SubmitResult =
            // 幂等下沉检查（决策 13/15）：同一 commandId 已提交过则直接返回原提交，不追加事件。
            // 与 CommandEngine.plan 的检查互补，覆盖 plan 之后、此处之前的并发窗口。
            let idemHit: SubmitResult option =
                match submit.commandId with
                | Some cid ->
                    match Projection.tryIdem projection cid with
                    | Some idemRec when submit.commandHash |> Option.exists (fun h -> h = idemRec.canonicalHash) ->
                        // commits 按 id 升序；二分定位目标 id（幂等命中通常是最新提交附近，二分保证最坏 O(log n)）
                        // 防御：commits 为空（投影有记录但内存缺提交）时直接走 None 分支
                        let found =
                            if commits.Count = 0 then None
                            else
                                let mutable lo = 0
                                let mutable hi = commits.Count - 1
                                let mutable r: Events.Commit option = None
                                while lo <= hi && r.IsNone do
                                    let mid = (lo + hi) / 2
                                    let c = commits[mid]
                                    if c.id = idemRec.commitId then r <- Some c
                                    elif c.id < idemRec.commitId then lo <- mid + 1
                                    else hi <- mid - 1
                                r
                        match found with
                        | Some original -> Some(IdempotentReplay original)
                        | None ->
                            // 投影有记录但内存列表缺该提交（理论上不应发生）；按冲突处理，避免重复提交
                            Some(CommandIdRejected(Poisoned(sprintf "idempotency record %s references missing commit %d" cid idemRec.commitId)))
                    | Some _ ->
                        // 同一 commandId 对应不同规范化载荷：严重冲突，拒绝（决策 15/Q145）。
                        // stderr 由 ServerApp 的 command-id-conflict 事件记录（Q145），此处不重复写截尾事件。
                        let conflictErr = CommandIdConflict(sprintf "commandId %s reused with different payload" cid)
                        Some(CommandIdRejected conflictErr)
                    | None -> None
                | None -> None
            match idemHit with
            | Some r -> r
            | None ->
            let id = nextId
            let nowUtc = submit.nowUtc |> Option.defaultValue DateTimeOffset.UtcNow
            let baseCommit = Events.Commit.create id nowUtc submit.events
            let commit =
                match submit.commandId, submit.commandType, submit.commandHash with
                | Some cid, Some ct, Some ch -> Events.Commit.withCommand cid ct ch baseCommit
                | _ -> baseCommit
            let offsetBefore =
                try
                    writer.AppendCommit commit
                with e ->
                    // 决策 40：append 失败（半行/未 flush）已由写入器回滚偏移，等同截尾复用 id
                    onTruncated(commit, Poisoned(sprintf "append failed: %s" e.Message), -1L, writer.CurrentFilePath)
                    -1L
            if offsetBefore < 0L then
                CommitFailed(Poisoned "append failed")
            else
                // T-002 故障注入：AfterFlushBeforeProjection（记录已落盘，投影未应用）
                if FaultInjector.tryFail FaultInjector.StorageFaultPoint.AfterFlushBeforeProjection then
                    // 模拟「落盘成功但投影前崩溃」：截尾该行复用 id（决策 40 语义）
                    try
                        writer.TruncateTo offsetBefore
                        onTruncated(
                            commit,
                            Poisoned "injected fault: AfterFlushBeforeProjection",
                            offsetBefore,
                            writer.CurrentFilePath)
                        TruncatedAndReused(commit, Poisoned "injected fault: AfterFlushBeforeProjection")
                    with _ ->
                        CommitFailed(Poisoned "injected fault: AfterFlushBeforeProjection (truncate failed)")
                else
                match Projection.applyCommit projection commit with
                | Ok newProj ->
                    projection <- newProj
                    commits.Add commit
                    nextId <- nextId + 1UL
                    // T-002 故障注入：AfterProjectionBeforePublish（投影已应用，发布前失败）
                    if FaultInjector.tryFail FaultInjector.StorageFaultPoint.AfterProjectionBeforePublish then
                        // 投影已更新但对外未发布：返回失败，但投影状态已前进。
                        // 调用方（ServerApp）会看到 CommitFailed；下次提交 id 已 +1（与日志一致）。
                        CommitFailed(Poisoned "injected fault: AfterProjectionBeforePublish")
                    else
                    onCommitted commit
                    Committed commit
                | Error err ->
                    // 决策 40：运行时投影失败 -> 截掉尾行、复用 id；stderr 忠实记录（调用方处理）
                    try
                        // T-002 故障注入：DuringTruncate（截尾失败 → poison）
                        if FaultInjector.tryFail FaultInjector.StorageFaultPoint.DuringTruncate then
                            raise (IOException "injected fault: DuringTruncate")
                        writer.TruncateTo offsetBefore
                        onTruncated(commit, err, offsetBefore, writer.CurrentFilePath)
                        TruncatedAndReused(commit, err)
                    with e ->
                        // 无法截尾：poison 标记并继续（决策 40：截尾失败才 poison）
                        onTruncated(commit, Poisoned(sprintf "projection failed (%s); truncation failed: %s" (WanxiangError.message err) e.Message), offsetBefore, writer.CurrentFilePath)
                        CommitFailed(Poisoned(sprintf "projection failed and truncation failed: %s" e.Message))

        let rec loop () =
            async {
                let! msg = inbox.Receive()
                match msg with
                | Submit(submit, reply) ->
                    reply.Reply(doSubmit submit)
                    return! loop ()
                | GetProjection reply ->
                    reply.Reply projection
                    return! loop ()
                | GetCommitsAfter(cursor, limitOpt, reply) ->
                    // 二分定位游标后截取（避免 O(n) 全量 filter）；游标 = 客户端已应用的最后 id
                    let mutable lo = 0
                    let mutable hi = commits.Count - 1
                    let mutable idx = commits.Count
                    while lo <= hi do
                        let mid = (lo + hi) / 2
                        if commits[mid].id > cursor then
                            idx <- mid
                            hi <- mid - 1
                        else
                            lo <- mid + 1
                    // 分页上限（§13/§22：catch-up 单批有限）：limit = None 保持全量语义
                    // （快照 v2 全量写快照用），Some n 只复制最多 n 条，避免慢客户端
                    // 追赶长历史时每批 O(n) 复制剩余全部（O(n²)）并阻塞单写者 mailbox。
                    let limit = commits.Count - idx
                    let count = match limitOpt with Some n -> min n limit | None -> limit
                    reply.Reply([ for i = idx to idx + count - 1 do commits[i] ])
                    return! loop ()
                | GetAllCommits reply ->
                    // 全量提交（含启动重放 + 本进程提交）：优雅关闭写快照时
                    // 需要把 commit 外壳固化进快照（快照 v2，供快照启动直接加载，
                    // 无需 collect-only 扫描日志）。
                    reply.Reply([ for c in commits -> c ])
                    return! loop ()
                | GetCurrentFile reply ->
                    reply.Reply(writer.CurrentFileName, writer.Position)
                    return! loop ()
                | FlushAndStop reply ->
                    writer.Dispose()
                    reply.Reply()
            }
        loop ())
    do mailbox.Start()

    /// 提交一条规划好的命令事件集（同步等待结果）。
    member _.Submit(submit: CommitSubmit) : SubmitResult =
        mailbox.PostAndReply(fun ch -> Submit(submit, ch))

    /// 提交一组不含命令标识的事件（如 Agent 完整消息记账）。
    member _.SubmitEvents(events: EventData list) : SubmitResult =
        mailbox.PostAndReply(fun ch -> Submit({ events = events; commandId = None; commandType = None; commandHash = None; nowUtc = None }, ch))

    /// 当前投影快照（不可变）。
    member _.Projection : Projection =
        mailbox.PostAndReply(GetProjection)

    /// 返回游标之后的提交，用于连接断线后的权威 catch-up。
    /// limit = None 返回全部剩余（快照 v2 写快照等全量场景）；
    /// Some n 只返回最多 n 条（慢客户端分页追赶，§13/§22 单批有限）。
    member _.CommitsAfter(cursor: CommitId, ?limit: int) : Events.Commit list =
        let limitOpt = limit |> Option.map (fun n -> max 0 n)
        mailbox.PostAndReply(fun ch -> GetCommitsAfter(cursor, limitOpt, ch))

    /// 全部已提交（启动重放 + 本进程提交，按 id 升序）。快照 v2 写快照用。
    member _.AllCommits : Events.Commit list =
        mailbox.PostAndReply(GetAllCommits)

    /// 当前打开的日志文件名与字节长度（快照锚点用；mailbox 线程内读取保证与写入串行）。
    member _.CurrentFileAnchor() : string * int64 =
        mailbox.PostAndReply(GetCurrentFile)

    /// 优雅关闭：排空提交队列、flush、释放日志流。
    member this.Shutdown() : unit =
        if not stopped then
            stopped <- true
            mailbox.PostAndReply(FlushAndStop)
        else
            ()

    interface IDisposable with
        member this.Dispose() = this.Shutdown()

and private CoordinatorMessage =
    | Submit of CommitSubmit * AsyncReplyChannel<SubmitResult>
    | GetProjection of AsyncReplyChannel<Projection>
    | GetCommitsAfter of CommitId * int option * AsyncReplyChannel<Events.Commit list>
    | GetAllCommits of AsyncReplyChannel<Events.Commit list>
    | GetCurrentFile of AsyncReplyChannel<string * int64>
    | FlushAndStop of AsyncReplyChannel<unit>
