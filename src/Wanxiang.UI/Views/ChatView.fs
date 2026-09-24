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
            BorderThickness = Thickness ControlMetrics.borderWidth,
            CornerRadius = CornerRadius Tokens.radiusSm,
            Height = Tokens.iconButton,
            MinHeight = Tokens.iconButton,
            MaxHeight = Tokens.iconButton,
            MinWidth = ControlMetrics.emptyStateTitleMinWidth,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = Thickness(Tokens.space3, 0.0),
            Focusable = false,
            Child = titleText)

    let generatingChip =
        Border(
            Background = Tokens.accentFaint,
            CornerRadius = CornerRadius Tokens.radiusPill,
            Padding = Thickness(ControlMetrics.chipPaddingX, ControlMetrics.chipPaddingY),
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

    // 上一条 / 下一条：跨问答跳转。长会话里找「上一个提问」不该靠滚轮拖半屏。
    // 借 kelivo scroll_nav_buttons 的上/下一条语义，但收敛到顶栏按钮组：
    // 桌面端的全局聊天动作本来就在这一行（停止 / 分叉 / 会话设置），
    // 不再引入第二套悬浮按钮层——那会压住消息内容，也和顶栏形成第二个动作入口。
    let prevExchangeButton = Ui.iconButton Icons.chevronsUp "上一条提问"
    let nextExchangeButton = Ui.iconButton Icons.chevronsDown "下一条提问"
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
            // 末条消息与输入区之间的留白统一由 messagePanel.Margin 承担，底部 Padding 设为 0 避免叠加。
            // 水平留白先落最窄档（shellInset=16，同 horizontalInset 0.0 的取值），
            // 挂树后由 applyHorizontalInsets 按 ChatView 自身宽度实时取档（见 Build）。
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
    let mutable currentPrimaryAction: unit -> unit = ignore
    let emptyActionButton =
        let btn = Ui.button Ui.Primary "" (fun () -> currentPrimaryAction ())
        btn.HorizontalAlignment <- HorizontalAlignment.Center
        btn.VerticalAlignment <- VerticalAlignment.Center
        btn.MinWidth <- ControlMetrics.emptyStateActionMinWidth
        btn.Height <- ControlMetrics.emptyStateActionHeight
        btn.CornerRadius <- CornerRadius Tokens.radiusMd
        btn.Padding <- Thickness(Tokens.space4, 0.0)
        btn.Focusable <- true
        btn.IsVisible <- false
        btn
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
    /// 上一帧是否挂着流式卡。流式卡转正（streamingMessage 从 Some 变 None，
    /// 已提交消息进列表）时 desired 条数不变，delta 计未读那条路看不见这次新增：
    /// 用户划上去等生成，回复落定那一刻计数仍是 0，「回到最新」不带任何提示。
    /// kelivo 在 scroll_controller.dart:520 用 stickToBottomAfterGeneration 记同一件事。
    let mutable wasStreaming = false
    let mutable previousRenderedMessageCount: int = 0
    let mutable smoothScrollTimer: DispatcherTimer option = None

    let mutable atBottom = true
    /// 当前「上一条 / 下一条」跳转锚点：用户消息卡的身份。
    /// None 表示还没跳转过，此时以当前所在的那一轮问答为基准（kelivo 的
    /// jump-to-question 语义；见 currentExchangeIndex）。
    let mutable exchangeAnchor: CardKey option = None
    let mutable headerBar: Border = Unchecked.defaultof<Border>
    let mutable pendingHistoryAnchor: (float * float) option = None
    let mutable compactMode = false
    let mutable hasConversationChrome = false
    let mutable isGenerating = false
    /// 停止按钮当前是否可点（生成中且已认证且存在生成 Id 时为 true）。
    /// 禁用态只在槽位可见时施加：隐藏帧上写 opacityDisabled 会让「不可见」重新显形。
    let mutable canStop = false
    let mutable titleEditable = false
    let mutable editingTitle = false

    // ---------- 问答跳转（上一条 / 下一条） ----------

    /// 当前面板上每张卡的「用户消息」身份，按挂载顺序。
    /// mountedKeys 与 messagePanel.Children 逐一对齐（渲染循环一帧一卡），
    /// 所以这里是用户卡的下标表，不必给卡打 Tag。
    let exchangeIndices () : int list =
        mountedKeys
        |> Array.mapi (fun index key ->
            match key with
            | Some k when k.role = "user" -> Some index
            | _ -> None)
        |> Array.choose id
        |> Array.toList

    /// 当前所在的那一轮问答 = 最后一张「开头已经在视口内」的用户卡。
    /// 不用 slack：贴底时倒数第二轮的回答可能还压在视口里，把它当成当前轮会让
    /// 「下一条」在已经最后一条时仍然可点。开场流式（没有已提交用户卡）时退回首张。
    let currentExchangeIndex () : int =
        let viewportBottom = scroller.Offset.Y + scroller.Viewport.Height
        let cards = exchangeIndices ()
        cards
        |> List.filter (fun index ->
            index < messagePanel.Children.Count
            && messagePanel.Children.[index].Bounds.Y <= viewportBottom)
        |> List.tryLast
        |> Option.orElseWith (fun () -> List.tryHead cards)
        |> Option.defaultValue 0

    /// 把某张用户卡平滑带到视口顶部：目标卡片尽量完整可见，不被输入区挡住。
    let smoothScrollToCard (child: Control) =
        // 与 SmoothScrollToEnd 同一条曲线、同一帧节拍；只换目标点。
        // 计时器登记到 smoothScrollTimer：连按「上一条 / 下一条」或动画途中滚轮，
        // 旧动画必须先停，否则两个动画都在写 Offset.Y，画面跟着两个目标打架。
        let stopSmoothScroll () =
            smoothScrollTimer |> Option.iter (fun t -> t.Stop())
            smoothScrollTimer <- None
        let target = max 0.0 (child.Bounds.Y - Tokens.space5)
        if abs (scroller.Offset.Y - target) < 5.0 then
            scroller.Offset <- Vector(scroller.Offset.X, target)
        elif MotionPolicy.isReduced () then
            scroller.Offset <- Vector(scroller.Offset.X, target)
        else
            stopSmoothScroll ()
            let startOffset = scroller.Offset.Y
            let startTime = DateTime.UtcNow
            let durationMs = MotionLedger.smoothScrollDuration.TotalMilliseconds
            let timer = new DispatcherTimer(Interval = MotionLedger.smoothScrollFrame)
            timer.Tick.Add(fun _ ->
                let elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds
                if elapsed >= durationMs then
                    stopSmoothScroll ()
                    scroller.Offset <- Vector(scroller.Offset.X, target)
                else
                    let progress = min 1.0 (elapsed / durationMs)
                    let eased = MotionPolicy.easeOutCubic.Ease progress
                    let newY = startOffset + (target - startOffset) * eased
                    scroller.Offset <- Vector(scroller.Offset.X, newY))
            smoothScrollTimer <- Some timer
            timer.Start()


    /// 顶栏两个跳转钮的可用态：端点处不可点，但保留槽位（只切 opacity/命中，不动宽度），
    /// 否则用户每翻到第一条按钮就消失一次，顶栏按钮会左右跳。
    let syncExchangeNavState () =
        let cards = exchangeIndices ()
        let anchorIndex =
            match exchangeAnchor with
            | Some key -> mountedKeys |> Array.tryFindIndex ((=) (Some key)) |> Option.defaultValue -1
            | None -> currentExchangeIndex ()
        let hasPrev = cards |> List.exists (fun index -> index < anchorIndex)
        let hasNext = cards |> List.exists (fun index -> index > anchorIndex)
        Ui.setEnabled prevExchangeButton hasPrev
        Ui.setEnabled nextExchangeButton hasNext
        // 提示随手性走：禁用写原因，重新可用时还原成动作名，不留下一条过时的
        // 「已经是第一条提问」骗用户（滚回去后它明明又可用）。
        ToolTip.SetTip(prevExchangeButton, if hasPrev then "上一条提问" else "已经是第一条提问")
        ToolTip.SetTip(nextExchangeButton, if hasNext then "下一条提问" else "已经是最后一条提问")


    do
        generatingChip.Child <-
            // Streaming 文本本身已经持续变化；Header 只需要一个静态状态点，
            // 不再额外跑第二个 spinner 与正文竞争注意力 / UI thread。
            let stateDot =
                Border(
                    Width = ControlMetrics.chipDotSize,
                    Height = ControlMetrics.chipDotSize,
                    CornerRadius = CornerRadius Tokens.radiusPill,
                    Background = Tokens.accent,
                    VerticalAlignment = VerticalAlignment.Center)
            let row = Ui.hstack Tokens.space2 [ stateDot :> Control; generatingCaption :> Control ]
            row
        titleEditShell.Height <- Tokens.iconButton
        titleEditShell.MinHeight <- Tokens.iconButton
        titleEditShell.MaxHeight <- Tokens.iconButton
        titleEditShell.MinWidth <- ControlMetrics.emptyStateTitleMinWidth
        titleEditShell.Padding <- Thickness(Tokens.space3, 0.0)
        titleEditShell.CornerRadius <- CornerRadius Tokens.radiusSm
        titleEditShell.Background <- Tokens.surface
        titleEditShell.BorderBrush <- Tokens.border
        titleEditShell.BorderThickness <- Thickness ControlMetrics.borderWidth
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

    /// 顶栏与滚动区共用同一档位、同一宽度来源：一次读取 ChatView 自身 Bounds
    ///（即父级 chatColumn 的布局槽宽）经 LayoutPolicy.horizontalInset 取档，
    /// 同步写 headerBar 与 scroller 两处水平留白——窗口宽度跨断档时同进同退，
    /// 任何宽度下标题左缘与消息列左缘对齐，不会各取各的档。
    /// 两处 Padding 都只裁剪各自内容、不改变任何控件自身 Bounds：写 Padding 不
    /// 引起 Bounds 变化，故无布局反馈环。宽度跨断档时值才真正改变。
    member private this.ApplyHorizontalInsets() =
        let inset = LayoutPolicy.horizontalInset this.Bounds.Width
        headerBar.Padding <- Thickness(inset, 0.0, inset, 0.0)
        scroller.Padding <- Thickness(inset, Tokens.space5, inset, 0.0)

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

    /// 顶栏生成中 chip 的显隐：SetGenerating 与 SetCompactMode 两条路径的唯一收口。
    ///
    /// 桌面（非 compact）保留常驻槽位防跳动：走 setReservedActionVisible，
    /// IsVisible 恒为 true，只切 Opacity 与可操作性，生成开始/结束时顶栏不跳。
    /// compact 下 chip 整体不占布局：那个原语会把 IsVisible 写死为 true，
    /// 在紧凑顶栏里会白吃一块标题宽度，所以直接收起并把 Opacity 归零。
    member private this.SyncGeneratingChipVisibility() =
        if compactMode then
            generatingChip.IsVisible <- false
            generatingChip.Opacity <- 0.0
        else
            Ui.setReservedActionVisible generatingChip isGenerating

    /// 停止按钮的常驻槽位：SetGenerating / SetCompactMode / 初始化三条路径的唯一收口。
    ///
    /// 与顶栏 chip 同一原语（Ui.setReservedActionVisible）：IsVisible 恒为 true，
    /// 只切 Opacity / 命中 / 聚焦 / 无障碍视图。停止按钮位于右侧 Dock.Right 组，
    /// 用 IsVisible 直接开关会让组宽、标题可用宽度与按钮位置在生成开始/结束时位移。
    /// 禁用态只在槽位可见（生成中）时施加：隐藏后再写 Opacity 会让隐藏按钮显形。
    member private this.SyncStopSlot() =
        Ui.setReservedActionVisible stopButton isGenerating
        if isGenerating then Ui.setEnabled stopButton canStop

    member this.SetGenerating(generating: bool, statusText: string) =
        isGenerating <- generating
        this.SyncGeneratingChipVisibility()
        generatingCaption.Text <- if String.IsNullOrWhiteSpace statusText then "生成中" else statusText
        this.SyncStopSlot()

    member this.SetCanStop(value: bool) =
        canStop <- value
        // 非生成态时停止按钮处于保留隐藏槽位，此时施加禁用视觉（setEnabled 会写
        // Opacity）会让隐藏的按钮重新显形。AppShell 的真实顺序是 SetGenerating
        // 之后跟一次 SetCanStop，隐藏帧上跳过即可保持「看不见」。
        if isGenerating then Ui.setEnabled stopButton value

    /// 会话状态变化时同步顶栏功能按钮的可用性。
    member this.SetConversationChrome(hasConversation: bool) =
        hasConversationChrome <- hasConversation
        forkButton.IsVisible <- hasConversation && not compactMode
        // 问答跳转只在有会话时出现；compact 档顶栏让位，和 forkButton 同一判据。
        prevExchangeButton.IsVisible <- hasConversation && not compactMode
        nextExchangeButton.IsVisible <- hasConversation && not compactMode
        syncExchangeNavState ()
        sessionSettingsButton.IsVisible <- hasConversation
        if not (isNull (box headerBar)) then headerBar.IsVisible <- true


    member this.SetCompactMode(value: bool) =
        compactMode <- value
        let size = if value then LayoutPolicy.compactActionTarget else Tokens.iconButton
        for button in [ sidebarToggleButton; stopButton; forkButton; sessionSettingsButton; prevExchangeButton; nextExchangeButton; scrollToBottomButton ] do
            Ui.setSquareTarget button size
        this.SyncStopSlot()
        sessionSettingsButton.IsVisible <- hasConversationChrome
        this.UpdateScrollToBottomAppearance()
        this.SyncGeneratingChipVisibility()
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
            match onPrimary with
            | Some(label, _) ->
                Ui.setButtonText emptyActionButton label
                ToolTip.SetTip(emptyActionButton, label)
                Avalonia.Automation.AutomationProperties.SetName(emptyActionButton, label)
                Avalonia.Automation.AutomationProperties.SetHelpText(emptyActionButton, label)
                emptyActionButton.IsVisible <- true
            | None -> emptyActionButton.IsVisible <- false
            match emptyActionButton.Parent with
            | :? Panel as panel -> panel.Children.Remove emptyActionButton |> ignore
            | _ -> ()
            let logo = brandLogo Tokens.logoEmpty
            logo.HorizontalAlignment <- HorizontalAlignment.Center
            emptyPanel.Children.Clear()
            emptyPanel.Children.Add(
                Ui.emptyStateWith true logo (Some title) hint None (Some emptyActionButton))
        emptyPanel.IsVisible <- true
        scroller.IsVisible <- false
        this.HideSkeletonLoading()
        messagePanel.IsVisible <- false

    member this.HideEmpty() =
        lastEmptyState <- None
        currentPrimaryAction <- ignore
        emptyActionButton.IsVisible <- false
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
        // 锚点跟着会话一起丢：换会话后旧锚引用的卡片已不在面板上。
        exchangeAnchor <- None
        this.UpdateScrollToBottomAppearance()
        syncExchangeNavState ()
    /// 停止骨架呼吸动画。
    member private this.StopSkeletonBreathing() =
        skeletonTimer |> Option.iter (fun t -> t.Stop())
        skeletonTimer <- None
        skeletonPanel.Opacity <- if skeletonPanel.IsVisible && MotionPolicy.isReduced () then Tokens.skeletonOpacityReduced else 1.0

    /// 启动骨架呼吸动画（低频平滑透明度呼吸）。
    member private this.StartSkeletonBreathing() =
        skeletonTimer |> Option.iter (fun t -> t.Stop())
        skeletonTimer <- None
        if MotionPolicy.isReduced () then
            skeletonPanel.Opacity <- Tokens.skeletonOpacityReduced
        elif not attached then
            // 尚未挂载到视觉树时不启动计时器；挂载时若骨架仍可见会触发
            skeletonPanel.Opacity <- Tokens.skeletonOpacityReduced
        else
            skeletonOpacityPhase <- 0.0
            let timer = new DispatcherTimer(Interval = MotionLedger.skeletonBreathFrame)
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
                skeletonPanel.Opacity <- Tokens.skeletonOpacityReduced
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
                        Opacity = Tokens.skeletonOpacityBase,
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
                        Width = ControlMetrics.skeletonAvatarSize,
                        Height = ControlMetrics.skeletonAvatarSize,
                        CornerRadius = CornerRadius Tokens.radiusPill,
                        Background = Tokens.borderSoft,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = Thickness(0.0, Tokens.iconBaselineNudge, Tokens.space3, 0.0),
                        Opacity = Tokens.skeletonOpacityReduced)
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
                        // 与真实用户气泡同一 chrome：hairlineStrong 细描边，
                        // 骨架换成真卡时轮廓不变，不产生跳变。
                        Background = Tokens.borderSoft,
                        BorderBrush = Tokens.hairlineStrong,
                        BorderThickness = Thickness ControlMetrics.borderWidth,
                        CornerRadius = CornerRadius(Tokens.radiusLg, Tokens.radiusLg, Tokens.radiusSm, Tokens.radiusLg),
                        Width = 260.0,
                        Height = 40.0,
                        Opacity = Tokens.skeletonOpacityBreathMin,
                        HorizontalAlignment = HorizontalAlignment.Right)
                let avatar =
                    Border(
                        Width = ControlMetrics.skeletonAvatarSize,
                        Height = ControlMetrics.skeletonAvatarSize,
                        CornerRadius = CornerRadius Tokens.radiusPill,
                        Background = Tokens.borderSoft,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = Thickness(0.0, Tokens.iconBaselineNudge, 0.0, 0.0),
                        Opacity = Tokens.skeletonOpacityBreathMin)
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
        // 流式收尾：卡数没变（流式卡原位换成已提交卡），delta 路抓不到，
        // 但只要用户不在底部，这就是一条刚落地的新消息。kelivo scroll_controller.dart:520
        // 的 stickToBottomAfterGeneration 记同一件事。
        let streamingJustEnded = wasStreaming && streamingMessage.IsNone
        wasStreaming <- streamingMessage.IsSome
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
        | Some(err, retry) -> desired.Add(None, fun () -> MessageCard.errorCard err retry None)
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
        // 锚点指向的卡可能已经不在了（流式卡换代、错误卡消失）：丢掉锚点，
        // 下一次跳转重新用视口位置起算，不会指向一张不存在的卡。
        match exchangeAnchor with
        | Some key when not (Array.contains (Some key) mountedKeys) -> exchangeAnchor <- None
        | _ -> ()
        syncExchangeNavState ()

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
            // 分页加载更早历史也是正的 delta，但那批消息在历史上就已经读过了，
            // 不是「新来的」：划上去翻旧账时冒出「N 条新消息」纯属误导。
            // 判据用已有信号 pendingHistoryAnchor：BeginHistoryPrependAnchor 在派发
            // 分页前置位，RestoreHistoryPrependAnchorDeferred 在本次渲染之后清位，
            // 因此这次渲染内它正好是 Some。不再另建一个 isLoadingHistory 字段。
            // 借 kelivo message_list_view.dart 的 layout 定位请求：内容从上方增长
            // 与尾部新增由两条不同通路处理，从不相混。
            // 流式子卡不进 delta 记账：它是占位，正文还没落定。用户划上去时
            // 看到进度不该记「新消息」——落定那次才记一次（见下方 streamingJustEnded）。
            let countDelta = if streamingMessage.IsSome then 0 else delta
            if pendingHistoryAnchor.IsNone then
                // 流式 delta 替换同一张卡（delta = 0）时绝不记未读：
                // 用户正看着的那条卡在原地刷新，不是“新消息”。
                if countDelta > 0 && previousRenderedMessageCount > 0 then
                    unreadSinceScrolledUp <- unreadSinceScrolledUp + countDelta
                elif unreadSinceScrolledUp = 0 && not wasAtBottom && countDelta > 0 then
                    unreadSinceScrolledUp <- max 1 countDelta
            // 上面两条只数「条数变化」，流式收尾是条数不变的一次新增，
            // 单独在分页判据之外补记：同一次渲染既有分页又有流式收尾不可能同时成立。
            if streamingJustEnded && not wasAtBottom && pendingHistoryAnchor.IsNone then
                unreadSinceScrolledUp <- max 1 (unreadSinceScrolledUp + 1)
            // 分页这一帧也要刷新按钮外观：未读计数不动，但按钮可见性/文案
            // 与「是否在底部」相关，同一处刷新不能漏掉这条路。
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
                    scrollToBottomButton.MinWidth <- ControlMetrics.scrollToBottomMinWidth
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

    /// 平滑滚动到顶部：与 SmoothScrollToEnd 同一条曲线、同一帧节拍，只换目标点。
    ///
    /// 为什么不能只写 Offset <- 0：ctrl+Home 常连着一次「看一下最早那个提问」的意图，
    /// 直接从数万像素高处瞬移会让用户失去当前位置感，平滑上滚与既有的「回到最新」
    /// 体感一致。Reduced motion 下退化为瞬移，与 ScrollToEndDeferred 同口径。
    ///
    /// 锚点同样在这里作废：这条路径也是用户意图入口（快捷键走的就是它），
    /// 理由见 ScrollToBeginning。
    member this.SmoothScrollToBeginning() =
        exchangeAnchor <- None
        // 一离开底部就不再是「贴底」：否则整个上滚过程里「回到最新」一直藏着，
        // 用户中途改主意只能继续滚。与 ScrollChanged 里 nowAtBottom 同一个判据的方向。
        atBottom <- false
        smoothScrollTimer |> Option.iter (fun t -> t.Stop())
        smoothScrollTimer <- None
        if MotionPolicy.isReduced () then
            this.ScrollToBeginningDeferred()
        else
            let startOffset = scroller.Offset.Y
            if startOffset < 5.0 then
                this.UpdateScrollToBottomAppearance()
            else
                let startTime = DateTime.UtcNow
                let durationMs = MotionLedger.smoothScrollDuration.TotalMilliseconds
                let timer = new DispatcherTimer(Interval = MotionLedger.smoothScrollFrame)
                timer.Tick.Add(fun _ ->
                    let elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds
                    if elapsed >= durationMs then
                        timer.Stop()
                        smoothScrollTimer <- None
                        scroller.Offset <- Vector(scroller.Offset.X, 0.0)
                        this.UpdateScrollToBottomAppearance()
                    else
                        let progress = min 1.0 (elapsed / durationMs)
                        // 同一 easeOutCubic：上滚与下滚共享同一条曲线，端点两条路手感不劈叉。
                        let eased = MotionPolicy.easeOutCubic.Ease progress
                        let newY = startOffset * (1.0 - eased)
                        scroller.Offset <- Vector(scroller.Offset.X, newY)
                        this.UpdateScrollToBottomAppearance())
                smoothScrollTimer <- Some timer
                timer.Start()

    /// 滚到顶必须等布局把新内容的高度算进 Extent：与 ScrollToEndDeferred 对称地重试几次，
    /// 否则 prepend 的更早历史刚到那一帧还会停在旧顶端。
    member this.ScrollToBeginningDeferred() =
        let rec attempt (remaining: int) =
            Dispatcher.UIThread.Post(
                (fun () ->
                    scroller.ScrollToHome()
                    if remaining > 0 then attempt (remaining - 1)),
                DispatcherPriority.Background)
        attempt 3

    /// 用户意图入口：回到会话开头。
    ///
    /// 与 Deferred/Smooth 两兄弟的分工同 ScrollToEnd：这个成员是「用户说要回到开头」，
    /// 因此把阅读位置相关的状态一起收口——锚点作废，让接下来的「上一条 / 下一条」
    /// 从新的视口重新起算。留着旧锚点会让导航从半途那轮开始，看起来像跳过了
    /// 用户刚刚翻过去的内容。
    ///
    /// 锚点只能在这一层清：Deferred 那层被自动跟随（每个流式 delta）反复调用，
    /// 在那里清等于每次接收都丢锚点。
    member this.ScrollToBeginning() =
        exchangeAnchor <- None
        this.ScrollToBeginningDeferred()

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
                let durationMs = MotionLedger.smoothScrollDuration.TotalMilliseconds
                let timer = new DispatcherTimer(Interval = MotionLedger.smoothScrollFrame)
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
                        // 统一缓动：改走 MotionPolicy.easeOutCubic（与全应用状态补间同一条
                        // 曲线），替换本地手写 cubic，兑现 smoothScrollDuration 的 ease-out 注释。
                        let eased = MotionPolicy.easeOutCubic.Ease progress
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

    /// 跳转到上一条 / 下一条用户提问。`direction` < 0 向上，> 0 向下。
    /// 锚点语义与 kelivo 一致：跳过一次之后箭头就跟着锚点走，不再受当前滚动位置影响，
    /// 连续按「上一条」能一排排往回翻。
    member this.JumpExchange(direction: int) =
        let cards = exchangeIndices ()
        if not (List.isEmpty cards) then
            let current =
                match exchangeAnchor with
                | Some key ->
                    mountedKeys
                    |> Array.tryFindIndex ((=) (Some key))
                    |> Option.defaultValue -1
                | None -> currentExchangeIndex ()
            let target =
                if direction < 0 then
                    cards
                    |> List.filter (fun index -> index < current)
                    |> List.tryLast
                else
                    cards |> List.tryFind (fun index -> index > current)
            match target with
            | Some index ->
                match mountedKeys.[index] with
                | Some key -> exchangeAnchor <- Some key
                | None -> ()
                if index < messagePanel.Children.Count then
                    smoothScrollToCard messagePanel.Children.[index]
                // 跳转离开底部：新回复不该把用户从正在看的历史里拽走。
                // atBottom 只在目标真的是底部时才保持，否则置假让「回到最新」接管。
                atBottom <- index = List.max cards
                unreadSinceScrolledUp <- 0
                this.UpdateScrollToBottomAppearance()
            | None -> ()
        // 顶按钮的可用态随时跟着收敛：跳转后可能已经到了端点。
        syncExchangeNavState ()

    member this.Build() =
        this.Background <- Tokens.canvas

        Ui.onClick prevExchangeButton (fun () -> this.JumpExchange -1)
        Ui.onClick nextExchangeButton (fun () -> this.JumpExchange 1)
        Ui.onClick titleAction (fun () -> this.BeginTitleEdit())
        titleAction.KeyDown.Add(fun e ->
            if (e.Key = Key.F2 || e.Key = Key.Enter) && titleEditable then
                e.Handled <- true
                this.BeginTitleEdit())
        titleAction.PointerEntered.Add(fun _ -> updateTitleActionVisual ())
        // 按压反馈与 Ui.attachSurfaceFeedback 同一节奏：瞬时 Opacity 脉冲（不进过渡集合），焦点/几何不变。
        titleAction.PointerPressed.Add(fun _ -> titleAction.Opacity <- Tokens.opacityPressed)
        titleAction.PointerReleased.Add(fun _ -> titleAction.Opacity <- 1.0)
        titleAction.PointerExited.Add(fun _ ->
            titleAction.Opacity <- 1.0
            updateTitleActionVisual ())
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
        Ui.setReservedActionVisible stopButton false
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
        // 从左到右按保留优先级排列：Stop > Session Settings > Fork > Prev/Next。
        // 问答跳转排最后：它不是每次生成都会用的动作，优先级低于停止与分叉。
        let rightGroup =
            Ui.hstack
                Tokens.space1
                [ stopButton :> Control
                  sessionSettingsButton :> Control
                  forkButton :> Control
                  prevExchangeButton :> Control
                  nextExchangeButton :> Control ]
        let headerDock = DockPanel(LastChildFill = true, VerticalAlignment = VerticalAlignment.Center)
        DockPanel.SetDock(rightGroup, Dock.Right)
        headerDock.Children.Add rightGroup
        headerDock.Children.Add leftGroup
        headerBar <-
            Border(
                Height = Tokens.barHeight,
                // 初始为最窄档（= horizontalInset 0.0 的取值），挂树后由
                // applyHorizontalInsets 与 scroller 同源取档，不再双写口径。
                Padding = Thickness(Tokens.shellInset, 0.0, Tokens.shellInset, 0.0),
                BorderBrush = Tokens.hairline,
                BorderThickness = Thickness(0.0, 0.0, 0.0, ControlMetrics.borderWidth),
                IsVisible = false,
                Child = headerDock)
        let header = headerBar


        // 让 Avalonia 的 ContentPresenter 完成 reading column 的收缩/拉伸；
        // `MaxWidth + Stretch` 取代手工监听 Viewport 后写 Width，避免 resize/scale 时
        // 维护第二套宽度同步逻辑。
        scroller.Content <- messagePanel
        // 水平留白按实时宽度取档：首调对齐当前档位，之后仅宽度跨断档时重写。
        // 订阅 ChatView 自身 Bounds（headerBar 已在前文赋值），一次刷新顶栏与滚动区。
        this.ApplyHorizontalInsets()
        this.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then this.ApplyHorizontalInsets())
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
            // 手动滚动会改变「当前是哪一轮」：没有锚点时锚点由视口定，
            // 所以端点禁用态必须跟着滚动走，只在渲染时刷一次会停在旧状态。
            syncExchangeNavState ()
            this.UpdateScrollToBottomAppearance()
            // 骨架可见时不预取更早历史：会话切换中的 extent 抖动会误触发，
            // 等真实内容挂载后再按正常阈值取。
            if offset <= 0.5 && extent > viewport && not skeletonPanel.IsVisible then actions.requestOlderHistory ())
        // 滚轮不论方向都是「用户此刻要动」：向上滚原本会打断平滑滚动，向下滚同理
        // （Avalonia 向下滚 Delta.Y 为负）。只截一半会让「上滚动画途中改主意往下滚」
        // 变成两个动画争抢 Offset——手势输了。
        scroller.PointerWheelChanged.Add(fun e ->
            if abs e.Delta.Y > 0.0 then
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
                skeletonPanel.Opacity <- Tokens.skeletonOpacityReduced
            smoothScrollTimer |> Option.iter (fun t -> t.Stop())
            smoothScrollTimer <- None
            motionSubscription |> Option.iter (fun s -> s.Dispose())
            motionSubscription <- None)
