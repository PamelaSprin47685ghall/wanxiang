namespace Wanxiang.Server

open System
open System.Collections.Concurrent
open System.Net.WebSockets
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Store

/// 命令执行结果（由 ServerApp 注入的 executeCommand 产生）。
type CommandExecutionResult =
    /// SendUserMessage：已入队（等待插入点提交）
    | CommandQueued
    /// 已提交
    | CommandCommitted of commitId: CommitId
    /// 幂等命中（原提交）
    | CommandIdempotent of commitId: CommitId
    | CommandFailed of WanxiangError

/// 握手状态机（决策 55/69/70）：握手先于认证；认证前只接受认证/配对事件。
/// 未收到 Hello 或认证完成前收到业务事件 → 视为协议违规关闭。
type HandshakeState =
    | NotStarted
    | HelloSeen
    | Authenticated

/// 连接状态与处理循环。
/// 遵循：
/// - fire-and-forget 对称事件协议（决策 25）；
/// - 单写者 FIFO 发送队列（决策 31）；
/// - 慢客户端切换为快照重同步，不无限积压（决策 32-34）；
/// - 写权限由命令涉及投影水位判断（决策 35/38）；
/// - 观察关系绑定连接，断线自动释放（决策 28）。
type WsConnection(
    connectionId: int,
    ws: WebSocket,
    remoteAddress: string,
    getProjection: unit -> Projection,
    getConfig: unit -> AppConfig,
    updateConfig: (AppConfig -> AppConfig) -> Result<unit, string>,
    executeCommand: CommitId -> ClientCommand -> CommandExecutionResult,
    orchestrator: ChatOrchestrator,
    attachmentStore: AttachmentStore,
    getCommitsAfter: CommitId -> Events.Commit list,
    pairing: Auth.PairingState,
    failureTracker: Auth.FailureTracker,
    logPairingCode: string -> unit,
    logInfo: string -> unit) =

    let channelOptions = BoundedChannelOptions(2048, FullMode = BoundedChannelFullMode.Wait)
    let sendChannel = Channel.CreateBounded<WireEvent>(channelOptions)
    let mutable handshakeState: HandshakeState = NotStarted
    let mutable tokenHash: string option = None
    let mutable observedList = false
    let mutable observedConversations: Set<Guid> = Set.empty
    /// 游标/快照状态跨线程读写保护（P3-1：recvTask 与 catch-up Task 并发）。
    let cursorLock = obj()
    let mutable appliedCursor: CommitId = 0UL
    let mutable awaitingCursor: CommitId option = None
    let mutable snapshotMode = false
    let mutable closed = false
    let mutable pairingRequested = false
    /// catch-up 进行中标记（Interlocked，防多入口并发发送重复批次）。
    let mutable catchUpRunning = 0

    /// 快照携带的最大消息数（Q127/P1-2：长会话只带尾部，更早历史走 history.request 分页）。
    let snapshotMessageLimit = 200

    member _.TrySend(ev: WireEvent) : bool =
        let ok = sendChannel.Writer.TryWrite ev
        ok

    member _.IsAuthenticated =
        match lock cursorLock (fun () -> handshakeState) with
        | Authenticated -> true
        | _ -> false
    member _.TokenHash = lock cursorLock (fun () -> tokenHash)
    member _.Observes(convId: Guid) : bool = lock cursorLock (fun () -> observedConversations.Contains convId)
    member _.ObservesList = lock cursorLock (fun () -> observedList)
    member _.AppliedCursor = lock cursorLock (fun () -> appliedCursor)
    member _.IsInSnapshotMode = lock cursorLock (fun () -> snapshotMode)

    /// 强制关闭（令牌吊销时调用，决策 58）。
    member _.ForceClose() : unit =
        lock cursorLock (fun () -> closed <- true)
        try
            if ws.State = WebSocketState.Open then
                // 异步关闭不阻塞调用线程（令牌吊销路径在 config reload 线程，避免同步阻塞/线程池饥饿）；
                // ContinueWith 观察异常，避免 unobserved task exception
                ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "token revoked", CancellationToken.None)
                    .ContinueWith((fun (t: System.Threading.Tasks.Task) -> t.Exception |> ignore), System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted)
                    |> ignore
                // 与 CloseWith 一致：Abort 解除 recvTask 阻塞，断线清理及时执行（吊销路径）
                ws.Abort()
        with _ -> ()

    /// 是否应接收某会话的权威增量（快照模式下跳过）。
    member _.ShouldReceiveAuthority(convId: Guid) : bool =
        lock cursorLock (fun () -> not snapshotMode && (observedList || observedConversations.Contains convId))

    /// 慢客户端处理（决策 32-34）：不因发送慢而断开。
    /// 发送队列积压时切换为只读追赶（catch-up）模式：暂停实时权威增量推送，
    /// 改为按批发送已提交事件（每批有限），客户端确认游标后再发下一批。
    /// 注意：本方法可能在 coordinator mailbox 线程内被调用（PushAuthority <- BroadcastCommit <- onCommitted），
    /// 因此 catch-up 必须调度到独立 Task，绝不能在此线程同步执行（否则 getCommitsAfter 的 PostAndReply 会自锁）。
    member private this.EnterSnapshotMode() =
        let shouldStart =
            lock cursorLock (fun () ->
                if snapshotMode then false
                else
                    snapshotMode <- true
                    true)
        if shouldStart then
            logInfo(sprintf "connection %s entering catch-up mode (slow client)" remoteAddress)
            Task.Run(fun () ->
                if not closed then
                    // 以已确认游标与等待确认游标的较大者为起点，避免重发已发送批次
                    let start = lock cursorLock (fun () -> max appliedCursor (match awaitingCursor with Some c -> c | None -> appliedCursor))
                    this.SendCatchUp start)
            |> ignore

    /// 客户端确认应用游标后继续 catch-up；权威提交从内存 replay 列表读取，
    /// 但只发送该连接观察范围内的事件（过滤未观察会话，决策 133：多会话观察共用游标）。
    /// 每批最多 64 条；客户端应用后发送 cursor.advanced 驱动下一批（决策 34）。
    /// 本方法只允许在非 coordinator mailbox 线程调用（Task.Run / recvTask / 连接线程）。
    /// catchUpRunning 守卫在整个会话（含延迟重试）内持有，函数末尾单一释放，避免并发重跑与状态收尾交叠。
    member private this.SendCatchUp(startCursor: CommitId) : unit =
        if Interlocked.CompareExchange(&catchUpRunning, 1, 0) <> 0 then
            () // 已有 catch-up 进行中：跳过，避免重复批次
        else
            let mutable doneCatchUp = false
            let mutable cursor = startCursor
            while not doneCatchUp do
                if closed then
                    doneCatchUp <- true
                else
                    try
                        let mutable continueCatchUp = true
                        while continueCatchUp && not closed do
                            let commits = getCommitsAfter cursor
                            let batch = commits |> List.truncate 64
                            if List.isEmpty batch then
                                lock cursorLock (fun () ->
                                    snapshotMode <- false
                                    awaitingCursor <- None)
                                continueCatchUp <- false
                                doneCatchUp <- true
                            else
                                // 过滤：只保留观察范围内 commit 的对应事件（列表观察者接收全部列表相关事件）
                                // 观察集合可能被 recvTask 的 HandleEvent 并发修改（Observe/Unobserve），
                                // 此处 Task.Run 线程读，需经 cursorLock 取快照（可见性一致）
                                let observedSet, listObserved =
                                    lock cursorLock (fun () -> observedConversations, observedList)
                                let items = JsonArray()
                                let mutable lastItemId = 0UL
                                for commit in batch do
                                    let relevant =
                                        commit.events
                                        |> List.exists (fun ev ->
                                            match ev with
                                            | AgentMessageRecorded d -> observedSet.Contains d.conversationId
                                            | MessageDeleted d -> observedSet.Contains d.conversationId
                                            | ConversationCreated d -> listObserved || observedSet.Contains d.conversationId
                                            | ConversationForked d -> listObserved || observedSet.Contains d.conversationId
                                            | ConversationRenamed d -> listObserved || observedSet.Contains d.conversationId
                                            | EventData.ConversationDeleted d -> listObserved || observedSet.Contains d.conversationId
                                            | ConversationConfigUpdated d -> listObserved || observedSet.Contains d.conversationId)
                                    if relevant then
                                        items.Add(CommitCodec.commitToJsonLine commit)
                                        lastItemId <- commit.id
                                if items.Count > 0 then
                                    let catchUp: WireEvent = AuthorityCatchUp {| fromCursor = cursor; toCommitId = (batch |> List.last).id; items = items |}
                                    if this.TrySend catchUp then
                                        lock cursorLock (fun () ->
                                            // awaitingCursor = 最后一条实际发送的 item id（客户端确认的游标是它）
                                            awaitingCursor <- Some lastItemId)
                                        // 等待客户端 cursor.advanced 后再发下一批
                                        continueCatchUp <- false
                                        doneCatchUp <- true
                                    else
                                        // 发送队列仍满：不断开，稍后重试（决策 32：容忍慢客户端）
                                        // 重试在守卫内进行（doneCatchUp=false 且外层循环继续），保持单一释放
                                        Thread.Sleep 100
                                        continueCatchUp <- false
                                else
                                    // 本批无观察范围内事件：推进游标并继续下一批（循环而非递归，避免深栈）
                                    cursor <- (batch |> List.last).id
                                    lock cursorLock (fun () -> appliedCursor <- cursor)
                    with e ->
                        // 异常：若首批未发出（awaitingCursor 为空），短暂延迟后重试；
                        // 否则等待客户端 cursor.advanced 驱动（CursorAdvanced 分支会再次 SendCatchUp）
                        match lock cursorLock (fun () -> awaitingCursor) with
                        | None ->
                            Thread.Sleep 100
                            doneCatchUp <- false
                        | Some _ ->
                            doneCatchUp <- true
            Interlocked.Exchange(&catchUpRunning, 0) |> ignore

    /// 发送会话快照（当前投影）。快照携带全局 lastCommitId；
    /// 客户端完整应用后发送 cursor.advanced 推进游标（决策 33/135）。
    /// 长会话只携带尾部消息（P1-2/Q127），更早历史由客户端通过 history.request 分页。
    member this.SendConversationSnapshot(convId: Guid) : bool =
        let proj = getProjection ()
        match Projection.tryConversation proj convId with
        | None -> false
        | Some conv ->
            let state = orchestrator.RuntimeStateOf convId
            let messages, earliest, hasMore = ServerModel.conversationMessagesTail proj conv snapshotMessageLimit
            let sent =
                this.TrySend
                    (ConversationSnapshot
                        {| conversationId = convId
                           title = conv.title
                           lastCommitId = proj.latestCommitId
                           runtimeState = state
                           messages = messages
                           snapshotEarliestCommitId = earliest
                           snapshotHasMore = hasMore |})
            if sent then
                lock cursorLock (fun () -> awaitingCursor <- Some proj.latestCommitId)
            sent

    /// 单写者发送循环（决策 31）。
    member private this.SendLoop(ct: CancellationToken) : Task =
        task {
            try
                let reader = sendChannel.Reader
                while not closed do
                    let! ok = reader.WaitToReadAsync(ct)
                    if not ok then
                        closed <- true
                    else
                        let mutable more = true
                        while more && not closed do
                            let mutable ev: WireEvent = Unchecked.defaultof<WireEvent>
                            if reader.TryRead(&ev) then
                                let json = WireCodec.encode ev
                                let bytes = Encoding.UTF8.GetBytes json
                                try
                                    if ws.State = WebSocketState.Open then
                                        do! ws.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
                                with e ->
                                    closed <- true
                            else
                                more <- false
            with
            | :? OperationCanceledException -> ()
            | _ -> ()
        }

    /// 推送权威事件；队列满时切换为批式 catch-up（决策 32-34：不为慢客户端无限积压，也不断开）。
    member this.PushAuthority(ev: WireEvent) : unit =
        if not (this.TrySend ev) then
            this.EnterSnapshotMode()

    /// 推送临时事件（delta 等）；慢客户端直接丢弃（决策 34）。
    member this.PushTransient(ev: WireEvent) : unit =
        if not (lock cursorLock (fun () -> snapshotMode)) then
            this.TrySend ev |> ignore

    member private this.CloseWith(status: WebSocketCloseStatus, reason: string) : Task =
        task {
            logInfo(sprintf "conn %d closing via CloseWith: %s (%d)" connectionId reason (int status))
            lock cursorLock (fun () -> closed <- true)
            try
                if ws.State = WebSocketState.Open then
                    do! ws.CloseAsync(status, reason, CancellationToken.None)
                    // CloseAsync 后服务端本地 ReceiveAsync 可能仍阻塞；Abort 强制 recvTask 退出，
                    // 使 Task.WhenAll 返回、AbortByConnection 附件清理及时执行（认证超时/协议违规路径）
                    ws.Abort()
            with _ -> ()
        }

    member private this.SendAndClose(ev: WireEvent, reason: string) : Task =
        task {
            this.TrySend ev |> ignore
            do! Task.Delay 100
            do! this.CloseWith(WebSocketCloseStatus.PolicyViolation, reason)
        }

    member private this.TryAuthenticate(token: string) : ClientAuthRecord option =
        let hash = Auth.hashToken token
        let cfg = getConfig ()
        cfg.authClients
        |> List.tryFind (fun c -> (not c.revoked) && Auth.constantTimeEquals c.tokenHash hash)

    /// 原子写回配置（lastSeen 更新等，锁内读-改-写防并发覆盖）；失败仅记 stderr，不阻断认证（lastSeen 是诊断信息）。
    member private _.UpdateLastSeen(hash: string) : unit =
        let now = DateTimeOffset.UtcNow
        match updateConfig (fun cfg ->
            { cfg with
                authClients =
                    cfg.authClients
                    |> List.map (fun c -> if c.tokenHash = hash then { c with lastSeenUtc = Some now } else c) }) with
        | Ok () -> ()
        | Error e -> Stderr.write "last-seen-update-failed" [ "message", e ]

    member private this.HandleEvent(ev: WireEvent) : Task =
        task {
            match ev with
            | Hello d ->
                if d.protocol <> "wanxiang" || d.version <> Constants.ProtocolVersion then
                    do! this.SendAndClose(UpgradeRequired {| serverVersion = Constants.ProtocolVersion; clientVersion = d.version |}, "protocol version mismatch")
                else
                    match lock cursorLock (fun () -> handshakeState) with
                    | NotStarted -> lock cursorLock (fun () -> handshakeState <- HelloSeen)
                    | _ -> () // 重复 Hello 幂等
            | Ping -> this.TrySend Pong |> ignore
            | Pong -> ()
            | AuthPresent d ->
                match handshakeState with
                | NotStarted ->
                    // 握手先于认证（决策 55/69）：未发 Hello 直接认证 → 协议违规
                    do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "hello required before auth")
                | HelloSeen ->
                    match this.TryAuthenticate d.token with
                    | Some client ->
                        lock cursorLock (fun () ->
                            handshakeState <- Authenticated
                            tokenHash <- Some(Auth.hashToken d.token))
                        let cfg = getConfig ()
                        this.TrySend(AuthAccepted {| instanceId = cfg.instanceId.ToString("D") |}) |> ignore
                        // 决策 54：最近连接时间挂在该哈希记录下；认证成功后更新（节流：1 分钟内不重复写盘）
                        let now = DateTimeOffset.UtcNow
                        let stale =
                            match client.lastSeenUtc with
                            | Some t -> (now - t).TotalMinutes >= 1.0
                            | None -> true
                        if stale then
                            this.UpdateLastSeen client.tokenHash
                    | None ->
                        do! this.SendAndClose(AuthRejected {| reason = "invalid token" |}, "authentication failed")
                | Authenticated -> () // 已认证重复 auth.present：忽略
            | PairingRequested d ->
                match handshakeState with
                | NotStarted -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "hello required before pairing")
                | Authenticated -> () // 已认证：配对无意义，忽略
                | HelloSeen ->
                    if not pairingRequested then
                        let now = DateTimeOffset.UtcNow
                        if failureTracker.IsFrozen(now, remoteAddress) then
                            let cfg = getConfig ()
                            this.TrySend(PairingFailed {| reason = "too many failed attempts; frozen"; frozen = true; freezeMinutes = cfg.pairingFreezeMinutes |}) |> ignore
                        else
                            pairingRequested <- true
                            let code = pairing.Start(now, TimeSpan.FromMinutes 5.0)
                            logPairingCode code
                            this.TrySend(PairingStarted {| expiresInSeconds = 300 |}) |> ignore
            | PairingAttempted d ->
                match handshakeState with
                | NotStarted -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "hello required before pairing")
                | Authenticated -> ()
                | HelloSeen ->
                    if pairingRequested then
                        let now = DateTimeOffset.UtcNow
                        if failureTracker.IsFrozen(now, remoteAddress) then
                            let cfg = getConfig ()
                            this.TrySend(PairingFailed {| reason = "frozen"; frozen = true; freezeMinutes = cfg.pairingFreezeMinutes |}) |> ignore
                        else
                            match pairing.TryConsume(now, d.code) with
                            | Ok () ->
                                // 决策 56：先持久化令牌哈希到 TOML 并 reload 成功，再下发令牌原文
                                let token = Auth.generateToken ()
                                let hash = Auth.hashToken token
                                let name = d.clientName |> Option.defaultValue (sprintf "client-%s" remoteAddress)
                                // 原子读-改-写（决策 46/59）：并发配对/吊销不互相覆盖
                                match updateConfig (fun cfg ->
                                    { cfg with
                                        authClients =
                                            { tokenHash = hash
                                              name = name
                                              createdAtUtc = now
                                              lastSeenUtc = None
                                              revoked = false }
                                            :: cfg.authClients }) with
                                | Ok () ->
                                    failureTracker.Clear remoteAddress
                                    lock cursorLock (fun () ->
                                        handshakeState <- Authenticated
                                        tokenHash <- Some hash)
                                    this.TrySend(PairingSucceeded {| token = token |}) |> ignore
                                    let serverCfg = getConfig ()
                                    this.TrySend(AuthAccepted {| instanceId = serverCfg.instanceId.ToString("D") |}) |> ignore
                                | Error e ->
                                    this.TrySend(PairingFailed {| reason = e; frozen = false; freezeMinutes = 0 |}) |> ignore
                            | Error e ->
                                let frozen = failureTracker.RecordFailure(now, remoteAddress)
                                let cfg = getConfig ()
                                this.TrySend(PairingFailed {| reason = e; frozen = frozen; freezeMinutes = if frozen then cfg.pairingFreezeMinutes else 0 |}) |> ignore
            | ObserveConversationList ->
                match handshakeState with
                | Authenticated ->
                    lock cursorLock (fun () -> observedList <- true)
                    let proj = getProjection ()
                    if this.TrySend(ConversationListSnapshot {| items = ServerModel.conversationListItems proj orchestrator.RuntimeStateOf; lastCommitId = proj.latestCommitId |}) then
                        lock cursorLock (fun () -> awaitingCursor <- Some proj.latestCommitId)
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | UnobserveConversationList ->
                lock cursorLock (fun () -> observedList <- false)
            | ObserveConversation d ->
                match handshakeState with
                | Authenticated ->
                    lock cursorLock (fun () -> observedConversations <- observedConversations.Add d.conversationId)
                    // 决策 29：首次 observe 推送完整 snapshot；慢客户端（队列满）下快照发送失败时
                    // 降级到 catch-up 分批重同步（决策 32-34），绝不静默丢弃快照
                    if not (this.SendConversationSnapshot d.conversationId) then
                        this.EnterSnapshotMode()
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | UnobserveConversation d ->
                lock cursorLock (fun () -> observedConversations <- observedConversations.Remove d.conversationId)
            | CursorAdvanced d ->
                // 决策 33/35：游标表示客户端已成功应用的最后全局提交 id。
                // 上限保护：客户端声明的 id 超过服务端最新提交时按最新提交处理，
                // 防止 uint64.MaxValue 等超界值把 appliedCursor 抬到虚构位置、令 catch-up 起点错乱。
                // （陈旧检测本身仍信任客户端确认值——决策 149：服务端只信任已明确确认的游标。）
                match handshakeState with
                | Authenticated ->
                    let latest = (getProjection ()).latestCommitId
                    let claimed = min d.id latest
                    let (advanced, expected) =
                        lock cursorLock (fun () ->
                            let prev = appliedCursor
                            if claimed > appliedCursor then
                                appliedCursor <- claimed
                            (claimed > prev, awaitingCursor))
                    if advanced then
                        match expected with
                        | Some exp when claimed >= exp ->
                            lock cursorLock (fun () -> awaitingCursor <- None)
                            this.SendCatchUp (lock cursorLock (fun () -> appliedCursor))
                        | _ -> ()
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | Command cmd ->
                match handshakeState with
                | Authenticated ->
                    let invId = ClientCommand.invocationId cmd
                    let cursor = lock cursorLock (fun () -> appliedCursor)
                    match executeCommand cursor cmd with
                    | CommandQueued ->
                        this.TrySend(WireEvent.CommandAccepted {| invocationId = invId |}) |> ignore
                    | CommandCommitted commitId ->
                        this.TrySend(WireEvent.CommandAccepted {| invocationId = invId |}) |> ignore
                        let commandId = CommandId.compute invId (ClientCommand.commandType cmd) (ClientCommand.canonicalPayload cmd)
                        this.TrySend(WireEvent.CommandCommitted {| invocationId = invId; commandId = commandId; commitId = commitId |}) |> ignore
                    | CommandIdempotent commitId ->
                        let commandId = CommandId.compute invId (ClientCommand.commandType cmd) (ClientCommand.canonicalPayload cmd)
                        this.TrySend(WireEvent.CommandCommitted {| invocationId = invId; commandId = commandId; commitId = commitId |}) |> ignore
                    | CommandFailed err ->
                        // 决策 36：stale-projection 拒绝携带 requiredCommitId（客户端需追到的提交 id）
                        let requiredCommitId =
                            match err with
                            | StaleProjection wm -> Some wm
                            | _ -> None
                        this.TrySend(WireEvent.CommandRejected {| invocationId = invId; code = WanxiangError.code err; message = WanxiangError.message err; requiredCommitId = requiredCommitId |}) |> ignore
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | GenerationCancel d ->
                match handshakeState with
                | Authenticated -> orchestrator.CancelGeneration(d.conversationId, d.generationId) |> ignore
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | AttachmentBegin d ->
                match handshakeState with
                | Authenticated ->
                    match attachmentStore.Begin(connectionId, d.attachmentId, d.totalBytes, d.sha256, d.mediaType, d.fileName) with
                    | Ok () -> ()
                    | Error e ->
                        attachmentStore.Abort(d.attachmentId, WanxiangError.message e)
                        this.TrySend(AttachmentAborted {| attachmentId = d.attachmentId; reason = WanxiangError.message e |}) |> ignore
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | AttachmentChunk d ->
                match handshakeState with
                | Authenticated ->
                    match attachmentStore.AppendChunk(d.attachmentId, d.index, d.dataBase64) with
                    | Ok () -> ()
                    | Error e ->
                        attachmentStore.Abort(d.attachmentId, WanxiangError.message e)
                        this.TrySend(AttachmentAborted {| attachmentId = d.attachmentId; reason = WanxiangError.message e |}) |> ignore
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | AttachmentComplete d ->
                match handshakeState with
                | Authenticated ->
                    match attachmentStore.Complete(d.attachmentId, d.sha256) with
                    | Ok ref ->
                        this.TrySend(AttachmentCommitted {| attachmentId = d.attachmentId; sha256 = ref.sha256; size = ref.size |}) |> ignore
                    | Error e ->
                        this.TrySend(AttachmentAborted {| attachmentId = d.attachmentId; reason = WanxiangError.message e |}) |> ignore
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | AttachmentDownloadRequest d ->
                match handshakeState with
                | Authenticated ->
                    match attachmentStore.OpenRead d.sha256 with
                    | None ->
                        this.TrySend(ServerError {| message = sprintf "attachment %s not found" d.sha256 |}) |> ignore
                    | Some (stream, size) ->
                        // Q176/P2-7：下载回传声明/嗅探元数据（.meta 由 AttachmentStore 在 complete 时落盘）
                        let metaType, metaName, _ = attachmentStore.Metadata d.sha256 |> Option.defaultValue ("application/octet-stream", d.sha256, size)
                        let displayName = if String.IsNullOrWhiteSpace metaName then d.sha256 else metaName
                        this.TrySend(AttachmentDownloadBegin {| sha256 = d.sha256.ToLowerInvariant(); size = size; mediaType = metaType; fileName = displayName |}) |> ignore
                        task {
                            use stream = stream
                            let mutable index = 0
                            // Q172：下载 chunk 跟随配置（与上传一致，默认 256 KiB）
                            let chunkBytes = max 1024 ((getConfig ()).chunkSizeBytes)
                            let buf = Array.zeroCreate<byte> chunkBytes
                            let mutable more = true
                            let mutable failed = false
                            while more && not failed do
                                let! n = stream.ReadAsync(buf, 0, buf.Length)
                                if n = 0 then
                                    if this.TrySend(AttachmentDownloadComplete {| sha256 = d.sha256 |}) then
                                        more <- false
                                    else
                                        failed <- true
                                else
                                    let b64 = Convert.ToBase64String(buf, 0, n)
                                    if this.TrySend(AttachmentDownloadChunk {| sha256 = d.sha256; index = index; dataBase64 = b64 |}) then
                                        index <- index + 1
                                    else
                                        failed <- true
                            if failed then
                                this.TrySend(ServerError {| message = "attachment download interrupted" |}) |> ignore
                        } |> ignore
            | HistoryRequest d ->
                // Q127/P1-2：按全局 commitID 反向分页；页边界用稳定 commitID（不用 offset）。
                match handshakeState with
                | Authenticated ->
                    let proj = getProjection ()
                    match Projection.tryConversation proj d.conversationId with
                    | None -> ()
                    | Some conv ->
                        let items, hasMore = ServerModel.historyPageItems proj conv d.beforeCommitId d.limit
                        this.TrySend(HistoryPage {| conversationId = d.conversationId; beforeCommitId = d.beforeCommitId; items = items; hasMore = hasMore |}) |> ignore
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | _ -> ()
        }

    /// 连接主循环：先发 Hello，然后并行收发。
    /// 认证超时（决策 55）：建立后 15 秒内未完成认证则关闭，避免悬挂连接占用资源。
    member this.Run(ct: CancellationToken) : Task =
        task {
            let cfg = getConfig ()
            this.TrySend(Hello {| protocol = "wanxiang"; version = Constants.ProtocolVersion; instanceId = Some(cfg.instanceId.ToString("D")) |}) |> ignore
            let sendTask = this.SendLoop(ct)
            let recvTask =
                task {
                    let buffer = Array.zeroCreate<byte> (1024 * 1024)
                    let ms = new System.IO.MemoryStream()
                    try
                        while ws.State = WebSocketState.Open && not closed do
                            let! result = ws.ReceiveAsync(ArraySegment<byte>(buffer), ct)
                            if result.MessageType = WebSocketMessageType.Close then
                                closed <- true
                            elif result.MessageType = WebSocketMessageType.Binary then
                                do! this.CloseWith(WebSocketCloseStatus.InvalidMessageType, "binary frames not supported")
                            else
                                ms.Write(buffer, 0, result.Count)
                                if result.EndOfMessage then
                                    let jsonText = Encoding.UTF8.GetString(ms.ToArray())
                                    ms.SetLength 0L
                                    do! this.HandleJson jsonText
                    with
                    | :? OperationCanceledException -> ()
                    | e -> Stderr.write "ws-recv-exception" [ "connectionId", connectionId; "message", e.Message; "exception", e ]
                    closed <- true
                    sendChannel.Writer.TryComplete() |> ignore
                }
            // 认证超时：Hello 发出后 15 秒内必须认证（auth.present 或配对成功），否则关闭连接
            let authTimeout = task {
                let deadline = DateTimeOffset.UtcNow.AddSeconds 15.0
                let isAuthed () = lock cursorLock (fun () -> handshakeState = Authenticated)
                let isClosed () = lock cursorLock (fun () -> closed)
                while not (isClosed ()) && not (isAuthed ()) && DateTimeOffset.UtcNow < deadline do
                    do! Task.Delay 500
                if not (isClosed ()) && not (isAuthed ()) then
                    do! this.CloseWith(WebSocketCloseStatus.PolicyViolation, "authentication timeout")
            }
            do! Task.WhenAll(sendTask, recvTask, authTimeout)
            closed <- true
            sendChannel.Writer.TryComplete() |> ignore
            // 断线清理：取消本连接发起的未完成附件上传（决策 71）
            attachmentStore.AbortByConnection connectionId
            try
                if ws.State = WebSocketState.Open then
                    do! ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
            with _ -> ()
        }

    member private this.HandleJson(jsonText: string) : Task =
        task {
            // 命令事件走专用解码路径（决策 25：fire-and-forget，无传输 request/response）
            match WireCodec.tryDecodeCommand jsonText with
            | Ok cmd ->
                do! this.HandleEvent(WireEvent.Command cmd)
            | Error _ ->
                match WireCodec.tryDecode jsonText with
                | Error msg ->
                    Stderr.write "protocol-decode-error" [ "address", remoteAddress; "message", msg ]
                    do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "invalid event")
                | Ok ev -> do! this.HandleEvent ev
        }

/// 连接注册表：管理全部连接并广播权威增量。
/// 注意：BroadcastCommit 在单写者（mailbox）线程内被调用，禁止在此路径同步读取投影（会自锁）。
type ConnectionRegistry() =

    let connections = ConcurrentDictionary<int, WsConnection>()
    let mutable nextId = 0

    /// 预分配连接 id（构造 WsConnection 前调用；断线清理附件上传用）。
    member _.AddPlaceholder() : int =
        Interlocked.Increment(&nextId)

    /// 将已构造的连接对象放入注册表（与 AddPlaceholder 的 id 配对）。
    member _.Set(id: int, conn: WsConnection) : unit =
        connections[id] <- conn

    member _.Remove(id: int) : unit =
        connections.TryRemove id |> ignore

    member _.All() : WsConnection list =
        connections.Values |> List.ofSeq

    /// 广播权威提交（coordinator.onCommitted 注入）。
    member this.BroadcastCommit(commit: Events.Commit) : unit =
        // 收集受影响会话与变更种类
        let affected =
            [ for ev in commit.events do
                  match ev with
                  | AgentMessageRecorded d -> yield d.conversationId, "message"
                  | MessageDeleted d -> yield d.conversationId, "message"
                  | ConversationCreated d -> yield d.conversationId, "list"
                  | ConversationForked d -> yield d.conversationId, "list"
                  | ConversationRenamed d -> yield d.conversationId, "list"
                  | EventData.ConversationDeleted d -> yield d.conversationId, "list"
                  | ConversationConfigUpdated d -> yield d.conversationId, "list" ]
        for (convId, kind) in affected do
            for conn in this.All() do
                match kind with
                | "message" ->
                    if conn.Observes convId && conn.ShouldReceiveAuthority convId then
                        for ev in commit.events do
                            match ev with
                            | AgentMessageRecorded d when d.conversationId = convId ->
                                conn.PushAuthority(MessageCommitted {| conversationId = convId; commitId = commit.id; payload = d.payloadJson |})
                            | MessageDeleted d when d.conversationId = convId ->
                                let change = JsonObject()
                                change["deletedMessage"] <- d.messageCommitId
                                conn.PushAuthority(ConversationUpdated {| conversationId = convId; commitId = commit.id; change = change |})
                            | _ -> ()
                | "list" ->
                    if conn.ShouldReceiveAuthority convId then
                        let change = JsonObject()
                        change["commitId"] <- commit.id
                        conn.PushAuthority(ConversationUpdated {| conversationId = convId; commitId = commit.id; change = change |})
                    // 列表观察者收到 conversation.updated 后重新 observe 列表获取最新摘要
                    // （避免在单写者线程内读取投影）
                | _ -> ()

    /// 广播临时事件（generation.delta 等）给观察某会话的连接。
    /// P2-6：generation 状态变化同步推送轻量列表更新（会话摘要含 runtimeState，Q125）。
    member this.BroadcastTransient(convId: Guid, ev: WireEvent) : unit =
        for conn in this.All() do
            if conn.Observes convId then
                conn.PushTransient ev
            match ev with
            | GenerationStarted _ | GenerationFinished _ ->
                // 列表观察者：推送轻量 conversation.updated，客户端重新 observe 列表摘要
                if conn.ObservesList && conn.ShouldReceiveAuthority convId then
                    let change = JsonObject()
                    change["runtimeState"] <-
                        match ev with
                        | GenerationStarted _ -> "generating"
                        | _ -> "idle"
                    conn.PushAuthority(ConversationUpdated {| conversationId = convId; commitId = 0UL; change = change |})
            | _ -> ()
