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
                  extraJson = existing |> Option.bind (fun p -> p.extraJson)
                  visionOverrides =
                    // 缺省表示「保持原值」；给了就整体替换（设置界面总是整份提交）。
                    // 只保留仍在模型列表里的模型，删掉的模型不留孤儿指定。
                    (let hasKey =
                        let mutable n: JsonNode = null
                        payload.TryGetPropertyValue("visionOverrides", &n) && not (isNull n)
                     let raw =
                        if hasKey then
                            match payload["visionOverrides"] with
                            | :? JsonObject as o ->
                                [ for kv in o do
                                      if not (isNull kv.Value) && kv.Value.GetValueKind() = JsonValueKind.True then yield kv.Key, true
                                      elif not (isNull kv.Value) && kv.Value.GetValueKind() = JsonValueKind.False then yield kv.Key, false ]
                                |> Map.ofList
                            | _ -> Map.empty
                        else existing |> Option.map (fun p -> p.visionOverrides) |> Option.defaultValue Map.empty
                     raw |> Map.filter (fun m _ -> List.contains m models)) }
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
                  if cfg.mcpServers.ContainsKey id then sprintf "provider.id: 与 MCP 服务 %s 冲突" id ]
            if List.isEmpty errors then
                Ok { cfg with providers = cfg.providers.Add(id, provider) }
            else
                Error errors

    let deleteProvider (cfg: AppConfig) (id: string) : Result<AppConfig, string list> =
        if cfg.providers.ContainsKey id then
            // 全局默认指向被删的模型服务时一并清掉：留着只会写出悬空引用。
            let generation =
                if cfg.generation.defaultProvider = Some id then
                    { cfg.generation with defaultProvider = None; defaultModel = None }
                else
                    cfg.generation
            Ok { cfg with providers = cfg.providers.Remove id; generation = generation }
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
                  if cfg.providers.ContainsKey id then sprintf "mcp.id: 与模型服务 %s 冲突" id ]
            if List.isEmpty errors then
                Ok { cfg with mcpServers = cfg.mcpServers.Add(id, server) }
            else
                Error errors

    /// 决策 431/432：新建或覆盖一个对话预设。名称必填、去重（不区分大小写，同 id 覆盖除外）；
    /// 模型服务与工具只做「存在即可」的弱校验——预设可能早于模型服务被删，套用时再回落。
    let upsertPreset (cfg: AppConfig) (payload: JsonObject) : Result<AppConfig, string list> =
        let id =
            str payload "id"
            |> Option.defaultWith (fun () -> "p" + Guid.CreateVersion7().ToString("N").Substring(20))
        let name = str payload "name" |> Option.defaultValue ""
        let preset : ConversationPreset =
            { id = id
              name = name
              provider = str payload "provider" |> Option.defaultValue ""
              model = str payload "model" |> Option.defaultValue ""
              instructions = str payload "instructions"
              tools = stringList payload "tools" |> List.distinct
              temperature = floatOf payload "temperature"
              topP = floatOf payload "topP"
              maxTokens = intOf payload "maxTokens"
              thinkingBudget = intOf payload "thinkingBudget" }
        let errors =
            [ if String.IsNullOrWhiteSpace name then "预设名称不能为空"
              elif name.Length > ConversationPreset.maxNameLength then
                  sprintf "预设名称最多 %d 个字符" ConversationPreset.maxNameLength
              if cfg.presets |> Map.exists (fun k p -> k <> id && String.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)) then
                  sprintf "已经有一个叫「%s」的预设" name
              if not (cfg.presets.ContainsKey id) && cfg.presets.Count >= ConversationPreset.maxCount then
                  sprintf "预设最多 %d 个" ConversationPreset.maxCount
              if id |> Seq.exists (fun c -> not (Char.IsLetterOrDigit c || c = '-' || c = '_')) then "预设 id 只能含字母、数字、- 与 _"
              match preset.temperature with
              | Some t when t < 0.0 || t > 2.0 -> "temperature: 需在 0–2 之间"
              | _ -> ()
              match preset.topP with
              | Some p when p <= 0.0 || p > 1.0 -> "topP: 需在 0–1 之间"
              | _ -> ()
              match preset.maxTokens with
              | Some m when m <= 0 -> "maxTokens: 需为正整数"
              | _ -> ()
              match preset.thinkingBudget with
              | Some b when b < 0 -> "thinkingBudget: 不能为负"
              | _ -> () ]
        if List.isEmpty errors then Ok { cfg with presets = cfg.presets.Add(id, preset) }
        else Error errors

    let deletePreset (cfg: AppConfig) (id: string) : Result<AppConfig, string list> =
        if cfg.presets.ContainsKey id then Ok { cfg with presets = cfg.presets.Remove id }
        else Error [ "这个预设已经不存在" ]

    let deleteMcp (cfg: AppConfig) (id: string) : Result<AppConfig, string list> =
        if cfg.mcpServers.ContainsKey id then Ok { cfg with mcpServers = cfg.mcpServers.Remove id }
        else Error [ sprintf "MCP 服务 %s 不存在" id ]

    /// 更新工具设置（第 70/71 节）：授权文件夹、每个工具的审批策略、审批超时、调试工具开关。
    /// 缺省字段保持原值；`policy` 给出时整体替换（`default` 是兜底，其余键是工具稳定标识）。
    let updateTools (cfg: AppConfig) (payload: JsonObject) : Result<AppConfig, string list> =
        let hasKey (k: string) =
            let mutable n: JsonNode = null
            payload.TryGetPropertyValue(k, &n)
        let errors = ResizeArray<string>()
        let current = cfg.tools
        let policyDefault, policy =
            let mutable n: JsonNode = null
            if payload.TryGetPropertyValue("policy", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Object then
                let mutable dflt = Auto
                let mutable map = Map.empty
                for kv in n.AsObject() do
                    let v = if isNull kv.Value || kv.Value.GetValueKind() <> JsonValueKind.String then "" else kv.Value.GetValue<string>()
                    match ToolPolicy.tryParse v with
                    | Some p -> if kv.Key = "default" then dflt <- p else map <- Map.add kv.Key p map
                    | None -> errors.Add(sprintf "tools.policy.%s: 只允许 auto、first、always" kv.Key)
                dflt, map
            else current.policyDefault, current.policy
        let tools =
            { current with
                fileReadRoots = if hasKey "fileReadRoots" then stringList payload "fileReadRoots" else current.fileReadRoots
                policyDefault = policyDefault
                policy = policy
                approvalTimeoutSeconds = intOf payload "approvalTimeoutSeconds" |> Option.defaultValue current.approvalTimeoutSeconds
                exposeDebug = boolOf payload "exposeDebug" current.exposeDebug }
        if tools.approvalTimeoutSeconds <= 0 then errors.Add "tools.approvalTimeoutSeconds: 需为正整数"
        for root in tools.fileReadRoots do
            if not (IO.Path.IsPathRooted root) then errors.Add(sprintf "tools.fileReadRoots: %s 需为绝对路径" root)
        if errors.Count = 0 then Ok { cfg with tools = tools } else Error(List.ofSeq errors)

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
              thinkingBudget = intOf payload "thinkingBudget" |> Option.defaultValue current.thinkingBudget
              // 全局默认模型：键存在即覆盖，空串 = 清除（回落为首个可用模型服务的 defaultModel）。
              defaultProvider = if hasKey "defaultProvider" then str payload "defaultProvider" else current.defaultProvider
              defaultModel = if hasKey "defaultModel" then str payload "defaultModel" else current.defaultModel }
        let mutable toolsErrors: string list = []
        let toolsCfg =
            let mutable n: JsonNode = null
            if payload.TryGetPropertyValue("tools", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Object then
                let t = n.AsObject()
                let basic =
                    { cfg.tools with
                        fileReadRoots = stringList t "fileReadRoots"
                        callTimeoutSeconds = intOf t "callTimeoutSeconds" |> Option.defaultValue cfg.tools.callTimeoutSeconds }
                // 审批策略等新键沿用同一个入口：缺省保持原值，旧客户端只发两个老键时不受影响
                match updateTools { cfg with tools = basic } t with
                | Ok merged -> merged.tools
                | Error es ->
                    toolsErrors <- es
                    basic
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
              match generation.defaultProvider, generation.defaultModel with
              | None, Some _ -> "generation.defaultModel: 需要同时指定 defaultProvider"
              | Some pid, model ->
                  match cfg.providers |> Map.tryFind pid with
                  | None -> sprintf "generation.defaultProvider: 模型服务 %s 不存在" pid
                  | Some p ->
                      match model with
                      | Some m when not (ProviderConfig.hasModel m p) ->
                          sprintf "generation.defaultModel: %s 不在 %s 的模型列表中" m pid
                      | _ -> ()
              | None, None -> ()
              if toolsCfg.callTimeoutSeconds <= 0 then "tools.callTimeoutSeconds: 需为正整数"
              for root in toolsCfg.fileReadRoots do
                  if not (IO.Path.IsPathRooted root) then sprintf "tools.fileReadRoots: %s 需为绝对路径" root
              yield! toolsErrors ]
        if List.isEmpty errors then Ok { cfg with generation = generation; tools = toolsCfg }
        else Error errors
