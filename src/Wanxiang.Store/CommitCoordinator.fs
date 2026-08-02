namespace Wanxiang.Store

open System
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

/// Commit Coordinator 与 NDJSON 单写者是同一进程内组件（决策 39）。
/// 独占：分配连续 id、选择 UTC 日期文件、序列化原子提交、append+flush、
/// 按日志顺序更新内存投影、向观察者发布权威增量。
/// 所有永久状态变化必须经由此单写者。
/// 幂等边界（决策 13/15）：命令入口（CommandEngine.plan）与单写者内部双重检查。
/// 单写者内部检查覆盖"plan 之后、提交之前"的竞态窗口（如排队消息 DrainQueue），
/// 命中时返回 IdempotentReplay，不追加事件。
type CommitCoordinator(dataDir: string, outcome: ReplayOutcome, onCommitted: Events.Commit -> unit, onTruncated: Events.Commit * WanxiangError -> unit) =

    let writer = new NdjsonWriter(dataDir, outcome.lastDateUtc)

    // 以下状态只在 mailbox 处理线程内访问
    let mutable projection: Projection = outcome.projection
    let mutable nextId: CommitId = outcome.lastCommitId + 1UL
    /// 已提交序列（按 id 升序；ResizeArray 避免 list 追加 O(n²)，只在 mailbox 线程访问）
    let commits = System.Collections.Generic.List<Events.Commit>(outcome.commits)
    let mutable stopped = false

    let mailbox = MailboxProcessor.Start(fun inbox ->
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
                            Some(TruncatedAndReused(Events.Commit.create nextId DateTimeOffset.UtcNow submit.events,
                                 Poisoned(sprintf "idempotency record %s references missing commit %d" cid idemRec.commitId)))
                    | Some _ ->
                        // 同一 commandId 对应不同规范化载荷：严重冲突，拒绝（决策 15/Q145）。
                        // stderr 由 ServerApp 的 command-id-conflict 事件记录（Q145），此处不重复写截尾事件。
                        let conflictErr = CommandIdConflict(sprintf "commandId %s reused with different payload" cid)
                        let conflictCommit = Events.Commit.create nextId DateTimeOffset.UtcNow submit.events
                        Some(TruncatedAndReused(conflictCommit, conflictErr))
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
                    onTruncated(commit, Poisoned(sprintf "append failed: %s" e.Message))
                    -1L
            if offsetBefore < 0L then
                CommitFailed(Poisoned "append failed")
            else
                match Projection.applyCommit projection commit with
                | Ok newProj ->
                    projection <- newProj
                    commits.Add commit
                    nextId <- nextId + 1UL
                    onCommitted commit
                    Committed commit
                | Error err ->
                    // 决策 40：运行时投影失败 -> 截掉尾行、复用 id；stderr 忠实记录（调用方处理）
                    try
                        writer.TruncateTo offsetBefore
                        onTruncated(commit, err)
                        TruncatedAndReused(commit, err)
                    with e ->
                        // 无法截尾：poison 标记并继续（决策 40：截尾失败才 poison）
                        onTruncated(commit, Poisoned(sprintf "projection failed (%s); truncation failed: %s" (WanxiangError.message err) e.Message))
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
                | GetCommitsAfter(cursor, reply) ->
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
                    reply.Reply([ for i = idx to commits.Count - 1 do commits[i] ])
                    return! loop ()
                | FlushAndStop reply ->
                    writer.Dispose()
                    reply.Reply()
            }
        loop ())

    /// 提交一条规划好的命令事件集（同步等待结果）。
    member _.Submit(submit: CommitSubmit) : SubmitResult =
        mailbox.PostAndReply(fun ch -> Submit(submit, ch))

    /// 提交一组不含命令标识的事件（如 Agent 完整消息记账）。
    member _.SubmitEvents(events: EventData list) : SubmitResult =
        mailbox.PostAndReply(fun ch -> Submit({ events = events; commandId = None; commandType = None; commandHash = None; nowUtc = None }, ch))

    /// 当前投影快照（不可变）。
    member _.Projection : Projection =
        mailbox.PostAndReply(GetProjection)

    /// 返回 replay 加载的有效提交，用于连接断线后的权威 catch-up。
    member _.CommitsAfter(cursor: CommitId) : Events.Commit list =
        mailbox.PostAndReply(fun ch -> GetCommitsAfter(cursor, ch))

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
    | GetCommitsAfter of CommitId * AsyncReplyChannel<Events.Commit list>
    | FlushAndStop of AsyncReplyChannel<unit>
