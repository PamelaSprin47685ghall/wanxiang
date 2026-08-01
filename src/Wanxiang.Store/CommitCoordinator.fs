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

/// Commit Coordinator 与 NDJSON 单写者是同一进程内组件（决策 39）。
/// 独占：分配连续 id、选择 UTC 日期文件、序列化原子提交、append+flush、
/// 按日志顺序更新内存投影、向观察者发布权威增量。
/// 所有永久状态变化必须经由此单写者。
type CommitCoordinator(dataDir: string, outcome: ReplayOutcome, onCommitted: Events.Commit -> unit, onTruncated: Events.Commit * WanxiangError -> unit) =

    let writer = new NdjsonWriter(dataDir, outcome.lastDateUtc)

    // 以下状态只在 mailbox 处理线程内访问
    let mutable projection: Projection = outcome.projection
    let mutable nextId: CommitId = outcome.lastCommitId + 1UL
    let mutable commits: Events.Commit list = outcome.commits
    let mutable stopped = false

    let mailbox = MailboxProcessor.Start(fun inbox ->
        let doSubmit (submit: CommitSubmit) : SubmitResult =
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
                    onTruncated(commit, Poisoned(sprintf "append failed: %s" e.Message))
                    -1L
            if offsetBefore < 0L then
                CommitFailed(Poisoned "append failed")
            else
                match Projection.applyCommit projection commit with
                | Ok newProj ->
                    projection <- newProj
                    commits <- commits @ [ commit ]
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
                    reply.Reply(commits |> List.filter (fun c -> c.id > cursor))
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
