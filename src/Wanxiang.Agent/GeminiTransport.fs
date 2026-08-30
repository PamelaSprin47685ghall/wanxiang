namespace Wanxiang.Agent

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Channels
open Microsoft.Extensions.AI
open Wanxiang.Config

/// Gemini generateContent 的原生传输。
///
/// 走原生而不是 OpenAI 兼容端点的收益：`systemInstruction`、thinking 配置、
/// 以及 inlineData 的多模态入参都只在原生协议里完整。
type GeminiChatClient(provider: ProviderConfig, model: string, http: HttpClient, thinkingBudget: int) =

    /// baseUrl 若没带版本段就补 `/v1beta`；带了就照用（便于指向代理或自建网关）。
    let root =
        let trimmed = provider.baseUrl.TrimEnd '/'
        if trimmed.Contains "/v1" then trimmed else trimmed + "/v1beta"

    let endpoint = $"{root}/models/{model}:streamGenerateContent?alt=sse"

    let newRequest (body: JsonObject) =
        let request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        request.Content <- new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        match provider.apiKey with
        | Some key when not (String.IsNullOrWhiteSpace key) ->
            // 用请求头而非 ?key=：URL 会进日志与错误详情，密钥不该出现在那里
            request.Headers.TryAddWithoutValidation("x-goog-api-key", key) |> ignore
        | _ -> ()
        request

    let stream (messages: ChatMessage seq) (options: ChatOptions) (ct: CancellationToken) =
        let channel = Channel.CreateUnbounded<ChatResponseUpdate>()

        let pump =
            task {
                try
                    let body = GeminiRequest.build messages options thinkingBudget
                    use! response =
                        ProviderSse.sendWithRetry http (fun () -> newRequest body) provider.maxRetries ct
                    if not response.IsSuccessStatusCode then
                        let! text = response.Content.ReadAsStringAsync ct
                        ProviderSse.raiseForStatus response text
                    use! raw = response.Content.ReadAsStreamAsync ct

                    let mutable promptTokens = 0
                    let mutable outputTokens = 0
                    let mutable totalTokens = 0
                    let mutable finish = ""
                    let mutable callIndex = 0

                    let emit (contents: AIContent list) =
                        channel.Writer.TryWrite(
                            ChatResponseUpdate(Nullable ChatRole.Assistant, List.toArray contents))
                        |> ignore

                    let enumerator = (ProviderSse.read raw ct).GetAsyncEnumerator ct
                    try
                        let mutable go = true
                        while go do
                            let! moved = enumerator.MoveNextAsync()
                            if not moved then go <- false
                            else
                                match JsonNode.Parse enumerator.Current.data with
                                | null -> ()
                                | node ->
                                    // 上游把错误也放在 200 的流里时，body 里会有 error 对象
                                    match ProviderSse.tryObject node "error" with
                                    | Some error ->
                                        let message =
                                            ProviderSse.tryString error "message"
                                            |> Option.defaultValue "gemini stream error"
                                        raise (HttpRequestException message)
                                    | None -> ()

                                    match ProviderSse.tryObject node "usageMetadata" with
                                    | Some usage ->
                                        promptTokens <-
                                            ProviderSse.tryInt usage "promptTokenCount"
                                            |> Option.defaultValue promptTokens
                                        outputTokens <-
                                            ProviderSse.tryInt usage "candidatesTokenCount"
                                            |> Option.defaultValue outputTokens
                                        totalTokens <-
                                            ProviderSse.tryInt usage "totalTokenCount"
                                            |> Option.defaultValue totalTokens
                                    | None -> ()

                                    match ProviderSse.tryArray node "candidates" with
                                    | Some candidates ->
                                        for candidate in candidates do
                                            if not (isNull candidate) then
                                                finish <-
                                                    ProviderSse.tryString candidate "finishReason"
                                                    |> Option.defaultValue finish
                                                match ProviderSse.tryObject candidate "content" with
                                                | Some content ->
                                                    match ProviderSse.tryArray content "parts" with
                                                    | Some parts ->
                                                        for part in parts do
                                                            if not (isNull part) then
                                                                match ProviderSse.tryString part "text" with
                                                                | Some text when text.Length > 0 ->
                                                                    // thought=true 的片段是思考内容，
                                                                    // 与正式回答分开呈现
                                                                    if ProviderSse.tryBool part "thought" = Some true then
                                                                        emit [ TextReasoningContent text :> AIContent ]
                                                                    else
                                                                        emit [ TextContent text :> AIContent ]
                                                                | _ -> ()
                                                                match ProviderSse.tryObject part "functionCall" with
                                                                | Some call ->
                                                                    let name =
                                                                        ProviderSse.tryString call "name"
                                                                        |> Option.defaultValue ""
                                                                    let arguments = Dictionary<string, obj>()
                                                                    match ProviderSse.tryObject call "args" with
                                                                    | Some(:? JsonObject as args) ->
                                                                        for KeyValue(key, value) in args do
                                                                            arguments[key] <- box value
                                                                    | _ -> ()
                                                                    let callId =
                                                                        GeminiRequest.makeCallId name callIndex
                                                                    callIndex <- callIndex + 1
                                                                    emit
                                                                        [ FunctionCallContent(callId, name, arguments)
                                                                          :> AIContent ]
                                                                | None -> ()
                                                    | None -> ()
                                                | None -> ()
                                    | None -> ()
                    finally
                        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

                    let usage = UsageDetails()
                    usage.InputTokenCount <- Nullable(int64 promptTokens)
                    usage.OutputTokenCount <- Nullable(int64 outputTokens)
                    usage.TotalTokenCount <-
                        Nullable(int64 (if totalTokens > 0 then totalTokens else promptTokens + outputTokens))
                    let closing =
                        ChatResponseUpdate(Nullable ChatRole.Assistant, [| UsageContent usage :> AIContent |])
                    if not (String.IsNullOrEmpty finish) then
                        closing.FinishReason <- Nullable(ChatFinishReason finish)
                    channel.Writer.TryWrite closing |> ignore
                    channel.Writer.Complete()
                with ex -> channel.Writer.Complete ex
            }

        ignore pump
        channel.Reader.ReadAllAsync ct

    interface IChatClient with

        member _.GetStreamingResponseAsync(messages, options, ct) = stream messages options ct

        member this.GetResponseAsync(messages, options, ct) =
            // 非流式聚合流式结果，避免「流式对、非流式错」这种分裂
            task {
                let updates = (this :> IChatClient).GetStreamingResponseAsync(messages, options, ct)
                return! updates.ToChatResponseAsync ct
            }

        member _.GetService(serviceType, _) =
            if serviceType = typeof<GeminiChatClient> then box () else null

    interface IDisposable with
        // HttpClient 由 ProviderHttp 按 provider 共享，不在这里释放
        member _.Dispose() = ()
