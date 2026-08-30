namespace Wanxiang.Server

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Wanxiang.Config

/// 一个 MCP 工具的声明（名称 + 描述 + 入参 schema）。
type McpToolInfo = {
    name: string
    description: string
    /// 服务器给出的 inputSchema。转发给模型，模型才能填对参数。
    inputSchema: JsonNode option
}

/// MCP 客户端：一个 MCP 服务器一个实例，按需连接。
///
/// 与旧实现的三处关键差别：
/// 1. 工具声明会被缓存并暴露出去（`Tools()`），因此界面能列出可用工具，
///    模型也能拿到真实的 `inputSchema` 与描述，而不是一个 `args: string`；
/// 2. 每次调用都有超时（`callTimeoutSeconds`），挂死的服务器不再冻结整场生成；
/// 3. 传输可以是本地 stdio 也可以是远程 HTTP。
type McpClient(config: McpServerConfig, onLog: string -> unit) =

    let transport: IMcpTransport =
        match config.url with
        | Some url -> McpHttpTransport(config, url, onLog) :> IMcpTransport
        | None -> McpStdioTransport(config, onLog) :> IMcpTransport

    let semaphore = new SemaphoreSlim(config.maxConcurrency |> Option.defaultValue Int32.MaxValue)
    let mutable activeCount = 0
    let mutable stopped = false
    let toolCache = ConcurrentDictionary<string, McpToolInfo>()
    let mutable toolsLoaded = false
    let cacheGate = obj ()

    let callTimeoutMs = max 1000 (config.callTimeoutSeconds * 1000)

    let resultOf (response: JsonNode) : Result<JsonNode, string> =
        match response with
        | :? JsonObject as o ->
            let mutable result: JsonNode = null
            if o.TryGetPropertyValue("result", &result) && not (isNull result) then Ok result
            else
                let mutable err: JsonNode = null
                if o.TryGetPropertyValue("error", &err) && not (isNull err) then Error(err.ToJsonString())
                else Error "MCP 响应既无 result 也无 error"
        | _ -> Error "MCP 响应不是 JSON 对象"

    member _.Id = config.id

    /// 并发受限的请求入口。`activeCount` 覆盖「等名额 + 执行中」，
    /// 关闭时据此排空，绝不在排空瞬间把排队调用打成失败（决策 100）。
    member private _.Request(method: string, paramsObj: JsonObject, ct: CancellationToken) : Task<JsonNode> =
        task {
            if stopped then return McpJson.errorNode(sprintf "MCP %s 已停止" config.id)
            else
                Interlocked.Increment(&activeCount) |> ignore
                let mutable acquired = false
                try
                    try
                        let! ok = semaphore.WaitAsync(Timeout.Infinite, ct)
                        acquired <- ok
                        if not ok then return McpJson.errorNode "无法获取 MCP 并发名额"
                        else return! transport.Request(method, paramsObj, callTimeoutMs, ct)
                    with
                    | :? OperationCanceledException -> return McpJson.errorNode "调用已取消"
                    | :? ObjectDisposedException -> return McpJson.errorNode(sprintf "MCP %s 已停止" config.id)
                    | ex -> return McpJson.errorNode ex.Message
                finally
                    if acquired then (try semaphore.Release() |> ignore with _ -> ())
                    Interlocked.Decrement(&activeCount) |> ignore
        }

    /// 拉取并缓存工具声明。失败返回空列表并记日志（不阻塞会话）。
    member this.LoadTools(ct: CancellationToken) : Task<McpToolInfo list> =
        task {
            let! response = this.Request("tools/list", JsonObject(), ct)
            match resultOf response with
            | Error e ->
                onLog(sprintf "[mcp:%s] tools/list 失败：%s" config.id e)
                return []
            | Ok result ->
                let infos =
                    match result with
                    | :? JsonObject as ro ->
                        let mutable toolsNode: JsonNode = null
                        if ro.TryGetPropertyValue("tools", &toolsNode)
                           && not (isNull toolsNode)
                           && toolsNode.GetValueKind() = JsonValueKind.Array then
                            [ for t in toolsNode.AsArray() do
                                  match t with
                                  | :? JsonObject as a ->
                                      let str k =
                                          let mutable n: JsonNode = null
                                          if a.TryGetPropertyValue(k, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then
                                              n.GetValue<string>()
                                          else
                                              ""
                                      let name = str "name"
                                      if not (String.IsNullOrWhiteSpace name) then
                                          let schema =
                                              let mutable n: JsonNode = null
                                              if a.TryGetPropertyValue("inputSchema", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Object then
                                                  Some(n.DeepClone())
                                              else
                                                  None
                                          { name = name
                                            description = str "description"
                                            inputSchema = schema }
                                  | _ -> () ]
                        else
                            []
                    | _ -> []
                lock cacheGate (fun () ->
                    toolCache.Clear()
                    for info in infos do toolCache[info.name] <- info
                    toolsLoaded <- true)
                return infos
        }

    /// 已缓存的工具声明；首次访问会同步拉取一次。
    member this.Tools() : McpToolInfo list =
        if not toolsLoaded then
            try
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 20.0)
                this.LoadTools(timeout.Token).GetAwaiter().GetResult() |> ignore
            with ex ->
                onLog(sprintf "[mcp:%s] 工具发现失败：%s" config.id ex.Message)
        toolCache.Values |> List.ofSeq |> List.sortBy (fun t -> t.name)

    member this.TryTool(name: string) : McpToolInfo option =
        match toolCache.TryGetValue name with
        | true, info -> Some info
        | _ -> this.Tools() |> List.tryFind (fun t -> t.name = name)

    /// 调用工具。参数是模型给出的 JSON 对象。
    /// Q170：参数解析失败不调用工具，直接返回失败结果交 Provider 处理。
    member this.Call(toolName: string, argsJson: string, ct: CancellationToken) : Task<string> =
        task {
            let parsed =
                if String.IsNullOrWhiteSpace argsJson then Some(JsonObject() :> JsonNode)
                else
                    try
                        match JsonNode.Parse argsJson with
                        | null -> Some(JsonObject() :> JsonNode)
                        | node -> Some node
                    with _ -> None
            match parsed with
            | None -> return """{"error":"invalid arguments: not valid JSON"}"""
            | Some args ->
                let paramsObj = JsonObject()
                paramsObj["name"] <- toolName
                paramsObj["arguments"] <- args
                let! response = this.Request("tools/call", paramsObj, ct)
                match resultOf response with
                | Ok result -> return result.ToJsonString()
                | Error e -> return sprintf """{"error":%s}""" (JsonSerializer.Serialize e)
        }

    /// 排空关闭（决策 98/100）：不再接新调用，允许在途与排队调用自然完成。
    member _.Stop() =
        stopped <- true
        let deadline = DateTimeOffset.UtcNow.AddSeconds 5.0
        while (transport.Pending > 0 || activeCount > 0) && DateTimeOffset.UtcNow < deadline do
            Thread.Sleep 50
        transport.Stop()
        try semaphore.Dispose() with _ -> ()
