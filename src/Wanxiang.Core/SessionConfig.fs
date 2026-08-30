namespace Wanxiang.Core

open System
open System.Text.Json.Nodes

/// 会话级配置快照：创建/修改会话时整体固化，不记录 patch。
type SessionConfig = {
    /// Provider 稳定标识（TOML `[providers.<id>]` 的 id）
    provider: string
    /// 模型名称。必须是该 provider `models` 列表中的一项；
    /// 越界或留空时服务端回落到 provider 的 defaultModel。
    model: string
    /// 系统指令
    instructions: string option
    /// 启用的工具稳定标识（builtin:... / mcp:<id>/<name>）
    tools: string list
    /// 生成参数（可选，透传）
    temperature: float option
    topP: float option
    maxTokens: int option
    /// 思维链预算（token）。None = 跟随 [generation] 默认值，0 = 本会话不开。
    thinkingBudget: int option
    /// 其余生成参数，原样透传给 Provider（reasoning_effort 等模型专属项）
    extraJson: JsonNode option
}

module SessionConfig =

    let empty : SessionConfig =
        { provider = ""
          model = ""
          instructions = None
          tools = []
          temperature = None
          topP = None
          maxTokens = None
          thinkingBudget = None
          extraJson = None }

    /// 逐字段校验，返回全部问题（供 UI 精确指出哪一项不合法）。
    let validate (cfg: SessionConfig) : string list =
        [ if String.IsNullOrWhiteSpace cfg.provider then "provider: 必填"
          if String.IsNullOrWhiteSpace cfg.model then "model: 必填"
          match cfg.temperature with
          | Some t when t < 0.0 || t > 2.0 -> "temperature: 需在 0–2 之间"
          | _ -> ()
          match cfg.topP with
          | Some p when p <= 0.0 || p > 1.0 -> "topP: 需在 0–1 之间"
          | _ -> ()
          match cfg.maxTokens with
          | Some m when m <= 0 -> "maxTokens: 需为正整数"
          | _ -> () ]

    let isValid (cfg: SessionConfig) : bool = validate cfg |> List.isEmpty
