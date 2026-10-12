namespace Wanxiang.Tests

open System
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Automation.Provider
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Config
open Wanxiang.Protocol
open Wanxiang.Server
open Wanxiang.UI

/// W1 UI 验收：聊天主界面上的「连接一个模型」卡片、动作数、类型化错误呈现、
/// 本机 Ollama 一键使用、设置页的预设分组 / 传输切换 / 全局默认。
[<Trait("Category", "UI")>]
module OnboardingUiTests =

    let private strOf (o: JsonObject) (key: string) = o.[key].GetValue<string>()

    let private flags = BindingFlags.Instance ||| BindingFlags.NonPublic

    let private visual (root: Control) : Control list =
        root.GetVisualDescendants() |> Seq.choose (function :? Control as c -> Some c | _ -> None) |> List.ofSeq

    let private named (root: Control) (name: string) : Control list =
        visual root |> List.filter (fun c -> AutomationProperties.GetName c = name)

    let private byName (root: Control) (name: string) : Control =
        match named root name with
        | c :: _ -> c
        | [] -> failwithf "找不到无障碍名为「%s」的控件" name

    let private invoke (control: Control) =
        ControlAutomationPeer.CreatePeerForElement control
        |> Assert.IsAssignableFrom<IInvokeProvider>
        |> fun p -> p.Invoke()
        Dispatcher.UIThread.RunJobs()

    let private textOf (root: Control) (name: string) =
        match byName root name with
        | :? TextBlock as t -> t.Text
        | other -> failwithf "%s 不是 TextBlock：%s" name (other.GetType().Name)

    let private provider (id: string) (models: string list) (defaultModel: string) : ProviderConfig =
        { id = id
          kind = "openai"
          label = id
          baseUrl = "https://example.invalid/v1"
          apiKey = Some "sk-test"
          models = models
          defaultModel = defaultModel
          timeoutSeconds = 30
          maxRetries = 0
          enabled = true
          promptCaching = false
          headers = Map.empty
          extraJson = None }

    // ---- 用一个内存「主机」替代 WebSocket：真实 ConfigMutation + 真实探测，端点指向本地 mock ----

    /// 本地 mock OpenAI 兼容端点（等价于 tools/mock_openai.py 的 GET /v1/models）。
    type private MockModels(status: int, body: string) =
        let listener = new System.Net.HttpListener()
        let port =
            let l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
            l.Start()
            let p = (l.LocalEndpoint :?> System.Net.IPEndPoint).Port
            l.Stop()
            p
        let prefix = sprintf "http://127.0.0.1:%d/" port
        do
            listener.Prefixes.Add prefix
            listener.Start()
            Task.Run(fun () ->
                try
                    while listener.IsListening do
                        let ctx = listener.GetContext()
                        let bytes = Encoding.UTF8.GetBytes body
                        ctx.Response.StatusCode <- status
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                        ctx.Response.OutputStream.Close()
                with _ -> ())
            |> ignore
        member _.BaseUrl = prefix.TrimEnd '/'
        interface IDisposable with
            member _.Dispose() = (try listener.Stop() with _ -> ())

    /// 内存主机：持有 AppConfig，收到卡片动作后经 ConfigMutation 落配置，探测走真实 HTTP。
    type private FakeHost(mockUrl: string) =
        let mutable cfg = AppConfig.defaults (Guid.Parse "33333333-3333-3333-3333-333333333333")
        member val Payloads = List<JsonObject>() with get
        member val Discarded = List<string>() with get
        member val Toasts = List<string>() with get
        member val Probes = List<string>() with get
        member _.Config = cfg
        member _.Catalog() =
            Catalog.parse (ProviderCatalog.providers cfg) (JsonArray()) (ProviderCatalog.generation cfg)
        member this.Actions(card: OnboardingCard option ref, discover: unit -> Task<string list option>) : OnboardingActions =
            { upsertProvider =
                fun payload completed ->
                    this.Payloads.Add(payload.DeepClone() :?> JsonObject)
                    // 首次运行卡片的端点替换为 mock：模拟「在 mock 服务上」
                    let wire = payload.DeepClone() :?> JsonObject
                    if wire["baseUrl"].GetValue<string>().Contains "api." then wire["baseUrl"] <- mockUrl
                    match ConfigMutation.upsertProvider cfg wire with
                    | Ok next ->
                        cfg <- next
                        completed true
                    | Error _ -> completed false
              discardProvider =
                fun id ->
                    this.Discarded.Add id
                    match ConfigMutation.deleteProvider cfg id with
                    | Ok next -> cfg <- next
                    | Error _ -> ()
              probeProvider =
                fun id ->
                    this.Probes.Add id
                    let p = cfg.providers[id]
                    // 在线程池上跑并同步等待：UI 线程有同步上下文，直接阻塞会和 task 续体互等。
                    let result =
                        Task.Run<Result<string list, string>>(fun () -> ProviderCatalog.probeModels p CancellationToken.None).Result
                    match result with
                    | Ok models -> card.Value.Value.ApplyProbe(id, true, models, None) |> ignore
                    | Error e -> card.Value.Value.ApplyProbe(id, false, [], Some e) |> ignore
              takenIds = fun () -> cfg.providers |> Map.toList |> List.map fst
              discoverLocal = discover
              openAdvanced = ignore
              toast = fun m _ -> this.Toasts.Add m }

    let private noDiscovery () : Task<string list option> = Task.FromResult None

    let private mount (card: OnboardingCard) =
        let host = Grid()
        host.Children.Add card.Root
        let window = Window(Width = 700.0, Height = 700.0, Content = host)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        window, host

    // ---- 验收 2：mock 服务上的完整流程，动作 ≤ 4 ----

    [<Fact>]
    let ``mock 服务上：选预设、填密钥、点连接后模型列表自动填充且默认模型被选中`` () =
        Headless.run (fun () ->
            use mock = new MockModels(200, """{"data":[{"id":"zz-embed"},{"id":"gpt-4o"},{"id":"gpt-5"}]}""")
            let host = FakeHost(mock.BaseUrl)
            let cardRef = ref None
            let card = OnboardingCard(OverlayHost(Grid()), host.Actions(cardRef, noDiscovery))
            cardRef.Value <- Some card
            let window, root = mount card
            try
                let mutable userActions = 0
                invoke (byName root "OpenAI")
                userActions <- userActions + 1
                (byName root "API Key" :?> TextBox).Text <- "sk-mock"
                userActions <- userActions + 1
                invoke (byName root "连接")
                userActions <- userActions + 1
                Dispatcher.UIThread.RunJobs()
                // 探测与写回都已完成：模型清单来自 mock，而不是预设自带的清单
                let saved = host.Config.providers["openai"]
                Assert.Equal<string list>([ "gpt-4o"; "gpt-5"; "zz-embed" ], saved.models)
                Assert.Equal("gpt-5", saved.defaultModel)
                Assert.Equal(Some "sk-mock", saved.apiKey)
                Assert.True saved.enabled
                Assert.Empty host.Discarded
                // 先保存预设、再写回探测结果：两次写入走的都是 upsert
                Assert.Equal(2, host.Payloads.Count)
                Assert.Equal(OnboardingStep.Idle, card.Step)
                // 目录就绪后外壳收尾：取走 (id, 模型)
                let taken = card.TakeConnected(host.Catalog())
                Assert.Equal(Some("openai", "gpt-5"), taken)
                Assert.Equal(None, card.TakeConnected(host.Catalog()))
                // 第 4 个动作是回车发送；连接成功后用户再不需要任何别的动作
                userActions <- userActions + 1
                Assert.True(userActions <= 4, sprintf "动作数 %d 超过 4" userActions)
                Assert.DoesNotContain(host.Toasts, fun m -> m.Contains "失败")
            finally
                window.Close()
        )

    [<Fact>]
    let ``密钥为空时不发起任何请求并就地说明`` () =
        Headless.run (fun () ->
            use mock = new MockModels(200, """{"data":[]}""")
            let host = FakeHost(mock.BaseUrl)
            let cardRef = ref None
            let card = OnboardingCard(OverlayHost(Grid()), host.Actions(cardRef, noDiscovery))
            cardRef.Value <- Some card
            let window, root = mount card
            try
                invoke (byName root "连接")
                Assert.Empty host.Payloads
                Assert.Contains("密钥", textOf root "连接状态")
                Assert.Equal(OnboardingStep.Idle, card.Step)
            finally
                window.Close()
        )

    // ---- 验收 3：失败原因逐类呈现 ----

    let private failFlow (status: int) (body: string) (presetLabel: string) =
        use mock = new MockModels(status, body)
        let host = FakeHost(mock.BaseUrl)
        let cardRef = ref None
        let card = OnboardingCard(OverlayHost(Grid()), host.Actions(cardRef, noDiscovery))
        cardRef.Value <- Some card
        let window, root = mount card
        try
            invoke (byName root presetLabel)
            (byName root "API Key" :?> TextBox).Text <- "sk-bad"
            invoke (byName root "连接")
            Dispatcher.UIThread.RunJobs()
            let status = textOf root "连接状态"
            let switchVisible = (named root "改用兼容端点") |> List.exists (fun c -> c.IsVisible)
            status, switchVisible, host.Discarded |> List.ofSeq, host.Config.providers.Count, card.Step
        finally
            window.Close()

    [<Fact>]
    let ``密钥无效、路径错、不通各自给出不同的人话原因，并撤销半成品`` () =
        Headless.run (fun () ->
            let invalidText, _, discarded1, count1, step1 = failFlow 401 "nope" "OpenAI"
            let pathText, openAiSwitch, _, _, _ = failFlow 404 "nope" "OpenAI"
            let nativePath, nativeSwitch, _, _, _ = failFlow 404 "nope" "Anthropic Claude"
            // 完全连不上：先拿到一个没人监听的端口
            let dead =
                let l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
                l.Start()
                let p = (l.LocalEndpoint :?> System.Net.IPEndPoint).Port
                l.Stop()
                sprintf "http://127.0.0.1:%d" p
            let host = FakeHost(dead)
            let cardRef = ref None
            let card = OnboardingCard(OverlayHost(Grid()), host.Actions(cardRef, noDiscovery))
            cardRef.Value <- Some card
            let window, root = mount card
            let unreachableText =
                try
                    (byName root "API Key" :?> TextBox).Text <- "sk-x"
                    invoke (byName root "连接")
                    Dispatcher.UIThread.RunJobs()
                    textOf root "连接状态"
                finally
                    window.Close()
            let texts = [ invalidText; pathText; unreachableText ]
            Assert.Equal(3, texts |> List.distinct |> List.length)
            for t in texts do
                Assert.False(t.StartsWith "[", "错误码前缀不能漏到界面上：" + t)
                Assert.True(t.Length > 25 && t.Contains "请", t)
            Assert.Contains("密钥", invalidText)
            Assert.Contains("/v1", pathText)
            // 探测失败：刚创建的服务被撤销，不留一个连不通的半成品；卡片回到可重试状态
            Assert.Equal<string list>([ "openai" ], discarded1)
            Assert.Equal(0, count1)
            Assert.Equal(OnboardingStep.Idle, step1)
            // 下一步：只有这家有另一条传输、且错误是路径类时才给「改用兼容端点」
            Assert.False openAiSwitch
            Assert.True nativeSwitch
            Assert.Contains("兼容端点", nativePath)
        )

    [<Fact>]
    let ``路径错时改用兼容端点后重试，载荷换成兼容传输`` () =
        Headless.run (fun () ->
            use mock = new MockModels(404, "nope")
            let host = FakeHost(mock.BaseUrl)
            let cardRef = ref None
            let card = OnboardingCard(OverlayHost(Grid()), host.Actions(cardRef, noDiscovery))
            cardRef.Value <- Some card
            let window, root = mount card
            try
                invoke (byName root "Anthropic Claude")
                (byName root "API Key" :?> TextBox).Text <- "sk-ant"
                invoke (byName root "连接")
                Dispatcher.UIThread.RunJobs()
                Assert.Equal("anthropic", (strOf host.Payloads.[0] "kind"))
                invoke (byName root "改用兼容端点")
                invoke (byName root "连接")
                Dispatcher.UIThread.RunJobs()
                let retry = host.Payloads.[1]
                Assert.Equal("openai", retry["kind"].GetValue<string>())
                Assert.Equal("https://api.anthropic.com/v1", retry["baseUrl"].GetValue<string>())
            finally
                window.Close()
        )

    // ---- 本机 Ollama ----

    [<Fact>]
    let ``发现本机 Ollama 时给出一键使用，点击后用发现的模型保存且不要密钥`` () =
        Headless.run (fun () ->
            let host = FakeHost("http://unused.invalid")
            let cardRef = ref None
            let found = [ "qwen3:8b"; "llama3.2" ]
            let card =
                OnboardingCard(OverlayHost(Grid()), host.Actions(cardRef, fun () -> Task.FromResult(Some found)))
            cardRef.Value <- Some card
            let window, root = mount card
            try
                Assert.Empty(named root "一键使用本机 Ollama")
                card.StartDiscovery()
                let mutable tries = 0
                while (named root "一键使用本机 Ollama").IsEmpty && tries < 100 do
                    Thread.Sleep 20
                    Dispatcher.UIThread.RunJobs()
                    tries <- tries + 1
                Assert.Equal(Some found, card.Discovered)
                invoke (byName root "一键使用本机 Ollama")
                Assert.Equal(1, host.Payloads.Count)
                let payload = host.Payloads.[0]
                Assert.Equal("openai", payload["kind"].GetValue<string>())
                Assert.Equal("http://127.0.0.1:11434/v1", payload["baseUrl"].GetValue<string>())
                Assert.False(payload.ContainsKey "apiKey")
                let saved = host.Config.providers["ollama"]
                Assert.Equal<string list>(found, saved.models)
                Assert.Equal("qwen3:8b", saved.defaultModel)
                Assert.Equal(Some("ollama", "qwen3:8b"), card.TakeConnected(host.Catalog()))
            finally
                window.Close()
        )

    [<Fact>]
    let ``没有 Ollama 时不显示一键使用`` () =
        Headless.run (fun () ->
            let host = FakeHost("http://unused.invalid")
            let cardRef = ref None
            let card = OnboardingCard(OverlayHost(Grid()), host.Actions(cardRef, noDiscovery))
            cardRef.Value <- Some card
            let window, root = mount card
            try
                card.ShowDiscovered None
                Dispatcher.UIThread.RunJobs()
                Assert.Empty(named root "一键使用本机 Ollama" |> List.filter (fun c -> c.IsEffectivelyVisible))
                // 选 Ollama 预设时不要求密钥
                invoke (byName root "Ollama（本机）")
                Assert.False((visual root |> List.find (fun c -> AutomationProperties.GetName c = "API Key" && c :? TextBox)).IsEffectivelyVisible)
            finally
                window.Close()
        )

    // ---- 验收 1：空配置启动，主界面出现卡片，设置页未被打开 ----

    let private handle (view: MainView) (ev: WireEvent) =
        typeof<MainView>.GetMethod("HandleEvent", flags).Invoke(view, [| box ev |]) |> ignore
        Dispatcher.UIThread.RunJobs()

    let private field<'T> (view: MainView) (name: string) : 'T =
        typeof<MainView>.GetField(name, flags).GetValue view :?> 'T

    let private createShell () =
        MotionPolicy.setReduced true
        let shell = MainView()
        shell.Build()
        let window = Window(Width = 1000.0, Height = 800.0, Content = shell)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        shell, window

    let private emptyCatalog () =
        CatalogSnapshot {| providers = JsonArray(); tools = JsonArray(); generation = JsonObject() |}

    let private catalogOf (cfg: AppConfig) =
        CatalogSnapshot {| providers = ProviderCatalog.providers cfg; tools = JsonArray(); generation = ProviderCatalog.generation cfg |}

    let private pendingRequestIds (view: MainView) : Guid list =
        let tracker = field<obj> view "configFeedback"
        let pending = tracker.GetType().GetField("pending", flags).GetValue tracker
        let keys = pending.GetType().GetProperty("Keys").GetValue pending :?> IEnumerable<Guid>
        List.ofSeq keys

    [<Fact>]
    let ``空配置启动：聊天主界面出现连接卡片，设置页没有被打开`` () =
        Headless.run (fun () ->
            let shell, window = createShell ()
            try
                handle shell (AuthAccepted {| instanceId = "i-empty" |})
                handle shell (emptyCatalog ())
                let card = named shell "连接一个模型"
                Assert.NotEmpty card
                Assert.True(card.Head.IsEffectivelyVisible, "卡片应当可见")
                let settingsHost = field<Grid> shell "settingsHost"
                Assert.False settingsHost.IsVisible
                // 常用 4 家都在卡片上；「更多」收着其余的
                for label in [ "OpenAI"; "Anthropic Claude"; "Google Gemini"; "Ollama（本机）" ] do
                    Assert.True((byName shell label).IsEffectivelyVisible, label)
                Assert.NotEmpty(named shell "更多模型服务")
                // 输入区此刻不可用，原因就地说明
                Assert.NotEmpty(named shell "高级设置")
            finally
                window.Close()
        )

    [<Fact>]
    let ``外壳里走完整条旅程：连接成功后输入框预填并聚焦，用户动作不超过 4`` () =
        Headless.run (fun () ->
            let shell, window = createShell ()
            try
                handle shell (AuthAccepted {| instanceId = "i-journey" |})
                handle shell (emptyCatalog ())
                let mutable userActions = 0
                invoke (byName shell "OpenAI")
                userActions <- userActions + 1
                (byName shell "API Key" :?> TextBox).Text <- "sk-journey"
                userActions <- userActions + 1
                invoke (byName shell "连接")
                userActions <- userActions + 1
                // 主机：第一次保存成功
                let first = pendingRequestIds shell
                Assert.Single first |> ignore
                handle shell (ConfigApplied {| requestId = first.Head; ok = true; errors = [] |})
                // 主机：探测返回 mock 的模型清单
                let models = JsonArray([| JsonNode.op_Implicit "gpt-4o"; JsonNode.op_Implicit "gpt-5" |])
                handle shell (ProviderProbeResult {| requestId = Guid.NewGuid(); providerId = "openai"; ok = true; models = models; error = None |})
                // 主机：清单与默认模型写回成功，随后目录快照到达
                let second = pendingRequestIds shell
                Assert.Single second |> ignore
                handle shell (ConfigApplied {| requestId = second.Head; ok = true; errors = [] |})
                let preset = ProviderPresets.tryFind "openai" |> Option.get
                let cfg0 = AppConfig.defaults (Guid.NewGuid())
                let cfg =
                    match
                        ConfigMutation.upsertProvider
                            cfg0
                            (ProviderPresets.payload "openai" preset.label preset.kind preset.baseUrl (Some "sk-journey") [ "gpt-4o"; "gpt-5" ] "gpt-5" true 120 2)
                    with
                    | Ok c -> c
                    | Error e -> failwithf "%A" e
                handle shell (catalogOf cfg)
                // 卡片已收起；输入框预填第一句话并获得焦点
                Assert.Empty(named shell "连接一个模型" |> List.filter (fun c -> c.IsEffectivelyVisible))
                let composer = field<Composer> shell "composer"
                Assert.Equal(Onboarding.FirstPrompt, composer.Text)
                match window.FocusManager.GetFocusedElement() with
                | :? TextBox as box -> Assert.Equal(Onboarding.FirstPrompt, box.Text)
                | other -> failwithf "焦点应在输入框，实际 %A" other
                // 设置页全程没被打开
                Assert.False((field<Grid> shell "settingsHost").IsVisible)
                // 第 4 个动作：回车发送
                userActions <- userActions + 1
                Assert.True(userActions <= 4, sprintf "动作数 %d 超过 4" userActions)
                // 新对话起手的模型 = 刚选定的默认模型
                let command =
                    typeof<MainView>.GetMethod("MakeConversationCommand", flags).Invoke(shell, [| box (Guid.NewGuid()) |])
                match command with
                | :? Wanxiang.Core.ClientCommand as cmd ->
                    match cmd with
                    | Wanxiang.Core.CreateConversation d ->
                        Assert.Equal("openai", d.config.provider)
                        Assert.Equal("gpt-5", d.config.model)
                    | other -> failwithf "应是 CreateConversation：%A" other
                | other -> failwithf "意外返回 %A" other
            finally
                window.Close()
        )

    [<Fact>]
    let ``已有可用模型时不出现卡片，输入框可用`` () =
        Headless.run (fun () ->
            let shell, window = createShell ()
            try
                let preset = ProviderPresets.tryFind "openai" |> Option.get
                let cfg =
                    match ConfigMutation.upsertProvider (AppConfig.defaults (Guid.NewGuid())) (ProviderPresets.presetPayload preset "openai" (Some "k") []) with
                    | Ok c -> c
                    | Error e -> failwithf "%A" e
                handle shell (AuthAccepted {| instanceId = "i-ready" |})
                handle shell (catalogOf cfg)
                Assert.Empty(named shell "连接一个模型" |> List.filter (fun c -> c.IsEffectivelyVisible))
            finally
                window.Close()
        )

    // ---- 验收 5（UI 侧）：新对话模型 = 全局默认 ----

    [<Fact>]
    let ``外壳里新对话的模型取全局默认，删掉键后回落`` () =
        Headless.run (fun () ->
            let shell, window = createShell ()
            try
                let cfg0 = AppConfig.defaults (Guid.NewGuid())
                let cfg =
                    { cfg0 with
                        providers =
                            Map.ofList
                                [ "alpha", provider "alpha" [ "a1"; "a2" ] "a1"
                                  "beta", provider "beta" [ "b1"; "b2" ] "b2" ] }
                let withGlobal = { cfg with generation = { cfg.generation with defaultProvider = Some "beta"; defaultModel = Some "b1" } }
                let modelOfNewConversation () =
                    match typeof<MainView>.GetMethod("MakeConversationCommand", flags).Invoke(shell, [| box (Guid.NewGuid()) |]) with
                    | :? Wanxiang.Core.ClientCommand as cmd ->
                        match cmd with
                        | Wanxiang.Core.CreateConversation d -> d.config.provider, d.config.model
                        | other -> failwithf "%A" other
                    | other -> failwithf "%A" other
                handle shell (AuthAccepted {| instanceId = "i-default" |})
                handle shell (catalogOf withGlobal)
                Assert.Equal(("beta", "b1"), modelOfNewConversation ())
                handle shell (catalogOf cfg)
                Assert.Equal(("alpha", "a1"), modelOfNewConversation ())
            finally
                window.Close()
        )

    // ---- 设置页：预设分组、传输切换、全局默认、两条入口同一 TOML ----

    let private settingsActions (payloads: List<JsonObject>) (generations: List<JsonObject>) : SettingsActions =
        { upsertProvider = fun payload completed -> (payloads.Add(payload.DeepClone() :?> JsonObject); completed true)
          deleteProvider = ignore
          probeProvider = ignore
          upsertMcp = fun _ completed -> completed true
          deleteMcp = ignore
          updateGeneration = fun payload completed -> (generations.Add(payload.DeepClone() :?> JsonObject); completed true)
          savePrefs = ignore
          toast = fun _ _ -> () }

    let private mountSettings (actions: SettingsActions) =
        let root = Grid()
        let overlay = OverlayHost(root)
        overlay.WireDismiss()
        let providers = SettingsProviders(overlay, actions)
        root.Children.Insert(0, providers.Build())
        let window = Window(Width = 760.0, Height = 800.0, Content = root)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        providers, root, overlay, window

    [<Fact>]
    let ``设置页与首次运行卡片保存路径相同：同一个 ConfigMutation 写出相同 TOML`` () =
        Headless.run (fun () ->
            let payloads = List<JsonObject>()
            let _, root, _, window = mountSettings (settingsActions payloads (List()))
            try
                invoke (byName root "添加模型服务")
                (byName root "API Key" :?> TextBox).Text <- "sk-same"
                invoke (byName root "添加")
                Assert.Single payloads |> ignore
                let empty = AppConfig.defaults (Guid.Parse "44444444-4444-4444-4444-444444444444")
                let write (payload: JsonObject) =
                    match ConfigMutation.upsertProvider empty payload with
                    | Ok cfg -> TomlCodec.serialize cfg
                    | Error e -> failwithf "%A" e
                let preset = ProviderPresets.tryFind "openai" |> Option.get
                let viaCard = ProviderPresets.presetPayload preset "openai" (Some "sk-same") []
                Assert.Equal(write payloads.[0], write viaCard)
            finally
                window.Close()
        )

    [<Fact>]
    let ``预设下拉：常用在前、其余折叠到更多，Claude 与 Gemini 各只有一项`` () =
        Headless.run (fun () ->
            let _, root, overlay, window = mountSettings (settingsActions (List()) (List()))
            try
                invoke (byName root "添加模型服务")
                let presetButton =
                    visual root |> List.find (fun c -> (ToolTip.GetTip c :?> string) = "OpenAI")
                presetButton.RaiseEvent(
                    KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, KeyModifiers = KeyModifiers.None))
                Dispatcher.UIThread.RunJobs()
                Assert.True overlay.IsPopupOpen
                let entries =
                    visual root
                    |> List.filter (fun c -> AutomationProperties.GetControlTypeOverride(c).HasValue)
                    |> List.map AutomationProperties.GetName
                    |> List.filter (isNull >> not)
                for label in ProviderPresets.all |> List.map _.label do
                    Assert.Contains(label, entries)
                Assert.Equal(1, entries |> List.filter (fun l -> l.StartsWith "Anthropic Claude") |> List.length)
                Assert.Equal(1, entries |> List.filter (fun l -> l.StartsWith "Google Gemini") |> List.length)
                Assert.DoesNotContain(entries, fun l -> l.Contains "兼容端点（")
            finally
                window.Close()
        )

    [<Fact>]
    let ``高级区才可切换传输：Claude 默认原生，切到兼容端点后改写地址与传输`` () =
        Headless.run (fun () ->
            let payloads = List<JsonObject>()
            let _, root, _, window = mountSettings (settingsActions payloads (List()))
            try
                invoke (byName root "添加模型服务")
                // OpenAI 没有另一条传输：切换入口不出现
                Assert.DoesNotContain(named root "改用兼容端点", fun c -> c.IsVisible)
                let presetButton = visual root |> List.find (fun c -> (ToolTip.GetTip c :?> string) = "OpenAI")
                presetButton.RaiseEvent(
                    KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, KeyModifiers = KeyModifiers.None))
                Dispatcher.UIThread.RunJobs()
                let claudeEntry =
                    visual root
                    |> List.find (fun c -> AutomationProperties.GetName c = "Anthropic Claude" && AutomationProperties.GetControlTypeOverride(c).HasValue)
                invoke claudeEntry
                let toggle = byName root "改用兼容端点"
                Assert.True toggle.IsVisible
                Assert.Equal("改用兼容端点", ((toggle :?> Border).Child :?> TextBlock).Text)
                invoke toggle
                let urlBox = byName root "端点地址" :?> TextBox
                Assert.Equal("https://api.anthropic.com/v1", urlBox.Text)
                (byName root "API Key" :?> TextBox).Text <- "sk-ant"
                invoke (byName root "添加")
                Assert.Equal("openai", (strOf payloads.[0] "kind"))
                Assert.Equal("https://api.anthropic.com/v1", (strOf payloads.[0] "baseUrl"))
            finally
                window.Close()
        )

    [<Fact>]
    let ``设置页：可把模型服务设为默认，已是默认的显示标记并可取消`` () =
        Headless.run (fun () ->
            let generations = List<JsonObject>()
            let providers, root, overlay, window = mountSettings (settingsActions (List()) generations)
            try
                let cfg0 = AppConfig.defaults (Guid.NewGuid())
                let cfg =
                    { cfg0 with
                        providers =
                            Map.ofList
                                [ "alpha", provider "alpha" [ "a1" ] "a1"
                                  "beta", provider "beta" [ "b1"; "b2" ] "b2" ] }
                let catalogOf (c: AppConfig) =
                    Catalog.parse (ProviderCatalog.providers c) (JsonArray()) (ProviderCatalog.generation c)
                providers.SetCatalog(catalogOf cfg)
                Dispatcher.UIThread.RunJobs()
                // 未设置全局默认：没有「默认」标记
                Assert.DoesNotContain(visual root, fun c -> c :? TextBlock && (c :?> TextBlock).Text = "默认")
                // 设置 beta 为默认：第二行的「更多」菜单里选「设为默认模型服务」
                let moreButtons = named root "更多"
                Assert.Equal(2, moreButtons.Length)
                invoke moreButtons.[1]
                invoke (byName root "设为默认模型服务")
                Assert.Single generations |> ignore
                Assert.Equal("beta", (strOf generations.[0] "defaultProvider"))
                Assert.Equal("b2", (strOf generations.[0] "defaultModel"))
                // 该载荷经同一个 ConfigMutation 即得到全局默认
                match ConfigMutation.updateGeneration cfg generations.[0] with
                | Ok next -> Assert.Equal(Some "beta", next.generation.defaultProvider)
                | Error e -> failwithf "%A" e
                // 目录里带上全局默认后：出现「默认」标记，菜单变成「取消默认」
                let withDefault =
                    match ConfigMutation.updateGeneration cfg generations.[0] with
                    | Ok next -> next
                    | Error e -> failwithf "%A" e
                providers.SetCatalog(catalogOf withDefault)
                Dispatcher.UIThread.RunJobs()
                Assert.Equal(1, visual root |> List.filter (fun c -> c :? TextBlock && (c :?> TextBlock).Text = "默认") |> List.length)
                invoke ((named root "更多").[1])
                invoke (byName root "取消默认")
                Assert.Equal("", (strOf generations.[1] "defaultProvider"))
                match ConfigMutation.updateGeneration withDefault generations.[1] with
                | Ok cleared -> Assert.Equal(None, cleared.generation.defaultProvider)
                | Error e -> failwithf "%A" e
            finally
                window.Close()
        )
