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
open Wanxiang2.SettingsScreen
open Wanxiang2.DevicesPane
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
                [ ConnectionIssue.Rejected "refused", Some "重试连接"
                  ConnectionIssue.TimedOut, Some "重试连接"
                  ConnectionIssue.TokenInvalid, Some "重新配对"
                  ConnectionIssue.PairingFrozen 5, None
                  ConnectionIssue.PairingRejected "pairing code expired", Some "重试配对"
                  ConnectionIssue.Disconnected "x", Some "重试连接" ]
            for issue, expected in cases do
                let overlay, handle, _, _ = openDialog ()
                handle.fail issue
                let visible = visibleRetryButtons overlay.Root |> List.map AutomationProperties.GetName
                match expected with
                | Some label -> Assert.Equal<string list>([ label ], visible)
                | None -> Assert.Empty visible
                // 表与实现一致（防止有人改了其中一边）
                Assert.Equal(expected, ConnectionIssue.retry issue |> Option.map ConnectIssueText.retryLabel))

    [<Fact>]
    let ``wrong pairing code keeps a single primary action and no retry button`` () =
        Headless.run (fun () ->
            // 决策 285：码输错时会话仍有效，「提交配对码」就是下一步，不再并排「重试配对」
            let overlay, handle, _, _ = openDialog ()
            handle.fail (ConnectionIssue.PairingRejected "invalid pairing code")
            Dispatcher.UIThread.RunJobs()
            Assert.Empty(visibleRetryButtons overlay.Root)
            Assert.Contains("配对码不对", textOf overlay.Root))

    [<Fact>]
    let ``retry visibility does not depend on message keywords`` () =
        Headless.run (fun () ->
            // 中性进度文案即使含“失败/拒绝/超时/断开/错误”也不得出现重试（旧实现靠关键词判断）
            let overlay, handle, _, _ = openDialog ()
            handle.info "这里写了失败 拒绝 超时 断开 错误，但只是普通提示"
            Assert.Empty(visibleRetryButtons overlay.Root)
            // 类型化错误的文案不含任何旧关键词，也照样出现重试
            let text = ConnectIssueText.describe (ConnectionIssue.PairingRejected "x")
            for keyword in [ "失败"; "拒绝"; "断开"; "超时"; "错误" ] do
                Assert.DoesNotContain(keyword, text)
            handle.fail (ConnectionIssue.PairingRejected "x")
            Assert.Equal(1, (visibleRetryButtons overlay.Root).Length)
            // 之后的中性提示会收起重试
            handle.info "配对成功，正在认证…"
            Assert.Empty(visibleRetryButtons overlay.Root))

    [<Fact>]
    let ``frozen pairing shows the minutes and offers no retry`` () =
        Headless.run (fun () ->
            let overlay, handle, _, _ = openDialog ()
            handle.fail (ConnectionIssue.PairingFrozen 5)
            Assert.Contains("5 分钟", textOf overlay.Root)
            Assert.Empty(visibleRetryButtons overlay.Root))

    [<Fact>]
    let ``token invalid returns to re pairing instead of a generic connect failure`` () =
        Headless.run (fun () ->
            let overlay, handle, requests, _ = openDialog ()
            handle.fail ConnectionIssue.TokenInvalid
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
            handle.fail ConnectionIssue.TimedOut
            click (visibleButton overlay.Root "重试连接").Value
            Assert.Equal(1, requests.Count)
            Assert.False(requests[0].usePairing)
            Assert.Equal("ws://127.0.0.1:8765/ws", requests[0].url))

    // ---------- 配对页提示：本机 / 远端 ----------

    [<Fact>]
    let ``connect dialog updates the pairing hint when the address changes`` () =
        Headless.run (fun () ->
            let overlay, _, _, _ = openDialog ()
            click (visibleButton overlay.Root "没有令牌？改用配对码").Value
            Assert.Contains(PairingHint.codeHint "ws://127.0.0.1:1/ws", textOf overlay.Root)
            let urlBox =
                descendants overlay.Root
                |> Seq.pick (fun c -> match c with :? TextBox as t when t.Text = "ws://127.0.0.1:8765/ws" -> Some t | _ -> None)
            urlBox.Text <- "wss://host.example.com/ws"
            Dispatcher.UIThread.RunJobs()
            Assert.Contains(PairingHint.codeHint "wss://host.example.com/ws", textOf overlay.Root))

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
        { panel: DevicesDesk
          root: Control
          invites: ResizeArray<unit>
          requests: ResizeArray<unit>
          renames: ResizeArray<string * string>
          revokes: ResizeArray<DeviceInfo>
          toasts: ResizeArray<string> }

    let private harness () =
        let invites, requests = ResizeArray<unit>(), ResizeArray<unit>()
        let renames, revokes, toasts = ResizeArray<string * string>(), ResizeArray<DeviceInfo>(), ResizeArray<string>()
        let panel = DevicesDesk(SharedTheme.manager, OverlayHost(Grid()).Desk)
        panel.ListRequested.Add(fun () -> requests.Add())
        panel.InviteRequested.Add(fun () -> invites.Add())
        panel.RenameRequested.Add(fun (id, name) -> renames.Add((id, name)))
        panel.RevokeRequested.Add revokes.Add
        panel.SetLanSource(fun () -> [])
        let root = panel.View
        { panel = panel; root = root; invites = invites; requests = requests; renames = renames; revokes = revokes; toasts = toasts }

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
    let ``invite target follows the host address`` () =
        Assert.Equal(InviteTarget.Reachable "http://192.168.1.5:8765/", DevicesModel.inviteTarget "ws://192.168.1.5:8765/ws" [ "1.2.3.4" ])
        Assert.Equal(InviteTarget.Reachable "https://wx.example.com/", DevicesModel.inviteTarget "wss://wx.example.com/ws" [])
        Assert.Equal(InviteTarget.LoopbackOnly [ "http://1.2.3.4:8765/" ], DevicesModel.inviteTarget "ws://127.0.0.1:8765/ws" [ "1.2.3.4" ])
        Assert.Equal(InviteTarget.LoopbackOnly [], DevicesModel.inviteTarget "ws://localhost:8765/ws" [])
        Assert.Equal(InviteTarget.NoAddress, DevicesModel.inviteTarget "" [])
        Assert.Equal(InviteTarget.NoAddress, DevicesModel.inviteTarget "not a url" [])

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
            dispatch view (PairingFailed {| reason = "pairing code expired"; frozen = false; freezeMinutes = 0 |})
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
            let settings = field<SettingsScreen> view "settings"
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
