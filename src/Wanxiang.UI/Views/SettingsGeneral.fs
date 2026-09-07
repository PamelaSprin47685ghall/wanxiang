namespace Wanxiang.UI

open System
open System.Globalization
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Wanxiang.Core

/// 生成默认值、外观偏好与关于信息。
///
/// 生成参数落到服务端配置（新会话继承）；外观偏好只存本机
/// （Q195：窗口尺寸与主题是客户端偏好，不进业务 TOML、不经 NDJSON）。
type SettingsGeneral(overlay: OverlayHost, actions: SettingsActions, onPrefsChanged: UiPrefs -> unit) =

    let mutable catalog = Catalog.empty
    let mutable prefs = UiPrefs.defaults

    let temperatureBox = snd (Ui.textField "留空 = 使用服务商默认")
    let topPBox = snd (Ui.textField "0–1，留空不设")
    let maxTokensBox = snd (Ui.textField "留空不限制")
    let contextBox = snd (Ui.textField "200")
    let toolRoundsBox = snd (Ui.textField "12")
    let instructionsBox = snd (Ui.textArea "新会话的默认系统指令（可留空）" 96.0)

    let mutable readAutoTitle: unit -> bool = fun () -> true
    let mutable setAutoTitle: bool -> unit = ignore
    let mutable setGenerationPending: bool -> unit = ignore
    /// 生成保存往返中的重入 guard：按钮已禁用，但重复调用仍能进 Save。
    let mutable generationSaving = false
    let mutable syncAppearance: UiPrefs -> unit = ignore

    let floatText (value: float option) =
        match value with
        | Some v -> v.ToString("0.##", CultureInfo.InvariantCulture)
        | None -> ""

    let parseFloatOpt (raw: string) =
        if String.IsNullOrWhiteSpace raw then None
        else
            match Double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, v -> Some v
            | _ -> None

    let parseIntOpt (raw: string) =
        if String.IsNullOrWhiteSpace raw then None
        else
            match Int32.TryParse(raw.Trim()) with
            | true, v -> Some v
            | _ -> None

    let switchRow (title: string) (hint: string) (initial: bool) (onChanged: bool -> unit) : Control * (unit -> bool) * (bool -> unit) =
        let toggle, read, write, flip = Ui.toggle initial onChanged
        // 一行只有一个 Tab 停靠点：内层开关不进 Tab 序，行容器统一代理键盘，
        // Enter/Space 只走 Ui.onClick 一次，单次翻转不变。鼠标点开关本身仍有效。
        toggle.Focusable <- false
        Avalonia.Automation.AutomationProperties.SetName(toggle, title)
        Avalonia.Automation.AutomationProperties.SetHelpText(toggle, hint)
        let caption = Ui.label title
        let note = Ui.caption hint
        let column = Ui.vstack Tokens.space1 [ caption :> Control; note :> Control ]
        let dock = DockPanel(LastChildFill = true)
        DockPanel.SetDock(toggle, Dock.Right)
        dock.Children.Add toggle
        dock.Children.Add column
        let row =
            ActionBorder(
                Padding = Thickness(Tokens.space2, Tokens.space2),
                CornerRadius = CornerRadius Tokens.radiusMd,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = true,
                MinHeight = 44.0,
                Child = dock)
        Avalonia.Automation.AutomationProperties.SetName(row, title)
        Avalonia.Automation.AutomationProperties.SetHelpText(row, hint)
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
            row,
            Nullable Avalonia.Automation.Peers.AutomationControlType.CheckBox)
        let updateRowBackground () =
            row.Background <- if row.IsPointerOver || row.IsFocused then Tokens.hover :> IBrush else Brushes.Transparent :> IBrush
        row.PointerEntered.Add(fun _ -> updateRowBackground ())
        row.PointerExited.Add(fun _ -> updateRowBackground ())
        row.GotFocus.Add(fun _ -> updateRowBackground ())
        row.LostFocus.Add(fun _ -> updateRowBackground ())
        let toggleAndRefresh () =
            flip ()
            updateRowBackground ()
            let currentState = if read () then "开启" else "关闭"
            Avalonia.Automation.AutomationProperties.SetItemStatus(row, currentState)
        Avalonia.Automation.AutomationProperties.SetItemStatus(row, if initial then "开启" else "关闭")
        // Enter/Space 由 Ui.onClick 拥有：这里再绑一次会翻转两次，键盘用户将永远打不开开关。
        Ui.onClick row toggleAndRefresh
        let syncWrite v =
            write v
            Avalonia.Automation.AutomationProperties.SetItemStatus(row, if v then "开启" else "关闭")
        row :> Control, read, syncWrite

    member this.SetCatalog(next: Catalog) =
        catalog <- next
        let g = next.generation
        temperatureBox.Text <- floatText g.temperature
        topPBox.Text <- floatText g.topP
        maxTokensBox.Text <- (match g.maxTokens with Some v -> string v | None -> "")
        contextBox.Text <- string g.maxContextMessages
        toolRoundsBox.Text <- string g.maxToolRounds
        instructionsBox.Text <- g.instructions |> Option.defaultValue ""
        setAutoTitle g.autoTitle

    member this.SetPrefs(next: UiPrefs) =
        prefs <- next
        syncAppearance next

    member private this.SaveGenerationCore() =
        let fields = [ temperatureBox; topPBox; maxTokensBox; contextBox; toolRoundsBox ]
        for box in fields do Ui.clearFieldError box
        let mutable firstInvalid: TextBox option = None
        let fail box message =
            Ui.setFieldError box message
            if firstInvalid.IsNone then firstInvalid <- Some box
        let raw (box: TextBox) = if isNull box.Text then "" else box.Text.Trim()
        let parseOptionalFloat box valid message =
            let text = raw box
            if String.IsNullOrWhiteSpace text then None
            else
                match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, value when valid value -> Some value
                | _ ->
                    fail box message
                    None
        let parseOptionalPositiveInt box =
            let text = raw box
            if String.IsNullOrWhiteSpace text then None
            else
                match Int32.TryParse text with
                | true, value when value > 0 -> Some value
                | _ ->
                    fail box "请输入大于 0 的整数，或留空。"
                    None
        let parseRequiredInt box valid message =
            match Int32.TryParse(raw box) with
            | true, value when valid value -> Some value
            | _ ->
                fail box message
                None

        let temperature =
            parseOptionalFloat temperatureBox (fun value -> value >= 0.0 && value <= 2.0) "请输入 0–2 之间的数字，或留空。"
        let topP =
            parseOptionalFloat topPBox (fun value -> value >= 0.0 && value <= 1.0) "请输入 0 到 1 之间的数字，或留空。"
        let maxTokens = parseOptionalPositiveInt maxTokensBox
        let contextMessages = parseRequiredInt contextBox (fun value -> value >= 0) "请输入 0 或正整数。"
        let toolRounds = parseRequiredInt toolRoundsBox (fun value -> value > 0) "请输入大于 0 的整数。"

        match firstInvalid with
        | Some box ->
            let errorMsg = (Ui.fieldValidationMessage box).Text
            if not (String.IsNullOrWhiteSpace errorMsg) then
                actions.toast errorMsg Warning
            // 错误行只展开自己所在的组（hint 与 error 互换），边框粗细不变；
            // 这里只把焦点送到第一个错误处，不碰兄弟。
            box.BringIntoView()
            box.Focus(NavigationMethod.Directional) |> ignore
            Dispatcher.UIThread.Post(fun () ->
                if box.IsEffectivelyVisible && box.IsEnabled then
                    box.BringIntoView()
                    box.Focus(NavigationMethod.Directional) |> ignore)
        | None ->
            let payload = JsonObject()
            match temperature with Some v -> payload["temperature"] <- v | None -> payload["temperature"] <- null
            match topP with Some v -> payload["topP"] <- v | None -> payload["topP"] <- null
            match maxTokens with Some v -> payload["maxTokens"] <- v | None -> payload["maxTokens"] <- null
            let instructions = if isNull instructionsBox.Text then "" else instructionsBox.Text.Trim()
            payload["instructions"] <- if String.IsNullOrWhiteSpace instructions then null else JsonNode.op_Implicit instructions
            payload["maxContextMessages"] <- contextMessages.Value
            payload["maxToolRounds"] <- toolRounds.Value
            payload["autoTitle"] <- readAutoTitle ()
            setGenerationPending true
            actions.updateGeneration payload (fun _ -> setGenerationPending false)

    /// pending 中的保存直接返回，不做二次提交。
    member private this.SaveGeneration() =
        if generationSaving then () else this.SaveGenerationCore()

    member this.BuildGeneration() : Control =
        let autoTitleRow, readAuto, writeAuto =
            switchRow "自动生成会话标题" "首轮对话后用同一个模型起一个短标题；失败时回落到你的第一句话。" true ignore
        readAutoTitle <- readAuto
        setAutoTitle <- writeAuto
        let saveButton = Ui.button Ui.Primary "保存生成设置" (fun () -> this.SaveGeneration())
        Ui.preparePendingButton saveButton
        saveButton.HorizontalAlignment <- HorizontalAlignment.Left
        setGenerationPending <- fun pending ->
            generationSaving <- pending
            Ui.setButtonPending saveButton pending "保存生成设置" "正在保存…"
        let grid = Grid(ColumnSpacing = Tokens.space4, RowSpacing = Tokens.space3)
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        for _ in 1 .. 5 do grid.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        let temperatureField = Ui.inputFieldGroup "Temperature" "越高越发散。留空则用服务商默认值。" temperatureBox
        let topPField = Ui.inputFieldGroup "Top P" "核采样阈值，0–1。" topPBox
        let maxTokensField = Ui.inputFieldGroup "最大输出 token" "限制单次回复长度。" maxTokensBox
        let contextField = Ui.inputFieldGroup "上下文消息上限" "每次请求最多携带多少条历史，防止长会话撞上模型上限。" contextBox
        let toolRoundsField = Ui.inputFieldGroup "工具调用轮数上限" "模型连续调用工具超过这个轮数就中止本次生成。" toolRoundsBox
        for control in [ temperatureField; topPField; maxTokensField; contextField; toolRoundsField ] do grid.Children.Add control
        let applyGridLayout width =
            let single = width > 0.0 && width < LayoutPolicy.formSingleColumnBreakpoint
            grid.ColumnDefinitions[1].Width <-
                if single then GridLength(0.0) else GridLength(1.0, GridUnitType.Star)
            let place (control: Control) row column =
                Grid.SetRow(control, row)
                Grid.SetColumn(control, column)
            if single then
                [ temperatureField; topPField; maxTokensField; contextField; toolRoundsField ]
                |> List.iteri (fun row control -> place control row 0)
            else
                place temperatureField 0 0
                place topPField 0 1
                place maxTokensField 1 0
                place contextField 1 1
                place toolRoundsField 2 0
        grid.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then applyGridLayout grid.Bounds.Width)
        applyGridLayout grid.Bounds.Width
        Ui.vstack
            Tokens.space6
            [ Ui.vstack
                  Tokens.space1
                  [ Ui.heading "生成" :> Control
                    Ui.caption "这些是新会话的默认值。单个会话可以在会话设置里单独调整。" :> Control ]
              :> Control
              grid :> Control
              Ui.controlFieldGroup "默认系统指令" "会作为 system 消息随每次请求发送。" (instructionsBox.Parent :?> Control)
              autoTitleRow
              Ui.hairline () :> Control
              saveButton :> Control ]
        :> Control

    member this.BuildAppearance() : Control =
        let themeButton, setThemeText =
            Menu.selectButton
                overlay
                (ThemePreference.label prefs.theme)
                (fun () ->
                    [ "主题",
                      [ FollowSystem; AlwaysLight; AlwaysDark ]
                      |> List.map (fun option ->
                          MenuEntry.create (ThemePreference.label option) (fun () ->
                              prefs <- { prefs with theme = option }
                              onPrefsChanged prefs)
                          |> MenuEntry.markSelected (prefs.theme = option)) ])
        themeButton.HorizontalAlignment <- HorizontalAlignment.Left
        Tokens.Changed.Publish.Add(fun _ -> setThemeText (ThemePreference.label prefs.theme))

        let fontSizeCaption =
            TextBlock(
                Text = sprintf "%.1f pt" prefs.fontScale,
                FontSize = Tokens.fontSmall,
                Foreground = Tokens.textMuted,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 56.0)
        let adjust (delta: float) =
            let next = Math.Clamp(prefs.fontScale + delta, UiPrefs.minFontScale, UiPrefs.maxFontScale)
            if abs (next - prefs.fontScale) > 0.01 then
                prefs <- { prefs with fontScale = next }
                fontSizeCaption.Text <- sprintf "%.1f pt" next
                onPrefsChanged prefs
        let smaller = Ui.iconButton Icons.minus "更小"
        Ui.onClick smaller (fun () -> adjust -0.5)
        let larger = Ui.iconButton Icons.plus "更大"
        Ui.onClick larger (fun () -> adjust 0.5)
        let fontRow =
            let controls = Ui.hstack Tokens.space2 [ smaller :> Control; fontSizeCaption :> Control; larger :> Control ]
            let column = Ui.vstack Tokens.space1 [ Ui.label "消息字号" :> Control; Ui.caption "影响对话正文与代码块。" :> Control ]
            let dock = DockPanel(LastChildFill = true)
            DockPanel.SetDock(controls, Dock.Right)
            dock.Children.Add controls
            dock.Children.Add column
            Border(
                Padding = Thickness(Tokens.space2, Tokens.space2),
                MinHeight = 44.0,
                Child = dock)
            :> Control

        let enterRow, _, setEnterSends =
            switchRow "Enter 直接发送" "关闭后用 Ctrl+Enter 发送，Enter 换行。" prefs.enterSends (fun value ->
                prefs <- { prefs with enterSends = value }
                onPrefsChanged prefs)
        let collapseRow, _, setCollapseReasoning =
            switchRow "完成后收起思考过程" "生成结束时自动折叠模型的思维链。" prefs.autoCollapseReasoning (fun value ->
                prefs <- { prefs with autoCollapseReasoning = value }
                onPrefsChanged prefs)
        let codeWrapRow, _, setCodeWrap =
            switchRow "代码块长行折行" "关闭时长行横向滚动；开启后折行显示，一屏读完。" prefs.codeWrap (fun value ->
                prefs <- { prefs with codeWrap = value }
                onPrefsChanged prefs)
        let archivedRow, _, setArchived =
            switchRow "在侧栏显示已归档会话" "归档会话默认收起，避免长列表。" prefs.showArchived (fun value ->
                prefs <- { prefs with showArchived = value }
                onPrefsChanged prefs)
        let motionRow, _, setReduceMotion =
            switchRow "减少动态效果" "停止加载指示器等持续旋转；状态变化仍会即时显示。" prefs.reduceMotion (fun value ->
                prefs <- { prefs with reduceMotion = value }
                onPrefsChanged prefs)

        syncAppearance <- fun next ->
            setThemeText (ThemePreference.label next.theme)
            fontSizeCaption.Text <- sprintf "%.1f pt" next.fontScale
            setEnterSends next.enterSends
            setCollapseReasoning next.autoCollapseReasoning
            setCodeWrap next.codeWrap
            setArchived next.showArchived
            setReduceMotion next.reduceMotion
        syncAppearance prefs

        Ui.vstack
            Tokens.space6
            [ Ui.vstack
                  Tokens.space1
                  [ Ui.heading "外观与交互" :> Control
                    Ui.caption "这些偏好只保存在本机，不会同步到服务端。" :> Control ]
              :> Control
              Ui.controlFieldGroup "主题" "深色主题是同一套纸感在低光下的版本。" (themeButton :> Control)
              fontRow
              Ui.hairline () :> Control
              enterRow
              collapseRow
              codeWrapRow
              archivedRow
              motionRow ]
        :> Control

    member this.BuildAbout(instanceId: string, serverUrl: string) : Control =
        let row (key: string) (value: string) : Control =
            let k = TextBlock(Text = key, FontSize = Tokens.fontSmall, Foreground = Tokens.textMuted, MinWidth = 110.0)
            let v =
                SelectableTextBlock(
                    Text = value,
                    FontSize = Tokens.fontSmall,
                    FontFamily = Tokens.monoFontFamily,
                    Foreground = Tokens.text,
                    TextWrapping = TextWrapping.Wrap,
                    SelectionBrush = Tokens.accentSoft)
            Ui.hstack Tokens.space3 [ k :> Control; v :> Control ] :> Control
        let platformDesc = if OperatingSystem.IsBrowser() then "WebAssembly (PWA)" else sprintf "Desktop (.NET %s)" (Environment.Version.ToString())
        let osDesc = Environment.OSVersion.ToString()
        let copyDiagButton = Ui.button Ui.Secondary "复制诊断报告" (fun () ->
            let diag =
                sprintf "万象系统诊断报告\n- 协议版本: %d\n- 日志格式: %d\n- 运行环境: %s\n- 操作系统: %s\n- 实例标识: %s\n- 连接端点: %s\n- 报告时间: %s"
                    Constants.ProtocolVersion
                    Constants.FormatVersion
                    platformDesc
                    osDesc
                    (if String.IsNullOrWhiteSpace instanceId then "未连接" else instanceId)
                    (if String.IsNullOrWhiteSpace serverUrl then "未连接" else serverUrl)
                    (DateTimeOffset.UtcNow.ToString("O"))
            // 先解析剪贴板：拿不到就直说失败，成功 toast 只在复制完成后发。
            match TopLevel.GetTopLevel overlay.Root with
            | null -> actions.toast "无法访问剪贴板，诊断报告未复制。" Failure
            | top ->
                match top.Clipboard with
                | null -> actions.toast "无法访问剪贴板，诊断报告未复制。" Failure
                | clip ->
                    async {
                        try
                            do! clip.SetTextAsync diag |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () -> actions.toast "诊断报告已复制到剪贴板。" Success)
                        with _ ->
                            Dispatcher.UIThread.Post(fun () -> actions.toast "复制诊断报告失败，请重试。" Failure)
                    }
                    |> Async.Start)
        copyDiagButton.HorizontalAlignment <- HorizontalAlignment.Left
        Ui.vstack
            Tokens.space6
            [ Ui.vstack Tokens.space1 [ Ui.heading "关于与系统诊断" :> Control ] :> Control
              Ui.card(
                  Ui.vstack
                      Tokens.space2
                      [ row "协议版本" (string Constants.ProtocolVersion)
                        row "日志格式版本" (string Constants.FormatVersion)
                        row "客户端平台" platformDesc
                        row "操作系统" osDesc
                        row "服务器实例" (if String.IsNullOrWhiteSpace instanceId then "未连接" else instanceId)
                        row "连接地址" (if String.IsNullOrWhiteSpace serverUrl then "未连接" else serverUrl)
                        Border(Height = Tokens.space2) :> Control
                        copyDiagButton :> Control ])
              :> Control
              Ui.card(
                  Ui.vstack
                      Tokens.space2
                      [ Ui.label "设计与实现" :> Control
                        Ui.caption
                            "万象是独立实现的对等 C/S 聊天客户端：每个实例既是服务端也是客户端，"
                        :> Control
                        Ui.caption "会话以 NDJSON 事件日志为唯一权威，界面只是投影的一个视图。" :> Control ])
              :> Control
              Ui.card(
                  Ui.vstack
                      Tokens.space2
                      [ Ui.label "致谢" :> Control
                        Ui.caption "界面思路受 Kelivo 启发，代码与数据模型均为独立实现。" :> Control
                        Ui.caption "内嵌字体 Sarasa Term SC 采用 OFL-1.1 许可。" :> Control ])
              :> Control ]
        :> Control
