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
        }

    let ensureStarted () : Result<unit, string> =
        lock lockObj (fun () ->
            if not (isNull process) && not process.HasExited then
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
                        for kv in config.env do psi.Environment[kv.Key] <- kv.Value
                        let p = Process.Start psi
                        process <- p
                        stdin <- p.StandardInput
                        // 日志转发（决策 164：附加 id/pid/时间）
                        p.ErrorDataReceived.Add(fun e ->
                            if not (isNull e.Data) then onLog(sprintf "[mcp:%s pid=%d] %s" config.id p.Id e.Data))
                        p.BeginErrorReadLine()
                        readerTask <- Task.Run<Task>(Func<Task>(fun () -> readLoop p))
                        // 初始化握手
                        this.RequestRaw("initialize", JsonObject()) |> Async.AwaitTask |> Async.RunSynchronously |> ignore
                        let notif = JsonObject()
                        notif["jsonrpc"] <- "2.0"
                        notif["method"] <- "notifications/initialized"
                        writeLine notif
                        Ok()
                    with e ->
                        Error(sprintf "mcp %s start failed: %s" config.id e.Message))

    member _.Id = config.id

    member private this.RequestRaw(method: string, paramsObj: JsonObject) : Task<JsonNode> =
        task {
            let id = Interlocked.Increment(&nextId)
            let idNode = JsonNode.op_Implicit id
            let req = JsonObject()
            req["jsonrpc"] <- "2.0"
            req["id"] <- idNode
            req["method"] <- method
            if paramsObj.Count > 0 then req["params"] <- paramsObj
            let tcs = TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously)
            pending[idNode.ToJsonString()] <- tcs
            try
                writeLine req
            with e ->
                pending.TryRemove(idNode.ToJsonString()) |> ignore
                tcs.TrySetException e |> ignore
            return! tcs.Task
        }

    /// 发送 JSON-RPC 请求并等待响应（按配置限流并发）。
    member this.Request(method: string, paramsObj: JsonObject) : Task<JsonNode> =
        task {
            match ensureStarted () with
            | Error e ->
                return JsonNode.Parse(sprintf """{"error":"%s"}""" (e.Replace("\"", "'")))
            | Ok () ->
                let acquired: bool = semaphore.WaitAsync(Timeout.Infinite).GetAwaiter().GetResult()
                try
                    return! this.RequestRaw(method, paramsObj)
                finally
                    if acquired then
                        semaphore.Release() |> ignore
        }

    member this.ListTools() : Task<(string * string) list> =
        task {
            let! resp = this.Request("tools/list", JsonObject())
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
    member this.Call(toolName: string, argsJson: string) : Task<string> =
        task {
            let paramsObj = JsonObject()
            paramsObj["name"] <- toolName
            let args =
                try JsonNode.Parse argsJson
                with _ -> JsonObject()
            paramsObj["arguments"] <- args
            let! resp = this.Request("tools/call", paramsObj)
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

    member _.Stop() =
        lock lockObj (fun () ->
            if not (isNull process) then
                try process.Kill(entireProcessTree = true) with _ -> ()
                process.Dispose()
                process <- null)

/// 工具注册表：内置工具 + MCP 工具（决策 95-100）。
/// 会话配置只保存稳定标识（builtin:... / mcp:<server-id>/<tool-name>）。
type ToolRegistry(getMcpServers: unit -> Map<string, McpServerConfig>, onLog: string -> unit) =

    let mcpClients = ConcurrentDictionary<string, McpClient>()

    member _.GetMcpClient(id: string) : McpClient option =
        match getMcpServers () |> Map.tryFind id with
        | None -> None
        | Some cfg -> mcpClients.GetOrAdd(id, fun _ -> McpClient(cfg, onLog)) |> Some

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
                            client.Call(toolName, args) |> Async.AwaitTask |> Async.StartAsTask),
                        name = sprintf "mcp_%s_%s" serverId toolName,
                        description = sprintf "MCP tool %s/%s" serverId toolName)
                tools <- tools @ [ fn ]
            | None -> ()
        tools

    /// 关闭全部 MCP 客户端（排空后关闭，决策 98）。
    member _.Dispose() =
        for kv in mcpClients do
            kv.Value.Stop()
