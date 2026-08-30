namespace Wanxiang.Server

open System
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Wanxiang.Config

/// 可选 provider / 模型 / 工具的目录。
///
/// 客户端不该靠用户手打 provider id 和模型名——那是配置文件时代的交互。
/// 这里把服务端已知的一切可选项整理成快照下发，选择器直接渲染。
/// **密钥永不出现在快照里**，只用 `hasApiKey` 表示是否已配置。
module ProviderCatalog =

    let private providerToJson (p: ProviderConfig) : JsonObject =
        let o = JsonObject()
        o["id"] <- p.id
        o["label"] <- ProviderConfig.displayName p
        o["kind"] <- p.kind
        o["baseUrl"] <- p.baseUrl
        o["hasApiKey"] <- (match p.apiKey with Some k -> not (String.IsNullOrWhiteSpace k) | None -> false)
        o["models"] <- JsonArray([| for m in p.models -> JsonNode.op_Implicit m |])
        o["defaultModel"] <- p.defaultModel
        o["timeoutSeconds"] <- p.timeoutSeconds
        o["maxRetries"] <- p.maxRetries
        o["enabled"] <- p.enabled
        if not (Map.isEmpty p.headers) then
            let h = JsonObject()
            for KeyValue(k, v) in p.headers do h[k] <- v
            o["headers"] <- h
        o

    let private generationToJson (g: GenerationDefaults) : JsonObject =
        let o = JsonObject()
        match g.temperature with Some t -> o["temperature"] <- t | None -> ()
        match g.topP with Some t -> o["topP"] <- t | None -> ()
        match g.maxTokens with Some m -> o["maxTokens"] <- m | None -> ()
        match g.instructions with Some s -> o["instructions"] <- s | None -> ()
        o["maxContextMessages"] <- g.maxContextMessages
        o["autoTitle"] <- g.autoTitle
        o["maxToolRounds"] <- g.maxToolRounds
        o

    let private mcpToJson (m: McpServerConfig) : JsonObject =
        let o = JsonObject()
        o["id"] <- m.id
        o["label"] <- McpServerConfig.displayName m
        match m.command with Some c -> o["command"] <- c | None -> ()
        if not (List.isEmpty m.args) then
            o["args"] <- JsonArray([| for a in m.args -> JsonNode.op_Implicit a |])
        if not (Map.isEmpty m.env) then
            let e = JsonObject()
            for KeyValue(k, v) in m.env do e[k] <- v
            o["env"] <- e
        match m.url with Some u -> o["url"] <- u | None -> ()
        match m.maxConcurrency with Some c -> o["maxConcurrency"] <- c | None -> ()
        o["callTimeoutSeconds"] <- m.callTimeoutSeconds
        o["enabled"] <- m.enabled
        o

    /// providers 数组（含未启用者，设置界面需要看到全部）。
    let providers (cfg: AppConfig) : JsonArray =
        JsonArray(
            [| for p in cfg.providers |> Map.toList |> List.map snd |> List.sortBy (fun p -> p.id) ->
                   providerToJson p :> JsonNode |])

    let mcpServers (cfg: AppConfig) : JsonArray =
        JsonArray(
            [| for m in cfg.mcpServers |> Map.toList |> List.map snd |> List.sortBy (fun m -> m.id) ->
                   mcpToJson m :> JsonNode |])

    /// 目录快照的 generation 段：生成默认值 + MCP 服务器清单
    /// （客户端设置界面与会话设置共用同一份数据）。
    let generation (cfg: AppConfig) : JsonObject =
        let o = generationToJson cfg.generation
        o["mcpServers"] <- mcpServers cfg
        let t = JsonObject()
        t["fileReadRoots"] <- JsonArray([| for r in cfg.tools.fileReadRoots -> JsonNode.op_Implicit r |])
        t["callTimeoutSeconds"] <- cfg.tools.callTimeoutSeconds
        o["tools"] <- t
        o

    let private stringField (name: string) (item: JsonNode) =
        match item with
        | :? JsonObject as o ->
            match o[name] with
            | null -> None
            | value ->
                if value.GetValueKind() = JsonValueKind.String then Some(value.GetValue<string>()) else None
        | _ -> None

    let private arrayField (root: JsonObject) (key: string) =
        match root[key] with
        | :? JsonArray as items -> List.ofSeq (Seq.cast<JsonNode> items)
        | _ -> []

    /// 从探活响应里取模型名。
    ///
    /// OpenAI 与 Anthropic 都返回 `{data:[{id}]}`；Gemini 返回
    /// `{models:[{name:"models/gemini-2.5-pro"}]}`，那层 `models/` 前缀要去掉，
    /// 否则填回模型列表后每个模型名都是错的。
    let modelIdsFromProbe (body: string) : Result<string list, string> =
        match JsonNode.Parse body with
        | :? JsonObject as root ->
            let fromData = arrayField root "data" |> List.choose (stringField "id")
            let fromModels =
                arrayField root "models"
                |> List.choose (stringField "name")
                |> List.map (fun name ->
                    if name.StartsWith("models/", StringComparison.Ordinal) then name.Substring 7 else name)
            match fromData @ fromModels with
            | [] -> Error "响应里没有模型清单（既无 data 也无 models）"
            | ids -> Ok(ids |> List.distinct |> List.sort)
        | _ -> Error "响应不是 JSON 对象"

    /// 探活请求的端点与鉴权头。三种传输各不相同，用错就是 401 或 404。
    let private probeRequest (p: ProviderConfig) =
        let trimmed = p.baseUrl.TrimEnd '/'
        let key = p.apiKey |> Option.filter (String.IsNullOrWhiteSpace >> not)
        match p.kind with
        | "anthropic" ->
            let url =
                if trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) then trimmed + "/models"
                else trimmed + "/v1/models"
            url,
            [ match key with
              | Some k -> "x-api-key", k
              | None -> ()
              "anthropic-version", "2023-06-01" ]
        | "gemini" ->
            let root = if trimmed.Contains "/v1" then trimmed else trimmed + "/v1beta"
            root + "/models",
            [ match key with
              | Some k -> "x-goog-api-key", k
              | None -> () ]
        | _ ->
            trimmed + "/models",
            [ match key with
              | Some k -> "Authorization", "Bearer " + k
              | None -> () ]

    /// 向 provider 的模型清单端点探活。
    /// 失败原因原样带回，设置界面能直接告诉用户是密钥错还是网络不通。
    let probeModels (p: ProviderConfig) (ct: CancellationToken) : Task<Result<string list, string>> =
        task {
            try
                let url, authHeaders = probeRequest p
                use request = new HttpRequestMessage(HttpMethod.Get, url)
                for (name, value) in authHeaders do
                    request.Headers.TryAddWithoutValidation(name, value) |> ignore
                for KeyValue(name, value) in p.headers do
                    request.Headers.TryAddWithoutValidation(name, value) |> ignore
                use client = new HttpClient()
                client.Timeout <- TimeSpan.FromSeconds(float (min p.timeoutSeconds 30))
                let! response = client.SendAsync(request, ct)
                let! body = response.Content.ReadAsStringAsync ct
                if not response.IsSuccessStatusCode then
                    let hint =
                        match int response.StatusCode with
                        | 401 | 403 -> "凭据被拒绝，请检查 API Key"
                        | 404 ->
                            match p.kind with
                            | "anthropic" -> "端点不存在，anthropic 传输的 baseUrl 填到域名即可"
                            | "gemini" -> "端点不存在，gemini 传输的 baseUrl 填到域名即可"
                            | _ -> "端点不存在，请检查 baseUrl 是否以 /v1 结尾"
                        | s when s >= 500 -> "服务商内部错误"
                        | _ -> "请求被拒绝"
                    return Error(sprintf "HTTP %d · %s" (int response.StatusCode) hint)
                else
                    return modelIdsFromProbe body
            with
            | :? OperationCanceledException -> return Error "探测已取消"
            | ex -> return Error ex.Message
        }
