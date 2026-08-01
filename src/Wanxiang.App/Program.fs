namespace Wanxiang.App

open System
open System.IO
open System.Net
open System.Net.Sockets
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
              portError = "" }
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
        | Ok _ -> d.replayOk <- true
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
        eprintfn "overall: %s" (if ok then "PASS" else "FAIL")
        if not ok then exit 1

module Program =

    let logInfo (msg: string) =
        let o = System.Text.Json.Nodes.JsonObject()
        o["level"] <- "info"
        o["event"] <- "info"
        o["utc"] <- DateTimeOffset.UtcNow.ToString("o")
        o["message"] <- msg
        eprintfn "%s" (o.ToJsonString())

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
                    match Replay.replay dataDir true with
                    | Ok outcome ->
                        for f in outcome.truncatedFiles do
                            logInfo(sprintf "truncated %s" f)
                    | Error e ->
                        eprintfn "fix failed: %s" e
                        exit 1
                let d = Doctor.run (configPath, dataDir)
                Doctor.print d
                exit 0

            // 启动前先执行 doctor/fix（决策 51 第二问：先检查修复，再启动组件）
            if switches.fix then
                match Replay.replay dataDir true with
                | Ok outcome ->
                    for f in outcome.truncatedFiles do
                        logInfo(sprintf "startup fix truncated %s" f)
                | Error e ->
                    eprintfn "fix failed: %s" e
                    exit 1
            else
                match Replay.replay dataDir false with
                | Ok _ -> ()
                | Error e ->
                    eprintfn "log damaged: %s (run with --fix to repair)" e
                    exit 1

            // 自动配对（决策 64）：server+client 同进程时，本机桌面 Client 自动获得令牌
            if switches.server && switches.client then
                let result =
                    try
                        let cfg = TomlCodec.tryParse (File.ReadAllText configPath)
                        match cfg with
                        | Ok cfg ->
                            let token = Wanxiang.Config.Auth.generateToken ()
                            let hash = Wanxiang.Config.Auth.hashToken token
                            let hasLocal =
                                cfg.authClients |> List.exists (fun c -> c.name = "wanxiang-desktop")
                            if hasLocal then
                                Ok()
                            else
                                let newCfg =
                                    { cfg with
                                        authClients =
                                            { tokenHash = hash
                                              name = "wanxiang-desktop"
                                              createdAtUtc = DateTimeOffset.UtcNow
                                              lastSeenUtc = None
                                              revoked = false }
                                            :: cfg.authClients }
                                // 写回 TOML（自动配对）
                                File.WriteAllText(configPath, TomlCodec.serialize newCfg)
                                Ok()
                        | Error e -> Error(String.concat "; " e)
                    with e ->
                        Error e.Message
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
                    let app = Wanxiang.Server.ServerApp(dataDir, configPath, false, pwaDir, logInfo)
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
                // 无 UI：服务器等待信号优雅关闭（决策 101/102）
                use cts = new CancellationTokenSource()
                let mutable sigCount = 0
                let handler = ConsoleCancelEventHandler(fun _ e ->
                    sigCount <- sigCount + 1
                    if sigCount >= 2 then
                        e.Cancel <- false
                        Environment.Exit 130
                    else
                        e.Cancel <- true
                        logInfo "shutting down gracefully (send signal again to force)"
                        stopServer ()
                        cts.Cancel())
                Console.CancelKeyPress.AddHandler handler
                try
                    Task.Delay(Timeout.Infinite, cts.Token).Wait()
                with :? AggregateException ->
                    ()
                stopServer ()
                0
        with e ->
            eprintfn "wanxiang: %s" e.Message
            exit 1
