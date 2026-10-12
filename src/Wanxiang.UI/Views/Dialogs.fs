namespace Wanxiang.UI
open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Input.TextInput
open Avalonia.Layout

open Avalonia.Media
open Avalonia.Threading
open Avalonia.Automation
open Wanxiang.Core
open Wanxiang.Client

/// 连接对话框返回的两个入口：`info` 报中性进度，`fail` 报类型化的失败。
/// 失败的重试按钮由 `ConnectionIssue.retry` 决定，与文案无关。
type ConnectDialogHandle = {
    info: string -> unit
    fail: ConnectionIssue -> unit
}

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
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Spacing.spaceMd, HorizontalAlignment = HorizontalAlignment.Right)
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
        // Enter 归属：按钮级（Ui.onClick 在每个按钮上绑 Enter/Space）是唯一的确认路径；
        // 行级 Enter/Space 只是非破坏性对话框的便利（焦点在按钮行内即确认）。
        // Danger 语气不设行级确认：破坏性操作必须由用户显式落在确认按钮上激活，
        // 避免内容区冒泡上来的 Enter 被误确认。Escape 不在这里处理，统一走 OverlayHost.HandleEscape。
        row.KeyDown.Add(fun e ->
            if tone <> Ui.Danger then
                if (e.Key = Key.Enter || e.Key = Key.Space) && not cancelButton.IsFocused then
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
        helper.Margin <- Thickness(ControlMetrics.fieldInsetX, Spacing.spaceXs, 0.0, 0.0)
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
                submit ())
        // Escape 不在这里处理，统一走 OverlayHost.HandleEscape（提交 pending 中会被守卫拦截）。
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
        let buttons = StackPanel(Orientation = Orientation.Horizontal, Spacing = Spacing.spaceMd, HorizontalAlignment = HorizontalAlignment.Right)
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
            // 非破坏性 prompt 保留行级 Enter 确认（焦点在按钮行内即提交）；Escape 走 HandleEscape。
            if (e.Key = Key.Enter || e.Key = Key.Space) && not cancelButton.IsFocused then
                e.Handled <- true
                submit ()
            elif (e.Key = Key.Enter || e.Key = Key.Space) && cancelButton.IsFocused then
                e.Handled <- true
                overlay.CloseDialog())
        let content =
            let fieldBox = Ui.vstack 0.0 [ shell :> Control; validation :> Control; helper :> Control ]
            Ui.vstack Spacing.space2xl [ Ui.title title :> Control; fieldBox :> Control; buttons :> Control ]
        overlay.ShowDialog(content :> Control, 420.0, onClosed = (fun () ->
            // 焦点恢复统一走 OverlayHost 的单记忆路径，这里只标记失活。
            active <- false),
            canDismiss = (fun () -> not pending))
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
        let confirmAction () =
            overlay.CloseDialog()
            onConfirm ()
        let buttons, dangerButton =
            actionRowWithDefault overlay confirmLabel Ui.Danger confirmAction defaultFocusDanger
        AutomationProperties.SetHelpText(dangerButton, sprintf "%s · %s" (ToolTip.GetTip dangerButton :?> string) body)
        let content =
            Ui.vstack
                Spacing.space2xl
                [ Ui.title title :> Control
                  message :> Control
                  buttons :> Control ]
        // 破坏性确认无内容级 Enter/Space 快捷：必须显式聚焦确认按钮再激活；
        // 默认焦点落在取消（安全项）上；Escape 统一走 OverlayHost.HandleEscape。
        // 关闭后焦点由 OverlayHost 恢复到触发控件，无需第二套记忆。
        overlay.ShowDialog(content :> Control, 420.0)
        Dispatcher.UIThread.Post(fun () ->
            let target = if defaultFocusDanger then dangerButton else buttons.Children[0] :?> Border
            target.Focus(NavigationMethod.Directional) |> ignore)


    /// 破坏性操作确认：主按钮用 Danger 语气 + 警示 tooltip，默认焦点落在取消（安全项）上。
    let confirm (overlay: OverlayHost) (title: string) (body: string) (confirmLabel: string) (onConfirm: unit -> unit) =
        confirmWithFocus overlay title body confirmLabel onConfirm false

    /// 长文本编辑（编辑消息并分叉）。
    let editText (overlay: OverlayHost) (title: string) (initial: string) (confirmLabel: string) (onConfirm: string -> unit) =
        let shell, box = Ui.textArea "消息内容" ControlMetrics.textAreaLongMinHeight
        box.Text <- initial
        let submit () =
            let value = if isNull box.Text then "" else box.Text
            overlay.CloseDialog()
            onConfirm value
        let buttons, confirmButton =
            actionRow overlay confirmLabel Ui.Primary submit
        AutomationProperties.SetHelpText(confirmButton, sprintf "确认“%s”(Ctrl+Enter)" confirmLabel)
        ToolTip.SetTip(confirmButton, sprintf "确认“%s”(Ctrl+Enter)" confirmLabel)
        box.KeyDown.Add(fun e ->
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
            if e.Key = Key.Enter && ctrl then
                e.Handled <- true
                submit ())
        let content =
            Ui.vstack
                Spacing.space2xl
                [ Ui.title title :> Control
                  Ui.caption "会以你编辑后的内容新建一个分叉会话，原会话保持不变。" :> Control
                  shell :> Control
                  buttons :> Control ]
        overlay.ShowDialog(content :> Control, 520.0)
        Dispatcher.UIThread.Post(fun () ->
            box.Focus() |> ignore
            box.CaretIndex <- (if isNull box.Text then 0 else box.Text.Length))

    /// 类型化连接错误的用户可见文案：发生了什么 / 为什么 / 下一步。
    /// 只用于展示；是否出现重试按钮看 `ConnectionIssue.retry`，不看这里的字。
    let describeIssue (issue: ConnectionIssue) : string =
        match issue with
        | Rejected _ -> "主机拒绝了连接。可能没有在运行，或地址、端口不对。检查地址后再试。"
        | TimedOut -> "连接超时。主机没有及时回应，可能网络不通或主机在休眠。确认主机在线后再试。"
        | TokenInvalid -> "这台设备的授权已失效。令牌无效或已被吊销。请重新配对。"
        | PairingFrozen minutes ->
            sprintf "配对已冻结 %d 分钟。连续输错次数过多。稍后再试，或请已连接的桌面端为这台设备发配对码。" minutes
        | PairingRejected reason ->
            let why =
                match reason with
                | "invalid pairing code" -> "配对码不对"
                | "pairing code expired" -> "配对码已过期"
                | "pairing code already used" -> "配对码已被使用"
                | "no pairing session active" -> "没有进行中的配对"
                | other -> other
            sprintf "配对没有成功：%s。请核对 6 位配对码后再试一次。" why
        | Disconnected detail ->
            if String.IsNullOrWhiteSpace detail then "连接已断开。网络中断或主机已关闭。检查后重新连接。"
            else sprintf "连接已断开：%s。网络中断或主机已关闭。检查后重新连接。" detail

    /// 重试按钮的文案（只对应动作，不对应错误文案）。
    let retryLabel (retry: ConnectionRetry) : string =
        match retry with
        | RetryConnect -> "重试连接"
        | RePair -> "重新配对"
        | RetryPairing -> "重试配对"

    /// 地址是否指向本机（决定配对码提示该怎么拿）。
    let isLoopbackUrl (url: string) : bool =
        match Uri.TryCreate((if isNull url then "" else url.Trim()), UriKind.Absolute) with
        | true, uri ->
            uri.IsLoopback || uri.Host = "localhost"
        | _ -> false

    /// 配对码从哪里拿：本机与远端不同。
    let pairingCodeHint (url: string) : string =
        if isLoopbackUrl url then
            "在运行万象的这台机器上：打开桌面应用的「设置 → 设备 → 添加设备」，或查看启动万象的终端输出。5 分钟内有效。"
        else
            "这是远端主机：请在已连接的桌面应用里打开「设置 → 设备 → 添加设备」，会显示 6 位配对码。主机没有桌面应用时，查看它的终端输出。5 分钟内有效。"

    /// 连接对话框：地址 + 令牌，或走配对码。
    let connectWithClosed
        (overlay: OverlayHost)
        (defaultUrl: string)
        (onConnect: ConnectRequest -> unit)
        (onSubmitCode: string -> unit)
        (onClosed: unit -> unit)
        : ConnectDialogHandle =
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
        // 触屏：弹出数字键盘并把回车键标为“提交”（浏览器后端映射为 inputmode / enterkeyhint）。
        TextInputOptions.SetContentType(codeBox, TextInputContentType.Digits)
        TextInputOptions.SetReturnKeyType(codeBox, TextInputReturnKeyType.Done)
        let pairingHint = Ui.caption (pairingCodeHint defaultUrl)
        let codeSectionInner =
            Ui.vstack
                Spacing.spaceMd
                [ Ui.fieldLabel "配对码" :> Control
                  codeShell :> Control
                  pairingHint :> Control ]
                // 配对码区是对话框内的分组表面：surfaceContainer + 最轻发丝边，与主字段区分层。
        let codeSection =
            Ui.groupingCard (Thickness(Spacing.spaceXl, Spacing.spaceMd)) codeSectionInner Spacing.Radius.md
        codeSection.IsVisible <- false

        let status =
            TextBlock(
                Text = "",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = false,
                LineHeight = ReadingRhythm.captionLineHeight)
        // 连接错误走 assertive live region：与行内字段错误（Ui.setFieldError）同等的读屏待遇。
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive)

        let mutable connectActionRef: unit -> unit = ignore
        let mutable submitCodeRef: unit -> unit = ignore
        let mutable startPairingRef: unit -> unit = ignore
        /// 当前失败对应的重试动作（来自 `ConnectionIssue.retry`，不由文案推断）。
        let mutable pendingRetry: ConnectionRetry option = None

        let retryButton =
            Ui.button Ui.Secondary "重试连接" (fun () ->
                match pendingRetry with
                | Some RetryConnect -> connectActionRef ()
                | Some RePair ->
                    // 令牌已失效：旧令牌留着只会再次被拒绝
                    tokenBox.Text <- ""
                    startPairingRef ()
                | Some RetryPairing ->
                    let code = if isNull codeBox.Text then "" else codeBox.Text.Trim()
                    if code.Length = 6 then submitCodeRef () else startPairingRef ()
                | None -> ())
        retryButton.IsVisible <- false
        retryButton.HorizontalAlignment <- HorizontalAlignment.Left

        let showMessage (message: string) (failure: bool) =
            status.Text <- message
            let hasMessage = not (String.IsNullOrWhiteSpace message)
            status.IsVisible <- hasMessage
            AutomationProperties.SetName(status, message)
            status.Foreground <- (if failure && hasMessage then Tokens.danger else Tokens.textMuted)

        /// 中性进度/校验提示：不出现重试按钮。
        let announce message =
            pendingRetry <- None
            showMessage message false
            retryButton.IsVisible <- false

        /// 类型化失败：红字 + 由类型决定的重试按钮。
        let fail (issue: ConnectionIssue) =
            showMessage (describeIssue issue) true
            pendingRetry <- ConnectionIssue.retry issue
            match pendingRetry with
            | Some retry ->
                Ui.setButtonText retryButton (retryLabel retry)
                retryButton.IsVisible <- true
            | None -> retryButton.IsVisible <- false

        let submitCode () =
            let code = if isNull codeBox.Text then "" else codeBox.Text.Trim()
            if code.Length = 6 then onSubmitCode code
            else announce "请输入 6 位配对码。"
        submitCodeRef <- submitCode
        codeBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                submitCode ())
        // 配对码只含数字：粘贴“123 456”“123-456”或带全角数字时归一化为纯数字，
        // 凑满 6 位即自动提交，手机上不必再去够“提交配对码”按钮。
        let mutable normalizingCode = false
        // TextChanged 会在布局重排时重放同一文本：同一串 6 位码只自动提交一次，改动后才可再次触发。
        let mutable autoSubmitted = ""
        codeBox.TextChanged.Add(fun _ ->
            if not normalizingCode then
                let raw = if isNull codeBox.Text then "" else codeBox.Text
                let digits =
                    raw
                    |> Seq.filter Char.IsDigit
                    |> Seq.map (fun ch -> char (int '0' + int (Char.GetNumericValue ch)))
                    |> Seq.truncate 6
                    |> Seq.toArray
                    |> String
                if digits <> raw then
                    normalizingCode <- true
                    codeBox.Text <- digits
                    codeBox.CaretIndex <- digits.Length
                    normalizingCode <- false
                if digits.Length = 6 then
                    if digits <> autoSubmitted then
                        autoSubmitted <- digits
                        submitCode ()
                else
                    autoSubmitted <- "")

        let codeButton = Ui.button Ui.Secondary "提交配对码" submitCode
        codeButton.IsVisible <- false

        let startPairing () =
            codeSection.IsVisible <- true
            codeButton.IsVisible <- true
            pairingHint.Text <- pairingCodeHint (if isNull urlBox.Text then "" else urlBox.Text)
            onConnect
                { url = (if isNull urlBox.Text then "" else urlBox.Text.Trim())
                  token = None
                  usePairing = true }
            Dispatcher.UIThread.Post(fun () -> codeBox.Focus() |> ignore)
        startPairingRef <- startPairing
        urlBox.TextChanged.Add(fun _ -> pairingHint.Text <- pairingCodeHint urlBox.Text)

        let pairingToggle =
            Ui.button Ui.Ghost "没有令牌？改用配对码" (fun () ->
                let show = not codeSection.IsVisible
                if show then startPairing ()
                else
                    codeSection.IsVisible <- false
                    codeButton.IsVisible <- false)
        pairingToggle.HorizontalAlignment <- HorizontalAlignment.Left

        let connectAction () =
            let url = if isNull urlBox.Text then "" else urlBox.Text.Trim()
            let token = if isNull tokenBox.Text then "" else tokenBox.Text.Trim()
            if String.IsNullOrWhiteSpace url then
                announce "请填写服务器地址。"
            else
                onConnect
                    { url = url
                      token = (if String.IsNullOrWhiteSpace token then None else Some token)
                      usePairing = false }
        connectActionRef <- connectAction

        urlBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                connectAction ())
        tokenBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                connectAction ())

        // 页脚走统一 actionRow：右对齐节律、取消只关闭、左右键在两按钮间移动，与全对话框一致。
        let footerRow, connectBtn = actionRow overlay "连接" Ui.Primary connectAction
        // 连接对话框沿用统一取消语义（只关闭、不销毁），保留更具体的提示文案。
        match footerRow.Children[0] with
        | :? Border as cancelBtn ->
            AutomationProperties.SetHelpText(cancelBtn, "取消并关闭连接对话框 (Esc)")
            ToolTip.SetTip(cancelBtn, "取消并关闭连接对话框 (Esc)")
        | _ -> ()
        AutomationProperties.SetName(connectBtn, "连接")
        AutomationProperties.SetHelpText(connectBtn, "连接至服务器 (Enter)")
        ToolTip.SetTip(connectBtn, "连接至服务器 (Enter)")

        let content =
            Ui.vstack
                Spacing.space2xl
                [ Ui.vstack
                      Spacing.spaceXs
                      [ Ui.title "连接万象服务器" :> Control
                        Ui.caption "服务端负责运行模型、保存会话与管理密钥；客户端只通过 WebSocket 与它通信。" :> Control ]
                  :> Control
                  urlField
                  tokenField
                  status :> Control
                  retryButton :> Control
                  Border(Height = ControlMetrics.borderWidth, Background = Tokens.hairline, HorizontalAlignment = HorizontalAlignment.Stretch) :> Control
                  pairingToggle :> Control
                  codeSection :> Control
                  codeButton :> Control
                  footerRow :> Control ]
        // Escape 统一走 OverlayHost.HandleEscape，这里不设局部处理。
        // 首焦落在第一个还缺内容的字段上：地址已记住（defaultUrl 非空）时
        // 再聚焦地址框只是让用户多按一次 Tab，真正要填的是令牌。
        // 借 kelivo 对话框 autofocus 落在「最需要补齐的那一项」的做法，
        // 但只在两个字段间选：不猜配对码区（要显式切换才出现）。
        //
        // 经 ShowDialog 的 initialFocus 指定，而不是自己再 Post 一次：
        // 自己 Post 会与 focusFirst 的出队顺序打架（真机复现过：环落在
        // 「没有令牌？改用配对码」上，而不是目标字段）。
        let target =
            if String.IsNullOrWhiteSpace (if isNull urlBox.Text then "" else urlBox.Text) then urlBox else tokenBox
        overlay.ShowDialog(content :> Control, 460.0, initialFocus = target, onClosed = onClosed)
        { info = announce; fail = fail }

    let connect
        (overlay: OverlayHost)
        (defaultUrl: string)
        (onConnect: ConnectRequest -> unit)
        (onSubmitCode: string -> unit)
        : ConnectDialogHandle =
        connectWithClosed overlay defaultUrl onConnect onSubmitCode ignore


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
        let mutable revealParameters: unit -> unit = ignore

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

        let instructionsShell, instructionsBox = Ui.textArea "留空则使用服务端默认指令" ControlMetrics.textAreaMinHeight
        instructionsBox.Text <- current.instructions |> Option.defaultValue ""

        // 表单错误摘要：与 SettingsGeneral / SettingsTools 同款的可聚焦 live region，
        // 多错时由 Ui.applyValidationFeedback 汇总呈现，单错时收起。
        let errorSummary =
            TextBlock(
                Text = "",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.danger,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = ReadingRhythm.captionLineHeight,
                IsVisible = false,
                Focusable = true)
        AutomationProperties.SetName(errorSummary, "表单错误摘要")
        AutomationProperties.SetLiveSetting(errorSummary, AutomationLiveSetting.Assertive)

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
        // 用户动手修正任一字段即收起摘要：与字段行内「一改即清」同一时机，
        // 不再让 assertive live region 播报已经修正的旧错误。
        Ui.bindValidationSummaryReset
            (Some errorSummary)
            [ temperatureBox; topPBox; maxTokensBox; thinkingBudgetBox ]

        // Escape 统一走 OverlayHost.HandleEscape（提交 pending 中会被守卫拦截），
        // 这里不再为输入框设隧道 Esc 处理。

        let selectedTools = System.Collections.Generic.HashSet<string>(current.tools)
        let toolsPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceXs)
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
                let left = Ui.hstack Spacing.spaceMd [ caption :> Control; source :> Control ]
                let row = DockPanel(LastChildFill = true)
                DockPanel.SetDock(toggle, Dock.Right)
                row.Children.Add toggle
                row.Children.Add left
                toolsPanel.Children.Add row

        let save () =
            for box in [ temperatureBox; topPBox; maxTokensBox; thinkingBudgetBox ] do Ui.clearFieldError box
            errorSummary.IsVisible <- false
            errorSummary.Text <- ""
            let errors = ResizeArray<TextBox * string>()
            let invalid (box: TextBox) message =
                Ui.setFieldError box message
                errors.Add(box, message)
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
            if List.isEmpty (List.ofSeq errors) then
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
                                if ok then overlay.CloseDialog()
                                else overlay.Toast("保存失败，请重试。", Failure))
            else
                revealParameters ()
                Ui.applyValidationFeedback (List.ofSeq errors) (Some errorSummary) (fun message -> overlay.Toast(message, Warning))

        let temperatureColumn = Ui.inputFieldGroup "Temperature" "0–2，留空跟随服务端默认" temperatureBox
        let topPColumn = Ui.inputFieldGroup "Top P" "0–1，留空跟随服务端默认" topPBox
        let maxTokensColumn = Ui.inputFieldGroup "最大输出 token" "限制单次回复，留空不限制" maxTokensBox
        let thinkingBudgetColumn = Ui.inputFieldGroup "思维链预算（token）" "0 关闭思维链，留空跟随默认" thinkingBudgetBox
        let paramGrid, applyParamLayout = Ui.twoColumnForm Spacing.space2xl Spacing.spaceXl 4 [ temperatureColumn; topPColumn; maxTokensColumn; thinkingBudgetColumn ]
        let parameterSummary =
            if current.temperature.IsSome || current.topP.IsSome || current.maxTokens.IsSome || current.thinkingBudget.IsSome then
                "生成参数 · 已自定义"
            else "生成参数 · 跟随默认值"
        let advanced =
            Expander(
                Header = Ui.title parameterSummary,
                Content = paramGrid,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ContentTransition = null)
        AutomationProperties.SetName(advanced, "生成参数")
        revealParameters <- fun () -> advanced.IsExpanded <- true
        let tools =
            Expander(
                Header = Ui.title (sprintf "工具 · 已启用 %d 项" selectedTools.Count),
                Content = toolsPanel,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ContentTransition = null)
        AutomationProperties.SetName(tools, "会话工具")
        // 计数在展开/收起时更新，不重建工具开关，也不丢键盘位置。
        tools.PropertyChanged.Add(fun e ->
            if e.Property = Expander.IsExpandedProperty then
                tools.Header <- Ui.title (sprintf "工具 · 已启用 %d 项" selectedTools.Count))

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
        let footer = StackPanel(Orientation = Orientation.Horizontal, Spacing = Spacing.spaceMd, HorizontalAlignment = HorizontalAlignment.Right)
        footer.Children.Add cancelButton
        footer.Children.Add saveButton
        // 页脚加入左右键导航节律：与其他对话框按钮行一致，Left/Right 在两按钮间移动。
        cancelButton.KeyDown.Add(fun e ->
            if e.Key = Key.Right && saveButton.IsEnabled then
                e.Handled <- true
                saveButton.Focus(NavigationMethod.Directional) |> ignore)
        saveButton.KeyDown.Add(fun e ->
            if e.Key = Key.Left && cancelButton.IsEnabled then
                e.Handled <- true
                cancelButton.Focus(NavigationMethod.Directional) |> ignore)
        footer.KeyDown.Add(fun e ->
            // 非破坏性设置页保留行级 Enter 确认；Escape 统一走 OverlayHost.HandleEscape。
            if (e.Key = Key.Enter || e.Key = Key.Space) && not cancelButton.IsFocused then
                e.Handled <- true
                save ())
        let content =
            Ui.vstack
                Spacing.space2xl
                [ errorSummary :> Control
                  Ui.controlFieldGroup "模型" "" (modelButton :> Control)
                  Ui.controlFieldGroup "系统指令" "" (instructionsShell :> Control)
                  tools :> Control
                  advanced :> Control ]
        let dialog = DockPanel(LastChildFill = true, MaxHeight = LayoutPolicy.dialogContentMaxHeight)
        let heading =
            Ui.vstack Spacing.spaceXs
                [ Ui.heading "会话上下文" :> Control
                  Ui.caption "只影响当前会话。保存后用于后续请求，不改动已有消息。" :> Control ]
        heading.Margin <- Thickness(0.0, 0.0, 0.0, Spacing.space2xl)
        footer.Margin <- Thickness(0.0, Spacing.space2xl, 0.0, 0.0)
        DockPanel.SetDock(heading, Dock.Top)
        DockPanel.SetDock(footer, Dock.Bottom)
        dialog.Children.Add heading
        dialog.Children.Add footer
        dialog.KeyDown.Add(fun e ->
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
            // Ctrl+Enter 保存；Escape 统一走 OverlayHost.HandleEscape。
            if e.Key = Key.Enter && ctrl then
                e.Handled <- true
                save ())
        let scroller =
            ScrollViewer(
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        dialog.Children.Add scroller
        applyParamLayout ()
        overlay.ShowDialog(dialog :> Control, 520.0, scrollWholeContent = false, onClosed = (fun () ->
            dialogActive <- false),
            canDismiss = (fun () -> not pending))

    /// 快捷键帮助（分类清晰、对标桌面端成熟软件）。
    let shortcuts (overlay: OverlayHost) =
        let sections =
            [ "全局与导航",
              [ "Ctrl / ⌘ + N", "新建会话"
                "Ctrl / ⌘ + W", "关闭窗口"
                "Ctrl / ⌘ + B", "切换侧边栏展开 / 折叠"
                "Ctrl / ⌘ + K", "快速聚焦搜索栏"
                "Ctrl / ⌘ + 1 ~ 9", "按侧栏当前可见顺序快速跳转至对应会话"
                "Ctrl / ⌘ + ,", "打开全局设置"
                "Ctrl / ⌘ + M", "切换本会话使用的模型"
                "Ctrl / ⌘ + Shift + S", "切换主题（跟随系统 → 浅色 → 深色轮转）"
                "Ctrl / ⌘ + /", "显示快捷键帮助"
                "F1", "显示快捷键帮助" ]
              // 端点滚动与问答跳转同组：三条都是「在会话里移动阅读位置」。
              "输入与会话",
              [ "Enter", "发送消息（或换行，按偏好）"
                "Shift + Enter", "换行输入"
                "Ctrl / ⌘ + Enter", "发送 / 换行（与 Enter 互补，按当前偏好）"
                "Ctrl / ⌘ + Shift + E", "导出当前会话为 Markdown"
                "Ctrl / ⌘ + 上 / 下", "跳到上一条 / 下一条提问（顶栏同款按钮）"
                "Ctrl / ⌘ + Home", "滚到会话开头（最早的提问）"
                "Ctrl / ⌘ + End", "滚到会话结尾（最新消息）"
                "Ctrl / ⌘ + L", "聚焦消息输入框"
                "Esc", "关闭弹层 / 取消编辑 / 停止生成" ]
            // 行级快捷键此前只在右键菜单里，键盘用户无从得知：列在这里说明
            // 这些键位真实可用（Sidebar 行 / ChatView 标题的处理与此一一对应），
            // 不新增任何行为，只消除信息差。
              "会话行",
              [ "Enter", "打开当前会话（或提交重命名）"
                "P", "置顶 / 取消置顶当前会话"
                "F2", "重命名当前会话"
                "Delete", "删除当前会话（多选模式下批量删除）" ] ]
        let contentPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceXl)
        for (category, items) in sections do
            contentPanel.Children.Add(Ui.sectionLabel category :> Control)
            // 键位行间距 space1→space2：快捷键是扫读内容，行距放宽减少视觉粘连。
            let groupRows = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceMd)
            for (keys, description) in items do
                // 键位 chip 与其它次级面同一几何：走 Ui.groupingCard；
                // 纵向密度取 compactRowPaddingY（此前是裸 2.0），不再各写一份。
                let key =
                    let keyCap =
                        Ui.groupingCard
                            (Thickness(Spacing.spaceMd, Spacing.spaceXXs))
                            (TextBlock(
                                Text = keys,
                                FontSize = Tokens.fontMicro,
                                FontWeight = FontWeight.Medium,
                                FontFamily = Tokens.monoFontFamily,
                                Foreground = Tokens.textMuted,
                                HorizontalAlignment = HorizontalAlignment.Center))
                            Spacing.Radius.sm
                    keyCap.MinWidth <- 136.0
                    keyCap
                let caption =
                    TextBlock(
                        Text = description,
                        FontSize = Tokens.fontSmall,
                        Foreground = Tokens.text,
                        VerticalAlignment = VerticalAlignment.Center)
                groupRows.Children.Add(Ui.hstack Spacing.spaceXl [ key :> Control; caption :> Control ])
            contentPanel.Children.Add groupRows
        let closeBtn = Ui.button Ui.Primary "关闭" (fun () -> overlay.CloseDialog())
        AutomationProperties.SetName(closeBtn, "关闭")
        AutomationProperties.SetHelpText(closeBtn, "关闭快捷键帮助 (Esc / Enter)")
        ToolTip.SetTip(closeBtn, "关闭快捷键帮助 (Esc / Enter)")
        closeBtn.HorizontalAlignment <- HorizontalAlignment.Right
        let content = Ui.vstack Spacing.space2xl [ Ui.title "键盘快捷键" :> Control; contentPanel :> Control; closeBtn :> Control ]
        // 关闭只走按钮级激活（Ui.onClick）与全局 Escape：内容级 Enter 快捷会误关嵌套层，
        // 且与按钮级重复触发两次 CloseDialog。
        let scroller =
            ScrollViewer(
                Content = content,
                MaxHeight = LayoutPolicy.dialogContentMaxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        // 内容面板本身不进 Tab 序：唯一的键盘停留点就是关闭按钮，
        // Tab 顺序与视觉顺序一致，无不可见的焦点空转。

        closeBtn.Focusable <- true

        overlay.ShowDialog(scroller :> Control, 460.0)
        Dispatcher.UIThread.Post(fun () ->
            if closeBtn.IsEffectivelyVisible && closeBtn.IsEnabled then
                closeBtn.Focus() |> ignore)
