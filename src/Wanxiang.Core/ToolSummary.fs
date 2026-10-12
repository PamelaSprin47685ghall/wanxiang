namespace Wanxiang.Core

open System
open System.Text.Json.Nodes

/// 工具调用的人话摘要（第 72 节）。服务端写审批请求、客户端渲染工具调用卡片共用同一份，
/// 两边文案不会漂移。只读 `FunctionCallContent` 的名字与参数，不触碰日志。
module ToolSummary =

    let private stringArg (args: JsonNode) (key: string) : string option =
        match args with
        | :? JsonObject as o ->
            let mutable n: JsonNode = null
            if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = System.Text.Json.JsonValueKind.String then
                let s = n.GetValue<string>()
                if String.IsNullOrWhiteSpace s then None else Some s
            else None
        | _ -> None

    /// 内置工具的函数名。
    let isBuiltin (functionName: string) = functionName.StartsWith("builtin_", StringComparison.Ordinal)

    /// 把函数名（`builtin_time` / `mcp_<server>_<tool>`）与参数变成一句话。
    /// 内置工具各有专属描述；MCP 与未知工具回落到「调用 X（展开看参数）」。
    let describe (functionName: string) (args: JsonNode) : string =
        match functionName with
        | "builtin_time" -> "查看当前时间"
        | "builtin_echo" -> "原样返回一段文字（调试工具）"
        | "builtin_file_read" ->
            match stringArg args "path" with
            | Some p -> sprintf "读取文件 %s" p
            | None -> "读取一个文件（展开看参数）"
        | "builtin_file_list" ->
            match stringArg args "path" with
            | Some p -> sprintf "列出文件夹 %s 里的内容" p
            | None -> "列出一个文件夹里的内容（展开看参数）"
        | name when name.StartsWith("mcp_", StringComparison.Ordinal) ->
            sprintf "调用 %s（展开看参数）" (name.Substring 4)
        | name when String.IsNullOrWhiteSpace name -> "调用工具（展开看参数）"
        | name -> sprintf "调用 %s（展开看参数）" name

    /// 工具稳定标识（`builtin:time`、`mcp:<server>/<tool>`）的显示名。
    let displayName (toolId: string) : string =
        if toolId.StartsWith("builtin:", StringComparison.Ordinal) then
            match toolId.Substring 8 with
            | "time" -> "当前时间"
            | "echo" -> "回显（调试）"
            | "file.read" -> "读取文件"
            | "file.list" -> "列出文件夹"
            | other -> other
        elif toolId.StartsWith("mcp:", StringComparison.Ordinal) then toolId.Substring 4
        else toolId

    /// 函数名（`builtin_echo` / `mcp_<server>_<tool>`）的显示名：工具卡标题给人看，
    /// 不直接露出 `builtin_` 前缀这种协议细节（视觉走查 11 号截图）。
    let displayNameOfFunction (functionName: string) : string =
        if isBuiltin functionName then
            displayName ("builtin:" + functionName.Substring("builtin_".Length).Replace("_", "."))
        elif functionName.StartsWith("mcp_", StringComparison.Ordinal) then functionName.Substring 4
        else functionName

    /// 拒绝时写入工具结果的固定文案（含「用户拒绝」，模型据此理解并继续）。
    let userDeniedText = "用户拒绝了此次工具调用。"
    let userDeniedResult = """{"error":"用户拒绝了此次工具调用。"}"""
