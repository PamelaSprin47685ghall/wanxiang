namespace Wanxiang.Client

open System
open System.Net.WebSockets
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Wanxiang.Core
open Wanxiang.Protocol

/// 客户端 WebSocket 连接（fire-and-forget 事件协议）。
/// 断线后由上层决定重连（决策 26/27：重连后重新 observe）。
type WsClient() =

    let mutable ws: ClientWebSocket = null
    let mutable cts: CancellationTokenSource = null
    let mutable connectionGeneration = 0
    let sendGate = new SemaphoreSlim(1, 1)
    let connectionLock = obj()

    let detachConnection () =
        let generation, previousSocket, previousCts =
            lock connectionLock (fun () ->
                let generation = Interlocked.Increment(&connectionGeneration)
                let previousSocket, previousCts = ws, cts
                ws <- null
                cts <- null
                generation, previousSocket, previousCts)
        if not (isNull previousCts) then
            try previousCts.Cancel() with _ -> ()
            previousCts.Dispose()
        if not (isNull previousSocket) then
            try previousSocket.Dispose() with _ -> ()
        generation

    let onEvent = Event<WireEvent>()
    let onClosed = Event<exn option>()
    let onConnectionEvent = Event<int * WireEvent>()
    let onConnectionClosed = Event<int * exn option>()

    member _.EventReceived = onEvent.Publish
    member _.Closed = onClosed.Publish
    member _.ConnectionEventReceived = onConnectionEvent.Publish
    member _.ConnectionClosed = onConnectionClosed.Publish
    member _.ConnectionGeneration = Volatile.Read(&connectionGeneration)

    member _.IsConnected =
        lock connectionLock (fun () -> not (isNull ws) && ws.State = WebSocketState.Open)

    /// 连接并启动接收循环（认证前连接处于受限状态，决策 55）。
    member this.ConnectWithGenerationAsync(uri: Uri, ct: CancellationToken) : Task<int> =
        task {
            let generation = detachConnection ()
            let newWs = new ClientWebSocket()
            let newCts = CancellationTokenSource.CreateLinkedTokenSource ct
            try
                do! newWs.ConnectAsync(uri, newCts.Token)
                let installed =
                    lock connectionLock (fun () ->
                        if generation = connectionGeneration then
                            ws <- newWs
                            cts <- newCts
                            true
                        else false)
                if installed then
                    Async.Start(this.ReceiveLoop(newWs, newCts.Token, generation)) |> ignore
                    return generation
                else
                    newWs.Dispose()
                    newCts.Dispose()
                    return raise (OperationCanceledException "connection superseded")
            with e ->
                newCts.Dispose()
                newWs.Dispose()
                return raise e
        }

    member this.ConnectAsync(uri: Uri, ct: CancellationToken) : Task =
        task {
            let! _ = this.ConnectWithGenerationAsync(uri, ct)
            return ()
        }

    member _.Disconnect() = detachConnection () |> ignore

    /// 返回传输是否完成，而不是把断线当成功。业务保存仍只认 command.committed。
    member private _.TrySendTextAsync(json: string, expectedGeneration: int option) : Task<bool> =
        task {
            let socket, tokenSource =
                lock connectionLock (fun () ->
                    if expectedGeneration |> Option.exists (fun expected -> expected <> connectionGeneration) then null, null
                    else ws, cts)
            if not (isNull socket) && not (isNull tokenSource) && socket.State = WebSocketState.Open then
                let bytes = Encoding.UTF8.GetBytes json
                try
                    do! sendGate.WaitAsync(tokenSource.Token)
                    try
                        if socket.State = WebSocketState.Open then
                            do! socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, tokenSource.Token)
                            return true
                        else return false
                    finally
                        sendGate.Release() |> ignore
                with
                | :? OperationCanceledException -> return false
                | :? ObjectDisposedException -> return false
                | :? WebSocketException -> return false
            else return false
        }

    member this.TrySendAsync(ev: WireEvent) : Task<bool> = this.TrySendTextAsync(WireCodec.encode ev, None)
    member this.TrySendCommandAsync(cmd: ClientCommand) : Task<bool> = this.TrySendTextAsync(WireCodec.encodeCommand cmd, None)
    member this.TrySendCommandAtGenerationAsync(generation: int, cmd: ClientCommand) : Task<bool> =
        this.TrySendTextAsync(WireCodec.encodeCommand cmd, Some generation)
    member this.TrySendAtGenerationAsync(generation: int, ev: WireEvent) : Task<bool> =
        this.TrySendTextAsync(WireCodec.encode ev, Some generation)

    /// 保留原有 API；需要用户反馈的调用方使用 TrySend 系列。
    member this.SendAsync(ev: WireEvent) : Task =
        task {
            let! _ = this.TrySendAsync ev
            return ()
        }

    member this.SendCommandAsync(cmd: ClientCommand) : Task =
        task {
            let! _ = this.TrySendCommandAsync cmd
            return ()
        }

    /// 接收循环用 async 而不是 task：browser-wasm 单线程运行时中，task CE 对 pending task 的 await 会
    /// 走 Task.InternalWait 阻塞等待（“Cannot wait on monitors on this runtime”），async 的 trampoline 不会。
    member private this.ReceiveLoop(socket: ClientWebSocket, ct: CancellationToken, generation: int) : Async<unit> =
        async {
            let mutable shouldNotify = true
            let isCurrent () = generation = connectionGeneration
            let closed error =
                if isCurrent () then
                    onConnectionClosed.Trigger(generation, error)
                    onClosed.Trigger error
            try
                let buffer = Array.zeroCreate<byte> (1024 * 1024)
                use ms = new System.IO.MemoryStream()
                let mutable doneReceiving = false
                while not doneReceiving && socket.State = WebSocketState.Open do
                    let! result = socket.ReceiveAsync(ArraySegment<byte>(buffer), ct) |> Async.AwaitTask
                    if result.MessageType = WebSocketMessageType.Close then
                        doneReceiving <- true
                    elif result.MessageType = WebSocketMessageType.Binary then
                        doneReceiving <- true
                        closed (Some(exn "binary frame received"))
                        shouldNotify <- false
                    else
                        ms.Write(buffer, 0, result.Count)
                        if result.EndOfMessage then
                            let text = Encoding.UTF8.GetString(ms.ToArray())
                            ms.SetLength 0L
                            match WireCodec.tryDecode text with
                            | Ok ev when isCurrent () ->
                                onConnectionEvent.Trigger(generation, ev)
                                onEvent.Trigger ev
                            | Ok _ -> ()
                            | Error _ -> ()
                if shouldNotify then closed None
            with
            | :? OperationCanceledException -> if shouldNotify then closed None
            | :? ObjectDisposedException -> if shouldNotify then closed None
            | :? WebSocketException as e -> if shouldNotify then closed (Some e)
            | e -> if shouldNotify then closed (Some e)
        }

/// 客户端会话状态（内存态；PWA 不持久缓存，决策 3）。
type ConversationView = {
    conversationId: Guid
    mutable title: string
    mutable lastCommitId: CommitId
    mutable runtimeState: string
    mutable messages: JsonArray
    /// 快照携带的最早 commitId 与是否还有更早历史（Q127 分页）
    mutable pageEarliest: CommitId
    mutable pageHasMore: bool
    /// 会话级配置快照（P0-4：UI 读写 SessionConfig）
    mutable config: SessionConfig
}

/// 客户端状态机：维护观察视图 + 游标。
type ClientState() =

    let mutable conversationList: JsonArray = JsonArray()
    let mutable conversations: Map<Guid, ConversationView> = Map.empty
    let mutable cursor: CommitId = 0UL
    let mutable latestCommitId: CommitId = 0UL

    let listChanged = Event<JsonArray>()
    let convChanged = Event<Guid>()
    let cursorChanged = Event<CommitId>()

    member _.ConversationList = conversationList
    member _.Conversations = conversations
    member _.Cursor = cursor
    member _.LatestCommitId = latestCommitId

    /// 实例身份改变时清空旧投影，绝不把另一台服务端的游标带过来。
    member _.Reset() =
        conversationList <- JsonArray()
        conversations <- Map.empty
        cursor <- 0UL
        latestCommitId <- 0UL

    member _.ListChanged = listChanged.Publish
    member _.ConversationChanged = convChanged.Publish
    member _.CursorChanged = cursorChanged.Publish

    /// 处理服务端事件，更新本地状态。
    member this.Handle(ev: WireEvent) : unit =
        match ev with
        | ConversationListSnapshot d ->
            conversationList <- d.items
            latestCommitId <- max latestCommitId d.lastCommitId
            listChanged.Trigger conversationList
        | ConversationSnapshot d ->
            let view =
                match conversations.TryFind d.conversationId with
                | Some v ->
                    v.title <- d.title
                    v.lastCommitId <- d.lastCommitId
                    v.runtimeState <- d.runtimeState
                    v.messages <- d.messages
                    v.pageEarliest <- d.snapshotEarliestCommitId
                    v.pageHasMore <- d.snapshotHasMore
                    v.config <- d.config
                    v
                | None ->
                    { conversationId = d.conversationId
                      title = d.title
                      lastCommitId = d.lastCommitId
                      runtimeState = d.runtimeState
                      messages = d.messages
                      pageEarliest = d.snapshotEarliestCommitId
                      pageHasMore = d.snapshotHasMore
                      config = d.config }
            conversations <- conversations.Add(d.conversationId, view)
            latestCommitId <- max latestCommitId d.lastCommitId
            convChanged.Trigger d.conversationId
        | HistoryPage d ->
            // Q127 分页：把更早历史按 commitId 升序前置拼接（去重）
            match conversations.TryFind d.conversationId with
            | Some v ->
                let existing = System.Collections.Generic.HashSet<CommitId>()
                for m in v.messages do
                    if m <> null && m.GetValueKind() = System.Text.Json.JsonValueKind.Object then
                        let o = m.AsObject()
                        let mutable c: System.Text.Json.Nodes.JsonNode = null
                        if o.TryGetPropertyValue("commitId", &c) && c <> null then
                            existing.Add(c.GetValue<uint64>()) |> ignore
                let prev = JsonArray()
                for m in d.items do
                    let commitId =
                        match m with
                        | :? System.Text.Json.Nodes.JsonObject as o ->
                            let mutable c: System.Text.Json.Nodes.JsonNode = null
                            if o.TryGetPropertyValue("commitId", &c) && c <> null then c.GetValue<uint64>() else 0UL
                        | _ -> 0UL
                    if commitId > 0UL && not (existing.Contains commitId) then
                        prev.Add (m.DeepClone())
                let combined = JsonArray()
                for m in prev do combined.Add m
                for m in v.messages do combined.Add (m.DeepClone())
                v.messages <- combined
                if prev.Count > 0 then
                    match prev[0] with
                    | :? System.Text.Json.Nodes.JsonObject as o ->
                        let mutable c: System.Text.Json.Nodes.JsonNode = null
                        if o.TryGetPropertyValue("commitId", &c) && c <> null then
                            v.pageEarliest <- c.GetValue<uint64>()
                    | _ -> ()
                v.pageHasMore <- d.hasMore
                convChanged.Trigger d.conversationId
            | None -> ()
        | MessageCommitted d ->
            match conversations.TryFind d.conversationId with
            | Some v ->
                // commitId 是消息的唯一标识：重复推送、快照与 catch-up 交叠时
                // 必须按它去重，否则同一条回复会在界面上出现两次。
                let alreadyApplied =
                    v.messages
                    |> Seq.exists (fun m ->
                        match m with
                        | :? System.Text.Json.Nodes.JsonObject as o ->
                            let mutable c: System.Text.Json.Nodes.JsonNode = null
                            o.TryGetPropertyValue("commitId", &c)
                            && not (isNull c)
                            && c.GetValue<uint64>() = d.commitId
                        | _ -> false)
                if not alreadyApplied then
                    let o = System.Text.Json.Nodes.JsonObject()
                    o["commitId"] <- d.commitId
                    o["committedAt"] <- d.committedAt.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                    o["payload"] <- d.payload.DeepClone()
                    v.messages.Add o
                v.lastCommitId <- max v.lastCommitId d.commitId
                latestCommitId <- max latestCommitId d.commitId
                convChanged.Trigger d.conversationId
            | None -> ()
        | ConversationUpdated d ->
            latestCommitId <- max latestCommitId d.commitId
            match conversations.TryFind d.conversationId with
            | Some v ->
                if not (isNull d.change) then
                    let mutable delNode: System.Text.Json.Nodes.JsonNode = null
                    if d.change.TryGetPropertyValue("deletedMessage", &delNode) && not (isNull delNode) then
                        let delId = delNode.GetValue<uint64>()
                        let remaining = JsonArray()
                        for m in v.messages do
                            match m with
                            | :? System.Text.Json.Nodes.JsonObject as o ->
                                let mutable c: System.Text.Json.Nodes.JsonNode = null
                                if o.TryGetPropertyValue("commitId", &c) && not (isNull c) && c.GetValue<uint64>() = delId then
                                    ()
                                else
                                    remaining.Add(m.DeepClone())
                            | _ -> remaining.Add(m.DeepClone())
                        v.messages <- remaining
                    let mutable titleNode: System.Text.Json.Nodes.JsonNode = null
                    if d.change.TryGetPropertyValue("title", &titleNode) && not (isNull titleNode) then
                        v.title <- titleNode.GetValue<string>()
                // 水位只能单调上升：generation 状态变化推来的 conversation.updated
                // 携带 commitId = 0，直接赋值会把水位清零，
                // 于是 catch-up 把整段历史当成未应用重放一遍，界面出现重复消息。
                v.lastCommitId <- max v.lastCommitId d.commitId
                convChanged.Trigger d.conversationId
            | None -> ()
        | AuthorityCatchUp d ->
            // 慢客户端追赶（决策 32-34）：应用批次中的权威提交，然后推进游标
            let mutable maxApplied = d.fromCursor
            for line in d.items do
                match CommitCodec.tryCommitFromJsonLine (line.GetValue<string>()) with
                | Some commit ->
                    for ev in commit.events do
                        match ev with
                        | AgentMessageRecorded m when conversations.ContainsKey m.conversationId ->
                            match conversations.TryFind m.conversationId with
                            | Some v ->
                                // 按 commitId 精确去重：水位比较不足以防重放
                                let alreadyApplied =
                                    v.messages
                                    |> Seq.exists (fun existing ->
                                        match existing with
                                        | :? System.Text.Json.Nodes.JsonObject as o ->
                                            let mutable c: System.Text.Json.Nodes.JsonNode = null
                                            o.TryGetPropertyValue("commitId", &c)
                                            && not (isNull c)
                                            && c.GetValue<uint64>() = commit.id
                                        | _ -> false)
                                if not alreadyApplied then
                                    let o = System.Text.Json.Nodes.JsonObject()
                                    o["commitId"] <- commit.id
                                    o["committedAt"] <- commit.committedAtUtc.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                                    o["payload"] <- m.payloadJson.DeepClone()
                                    v.messages.Add o
                                    v.lastCommitId <- max v.lastCommitId commit.id
                                    convChanged.Trigger m.conversationId
                            | _ -> ()
                        | MessageDeleted m when conversations.ContainsKey m.conversationId ->
                            match conversations.TryFind m.conversationId with
                            | Some v ->
                                let remaining = JsonArray()
                                for item in v.messages do
                                    match item with
                                    | :? System.Text.Json.Nodes.JsonObject as o ->
                                        let mutable c: System.Text.Json.Nodes.JsonNode = null
                                        if o.TryGetPropertyValue("commitId", &c) && not (isNull c) && c.GetValue<uint64>() = m.messageCommitId then
                                            ()
                                        else
                                            remaining.Add(item.DeepClone())
                                    | _ -> remaining.Add(item.DeepClone())
                                v.messages <- remaining
                                v.lastCommitId <- max v.lastCommitId commit.id
                                convChanged.Trigger m.conversationId
                            | _ -> ()
                        | _ -> ()
                    maxApplied <- max maxApplied commit.id
                | None -> ()
            latestCommitId <- max latestCommitId d.toCommitId
            this.AdvanceCursorTo maxApplied
        | GenerationStarted d ->
            match conversations.TryFind d.conversationId with
            | Some v ->
                v.runtimeState <- "generating"
                convChanged.Trigger d.conversationId
            | None -> ()
        | GenerationFinished d ->
            match conversations.TryFind d.conversationId with
            | Some v ->
                v.runtimeState <- "idle"
                convChanged.Trigger d.conversationId
            | None -> ()
        | _ -> ()

    /// 客户端应用了一批事件后推进游标（决策 33）。
    member this.AdvanceCursor() : unit =
        cursor <- latestCommitId
        cursorChanged.Trigger cursor

    /// catch-up 批次应用完成后推进游标：只确认实际应用的 commit（不含被服务端过滤的跳号区间）。
    member this.AdvanceCursorTo(applied: CommitId) : unit =
        cursor <- max cursor applied
        cursorChanged.Trigger cursor

    /// 生成要发送给服务端的游标确认事件。
    member this.CursorAdvancedEvent() : WireEvent =
        CursorAdvanced {| id = cursor |}
