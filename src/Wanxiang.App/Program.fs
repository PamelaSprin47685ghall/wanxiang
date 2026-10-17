namespace Wanxiang.App

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Input.Platform
open Avalonia.Threading
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Client
open Wanxiang.UI

module Program =

    /// info 日志统一走 Wanxiang.Server.Stderr（结构化 JSON Lines + 已知密钥脱敏，决策 41/198）。
    let logInfo (msg: string) = Wanxiang.Server.Stderr.info msg

    [<EntryPoint; STAThread>]
    let main argv =
        try
            let parsed = Cli.parse argv
            let cli =
                if parsed.help then parsed
                else
                    { parsed with
                        config = parsed.config |> Option.map Wanxiang.UI.LocalState.requireOutsideGit
                        data = parsed.data |> Option.map Wanxiang.UI.LocalState.requireOutsideGit
                        bundle = parsed.bundle |> Option.map Wanxiang.UI.LocalState.requireOutsideGit }
            if cli.help then
                eprintfn "%s" Cli.usage
                exit 0
            // 路径与开关（决策 60：命令行 > TOML > 程序默认）由 LaunchPlan 推导
            let home = Wanxiang.UI.LocalState.configHome ()
            let requestedConfig, requestedData = Cli.paths cli home
            let configPath = Wanxiang.UI.LocalState.requireOutsideGit requestedConfig
            let dataDir = Wanxiang.UI.LocalState.requireOutsideGit requestedData
            // LaunchPlan 会读 TOML；派生的默认文件也必须先通过路径检查。
            let plan = LaunchPlan.ofOptions { cli with config = Some configPath; data = Some dataDir } home
            let switches = plan.switches
            let isDoctorOnly = RunModes.isDoctorOnly switches
            if not isDoctorOnly || switches.fix then
                let workspace = Directory.GetCurrentDirectory()
                if home = Wanxiang.UI.LocalState.rootFor workspace then
                    Wanxiang.UI.LocalState.initialize workspace |> ignore
            // 偏好与客户端令牌跟随本次配置，不暗读旧的用户级目录。
            Environment.SetEnvironmentVariable("WANXIANG_HOME", Path.GetDirectoryName configPath)

            // 决策 251：桌面已在运行时（系统打开链接 = 再启动一次），把链接交给它后退出，
            // 不去抢数据锁、也不弹第二个窗口。
            let instanceSocket = Wanxiang.UI.SingleInstance.socketPath (Path.GetDirectoryName configPath)
            if switches.client && Wanxiang.UI.SingleInstance.tryForward instanceSocket cli.openUrl then
                logInfo "forwarded to the running wanxiang window"
                exit 0
            cli.openUrl |> Option.iter Wanxiang.UI.ExternalLinks.post

            // 决策 50：pwa 开关 = 托管 PWA 静态资源。PWA 由 S 托管并依赖其 /ws 端点（决策 2），
            // 因此 pwa=true 隐含 server 功能——pwa-only 组合 = 托管 PWA + WebSocket 端点的无 UI 模式，
            // 绝不静默空转挂起。
            let effectiveServer = RunModes.runsHost switches

            if isDoctorOnly then
                // 0000：只读 doctor；0001：doctor + 修复（决策 51）
                if switches.fix then
                    logInfo "doctor --fix: repairing log"
                    // 决策 105/Q110：任何可能修改日志的修复必须在持锁后进行（StoreLaunch 负责）
                    match StoreLaunch.fix dataDir logInfo with
                    | Ok () -> ()
                    | Error e ->
                        eprintfn "%s" e
                        exit 1
                // O-002：`doctor --bundle <path>` 生成 support bundle（默认只收集元数据，
                // 禁止 apiKey/token/完整用户消息/完整附件）。
                // 先建 bundle（配置损坏/日志损坏正是最需要诊断的时候——不能因为诊断失败而不产出证据），
                // 再跑只读 doctor 并打印。
                match cli.bundle with
                | Some bundlePath ->
                    match Wanxiang.Server.SupportBundle.create dataDir configPath bundlePath with
                    | Ok dir -> logInfo(sprintf "support bundle written to %s" dir)
                    | Error e ->
                        eprintfn "support bundle failed: %s" e
                        exit 1
                | None -> ()
                let d = Wanxiang.Server.Doctor.run (configPath, dataDir)
                Wanxiang.Server.Doctor.print d
                exit 0

            // 启动前先执行 doctor/fix（决策 51 第二问：先检查修复，再启动组件）。
            // Q107：纯客户端（无 S）不检查本地数据目录状态，不被数据问题阻塞启动。
            // 数据锁（决策 105/Q110）：任何可能修改 NDJSON 的修复必须在持锁后进行。
            let replayStartup () =
                if not effectiveServer then
                    None
                else
                    // S-001 / S-003：只读检查，损坏时只读降级启动；格式不支持、读不了才拒绝启动（StoreLaunch 负责）。
                    // 打开的存储直接交给 ServerApp（不再重放第二次）。
                    match StoreLaunch.openForStartup dataDir logInfo with
                    | Ok store -> Some store
                    | Error e ->
                        eprintfn "%s" e
                        exit 1

            // 自动配对（决策 64 + Q190）：server+client 同进程时，本机桌面 Client 自动获得永久令牌。
            // - 主机 TOML 只存哈希；客户端令牌原文保存到本机 client.toml（决策 54）；
            // - 任一侧丢失（哈希不在主机 / 客户端令牌文件丢失）时创建新 token；
            // - 写 TOML 走 ConfigStore.Rewrite 原子路径（临时文件 + rename + reload，决策 42/43）。
            if RunModes.autoPairsDesktop switches then
                match DesktopPairing.ensure configPath with
                | Ok () -> ()
                | Error e -> logInfo(sprintf "desktop auto-pairing skipped: %s" e)

            // 主机（若启用）：后台线程运行。
            let startServer () =
                let startupReplayOutcome = replayStartup ()
                if effectiveServer then
                    let pwaDir = if switches.pwa then PwaAssets.locate AppContext.BaseDirectory else None
                    let app = new Wanxiang.Server.ServerApp(
                            dataDir, configPath, false, pwaDir, logInfo,
                            ?startupStore = startupReplayOutcome,
                            // 产品版本取本程序集（Wanxiang.App）版本；旧实现取 Wanxiang.Server 程序集版本，产品构建时两者相同
                            ?hostVersion = (match System.Reflection.Assembly.GetExecutingAssembly().GetName().Version with null -> None | v -> Some(v.ToString())))
                    app.Start(switches.pwa)
                    Some app
                else
                    None

            // 决策 291：主机 + 桌面同进程时，事件日志重放与主机启动放到后台，窗口先画出来。
            // 历史越长重放越久，用户却只需要先看到界面骨架；客户端的首次连接会等这个门
            // （LocalHostGate），不会因为主机还没监听而先报一次「连接失败」再退避重连。
            // 纯主机模式没有界面可先画，仍同步启动。
            let serverTask: Task<Wanxiang.Server.ServerApp option> =
                if RunModes.hostInBackground switches then Task.Run(fun () -> startServer ())
                else Task.FromResult(startServer ())
            Wanxiang.UI.LocalHostGate.ready <- (serverTask :> Task)
            // 稳定性（r17）：主机启动卡住时，退出最多等 15 秒，不无限挂起
            let serverApp () =
                try if serverTask.Wait(TimeSpan.FromSeconds 15.0) then serverTask.Result else None
                with _ -> None
            let stopServer () =
                match serverApp () with
                | Some app -> app.Stop()
                | None -> ()
                logInfo "wanxiang stopped"

            if switches.client then
                // 桌面客户端（决策 49：同进程 UI，通过真实 WebSocket 连接本机 S）
                // 有本地 S 时自动连接 loopback（决策 62）；无 S 时由用户在 UI 中选择（决策 62）
                // Q101/102：默认 UI+S 模式同样注册信号处理——第一次信号优雅排空（连接关闭、
                // 单写者 flush）后退出，第二次立即退出（强制终止由外部管理器负责）
                let signals = ShutdownSignals()
                let shutdownUi () =
                    match signals.Signal() with
                    | ShutdownStep.Force -> Environment.Exit ShutdownSignals.ForceExitCode
                    | ShutdownStep.Graceful ->
                        logInfo "shutting down gracefully (signal; send again to force)"
                        stopServer ()
                        Environment.Exit 0
                let ctrlHandler = ConsoleCancelEventHandler(fun _ e ->
                    e.Cancel <- true
                    shutdownUi ())
                Console.CancelKeyPress.AddHandler ctrlHandler
                let sigterm =
                    try
                        Some(PosixSignalRegistration.Create(PosixSignal.SIGTERM, Action<PosixSignalContext>(fun ctx ->
                            ctx.Cancel <- true
                            shutdownUi ())))
                    with _ -> None
                // 桌面 UI 入口（决策 48：本机客户端也必须走真实 WebSocket，入口仅负责 AppBuilder 平台启动）
                Wanxiang.UI.UrlSchemeRegistration.registerCurrentUser logInfo
                let mainWindow () =
                    match Application.Current with
                    | null -> None
                    | app ->
                        match app.ApplicationLifetime with
                        | :? IClassicDesktopStyleApplicationLifetime as l when not (isNull l.MainWindow) -> Some l.MainWindow
                        | _ -> None
                let activate () =
                    Avalonia.Threading.Dispatcher.UIThread.Post(fun () ->
                        mainWindow () |> Option.iter (fun w ->
                            if w.WindowState = Avalonia.Controls.WindowState.Minimized then
                                w.WindowState <- Avalonia.Controls.WindowState.Normal
                            w.Activate()))
                use _instance =
                    Wanxiang.UI.SingleInstance.listen
                        instanceSocket
                        (fun url -> Wanxiang.UI.ExternalLinks.post url; activate ())
                        activate
                // 决策 251：Avalonia 主线程初始化后，macOS 打开链接走 OpenUri 激活。
                Avalonia.Threading.Dispatcher.UIThread.Post(fun () ->
                    try
                        match Application.Current with
                        | null -> ()
                        | app ->
                            match app.TryGetFeature<IActivatableLifetime>() with
                            | null -> ()
                            | lifetime ->
                                lifetime.Activated.Add(fun e ->
                                    match e with
                                    | :? ProtocolActivatedEventArgs as p when p.Kind = ActivationKind.OpenUri && not (isNull p.Uri) ->
                                        Wanxiang.UI.ExternalLinks.post (p.Uri.ToString())
                                    | _ -> ())
                    with _ -> ())
                let runDesktopUi (argv: string array) : int =
                    // 代码着色与公式排版跑在进程内的 JS 引擎上。基座脚本解析要几百毫秒，
                    // 放到后台线程先付掉；没预热完也只是第一次渲染慢，不影响正确性。
                    let rich, backend = RichJintAdapter.create ()
                    Wanxiang.UI.RichBackend.install backend
                    Task.Run(fun () -> rich.Warmup()) |> ignore

                    // ---- 批次 2 接线：ShellComposer + EntryShells（参照 PWA 入口的完整接线）----
                    // 真实 WebSocket 客户端（WsClient 经 AppShell 包传递引用；命名空间 Wanxiang.Client）。
                    let client = Wanxiang.Client.WsClient()
                    let mutable shell: ShellComposer = Unchecked.defaultof<_>
                    let mutable overlay: Wanxiang2.OverlayDesk.OverlayDesk = Unchecked.defaultof<_>
                    let mutable rootGrid: Grid = null
                    // TODO(接线缺口)：ShellComposer.Connect() 不把 lastUrl 传给 ports.Connect()，
                    // 入口壳暂以 CredentialStore.defaultServerUrl()（本机 loopback）兜底；
                    // 待 AppShell 组合者把 URL 传入 Connect 端口后改为使用传入值。
                    let mutable lastUrl = Wanxiang.UI.CredentialStore.defaultServerUrl()
                    // 计时器归入口壳：DispatcherTimer 包装成 ShellPorts.After / Every。
                    let after (delay: TimeSpan) (action: unit -> unit) : IDisposable =
                        let timer = DispatcherTimer()
                        timer.Interval <- delay
                        timer.Tag <- box (fun _ -> action ())
                        timer.Tick.Add(fun _ -> action ())
                        timer.Start()
                        { new IDisposable with member _.Dispose() = timer.Stop() }
                    let every (interval: TimeSpan) (action: unit -> unit) : IDisposable =
                        let timer = DispatcherTimer()
                        timer.Interval <- interval
                        timer.Tick.Add(fun _ -> action ())
                        timer.Start()
                        { new IDisposable with member _.Dispose() = timer.Stop() }

                    // ---- 桌面 IO 端口（ShellPorts 的 IO 面；浏览器端口给空实现）----
                    // 剪贴板写：Avalonia 的剪贴板在 TopLevel.Clipboard（Application 没有 Clipboard）。
                    // 桌面端经 Application.Current 的 IClassicDesktopStyleApplicationLifetime.MainWindow
                    // （Window 是 TopLevel 子类）获取；失败静默（与宿主 AppShell 的 topLevel() 同路径）。
                    let copyToClipboard (text: string) =
                        try
                            match Avalonia.Application.Current with
                            | null -> ()
                            | app ->
                                match app.ApplicationLifetime with
                                | :? IClassicDesktopStyleApplicationLifetime as lifetime when not (isNull lifetime.MainWindow) ->
                                    match lifetime.MainWindow.Clipboard with
                                    | null -> ()
                                    | clip -> clip.SetTextAsync(text) |> ignore
                                | _ -> ()
                        with _ -> ()
                    // 系统浏览器打开链接：wanxiang:// 投收件箱（MainView 订阅），http(s) 用系统浏览器
                    let openLink (url: string) =
                        if url.StartsWith("wanxiang://", StringComparison.OrdinalIgnoreCase) then
                            Wanxiang.UI.ExternalLinks.post url
                        else
                            try
                                let psi = Diagnostics.ProcessStartInfo(url)
                                psi.UseShellExecute <- true
                                Diagnostics.Process.Start psi |> ignore
                            with _ -> ()
                    // TODO(桌面 IO)：文件保存对话框 / 文件选择对话框 / 拖入 / 粘贴 / 草稿落盘暂为空实现，
                    // 待桌面 IO 壳（StorageProvider / 文件对话框）接入后补全。
                    let saveDownload (_name: string) (_bytes: byte[]) = ()
                    let pickFiles () = ()
                    let dropFiles (_items: obj list) = ()
                    let pasteFromClipboard () : bool = false
                    let applyDraftWrite (_write: DraftWrite) = ()
                    // 系统深色偏好：桌面可经 Application.RequestedThemeVariant 判断，暂固定浅色（与 PWA 一致）
                    let systemPrefersDark () : bool = false
                    // 浏览器端口：桌面不用，空实现（与 AppShell「浏览器端口 PWA 才用」注释一致）
                    let browserViewportWidth () : float = 0.0
                    let browserResumeEpoch () : int = 0
                    let browserPageUrl () : string = ""
                    let browserConversationSucceeded () = ()
                    let browserAddHidden (_: unit -> unit) = ()

                    // 连接 / 认证发送（真实 WsClient；须在 buildViews 之前定义，ApprovalDesk 构造引用 send）
                    let connectAsync () : Async<int> =
                        async {
                            try
                                let! epoch = client.ConnectWithGenerationAsync(Uri lastUrl, CancellationToken.None) |> Async.AwaitTask
                                return epoch
                            with _ ->
                                return -1
                        }
                    let sendAuth (epoch: int) (ev: WireEvent) =
                        client.TrySendAtGenerationAsync(epoch, ev) |> ignore
                    let send (ev: WireEvent) = client.TrySendAsync ev |> ignore
                    let sendCommand (cmd: ClientCommand) = client.TrySendCommandAsync cmd |> ignore

                    // ---- 视图组装（ShellViews）：参照 PWA 入口的 9 Desk 构造，桌面端共用同一批主题活实例 ----
                    let overlayLook () : Wanxiang2.OverlayDesk.OverlayDeskLook =
                        let theme = SharedTheme.manager
                        let brush k = theme.Brush k :> Avalonia.Media.IBrush
                        let metric m = theme.Metric m :> IObservable<float>
                        let space s = theme.Space s :> IObservable<float>
                        { Layer =
                            { Scrim = brush Wanxiang2.ThemePalette.PaletteKey.Scrim
                              Surface = brush Wanxiang2.ThemePalette.PaletteKey.Surface
                              Edge = brush Wanxiang2.ThemePalette.PaletteKey.Border
                              EdgeWidth = Wanxiang2.Theme.Live.map Thickness (metric Wanxiang2.ThemeMetrics.Metric.BorderWidth)
                              DialogRadius = Wanxiang2.Theme.Live.map CornerRadius (space Wanxiang2.Theme.Space.RadiusLg)
                              PopupRadius = Wanxiang2.Theme.Live.map CornerRadius (space Wanxiang2.Theme.Space.RadiusMd)
                              DialogPadding = Wanxiang2.Theme.Live.map (fun v -> Thickness(v)) (space Wanxiang2.Theme.Space.Xl)
                              PopupPadding = Wanxiang2.Theme.Live.map (fun v -> Thickness(v)) (space Wanxiang2.Theme.Space.Xs)
                              DialogShadow = Wanxiang2.Theme.LiveValue(Avalonia.Media.BoxShadows()) :> IObservable<_>
                              PopupShadow = Wanxiang2.Theme.LiveValue(Avalonia.Media.BoxShadows()) :> IObservable<_>
                              ViewportInset = Wanxiang2.Theme.LiveValue 12.0 :> IObservable<_>
                              PopupGap = Wanxiang2.Theme.LiveValue 4.0 :> IObservable<_>
                              PopupMinWidth = Wanxiang2.Theme.LiveValue 180.0 :> IObservable<_>
                              ScrimFade = Wanxiang2.Theme.LiveValue TimeSpan.Zero :> IObservable<_>
                              ScrimEasing = Avalonia.Animation.Easings.CubicEaseOut() }
                          Toasts =
                            { Surface = brush Wanxiang2.ThemePalette.PaletteKey.SurfaceRaised
                              Edge = brush Wanxiang2.ThemePalette.PaletteKey.Border
                              Text = brush Wanxiang2.ThemePalette.PaletteKey.Text
                              Accent = fun _ -> brush Wanxiang2.ThemePalette.PaletteKey.Accent
                              AccentWidth = Wanxiang2.Theme.LiveValue 2.0 :> IObservable<_>
                              AccentRadius = Wanxiang2.Theme.LiveValue(CornerRadius 999.0) :> IObservable<_>
                              AccentOpacity = Wanxiang2.Theme.LiveValue 0.6 :> IObservable<_>
                              EdgeWidth = Wanxiang2.Theme.Live.map Thickness (metric Wanxiang2.ThemeMetrics.Metric.BorderWidth)
                              Radius = Wanxiang2.Theme.LiveValue(CornerRadius 8.0) :> IObservable<_>
                              Padding = Wanxiang2.Theme.LiveValue(Thickness(16.0, 12.0)) :> IObservable<_>
                              Shadow = Wanxiang2.Theme.LiveValue(Avalonia.Media.BoxShadows()) :> IObservable<_>
                              FontSize = metric Wanxiang2.ThemeMetrics.Metric.FontBody
                              LineHeight = Wanxiang2.Theme.LiveValue 18.0 :> IObservable<_>
                              ColumnSpacing = Wanxiang2.Theme.LiveValue 12.0 :> IObservable<_>
                              Spacing = Wanxiang2.Theme.LiveValue 8.0 :> IObservable<_>
                              Margin = Wanxiang2.Theme.LiveValue(Thickness(24.0, 0.0, 24.0, 32.0)) :> IObservable<_>
                              MaxWidth = Wanxiang2.Theme.LiveValue 480.0 :> IObservable<_>
                              BodyMaxHeight = Wanxiang2.Theme.LiveValue 160.0 :> IObservable<_>
                              Fade = Wanxiang2.Theme.LiveValue TimeSpan.Zero :> IObservable<_>
                              FadeEasing = Avalonia.Animation.Easings.CubicEaseOut() }
                          Menu =
                            { Text = brush Wanxiang2.ThemePalette.PaletteKey.Text
                              Muted = brush Wanxiang2.ThemePalette.PaletteKey.TextMuted
                              Faint = brush Wanxiang2.ThemePalette.PaletteKey.TextFaint
                              Danger = brush Wanxiang2.ThemePalette.PaletteKey.Danger
                              Accent = brush Wanxiang2.ThemePalette.PaletteKey.Accent
                              Check = fun b -> Wanxiang2.IconSet.IconSet.image Wanxiang2.IconSet.Glyph.Check b (metric Wanxiang2.ThemeMetrics.Metric.IconGlyph)
                              Chevron = fun b -> Wanxiang2.IconSet.IconSet.image Wanxiang2.IconSet.Glyph.ChevronRight b (metric Wanxiang2.ThemeMetrics.Metric.IconGlyph)
                              FontSize = metric Wanxiang2.ThemeMetrics.Metric.FontBody
                              HintFontSize = metric Wanxiang2.ThemeMetrics.Metric.FontCaption
                              IconSlotWidth = Wanxiang2.Theme.LiveValue 16.0 :> IObservable<_>
                              IconHeight = Wanxiang2.Theme.LiveValue 16.0 :> IObservable<_>
                              RightSlotMinWidth = Wanxiang2.Theme.LiveValue 16.0 :> IObservable<_>
                              HintMargin = Wanxiang2.Theme.LiveValue(Thickness(12.0, 0.0, 0.0, 0.0)) :> IObservable<_>
                              ColumnSpacing = Wanxiang2.Theme.LiveValue 6.0 :> IObservable<_>
                              ItemPadding = Wanxiang2.Theme.LiveValue(Thickness(8.0, 4.0)) :> IObservable<_>
                              ItemRadius = Wanxiang2.Theme.LiveValue(CornerRadius 4.0) :> IObservable<_>
                              ItemMinHeight = Wanxiang2.Theme.LiveValue 32.0 :> IObservable<_>
                              ItemSpacing = Wanxiang2.Theme.LiveValue 2.0 :> IObservable<_>
                              MaxHeight = Wanxiang2.Theme.LiveValue 200.0 :> IObservable<_>
                              DividerMargin = Wanxiang2.Theme.LiveValue(Thickness(6.0, 2.0)) :> IObservable<_>
                              HeaderMargin = Wanxiang2.Theme.LiveValue(Thickness(8.0, 6.0, 8.0, 2.0)) :> IObservable<_>
                              SelectPadding = Wanxiang2.Theme.LiveValue(Thickness(8.0, 4.0)) :> IObservable<_>
                              SelectRadius = Wanxiang2.Theme.LiveValue(CornerRadius 8.0) :> IObservable<_>
                              SelectEdgeWidth = Wanxiang2.Theme.LiveValue(Thickness 1.0) :> IObservable<_>
                              SelectCaptionMaxWidth = Wanxiang2.Theme.LiveValue 260.0 :> IObservable<_> }
                          MenuMinWidth = Wanxiang2.Theme.LiveValue 200.0 :> IObservable<_>
                          GroupedMenuMinWidth = Wanxiang2.Theme.LiveValue 240.0 :> IObservable<_>
                          DwellFailureMs = Wanxiang2.Theme.LiveValue 9000.0 :> IObservable<_>
                          DwellWarningMs = Wanxiang2.Theme.LiveValue 6500.0 :> IObservable<_>
                          DwellDefaultMs = Wanxiang2.Theme.LiveValue 4000.0 :> IObservable<_>
                          DismissTick = TimeSpan.FromMilliseconds 100.0 }

                    let buildViews () : ShellViews =
                        let theme = SharedTheme.manager
                        let styles = SharedTheme.styles
                        let root = Grid()
                        rootGrid <- root
                        let ov = Wanxiang2.OverlayDesk.OverlayDesk(root, overlayLook ())
                        overlay <- ov
                        let brandLogo = SharedTheme.logo

                        let composer = Wanxiang2.ComposerDesk.ComposerDesk(Wanxiang2.ComposerDesk.ComposerLooks.ofTheme theme styles,
                            { Submit = fun _ -> false
                              StopGeneration = fun () -> shell.Commands.HandleShortcut Wanxiang.UI.ShortcutAction.Escape |> ignore
                              PickAttachment = fun () -> pickFiles ()
                              RemoveAttachment = fun _ -> ()
                              OpenModelPicker = fun _ -> ()
                              DropFiles = fun _ -> ()
                              PasteFromClipboard = pasteFromClipboard
                              OpenSessionSettings = fun () -> () })

                        let approval = Wanxiang2.ApprovalDesk.ApprovalDesk(theme, ov, send)

                        let tintHost: Wanxiang2.CodeTint.CodeTintHost =
                            { HighlightHtml = fun (code, lang) ->
                                  match Wanxiang.UI.RichBackend.current () with
                                  | Some backend -> backend.HighlightHtml(code, lang)
                                  | None -> None
                              Background = fun work -> work ()
                              After = fun ms action -> after (TimeSpan.FromMilliseconds(float ms)) action |> ignore
                              Synchronous = true }
                        let tint = Wanxiang2.CodeTint.CodeTint(tintHost)
                        let cards = Wanxiang2.MessageCardView.MessageCards(Wanxiang2.MessageCardView.CardLooks.ofTheme theme styles,
                            { Tint = tint
                              MathHtml = fun (tex, display) ->
                                  match Wanxiang.UI.RichBackend.current () with
                                  | Some backend -> backend.MathHtml(tex, display)
                                  | None -> None
                              Wrap = Wanxiang2.MarkdownView.CodeWrapMemory(false)
                              Clock = Wanxiang2.MessageCardView.CaretClock()
                              Touch = Wanxiang2.MessageCardView.CardTouch()
                              Now = fun () -> DateTimeOffset.UtcNow })

                        let chat = Wanxiang2.ChatScreen.ChatScreen(Wanxiang2.ChatScreen.ChatScreenLooks.ofTheme theme styles,
                            cards, composer, approval, ov, brandLogo,
                            { Card = { CopyText = copyToClipboard
                                       Regenerate = fun () -> ()
                                       EditAndFork = fun _ -> ()
                                       DeleteMessage = fun _ -> ()
                                       DownloadAttachment = fun _ -> ()
                                       OpenLink = openLink
                                       CopyLink = None
                                       Recover = None }
                              RenameTitle = fun _ -> ()
                              ToggleSidebar = fun () -> shell.Commands.HandleShortcut Wanxiang.UI.ShortcutAction.ToggleSidebar |> ignore
                              OpenSessionSettings = fun () -> ()
                              ForkFromHere = fun () -> ()
                              ExportConversation = fun () -> ()
                              StopGeneration = fun () -> shell.Commands.HandleShortcut Wanxiang.UI.ShortcutAction.Escape |> ignore
                              RequestOlderHistory = fun () -> ()
                              FocusHome = fun () -> composer.Focus()
                              OpenParent = fun _ -> ()
                              SiblingSelected = fun _ -> ()
                              After = after })

                        let sidebar = Wanxiang2.SidebarDesk.SidebarDesk(theme, ov,
                            { newConversation = fun () -> shell.Commands.HandleShortcut Wanxiang.UI.ShortcutAction.NewConversation |> ignore
                              openConversation = fun _id -> shell.Commands.HandleShortcut(Wanxiang.UI.ShortcutAction.OpenConversationAt 0) |> ignore
                              renameConversation = fun _ -> ()
                              deleteConversation = fun _ -> ()
                              setPinned = fun _ _ -> ()
                              setArchived = fun _ _ -> ()
                              selectionAllPinned = fun _ -> false
                              setPinnedMany = fun _ _ -> ()
                              setArchivedMany = fun _ _ -> ()
                              deleteMany = fun _ -> ()
                              duplicateAsFork = fun _ -> ()
                              exportConversation = fun _ -> ()
                              openSettings = fun () -> shell.Commands.HandleShortcut Wanxiang.UI.ShortcutAction.OpenSettings |> ignore
                              reconnect = fun () -> shell.ResumeReconnectNow()
                              toggleArchivedVisibility = fun () -> ()
                              closeNavigation = fun () -> () },
                            brandLogo)

                        let settingsHooks: Wanxiang2.SettingsScreen.SettingsHooks =
                            { UpsertProvider = fun _ _ -> ()
                              DeleteProvider = ignore
                              ProbeProvider = fun _ -> ()
                              UpsertMcp = fun _ _ -> ()
                              DeleteMcp = ignore
                              UpdateGeneration = fun _ _ -> ()
                              SavePrefs = Wanxiang.UI.UiPrefsFile.save }
                        let settings = Wanxiang2.SettingsScreen.SettingsScreen(theme, ov, settingsHooks, Wanxiang.UI.UiPrefsFile.load ())

                        let onboardingHooks: Wanxiang2.SettingsScreen.OnboardingHooks =
                            { UpsertProvider = fun _ _ -> ()
                              DiscardProvider = ignore
                              ProbeProvider = fun _ -> ()
                              TakenIds = fun () -> []
                              DiscoverLocal = fun () -> System.Threading.Tasks.Task.FromResult(None)
                              OpenAdvanced = fun () -> () }
                        let onboarding = Wanxiang2.SettingsScreen.OnboardingDesk(theme, ov, onboardingHooks)

                        let workspace = Wanxiang2.Workspace.Workspace(theme, root, sidebar, chat, composer,
                            { PrefersTouch = fun () -> SharedTheme.input.PrefersTouch
                              SidebarWidth = fun () -> Wanxiang.UI.UiPrefsFile.limits.sidebarWidth
                              InitialSidebarCollapsed = false })

                        let palette = Wanxiang2.PaletteDesk.PaletteDesk(theme, ov, fun () -> SharedTheme.input.PrefersTouch)
                        let modelSwitch = Wanxiang2.ModelSwitchDesk.ModelSwitchDesk(theme, ov)

                        { Sidebar = sidebar
                          Chat = chat
                          Composer = composer
                          Settings = settings
                          Onboarding = onboarding
                          Workspace = workspace
                          Approval = approval
                          Palette = palette
                          ModelSwitch = modelSwitch
                          SettingsHostVisible = fun visible -> settings.View.IsVisible <- visible
                          WorkspaceHostVisible = fun visible -> workspace.Host.IsVisible <- visible
                          SettingsFocusInitial = fun () -> settings.FocusInitial()
                          SettingsShowSection = fun section ->
                              section |> Option.iter (fun s -> Wanxiang2.SettingsScreen.SettingsPart.ofKey s |> Option.iter settings.ShowSection) }

                    /// 根视图工厂（App.Root）：MainView 由 ShellComposer 驱动，这里给根 Grid。
                    let rootFactory () : Control =
                        if isNull rootGrid then buildViews () |> ignore
                        rootGrid :> Control

                    // 提示条（OverlayDesk）
                    let toast (text: string) (tone: ShellToastTone) =
                        match tone with
                        | ShellToastTone.Info -> overlay.Toast(text, Wanxiang2.ToastStrip.ToastTone.Info)
                        | ShellToastTone.Success -> overlay.Toast(text, Wanxiang2.ToastStrip.ToastTone.Success)
                        | ShellToastTone.Warning -> overlay.Toast(text, Wanxiang2.ToastStrip.ToastTone.Warning)
                        | ShellToastTone.Failure -> overlay.Toast(text, Wanxiang2.ToastStrip.ToastTone.Failure)
                    let toastUndo (text: string) (undo: unit -> unit) =
                        overlay.ToastWith(text, Wanxiang2.ToastStrip.ToastTone.Info, Some("撤销", undo))

                    let ports: ShellPorts =
                        { Connect = connectAsync
                          SendAuth = sendAuth
                          Send = send
                          SendCommand = sendCommand
                          After = after
                          Every = every
                          Toast = toast
                          ToastUndo = toastUndo
                          CopyToClipboard = copyToClipboard
                          OpenLink = openLink
                          SaveDownload = saveDownload
                          PickFiles = pickFiles
                          DropFiles = dropFiles
                          PasteFromClipboard = pasteFromClipboard
                          ApplyDraftWrite = applyDraftWrite
                          PrefsLoad = Wanxiang.UI.UiPrefsFile.load
                          PrefsSave = Wanxiang.UI.UiPrefsFile.save
                          TryLoadClientToml = Wanxiang.UI.CredentialStore.tryLoadClientToml
                          TryLoadBrowserConnection =
                            fun () ->
                                // 端口要求 Async；CredentialStore 返回 Task，用 AwaitTask 转换。
                                Wanxiang.UI.CredentialStore.tryLoadBrowserConnectionAsync ()
                                |> Async.AwaitTask
                          SaveBrowserConnection = fun _ _ _ _ -> ()
                          BrowserViewportWidth = browserViewportWidth
                          BrowserResumeEpoch = browserResumeEpoch
                          BrowserPageUrl = browserPageUrl
                          BrowserConversationSucceeded = browserConversationSucceeded
                          BrowserAddHidden = browserAddHidden
                          SystemPrefersDark = systemPrefersDark
                          BuildViews = buildViews }
                    shell <- ShellComposer(ports)
                    // App 静态端口（EntryShells）：Root 创建根视图、Build 调 ShellComposer.Build 并接事件、
                    // 关窗落草稿、偏好读写。
                    Wanxiang.UI.App.Root <- Some rootFactory
                    Wanxiang.UI.App.Build <- Some(fun _view ->
                        shell.Build() |> ignore
                        client.EventReceived.Add(fun ev -> Avalonia.Threading.Dispatcher.UIThread.Post(fun () -> shell.ApplyHostEvent ev))
                        shell.AutoConnect())
                    Wanxiang.UI.App.FlushDrafts <- Some(fun () -> shell.FlushDrafts())
                    Wanxiang.UI.App.LoadPrefs <- Some Wanxiang.UI.UiPrefsFile.load
                    Wanxiang.UI.App.SavePrefs <- Some Wanxiang.UI.UiPrefsFile.save
                    // 宿主 StartupTrace：采样模式首帧打点 + requestExit（决策 290）。
                    Wanxiang.UI.App.MarkFirstFrame <-
                        Some(fun () ->
                            Wanxiang.UI.StartupTrace.mark "firstFrame" |> ignore
                            Wanxiang.UI.StartupTrace.requestExit ())
                    // MainWindow 由 EntryShells 的 App.OnFrameworkInitializationCompleted 在
                    // IClassicDesktopStyleApplicationLifetime 分支构造（Root/Build/LoadPrefs/SavePrefs 齐备时）。
                    AppBuilder
                        .Configure<Wanxiang.UI.App>()
                        .UsePlatformDetect()
                        .StartWithClassicDesktopLifetime(argv |> Array.filter (fun a -> not (a.StartsWith("wanxiang://", StringComparison.OrdinalIgnoreCase))))
                let code = runDesktopUi argv
                stopServer ()
                match sigterm with Some r -> r.Dispose() | None -> ()
                code
            else
                // 无 UI：主机等待信号优雅关闭（决策 101/102）。
                // SIGTERM（kill 默认）与 SIGINT（Ctrl+C）都触发优雅关闭：第一次排空退出，第二次立即退出。
                use cts = new CancellationTokenSource()
                let signals = ShutdownSignals()
                let shutdownOnce () =
                    // 信号回调可能并发（SIGINT+SIGTERM 同时到达），ShutdownSignals 保证恰好一次 Graceful
                    match signals.Signal() with
                    | ShutdownStep.Force -> Environment.Exit ShutdownSignals.ForceExitCode
                    | ShutdownStep.Graceful ->
                        logInfo "shutting down gracefully (send signal again to force)"
                        stopServer ()
                        cts.Cancel()
                let ctrlHandler = ConsoleCancelEventHandler(fun _ e ->
                    e.Cancel <- true
                    shutdownOnce ())
                Console.CancelKeyPress.AddHandler ctrlHandler
                let sigterm =
                    try
                        Some(PosixSignalRegistration.Create(PosixSignal.SIGTERM, Action<PosixSignalContext>(fun ctx ->
                            ctx.Cancel <- true
                            shutdownOnce ())))
                    with _ -> None
                try
                    Task.Delay(Timeout.Infinite, cts.Token).Wait()
                with
                | :? AggregateException -> ()
                | :? OperationCanceledException -> ()
                stopServer ()
                match sigterm with Some r -> r.Dispose() | None -> ()
                0
        with e ->
            eprintfn "wanxiang: %s" e.Message
            exit 1
