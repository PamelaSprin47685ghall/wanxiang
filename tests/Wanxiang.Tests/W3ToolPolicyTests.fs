module Wanxiang.Tests.W3ToolPolicyTests

// W3「工具：可读、可控」的验收测试（第 70 节–74）。
// 每个测试名前缀 [AC-n] 对应 docs/ux/redesign-proposal.md W3 验收条目。

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Server
open Wanxiang.Store
open Wanxiang.Tests.Helpers

// ---------------------------------------------------------------- 配置与默认值

/// 从真实序列化结果出发，把 extra 接在 `[tools]` 的 callTimeoutSeconds 之后。
let private baseToml (extraTools: string) =
    let text = TomlCodec.serialize (AppConfig.defaults (Guid.Parse "0192b4a0-0000-7000-8000-000000000001"))
    let marker = "callTimeoutSeconds = 30\n"
    let at = text.IndexOf("[tools]")
    let k = text.IndexOf(marker, at)
    Assert.True(k > 0)
    text.Insert(k + marker.Length, extraTools + "\n")

let private parseOk (toml: string) =
    match TomlCodec.tryParse toml with
    | Ok cfg -> cfg
    | Error es -> failwithf "解析失败：%A" es

[<Fact>]
let ``[AC-1] 既有配置没写 policy：全自动、不需要审批（行为与升级前一致）`` () =
    let cfg = parseOk (baseToml "")
    Assert.Equal(Auto, cfg.tools.policyDefault)
    Assert.True(Map.isEmpty cfg.tools.policy)
    Assert.Equal(300, cfg.tools.approvalTimeoutSeconds)
    Assert.False cfg.tools.exposeDebug
    let broker = ToolApprovalBroker((fun () -> cfg), (fun _ _ -> ()))
    Assert.False(broker.NeedsApproval(Guid.NewGuid(), "builtin:time"))
    Assert.False(broker.NeedsApproval(Guid.NewGuid(), "mcp:fs/read"))

[<Fact>]
let ``[AC-1] 重写既有配置不会悄悄写入 policy（老用户不被改成询问）`` () =
    let cfg = parseOk (baseToml "")
    let text = TomlCodec.serialize cfg
    Assert.DoesNotContain("policy", text)
    Assert.DoesNotContain("exposeDebug", text)
    Assert.Equal(Auto, (parseOk text).tools.policyDefault)

[<Fact>]
let ``[AC-1] 全新安装写出的配置默认先询问（time 保持自动）`` () =
    let dir = tempDir ()
    try
        let path = Path.Combine(dir, "config.toml")
        match ConfigStore.Open(path, ignore, ignore) with
        | Error e -> failwith e
        | Ok store ->
            use _ = store
            let cfg = parseOk (File.ReadAllText path)
            Assert.Equal(AskFirst, cfg.tools.policyDefault)
            Assert.Equal(Auto, ToolsConfig.policyOf cfg.tools "builtin:time")
            Assert.Equal(AskFirst, ToolsConfig.policyOf cfg.tools "builtin:file.read")
            Assert.Equal(AskFirst, ToolsConfig.policyOf cfg.tools "mcp:x/y")
    finally cleanup dir

[<Fact>]
let ``[AC-2] tools.policy 可解析、可往返；非法取值被拒绝`` () =
    let toml =
        baseToml "approvalTimeoutSeconds = 42\nexposeDebug = true\n\n[tools.policy]\ndefault = \"first\"\n\"builtin:file.read\" = \"always\"\n\"mcp:fs/write\" = \"auto\"\n"
    let cfg = parseOk toml
    Assert.Equal(AskFirst, cfg.tools.policyDefault)
    Assert.Equal(AskEach, ToolsConfig.policyOf cfg.tools "builtin:file.read")
    Assert.Equal(Auto, ToolsConfig.policyOf cfg.tools "mcp:fs/write")
    Assert.Equal(42, cfg.tools.approvalTimeoutSeconds)
    Assert.True cfg.tools.exposeDebug
    let again = parseOk (TomlCodec.serialize cfg)
    Assert.Equal(cfg.tools, again.tools)
    match TomlCodec.tryParse (baseToml "[tools.policy]\n\"builtin:time\" = \"sometimes\"\n") with
    | Ok _ -> failwith "非法策略值应被拒绝"
    | Error es -> Assert.Contains(es, fun e -> e.Contains "tools.policy")
    match TomlCodec.tryParse (baseToml "approvalTimeoutSeconds = 0") with
    | Ok _ -> failwith "审批超时必须为正"
    | Error es -> Assert.Contains(es, fun e -> e.Contains "approvalTimeoutSeconds")

[<Fact>]
let ``[AC-2] 设置页载荷：updateTools 与老的 updateGeneration 都不会冲掉 policy`` () =
    let cfg0 = AppConfig.defaults (Guid.NewGuid())
    let cfg = { cfg0 with tools = { cfg0.tools with policy = Map.ofList [ "builtin:time", AskEach ]; policyDefault = AskFirst } }
    // 老客户端：只发 fileReadRoots + callTimeoutSeconds
    let legacy = JsonNode.Parse("""{"tools":{"fileReadRoots":["/tmp"],"callTimeoutSeconds":20}}""").AsObject()
    match ConfigMutation.updateGeneration cfg legacy with
    | Ok c ->
        Assert.Equal<string list>([ "/tmp" ], c.tools.fileReadRoots)
        Assert.Equal(AskEach, ToolsConfig.policyOf c.tools "builtin:time")
        Assert.Equal(AskFirst, c.tools.policyDefault)
    | Error es -> failwithf "%A" es
    // 新客户端：通过 tools 子对象设置策略与调试开关
    let modern =
        JsonNode.Parse("""{"tools":{"fileReadRoots":["/data"],"policy":{"default":"auto","mcp:a/b":"always"},"exposeDebug":true}}""").AsObject()
    match ConfigMutation.updateGeneration cfg modern with
    | Ok c ->
        Assert.Equal(Auto, c.tools.policyDefault)
        Assert.Equal(AskEach, ToolsConfig.policyOf c.tools "mcp:a/b")
        Assert.True c.tools.exposeDebug
    | Error es -> failwithf "%A" es
    let bad = JsonNode.Parse("""{"tools":{"policy":{"x":"nope"}}}""").AsObject()
    match ConfigMutation.updateGeneration cfg bad with
    | Ok _ -> failwith "非法策略应被拒绝"
    | Error es -> Assert.Contains(es, fun e -> e.Contains "tools.policy")
    // 协议级入口 updateTools：缺省字段保持原值
    match ConfigMutation.updateTools cfg (JsonNode.Parse("""{"exposeDebug":true}""").AsObject()) with
    | Ok c ->
        Assert.True c.tools.exposeDebug
        Assert.Equal(AskEach, ToolsConfig.policyOf c.tools "builtin:time")
    | Error es -> failwithf "%A" es

// ---------------------------------------------------------------- 目录整理与人话摘要

[<Fact>]
let ``[AC-7] builtin_echo 默认不进目录；tools.exposeDebug 打开才可见；老对话照常可用`` () =
    let cfg0 = AppConfig.defaults (Guid.NewGuid())
    let withRoots = { cfg0 with tools = { cfg0.tools with fileReadRoots = [ "/tmp" ] } }
    let ids (cfg: AppConfig) =
        let r = ToolRegistry((fun () -> cfg), ignore)
        r.Descriptors() |> List.map (fun d -> d.id)
    Assert.DoesNotContain("builtin:echo", ids withRoots)
    Assert.Contains("builtin:time", ids withRoots)
    Assert.Contains("builtin:file.read", ids withRoots)
    let debug = { withRoots with tools = { withRoots.tools with exposeDebug = true } }
    Assert.Contains("builtin:echo", ids debug)
    // 已经选了 echo 的老对话：仍能构建出该工具，也不会被报成「配置漂移」
    let registry = ToolRegistry((fun () -> withRoots), ignore)
    let session = { SessionConfig.empty with tools = [ "builtin:echo" ] }
    Assert.Single(registry.BuildTools session) |> ignore
    Assert.Empty(registry.MissingTools session)

// ---------------------------------------------------------------- 协议加法

[<Fact>]
let ``[AC-4/6] 审批事件往返，并走 wanxiang.dev CUSTOM 命名空间（标准客户端合法忽略）`` () =
    let id = Guid.NewGuid()
    let conv = Guid.NewGuid()
    let events =
        [ ToolApprovalCapability {| supported = true |}
          ToolApprovalRequested
              {| approvalId = id; conversationId = conv; generationId = Guid.NewGuid(); callId = "c1"; toolName = "builtin_time"
                 summary = "查看当前时间"; arguments = JsonNode.Parse """{"a":1}"""; policy = "always"; expiresInSeconds = 300 |}
          ToolApprovalDecision {| approvalId = id; approved = false |}
          ToolApprovalResolved {| approvalId = id; conversationId = conv; status = "denied"; reason = "用户拒绝了此次工具调用。"; alreadyHandled = true |}
          ConfigUpdateTools {| requestId = Guid.NewGuid(); tools = JsonNode.Parse("""{"exposeDebug":true}""").AsObject() |} ]
    for ev in events do
        let text = WireAgui.encode ev
        let node = JsonNode.Parse text
        Assert.Equal("CUSTOM", node["type"].GetValue<string>())
        Assert.StartsWith("wanxiang.dev/", node["name"].GetValue<string>())
        match WireAgui.tryDecode text with
        | Ok(Some decoded) -> Assert.Equal(WireEvent.typeName ev, WireEvent.typeName decoded)
        | other -> failwithf "往返失败 %s：%A" (WireEvent.typeName ev) other
    // 旧客户端：不认识的 wanxiang.dev 自定义事件被宽容忽略，而不是报错断连
    let unknown = """{"type":"CUSTOM","name":"wanxiang.dev/tool.future-thing","value":{"x":1}}"""
    match WireAgui.tryDecode unknown with
    | Ok(Some _) -> failwith "未知事件不应被解成已知事件"
    | Ok None | Error _ -> () // 客户端对两种结果都忽略（WsClient：Ok None | Error _ -> ()），连接不断

// ---------------------------------------------------------------- 审批代理

let private policyConfig (policy: ToolPolicy) (timeoutSeconds: int) : AppConfig =
    let c = AppConfig.defaults (Guid.NewGuid())
    { c with tools = { c.tools with policyDefault = policy; approvalTimeoutSeconds = timeoutSeconds } }

let private newBroker (cfg: AppConfig) (capable: bool) =
    let sent = ResizeArray<Guid * WireEvent>()
    let broker = ToolApprovalBroker((fun () -> cfg), (fun c ev -> lock sent (fun () -> sent.Add((c, ev)))))
    broker.CapabilityProbe <- fun _ -> capable
    broker, sent

let private request (broker: ToolApprovalBroker) (conv: Guid) (ct: CancellationToken) =
    broker.Request(conv, Guid.NewGuid(), "call-1", "builtin_time", "builtin:time", JsonNode.Parse "{}", ct)

let private pendingId (sent: ResizeArray<Guid * WireEvent>) =
    lock sent (fun () ->
        sent |> Seq.pick (fun (_, ev) -> match ev with ToolApprovalRequested r -> Some r.approvalId | _ -> None))

[<Fact>]
let ``[AC-2] 每次询问：挂起等待，期间没有任何人决定就一直不返回；批准后继续`` () =
    let conv = Guid.NewGuid()
    let broker, sent = newBroker (policyConfig AskEach 300) true
    Assert.True(broker.NeedsApproval(conv, "builtin:time"))
    let task = request broker conv CancellationToken.None
    Thread.Sleep 400
    Assert.False task.IsCompleted
    Assert.Equal(1, broker.PendingCount)
    broker.Decide(pendingId sent, true, ignore)
    Assert.Equal(Approved, task.Result)
    Assert.Equal(0, broker.PendingCount)
    // 每次询问：批准一次不等于永久放行
    Assert.True(broker.NeedsApproval(conv, "builtin:time"))

[<Fact>]
let ``[AC-2] 首次询问：同一对话里批准一次后不再问，换对话仍然问`` () =
    let conv, other = Guid.NewGuid(), Guid.NewGuid()
    let broker, sent = newBroker (policyConfig AskFirst 300) true
    Assert.True(broker.NeedsApproval(conv, "builtin:time"))
    let task = request broker conv CancellationToken.None
    broker.Decide(pendingId sent, true, ignore)
    Assert.Equal(Approved, task.Result)
    Assert.False(broker.NeedsApproval(conv, "builtin:time"))
    Assert.True(broker.NeedsApproval(other, "builtin:time"))
    // 拒绝不被记住
    let broker2, sent2 = newBroker (policyConfig AskFirst 300) true
    let t2 = request broker2 conv CancellationToken.None
    broker2.Decide(pendingId sent2, false, ignore)
    Assert.Equal(Denied(ToolSummary.userDeniedText, true), t2.Result)
    Assert.True(broker2.NeedsApproval(conv, "builtin:time"))

[<Fact>]
let ``[AC-2] 断线重连后待审批请求仍可补发（PendingFor）`` () =
    let conv = Guid.NewGuid()
    let broker, sent = newBroker (policyConfig AskEach 300) true
    let task = request broker conv CancellationToken.None
    let id = pendingId sent
    match broker.PendingFor conv with
    | [ ToolApprovalRequested r ] ->
        Assert.Equal(id, r.approvalId)
        Assert.True(r.expiresInSeconds > 0 && r.expiresInSeconds <= 300)
        Assert.Equal("查看当前时间", r.summary)
    | other -> failwithf "应恰有一条待审批：%A" other
    Assert.Empty(broker.PendingFor(Guid.NewGuid()))
    broker.Decide(id, true, ignore)
    task.Wait()
    Assert.Empty(broker.PendingFor conv)

[<Fact>]
let ``[AC-3] 两个客户端同时决定：恰有一个被接受，另一个收到「已处理」`` () =
    for _ in 1..25 do
        let conv = Guid.NewGuid()
        let broker, sent = newBroker (policyConfig AskEach 300) true
        let task = request broker conv CancellationToken.None
        let id = pendingId sent
        let replies = ResizeArray<WireEvent>()
        let reply ev = lock replies (fun () -> replies.Add ev)
        let barrier = new Barrier 2
        let a = Task.Run(fun () -> barrier.SignalAndWait(); broker.Decide(id, true, reply))
        let b = Task.Run(fun () -> barrier.SignalAndWait(); broker.Decide(id, false, reply))
        Task.WaitAll(a, b)
        let outcome = task.Result
        // 恰有一次广播（接受者），恰有一次「已处理」回复（落败者）
        let resolved =
            lock sent (fun () -> sent |> Seq.choose (fun (_, ev) -> match ev with ToolApprovalResolved r -> Some r | _ -> None) |> List.ofSeq)
        Assert.Equal(1, resolved.Length)
        Assert.False resolved.Head.alreadyHandled
        Assert.Equal(1, replies.Count)
        match replies[0] with
        | ToolApprovalResolved r ->
            Assert.True r.alreadyHandled
            Assert.Equal(resolved.Head.status, r.status)
        | other -> failwithf "%A" other
        match outcome with
        | Approved -> Assert.Equal("approved", resolved.Head.status)
        | Denied(_, true) -> Assert.Equal("denied", resolved.Head.status)
        | other -> failwithf "%A" other

[<Fact>]
let ``[AC-5] 重启后挂起的审批消失：旧审批编号得到「已失效」，不会卡死`` () =
    let broker, _ = newBroker (policyConfig AskEach 300) true
    Assert.Equal(0, broker.PendingCount)
    let replies = ResizeArray<WireEvent>()
    broker.Decide(Guid.NewGuid(), true, replies.Add)
    match Seq.toList replies with
    | [ ToolApprovalResolved r ] ->
        Assert.True r.alreadyHandled
        Assert.Equal("expired", r.status)
    | other -> failwithf "%A" other

[<Fact>]
let ``审批能力的观察者在宽限内出现（分叉后立即生成）：照常发起审批而不是秒拒`` () =
    let conv = Guid.NewGuid()
    let broker, sent = newBroker (policyConfig AskEach 300) false
    let mutable capable = false
    broker.CapabilityProbe <- fun _ -> capable
    use cts = new CancellationTokenSource()
    let task = request broker conv cts.Token
    Thread.Sleep 300
    capable <- true
    let deadline = DateTime.UtcNow.AddSeconds 3.0
    while DateTime.UtcNow < deadline && (lock sent (fun () -> sent.Count)) = 0 do Thread.Sleep 20
    Assert.False task.IsCompleted
    Assert.Contains(lock sent (fun () -> List.ofSeq sent), fun (_, ev) -> match ev with ToolApprovalRequested _ -> true | _ -> false)
    cts.Cancel()

[<Fact>]
let ``[AC-6] 没有具备审批能力的客户端在线：立即拒绝并给出人话原因，不等待`` () =
    let conv = Guid.NewGuid()
    let broker, sent = newBroker (policyConfig AskEach 300) false
    let task = request broker conv CancellationToken.None
    Assert.True(task.Wait 2000)
    match task.Result with
    | Denied(reason, false) ->
        Assert.Contains("没有支持审批的客户端", reason)
        Assert.Contains("拒绝", reason)
    | other -> failwithf "%A" other
    Assert.Equal(0, broker.PendingCount)
    Assert.Empty(lock sent (fun () -> List.ofSeq sent))

[<Fact>]
let ``[AC-6] 审批超时自动拒绝（缺省 300 秒，可配置）`` () =
    Assert.Equal(300, ToolsConfig.defaults.approvalTimeoutSeconds)
    let conv = Guid.NewGuid()
    let broker, sent = newBroker (policyConfig AskEach 1) true
    let sw = Diagnostics.Stopwatch.StartNew()
    let task = request broker conv CancellationToken.None
    Assert.True(task.Wait 10000)
    Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds 800.0)
    match task.Result with
    | Denied(reason, false) -> Assert.Contains("审批超时", reason)
    | other -> failwithf "%A" other
    let statuses = lock sent (fun () -> sent |> Seq.choose (fun (_, ev) -> match ev with ToolApprovalResolved r -> Some r.status | _ -> None) |> List.ofSeq)
    Assert.Equal<string list>([ "expired" ], statuses)

[<Fact>]
let ``[AC-2] 取消生成时等待中的审批作废`` () =
    let conv = Guid.NewGuid()
    let gen = Guid.NewGuid()
    let broker, sent = newBroker (policyConfig AskEach 300) true
    use cts = new CancellationTokenSource()
    let task = broker.Request(conv, gen, "c", "builtin_time", "builtin:time", JsonNode.Parse "{}", cts.Token)
    Assert.Equal(1, broker.PendingCount)
    broker.CancelGeneration(conv, Guid.NewGuid()) // 别的生成：不受影响
    Assert.Equal(1, broker.PendingCount)
    broker.CancelGeneration(conv, gen)
    Assert.Equal(ApprovalCancelled, task.Result)
    Assert.Equal(0, broker.PendingCount)
    // 对话被删除同理
    let t2 = broker.Request(conv, gen, "c2", "builtin_time", "builtin:time", JsonNode.Parse "{}", CancellationToken.None)
    broker.ClearConversation conv
    Assert.Equal(ApprovalCancelled, t2.Result)

// ---------------------------------------------------------------- 端到端（真实主机 + 假上游）

/// 假的 OpenAI 兼容上游：第一次请求回一个 builtin_time 调用，之后回一句文字。
type private FakeModel() =
    let port = Wanxiang.Tests.ServerE2eTests.pickPort ()
    let listener = new HttpListener()
    let bodies = ResizeArray<string>()
    do
        listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
        listener.Start()
        let rec loop () =
            task {
                try
                    let! ctx = listener.GetContextAsync()
                    use reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)
                    let body = reader.ReadToEnd()
                    let n = lock bodies (fun () -> bodies.Add body; bodies.Count)
                    ctx.Response.StatusCode <- 200
                    ctx.Response.ContentType <- "text/event-stream"
                    let chunk (delta: string) (finish: string) =
                        sprintf """data: {"id":"c","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":%s,"finish_reason":%s}]}""" delta finish
                        + "\n\n"
                    let sb = StringBuilder()
                    if n = 1 then
                        sb.Append(chunk """{"role":"assistant","content":""}""" "null") |> ignore
                        sb.Append(chunk """{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"builtin_time","arguments":"{}"}}]}""" "null") |> ignore
                        sb.Append(chunk "{}" "\"tool_calls\"") |> ignore
                    else
                        sb.Append(chunk """{"role":"assistant","content":"好的，已完成。"}""" "null") |> ignore
                        sb.Append(chunk "{}" "\"stop\"") |> ignore
                    sb.Append("data: [DONE]\n\n") |> ignore
                    let bytes = Encoding.UTF8.GetBytes(sb.ToString())
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                    ctx.Response.Close()
                    return! loop ()
                with _ -> ()
            }
        loop () |> ignore
    member _.Url = sprintf "http://127.0.0.1:%d/v1" port
    member _.Bodies = lock bodies (fun () -> List.ofSeq bodies)
    interface IDisposable with
        member _.Dispose() = try listener.Stop(); listener.Close() with _ -> ()

type private Host(port: int, token: string, providerUrl: string, tools: ToolsConfig, dir: string) =
    let configPath = Path.Combine(dir, "config.toml")
    let mutable server: ServerApp option = None
    member _.Dir = dir
    member _.Start() =
        if not (File.Exists configPath) then
            let id = Guid.CreateVersion7()
            let cfg =
                { AppConfig.defaults id with
                    listen = sprintf "127.0.0.1:%d" port
                    tools = tools
                    generation = { (AppConfig.defaults id).generation with autoTitle = false }
                    providers =
                        Map.ofList
                            [ "test-provider",
                              { id = "test-provider"; kind = "openai"; label = "T"; baseUrl = providerUrl; apiKey = None
                                models = [ "test-model" ]; defaultModel = "test-model"
                                timeoutSeconds = ProviderConfig.defaultTimeoutSeconds; maxRetries = 0; enabled = true
                                promptCaching = false; headers = Map.empty; extraJson = None; visionOverrides = Map.empty } ]
                    authClients =
                        [ { tokenHash = ClientToken.ClientToken.hash token; name = "w3"; createdAtUtc = DateTimeOffset.UtcNow
                            lastSeenUtc = None; revoked = false } ] }
            File.WriteAllText(configPath, TomlCodec.serialize cfg)
        let app = new ServerApp(dir, configPath, false, None, ignore)
        app.Start(false)
        server <- Some app
    member _.Stop() =
        match server with
        | Some s -> (s :> IDisposable).Dispose(); server <- None
        | None -> ()
    member this.LogText() =
        let dir = DataPaths.eventsDir dir
        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.jsonseq") |> Array.map File.ReadAllText |> String.concat "\n"
        else ""
    interface IDisposable with
        member this.Dispose() = this.Stop(); cleanup dir

module private Ws =
    module E = Wanxiang.Tests.ServerE2eTests.E2e
    let typeOf (o: JsonObject) = o["type"].GetValue<string>()
    let payload (o: JsonObject) (k: string) : JsonNode = o["payload"].AsObject()[k]
    let sendEv ws (ev: WireEvent) ct = E.send ws (JsonNode.Parse(WireCodec.encode ev).AsObject()) ct
    let sendCmd ws (cmd: ClientCommand) ct = E.send ws (JsonNode.Parse(WireCodec.encodeCommand cmd).AsObject()) ct
    let wait ws ct name = E.waitFor ws ct (fun o -> typeOf o = name)
    let rec strings (n: JsonNode) : string list =
        match n with
        | null -> []
        | :? JsonObject as o -> [ for kv in o do yield! strings kv.Value ]
        | :? JsonArray as a -> [ for i in a do yield! strings i ]
        | v -> (try [ v.GetValue<string>() ] with _ -> [])
    /// 读到类型满足 stop 为止，返回途中见到的全部事件
    let collectUntil ws (ct: CancellationToken) (stop: JsonObject -> bool) : JsonObject list =
        let seen = ResizeArray<JsonObject>()
        let mutable fin = false
        while not fin do
            let ev = E.recvEvent ws ct
            seen.Add ev
            if stop ev then fin <- true
        List.ofSeq seen

type private Session = { host: Host; model: FakeModel; port: int; token: string; conv: Guid }

let private withSession (tools: ToolsConfig) (body: Session -> CancellationToken -> unit) =
    use model = new FakeModel()
    let port, token = Wanxiang.Tests.ServerE2eTests.pickPort (), ClientToken.ClientToken.generate ()
    use host = new Host(port, token, model.Url, tools, tempDir ())
    host.Start()
    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 40.0)
    body { host = host; model = model; port = port; token = token; conv = Guid.NewGuid() } timeout.Token

/// 连接、认证、建对话、观察、（可选）声明审批能力，返回 socket。
let private connect (s: Session) (ct: CancellationToken) (capable: bool) (create: bool) =
    let ws = Wanxiang.Tests.ServerE2eTests.E2e.conn s.port s.token ct
    Ws.wait ws ct "auth.accepted" |> ignore
    if create then
        Ws.sendCmd ws
            (CreateConversation
                {| invocationId = Guid.NewGuid(); conversationId = s.conv; title = "审批"
                   config = { SessionConfig.empty with provider = "test-provider"; model = "test-model"; tools = [ "builtin:time" ] } |})
            ct
        Ws.wait ws ct "command.committed" |> ignore
    Ws.sendEv ws (ObserveConversation {| conversationId = s.conv |}) ct
    let snap = Ws.wait ws ct "conversation.snapshot"
    Ws.sendEv ws (CursorAdvanced {| id = (Ws.payload snap "lastCommitId").GetValue<uint64>() |}) ct
    if capable then Ws.sendEv ws (ToolApprovalCapability {| supported = true |}) ct
    ws

let private ask (s: Session) (ws: Net.WebSockets.ClientWebSocket) (ct: CancellationToken) =
    Ws.sendCmd ws
        (SendUserMessage
            {| invocationId = Guid.NewGuid(); conversationId = s.conv
               messageJson = JsonNode.Parse("""{"role":"user","contents":[{"text":"几点了"}]}""") |})
        ct

let private askEach = { ToolsConfig.defaults with policyDefault = AskEach }

let private finishedStatus (ev: JsonObject) = Ws.typeOf ev = "generation.finished"

let private conversationStrings (s: Session) (ws: Net.WebSockets.ClientWebSocket) (ct: CancellationToken) =
    Ws.sendEv ws (ObserveConversation {| conversationId = s.conv |}) ct
    let snap = Ws.wait ws ct "conversation.snapshot"
    Ws.strings (Ws.payload snap "messages")

[<Fact>]
let ``[AC-1] e2e 默认配置：工具无需审批直接执行，旧客户端（无审批能力）照常工作`` () =
    withSession ToolsConfig.defaults (fun s ct ->
        use ws = connect s ct false true
        ask s ws ct
        let events = Ws.collectUntil ws ct finishedStatus
        Assert.DoesNotContain(events, fun e -> (Ws.typeOf e).StartsWith "tool.approval")
        Assert.Equal(2, s.model.Bodies.Length)
        let texts = conversationStrings s ws ct
        Assert.Contains(texts, fun t -> t.Contains "好的，已完成")
        Assert.DoesNotContain(texts, fun t -> t.Contains "拒绝"))

[<Fact>]
let ``[AC-2/7] e2e 每次询问：审批卡出现 → 批准 → 工具结果记账 → 模型继续；审批不进日志`` () =
    withSession askEach (fun s ct ->
        use ws = connect s ct true true
        ask s ws ct
        let req = Ws.wait ws ct "tool.approval-requested"
        Assert.Equal("查看当前时间", (Ws.payload req "summary").GetValue<string>())
        Assert.Equal("always", (Ws.payload req "policy").GetValue<string>())
        let id = Guid.Parse((Ws.payload req "approvalId").GetValue<string>())
        Ws.sendEv ws (ToolApprovalDecision {| approvalId = id; approved = true |}) ct
        let resolved = Ws.wait ws ct "tool.approval-resolved"
        Assert.Equal("approved", (Ws.payload resolved "status").GetValue<string>())
        Ws.wait ws ct "generation.finished" |> ignore
        Assert.Equal(2, s.model.Bodies.Length)
        let texts = conversationStrings s ws ct
        Assert.DoesNotContain(texts, fun t -> t.Contains "用户拒绝")
        Assert.Contains(texts, fun t -> t.Contains "好的，已完成")
        let log = s.host.LogText()
        Assert.DoesNotContain("approval", log)
        Assert.DoesNotContain(id.ToString("D"), log))

[<Fact>]
let ``[AC-4] e2e 拒绝：日志里有一条含「用户拒绝」的完整工具结果，模型看到后继续`` () =
    withSession askEach (fun s ct ->
        use ws = connect s ct true true
        ask s ws ct
        let req = Ws.wait ws ct "tool.approval-requested"
        let id = Guid.Parse((Ws.payload req "approvalId").GetValue<string>())
        Ws.sendEv ws (ToolApprovalDecision {| approvalId = id; approved = false |}) ct
        let resolved = Ws.wait ws ct "tool.approval-resolved"
        Assert.Equal("denied", (Ws.payload resolved "status").GetValue<string>())
        Ws.wait ws ct "generation.finished" |> ignore
        // 模型的第二次请求里带着这条工具结果
        Assert.Equal(2, s.model.Bodies.Length)
        let second = JsonNode.Parse(s.model.Bodies[1])
        let toolMessages = second["messages"].AsArray() |> Seq.filter (fun m -> m["role"].GetValue<string>() = "tool") |> List.ofSeq
        Assert.Single toolMessages |> ignore
        Assert.Contains("用户拒绝", toolMessages.Head["content"].ToJsonString(Text.Json.JsonSerializerOptions(Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)))
        // 日志（快照）里同样是完整消息
        let texts = conversationStrings s ws ct
        Assert.Single(texts |> List.filter (fun t -> t.Contains "用户拒绝")) |> ignore
        Assert.DoesNotContain("approval", s.host.LogText()))

[<Fact>]
let ``[AC-6] e2e 旧客户端遇到需要审批的调用：主机立即拒绝，不会永远挂住`` () =
    withSession askEach (fun s ct ->
        use ws = connect s ct false true // 观察了对话，但没声明审批能力
        ask s ws ct
        let events = Ws.collectUntil ws ct finishedStatus
        Assert.DoesNotContain(events, fun e -> (Ws.typeOf e).StartsWith "tool.approval")
        let texts = conversationStrings s ws ct
        Assert.Single(texts |> List.filter (fun t -> t.Contains "没有支持审批的客户端")) |> ignore)

[<Fact>]
let ``[AC-6] e2e 审批超时：到点自动拒绝并继续`` () =
    withSession { askEach with approvalTimeoutSeconds = 1 } (fun s ct ->
        use ws = connect s ct true true
        ask s ws ct
        let events = Ws.collectUntil ws ct finishedStatus
        let resolved = events |> List.find (fun e -> Ws.typeOf e = "tool.approval-resolved")
        Assert.Equal("expired", (Ws.payload resolved "status").GetValue<string>())
        let texts = conversationStrings s ws ct
        Assert.Single(texts |> List.filter (fun t -> t.Contains "审批超时")) |> ignore)

[<Fact>]
let ``[AC-3] e2e 两个客户端同时观察、同时决定：恰有一个被接受，另一个收到「已处理」`` () =
    withSession askEach (fun s ct ->
        use a = connect s ct true true
        use b = connect s ct true false
        ask s a ct
        let reqA = Ws.wait a ct "tool.approval-requested"
        let reqB = Ws.wait b ct "tool.approval-requested"
        let id = Guid.Parse((Ws.payload reqA "approvalId").GetValue<string>())
        Assert.Equal(id, Guid.Parse((Ws.payload reqB "approvalId").GetValue<string>()))
        let barrier = new Barrier 2
        let t1 = Task.Run(fun () -> barrier.SignalAndWait(); Ws.sendEv a (ToolApprovalDecision {| approvalId = id; approved = true |}) ct)
        let t2 = Task.Run(fun () -> barrier.SignalAndWait(); Ws.sendEv b (ToolApprovalDecision {| approvalId = id; approved = false |}) ct)
        Task.WaitAll(t1, t2)
        let isLate (e: JsonObject) = Ws.typeOf e = "tool.approval-resolved" && (Ws.payload e "alreadyHandled").GetValue<bool>()
        let isBroadcast (e: JsonObject) = Ws.typeOf e = "tool.approval-resolved" && not ((Ws.payload e "alreadyHandled").GetValue<bool>())
        // 两条连接并行读：各自读到生成结束；落败者的「已处理」回复可能晚于生成结束到达，所以再多读一小段
        let reader (ws: Net.WebSockets.ClientWebSocket) =
            Task.Run(fun () ->
                let first = Ws.collectUntil ws ct finishedStatus
                if List.exists isLate first then first
                else
                    use extra = CancellationTokenSource.CreateLinkedTokenSource ct
                    extra.CancelAfter(TimeSpan.FromSeconds 4.0)
                    try first @ Ws.collectUntil ws extra.Token isLate
                    with _ -> first)
        let ta, tb = reader a, reader b
        Task.WaitAll(ta, tb)
        let ea, eb = ta.Result, tb.Result
        let broadcastsA = ea |> List.filter isBroadcast
        let broadcastsB = eb |> List.filter isBroadcast
        Assert.Single broadcastsA |> ignore
        Assert.Single broadcastsB |> ignore
        let late = (ea @ eb) |> List.filter isLate
        Assert.Single late |> ignore
        Assert.Equal((Ws.payload broadcastsA.Head "status").GetValue<string>(), (Ws.payload late.Head "status").GetValue<string>()))

[<Fact>]
let ``[AC-2] e2e 断线重连后审批卡片仍在，可继续决定`` () =
    withSession askEach (fun s ct ->
        let first = connect s ct true true
        ask s first ct
        let req1 = Ws.wait first ct "tool.approval-requested"
        let id = (Ws.payload req1 "approvalId").GetValue<string>()
        first.Dispose() // 断线
        use second = connect s ct true false
        let req2 = Ws.wait second ct "tool.approval-requested"
        Assert.Equal(id, (Ws.payload req2 "approvalId").GetValue<string>())
        Ws.sendEv second (ToolApprovalDecision {| approvalId = Guid.Parse id; approved = true |}) ct
        Ws.wait second ct "generation.finished" |> ignore
        Assert.Equal(2, s.model.Bodies.Length))

[<Fact>]
let ``[AC-2] e2e 取消生成：等待中的审批作废，且不留工具结果`` () =
    withSession askEach (fun s ct ->
        use ws = connect s ct true true
        ask s ws ct
        let started = Ws.wait ws ct "generation.started"
        let req = Ws.wait ws ct "tool.approval-requested"
        let gen = Guid.Parse((Ws.payload req "runId").GetValue<string>())
        Ws.sendEv ws (GenerationCancel {| conversationId = s.conv; generationId = gen |}) ct
        let resolved = Ws.wait ws ct "tool.approval-resolved"
        Assert.Equal("cancelled", (Ws.payload resolved "status").GetValue<string>())
        let fin = Ws.wait ws ct "generation.finished"
        Assert.Equal("cancelled", (Ws.payload fin "status").GetValue<string>())
        let texts = conversationStrings s ws ct
        Assert.DoesNotContain(texts, fun t -> t.Contains "用户拒绝")
        ignore started)

[<Fact>]
let ``[AC-5] e2e 重启：挂起的审批消失，对话不卡死（不再下发、不再等待）`` () =
    use model = new FakeModel()
    let port, token = Wanxiang.Tests.ServerE2eTests.pickPort (), ClientToken.ClientToken.generate ()
    use host = new Host(port, token, model.Url, askEach, tempDir ())
    host.Start()
    let s = { host = host; model = model; port = port; token = token; conv = Guid.NewGuid() }
    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 40.0)
    let ct = timeout.Token
    let ws = connect s ct true true
    ask s ws ct
    let req = Ws.wait ws ct "tool.approval-requested"
    let id = Guid.Parse((Ws.payload req "approvalId").GetValue<string>())
    ws.Dispose()
    host.Stop()
    host.Start()
    use again = connect s ct true false
    // 重启后对话是空闲的，且没有任何待审批请求重发
    Ws.sendEv again (ObserveConversation {| conversationId = s.conv |}) ct
    let snap = Ws.wait again ct "conversation.snapshot"
    Assert.Equal("idle", (Ws.payload snap "runtimeState").GetValue<string>())
    // 对旧审批编号做决定：得到「已失效」，不会误触发任何工具
    Ws.sendEv again (ToolApprovalDecision {| approvalId = id; approved = true |}) ct
    let late = Ws.wait again ct "tool.approval-resolved"
    Assert.True((Ws.payload late "alreadyHandled").GetValue<bool>())
    Assert.Equal("expired", (Ws.payload late "status").GetValue<string>())
    Assert.Equal(1, model.Bodies.Length)

[<Fact>]
let ``[AC-6] e2e 未认证连接发审批决定被拒绝（不能抢决定）`` () =
    withSession askEach (fun s ct ->
        let ws = new Net.WebSockets.ClientWebSocket()
        ws.ConnectAsync(Uri(sprintf "ws://127.0.0.1:%d/ws" s.port), ct).GetAwaiter().GetResult()
        Ws.sendEv ws (ToolApprovalDecision {| approvalId = Guid.NewGuid(); approved = true |}) ct
        let buffer = Array.zeroCreate<byte> 4096
        let mutable closed = false
        let mutable guard = 0
        while not closed && guard < 20 do
            let r = ws.ReceiveAsync(ArraySegment<byte>(buffer), ct).GetAwaiter().GetResult()
            if r.MessageType = Net.WebSockets.WebSocketMessageType.Close then closed <- true
            guard <- guard + 1
        Assert.True closed)
