namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
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

    let private formatDuration (ms: int64) =
        if ms >= 1000L then sprintf "%.1f 秒" (float ms / 1000.0) else sprintf "%d 毫秒" (int ms)

    /// 思考过程：左缘竖线 + 可折叠。流式期间默认展开，让人看到模型在动。
    let private reasoningBlock (ctx: MessageContext) (reasoning: string) (durationMs: int64 option) : Control =
        let collapsed = not ctx.streaming && ctx.autoCollapseReasoning
        let body =
            SelectableTextBlock(
                Text = reasoning,
                TextWrapping = TextWrapping.Wrap,
                FontSize = ctx.fontSize - 1.5,
                Foreground = Tokens.textMuted,
                LineHeight = (ctx.fontSize - 1.5) * 1.6,
                IsVisible = not collapsed,
                Margin = Thickness(0.0, Tokens.space2, 0.0, 0.0),
                SelectionBrush = Tokens.accentSoft)
        let chevron = Icons.chevronRight Tokens.textFaint
        let chevronDown = Icons.chevronDown Tokens.textFaint
        let chevronHost = Border(Child = (if collapsed then chevron else chevronDown), VerticalAlignment = VerticalAlignment.Center)
        let durationText =
            match durationMs with
            | Some ms -> sprintf " · %s" (formatDuration ms)
            | None -> ""
        let caption =
            TextBlock(
                Text = (if ctx.streaming then "正在思考" + durationText else "思考过程" + durationText),
                FontSize = ctx.fontSize - 2.0,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.textMuted,
                VerticalAlignment = VerticalAlignment.Center)
        let header =
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space1, Cursor = handCursor)
            row.Children.Add chevronHost
            row.Children.Add caption
            row.Focusable <- true
            Avalonia.Automation.AutomationProperties.SetName(row, "切换思考过程")
            row
        let toggle () =
            body.IsVisible <- not body.IsVisible
            chevronHost.Child <- if body.IsVisible then Icons.chevronDown Tokens.textFaint else Icons.chevronRight Tokens.textFaint
        header.PointerReleased.Add(fun e ->
            e.Handled <- true
            toggle ())
        header.KeyDown.Add(fun e ->
            if e.Key = Key.Enter || e.Key = Key.Space then
                e.Handled <- true
                toggle ())
        let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        stack.Children.Add header
        stack.Children.Add body
        Border(
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
    let private toolCallCard (ctx: MessageContext) (call: ToolCallView) : Control =
        let running = call.result.IsNone
        let statusBrush: IBrush = if running then Tokens.accent else Tokens.success
        let icon: Control = if running then Ui.spinner 14.0 else Icons.wrench statusBrush
        icon.VerticalAlignment <- VerticalAlignment.Center
        let name =
            TextBlock(
                Text = call.name,
                FontSize = Tokens.fontSmall,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text,
                VerticalAlignment = VerticalAlignment.Center)
        let state =
            Border(
                Background = (if running then Tokens.accentFaint :> IBrush else Tokens.surface :> IBrush),
                CornerRadius = CornerRadius Tokens.radiusPill,
                Padding = Thickness(Tokens.space2, 1.0),
                VerticalAlignment = VerticalAlignment.Center,
                Child =
                    TextBlock(
                        Text = (if running then "执行中" else "已完成"),
                        FontSize = Tokens.fontMicro,
                        Foreground = statusBrush,
                        VerticalAlignment = VerticalAlignment.Center))
        /// 未展开时也给一行参数摘要：多数时候用户只想确认「传了什么」。
        let summaryText =
            let compact (raw: string) =
                if String.IsNullOrWhiteSpace raw then ""
                else
                    let single = raw.Replace('\n', ' ').Replace('\r', ' ').Trim()
                    if single.Length > 96 then single.Substring(0, 96) + "…" else single
            compact call.argumentsJson
        let summary =
            TextBlock(
                Text = summaryText,
                FontFamily = Tokens.monoFontFamily,
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textFaint,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsVisible = not (String.IsNullOrWhiteSpace summaryText),
                Margin = Thickness(0.0, 3.0, 0.0, 0.0))
        let detail =
            let text =
                [ if not (String.IsNullOrWhiteSpace call.argumentsJson) then "参数\n" + call.argumentsJson
                  match call.result with
                  | Some result when not (String.IsNullOrWhiteSpace result) -> "结果\n" + result
                  | _ -> () ]
                |> String.concat "\n\n"
            SelectableTextBlock(
                Text = text,
                FontFamily = Tokens.monoFontFamily,
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17.0,
                IsVisible = false,
                Margin = Thickness(0.0, Tokens.space2, 0.0, 0.0),
                SelectionBrush = Tokens.accentSoft)
        let chevronHost = Border(Child = Icons.chevronDown Tokens.textFaint, VerticalAlignment = VerticalAlignment.Center)
        let headerRow = DockPanel(LastChildFill = false)
        let left = Ui.hstack Tokens.space2 [ icon; name :> Control; state :> Control ]
        DockPanel.SetDock(left, Dock.Left)
        DockPanel.SetDock(chevronHost, Dock.Right)
        headerRow.Children.Add left
        headerRow.Children.Add chevronHost
        let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        stack.Children.Add headerRow
        stack.Children.Add summary
        stack.Children.Add detail
        let host =
            Border(
                Background = Tokens.surface,
                BorderBrush = Tokens.border,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusMd,
                Padding = Thickness(Tokens.space3, Tokens.space3),
                Margin = Thickness(0.0, 0.0, 0.0, Tokens.space3),
                Cursor = handCursor,
                Focusable = true,
                Child = stack)
        let toggle () =
            detail.IsVisible <- not detail.IsVisible
            summary.IsVisible <- not detail.IsVisible && not (String.IsNullOrWhiteSpace summaryText)
            chevronHost.Child <-
                if detail.IsVisible then Icons.chevronUp Tokens.textFaint else Icons.chevronDown Tokens.textFaint
        host.PointerReleased.Add(fun e ->
            e.Handled <- true
            toggle ())
        host.KeyDown.Add(fun e ->
            if e.Key = Key.Enter || e.Key = Key.Space then
                e.Handled <- true
                toggle ())
        ToolTip.SetTip(host, "点击展开参数与结果")
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
                MaxWidth = 260.0,
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
        let host =
            Border(
                Background = Tokens.surface,
                BorderBrush = Tokens.border,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusMd,
                Padding = Thickness(Tokens.space3, 6.0),
                Margin = Thickness(0.0, Tokens.space1, 0.0, 0.0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = (if missing then null else handCursor),
                Child = row)
        if not missing then
            ToolTip.SetTip(host, "下载附件")
            host.PointerEntered.Add(fun _ -> host.Background <- Tokens.surfaceRaised)
            host.PointerExited.Add(fun _ -> host.Background <- Tokens.surface)
            host.PointerReleased.Add(fun e ->
                e.Handled <- true
                actions.downloadAttachment attachment.sha256)
        host :> Control

    /// 悬停操作条。绝对不占文档流，否则每次悬停都会推动整段排版。
    /// 操作按钮常驻低透明度。
    ///
    /// 只在 hover 时出现等于在触屏上不存在、在键盘上摸不到；
    /// 常驻一档淡显既可发现，又不至于跟正文抢注意力。
    /// 不能再低：更淡的图标在浅纸背景上已经读不出形状。
    let idleActionOpacity = 0.62

    /// 消息操作按钮。放在脚注行里随文档流排布——
    /// 早先做成浮在消息上方的悬浮条，常驻显示后就会压住头像和气泡边角。
    let private actionButtons (message: MessageView) (ctx: MessageContext) (actions: MessageActions) : StackPanel =
        let row =
            StackPanel(
                Orientation = Orientation.Horizontal,
                Spacing = 0.0,
                Opacity = idleActionOpacity,
                VerticalAlignment = VerticalAlignment.Center)
        let transitions = Avalonia.Animation.Transitions()
        transitions.Add(Avalonia.Animation.DoubleTransition(Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds 150.0))
        row.Transitions <- transitions
        let addButton (icon: IBrush -> Control) (tip: string) (action: unit -> unit) =
            let button = Ui.iconButton icon tip
            button.Width <- 24.0
            button.Height <- 24.0
            button.MinWidth <- 24.0
            button.MinHeight <- 24.0
            Ui.onClick button action
            row.Children.Add button
        let addCopyButton (text: string) =
            let button = Ui.iconButton Icons.copy "复制"
            button.Width <- 24.0
            button.Height <- 24.0
            button.MinWidth <- 24.0
            button.MinHeight <- 24.0
            let doCopy () =
                actions.copyText text
                Ui.setIcon button Icons.check Tokens.success
                ToolTip.SetTip(button, "已复制！")
                let timer = new DispatcherTimer(Interval = TimeSpan.FromMilliseconds 1500.0)
                timer.Tick.Add(fun _ ->
                    timer.Stop()
                    Ui.setIcon button Icons.copy Tokens.textMuted
                    ToolTip.SetTip(button, "复制"))
                timer.Start()
            Ui.onClick button doCopy
            row.Children.Add button
        if not (String.IsNullOrWhiteSpace message.text) then
            addCopyButton message.text
        if MessageView.isUser message && not ctx.streaming then
            addButton Icons.pencil "编辑并分叉" (fun () -> actions.editAndFork message)
        if not (MessageView.isUser message) && ctx.isLastAssistant && not ctx.streaming then
            addButton Icons.refresh "重新生成" actions.regenerate
        match message.commitId with
        | Some commitId when not ctx.streaming ->
            addButton Icons.trash "删除这条消息" (fun () -> actions.deleteMessage commitId)
        | _ -> ()
        row

    /// 生成失败卡片：错误分类 + 可行动建议 + 重试入口。
    let errorCard (error: GenerationError) (onRetry: unit -> unit) : Control =
        let icon = Icons.alert Tokens.danger
        icon.VerticalAlignment <- VerticalAlignment.Top
        icon.Margin <- Thickness(0.0, 2.0, 0.0, 0.0)
        let title =
            TextBlock(
                Text = error.message,
                FontSize = Tokens.fontBody,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text,
                TextWrapping = TextWrapping.Wrap)
        let hint =
            TextBlock(
                Text = GenerationErrorKind.hint error.kind,
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18.0)
        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = 3.0)
        column.Children.Add title
        column.Children.Add hint
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
        if error.retryable then
            actionRow.Children.Add(Ui.button Ui.Secondary "重试" onRetry)
        match error.detail with
        | Some detail ->
            let detailText =
                SelectableTextBlock(
                    Text = detail,
                    FontFamily = Tokens.monoFontFamily,
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.textFaint,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = false,
                    Margin = Thickness(0.0, Tokens.space2, 0.0, 0.0),
                    SelectionBrush = Tokens.accentSoft)
            actionRow.Children.Add(Ui.button Ui.Ghost "技术细节" (fun () -> detailText.IsVisible <- not detailText.IsVisible))
            if actionRow.Children.Count > 0 then column.Children.Add actionRow
            column.Children.Add detailText
        | None -> if actionRow.Children.Count > 0 then column.Children.Add actionRow
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space3)
        row.Children.Add icon
        row.Children.Add column
        Border(
            Background = Tokens.dangerSoft,
            CornerRadius = CornerRadius Tokens.radiusLg,
            Padding = Thickness(Tokens.space4, Tokens.space3),
            Margin = Thickness(0.0, Tokens.space2, 0.0, 0.0),
            MaxWidth = Tokens.readingWidth,
            Child = row)
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

    /// 脚注：时间 + 可选用量。合成一行，避免两条弱字上下堆叠。
    let private footer (message: MessageView) (usage: GenerationUsage option) : Control option =
        let parts =
            [ match message.committedAt with
              | Some at -> formatTimestamp at
              | None -> ()
              match usage |> Option.bind GenerationUsage.formatSummary with
              | Some summary -> summary
              | None -> () ]
        if List.isEmpty parts then None
        else
            let text =
                TextBlock(
                    Text = String.Join(" · ", parts),
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.textMuted,
                    HorizontalAlignment =
                        (if MessageView.isUser message then HorizontalAlignment.Right else HorizontalAlignment.Left),
                    VerticalAlignment = VerticalAlignment.Center)
            match usage with
            | Some value -> ToolTip.SetTip(text, GenerationUsage.formatDetail value)
            | None -> ()
            Some(text :> Control)

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
                        LineHeight = ctx.fontSize * 1.6,
                        SelectionBrush = Tokens.accentHover))
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
                    Margin = Thickness(0.0, 2.0, 0.0, 0.0),
                    Opacity = 0.85)
            body.Children.Add caret

        let bubble =
            if MessageView.isUser message then
                Border(
                    Background = Tokens.userBubble,
                    CornerRadius = CornerRadius(Tokens.radiusLg, Tokens.radiusLg, Tokens.radiusSm, Tokens.radiusLg),
                    Padding = Thickness(Tokens.space4, Tokens.space3),
                    MaxWidth = 560.0,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = body)
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
                        Margin = Thickness(0.0, 2.0, 0.0, 0.0))
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
                host.Margin <- Thickness(0.0, 2.0, 0.0, 0.0)
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
                avatar.Margin <- Thickness(0.0, 2.0, Tokens.space3, 0.0)
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
                line.Margin <- Thickness(0.0, 2.0, gutter - 5.0, 0.0)
                match metaText with
                | Some text -> line.Children.Add text
                | None -> ()
                line.Children.Add buttons
            else
                line.HorizontalAlignment <- HorizontalAlignment.Left
                line.Margin <- Thickness(gutter, 2.0, 0.0, 0.0)
                line.Children.Add buttons
                match metaText with
                | Some text -> line.Children.Add text
                | None -> ()
            line

        let host = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0, HorizontalAlignment = HorizontalAlignment.Stretch)
        host.Children.Add row
        host.Children.Add metaRow
        let highlight () = buttons.Opacity <- 1.0
        let dim () = buttons.Opacity <- idleActionOpacity
        host.PointerEntered.Add(fun _ -> highlight ())
        host.PointerExited.Add(fun _ -> dim ())
        // 键盘走查也要能把按钮点亮，否则 Tab 到它上面时还是半透明的
        buttons.GotFocus.Add(fun _ -> highlight ())
        buttons.LostFocus.Add(fun _ -> dim ())
        host :> Control
