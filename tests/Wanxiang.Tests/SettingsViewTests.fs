namespace Wanxiang.Tests

open Wanxiang2.SettingsScreen
open System
open System.Reflection
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.UI

[<Trait("Category", "UI")>]
module SettingsViewTests =

    let rec private descendants (control: Control) =
        seq {
            yield control
            match control with
            | :? Panel as panel ->
                for child in panel.Children do
                    yield! descendants child
            | :? Decorator as decorator when not (isNull decorator.Child) ->
                yield! descendants decorator.Child
            | :? ContentControl as host ->
                match host.Content with
                | :? Control as child -> yield! descendants child
                | _ -> ()
            | _ -> ()
        }

    let private byAutomationName (root: Control) (name: string) =
        descendants root
        |> Seq.find (fun control -> AutomationProperties.GetName(control) = name)

    let private byAutomationId (root: Control) (id: string) =
        descendants root
        |> Seq.find (fun control -> AutomationProperties.GetAutomationId(control) = id)

    let private tryByAutomationName (root: Control) (name: string) =
        descendants root
        |> Seq.tryFind (fun control -> AutomationProperties.GetName(control) = name)

    let private overlay () = OverlayHost(Grid())

    let private stubActions
        (toasts: ResizeArray<string * ToastTone>)
        (upsertProvider: JsonObject -> (bool -> unit) -> unit)
        (deleteProvider: string -> unit)
        (probeProvider: string -> unit)
        (upsertMcp: JsonObject -> (bool -> unit) -> unit)
        (deleteMcp: string -> unit)
        (updateGeneration: JsonObject -> (bool -> unit) -> unit)
        : Wanxiang2.SettingsScreen.SettingsHooks =
        { UpsertProvider = upsertProvider
          DeleteProvider = deleteProvider
          ProbeProvider = probeProvider
          UpsertMcp = upsertMcp
          DeleteMcp = deleteMcp
          UpdateGeneration = updateGeneration
          SavePrefs = ignore }

    let private defaultActions (toasts: ResizeArray<string * ToastTone>) =
        stubActions
            toasts
            (fun _ cb -> cb true)
            ignore
            ignore
            (fun _ cb -> cb true)
            ignore
            (fun _ cb -> cb true)

    let private invokePrivate<'T> (target: obj) (methodName: string) (args: obj array) : 'T =
        let flags = BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static
        let m = target.GetType().GetMethod(methodName, flags)
        if isNull m then
            failwithf "Method %s not found on %s" methodName (target.GetType().FullName)
        m.Invoke(target, args) :?> 'T

    let private getPrivateField<'T> (target: obj) (fieldName: string) : 'T =
        let flags = BindingFlags.NonPublic ||| BindingFlags.Instance
        let f = target.GetType().GetField(fieldName, flags)
        if isNull f then
            failwithf "Field %s not found on %s" fieldName (target.GetType().FullName)
        f.GetValue(target) :?> 'T

    let private setPrivateField<'T> (target: obj) (fieldName: string) (value: 'T) =
        let flags = BindingFlags.NonPublic ||| BindingFlags.Instance
        let f = target.GetType().GetField(fieldName, flags)
        if isNull f then
            failwithf "Field %s not found on %s" fieldName (target.GetType().FullName)
        f.SetValue(target, box value)

    /// Headless 环境下的多轮异步帧泵送辅助函数，驱动 Dispatcher 与 UIThread.Post 任务队列
    let private pump (ms: int) =
        let sw = System.Diagnostics.Stopwatch.StartNew()
        while sw.ElapsedMilliseconds < int64 ms do
            System.Threading.Thread.Sleep 15
            Dispatcher.UIThread.RunJobs()

    /// 模拟表单提交：向保存按钮派发 Enter 键盘事件并立即调用 Dispatcher.UIThread.RunJobs() 泵送异步验证提示
    let private submit (saveBtn: Border) =
        saveBtn.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = saveBtn))
        Dispatcher.UIThread.RunJobs()
        pump 40

    // ==========================================
    // 1. SettingsTools: 表单校验、正则与 MCP 载荷生成
    // ==========================================

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``SettingsTools mcpPayload constructs complete and compliant JsonObject`` () =
        Headless.run (fun () ->

            // 测试完整字段生成
            let payload1: JsonObject =
                McpForm.payload { id = "server-1"; label = "Server One"; command = "npx"; args = [ "-y"; "pkg" ]; url = ""; timeout = 120 } true
            Assert.Equal("server-1", payload1["id"].GetValue<string>())
            Assert.Equal("Server One", payload1["label"].GetValue<string>())
            Assert.Equal("npx", payload1["command"].GetValue<string>())
            Assert.False(payload1.ContainsKey("url"))
            Assert.Equal(2, (payload1["args"] :?> JsonArray).Count)
            Assert.Equal(120, payload1["callTimeoutSeconds"].GetValue<int>())
            Assert.True(payload1["enabled"].GetValue<bool>())

            // 测试远程 URL 字段（无 command 与 args）
            let payload2: JsonObject =
                McpForm.payload { id = "server-2"; label = "Server Two"; command = ""; args = []; url = "https://example.com/mcp"; timeout = 60 } false
            Assert.Equal("server-2", payload2["id"].GetValue<string>())
            Assert.False(payload2.ContainsKey("command"))
            Assert.False(payload2.ContainsKey("args"))
            Assert.Equal("https://example.com/mcp", payload2["url"].GetValue<string>())
            Assert.Equal(60, payload2["callTimeoutSeconds"].GetValue<int>())
            Assert.False(payload2["enabled"].GetValue<bool>())
        )

    // ==========================================
    // 2. SettingsProviders: 模型服务校验、Loopback、BaseUrl 缺省与模型增删改
    // ==========================================

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``SettingsProviders isLoopbackUrl identifies local and loopback addresses`` () =
        Headless.run (fun () ->
            let isLoopback (url: string) : bool = ProviderForm.isLoopbackUrl url

            Assert.True(isLoopback "http://127.0.0.1:11434")
            Assert.True(isLoopback "http://localhost:8080/v1")
            Assert.True(isLoopback "http://[::1]:8000")
            Assert.True(isLoopback "http://::1:8000")
            Assert.False(isLoopback "https://api.openai.com/v1")
            Assert.False(isLoopback "https://api.anthropic.com")
            Assert.False(isLoopback "")
            Assert.False(isLoopback null)
        )

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``SettingsProviders providerPayload constructs correct JsonObject`` () =
        Headless.run (fun () ->
            let payload: JsonObject =
                ProviderPresets.payload "prov-1" "Provider 1" "openai" "https://api.example.com/v1" (Some "sk-12345") [ "gpt-4"; "gpt-3.5" ] "gpt-4" true 90 3

            Assert.Equal("prov-1", payload["id"].GetValue<string>())
            Assert.Equal("Provider 1", payload["label"].GetValue<string>())
            Assert.Equal("openai", payload["kind"].GetValue<string>())
            Assert.Equal("https://api.example.com/v1", payload["baseUrl"].GetValue<string>())
            Assert.Equal("sk-12345", payload["apiKey"].GetValue<string>())
            Assert.Equal(2, (payload["models"] :?> JsonArray).Count)
            Assert.Equal("gpt-4", payload["defaultModel"].GetValue<string>())
            Assert.True(payload["enabled"].GetValue<bool>())
            Assert.Equal(90, payload["timeoutSeconds"].GetValue<int>())
            Assert.Equal(3, payload["maxRetries"].GetValue<int>())
        )

    // ==========================================
    // 3. SettingsGeneral: 生成参数校验、外观主题/字号与系统诊断
    // ==========================================

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``SettingsGeneral pure parse and formatting helpers behave correctly`` () =
        Headless.run (fun () ->
            let floatText (v: float option) : string = GenerationForm.floatText v
            let ok t = Result.isOk (GenerationForm.validate t "" "" "20" "8")
            let okInt t = Result.isOk (GenerationForm.validate "" "" t "20" "8")

            Assert.Equal("1.25", floatText (Some 1.25))
            Assert.Equal("", floatText None)

            Assert.True(ok "0.7")
            Assert.True(ok "  1.0  ")
            Assert.False(ok "abc")
            Assert.True(ok "")

            Assert.True(okInt "100")
            Assert.True(okInt "  42  ")
            Assert.False(okInt "12.34")
            Assert.True(okInt "")
        )

    // ==========================================
    // 4. SettingsView: 分区导航、缓存失效与滚动位置
    // ==========================================
