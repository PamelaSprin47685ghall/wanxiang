namespace Wanxiang.Server

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Wanxiang.Config
open Wanxiang.Core

/// 内置工具（决策 95：随万象编译发布，由代码注册）。
/// 稳定标识：builtin:echo / builtin:time / builtin:file.read
type BuiltinTools private () =

    static member All() : AITool list =
        let echo =
            AIFunctionFactory.Create(
                Func<string, string>(fun text -> text),
                name = "builtin_echo",
                description = "Echo back the input text. Useful for testing tool calling.")
        let time =
            AIFunctionFactory.Create(
                Func<string>(fun () -> DateTimeOffset.UtcNow.ToString("o")),
                name = "builtin_time",
                description = "Returns the current UTC time in ISO 8601 format.")
        let fileRead =
            AIFunctionFactory.Create(
                Func<string, string>(fun path ->
                    try
                        let fi = FileInfo(path)
                        if fi.Length > 1024L * 1024L then "file too large (max 1 MiB)"
                        else File.ReadAllText(path, Encoding.UTF8)
                    with e ->
                        sprintf "error: %s" e.Message),
                name = "builtin_file_read",
                description = "Reads a text file from the local filesystem (max 1 MiB). Path must be absolute.")
        [ echo :> AITool; time :> AITool; fileRead :> AITool ]

/// MCP stdio 客户端：JSON-RPC 2.0 over stdio（决策 96-100）。
/// - 同一 MCP 配置共享一个子进程实例，按需启动；
/// - 并发调用使用独立 request id，返回可乱序；
/// - maxConcurrency 用信号量限制。
type McpClient(config: McpServerConfig, onLog: string -> unit) as this =

    let lockObj = obj()
    let mutable process: Process = null
    let mutable stdin: StreamWriter = null
    let mutable readerTask: Task = null
    let pending = ConcurrentDictionary<string, TaskCompletionSource<JsonNode>>()
    let mutable nextId = 0
    let semaphore = SemaphoreSlim(config.maxConcurrency |> Option.defaultValue Int32.MaxValue)
    /// 等待名额或执行中的调用数（Stop 排空判定：排队调用仍按旧配置执行，决策 100）
    let mutable activeCount = 0
    /// Stop 已调用（决策 98：排空后不再接受任何调用，绝不重新拉起子进程）
    let mutable stopped = false

    let writeLine (o: JsonObject) =
        lock lockObj (fun () ->
            stdin.WriteLine(o.ToJsonString())
            stdin.Flush())

    let readLoop (p: Process) =
        task {
            try
                while not p.StandardOutput.EndOfStream do
                    let line = p.StandardOutput.ReadLine()
                    if not (String.IsNullOrWhiteSpace line) then
                        try
                            let node = JsonNode.Parse line
                            match node with
                            | :? JsonObject as o ->
                                let mutable idNode: JsonNode = null
                                if o.TryGetPropertyValue("id", &idNode) && not (isNull idNode) then
                                    let key = idNode.ToJsonString()
                                    match pending.TryGetValue key with
                                    | true, tcs ->
                                        pending.TryRemove key |> ignore
                                        tcs.TrySetResult o |> ignore
                                    | _ -> ()
                            | _ -> ()
                        with _ -> ()
            with _ -> ()
            // 子进程退出/读取异常：把全部在途调用结束为失败 Tool Result（决策 97：不自动重放，正常记账失败结果）
            for kv in pending do
                kv.Value.TrySetResult(JsonNode.Parse("""{"error":"mcp process exited"}""")) |> ignore
            pending.Clear()
        }

    let ensureStarted () : Result<unit, string> =
        lock lockObj (fun () ->
            if stopped then
                Error(sprintf "mcp %s: server stopped" config.id)
            elif not (isNull process) && not process.HasExited then
                Ok()
            else
                match config.command with
                | None -> Error(sprintf "mcp %s: no command configured" config.id)
                | Some cmd ->
                    try
                        let psi = ProcessStartInfo(cmd)
                        for a in config.args do psi.ArgumentList.Add a
                        psi.RedirectStandardInput <- true
                        psi.RedirectStandardOutput <- true
                        psi.RedirectStandardError <- true
                        psi.UseShellExecute <- false
                        // Q165：环境变量白名单化（必要基础项 + TOML 显式配置），不继承完整父环境
                        psi.Environment.Clear()
                        for key in [ "PATH"; "HOME"; "LANG"; "TMPDIR"; "TZ" ] do
                            match Environment.GetEnvironmentVariable key with
                            | v when not (String.IsNullOrEmpty v) -> psi.Environment[key] <- v
                            | _ -> ()
                        for kv in config.env do psi.Environment[kv.Key] <- kv.Value
                        let p = Process.Start psi
                        process <- p
                        stdin <- p.StandardInput
                        // 日志转发（决策 164：附加 id/pid/时间；密钥脱敏由 Stderr.info 统一处理，Q164）
                        p.ErrorDataReceived.Add(fun e ->
                            if not (isNull e.Data) then onLog(sprintf "[mcp:%s pid=%d] %s" config.id p.Id e.Data))
                        p.BeginErrorReadLine()
                        readerTask <- Task.Run<Task>(Func<Task>(fun () -> readLoop p))
                        // 初始化握手（带超时，MCP 子进程挂起不会无限阻塞会话生成）。
                        // 响应必须含 result（成功）且无 error 字段——否则握手失败，终止挂起进程，
                        // 避免把"超时/失败"当作成功、让每次调用都卡满超时窗口。
                        let initResp =
                            this.RequestRaw("initialize", JsonObject(), CancellationToken.None, 15000)
                            |> Async.AwaitTask
                            |> Async.RunSynchronously
                        let fail (reason: string) =
                            try
                                if not (isNull process) && not process.HasExited then
                                    process.Kill(entireProcessTree = true)
                            with _ -> ()
                            try if not (isNull process) then process.Dispose() with _ -> ()
                            process <- null
                            Error reason
                        match initResp with
                        | :? JsonObject as o ->
                            let mutable errNode: JsonNode = null
                            let mutable resultNode: JsonNode = null
                            if o.TryGetPropertyValue("error", &errNode) && not (isNull errNode) then
                                fail(sprintf "mcp %s initialize rejected: %s" config.id (errNode.ToJsonString()))
                            elif o.TryGetPropertyValue("result", &resultNode) && not (isNull resultNode) then
                                // 握手成功：发送 initialized 通知
                                let notif = JsonObject()
                                notif["jsonrpc"] <- "2.0"
                                notif["method"] <- "notifications/initialized"
                                writeLine notif
                                Ok()
                            else
                                fail(sprintf "mcp %s: unexpected initialize response" config.id)
                        | _ -> fail(sprintf "mcp %s: invalid initialize response" config.id)
                    with e ->
                        Error(sprintf "mcp %s start failed: %s" config.id e.Message))

    member _.Id = config.id

    member private this.RequestRaw(method: string, paramsObj: JsonObject, ct: CancellationToken, ?timeoutMs: int) : Task<JsonNode> =
        task {
            let id = Interlocked.Increment(&nextId)
            let idNode = JsonNode.op_Implicit id
            let req = JsonObject()
            req["jsonrpc"] <- "2.0"
            req["id"] <- idNode
            req["method"] <- method
            if paramsObj.Count > 0 then req["params"] <- paramsObj
            let tcs = TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously)
            let key = idNode.ToJsonString()
            pending[key] <- tcs
            try
                writeLine req
            with e ->
                pending.TryRemove key |> ignore
                tcs.TrySetException e |> ignore
            let timeout =
                timeoutMs |> Option.map (float >> TimeSpan.FromMilliseconds) |> Option.defaultValue Timeout.InfiniteTimeSpan
            try
                return! tcs.Task.WaitAsync(timeout, ct)
            with
            | :? TimeoutException ->
                pending.TryRemove key |> ignore
                tcs.TrySetCanceled() |> ignore
                return JsonNode.Parse("""{"error":"mcp request timeout"}""")
            | :? OperationCanceledException ->
                pending.TryRemove key |> ignore
                tcs.TrySetCanceled() |> ignore
                return JsonNode.Parse("""{"error":"cancelled"}""")
        }

    /// 发送 JSON-RPC 请求并等待响应（按配置限流并发；Q167：排队中的调用取消时不启动子进程，先等名额再 ensureStarted）。
    /// activeCount 统计等待名额+执行中的调用数：Stop 排空时据此等待（决策 100：已排队调用仍按旧配置执行），
    /// 避免 semaphore.Dispose 让排队中的调用立即失败。
    member this.Request(method: string, paramsObj: JsonObject, ct: CancellationToken) : Task<JsonNode> =
        task {
            let mutable acquired = false
            Interlocked.Increment(&activeCount)
            let mutable result = JsonNode.Parse("""{"error":"mcp server stopped"}""")
            try
                let! a = semaphore.WaitAsync(Timeout.Infinite, ct)
                acquired <- a
                if acquired then
                    // 拿到名额后才启动子进程（若已退出/未启动则按需启动；排队中的调用不触发启动）
                    match ensureStarted () with
                    | Error e -> result <- JsonNode.Parse(sprintf """{"error":"%s"}""" (e.Replace("\"", "'")))
                    | Ok () ->
                        let! resp = this.RequestRaw(method, paramsObj, ct)
                        result <- resp
            with
            | :? OperationCanceledException -> result <- JsonNode.Parse("""{"error":"cancelled"}""")
            | :? ObjectDisposedException -> result <- JsonNode.Parse("""{"error":"mcp server stopped"}""")
            | e -> result <- JsonNode.Parse(sprintf """{"error":"%s"}""" (e.Message.Replace("\"", "'")))
            // 排空期间信号量可能已被 Stop 超时后 Dispose：Release 抛异常不影响真实响应返回
            if acquired then
                try semaphore.Release() |> ignore with _ -> ()
            Interlocked.Decrement(&activeCount) |> ignore
            return result
        }

    member this.ListTools() : Task<(string * string) list> =
        task {
            let! resp = this.Request("tools/list", JsonObject(), CancellationToken.None)
            let results = System.Collections.Generic.List<string * string>()
            match resp with
            | :? JsonObject as o ->
                let mutable r: JsonNode = null
                if o.TryGetPropertyValue("result", &r) && not (isNull r) && r.GetValueKind() = JsonValueKind.Object then
                    let mutable toolsNode: JsonNode = null
                    if r.AsObject().TryGetPropertyValue("tools", &toolsNode) && not (isNull toolsNode) && toolsNode.GetValueKind() = JsonValueKind.Array then
                        for t in toolsNode.AsArray() do
                            if t <> null && t.GetValueKind() = JsonValueKind.Object then
                                let toolObj = t.AsObject()
                                let mutable n: JsonNode = null
                                let name = if toolObj.TryGetPropertyValue("name", &n) && not (isNull n) then n.GetValue<string>() else ""
                                let desc = if toolObj.TryGetPropertyValue("description", &n) && not (isNull n) then n.GetValue<string>() else ""
                                results.Add(name, desc)
            | _ -> ()
            return List.ofSeq results
        }

    /// 调用 MCP 工具。返回完整 result（JSON 文本）。
    /// P3-3：ct 透传，取消生成时排队中未启动的调用直接取消（Q167），不启动子进程。
    /// Q170：参数解析失败不调用 Tool，直接生成失败 Tool Result（保留原 Tool Call，交 Provider 处理）。
    member this.Call(toolName: string, argsJson: string, ct: CancellationToken) : Task<string> =
        task {
            let paramsObj = JsonObject()
            paramsObj["name"] <- toolName
            let args =
                try
                    match JsonNode.Parse argsJson with
                    | null -> None
                    | node -> Some node
                with _ -> None
            match args with
            | None ->
                // Q170：参数无法解析为合法 JSON → 不调用 Tool
                return """{"error":"invalid arguments: not valid JSON"}"""
            | Some parsedArgs ->
                paramsObj["arguments"] <- parsedArgs
                let! resp = this.Request("tools/call", paramsObj, ct)
                match resp with
                | :? JsonObject as o ->
                    let mutable r: JsonNode = null
                    if o.TryGetPropertyValue("result", &r) && not (isNull r) then
                        return r.ToJsonString()
                    else
                        let mutable e: JsonNode = null
                        if o.TryGetPropertyValue("error", &e) && not (isNull e) then
                            return sprintf """{"error":%s}""" (e.ToJsonString())
                        else
                            return """{"error":"empty mcp response"}"""
                | _ -> return """{"error":"invalid mcp response"}"""
        }

    /// 排空关闭（决策 98/100）：停止接收新调用，允许在途调用与已排队调用自然完成；排空后关闭子进程。
    /// 不强制中断在途调用（避免已产生副作用却丢失结果）；排空窗口超时后才强制结束。
    member this.Stop() =
        // 0. 标记停止：此后 ensureStarted/Request 一律拒绝（排空中的在途/排队调用不受影响），
        //    防止排空窗口内旧工具快照发起新调用重新拉起孤儿子进程
        lock lockObj (fun () -> stopped <- true)
        // 1. 排空等待：pending（在途 JSON-RPC）与 activeCount（等待名额+执行中）都归零，
        //    或超出排空窗口（挂起进程/无限等待的调用）。期间排队调用仍按旧配置执行（决策 100），
        //    绝不在排空开始瞬间 Dispose 信号量把排队调用打成失败。
        let deadline = DateTimeOffset.UtcNow.AddSeconds 5.0
        while (pending.Count > 0 || activeCount > 0) && DateTimeOffset.UtcNow < deadline do
            Thread.Sleep 50
        // 2. 排空窗口结束仍挂起的调用：结束为失败结果（决策 97：不自动重放）
        if pending.Count > 0 then
            for kv in pending do
                kv.Value.TrySetResult(JsonNode.Parse("""{"error":"mcp server stopping"}""")) |> ignore
            pending.Clear()
        // 3. 仍阻塞在 WaitAsync 的排队调用：释放信号量使其失败结束（进程关闭不能无限等待）
        if activeCount > 0 then
            try semaphore.Dispose() with _ -> ()
        // 4. 关闭子进程（无论排空是否完成，避免进程悬挂泄漏）
        lock lockObj (fun () ->
            if not (isNull process) then
                try process.Kill(entireProcessTree = true) with _ -> ()
                process.Dispose()
                process <- null)

/// 工具注册表：内置工具 + MCP 工具（决策 95-100）。
/// 会话配置只保存稳定标识（builtin:... / mcp:<server-id>/<tool-name>）。
type ToolRegistry(getMcpServers: unit -> Map<string, McpServerConfig>, onLog: string -> unit) =

    let mcpClients = ConcurrentDictionary<string, McpClient>()
    /// 已缓存客户端对应的配置指纹（P2-1：配置热更新后旧客户端排空关闭，新建客户端）。
    let mcpFingerprints = ConcurrentDictionary<string, string>()
    let registryLock = obj()

    let fingerprint (cfg: McpServerConfig) : string =
        sprintf "%s|%A|%A|%s|%A"
            (cfg.command |> Option.defaultValue "")
            cfg.args
            cfg.env
            (cfg.url |> Option.defaultValue "")
            cfg.maxConcurrency

    member _.GetMcpClient(id: string) : McpClient option =
        lock registryLock (fun () ->
            match getMcpServers () |> Map.tryFind id with
            | None ->
                // 配置已删除：排空关闭旧客户端（决策 98）
                match mcpClients.TryRemove id with
                | true, old -> old.Stop()
                | _ -> ()
                None
            | Some cfg ->
                let fp = fingerprint cfg
                match mcpClients.TryGetValue id with
                | true, client ->
                    match mcpFingerprints.TryGetValue id with
                    | true, fp when fp = fingerprint cfg -> Some client
                    | _ ->
                        // 配置变化：旧客户端排空关闭，新建（决策 98：新调用立即使用新配置）
                        match mcpClients.TryRemove id with
                        | true, old -> old.Stop()
                        | _ -> ()
                        let client = McpClient(cfg, onLog)
                        mcpClients[id] <- client
                        mcpFingerprints[id] <- fingerprint cfg
                        Some client
                | _ ->
                    let client = McpClient(cfg, onLog)
                    mcpClients[id] <- client
                    mcpFingerprints[id] <- fingerprint cfg
                    Some client)

    /// 构建会话启用的 AITool 列表。
    member this.BuildTools(config: SessionConfig) : AITool list =
        let requested = config.tools |> Set.ofList
        let mutable tools: AITool list = []
        for t in BuiltinTools.All() do
            // 兼容标识映射：builtin:echo ↔ builtin_echo
            let canonicalName = "builtin:" + t.Name.Replace("builtin_", "").Replace("_", ".")
            if requested.Contains canonicalName then
                tools <- tools @ [ t ]
        for (serverId, toolName) in requested |> Seq.choose (fun id ->
            if id.StartsWith "mcp:" then
                let rest = id.Substring 4
                match rest.IndexOf '/' with
                | -1 -> None
                | i -> Some(rest.Substring(0, i), rest.Substring(i + 1))
            else None) do
            match this.GetMcpClient serverId with
            | Some client ->
                let fn =
                    AIFunctionFactory.Create(
                        Func<string, CancellationToken, Task<string>>(fun args ct ->
                            client.Call(toolName, args, ct)),
                        name = sprintf "mcp_%s_%s" serverId toolName,
                        description = sprintf "MCP tool %s/%s" serverId toolName)
                tools <- tools @ [ fn ]
            | None -> ()
        tools

    /// 关闭全部 MCP 客户端（排空后关闭，决策 98）。
    member _.Dispose() =
        for kv in mcpClients do
            kv.Value.Stop()
