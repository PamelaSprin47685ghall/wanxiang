namespace Wanxiang.App

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls.ApplicationLifetimes

module Program =

    /// info 日志统一走 Wanxiang.Server.Stderr（结构化 JSON Lines + 已知密钥脱敏，决策 41/198）。
    let logInfo (msg: string) = Wanxiang.Server.Stderr.info msg

    [<EntryPoint; STAThread>]
    let main argv =
        try
            let cli = Cli.parse argv
            if cli.help then
                eprintfn "%s" Cli.usage
                exit 0
            // 路径与开关（决策 60：命令行 > TOML > 程序默认）由 LaunchPlan 推导
            let plan = LaunchPlan.ofOptions cli (Cli.defaultHome ())
            let configPath = plan.configPath
            let dataDir = plan.dataDir
            let switches = plan.switches
            let isDoctorOnly = RunModes.isDoctorOnly switches

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
