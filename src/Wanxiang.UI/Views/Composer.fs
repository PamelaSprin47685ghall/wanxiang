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
            FontSize = Tokens.fontReading,
            Padding = Thickness 0.0,
            MinHeight = ControlMetrics.composerInputMinHeight,
            MaxHeight = ControlMetrics.composerInputMaxHeight,
            VerticalContentAlignment = VerticalAlignment.Center)

    let attachmentStrip =
        WrapPanel(
            Orientation = Orientation.Horizontal,
            ItemSpacing = Tokens.space2,
            LineSpacing = Tokens.space2)
    let attachmentScroller =
        ScrollViewer(
            Content = attachmentStrip,
            MaxHeight = LayoutPolicy.attachmentDraftMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsVisible = false,
            Margin = Thickness(0.0, 0.0, 0.0, Tokens.space2))

    let sendButton = Ui.iconButtonAccent Icons.send "发送"
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
            Foreground = Tokens.textFaint,
            VerticalAlignment = VerticalAlignment.Center)
    let shell =
        Border(
            Background = Tokens.surface,
            BorderBrush = Tokens.border,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusLg)

    let disabledNotice =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontSmall,
            Foreground = Tokens.textFaint,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
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
            shell.Background <- Tokens.accentSoft
            shell.BorderBrush <- Tokens.accent
            shell.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accentSoft.Color))
        elif input.IsFocused then
            shell.Background <- Tokens.surface
            shell.BorderBrush <- Tokens.accent
            shell.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accentSoft.Color))
        else
            shell.Background <- Tokens.surface
            shell.BorderBrush <- Tokens.border
            shell.BoxShadow <- Tokens.shadowSoft ()

    let refreshSendState () =
        let hasText = not (String.IsNullOrWhiteSpace input.Text)
        let hasAttachment = attachments |> List.exists (fun a -> a.ready)
        let uploading = attachments |> List.exists (fun a -> not a.ready)
        let canSend = enabled && not uploading && (hasText || hasAttachment)
        Ui.setEnabled sendButton (if generating then canStop else canSend)
        queueButton.IsVisible <- generating
        Ui.setEnabled queueButton canSend
        let sendTip =
            if generating then "停止生成"
            elif not enabled then
                if not (String.IsNullOrWhiteSpace disabledReason) then disabledReason else "连接已断开"
            elif uploading then "等待附件上传完成"
            elif not hasText && not hasAttachment then "未输入内容"
            else "发送"
        ToolTip.SetTip(sendButton, sendTip)
        Avalonia.Automation.AutomationProperties.SetName(sendButton, if generating then "停止生成" else "发送")

        if generating then
            let queueTip =
                if not enabled then
                    if not (String.IsNullOrWhiteSpace disabledReason) then disabledReason else "连接已断开"
                elif uploading then "等待附件上传完成"
                elif not hasText && not hasAttachment then "未输入内容"
                else "排队发送"
            ToolTip.SetTip(queueButton, queueTip)
            Avalonia.Automation.AutomationProperties.SetName(queueButton, "排队发送")

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

        Ui.onClick sendButton (fun () ->
            if generating then actions.stopGeneration () else this.Submit())
        Ui.onClick queueButton (fun () -> this.Submit())

        input.TextChanged.Add(fun _ -> refreshSendState ())
        // TextBox 的 AcceptsReturn 会在自己的 OnKeyDown 里吃掉 Enter，
        // 冒泡阶段的监听器再拦已经太晚，必须走隧道阶段。
        input.AddHandler(
            InputElement.KeyDownEvent,
            EventHandler<KeyEventArgs>(fun _ e ->
                if e.Key = Key.Enter then
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
                    let currentText = if isNull input.Text then "" else input.Text
                    // Caret is at line 0 if CaretIndex = 0 or if there is no newline before CaretIndex
                    let isAtBeginning =
                        input.CaretIndex = 0 ||
                        let idx = Math.Min(input.CaretIndex, currentText.Length)
                        not (currentText.Substring(0, idx).Contains('\n'))
                    if isAtBeginning && promptHistory.Count > 0 then
                        if historyIndex = -1 then
                            uncommittedDraft <- currentText
                            historyIndex <- promptHistory.Count - 1
                        elif historyIndex > 0 then
                            historyIndex <- historyIndex - 1
                        input.Text <- promptHistory.[historyIndex]
                        input.CaretIndex <- (if isNull input.Text then 0 else input.Text.Length)
                        e.Handled <- true
                elif e.Key = Key.Down && e.KeyModifiers = KeyModifiers.None then
                    if historyIndex <> -1 then
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
                        if promptHistory.Count = 0 || promptHistory.[promptHistory.Count - 1] <> text then
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
        for attachment in attachments do
            let icon =
                if attachment.ready then
                    let glyph = if attachment.mediaType.StartsWith("image/", StringComparison.Ordinal) then Icons.image else Icons.file
                    glyph Tokens.textMuted
                else
                    Ui.spinner 13.0
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
            let remove = Ui.iconButton Icons.close "移除"
            Ui.onClick remove (fun () ->
                actions.removeAttachment attachment.attachmentId
                tryFocusInput ())
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space1, VerticalAlignment = VerticalAlignment.Center)
            row.Children.Add iconSlot
            row.Children.Add name
            row.Children.Add size
            row.Children.Add remove
            attachmentStrip.Children.Add(
                Border(
                    Background = Tokens.surfaceRaised,
                    BorderBrush = Tokens.border,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius Tokens.radiusSm,
                    Padding = Thickness(ControlMetrics.compactChipPaddingX, ControlMetrics.compactChipPaddingY),
                    Child = row))
        attachmentScroller.IsVisible <- not (List.isEmpty attachments)

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
            ToolTip.SetTip(sendButton, "停止生成")
            Avalonia.Automation.AutomationProperties.SetName(sendButton, "停止生成")
        else
            Ui.setIcon sendButton Icons.send Tokens.textOnAccent
            ToolTip.SetTip(sendButton, "发送")
            Avalonia.Automation.AutomationProperties.SetName(sendButton, "发送")
        refreshSendState ()

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
        disabledNotice.Text <- reason
        disabledNotice.IsVisible <- not value && not (String.IsNullOrWhiteSpace reason)
        refreshSendState ()
        if not wasEnabled && value && wasActiveBeforeDisabled then
            wasActiveBeforeDisabled <- false
            tryFocusInput ()

    member _.SetModelLabel(text: string) = modelCaption.Text <- text

    member _.SetEnterSends(value: bool) =
        enterSends <- value
        hintText.Text <- if value then "Enter 发送 · Shift+Enter 换行" else "Ctrl+Enter 发送 · Enter 换行"

    /// 窄屏只收紧已有控件，不增加第二套输入逻辑：隐藏键盘提示、压缩边距与模型标签。
    member this.SetCompactMode(value: bool) =
        hintText.IsVisible <- not value
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

        let footerRow = DockPanel(LastChildFill = false)
        DockPanel.SetDock(modelChip, Dock.Left)
        DockPanel.SetDock(hintText, Dock.Right)
        footerRow.Children.Add modelChip
        footerRow.Children.Add hintText

        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2)
        column.Children.Add attachmentScroller
        column.Children.Add inputRow
        column.Children.Add(Ui.hairline ())
        column.Children.Add footerRow

        shell.Padding <-
            Thickness(
                ControlMetrics.composerShellPaddingX,
                ControlMetrics.composerShellPaddingY,
                Tokens.space2,
                ControlMetrics.composerShellPaddingY)
        shell.Child <- column
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
            if not (isWithinBounds this e || isWithinBounds shell e) then
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
