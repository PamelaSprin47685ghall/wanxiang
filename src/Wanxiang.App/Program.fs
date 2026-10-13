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
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Store

module Cli =

    type CliOptions = {
        server: bool option
        client: bool option
        pwa: bool option
        fix: bool option
        config: string option
        data: string option
        bundle: string option
        /// 决策 251：系统打开 `wanxiang://` 链接时作为唯一位置参数传入
        openUrl: string option
    }

    let defaults =
        { server = None
          client = None
          pwa = None
          fix = None
          config = None
          data = None
          bundle = None
          openUrl = None }

    let parseBool (s: string) : bool option =
        match s.ToLowerInvariant() with
        | "true" | "1" | "yes" | "on" -> Some true
        | "false" | "0" | "no" | "off" -> Some false
        | _ -> None

    /// 解析命令行。支持 --server=true/false、--client、--pwa、--fix、--config <path>、--data <dir>、--bundle <path>。
    let parse (argv: string array) : CliOptions =
        let mutable o = defaults
        let mutable i = 0
        while i < argv.Length do
            let arg = argv[i]
            let inline takeValue () =
                i <- i + 1
                if i >= argv.Length then failwithf "missing value for %s" arg
                argv[i]
            if arg.StartsWith "--config=" then o <- { o with config = Some(arg.Substring "--config=".Length) }
            elif arg = "--config" then o <- { o with config = Some(takeValue ()) }
            elif arg.StartsWith "--data=" then o <- { o with data = Some(arg.Substring "--data=".Length) }
            elif arg = "--data" then o <- { o with data = Some(takeValue ()) }
            elif arg.StartsWith "--server=" then o <- { o with server = parseBool (arg.Substring "--server=".Length) }
            elif arg = "--server" then o <- { o with server = Some true }
            elif arg.StartsWith "--client=" then o <- { o with client = parseBool (arg.Substring "--client=".Length) }
            elif arg = "--client" then o <- { o with client = Some true }
            elif arg.StartsWith "--pwa=" then o <- { o with pwa = parseBool (arg.Substring "--pwa=".Length) }
            elif arg = "--pwa" then o <- { o with pwa = Some true }
            elif arg = "--fix" then o <- { o with fix = Some true }
            elif arg.StartsWith "--fix=" then o <- { o with fix = parseBool (arg.Substring "--fix=".Length) }
            elif arg.StartsWith "--bundle=" then o <- { o with bundle = Some(arg.Substring "--bundle=".Length) }
            elif arg = "--bundle" then o <- { o with bundle = Some(takeValue ()) }
            elif arg.StartsWith("wanxiang://", StringComparison.OrdinalIgnoreCase) then o <- { o with openUrl = Some arg }
            elif arg = "--help" || arg = "-h" then
                eprintfn "usage: wanxiang [--server=true|false] [--client=true|false] [--pwa=true|false] [--fix] [--config <path>] [--data <dir>] [--bundle <path>]"
                exit 0
            else
                failwithf "unknown argument: %s" arg
            i <- i + 1
        o

    let defaultHome () : string =
        match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
        | s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")

module Program =

    /// info 日志统一走 Wanxiang.Server.Stderr（结构化 JSON Lines + 已知密钥脱敏，决策 41/198）。
    let logInfo (msg: string) = Wanxiang.Server.Stderr.info msg

    [<EntryPoint; STAThread>]
    let main argv =
        try
            let cli = Cli.parse argv
            let home = cli.config |> Option.defaultValue (Path.Combine(Cli.defaultHome (), "config.toml")) |> Path.GetFullPath
            let dataDir = cli.data |> Option.defaultValue (Path.Combine(Path.GetDirectoryName home, "data")) |> Path.GetFullPath
            let configPath = home

            // 解析 TOML 得到默认开关（决策 60：命令行 > TOML > 程序默认）
            let tomlSwitches =
                if File.Exists configPath then
                    match TomlCodec.tryParse (File.ReadAllText configPath) with
                    | Ok cfg -> Some cfg.runtime
                    | Error _ -> None
                else
                    None
            let defaults = tomlSwitches |> Option.defaultValue { server = true; client = true; pwa = true; fix = false }

            let switches =
                { server = cli.server |> Option.defaultValue defaults.server
                  client = cli.client |> Option.defaultValue defaults.client
                  pwa = cli.pwa |> Option.defaultValue defaults.pwa
                  fix = cli.fix |> Option.defaultValue defaults.fix }

            let isDoctorOnly = not switches.server && not switches.client && not switches.pwa

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
            let effectiveServer = switches.server || switches.pwa

            if isDoctorOnly then
                // 0000：只读 doctor；0001：doctor + 修复（决策 51）
                if switches.fix then
                    logInfo "doctor --fix: repairing log"
                    // 决策 105/Q110：任何可能修改 NDJSON 的修复必须在持锁后进行（与运行期写入互斥）
                    match DataLock.Acquire dataDir with
                    | Error e ->
                        eprintfn "data directory locked: %s" e
                        exit 1
                    | Ok lockHandle ->
                        match lockHandle.PidWarning with
                        | Some w -> logInfo(sprintf "warning: %s" w)
                        | None -> ()
                        // 修复前先只读检查（算法 R）；Healthy 无需修复。
                        // 修复前建立证据包（算法 B）：复制受影响文件 + SHA-256 + manifest。
                        // 只有证据包建立成功才允许执行修复（S-002 / R1）。
                        let result =
                            match Inspect.run dataDir with
                            | Healthy _ -> Ok []
                            | RepairRequired (_, plan) ->
                                match Wanxiang.Store.RecoveryBundle.create dataDir plan with
                                | Error e -> Error e
                                | Ok bundle ->
                                    logInfo(sprintf "recovery bundle created at %s" bundle)
                                    match Repair.repair dataDir with
                                    | Ok truncatedFiles ->
                                        for f in truncatedFiles do
                                            logInfo(sprintf "truncated %s" f)
                                        Ok truncatedFiles
                                    | Error e -> Error e
                            | OutcomeUnknown (_, commitId, reason) ->
                                logInfo(sprintf "outcome of commit %d unknown (%s); reopening settled it, nothing to truncate" commitId reason)
                                Ok []
                            | HostOwned _ -> Error "the event log is held by a running host; stop it first"
                            | UnsupportedFormat msg -> Error msg
                            | Unreadable msg -> Error msg
                        (lockHandle :> IDisposable).Dispose()
                        match result with
                        | Ok _ -> ()
                        | Error e ->
                            eprintfn "fix failed: %s" e
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
                    let lockHandle =
                        match DataLock.Acquire dataDir with
                        | Ok l ->
                            match l.PidWarning with
                            | Some w -> logInfo(sprintf "warning: %s" w)
                            | None -> ()
                            Some l
                        | Error e ->
                            eprintfn "data directory locked: %s" e
                            exit 1
                    // S-001 / STOP-1：正常启动**只读检查**（算法 R），不再自动截尾/删除。
                    // Healthy → 正常启动；RepairRequired → 以最长有效前缀启动**只读降级模式**，
                    // 禁止一切永久写入，提示运行 doctor --fix（修复会先建立证据包）。
                    // 只有 UnsupportedFormat / Unreadable 这类阻断性问题才拒绝启动。
                    // S-003 / STOP-2（算法 L）：优先从快照启动——最新有效快照作为投影基线，
                    // 只重放 throughCommitId 之后的日志；快照损坏/超前则回退完整重放（L8/L9）。
                    // 启动检查与打开合为一步：打开的存储直接交给 ServerApp（不再重放第二次）。
                    let store =
                        try new EventStore(dataDir)
                        with e ->
                            eprintfn "event log not readable: %s" e.Message
                            exit 1
                    let inspectStartup () =
                        let r = Inspect.ofStore store
                        match r with
                        | Healthy o | RepairRequired(o, _) ->
                            match o.startup.snapshot with
                            | LedgerStore.Used through -> logInfo(sprintf "snapshot %d loaded; replayed %d commits after it" through o.startup.replayedCommits)
                            | LedgerStore.Absent -> logInfo "no snapshot; full replay"
                            | LedgerStore.Discarded why -> logInfo(sprintf "snapshot discarded (%s); full replay" why)
                        | _ -> ()
                        r

                    let outcome =
                        match inspectStartup () with
                        | Healthy _ -> Some store
                        | OutcomeUnknown (o, commitId, reason) ->
                            logInfo(sprintf "outcome of commit %d unknown (%s); starting read-only (valid through commit %d)" commitId reason o.lastCommitId)
                            Some store
                        | HostOwned _ -> Some store
                        | RepairRequired (o, plan) ->
                            logInfo(
                                sprintf
                                    "event log damaged at %s (%s); starting read-only degraded mode (valid through commit %d). Run `wanxiang doctor --fix` to repair."
                                    plan.damage.file
                                    plan.damage.reason
                                    o.lastCommitId)
                            Some store
                        | UnsupportedFormat msg ->
                            eprintfn "unsupported event log format: %s" msg
                            exit 1
                        | Unreadable msg ->
                            eprintfn "event log not readable: %s" msg
                            exit 1
                    match lockHandle with
                    | Some l -> (l :> IDisposable).Dispose()
                    | None -> ()
                    outcome

            // 自动配对（决策 64 + Q190）：server+client 同进程时，本机桌面 Client 自动获得永久令牌。
            // - 主机 TOML 只存哈希；客户端令牌原文保存到本机 client.toml（决策 54）；
            // - 任一侧丢失（哈希不在主机 / 客户端令牌文件丢失）时创建新 token；
            // - 写 TOML 走 ConfigStore.Rewrite 原子路径（临时文件 + rename + reload，决策 42/43）。
            if switches.server && switches.client then
                let clientConfigPath = Path.Combine(Path.GetDirectoryName configPath, "client.toml")
                let writeClientConfig (token: string) =
                    try
                        let text =
                            sprintf "[client]\nurl = \"ws://%s/ws\"\ntoken = \"%s\"\n" (TomlCodec.tryParse (File.ReadAllText configPath) |> function Ok c -> c.listen | Error _ -> "127.0.0.1:8765") token
                        let dir = Path.GetDirectoryName clientConfigPath
                        let tmp = Path.Combine(dir, sprintf ".client.toml.tmp.%d" Environment.ProcessId)
                        use fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)
                        try File.SetUnixFileMode(tmp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
                        let bytes = Text.Encoding.UTF8.GetBytes text
                        fs.Write(bytes, 0, bytes.Length)
                        fs.Flush()
                        File.Move(tmp, clientConfigPath, true)
                        Ok()
                    with e ->
                        Error e.Message
                let readClientToken () =
                    if not (File.Exists clientConfigPath) then None
                    else
                        try
                            File.ReadAllLines clientConfigPath
                            |> Array.tryPick (fun line ->
                                let t = line.Trim()
                                if t.StartsWith "token" then
                                    let v = t.Substring(t.IndexOf '=' + 1).Trim().Trim('"')
                                    if String.IsNullOrWhiteSpace v then None else Some v
                                else None)
                        with _ -> None
                let result =
                    // 首次启动 TOML 不存在：先生成完整默认 TOML（决策 182，原子路径）
                    let ensureConfig () =
                        if File.Exists configPath then Ok()
                        else
                            match Wanxiang.Config.ConfigStore.Open(configPath, ignore, ignore) with
                            | Ok cs ->
                                (cs :> IDisposable).Dispose()
                                Ok()
                            | Error e -> Error e
                    match ensureConfig () with
                    | Error e -> Error e
                    | Ok () ->
                        match Wanxiang.Config.ConfigStore.Open(configPath, ignore, ignore) with
                        | Error e -> Error e
                        | Ok cs ->
                            let cfg = cs.Current
                            let existingToken = readClientToken ()
                            let tokenValid =
                                existingToken
                                |> Option.exists (fun tok ->
                                    cfg.authClients
                                    |> List.exists (fun c -> c.tokenHash = Wanxiang.Config.Auth.hashToken tok && not c.revoked))
                            let outcome =
                                if tokenValid then
                                    Ok()
                                else
                                    let token = Wanxiang.Config.Auth.generateToken ()
                                    let hash = Wanxiang.Config.Auth.hashToken token
                                    let newCfg =
                                        { cfg with
                                            authClients =
                                                { tokenHash = hash
                                                  name = Wanxiang.Config.Auth.desktopClientName
                                                  createdAtUtc = DateTimeOffset.UtcNow
                                                  lastSeenUtc = None
                                                  revoked = false }
                                                :: cfg.authClients }
                                    match cs.Rewrite newCfg with
                                    | Ok () -> writeClientConfig token
                                    | Error e -> Error e
                            (cs :> IDisposable).Dispose()
                            outcome
                match result with
                | Ok () -> ()
                | Error e -> logInfo(sprintf "desktop auto-pairing skipped: %s" e)

            // 主机（若启用）：后台线程运行。
            let startServer () =
                let startupReplayOutcome = replayStartup ()
                if effectiveServer then
                    let pwaDir =
                        if switches.pwa then
                            let candidate = Path.Combine(AppContext.BaseDirectory, "pwa")
                            if Directory.Exists candidate then Some candidate
                            else
                                // 开发模式：优先 Wanxiang.Pwa 的发布产物 AppBundle（含 _framework 运行时），其次源码静态壳
                                let baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."))
                                let publishCandidates = [
                                    Path.Combine(baseDir, "Wanxiang.Pwa", "bin", "Debug", "net10.0-browser", "browser-wasm", "AppBundle")
                                    Path.Combine(baseDir, "Wanxiang.Pwa", "bin", "Release", "net10.0-browser", "browser-wasm", "AppBundle")
                                ]
                                match publishCandidates |> List.tryFind Directory.Exists with
                                | Some p -> Some(Path.GetFullPath p)
                                | None ->
                                    let dev = Path.Combine(baseDir, "Wanxiang.Pwa", "wwwroot")
                                    if Directory.Exists dev then Some dev else None
                        else None
                    let app = new Wanxiang.Server.ServerApp(dataDir, configPath, false, pwaDir, logInfo, ?startupStore = startupReplayOutcome)
                    app.Start(switches.pwa)
                    Some app
                else
                    None

            // 决策 291：主机 + 桌面同进程时，事件日志重放与主机启动放到后台，窗口先画出来。
            // 历史越长重放越久，用户却只需要先看到界面骨架；客户端的首次连接会等这个门
            // （LocalHostGate），不会因为主机还没监听而先报一次「连接失败」再退避重连。
            // 纯主机模式没有界面可先画，仍同步启动。
            let serverTask: Task<Wanxiang.Server.ServerApp option> =
                if switches.client && effectiveServer then Task.Run(fun () -> startServer ())
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
                let signalLock = obj()
                let mutable sigCount = 0
                let shutdownUi () =
                    lock signalLock (fun () ->
                        sigCount <- sigCount + 1
                        if sigCount >= 2 then
                            Environment.Exit 130
                        else
                            logInfo "shutting down gracefully (signal; send again to force)"
                            stopServer ()
                            Environment.Exit 0)
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
                    let rich = JintRichBackend()
                    Wanxiang.UI.RichBackend.install rich
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
                let signalLock = obj()
                let mutable sigCount = 0
                let shutdownOnce () =
                    // 信号回调可能并发（SIGINT+SIGTERM 同时到达），锁内幂等计数
                    lock signalLock (fun () ->
                        sigCount <- sigCount + 1
                        if sigCount >= 2 then
                            Environment.Exit 130
                        else
                            logInfo "shutting down gracefully (send signal again to force)"
                            stopServer ()
                            cts.Cancel())
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
