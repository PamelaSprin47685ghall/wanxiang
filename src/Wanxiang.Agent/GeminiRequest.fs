namespace Wanxiang.Agent

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open Microsoft.Extensions.AI

/// Gemini generateContent 的请求构造。
///
/// 与 OpenAI 兼容层的实质差异：角色叫 `user`/`model` 而非 `assistant`；
/// system 走顶层 `systemInstruction`；生成参数收在 `generationConfig` 里；
/// 工具调用是 part（`functionCall` / `functionResponse`）而不是独立字段。
module GeminiRequest =

    /// Gemini 的 functionCall **不带调用 id**，而编排层要靠 id 把结果配回请求。
    /// 因此把 id 造成 `name@序号` 这种自描述形式：回传 functionResponse 时
    /// 只需从 id 里把 name 拆出来，不必额外维护一张映射表。
    [<Literal>]
    let CallIdSeparator = '@'

    let makeCallId (name: string) (index: int) = $"{name}{CallIdSeparator}{index}"

    let nameOfCallId (callId: string) =
        if String.IsNullOrEmpty callId then ""
        else
            match callId.LastIndexOf CallIdSeparator with
            | -1 -> callId
            | at -> callId.Substring(0, at)

    let private textPart (text: string) =
        let o = JsonObject()
        o["text"] <- JsonValue.Create text
        o :> JsonNode

    let private inlineDataPart (media: string) (base64: string) =
        let data = JsonObject()
        data["mimeType"] <- JsonValue.Create media
        data["data"] <- JsonValue.Create base64
        let o = JsonObject()
        o["inlineData"] <- data
        o :> JsonNode

    let private functionCallPart (name: string) (arguments: IDictionary<string, obj>) =
        let args = JsonObject()
        if not (isNull arguments) then
            for KeyValue(key, value) in arguments do
                args[key] <-
                    match value with
                    | null -> null
                    | :? JsonNode as node -> node.DeepClone()
                    | other -> JsonValue.Create(string other) :> JsonNode
        let call = JsonObject()
        call["name"] <- JsonValue.Create name
        call["args"] <- args
        let o = JsonObject()
        o["functionCall"] <- call
        o :> JsonNode

    let private functionResponsePart (callId: string) (result: obj) =
        // response 必须是对象；标量结果包一层，否则 Gemini 直接 400
        let payload = JsonObject()
        payload["result"] <-
            JsonValue.Create(
                match result with
                | null -> ""
                | :? string as s -> s
                | other -> string other)
        let body = JsonObject()
        body["name"] <- JsonValue.Create(nameOfCallId callId)
        body["response"] <- payload
        let o = JsonObject()
        o["functionResponse"] <- body
        o :> JsonNode

    let private partsOf (message: ChatMessage) : JsonNode list =
        [ for content in message.Contents do
            match content with
            | :? TextContent as text when not (String.IsNullOrEmpty text.Text) -> textPart text.Text
            // inlineData 通吃图片、PDF、音频、视频；能不能发已在
            // AttachmentContent 按传输能力过滤过，这里不必再判类型
            | :? DataContent as data -> inlineDataPart data.MediaType (Convert.ToBase64String(data.Data.ToArray()))
            | :? FunctionCallContent as call -> functionCallPart call.Name call.Arguments
            | :? FunctionResultContent as result -> functionResponsePart result.CallId result.Result
            | _ -> () ]

    let private roleOf (message: ChatMessage) =
        if message.Role = ChatRole.Assistant then "model" else "user"

    /// 相邻同角色合并：Gemini 对交替没有硬要求，但连续同角色会让模型
    /// 把两轮当成一轮，工具结果紧跟用户消息时语义会串。
    let private mergeAdjacent (items: (string * JsonNode list) list) =
        items
        |> List.fold
            (fun acc (role, parts) ->
                match acc with
                | (previousRole, previousParts) :: rest when previousRole = role ->
                    (role, previousParts @ parts) :: rest
                | _ -> (role, parts) :: acc)
            []
        |> List.rev

    let private toolsOf (options: ChatOptions) =
        match options with
        | null -> None
        | _ when isNull options.Tools || options.Tools.Count = 0 -> None
        | _ ->
            let declarations = JsonArray()
            for tool in options.Tools do
                match tool with
                | :? AIFunction as fn ->
                    let entry = JsonObject()
                    entry["name"] <- JsonValue.Create fn.Name
                    if not (String.IsNullOrWhiteSpace fn.Description) then
                        entry["description"] <- JsonValue.Create fn.Description
                    match ProviderSse.schemaOf fn.JsonSchema with
                    | Some schema -> entry["parameters"] <- schema
                    | None -> ()
                    declarations.Add entry
                | _ -> ()
            if declarations.Count = 0 then None
            else
                let holder = JsonObject()
                holder["functionDeclarations"] <- declarations
                let array = JsonArray()
                array.Add holder
                Some(array :> JsonNode)

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

    /// 思维链。Gemini 把它放在 generationConfig 里，且要显式要求回传思考内容，
    /// 否则只计费不给看。
    let private thinkingConfig (budget: int) =
        let o = JsonObject()
        o["thinkingBudget"] <- JsonValue.Create budget
        o["includeThoughts"] <- JsonValue.Create true
        o :> JsonNode

    let build (messages: ChatMessage seq) (options: ChatOptions) (thinkingBudget: int) : JsonObject =
        let body = JsonObject()

        match systemOf messages options with
        | Some system ->
            let parts = JsonArray()
            parts.Add(textPart system)
            let instruction = JsonObject()
            instruction["parts"] <- parts
            body["systemInstruction"] <- instruction
        | None -> ()

        let conversation =
            messages
            |> Seq.filter (fun m -> m.Role <> ChatRole.System)
            |> Seq.map (fun m -> roleOf m, partsOf m)
            |> Seq.filter (fun (_, parts) -> not (List.isEmpty parts))
            |> List.ofSeq
            |> mergeAdjacent

        let contents = JsonArray()
        for (role, parts) in conversation do
            let entry = JsonObject()
            entry["role"] <- JsonValue.Create role
            let array = JsonArray()
            for part in parts do array.Add part
            entry["parts"] <- array
            contents.Add entry
        body["contents"] <- contents

        if not (isNull options) then
            let config = JsonObject()
            if options.Temperature.HasValue then
                config["temperature"] <- JsonValue.Create(float options.Temperature.Value)
            if options.TopP.HasValue then config["topP"] <- JsonValue.Create(float options.TopP.Value)
            if options.MaxOutputTokens.HasValue then
                config["maxOutputTokens"] <- JsonValue.Create options.MaxOutputTokens.Value
            if thinkingBudget > 0 then config["thinkingConfig"] <- thinkingConfig thinkingBudget
            // 会话 extraJson 合并进 generationConfig——Gemini 的生成参数
            // （thinkingConfig / responseMimeType / safetySettings 之外的项）都在这里。
            // 不合并等于把会话里配的模型专属参数静默丢掉。
            if not (isNull options.AdditionalProperties) then
                for KeyValue(key, value) in options.AdditionalProperties do
                    match value with
                    | :? JsonNode as node -> config[key] <- node.DeepClone()
                    | null -> ()
                    | other -> config[key] <- JsonValue.Create(string other)
            if config.Count > 0 then body["generationConfig"] <- config
            match toolsOf options with
            | Some tools -> body["tools"] <- tools
            | None -> ()

        body
