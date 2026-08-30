namespace Wanxiang.UI

/// 一个服务商预设：填好端点与常见模型，用户只需要粘贴密钥。
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
}

/// 服务商预设表。
///
/// 大多数服务商走 OpenAI 兼容端点，这套协议已是事实标准，不必为每家写 SDK。
/// Claude 与 Gemini 另有**原生**预设：兼容层会丢掉思维链、prompt caching、
/// systemInstruction 这些只在原生协议里存在的能力。
/// 预设的价值在于用户不用去翻文档找 baseUrl 和模型名。
module ProviderPresets =

    let all : ProviderPreset list =
        [ { key = "openai"
            label = "OpenAI"
            kind = "openai"
            baseUrl = "https://api.openai.com/v1"
            models = [ "gpt-5"; "gpt-5-mini"; "gpt-4.1"; "gpt-4o"; "gpt-4o-mini"; "o4-mini" ]
            needsApiKey = true
            hint = "在 platform.openai.com 的 API keys 页面创建。" }

          { key = "claude"
            label = "Anthropic Claude（原生）"
            kind = "anthropic"
            baseUrl = "https://api.anthropic.com"
            models =
              [ "claude-opus-4-1"
                "claude-sonnet-4-5"
                "claude-haiku-4-5"
                "claude-3-7-sonnet-latest" ]
            needsApiKey = true
            hint = "原生 Messages API，支持思维链与 prompt caching；密钥在 console.anthropic.com 创建。" }

          { key = "gemini"
            label = "Google Gemini（原生）"
            kind = "gemini"
            baseUrl = "https://generativelanguage.googleapis.com"
            models = [ "gemini-2.5-pro"; "gemini-2.5-flash"; "gemini-2.0-flash" ]
            needsApiKey = true
            hint = "原生 generateContent，支持 systemInstruction 与多模态入参；密钥在 aistudio.google.com 创建。" }

          { key = "anthropic-compat"
            label = "Anthropic Claude（兼容端点）"
            kind = "openai"
            baseUrl = "https://api.anthropic.com/v1"
            models =
              [ "claude-opus-4-1"
                "claude-sonnet-4-5"
                "claude-haiku-4-5"
                "claude-3-7-sonnet-latest" ]
            needsApiKey = true
            hint = "使用 Anthropic 的 OpenAI 兼容端点；密钥在 console.anthropic.com 创建。" }

          { key = "gemini-compat"
            label = "Google Gemini（兼容端点）"
            kind = "openai"
            baseUrl = "https://generativelanguage.googleapis.com/v1beta/openai"
            models = [ "gemini-2.5-pro"; "gemini-2.5-flash"; "gemini-2.0-flash" ]
            needsApiKey = true
            hint = "使用 Gemini 的 OpenAI 兼容端点；密钥在 aistudio.google.com 创建。" }

          { key = "deepseek"
            label = "DeepSeek"
            kind = "openai"
            baseUrl = "https://api.deepseek.com/v1"
            models = [ "deepseek-chat"; "deepseek-reasoner" ]
            needsApiKey = true
            hint = "在 platform.deepseek.com 创建密钥。" }

          { key = "moonshot"
            label = "Moonshot Kimi"
            kind = "openai"
            baseUrl = "https://api.moonshot.cn/v1"
            models = [ "kimi-k2-0905-preview"; "moonshot-v1-128k"; "moonshot-v1-32k" ]
            needsApiKey = true
            hint = "在 platform.moonshot.cn 创建密钥。" }

          { key = "zhipu"
            label = "智谱 GLM"
            kind = "openai"
            baseUrl = "https://open.bigmodel.cn/api/paas/v4"
            models = [ "glm-4.6"; "glm-4.5"; "glm-4.5-air" ]
            needsApiKey = true
            hint = "在 open.bigmodel.cn 创建密钥。" }

          { key = "qwen"
            label = "通义千问（阿里云百炼）"
            kind = "openai"
            baseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1"
            models = [ "qwen3-max"; "qwen-plus"; "qwen-turbo" ]
            needsApiKey = true
            hint = "在阿里云百炼控制台创建 API-KEY。" }

          { key = "siliconflow"
            label = "硅基流动"
            kind = "openai"
            baseUrl = "https://api.siliconflow.cn/v1"
            models = [ "deepseek-ai/DeepSeek-V3"; "Qwen/Qwen3-235B-A22B" ]
            needsApiKey = true
            hint = "在 cloud.siliconflow.cn 创建密钥。" }

          { key = "openrouter"
            label = "OpenRouter"
            kind = "openai"
            baseUrl = "https://openrouter.ai/api/v1"
            models = [ "anthropic/claude-sonnet-4.5"; "openai/gpt-5"; "google/gemini-2.5-pro" ]
            needsApiKey = true
            hint = "一个密钥聚合多家模型；在 openrouter.ai 创建。" }

          { key = "groq"
            label = "Groq"
            kind = "openai"
            baseUrl = "https://api.groq.com/openai/v1"
            models = [ "llama-3.3-70b-versatile"; "openai/gpt-oss-120b" ]
            needsApiKey = true
            hint = "在 console.groq.com 创建密钥。" }

          { key = "xai"
            label = "xAI Grok"
            kind = "openai"
            baseUrl = "https://api.x.ai/v1"
            models = [ "grok-4"; "grok-3"; "grok-3-mini" ]
            needsApiKey = true
            hint = "在 console.x.ai 创建密钥。" }

          { key = "ollama"
            label = "Ollama（本机）"
            kind = "openai"
            baseUrl = "http://127.0.0.1:11434/v1"
            models = [ "qwen3"; "llama3.2"; "gemma3" ]
            needsApiKey = false
            hint = "先在本机运行 ollama serve；模型名用 ollama list 里的名字。" }

          { key = "lmstudio"
            label = "LM Studio（本机）"
            kind = "openai"
            baseUrl = "http://127.0.0.1:1234/v1"
            models = [ "local-model" ]
            needsApiKey = false
            hint = "在 LM Studio 里开启 Local Server。" }

          { key = "custom"
            label = "自定义 OpenAI 兼容端点"
            kind = "openai"
            baseUrl = ""
            models = []
            needsApiKey = true
            hint = "任何提供 /v1/chat/completions 的服务都可以填在这里。" } ]

    let tryFind (key: string) = all |> List.tryFind (fun p -> p.key = key)

    /// 用 baseUrl 反查预设，便于编辑已有服务商时高亮当前预设。
    let matchByBaseUrl (baseUrl: string) =
        if System.String.IsNullOrWhiteSpace baseUrl then None
        else
            let normalized = baseUrl.TrimEnd('/')
            all |> List.tryFind (fun p -> p.baseUrl.TrimEnd('/') = normalized)

    /// 给新服务商建议一个不冲突的稳定标识。
    let suggestId (preset: ProviderPreset) (taken: string list) =
        let baseId = if preset.key = "custom" then "provider" else preset.key
        if not (List.contains baseId taken) then baseId
        else
            let rec probe n =
                let candidate = sprintf "%s-%d" baseId n
                if List.contains candidate taken then probe (n + 1) else candidate
            probe 2
