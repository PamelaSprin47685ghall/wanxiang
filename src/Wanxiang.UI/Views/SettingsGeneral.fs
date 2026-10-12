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
/// 生成参数落到主机配置（新对话继承）；外观偏好只存本机
/// （Q195：窗口尺寸与主题是客户端偏好，不进业务 TOML、不经 NDJSON）。
type SettingsGeneral(overlay: OverlayHost, actions: SettingsActions, onPrefsChanged: UiPrefs -> unit) =

    let mutable catalog = Catalog.empty
    let mutable prefs = UiPrefs.defaults

    let temperatureBox = snd (Ui.textField "留空 = 使用模型服务默认")
    let topPBox = snd (Ui.textField "0–1，留空不设")
    let maxTokensBox = snd (Ui.textField "留空不限制")
    let contextBox = snd (Ui.textField "200")
    let toolRoundsBox = snd (Ui.textField "12")
    let instructionsBox = snd (Ui.textArea "新对话的默认系统指令（可留空）" ControlMetrics.textAreaMinHeight)
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
    let mutable generationDirty = false
    let mutable generationSyncing = false
    let mutable generationRevision = 0
    let mutable generationBaseline = catalog.generation
    let mutable generationScope = ""
    let mutable draftScope = ""
    let mutable scopeEpoch = 0
    let generationStatus = Ui.caption "修改后保存，仅用于新对话。"
    let mutable syncAppearance: UiPrefs -> unit = ignore

    let markGenerationDirty () =
        if not generationSyncing then
            if not generationDirty then draftScope <- generationScope
            generationRevision <- generationRevision + 1
            generationDirty <- true
            generationStatus.Text <-
                if draftScope <> generationScope then "主机已切换。旧草稿仍在，但不能保存到另一台主机。请返回原主机，或放弃修改。"
                else "有未保存的修改。离开设置后，草稿仍留在当前窗口。"

    do
        Avalonia.Automation.AutomationProperties.SetName(generationStatus, "生成设置保存状态")
        Avalonia.Automation.AutomationProperties.SetLiveSetting(
            generationStatus, Avalonia.Automation.AutomationLiveSetting.Polite)
        for box in [ temperatureBox; topPBox; maxTokensBox; contextBox; toolRoundsBox; instructionsBox ] do
            box.PropertyChanged.Add(fun e ->
                if e.Property = TextBox.TextProperty then markGenerationDirty ())

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
        // 目录更新也会由其它设置操作触发；不能用它覆盖尚未提交的表单。
        if not generationDirty && not generationSaving then
            generationSyncing <- true
            try
                let g = next.generation
                temperatureBox.Text <- floatText g.temperature
                topPBox.Text <- floatText g.topP
                maxTokensBox.Text <- (match g.maxTokens with Some v -> string v | None -> "")
                contextBox.Text <- string g.maxContextMessages
                toolRoundsBox.Text <- string g.maxToolRounds
                instructionsBox.Text <- g.instructions |> Option.defaultValue ""
                setAutoTitle g.autoTitle
                generationBaseline <- g
            finally
                generationSyncing <- false
        elif not generationSaving && draftScope = generationScope && next.generation <> generationBaseline then
            generationStatus.Text <- "主机默认值已变化。草稿已保留；请核对后保存，或放弃修改。"

    /// 只保留一份可见草稿；切换目标后阻止把旧主机的表单误写给新主机。
    member _.SetGenerationScope(instance: string) =
        if generationScope <> instance then
            scopeEpoch <- scopeEpoch + 1
            generationScope <- instance
            generationSaving <- false
            setGenerationPending false
            if generationDirty then
                generationStatus.Text <-
                    if draftScope = instance then "已回到草稿所属的主机。修改尚未保存。"
                    else "主机已切换。旧草稿仍在，但不能保存到另一台主机。请返回原主机，或放弃修改。"

    member _.HasUnsavedGeneration = generationDirty

    member this.DiscardGenerationChanges() =
        if not generationSaving then
            generationDirty <- false
            draftScope <- generationScope
            generationRevision <- generationRevision + 1
            this.SetCatalog catalog
            for box in [ temperatureBox; topPBox; maxTokensBox; contextBox; toolRoundsBox ] do Ui.clearFieldError box
            generationErrorSummary.IsVisible <- false
            generationStatus.Text <- "已恢复当前主机的默认值。"
            setGenerationPending false

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
            let submitted =
                { catalog.generation with
                    temperature = temperature
                    topP = topP
                    maxTokens = maxTokens
                    instructions = if String.IsNullOrWhiteSpace instructions then None else Some instructions
                    maxContextMessages = contextMessages.Value
                    maxToolRounds = toolRounds.Value
                    autoTitle = readAutoTitle () }
            let submittedRevision = generationRevision
            let submittedEpoch = scopeEpoch
            setGenerationPending true
            generationStatus.Text <- "正在保存到主机…"
            actions.updateGeneration payload (fun ok ->
                if submittedEpoch <> scopeEpoch then ()
                elif ok then
                    setGenerationPending false
                    generationBaseline <- submitted
                    catalog <- { catalog with generation = submitted }
                    // 往返期间仍可编辑。较早提交的确认不能把后续编辑标成已保存。
                    generationDirty <- generationRevision <> submittedRevision
                    generationStatus.Text <-
                        if generationDirty then "上一版已保存，后续修改尚未保存。"
                        else "已保存。之后新建的对话将使用这些默认值。"
                else
                    setGenerationPending false
                    generationDirty <- true
                    generationStatus.Text <- "保存失败。修改仍在当前窗口，可以重试。"
                    actions.toast "保存失败，请重试。" Failure)
        else
            Ui.applyValidationFeedback (List.ofSeq errors) (Some generationErrorSummary) (fun message -> actions.toast message Warning)

    /// pending 中的保存直接返回，不做二次提交。
    member private this.SaveGeneration() =
        if generationSaving || (generationDirty && draftScope <> generationScope) then ()
        else this.SaveGenerationCore()

    member this.BuildGeneration() : Control =
        let autoTitleRow, readAuto, writeAuto =
            Ui.switchRow "自动生成对话标题" "首轮对话后用同一个模型起一个短标题；失败时回落到你的第一句话。" catalog.generation.autoTitle (fun _ -> markGenerationDirty ())
        readAutoTitle <- readAuto
        setAutoTitle <- writeAuto
        let saveButton = Ui.button Ui.Primary "保存生成设置" (fun () -> this.SaveGeneration())
        // 键盘位与文案都在按钮上自描述：本地表单（模型服务、MCP、对话参数）
        // 一律「Ctrl/⌘+Enter 保存」，这里此前只响应鼠标点击。
        Avalonia.Automation.AutomationProperties.SetHelpText(saveButton, "保存生成设置 (Ctrl+Enter)")
        ToolTip.SetTip(saveButton, "保存生成设置 (Ctrl+Enter)")
        Ui.preparePendingButton saveButton
        saveButton.HorizontalAlignment <- HorizontalAlignment.Left
        let discardButton = Ui.button Ui.Ghost "放弃修改" (fun () -> this.DiscardGenerationChanges())
        setGenerationPending <- fun pending ->
            generationSaving <- pending
            Ui.setButtonPending saveButton pending "保存生成设置" "正在保存…"
            if not pending then Ui.setEnabled saveButton (not generationDirty || draftScope = generationScope)
            Ui.setEnabled discardButton (not pending)
        let temperatureField = Ui.inputFieldGroup "Temperature" "越高越发散。留空则用模型服务默认值。" temperatureBox
        let topPField = Ui.inputFieldGroup "Top P" "核采样阈值，0–1。" topPBox
        let maxTokensField = Ui.inputFieldGroup "最大输出 token" "限制单次回复长度。" maxTokensBox
        let contextField = Ui.inputFieldGroup "上下文消息上限" "每次请求最多携带多少条历史，防止长对话撞上模型上限。" contextBox
        let toolRoundsField = Ui.inputFieldGroup "工具调用轮数上限" "模型连续调用工具超过这个轮数就中止本次生成。" toolRoundsBox
        let grid, applyGridLayout =
            Ui.twoColumnForm Spacing.space2xl Spacing.spaceXl 5
                [ temperatureField; topPField; maxTokensField; contextField; toolRoundsField ]
        applyGridLayout ()
        // Ctrl+Enter 提交：Enter/Space 归 Ui.onClick（在 saveButton 上），行级不重复处理，
        // 多行指令框里的回车也不会误提交——只认带 Ctrl/⌘ 的那一次。
        // 与 SettingsProviders / SettingsTools 表单同一写法。
        let generationForm =
            Ui.vstack
                Spacing.space4xl
                [ Ui.vstack
                      Spacing.spaceXs
                      [ Ui.heading "默认设置" :> Control
                        Ui.caption "保存到当前主机，供所有客户端新建对话时使用。不会改动已有对话。" :> Control ]
                  :> Control
                  generationErrorSummary :> Control
                  // 数值表单、指令框与开关行收进与 Appearance/About 同档的分组面板卡
                  //（面板档 padding (space4, space4) + radiusLg），卡内用 groupDivider 分行，
                  // 与其余设置页共用同一「卡/区」中间层；字段、顺序与保存入口不变。
                  (Ui.groupingCard (Thickness(Spacing.space2xl, Spacing.space2xl))
                      (Ui.vstack
                          Spacing.spaceXs
                          [ grid :> Control
                            groupDivider () :> Control
                            Ui.controlFieldGroup "默认系统指令" "会作为 system 消息随每次请求发送。" (instructionsBox.Parent :?> Control)
                            groupDivider () :> Control
                            autoTitleRow ])
                      Spacing.Radius.lg) :> Control
                  Ui.hairline () :> Control
                  generationStatus :> Control
                  Ui.hstack Spacing.spaceMd [ saveButton :> Control; discardButton :> Control ] :> Control ]
        generationForm.KeyDown.Add(fun e ->
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
            if e.Key = Key.Enter && ctrl then
                e.Handled <- true
                this.SaveGeneration())
        // 五个数值单行框 Enter 即保存：与连接对话框、模型服务/工具编辑页同一约定。
        // 系统指令是多行 textArea，那里的回车是换行，不接。
        let ctrl (e: KeyEventArgs) =
            e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
        for box in [ temperatureBox; topPBox; maxTokensBox; contextBox; toolRoundsBox ] do
            box.KeyDown.Add(fun e ->
                if e.Key = Key.Enter && not (ctrl e) then
                    e.Handled <- true
                    this.SaveGeneration())
        // 焦点移动只改变编辑位置，不提交；切换分区后仍使用同一张表单。
        generationForm :> Control

    /// 主机状态与「检查主机」的状态源（AppShell 写入，关于页订阅）。
    member val Health = HostHealth() with get

    /// 「本机草稿」区与外壳之间的桥（清除动作与概况）。
    member val LocalDrafts = LocalDraftControls() with get

    /// 本机草稿：开关 + 清除。草稿只存本机、不上传不同步；关闭立即删除已存内容。
    member private this.BuildLocalDrafts(setSwitch: (bool -> unit) -> unit) : Control =
        let draftsRow, _, setPersist =
            Ui.switchRow
                "保存未发送的草稿"
                "在这台设备上保存输入框里没发出的文字、附件名称和未确认的消息，重新打开后恢复。只存在本机，不会上传或同步。关闭后会立即删除已保存的内容。"
                prefs.persistDrafts
                (fun value ->
                    prefs <- { prefs with persistDrafts = value }
                    onPrefsChanged prefs)
        setSwitch setPersist
        let summaryText = Ui.caption this.LocalDrafts.Summary
        Avalonia.Automation.AutomationProperties.SetAutomationId(summaryText, "local-drafts-summary")
        let clearButton =
            Ui.button Ui.Secondary "清除本地草稿" (fun () ->
                // 确认框由外壳弹（Dialogs 在设置视图之后编译）；这里只发出请求。
                this.LocalDrafts.ClearRequested())
        clearButton.HorizontalAlignment <- HorizontalAlignment.Left
        Avalonia.Automation.AutomationProperties.SetAutomationId(clearButton, "clear-local-drafts")
        this.LocalDrafts.Changed.Add(fun () -> summaryText.Text <- this.LocalDrafts.Summary)
        Ui.groupingCard
            (Thickness(Spacing.space2xl, Spacing.space2xl))
            (Ui.vstack
                Spacing.spaceMd
                [ draftsRow
                  groupDivider () :> Control
                  summaryText :> Control
                  clearButton :> Control ])
            Spacing.Radius.lg
        :> Control

    /// 主机状态：配置是否被拒绝 + 「检查主机」（doctor 的只读结果）。
    member private this.BuildHostStatus() : Control =
        let host = this.Health
        let body = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceMd)
        let render () =
            body.Children.Clear()
            // 配置状态
            let issueLine =
                match host.ConfigIssue with
                | Some(summary, errors) ->
                    let detail = errors |> String.concat "
"
                    let lines =
                        [ Ui.label "配置有误" :> Control
                          (let t = Ui.caption summary
                           t.Foreground <- Tokens.danger
                           Avalonia.Automation.AutomationProperties.SetAutomationId(t, "host-config-issue")
                           t :> Control)
                          (let t = Ui.caption detail
                           Avalonia.Automation.AutomationProperties.SetAutomationId(t, "host-config-issue-detail")
                           t :> Control)
                          Ui.caption "修好配置文件后，这条提示会自动消失。" :> Control ]
                    Ui.vstack Spacing.spaceXs lines :> Control
                | None ->
                    let t = Ui.caption "配置正常，主机正在使用最新一份配置。"
                    Avalonia.Automation.AutomationProperties.SetAutomationId(t, "host-config-ok")
                    t :> Control
            body.Children.Add issueLine
            body.Children.Add(Ui.hairline ())
            // 检查主机
            let checkButton =
                Ui.button Ui.Secondary (match host.Doctor with Running -> "正在检查…" | _ -> "检查主机") (fun () ->
                    match host.Doctor with
                    | Running -> ()
                    | _ -> host.BeginCheck())
            checkButton.HorizontalAlignment <- HorizontalAlignment.Left
            Avalonia.Automation.AutomationProperties.SetAutomationId(checkButton, "check-host")
            (match host.Doctor with Running -> Ui.setEnabled checkButton false | _ -> ())
            body.Children.Add checkButton
            match host.Doctor with
            | NotRun -> body.Children.Add(Ui.caption "只读检查：配置、磁盘、数据目录、事件日志与附件。不会修改任何内容。")
            | Running -> body.Children.Add(Ui.caption "正在检查，可能需要几秒…")
            | Unavailable reason -> body.Children.Add(Ui.caption reason)
            | Report(ok, checks, at) ->
                let headline = Ui.label (if ok then "全部通过" else "有项目需要处理")
                Avalonia.Automation.AutomationProperties.SetAutomationId(headline, "doctor-headline")
                body.Children.Add headline
                for c in checks do
                    let icon: Control = if c.ok then Icons.checkCircle Tokens.success else Icons.alert Tokens.danger
                    let name = Ui.label (DoctorCheck.label c.name)
                    name.MinWidth <- ControlMetrics.aboutKeyMinWidth
                    let detail =
                        SelectableTextBlock(
                            Text = c.detail,
                            FontSize = Tokens.fontCaption,
                            Foreground = (if c.ok then Tokens.textMuted else Tokens.danger),
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center)
                    let line = Ui.hstack Spacing.spaceMd [ icon; name :> Control; detail :> Control ]
                    Avalonia.Automation.AutomationProperties.SetAutomationId(line, sprintf "doctor-check-%s" c.name)
                    body.Children.Add line
                body.Children.Add(Ui.caption (sprintf "检查时间 %s" (at.ToString "HH:mm:ss")))
        render ()
        let handler = Handler<unit>(fun _ () -> render ())
        host.Changed.AddHandler handler
        body.DetachedFromLogicalTree.Add(fun _ -> host.Changed.RemoveHandler handler)
        Ui.groupingCard
            (Thickness(Spacing.space2xl, Spacing.space2xl))
            (Ui.vstack Spacing.spaceMd [ Ui.label "主机状态" :> Control; body :> Control ])
            Spacing.Radius.lg
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
            let controls = Ui.hstack Spacing.spaceMd [ smaller :> Control; fontSizeCaption :> Control; larger :> Control ]
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
            Ui.switchRow "在侧栏显示已归档对话" "归档对话默认收起，避免长列表。" prefs.showArchived (fun value ->
                prefs <- { prefs with showArchived = value }
                onPrefsChanged prefs)
        let motionRow, _, setReduceMotion =
            Ui.switchRow "减少动态效果" "停止加载指示器等持续旋转；状态变化仍会即时显示。" prefs.reduceMotion (fun value ->
                prefs <- { prefs with reduceMotion = value }
                onPrefsChanged prefs)

        let mutable setPersistDrafts: bool -> unit = ignore
        let localDraftsCard = this.BuildLocalDrafts(fun setter -> setPersistDrafts <- setter)

        syncAppearance <- fun next ->
            setPersistDrafts next.persistDrafts
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
            Spacing.space4xl
            [ Ui.vstack
                  Spacing.spaceXs
                  [ Ui.heading "外观与交互" :> Control
                    Ui.caption "这些偏好只保存在本机，不会同步到主机。" :> Control ]
              :> Control
              // 分组面板内边距走面板档 (space4, space4)：比列表行卡 (space4/space3) 更宽松、四角均衡，
              // 用于承载多行/多字段的容器卡；设置四页共用这一小档集合。
              (Ui.groupingCard (Thickness(Spacing.space2xl, Spacing.space2xl))
                  (Ui.vstack
                      Spacing.spaceXs
                      [ Ui.controlFieldGroup "主题" "深色主题是同一套纸感在低光下的版本。" (themeButton :> Control)
                        groupDivider () :> Control
                        fontRow ])
                  Spacing.Radius.lg) :> Control
              (Ui.groupingCard (Thickness(Spacing.space2xl, Spacing.space2xl))
                  (Ui.vstack
                      Spacing.spaceXs
                      [ enterRow
                        groupDivider () :> Control
                        collapseRow
                        groupDivider () :> Control
                        codeWrapRow
                        groupDivider () :> Control
                        archivedRow
                        groupDivider () :> Control
                        motionRow ])
                  Spacing.Radius.lg) :> Control
              Ui.vstack
                  Spacing.spaceXs
                  [ Ui.heading "本机草稿" :> Control
                    Ui.caption "草稿只保存在这台设备上，不会上传，也不会出现在对话记录里。" :> Control ]
              :> Control
              localDraftsCard ]
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
            Ui.hstack Spacing.spaceXl [ k :> Control; v :> Control ] :> Control
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
            Spacing.space4xl
            [ Ui.vstack Spacing.spaceXs [ Ui.heading "关于与系统诊断" :> Control ] :> Control
              Ui.groupingCard
                  (Thickness(Spacing.space2xl, Spacing.space2xl))
                  (Ui.vstack
                      Spacing.spaceMd
                      [ row "协议版本" (string Constants.ProtocolVersion)
                        row "日志格式版本" (string Constants.FormatVersion)
                        row "客户端平台" platformDesc
                        row "操作系统" osDesc
                        row "主机实例" (if String.IsNullOrWhiteSpace instanceId then "未连接" else instanceId)
                        row "连接地址" (if String.IsNullOrWhiteSpace serverUrl then "未连接" else serverUrl)
                        Ui.hairline () :> Control
                        copyDiagButton :> Control ])
                  Spacing.Radius.lg
              :> Control
              this.BuildHostStatus()
              Ui.groupingCard
                  (Thickness(Spacing.space2xl, Spacing.space2xl))
                  (Ui.vstack
                      Spacing.spaceMd
                      [ Ui.label "设计与实现" :> Control
                        Ui.caption "万象是独立实现的对等 C/S 聊天客户端：每个实例既是主机也是客户端，" :> Control
                        Ui.caption "对话以 NDJSON 事件日志为唯一权威，界面只是投影的一个视图。" :> Control ])
                  Spacing.Radius.lg
              :> Control
              Ui.groupingCard
                  (Thickness(Spacing.space2xl, Spacing.space2xl))
                  (Ui.vstack
                      Spacing.spaceMd
                      [ Ui.label "致谢" :> Control
                        Ui.caption "界面思路受 Kelivo 启发，代码与数据模型均为独立实现。" :> Control
                        Ui.caption "内嵌字体 Sarasa Term SC 采用 OFL-1.1 许可。" :> Control ])
                  Spacing.Radius.lg
              :> Control ]
        :> Control
