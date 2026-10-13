namespace Wanxiang.Core

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes

/// 投影的 JSON 编解码（快照正文用；与线上 Wire 无关）。
/// 从旧 Store/Snapshot.fs 搬来：投影形状属于领域，快照外壳交给 wanxiang2 Snapshot 库。
module ProjectionCodec =

    // ---------- JSON 辅助 ----------

    let private tryGet (o: JsonObject) (k: string) : JsonNode option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(k, &n) && not (isNull n) then Some n else None

    let private tryStr (o: JsonObject) (k: string) : string option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
        | _ -> None

    let private tryUInt64 (o: JsonObject) (k: string) : CommitId option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.String ->
            match UInt64.TryParse(v.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, x -> Some x
            | _ -> None
        | _ -> None

    let private tryInt64 (o: JsonObject) (k: string) : int64 option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.Number ->
            match v with
            | :? JsonValue as jv ->
                match jv.TryGetValue<int64>() with
                | true, x -> Some x
                | _ -> None
            | _ -> None
        | _ -> None

    let private tryInt (o: JsonObject) (k: string) : int option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.Number ->
            match v with
            | :? JsonValue as jv ->
                match jv.TryGetValue<int>() with
                | true, x -> Some x
                | _ -> None
            | _ -> None
        | _ -> None

    let private tryBool (o: JsonObject) (k: string) : bool option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.True -> Some true
        | Some v when v.GetValueKind() = JsonValueKind.False -> Some false
        | _ -> None

    let private tryDateTime (o: JsonObject) (k: string) : DateTimeOffset option =
        match tryStr o k with
        | Some s ->
            match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, d -> Some d
            | _ -> None
        | None -> None

    let private tryGuid (o: JsonObject) (k: string) : Guid option =
        match tryStr o k with
        | Some s ->
            match Guid.TryParse s with
            | true, g -> Some g
            | _ -> None
        | None -> None

    // ---------- 投影 JSON 编解码（快照专用；与线上 Wire 无关） ----------

    let private messageToJson (m: MessageRecord) : JsonObject =
        let o = JsonObject()
        o["commitId"] <- m.commitId.ToString(CultureInfo.InvariantCulture)
        o["conversationId"] <- m.conversationId.ToString("D")
        o["payloadJson"] <- m.payloadJson.DeepClone()
        o["committedAtUtc"] <- m.committedAtUtc.UtcDateTime.ToString("o")
        match m.deletedAtCommitId with
        | Some d -> o["deletedAtCommitId"] <- d.ToString(CultureInfo.InvariantCulture)
        | None -> ()
        o

    let private messageFromJson (o: JsonObject) : MessageRecord option =
        match tryUInt64 o "commitId", tryGuid o "conversationId", tryGet o "payloadJson", tryDateTime o "committedAtUtc" with
        | Some cid, Some conv, Some payload, Some at ->
            Some
                { commitId = cid
                  conversationId = conv
                  payloadJson = payload.DeepClone()
                  committedAtUtc = at
                  deletedAtCommitId = tryUInt64 o "deletedAtCommitId" }
        | _ -> None

    let private parentToJson (p: (Guid * CommitId option) option) : JsonNode =
        match p with
        | None -> null
        | Some (g, after) ->
            let o = JsonObject()
            o["parentId"] <- g.ToString("D")
            match after with
            | Some a -> o["afterId"] <- a.ToString(CultureInfo.InvariantCulture)
            | None -> ()
            o :> JsonNode

    let private parentFromJson (v: JsonNode) : ((Guid * CommitId option) option) option =
        // None = 解析失败；Some x = 成功
        match v with
        | null -> Some None
        | :? JsonObject as o ->
            match tryGuid o "parentId" with
            | Some g -> Some(Some(g, tryUInt64 o "afterId"))
            | None -> None
        | _ -> None

    let private conversationToJson (c: Conversation) : JsonObject =
        let o = JsonObject()
        o["conversationId"] <- c.conversationId.ToString("D")
        o["title"] <- c.title
        o["createdAtUtc"] <- c.createdAtUtc.UtcDateTime.ToString("o")
        o["lastActivityAtUtc"] <- c.lastActivityAtUtc.UtcDateTime.ToString("o")
        o["deleted"] <- c.deleted
        o["pinned"] <- c.pinned
        o["archived"] <- c.archived
        o["parent"] <- parentToJson c.parent
        match c.forkBaseCommitId with
        | Some b -> o["forkBaseCommitId"] <- b.ToString(CultureInfo.InvariantCulture)
        | None -> ()
        o["config"] <- CommitCodec.configToJson c.config
        let arr = JsonArray()
        for m in c.messages do
            arr.Add(messageToJson m)
        o["messages"] <- arr
        match c.lastCommitId with
        | Some l -> o["lastCommitId"] <- l.ToString(CultureInfo.InvariantCulture)
        | None -> ()
        o

    let private conversationFromJson (o: JsonObject) : Conversation option =
        match
            tryGuid o "conversationId",
            tryStr o "title",
            tryDateTime o "createdAtUtc",
            tryDateTime o "lastActivityAtUtc",
            tryBool o "deleted",
            tryBool o "pinned",
            tryBool o "archived"
        with
        | Some cid, Some title, Some created, Some lastAct, Some deleted, Some pinned, Some archived ->
            let parent =
                match tryGet o "parent" with
                | Some v -> parentFromJson v
                | None -> Some None
            match parent with
            | None -> None
            | Some parentVal ->
                match tryGet o "config" with
                | Some v when v.GetValueKind() = JsonValueKind.Object ->
                    let cfgNode = v.AsObject()
                    let messages =
                        match tryGet o "messages" with
                        | Some v when v.GetValueKind() = JsonValueKind.Array ->
                            let mutable ok = true
                            let list = ResizeArray<MessageRecord>()
                            for item in v.AsArray() do
                                if item <> null && item.GetValueKind() = JsonValueKind.Object then
                                    match messageFromJson (item.AsObject()) with
                                    | Some m -> list.Add m
                                    | None -> ok <- false
                                else
                                    ok <- false
                            if ok then Some(List.ofSeq list) else None
                        | _ -> None
                    match messages with
                    | Some msgs ->
                        Some
                            { conversationId = cid
                              title = title
                              createdAtUtc = created
                              lastActivityAtUtc = lastAct
                              deleted = deleted
                              pinned = pinned
                              archived = archived
                              parent = parentVal
                              forkBaseCommitId = tryUInt64 o "forkBaseCommitId"
                              config = CommitCodec.configFromJson cfgNode
                              messages = msgs
                              lastCommitId = tryUInt64 o "lastCommitId" }
                    | None -> None
                | _ -> None
        | _ -> None


    let toJson (p: Projection) : JsonObject =
        let o = JsonObject()
        let convs = JsonArray()
        for KeyValue(_, c) in p.conversations do
            convs.Add(conversationToJson c)
        o["conversations"] <- convs
        o["latestCommitId"] <- p.latestCommitId.ToString(CultureInfo.InvariantCulture)
        o

    let tryFromJson (o: JsonObject) : Projection option =
        let convs =
            match tryGet o "conversations" with
            | Some v when v.GetValueKind() = JsonValueKind.Array ->
                let mutable ok = true
                let map = System.Collections.Generic.Dictionary<Guid, Conversation>()
                for item in v.AsArray() do
                    if item <> null && item.GetValueKind() = JsonValueKind.Object then
                        match conversationFromJson (item.AsObject()) with
                        | Some c -> map[c.conversationId] <- c
                        | None -> ok <- false
                    else
                        ok <- false
                if ok then Some(map |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq) else None
            | _ -> None
        match convs, tryUInt64 o "latestCommitId" with
        | Some c, Some latest -> Some { conversations = c; latestCommitId = latest }
        | _ -> None
