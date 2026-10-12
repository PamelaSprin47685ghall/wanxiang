namespace Wanxiang.UI

/// UI 侧传输类型闭合枚举：协议层字符串只在映射边界进出，不直接参与 UI 分支。
type ProviderKind =
    | OpenAiCompatible
    | AnthropicNative
    | GeminiNative
    | UnknownKind of string

module ProviderKind =

    let toText (kind: ProviderKind) =
        match kind with
        | OpenAiCompatible -> "openai"
        | AnthropicNative -> "anthropic"
        | GeminiNative -> "gemini"
        | UnknownKind raw -> raw

    let ofText (raw: string) =
        match raw with
        | "anthropic" -> AnthropicNative
        | "gemini" -> GeminiNative
        | "openai" -> OpenAiCompatible
        | _ when System.String.IsNullOrWhiteSpace raw -> OpenAiCompatible
        | _ -> UnknownKind raw

/// 一个模型服务预设：填好端点与常见模型，用户只需要粘贴密钥。
type ProviderPreset = {
    key: string
    label: string
    /// 传输类型：openai | anthropic | gemini
    kind: string
    baseUrl: string
    models: string list
    /// 是否需要密钥（本地推理不需要）
    needsApiKey: bool
    /// 去哪里拿密钥
    hint: string
    /// 同一家的另一条传输（原生 ↔ OpenAI 兼容端点）：(kind, baseUrl)。
    /// 用户默认只看到一个条目，由 `kind` 自动决定；只有高级区才能切换。
    alternate: (string * string) option
}

/// 模型服务预设表。
///
/// 大多数模型服务走 OpenAI 兼容端点，这套协议已是事实标准，不必为每家写 SDK。
/// Claude 与 Gemini 另有**原生**预设：兼容层会丢掉思维链、prompt caching、
/// systemInstruction 这些只在原生协议里存在的能力。
/// 预设的价值在于用户不用去翻文档找 baseUrl 和模型名。
module ProviderPresets =

    /// 预设表。前 4 家（`featured`）置顶；Claude 与 Gemini 的「原生 / 兼容端点」合并为一个条目，
    /// 默认走原生，兼容端点放在 `alternate`，高级区才可切换。
    let all : ProviderPreset list =
        [ { key = "openai"
            label = "OpenAI"
            kind = "openai"
            baseUrl = "https://api.openai.com/v1"
            models = [ "gpt-5"; "gpt-5-mini"; "gpt-4.1"; "gpt-4o"; "gpt-4o-mini"; "o4-mini" ]
            needsApiKey = true
            hint = "在 platform.openai.com 的 API keys 页面创建。"
            alternate = None }

          { key = "claude"
            label = "Anthropic Claude"
            kind = "anthropic"
            baseUrl = "https://api.anthropic.com"
            models =
              [ "claude-opus-4-1"
                "claude-sonnet-4-5"
                "claude-haiku-4-5"
                "claude-3-7-sonnet-latest" ]
            needsApiKey = true
            hint = "原生 Messages API，支持思维链与 prompt caching；密钥在 console.anthropic.com 创建。"
            alternate = Some("openai", "https://api.anthropic.com/v1") }

          { key = "gemini"
            label = "Google Gemini"
            kind = "gemini"
            baseUrl = "https://generativelanguage.googleapis.com"
            models = [ "gemini-2.5-pro"; "gemini-2.5-flash"; "gemini-2.0-flash" ]
            needsApiKey = true
            hint = "原生 generateContent，支持 systemInstruction 与多模态入参；密钥在 aistudio.google.com 创建。"
            alternate = Some("openai", "https://generativelanguage.googleapis.com/v1beta/openai") }

          { key = "ollama"
            label = "Ollama（本机）"
            kind = "openai"
            baseUrl = "http://127.0.0.1:11434/v1"
            models = [ "qwen3"; "llama3.2"; "gemma3" ]
            needsApiKey = false
            hint = "先在本机运行 ollama serve；模型名用 ollama list 里的名字。"
            alternate = None }

          { key = "deepseek"
            label = "DeepSeek"
            kind = "openai"
            baseUrl = "https://api.deepseek.com/v1"
            models = [ "deepseek-chat"; "deepseek-reasoner" ]
            needsApiKey = true
            hint = "在 platform.deepseek.com 创建密钥。"
            alternate = None }

          { key = "moonshot"
            label = "Moonshot Kimi"
            kind = "openai"
            baseUrl = "https://api.moonshot.cn/v1"
            models = [ "kimi-k2-0905-preview"; "moonshot-v1-128k"; "moonshot-v1-32k" ]
            needsApiKey = true
            hint = "在 platform.moonshot.cn 创建密钥。"
            alternate = None }

          { key = "zhipu"
            label = "智谱 GLM"
            kind = "openai"
            baseUrl = "https://open.bigmodel.cn/api/paas/v4"
            models = [ "glm-4.6"; "glm-4.5"; "glm-4.5-air" ]
            needsApiKey = true
            hint = "在 open.bigmodel.cn 创建密钥。"
            alternate = None }

          { key = "qwen"
            label = "通义千问（阿里云百炼）"
            kind = "openai"
            baseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1"
            models = [ "qwen3-max"; "qwen-plus"; "qwen-turbo" ]
            needsApiKey = true
            hint = "在阿里云百炼控制台创建密钥。"
            alternate = None }

          { key = "siliconflow"
            label = "硅基流动"
            kind = "openai"
            baseUrl = "https://api.siliconflow.cn/v1"
            models = [ "deepseek-ai/DeepSeek-V3"; "Qwen/Qwen3-235B-A22B" ]
            needsApiKey = true
            hint = "在 cloud.siliconflow.cn 创建密钥。"
            alternate = None }

          { key = "openrouter"
            label = "OpenRouter"
            kind = "openai"
            baseUrl = "https://openrouter.ai/api/v1"
            models = [ "anthropic/claude-sonnet-4.5"; "openai/gpt-5"; "google/gemini-2.5-pro" ]
            needsApiKey = true
            hint = "一个密钥聚合多家模型；在 openrouter.ai 创建。"
            alternate = None }

          { key = "groq"
            label = "Groq"
            kind = "openai"
            baseUrl = "https://api.groq.com/openai/v1"
            models = [ "llama-3.3-70b-versatile"; "openai/gpt-oss-120b" ]
            needsApiKey = true
            hint = "在 console.groq.com 创建密钥。"
            alternate = None }

          { key = "xai"
            label = "xAI Grok"
            kind = "openai"
            baseUrl = "https://api.x.ai/v1"
            models = [ "grok-4"; "grok-3"; "grok-3-mini" ]
            needsApiKey = true
            hint = "在 console.x.ai 创建密钥。"
            alternate = None }

          { key = "lmstudio"
            label = "LM Studio（本机）"
            kind = "openai"
            baseUrl = "http://127.0.0.1:1234/v1"
            models = [ "local-model" ]
            needsApiKey = false
            hint = "在 LM Studio 里开启 Local Server。"
            alternate = None }

          { key = "custom"
            label = "自定义 OpenAI 兼容端点"
            kind = "openai"
            baseUrl = ""
            models = []
            needsApiKey = true
            hint = "任何提供 /v1/chat/completions 的服务都可以填在这里。"
            alternate = None } ]

    /// 置顶展示的常用 4 家：OpenAI / Claude / Gemini / 本机 Ollama。其余折叠到「更多」。
    let featured : ProviderPreset list = all |> List.take 4

    /// 其余预设（折叠区）。
    let more : ProviderPreset list = all |> List.skip 4

    /// 本机 Ollama 的探测地址（只对 loopback 发请求，由用户所在的桌面客户端发起）。
    [<Literal>]
    let OllamaProbeUrl = "http://127.0.0.1:11434/v1/models"

    let tryFind (key: string) = all |> List.tryFind (fun p -> p.key = key)

    /// 预设的传输类型（UI 分支请用它，而不是直接比字符串）。
    let kindOf (preset: ProviderPreset) = ProviderKind.ofText preset.kind

    /// 归一化端点：首尾空白与末尾斜杠不产生新身份。
    /// 粘贴 `https://api.openai.com/v1/` 与手打 `https://api.openai.com/v1`
    /// 是同一个端点，预设匹配、分歧判断与保存载荷都走这里。
    let normalizeBaseUrl (url: string) =
        if System.String.IsNullOrWhiteSpace url then ""
        else url.Trim().TrimEnd('/')

    /// 用 baseUrl 反查预设，便于编辑已有模型服务时高亮当前预设。
    let matchByBaseUrl (baseUrl: string) =
        if System.String.IsNullOrWhiteSpace baseUrl then None
        else
            let normalized = normalizeBaseUrl baseUrl
            all
            |> List.tryFind (fun p ->
                normalizeBaseUrl p.baseUrl = normalized
                || (match p.alternate with
                    | Some(_, alt) -> normalizeBaseUrl alt = normalized
                    | None -> false))

    /// 给新模型服务建议一个不冲突的稳定标识。
    let suggestId (preset: ProviderPreset) (taken: string list) =
        let baseId = if preset.key = "custom" then "provider" else preset.key
        if not (List.contains baseId taken) then baseId
        else
            let rec probe n =
                let candidate = sprintf "%s-%d" baseId n
                if List.contains candidate taken then probe (n + 1) else candidate
            probe 2

    /// 模型服务保存载荷。设置页编辑器与首次运行卡片**共用这一个**构造函数，
    /// 两个入口因此经同一个 `ConfigMutation.upsertProvider` 写出相同的 TOML。
    let payload
        (id: string)
        (label: string)
        (kind: string)
        (baseUrl: string)
        (apiKey: string option)
        (models: string list)
        (defaultModel: string)
        (enabled: bool)
        (timeout: int)
        (retries: int)
        : System.Text.Json.Nodes.JsonObject =
        let o = System.Text.Json.Nodes.JsonObject()
        o["id"] <- id
        o["label"] <- label
        // 传输类型由预设决定（原生 Claude / 原生 Gemini / OpenAI 兼容）。
        o["kind"] <- (if System.String.IsNullOrWhiteSpace kind then "openai" else kind)
        o["baseUrl"] <- baseUrl
        match apiKey with
        | Some key when not (System.String.IsNullOrWhiteSpace key) -> o["apiKey"] <- key
        | _ -> ()
        o["models"] <- System.Text.Json.Nodes.JsonArray([| for m in models -> System.Text.Json.Nodes.JsonNode.op_Implicit m |])
        o["defaultModel"] <- defaultModel
        o["enabled"] <- enabled
        o["timeoutSeconds"] <- timeout
        o["maxRetries"] <- retries
        o

    /// 预设默认的保存载荷（首次运行卡片：用户只填了密钥）。
    /// `models` 为空时用预设自带的清单（探测前先落一份可用的）；超时与重试取编辑器同一默认值。
    let presetPayload (preset: ProviderPreset) (id: string) (apiKey: string option) (models: string list) =
        let models = if List.isEmpty models then preset.models else models
        payload id preset.label preset.kind preset.baseUrl apiKey models (List.head models) true 120 2
