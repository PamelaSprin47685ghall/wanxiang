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
/// - 模型选择就在输入框里，换模型不必开设置。
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
            // V28: 垂直内边距sapce1，不散落 4.0。
            Padding = Thickness(0.0, Tokens.space1, 0.0, Tokens.space1),
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

    // 几何全由 Ui.iconButtonAccent 固定（发送↔停止只换字形）；此处不再复述尺寸，避免双写漂移。
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
            // V29: 常态保留最轻发丝轮廓（不是全透明），焦点/悬停只切色；
            // 轮廓与底色补间走 Ui.surfaceBorderedTransitions，边框粗细恒定不位移。
            BorderBrush = Tokens.hairline,
            BorderThickness = Thickness ControlMetrics.borderWidth,
            VerticalAlignment = VerticalAlignment.Center,
            Transitions = Ui.surfaceBorderedTransitions ())
    let hintText =
        TextBlock(
            Text = "",
            // 键位提示是重要 affordance：升到 caption 档 + textMuted，不再与角标混同的 micro/faint。
            FontSize = Tokens.fontCaption,
            Padding = Thickness(0.0, ControlMetrics.compactChipPaddingY),
            Foreground = Tokens.textMuted,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center)

    // 拖放入口的可发现性：AllowDrop 与 dropHintBanner 只服务「已经在拖」的用户，
    // 静态界面此前没有任何线索。这里在 footer caption 行补一条常驻弱提示：复用
    // Ui.caption（fontCaption + textMuted）同一档位，不加新行高、不碰拖放逻辑。
    let dropAttachHint =
        let hint = Ui.caption "可将文件拖入此处添加附件"
        // footer 是单行 caption 行：覆盖 Ui.caption 的换行为不换行，窄窗口省略而非折行；
        // 左空与模型芯片分开，剩余宽度仍由 fill 的 hintText 承接。
        hint.TextWrapping <- TextWrapping.NoWrap
        hint.TextTrimming <- TextTrimming.CharacterEllipsis
        hint.Margin <- Thickness(Tokens.space2, 0.0, 0.0, 0.0)
        hint.VerticalAlignment <- VerticalAlignment.Center
        // 纯文本控件补 Name 供读屏读取，与 SetPendingMessages 的 pendingNotice 同一惯例。
        Avalonia.Automation.AutomationProperties.SetName(hint, hint.Text)
        hint
    let shell =
        Border(
            Background = Tokens.surface,
            BorderBrush = Tokens.hairline,
            BorderThickness = Thickness ControlMetrics.borderWidth,
            CornerRadius = CornerRadius Tokens.radiusLg)

    let dropHintBanner =
        Border(
            Background = Tokens.accentFaint,
            BorderBrush = Tokens.accentSoft,
            BorderThickness = Thickness ControlMetrics.borderWidth,
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
    // V31: 禁用提示独立高亮（不随 shell 变灰）：不透明 +强一档的描边。
    let disabledNotice =
        Border(
            // 禁用说明不随 shell 变灰：surfaceContainer 暖纸中间层 + hairlineStrong 描边，
            // 与变灰的输入壳形成清晰但不刺眼的分层。
            Background = Tokens.surfaceContainer,
            BorderBrush = Tokens.hairlineStrong,
            BorderThickness = Thickness ControlMetrics.borderWidth,
            CornerRadius = CornerRadius Tokens.radiusMd,
            Padding = Thickness(Tokens.space3, Tokens.space2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = disabledNoticeText,
            Opacity = 1.0,
            IsVisible = false)

    let mutable enterSends = true
    // 当前 compact 档：宽度驱动的留白刷新只在非 compact 路径生效，
    // 与 SetCompactMode 协作而非争抢同一份 Padding 写权。
    let mutable compactMode = false
    let mutable generating = false
    let mutable canStop = true
    let mutable enabled = false
    let mutable disabledReason = ""
    let mutable wasActiveBeforeDisabled = false
    let mutable isDraggingOver = false
    // 发送历史全局共享（shell 风格，不随会话隔离）；半截输入由 uncommittedDraft + per-session draft 持有，不进历史。
    let promptHistory = ResizeArray<string>()
    let mutable historyIndex = -1
    let mutable uncommittedDraft = ""
    let maxHistoryCount = 100
    let mutable attachments: PendingAttachment list = []
    let mutable pendingFocusAttachmentId: Guid option = None
    let removeButtons = System.Collections.Generic.Dictionary<Guid, Control>()

    /// IME 组合中（CJK 候选选择）时 Up/Down/Enter 归输入法：TextPresenter.PreeditText 非空即让路，
    /// 不做历史召回与提交。Shift+选择本就要求 Modifiers=None，已天然绕行。
    let isImeComposing () =
        input.GetVisualDescendants()
        |> Seq.tryPick (function :? Avalonia.Controls.Presenters.TextPresenter as p -> Some p | _ -> None)
        |> Option.exists (fun presenter -> not (String.IsNullOrEmpty presenter.PreeditText))

    let tryFocusInput () =
        let act () =
            try
                if VisualExtensions.IsAttachedToVisualTree input && input.IsEffectivelyVisible && input.IsEnabled then
                    input.Focus() |> ignore
            with _ -> ()
        if Dispatcher.UIThread.CheckAccess() then act ()
        else Dispatcher.UIThread.Post(fun () -> act ())

    // V30/V31: 边框与占位符各走独立路径。边框先按拖拽/焦点算，再由禁用状态一统压回底色；占位符单独计算（禁用理由优先于拖拽提示），有正文时占位符本就不可见，绝不碰 input.Text。
    let updateShellVisual () =
        if isDraggingOver && enabled then
            shell.Background <- Tokens.surface
            shell.BorderBrush <- Tokens.accent
            shell.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accent.Color))
            dropHintBanner.IsVisible <- true
        elif input.IsFocused then
            shell.Background <- Tokens.surface
            shell.BorderBrush <- Tokens.accent
            shell.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accentSoft.Color))
            dropHintBanner.IsVisible <- false
        else
            shell.Background <- Tokens.surface
            shell.BorderBrush <- Tokens.hairline
            // 常驻 chrome 不投软阴影（软阴影只留给拖拽提示这类瞬时浮层）；
            // 与消息区的分层由 surface + hairline 承担，焦点态仍是无位移外环。
            shell.BoxShadow <- BoxShadows()
            dropHintBanner.IsVisible <- false
        // V31: 禁用时边框回最轻档，焦点也不提亮，避免“不可发送却像可输入”。
        if not enabled then
            shell.BorderBrush <- Tokens.hairline
            shell.BoxShadow <- BoxShadows()
        input.PlaceholderText <-
            if not enabled && not (String.IsNullOrWhiteSpace disabledReason) then
                disabledReason
            elif isDraggingOver && enabled then
                "释放文件以添加到当前会话附件"
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
        let uploading = attachments |> List.exists (fun a -> not a.ready && not a.failed)
        let hasFailed = attachments |> List.exists _.failed
        // 失败残留同样挡住发送：TryConsumeReady 只认全 ready；用户移除或重加后即恢复，不楔死。
        let canSend = enabled && not uploading && not hasFailed && (hasText || hasAttachment)
        Ui.setEnabled sendButton (if generating then canStop else canSend)
        Ui.setEnabled queueButton canSend
        Ui.setReservedActionVisible queueButton generating
        if generating && not canSend then
            queueButton.Opacity <- Tokens.opacityDisabled
            queueButton.IsHitTestVisible <- false
            queueButton.Focusable <- false
        let sendTip =
            if generating then
                if canStop then "停止生成 (Escape)" else "已停止，等待确认…"
            elif not enabled then
                if not (String.IsNullOrWhiteSpace disabledReason) then disabledReason else "连接已断开"
            elif uploading then
                "附件上传中，请稍候…"
            elif hasFailed then
                "有附件上传失败，移除或重新添加后发送"
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
                elif hasFailed then "有附件上传失败，移除或重新添加后发送"
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
        modelChip.PointerExited.Add(fun _ ->
            if not modelChip.IsFocused then
                modelChip.Background <- Brushes.Transparent
                modelChip.BorderBrush <- Tokens.hairline)
        // V29: 键盘焦点补描边（ActionBorder 自带外环阴影；这里补一条描边色，与悬停底色正交）。
        modelChip.GotFocus.Add(fun _ ->
            modelChip.Background <- Tokens.hover
            modelChip.BorderBrush <- Tokens.accent)
        modelChip.LostFocus.Add(fun _ ->
            modelChip.Background <- Brushes.Transparent
            modelChip.BorderBrush <- Tokens.hairline)
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
                // 组合中回车归输入法（候选确认），不提交。
                elif e.Key = Key.Enter && not (isImeComposing ()) then
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
                // 组合中 Up/Down 归输入法（候选导航），不召回历史。
                elif e.Key = Key.Up && e.KeyModifiers = KeyModifiers.None && not (isImeComposing ()) then
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
                // 组合中 Up/Down 归输入法（候选导航），不召回历史。
                elif e.Key = Key.Down && e.KeyModifiers = KeyModifiers.None && not (isImeComposing ()) then
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

    /// chip 的 Opacity 补间：入场/退场共用 MotionPolicy 时长 + DoubleTransition(Visual.OpacityProperty)，
    /// 与 MessageCard 操作条、复制键同一机制；减弱动效经 MotionPolicy.isReduced 门控降为瞬时。
    /// 只补间 Opacity：几何、焦点、移除流程与读屏播报均不经过这里。
    let attachmentChipTransitions () =
        let transitions = Avalonia.Animation.Transitions()
        let opacityTransition = Avalonia.Animation.DoubleTransition()
        opacityTransition.Property <- Visual.OpacityProperty
        opacityTransition.Duration <-
            if MotionPolicy.isReduced () then
                TimeSpan.Zero
            else
                MotionPolicy.duration MotionLedger.controlRowFade.TotalMilliseconds
        transitions.Add opacityTransition
        transitions

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

    /// 附件滚动窗按“整行”取高：用已渲染 chip 的实测高度对默认上限取整行倍数，避免半行露出。
    member private _.SnapAttachmentScroller() =
        if attachmentStrip.Children.Count > 0 then
            match attachmentStrip.Children.[0] with
            | :? Border as chip ->
                let h = chip.Bounds.Height
                if h > 0.0 then
                    let rows = max 1 (int (LayoutPolicy.attachmentDraftMaxHeight / h))
                    attachmentScroller.MaxHeight <- float rows * h
            | _ -> ()

    member private this.RenderAttachments() =
        // 现有 chip 的 Tag 记着各自 attachmentId：兄弟附件上传状态翻转会全量重建整条，
        // 只有“真正新加入”的 chip 才播入场补间，旧 chip 重建不重放。
        let renderedBefore =
            attachmentStrip.Children
            |> Seq.choose (fun child ->
                match child with
                | :? Border as existing ->
                    match existing.Tag with
                    | :? Guid as id -> Some id
                    | _ -> None
                | _ -> None)
            |> Set.ofSeq
        attachmentStrip.Children.Clear()
        removeButtons.Clear()
        for attachment in attachments do
            let icon =
                if attachment.failed then
                    // 失败态同槽位：15px icon slot 与 state 宽度不变，只换字形与颜色，可移除、可重新添加重试。
                    Icons.alert Tokens.danger
                elif attachment.ready then
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
                    Text = (if attachment.ready || attachment.failed then AttachmentRef.formatSize attachment.size else "上传中"),
                    FontSize = Tokens.fontMicro,
                    Foreground = (if attachment.failed then Tokens.danger else Tokens.textFaint),
                    Width = ControlMetrics.composerAttachmentStateWidth,
                    TextAlignment = TextAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center)
            let formattedSize =
                if attachment.ready || attachment.failed then AttachmentRef.formatSize attachment.size else "上传中"
            ToolTip.SetTip(
                size,
                if attachment.failed then sprintf "上传失败：%s，可移除或重新添加" formattedSize else sprintf "文件大小：%s" formattedSize)
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
            let chipLabel =
                if attachment.failed then
                    sprintf "附件上传失败：%s（%s），可移除或重新添加" attachment.fileName (AttachmentRef.formatSize attachment.size)
                else
                    // V34: 全角括号，与同文案“上传失败：%s（%s）”统一，不中英半角混排。
                    sprintf "附件：%s（%s）" attachment.fileName (AttachmentRef.formatSize attachment.size)
            // 附件 chip 与其它输入壳内次级面同一几何：走 Ui.groupingCard
            // （surfaceContainer + 最轻发丝边 + chip 密度），不再手抄一份边框三重奏。
            let chip =
                Ui.groupingCard
                    (Thickness(ControlMetrics.compactChipPaddingX, ControlMetrics.compactChipPaddingY))
                    row
                    Tokens.radiusSm
            // 失败档是「不同描边档」的既有意图：hairlineStrong 比分组卡默认 hairline 高一档，
            // 与 danger 字形配合突出失败态；圆角/内边距/底色仍走共享工厂。
            if attachment.failed then chip.BorderBrush <- Tokens.hairlineStrong
            // 只补间 Opacity：几何、焦点、移除流程（onClick 的 pendingFocusAttachmentId）与
            // 上传中读屏播报（SetLiveSetting）都不经过这里。
            chip.Transitions <- attachmentChipTransitions ()
            chip.Tag <- attachment.attachmentId
            if not (Set.contains attachment.attachmentId renderedBefore) then
                if MotionPolicy.isReduced () then
                    // 减弱动效：瞬时到位，连 0 起步都不发生。
                    chip.Opacity <- 1.0
                else
                    chip.Opacity <- 0.0
                    // 附件条已在可视化树中，挂载后写回 1.0 由上面的 Opacity 补间接管。
                    chip.AttachedToVisualTree.Add(fun _ -> chip.Opacity <- 1.0)
            ToolTip.SetTip(chip, chipLabel)
            Avalonia.Automation.AutomationProperties.SetName(chip, chipLabel)
            // V33: 上传中（size 行显“上传中”）向读屏直播；就绪/失败有静态名，不打扰。
            if not attachment.ready && not attachment.failed then
                Avalonia.Automation.AutomationProperties.SetLiveSetting(chip, Avalonia.Automation.AutomationLiveSetting.Polite)
            attachmentStrip.Children.Add chip
        attachmentScroller.IsVisible <- not (List.isEmpty attachments)
        // 滚动窗按“整行”取高：布局完成后用实测 chip 高度对默认上限取整，避免半行露出。
        Dispatcher.UIThread.Post(fun () -> this.SnapAttachmentScroller())
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
            // 数据型重渲染（兄弟附件完成/失败）：显式移除流程已通过 pendingFocusAttachmentId 占位，
            // 其它情况把当前聚焦的 remove 按钮原位恢复，避免焦点掉到页顶。
            if pendingFocusAttachmentId.IsNone then
                removeButtons
                |> Seq.tryPick (fun kv -> if kv.Value.IsFocused then Some kv.Key else None)
                |> Option.filter (fun id -> items |> List.exists (fun a -> a.attachmentId = id))
                |> Option.iter (fun id -> pendingFocusAttachmentId <- Some id)
            attachments <- items
            this.RenderAttachments()
            refreshSendState ()

    /// 历史召回中输入框显示的是旧发送，真正的半截草稿在 uncommittedDraft；
    /// 会话切换经由它保存，半截输入不会被召回项覆盖。
    member _.Text =
        if historyIndex <> -1 then uncommittedDraft
        elif isNull input.Text then ""
        else input.Text

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
                // V34: 禅期/状态文案统一中文标点，纯文本控件补 Name 供读屏读取。
                let status = TextBlock(Text = PendingMessage.status item, TextWrapping = TextWrapping.Wrap,
                                       FontSize = Tokens.fontMicro, Foreground = Tokens.textMuted)
                Avalonia.Automation.AutomationProperties.SetName(status, PendingMessage.status item)
                let row = DockPanel(LastChildFill = true)
                if PendingMessage.canRetry item then
                    let button = Ui.button Ui.Secondary "重试" (fun () -> retry (PendingMessage.invocationId item))
                    Ui.setEnabled button canRetry
                    DockPanel.SetDock(button, Dock.Right)
                    row.Children.Add button
                row.Children.Add status
                body.Children.Add row
                // 未确认消息是同级的次级表面：走 Ui.groupingCard 分组卡
                // （surfaceContainer + 发丝边，比常规 surface+border 卡片轻一档），不抢输入壳层级。
                pendingPanel.Children.Add(
                    Ui.groupingCard (Thickness(Tokens.space3, Tokens.space2)) body Tokens.radiusMd)
            // 首条消息创建会话失败时尚无侧栏条目，切走后仍要有找回入口。
            for conversationId, pending in elsewhere |> List.groupBy _.conversationId do
                let label = sprintf "查看另一个会话的未确认消息（%d 条）" pending.Length
                pendingPanel.Children.Add(Ui.button Ui.Secondary label (fun () -> openConversation conversationId))
            if not (List.isEmpty items) then
                let pendingNotice = Ui.caption "未确认内容只保留在当前窗口，关闭或刷新前请先确认保存。"
                Avalonia.Automation.AutomationProperties.SetName(pendingNotice, pendingNotice.Text)
                pendingPanel.Children.Add(pendingNotice)
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
        // V35: 分隔符对齐 ChatView/快捷键帮助的“/”惯例（Dialogs 中“Ctrl / ⌘ + N”、关闭提示“Esc / Enter”），不用“·”另造分隔。
        hintText.Text <- if value then "Enter 发送 / Shift+Enter 换行" else "Ctrl+Enter 发送 / Enter 换行"
        refreshSendState ()

    /// 非 compact 路径的水平留白：按 Composer 自身宽度（父级 chatColumn 给出）经
    /// LayoutPolicy.horizontalInset 取档。Padding 只裁剪内容、不影响 Composer 自身
    /// Bounds，写入不触发 Bounds 变化——无布局反馈环。compact 档由 SetCompactMode
    /// 写固定收紧边距，两条路径由 compactMode 分派、不同时生效。
    member private this.ApplyComposerPadding() =
        let inset = LayoutPolicy.horizontalInset this.Bounds.Width
        this.Padding <- Thickness(inset, Tokens.space4, inset, Tokens.space3)

    /// 窄屏只收紧已有控件，不增加第二套输入逻辑：隐藏键盘提示、压缩边距与模型标签。
    member this.SetCompactMode(value: bool) =
        // compact 下提示行直接折叠；footer 高度由 modelChip.MinHeight 兜底，不塌陷。
        // 拖放提示与键位提示同级，一并折叠：不引入第二套显隐逻辑。
        hintText.IsVisible <- not value
        hintText.IsHitTestVisible <- not value
        dropAttachHint.IsVisible <- not value
        dropAttachHint.IsHitTestVisible <- not value
        modelCaption.MaxWidth <-
            if value then ControlMetrics.composerModelCompactMaxWidth else ControlMetrics.composerModelMaxWidth
        let actionSize = if value then LayoutPolicy.compactActionTarget else Tokens.iconButton
        Ui.setSquareTarget attachButton actionSize
        Ui.setSquareTarget sendButton actionSize
        Ui.setSquareTarget queueButton actionSize
        modelChip.MinHeight <- if value then LayoutPolicy.compactActionTarget else 0.0
        compactMode <- value
        this.Padding <-
            if value then
                // compact 档：固定收紧边距，不随宽度浮动（窄屏省地优先于呼吸感）。
                Thickness(Tokens.space3, Tokens.space3, Tokens.space3, Tokens.space2)
            else
                // 非 compact 档：水平留白随实际宽度取档（桌面 32/48 的呼吸感）。
                let inset = LayoutPolicy.horizontalInset this.Bounds.Width
                Thickness(inset, Tokens.space4, inset, Tokens.space3)

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
        // 水平留白按当前宽度取档：未挂树时 Bounds=0 落最窄档（= 旧 shellInset 值）。
        this.ApplyComposerPadding()
        // 宽度跨断点时刷新非 compact 留白；compact 档保留收紧边距、不参与。
        this.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty && not compactMode then
                this.ApplyComposerPadding())

        // 次级图标操作（附件/排队）与所有 Ui.iconButton 一致：不带一次性描边，30px ghost
        // 目标靠透明底 + hover 底区分，发送键保持 accent 实心。曾单给这两个补 hairline 轮廓，
        // 是「只有它们有」的全局歧异，按 Chat+Overlays 收敛要求移除。

        // attach/queue/send 三个 30px 目标间距 space1→space2：减少误触，呼吸感对齐 footer。
        let actionRow = Ui.hstack Tokens.space2 [ attachButton :> Control; queueButton :> Control; sendButton :> Control ]
        actionRow.VerticalAlignment <- VerticalAlignment.Bottom

        let inputRow = DockPanel(LastChildFill = true)
        DockPanel.SetDock(actionRow, Dock.Right)
        inputRow.Children.Add actionRow
        inputRow.Children.Add input

        let footerRow = DockPanel(LastChildFill = true)
        DockPanel.SetDock(modelChip, Dock.Left)
        // 拖放提示与模型芯片同 dock 在左侧（芯片之后、fill 的键位提示之前）：
        // 窄窗口下先省略两条提示文本，模型芯片不与任何文本重叠争位。
        DockPanel.SetDock(dropAttachHint, Dock.Left)
        // hint 不 dock、作为 fill 占剩余宽度并右对齐：长模型名或窄窗口下只省略提示文本，绝不与模型芯片重叠争位。
        footerRow.Children.Add modelChip
        footerRow.Children.Add dropAttachHint
        footerRow.Children.Add hintText

        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2)
        column.Children.Add attachmentScroller
        column.Children.Add inputRow
        // 输入壳内的上下分层：最轻档 hairline（Ui.hairline 取 borderSoft，是更上一档的标准分隔）。
        column.Children.Add(Border(Height = ControlMetrics.borderWidth, Background = Tokens.hairline, HorizontalAlignment = HorizontalAlignment.Stretch) :> Control)
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
                ControlMetrics.composerShellPaddingX,
                ControlMetrics.composerShellPaddingY)
        shell.Child <- shellGrid
        // 输入壳是常驻 chrome：不带软阴影；表面分层与焦点外环已足够，
        // 描边/底色补间走共享 transitions（reduced-motion 自动降级为瞬时）。
        shell.Transitions <- Ui.surfaceBorderedTransitions ()
        shell.BoxShadow <- BoxShadows()

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

        // V32: 分支显式挂在 hasFiles 上（先排除无文件）：有文件时每帧只置 Copy，无文件时每帧置 None，状态翻转才刷视觉，不再闪断。
        let handleDragOver (e: DragEventArgs) =
            let hasFiles =
                e.DataTransfer <> null &&
                (e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.TryGetFiles() <> null)
            if not hasFiles then
                e.DragEffects <- DragDropEffects.None
                if isDraggingOver then
                    isDraggingOver <- false
                    updateShellVisual ()
            else
                e.DragEffects <- DragDropEffects.Copy
                if not isDraggingOver then
                    isDraggingOver <- true
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
