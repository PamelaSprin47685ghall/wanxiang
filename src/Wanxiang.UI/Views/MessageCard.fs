namespace Wanxiang.UI

open System
open System.Globalization
open System.Text.Json
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Controls.Primitives
open Avalonia.Automation
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Wanxiang.Core

/// 消息级操作，由 AppShell 注入。
type MessageActions = {
    copyText: string -> unit
    regenerate: unit -> unit
    editAndFork: MessageView -> unit
    deleteMessage: uint64 -> unit
    downloadAttachment: string -> unit
    openLink: string -> unit
}

/// 渲染一条消息所需的上下文。
type MessageContext = {
    fontSize: float
    autoCollapseReasoning: bool
    /// 该条是否为流式进行中的临时消息
    streaming: bool
    /// 是否是最后一条助手消息（决定是否展示用量与「重新生成」）
    isLastAssistant: bool
    usage: GenerationUsage option
    /// 内容已丢失的附件（下载失败）
    missingAttachments: Set<string>
    brandAvatar: unit -> Control
}

/// 一条消息的可视化。
///
/// 与旧实现的差别：助手回复走真正的块级 Markdown；工具调用有独立卡片；
/// 操作条悬停即现且不占文档流；用户气泡与助手正文在视觉重量上明确分层。
module MessageCard =

    let private handCursor = new Cursor(StandardCursorType.Hand)

    /// 用户气泡选中底：半透明白只把深底提亮一档，浅/深主题下暖白字都保持可读。
    /// 主题无关因此不进 Tokens，只在气泡内使用。
    let private userBubbleSelection = SolidColorBrush(Color.FromArgb(0x59uy, 255uy, 255uy, 255uy)) :> IBrush

    /// 大段详情统一使用“限高阅读窗 → 主动展开全文”的二阶段 contract。
    /// 首次展开不会把当前阅读位置瞬间推走数屏；需要全文时用户仍有明确入口。
    let private detailViewport (content: Control) (initiallyVisible: bool) : Control * (bool -> unit) =
        let scroller =
            ScrollViewer(
                Content = content,
                ClipToBounds = true,
                MaxHeight = LayoutPolicy.expandedDetailMaxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                IsVisible = initiallyVisible)
        let mutable visible = initiallyVisible
        let mutable full = false
        let mutable toggleFull: unit -> unit = ignore
        let expandButton = Ui.button Ui.Ghost "展开全部" (fun () -> toggleFull ())
        expandButton.HorizontalAlignment <- HorizontalAlignment.Left
        expandButton.Cursor <- handCursor
        expandButton.Focusable <- true
        expandButton.IsVisible <- false
        ToolTip.SetTip(expandButton, "展开查看全部内容")
        Avalonia.Automation.AutomationProperties.SetName(expandButton, "展开全部")
        let refreshButton () =
            let clipped = scroller.Extent.Height > scroller.Viewport.Height + 1.0
            expandButton.IsVisible <- visible && (full || clipped)
        scroller.LayoutUpdated.Add(fun _ ->
            if visible then refreshButton ())
        toggleFull <- fun () ->
            full <- not full
            scroller.MaxHeight <- if full then Double.PositiveInfinity else LayoutPolicy.expandedDetailMaxHeight
            Ui.setButtonText expandButton (if full then "收回限高" else "展开全部")
            ToolTip.SetTip(expandButton, if full then "收回到限高区域" else "展开查看全部内容")
            Avalonia.Automation.AutomationProperties.SetName(expandButton, if full then "收回限高" else "展开全部")
            Dispatcher.UIThread.Post refreshButton
        scroller.PropertyChanged.Add(fun args ->
            if args.Property = ScrollViewer.ExtentProperty || args.Property = ScrollViewer.ViewportProperty then
                refreshButton ())
        let host =
            StackPanel(
                Orientation = Orientation.Vertical,
                Spacing = Tokens.space1,
                IsVisible = initiallyVisible)
        host.Children.Add scroller
        host.Children.Add expandButton
        let setVisible value =
            visible <- value
            scroller.IsVisible <- value
            host.IsVisible <- value
            if not value && full then
                full <- false
                scroller.MaxHeight <- LayoutPolicy.expandedDetailMaxHeight
                Ui.setButtonText expandButton "展开全部"
                ToolTip.SetTip(expandButton, "展开查看全部内容")
                Avalonia.Automation.AutomationProperties.SetName(expandButton, "展开全部")
            Dispatcher.UIThread.Post refreshButton
        host :> Control, setVisible

    let private formatDuration (ms: int64) =
        if ms >= 1000L then sprintf "%.1fs" (float ms / 1000.0)
        elif ms > 0L then sprintf "%dms" (int ms)
        else ""

    let private technicalText (text: string) (size: float) (brush: IBrush) =
        SelectableTextBlock(
            Text = text,
            FontFamily = Tokens.monoFontFamily,
            FontSize = size,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = ReadingRhythm.technicalLineHeight size,
            SelectionBrush = Tokens.accentSoft)

    let private copyPayloadToClipboard (visual: Visual) (text: string) (onCopied: unit -> unit) =
        try
            match TopLevel.GetTopLevel visual with
            | null -> ()
            | top ->
                match top.Clipboard with
                | null -> ()
                | clip ->
                    clip.SetTextAsync text |> ignore
                    onCopied ()
        with _ -> ()

    /// 图标字形与脚注文本基线对齐：只下沉 iconBaselineNudge，不改按钮外尺寸。
    let private applyIconBaselineNudge (button: Border) =
        match button.Child with
        | null -> ()
        | glyph ->
            glyph.VerticalAlignment <- VerticalAlignment.Center
            glyph.Margin <- Thickness(0.0, Tokens.iconBaselineNudge, 0.0, 0.0)

    let private createCopyButton (tipText: string) (accessibleName: string) (getText: unit -> string) : Border =
        let button = Ui.iconButton Icons.copy tipText
        button.Focusable <- true
        button.Margin <- Thickness 0.0
        button.Padding <- Thickness 0.0
        button.Cursor <- handCursor
        Ui.setSquareTarget button LayoutPolicy.inlineActionTarget
        applyIconBaselineNudge button
        ToolTip.SetTip(button, tipText)
        Avalonia.Automation.AutomationProperties.SetName(button, accessibleName)
        Avalonia.Automation.AutomationProperties.SetHelpText(button, accessibleName)
        Avalonia.Automation.AutomationProperties.SetLiveSetting(button, AutomationLiveSetting.Polite)
        let mutable copyTimer: DispatcherTimer option = None
        let stopTimer () =
            match copyTimer with
            | Some t ->
                t.Stop()
                copyTimer <- None
            | None -> ()
        let restoreDefaultState () =
            stopTimer ()
            Ui.setIcon button Icons.copy Tokens.textMuted
            Ui.setSquareTarget button LayoutPolicy.inlineActionTarget
            applyIconBaselineNudge button
            ToolTip.SetTip(button, tipText)
            Avalonia.Automation.AutomationProperties.SetName(button, accessibleName)
            Avalonia.Automation.AutomationProperties.SetHelpText(button, accessibleName)
        let doCopy () =
            let payload = getText ()
            if not (String.IsNullOrEmpty payload) then
                copyPayloadToClipboard button payload (fun () ->
                    stopTimer ()
                    Ui.setIcon button Icons.check Tokens.success
                    Ui.setSquareTarget button LayoutPolicy.inlineActionTarget
                    applyIconBaselineNudge button
                    ToolTip.SetTip(button, "已复制")
                    Avalonia.Automation.AutomationProperties.SetName(button, "已复制")
                    Avalonia.Automation.AutomationProperties.SetHelpText(button, "已复制到剪贴板")
                    let timer = new DispatcherTimer(Interval = MotionLedger.copyConfirmationHold)
                    timer.Tick.Add(fun _ ->
                        if copyTimer = Some timer then
                            restoreDefaultState ())
                    copyTimer <- Some timer
                    timer.Start())
        Ui.onClick button doCopy
        button.DetachedFromVisualTree.Add(fun _ -> restoreDefaultState ())
        button

    /// 思考过程：左缘竖线 + 可折叠。流式期间默认展开，让人看到模型在动。
    /// 支持无障碍、平滑悬浮反馈与键盘 Enter/Space 激活。
    let private reasoningBlock (ctx: MessageContext) (reasoning: string) (durationMs: int64 option) : Control =
        let collapsed = not ctx.streaming && ctx.autoCollapseReasoning
        let bodyText =
            SelectableTextBlock(
                Text = reasoning,
                TextWrapping = TextWrapping.Wrap,
                FontSize = ctx.fontSize - 1.5,
                Foreground = Tokens.textMuted,
                LineHeight = ReadingRhythm.secondaryLineHeight (ctx.fontSize - 1.5),
                SelectionBrush = Tokens.accentSoft)
        let body, setBodyVisible = detailViewport (bodyText :> Control) (not collapsed)
        body.Margin <- Thickness(0.0, Tokens.space2, 0.0, 0.0)
        let mutable bodyVisible = not collapsed
        let chevronHost =
            Border(
                Width = Tokens.iconGlyph,
                Height = Tokens.iconGlyph,
                MinWidth = Tokens.iconGlyph,
                MinHeight = Tokens.iconGlyph,
                Margin = Thickness 0.0,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center)
        let rotateTransform = RotateTransform(if bodyVisible then 90.0 else 0.0)
        let chevronTransitions = Avalonia.Animation.Transitions()
        let rotateTransition = Avalonia.Animation.DoubleTransition()
        rotateTransition.Property <- RotateTransform.AngleProperty
        rotateTransition.Duration <- MotionPolicy.duration 150
        chevronTransitions.Add rotateTransition
        rotateTransform.Transitions <- chevronTransitions
        let chevronGlyph = Icons.chevronRight Tokens.textMuted
        chevronGlyph.HorizontalAlignment <- HorizontalAlignment.Center
        chevronGlyph.VerticalAlignment <- VerticalAlignment.Center
        chevronGlyph.RenderTransform <- rotateTransform
        chevronGlyph.RenderTransformOrigin <- RelativePoint.Center
        chevronHost.Child <- chevronGlyph
        let syncChevron () =
            rotateTransform.Angle <- if bodyVisible then 90.0 else 0.0
        let titleRun = Run()
        let durRun = Run()
        let syncStateText () =
            let stateDesc = if bodyVisible then " (收起)" else " (展开)"
            let baseText = if ctx.streaming then "正在思考" else "已深度思考"
            titleRun.Text <- baseText + stateDesc
            titleRun.Foreground <- Tokens.textMuted
            durRun.Foreground <- Tokens.textMuted
        let durationText =
            match durationMs with
            | Some ms ->
                let formatted = formatDuration ms
                if String.IsNullOrWhiteSpace formatted then ""
                else
                    sprintf " · %s" formatted
            | None -> ""
        let caption =
            let tb =
                TextBlock(
                    FontSize = Tokens.fontCaption,
                    FontWeight = FontWeight.Medium,
                    LineHeight = ReadingRhythm.captionLineHeight,
                    Foreground = Tokens.textMuted,
                    VerticalAlignment = VerticalAlignment.Center)
            tb.Inlines.Add titleRun
            if not (String.IsNullOrWhiteSpace durationText) then
                durRun.Text <- durationText
                durRun.Foreground <- Tokens.textMuted
                tb.Inlines.Add durRun
            tb
        let headerRow = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, VerticalAlignment = VerticalAlignment.Center)
        headerRow.Cursor <- handCursor
        headerRow.Children.Add chevronHost
        headerRow.Children.Add caption
        let headerTransitions = Avalonia.Animation.Transitions()
        let opacityTransition = Avalonia.Animation.DoubleTransition()
        opacityTransition.Property <- Visual.OpacityProperty
        opacityTransition.Duration <- MotionPolicy.duration 120
        headerTransitions.Add opacityTransition
        let bgTransition = Avalonia.Animation.BrushTransition()
        bgTransition.Property <- Border.BackgroundProperty
        bgTransition.Duration <- MotionPolicy.duration 120
        headerTransitions.Add bgTransition
        let borderTransition = Avalonia.Animation.BrushTransition()
        borderTransition.Property <- Border.BorderBrushProperty
        borderTransition.Duration <- MotionPolicy.duration 120
        headerTransitions.Add borderTransition
        let header =
            ActionBorder(
                CornerRadius = CornerRadius Tokens.radiusSm,
                Padding = Thickness(Tokens.space1, 3.0),
                Margin = Thickness 0.0,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = Thickness 1.0,
                Cursor = handCursor,
                Focusable = true,
                Transitions = headerTransitions,
                Child = headerRow)
        let syncHeaderName () =
            let actionName = if bodyVisible then "收起思考过程" else "展开思考过程"
            Avalonia.Automation.AutomationProperties.SetName(
                header, actionName)
            Avalonia.Automation.AutomationProperties.SetHelpText(
                header, sprintf "%s思考过程" (if bodyVisible then "点击折叠" else "点击展开"))
            Avalonia.Automation.AutomationProperties.SetRole(header, AutomationRole.Button)
            Avalonia.Automation.AutomationProperties.SetExpanded(header, bodyVisible)
            ToolTip.SetTip(header, if bodyVisible then "收起思考过程" else "展开思考过程")
            syncStateText ()
        let toggle () =
            bodyVisible <- not bodyVisible
            setBodyVisible bodyVisible
            syncChevron ()
            syncHeaderName ()
        let updateHeaderVisual () =
            if header.IsFocused then
                header.BorderBrush <- Tokens.accent
                header.Background <- Tokens.hover
                header.Opacity <- 1.0
            elif header.IsPointerOver then
                header.BorderBrush <- Tokens.line
                header.Background <- Tokens.hover
                header.Opacity <- 0.9
            else
                header.BorderBrush <- Brushes.Transparent
                header.Background <- Brushes.Transparent
                header.Opacity <- 1.0
        header.PointerEntered.Add(fun _ -> updateHeaderVisual ())
        header.PointerExited.Add(fun _ -> updateHeaderVisual ())
        header.GotFocus.Add(fun _ -> updateHeaderVisual ())
        header.LostFocus.Add(fun _ -> updateHeaderVisual ())
        Ui.onClick header toggle
        syncChevron ()
        syncHeaderName ()
        let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        stack.Children.Add header
        stack.Children.Add body
        Border(
            Background = Tokens.surfaceSoft,
            BorderBrush = Tokens.borderSoft,
            BorderThickness = Thickness(2.0, 0.0, 0.0, 0.0),
            Padding = Thickness(Tokens.space3, Tokens.space1, 0.0, Tokens.space1),
            Margin = Thickness(0.0, 0.0, 0.0, Tokens.space3),
            Child = stack)
        :> Control

    /// 工具调用卡片：名称 + 状态 + 参数摘要，展开后看完整参数与结果。
    ///
    /// 刻意做成与代码块同一量级的卡片：工具调用是模型行为的一部分，
    /// 缩成一个灰色小标签会让人以为它不重要。
    let private prettyFormatJson (raw: string) : string =
        if String.IsNullOrWhiteSpace raw then ""
        else
            let trimmed = raw.Trim()
            if (trimmed.StartsWith "{" && trimmed.EndsWith "}") || (trimmed.StartsWith "[" && trimmed.EndsWith "]") then
                try
                    use doc = JsonDocument.Parse trimmed
                    let options = JsonSerializerOptions(WriteIndented = true)
                    JsonSerializer.Serialize(doc.RootElement, options)
                with _ -> raw
            else raw

    let private formatToolArgsPreview (argsJson: string) : int * string =
        if String.IsNullOrWhiteSpace argsJson then 0, ""
        else
            let trimmed = argsJson.Trim()
            try
                use doc = JsonDocument.Parse trimmed
                match doc.RootElement.ValueKind with
                | JsonValueKind.Object ->
                    let props = [ for p in doc.RootElement.EnumerateObject() -> p.Name, p.Value ]
                    let count = props.Length
                    if count = 0 then 0, "无参数"
                    else
                        let formatVal (v: JsonElement) =
                            match v.ValueKind with
                            | JsonValueKind.String ->
                                let s = v.GetString().Replace('\n', ' ').Replace('\r', ' ').Trim()
                                if s.Length <= 32 then sprintf "\"%s\"" s else sprintf "\"%s…\"" (s.Substring(0, 30))
                            | JsonValueKind.Number -> v.GetRawText()
                            | JsonValueKind.True -> "true"
                            | JsonValueKind.False -> "false"
                            | JsonValueKind.Null -> "null"
                            | JsonValueKind.Array -> sprintf "[%d]" (v.GetArrayLength())
                            | JsonValueKind.Object -> sprintf "{%d}" (v.EnumerateObject() |> Seq.length)
                            | _ -> v.GetRawText()
                        let pairs = props |> List.map (fun (name, v) -> sprintf "%s: %s" name (formatVal v))
                        let joined = String.Join(", ", pairs)
                        count, (if joined.Length > 96 then joined.Substring(0, 95) + "…" else joined)
                | JsonValueKind.Array ->
                    let count = doc.RootElement.GetArrayLength()
                    count, sprintf "[%d 项]" count
                | _ ->
                    let s = trimmed.Replace('\n', ' ').Replace('\r', ' ')
                    1, (if s.Length > 96 then s.Substring(0, 95) + "…" else s)
            with _ ->
                let single = trimmed.Replace('\n', ' ').Replace('\r', ' ')
                1, (if single.Length > 96 then single.Substring(0, 95) + "…" else single)

    let private tryExtractToolError (resultOpt: string option) : string option =
        match resultOpt with
        | Some res when not (String.IsNullOrWhiteSpace res) ->
            let trimmed = res.Trim()
            if trimmed.StartsWith "error:" then
                let msg = trimmed.Substring(6).Trim()
                Some(if String.IsNullOrWhiteSpace msg then "执行失败" else msg)
            elif trimmed.StartsWith "{" && trimmed.EndsWith "}" then
                try
                    use doc = JsonDocument.Parse trimmed
                    let root = doc.RootElement
                    if root.ValueKind = JsonValueKind.Object then
                        let mutable errProp = Unchecked.defaultof<_>
                        if root.TryGetProperty("error", &errProp) then
                            match errProp.ValueKind with
                            | JsonValueKind.String -> Some(errProp.GetString())
                            | JsonValueKind.Object ->
                                let mutable msgProp = Unchecked.defaultof<_>
                                if errProp.TryGetProperty("message", &msgProp) && msgProp.ValueKind = JsonValueKind.String then
                                    Some(msgProp.GetString())
                                else Some(errProp.GetRawText())
                            | _ -> Some(errProp.GetRawText())
                        else None
                    else None
                with _ -> None
            else None
        | _ -> None

    let private toolCallCard (ctx: MessageContext) (call: ToolCallView) : Control =
        let running = call.result.IsNone
        let hasError =
            match call.result with
            | Some res when not (String.IsNullOrWhiteSpace res) ->
                let trimmed = res.Trim()
                if trimmed.StartsWith "{" && trimmed.EndsWith "}" then
                    try
                        use doc = JsonDocument.Parse trimmed
                        let root = doc.RootElement
                        root.ValueKind = JsonValueKind.Object
                        && (root.TryGetProperty("error", ref (Unchecked.defaultof<_>))
                            || (let mutable isErr = Unchecked.defaultof<_>
                                root.TryGetProperty("isError", &isErr) && isErr.ValueKind = JsonValueKind.True))
                    with _ -> false
                else false
            | _ -> false
        let statusBrush: IBrush =
            if running then Tokens.accent
            elif hasError then Tokens.danger
            else Tokens.success
        let statusBg: IBrush =
            if running then Tokens.surfaceSoft :> IBrush
            elif hasError then Tokens.dangerSoft :> IBrush
            else Tokens.surfaceSoft :> IBrush
        let statusBadgeText =
            if running then "执行中"
            elif hasError then "执行失败"
            else "已完成"
        let icon: Control =
            if running then Ui.spinner 14.0
            elif hasError then Icons.alert statusBrush
            else Icons.wrench statusBrush
        icon.VerticalAlignment <- VerticalAlignment.Center
        let statusGlyph: Control =
            if running then Ui.spinner 11.0
            elif hasError then Icons.alert statusBrush
            else Icons.check statusBrush
        statusGlyph.VerticalAlignment <- VerticalAlignment.Center
        statusGlyph.Margin <- Thickness(0.0, 0.0, Tokens.space1, 0.0)
        let name =
            TextBlock(
                Text = call.name,
                FontFamily = Tokens.monoFontFamily,
                FontSize = Tokens.fontSmall,
                FontWeight = FontWeight.SemiBold,
                Foreground = Tokens.text,
                VerticalAlignment = VerticalAlignment.Center)
        let statusText =
            TextBlock(
                Text = statusBadgeText,
                FontSize = Tokens.fontMicro,
                Foreground = statusBrush,
                VerticalAlignment = VerticalAlignment.Center)
        let statusPillContent = Ui.hstack 0.0 [ statusGlyph; statusText :> Control ]
        let state =
            Border(
                Background = statusBg,
                BorderBrush = Tokens.borderSoft,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusPill,
                Padding = Thickness(Tokens.space2, 2.0),
                Margin = Thickness 0.0,
                VerticalAlignment = VerticalAlignment.Center,
                Child = statusPillContent)
        /// 未展开时也给一行参数摘要：多数时候用户只想确认「传了什么」。
        let argCount, previewText = formatToolArgsPreview call.argumentsJson
        let summaryText =
            previewText
        let summary =
            TextBlock(
                Text = summaryText,
                FontFamily = Tokens.monoFontFamily,
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textMuted,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsVisible = not (String.IsNullOrWhiteSpace summaryText),
                Margin = Thickness(0.0, Tokens.space1, 0.0, 0.0))
        let errorSummaryOpt = if hasError then tryExtractToolError call.result else None
        let errorBanner =
            let errGlyph = Icons.alert Tokens.danger
            errGlyph.VerticalAlignment <- VerticalAlignment.Center
            let errText =
                TextBlock(
                    Text = defaultArg errorSummaryOpt "执行失败：未返回正常结果",
                    FontFamily = Tokens.monoFontFamily,
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.danger,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center)
            let bannerContent = Ui.hstack Tokens.space1 [ errGlyph; errText :> Control ]
            Border(
                Background = Tokens.dangerSoft,
                BorderBrush = Tokens.borderSoft,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusSm,
                Padding = Thickness(Tokens.space2, Tokens.space1),
                Margin = Thickness(0.0, Tokens.space1, 0.0, 0.0),
                IsVisible = hasError,
                Child = bannerContent)
        let rawPayloadText () =
            [ if not (String.IsNullOrWhiteSpace call.argumentsJson) then sprintf "// 参数 (Arguments)\n%s" call.argumentsJson
              match call.result with
              | Some res when not (String.IsNullOrWhiteSpace res) -> sprintf "// 结果 (Result)\n%s" res
              | _ -> () ]
            |> String.concat "\n\n"
        let detailPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2)
        let createDetailSection (label: string) (payload: string) (isError: bool) =
            let header = DockPanel(LastChildFill = false)
            let sectionTitle =
                TextBlock(
                    Text = label,
                    FontFamily = Tokens.fontFamily,
                    FontSize = Tokens.fontMicro,
                    FontWeight = FontWeight.Medium,
                    Foreground = (if isError then Tokens.danger else Tokens.textMuted),
                    VerticalAlignment = VerticalAlignment.Center)
            let copyBtn = createCopyButton (sprintf "复制%s" label) (sprintf "复制工具%s" label) (fun () -> payload)
            DockPanel.SetDock(sectionTitle, Dock.Left)
            DockPanel.SetDock(copyBtn, Dock.Right)
            header.Children.Add sectionTitle
            header.Children.Add copyBtn
            let formattedPayload = prettyFormatJson payload
            let contentText = technicalText formattedPayload Tokens.fontCaption (if isError then Tokens.danger else Tokens.codeText)
            let codeBox =
                Border(
                    Background = Tokens.codeBlockBackground,
                    BorderBrush = Tokens.borderSoft,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius Tokens.radiusSm,
                    Padding = Thickness(Tokens.space3, Tokens.space2),
                    Child = contentText)
            let section = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space1)
            section.Children.Add header
            section.Children.Add codeBox
            section
        if not (String.IsNullOrWhiteSpace call.argumentsJson) then
            detailPanel.Children.Add(createDetailSection "调用参数" call.argumentsJson false)
        match call.result with
        | Some result when not (String.IsNullOrWhiteSpace result) ->
            detailPanel.Children.Add(createDetailSection (if hasError then "执行结果 (异常)" else "执行结果") result hasError)
        | _ -> ()
        let detail, setDetailVisible = detailViewport (detailPanel :> Control) false
        detail.Margin <- Thickness(0.0, Tokens.space2, 0.0, 0.0)
        let chevronHost =
            Border(
                Width = Tokens.iconGlyph,
                Height = Tokens.iconGlyph,
                MinWidth = Tokens.iconGlyph,
                MinHeight = Tokens.iconGlyph,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center)
        let toolCopyButton =
            createCopyButton "复制工具参数与结果" (sprintf "复制工具 %s 参数与结果" call.name) rawPayloadText
        toolCopyButton.Margin <- Thickness(0.0, 0.0, Tokens.space1, 0.0)
        toolCopyButton.VerticalAlignment <- VerticalAlignment.Center
        let headerActions = Ui.hstack Tokens.space1 [ toolCopyButton :> Control; chevronHost :> Control ]
        headerActions.VerticalAlignment <- VerticalAlignment.Center
        let headerDock = DockPanel(LastChildFill = false)
        let left = Ui.hstack Tokens.space2 [ icon; name :> Control; state :> Control ]
        DockPanel.SetDock(left, Dock.Left)
        DockPanel.SetDock(headerActions, Dock.Right)
        headerDock.Children.Add left
        headerDock.Children.Add headerActions
        let headerContent = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        headerContent.Children.Add headerDock
        headerContent.Children.Add summary
        if hasError then headerContent.Children.Add errorBanner
        let headerRow =
            ActionBorder(
                CornerRadius = CornerRadius Tokens.radiusSm,
                Margin = Thickness 0.0,
                Background = Brushes.Transparent,
                Cursor = handCursor,
                Focusable = true,
                Child = headerContent)
        let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        stack.Children.Add headerRow
        stack.Children.Add detail
        let host =
            ActionBorder(
                Background = Tokens.surface,
                BorderBrush = Tokens.border,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusMd,
                Padding = Thickness(Tokens.space3, Tokens.space3),
                Margin = Thickness(0.0, 0.0, 0.0, Tokens.space3),
                Child = stack)
        let mutable detailVisible = false
        let toolRotate = RotateTransform(if detailVisible then 90.0 else 0.0)
        let toolChevronTransitions = Avalonia.Animation.Transitions()
        let toolRotateTransition = Avalonia.Animation.DoubleTransition()
        toolRotateTransition.Property <- RotateTransform.AngleProperty
        toolRotateTransition.Duration <- MotionPolicy.duration 150
        toolChevronTransitions.Add toolRotateTransition
        toolRotate.Transitions <- toolChevronTransitions
        let toolChevronGlyph = Icons.chevronRight Tokens.textFaint
        toolChevronGlyph.HorizontalAlignment <- HorizontalAlignment.Center
        toolChevronGlyph.VerticalAlignment <- VerticalAlignment.Center
        toolChevronGlyph.RenderTransform <- toolRotate
        toolChevronGlyph.RenderTransformOrigin <- RelativePoint.Center
        chevronHost.Child <- toolChevronGlyph
        let syncChevron () =
            toolRotate.Angle <- if detailVisible then 90.0 else 0.0
        let syncToolName () =
            let statusText = statusBadgeText
            let argInfo =
                if argCount > 0 && not (String.IsNullOrWhiteSpace previewText) then
                    sprintf "，%d 个参数：%s" argCount previewText
                elif not (String.IsNullOrWhiteSpace previewText) then
                    sprintf "，参数：%s" previewText
                else
                    ""
            let label = sprintf "%s工具调用 %s（%s%s）" (if detailVisible then "收起" else "展开") call.name statusText argInfo
            Avalonia.Automation.AutomationProperties.SetName(
                host, sprintf "%s工具调用 %s" (if detailVisible then "收起" else "展开") call.name)
            Avalonia.Automation.AutomationProperties.SetName(headerRow, sprintf "%s工具调用 %s" (if detailVisible then "收起" else "展开") call.name)
            Avalonia.Automation.AutomationProperties.SetHelpText(headerRow, label)
            Avalonia.Automation.AutomationProperties.SetItemStatus(headerRow, sprintf "调用工具 · %s（%s）" call.name statusText)
            Avalonia.Automation.AutomationProperties.SetHelpText(headerRow, if detailVisible then "收起参数与结果" else "展开查看参数与结果")
            Avalonia.Automation.AutomationProperties.SetHelpText(host, if detailVisible then "收起参数与结果" else "展开查看参数与结果")
            ToolTip.SetTip(headerRow, if detailVisible then "点击收起参数与结果" else "点击展开参数与结果")
            ToolTip.SetTip(host, if detailVisible then "点击收起参数与结果" else "点击展开参数与结果")
        let toggle () =
            detailVisible <- not detailVisible
            setDetailVisible detailVisible
            summary.IsVisible <- not detailVisible && not (String.IsNullOrWhiteSpace summaryText)
            syncChevron ()
            syncToolName ()
        host.SetInvokeAction toggle
        headerRow.SetInvokeAction toggle
        headerRow.PointerReleased.Add(fun e ->
            if not e.Handled && headerRow.IsEnabled && headerRow.IsHitTestVisible && e.InitialPressMouseButton = MouseButton.Left then
                if not toolCopyButton.IsPointerOver then
                    e.Handled <- true
                    toggle ())
        headerRow.KeyDown.Add(fun e ->
            if not e.Handled && headerRow.IsFocused && headerRow.IsEnabled && (e.Key = Key.Enter || e.Key = Key.Space) then
                e.Handled <- true
                toggle ())
        headerRow.PointerEntered.Add(fun _ ->
            headerRow.Background <- Tokens.hover)
        headerRow.PointerExited.Add(fun _ ->
            headerRow.Background <- Brushes.Transparent)
        syncChevron ()
        syncToolName ()
        host :> Control

    /// 附件：图片给缩略入口，其余给文件条。
    let private attachmentRow (ctx: MessageContext) (actions: MessageActions) (attachment: AttachmentRef) : Control =
        let missing = ctx.missingAttachments.Contains attachment.sha256
        let icon = if AttachmentRef.isImage attachment then Icons.image else Icons.file
        let glyph = icon (if missing then Tokens.textFaint else Tokens.textMuted)
        glyph.VerticalAlignment <- VerticalAlignment.Center
        let name =
            TextBlock(
                Text = attachment.fileName,
                FontSize = Tokens.fontSmall,
                Foreground = (if missing then Tokens.textFaint else Tokens.text),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = ControlMetrics.attachmentNameMaxWidth,
                VerticalAlignment = VerticalAlignment.Center)
        let meta =
            TextBlock(
                Text = (if missing then "内容已丢失" else AttachmentRef.formatSize attachment.size),
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textFaint,
                VerticalAlignment = VerticalAlignment.Center)
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, VerticalAlignment = VerticalAlignment.Center)
        row.Children.Add glyph
        row.Children.Add name
        row.Children.Add meta
        if not missing then
            let download = Icons.download Tokens.textFaint
            download.VerticalAlignment <- VerticalAlignment.Center
            row.Children.Add download
        let host: Border =
            if missing then
                Border(
                    Background = Tokens.surface,
                    BorderBrush = Tokens.border,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius Tokens.radiusMd,
                    Padding = Thickness(Tokens.space3, ControlMetrics.attachmentRowPaddingY),
                    Margin = Thickness(0.0, Tokens.space1, 0.0, 0.0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = row)
            else
                ActionBorder(
                    Background = Tokens.surface,
                    BorderBrush = Tokens.border,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius Tokens.radiusMd,
                    Padding = Thickness(Tokens.space3, ControlMetrics.attachmentRowPaddingY),
                    Margin = Thickness(0.0, Tokens.space1, 0.0, 0.0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Cursor = handCursor,
                    Focusable = true,
                    Child = row)
        if not missing then
            ToolTip.SetTip(host, "下载附件")
            Avalonia.Automation.AutomationProperties.SetName(host, sprintf "下载附件 %s" attachment.fileName)
            host.PointerEntered.Add(fun _ -> host.Background <- Tokens.surfaceRaised)
            host.PointerExited.Add(fun _ -> host.Background <- Tokens.surface)
            Ui.onClick host (fun () -> actions.downloadAttachment attachment.sha256)
        host :> Control

    /// 悬停操作条。绝对不占文档流，否则每次悬停都会推动整段排版。
    /// 操作按钮常驻低透明度。
    ///
    /// 只在 hover 时出现等于在触屏上不存在、在键盘上摸不到；
    /// 常驻一档淡显既可发现，又不至于跟正文抢注意力。
    /// 不能再低：更淡的图标在浅纸背景上已经读不出形状。
    let idleActionOpacity = Tokens.opacitySubtle

    /// 消息操作按钮。放在脚注行里随文档流排布——
    /// 早先做成浮在消息上方的悬浮条，常驻显示后就会压住头像和气泡边角。
    let private actionButtons (message: MessageView) (ctx: MessageContext) (actions: MessageActions) : StackPanel =
        let rowTransitions = Avalonia.Animation.Transitions()
        let opacityTransition = Avalonia.Animation.DoubleTransition()
        opacityTransition.Property <- Visual.OpacityProperty
        opacityTransition.Duration <- MotionPolicy.duration 150
        rowTransitions.Add opacityTransition
        let row =
            StackPanel(
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.space1,
                Margin = Thickness 0.0,
                Opacity = idleActionOpacity,
                Transitions = rowTransitions,
                VerticalAlignment = VerticalAlignment.Center)
        let addButton (icon: IBrush -> Control) (tip: string) (actionName: string) (help: string) (action: unit -> unit) =
            let button = Ui.iconButton icon tip
            button.Focusable <- true
            button.Margin <- Thickness 0.0
            button.Padding <- Thickness 0.0
            button.Cursor <- handCursor
            Ui.setSquareTarget button LayoutPolicy.inlineActionTarget
            applyIconBaselineNudge button
            ToolTip.SetTip(button, tip)
            Avalonia.Automation.AutomationProperties.SetName(button, actionName)
            Avalonia.Automation.AutomationProperties.SetHelpText(button, help)
            Ui.onClick button action
            row.Children.Add button
        let addCopyButton (text: string) =
            let button = Ui.iconButton Icons.copy "复制"
            button.Focusable <- true
            button.Margin <- Thickness 0.0
            button.Padding <- Thickness 0.0
            button.Cursor <- handCursor
            Ui.setSquareTarget button LayoutPolicy.inlineActionTarget
            applyIconBaselineNudge button
            ToolTip.SetTip(button, "复制")
            Avalonia.Automation.AutomationProperties.SetName(button, "复制")
            Avalonia.Automation.AutomationProperties.SetHelpText(button, "复制消息正文")
            Avalonia.Automation.AutomationProperties.SetLiveSetting(button, AutomationLiveSetting.Polite)
            let mutable copyTimer: DispatcherTimer option = None
            let stopTimer () =
                match copyTimer with
                | Some t ->
                    t.Stop()
                    copyTimer <- None
                | None -> ()
            let restoreDefaultState () =
                stopTimer ()
                Ui.setIcon button Icons.copy Tokens.textMuted
                Ui.setSquareTarget button LayoutPolicy.inlineActionTarget
                applyIconBaselineNudge button
                ToolTip.SetTip(button, "复制")
                Avalonia.Automation.AutomationProperties.SetName(button, "复制")
                Avalonia.Automation.AutomationProperties.SetHelpText(button, "复制消息正文")
            let doCopy () =
                actions.copyText text
                stopTimer ()
                Ui.setIcon button Icons.check Tokens.success
                Ui.setSquareTarget button LayoutPolicy.inlineActionTarget
                applyIconBaselineNudge button
                ToolTip.SetTip(button, "已复制")
                Avalonia.Automation.AutomationProperties.SetName(button, "已复制")
                Avalonia.Automation.AutomationProperties.SetHelpText(button, "已复制消息正文")
                let timer = new DispatcherTimer(Interval = MotionLedger.copyConfirmationHold)
                timer.Tick.Add(fun _ ->
                    if copyTimer = Some timer then
                        restoreDefaultState ())
                copyTimer <- Some timer
                timer.Start()
            Ui.onClick button doCopy
            button.DetachedFromVisualTree.Add(fun _ -> restoreDefaultState ())
            row.Children.Add button
        if not (String.IsNullOrWhiteSpace message.text) then
            addCopyButton message.text
        if MessageView.isUser message && not ctx.streaming then
            addButton Icons.pencil "编辑并分叉" "编辑并分叉" "以编辑后的消息创建分叉会话" (fun () ->
                actions.editAndFork message)
        if not (MessageView.isUser message) && ctx.isLastAssistant && not ctx.streaming then
            addButton Icons.refresh "重新生成" "重新生成" "丢弃最后一次回复并重新生成" (fun () ->
                actions.regenerate ())
        match message.commitId with
        | Some commitId when not ctx.streaming ->
            addButton Icons.trash "删除这条消息" "删除这条消息" "从会话历史中删除这条消息" (fun () ->
                actions.deleteMessage commitId)
        | _ -> ()
        row

    /// 生成失败卡片：错误分类 + 可行动建议 + 重试入口。
    let errorCard (error: GenerationError) (onRetry: unit -> unit) : Control =
        let icon = Icons.alert Tokens.danger
        icon.VerticalAlignment <- VerticalAlignment.Top
        icon.Margin <- Thickness(0.0, Tokens.iconBaselineNudge, 0.0, 0.0)
        let title =
            TextBlock(
                Text = error.message,
                FontSize = Tokens.fontBody,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text,
                TextWrapping = TextWrapping.Wrap)
        let hint =
            TextBlock(
                Text = GenerationError.hint error,
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = ReadingRhythm.helperLineHeight)
        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = 3.0)
        column.Children.Add title
        column.Children.Add hint
        let topRow = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space3)
        topRow.Children.Add icon
        topRow.Children.Add column
        match error.retryAfterSeconds with
        | Some seconds ->
            column.Children.Add(
                TextBlock(
                    Text = sprintf "建议等待约 %d 秒后重试。" seconds,
                    FontSize = Tokens.fontCaption,
                    Foreground = Tokens.warning))
        | None -> ()
        // 恢复动作紧跟说明：告诉用户「可以重试」却不给按钮，
        // 等于逼他把刚才那句话重新打一遍。
        let actionRow =
            StackPanel(
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.space2,
                Margin = Thickness(0.0, Tokens.space3, 0.0, 0.0))
        let detailContainer = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        if error.retryable then
            let retryButton = Ui.button Ui.Secondary "重试" onRetry
            retryButton.Focusable <- true
            retryButton.MinWidth <- 80.0
            retryButton.Margin <- Thickness 0.0
            retryButton.Padding <- Thickness(Tokens.space4, ControlMetrics.textButtonPaddingY)
            retryButton.Cursor <- handCursor
            Avalonia.Automation.AutomationProperties.SetName(retryButton, "重试生成")
            Avalonia.Automation.AutomationProperties.SetHelpText(retryButton, "重新尝试生成")
            ToolTip.SetTip(retryButton, "重新尝试生成")
            actionRow.Children.Add retryButton
        match error.detail with
        | Some detail ->
            let detailText = technicalText detail Tokens.fontMicro Tokens.codeMuted
            let detailHeader = DockPanel(LastChildFill = false)
            let detailTitle =
                TextBlock(
                    Text = "错误堆栈与技术细节",
                    FontSize = Tokens.fontMicro,
                    FontWeight = FontWeight.Medium,
                    Foreground = Tokens.textMuted,
                    VerticalAlignment = VerticalAlignment.Center)
            let copyDetailBtn = createCopyButton "复制错误细节" "复制错误技术细节" (fun () -> detail)
            DockPanel.SetDock(detailTitle, Dock.Left)
            DockPanel.SetDock(copyDetailBtn, Dock.Right)
            detailHeader.Children.Add detailTitle
            detailHeader.Children.Add copyDetailBtn
            let codeBox =
                Border(
                    Background = Tokens.codeBlockBackground,
                    BorderBrush = Tokens.borderSoft,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius Tokens.radiusSm,
                    Padding = Thickness(Tokens.space3, Tokens.space2),
                    Child = detailText)
            let detailStack = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space1)
            detailStack.Children.Add detailHeader
            detailStack.Children.Add codeBox
            let detailHost, setDetailVisible = detailViewport (detailStack :> Control) false
            detailHost.Margin <- Thickness(0.0, Tokens.space2, 0.0, 0.0)
            let mutable visible = false
            let mutable toggleDetail: unit -> unit = ignore
            // 与思考过程/工具调用同一套 chevron 语法：字形常驻、只转 0°/90°；
            // Ui.onClick 已含 Enter/Space 键盘激活，不再另挂 KeyDown（挂两份会触发两次）。
            let detailChevronHost =
                Border(
                    Width = Tokens.iconGlyph,
                    Height = Tokens.iconGlyph,
                    MinWidth = Tokens.iconGlyph,
                    MinHeight = Tokens.iconGlyph,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center)
            let detailRotate = RotateTransform(0.0)
            let detailChevronTransitions = Avalonia.Animation.Transitions()
            let detailRotateTransition = Avalonia.Animation.DoubleTransition()
            detailRotateTransition.Property <- RotateTransform.AngleProperty
            detailRotateTransition.Duration <- MotionPolicy.duration 150
            detailChevronTransitions.Add detailRotateTransition
            detailRotate.Transitions <- detailChevronTransitions
            let detailChevronGlyph = Icons.chevronRight Tokens.textMuted
            detailChevronGlyph.HorizontalAlignment <- HorizontalAlignment.Center
            detailChevronGlyph.VerticalAlignment <- VerticalAlignment.Center
            detailChevronGlyph.RenderTransform <- detailRotate
            detailChevronGlyph.RenderTransformOrigin <- RelativePoint.Center
            detailChevronHost.Child <- detailChevronGlyph
            let detailCaption =
                TextBlock(
                    Text = "技术细节",
                    FontSize = Tokens.fontSmall,
                    FontWeight = FontWeight.Medium,
                    Foreground = Tokens.textMuted,
                    VerticalAlignment = VerticalAlignment.Center)
            let detailHeaderRow = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, VerticalAlignment = VerticalAlignment.Center)
            detailHeaderRow.Children.Add detailChevronHost
            detailHeaderRow.Children.Add detailCaption
            let detailToggle =
                ActionBorder(
                    CornerRadius = CornerRadius Tokens.radiusSm,
                    Padding = Thickness(Tokens.space1, 3.0),
                    Margin = Thickness 0.0,
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = Thickness 1.0,
                    Cursor = handCursor,
                    Focusable = true,
                    Child = detailHeaderRow)
            let syncDetailButton () =
                detailRotate.Angle <- if visible then 90.0 else 0.0
                detailCaption.Text <- if visible then "收起技术细节" else "技术细节"
                ToolTip.SetTip(detailToggle, if visible then "收起错误技术细节" else "展开查看技术细节")
                Avalonia.Automation.AutomationProperties.SetName(detailToggle, if visible then "收起技术细节" else "技术细节")
                Avalonia.Automation.AutomationProperties.SetHelpText(detailToggle, if visible then "收起错误技术细节" else "展开查看技术细节")
                Avalonia.Automation.AutomationProperties.SetRole(detailToggle, AutomationRole.Button)
                Avalonia.Automation.AutomationProperties.SetExpanded(detailToggle, visible)
            let updateDetailVisual () =
                if detailToggle.IsFocused then
                    detailToggle.BorderBrush <- Tokens.accent
                    detailToggle.Background <- Tokens.hover
                    detailToggle.Opacity <- 1.0
                elif detailToggle.IsPointerOver then
                    detailToggle.BorderBrush <- Tokens.line
                    detailToggle.Background <- Tokens.hover
                    detailToggle.Opacity <- 0.9
                else
                    detailToggle.BorderBrush <- Brushes.Transparent
                    detailToggle.Background <- Brushes.Transparent
                    detailToggle.Opacity <- 1.0
            detailToggle.PointerEntered.Add(fun _ -> updateDetailVisual ())
            detailToggle.PointerExited.Add(fun _ -> updateDetailVisual ())
            detailToggle.GotFocus.Add(fun _ -> updateDetailVisual ())
            detailToggle.LostFocus.Add(fun _ -> updateDetailVisual ())
            Ui.onClick detailToggle (fun () -> toggleDetail ())
            toggleDetail <- fun () ->
                visible <- not visible
                setDetailVisible visible
                syncDetailButton ()
            syncDetailButton ()
            actionRow.Children.Add detailToggle
            detailContainer.Children.Add detailHost
        | None -> ()
        let cardLayout = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        cardLayout.Children.Add topRow
        if actionRow.Children.Count > 0 then cardLayout.Children.Add actionRow
        if detailContainer.Children.Count > 0 then cardLayout.Children.Add detailContainer
        Border(
            Background = Tokens.dangerSoft,
            CornerRadius = CornerRadius Tokens.radiusLg,
            Padding = Thickness(Tokens.space4, Tokens.space3),
            Margin = Thickness(0.0, Tokens.space2, 0.0, 0.0),
            MaxWidth = Tokens.readingWidth,
            Child = cardLayout)
        :> Control

    /// 消息时间的展示形式：今天只给时刻，昨天加前缀，更早给日期。
    /// 聊天里绝大多数消息是今天的，堆完整日期只是噪音。
    let private formatTimestamp (at: DateTimeOffset) : string =
        let local = at.ToLocalTime()
        let today = DateTimeOffset.Now.Date
        let days = (today - local.Date).TotalDays
        if days < 1.0 then local.ToString "HH:mm"
        elif days < 2.0 then local.ToString "'昨天' HH:mm"
        elif local.Year = DateTimeOffset.Now.Year then local.ToString "M月d日 HH:mm"
        else local.ToString "yyyy年M月d日 HH:mm"

    /// 渲染用量角标/脚注：紧凑、优雅且无杂乱感。
    /// 用标签（Tokens.textFaint）与等宽数字（Tokens.textMuted, monoFontFamily）搭配，
    /// 并支持无障碍朗读名称与悬浮完整提示。
    let private renderUsage (u: GenerationUsage) : Control option =
        let total =
            match u.totalTokens with
            | Some t -> Some t
            | None ->
                match u.promptTokens, u.completionTokens with
                | Some p, Some c -> Some(p + c)
                | _ -> None

        // 没有任何 token 或耗时信息时不渲染
        if Option.isNone total && Option.isNone u.promptTokens && Option.isNone u.completionTokens && Option.isNone u.durationMs then
            None
        else
            let tb =
                TextBlock(
                    FontSize = Tokens.fontMicro,
                    VerticalAlignment = VerticalAlignment.Center)

            let appendSep () =
                let sep = Run(" · ")
                sep.Foreground <- Tokens.textFaint
                tb.Inlines.Add sep

            let appendMetric (label: string) (count: int) =
                let lblRun = Run(label + " ")
                lblRun.Foreground <- Tokens.textFaint
                let numRun = Run(count.ToString("N0", CultureInfo.InvariantCulture))
                numRun.FontFamily <- Tokens.monoFontFamily
                numRun.Foreground <- Tokens.textMuted
                tb.Inlines.Add lblRun
                tb.Inlines.Add numRun

            let mutable hasPrev = false

            // 如果有总数且明细齐全，紧凑展示输入、输出，或优先展示总计/输入/输出
            match u.promptTokens, u.completionTokens with
            | Some p, Some c ->
                appendMetric "入" p
                appendSep ()
                appendMetric "出" c
                hasPrev <- true
                match total with
                | Some t when t <> (p + c) ->
                    appendSep ()
                    appendMetric "总" t
                | _ -> ()
            | Some p, None ->
                appendMetric "入" p
                hasPrev <- true
            | None, Some c ->
                appendMetric "出" c
                hasPrev <- true
            | None, None ->
                match total with
                | Some t ->
                    appendMetric "tokens" t
                    hasPrev <- true
                | None -> ()

            match u.durationMs with
            | Some ms ->
                if hasPrev then appendSep ()
                let durText =
                    if ms >= 1000L then
                        sprintf "%.1fs" (float ms / 1000.0)
                    else
                        sprintf "%dms" ms
                let durRun = Run(durText)
                durRun.FontFamily <- Tokens.monoFontFamily
                durRun.Foreground <- Tokens.textMuted
                tb.Inlines.Add durRun
            | None -> ()

            // 悬停交互高亮
            tb.PointerEntered.Add(fun _ ->
                for inlineItem in tb.Inlines do
                    match inlineItem with
                    | :? Run as r when r.Foreground = (Tokens.textMuted :> IBrush) ->
                        r.Foreground <- Tokens.text
                    | _ -> ())
            tb.PointerExited.Add(fun _ ->
                for inlineItem in tb.Inlines do
                    match inlineItem with
                    | :? Run as r when r.Foreground = (Tokens.text :> IBrush) ->
                        r.Foreground <- Tokens.textMuted
                    | _ -> ())

            // 屏幕阅读器与完整详情 ToolTip
            let pStr = u.promptTokens |> Option.map (fun v -> v.ToString("N0", CultureInfo.InvariantCulture)) |> Option.defaultValue "-"
            let cStr = u.completionTokens |> Option.map (fun v -> v.ToString("N0", CultureInfo.InvariantCulture)) |> Option.defaultValue "-"
            let tStr = total |> Option.map (fun v -> v.ToString("N0", CultureInfo.InvariantCulture)) |> Option.defaultValue "-"
            let a11yName = sprintf "Token 使用情况：输入 %s，输出 %s，总计 %s" pStr cStr tStr
            AutomationProperties.SetName(tb, a11yName)
            ToolTip.SetTip(tb, GenerationUsage.formatDetail u)

            Some(tb :> Control)

    /// 脚注：时间 + 可选用量。合成一行，避免两条弱字上下堆叠。
    let private footer (message: MessageView) (usage: GenerationUsage option) : Control option =
        let timeText = message.committedAt |> Option.map formatTimestamp
        let usageControl = usage |> Option.bind renderUsage
        match timeText, usageControl with
        | None, None -> None
        | Some time, None ->
            let text =
                TextBlock(
                    Text = time,
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.textFaint,
                    HorizontalAlignment =
                        (if MessageView.isUser message then HorizontalAlignment.Right else HorizontalAlignment.Left),
                    VerticalAlignment = VerticalAlignment.Center)
            Some(text :> Control)
        | None, Some uCtrl ->
            Some uCtrl
        | Some time, Some uCtrl ->
            let panel =
                StackPanel(
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.space1,
                    VerticalAlignment = VerticalAlignment.Center)
            let timeTb =
                TextBlock(Text = time, FontSize = Tokens.fontMicro, Foreground = Tokens.textFaint, VerticalAlignment = VerticalAlignment.Center)
            let sepTb =
                TextBlock(Text = " · ", FontSize = Tokens.fontMicro, Foreground = Tokens.textFaint, VerticalAlignment = VerticalAlignment.Center)
            panel.Children.Add timeTb
            panel.Children.Add sepTb
            panel.Children.Add uCtrl
            Some(panel :> Control)

    /// 渲染一条消息。返回可直接塞进消息列表的控件。
    let render (message: MessageView) (ctx: MessageContext) (actions: MessageActions) : Control =
        let renderer = MarkdownRenderer(ctx.fontSize, actions.copyText, actions.openLink, not ctx.streaming)
        let body = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)

        if not (String.IsNullOrWhiteSpace message.reasoning) then
            let duration = ctx.usage |> Option.bind (fun u -> u.durationMs)
            body.Children.Add(reasoningBlock ctx message.reasoning duration)

        for call in message.toolCalls do
            body.Children.Add(toolCallCard ctx call)

        if not (String.IsNullOrWhiteSpace message.text) then
            if MessageView.isUser message then
                body.Children.Add(
                    SelectableTextBlock(
                        Text = message.text,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = ctx.fontSize,
                        Foreground = Tokens.userBubbleText,
                        LineHeight = ReadingRhythm.proseLineHeight ctx.fontSize,
                        SelectionBrush = userBubbleSelection))
            else
                body.Children.Add(renderer.RenderText message.text)

        for attachment in message.attachments do
            body.Children.Add(attachmentRow ctx actions attachment)

        if ctx.streaming then
            let caret =
                Border(
                    Width = 7.0,
                    Height = 15.0,
                    Background = Tokens.accent,
                    CornerRadius = CornerRadius 1.5,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = Thickness(0.0, Tokens.iconBaselineNudge, 0.0, 0.0),
                    Opacity = Tokens.opacityStreamingCaret)
            body.Children.Add caret

        let bubble =
            if MessageView.isUser message then
                // 用户气泡可聚焦：键盘焦点环只用 focusRingSpread 外阴影，不加边框粗细，不抖动排版。
                let host =
                    Border(
                        Background = Tokens.userBubble,
                        CornerRadius = CornerRadius(Tokens.radiusLg, Tokens.radiusLg, Tokens.radiusSm, Tokens.radiusLg),
                        Padding = Thickness(Tokens.space4, Tokens.space3),
                        MaxWidth = LayoutPolicy.userMessageMaxWidth,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Focusable = true,
                        Child = body)
                Avalonia.Automation.AutomationProperties.SetName(host, "用户消息")
                host.GotFocus.Add(fun e ->
                    match e.NavigationMethod with
                    | NavigationMethod.Tab
                    | NavigationMethod.Directional ->
                        host.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accent.Color))
                    | _ -> ())
                host.LostFocus.Add(fun _ -> host.BoxShadow <- BoxShadows())
                host
            else
                Border(
                    Background = Brushes.Transparent,
                    Padding = Thickness 0.0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = body)

        let avatar =
            if MessageView.isUser message then
                let host =
                    Border(
                        Width = Tokens.logoAvatar,
                        Height = Tokens.logoAvatar,
                        CornerRadius = CornerRadius Tokens.radiusPill,
                        Background = Tokens.accentSoft,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = Thickness(0.0, Tokens.iconBaselineNudge, 0.0, 0.0))
                host.Child <-
                    TextBlock(
                        Text = "你",
                        FontSize = Tokens.fontMicro,
                        FontWeight = FontWeight.Medium,
                        Foreground = Tokens.accent,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center)
                host :> Control
            else
                let host = ctx.brandAvatar ()
                host.VerticalAlignment <- VerticalAlignment.Top
                host.Margin <- Thickness(0.0, Tokens.iconBaselineNudge, 0.0, 0.0)
                host

        let row: Control =
            if MessageView.isUser message then
                let stack = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space3)
                stack.HorizontalAlignment <- HorizontalAlignment.Right
                stack.Children.Add bubble
                stack.Children.Add avatar
                stack :> Control
            else
                // 助手正文必须铺满阅读列：横向 StackPanel 只会给子元素「期望宽度」，
                // 于是代码块和表格被压成窄条。用 DockPanel 让头像靠左、气泡填满剩余空间。
                let dock = DockPanel(LastChildFill = true, HorizontalAlignment = HorizontalAlignment.Stretch)
                avatar.Margin <- Thickness(0.0, Tokens.iconBaselineNudge, Tokens.space3, 0.0)
                DockPanel.SetDock(avatar, Dock.Left)
                dock.Children.Add avatar
                bubble.Width <- Double.NaN
                bubble.HorizontalAlignment <- HorizontalAlignment.Stretch
                dock.Children.Add bubble
                dock :> Control

        // 脚注行：时间 + 操作按钮，随文档流排在消息下方。
        // 头像占了 26pt 加 12pt 间距，脚注缩进同样的量才能与正文左缘对齐。
        let gutter = Tokens.logoAvatar + Tokens.space3
        let buttons = actionButtons message ctx actions
        let metaText =
            if ctx.streaming then None
            else footer message (if ctx.isLastAssistant then ctx.usage else None)
        let metaRow =
            let line =
                StackPanel(
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.space2,
                    VerticalAlignment = VerticalAlignment.Center)
            if MessageView.isUser message then
                line.HorizontalAlignment <- HorizontalAlignment.Right
                line.Margin <- Thickness(0.0, Tokens.space2, gutter, 0.0)
                match metaText with
                | Some text -> line.Children.Add text
                | None -> ()
                line.Children.Add buttons
            else
                line.HorizontalAlignment <- HorizontalAlignment.Left
                line.Margin <- Thickness(gutter, Tokens.space2, 0.0, 0.0)
                line.Children.Add buttons
                match metaText with
                | Some text -> line.Children.Add text
                | None -> ()
            line

        let host = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0, HorizontalAlignment = HorizontalAlignment.Stretch)
        host.Children.Add row
        host.Children.Add metaRow
        let syncVisual () =
            if host.IsPointerOver || buttons.IsKeyboardFocusWithin then
                buttons.Opacity <- 1.0
            else
                buttons.Opacity <- idleActionOpacity
        let highlight () =
            buttons.Opacity <- 1.0
        let dim () =
            Dispatcher.UIThread.Post(fun () -> syncVisual ())
        host.PointerEntered.Add(fun _ -> highlight ())
        host.PointerExited.Add(fun _ -> dim ())
        host.GotFocus.Add(fun _ -> syncVisual ())
        host.LostFocus.Add(fun _ -> dim ())
        buttons.GotFocus.Add(fun _ -> highlight ())
        buttons.LostFocus.Add(fun _ -> dim ())
        buttons.PointerEntered.Add(fun _ -> highlight ())
        buttons.PointerExited.Add(fun _ -> dim ())
        host :> Control
