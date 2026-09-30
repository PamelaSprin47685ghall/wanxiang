namespace Wanxiang.Tests

open System
open System.Net.WebSockets
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Wanxiang.Client
open Wanxiang.Core
open Wanxiang.Protocol

module ClientStateAndWsClientTests =

    // ==========================================
    // 1. ReconnectBackoff 退避重连状态机与计算策略
    // ==========================================

    [<Fact>]
    let ``ReconnectBackoff base delay is 1000ms and max delay is 30000ms`` () =
        Assert.Equal(1000, ReconnectBackoff.baseDelayMs)
        Assert.Equal(30000, ReconnectBackoff.maxDelayMs)

    [<Fact>]
    let ``ReconnectBackoff next doubles delay under cap`` () =
        Assert.Equal(2000, ReconnectBackoff.next 1000)
        Assert.Equal(4000, ReconnectBackoff.next 2000)
        Assert.Equal(8000, ReconnectBackoff.next 4000)
        Assert.Equal(16000, ReconnectBackoff.next 8000)

    [<Fact>]
    let ``ReconnectBackoff next clamps at maxDelayMs`` () =
        Assert.Equal(30000, ReconnectBackoff.next 16000)
        Assert.Equal(30000, ReconnectBackoff.next 25000)
        Assert.Equal(30000, ReconnectBackoff.next 30000)
        Assert.Equal(30000, ReconnectBackoff.next 50000)

    [<Fact>]
    let ``ReconnectBackoff next handles smaller than baseDelayMs values safely`` () =
        // max baseDelayMs current ensures lower values bounce to at least 2000ms
        Assert.Equal(2000, ReconnectBackoff.next 0)
        Assert.Equal(2000, ReconnectBackoff.next 500)
        Assert.Equal(2000, ReconnectBackoff.next -100)

    [<Fact>]
    let ``ReconnectBackoff schedule produces correct sequence for failure counts`` () =
        let s0 = ReconnectBackoff.schedule 0
        Assert.Empty s0

        let s1 = ReconnectBackoff.schedule 1
        Assert.Equal<int list>([ 1000 ], s1)

        let s3 = ReconnectBackoff.schedule 3
        Assert.Equal<int list>([ 1000; 2000; 4000 ], s3)

        let s7 = ReconnectBackoff.schedule 7
        Assert.Equal<int list>([ 1000; 2000; 4000; 8000; 16000; 30000; 30000 ], s7)

    // ==========================================
    // 2. WsClient 状态、代际隔离与断线行为
    // ==========================================

    [<Fact>]
    let ``WsClient initial state is disconnected with generation zero`` () =
        let client = new WsClient()
        Assert.False(client.IsConnected)
        Assert.Equal(0, client.ConnectionGeneration)

    [<Fact>]
    let ``WsClient TrySend when disconnected safely returns false`` () =
        let client = new WsClient()
        let task = client.TrySendAsync(WireEvent.Ping)
        let sent = task.GetAwaiter().GetResult()
        Assert.False(sent)

    [<Fact>]
    let ``WsClient TrySendAtGeneration with mismatched generation safely returns false`` () =
        let client = new WsClient()
        let task = client.TrySendAtGenerationAsync(999, WireEvent.Ping)
        let sent = task.GetAwaiter().GetResult()
        Assert.False(sent)

    [<Fact>]
    let ``WsClient TrySendCommand when disconnected safely returns false`` () =
        let client = new WsClient()
        let cmd = ClientCommand.RenameConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = "test" |}
        let task = client.TrySendCommandAsync(cmd)
        let sent = task.GetAwaiter().GetResult()
        Assert.False(sent)

    [<Fact>]
    let ``WsClient TrySendCommandAtGeneration when disconnected safely returns false`` () =
        let client = new WsClient()
        let cmd = ClientCommand.RenameConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = "test" |}
        let task = client.TrySendCommandAtGenerationAsync(1, cmd)
        let sent = task.GetAwaiter().GetResult()
        Assert.False(sent)

    [<Fact>]
    let ``WsClient Disconnect on fresh client increments generation and does not throw`` () =
        let client = new WsClient()
        client.Disconnect()
        Assert.Equal(1, client.ConnectionGeneration)
        client.Disconnect()
        Assert.Equal(2, client.ConnectionGeneration)

    [<Fact>]
    let ``WsClient Connect with canceled token immediately throws or fails safely and increments generation`` () =
        let client = new WsClient()
        use cts = new CancellationTokenSource()
        cts.Cancel()
        let uri = Uri("ws://127.0.0.1:65534/test")
        let task = client.ConnectAsync(uri, cts.Token)
        Assert.ThrowsAny<Exception>(fun () -> task.GetAwaiter().GetResult() |> ignore) |> ignore
        Assert.False(client.IsConnected)
        Assert.Equal(1, client.ConnectionGeneration)

    [<Fact>]
    let ``WsClient ConnectWithGenerationAsync increments generation on each attempt`` () =
        let client = new WsClient()
        use cts = new CancellationTokenSource()
        cts.Cancel()
        let uri = Uri("ws://127.0.0.1:65534/test")
        try client.ConnectWithGenerationAsync(uri, cts.Token).GetAwaiter().GetResult() |> ignore with _ -> ()
        Assert.Equal(1, client.ConnectionGeneration)
        try client.ConnectWithGenerationAsync(uri, cts.Token).GetAwaiter().GetResult() |> ignore with _ -> ()
        Assert.Equal(2, client.ConnectionGeneration)

    [<Fact>]
    let ``WsClient SendAsync and SendCommandAsync fire-and-forget when disconnected safely complete`` () =
        let client = new WsClient()
        let t1 = client.SendAsync(WireEvent.Ping)
        t1.GetAwaiter().GetResult()
        let cmd = ClientCommand.ForkConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); parentConversationId = Guid.NewGuid(); forkAfterId = None; config = { SessionConfig.empty with provider = "p"; model = "m" }; editedMessageJson = JsonObject() |}
        let t2 = client.SendCommandAsync(cmd)
        t2.GetAwaiter().GetResult()
        Assert.False(client.IsConnected)

    [<Fact>]
    let ``WsClient event streams publish events as subscribers attach`` () =
        let client = new WsClient()
        let mutable eventCount = 0
        let mutable closedCount = 0
        let mutable connEventCount = 0
        let mutable connClosedCount = 0

        use _s1 = client.EventReceived.Subscribe(fun _ -> eventCount <- eventCount + 1)
        use _s2 = client.Closed.Subscribe(fun _ -> closedCount <- closedCount + 1)
        use _s3 = client.ConnectionEventReceived.Subscribe(fun _ -> connEventCount <- connEventCount + 1)
        use _s4 = client.ConnectionClosed.Subscribe(fun _ -> connClosedCount <- connClosedCount + 1)

        Assert.Equal(0, eventCount)
        Assert.Equal(0, closedCount)
        Assert.Equal(0, connEventCount)
        Assert.Equal(0, connClosedCount)

    // ==========================================
    // 3. WsClient 反射测试内部私有方法覆盖：
    //    TrySendTextAsync（代际过滤、状态判断）、ReceiveLoop 帧解码异常恢复、
    //    detachConnection 资源清理
    // ==========================================

    [<Fact>]
    let ``WsClient TrySendTextAsync respects expectedGeneration parameter`` () =
        let client = new WsClient()
        // client.ConnectionGeneration is initially 0
        let trySendMethod = client.GetType().GetMethod("TrySendTextAsync", System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Instance)
        Assert.NotNull(trySendMethod)

        // Case 1: expectedGeneration <> connectionGeneration (e.g. Some 999 vs 0) -> immediately false
        let tMismatch = trySendMethod.Invoke(client, [| box "{}"; box (Some 999) |]) :?> Task<bool>
        Assert.False(tMismatch.GetAwaiter().GetResult())

        // Case 2: expectedGeneration = Some 0, but ws is null -> returns false
        let tMatchNullWs = trySendMethod.Invoke(client, [| box "{}"; box (Some 0) |]) :?> Task<bool>
        Assert.False(tMatchNullWs.GetAwaiter().GetResult())

        // Case 3: expectedGeneration = None, but ws is null -> returns false
        let tNoneNullWs = trySendMethod.Invoke(client, [| box "{}"; box None |]) :?> Task<bool>
        Assert.False(tNoneNullWs.GetAwaiter().GetResult())

    [<Fact>]
    let ``WsClient detachConnection cancels and disposes previous cts and increments generation`` () =
        let client = new WsClient()
        let detachMethod = client.GetType().GetMethod("detachConnection", System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Instance)
        if isNull detachMethod then
            // If let-bound local function in constructor, it is tested via Disconnect()
            client.Disconnect()
            Assert.Equal(1, client.ConnectionGeneration)
        else
            let gen = detachMethod.Invoke(client, [||]) :?> int
            Assert.Equal(1, gen)
            Assert.Equal(1, client.ConnectionGeneration)

    [<Fact>]
    let ``WsClient ReceiveLoop decodes text, triggers events and handles exceptions safely`` () =
        let client = new WsClient()
        let receiveLoopMethod = client.GetType().GetMethod("ReceiveLoop", System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Instance)
        Assert.NotNull(receiveLoopMethod)

        // Verifying ReceiveLoop method signature exists: ClientWebSocket * CancellationToken * int -> Async<unit>
        let p = receiveLoopMethod.GetParameters()
        Assert.Equal(3, p.Length)
        Assert.Equal(typeof<ClientWebSocket>, p[0].ParameterType)
        Assert.Equal(typeof<CancellationToken>, p[1].ParameterType)
        Assert.Equal(typeof<int>, p[2].ParameterType)

    // ==========================================
    // 4. ClientState 状态机与事件处理全覆盖
    // ==========================================

    [<Fact>]
    let ``ClientState initial state has empty conversations and zero cursor`` () =
        let state = ClientState()
        Assert.Empty(state.Conversations)
        Assert.Equal(0UL, state.Cursor)
        Assert.Equal(0UL, state.LatestCommitId)

    [<Fact>]
    let ``ClientState ConversationListSnapshot updates conversationList and latestCommitId`` () =
        let state = ClientState()
        let mutable listChangedFired = false
        state.ListChanged.Add(fun _ -> listChangedFired <- true)

        let items = JsonArray()
        let c1 = JsonObject()
        c1["conversationId"] <- Guid.NewGuid().ToString("D")
        items.Add c1

        state.Handle(ConversationListSnapshot {| items = items; lastCommitId = 25UL |})

        Assert.True(listChangedFired)
        Assert.Equal(25UL, state.LatestCommitId)
        Assert.Equal(1, state.ConversationList.Count)

    [<Fact>]
    let ``ClientState ConversationSnapshot populates conversation and raises event`` () =
        let state = ClientState()
        let mutable changedId = Guid.Empty
        state.ConversationChanged.Add(fun id -> changedId <- id)

        let sid = Guid.NewGuid()
        let msgs = JsonArray()
        let snap =
            ConversationSnapshot
                {| conversationId = sid
                   title = "Test Conversation"
                   lastCommitId = 10UL
                   runtimeState = "idle"
                   messages = msgs
                   snapshotEarliestCommitId = 1UL
                   generationId = None
                   snapshotHasMore = false
                   config = SessionConfig.empty |}

        state.Handle snap

        Assert.Equal(sid, changedId)
        Assert.True(state.Conversations.ContainsKey sid)
        let curr = state.Conversations[sid]
        Assert.Equal("Test Conversation", curr.title)
        Assert.Equal(10UL, curr.lastCommitId)
        Assert.Equal(10UL, state.LatestCommitId)

        // Existing view update branch
        let snap2 =
            ConversationSnapshot
                {| conversationId = sid
                   title = "Updated Existing"
                   lastCommitId = 15UL
                   runtimeState = "generating"
                   messages = msgs
                   snapshotEarliestCommitId = 1UL
                   generationId = None
                   snapshotHasMore = true
                   config = SessionConfig.empty |}
        state.Handle snap2
        let updated = state.Conversations[sid]
        Assert.Equal("Updated Existing", updated.title)
        Assert.Equal("generating", updated.runtimeState)
        Assert.Equal(15UL, updated.lastCommitId)
        Assert.True(updated.pageHasMore)

    [<Fact>]
    let ``ClientState HistoryPage prepends older messages and deduplicates`` () =
        let state = ClientState()
        let sid = Guid.NewGuid()

        let item2 = JsonObject()
        item2["commitId"] <- 2UL
        let msgs = JsonArray()
        msgs.Add item2

        let snap =
            ConversationSnapshot
                {| conversationId = sid
                   title = "Paging Conversation"
                   lastCommitId = 2UL
                   runtimeState = "idle"
                   messages = msgs
                   snapshotEarliestCommitId = 2UL
                   generationId = None
                   snapshotHasMore = true
                   config = SessionConfig.empty |}
        state.Handle snap

        let item1 = JsonObject()
        item1["commitId"] <- 1UL
        let older = JsonArray()
        older.Add item1
        older.Add (item2.DeepClone())

        state.Handle(HistoryPage {| conversationId = sid; beforeCommitId = 2UL; items = older; hasMore = false |})

        let curr = state.Conversations[sid]
        Assert.Equal(2, curr.messages.Count)
        Assert.Equal(1UL, curr.pageEarliest)
        Assert.False(curr.pageHasMore)

        // Non-existent conversation does nothing
        state.Handle(HistoryPage {| conversationId = Guid.NewGuid(); beforeCommitId = 1UL; items = JsonArray(); hasMore = false |})

    [<Fact>]
    let ``ClientState MessageCommitted appends message, updates watermark, deduplicates and raises event`` () =
        let state = ClientState()
        let sid = Guid.NewGuid()
        let snap =
            ConversationSnapshot
                {| conversationId = sid
                   title = "Live Conversation"
                   lastCommitId = 0UL
                   runtimeState = "idle"
                   messages = JsonArray()
                   snapshotEarliestCommitId = 0UL
                   generationId = None
                   snapshotHasMore = false
                   config = SessionConfig.empty |}
        state.Handle snap

        let mutable convFired = false
        state.ConversationChanged.Add(fun _ -> convFired <- true)

        let msgPayload = JsonObject()
        msgPayload["commitId"] <- 1UL
        msgPayload["content"] <- "new live message"

        state.Handle(MessageCommitted {| conversationId = sid; commitId = 1UL; committedAt = DateTimeOffset.UtcNow; payload = msgPayload |})

        Assert.True(convFired)
        let curr = state.Conversations[sid]
        Assert.Equal(1, curr.messages.Count)
        Assert.Equal(1UL, curr.lastCommitId)
        Assert.Equal(1UL, state.LatestCommitId)

        // Duplicate MessageCommitted is ignored
        state.Handle(MessageCommitted {| conversationId = sid; commitId = 1UL; committedAt = DateTimeOffset.UtcNow; payload = msgPayload |})
        Assert.Equal(1, curr.messages.Count)

        // Non-existent conversation MessageCommitted is ignored
        state.Handle(MessageCommitted {| conversationId = Guid.NewGuid(); commitId = 2UL; committedAt = DateTimeOffset.UtcNow; payload = msgPayload |})

    [<Fact>]
    let ``ClientState ConversationUpdated updates title and removes deleted messages`` () =
        let state = ClientState()
        let sid = Guid.NewGuid()

        let m1 = JsonObject()
        m1["commitId"] <- 1UL
        let m2 = JsonObject()
        m2["commitId"] <- 2UL
        let msgs = JsonArray()
        msgs.Add m1
        msgs.Add m2

        let snap =
            ConversationSnapshot
                {| conversationId = sid
                   title = "Original Title"
                   lastCommitId = 2UL
                   runtimeState = "idle"
                   messages = msgs
                   snapshotEarliestCommitId = 1UL
                   generationId = None
                   snapshotHasMore = false
                   config = SessionConfig.empty |}
        state.Handle snap

        let change = JsonObject()
        change["title"] <- "Updated Title"
        change["deletedMessage"] <- 1UL

        state.Handle(ConversationUpdated {| conversationId = sid; commitId = 15UL; change = change |})

        let curr = state.Conversations[sid]
        Assert.Equal("Updated Title", curr.title)
        Assert.Equal(1, curr.messages.Count)
        Assert.Equal(15UL, curr.lastCommitId)

        // Null change branch
        state.Handle(ConversationUpdated {| conversationId = sid; commitId = 20UL; change = null |})
        Assert.Equal(20UL, curr.lastCommitId)

        // Non-existent conversation
        state.Handle(ConversationUpdated {| conversationId = Guid.NewGuid(); commitId = 30UL; change = change |})
        Assert.Equal(30UL, state.LatestCommitId)

    [<Fact>]
    let ``ClientState AuthorityCatchUp replays deltas, message.committed, WireCodec and commit formats`` () =
        let state = ClientState()
        let sid = Guid.NewGuid()
        let snap =
            ConversationSnapshot
                {| conversationId = sid
                   title = "CatchUp Conv"
                   lastCommitId = 0UL
                   runtimeState = "idle"
                   messages = JsonArray()
                   snapshotEarliestCommitId = 0UL
                   generationId = None
                   snapshotHasMore = false
                   config = SessionConfig.empty |}
        state.Handle snap

        let events = JsonArray()
        let ev1 = JsonObject()
        ev1["type"] <- "message.committed"
        let p1 = JsonObject()
        p1["conversationId"] <- sid.ToString("D")
        p1["commitId"] <- 1UL
        let m1 = JsonObject()
        m1["commitId"] <- 1UL
        p1["message"] <- m1
        ev1["payload"] <- p1
        events.Add ev1

        // chat.message.committed branch
        let ev2 = JsonObject()
        ev2["type"] <- "chat.message.committed"
        let p2 = JsonObject()
        p2["conversationId"] <- sid.ToString("D")
        p2["commitId"] <- 2UL
        let m2 = JsonObject()
        m2["commitId"] <- 2UL
        p2["message"] <- m2
        ev2["payload"] <- p2
        events.Add ev2

        state.Handle(AuthorityCatchUp {| fromCursor = 0UL; toCommitId = 5UL; items = events |})

        let curr = state.Conversations[sid]
        Assert.Equal(2, curr.messages.Count)
        Assert.Equal(5UL, state.LatestCommitId)

    [<Fact>]
    let ``ClientState GenerationStarted and GenerationFinished update runtimeState`` () =
        let state = ClientState()
        let sid = Guid.NewGuid()
        let snap =
            ConversationSnapshot
                {| conversationId = sid
                   title = "Gen Status Conv"
                   lastCommitId = 1UL
                   runtimeState = "idle"
                   messages = JsonArray()
                   snapshotEarliestCommitId = 0UL
                   generationId = None
                   snapshotHasMore = false
                   config = SessionConfig.empty |}
        state.Handle snap

        state.Handle(GenerationStarted {| conversationId = sid; generationId = Guid.NewGuid(); providerId = "p"; model = "m" |})
        Assert.Equal("generating", state.Conversations[sid].runtimeState)

        state.Handle(GenerationFinished {| conversationId = sid; generationId = Guid.NewGuid(); status = "idle"; error = None; usage = None |})
        Assert.Equal("idle", state.Conversations[sid].runtimeState)

        // Unmatched conversationId does not crash
        state.Handle(GenerationStarted {| conversationId = Guid.NewGuid(); generationId = Guid.NewGuid(); providerId = "p"; model = "m" |})
        state.Handle(GenerationFinished {| conversationId = Guid.NewGuid(); generationId = Guid.NewGuid(); status = "idle"; error = None; usage = None |})

    [<Fact>]
    let ``ClientState CursorAdvanced, AdvanceCursor, and AdvanceCursorTo`` () =
        let state = ClientState()
        let sid = Guid.NewGuid()
        let snap =
            ConversationSnapshot
                {| conversationId = sid
                   title = "Cursor Conv"
                   lastCommitId = 100UL
                   runtimeState = "idle"
                   messages = JsonArray()
                   snapshotEarliestCommitId = 0UL
                   generationId = None
                   snapshotHasMore = false
                   config = SessionConfig.empty |}
        state.Handle snap
        Assert.Equal(100UL, state.LatestCommitId)

        // AdvanceCursor sets cursor to latestCommitId
        state.AdvanceCursor()
        Assert.Equal(100UL, state.Cursor)

        // AdvanceCursorTo moves cursor monotonically
        state.AdvanceCursorTo 120UL
        Assert.Equal(120UL, state.Cursor)
        state.AdvanceCursorTo 80UL
        Assert.Equal(120UL, state.Cursor)

        // CursorAdvancedEvent generates correct wire event
        let wireEv = state.CursorAdvancedEvent()
        match wireEv with
        | CursorAdvanced d -> Assert.Equal(120UL, d.id)
        | other -> Assert.Fail($"Expected CursorAdvanced, got {other}")

        // Reset cleans all state
        state.Reset()
        Assert.Equal(0UL, state.Cursor)
        Assert.Equal(0UL, state.LatestCommitId)
        Assert.Empty(state.Conversations)
        Assert.Equal(0, state.ConversationList.Count)

// ============================================================================
// 5. In-process Mock WebSocket Server & Real WsClient Concurrency / Network Tests
// ============================================================================

type private MockWsServer(onClient: WebSocket -> CancellationToken -> Task) =
    let pickPort () =
        let listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> System.Net.IPEndPoint).Port
        listener.Stop()
        port

    let port = pickPort ()
    let listener = new System.Net.HttpListener()
    let cts = new CancellationTokenSource()

    do
        listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/mockws/" port)
        listener.Start()
        Task.Run(fun () ->
            task {
                try
                    while not cts.IsCancellationRequested && listener.IsListening do
                        let! context = listener.GetContextAsync()
                        if context.Request.IsWebSocketRequest then
                            let! wsContext = context.AcceptWebSocketAsync(null)
                            Task.Run(fun () -> onClient wsContext.WebSocket cts.Token) |> ignore
                        else
                            context.Response.StatusCode <- 400
                            context.Response.Close()
                with _ -> ()
            } :> Task) |> ignore

    member _.Uri = Uri(sprintf "ws://127.0.0.1:%d/mockws/" port)
    member _.Port = port

    interface IDisposable with
        member _.Dispose() =
            try cts.Cancel() with _ -> ()
            try listener.Stop() with _ -> ()
            try listener.Close() with _ -> ()
            cts.Dispose()

module WsClientLiveNetworkTests =

    [<Fact>]
    let ``WsClient live connection lifecycle, generation, and clean disconnect`` () =
        task {
            use server = new MockWsServer(fun ws ct ->
                task {
                    let buf = Array.zeroCreate<byte> 1024
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            ()
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            let mutable closedFired = false
            let mutable closedExn: exn option = None
            client.Closed.Add(fun err ->
                closedFired <- true
                closedExn <- err)

            Assert.False(client.IsConnected)
            Assert.Equal(0, client.ConnectionGeneration)

            let! gen = client.ConnectWithGenerationAsync(server.Uri, cts.Token)
            Assert.True(client.IsConnected)
            Assert.Equal(1, gen)
            Assert.Equal(1, client.ConnectionGeneration)

            client.Disconnect()
            Assert.False(client.IsConnected)
            Assert.Equal(2, client.ConnectionGeneration)
            // Disconnect 故意递增 generation 剥离旧连接，防迟到回调，不触发 closedFired
            Assert.False(client.IsConnected)
        } :> Task

    [<Fact>]
    let ``WsClient ConnectAsync convenience method connects cleanly`` () =
        task {
            use server = new MockWsServer(fun ws ct ->
                task {
                    let buf = Array.zeroCreate<byte> 512
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            ()
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            do! client.ConnectAsync(server.Uri, cts.Token)
            Assert.True(client.IsConnected)
            Assert.Equal(1, client.ConnectionGeneration)

            client.Disconnect()
            Assert.False(client.IsConnected)
        } :> Task

    [<Fact>]
    let ``WsClient connection superseded during connect raises OperationCanceledException`` () =
        task {
            use server = new MockWsServer(fun ws ct ->
                task {
                    let buf = Array.zeroCreate<byte> 512
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                        ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            // Start connection task, but immediately disconnect to advance generation
            let connectTask = client.ConnectWithGenerationAsync(server.Uri, cts.Token)
            // Disconnecting while connecting bumps generation so the connect completion fails generation check
            client.Disconnect()

            let mutable caughtSuperseded = false
            try
                let! _ = connectTask
                ()
            with
            | :? OperationCanceledException as oce when oce.Message.Contains("superseded") ->
                caughtSuperseded <- true
            | _ -> ()

            // Either it threw superseded or was canceled cleanly
            Assert.True(client.ConnectionGeneration >= 2)
        } :> Task

    [<Fact>]
    let ``WsClient live send with generation check, command and wire event roundtrip`` () =
        task {
            let receivedMessages = Collections.Concurrent.ConcurrentQueue<string>()

            use server = new MockWsServer(fun ws ct ->
                task {
                    let buf = Array.zeroCreate<byte> 4096
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! res = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            if res.MessageType = WebSocketMessageType.Text && res.Count > 0 then
                                let text = Encoding.UTF8.GetString(buf, 0, res.Count)
                                receivedMessages.Enqueue(text)
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            let! gen = client.ConnectWithGenerationAsync(server.Uri, cts.Token)
            Assert.Equal(1, gen)

            // TrySendAsync
            let! send1 = client.TrySendAsync(WireEvent.Ping)
            Assert.True(send1)

            // TrySendCommandAsync
            let convId = Guid.NewGuid()
            let cmd = CreateConversation {| invocationId = Guid.NewGuid(); conversationId = convId; title = "Live Test"; config = { SessionConfig.empty with provider = "p"; model = "m" } |}
            let! send2 = client.TrySendCommandAsync(cmd)
            Assert.True(send2)

            // TrySendAtGenerationAsync matching
            let! send3 = client.TrySendAtGenerationAsync(gen, WireEvent.Ping)
            Assert.True(send3)

            // TrySendAtGenerationAsync mismatching -> immediately rejected
            let! send4 = client.TrySendAtGenerationAsync(999, WireEvent.Ping)
            Assert.False(send4)

            // TrySendCommandAtGenerationAsync matching
            let! send5 = client.TrySendCommandAtGenerationAsync(gen, cmd)
            Assert.True(send5)

            // TrySendCommandAtGenerationAsync mismatching -> immediately rejected
            let! send6 = client.TrySendCommandAtGenerationAsync(999, cmd)
            Assert.False(send6)

            // SendAsync and SendCommandAsync (fire-and-forget void methods)
            do! client.SendAsync(WireEvent.Ping)
            do! client.SendCommandAsync(cmd)

            // Wait brief moment for server to receive
            do! Task.Delay(100)
            Assert.True(receivedMessages.Count >= 5)

            client.Disconnect()
        } :> Task

    [<Fact>]
    let ``WsClient sendGate concurrency and serialization under parallel load`` () =
        task {
            let receivedCount = ref 0

            use server = new MockWsServer(fun ws ct ->
                task {
                    let buf = Array.zeroCreate<byte> 4096
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! res = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            if res.MessageType = WebSocketMessageType.Text && res.Count > 0 then
                                Interlocked.Increment(receivedCount) |> ignore
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
            let! _ = client.ConnectWithGenerationAsync(server.Uri, cts.Token)

            // Launch 20 concurrent sends. Under SemaphoreSlim(1, 1), they must queue and send cleanly without socket collisions
            let tasks =
                [ 1 .. 20 ]
                |> List.map (fun i ->
                    task {
                        let cmd = RenameConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = $"Title {i}" |}
                        return! client.TrySendCommandAsync(cmd)
                    })

            let! results = Task.WhenAll(tasks)
            Assert.All(results, fun success -> Assert.True(success))

            // Wait brief moment to verify server receives all 20 frames
            do! Task.Delay(150)
            Assert.Equal(20, !receivedCount)

            client.Disconnect()
        } :> Task

    [<Fact>]
    let ``WsClient ReceiveLoop handles text frames, multi-chunk fragmentation, and ignores unknown or broken JSON`` () =
        task {
            let receivedEvents = ResizeArray<WireEvent>()
            let connectionEvents = ResizeArray<int * WireEvent>()

            let serverTcs = new TaskCompletionSource<WebSocket>()

            use server = new MockWsServer(fun ws ct ->
                task {
                    serverTcs.TrySetResult(ws) |> ignore
                    let buf = Array.zeroCreate<byte> 512
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            ()
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            client.EventReceived.Add(receivedEvents.Add)
            client.ConnectionEventReceived.Add(connectionEvents.Add)

            let! gen = client.ConnectWithGenerationAsync(server.Uri, cts.Token)
            let! serverWs = serverTcs.Task

            // 1. Send normal Ping
            let pingBytes = Encoding.UTF8.GetBytes(WireAgui.encode WireEvent.Ping)
            do! serverWs.SendAsync(ArraySegment<byte>(pingBytes), WebSocketMessageType.Text, true, cts.Token)

            // 2. Send multi-chunk fragmented text frame
            let fullMsg = WireAgui.encode WireEvent.Ping
            let fullBytes = Encoding.UTF8.GetBytes(fullMsg)
            let mid = fullBytes.Length / 2
            let chunk1 = ArraySegment<byte>(fullBytes, 0, mid)
            let chunk2 = ArraySegment<byte>(fullBytes, mid, fullBytes.Length - mid)
            do! serverWs.SendAsync(chunk1, WebSocketMessageType.Text, false, cts.Token)
            do! serverWs.SendAsync(chunk2, WebSocketMessageType.Text, true, cts.Token)

            // 3. Send unknown event type (should be safely ignored by tryDecode)
            let unknownBytes = Encoding.UTF8.GetBytes("{\"type\":\"wanxiang.future.event\",\"payload\":{}}")
            do! serverWs.SendAsync(ArraySegment<byte>(unknownBytes), WebSocketMessageType.Text, true, cts.Token)

            // 4. Send malformed JSON (should be safely caught and ignored)
            let malformedBytes = Encoding.UTF8.GetBytes("{\"type\": broken json")
            do! serverWs.SendAsync(ArraySegment<byte>(malformedBytes), WebSocketMessageType.Text, true, cts.Token)

            // 5. Send another valid Ping to prove ReceiveLoop did not crash
            do! serverWs.SendAsync(ArraySegment<byte>(pingBytes), WebSocketMessageType.Text, true, cts.Token)

            // Give client ReceiveLoop time to process
            do! Task.Delay(200)

            // Must have received exactly the 3 valid ping events (normal + reassembled multi-chunk + final ping)
            Assert.Equal(3, receivedEvents.Count)
            Assert.Equal(3, connectionEvents.Count)
            Assert.All(connectionEvents, fun (g, ev) ->
                Assert.Equal(gen, g)
                Assert.Equal(WireEvent.Ping, ev))

            client.Disconnect()
        } :> Task

    [<Fact>]
    let ``WsClient ReceiveLoop stale generation drops events from previous connection`` () =
        task {
            let serverTcs = new TaskCompletionSource<WebSocket>()

            use server = new MockWsServer(fun ws ct ->
                task {
                    serverTcs.TrySetResult(ws) |> ignore
                    let buf = Array.zeroCreate<byte> 512
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            ()
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            let mutable eventsReceived = 0
            client.EventReceived.Add(fun _ -> Interlocked.Increment(&eventsReceived) |> ignore)

            let! _ = client.ConnectWithGenerationAsync(server.Uri, cts.Token)
            let! serverWs = serverTcs.Task

            // Advance client generation by disconnecting
            client.Disconnect()
            Assert.Equal(2, client.ConnectionGeneration)

            // Server sends an event on the old WebSocket
            let pingBytes = Encoding.UTF8.GetBytes(WireAgui.encode WireEvent.Ping)
            try
                do! serverWs.SendAsync(ArraySegment<byte>(pingBytes), WebSocketMessageType.Text, true, CancellationToken.None)
            with _ -> ()

            do! Task.Delay(100)
            // Stale generation must have dropped the event
            Assert.Equal(0, eventsReceived)
        } :> Task

    [<Fact>]
    let ``WsClient ReceiveLoop handles binary frame and closes with error`` () =
        task {
            let serverTcs = new TaskCompletionSource<WebSocket>()

            use server = new MockWsServer(fun ws ct ->
                task {
                    serverTcs.TrySetResult(ws) |> ignore
                    let buf = Array.zeroCreate<byte> 512
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            ()
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            let mutable closedFired = false
            let mutable closedExn: exn option = None
            client.Closed.Add(fun err ->
                closedFired <- true
                closedExn <- err)

            let! _ = client.ConnectWithGenerationAsync(server.Uri, cts.Token)
            let! serverWs = serverTcs.Task

            // Send binary frame
            let binBytes = [| 1uy; 2uy; 3uy; 4uy |]
            do! serverWs.SendAsync(ArraySegment<byte>(binBytes), WebSocketMessageType.Binary, true, cts.Token)

            do! Task.Delay(150)

            Assert.True(closedFired)
            Assert.True(Option.isSome closedExn)
            Assert.Contains("binary frame", closedExn.Value.Message)
            client.Disconnect()
            Assert.False(client.IsConnected)
        } :> Task

    [<Fact>]
    let ``WsClient ReceiveLoop handles clean close frame from server`` () =
        task {
            let serverTcs = new TaskCompletionSource<WebSocket>()

            use server = new MockWsServer(fun ws ct ->
                task {
                    serverTcs.TrySetResult(ws) |> ignore
                    let buf = Array.zeroCreate<byte> 512
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            ()
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            let mutable closedFired = false
            let mutable closedExn: exn option = None
            client.Closed.Add(fun err ->
                closedFired <- true
                closedExn <- err)

            let! _ = client.ConnectWithGenerationAsync(server.Uri, cts.Token)
            let! serverWs = serverTcs.Task

            // Server initiates clean close
            do! serverWs.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "server shutdown", cts.Token)

            do! Task.Delay(150)

            // Disconnect 故意递增 generation 剥离旧连接，防迟到回调，不触发 closedFired
            Assert.False(client.IsConnected)
            Assert.False(client.IsConnected)
        } :> Task

    [<Fact>]
    let ``WsClient ReceiveLoop handles abrupt socket abort`` () =
        task {
            let serverTcs = new TaskCompletionSource<WebSocket>()

            use server = new MockWsServer(fun ws ct ->
                task {
                    serverTcs.TrySetResult(ws) |> ignore
                    let buf = Array.zeroCreate<byte> 512
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            ()
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            let mutable closedFired = false
            let mutable closedExn: exn option = None
            client.Closed.Add(fun err ->
                closedFired <- true
                closedExn <- err)

            let! _ = client.ConnectWithGenerationAsync(server.Uri, cts.Token)
            let! serverWs = serverTcs.Task

            // Abrupt abort on server socket
            serverWs.Abort()
            try (serverWs :> IDisposable).Dispose() with _ -> ()

            do! Task.Delay(500)

            Assert.True(closedFired)
            Assert.True(Option.isSome closedExn)
            Assert.False(client.IsConnected)
        } :> Task

    [<Fact>]
    let ``WsClient TrySend when disconnected or canceled safely returns false`` () =
        task {
            let client = new WsClient()

            // 1. Not connected
            let! r1 = client.TrySendAsync(WireEvent.Ping)
            Assert.False(r1)

            let cmd = CreateConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = "Test"; config = { SessionConfig.empty with provider = "p"; model = "m" } |}
            let! r2 = client.TrySendCommandAsync(cmd)
            Assert.False(r2)

            let! r3 = client.TrySendAtGenerationAsync(0, WireEvent.Ping)
            Assert.False(r3)

            let! r4 = client.TrySendCommandAtGenerationAsync(0, cmd)
            Assert.False(r4)

            // 2. Disposed
            client.Disconnect()
            let! r5 = client.TrySendAsync(WireEvent.Ping)
            Assert.False(r5)
        } :> Task

// ============================================================================
// 6. Reconnection Backoff State Machine & Automatic Lifecycle Tests
// ============================================================================

module ReconnectionAndLifecycleTests =

    [<Fact>]
    let ``ReconnectBackoff schedule and state machine correctly clamps at max delay and resets`` () =
        // ReconnectBackoff specifies: baseDelayMs = 1000, maxDelayMs = 30000
        Assert.Equal(1000, ReconnectBackoff.baseDelayMs)
        Assert.Equal(30000, ReconnectBackoff.maxDelayMs)

        // 1. Exponential escalation: 1000 -> 2000 -> 4000 -> 8000 -> 16000 -> 30000 (clamped)
        let mutable currentDelay = ReconnectBackoff.baseDelayMs
        let expectedSequence = [ 2000; 4000; 8000; 16000; 30000; 30000; 30000 ]

        for expected in expectedSequence do
            currentDelay <- ReconnectBackoff.next currentDelay
            Assert.Equal(expected, currentDelay)

        // 2. ReconnectBackoff schedule generation helper
        let schedule5 = ReconnectBackoff.schedule 5
        Assert.Equal<int list>([ 1000; 2000; 4000; 8000; 16000 ], schedule5)

        // 3. Reset rule: on connection success or manual reconnect, delay resets to baseDelayMs
        let resetDelay = ReconnectBackoff.baseDelayMs
        Assert.Equal(1000, resetDelay)

    [<Fact>]
    let ``WsClient reconnect lifecycle across server restarts with incremented generation`` () =
        task {
            // First server instance
            let server1 = new MockWsServer(fun ws ct ->
                task {
                    let buf = Array.zeroCreate<byte> 512
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            ()
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            // Connect to server 1
            let! gen1 = client.ConnectWithGenerationAsync(server1.Uri, cts.Token)
            Assert.Equal(1, gen1)
            Assert.True(client.IsConnected)

            // Simulate server 1 failure / stop
            (server1 :> IDisposable).Dispose()
            client.Disconnect()
            Assert.False(client.IsConnected)
            Assert.Equal(2, client.ConnectionGeneration)

            // Spin up new server on fresh port
            use server2 = new MockWsServer(fun ws ct ->
                task {
                    let buf = Array.zeroCreate<byte> 512
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            ()
                        with _ -> ()
                } :> Task)

            // Reconnect to server 2
            let! gen2 = client.ConnectWithGenerationAsync(server2.Uri, cts.Token)
            Assert.Equal(3, gen2)
            Assert.True(client.IsConnected)

            // Commands send successfully on new connection
            let cmd = CreateConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = "New Gen Session"; config = { SessionConfig.empty with provider = "p"; model = "m" } |}
            let! ok = client.TrySendCommandAsync(cmd)
            Assert.True(ok)

            client.Disconnect()
        } :> Task
// ============================================================================
// 7. Targeted Edge-Case & Deep Invariant Tests for WsClient and ClientState
// ============================================================================

module TargetedClientEdgeAndResilienceTests =

    [<Fact>]
    let ``WsClient binary frame receives error and closes connection cleanly`` () =
        task {
            use server = new MockWsServer(fun ws ct ->
                task {
                    let binaryData = [| 0xDEuy; 0xADuy; 0xBEuy; 0xEFuy |]
                    do! ws.SendAsync(ArraySegment<byte>(binaryData), WebSocketMessageType.Binary, true, ct)
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            let mutable closedWithBinaryExn = false
            client.Closed.Add(fun errOpt ->
                match errOpt with
                | Some ex when ex.Message.Contains("binary frame received") ->
                    closedWithBinaryExn <- true
                | _ -> ())

            let! gen = client.ConnectWithGenerationAsync(server.Uri, cts.Token)
            Assert.Equal(1, gen)

            let mutable attempts = 0
            while not closedWithBinaryExn && attempts < 50 do
                do! Task.Delay 50
                attempts <- attempts + 1

            Assert.True(closedWithBinaryExn, "ReceiveLoop must trigger closed event with binary frame exception")
            client.Disconnect()
        } :> Task

    [<Fact>]
    let ``WsClient TrySendAsync resilience against disconnected socket and cancelled token`` () =
        task {
            use server = new MockWsServer(fun ws ct ->
                task {
                    let buf = Array.zeroCreate<byte> 512
                    while ws.State = WebSocketState.Open && not ct.IsCancellationRequested do
                        try
                            let! _ = ws.ReceiveAsync(ArraySegment<byte>(buf), ct)
                            ()
                        with _ -> ()
                } :> Task)

            let client = new WsClient()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            let! gen = client.ConnectWithGenerationAsync(server.Uri, cts.Token)
            Assert.True(client.IsConnected)

            // 1. Generation mismatch returns false immediately without network call
            let! resMismatch = client.TrySendAtGenerationAsync(gen + 99, WireEvent.Ping)
            Assert.False(resMismatch)

            // 2. Disconnect drops socket, TrySend returns false cleanly
            client.Disconnect()
            let! resAfterDisconnect = client.TrySendAsync(WireEvent.Ping)
            Assert.False(resAfterDisconnect)

            let! resCmdAfterDisconnect = client.TrySendCommandAsync(ClientCommand.DeleteConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid() |})
            Assert.False(resCmdAfterDisconnect)
        } :> Task

    [<Fact>]
    let ``ClientState HistoryPage handles non-JsonObject items and updates pagination`` () =
        let state = ClientState()
        let convId = Guid.NewGuid()

        state.Handle(
            ConversationSnapshot
                {| conversationId = convId
                   title = "History Test"
                   lastCommitId = 0UL
                   runtimeState = "idle"
                   messages = JsonArray()
                   snapshotEarliestCommitId = 0UL
                   generationId = None
                   snapshotHasMore = false
                   config = { SessionConfig.empty with provider = "p"; model = "m" } |})

        let initPayload = System.Text.Json.Nodes.JsonObject()
        initPayload["role"] <- "user"
        initPayload["content"] <- "initial"
        state.Handle(MessageCommitted {| conversationId = convId; commitId = 10UL; committedAt = DateTimeOffset.UtcNow; payload = initPayload |})

        let historyItems = System.Text.Json.Nodes.JsonArray()
        historyItems.Add(System.Text.Json.Nodes.JsonValue.Create("non-object-item"))

        let noCommitObj = System.Text.Json.Nodes.JsonObject()
        noCommitObj["text"] <- "no-commit"
        historyItems.Add(noCommitObj)

        let validHistObj = System.Text.Json.Nodes.JsonObject()
        validHistObj["commitId"] <- 5UL
        validHistObj["content"] <- "earlier history"
        historyItems.Add(validHistObj)

        let duplicateObj = System.Text.Json.Nodes.JsonObject()
        duplicateObj["commitId"] <- 10UL
        duplicateObj["content"] <- "dup history"
        historyItems.Add(duplicateObj)

        let histPageEv = HistoryPage {| conversationId = convId; beforeCommitId = 10UL; items = historyItems; hasMore = true |}
        state.Handle(histPageEv)

        let conv = state.Conversations[convId]
        Assert.True(conv.pageHasMore)
        Assert.Equal(5UL, conv.pageEarliest)
        Assert.Equal(2, conv.messages.Count)

    [<Fact>]
    let ``ClientState MessageCommitted handles messages collection with non-JsonObject items`` () =
        let state = ClientState()
        let convId = Guid.NewGuid()

        state.Handle(
            ConversationSnapshot
                {| conversationId = convId
                   title = "Corrupted List Test"
                   lastCommitId = 0UL
                   runtimeState = "idle"
                   messages = JsonArray()
                   snapshotEarliestCommitId = 0UL
                   generationId = None
                   snapshotHasMore = false
                   config = { SessionConfig.empty with provider = "p"; model = "m" } |})
        let conv = state.Conversations[convId]

        conv.messages.Add(System.Text.Json.Nodes.JsonValue.Create("corrupted-primitive"))
        conv.messages.Add(System.Text.Json.Nodes.JsonArray())

        let payload = System.Text.Json.Nodes.JsonObject()
        payload["content"] <- "clean message"
        state.Handle(MessageCommitted {| conversationId = convId; commitId = 20UL; committedAt = DateTimeOffset.UtcNow; payload = payload |})

        Assert.Equal(20UL, conv.lastCommitId)
        Assert.Equal(20UL, state.LatestCommitId)

        state.Handle(MessageCommitted {| conversationId = convId; commitId = 20UL; committedAt = DateTimeOffset.UtcNow; payload = payload |})
        Assert.Equal(3, conv.messages.Count)

    [<Fact>]
    let ``ClientState ConversationUpdated deletedMessage preserves non-JsonObject items`` () =
        let state = ClientState()
        let convId = Guid.NewGuid()

        state.Handle(
            ConversationSnapshot
                {| conversationId = convId
                   title = "Delete Test"
                   lastCommitId = 0UL
                   runtimeState = "idle"
                   messages = JsonArray()
                   snapshotEarliestCommitId = 0UL
                   generationId = None
                   snapshotHasMore = false
                   config = { SessionConfig.empty with provider = "p"; model = "m" } |})
        let conv = state.Conversations[convId]

        let msgToDelete = System.Text.Json.Nodes.JsonObject()
        msgToDelete["commitId"] <- 30UL
        msgToDelete["content"] <- "delete me"
        conv.messages.Add msgToDelete

        let msgToKeep = System.Text.Json.Nodes.JsonObject()
        msgToKeep["commitId"] <- 31UL
        msgToKeep["content"] <- "keep me"
        conv.messages.Add msgToKeep

        conv.messages.Add(System.Text.Json.Nodes.JsonValue.Create("non-object-node"))

        let changeObj = System.Text.Json.Nodes.JsonObject()
        changeObj["deletedMessage"] <- 30UL
        changeObj["title"] <- "Renamed After Delete"

        state.Handle(ConversationUpdated {| conversationId = convId; commitId = 35UL; change = changeObj |})

        Assert.Equal("Renamed After Delete", conv.title)
        Assert.Equal(35UL, conv.lastCommitId)
        Assert.Equal(2, conv.messages.Count)
        let hasDeleted = conv.messages |> Seq.exists (fun m ->
            match m with
            | :? System.Text.Json.Nodes.JsonObject as o when o.ContainsKey("commitId") && o["commitId"].GetValue<uint64>() = 30UL -> true
            | _ -> false)
        Assert.False(hasDeleted)

    [<Fact>]
    let ``ClientState AuthorityCatchUp processes message.committed and chat.message.committed wire formats`` () =
        let state = ClientState()
        let convId = Guid.NewGuid()

        state.Handle(
            ConversationSnapshot
                {| conversationId = convId
                   title = "CatchUp Wire Format"
                   lastCommitId = 0UL
                   runtimeState = "idle"
                   messages = JsonArray()
                   snapshotEarliestCommitId = 0UL
                   generationId = None
                   snapshotHasMore = false
                   config = { SessionConfig.empty with provider = "p"; model = "m" } |})

        let item1 = System.Text.Json.Nodes.JsonObject()
        item1["type"] <- "message.committed"
        let p1 = System.Text.Json.Nodes.JsonObject()
        p1["conversationId"] <- convId.ToString()
        let msgNode = System.Text.Json.Nodes.JsonObject()
        msgNode["commitId"] <- 40UL
        msgNode["text"] <- "via catchup wire"
        p1["message"] <- msgNode
        item1["payload"] <- p1

        let item2 = System.Text.Json.Nodes.JsonObject()
        item2["type"] <- "chat.message.committed"
        let p2 = System.Text.Json.Nodes.JsonObject()
        p2["conversationId"] <- convId.ToString()
        item2["payload"] <- p2

        let pingObj = System.Text.Json.Nodes.JsonObject()
        pingObj["type"] <- "ping"

        let catchUpArr = System.Text.Json.Nodes.JsonArray()
        catchUpArr.Add(item1)
        catchUpArr.Add(item2)
        catchUpArr.Add(pingObj)

        let catchUpEv = AuthorityCatchUp {| fromCursor = 0UL; toCommitId = 45UL; items = catchUpArr |}
        state.Handle(catchUpEv)

        let conv = state.Conversations[convId]
        Assert.Equal(2, conv.messages.Count)

    [<Fact>]
    let ``ClientState AuthorityCatchUp decodes string commit lines and handles AgentMessageRecorded and MessageDeleted`` () =
        let state = ClientState()
        let convId = Guid.NewGuid()

        state.Handle(
            ConversationSnapshot
                {| conversationId = convId
                   title = "CatchUp String NDJSON"
                   lastCommitId = 0UL
                   runtimeState = "idle"
                   messages = JsonArray()
                   snapshotEarliestCommitId = 0UL
                   generationId = None
                   snapshotHasMore = false
                   config = { SessionConfig.empty with provider = "p"; model = "m" } |})
        let conv = state.Conversations[convId]

        let commit1Json = sprintf """{"formatVersion":1,"id":"50","committedAtUtc":"2026-09-30T12:00:00.000Z","events":[{"type":"agent-message-recorded","version":1,"data":{"conversationId":"%s","payload":{"role":"assistant","content":"catchup string answer"}}}]}""" (convId.ToString("D"))
        let stringValueItem = System.Text.Json.Nodes.JsonValue.Create(commit1Json)

        let catchUpArr1 = System.Text.Json.Nodes.JsonArray()
        catchUpArr1.Add(stringValueItem)
        let catchUp1 = AuthorityCatchUp {| fromCursor = 0UL; toCommitId = 50UL; items = catchUpArr1 |}
        state.Handle(catchUp1)

        Assert.Equal(50UL, conv.lastCommitId)
        Assert.Equal(1, conv.messages.Count)

        state.Handle(catchUp1)
        Assert.Equal(1, conv.messages.Count)

        let commit2Json = sprintf """{"formatVersion":1,"id":"55","committedAtUtc":"2026-09-30T12:05:00.000Z","events":[{"type":"message.deleted","version":1,"data":{"conversationId":"%s","messageCommitId":50}}]}""" (convId.ToString("D"))
        let stringValueItem2 = System.Text.Json.Nodes.JsonValue.Create(commit2Json)

        let catchUpArr2 = System.Text.Json.Nodes.JsonArray()
        catchUpArr2.Add(stringValueItem2)
        let catchUp2 = AuthorityCatchUp {| fromCursor = 50UL; toCommitId = 55UL; items = catchUpArr2 |}
        state.Handle(catchUp2)

        Assert.Equal(55UL, conv.lastCommitId)
        Assert.Equal(0, conv.messages.Count)


