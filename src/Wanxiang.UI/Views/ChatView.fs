namespace Wanxiang.UI

open System
open System.Collections.Generic
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Wanxiang.Core

/// 聊天区对外暴露的动作。
type ChatActions = {
    renameTitle: string -> unit
    openSessionSettings: unit -> unit
    forkFromHere: unit -> unit
    stopGeneration: unit -> unit
    requestOlderHistory: unit -> unit
    retryLast: unit -> unit
    toggleSidebar: unit -> unit
    message: MessageActions
}

/// 空态在不同前提下要说不同的话，否则用户不知道下一步做什么。
type ChatEmptyState =
    /// 未连接
    | NotConnected
    /// 已连接但没有可用服务商
    | NoProvider
    /// 有服务商但没选会话
    | NoConversation
    /// 会话是空的
    | EmptyConversation

/// 一张消息卡的身份。
///
/// 已提交的消息在事件溯源模型下内容不可变，所以 `commit` 加上几个真会变的量
/// （工具结果条数、影响外观的上下文开关）就足以判断「这张卡还是原来那张」。
/// 不做内容哈希：那要扫全文，而这里每帧都要算。
type private CardKey =
    { commit: uint64
      role: string
      toolCalls: int
      toolResults: int
      attachments: int
      fontSize: float
      collapse: bool
      lastAssistant: bool
      /// 用量只在末条助手消息上显示，其余卡片与它无关
      usageTag: int
      /// 附件丢失标记只影响带附件的卡片
      missingTag: int }

/// 聊天主区：顶栏（标题 / 模型 / 操作）+ 消息列表。
type ChatView(actions: ChatActions, brandLogo: float -> Control) =
    inherit Border()

    let titleText =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontTitle,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis)
    let titleEditShell, titleEditBox = Ui.textField "会话标题"
    let titleHost = Grid()
    let titleAction =
        ActionBorder(
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusSm,
            Height = Tokens.iconButton,
            MinHeight = Tokens.iconButton,
            MaxHeight = Tokens.iconButton,
            MinWidth = 120.0,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = Thickness(Tokens.space3, 0.0),
            Focusable = false,
            Child = titleText)

    let generatingChip =
        Border(
            Background = Tokens.accentFaint,
            CornerRadius = CornerRadius Tokens.radiusPill,
            Padding = Thickness(Tokens.space3, 3.0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = true,
            Opacity = 0.0,
            IsHitTestVisible = false)
    let generatingCaption =
        TextBlock(
            Text = "生成中",
            FontSize = Tokens.fontMicro,
            Foreground = Tokens.accent,
            VerticalAlignment = VerticalAlignment.Center)

    let stopButton = Ui.iconButton Icons.stop "停止生成"
    let forkButton = Ui.iconButton Icons.fork "从当前分叉"
    let sessionSettingsButton = Ui.iconButton Icons.sliders "会话设置"
    let sidebarToggleButton = Ui.iconButton Icons.panelLeft "切换侧边栏（Ctrl+B / ⌘B）"

    let messagePanel =
        StackPanel(
            Orientation = Orientation.Vertical,
            Spacing = ContentMetrics.messageGap,
            // 滚到底时末条消息与输入区之间的呼吸量：
            // 由 messagePanel 的下边距统一定义（见 ScrollLayoutTests）。
            // 实测可见量比设定值小约 15pt，48 对应约 33pt 的净留白。
            Margin = Thickness(0.0, 0.0, 0.0, ContentMetrics.messageEndBreathing),
            MaxWidth = Tokens.readingWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch)
    let scroller =
        ScrollViewer(
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // 末条消息与输入区之间的留白统一由 messagePanel.Margin 承担，底部 Padding 设为 0 避免叠加
            Padding = Thickness(Tokens.shellInset, Tokens.space5, Tokens.shellInset, 0.0))
    let emptyPanel =
        StackPanel(
            Orientation = Orientation.Vertical,
            Spacing = 0.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // reading column 内水平居中，留出下方呼吸留白形成微妙向上视差偏置 (optical bias)
            Margin = Thickness(Tokens.space4, 0.0, Tokens.space4, Tokens.space8),
            MaxWidth = ContentMetrics.emptyStateMaxWidth,
            IsVisible = false)
    let emptyTitle =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontHeading,
            FontWeight = FontWeight.SemiBold,
            Foreground = Tokens.text,
            TextAlignment = TextAlignment.Center,
            LineHeight = ReadingRhythm.headingLineHeight,
            LetterSpacing = 0.4,
            Margin = Thickness(0.0, Tokens.space4, 0.0, 0.0))
    let emptyHint =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontBody,
            Foreground = Tokens.textMuted,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = ReadingRhythm.emptyStateLineHeight,
            MaxWidth = ContentMetrics.emptyStateMaxWidth,
            Margin = Thickness(0.0, Tokens.space3, 0.0, 0.0))
    let mutable currentPrimaryAction: unit -> unit = ignore
    let emptyActionButton =
        let btn = Ui.button Ui.Primary "" (fun () -> currentPrimaryAction ())
        btn.HorizontalAlignment <- HorizontalAlignment.Center
        btn.VerticalAlignment <- VerticalAlignment.Center
        btn.MinWidth <- 136.0
        btn.Height <- 38.0
        btn.CornerRadius <- CornerRadius Tokens.radiusMd
        btn.Padding <- Thickness(Tokens.space4, 0.0)
        btn.Focusable <- true
        btn.IsVisible <- false
        btn
    let emptyActions =
        let panel =
            StackPanel(
                Orientation = Orientation.Vertical,
                Spacing = Tokens.space2,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = Thickness(0.0, Tokens.space4, 0.0, 0.0),
                IsVisible = false)
        panel.Children.Add emptyActionButton
        panel
    let mutable lastEmptyState: (ChatEmptyState * string option) option = None
    let skeletonPanel =
        StackPanel(
            Orientation = Orientation.Vertical,
            Spacing = ContentMetrics.messageGap,
            Margin = Thickness(0.0, 0.0, 0.0, ContentMetrics.messageEndBreathing),
            MaxWidth = Tokens.readingWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = false)
    let mutable skeletonTimer: DispatcherTimer option = None
    let mutable skeletonOpacityPhase = 0.0
    let mutable motionSubscription: IDisposable option = None
    let mutable attached = false

    /// 已渲染的卡片，按身份缓存。
    ///
    /// 不加这层缓存的话，流式期间每批 delta 都要把整表拆掉重建：
    /// 200 条消息实测约 100ms 一次，越聊越卡，而其中只有最后一张卡真的变了。
    let cardCache = Dictionary<CardKey, Control>()
    /// 当前挂在面板上的卡片身份，用来算最长公共前缀
    let mutable mountedKeys: CardKey option array = [||]

    let scrollToBottomButton = Ui.iconButtonAccent Icons.arrowDown "回到最新"
    let scrollToBottomLabel =
        TextBlock(
            Text = "回到最新",
            FontSize = Tokens.fontSmall,
            Foreground = Tokens.textOnAccent,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center)
    let scrollToBottomIcon = Icons.arrowDown Tokens.textOnAccent
    let scrollToBottomContent =
        let panel = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, VerticalAlignment = VerticalAlignment.Center)
        panel.Children.Add scrollToBottomIcon
        panel.Children.Add scrollToBottomLabel
        panel
    let mutable unreadSinceScrolledUp: int = 0
    let mutable previousRenderedMessageCount: int = 0
    let mutable smoothScrollTimer: DispatcherTimer option = None

    let mutable atBottom = true
    let mutable headerBar: Border = Unchecked.defaultof<Border>
    let mutable pendingHistoryAnchor: (float * float) option = None
    let mutable compactMode = false
    let mutable hasConversationChrome = false
    let mutable isGenerating = false
    let mutable titleEditable = false
    let mutable editingTitle = false

    do
        generatingChip.Child <-
            // Streaming 文本本身已经持续变化；Header 只需要一个静态状态点，
            // 不再额外跑第二个 spinner 与正文竞争注意力 / UI thread。
            let stateDot =
                Border(
                    Width = 5.0,
                    Height = 5.0,
                    CornerRadius = CornerRadius Tokens.radiusPill,
                    Background = Tokens.accent,
                    VerticalAlignment = VerticalAlignment.Center)
            let row = Ui.hstack Tokens.space2 [ stateDot :> Control; generatingCaption :> Control ]
            row
        titleEditShell.Height <- Tokens.iconButton
        titleEditShell.MinHeight <- Tokens.iconButton
        titleEditShell.MaxHeight <- Tokens.iconButton
        titleEditShell.MinWidth <- 120.0
        titleEditShell.Padding <- Thickness(Tokens.space3, 0.0)
        titleEditShell.CornerRadius <- CornerRadius Tokens.radiusSm
        titleEditShell.Background <- Tokens.surface
        titleEditShell.BorderBrush <- Tokens.border
        titleEditShell.BorderThickness <- Thickness 1.0
        titleEditShell.VerticalAlignment <- VerticalAlignment.Center
        titleEditBox.FontSize <- Tokens.fontTitle
        titleEditBox.FontWeight <- FontWeight.Medium
        titleEditBox.VerticalAlignment <- VerticalAlignment.Center
        titleEditBox.VerticalContentAlignment <- VerticalAlignment.Center
        titleEditBox.Padding <- Thickness 0.0
        titleEditBox.MinHeight <- 0.0
        titleEditBox.Margin <- Thickness 0.0
        ToolTip.SetTip(generatingChip, "正在生成回答")
        Avalonia.Automation.AutomationProperties.SetName(generatingChip, "正在生成回答")
        titleHost.Children.Add titleAction
        titleHost.Children.Add titleEditShell
        titleEditShell.IsVisible <- false

    let updateTitleActionVisual () =
        if not titleEditable then
            titleAction.Background <- Brushes.Transparent
            titleAction.BorderBrush <- Brushes.Transparent
            titleAction.BoxShadow <- BoxShadows()
            titleText.Foreground <- Tokens.textFaint
        elif titleAction.IsFocused then
            titleAction.Background <- Brushes.Transparent
            titleAction.BorderBrush <- Tokens.accent
            titleAction.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accent.Color))
            titleText.Foreground <- Tokens.accent
        elif titleAction.IsPointerOver then
            // 悬停只换底色与描边：边框粗细与阴影不变，不向外扩张。
            titleAction.Background <- Tokens.hover
            titleAction.BorderBrush <- Tokens.borderSoft
            titleAction.BoxShadow <- BoxShadows()
            titleText.Foreground <- Tokens.text
        else
            titleAction.Background <- Brushes.Transparent
            titleAction.BorderBrush <- Brushes.Transparent
            titleAction.BoxShadow <- BoxShadows()
            titleText.Foreground <- Tokens.text

    // 内框几何与 titleAction 对齐：外壳定高、内框填满内高并横向拉伸，
    // 使进出编辑模式时顶栏高度与右侧按钮位置纹丝不动。
    let ensureTitleEditGeometry () =
        titleHost.Height <- Tokens.iconButton
        titleHost.MinHeight <- Tokens.iconButton
        titleHost.MaxHeight <- Tokens.iconButton
        titleHost.Margin <- Thickness 0.0
        titleAction.Margin <- Thickness 0.0
        titleEditShell.Margin <- Thickness 0.0
        titleEditBox.HorizontalAlignment <- HorizontalAlignment.Stretch
        titleEditBox.Height <- Tokens.iconButton - 2.0
        titleEditBox.MaxHeight <- Tokens.iconButton - 2.0

    // 标题铬层（ToolTip / 自动化名称 / 悬停视觉）统一入口，
    // 提交与取消都要经过它，避免可见文本与无障碍名称脱节。
    let syncTitleChrome (text: string) =
        ToolTip.SetTip(titleAction, text)
        ToolTip.SetTip(titleText, text)
        Avalonia.Automation.AutomationProperties.SetName(titleText, text)
        Avalonia.Automation.AutomationProperties.SetName(
            titleAction,
            if titleEditable then sprintf "重命名会话：%s" text else text)
        updateTitleActionVisual ()

    // 把方向键与键盘焦点都送回标题按钮：先同步尝试一次，
    // 再在 Input 优先级补一次，应对布局刚切换完还接不住焦点的那一帧。
    let restoreTitleActionFocus () =
        if titleAction.IsVisible && titleAction.Focusable then
            titleAction.Focus(NavigationMethod.Directional) |> ignore
            Dispatcher.UIThread.Post(
                (fun () ->
                    if titleAction.IsVisible && titleAction.Focusable && not titleAction.IsFocused then
                        titleAction.Focus(NavigationMethod.Directional) |> ignore),
                DispatcherPriority.Input)

    do ensureTitleEditGeometry ()

    member private this.CommitTitle(restoreFocus: bool) =
        if editingTitle || titleEditShell.IsVisible then
            editingTitle <- false
            let next = if isNull titleEditBox.Text then "" else titleEditBox.Text.Trim()
            titleAction.IsVisible <- true
            titleEditShell.IsVisible <- false
            let effective =
                if not (String.IsNullOrWhiteSpace next) && next <> titleText.Text then
                    titleText.Text <- next
                    titleEditBox.Text <- next
                    actions.renameTitle next
                    next
                else
                    titleEditBox.Text <- titleText.Text
                    titleText.Text
            syncTitleChrome effective
            if restoreFocus then
                restoreTitleActionFocus ()

    member private this.CancelTitleEdit() =
        if editingTitle || titleEditShell.IsVisible then
            editingTitle <- false
            titleEditBox.Text <- titleText.Text
            titleAction.IsVisible <- true
            titleEditShell.IsVisible <- false
            syncTitleChrome titleText.Text
            restoreTitleActionFocus ()

    member private this.BeginTitleEdit() =
        if titleEditable && titleAction.Focusable then
            // 进编辑模式前先对齐几何：与 titleAction 同高同宽，右侧按钮不跳。
            ensureTitleEditGeometry ()
            editingTitle <- true
            titleEditBox.Text <- titleText.Text
            titleAction.IsVisible <- false
            titleEditShell.IsVisible <- true
            titleEditBox.Focus() |> ignore
            titleEditBox.SelectAll()
            Dispatcher.UIThread.Post(
                (fun () ->
                    if titleEditShell.IsVisible then
                        titleEditBox.Focus() |> ignore
                        titleEditBox.SelectAll()),
                DispatcherPriority.Input)

    member this.SetTitle(text: string, editable: bool) =
        titleEditable <- editable
        // 服务端标题在用户改名途中到达（首答自动标题）时，不能丢弃输入框里的草稿：
        // 隐藏标签跟进最新值（取消会露出它），提交仍以草稿为准。
        let editing = editable && editingTitle && titleEditShell.IsVisible
        editingTitle <- editing
        titleEditShell.IsVisible <- editing
        titleAction.IsVisible <- not editing
        titleText.TextTrimming <- TextTrimming.CharacterEllipsis
        titleText.Text <- text
        if not editing then
            titleEditBox.Text <- text
        titleAction.Focusable <- editable
        titleAction.Cursor <- if editable then new Cursor(StandardCursorType.Hand) else null
        syncTitleChrome text

    member this.SetGenerating(generating: bool, statusText: string) =
        isGenerating <- generating
        Ui.setReservedActionVisible generatingChip (generating && not compactMode)
        generatingCaption.Text <- if String.IsNullOrWhiteSpace statusText then "生成中" else statusText
        stopButton.IsVisible <- generating

    member _.SetCanStop(value: bool) = Ui.setEnabled stopButton value

    /// 会话状态变化时同步顶栏功能按钮的可用性。
    member this.SetConversationChrome(hasConversation: bool) =
        hasConversationChrome <- hasConversation
        forkButton.IsVisible <- hasConversation && not compactMode
        sessionSettingsButton.IsVisible <- hasConversation
        if not (isNull (box headerBar)) then headerBar.IsVisible <- true

    member this.SetCompactMode(value: bool) =
        compactMode <- value
        let size = if value then LayoutPolicy.compactActionTarget else Tokens.iconButton
        for button in [ sidebarToggleButton; stopButton; forkButton; sessionSettingsButton; scrollToBottomButton ] do
            Ui.setSquareTarget button size
        stopButton.IsVisible <- isGenerating
        sessionSettingsButton.IsVisible <- hasConversationChrome
        this.UpdateScrollToBottomAppearance()
        Ui.setReservedActionVisible generatingChip (isGenerating && not value)
        forkButton.IsVisible <- hasConversationChrome && not value

    member this.ShowEmpty(state: ChatEmptyState, onPrimary: (string * (unit -> unit)) option) =
        match onPrimary with
        | Some(_, action) -> currentPrimaryAction <- action
        | None -> currentPrimaryAction <- ignore
        let primaryLabel = onPrimary |> Option.map fst
        let currentStateKey = (state, primaryLabel)
        if lastEmptyState <> Some currentStateKey then
            lastEmptyState <- Some currentStateKey
            let title, hint =
                match state with
                | NotConnected -> "先连接一台万象服务器", "服务端负责运行模型与保存会话；客户端只是它的一个视图。"
                | NoProvider -> "还没有可用的模型", "添加一个服务商并填入密钥，就可以开始对话了。"
                | NoConversation -> "开始一次新对话", "左侧新建会话，或从历史里挑一个继续。"
                | EmptyConversation -> "说点什么吧", "这个会话还是空的。你的第一句话会决定它的标题。"
            emptyTitle.Text <- title
            emptyHint.Text <- hint
            match onPrimary with
            | Some(label, _) ->
                Ui.setButtonText emptyActionButton label
                ToolTip.SetTip(emptyActionButton, label)
                Avalonia.Automation.AutomationProperties.SetName(emptyActionButton, label)
                Avalonia.Automation.AutomationProperties.SetHelpText(emptyActionButton, label)
                emptyActionButton.IsVisible <- true
                emptyActions.IsVisible <- true
            | None ->
                emptyActionButton.IsVisible <- false
                emptyActions.IsVisible <- false
        emptyPanel.IsVisible <- true
        scroller.IsVisible <- false
        this.HideSkeletonLoading()
        messagePanel.IsVisible <- false

    member this.HideEmpty() =
        lastEmptyState <- None
        currentPrimaryAction <- ignore
        emptyActionButton.IsVisible <- false
        emptyActions.IsVisible <- false
        this.ResetScrollState()
        emptyPanel.IsVisible <- false
        scroller.IsVisible <- true
        messagePanel.IsVisible <- true

    /// 重置滚动到底状态与未读计数（换会话、加载骨架或切换视图时调用）。
    member this.ResetScrollState() =
        smoothScrollTimer |> Option.iter (fun t -> t.Stop())
        smoothScrollTimer <- None
        atBottom <- true
        unreadSinceScrolledUp <- 0
        previousRenderedMessageCount <- 0
        this.UpdateScrollToBottomAppearance()
    /// 停止骨架呼吸动画。
    member private this.StopSkeletonBreathing() =
        skeletonTimer |> Option.iter (fun t -> t.Stop())
        skeletonTimer <- None
        skeletonPanel.Opacity <- if skeletonPanel.IsVisible && MotionPolicy.isReduced () then 0.85 else 1.0

    /// 启动骨架呼吸动画（低频平滑透明度呼吸）。
    member private this.StartSkeletonBreathing() =
        skeletonTimer |> Option.iter (fun t -> t.Stop())
        skeletonTimer <- None
        if MotionPolicy.isReduced () then
            skeletonPanel.Opacity <- 0.85
        elif not attached then
            // 尚未挂载到视觉树时不启动计时器；挂载时若骨架仍可见会触发
            skeletonPanel.Opacity <- 0.85
        else
            skeletonOpacityPhase <- 0.0
            let timer = new DispatcherTimer(Interval = TimeSpan.FromMilliseconds 50.0)
            timer.Tick.Add(fun _ ->
                skeletonOpacityPhase <- skeletonOpacityPhase + 0.12
                // 0.55 ~ 0.95 之间的平滑呼吸
                let alpha = 0.75 + 0.2 * Math.Sin(skeletonOpacityPhase)
                skeletonPanel.Opacity <- alpha)
            timer.Start()
            skeletonTimer <- Some timer

    member private this.RefreshSkeletonMotion() =
        if skeletonPanel.IsVisible then
            if MotionPolicy.isReduced () then
                this.StopSkeletonBreathing()
                skeletonPanel.Opacity <- 0.85
            elif attached then
                this.StartSkeletonBreathing()

    /// 构建骨架屏占位内容：
    /// Assistant (头像 + 多行正文条) -> User (右对齐气泡) -> Assistant (头像 + 多行正文条)
    member private this.EnsureSkeletonBuilt() =
        if skeletonPanel.Children.Count = 0 then
            Avalonia.Automation.AutomationProperties.SetName(skeletonPanel, "正在加载历史消息")
            let makeTextBar (height: float) (widthFraction: float) =
                let border =
                    Border(
                        Height = height,
                        CornerRadius = CornerRadius Tokens.radiusSm,
                        Background = Tokens.borderSoft,
                        Opacity = 0.65,
                        HorizontalAlignment = HorizontalAlignment.Stretch)
                let colStar = widthFraction
                let colRest = max 0.01 (1.0 - widthFraction)
                let grid = Grid(HorizontalAlignment = HorizontalAlignment.Stretch)
                grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(colStar, GridUnitType.Star)))
                grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(colRest, GridUnitType.Star)))
                Grid.SetColumn(border, 0)
                grid.Children.Add border
                grid :> Control

            let makeAssistantSkeleton (lineFractions: (float * float) list) =
                let avatar =
                    Border(
                        Width = 28.0,
                        Height = 28.0,
                        CornerRadius = CornerRadius Tokens.radiusPill,
                        Background = Tokens.borderSoft,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = Thickness(0.0, Tokens.iconBaselineNudge, Tokens.space3, 0.0),
                        Opacity = 0.85)
                let lines = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Stretch)
                for (h, w) in lineFractions do
                    lines.Children.Add(makeTextBar h w)
                let dock = DockPanel(LastChildFill = true, HorizontalAlignment = HorizontalAlignment.Stretch)
                DockPanel.SetDock(avatar, Dock.Left)
                dock.Children.Add avatar
                dock.Children.Add lines
                Avalonia.Automation.AutomationProperties.SetName(dock, "正在加载助手消息")
                dock :> Control

            let makeUserSkeleton () =
                let bubble =
                    Border(
                        Background = Tokens.borderSoft,
                        CornerRadius = CornerRadius(Tokens.radiusLg, Tokens.radiusLg, Tokens.radiusSm, Tokens.radiusLg),
                        Width = 260.0,
                        Height = 40.0,
                        Opacity = 0.55,
                        HorizontalAlignment = HorizontalAlignment.Right)
                let avatar =
                    Border(
                        Width = 28.0,
                        Height = 28.0,
                        CornerRadius = CornerRadius Tokens.radiusPill,
                        Background = Tokens.borderSoft,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = Thickness(0.0, Tokens.iconBaselineNudge, 0.0, 0.0),
                        Opacity = 0.55)
                let stack = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space3, HorizontalAlignment = HorizontalAlignment.Right)
                stack.Children.Add bubble
                stack.Children.Add avatar
                Avalonia.Automation.AutomationProperties.SetName(stack, "正在加载用户消息")
                stack :> Control

            // 助手骨架 1
            skeletonPanel.Children.Add(makeAssistantSkeleton [ (14.0, 0.45); (13.0, 0.85); (13.0, 0.70); (13.0, 0.35) ])
            // 用户骨架
            skeletonPanel.Children.Add(makeUserSkeleton ())
            // 助手骨架 2
            skeletonPanel.Children.Add(makeAssistantSkeleton [ (14.0, 0.60); (13.0, 0.90); (13.0, 0.75); (13.0, 0.50) ])

    /// 显示骨架屏（换会话或初次加载中，替代空白屏与“加载中…”）
    member this.ShowSkeletonLoading() =
        this.ResetScrollState()
        this.EnsureSkeletonBuilt()
        lastEmptyState <- None
        emptyPanel.IsVisible <- false
        messagePanel.IsVisible <- false
        scroller.IsVisible <- true
        skeletonPanel.IsVisible <- true
        scroller.Content <- skeletonPanel
        this.StartSkeletonBreathing()

    /// 隐藏骨架屏
    member this.HideSkeletonLoading() =
        if skeletonPanel.IsVisible then
            this.StopSkeletonBreathing()
            skeletonPanel.IsVisible <- false
            scroller.Content <- messagePanel
            messagePanel.IsVisible <- true

    member this.IsSkeletonVisible: bool = skeletonPanel.IsVisible
    member this.IsMessagePanelVisible: bool = messagePanel.IsVisible

    /// 重绘全部消息。流式期间由 `streamingMessage` 追加一条临时消息。
    member this.RenderMessages
        (
            messages: MessageView list,
            streamingMessage: MessageView option,
            error: (GenerationError * (unit -> unit)) option,
            fontSize: float,
            autoCollapseReasoning: bool,
            usage: GenerationUsage option,
            missingAttachments: Set<string>
        ) =
        this.HideSkeletonLoading()
        let wasAtBottom = atBottom
        let merged = MessageView.mergeToolResults messages
        let lastAssistantIndex =
            merged
            |> List.mapi (fun index m -> index, m)
            |> List.filter (fun (_, m) -> not (MessageView.isUser m))
            |> List.tryLast
            |> Option.map fst
        let ctxFor (index: int) (isStreaming: bool) =
            { fontSize = fontSize
              autoCollapseReasoning = autoCollapseReasoning
              streaming = isStreaming
              isLastAssistant = (streamingMessage.IsNone && lastAssistantIndex = Some index)
              usage = usage
              missingAttachments = missingAttachments
              brandAvatar = fun () -> brandLogo Tokens.logoAvatar }
        // 先列出这一帧想要的卡片：已提交的带身份（可复用），
        // 流式卡与错误卡没有身份（每帧必然变，本来就该重建）
        let desired = ResizeArray<CardKey option * (unit -> Control)>()

        merged
        |> List.iteri (fun index message ->
            if MessageView.hasVisibleBody message then
                let ctx = ctxFor index false
                let key =
                    message.commitId
                    |> Option.map (fun commit ->
                        { commit = commit
                          role = message.role
                          toolCalls = List.length message.toolCalls
                          toolResults = List.length message.toolResults
                          attachments = List.length message.attachments
                          fontSize = ctx.fontSize
                          collapse = ctx.autoCollapseReasoning
                          lastAssistant = ctx.isLastAssistant
                          usageTag = (if ctx.isLastAssistant then hash ctx.usage else 0)
                          missingTag =
                            (if List.isEmpty message.attachments then 0 else hash ctx.missingAttachments) })
                desired.Add(key, fun () -> MessageCard.render message ctx actions.message (Some index)))

        match streamingMessage with
        | Some streaming when MessageView.hasVisibleBody streaming ->
            desired.Add(None, fun () -> MessageCard.render streaming (ctxFor -1 true) actions.message None)
        | _ -> ()
        match error with
        | Some(err, retry) -> desired.Add(None, fun () -> MessageCard.errorCard err retry)
        | None -> ()

        // 只动前缀之后的部分。追加一条消息或刷新流式卡时，前面几十上百张卡不动。
        let mutable common = 0
        while common < mountedKeys.Length
              && common < desired.Count
              && (match mountedKeys[common], fst desired[common] with
                  | Some mounted, Some wanted -> mounted = wanted
                  | _ -> false) do
            common <- common + 1

        if messagePanel.Children.Count > common then
            messagePanel.Children.RemoveRange(common, messagePanel.Children.Count - common)

        for index in common .. desired.Count - 1 do
            let key, create = desired[index]
            let control =
                match key with
                | None -> create ()
                | Some k ->
                    match cardCache.TryGetValue k with
                    | true, cached -> cached
                    | _ ->
                        let created = create ()
                        cardCache[k] <- created
                        created
            messagePanel.Children.Add control

        mountedKeys <- desired |> Seq.map fst |> Array.ofSeq

        // 缓存只保留这一帧用到的身份：换会话、改字号都会让旧身份失效，
        // 留着只是占内存
        let live = HashSet<CardKey>()
        for key in mountedKeys do
            match key with
            | Some k -> live.Add k |> ignore
            | None -> ()
        if cardCache.Count > live.Count then
            for stale in cardCache.Keys |> Seq.filter (live.Contains >> not) |> Array.ofSeq do
                cardCache.Remove stale |> ignore

        if wasAtBottom then this.ScrollToEndDeferred()
        else
            let newCount = desired.Count
            let delta = newCount - previousRenderedMessageCount
            // 流式 delta 替换同一张卡（delta = 0）时绝不记未读：
            // 用户正看着的那条卡在原地刷新，不是“新消息”。
            if delta > 0 && previousRenderedMessageCount > 0 then
                unreadSinceScrolledUp <- unreadSinceScrolledUp + delta
            elif unreadSinceScrolledUp = 0 && not wasAtBottom && delta > 0 then
                unreadSinceScrolledUp <- max 1 delta
            this.UpdateScrollToBottomAppearance()
        previousRenderedMessageCount <- desired.Count

    member private this.UpdateScrollToBottomAppearance() =
        let extent = scroller.Extent.Height
        let viewport = scroller.Viewport.Height
        scrollToBottomButton.Margin <- Thickness(0.0, 0.0, Tokens.space5, Tokens.space4)
        if atBottom then
            unreadSinceScrolledUp <- 0
            scrollToBottomButton.IsVisible <- false
            scrollToBottomLabel.Text <- "回到最新"
            scrollToBottomLabel.IsVisible <- false
            ToolTip.SetTip(scrollToBottomButton, "回到最新")
            Avalonia.Automation.AutomationProperties.SetName(scrollToBottomButton, "回到最新")
            let size = if compactMode then LayoutPolicy.compactActionTarget else Tokens.iconButton
            Ui.setSquareTarget scrollToBottomButton size
            scrollToBottomButton.Padding <- Thickness 0.0
        else
            scrollToBottomButton.IsVisible <- extent > viewport + ContentMetrics.scrollBottomRevealThreshold
            if unreadSinceScrolledUp > 0 then
                let text = sprintf "%d 条新消息" unreadSinceScrolledUp
                scrollToBottomLabel.Text <- text
                scrollToBottomLabel.IsVisible <- not compactMode
                ToolTip.SetTip(scrollToBottomButton, text)
                Avalonia.Automation.AutomationProperties.SetName(scrollToBottomButton, text)
                if compactMode then
                    let size = LayoutPolicy.compactActionTarget
                    Ui.setSquareTarget scrollToBottomButton size
                    scrollToBottomButton.Padding <- Thickness 0.0
                else
                    let height = Tokens.iconButton
                    scrollToBottomButton.Width <- Double.NaN
                    scrollToBottomButton.MinWidth <- 96.0
                    scrollToBottomButton.Height <- height
                    scrollToBottomButton.MinHeight <- height
                    scrollToBottomButton.Padding <- Thickness(Tokens.space3, 0.0, Tokens.space4, 0.0)
            else
                scrollToBottomLabel.Text <- "回到最新"
                scrollToBottomLabel.IsVisible <- false
                ToolTip.SetTip(scrollToBottomButton, "回到最新")
                Avalonia.Automation.AutomationProperties.SetName(scrollToBottomButton, "回到最新")
                let size = if compactMode then LayoutPolicy.compactActionTarget else Tokens.iconButton
                Ui.setSquareTarget scrollToBottomButton size
                scrollToBottomButton.Padding <- Thickness 0.0

    /// 丢弃卡片缓存，下一次重绘全部重建。
    ///
    /// 后台着色算完时要用：着色结果缓存在 `Highlight` 里，
    /// 但卡片身份没变，不清缓存的话屏幕上会一直挂着没着色的那一版。
    member _.InvalidateCards() =
        cardCache.Clear()
        mountedKeys <- [||]

    /// 滚到底必须等布局把新内容的高度算进 Extent，否则会停在旧的底部，
    /// 末条消息（例如带「重试」按钮的错误卡片）就被输入区挡住。
    member this.ScrollToEndDeferred() =
        let rec attempt (remaining: int) =
            Dispatcher.UIThread.Post(
                (fun () ->
                    scroller.ScrollToEnd()
                    if remaining > 0 then attempt (remaining - 1)),
                DispatcherPriority.Background)
        attempt 3

    member this.ScrollToEnd() = this.ScrollToEndDeferred()

    /// 平滑滚动到底部
    member this.SmoothScrollToEnd() =
        smoothScrollTimer |> Option.iter (fun t -> t.Stop())
        smoothScrollTimer <- None
        if MotionPolicy.isReduced () then
            this.ScrollToEndDeferred()
        else
            let startOffset = scroller.Offset.Y
            let maxTarget = max 0.0 (scroller.Extent.Height - scroller.Viewport.Height)
            let distance = maxTarget - startOffset
            if distance < 5.0 then
                atBottom <- true
                unreadSinceScrolledUp <- 0
                this.ScrollToEndDeferred()
                this.UpdateScrollToBottomAppearance()
            else
                let startTime = DateTime.UtcNow
                let durationMs = 180.0
                let timer = new DispatcherTimer(Interval = TimeSpan.FromMilliseconds 16.0)
                timer.Tick.Add(fun _ ->
                    let elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds
                    let currentExtent = scroller.Extent.Height
                    let currentViewport = scroller.Viewport.Height
                    let target = max 0.0 (currentExtent - currentViewport)
                    if elapsed >= durationMs || target <= 0.0 then
                        timer.Stop()
                        smoothScrollTimer <- None
                        atBottom <- true
                        unreadSinceScrolledUp <- 0
                        scroller.ScrollToEnd()
                        this.UpdateScrollToBottomAppearance()
                    else
                        let progress = min 1.0 (elapsed / durationMs)
                        // Ease-out cubic: 1 - (1 - t)^3
                        let eased = 1.0 - Math.Pow(1.0 - progress, 3.0)
                        let newY = startOffset + (target - startOffset) * eased
                        scroller.Offset <- Vector(scroller.Offset.X, newY))
                smoothScrollTimer <- Some timer
                timer.Start()

    /// 加载更早历史前记录当前滚动几何。分页是 prepend，不保留 anchor 的话
    /// 新内容一插到顶部，用户正在读的那条消息会瞬间跳出视口。
    member _.BeginHistoryPrependAnchor() =
        pendingHistoryAnchor <- Some(scroller.Extent.Height, scroller.Offset.Y)

    /// HistoryPage 已写入 state 并完成 RenderMessages 后调用。用 extent 增量补偿
    /// prepend 的新增高度，让原先 viewport 内的内容保持在原来的屏幕位置。
    member _.RestoreHistoryPrependAnchorDeferred() =
        match pendingHistoryAnchor with
        | None -> ()
        | Some(oldExtent, oldOffset) ->
            pendingHistoryAnchor <- None
            Dispatcher.UIThread.Post(
                (fun () ->
                    let delta = max 0.0 (scroller.Extent.Height - oldExtent)
                    let target = max 0.0 (oldOffset + delta)
                    scroller.Offset <- Vector(scroller.Offset.X, target)),
                DispatcherPriority.Background)

    member _.CancelHistoryPrependAnchor() = pendingHistoryAnchor <- None

    member this.Build() =
        this.Background <- Tokens.canvas

        Ui.onClick titleAction (fun () -> this.BeginTitleEdit())
        titleAction.KeyDown.Add(fun e ->
            if (e.Key = Key.F2 || e.Key = Key.Enter) && titleEditable then
                e.Handled <- true
                this.BeginTitleEdit())
        titleAction.PointerEntered.Add(fun _ -> updateTitleActionVisual ())
        titleAction.PointerExited.Add(fun _ -> updateTitleActionVisual ())
        titleAction.GotFocus.Add(fun _ -> updateTitleActionVisual ())
        titleAction.LostFocus.Add(fun _ -> updateTitleActionVisual ())
        titleEditBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                this.CommitTitle true
            elif e.Key = Key.Escape then
                e.Handled <- true
                this.CancelTitleEdit())
        titleEditBox.GotFocus.Add(fun _ ->
            titleEditShell.BorderBrush <- Tokens.accent
            titleEditShell.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accentSoft.Color)))
        titleEditBox.LostFocus.Add(fun _ ->
            titleEditShell.BorderBrush <- Tokens.border
            titleEditShell.BoxShadow <- BoxShadows()
            if editingTitle then
                let top = TopLevel.GetTopLevel(this)
                let newFocus = if isNull top || isNull top.FocusManager then null else top.FocusManager.GetFocusedElement()
                let shouldRestoreFocus = isNull newFocus || obj.ReferenceEquals(newFocus, top) || obj.ReferenceEquals(newFocus, this)
                this.CommitTitle shouldRestoreFocus)
        titleEditShell.PointerEntered.Add(fun _ ->
            if not titleEditBox.IsFocused then titleEditShell.BorderBrush <- Tokens.line)
        titleEditShell.PointerExited.Add(fun _ ->
            if not titleEditBox.IsFocused then titleEditShell.BorderBrush <- Tokens.border)

        Ui.onClick stopButton (fun () -> actions.stopGeneration ())
        Ui.onClick forkButton (fun () -> actions.forkFromHere ())
        Ui.onClick sessionSettingsButton (fun () -> actions.openSessionSettings ())
        Ui.onClick sidebarToggleButton (fun () -> actions.toggleSidebar ())
        stopButton.IsVisible <- false
        forkButton.IsVisible <- false
        sessionSettingsButton.IsVisible <- false

        // 顶栏左组不能用横向 StackPanel：它会用无限宽测量标题，长标题不会真正
        // 进入 ellipsis，而是去挤右侧动作。Grid 把标题放进唯一可 shrink 的 Star 列。
        let leftGroup = Grid(ColumnSpacing = Tokens.space2, VerticalAlignment = VerticalAlignment.Center)
        leftGroup.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        leftGroup.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        leftGroup.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        titleHost.MinWidth <- 0.0
        titleHost.HorizontalAlignment <- HorizontalAlignment.Stretch
        titleHost.VerticalAlignment <- VerticalAlignment.Center
        titleText.HorizontalAlignment <- HorizontalAlignment.Stretch
        titleAction.HorizontalAlignment <- HorizontalAlignment.Stretch
        titleEditShell.HorizontalAlignment <- HorizontalAlignment.Stretch
        Grid.SetColumn(sidebarToggleButton, 0)
        Grid.SetColumn(titleHost, 1)
        Grid.SetColumn(generatingChip, 2)
        leftGroup.Children.Add sidebarToggleButton
        leftGroup.Children.Add titleHost
        leftGroup.Children.Add generatingChip
        // 从左到右按保留优先级排列：Stop > Session Settings > Fork。
        let rightGroup = Ui.hstack Tokens.space1 [ stopButton :> Control; sessionSettingsButton :> Control; forkButton :> Control ]
        let headerDock = DockPanel(LastChildFill = true, VerticalAlignment = VerticalAlignment.Center)
        DockPanel.SetDock(rightGroup, Dock.Right)
        headerDock.Children.Add rightGroup
        headerDock.Children.Add leftGroup
        headerBar <-
            Border(
                Height = Tokens.barHeight,
                Padding = Thickness(Tokens.shellInset, 0.0, Tokens.shellInset, 0.0),
                BorderBrush = Tokens.borderSoft,
                BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0),
                IsVisible = false,
                Child = headerDock)
        let header = headerBar

        emptyPanel.Children.Add(
            let logo = brandLogo Tokens.logoEmpty
            logo.HorizontalAlignment <- HorizontalAlignment.Center
            logo)
        emptyPanel.Children.Add emptyTitle
        emptyPanel.Children.Add emptyHint
        emptyPanel.Children.Add emptyActions

        // 让 Avalonia 的 ContentPresenter 完成 reading column 的收缩/拉伸；
        // `MaxWidth + Stretch` 取代手工监听 Viewport 后写 Width，避免 resize/scale 时
        // 维护第二套宽度同步逻辑。
        scroller.Content <- messagePanel
        scroller.ScrollChanged.Add(fun e ->
            let extent = scroller.Extent.Height
            let viewport = scroller.Viewport.Height
            let offset = scroller.Offset.Y
            // 用户手动向上滚动（或滚轮/触控向上拉），立即打断平滑自动滚动，不与用户手势冲突
            if e.OffsetDelta.Y < -0.1 then
                smoothScrollTimer |> Option.iter (fun t -> t.Stop())
                smoothScrollTimer <- None
            let wasAtBottom = atBottom
            let nowAtBottom = extent - viewport - offset < ContentMetrics.scrollBottomThreshold
            atBottom <- nowAtBottom
            if nowAtBottom then
                unreadSinceScrolledUp <- 0
                scrollToBottomButton.IsVisible <- false
            this.UpdateScrollToBottomAppearance()
            // 骨架可见时不预取更早历史：会话切换中的 extent 抖动会误触发，
            // 等真实内容挂载后再按正常阈值取。
            if offset <= 0.5 && extent > viewport && not skeletonPanel.IsVisible then actions.requestOlderHistory ())
        scroller.PointerWheelChanged.Add(fun e ->
            if e.Delta.Y > 0.0 then
                smoothScrollTimer |> Option.iter (fun t -> t.Stop())
                smoothScrollTimer <- None)

        scrollToBottomButton.Child <- scrollToBottomContent
        scrollToBottomLabel.IsVisible <- false
        scrollToBottomButton.BoxShadow <- Tokens.shadowPopup ()
        scrollToBottomButton.IsVisible <- false
        scrollToBottomButton.HorizontalAlignment <- HorizontalAlignment.Right
        scrollToBottomButton.VerticalAlignment <- VerticalAlignment.Bottom
        scrollToBottomButton.Margin <- Thickness(0.0, 0.0, Tokens.space5, Tokens.space4)
        let doScrollToBottom () =
            atBottom <- true
            unreadSinceScrolledUp <- 0
            this.UpdateScrollToBottomAppearance()
            this.SmoothScrollToEnd()
        Ui.onClick scrollToBottomButton doScrollToBottom

        let body = Grid()
        body.Children.Add scroller
        body.Children.Add emptyPanel
        body.Children.Add scrollToBottomButton

        let layout = DockPanel()
        DockPanel.SetDock(header, Dock.Top)
        layout.Children.Add header
        layout.Children.Add body
        this.Child <- layout

        this.AttachedToVisualTree.Add(fun _ ->
            attached <- true
            motionSubscription |> Option.iter (fun s -> s.Dispose())
            motionSubscription <- Some(MotionPolicy.Changed.Publish.Subscribe(fun _ -> this.RefreshSkeletonMotion()))
            if skeletonPanel.IsVisible then
                this.RefreshSkeletonMotion())
        this.DetachedFromVisualTree.Add(fun _ ->
            attached <- false
            this.StopSkeletonBreathing()
            if skeletonPanel.IsVisible then
                skeletonPanel.Opacity <- 0.85
            smoothScrollTimer |> Option.iter (fun t -> t.Stop())
            smoothScrollTimer <- None
            motionSubscription |> Option.iter (fun s -> s.Dispose())
            motionSubscription <- None)
