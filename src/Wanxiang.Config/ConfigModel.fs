namespace Wanxiang.Config

open System
open System.Text.Json.Nodes

/// Provider 配置（TOML `[providers.<id>]`）。
///
/// 一个 provider = 一组凭据 + 一个端点 + 一批可用模型。
/// 模型是 provider 的**列表**属性，不再一个模型开一个 provider：
/// 用户在会话里换模型不需要改配置。
type ProviderConfig = {
    id: string
    /// 传输类型：
    /// - `"openai"`：OpenAI 兼容 HTTP（OpenAI / Ollama / DeepSeek / OpenRouter /
    ///   Groq / 硅基流动 / Moonshot / 智谱 / xAI，以及 Anthropic 与 Gemini 的兼容端点）
    /// - `"anthropic"`：Anthropic Messages API 原生
    /// - `"gemini"`：Gemini generateContent 原生
    kind: string
    /// 界面展示名；空则回落到 id
    label: string
    baseUrl: string
    apiKey: string option
    /// 可用模型列表（模型选择器的数据源，至少一项）
    models: string list
    /// 默认模型，必须出现在 models 中
    defaultModel: string
    /// 单次 HTTP 请求超时（秒）
    timeoutSeconds: int
    /// 可重试失败的最大重试次数（指数退避 + 尊重 Retry-After）
    /// 默认 0：计费调用默认不自动重发，需要才显式打开。
    maxRetries: int
    /// 关闭后不参与模型选择，也不允许新会话使用
    enabled: bool
    /// 提示词缓存（目前只有 Anthropic 原生传输支持）。
    /// 默认关：缓存写入比普通请求贵，开与不开是计费决定，不该由程序替用户做。
    promptCaching: bool
    /// 追加请求头（如网关鉴权）
    headers: Map<string, string>
    /// 其余生成参数，原样透传给 Provider
    extraJson: JsonNode option
}

module ProviderConfig =

    let defaultTimeoutSeconds = 120
    let defaultMaxRetries = 0

    /// 界面展示名：label 为空时回落 id。
    let displayName (p: ProviderConfig) : string =
        if String.IsNullOrWhiteSpace p.label then p.id else p.label

    /// 该 provider 是否提供指定模型。
    let hasModel (model: string) (p: ProviderConfig) : bool =
        p.models |> List.exists (fun m -> String.Equals(m, model, StringComparison.Ordinal))

    /// 解析会话请求的模型：命中列表则用它，否则回落默认模型。
    let resolveModel (requested: string) (p: ProviderConfig) : string =
        if not (String.IsNullOrWhiteSpace requested) && hasModel requested p then requested
        elif not (String.IsNullOrWhiteSpace p.defaultModel) then p.defaultModel
        else p.models |> List.tryHead |> Option.defaultValue ""

/// MCP Server 配置（TOML `[mcp.<id>]`）。
type McpServerConfig = {
    id: string
    /// 界面展示名；空则回落 id
    label: string
    /// 本地 stdio MCP：可执行文件
    command: string option
    args: string list
    env: Map<string, string>
    /// 远程 MCP：Streamable HTTP / SSE 端点（与 command 二选一）
    url: string option
    /// 并发上限；None = 不额外限流
    maxConcurrency: int option
    /// 单次工具调用超时（秒）
    callTimeoutSeconds: int
    /// 关闭后不注册其工具
    enabled: bool
}

module McpServerConfig =

    let defaultCallTimeoutSeconds = 60

    let displayName (m: McpServerConfig) : string =
        if String.IsNullOrWhiteSpace m.label then m.id else m.label

/// 生成参数默认值（会话未显式指定时使用）。
type GenerationDefaults = {
    temperature: float option
    topP: float option
    maxTokens: int option
    /// 默认系统指令（新会话初值）
    instructions: string option
    /// 单次请求最多携带多少条历史消息；0 = 不限
    maxContextMessages: int
    /// 单次请求最多携带多少 token（含图片估算，见 GenerationContext.trim）。
    /// 超出即从旧往新裁剪，与 maxContextMessages 是两道栏；0 = 不限。
    maxContextTokens: int
    /// 首轮对话后自动生成会话标题
    autoTitle: bool
    /// 工具调用循环的最大轮次，防止模型无限调工具
    maxToolRounds: int
    /// 思维链预算（token）。0 = 不开。
    /// 原生传输把它映到 Anthropic 的 `thinking.budget_tokens` 与
    /// Gemini 的 `generationConfig.thinkingConfig.thinkingBudget`；
    /// OpenAI 兼容传输忽略它（那边靠 extraJson 里的 reasoning_effort）。
    thinkingBudget: int
}

module GenerationDefaults =

    let defaults : GenerationDefaults =
        { temperature = None
          topP = None
          maxTokens = None
          instructions = None
          maxContextMessages = 200
          maxContextTokens = 128000
          thinkingBudget = 0
          autoTitle = true
          maxToolRounds = 12 }

/// 内建工具配置。
type ToolsConfig = {
    /// `builtin:file.read` 的沙箱根目录白名单；空列表 = 该工具不可用
    fileReadRoots: string list
    /// 单个内建工具调用超时（秒）
    callTimeoutSeconds: int
}

module ToolsConfig =

    let defaults : ToolsConfig =
        { fileReadRoots = []
          callTimeoutSeconds = 30 }

/// 已授权客户端（令牌哈希即身份）。
type ClientAuthRecord = {
    tokenHash: string
    name: string
    createdAtUtc: DateTimeOffset
    lastSeenUtc: DateTimeOffset option
    revoked: bool
}

type RuntimeSwitches = {
    server: bool
    client: bool
    pwa: bool
    fix: bool
}

/// 唯一配置事实来源的强类型视图（TOML 是权威；本类型是其投影）。
type AppConfig = {
    configVersion: int
    instanceId: Guid
    runtime: RuntimeSwitches
    listen: string
    /// PEM 证书链与私钥的路径。两者都填才启 TLS，空串表示不启。
    /// 用 PEM 而非 PFX：certbot 与绝大多数签发流程直接产出 PEM。
    tlsCertPath: string
    tlsKeyPath: string
    maxAttachmentBytes: int64
    chunkSizeBytes: int
    pairingFailureWindowMinutes: int
    pairingMaxFailures: int
    pairingFreezeMinutes: int
    generation: GenerationDefaults
    tools: ToolsConfig
    providers: Map<string, ProviderConfig>
    mcpServers: Map<string, McpServerConfig>
    authClients: ClientAuthRecord list
}

module AppConfig =

    /// 配置结构版本。序列化格式尚未冻结，破坏性变更直接抬版本。
    [<Literal>]
    let CurrentVersion = 2

    let defaults (instanceId: Guid) : AppConfig =
        { configVersion = CurrentVersion
          instanceId = instanceId
          runtime = { server = true; client = true; pwa = true; fix = false }
          listen = "127.0.0.1:8765"
          tlsCertPath = ""
          tlsKeyPath = ""
          maxAttachmentBytes = 64L * 1024L * 1024L
          chunkSizeBytes = 256 * 1024
          pairingFailureWindowMinutes = 1
          pairingMaxFailures = 5
          pairingFreezeMinutes = 5
          generation = GenerationDefaults.defaults
          tools = ToolsConfig.defaults
          providers = Map.empty
          mcpServers = Map.empty
          authClients = [] }

    /// 参与模型选择的 provider（启用且至少有一个模型），按 id 稳定排序。
    let usableProviders (cfg: AppConfig) : ProviderConfig list =
        cfg.providers
        |> Map.toList
        |> List.map snd
        |> List.filter (fun p -> p.enabled && not (List.isEmpty p.models))
        |> List.sortBy (fun p -> p.id)

    /// 会话默认 provider：首个可用者。
    let defaultProvider (cfg: AppConfig) : ProviderConfig option =
        usableProviders cfg |> List.tryHead

    let tryProvider (id: string) (cfg: AppConfig) : ProviderConfig option =
        cfg.providers |> Map.tryFind id
