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
open Avalonia.VisualTree
open Avalonia.LogicalTree
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
    /// 焦点归宿：瞬态控件（回到最新 / 停止键 / 生成收尾）隐藏后键盘用户的落点，
    /// 由 AppShell 注入输入区。Avalonia 隐藏控件会直接清焦点成 null，不会自动迁移。
    focusHome: unit -> unit
    message: MessageActions
}

/// 空对话的起手式：标签 → 填进输入框的开头。只给开头不给整句，用户补完再发（决策 222）。
module ChatStarters =
    let items =
        [ "解释概念", "用通俗的话解释："
          "总结要点", "帮我总结下面这段内容的要点：\n\n"
          "改写润色", "帮我改写得更清楚、更简洁：\n\n"
          "写代码", "写一段代码，实现：" ]

/// 空态在不同前提下要说不同的话，否则用户不知道下一步做什么。
type ChatEmptyState =
    /// 未连接（无保存凭据）
    | NotConnected
    /// 连接或认证失败，附失败原因
    | ConnectionFailed of reason: string
    /// 已连接但没有可用模型服务
    | NoProvider
    /// 有模型服务但没选对话
    | NoConversation
    /// 对话是空的
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
      missingTag: int
      /// 同一消息的版本切换控件（W4）：只影响带该控件的用户卡，其余恒为 0
      siblingTag: int
      /// 生成仍在进行、这条的工具调用还没有结果（等审批或执行中）
      awaitingTools: bool
      /// 等待批准的调用集合（只在 awaitingTools 时有意义）
      approvalTag: int }

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
    let titleEditShell, titleEditBox = Ui.textField "对话标题"
    let titleHost = Grid()
    let titleAction =
        ActionBorder(
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = Thickness ControlMetrics.borderWidth,
            CornerRadius = CornerRadius Spacing.Radius.sm,
            Height = Tokens.iconButton,
            MinHeight = Tokens.iconButton,
            MaxHeight = Tokens.iconButton,
            MinWidth = ControlMetrics.emptyStateTitleMinWidth,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = Thickness(Spacing.spaceXl, 0.0),
            Focusable = false,
            Child = titleText)

    let generatingChip =
        Border(
            Background = Tokens.accentFaint,
            CornerRadius = CornerRadius Spacing.Radius.pill,
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

    // 决策 240：顶栏常驻「当前模型」芯片——回答质量最直接的变量不该藏在输入区角落。
    // 点开是可搜索的快速切换；切换只影响之后的回复。
    let headerModelCaption =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontSmall,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.accent,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220.0)
    let headerModelChip =
        ActionBorder(
            Name = "HeaderModelChip",
            Background = Tokens.accentFaint,
            CornerRadius = CornerRadius Spacing.Radius.pill,
            Padding = Thickness(ControlMetrics.chipPaddingX * 1.5, ControlMetrics.chipPaddingY),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = true,
            IsVisible = false)
    let mutable openModelSwitcher: Control -> unit = ignore
    // 顶栏只交代“正在看哪段对话”。低频管理与阅读导航进入有文字的菜单。
    let moreButton = Ui.iconButton Icons.more "对话操作"
    let mutable showMore: Control -> unit = ignore
    let mutable awaitingApproval: Set<string> = Set.empty
    let mutable partialReply: (MessageView * (unit -> unit)) option = None
    let mutable lastFlashedCommit: uint64 option = None
    let mutable highlightedCommit: uint64 option = None
    let mutable highlightTimer: (DispatcherTimer * (unit -> unit)) option = None
    let mutable canJumpPrevious = false
    /// 决策 300：主机上还有更早的历史页（AppShell 按 pageHasMore 同步）。
    let mutable olderOnHost = false
    /// 决策 300：「上一轮」越过已加载历史时挂起的跳转，下一页渲染完再续跳。
    let mutable pendingJumpPrevious = false
    let mutable pendingJumpFirst: uint64 option = None
    let mutable canJumpNext = false
    let sidebarToggleButton = Ui.iconButton Icons.panelLeft "切换侧边栏（Ctrl / ⌘ + B）"

    // W4：分叉面包屑。只在“当前对话是某个对话的分叉”时出现；父对话在时可点击回到它，
    // 父对话已删除时是纯文字（分叉本身仍完整可用，决策 202）。
    let breadcrumbText =
        TextBlock(
            FontSize = Tokens.fontCaption,
            Foreground = Tokens.textMuted,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center)
    let breadcrumbAction =
        ActionBorder(
            Background = Brushes.Transparent,
            CornerRadius = CornerRadius Spacing.Radius.sm,
            Padding = Thickness(Spacing.spaceMd, Spacing.spaceXs),
            HorizontalAlignment = HorizontalAlignment.Left,
            Focusable = false)
    let breadcrumbBar =
        Border(
            Padding = Thickness(Tokens.shellInset, 0.0, Tokens.shellInset, Spacing.spaceXs),
            IsVisible = false)
    let mutable breadcrumbTarget: Guid option = None
    let mutable openBreadcrumbParent: Guid -> unit = ignore

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
            Padding = Thickness(Tokens.shellInset, Spacing.space3xl, Tokens.shellInset, 0.0))
    let emptyPanel =
        StackPanel(
            Orientation = Orientation.Vertical,
            Spacing = 0.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // reading column 内水平居中，留出下方呼吸留白形成微妙向上视差偏置 (optical bias)
            Margin = Thickness(Spacing.space2xl, 0.0, Spacing.space2xl, Spacing.space6xl),
            MaxWidth = ContentMetrics.emptyStateMaxWidth,
            IsVisible = false)
    let mutable currentPrimaryAction: unit -> unit = ignore
    let emptyActionButton =
        let btn = Ui.button Ui.Primary "" (fun () -> currentPrimaryAction ())
        btn.HorizontalAlignment <- HorizontalAlignment.Center
        btn.VerticalAlignment <- VerticalAlignment.Center
        btn.MinWidth <- ControlMetrics.emptyStateActionMinWidth
        btn.Height <- ControlMetrics.emptyStateActionHeight
        btn.CornerRadius <- CornerRadius Spacing.Radius.md
        btn.Padding <- Thickness(Spacing.space2xl, 0.0)
        btn.Focusable <- true
        btn.IsVisible <- false
        btn
    let mutable lastEmptyState: (ChatEmptyState * string option) option = None
    /// 决策 433：空态里的「从预设开始」。没有预设时整行不出现，首次使用的空态保持原样。
    let mutable presetStarters: (string * string * (unit -> unit)) list = []
    /// 决策 434：顶栏「⋯」里的「存为预设…」；未接线时不显示。
    let mutable savePresetAction: (unit -> unit) option = None
    let presetRow () : Control =
        let row = WrapPanel(Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center)
        for (name, detail, run) in presetStarters do
            let chip = Ui.button Ui.Secondary name run
            chip.Margin <- Thickness(Spacing.spaceXs)
            chip.Focusable <- true
            chip.Name <- "preset-chip"
            ToolTip.SetTip(chip, detail)
            Avalonia.Automation.AutomationProperties.SetName(chip, sprintf "套用预设 %s" name)
            Avalonia.Automation.AutomationProperties.SetHelpText(chip, detail)
            row.Children.Add chip
        let caption = Ui.caption "从预设开始"
        caption.HorizontalAlignment <- HorizontalAlignment.Center
        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceXs)
        column.Margin <- Thickness(0.0, Spacing.spaceXl, 0.0, 0.0)
        column.Children.Add caption
        column.Children.Add row
        column :> Control
    /// 空对话的起手式（决策 222）：点一下只把半句话填进输入框，不替用户发送。
    let mutable starterPick: (string -> unit) option = None
    let starterRow () : Control =
        let row = WrapPanel(Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center)
        row.Margin <- Thickness(0.0, Spacing.spaceXl, 0.0, 0.0)
        for (label, seed) in ChatStarters.items do
            let chip = Ui.button Ui.Secondary label (fun () -> starterPick |> Option.iter (fun pick -> pick seed))
            chip.Margin <- Thickness(Spacing.spaceXs)
            chip.Focusable <- true
            chip.Name <- "starter-chip"
            ToolTip.SetTip(chip, sprintf "填入「%s」" seed)
            Avalonia.Automation.AutomationProperties.SetName(chip, label)
            row.Children.Add chip
        row :> Control
    /// 决策 310：更早一页历史取不回来时的行内提示 + 重试（浮在消息区顶部，不进滚动内容，不改滚动锚点）。
    let olderFailureText =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontSmall,
            Foreground = Tokens.text,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center)
    let mutable olderRetry: unit -> unit = ignore
    let olderRetryButton = Ui.button Ui.Secondary "重试" (fun () -> olderRetry ())
    let olderFailureBanner =
        let row = Grid(ColumnSpacing = Spacing.spaceXl)
        row.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        row.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        Grid.SetColumn(olderFailureText, 0)
        Grid.SetColumn(olderRetryButton, 1)
        row.Children.Add olderFailureText
        row.Children.Add olderRetryButton
        let border =
            Border(
                Name = "older-page-failure",
                Background = Tokens.surfaceRaised,
                BorderBrush = Tokens.warning,
                BorderThickness = Thickness ControlMetrics.borderWidth,
                CornerRadius = CornerRadius Spacing.Radius.lg,
                Padding = Thickness(Spacing.space2xl, Spacing.spaceMd),
                Margin = Thickness(Spacing.space2xl, Spacing.spaceMd, Spacing.space2xl, 0.0),
                MaxWidth = Tokens.readingWidth,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                IsVisible = false,
                Child = row)
        border.BoxShadow <- Tokens.shadowPopup ()
        Avalonia.Automation.AutomationProperties.SetLiveSetting(olderFailureText, Avalonia.Automation.AutomationLiveSetting.Polite)
        Avalonia.Automation.AutomationProperties.SetName(olderRetryButton, "重试加载更早消息")
        border
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
    /// 决策 292：流式卡原位更新。外形（除正文外的一切：思考、工具调用、附件、字号、审批）不变时，
    /// 同一张卡留在面板上，只把新文字交给它的 StreamingMarkdown；外形变了才换代（streamGeneration + 1）。
    let mutable streamSlot: (obj * CardKey * StreamingMarkdown) option = None
    let mutable streamGeneration = 0
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
        let panel = StackPanel(Orientation = Orientation.Horizontal, Spacing = Spacing.spaceMd, VerticalAlignment = VerticalAlignment.Center)
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
    /// “回到最新”是一次明确的用户意图：滚动动画尚未完成时，ScrollChanged
    /// 看到的中途偏移不能把刚隐藏的按钮重新点亮。到达底部或用户重新滚动后解除。
    let mutable returningToLatest = false
    /// 当前「上一条 / 下一条」跳转锚点：用户消息卡的身份。
    /// None 表示还没跳转过，此时以当前所在的那一轮问答为基准（kelivo 的
    /// jump-to-question 语义；见 currentExchangeIndex）。
    let mutable exchangeAnchor: CardKey option = None
    /// 搜索命中跳转（W4）：目标消息已定位后，“贴底自动滚到末尾”的延迟重试不得把它拽走。
    /// 用户滚轮、回到最新、换对话时解除。
    let mutable revealPinned = false
    /// 决策 460：定位钉住的目标卡。钉住期间布局再变（分页前置、卡片晚量高、重绘）都按它的当前位置重新对齐，
    /// 而不是只在发起时量一次 Bounds——满载 CI 上量早了就会停在旧底部。
    let mutable revealTarget: Control option = None
    /// 平滑滚动动画最近一次写入的偏移。ScrollChanged 据此区分「动画自己往上走」和「用户往上滚」：
    /// 向上的动画（定位到更早的消息、回到顶部）每一帧都是负增量，若当成用户手势就会在第一帧把自己打断（v4 走查）。
    let mutable lastAnimatedOffset = nan
    let mutable headerBar: Border = Unchecked.defaultof<Border>
    let mutable pendingHistoryAnchor: (float * float) option = None
    /// 决策 293：打开长对话只挂尾部一个窗口的卡片（首屏只排这几十张），
    /// 往上滚到窗口顶再把已在本机的更早消息挂上来（与分页同一套 prepend 锚点），本机挂完了才向主机要更早一页。
    /// 搜索命中 / 消息链接指向窗口外的消息时，窗口直接扩到那条。
    let mutable mountWindow = ChatView.InitialMountWindow
    /// 本帧被窗口隐藏（已加载但未挂载）的消息：按顺序的 commitId
    let mutable hiddenCommits: uint64[] = [||]
    let mutable lastFirstCommit: uint64 option = None
    let mutable lastMergedCount = 0
    let mutable rerenderLast: unit -> unit = ignore
    let mutable compactMode = false
    let mutable hasConversationChrome = false
    let mutable isGenerating = false
    /// 菜单的停止项只在主机已确认生成身份、且连接允许操作时出现。
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

    /// 当前所在的那一轮问答 = 第一张「结尾已经越过视口顶端」的用户卡。
    /// 取视口顶端的第一张：用户在屏幕上方读第 1 轮时「下一条」就该送第 2 轮，
    /// 而不是拿视口底端那张当当前轮——后者会让大屏一次跳两三轮，方向感断掉。
    /// 判据与 kelivo _firstVisibleMessageBelowTopOverlay 同向：卡片底边越过视口顶端
    /// 才算它（scroll_controller.dart:805-817）。开场流式（没有已提交用户卡）时退回首张。
    let currentExchangeIndex () : int =
        let viewportTop = scroller.Offset.Y
        let cards = exchangeIndices ()
        cards
        |> List.filter (fun index ->
            index < messagePanel.Children.Count
            && messagePanel.Children.[index].Bounds.Y + messagePanel.Children.[index].Bounds.Height > viewportTop + 0.5)
        |> List.tryHead
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
        let targetOf () = max 0.0 (child.Bounds.Y - Spacing.space3xl)
        let target = targetOf ()
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
                // 每帧按卡片当前位置取目标：动画途中布局变了也落在卡片上（决策 460）。
                let target = targetOf ()
                if elapsed >= durationMs then
                    stopSmoothScroll ()
                    lastAnimatedOffset <- target
                    scroller.Offset <- Vector(scroller.Offset.X, target)
                else
                    let progress = min 1.0 (elapsed / durationMs)
                    let eased = MotionPolicy.easeOutCubic.Ease progress
                    let newY = startOffset + (target - startOffset) * eased
                    lastAnimatedOffset <- newY
                    scroller.Offset <- Vector(scroller.Offset.X, newY))
            smoothScrollTimer <- Some timer
            timer.Start()


    /// 阅读导航的端点状态。菜单与快捷键共用锚点，不再为不可用动作预留顶栏按钮。
    let syncExchangeNavState () =
        let cards = exchangeIndices ()
        let anchorIndex =
            match exchangeAnchor with
            | Some key -> mountedKeys |> Array.tryFindIndex ((=) (Some key)) |> Option.defaultValue -1
            | None -> currentExchangeIndex ()
        let hasPrev = cards |> List.exists (fun index -> index < anchorIndex)
        let hasNext = cards |> List.exists (fun index -> index > anchorIndex)
        // 贴底时没有「下一条」可去：锚点改取视口顶端第一张后，锚点本身可能
        // 不是最后一张（大屏一次露出数轮），端点禁用必须显式叠这条，否则贴底
        // 「下一条」仍可点，按下去只是原地跳一格再被自动跟随拽回底部。
        // 与 kelivo 的 hasNext 端点规则同向：target 越界即不可跳。
        let hasNext = hasNext && not atBottom
        // 决策 300：窗口外还有未挂载 / 主机上还有更早的消息时，「上一轮」永远可用——
        // 按下去先按需加载，再跳到真正的上一轮。
        let hasPrev = hasPrev || (not (List.isEmpty cards) && (hiddenCommits.Length > 0 || olderOnHost))
        canJumpPrevious <- hasPrev
        canJumpNext <- hasNext


    do
        generatingChip.Child <-
            // Streaming 文本本身已经持续变化；Header 只需要一个静态状态点，
            // 不再额外跑第二个 spinner 与正文竞争注意力 / UI thread。
            let stateDot =
                Border(
                    Width = ControlMetrics.chipDotSize,
                    Height = ControlMetrics.chipDotSize,
                    CornerRadius = CornerRadius Spacing.Radius.pill,
                    Background = Tokens.accent,
                    VerticalAlignment = VerticalAlignment.Center)
            let row = Ui.hstack Spacing.spaceMd [ stateDot :> Control; generatingCaption :> Control ]
            row
        titleEditShell.Height <- Tokens.iconButton
        titleEditShell.MinHeight <- Tokens.iconButton
        titleEditShell.MaxHeight <- Tokens.iconButton
        titleEditShell.MinWidth <- ControlMetrics.emptyStateTitleMinWidth
        titleEditShell.Padding <- Thickness(Spacing.spaceXl, 0.0)
        titleEditShell.CornerRadius <- CornerRadius Spacing.Radius.sm
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
            titleAction.BoxShadow <- BoxShadows(BoxShadow(Spread = Spacing.Stroke.thick, Color = Tokens.accent.Color))
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
            if titleEditable then sprintf "重命名对话：%s" text else text)
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
        scroller.Padding <- Thickness(inset, Spacing.space3xl, inset, 0.0)

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
        // 主机标题在用户改名途中到达（首答自动标题）时，不能丢弃输入框里的草稿：
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

    /// 停止的常驻入口属于输入区；顶栏只显示状态，不再预留第二个停止按钮。
    member this.SetGenerating(generating: bool, statusText: string, ?focusHome: unit -> unit) =
        isGenerating <- generating
        this.SyncGeneratingChipVisibility()
        generatingCaption.Text <- if String.IsNullOrWhiteSpace statusText then "生成中" else statusText

    member this.SetCanStop(value: bool) =
        canStop <- value

    member _.CanJumpPrevious = canJumpPrevious
    member _.CanJumpNext = canJumpNext

    member this.ConfigureMenu(overlay: OverlayHost, exportConversation: unit -> unit) =
        showMore <- fun anchor ->
            syncExchangeNavState ()
            let conversation =
                [ if titleEditable then
                      MenuEntry.create "重命名" (fun () -> this.BeginTitleEdit()) |> MenuEntry.withIcon Icons.pencil
                  MenuEntry.create "对话上下文" actions.openSessionSettings |> MenuEntry.withIcon Icons.sliders
                  MenuEntry.create "从此分叉" actions.forkFromHere |> MenuEntry.withIcon Icons.fork
                  match savePresetAction with
                  | Some save -> MenuEntry.create "存为预设…" save |> MenuEntry.withIcon Icons.sliders
                  | None -> ()
                  MenuEntry.create "导出（Markdown / JSON）…" exportConversation |> MenuEntry.withIcon Icons.download ]
            let reading =
                [ if canJumpPrevious then
                      MenuEntry.create "上一条提问" (fun () -> this.JumpExchange -1) |> MenuEntry.withIcon Icons.chevronsUp
                  if canJumpNext then
                      MenuEntry.create "下一条提问" (fun () -> this.JumpExchange 1) |> MenuEntry.withIcon Icons.chevronsDown
                  if messagePanel.Children.Count > 0 then
                      MenuEntry.create "回到对话开头" (fun () -> this.SmoothScrollToBeginning()) ]
                  // 决策 354：「回到最新」由右下角浮钮承担（离开底部才出现），菜单里不再重复一份。
            let groups =
                [ "当前对话", conversation
                  if not (List.isEmpty reading) then "阅读位置", reading
                  if isGenerating && canStop then
                      "执行", [ MenuEntry.create "停止生成" (fun () -> actions.stopGeneration (); actions.focusHome ()) |> MenuEntry.withIcon Icons.stop ] ]
            Menu.showGrouped overlay anchor false groups

    /// 对话状态变化时同步顶栏功能按钮的可用性。
    member this.SetConversationChrome(hasConversation: bool) =
        hasConversationChrome <- hasConversation
        moreButton.IsVisible <- hasConversation
        syncExchangeNavState ()
        if not (isNull (box headerBar)) then headerBar.IsVisible <- true


    /// 版本切换所需的谱系查询：(这条用户消息的 commitId, 它前一条消息的 commitId) → 同位置的全部版本。
    /// 返回 None 或不足 2 个版本时不画控件。默认无谱系：旧调用方行为不变。
    member val SiblingProvider: uint64 -> uint64 option -> SiblingVersions option = (fun _ _ -> None) with get, set
    /// 用户在版本控件上选定了另一个版本（对话 id）。
    member val SiblingSelected: Guid -> unit = ignore with get, set

    /// 顶栏模型芯片（决策 240）。model 为空时隐藏；providerLabel 进 tooltip 与读屏名。
    member _.SetHeaderModel(model: string, providerLabel: string, enabled: bool) =
        if String.IsNullOrWhiteSpace model then
            headerModelChip.IsVisible <- false
        else
            // 决策 350：模型只在输入框里选一处（离「发送」最近、决定下一条回复）。
            // 顶栏芯片与输入框芯片打开同一个切换器、显示同一个名字，两处同义入口只留一个。
            headerModelChip.IsVisible <- false
            headerModelCaption.Text <- model
            let full = if String.IsNullOrWhiteSpace providerLabel then model else sprintf "%s · %s" providerLabel model
            ToolTip.SetTip(headerModelChip, sprintf "当前模型：%s。点击切换，从下一条回复开始生效" full)
            Avalonia.Automation.AutomationProperties.SetName(headerModelChip, sprintf "当前模型 %s，切换模型" full)
            // 决策 303：芯片是按钮（读屏念「按钮」而不是一段文字），说明生效时机。
            Avalonia.Automation.AutomationProperties.SetRole(headerModelChip, AutomationRole.Button)
            Avalonia.Automation.AutomationProperties.SetHelpText(headerModelChip, "打开模型切换器，从下一条回复开始生效")
            Ui.setEnabled headerModelChip enabled

    /// 顶栏模型芯片点击后的去处（AppShell 注入）。
    member _.SetModelSwitcherHandler(handler: Control -> unit) = openModelSwitcher <- handler

    member _.HeaderModelAnchor : Control = headerModelChip :> Control
    member _.HeaderModelText : string = headerModelCaption.Text
    member _.HeaderModelVisible : bool = headerModelChip.IsVisible

    /// 设置（或清除）顶栏面包屑。crumb = None 时整条隐藏。
    member this.SetBreadcrumb(crumb: ForkBreadcrumb option, openParent: Guid -> unit) =
        openBreadcrumbParent <- openParent
        match crumb with
        | None ->
            breadcrumbTarget <- None
            breadcrumbBar.IsVisible <- false
        | Some c ->
            breadcrumbText.Text <- c.text
            breadcrumbTarget <- if c.parentAvailable then Some c.parentId else None
            breadcrumbAction.Focusable <- c.parentAvailable
            breadcrumbAction.Cursor <- if c.parentAvailable then new Cursor(StandardCursorType.Hand) else null
            ToolTip.SetTip(breadcrumbAction, c.description)
            Avalonia.Automation.AutomationProperties.SetName(breadcrumbAction, c.text)
            Avalonia.Automation.AutomationProperties.SetHelpText(breadcrumbAction, c.description)
            if c.parentAvailable then Avalonia.Automation.AutomationProperties.SetRole(breadcrumbAction, AutomationRole.Button)
            else Avalonia.Automation.AutomationProperties.SetControlTypeOverride(breadcrumbAction, Nullable Avalonia.Automation.Peers.AutomationControlType.Text)
            breadcrumbBar.IsVisible <- true

    /// 面包屑当前显示的文字（空串 = 不显示）。
    member _.BreadcrumbText: string = if breadcrumbBar.IsVisible then breadcrumbText.Text else ""

    /// 激活面包屑（回到父对话）；不可点击时什么也不做。返回是否发生了跳转。
    member _.ActivateBreadcrumb() : bool =
        match breadcrumbTarget with
        | Some id ->
            openBreadcrumbParent id
            true
        | None -> false

    /// 把指定消息滚到视口。卡片不在已加载窗口里（更早的历史）时返回 false，由调用方先加载更早历史。
    /// 最近一次被「闪一下」的消息（测试与无障碍：定位成功的可观察信号）。
    member _.LastFlashedCommit = lastFlashedCommit

    /// 滚到一条消息并短暂高亮（第 211 节）：搜索命中跳转后，眼睛要能立刻找到是哪一条。
    /// 背景淡色停留约 1.6 秒后复原；不改布局，不抢焦点。
    member this.RevealAndHighlight(commitId: uint64) : bool =
        if this.ScrollToMessage commitId then
            lastFlashedCommit <- Some commitId
            Dispatcher.UIThread.Post(
                (fun () ->
                    let index = mountedKeys |> Array.tryFindIndex (function Some k -> k.commit = commitId | None -> false)
                    match index with
                    | Some i when i < messagePanel.Children.Count ->
                        // 卡片本身是 StackPanel（或带版本条的竖排），背景刷不出圆角面：
                        // 找 MessageCard 外包的高亮承载面（第 211 节）。
                        let surface =
                            match messagePanel.Children.[i] with
                            | :? Border as b when b.Name = MessageCard.revealHighlightName -> Some b
                            | card ->
                                card.GetVisualDescendants()
                                |> Seq.tryPick (function
                                    | :? Border as b when b.Name = MessageCard.revealHighlightName -> Some b
                                    | _ -> None)
                                |> Option.orElse (
                                    card.GetLogicalDescendants()
                                    |> Seq.tryPick (function
                                        | :? Border as b when b.Name = MessageCard.revealHighlightName -> Some b
                                        | _ -> None))
                        match surface with
                        | Some b ->
                            highlightTimer |> Option.iter (fun (t: DispatcherTimer, restore) -> t.Stop(); restore ())
                            b.Background <- Tokens.accentSoft
                            highlightedCommit <- Some commitId
                            let restore () =
                                b.Background <- Brushes.Transparent
                                highlightedCommit <- None
                                highlightTimer <- None
                            let timer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 1600.0)
                            timer.Tick.Add(fun _ ->
                                timer.Stop()
                                restore ())
                            highlightTimer <- Some(timer, restore)
                            timer.Start()
                        | None -> ()
                    | _ -> ()),
                DispatcherPriority.Background)
            true
        else false

    /// 视口是否被定位钉住（搜索命中 / 版本切换后，直到用户滚动或回到底部）。
    member _.RevealPinned = revealPinned

    /// 当前正高亮着的消息（高亮结束后为 None）。
    member _.HighlightedCommit = highlightedCommit

    /// 立即结束定位高亮（计时器到点时走的同一路径；无显示测试里 DispatcherTimer 不可确定泵出）。
    member _.EndRevealHighlight() =
        highlightTimer |> Option.iter (fun (t: DispatcherTimer, restore) -> t.Stop(); restore ())

    member this.ScrollToMessage(commitId: uint64) : bool =
        this.EnsureMounted commitId
        let indexOf () =
            mountedKeys |> Array.tryFindIndex (function Some k -> k.commit = commitId | None -> false)
        match indexOf () with
        | Some _ ->
            revealPinned <- true
            atBottom <- false
            // 分页前置的锚点恢复会把偏移写回旧位置，这里的目标位置优先
            pendingHistoryAnchor <- None
            this.UpdateScrollToBottomAppearance()
            // 卡片刚挂上时 Bounds 还没量好：等一次布局再量（按身份重新找，期间卡片序可能变）。
            Dispatcher.UIThread.Post(
                (fun () ->
                    match indexOf () with
                    | Some index when index < messagePanel.Children.Count ->
                        let child = messagePanel.Children.[index]
                        if revealPinned then revealTarget <- Some child
                        smoothScrollToCard child
                    | _ -> ()),
                DispatcherPriority.Background)
            true
        | None -> false

    member this.SetCompactMode(value: bool) =
        compactMode <- value
        let size = if value then LayoutPolicy.compactActionTarget else Tokens.iconButton
        for button in [ sidebarToggleButton; moreButton; scrollToBottomButton ] do
            Ui.setSquareTarget button size
        moreButton.IsVisible <- hasConversationChrome
        // 手机窄屏：芯片收窄但不收起——标题可以截断，当前模型不能看不见。
        headerModelCaption.MaxWidth <- if value then 120.0 else 220.0
        headerModelChip.MinHeight <- if value then LayoutPolicy.compactActionTarget else 0.0
        this.UpdateScrollToBottomAppearance()
        this.SyncGeneratingChipVisibility()

    /// 空态标题靠近输入区；只改布局，不移除或重挂输入控件。
    member _.SetStartMode(value: bool) =
        emptyPanel.VerticalAlignment <- if value then VerticalAlignment.Bottom else VerticalAlignment.Center
        emptyPanel.Margin <- Thickness(Spacing.space2xl, 0.0, Spacing.space2xl, Spacing.space2xl)

    /// 起手式的去处（通常是 Composer.SetText）；不设则空态不显示起手式。
    member _.SetStarterHandler(pick: string -> unit) =
        starterPick <- Some pick
        lastEmptyState <- None

    /// 决策 433：空态的预设芯片（名称, 说明, 套用）。列表变化时下次 ShowEmpty 重建。
    member _.SetPresetStarters(items: (string * string * (unit -> unit)) list) =
        let names l = l |> List.map (fun (n, d, _) -> n, d)
        if names items <> names presetStarters then lastEmptyState <- None
        presetStarters <- items

    member _.PresetStarterCount = presetStarters.Length

    member _.SetSavePresetAction(action: (unit -> unit) option) = savePresetAction <- action

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
                | NotConnected -> "先连接一台万象主机", "主机是运行万象的那台电脑：模型、密钥和对话都在它上面。"
                | ConnectionFailed reason -> "没能连上主机", sprintf "%s。请确认主机在运行、网络可达，然后重新连接。" reason
                | NoProvider -> "还没有可用的模型", "添加一个模型服务并填入密钥，就可以开始对话了。"
                | NoConversation -> "今天想讨论什么？", "发出第一条消息后，会自动建立对话。"
                | EmptyConversation -> "开始这段对话", "输入问题，或附上一份文件。"
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
            emptyPanel.Children.Clear()
            match state with
            | NoConversation | EmptyConversation ->
                emptyPanel.Children.Add(
                    Ui.conversationPrompt title hint (onPrimary |> Option.map (fun _ -> emptyActionButton)))
                if not (List.isEmpty presetStarters) then emptyPanel.Children.Add(presetRow ())
                if starterPick.IsSome then emptyPanel.Children.Add(starterRow ())
            | NotConnected | ConnectionFailed _ | NoProvider ->
                let logo = brandLogo Tokens.logoEmpty
                logo.HorizontalAlignment <- HorizontalAlignment.Center
                emptyPanel.Children.Add(
                    Ui.emptyStateWith true logo (Some title) hint None (Some emptyActionButton))
        emptyPanel.HorizontalAlignment <- HorizontalAlignment.Center
        emptyPanel.IsVisible <- true
        scroller.IsVisible <- false
        this.HideSkeletonLoading()
        messagePanel.IsVisible <- false

    /// 在空态位置放一块自带交互的内容（首次运行的「连接一个模型」卡片）。
    /// 只换 emptyPanel 的子项，不动滚动区与输入控件。
    member this.ShowEmbeddedEmpty(content: Control) =
        lastEmptyState <- None
        currentPrimaryAction <- ignore
        emptyActionButton.IsVisible <- false
        let alreadyHosted = emptyPanel.Children.Count = 1 && obj.ReferenceEquals(emptyPanel.Children[0], content)
        if not alreadyHosted then
            match content.Parent with
            | :? Panel as panel -> panel.Children.Remove content |> ignore
            | _ -> ()
            emptyPanel.Children.Clear()
            emptyPanel.Children.Add content
        // 内嵌卡片（首次连接）按列宽拉伸到 MaxWidth：Center 让宽度跟内容走，
        // 出现一行错误提示整张卡就变宽。
        emptyPanel.HorizontalAlignment <- HorizontalAlignment.Stretch
        emptyPanel.IsVisible <- true
        scroller.IsVisible <- false
        this.HideSkeletonLoading()
        messagePanel.IsVisible <- false

    member this.HideEmpty() =
        lastEmptyState <- None
        currentPrimaryAction <- ignore
        emptyActionButton.IsVisible <- false
        // 每次重绘都会经过这里：定位（搜索命中 / 版本切换）刚把视口钉在目标消息上时不能复位，
        // 否则紧随其后的重绘把 atBottom 改回 true、停掉滚动动画，视口又被拉回底部（v4 走查）。
        // 换对话由 AppShell.SelectConversation 显式复位。
        if not revealPinned then this.ResetScrollState()
        emptyPanel.IsVisible <- false
        scroller.IsVisible <- true
        messagePanel.IsVisible <- true

    /// 重置滚动到底状态与未读计数（换对话、加载骨架或切换视图时调用）。
    /// 决策 293：首屏挂载窗口与每次向上扩展的步长
    static member InitialMountWindow = 40
    static member MountWindowStep = 60

    /// 决策 300：主机上是否还有更早的历史页。没有了就撤掉挂起的「上一轮」。
    member _.SetOlderAvailable(value: bool) =
        olderOnHost <- value
        if not value then pendingJumpPrevious <- false
        syncExchangeNavState ()

    member _.PendingJumpPrevious = pendingJumpPrevious

    /// 决策 310：更早一页取失败（超时 / 断线 / 主机拒绝）。挂起的「上一轮」撤掉，
    /// 消息区顶部给一句原因和「重试」；重试由调用方重新发页请求。
    member _.ShowOlderPageFailure(message: string, retry: unit -> unit) =
        pendingJumpPrevious <- false
        olderRetry <- (fun () ->
            olderFailureBanner.IsVisible <- false
            retry ())
        olderFailureText.Text <- message
        Avalonia.Automation.AutomationProperties.SetName(olderFailureText, message)
        olderFailureBanner.IsVisible <- true
        syncExchangeNavState ()

    member _.HideOlderPageFailure() = olderFailureBanner.IsVisible <- false
    member _.OlderPageFailureVisible = olderFailureBanner.IsVisible
    member _.OlderPageFailureText = olderFailureText.Text
    member _.OlderPageRetryButton = olderRetryButton

    /// 当前因窗口未挂载的已加载消息数（测试 / 诊断用）
    member _.HiddenMessageCount = hiddenCommits.Length

    /// 把窗口向上扩一步（已加载、未挂载的消息先挂）；返回是否真的扩了。
    member this.ExpandMountWindow() : bool =
        if hiddenCommits.Length = 0 then false
        else
            this.BeginHistoryPrependAnchor()
            mountWindow <- mountWindow + ChatView.MountWindowStep
            rerenderLast ()
            this.RestoreHistoryPrependAnchorDeferred()
            true

    /// 确保某条消息已挂载（窗口外则扩到它）。
    member private this.EnsureMounted(commitId: uint64) =
        match Array.tryFindIndex ((=) commitId) hiddenCommits with
        | Some index ->
            mountWindow <- mountWindow + (hiddenCommits.Length - index) + 4
            rerenderLast ()
        | None -> ()

    member this.ResetScrollState() =
        mountWindow <- ChatView.InitialMountWindow
        revealPinned <- false
        revealTarget <- None
        smoothScrollTimer |> Option.iter (fun t -> t.Stop())
        smoothScrollTimer <- None
        atBottom <- true
        returningToLatest <- false
        unreadSinceScrolledUp <- 0
        previousRenderedMessageCount <- 0
        // 锚点跟着对话一起丢：换对话后旧锚引用的卡片已不在面板上。
        exchangeAnchor <- None
        pendingJumpPrevious <- false
        olderFailureBanner.IsVisible <- false
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
                        CornerRadius = CornerRadius Spacing.Radius.sm,
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
                        CornerRadius = CornerRadius Spacing.Radius.pill,
                        Background = Tokens.borderSoft,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = Thickness(0.0, 0.0, Spacing.spaceXl, 0.0),
                        Opacity = Tokens.skeletonOpacityReduced)
                let lines = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceMd, HorizontalAlignment = HorizontalAlignment.Stretch)
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
                        CornerRadius = CornerRadius(Spacing.Radius.lg, Spacing.Radius.lg, Spacing.Radius.sm, Spacing.Radius.lg),
                        Width = 260.0,
                        Height = 40.0,
                        Opacity = Tokens.skeletonOpacityBreathMin,
                        HorizontalAlignment = HorizontalAlignment.Right)
                let avatar =
                    Border(
                        Width = ControlMetrics.skeletonAvatarSize,
                        Height = ControlMetrics.skeletonAvatarSize,
                        CornerRadius = CornerRadius Spacing.Radius.pill,
                        Background = Tokens.borderSoft,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = Thickness 0.0,
                        Opacity = Tokens.skeletonOpacityBreathMin)
                let stack = StackPanel(Orientation = Orientation.Horizontal, Spacing = Spacing.spaceXl, HorizontalAlignment = HorizontalAlignment.Right)
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

    /// 显示骨架屏（换对话或初次加载中，替代空白屏与“加载中…”）
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
    /// 在用户消息卡下方加 `‹ n/m ›` 版本切换条（同一条消息的原文与各分叉）。
    /// 条在卡外：卡片自身的内容、右键菜单与键盘行为不变。
    member private this.WithSiblingBar(card: Control, s: SiblingVersions) : Control =
        let go (delta: int) =
            let target = s.index + delta
            if target >= 0 && target < s.count then this.SiblingSelected(List.item target s.targets)
        let previous = Ui.iconButton Icons.chevronLeft "上一个版本"
        let next = Ui.iconButton Icons.chevronRight "下一个版本"
        previous.IsEnabled <- s.index > 0
        next.IsEnabled <- s.index < s.count - 1
        Ui.onClick previous (fun () -> go -1)
        Ui.onClick next (fun () -> go 1)
        let label =
            TextBlock(
                Text = sprintf "%d/%d" (s.index + 1) s.count,
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                VerticalAlignment = VerticalAlignment.Center)
        let name = sprintf "这条消息的版本 %d / %d" (s.index + 1) s.count
        Avalonia.Automation.AutomationProperties.SetName(label, name)
        ToolTip.SetTip(label, "同一条消息编辑后产生的版本（每个版本是一个分叉）")
        let bar = Ui.hstack Spacing.spaceXs [ previous :> Control; label :> Control; next :> Control ]
        bar.HorizontalAlignment <- HorizontalAlignment.Right
        Avalonia.Automation.AutomationProperties.SetName(bar, name)
        Ui.vstack Spacing.spaceXs [ card; bar :> Control ] :> Control

    /// 主机发出 / 撤回审批请求时更新：对应工具调用显示「等待批准」（第 206 节）。
    member _.SetAwaitingApproval(callIds: Set<string>) = awaitingApproval <- callIds
    member _.AwaitingApproval = awaitingApproval

    /// 决策 230：中途失败留下的半截回复与「继续生成」动作；None 表示没有。
    member _.SetPartialReply(partial: (MessageView * (unit -> unit)) option) = partialReply <- partial
    member _.PartialReply = partialReply

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
              awaitingApproval = awaitingApproval; brandAvatar = fun () -> brandLogo Tokens.logoAvatar }
        // 先列出这一帧想要的卡片：已提交的带身份（可复用），
        // 流式卡与错误卡没有身份（每帧必然变，本来就该重建）
        let desired = ResizeArray<CardKey option * (unit -> Control)>()
        // 同一消息的版本（W4）：前一条消息按原始顺序取，不用合并后的列表
        // （工具结果会被折进调用消息，前一条身份会错位）。
        let previousCommit =
            let map = Dictionary<uint64, uint64 option>()
            let mutable last: uint64 option = None
            for m in messages do
                match m.commitId with
                | Some c ->
                    map[c] <- last
                    last <- Some c
                | None -> ()
            map
        let siblingsOf (message: MessageView) : SiblingVersions option =
            match message.commitId with
            | Some commit when MessageView.isUser message ->
                let prev = match previousCommit.TryGetValue commit with | true, p -> p | _ -> None
                this.SiblingProvider commit prev
            | _ -> None

        // 决策 293：只挂尾部窗口。分页回来的更早一页（prepend 锚点在途、首条变了）整页挂上——
        // 那是用户滚到顶要看的内容。
        let mergedCount = List.length merged
        let firstCommit = merged |> List.tryHead |> Option.bind (fun m -> m.commitId)
        if pendingHistoryAnchor.IsSome && firstCommit <> lastFirstCommit && mergedCount > lastMergedCount && lastMergedCount > 0 then
            mountWindow <- max mountWindow (min mergedCount (lastMergedCount - hiddenCommits.Length + (mergedCount - lastMergedCount)))
        lastFirstCommit <- firstCommit
        lastMergedCount <- mergedCount
        let firstMounted = max 0 (mergedCount - mountWindow)
        hiddenCommits <- merged |> List.truncate firstMounted |> List.choose (fun m -> m.commitId) |> Array.ofList
        rerenderLast <- fun () ->
            this.RenderMessages(messages, streamingMessage, error, fontSize, autoCollapseReasoning, usage, missingAttachments)
        merged
        |> List.iteri (fun index message ->
            if index >= firstMounted && MessageView.hasVisibleBody message then
                // 生成还在进行（例如等工具审批）时，最后一条助手消息里没有结果的工具调用是「执行中」，
                // 不是「结果未知」——后者只该在生成结束后出现（视觉走查 11 号截图）。
                let awaitingTools =
                    isGenerating
                    && streamingMessage.IsNone
                    && lastAssistantIndex = Some index
                    && message.toolCalls |> List.exists (fun c -> c.result.IsNone)
                let ctx = ctxFor index awaitingTools
                let siblings = siblingsOf message
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
                            (if List.isEmpty message.attachments then 0 else hash ctx.missingAttachments)
                          siblingTag = (match siblings with Some s -> hash (s.index, s.count, s.targets) | None -> 0)
                          awaitingTools = awaitingTools
                          approvalTag = (if awaitingTools then hash awaitingApproval else 0) })
                desired.Add(
                    key,
                    fun () ->
                        let card = MessageCard.render message ctx actions.message (Some index)
                        match siblings with
                        | Some s -> this.WithSiblingBar(card, s)
                        | None -> card))

        match streamingMessage with
        | Some streaming when MessageView.hasVisibleBody streaming ->
            let ctx = ctxFor -1 true
            let shape =
                box ({ streaming with text = "" }, String.IsNullOrWhiteSpace streaming.text, fontSize, autoCollapseReasoning, awaitingApproval)
            match streamSlot with
            | Some(prevShape, key, live) when prevShape = shape ->
                live.Update streaming.text
                desired.Add(Some key, fun () -> MessageCard.render streaming ctx actions.message None)
            | _ ->
                streamGeneration <- streamGeneration + 1
                let key =
                    { commit = UInt64.MaxValue
                      role = "streaming"
                      toolCalls = 0
                      toolResults = 0
                      attachments = 0
                      fontSize = fontSize
                      collapse = autoCollapseReasoning
                      lastAssistant = false
                      usageTag = 0
                      missingTag = 0
                      siblingTag = streamGeneration
                      awaitingTools = false
                      approvalTag = 0 }
                let card, live = MessageCard.renderWithStream streaming ctx actions.message None
                cardCache[key] <- card
                streamSlot <- live |> Option.map (fun l -> shape, key, l)
                desired.Add(Some key, fun () -> card)
        | _ -> streamSlot <- None
        let partialNow =
            match error with
            | Some _ -> partialReply |> Option.filter (fun (p, _) -> streamingMessage.IsNone && MessageView.hasVisibleBody p)
            | None -> None
        match partialNow with
        | Some(partial, _) ->
            desired.Add(None, fun () ->
                let card = MessageCard.render partial (ctxFor -1 false) actions.message None
                Ui.vstack Spacing.spaceXs [ card; MessageCard.incompleteMarker () ] :> Control)
        | None -> ()
        match error with
        | Some(err, retry) ->
            desired.Add(None, fun () -> MessageCard.errorCardWith err retry None (partialNow |> Option.map snd))
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
        if pendingJumpPrevious && lastFirstCommit <> pendingJumpFirst then
            pendingJumpPrevious <- false
            // 等分页锚点恢复（Background）之后再跳，免得被写回旧偏移。
            Dispatcher.UIThread.Post((fun () -> this.JumpExchange -1), DispatcherPriority.ContextIdle)

        // 缓存只保留这一帧用到的身份：换对话、改字号都会让旧身份失效，
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
            // 窗口外未挂载的消息也算条数：否则超过挂载窗口后尾部新增 delta 恒为 0（决策 293）。
            let newCount = desired.Count + firstMounted
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
        previousRenderedMessageCount <- desired.Count + firstMounted

    member private this.UpdateScrollToBottomAppearance() =
        let extent = scroller.Extent.Height
        let viewport = scroller.Viewport.Height
        scrollToBottomButton.Margin <- Thickness(0.0, 0.0, Spacing.space3xl, Spacing.space2xl)
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
                    scrollToBottomButton.Padding <- Thickness(Spacing.spaceXl, 0.0, Spacing.space2xl, 0.0)
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
                    if not revealPinned then scroller.ScrollToEnd()
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
        returningToLatest <- false
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
                        lastAnimatedOffset <- newY
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

    /// 用户意图入口：回到对话开头。
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
        revealPinned <- false
        revealTarget <- None
        smoothScrollTimer |> Option.iter (fun t -> t.Stop())
        smoothScrollTimer <- None
        returningToLatest <- true
        atBottom <- true
        unreadSinceScrolledUp <- 0
        this.UpdateScrollToBottomAppearance()
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
                        lastAnimatedOffset <- newY
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
            | None when direction < 0 && hiddenCommits.Length > 0 ->
                // 决策 300：本机已加载、只是没挂载——先挂上一步再续跳（锚点不变，索引随之后移）。
                if exchangeAnchor.IsNone && current >= 0 then exchangeAnchor <- mountedKeys.[current]
                if this.ExpandMountWindow() then this.JumpExchange direction
            | None when direction < 0 && olderOnHost ->
                if exchangeAnchor.IsNone && current >= 0 then exchangeAnchor <- mountedKeys.[current]
                pendingJumpPrevious <- true
                pendingJumpFirst <- lastFirstCommit
                actions.requestOlderHistory ()
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

        Ui.onClick moreButton (fun () -> showMore moreButton)
        Ui.onClick titleAction (fun () -> this.BeginTitleEdit())
        titleAction.KeyDown.Add(fun e ->
            if (e.Key = Key.F2 || e.Key = Key.Enter) && titleEditable then
                e.Handled <- true
                this.BeginTitleEdit())
        // hover / press / focus 的底色与不透明度交给 Interaction 的原生伪类
        // （`:pointerover` / `:pressed` / `:focus-visible`）；这里只登记表面。
        Interaction.surface titleAction Interaction.HoverVariant.plain |> ignore
        Interaction.reportFocusOrigin titleAction
        updateTitleActionVisual ()
        titleEditBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                this.CommitTitle true
            elif e.Key = Key.Escape then
                e.Handled <- true
                this.CancelTitleEdit())
        // 焦点环（BoxShadow）交给 Interaction 的 `:focus-within` 伪类——输入框
        // 聚焦时壳（titleEditShell）才命中，输入框自己不可聚焦。描边色是值，
        // 仍由事件写。
        Interaction.surface titleEditShell Interaction.HoverVariant.bordered |> ignore
        titleEditBox.GotFocus.Add(fun _ -> titleEditShell.BorderBrush <- Tokens.accent)
        titleEditBox.LostFocus.Add(fun _ ->
            titleEditShell.BorderBrush <- Tokens.border
            if editingTitle then
                let top = TopLevel.GetTopLevel(this)
                let newFocus = if isNull top || isNull top.FocusManager then null else top.FocusManager.GetFocusedElement()
                let shouldRestoreFocus = isNull newFocus || obj.ReferenceEquals(newFocus, top) || obj.ReferenceEquals(newFocus, this)
                this.CommitTitle shouldRestoreFocus)
        titleEditShell.PointerEntered.Add(fun _ ->
            if not titleEditBox.IsFocused then titleEditShell.BorderBrush <- Tokens.line)
        titleEditShell.PointerExited.Add(fun _ ->
            if not titleEditBox.IsFocused then titleEditShell.BorderBrush <- Tokens.border)

        Ui.onClick sidebarToggleButton (fun () -> actions.toggleSidebar ())
        moreButton.IsVisible <- false

        // 顶栏左组不能用横向 StackPanel：它会用无限宽测量标题，长标题不会真正
        // 进入 ellipsis，而是去挤右侧动作。Grid 把标题放进唯一可 shrink 的 Star 列。
        let leftGroup = Grid(ColumnSpacing = Spacing.spaceMd, VerticalAlignment = VerticalAlignment.Center)
        leftGroup.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        leftGroup.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        leftGroup.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        leftGroup.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        headerModelChip.Child <-
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Spacing.spaceXs, VerticalAlignment = VerticalAlignment.Center)
            let spark = Icons.chevronDown Tokens.accent
            spark.VerticalAlignment <- VerticalAlignment.Center
            row.Children.Add headerModelCaption
            row.Children.Add spark
            row
        Interaction.surface headerModelChip Interaction.HoverVariant.plain |> ignore
        Interaction.reportFocusOrigin headerModelChip
        Ui.onClick headerModelChip (fun () -> openModelSwitcher (headerModelChip :> Control))
        Grid.SetColumn(headerModelChip, 3)
        leftGroup.Children.Add headerModelChip
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
        let rightGroup = Ui.hstack Spacing.spaceXs [ moreButton :> Control ]
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
            // 决策 460：钉住期间内容高度变了（不是用户在滚）→ 把目标卡重新对齐到顶部。
            // 动画进行中由动画每帧自己对齐，这里只管动画结束之后的布局漂移。
            match revealTarget with
            | Some child when revealPinned && smoothScrollTimer.IsNone && abs e.ExtentDelta.Y > 0.1 ->
                if isNull child.Parent then revealTarget <- None
                else
                    let target = max 0.0 (child.Bounds.Y - Spacing.space3xl)
                    if abs (scroller.Offset.Y - target) > 1.0 then
                        lastAnimatedOffset <- target
                        scroller.Offset <- Vector(scroller.Offset.X, target)
            | _ -> ()
            let extent = scroller.Extent.Height
            let viewport = scroller.Viewport.Height
            let offset = scroller.Offset.Y
            // 用户手动向上滚动（或滚轮/触控向上拉），立即打断平滑自动滚动，不与用户手势冲突
            let fromAnimation = abs (offset - lastAnimatedOffset) < 1.0
            if e.OffsetDelta.Y < -0.1 && not fromAnimation then
                returningToLatest <- false
                smoothScrollTimer |> Option.iter (fun t -> t.Stop())
                smoothScrollTimer <- None
            let nowAtBottom = extent - viewport - offset < ContentMetrics.scrollBottomThreshold
            if nowAtBottom then returningToLatest <- false
            // 中途帧仍按用户已发出的“回到底”意图处理，不重新显示按钮。
            atBottom <- nowAtBottom || returningToLatest
            if nowAtBottom then
                unreadSinceScrolledUp <- 0
                scrollToBottomButton.IsVisible <- false
            // 手动滚动会改变「当前是哪一轮」：没有锚点时锚点由视口定，
            // 所以端点禁用态必须跟着滚动走，只在渲染时刷一次会停在旧状态。
            syncExchangeNavState ()
            this.UpdateScrollToBottomAppearance()
            // 骨架可见时不预取更早历史：对话切换中的 extent 抖动会误触发，
            // 等真实内容挂载后再按正常阈值取。
            if offset <= 0.5 && extent > viewport && not skeletonPanel.IsVisible then
                // 决策 293：本机还有没挂的更早消息时先挂它们；只认「用户把视口往上推到了顶」
                // （本帧偏移在减小），首屏布局时偏移本来就是 0，不能因此把整段历史一次挂满。
                if hiddenCommits.Length > 0 then
                    if e.OffsetDelta.Y < -0.1 then this.ExpandMountWindow() |> ignore
                // 决策 310：上一页刚失败时不靠滚动静默重发——由行内「重试」显式触发。
                elif not olderFailureBanner.IsVisible then actions.requestOlderHistory ())
        // 滚轮不论方向都是「用户此刻要动」：向上滚原本会打断平滑滚动，向下滚同理
        // （Avalonia 向下滚 Delta.Y 为负）。只截一半会让「上滚动画途中改主意往下滚」
        // 变成两个动画争抢 Offset——手势输了。
        scroller.PointerWheelChanged.Add(fun e ->
            if abs e.Delta.Y > 0.0 then
                revealPinned <- false
                revealTarget <- None
                returningToLatest <- false
                smoothScrollTimer |> Option.iter (fun t -> t.Stop())
                smoothScrollTimer <- None)

        scrollToBottomButton.Child <- scrollToBottomContent
        scrollToBottomLabel.IsVisible <- false
        scrollToBottomButton.BoxShadow <- Tokens.shadowPopup ()
        scrollToBottomButton.IsVisible <- false
        scrollToBottomButton.HorizontalAlignment <- HorizontalAlignment.Right
        scrollToBottomButton.VerticalAlignment <- VerticalAlignment.Bottom
        scrollToBottomButton.Margin <- Thickness(0.0, 0.0, Spacing.space3xl, Spacing.space2xl)
        let doScrollToBottom () =
            // 键盘激活这条路径：按钮随即因 atBottom 隐藏，而 Avalonia 不会自动
            // 迁走焦点——停在按钮上的焦点直接被清成 null（探针实测），用户接着
            // 按 Tab 会从窗口根部重新走。归宿取消息面板：用户刚才是在读消息，
            // 视线要落回内容而不是被拽到输入区（与停止键 focusHome 分场景）。
            let focusWasOnButton = scrollToBottomButton.IsFocused
            atBottom <- true
            unreadSinceScrolledUp <- 0
            this.UpdateScrollToBottomAppearance()
            this.SmoothScrollToEnd()
            if focusWasOnButton then actions.focusHome ()
        Ui.onClick scrollToBottomButton doScrollToBottom

        let body = Grid()
        body.Children.Add scroller
        // 空态/首次连接卡放进自己的滚动区：矮窗口里卡片比可用高度高时可以滚动，
        // 而不是溢出到输入区上。可见性跟随 emptyPanel，隐藏时不挡消息列表的命中。
        let emptyScroller =
            ScrollViewer(
                Content = emptyPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                IsVisible = emptyPanel.IsVisible)
        emptyPanel.PropertyChanged.Add(fun e ->
            if e.Property = Visual.IsVisibleProperty then emptyScroller.IsVisible <- emptyPanel.IsVisible)
        body.Children.Add emptyScroller
        body.Children.Add scrollToBottomButton
        body.Children.Add olderFailureBanner

        breadcrumbAction.Child <-
            let icon = Icons.fork Tokens.textMuted
            icon.Width <- Tokens.iconGlyph
            icon.Height <- Tokens.iconGlyph
            icon.VerticalAlignment <- VerticalAlignment.Center
            Ui.hstack Spacing.spaceMd [ icon; breadcrumbText :> Control ]
        Interaction.surface breadcrumbAction Interaction.HoverVariant.plain |> ignore
        Interaction.reportFocusOrigin breadcrumbAction
        Ui.onClick breadcrumbAction (fun () -> this.ActivateBreadcrumb() |> ignore)
        breadcrumbBar.Child <- breadcrumbAction
        let layout = DockPanel()
        // 面包屑放进头部容器（与标题共用水平留白档位）：DockPanel 子项仍是 [顶部; 主体]，既有布局契约不变。
        let top = StackPanel()
        let headerContent = header.Child
        header.Child <- null
        top.Children.Add headerContent
        top.Children.Add breadcrumbBar
        header.Child <- top
        DockPanel.SetDock(header, Dock.Top)
        layout.Children.Add header
        layout.Children.Add body
        this.Child <- layout

        this.AttachedToVisualTree.Add(fun _ ->
            ParityProbe.hit "lifecycle.chatAttached"
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
