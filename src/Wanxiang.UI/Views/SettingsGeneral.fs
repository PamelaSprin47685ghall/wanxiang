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
    let instructionsBox = snd (Ui.textArea "新会话的默认系统指令（可留空）" ControlMetrics.textAreaMinHeight)
    let generationErrorSummary =
        let summary =
            TextBlock(
                Text = "",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.danger,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = ReadingRhythm.captionLineHeight,
                IsVisible = false,
                Focusable = true)
        Avalonia.Automation.AutomationProperties.SetName(summary, "表单错误摘要")
        Avalonia.Automation.AutomationProperties.SetLiveSetting(
            summary,
            Avalonia.Automation.AutomationLiveSetting.Assertive)
        summary

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

    // 分组卡统一走 Ui.groupingCard（Primitives 单一来源）：本地副本已删除。

    /// 组内分隔线走内容档（Ui.hairline = borderSoft）：分隔面板内多行内容的发丝线与其它
    /// 内容分隔线同一档；窗口 / 导航这类 chrome 分隔才用更轻的 Tokens.hairline。
    let groupDivider () : Border = Ui.hairline ()

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
        generationErrorSummary.IsVisible <- false
        generationErrorSummary.Text <- ""
        let errors = ResizeArray<TextBox * string>()
        let fail box message =
            Ui.setFieldError box message
            errors.Add(box, message)
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

        if List.isEmpty (List.ofSeq errors) then
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
            actions.updateGeneration payload (fun ok ->
                setGenerationPending false
                if not ok then actions.toast "保存失败，请重试。" Failure)
        else
            Ui.applyValidationFeedback (List.ofSeq errors) (Some generationErrorSummary) (fun message -> actions.toast message Warning)

    /// pending 中的保存直接返回，不做二次提交。
    member private this.SaveGeneration() =
        if generationSaving then () else this.SaveGenerationCore()

    member this.BuildGeneration() : Control =
        let autoTitleRow, readAuto, writeAuto =
            Ui.switchRow "自动生成会话标题" "首轮对话后用同一个模型起一个短标题；失败时回落到你的第一句话。" true ignore
        readAutoTitle <- readAuto
        setAutoTitle <- writeAuto
        let saveButton = Ui.button Ui.Primary "保存生成设置" (fun () -> this.SaveGeneration())
        // 键盘位与文案都在按钮上自描述：本地表单（服务商、MCP、会话参数）
        // 一律「Ctrl/⌘+Enter 保存」，这里此前只响应鼠标点击。
        Avalonia.Automation.AutomationProperties.SetHelpText(saveButton, "保存生成设置 (Ctrl+Enter)")
        ToolTip.SetTip(saveButton, "保存生成设置 (Ctrl+Enter)")
        Ui.preparePendingButton saveButton
        saveButton.HorizontalAlignment <- HorizontalAlignment.Left
        setGenerationPending <- fun pending ->
            generationSaving <- pending
            Ui.setButtonPending saveButton pending "保存生成设置" "正在保存…"
        let temperatureField = Ui.inputFieldGroup "Temperature" "越高越发散。留空则用服务商默认值。" temperatureBox
        let topPField = Ui.inputFieldGroup "Top P" "核采样阈值，0–1。" topPBox
        let maxTokensField = Ui.inputFieldGroup "最大输出 token" "限制单次回复长度。" maxTokensBox
        let contextField = Ui.inputFieldGroup "上下文消息上限" "每次请求最多携带多少条历史，防止长会话撞上模型上限。" contextBox
        let toolRoundsField = Ui.inputFieldGroup "工具调用轮数上限" "模型连续调用工具超过这个轮数就中止本次生成。" toolRoundsBox
        let grid, applyGridLayout =
            Ui.twoColumnForm Tokens.space4 Tokens.space3 5
                [ temperatureField; topPField; maxTokensField; contextField; toolRoundsField ]
        applyGridLayout ()
        // Ctrl+Enter 提交：Enter/Space 归 Ui.onClick（在 saveButton 上），行级不重复处理，
        // 多行指令框里的回车也不会误提交——只认带 Ctrl/⌘ 的那一次。
        // 与 SettingsProviders / SettingsTools 表单同一写法。
        let generationForm =
            Ui.vstack
                Tokens.space6
                [ Ui.vstack
                      Tokens.space1
                      [ Ui.heading "生成" :> Control
                        Ui.caption "这些是新会话的默认值。单个会话可以在会话设置里单独调整。" :> Control ]
                  :> Control
                  generationErrorSummary :> Control
                  // 数值表单、指令框与开关行收进与 Appearance/About 同档的分组面板卡
                  //（面板档 padding (space4, space4) + radiusLg），卡内用 groupDivider 分行，
                  // 与其余设置页共用同一「卡/区」中间层；字段、顺序与保存入口不变。
                  (Ui.groupingCard (Thickness(Tokens.space4, Tokens.space4))
                      (Ui.vstack
                          Tokens.space1
                          [ grid :> Control
                            groupDivider () :> Control
                            Ui.controlFieldGroup "默认系统指令" "会作为 system 消息随每次请求发送。" (instructionsBox.Parent :?> Control)
                            groupDivider () :> Control
                            autoTitleRow ])
                      Tokens.radiusLg) :> Control
                  Ui.hairline () :> Control
                  saveButton :> Control ]
        generationForm.KeyDown.Add(fun e ->
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
            if e.Key = Key.Enter && ctrl then
                e.Handled <- true
                this.SaveGeneration())
        // 五个数值单行框 Enter 即保存：与连接对话框、服务商/工具编辑页同一约定。
        // 系统指令是多行 textArea，那里的回车是换行，不接。
        let ctrl (e: KeyEventArgs) =
            e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
        for box in [ temperatureBox; topPBox; maxTokensBox; contextBox; toolRoundsBox ] do
            box.KeyDown.Add(fun e ->
                if e.Key = Key.Enter && not (ctrl e) then
                    e.Handled <- true
                    this.SaveGeneration())
        generationForm :> Control

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
                MinWidth = ControlMetrics.fontSizeValueMinWidth)
        let smaller = Ui.iconButton Icons.minus "更小"
        let larger = Ui.iconButton Icons.plus "更大"
        // 边界即禁用：字号到达下限／上限时对应方向的按钮不可点，替代原先静默跳过——
        // 按钮点下去毫无变化就是坏反馈，禁用是即可见的边界说明。初始态与外部偏好同步都经这里。
        let applyFontBounds () =
            Ui.setEnabled smaller (prefs.fontScale > UiPrefs.minFontScale + 0.01)
            Ui.setEnabled larger (prefs.fontScale < UiPrefs.maxFontScale - 0.01)
        let adjust (delta: float) =
            let next = Math.Clamp(prefs.fontScale + delta, UiPrefs.minFontScale, UiPrefs.maxFontScale)
            if abs (next - prefs.fontScale) > 0.01 then
                prefs <- { prefs with fontScale = next }
                fontSizeCaption.Text <- sprintf "%.1f pt" next
                onPrefsChanged prefs
            applyFontBounds ()
        Ui.onClick smaller (fun () -> adjust -0.5)
        Ui.onClick larger (fun () -> adjust 0.5)
        applyFontBounds ()
        let fontRow =
            let controls = Ui.hstack Tokens.space2 [ smaller :> Control; fontSizeCaption :> Control; larger :> Control ]
            // 与 Ui.switchRow 同一行壳（settingsRowShell）：右侧换成了字号调节组；
            // 原手抄的 Border + DockPanel 收回共享原语，几何逐字不变，无开关交互层。
            Ui.settingsRowShell "消息字号" "影响对话正文与代码块。" controls :> Control

        let enterRow, _, setEnterSends =
            Ui.switchRow "Enter 直接发送" "关闭后用 Ctrl+Enter 发送，Enter 换行。" prefs.enterSends (fun value ->
                prefs <- { prefs with enterSends = value }
                onPrefsChanged prefs)
        let collapseRow, _, setCollapseReasoning =
            Ui.switchRow "完成后收起思考过程" "生成结束时自动折叠模型的思维链。" prefs.autoCollapseReasoning (fun value ->
                prefs <- { prefs with autoCollapseReasoning = value }
                onPrefsChanged prefs)
        let codeWrapRow, _, setCodeWrap =
            Ui.switchRow "代码块长行折行" "关闭时长行横向滚动；开启后折行显示，一屏读完。" prefs.codeWrap (fun value ->
                prefs <- { prefs with codeWrap = value }
                onPrefsChanged prefs)
        let archivedRow, _, setArchived =
            Ui.switchRow "在侧栏显示已归档会话" "归档会话默认收起，避免长列表。" prefs.showArchived (fun value ->
                prefs <- { prefs with showArchived = value }
                onPrefsChanged prefs)
        let motionRow, _, setReduceMotion =
            Ui.switchRow "减少动态效果" "停止加载指示器等持续旋转；状态变化仍会即时显示。" prefs.reduceMotion (fun value ->
                prefs <- { prefs with reduceMotion = value }
                onPrefsChanged prefs)

        syncAppearance <- fun next ->
            setThemeText (ThemePreference.label next.theme)
            fontSizeCaption.Text <- sprintf "%.1f pt" next.fontScale
            // 外部偏好同步（AppShell.SavePrefs → SettingsView.SetPrefs）同样刷新边界禁用态。
            applyFontBounds ()
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
              // 分组面板内边距走面板档 (space4, space4)：比列表行卡 (space4/space3) 更宽松、四角均衡，
              // 用于承载多行/多字段的容器卡；设置四页共用这一小档集合。
              (Ui.groupingCard (Thickness(Tokens.space4, Tokens.space4))
                  (Ui.vstack
                      Tokens.space1
                      [ Ui.controlFieldGroup "主题" "深色主题是同一套纸感在低光下的版本。" (themeButton :> Control)
                        groupDivider () :> Control
                        fontRow ])
                  Tokens.radiusLg) :> Control
              (Ui.groupingCard (Thickness(Tokens.space4, Tokens.space4))
                  (Ui.vstack
                      Tokens.space1
                      [ enterRow
                        groupDivider () :> Control
                        collapseRow
                        groupDivider () :> Control
                        codeWrapRow
                        groupDivider () :> Control
                        archivedRow
                        groupDivider () :> Control
                        motionRow ])
                  Tokens.radiusLg) :> Control ]
        :> Control

    member this.BuildAbout(instanceId: string, serverUrl: string) : Control =
        let row (key: string) (value: string) : Control =
            let k = TextBlock(Text = key, FontSize = Tokens.fontSmall, Foreground = Tokens.textMuted, MinWidth = ControlMetrics.aboutKeyMinWidth)
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
              Ui.groupingCard
                  (Thickness(Tokens.space4, Tokens.space4))
                  (Ui.vstack
                      Tokens.space2
                      [ row "协议版本" (string Constants.ProtocolVersion)
                        row "日志格式版本" (string Constants.FormatVersion)
                        row "客户端平台" platformDesc
                        row "操作系统" osDesc
                        row "服务器实例" (if String.IsNullOrWhiteSpace instanceId then "未连接" else instanceId)
                        row "连接地址" (if String.IsNullOrWhiteSpace serverUrl then "未连接" else serverUrl)
                        Ui.hairline () :> Control
                        copyDiagButton :> Control ])
                  Tokens.radiusLg
              :> Control
              Ui.groupingCard
                  (Thickness(Tokens.space4, Tokens.space4))
                  (Ui.vstack
                      Tokens.space2
                      [ Ui.label "设计与实现" :> Control
                        Ui.caption "万象是独立实现的对等 C/S 聊天客户端：每个实例既是服务端也是客户端，" :> Control
                        Ui.caption "会话以 NDJSON 事件日志为唯一权威，界面只是投影的一个视图。" :> Control ])
                  Tokens.radiusLg
              :> Control
              Ui.groupingCard
                  (Thickness(Tokens.space4, Tokens.space4))
                  (Ui.vstack
                      Tokens.space2
                      [ Ui.label "致谢" :> Control
                        Ui.caption "界面思路受 Kelivo 启发，代码与数据模型均为独立实现。" :> Control
                        Ui.caption "内嵌字体 Sarasa Term SC 采用 OFL-1.1 许可。" :> Control ])
                  Tokens.radiusLg
              :> Control ]
        :> Control
