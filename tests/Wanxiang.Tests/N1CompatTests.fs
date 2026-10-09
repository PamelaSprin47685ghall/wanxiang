module Wanxiang.Tests.N1CompatTests

open System
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Tests.Helpers

/// P-003：场景 G（AGENTS.md §31）——协议 N-1 兼容。
///
/// ADR-001 方案 B：版本完全 in-band（AG-UI RUN_STARTED.protocolVersion），
/// Hello/UpgradeRequired 无生产路径。因此 N-1 兼容的机器断言是：
///
/// 1. 当前 Wire 解码器仍能读旧客户端事件形态（黄金 wire 样本，冻结为字面量）。
/// 2. 未知事件类型（未来客户端）→ 宽容忽略，不切断连接（AG-UI 增量安全）。
/// 3. 已知类型但畸形载荷 → 明确协议错误（不是静默部分工作）。
/// 4. 当前服务端 + 旧式客户端（auth.present 无 Hello 握手）→ 正常工作。
/// 5. 未认证业务事件 → 明确 protocol error（绝不半工作状态）。
module N1Compat =

    // ==========================================
    // 1. Wire 解码器 N-1：旧事件形态仍可读（黄金样本冻结）
    // ==========================================

    [<Fact>]
    let ``旧式 auth.present 事件形态仍被当前解码器识别`` () =
        // 旧客户端（无 Hello 握手时代）发送的 {type, payload} 外壳
        let json = """{"type":"auth.present","payload":{"token":"tok-123"}}"""
        match WireCodec.tryDecode json with
        | Ok (AuthPresent d) -> Assert.Equal("tok-123", d.token)
        | other -> failwithf "expected AuthPresent, got %A" other

    [<Fact>]
    let ``旧式 pairing.requested 事件形态仍被当前解码器识别`` () =
        let json = """{"type":"pairing.requested","payload":{"clientName":"legacy-client"}}"""
        match WireCodec.tryDecode json with
        | Ok (PairingRequested d) -> Assert.Equal(Some "legacy-client", d.clientName)
        | other -> failwithf "expected PairingRequested, got %A" other

    [<Fact>]
    let ``Hello 事件仍可解码（兼容 reader，ADR-001 方案 B 保留）`` () =
        // 方案 B：保留兼容 reader 仅用于读取旧客户端事件；无生产发送路径。
        let json = """{"type":"protocol.hello","payload":{"protocol":"wanxiang","version":1}}"""
        match WireCodec.tryDecode json with
        | Ok (Hello d) ->
            Assert.Equal("wanxiang", d.protocol)
            Assert.Equal(1, d.version)
        | other -> failwithf "expected Hello, got %A" other

    [<Fact>]
    let ``UpgradeRequired 事件仍可解码（兼容 reader）`` () =
        let json = """{"type":"protocol.upgrade-required","payload":{"serverVersion":2,"clientVersion":1}}"""
        match WireCodec.tryDecode json with
        | Ok (UpgradeRequired d) ->
            Assert.Equal(2, d.serverVersion)
            Assert.Equal(1, d.clientVersion)
        | other -> failwithf "expected UpgradeRequired, got %A" other

    // ==========================================
    // 2. AG-UI N-1：未知事件类型宽容忽略
    // ==========================================

    [<Fact>]
    let ``未来 AG-UI 事件类型被宽容识别为 Unrecognized 而非抛异常`` () =
        // 官方 SDK 对未知 type 抛 AGUIUnknownEventTypeException（与其自家规范矛盾），
        // 万象必须自建宽容读层（SSOT 56.3 硬约束 3）。
        let json = """{"type":"SOME_FUTURE_EVENT_TYPE_2099","payload":{"x":1}}"""
        match Wanxiang.Agui.TolerantReader.parse json with
        | Wanxiang.Agui.ParsedEvent.Unrecognized name -> Assert.Equal("SOME_FUTURE_EVENT_TYPE_2099", name)
        | other -> failwithf "expected Unrecognized, got %A" other

    [<Fact>]
    let ``别家 CUSTOM 事件被识别为 Custom 而非拒绝`` () =
        // AG-UI 对 CUSTOM 规定 MUST ignore；别家命名空间不得切断连接
        let json = """{"type":"CUSTOM","name":"other-vendor/thing","value":{"a":1}}"""
        match Wanxiang.Agui.TolerantReader.parse json with
        | Wanxiang.Agui.ParsedEvent.Custom (name, _) -> Assert.Equal("other-vendor/thing", name)
        | other -> failwithf "expected Custom, got %A" other

    [<Fact>]
    let ``已知类型但畸形载荷被识别为 Malformed（真协议错误）`` () =
        // 已知类型字段畸形 = 真的坏了：不得静默忽略
        let json = """{"type":"CUSTOM"}""" // 缺 name
        match Wanxiang.Agui.TolerantReader.parse json with
        | Wanxiang.Agui.ParsedEvent.Malformed reason -> Assert.Contains("name", reason)
        | other -> failwithf "expected Malformed, got %A" other

    // ==========================================
    // 3. 服务端 N-1：旧式客户端可用；未认证业务事件明确拒绝
    // ==========================================

    [<Fact>]
    let ``当前服务端 + 旧式客户端（auth.present 无 Hello）正常认证`` () =
        // 端到端：真实 ServerApp + ClientWebSocket。旧式客户端不发送 Hello 握手事件，
        // 连接后第一个动作即 auth.present（ADR-001 方案 B）。
        // 复用 ServerE2eTests 的 ServerHandle（同进程真实服务端）。
        let port = Wanxiang.Tests.ServerE2eTests.pickPort ()
        let token = Wanxiang.Config.Auth.generateToken ()
        let server = Wanxiang.Tests.ServerE2eTests.E2e.ServerHandle(port, token)
        try
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 20.0)
            let ct = timeout.Token
            use ws = Wanxiang.Tests.ServerE2eTests.E2e.conn port token ct
            let ev = Wanxiang.Tests.ServerE2eTests.E2e.waitFor ws ct (fun ev -> ev["type"].GetValue<string>() = "auth.accepted")
            Assert.Equal("auth.accepted", ev["type"].GetValue<string>())
        finally
            server.Dispose()

    [<Fact>]
    let ``未认证业务事件被明确拒绝（protocol error）而非半工作`` () =
        // 场景 G：不得「基本能用但部分事件被静默忽略」。
        // 未认证即发业务命令 → 服务端必须明确 protocol error 关闭连接。
        let port = Wanxiang.Tests.ServerE2eTests.pickPort ()
        let token = Wanxiang.Config.Auth.generateToken ()
        let server = Wanxiang.Tests.ServerE2eTests.E2e.ServerHandle(port, token)
        try
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 20.0)
            let ct = timeout.Token
            use ws = new ClientWebSocket()
            ws.ConnectAsync(Uri(sprintf "ws://127.0.0.1:%d/ws" port), ct).GetAwaiter().GetResult()
            // 不认证直接发业务命令（conversation.create）
            let cmd = CreateConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = "x"; config = testConfig () |}
            let carrier = JsonObject()
            carrier["type"] <- "CUSTOM"
            carrier["name"] <- "wanxiang.dev/conversation.create"
            carrier["value"] <- JsonNode.Parse(WireCodec.encodeCommand cmd).AsObject()
            let bytes = Encoding.UTF8.GetBytes(carrier.ToJsonString())
            ws.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).GetAwaiter().GetResult()
            // 服务端必须关闭连接（ProtocolError），不能静默接受
            // 第一次 Receive 返回 Close 帧（状态 + 原因）；连接进入 CloseReceived。
            // 第二次 Receive 抛 WebSocketException（连接已关闭）。
            let buf = Array.zeroCreate<byte> 1024
            let mutable sawClose = false
            let mutable threw = false
            try
                let result = ws.ReceiveAsync(ArraySegment<byte>(buf), ct).GetAwaiter().GetResult()
                if result.MessageType = WebSocketMessageType.Close then
                    sawClose <- true
                // 第二次：连接已关闭 → 抛异常
                ws.ReceiveAsync(ArraySegment<byte>(buf), ct).GetAwaiter().GetResult() |> ignore
            with :? WebSocketException ->
                threw <- true
            Assert.True(sawClose || threw, "expected server to close connection with protocol error")
        finally
            server.Dispose()
