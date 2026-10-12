module Wanxiang.Tests.ModelChipRateLimitTests

// 决策 240–243：顶栏模型芯片 + 快速切换；模型服务限流头经 wanxiang.dev/provider.rate-limit 下发，错误卡倒计时。

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json.Nodes
open System.Threading
open Microsoft.Extensions.AI
open Xunit
open Wanxiang.Agent
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.UI

let private now = DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero)
let private lookupOf (pairs: (string * string) list) =
    let m = pairs |> List.map (fun (k, v) -> k.ToLowerInvariant(), v) |> Map.ofList
    fun (name: string) -> m.TryFind(name.ToLowerInvariant())

// ---------------------------------------------------------------- 头解析

[<Fact>]
let ``parseDelay understands seconds, go durations, epoch and absolute times`` () =
    Assert.Equal(Some 30, ProviderRateLimit.parseDelay now "30")
    Assert.Equal(Some 2, ProviderRateLimit.parseDelay now "1.5")
    Assert.Equal(Some 360, ProviderRateLimit.parseDelay now "6m0s")
    Assert.Equal(Some 1, ProviderRateLimit.parseDelay now "20ms")
    Assert.Equal(Some 3723, ProviderRateLimit.parseDelay now "1h2m3s")
    Assert.Equal(Some 90, ProviderRateLimit.parseDelay now (string (now.AddSeconds(90.0).ToUnixTimeSeconds())))
    Assert.Equal(Some 45, ProviderRateLimit.parseDelay now (now.AddSeconds(45.0).ToString("o")))
    Assert.Equal(Some 0, ProviderRateLimit.parseDelay now (now.AddSeconds(-10.0).ToString("o")))
    Assert.Equal(None, ProviderRateLimit.parseDelay now "soon")
    Assert.Equal(None, ProviderRateLimit.parseDelay now "")

[<Fact>]
let ``openai style headers wait for the exhausted dimension`` () =
    let limit =
        ProviderRateLimit.ofHeaders now (lookupOf [
            "x-ratelimit-remaining-requests", "0"
            "x-ratelimit-remaining-tokens", "1200"
            "x-ratelimit-reset-requests", "12s"
            "x-ratelimit-reset-tokens", "6m0s" ])
    match limit with
    | Some l ->
        Assert.Equal(Some 0, l.remainingRequests)
        Assert.Equal(Some 1200, l.remainingTokens)
        // 请求数见底：等请求维度恢复（12 秒），不是等更晚的 token 维度
        Assert.Equal(Some 12, l.resetSeconds)
        Assert.True(ProviderRateLimit.isExhausted l)
        Assert.Equal(Some 12, ProviderRateLimit.waitSeconds l)
    | None -> Assert.Fail "expected limit"

[<Fact>]
let ``anthropic style headers and retry-after take priority`` () =
    let limit =
        ProviderRateLimit.ofHeaders now (lookupOf [
            "Anthropic-RateLimit-Tokens-Remaining", "0"
            "anthropic-ratelimit-tokens-reset", now.AddSeconds(75.0).ToString("o")
            "Retry-After", "20" ])
    match limit with
    | Some l ->
        Assert.Equal(Some 0, l.remainingTokens)
        Assert.Equal(Some 75, l.resetSeconds)
        Assert.Equal(Some 20, l.retryAfterSeconds)
        Assert.Equal(Some 20, ProviderRateLimit.waitSeconds l)
    | None -> Assert.Fail "expected limit"

[<Fact>]
let ``no rate limit headers yields none so no empty event is sent`` () =
    Assert.Equal(None, ProviderRateLimit.ofHeaders now (lookupOf [ "content-type", "application/json" ]))

[<Fact>]
let ``countdown text reads naturally`` () =
    Assert.Equal("现在可以重试了。", ProviderRateLimit.countdownText 0)
    Assert.Equal("9 秒后可重试。", ProviderRateLimit.countdownText 9)
    Assert.Equal("1 分 05 秒后可重试。", ProviderRateLimit.countdownText 65)
    Assert.Equal("约 2 小时 1 分后可重试。", ProviderRateLimit.countdownText 7290)

// ---------------------------------------------------------------- 协议

let private sampleNotice () =
    ProviderRateLimitNotice
        {| conversationId = Guid.CreateVersion7()
           generationId = Guid.CreateVersion7()
           providerId = "openai"
           limit =
             { remainingRequests = Some 0
               remainingTokens = None
               resetSeconds = Some 42
               retryAfterSeconds = Some 30 } |}

[<Fact>]
let ``rate limit notice is an additive wanxiang dev custom event and round trips`` () =
    let ev = sampleNotice ()
    let frame = WireAgui.encode ev
    let node = JsonNode.Parse(frame).AsObject()
    Assert.Equal("CUSTOM", node["type"].GetValue<string>())
    Assert.Equal("wanxiang.dev/provider.rate-limit", node["name"].GetValue<string>())
    // 缺省项不出现在线上，旧端与新端都不会读到编造的 0
    Assert.Null(node["value"].["remainingTokens"])
    match WireAgui.tryDecode frame, ev with
    | Ok(Some(ProviderRateLimitNotice d)), ProviderRateLimitNotice expected ->
        Assert.Equal(expected.conversationId, d.conversationId)
        Assert.Equal(expected.generationId, d.generationId)
        Assert.Equal("openai", d.providerId)
        Assert.Equal(expected.limit, d.limit)
    | other, _ -> Assert.Fail(sprintf "unexpected decode %A" other)

[<Fact>]
let ``an older decoder sees only an unknown wanxiang dev event it can drop`` () =
    // 旧端没有这个类型：解码成「不认识」（Ok None），丢弃且不断连（55.8 R1）。
    let frame = WireAgui.encode (sampleNotice ())
    let renamed = frame.Replace("wanxiang.dev/provider.rate-limit", "wanxiang.dev/provider.rate-limit-v0-unknown")
    match WireAgui.tryDecode renamed with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "should be ignored, got %A" other)

// ---------------------------------------------------------------- 传输层：真实 429 带头

type private RateLimitedUpstream(headers: (string * string) list) =
    let port =
        let probe = new TcpListener(IPAddress.Loopback, 0)
        probe.Start()
        let chosen = (probe.LocalEndpoint :?> IPEndPoint).Port
        probe.Stop()
        chosen
    let listener = new HttpListener()
    do
        listener.Prefixes.Add $"http://127.0.0.1:{port}/"
        listener.Start()
        async {
            try
                while listener.IsListening do
                    let! context = listener.GetContextAsync() |> Async.AwaitTask
                    use reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)
                    reader.ReadToEnd() |> ignore
                    context.Response.StatusCode <- 429
                    context.Response.ContentType <- "application/json"
                    for (k, v) in headers do context.Response.Headers[k] <- v
                    let bytes = Encoding.UTF8.GetBytes """{"error":{"message":"rate limited","type":"rate_limit"}}"""
                    context.Response.OutputStream.Write(bytes, 0, bytes.Length)
                    context.Response.Close()
            with _ -> ()
        }
        |> Async.Start
    member _.BaseUrl = $"http://127.0.0.1:{port}"
    interface IDisposable with
        member _.Dispose() =
            try
                listener.Stop()
                listener.Close()
            with _ -> ()

let private providerOf (kind: string) (baseUrl: string) : ProviderConfig =
    { id = "limited"
      kind = kind
      label = "Limited"
      baseUrl = baseUrl
      apiKey = Some "k"
      models = [ "m" ]
      defaultModel = "m"
      timeoutSeconds = 10
      maxRetries = 0
      enabled = true
      promptCaching = false
      headers = Map.empty
      extraJson = None }

let private runAgainst (kind: string) (pathSuffix: string) =
    task {
        use upstream =
            new RateLimitedUpstream(
                [ "x-ratelimit-remaining-requests", "0"
                  "x-ratelimit-reset-requests", "40s"
                  "Retry-After", "25" ])
        let provider = providerOf kind (upstream.BaseUrl + pathSuffix)
        let session = { SessionConfig.empty with provider = "limited"; model = "m" }
        let runtime = AgentRuntime(provider, session, [], null, 0)
        let agentSession = runtime.CreateSession(Guid.NewGuid())
        let! result = runtime.RunStreaming(agentSession, [ ChatMessage(ChatRole.User, "hi") ], ignore, CancellationToken.None)
        return result, runtime.LastRateLimit
    }

[<Theory>]
[<InlineData("openai", "/v1")>]
[<InlineData("anthropic", "")>]
let ``a 429 with rate limit headers is classified and its headers survive to the runtime`` (kind: string, suffix: string) =
    task {
        let! result, limit = runAgainst kind suffix
        match result with
        | Failed err -> Assert.Equal(GenerationErrorKind.ProviderRateLimited, err.kind)
        | other -> Assert.Fail(sprintf "expected failure, got %A" other)
        match limit with
        | Some l ->
            Assert.Equal(Some 0, l.remainingRequests)
            Assert.Equal(Some 40, l.resetSeconds)
            Assert.Equal(Some 25, l.retryAfterSeconds)
        | None -> Assert.Fail "rate limit headers were lost"
    }

// ---------------------------------------------------------------- 错误卡倒计时登记

[<Fact>]
let ``retry countdown deadline is fixed at finish time and survives re-reads`` () =
    let gid = Guid.CreateVersion7()
    let err = GenerationError.create ProviderRateLimited "限流"
    RetryCountdown.notice gid { ProviderRateLimit.empty with resetSeconds = Some 60; remainingRequests = Some 0 } now
    RetryCountdown.attach gid err (now.AddSeconds 2.0)
    match RetryCountdown.tryGet err (now.AddSeconds 30.0) with
    | Some info ->
        Assert.Equal(now.AddSeconds 60.0, info.retryAt)
        Assert.Equal(30, RetryCountdown.remainingSeconds info (now.AddSeconds 30.0))
        Assert.Equal(0, RetryCountdown.remainingSeconds info (now.AddSeconds 90.0))
        Assert.True(info.limit |> Option.exists ProviderRateLimit.isExhausted)
    | None -> Assert.Fail "expected countdown"

[<Fact>]
let ``non retryable errors never get a countdown`` () =
    let gid = Guid.CreateVersion7()
    let err = { GenerationError.create ProviderAuthFailed "密钥无效" with retryAfterSeconds = Some 10 }
    RetryCountdown.notice gid { ProviderRateLimit.empty with retryAfterSeconds = Some 10 } now
    RetryCountdown.attach gid err now
    Assert.Equal(None, RetryCountdown.tryGet err now)

// ---------------------------------------------------------------- 快速切换纯逻辑

let private provider (id: string) (label: string) (models: string list) : ProviderInfo =
    { id = id; label = label; kind = "openai"; baseUrl = ""; hasApiKey = true; models = models
      defaultModel = List.head models; timeoutSeconds = 30; maxRetries = 0; enabled = true }

let private catalog =
    { Catalog.empty with
        providers =
          [ provider "oa" "OpenAI" [ "gpt-4o"; "gpt-4o-mini"; "o3" ]
            provider "ds" "DeepSeek" [ "deepseek-chat"; "deepseek-reasoner" ]
            { provider "off" "Disabled" [ "x" ] with enabled = false } ]
        generation = { Catalog.empty.generation with defaultProvider = Some "ds"; defaultModel = Some "deepseek-chat" } }

[<Fact>]
let ``switcher lists usable models with provider, current and default marks`` () =
    let items = ModelSwitcher.options catalog (Some("oa", "o3"))
    Assert.Equal(5, items.Length)
    Assert.DoesNotContain(items, fun o -> o.providerId = "off")
    Assert.Equal<string list>([ "o3" ], items |> List.filter _.isCurrent |> List.map _.model)
    Assert.Equal<string list>([ "deepseek-chat" ], items |> List.filter _.isDefault |> List.map _.model)
    Assert.All(items, fun o -> Assert.False(String.IsNullOrWhiteSpace o.providerLabel))

[<Fact>]
let ``switcher search matches model or provider and ranks prefix hits first`` () =
    let items = ModelSwitcher.options catalog None
    Assert.Equal<string list>([ "deepseek-chat"; "deepseek-reasoner" ], ModelSwitcher.filter "deepseek" items |> List.map _.model)
    Assert.Equal<string list>([ "gpt-4o-mini" ], ModelSwitcher.filter "openai MINI" items |> List.map _.model)
    Assert.Equal<string list>([ "o3"; "gpt-4o"; "gpt-4o-mini" ] |> List.sort, ModelSwitcher.filter "o" items |> List.filter (fun o -> o.providerId = "oa") |> List.map _.model |> List.sort)
    Assert.Equal("o3", (ModelSwitcher.filter "o" items |> List.head).model)
    Assert.Empty(ModelSwitcher.filter "claude" items)
    Assert.Equal(items.Length, (ModelSwitcher.filter "  " items).Length)

[<Fact>]
let ``switching copy says it applies from the next reply`` () =
    Assert.Contains("从下一条回复开始生效", ModelSwitcher.switchedToast "OpenAI" "o3")
    Assert.Contains("已有回复不会改变", ModelSwitcher.appliesNote)
