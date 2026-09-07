namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Avalonia.Interactivity
open Avalonia.Automation
open Wanxiang.Core

/// 连接对话框的回调。
type ConnectRequest = {
    url: string
    token: string option
    /// true 表示走配对流程而不是直接用令牌
    usePairing: bool
}

/// 应用内对话框。桌面与 PWA 共用，全部画在 OverlayHost 上。
module Dialogs =

    /// 返回按钮行与主操作按钮：调用方可在 ShowDialog 之后把焦点 post 到主按钮上
    ///（ShowDialog 内部的 focusFirst 会先聚焦第一个可聚焦项，后 post 者胜出）。
    /// 返回按钮行与主操作按钮，可指定默认聚焦按钮（true 聚焦确认按钮，false 聚焦取消按钮）。
    let private actionRowWithDefault
        (overlay: OverlayHost)
        (confirmLabel: string)
        (tone: Ui.ButtonTone)
        (onConfirm: unit -> unit)
        (defaultFocusConfirm: bool)
        =
        let cancelButton = Ui.button Ui.Ghost "取消" (fun () -> overlay.CloseDialog())
        AutomationProperties.SetName(cancelButton, "取消")
        AutomationProperties.SetHelpText(cancelButton, "取消并关闭对话框 (Esc)")
        ToolTip.SetTip(cancelButton, "取消并关闭对话框 (Esc)")
        let confirmButton = Ui.button tone confirmLabel onConfirm
        AutomationProperties.SetName(confirmButton, confirmLabel)
        let confirmTip =
            match tone with
            | Ui.Danger -> sprintf "确认“%s”，此操作可能无法撤销" confirmLabel
            | _ -> confirmLabel
        AutomationProperties.SetHelpText(confirmButton, confirmTip)
        ToolTip.SetTip(confirmButton, confirmTip)
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Right)
        let wireArrowNavigation (leftBtn: Border) (rightBtn: Border) =
            leftBtn.KeyDown.Add(fun e ->
                if e.Key = Key.Right && rightBtn.IsEnabled then
                    e.Handled <- true
                    rightBtn.Focus(NavigationMethod.Directional) |> ignore)
            rightBtn.KeyDown.Add(fun e ->
                if e.Key = Key.Left && leftBtn.IsEnabled then
                    e.Handled <- true
                    leftBtn.Focus(NavigationMethod.Directional) |> ignore)
        wireArrowNavigation cancelButton confirmButton

        row.Children.Add cancelButton
        row.Children.Add confirmButton
        row.KeyDown.Add(fun e ->
            if e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog()
            elif (e.Key = Key.Enter || e.Key = Key.Space) && not cancelButton.IsFocused then
                e.Handled <- true
                onConfirm ()
            elif (e.Key = Key.Enter || e.Key = Key.Space) && cancelButton.IsFocused then
                e.Handled <- true
                overlay.CloseDialog())
        row, confirmButton

    let private actionRow (overlay: OverlayHost) (confirmLabel: string) (tone: Ui.ButtonTone) (onConfirm: unit -> unit) =
        actionRowWithDefault overlay confirmLabel tone onConfirm false

    /// 单行输入对话框（重命名等）。
    let prompt
        (overlay: OverlayHost)
        (title: string)
        (hint: string)
        (initial: string)
        (confirmLabel: string)
        (onConfirm: string -> (bool -> unit) -> unit)
        =
        let shell, box = Ui.textField hint
        box.Text <- initial
        let validation = Ui.fieldValidationMessage box
        let helper = Ui.caption "请输入非空内容"
        helper.Margin <- Thickness(2.0, Tokens.space1, 0.0, 0.0)
        helper.IsVisible <- false
        let mutable active = true
        let mutable pending = false
        let mutable setPending: bool -> unit = ignore
        let mutable isConfirmEnabled = false
        let mutable updateConfirmState: unit -> unit = ignore
        let mutable canSubmit = false

        let updateValidity () =
            let raw = if isNull box.Text then "" else box.Text
            let trimmed = raw.Trim()
            let isEmpty = String.IsNullOrWhiteSpace trimmed
            if isEmpty then
                if raw.Length > 0 then
                    Ui.setFieldError box "输入内容不能全为空白字符。"
                    helper.IsVisible <- false
                else
                    Ui.clearFieldError box
                    helper.Text <- "内容不能为空，请输入有效文本。"
                    helper.IsVisible <- true
            else
                Ui.clearFieldError box
                helper.IsVisible <- false
            let canSubmit = not pending && not isEmpty
            isConfirmEnabled <- canSubmit
            updateConfirmState ()
            canSubmit

        box.GetObservable(TextBox.TextProperty).Subscribe(fun _ ->
            updateValidity () |> ignore) |> ignore

        let submit () =
            let value = if isNull box.Text then "" else box.Text.Trim()
            if not pending && isConfirmEnabled && not (String.IsNullOrWhiteSpace value) then
                setPending true
                onConfirm value (fun ok ->
                    if active then
                        setPending false
                        if ok then overlay.CloseDialog())
        box.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                submit ()
            elif e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog())
        let cancelButton = Ui.button Ui.Ghost "取消" (fun () -> overlay.CloseDialog())
        AutomationProperties.SetName(cancelButton, "取消")
        AutomationProperties.SetHelpText(cancelButton, "取消并关闭对话框 (Esc)")
        ToolTip.SetTip(cancelButton, "取消并关闭对话框 (Esc)")
        let confirmButton = Ui.button Ui.Primary confirmLabel submit
        AutomationProperties.SetName(confirmButton, confirmLabel)
        AutomationProperties.SetHelpText(confirmButton, sprintf "确认“%s”(Enter)" confirmLabel)
        ToolTip.SetTip(confirmButton, sprintf "确认“%s”(Enter)" confirmLabel)
        Ui.preparePendingButton confirmButton
        setPending <- fun value ->
            pending <- value
            Ui.setButtonPending confirmButton value confirmLabel "正在保存…"
            Ui.setEnabled cancelButton (not value)
        let buttons = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Right)
        let wireArrowNavigation (leftBtn: Border) (rightBtn: Border) =
            leftBtn.KeyDown.Add(fun e ->
                if e.Key = Key.Right && rightBtn.IsEnabled then
                    e.Handled <- true
                    rightBtn.Focus(NavigationMethod.Directional) |> ignore)
            rightBtn.KeyDown.Add(fun e ->
                if e.Key = Key.Left && leftBtn.IsEnabled then
                    e.Handled <- true
                    leftBtn.Focus(NavigationMethod.Directional) |> ignore)
        wireArrowNavigation cancelButton confirmButton
        updateConfirmState <- fun () ->
            Ui.setEnabled confirmButton isConfirmEnabled
        updateValidity () |> ignore

        buttons.Children.Add cancelButton
        buttons.Children.Add confirmButton
        buttons.KeyDown.Add(fun e ->
            if e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog()
            elif (e.Key = Key.Enter || e.Key = Key.Space) && not cancelButton.IsFocused then
                e.Handled <- true
                submit ()
            elif (e.Key = Key.Enter || e.Key = Key.Space) && cancelButton.IsFocused then
                e.Handled <- true
                overlay.CloseDialog())
        let content =
            let fieldBox = Ui.vstack 0.0 [ shell :> Control; validation :> Control; helper :> Control ]
            Ui.vstack Tokens.space4 [ Ui.title title :> Control; fieldBox :> Control; buttons :> Control ]
        content.KeyDown.Add(fun e ->
            if e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog())
        overlay.ShowDialog(content :> Control, 420.0, onClosed = (fun () ->
            // 焦点恢复统一走 OverlayHost 的单记忆路径，这里只标记失活。
            active <- false))
        Dispatcher.UIThread.Post(fun () ->
            box.Focus() |> ignore
            box.SelectAll())

    /// 破坏性或常规操作确认，可显式指定打开时默认聚焦主按钮或取消按钮。
    let confirmWithFocus
        (overlay: OverlayHost)
        (title: string)
        (body: string)
        (confirmLabel: string)
        (onConfirm: unit -> unit)
        (defaultFocusDanger: bool)
        =
        let message =
            TextBlock(
                Text = body,
                FontSize = Tokens.fontBody,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = ReadingRhythm.uiBodyLineHeight)
        AutomationProperties.SetName(message, body)
        AutomationProperties.SetHelpText(message, body)
        let cancelAction () = overlay.CloseDialog()
        let confirmAction () =
            overlay.CloseDialog()
            onConfirm ()
        let buttons, dangerButton =
            actionRowWithDefault overlay confirmLabel Ui.Danger confirmAction defaultFocusDanger
        AutomationProperties.SetHelpText(dangerButton, sprintf "%s · %s" (ToolTip.GetTip dangerButton :?> string) body)
        let content =
            Ui.vstack
                Tokens.space4
                [ Ui.title title :> Control
                  message :> Control
                  buttons :> Control ]
        content.KeyDown.Add(fun e ->
            if e.Key = Key.Escape then
                e.Handled <- true
                cancelAction ()
            elif (e.Key = Key.Enter || e.Key = Key.Space) && not buttons.Children[0].IsFocused then
                e.Handled <- true
                confirmAction ())
        // 关闭后焦点由 OverlayHost 恢复到触发控件，无需第二套记忆。
        overlay.ShowDialog(content :> Control, 420.0)
        Dispatcher.UIThread.Post(fun () ->
            let target = if defaultFocusDanger then dangerButton else buttons.Children[0] :?> Border
            target.Focus(NavigationMethod.Directional) |> ignore)


    /// 破坏性操作确认：主按钮用 Danger 语气 + 警示 tooltip，打开后焦点直接落在它上面。
    let confirm (overlay: OverlayHost) (title: string) (body: string) (confirmLabel: string) (onConfirm: unit -> unit) =
        confirmWithFocus overlay title body confirmLabel onConfirm true

    /// 长文本编辑（编辑消息并分叉）。
    let editText (overlay: OverlayHost) (title: string) (initial: string) (confirmLabel: string) (onConfirm: string -> unit) =
        let shell, box = Ui.textArea "消息内容" 160.0
        box.Text <- initial
        let submit () =
            let value = if isNull box.Text then "" else box.Text
            overlay.CloseDialog()
            onConfirm value
        let buttons, confirmButton =
            actionRow overlay confirmLabel Ui.Primary submit
        ToolTip.SetTip(confirmButton, sprintf "确认“%s”(Ctrl+Enter)" confirmLabel)
        box.KeyDown.Add(fun e ->
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
            if e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog()
            elif e.Key = Key.Enter && ctrl then
                e.Handled <- true
                submit ())
        let content =
            Ui.vstack
                Tokens.space4
                [ Ui.title title :> Control
                  Ui.caption "会以你编辑后的内容新建一个分叉会话，原会话保持不变。" :> Control
                  shell :> Control
                  buttons :> Control ]
        content.KeyDown.Add(fun e ->
            if e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog())
        overlay.ShowDialog(content :> Control, 520.0)
        Dispatcher.UIThread.Post(fun () ->
            box.Focus() |> ignore
            box.CaretIndex <- (if isNull box.Text then 0 else box.Text.Length))

    /// 连接对话框：地址 + 令牌，或走配对码。
    let connect
        (overlay: OverlayHost)
        (defaultUrl: string)
        (onConnect: ConnectRequest -> unit)
        (onSubmitCode: string -> unit)
        : (string -> unit) =
        let urlField, urlBox = Ui.labeledField "服务器地址" "ws://127.0.0.1:8765/ws"
        urlBox.Text <- defaultUrl
        let tokenField, tokenBox = Ui.labeledField "访问令牌" "有令牌就粘贴，没有就用配对码"
        tokenBox.PasswordChar <- '●'

        let codeShell, codeBox = Ui.textField "6 位配对码"
        codeBox.MaxLength <- 6
        codeBox.FontSize <- Tokens.fontTitle
        codeBox.TextAlignment <- TextAlignment.Center
        codeBox.LetterSpacing <- 8.0
        codeBox.FontFamily <- Tokens.monoFontFamily
        let codeSection =
            Ui.vstack
                Tokens.space2
                [ Ui.fieldLabel "配对码" :> Control
                  codeShell :> Control
                  Ui.caption "配对码会打印在服务端终端（stderr），5 分钟内有效。" :> Control ]
        codeSection.IsVisible <- false

        let status =
            TextBlock(
                Text = "",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = false,
                LineHeight = ReadingRhythm.captionLineHeight)

        let submitCode () =
            let code = if isNull codeBox.Text then "" else codeBox.Text.Trim()
            if code.Length = 6 then onSubmitCode code
        codeBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                submitCode ()
            elif e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog())

        let codeButton = Ui.button Ui.Secondary "提交配对码" submitCode
        codeButton.IsVisible <- false

        let pairingToggle =
            Ui.button Ui.Ghost "没有令牌？改用配对码" (fun () ->
                let show = not codeSection.IsVisible
                codeSection.IsVisible <- show
                codeButton.IsVisible <- show
                if show then
                    onConnect
                        { url = (if isNull urlBox.Text then "" else urlBox.Text.Trim())
                          token = None
                          usePairing = true }
                    Dispatcher.UIThread.Post(fun () -> codeBox.Focus() |> ignore))
        pairingToggle.HorizontalAlignment <- HorizontalAlignment.Left

        let connectAction () =
            let url = if isNull urlBox.Text then "" else urlBox.Text.Trim()
            let token = if isNull tokenBox.Text then "" else tokenBox.Text.Trim()
            if String.IsNullOrWhiteSpace url then
                status.Text <- "请填写服务器地址。"
                status.IsVisible <- true
            else
                onConnect
                    { url = url
                      token = (if String.IsNullOrWhiteSpace token then None else Some token)
                      usePairing = false }

        urlBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                connectAction ()
            elif e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog())
        tokenBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                connectAction ()
            elif e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog())

        let content =
            Ui.vstack
                Tokens.space4
                [ Ui.vstack
                      Tokens.space1
                      [ Ui.title "连接万象服务器" :> Control
                        Ui.caption "服务端负责运行模型、保存会话与管理密钥；客户端只通过 WebSocket 与它通信。" :> Control ]
                  :> Control
                  urlField
                  tokenField
                  status :> Control
                  Ui.hairline () :> Control
                  pairingToggle :> Control
                  codeSection :> Control
                  codeButton :> Control
                  (let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Right)
                   let cancelBtn = Ui.button Ui.Ghost "稍后再说" (fun () -> overlay.CloseDialog())
                   AutomationProperties.SetName(cancelBtn, "稍后再说")
                   AutomationProperties.SetHelpText(cancelBtn, "取消并关闭连接对话框 (Esc)")
                   ToolTip.SetTip(cancelBtn, "取消并关闭连接对话框 (Esc)")
                   let connectBtn = Ui.button Ui.Primary "连接" connectAction
                   AutomationProperties.SetName(connectBtn, "连接")
                   AutomationProperties.SetHelpText(connectBtn, "连接至服务器 (Enter)")
                   ToolTip.SetTip(connectBtn, "连接至服务器 (Enter)")
                   row.Children.Add cancelBtn
                   row.Children.Add connectBtn
                   row.KeyDown.Add(fun e ->
                       if e.Key = Key.Escape then
                           e.Handled <- true
                           overlay.CloseDialog()
                       elif (e.Key = Key.Enter || e.Key = Key.Space) && not cancelBtn.IsFocused then
                           e.Handled <- true
                           connectAction ())
                   row :> Control) ]
        content.KeyDown.Add(fun e ->
            if e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog())
        overlay.ShowDialog(content :> Control, 460.0)
        Dispatcher.UIThread.Post(fun () -> urlBox.Focus() |> ignore)
        fun message ->
            status.Text <- message
            status.IsVisible <- not (String.IsNullOrWhiteSpace message)

    /// 会话设置：模型、生成参数、系统指令、工具勾选。
    let sessionSettings
        (overlay: OverlayHost)
        (catalog: Catalog)
        (current: SessionConfig)
        (onSave: SessionConfig -> (bool -> unit) -> unit)
        =
        let mutable dialogActive = true
        let mutable pending = false
        let mutable setPending: bool -> unit = ignore
        let mutable providerId = current.provider
        let mutable model = current.model

        let mutable setModelText: string -> unit = ignore
        let modelOptions () =
            [ for provider in Catalog.usableProviders catalog ->
                  provider.label,
                  [ for candidate in provider.models ->
                        MenuEntry.create candidate (fun () ->
                            providerId <- provider.id
                            model <- candidate
                            setModelText (sprintf "%s · %s" provider.label candidate))
                        |> MenuEntry.markSelected (provider.id = providerId && candidate = model) ] ]
        let modelButton, setText =
            Menu.selectButton overlay (Catalog.describeModel providerId model catalog) modelOptions
        setModelText <- setText
        modelButton.HorizontalAlignment <- HorizontalAlignment.Stretch

        let instructionsShell, instructionsBox = Ui.textArea "留空则使用服务端默认指令" 96.0
        instructionsBox.Text <- current.instructions |> Option.defaultValue ""

        let _, temperatureBox = Ui.textField "0–2，留空跟随默认"
        temperatureBox.Text <-
            match current.temperature with
            | Some t -> t.ToString("0.##", Globalization.CultureInfo.InvariantCulture)
            | None -> ""
        let _, topPBox = Ui.textField "0–1，留空跟随默认"
        topPBox.Text <-
            match current.topP with
            | Some p -> p.ToString("0.##", Globalization.CultureInfo.InvariantCulture)
            | None -> ""
        let _, maxTokensBox = Ui.textField "留空不限制"
        maxTokensBox.Text <- (match current.maxTokens with Some m -> string m | None -> "")
        let _, thinkingBudgetBox = Ui.textField "留空跟随默认；0 关闭思维链"
        thinkingBudgetBox.Text <- (match current.thinkingBudget with Some b -> string b | None -> "")

        // 隧道监听 Escape：当子控件（输入框等）拥有焦点时，若当前有文本选中则清除选中，
        // 无选中时按 Escape 立即关闭对话框，避免子控件吞掉 Escape 事件。
        let wireEscapeForSubBox (box: TextBox) =
            box.AddHandler(
                InputElement.KeyDownEvent,
                EventHandler<KeyEventArgs>(fun _ e ->
                    if e.Key = Key.Escape then
                        if box.SelectionStart <> box.SelectionEnd then
                            e.Handled <- true
                            box.ClearSelection()
                        else
                            e.Handled <- true
                            overlay.CloseDialog()),
                RoutingStrategies.Tunnel)

        for box in [ instructionsBox; temperatureBox; topPBox; maxTokensBox; thinkingBudgetBox ] do
            wireEscapeForSubBox box

        let selectedTools = System.Collections.Generic.HashSet<string>(current.tools)
        let toolsPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space1)
        if List.isEmpty catalog.tools then
            toolsPanel.Children.Add(Ui.caption "服务端当前没有可用工具。")
        else
            for tool in catalog.tools do
                let toggle, _, _, _ =
                    Ui.toggle
                        (selectedTools.Contains tool.id)
                        (fun value -> if value then selectedTools.Add tool.id |> ignore else selectedTools.Remove tool.id |> ignore)
                Avalonia.Automation.AutomationProperties.SetName(toggle, sprintf "启用工具 %s" tool.label)
                let caption =
                    TextBlock(
                        Text = tool.label,
                        FontSize = Tokens.fontSmall,
                        Foreground = Tokens.text,
                        VerticalAlignment = VerticalAlignment.Center)
                let source = Ui.tag (if tool.source = "mcp" then tool.serverId |> Option.defaultValue "mcp" else "内置")
                let left = Ui.hstack Tokens.space2 [ caption :> Control; source :> Control ]
                let row = DockPanel(LastChildFill = true)
                DockPanel.SetDock(toggle, Dock.Right)
                row.Children.Add toggle
                row.Children.Add left
                toolsPanel.Children.Add row

        let save () =
            for box in [ temperatureBox; topPBox; maxTokensBox; thinkingBudgetBox ] do Ui.clearFieldError box
            let mutable firstInvalid: TextBox option = None
            let invalid (box: TextBox) message =
                Ui.setFieldError box message
                if firstInvalid.IsNone then firstInvalid <- Some box
            let parseFloat (box: TextBox) (valid: float -> bool) message =
                let raw = if isNull box.Text then "" else box.Text.Trim()
                if String.IsNullOrWhiteSpace raw then None
                else
                    match Double.TryParse(raw, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                    | true, value when valid value -> Some value
                    | _ ->
                        invalid box message
                        None
            let parseInt (box: TextBox) (valid: int -> bool) message =
                let raw = if isNull box.Text then "" else box.Text.Trim()
                if String.IsNullOrWhiteSpace raw then None
                else
                    match Int32.TryParse raw with
                    | true, value when valid value -> Some value
                    | _ ->
                        invalid box message
                        None
            let temperature = parseFloat temperatureBox (fun v -> v >= 0.0 && v <= 2.0) "请输入 0–2 之间的数字。"
            let topP = parseFloat topPBox (fun v -> v >= 0.0 && v <= 1.0) "请输入 0–1 之间的数字。"
            let maxTokens = parseInt maxTokensBox (fun v -> v > 0) "请输入正整数。"
            let thinkingBudget = parseInt thinkingBudgetBox (fun v -> v >= 0) "请输入 0 或正整数。"
            let instructions =
                let text = if isNull instructionsBox.Text then "" else instructionsBox.Text.Trim()
                if String.IsNullOrWhiteSpace text then None else Some text
            match firstInvalid with
            | Some box ->
                box.BringIntoView()
                box.Focus(NavigationMethod.Directional) |> ignore
                Dispatcher.UIThread.Post(fun () ->
                    if box.IsEffectivelyVisible && box.IsEnabled then
                        box.BringIntoView()
                        box.Focus(NavigationMethod.Directional) |> ignore)
            | None ->
                if not pending then
                    setPending true
                    onSave
                        { current with
                            provider = providerId
                            model = model
                            instructions = instructions
                            temperature = temperature
                            topP = topP
                            maxTokens = maxTokens
                            thinkingBudget = thinkingBudget
                            tools = List.ofSeq selectedTools }
                        (fun ok ->
                            if dialogActive then
                                setPending false
                                if ok then overlay.CloseDialog())

        let paramGrid = Grid(ColumnSpacing = Tokens.space3, RowSpacing = Tokens.space3)
        paramGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        paramGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        for _ in 1 .. 4 do paramGrid.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        let temperatureColumn = Ui.inputFieldGroup "Temperature" "0–2，留空跟随服务端默认" temperatureBox
        let topPColumn = Ui.inputFieldGroup "Top P" "0–1，留空跟随服务端默认" topPBox
        let maxTokensColumn = Ui.inputFieldGroup "最大输出 token" "限制单次回复，留空不限制" maxTokensBox
        let thinkingBudgetColumn = Ui.inputFieldGroup "思维链预算（token）" "0 关闭思维链，留空跟随默认" thinkingBudgetBox
        paramGrid.Children.Add temperatureColumn
        paramGrid.Children.Add topPColumn
        paramGrid.Children.Add maxTokensColumn
        paramGrid.Children.Add thinkingBudgetColumn
        let applyParamLayout width =
            let single = width > 0.0 && width < LayoutPolicy.formSingleColumnBreakpoint
            paramGrid.ColumnDefinitions[1].Width <-
                if single then GridLength(0.0) else GridLength(1.0, GridUnitType.Star)
            let place (control: Control) row column =
                Grid.SetRow(control, row)
                Grid.SetColumn(control, column)
            if single then
                place temperatureColumn 0 0
                place topPColumn 1 0
                place maxTokensColumn 2 0
                place thinkingBudgetColumn 3 0
            else
                place temperatureColumn 0 0
                place topPColumn 0 1
                place maxTokensColumn 1 0
                place thinkingBudgetColumn 1 1
        paramGrid.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then applyParamLayout paramGrid.Bounds.Width)

        let cancelButton = Ui.button Ui.Ghost "取消" (fun () -> overlay.CloseDialog())
        AutomationProperties.SetName(cancelButton, "取消")
        AutomationProperties.SetHelpText(cancelButton, "取消并关闭对话框 (Esc)")
        ToolTip.SetTip(cancelButton, "取消并关闭对话框 (Esc)")
        let saveButton = Ui.button Ui.Primary "保存" save
        AutomationProperties.SetName(saveButton, "保存")
        AutomationProperties.SetHelpText(saveButton, "保存会话设置 (Ctrl+Enter)")
        ToolTip.SetTip(saveButton, "保存会话设置 (Ctrl+Enter)")
        Ui.preparePendingButton saveButton
        setPending <- fun value ->
            pending <- value
            Ui.setButtonPending saveButton value "保存" "正在保存…"
            Ui.setEnabled cancelButton (not value)
        let footer = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Right)
        footer.Children.Add cancelButton
        footer.Children.Add saveButton
        footer.KeyDown.Add(fun e ->
            if e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog()
            elif (e.Key = Key.Enter || e.Key = Key.Space) && not cancelButton.IsFocused then
                e.Handled <- true
                save ())
        let content =
            Ui.vstack
                Tokens.space4
                [ Ui.vstack Tokens.space1 [ Ui.title "会话设置" :> Control; Ui.caption "只影响当前会话，不改服务端默认值。" :> Control ] :> Control
                  Ui.controlFieldGroup "模型" "" (modelButton :> Control)
                  Ui.controlFieldGroup "系统指令" "" (instructionsShell :> Control)
                  paramGrid :> Control
                  Ui.hairline () :> Control
                  Ui.vstack Tokens.space2 [ Ui.sectionLabel "可用工具" :> Control; toolsPanel :> Control ] :> Control
                  Ui.hairline () :> Control
                  footer :> Control ]
        content.KeyDown.Add(fun e ->
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
            if e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog()
            elif e.Key = Key.Enter && ctrl then
                e.Handled <- true
                save ())
        let scroller =
            ScrollViewer(
                Content = content,
                MaxHeight = LayoutPolicy.dialogContentMaxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        applyParamLayout paramGrid.Bounds.Width
        overlay.ShowDialog(scroller :> Control, 520.0, onClosed = (fun () ->
            dialogActive <- false))

    /// 快捷键帮助（分类清晰、对标桌面端成熟软件）。
    let shortcuts (overlay: OverlayHost) =
        let sections =
            [ "全局与导航",
              [ "Ctrl / ⌘ + N", "新建会话"
                "Ctrl / ⌘ + B", "切换侧边栏展开 / 折叠"
                "Ctrl / ⌘ + K", "快速聚焦搜索栏"
                "Ctrl / ⌘ + 1 ~ 9", "快速跳转至对应会话"
                "Ctrl / ⌘ + ,", "打开全局设置"
                "Ctrl / ⌘ + Shift + S", "切换深色 / 浅色主题"
                "Ctrl / ⌘ + /", "显示快捷键帮助" ]
              "输入与会话",
              [ "Enter", "发送消息（或换行，按偏好）"
                "Shift + Enter", "换行输入"
                "Ctrl / ⌘ + Enter", "强制发送消息"
                "Ctrl / ⌘ + Shift + E", "导出当前会话为 Markdown"
                "Esc", "关闭弹层 / 取消编辑 / 停止生成" ] ]
        let contentPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space3)
        for (category, items) in sections do
            contentPanel.Children.Add(Ui.sectionLabel category :> Control)
            let groupRows = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space1)
            for (keys, description) in items do
                let key =
                    Border(
                        Background = Tokens.surface,
                        BorderBrush = Tokens.border,
                        BorderThickness = Thickness 1.0,
                        CornerRadius = CornerRadius Tokens.radiusSm,
                        Padding = Thickness(Tokens.space2, 2.0),
                        MinWidth = 136.0,
                        Child =
                            TextBlock(
                                Text = keys,
                                FontSize = Tokens.fontMicro,
                                FontFamily = Tokens.monoFontFamily,
                                Foreground = Tokens.textMuted,
                                HorizontalAlignment = HorizontalAlignment.Center))
                let caption =
                    TextBlock(
                        Text = description,
                        FontSize = Tokens.fontSmall,
                        Foreground = Tokens.text,
                        VerticalAlignment = VerticalAlignment.Center)
                groupRows.Children.Add(Ui.hstack Tokens.space3 [ key :> Control; caption :> Control ])
            contentPanel.Children.Add groupRows
        let closeBtn = Ui.button Ui.Primary "关闭" (fun () -> overlay.CloseDialog())
        AutomationProperties.SetName(closeBtn, "关闭")
        AutomationProperties.SetHelpText(closeBtn, "关闭快捷键帮助 (Esc / Enter)")
        ToolTip.SetTip(closeBtn, "关闭快捷键帮助 (Esc / Enter)")
        closeBtn.HorizontalAlignment <- HorizontalAlignment.Right
        let content = Ui.vstack Tokens.space4 [ Ui.title "键盘快捷键" :> Control; contentPanel :> Control; closeBtn :> Control ]
        content.KeyDown.Add(fun e ->
            if e.Key = Key.Escape || e.Key = Key.Enter || (e.Key = Key.Space && closeBtn.IsFocused) then
                e.Handled <- true
                overlay.CloseDialog())
        let scroller =
            ScrollViewer(
                Content = content,
                MaxHeight = LayoutPolicy.dialogContentMaxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        content.Focusable <- true
        closeBtn.Focusable <- true
        overlay.ShowDialog(scroller :> Control, 460.0)
        Dispatcher.UIThread.Post(fun () ->
            if closeBtn.IsEffectivelyVisible && closeBtn.IsEnabled then
                closeBtn.Focus() |> ignore
            elif content.IsEffectivelyVisible && content.IsEnabled then
                content.Focus() |> ignore)
