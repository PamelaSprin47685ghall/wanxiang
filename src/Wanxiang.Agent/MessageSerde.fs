namespace Wanxiang.Agent

open System
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.Extensions.AI
open Wanxiang.Core

/// 消息里的附件引用。客户端手写的 `{"type":"attachment", ...}` 内容项，
/// NDJSON 原样保存；送给 Provider 前由编排层解析成真实内容（图片/文本）。
type AttachmentReference = {
    sha256: string
    mediaType: string
    fileName: string
    size: int64
}

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

    let private tryStr (o: JsonObject) (key: string) : string option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then
            Some(n.GetValue<string>())
        else
            None

    let private tryInt64 (o: JsonObject) (key: string) : int64 option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
            match n with
            | :? JsonValue as v ->
                match v.TryGetValue<int64>() with
                | true, i -> Some i
                | _ -> None
            | _ -> None
        else
            None

    let private contentsArray (node: JsonNode) : JsonArray option =
        match node with
        | :? JsonObject as o ->
            let mutable c: JsonNode = null
            if o.TryGetPropertyValue("contents", &c) && not (isNull c) && c.GetValueKind() = JsonValueKind.Array then
                Some(c.AsArray())
            else
                None
        | _ -> None

    let private isAttachmentItem (o: JsonObject) : bool =
        match tryStr o "type" with
        | Some "attachment" -> true
        | _ -> false

    /// 从原始 payload 里抽取附件引用（保持出现顺序）。
    let attachmentRefs (node: JsonNode) : AttachmentReference list =
        match contentsArray node with
        | None -> []
        | Some arr ->
            [ for item in arr do
                  match item with
                  | :? JsonObject as o when isAttachmentItem o ->
                      match tryStr o "sha256" with
                      | Some sha when not (String.IsNullOrWhiteSpace sha) ->
                          { sha256 = sha.ToLowerInvariant()
                            mediaType = tryStr o "mediaType" |> Option.defaultValue "application/octet-stream"
                            fileName = tryStr o "fileName" |> Option.defaultValue sha
                            size = tryInt64 o "size" |> Option.defaultValue 0L }
                      | _ -> ()
                  | _ -> () ]

    /// 逐项解析 contents。
    ///
    /// 历史上这里是「只要有一项缺 `$type` 就整条消息只取 text」，
    /// 结果混合消息（附件 + 工具调用 + 文本）会静默丢内容。现在逐项判断：
    /// 带 `$type` 的项走严格反序列化，手写 `{text}` 项转 TextContent，
    /// 附件项交给编排层单独解析（此处跳过，避免变成一段无意义的 JSON 文本）。
    let private parseContentsItemwise (arr: JsonArray) (msg: ChatMessage) : unit =
        for item in arr do
            match item with
            | :? JsonObject as o ->
                let mutable typeTag: JsonNode = null
                if o.TryGetPropertyValue("$type", &typeTag) then
                    try
                        let content = o.Deserialize<AIContent>(options)
                        if not (isNull content) then msg.Contents.Add content
                    with _ -> ()
                elif isAttachmentItem o then
                    () // 由 AttachmentResolver 转成 DataContent / TextContent
                else
                    match tryStr o "text" with
                    | Some text when not (String.IsNullOrEmpty text) -> msg.Contents.Add(TextContent text)
                    | _ -> ()
            | _ -> ()

    let private roleOf (raw: string option) : ChatRole =
        match raw with
        | Some "assistant" -> ChatRole.Assistant
        | Some "system" -> ChatRole.System
        | Some "tool" -> ChatRole.Tool
        | _ -> ChatRole.User

    /// 宽松回退：客户端手写 JSON（无 $type 鉴别器）→ ChatMessage。
    let private parseLoose (node: JsonNode) (allowEmpty: bool) : ChatMessage option =
        try
            match node with
            | :? JsonObject as o ->
                let msg = ChatMessage()
                msg.Role <- roleOf (tryStr o "role")
                match contentsArray node with
                | Some arr -> parseContentsItemwise arr msg
                | None -> ()
                if msg.Contents.Count = 0 && not allowEmpty then None else Some msg
            | _ -> None
        with _ -> None

    /// 是否存在缺 $type 的 contents 项（客户端手写 JSON）。
    let private hasUntagged (node: JsonNode) : bool =
        match contentsArray node with
        | None -> false
        | Some arr ->
            arr
            |> Seq.exists (fun item ->
                match item with
                | :? JsonObject as io ->
                    let mutable t: JsonNode = null
                    not (io.TryGetPropertyValue("$type", &t))
                | _ -> false)

    /// 解析一条持久化消息。
    ///
    /// `allowEmptyContents`：仅带附件的用户消息解析后 contents 为空，
    /// 但它并不是坏数据——附件随后由编排层补上，因此允许空。
    let fromJsonNodeWith (allowEmptyContents: bool) (node: JsonNode) : ChatMessage option =
        if hasUntagged node then
            parseLoose node allowEmptyContents
        else
            try
                let msg = node.Deserialize<ChatMessage>(options)
                if isNull msg then parseLoose node allowEmptyContents
                elif msg.Contents.Count = 0 && not allowEmptyContents then parseLoose node allowEmptyContents
                else Some msg
            with _ ->
                parseLoose node allowEmptyContents

    let fromJsonNode (node: JsonNode) : ChatMessage option = fromJsonNodeWith false node

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

    /// 构造一条纯文本消息。
    let textMessage (role: ChatRole) (text: string) : ChatMessage =
        let msg = ChatMessage()
        msg.Role <- role
        msg.Contents.Add(TextContent text)
        msg
