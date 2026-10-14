namespace Wanxiang.Tests

open System
open System.Net
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Wanxiang.Config
open Wanxiang.Server
open Wanxiang.UI

/// W1（首次运行与模型服务）的非 UI 验收：全局默认模型、configVersion 3 迁移、
/// 类型化探测错误、预设重排、本机 Ollama 发现、两条保存入口同一 TOML。
module OnboardingConfigTests =

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
          extraJson = None; visionOverrides = Map.empty }

    let private twoProviders () =
        let cfg = AppConfig.defaults (Guid.Parse "11111111-1111-1111-1111-111111111111")
        { cfg with
            providers =
                Map.ofList
                    [ "alpha", provider "alpha" [ "a1"; "a2" ] "a1"
                      "beta", provider "beta" [ "b1"; "b2" ] "b2" ] }

    let private withDefault (p: string option) (m: string option) (cfg: AppConfig) =
        { cfg with generation = { cfg.generation with defaultProvider = p; defaultModel = m } }

    let private parseOk (text: string) =
        match TomlCodec.tryParse text with
        | Ok cfg -> cfg
        | Error errs -> failwithf "应当读入成功：%s" (String.Join("；", errs))

    // ---- 验收 4：configVersion 2 → 3 ----

    [<Fact>]
    let ``configVersion 2 旧 TOML 读入后行为不变且写回仍是 2`` () =
        let oldText = TomlCodec.serialize (twoProviders ())
        Assert.Contains("configVersion = 2", oldText)
        Assert.DoesNotContain("defaultProvider", oldText)
        let cfg = parseOk oldText
        Assert.Equal(None, cfg.generation.defaultProvider)
        Assert.Equal(None, cfg.generation.defaultModel)
        // 行为不变：默认仍是首个可用模型服务（按 id 排序）及其 defaultModel
        let selection = AppConfig.defaultSelection cfg
        Assert.Equal(Some("alpha", "a1"), selection |> Option.map (fun (p, m) -> p.id, m))
        // 没有设置全局默认：写回保持 2，旧版本主机仍读得懂
        Assert.Equal(oldText, TomlCodec.serialize cfg)

    [<Fact>]
    let ``设置全局默认后写回升为 3，清除后回到 2`` () =
        let cfg = twoProviders () |> withDefault (Some "beta") (Some "b1")
        let text = TomlCodec.serialize cfg
        Assert.Contains("configVersion = 3", text)
        Assert.Contains("defaultProvider = \"beta\"", text)
        Assert.Contains("defaultModel = \"b1\"", text)
        let cleared = parseOk text |> withDefault None None
        Assert.Contains("configVersion = 2", TomlCodec.serialize cleared)

    [<Fact>]
    let ``版本 3 文件重复读写幂等`` () =
        let text1 = TomlCodec.serialize (twoProviders () |> withDefault (Some "beta") (Some "b1"))
        let cfg1 = parseOk text1
        let text2 = TomlCodec.serialize cfg1
        let text3 = TomlCodec.serialize (parseOk text2)
        Assert.Equal(text1, text2)
        Assert.Equal(text2, text3)
        Assert.Equal(Some "beta", cfg1.generation.defaultProvider)
        Assert.Equal(Some "b1", cfg1.generation.defaultModel)

    [<Fact>]
    let ``未知键仍整体拒绝（版本 2 与 3）`` () =
        let v2 = TomlCodec.serialize (twoProviders ())
        let v3 = TomlCodec.serialize (twoProviders () |> withDefault (Some "beta") None)
        for text in [ v2; v3 ] do
            let badGeneration = text.Replace("[generation]\n", "[generation]\nbogusKey = 1\n")
            Assert.True(TomlCodec.tryParse badGeneration |> Result.isError, "generation 下的未知键必须整体拒绝")
            Assert.True(TomlCodec.tryParse (text + "\n[bogusTable]\nx = 1\n") |> Result.isError, "未知顶层表必须整体拒绝")
        Assert.True(TomlCodec.tryParse "configVersion = 4\n" |> Result.isError)

    // ---- 验收 5：新对话模型 = 全局默认；删键后回落 ----

    [<Fact>]
    let ``新对话的模型取全局默认，删掉键后回落为模型服务 defaultModel`` () =
        let cfg = twoProviders ()
        let withGlobal = cfg |> withDefault (Some "beta") (Some "b1")
        let sel = AppConfig.defaultSelection withGlobal |> Option.get
        Assert.Equal(("beta", "b1"), ((fst sel).id, snd sel))
        // 主机注入对话配置走 defaultProvider + resolveModel：同一结果
        let injected = AppConfig.defaultProvider withGlobal |> Option.get
        Assert.Equal("beta", injected.id)
        Assert.Equal("b1", ProviderConfig.resolveModel "" injected)
        // 删掉全局默认键 → 首个可用模型服务及其 defaultModel（与旧行为一致）
        let removed = AppConfig.defaultProvider (withGlobal |> withDefault None None) |> Option.get
        Assert.Equal("alpha", removed.id)
        Assert.Equal("a1", ProviderConfig.resolveModel "" removed)

    [<Fact>]
    let ``全局默认悬空或不可用时回落，不报错也不卡死`` () =
        let cfg = twoProviders ()
        // 指向不存在的模型服务
        let dangling = cfg |> withDefault (Some "gone") (Some "x")
        Assert.Equal("alpha", (AppConfig.defaultSelection dangling |> Option.get |> fst).id)
        // 模型不在该模型服务列表里：回落该模型服务自己的 defaultModel
        let wrongModel = cfg |> withDefault (Some "beta") (Some "not-there")
        Assert.Equal(("beta", "b2"), (let p, m = AppConfig.defaultSelection wrongModel |> Option.get in p.id, m))
        // 模型服务被停用：整体回落
        let disabled =
            { cfg with providers = cfg.providers.Add("beta", { cfg.providers["beta"] with enabled = false }) }
            |> withDefault (Some "beta") (Some "b1")
        Assert.Equal("alpha", (AppConfig.defaultSelection disabled |> Option.get |> fst).id)
        // 悬空引用的文件仍可读入（不会因删了模型服务就让整份配置被拒）
        Assert.True(TomlCodec.tryParse (TomlCodec.serialize dangling) |> Result.isOk)

    [<Fact>]
    let ``ConfigMutation 写入、校验与清除全局默认`` () =
        let cfg = twoProviders ()
        let set (o: JsonObject) = ConfigMutation.updateGeneration cfg o
        let ok = JsonObject()
        ok["defaultProvider"] <- "beta"
        ok["defaultModel"] <- "b1"
        match set ok with
        | Ok next ->
            Assert.Equal(Some "beta", next.generation.defaultProvider)
            Assert.Equal(Some "b1", next.generation.defaultModel)
            // 其余生成参数不受影响
            Assert.Equal(cfg.generation.maxToolRounds, next.generation.maxToolRounds)
            // 空串 = 清除
            let clear = JsonObject()
            clear["defaultProvider"] <- ""
            clear["defaultModel"] <- ""
            match ConfigMutation.updateGeneration next clear with
            | Ok cleared ->
                Assert.Equal(None, cleared.generation.defaultProvider)
                Assert.Equal(None, cleared.generation.defaultModel)
            | Error e -> failwithf "清除失败：%A" e
            // 不带这两个键的更新保持原值（设置页其他表单不会误清全局默认）
            let unrelated = JsonObject()
            unrelated["autoTitle"] <- false
            match ConfigMutation.updateGeneration next unrelated with
            | Ok kept -> Assert.Equal(Some "beta", kept.generation.defaultProvider)
            | Error e -> failwithf "无关更新失败：%A" e
            // 删除被指向的模型服务时一并清掉默认
            match ConfigMutation.deleteProvider next "beta" with
            | Ok afterDelete ->
                Assert.Equal(None, afterDelete.generation.defaultProvider)
                Assert.Equal(None, afterDelete.generation.defaultModel)
            | Error e -> failwithf "删除失败：%A" e
        | Error e -> failwithf "设置失败：%A" e
        let missing = JsonObject()
        missing["defaultProvider"] <- "nope"
        Assert.True(set missing |> Result.isError)
        let badModel = JsonObject()
        badModel["defaultProvider"] <- "beta"
        badModel["defaultModel"] <- "a1"
        Assert.True(set badModel |> Result.isError)
        let orphanModel = JsonObject()
        orphanModel["defaultModel"] <- "b1"
        Assert.True(set orphanModel |> Result.isError)

    [<Fact>]
    let ``目录快照下发全局默认，客户端与主机选择规则一致`` () =
        let cfg = twoProviders ()
        let snapshotOf (c: AppConfig) =
            Catalog.parse (ProviderCatalog.providers c) (JsonArray()) (ProviderCatalog.generation c)
        let noDefault = snapshotOf cfg
        Assert.Equal(None, noDefault.generation.defaultProvider)
        Assert.False((ProviderCatalog.generation cfg).ContainsKey "defaultProvider", "未设置时不下发该键（旧客户端无感）")
        let withGlobal = cfg |> withDefault (Some "beta") (Some "b1")
        let catalog = snapshotOf withGlobal
        Assert.Equal(Some "beta", catalog.generation.defaultProvider)
        for c in [ cfg; withGlobal; cfg |> withDefault (Some "beta") None ] do
            let server = AppConfig.defaultSelection c |> Option.map (fun (p, m) -> p.id, m)
            Assert.Equal(server, Catalog.defaultSelection (snapshotOf c))

    // ---- 验收 3：类型化探测错误 ----

    let private getFreePort () =
        let l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)
        l.Start()
        let port = (l.LocalEndpoint :?> System.Net.IPEndPoint).Port
        l.Stop()
        port

    /// 起一个只应答一次的本地 HTTP 端点。
    let private serveOnce (status: int) (body: string) : string * (unit -> unit) =
        let listener = new HttpListener()
        let prefix = sprintf "http://127.0.0.1:%d/" (getFreePort ())
        listener.Prefixes.Add prefix
        listener.Start()
        let task =
            Task.Run(fun () ->
                try
                    let ctx = listener.GetContext()
                    let bytes = Encoding.UTF8.GetBytes body
                    ctx.Response.StatusCode <- status
                    ctx.Response.ContentType <- "application/json"
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                    ctx.Response.OutputStream.Close()
                with _ -> ())
        prefix.TrimEnd '/',
        fun () ->
            (try listener.Stop() with _ -> ())
            task.Wait 2000 |> ignore

    let private probe (kind: string) (baseUrl: string) =
        let p = { provider "probe" [ "m" ] "m" with kind = kind; baseUrl = baseUrl; timeoutSeconds = 5 }
        ProviderCatalog.probeModelsTyped p CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously

    [<Fact>]
    let ``探测失败：密钥无效、端点不通、路径错各自给出不同的人话原因`` () =
        let keyUrl, stopKey = serveOnce 401 """{"error":"invalid api key sk-test"}"""
        let pathUrl, stopPath = serveOnce 404 "not found"
        let deadUrl = sprintf "http://127.0.0.1:%d" (getFreePort ())
        try
            let invalid = probe "openai" keyUrl
            let wrongPath = probe "openai" pathUrl
            let dead = probe "openai" deadUrl
            let failureOf r = match r with Error(f, m) -> f, m | Ok _ -> failwith "应当失败"
            let f1, m1 = failureOf invalid
            let f2, m2 = failureOf wrongPath
            let f3, m3 = failureOf dead
            Assert.Equal(ProviderCatalog.InvalidKey, f1)
            Assert.Equal(ProviderCatalog.EndpointNotFound, f2)
            Assert.Equal(ProviderCatalog.Unreachable, f3)
            // 三条互不相同
            Assert.Equal(3, [ m1; m2; m3 ] |> List.distinct |> List.length)
            for m in [ m1; m2; m3 ] do
                // 不是只写“失败”：至少三段（发生了什么 / 为什么 / 下一步），带下一步动作
                Assert.True(m.Length > 25, m)
                Assert.True(m <> "失败" && not (m.Trim().EndsWith "失败"), m)
                Assert.Contains("请", m)
            // 密钥永不出现在错误文案里（决策 41）
            for m in [ m1; m2; m3 ] do Assert.DoesNotContain("sk-test", m)
        finally
            stopKey ()
            stopPath ()

    [<Fact>]
    let ``探测失败：原生端点路径错时提示改用兼容端点，兼容端点路径错时提示检查 v1`` () =
        let url1, stop1 = serveOnce 404 "x"
        let url2, stop2 = serveOnce 404 "x"
        try
            match probe "anthropic" url1, probe "openai" url2 with
            | Error(_, nativeMsg), Error(_, compatMsg) ->
                Assert.Contains("兼容端点", nativeMsg)
                Assert.Contains("/v1", compatMsg)
            | _ -> failwith "应当失败"
        finally
            stop1 ()
            stop2 ()

    [<Fact>]
    let ``探测失败：限流、模型服务错误、非清单响应都有各自类型`` () =
        let cases =
            [ 429, "{}", ProviderCatalog.RateLimited
              503, "oops", ProviderCatalog.ServerError
              200, "<html>hello</html>", ProviderCatalog.BadResponse
              418, "teapot", ProviderCatalog.Rejected ]
        for status, body, expected in cases do
            let url, stop = serveOnce status body
            try
                match probe "openai" url with
                | Error(f, _) -> Assert.Equal(expected, f)
                | Ok _ -> failwithf "状态 %d 应当失败" status
            finally
                stop ()

    [<Fact>]
    let ``探测成功仍返回排序后的模型清单`` () =
        let url, stop = serveOnce 200 """{"data":[{"id":"zz"},{"id":"aa"}]}"""
        try
            match probe "openai" url with
            | Ok ids -> Assert.Equal<string list>([ "aa"; "zz" ], ids)
            | Error(_, m) -> failwith m
        finally
            stop ()

    // ---- 预设重排与合并 ----

    // ---- 验收 6：卡片与设置页同一保存路径 ----

    [<Fact>]
    let ``两条入口经同一个 ConfigMutation 写出相同的 TOML`` () =
        let g = Guid.Parse "22222222-2222-2222-2222-222222222222"
        let empty = AppConfig.defaults g
        let preset = ProviderPresets.tryFind "openai" |> Option.get
        let viaCard = ProviderPresets.presetPayload preset "openai" (Some "sk-test") []
        // 设置页编辑器按同样的预设默认值调用同一个构造函数
        let viaSettings =
            ProviderPresets.payload
                "openai" preset.label preset.kind preset.baseUrl (Some "sk-test")
                preset.models (List.head preset.models) true 120 2
        let write (payload: JsonObject) =
            match ConfigMutation.upsertProvider empty payload with
            | Ok cfg -> TomlCodec.serialize cfg
            | Error e -> failwithf "%A" e
        Assert.Equal(write viaSettings, write viaCard)
        // 第二步（探测后写回清单与默认模型）同样与设置页“填完清单再保存”一致
        let probed = [ "gpt-4o"; "gpt-5"; "text-embedding-3-small" ]
        let cardStep2 =
            ProviderPresets.payload "openai" preset.label preset.kind preset.baseUrl None probed (OnboardingRules.pickDefaultModel preset.models probed) true 120 2
        let settingsStep2 =
            ProviderPresets.payload "openai" preset.label preset.kind preset.baseUrl None probed "gpt-5" true 120 2
        let after (step2: JsonObject) =
            match ConfigMutation.upsertProvider empty viaCard with
            | Ok cfg ->
                match ConfigMutation.upsertProvider cfg step2 with
                | Ok cfg2 -> TomlCodec.serialize cfg2
                | Error e -> failwithf "%A" e
            | Error e -> failwithf "%A" e
        Assert.Equal(after settingsStep2, after cardStep2)
        Assert.Contains("defaultModel = \"gpt-5\"", after cardStep2)
        Assert.Contains("apiKey = \"sk-test\"", after cardStep2)

    // ---- 本机 Ollama 发现 ----

    [<Fact>]
    let ``只有主机在本机时才探测本机端口`` () =
        Assert.True(OnboardingRules.canDiscoverLocal "ws://127.0.0.1:8765/ws")
        Assert.True(OnboardingRules.canDiscoverLocal "http://localhost:8765")
        Assert.False(OnboardingRules.canDiscoverLocal "https://wanxiang.example.com")
        Assert.False(OnboardingRules.canDiscoverLocal "")
        Assert.Equal("http://127.0.0.1:11434/v1/models", ProviderPresets.OllamaProbeUrl)
