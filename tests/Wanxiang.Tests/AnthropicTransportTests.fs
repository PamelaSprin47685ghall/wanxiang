namespace Wanxiang.Tests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Agent

module AnthropicTransportTests =

    type MockHttpHandler(handler: HttpRequestMessage -> HttpResponseMessage) =
        inherit HttpMessageHandler()
        override _.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken) =
            Task.FromResult(handler request)

    type AsyncMockHttpHandler(handler: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage>) =
        inherit HttpMessageHandler()
        override _.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken) =
            handler request cancellationToken

    let private createProvider (baseUrl: string option) (apiKey: string option) : ProviderConfig =
        {
            id = "test-anthropic"
            kind = "anthropic"
            label = "Test Anthropic"
            baseUrl = defaultArg baseUrl "https://api.anthropic.com"
            apiKey = apiKey
            models = [ "claude-3-5-sonnet-20241022" ]
            defaultModel = "claude-3-5-sonnet-20241022"
            timeoutSeconds = 30
            maxRetries = 0
            enabled = true
            promptCaching = false
            headers = Map.empty
            extraJson = None
        }

    let private createClient (handler: HttpMessageHandler) (baseUrl: string option) (apiKey: string option) (thinkingBudget: int option) : IChatClient =
        let provider = createProvider baseUrl apiKey
        let httpClient = new HttpClient(handler)
        new AnthropicChatClient(provider, "claude-3-5-sonnet-20241022", httpClient, defaultArg thinkingBudget 0) :> IChatClient

    let private drainStream (stream: IAsyncEnumerable<ChatResponseUpdate>) =
        task {
            let updates = List<ChatResponseUpdate>()
            let enumerator = stream.GetAsyncEnumerator()
            let mutable hasNext = true
            while hasNext do
                let! next = enumerator.MoveNextAsync()
                if next then
                    updates.Add(enumerator.Current)
                else
                    hasNext <- false
            let! _ = enumerator.DisposeAsync()
            return updates :> IReadOnlyList<ChatResponseUpdate>
        }

    let private sseResponse (events: string list) =
        let body = String.concat "\n\n" events + "\n\n"
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(body, Encoding.UTF8, "text/event-stream")
        response

    [<Fact>]
    let ``GetStreamingResponseAsync parses message_start text_delta and message_stop properly`` () =
        task {
            let ssePayload = [
                """event: message_start
data: {"type":"message_start","message":{"id":"msg_123","model":"claude-3-5-sonnet","usage":{"input_tokens":25,"output_tokens":1}}}"""
                """event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""
                """event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello "}}"""
                """event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"world!"}}"""
                """event: content_block_stop
data: {"type":"content_block_stop","index":0}"""
                """event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":12}}"""
                """event: message_stop
data: {"type":"message_stop"}"""
            ]

            let mutable capturedRequest: HttpRequestMessage option = None
            let handler = new MockHttpHandler(fun req ->
                capturedRequest <- Some req
                sseResponse ssePayload
            )
            use client = createClient handler None (Some "sk-ant-test-key") None
            let messages = [ ChatMessage(ChatRole.User, "Hi") ]

            let! updates = drainStream (client.GetStreamingResponseAsync(messages))

            Assert.True(capturedRequest.IsSome)
            let req = capturedRequest.Value
            Assert.Equal("https://api.anthropic.com/v1/messages", req.RequestUri.ToString())
            Assert.Equal("sk-ant-test-key", req.Headers.GetValues("x-api-key") |> Seq.head)
            Assert.Equal("2023-06-01", req.Headers.GetValues("anthropic-version") |> Seq.head)

            Assert.True(updates.Count >= 3)
            let textDeltas =
                updates
                |> Seq.choose (fun u -> if String.IsNullOrEmpty u.Text then None else Some u.Text)
                |> String.concat ""
            Assert.Equal("Hello world!", textDeltas)

            let finalUpdate = updates |> Seq.last
            Assert.Equal<Nullable<ChatFinishReason>>(Nullable ChatFinishReason.Stop, finalUpdate.FinishReason)
            Assert.Equal("msg_123", finalUpdate.ResponseId)
            Assert.NotNull(finalUpdate.Contents)
            let usageContent =
                finalUpdate.Contents
                |> Seq.tryPick (fun c -> match c with :? UsageContent as u -> Some u.Details | _ -> None)
            Assert.True(usageContent.IsSome)
            let usage = usageContent.Value
            Assert.Equal(Nullable 25L, usage.InputTokenCount)
            Assert.Equal(Nullable 12L, usage.OutputTokenCount)
            Assert.Equal(Nullable 37L, usage.TotalTokenCount)
        }

    [<Fact>]
    let ``GetStreamingResponseAsync custom baseUrl and thinking budget appends beta header`` () =
        task {
            let ssePayload = [
                """event: message_start
data: {"type":"message_start","message":{"id":"msg_think","usage":{"input_tokens":10,"output_tokens":2}}}"""
                """event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me ponder..."}}"""
                """event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Result"}}"""
                """event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"max_tokens"}}"""
                """event: message_stop
data: {"type":"message_stop"}"""
            ]

            let mutable capturedRequest: HttpRequestMessage option = None
            let handler = new MockHttpHandler(fun req ->
                capturedRequest <- Some req
                sseResponse ssePayload
            )
            use client = createClient handler (Some "https://proxy.example.com/anthropic/") (Some "ant-key") (Some 2048)
            let messages = [ ChatMessage(ChatRole.User, "Deep think") ]

            let! updates = drainStream (client.GetStreamingResponseAsync(messages))

            Assert.True(capturedRequest.IsSome)
            let req = capturedRequest.Value
            Assert.Equal("https://proxy.example.com/anthropic/v1/messages", req.RequestUri.ToString())
            Assert.True(req.Headers.Contains("anthropic-beta"))
            let betas = req.Headers.GetValues("anthropic-beta") |> Seq.toList
            Assert.Contains("interleaved-thinking-2025-05-14", betas)

            let reasoningUpdates =
                updates
                |> Seq.choose (fun u ->
                    u.Contents
                    |> Seq.tryPick (fun c ->
                        match c with
                        | :? TextContent as tc when tc.RawRepresentation = box "thinking" -> Some tc.Text
                        | _ -> None
                    )
                )
                |> Seq.toList
            Assert.Equal<string list>([ "Let me ponder..." ], reasoningUpdates)

            let lastUpdate = updates |> Seq.last
            Assert.Equal<Nullable<ChatFinishReason>>(Nullable ChatFinishReason.Length, lastUpdate.FinishReason)
        }

    [<Fact>]
    let ``GetStreamingResponseAsync handles tool_use block and emits FunctionCallContent on block stop`` () =
        task {
            let ssePayload = [
                """event: message_start
data: {"type":"message_start","message":{"id":"msg_tool","usage":{"input_tokens":100,"output_tokens":5}}}"""
                """event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call_abc123","name":"lookup_weather"}}"""
                """event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}"""
                """event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"Beijing\"}"}}"""
                """event: content_block_stop
data: {"type":"content_block_stop","index":1}"""
                """event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":40}}"""
                """event: message_stop
data: {"type":"message_stop"}"""
            ]

            let handler = new MockHttpHandler(fun _ -> sseResponse ssePayload)
            use client = createClient handler None (Some "key") None
            let messages = [ ChatMessage(ChatRole.User, "Weather in Beijing?") ]

            let! updates = drainStream (client.GetStreamingResponseAsync(messages))

            let toolCalls =
                updates
                |> Seq.choose (fun u ->
                    u.Contents
                    |> Seq.tryPick (fun c -> match c with :? FunctionCallContent as fcc -> Some fcc | _ -> None)
                )
                |> Seq.toList

            Assert.Single(toolCalls) |> ignore
            let call = toolCalls.Head
            Assert.Equal("call_abc123", call.CallId)
            Assert.Equal("lookup_weather", call.Name)
            Assert.NotNull(call.Arguments)
            Assert.True(call.Arguments.ContainsKey("city"))
            Assert.Equal("Beijing", call.Arguments["city"].ToString())

            let finalUpdate = updates |> Seq.last
            Assert.Equal<Nullable<ChatFinishReason>>(Nullable ChatFinishReason.ToolCalls, finalUpdate.FinishReason)
        }

    [<Fact>]
    let ``GetStreamingResponseAsync handles malformed tool_use input json gracefully without crashing`` () =
        task {
            let ssePayload = [
                """event: message_start
data: {"type":"message_start","message":{"id":"msg_bad_json","usage":{"input_tokens":50,"output_tokens":5}}}"""
                """event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call_bad","name":"bad_func"}}"""
                """event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{not valid json..."}}"""
                """event: content_block_stop
data: {"type":"content_block_stop","index":0}"""
                """event: message_stop
data: {"type":"message_stop"}"""
            ]

            let handler = new MockHttpHandler(fun _ -> sseResponse ssePayload)
            use client = createClient handler None (Some "key") None
            let messages = [ ChatMessage(ChatRole.User, "Do something") ]

            let! updates = drainStream (client.GetStreamingResponseAsync(messages))

            let toolCalls =
                updates
                |> Seq.choose (fun u ->
                    u.Contents
                    |> Seq.tryPick (fun c -> match c with :? FunctionCallContent as fcc -> Some fcc | _ -> None)
                )
                |> Seq.toList

            Assert.Empty(toolCalls)
        }

    [<Fact>]
    let ``GetStreamingResponseAsync handles non-success HTTP status with error body`` () =
        task {
            let handler = new MockHttpHandler(fun _ ->
                let resp = new HttpResponseMessage(HttpStatusCode.BadRequest)
                resp.Content <- new StringContent("""{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens exceeds limit"}}""", Encoding.UTF8, "application/json")
                resp
            )
            use client = createClient handler None (Some "key") None
            let messages = [ ChatMessage(ChatRole.User, "Test") ]

            let! ex = Assert.ThrowsAsync<HttpRequestException>(fun () ->
                task {
                    let! _ = drainStream (client.GetStreamingResponseAsync(messages))
                    ()
                }
            )

            Assert.Contains("max_tokens exceeds limit", ex.Message)
            Assert.Contains("400", ex.Message)
        }

    [<Fact>]
    let ``GetStreamingResponseAsync handles non-success HTTP status with plain text error body`` () =
        task {
            let handler = new MockHttpHandler(fun _ ->
                let resp = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                resp.Content <- new StringContent("Server crashed hard", Encoding.UTF8, "text/plain")
                resp
            )
            use client = createClient handler None (Some "key") None
            let messages = [ ChatMessage(ChatRole.User, "Crash") ]

            let! ex = Assert.ThrowsAsync<HttpRequestException>(fun () ->
                task {
                    let! _ = drainStream (client.GetStreamingResponseAsync(messages))
                    ()
                }
            )

            Assert.Contains("Server crashed hard", ex.Message)
            Assert.Contains("500", ex.Message)
        }

    [<Fact>]
    let ``GetStreamingResponseAsync throws when stream contains error event type`` () =
        task {
            let ssePayload = [
                """event: message_start
data: {"type":"message_start","message":{"id":"msg_err","usage":{"input_tokens":10,"output_tokens":1}}}"""
                """event: error
data: {"type":"error","error":{"type":"overloaded_error","message":"Anthropic is currently overloaded."}}"""
            ]

            let handler = new MockHttpHandler(fun _ -> sseResponse ssePayload)
            use client = createClient handler None (Some "key") None
            let messages = [ ChatMessage(ChatRole.User, "Test") ]

            let! ex = Assert.ThrowsAsync<HttpRequestException>(fun () ->
                task {
                    let! _ = drainStream (client.GetStreamingResponseAsync(messages))
                    ()
                }
            )

            Assert.Contains("Anthropic is currently overloaded.", ex.Message)
        }

    [<Fact>]
    let ``GetStreamingResponseAsync handles empty stream and ignores unrecognized event lines and empty comments`` () =
        task {
            let ssePayload = [
                ": keepalive comment"
                "event: ping"
                "data: {\"type\":\"ping\"}"
                "event: unknown_custom_event"
                "data: {\"type\":\"unknown_custom_event\"}"
                "data: not json at all"
                ""
                "event: message_start"
                "data: null"
                "event: message_delta"
                "data: {\"type\":\"message_delta\",\"delta\":{}}"
                "event: message_stop"
                "data: {}"
            ]

            let handler = new MockHttpHandler(fun _ -> sseResponse ssePayload)
            use client = createClient handler None (Some "key") None
            let messages = [ ChatMessage(ChatRole.User, "Ping test") ]

            let! updates = drainStream (client.GetStreamingResponseAsync(messages))

            Assert.NotEmpty(updates)
            let lastUpdate = updates |> Seq.last
            Assert.Null(lastUpdate.FinishReason)
        }

    [<Fact>]
    let ``GetStreamingResponseAsync supports cancellation token mid-stream`` () =
        task {
            use cts = new CancellationTokenSource()

            let asyncStreamHandler (req: HttpRequestMessage) (ct: CancellationToken) =
                task {
                    let stream = new MemoryStream()
                    let writer = new StreamWriter(stream, Encoding.UTF8, 1024, true)
                    writer.Write("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_cancel\"}}\n\n")
                    writer.Flush()
                    stream.Position <- 0L

                    let response = new HttpResponseMessage(HttpStatusCode.OK)
                    response.Content <- new StreamContent(stream)
                    return response
                }

            let handler = new AsyncMockHttpHandler(asyncStreamHandler)
            use client = createClient handler None (Some "key") None
            let messages = [ ChatMessage(ChatRole.User, "Cancel me") ]

            cts.Cancel()

            let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                task {
                    let stream = client.GetStreamingResponseAsync(messages, cancellationToken = cts.Token)
                    let! _ = drainStream stream
                    ()
                }
            )
            ()
        }

    [<Fact>]
    let ``GetResponseAsync buffers streaming updates and returns single ChatResponse`` () =
        task {
            let ssePayload = [
                """event: message_start
data: {"type":"message_start","message":{"id":"msg_full","usage":{"input_tokens":15,"output_tokens":0}}}"""
                """event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Complete "}}"""
                """event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"result."}}"""
                """event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":8}}"""
                """event: message_stop
data: {"type":"message_stop"}"""
            ]

            let handler = new MockHttpHandler(fun _ -> sseResponse ssePayload)
            use client = createClient handler None (Some "key") None
            let messages = [ ChatMessage(ChatRole.User, "Full completion") ]

            let! completion = client.GetResponseAsync(messages)

            Assert.NotNull(completion)
            Assert.Equal("Complete result.", completion.Text)
            Assert.Equal<Nullable<ChatFinishReason>>(Nullable ChatFinishReason.Stop, completion.FinishReason)
            Assert.NotNull(completion.Usage)
            Assert.Equal(Nullable 15L, completion.Usage.InputTokenCount)
            Assert.Equal(Nullable 8L, completion.Usage.OutputTokenCount)
        }

    [<Fact>]
    let ``GetService returns expected service instance or null`` () =
        let handler = new MockHttpHandler(fun _ -> new HttpResponseMessage(HttpStatusCode.OK))
        use client = createClient handler None (Some "key") None

        let selfService = client.GetService(typeof<AnthropicChatClient>, null)
        Assert.NotNull(selfService)

        let otherService = client.GetService(typeof<string>, null)
        Assert.Null(otherService)
