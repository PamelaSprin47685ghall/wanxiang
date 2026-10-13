namespace Wanxiang.Core

open System

// CommitId、GenerationUsage、GenerationError 归 wanxiang2 Wire 包（线协议值类型）。

module Constants =
    /// 协议版本：客户端与主机必须完全相等。
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

    /// 提交来源默认值（诊断字段；主机单写者会覆盖为真实实例标识）。
    [<Literal>]
    let DefaultSource = "wanxiang"

    /// 测试/无主机场景下的哨兵 bootId：全零表示「未指定进程世代」。
    /// 真实提交由单写者赋真实 bootId（RFC 5848 语义）。
    let DefaultBootId = Guid.Empty

/// 单次生成用量（generation.finished 透传，不落 NDJSON）。
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
    | AttachmentTooLarge of limitBytes: int64 * actualBytes: int64
    | AttachmentHashMismatch of expected: string * actual: string
    | AttachmentIncomplete of attachmentId: Guid
    | ConfigRejected of message: string
    | AuthRejected of message: string
    | Cancelled of string

module WanxiangError =
    /// 字节数的人话写法：MiB 精度只为可读，不为对账。
    let private formatBytes (bytes: int64) : string =
        if bytes < 1024L then sprintf "%d B" bytes
        elif bytes < 1024L * 1024L then sprintf "%.1f KiB" (float bytes / 1024.0)
        else sprintf "%.1f MiB" (float bytes / (1024.0 * 1024.0))

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
        | AttachmentTooLarge (limitBytes, actualBytes) ->
            sprintf "附件过大：上限 %s，本次 %s。请压缩文件后重试，或调大 network.maxAttachmentBytes。" (formatBytes limitBytes) (formatBytes actualBytes)
        | AttachmentHashMismatch (e, a) -> sprintf "attachment hash mismatch: expected %s actual %s" e a
        | AttachmentIncomplete aid -> sprintf "attachment %O is incomplete" aid
        | ConfigRejected m -> m
        | AuthRejected m -> m
        | Cancelled m -> m
