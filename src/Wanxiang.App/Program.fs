namespace Wanxiang.App

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
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
    }

    let defaults =
        { server = None
          client = None
          pwa = None
          fix = None
          config = None
          data = None }

    let parseBool (s: string) : bool option =
        match s.ToLowerInvariant() with
        | "true" | "1" | "yes" | "on" -> Some true
        | "false" | "0" | "no" | "off" -> Some false
        | _ -> None

    /// 解析命令行。支持 --server=true/false、--client、--pwa、--fix、--config <path>、--data <dir>。
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
            elif arg = "--help" || arg = "-h" then
                eprintfn "usage: wanxiang [--server=true|false] [--client=true|false] [--pwa=true|false] [--fix] [--config <path>] [--data <dir>]"
                exit 0
            else
                failwithf "unknown argument: %s" arg
            i <- i + 1
        o

    let defaultHome () : string =
        match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
        | s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")

module Doctor =

    type Diagnosis = {
        mutable configOk: bool
        mutable configErrors: string list
        mutable lockOk: bool
        mutable lockError: string
        mutable replayOk: bool
        mutable replayError: string
        mutable portOk: bool
        mutable portError: string
        mutable attachmentsOk: bool
        mutable attachmentsMissing: string list
    }

    let private portInUse (listen: string) : bool =
        try
            let parts = listen.Split ':'
            if parts.Length <> 2 then false
            else
                let host = parts[0]
                let port = int parts[1]
                use client = new TcpClient()
                let ar = client.BeginConnect(host, port, null, null)
                ar.AsyncWaitHandle.WaitOne 300 |> ignore
                client.EndConnect ar |> ignore
                true
        with _ ->
            false

    /// 只读诊断（决策 51：doctor 默认严格只读，不修复）。
    let run (configPath: string, dataDir: string) : Diagnosis =
        let d =
            { configOk = false
              configErrors = []
              lockOk = false
              lockError = ""
              replayOk = false
              replayError = ""
              portOk = true
              portError = ""
              attachmentsOk = true
              attachmentsMissing = [] }
        // 配置
        if File.Exists configPath then
            match TomlCodec.tryParse (File.ReadAllText configPath) with
            | Ok cfg ->
                d.configOk <- true
                d.portOk <- not (portInUse cfg.listen)
                if not d.portOk then d.portError <- sprintf "port %s already in use" cfg.listen
            | Error errs -> d.configErrors <- errs
        else
            d.configErrors <- [ sprintf "config file not found: %s" configPath ]
        // 数据锁
        match DataLock.Acquire dataDir with
        | Ok l ->
            d.lockOk <- true
            (l :> IDisposable).Dispose()
        | Error e -> d.lockError <- e
        // replay（只读，不修复）
        match Replay.replay dataDir false with
        | Ok outcome ->
            d.replayOk <- true
            // Q179：附件引用可达性检查（只读报告，不修复）
            try
                let store = new Wanxiang.Server.AttachmentStore(dataDir, 1024L)
                try
                    let missing = System.Collections.Generic.HashSet<string>()
                    for conv in Projection.conversationList outcome.projection do
                        for m in Projection.effectiveMessages outcome.projection conv do
                            for (sha, _, _, _) in Wanxiang.Server.ServerModel.attachmentRefsOf m.payloadJson do
                                if not (store.Exists sha) then
                                    missing.Add(sha) |> ignore
                    d.attachmentsMissing <- missing |> Seq.toList
                    d.attachmentsOk <- List.isEmpty d.attachmentsMissing
                finally
                    store.Dispose()
            with _ -> ()
        | Error e -> d.replayError <- e
        d

    let print (d: Diagnosis) : unit =
        let mutable ok = true
        let report (name: string) (pass: bool) (detail: string) =
            if pass then
                eprintfn "%-12s OK" name
            else
                ok <- false
                eprintfn "%-12s FAIL: %s" name detail
        report "config" d.configOk (String.concat "; " d.configErrors)
        report "data-lock" d.lockOk d.lockError
        report "log-replay" d.replayOk d.replayError
        report "listen-port" d.portOk d.portError
        report "attachments" d.attachmentsOk (sprintf "%d referenced blob(s) missing" (List.length d.attachmentsMissing))
        eprintfn "overall: %s" (if ok then "PASS" else "FAIL")
        if not ok then exit 1

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
                        let result =
                            match Replay.replay dataDir true with
                            | Ok outcome ->
                                for f in outcome.truncatedFiles do
                                    logInfo(sprintf "truncated %s" f)
                                Ok()
                            | Error e -> Error e
                        (lockHandle :> IDisposable).Dispose()
                        match result with
                        | Ok () -> ()
                        | Error e ->
                            eprintfn "fix failed: %s" e
                            exit 1
                let d = Doctor.run (configPath, dataDir)
                Doctor.print d
                exit 0

            // 启动前先执行 doctor/fix（决策 51 第二问：先检查修复，再启动组件）。
            // Q107：纯客户端（无 S）不检查本地数据目录状态，不被数据问题阻塞启动。
            // 数据锁（决策 105/Q110）：任何可能修改 NDJSON 的修复必须在持锁后进行。
            let startupReplayOutcome =
                if not switches.server then
                    None
                else
                    let lockHandle =
                        match DataLock.Acquire dataDir with
                        | Ok l -> Some l
                        | Error e ->
                            eprintfn "data directory locked: %s" e
                            exit 1
                    let outcome =
                        if switches.fix then
                            match Replay.replay dataDir true with
                            | Ok o ->
                                for f in o.truncatedFiles do
                                    logInfo(sprintf "startup fix truncated %s" f)
                                Some o
                            | Error e ->
                                eprintfn "fix failed: %s" e
                                exit 1
                        else
                            match Replay.replay dataDir false with
                            | Ok o -> Some o
                            | Error e ->
                                eprintfn "log damaged: %s (run with --fix to repair)" e
                                exit 1
                    match lockHandle with
                    | Some l -> (l :> IDisposable).Dispose()
                    | None -> ()
                    outcome

            // 自动配对（决策 64 + Q190）：server+client 同进程时，本机桌面 Client 自动获得永久令牌。
            // - 服务端 TOML 只存哈希；客户端令牌原文保存到本机 client.toml（决策 54）；
            // - 任一侧丢失（哈希不在服务端 / 客户端令牌文件丢失）时创建新 token；
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
                                                  name = "wanxiang-desktop"
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

            // 服务器（若启用）：后台线程运行
            let serverApp =
                if switches.server then
                    let pwaDir =
                        if switches.pwa then
                            let candidate = Path.Combine(AppContext.BaseDirectory, "pwa")
                            if Directory.Exists candidate then Some candidate
                            else
                                // 开发模式：从源码目录读取
                                let dev = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Wanxiang.Pwa", "wwwroot")
                                if Directory.Exists(Path.GetFullPath dev) then Some(Path.GetFullPath dev)
                                else None
                        else None
                    let app = Wanxiang.Server.ServerApp(dataDir, configPath, false, pwaDir, logInfo, ?startupOutcome = startupReplayOutcome)
                    app.Start(switches.pwa)
                    Some app
                else
                    None

            let stopServer () =
                match serverApp with
                | Some app -> app.Stop()
                | None -> ()
                logInfo "wanxiang stopped"

            if switches.client then
                // 桌面客户端（决策 49：同进程 UI，通过真实 WebSocket 连接本机 S）
                // 有本地 S 时自动连接 loopback（决策 62）；无 S 时由用户在 UI 中选择（决策 62）
                let code = Wanxiang.UI.UiEntry.run argv
                stopServer ()
                code
            else
                // 无 UI：服务器等待信号优雅关闭（决策 101/102）。
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
