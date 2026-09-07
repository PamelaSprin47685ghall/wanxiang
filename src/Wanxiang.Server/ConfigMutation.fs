namespace Wanxiang.Server

open System
open System.Text.Json
open System.Text.Json.Nodes
open Wanxiang.Config

/// 把客户端发来的配置片段合并进 `AppConfig`。
///
/// TOML 仍是唯一权威：这里只负责「JSON 片段 → 强类型配置」的解析与校验，
/// 真正落盘由 `ConfigStore.Rewrite` 原子完成。
/// 设置界面因此不需要用户手改文件，也不会因为半个字段写错就毁掉整份配置。
module ConfigMutation =

    let private str (o: JsonObject) (key: string) : string option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then
            let s = n.GetValue<string>()
            if String.IsNullOrWhiteSpace s then None else Some(s.Trim())
        else
            None

    let private intOf (o: JsonObject) (key: string) : int option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
            match n with
            | :? JsonValue as v ->
                match v.TryGetValue<int>() with
                | true, i -> Some i
                | _ -> None
            | _ -> None
        else
            None

    let private floatOf (o: JsonObject) (key: string) : float option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
            match n with
            | :? JsonValue as v ->
                match v.TryGetValue<float>() with
                | true, f -> Some f
                | _ -> None
            | _ -> None
        else
            None

    let private boolOf (o: JsonObject) (key: string) (fallback: bool) : bool =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) then
            match n.GetValueKind() with
            | JsonValueKind.True -> true
            | JsonValueKind.False -> false
            | _ -> fallback
        else
            fallback

    let private stringList (o: JsonObject) (key: string) : string list =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Array then
            [ for item in n.AsArray() do
                  if not (isNull item) && item.GetValueKind() = JsonValueKind.String then
                      let s = item.GetValue<string>()
                      if not (String.IsNullOrWhiteSpace s) then s.Trim() ]
            |> List.distinct
        else
            []

    let private stringMap (o: JsonObject) (key: string) : Map<string, string> =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Object then
            n.AsObject()
            |> Seq.choose (fun kv ->
                if isNull kv.Value then None
                elif kv.Value.GetValueKind() = JsonValueKind.String then Some(kv.Key, kv.Value.GetValue<string>())
                else None)
            |> Map.ofSeq
        else
            Map.empty

    /// 稳定标识约束：出现在 TOML 表名与协议里，必须是保守字符集。
    let private validId (id: string) : bool =
        not (String.IsNullOrWhiteSpace id)
        && id.Length <= 48
        && id |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' || c = '.')

    /// 新增或更新一个 provider。`apiKey` 缺省表示「保持原值」，
    /// 这样设置界面可以在不回显密钥的前提下修改其他字段。
    let upsertProvider (cfg: AppConfig) (payload: JsonObject) : Result<AppConfig, string list> =
        match str payload "id" with
        | None -> Error [ "provider.id: 必填" ]
        | Some id when not (validId id) -> Error [ "provider.id: 只允许字母、数字、- _ . 且不超过 48 字符" ]
        | Some id ->
            let existing = cfg.providers |> Map.tryFind id
            let models = stringList payload "models"
            let defaultModel =
                match str payload "defaultModel" with
                | Some m -> m
                | None -> models |> List.tryHead |> Option.defaultValue ""
            let apiKey =
                match str payload "apiKey" with
                | Some k -> Some k
                | None -> existing |> Option.bind (fun p -> p.apiKey)
            let provider =
                { id = id
                  kind = str payload "kind" |> Option.defaultValue "openai"
                  label = str payload "label" |> Option.defaultValue ""
                  baseUrl = str payload "baseUrl" |> Option.defaultValue ""
                  apiKey = apiKey
                  models = models
                  defaultModel = defaultModel
                  timeoutSeconds =
                    intOf payload "timeoutSeconds"
                    |> Option.orElse (existing |> Option.map (fun p -> p.timeoutSeconds))
                    |> Option.defaultValue ProviderConfig.defaultTimeoutSeconds
                  maxRetries =
                    intOf payload "maxRetries"
                    |> Option.orElse (existing |> Option.map (fun p -> p.maxRetries))
                    |> Option.defaultValue ProviderConfig.defaultMaxRetries
                  enabled = boolOf payload "enabled" (existing |> Option.map (fun p -> p.enabled) |> Option.defaultValue true)
                  headers =
                    (let h = stringMap payload "headers"
                     if Map.isEmpty h then existing |> Option.map (fun p -> p.headers) |> Option.defaultValue Map.empty else h)
                  promptCaching =
                    boolOf payload "promptCaching" (existing |> Option.map (fun p -> p.promptCaching) |> Option.defaultValue false)
                  extraJson = existing |> Option.bind (fun p -> p.extraJson) }
            let errors =
                [ if not (TomlCodec.supportedProviderKinds.Contains provider.kind) then
                      sprintf "provider.kind: 暂不支持 %s" provider.kind
                  if String.IsNullOrWhiteSpace provider.baseUrl then "provider.baseUrl: 必填"
                  elif not (Uri.IsWellFormedUriString(provider.baseUrl, UriKind.Absolute)) then
                      "provider.baseUrl: 需为完整 URL，例如 https://api.openai.com/v1"
                  if List.isEmpty provider.models then "provider.models: 至少填一个模型"
                  elif not (ProviderConfig.hasModel provider.defaultModel provider) then
                      "provider.defaultModel: 必须是 models 中的一项"
                  if provider.timeoutSeconds <= 0 then "provider.timeoutSeconds: 需为正整数"
                  if provider.maxRetries < 0 then "provider.maxRetries: 不能为负"
                  if cfg.mcpServers.ContainsKey id then sprintf "provider.id: 与 MCP 服务器 %s 冲突" id ]
            if List.isEmpty errors then
                Ok { cfg with providers = cfg.providers.Add(id, provider) }
            else
                Error errors

    let deleteProvider (cfg: AppConfig) (id: string) : Result<AppConfig, string list> =
        if cfg.providers.ContainsKey id then Ok { cfg with providers = cfg.providers.Remove id }
        else Error [ sprintf "provider %s 不存在" id ]

    let upsertMcp (cfg: AppConfig) (payload: JsonObject) : Result<AppConfig, string list> =
        match str payload "id" with
        | None -> Error [ "mcp.id: 必填" ]
        | Some id when not (validId id) -> Error [ "mcp.id: 只允许字母、数字、- _ . 且不超过 48 字符" ]
        | Some id ->
            let existing = cfg.mcpServers |> Map.tryFind id
            let server =
                { id = id
                  label = str payload "label" |> Option.defaultValue ""
                  command = str payload "command"
                  args = stringList payload "args"
                  env =
                    (let e = stringMap payload "env"
                     if Map.isEmpty e then existing |> Option.map (fun m -> m.env) |> Option.defaultValue Map.empty else e)
                  url = str payload "url"
                  maxConcurrency = intOf payload "maxConcurrency"
                  callTimeoutSeconds =
                    intOf payload "callTimeoutSeconds"
                    |> Option.orElse (existing |> Option.map (fun m -> m.callTimeoutSeconds))
                    |> Option.defaultValue McpServerConfig.defaultCallTimeoutSeconds
                  enabled = boolOf payload "enabled" (existing |> Option.map (fun m -> m.enabled) |> Option.defaultValue true) }
            let errors =
                [ match server.command, server.url with
                  | None, None -> "mcp: command 与 url 至少填一个"
                  | Some _, Some _ -> "mcp: command 与 url 只能填一个"
                  | _ -> ()
                  match server.url with
                  | Some u when not (Uri.IsWellFormedUriString(u, UriKind.Absolute)) -> "mcp.url: 需为完整 URL"
                  | _ -> ()
                  if server.callTimeoutSeconds <= 0 then "mcp.callTimeoutSeconds: 需为正整数"
                  match server.maxConcurrency with
                  | Some c when c <= 0 -> "mcp.maxConcurrency: 需为正整数"
                  | _ -> ()
                  if cfg.providers.ContainsKey id then sprintf "mcp.id: 与服务商 %s 冲突" id ]
            if List.isEmpty errors then
                Ok { cfg with mcpServers = cfg.mcpServers.Add(id, server) }
            else
                Error errors

    let deleteMcp (cfg: AppConfig) (id: string) : Result<AppConfig, string list> =
        if cfg.mcpServers.ContainsKey id then Ok { cfg with mcpServers = cfg.mcpServers.Remove id }
        else Error [ sprintf "MCP 服务器 %s 不存在" id ]

    /// 更新生成默认值与工具配置。缺省字段保持原值。
    let updateGeneration (cfg: AppConfig) (payload: JsonObject) : Result<AppConfig, string list> =
        let current = cfg.generation
        let hasKey (k: string) =
            let mutable n: JsonNode = null
            payload.TryGetPropertyValue(k, &n)
        let optFloat k currentValue = if hasKey k then floatOf payload k else currentValue
        let optInt k currentValue = if hasKey k then intOf payload k else currentValue
        let generation =
            { temperature = optFloat "temperature" current.temperature
              topP = optFloat "topP" current.topP
              maxTokens = optInt "maxTokens" current.maxTokens
              instructions = if hasKey "instructions" then str payload "instructions" else current.instructions
              maxContextMessages = intOf payload "maxContextMessages" |> Option.defaultValue current.maxContextMessages
              maxContextTokens = intOf payload "maxContextTokens" |> Option.defaultValue current.maxContextTokens
              autoTitle = boolOf payload "autoTitle" current.autoTitle
              maxToolRounds = intOf payload "maxToolRounds" |> Option.defaultValue current.maxToolRounds
              thinkingBudget = intOf payload "thinkingBudget" |> Option.defaultValue current.thinkingBudget }
        let toolsCfg =
            let mutable n: JsonNode = null
            if payload.TryGetPropertyValue("tools", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Object then
                let t = n.AsObject()
                { fileReadRoots = stringList t "fileReadRoots"
                  callTimeoutSeconds = intOf t "callTimeoutSeconds" |> Option.defaultValue cfg.tools.callTimeoutSeconds }
            else
                cfg.tools
        let errors =
            [ match generation.temperature with
              | Some t when t < 0.0 || t > 2.0 -> "generation.temperature: 需在 0–2 之间"
              | _ -> ()
              match generation.topP with
              | Some p when p <= 0.0 || p > 1.0 -> "generation.topP: 需在 0–1 之间"
              | _ -> ()
              match generation.maxTokens with
              | Some m when m <= 0 -> "generation.maxTokens: 需为正整数"
              | _ -> ()
              if generation.maxContextMessages < 0 then "generation.maxContextMessages: 不能为负"
              if generation.maxContextTokens < 0 then "generation.maxContextTokens: 不能为负"
              if generation.maxToolRounds <= 0 then "generation.maxToolRounds: 需为正整数"
              if toolsCfg.callTimeoutSeconds <= 0 then "tools.callTimeoutSeconds: 需为正整数"
              for root in toolsCfg.fileReadRoots do
                  if not (IO.Path.IsPathRooted root) then sprintf "tools.fileReadRoots: %s 需为绝对路径" root ]
        if List.isEmpty errors then Ok { cfg with generation = generation; tools = toolsCfg }
        else Error errors
