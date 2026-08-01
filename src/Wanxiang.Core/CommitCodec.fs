namespace Wanxiang.Core

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes

/// 事件与提交的 JSON 编解码（NDJSON 行 <-> Commit）。
module CommitCodec =

    let private jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.General)

    let private tryGet (o: JsonObject) (key: string) : JsonNode option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) then
            if isNull n then None else Some n
        else
            None

    let private tryGetDouble (v: JsonNode) : double option =
        match v with
        | :? JsonValue as jv ->
            match jv.TryGetValue<double>() with
            | true, d -> Some d
            | _ -> None
        | _ -> None

    let private tryGetInt (v: JsonNode) : int option =
        match v with
        | :? JsonValue as jv ->
            match jv.TryGetValue<int>() with
            | true, i -> Some i
            | _ -> None
        | _ -> None

    let private tryGetUInt64 (v: JsonNode) : uint64 option =
        match v with
        | :? JsonValue as jv ->
            match jv.TryGetValue<uint64>() with
            | true, i -> Some i
            | _ -> None
        | _ -> None

    let configToJson (cfg: SessionConfig) : JsonObject =
        let o = JsonObject()
        o["provider"] <- cfg.provider
        o["model"] <- cfg.model
        match cfg.instructions with Some s -> o["instructions"] <- s | None -> ()
        if not (List.isEmpty cfg.tools) then
            o["tools"] <- JsonArray([| for t in cfg.tools -> JsonNode.op_Implicit t |])
        match cfg.temperature with Some t -> o["temperature"] <- t | None -> ()
        match cfg.maxTokens with Some m -> o["maxTokens"] <- m | None -> ()
        match cfg.extraJson with Some n -> o["extra"] <- n.DeepClone() | None -> ()
        o

    let configFromJson (o: JsonObject) : SessionConfig =
        let getStr (k: string) =
            match tryGet o k with
            | Some v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
            | _ -> None
        let getFloat (k: string) =
            match tryGet o k with
            | Some v -> tryGetDouble v
            | None -> None
        let getInt (k: string) =
            match tryGet o k with
            | Some v -> tryGetInt v
            | None -> None
        let getTools () =
            match tryGet o "tools" with
            | Some v when v.GetValueKind() = JsonValueKind.Array ->
                [ for item in v.AsArray() do
                      if item <> null && item.GetValueKind() = JsonValueKind.String then
                          item.GetValue<string>() ]
            | _ -> []
        let extra =
            match tryGet o "extra" with
            | Some v -> Some(v.DeepClone())
            | _ -> None
        { provider = getStr "provider" |> Option.defaultValue "openai"
          model = getStr "model" |> Option.defaultValue ""
          instructions = getStr "instructions"
          tools = getTools ()
          temperature = getFloat "temperature"
          maxTokens = getInt "maxTokens"
          extraJson = extra }

    let eventDataToJson (ev: EventData) : JsonObject =
        let o = JsonObject()
        o["type"] <- Events.eventType ev
        o["version"] <- Events.eventVersion ev
        let data = JsonObject()
        match ev with
        | ConversationCreated d ->
            data["conversationId"] <- d.conversationId.ToString("D")
            data["title"] <- d.title
            data["config"] <- configToJson d.config
        | ConversationForked d ->
            data["conversationId"] <- d.conversationId.ToString("D")
            data["parentConversationId"] <- d.parentConversationId.ToString("D")
            match d.forkAfterId with Some id -> data["forkAfterId"] <- id | None -> ()
        | ConversationRenamed d ->
            data["conversationId"] <- d.conversationId.ToString("D")
            data["title"] <- d.title
        | ConversationConfigUpdated d ->
            data["conversationId"] <- d.conversationId.ToString("D")
            data["config"] <- configToJson d.config
        | ConversationDeleted d ->
            data["conversationId"] <- d.conversationId.ToString("D")
        | AgentMessageRecorded d ->
            data["conversationId"] <- d.conversationId.ToString("D")
            data["payload"] <- d.payloadJson.DeepClone()
        | MessageDeleted d ->
            data["conversationId"] <- d.conversationId.ToString("D")
            data["messageCommitId"] <- d.messageCommitId
        o["data"] <- data
        o

    let private parseGuid (v: JsonNode) : Guid option =
        try
            match v.GetValueKind() with
            | JsonValueKind.String -> Some(Guid.Parse(v.GetValue<string>()))
            | _ -> None
        with _ -> None

    let private parseCommitId (v: JsonNode) : CommitId option =
        try
            match v.GetValueKind() with
            | JsonValueKind.Number -> tryGetUInt64 v
            | _ -> None
        with _ -> None

    let private configFromNode (v: JsonNode) : SessionConfig option =
        match v with
        | :? JsonObject as o -> Some(configFromJson o)
        | _ -> None

    let private tryString (o: JsonObject) (k: string) : string option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
        | _ -> None

    /// 按事件外壳解析为领域事件。结构非法或版本未知返回 None（触发损坏处理）。
    let tryEventFromJson (o: JsonObject) : EventData option =
        match tryGet o "type", tryGet o "version", tryGet o "data" with
        | Some typeNode, Some versionNode, Some dataNode
            when typeNode.GetValueKind() = JsonValueKind.String
                 && versionNode.GetValueKind() = JsonValueKind.Number
                 && dataNode.GetValueKind() = JsonValueKind.Object ->
            match tryGetInt versionNode with
            | Some v when v = Constants.EventVersion1 ->
                let data = dataNode.AsObject()
                let guid k = match tryGet data k with Some v -> parseGuid v | None -> None
                let commitId k = match tryGet data k with Some v -> parseCommitId v | None -> None
                let cfg () = match tryGet data "config" with Some v -> configFromNode v | None -> None
                match tryString o "type" with
                | Some "conversation.created" ->
                    match guid "conversationId", cfg () with
                    | Some cid, Some c ->
                        let title = tryString data "title" |> Option.defaultValue ""
                        Some(ConversationCreated { conversationId = cid; title = title; config = c })
                    | _ -> None
                | Some "conversation.forked" ->
                    match guid "conversationId", guid "parentConversationId" with
                    | Some cid, Some pid ->
                        let after = match tryGet data "forkAfterId" with Some v -> parseCommitId v | None -> None
                        Some(ConversationForked { conversationId = cid; parentConversationId = pid; forkAfterId = after })
                    | _ -> None
                | Some "conversation.renamed" ->
                    match guid "conversationId" with
                    | Some cid ->
                        let title = tryString data "title" |> Option.defaultValue ""
                        Some(ConversationRenamed { conversationId = cid; title = title })
                    | _ -> None
                | Some "conversation.config-updated" ->
                    match guid "conversationId", cfg () with
                    | Some cid, Some c -> Some(ConversationConfigUpdated { conversationId = cid; config = c })
                    | _ -> None
                | Some "conversation.deleted" ->
                    match guid "conversationId" with
                    | Some cid -> Some(ConversationDeleted { conversationId = cid })
                    | _ -> None
                | Some "agent-message-recorded" ->
                    match guid "conversationId" with
                    | Some cid ->
                        match tryGet data "payload" with
                        | Some p -> Some(AgentMessageRecorded { conversationId = cid; payloadJson = p.DeepClone() })
                        | None -> None
                    | _ -> None
                | Some "message.deleted" ->
                    match guid "conversationId", commitId "messageCommitId" with
                    | Some cid, Some mid -> Some(MessageDeleted { conversationId = cid; messageCommitId = mid })
                    | _ -> None
                | _ -> None
            | _ -> None
        | _ -> None

    /// 将 Commit 序列化为一条 NDJSON 行（不含换行）。
    let commitToJsonLine (commit: Events.Commit) : string =
        let o = JsonObject()
        o["formatVersion"] <- commit.formatVersion
        o["id"] <- commit.id
        o["committedAtUtc"] <- commit.committedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
        match commit.commandId with Some c -> o["commandId"] <- c | None -> ()
        match commit.commandType with Some t -> o["commandType"] <- t | None -> ()
        match commit.commandHash with Some h -> o["commandHash"] <- h | None -> ()
        let arr = JsonArray()
        for ev in commit.events do
            arr.Add(eventDataToJson ev)
        o["events"] <- arr
        o.ToJsonString(JsonSerializerOptions(JsonSerializerDefaults.General))

    /// 解析一条 NDJSON 行。结构非法返回 None。
    let tryCommitFromJsonLine (line: string) : Events.Commit option =
        try
            let node = JsonNode.Parse line
            match node with
            | :? JsonObject as o ->
                match tryGet o "formatVersion", tryGet o "id", tryGet o "committedAtUtc", tryGet o "events" with
                | Some fv, Some idNode, Some tsNode, Some evNode
                    when fv.GetValueKind() = JsonValueKind.Number
                         && idNode.GetValueKind() = JsonValueKind.Number
                         && tsNode.GetValueKind() = JsonValueKind.String
                         && evNode.GetValueKind() = JsonValueKind.Array ->
                    match tryGetInt fv, tryGetUInt64 idNode with
                    | Some f, Some id when f = Constants.FormatVersion ->
                        let events =
                            let mutable ok = true
                            let list = System.Collections.Generic.List<EventData>()
                            for e in evNode.AsArray() do
                                if e <> null && e.GetValueKind() = JsonValueKind.Object then
                                    match tryEventFromJson (e.AsObject()) with
                                    | Some ev -> list.Add ev
                                    | None -> ok <- false
                                else ok <- false
                            if ok then Some(List.ofSeq list) else None
                        match events with
                        | Some evs ->
                            match DateTimeOffset.TryParse(tsNode.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
                            | true, t ->
                                let commandId = tryString o "commandId"
                                let commandType = tryString o "commandType"
                                let commandHash = tryString o "commandHash"
                                Some
                                    { formatVersion = f
                                      id = id
                                      committedAtUtc = t
                                      commandId = commandId
                                      commandType = commandType
                                      commandHash = commandHash
                                      events = evs }
                            | _ -> None
                        | None -> None
                    | _ -> None
                | _ -> None
            | _ -> None
        with _ -> None
