namespace Wanxiang.Agent

open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.Extensions.AI
open Wanxiang.Core

/// ChatMessage 与 JSON 互转（使用 Agent Framework / Microsoft.Extensions.AI 的 JSON 配置，决策 18/19）。
module MessageSerde =

    let options = AIJsonUtilities.DefaultOptions

    let serialize (msg: ChatMessage) : string =
        JsonSerializer.Serialize(msg, options)

    let toJsonNode (msg: ChatMessage) : JsonNode =
        JsonNode.Parse(serialize msg)

    let deserialize (json: string) : ChatMessage option =
        try
            Some(JsonSerializer.Deserialize<ChatMessage>(json, options))
        with _ ->
            None

    let fromJsonNode (node: JsonNode) : ChatMessage option =
        try
            Some(node.Deserialize<ChatMessage>(options))
        with _ ->
            None

    /// 从消息内容中提取文本（用于会话摘要等展示用途）。
    let textOf (msg: ChatMessage) : string =
        msg.Contents
        |> Seq.choose (fun c ->
            match c with
            | :? TextContent as t -> Some t.Text
            | _ -> None)
        |> String.concat ""

    /// 消息角色名（user / assistant / tool / system）。
    let roleName (msg: ChatMessage) : string =
        if msg.Role = ChatRole.User then "user"
        elif msg.Role = ChatRole.Assistant then "assistant"
        elif msg.Role = ChatRole.System then "system"
        elif msg.Role = ChatRole.Tool then "tool"
        else msg.Role.Value

    /// 是否包含工具调用内容。
    let hasToolCall (msg: ChatMessage) : bool =
        msg.Contents |> Seq.exists (fun c -> c :? FunctionCallContent)

    let toolCalls (msg: ChatMessage) : FunctionCallContent list =
        msg.Contents
        |> Seq.choose (fun c ->
            match c with
            | :? FunctionCallContent as f -> Some f
            | _ -> None)
        |> List.ofSeq

    /// 构造一条工具结果消息（role=tool，含 FunctionResultContent）。
    let toolResultMessage (call: FunctionCallContent) (resultJson: string) : ChatMessage =
        let msg = ChatMessage()
        msg.Role <- ChatRole.Tool
        let result = FunctionResultContent(call.CallId, resultJson)
        msg.Contents.Add result
        msg
