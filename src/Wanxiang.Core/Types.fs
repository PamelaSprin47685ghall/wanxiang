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

/// 单次生成用量（generation.finished 透传，不落 NDJSON）。
type GenerationUsage = {
    promptTokens: int option
    completionTokens: int option
    cachedTokens: int option
    totalTokens: int option
    durationMs: int64 option
}

module GenerationUsage =

    let empty =
        { promptTokens = None
          completionTokens = None
          cachedTokens = None
          totalTokens = None
          durationMs = None }

    let formatSummary (u: GenerationUsage) : string option =
        let total =
            match u.totalTokens with
            | Some t -> Some t
            | None ->
                match u.promptTokens, u.completionTokens with
                | Some p, Some c -> Some(p + c)
                | _ -> None
        match total with
        | None -> None
        | Some t ->
            let tok =
                if t >= 1000 then sprintf "%.1fk tok" (float t / 1000.0)
                else sprintf "%d tok" t
            match u.durationMs with
            | Some ms when ms >= 1000L -> Some(sprintf "%s · %.1fs" tok (float ms / 1000.0))
            | Some ms -> Some(sprintf "%s · %dms" tok (int ms))
            | None -> Some tok

    let formatDetail (u: GenerationUsage) : string =
        let part label v =
            match v with
            | Some n -> sprintf "%s %d" label n
            | None -> ""
        let parts =
            [| part "输入" u.promptTokens
               part "输出" u.completionTokens
               part "缓存" u.cachedTokens
               part "合计" u.totalTokens |]
            |> Array.filter (fun s -> s <> "")
        let body = if parts.Length = 0 then "暂无 token 数据" else String.Join(" · ", parts)
        match u.durationMs with
        | Some ms when ms >= 1000L -> body + sprintf " · 耗时 %.1fs" (float ms / 1000.0)
        | Some ms -> body + sprintf " · 耗时 %dms" (int ms)
        | None -> body

/// 生成失败的结构化分类。决定 UI 如何呈现、是否给「重试」入口、
/// 以及提示用户该去改哪里（密钥 / 模型 / 网络 / 内容）。
type GenerationErrorKind =
    /// 密钥无效或权限不足（401 / 403）
    | ProviderAuthFailed
    /// 触发限流（429）
    | ProviderRateLimited
    /// 请求超时或流中断
    | ProviderTimeout
    /// 服务端不可用 / 网络失败（5xx、DNS、连接被拒）
    | ProviderUnavailable
    /// 请求被拒（400）：参数不被该模型支持等
    | ProviderBadRequest
    /// 上下文超长
    | ContextTooLong
    /// 模型不存在或无权访问（404）
    | ModelNotFound
    /// 被内容策略拦截
    | ContentFiltered
    /// 工具执行失败
    | ToolFailed
    /// 会话配置指向的 provider/模型已不存在
    | ConfigInvalid
    /// 未归类
    | UnknownFailure

/// 面向用户的生成失败描述（随 generation.finished 下发）。
type GenerationError = {
    kind: GenerationErrorKind
    /// 面向用户的一句话说明
    message: string
    /// 面向开发者的原始细节（异常文本 / 上游响应片段）
    detail: string option
    /// 是否值得重试（UI 给「重试」按钮）
    retryable: bool
    /// 建议等待秒数（来自 Retry-After）
    retryAfterSeconds: int option
}

module GenerationErrorKind =

    let code (k: GenerationErrorKind) : string =
        match k with
        | ProviderAuthFailed -> "provider-auth-failed"
        | ProviderRateLimited -> "provider-rate-limited"
        | ProviderTimeout -> "provider-timeout"
        | ProviderUnavailable -> "provider-unavailable"
        | ProviderBadRequest -> "provider-bad-request"
        | ContextTooLong -> "context-too-long"
        | ModelNotFound -> "model-not-found"
        | ContentFiltered -> "content-filtered"
        | ToolFailed -> "tool-failed"
        | ConfigInvalid -> "config-invalid"
        | UnknownFailure -> "unknown-failure"

    let ofCode (s: string) : GenerationErrorKind =
        match s with
        | "provider-auth-failed" -> ProviderAuthFailed
        | "provider-rate-limited" -> ProviderRateLimited
        | "provider-timeout" -> ProviderTimeout
        | "provider-unavailable" -> ProviderUnavailable
        | "provider-bad-request" -> ProviderBadRequest
        | "context-too-long" -> ContextTooLong
        | "model-not-found" -> ModelNotFound
        | "content-filtered" -> ContentFiltered
        | "tool-failed" -> ToolFailed
        | "config-invalid" -> ConfigInvalid
        | _ -> UnknownFailure

    /// 针对服务商不可用（5xx 服务端故障 vs 本地网络不可达）的细分建议。
    let hintUnavailable (isServerOutage: bool) : string =
        if isServerOutage then "服务商暂时过载或维护中，稍等后可重试。"
        else "无法连接到服务商网络，请检查网络设置。"

    /// 该类别下用户能做什么（UI 在错误卡片里给出的行动建议）。
    let hint (k: GenerationErrorKind) : string =
        match k with
        | ProviderAuthFailed -> "请在设置中检查该服务商的 API Key。"
        | ProviderRateLimited -> "已触发服务商限流，稍等一会儿再试。"
        | ProviderTimeout -> "网络或服务商响应过慢，可直接重试。"
        | ProviderUnavailable -> "服务商暂时过载或网络异常，请检查网络设置或稍后重试。"
        | ProviderBadRequest -> "当前模型不接受这些生成参数，请到会话设置调整。"
        | ContextTooLong -> "对话太长了，可新建会话或删除部分早期消息。"
        | ModelNotFound -> "该模型不可用，请在设置中确认模型名称。"
        | ContentFiltered -> "内容被服务商的安全策略拦截。"
        | ToolFailed -> "工具执行失败，请检查工具配置。"
        | ConfigInvalid -> "会话使用的服务商或模型已不存在，请到会话设置重新选择。"
        | UnknownFailure -> "可以先重试；若持续失败请查看服务端日志。"

module GenerationError =

    /// 判断指定错误类别是否属于可重试的临时故障（标准 HTTP 语义）。
    let isRetryable (kind: GenerationErrorKind) : bool =
        match kind with
        | ProviderRateLimited | ProviderTimeout | ProviderUnavailable | UnknownFailure -> true
        | ProviderAuthFailed | ProviderBadRequest | ContextTooLong | ModelNotFound | ContentFiltered | ToolFailed | ConfigInvalid -> false

    let create (kind: GenerationErrorKind) (message: string) : GenerationError =
        { kind = kind
          message = message
          detail = None
          retryable = isRetryable kind
          retryAfterSeconds = None }

    let withDetail (detail: string) (e: GenerationError) : GenerationError =
        { e with detail = if System.String.IsNullOrWhiteSpace detail then None else Some detail }

    /// 根据具体失败信息与技术细节提供更有针对性的行动建议。
    /// 区分 502/503/504 等服务商过载/维护，与本地连接断开/DNS 异常。
    let hint (e: GenerationError) : string =
        match e.kind with
        | ProviderUnavailable ->
            let combined =
                let d = e.detail |> Option.defaultValue ""
                sprintf "%s %s" e.message d
            let is5xx =
                combined.Contains "500" || combined.Contains "502" || combined.Contains "503" || combined.Contains "504"
                || combined.Contains "Bad Gateway" || combined.Contains "Service Unavailable"
                || combined.Contains "Gateway Timeout" || combined.Contains "Internal Server Error"
                || combined.Contains "过载" || combined.Contains "维护"
            let isNetwork =
                combined.Contains "无法连接" || combined.Contains "Connection refused"
                || combined.Contains "actively refused" || combined.Contains "no such host"
                || combined.Contains "name or service not known" || combined.Contains "network is unreachable"
                || combined.Contains "network unreachable" || combined.Contains "connection reset"
                || combined.Contains "SocketException"
            if is5xx && not isNetwork then
                GenerationErrorKind.hintUnavailable true
            elif isNetwork then
                GenerationErrorKind.hintUnavailable false
            else
                GenerationErrorKind.hint e.kind
        | other -> GenerationErrorKind.hint other

    /// 供 UI 单行展示：说明 + 行动建议。
    let display (e: GenerationError) : string =
        let h = hint e
        if e.message.Contains h then e.message
        elif e.message.EndsWith("。") || e.message.EndsWith(" ") then e.message + h
        else e.message.TrimEnd() + " " + h

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
