namespace Wanxiang.Protocol

open System
open System.Text.Json
open System.Text.Json.Nodes
open Wanxiang.Core

/// Wire 事件编解码（UTF-8 JSON 文本帧，每个协议事件一个 WebSocket message）。
module WireCodec =

    let private tryGet (o: JsonObject) (key: string) : JsonNode option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) then
            if isNull n then None else Some n
        else
            None

    let private tryString (o: JsonObject) (k: string) : string option =
        match tryGet o k with
        | Some v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
        | _ -> None

    let private tryInt (o: JsonObject) (k: string) : int option =
        match tryGet o k with
        | Some (:? JsonValue as v) when v.GetValueKind() = JsonValueKind.Number ->
            match v.TryGetValue<int>() with
            | true, i -> Some i
            | _ -> None
        | _ -> None

    let private tryInt64 (o: JsonObject) (k: string) : int64 option =
        match tryGet o k with
        | Some (:? JsonValue as v) when v.GetValueKind() = JsonValueKind.Number ->
            match v.TryGetValue<int64>() with
            | true, i -> Some i
            | _ -> None
        | _ -> None

    let private tryUInt64 (o: JsonObject) (k: string) : uint64 option =
        match tryGet o k with
        | Some (:? JsonValue as v) when v.GetValueKind() = JsonValueKind.Number ->
            match v.TryGetValue<uint64>() with
            | true, i -> Some i
            | _ -> None
        | _ -> None

    let private tryGuid (o: JsonObject) (k: string) : Guid option =
        match tryString o k with
        | Some s ->
            match Guid.TryParse s with
            | true, g -> Some g
            | _ -> None
        | None -> None

    let private putGuid (o: JsonObject) (k: string) (g: Guid) = o[k] <- g.ToString("D")

    // ---------- 编码 ----------

    let encode (ev: WireEvent) : string =
        let o = JsonObject()
        o["type"] <- WireEvent.typeName ev
        let p = JsonObject()
        match ev with
        | Hello d ->
            p["protocol"] <- d.protocol
            p["version"] <- d.version
            match d.instanceId with Some i -> p["instanceId"] <- i | None -> ()
        | UpgradeRequired d ->
            p["serverVersion"] <- d.serverVersion
            p["clientVersion"] <- d.clientVersion
        | AuthPresent d -> p["token"] <- d.token
        | AuthAccepted d -> p["instanceId"] <- d.instanceId
        | WireEvent.AuthRejected d -> p["reason"] <- d.reason
        | PairingRequested d -> match d.clientName with Some n -> p["clientName"] <- n | None -> ()
        | PairingStarted d -> p["expiresInSeconds"] <- d.expiresInSeconds
        | PairingAttempted d ->
            p["code"] <- d.code
            match d.clientName with Some n -> p["clientName"] <- n | None -> ()
        | PairingSucceeded d -> p["token"] <- d.token
        | PairingFailed d ->
            p["reason"] <- d.reason
            p["frozen"] <- d.frozen
            p["freezeMinutes"] <- d.freezeMinutes
        | ObserveConversationList -> ()
        | UnobserveConversationList -> ()
        | ConversationListSnapshot d ->
            p["items"] <- d.items.DeepClone()
            p["lastCommitId"] <- d.lastCommitId
        | ObserveConversation d -> putGuid p "conversationId" d.conversationId
        | UnobserveConversation d -> putGuid p "conversationId" d.conversationId
        | ConversationSnapshot d ->
            putGuid p "conversationId" d.conversationId
            p["title"] <- d.title
            p["lastCommitId"] <- d.lastCommitId
            p["runtimeState"] <- d.runtimeState
            p["messages"] <- d.messages.DeepClone()
            p["snapshotEarliestCommitId"] <- d.snapshotEarliestCommitId
            p["snapshotHasMore"] <- d.snapshotHasMore
        | ConversationUpdated d ->
            putGuid p "conversationId" d.conversationId
            p["commitId"] <- d.commitId
            p["change"] <- d.change.DeepClone()
        | MessageCommitted d ->
            putGuid p "conversationId" d.conversationId
            p["commitId"] <- d.commitId
            p["payload"] <- d.payload.DeepClone()
        | HistoryRequest d ->
            putGuid p "conversationId" d.conversationId
            p["beforeCommitId"] <- d.beforeCommitId
            p["limit"] <- d.limit
        | HistoryPage d ->
            putGuid p "conversationId" d.conversationId
            p["beforeCommitId"] <- d.beforeCommitId
            p["items"] <- d.items.DeepClone()
            p["hasMore"] <- d.hasMore
        | Command _ -> failwith "Command 事件使用 encodeCommand"
        | CommandAccepted d -> putGuid p "invocationId" d.invocationId
        | CommandCommitted d ->
            putGuid p "invocationId" d.invocationId
            p["commandId"] <- d.commandId
            p["commitId"] <- d.commitId
        | CommandRejected d ->
            putGuid p "invocationId" d.invocationId
            p["code"] <- d.code
            p["message"] <- d.message
        | CursorAdvanced d -> p["id"] <- d.id
        | AuthorityCatchUp d ->
            p["fromCursor"] <- d.fromCursor
            p["toCommitId"] <- d.toCommitId
            p["items"] <- d.items.DeepClone()
        | GenerationDelta d ->
            putGuid p "conversationId" d.conversationId
            putGuid p "generationId" d.generationId
            p["payload"] <- d.payload.DeepClone()
        | GenerationStarted d ->
            putGuid p "conversationId" d.conversationId
            putGuid p "generationId" d.generationId
        | GenerationFinished d ->
            putGuid p "conversationId" d.conversationId
            putGuid p "generationId" d.generationId
            p["status"] <- d.status
            match d.error with Some e -> p["error"] <- e | None -> ()
        | GenerationCancel d ->
            putGuid p "conversationId" d.conversationId
            putGuid p "generationId" d.generationId
        | AttachmentBegin d ->
            putGuid p "attachmentId" d.attachmentId
            p["totalBytes"] <- d.totalBytes
            p["sha256"] <- d.sha256
            p["mediaType"] <- d.mediaType
            p["fileName"] <- d.fileName
        | AttachmentChunk d ->
            putGuid p "attachmentId" d.attachmentId
            p["index"] <- d.index
            p["data"] <- d.dataBase64
        | AttachmentComplete d ->
            putGuid p "attachmentId" d.attachmentId
            p["sha256"] <- d.sha256
        | AttachmentCommitted d ->
            putGuid p "attachmentId" d.attachmentId
            p["sha256"] <- d.sha256
            p["size"] <- d.size
        | AttachmentAborted d ->
            putGuid p "attachmentId" d.attachmentId
            p["reason"] <- d.reason
        | AttachmentDownloadRequest d -> p["sha256"] <- d.sha256
        | AttachmentDownloadBegin d ->
            p["sha256"] <- d.sha256
            p["size"] <- d.size
            p["mediaType"] <- d.mediaType
            p["fileName"] <- d.fileName
        | AttachmentDownloadChunk d ->
            p["sha256"] <- d.sha256
            p["index"] <- d.index
            p["data"] <- d.dataBase64
        | AttachmentDownloadComplete d -> p["sha256"] <- d.sha256
        | ConfigChanged d -> p["reason"] <- d.reason
        | ServerError d -> p["message"] <- d.message
        | Ping -> ()
        | Pong -> ()
        if p.Count > 0 then
            o["payload"] <- p
        o.ToJsonString(JsonSerializerOptions(JsonSerializerDefaults.General))

    /// 编码客户端写命令（Command 事件专用路径）。
    let encodeCommand (cmd: ClientCommand) : string =
        let o = JsonObject()
        o["type"] <- ClientCommand.commandType cmd
        let p = JsonObject()
        let inv = ClientCommand.invocationId cmd
        putGuid p "invocationId" inv
        match cmd with
        | CreateConversation d ->
            putGuid p "conversationId" d.conversationId
            p["title"] <- d.title
            p["config"] <- CommitCodec.configToJson d.config
        | ForkConversation d ->
            putGuid p "conversationId" d.conversationId
            putGuid p "parentConversationId" d.parentConversationId
            match d.forkAfterId with Some id -> p["forkAfterId"] <- id | None -> ()
            p["config"] <- CommitCodec.configToJson d.config
            p["message"] <- d.editedMessageJson.DeepClone()
        | SendUserMessage d ->
            putGuid p "conversationId" d.conversationId
            p["message"] <- d.messageJson.DeepClone()
        | RenameConversation d ->
            putGuid p "conversationId" d.conversationId
            p["title"] <- d.title
        | DeleteConversation d -> putGuid p "conversationId" d.conversationId
        | DeleteMessage d ->
            putGuid p "conversationId" d.conversationId
            p["messageCommitId"] <- d.messageCommitId
        | UpdateConversationConfig d ->
            putGuid p "conversationId" d.conversationId
            p["config"] <- CommitCodec.configToJson d.config
        o["payload"] <- p
        o.ToJsonString(JsonSerializerOptions(JsonSerializerDefaults.General))

    // ---------- 解码 ----------

    /// 从 JSON 文本解码命令事件（C→S）。
    let tryDecodeCommand (jsonText: string) : Result<ClientCommand, string> =
        try
            match JsonNode.Parse jsonText with
            | :? JsonObject as o ->
                match tryString o "type" with
                | Some "conversation.create" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "conversationId" with
                        | Some inv, Some cid ->
                            let title = tryString p "title" |> Option.defaultValue ""
                            let cfg =
                                match tryGet p "config" with
                                | Some (:? JsonObject as c) -> CommitCodec.configFromJson c
                                | _ -> SessionConfig.empty
                            Ok(CreateConversation {| invocationId = inv; conversationId = cid; title = title; config = cfg |})
                        | _ -> Error "conversation.create: missing invocationId/conversationId"
                    | _ -> Error "conversation.create: missing payload"
                | Some "conversation.fork" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "conversationId", tryGuid p "parentConversationId" with
                        | Some inv, Some cid, Some pid ->
                            let after = match tryUInt64 p "forkAfterId" with Some v -> Some v | None -> None
                            let cfg =
                                match tryGet p "config" with
                                | Some (:? JsonObject as c) -> CommitCodec.configFromJson c
                                | _ -> SessionConfig.empty
                            match tryGet p "message" with
                            | Some msg ->
                                Ok(ForkConversation {| invocationId = inv; conversationId = cid; parentConversationId = pid; forkAfterId = after; config = cfg; editedMessageJson = msg.DeepClone() |})
                            | None -> Error "conversation.fork: missing message"
                        | _ -> Error "conversation.fork: missing invocationId/conversationId/parentConversationId"
                    | _ -> Error "conversation.fork: missing payload"
                | Some "chat.user-message.enqueue" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "conversationId" with
                        | Some inv, Some cid ->
                            match tryGet p "message" with
                            | Some msg -> Ok(SendUserMessage {| invocationId = inv; conversationId = cid; messageJson = msg.DeepClone() |})
                            | None -> Error "chat.user-message.enqueue: missing message"
                        | _ -> Error "chat.user-message.enqueue: missing invocationId/conversationId"
                    | _ -> Error "chat.user-message.enqueue: missing payload"
                | Some "conversation.rename" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "conversationId" with
                        | Some inv, Some cid ->
                            let title = tryString p "title" |> Option.defaultValue ""
                            Ok(RenameConversation {| invocationId = inv; conversationId = cid; title = title |})
                        | _ -> Error "conversation.rename: missing invocationId/conversationId"
                    | _ -> Error "conversation.rename: missing payload"
                | Some "conversation.delete" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "conversationId" with
                        | Some inv, Some cid -> Ok(DeleteConversation {| invocationId = inv; conversationId = cid |})
                        | _ -> Error "conversation.delete: missing invocationId/conversationId"
                    | _ -> Error "conversation.delete: missing payload"
                | Some "message.delete" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "conversationId", tryUInt64 p "messageCommitId" with
                        | Some inv, Some cid, Some mid -> Ok(DeleteMessage {| invocationId = inv; conversationId = cid; messageCommitId = mid |})
                        | _ -> Error "message.delete: missing invocationId/conversationId/messageCommitId"
                    | _ -> Error "message.delete: missing payload"
                | Some "conversation.config-update" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "conversationId" with
                        | Some inv, Some cid ->
                            let cfg =
                                match tryGet p "config" with
                                | Some (:? JsonObject as c) -> CommitCodec.configFromJson c
                                | _ -> SessionConfig.empty
                            Ok(UpdateConversationConfig {| invocationId = inv; conversationId = cid; config = cfg |})
                        | _ -> Error "conversation.config-update: missing invocationId/conversationId"
                    | _ -> Error "conversation.config-update: missing payload"
                | _ -> Error "not a command event"
            | _ -> Error "not a JSON object"
        with e ->
            Error(sprintf "command decode failed: %s" e.Message)

    /// 从 JSON 文本解码通用事件。
    let tryDecode (jsonText: string) : Result<WireEvent, string> =
        try
            match JsonNode.Parse jsonText with
            | :? JsonObject as o ->
                let p =
                    match tryGet o "payload" with
                    | Some (:? JsonObject as po) -> po
                    | _ -> JsonObject()
                match tryString o "type" with
                | Some "protocol.hello" ->
                    let protocol = tryString p "protocol" |> Option.defaultValue ""
                    let version = tryInt p "version" |> Option.defaultValue 0
                    Ok(Hello {| protocol = protocol; version = version; instanceId = tryString p "instanceId" |})
                | Some "protocol.upgrade-required" ->
                    Ok(UpgradeRequired {| serverVersion = tryInt p "serverVersion" |> Option.defaultValue 0; clientVersion = tryInt p "clientVersion" |> Option.defaultValue 0 |})
                | Some "auth.present" -> Ok(AuthPresent {| token = tryString p "token" |> Option.defaultValue "" |})
                | Some "auth.accepted" -> Ok(AuthAccepted {| instanceId = tryString p "instanceId" |> Option.defaultValue "" |})
                | Some "auth.rejected" -> Ok(WireEvent.AuthRejected {| reason = tryString p "reason" |> Option.defaultValue "" |})
                | Some "pairing.requested" -> Ok(PairingRequested {| clientName = tryString p "clientName" |})
                | Some "pairing.started" -> Ok(PairingStarted {| expiresInSeconds = tryInt p "expiresInSeconds" |> Option.defaultValue 300 |})
                | Some "pairing.attempted" -> Ok(PairingAttempted {| code = tryString p "code" |> Option.defaultValue ""; clientName = tryString p "clientName" |})
                | Some "pairing.succeeded" -> Ok(PairingSucceeded {| token = tryString p "token" |> Option.defaultValue "" |})
                | Some "pairing.failed" ->
                    let frozen =
                        match tryGet p "frozen" with
                        | Some v when v.GetValueKind() = JsonValueKind.True -> true
                        | _ -> false
                    Ok(PairingFailed {| reason = tryString p "reason" |> Option.defaultValue ""; frozen = frozen; freezeMinutes = tryInt p "freezeMinutes" |> Option.defaultValue 0 |})
                | Some "conversation-list.observe" -> Ok ObserveConversationList
                | Some "conversation-list.unobserve" -> Ok UnobserveConversationList
                | Some "conversation.observe" ->
                    match tryGuid p "conversationId" with Some cid -> Ok(ObserveConversation {| conversationId = cid |}) | None -> Error "conversation.observe: missing conversationId"
                | Some "conversation.unobserve" ->
                    match tryGuid p "conversationId" with Some cid -> Ok(UnobserveConversation {| conversationId = cid |}) | None -> Error "conversation.unobserve: missing conversationId"
                | Some "conversation-list.snapshot" ->
                    let items = match tryGet p "items" with Some (:? JsonArray as a) -> a | _ -> JsonArray()
                    Ok(ConversationListSnapshot {| items = items; lastCommitId = tryUInt64 p "lastCommitId" |> Option.defaultValue 0UL |})
                | Some "conversation.snapshot" ->
                    match tryGuid p "conversationId" with
                    | Some cid ->
                        let msgs = match tryGet p "messages" with Some (:? JsonArray as a) -> a | _ -> JsonArray()
                        let hasMore =
                            match tryGet p "snapshotHasMore" with
                            | Some v when v.GetValueKind() = JsonValueKind.True -> true
                            | _ -> false
                        Ok(ConversationSnapshot {| conversationId = cid; title = tryString p "title" |> Option.defaultValue ""; lastCommitId = tryUInt64 p "lastCommitId" |> Option.defaultValue 0UL; runtimeState = tryString p "runtimeState" |> Option.defaultValue "idle"; messages = msgs; snapshotEarliestCommitId = tryUInt64 p "snapshotEarliestCommitId" |> Option.defaultValue 0UL; snapshotHasMore = hasMore |})
                    | None -> Error "conversation.snapshot: missing conversationId"
                | Some "conversation.updated" ->
                    match tryGuid p "conversationId" with
                    | Some cid ->
                        let change = match tryGet p "change" with Some (:? JsonObject as c) -> c | _ -> JsonObject()
                        Ok(ConversationUpdated {| conversationId = cid; commitId = tryUInt64 p "commitId" |> Option.defaultValue 0UL; change = change |})
                    | None -> Error "conversation.updated: missing conversationId"
                | Some "conversation.message-committed" ->
                    match tryGuid p "conversationId" with
                    | Some cid ->
                        match tryGet p "payload" with
                        | Some payload -> Ok(MessageCommitted {| conversationId = cid; commitId = tryUInt64 p "commitId" |> Option.defaultValue 0UL; payload = payload |})
                        | None -> Error "conversation.message-committed: missing payload"
                    | None -> Error "conversation.message-committed: missing conversationId"
                | Some "history.request" ->
                    match tryGuid p "conversationId" with
                    | Some cid ->
                        Ok(HistoryRequest {| conversationId = cid; beforeCommitId = tryUInt64 p "beforeCommitId" |> Option.defaultValue 0UL; limit = tryInt p "limit" |> Option.defaultValue 100 |})
                    | None -> Error "history.request: missing conversationId"
                | Some "history.page" ->
                    match tryGuid p "conversationId" with
                    | Some cid ->
                        let items = match tryGet p "items" with Some (:? JsonArray as a) -> a | _ -> JsonArray()
                        let hasMore =
                            match tryGet p "hasMore" with
                            | Some v when v.GetValueKind() = JsonValueKind.True -> true
                            | _ -> false
                        Ok(HistoryPage {| conversationId = cid; beforeCommitId = tryUInt64 p "beforeCommitId" |> Option.defaultValue 0UL; items = items; hasMore = hasMore |})
                    | None -> Error "history.page: missing conversationId"
                | Some "command.accepted" ->
                    match tryGuid p "invocationId" with Some inv -> Ok(CommandAccepted {| invocationId = inv |}) | None -> Error "command.accepted: missing invocationId"
                | Some "command.committed" ->
                    match tryGuid p "invocationId" with
                    | Some inv -> Ok(CommandCommitted {| invocationId = inv; commandId = tryString p "commandId" |> Option.defaultValue ""; commitId = tryUInt64 p "commitId" |> Option.defaultValue 0UL |})
                    | None -> Error "command.committed: missing invocationId"
                | Some "command.rejected" ->
                    match tryGuid p "invocationId" with
                    | Some inv -> Ok(CommandRejected {| invocationId = inv; code = tryString p "code" |> Option.defaultValue ""; message = tryString p "message" |> Option.defaultValue "" |})
                    | None -> Error "command.rejected: missing invocationId"
                | Some "cursor.advanced" -> Ok(CursorAdvanced {| id = tryUInt64 p "id" |> Option.defaultValue 0UL |})
                | Some "authority.catch-up" ->
                    let items = match tryGet p "items" with Some (:? JsonArray as a) -> a | _ -> JsonArray()
                    Ok(AuthorityCatchUp {| fromCursor = tryUInt64 p "fromCursor" |> Option.defaultValue 0UL; toCommitId = tryUInt64 p "toCommitId" |> Option.defaultValue 0UL; items = items |})
                | Some "generation.delta" ->
                    match tryGuid p "conversationId", tryGuid p "generationId" with
                    | Some cid, Some gid ->
                        match tryGet p "payload" with
                        | Some payload -> Ok(GenerationDelta {| conversationId = cid; generationId = gid; payload = payload |})
                        | None -> Error "generation.delta: missing payload"
                    | _ -> Error "generation.delta: missing conversationId/generationId"
                | Some "generation.started" ->
                    match tryGuid p "conversationId", tryGuid p "generationId" with
                    | Some cid, Some gid -> Ok(GenerationStarted {| conversationId = cid; generationId = gid |})
                    | _ -> Error "generation.started: missing conversationId/generationId"
                | Some "generation.finished" ->
                    match tryGuid p "conversationId", tryGuid p "generationId" with
                    | Some cid, Some gid -> Ok(GenerationFinished {| conversationId = cid; generationId = gid; status = tryString p "status" |> Option.defaultValue "completed"; error = tryString p "error" |})
                    | _ -> Error "generation.finished: missing conversationId/generationId"
                | Some "generation.cancel" ->
                    match tryGuid p "conversationId", tryGuid p "generationId" with
                    | Some cid, Some gid -> Ok(GenerationCancel {| conversationId = cid; generationId = gid |})
                    | _ -> Error "generation.cancel: missing conversationId/generationId"
                | Some "attachment.begin" ->
                    match tryGuid p "attachmentId" with
                    | Some aid ->
                        Ok(AttachmentBegin {| attachmentId = aid; totalBytes = tryInt64 p "totalBytes" |> Option.defaultValue 0L; sha256 = tryString p "sha256" |> Option.defaultValue ""; mediaType = tryString p "mediaType" |> Option.defaultValue ""; fileName = tryString p "fileName" |> Option.defaultValue "" |})
                    | None -> Error "attachment.begin: missing attachmentId"
                | Some "attachment.chunk" ->
                    match tryGuid p "attachmentId" with
                    | Some aid ->
                        Ok(AttachmentChunk {| attachmentId = aid; index = tryInt p "index" |> Option.defaultValue 0; dataBase64 = tryString p "data" |> Option.defaultValue "" |})
                    | None -> Error "attachment.chunk: missing attachmentId"
                | Some "attachment.complete" ->
                    match tryGuid p "attachmentId" with
                    | Some aid -> Ok(AttachmentComplete {| attachmentId = aid; sha256 = tryString p "sha256" |> Option.defaultValue "" |})
                    | None -> Error "attachment.complete: missing attachmentId"
                | Some "attachment.committed" ->
                    match tryGuid p "attachmentId" with
                    | Some aid -> Ok(AttachmentCommitted {| attachmentId = aid; sha256 = tryString p "sha256" |> Option.defaultValue ""; size = tryInt64 p "size" |> Option.defaultValue 0L |})
                    | None -> Error "attachment.committed: missing attachmentId"
                | Some "attachment.aborted" ->
                    match tryGuid p "attachmentId" with
                    | Some aid -> Ok(AttachmentAborted {| attachmentId = aid; reason = tryString p "reason" |> Option.defaultValue "" |})
                    | None -> Error "attachment.aborted: missing attachmentId"
                | Some "attachment.download-request" -> Ok(AttachmentDownloadRequest {| sha256 = tryString p "sha256" |> Option.defaultValue "" |})
                | Some "attachment.download-begin" -> Ok(AttachmentDownloadBegin {| sha256 = tryString p "sha256" |> Option.defaultValue ""; size = tryInt64 p "size" |> Option.defaultValue 0L; mediaType = tryString p "mediaType" |> Option.defaultValue ""; fileName = tryString p "fileName" |> Option.defaultValue "" |})
                | Some "attachment.download-chunk" -> Ok(AttachmentDownloadChunk {| sha256 = tryString p "sha256" |> Option.defaultValue ""; index = tryInt p "index" |> Option.defaultValue 0; dataBase64 = tryString p "data" |> Option.defaultValue "" |})
                | Some "attachment.download-complete" -> Ok(AttachmentDownloadComplete {| sha256 = tryString p "sha256" |> Option.defaultValue "" |})
                | Some "config.changed" -> Ok(ConfigChanged {| reason = tryString p "reason" |> Option.defaultValue "" |})
                | Some "server.error" -> Ok(ServerError {| message = tryString p "message" |> Option.defaultValue "" |})
                | Some "protocol.ping" -> Ok Ping
                | Some "protocol.pong" -> Ok Pong
                | Some other -> Error(sprintf "unknown event type %s" other)
                | None -> Error "missing type"
            | _ -> Error "not a JSON object"
        with e ->
            Error(sprintf "event decode failed: %s" e.Message)
