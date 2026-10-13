module Wanxiang.Tests.R27HostCapabilitiesTests

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.Config
open Wanxiang.Protocol
open Wanxiang.Client
open Wanxiang.UI

// 决策 440–444：主机能力声明；旧主机静默丢弃新请求时，客户端到点退出等待并说明原因。

[<Fact>]
let ``host capabilities roundtrip through codec`` () =
    let ev = HostCapabilities {| hostVersion = "1.2.3"; features = HostFeatures.all |}
    match WireCodec.tryDecode (WireCodec.encode ev) with
    | Ok(HostCapabilities d) ->
        Assert.Equal("1.2.3", d.hostVersion)
        Assert.Equal<string list>(HostFeatures.all, d.features)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``unknown capabilities mean supported; declared table gates`` () =
    Assert.True(HostFeatures.supports None HostFeatures.Presets)
    Assert.False(HostFeatures.supports (Some(Set.ofList [ HostFeatures.Search ])) HostFeatures.Presets)
    Assert.True(HostFeatures.supports (Some(Set.ofList HostFeatures.all)) HostFeatures.Presets)
    Assert.Equal("主机版本较旧，不支持对话预设。请更新主机后再试。", HostFeatures.unsupportedMessage HostFeatures.Presets)
    Assert.Contains("版本可能较旧", HostFeatures.noResponseMessage None)
    Assert.DoesNotContain("版本", HostFeatures.noResponseMessage (Some Set.empty))

[<Fact>]
let ``config tracker expires silent requests and resolves only once`` () =
    let tracker = ConfigFeedbackTracker()
    let id = Guid.NewGuid()
    let mutable result = None
    tracker.Track(id, "ok", fun ok -> result <- Some ok)
    Assert.Empty(tracker.Expire(DateTimeOffset.UtcNow, TimeSpan.FromSeconds 12.0))
    let expired = tracker.Expire(DateTimeOffset.UtcNow.AddSeconds 13.0, TimeSpan.FromSeconds 12.0)
    Assert.Equal(1, expired.Length)
    expired |> List.iter (fun f -> f.onCompleted false)
    Assert.Equal(Some false, result)
    Assert.True((tracker.Resolve id).IsNone)
    Assert.Equal(0, tracker.Count)

[<Fact>]
let ``command tracker expires newer commands but leaves message delivery to outbox`` () =
    let tracker = CommandFeedbackTracker()
    let cont = ContinueResponse {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); messageJson = System.Text.Json.Nodes.JsonObject() |}
    let send = SendUserMessage {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); messageJson = System.Text.Json.Nodes.JsonObject() |}
    tracker.Track(cont, None, ignore)
    tracker.Track(send, None, ignore)
    let expired = tracker.Expire(DateTimeOffset.UtcNow.AddMinutes 1.0, TimeSpan.FromSeconds 12.0)
    Assert.Equal(1, expired.Length)
    Assert.Equal(1, tracker.Count)

[<Fact>]
let ``host announces capabilities right after authentication`` () =
    let port = ServerE2eTests.pickPort ()
    let token = Auth.generateToken ()
    let handle = ServerE2eTests.E2e.ServerHandle(port, token)
    try
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
        let client = new WsClient()
        try
            client.ConnectAsync(Uri(sprintf "ws://127.0.0.1:%d/ws" port), cts.Token).GetAwaiter().GetResult()
            let caps = TaskCompletionSource<string list>()
            use _sub =
                client.EventReceived.Subscribe(fun ev ->
                    match ev with
                    | HostCapabilities d -> caps.TrySetResult d.features |> ignore
                    | _ -> ())
            client.TrySendAsync(AuthPresent {| token = token |}).GetAwaiter().GetResult() |> ignore
            Assert.True(caps.Task.Wait(TimeSpan.FromSeconds 5.0))
            Assert.Contains(HostFeatures.Presets, caps.Task.Result)
            Assert.Contains(HostFeatures.ContinueResponse, caps.Task.Result)
        finally
            client.Disconnect()
    finally
        handle.Dispose()

let private flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

let private shell () =
    MotionPolicy.setReduced true
    let view = MainView()
    view.Build()
    let window = Window(Width = 1000.0, Height = 700.0, Content = view)
    window.Show()
    typeof<MainView>.GetField("authenticated", flags).SetValue(view, true)
    view, window

let private handle (view: MainView) (ev: WireEvent) =
    typeof<MainView>.GetMethod("HandleEvent", flags).Invoke(view, [| box ev |]) |> ignore
    Dispatcher.UIThread.RunJobs()

let private requireFeature (view: MainView) (f: string) =
    typeof<MainView>.GetMethod("RequireFeature", flags).Invoke(view, [| box f |]) :?> bool

[<Trait("Category", "UI")>]
[<Fact>]
let ``declared host without presets blocks preset requests up front`` () =
    Headless.run (fun () ->
    let view, window = shell ()
    Assert.True(requireFeature view HostFeatures.Presets) // 能力未知：照常
    handle view (HostCapabilities {| hostVersion = "0.9"; features = [ HostFeatures.Search ] |})
    Assert.False(requireFeature view HostFeatures.Presets)
    Assert.False(requireFeature view HostFeatures.ContinueResponse)
    Assert.True(requireFeature view HostFeatures.Search)
    handle view (HostCapabilities {| hostVersion = "1.0"; features = HostFeatures.all |})
    Assert.True(requireFeature view HostFeatures.Presets)
    window.Close())

[<Trait("Category", "UI")>]
[<Fact>]
let ``old host that never answers a preset save releases the dialog after the timeout`` () =
    Headless.run (fun () ->
    let view, window = shell ()
    let tracker = typeof<MainView>.GetField("configFeedback", flags).GetValue(view) :?> ConfigFeedbackTracker
    let mutable completed = None
    tracker.Track(Guid.NewGuid(), "已存为预设", fun ok -> completed <- Some ok)
    let expire (at: DateTimeOffset) =
        typeof<MainView>.GetMethod("ExpireHostRequests", flags).Invoke(view, [| box at |]) |> ignore
        Dispatcher.UIThread.RunJobs()
    expire (DateTimeOffset.UtcNow.AddSeconds 1.0)
    Assert.Equal(None, completed)
    expire (DateTimeOffset.UtcNow + MotionLedger.hostRequestTimeout + TimeSpan.FromSeconds 1.0)
    Assert.Equal(Some false, completed)
    Assert.Equal(0, tracker.Count)
    window.Close())

[<Trait("Category", "UI")>]
[<Fact>]
let ``timers start even when the view is attached before Build like the PWA does`` () =
    Headless.run (fun () ->
    MotionPolicy.setReduced true
    let view = MainView()
    let window = Window(Width = 900.0, Height = 600.0, Content = view)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    view.Build()
    Dispatcher.UIThread.RunJobs()
    let started = typeof<MainView>.GetField("timersStarted", flags).GetValue(view) :?> bool
    Assert.True(started)
    window.Close())
