namespace Wanxiang.UI

open System
open System.Net.NetworkInformation
open System.Net.Sockets
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open QRCoder

/// 一台已授权设备（来自 `wanxiang.dev/devices.snapshot`）。令牌哈希不出主机，`id` 是截断的稳定标识。
type DeviceInfo = {
    id: string
    name: string
    createdAt: DateTimeOffset option
    lastSeen: DateTimeOffset option
    revoked: bool
    online: bool
    /// 当前这条连接就是它
    current: bool
    /// 本机桌面客户端（保留名；不能在这里重命名或吊销）
    desktop: bool
}

/// 配对邀请：码 + 到期时间。只在内存，不入日志。
type PairingInviteView = {
    code: string
    expiresAtUtc: DateTimeOffset
}

/// 手机应该打开哪个地址。
type InviteTarget =
    /// 主机地址对外可达：直接用它。
    | Reachable of url: string
    /// 主机只监听本机回环地址：手机无法直接访问；候选是这台机器的局域网地址（可能为空）。
    | LoopbackOnly of candidates: string list
    /// 还没有连接地址。
    | NoAddress

/// 设备分区向外发出的动作。全部落到协议上，界面不直接碰 TOML。
type DeviceActions = {
    /// 请求设备列表快照。
    request: unit -> unit
    /// 请求配对邀请（仅桌面本机客户端会被主机接受）。
    invite: unit -> unit
    /// deviceId → 新名称
    rename: string -> string -> unit
    /// deviceId → 吊销（确认由调用方负责）
    revoke: DeviceInfo -> unit
    toast: string -> ToastTone -> unit
}

module DeviceActions =
    let none : DeviceActions =
        { request = ignore
          invite = ignore
          rename = fun _ _ -> ()
          revoke = ignore
          toast = fun _ _ -> () }

module DevicesModel =

    /// 邀请有效期（决策 46）：与主机一致，仅用于兜底显示；以主机下发的到期时间为准。
    let inviteLifetime = TimeSpan.FromMinutes 5.0

    let private tryTime (o: JsonObject) (key: string) : DateTimeOffset option =
        match o[key] with
        | :? JsonValue as v ->
            match v.TryGetValue<string>() with
            | true, raw ->
                match DateTimeOffset.TryParse(raw, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind) with
                | true, t -> Some t
                | _ -> None
            | _ -> None
        | _ -> None

    let private tryBool (o: JsonObject) (key: string) : bool =
        match o[key] with
        | :? JsonValue as v ->
            match v.TryGetValue<bool>() with
            | true, b -> b
            | _ -> false
        | _ -> false

    let private tryString (o: JsonObject) (key: string) : string =
        match o[key] with
        | :? JsonValue as v ->
            match v.TryGetValue<string>() with
            | true, s -> s
            | _ -> ""
        | _ -> ""

    /// 解析设备快照。未知字段忽略，缺字段取保守默认值（宽容读）。
    let parse (items: JsonArray) : DeviceInfo list =
        [ for item in items do
              match item with
              | :? JsonObject as o ->
                  yield
                      { id = tryString o "id"
                        name = tryString o "name"
                        createdAt = tryTime o "createdAt"
                        lastSeen = tryTime o "lastSeen"
                        revoked = tryBool o "revoked"
                        online = tryBool o "online"
                        current = tryBool o "current"
                        desktop = tryBool o "desktop" }
              | _ -> () ]

    /// 剩余时间 → `m:ss`；过期（≤ 0）显示 `0:00`。
    let countdown (remaining: TimeSpan) : string =
        let total = max 0 (int (Math.Ceiling remaining.TotalSeconds))
        sprintf "%d:%02d" (total / 60) (total % 60)

    /// 6 位码在界面上分成 3+3，便于口述和核对；复制与校验仍用原值。
    let groupCode (code: string) : string =
        if not (isNull code) && code.Length = 6 then code.Substring(0, 3) + " " + code.Substring 3 else code

    /// 设备行的“最后在线”文案。
    let lastSeenText (now: DateTimeOffset) (device: DeviceInfo) : string =
        if device.revoked then "已吊销"
        elif device.online then "在线"
        else
            match device.lastSeen with
            | None -> "从未连接"
            | Some t ->
                let age = now - t
                if age < TimeSpan.FromMinutes 1.0 then "刚刚在线"
                elif age < TimeSpan.FromHours 1.0 then sprintf "%d 分钟前在线" (int age.TotalMinutes)
                elif age < TimeSpan.FromDays 1.0 then sprintf "%d 小时前在线" (int age.TotalHours)
                else sprintf "%s 在线" (t.ToLocalTime().ToString("yyyy-MM-dd"))

    let createdText (device: DeviceInfo) : string =
        match device.createdAt with
        | Some t -> sprintf "加入于 %s" (t.ToLocalTime().ToString("yyyy-MM-dd"))
        | None -> "加入时间未知"

    /// ws(s)://host:port/ws → http(s)://host:port/（同源 PWA 的入口）。
    let httpOrigin (wsUrl: string) : string option =
        match Uri.TryCreate((if isNull wsUrl then "" else wsUrl.Trim()), UriKind.Absolute) with
        | true, uri when uri.Scheme = "ws" || uri.Scheme = "wss" ->
            let scheme = if uri.Scheme = "wss" then "https" else "http"
            Some(UriBuilder(scheme, uri.Host, uri.Port, "/").Uri.ToString())
        | _ -> None

    let isLoopbackHost (wsUrl: string) : bool =
        match Uri.TryCreate((if isNull wsUrl then "" else wsUrl.Trim()), UriKind.Absolute) with
        | true, uri -> uri.IsLoopback || uri.Host = "localhost"
        | _ -> false

    /// 本机的局域网 IPv4 地址（桌面端；浏览器或受限环境返回空）。
    let lanAddresses () : string list =
        try
            [ for nic in NetworkInterface.GetAllNetworkInterfaces() do
                  if nic.OperationalStatus = OperationalStatus.Up && nic.NetworkInterfaceType <> NetworkInterfaceType.Loopback then
                      for ua in nic.GetIPProperties().UnicastAddresses do
                          if ua.Address.AddressFamily = AddressFamily.InterNetwork then
                              yield ua.Address.ToString() ]
            |> List.distinct
        with _ -> []

    /// 手机该打开的地址。`lan` 注入便于测试。
    let inviteTarget (wsUrl: string) (lan: string list) : InviteTarget =
        match httpOrigin wsUrl with
        | None -> NoAddress
        | Some origin ->
            if isLoopbackHost wsUrl then
                let port = (Uri origin).Port
                let scheme = (Uri origin).Scheme
                LoopbackOnly [ for ip in lan -> UriBuilder(scheme, ip, port, "/").Uri.ToString() ]
            else Reachable origin

    /// 二维码模块矩阵（true = 深色模块）。使用成熟库 QRCoder 编码，不自行实现（规范 58）。
    let qrMatrix (text: string) : bool[][] =
        use generator = new QRCodeGenerator()
        use data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M)
        // QRCoder 的 ModuleMatrix 自带 4 模块静区；这里去掉，由绘制层统一补静区（QrCodeView）。
        let quiet = 4
        let size = data.ModuleMatrix.Count - quiet * 2
        [| for r in 0 .. size - 1 -> [| for c in 0 .. size - 1 -> data.ModuleMatrix[r + quiet][c + quiet] |] |]

/// 画二维码的控件：白底深色模块、四周静区（4 模块），与主题无关（扫码器要对比度）。
type QrCodeView(text: string, moduleSize: float) =
    inherit Control()

    let matrix = DevicesModel.qrMatrix text
    let quiet = 4
    let modules = matrix.Length + quiet * 2

    member _.Text = text
    member _.ModuleCount = matrix.Length
    member _.IsDark(row: int, col: int) : bool = matrix[row][col]

    override _.MeasureOverride(_) =
        let side = float modules * moduleSize
        Size(side, side)

    override _.Render(context) =
        let side = float modules * moduleSize
        context.FillRectangle(Brushes.White, Rect(0.0, 0.0, side, side))
        for r in 0 .. matrix.Length - 1 do
            for c in 0 .. matrix.Length - 1 do
                if matrix[r][c] then
                    context.FillRectangle(
                        Brushes.Black,
                        Rect(float (c + quiet) * moduleSize, float (r + quiet) * moduleSize, moduleSize, moduleSize))

/// 设置 → 设备：添加设备（码 + 地址 + 二维码 + 倒计时）、设备列表、重命名、吊销。
///
/// 邀请码只存在于内存和这块界面里：不入日志、不写文件、不上传。
type SettingsDevices(overlay: OverlayHost, initialActions: DeviceActions) =

    let mutable actions = initialActions
    let mutable devices: DeviceInfo list = []
    let mutable canInvite = false
    let mutable invite: PairingInviteView option = None
    let mutable inviteRejected: string option = None
    let mutable loading = false
    let mutable loadNote: string option = None
    let mutable wsUrl = ""
    let mutable lan: unit -> string list = DevicesModel.lanAddresses
    let mutable now: DateTimeOffset = DateTimeOffset.UtcNow
    let mutable editingId: string option = None
    let mutable timer: DispatcherTimer option = None
    let mutable built = false
    let mutable refresh: unit -> unit = ignore

    let root = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.space4xl)
    let inviteHost = ContentControl()
    let listHost = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceMd)

    let tag (control: Control) (label: string) (id: string) =
        AutomationProperties.SetName(control, label)
        AutomationProperties.SetAutomationId(control, id)

    let spacer () : Control = Border(Height = 0.0) :> Control

    let remaining () =
        match invite with
        | Some i -> i.expiresAtUtc - now
        | None -> TimeSpan.Zero

    let addressBlock (url: string) : Control list =
        let address =
            SelectableTextBlock(Text = url, FontFamily = Tokens.monoFontFamily, FontSize = Tokens.fontSmall, TextWrapping = TextWrapping.Wrap)
        tag address url "devices-address"
        let qr = QrCodeView(url, 6.0)
        qr.HorizontalAlignment <- HorizontalAlignment.Left
        tag qr (sprintf "二维码，扫描后打开 %s" url) "devices-qr"
        [ address :> Control; qr :> Control ]

    let inviteCard () : Control =
        let card (children: Control list) =
            Ui.groupingCard (Thickness(Spacing.space2xl, Spacing.space2xl)) (Ui.vstack Spacing.spaceMd children) Spacing.Radius.lg :> Control
        match canInvite, invite with
        | false, _ ->
            let text =
                if String.IsNullOrWhiteSpace wsUrl then "尚未连接主机。"
                else "只有这台主机上的桌面应用可以添加设备。请在桌面应用里打开「设置 → 设备 → 添加设备」。"
            let caption = Ui.caption text
            tag caption text "devices-invite-unavailable"
            card [ caption :> Control ]
        | true, None ->
            let button = Ui.button Ui.Primary "添加设备" (fun () -> inviteRejected <- None; actions.invite ())
            button.HorizontalAlignment <- HorizontalAlignment.Left
            tag button "添加设备" "devices-add"
            let rejected =
                match inviteRejected with
                | Some "not-desktop" -> Some "主机没有批准这次邀请：只有主机上的桌面应用可以添加设备。"
                | Some _ -> Some "主机没有批准这次邀请，请稍后再试。"
                | None -> None
            card
                [ Ui.label "把手机、平板或另一台电脑接入这台主机。" :> Control
                  Ui.caption "会生成一个 6 位配对码和地址，5 分钟内有效，只能用一次。" :> Control
                  (match rejected with
                   | Some r ->
                       let t = Ui.caption r
                       t.Foreground <- Tokens.danger
                       t :> Control
                   | None -> spacer ())
                  button :> Control ]
        | true, Some i ->
            let left = remaining ()
            let expired = left <= TimeSpan.Zero
            let codeText =
                TextBlock(
                    Text = DevicesModel.groupCode i.code,
                    FontSize = Tokens.fontDisplay,
                    FontFamily = Tokens.monoFontFamily,
                    FontWeight = FontWeight.Medium,
                    LetterSpacing = 6.0,
                    Foreground = (if expired then Tokens.textMuted else Tokens.text),
                    HorizontalAlignment = HorizontalAlignment.Left)
            tag codeText (sprintf "配对码 %s" (String.Join(" ", i.code.ToCharArray()))) "devices-code"
            let countdownText =
                TextBlock(
                    Text = (if expired then "已过期" else sprintf "%s 后失效" (DevicesModel.countdown left)),
                    FontSize = Tokens.fontSmall,
                    Foreground = (if expired then Tokens.danger else Tokens.textMuted))
            tag countdownText countdownText.Text "devices-countdown"
            let addressControls : Control list =
                match DevicesModel.inviteTarget wsUrl (lan ()) with
                | Reachable url ->
                    [ Ui.caption "在新设备上打开这个地址（或扫码），再输入上面的配对码：" :> Control ] @ addressBlock url
                | LoopbackOnly candidates ->
                    let hint =
                        Ui.caption
                            "这台主机只监听本机地址，手机暂时打不开它。要让手机接入，请把 config.toml 里的 listen 改成 0.0.0.0:端口（局域网内使用建议配置 TLS），或放在反向代理后面。"
                    match candidates with
                    | first :: others ->
                        [ hint :> Control
                          Ui.caption "改好后，在新设备上打开这个局域网地址（或扫码）：" :> Control ]
                        @ addressBlock first
                        @ (if others.IsEmpty then [] else [ Ui.caption (sprintf "其他网卡地址：%s" (String.Join("、", others))) :> Control ])
                    | [] -> [ hint :> Control ]
                | NoAddress -> [ Ui.caption "没有可用的主机地址。" :> Control ]
            let regenerate = Ui.button (if expired then Ui.Primary else Ui.Secondary) "重新生成" (fun () -> actions.invite ())
            tag regenerate "重新生成配对码" "devices-regenerate"
            let close =
                Ui.button Ui.Ghost "完成" (fun () ->
                    invite <- None
                    refresh ())
            tag close "关闭配对码" "devices-close-invite"
            card (
                [ Ui.caption "配对码" :> Control; codeText :> Control; countdownText :> Control ]
                @ addressControls
                @ [ Ui.hstack Spacing.spaceMd [ regenerate :> Control; close :> Control ] :> Control ]
            )

    let deviceRow (device: DeviceInfo) : Control =
        let badges =
            [ if device.current then yield "这台设备"
              if device.desktop then yield "桌面"
              if device.revoked then yield "已吊销" ]
        let title =
            TextBlock(
                // 保留名 wanxiang-desktop 是协议标识，给人看的是「本机桌面客户端」
                Text =
                    (if String.IsNullOrWhiteSpace device.name then "未命名设备"
                     elif device.desktop && device.name = "wanxiang-desktop" then "本机桌面客户端"
                     else device.name),
                FontSize = Tokens.fontBody,
                FontWeight = FontWeight.Medium,
                Foreground = (if device.revoked then Tokens.textMuted else Tokens.text),
                TextWrapping = TextWrapping.Wrap)
        let meta =
            Ui.caption (
                String.Join(
                    " · ",
                    badges @ [ DevicesModel.lastSeenText now device; DevicesModel.createdText device ]
                )
            )
        let buttons : Control list =
            if device.revoked then []
            else
                let rename = Ui.button Ui.Ghost "重命名" (fun () -> editingId <- Some device.id; refresh ())
                tag rename (sprintf "重命名设备 %s" device.name) (sprintf "devices-rename-%s" device.id)
                let canRevoke = not device.current && not device.desktop
                let revoke = Ui.button Ui.Danger "吊销" (fun () -> actions.revoke device)
                tag revoke (sprintf "吊销设备 %s" device.name) (sprintf "devices-revoke-%s" device.id)
                [ if not device.desktop then yield rename :> Control
                  if canRevoke then yield revoke :> Control ]
        let editor () : Control =
            let shell, box = Ui.textField "设备名称"
            box.Text <- device.name
            box.MaxLength <- 64
            tag box "设备名称" (sprintf "devices-name-%s" device.id)
            let save () =
                let value = if isNull box.Text then "" else box.Text.Trim()
                if value = "" then actions.toast "设备名称不能为空。" Failure
                else
                    editingId <- None
                    actions.rename device.id value
                    refresh ()
            box.KeyDown.Add(fun e ->
                if e.Key = Key.Enter then
                    e.Handled <- true
                    save ())
            let saveButton = Ui.button Ui.Primary "保存" save
            tag saveButton "保存设备名称" (sprintf "devices-name-save-%s" device.id)
            let cancel = Ui.button Ui.Ghost "取消" (fun () -> editingId <- None; refresh ())
            tag cancel "取消重命名" (sprintf "devices-name-cancel-%s" device.id)
            Dispatcher.UIThread.Post(fun () -> box.Focus() |> ignore)
            Ui.vstack Spacing.spaceMd [ shell :> Control; Ui.hstack Spacing.spaceMd [ saveButton :> Control; cancel :> Control ] :> Control ]
            :> Control
        let content: Control list =
            if editingId = Some device.id && not device.revoked then [ editor () ]
            else [ title :> Control; meta :> Control; Ui.hstack Spacing.spaceMd buttons :> Control ]
        let card = Ui.groupingCard (Thickness(Spacing.spaceXl, Spacing.spaceMd)) (Ui.vstack Spacing.spaceXs content) Spacing.Radius.md
        tag card (sprintf "设备 %s" device.name) (sprintf "devices-row-%s" device.id)
        card :> Control

    let listPanel () : Control list =
        match loadNote with
        | Some note -> [ Ui.caption note :> Control ]
        | None ->
            if loading && devices.IsEmpty then [ Ui.caption "正在读取设备列表…" :> Control ]
            elif devices.IsEmpty then [ Ui.caption "还没有设备。" :> Control ]
            else
                // 有效设备在前，已吊销在后
                devices
                |> List.sortBy (fun d -> (if d.revoked then 1 else 0), (if d.current then 0 else 1))
                |> List.map deviceRow

    let render () =
        inviteHost.Content <- inviteCard ()
        listHost.Children.Clear()
        for c in listPanel () do listHost.Children.Add c

    do refresh <- render

    let stopTimer () =
        timer |> Option.iter (fun t -> t.Stop())
        timer <- None

    /// 读取设备列表的有界等待：旧主机忽略未知事件时不会回复，不能一直显示“正在读取”。
    let mutable loadTimer: DispatcherTimer option = None
    let stopLoadTimer () =
        loadTimer |> Option.iter (fun t -> t.Stop())
        loadTimer <- None

    let startTimer (tick: unit -> unit) =
        stopTimer ()
        let t = DispatcherTimer(Interval = TimeSpan.FromSeconds 1.0)
        t.Tick.Add(fun _ -> tick ())
        t.Start()
        timer <- Some t

    /// 绑定外壳动作（设置视图先构造面板，AppShell 随后接线）。
    member _.Bind(next: DeviceActions) = actions <- next

    /// 请求设备列表时开始有界等待；超时无回复则提示主机版本可能较旧。
    member private this.BeginLoad() =
        loading <- true
        loadNote <- None
        stopLoadTimer ()
        let t = DispatcherTimer(Interval = TimeSpan.FromSeconds 8.0)
        t.Tick.Add(fun _ -> this.NotifyLoadTimeout())
        t.Start()
        loadTimer <- Some t

    /// 等待超时（也供测试直接调用）：仍在读取则结束等待并提示。
    member _.NotifyLoadTimeout() =
        stopLoadTimer ()
        if loading then
            loading <- false
            loadNote <- Some "主机没有返回设备列表。如果主机版本较旧，可能还不支持设备管理，请先升级主机。"
            if built then render ()

    /// 测试注入局域网地址来源。
    member _.SetLanSource(source: unit -> string list) = lan <- source

    member this.Build() : Control =
        if not built then
            built <- true
            let refreshButton =
                Ui.button Ui.Ghost "刷新" (fun () ->
                    this.BeginLoad()
                    render ()
                    actions.request ())
            tag refreshButton "刷新设备列表" "devices-refresh"
            refreshButton.HorizontalAlignment <- HorizontalAlignment.Left
            tag listHost "设备列表" "devices-list"
            root.Children.Add(Ui.vstack Spacing.spaceXs [ Ui.heading "设备" :> Control; Ui.caption "连接到这台主机的手机、浏览器和桌面应用。吊销后该设备立即断开，需要重新配对才能再连。" :> Control ])
            root.Children.Add inviteHost
            root.Children.Add(Ui.vstack Spacing.spaceMd [ Ui.sectionLabel "已授权的设备" :> Control; listHost :> Control; refreshButton :> Control ])
            render ()
        root :> Control

    /// 切进设备分区时调用：读取最新列表。
    member this.Refresh() =
        this.BeginLoad()
        if built then render ()
        actions.request ()

    member _.SetConnection(url: string) =
        wsUrl <- url
        if built then render ()

    member _.SetDevices(items: DeviceInfo list, canInviteNow: bool) =
        devices <- items
        canInvite <- canInviteNow
        now <- DateTimeOffset.UtcNow
        stopLoadTimer ()
        loading <- false
        loadNote <- None
        if built then render ()

    /// 主机下发了邀请：开始倒计时（以主机给的到期时间为准）。
    member _.SetInvite(code: string, expiresAtUtc: DateTimeOffset, nowUtc: DateTimeOffset) =
        invite <- Some { code = code; expiresAtUtc = expiresAtUtc }
        inviteRejected <- None
        now <- nowUtc
        if built then
            render ()
            startTimer (fun () ->
                now <- DateTimeOffset.UtcNow
                render ()
                if remaining () <= TimeSpan.Zero then stopTimer ())

    member _.SetInviteRejected(reason: string) =
        invite <- None
        inviteRejected <- Some reason
        if built then render ()

    /// 外部推进时钟（测试与定时器共用）。
    member _.Tick(nowUtc: DateTimeOffset) =
        now <- nowUtc
        if built then render ()

    /// 读取设备列表期间连接断开：旧版主机不认识设备事件时会直接断开。
    member _.NotifyDisconnected() =
        stopLoadTimer ()
        if loading then
            loading <- false
            loadNote <- Some "读取设备列表时连接断开了。如果主机版本较旧，可能还不支持设备管理，请先升级主机。"
            if built then render ()
        invite <- None
        stopTimer ()
        if built then render ()

    member _.CurrentInvite = invite
    member _.Devices = devices
    member _.CanInvite = canInvite
    member _.IsLoading = loading
    member _.IsBuilt = built
    /// 面板当前是否真的在屏幕上（用于只在可见时响应配置变更）。
    member _.IsShown = built && root.IsEffectivelyVisible
