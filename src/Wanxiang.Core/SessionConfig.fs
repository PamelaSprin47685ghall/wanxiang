namespace Wanxiang.Core

open System.Text.Json.Nodes

/// 会话级配置快照：创建/修改会话时整体固化，不记录 patch。
type SessionConfig = {
    /// Provider 稳定标识，如 "openai" / "ollama"
    provider: string
    /// 模型名称
    model: string
    /// 系统指令
    instructions: string option
    /// 启用的工具稳定标识（builtin:... / mcp:<id>/<name>）
    tools: string list
    /// 生成参数（可选，透传）
    temperature: float option
    maxTokens: int option
    /// 其余生成参数，原样透传给 Provider（首版透明传递）
    extraJson: JsonNode option
}

module SessionConfig =

    let empty : SessionConfig =
        { provider = "openai"
          model = ""
          instructions = None
          tools = []
          temperature = None
          maxTokens = None
          extraJson = None }

    let isValid (cfg: SessionConfig) : bool =
        not (System.String.IsNullOrWhiteSpace cfg.provider)
        && not (System.String.IsNullOrWhiteSpace cfg.model)
        && (match cfg.temperature with Some t -> t >= 0.0 && t <= 2.0 | None -> true)
        && (match cfg.maxTokens with Some m -> m > 0 | None -> true)
