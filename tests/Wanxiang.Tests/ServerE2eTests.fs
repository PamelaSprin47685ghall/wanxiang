module Wanxiang.Tests.ServerE2eTests

open System
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
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
