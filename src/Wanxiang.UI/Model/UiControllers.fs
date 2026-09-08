namespace Wanxiang.UI

open System
open System.Collections.Generic
open System.IO
open System.Text.Json.Nodes
open Avalonia.Input
open Wanxiang.Core

/// 临时生成状态按会话保存；导航只选择要显示哪一份，不拥有生成本身。
type ConversationRun = {
    generationId: Guid option
    running: bool
    message: MessageView option
    error: GenerationError option
    usage: GenerationUsage option
}

type ConversationRuns() =
    let empty = { generationId = None; running = false; message = None; error = None; usage = None }
    let mutable items: Map<Guid, ConversationRun> = Map.empty
    // 退休台账防同代事件的重复记账（重复 Finish/迟到 Start）。generationId 是 GUID
    // 不复用，台账只需覆盖「同一次连接内可能的重复/迟到投递窗口」——游标协议本身
    // 已按 cursor 去重，这里只是纵深防御。没有上限时每次生成都永久增加一个条目，
    // 长会话用户会让集合单调增长（Phase 0 风险 R2），因此给 FIFO 总量上限。
    let retiredCapacity = 1024
    let mutable retired: Set<Guid * Guid> = Set.empty
    let retiredOrder = Queue<Guid * Guid>()
    let retire (id: Guid) (generationId: Guid) =
        if not (retired.Contains(id, generationId)) then
            retired <- retired.Add(id, generationId)
            retiredOrder.Enqueue(id, generationId)
            while retiredOrder.Count > retiredCapacity do
                let oldest = retiredOrder.Dequeue()
                retired <- retired.Remove oldest

    member _.Get(id: Guid option) = id |> Option.bind items.TryFind |> Option.defaultValue empty

    member this.Snapshot(id: Guid, runtimeState: string, generationId: Guid option) =
        let current = this.Get(Some id)
        let running = ConversationSummary.isGeneratingStatus runtimeState
        let generationId = generationId |> Option.filter (fun id -> id <> Guid.Empty)
        if not (generationId |> Option.exists (fun generation -> retired.Contains(id, generation))) then
            items <- items.Add(id,
                { current with
                    running = running
                    generationId = if running then generationId else None
                    message = if running && current.generationId = generationId then current.message else None
                    error = if running then None else current.error })

    member this.Start(id: Guid, generationId: Guid) =
        let current = this.Get(Some id)
        if current.generationId <> Some generationId && not (retired.Contains(id, generationId)) then
            current.generationId |> Option.iter (fun previous -> retire id previous)
            items <- items.Add(id, { current with generationId = Some generationId; running = true; message = None; error = None })

    member this.Delta(id: Guid, generationId: Guid, message: MessageView) =
        let current = this.Get(Some id)
        if current.running && current.generationId = Some generationId then
            items <- items.Add(id, { current with message = Some message })
            true
        else false

    member this.Finish(id: Guid, generationId: Guid, error: GenerationError option, usage: GenerationUsage option) =
        let current = this.Get(Some id)
        let applicable = not (retired.Contains(id, generationId)) && (current.generationId.IsNone || current.generationId = Some generationId)
        retire id generationId
        if applicable then
            items <- items.Add(id,
                { current with generationId = None; running = false; message = None; error = error
                               usage = usage |> Option.orElse current.usage })
            true
        else false

    member this.ClearMessage(id: Guid) =
        items <- items.Add(id, { this.Get(Some id) with message = None })

    member this.ClearError(id: Guid) =
        items <- items.Add(id, { this.Get(Some id) with error = None })

    member _.Disconnect() =
        items <- items |> Map.map (fun _ item -> { item with generationId = None; running = false; message = None })

    /// 退休台账当前条目数（诊断/测试用：证明 FIFO 淘汰在生效）。
    member _.RetiredCount = retired.Count

    member _.Clear() =
        items <- Map.empty
        retired <- Set.empty
        retiredOrder.Clear()

/// Composer 中的一条附件草稿。用 attachmentId 而不是 sha256 作为身份：
/// 同一文件被选择两次时，两条草稿仍能独立上传、删除和完成。
type PendingAttachment = {
    attachmentId: Guid
    sha256: string
    size: int64
    mediaType: string
    fileName: string
    /// 上传中时为 false
    ready: bool
    /// 上传失败后保留的可见残留：仍占一条 chip，可移除、可重新添加重试；发送前必须先清理。
    failed: bool
}

/// 附件传输所需元数据。协议层完成/失败事件通过 attachmentId 回来。
type AttachmentUpload = {
    attachmentId: Guid
    fileName: string
    mediaType: string
    size: int64
    sha256: string
}

/// 附件草稿状态机。UI 只消费 Items，不再自己同时维护 pending list + upload dictionary。
type AttachmentDraftController() =
    let uploads = Dictionary<Guid, AttachmentUpload>()
    let mutable items: PendingAttachment list = []
    /// 所属会话键，由 ComposerDrafts 在创建/搬移草稿时维护；Complete 透出它，
    /// AppShell 只在它等于当前可见会话时才 toast 上传完成。
    let mutable ownerKey: string * Guid option = "", None

    member _.Items = items
    member _.HasReady = items |> List.exists _.ready
    member _.HasUploading = items |> List.exists (fun item -> not item.ready)
    member _.HasFailed = items |> List.exists _.failed
    member _.OwnerKey with get () = ownerKey and set value = ownerKey <- value

    member _.Begin(upload: AttachmentUpload) =
        uploads[upload.attachmentId] <- upload
        items <-
            items
            @ [ { attachmentId = upload.attachmentId
                  sha256 = upload.sha256
                  size = upload.size
                  mediaType = upload.mediaType
                  fileName = upload.fileName
                  ready = false
                  failed = false } ]
        items

    /// 用户从草稿中移除后，底层上传可以自然完成，但不会再重新出现在 Composer。
    member _.Remove(attachmentId: Guid) =
        items <- items |> List.filter (fun item -> item.attachmentId <> attachmentId)
        items

    /// 返回上传元数据，以及完成时这条附件是否仍在用户草稿里。
    /// 一并透出所属会话键：调用方只在它等于当前可见会话时才 toast，避免后台会话的完成误报。
    member _.Complete(attachmentId: Guid, committedSize: int64) : (AttachmentUpload * bool * (string * Guid option)) option =
        match uploads.TryGetValue attachmentId with
        | true, upload ->
            uploads.Remove attachmentId |> ignore
            let mutable stillDrafted = false
            items <-
                items
                |> List.map (fun item ->
                    if item.attachmentId = attachmentId then
                        stillDrafted <- true
                        { item with ready = true; failed = false; size = committedSize }
                    else
                        item)
            Some(upload, stillDrafted, ownerKey)
        | _ -> None

    /// 失败时保留一条 failed 残留（同槽位 chip 呈现，可移除、可重新添加重试）；相同 sha256 的其它附件不受影响。
    /// 返回上传元数据（fileName 即 toast 用名）、是否仍在草稿里、所属会话键；同一 attachmentId 只报一次。
    member _.Abort(attachmentId: Guid) : (AttachmentUpload * bool * (string * Guid option)) option =
        match uploads.TryGetValue attachmentId with
        | true, upload ->
            uploads.Remove attachmentId |> ignore
            let stillDrafted = items |> List.exists (fun item -> item.attachmentId = attachmentId)
            items <-
                items
                |> List.map (fun item ->
                    if item.attachmentId = attachmentId then { item with ready = false; failed = true } else item)
            Some(upload, stillDrafted, ownerKey)
        | _ -> None


    /// 断连时把所有 uploading 翻成 failed 残留并返回它们；语义上发送仍被阻塞，
    /// 但用户移除残留后即可恢复，不会因“永远上传中”楔死发送。
    member _.MarkAllUploadingAsFailed() : PendingAttachment list =
        let failedNow = ResizeArray<PendingAttachment>()
        items <-
            items
            |> List.map (fun item ->
                if not item.ready && not item.failed then
                    uploads.Remove item.attachmentId |> ignore
                    let failed = { item with ready = false; failed = true }
                    failedNow.Add failed
                    failed
                else item)
        List.ofSeq failedNow

    /// 发送是草稿的原子消费点。只要仍有 uploading，就拒绝消费。
    member _.TryConsumeReady() : PendingAttachment list option =
        if items |> List.exists (fun item -> not item.ready) then
            None
        else
            let consumed = items
            items <- []
            Some consumed

/// 附件下载的在途接收缓冲，键为小写 sha256。
///
/// 服务端按 chunk 推流，只有完成事件会消费缓冲。断连、重试或完成事件丢失时
/// 缓冲必须能整体丢弃，否则每个半途而废的下载都会在内存里留下一个永不释放的
/// MemoryStream（Phase 0 风险 R1）。单个下载另有硬上限，超限即丢弃缓冲并让
/// 调用方给出一次性提示，内存不随异常流无限增长。不持久化任何内容；
/// 断连由 AppShell 调用 Clear 清零。
type DownloadChunkResult =
    | Accepted
    /// 本次写入触顶：缓冲已整体丢弃，调用方应提示一次
    | Capped
    /// 没有在途缓冲（尚未 Begin、已被 Capped 清掉或已完成）
    | Unknown

type DownloadBuffers(?capBytes: int64) =
    let capBytes = defaultArg capBytes (128L * 1024L * 1024L)
    let buffers = Dictionary<string, MemoryStream>()
    let names = Dictionary<string, string>()

    member _.PendingCount = buffers.Count

    /// 开始接收。同键重复 Begin（重试）会重置缓冲并更新文件名，不叠加第二个流。
    member _.Begin(sha256: string, fileName: string) =
        let key = sha256.ToLowerInvariant()
        match buffers.TryGetValue key with
        | true, existing ->
            existing.Dispose()
            buffers[key] <- new MemoryStream()
        | _ -> buffers[key] <- new MemoryStream()
        names[key] <- fileName

    member _.Append(sha256: string, bytes: byte[]) : DownloadChunkResult =
        let key = sha256.ToLowerInvariant()
        match buffers.TryGetValue key with
        | true, buffer ->
            if int64 buffer.Length + int64 bytes.Length > capBytes then
                buffer.Dispose()
                buffers.Remove key |> ignore
                names.Remove key |> ignore
                Capped
            else
                buffer.Write(bytes, 0, bytes.Length)
                Accepted
        | _ -> Unknown

    /// 完成接收：取走文件名与字节并移除缓冲。文件名缺失时回落到 sha256。
    member _.Take(sha256: string) : (string * byte[]) option =
        let key = sha256.ToLowerInvariant()
        match buffers.TryGetValue key with
        | true, buffer ->
            buffers.Remove key |> ignore
            let fileName =
                match names.TryGetValue key with
                | true, name ->
                    names.Remove key |> ignore
                    name
                | _ -> sha256
            let bytes = buffer.ToArray()
            buffer.Dispose()
            Some(fileName, bytes)
        | _ -> None

    /// 断连/重连：底层流已死，不会再有完成事件，丢弃所有在途缓冲。
    member _.Clear() =
        for entry in buffers do
            entry.Value.Dispose()
        buffers.Clear()
        names.Clear()

type ComposerDraft = {
    mutable text: string
    attachments: AttachmentDraftController
}

/// 草稿仅在客户端内存中保存，按实例和会话隔离；不构成第二份业务数据库。
type ComposerDrafts() =
    let mutable items: Map<string * Guid option, ComposerDraft> = Map.empty

    member _.Get(instanceId: string, conversationId: Guid option) =
        let key = instanceId, conversationId
        match items.TryFind key with
        | Some draft -> draft
        | None ->
            let draft = { text = ""; attachments = AttachmentDraftController() }
            draft.attachments.OwnerKey <- key
            items <- items.Add(key, draft)
            draft

    member _.Move(instanceId: string, source: Guid option, target: Guid option) =
        match items.TryFind(instanceId, source) with
        | Some draft ->
            draft.attachments.OwnerKey <- (instanceId, target)
            items <- items.Remove((instanceId, source)).Add((instanceId, target), draft)
        | None -> ()

    member _.All = items |> Map.toSeq |> Seq.map snd

type DeliveryState =
    | PreparingConversation
    | SendingMessage
    | QueuedMessage
    | UnconfirmedMessage of string
    | RejectedMessage of string

type PendingMessage = {
    instanceId: string
    conversationId: Guid
    command: ClientCommand
    creation: ClientCommand option
    creationConfirmed: bool
    text: string
    attachments: PendingAttachment list
    state: DeliveryState
    lastAttemptAt: DateTimeOffset
}

module PendingMessage =
    let invocationId item = ClientCommand.invocationId item.command
    let canRetry item =
        match item.state with UnconfirmedMessage _ | RejectedMessage _ -> true | _ -> false
    let status item =
        match item.state with
        | PreparingConversation -> "正在准备会话…"
        | SendingMessage -> "正在发送，等待保存确认…"
        | QueuedMessage -> "已排队，尚未保存"
        | UnconfirmedMessage reason -> "尚未确认保存：" + reason
        | RejectedMessage reason -> "未发送：" + reason

/// 输入框只把内容交给这里；直到 command.committed 才释放。
/// 重试始终使用原命令、原 invocationId 和原附件引用，不猜测服务端是否已写入。
type MessageOutbox() =
    let mutable items: Map<Guid, PendingMessage> = Map.empty

    member _.ForInstance(instanceId: string) =
        items |> Map.toList |> List.map snd |> List.filter (fun item -> item.instanceId = instanceId)

    member _.Items(instanceId: string, conversationId: Guid option) =
        items |> Map.toList |> List.map snd
        |> List.filter (fun item -> item.instanceId = instanceId && Some item.conversationId = conversationId)

    member _.TryFind(id: Guid) = items.TryFind id
    member _.Count = items.Count

    member _.Stage(instanceId: string, conversationId: Guid, text: string, attachments: PendingAttachment list, creation: ClientCommand option) =
        let contents = JsonArray()
        if not (String.IsNullOrWhiteSpace text) then
            let node = JsonObject()
            node["text"] <- text
            contents.Add node
        for attachment in attachments do
            let node = JsonObject()
            node["type"] <- "attachment"
            node["sha256"] <- attachment.sha256
            node["size"] <- attachment.size
            node["mediaType"] <- attachment.mediaType
            node["fileName"] <- attachment.fileName
            contents.Add node
        let message = JsonObject()
        message["role"] <- "user"
        message["contents"] <- contents
        let id = Guid.CreateVersion7()
        let item =
            { instanceId = instanceId; conversationId = conversationId
              command = SendUserMessage {| invocationId = id; conversationId = conversationId; messageJson = message |}
              creation = creation; creationConfirmed = creation.IsNone
              text = text; attachments = attachments
              state = (if creation.IsSome then PreparingConversation else SendingMessage)
              lastAttemptAt = DateTimeOffset.UtcNow }
        items <- items.Add(id, item)
        item

    member _.SetState(id: Guid, state: DeliveryState) =
        items <- items |> Map.change id (Option.map (fun item ->
            { item with state = state
                        lastAttemptAt =
                            match state with
                            | PreparingConversation | SendingMessage -> DateTimeOffset.UtcNow
                            | _ -> item.lastAttemptAt }))

    member _.Expire(now: DateTimeOffset, timeout: TimeSpan) =
        let mutable changed = false
        items <- items |> Map.map (fun _ item ->
            match item.state with
            | PreparingConversation | SendingMessage when now - item.lastAttemptAt >= timeout ->
                changed <- true
                { item with state = UnconfirmedMessage "暂未收到确认；内容已保留，可重试核对。" }
            | _ -> item)
        changed

    member _.ConversationReady(instanceId: string, conversationId: Guid) =
        items <- items |> Map.map (fun _ item ->
            if item.instanceId = instanceId && item.conversationId = conversationId then { item with creationConfirmed = true }
            else item)

    member this.Accept(id: Guid) =
        if items.ContainsKey id then this.SetState(id, QueuedMessage)

    member _.Commit(id: Guid) = items <- items.Remove id

    member _.RejectCreation(id: Guid, reason: string) =
        items <- items |> Map.map (fun _ item ->
            if item.creation |> Option.exists (fun command -> ClientCommand.invocationId command = id) then
                { item with state = RejectedMessage reason }
            else item)

    member _.Disconnect(instanceId: string) =
        items <- items |> Map.map (fun _ item ->
            if item.instanceId = instanceId then
                { item with state = UnconfirmedMessage "连接已断开；重试会核对原命令，不会重复保存。" }
            else item)

/// 一个命令对应的 UI 后续动作。只有服务端 CommandCommitted 后才执行。
type CommandFeedback = {
    successMessage: string option
    onCommitted: unit -> unit
    onCompleted: bool -> unit
}

/// request → commit/reject 的轻量状态机，避免 MainView 散落维护 invocation dictionary。
type CommandFeedbackTracker() =
    let pending = Dictionary<Guid, CommandFeedback>()

    member _.Count = pending.Count

    member _.Track(cmd: ClientCommand, successMessage: string option, onCommitted: unit -> unit) =
        let invocationId = ClientCommand.invocationId cmd
        pending[invocationId] <-
            { successMessage = successMessage
              onCommitted = onCommitted
              onCompleted = ignore }

    member _.TrackWithCompletion(
        cmd: ClientCommand,
        successMessage: string option,
        onCommitted: unit -> unit,
        onCompleted: bool -> unit
    ) =
        pending[ClientCommand.invocationId cmd] <-
            { successMessage = successMessage
              onCommitted = onCommitted
              onCompleted = onCompleted }

    member _.Commit(invocationId: Guid) =
        match pending.TryGetValue invocationId with
        | true, feedback ->
            pending.Remove invocationId |> ignore
            Some feedback
        | _ -> None

    member _.Reject(invocationId: Guid) =
        match pending.TryGetValue invocationId with
        | true, feedback ->
            pending.Remove invocationId |> ignore
            Some feedback
        | _ -> None

    member _.RejectAll() =
        let callbacks = pending.Values |> Seq.map _.onCompleted |> Array.ofSeq
        pending.Clear()
        callbacks |> Array.iter (fun callback -> callback false)

    member _.Clear() = pending.Clear()

/// 配置写入同样遵循 request → ConfigApplied 的权威确认。
type ConfigFeedback = {
    successMessage: string
    onCompleted: bool -> unit
}

type ConfigFeedbackTracker() =
    let pending = Dictionary<Guid, ConfigFeedback>()

    member _.Count = pending.Count

    member _.Track(requestId: Guid, successMessage: string, onCompleted: bool -> unit) =
        pending[requestId] <-
            { successMessage = successMessage
              onCompleted = onCompleted }

    member _.Resolve(requestId: Guid) =
        match pending.TryGetValue requestId with
        | true, feedback ->
            pending.Remove requestId |> ignore
            Some feedback
        | _ -> None

    /// 连接断开时让所有等待中的编辑器退出 pending，避免永久灰掉。
    member _.RejectAll() =
        let callbacks = pending.Values |> Seq.map _.onCompleted |> Array.ofSeq
        pending.Clear()
        callbacks |> Array.iter (fun callback -> callback false)

/// 主壳导航的纯状态。视图只负责把这个状态投影成 Grid/Visibility；
/// “窗口变窄时该开什么、Ctrl+B 在 compact/desktop 分别意味着什么”集中在这里。
type NavigationSnapshot = {
    compactMode: bool
    compactNavigationOpen: bool
    sidebarCollapsed: bool
}

type NavigationController(initialSidebarCollapsed: bool) =
    let mutable state =
        { compactMode = false
          compactNavigationOpen = false
          sidebarCollapsed = initialSidebarCollapsed }
    let mutable viewportApplied = false

    member _.State = state

    /// 返回是否需要重新投影布局，以及更新后的状态。
    member _.ApplyViewport(width: float, hasConversation: bool) : bool * NavigationSnapshot =
        if width <= 0.0 then false, state
        else
            let compact = width < LayoutPolicy.compactBreakpoint
            let changed = compact <> state.compactMode
            let needsApply = changed || not viewportApplied
            if needsApply then
                viewportApplied <- true
                state <-
                    if compact then
                        { state with
                            compactMode = true
                            compactNavigationOpen = if changed then not hasConversation else state.compactNavigationOpen }
                    else
                        { state with compactMode = false; compactNavigationOpen = false }
            needsApply, state

    member _.ToggleSidebar() =
        state <-
            if state.compactMode then
                { state with compactNavigationOpen = not state.compactNavigationOpen }
            else
                { state with sidebarCollapsed = not state.sidebarCollapsed }
        state

    member _.SetCompactNavigation(opened: bool) =
        if state.compactMode then state <- { state with compactNavigationOpen = opened }
        state

    member _.EnsureSearchVisible() =
        state <-
            if state.compactMode then { state with compactNavigationOpen = true }
            elif state.sidebarCollapsed then { state with sidebarCollapsed = false }
            else state
        state

/// 全局快捷键只做“按键 → 意图”解析；是否允许在 modal 打开时执行、
/// 以及具体副作用由 MainView 决定。
type ShortcutAction =
    | Escape
    | ToggleSidebar
    | NewConversation
    | FocusSearch
    | OpenSettings
    | ToggleTheme
    | ExportConversation
    | OpenConversationAt of int
    | ShowShortcuts
    | NoShortcut

module ShortcutRouter =

    let resolve (e: KeyEventArgs) =
        let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
        let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
        if e.Key = Key.Escape then Escape
        elif ctrl && e.Key = Key.B then ToggleSidebar
        elif ctrl && e.Key = Key.N then NewConversation
        elif ctrl && e.Key = Key.K then FocusSearch
        elif ctrl && e.Key = Key.OemComma then OpenSettings
        elif ctrl && shift && e.Key = Key.S then ToggleTheme
        elif ctrl && shift && e.Key = Key.E then ExportConversation
        elif ctrl && not shift && e.Key >= Key.D1 && e.Key <= Key.D9 then
            OpenConversationAt(int e.Key - int Key.D1)
        elif ctrl && (e.Key = Key.OemQuestion || e.Key = Key.Divide) then ShowShortcuts
        elif e.Key = Key.F1 then ShowShortcuts
        else NoShortcut
