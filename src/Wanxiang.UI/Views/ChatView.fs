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
    message: Wanxiang2.MessageCardView.CardActions
}

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
    /// 定位高亮的记账归 RevealFlash；计时器与承载面仍在这里。
    let flash = RevealFlash()
    let mutable highlightTimer: (DispatcherTimer * (unit -> unit)) option = None
    /// 决策 300：主机上还有更早的历史页（AppShell 按 pageHasMore 同步）。
    /// 决策 300：「上一轮」越过已加载历史时挂起的跳转，下一页渲染完再续跳。
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
    let breadcrumb = BreadcrumbAction<Guid>()
    let mutable openBreadcrumbParent: Guid -> unit = ignore

    /// 滚动区、空态、骨架、问答跳转、贴底与「回到最新」归 Wanxiang2.ChatPane；消息卡放进它的 Messages 插槽。
    let pane = Wanxiang2.ChatPane.ChatPane<CardKey>(Wanxiang2.ChatPane.ChatPaneLooks.ofTheme SharedTheme.manager SharedTheme.styles, brandLogo)
    let messagePanel = pane.Messages
    let scroller = pane.Scroller.Viewer
    let follow = pane.Follow
    let jump = pane.Jumps
    /// 决策 433：顶栏「⋯」里的「存为预设…」；未接线时不显示。
    let mutable savePresetAction: (unit -> unit) option = None
    let mutable presetStarterCount = 0
    do
        messagePanel.Spacing <- ContentMetrics.messageGap
        // 滚到底时末条消息与输入区之间的呼吸量由 messagePanel 的下边距统一定义（见 ScrollLayoutTests）。
        messagePanel.Margin <- Thickness(0.0, 0.0, 0.0, ContentMetrics.messageEndBreathing)
        messagePanel.MaxWidth <- Tokens.readingWidth
        messagePanel.HorizontalAlignment <- HorizontalAlignment.Stretch
        scroller.Padding <- Thickness(Tokens.shellInset, Spacing.space3xl, Tokens.shellInset, 0.0)

    /// 已渲染的卡片，按身份缓存。
    ///
    /// 不加这层缓存的话，流式期间每批 delta 都要把整表拆掉重建：
    /// 200 条消息实测约 100ms 一次，越聊越卡，而其中只有最后一张卡真的变了。
    /// 复用归 Wanxiang2.MessageCardView.CardDeck（CardReconcile）；身份仍是这里的 CardKey
    /// （比 CardDeck.key 多记工具结果、版本条与审批），挂载顺序另记一份给跳转与导航。
    let deck = Wanxiang2.MessageCardView.CardDeck(messagePanel)
    let mutable mounted: CardKey option[] = [||]
    let keyText (k: CardKey) =
        sprintf "%d|%s|%d|%d|%d|%g|%b|%b|%d|%d|%d|%b|%d"
            k.commit k.role k.toolCalls k.toolResults k.attachments k.fontSize k.collapse k.lastAssistant
            k.usageTag k.missingTag k.siblingTag k.awaitingTools k.approvalTag
    /// 决策 292：流式卡原位更新。外形（除正文外的一切：思考、工具调用、附件、字号、审批）不变时，
    /// 同一张卡留在面板上，只把新文字交给它的 StreamingMarkdown；外形变了才换代（streamGeneration + 1）。
    let mutable streamSlot: (obj * CardKey * Wanxiang2.MarkdownView.MarkdownStream) option = None
    let mutable streamGeneration = 0

    /// 决策 460：定位钉住的目标卡。钉住期间布局再变都按它的当前位置重新对齐。
    let mutable revealTarget: Control option = None
    let mutable headerBar: Border = Unchecked.defaultof<Border>
    /// 决策 293：打开长对话只挂尾部一个窗口的卡片（首屏只排这几十张），
    /// 往上滚到窗口顶再把已在本机的更早消息挂上来（与分页同一套 prepend 锚点），本机挂完了才向主机要更早一页。
    /// 搜索命中 / 消息链接指向窗口外的消息时，窗口直接扩到那条。
    /// 窗口大小、本帧隐藏（已加载但未挂载）的提交与上一帧首条归 MountWindow。
    let mountWindow = MountWindow(MountWindow.Initial, MountWindow.Step)
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
    /// mounted 与 messagePanel.Children 逐一对齐（渲染循环一帧一卡），
    /// 所以这里是用户卡的下标表，不必给卡打 Tag。
    let exchangeIndices () : int list = ExchangeJump<CardKey>.Cards(mounted, fun k -> k.role = "user")

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

    /// 「回到最新」浮钮随 follow 状态重画（ChatPane 没有公开的刷新入口，SetCompact 会重算一次）。
    let refreshLatest () = pane.SetCompact compactMode

    /// 把某张用户卡平滑带到视口顶部：目标卡片尽量完整可见，不被输入区挡住。每帧按卡片当前位置取目标（决策 460）。
    let smoothScrollToCard (child: Control) =
        let targetOf () = max 0.0 (child.Bounds.Y - Spacing.space3xl)
        if not (pane.Scroller.SmoothTo(targetOf, ignore, ignore)) then pane.Scroller.SetOffset(targetOf ())

    /// 阅读导航的端点状态（规则见 ExchangeJump）。
    let syncExchangeNavState () =
        pane.RefreshJumps(mounted, exchangeIndices (), currentExchangeIndex (), mountWindow.HiddenCount > 0)

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

    member _.CanJumpPrevious = pane.Nav.CanJumpPrevious
    member _.CanJumpNext = pane.Nav.CanJumpNext

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
                [ if pane.Nav.CanJumpPrevious then
                      MenuEntry.create "上一条提问" (fun () -> this.JumpExchange -1) |> MenuEntry.withIcon Icons.chevronsUp
                  if pane.Nav.CanJumpNext then
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
        let view = breadcrumb.Set(crumb |> Option.map (fun c -> c.text, c.description, c.parentAvailable, c.parentId))
        match crumb with
        | None -> breadcrumbBar.IsVisible <- view.visible
        | Some c ->
            breadcrumbText.Text <- view.text
            breadcrumbAction.Focusable <- c.parentAvailable
            breadcrumbAction.Cursor <- if c.parentAvailable then new Cursor(StandardCursorType.Hand) else null
            ToolTip.SetTip(breadcrumbAction, c.description)
            Avalonia.Automation.AutomationProperties.SetName(breadcrumbAction, c.text)
            Avalonia.Automation.AutomationProperties.SetHelpText(breadcrumbAction, c.description)
            if c.parentAvailable then Avalonia.Automation.AutomationProperties.SetRole(breadcrumbAction, AutomationRole.Button)
            else Avalonia.Automation.AutomationProperties.SetControlTypeOverride(breadcrumbAction, Nullable Avalonia.Automation.Peers.AutomationControlType.Text)
            breadcrumbBar.IsVisible <- view.visible

    /// 面包屑当前显示的文字（空串 = 不显示）。
    member _.BreadcrumbText: string = breadcrumb.Text

    /// 激活面包屑（回到父对话）；不可点击时什么也不做。返回是否发生了跳转。
    member _.ActivateBreadcrumb() : bool =
        match breadcrumb.Activate() with
        | Some id ->
            openBreadcrumbParent id
            true
        | None -> false

    /// 把指定消息滚到视口。卡片不在已加载窗口里（更早的历史）时返回 false，由调用方先加载更早历史。
    /// 最近一次被「闪一下」的消息（测试与无障碍：定位成功的可观察信号）。
    member _.LastFlashedCommit = flash.LastFlashed

    /// 滚到一条消息并短暂高亮（第 211 节）：搜索命中跳转后，眼睛要能立刻找到是哪一条。
    /// 背景淡色停留约 1.6 秒后复原；不改布局，不抢焦点。
    member this.RevealAndHighlight(commitId: uint64) : bool =
        if this.ScrollToMessage commitId then
            flash.Revealed commitId
            Dispatcher.UIThread.Post(
                (fun () ->
                    let index = mounted |> Array.tryFindIndex (function Some k -> k.commit = commitId | None -> false)
                    match index with
                    | Some i when i < messagePanel.Children.Count ->
                        // 卡片本身是 StackPanel（或带版本条的竖排），背景刷不出圆角面：
                        // 找 MessageCard 外包的高亮承载面（第 211 节）。
                        let surface =
                            match messagePanel.Children.[i] with
                            | :? Border as b when b.Name = Cards.highlightName -> Some b
                            | card ->
                                card.GetVisualDescendants()
                                |> Seq.tryPick (function
                                    | :? Border as b when b.Name = Cards.highlightName -> Some b
                                    | _ -> None)
                                |> Option.orElse (
                                    card.GetLogicalDescendants()
                                    |> Seq.tryPick (function
                                        | :? Border as b when b.Name = Cards.highlightName -> Some b
                                        | _ -> None))
                        match surface with
                        | Some b ->
                            highlightTimer |> Option.iter (fun (t: DispatcherTimer, restore) -> t.Stop(); restore ())
                            b.Background <- Tokens.accentSoft
                            flash.Begin commitId |> ignore
                            let restore () =
                                b.Background <- Brushes.Transparent
                                flash.End() |> ignore
                                highlightTimer <- None
                            let timer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds RevealFlash.DwellMs)
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
    member _.RevealPinned = follow.RevealPinned

    /// 当前正高亮着的消息（高亮结束后为 None）。
    member _.HighlightedCommit = flash.Highlighted

    /// 立即结束定位高亮（计时器到点时走的同一路径；无显示测试里 DispatcherTimer 不可确定泵出）。
    member _.EndRevealHighlight() =
        highlightTimer |> Option.iter (fun (t: DispatcherTimer, restore) -> t.Stop(); restore ())

    member this.ScrollToMessage(commitId: uint64) : bool =
        this.EnsureMounted commitId
        let indexOf () =
            mounted |> Array.tryFindIndex (function Some k -> k.commit = commitId | None -> false)
        match indexOf () with
        | Some _ ->
            follow.Pin()
            // 分页前置的锚点恢复会把偏移写回旧位置，这里的目标位置优先
            pane.Scroller.CancelPrependAnchor()
            refreshLatest ()
            // 卡片刚挂上时 Bounds 还没量好：等一次布局再量（按身份重新找，期间卡片序可能变）。
            Dispatcher.UIThread.Post(
                (fun () ->
                    match indexOf () with
                    | Some index when index < messagePanel.Children.Count ->
                        let child = messagePanel.Children.[index]
                        if follow.RevealPinned then revealTarget <- Some child
                        smoothScrollToCard child
                    | _ -> ()),
                DispatcherPriority.Background)
            true
        | None -> false

    member this.SetCompactMode(value: bool) =
        compactMode <- value
        let size = if value then LayoutPolicy.compactActionTarget else Tokens.iconButton
        for button in [ sidebarToggleButton; moreButton ] do
            Ui.setSquareTarget button size
        pane.SetCompact value
        moreButton.IsVisible <- hasConversationChrome
        // 手机窄屏：芯片收窄但不收起——标题可以截断，当前模型不能看不见。
        headerModelCaption.MaxWidth <- if value then 120.0 else 220.0
        headerModelChip.MinHeight <- if value then LayoutPolicy.compactActionTarget else 0.0
        this.SyncGeneratingChipVisibility()

    /// 空态标题靠近输入区；只改布局，不移除或重挂输入控件。
    member _.SetStartMode(value: bool) = pane.Empty.SetBottomAligned value

    /// 起手式的去处（通常是 Composer.SetText）；不设则空态不显示起手式。
    member _.SetStarterHandler(pick: string -> unit) =
        pane.Empty.SetStarters ChatStarters.items
        pane.Empty.SetStarterPick(Some pick)

    /// 决策 433：空态的预设芯片（名称, 说明, 套用）。
    member _.SetPresetStarters(items: (string * string * (unit -> unit)) list) =
        presetStarterCount <- items.Length
        pane.Empty.SetPresets items

    member _.PresetStarterCount = presetStarterCount

    member _.SetSavePresetAction(action: (unit -> unit) option) = savePresetAction <- action

    member this.ShowEmpty(state: ChatEmptyState, onPrimary: (string * (unit -> unit)) option) =
        pane.ShowEmpty(state, onPrimary)
        messagePanel.IsVisible <- false

    /// 在空态位置放一块自带交互的内容（首次运行的「连接一个模型」卡片）。
    member this.ShowEmbeddedEmpty(content: Control) =
        pane.ShowEmbedded content
        pane.HideSkeleton()
        messagePanel.IsVisible <- false

    member this.HideEmpty() =
        // 定位（搜索命中 / 版本切换）刚把视口钉在目标消息上时不能复位（v4 走查）；换对话由 AppShell 显式复位。
        if not follow.RevealPinned then this.ResetScrollState()
        pane.HideEmpty()
        messagePanel.IsVisible <- true

    /// 重置滚动到底状态与未读计数（换对话、加载骨架或切换视图时调用）。
    /// 决策 293：首屏挂载窗口与每次向上扩展的步长
    static member InitialMountWindow = MountWindow.Initial
    static member MountWindowStep = MountWindow.Step

    /// 决策 300：主机上是否还有更早的历史页。没有了就撤掉挂起的「上一轮」。
    member _.SetOlderAvailable(value: bool) =
        pane.SetOlderAvailable value
        syncExchangeNavState ()

    member _.PendingJumpPrevious = jump.PendingPrevious

    /// 决策 310：更早一页取失败（超时 / 断线 / 主机拒绝）。挂起的「上一轮」撤掉，
    /// 消息区顶部给一句原因和「重试」；重试由调用方重新发页请求。
    member _.ShowOlderPageFailure(message: string, retry: unit -> unit) =
        pane.ShowOlderFailure(message, retry)
        syncExchangeNavState ()

    member _.HideOlderPageFailure() = pane.HideOlderFailure()
    member _.OlderPageFailureVisible = pane.Nav.OlderFailureVisible
    member _.OlderPageFailureText = pane.Nav.OlderFailureText
    member _.OlderPageRetryButton = pane.Nav.RetryButton

    /// 当前因窗口未挂载的已加载消息数（测试 / 诊断用）
    member _.HiddenMessageCount = mountWindow.HiddenCount

    /// 把窗口向上扩一步（已加载、未挂载的消息先挂）；返回是否真的扩了。
    member this.ExpandMountWindow() : bool =
        if mountWindow.HiddenCount = 0 then false
        else
            this.BeginHistoryPrependAnchor()
            mountWindow.Expand() |> ignore
            rerenderLast ()
            this.RestoreHistoryPrependAnchorDeferred()
            true

    /// 确保某条消息已挂载（窗口外则扩到它）。
    member private this.EnsureMounted(commitId: uint64) =
        if mountWindow.EnsureMounted commitId then rerenderLast ()

    member this.ResetScrollState() =
        mountWindow.Reset()
        revealTarget <- None
        // 锚点跟着对话一起丢：换对话后旧锚引用的卡片已不在面板上。
        pane.Reset()
        pane.HideOlderFailure()
        syncExchangeNavState ()

    /// 显示骨架屏（换对话或初次加载中，替代空白屏与“加载中…”）
    member this.ShowSkeletonLoading() =
        this.ResetScrollState()
        pane.HideEmpty()
        messagePanel.IsVisible <- false
        pane.ShowSkeleton()

    /// 隐藏骨架屏
    member this.HideSkeletonLoading() =
        if pane.Skeleton.IsShown then
            pane.HideSkeleton()
            messagePanel.IsVisible <- true

    member this.IsSkeletonVisible: bool = pane.Skeleton.IsShown
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
        let merged = MessageView.mergeToolResults messages
        let lastAssistantIndex =
            merged
            |> List.mapi (fun index m -> index, m)
            |> List.filter (fun (_, m) -> not (MessageView.isUser m))
            |> List.tryLast
            |> Option.map fst
        let ctxFor (index: int) (isStreaming: bool) =
            ({ FontSize = fontSize
               AutoCollapseReasoning = autoCollapseReasoning
               Streaming = isStreaming
               IsLastAssistant = (streamingMessage.IsNone && lastAssistantIndex = Some index)
               Usage = usage
               MissingAttachments = missingAttachments
               AwaitingApproval = awaitingApproval
               BrandAvatar = fun () -> brandLogo Tokens.logoAvatar }: Wanxiang2.MessageCardView.CardContext)
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
        let firstMounted = mountWindow.Plan(merged |> List.map (fun m -> m.commitId), pane.Scroller.PrependAnchorPending)
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
                          fontSize = ctx.FontSize
                          collapse = ctx.AutoCollapseReasoning
                          lastAssistant = ctx.IsLastAssistant
                          usageTag = (if ctx.IsLastAssistant then hash ctx.Usage else 0)
                          missingTag =
                            (if List.isEmpty message.attachments then 0 else hash ctx.MissingAttachments)
                          siblingTag = (match siblings with Some s -> hash (s.index, s.count, s.targets) | None -> 0)
                          awaitingTools = awaitingTools
                          approvalTag = (if awaitingTools then hash awaitingApproval else 0) })
                desired.Add(
                    key,
                    fun () ->
                        let card = Cards.render message ctx actions.message (Some index)
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
                desired.Add(Some key, fun () -> Cards.render streaming ctx actions.message None)
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
                let card, live = Cards.renderWithStream streaming ctx actions.message None
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
                let card = Cards.render partial (ctxFor -1 false) actions.message None
                Ui.vstack Spacing.spaceXs [ card; Cards.incompleteMarker () ] :> Control)
        | None -> ()
        match error with
        | Some(err, retry) ->
            desired.Add(None, fun () -> Cards.errorCard err retry actions.message (partialNow |> Option.map snd))
        | None -> ()

        // 只动前缀之后的部分。追加一条消息或刷新流式卡时，前面几十上百张卡不动。
        // 缓存只保留这一帧用到的身份：换对话、改字号都会让旧身份失效，留着只是占内存（Reconciler 每帧清）。
        deck.Show [ for key, factory in desired -> key |> Option.map keyText, factory ] |> ignore
        mounted <- [| for key, _ in desired -> key |]
        // 锚点指向的卡可能已经不在了（流式卡换代、错误卡消失）：丢掉锚点，
        // 下一次跳转重新用视口位置起算，不会指向一张不存在的卡。
        let resume = jump.Mounted(mounted, mountWindow.LastFirstCommit)
        syncExchangeNavState ()
        if resume then
            // 等分页锚点恢复（Background）之后再跳，免得被写回旧偏移。
            Dispatcher.UIThread.Post((fun () -> this.JumpExchange -1), DispatcherPriority.ContextIdle)

        // 窗口外未挂载的消息也算条数（决策 293）；分页前置与流式占位不记未读，流式收尾补记一条。规则见 ScrollFollow。
        // ChatPane 约定：渲染后同步调用、先于布局，否则贴底跟随会失效。
        pane.Rendered(streamingMessage.IsSome, desired.Count + firstMounted, pane.Scroller.PrependAnchorPending)

    /// 丢弃卡片缓存，下一次重绘全部重建。
    ///
    /// 后台着色算完时要用：着色结果缓存在 `Highlight` 里，
    /// 但卡片身份没变，不清缓存的话屏幕上会一直挂着没着色的那一版。
    member _.InvalidateCards() =
        deck.Clear()
        mounted <- [||]

    /// 滚到底必须等布局把新内容的高度算进 Extent（定位钉住时不滚）。
    member this.ScrollToEndDeferred() = pane.Scroller.ScrollToEndDeferred(fun () -> follow.RevealPinned)

    member this.ScrollToEnd() = this.ScrollToEndDeferred()

    /// 平滑滚动到顶部：与「回到最新」同一条曲线；减弱动效下退化为瞬移。锚点作废。
    member this.SmoothScrollToBeginning() =
        jump.ClearAnchor()
        follow.LeaveForBeginning()
        pane.Scroller.StopSmooth()
        if not (pane.Scroller.SmoothTo((fun () -> 0.0), refreshLatest, fun _ -> refreshLatest ())) then
            if pane.Scroller.Reduced then this.ScrollToBeginningDeferred()
            else refreshLatest ()
        else refreshLatest ()

    member this.ScrollToBeginningDeferred() = pane.Scroller.ScrollToHomeDeferred()

    /// 用户意图入口：回到对话开头；阅读锚点只在这一层清。
    member this.ScrollToBeginning() =
        jump.ClearAnchor()
        this.ScrollToBeginningDeferred()

    /// 平滑滚动到底部
    member this.SmoothScrollToEnd() =
        revealTarget <- None
        pane.ScrollToLatest()

    /// 分页前置的滚动锚点（ChatScroller）。
    member _.BeginHistoryPrependAnchor() = pane.Scroller.BeginPrependAnchor()
    member _.RestoreHistoryPrependAnchorDeferred() = pane.Scroller.RestorePrependAnchorDeferred()
    member _.CancelHistoryPrependAnchor() = pane.Scroller.CancelPrependAnchor()

    /// 测试读：消息列滚动区、消息插槽与「回到最新」浮钮。
    member _.MessageScroller: ScrollViewer = scroller
    member _.MessagesPanel: StackPanel = messagePanel
    member _.LatestButton = pane.Scroller.LatestButton

    /// 平滑滚动是否在进行（测试读）。
    member _.IsSmoothScrolling = pane.Scroller.IsSmoothing

    /// 跳转到上一条 / 下一条用户提问。`direction` < 0 向上，> 0 向下。
    /// 锚点语义与 kelivo 一致：跳过一次之后箭头就跟着锚点走，不再受当前滚动位置影响，
    /// 连续按「上一条」能一排排往回翻。
    member this.JumpExchange(direction: int) =
        let cards = exchangeIndices ()
        match pane.Jump(direction, mounted, cards, currentExchangeIndex (), mountWindow.HiddenCount > 0, mountWindow.LastFirstCommit) with
        | JumpStep.ExpandAndRetry ->
            // 决策 300：本机已加载、只是没挂载——先挂上一步再续跳。
            if this.ExpandMountWindow() then this.JumpExchange direction
        | JumpStep.RequestOlder -> actions.requestOlderHistory ()
        | JumpStep.ScrollTo(index, _) ->
            // ChatPane 已停旧动画并记下跳转；宿主把那张卡滚进视口。
            if index < messagePanel.Children.Count then smoothScrollToCard messagePanel.Children.[index]
        | JumpStep.Stay -> ()
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


        this.ApplyHorizontalInsets()
        this.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then this.ApplyHorizontalInsets())
        pane.Scroller.Scrolled.Add(fun note ->
            // 决策 460：钉住期间内容高度变了（不是用户在滚）→ 把目标卡重新对齐到顶部。
            match revealTarget with
            | Some child when follow.RevealPinned && not pane.Scroller.IsSmoothing && abs note.ExtentDelta > 0.1 ->
                if isNull child.Parent then revealTarget <- None
                else
                    let target = max 0.0 (child.Bounds.Y - Spacing.space3xl)
                    if abs (scroller.Offset.Y - target) > 1.0 then pane.Scroller.SetOffset target
            | _ -> ()
            syncExchangeNavState ()
            // 骨架可见时不预取更早历史。
            if note.AtTop && not pane.Skeleton.IsShown then
                // 决策 293：本机还有没挂的更早消息时先挂它们；只认用户把视口往上推到了顶。
                if mountWindow.HiddenCount > 0 then
                    if note.Upward then this.ExpandMountWindow() |> ignore
                // 决策 310：上一页刚失败时不靠滚动静默重发。
                elif not pane.Nav.OlderFailureVisible then actions.requestOlderHistory ())
        pane.Scroller.Wheeled.Add(fun () -> revealTarget <- None)
        pane.FocusReturned.Add(fun () -> actions.focusHome ())
        let body = pane.Host

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

        this.AttachedToVisualTree.Add(fun _ -> ParityProbe.hit "lifecycle.chatAttached")
        this.DetachedFromVisualTree.Add(fun _ -> pane.Scroller.StopSmooth())
