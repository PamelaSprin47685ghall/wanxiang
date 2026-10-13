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
type internal HostFeaturesMarker = class end

type CommandExecutionResult =
    /// SendUserMessage：已入队（等待插入点提交）
    | CommandQueued
    /// 已提交
    | CommandCommitted of commitId: CommitId
    /// 幂等命中（原提交）
    | CommandIdempotent of commitId: CommitId
    | CommandFailed of WanxiangError

/// 握手状态机（决策 55/69/70 现行决议 + ADR-001 方案 B）：版本协商完全 in-band，
/// 无 Hello 握手事件。认证前只接受认证/配对事件；收到业务事件 → 视为协议违规关闭。
/// `Hello`/`UpgradeRequired` 事件仍被解码（兼容旧客户端），但不改变状态。
type HandshakeState =
    | NotStarted
    | Authenticated

/// 设备管理动作（重命名 / 吊销）。
type DeviceChange =
    | Rename of string
    | Revoke

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
    /// 当前可用工具的目录（内置 + MCP 发现）
    toolDescriptors: unit -> JsonArray,
    getCommitsAfter: CommitId -> int -> Events.Commit list,
    desk: PairingDesk.PairingDesk,
    logPairingCode: string -> unit,
    /// 当前已认证连接的令牌哈希（设备列表的“在线”状态；运行时状态，不落盘）。
    onlineTokenHashes: unit -> string list,
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
    /// 客户端声明过自己能处理工具审批（`tool.approval-capability`）。旧客户端永远是 false。
    let mutable approvalCapable = false
    /// catch-up 进行中标记（Interlocked，防多入口并发发送重复批次）。
    let mutable catchUpRunning = 0
    /// 全文搜索进行中的数量（W4，预算 SearchLimits.maxInFlightPerConnection）。
    let mutable searchesInFlight = 0

    /// 快照携带的最大消息数（Q127/P1-2：长对话只带尾部，更早历史走 history.request 分页）。
    let snapshotMessageLimit = 200
    /// catch-up 连续异常重试上限（R5：无界循环必须有界；coordinator 停止等持续故障时
    /// 耗尽后退出 catch-up 模式恢复实时推送，避免无限重试与悬挂连接）。
    let maxCatchUpRetries = 10

    /// 主机状态（配置拒绝、检查主机）由 ServerApp 在构造后注入；缺省时这些只读能力静默不可用。
    member val HostStatus: HostStatus option = None with get, set

    /// 认证完成后补发主机当前的可见状态：断线期间发生的配置拒绝，重连后也要看得到。
    /// 决策 440：认证后紧随 AuthAccepted 声明主机版本与功能表。
    member private this.AnnounceCapabilities() : unit =
        let version =
            let asm = typeof<HostFeaturesMarker>.Assembly
            match asm.GetName().Version with
            | null -> ""
            | v -> v.ToString()
        this.TrySend(HostCapabilities {| hostVersion = version; features = HostFeatures.all |}) |> ignore

    member private this.AnnounceHostStatus() : unit =
        match this.HostStatus |> Option.bind _.Current with
        | Some ev -> this.TrySend ev |> ignore
        | None -> ()

    member _.TrySend(ev: WireEvent) : bool =
        let ok = sendChannel.Writer.TryWrite ev
        ok

    member _.IsAuthenticated =
        match lock cursorLock (fun () -> handshakeState) with
        | Authenticated -> true
        | _ -> false
    member _.TokenHash = lock cursorLock (fun () -> tokenHash)
    member _.Observes(convId: Guid) : bool = lock cursorLock (fun () -> observedConversations.Contains convId)
    member _.ApprovalCapable = lock cursorLock (fun () -> approvalCapable)
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

    /// 是否应接收某对话的权威增量（快照模式下跳过）。
    member _.ShouldReceiveAuthority(convId: Guid) : bool =
        lock cursorLock (fun () -> not snapshotMode && (observedList || observedConversations.Contains convId))

    /// 慢客户端处理（决策 32-34）：不因发送慢而断开。
    /// 发送队列积压时切换为只读追赶（catch-up）模式：暂停实时权威增量推送，
    /// 改为按批发送已提交事件（每批有限），客户端确认游标后再发下一批。
    /// 注意：本方法可能在 coordinator mailbox 线程内被调用（PushAuthority <- BroadcastCommit <- onCommitted），
    /// 因此 catch-up 必须调度到独立 Task，绝不能在此线程同步执行（getCommitsAfter 走 LedgerStore.CommitsAfter，持存储锁读盘）。
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
    /// 但只发送该连接观察范围内的事件（过滤未观察对话，决策 133：多对话观察共用游标）。
    /// 每批最多 64 条；客户端应用后发送 cursor.advanced 驱动下一批（决策 34）。
    /// 本方法只允许在非 coordinator mailbox 线程调用（Task.Run / recvTask / 连接线程）。
    /// catchUpRunning 守卫在整个对话（含延迟重试）内持有，函数末尾单一释放，避免并发重跑与状态收尾交叠。
    member private this.SendCatchUp(startCursor: CommitId) : unit =
        if Interlocked.CompareExchange(&catchUpRunning, 1, 0) <> 0 then
            () // 已有 catch-up 进行中：跳过，避免重复批次
        else
            // 连续异常重试上限：getCommitsAfter 的 PostAndReply 在 coordinator 停止时
            // 会抛异常；无限重试是 R5 违规（无界循环）。耗尽后退出 catch-up 模式
            // 恢复实时推送（宁可丢增量也不让连接悬挂），下次队列满会重新进入。
            let mutable retries = 0
            let mutable doneCatchUp = false
            let mutable cursor = startCursor
            while not doneCatchUp do
                if closed then
                    doneCatchUp <- true
                else
                    try
                        let mutable continueCatchUp = true
                        while continueCatchUp && not closed do
                            // 分页取批：只取最多 64 条（§13/§22 单批上限），
                            // 避免慢客户端追赶长历史时每批 O(n) 复制剩余全部（O(n²)）。
                            let batch = getCommitsAfter cursor 64
                            if List.isEmpty batch then
                                lock cursorLock (fun () ->
                                    snapshotMode <- false
                                    awaitingCursor <- None)
                                // 竞态窗口：空批判定后、snapshotMode=false 生效前，新提交 X
                                // 广播会被 ShouldReceiveAuthority（snapshotMode 仍 true）跳过，
                                // 若直接退出则 X 永久丢失（客户端游标停在旧位置，后续提交正常推送
                                // 但 X 缺失 → 消息空洞）。
                                // 修复：用最新水位复核——游标落后则重新进入 catch-up 补发。
                                // 若 X 恰好在 snapshotMode=false 之后广播成功，这里 latest > cursor
                                // 会重复补发 X，客户端按 commitId 精确去重（alreadyApplied），安全。
                                let latest = (getProjection ()).latestCommitId
                                if latest > cursor then
                                    // 有新提交在退出窗口内到达：重新进入 catch-up 从 cursor 续取
                                    lock cursorLock (fun () -> snapshotMode <- true)
                                    continueCatchUp <- false
                                    // 不置 doneCatchUp：外层循环重新取批（cursor 未变，取到新提交）
                                else
                                    continueCatchUp <- false
                                    doneCatchUp <- true
                                    retries <- 0
                            else
                                // 过滤：只保留观察范围内 commit 的对应事件（列表观察者接收全部列表相关事件）
                                // 观察集合可能被 recvTask 的 HandleEvent 并发修改（Observe/Unobserve），
                                // 此处 Task.Run 线程读，需经 cursorLock 取快照（可见性一致）
                                let observedSet, listObserved =
                                    lock cursorLock (fun () -> observedConversations, observedList)
                                let items = JsonArray()
                                let mutable lastItemId = 0UL
                                // §22：单批字节预算（8 MiB）——64 条事件数上限之外再限总字节，
                                // 防单条 4 MiB（WebSocket 上限）× 64 条 = 256 MiB 的极端单批。
                                // 超预算即 break，剩余 commit 由下一批续取（游标推进逻辑不变）。
                                let mutable batchBytes = 0
                                let maxBatchBytes = 8 * 1024 * 1024
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
                                            | ConversationConfigUpdated d -> listObserved || observedSet.Contains d.conversationId
                                            | ConversationFlagsChanged d -> listObserved || observedSet.Contains d.conversationId)
                                    if relevant then
                                        let line = CommitCodec.commitToJsonLine commit
                                        let bytes = Text.Encoding.UTF8.GetByteCount line
                                        if batchBytes + bytes > maxBatchBytes && items.Count > 0 then
                                            // 本批已满：停止累积，剩余下批续取
                                            // 不更新 lastItemId——该 commit 未发送，游标停在已发送的最后一条
                                            ()
                                        else
                                            items.Add line
                                            batchBytes <- batchBytes + bytes
                                            lastItemId <- commit.id
                                if items.Count > 0 then
                                    // toCommitId = 实际发送的最后一条（字节预算 break 或过滤后
                                    // batch 尾部可能未发送——水位不得超前于客户端实际收到的内容）
                                    let catchUp: WireEvent = AuthorityCatchUp {| fromCursor = cursor; toCommitId = lastItemId; items = items |}
                                    if this.TrySend catchUp then
                                        lock cursorLock (fun () ->
                                            // awaitingCursor = 最后一条实际发送的 item id（客户端确认的游标是它）
                                            awaitingCursor <- Some lastItemId)
                                        retries <- 0
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
                                    retries <- 0
                    with e ->
                        // O-001/R6：catch-up 异常必须留下诊断证据（含 connectionId），
                        // 不得无证据继续运行。
                        Stderr.write
                            "ws-catchup-failed"
                            [ "connectionId", connectionId
                              "cursor", cursor
                              "message", e.Message
                              "exception", e ]
                        // 异常：若首批未发出（awaitingCursor 为空），短暂延迟后重试；
                        // 否则等待客户端 cursor.advanced 驱动（CursorAdvanced 分支会再次 SendCatchUp）
                        match lock cursorLock (fun () -> awaitingCursor) with
                        | None ->
                            retries <- retries + 1
                            if retries >= maxCatchUpRetries then
                                // 重试耗尽（coordinator 停止等持续故障）：退出 catch-up 模式，
                                // 恢复实时推送路径，避免无限重试（R5 无界循环）与悬挂连接。
                                lock cursorLock (fun () ->
                                    snapshotMode <- false
                                    awaitingCursor <- None)
                                doneCatchUp <- true
                            else
                                Thread.Sleep 100
                                doneCatchUp <- false
                        | Some _ ->
                            doneCatchUp <- true
            Interlocked.Exchange(&catchUpRunning, 0) |> ignore

    /// 发送对话快照（当前投影）。快照携带全局 lastCommitId；
    /// 客户端完整应用后发送 cursor.advanced 推进游标（决策 33/135）。
    /// 长对话只携带尾部消息（P1-2/Q127），更早历史由客户端通过 history.request 分页。
    member this.SendConversationSnapshot(convId: Guid) : bool =
        let proj = getProjection ()
        match Projection.tryConversation proj convId with
        | None -> false
        | Some conv ->
            let messages, earliest, hasMore = ServerModel.conversationMessagesTail proj conv snapshotMessageLimit
            let sent =
                orchestrator.WithRuntimeSnapshot(convId, fun state generationId ->
                    this.TrySend
                        (ConversationSnapshot
                            {| conversationId = convId
                               title = conv.title
                               lastCommitId = proj.latestCommitId
                               runtimeState = state
                               generationId = generationId
                               messages = messages
                               snapshotEarliestCommitId = earliest
                               snapshotHasMore = hasMore
                               config = conv.config |}))
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
                                // AG-UI 承载（语义 WireEvent → AG-UI 事件对象）
                                let json = WireAgui.encode ev
                                let bytes = Encoding.UTF8.GetBytes json
                                try
                                    if ws.State = WebSocketState.Open then
                                        do! ws.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
                                with e ->
                                    // O-001：发送失败必须留下诊断证据（R6：不允许无证据继续运行）。
                                    // 连接已不可用，标记关闭；异常与连接 id 写入 stderr，不输出 token/密钥。
                                    Stderr.write
                                        "ws-send-failed"
                                        [ "connectionId", connectionId
                                          "message", e.Message
                                          "exception", e ]
                                    closed <- true
                            else
                                more <- false
            with
            | :? OperationCanceledException -> ()
            | e ->
                // O-001/R6：发送循环外层异常（WaitToReadAsync/SendAsync 抛非取消异常，
                // 如 ObjectDisposedException、WebSocketException）必须留下诊断证据，
                // 且必须标记 closed——否则 recvTask 继续收、PushAuthority 继续入队，
                // 连接悬挂（发送死、接收活）。
                Stderr.write
                    "ws-send-failed"
                    [ "connectionId", connectionId
                      "message", e.Message
                      "exception", e ]
                lock cursorLock (fun () -> closed <- true)
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
                    // CloseAsync 后主机本地 ReceiveAsync 可能仍阻塞；Abort 强制 recvTask 退出，
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
        let cfg = getConfig ()
        cfg.authClients
        |> List.tryFind (fun c -> (not c.revoked) && ClientToken.ClientToken.matches token c.tokenHash)

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
            // 协议无独立握手事件：版本 in-band（RUN_STARTED.protocolVersion），
            // 认证即连接后的第一个动作。
            | Hello _ | UpgradeRequired _ -> ()
            | Ping -> this.TrySend Pong |> ignore
            | Pong -> ()
            | AuthPresent d ->
                // 连接即认证：未认证的任何前置状态一视同仁，直接认证。
                match handshakeState with
                | NotStarted ->
                    match this.TryAuthenticate d.token with
                    | Some client ->
                        lock cursorLock (fun () ->
                            handshakeState <- Authenticated
                            tokenHash <- Some(ClientToken.ClientToken.hash d.token))
                        let cfg = getConfig ()
                        this.TrySend(AuthAccepted {| instanceId = cfg.instanceId.ToString("D") |}) |> ignore
                        this.AnnounceCapabilities()
                        this.AnnounceHostStatus()
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
                | Authenticated -> () // 已认证：配对无意义，忽略
                | NotStarted ->
                    if not pairingRequested then
                        match desk.Request(DateTimeOffset.UtcNow, remoteAddress) with
                        | PairingDesk.Frozen _ ->
                            let cfg = getConfig ()
                            this.TrySend(PairingFailed {| reason = "too many failed attempts; frozen"; frozen = true; freezeMinutes = cfg.pairingFreezeMinutes |}) |> ignore
                        | PairingDesk.Started(seconds, newCode) ->
                            pairingRequested <- true
                            // 沿用桌面邀请码时 newCode = None：不另生成、不打 stderr。
                            newCode |> Option.iter logPairingCode
                            this.TrySend(PairingStarted {| expiresInSeconds = seconds |}) |> ignore
            | PairingAttempted d ->
                match handshakeState with
                | Authenticated -> ()
                | NotStarted ->
                    let now = DateTimeOffset.UtcNow
                    match desk.Attempt(now, remoteAddress, d.code, d.clientName, pairingRequested) with
                    | PairingDesk.Ignored -> ()
                    | PairingDesk.Refused(reason, frozenUntil) ->
                        let cfg = getConfig ()
                        let frozen = frozenUntil.IsSome
                        this.TrySend(PairingFailed {| reason = reason; frozen = frozen; freezeMinutes = if frozen then cfg.pairingFreezeMinutes else 0 |}) |> ignore
                    | PairingDesk.Accepted(token, hash, name) ->
                        // 决策 56：先持久化令牌哈希到 TOML 并 reload 成功，再 Paired、再下发令牌原文
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
                            desk.Paired remoteAddress
                            lock cursorLock (fun () ->
                                handshakeState <- Authenticated
                                tokenHash <- Some hash)
                            this.TrySend(PairingSucceeded {| token = token |}) |> ignore
                            let serverCfg = getConfig ()
                            this.TrySend(AuthAccepted {| instanceId = serverCfg.instanceId.ToString("D") |}) |> ignore
                            this.AnnounceCapabilities()
                            this.AnnounceHostStatus()
                        | Error e ->
                            this.TrySend(PairingFailed {| reason = e; frozen = false; freezeMinutes = 0 |}) |> ignore
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
                    // 决策 250：刷新 / 换设备后，半截回复与错误卡随快照一起回来
                    orchestrator.Partials.TryGet d.conversationId |> Option.iter (fun ev -> this.TrySend ev |> ignore)
                    // 断线重连后审批卡片仍在：补发这个对话里还在等待的审批请求
                    if this.ApprovalCapable then
                        for ev in orchestrator.Approvals.PendingFor d.conversationId do
                            this.TrySend ev |> ignore
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | UnobserveConversation d ->
                lock cursorLock (fun () -> observedConversations <- observedConversations.Remove d.conversationId)
            | CursorAdvanced d ->
                // 决策 33/35：游标表示客户端已成功应用的最后全局提交 id。
                // 上限保护：客户端声明的 id 超过主机最新提交时按最新提交处理，
                // 防止 uint64.MaxValue 等超界值把 appliedCursor 抬到虚构位置、令 catch-up 起点错乱。
                // （陈旧检测本身仍信任客户端确认值——决策 149：主机只信任已明确确认的游标。）
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
                | Authenticated ->
                    match orchestrator.CancelGeneration(d.conversationId, d.generationId) with
                    | Ok () -> ()
                    // 生成可能刚刚结束。重新给出权威状态，不能让停止按钮永久等待。
                    | Error _ -> this.SendConversationSnapshot d.conversationId |> ignore
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
                        } |> this.Detach
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
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
            | CatalogRequest ->
                match handshakeState with
                | Authenticated -> this.SendCatalog()
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | ConversationExportRead d ->
                match handshakeState with
                | Authenticated ->
                    let ev =
                        match ServerModel.exportPage (getProjection ()) d with
                        | Ok page -> ConversationExportPage page
                        | Error message -> ConversationExportFailed {| exportId = d.exportId; message = message |}
                    this.TrySend ev |> ignore
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | SearchQuery d ->
                match handshakeState with
                | Authenticated ->
                    // 只读查询：对不可变投影快照扫描，离开接收循环执行，不阻塞其它帧；
                    // 同一连接并发有上限，超出直接说明原因（不排队、不静默丢）。
                    if Interlocked.Increment(&searchesInFlight) > SearchLimits.maxInFlightPerConnection then
                        Interlocked.Decrement(&searchesInFlight) |> ignore
                        this.TrySend(SearchFailed {| queryId = d.queryId; message = "上一次搜索还没结束，请稍后再试。" |}) |> ignore
                    else
                        let proj = getProjection ()
                        Task.Run(fun () ->
                            try
                                let ev =
                                    try
                                        match SearchService.search proj d with
                                        | Ok page -> SearchResults page
                                        | Error message -> SearchFailed {| queryId = d.queryId; message = message |}
                                    with _ ->
                                        SearchFailed {| queryId = d.queryId; message = "搜索没有完成：主机处理时出错。请重试。" |}
                                this.TrySend ev |> ignore
                            finally
                                Interlocked.Decrement(&searchesInFlight) |> ignore)
                        |> this.Detach
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | ProviderProbeRequest d ->
                match handshakeState with
                | Authenticated ->
                    match AppConfig.tryProvider d.providerId (getConfig ()) with
                    | None ->
                        this.TrySend(
                            ProviderProbeResult
                                {| requestId = d.requestId
                                   providerId = d.providerId
                                   ok = false
                                   models = JsonArray()
                                   error = Some(sprintf "模型服务 %s 不存在" d.providerId) |})
                        |> ignore
                    | Some provider ->
                        // 探活可能等上十几秒，绝不阻塞接收循环
                        task {
                            let! result = ProviderCatalog.probeModels provider CancellationToken.None
                            match result with
                            | Ok models ->
                                this.TrySend(
                                    ProviderProbeResult
                                        {| requestId = d.requestId
                                           providerId = d.providerId
                                           ok = true
                                           models = JsonArray([| for m in models -> JsonNode.op_Implicit m |])
                                           error = None |})
                                |> ignore
                                // 探测顺带拿到了看图能力（决策 330），刷新目录让输入区立即用上。
                                this.SendCatalog()
                            | Error e ->
                                this.TrySend(
                                    ProviderProbeResult
                                        {| requestId = d.requestId
                                           providerId = d.providerId
                                           ok = false
                                           models = JsonArray()
                                           error = Some e |})
                                |> ignore
                        }
                        |> this.Detach
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | DoctorRequest d ->
                // 纯读：不改任何状态；未认证的连接按协议违规处理，与其他业务事件一致。
                match handshakeState with
                | Authenticated ->
                    match this.HostStatus with
                    | Some status ->
                        task {
                            let! report = status.RunDoctor(d.requestId, Stderr.redact)
                            this.TrySend report |> ignore
                        }
                        |> this.Detach
                    | None -> ()
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | ConfigUpsertProvider d -> do! this.ApplyConfig(d.requestId, ConfigMutation.upsertProvider, d.provider)
            | ConfigUpsertMcp d -> do! this.ApplyConfig(d.requestId, ConfigMutation.upsertMcp, d.server)
            | ConfigUpdateGeneration d -> do! this.ApplyConfig(d.requestId, ConfigMutation.updateGeneration, d.generation)
            | ToolApprovalCapability d ->
                match handshakeState with
                | Authenticated ->
                    lock cursorLock (fun () -> approvalCapable <- d.supported)
                    if d.supported then
                        let observed = lock cursorLock (fun () -> observedConversations)
                        for convId in observed do
                            for ev in orchestrator.Approvals.PendingFor convId do
                                this.TrySend ev |> ignore
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | ToolApprovalDecision d ->
                match handshakeState with
                | Authenticated -> orchestrator.Approvals.Decide(d.approvalId, d.approved, (fun ev -> this.TrySend ev |> ignore))
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | ConfigUpdateTools d -> do! this.ApplyConfig(d.requestId, ConfigMutation.updateTools, d.tools)
            | ConfigDeleteProvider d ->
                do! this.ApplyConfigWith(d.requestId, fun cfg -> ConfigMutation.deleteProvider cfg d.providerId)
            | ConfigDeleteMcp d ->
                do! this.ApplyConfigWith(d.requestId, fun cfg -> ConfigMutation.deleteMcp cfg d.serverId)
            | ConfigUpsertPreset d -> do! this.ApplyConfig(d.requestId, ConfigMutation.upsertPreset, d.preset)
            | ConfigDeletePreset d ->
                do! this.ApplyConfigWith(d.requestId, fun cfg -> ConfigMutation.deletePreset cfg d.presetId)
            | PairingInvite ->
                match handshakeState with
                | Authenticated ->
                    let cfg = getConfig ()
                    let record =
                        lock cursorLock (fun () -> tokenHash)
                        |> Option.bind (fun h -> cfg.authClients |> List.tryFind (fun c -> c.tokenHash = h))
                    let name, revoked = match record with Some r -> r.name, r.revoked | None -> "", true
                    match desk.Invite(DateTimeOffset.UtcNow, name, revoked, remoteAddress, TimeSpan.FromMinutes 5.0) with
                    | Ok s ->
                        // 邀请码只在内存与本条出站事件里：不写日志、不写 stderr。
                        this.TrySend(PairingInviteIssued {| code = s.code; expiresAtUtc = s.expiresAtUtc; expiresInSeconds = 300 |}) |> ignore
                    | Error _ ->
                        this.TrySend(PairingInviteRejected {| reason = "not-desktop" |}) |> ignore
                | NotStarted ->
                    // 未认证连接发邀请：告知拒绝后关闭。（PairingDesk 不对外记失败，旧版计入限流这一点见缺口）
                    do! this.SendAndClose(PairingInviteRejected {| reason = "not-authenticated" |}, "not authenticated")
            | DevicesRequest ->
                match handshakeState with
                | Authenticated -> this.SendDevices()
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | DeviceRename d ->
                match handshakeState with
                | Authenticated -> this.ChangeDevice(d.requestId, d.deviceId, DeviceChange.Rename d.name)
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | DeviceRevoke d ->
                match handshakeState with
                | Authenticated -> this.ChangeDevice(d.requestId, d.deviceId, DeviceChange.Revoke)
                | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
            | _ -> ()
        }

    /// 当前连接对应的令牌记录。
    member private _.CurrentClientRecord() : ClientAuthRecord option =
        let cfg = getConfig ()
        lock cursorLock (fun () -> tokenHash)
        |> Option.bind (fun h -> cfg.authClients |> List.tryFind (fun c -> c.tokenHash = h))

    /// 下发设备列表（含运行时在线状态；令牌哈希不出主机，只给截断的稳定 id）。
    member private this.SendDevices() : unit =
        let cfg = getConfig ()
        let online = onlineTokenHashes () |> Set.ofList
        let me = lock cursorLock (fun () -> tokenHash)
        let items = JsonArray()
        for c in cfg.authClients do
            let o = JsonObject()
            o["id"] <- ClientToken.ClientToken.deviceId c.tokenHash
            o["name"] <- c.name
            o["createdAt"] <- c.createdAtUtc.UtcDateTime.ToString("o", Globalization.CultureInfo.InvariantCulture)
            match c.lastSeenUtc with
            | Some t -> o["lastSeen"] <- t.UtcDateTime.ToString("o", Globalization.CultureInfo.InvariantCulture)
            | None -> ()
            o["revoked"] <- c.revoked
            o["online"] <- (not c.revoked && online.Contains c.tokenHash)
            o["current"] <- (me = Some c.tokenHash)
            o["desktop"] <- DesktopClient.DesktopClient.isReservedName c.name
            items.Add o
        let canInvite = (match this.CurrentClientRecord() with Some r -> DesktopClient.DesktopClient.isDesktop r.name r.revoked remoteAddress | None -> false)
        this.TrySend(DevicesSnapshot {| items = items; canInvite = canInvite |}) |> ignore

    /// 重命名/吊销设备：与其他配置写入同一条原子路径；结果以 config.applied 回执，随后下发新列表。
    member private this.ChangeDevice(requestId: Guid, deviceId: string, change: DeviceChange) : unit =
        let cfg = getConfig ()
        let me = lock cursorLock (fun () -> tokenHash)
        let reject (message: string) =
            this.TrySend(ConfigApplied {| requestId = requestId; ok = false; errors = [ message ] |}) |> ignore
        match cfg.authClients |> List.tryFind (fun c -> ClientToken.ClientToken.deviceId c.tokenHash = deviceId) with
        | None -> reject "设备不存在"
        | Some target ->
            let isDesktop = DesktopClient.DesktopClient.isReservedName target.name
            match change with
            | DeviceChange.Rename name when String.IsNullOrWhiteSpace name -> reject "设备名称不能为空"
            | DeviceChange.Rename name when name.Trim().Length > 64 -> reject "设备名称最多 64 个字符"
            | DeviceChange.Rename name when DesktopClient.DesktopClient.isReservedName name -> reject "该名称为本机桌面客户端保留"
            | DeviceChange.Rename _ when isDesktop -> reject "本机桌面客户端不能重命名"
            | DeviceChange.Revoke when isDesktop -> reject "本机桌面客户端不能在这里吊销"
            | DeviceChange.Revoke when me = Some target.tokenHash -> reject "不能吊销当前正在使用的设备"
            | DeviceChange.Revoke when target.revoked -> reject "该设备已被吊销"
            | _ ->
                let result =
                    updateConfig (fun current ->
                        { current with
                            authClients =
                                current.authClients
                                |> List.map (fun c ->
                                    if c.tokenHash <> target.tokenHash then c
                                    else
                                        match change with
                                        | DeviceChange.Rename name -> { c with name = name.Trim() }
                                        | DeviceChange.Revoke -> { c with revoked = true }) })
                match result with
                | Ok () ->
                    this.TrySend(ConfigApplied {| requestId = requestId; ok = true; errors = [] |}) |> ignore
                    this.SendDevices()
                | Error e -> reject e

    /// 下发目录快照（可用模型服务 / 模型 / 工具 / 生成默认值）。
    member private this.SendCatalog() : unit =
        let cfg = getConfig ()
        this.TrySend(
            CatalogSnapshot
                {| providers = ProviderCatalog.providers cfg
                   tools = toolDescriptors ()
                   generation = ProviderCatalog.generation cfg |})
        |> ignore

    /// 后台任务与连接生命周期解耦，同时不留未观察异常。
    member private _.Detach(work: Task) : unit =
        work.ContinueWith(fun (t: Task) ->
            if not (isNull t.Exception) then
                // O-001/R6：后台任务异常必须留下 error 级诊断证据（含 connectionId），
                // 不得仅 info 级或完全未观察。
                let baseEx = t.Exception.GetBaseException()
                Stderr.write
                    "detached-task-failed"
                    [ "connectionId", connectionId
                      "message", baseEx.Message
                      "exception", baseEx ]
                logInfo(sprintf "detached task failed: %s" baseEx.Message))
        |> ignore

    /// 配置写入的公共路径：解析 → 校验 → 原子重写 → 回执 + 广播新目录。
    member private this.ApplyConfig(requestId: Guid, apply: AppConfig -> JsonObject -> Result<AppConfig, string list>, payload: JsonObject) : Task =
        this.ApplyConfigWith(requestId, fun cfg -> apply cfg payload)

    member private this.ApplyConfigWith(requestId: Guid, apply: AppConfig -> Result<AppConfig, string list>) : Task =
        task {
            match handshakeState with
            | Authenticated ->
                // 先在当前快照上校验，避免把非法配置塞进原子重写
                match apply (getConfig ()) with
                | Error errors ->
                    this.TrySend(ConfigApplied {| requestId = requestId; ok = false; errors = errors |}) |> ignore
                | Ok _ ->
                    let mutable failure: string list = []
                    let result =
                        updateConfig (fun cfg ->
                            match apply cfg with
                            | Ok next -> next
                            | Error errors ->
                                failure <- errors
                                cfg)
                    if not (List.isEmpty failure) then
                        this.TrySend(ConfigApplied {| requestId = requestId; ok = false; errors = failure |}) |> ignore
                    else
                        match result with
                        | Ok () ->
                            this.TrySend(ConfigApplied {| requestId = requestId; ok = true; errors = [] |}) |> ignore
                            this.SendCatalog()
                        | Error e ->
                            this.TrySend(ConfigApplied {| requestId = requestId; ok = false; errors = [ e ] |}) |> ignore
            | _ -> do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "not authenticated")
        }

    /// 连接主循环：先发 Hello，然后并行收发。
    /// 认证超时（决策 55）：建立后 15 秒内未完成认证则关闭，避免悬挂连接占用资源。
    member this.Run(ct: CancellationToken) : Task =
        task {
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
            // 入站承载归一：wanxiang.dev/* CUSTOM 的 value（扁平或 {type,payload} 形态）
            // 统一折成 {type,payload}，再按命令/事件双路径解码。
            // 未知类型与别家 CUSTOM：合法忽略并告警，**不得**断连（AG-UI 增量安全）；
            // 畸形载荷才是协议违规。
            let inner =
                match Wanxiang.Agui.TolerantReader.parse jsonText with
                | Wanxiang.Agui.ParsedEvent.Custom(name, value) when name.StartsWith Wanxiang.Agui.Capabilities.Namespace ->
                    let wireType = name.Substring Wanxiang.Agui.Capabilities.Namespace.Length
                    match value with
                    | :? System.Text.Json.Nodes.JsonObject as vo when vo.ContainsKey "type" ->
                        // value 已是完整外壳
                        Ok(Some(value.ToJsonString()))
                    | :? System.Text.Json.Nodes.JsonObject as vo ->
                        // 扁平字段 → 折成 {type,payload}
                        let wrapped = System.Text.Json.Nodes.JsonObject()
                        wrapped["type"] <- wireType
                        wrapped["payload"] <- vo.DeepClone()
                        Ok(Some(wrapped.ToJsonString()))
                    | _ ->
                        let wrapped = System.Text.Json.Nodes.JsonObject()
                        wrapped["type"] <- wireType
                        wrapped["payload"] <- (match value with null -> System.Text.Json.Nodes.JsonObject() :> System.Text.Json.Nodes.JsonNode | v -> v.DeepClone())
                        Ok(Some(wrapped.ToJsonString()))
                | Wanxiang.Agui.ParsedEvent.Custom _ ->
                    Ok(None)    // 别家 CUSTOM：忽略
                | Wanxiang.Agui.ParsedEvent.Unrecognized _ ->
                    Ok(None)    // 未知类型：忽略（对端可能比本端新）
                | Wanxiang.Agui.ParsedEvent.Known _ ->
                    // 标准 AG-UI 面（RUN_STARTED 等）：走事件解码
                    Ok(Some jsonText)
                | Wanxiang.Agui.ParsedEvent.Malformed reason ->
                    Error reason
            match inner with
            | Error reason ->
                Stderr.write "protocol-decode-error" [ "address", remoteAddress; "message", reason ]
                do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "invalid event")
            | Ok None ->
                // 未知类型是合法忽略（AG-UI 增量安全），记录用于诊断；
                // 但只在认证后记录——未认证客户端（认证超时 15s 自动关闭）可
                // 高频发送未知类型洪泛 stderr（磁盘/日志 DoS 面），不值得为
                // 未认证流量留下诊断记录。
                if this.IsAuthenticated then
                    Stderr.write "protocol-unknown-event-ignored" [ "address", remoteAddress ]
            | Ok(Some payload) ->
                match WireCodec.tryDecodeCommand payload with
                | Ok cmd ->
                    do! this.HandleEvent(WireEvent.Command cmd)
                | Error cmdErr ->
                    match WireCodec.tryDecode payload with
                    | Ok ev -> do! this.HandleEvent ev
                    | Error evErr when WireCodec.isUnknownType (string cmdErr) (string evErr) ->
                        // 55.8 R1：未知的 wanxiang.dev/* 事件类型（对端可能比本端新）：丢弃，不断连。
                        if this.IsAuthenticated then
                            Stderr.write "protocol-unknown-event-ignored" [ "address", remoteAddress ]
                    | Error evErr ->
                        // 两条路都解不出：真畸形。报两者中更相关的（命令优先）。
                        let detail = sprintf "command: %s; event: %s" (string cmdErr) (string evErr)
                        Stderr.write "protocol-decode-error" [ "address", remoteAddress; "message", detail ]
                        do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "invalid event")
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

    /// 广播一个与具体对话无关的事件（配置变更、目录快照）。
    /// 只发给已认证连接：未认证连接不该看到模型服务清单。
    member this.BroadcastToAll(ev: WireEvent) : unit =
        for conn in this.All() do
            if conn.IsAuthenticated then conn.PushTransient ev

    /// 广播权威提交（coordinator.onCommitted 注入）。
    member this.BroadcastCommit(commit: Events.Commit) : unit =
        // 收集受影响对话与变更种类
        // 每个 (对话, 变更种类) 只处理一次：下面的内层循环会重走整个 events 数组，
        // 若 affected 里同一对出现 N 次，同一条消息就会被推送 N 次
        // （一次提交含多条消息时必然发生，例如「重新生成」的批量作废）。
        let affected =
            [ for ev in commit.events do
                  match ev with
                  | AgentMessageRecorded d -> yield d.conversationId, "message"
                  | MessageDeleted d -> yield d.conversationId, "message"
                  | ConversationCreated d -> yield d.conversationId, "list"
                  | ConversationForked d -> yield d.conversationId, "list"
                  | ConversationRenamed d -> yield d.conversationId, "list"
                  | EventData.ConversationDeleted d -> yield d.conversationId, "list"
                  | ConversationConfigUpdated d -> yield d.conversationId, "list"
                  | ConversationFlagsChanged d -> yield d.conversationId, "list" ]
            |> List.distinct
        for (convId, kind) in affected do
            for conn in this.All() do
                match kind with
                | "message" ->
                    if conn.Observes convId && conn.ShouldReceiveAuthority convId then
                        for ev in commit.events do
                            match ev with
                            | AgentMessageRecorded d when d.conversationId = convId ->
                                conn.PushAuthority(
                                    MessageCommitted
                                        {| conversationId = convId
                                           commitId = commit.id
                                           committedAt = commit.committedAtUtc
                                           payload = d.payloadJson |})
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

    /// 广播临时事件（generation.delta 等）给观察某对话的连接。
    /// P2-6：generation 状态变化同步推送轻量列表更新（对话摘要含 runtimeState，Q125）。
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
