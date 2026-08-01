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

    let onEvent = Event<WireEvent>()
    let onClosed = Event<exn option>()

    member _.EventReceived = onEvent.Publish
    member _.Closed = onClosed.Publish

    member _.IsConnected =
        not (isNull ws) && ws.State = WebSocketState.Open

    /// 连接并启动接收循环（认证前连接处于受限状态，决策 55）。
    member this.ConnectAsync(uri: Uri, ct: CancellationToken) : Task =
        task {
            this.Disconnect()
            let newWs = new ClientWebSocket()
            let newCts = CancellationTokenSource.CreateLinkedTokenSource ct
            let generation = connectionGeneration + 1
            connectionGeneration <- generation
            try
                do! newWs.ConnectAsync(uri, newCts.Token)
                ws <- newWs
                cts <- newCts
                this.ReceiveLoop(newWs, newCts.Token, generation) |> ignore
            with e ->
                newCts.Dispose()
                newWs.Dispose()
                return raise e
        }

    member _.Disconnect() =
        if not (isNull cts) then
            try cts.Cancel() with _ -> ()
        if not (isNull ws) then
            try ws.Dispose() with _ -> ()
        ws <- null

    /// 发送一个事件（文本 JSON 帧）。ClientWebSocket 同时只允许一个发送者。
    member _.SendAsync(ev: WireEvent) : Task =
        task {
            let socket = ws
            let tokenSource = cts
            if not (isNull socket) && not (isNull tokenSource) && socket.State = WebSocketState.Open then
                let json = WireCodec.encode ev
                let bytes = Encoding.UTF8.GetBytes json
                try
                    do! sendGate.WaitAsync(tokenSource.Token)
                    try
                        if socket.State = WebSocketState.Open then
                            do! socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, tokenSource.Token)
                    finally
                        sendGate.Release() |> ignore
                with
                | :? OperationCanceledException -> ()
                | :? ObjectDisposedException -> ()
                | :? WebSocketException -> ()
        }

    /// 发送客户端命令（专用编码路径）。
    member _.SendCommandAsync(cmd: ClientCommand) : Task =
        task {
            let socket = ws
            let tokenSource = cts
            if not (isNull socket) && not (isNull tokenSource) && socket.State = WebSocketState.Open then
                let json = WireCodec.encodeCommand cmd
                let bytes = Encoding.UTF8.GetBytes json
                try
                    do! sendGate.WaitAsync(tokenSource.Token)
                    try
                        if socket.State = WebSocketState.Open then
                            do! socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, tokenSource.Token)
                    finally
                        sendGate.Release() |> ignore
                with
                | :? OperationCanceledException -> ()
                | :? ObjectDisposedException -> ()
                | :? WebSocketException -> ()
        }

    member private this.ReceiveLoop(socket: ClientWebSocket, ct: CancellationToken, generation: int) : Task =
        task {
            let mutable shouldNotify = true
            let isCurrent () = generation = connectionGeneration
            try
                let buffer = Array.zeroCreate<byte> (1024 * 1024)
                let ms = new System.IO.MemoryStream()
                let mutable doneReceiving = false
                while not doneReceiving && socket.State = WebSocketState.Open do
                    let! result = socket.ReceiveAsync(ArraySegment<byte>(buffer), ct)
                    if result.MessageType = WebSocketMessageType.Close then
                        doneReceiving <- true
                    elif result.MessageType = WebSocketMessageType.Binary then
                        doneReceiving <- true
                        if isCurrent () then onClosed.Trigger(Some(exn "binary frame received"))
                        shouldNotify <- false
                    else
                        ms.Write(buffer, 0, result.Count)
                        if result.EndOfMessage then
                            let text = Encoding.UTF8.GetString(ms.ToArray())
                            ms.SetLength 0L
                            match WireCodec.tryDecode text with
                            | Ok ev -> onEvent.Trigger ev
                            | Error _ -> ()
                if shouldNotify && isCurrent () then onClosed.Trigger None
            with
            | :? OperationCanceledException -> if shouldNotify && isCurrent () then onClosed.Trigger None
            | :? ObjectDisposedException -> if shouldNotify && isCurrent () then onClosed.Trigger None
            | :? WebSocketException as e -> if shouldNotify && isCurrent () then onClosed.Trigger(Some e)
            | e -> if shouldNotify && isCurrent () then onClosed.Trigger(Some e)
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
                    v
                | None ->
                    { conversationId = d.conversationId
                      title = d.title
                      lastCommitId = d.lastCommitId
                      runtimeState = d.runtimeState
                      messages = d.messages
                      pageEarliest = d.snapshotEarliestCommitId
                      pageHasMore = d.snapshotHasMore }
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
                let o = System.Text.Json.Nodes.JsonObject()
                o["commitId"] <- d.commitId
                o["payload"] <- d.payload.DeepClone()
                v.messages.Add o
                v.lastCommitId <- d.commitId
                latestCommitId <- max latestCommitId d.commitId
                convChanged.Trigger d.conversationId
            | None -> ()
        | ConversationUpdated d ->
            latestCommitId <- max latestCommitId d.commitId
            match conversations.TryFind d.conversationId with
            | Some v ->
                v.lastCommitId <- d.commitId
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
                            | Some v when commit.id > v.lastCommitId ->
                                // 去重：实时 MessageCommitted 已应用过的 commit 不重复添加
                                let o = System.Text.Json.Nodes.JsonObject()
                                o["commitId"] <- commit.id
                                o["payload"] <- m.payloadJson.DeepClone()
                                v.messages.Add o
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
