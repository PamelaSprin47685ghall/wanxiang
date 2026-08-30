namespace Wanxiang.Agent

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Wanxiang.Config

/// Anthropic Messages API 的请求构造。
///
/// 与 OpenAI 兼容层的三处实质差异，正是必须原生实现的理由：
/// system 是**顶层字段**而不是一条消息；`max_tokens` 是**必填**；
/// 工具调用与工具结果是 content block（`tool_use` / `tool_result`）而不是独立字段。
module AnthropicRequest =

    [<Literal>]
    let ApiVersion = "2023-06-01"

    /// Anthropic 没有 max_tokens 就直接 400。给一个够用的默认值而不是让请求失败。
    [<Literal>]
    let DefaultMaxTokens = 4096

    let private textBlock (text: string) =
        let o = JsonObject()
        o["type"] <- JsonValue.Create "text"
        o["text"] <- JsonValue.Create text
        o :> JsonNode

    let private imageBlock (media: string) (base64: string) =
        let source = JsonObject()
        source["type"] <- JsonValue.Create "base64"
        source["media_type"] <- JsonValue.Create media
        source["data"] <- JsonValue.Create base64
        let o = JsonObject()
        o["type"] <- JsonValue.Create "image"
        o["source"] <- source
        o :> JsonNode

    /// PDF 走 document 块。Anthropic 的 Messages API 只认 PDF 这一种文档，
    /// 其余类型在上游（AttachmentContent）就已按能力过滤掉了。
    let private documentBlock (media: string) (base64: string) =
        let source = JsonObject()
        source["type"] <- JsonValue.Create "base64"
        source["media_type"] <- JsonValue.Create media
        source["data"] <- JsonValue.Create base64
        let o = JsonObject()
        o["type"] <- JsonValue.Create "document"
        o["source"] <- source
        o :> JsonNode

    let private toolUseBlock (callId: string) (name: string) (arguments: IDictionary<string, obj>) =
        let input = JsonObject()
        if not (isNull arguments) then
            for KeyValue(key, value) in arguments do
                input[key] <-
                    match value with
                    | null -> null
                    | :? JsonNode as node -> node.DeepClone()
                    | other -> JsonValue.Create(string other) :> JsonNode
        let o = JsonObject()
        o["type"] <- JsonValue.Create "tool_use"
        o["id"] <- JsonValue.Create callId
        o["name"] <- JsonValue.Create name
        o["input"] <- input
        o :> JsonNode

    let private toolResultBlock (callId: string) (result: obj) =
        let o = JsonObject()
        o["type"] <- JsonValue.Create "tool_result"
        o["tool_use_id"] <- JsonValue.Create callId
        o["content"] <-
            JsonValue.Create(
                match result with
                | null -> ""
                | :? string as s -> s
                | other -> string other)
        o :> JsonNode

    /// 一条消息拆成若干 content block。返回 None 表示这条消息不进 messages
    /// （system 已被提到顶层）。
    let private blocksOf (message: ChatMessage) : JsonNode list =
        [ for content in message.Contents do
            match content with
            | :? TextContent as text when not (String.IsNullOrEmpty text.Text) -> textBlock text.Text
            | :? DataContent as data when data.HasTopLevelMediaType "image" ->
                imageBlock data.MediaType (Convert.ToBase64String(data.Data.ToArray()))
            | :? DataContent as data when data.MediaType = "application/pdf" ->
                documentBlock data.MediaType (Convert.ToBase64String(data.Data.ToArray()))
            | :? FunctionCallContent as call -> toolUseBlock call.CallId call.Name call.Arguments
            | :? FunctionResultContent as result -> toolResultBlock result.CallId result.Result
            | _ -> () ]

    let private roleOf (message: ChatMessage) =
        if message.Role = ChatRole.Assistant then "assistant" else "user"

    /// 相邻同角色消息必须合并：Anthropic 要求 user/assistant 交替，
    /// 连续两条同角色会被判 400（工具结果紧跟用户消息时很容易撞上）。
    let private mergeAdjacent (items: (string * JsonNode list) list) =
        items
        |> List.fold
            (fun acc (role, blocks) ->
                match acc with
                | (previousRole, previousBlocks) :: rest when previousRole = role ->
                    (role, previousBlocks @ blocks) :: rest
                | _ -> (role, blocks) :: acc)
            []
        |> List.rev

    let private toolsOf (options: ChatOptions) =
        match options with
        | null -> None
        | _ when isNull options.Tools || options.Tools.Count = 0 -> None
        | _ ->
            let array = JsonArray()
            for tool in options.Tools do
                match tool with
                | :? AIFunction as fn ->
                    let entry = JsonObject()
                    entry["name"] <- JsonValue.Create fn.Name
                    if not (String.IsNullOrWhiteSpace fn.Description) then
                        entry["description"] <- JsonValue.Create fn.Description
                    match ProviderSse.schemaOf fn.JsonSchema with
                    | Some schema -> entry["input_schema"] <- schema
                    | None -> ()
                    array.Add entry
                | _ -> ()
            if array.Count = 0 then None else Some(array :> JsonNode)

    /// system 指令来自两处：ChatOptions.Instructions 与 System 角色的消息。
    /// 两者都要，且顺序为「指令在前」。
    let private systemOf (messages: ChatMessage seq) (options: ChatOptions) =
        let fromOptions =
            match options with
            | null -> []
            | _ when String.IsNullOrWhiteSpace options.Instructions -> []
            | _ -> [ options.Instructions ]
        let fromMessages =
            [ for message in messages do
                if message.Role = ChatRole.System then
                    let text = message.Text
                    if not (String.IsNullOrWhiteSpace text) then text ]
        match fromOptions @ fromMessages with
        | [] -> None
        | parts -> Some(String.Join("\n\n", parts))

    /// system 的数组形态。要打 `cache_control` 就必须用数组——
    /// 字符串形态挂不上缓存标记。
    let private systemBlocks (text: string) (cache: bool) =
        let block = JsonObject()
        block["type"] <- JsonValue.Create "text"
        block["text"] <- JsonValue.Create text
        if cache then
            let control = JsonObject()
            control["type"] <- JsonValue.Create "ephemeral"
            block["cache_control"] <- control
        let array = JsonArray()
        array.Add block
        array :> JsonNode

    /// 思维链。Anthropic 要求 `max_tokens > budget_tokens`，
    /// 否则直接 400；这里返回需要的最小 max_tokens 一并交给调用方。
    let private thinkingBlock (budget: int) =
        let o = JsonObject()
        o["type"] <- JsonValue.Create "enabled"
        o["budget_tokens"] <- JsonValue.Create budget
        o :> JsonNode

    let build
        (model: string)
        (messages: ChatMessage seq)
        (options: ChatOptions)
        (stream: bool)
        (thinkingBudget: int)
        (promptCaching: bool)
        : JsonObject =
        let body = JsonObject()
        body["model"] <- JsonValue.Create model
        body["stream"] <- JsonValue.Create stream
        let requestedMaxTokens =
            match options with
            | null -> DefaultMaxTokens
            | _ when options.MaxOutputTokens.HasValue -> options.MaxOutputTokens.Value
            | _ -> DefaultMaxTokens
        // 思维链预算必须小于 max_tokens。用户把两者配成冲突时抬高 max_tokens，
        // 而不是让请求以一个看不懂的 400 失败。
        let maxTokens =
            if thinkingBudget > 0 && requestedMaxTokens <= thinkingBudget then thinkingBudget + DefaultMaxTokens
            else requestedMaxTokens
        body["max_tokens"] <- JsonValue.Create maxTokens
        if thinkingBudget > 0 then body["thinking"] <- thinkingBlock thinkingBudget
        match systemOf messages options with
        | Some system -> body["system"] <- systemBlocks system promptCaching
        | None -> ()
        if not (isNull options) then
            if options.Temperature.HasValue then
                body["temperature"] <- JsonValue.Create(float options.Temperature.Value)
            if options.TopP.HasValue then body["top_p"] <- JsonValue.Create(float options.TopP.Value)
            match toolsOf options with
            | Some tools -> body["tools"] <- tools
            | None -> ()
            // 会话 extraJson 原样透传到顶层——Anthropic 的 thinking / metadata /
            // service_tier 都在顶层。不透传等于把会话里配的模型专属参数静默丢掉。
            if not (isNull options.AdditionalProperties) then
                for KeyValue(key, value) in options.AdditionalProperties do
                    match value with
                    | :? JsonNode as node -> body[key] <- node.DeepClone()
                    | null -> ()
                    | other -> body[key] <- JsonValue.Create(string other)

        let conversation =
            messages
            |> Seq.filter (fun m -> m.Role <> ChatRole.System)
            |> Seq.map (fun m -> roleOf m, blocksOf m)
            |> Seq.filter (fun (_, blocks) -> not (List.isEmpty blocks))
            |> List.ofSeq
            |> mergeAdjacent

        let array = JsonArray()
        for (role, blocks) in conversation do
            let entry = JsonObject()
            entry["role"] <- JsonValue.Create role
            let content = JsonArray()
            for block in blocks do content.Add block
            entry["content"] <- content
            array.Add entry
        body["messages"] <- array
        body
