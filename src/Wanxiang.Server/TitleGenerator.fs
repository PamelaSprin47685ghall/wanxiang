namespace Wanxiang.Server

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Wanxiang.Agent
open Wanxiang.Config
open Wanxiang.Core

/// 首轮对话后自动给会话起标题。
///
/// 用户不该被迫看着一列「会话 3f8a1c22」。这里用同一个 provider
/// 做一次极短的旁路补全；失败就静默回落到用户首句的截断，
/// 绝不因为起标题失败影响正常对话。
module TitleGenerator =

    /// 标题最长字符数：侧栏一行能放下的量级。
    let maxLength = 24

    let private instruction =
        "用一句不超过 12 个字的短语概括这段对话的主题，作为会话标题。"
        + "只输出标题本身，不要引号、句号、前后缀或解释。"

    /// 从用户首句截断出兜底标题。
    let fallback (userText: string) : string =
        let cleaned = PlainText.ofMarkdown userText
        if String.IsNullOrWhiteSpace cleaned then "新会话"
        elif cleaned.Length <= maxLength then cleaned
        else cleaned.Substring(0, maxLength) + "…"

    /// 清洗模型返回：去引号、去换行、限长。
    let sanitize (raw: string) (userText: string) : string =
        if String.IsNullOrWhiteSpace raw then fallback userText
        else
            let firstLine =
                raw.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryHead
                |> Option.defaultValue raw
            let trimmed =
                PlainText.ofMarkdown firstLine
                |> fun s -> s.Trim('"', '\'', '「', '」', '“', '”', '.', '。', ':', '：')
                |> fun s -> s.Trim()
            let firstSentence =
                let stops = [| '。'; '！'; '？'; '\n'; '.'; '!'; '?'; ';'; '；' |]
                match trimmed.IndexOfAny stops with
                | -1 -> trimmed
                | index when index >= 4 -> trimmed.Substring(0, index)
                | _ -> trimmed
            if String.IsNullOrWhiteSpace firstSentence then fallback userText
            elif firstSentence.Length <= maxLength then firstSentence
            else firstSentence.Substring(0, maxLength) + "…"

    /// 该会话标题是否还是系统生成的占位（只有占位标题才自动改名，
    /// 用户手动命名过的会话绝不覆盖）。
    let isPlaceholder (title: string) : bool =
        if String.IsNullOrWhiteSpace title then true
        else
            let t = title.Trim()
            t = "新会话" || t.StartsWith("会话 ", StringComparison.Ordinal)

    /// 请求一个标题。已有真实标题、关闭开关或消息不足时返回 None。
    let suggest
        (runtime: AgentRuntime)
        (messages: ChatMessage list)
        (ct: CancellationToken)
        : Task<string option> =
        task {
            let transcript =
                messages
                |> List.filter (fun m -> m.Role = ChatRole.User || m.Role = ChatRole.Assistant)
                |> List.truncate 4
                |> List.map (fun m ->
                    let role = if m.Role = ChatRole.User then "用户" else "助手"
                    let text = MessageSerde.textOf m
                    let clipped = if text.Length > 400 then text.Substring(0, 400) else text
                    sprintf "%s：%s" role clipped)
                |> String.concat "\n"
            if String.IsNullOrWhiteSpace transcript then
                return None
            else
                let prompt =
                    [ MessageSerde.textMessage ChatRole.System instruction
                      MessageSerde.textMessage ChatRole.User transcript ]
                let! result = runtime.CompleteOnce(prompt, 48, ct)
                match result with
                | Ok text when not (String.IsNullOrWhiteSpace text) ->
                    return Some(sanitize text (GenerationContext.lastUserText messages))
                | _ -> return None
        }
