namespace Wanxiang.Protocol

open System
open System.Globalization
open System.Text.Encodings.Web
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

    let private parseUsage (p: JsonObject) : GenerationUsage option =
        match tryGet p "usage" with
        | Some (:? JsonObject as uo) ->
            Some
                { promptTokens = tryInt uo "promptTokens"
                  completionTokens = tryInt uo "completionTokens"
                  cachedTokens = tryInt uo "cachedTokens"
                  totalTokens = tryInt uo "totalTokens"
                  durationMs = tryInt64 uo "durationMs" }
        | _ -> None

    let private encodeGenerationError (e: GenerationError) : JsonObject =
        let o = JsonObject()
        o["code"] <- GenerationErrorKind.code e.kind
        o["message"] <- e.message
        match e.detail with Some d -> o["detail"] <- d | None -> ()
        o["retryable"] <- e.retryable
        match e.retryAfterSeconds with Some s -> o["retryAfterSeconds"] <- s | None -> ()
        o

    let private parseGenerationError (p: JsonObject) : GenerationError option =
        match tryGet p "error" with
        | Some (:? JsonObject as eo) ->
            let kind = tryString eo "code" |> Option.map GenerationErrorKind.ofCode |> Option.defaultValue UnknownFailure
            Some
                { kind = kind
                  message = tryString eo "message" |> Option.defaultValue "生成失败。"
                  detail = tryString eo "detail"
                  retryable =
                    (match tryGet eo "retryable" with
                     | Some v -> v.GetValueKind() = JsonValueKind.True
                     | None -> false)
                  retryAfterSeconds = tryInt eo "retryAfterSeconds" }
        | Some v when v.GetValueKind() = JsonValueKind.String ->
            // 兼容旧式纯文本 error（开发期日志/手写帧）
            Some(GenerationError.create UnknownFailure (v.GetValue<string>()))
        | _ -> None

    let private tryArray (o: JsonObject) (k: string) : JsonArray =
        match tryGet o k with
        | Some (:? JsonArray as a) -> a.DeepClone() :?> JsonArray
        | _ -> JsonArray()

    let private tryObject (o: JsonObject) (k: string) : JsonObject =
        match tryGet o k with
        | Some (:? JsonObject as jo) -> jo.DeepClone() :?> JsonObject
        | _ -> JsonObject()

    let private tryStringList (o: JsonObject) (k: string) : string list =
        match tryGet o k with
        | Some (:? JsonArray as a) ->
            [ for item in a do
                  if not (isNull item) && item.GetValueKind() = JsonValueKind.String then item.GetValue<string>() ]
        | _ -> []

    /// 时间戳解析。缺失或非法时回落到当前时刻——界面宁可显示一个近似时间，
    /// 也不该因为一个展示字段而丢掉整条消息。
    let private tryTimestamp (o: JsonObject) (k: string) : DateTimeOffset =
        match tryString o k with
        | Some raw ->
            match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind) with
            | true, value -> value
            | _ -> DateTimeOffset.UtcNow
        | None -> DateTimeOffset.UtcNow

    let private tryGuid (o: JsonObject) (k: string) : Guid option =
        match tryString o k with
        | Some s ->
            match Guid.TryParse s with
            | true, g -> Some g
            | _ -> None
        | None -> None

    let private putGuid (o: JsonObject) (k: string) (g: Guid) = o[k] <- g.ToString("D")

    // ---------- 编码 ----------

    /// 语义事件的业务载荷（不含 type 外壳）——CUSTOM 扁平承载的数据源。
    /// 身份字段用 AG-UI 命名：conversationId→threadId、generationId→runId、
    /// 消息边界 commitId→messageId。
    let payloadOf (ev: WireEvent) : string =
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
        | ObserveConversation d -> putGuid p "threadId" d.conversationId
        | UnobserveConversation d -> putGuid p "threadId" d.conversationId
        | ConversationSnapshot d ->
            putGuid p "threadId" d.conversationId
            p["title"] <- d.title
            p["lastCommitId"] <- d.lastCommitId
            p["runtimeState"] <- d.runtimeState
            match d.generationId with Some id -> putGuid p "runId" id | None -> ()
            p["messages"] <- d.messages.DeepClone()
            p["snapshotEarliestCommitId"] <- d.snapshotEarliestCommitId
            p["snapshotHasMore"] <- d.snapshotHasMore
            p["config"] <- CommitCodec.configToJson d.config
        | ConversationUpdated d ->
            putGuid p "threadId" d.conversationId
            p["commitId"] <- d.commitId
            p["change"] <- d.change.DeepClone()
        | MessageCommitted d ->
            putGuid p "threadId" d.conversationId
            p["messageId"] <- d.commitId
            p["committedAt"] <- d.committedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)
            p["content"] <- d.payload.DeepClone()
        | HistoryRequest d ->
            putGuid p "threadId" d.conversationId
            p["beforeCommitId"] <- d.beforeCommitId
            p["limit"] <- d.limit
        | HistoryPage d ->
            putGuid p "threadId" d.conversationId
            p["beforeCommitId"] <- d.beforeCommitId
            p["items"] <- d.items.DeepClone()
            p["hasMore"] <- d.hasMore
        | Command _ -> invalidOp "Command 事件使用 encodeCommand"
        | ConversationExportRead d ->
            putGuid p "exportId" d.exportId
            putGuid p "threadId" d.conversationId
            p["beforeCommitId"] <- d.beforeCommitId
            match d.atCommitId with Some id -> p["atCommitId"] <- id | None -> ()
        | ConversationExportPage d ->
            putGuid p "exportId" d.exportId
            putGuid p "threadId" d.conversationId
            p["atCommitId"] <- d.atCommitId
            p["beforeCommitId"] <- d.beforeCommitId
            p["title"] <- d.title
            p["items"] <- d.items.DeepClone()
            p["totalMessages"] <- d.totalMessages
            p["hasMore"] <- d.hasMore
        | ConversationExportFailed d ->
            putGuid p "exportId" d.exportId
            p["message"] <- d.message
        | SearchQuery d ->
            putGuid p "queryId" d.queryId
            p["query"] <- d.query
            p["includeArchived"] <- d.includeArchived
            p["offset"] <- d.offset
            p["limit"] <- d.limit
        | SearchResults d ->
            putGuid p "queryId" d.queryId
            p["query"] <- d.query
            p["offset"] <- d.offset
            p["items"] <- d.items.DeepClone()
            p["hasMore"] <- d.hasMore
            p["truncated"] <- d.truncated
            p["atCommitId"] <- d.atCommitId
        | SearchFailed d ->
            putGuid p "queryId" d.queryId
            p["message"] <- d.message
        | CommandAccepted d -> putGuid p "invocationId" d.invocationId
        | CommandCommitted d ->
            putGuid p "invocationId" d.invocationId
            p["commandId"] <- d.commandId
            p["commitId"] <- d.commitId
        | CommandRejected d ->
            putGuid p "invocationId" d.invocationId
            p["code"] <- d.code
            p["message"] <- d.message
            // 决策 36：stale-projection 拒绝必须携带 requiredCommitId，客户端据此追赶
            match d.requiredCommitId with
            | Some id -> p["requiredCommitId"] <- id
            | None -> ()
        | CursorAdvanced d -> p["id"] <- d.id
        | AuthorityCatchUp d ->
            p["fromCursor"] <- d.fromCursor
            p["toCommitId"] <- d.toCommitId
            p["items"] <- d.items.DeepClone()
        | GenerationDelta d ->
            putGuid p "threadId" d.conversationId
            putGuid p "runId" d.generationId
            p["content"] <- d.payload.DeepClone()
        | GenerationStarted d ->
            putGuid p "threadId" d.conversationId
            putGuid p "runId" d.generationId
            p["providerId"] <- d.providerId
            p["model"] <- d.model
        | GenerationFinished d ->
            putGuid p "threadId" d.conversationId
            putGuid p "runId" d.generationId
            p["status"] <- d.status
            match d.error with Some e -> p["error"] <- encodeGenerationError e | None -> ()
            match d.usage with
            | Some u ->
                let uo = JsonObject()
                match u.promptTokens with Some v -> uo["promptTokens"] <- v | None -> ()
                match u.completionTokens with Some v -> uo["completionTokens"] <- v | None -> ()
                match u.cachedTokens with Some v -> uo["cachedTokens"] <- v | None -> ()
                match u.totalTokens with Some v -> uo["totalTokens"] <- v | None -> ()
                match u.durationMs with Some v -> uo["durationMs"] <- v | None -> ()
                p["usage"] <- uo
            | None -> ()
        | GenerationCancel d ->
            putGuid p "threadId" d.conversationId
            putGuid p "runId" d.generationId
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
        | CatalogRequest -> ()
        | CatalogSnapshot d ->
            p["providers"] <- d.providers.DeepClone()
            p["tools"] <- d.tools.DeepClone()
            p["generation"] <- d.generation.DeepClone()
        | ProviderProbeRequest d ->
            putGuid p "requestId" d.requestId
            p["providerId"] <- d.providerId
        | ProviderProbeResult d ->
            putGuid p "requestId" d.requestId
            p["providerId"] <- d.providerId
            p["ok"] <- d.ok
            p["models"] <- d.models.DeepClone()
            match d.error with Some e -> p["error"] <- e | None -> ()
        | ConfigUpsertProvider d ->
            putGuid p "requestId" d.requestId
            p["provider"] <- d.provider.DeepClone()
        | ConfigDeleteProvider d ->
            putGuid p "requestId" d.requestId
            p["providerId"] <- d.providerId
        | ConfigUpsertMcp d ->
            putGuid p "requestId" d.requestId
            p["server"] <- d.server.DeepClone()
        | ConfigDeleteMcp d ->
            putGuid p "requestId" d.requestId
            p["serverId"] <- d.serverId
        | ConfigUpdateGeneration d ->
            putGuid p "requestId" d.requestId
            p["generation"] <- d.generation.DeepClone()
        | ConfigUpsertPreset d ->
            putGuid p "requestId" d.requestId
            p["preset"] <- d.preset.DeepClone()
        | ConfigDeletePreset d ->
            putGuid p "requestId" d.requestId
            p["presetId"] <- d.presetId
        | ConfigApplied d ->
            putGuid p "requestId" d.requestId
            p["ok"] <- d.ok
            if not (List.isEmpty d.errors) then
                p["errors"] <- JsonArray([| for e in d.errors -> JsonNode.op_Implicit e |])
        | ConfigUpdateTools d ->
            putGuid p "requestId" d.requestId
            p["tools"] <- d.tools.DeepClone()
        | ToolApprovalCapability d -> p["supported"] <- d.supported
        | ToolApprovalRequested d ->
            putGuid p "approvalId" d.approvalId
            putGuid p "threadId" d.conversationId
            putGuid p "runId" d.generationId
            p["callId"] <- d.callId
            p["toolName"] <- d.toolName
            p["summary"] <- d.summary
            p["arguments"] <- (if isNull d.arguments then null else d.arguments.DeepClone())
            p["policy"] <- d.policy
            p["expiresInSeconds"] <- d.expiresInSeconds
        | ToolApprovalDecision d ->
            putGuid p "approvalId" d.approvalId
            p["approved"] <- d.approved
        | ToolApprovalResolved d ->
            putGuid p "approvalId" d.approvalId
            putGuid p "threadId" d.conversationId
            p["status"] <- d.status
            p["reason"] <- d.reason
            p["alreadyHandled"] <- d.alreadyHandled
        | ConfigChanged d -> p["reason"] <- d.reason
        | ServerError d -> p["message"] <- d.message
        | HostConfigRejected d ->
            p["summary"] <- d.summary
            p["errors"] <- JsonArray([| for e in d.errors -> JsonNode.op_Implicit e |])
        | DoctorRequest d -> putGuid p "requestId" d.requestId
        | DoctorReport d ->
            putGuid p "requestId" d.requestId
            p["ok"] <- d.ok
            p["checks"] <- d.checks.DeepClone()
        | Ping -> ()
        | Pong -> ()
        | PairingInvite -> ()
        | PairingInviteIssued d ->
            p["code"] <- d.code
            p["expiresAt"] <- d.expiresAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)
            p["expiresInSeconds"] <- d.expiresInSeconds
        | PairingInviteRejected d -> p["reason"] <- d.reason
        | HostCapabilities d ->
            p["hostVersion"] <- d.hostVersion
            p["features"] <- JsonArray([| for f in d.features -> JsonNode.op_Implicit f |])
        | DevicesRequest -> ()
        | DevicesSnapshot d ->
            p["items"] <- d.items.DeepClone()
            p["canInvite"] <- d.canInvite
        | ProviderRateLimitNotice d ->
            putGuid p "threadId" d.conversationId
            putGuid p "runId" d.generationId
            p["providerId"] <- d.providerId
            d.limit.remainingRequests |> Option.iter (fun v -> p["remainingRequests"] <- JsonValue.Create v)
            d.limit.remainingTokens |> Option.iter (fun v -> p["remainingTokens"] <- JsonValue.Create v)
            d.limit.resetSeconds |> Option.iter (fun v -> p["resetSeconds"] <- JsonValue.Create v)
            d.limit.retryAfterSeconds |> Option.iter (fun v -> p["retryAfterSeconds"] <- JsonValue.Create v)
        | GenerationPartial d ->
            putGuid p "threadId" d.conversationId
            putGuid p "runId" d.generationId
            p["message"] <- d.message.DeepClone()
            match d.error with Some e -> p["error"] <- encodeGenerationError e | None -> ()
        | DeviceRename d ->
            putGuid p "requestId" d.requestId
            p["deviceId"] <- d.deviceId
            p["name"] <- d.name
        | DeviceRevoke d ->
            putGuid p "requestId" d.requestId
            p["deviceId"] <- d.deviceId
        p.ToJsonString(JsonSerializerOptions(JsonSerializerDefaults.General))

    let encode (ev: WireEvent) : string =
        let o = JsonObject()
        o["type"] <- WireEvent.typeName ev
        let payloadJson = payloadOf ev
        if not (String.IsNullOrEmpty payloadJson) && payloadJson <> "{}" then
            o["payload"] <- JsonNode.Parse(payloadJson).DeepClone()
        o.ToJsonString(JsonSerializerOptions(JsonSerializerDefaults.General, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))

    /// 编码客户端写命令（Command 事件专用路径）。
    let encodeCommand (cmd: ClientCommand) : string =
        let o = JsonObject()
        o["type"] <- ClientCommand.commandType cmd
        let p = JsonObject()
        let inv = ClientCommand.invocationId cmd
        putGuid p "invocationId" inv
        match cmd with
        | CreateConversation d ->
            putGuid p "threadId" d.conversationId
            p["title"] <- d.title
            p["config"] <- CommitCodec.configToJson d.config
        | ForkConversation d ->
            putGuid p "threadId" d.conversationId
            putGuid p "parentConversationId" d.parentConversationId
            match d.forkAfterId with Some id -> p["forkAfterId"] <- id | None -> ()
            p["config"] <- CommitCodec.configToJson d.config
            p["message"] <- d.editedMessageJson.DeepClone()
        | SendUserMessage d ->
            putGuid p "threadId" d.conversationId
            p["message"] <- d.messageJson.DeepClone()
        | RenameConversation d ->
            putGuid p "threadId" d.conversationId
            p["title"] <- d.title
        | DeleteConversation d -> putGuid p "threadId" d.conversationId
        | DeleteMessage d ->
            putGuid p "threadId" d.conversationId
            p["messageCommitId"] <- d.messageCommitId
        | UpdateConversationConfig d ->
            putGuid p "threadId" d.conversationId
            p["config"] <- CommitCodec.configToJson d.config
        | SetConversationFlags d ->
            putGuid p "threadId" d.conversationId
            p["pinned"] <- d.pinned
            p["archived"] <- d.archived
        | RegenerateResponse d -> putGuid p "threadId" d.conversationId
        | ContinueResponse d ->
            putGuid p "threadId" d.conversationId
            p["message"] <- d.messageJson.DeepClone()
        | ImportConversation d ->
            putGuid p "threadId" d.conversationId
            p["title"] <- d.title
            p["config"] <- CommitCodec.configToJson d.config
            p["messages"] <- d.messages.DeepClone()
        o["payload"] <- p
        o.ToJsonString(JsonSerializerOptions(JsonSerializerDefaults.General, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))

    // ---------- 解码 ----------

    /// `tryDecode` 对未知事件类型返回的错误前缀（55.8 R1：未知类型须被调用方忽略，而不是当作畸形）。
    [<Literal>]
    let unknownEventTypePrefix = "unknown event type "

    /// 命令解码对非命令类型返回的错误文本。
    [<Literal>]
    let notACommandError = "not a command event"

    /// 命令/事件两条解码路径是否都只是“不认识这个类型”（而非已知类型的畸形载荷）。
    let isUnknownType (cmdErr: string) (evErr: string) : bool =
        cmdErr = notACommandError && evErr.StartsWith unknownEventTypePrefix

    /// 从 JSON 文本解码命令事件（C→S）。
    let tryDecodeCommand (jsonText: string) : Result<ClientCommand, string> =
        try
            match JsonNode.Parse jsonText with
            | :? JsonObject as o ->
                match tryGet o "type" with
                | None -> Error "missing type"
                | Some v when v.GetValueKind() <> JsonValueKind.String -> Error "missing type"
                | _ ->
                match tryString o "type" with
                | Some "conversation.create" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId" with
                        | Some inv, Some cid ->
                            let title = tryString p "title" |> Option.defaultValue ""
                            let cfg =
                                match tryGet p "config" with
                                | Some (:? JsonObject as c) -> CommitCodec.configFromJson c
                                | _ -> SessionConfig.empty
                            Ok(CreateConversation {| invocationId = inv; conversationId = cid; title = title; config = cfg |})
                        | _ -> Error "conversation.create: missing invocationId/threadId"
                    | _ -> Error "conversation.create: missing payload"
                | Some "conversation.fork" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId" with
                        | Some inv, Some cid ->
                            match tryGuid p "parentThreadId" |> Option.orElse (tryGuid p "parentConversationId") with
                            | Some pid ->
                                let after = match tryUInt64 p "forkAfterId" with Some v -> Some v | None -> None
                                let cfg =
                                    match tryGet p "config" with
                                    | Some (:? JsonObject as c) -> CommitCodec.configFromJson c
                                    | _ -> SessionConfig.empty
                                match tryGet p "editedMessage" |> Option.orElse (tryGet p "message") with
                                | Some msg ->
                                    Ok(ForkConversation {| invocationId = inv; conversationId = cid; parentConversationId = pid; forkAfterId = after; config = cfg; editedMessageJson = msg.DeepClone() |})
                                | None -> Error "conversation.fork: missing editedMessage"
                            | None -> Error "conversation.fork: missing parentThreadId"
                        | _ -> Error "conversation.fork: missing invocationId/threadId"
                    | _ -> Error "conversation.fork: missing payload"
                | Some "chat.user-message.enqueue" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId" with
                        | Some inv, Some cid ->
                            match tryGet p "message" with
                            | Some msg -> Ok(SendUserMessage {| invocationId = inv; conversationId = cid; messageJson = msg.DeepClone() |})
                            | None -> Error "chat.user-message.enqueue: missing message"
                        | _ -> Error "chat.user-message.enqueue: missing invocationId/threadId"
                    | _ -> Error "chat.user-message.enqueue: missing payload"
                | Some "conversation.rename" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId" with
                        | Some inv, Some cid ->
                            let title = tryString p "title" |> Option.defaultValue ""
                            Ok(RenameConversation {| invocationId = inv; conversationId = cid; title = title |})
                        | _ -> Error "conversation.rename: missing invocationId/threadId"
                    | _ -> Error "conversation.rename: missing payload"
                | Some "conversation.delete" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId" with
                        | Some inv, Some cid -> Ok(DeleteConversation {| invocationId = inv; conversationId = cid |})
                        | _ -> Error "conversation.delete: missing invocationId/threadId"
                    | _ -> Error "conversation.delete: missing payload"
                | Some "message.delete" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId", tryUInt64 p "messageCommitId" with
                        | Some inv, Some cid, Some mid -> Ok(DeleteMessage {| invocationId = inv; conversationId = cid; messageCommitId = mid |})
                        | Some _, Some _, None -> Error "message.delete: missing messageCommitId"
                        | _ -> Error "message.delete: missing invocationId/threadId"
                    | _ -> Error "message.delete: missing payload"
                | Some "conversation.config-update" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId" with
                        | Some inv, Some cid ->
                            let cfg =
                                match tryGet p "config" with
                                | Some (:? JsonObject as c) -> CommitCodec.configFromJson c
                                | _ -> SessionConfig.empty
                            Ok(UpdateConversationConfig {| invocationId = inv; conversationId = cid; config = cfg |})
                        | _ -> Error "conversation.config-update: missing invocationId/threadId"
                    | _ -> Error "conversation.config-update: missing payload"
                | Some "conversation.flags-set" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId" with
                        | Some inv, Some cid ->
                            let flag k =
                                match tryGet p k with
                                | Some v -> v.GetValueKind() = JsonValueKind.True
                                | None -> false
                            Ok(SetConversationFlags {| invocationId = inv; conversationId = cid; pinned = flag "pinned"; archived = flag "archived" |})
                        | _ -> Error "conversation.flags-set: missing invocationId/threadId"
                    | _ -> Error "conversation.flags-set: missing payload"
                | Some "chat.regenerate" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId" with
                        | Some inv, Some cid -> Ok(RegenerateResponse {| invocationId = inv; conversationId = cid |})
                        | _ -> Error "chat.regenerate: missing invocationId/threadId"
                    | _ -> Error "chat.regenerate: missing payload"
                | Some "chat.continue" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId", tryGet p "message" with
                        | Some inv, Some cid, Some (:? JsonObject as m) ->
                            Ok(ContinueResponse {| invocationId = inv; conversationId = cid; messageJson = m.DeepClone() |})
                        | Some _, Some _, _ -> Error "chat.continue: missing message"
                        | _ -> Error "chat.continue: missing invocationId/threadId"
                    | _ -> Error "chat.continue: missing payload"
                | Some "conversation.import" ->
                    match tryGet o "payload" with
                    | Some (:? JsonObject as p) ->
                        match tryGuid p "invocationId", tryGuid p "threadId", tryGet p "messages" with
                        | Some inv, Some cid, Some (:? JsonArray as msgs) ->
                            let title = tryString p "title" |> Option.defaultValue ""
                            let cfg =
                                match tryGet p "config" with
                                | Some (:? JsonObject as c) -> CommitCodec.configFromJson c
                                | _ -> SessionConfig.empty
                            Ok(ImportConversation {| invocationId = inv; conversationId = cid; title = title; config = cfg; messages = msgs.DeepClone().AsArray() |})
                        | Some _, Some _, _ -> Error "conversation.import: missing messages"
                        | _ -> Error "conversation.import: missing invocationId/threadId"
                    | _ -> Error "conversation.import: missing payload"
                | _ -> Error notACommandError
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
                    match tryGuid p "threadId" with Some cid -> Ok(ObserveConversation {| conversationId = cid |}) | None -> Error "conversation.observe: missing threadId"
                | Some "conversation.unobserve" ->
                    match tryGuid p "threadId" with Some cid -> Ok(UnobserveConversation {| conversationId = cid |}) | None -> Error "conversation.unobserve: missing threadId"
                | Some "conversation-list.snapshot" ->
                    let items = match tryGet p "items" with Some (:? JsonArray as a) -> a | _ -> JsonArray()
                    Ok(ConversationListSnapshot {| items = items; lastCommitId = tryUInt64 p "lastCommitId" |> Option.defaultValue 0UL |})
                | Some "conversation.snapshot" ->
                    match tryGuid p "threadId" with
                    | Some cid ->
                        let msgs = match tryGet p "messages" with Some (:? JsonArray as a) -> a | _ -> JsonArray()
                        let hasMore =
                            match tryGet p "snapshotHasMore" with
                            | Some v when v.GetValueKind() = JsonValueKind.True -> true
                            | _ -> false
                        let cfg =
                            match tryGet p "config" with
                            | Some (:? JsonObject as c) -> CommitCodec.configFromJson c
                            | _ -> SessionConfig.empty
                        Ok(ConversationSnapshot {| conversationId = cid; title = tryString p "title" |> Option.defaultValue ""; lastCommitId = tryUInt64 p "lastCommitId" |> Option.defaultValue 0UL; runtimeState = tryString p "runtimeState" |> Option.defaultValue "idle"; generationId = tryGuid p "runId"; messages = msgs; snapshotEarliestCommitId = tryUInt64 p "snapshotEarliestCommitId" |> Option.defaultValue 0UL; snapshotHasMore = hasMore; config = cfg |})
                    | None -> Error "conversation.snapshot: missing threadId"
                | Some "conversation.updated" ->
                    match tryGuid p "threadId" with
                    | Some cid ->
                        let change = match tryGet p "change" with Some (:? JsonObject as c) -> c | _ -> JsonObject()
                        Ok(ConversationUpdated {| conversationId = cid; commitId = tryUInt64 p "commitId" |> Option.defaultValue 0UL; change = change |})
                    | None -> Error "conversation.updated: missing threadId"
                | Some "conversation.message-committed" ->
                    match tryGuid p "threadId" with
                    | Some cid ->
                        match tryGet p "content" with
                        | Some payload ->
                            Ok(MessageCommitted
                                {| conversationId = cid
                                   commitId = tryUInt64 p "messageId" |> Option.defaultValue 0UL
                                   committedAt = tryTimestamp p "committedAt"
                                   payload = payload |})
                        | None -> Error "conversation.message-committed: missing payload"
                    | None -> Error "conversation.message-committed: missing threadId"
                | Some "history.request" ->
                    match tryGuid p "threadId" with
                    | Some cid ->
                        Ok(HistoryRequest {| conversationId = cid; beforeCommitId = tryUInt64 p "beforeCommitId" |> Option.defaultValue 0UL; limit = tryInt p "limit" |> Option.defaultValue 100 |})
                    | None -> Error "history.request: missing threadId"
                | Some "history.page" ->
                    match tryGuid p "threadId" with
                    | Some cid ->
                        let items = match tryGet p "items" with Some (:? JsonArray as a) -> a | _ -> JsonArray()
                        let hasMore =
                            match tryGet p "hasMore" with
                            | Some v when v.GetValueKind() = JsonValueKind.True -> true
                            | _ -> false
                        Ok(HistoryPage {| conversationId = cid; beforeCommitId = tryUInt64 p "beforeCommitId" |> Option.defaultValue 0UL; items = items; hasMore = hasMore |})
                    | None -> Error "history.page: missing threadId"
                | Some "command.accepted" ->
                    match tryGuid p "invocationId" with Some inv -> Ok(CommandAccepted {| invocationId = inv |}) | None -> Error "command.accepted: missing invocationId"
                | Some "conversation.export-read" ->
                    let at = tryUInt64 p "atCommitId"
                    let validAt = (tryGet p "atCommitId").IsNone || at.IsSome
                    match tryGuid p "exportId", tryGuid p "threadId", tryUInt64 p "beforeCommitId" with
                    | Some exportId, Some conversationId, Some before when validAt ->
                        Ok(ConversationExportRead { exportId = exportId; conversationId = conversationId; atCommitId = at; beforeCommitId = before })
                    | _ -> Error "conversation.export-read: invalid identity or boundary"
                | Some "conversation.export-page" ->
                    match tryGuid p "exportId", tryGuid p "threadId", tryUInt64 p "atCommitId", tryUInt64 p "beforeCommitId", tryInt p "totalMessages", tryString p "title", tryGet p "items", tryGet p "hasMore" with
                    | Some exportId, Some conversationId, Some at, Some before, Some count, Some title, Some (:? JsonArray as items), Some more
                        when count >= 0 && (more.GetValueKind() = JsonValueKind.True || more.GetValueKind() = JsonValueKind.False) ->
                        Ok(ConversationExportPage
                            { exportId = exportId; conversationId = conversationId; atCommitId = at; beforeCommitId = before
                              title = title; items = items; totalMessages = count; hasMore = more.GetValue<bool>() })
                    | _ -> Error "conversation.export-page: incomplete or invalid page"
                | Some "conversation.export-failed" ->
                    match tryGuid p "exportId", tryString p "message" with
                    | Some exportId, Some message -> Ok(ConversationExportFailed {| exportId = exportId; message = message |})
                    | _ -> Error "conversation.export-failed: missing exportId/message"
                | Some "search.query" ->
                    match tryGuid p "queryId", tryString p "query" with
                    | Some qid, Some q ->
                        Ok(SearchQuery
                            { queryId = qid; query = q
                              includeArchived = (match tryGet p "includeArchived" with Some v -> v.GetValueKind() = JsonValueKind.True | None -> false)
                              offset = tryInt p "offset" |> Option.defaultValue 0 |> max 0
                              limit = tryInt p "limit" |> Option.defaultValue SearchLimits.defaultPageSize })
                    | _ -> Error "search.query: missing queryId/query"
                | Some "search.results" ->
                    match tryGuid p "queryId", tryGet p "items" with
                    | Some qid, Some (:? JsonArray as items) ->
                        let flag k = match tryGet p k with Some v -> v.GetValueKind() = JsonValueKind.True | None -> false
                        Ok(SearchResults
                            { queryId = qid
                              query = tryString p "query" |> Option.defaultValue ""
                              offset = tryInt p "offset" |> Option.defaultValue 0
                              items = items
                              hasMore = flag "hasMore"
                              truncated = flag "truncated"
                              atCommitId = tryUInt64 p "atCommitId" |> Option.defaultValue 0UL })
                    | _ -> Error "search.results: missing queryId/items"
                | Some "search.failed" ->
                    match tryGuid p "queryId" with
                    | Some qid -> Ok(SearchFailed {| queryId = qid; message = tryString p "message" |> Option.defaultValue "" |})
                    | None -> Error "search.failed: missing queryId"
                | Some "command.committed" ->
                    match tryGuid p "invocationId" with
                    | Some inv -> Ok(CommandCommitted {| invocationId = inv; commandId = tryString p "commandId" |> Option.defaultValue ""; commitId = tryUInt64 p "commitId" |> Option.defaultValue 0UL |})
                    | None -> Error "command.committed: missing invocationId"
                | Some "command.rejected" ->
                    match tryGuid p "invocationId" with
                    | Some inv ->
                        Ok(CommandRejected
                            {| invocationId = inv
                               code = tryString p "code" |> Option.defaultValue ""
                               message = tryString p "message" |> Option.defaultValue ""
                               requiredCommitId = tryUInt64 p "requiredCommitId" |})
                    | None -> Error "command.rejected: missing invocationId"
                | Some "cursor.advanced" -> Ok(CursorAdvanced {| id = tryUInt64 p "id" |> Option.defaultValue 0UL |})
                | Some "authority.catch-up" ->
                    let items = match tryGet p "items" with Some (:? JsonArray as a) -> a | _ -> JsonArray()
                    Ok(AuthorityCatchUp {| fromCursor = tryUInt64 p "fromCursor" |> Option.defaultValue 0UL; toCommitId = tryUInt64 p "toCommitId" |> Option.defaultValue 0UL; items = items |})
                | Some "generation.delta" ->
                    match tryGuid p "threadId", tryGuid p "runId" with
                    | Some cid, Some gid ->
                        match tryGet p "content" with
                        | Some payload -> Ok(GenerationDelta {| conversationId = cid; generationId = gid; payload = payload |})
                        | None -> Error "generation.delta: missing payload"
                    | _ -> Error "generation.delta: missing threadId/runId"
                | Some "generation.started" ->
                    match tryGuid p "threadId", tryGuid p "runId" with
                    | Some cid, Some gid ->
                        Ok(GenerationStarted
                            {| conversationId = cid
                               generationId = gid
                               providerId = tryString p "providerId" |> Option.defaultValue ""
                               model = tryString p "model" |> Option.defaultValue "" |})
                    | _ -> Error "generation.started: missing threadId/runId"
                | Some "generation.finished" ->
                    match tryGuid p "threadId", tryGuid p "runId" with
                    | Some cid, Some gid ->
                        Ok(GenerationFinished {| conversationId = cid; generationId = gid; status = tryString p "status" |> Option.defaultValue "completed"; error = parseGenerationError p; usage = parseUsage p |})
                    | _ -> Error "generation.finished: missing threadId/runId"
                | Some "generation.cancel" ->
                    match tryGuid p "threadId", tryGuid p "runId" with
                    | Some cid, Some gid -> Ok(GenerationCancel {| conversationId = cid; generationId = gid |})
                    | _ -> Error "generation.cancel: missing threadId/runId"
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
                | Some "catalog.request" -> Ok CatalogRequest
                | Some "catalog.snapshot" ->
                    Ok(CatalogSnapshot {| providers = tryArray p "providers"; tools = tryArray p "tools"; generation = tryObject p "generation" |})
                | Some "provider.probe" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(ProviderProbeRequest {| requestId = rid; providerId = tryString p "providerId" |> Option.defaultValue "" |})
                    | None -> Error "provider.probe: missing requestId"
                | Some "provider.probe-result" ->
                    match tryGuid p "requestId" with
                    | Some rid ->
                        Ok(ProviderProbeResult
                            {| requestId = rid
                               providerId = tryString p "providerId" |> Option.defaultValue ""
                               ok = (match tryGet p "ok" with Some v -> v.GetValueKind() = JsonValueKind.True | None -> false)
                               models = tryArray p "models"
                               error = tryString p "error" |})
                    | None -> Error "provider.probe-result: missing requestId"
                | Some "config.provider-upsert" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(ConfigUpsertProvider {| requestId = rid; provider = tryObject p "provider" |})
                    | None -> Error "config.provider-upsert: missing requestId"
                | Some "config.provider-delete" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(ConfigDeleteProvider {| requestId = rid; providerId = tryString p "providerId" |> Option.defaultValue "" |})
                    | None -> Error "config.provider-delete: missing requestId"
                | Some "config.mcp-upsert" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(ConfigUpsertMcp {| requestId = rid; server = tryObject p "server" |})
                    | None -> Error "config.mcp-upsert: missing requestId"
                | Some "config.mcp-delete" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(ConfigDeleteMcp {| requestId = rid; serverId = tryString p "serverId" |> Option.defaultValue "" |})
                    | None -> Error "config.mcp-delete: missing requestId"
                | Some "config.generation-update" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(ConfigUpdateGeneration {| requestId = rid; generation = tryObject p "generation" |})
                    | None -> Error "config.generation-update: missing requestId"
                | Some "config.preset-upsert" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(ConfigUpsertPreset {| requestId = rid; preset = tryObject p "preset" |})
                    | None -> Error "config.preset-upsert: missing requestId"
                | Some "config.preset-delete" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(ConfigDeletePreset {| requestId = rid; presetId = tryString p "presetId" |> Option.defaultValue "" |})
                    | None -> Error "config.preset-delete: missing requestId"
                | Some "config.applied" ->
                    match tryGuid p "requestId" with
                    | Some rid ->
                        Ok(ConfigApplied
                            {| requestId = rid
                               ok = (match tryGet p "ok" with Some v -> v.GetValueKind() = JsonValueKind.True | None -> false)
                               errors = tryStringList p "errors" |})
                    | None -> Error "config.applied: missing requestId"
                | Some "config.tools-update" ->
                    match tryGuid p "requestId" with
                    | Some rid ->
                        match tryGet p "tools" with
                        | Some (:? JsonObject as o) -> Ok(ConfigUpdateTools {| requestId = rid; tools = o.DeepClone() :?> JsonObject |})
                        | _ -> Error "config.tools-update: tools must be an object"
                    | None -> Error "config.tools-update: missing requestId"
                | Some "tool.approval-capability" ->
                    Ok(ToolApprovalCapability {| supported = (match tryGet p "supported" with Some v -> v.GetValueKind() <> JsonValueKind.False | None -> true) |})
                | Some "tool.approval-requested" ->
                    match tryGuid p "approvalId", tryGuid p "threadId", tryGuid p "runId" with
                    | Some aid, Some cid, Some gid ->
                        Ok(ToolApprovalRequested
                            {| approvalId = aid
                               conversationId = cid
                               generationId = gid
                               callId = tryString p "callId" |> Option.defaultValue ""
                               toolName = tryString p "toolName" |> Option.defaultValue ""
                               summary = tryString p "summary" |> Option.defaultValue ""
                               arguments = (match tryGet p "arguments" with Some v -> v.DeepClone() | None -> null)
                               policy = tryString p "policy" |> Option.defaultValue "always"
                               expiresInSeconds = (match tryGet p "expiresInSeconds" with Some v when v.GetValueKind() = JsonValueKind.Number -> v.GetValue<int>() | _ -> 0) |})
                    | _ -> Error "tool.approval-requested: missing approvalId/threadId/runId"
                | Some "tool.approval-decision" ->
                    match tryGuid p "approvalId" with
                    | Some aid ->
                        Ok(ToolApprovalDecision {| approvalId = aid; approved = (match tryGet p "approved" with Some v -> v.GetValueKind() = JsonValueKind.True | None -> false) |})
                    | None -> Error "tool.approval-decision: missing approvalId"
                | Some "tool.approval-resolved" ->
                    match tryGuid p "approvalId" with
                    | Some aid ->
                        Ok(ToolApprovalResolved
                            {| approvalId = aid
                               conversationId = tryGuid p "threadId" |> Option.defaultValue Guid.Empty
                               status = tryString p "status" |> Option.defaultValue ""
                               reason = tryString p "reason" |> Option.defaultValue ""
                               alreadyHandled = (match tryGet p "alreadyHandled" with Some v -> v.GetValueKind() = JsonValueKind.True | None -> false) |})
                    | None -> Error "tool.approval-resolved: missing approvalId"
                | Some "config.changed" -> Ok(ConfigChanged {| reason = tryString p "reason" |> Option.defaultValue "" |})
                | Some "server.error" -> Ok(ServerError {| message = tryString p "message" |> Option.defaultValue "" |})
                | Some "config.rejected" ->
                    Ok(HostConfigRejected
                        {| summary = tryString p "summary" |> Option.defaultValue ""
                           errors = tryStringList p "errors" |})
                | Some "doctor.request" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(DoctorRequest {| requestId = rid |})
                    | None -> Error "doctor.request: missing requestId"
                | Some "doctor.report" ->
                    match tryGuid p "requestId" with
                    | Some rid ->
                        Ok(DoctorReport
                            {| requestId = rid
                               ok = (match tryGet p "ok" with Some v -> v.GetValueKind() = JsonValueKind.True | None -> false)
                               checks = tryArray p "checks" |})
                    | None -> Error "doctor.report: missing requestId"
                | Some "protocol.ping" -> Ok Ping
                | Some "protocol.pong" -> Ok Pong
                | Some "pairing.invite" -> Ok PairingInvite
                | Some "pairing.invite-issued" ->
                    Ok(PairingInviteIssued
                        {| code = tryString p "code" |> Option.defaultValue ""
                           expiresAtUtc = tryTimestamp p "expiresAt"
                           expiresInSeconds = tryInt p "expiresInSeconds" |> Option.defaultValue 300 |})
                | Some "host.capabilities" ->
                    Ok(HostCapabilities {| hostVersion = tryString p "hostVersion" |> Option.defaultValue ""; features = tryStringList p "features" |})
                | Some "pairing.invite-rejected" -> Ok(PairingInviteRejected {| reason = tryString p "reason" |> Option.defaultValue "" |})
                | Some "devices.request" -> Ok DevicesRequest
                | Some "devices.snapshot" ->
                    Ok(DevicesSnapshot
                        {| items = tryArray p "items"
                           canInvite = (match tryGet p "canInvite" with Some v -> v.GetValueKind() = JsonValueKind.True | None -> false) |})
                | Some "device.rename" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(DeviceRename {| requestId = rid; deviceId = tryString p "deviceId" |> Option.defaultValue ""; name = tryString p "name" |> Option.defaultValue "" |})
                    | None -> Error "device.rename: missing requestId"
                | Some "device.revoke" ->
                    match tryGuid p "requestId" with
                    | Some rid -> Ok(DeviceRevoke {| requestId = rid; deviceId = tryString p "deviceId" |> Option.defaultValue "" |})
                    | None -> Error "device.revoke: missing requestId"
                | Some "provider.rate-limit" ->
                    match tryGuid p "threadId", tryGuid p "runId" with
                    | Some cid, Some gid ->
                        let nonNeg key = tryInt p key |> Option.filter (fun v -> v >= 0)
                        Ok(ProviderRateLimitNotice
                            {| conversationId = cid
                               generationId = gid
                               providerId = tryString p "providerId" |> Option.defaultValue ""
                               limit =
                                 { remainingRequests = nonNeg "remainingRequests"
                                   remainingTokens = nonNeg "remainingTokens"
                                   resetSeconds = nonNeg "resetSeconds"
                                   retryAfterSeconds = nonNeg "retryAfterSeconds" } |})
                    | _ -> Error "provider.rate-limit: missing threadId/runId"
                | Some "generation.partial" ->
                    match tryGuid p "threadId", tryGuid p "runId", tryGet p "message" with
                    | Some cid, Some gid, Some msg ->
                        Ok(GenerationPartial {| conversationId = cid; generationId = gid; message = msg.DeepClone(); error = parseGenerationError p |})
                    | _ -> Error "generation.partial: missing threadId/runId/message"
                | Some other -> Error(unknownEventTypePrefix + other)
                | None -> Error "missing type"
            | _ -> Error "not a JSON object"
        with e ->
            Error(sprintf "event decode failed: %s" e.Message)
