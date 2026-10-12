namespace Wanxiang.UI

open System

/// 决策 320：模型能不能看图，按模型名判断。
///
/// 模型服务的模型列表不带能力字段，靠名字是唯一现成的信号。三档：
/// - Supported：已知能看图；
/// - Unsupported：已知只吃文字——带图发送前就拦下，并说清怎么办；
/// - Unknown：认不出——放行，主机会把图交给模型服务，由它决定。
/// 宁可放行认不出的，也不误拦能看图的模型。
type VisionSupport =
    | VisionYes
    | VisionNo
    | VisionUnknown

module ModelVision =

    let private contains (needle: string) (s: string) = s.Contains(needle, StringComparison.Ordinal)

    /// 明确能看图的名字片段。
    let private visionMarkers =
        [ "vision"; "-vl"; "vl-"; "llava"; "pixtral"; "minicpm-v"; "moondream"; "bakllava"
          "gpt-4o"; "gpt-4.1"; "gpt-4-turbo"; "gpt-5"; "chatgpt-4o"; "o3"; "o4"
          "claude-3"; "claude-sonnet-4"; "claude-opus-4"; "claude-haiku-4"; "claude-4"
          "gemini"; "gemma3"; "gemma-3"; "llama-4"; "llama4"; "qwen2.5-omni"; "qvq"; "grok-4"; "grok-2-vision"; "glm-4v"; "internvl" ]

    /// 明确只吃文字的名字片段（先判能看图的，避免 "llama3.2-vision" 被 "llama3" 误伤）。
    let private textOnlyMarkers =
        [ "gpt-3.5"; "o1-mini"; "o3-mini"; "deepseek"; "qwq"; "qwen2.5-coder"; "codellama"
          "mistral"; "mixtral"; "phi3"; "phi-3"; "llama2"; "llama3"; "llama-3"; "qwen2"; "qwen3"
          "embedding"; "embed"; "whisper"; "tts" ]

    let classify (modelId: string) : VisionSupport =
        if String.IsNullOrWhiteSpace modelId then VisionUnknown
        else
            let id = modelId.Trim().ToLowerInvariant()
            // 文本 mini 推理模型优先于 "o3" 视觉标记。
            if contains "o3-mini" id || contains "o1-mini" id then VisionNo
            elif visionMarkers |> List.exists (fun m -> contains m id) then VisionYes
            elif textOnlyMarkers |> List.exists (fun m -> contains m id) then VisionNo
            else VisionUnknown

    /// 带图却遇到只吃文字的模型时，输入框上方的提示（说清问题与两条出路）。
    let blockedMessage (modelLabel: string) =
        sprintf "当前模型「%s」不能识别图片，图片不会发出去。请换一个能看图的模型，或移除图片后再发送。" modelLabel
