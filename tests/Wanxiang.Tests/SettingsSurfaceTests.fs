module Wanxiang.Tests.SettingsSurfaceTests

open System
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
open Wanxiang.Tests

/// 设置与对话框呈现面的回归测试。
///
/// 覆盖本轮修正的行为契约：
/// - 启用开关行统一走 Ui.switchRow（行最小高 / ItemStatus / 读写语义）；
/// - 表单校验反馈统一走 Ui.applyValidationFeedback（单错 / 多错 / 清零三种节律）；
/// - 状态 tag 语气分档（Warning / Neutral）；
/// - 已停用卡只弱化文字列，操作按钮保持可用；
/// - About 区不再有语义错位的 spacer；
/// - 双列表单列距与多行输入最小高走共享常量；
/// - 持久化回调失败时给出 Failure 档 toast，不再静默。

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

let private firstTextBlock (root: Control) (text: string) =
    descendants root
    |> Seq.tryPick (function
        | :? TextBlock as block when block.Text = text -> Some block
        | _ -> None)

let private iconButtons (root: Control) =
    descendants root
    |> Seq.choose (function
        | :? Border as border when border.Width = Tokens.iconButton && border.MinHeight = Tokens.iconButton -> Some border
        | _ -> None)

let private overlay () = OverlayHost(Grid())

let private stubActions
    (toasts: ResizeArray<string * ToastTone>)
    (updateGeneration: JsonObject -> (bool -> unit) -> unit)
    : SettingsActions =
    { upsertProvider = fun _ completed -> completed true
      deleteProvider = ignore
      probeProvider = ignore
      upsertMcp = fun _ completed -> completed true
      deleteMcp = ignore
      updateGeneration = updateGeneration
      savePrefs = ignore
      toast = fun message tone -> toasts.Add(message, tone) }

[<Fact>]
let ``switch row consumes the shared settings row height and toggle semantics`` () =
    Headless.ensure ()
    let row, read, write = Ui.switchRow "测试开关" "用于验证开关行契约。" true ignore
    Assert.Equal(ControlMetrics.settingsRowMinHeight, row.MinHeight, 3)
    Assert.Equal(ControlMetrics.rowMinHeight, ControlMetrics.settingsRowMinHeight, 3)
    Assert.Equal("开启", AutomationProperties.GetItemStatus(row))
    Assert.True(read ())
    write false
    Assert.False(read ())
    Assert.Equal("关闭", AutomationProperties.GetItemStatus(row))
    Assert.Equal(Tokens.radiusMd, (row :?> Border).CornerRadius.TopLeft, 3)

[<Fact>]
let ``settings row and text area metrics keep single sources`` () =
    Assert.Equal(ControlMetrics.rowMinHeight, ControlMetrics.settingsRowMinHeight, 3)
    Assert.True(ControlMetrics.textAreaMinHeight > 0.0)
    Assert.True(ControlMetrics.textAreaLongMinHeight > ControlMetrics.textAreaMinHeight)

[<Fact>]
let ``validation feedback with a single error toasts the message and hides the summary`` () =
    Headless.ensure ()
    let box = TextBox()
    let summary = TextBlock(IsVisible = true, Focusable = true)
    let toasts = ResizeArray<string>()
    Ui.applyValidationFeedback [ box, "内容不能为空。" ] (Some summary) (fun message -> toasts.Add message)
    Assert.Equal<string list>([ "内容不能为空。" ], List.ofSeq toasts)
    Assert.False(summary.IsVisible)

[<Fact>]
let ``validation feedback with multiple errors fills the focusable summary`` () =
    Headless.ensure ()
    let first = TextBox()
    let second = TextBox()
    let summary = TextBlock(IsVisible = false, Focusable = true)
    let toasts = ResizeArray<string>()
    Ui.applyValidationFeedback [ first, "甲错"; second, "乙错" ] (Some summary) (fun message -> toasts.Add message)
    Assert.True(summary.IsVisible)
    Assert.Equal("有 2 处需要修正：甲错；乙错", summary.Text)
    Assert.Equal<string list>([ "有 2 处需要修正，请查看表单顶部摘要。" ], List.ofSeq toasts)

[<Fact>]
let ``validation feedback with no errors stays silent and hides the summary`` () =
    // Ui 模块顶层建有 Cursor（handCursor）：触碰其任何成员前必须先 ensure，
    // 否则模块静态构造在无 ICursorFactory 的进程里直接炸。
    Headless.ensure ()
    let summary = TextBlock(IsVisible = true)
    let toasts = ResizeArray<string>()
    Ui.applyValidationFeedback [] (Some summary) (fun message -> toasts.Add message)
    Assert.False(summary.IsVisible)
    Assert.Equal(0, toasts.Count)

[<Fact>]
let ``warning tag tone uses the warning brush while neutral stays muted`` () =
    Headless.ensure ()
    let warning = Ui.tagWith Ui.TagTone.Warning "缺少密钥"
    let neutral = Ui.tag "已配置"
    let warningBorder = warning
    let neutralBorder = neutral
    Assert.Equal(Tokens.warning.Color, (warningBorder.BorderBrush :?> SolidColorBrush).Color)
    Assert.Equal(Tokens.warning.Color, ((warningBorder.Child :?> TextBlock).Foreground :?> SolidColorBrush).Color)
    Assert.Equal(Tokens.border.Color, (neutralBorder.BorderBrush :?> SolidColorBrush).Color)
    Assert.Equal(Tokens.textMuted.Color, ((neutralBorder.Child :?> TextBlock).Foreground :?> SolidColorBrush).Color)

[<Fact>]
let ``generation form consumes shared column spacing and text metrics`` () =
    Headless.ensure ()
    let toasts = ResizeArray<string * ToastTone>()
    let general = SettingsGeneral(overlay (), stubActions toasts (fun _ completed -> completed true), ignore)
    let built = general.BuildGeneration()
    let grid =
        descendants built
        |> Seq.find (function
            | :? Grid as candidate -> candidate.ColumnDefinitions.Count = 2
            | _ -> false)
    Assert.Equal(Tokens.space4, (grid :?> Grid).ColumnSpacing, 3)
    let instructionBox =
        descendants built
        |> Seq.tryPick (function
            | :? TextBox as box when box.AcceptsReturn -> Some box
            | _ -> None)
        |> Option.defaultWith (fun () -> failwith "生成表单缺少多行指令输入框")
    Assert.Equal(ControlMetrics.textAreaMinHeight, instructionBox.MinHeight, 3)
    let switchRows =
        descendants built
        |> Seq.choose (function
            | :? Border as border when border.MinHeight = ControlMetrics.settingsRowMinHeight -> Some border
            | _ -> None)
        |> Seq.length
    Assert.True(switchRows >= 1, "生成表单至少有一个开关行走共享行最小高")

// 生成设置是设置区唯一不认 Ctrl+Enter 的表单：本地其余表单（服务商 / MCP / 会话参数）
// 全部「Ctrl/⌘+Enter 保存」。补上同样的行级 KeyDown，并把键位写进按钮的无障碍说明，
// 用户不必先到处试键。锁：Ctrl+Enter 真触发保存，裸 Enter 不触发（归 Ui.onClick）。
// 生成设置此前是设置区唯一不认 Ctrl+Enter 的表单：本地其余表单（服务商 / MCP / 会话参数）
// 全部「Ctrl/⌘+Enter 保存」。按钮的无障碍说明现在把键位写清楚，不必靠试。
// 键盘路由本身的验证不在此处：合成按键在无头窗口里不会从焦点控件冒泡到祖先
// （同一原因，SettingsProviders / SettingsTools 的行级 KeyDown 也没有单测），
// 这里只锁用户可观察的那一半——按钮自描述里的键位。
// 生成参数此前的提交时机只有 Enter / Ctrl+Enter / 保存按钮三条：改完数值直接点去
// 别处（切分区、点窗口其它区域），那次编辑既不入库也不提示，切回来被 SetCatalog
// 的旧值覆盖——静默回滚。kelivo display_settings_page.dart:1736-1755 的 FocusNode
// listener 失焦即落库。锁：数值框失焦真的触发 updateGeneration。
[<Fact>]
let ``editing a generation number and moving away commits it`` () =
    Headless.ensure ()
    let toasts = ResizeArray<string * ToastTone>()
    let saved = ResizeArray<JsonObject>()
    let general =
        SettingsGeneral(overlay (), stubActions toasts (fun payload completed ->
            saved.Add payload
            completed true), ignore)
    let built = general.BuildGeneration()
    let window = Window(Width = 760.0, Height = 900.0, Content = built)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    try
        // 按占位文案锁定「上下文消息上限」框：五个数值框里只有它的占位是 "200"。
        let contextBox =
            descendants built
            |> Seq.choose (function :? TextBox as box -> Some box | _ -> None)
            |> Seq.find (fun box -> box.PlaceholderText = "200")
        Assert.Equal(0, saved.Count)
        // 表单未接 SetCatalog：五个框都是空文本，必需框（上下文/工具轮数）空即校验失败。
        // 先把整表填成合法默认值，再单独改要测的那一格。
        for box in
            descendants built
            |> Seq.choose (function :? TextBox as box -> Some box | _ -> None) do
            if isNull box.PlaceholderText || not (box.AcceptsReturn) then
                match box.PlaceholderText with
                | "200" -> box.Text <- "200"
                | "12" -> box.Text <- "12"
                | _ -> box.Text <- ""
        contextBox.Focus() |> ignore
        Assert.True(contextBox.IsFocused, "前置条件：焦点已在上下文框")
        contextBox.Text <- "321"
        // 把焦点移到另一个数值框：LostFocus 在这一次转移上触发。
        let temperatureBox =
            descendants built
            |> Seq.choose (function :? TextBox as box -> Some box | _ -> None)
            |> Seq.find (fun box -> not (obj.ReferenceEquals(box, contextBox)))
        temperatureBox.Focus() |> ignore
        Assert.False(contextBox.IsFocused, "前置条件：焦点已离开上下文框")
        Dispatcher.UIThread.RunJobs()
        Assert.True(saved.Count > 0, "失焦即提交：改完上下文上限点去别处必须落库")
        let payload = saved.[saved.Count - 1]
        Assert.Equal(321, int (payload.["maxContextMessages"].GetValue<int>()))
    finally
        window.Close()

[<Fact>]
let ``generation save button advertises the ctrl enter shortcut`` () =
    Headless.ensure ()
    let toasts = ResizeArray<string * ToastTone>()
    let general = SettingsGeneral(overlay (), stubActions toasts (fun _ completed -> completed true), ignore)
    let built = general.BuildGeneration()
    // 按无障碍名找按钮（Ui.button 返回的是 Border，不是 Button）：
    // 单测必须由用户可观察的标识定位，不靠控件类型猜。
    let saveButton =
        descendants built
        |> Seq.find (fun c -> AutomationProperties.GetName c = "保存生成设置")
        :?> Border
    Assert.Equal<string>("保存生成设置 (Ctrl+Enter)", AutomationProperties.GetHelpText saveButton)
    Assert.Equal<string>("保存生成设置 (Ctrl+Enter)", ToolTip.GetTip saveButton :?> string)

[<Fact>]
let ``about section drops the misplaced spacer`` () =
    Headless.ensure ()
    let toasts = ResizeArray<string * ToastTone>()
    let general = SettingsGeneral(overlay (), stubActions toasts (fun _ completed -> completed true), ignore)
    let built = general.BuildAbout("instance-1", "ws://127.0.0.1:8765/ws")
    let misplacedSpacers =
        descendants built
        |> Seq.filter (function
            | :? Border as border -> border.Height = Tokens.space2
            | _ -> false)
        |> Seq.length
    Assert.Equal(0, misplacedSpacers)
    let hairlines =
        descendants built
        |> Seq.filter (function
            | :? Border as border -> border.Height = 1.0 && not (isNull border.Background) && border.Background = Tokens.borderSoft
            | _ -> false)
        |> Seq.length
    Assert.True(hairlines >= 1, "关于区应在信息行与操作按钮之间保留一条发丝线")

[<Fact>]
let ``disabled mcp card dims its text while keeping row actions alive`` () =
    Headless.ensure ()
    let overlayHost = overlay ()
    let toasts = ResizeArray<string * ToastTone>()
    let tools = SettingsTools(overlayHost, stubActions toasts (fun _ completed -> completed true))
    let server: McpInfo =
        { id = "filesystem"
          label = "文件系统"
          command = Some "npx"
          args = [ "-y"; "server" ]
          url = None
          callTimeoutSeconds = 60
          enabled = false }
    tools.SetCatalog { Catalog.empty with mcpServers = [ server ] }
    let built = tools.Build()
    let nameBlock =
        firstTextBlock built "文件系统"
        |> Option.defaultWith (fun () -> failwith "MCP 卡缺少名称文本")
    Assert.Same(Tokens.textMuted, nameBlock.Foreground)
    let rowActionButtons = iconButtons built |> Seq.toList
    Assert.True(rowActionButtons.Length >= 2, "已停用 MCP 卡仍应暴露编辑与更多按钮")
    Assert.All(rowActionButtons, fun button -> Assert.True(button.IsEnabled && button.IsHitTestVisible))

[<Fact>]
let ``missing key reads as warning tag and disabled provider dims its text`` () =
    Headless.ensure ()
    let overlayHost = overlay ()
    let toasts = ResizeArray<string * ToastTone>()
    let providers = SettingsProviders(overlayHost, stubActions toasts (fun _ completed -> completed true))
    let needsKey: ProviderInfo =
        { id = "openai"
          label = "OpenAI"
          kind = "openai"
          baseUrl = "https://api.openai.com/v1"
          hasApiKey = false
          models = [ "gpt-4o" ]
          defaultModel = "gpt-4o"
          timeoutSeconds = 120
          maxRetries = 2
          enabled = true }
    let disabled = { needsKey with id = "local"; label = "本地推理"; hasApiKey = true; enabled = false }
    providers.SetCatalog { Catalog.empty with providers = [ needsKey; disabled ] }
    let built = providers.Build()
    let warningTag =
        descendants built
        |> Seq.tryFind (function
            | :? Border as border ->
                match border.Child with
                | :? TextBlock as text -> text.Text = "缺少密钥"
                | _ -> false
            | _ -> false)
        |> Option.defaultWith (fun () -> failwith "缺少密钥状态缺少 Warning tag")
    Assert.Equal(Tokens.warning.Color, ((warningTag :?> Border).BorderBrush :?> SolidColorBrush).Color)
    let mutedName =
        firstTextBlock built "本地推理"
        |> Option.defaultWith (fun () -> failwith "已停用服务商卡缺少名称文本")
    Assert.Same(Tokens.textMuted, mutedName.Foreground)
    let enabledName =
        firstTextBlock built "OpenAI"
        |> Option.defaultWith (fun () -> failwith "启用中服务商卡缺少名称文本")
    Assert.Same(Tokens.text, enabledName.Foreground)
    let rowActionButtons = iconButtons built |> Seq.toList
    Assert.True(rowActionButtons.Length >= 4, "每个服务商行都应保留编辑与更多按钮")
    Assert.All(rowActionButtons, fun button -> Assert.True(button.IsEnabled && button.IsHitTestVisible))

[<Fact>]
let ``generation save failure surfaces a failure toast instead of silence`` () =
    Headless.ensure ()
    let toasts = ResizeArray<string * ToastTone>()
    let general =
        SettingsGeneral(overlay (), stubActions toasts (fun _ completed -> completed false), ignore)
    let built = general.BuildGeneration()
    general.SetCatalog Catalog.empty
    let saveButton =
        tryByAutomationName built "保存生成设置"
        |> Option.defaultWith (fun () -> failwith "生成表单缺少保存按钮")
    saveButton.RaiseEvent(
        KeyEventArgs(
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter))
    Assert.True(
        toasts
        |> Seq.exists (fun (message, tone) -> message = "保存失败，请重试。" && tone = ToastTone.Failure),
        "保存失败必须给出 Failure 档提示，不能静默。")
    Assert.Equal("保存生成设置", AutomationProperties.GetName(saveButton))

[<Fact>]
let ``settings window title dominates section headings`` () =
    Headless.ensure ()
    let root = Grid()
    let overlayHost = OverlayHost(root)
    let actions: SettingsActions =
        { upsertProvider = fun _ cb -> cb true
          deleteProvider = ignore
          probeProvider = ignore
          upsertMcp = fun _ cb -> cb true
          deleteMcp = ignore
          updateGeneration = fun _ cb -> cb true
          savePrefs = ignore
          toast = fun _ _ -> () }
    let settings = SettingsView(overlayHost, actions, ignore, ignore)
    settings.Build()
    let windowTitle =
        firstTextBlock settings "设置"
        |> Option.defaultWith (fun () -> failwith "设置缺少窗口标题")
    let sectionHeading =
        firstTextBlock settings "服务商"
        |> Option.defaultWith (fun () -> failwith "分区缺少小节标题")
    // 窗口（页面主标题）应不小于小节标题，消除「标题 < 小节标题」的层级倒挂。
    Assert.True(windowTitle.FontSize >= sectionHeading.FontSize, "窗口标题应不小于小节标题")
    Assert.True(windowTitle.FontSize >= Tokens.fontHeading, "窗口标题应达到区块标题档（Ui.heading = fontHeading）")

[<Fact>]
let ``appearance and about consume new metrics tokens and content-tier dividers`` () =
    Headless.ensure ()
    let toasts = ResizeArray<string * ToastTone>()
    let general = SettingsGeneral(overlay (), stubActions toasts (fun _ completed -> completed true), ignore)
    // 字号槽宽走 token（原裸 56.0）
    let appearance = general.BuildAppearance()
    let fontSizeValue =
        descendants appearance
        |> Seq.tryPick (function
            | :? TextBlock as block when not (isNull block.Text) && block.Text.Contains "pt" -> Some block
            | _ -> None)
        |> Option.defaultWith (fun () -> failwith "外观页缺少字号值文本")
    Assert.Equal(ControlMetrics.fontSizeValueMinWidth, fontSizeValue.MinWidth, 3)
    // 「关于」键名列槽宽走 token（原裸 110.0）
    let about = general.BuildAbout("instance-1", "ws://127.0.0.1:8765/ws")
    let aboutKey =
        firstTextBlock about "协议版本"
        |> Option.defaultWith (fun () -> failwith "关于页缺少键名列文本")
    Assert.Equal(ControlMetrics.aboutKeyMinWidth, aboutKey.MinWidth, 3)
    // 面板内发丝分隔线统一走内容档 borderSoft，内容区不再混用更轻的 chrome 档 Tokens.hairline
    let chromeTierDividers =
        descendants appearance
        |> Seq.filter (function
            | :? Border as border ->
                border.Height = ControlMetrics.borderWidth
                && not (isNull border.Background)
                && border.Background = Tokens.hairline
            | _ -> false)
        |> Seq.length
    Assert.Equal(0, chromeTierDividers)

[<Fact>]
let ``built-in tool row title shares the row-title body font`` () =
    Headless.ensure ()
    let overlayHost = overlay ()
    let toasts = ResizeArray<string * ToastTone>()
    let tools = SettingsTools(overlayHost, stubActions toasts (fun _ completed -> completed true))
    let tool: ToolInfo =
        { id = "read_file"; label = "读取文件"; description = "读取沙箱内文件"; source = "builtin"; serverId = None }
    tools.SetCatalog { Catalog.empty with tools = [ tool ] }
    let built = tools.Build()
    let nameBlock =
        firstTextBlock built "读取文件"
        |> Option.defaultWith (fun () -> failwith "内置工具行缺少名称文本")
    // 与 MCP 行名、服务商行名同一「行标题」角色，统一 fontBody（此前本地用 fontSmall）。
    Assert.Equal(Tokens.fontBody, nameBlock.FontSize, 3)

[<Fact>]
let ``generation form fields sit inside a grouping card`` () =
    // Generation 面板的数值表单、指令框与开关行收进分组卡（与 Appearance / About 同档的
    // 面板卡：surfaceContainer + hairline + 1px），不再裸露成 vstack。判定特征与
    // PresentationRefreshTests 对 BuildAppearance groupingCards >= 1 的既有断法同一套。
    Headless.ensure ()
    let toasts = ResizeArray<string * ToastTone>()
    let general = SettingsGeneral(overlay (), stubActions toasts (fun _ completed -> completed true), ignore)
    let built = general.BuildGeneration()
    let groupingCards =
        descendants built
        |> Seq.choose (function
            | :? Border as border -> Some border
            | _ -> None)
        |> Seq.filter (fun border ->
            Object.ReferenceEquals(border.Background, Tokens.surfaceContainer)
            && Object.ReferenceEquals(border.BorderBrush, Tokens.hairline)
            && border.BorderThickness = Thickness 1.0)
        |> Seq.toList
    Assert.True(List.length groupingCards >= 1, "生成表单字段应收进至少一张 Ui.groupingCard 分组卡")

