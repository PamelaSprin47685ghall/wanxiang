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

/// 连接状态与处理循环。
/// 遵循：
/// - fire-and-forget 对称事件协议（决策 25）；
/// - 单写者 FIFO 发送队列（决策 31）；
/// - 慢客户端切换为快照重同步，不无限积压（决策 32-34）；
/// - 写权限由命令涉及投影水位判断（决策 35/38）；
/// - 观察关系绑定连接，断线自动释放（决策 28）。
type WsConnection(
    ws: WebSocket,
    remoteAddress: string,
    getProjection: unit -> Projection,
    getConfig: unit -> AppConfig,
    rewriteConfig: AppConfig -> Result<unit, string>,
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
    let mutable authenticated = false
    let mutable tokenHash: string option = None
    let mutable observedList = false
    let mutable observedConversations: Set<Guid> = Set.empty
    let mutable appliedCursor: CommitId = 0UL
    let mutable advertisedCursor: CommitId = 0UL
    let mutable awaitingCursor: CommitId option = None
    let mutable snapshotMode = false
    let mutable closed = false
    let mutable pairingRequested = false
    /// catch-up 进行中标记（Interlocked，防多入口并发发送重复批次）。
    let mutable catchUpRunning = 0

    member _.TrySend(ev: WireEvent) : bool =
        sendChannel.Writer.TryWrite ev

    member _.IsAuthenticated = authenticated
    member _.TokenHash = tokenHash
    member _.Observes(convId: Guid) : bool = observedConversations.Contains convId
    member _.ObservesList = observedList
    member _.AppliedCursor = appliedCursor
    member _.IsInSnapshotMode = snapshotMode

    /// 强制关闭（令牌吊销时调用，决策 58）。
    member _.ForceClose() : unit =
        closed <- true
        try
            if ws.State = WebSocketState.Open then
                ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "token revoked", CancellationToken.None).GetAwaiter().GetResult()
        with _ -> ()

    /// 是否应接收某会话的权威增量（快照模式下跳过）。
    member _.ShouldReceiveAuthority(convId: Guid) : bool =
        not snapshotMode && (observedList || observedConversations.Contains convId)

    /// 慢客户端处理（决策 32-34）：不因发送慢而断开。
    /// 发送队列积压时切换为只读追赶（catch-up）模式：暂停实时权威增量推送，
    /// 改为按批发送已提交事件（每批有限），客户端确认游标后再发下一批。
    /// 注意：本方法可能在 coordinator mailbox 线程内被调用（PushAuthority <- BroadcastCommit <- onCommitted），
    /// 因此 catch-up 必须调度到独立 Task，绝不能在此线程同步执行（否则 getCommitsAfter 的 PostAndReply 会自锁）。
    member private this.EnterSnapshotMode() =
        if not snapshotMode then
            snapshotMode <- true
            logInfo(sprintf "connection %s entering catch-up mode (slow client)" remoteAddress)
            Task.Run(fun () ->
                if not closed then
                    // 以已确认游标与等待确认游标的较大者为起点，避免重发已发送批次
                    let start = max appliedCursor (match awaitingCursor with Some c -> c | None -> appliedCursor)
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
                                snapshotMode <- false
                                awaitingCursor <- None
                                continueCatchUp <- false
                                doneCatchUp <- true
                            else
                                // 过滤：只保留观察范围内 commit 的对应事件（列表观察者接收全部列表相关事件）
                                let items = JsonArray()
                                let mutable lastItemId = 0UL
                                for commit in batch do
                                    let relevant =
                                        commit.events
                                        |> List.exists (fun ev ->
                                            match ev with
                                            | AgentMessageRecorded d -> observedConversations.Contains d.conversationId
                                            | MessageDeleted d -> observedConversations.Contains d.conversationId
                                            | ConversationCreated d -> observedList || observedConversations.Contains d.conversationId
                                            | ConversationForked d -> observedList || observedConversations.Contains d.conversationId
                                            | ConversationRenamed d -> observedList || observedConversations.Contains d.conversationId
                                            | EventData.ConversationDeleted d -> observedList || observedConversations.Contains d.conversationId
                                            | ConversationConfigUpdated d -> observedList || observedConversations.Contains d.conversationId)
                                    if relevant then
                                        items.Add(CommitCodec.commitToJsonLine commit)
                                        lastItemId <- commit.id
                                if items.Count > 0 then
                                    let catchUp: WireEvent = AuthorityCatchUp {| fromCursor = cursor; toCommitId = (batch |> List.last).id; items = items |}
                                    if this.TrySend catchUp then
                                        // awaitingCursor = 最后一条实际发送的 item id（客户端确认的游标是它）
                                        advertisedCursor <- lastItemId
                                        awaitingCursor <- Some advertisedCursor
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
                                    appliedCursor <- cursor
                    with _ ->
                        // 异常：若首批未发出（awaitingCursor 为空），短暂延迟后重试；
                        // 否则等待客户端 cursor.advanced 驱动（CursorAdvanced 分支会再次 SendCatchUp）
                        match awaitingCursor with
                        | None ->
                            Thread.Sleep 100
                            doneCatchUp <- false
                        | Some _ ->
                            doneCatchUp <- true
            Interlocked.Exchange(&catchUpRunning, 0) |> ignore

    /// 发送会话快照（当前投影）。快照携带全局 lastCommitId；
    /// 客户端完整应用后发送 cursor.advanced 推进游标（决策 33/135）。
    member this.SendConversationSnapshot(convId: Guid) : bool =
        let proj = getProjection ()
        match Projection.tryConversation proj convId with
        | None -> false
        | Some conv ->
            let state = orchestrator.RuntimeStateOf convId
            let sent =
                this.TrySend
                    (ConversationSnapshot
                        {| conversationId = convId
                           title = conv.title
                           lastCommitId = proj.latestCommitId
                           runtimeState = state
                           messages = ServerModel.conversationMessages proj conv |})
            if sent then
                awaitingCursor <- Some proj.latestCommitId
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
                                with _ ->
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
        if not snapshotMode then
            this.TrySend ev |> ignore

    member private this.CloseWith(status: WebSocketCloseStatus, reason: string) : Task =
        task {
            closed <- true
            try
                if ws.State = WebSocketState.Open then
                    do! ws.CloseAsync(status, reason, CancellationToken.None)
            with _ -> ()
        }

    member private this.SendAndClose(ev: WireEvent, reason: string) : Task =
        task {
            this.TrySend ev |> ignore
            do! Task.Delay 100
            do! this.CloseWith(WebSocketCloseStatus.PolicyViolation, reason)
        }

    member private this.TryAuthenticate(token: string) : bool =
        let hash = Auth.hashToken token
        let cfg = getConfig ()
        cfg.authClients
        |> List.exists (fun c -> c.tokenHash = hash && not c.revoked)

    member private this.HandleEvent(ev: WireEvent) : Task =
        task {
            match ev with
            | Hello d ->
                if d.version <> Constants.ProtocolVersion then
                    do! this.SendAndClose(UpgradeRequired {| serverVersion = Constants.ProtocolVersion; clientVersion = d.version |}, "protocol version mismatch")
            | Ping -> this.TrySend Pong |> ignore
            | Pong -> ()
            | AuthPresent d ->
                if not authenticated && this.TryAuthenticate d.token then
                    authenticated <- true
                    tokenHash <- Some(Auth.hashToken d.token)
                    let cfg = getConfig ()
                    this.TrySend(AuthAccepted {| instanceId = cfg.instanceId.ToString("D") |}) |> ignore
                elif not authenticated then
                    do! this.SendAndClose(AuthRejected {| reason = "invalid token" |}, "authentication failed")
            | PairingRequested d ->
                if not authenticated && not pairingRequested then
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
                if not authenticated && pairingRequested then
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
                            let cfg = getConfig ()
                            let newCfg =
                                { cfg with
                                    authClients =
                                        { tokenHash = hash
                                          name = d.clientName |> Option.defaultValue (sprintf "client-%s" remoteAddress)
                                          createdAtUtc = now
                                          lastSeenUtc = None
                                          revoked = false }
                                        :: cfg.authClients }
                            match rewriteConfig newCfg with
                            | Ok () ->
                                failureTracker.Clear remoteAddress
                                authenticated <- true
                                tokenHash <- Some hash
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
                if authenticated then
                    observedList <- true
                    let proj = getProjection ()
                    if this.TrySend(ConversationListSnapshot {| items = ServerModel.conversationListItems proj orchestrator.RuntimeStateOf; lastCommitId = proj.latestCommitId |}) then
                        awaitingCursor <- Some proj.latestCommitId
            | UnobserveConversationList ->
                observedList <- false
            | ObserveConversation d ->
                if authenticated then
                    observedConversations <- observedConversations.Add d.conversationId
                    this.SendConversationSnapshot d.conversationId |> ignore
            | UnobserveConversation d ->
                observedConversations <- observedConversations.Remove d.conversationId
            | CursorAdvanced d ->
                // 决策 33/35：游标表示客户端已成功应用的最后全局提交 id；
                // 快照应用完成或 catch-up 批次应用完成后客户端发送 cursor.advanced。
                // 幂等：只向前推进；允许客户端确认的 id 大于我们等待的（快照/批之间可能有新提交）。
                if d.id > appliedCursor then
                    appliedCursor <- d.id
                match awaitingCursor with
                | Some expected when d.id >= expected ->
                    awaitingCursor <- None
                    this.SendCatchUp appliedCursor
                | _ -> ()
            | Command cmd ->
                if authenticated then
                    let invId = ClientCommand.invocationId cmd
                    match executeCommand appliedCursor cmd with
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
                        this.TrySend(WireEvent.CommandRejected {| invocationId = invId; code = WanxiangError.code err; message = WanxiangError.message err |}) |> ignore
            | GenerationCancel d ->
                if authenticated then
                    orchestrator.CancelGeneration(d.conversationId, d.generationId) |> ignore
            | AttachmentBegin d ->
                if authenticated then
                    match attachmentStore.Begin(d.attachmentId, d.totalBytes, d.sha256, d.mediaType, d.fileName) with
                    | Ok () -> ()
                    | Error e ->
                        attachmentStore.Abort(d.attachmentId, WanxiangError.message e)
                        this.TrySend(AttachmentAborted {| attachmentId = d.attachmentId; reason = WanxiangError.message e |}) |> ignore
            | AttachmentChunk d ->
                if authenticated then
                    match attachmentStore.AppendChunk(d.attachmentId, d.dataBase64) with
                    | Ok () -> ()
                    | Error e ->
                        attachmentStore.Abort(d.attachmentId, WanxiangError.message e)
                        this.TrySend(AttachmentAborted {| attachmentId = d.attachmentId; reason = WanxiangError.message e |}) |> ignore
            | AttachmentComplete d ->
                if authenticated then
                    match attachmentStore.Complete(d.attachmentId, d.sha256) with
                    | Ok ref ->
                        this.TrySend(AttachmentCommitted {| attachmentId = d.attachmentId; sha256 = ref.sha256; size = ref.size |}) |> ignore
                    | Error e ->
                        this.TrySend(AttachmentAborted {| attachmentId = d.attachmentId; reason = WanxiangError.message e |}) |> ignore
            | AttachmentDownloadRequest d ->
                if authenticated then
                    match attachmentStore.OpenRead d.sha256 with
                    | None ->
                        this.TrySend(ServerError {| message = sprintf "attachment %s not found" d.sha256 |}) |> ignore
                    | Some (stream, size) ->
                        this.TrySend(AttachmentDownloadBegin {| sha256 = d.sha256.ToLowerInvariant(); size = size; mediaType = "application/octet-stream"; fileName = d.sha256 |}) |> ignore
                        task {
                            use stream = stream
                            let mutable index = 0
                            let buf = Array.zeroCreate<byte> (256 * 1024)
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
            | _ -> ()
        }

    /// 连接主循环：先发 Hello，然后并行收发。
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
                    | _ -> ()
                    closed <- true
                    sendChannel.Writer.TryComplete() |> ignore
                }
            do! Task.WhenAll(sendTask, recvTask)
            closed <- true
            sendChannel.Writer.TryComplete() |> ignore
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

    member _.Add(conn: WsConnection) : int =
        let id = Interlocked.Increment(&nextId)
        connections[id] <- conn
        id

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
    member this.BroadcastTransient(convId: Guid, ev: WireEvent) : unit =
        for conn in this.All() do
            if conn.Observes convId then
                conn.PushTransient ev
