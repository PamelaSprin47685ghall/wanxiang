namespace Wanxiang.Store

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Wanxiang.Core
open Wanxiang.Core.Ledger

/// 快照元数据（加载后返回给调用方）。
type SnapshotInfo = {
    /// 快照冻结时刻的全局提交水位
    throughCommitId: CommitId
    /// 快照覆盖的最后一个事件文件（文件名）；续播从该文件的 throughOffset 字节起
    throughFile: string option
    /// 在 throughFile 内的字节偏移（快照之后第一条记录的起点）
    throughOffset: int64
    createdAtUtc: DateTimeOffset
    /// 快照文件完整路径
    path: string
    /// 冻结的投影副本
    projection: Projection
}

/// 快照优先启动的结论（供调用方记录诊断证据；R6）。
type SnapshotStartup =
    /// 无任何快照（首次运行/从未创建）
    | NoSnapshot
    /// 使用最新有效快照
    | SnapshotUsed of SnapshotInfo
    /// 全部快照损坏/校验失败：回退完整重放（算法 L8/L9）；reasons 是证据
    | SnapshotCorrupt of reasons: string list
    /// 快照超前于日志（覆盖文件缺失/被截断）：回退完整重放
    | SnapshotStale of reason: string

/// 投影快照（算法 S：Create Snapshot；算法 L：Load From Snapshot）。
///
/// 快照 = 在 throughCommitId 冻结的投影副本 + 校验和；**不删除任何事件日志**（第一版只优化启动）。
/// 布局：data/snapshots/projection-<throughCommitId>.snapshot
///
/// 信封（JSON）：
///   snapshotFormatVersion (number)
///   throughCommitId (string，RFC 8785 附录 D 大整数)
///   createdAt (RFC3339)
///   throughFile (string，可选)
///   throughOffset (number)
///   projection (object)
///   checksum (string) = SHA-256(JCS(信封去掉 checksum 字段))
module Snapshot =

    /// 快照格式版本（单一权威，P-002；version-authority.md 钉住）。
    [<Literal>]
    let FormatVersion = 1

    let snapshotsDir (dataDir: string) = Path.Combine(dataDir, "snapshots")

    let fileName (throughCommitId: CommitId) = sprintf "projection-%012d.snapshot" throughCommitId

    let filePath (dataDir: string) (throughCommitId: CommitId) = Path.Combine(snapshotsDir dataDir, fileName throughCommitId)

    // ---------- JSON 辅助 ----------

    let private tryGet (o: JsonObject) (k: string) : JsonNode option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(k, &n) && not (isNull n) then Some n else None

    let private tryStr (o: JsonObject) (k: string) : string option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
        | _ -> None

    let private tryUInt64 (o: JsonObject) (k: string) : CommitId option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.String ->
            match UInt64.TryParse(v.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, x -> Some x
            | _ -> None
        | _ -> None

    let private tryInt64 (o: JsonObject) (k: string) : int64 option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.Number ->
            match v with
            | :? JsonValue as jv ->
                match jv.TryGetValue<int64>() with
                | true, x -> Some x
                | _ -> None
            | _ -> None
        | _ -> None

    let private tryInt (o: JsonObject) (k: string) : int option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.Number ->
            match v with
            | :? JsonValue as jv ->
                match jv.TryGetValue<int>() with
                | true, x -> Some x
                | _ -> None
            | _ -> None
        | _ -> None

    let private tryBool (o: JsonObject) (k: string) : bool option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.True -> Some true
        | Some v when v.GetValueKind() = JsonValueKind.False -> Some false
        | _ -> None

    let private tryDateTime (o: JsonObject) (k: string) : DateTimeOffset option =
        match tryStr o k with
        | Some s ->
            match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, d -> Some d
            | _ -> None
        | None -> None

    let private tryGuid (o: JsonObject) (k: string) : Guid option =
        match tryStr o k with
        | Some s ->
            match Guid.TryParse s with
            | true, g -> Some g
            | _ -> None
        | None -> None

    // ---------- 投影 JSON 编解码（快照专用；与线上 Wire 无关） ----------

    let private messageToJson (m: MessageRecord) : JsonObject =
        let o = JsonObject()
        o["commitId"] <- m.commitId.ToString(CultureInfo.InvariantCulture)
        o["conversationId"] <- m.conversationId.ToString("D")
        o["payloadJson"] <- m.payloadJson.DeepClone()
        o["committedAtUtc"] <- m.committedAtUtc.UtcDateTime.ToString("o")
        match m.deletedAtCommitId with
        | Some d -> o["deletedAtCommitId"] <- d.ToString(CultureInfo.InvariantCulture)
        | None -> ()
        o

    let private messageFromJson (o: JsonObject) : MessageRecord option =
        match tryUInt64 o "commitId", tryGuid o "conversationId", tryGet o "payloadJson", tryDateTime o "committedAtUtc" with
        | Some cid, Some conv, Some payload, Some at ->
            Some
                { commitId = cid
                  conversationId = conv
                  payloadJson = payload.DeepClone()
                  committedAtUtc = at
                  deletedAtCommitId = tryUInt64 o "deletedAtCommitId" }
        | _ -> None

    let private parentToJson (p: (Guid * CommitId option) option) : JsonNode =
        match p with
        | None -> null
        | Some (g, after) ->
            let o = JsonObject()
            o["parentId"] <- g.ToString("D")
            match after with
            | Some a -> o["afterId"] <- a.ToString(CultureInfo.InvariantCulture)
            | None -> ()
            o :> JsonNode

    let private parentFromJson (v: JsonNode) : ((Guid * CommitId option) option) option =
        // None = 解析失败；Some x = 成功
        match v with
        | null -> Some None
        | :? JsonObject as o ->
            match tryGuid o "parentId" with
            | Some g -> Some(Some(g, tryUInt64 o "afterId"))
            | None -> None
        | _ -> None

    let private conversationToJson (c: Conversation) : JsonObject =
        let o = JsonObject()
        o["conversationId"] <- c.conversationId.ToString("D")
        o["title"] <- c.title
        o["createdAtUtc"] <- c.createdAtUtc.UtcDateTime.ToString("o")
        o["lastActivityAtUtc"] <- c.lastActivityAtUtc.UtcDateTime.ToString("o")
        o["deleted"] <- c.deleted
        o["pinned"] <- c.pinned
        o["archived"] <- c.archived
        o["parent"] <- parentToJson c.parent
        match c.forkBaseCommitId with
        | Some b -> o["forkBaseCommitId"] <- b.ToString(CultureInfo.InvariantCulture)
        | None -> ()
        o["config"] <- CommitCodec.configToJson c.config
        let arr = JsonArray()
        for m in c.messages do
            arr.Add(messageToJson m)
        o["messages"] <- arr
        match c.lastCommitId with
        | Some l -> o["lastCommitId"] <- l.ToString(CultureInfo.InvariantCulture)
        | None -> ()
        o

    let private conversationFromJson (o: JsonObject) : Conversation option =
        match
            tryGuid o "conversationId",
            tryStr o "title",
            tryDateTime o "createdAtUtc",
            tryDateTime o "lastActivityAtUtc",
            tryBool o "deleted",
            tryBool o "pinned",
            tryBool o "archived"
        with
        | Some cid, Some title, Some created, Some lastAct, Some deleted, Some pinned, Some archived ->
            let parent =
                match tryGet o "parent" with
                | Some v -> parentFromJson v
                | None -> Some None
            match parent with
            | None -> None
            | Some parentVal ->
                match tryGet o "config" with
                | Some v when v.GetValueKind() = JsonValueKind.Object ->
                    let cfgNode = v.AsObject()
                    let messages =
                        match tryGet o "messages" with
                        | Some v when v.GetValueKind() = JsonValueKind.Array ->
                            let mutable ok = true
                            let list = ResizeArray<MessageRecord>()
                            for item in v.AsArray() do
                                if item <> null && item.GetValueKind() = JsonValueKind.Object then
                                    match messageFromJson (item.AsObject()) with
                                    | Some m -> list.Add m
                                    | None -> ok <- false
                                else
                                    ok <- false
                            if ok then Some(List.ofSeq list) else None
                        | _ -> None
                    match messages with
                    | Some msgs ->
                        Some
                            { conversationId = cid
                              title = title
                              createdAtUtc = created
                              lastActivityAtUtc = lastAct
                              deleted = deleted
                              pinned = pinned
                              archived = archived
                              parent = parentVal
                              forkBaseCommitId = tryUInt64 o "forkBaseCommitId"
                              config = CommitCodec.configFromJson cfgNode
                              messages = msgs
                              lastCommitId = tryUInt64 o "lastCommitId" }
                    | None -> None
                | _ -> None
        | _ -> None

    let private idemToJson (r: IdemRecord) : JsonObject =
        let o = JsonObject()
        o["commandId"] <- r.commandId
        o["invocationId"] <- r.invocationId.ToString("D")
        o["commandType"] <- r.commandType
        o["canonicalHash"] <- r.canonicalHash
        o["commitId"] <- r.commitId.ToString(CultureInfo.InvariantCulture)
        o

    let private idemFromJson (o: JsonObject) : IdemRecord option =
        match tryStr o "commandId", tryGuid o "invocationId", tryStr o "commandType", tryStr o "canonicalHash", tryUInt64 o "commitId" with
        | Some cid, Some inv, Some ctype, Some hash, Some commitId ->
            Some
                { commandId = cid
                  invocationId = inv
                  commandType = ctype
                  canonicalHash = hash
                  commitId = commitId }
        | _ -> None

    let private projectionToJson (p: Projection) : JsonObject =
        let o = JsonObject()
        let convs = JsonArray()
        for KeyValue(_, c) in p.conversations do
            convs.Add(conversationToJson c)
        o["conversations"] <- convs
        let idems = JsonArray()
        for KeyValue(_, r) in p.idempotency do
            idems.Add(idemToJson r)
        o["idempotency"] <- idems
        o["latestCommitId"] <- p.latestCommitId.ToString(CultureInfo.InvariantCulture)
        o

    let private projectionFromJson (o: JsonObject) : Projection option =
        let convs =
            match tryGet o "conversations" with
            | Some v when v.GetValueKind() = JsonValueKind.Array ->
                let mutable ok = true
                let map = System.Collections.Generic.Dictionary<Guid, Conversation>()
                for item in v.AsArray() do
                    if item <> null && item.GetValueKind() = JsonValueKind.Object then
                        match conversationFromJson (item.AsObject()) with
                        | Some c -> map[c.conversationId] <- c
                        | None -> ok <- false
                    else
                        ok <- false
                if ok then Some(map |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq) else None
            | _ -> None
        let idems =
            match tryGet o "idempotency" with
            | Some v when v.GetValueKind() = JsonValueKind.Array ->
                let mutable ok = true
                let map = System.Collections.Generic.Dictionary<string, IdemRecord>()
                for item in v.AsArray() do
                    if item <> null && item.GetValueKind() = JsonValueKind.Object then
                        match idemFromJson (item.AsObject()) with
                        | Some r -> map[r.commandId] <- r
                        | None -> ok <- false
                    else
                        ok <- false
                if ok then Some(map |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq) else None
            | _ -> None
        match convs, idems, tryUInt64 o "latestCommitId" with
        | Some c, Some i, Some latest -> Some { conversations = c; idempotency = i; latestCommitId = latest }
        | _ -> None

    // ---------- 校验和 ----------

    /// 信封校验和 = SHA-256(JCS(信封去掉 checksum 字段))。
    /// JCS 规范化使字段顺序不影响校验和（RFC 8785）。
    let private envelopeChecksum (o: JsonObject) : string =
        let copy = JsonObject()
        for kv in o do
            if kv.Key <> "checksum" then
                copy[kv.Key] <- kv.Value.DeepClone()
        Jcs.sha256Hex (copy.ToJsonString(JsonSerializerOptions(JsonSerializerDefaults.General)))

    // ---------- 算法 S：写快照 ----------

    /// 创建快照（算法 S）：序列化 → 写临时文件 → flush → 校验和 → 重读验证 → 原子 rename。
    /// **不删除任何事件日志**。返回正式快照文件完整路径。
    let write (dataDir: string) (projection: Projection) (throughFile: string option) (throughOffset: int64) : Result<string, string> =
        try
            let throughId = projection.latestCommitId
            let dir = snapshotsDir dataDir
            Directory.CreateDirectory dir |> ignore
            let o = JsonObject()
            o["snapshotFormatVersion"] <- FormatVersion
            o["throughCommitId"] <- throughId.ToString(CultureInfo.InvariantCulture)
            o["createdAt"] <- DateTimeOffset.UtcNow.UtcDateTime.ToString("o")
            match throughFile with
            | Some f -> o["throughFile"] <- f
            | None -> ()
            o["throughOffset"] <- throughOffset
            o["projection"] <- projectionToJson projection
            let checksum = envelopeChecksum o
            o["checksum"] <- checksum
            let bytes = Encoding.UTF8.GetBytes(o.ToJsonString(JsonSerializerOptions(JsonSerializerDefaults.General)))
            // 临时文件名含 GUID：并行测试/并发写快照时避免同名冲突
            let tmp = Path.Combine(dir, sprintf ".tmp-%d-%d-%s" Environment.ProcessId throughId (Guid.NewGuid().ToString("N")))
            do
                use fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)
                fs.Write(bytes, 0, bytes.Length)
                fs.Flush(true) // S4：耐久 flush
            // S5-S7：重新读取并验证校验和
            match JsonNode.Parse(File.ReadAllText tmp) with
            | :? JsonObject as parsed ->
                if envelopeChecksum parsed <> checksum then
                    failwith "snapshot self-check failed (checksum mismatch after write)"
            | _ -> failwith "snapshot self-check failed (re-read is not an object)"
            // S8：原子 rename
            let final = filePath dataDir throughId
            File.Move(tmp, final, true)
            Ok final
        with e -> Error(sprintf "snapshot write failed: %s" e.Message)

    // ---------- 算法 L：读快照 ----------

    /// 加载并验证单个快照文件（L3/L4：格式版本 + checksum；L5：投影）。
    let tryLoad (path: string) : Result<SnapshotInfo, string> =
        try
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as o ->
                match tryInt o "snapshotFormatVersion" with
                | Some v when v <> FormatVersion ->
                    Error(sprintf "unsupported snapshot formatVersion %d (expected %d)" v FormatVersion)
                | None -> Error "missing snapshotFormatVersion"
                | Some _ ->
                    match tryStr o "checksum" with
                    | None -> Error "missing checksum"
                    | Some stored ->
                        let actual = envelopeChecksum o
                        if stored <> actual then
                            Error "snapshot checksum mismatch"
                        else
                            match tryUInt64 o "throughCommitId", tryDateTime o "createdAt" with
                            | Some throughId, Some createdAt ->
                                match tryGet o "projection" with
                                | Some v when v.GetValueKind() = JsonValueKind.Object ->
                                    match projectionFromJson (v.AsObject()) with
                                    | Some proj when proj.latestCommitId = throughId ->
                                        Ok
                                            { throughCommitId = throughId
                                              throughFile = tryStr o "throughFile"
                                              throughOffset = tryInt64 o "throughOffset" |> Option.defaultValue 0L
                                              createdAtUtc = createdAt
                                              path = path
                                              projection = proj }
                                    | Some _ -> Error "snapshot projection latestCommitId != throughCommitId"
                                    | None -> Error "snapshot projection decode failed"
                                | _ -> Error "snapshot projection missing"
                            | _ -> Error "missing throughCommitId/createdAt"
            | _ -> Error "not a JSON object"
        with e -> Error(sprintf "snapshot load failed: %s" e.Message)

    /// 找最新有效快照（L8：损坏快照忽略，回退上一快照；全部损坏 → SnapshotCorrupt）。
    /// 按 throughCommitId 降序尝试；第一个验证通过的即最新有效。
    let findLatest (dataDir: string) : SnapshotStartup =
        let dir = snapshotsDir dataDir
        if not (Directory.Exists dir) then
            NoSnapshot
        else
            let files =
                try
                    Directory.GetFiles(dir, "*.snapshot") |> Array.sortByDescending (fun f -> Path.GetFileName f)
                with _ -> [||]
            if files.Length = 0 then
                NoSnapshot
            else
                let corrupt = ResizeArray<string>()
                let mutable found: SnapshotInfo option = None
                let mutable i = 0
                while i < files.Length && found.IsNone do
                    match tryLoad files[i] with
                    | Ok info ->
                        // L8 补充：快照锚点文件必须存在且长度 >= throughOffset，
                        // 否则快照超前于日志（覆盖文件被删/截断）——回退完整重放。
                        let stale =
                            match info.throughFile with
                            | Some f ->
                                let p = Path.Combine(DataPaths.eventsDir dataDir, f)
                                if not (File.Exists p) then Some(sprintf "snapshot anchor file %s missing" f)
                                elif FileInfo(p).Length < info.throughOffset then
                                    Some(sprintf "snapshot anchor file %s truncated (len %d < offset %d)" f (FileInfo(p).Length) info.throughOffset)
                                else None
                            | None -> None
                        match stale with
                        | Some reason ->
                            // 该快照超前：跳过它，尝试更旧的快照；全部超前 → SnapshotStale
                            corrupt.Add(sprintf "%s: %s" (Path.GetFileName files[i]) reason)
                        | None -> found <- Some info
                    | Error e -> corrupt.Add(sprintf "%s: %s" (Path.GetFileName files[i]) e)
                    i <- i + 1
                match found with
                | Some info -> SnapshotUsed info
                | None ->
                    // 区分：全是「超前」→ SnapshotStale（日志比快照新，正常回退）；
                    // 有校验失败 → SnapshotCorrupt（需要诊断）。
                    if corrupt |> Seq.exists (fun c -> not (c.Contains "missing" || c.Contains "truncated")) then
                        SnapshotCorrupt(List.ofSeq corrupt)
                    else
                        SnapshotStale(String.concat "; " corrupt)
