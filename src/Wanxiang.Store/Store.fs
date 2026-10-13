namespace Wanxiang.Store

open System
open System.Text.Json.Nodes
open Wanxiang.Core
open LedgerStore

// 领域胶水。组合（写序、健康、只读、修复、快照、锁）全归 Wanxiang2.LedgerStore；
// 这里只有：事件/状态编解码、折叠、WanxiangError 映射、目录选择（dataDir/ledger）。

type Checkpoint = Ledger.Checkpoint<Projection>

module Assembly =

    let toCommit (c: Ledger.Commit<EventData>) : Events.Commit =
        let b = Events.Commit.create c.id c.at c.events
        match c.stamp with
        | Some s -> { b with commandId = Some s.commandId; commandHash = Some s.hash }
        | None -> b

    let ofCommit (c: Events.Commit) : Ledger.Commit<EventData> =
        let stamp =
            match c.commandId, c.commandHash with
            | Some cid, Some h -> Some(({ commandId = cid; hash = h }: Ledger.Stamp))
            | _ -> None
        { id = c.id; at = c.committedAtUtc; stamp = stamp; events = c.events }

    let codec: Codec<Projection, EventData> =
        { encodeEvent = fun e -> CommitCodec.eventDataToJson e :> JsonNode
          decodeEvent =
            fun n ->
                match n with
                | :? JsonObject as o -> (match CommitCodec.tryEventFromJson o with Some e -> Ok e | None -> Error "unknown or malformed event")
                | _ -> Error "event is not an object"
          encodeState = fun p -> ProjectionCodec.toJson p :> JsonNode
          decodeState =
            fun n ->
                match n with
                | :? JsonObject as o -> (match ProjectionCodec.tryFromJson o with Some p -> Ok p | None -> Error "projection malformed")
                | _ -> Error "projection is not an object" }

    let fold (p: Projection) (c: Ledger.Commit<EventData>) : Result<Projection, WanxiangError> =
        Projection.applyCommit p (toCommit c)

    /// 一条提交的外壳文本（测试造数据、诊断用）。
    let envelope (c: Events.Commit) : string =
        ofCommit c |> Ledger.Commit.map codec.encodeEvent |> Ledger.Envelope.write

    /// 一条提交在日志里的完整一帧。
    let frame (c: Events.Commit) : byte[] = envelope c |> JsonSeq.Frame.encode

    let openStore (dataDir: string) (policy: SnapshotPolicy) : LedgerStore<Projection, EventData, WanxiangError> =
        DataPaths.ensureDataDirs dataDir
        new LedgerStore<Projection, EventData, WanxiangError>(DataPaths.eventsDir dataDir, fold, Projection.empty, codec, policy)

/// 提交请求（由 Server 层规划后送入存储）。
type CommitSubmit =
    { events: EventData list
      commandId: string option
      /// 只用于诊断，不持久化
      commandType: string option
      commandHash: string option
      /// 测试可注入时间；None = UtcNow
      nowUtc: DateTimeOffset option }

type SubmitResult =
    | Committed of Events.Commit
    /// 未落盘（领域拒绝、写失败、结果未知、只读、已关闭）
    | CommitFailed of WanxiangError
    | IdempotentReplay of Events.Commit
    | CommandIdRejected of WanxiangError

/// 旧领域面向 LedgerStore 的薄壳：只做类型映射。onCommitted 在提交成功后同步调用。
type EventStore(dataDir: string, ?onCommitted: Events.Commit -> unit, ?policy: SnapshotPolicy) =
    // 默认：每 1000 条存快照；关闭时由调用方决定（只读降级时不存，免得固化损坏前的投影）。
    let store = Assembly.openStore dataDir (defaultArg policy { everyCommits = Some 1000; onClose = false })
    let mutable stopped = false
    let mutable onCommitted = defaultArg onCommitted ignore

    member _.DataDir = dataDir
    /// 先打开（启动检查即用这次打开的结论），稍后再接上广播。
    member _.SetOnCommitted(f: Events.Commit -> unit) = onCommitted <- f
    member _.Health = store.Health
    member _.Startup = store.Startup
    member _.LastSnapshotError = store.LastSnapshotError
    member _.IsReadOnly = store.Health <> Healthy
    member _.Projection : Projection = store.State
    member _.LastCommitId : CommitId = store.LastId
    member _.Stamps : Map<string, string * CommitId> = store.Stamps
    member _.Checkpoint : Checkpoint = store.Checkpoint

    member _.Submit(submit: CommitSubmit) : SubmitResult =
        if stopped then CommitFailed(Poisoned "store stopped")
        else
            let at = submit.nowUtc |> Option.defaultWith (fun () -> DateTimeOffset.UtcNow)
            let answer =
                match submit.commandId, submit.commandHash with
                | Some cid, Some h -> store.Submit(submit.events, at, { commandId = cid; hash = h })
                | _ -> store.Submit(submit.events, at)
            match answer with
            | Submitted.Committed c ->
                let c = Assembly.toCommit c
                (try onCommitted c with _ -> ())
                Committed c
            | Submitted.Duplicate c -> IdempotentReplay(Assembly.toCommit c)
            | Submitted.Conflict _ ->
                CommandIdRejected(CommandIdConflict(sprintf "commandId %s reused with different payload" (defaultArg submit.commandId "")))
            | Submitted.Rejected e -> CommitFailed e
            | Submitted.NotWritten why -> CommitFailed(Poisoned("append failed: " + why))
            | Submitted.Unknown(id, why) -> CommitFailed(Poisoned(sprintf "outcome of commit %d unknown; read-only: %s" id why))
            | Submitted.ReadOnly why -> CommitFailed(Poisoned("event log read-only: " + why))

    member this.SubmitEvents(events: EventData list) : SubmitResult =
        this.Submit { events = events; commandId = None; commandType = None; commandHash = None; nowUtc = None }

    /// 游标之后的提交（catch-up）。limit = None 返回全部剩余。
    member _.CommitsAfter(cursor: CommitId, ?limit: int) : Events.Commit list =
        match store.CommitsAfter(cursor, defaultArg limit Int32.MaxValue) with
        | Ok cs -> cs |> List.map Assembly.toCommit
        | Error e -> failwithf "commits after %d unreadable: %s" cursor e

    member _.SaveSnapshot() = store.SaveSnapshot()

    /// 用户明确要求时：截掉损坏点之后的字节（调用方先建证据包）。
    member _.Repair() = store.Repair()

    member _.Shutdown() =
        if not stopped then
            stopped <- true
            (store :> IDisposable).Dispose()

    interface IDisposable with
        member this.Dispose() = this.Shutdown()

/// 打开时的只读结论（doctor / 启动 / 诊断用）。打开本身不改日志字节。
type Damage = { file: string; byteOffset: int64; reason: string }
type RepairPlan = { damage: Damage; affectedFiles: string list }

/// 打开时看到的最长有效前缀。
type ReplayOutcome =
    { projection: Projection
      lastCommitId: CommitId
      checkpoint: Checkpoint
      /// 快照用没用上、续播了几条（LedgerStore.Startup）
      startup: Startup
      /// commandId -> (hash, commitId)
      stamps: Map<string, string * CommitId> }

type InspectResult =
    | Healthy of ReplayOutcome
    /// 发现损坏：outcome 是最长有效前缀；存储只读，等 doctor --fix。
    | RepairRequired of ReplayOutcome * RepairPlan
    /// 某次写入结果未知：commit id 那条可能在盘上也可能不在；存储只读，重开时从日志查明。
    | OutcomeUnknown of ReplayOutcome * commitId: CommitId * reason: string
    /// 目录由运行中的宿主持有：只有不持锁检查的结论（没有投影）。
    | HostOwned of Inspection
    | UnsupportedFormat of string
    | Unreadable of string

module Inspect =

    let ofStore (s: EventStore) : InspectResult =
        let o = { projection = s.Projection; lastCommitId = s.LastCommitId; checkpoint = s.Checkpoint; startup = s.Startup; stamps = s.Stamps }
        match s.Health with
        | Health.Healthy -> Healthy o
        | Health.Damaged(at, why) when why.StartsWith "written by a newer version" ->
            UnsupportedFormat(sprintf "event log %s at byte %d" why at)
        | Health.Damaged(at, why) ->
            let f = DataPaths.logFile s.DataDir
            RepairRequired(o, { damage = { file = f; byteOffset = at; reason = why }; affectedFiles = [ f ] })
        | Health.OutcomeUnknown(id, why) -> OutcomeUnknown(o, id, why)

    /// 不持锁、不写字节地看一眼（宿主正在运行时给外部 doctor / 支持包用）。
    let peek (dataDir: string) : Inspection =
        LedgerStore<Projection, EventData, WanxiangError>.Inspect(DataPaths.eventsDir dataDir, Assembly.codec, Assembly.fold, Projection.empty)

    /// 打开、读结论、关闭（不存快照）。
    let run (dataDir: string) : InspectResult =
        try
            use s = new EventStore(dataDir, policy = SnapshotPolicy.Manual)
            ofStore s
        with e -> Unreadable e.Message

    /// 命令行 doctor / 支持包：能独占就完整打开；宿主持锁时改为不持锁检查。
    let runOrPeek (dataDir: string) : InspectResult =
        let opened =
            try Ok(new EventStore(dataDir, policy = SnapshotPolicy.Manual))
            with
            | :? InvalidOperationException as e -> Error(Choice1Of2 e.Message)
            | e -> Error(Choice2Of2 e.Message)
        match opened with
        | Ok s ->
            use s = s
            ofStore s
        | Error(Choice1Of2 _) -> (try HostOwned(peek dataDir) with e -> Unreadable e.Message)
        | Error(Choice2Of2 m) -> Unreadable m

module Repair =

    /// 显式修复（doctor --fix；调用方先建证据包）：截到损坏点。返回被改动的文件。
    let repair (dataDir: string) : Result<string list, string> =
        try
            use s = new EventStore(dataDir, policy = SnapshotPolicy.Manual)
            s.Repair() |> Result.map (fun _ -> [ DataPaths.logFile dataDir ])
        with e -> Error(sprintf "repair failed: %s" e.Message)
