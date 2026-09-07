namespace Wanxiang.Agent

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Wanxiang.Config
open Wanxiang.Core

/// 单次生成调用（一次 Provider 往返）。
type AgentCallResult =
    | Completed of usage: UsageDetails option
    | Failed of GenerationError
    | Cancelled

/// 把 Provider 端异常翻译成面向用户的结构化错误。
///
/// 上游 SDK 只给 `ClientResultException`（带 HTTP 状态码）或裸 `HttpRequestException`，
/// 直接把 `ex.Message` 抛给用户既看不懂也可能带出 URL/头片段，因此在这里统一归类。
module ProviderFailure =

    /// 上游响应正文里能识别出的语义信号（状态码不足以区分时使用）。
    let private bodySignals =
        [ "context_length_exceeded", ContextTooLong
          "context length", ContextTooLong
          "maximum context", ContextTooLong
          "too many tokens", ContextTooLong
          "reduce the length", ContextTooLong
          "content_filter", ContentFiltered
          "content management policy", ContentFiltered
          "safety", ContentFiltered
          "model_not_found", ModelNotFound
          "does not exist", ModelNotFound
          "invalid_api_key", ProviderAuthFailed
          "incorrect api key", ProviderAuthFailed ]

    let private detailOf (text: string) : string =
        let cleanHtml (s: string) =
            Regex.Replace(s, @"<[^>]+>", " ")
        let normalizeSpaces (s: string) =
            Regex.Replace(s, @"[\r\n\t ]+", " ")
        // 只保留前若干字符：足够定位问题，又不至于把整份上游响应灌进 UI
        let sanitized = if isNull text then "" else text |> cleanHtml |> normalizeSpaces
        let trimmed = sanitized.Trim()
        if trimmed.Length > 600 then trimmed.Substring(0, 600) + "…" else trimmed

    let private signalFromBody (text: string) : GenerationErrorKind option =
        if String.IsNullOrWhiteSpace text then None
        else
            let lowered = text.ToLowerInvariant()
            bodySignals |> List.tryPick (fun (needle, kind) -> if lowered.Contains needle then Some kind else None)

    let private retryAfterOf (ex: System.ClientModel.ClientResultException) : int option =
        try
            let response = ex.GetRawResponse()
            if isNull response then None
            else
                let mutable value = ""
                if response.Headers.TryGetValue("Retry-After", &value) then
                    match Int32.TryParse value with
                    | true, seconds when seconds > 0 -> Some seconds
                    | _ ->
                        match DateTimeOffset.TryParse value with
                        | true, at ->
                            let seconds = int (at - DateTimeOffset.UtcNow).TotalSeconds
                            if seconds > 0 then Some seconds else None
                        | _ -> None
                else None
        with _ -> None

    let private kindOfStatus (status: int) (body: string) : GenerationErrorKind =
        match signalFromBody body with
        | Some kind -> kind
        | None ->
            match status with
            | 400 | 422 -> ProviderBadRequest
            | 401 | 403 -> ProviderAuthFailed
            | 404 -> ModelNotFound
            | 408 -> ProviderTimeout
            | 429 -> ProviderRateLimited
            | s when s >= 500 -> ProviderUnavailable
            | _ -> UnknownFailure

    let private serverUnavailableMessage (providerLabel: string) (status: int option) : string =
        match status with
        | Some s when s >= 500 -> sprintf "「%s」服务暂时不可用（HTTP %d）。" providerLabel s
        | _ -> sprintf "「%s」服务暂时不可用。" providerLabel

    let private networkErrorMessage (providerLabel: string) : string =
        sprintf "无法连接「%s」，请检查网络或地址。" providerLabel

    let private messageOf (kind: GenerationErrorKind) (providerLabel: string) (model: string) : string =
        match kind with
        | ProviderAuthFailed -> sprintf "「%s」拒绝了当前凭据。" providerLabel
        | ProviderRateLimited -> sprintf "「%s」限流了这次请求。" providerLabel
        | ProviderTimeout -> sprintf "等待「%s」响应超时。" providerLabel
        | ProviderUnavailable -> networkErrorMessage providerLabel
        | ProviderBadRequest -> sprintf "「%s」认为这次请求参数不合法。" providerLabel
        | ContextTooLong -> "本次上下文超出模型能接受的长度。"
        | ModelNotFound -> sprintf "「%s」上找不到模型 %s。" providerLabel model
        | ContentFiltered -> "内容被服务商安全策略拦截。"
        | ToolFailed -> "工具执行失败。"
        | ConfigInvalid -> "会话配置无效。"
        | UnknownFailure -> sprintf "调用「%s」时发生未预期的错误。" providerLabel

    let rec private isNetworkException (ex: exn) : bool =
        match ex with
        | null -> false
        | :? SocketException -> true
        | :? HttpRequestException as hre when not hre.StatusCode.HasValue -> true
        | _ ->
            let msg = if isNull ex.Message then "" else ex.Message.ToLowerInvariant()
            msg.Contains "connection refused"
            || msg.Contains "actively refused"
            || msg.Contains "name or service not known"
            || msg.Contains "no such host"
            || msg.Contains "network is unreachable"
            || msg.Contains "network unreachable"
            || msg.Contains "connection reset"
            || (not (isNull ex.InnerException) && isNetworkException ex.InnerException)

    let private tryParse5xxStatus (text: string) : int option =
        if String.IsNullOrWhiteSpace text then None
        else
            let lowered = text.ToLowerInvariant()
            if lowered.Contains "500" || lowered.Contains "internal server error" then Some 500
            elif lowered.Contains "502" || lowered.Contains "bad gateway" then Some 502
            elif lowered.Contains "503" || lowered.Contains "service unavailable" then Some 503
            elif lowered.Contains "504" || lowered.Contains "gateway timeout" then Some 504
            else None

    /// 归类一个 Provider 调用异常。
    let classify (providerLabel: string) (model: string) (ex: exn) : GenerationError =
        match ex with
        | :? System.ClientModel.ClientResultException as cre ->
            let body =
                try
                    let response = cre.GetRawResponse()
                    if isNull response then cre.Message
                    else
                        let content = response.Content
                        if isNull content then cre.Message else content.ToString()
                with _ -> cre.Message
            let kind =
                if cre.Status <= 0 then ProviderUnavailable
                else kindOfStatus cre.Status body
            let message =
                match kind with
                | ProviderUnavailable when cre.Status >= 500 ->
                    serverUnavailableMessage providerLabel (Some cre.Status)
                | ProviderUnavailable ->
                    networkErrorMessage providerLabel
                | other ->
                    messageOf other providerLabel model
            { GenerationError.create kind message with
                detail = Some(detailOf (if cre.Status > 0 then sprintf "HTTP %d · %s" cre.Status body else cre.Message))
                retryAfterSeconds = retryAfterOf cre
                retryable =
                    match kind with
                    | ProviderRateLimited | ProviderTimeout | ProviderUnavailable | UnknownFailure -> true
                    | _ -> false }
        | :? TaskCanceledException
        | :? TimeoutException ->
            GenerationError.create ProviderTimeout (messageOf ProviderTimeout providerLabel model)
            |> GenerationError.withDetail (detailOf ex.Message)
        | :? HttpRequestException as hre ->
            let statusCode = hre.StatusCode |> Option.ofNullable |> Option.map int
            let kind =
                match statusCode with
                | Some code -> kindOfStatus code hre.Message
                | None -> ProviderUnavailable
            let message =
                match kind with
                | ProviderUnavailable ->
                    match statusCode with
                    | Some code when code >= 500 ->
                        serverUnavailableMessage providerLabel (Some code)
                    | _ ->
                        networkErrorMessage providerLabel
                | other ->
                    messageOf other providerLabel model
            { GenerationError.create kind message with
                detail = Some(detailOf hre.Message)
                retryable =
                    match kind with
                    | ProviderRateLimited | ProviderTimeout | ProviderUnavailable | UnknownFailure -> true
                    | _ -> false }
        | :? SocketException as se ->
            let msg = networkErrorMessage providerLabel
            { GenerationError.create ProviderUnavailable msg with
                detail = Some(detailOf se.Message)
                retryable = true }
        | _ ->
            match signalFromBody ex.Message with
            | Some kind ->
                GenerationError.create kind (messageOf kind providerLabel model)
                |> GenerationError.withDetail (detailOf ex.Message)
            | None ->
                if isNetworkException ex then
                    let msg = networkErrorMessage providerLabel
                    { GenerationError.create ProviderUnavailable msg with
                        detail = Some(detailOf ex.Message)
                        retryable = true }
                else
                    match tryParse5xxStatus ex.Message with
                    | Some status ->
                        let msg = serverUnavailableMessage providerLabel (Some status)
                        { GenerationError.create ProviderUnavailable msg with
                            detail = Some(detailOf ex.Message)
                            retryable = true }
                    | None ->
                        GenerationError.create UnknownFailure (messageOf UnknownFailure providerLabel model)
                        |> GenerationError.withDetail (detailOf ex.Message)

/// 每个 provider 共享一个 HttpClient：连接池复用，超时与附加请求头在此固化。
module private ProviderHttp =

    let private clients = Dictionary<string, HttpClient>()
    let private gate = obj ()

    /// 影响 HttpClient 构造的字段指纹；变化即重建。
    let private fingerprint (p: ProviderConfig) =
        let headers = p.headers |> Map.toList |> List.map (fun (k, v) -> k + "=" + v) |> String.concat ";"
        sprintf "%s|%d|%s" p.id p.timeoutSeconds headers

    let get (p: ProviderConfig) : HttpClient =
        let key = fingerprint p
        lock gate (fun () ->
            match clients.TryGetValue key with
            | true, existing -> existing
            | _ ->
                let handler = new SocketsHttpHandler()
                handler.PooledConnectionLifetime <- TimeSpan.FromMinutes 5.0
                handler.AutomaticDecompression <- DecompressionMethods.All
                let client = new HttpClient(handler)
                // 流式响应的整段时长可能远超单次请求超时，交给 NetworkTimeout 管首字节，
                // HttpClient.Timeout 放宽到 provider 超时的若干倍，避免长回答被硬切。
                client.Timeout <- TimeSpan.FromSeconds(float p.timeoutSeconds * 10.0)
                for KeyValue(name, value) in p.headers do
                    try client.DefaultRequestHeaders.TryAddWithoutValidation(name, value) |> ignore with _ -> ()
                clients[key] <- client
                client)

/// Agent 运行时：构建与执行。
/// 每个会话一个运行时 agent + session；消息历史由应用托管（投影），
/// 不依赖 Provider 服务端会话状态（决策 20）。
///
/// 模型来自**会话配置**（`SessionConfig.model`），provider 只提供端点与凭据；
/// provider 的 `defaultModel` 仅在会话未指定或指定了列表外模型时兜底。
/// `thinkingBudget` 是**已解析**的有效值（会话覆盖 → [generation] 默认），
/// 因为这一层看不到全局配置。0 表示不开思维链。
type AgentRuntime(
    provider: ProviderConfig,
    session: SessionConfig,
    tools: AITool list,
    historyProvider: ChatHistoryProvider,
    thinkingBudget: int) =

    let resolvedModel = ProviderConfig.resolveModel session.model provider
    let providerLabel = ProviderConfig.displayName provider

    /// OpenAI 兼容传输交给官方 SDK；两个原生协议是本项目自己实现的
    /// `IChatClient`，共用 ProviderHttp 的连接池与请求头。
    let openAiChatClient () =
        let clientOptions = OpenAI.OpenAIClientOptions()
        clientOptions.Endpoint <- Uri provider.baseUrl
        clientOptions.NetworkTimeout <- TimeSpan.FromSeconds(float provider.timeoutSeconds)
        clientOptions.RetryPolicy <- System.ClientModel.Primitives.ClientRetryPolicy(provider.maxRetries)
        clientOptions.Transport <- new System.ClientModel.Primitives.HttpClientPipelineTransport(ProviderHttp.get provider)
        let credential = System.ClientModel.ApiKeyCredential(provider.apiKey |> Option.defaultValue "unused")
        let client = new OpenAI.OpenAIClient(credential, clientOptions)
        client.GetChatClient(resolvedModel).AsIChatClient()

    let chatClient: IChatClient =
        match provider.kind with
        | "anthropic" -> new AnthropicChatClient(provider, resolvedModel, ProviderHttp.get provider, thinkingBudget)
        | "gemini" -> new GeminiChatClient(provider, resolvedModel, ProviderHttp.get provider, thinkingBudget)
        | _ -> openAiChatClient ()

    let agentOptions = ChatClientAgentOptions()

    do
        agentOptions.Name <- provider.id
        let chatOptions = ChatOptions()
        chatOptions.ModelId <- resolvedModel
        chatOptions.Instructions <- session.instructions |> Option.defaultValue null
        chatOptions.Tools <- ResizeArray<AITool>(tools)
        chatOptions.Temperature <- (match session.temperature with Some t -> Nullable(float32 t) | None -> Nullable())
        chatOptions.TopP <- (match session.topP with Some p -> Nullable(float32 p) | None -> Nullable())
        chatOptions.MaxOutputTokens <- (match session.maxTokens with Some m -> Nullable m | None -> Nullable())
        // 会话 extraJson 原样透传：模型专属参数（reasoning_effort、enable_thinking…）
        // 不需要万象逐个建模即可使用。
        match session.extraJson with
        | Some node when not (isNull node) ->
            match node with
            | :? System.Text.Json.Nodes.JsonObject as o ->
                let bag = Dictionary<string, obj>()
                for KeyValue(key, value) in o do
                    if not (isNull value) then bag[key] <- box (value.DeepClone())
                if bag.Count > 0 then chatOptions.AdditionalProperties <- AdditionalPropertiesDictionary(bag)
            | _ -> ()
        | _ -> ()
        agentOptions.ChatOptions <- chatOptions
        agentOptions.ChatHistoryProvider <- historyProvider
        // 禁用默认中间件（FunctionInvokingChatClient 等）：工具循环由万象编排层控制（决策 92/93）
        agentOptions.UseProvidedChatClientAsIs <- true

    let agent = chatClient.AsAIAgent(agentOptions)

    /// 实际生效的模型（会话请求 + provider 列表解析后的结果）。
    member _.Model = resolvedModel

    /// 实际生效的 provider id。
    member _.ProviderId = provider.id

    /// 创建会话并绑定 conversationId（运行时对象）。
    member _.CreateSession(conversationId: Guid) : AgentSession =
        let session = agent.CreateSessionAsync().AsTask().GetAwaiter().GetResult()
        session.StateBag.SetValue<string>(WanxiangHistoryProvider.ConversationIdKey, conversationId.ToString("D"))
        session

    /// 流式执行一次调用（上下文消息列表由编排层显式构造）。delta 通过回调转发（临时展示，不记账）。
    member _.RunStreaming(session: AgentSession, messages: ChatMessage list, onDelta: ChatMessage -> unit, ct: CancellationToken) : Task<AgentCallResult> =
        task {
            try
                let updates = agent.RunStreamingAsync(messages, session, cancellationToken = ct)
                let mutable deltaMsg: ChatMessage = null
                let collected = ResizeArray<AgentResponseUpdate>()
                let enumerator = updates.GetAsyncEnumerator(ct)
                let mutable running = true
                while running do
                    let! hasNext = enumerator.MoveNextAsync()
                    if hasNext then
                        let update = enumerator.Current
                        collected.Add(update)
                        if update.Role.HasValue && update.Role.Value = ChatRole.Assistant then
                            // 正文、思维链与工具调用都转发（决策 18 透明映射）：
                            // 工具调用在流里就可见，UI 不必等整轮提交才显示「正在调用工具」。
                            let pieces =
                                update.Contents
                                |> Seq.choose (fun c ->
                                    match c with
                                    | :? TextContent as t when not (String.IsNullOrEmpty t.Text) ->
                                        Some(TextContent(t.Text) :> AIContent)
                                    | :? TextReasoningContent as r when not (String.IsNullOrEmpty r.Text) ->
                                        Some(TextReasoningContent(r.Text) :> AIContent)
                                    | :? FunctionCallContent as f -> Some(f :> AIContent)
                                    | _ -> None)
                                |> List.ofSeq
                            if not pieces.IsEmpty then
                                if isNull deltaMsg then
                                    deltaMsg <- ChatMessage()
                                    deltaMsg.Role <- ChatRole.Assistant
                                for p in pieces do deltaMsg.Contents.Add p
                                onDelta deltaMsg
                    else
                        running <- false
                let usage =
                    try
                        let response = AgentResponseExtensions.ToAgentResponse(collected)
                        if isNull response || isNull response.Usage then None else Some response.Usage
                    with _ ->
                        None
                return Completed usage
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return Cancelled
            | ex -> return Failed(ProviderFailure.classify providerLabel resolvedModel ex)
        }

    /// 非流式执行一次调用（工具结果回传、标题生成等）。
    member _.Run(session: AgentSession, message: ChatMessage, ct: CancellationToken) : Task<AgentCallResult> =
        task {
            try
                let! agentResponse = agent.RunAsync(message, session, cancellationToken = ct)
                let usage = if isNull agentResponse || isNull agentResponse.Usage then None else Some agentResponse.Usage
                return Completed usage
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> return Cancelled
            | ex -> return Failed(ProviderFailure.classify providerLabel resolvedModel ex)
        }

    /// 一次性补全，不经 AgentSession、不写历史（标题生成等旁路用途）。
    member _.CompleteOnce(messages: ChatMessage list, maxTokens: int, ct: CancellationToken) : Task<Result<string, GenerationError>> =
        task {
            try
                let options = ChatOptions()
                options.ModelId <- resolvedModel
                options.MaxOutputTokens <- Nullable maxTokens
                options.Temperature <- Nullable 0.3f
                let! response = chatClient.GetResponseAsync(messages, options, ct)
                let text = if isNull response then "" else response.Text
                return Ok(if isNull text then "" else text.Trim())
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return Error(GenerationError.create ProviderTimeout "标题生成被取消。")
            | ex -> return Error(ProviderFailure.classify providerLabel resolvedModel ex)
        }
