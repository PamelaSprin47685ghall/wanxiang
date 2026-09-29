namespace Wanxiang.Core.Ledger

open System
open System.Text.Json
open System.Text.Json.Nodes

/// 账本外壳记录（`formatVersion = 2`）。
///
/// 序列化规则（**冻结**，流 A 提供、流 E 消费）：
/// - 内存中 `commitId` 是 `uint64`，落盘 **MUST** 是 JSON 字符串（RFC 8785 附录 D）；
/// - `at` 是 RFC 3339 UTC 时刻，毫秒精度，`Z` 结尾；
/// - `events` 是 AG-UI 事件对象数组，语义见 `wanxiang-protocol` 第 55 节。
type LedgerRecord =
    { formatVersion: int
      commitId: uint64
      bootId: Guid
      source: string
      at: DateTimeOffset
      commandId: string option
      commandType: string option
      commandHash: string option
      events: JsonArray }

type LedgerParseError = string

[<RequireQualifiedAccess>]
module Record =

    /// 当前外壳版本。读到其它值即判定为不可读（clean-break，不做迁移）。
    [<Literal>]
    let FormatVersion = 2

    /// 显式 byref 重载以避开 F# 重载歧义（FS0041）。
    let private tryProp (o: JsonObject) (k: string) : JsonNode option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(k, &n) && not (isNull n) then Some n else None

    let private tryStr (o: JsonObject) (k: string) : string option =
        match tryProp o k with
        | Some v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
        | _ -> None

    /// `formatVersion` 以 JSON **数字**写入，就必须按数字读回。
    /// 写读不同型会让每条记录都被判「缺字段」、replay 全空——真实踩过的坑。
    let private tryInt (o: JsonObject) (k: string) : int option =
        match tryProp o k with
        | Some v when v.GetValueKind() = JsonValueKind.Number ->
            match v with
            | :? JsonValue as jv ->
                match jv.TryGetValue<int>() with
                | true, i -> Some i
                | _ -> None
            | _ -> None
        | _ -> None

    let toJson (r: LedgerRecord) : string =
        let o = JsonObject()
        o["formatVersion"] <- JsonValue.Create(r.formatVersion)
        o["commitId"] <- JsonValue.Create(r.commitId.ToString())   // 字符串承载大整数
        o["bootId"] <- JsonValue.Create(r.bootId.ToString("D"))
        o["source"] <- JsonValue.Create(r.source)
        o["at"] <- JsonValue.Create(r.at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"))
        r.commandId |> Option.iter (fun v -> o["commandId"] <- JsonValue.Create v)
        r.commandType |> Option.iter (fun v -> o["commandType"] <- JsonValue.Create v)
        r.commandHash |> Option.iter (fun v -> o["commandHash"] <- JsonValue.Create v)
        o["events"] <- r.events.DeepClone()
        o.ToJsonString()

    let tryFromJson (json: string) : Result<LedgerRecord, LedgerParseError> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as o ->
                let fieldOf k = tryStr o k
                match tryInt o "formatVersion", fieldOf "commitId", fieldOf "bootId",
                      fieldOf "source", fieldOf "at", tryProp o "events" with
                | Some fv, Some cid, Some bid, Some src, Some at, Some ev ->
                    if fv <> FormatVersion then
                        Error(sprintf "unsupported formatVersion %d (expected %d)" fv FormatVersion)
                    elif ev.GetValueKind() <> JsonValueKind.Array then
                        Error "events is not a JSON array"
                    else
                        match UInt64.TryParse(cid: string), Guid.TryParse(bid: string),
                              DateTimeOffset.TryParse(at: string) with
                        | (true, cv), (true, bv), (true, tv) ->
                            Ok { formatVersion = FormatVersion
                                 commitId = cv
                                 bootId = bv
                                 source = src
                                 at = tv
                                 commandId = fieldOf "commandId"
                                 commandType = fieldOf "commandType"
                                 commandHash = fieldOf "commandHash"
                                 events = ev.AsArray() }
                        | _ -> Error "malformed field (commitId/bootId/at)"
                | _ -> Error "missing required field"
            | _ -> Error "top-level value is not a JSON object"
        with e -> Error(sprintf "parse failed: %s" e.Message)
