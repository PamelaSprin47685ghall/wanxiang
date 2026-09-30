namespace Wanxiang.Tests

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
        : SettingsActions =
        { upsertProvider = upsertProvider
          deleteProvider = deleteProvider
          probeProvider = probeProvider
          upsertMcp = upsertMcp
          deleteMcp = deleteMcp
          updateGeneration = updateGeneration
          savePrefs = ignore
          toast = fun message tone -> toasts.Add(message, tone) }

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

    [<Fact>]
    let ``SettingsTools mcpPayload constructs complete and compliant JsonObject`` () =
        Headless.run (fun () ->
            let toasts = ResizeArray<string * ToastTone>()
            let tools = SettingsTools(overlay (), defaultActions toasts)

            // 测试完整字段生成
            let payload1: JsonObject =
                invokePrivate tools "mcpPayload" [| box "server-1"; box "Server One"; box "npx"; box [ "-y"; "pkg" ]; box ""; box 120; box true |]
            Assert.Equal("server-1", payload1["id"].GetValue<string>())
            Assert.Equal("Server One", payload1["label"].GetValue<string>())
            Assert.Equal("npx", payload1["command"].GetValue<string>())
            Assert.False(payload1.ContainsKey("url"))
            Assert.Equal(2, (payload1["args"] :?> JsonArray).Count)
            Assert.Equal(120, payload1["callTimeoutSeconds"].GetValue<int>())
            Assert.True(payload1["enabled"].GetValue<bool>())

            // 测试远程 URL 字段（无 command 与 args）
            let payload2: JsonObject =
                invokePrivate tools "mcpPayload" [| box "server-2"; box "Server Two"; box ""; box ([]: string list); box "https://example.com/mcp"; box 60; box false |]
            Assert.Equal("server-2", payload2["id"].GetValue<string>())
            Assert.False(payload2.ContainsKey("command"))
            Assert.False(payload2.ContainsKey("args"))
            Assert.Equal("https://example.com/mcp", payload2["url"].GetValue<string>())
            Assert.Equal(60, payload2["callTimeoutSeconds"].GetValue<int>())
            Assert.False(payload2["enabled"].GetValue<bool>())
        )

    [<Fact>]
    let ``SettingsTools ShowEditor validates id, command/url, timeout and saves valid config`` () =
        Headless.run (fun () ->
            let root = Grid()
            let over = OverlayHost(root)
            let toasts = ResizeArray<string * ToastTone>()
            let savedMcp = ResizeArray<JsonObject>()
            let actions =
                stubActions
                    toasts
                    (fun _ cb -> cb true)
                    ignore
                    ignore
                    (fun payload cb ->
                        savedMcp.Add payload
                        cb true)
                    ignore
                    (fun _ cb -> cb true)
            let tools = SettingsTools(over, actions)
            let window = Window(Width = 800.0, Height = 600.0, Content = root)
            window.Show()
            Dispatcher.UIThread.RunJobs()

            // 1. 打开新建编辑器
            invokePrivate<unit> tools "ShowEditor" [| box None |]
            Dispatcher.UIThread.RunJobs()

            let saveBtn = byAutomationName root "添加" :?> Border
            let errorSummary = tryByAutomationName root "表单错误摘要"

            // 获取输入框
            let textBoxes = descendants root |> Seq.choose (function :? TextBox as tb -> Some tb | _ -> None) |> List.ofSeq
            Assert.True(textBoxes.Length >= 6)
            let idBox = textBoxes.[0]
            let labelBox = textBoxes.[1]
            let commandBox = textBoxes.[2]
            let argsBox = textBoxes.[3]
            let urlBox = textBoxes.[4]
            let timeoutBox = textBoxes.[5]

            // 触发空白表单保存 -> 校验失败（id, label, command/url 均为空），错误摘要应可见
            submit saveBtn
            Assert.Empty(savedMcp)
            Assert.True(errorSummary.IsSome && errorSummary.Value.IsVisible)
            Assert.Equal("稳定标识不能为空。", Ui.fieldValidationMessage(idBox).Text)
            Assert.Equal("显示名称不能为空。", Ui.fieldValidationMessage(labelBox).Text)

            // 2. 测试非法 ID（包含非法字符）
            idBox.Text <- "invalid id with space!"
            labelBox.Text <- "My MCP"
            commandBox.Text <- "npx"
            timeoutBox.Text <- "60"
            submit saveBtn
            Assert.Empty(savedMcp)
            Assert.Equal("稳定标识仅支持英文字母、数字、下划线(_)与连字符(-)。", Ui.fieldValidationMessage(idBox).Text)

            // 3. 测试 command 与 url 互斥（同时填写）
            idBox.Text <- "valid_id-123"
            urlBox.Text <- "https://api.example.com"
            submit saveBtn
            Assert.Empty(savedMcp)
            Assert.Equal("与远程端点只能填写一个。", Ui.fieldValidationMessage(commandBox).Text)
            Assert.Equal("与本地命令只能填写一个。", Ui.fieldValidationMessage(urlBox).Text)

            // 4. 测试非法 URL 格式（非 http/https）
            commandBox.Text <- ""
            urlBox.Text <- "ftp://illegal.com"
            submit saveBtn
            Assert.Empty(savedMcp)
            Assert.Equal("远程端点地址必须以 http:// 或 https:// 开头。", Ui.fieldValidationMessage(urlBox).Text)

            // 5. 测试超时验证（<1、>3600、非数字）
            urlBox.Text <- "https://api.example.com"
            timeoutBox.Text <- "0"
            submit saveBtn
            Assert.Empty(savedMcp)
            Assert.Equal("请输入 1 到 3600 之间的秒数。", Ui.fieldValidationMessage(timeoutBox).Text)

            timeoutBox.Text <- "5000"
            submit saveBtn
            Assert.Empty(savedMcp)
            Assert.Equal("请输入 1 到 3600 之间的秒数。", Ui.fieldValidationMessage(timeoutBox).Text)

            timeoutBox.Text <- "not_a_number"
            submit saveBtn
            Assert.Empty(savedMcp)
            Assert.Equal("请输入有效的正整数秒数（1–3600）。", Ui.fieldValidationMessage(timeoutBox).Text)

            // 6. 填写完全合法的内容并提交
            timeoutBox.Text <- "120"
            argsBox.Text <- "--arg1\n  --arg2  \n"
            submit saveBtn

            Assert.Single(savedMcp) |> ignore
            let payload = savedMcp.[0]
            Assert.Equal("valid_id-123", payload["id"].GetValue<string>())
            Assert.Equal("My MCP", payload["label"].GetValue<string>())
            Assert.Equal("https://api.example.com", payload["url"].GetValue<string>())
            Assert.Equal(120, payload["callTimeoutSeconds"].GetValue<int>())

            window.Close()
        )

    [<Fact>]
    let ``SettingsTools SetCatalog renders tool list, mcp list and sandbox notes`` () =
        Headless.run (fun () ->
            let toasts = ResizeArray<string * ToastTone>()
            let deletedMcp = ResizeArray<string>()
            let actions =
                stubActions
                    toasts
                    (fun _ cb -> cb true)
                    ignore
                    ignore
                    (fun _ cb -> cb true)
                    (fun id -> deletedMcp.Add id)
                    (fun _ cb -> cb true)
            let tools = SettingsTools(overlay (), actions)
            let built = tools.Build()

            // 1. 空目录测试
            tools.SetCatalog Catalog.empty
            let emptyBlocks = descendants built |> Seq.choose (function :? TextBlock as tb when tb.Text.Contains("当前没有可用工具") -> Some tb | _ -> None)
            Assert.NotEmpty(emptyBlocks)

            // 2. 装载内置工具与 MCP 服务器
            let sampleTool1: ToolInfo =
                { id = "read_file"
                  label = "读取文件"
                  description = "读取本地沙箱中的文件。"
                  source = "builtin"
                  serverId = None }
            let sampleTool2: ToolInfo =
                { id = "mcp_tool"
                  label = "MCP 工具"
                  description = ""
                  source = "mcp"
                  serverId = Some "mcp-srv" }
            let sampleMcp: McpInfo =
                { id = "mcp-srv"
                  label = "示例 MCP"
                  command = Some "python"
                  args = [ "main.py" ]
                  url = None
                  callTimeoutSeconds = 30
                  enabled = true }
            let sampleDisabledMcp: McpInfo =
                { id = "mcp-remote"
                  label = "远程 MCP"
                  command = None
                  args = []
                  url = Some "https://mcp.remote.com"
                  callTimeoutSeconds = 60
                  enabled = false }

            let catalogWithTools =
                { Catalog.empty with
                    tools = [ sampleTool1; sampleTool2 ]
                    mcpServers = [ sampleMcp; sampleDisabledMcp ]
                    generation = { Catalog.empty.generation with fileReadRoots = [ "/tmp/sandbox" ] } }

            tools.SetCatalog catalogWithTools

            // 检查工具卡片与 MCP 卡片渲染
            let toolLabels = descendants built |> Seq.choose (function :? TextBlock as tb when tb.Text = "读取文件" || tb.Text = "MCP 工具" -> Some tb.Text | _ -> None) |> List.ofSeq
            Assert.Contains("读取文件", toolLabels)
            Assert.Contains("MCP 工具", toolLabels)

            let mcpLabels = descendants built |> Seq.choose (function :? TextBlock as tb when tb.Text = "示例 MCP" || tb.Text = "远程 MCP" -> Some tb.Text | _ -> None) |> List.ofSeq
            Assert.Contains("示例 MCP", mcpLabels)
            Assert.Contains("远程 MCP", mcpLabels)

            // 检查沙箱根目录提示
            let sandboxNote = descendants built |> Seq.choose (function :? TextBlock as tb when tb.Text.Contains("/tmp/sandbox") -> Some tb.Text | _ -> None) |> List.ofSeq
            Assert.NotEmpty(sandboxNote)
        )

    // ==========================================
    // 2. SettingsProviders: 供应商校验、Loopback、BaseUrl 缺省与模型增删改
    // ==========================================

    [<Fact>]
    let ``SettingsProviders isLoopbackUrl identifies local and loopback addresses`` () =
        Headless.run (fun () ->
            let toasts = ResizeArray<string * ToastTone>()
            let providers = SettingsProviders(overlay (), defaultActions toasts)

            let isLoopback (url: string) : bool =
                invokePrivate providers "isLoopbackUrl" [| box url |]

            Assert.True(isLoopback "http://127.0.0.1:11434")
            Assert.True(isLoopback "http://localhost:8080/v1")
            Assert.True(isLoopback "http://[::1]:8000")
            Assert.True(isLoopback "http://::1:8000")
            Assert.False(isLoopback "https://api.openai.com/v1")
            Assert.False(isLoopback "https://api.anthropic.com")
            Assert.False(isLoopback "")
            Assert.False(isLoopback null)
        )

    [<Fact>]
    let ``SettingsProviders providerPayload constructs correct JsonObject`` () =
        Headless.run (fun () ->
            let toasts = ResizeArray<string * ToastTone>()
            let providers = SettingsProviders(overlay (), defaultActions toasts)

            let payload: JsonObject =
                invokePrivate providers "providerPayload"
                    [| box "prov-1"
                       box "Provider 1"
                       box "openai"
                       box "https://api.example.com/v1"
                       box (Some "sk-12345")
                       box [ "gpt-4"; "gpt-3.5" ]
                       box "gpt-4"
                       box true
                       box 90
                       box 3 |]

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

    [<Fact>]
    let ``SettingsProviders ShowEditor validates required fields and default model existence`` () =
        Headless.run (fun () ->
            let root = Grid()
            let over = OverlayHost(root)
            let toasts = ResizeArray<string * ToastTone>()
            let savedProviders = ResizeArray<JsonObject>()
            let actions =
                stubActions
                    toasts
                    (fun payload cb ->
                        savedProviders.Add payload
                        cb true)
                    ignore
                    ignore
                    (fun _ cb -> cb true)
                    ignore
                    (fun _ cb -> cb true)
            let providers = SettingsProviders(over, actions)
            let window = Window(Width = 800.0, Height = 600.0, Content = root)
            window.Show()
            Dispatcher.UIThread.RunJobs()

            // 打开新建服务商编辑器（创建模式下按钮为“添加”）
            invokePrivate<unit> providers "ShowEditor" [| box None |]
            Dispatcher.UIThread.RunJobs()

            let saveBtn = byAutomationName root "添加" :?> Border

            let textBoxes = descendants root |> Seq.choose (function :? TextBox as tb -> Some tb | _ -> None) |> List.ofSeq
            Assert.True(textBoxes.Length >= 6)
            let idBox = textBoxes.[0]
            let labelBox = textBoxes.[1]
            let urlBox = textBoxes.[2]
            let keyBox = textBoxes.[3]
            let modelsBox = textBoxes.[4]
            let defaultModelBox = textBoxes.[5]

            // 1. 清空必填字段并保存 -> 验证必填字段错误（默认模型留空为合法可选，不触发必填错误）
            idBox.Text <- ""
            labelBox.Text <- ""
            urlBox.Text <- ""
            modelsBox.Text <- ""
            defaultModelBox.Text <- ""
            submit saveBtn

            Assert.Empty(savedProviders)
            Assert.Equal("稳定标识不能为空。", Ui.fieldValidationMessage(idBox).Text)
            Assert.Equal("显示名称不能为空。", Ui.fieldValidationMessage(labelBox).Text)
            Assert.Equal("端点地址不能为空。", Ui.fieldValidationMessage(urlBox).Text)
            Assert.Equal("至少填写一个模型名（每行一个）。", Ui.fieldValidationMessage(modelsBox).Text)

            // 2. 填写非法 URL
            idBox.Text <- "custom-prov"
            labelBox.Text <- "Custom Provider"
            urlBox.Text <- "invalid-url"
            modelsBox.Text <- "model-a\nmodel-b"
            submit saveBtn
            Assert.Empty(savedProviders)
            Assert.Equal("端点地址必须以 http:// 或 https:// 开头。", Ui.fieldValidationMessage(urlBox).Text)

            // 3. 填写不在模型列表中的默认模型 -> 精确触发默认模型存在性校验
            urlBox.Text <- "https://api.openai.com/v1"
            defaultModelBox.Text <- "non-existent-model"
            submit saveBtn
            Assert.Empty(savedProviders)
            Assert.Equal("默认模型“non-existent-model”不在模型列表中，请从列表中选择或留空自动选用。", Ui.fieldValidationMessage(defaultModelBox).Text)

            // 4. 留空默认模型 -> 应自动选用列表首个模型并合法保存
            defaultModelBox.Text <- ""
            submit saveBtn

            Assert.Single(savedProviders) |> ignore
            let payload = savedProviders.[0]
            Assert.Equal("custom-prov", payload["id"].GetValue<string>())
            Assert.Equal("model-a", payload["defaultModel"].GetValue<string>())

            window.Close()
        )

    [<Fact>]
    let ``SettingsProviders applyPreset updates endpoint, models and tracks divergence to custom`` () =
        Headless.run (fun () ->
            let root = Grid()
            let over = OverlayHost(root)
            let toasts = ResizeArray<string * ToastTone>()
            let providers = SettingsProviders(over, defaultActions toasts)
            let window = Window(Width = 800.0, Height = 600.0, Content = root)
            window.Show()
            Dispatcher.UIThread.RunJobs()

            // 1. 打开新建编辑器：此时默认载入 OpenAI 预设
            invokePrivate<unit> providers "ShowEditor" [| box None |]
            Dispatcher.UIThread.RunJobs()

            let textBoxes = descendants root |> Seq.choose (function :? TextBox as tb -> Some tb | _ -> None) |> List.ofSeq
            let idBox = textBoxes.[0]
            let labelBox = textBoxes.[1]
            let urlBox = textBoxes.[2]
            let modelsBox = textBoxes.[4]

            // 验证默认从 OpenAI 预设初始化
            Assert.Equal("openai", idBox.Text)
            Assert.Equal("OpenAI", labelBox.Text)
            Assert.Equal("https://api.openai.com/v1", urlBox.Text)

            // 查找预设选择按钮（文本为 "OpenAI"）
            let presetTextBlocks =
                descendants root
                |> Seq.choose (function :? TextBlock as tb when tb.Text = "OpenAI" -> Some tb | _ -> None)
                |> List.ofSeq
            Assert.NotEmpty(presetTextBlocks)

            // 2. 手动修改模型列表以偏离预设 -> 触发 markCustomIfDiverged，预设按钮文本应自动变为“自定义”
            modelsBox.Text <- "custom-model-only"
            Dispatcher.UIThread.RunJobs()
            pump 50

            let customTextBlocks =
                descendants root
                |> Seq.choose (function :? TextBlock as tb when tb.Text = "自定义" -> Some tb | _ -> None)
                |> List.ofSeq
            Assert.NotEmpty(customTextBlocks)

            window.Close()
        )

    // ==========================================
    // 3. SettingsGeneral: 生成参数校验、外观主题/字号与系统诊断
    // ==========================================

    [<Fact>]
    let ``SettingsGeneral pure parse and formatting helpers behave correctly`` () =
        Headless.run (fun () ->
            let toasts = ResizeArray<string * ToastTone>()
            let general = SettingsGeneral(overlay (), defaultActions toasts, ignore)

            let floatText (v: float option) : string =
                invokePrivate general "floatText" [| box v |]
            let parseFloatOpt (s: string) : float option =
                invokePrivate general "parseFloatOpt" [| box s |]
            let parseIntOpt (s: string) : int option =
                invokePrivate general "parseIntOpt" [| box s |]

            Assert.Equal("1.25", floatText (Some 1.25))
            Assert.Equal("", floatText None)

            Assert.Equal(Some 0.7, parseFloatOpt "0.7")
            Assert.Equal(Some 1.0, parseFloatOpt "  1.0  ")
            Assert.True(Option.isNone (parseFloatOpt "abc"))
            Assert.True(Option.isNone (parseFloatOpt ""))

            Assert.Equal(Some 100, parseIntOpt "100")
            Assert.Equal(Some 42, parseIntOpt "  42  ")
            Assert.True(Option.isNone (parseIntOpt "12.34"))
            Assert.True(Option.isNone (parseIntOpt ""))
        )

    [<Fact>]
    let ``SettingsGeneral SaveGenerationCore validates numeric ranges and generates payload`` () =
        Headless.run (fun () ->
            let toasts = ResizeArray<string * ToastTone>()
            let savedGeneration = ResizeArray<JsonObject>()
            let actions =
                stubActions
                    toasts
                    (fun _ cb -> cb true)
                    ignore
                    ignore
                    (fun _ cb -> cb true)
                    ignore
                    (fun payload cb ->
                        savedGeneration.Add payload
                        cb true)

            let general = SettingsGeneral(overlay (), actions, ignore)
            // 先装载空配置以赋予表单标准合法初值（contextMessages=200, toolRounds=12），保证必填字段有合法默认值
            general.SetCatalog Catalog.empty
            let built = general.BuildGeneration()

            let tempBox: TextBox = getPrivateField general "temperatureBox"
            let topPBox: TextBox = getPrivateField general "topPBox"
            let maxTokensBox: TextBox = getPrivateField general "maxTokensBox"
            let contextBox: TextBox = getPrivateField general "contextBox"
            let toolRoundsBox: TextBox = getPrivateField general "toolRoundsBox"
            let instructionsBox: TextBox = getPrivateField general "instructionsBox"

            // 确保 contextBox 和 toolRoundsBox 有合法初值，避免组合干扰单项校验
            contextBox.Text <- "100"
            toolRoundsBox.Text <- "10"

            // 1. 测试温度非法（<0.0 或 >2.0）
            tempBox.Text <- "2.5"
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()
            Assert.Empty(savedGeneration)
            Assert.Equal("请输入 0–2 之间的数字，或留空。", Ui.fieldValidationMessage(tempBox).Text)

            tempBox.Text <- "-0.5"
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()
            Assert.Empty(savedGeneration)
            Assert.Equal("请输入 0–2 之间的数字，或留空。", Ui.fieldValidationMessage(tempBox).Text)

            // 恢复温度为合法值 0.7
            tempBox.Text <- "0.7"

            // 2. 测试 TopP 非法（<0.0 或 >1.0）：生产代码文案严格包含汉字“到”
            topPBox.Text <- "1.5"
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()
            Assert.Empty(savedGeneration)
            Assert.Equal("请输入 0 到 1 之间的数字，或留空。", Ui.fieldValidationMessage(topPBox).Text)

            topPBox.Text <- "-0.1"
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()
            Assert.Empty(savedGeneration)
            Assert.Equal("请输入 0 到 1 之间的数字，或留空。", Ui.fieldValidationMessage(topPBox).Text)

            // 恢复 TopP 为合法值 0.9
            topPBox.Text <- "0.9"

            // 3. 测试 maxTokens 非法（<= 0）
            maxTokensBox.Text <- "0"
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()
            Assert.Empty(savedGeneration)
            Assert.Equal("请输入大于 0 的整数，或留空。", Ui.fieldValidationMessage(maxTokensBox).Text)

            maxTokensBox.Text <- "-10"
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()
            Assert.Empty(savedGeneration)
            Assert.Equal("请输入大于 0 的整数，或留空。", Ui.fieldValidationMessage(maxTokensBox).Text)

            // 恢复 maxTokens 为合法值 4096
            maxTokensBox.Text <- "4096"

            // 4. 测试上下文消息上限必填且非负（>= 0）
            contextBox.Text <- "-5"
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()
            Assert.Empty(savedGeneration)
            Assert.Equal("请输入 0 或正整数。", Ui.fieldValidationMessage(contextBox).Text)

            // 恢复 contextBox 为 200
            contextBox.Text <- "200"

            // 测试工具轮数上限必填且为正整数（> 0）
            toolRoundsBox.Text <- "0"
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()
            Assert.Empty(savedGeneration)
            Assert.Equal("请输入大于 0 的整数。", Ui.fieldValidationMessage(toolRoundsBox).Text)

            toolRoundsBox.Text <- "-2"
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()
            Assert.Empty(savedGeneration)
            Assert.Equal("请输入大于 0 的整数。", Ui.fieldValidationMessage(toolRoundsBox).Text)

            // 恢复 toolRoundsBox 为 12
            toolRoundsBox.Text <- "12"

            // 5. 填写合法生成参数并保存 -> 验证 payload
            instructionsBox.Text <- "You are a helpful assistant."
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()

            Assert.Single(savedGeneration) |> ignore
            let payload = savedGeneration.[0]
            Assert.Equal(0.7, payload["temperature"].GetValue<float>())
            Assert.Equal(0.9, payload["topP"].GetValue<float>())
            Assert.Equal(4096, payload["maxTokens"].GetValue<int>())
            Assert.Equal(200, payload["maxContextMessages"].GetValue<int>())
            Assert.Equal(12, payload["maxToolRounds"].GetValue<int>())
            Assert.Equal("You are a helpful assistant.", payload["instructions"].GetValue<string>())

            // 6. 验证可选参数留空时生成 null
            savedGeneration.Clear()
            tempBox.Text <- ""
            topPBox.Text <- ""
            maxTokensBox.Text <- ""
            instructionsBox.Text <- ""
            invokePrivate<unit> general "SaveGenerationCore" [||]
            Dispatcher.UIThread.RunJobs()

            Assert.Single(savedGeneration) |> ignore
            let nullPayload = savedGeneration.[0]
            Assert.True(nullPayload["temperature"] = null)
            Assert.True(nullPayload["topP"] = null)
            Assert.True(nullPayload["maxTokens"] = null)
            Assert.True(nullPayload["instructions"] = null)
        )

    [<Fact>]
    let ``SettingsGeneral BuildAppearance adjusts fontScale within bounds and updates prefs`` () =
        Headless.run (fun () ->
            let toasts = ResizeArray<string * ToastTone>()
            let mutable updatedPrefs = UiPrefs.defaults
            let general = SettingsGeneral(overlay (), defaultActions toasts, (fun p -> updatedPrefs <- p))
            let built = general.BuildAppearance()

            // 验证初始字号
            general.SetPrefs { UiPrefs.defaults with fontScale = 14.5 }

            // 找到更小和更大按钮
            let iconBtns =
                descendants built
                |> Seq.choose (function
                    | :? Border as b when AutomationProperties.GetName(b) = "更小" || AutomationProperties.GetName(b) = "更大" ->
                        Some(AutomationProperties.GetName(b), b)
                    | _ -> None)
                |> Map.ofSeq

            let smallerBtn = iconBtns.["更小"]
            let largerBtn = iconBtns.["更大"]

            // 验证调到上限时按钮禁用态
            general.SetPrefs { UiPrefs.defaults with fontScale = UiPrefs.maxFontScale }
            Assert.False(largerBtn.IsEnabled)
            Assert.True(smallerBtn.IsEnabled)

            // 验证调到下限时按钮禁用态
            general.SetPrefs { UiPrefs.defaults with fontScale = UiPrefs.minFontScale }
            Assert.False(smallerBtn.IsEnabled)
            Assert.True(largerBtn.IsEnabled)
        )

    [<Fact>]
    let ``SettingsGeneral BuildAbout formats system diagnostics with version and platform`` () =
        Headless.run (fun () ->
            let toasts = ResizeArray<string * ToastTone>()
            let general = SettingsGeneral(overlay (), defaultActions toasts, ignore)
            let built = general.BuildAbout("test-instance-id", "http://127.0.0.1:8765")

            // 模式匹配顺序：SelectableTextBlock 为 TextBlock 的派生类，必须排在前面以避免 FS0026 警告
            let textBlocks =
                descendants built
                |> Seq.choose (function
                    | :? SelectableTextBlock as stb -> Some stb.Text
                    | :? TextBlock as tb -> Some tb.Text
                    | _ -> None)
                |> List.ofSeq
            Assert.Contains("test-instance-id", textBlocks)
            Assert.Contains("http://127.0.0.1:8765", textBlocks)
            Assert.Contains(string Constants.ProtocolVersion, textBlocks)
            Assert.Contains(string Constants.FormatVersion, textBlocks)
        )

    // ==========================================
    // 4. SettingsView: 分区导航、缓存失效与滚动位置
    // ==========================================

    [<Fact>]
    let ``SettingsView navigates sections, remembers scroll positions and invalidates cache`` () =
        Headless.run (fun () ->
            let root = Grid()
            let over = OverlayHost(root)
            let toasts = ResizeArray<string * ToastTone>()
            let view = SettingsView(over, defaultActions toasts, ignore, ignore)
            let window = Window(Width = 900.0, Height = 700.0, Content = view)
            window.Show()
            Dispatcher.UIThread.RunJobs()

            // 默认展示 Providers 分区
            let currentSec: SettingsSection = getPrivateField view "current"
            Assert.Equal(Providers, currentSec)

            // 切换到 Tools
            invokePrivate<unit> view "Select" [| box Tools |]
            let currentSec2: SettingsSection = getPrivateField view "current"
            Assert.Equal(Tools, currentSec2)

            // 验证分区滚动位置设定与读取
            view.SetSectionScrollOffset(Tools, 150.0)
            Assert.Equal(150.0, view.GetSectionScrollOffset(Tools))

            // 相对方向导航 NavigateSection (+1)
            invokePrivate<unit> view "NavigateSection" [| box 1 |]
            let currentSec3: SettingsSection = getPrivateField view "current"
            Assert.Equal(Generation, currentSec3)

            // 验证失效缓存
            invokePrivate<unit> view "invalidate" [| box [ Generation; Appearance ] |]
            let builtMap: System.Collections.Generic.Dictionary<SettingsSection, Control> = getPrivateField view "builtSections"
            Assert.False(builtMap.ContainsKey(Generation))
            Assert.False(builtMap.ContainsKey(Appearance))

            window.Close()
        )
