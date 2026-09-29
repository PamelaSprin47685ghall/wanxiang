namespace Wanxiang.Agui

open System.Text.Json
open System.Text.Json.Nodes
open AGUI.Abstractions

/// 宽容读取层。
///
/// **为什么必须自建**：AG-UI 规范要求未识别的事件类型可被安全忽略（`MUST ignore`
/// 的增量安全策略），但官方 .NET SDK 对未知 `type` 抛 `AGUIUnknownEventTypeException`。
/// 直接使用官方反序列化会把「对端更新」误判为「对端违规」并切断连接。
///
/// 本层把入站文本分成四态，只有**已知类型但字段畸形**才是致命错误。
type ParsedEvent =
    /// 已识别的标准 AG-UI 事件。
    | Known of BaseEvent
    /// 未识别的 `type`：规范要求忽略并告警，**不得**因此终止连接。
    | Unrecognized of typeName: string
    /// 已知 `type` 但载荷不可解析：这是真的坏了。
    | Malformed of reason: string
    /// `CUSTOM` 事件，携带扩展名与值。
    | Custom of name: string * value: JsonNode

[<RequireQualifiedAccess>]
module TolerantReader =

    let private options = AGUIJsonSerializerContext.Default.Options

    /// 官方 SDK 声明的全部标准事件类型名。取字符串常量，不硬编码列表。
    let private knownTypes: Set<string> =
        Set.ofArray
            [| for f in typeof<AGUIEventTypes>.GetFields() do
                   match f.GetValue null with
                   | :? string as s -> yield s
                   | _ -> () |]

    /// 显式 byref 重载以避开 F# 重载歧义（FS0041）。
    let private tryProp (o: JsonObject) (k: string) : JsonNode option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(k, &n) && not (isNull n) then Some n else None

    /// 解析一条入站事件文本。
    let parse (json: string) : ParsedEvent =
        try
            match JsonNode.Parse json with
            | :? JsonObject as o ->
                match tryProp o "type" with
                | Some t when t.GetValueKind() = JsonValueKind.String ->
                    let name = t.GetValue<string>()
                    if name = "CUSTOM" then
                        match tryProp o "name" with
                        | Some n when n.GetValueKind() = JsonValueKind.String ->
                            let value =
                                match tryProp o "value" with
                                | Some v -> v.DeepClone()
                                | None -> JsonNode.Parse "null"
                            Custom(n.GetValue<string>(), value)
                        | _ -> Malformed "CUSTOM event without a string 'name'"
                    elif Set.contains name knownTypes then
                        try
                            Known(JsonSerializer.Deserialize<BaseEvent>(json, options))
                        with e ->
                            Malformed(sprintf "known type '%s' failed to deserialize: %s" name e.Message)
                    else
                        Unrecognized name
                | _ -> Malformed "missing or non-string 'type'"
            | _ -> Malformed "event is not a JSON object"
        with e -> Malformed(sprintf "invalid JSON: %s" e.Message)
