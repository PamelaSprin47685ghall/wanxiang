namespace Wanxiang.Core

open System

/// 全局连续提交序号（从 1 开始，跨 UTC 日期文件不归零，不允许空洞）。
type CommitId = uint64

module Constants =
    /// 协议版本：客户端与服务端必须完全相等。
    [<Literal>]
    let ProtocolVersion = 1

    /// 提交外壳格式版本。
    [<Literal>]
    let FormatVersion = 1

    /// 事件版本（每个事件类型独立的 data 结构版本，v1 永久固定）。
    [<Literal>]
    let EventVersion1 = 1

    /// 幂等命令前缀。
    [<Literal>]
    let CommandIdPrefix = "wanxiang.command.v1"

    /// WebSocket 业务端点路径
    [<Literal>]
    let WsPath = "/ws"

type WanxiangError =
    | ValidationError of string
    | StaleProjection of requiredCommitId: CommitId
    | CommandIdConflict of message: string
    | ConversationNotFound of Guid
    | ConversationDeleted of Guid
    | ConversationIdTaken of Guid
    | ForkParentNotFound of Guid
    | ForkPointInvalid of message: string
    | GenerationNotFound of conversationId: Guid * generationId: Guid
    | GenerationBusy of conversationId: Guid
    | NotAuthenticated
    | ProtocolMismatch of serverVersion: int * clientVersion: int
    | UnknownEventType of string
    | Poisoned of string
    | AttachmentTooLarge of maxBytes: int64
    | AttachmentHashMismatch of expected: string * actual: string
    | AttachmentIncomplete of attachmentId: Guid
    | ConfigRejected of message: string
    | AuthRejected of message: string
    | Cancelled of string

module WanxiangError =
    let code (err: WanxiangError) : string =
        match err with
        | ValidationError _ -> "validation-error"
        | StaleProjection _ -> "stale-projection"
        | CommandIdConflict _ -> "command-id-conflict"
        | ConversationNotFound _ -> "conversation-not-found"
        | ConversationDeleted _ -> "conversation-deleted"
        | ConversationIdTaken _ -> "conversation-id-taken"
        | ForkParentNotFound _ -> "fork-parent-not-found"
        | ForkPointInvalid _ -> "fork-point-invalid"
        | GenerationNotFound _ -> "generation-not-found"
        | GenerationBusy _ -> "generation-busy"
        | NotAuthenticated -> "not-authenticated"
        | ProtocolMismatch _ -> "protocol-mismatch"
        | UnknownEventType _ -> "unknown-event-type"
        | Poisoned _ -> "poisoned"
        | AttachmentTooLarge _ -> "attachment-too-large"
        | AttachmentHashMismatch _ -> "attachment-hash-mismatch"
        | AttachmentIncomplete _ -> "attachment-incomplete"
        | ConfigRejected _ -> "config-rejected"
        | AuthRejected _ -> "auth-rejected"
        | Cancelled _ -> "cancelled"

    let message (err: WanxiangError) : string =
        match err with
        | ValidationError m -> m
        | StaleProjection id -> sprintf "client state is stale; required commit id %d" id
        | CommandIdConflict m -> m
        | ConversationNotFound id -> sprintf "conversation %O not found" id
        | ConversationDeleted id -> sprintf "conversation %O is deleted" id
        | ConversationIdTaken id -> sprintf "conversation id %O already taken" id
        | ForkParentNotFound id -> sprintf "fork parent %O not found" id
        | ForkPointInvalid m -> m
        | GenerationNotFound (c, g) -> sprintf "generation %O not found in conversation %O" g c
        | GenerationBusy c -> sprintf "conversation %O already has an active generation" c
        | NotAuthenticated -> "not authenticated"
        | ProtocolMismatch (s, c) -> sprintf "protocol version mismatch: server=%d client=%d" s c
        | UnknownEventType t -> sprintf "unknown event type: %s" t
        | Poisoned m -> sprintf "poisoned: %s" m
        | AttachmentTooLarge maxBytes -> sprintf "attachment exceeds max size %d bytes" maxBytes
        | AttachmentHashMismatch (e, a) -> sprintf "attachment hash mismatch: expected %s actual %s" e a
        | AttachmentIncomplete aid -> sprintf "attachment %O is incomplete" aid
        | ConfigRejected m -> m
        | AuthRejected m -> m
        | Cancelled m -> m
