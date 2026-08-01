namespace Wanxiang.Server

open System
open System.IO
open System.Net.WebSockets
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.FileProviders
open Microsoft.Extensions.Hosting
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Store

/// 万象服务器实例（组装：数据锁 → replay → 配置 → 协调器 → 编排 → 连接 → HTTP）。
type ServerApp(dataDir: string, configPath: string, fix: bool, pwaDir: string option, logInfo: string -> unit) =

    let lockHandle = DataLock.Acquire dataDir
    let replayOutcome =
        match lockHandle with
        | Error e -> failwith e
        | Ok _ ->
            match Replay.replay dataDir fix with
            | Ok outcome ->
                for f in outcome.truncatedFiles do
                    Stderr.replayTruncated f "truncated during startup"
                outcome
            | Error e -> failwith e

    let mutable configStore: ConfigStore option = None
    let mutable coordinator: CommitCoordinator option = None
    let mutable orchestrator: ChatOrchestrator option = None
    let mutable registry: ConnectionRegistry option = None
    let mutable host: IHost option = None
    let mutable stopping = false

    let pairing = Auth.PairingState()
    let mutable failureTracker = Auth.FailureTracker(TimeSpan.FromMinutes 1.0, 5, TimeSpan.FromMinutes 5.0)

    let currentConfig () =
        match configStore with
        | Some cs -> cs.Current
        | None -> failwith "config store not ready"

    let currentProjection () =
        match coordinator with
        | Some c -> c.Projection
        | None -> failwith "coordinator not ready"

    let onConfigReloaded (cfg: AppConfig) =
        // 决策 58：吊销令牌后立即断开对应连接
        try
            match registry with
            | Some reg ->
                for conn in reg.All() do
                    match conn.TokenHash with
                    | Some hash ->
                        let stillValid = cfg.authClients |> List.exists (fun c -> c.tokenHash = hash && not c.revoked)
                        if not stillValid && conn.IsAuthenticated then
                            conn.ForceClose()
                    | None -> ()
            | None -> ()
            failureTracker <-
                Auth.FailureTracker(
                    TimeSpan.FromMinutes(float cfg.pairingFailureWindowMinutes),
                    cfg.pairingMaxFailures,
                    TimeSpan.FromMinutes(float cfg.pairingFreezeMinutes))
        with _ -> ()

    let onConfigRejected (errs: string) =
        Stderr.write "config-rejected" [ "errors", errs ]

    let executeCommand (clientCursor: CommitId) (cmd: ClientCommand) : CommandExecutionResult =
        let coord =
            match coordinator with
            | Some c -> c
            | None -> failwith "coordinator not ready"
        try
            match CommandEngine.plan (coord.Projection) clientCursor cmd with
            | Rejected e ->
                // Q145：commandId 冲突（同 commandId 不同载荷）视为严重错误，写 stderr
                match e with
                | CommandIdConflict _ -> Stderr.write "command-id-conflict" [ "commandType", ClientCommand.commandType cmd ]
                | _ -> ()
                CommandExecutionResult.CommandFailed e
            | IdempotentReplay cid -> CommandExecutionResult.CommandIdempotent cid
            | Planned plan ->
                match cmd with
                | SendUserMessage _ ->
                    match orchestrator with
                    | Some o -> o.HandleSendUserMessage cmd
                    | None -> ()
                    CommandExecutionResult.CommandQueued
                | _ ->
                    let submit =
                        { events = plan.events
                          commandId = Some plan.commandId
                          commandType = Some plan.commandType
                          commandHash = Some plan.canonicalHash
                          nowUtc = None }
                    match coord.Submit submit with
                    | SubmitResult.Committed c -> CommandExecutionResult.CommandCommitted c.id
                    | SubmitResult.TruncatedAndReused (commit, err) ->
                        Stderr.truncated (CommitCodec.commitToJsonLine commit) err
                        CommandExecutionResult.CommandFailed err
                    | SubmitResult.CommitFailed e -> CommandExecutionResult.CommandFailed e
        with e ->
            Stderr.write "execute-command-error" [ "message", e.ToString() ]
            CommandExecutionResult.CommandFailed(Poisoned e.Message)

    let toolRegistry = ToolRegistry((fun () -> (currentConfig ()).mcpServers), logInfo)

    /// 启动服务器（server 开关）。
    member this.Start(servePwa: bool) : unit =
        match ConfigStore.Open(configPath, onConfigReloaded, onConfigRejected) with
        | Error e -> failwith e
        | Ok cs -> configStore <- Some cs
        let broadcastCommit (commit: Events.Commit) =
            try
                match registry with
                | Some reg -> reg.BroadcastCommit commit
                | None -> ()
            with _ -> ()
        let onTruncated (commit: Events.Commit, err: WanxiangError) =
            Stderr.truncated (CommitCodec.commitToJsonLine commit) err
        coordinator <- Some(CommitCoordinator(dataDir, replayOutcome, broadcastCommit, onTruncated))
        registry <- Some(ConnectionRegistry())
        let broadcastToConversation (convId: Guid) (ev: WireEvent) =
            match registry with
            | Some reg -> reg.BroadcastTransient(convId, ev)
            | None -> ()
        orchestrator <-
            Some(ChatOrchestrator(coordinator.Value, currentProjection, broadcastToConversation, toolRegistry, currentConfig, logInfo))
        let attachmentStore = AttachmentStore(dataDir, (currentConfig ()).maxAttachmentBytes)
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls(sprintf "http://%s" (currentConfig ()).listen)
        let app = builder.Build()
        app.UseWebSockets() |> ignore

        app.Map(
            Constants.WsPath,
            Func<HttpContext, Task>(fun (ctx: HttpContext) ->
                task {
                    if ctx.WebSockets.IsWebSocketRequest then
                        let! ws = ctx.WebSockets.AcceptWebSocketAsync()
                        let remote =
                            match ctx.Connection.RemoteIpAddress with
                            | null -> "?"
                            | ip -> ip.ToString()
                        let conn =
                            WsConnection(
                                ws,
                                remote,
                                currentProjection,
                                currentConfig,
                                (fun c ->
                                    match configStore with
                                    | Some cs -> cs.Rewrite c
                                    | None -> Error "config store not ready"),
                                executeCommand,
                                orchestrator.Value,
                                attachmentStore,
                                (fun cursor -> coordinator.Value.CommitsAfter cursor),
                                pairing,
                                failureTracker,
                                (fun code ->
                                    // 决策 46：配对码输出到 stderr，能读取 stderr 的用户有权批准
                                    Stderr.write "pairing-code" [ "code", code; "expiresInSeconds", 300 ]),
                                logInfo)
                        let id =
                            match registry with
                            | Some reg -> reg.Add conn
                            | None -> 0
                        try
                            do! conn.Run(ctx.RequestAborted)
                        finally
                            match registry with
                            | Some reg -> reg.Remove id
                            | None -> ()
                    else
                        ctx.Response.StatusCode <- 404
                }))
        |> ignore

        if servePwa then
            match pwaDir with
            | Some dir when Directory.Exists dir ->
                app.UseStaticFiles(StaticFileOptions(FileProvider = new PhysicalFileProvider(Path.GetFullPath dir)))
                |> ignore
                // 根路径与未知路径回退到 index.html
                app.MapGet(
                    "/",
                    Func<HttpContext, Task>(fun (ctx: HttpContext) ->
                        task {
                            ctx.Response.ContentType <- "text/html"
                            let path = Path.Combine(Path.GetFullPath dir, "index.html")
                            if File.Exists path then
                                let! bytes = File.ReadAllBytesAsync path
                                do! ctx.Response.Body.WriteAsync(bytes)
                            else
                                ctx.Response.StatusCode <- 404
                        }))
                |> ignore
            | _ ->
                logInfo "pwa enabled but pwa dir missing; serving 404 on /"

        host <- Some app
        app.Start()
        logInfo(sprintf "wanxiang server listening on http://%s (ws=%s pwa=%b)" (currentConfig ()).listen Constants.WsPath servePwa)

    /// 优雅关闭：停止接收新工作、取消生成、排空单写者并 flush（决策 101）。
    member this.Stop() : unit =
        if not stopping then
            stopping <- true
            try
                match host with
                | Some h -> h.StopAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult()
                | None -> ()
            with _ -> ()
            try
                match orchestrator with
                | Some o -> o.Dispose()
                | None -> ()
            with _ -> ()
            try
                match coordinator with
                | Some c -> c.Shutdown()
                | None -> ()
            with _ -> ()
            try
                match configStore with
                | Some cs -> cs.Dispose()
                | None -> ()
            with _ -> ()
            try toolRegistry.Dispose() with _ -> ()
            try
                match lockHandle with
                | Ok l -> (l :> IDisposable).Dispose()
                | Error _ -> ()
            with _ -> ()

    interface IDisposable with
        member this.Dispose() = this.Stop()
