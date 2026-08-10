namespace Wanxiang.Protocol

open System
open System.Text.Json.Nodes
open Wanxiang.Core

/// 线上协议事件（fire-and-forget 对称事件，决策 25）。
/// C→S 与 S→C 使用同一外壳；权限差异由事件类型决定。
type WireEvent =
    // ---- 握手与认证 ----
    | Hello of {| protocol: string; version: int; instanceId: string option |}
    | UpgradeRequired of {| serverVersion: int; clientVersion: int |}
    | AuthPresent of {| token: string |}
    | AuthAccepted of {| instanceId: string |}
    | AuthRejected of {| reason: string |}
    | PairingRequested of {| clientName: string option |}
    | PairingStarted of {| expiresInSeconds: int |}
    | PairingAttempted of {| code: string; clientName: string option |}
    | PairingSucceeded of {| token: string |}
    | PairingFailed of {| reason: string; frozen: bool; freezeMinutes: int |}
    // ---- 观察 ----
    | ObserveConversationList
    | UnobserveConversationList
    | ConversationListSnapshot of {| items: JsonArray; lastCommitId: CommitId |}
    | ObserveConversation of {| conversationId: Guid |}
    | UnobserveConversation of {| conversationId: Guid |}
    | ConversationSnapshot of {| conversationId: Guid; title: string; lastCommitId: CommitId; runtimeState: string; messages: JsonArray; snapshotEarliestCommitId: CommitId; snapshotHasMore: bool; config: SessionConfig |}
    | ConversationUpdated of {| conversationId: Guid; commitId: CommitId; change: JsonObject |}
    | MessageCommitted of {| conversationId: Guid; commitId: CommitId; payload: JsonNode |}
    // ---- 历史分页（Q127：按 commitID 反向分页，稳定 ID 作页边界）----
    | HistoryRequest of {| conversationId: Guid; beforeCommitId: CommitId; limit: int |}
    | HistoryPage of {| conversationId: Guid; beforeCommitId: CommitId; items: JsonArray; hasMore: bool |}
    // ---- 命令（C→S）与确认 ----
    | Command of ClientCommand
    | CommandAccepted of {| invocationId: Guid |}
    | CommandCommitted of {| invocationId: Guid; commandId: string; commitId: CommitId |}
    | CommandRejected of {| invocationId: Guid; code: string; message: string; requiredCommitId: CommitId option |}
    | CursorAdvanced of {| id: CommitId |}
    | AuthorityCatchUp of {| fromCursor: CommitId; toCommitId: CommitId; items: JsonArray |}
    // ---- 生成 ----
    | GenerationDelta of {| conversationId: Guid; generationId: Guid; payload: JsonNode |}
    | GenerationStarted of {| conversationId: Guid; generationId: Guid |}
    | GenerationFinished of {| conversationId: Guid; generationId: Guid; status: string; error: string option |}
    | GenerationCancel of {| conversationId: Guid; generationId: Guid |}
    // ---- 附件 ----
    | AttachmentBegin of {| attachmentId: Guid; totalBytes: int64; sha256: string; mediaType: string; fileName: string |}
    | AttachmentChunk of {| attachmentId: Guid; index: int; dataBase64: string |}
    | AttachmentComplete of {| attachmentId: Guid; sha256: string |}
    | AttachmentCommitted of {| attachmentId: Guid; sha256: string; size: int64 |}
    | AttachmentAborted of {| attachmentId: Guid; reason: string |}
    | AttachmentDownloadRequest of {| sha256: string |}
    | AttachmentDownloadBegin of {| sha256: string; size: int64; mediaType: string; fileName: string |}
    | AttachmentDownloadChunk of {| sha256: string; index: int; dataBase64: string |}
    | AttachmentDownloadComplete of {| sha256: string |}
    // ---- 配置与系统 ----
    | ConfigChanged of {| reason: string |}
    | ServerError of {| message: string |}
    | Ping
    | Pong

module WireEvent =

    let typeName (ev: WireEvent) : string =
        match ev with
        | Hello _ -> "protocol.hello"
        | UpgradeRequired _ -> "protocol.upgrade-required"
        | AuthPresent _ -> "auth.present"
        | AuthAccepted _ -> "auth.accepted"
        | AuthRejected _ -> "auth.rejected"
        | PairingRequested _ -> "pairing.requested"
        | PairingStarted _ -> "pairing.started"
        | PairingAttempted _ -> "pairing.attempted"
        | PairingSucceeded _ -> "pairing.succeeded"
        | PairingFailed _ -> "pairing.failed"
        | ObserveConversationList -> "conversation-list.observe"
        | UnobserveConversationList -> "conversation-list.unobserve"
        | ConversationListSnapshot _ -> "conversation-list.snapshot"
        | ObserveConversation _ -> "conversation.observe"
        | UnobserveConversation _ -> "conversation.unobserve"
        | ConversationSnapshot _ -> "conversation.snapshot"
        | HistoryRequest _ -> "history.request"
        | HistoryPage _ -> "history.page"
        | ConversationUpdated _ -> "conversation.updated"
        | MessageCommitted _ -> "conversation.message-committed"
        | Command c -> ClientCommand.commandType c
        | CommandAccepted _ -> "command.accepted"
        | CommandCommitted _ -> "command.committed"
        | CommandRejected _ -> "command.rejected"
        | CursorAdvanced _ -> "cursor.advanced"
        | AuthorityCatchUp _ -> "authority.catch-up"
        | GenerationDelta _ -> "generation.delta"
        | GenerationStarted _ -> "generation.started"
        | GenerationFinished _ -> "generation.finished"
        | GenerationCancel _ -> "generation.cancel"
        | AttachmentBegin _ -> "attachment.begin"
        | AttachmentChunk _ -> "attachment.chunk"
        | AttachmentComplete _ -> "attachment.complete"
        | AttachmentCommitted _ -> "attachment.committed"
        | AttachmentAborted _ -> "attachment.aborted"
        | AttachmentDownloadRequest _ -> "attachment.download-request"
        | AttachmentDownloadBegin _ -> "attachment.download-begin"
        | AttachmentDownloadChunk _ -> "attachment.download-chunk"
        | AttachmentDownloadComplete _ -> "attachment.download-complete"
        | ConfigChanged _ -> "config.changed"
        | ServerError _ -> "server.error"
        | Ping -> "protocol.ping"
        | Pong -> "protocol.pong"
