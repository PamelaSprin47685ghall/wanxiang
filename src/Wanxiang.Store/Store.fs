namespace Wanxiang.Store

open System
open System.IO
open System.Text.Json.Nodes
open Wanxiang.Core

// 本文件是组装层：把 wanxiang2 的 JsonSeq / Ledger / Snapshot 三个泛型库
// 用 Wanxiang 的事件类型（EventData）与状态类型（Projection）装起来。
// 机制都在库里；这里只有类型映射、路径约定和对上层保持不变的名字。

/// 领域与账本之间的映射。
module Assembly =

    let codec: Ledger.Codec<EventData> =
        { encode = fun e -> CommitCodec.eventDataToJson e :> JsonNode
          decode =
            fun n ->
                match n with
                | :? JsonObject as o ->
                    match CommitCodec.tryEventFromJson o with
                    | Some e -> Ok e
                    | None -> Error "unknown or malformed event"
                | _ -> Error "event is not a JSON object" }

    /// 账本提交 -> 上层沿用的 Events.Commit。commandType/bootId/source 不再持久化。
    let toCommit (c: Ledger.Commit<EventData>) : Events.Commit =
        let b = Events.Commit.create c.id c.at c.events
        match c.stamp with
        | Some s -> { b with commandId = Some s.commandId; commandHash = Some s.hash }
        | None -> b

    /// 折叠。lastError 记下领域错误原样（账本只传字符串），供协调器还原 WanxiangError。
    let fold (lastError: WanxiangError option ref) (p: Projection) (c: Ledger.Commit<EventData>) : Result<Projection, string> =
        match Projection.applyCommit p (toCommit c) with
        | Ok p' -> Ok p'
        | Error e ->
            lastError.Value <- Some e
            Error(WanxiangError.message e)

    /// 一条提交在日志里的外壳文本（不含分帧；测试造数据、诊断用）。
    let envelope (c: Events.Commit) : string =
        let stamp =
            match c.commandId, c.commandHash with
            | Some cid, Some h -> Some(({ commandId = cid; hash = h }: Ledger.Stamp))
            | _ -> None
        ({ id = c.id; at = c.committedAtUtc; stamp = stamp; events = c.events }: Ledger.Commit<EventData>)
        |> Ledger.Envelope.encode codec

    /// 一条提交在日志里的完整一帧。
    let frame (c: Events.Commit) : byte[] = envelope c |> JsonSeq.Frame.encode

    let foldPure (p: Projection) (c: Ledger.Commit<EventData>) =
        Projection.applyCommit p (toCommit c) |> Result.mapError WanxiangError.message

    let toLogRecord (r: JsonSeq.Record) : Ledger.LogRecord =
        { payload = r.payload; complete = r.complete; startOffset = r.startOffset; endOffset = r.endOffset }

    /// 只读日志：不持写句柄，与运行中的主机共存（FileShare.ReadWrite）。
    /// limit：只暴露该偏移之前的字节（只读降级模式用，损坏点之后的内容不可见）。
    let readOnlyLog (path: string) (limit: int64 option) : Ledger.Log =
        let readFrom (offset: int64) =
            if not (File.Exists path) then []
            else
                use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
                let stop = match limit with Some l -> min l fs.Length | None -> fs.Length
                if offset >= stop then []
                else
                    let buf = Array.zeroCreate<byte> (int (stop - offset))
                    fs.Position <- offset
                    fs.ReadExactly(buf, 0, buf.Length)
                    JsonSeq.Frame.split offset (ReadOnlySpan buf)
                    |> List.map toLogRecord
        { readFrom = readFrom
          append = fun _ -> raise (InvalidOperationException "event log is read-only")
          truncate = ignore }

    /// 可写日志：JsonSeqFile 原样透传，外加测试用故障注入。
    let writableLog (file: JsonSeq.JsonSeqFile) : Ledger.Log =
        { readFrom =
            fun o ->
                file.ReadFrom o
                |> List.map toLogRecord
          append =
            fun text ->
                if FaultInjector.tryFail FaultInjector.BeforeAppend then raise (IOException "injected fault: BeforeAppend")
                let after = file.Append text
                if FaultInjector.tryFail FaultInjector.AfterAppend then raise (IOException "injected fault: AfterAppend")
                after
          truncate = file.Truncate }

/// 启动重放结果。checkpoint 交给协调器续播，也可直接存成快照。
type ReplayOutcome =
    { projection: Projection
      lastCommitId: CommitId
      checkpoint: Ledger.Checkpoint<Projection> }

module ReplayOutcome =
    let ofCheckpoint (cp: Ledger.Checkpoint<Projection>) : ReplayOutcome =
        { projection = cp.state; lastCommitId = cp.throughId; checkpoint = cp }

    let empty = ofCheckpoint (Ledger.Checkpoint.empty Projection.empty)

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

    /// 从基线续播（快照启动）。基线为空即完整重放。
    let inspectFrom (dataDir: string) (from: Ledger.Checkpoint<Projection>) : InspectResult =
        let path = DataPaths.logFile dataDir
        try
            match Ledger.Ledger.openLog (Assembly.readOnlyLog path None) Assembly.codec Assembly.foldPure from with
            | Ledger.Ready l -> Healthy(ReplayOutcome.ofCheckpoint l.Checkpoint)
            | Ledger.Damaged d ->
                RepairRequired(
                    ReplayOutcome.ofCheckpoint d.prefix,
                    { damage = { file = path; byteOffset = d.validUntil; reason = d.reason }
                      affectedFiles = [ path ] })
            | Ledger.Unsupported v -> UnsupportedFormat(sprintf "event log written by a newer version (envelope v%d)" v)
        with e -> Unreadable(sprintf "cannot read %s: %s" path e.Message)

    let inspect (dataDir: string) : InspectResult =
        inspectFrom dataDir (Ledger.Checkpoint.empty Projection.empty)

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
      checkpoint: Ledger.Checkpoint<Projection> }

/// 快照启动结论（供诊断留证据）。
type SnapshotStartup =
    | NoSnapshot
    | SnapshotUsed of SnapshotInfo
    | SnapshotCorrupt of reasons: string list
    /// 快照超前于日志（日志被截断/替换）：回退完整重放
    | SnapshotStale of reason: string

/// 账本基线快照：外壳（原子写、校验和、版本）由 wanxiang2 Snapshot 库负责，
/// 这里只定正文形状 = 基线 Checkpoint<Projection> + 创建时间。
module Snapshot =

    let FormatVersion = Snapshot.Snapshot.Version

    let snapshotsDir = DataPaths.snapshotsDir

    let private u64 (n: JsonNode) = UInt64.Parse(n.GetValue<string>(), Globalization.CultureInfo.InvariantCulture)

    let private encode (createdAt: DateTimeOffset, cp: Ledger.Checkpoint<Projection>) : string =
        let o = JsonObject()
        o["createdAt"] <- createdAt.ToString("o")
        o["throughId"] <- string cp.throughId
        o["offset"] <- cp.offset
        let stamps = JsonArray()
        for KeyValue(cid, (hash, id)) in cp.stamps do
            let s = JsonObject()
            s["commandId"] <- cid
            s["hash"] <- hash
            s["commitId"] <- string id
            stamps.Add s
        o["stamps"] <- stamps
        o["projection"] <- ProjectionCodec.toJson cp.state
        o.ToJsonString()

    let private decode (text: string) : Result<DateTimeOffset * Ledger.Checkpoint<Projection>, string> =
        try
            let o = JsonNode.Parse(text).AsObject()
            match ProjectionCodec.tryFromJson (o["projection"].AsObject()) with
            | None -> Error "projection malformed"
            | Some p ->
                let stamps =
                    o["stamps"].AsArray()
                    |> Seq.map (fun s -> s["commandId"].GetValue<string>(), (s["hash"].GetValue<string>(), u64 s["commitId"]))
                    |> Map.ofSeq
                Ok(
                    DateTimeOffset.Parse(o["createdAt"].GetValue<string>(), Globalization.CultureInfo.InvariantCulture),
                    { state = p; throughId = u64 o["throughId"]; offset = o["offset"].GetValue<int64>(); stamps = stamps }
                )
        with e -> Error e.Message

    /// 写快照（原子替换）。
    let write (dataDir: string) (cp: Ledger.Checkpoint<Projection>) : Result<string, string> =
        try
            let path = DataPaths.snapshotFile dataDir
            Snapshot.Snapshot.save path encode (DateTimeOffset.UtcNow, cp)
            Ok path
        with e -> Error e.Message

    /// 读取快照并判定能否用作启动基线。
    let findLatest (dataDir: string) : SnapshotStartup =
        let path = DataPaths.snapshotFile dataDir
        match Snapshot.Snapshot.load path decode with
        | Snapshot.Missing -> NoSnapshot
        | Snapshot.Corrupt r -> SnapshotCorrupt [ r ]
        | Snapshot.Loaded(createdAt, cp) ->
            let logLen = try FileInfo(DataPaths.logFile dataDir).Length with _ -> 0L
            let log = DataPaths.logFile dataDir
            if cp.offset > (if File.Exists log then logLen else 0L) then
                SnapshotStale(sprintf "snapshot covers %d bytes but log has %d" cp.offset logLen)
            else
                SnapshotUsed
                    { throughCommitId = cp.throughId
                      createdAtUtc = createdAt
                      path = path
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

/// 单写者：wanxiang2 Ledger 之上的薄壳。
/// 账本先折叠后写盘，所以不存在「已落盘但投影失败」的截尾路径。
/// outcome 为损坏前缀（只读降级）时，协调器只读：所有提交返回 CommitFailed。
type CommitCoordinator(dataDir: string, outcome: ReplayOutcome, onCommitted: Events.Commit -> unit, onTruncated: Events.Commit * WanxiangError * int64 * string -> unit) =

    do DataPaths.ensureDataDirs dataDir
    let path = DataPaths.logFile dataDir
    let gate = obj ()
    let mutable nowOverride: DateTimeOffset option = None
    let lastError = ref None
    let now () = nowOverride |> Option.defaultWith (fun () -> DateTimeOffset.UtcNow)
    let mutable stopped = false

    let file = new JsonSeq.JsonSeqFile(path)
    do try File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()

    let ledger, readOnly =
        match Ledger.Ledger.load (Assembly.writableLog file) Assembly.codec (Assembly.fold lastError) outcome.checkpoint now with
        | Ledger.Ready l -> l, false
        | Ledger.Damaged d ->
            match Ledger.Ledger.load (Assembly.readOnlyLog path (Some d.validUntil)) Assembly.codec Assembly.foldPure outcome.checkpoint now with
            | Ledger.Ready l -> l, true
            | _ -> failwithf "cannot open damaged event log read-only: %s" d.reason
        | Ledger.Unsupported v -> failwithf "event log written by a newer version (envelope v%d)" v

    // [例外登记] 分页索引：Ledger.ReadAfter 没有 limit，每次都解码游标之后的全部记录，
    // 慢客户端按 64 条一批追赶长历史就是 O(n²)。这里自己记每条提交的起止偏移，按页读。
    // 使用方：CommitsAfter（WsConnection catch-up）。解除条件：Ledger.ReadAfter 支持 limit 后删除。
    // 索引从日志头开始（快照不删日志，所以全量历史都在）：第 i 条记录即 id = i + 1。
    let bounds = ResizeArray<int64 * int64>()
    do
        let replayed = (Assembly.readOnlyLog path (Some ledger.Checkpoint.offset)).readFrom 0L
        for r in replayed do
            bounds.Add(r.startOffset, r.endOffset)

    let readPage (cursor: CommitId) (limit: int) : Events.Commit list =
        let i0 = int cursor
        let n = min limit (bounds.Count - i0)
        if n <= 0 then []
        else
            let start, _ = bounds[i0]
            let _, stop = bounds[i0 + n - 1]
            (Assembly.readOnlyLog path (Some stop)).readFrom start
            |> List.choose (fun r ->
                match Ledger.Envelope.decode Assembly.codec r.payload with
                | Ledger.Envelope.Ok c when c.id > cursor -> Some(Assembly.toCommit c)
                | _ -> None)

    do
        ledger.Committed.Add(fun c ->
            let before = match bounds.Count with 0 -> 0L | k -> snd bounds[k - 1]
            bounds.Add(before, ledger.Checkpoint.offset)
            try onCommitted (Assembly.toCommit c) with _ -> ())

    member _.IsReadOnly = readOnly

    member _.Submit(submit: CommitSubmit) : SubmitResult =
        lock gate (fun () ->
            if stopped then CommitFailed(Poisoned "coordinator stopped")
            elif readOnly then CommitFailed(Poisoned "event log damaged; read-only")
            else
                let stamp =
                    match submit.commandId, submit.commandHash with
                    | Some cid, Some h -> Some(({ commandId = cid; hash = h }: Ledger.Stamp))
                    | _ -> None
                nowOverride <- submit.nowUtc
                lastError.Value <- None
                let r =
                    match stamp with
                    | Some s -> ledger.Submit(submit.events, s)
                    | None -> ledger.Submit(submit.events)
                nowOverride <- None
                match r with
                | Ledger.Committed c -> Committed(Assembly.toCommit c)
                | Ledger.Duplicate id ->
                    match ledger.ReadAfter(id - 1UL) with
                    | c :: _ when c.id = id -> IdempotentReplay(Assembly.toCommit c)
                    | _ -> CommandIdRejected(Poisoned(sprintf "idempotency stamp references unreadable commit %d" id))
                | Ledger.StampConflict _ ->
                    CommandIdRejected(CommandIdConflict(sprintf "commandId %s reused with different payload" (defaultArg submit.commandId "")))
                | Ledger.Rejected msg -> CommitFailed(lastError.Value |> Option.defaultValue (ValidationError msg))
                | Ledger.WriteFailed e ->
                    let attempted = Events.Commit.create (ledger.LastId + 1UL) (now ()) submit.events
                    onTruncated (attempted, Poisoned(sprintf "append failed: %s" e.Message), ledger.Checkpoint.offset, path)
                    CommitFailed(Poisoned "append failed"))

    member this.SubmitEvents(events: EventData list) : SubmitResult =
        this.Submit { events = events; commandId = None; commandType = None; commandHash = None; nowUtc = None }

    member _.Projection : Projection = ledger.State

    /// 幂等索引：commandId -> (载荷哈希, 提交 id)。交给 CommandEngine.planStamped。
    member _.Stamps : Map<string, string * CommitId> = ledger.Checkpoint.stamps

    /// 当前基线（关闭时写快照用）。
    member _.Checkpoint : Ledger.Checkpoint<Projection> = ledger.Checkpoint

    /// 游标之后的提交（catch-up）。limit = None 返回全部剩余。
    member _.CommitsAfter(cursor: CommitId, ?limit: int) : Events.Commit list =
        lock gate (fun () ->
            match limit with
            | Some n -> readPage cursor (max 0 n)
            | _ ->
                let all = ledger.ReadAfter cursor
                let taken = match limit with Some n -> List.truncate (max 0 n) all | None -> all
                taken |> List.map Assembly.toCommit)

    member _.Shutdown() : unit =
        lock gate (fun () ->
            if not stopped then
                stopped <- true
                (file :> IDisposable).Dispose())

    interface IDisposable with
        member this.Dispose() = this.Shutdown()
