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
open Wanxiang.Tests.Helpers

/// 端到端：启动真实 ServerApp + ClientWebSocket。
/// 验证 P0-1 修复：观察快照 → cursor.advanced → 写命令不再被 stale-projection 拒绝。
module private E2e =

    type ServerHandle(port: int, token: string) as this =
        let dir = tempDir ()
        let configPath = Path.Combine(dir, "config.toml")
        let mutable server: Wanxiang.Server.ServerApp option = None
        do
            let instanceId = Guid.NewGuid()
            let hash = Auth.hashToken token
            let cfg =
                { AppConfig.defaults instanceId with
                    listen = sprintf "127.0.0.1:%d" port
                    authClients =
                        [ { tokenHash = hash
                            name = "e2e-test"
                            createdAtUtc = DateTimeOffset.UtcNow
                            lastSeenUtc = None
                            revoked = false } ] }
            File.WriteAllText(configPath, TomlCodec.serialize cfg)
            let app = Wanxiang.Server.ServerApp(dir, configPath, false, None, ignore)
            app.Start(false)
            server <- Some app
        member _.Dispose() =
            match server with
            | Some s -> (s :> IDisposable).Dispose()
            | None -> ()
            cleanup dir

    let recvEvent (ws: ClientWebSocket) (ct: CancellationToken) : JsonObject =
        let buffer = Array.zeroCreate<byte> (1024 * 1024)
        let ms = new MemoryStream()
        let mutable finished = false
        while not finished do
            let result = ws.ReceiveAsync(ArraySegment<byte>(buffer), ct).GetAwaiter().GetResult()
            ms.Write(buffer, 0, result.Count)
            if result.EndOfMessage then finished <- true
        let text = Encoding.UTF8.GetString(ms.ToArray())
        JsonNode.Parse(text).AsObject()

    let send (ws: ClientWebSocket) (ev: JsonObject) (ct: CancellationToken) =
        let bytes = Encoding.UTF8.GetBytes(ev.ToJsonString())
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
        // hello
        let hello = JsonObject()
        hello["type"] <- "protocol.hello"
        let hp = JsonObject()
        hp["protocol"] <- "wanxiang"
        hp["version"] <- Constants.ProtocolVersion
        hello["payload"] <- hp
        send ws hello ct
        // auth
        let auth = JsonObject()
        auth["type"] <- "auth.present"
        let ap = JsonObject()
        ap["token"] <- token
        auth["payload"] <- ap
        send ws auth ct
        ws

let private pickPort () =
    let listener = new Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> Net.IPEndPoint).Port
    listener.Stop()
    port

/// P0-1 端到端：快照后推进游标，写命令应成功而非 stale-projection。
[<Fact>]
let ``e2e write command succeeds after snapshot cursor advance`` () =
    let port = pickPort ()
    let token = Auth.generateToken ()
    let handle = E2e.ServerHandle(port, token)
    try
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
        let ws = E2e.conn port token cts.Token
        // 等待 auth.accepted
        let authEv = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "auth.accepted")
        Assert.Equal("auth.accepted", authEv["type"].GetValue<string>())
        // observe 列表
        let obs = JsonObject()
        obs["type"] <- "conversation-list.observe"
        E2e.send ws obs cts.Token
        let listSnap = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "conversation-list.snapshot")
        let listPayload = listSnap["payload"].AsObject()
        let lastCommit = listPayload["lastCommitId"].GetValue<uint64>()
        // 推进游标
        let adv = JsonObject()
        adv["type"] <- "cursor.advanced"
        let ap = JsonObject()
        ap["id"] <- lastCommit
        adv["payload"] <- ap
        E2e.send ws adv cts.Token
        // 创建会话（此时游标已追平，不应 stale）
        let convId = Guid.NewGuid()
        let create = JsonObject()
        create["type"] <- "conversation.create"
        let cp = JsonObject()
        cp["invocationId"] <- Guid.NewGuid().ToString("D")
        cp["conversationId"] <- convId.ToString("D")
        cp["title"] <- "e2e 会话"
        cp["config"] <- JsonNode.Parse("""{"provider":"openai","model":"test-model"}""")
        create["payload"] <- cp
        E2e.send ws create cts.Token
        // 期望 command.committed 而非 command.rejected
        let resp = E2e.waitFor ws cts.Token (fun o ->
            let t = o["type"].GetValue<string>()
            t = "command.committed" || t = "command.rejected")
        let t = resp["type"].GetValue<string>()
        Assert.Equal("command.committed", t)
        ws.Dispose()
    finally
        handle.Dispose()

/// 观察会话 → 推进游标 → 发送消息：enqueue 响应与 message-committed 推送都应到达（回归）。
[<Fact>]
let ``e2e observe advance cursor then enqueue gets response and push`` () =
    let port = pickPort ()
    let token = Auth.generateToken ()
    let dir = tempDir ()
    let configPath = Path.Combine(dir, "config.toml")
    try
        DataPaths.ensureDataDirs dir
        let convId = Guid.NewGuid()
        let cfg =
            { AppConfig.defaults (Guid.NewGuid()) with
                listen = sprintf "127.0.0.1:%d" port
                authClients =
                    [ { tokenHash = Auth.hashToken token
                        name = "e2e"
                        createdAtUtc = DateTimeOffset.UtcNow
                        lastSeenUtc = None
                        revoked = false } ] }
        File.WriteAllText(configPath, TomlCodec.serialize cfg)
        let eventsFile = DataPaths.eventFilePath dir DateTime.UtcNow
        let commits =
            [ Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "A"; config = Helpers.testConfig () } ] ]
        File.WriteAllLines(eventsFile, commits |> List.map CommitCodec.commitToJsonLine)
        let app = Wanxiang.Server.ServerApp(dir, configPath, false, None, ignore)
        app.Start(false)
        try
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
            let ws = E2e.conn port token cts.Token
            E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "auth.accepted") |> ignore
            // P2-6：列表观察者应收到 generation 状态变化推送（轻量 conversation.updated）
            let obsList = JsonObject()
            obsList["type"] <- "conversation-list.observe"
            E2e.send ws obsList cts.Token
            E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "conversation-list.snapshot") |> ignore
            let obs = JsonObject()
            obs["type"] <- "conversation.observe"
            let op = JsonObject()
            op["conversationId"] <- convId.ToString("D")
            obs["payload"] <- op
            E2e.send ws obs cts.Token
            let snap = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "conversation.snapshot")
            let last = (snap["payload"].AsObject()["lastCommitId"]).GetValue<uint64>()
            let adv = JsonObject()
            adv["type"] <- "cursor.advanced"
            let ap = JsonObject()
            ap["id"] <- last
            adv["payload"] <- ap
            E2e.send ws adv cts.Token
            // 推进后立即 enqueue
            let enq = JsonObject()
            enq["type"] <- "chat.user-message.enqueue"
            let ep = JsonObject()
            ep["invocationId"] <- Guid.NewGuid().ToString("D")
            ep["conversationId"] <- convId.ToString("D")
            ep["message"] <- JsonNode.Parse("""{"role":"user","contents":[{"text":"hi"}]}""")
            enq["payload"] <- ep
            E2e.send ws enq cts.Token
            // 事件顺序：message-committed → command.committed → generation.finished → conversation.updated(idle) → command.accepted
            let gotPush = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "conversation.message-committed")
            Assert.NotNull gotPush
            // P2-6：列表观察者应收到 generation 生命周期推送（provider 缺失 → 直接 finished(failed)）
            let gotListUpdate = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "conversation.updated")
            Assert.NotNull gotListUpdate
            let change = (gotListUpdate["payload"].AsObject()["change"]).AsObject()
            Assert.Equal("idle", (change["runtimeState"]).GetValue<string>())
            let gotAccepted = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "command.accepted")
            Assert.NotNull gotAccepted
            ws.Dispose()
        finally
            (app :> IDisposable).Dispose()
    finally
        cleanup dir

let ``e2e history paging slices by commit id`` () =
    let port = pickPort ()
    let token = Auth.generateToken ()
    let dir = tempDir ()
    let configPath = Path.Combine(dir, "config.toml")
    try
        // 预写日志：会话创建 + 5 条消息（id 1..6）
        DataPaths.ensureDataDirs dir
        let convId = Guid.NewGuid()
        let cfg =
            { AppConfig.defaults (Guid.NewGuid()) with
                listen = sprintf "127.0.0.1:%d" port
                authClients =
                    [ { tokenHash = Auth.hashToken token
                        name = "e2e"
                        createdAtUtc = DateTimeOffset.UtcNow
                        lastSeenUtc = None
                        revoked = false } ] }
        File.WriteAllText(configPath, TomlCodec.serialize cfg)
        let eventsFile = DataPaths.eventFilePath dir DateTime.UtcNow
        let commits =
            [ Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "分页"; config = Helpers.testConfig () } ]
              Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = Helpers.userMessageJson "m1" } ]
              Events.Commit.create 3UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = Helpers.assistantMessageJson "m2" } ]
              Events.Commit.create 4UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = Helpers.userMessageJson "m3" } ]
              Events.Commit.create 5UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = Helpers.assistantMessageJson "m4" } ]
              Events.Commit.create 6UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = Helpers.userMessageJson "m5" } ] ]
        File.WriteAllLines(eventsFile, commits |> List.map CommitCodec.commitToJsonLine)
        let app = Wanxiang.Server.ServerApp(dir, configPath, false, None, ignore)
        app.Start(false)
        try
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
            let ws = E2e.conn port token cts.Token
            E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "auth.accepted") |> ignore
            // observe 会话 → 快照含全部 5 条（未超快照上限）+ hasMore=false
            let obs = JsonObject()
            obs["type"] <- "conversation.observe"
            let op = JsonObject()
            op["conversationId"] <- convId.ToString("D")
            obs["payload"] <- op
            E2e.send ws obs cts.Token
            let snap = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "conversation.snapshot")
            let sp = snap["payload"].AsObject()
            Assert.Equal(5, sp["messages"].AsArray().Count)
            Assert.False(sp["snapshotHasMore"].GetValue<bool>())
            // 推进游标
            let adv = JsonObject()
            adv["type"] <- "cursor.advanced"
            let ap = JsonObject()
            ap["id"] <- sp["lastCommitId"].GetValue<uint64>()
            adv["payload"] <- ap
            E2e.send ws adv cts.Token
            // history.request：beforeCommitId=6, limit=2 → [4,5], hasMore=true
            let hr = JsonObject()
            hr["type"] <- "history.request"
            let hp = JsonObject()
            hp["conversationId"] <- convId.ToString("D")
            hp["beforeCommitId"] <- 6UL
            hp["limit"] <- 2
            hr["payload"] <- hp
            E2e.send ws hr cts.Token
            let page = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "history.page")
            let pp = page["payload"].AsObject()
            let items = pp["items"].AsArray()
            Assert.Equal(2, items.Count)
            Assert.Equal(4UL, (items[0].AsObject()["commitId"]).GetValue<uint64>())
            Assert.Equal(5UL, (items[1].AsObject()["commitId"]).GetValue<uint64>())
            Assert.True(pp["hasMore"].GetValue<bool>())
            ws.Dispose()
        finally
            (app :> IDisposable).Dispose()
    finally
        cleanup dir

/// P1-1/P2-2/P2-7 端到端：附件上传（分块）→ committed → 下载回传声明/嗅探元数据。
[<Fact>]
let ``e2e attachment upload and download round trip`` () =
    let port = pickPort ()
    let token = Auth.generateToken ()
    let handle = E2e.ServerHandle(port, token)
    try
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
        let ws = E2e.conn port token cts.Token
        E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "auth.accepted") |> ignore
        // 1x1 PNG：声明 octet-stream，服务端嗅探为 image/png（P2-2/Q176）
        let png = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy; 0x00uy; 0x00uy; 0x00uy; 0x0Duy; 0x49uy; 0x48uy; 0x44uy; 0x52uy |]
        let hash = Convert.ToHexString(SHA256.HashData png).ToLowerInvariant()
        let aid = Guid.NewGuid()
        let beginEv = JsonObject()
        beginEv["type"] <- "attachment.begin"
        let bp = JsonObject()
        bp["attachmentId"] <- aid.ToString("D")
        bp["totalBytes"] <- int64 png.Length
        bp["sha256"] <- hash
        bp["mediaType"] <- "application/octet-stream"
        bp["fileName"] <- "sniff.png"
        beginEv["payload"] <- bp
        E2e.send ws beginEv cts.Token
        let chunk = JsonObject()
        chunk["type"] <- "attachment.chunk"
        let cp = JsonObject()
        cp["attachmentId"] <- aid.ToString("D")
        cp["index"] <- 0
        cp["data"] <- Convert.ToBase64String png
        chunk["payload"] <- cp
        E2e.send ws chunk cts.Token
        let complete = JsonObject()
        complete["type"] <- "attachment.complete"
        let cpp = JsonObject()
        cpp["attachmentId"] <- aid.ToString("D")
        cpp["sha256"] <- hash
        complete["payload"] <- cpp
        E2e.send ws complete cts.Token
        let committed = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "attachment.committed")
        Assert.Equal(hash, (committed["payload"].AsObject()["sha256"]).GetValue<string>())
        // 下载：begin 应回传嗅探 mediaType 与 fileName（P2-7/Q176）
        let req = JsonObject()
        req["type"] <- "attachment.download-request"
        let rp = JsonObject()
        rp["sha256"] <- hash
        req["payload"] <- rp
        E2e.send ws req cts.Token
        let dbegin = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "attachment.download-begin")
        let dp = dbegin["payload"].AsObject()
        Assert.Equal("image/png", dp["mediaType"].GetValue<string>())
        Assert.Equal("sniff.png", dp["fileName"].GetValue<string>())
        Assert.Equal(int64 png.Length, dp["size"].GetValue<int64>())
        // 接收全部块
        let dchunk = E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "attachment.download-chunk")
        Assert.Equal(Convert.ToBase64String png, (dchunk["payload"].AsObject()["data"]).GetValue<string>())
        E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "attachment.download-complete") |> ignore
        ws.Dispose()
    finally
        handle.Dispose()

/// P0-1 补：陈旧客户端（游标落后）写命令应被 stale-projection 拒绝。
[<Fact>]
let ``e2e stale client write command is rejected`` () =
    let port = pickPort ()
    let token = Auth.generateToken ()
    let handle = E2e.ServerHandle(port, token)
    try
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
        let ws = E2e.conn port token cts.Token
        E2e.waitFor ws cts.Token (fun o -> o["type"].GetValue<string>() = "auth.accepted") |> ignore
        // 不 observe 也不推进游标（游标保持 0）
        // 直接尝试创建会话 —— 需要全局最新水位；空库时 latestCommitId=0，可能成功。
        // 先通过另一连接创建一条提交使全局水位 > 0
        let ws2 = E2e.conn port token cts.Token
        E2e.waitFor ws2 cts.Token (fun o -> o["type"].GetValue<string>() = "auth.accepted") |> ignore
        let obs2 = JsonObject()
        obs2["type"] <- "conversation-list.observe"
        E2e.send ws2 obs2 cts.Token
        let snap2 = E2e.waitFor ws2 cts.Token (fun o -> o["type"].GetValue<string>() = "conversation-list.snapshot")
        let snap2Payload = snap2["payload"].AsObject()
        let last2 = snap2Payload["lastCommitId"].GetValue<uint64>()
        let adv2 = JsonObject()
        adv2["type"] <- "cursor.advanced"
        let ap2 = JsonObject()
        ap2["id"] <- last2
        adv2["payload"] <- ap2
        E2e.send ws2 adv2 cts.Token
        let convId = Guid.NewGuid()
        let create = JsonObject()
        create["type"] <- "conversation.create"
        let cp = JsonObject()
        cp["invocationId"] <- Guid.NewGuid().ToString("D")
        cp["conversationId"] <- convId.ToString("D")
        cp["title"] <- "制造提交"
        cp["config"] <- JsonNode.Parse("""{"provider":"openai","model":"test-model"}""")
        create["payload"] <- cp
        E2e.send ws2 create cts.Token
        E2e.waitFor ws2 cts.Token (fun o ->
            let t = o["type"].GetValue<string>()
            t = "command.committed" || t = "command.rejected") |> ignore
        // 现在 ws1 游标仍为 0，全局水位已 > 0 → 创建新会话应被 stale 拒绝
        let convId2 = Guid.NewGuid()
        let create2 = JsonObject()
        create2["type"] <- "conversation.create"
        let cp2 = JsonObject()
        cp2["invocationId"] <- Guid.NewGuid().ToString("D")
        cp2["conversationId"] <- convId2.ToString("D")
        cp2["title"] <- "陈旧客户端"
        cp2["config"] <- JsonNode.Parse("""{"provider":"openai","model":"test-model"}""")
        create2["payload"] <- cp2
        E2e.send ws create2 cts.Token
        let resp = E2e.waitFor ws cts.Token (fun o ->
            let t = o["type"].GetValue<string>()
            t = "command.committed" || t = "command.rejected")
        Assert.Equal("command.rejected", resp["type"].GetValue<string>())
        let respPayload = resp["payload"].AsObject()
        let code = respPayload["code"].GetValue<string>()
        Assert.Equal("stale-projection", code)
        ws.Dispose()
        ws2.Dispose()
    finally
        handle.Dispose()
