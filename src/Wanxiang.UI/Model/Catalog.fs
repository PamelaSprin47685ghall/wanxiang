namespace Wanxiang.UI

open System
open System.Text.Json
open System.Text.Json.Nodes

/// 主机下发的一个模型服务（`catalog.snapshot`）。密钥永不下发，只有 `hasApiKey`。
type ProviderInfo = {
    id: string
    label: string
    kind: string
    baseUrl: string
    hasApiKey: bool
    models: string list
    defaultModel: string
    timeoutSeconds: int
    maxRetries: int
    enabled: bool
    /// 决策 331：用户手动指定的「能否看图」（模型 → 能/不能）
    visionOverrides: Map<string, bool>
    /// 决策 330：模型服务提供的「能否看图」信息（主机探测或已知清单）
    visionDetected: Map<string, bool>
}

/// 一个可用工具。
type ToolInfo = {
    id: string
    label: string
    description: string
    /// "builtin" | "mcp"
    source: string
    serverId: string option
}

/// 一个 MCP 服务配置。
type McpInfo = {
    id: string
    label: string
    command: string option
    args: string list
    url: string option
    callTimeoutSeconds: int
    enabled: bool
}

/// 生成默认值。
type GenerationInfo = {
    temperature: float option
    topP: float option
    maxTokens: int option
    instructions: string option
    maxContextMessages: int
    autoTitle: bool
    maxToolRounds: int
    fileReadRoots: string list
    /// 全局默认模型服务（`[generation] defaultProvider`，可选）
    defaultProvider: string option
    /// 全局默认模型（`[generation] defaultModel`，可选）
    defaultModel: string option
    /// 未单独配置的工具所用审批策略："auto" | "first" | "always"
    toolPolicyDefault: string
    /// 单个工具的审批策略（键是稳定标识）
    toolPolicy: Map<string, string>
    approvalTimeoutSeconds: int
    exposeDebug: bool
}

/// 对话预设（决策 431）：主机配置里的可复用起点。provider/model 为空表示跟随默认。
type PresetInfo = {
    id: string
    name: string
    provider: string
    model: string
    instructions: string option
    tools: string list
    temperature: float option
    topP: float option
    maxTokens: int option
    thinkingBudget: int option
}

/// 主机能力目录。客户端所有选择器（模型、工具）都从这里取数据，
/// 用户永远不需要手打模型服务 id 或模型名。
type Catalog = {
    providers: ProviderInfo list
    tools: ToolInfo list
    mcpServers: McpInfo list
    generation: GenerationInfo
    /// 决策 432：随目录快照下发；旧主机没有这一项 = 空列表。
    presets: PresetInfo list
}

module Catalog =

    let empty =
        { providers = []
          tools = []
          mcpServers = []
          presets = []
          generation =
            { temperature = None
              topP = None
              maxTokens = None
              instructions = None
              maxContextMessages = 200
              autoTitle = true
              maxToolRounds = 12
              fileReadRoots = []
              defaultProvider = None
              defaultModel = None
              toolPolicyDefault = "auto"
              toolPolicy = Map.empty
              approvalTimeoutSeconds = 300
              exposeDebug = false } }

    let private str (o: JsonObject) key fallback =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then
            n.GetValue<string>()
        else
            fallback

    let private strOpt (o: JsonObject) key =
        let value = str o key ""
        if String.IsNullOrWhiteSpace value then None else Some value

    let private intOf (o: JsonObject) key fallback =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
            n.GetValue<int>()
        else
            fallback

    let private intOpt (o: JsonObject) key =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
            Some(n.GetValue<int>())
        else
            None

    let private floatOpt (o: JsonObject) key =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
            Some(n.GetValue<double>())
        else
            None

    let private boolOf (o: JsonObject) key fallback =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) then
            match n.GetValueKind() with
            | JsonValueKind.True -> true
            | JsonValueKind.False -> false
            | _ -> fallback
        else
            fallback

    let private strings (o: JsonObject) key =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Array then
            [ for item in n.AsArray() do
                  if not (isNull item) && item.GetValueKind() = JsonValueKind.String then item.GetValue<string>() ]
        else
            []

    let private boolMap (o: JsonObject) (key: string) : Map<string, bool> =
        match o[key] with
        | :? JsonObject as m ->
            [ for kv in m do
                  if not (isNull kv.Value) then
                      match kv.Value.GetValueKind() with
                      | JsonValueKind.True -> yield kv.Key, true
                      | JsonValueKind.False -> yield kv.Key, false
                      | _ -> () ]
            |> Map.ofList
        | _ -> Map.empty

    let private providerOf (o: JsonObject) : ProviderInfo =
        { id = str o "id" ""
          label = (let l = str o "label" "" in if String.IsNullOrWhiteSpace l then str o "id" "" else l)
          kind = str o "kind" "openai"
          baseUrl = str o "baseUrl" ""
          hasApiKey = boolOf o "hasApiKey" false
          models = strings o "models"
          defaultModel = str o "defaultModel" ""
          timeoutSeconds = intOf o "timeoutSeconds" 120
          maxRetries = intOf o "maxRetries" 2
          enabled = boolOf o "enabled" true
          visionOverrides = boolMap o "visionOverrides"
          visionDetected = boolMap o "visionDetected" }

    let private toolOf (o: JsonObject) : ToolInfo =
        { id = str o "id" ""
          label = (let l = str o "label" "" in if String.IsNullOrWhiteSpace l then str o "id" "" else l)
          description = str o "description" ""
          source = str o "source" "builtin"
          serverId = strOpt o "serverId" }

    let private mcpOf (o: JsonObject) : McpInfo =
        { id = str o "id" ""
          label = (let l = str o "label" "" in if String.IsNullOrWhiteSpace l then str o "id" "" else l)
          command = strOpt o "command"
          args = strings o "args"
          url = strOpt o "url"
          callTimeoutSeconds = intOf o "callTimeoutSeconds" 60
          enabled = boolOf o "enabled" true }

    let private objects (array: JsonArray) =
        [ for item in array do
              match item with
              | :? JsonObject as o -> o
              | _ -> () ]

    let parse (providers: JsonArray) (tools: JsonArray) (generation: JsonObject) : Catalog =
        let mcpArray =
            let mutable n: JsonNode = null
            if generation.TryGetPropertyValue("mcpServers", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Array then
                n.AsArray()
            else
                JsonArray()
        let toolsConfig =
            let mutable n: JsonNode = null
            if generation.TryGetPropertyValue("tools", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Object then
                n.AsObject()
            else
                JsonObject()
        { providers = objects providers |> List.map providerOf |> List.filter (fun p -> p.id <> "")
          tools = objects tools |> List.map toolOf |> List.filter (fun t -> t.id <> "")
          mcpServers = objects mcpArray |> List.map mcpOf |> List.filter (fun m -> m.id <> "")
          presets =
            (let mutable n: JsonNode = null
             if generation.TryGetPropertyValue("presets", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Array then
                 objects (n.AsArray())
                 |> List.map (fun o ->
                     { id = str o "id" ""
                       name = str o "name" ""
                       provider = str o "provider" ""
                       model = str o "model" ""
                       instructions = strOpt o "instructions"
                       tools = strings o "tools"
                       temperature = floatOpt o "temperature"
                       topP = floatOpt o "topP"
                       maxTokens = intOpt o "maxTokens"
                       thinkingBudget = intOpt o "thinkingBudget" })
                 |> List.filter (fun p -> p.id <> "" && p.name <> "")
             else [])
          generation =
            { temperature = floatOpt generation "temperature"
              topP = floatOpt generation "topP"
              maxTokens = intOpt generation "maxTokens"
              instructions = strOpt generation "instructions"
              maxContextMessages = intOf generation "maxContextMessages" 200
              autoTitle = boolOf generation "autoTitle" true
              maxToolRounds = intOf generation "maxToolRounds" 12
              fileReadRoots = strings toolsConfig "fileReadRoots"
              defaultProvider = strOpt generation "defaultProvider"
              defaultModel = strOpt generation "defaultModel"
              toolPolicyDefault = str toolsConfig "policyDefault" "auto"
              toolPolicy =
                (let mutable n: JsonNode = null
                 if toolsConfig.TryGetPropertyValue("policy", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Object then
                     n.AsObject()
                     |> Seq.choose (fun kv ->
                         if not (isNull kv.Value) && kv.Value.GetValueKind() = JsonValueKind.String then Some(kv.Key, kv.Value.GetValue<string>())
                         else None)
                     |> Map.ofSeq
                 else Map.empty)
              approvalTimeoutSeconds = intOf toolsConfig "approvalTimeoutSeconds" 300
              exposeDebug = boolOf toolsConfig "exposeDebug" false } }

    /// 参与模型选择的模型服务：启用且至少一个模型。
    let usableProviders (catalog: Catalog) =
        catalog.providers |> List.filter (fun p -> p.enabled && not (List.isEmpty p.models))

    let isReady (catalog: Catalog) = not (List.isEmpty (usableProviders catalog))

    let tryProvider (id: string) (catalog: Catalog) =
        catalog.providers |> List.tryFind (fun p -> p.id = id)

    /// 展示名：「模型服务 · 模型」。找不到模型服务时退化成裸模型名。
    let describeModel (providerId: string) (model: string) (catalog: Catalog) =
        match tryProvider providerId catalog with
        | Some p when not (String.IsNullOrWhiteSpace model) -> sprintf "%s · %s" p.label model
        | Some p -> p.label
        | None when String.IsNullOrWhiteSpace model -> "未选择模型"
        | None -> model

    /// 新对话的默认 (模型服务 id, 模型)：
    /// 全局默认存在且可用 → 它（模型不在列表中则回落该服务的 defaultModel）；
    /// 否则首个可用的模型服务及其默认模型。与主机 `AppConfig.defaultSelection` 同一规则。
    let defaultSelection (catalog: Catalog) =
        let usable = usableProviders catalog
        let modelOf (p: ProviderInfo) (requested: string) =
            if not (String.IsNullOrWhiteSpace requested) && List.contains requested p.models then requested
            elif not (String.IsNullOrWhiteSpace p.defaultModel) then p.defaultModel
            else p.models |> List.tryHead |> Option.defaultValue ""
        let chosen =
            catalog.generation.defaultProvider
            |> Option.bind (fun id -> usable |> List.tryFind (fun p -> p.id = id))
        match chosen with
        | Some p -> Some(p.id, modelOf p (catalog.generation.defaultModel |> Option.defaultValue ""))
        | None -> usable |> List.tryHead |> Option.map (fun p -> p.id, modelOf p "")

    /// 决策 330/331：某模型能否看图。用户指定 > 模型服务信息 > 名字推断。
    let visionOf (providerId: string) (model: string) (catalog: Catalog) : VisionSupport * VisionSource =
        let ofBool b = if b then VisionYes else VisionNo
        match tryProvider providerId catalog with
        | Some p when p.visionOverrides.ContainsKey model -> ofBool p.visionOverrides[model], VisionFromUser
        | Some p when p.visionDetected.ContainsKey model -> ofBool p.visionDetected[model], VisionFromProvider
        | _ -> ModelVision.classify model, VisionFromName

    /// 该 (服务, 模型) 是否正是全局默认（设置页行内标记用）。
    let isGlobalDefault (providerId: string) (catalog: Catalog) =
        match defaultSelection catalog with
        | Some(id, _) -> id = providerId && catalog.generation.defaultProvider = Some providerId
        | None -> false

/// 对话预设的纯逻辑（决策 433–436）：套用、另存、描述。界面只负责画。
module Presets =
    open Wanxiang.Core

    /// 套用预设得到的会话配置。预设里的模型服务不可用（被删、停用、没有该模型）时
    /// 回落到新对话默认模型，并返回 Some 提示文案；不可用的工具静默剔除并计入提示。
    let toSessionConfig (catalog: Catalog) (preset: PresetInfo) : SessionConfig * string option =
        let usable = Catalog.usableProviders catalog
        let requested =
            if String.IsNullOrWhiteSpace preset.provider then None
            else usable |> List.tryFind (fun p -> p.id = preset.provider)
        let provider, model, modelNote =
            match requested with
            | Some p ->
                let model =
                    if not (String.IsNullOrWhiteSpace preset.model) && List.contains preset.model p.models then preset.model
                    elif not (String.IsNullOrWhiteSpace p.defaultModel) then p.defaultModel
                    else p.models |> List.tryHead |> Option.defaultValue ""
                let note =
                    if not (String.IsNullOrWhiteSpace preset.model) && model <> preset.model then
                        Some(sprintf "预设里的模型 %s 已不可用，改用 %s。" preset.model model)
                    else None
                p.id, model, note
            | None ->
                let p, m = Catalog.defaultSelection catalog |> Option.defaultValue ("", "")
                let note =
                    if String.IsNullOrWhiteSpace preset.provider then None
                    else Some(sprintf "预设里的模型服务已不可用，改用默认模型 %s。" (Catalog.describeModel p m catalog))
                p, m, note
        let available = catalog.tools |> List.map (fun t -> t.id) |> Set.ofList
        let tools = preset.tools |> List.filter available.Contains
        let dropped = List.length preset.tools - List.length tools
        let toolNote = if dropped > 0 then Some(sprintf "有 %d 个工具主机上已没有，已跳过。" dropped) else None
        let note =
            match [ modelNote; toolNote ] |> List.choose id with
            | [] -> None
            | parts -> Some(String.concat "" parts)
        { SessionConfig.empty with
            provider = provider
            model = model
            instructions = preset.instructions |> Option.orElse catalog.generation.instructions
            tools = tools
            temperature = preset.temperature |> Option.orElse catalog.generation.temperature
            topP = preset.topP |> Option.orElse catalog.generation.topP
            maxTokens = preset.maxTokens |> Option.orElse catalog.generation.maxTokens
            thinkingBudget = preset.thinkingBudget },
        note

    /// 「存为预设」的上行负载：取对话当前的完整会话配置。id 缺省 = 新建。
    let payloadOf (id: string option) (name: string) (config: SessionConfig) : JsonObject =
        let o = JsonObject()
        id |> Option.iter (fun i -> o["id"] <- i)
        o["name"] <- name.Trim()
        o["provider"] <- config.provider
        o["model"] <- config.model
        config.instructions |> Option.iter (fun i -> o["instructions"] <- i)
        o["tools"] <- JsonArray([| for t in config.tools -> JsonNode.op_Implicit t |])
        config.temperature |> Option.iter (fun x -> o["temperature"] <- x)
        config.topP |> Option.iter (fun x -> o["topP"] <- x)
        config.maxTokens |> Option.iter (fun x -> o["maxTokens"] <- x)
        config.thinkingBudget |> Option.iter (fun x -> o["thinkingBudget"] <- x)
        o

    let ofPreset (p: PresetInfo) : SessionConfig =
        { SessionConfig.empty with
            provider = p.provider; model = p.model; instructions = p.instructions; tools = p.tools
            temperature = p.temperature; topP = p.topP; maxTokens = p.maxTokens; thinkingBudget = p.thinkingBudget }

    /// 一行说明：模型 · 指令首句 · 工具数，用在命令面板副标题和设置列表。
    let describe (catalog: Catalog) (p: PresetInfo) : string =
        let model =
            if String.IsNullOrWhiteSpace p.provider then "默认模型"
            else Catalog.describeModel p.provider p.model catalog
        let instr =
            p.instructions
            |> Option.map (fun i ->
                let line = i.Split('\n').[0].Trim()
                if line.Length > 24 then line.Substring(0, 24) + "…" else line)
        let tools = if List.isEmpty p.tools then None else Some(sprintf "%d 个工具" p.tools.Length)
        String.Join(" · ", [ Some model; instr; tools ] |> List.choose id)

    /// 新名字的建议：对话标题，重名时补序号。
    let suggestName (existing: PresetInfo list) (title: string) : string =
        let baseName =
            let t = if String.IsNullOrWhiteSpace title || title = "新对话" then "我的预设" else title.Trim()
            if t.Length > 30 then t.Substring(0, 30) else t
        let taken = existing |> List.map (fun p -> p.name.ToLowerInvariant()) |> Set.ofList
        if not (taken.Contains(baseName.ToLowerInvariant())) then baseName
        else
            Seq.initInfinite (fun i -> sprintf "%s %d" baseName (i + 2))
            |> Seq.find (fun n -> not (taken.Contains(n.ToLowerInvariant())))

/// 模型探测失败的类型（与主机 `ProviderCatalog.ProbeFailure` 的稳定错误码一一对应）。
/// 界面按类型决定下一步，不再靠文案关键词判断。
type ProbeFailureKind =
    | ProbeInvalidKey
    | ProbeEndpointNotFound
    | ProbeRateLimited
    | ProbeServerError
    | ProbeUnreachable
    | ProbeTimedOut
    | ProbeBadResponse
    | ProbeRejected
    | ProbeCancelled
    /// 旧主机（无错误码前缀）或未知码
    | ProbeUnclassified

module ProbeFailureKind =

    let ofCode (code: string) =
        match code with
        | "invalid-key" -> ProbeInvalidKey
        | "endpoint-not-found" -> ProbeEndpointNotFound
        | "rate-limited" -> ProbeRateLimited
        | "server-error" -> ProbeServerError
        | "unreachable" -> ProbeUnreachable
        | "timeout" -> ProbeTimedOut
        | "bad-response" -> ProbeBadResponse
        | "rejected" -> ProbeRejected
        | "cancelled" -> ProbeCancelled
        | _ -> ProbeUnclassified

    /// 解析线上 `[code] 说明`。没有前缀时类型为 ProbeUnclassified，说明原样保留。
    let parse (text: string) : ProbeFailureKind * string =
        if not (String.IsNullOrWhiteSpace text) && text.StartsWith("[", StringComparison.Ordinal) then
            let close = text.IndexOf ']'
            if close > 1 then ofCode (text.Substring(1, close - 1)), text.Substring(close + 1).TrimStart()
            else ProbeUnclassified, text
        else
            ProbeUnclassified, (if isNull text then "" else text)

    /// 失败后值得给「改用兼容端点 / 改用原生接口」这类下一步的类型。
    let suggestsTransportSwitch (kind: ProbeFailureKind) =
        match kind with
        | ProbeEndpointNotFound | ProbeBadResponse -> true
        | _ -> false

/// 快速切换模型里的一行（决策 240）。
type ModelOption = {
    providerId: string
    providerLabel: string
    model: string
    /// 本对话（或新对话草稿）当前使用的就是它
    isCurrent: bool
    /// 新对话的默认模型（与 `Catalog.defaultSelection` 同一规则）
    isDefault: bool
}

/// 快速切换模型的纯逻辑：列出、搜索、排序。界面只负责画。
module ModelSwitcher =

    /// 所有可用模型，按模型服务的配置顺序、服务内模型顺序。
    let options (catalog: Catalog) (current: (string * string) option) : ModelOption list =
        let defaultSel = Catalog.defaultSelection catalog
        [ for p in Catalog.usableProviders catalog do
              for m in p.models ->
                  { providerId = p.id
                    providerLabel = p.label
                    model = m
                    isCurrent = current = Some(p.id, m)
                    isDefault = defaultSel = Some(p.id, m) } ]

    /// 搜索：按空白拆词，每个词都要出现在「模型名」或「服务名」里（不区分大小写）。
    /// 空查询返回全部。命中模型名开头的排前面，其余保持原顺序。
    let filter (query: string) (items: ModelOption list) : ModelOption list =
        let terms =
            if String.IsNullOrWhiteSpace query then [||]
            else query.Trim().ToLowerInvariant().Split([| ' '; '\t'; '　' |], StringSplitOptions.RemoveEmptyEntries)
        if terms.Length = 0 then items
        else
            let hay (o: ModelOption) = (o.model + " " + o.providerLabel + " " + o.providerId).ToLowerInvariant()
            let matched = items |> List.filter (fun o -> let h = hay o in terms |> Array.forall h.Contains)
            let first = terms[0]
            let prefix, rest = matched |> List.partition (fun o -> o.model.ToLowerInvariant().StartsWith first)
            prefix @ rest

    /// 顶栏芯片文字：模型名（短），服务名放第二层（提示与读屏）。
    let chipText (providerId: string) (model: string) (catalog: Catalog) : string =
        if String.IsNullOrWhiteSpace model then Catalog.describeModel providerId model catalog else model

    /// 切换后的确认文案：明说「从下一条回复开始」，已有回复不受影响。
    let switchedToast (providerLabel: string) (model: string) : string =
        sprintf "已切换到 %s · %s，从下一条回复开始生效" providerLabel model

    /// 浮层底部常驻说明。
    let appliesNote = "切换只影响之后的回复，已有回复不会改变。"
