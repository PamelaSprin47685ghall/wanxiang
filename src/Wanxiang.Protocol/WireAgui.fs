namespace Wanxiang.Protocol

open System
open System.Text.Json
open System.Text.Json.Nodes
open AGUI.Abstractions
open Wanxiang.Agui

/// WireEvent ⇄ AG-UI 承载适配层。
///
/// **架构位置**（SSOT `wanxiang-protocol` 第 55 节）：
/// `WireEvent` 的 56 个变体是万象自己的**语义层**，不换；
/// 这一层只换**承载**——出站把语义事件映射成 AG-UI 事件对象，入站反向。
///
/// 映射规则（55.4 标准面 + 55.5 CUSTOM 面）：
/// - 生成类事件（started/delta/finished）→ 标准 AG-UI 事件（RUN_STARTED / TEXT_MESSAGE_* / RUN_FINISHED / RUN_ERROR）
/// - 其余全部 → `CUSTOM`，`name = wanxiang.dev/<面>`，`value` = 原载荷
/// - `Hello` / `UpgradeRequired` **已删除**（版本改 in-band；宽容读取层会忽略旧名）
///
/// 序列化陷阱（实测）：AG-UI 消息必须按 `typeof<AGUIMessage>` 序列化，
/// 按运行期类型会丢 content。事件对象本身无此问题（converter 挂在事件基类上）。
module WireAgui =

    let private aguiOptions = AGUIJsonSerializerContext.Default.Options

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
                // 实测：RunFinishedEvent 无 Reason 成员、Outcome 无公共构造器，
                // cancelled 语义无法在标准面上表达 → 用 CUSTOM 面承载（SSOT 55.5）。
                let ce = CustomEvent()
                ce.Name <- sprintf "%sgeneration-finished" Capabilities.Namespace
                let o = JsonObject()
                o["type"] <- "generation.finished"
                let pl = JsonObject()
                pl["conversationId"] <- d.conversationId.ToString("D")
                pl["generationId"] <- d.generationId.ToString("D")
                pl["status"] <- "cancelled"
                o["payload"] <- pl
                ce.Value <- Nullable(o.ToJsonString() |> JsonDocument.Parse |> fun doc -> doc.RootElement.Clone())
                ce :> BaseEvent
            | _ ->
                // failed / 其它异常态：RUN_ERROR（SSOT 55.4）
                let e = RunErrorEvent()
                e.Message <- (d.error |> Option.map (fun er -> er.message) |> Option.defaultValue d.status)
                e.Code <- (d.status)
                e :> BaseEvent
        | GenerationDelta d ->
            // 文本增量走标准 TEXT_MESSAGE_CHUNK；推理/工具等非文本载荷走 CUSTOM。
            // payload 结构由 ChatOrchestrator 决定；文本 delta 是主路径。
            match d.payload with
            | :? JsonObject as p when p.ContainsKey "text" ->
                let e = TextMessageChunkEvent()
                e.MessageId <- d.generationId.ToString("D")
                e.Delta <- (p["text"].GetValue<string>())
                e :> BaseEvent
            | _ ->
                let o = JsonObject()
                o["conversationId"] <- d.conversationId.ToString("D")
                o["generationId"] <- d.generationId.ToString("D")
                o["payload"] <- d.payload.DeepClone()
                let ce = CustomEvent()
                ce.Name <- sprintf "%sgeneration-delta" Capabilities.Namespace
                ce.Value <- Nullable(o.ToJsonString() |> JsonDocument.Parse |> fun d -> d.RootElement.Clone())
                ce :> BaseEvent
        | Hello _ ->
            // 已删除的握手事件：宽容层会忽略；出站不再产生。
            let ce = CustomEvent()
            ce.Name <- sprintf "%serror" Capabilities.Namespace
            ce.Value <- Nullable(JsonDocument.Parse("\"hello is removed\"").RootElement.Clone())
            ce :> BaseEvent
        | UpgradeRequired _ ->
            let ce = CustomEvent()
            ce.Name <- sprintf "%serror" Capabilities.Namespace
            ce.Value <- Nullable(JsonDocument.Parse("\"upgrade-required is removed\"").RootElement.Clone())
            ce :> BaseEvent
        | other ->
            // 其余语义面 → CUSTOM，name 按原类型名派生（wanxiang.dev/<type>）
            let ce = CustomEvent()
            ce.Name <- sprintf "%s%s" Capabilities.Namespace (WireEvent.typeName other)
            ce.Value <- Nullable(WireCodec.encode other |> JsonDocument.Parse |> fun d -> d.RootElement.Clone())
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
                // CUSTOM value 里存的是原 WireCodec JSON（{type,payload} 外壳）
                match value with
                | :? JsonObject as vo when vo.ContainsKey "payload" ->
                    match WireCodec.tryDecode (value.ToJsonString()) with
                    | Ok ev -> Ok(Some ev)
                    | Error e -> Error(sprintf "custom %s: %s" wireType e)
                | _ ->
                    // 非外壳形态的 wanxiang.dev/*（auth 等）：按原载荷直接解
                    let wrapped = JsonObject()
                    wrapped["type"] <- wireType
                    wrapped["payload"] <- value.DeepClone()
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
                            {| conversationId = Guid.Parse e.ThreadId
                               generationId = Guid.Parse e.RunId
                               providerId = ""
                               model = "" |}))
            | :? TextMessageChunkEvent as e ->
                Ok(
                    Some(
                        GenerationDelta
                            {| conversationId = Guid.Empty
                               generationId = Guid.Parse e.MessageId
                               payload = (let p = JsonObject() in p["text"] <- e.Delta; p) |}))
            | :? RunFinishedEvent as e ->
                Ok(
                    Some(
                        GenerationFinished
                            {| conversationId = Guid.Parse e.ThreadId
                               generationId = Guid.Parse e.RunId
                               status = (match e.Outcome with null -> "completed" | _ -> "completed")
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
