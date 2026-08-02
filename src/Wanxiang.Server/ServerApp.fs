namespace Wanxiang.Server

open System
open System.IO
open System.Net.WebSockets
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.StaticFiles
open Microsoft.Extensions.FileProviders
open Microsoft.Extensions.Hosting
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Store

/// 万象服务器实例（组装：数据锁 → replay → 配置 → 协调器 → 编排 → 连接 → HTTP）。
/// 数据锁（决策 105/Q110）：运行期全程持有到 Stop，确保同一数据目录只有一个 S 进程。
/// startupOutcome：调用方（Program）在持锁状态下完成的一次性 replay/fix 结果，避免二次 replay；
/// 锁仍由本构造器独立获取并持有。
type ServerApp(dataDir: string, configPath: string, fix: bool, pwaDir: string option, logInfo: string -> unit, ?startupOutcome: ReplayOutcome) =

    /// 运行期数据锁：获取失败即启动失败；Stop 时释放（严格单进程，决策 105）
    /// 无论 startupOutcome 是否提供都必须检查 Acquire 结果（第二进程抢占时拒绝启动）。
    let lockHandle: Result<DataLock, string> = DataLock.Acquire dataDir

    do
        // 锁获取失败（另一实例已持锁）→ 启动失败，绝不静默无锁运行（决策 105）
        match lockHandle with
        | Error e -> failwith e
        | Ok _ -> ()

    let replayOutcome =
        match startupOutcome with
        | Some outcome -> outcome
        | None ->
            match lockHandle with
            | Error e -> failwith e
            | Ok _ ->
                // 锁由 lockHandle 全程持有（不能 use 释放）；replay 在锁内执行
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

    /// 按 TOML 重建配对限流器（Q188：每远端地址每分钟失败次数/冻结分钟可通过 TOML 调整；启动即生效）
    let rebuildFailureTracker (cfg: AppConfig) =
        failureTracker <-
            Auth.FailureTracker(
                TimeSpan.FromMinutes(float cfg.pairingFailureWindowMinutes),
                cfg.pairingMaxFailures,
                TimeSpan.FromMinutes(float cfg.pairingFreezeMinutes))

    let onConfigReloaded (cfg: AppConfig) =
        // 决策 41：配置热重载后新 apiKey 也纳入 stderr 脱敏集（Q164 密钥不落 stderr）
        Stderr.registerSecrets (
            cfg.providers
            |> Seq.choose (fun kv -> kv.Value.apiKey)
            |> Seq.filter (fun k -> not (String.IsNullOrWhiteSpace k)))
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
            rebuildFailureTracker cfg
        with _ -> ()

    let onConfigRejected (errs: string) =
        Stderr.write "config-rejected" [ "errors", errs ]

    let executeCommand (clientCursor: CommitId) (cmd: ClientCommand) : CommandExecutionResult =
        let coord =
            match coordinator with
            | Some c -> c
            | None -> failwith "coordinator not ready"
        try
            // 默认会话配置注入：客户端创建/修改会话时若未提供有效 provider+model
            // （SessionConfig.empty 或硬编码过时值），用 TOML 第一个 provider 的 model 填充。
            // 幂等性：注入是确定性的（同 TOML 下同输入同输出），commandId 基于注入后 cmd 计算，重试一致。
            let cmd =
                let fill (cfg: SessionConfig) : SessionConfig =
                    if SessionConfig.isValid cfg then cfg
                    else
                        match (currentConfig ()).providers |> Map.toList |> List.tryHead with
                        | None -> cfg // 无 provider 配置：保持原样，由 plan 的 isValid 拒绝并给出明确错误
                        | Some (id, p) -> { cfg with provider = id; model = p.model }
                match cmd with
                | CreateConversation d -> CreateConversation {| d with config = fill d.config |}
                // UpdateConversationConfig：仅当客户端 config 无效/为空时注入 TOML 默认 provider；
                // 显式有效的 config（含用户选定 provider/model）绝不改写（决策 86 完整快照语义）
                | UpdateConversationConfig d -> UpdateConversationConfig {| d with config = fill d.config |}
                // ForkConversation：配置由父会话投影继承（决策 81），无需填充
                | other -> other
            match CommandEngine.plan (coord.Projection) clientCursor cmd with
            | Rejected e ->
                // Q145：commandId 冲突（同 commandId 不同载荷）视为严重错误，写 stderr
                match e with
                | CommandIdConflict _ -> Stderr.write "command-id-conflict" [ "commandType", ClientCommand.commandType cmd ]
                | _ -> ()
                CommandExecutionResult.CommandFailed e
            | PlanResult.IdempotentReplay cid -> CommandExecutionResult.CommandIdempotent cid
            | Planned plan ->
                match cmd with
                | SendUserMessage _ ->
                    // 决策 35/36/38：写权限按命令涉及投影是否追平判断，落后立即拒绝（不排队）。
                    // plan 已对 SendUserMessage 计算 watermark；此处必须检查，否则落后客户端可绕过 stale 拒绝。
                    if clientCursor < plan.watermark then
                        CommandExecutionResult.CommandFailed(StaleProjection plan.watermark)
                    else
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
                    | SubmitResult.IdempotentReplay c -> CommandExecutionResult.CommandIdempotent c.id
                    | SubmitResult.CommandIdRejected e ->
                        // Q145：同 commandId 不同 payload 的冲突（或幂等记录异常）走专用 stderr 事件
                        Stderr.write "command-id-conflict" [ "commandType", ClientCommand.commandType cmd; "message", WanxiangError.message e ]
                        CommandExecutionResult.CommandFailed e
                    | SubmitResult.TruncatedAndReused (_, err) ->
                        // 仅剩运行时投影失败截尾路径；stderr 已由协调器 onTruncated 忠实记录（决策 40）
                        CommandExecutionResult.CommandFailed err
                    | SubmitResult.CommitFailed e -> CommandExecutionResult.CommandFailed e
        with e ->
            Stderr.write "execute-command-error" [ "message", e.ToString() ]
            CommandExecutionResult.CommandFailed(Poisoned e.Message)

    // P3-2：MCP 子进程 stderr 转发前对已知密钥值做精确替换（Q164）。
    let mcpLog (msg: string) = logInfo msg // Stderr.info 内部统一 redact（Q164）

    let toolRegistry = ToolRegistry((fun () -> (currentConfig ()).mcpServers), mcpLog)

    /// 启动服务器（server 开关）。
    member this.Start(servePwa: bool) : unit =
        match ConfigStore.Open(configPath, onConfigReloaded, onConfigRejected) with
        | Error e -> failwith e
        | Ok cs ->
            configStore <- Some cs
            // 决策 41：写 stderr 前注册已知密钥值做精确替换（apiKey 等）；后续异常/日志不再泄漏
            Stderr.registerSecrets (
                cs.Current.providers
                |> Seq.choose (fun kv -> kv.Value.apiKey)
                |> Seq.filter (fun k -> not (String.IsNullOrWhiteSpace k)))
            // Q188：配对限流参数以 TOML 为权威——启动时即按 TOML 重建，而非等首次热重载
            rebuildFailureTracker cs.Current
        let broadcastCommit (commit: Events.Commit) =
            try
                match registry with
                | Some reg -> reg.BroadcastCommit commit
                | None -> ()
            with _ -> ()
        let onTruncated (commit: Events.Commit, err: WanxiangError, byteOffset: int64, file: string) =
            Stderr.truncated commit file byteOffset err
        coordinator <- Some(CommitCoordinator(dataDir, replayOutcome, broadcastCommit, onTruncated))
        registry <- Some(ConnectionRegistry())
        let broadcastToConversation (convId: Guid) (ev: WireEvent) =
            match registry with
            | Some reg -> reg.BroadcastTransient(convId, ev)
            | None -> ()
        orchestrator <-
            Some(ChatOrchestrator(coordinator.Value, currentProjection, broadcastToConversation, toolRegistry, currentConfig, logInfo))
        let attachmentStore = AttachmentStore(dataDir, (currentConfig ()).maxAttachmentBytes, (currentConfig ()).chunkSizeBytes)
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
                        // 先注册获得连接 id（断线清理附件上传用），再构造连接对象
                        let id =
                            match registry with
                            | Some reg -> reg.AddPlaceholder()
                            | None -> 0
                        let conn =
                            WsConnection(
                                id,
                                ws,
                                remote,
                                currentProjection,
                                currentConfig,
                                (fun f ->
                                    match configStore with
                                    | Some cs -> cs.Update f
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
                        match registry with
                        | Some reg -> reg.Set(id, conn)
                        | None -> ()
                        try
                            do! conn.Run(ctx.RequestAborted)
                        finally
                            match registry with
                            | Some reg -> reg.Remove id
                            | None -> ()
                    else
                        ctx.Response.StatusCode <- 404
                }))

        if servePwa then
            match pwaDir with
            | Some dir when Directory.Exists dir ->
                // PWA 产物（.NET WASM AppBundle）含非常规扩展名（.symbols 等）：补全 MIME 映射，避免 Kestrel 对未知类型返回 404
                let contentTypeProvider = FileExtensionContentTypeProvider()
                contentTypeProvider.Mappings[".symbols"] <- "application/octet-stream"
                contentTypeProvider.Mappings[".dat"] <- "application/octet-stream"
                contentTypeProvider.Mappings[".pdb"] <- "application/octet-stream"
                app.UseStaticFiles(StaticFileOptions(FileProvider = new PhysicalFileProvider(Path.GetFullPath dir), ContentTypeProvider = contentTypeProvider))
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
    /// Q102：不设置内部固定超时——先关闭全部 WebSocket 连接让 Kestrel 排空完成；
    /// 强制终止由第二次终止信号（Program）或外部服务管理器负责。
    member this.Stop() : unit =
        if not stopping then
            stopping <- true
            try
                // 先强制关闭全部连接（吊销/断线路径），否则 Kestrel 排空会等待永不结束的 WebSocket
                match registry with
                | Some reg -> for conn in reg.All() do conn.ForceClose()
                | None -> ()
            with _ -> ()
            try
                match host with
                | Some h -> h.StopAsync().GetAwaiter().GetResult()
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
