namespace Wanxiang.Server

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Wanxiang.Config
open Wanxiang.Core

/// 把一个 MCP 工具包装成模型可调用的 `AIFunction`，并**原样转发**
/// 服务器给出的 `inputSchema`。
///
/// 旧实现把每个 MCP 工具都注册成 `Func<string, ...>`，模型只看到一个
/// 叫 `args` 的字符串参数、以及一句「MCP tool x/y」的假描述——
/// 结果是模型几乎不可能正确调用任何 MCP 工具。
type McpFunction(client: McpClient, serverId: string, info: McpToolInfo) =
    inherit AIFunction()

    /// 无 inputSchema 时给一个接受任意对象的兜底 schema。
    static let permissiveSchema =
        JsonDocument.Parse("""{"type":"object","additionalProperties":true}""").RootElement.Clone()

    let schema =
        match info.inputSchema with
        | Some node ->
            try JsonDocument.Parse(node.ToJsonString()).RootElement.Clone()
            with _ -> permissiveSchema
        | None -> permissiveSchema

    let functionName = sprintf "mcp_%s_%s" serverId info.name

    override _.Name = functionName

    override _.Description =
        if String.IsNullOrWhiteSpace info.description then
            sprintf "%s 提供的工具 %s。" serverId info.name
        else
            info.description

    override _.JsonSchema = schema

    override _.InvokeCoreAsync(args: AIFunctionArguments, ct: CancellationToken) : ValueTask<obj> =
        ValueTask<obj>(
            task {
                // 模型给的参数是键值对，MCP 需要一个 JSON 对象
                let payload = JsonObject()
                for KeyValue(key, value) in args do
                    payload[key] <-
                        match value with
                        | null -> null
                        | :? JsonNode as node -> node.DeepClone()
                        | :? JsonElement as element -> JsonNode.Parse(element.GetRawText())
                        | other ->
                            try JsonSerializer.SerializeToNode(other, AIJsonUtilities.DefaultOptions)
                            with _ -> JsonNode.op_Implicit(string other)
                let! result = client.Call(info.name, payload.ToJsonString(), ct)
                return box result
            })

/// 一个工具在界面上的样子。
type ToolDescriptor = {
    /// 稳定标识（会话配置里存的就是它）
    id: string
    /// 模型看到的函数名
    functionName: string
    label: string
    description: string
    /// "builtin" | "mcp"
    source: string
    /// MCP 工具所属服务器 id
    serverId: string option
}

/// 工具注册表：内置工具 + MCP 工具（决策 95-100）。
/// 会话配置只保存稳定标识（`builtin:...` / `mcp:<server-id>/<tool-name>`）。
type ToolRegistry(getConfig: unit -> AppConfig, onLog: string -> unit) =

    let mcpClients = ConcurrentDictionary<string, McpClient>()
    /// 已缓存客户端对应的配置指纹（配置热更新后旧客户端排空关闭，新建客户端）。
    let mcpFingerprints = ConcurrentDictionary<string, string>()
    let registryLock = obj ()

    let fingerprint (cfg: McpServerConfig) : string =
        sprintf "%s|%A|%A|%s|%A|%d"
            (cfg.command |> Option.defaultValue "")
            cfg.args
            cfg.env
            (cfg.url |> Option.defaultValue "")
            cfg.maxConcurrency
            cfg.callTimeoutSeconds

    let mcpServers () = (getConfig ()).mcpServers

    /// 解析 `mcp:<server>/<tool>` 形式的稳定标识。
    let parseMcpId (id: string) : (string * string) option =
        if id.StartsWith("mcp:", StringComparison.Ordinal) then
            let rest = id.Substring 4
            match rest.IndexOf '/' with
            | -1 -> None
            | i -> Some(rest.Substring(0, i), rest.Substring(i + 1))
        else
            None

    member _.GetMcpClient(id: string) : McpClient option =
        lock registryLock (fun () ->
            let dropCached () =
                match mcpClients.TryRemove id with
                | true, old -> old.Stop()
                | _ -> ()
            match mcpServers () |> Map.tryFind id with
            | None ->
                // 配置已删除：排空关闭旧客户端（决策 98）
                dropCached ()
                None
            | Some cfg when not cfg.enabled ->
                dropCached ()
                None
            | Some cfg ->
                let fp = fingerprint cfg
                let create () =
                    let client = McpClient(cfg, onLog)
                    mcpClients[id] <- client
                    mcpFingerprints[id] <- fp
                    Some client
                match mcpClients.TryGetValue id with
                | true, client ->
                    match mcpFingerprints.TryGetValue id with
                    | true, cached when cached = fp -> Some client
                    | _ ->
                        // 配置变化：旧客户端排空关闭，新建（决策 98：新调用立即使用新配置）
                        match mcpClients.TryRemove id with
                        | true, old -> old.Stop()
                        | _ -> ()
                        create ()
                | _ -> create ())

    /// 已缓存客户端里对应配置消失的那些：排空关闭，避免孤儿子进程。
    member _.PruneRemoved() : unit =
        lock registryLock (fun () ->
            let live = mcpServers ()
            for kv in mcpClients do
                match live |> Map.tryFind kv.Key with
                | Some cfg when cfg.enabled -> ()
                | _ ->
                    match mcpClients.TryRemove kv.Key with
                    | true, old -> old.Stop()
                    | _ -> ())

    /// 全部可选工具（内置 + 已连上的 MCP 服务器）。客户端据此渲染工具清单。
    member this.Descriptors() : ToolDescriptor list =
        let cfg = getConfig ()
        let builtins =
            BuiltinTools.descriptors cfg.tools
            |> List.map (fun (id, fn, desc) ->
                { id = id
                  functionName = fn
                  label = id.Substring("builtin:".Length)
                  description = desc
                  source = "builtin"
                  serverId = None })
        let mcp =
            cfg.mcpServers
            |> Map.toList
            |> List.map snd
            |> List.filter (fun s -> s.enabled)
            |> List.sortBy (fun s -> s.id)
            |> List.collect (fun server ->
                match this.GetMcpClient server.id with
                | None -> []
                | Some client ->
                    client.Tools()
                    |> List.map (fun info ->
                        { id = sprintf "mcp:%s/%s" server.id info.name
                          functionName = sprintf "mcp_%s_%s" server.id info.name
                          label = info.name
                          description = info.description
                          source = "mcp"
                          serverId = Some server.id }))
        builtins @ mcp

    /// 目录快照用的 JSON 形态。
    member this.DescriptorsJson() : JsonArray =
        let arr = JsonArray()
        for d in this.Descriptors() do
            let o = JsonObject()
            o["id"] <- d.id
            o["functionName"] <- d.functionName
            o["label"] <- d.label
            o["description"] <- d.description
            o["source"] <- d.source
            match d.serverId with Some s -> o["serverId"] <- s | None -> ()
            arr.Add o
        arr

    /// 构建会话启用的 AITool 列表。
    member this.BuildTools(config: SessionConfig) : AITool list =
        let appCfg = getConfig ()
        let requested = config.tools |> Set.ofList
        let builtins =
            BuiltinTools.all appCfg.tools
            |> List.filter (fun t -> requested.Contains(BuiltinTools.stableId t.Name))
        let mcpTools =
            requested
            |> Seq.choose parseMcpId
            |> Seq.distinct
            |> Seq.choose (fun (serverId, toolName) ->
                match this.GetMcpClient serverId with
                | None -> None
                | Some client ->
                    match client.TryTool toolName with
                    | Some info -> Some(McpFunction(client, serverId, info) :> AITool)
                    | None ->
                        onLog(sprintf "[mcp:%s] 会话请求的工具 %s 不存在，已跳过" serverId toolName)
                        None)
            |> List.ofSeq
        builtins @ mcpTools

    /// 会话请求了但当前不可用的工具（供 UI 提示配置漂移）。
    member this.MissingTools(config: SessionConfig) : string list =
        let available = this.Descriptors() |> List.map (fun d -> d.id) |> Set.ofList
        config.tools |> List.filter (fun t -> not (available.Contains t))

    /// 关闭全部 MCP 客户端（排空后关闭，决策 98）。
    member _.Dispose() =
        for kv in mcpClients do
            kv.Value.Stop()
