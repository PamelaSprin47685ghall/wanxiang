module Wanxiang.Tests.WsOriginTests

// 决策 280–283：WebSocket 握手 Origin/Host 检查（跨站 WebSocket 劫持、DNS 重绑定）。

open System
open System.Net.WebSockets
open System.Threading
open Xunit
open Wanxiang.Config
open Wanxiang.Tests.DevicesTests

[<Theory>]
[<InlineData("", "127.0.0.1:8765", true)>]
[<InlineData("http://127.0.0.1:8765", "127.0.0.1:8765", true)>]
[<InlineData("http://LOCALHOST:8765", "localhost:8765", true)>]
[<InlineData("https://wx.example.com", "wx.example.com", true)>]
[<InlineData("https://wx.example.com", "wx.example.com:443", true)>]
[<InlineData("http://evil.example", "127.0.0.1:8765", false)>]
[<InlineData("http://127.0.0.1:9999", "127.0.0.1:8765", false)>]
[<InlineData("null", "127.0.0.1:8765", false)>]
[<InlineData("chrome-extension://abc", "127.0.0.1:8765", false)>]
[<InlineData("http://127.0.0.1:8765", "", false)>]
let ``Origin 必须与 Host 同源`` (origin: string, host: string, expected: bool) =
    Assert.Equal(expected, WsHandshake.WsHandshake.isAllowedOrigin origin host)

[<Theory>]
[<InlineData("127.0.0.1:8765", true)>]
[<InlineData("localhost:8765", true)>]
[<InlineData("[::1]:8765", true)>]
[<InlineData("192.168.1.20:8765", true)>]
[<InlineData("rebind.evil.example:8765", false)>]
[<InlineData("wx.example.com", false)>]
[<InlineData("", false)>]
let ``Host 只放行 IP 与 localhost`` (host: string, expected: bool) =
    Assert.Equal(expected, WsHandshake.WsHandshake.isAllowedHost host [])

[<Fact>]
let ``登记过的反向代理域名放行`` () =
    Assert.True(WsHandshake.WsHandshake.isAllowedHost "wx.example.com" [ "wx.example.com" ])
    Assert.False(WsHandshake.WsHandshake.isAllowedHost "wx.example.com.evil" [ "wx.example.com" ])

let private tryConnect (port: int) (origin: string option) : bool =
    use ws = new ClientWebSocket()
    origin |> Option.iter (fun o -> ws.Options.SetRequestHeader("Origin", o))
    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
    try
        ws.ConnectAsync(Uri(sprintf "ws://127.0.0.1:%d/ws" port), cts.Token).GetAwaiter().GetResult()
        true
    with _ -> false

[<Fact>]
let ``主机拒绝跨源网页握手、放行同源与原生客户端`` () =
    let port = Wanxiang.Tests.ServerE2eTests.pickPort ()
    let s = new DeviceServer(port)
    try
        Assert.True(tryConnect port None)
        Assert.True(tryConnect port (Some(sprintf "http://127.0.0.1:%d" port)))
        Assert.False(tryConnect port (Some "http://evil.example"))
        Assert.False(tryConnect port (Some "null"))
    finally
        s.Dispose()
