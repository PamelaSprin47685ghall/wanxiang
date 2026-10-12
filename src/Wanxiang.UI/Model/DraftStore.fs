namespace Wanxiang.UI

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Wanxiang.Interop

/// 本地草稿与未发送消息（W5，决策 80/81）。
///
/// 这是**客户端本机**的便利存储，不是业务数据：
/// - 不入事件日志、不上传、不同步（决策 195：本机客户端偏好的同一类）；
/// - 按「主机 instanceId + 对话 id」索引，切换主机互不泄漏；
/// - 只存文字与附件引用（名称/类型/大小/sha256），**不存附件字节、不存令牌或密钥**；
/// - 桌面写 `$WANXIANG_HOME/drafts.json`（与 client.toml、ui.json 同目录，仅当前用户可读写）；
///   PWA 写 IndexedDB；
/// - 用户可以随时关闭（立即删除已存内容）或一键清除。
type DraftAttachmentRef = {
    fileName: string
    mediaType: string
    size: int64
    sha256: string
}

/// 一份未发送的输入。conversationId = None 表示还没有对话的起步输入。
type StoredDraft = {
    instanceId: string
    conversationId: Guid option
    text: string
    attachments: DraftAttachmentRef list
}

/// 一条未被主机确认保存的发送。保留原 invocationId，重试时由主机幂等核对。
type StoredUnsent = {
    instanceId: string
    conversationId: Guid
    invocationId: Guid
    text: string
    attachments: DraftAttachmentRef list
    savedAt: DateTimeOffset
}

type DraftSnapshot = {
    drafts: StoredDraft list
    unsent: StoredUnsent list
}

module DraftSnapshot =
    let empty = { drafts = []; unsent = [] }
    let isEmpty (s: DraftSnapshot) = List.isEmpty s.drafts && List.isEmpty s.unsent

module DraftCodec =

    /// 文件外壳版本：新增字段只加不改，旧版读到未知字段忽略；读到更高版本整份忽略（不覆盖、不报错）。
    let formatVersion = 1

    let private refToJson (r: DraftAttachmentRef) =
        let o = JsonObject()
        o["fileName"] <- r.fileName
        o["mediaType"] <- r.mediaType
        o["size"] <- r.size
        o["sha256"] <- r.sha256
        o

    let serialize (snapshot: DraftSnapshot) : string =
        let root = JsonObject()
        root["version"] <- formatVersion
        let drafts = JsonArray()
        for d in snapshot.drafts do
            let o = JsonObject()
            o["instanceId"] <- d.instanceId
            (match d.conversationId with
             | Some id -> o["conversationId"] <- id.ToString "D"
             | None -> ())
            o["text"] <- d.text
            o["attachments"] <- JsonArray([| for r in d.attachments -> refToJson r :> JsonNode |])
            drafts.Add o
        root["drafts"] <- drafts
        let unsent = JsonArray()
        for u in snapshot.unsent do
            let o = JsonObject()
            o["instanceId"] <- u.instanceId
            o["conversationId"] <- u.conversationId.ToString "D"
            o["invocationId"] <- u.invocationId.ToString "D"
            o["text"] <- u.text
            o["attachments"] <- JsonArray([| for r in u.attachments -> refToJson r :> JsonNode |])
            o["savedAt"] <- u.savedAt.ToString "O"
            unsent.Add o
        root["unsent"] <- unsent
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))

    let private str (o: JsonObject) key =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then Some(n.GetValue<string>()) else None

    let private guid (o: JsonObject) key =
        str o key |> Option.bind (fun s -> match Guid.TryParse s with | true, g -> Some g | _ -> None)

    let private refs (o: JsonObject) =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue("attachments", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Array then
            [ for item in n.AsArray() do
                  if not (isNull item) && item.GetValueKind() = JsonValueKind.Object then
                      let a = item.AsObject()
                      let size =
                          let mutable s: JsonNode = null
                          if a.TryGetPropertyValue("size", &s) && not (isNull s) && s.GetValueKind() = JsonValueKind.Number then s.GetValue<int64>() else 0L
                      yield
                          { fileName = str a "fileName" |> Option.defaultValue ""
                            mediaType = str a "mediaType" |> Option.defaultValue "application/octet-stream"
                            size = size
                            sha256 = str a "sha256" |> Option.defaultValue "" } ]
        else []

    /// 宽容读取：任何损坏、未知版本、字段缺失都退化为「少恢复一些」，绝不抛出、绝不阻塞启动。
    let tryParse (text: string) : DraftSnapshot =
        try
            match JsonNode.Parse text with
            | null -> DraftSnapshot.empty
            | node when node.GetValueKind() <> JsonValueKind.Object -> DraftSnapshot.empty
            | node ->
                let root = node.AsObject()
                let version =
                    let mutable v: JsonNode = null
                    if root.TryGetPropertyValue("version", &v) && not (isNull v) && v.GetValueKind() = JsonValueKind.Number then v.GetValue<int>() else 0
                if version < 1 || version > formatVersion then DraftSnapshot.empty
                else
                    let items key =
                        let mutable n: JsonNode = null
                        if root.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Array then
                            [ for item in n.AsArray() do
                                  if not (isNull item) && item.GetValueKind() = JsonValueKind.Object then yield item.AsObject() ]
                        else []
                    { drafts =
                        [ for o in items "drafts" do
                              match str o "instanceId" with
                              | Some instanceId ->
                                  yield
                                      { instanceId = instanceId
                                        conversationId = guid o "conversationId"
                                        text = str o "text" |> Option.defaultValue ""
                                        attachments = refs o }
                              | None -> () ]
                      unsent =
                        [ for o in items "unsent" do
                              match str o "instanceId", guid o "conversationId", guid o "invocationId" with
                              | Some instanceId, Some conversationId, Some invocationId ->
                                  yield
                                      { instanceId = instanceId
                                        conversationId = conversationId
                                        invocationId = invocationId
                                        text = str o "text" |> Option.defaultValue ""
                                        attachments = refs o
                                        savedAt =
                                            match str o "savedAt" |> Option.map (fun s -> DateTimeOffset.TryParse s) with
                                            | Some(true, t) -> t
                                            | _ -> DateTimeOffset.UtcNow }
                              | _ -> () ] }
        with _ ->
            DraftSnapshot.empty

/// 草稿落点。桌面是文件，PWA 是 IndexedDB；测试用内存实现模拟「页面被回收后重开」。
type IDraftBackend =
    abstract Load: unit -> Task<string option>
    abstract Save: string -> Task
    abstract Clear: unit -> Task

/// 桌面：原子写（临时文件 + rename），文件仅当前用户可读写（Q118 同口径）。
type FileDraftBackend(path: string) =
    member _.Path = path
    interface IDraftBackend with
        member _.Load() =
            Task.FromResult(
                try
                    if File.Exists path then Some(File.ReadAllText path) else None
                with _ -> None)
        member _.Save(text) =
            try
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                let tmp = path + ".tmp"
                File.WriteAllText(tmp, text, UTF8Encoding false)
                try File.SetUnixFileMode(tmp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
                File.Move(tmp, path, true)
            with _ -> ()
            Task.CompletedTask
        member _.Clear() =
            try if File.Exists path then File.Delete path with _ -> ()
            Task.CompletedTask

    static member DefaultPath() =
        let home =
            match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
            | s when not (String.IsNullOrWhiteSpace s) -> s
            | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")
        Path.Combine(home, "drafts.json")

/// 进程内存：测试替身，也是「宿主没有任何持久存储」时的退路（此时草稿仍按进程内存工作，与旧行为一致）。
type MemoryDraftBackend() =
    let mutable stored: string option = None
    member _.Stored = stored
    interface IDraftBackend with
        member _.Load() = Task.FromResult stored
        member _.Save(text) =
            stored <- Some text
            Task.CompletedTask
        member _.Clear() =
            stored <- None
            Task.CompletedTask

/// PWA：IndexedDB（经 BrowserBridge；wwwroot/main.js 的 draftGet/draftPut/draftDelete）。
type BrowserDraftBackend() =
    interface IDraftBackend with
        member _.Load() =
            task {
                try
                    let! text = BrowserBridge.DraftGet()
                    return if String.IsNullOrWhiteSpace text then None else Some text
                with _ -> return None
            }
        member _.Save(text) =
            task { try do! BrowserBridge.DraftPut text with _ -> () } :> Task
        member _.Clear() =
            task { try do! BrowserBridge.DraftDelete() with _ -> () } :> Task

/// 草稿存储：把「是否启用」的唯一判断收在这里。关闭时不写、也不读，并删除已存内容。
type DraftStore(backend: IDraftBackend, isEnabled: unit -> bool) =
    let mutable lastSaved: string option = None

    /// 平台默认落点：桌面文件 / PWA IndexedDB。开关由调用方（外壳持有的偏好）决定。
    static member Default(isEnabled: unit -> bool) : DraftStore =
        let backend: IDraftBackend =
            if OperatingSystem.IsBrowser() then BrowserDraftBackend() :> IDraftBackend
            else FileDraftBackend(FileDraftBackend.DefaultPath()) :> IDraftBackend
        DraftStore(backend, isEnabled)

    member _.Enabled = isEnabled ()

    member _.Load() : Task<DraftSnapshot> =
        task {
            if not (isEnabled ()) then return DraftSnapshot.empty
            else
                let! text = backend.Load()
                match text with
                | Some t ->
                    lastSaved <- Some t
                    return DraftCodec.tryParse t
                | None -> return DraftSnapshot.empty
        }

    /// 内容没变就不写；空快照等于删除，磁盘上不留空壳。
    member _.Save(snapshot: DraftSnapshot) : Task =
        task {
            if isEnabled () then
                if DraftSnapshot.isEmpty snapshot then
                    if lastSaved.IsSome then
                        lastSaved <- None
                        do! backend.Clear()
                else
                    let text = DraftCodec.serialize snapshot
                    if lastSaved <> Some text then
                        lastSaved <- Some text
                        do! backend.Save text
        } :> Task

    /// 清除本地草稿（设置里的显式动作，以及关闭保存时）：删除落盘内容。
    member _.Clear() : Task =
        task {
            lastSaved <- None
            do! backend.Clear()
        } :> Task

/// 草稿内存状态 ⇄ 可落盘快照的转换（纯函数，便于测试）。
module DraftPolicy =

    let private refOf (a: PendingAttachment) : DraftAttachmentRef =
        { fileName = a.fileName; mediaType = a.mediaType; size = a.size; sha256 = a.sha256 }

    /// 恢复出来的引用：失败残留的形状（占 chip、阻塞发送、可移除），并登记为「需重新选择」。
    let private pendingOf (r: DraftAttachmentRef) : PendingAttachment =
        let id = Guid.NewGuid()
        RestoredAttachments.mark id
        { attachmentId = id
          sha256 = r.sha256
          size = r.size
          mediaType = r.mediaType
          fileName = r.fileName
          ready = false
          failed = true }

    /// 当前内存 → 快照。
    /// - 草稿：文字或附件非空才存；附件只存「已就绪」与「已是恢复引用」的（上传中/失败的字节本机没有意义）。
    /// - 未发送：outbox 中「未确认/被拒/排队/发送中」且对话已确认创建的，存为未确认；
    ///   对话尚未确认创建的，其文字折回该主机的起步草稿（对话在主机并不存在，没有可重试的目标）。
    let snapshotOf (drafts: ComposerDrafts) (outbox: MessageOutbox) (savedAtOf: Guid -> DateTimeOffset) : DraftSnapshot =
        let draftRows =
            [ for (instanceId, conversationId), draft in drafts.Entries do
                  if not (String.IsNullOrEmpty instanceId) then
                      let refs =
                          [ for a in draft.attachments.Items do
                                if (a.ready || RestoredAttachments.isRestored a.attachmentId) && not (String.IsNullOrEmpty a.sha256) then yield refOf a ]
                      if not (String.IsNullOrWhiteSpace draft.text) || not (List.isEmpty refs) then
                          yield
                              { instanceId = instanceId
                                conversationId = conversationId
                                text = draft.text
                                attachments = refs } ]
        let items =
            [ for instanceId in (outbox.AllInstances()) do yield! outbox.ForInstance instanceId ]
            |> List.filter (fun item -> item.state <> PreparingConversation || item.creationConfirmed)
        let confirmed = items |> List.filter _.creationConfirmed
        let orphaned = items |> List.filter (fun item -> not item.creationConfirmed)
        let unsent =
            [ for item in confirmed do
                  yield
                      { instanceId = item.instanceId
                        conversationId = item.conversationId
                        invocationId = PendingMessage.invocationId item
                        text = item.text
                        attachments = item.attachments |> List.map refOf
                        savedAt = savedAtOf (PendingMessage.invocationId item) } ]
        // 未确认创建的对话：折回起步草稿
        let foldedBack =
            orphaned
            |> List.groupBy _.instanceId
            |> List.map (fun (instanceId, group) ->
                let text = group |> List.map _.text |> List.filter (fun t -> not (String.IsNullOrWhiteSpace t)) |> String.concat "\n\n"
                instanceId, text, group |> List.collect (fun i -> i.attachments |> List.map refOf))
        let merged =
            foldedBack
            |> List.fold
                (fun (rows: StoredDraft list) (instanceId, text, refs) ->
                    match rows |> List.tryFind (fun r -> r.instanceId = instanceId && r.conversationId.IsNone) with
                    | Some existing ->
                        let joined = [ existing.text; text ] |> List.filter (fun t -> not (String.IsNullOrWhiteSpace t)) |> String.concat "\n\n"
                        rows |> List.map (fun r -> if obj.ReferenceEquals(r, existing) then { r with text = joined; attachments = r.attachments @ refs } else r)
                    | None ->
                        if String.IsNullOrWhiteSpace text && List.isEmpty refs then rows
                        else rows @ [ { instanceId = instanceId; conversationId = None; text = text; attachments = refs } ])
                draftRows
        { drafts = merged; unsent = unsent }

    /// 落盘用的合并：内存是「已恢复主机」的唯一事实；还没连接过的其他主机，其已存草稿原样保留，
    /// 不能因为这次没打开它就被一次保存抹掉。
    let mergeForSave (stored: DraftSnapshot) (restoredInstances: Set<string>) (current: DraftSnapshot) : DraftSnapshot =
        { drafts = (stored.drafts |> List.filter (fun d -> not (restoredInstances.Contains d.instanceId))) @ current.drafts
          unsent = (stored.unsent |> List.filter (fun u -> not (restoredInstances.Contains u.instanceId))) @ current.unsent }

    /// 快照 → 内存。只恢复指定主机的内容；已有内容（用户这次已经输入的）不被覆盖。
    /// 返回恢复的未确认发送条数（用于提示）。
    let restoreInto (snapshot: DraftSnapshot) (instanceId: string) (drafts: ComposerDrafts) (outbox: MessageOutbox) : int =
        for d in snapshot.drafts do
            if d.instanceId = instanceId then
                let live = drafts.Get(instanceId, d.conversationId)
                if String.IsNullOrWhiteSpace live.text then live.text <- d.text
                live.attachments.RestoreReferences(d.attachments |> List.map pendingOf) |> ignore
        let mutable restored = 0
        for u in snapshot.unsent do
            if u.instanceId = instanceId && (outbox.TryFind u.invocationId).IsNone then
                let attachments =
                    u.attachments
                    |> List.map (fun r ->
                        { attachmentId = Guid.NewGuid(); sha256 = r.sha256; size = r.size
                          mediaType = r.mediaType; fileName = r.fileName; ready = true; failed = false })
                outbox.Restore(instanceId, u.conversationId, u.invocationId, u.text, attachments, "上次关闭前未确认保存；可重试，不会重复保存。") |> ignore
                restored <- restored + 1
        restored

    /// 已被删除的对话：曾经存在（known）而最新列表里没有，且没有在途发送。返回应清理的对话 id。
    let deletedConversations (listed: Set<Guid>) (known: Set<Guid>) (inFlight: Set<Guid>) : Set<Guid> =
        known |> Set.filter (fun id -> not (listed.Contains id) && not (inFlight.Contains id))
