module Wanxiang.Tests.ServerE2eTests

open System
open System.IO
open System.Net.WebSockets
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Store
open Wanxiang.Client
open Wanxiang.Tests.Helpers

/// 端到端：启动真实 ServerApp + ClientWebSocket。
/// 验证 P0-1 修复：观察快照 → cursor.advanced → 写命令不再被 stale-projection 拒绝。
module internal E2e =

    type ServerHandle(port: int, token: string, ?providerUrl: string, ?initialCommits: Events.Commit list) =
        let dir = tempDir ()
        let configPath = Path.Combine(dir, "config.toml")
        let mutable server: Wanxiang.Server.ServerApp option = None
        do
            let instanceId = Guid.CreateVersion7()
            let hash = ClientToken.ClientToken.hash token
            let cfg =
                { AppConfig.defaults instanceId with
                    listen = sprintf "127.0.0.1:%d" port
                    generation = { (AppConfig.defaults instanceId).generation with autoTitle = false }
                    providers =
                        Map.ofList
                            [ "test-provider",
                              { id = "test-provider"
                                kind = "openai"
                                label = "Test Provider"
                                baseUrl = defaultArg providerUrl "http://127.0.0.1:1/v1"
                                apiKey = None
                                models = [ "test-model" ]
                                defaultModel = "test-model"
                                timeoutSeconds = ProviderConfig.defaultTimeoutSeconds
                                maxRetries = 0
                                enabled = true
                                promptCaching = false
                                headers = Map.empty
                                extraJson = None; visionOverrides = Map.empty } ]
                    authClients =
                        [ { tokenHash = hash
                            name = "e2e-test"
                            createdAtUtc = DateTimeOffset.UtcNow
                            lastSeenUtc = None
                            revoked = false } ] }
            File.WriteAllText(configPath, TomlCodec.serialize cfg)
            match initialCommits with
            | Some commits ->
                DataPaths.ensureDataDirs dir
                // 种子也按 RFC 7464 记录写（RS + JSON + LF），与运行期写入器一致。
                let path = DataPaths.logFile dir
                let bytes =
                    commits
                    |> List.map (fun c -> Wanxiang.Store.Assembly.frame c)
                    |> Array.concat
                File.WriteAllBytes(path, bytes)
            | None -> ()
            let app = new Wanxiang.Server.ServerApp(dir, configPath, false, None, ignore)
            app.Start(false)
            server <- Some app
        member _.CommitCount =
            Directory.GetFiles(DataPaths.eventsDir dir, "*.jsonseq")
            |> Array.sumBy (fun path -> System.IO.File.ReadAllText(path).Split(char 0x1E) |> Array.filter (fun seg -> seg.Trim().Length > 0) |> Array.length)
        member _.Dispose() =
            match server with
            | Some s -> (s :> IDisposable).Dispose()
            | None -> ()
            cleanup dir


    /// 入站 AG-UI 事件归一化回 {type, payload} 外壳，供既有断言复用。
    /// 主机出站承载（SSOT 55）：
    /// - 标准事件（生成流）→ {type:"RUN_STARTED",...}（扁平）
    /// - 其余语义 → {type:"CUSTOM", name:"wanxiang.dev/<wireType>", value:{type,payload}}
    /// 这里统一折成 {type:<wireType>, payload:<payload>}；标准面事件按语义事件名折算。
    let normalizeAgui (node: JsonNode) : JsonObject =
        let o = node.AsObject()
        let wireType = o["type"].GetValue<string>()
        if wireType = "CUSTOM" then
            let name = o["name"].GetValue<string>()
            if name.StartsWith "wanxiang.dev/" then
                let inner = name.Substring "wanxiang.dev/".Length
                match o["value"] with
                | :? JsonObject as v when v.ContainsKey "payload" ->
                    // value 里存的就是原外壳
                    v.DeepClone() :?> JsonObject
                | :? JsonObject as v ->
                    // 扁平 value：直接作 payload
                    let wrapped = JsonObject()
                    wrapped["type"] <- inner
                    wrapped["payload"] <- v.DeepClone()
                    wrapped
                | _ ->
                    let wrapped = JsonObject()
                    wrapped["type"] <- inner
                    wrapped["payload"] <- (match o["value"] with null -> JsonObject() :> JsonNode | v -> v.DeepClone())
                    wrapped
            else
                o
        else
            // 标准面事件折成语义事件名，供场景断言复用：
            // RUN_STARTED→generation.started；RUN_FINISHED→generation.finished；
            // RUN_ERROR→generation.finished(failed)；TEXT_MESSAGE_*→generation.delta。
            match wireType with
            | "RUN_STARTED" ->
                let w = JsonObject()
                w["type"] <- "generation.started"
                let pl = JsonObject()
                pl["threadId"] <- o["threadId"].DeepClone()
                pl["runId"] <- o["runId"].DeepClone()
                pl["providerId"] <- ""
                pl["model"] <- ""
                w["payload"] <- pl
                w
            | "RUN_FINISHED" ->
                let w = JsonObject()
                w["type"] <- "generation.finished"
                let pl = JsonObject()
                pl["threadId"] <- o["threadId"].DeepClone()
                pl["runId"] <- o["runId"].DeepClone()
                // Outcome 是 {type:"..."} 对象（RunFinishedCancelledOutcome 等）
                let status =
                    match o["outcome"] with
                    | :? JsonObject as oc ->
                        match oc["type"] with
                        | :? JsonValue as v when v.TryGetValue<string>() |> fst -> v.GetValue<string>()
                        | _ -> "success"
                    | _ -> "success"
                // outcome 名 → status 名
                let wanxiangStatus =
                    match status with
                    | "cancelled" -> "cancelled"
                    | "interrupted" -> "cancelled"
                    | _ -> "completed"
                pl["status"] <- wanxiangStatus
                w["payload"] <- pl
                w
            | "RUN_ERROR" ->
                let w = JsonObject()
                w["type"] <- "generation.finished"
                let pl = JsonObject()
                pl["status"] <- "failed"
                pl["conversationId"] <- ""
                pl["generationId"] <- ""
                w["payload"] <- pl
                w
            | "TEXT_MESSAGE_CHUNK" ->
                let w = JsonObject()
                w["type"] <- "generation.delta"
                let pl = JsonObject()
                pl["text"] <- o["delta"].DeepClone()
                w["payload"] <- pl
                w
            | _ -> o

    let recvEvent (ws: ClientWebSocket) (ct: CancellationToken) : JsonObject =
        let buffer = Array.zeroCreate<byte> (1024 * 1024)
        let ms = new MemoryStream()
        let mutable finished = false
        while not finished do
            let result = ws.ReceiveAsync(ArraySegment<byte>(buffer), ct).GetAwaiter().GetResult()
            ms.Write(buffer, 0, result.Count)
            if result.EndOfMessage then finished <- true
        let text = Encoding.UTF8.GetString(ms.ToArray())
        (JsonNode.Parse(text) |> normalizeAgui)

    let send (ws: ClientWebSocket) (ev: JsonObject) (ct: CancellationToken) =
        // 手写 {type,payload} 事件统一折成 CUSTOM wanxiang.dev/<type> 承载（value = 原对象）。
        let carrier =
            let t = ev["type"].GetValue<string>()
            if t = "CUSTOM" then ev
            else
                let ce = JsonObject()
                ce["type"] <- "CUSTOM"
                ce["name"] <- sprintf "wanxiang.dev/%s" t
                ce["value"] <- ev.DeepClone()
                ce
        let bytes = Encoding.UTF8.GetBytes(carrier.ToJsonString())
        ws.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).GetAwaiter().GetResult()

    let waitFor (ws: ClientWebSocket) (ct: CancellationToken) (predicate: JsonObject -> bool) : JsonObject =
        let mutable ev = recvEvent ws ct
        let mutable attempts = 0
        while not (predicate ev) && attempts < 50 do
            ev <- recvEvent ws ct
            attempts <- attempts + 1
        ev

    let conn (port: int) (token: string) (ct: CancellationToken) : ClientWebSocket =
        let ws = new ClientWebSocket()
        ws.ConnectAsync(Uri(sprintf "ws://127.0.0.1:%d/ws" port), ct).GetAwaiter().GetResult()
        // 连接即认证（版本 in-band，SSOT 55.4）；{type,payload} 由 send() 折成 CUSTOM 承载。
        let auth = JsonObject()
        auth["type"] <- "auth.present"
        let ap = JsonObject()
        ap["token"] <- token
        auth["payload"] <- ap
        send ws auth ct
        ws

let internal pickPort () =
    let listener = new Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> Net.IPEndPoint).Port
    listener.Stop()
    port

[<Fact>]
let ``e2e full export is read-only and stable while another client deletes and renames`` () =
    let port, token, id = pickPort (), ClientToken.ClientToken.generate (), Guid.NewGuid()
    let now = DateTimeOffset.UtcNow
    let commits =
        Events.Commit.create 1UL now [ ConversationCreated { conversationId = id; title = "原始标题"; config = testConfig () } ]
        :: [ for n in 1 .. 450 -> Events.Commit.create (uint64 n + 1UL) now [ AgentMessageRecorded { conversationId = id; payloadJson = userMessageJson (sprintf "message-%04d" n) } ] ]
    let server = E2e.ServerHandle(port, token, initialCommits = commits)
    try
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 20.0)
        let ct = timeout.Token
        use reader = E2e.conn port token ct
        use writer = E2e.conn port token ct
        let wait ws name =
            let event = E2e.waitFor ws ct (fun ev -> ev["type"].GetValue<string>() = name)
            Assert.Equal(name, event["type"].GetValue<string>())
            event
        let send ws event = E2e.send ws (JsonNode.Parse(WireAgui.encode event).AsObject()) ct
        let command ws cmd = E2e.send ws (JsonNode.Parse(WireCodec.encodeCommand cmd).AsObject()) ct
        let value (event: JsonObject) (key: string) = event["payload"].AsObject()[key]
        wait reader "auth.accepted" |> ignore
        wait writer "auth.accepted" |> ignore
        let controller = Wanxiang.UI.ConversationExportController id
        let readPage query =
            send reader (ConversationExportRead query)
            let event = wait reader "conversation.export-page"
            match WireCodec.tryDecode(event.ToJsonString()) with
            | Ok (ConversationExportPage page) -> page
            | other -> failwithf "bad page: %A" other
        let first = readPage (controller.Start())
        Assert.Equal(451UL, first.atCommitId)
        Assert.Equal(450, first.totalMessages)
        let mutable next = controller.Accept first
        Assert.True next.IsSome
        send writer ObserveConversationList
        let list = wait writer "conversation-list.snapshot"
        send writer (CursorAdvanced {| id = (value list "lastCommitId").GetValue<uint64>() |})
        command writer (DeleteMessage {| invocationId = Guid.NewGuid(); conversationId = id; messageCommitId = 2UL |})
        let deleted = wait writer "command.committed"
        send writer (CursorAdvanced {| id = (value deleted "commitId").GetValue<uint64>() |})
        command writer (RenameConversation {| invocationId = Guid.NewGuid(); conversationId = id; title = "更新后的标题" |})
        wait writer "command.committed" |> ignore
        while next.IsSome do next <- controller.Accept(readPage next.Value)
        let document = controller.Document |> Option.get
        Assert.Equal("原始标题", document.title)
        Assert.Equal(450, document.messages.Length)
        Assert.Equal("message-0001", document.messages.Head.text)
        Assert.Equal("message-0450", (List.last document.messages).text)
        Assert.Equal(453, server.CommitCount)
        // 读者仅导出，既没 observe 也没推进 cursor，随后写入必须仍被 stale 检查挡住。
        command reader (RenameConversation {| invocationId = Guid.NewGuid(); conversationId = id; title = "不应生效" |})
        let rejected = wait reader "command.rejected"
        Assert.Equal("stale-projection", (value rejected "code").GetValue<string>())
        Assert.Equal(453, server.CommitCount)
        let missing = { controller.Start() with conversationId = Guid.NewGuid() }
        send reader (ConversationExportRead missing)
        let failed = wait reader "conversation.export-failed"
        Assert.Equal(missing.exportId.ToString("D"), (value failed "exportId").GetValue<string>())
    finally server.Dispose()

[<Fact>]
let ``e2e reobserve restores cancel identity and retrying the original send is idempotent`` () =
    // 上游接收连接但不返回响应，生成会一直保持在途，直到测试通过另一条 WS 取消。
    use upstream = new System.Net.HttpListener()
    let upstreamPort = pickPort ()
    upstream.Prefixes.Add(sprintf "http://127.0.0.1:%d/" upstreamPort)
    upstream.Start()
    let port = pickPort ()
    let token = ClientToken.ClientToken.generate ()
    let server = E2e.ServerHandle(port, token, providerUrl = sprintf "http://127.0.0.1:%d/v1" upstreamPort)
    try
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 20.0)
        let ct = timeout.Token
        use owner = E2e.conn port token ct
        let eventType (o: JsonObject) = o["type"].GetValue<string>()
        let value (o: JsonObject) (key: string) : JsonNode = o["payload"].AsObject()[key]
        let wait ws name = E2e.waitFor ws ct (fun o -> eventType o = name)
        let send ws ev = E2e.send ws (JsonNode.Parse(WireCodec.encode ev).AsObject()) ct
        let command ws cmd = E2e.send ws (JsonNode.Parse(WireCodec.encodeCommand cmd).AsObject()) ct
        wait owner "auth.accepted" |> ignore
        let id = Guid.NewGuid()
        command owner (CreateConversation {| invocationId = Guid.NewGuid(); conversationId = id; title = "可靠性测试"
                                             config = { SessionConfig.empty with provider = "test-provider"; model = "test-model" } |})
        wait owner "command.committed" |> ignore
        send owner (ObserveConversation {| conversationId = id |})
        let initial = wait owner "conversation.snapshot"
        send owner (CursorAdvanced {| id = (value initial "lastCommitId").GetValue<uint64>() |})
        let invocation = Guid.NewGuid()
        let original = SendUserMessage {| invocationId = invocation; conversationId = id
                                          messageJson = JsonNode.Parse("""{"role":"user","contents":[{"text":"只保存一次"}]}""") |}
        command owner original
        let mutable generation: Guid option = None
        let mutable commit: uint64 option = None
        while generation.IsNone || commit.IsNone do
            let ev = E2e.recvEvent owner ct
            if eventType ev = "generation.started" then generation <- Some(Guid.Parse((value ev "runId").GetValue<string>()))
            elif eventType ev = "command.committed" && (value ev "invocationId").GetValue<string>() = invocation.ToString("D") then
                commit <- Some((value ev "commitId").GetValue<uint64>())
        // 新客户端没有见过 generation.started，只能依赖快照中的运行身份。
        use observer = E2e.conn port token ct
        wait observer "auth.accepted" |> ignore
        send observer (ObserveConversation {| conversationId = id |})
        let resumed = wait observer "conversation.snapshot"
        let resumedGeneration = Guid.Parse((value resumed "runId").GetValue<string>())
        Assert.Equal(generation.Value, resumedGeneration)
        send observer (CursorAdvanced {| id = (value resumed "lastCommitId").GetValue<uint64>() |})
        // 模拟客户端未处理保存确认，按原 invocationId 和原载荷重试。
        command observer original
        let replay = wait observer "command.committed"
        Assert.Equal(commit.Value, (value replay "commitId").GetValue<uint64>())
        send observer (ObserveConversation {| conversationId = id |})
        let afterRetry = wait observer "conversation.snapshot"
        Assert.Single((value afterRetry "messages").AsArray()) |> ignore
        send observer (GenerationCancel {| conversationId = id; generationId = resumedGeneration |})
        let finished = wait observer "generation.finished"
        Assert.Equal("cancelled", (value finished "status").GetValue<string>())
        // 结束事件发出后立即读快照，不应复活已经结束的生成。
        send observer (ObserveConversation {| conversationId = id |})
        let idle = wait observer "conversation.snapshot"
        Assert.Equal("idle", (value idle "runtimeState").GetValue<string>())
        Assert.Null(value idle "runId")
        let guarded = Wanxiang.Client.WsClient()
        try
            let epoch = guarded.ConnectWithGenerationAsync(Uri(sprintf "ws://127.0.0.1:%d/ws" port), ct).GetAwaiter().GetResult()
            Assert.False(guarded.TrySendAtGenerationAsync(epoch - 1, AuthPresent {| token = token |}).GetAwaiter().GetResult())
            Assert.False(guarded.TrySendCommandAtGenerationAsync(epoch - 1, original).GetAwaiter().GetResult())
        finally guarded.Disconnect()
    finally
        server.Dispose()

/// P0-1 端到端：快照后推进游标，写命令应成功而非 stale-projection。
/// 观察对话 → 推进游标 → 发送消息：enqueue 响应与 message-committed 推送都应到达（回归）。
/// P1-1/P2-2/P2-7 端到端：附件上传（分块）→ committed → 下载回传声明/嗅探元数据。
/// P0-1 补：陈旧客户端（游标落后）写命令应被 stale-projection 拒绝。
/// 默认对话配置注入：客户端发空 config 创建对话，主机用 TOML 第一个 provider 填充（不硬编码）。
/// D5/决策 73 回归：删对话绝不触发 blob 清扫——即使 blob 又老又没有任何引用。
/// 旧行为（后台 sweep：1.5s 延迟 + 10 分钟 grace）会删掉这个倒签 mtime 的孤儿；
/// 新行为下没有任何删 blob 的路径，断言无条件成立（sleep 只给旧代码留开火时间）。
/// 端到端验证：客户端通过真实 WsClient.TrySendCommandAsync 发送命令，
/// 主机正常识别 AG-UI CUSTOM 承载并回传 command.committed（若使用旧外壳则会被主机作为 Unrecognized 忽略而超时失败）。
[<Fact>]
let ``e2e wsclient sends command through agui custom carrier and server commits`` () =
    let port = pickPort ()
    let token = ClientToken.ClientToken.generate ()
    let handle = E2e.ServerHandle(port, token)
    try
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
        let client = new WsClient()
        try
            client.ConnectAsync(Uri(sprintf "ws://127.0.0.1:%d/ws" port), cts.Token).GetAwaiter().GetResult()
            let authAcceptedTcs = TaskCompletionSource<bool>()
            let commitTcs = TaskCompletionSource<string>()
            use _sub =
                client.EventReceived.Subscribe(fun ev ->
                    match ev with
                    | AuthAccepted _ -> authAcceptedTcs.TrySetResult true |> ignore
                    | CommandCommitted d -> commitTcs.TrySetResult (d.invocationId.ToString("D")) |> ignore
                    | _ -> ())
            client.TrySendAsync(AuthPresent {| token = token |}).GetAwaiter().GetResult() |> ignore
            let authed = authAcceptedTcs.Task.Wait(TimeSpan.FromSeconds 5.0)
            Assert.True(authed)
            // 先 observe 列表以获取游标（避免 stale）
            let listSnapTcs = TaskCompletionSource<uint64>()
            use _listSub =
                client.EventReceived.Subscribe(fun ev ->
                    match ev with
                    | ConversationListSnapshot d -> listSnapTcs.TrySetResult d.lastCommitId |> ignore
                    | _ -> ())
            client.TrySendAsync(ObserveConversationList).GetAwaiter().GetResult() |> ignore
            let snapArrived = listSnapTcs.Task.Wait(TimeSpan.FromSeconds 5.0)
            Assert.True(snapArrived)
            client.TrySendAsync(CursorAdvanced {| id = listSnapTcs.Task.Result |}).GetAwaiter().GetResult() |> ignore
            // 发送创建对话命令
            let invId = Guid.NewGuid()
            let convId = Guid.NewGuid()
            let cmd =
                CreateConversation {|
                    invocationId = invId
                    conversationId = convId
                    title = "E2E WsClient AGUI"
                    config = { SessionConfig.empty with provider = "test-provider"; model = "test-model" }
                |}
            let sent = client.TrySendCommandAsync(cmd).GetAwaiter().GetResult()
            Assert.True sent
            // 若客户端发的是旧外壳，主机作为 Unrecognized 忽略，不会回传 command.committed，此处必定超时红掉；
            // 只有修复为 AG-UI CUSTOM 承载后，主机正常解码处理，才会回传 command.committed。
            let committed = commitTcs.Task.Wait(TimeSpan.FromSeconds 5.0)
            Assert.True(committed)
            Assert.Equal(invId.ToString("D"), commitTcs.Task.Result)
        finally
            client.Disconnect()
    finally
        handle.Dispose()

/// S-001 / STOP-1：日志损坏时正常启动进入**只读降级模式**——
/// 以最长有效前缀启动（历史可读），但写命令被明确拒绝（command.rejected），
/// 且**绝不自动截尾/删除**损坏文件（修复只能由 doctor --fix 显式执行）。