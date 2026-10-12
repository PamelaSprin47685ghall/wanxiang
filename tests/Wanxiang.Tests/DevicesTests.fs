module Wanxiang.Tests.DevicesTests

// W2（设备与配对）：邀请、设备列表、重命名、吊销的主机 E2E 与纯逻辑测试。

open System
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json.Nodes
open System.Threading
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Store
open Wanxiang.Tests.Helpers
open Wanxiang.Tests.ServerE2eTests

/// 主机夹具：一台“桌面”设备（保留名）与一台普通设备。
type DeviceServer(port: int) =
    let dir = tempDir ()
    let configPath = Path.Combine(dir, "config.toml")
    let desktopToken = Auth.generateToken ()
    let phoneToken = Auth.generateToken ()
    let mutable server: Wanxiang.Server.ServerApp option = None
    do
        let instanceId = Guid.CreateVersion7()
        let record name (token: string) : ClientAuthRecord =
            { tokenHash = Auth.hashToken token
              name = name
              createdAtUtc = DateTimeOffset.UtcNow
              lastSeenUtc = None
              revoked = false }
        let cfg =
            { AppConfig.defaults instanceId with
                listen = sprintf "127.0.0.1:%d" port
                generation = { (AppConfig.defaults instanceId).generation with autoTitle = false }
                authClients = [ record Auth.desktopClientName desktopToken; record "phone" phoneToken ] }
        File.WriteAllText(configPath, TomlCodec.serialize cfg)
        let app = new Wanxiang.Server.ServerApp(dir, configPath, false, None, ignore)
        app.Start(false)
        server <- Some app
    member _.Port = port
    member _.DesktopToken = desktopToken
    member _.PhoneToken = phoneToken
    member _.Config = (match TomlCodec.tryParse (File.ReadAllText configPath) with Ok c -> c | Error e -> failwithf "bad toml: %A" e)
    /// 事件日志目录下所有文件的字节快照（邀请不得改变日志）。
    member _.LogBytes =
        let eventsDir = DataPaths.eventsDir dir
        if Directory.Exists eventsDir then
            Directory.GetFiles(eventsDir) |> Array.sort |> Array.map (fun f -> f, File.ReadAllBytes f |> Convert.ToBase64String) |> Array.toList
        else []
    member _.Dispose() =
        match server with
        | Some s -> (s :> IDisposable).Dispose()
        | None -> ()
        cleanup dir

let private get<'T> (o: JsonObject) (k: string) : 'T = o[k].GetValue<'T>()
let private timeout () = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
let private typeOf (o: JsonObject) = o["type"].GetValue<string>()
let private payload (o: JsonObject) = o["payload"].AsObject()
let private sendEv (ws: ClientWebSocket) (ct: CancellationToken) (ev: WireEvent) =
    E2e.send ws (JsonNode.Parse(WireCodec.encode ev).AsObject()) ct
let private waitType ws ct (name: string) = E2e.waitFor ws ct (fun o -> typeOf o = name)

/// 未认证连接（尚未发 auth.present）。
let private anonymous (port: int) (ct: CancellationToken) =
    let ws = new ClientWebSocket()
    ws.ConnectAsync(Uri(sprintf "ws://127.0.0.1:%d/ws" port), ct).GetAwaiter().GetResult()
    ws

/// 读到对端关闭帧或连接断开为止。
let private waitClosed (ws: ClientWebSocket) (ct: CancellationToken) : bool =
    let buffer = Array.zeroCreate<byte> 4096
    let mutable closed = false
    try
        while not closed do
            let r = ws.ReceiveAsync(ArraySegment<byte>(buffer), ct).GetAwaiter().GetResult()
            if r.MessageType = WebSocketMessageType.Close then closed <- true
    with
    | :? WebSocketException -> closed <- true
    closed

let private invite (ws: ClientWebSocket) ct =
    sendEv ws ct PairingInvite
    let issued = waitType ws ct "pairing.invite-issued"
    (get<string> (payload issued) "code")

// ---------- 验收 1：邀请 → 另一条连接配对 → 永久令牌 ----------

[<Fact>]
let ``desktop invite code pairs another connection and yields a working permanent token`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use desktop = E2e.conn (s.Port) (s.DesktopToken) ct
        waitType desktop ct "auth.accepted" |> ignore
        let code = invite desktop ct
        Assert.Matches("^[0-9]{6}$", code)
        // 新设备：直接提交邀请码，不必先 pairing.requested
        use fresh = anonymous (s.Port) ct
        sendEv fresh ct (PairingAttempted {| code = code; clientName = Some "my-tablet" |})
        let ok = waitType fresh ct "pairing.succeeded"
        let token = (get<string> (payload ok) "token")
        Assert.StartsWith(Auth.tokenPrefix, token)
        // TOML 只存哈希，名称是自报名
        let added = s.Config.authClients |> List.find (fun c -> c.name = "my-tablet")
        Assert.Equal(Auth.hashToken token, added.tokenHash)
        // 令牌在第三条连接上可用
        use third = E2e.conn (s.Port) token ct
        waitType third ct "auth.accepted" |> ignore
        // 码单次有效
        use replay = anonymous (s.Port) ct
        sendEv replay ct (PairingAttempted {| code = code; clientName = None |})
        let failed = waitType replay ct "pairing.failed"
        Assert.False(String.IsNullOrEmpty((get<string> (payload failed) "reason")))
    finally s.Dispose()

[<Fact>]
let ``pairing request reuses a live invite instead of replacing the code`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use desktop = E2e.conn (s.Port) (s.DesktopToken) ct
        waitType desktop ct "auth.accepted" |> ignore
        let code = invite desktop ct
        // PWA 的既有流程：先 pairing.requested（等价于“我要配对”）
        use pwa = anonymous (s.Port) ct
        sendEv pwa ct (PairingRequested {| clientName = Some "pwa" |})
        let started = waitType pwa ct "pairing.started"
        let seconds = (get<int> (payload started) "expiresInSeconds")
        Assert.InRange(seconds, 1, 300)
        sendEv pwa ct (PairingAttempted {| code = code; clientName = Some "pwa" |})
        waitType pwa ct "pairing.succeeded" |> ignore
    finally s.Dispose()

// ---------- 验收 2：过期、冻结、日志不变 ----------

[<Fact>]
let ``invite code expires after five minutes and is single use`` () =
    let state = Auth.PairingState()
    let now = DateTimeOffset.UtcNow
    let code, expires = state.StartInvite(now, TimeSpan.FromMinutes 5.0)
    Assert.Equal(now + TimeSpan.FromMinutes 5.0, expires)
    Assert.True((state.ActiveInvite now).IsSome)
    Assert.True((state.ActiveInvite(now + TimeSpan.FromMinutes 4.9)).IsSome)
    Assert.True((state.ActiveInvite(now + TimeSpan.FromMinutes 5.1)).IsNone)
    match state.TryConsume(now + TimeSpan.FromMinutes 5.1, code) with
    | Error _ -> ()
    | Ok () -> failwith "expired invite code must not pair"
    // 新邀请替换旧邀请；单次使用后不再算有效邀请
    let c1, _ = state.StartInvite(now, TimeSpan.FromMinutes 5.0)
    let c2, _ = state.StartInvite(now, TimeSpan.FromMinutes 5.0)
    if c1 <> c2 then Assert.True((state.TryConsume(now, c1)).IsError)
    Assert.True((state.TryConsume(now, c2)).IsOk)
    Assert.True((state.ActiveInvite now).IsNone)
    // 普通 Start 清除邀请标记
    state.StartInvite(now, TimeSpan.FromMinutes 5.0) |> ignore
    state.Start(now, TimeSpan.FromMinutes 5.0) |> ignore
    Assert.True((state.ActiveInvite now).IsNone)

[<Fact>]
let ``wrong codes freeze the address after five failures even with a live invite`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use desktop = E2e.conn (s.Port) (s.DesktopToken) ct
        waitType desktop ct "auth.accepted" |> ignore
        let code = invite desktop ct
        let wrong = if code = "000000" then "111111" else "000000"
        use pwa = anonymous (s.Port) ct
        let mutable frozen = false
        for _ in 1..5 do
            sendEv pwa ct (PairingAttempted {| code = wrong; clientName = None |})
            let f = waitType pwa ct "pairing.failed"
            frozen <- (get<bool> (payload f) "frozen")
        Assert.True frozen
        // 冻结后连正确的码也被拒绝
        sendEv pwa ct (PairingAttempted {| code = code; clientName = None |})
        let f = waitType pwa ct "pairing.failed"
        Assert.True((get<bool> (payload f) "frozen"))
    finally s.Dispose()

[<Fact>]
let ``issuing an invite does not touch the event log`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use desktop = E2e.conn (s.Port) (s.DesktopToken) ct
        waitType desktop ct "auth.accepted" |> ignore
        let before = s.LogBytes
        let code = invite desktop ct
        use fresh = anonymous (s.Port) ct
        sendEv fresh ct (PairingAttempted {| code = code; clientName = Some "x" |})
        waitType fresh ct "pairing.succeeded" |> ignore
        Assert.Equal<(string * string) list>(before, s.LogBytes)
    finally s.Dispose()

// ---------- 验收 3：未认证 / 非桌面 ----------

[<Fact>]
let ``unauthenticated invite is rejected closes the connection and counts toward the rate limit`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        for _ in 1..5 do
            use ws = anonymous (s.Port) ct
            sendEv ws ct PairingInvite
            let rejected = waitType ws ct "pairing.invite-rejected"
            Assert.Equal("not-authenticated", (get<string> (payload rejected) "reason"))
            Assert.True(waitClosed ws ct)
        // 同一地址已被冻结：配对请求被告知冻结
        use ws = anonymous (s.Port) ct
        sendEv ws ct (PairingRequested {| clientName = None |})
        let f = waitType ws ct "pairing.failed"
        Assert.True((get<bool> (payload f) "frozen"))
    finally s.Dispose()

[<Fact>]
let ``authenticated non desktop device cannot invite and stays connected`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use phone = E2e.conn (s.Port) (s.PhoneToken) ct
        waitType phone ct "auth.accepted" |> ignore
        sendEv phone ct PairingInvite
        let rejected = waitType phone ct "pairing.invite-rejected"
        Assert.Equal("not-desktop", (get<string> (payload rejected) "reason"))
        sendEv phone ct Ping
        waitType phone ct "protocol.pong" |> ignore
        // 没有任何邀请被创建：新连接直接提交任意码得到失败
        use fresh = anonymous (s.Port) ct
        sendEv fresh ct (PairingAttempted {| code = "123456"; clientName = None |})
        // 无邀请且未请求配对：主机不处理（不回事件），改为请求后再试以确认没有有效码
        sendEv fresh ct (PairingRequested {| clientName = None |})
        waitType fresh ct "pairing.started" |> ignore
    finally s.Dispose()

[<Fact>]
let ``paired device cannot claim the reserved desktop name or gain invite rights`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use desktop = E2e.conn (s.Port) (s.DesktopToken) ct
        waitType desktop ct "auth.accepted" |> ignore
        let code = invite desktop ct
        use fresh = anonymous (s.Port) ct
        sendEv fresh ct (PairingAttempted {| code = code; clientName = Some " Wanxiang-Desktop " |})
        let ok = waitType fresh ct "pairing.succeeded"
        let token = (get<string> (payload ok) "token")
        let added = s.Config.authClients |> List.find (fun c -> c.tokenHash = Auth.hashToken token)
        Assert.False(Auth.isReservedClientName added.name)
        use again = E2e.conn (s.Port) token ct
        waitType again ct "auth.accepted" |> ignore
        sendEv again ct PairingInvite
        let r = waitType again ct "pairing.invite-rejected"
        Assert.Equal("not-desktop", (get<string> (payload r) "reason"))
    finally s.Dispose()

[<Fact>]
let ``desktop identity needs the reserved name an unrevoked record and a loopback address`` () =
    let record name revoked : ClientAuthRecord option =
        Some { tokenHash = "ab"; name = name; createdAtUtc = DateTimeOffset.UtcNow; lastSeenUtc = None; revoked = revoked }
    Assert.True(Auth.isDesktopClient (record Auth.desktopClientName false) "127.0.0.1")
    Assert.True(Auth.isDesktopClient (record Auth.desktopClientName false) "::1")
    Assert.True(Auth.isDesktopClient (record Auth.desktopClientName false) "::ffff:127.0.0.1")
    Assert.False(Auth.isDesktopClient (record Auth.desktopClientName false) "192.168.1.20")
    Assert.False(Auth.isDesktopClient (record Auth.desktopClientName false) "?")
    Assert.False(Auth.isDesktopClient (record Auth.desktopClientName true) "127.0.0.1")
    Assert.False(Auth.isDesktopClient (record "phone" false) "127.0.0.1")
    Assert.False(Auth.isDesktopClient None "127.0.0.1")

// ---------- 验收 4：旧客户端不受影响 ----------

[<Fact>]
let ``legacy pairing flow and unknown events still work for clients unaware of invites`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use phone = E2e.conn (s.Port) (s.PhoneToken) ct
        waitType phone ct "auth.accepted" |> ignore
        // 别家的 CUSTOM：忽略，不断连
        E2e.send phone (JsonNode.Parse("""{"type":"CUSTOM","name":"other.vendor/x","value":{"a":1}}""").AsObject()) ct
        sendEv phone ct CatalogRequest
        waitType phone ct "catalog.snapshot" |> ignore
        // 旧流程：pairing.requested → pairing.started（无邀请时仍是 300 秒）
        use legacy = anonymous (s.Port) ct
        sendEv legacy ct (PairingRequested {| clientName = Some "old" |})
        let started = waitType legacy ct "pairing.started"
        Assert.Equal(300, (get<int> (payload started) "expiresInSeconds"))
    finally s.Dispose()

[<Fact>]
let ``new events round trip through the codec and the agui carrier`` () =
    let expires = DateTimeOffset(2026, 10, 12, 4, 0, 0, TimeSpan.Zero)
    let roundTrip (ev: WireEvent) =
        match WireAgui.tryDecode (WireAgui.encode ev) with
        | Ok(Some decoded) -> decoded
        | other -> failwithf "decode failed: %A" other
    match roundTrip PairingInvite with
    | PairingInvite -> ()
    | other -> failwithf "%A" other
    match roundTrip (PairingInviteIssued {| code = "012345"; expiresAtUtc = expires; expiresInSeconds = 300 |}) with
    | PairingInviteIssued d ->
        Assert.Equal("012345", d.code)
        Assert.Equal(expires, d.expiresAtUtc)
        Assert.Equal(300, d.expiresInSeconds)
    | other -> failwithf "%A" other
    match roundTrip (PairingInviteRejected {| reason = "not-desktop" |}) with
    | PairingInviteRejected d -> Assert.Equal("not-desktop", d.reason)
    | other -> failwithf "%A" other
    let items = JsonArray()
    items.Add(JsonObject(dict [ "id", JsonNode.Parse "\"abc\"" ]))
    match roundTrip (DevicesSnapshot {| items = items; canInvite = true |}) with
    | DevicesSnapshot d ->
        Assert.True d.canInvite
        Assert.Equal(1, d.items.Count)
    | other -> failwithf "%A" other
    let rid = Guid.NewGuid()
    match roundTrip (DeviceRename {| requestId = rid; deviceId = "abc"; name = "平板" |}) with
    | DeviceRename d -> Assert.Equal((rid, "abc", "平板"), (d.requestId, d.deviceId, d.name))
    | other -> failwithf "%A" other
    match roundTrip (DeviceRevoke {| requestId = rid; deviceId = "abc" |}) with
    | DeviceRevoke d -> Assert.Equal((rid, "abc"), (d.requestId, d.deviceId))
    | other -> failwithf "%A" other
    match roundTrip DevicesRequest with
    | DevicesRequest -> ()
    | other -> failwithf "%A" other
    Assert.Equal("pairing.invite", WireEvent.typeName PairingInvite)
    Assert.Equal("pairing.invite-issued", WireEvent.typeName (PairingInviteIssued {| code = ""; expiresAtUtc = expires; expiresInSeconds = 0 |}))

// ---------- 验收 5：设备列表、重命名、吊销 ----------

let private devices (ws: ClientWebSocket) ct =
    sendEv ws ct DevicesRequest
    let snap = waitType ws ct "devices.snapshot"
    payload snap

let private findDevice (snapshot: JsonObject) (name: string) =
    snapshot["items"].AsArray() |> Seq.map (fun n -> n.AsObject()) |> Seq.find (fun o -> o["name"].GetValue<string>() = name)

[<Fact>]
let ``device list shows names online state and only desktop may invite`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use desktop = E2e.conn (s.Port) (s.DesktopToken) ct
        waitType desktop ct "auth.accepted" |> ignore
        let before = devices desktop ct
        Assert.True(before["canInvite"].GetValue<bool>())
        let me = findDevice before Auth.desktopClientName
        Assert.True(me["current"].GetValue<bool>())
        Assert.True(me["desktop"].GetValue<bool>())
        Assert.False((get<bool> (findDevice before "phone") "online"))
        use phone = E2e.conn (s.Port) (s.PhoneToken) ct
        waitType phone ct "auth.accepted" |> ignore
        let after = devices desktop ct
        Assert.True((get<bool> (findDevice after "phone") "online"))
        // 令牌哈希不得出主机：id 是 16 位截断，且不含完整哈希
        let id = (get<string> (findDevice after "phone") "id")
        Assert.Equal(16, id.Length)
        Assert.DoesNotContain(Auth.hashToken (s.PhoneToken), after.ToJsonString())
        let phoneView = devices phone ct
        Assert.False(phoneView["canInvite"].GetValue<bool>())
    finally s.Dispose()

[<Fact>]
let ``rename persists to toml and rejects empty reserved and desktop targets`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use desktop = E2e.conn (s.Port) (s.DesktopToken) ct
        waitType desktop ct "auth.accepted" |> ignore
        let snap = devices desktop ct
        let phoneId = (get<string> (findDevice snap "phone") "id")
        let desktopId = (get<string> (findDevice snap Auth.desktopClientName) "id")
        let rename id name =
            let rid = Guid.NewGuid()
            sendEv desktop ct (DeviceRename {| requestId = rid; deviceId = id; name = name |})
            let applied = waitType desktop ct "config.applied"
            Assert.Equal(rid.ToString("D"), (get<string> (payload applied) "requestId"))
            (get<bool> (payload applied) "ok")
        Assert.True(rename phoneId "  书房平板 ")
        Assert.Contains(s.Config.authClients, fun c -> c.name = "书房平板")
        Assert.False(rename phoneId "   ")
        Assert.False(rename phoneId "wanxiang-desktop")
        Assert.False(rename phoneId (String('x', 65)))
        Assert.False(rename desktopId "other")
        Assert.False(rename "0000000000000000" "ghost")
        Assert.Contains(s.Config.authClients, fun c -> c.name = Auth.desktopClientName)
    finally s.Dispose()

[<Fact>]
let ``revoking a device disconnects it immediately and marks revoked in toml and list`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use desktop = E2e.conn (s.Port) (s.DesktopToken) ct
        waitType desktop ct "auth.accepted" |> ignore
        use phone = E2e.conn (s.Port) (s.PhoneToken) ct
        waitType phone ct "auth.accepted" |> ignore
        let snap = devices desktop ct
        let phoneId = (get<string> (findDevice snap "phone") "id")
        let rid = Guid.NewGuid()
        sendEv desktop ct (DeviceRevoke {| requestId = rid; deviceId = phoneId |})
        let applied = waitType desktop ct "config.applied"
        Assert.True((get<bool> (payload applied) "ok"))
        let list = waitType desktop ct "devices.snapshot"
        let revoked = findDevice (payload list) "phone"
        Assert.True(revoked["revoked"].GetValue<bool>())
        Assert.False(revoked["online"].GetValue<bool>())
        // 该设备连接立即断开
        Assert.True(waitClosed phone ct)
        Assert.True((s.Config.authClients |> List.find (fun c -> c.name = "phone")).revoked)
        // 吊销后令牌不能再认证
        use again = E2e.conn (s.Port) (s.PhoneToken) ct
        let rejected = waitType again ct "auth.rejected"
        Assert.False(String.IsNullOrEmpty((get<string> (payload rejected) "reason")))
    finally s.Dispose()

[<Fact>]
let ``revoke refuses the current device the desktop device and already revoked devices`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use desktop = E2e.conn (s.Port) (s.DesktopToken) ct
        waitType desktop ct "auth.accepted" |> ignore
        use phone = E2e.conn (s.Port) (s.PhoneToken) ct
        waitType phone ct "auth.accepted" |> ignore
        let snap = devices phone ct
        let phoneId = (get<string> (findDevice snap "phone") "id")
        let desktopId = (get<string> (findDevice snap Auth.desktopClientName) "id")
        let revoke (ws: ClientWebSocket) id =
            sendEv ws ct (DeviceRevoke {| requestId = Guid.NewGuid(); deviceId = id |})
            let applied = waitType ws ct "config.applied"
            (get<bool> (payload applied) "ok")
        Assert.False(revoke phone phoneId)      // 当前设备
        Assert.False(revoke phone desktopId)    // 桌面设备
        Assert.True(revoke desktop phoneId)
        Assert.False(revoke desktop phoneId)    // 已吊销
        Assert.DoesNotContain(s.Config.authClients, fun c -> c.name = Auth.desktopClientName && c.revoked)
    finally s.Dispose()

[<Fact>]
let ``unauthenticated device requests close the connection`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use ws = anonymous (s.Port) ct
        sendEv ws ct DevicesRequest
        Assert.True(waitClosed ws ct)
    finally s.Dispose()

// ---------- 验收 7：本机桌面全自动，不出现配对码流程 ----------

[<Fact>]
let ``desktop token connects directly without any pairing event`` () =
    let s = new DeviceServer(pickPort ())
    try
        use cts = timeout ()
        let ct = cts.Token
        use desktop = E2e.conn (s.Port) (s.DesktopToken) ct
        let seen = ResizeArray<string>()
        let mutable ev = E2e.recvEvent desktop ct
        seen.Add(typeOf ev)
        // 认证后做一次往返，收齐认证阶段的全部事件
        sendEv desktop ct Ping
        while typeOf ev <> "protocol.pong" do
            ev <- E2e.recvEvent desktop ct
            seen.Add(typeOf ev)
        Assert.Contains("auth.accepted", seen)
        Assert.DoesNotContain(seen, fun t -> t.StartsWith "pairing.")
    finally s.Dispose()
