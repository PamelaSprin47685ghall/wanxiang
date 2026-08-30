namespace Wanxiang.UI

open System
open System.Text.Json
open System.Text.Json.Nodes

/// 服务端下发的一个服务商（`catalog.snapshot`）。密钥永不下发，只有 `hasApiKey`。
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

/// 一个 MCP 服务器配置。
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
}

/// 服务端能力目录。客户端所有选择器（模型、工具）都从这里取数据，
/// 用户永远不需要手打服务商 id 或模型名。
type Catalog = {
    providers: ProviderInfo list
    tools: ToolInfo list
    mcpServers: McpInfo list
    generation: GenerationInfo
}

module Catalog =

    let empty =
        { providers = []
          tools = []
          mcpServers = []
          generation =
            { temperature = None
              topP = None
              maxTokens = None
              instructions = None
              maxContextMessages = 200
              autoTitle = true
              maxToolRounds = 12
              fileReadRoots = [] } }

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
          enabled = boolOf o "enabled" true }

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
          generation =
            { temperature = floatOpt generation "temperature"
              topP = floatOpt generation "topP"
              maxTokens = intOpt generation "maxTokens"
              instructions = strOpt generation "instructions"
              maxContextMessages = intOf generation "maxContextMessages" 200
              autoTitle = boolOf generation "autoTitle" true
              maxToolRounds = intOf generation "maxToolRounds" 12
              fileReadRoots = strings toolsConfig "fileReadRoots" } }

    /// 参与模型选择的服务商：启用且至少一个模型。
    let usableProviders (catalog: Catalog) =
        catalog.providers |> List.filter (fun p -> p.enabled && not (List.isEmpty p.models))

    let isReady (catalog: Catalog) = not (List.isEmpty (usableProviders catalog))

    let tryProvider (id: string) (catalog: Catalog) =
        catalog.providers |> List.tryFind (fun p -> p.id = id)

    /// 展示名：「服务商 · 模型」。找不到服务商时退化成裸模型名。
    let describeModel (providerId: string) (model: string) (catalog: Catalog) =
        match tryProvider providerId catalog with
        | Some p when not (String.IsNullOrWhiteSpace model) -> sprintf "%s · %s" p.label model
        | Some p -> p.label
        | None when String.IsNullOrWhiteSpace model -> "未选择模型"
        | None -> model

    /// 首个可用的 (服务商 id, 模型)。
    let defaultSelection (catalog: Catalog) =
        usableProviders catalog
        |> List.tryHead
        |> Option.map (fun p ->
            let model =
                if not (String.IsNullOrWhiteSpace p.defaultModel) then p.defaultModel
                else p.models |> List.tryHead |> Option.defaultValue ""
            p.id, model)
