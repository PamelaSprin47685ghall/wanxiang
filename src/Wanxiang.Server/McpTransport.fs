namespace Wanxiang.Server

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Wanxiang.Config

/// MCP 传输层：把 JSON-RPC 请求送出去并等回响应。
/// 两种实现——本地 stdio 子进程、远程 Streamable HTTP。
type IMcpTransport =
    abstract Request: method: string * paramsObj: JsonObject * timeoutMs: int * ct: CancellationToken -> Task<JsonNode>
    abstract Notify: method: string * paramsObj: JsonObject -> unit
    abstract Stop: unit -> unit
    /// 在途请求数（排空判定）
    abstract Pending: int

module McpJson =

    let errorNode (message: string) : JsonNode =
        let o = JsonObject()
        let e = JsonObject()
        e["message"] <- message
        o["error"] <- e
        o :> JsonNode

    let initializeParams () : JsonObject =
        let p = JsonObject()
        p["protocolVersion"] <- "2025-06-18"
        let caps = JsonObject()
        caps["roots"] <- JsonObject()
        p["capabilities"] <- caps
        let info = JsonObject()
        info["name"] <- "wanxiang"
        info["version"] <- "1.0"
        p["clientInfo"] <- info
        p

/// 本地 stdio MCP 传输（决策 96-100）。
type McpStdioTransport(config: McpServerConfig, onLog: string -> unit) =

    let gate = obj ()
    let pending = ConcurrentDictionary<string, TaskCompletionSource<JsonNode>>()
    let mutable child: Process = null
    let mutable stdin: StreamWriter = null
    let mutable nextId = 0
    let mutable stopped = false

    let writeLine (o: JsonObject) =
        lock gate (fun () ->
            if isNull stdin then failwith "mcp stdin not ready"
            stdin.WriteLine(o.ToJsonString())
            stdin.Flush())

    let readLoop (p: Process) =
        task {
            try
                while not p.StandardOutput.EndOfStream do
                    let line = p.StandardOutput.ReadLine()
                    if not (String.IsNullOrWhiteSpace line) then
                        try
                            match JsonNode.Parse line with
                            | :? JsonObject as o ->
                                let mutable idNode: JsonNode = null
                                if o.TryGetPropertyValue("id", &idNode) && not (isNull idNode) then
                                    let key = idNode.ToJsonString()
                                    match pending.TryRemove key with
                                    | true, tcs -> tcs.TrySetResult o |> ignore
                                    | _ -> ()
                            | _ -> ()
                        with _ -> ()
            with _ -> ()
            // 子进程退出：在途调用结束为失败结果（决策 97：不自动重放）
            for kv in pending do
                kv.Value.TrySetResult(McpJson.errorNode "MCP 进程已退出") |> ignore
            pending.Clear()
        }

    /// 子进程环境白名单化（Q165）：不继承完整父环境，避免密钥外泄。
    let applyEnvironment (psi: ProcessStartInfo) =
        psi.Environment.Clear()
        for key in [ "PATH"; "HOME"; "LANG"; "LC_ALL"; "TMPDIR"; "TZ" ] do
            match Environment.GetEnvironmentVariable key with
            | v when not (String.IsNullOrEmpty v) -> psi.Environment[key] <- v
            | _ -> ()
        for KeyValue(k, v) in config.env do psi.Environment[k] <- v

    let killChild () =
        try
            if not (isNull child) && not child.HasExited then child.Kill(entireProcessTree = true)
        with _ -> ()
        try if not (isNull child) then child.Dispose() with _ -> ()
        child <- null
        stdin <- null

    member private this.SendRaw(method: string, paramsObj: JsonObject, timeoutMs: int, ct: CancellationToken) : Task<JsonNode> =
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
            let mutable writeError: string option = None
            try
                writeLine req
            with ex ->
                pending.TryRemove key |> ignore
                writeError <- Some(sprintf "写入 MCP 失败：%s" ex.Message)
            match writeError with
            | Some message -> return McpJson.errorNode message
            | None ->
                let timeout =
                    if timeoutMs > 0 then TimeSpan.FromMilliseconds(float timeoutMs) else Timeout.InfiniteTimeSpan
                try
                    return! tcs.Task.WaitAsync(timeout, ct)
                with
                | :? TimeoutException ->
                    pending.TryRemove key |> ignore
                    return McpJson.errorNode(sprintf "MCP 调用超时（%d 秒）" (timeoutMs / 1000))
                | :? OperationCanceledException ->
                    pending.TryRemove key |> ignore
                    return McpJson.errorNode "调用已取消"
        }

    /// 按需启动子进程并完成 initialize 握手。
    member private this.EnsureStarted() : Result<unit, string> =
        lock gate (fun () ->
            if stopped then Error(sprintf "MCP %s 已停止" config.id)
            elif not (isNull child) && not child.HasExited then Ok()
            else
                match config.command with
                | None -> Error(sprintf "MCP %s 未配置 command" config.id)
                | Some cmd ->
                    try
                        let psi = ProcessStartInfo cmd
                        for a in config.args do psi.ArgumentList.Add a
                        psi.RedirectStandardInput <- true
                        psi.RedirectStandardOutput <- true
                        psi.RedirectStandardError <- true
                        psi.UseShellExecute <- false
                        applyEnvironment psi
                        let p = Process.Start psi
                        child <- p
                        stdin <- p.StandardInput
                        p.ErrorDataReceived.Add(fun e ->
                            if not (isNull e.Data) then onLog(sprintf "[mcp:%s pid=%d] %s" config.id p.Id e.Data))
                        p.BeginErrorReadLine()
                        Task.Run<Task>(Func<Task>(fun () -> readLoop p)) |> ignore
                        let response =
                            this.SendRaw("initialize", McpJson.initializeParams (), 15000, CancellationToken.None)
                            |> fun t -> t.GetAwaiter().GetResult()
                        match response with
                        | :? JsonObject as o ->
                            let mutable errNode: JsonNode = null
                            let mutable resultNode: JsonNode = null
                            if o.TryGetPropertyValue("error", &errNode) && not (isNull errNode) then
                                killChild ()
                                Error(sprintf "MCP %s initialize 被拒绝：%s" config.id (errNode.ToJsonString()))
                            elif o.TryGetPropertyValue("result", &resultNode) && not (isNull resultNode) then
                                let notif = JsonObject()
                                notif["jsonrpc"] <- "2.0"
                                notif["method"] <- "notifications/initialized"
                                writeLine notif
                                Ok()
                            else
                                killChild ()
                                Error(sprintf "MCP %s initialize 响应异常" config.id)
                        | _ ->
                            killChild ()
                            Error(sprintf "MCP %s initialize 响应不是 JSON 对象" config.id)
                    with ex ->
                        killChild ()
                        Error(sprintf "MCP %s 启动失败：%s" config.id ex.Message))

    interface IMcpTransport with
        member this.Request(method, paramsObj, timeoutMs, ct) =
            task {
                match this.EnsureStarted() with
                | Error e -> return McpJson.errorNode e
                | Ok () -> return! this.SendRaw(method, paramsObj, timeoutMs, ct)
            }

        member _.Notify(method, paramsObj) =
            try
                let notif = JsonObject()
                notif["jsonrpc"] <- "2.0"
                notif["method"] <- method
                if paramsObj.Count > 0 then notif["params"] <- paramsObj
                writeLine notif
            with _ -> ()

        member _.Pending = pending.Count

        member _.Stop() =
            lock gate (fun () -> stopped <- true)
            for kv in pending do
                kv.Value.TrySetResult(McpJson.errorNode "MCP 正在关闭") |> ignore
            pending.Clear()
            lock gate (fun () -> killChild ())

/// 远程 MCP：Streamable HTTP 传输。
///
/// 之前 `[mcp.x] url` 是死配置——能写进 TOML、能通过校验，然后每次调用
/// 都报「未配置 command」。这里把它做通：POST JSON-RPC，响应可以是
/// 单个 JSON 对象，也可以是 SSE 流（取最后一个带 id 的响应帧）。
type McpHttpTransport(config: McpServerConfig, endpoint: string, onLog: string -> unit) =

    let http = new HttpClient()
    let mutable nextId = 0
    let mutable sessionId: string option = None
    let mutable initialized = false
    let mutable inFlight = 0
    let gate = obj ()

    do http.Timeout <- TimeSpan.FromSeconds(float (max 30 config.callTimeoutSeconds) + 10.0)

    /// SSE 帧里挑出 JSON-RPC 响应（`data:` 行拼接后解析）。
    let parseSse (body: string) : JsonNode option =
        body.Split('\n')
        |> Array.filter (fun line -> line.StartsWith("data:", StringComparison.Ordinal))
        |> Array.map (fun line -> line.Substring(5).Trim())
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.rev
        |> Array.tryPick (fun payload ->
            try
                match JsonNode.Parse payload with
                | :? JsonObject as o ->
                    let mutable idNode: JsonNode = null
                    if o.TryGetPropertyValue("id", &idNode) then Some(o :> JsonNode) else None
                | _ -> None
            with _ -> None)

    member private _.Post(envelope: JsonObject, timeoutMs: int, ct: CancellationToken) : Task<JsonNode> =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            request.Content <- new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json")
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream") |> ignore
            match sessionId with
            | Some sid -> request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sid) |> ignore
            | None -> ()
            for KeyValue(k, v) in config.env do
                // env 在 HTTP 传输下语义为附加请求头（本地 stdio 下才是环境变量）
                request.Headers.TryAddWithoutValidation(k, v) |> ignore
            use timeout = new CancellationTokenSource(if timeoutMs > 0 then timeoutMs else -1)
            use linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token)
            let! response = http.SendAsync(request, linked.Token)
            let mutable header: seq<string> = null
            if response.Headers.TryGetValues("Mcp-Session-Id", &header) then
                sessionId <- header |> Seq.tryHead
            let! body = response.Content.ReadAsStringAsync linked.Token
            if not response.IsSuccessStatusCode then
                return McpJson.errorNode(sprintf "MCP HTTP %d：%s" (int response.StatusCode) (body.Substring(0, min 200 body.Length)))
            elif String.IsNullOrWhiteSpace body then
                return McpJson.errorNode "MCP 响应为空"
            else
                let mediaType =
                    match response.Content.Headers.ContentType with
                    | null -> ""
                    | ct -> ct.MediaType
                if mediaType.Contains "event-stream" then
                    match parseSse body with
                    | Some node -> return node
                    | None -> return McpJson.errorNode "MCP SSE 响应中没有可用帧"
                else
                    try return JsonNode.Parse body
                    with ex -> return McpJson.errorNode(sprintf "MCP 响应解析失败：%s" ex.Message)
        }

    member private this.EnsureInitialized(ct: CancellationToken) : Task<Result<unit, string>> =
        task {
            if initialized then return Ok()
            else
                let envelope = JsonObject()
                envelope["jsonrpc"] <- "2.0"
                envelope["id"] <- JsonNode.op_Implicit(Interlocked.Increment(&nextId))
                envelope["method"] <- "initialize"
                envelope["params"] <- McpJson.initializeParams ()
                let! response = this.Post(envelope, 15000, ct)
                match response with
                | :? JsonObject as o ->
                    let mutable errNode: JsonNode = null
                    if o.TryGetPropertyValue("error", &errNode) && not (isNull errNode) then
                        return Error(sprintf "MCP %s initialize 被拒绝：%s" config.id (errNode.ToJsonString()))
                    else
                        let notif = JsonObject()
                        notif["jsonrpc"] <- "2.0"
                        notif["method"] <- "notifications/initialized"
                        let! _ = this.Post(notif, 5000, ct)
                        lock gate (fun () -> initialized <- true)
                        onLog(sprintf "[mcp:%s] connected to %s" config.id endpoint)
                        return Ok()
                | _ -> return Error(sprintf "MCP %s initialize 响应异常" config.id)
        }

    interface IMcpTransport with
        member this.Request(method, paramsObj, timeoutMs, ct) =
            task {
                Interlocked.Increment(&inFlight) |> ignore
                try
                    match! this.EnsureInitialized ct with
                    | Error e -> return McpJson.errorNode e
                    | Ok () ->
                        let envelope = JsonObject()
                        envelope["jsonrpc"] <- "2.0"
                        envelope["id"] <- JsonNode.op_Implicit(Interlocked.Increment(&nextId))
                        envelope["method"] <- method
                        if paramsObj.Count > 0 then envelope["params"] <- paramsObj
                        try
                            return! this.Post(envelope, timeoutMs, ct)
                        with
                        | :? OperationCanceledException when not ct.IsCancellationRequested ->
                            return McpJson.errorNode(sprintf "MCP 调用超时（%d 秒）" (timeoutMs / 1000))
                        | :? OperationCanceledException -> return McpJson.errorNode "调用已取消"
                        | ex -> return McpJson.errorNode(sprintf "MCP 调用失败：%s" ex.Message)
                finally
                    Interlocked.Decrement(&inFlight) |> ignore
            }

        member this.Notify(method, paramsObj) =
            let envelope = JsonObject()
            envelope["jsonrpc"] <- "2.0"
            envelope["method"] <- method
            if paramsObj.Count > 0 then envelope["params"] <- paramsObj
            this.Post(envelope, 5000, CancellationToken.None) |> ignore

        member _.Pending = inFlight

        member _.Stop() =
            try http.Dispose() with _ -> ()
