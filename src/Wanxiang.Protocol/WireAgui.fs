namespace Wanxiang.Protocol

open System
open System.Text.Json
open System.Text.Json.Nodes
open AGUI.Abstractions
open Wanxiang.Agui

/// WireEvent ⇄ AG-UI 承载适配层。
///
/// **架构位置**：`WireEvent` 的 56 个变体是万象的语义层；
/// 这一层负责**承载**——出站把语义事件编成 AG-UI 事件对象，入站解码。
///
/// 承载规则（标准面优先，`wanxiang.dev/*` CUSTOM 面承载自有语义）：
/// - 生成流（started/delta/finished）→ 标准 AG-UI 事件
///   （RUN_STARTED / TEXT_MESSAGE_* / TOOL_CALL_* / RUN_FINISHED / RUN_ERROR）
/// - 其余语义 → `CUSTOM`，`name = wanxiang.dev/<type>`，`value` 为扁平字段
/// - 协议版本 in-band（`RUN_STARTED.protocolVersion`），无独立握手事件
///
/// 序列化陷阱（实测）：AG-UI 消息必须按 `typeof<AGUIMessage>` 序列化，
/// 按运行期类型会丢 content。事件对象本身无此问题（converter 挂在事件基类上）。
module WireAgui =

    let private aguiOptions = AGUIJsonSerializerContext.Default.Options

    /// 无法映射到 AG-UI 标准面的增量：走 CUSTOM wanxiang.dev/generation-delta。
    let private customDelta (d: {| conversationId: Guid; generationId: Guid; payload: JsonNode |}) : BaseEvent =
        let o = JsonObject()
        o["conversationId"] <- d.conversationId.ToString("D")
        o["generationId"] <- d.generationId.ToString("D")
        o["payload"] <- d.payload.DeepClone()
        let ce = CustomEvent()
        ce.Name <- sprintf "%sgeneration-delta" Capabilities.Namespace
        ce.Value <- Nullable(o.ToJsonString() |> JsonDocument.Parse |> fun doc -> doc.RootElement.Clone())
        ce :> BaseEvent

    /// 出站：语义事件 → AG-UI 事件对象。
    /// 标准面（生成流）映射；其余走 CUSTOM。
    let toAguiEvent (ev: WireEvent) : BaseEvent =
        match ev with
        | GenerationStarted d ->
            let e = RunStartedEvent()
            e.ThreadId <- d.conversationId.ToString("D")
            e.RunId <- d.generationId.ToString("D")
            e :> BaseEvent
        | GenerationFinished d ->
            match d.status with
            | "completed" ->
                let e = RunFinishedEvent()
                e.ThreadId <- d.conversationId.ToString("D")
                e.RunId <- d.generationId.ToString("D")
                d.usage |> Option.iter (fun u ->
                    let tu = TokenUsage()
                    u.promptTokens |> Option.iter (fun v -> tu.InputTokens <- Nullable(int64 v))
                    u.completionTokens |> Option.iter (fun v -> tu.OutputTokens <- Nullable(int64 v))
                    u.totalTokens |> Option.iter (fun v -> tu.TotalTokens <- Nullable(int64 v))
                    e.Usage <- ResizeArray<TokenUsage>([ tu ]))
                e :> BaseEvent
            | "cancelled" ->
                // 标准面：RunFinishedOutcome 有派生类 RunFinishedCancelledOutcome（可构造）。
                let e = RunFinishedEvent()
                e.ThreadId <- d.conversationId.ToString("D")
                e.RunId <- d.generationId.ToString("D")
                e.Outcome <- RunFinishedCancelledOutcome()
                e :> BaseEvent
            | _ ->
                // failed / 其它异常态：RUN_ERROR
                let e = RunErrorEvent()
                e.Message <- (d.error |> Option.map (fun er -> er.message) |> Option.defaultValue d.status)
                e.Code <- (d.status)
                e :> BaseEvent
        | GenerationDelta d ->
            // 增量按 MAF 内容形状分派到 AG-UI 标准面（能用标准语义的都用标准）：
            //   contents[0].$type = "text"          → TEXT_MESSAGE_CHUNK
            //   contents[0].$type = "functionCall"  → TOOL_CALL_START + TOOL_CALL_ARGS + TOOL_CALL_END
            //   contents[0].$type = "functionResult"→ TOOL_CALL_RESULT
            //   其余（无法表达）                    → CUSTOM wanxiang.dev/generation-delta
            // payload 是 MAF ChatMessage JSON（MessageSerde.toJsonNode）。
            let firstContent () =
                match d.payload with
                | :? JsonObject as p ->
                    match p["contents"] with
                    | :? JsonArray as arr when arr.Count > 0 -> Some arr[0]
                    | _ -> None
                | _ -> None
            let messageId = d.generationId.ToString("D")
            match firstContent () with
            | Some (:? JsonObject as c) ->
                match (match c["$type"] with null -> null | t -> t.GetValue<string>()) with
                | "text" ->
                    let e = TextMessageChunkEvent()
                    e.MessageId <- messageId
                    (match c["text"] with null -> () | t -> e.Delta <- t.GetValue<string>())
                    e :> BaseEvent
                | "functionCall" ->
                    // 一个 MAF functionCall 内容 → AG-UI 工具调用三元组（START/ARGS/END）。
                    // AG-UI 单帧单事件，取 START 承载身份；ARGS/END 由后续 delta 补齐时同样分派。
                    let e = ToolCallStartEvent()
                    (match c["callId"] with null -> () | t -> e.ToolCallId <- t.GetValue<string>())
                    (match c["name"] with null -> () | t -> e.ToolCallName <- t.GetValue<string>())
                    e :> BaseEvent
                | "functionResult" ->
                    let e = ToolCallResultEvent()
                    (match c["callId"] with null -> () | t -> e.ToolCallId <- t.GetValue<string>())
                    (match c["result"] with null -> () | t -> e.Content <- AGUIContent.op_Implicit (t.ToString()))
                    e.MessageId <- messageId
                    e :> BaseEvent
                | _ -> customDelta d
            | _ -> customDelta d
        | other ->
            // 其余语义面 → CUSTOM（wanxiang.dev/<type>）。
            // value 按 AG-UI 事件风格承载：**扁平字段**。
            // 身份字段即 AG-UI 命名（threadId/runId/messageId）；
            // 业务字段原样平铺，消息载荷用 content（与 AG-UI 消息内容命名一致）。
            let ce = CustomEvent()
            ce.Name <- sprintf "%s%s" Capabilities.Namespace (WireEvent.typeName other)
            let flat = WireCodec.payloadOf other
            ce.Value <- Nullable(flat |> JsonDocument.Parse |> fun d -> d.RootElement.Clone())
            ce :> BaseEvent

    /// 出站：语义事件 → AG-UI JSON 文本（一帧一事件）。
    let encode (ev: WireEvent) : string =
        let aguiEv = toAguiEvent ev
        JsonSerializer.Serialize(aguiEv, aguiEv.GetType(), aguiOptions)

    /// 入站：AG-UI JSON 文本 → 语义事件。
    /// 未知 `type` / 未知 CUSTOM `name`：返回 None（**不**是错误——AG-UI 增量安全，
    /// 调用方忽略并告警，不得断连）。
    /// 已知面但载荷坏：返回 Some(Error ...)，调用方按协议违规处理。
    let tryDecode (jsonText: string) : Result<WireEvent option, string> =
        match TolerantReader.parse jsonText with
        | ParsedEvent.Malformed reason -> Error reason
        | ParsedEvent.Unrecognized _ -> Ok None
        | ParsedEvent.Custom(name, value: JsonNode) ->
            if not (name.StartsWith Capabilities.Namespace) then
                Ok None    // 别家的 CUSTOM：合法忽略
            else
                let wireType = name.Substring Capabilities.Namespace.Length
                match value with
                | :? JsonObject as vo when vo.ContainsKey "type" ->
                    // value 已是 {type,payload} 形态
                    match WireCodec.tryDecode (value.ToJsonString()) with
                    | Ok ev -> Ok(Some ev)
                    | Error e -> Error(sprintf "custom %s: %s" wireType e)
                | :? JsonObject as vo ->
                    // value 是扁平字段：折成 {type,payload}（字段名出入站一致）
                    let wrapped = JsonObject()
                    wrapped["type"] <- wireType
                    wrapped["payload"] <- vo.DeepClone()
                    match WireCodec.tryDecode (wrapped.ToJsonString()) with
                    | Ok ev -> Ok(Some ev)
                    | Error e -> Error(sprintf "custom %s: %s" wireType e)
        | ParsedEvent.Known (aguiEv: BaseEvent) ->
            // 标准 AG-UI 面反向映射回语义事件
            match aguiEv with
            | :? RunStartedEvent as e ->
                Ok(
                    Some(
                        GenerationStarted
                            {| conversationId = (match Guid.TryParse e.ThreadId with true, g -> g | _ -> Guid.Empty)
                               generationId = (match Guid.TryParse e.RunId with true, g -> g | _ -> Guid.Empty)
                               providerId = ""
                               model = "" |}))
            | :? TextMessageChunkEvent as e ->
                Ok(
                    Some(
                        GenerationDelta
                            {| conversationId = Guid.Empty
                               generationId = (match Guid.TryParse e.MessageId with true, g -> g | _ -> Guid.Empty)
                               payload = (let p = JsonObject() in p["text"] <- e.Delta; p) |}))
            | :? ReasoningMessageChunkEvent as e ->
                // 推理增量：以 MAF 内容形状承载（text 内容项）
                Ok(
                    Some(
                        GenerationDelta
                            {| conversationId = Guid.Empty
                               generationId = (match Guid.TryParse e.MessageId with true, g -> g | _ -> Guid.Empty)
                               payload =
                                   (let p = JsonObject()
                                    p["role"] <- "assistant"
                                    let cs = JsonArray()
                                    let c = JsonObject()
                                    c["$type"] <- "text"
                                    c["text"] <- e.Delta
                                    cs.Add c
                                    p["contents"] <- cs
                                    p) |}))
            | :? ToolCallStartEvent as e ->
                Ok(
                    Some(
                        GenerationDelta
                            {| conversationId = Guid.Empty
                               generationId = Guid.Empty
                               payload =
                                   (let p = JsonObject()
                                    p["role"] <- "assistant"
                                    let cs = JsonArray()
                                    let c = JsonObject()
                                    c["$type"] <- "functionCall"
                                    c["callId"] <- e.ToolCallId
                                    c["name"] <- e.ToolCallName
                                    cs.Add c
                                    p["contents"] <- cs
                                    p) |}))
            | :? ToolCallResultEvent as e ->
                Ok(
                    Some(
                        GenerationDelta
                            {| conversationId = Guid.Empty
                               generationId = (match Guid.TryParse e.MessageId with true, g -> g | _ -> Guid.Empty)
                               payload =
                                   (let p = JsonObject()
                                    p["role"] <- "tool"
                                    let cs = JsonArray()
                                    let c = JsonObject()
                                    c["$type"] <- "functionResult"
                                    c["callId"] <- e.ToolCallId
                                    c["result"] <- (match box e.Content with null -> null | c -> (string c))
                                    cs.Add c
                                    p["contents"] <- cs
                                    p) |}))
            | :? RunFinishedEvent as e ->
                // Outcome 派生类判 cancelled；其余按 completed。
                let status =
                    match e.Outcome with
                    | :? RunFinishedCancelledOutcome -> "cancelled"
                    | _ -> "completed"
                Ok(
                    Some(
                        GenerationFinished
                            {| conversationId = (match Guid.TryParse e.ThreadId with true, g -> g | _ -> Guid.Empty)
                               generationId = (match Guid.TryParse e.RunId with true, g -> g | _ -> Guid.Empty)
                               status = status
                               error = None
                               usage = None |}))
            | :? RunErrorEvent as e ->
                Ok(
                    Some(
                        GenerationFinished
                            {| conversationId = Guid.Empty
                               generationId = Guid.Empty
                               status = "failed"
                               error = Some { kind = Wanxiang.Core.GenerationErrorKind.ProviderTimeout; message = e.Message; detail = None; retryable = false; retryAfterSeconds = None }
                               usage = None |}))
            | _ -> Ok None    // 认识但当前不消费的标准事件（STATE_DELTA 等）：忽略
