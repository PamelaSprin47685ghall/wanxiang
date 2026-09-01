namespace Wanxiang.Agent

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// 一条 SSE 事件。`event:` 缺省时为空串（Gemini 只发 `data:`）。
type SseEvent = { name: string; data: string }

/// Provider 流式响应的公共管道：SSE 分帧、错误状态翻译、JSON 小工具。
///
/// Anthropic 与 Gemini 都用 SSE，但事件语义完全不同，所以只共享**分帧**，
/// 语义各自解析——把两套协议塞进一个解析器只会得到一堆 if。
module ProviderSse =

    /// 逐帧读取。SSE 以空行分帧，`data:` 可多行需拼接。
    let read (stream: Stream) (ct: CancellationToken) : IAsyncEnumerable<SseEvent> =
        let channel = Channel.CreateUnbounded<SseEvent>()

        let pump =
            task {
                try
                    use reader = new StreamReader(stream, Encoding.UTF8)
                    let data = StringBuilder()
                    let mutable name = ""
                    let mutable finished = false
                    while not finished && not ct.IsCancellationRequested do
                        let! line = reader.ReadLineAsync ct
                        match line with
                        | null ->
                            finished <- true
                            if data.Length > 0 then
                                channel.Writer.TryWrite { name = name; data = data.ToString() } |> ignore
                        | "" ->
                            if data.Length > 0 then
                                channel.Writer.TryWrite { name = name; data = data.ToString() } |> ignore
                                data.Clear() |> ignore
                                name <- ""
                        | text when text.StartsWith(":", StringComparison.Ordinal) -> ()
                        | text when text.StartsWith("event:", StringComparison.Ordinal) ->
                            name <- text.Substring(6).Trim()
                        | text when text.StartsWith("data:", StringComparison.Ordinal) ->
                            if data.Length > 0 then data.Append '\n' |> ignore
                            // W3C SSE 规范：仅剥离冒号后紧跟的一个空格；保留数据正文里的缩进
                            let payload =
                                if text.Length > 5 && text[5] = ' ' then text.Substring 6
                                elif text.Length > 5 then text.Substring 5
                                else ""
                            data.Append payload |> ignore
                        | _ -> ()
                    channel.Writer.Complete()
                with ex -> channel.Writer.Complete ex
            }

        ignore pump
        channel.Reader.ReadAllAsync ct

    /// 非 2xx 一律翻成带状态码的 HttpRequestException：
    /// `ProviderFailure` 正是按状态码归类错误，抛别的类型会退化成 UnknownFailure。
    let raiseForStatus (response: HttpResponseMessage) (body: string) : unit =
        if not response.IsSuccessStatusCode then
            let detail = if String.IsNullOrWhiteSpace body then response.ReasonPhrase else body
            raise (HttpRequestException(detail, null, Nullable response.StatusCode))

    let private transient (status: System.Net.HttpStatusCode) =
        let code = int status
        code = 429 || code = 408 || code >= 500

    /// 按 provider 的 maxRetries 退避重试。
    ///
    /// 只在**首个字节之前**重试：先用 ResponseHeadersRead 拿到状态行，
    /// 尚未向下游发出任何增量，此时重发是安全的；一旦开始流式输出就绝不重试，
    /// 否则用户会看到同一段回答出现两次。
    let sendWithRetry
        (http: HttpClient)
        (makeRequest: unit -> HttpRequestMessage)
        (maxRetries: int)
        (ct: CancellationToken)
        : Task<HttpResponseMessage> =
        task {
            let attempts = max 1 (maxRetries + 1)
            let mutable attempt = 0
            let mutable result = Unchecked.defaultof<HttpResponseMessage>
            let mutable finished = false
            while not finished do
                attempt <- attempt + 1
                let last = attempt >= attempts
                let mutable retryAfter = TimeSpan.Zero
                try
                    let request = makeRequest ()
                    let! response = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    if response.IsSuccessStatusCode || last || not (transient response.StatusCode) then
                        result <- response
                        finished <- true
                    else
                        retryAfter <-
                            match response.Headers.RetryAfter with
                            | null -> TimeSpan.Zero
                            | value when value.Delta.HasValue -> value.Delta.Value
                            | _ -> TimeSpan.Zero
                        response.Dispose()
                // 取消不是失败，直接向上传播；最后一次尝试的异常同样不吞，
                // 交给 ProviderFailure 归类成面向用户的错误。
                with ex when not last && not (ex :? OperationCanceledException) -> ignore ex
                if not finished then
                    // 指数退避，上限 8 秒；上游给了 Retry-After 就听它的
                    let backoff =
                        if retryAfter > TimeSpan.Zero then retryAfter
                        else TimeSpan.FromMilliseconds(float (min 8000 (250 * (1 <<< (attempt - 1)))))
                    do! Task.Delay(backoff, ct)
            return result
        }

    let private property (node: JsonNode) (key: string) : JsonNode option =
        match node with
        | :? JsonObject as o ->
            match o[key] with
            | null -> None
            | value -> Some value
        | _ -> None

    let tryString (node: JsonNode) (key: string) : string option =
        property node key
        |> Option.bind (fun value ->
            try
                if value.GetValueKind() = System.Text.Json.JsonValueKind.String then Some(value.GetValue<string>())
                else None
            with _ -> None)

    let tryInt (node: JsonNode) (key: string) : int option =
        property node key
        |> Option.bind (fun value ->
            try
                match value.GetValueKind() with
                | System.Text.Json.JsonValueKind.Number ->
                    let mutable i = 0
                    if value.AsValue().TryGetValue<int>(&i) then Some i
                    else
                        let mutable l = 0L
                        if value.AsValue().TryGetValue<int64>(&l) then Some(int l)
                        else None
                | _ -> None
            with _ -> None)

    let tryBool (node: JsonNode) (key: string) : bool option =
        property node key
        |> Option.bind (fun value -> try Some(value.GetValue<bool>()) with _ -> None)

    let tryObject (node: JsonNode) (key: string) : JsonNode option =
        match property node key with
        | Some(:? JsonObject as o) -> Some(o :> JsonNode)
        | _ -> None

    let tryArray (node: JsonNode) (key: string) : JsonArray option =
        match property node key with
        | Some(:? JsonArray as a) -> Some a
        | _ -> None

    /// JSON Schema 从 AITool 上取下来时是 `JsonElement`；两边协议都要它。
    let schemaOf (schema: System.Text.Json.JsonElement) : JsonNode option =
        try
            match JsonNode.Parse(schema.GetRawText()) with
            | null -> None
            | node -> Some node
        with _ -> None
