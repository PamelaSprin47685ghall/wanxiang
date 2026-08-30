namespace Wanxiang.UI

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

/// 消息里的附件引用（客户端写入 contents 的 `{"type":"attachment",...}` 项）。
type AttachmentRef = {
    sha256: string
    size: int64
    mediaType: string
    fileName: string
}

module AttachmentRef =

    let isImage (r: AttachmentRef) =
        let m = if isNull r.mediaType then "" else r.mediaType.ToLowerInvariant()
        m.StartsWith("image/", StringComparison.Ordinal)

    let formatSize (n: int64) =
        if n < 1024L then sprintf "%d B" n
        elif n < 1024L * 1024L then sprintf "%.1f KiB" (float n / 1024.0)
        else sprintf "%.1f MiB" (float n / (1024.0 * 1024.0))

/// 一次工具调用及其结果。调用与结果分属两条消息，
/// 渲染时需要合并成一张卡片，因此用 callId 关联。
type ToolCallView = {
    callId: string
    name: string
    argumentsJson: string
    /// 结果尚未返回时为 None（渲染成「执行中」）
    result: string option
}

/// 一条消息的渲染视图。
type MessageView = {
    /// user / assistant / tool / system
    role: string
    text: string
    /// 思维链单独提取，折叠展示
    reasoning: string
    toolCalls: ToolCallView list
    /// 本条消息里的工具结果（callId → 结果文本）
    toolResults: (string * string) list
    attachments: AttachmentRef list
    commitId: uint64 option
    /// 落盘时刻。流式临时消息为 None。
    committedAt: DateTimeOffset option
}

module MessageView =

    let empty =
        { role = "assistant"
          text = ""
          reasoning = ""
          toolCalls = []
          toolResults = []
          attachments = []
          commitId = None
          committedAt = None }

    let private str (o: JsonObject) (key: string) : string option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then
            Some(n.GetValue<string>())
        else
            None

    let private int64Of (o: JsonObject) (key: string) : int64 option =
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

    let private typeTag (o: JsonObject) : string =
        match str o "$type" with
        | Some t -> t
        | None -> str o "type" |> Option.defaultValue ""

    /// 工具调用参数：Agent Framework 用 `arguments` 存字典，
    /// 直接序列化成紧凑 JSON 供界面展示。
    let private argumentsOf (o: JsonObject) : string =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue("arguments", &n) && not (isNull n) then n.ToJsonString()
        else ""

    let private resultOf (o: JsonObject) : string =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue("result", &n) && not (isNull n) then
            if n.GetValueKind() = JsonValueKind.String then n.GetValue<string>() else n.ToJsonString()
        else
            ""

    /// 递归走 contents，把各类内容分门别类收集。
    let rec private walk
        (node: JsonNode)
        (text: StringBuilder)
        (reasoning: StringBuilder)
        (calls: ResizeArray<ToolCallView>)
        (results: ResizeArray<string * string>)
        (attachments: ResizeArray<AttachmentRef>)
        =
        match node with
        | null -> ()
        | :? JsonArray as arr -> for item in arr do walk item text reasoning calls results attachments
        | :? JsonObject as o ->
            match typeTag o with
            | "reasoning" ->
                match str o "text" with
                | Some t -> reasoning.Append t |> ignore
                | None -> ()
            | "functionCall" ->
                calls.Add
                    { callId = str o "callId" |> Option.defaultValue ""
                      name = str o "name" |> Option.defaultValue "tool"
                      argumentsJson = argumentsOf o
                      result = None }
            | "functionResult" ->
                results.Add(str o "callId" |> Option.defaultValue "", resultOf o)
            | "attachment" ->
                match str o "sha256" with
                | Some sha when not (String.IsNullOrWhiteSpace sha) ->
                    attachments.Add
                        { sha256 = sha
                          size = int64Of o "size" |> Option.defaultValue 0L
                          mediaType = str o "mediaType" |> Option.defaultValue "application/octet-stream"
                          fileName = str o "fileName" |> Option.defaultValue sha }
                | _ -> ()
            | _ ->
                match str o "text" with
                | Some t -> text.Append t |> ignore
                | None -> ()
                let mutable contents: JsonNode = null
                if o.TryGetPropertyValue("contents", &contents) && not (isNull contents) then
                    walk contents text reasoning calls results attachments
        | _ -> ()

    let ofJson (node: JsonNode) : MessageView =
        let text = StringBuilder()
        let reasoning = StringBuilder()
        let calls = ResizeArray<ToolCallView>()
        let results = ResizeArray<string * string>()
        let attachments = ResizeArray<AttachmentRef>()
        walk node text reasoning calls results attachments
        let role =
            match node with
            | :? JsonObject as o -> str o "role" |> Option.defaultValue "assistant"
            | _ -> "assistant"
        { role = role
          text = text.ToString()
          reasoning = reasoning.ToString()
          toolCalls = List.ofSeq calls
          toolResults = List.ofSeq results
          attachments = List.ofSeq attachments
          commitId = None
          committedAt = None }

    /// 从快照条目（`{commitId, payload}`）解析。
    let ofSnapshotItem (item: JsonNode) : MessageView =
        match item with
        | :? JsonObject as o ->
            let payload =
                let mutable p: JsonNode = null
                if o.TryGetPropertyValue("payload", &p) && not (isNull p) then p else item
            let commitId =
                let mutable c: JsonNode = null
                if o.TryGetPropertyValue("commitId", &c) && not (isNull c) then
                    match c with
                    | :? JsonValue as v ->
                        match v.TryGetValue<uint64>() with
                        | true, id -> Some id
                        | _ -> None
                    | _ -> None
                else
                    None
            let committedAt =
                match str o "committedAt" with
                | Some raw ->
                    match DateTimeOffset.TryParse(raw, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind) with
                    | true, value -> Some value
                    | _ -> None
                | None -> None
            { ofJson payload with commitId = commitId; committedAt = committedAt }
        | _ -> ofJson item

    let isUser (m: MessageView) = m.role = "user"
    let isTool (m: MessageView) = m.role = "tool"

    /// 这条消息有没有可展示的内容。纯工具结果消息由前一条助手消息的卡片承载，
    /// 因此单独渲染时应被跳过。
    let hasVisibleBody (m: MessageView) =
        not (String.IsNullOrWhiteSpace m.text)
        || not (String.IsNullOrWhiteSpace m.reasoning)
        || not (List.isEmpty m.toolCalls)
        || not (List.isEmpty m.attachments)

    /// 把工具结果回填到发起调用的消息上，得到可直接渲染的序列。
    /// 结果消息本身从序列中移除。
    let mergeToolResults (messages: MessageView list) : MessageView list =
        let resultMap =
            messages
            |> List.collect (fun m -> m.toolResults)
            |> List.filter (fun (id, _) -> not (String.IsNullOrWhiteSpace id))
            |> Map.ofList
        messages
        |> List.choose (fun m ->
            if isTool m && List.isEmpty m.toolCalls && not (hasVisibleBody m) then None
            else
                let filled =
                    m.toolCalls
                    |> List.map (fun call ->
                        match resultMap.TryFind call.callId with
                        | Some result -> { call with result = Some result }
                        | None -> call)
                Some { m with toolCalls = filled })
