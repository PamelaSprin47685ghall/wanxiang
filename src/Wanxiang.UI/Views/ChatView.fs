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
            Focusable = true,
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
            // 滚到底时末条消息与输入区之间的呼吸量。必须放在这里：
            // ScrollViewer.Padding 的下值不进可滚动范围（见 ScrollLayoutTests）。
            // 实测可见量比设定值小约 15pt，48 对应约 33pt 的净留白。
            Margin = Thickness(0.0, 0.0, 0.0, ContentMetrics.messageEndBreathing),
            MaxWidth = Tokens.readingWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch)
    let scroller =
        ScrollViewer(
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // 下内边距对 ScrollViewer 无效（不计入可滚动范围，见 ScrollLayoutTests），
            // 末条消息与输入区之间的留白由 messagePanel 的下边距承担
            Padding = Thickness(Tokens.shellInset, Tokens.space5, Tokens.shellInset, 0.0))

    let emptyPanel =
        StackPanel(
            Orientation = Orientation.Vertical,
            Spacing = Tokens.space3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = ContentMetrics.emptyStateMaxWidth,
            IsVisible = false)
    let emptyTitle =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontHeading,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.text,
            TextAlignment = TextAlignment.Center,
            LetterSpacing = 0.4)
    let emptyHint =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontBody,
            Foreground = Tokens.textMuted,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = ReadingRhythm.emptyStateLineHeight)
    let emptyActions = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Center)
    let mutable lastEmptyState: (ChatEmptyState * string option) option = None

    /// 已渲染的卡片，按身份缓存。
    ///
    /// 不加这层缓存的话，流式期间每批 delta 都要把整表拆掉重建：
    /// 200 条消息实测约 100ms 一次，越聊越卡，而其中只有最后一张卡真的变了。
    let cardCache = Dictionary<CardKey, Control>()
    /// 当前挂在面板上的卡片身份，用来算最长公共前缀
    let mutable mountedKeys: CardKey option array = [||]

    let scrollToBottomButton = Ui.iconButtonAccent Icons.arrowDown "回到最新"

    let mutable atBottom = true
    let mutable headerBar: Border = Unchecked.defaultof<Border>
    let mutable pendingHistoryAnchor: (float * float) option = None
    let mutable compactMode = false
    let mutable hasConversationChrome = false
    let mutable isGenerating = false
    let mutable titleEditable = false

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
        titleHost.Children.Add titleAction
        titleHost.Children.Add titleEditShell
        titleEditShell.IsVisible <- false

    member private this.CommitTitle(restoreFocus: bool) =
        let next = if isNull titleEditBox.Text then "" else titleEditBox.Text.Trim()
        titleEditShell.IsVisible <- false
        titleAction.IsVisible <- true
        if not (String.IsNullOrWhiteSpace next) && next <> titleText.Text then
            actions.renameTitle next
        if restoreFocus then
            Dispatcher.UIThread.Post(fun () -> titleAction.Focus(NavigationMethod.Tab) |> ignore)

    member private this.BeginTitleEdit() =
        if titleAction.IsVisible && titleAction.Focusable && not (String.IsNullOrWhiteSpace titleText.Text) then
            titleEditBox.Text <- titleText.Text
            titleAction.IsVisible <- false
            titleEditShell.IsVisible <- true
            Dispatcher.UIThread.Post(fun () ->
                titleEditBox.Focus() |> ignore
                titleEditBox.SelectAll())

    member this.SetTitle(text: string, editable: bool) =
        titleEditable <- editable
        titleText.Text <- text
        titleText.Foreground <- if editable then Tokens.text else Tokens.textFaint
        titleAction.Focusable <- editable
        titleAction.Cursor <- if editable then new Cursor(StandardCursorType.Hand) else null
        ToolTip.SetTip(titleAction, if editable then "点击重命名" else null)
        Avalonia.Automation.AutomationProperties.SetName(
            titleAction,
            if editable then sprintf "重命名会话：%s" text else text)

    member this.SetGenerating(generating: bool, statusText: string) =
        isGenerating <- generating
        Ui.setReservedActionVisible generatingChip (generating && not compactMode)
        generatingCaption.Text <- if String.IsNullOrWhiteSpace statusText then "生成中" else statusText
        stopButton.IsVisible <- generating

    /// 会话状态变化时同步顶栏功能按钮的可用性。
    member this.SetConversationChrome(hasConversation: bool) =
        hasConversationChrome <- hasConversation
        forkButton.IsVisible <- hasConversation && not compactMode
        sessionSettingsButton.IsVisible <- hasConversation
        if not (isNull (box headerBar)) then headerBar.IsVisible <- true

    member _.SetCompactMode(value: bool) =
        compactMode <- value
        let size = if value then LayoutPolicy.compactActionTarget else Tokens.iconButton
        for button in [ sidebarToggleButton; stopButton; forkButton; sessionSettingsButton; scrollToBottomButton ] do
            Ui.setSquareTarget button size
        Ui.setReservedActionVisible generatingChip (isGenerating && not value)
        forkButton.IsVisible <- hasConversationChrome && not value

    member this.ShowEmpty(state: ChatEmptyState, onPrimary: (string * (unit -> unit)) option) =
        let primaryLabel = onPrimary |> Option.map fst
        let currentStateKey = (state, primaryLabel)
        if lastEmptyState <> Some currentStateKey then
            lastEmptyState <- Some currentStateKey
            emptyActions.Children.Clear()
            let title, hint =
                match state with
                | NotConnected -> "先连接一台万象服务器", "服务端负责运行模型与保存会话；客户端只是它的一个视图。"
                | NoProvider -> "还没有可用的模型", "添加一个服务商并填入密钥，就可以开始对话了。"
                | NoConversation -> "开始一次新对话", "左侧新建会话，或从历史里挑一个继续。"
                | EmptyConversation -> "说点什么吧", "这个会话还是空的。你的第一句话会决定它的标题。"
            emptyTitle.Text <- title
            emptyHint.Text <- hint
            match onPrimary with
            | Some(label, action) ->
                let button = Ui.button Ui.Primary label action
                button.HorizontalAlignment <- HorizontalAlignment.Center
                emptyActions.Children.Add button
            | None -> ()
        emptyPanel.IsVisible <- true
        scroller.IsVisible <- false
        messagePanel.IsVisible <- false

    member this.HideEmpty() =
        lastEmptyState <- None
        emptyPanel.IsVisible <- false
        scroller.IsVisible <- true
        messagePanel.IsVisible <- true

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
                desired.Add(key, fun () -> MessageCard.render message ctx actions.message))

        match streamingMessage with
        | Some streaming when MessageView.hasVisibleBody streaming ->
            desired.Add(None, fun () -> MessageCard.render streaming (ctxFor -1 true) actions.message)
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
            if e.Key = Key.F2 && titleEditable then
                e.Handled <- true
                this.BeginTitleEdit())
        titleAction.PointerEntered.Add(fun _ ->
            if titleEditable then titleText.Foreground <- Tokens.accent)
        titleAction.PointerExited.Add(fun _ ->
            titleText.Foreground <- if titleEditable then Tokens.text else Tokens.textFaint)
        titleEditBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                this.CommitTitle true
            elif e.Key = Key.Escape then
                e.Handled <- true
                titleEditShell.IsVisible <- false
                titleAction.IsVisible <- true
                Dispatcher.UIThread.Post(fun () -> titleAction.Focus(NavigationMethod.Tab) |> ignore))
        titleEditBox.LostFocus.Add(fun _ -> if titleEditShell.IsVisible then this.CommitTitle false)

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
        titleText.HorizontalAlignment <- HorizontalAlignment.Stretch
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
        scroller.ScrollChanged.Add(fun _ ->
            let extent = scroller.Extent.Height
            let viewport = scroller.Viewport.Height
            let offset = scroller.Offset.Y
            atBottom <- extent - viewport - offset < ContentMetrics.scrollBottomThreshold
            scrollToBottomButton.IsVisible <- not atBottom && extent > viewport + ContentMetrics.scrollBottomRevealThreshold
            if offset <= 0.5 && extent > viewport then actions.requestOlderHistory ())

        scrollToBottomButton.IsVisible <- false
        scrollToBottomButton.HorizontalAlignment <- HorizontalAlignment.Right
        scrollToBottomButton.VerticalAlignment <- VerticalAlignment.Bottom
        scrollToBottomButton.Margin <- Thickness(0.0, 0.0, Tokens.space5, Tokens.space4)
        let doScrollToBottom () =
            atBottom <- true
            scroller.ScrollToEnd()
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
