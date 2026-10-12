namespace Wanxiang.Tests

open System
open System.Reflection
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Xunit
open Wanxiang.Client
open Wanxiang.Protocol
open Wanxiang.UI

/// W2（设备与配对）界面测试：连接对话框的类型化错误、设备分区、二维码、倒计时。
[<Trait("Category", "UI")>]
module DevicesUiTests =

    let rec private descendants (control: Control) : seq<Control> =
        seq {
            yield control
            match control with
            | :? Panel as panel ->
                for child in panel.Children do
                    yield! descendants child
            | :? ContentControl as host ->
                match host.Content with
                | :? Control as child -> yield! descendants child
                | _ -> ()
            | :? Decorator as decorator when not (isNull decorator.Child) -> yield! descendants decorator.Child
            | _ -> ()
        }

    let private click (control: Control) =
        control.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = control))

    let private byId (root: Control) (id: string) : Control option =
        descendants root |> Seq.tryFind (fun c -> AutomationProperties.GetAutomationId c = id)

    let private visibleButton (root: Control) (name: string) : Border option =
        descendants root
        |> Seq.tryPick (fun c ->
            match c with
            | :? Border as b when AutomationProperties.GetName b = name && b.IsVisible -> Some b
            | _ -> None)

    let private retryNames = [ "重试连接"; "重新配对"; "重试配对" ]
    let private visibleRetryButtons (root: Control) =
        retryNames |> List.choose (visibleButton root)

    let private textOf (control: Control) =
        descendants control
        |> Seq.choose (fun c ->
            match c with
            | :? TextBlock as t -> Some t.Text
            | _ -> None)
        |> String.concat "\n"

    // ---------- 验收 6：连接对话框按类型化错误决定重试 ----------

    let private openDialog () =
        let overlay = OverlayHost(Grid())
        let requests = ResizeArray<ConnectRequest>()
        let codes = ResizeArray<string>()
        let handle = Dialogs.connect overlay "ws://127.0.0.1:8765/ws" requests.Add codes.Add
        Dispatcher.UIThread.RunJobs()
        overlay, handle, requests, codes

    [<Fact>]
    let ``each typed connection error maps to its retry action regardless of wording`` () =
        Headless.run (fun () ->
            // 表驱动：期望只来自 ConnectionIssue.retry 与按钮文案表，不看错误文案
            let cases =
                [ Rejected "refused", Some "重试连接"
                  TimedOut, Some "重试连接"
                  TokenInvalid, Some "重新配对"
                  PairingFrozen 5, None
                  PairingRejected "invalid pairing code", Some "重试配对"
                  Disconnected "x", Some "重试连接" ]
            for issue, expected in cases do
                let overlay, handle, _, _ = openDialog ()
                handle.fail issue
                let visible = visibleRetryButtons overlay.Root |> List.map AutomationProperties.GetName
                match expected with
                | Some label -> Assert.Equal<string list>([ label ], visible)
                | None -> Assert.Empty visible
                // 表与实现一致（防止有人改了其中一边）
                Assert.Equal(expected, ConnectionIssue.retry issue |> Option.map Dialogs.retryLabel))

    [<Fact>]
    let ``retry visibility does not depend on message keywords`` () =
        Headless.run (fun () ->
            // 中性进度文案即使含“失败/拒绝/超时/断开/错误”也不得出现重试（旧实现靠关键词判断）
            let overlay, handle, _, _ = openDialog ()
            handle.info "这里写了失败 拒绝 超时 断开 错误，但只是普通提示"
            Assert.Empty(visibleRetryButtons overlay.Root)
            // 类型化错误的文案不含任何旧关键词，也照样出现重试
            let text = Dialogs.describeIssue (PairingRejected "x")
            for keyword in [ "失败"; "拒绝"; "断开"; "超时"; "错误" ] do
                Assert.DoesNotContain(keyword, text)
            handle.fail (PairingRejected "x")
            Assert.Equal(1, (visibleRetryButtons overlay.Root).Length)
            // 之后的中性提示会收起重试
            handle.info "配对成功，正在认证…"
            Assert.Empty(visibleRetryButtons overlay.Root))

    [<Fact>]
    let ``frozen pairing shows the minutes and offers no retry`` () =
        Headless.run (fun () ->
            let overlay, handle, _, _ = openDialog ()
            handle.fail (PairingFrozen 5)
            Assert.Contains("5 分钟", textOf overlay.Root)
            Assert.Empty(visibleRetryButtons overlay.Root))

    [<Fact>]
    let ``token invalid returns to re pairing instead of a generic connect failure`` () =
        Headless.run (fun () ->
            let overlay, handle, requests, _ = openDialog ()
            handle.fail TokenInvalid
            let button = (visibleButton overlay.Root "重新配对").Value
            Assert.Empty(visibleButton overlay.Root "重试连接" |> Option.toList)
            click button
            Assert.Equal(1, requests.Count)
            Assert.True(requests[0].usePairing)
            Assert.True(requests[0].token.IsNone))

    [<Fact>]
    let ``retry connect re issues the same connect request`` () =
        Headless.run (fun () ->
            let overlay, handle, requests, _ = openDialog ()
            handle.fail TimedOut
            click (visibleButton overlay.Root "重试连接").Value
            Assert.Equal(1, requests.Count)
            Assert.False(requests[0].usePairing)
            Assert.Equal("ws://127.0.0.1:8765/ws", requests[0].url))

    [<Fact>]
    let ``exceptions are classified into typed issues`` () =
        let refused = Net.Sockets.SocketException(int Net.Sockets.SocketError.ConnectionRefused)
        let wrapped = Net.WebSockets.WebSocketException("x", Net.Http.HttpRequestException("y", refused))
        match ConnectionIssue.ofException wrapped with
        | Rejected _ -> ()
        | other -> failwithf "expected Rejected, got %A" other
        match ConnectionIssue.ofException (TimeoutException "t") with
        | TimedOut -> ()
        | other -> failwithf "expected TimedOut, got %A" other
        match ConnectionIssue.ofException (Net.Sockets.SocketException(int Net.Sockets.SocketError.TimedOut)) with
        | TimedOut -> ()
        | other -> failwithf "expected TimedOut, got %A" other
        match ConnectionIssue.ofException (InvalidOperationException "boom") with
        | Disconnected d -> Assert.Equal("boom", d)
        | other -> failwithf "expected Disconnected, got %A" other

    // ---------- 配对页提示：本机 / 远端 ----------

    [<Fact>]
    let ``pairing hint differs between this machine and a remote host`` () =
        let local = Dialogs.pairingCodeHint "ws://127.0.0.1:8765/ws"
        let local2 = Dialogs.pairingCodeHint "ws://localhost:8765/ws"
        let remote = Dialogs.pairingCodeHint "wss://wanxiang.example.com/ws"
        Assert.Equal(local, local2)
        Assert.NotEqual<string>(local, remote)
        Assert.Contains("这台机器", local)
        Assert.Contains("远端", remote)
        for hint in [ local; remote ] do
            Assert.Contains("设备", hint)
            Assert.Contains("5 分钟", hint)

    [<Fact>]
    let ``connect dialog updates the pairing hint when the address changes`` () =
        Headless.run (fun () ->
            let overlay, _, _, _ = openDialog ()
            click (visibleButton overlay.Root "没有令牌？改用配对码").Value
            Assert.Contains(Dialogs.pairingCodeHint "ws://127.0.0.1:1/ws", textOf overlay.Root)
            let urlBox =
                descendants overlay.Root
                |> Seq.pick (fun c -> match c with :? TextBox as t when t.Text = "ws://127.0.0.1:8765/ws" -> Some t | _ -> None)
            urlBox.Text <- "wss://host.example.com/ws"
            Dispatcher.UIThread.RunJobs()
            Assert.Contains(Dialogs.pairingCodeHint "wss://host.example.com/ws", textOf overlay.Root))

    // ---------- 设备分区 ----------

    let private device id name : DeviceInfo =
        { id = id
          name = name
          createdAt = Some(DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero))
          lastSeen = None
          revoked = false
          online = false
          current = false
          desktop = false }

    type private Harness =
        { panel: SettingsDevices
          root: Control
          invites: ResizeArray<unit>
          requests: ResizeArray<unit>
          renames: ResizeArray<string * string>
          revokes: ResizeArray<DeviceInfo>
          toasts: ResizeArray<string> }

    let private harness () =
        let invites, requests = ResizeArray<unit>(), ResizeArray<unit>()
        let renames, revokes, toasts = ResizeArray<string * string>(), ResizeArray<DeviceInfo>(), ResizeArray<string>()
        let panel = SettingsDevices(OverlayHost(Grid()), DeviceActions.none)
        panel.Bind
            { request = fun () -> requests.Add()
              invite = fun () -> invites.Add()
              rename = fun id name -> renames.Add((id, name))
              revoke = revokes.Add
              toast = fun m _ -> toasts.Add m }
        panel.SetLanSource(fun () -> [])
        let root = panel.Build()
        { panel = panel; root = root; invites = invites; requests = requests; renames = renames; revokes = revokes; toasts = toasts }

    [<Fact>]
    let ``settings has a devices section in the host group`` () =
        Assert.Contains(Devices, SettingsSection.all)
        let group = SettingsSection.groups |> List.find (fun (_, sections) -> List.contains Devices sections) |> snd
        Assert.Contains(Providers, group)
        Assert.Equal("设备", SettingsSection.label Devices)
        Assert.Equal(SettingsSection.scope Providers, SettingsSection.scope Devices)

    [<Fact>]
    let ``selecting the devices section requests the list once`` () =
        Headless.run (fun () ->
            let toasts = ResizeArray<string * ToastTone>()
            let noop _ = ()
            let actions: SettingsActions =
                { upsertProvider = fun _ _ -> ()
                  deleteProvider = noop
                  probeProvider = noop
                  upsertMcp = fun _ _ -> ()
                  deleteMcp = noop
                  updateGeneration = fun _ _ -> ()
                  savePrefs = ignore
                  toast = fun m t -> toasts.Add((m, t)) }
            let view = SettingsView(OverlayHost(Grid()), actions, ignore, ignore)
            let requests = ResizeArray<unit>()
            view.Devices.Bind { DeviceActions.none with request = fun () -> requests.Add() }
            view.Build()
            Dispatcher.UIThread.RunJobs()
            Assert.Empty requests
            let nav =
                descendants view
                |> Seq.pick (fun c -> match c with :? Border as b when AutomationProperties.GetHelpText b = "设备" -> Some b | _ -> None)
            click nav
            Dispatcher.UIThread.RunJobs()
            Assert.Equal(1, requests.Count)
            Assert.True view.Devices.IsBuilt)

    [<Fact>]
    let ``device list read without a reply times out with the older host hint`` () =
        Headless.run (fun () ->
            let h = harness ()
            h.panel.Refresh()
            Assert.True h.panel.IsLoading
            h.panel.NotifyLoadTimeout()
            Assert.False h.panel.IsLoading
            Dispatcher.UIThread.RunJobs()
            Assert.Contains(descendants h.root, fun c -> match c with :? TextBlock as t -> t.Text.Contains "主机版本较旧" | _ -> false))

    [<Fact>]
    let ``device list marks current desktop online and revoked and sorts revoked last`` () =
        Headless.run (fun () ->
            let h = harness ()
            let now = DateTimeOffset.UtcNow
            let items =
                [ { device "dev-revoked" "旧手机" with revoked = true }
                  { device "dev-phone" "手机" with online = true }
                  { device "dev-desktop" "wanxiang-desktop" with desktop = true; current = true; online = true }
                  { device "dev-pad" "平板" with lastSeen = Some(now - TimeSpan.FromMinutes 5.0) } ]
            h.panel.SetDevices(items, true)
            let rows = (byId h.root "devices-list").Value
            let rowIds =
                descendants rows
                |> Seq.choose (fun c -> let id = AutomationProperties.GetAutomationId c in if not (String.IsNullOrEmpty id) && id.StartsWith "devices-row-" then Some id else None)
                |> Seq.toList
            Assert.Equal<string list>([ "devices-row-dev-desktop"; "devices-row-dev-phone"; "devices-row-dev-pad"; "devices-row-dev-revoked" ], rowIds)
            let text = textOf rows
            Assert.Contains("这台设备", text)
            Assert.Contains("桌面", text)
            Assert.Contains("在线", text)
            Assert.Contains("已吊销", text)
            Assert.Contains("5 分钟前在线", text)
            // 协议保留名不直接给人看
            Assert.Contains("本机桌面客户端", text)
            // 当前设备与桌面设备没有吊销按钮；已吊销的没有任何按钮
            Assert.True((byId h.root "devices-revoke-dev-desktop").IsNone)
            Assert.True((byId h.root "devices-revoke-dev-phone").IsSome)
            Assert.True((byId h.root "devices-revoke-dev-pad").IsSome)
            Assert.True((byId h.root "devices-revoke-dev-revoked").IsNone)
            Assert.True((byId h.root "devices-rename-dev-revoked").IsNone)
            // 桌面设备也不能重命名
            Assert.True((byId h.root "devices-rename-dev-desktop").IsNone)
            Assert.True((byId h.root "devices-rename-dev-phone").IsSome))

    [<Fact>]
    let ``revoke button passes the device to the action`` () =
        Headless.run (fun () ->
            let h = harness ()
            h.panel.SetDevices([ device "dev-phone" "手机" ], true)
            click (byId h.root "devices-revoke-dev-phone").Value
            Assert.Equal(1, h.revokes.Count)
            Assert.Equal("dev-phone", h.revokes[0].id))

    [<Fact>]
    let ``rename edits inline trims and sends the new name`` () =
        Headless.run (fun () ->
            let h = harness ()
            h.panel.SetDevices([ device "dev-phone" "手机" ], true)
            click (byId h.root "devices-rename-dev-phone").Value
            let box = (byId h.root "devices-name-dev-phone").Value :?> TextBox
            Assert.Equal("手机", box.Text)
            // 空名：提示且不发送
            box.Text <- "   "
            click (byId h.root "devices-name-save-dev-phone").Value
            Assert.Empty h.renames
            Assert.Equal(1, h.toasts.Count)
            box.Text <- "  书房平板 "
            click (byId h.root "devices-name-save-dev-phone").Value
            Assert.Equal<(string * string) list>([ "dev-phone", "书房平板" ], List.ofSeq h.renames)
            // 取消路径
            h.panel.SetDevices([ device "dev-phone" "书房平板" ], true)
            click (byId h.root "devices-rename-dev-phone").Value
            click (byId h.root "devices-name-cancel-dev-phone").Value
            Assert.True((byId h.root "devices-name-dev-phone").IsNone)
            Assert.Equal(1, h.renames.Count))

    [<Fact>]
    let ``only a desktop capable connection sees the add device button`` () =
        Headless.run (fun () ->
            let h = harness ()
            h.panel.SetConnection "ws://192.168.1.5:8765/ws"
            h.panel.SetDevices([ device "a" "A" ], false)
            Assert.True((byId h.root "devices-add").IsNone)
            Assert.True((byId h.root "devices-invite-unavailable").IsSome)
            h.panel.SetDevices([ device "a" "A" ], true)
            Assert.True((byId h.root "devices-add").IsSome)
            click (byId h.root "devices-add").Value
            Assert.Equal(1, h.invites.Count))

    [<Fact>]
    let ``invite card shows grouped code address qr and countdown`` () =
        Headless.run (fun () ->
            let h = harness ()
            h.panel.SetConnection "ws://192.168.1.5:8765/ws"
            h.panel.SetDevices([], true)
            let now = DateTimeOffset.UtcNow
            h.panel.SetInvite("012345", now + TimeSpan.FromMinutes 5.0, now)
            Assert.Equal("012 345", (byId h.root "devices-code").Value |> textOf)
            Assert.Equal("5:00 后失效", (byId h.root "devices-countdown").Value |> textOf)
            Assert.Equal("http://192.168.1.5:8765/", (byId h.root "devices-address").Value |> fun c -> (c :?> TextBlock).Text)
            let qr = (byId h.root "devices-qr").Value :?> QrCodeView
            Assert.Equal("http://192.168.1.5:8765/", qr.Text)
            h.panel.Tick(now + TimeSpan.FromSeconds 61.0)
            Assert.Equal("3:59 后失效", (byId h.root "devices-countdown").Value |> textOf)
            h.panel.Tick(now + TimeSpan.FromMinutes 5.0 + TimeSpan.FromSeconds 1.0)
            Assert.Equal("已过期", (byId h.root "devices-countdown").Value |> textOf)
            // 过期后“重新生成”再次请求邀请
            click (byId h.root "devices-regenerate").Value
            Assert.Equal(1, h.invites.Count)
            // 完成：关闭配对码卡片，回到“添加设备”
            click (byId h.root "devices-close-invite").Value
            Assert.True((byId h.root "devices-add").IsSome)
            Assert.True((byId h.root "devices-code").IsNone))

    [<Fact>]
    let ``loopback only host explains the listen setting and uses a lan address for the qr`` () =
        Headless.run (fun () ->
            let h = harness ()
            h.panel.SetLanSource(fun () -> [ "192.168.1.9"; "10.0.0.4" ])
            h.panel.SetConnection "ws://127.0.0.1:8765/ws"
            h.panel.SetDevices([], true)
            let now = DateTimeOffset.UtcNow
            h.panel.SetInvite("111111", now + TimeSpan.FromMinutes 5.0, now)
            let text = textOf h.root
            Assert.Contains("listen", text)
            Assert.Contains("其他网卡地址：http://10.0.0.4:8765/", text)
            Assert.Equal("http://192.168.1.9:8765/", ((byId h.root "devices-qr").Value :?> QrCodeView).Text)
            // 没有局域网地址：只给说明，不画二维码
            let h2 = harness ()
            h2.panel.SetConnection "ws://localhost:8765/ws"
            h2.panel.SetDevices([], true)
            h2.panel.SetInvite("222222", now + TimeSpan.FromMinutes 5.0, now)
            Assert.True((byId h2.root "devices-qr").IsNone)
            Assert.Contains("listen", textOf h2.root))

    [<Fact>]
    let ``rejected invite explains that only the desktop app can add devices`` () =
        Headless.run (fun () ->
            let h = harness ()
            h.panel.SetDevices([], true)
            h.panel.SetInviteRejected "not-desktop"
            Assert.Contains("桌面应用", textOf h.root)
            Assert.True((byId h.root "devices-code").IsNone))

    [<Fact>]
    let ``a disconnect while loading explains the host may be too old`` () =
        Headless.run (fun () ->
            let h = harness ()
            h.panel.Refresh()
            Assert.Equal(1, h.requests.Count)
            Assert.True h.panel.IsLoading
            h.panel.NotifyDisconnected()
            Assert.False h.panel.IsLoading
            Assert.Contains("版本较旧", textOf h.root))

    // ---------- 二维码与地址（纯逻辑）----------

    [<Fact>]
    let ``qr matrix is a square with finder patterns and is deterministic`` () =
        let a = DevicesModel.qrMatrix "http://192.168.1.5:8765/"
        let b = DevicesModel.qrMatrix "http://192.168.1.5:8765/"
        Assert.True(a.Length >= 21)
        Assert.All(a, fun row -> Assert.Equal(a.Length, row.Length))
        Assert.Equal<bool[][]>(a, b)
        // 三个定位图案：7x7 外框为深色
        let n = a.Length
        for (r0, c0) in [ 0, 0; 0, n - 7; n - 7, 0 ] do
            for i in 0 .. 6 do
                Assert.True(a[r0][c0 + i])
                Assert.True(a[r0 + 6][c0 + i])
                Assert.True(a[r0 + i][c0])
                Assert.True(a[r0 + i][c0 + 6])
            // 中心 3x3 实心
            Assert.True(a[r0 + 3][c0 + 3])
        Assert.NotEqual<bool[][]>(a, DevicesModel.qrMatrix "http://192.168.1.6:8765/")

    [<Fact>]
    let ``qr view measures with a quiet zone and draws on a white background`` () =
        Headless.run (fun () ->
            let view = QrCodeView("http://h/", 4.0)
            view.Measure(Size(1000.0, 1000.0))
            let side = float (view.ModuleCount + 8) * 4.0
            Assert.Equal(side, view.DesiredSize.Width)
            Assert.Equal(side, view.DesiredSize.Height))

    [<Fact>]
    let ``invite target follows the host address`` () =
        Assert.Equal(Reachable "http://192.168.1.5:8765/", DevicesModel.inviteTarget "ws://192.168.1.5:8765/ws" [ "1.2.3.4" ])
        Assert.Equal(Reachable "https://wx.example.com/", DevicesModel.inviteTarget "wss://wx.example.com/ws" [])
        Assert.Equal(LoopbackOnly [ "http://1.2.3.4:8765/" ], DevicesModel.inviteTarget "ws://127.0.0.1:8765/ws" [ "1.2.3.4" ])
        Assert.Equal(LoopbackOnly [], DevicesModel.inviteTarget "ws://localhost:8765/ws" [])
        Assert.Equal(NoAddress, DevicesModel.inviteTarget "" [])
        Assert.Equal(NoAddress, DevicesModel.inviteTarget "not a url" [])

    [<Fact>]
    let ``countdown and code grouping formats`` () =
        Assert.Equal("5:00", DevicesModel.countdown (TimeSpan.FromMinutes 5.0))
        Assert.Equal("0:01", DevicesModel.countdown (TimeSpan.FromSeconds 0.2))
        Assert.Equal("0:00", DevicesModel.countdown (TimeSpan.FromSeconds -3.0))
        Assert.Equal("1:05", DevicesModel.countdown (TimeSpan.FromSeconds 65.0))
        Assert.Equal("012 345", DevicesModel.groupCode "012345")
        Assert.Equal("12", DevicesModel.groupCode "12")

    [<Fact>]
    let ``device snapshot parsing is tolerant`` () =
        let items = JsonArray()
        items.Add(JsonNode.Parse """{"id":"a1","name":"手机","createdAt":"2026-10-01T08:00:00.0000000Z","lastSeen":"2026-10-12T01:00:00Z","revoked":false,"online":true,"current":true,"desktop":false,"future":1}""")
        items.Add(JsonNode.Parse """{"id":"b2"}""")
        items.Add(JsonNode.Parse "42")
        let parsed = DevicesModel.parse items
        Assert.Equal(2, parsed.Length)
        Assert.True(parsed[0].online && parsed[0].current && parsed[0].createdAt.IsSome && parsed[0].lastSeen.IsSome)
        Assert.Equal("", parsed[1].name)
        Assert.True(parsed[1].createdAt.IsNone && not parsed[1].revoked)

    // ---------- 整体接线：AppShell ----------

    let private handleEventMethod =
        lazy (typeof<MainView>.GetMethod("HandleEvent", BindingFlags.NonPublic ||| BindingFlags.Instance))

    let private dispatch (view: MainView) (ev: WireEvent) =
        handleEventMethod.Value.Invoke(view, [| box ev |]) |> ignore
        Dispatcher.UIThread.RunJobs()

    let private field<'T> (view: MainView) (name: string) : 'T =
        typeof<MainView>.GetField(name, BindingFlags.NonPublic ||| BindingFlags.Instance).GetValue(view) :?> 'T

    let private shell () =
        MotionPolicy.setReduced true
        let view = MainView()
        view.Build()
        let window = Window(Width = 1000.0, Height = 800.0, Content = view)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        view, window

    let private showConnectDialog (view: MainView) =
        typeof<MainView>.GetMethod("ShowConnectDialog", BindingFlags.NonPublic ||| BindingFlags.Instance).Invoke(view, [||]) |> ignore
        Dispatcher.UIThread.RunJobs()

    [<Fact>]
    let ``rejected token reopens the dialog on re pairing`` () =
        Headless.run (fun () ->
            let view, _ = shell ()
            dispatch view (AuthRejected {| reason = "invalid token" |})
            showConnectDialog view
            let overlay = field<OverlayHost> view "overlay"
            Assert.Equal<string list>([ "重新配对" ], visibleRetryButtons overlay.Root |> List.map AutomationProperties.GetName))

    [<Fact>]
    let ``pairing failures map to retry pairing or frozen without retry`` () =
        Headless.run (fun () ->
            let view, _ = shell ()
            dispatch view (PairingFailed {| reason = "invalid pairing code"; frozen = false; freezeMinutes = 0 |})
            showConnectDialog view
            let overlay = field<OverlayHost> view "overlay"
            Assert.Equal<string list>([ "重试配对" ], visibleRetryButtons overlay.Root |> List.map AutomationProperties.GetName)
            dispatch view (PairingFailed {| reason = "frozen"; frozen = true; freezeMinutes = 5 |})
            Assert.Empty(visibleRetryButtons overlay.Root)
            Assert.Contains("5 分钟", textOf overlay.Root))

    [<Fact>]
    let ``desktop local connection never shows a pairing dialog or code`` () =
        Headless.run (fun () ->
            let view, _ = shell ()
            let overlay = field<OverlayHost> view "overlay"
            // 桌面本机已有令牌：连接后直接 auth.accepted，不经过 pairing.*，界面不弹任何对话框
            dispatch view (AuthAccepted {| instanceId = Guid.NewGuid().ToString "D" |})
            Assert.False overlay.IsDialogOpen
            Assert.True(field<ConnectionIssue option> view "connectionIssue" |> Option.isNone)
            Assert.DoesNotContain("配对码", textOf overlay.Root))

    [<Fact>]
    let ``device events reach the devices panel through the shell`` () =
        Headless.run (fun () ->
            let view, _ = shell ()
            let settings = field<SettingsView> view "settings"
            let items = JsonArray()
            items.Add(JsonNode.Parse """{"id":"p1","name":"手机","revoked":false,"online":true,"current":false,"desktop":false}""")
            dispatch view (DevicesSnapshot {| items = items; canInvite = true |})
            Assert.Equal(1, settings.Devices.Devices.Length)
            Assert.True settings.Devices.CanInvite
            let expires = DateTimeOffset.UtcNow + TimeSpan.FromMinutes 5.0
            dispatch view (PairingInviteIssued {| code = "654321"; expiresAtUtc = expires; expiresInSeconds = 300 |})
            Assert.Equal(Some "654321", settings.Devices.CurrentInvite |> Option.map (fun i -> i.code))
            dispatch view (PairingInviteRejected {| reason = "not-desktop" |})
            Assert.True settings.Devices.CurrentInvite.IsNone)
