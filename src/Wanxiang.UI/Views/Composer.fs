namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Threading
open Avalonia.VisualTree

/// 输入区对外暴露的动作。
type ComposerActions = {
    /// true 表示内容已由本地待发送记录接管，不代表服务端已经保存。
    submit: string -> bool
    stopGeneration: unit -> unit
    pickAttachment: unit -> unit
    removeAttachment: Guid -> unit
    openModelPicker: Control -> unit
    dropFiles: IStorageItem list -> unit
    pasteFromClipboard: unit -> bool
}

/// 消息输入区。
///
/// 关键取舍：
/// - 无会话时整块禁用并说明原因，而不是留一个点了没反应的输入框；
/// - 生成中把发送键换成停止键，同一位置同一手势；
/// - 附件以可删除的小卡呈现，而不是一行状态文字；
/// - 模型选择就放在输入框里，换模型不必开设置。
type Composer(actions: ComposerActions) as this =
    inherit Border()

    let input =
        TextBox(
            PlaceholderText = "输入消息…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = Thickness 0.0,
            Background = Brushes.Transparent,
            Foreground = Tokens.text,
            CaretBrush = Tokens.accent,
            PlaceholderForeground = Tokens.textMuted,
            FontSize = Tokens.fontReading,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = Thickness(0.0, 4.0, 0.0, 4.0),
            MinHeight = ControlMetrics.composerInputMinHeight,
            MaxHeight = ControlMetrics.composerMaxHeight,
            VerticalContentAlignment = VerticalAlignment.Center)

    let attachmentStrip =
        WrapPanel(
            Orientation = Orientation.Horizontal,
            ItemSpacing = Tokens.space2,
            LineSpacing = Tokens.space2,
            Focusable = true)
    let attachmentScroller =
        ScrollViewer(
            Content = attachmentStrip,
            MaxHeight = LayoutPolicy.attachmentDraftMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AllowAutoHide = true,
            IsVisible = false,
            Margin = Thickness(0.0, 0.0, 0.0, Tokens.space2))

    let sendButton =
        let btn = Ui.iconButtonAccent Icons.send "发送"
        btn.HorizontalAlignment <- HorizontalAlignment.Center
        btn.VerticalAlignment <- VerticalAlignment.Center
        btn.Width <- Tokens.iconButton
        btn.Height <- Tokens.iconButton
        btn.MinWidth <- Tokens.iconButton
        btn.MinHeight <- Tokens.iconButton
        btn.CornerRadius <- CornerRadius Tokens.radiusPill
        btn
    let queueButton = Ui.iconButton Icons.send "排队发送"
    let pendingPanel = StackPanel(Spacing = Tokens.space2)
    let pendingScroller =
        ScrollViewer(Content = pendingPanel, MaxHeight = LayoutPolicy.pendingMessagesMaxHeight, IsVisible = false,
                     HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                     VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
    let mutable pendingKeys: (Guid * DeliveryState * bool * bool) list = []
    let attachButton = Ui.iconButton Icons.paperclip "添加附件"
    let modelCaption =
        TextBlock(
            Text = "未选择模型",
            FontSize = Tokens.fontMicro,
            Foreground = Tokens.textMuted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = ControlMetrics.composerModelMaxWidth)
    let modelChip =
        ActionBorder(
            Background = Brushes.Transparent,
            CornerRadius = CornerRadius Tokens.radiusSm,
            Padding = Thickness(ControlMetrics.compactChipPaddingX, ControlMetrics.compactChipPaddingY),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = true,
            VerticalAlignment = VerticalAlignment.Center)
    let hintText =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontMicro,
            Padding = Thickness(0.0, ControlMetrics.compactChipPaddingY),
            Foreground = Tokens.textFaint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center)
    let shell =
        Border(
            Background = Tokens.surface,
            BorderBrush = Tokens.borderSoft,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusLg)

    let dropHintBanner =
        Border(
            Background = Tokens.accentFaint,
            BorderBrush = Tokens.accentSoft,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusLg,
            IsHitTestVisible = false,
            IsVisible = false)
    let dropHintText =
        TextBlock(
            Text = "释放文件以添加到当前会话附件",
            FontSize = Tokens.fontSmall,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.accent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center)

    let disabledNoticeText =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontCaption,
            Foreground = Tokens.textMuted,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = ReadingRhythm.captionLineHeight)
    let disabledNotice =
        Border(
            Background = Tokens.surfaceRaised,
            BorderBrush = Tokens.borderSoft,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusMd,
            Padding = Thickness(Tokens.space3, Tokens.space2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = disabledNoticeText,
            Opacity = 0.9,
            IsVisible = false)

    let mutable enterSends = true
    let mutable generating = false
    let mutable canStop = true
    let mutable enabled = false
    let mutable disabledReason = ""
    let mutable wasActiveBeforeDisabled = false
    let mutable isDraggingOver = false
    let promptHistory = ResizeArray<string>()
    let mutable historyIndex = -1
    let mutable uncommittedDraft = ""
    let maxHistoryCount = 100
    let mutable attachments: PendingAttachment list = []
    let mutable pendingFocusAttachmentId: Guid option = None
    let removeButtons = System.Collections.Generic.Dictionary<Guid, Control>()

    let tryFocusInput () =
        let act () =
            try
                if VisualExtensions.IsAttachedToVisualTree input && input.IsEffectivelyVisible && input.IsEnabled then
                    input.Focus() |> ignore
            with _ -> ()
        if Dispatcher.UIThread.CheckAccess() then act ()
        else Dispatcher.UIThread.Post(fun () -> act ())

    let updateShellVisual () =
        if isDraggingOver then
            shell.Background <- Tokens.surface
            shell.BorderBrush <- Tokens.accent
            shell.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accent.Color))
            dropHintBanner.IsVisible <- true
            input.PlaceholderText <- "释放文件以添加到当前会话附件"
        elif input.IsFocused then
            shell.Background <- Tokens.surface
            shell.BorderBrush <- Tokens.accent
            shell.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accentSoft.Color))
            dropHintBanner.IsVisible <- false
            input.PlaceholderText <-
                if not enabled && not (String.IsNullOrWhiteSpace disabledReason) then
                    disabledReason
                else
                    "输入消息…"
        else
            shell.Background <- Tokens.surface
            shell.BorderBrush <- Tokens.borderSoft
            shell.BoxShadow <- Tokens.shadowSoft ()
            dropHintBanner.IsVisible <- false
            input.PlaceholderText <-
                if not enabled && not (String.IsNullOrWhiteSpace disabledReason) then
                    disabledReason
                else
                    "输入消息…"

    let refreshSendState () =
        let text = input.Text
        let checkPresenterMultiLine () =
            let presenter = input.GetVisualDescendants() |> Seq.tryPick (function :? Avalonia.Controls.Presenters.TextPresenter as p -> Some p | _ -> None)
            match presenter with
            | Some p when not (isNull p.TextLayout) -> p.TextLayout.TextLines.Count > 1
            | _ -> false
        let isMultiLine =
            not (isNull text) && (text.Contains('\n') || text.Contains('\r') || checkPresenterMultiLine ())
        input.VerticalContentAlignment <-
            if isMultiLine then VerticalAlignment.Top else VerticalAlignment.Center

        let hasText = not (String.IsNullOrWhiteSpace input.Text)
        let hasAttachment = attachments |> List.exists (fun a -> a.ready)
        let uploading = attachments |> List.exists (fun a -> not a.ready)
        let canSend = enabled && not uploading && (hasText || hasAttachment)
        Ui.setEnabled sendButton (if generating then canStop else canSend)
        Ui.setEnabled queueButton canSend
        Ui.setReservedActionVisible queueButton generating
        if generating && not canSend then
            queueButton.Opacity <- Tokens.opacityDisabled
            queueButton.IsHitTestVisible <- false
            queueButton.Focusable <- false
        let sendTip =
            if generating then
                if canStop then "停止生成 (Escape)" else "正在停止，请稍候…"
            elif not enabled then
                if not (String.IsNullOrWhiteSpace disabledReason) then disabledReason else "连接已断开"
            elif uploading then
                "附件上传中，请稍候…"
            elif not hasText && not hasAttachment then
                "请输入消息或添加附件"
            else
                if enterSends then "发送 (Enter)" else "发送 (Ctrl+Enter / ⌘Enter)"
        ToolTip.SetTip(sendButton, sendTip)
        let sendLabel = if generating then "停止生成" else "发送"
        Avalonia.Automation.AutomationProperties.SetName(sendButton, sendLabel)
        Avalonia.Automation.AutomationProperties.SetHelpText(sendButton, sendTip)
        if not (isNull sendButton.Child) then
            Avalonia.Automation.AutomationProperties.SetName(sendButton.Child, sendLabel)

        if generating then
            let queueTip =
                if not enabled then
                    if not (String.IsNullOrWhiteSpace disabledReason) then disabledReason else "连接已断开"
                elif uploading then "附件上传中，请稍候…"
                elif not hasText && not hasAttachment then "请输入消息或添加附件"
                else
                    if enterSends then "排队发送 (Enter)" else "排队发送 (Ctrl+Enter / ⌘Enter)"
            ToolTip.SetTip(queueButton, queueTip)
            Avalonia.Automation.AutomationProperties.SetName(queueButton, "排队发送")
            Avalonia.Automation.AutomationProperties.SetHelpText(queueButton, queueTip)
            if not (isNull queueButton.Child) then
                Avalonia.Automation.AutomationProperties.SetName(queueButton.Child, "排队发送")

    do
        modelChip.Child <-
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space1, VerticalAlignment = VerticalAlignment.Center)
            let glyph = Icons.sparkle Tokens.textFaint
            glyph.VerticalAlignment <- VerticalAlignment.Center
            row.Children.Add glyph
            row.Children.Add modelCaption
            let chevron = Icons.chevronDown Tokens.textFaint
            chevron.VerticalAlignment <- VerticalAlignment.Center
            row.Children.Add chevron
            row
        modelChip.PointerEntered.Add(fun _ -> modelChip.Background <- Tokens.hover)
        modelChip.PointerExited.Add(fun _ -> modelChip.Background <- Brushes.Transparent)
        Ui.onClick modelChip (fun () -> actions.openModelPicker(modelChip :> Control))
        ToolTip.SetTip(modelChip, "切换本会话使用的模型")
        Avalonia.Automation.AutomationProperties.SetName(modelChip, "切换本会话使用的模型")

        Ui.onClick attachButton (fun () -> actions.pickAttachment ())
        Avalonia.Automation.AutomationProperties.SetName(attachmentStrip, "附件列表")
        ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Disabled)
        ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto)

        Ui.onClick sendButton (fun () ->
            if generating then actions.stopGeneration () else this.Submit())
        Ui.onClick queueButton (fun () -> this.Submit())

        input.TextChanged.Add(fun _ -> refreshSendState ())
        input.SizeChanged.Add(fun _ -> refreshSendState ())
        // TextBox 的 AcceptsReturn 会在自己的 OnKeyDown 里吃掉 Enter，
        // 冒泡阶段的监听器再拦已经太晚，必须走隧道阶段。
        input.AddHandler(
            InputElement.KeyDownEvent,
            EventHandler<KeyEventArgs>(fun _ e ->
                if e.Key = Key.Escape then
                    if generating then
                        e.Handled <- true
                        actions.stopGeneration ()
                    else
                        if input.SelectionStart <> input.SelectionEnd then
                            e.Handled <- true
                            input.ClearSelection()
                        elif historyIndex <> -1 then
                            historyIndex <- -1
                            input.Text <- uncommittedDraft
                            input.CaretIndex <- (if isNull input.Text then 0 else input.Text.Length)
                            e.Handled <- true
                        elif input.IsFocused then
                            e.Handled <- true
                            match TopLevel.GetTopLevel input with
                            | null -> ()
                            | top ->
                                match top.FocusManager with
                                | null -> ()
                                | fm -> fm.Focus(null, NavigationMethod.Unspecified, KeyModifiers.None) |> ignore
                elif e.Key = Key.Enter then
                    let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
                    let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
                    if shift then ()
                    elif enterSends || ctrl then
                        e.Handled <- true
                        this.Submit()
                elif e.Key = Key.V then
                    let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
                    if ctrl then
                        if actions.pasteFromClipboard () then
                            e.Handled <- true
                elif e.Key = Key.Up && e.KeyModifiers = KeyModifiers.None then
                    let hasSelection = input.SelectionStart <> input.SelectionEnd
                    if not hasSelection && promptHistory.Count > 0 then
                        let currentText = if isNull input.Text then "" else input.Text
                        let isMultiline = currentText.IndexOfAny([| '\n'; '\r' |]) >= 0
                        let caret = input.CaretIndex
                        let hasNewlineBeforeCaret =
                            let idx = Math.Clamp(caret, 0, currentText.Length)
                            currentText.Substring(0, idx).IndexOfAny([| '\n'; '\r' |]) >= 0

                        let canRecall =
                            if isMultiline then
                                not hasNewlineBeforeCaret && caret = 0
                            elif historyIndex <> -1 then
                                true
                            else
                                currentText.Length = 0 || caret = 0

                        if canRecall then
                            if historyIndex = -1 then
                                uncommittedDraft <- currentText
                                historyIndex <- promptHistory.Count - 1
                            elif historyIndex > 0 then
                                historyIndex <- historyIndex - 1
                            input.Text <- promptHistory.[historyIndex]
                            input.CaretIndex <- (if isNull input.Text then 0 else input.Text.Length)
                            e.Handled <- true
                elif e.Key = Key.Down && e.KeyModifiers = KeyModifiers.None then
                    let hasSelection = input.SelectionStart <> input.SelectionEnd
                    if not hasSelection && historyIndex <> -1 then
                        let currentText = if isNull input.Text then "" else input.Text
                        let isAtEnd = input.CaretIndex >= currentText.Length
                        if isAtEnd then
                            if historyIndex < promptHistory.Count - 1 then
                                historyIndex <- historyIndex + 1
                                input.Text <- promptHistory.[historyIndex]
                                input.CaretIndex <- (if isNull input.Text then 0 else input.Text.Length)
                                e.Handled <- true
                            else
                                // Reached the end: restore draft
                                historyIndex <- -1
                                input.Text <- uncommittedDraft
                                input.CaretIndex <- (if isNull input.Text then 0 else input.Text.Length)
                                e.Handled <- true),
            RoutingStrategies.Tunnel)

    member private this.Submit() =
        let uploading = attachments |> List.exists (fun a -> not a.ready)
        if enabled && not uploading then
            let text = if isNull input.Text then "" else input.Text
            if not (String.IsNullOrWhiteSpace text) || attachments |> List.exists (fun a -> a.ready) then
                if actions.submit text then
                    if not (String.IsNullOrWhiteSpace text) then
                        let isDuplicate = promptHistory.Count > 0 && promptHistory.[promptHistory.Count - 1] = text
                        if not isDuplicate then
                            promptHistory.Add text
                            if promptHistory.Count > maxHistoryCount then
                                promptHistory.RemoveAt 0
                    historyIndex <- -1
                    uncommittedDraft <- ""
                    // 所有权已经移交；回调若切到了另一份草稿，不清它的内容。
                    if input.Text = text then
                        input.Text <- ""
                        input.CaretIndex <- 0
                    refreshSendState ()
                    tryFocusInput ()

    member private this.RenderAttachments() =
        attachmentStrip.Children.Clear()
        removeButtons.Clear()
        for attachment in attachments do
            let icon =
                if attachment.ready then
                    let glyph = if attachment.mediaType.StartsWith("image/", StringComparison.Ordinal) then Icons.image else Icons.file
                    glyph Tokens.textMuted
                else
                    // 与文件图标同为 iconGlyph：上传中→就绪只换字形，图标槽 15px 固定，chip 高度不动。
                    Ui.spinner Tokens.iconGlyph
            icon.VerticalAlignment <- VerticalAlignment.Center
            icon.HorizontalAlignment <- HorizontalAlignment.Center
            let iconSlot =
                Border(
                    Width = ControlMetrics.composerAttachmentIconSlot,
                    Height = ControlMetrics.composerAttachmentIconSlot,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = icon)
            let name =
                TextBlock(
                    Text = attachment.fileName,
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.text,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = ControlMetrics.composerAttachmentNameMaxWidth,
                    VerticalAlignment = VerticalAlignment.Center)
            let size =
                TextBlock(
                    Text = (if attachment.ready then AttachmentRef.formatSize attachment.size else "上传中"),
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.textFaint,
                    Width = ControlMetrics.composerAttachmentStateWidth,
                    TextAlignment = TextAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center)
            let formattedSize = if attachment.ready then AttachmentRef.formatSize attachment.size else "上传中"
            ToolTip.SetTip(size, sprintf "文件大小：%s" formattedSize)
            let removeLabel = sprintf "移除附件 %s" attachment.fileName
            let remove = Ui.iconButton Icons.close removeLabel
            ToolTip.SetTip(remove, removeLabel)
            Avalonia.Automation.AutomationProperties.SetName(remove, removeLabel)
            if not (isNull remove.Child) then
                Avalonia.Automation.AutomationProperties.SetName(remove.Child, "移除")

            removeButtons.[attachment.attachmentId] <- remove

            let currentAttachmentId = attachment.attachmentId
            Ui.onClick remove (fun () ->
                let remaining = attachments |> List.filter (fun a -> a.attachmentId <> currentAttachmentId)
                match remaining with
                | [] ->
                    pendingFocusAttachmentId <- None
                    actions.removeAttachment currentAttachmentId
                    // In case removeAttachment didn't trigger RenderAttachments synchronously:
                    tryFocusInput ()
                | items ->
                    let idx =
                        attachments
                        |> List.tryFindIndex (fun a -> a.attachmentId = currentAttachmentId)
                        |> Option.defaultValue 0
                    let targetIdx = Math.Clamp(idx, 0, items.Length - 1)
                    let nextTargetId = items.[targetIdx].attachmentId
                    pendingFocusAttachmentId <- Some nextTargetId
                    actions.removeAttachment currentAttachmentId
                    Dispatcher.UIThread.Post(fun () ->
                        match pendingFocusAttachmentId with
                        | Some targetId ->
                            pendingFocusAttachmentId <- None
                            match removeButtons.TryGetValue targetId with
                            | true, btn when btn.IsEffectivelyVisible && btn.IsEnabled ->
                                btn.Focus() |> ignore
                            | _ ->
                                if attachmentStrip.IsEffectivelyVisible && attachmentStrip.IsEnabled then
                                    attachmentStrip.Focus() |> ignore
                                else
                                    tryFocusInput ()
                        | None -> ()))
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space1, VerticalAlignment = VerticalAlignment.Center)
            row.Children.Add iconSlot
            row.Children.Add name
            row.Children.Add size
            row.Children.Add remove
            let chipLabel = sprintf "附件：%s (%s)" attachment.fileName (AttachmentRef.formatSize attachment.size)
            let chip =
                Border(
                    Background = Tokens.surfaceRaised,
                    BorderBrush = Tokens.border,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius Tokens.radiusSm,
                    Padding = Thickness(ControlMetrics.compactChipPaddingX, ControlMetrics.compactChipPaddingY),
                    Child = row)
            ToolTip.SetTip(chip, chipLabel)
            Avalonia.Automation.AutomationProperties.SetName(chip, chipLabel)
            attachmentStrip.Children.Add chip
        attachmentScroller.IsVisible <- not (List.isEmpty attachments)
        match pendingFocusAttachmentId with
        | Some targetId ->
            pendingFocusAttachmentId <- None
            Dispatcher.UIThread.Post(fun () ->
                match removeButtons.TryGetValue targetId with
                | true, btn when btn.IsEffectivelyVisible && btn.IsEnabled ->
                    btn.Focus() |> ignore
                | _ ->
                    if attachmentStrip.IsEffectivelyVisible && attachmentStrip.IsEnabled then
                        attachmentStrip.Focus() |> ignore
                    else
                        tryFocusInput ())
        | None -> ()

    member this.SetAttachments(items: PendingAttachment list) =
        if attachments <> items then
            attachments <- items
            this.RenderAttachments()
            refreshSendState ()

    member _.Text = if isNull input.Text then "" else input.Text

    member _.SetCanStop(value: bool) =
        canStop <- value
        refreshSendState ()

    /// 未确认消息常驻在输入区上方；失败不会只剩一条会消失的 toast。
    member _.SetPendingMessages(items: PendingMessage list, activeConversationId: Guid option, canRetry: bool, retry: Guid -> unit, openConversation: Guid -> unit) =
        let keys = items |> List.map (fun item -> PendingMessage.invocationId item, item.state, canRetry, activeConversationId = Some item.conversationId)
        if keys <> pendingKeys then
            pendingKeys <- keys
            pendingPanel.Children.Clear()
            let current, elsewhere = items |> List.partition (fun item -> activeConversationId = Some item.conversationId)
            for item in current do
                let body = StackPanel(Spacing = Tokens.space1)
                if not (String.IsNullOrEmpty item.text) then
                    body.Children.Add(
                        ScrollViewer(
                            MaxHeight = LayoutPolicy.pendingMessagePreviewMaxHeight,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            Content = SelectableTextBlock(Text = item.text, TextWrapping = TextWrapping.Wrap,
                                                          FontSize = Tokens.fontSmall, Foreground = Tokens.text)))
                if not (List.isEmpty item.attachments) then
                    body.Children.Add(Ui.caption (item.attachments |> List.map _.fileName |> String.concat "、"))
                let status = TextBlock(Text = PendingMessage.status item, TextWrapping = TextWrapping.Wrap,
                                       FontSize = Tokens.fontMicro, Foreground = Tokens.textMuted)
                let row = DockPanel(LastChildFill = true)
                if PendingMessage.canRetry item then
                    let button = Ui.button Ui.Secondary "重试" (fun () -> retry (PendingMessage.invocationId item))
                    Ui.setEnabled button canRetry
                    DockPanel.SetDock(button, Dock.Right)
                    row.Children.Add button
                row.Children.Add status
                body.Children.Add row
                pendingPanel.Children.Add(Ui.card body)
            // 首条消息创建会话失败时尚无侧栏条目，切走后仍要有找回入口。
            for conversationId, pending in elsewhere |> List.groupBy _.conversationId do
                let label = sprintf "查看另一个会话的未确认消息（%d 条）" pending.Length
                pendingPanel.Children.Add(Ui.button Ui.Secondary label (fun () -> openConversation conversationId))
            if not (List.isEmpty items) then
                pendingPanel.Children.Add(Ui.caption "未确认内容只保留在当前窗口，关闭或刷新前请先确认保存。")
            pendingScroller.IsVisible <- not (List.isEmpty items)

    member this.SetGenerating(value: bool) =
        generating <- value
        if value then
            Ui.setIcon sendButton Icons.stop Tokens.textOnAccent
        else
            Ui.setIcon sendButton Icons.send Tokens.textOnAccent
        // 发送/停止只换字形：按钮外尺寸与圆角在构造时已固定，切换时不动几何，避免 1px 抖动。
        refreshSendState ()

    member _.IsGenerating = generating

    /// 启用输入。`reason` 在禁用时说明为什么不能发。
    member this.SetEnabled(value: bool, reason: string) =
        let wasEnabled = enabled
        if not value && (input.IsFocused || wasActiveBeforeDisabled) then
            wasActiveBeforeDisabled <- true
        enabled <- value
        disabledReason <- reason
        // 连接暂不可用时仍能写草稿；只限制发送与上传。
        input.IsEnabled <- true
        Ui.setEnabled attachButton value
        shell.Opacity <- if value then 1.0 else Tokens.opacityComposerDisabled
        disabledNoticeText.Text <- reason
        disabledNotice.IsVisible <- not value && not (String.IsNullOrWhiteSpace reason)
        updateShellVisual ()
        refreshSendState ()
        if not wasEnabled && value && wasActiveBeforeDisabled then
            wasActiveBeforeDisabled <- false
            tryFocusInput ()

    member _.SetModelLabel(text: string) = modelCaption.Text <- text

    member _.SetEnterSends(value: bool) =
        enterSends <- value
        hintText.Text <- if value then "Enter 发送 · Shift+Enter 换行" else "Ctrl+Enter 发送 · Enter 换行"
        refreshSendState ()

    /// 窄屏只收紧已有控件，不增加第二套输入逻辑：隐藏键盘提示、压缩边距与模型标签。
    member this.SetCompactMode(value: bool) =
        hintText.IsVisible <- true
        hintText.Opacity <- if not value then 1.0 else 0.0
        hintText.IsHitTestVisible <- not value
        modelCaption.MaxWidth <-
            if value then ControlMetrics.composerModelCompactMaxWidth else ControlMetrics.composerModelMaxWidth
        let actionSize = if value then LayoutPolicy.compactActionTarget else Tokens.iconButton
        Ui.setSquareTarget attachButton actionSize
        Ui.setSquareTarget sendButton actionSize
        Ui.setSquareTarget queueButton actionSize
        modelChip.MinHeight <- if value then LayoutPolicy.compactActionTarget else 0.0
        this.Padding <-
            if value then Thickness(Tokens.space3, Tokens.space3, Tokens.space3, Tokens.space2)
            else Thickness(Tokens.shellInset, Tokens.space4, Tokens.shellInset, Tokens.space3)

    member _.Focus() = tryFocusInput ()

    /// 把文本塞进输入框（编辑重发、提示词模板）。
    member _.SetText(text: string) =
        input.Text <- text
        input.CaretIndex <- if isNull text then 0 else text.Length
        historyIndex <- -1
        uncommittedDraft <- ""
        tryFocusInput ()

    member this.Build() =
        this.Background <- Brushes.Transparent
        // 顶部留白是**确定**的间距：滚动区能留多少取决于 Extent 与 ScrollToEnd 的行为，
        // 而这里的 padding 一定在，末条消息的脚注行不会贴到输入卡上。
        this.Padding <- Thickness(Tokens.shellInset, Tokens.space4, Tokens.shellInset, Tokens.space3)

        let actionRow = Ui.hstack Tokens.space1 [ attachButton :> Control; queueButton :> Control; sendButton :> Control ]
        actionRow.VerticalAlignment <- VerticalAlignment.Bottom

        let inputRow = DockPanel(LastChildFill = true)
        DockPanel.SetDock(actionRow, Dock.Right)
        inputRow.Children.Add actionRow
        inputRow.Children.Add input

        let footerRow = DockPanel(LastChildFill = true)
        DockPanel.SetDock(modelChip, Dock.Left)
        // hint 不 dock、作为 fill 占剩余宽度并右对齐：长模型名或窄窗口下只省略提示文本，绝不与模型芯片重叠争位。
        footerRow.Children.Add modelChip
        footerRow.Children.Add hintText

        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2)
        column.Children.Add attachmentScroller
        column.Children.Add inputRow
        column.Children.Add(Ui.hairline ())
        column.Children.Add footerRow

        let dropOverlayContent =
            Border(
                Background = Tokens.accentSoft,
                CornerRadius = CornerRadius Tokens.radiusMd,
                Padding = Thickness(Tokens.space3, Tokens.space2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = dropHintText)
        dropHintBanner.Child <- dropOverlayContent

        let shellGrid = Grid()
        shellGrid.Children.Add column
        shellGrid.Children.Add dropHintBanner

        shell.Padding <-
            Thickness(
                ControlMetrics.composerShellPaddingX,
                ControlMetrics.composerShellPaddingY,
                Tokens.space2,
                ControlMetrics.composerShellPaddingY)
        shell.Child <- shellGrid
        shell.BoxShadow <- Tokens.shadowSoft ()

        input.GotFocus.Add(fun _ ->
            wasActiveBeforeDisabled <- true
            updateShellVisual ())
        input.LostFocus.Add(fun _ ->
            if enabled then wasActiveBeforeDisabled <- false
            updateShellVisual ())

        DragDrop.SetAllowDrop(shell, true)
        DragDrop.SetAllowDrop(this, true)

        let isWithinBounds (visual: Visual) (e: DragEventArgs) =
            try
                let bounds = visual.Bounds
                if bounds.Width > 0.0 && bounds.Height > 0.0 then
                    let pos = e.GetPosition visual
                    Rect(0.0, 0.0, bounds.Width, bounds.Height).Contains pos
                else false
            with _ -> false

        let handleDragOver (e: DragEventArgs) =
            let hasFiles =
                e.DataTransfer <> null &&
                (e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.TryGetFiles() <> null)
            if hasFiles then
                e.DragEffects <- DragDropEffects.Copy
                if not isDraggingOver then
                    isDraggingOver <- true
                    updateShellVisual ()
            else
                e.DragEffects <- DragDropEffects.None
                if isDraggingOver then
                    isDraggingOver <- false
                    updateShellVisual ()

        let handleDragLeave (e: DragEventArgs) =
            // DragLeave fires when exiting or moving between elements.
            if not (isWithinBounds this e) && not (isWithinBounds shell e) then
                if isDraggingOver then
                    isDraggingOver <- false
                    updateShellVisual ()
        let handleDrop (e: DragEventArgs) =
            if isDraggingOver then
                isDraggingOver <- false
                updateShellVisual ()
            if e.DataTransfer <> null then
                let files =
                    let multi = e.DataTransfer.TryGetFiles()
                    if multi <> null then List.ofSeq multi
                    else
                        let single = e.DataTransfer.TryGetFile()
                        if single <> null then [ single ] else []
                if not (List.isEmpty files) then
                    e.Handled <- true
                    actions.dropFiles files
                    tryFocusInput ()

        shell.AddHandler(DragDrop.DragOverEvent, EventHandler<DragEventArgs>(fun _ e -> handleDragOver e))
        shell.AddHandler(DragDrop.DragLeaveEvent, EventHandler<DragEventArgs>(fun _ e -> handleDragLeave e))
        shell.AddHandler(DragDrop.DropEvent, EventHandler<DragEventArgs>(fun _ e -> handleDrop e))
        this.AddHandler(DragDrop.DragOverEvent, EventHandler<DragEventArgs>(fun _ e -> handleDragOver e))
        this.AddHandler(DragDrop.DragLeaveEvent, EventHandler<DragEventArgs>(fun _ e -> handleDragLeave e))
        this.AddHandler(DragDrop.DropEvent, EventHandler<DragEventArgs>(fun _ e -> handleDrop e))

        let outer = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2, MaxWidth = Tokens.readingWidth)
        outer.Children.Add disabledNotice
        outer.Children.Add pendingScroller
        outer.Children.Add shell
        this.Child <- outer
        this.SetEnterSends true
        this.SetCompactMode false
        refreshSendState ()
