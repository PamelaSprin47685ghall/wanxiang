namespace Wanxiang.Store

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open System.Text.Json.Nodes
open Wanxiang.Core

// 组装层：在 wanxiang2 的对象之间路由消息。
//   Ledger（actor）发 AppendRequested -> 这里写 JsonSeqFile -> 回 Appended / AppendFailed。
//   读取：Ledger 回答位置（PositionsAfter / PositionOf），这里按位置读 JsonSeqFile。
// 位置 'P = 一条记录在日志里的字节区间 [start, end)。
// 本文件不持锁：串行化由账本的邮箱负责。

/// 记录在日志里的字节区间。
type Span = int64 * int64

/// 领域类型与库类型之间的映射。
module Assembly =

    /// 领域事件 -> 外壳里的 JSON。
    let toJson (c: Ledger.Commit<EventData>) : Ledger.Commit<JsonNode> =
        Ledger.Commit.map (fun e -> CommitCodec.eventDataToJson e :> JsonNode) c

    /// 外壳里的 JSON -> 领域事件。
    let ofJson (c: Ledger.Commit<JsonNode>) : Result<Ledger.Commit<EventData>, string> =
        let decoded =
            c.events
            |> List.map (fun n ->
                match n with
                | :? JsonObject as o -> CommitCodec.tryEventFromJson o
                | _ -> None)
        if decoded |> List.forall Option.isSome then Ok { id = c.id; at = c.at; stamp = c.stamp; events = List.choose id decoded }
        else Error "unknown or malformed event"

    /// 账本提交 -> 上层沿用的 Events.Commit。commandType/bootId/source 不再持久化。
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

    /// 一条提交的外壳文本（不含分帧；测试造数据、诊断用）。
    let envelope (c: Events.Commit) : string = ofCommit c |> toJson |> Ledger.Envelope.write

    /// 一条提交在日志里的完整一帧。
    let frame (c: Events.Commit) : byte[] = envelope c |> JsonSeq.Frame.encode

    /// 折叠。lastError 记下领域错误原样（账本只传字符串），供协调器还原 WanxiangError。
    let fold (lastError: WanxiangError option ref) (p: Projection) (c: Ledger.Commit<EventData>) : Result<Projection, string> =
        match Projection.applyCommit p (toCommit c) with
        | Ok p' -> Ok p'
        | Error e ->
            lastError.Value <- Some e
            Error(WanxiangError.message e)

    /// 只读读取 [from, until)：不持写句柄，可与运行中的主机共存。until = None 读到末尾。
    let read (path: string) (from: int64) (until: int64 option) : JsonSeq.Record list =
        if not (File.Exists path) then []
        else
            use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            let stop = defaultArg until fs.Length |> min fs.Length
            if from >= stop then []
            else
                let buf = Array.zeroCreate<byte> (int (stop - from))
                fs.Position <- from
                fs.ReadExactly(buf, 0, buf.Length)
                JsonSeq.Frame.split from (ReadOnlySpan buf)

    /// 一条记录 -> 领域提交；失败给出原因。
    let decode (r: JsonSeq.Record) : Result<Ledger.Commit<EventData>, Choice<string, int>> =
        match r.fault with
        | Some f -> Error(Choice1Of2 f)
        | None ->
            match Ledger.Envelope.read r.payload with
            | Ledger.Envelope.Parsed cj -> ofJson cj |> Result.mapError Choice1Of2
            | Ledger.Envelope.Invalid why -> Error(Choice1Of2("undecodable record: " + why))
            | Ledger.Envelope.NewerVersion v -> Error(Choice2Of2 v)

type Checkpoint = Ledger.Checkpoint<Projection, Span>

/// 启动重放结果。checkpoint 交给协调器续播，也可直接存成快照。
type ReplayOutcome =
    { projection: Projection
      lastCommitId: CommitId
      checkpoint: Checkpoint }

module ReplayOutcome =
    let ofCheckpoint (cp: Checkpoint) : ReplayOutcome =
        { projection = cp.state; lastCommitId = cp.throughId; checkpoint = cp }

    let emptyCheckpoint: Checkpoint = { state = Projection.empty; throughId = 0UL; stamps = Map.empty; positions = [] }

    let empty = ofCheckpoint emptyCheckpoint

/// 日志损坏点（只读检查产物）。
type Damage = { file: string; byteOffset: int64; reason: string }

/// 修复计划：截断到 damage.byteOffset。affectedFiles 供修复前建证据包。
type RepairPlan = { damage: Damage; affectedFiles: string list }

/// 只读检查结论。Inspect 绝不修改任何文件。
type InspectResult =
    | Healthy of ReplayOutcome
    /// 发现损坏：outcome 是最长有效前缀；启动方进入只读降级模式。
    | RepairRequired of ReplayOutcome * RepairPlan
    | UnsupportedFormat of string
    | Unreadable of string

module Replay =

    /// 记录 -> 账本的 Replay 消息；遇到第一个故障、解码失败或 ReplayRejected 即停，报告损坏点。
    let internal run (path: string) (from: Checkpoint) : InspectResult =
        let ledger = Ledger.Ledger<Projection, EventData, Span>(Assembly.fold (ref None), from)
        let mutable rejected: string option = None
        ledger.Outbox.Add(function Ledger.ReplayRejected(_, _, why) -> rejected <- Some why | _ -> ())
        let start = match List.tryLast from.positions with Some(_, e) -> e | None -> 0L
        let damaged (r: JsonSeq.Record) why =
            RepairRequired(
                ReplayOutcome.ofCheckpoint ledger.Checkpoint,
                { damage = { file = path; byteOffset = r.startOffset; reason = why }; affectedFiles = [ path ] })
        let rec go (records: JsonSeq.Record list) =
            match records with
            | [] -> Healthy(ReplayOutcome.ofCheckpoint ledger.Checkpoint)
            | r :: rest ->
                match Assembly.decode r with
                | Error(Choice2Of2 v) -> UnsupportedFormat(sprintf "event log written by a newer version (envelope v%d)" v)
                | Error(Choice1Of2 why) -> damaged r why
                | Ok c ->
                    ledger.Post(Ledger.Replay(c, (r.startOffset, r.endOffset)))
                    match rejected with
                    | Some why -> damaged r why
                    | None -> go rest
        go (Assembly.read path start None)

    /// 从基线续播（快照启动）。
    let inspectFrom (dataDir: string) (from: Checkpoint) : InspectResult =
        let path = DataPaths.logFile dataDir
        try run path from
        with e -> Unreadable(sprintf "cannot read %s: %s" path e.Message)

    let inspect (dataDir: string) : InspectResult = inspectFrom dataDir ReplayOutcome.emptyCheckpoint

module Repair =

    /// 显式修复：截断到损坏点（调用方须先持锁并建证据包）。返回被改动的文件。
    let repair (_dataDir: string) (plan: RepairPlan) : Result<string list, string> =
        try
            use fs = new FileStream(plan.damage.file, FileMode.Open, FileAccess.Write, FileShare.Read)
            fs.SetLength plan.damage.byteOffset
            fs.Flush true
            Ok [ plan.damage.file ]
        with e -> Error(sprintf "repair failed: %s" e.Message)

/// 快照元数据。
type SnapshotInfo =
    { throughCommitId: CommitId
      createdAtUtc: DateTimeOffset
      path: string
      projection: Projection
      checkpoint: Checkpoint }

/// 快照启动结论（供诊断留证据）。
type SnapshotStartup =
    | NoSnapshot
    | SnapshotUsed of SnapshotInfo
    | SnapshotCorrupt of reasons: string list
    /// 快照超前于日志（日志被截断/替换）：回退完整重放
    | SnapshotStale of reason: string

/// 账本基线快照：外壳由 wanxiang2 SnapshotFile 负责，正文 = Checkpoint + 创建时间。
module Snapshot =

    let FormatVersion = 1

    let snapshotsDir = DataPaths.snapshotsDir

    let private u64 (n: JsonNode) = UInt64.Parse(n.GetValue<string>(), Globalization.CultureInfo.InvariantCulture)

    let private encode (createdAt: DateTimeOffset) (cp: Checkpoint) : string =
        let o = JsonObject()
        o["createdAt"] <- createdAt.ToString("o")
        o["throughId"] <- string cp.throughId
        let stamps = JsonArray()
        for KeyValue(cid, (hash, id)) in cp.stamps do
            let s = JsonObject()
            s["commandId"] <- cid
            s["hash"] <- hash
            s["commitId"] <- string id
            stamps.Add s
        o["stamps"] <- stamps
        let pos = JsonArray()
        for (a, b) in cp.positions do
            pos.Add(JsonArray(JsonValue.Create a, JsonValue.Create b))
        o["positions"] <- pos
        o["projection"] <- ProjectionCodec.toJson cp.state
        o.ToJsonString()

    let private decode (text: string) : Result<DateTimeOffset * Checkpoint, string> =
        try
            let o = JsonNode.Parse(text).AsObject()
            match ProjectionCodec.tryFromJson (o["projection"].AsObject()) with
            | None -> Error "projection malformed"
            | Some p ->
                let stamps =
                    o["stamps"].AsArray()
                    |> Seq.map (fun s -> s["commandId"].GetValue<string>(), (s["hash"].GetValue<string>(), u64 s["commitId"]))
                    |> Map.ofSeq
                let positions =
                    o["positions"].AsArray()
                    |> Seq.map (fun n -> let a = n.AsArray() in a[0].GetValue<int64>(), a[1].GetValue<int64>())
                    |> List.ofSeq
                let throughId = u64 o["throughId"]
                if uint64 positions.Length <> throughId then Error "positions do not match throughId"
                else
                    Ok(
                        DateTimeOffset.Parse(o["createdAt"].GetValue<string>(), Globalization.CultureInfo.InvariantCulture),
                        { state = p; throughId = throughId; stamps = stamps; positions = positions }
                    )
        with e -> Error e.Message

    /// 写快照（原子替换）。
    let write (dataDir: string) (cp: Checkpoint) : Result<string, string> =
        try
            let file = Snapshot.SnapshotFile(DataPaths.snapshotFile dataDir)
            file.Save(encode DateTimeOffset.UtcNow cp)
            Ok file.Path
        with e -> Error e.Message

    /// 读取快照并判定能否用作启动基线。
    let findLatest (dataDir: string) : SnapshotStartup =
        let file = Snapshot.SnapshotFile(DataPaths.snapshotFile dataDir)
        match file.Load() with
        | Snapshot.Missing -> NoSnapshot
        | Snapshot.Corrupt r -> SnapshotCorrupt [ r ]
        | Snapshot.Loaded text ->
            match decode text with
            | Error r -> SnapshotCorrupt [ r ]
            | Ok(createdAt, cp) ->
                let log = DataPaths.logFile dataDir
                let logLen = if File.Exists log then FileInfo(log).Length else 0L
                let covered = match List.tryLast cp.positions with Some(_, e) -> e | None -> 0L
                if covered > logLen then
                    SnapshotStale(sprintf "snapshot covers %d bytes but log has %d" covered logLen)
                else
                    SnapshotUsed
                        { throughCommitId = cp.throughId
                          createdAtUtc = createdAt
                          path = file.Path
                          projection = cp.state
                          checkpoint = cp }

/// 提交请求（由 Server 层规划后送入单写者）。
type CommitSubmit =
    { events: EventData list
      commandId: string option
      /// 只用于诊断，不再持久化
      commandType: string option
      commandHash: string option
      /// 测试可注入时间；None = UtcNow
      nowUtc: DateTimeOffset option }

type SubmitResult =
    /// 已落盘并更新投影
    | Committed of Events.Commit
    /// 未落盘（写失败、领域拒绝、只读降级、已关闭）
    | CommitFailed of WanxiangError
    /// 同一 commandId 与载荷已提交过：返回原提交
    | IdempotentReplay of Events.Commit
    /// 同一 commandId、不同载荷：拒绝，未写盘
    | CommandIdRejected of WanxiangError

/// 单写者组装：账本（actor）与日志文件之间的消息路由。
/// outcome 之后若日志还有内容，先续播；续播遇到损坏则只读（不截断，交给 doctor --fix）。
type CommitCoordinator(dataDir: string, outcome: ReplayOutcome, onCommitted: Events.Commit -> unit, onTruncated: Events.Commit * WanxiangError * int64 * string -> unit) =

    do DataPaths.ensureDataDirs dataDir
    let path = DataPaths.logFile dataDir

    let start, readOnly =
        match Replay.run path outcome.checkpoint with
        | Healthy o -> o.checkpoint, false
        | RepairRequired(o, _) -> o.checkpoint, true
        | UnsupportedFormat msg -> failwith msg
        | Unreadable msg -> failwith msg

    let file = new JsonSeq.JsonSeqFile(path)
    do try File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()

    let lastError = ref None
    let ledger = Ledger.Ledger<Projection, EventData, Span>(Assembly.fold lastError, start)
    let replies = ConcurrentDictionary<Guid, TaskCompletionSource<Choice<Ledger.Commit<EventData>, Ledger.Refusal<Span>>>>()
    let mutable stopped = false

    let reply ticket r =
        let mutable tcs = null
        if replies.TryRemove(ticket, &tcs) then tcs.SetResult r

    /// 写入。fault 注入点：BeforeAppend（未写即失败）、AfterAppend（写了再失败，必须截回）。
    let append (c: Ledger.Commit<EventData>) : Ledger.LedgerIn<EventData, Span> =
        let before = file.Length
        try
            if FaultInjector.tryFail FaultInjector.BeforeAppend then raise (IOException "injected fault: BeforeAppend")
            let after = file.Append(Ledger.Envelope.write (Assembly.toJson c))
            if FaultInjector.tryFail FaultInjector.AfterAppend then raise (IOException "injected fault: AfterAppend")
            Ledger.Appended(c.id, (before, after))
        with e ->
            (try file.Truncate before with _ -> ())
            onTruncated (Assembly.toCommit c, Poisoned(sprintf "append failed: %s" e.Message), before, path)
            Ledger.AppendFailed(c.id, e.Message)

    do
        ledger.Outbox.Add(fun o ->
            match o with
            | Ledger.AppendRequested(_, c) -> ledger.Post(append c)
            | Ledger.Committed(Some t, c, _) ->
                (try onCommitted (Assembly.toCommit c) with _ -> ())
                reply t (Choice1Of2 c)
            | Ledger.Refused(t, r) -> reply t (Choice2Of2 r)
            | Ledger.Committed(None, _, _)
            | Ledger.ReplayRejected _ -> ())

    /// 按位置读出提交。
    let readSpans (spans: Span list) : Events.Commit list =
        match spans with
        | [] -> []
        | _ ->
            let from = fst (List.head spans)
            let until = snd (List.last spans)
            Assembly.read path from (Some until)
            |> List.choose (fun r -> match Assembly.decode r with Ok c -> Some(Assembly.toCommit c) | Error _ -> None)

    member _.IsReadOnly = readOnly

    member _.Submit(submit: CommitSubmit) : SubmitResult =
        if stopped then CommitFailed(Poisoned "coordinator stopped")
        elif readOnly then CommitFailed(Poisoned "event log damaged; read-only")
        else
            let stamp =
                match submit.commandId, submit.commandHash with
                | Some cid, Some h -> Some(({ commandId = cid; hash = h }: Ledger.Stamp))
                | _ -> None
            let ticket = Guid.NewGuid()
            let tcs = TaskCompletionSource<_>(TaskCreationOptions.RunContinuationsAsynchronously)
            replies[ticket] <- tcs
            lastError.Value <- None
            let at = submit.nowUtc |> Option.defaultWith (fun () -> DateTimeOffset.UtcNow)
            ledger.Post(Ledger.Submit(ticket, submit.events, at, stamp))
            match tcs.Task.Result with
            | Choice1Of2 c -> Committed(Assembly.toCommit c)
            | Choice2Of2(Ledger.Duplicate(_, span)) ->
                match readSpans [ span ] with
                | [ c ] -> IdempotentReplay c
                | _ -> CommandIdRejected(Poisoned "idempotency stamp references unreadable commit")
            | Choice2Of2(Ledger.StampConflict _) ->
                CommandIdRejected(CommandIdConflict(sprintf "commandId %s reused with different payload" (defaultArg submit.commandId "")))
            | Choice2Of2(Ledger.Rejected msg) -> CommitFailed(lastError.Value |> Option.defaultValue (ValidationError msg))
            | Choice2Of2(Ledger.WriteFailed _) -> CommitFailed(Poisoned "append failed")

    member this.SubmitEvents(events: EventData list) : SubmitResult =
        this.Submit { events = events; commandId = None; commandType = None; commandHash = None; nowUtc = None }

    member _.Projection : Projection = ledger.State

    /// 幂等索引：commandId -> (载荷哈希, 提交 id)。交给 CommandEngine.planStamped。
    member _.Stamps : Map<string, string * CommitId> = ledger.Checkpoint.stamps

    /// 当前基线（关闭时写快照用）。
    member _.Checkpoint : Checkpoint = ledger.Checkpoint

    /// 游标之后的提交（catch-up）。limit = None 返回全部剩余。
    member _.CommitsAfter(cursor: CommitId, ?limit: int) : Events.Commit list =
        let positions =
            match limit with
            | Some n -> ledger.PositionsAfter(cursor, max 0 n)
            | None -> ledger.PositionsAfter cursor
        positions |> List.map snd |> readSpans

    member _.Shutdown() : unit =
        if not stopped then
            stopped <- true
            (file :> IDisposable).Dispose()

    interface IDisposable with
        member this.Dispose() = this.Shutdown()
