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

    /// 宽松回退：客户端手写 JSON（无 $type 鉴别器的 {role, contents:[{text}]}）→ ChatMessage。
    let private parseLoose (node: JsonNode) : ChatMessage option =
        try
            match node with
            | :? JsonObject as o ->
                let getStr (k: string) =
                    let mutable n: JsonNode = null
                    if o.TryGetPropertyValue(k, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then Some(n.GetValue<string>()) else None
                let msg = ChatMessage()
                msg.Role <-
                    match getStr "role" with
                    | Some "assistant" -> ChatRole.Assistant
                    | Some "system" -> ChatRole.System
                    | Some "tool" -> ChatRole.Tool
                    | _ -> ChatRole.User
                let mutable contentsNode: JsonNode = null
                if o.TryGetPropertyValue("contents", &contentsNode) && not (isNull contentsNode) && contentsNode.GetValueKind() = JsonValueKind.Array then
                    for item in contentsNode.AsArray() do
                        match item with
                        | :? JsonObject as c ->
                            let mutable t: JsonNode = null
                            if c.TryGetPropertyValue("text", &t) && not (isNull t) && t.GetValueKind() = JsonValueKind.String then
                                msg.Contents.Add(TextContent(t.GetValue<string>()))
                        | _ -> ()
                if msg.Contents.Count = 0 then None else Some msg
            | _ -> None
        with _ -> None

    /// 检测未带 $type 鉴别器的 contents（客户端手写 JSON）：需要走宽松回退。
    let private needsLoose (node: JsonNode) : bool =
        match node with
        | :? JsonObject as o ->
            let mutable c: JsonNode = null
            if o.TryGetPropertyValue("contents", &c) && not (isNull c) && c.GetValueKind() = JsonValueKind.Array then
                c.AsArray()
                |> Seq.exists (fun item ->
                    match item with
                    | :? JsonObject as io ->
                        let mutable t: JsonNode = null
                        not (io.TryGetPropertyValue("$type", &t))
                    | _ -> false)
            else false
        | _ -> false

    let fromJsonNode (node: JsonNode) : ChatMessage option =
        if needsLoose node then
            parseLoose node
        else
            try
                let msg = node.Deserialize<ChatMessage>(options)
                if isNull msg then parseLoose node else Some msg
            with _ ->
                parseLoose node

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
