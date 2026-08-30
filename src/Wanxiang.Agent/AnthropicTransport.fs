namespace Wanxiang.Agent

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Wanxiang.Config

/// Anthropic Messages API 的原生传输。
///
/// 走原生而不是兼容端点的收益：思维链（`thinking`）、prompt caching、
/// 以及工具调用的增量 JSON 都只在原生协议里有。这里先把文本、工具、
/// 用量与错误做完整，其余按需再加。
type AnthropicChatClient(provider: ProviderConfig, model: string, http: HttpClient, thinkingBudget: int) =

    /// baseUrl 可能已含 `/v1`（用户从兼容端点配置迁移过来），两种都接受。
    let endpoint =
        let trimmed = provider.baseUrl.TrimEnd '/'
        if trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) then trimmed + "/messages"
        else trimmed + "/v1/messages"

    let newRequest (body: JsonObject) =
        let request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        request.Content <- new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicRequest.ApiVersion) |> ignore
        match provider.apiKey with
        | Some key when not (String.IsNullOrWhiteSpace key) ->
            request.Headers.TryAddWithoutValidation("x-api-key", key) |> ignore
        | _ -> ()
        request

    /// 累积中的工具调用。Anthropic 把入参切成 `input_json_delta` 片段流式下发，
    /// 必须等 `content_block_stop` 拼完整才能解析。
    let toolBuffer () = Dictionary<int, string * string * StringBuilder>()

    let stream (messages: ChatMessage seq) (options: ChatOptions) (ct: CancellationToken) =
        let channel = Channel.CreateUnbounded<ChatResponseUpdate>()

        let pump =
            task {
                try
                    let body =
                        AnthropicRequest.build model messages options true thinkingBudget provider.promptCaching
                    use! response =
                        ProviderSse.sendWithRetry http (fun () -> newRequest body) provider.maxRetries ct
                    if not response.IsSuccessStatusCode then
                        let! text = response.Content.ReadAsStringAsync ct
                        ProviderSse.raiseForStatus response text
                    use! raw = response.Content.ReadAsStreamAsync ct

                    let tools = toolBuffer ()
                    let mutable responseId = ""
                    let mutable inputTokens = 0
                    let mutable outputTokens = 0
                    let mutable finish = ""

                    let emit (contents: AIContent list) =
                        let update = ChatResponseUpdate(Nullable ChatRole.Assistant, List.toArray contents)
                        if not (String.IsNullOrEmpty responseId) then update.ResponseId <- responseId
                        channel.Writer.TryWrite update |> ignore

                    let enumerator = (ProviderSse.read raw ct).GetAsyncEnumerator ct
                    try
                        let mutable go = true
                        while go do
                            let! moved = enumerator.MoveNextAsync()
                            if not moved then go <- false
                            else
                                let event = enumerator.Current
                                match JsonNode.Parse event.data with
                                | null -> ()
                                | node ->
                                    let kind =
                                        ProviderSse.tryString node "type" |> Option.defaultValue event.name
                                    match kind with
                                    | "message_start" ->
                                        match ProviderSse.tryObject node "message" with
                                        | Some message ->
                                            responseId <-
                                                ProviderSse.tryString message "id" |> Option.defaultValue ""
                                            match ProviderSse.tryObject message "usage" with
                                            | Some usage ->
                                                inputTokens <-
                                                    ProviderSse.tryInt usage "input_tokens" |> Option.defaultValue 0
                                                outputTokens <-
                                                    ProviderSse.tryInt usage "output_tokens" |> Option.defaultValue 0
                                            | None -> ()
                                        | None -> ()
                                    | "content_block_start" ->
                                        let index = ProviderSse.tryInt node "index" |> Option.defaultValue 0
                                        match ProviderSse.tryObject node "content_block" with
                                        | Some block when
                                            ProviderSse.tryString block "type" = Some "tool_use"
                                            ->
                                            let id = ProviderSse.tryString block "id" |> Option.defaultValue ""
                                            let name = ProviderSse.tryString block "name" |> Option.defaultValue ""
                                            tools[index] <- (id, name, StringBuilder())
                                        | _ -> ()
                                    | "content_block_delta" ->
                                        let index = ProviderSse.tryInt node "index" |> Option.defaultValue 0
                                        match ProviderSse.tryObject node "delta" with
                                        | Some delta ->
                                            match ProviderSse.tryString delta "type" with
                                            | Some "text_delta" ->
                                                match ProviderSse.tryString delta "text" with
                                                | Some text when text.Length > 0 ->
                                                    emit [ TextContent text :> AIContent ]
                                                | _ -> ()
                                            // 思维链走独立的内容类型：界面单独折叠展示，
                                            // 也不并入会话摘要预览
                                            | Some "thinking_delta" ->
                                                match ProviderSse.tryString delta "thinking" with
                                                | Some text when text.Length > 0 ->
                                                    emit [ TextReasoningContent text :> AIContent ]
                                                | _ -> ()
                                            | Some "input_json_delta" ->
                                                match tools.TryGetValue index, ProviderSse.tryString delta "partial_json" with
                                                | (true, (_, _, buffer)), Some fragment -> buffer.Append fragment |> ignore
                                                | _ -> ()
                                            | _ -> ()
                                        | None -> ()
                                    | "content_block_stop" ->
                                        let index = ProviderSse.tryInt node "index" |> Option.defaultValue 0
                                        match tools.TryGetValue index with
                                        | true, (id, name, buffer) ->
                                            tools.Remove index |> ignore
                                            let arguments = Dictionary<string, obj>()
                                            let text = buffer.ToString()
                                            if not (String.IsNullOrWhiteSpace text) then
                                                try
                                                    match JsonNode.Parse text with
                                                    | :? JsonObject as parsed ->
                                                        for KeyValue(key, value) in parsed do
                                                            arguments[key] <- box value
                                                    | _ -> ()
                                                with _ -> ()
                                            emit [ FunctionCallContent(id, name, arguments) :> AIContent ]
                                        | _ -> ()
                                    | "message_delta" ->
                                        match ProviderSse.tryObject node "delta" with
                                        | Some delta ->
                                            finish <-
                                                ProviderSse.tryString delta "stop_reason" |> Option.defaultValue finish
                                        | None -> ()
                                        match ProviderSse.tryObject node "usage" with
                                        | Some usage ->
                                            outputTokens <-
                                                ProviderSse.tryInt usage "output_tokens"
                                                |> Option.defaultValue outputTokens
                                        | None -> ()
                                    | "error" ->
                                        let message =
                                            ProviderSse.tryObject node "error"
                                            |> Option.bind (fun e -> ProviderSse.tryString e "message")
                                            |> Option.defaultValue "anthropic stream error"
                                        raise (HttpRequestException message)
                                    | "message_stop" -> go <- false
                                    | _ -> ()
                    finally
                        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

                    // 用量与结束原因作为收尾更新：编排层按它记账
                    let usage = UsageDetails()
                    usage.InputTokenCount <- Nullable(int64 inputTokens)
                    usage.OutputTokenCount <- Nullable(int64 outputTokens)
                    usage.TotalTokenCount <- Nullable(int64 (inputTokens + outputTokens))
                    let closing = ChatResponseUpdate(Nullable ChatRole.Assistant, [| UsageContent usage :> AIContent |])
                    if not (String.IsNullOrEmpty responseId) then closing.ResponseId <- responseId
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
            // 非流式直接聚合流式结果：两条路径共用一套解析，不会出现「流式对、
            // 非流式错」这种最难查的分裂。
            task {
                let updates = (this :> IChatClient).GetStreamingResponseAsync(messages, options, ct)
                return! updates.ToChatResponseAsync ct
            }

        member _.GetService(serviceType, _) =
            if serviceType = typeof<AnthropicChatClient> then box () else null

    interface IDisposable with
        // HttpClient 由 ProviderHttp 按 provider 共享，不在这里释放
        member _.Dispose() = ()
