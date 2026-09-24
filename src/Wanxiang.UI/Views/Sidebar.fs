namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Shapes
open Avalonia.Controls.Primitives
open Avalonia.Controls.Templates
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Controls.Documents
open Avalonia.Threading
open Avalonia.VisualTree

/// 侧栏对外暴露的动作。
type SidebarActions = {
    newConversation: unit -> unit
    openConversation: Guid -> unit
    renameConversation: ConversationSummary -> unit
    deleteConversation: ConversationSummary -> unit
    setPinned: ConversationSummary -> bool -> unit
    setArchived: ConversationSummary -> bool -> unit
    /// 置顶是否对当前选中的全部会话成立（true = 全已置顶，操作即取消置顶）。
    selectionAllPinned: Guid list -> bool
    /// 对选中的整批会话置顶 / 取消置顶。
    setPinnedMany: Guid list -> bool -> unit
    setArchivedMany: Guid list -> bool -> unit
    deleteMany: Guid list -> unit
    duplicateAsFork: ConversationSummary -> unit
    exportConversation: ConversationSummary -> unit
    openSettings: unit -> unit
    reconnect: unit -> unit
    toggleArchivedVisibility: unit -> unit
    closeNavigation: unit -> unit
}

type private SidebarListItem =
    | SectionHeader of string
    | ConversationRow of ConversationSummary
    | ArchivedToggle

    member this.isItem =
        match this with
        | ConversationRow _ -> true
        | SectionHeader _ | ArchivedToggle -> false

    member this.IsItem = this.isItem

type private RowToolTip(text: string) as this =
    inherit TextBlock()
    do
        this.Text <- text
        this.FontFamily <- Tokens.fontFamily
    override _.ToString() = text

/// 侧栏空态三态互斥的种类标记：卡只在种类翻转时重建。
type private EmptyVariant =
    | NotConnected
    | ListLoading
    | NoConversations

/// 搜索命中切分：把一行文本按查询词切成「命中 / 非命中」交替片段，供标题与预览
/// 上色高亮。分段是纯函数——测试直接断言，不需要起窗口。
///
/// 为什么不是 TextBlock.Text 高亮：Avalonia 的 TextBlock.Text 是纯字符串，
/// 要局部上色只能靠 Inlines 里的 Run。这里只负责「切成哪几段、哪段着色」，
/// 视觉决策（颜色、字号、加粗）留给调用方。
///
/// 分段口径与 ConversationSummary.matches 完全一致（空格分词、忽略大小写、
/// 每个词都必须参与匹配）：搜索能返回这行，这里就一定会标出使它返回的那些词。
/// 两者的口径一旦漂移，就会出现「行被搜出来了却看不见哪里匹配」。
module SidebarText =

    /// 一个文本片段：Text 为内容，IsMatch 表示该片段应被视为查询命中。
    type Segment = { text: string; isMatch: bool }

    /// 与 ConversationSummary.matches 同一套分词规则，返回参与匹配的非空词表。
    let terms (query: string) : string list =
        if String.IsNullOrWhiteSpace query then []
        else
            query.Trim().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.distinct
            |> List.ofArray

    /// 把 text 按 terms 切成有序片段。
    ///
    /// 规则：
    /// - 空查询或空文本返回单段非命中（调用方就该当普通文本渲染）。
    /// - 命中片段按出现次序从左到右取；多个词命中同一区域时不重叠重复取，
    ///   全部命中片段先合并排序再去重，保证 text 被完整覆盖一次。
    /// - 大小写不敏感比较，但保留原文片段（不是查询词本身），长文本不被改写。
    let segments (query: string) (text: string) : Segment list =
        let words = terms query
        if List.isEmpty words || String.IsNullOrEmpty text then
            let safeText = if isNull text then "" else text
            [ { text = safeText; isMatch = false } ]
        else
            // 每个词单独扫一遍，收集命中的 [start, end) 区间。
            let raw =
                [ for word in words do
                    if String.IsNullOrEmpty word then ()
                    else
                        let mutable start = 0
                        let mutable keep = true
                        while keep do
                            let index = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase)
                            if index < 0 then keep <- false
                            else
                                yield (index, index + word.Length)
                                start <- index + word.Length
                                if start >= text.Length then keep <- false ]
            if List.isEmpty raw then
                [ { text = text; isMatch = false } ]
            else
                // 排序后合并重叠/相邻区间：相邻合并让「两个词挨着」也连成一段高亮，
                // 视觉上是一整块而不是两段中间被底色切碎。fold 一路保持列表有序，
                // 累加时按「最后一段」比较，新区间只接到末尾——不引用中间段。
                let sorted = raw |> List.sortBy fst
                let merged =
                    (sorted, [])
                    ||> List.fold (fun acc (s, e) ->
                        match acc with
                        | [] -> [ (s, e) ]
                        | prev ->
                            let ps, pe = List.last prev
                            if s <= pe then
                                // 与末段重叠或紧邻：把末段延长（max 保证完全包含不被砍短）。
                                (List.truncate (List.length prev - 1) prev) @ [ (ps, max pe e) ]
                            else
                                prev @ [ (s, e) ])
                // 从合并后的区间还原完整文本，空隙补非命中段。
                let spans =
                    [ let mutable cursor = 0
                      for (s, e) in merged do
                          if s > cursor then yield { text = text.Substring(cursor, s - cursor); isMatch = false }
                          yield { text = text.Substring(s, e - s); isMatch = true }
                          cursor <- e
                      if cursor < text.Length then
                          yield { text = text.Substring(cursor); isMatch = false } ]
                spans


/// 会话侧栏。
///
/// 旧版是一个平铺 ListBox：没有分组、没有置顶、没有空态、右键只有两项。
/// 这里按时间分组、置顶提前、搜索常驻、每行可右键操作，
/// 并且在「没有会话」「搜不到」「未连接」三种空态下说不同的话。
type Sidebar(overlay: OverlayHost, actions: SidebarActions, brandLogo: float -> Control) as this =
    inherit Border()

    let handCursor = new Cursor(StandardCursorType.Hand)

    /// 按查询词给一段文本上高亮：填入 TextBlock.Inlines，命中词着 accent 色。
    /// 查询词由调用方显式传入（RenderRow 取渲染快照、RefreshRowHost 取新快照），
    /// 所以它不读控件状态、可在任意调用点重放；查询为空时等价于纯文本渲染。
    /// 高亮只改前景色：命中段之外的 run 不设 Foreground，块自身的
    /// Foreground 继续统治非命中文本，选中/悬停/紧凑态切色不必顾及这里。
    let segmentText (block: TextBlock) (query: string) (text: string) =
        // 必须先丢掉 Text：Avalonia 的 TextBlock.Text 就是 Inlines 的一个隐式 run，
        // 留着它再 Add 会让同一段文本渲染两遍（一条不着色 + 一条着色，视觉上叠影）。
        block.Text <- null
        block.Inlines.Clear()
        for span in SidebarText.segments query text do
            let run = Run(span.text)
            if span.isMatch then
                run.Foreground <- Tokens.accent
            block.Inlines.Add run

    let searchShell, searchBox = Ui.textField "搜索会话"
    let clearSearchButton = Ui.iconButton Icons.close "清空搜索"
    let compactBackButton = Ui.iconButton Icons.arrowLeft "返回对话"
    let newConversationButton = Ui.iconButtonAccent Icons.plus "新建会话（Ctrl+N）"
    let settingsButton = Ui.iconButton Icons.gear "设置（Ctrl+,）"
    let searchDebounce = DispatcherTimer(Interval = MotionLedger.searchInputDebounce)
    let conversationList =
        ListBox(
            Background = Brushes.Transparent,
            BorderThickness = Thickness 0.0,
            // V24: ListBox 本体可聚焦，键盘 Tab/Directional 才能进入侧栏；行聚焦仍走 FocusRowAt 的 Post 机制，容器 ListBoxItem 保持 Focusable=false。
            Focusable = true)
    /// 空态宿主：内容卡按空态种类经 Ui.emptyState 整体构建，按钮与骨架实例跨种类保留，
    /// 焦点与自动化属性因此连续；margin/垂直居中保持旧观感，视觉锚点与设置面板同源。
    let emptyStateHost =
        Border(
            IsVisible = false,
            Margin = Thickness(Tokens.space5, Tokens.space8, Tokens.space5, Tokens.space8),
            VerticalAlignment = VerticalAlignment.Center)
    /// 当前空态种类：只在种类翻转时换卡，同种类内的列表刷新不重建这棵树。
    let mutable currentEmptyVariant: EmptyVariant option = None
    /// 列表加载中骨架：与 ChatView 骨架同一视觉语言（borderSoft 圆角条、错落宽度）。
    /// 只在「已连接 + 已请求列表 + 快照未达」时出现，绝不与确认空态同时呈现。
    let emptySkeleton =
        let panel =
            StackPanel(
                Orientation = Orientation.Vertical,
                Spacing = Tokens.space2,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = Thickness(0.0, Tokens.space2, 0.0, 0.0),
                IsVisible = false)
        for (barHeight, widthFraction) in [ (10.0, 0.85); (10.0, 0.60); (10.0, 0.72) ] do
            let bar =
                Border(
                    Height = barHeight,
                    CornerRadius = CornerRadius Tokens.radiusSm,
                    Background = Tokens.borderSoft,
                    Opacity = Tokens.skeletonOpacityBase,
                    HorizontalAlignment = HorizontalAlignment.Stretch)
            let row = Grid(HorizontalAlignment = HorizontalAlignment.Stretch)
            row.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(widthFraction, GridUnitType.Star)))
            row.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(max 0.01 (1.0 - widthFraction), GridUnitType.Star)))
            Grid.SetColumn(bar, 0)
            row.Children.Add bar
            panel.Children.Add row
        Avalonia.Automation.AutomationProperties.SetName(panel, "正在加载会话")
        panel
    /// 空态内嵌主按钮：与 ChatView 空态同一模式——单按钮 + 可替换的当前动作，
    /// 文案/动作随空态种类切换；右上角常驻加号不受影响。
    let mutable emptyPrimaryAction: unit -> unit = ignore
    let emptyActionButton =
        let button = Ui.button Ui.Primary "" (fun () -> emptyPrimaryAction ())
        button.HorizontalAlignment <- HorizontalAlignment.Center
        button.IsVisible <- false
        button
    let searchEmptyHintTitle =
        TextBlock(
            Text = "未找到匹配会话",
            FontSize = Tokens.fontSmall,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.textMuted,
            TextAlignment = TextAlignment.Center)
    let searchEmptyHintSub =
        TextBlock(
            Text = "点击清空，或按 Enter / Esc 恢复",
            FontSize = Tokens.fontMicro,
            Foreground = Tokens.textFaint,
            TextAlignment = TextAlignment.Center)
    let searchEmptyHintPanel =
        StackPanel(
            Orientation = Orientation.Vertical,
            Spacing = Tokens.space1,
            HorizontalAlignment = HorizontalAlignment.Center)
    let searchEmptyHint =
        ActionBorder(
            CornerRadius = CornerRadius Tokens.radiusMd,
            Padding = Thickness(Tokens.space4, Tokens.space3),
            Margin = Thickness(Tokens.space4, Tokens.space8, Tokens.space4, 0.0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Cursor = handCursor,
            Focusable = true,
            IsVisible = false)

    let statusDot = Ui.statusDot ControlMetrics.sidebarRunningDotSize
    let statusText =
        TextBlock(
            Text = "未连接",
            FontSize = Tokens.fontCaption,
            Foreground = Tokens.textMuted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis)

    let mutable summaries: ConversationSummary list = []
    /// 当前搜索词（Rebuild 时从搜索框快照）：行渲染与回收复用都据此上高亮。
    /// 只有 Rebuild 会写它，行渲染只是读者，所以防抖/立即两种时机不会互相打脸。
    let mutable searchQuery = ""
    let mutable activeId: Guid option = None
    let mutable showArchived = false
    let mutable connected = false
    /// 会话列表加载中：已连接且 ObserveConversationList 已发出、ConversationListSnapshot 未达。
    /// 置位只在 AppShell 发出列表请求时发生；解除只发生在快照数据到达（SetConversations）
    /// 或连接状态翻转（SetConnection）——同一事实单一写入链，无第二处 reconciliation。
    let mutable listLoading = false
    let rowHosts = System.Collections.Generic.Dictionary<Guid, Border>()
    let summaryById = System.Collections.Generic.Dictionary<Guid, ConversationSummary>()
    let mutable visibleRowIds: Guid array = [||]
    let flatIndexByConversation = System.Collections.Generic.Dictionary<Guid, int>()
    let mutable focusedRowId: Guid option = None
    let mutable archivedCount = 0
    let mutable hasArchivedToggle = false
    let mutable archivedToggleFlatIndex: int option = None
    let mutable archivedToggleHost: Border option = None
    let mutable compactMode = false
    let mutable focusedRowIndex = -1
    let mutable pendingFocusId: Guid option = None
    /// 批量选择模式：行点击改为切换选中，底部换成批量操作条。
    /// 桌面端从行的右键菜单进入；触屏没有右键与 Shift+F10，行的更多菜单同样提供入口。
    let mutable selectionMode = false
    let selectedIds = System.Collections.Generic.Dictionary<Guid, unit>()
    /// 选择模式头 / 底部操作条只在模式翻转时换控件：同模式内的选中变化只刷计数与按钮态，
    /// 不重建这棵树，按钮与计数文本的焦点保持连续。
    let selectionHeader =
        Border(
            Background = Tokens.surfaceContainer,
            Padding = Thickness(Tokens.space3, Tokens.space2),
            IsVisible = false,
            BorderBrush = Tokens.hairline,
            BorderThickness = Thickness(0.0, 0.0, 0.0, ControlMetrics.sidebarDividerWidth))
    let selectionCountText =
        TextBlock(
            Text = "已选 0 项",
            FontSize = Tokens.fontSmall,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis)
    let selectionActionBar =
        Border(
            Background = Tokens.surface,
            Padding = Thickness(Tokens.space2, Tokens.space2),
            IsVisible = false,
            BorderBrush = Tokens.hairline,
            BorderThickness = Thickness(0.0, ControlMetrics.sidebarDividerWidth, 0.0, 0.0))
    /// 品牌行与页脚在 Build 时构造；选择模式要把它们收起让位，故提升为字段。
    let mutable brandHeader: Border = Unchecked.defaultof<Border>
    let mutable sidebarFooter: Border = Unchecked.defaultof<Border>
    /// 两个批量键同样要按选择状态刷新文案（全选 / 取消全选、置顶 / 取消置顶）：
    /// 文案即状态，Build 时算一次会在勾选变化后说谎。提升为字段供 RefreshSelectionChrome 写。
    let mutable selectAllButton: Border = Unchecked.defaultof<Border>
    let mutable pinButton: Border = Unchecked.defaultof<Border>

    do
        Ui.setReservedActionVisible clearSearchButton false
        ToolTip.SetTip(clearSearchButton, "清空搜索")
        Avalonia.Automation.AutomationProperties.SetName(clearSearchButton, "清空搜索")
        compactBackButton.IsVisible <- false
        Ui.onClick compactBackButton actions.closeNavigation
        Ui.onClick newConversationButton actions.newConversation
        Ui.onClick settingsButton actions.openSettings
        Ui.onClick clearSearchButton (fun () -> this.ResetSearch true)
        // `Ui.textField` 创建时 TextBox 已挂在 shell 下。这里要把图标和清空按钮
        // 合进同一输入壳，必须先解除原父子关系，否则 Avalonia 会因重复视觉父级
        // 在 MainView.Build 阶段直接抛异常，浏览器表现为整页白屏。
        searchShell.Child <- null
        let searchIcon = Icons.search Tokens.textFaint
        searchIcon.VerticalAlignment <- VerticalAlignment.Center
        searchIcon.Width <- Tokens.iconGlyph
        searchIcon.Height <- Tokens.iconGlyph
        // 图标光学基线与输入文本对齐：只下沉 iconBaselineNudge，不改外尺寸。
        searchIcon.Margin <- Thickness(0.0, Tokens.iconBaselineNudge, Tokens.space2, 0.0)
        // 清空按钮常驻槽位（Ui.setReservedActionVisible 只切透明度/命中），
        // 左侧留出与搜索图标对称的光学间距，出现时输入框不收缩。
        clearSearchButton.Margin <- Thickness(Tokens.space2, 0.0, 0.0, 0.0)
        clearSearchButton.VerticalAlignment <- VerticalAlignment.Center
        searchBox.VerticalAlignment <- VerticalAlignment.Center
        // 搜索占位符用 muted：比正文弱、比 faint 强，空输入框一眼可辨。
        searchBox.PlaceholderForeground <- Tokens.textMuted
        let searchRow = DockPanel(LastChildFill = true, VerticalAlignment = VerticalAlignment.Center)
        DockPanel.SetDock(searchIcon, Dock.Left)
        DockPanel.SetDock(clearSearchButton, Dock.Right)
        searchRow.Children.Add searchIcon
        searchRow.Children.Add clearSearchButton
        searchRow.Children.Add searchBox
        searchShell.Child <- searchRow

        searchEmptyHintPanel.Children.Add searchEmptyHintTitle
        searchEmptyHintPanel.Children.Add searchEmptyHintSub
        searchEmptyHint.Child <- searchEmptyHintPanel
        ToolTip.SetTip(searchEmptyHint, "点击清空搜索（Esc）")
        Avalonia.Automation.AutomationProperties.SetName(searchEmptyHint, "未找到匹配会话，点击清空搜索")
        searchEmptyHint.PointerEntered.Add(fun _ ->
            searchEmptyHint.Background <- Tokens.hover)
        searchEmptyHint.PointerExited.Add(fun _ ->
            searchEmptyHint.Background <- Brushes.Transparent)
        searchEmptyHint.GotFocus.Add(fun _ ->
            searchEmptyHint.Background <- Tokens.hover)
        searchEmptyHint.LostFocus.Add(fun _ ->
            searchEmptyHint.Background <- Brushes.Transparent)
        Ui.onClick searchEmptyHint (fun () -> this.ResetSearch true)
        searchEmptyHint.KeyDown.Add(fun e ->
            // Enter/Space 由 Ui.onClick 接管（单次清空）；Escape 必须显式处理，onClick 不覆盖它。
            if e.Key = Key.Escape then
                e.Handled <- true
                this.ResetSearch true)

        // compact 抽屉的键盘包含：抽屉打开时 Tab / Shift+Tab 只在本侧栏内循环，不在两端漏到
        // 背景聊天/输入区。与对话框同一套约定（OverlayHost）：MainLayout 的 compact scrim 挡指针，
        // 这里由 OverlayHost.TrapTab 锁键盘、挡住背景的 Tab 通路；打开即聚焦搜索框、关闭把焦点交还输入区由
        // AppShell.SetCompactNavigation 负责。非 compact 下本处理器不干预，桌面态侧栏与主区照常互相 Tab。
        // Tab 包含复用对话框/浮层同一实现 OverlayHost.TrapTab（可聚焦集合取其公共谓词 Focusables：
        // 有效可见 + 可聚焦 + 可用），不另立第二套循环：只拦 Tab，到头回绕、不漏端。
        this.KeyDown.Add(fun e ->
            if compactMode && e.Key = Key.Tab then
                overlay.TrapTab(this :> Control, e))

    member private _.ApplyRowState(summary: ConversationSummary, host: Border) =
        let isActive = activeId = Some summary.id
        // 选择模式下选中行与「当前会话」共用同一套选中语言（selected 底 + accent 左缘 +
        // 半粗体），不引入第二套选中色：批量操作是同一语义的复数形式。
        let highlighted = isActive || (selectionMode && selectedIds.ContainsKey summary.id)
        host.Background <- if highlighted then Tokens.selected :> IBrush else Brushes.Transparent :> IBrush
        // 键盘焦点环由 ActionBorder 统一绘制（outline 语义、零位移）；
        // 这里只管选中底与左缘，悬停由事件处理补，避免两套阴影互相覆盖。
        host.BorderBrush <- if highlighted then Tokens.accent :> IBrush else Brushes.Transparent :> IBrush
        // V25: 选中补非色线索（字重）：悬停只动底色，选中另加标题半粗体。
        match host.Child with
        | :? StackPanel as column when column.Children.Count >= 2 ->
            match column.Children.[0] with
            | :? DockPanel as titleRow when titleRow.Children.Count >= 3 ->
                match titleRow.Children.[2] with
                | :? TextBlock as titleBlock -> titleBlock.FontWeight <- if highlighted then FontWeight.SemiBold else FontWeight.Medium
                | _ -> ()
            | _ -> ()
        | _ -> ()
        let status =
            match highlighted, summary.running with
            | true, true -> "当前会话，生成中"
            | true, false -> "当前会话"
            | false, true -> "生成中"
            | false, false when summary.pinned -> "已置顶"
            | false, false when summary.archived -> "已归档"
            | _ -> ""
        Avalonia.Automation.AutomationProperties.SetItemStatus(host, status)

    member private this.ApplyRowState(id: Guid, host: Border) =
        match summaryById.TryGetValue id with
        | true, summary -> this.ApplyRowState(summary, host)
        | _ ->
            match host.Tag with
            | :? ConversationSummary as summary -> this.ApplyRowState(summary, host)
            | _ ->
                let isActive = activeId = Some id
                let highlighted = isActive || (selectionMode && selectedIds.ContainsKey id)
                host.Background <- if highlighted then Tokens.selected :> IBrush else Brushes.Transparent :> IBrush
                // 焦点环同上：归 ActionBorder，避免与选中态阴影打架。
                host.BorderBrush <- if highlighted then Tokens.accent :> IBrush else Brushes.Transparent :> IBrush
                match host.Child with
                | :? StackPanel as column when column.Children.Count >= 2 ->
                    match column.Children.[0] with
                    | :? DockPanel as titleRow when titleRow.Children.Count >= 3 ->
                        match titleRow.Children.[2] with
                        | :? TextBlock as titleBlock -> titleBlock.FontWeight <- if highlighted then FontWeight.SemiBold else FontWeight.Medium
                        | _ -> ()
                    | _ -> ()
                | _ -> ()
                Avalonia.Automation.AutomationProperties.SetItemStatus(host, if highlighted then "当前会话" else "")

    member private _.FocusRowAt(index: int) =
        if index >= 0 && index < visibleRowIds.Length then
            let id = visibleRowIds[index]
            match flatIndexByConversation.TryGetValue id with
            | true, flatIndex ->
                conversationList.ScrollIntoView flatIndex
                // 单一机制：先 ScrollIntoView，聚焦只走一次 Post（容器实现后再 BringIntoView + 聚焦）。
                // 同步再调一次会和虚拟化实现竞争，造成焦点抖动。
                Dispatcher.UIThread.Post(
                    (fun () ->
                        match rowHosts.TryGetValue id with
                        | true, host ->
                            // Down 进结果时首项必须完整可见：布局完成后再 BringIntoView + 聚焦。
                            host.BringIntoView()
                            host.Focus NavigationMethod.Directional |> ignore
                        | _ -> ()),
                    DispatcherPriority.Input)
            | _ -> ()

    member private this.MoveRowFocus(id: Guid, delta: int) =
        match visibleRowIds |> Array.tryFindIndex ((=) id) with
        | Some 0 when delta < 0 ->
            this.FocusSearch(selectAll = false)
        | Some index ->
            let targetIndex = index + delta
            if targetIndex >= visibleRowIds.Length && hasArchivedToggle then
            // 末行继续 Down 就落到已归档开关上，而不是钳在原地；
            // 开关是列表最后一项，Up 会再桥回末行，来回都不断。

                this.FocusArchivedToggle()
            else
                this.FocusRowAt(Math.Clamp(targetIndex, 0, visibleRowIds.Length - 1))
        | None -> ()

    member private _.FocusArchivedToggle() =
        match archivedToggleFlatIndex with
        | Some flatIndex ->
            conversationList.ScrollIntoView flatIndex
            // 与 FocusRowAt 同一机制：聚焦只走 Post，避免与 ScrollIntoView 竞争。
            Dispatcher.UIThread.Post(fun () ->
                match archivedToggleHost with
                | Some host -> host.Focus NavigationMethod.Directional |> ignore
                | None -> ())
        | None -> ()

    member private this.FocusActiveConversation() =
        match activeId with
        | Some id ->
            match visibleRowIds |> Array.tryFindIndex ((=) id) with
            | Some idx -> this.FocusRowAt idx
            | None ->
                match rowHosts.TryGetValue id with
                | true, host -> host.Focus NavigationMethod.Directional |> ignore
                | _ -> ()
        | None ->
            if visibleRowIds.Length > 0 then
                this.FocusRowAt 0

    member this.ResetSearch(?focusSearch: bool) =
        let shouldFocus = defaultArg focusSearch true
        if not (String.IsNullOrEmpty searchBox.Text) then
            searchBox.Text <- ""
        searchDebounce.Stop()
        this.Rebuild()
        if shouldFocus then
            this.FocusSearch()

    /// 已归档开关：与会话行同高同圆角的弱操作行。
    /// 折叠时显示计数（展开入口），展开时变成收起入口；
    /// 图标 + 文案 + 人字形指示器，键盘与自动化行为都是按钮。
    member private this.RenderArchivedToggle() : Control =
        let expanded = showArchived
        let labelText =
            if expanded then "收起已归档会话"
            else sprintf "已归档会话 (%d)" archivedCount
        let archiveGlyph = Icons.archive Tokens.textMuted
        archiveGlyph.Width <- Tokens.iconGlyph
        archiveGlyph.Height <- Tokens.iconGlyph
        archiveGlyph.VerticalAlignment <- VerticalAlignment.Center
        let label =
            TextBlock(
                Text = labelText,
                FontSize = Tokens.fontSmall,
                Foreground = Tokens.textMuted,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis)
        // 披露箭头与 MessageCard 披露箭头同属一类 affordance，统一到 textMuted 一档。
        let chevronGlyph = (if expanded then Icons.chevronUp else Icons.chevronDown) Tokens.textMuted
        chevronGlyph.Width <- Tokens.iconGlyph
        chevronGlyph.Height <- Tokens.iconGlyph
        chevronGlyph.VerticalAlignment <- VerticalAlignment.Center
        let leading = Ui.hstack Tokens.space2 [ archiveGlyph; label :> Control ]
        leading.VerticalAlignment <- VerticalAlignment.Center
        DockPanel.SetDock(chevronGlyph, Dock.Right)
        let row = DockPanel(LastChildFill = true)
        row.Children.Add chevronGlyph
        row.Children.Add leading
        row.VerticalAlignment <- VerticalAlignment.Center
        let host =
            ActionBorder(
                Padding = Thickness(Tokens.space3, ControlMetrics.sidebarRowPaddingY),
                CornerRadius = CornerRadius Tokens.radiusMd,
                Background = Brushes.Transparent,
                Cursor = handCursor,
                Focusable = true,
                MinHeight = ControlMetrics.sidebarRowMinHeight,
                Child = row)
        archivedToggleHost <- Some(host :> Border)
        // 与其它可交互表面一致：底色补间走共享表面过渡（减弱动效瞬时到位，不改几何）。
        host.Transitions <- Ui.surfaceTransitions ()
        host.DetachedFromVisualTree.Add(fun _ ->
            match archivedToggleHost with
            | Some current when obj.ReferenceEquals(current, host) -> archivedToggleHost <- None
            | _ -> ())
        let accessibleName =
            if expanded then "收起已归档会话"
            // V26: Name 包含可见原文 labelText（含计数），再补“点击展开”，不重复计数。
            else sprintf "%s，点击展开" labelText
        Avalonia.Automation.AutomationProperties.SetName(host, accessibleName)
        Avalonia.Automation.AutomationProperties.SetRole(host, AutomationRole.Button)
        Avalonia.Automation.AutomationProperties.SetExpanded(host, expanded)
        ToolTip.SetTip(host, accessibleName)
        host.PointerEntered.Add(fun _ -> host.Background <- Tokens.hover)
        // 按压反馈与 Ui.attachSurfaceFeedback 同一节奏：瞬时 Opacity 脉冲，不进过渡集合，焦点/几何不变。
        host.PointerPressed.Add(fun _ -> host.Opacity <- Tokens.opacityPressed)
        host.PointerReleased.Add(fun _ -> host.Opacity <- 1.0)
        host.PointerExited.Add(fun _ ->
            host.Opacity <- 1.0
            if not host.IsFocused then host.Background <- Brushes.Transparent)
        host.GotFocus.Add(fun _ -> host.Background <- Tokens.hover)
        host.LostFocus.Add(fun _ -> host.Background <- Brushes.Transparent)
        // Ui.onClick 已接管 Enter/Space；切换会触发 Rebuild 重建本行，
        // Post 回来把焦点还给新行，键盘连按展开/收起不断线。
        Ui.onClick host (fun () ->
            actions.toggleArchivedVisibility ()
            Dispatcher.UIThread.Post(fun () ->
                match archivedToggleHost with
                | Some current -> current.Focus NavigationMethod.Directional |> ignore
                | None -> ()))
        host.KeyDown.Add(fun e ->
            if e.Key = Key.Up then
                e.Handled <- true
                if visibleRowIds.Length > 0 then
                    this.FocusRowAt(visibleRowIds.Length - 1)
                else
                    this.FocusSearch(selectAll = false)
            elif e.Key = Key.Down then
                // 已经是列表最后一项：吞掉，避免 ListBox 默认导航把焦点吸走。
                e.Handled <- true
            elif e.Key = Key.Home then
                e.Handled <- true
                if visibleRowIds.Length > 0 then this.FocusRowAt 0
                else this.FocusSearch(selectAll = false)
            elif e.Key = Key.End then
                e.Handled <- true
            elif e.Key = Key.Escape then
                if not (String.IsNullOrEmpty searchBox.Text) then
                    e.Handled <- true
                    this.ResetSearch true)
        host :> Control

    member private _.PreviewTextFor(summary: ConversationSummary) =
        // 自动标题取自回复首行，预览又从同一段正文开头截取，
        // 于是两行开头一模一样。去掉重复前缀，让预览真的补充信息。
        let withoutTitle =
            let preview = if isNull summary.preview then "" else summary.preview.Trim()
            let title = summary.title.TrimEnd('…').Trim()
            if title.Length >= 4 && preview.StartsWith(title, StringComparison.Ordinal) then
                preview.Substring(title.Length).TrimStart(' ', '·', '—', '-', '，', '。')
            else
                preview
        if not (String.IsNullOrWhiteSpace withoutTitle) then withoutTitle
        elif summary.messageCount > 0 then sprintf "%d 条消息" summary.messageCount
        else "还没有消息"

    member private _.RowTooltipFor(summary: ConversationSummary) =
        let text =
            sprintf
                "%s\n%d 条消息%s"
                summary.title
                summary.messageCount
                (if summary.isFork then " · 分叉会话" else "")
        RowToolTip text

    /// 状态槽内容：running 圆点 / pinned 图标 / idle 留空。槽位本身尺寸不变，
    /// 标题左缘不跟着生成状态漂（keep-clean：state-slot 几何）。
    member private _.SetStateSlot(stateSlot: Border, summary: ConversationSummary) =
        if summary.running then
            stateSlot.Child <-
                Ellipse(
                    Width = ControlMetrics.sidebarRunningDotSize,
                    Height = ControlMetrics.sidebarRunningDotSize,
                    Fill = Tokens.accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center)
        elif summary.pinned then
            let pin = Icons.pin Tokens.textFaint
            pin.Width <- ControlMetrics.sidebarStateGlyphSize
            pin.Height <- ControlMetrics.sidebarStateGlyphSize
            pin.HorizontalAlignment <- HorizontalAlignment.Center
            pin.VerticalAlignment <- VerticalAlignment.Center
            stateSlot.Child <- pin
        else
            stateSlot.Child <- null

    /// 行“更多操作”按钮的可见反馈。桌面与 compact/touch 同一行为：按钮槽位常驻，
    /// 常驻一档淡显，hover/focus 时回全不透明度，其余时刻是 opacitySubtle
    ///（与消息操作条常驻淡显同一档），不抢标题注意力。
    ///
    /// 原桌面走 hover 才现（Ui.setReservedActionVisible：闲置时不透明度 0、不可命中、
    /// 读屏不可见），扫视列表时看不出每行还藏着六项操作。改为常驻淡显后，可发现性
    /// 不再依赖 hover，且保持“存在但不喧宾夺主”的视觉重量。
    /// compact/touch 单独成立的理由（触屏没有 hover、没有右键、没有 Shift+F10，行上下文
    /// 菜单只能由这一个按钮触达）并入本统一路径：始终可命中、可聚焦、可被读屏发现。
    /// visible = true 即行正在被交互（hover/focus），按钮回全不透明；
    /// false 是常驻淡显，但依然可命中。
    /// 命中面不随本统一：仍按模式分档（RenderRow 初始值 / SetCompactMode 同步），
    /// compact/touch 用 LayoutPolicy.compactActionTarget（触控下限），
    /// 桌面用 Tokens.iconButton——常驻可点不等于抬高行高，扫视密度优先。
    member private _.ApplyRowActionVisibility(moreButton: Border) (visible: bool) =
        moreButton.IsVisible <- true
        moreButton.Opacity <- if visible then 1.0 else Tokens.opacitySubtle
        moreButton.IsHitTestVisible <- true
        moreButton.Focusable <- true
        Avalonia.Automation.AutomationProperties.SetAccessibilityView(
            moreButton,
            Avalonia.Automation.AccessibilityView.Default)

    /// 一行会话。选中态用强调色浅底 + 左缘，生成中是一个静态圆点（无动画，避免列表常驻动效）。
    member private this.RenderRow(summary: ConversationSummary) : Control =

        let title =
            TextBlock(
                Text = summary.title,
                FontSize = Tokens.fontSmall,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center)
        let preview =
            TextBlock(
                Text = this.PreviewTextFor summary,
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textFaint,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                // 预览左缘与标题文本左缘对齐：缩进恰好是状态槽宽 + 槽间距。
                Margin = Thickness(ControlMetrics.sidebarStateSlotWidth + Tokens.space2, 0.0, 0.0, 0.0))
        let titleRow = DockPanel(LastChildFill = true, VerticalAlignment = VerticalAlignment.Center)
        let stateSlot =
            Border(
                Width = ControlMetrics.sidebarStateSlotWidth,
                Height = ControlMetrics.sidebarStateGlyphSize,
                Margin = Thickness(0.0, 0.0, Tokens.space2, 0.0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left)
        this.SetStateSlot(stateSlot, summary)
        DockPanel.SetDock(stateSlot, Dock.Left)
        titleRow.Children.Add stateSlot
        let moreButton = Ui.iconButton Icons.more "更多操作"

        ToolTip.SetTip(moreButton, "更多操作")
        Avalonia.Automation.AutomationProperties.SetName(moreButton, sprintf "会话“%s”的操作菜单" summary.title)
        Avalonia.Automation.AutomationProperties.SetHelpText(moreButton, "打开会话操作菜单")
        // 可见性两种模式统一：常驻弱显 + 始终可命中（ApplyRowActionVisibility）。
        // 命中面按模式分档：compact/touch 抬到 compactActionTarget（触控下限，与顶栏
        // 主要图标动作同档），桌面保持 iconButton——只抬命中面，glyph 视觉尺寸不变。
        if compactMode then Ui.setSquareTarget moreButton LayoutPolicy.compactActionTarget
        this.ApplyRowActionVisibility moreButton false
        DockPanel.SetDock(moreButton, Dock.Right)
        titleRow.Children.Add moreButton
        titleRow.Children.Add title
        // 搜索命中高亮放在组装之后：此时 title/preview 的 Text 已定，
        // 填 Inlines 不会干扰随后的结构判断（结构判断按索引不按文本）。
        segmentText title searchQuery summary.title
        segmentText preview searchQuery (this.PreviewTextFor summary)
        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = ControlMetrics.sidebarRowContentSpacing, VerticalAlignment = VerticalAlignment.Center)
        column.Children.Add titleRow
        column.Children.Add preview
        let host =
            ActionBorder(
                Padding = Thickness(Tokens.space3, ControlMetrics.sidebarRowPaddingY),
                CornerRadius = CornerRadius Tokens.radiusMd,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = Thickness(ControlMetrics.sidebarSelectedEdgeWidth, 0.0, 0.0, 0.0),
                Cursor = handCursor,
                Focusable = true,
                MinHeight = ControlMetrics.sidebarRowMinHeight,
                Child = column)
        host.Tag <- summary
        // 状态即描边：选中左缘 2px 强调条本身就是边框态，底色另承载选中/悬停，
        // 因此并入描边补间，与全应用表面反馈同一机制、同一时长（减弱动效瞬时到位，不动几何）。
        host.Transitions <- Ui.surfaceBorderedTransitions ()
        this.ApplyRowState(summary, host)
        rowHosts[summary.id] <- host
        host.DetachedFromVisualTree.Add(fun _ ->
            match rowHosts.TryGetValue summary.id with
            | true, current when obj.ReferenceEquals(current, host) -> rowHosts.Remove summary.id |> ignore
            | _ -> ())
        Avalonia.Automation.AutomationProperties.SetName(host, summary.title)
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
            host,
            Nullable Avalonia.Automation.Peers.AutomationControlType.ListItem)
        let openMenu (target: Control) (alignRight: bool) =
            // 菜单打开瞬间按 id 取最新快照：行渲染时的 summary 可能已被覆盖（如置顶切换后），
            // 标签与动作绝不用渲染期闭包里的旧值。
            let live =
                match summaryById.TryGetValue summary.id with
                | true, current -> current
                | _ -> summary
            Menu.show
                overlay
                target
                alignRight
                [ MenuEntry.create "重命名 (F2)" (fun () -> actions.renameConversation live)
                  |> MenuEntry.withIcon Icons.pencil
                  MenuEntry.create "多选" (fun () -> this.EnterSelection(live.id))
                  |> MenuEntry.withIcon Icons.check
                  MenuEntry.create (if live.pinned then "取消置顶 (P)" else "置顶 (P)") (fun () -> actions.setPinned live (not live.pinned))
                  |> MenuEntry.withIcon Icons.pin
                  MenuEntry.create (if live.archived then "取消归档" else "归档") (fun () -> actions.setArchived live (not live.archived))
                  |> MenuEntry.withIcon Icons.archive
                  MenuEntry.create "从此分叉" (fun () -> actions.duplicateAsFork live) |> MenuEntry.withIcon Icons.fork
                  MenuEntry.create "导出为 Markdown" (fun () -> actions.exportConversation live) |> MenuEntry.withIcon Icons.download
                  MenuEntry.create "删除 (Delete)" (fun () -> actions.deleteConversation live)
                  |> MenuEntry.withIcon Icons.trash
                  |> MenuEntry.asDanger ]
        Ui.onClick moreButton (fun () -> openMenu (moreButton :> Control) true)
        moreButton.GotFocus.Add(fun _ ->
            this.ApplyRowActionVisibility moreButton true)
        moreButton.LostFocus.Add(fun _ ->
            if not host.IsFocused then this.ApplyRowActionVisibility moreButton false)
        host.PointerEntered.Add(fun _ ->
            // 悬停只动背景：选中行的强调左缘不动；悬停在已选项上层叠高亮，绝不比已选项更暗。
            let isActive = activeId = Some summary.id
            let isSelected = selectionMode && selectedIds.ContainsKey summary.id
            host.Background <-
                // 悬停叠层与按钮/菜单 hover 同一混合实现（Ui.blendOverlay，Primitives 唯一来源）。
                if isActive || isSelected then Ui.blendOverlay Tokens.selected Tokens.hover
                else Tokens.hover :> IBrush
            this.ApplyRowActionVisibility moreButton true)
        // 按压反馈与 Ui.attachSurfaceFeedback 同一节奏：瞬时 Opacity 脉冲（不进过渡集合），
        // 焦点环、选中左缘与几何一概不动。
        host.PointerPressed.Add(fun _ -> host.Opacity <- Tokens.opacityPressed)
        host.PointerReleased.Add(fun _ -> host.Opacity <- 1.0)
        host.PointerExited.Add(fun _ ->
            // 离开先复位按压透明度，再还原 hover 底（走 id + 实时快照，不用渲染期旧 summary）。
            host.Opacity <- 1.0
            this.ApplyRowState(summary.id, host)
            if not moreButton.IsFocused then this.ApplyRowActionVisibility moreButton false)
        host.GotFocus.Add(fun _ ->
            focusedRowId <- Some summary.id
            focusedRowIndex <-
                visibleRowIds
                |> Array.tryFindIndex ((=) summary.id)
                |> Option.defaultValue focusedRowIndex
            this.ApplyRowState(summary.id, host)
            if activeId <> Some summary.id then host.Background <- Tokens.hover
            this.ApplyRowActionVisibility moreButton true)
        host.LostFocus.Add(fun _ ->
            this.ApplyRowState(summary.id, host)
            if not moreButton.IsFocused then this.ApplyRowActionVisibility moreButton false)
        Ui.onClick host (fun () ->
            if selectionMode then this.ToggleSelected summary.id
            else actions.openConversation summary.id)
        // Enter/Space 由 Ui.onClick 统一接管（同一按键只打开一次）；
        // 这里只处理行内导航与行级快捷键。
        host.KeyDown.Add(fun e ->
            if e.Key = Key.F2 then
                e.Handled <- true
                match summaryById.TryGetValue summary.id with
                | true, live -> actions.renameConversation live
                | _ -> actions.renameConversation summary
            elif e.Key = Key.Delete then
                // 选择模式下 Delete 走批量删除，与底部操作条同一动作。
                if selectionMode then
                    e.Handled <- true
                    this.DeleteSelected()
                else
                e.Handled <- true
                match summaryById.TryGetValue summary.id with
                | true, live -> actions.deleteConversation live
                | _ -> actions.deleteConversation summary
            elif e.Key = Key.P && not (e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta) then
                // 置顶此前只有右键菜单一条路：行已拿到焦点时还要再唤菜单。
                // 与 F2 / Delete 同一约定——Rename 用它、Delete 用它，
                // 取 summaryById 里的活摘要，不信任渲染时那一版快照。
                e.Handled <- true
                match summaryById.TryGetValue summary.id with
                | true, live -> actions.setPinned live (not live.pinned)
                | _ -> actions.setPinned summary (not summary.pinned)
            elif selectionMode && e.Key = Key.Escape then
                // 搜索框为空时 Escape 退出批量模式：焦点留在当前行，选择立刻清零。
                if String.IsNullOrEmpty searchBox.Text then
                    e.Handled <- true
                    this.ExitSelection()
            elif e.Key = Key.Down then
                e.Handled <- true
                this.MoveRowFocus(summary.id, 1)
            elif e.Key = Key.Up then
                e.Handled <- true
                match visibleRowIds |> Array.tryFindIndex ((=) summary.id) with
                | Some 0 -> this.FocusSearch(selectAll = false)
                | _ -> this.MoveRowFocus(summary.id, -1)
            elif e.Key = Key.Escape then
                if not (String.IsNullOrEmpty searchBox.Text) then
                    e.Handled <- true
                    this.ResetSearch true
            elif e.Key = Key.Home then
                e.Handled <- true
                this.FocusRowAt 0
            elif e.Key = Key.End then
                e.Handled <- true
                // End 落到列表最后一项：已归档开关在时就是它。
                if hasArchivedToggle then this.FocusArchivedToggle()
                elif visibleRowIds.Length > 0 then this.FocusRowAt(visibleRowIds.Length - 1)

            elif (e.Key = Key.F10 && e.KeyModifiers.HasFlag KeyModifiers.Shift) || e.Key = Key.Apps then
                e.Handled <- true
                openMenu (host :> Control) false)
        host.PointerPressed.Add(fun e ->
            let props = e.GetCurrentPoint(host).Properties
            if props.IsRightButtonPressed then
                host.Focus NavigationMethod.Pointer |> ignore
                this.ApplyRowState(summary.id, host))

        host.PointerReleased.Add(fun e ->
            if e.InitialPressMouseButton = MouseButton.Right then
                e.Handled <- true
                host.Focus NavigationMethod.Pointer |> ignore
                this.ApplyRowState(summary.id, host)
                openMenu (host :> Control) false)

        ToolTip.SetTip(
            host,
            this.RowTooltipFor summary)
        host :> Control

    /// 回收复用：同一会话的新快照直接刷标题/预览/状态槽/提示，不重建事件链。

    /// 结构对不上就返回 false，调用方回退到 RenderRow。
    member private this.RefreshRowHost(host: Border, summary: ConversationSummary) : bool =
        host.Tag <- summary
        rowHosts[summary.id] <- host
        match host.Child with
        | :? StackPanel as column when column.Children.Count >= 2 ->
            match column.Children.[0] with
            | :? DockPanel as titleRow when titleRow.Children.Count >= 3 ->
                match titleRow.Children.[2] with
                | :? TextBlock as titleBlock ->
                    match column.Children.[1] with
                    | :? TextBlock as previewBlock ->
                        match titleRow.Children.[0] with
                        | :? Border as stateSlot ->
                            titleBlock.Text <- summary.title
                            previewBlock.Text <- this.PreviewTextFor summary
                            // 赋 Text 会整块替换 Inlines，高亮必须在赋值之后重放：
                            // 只在新行渲染时上色，回收复用的行就会丢高亮，
                            // 表现为「搜索框里敲了字，列表一刷新高亮就消失」。
                            segmentText titleBlock searchQuery summary.title
                            segmentText previewBlock searchQuery (this.PreviewTextFor summary)
                            this.SetStateSlot(stateSlot, summary)
                            this.ApplyRowState(summary, host)
                            Avalonia.Automation.AutomationProperties.SetName(host, summary.title)
                            ToolTip.SetTip(
                                host,
                                this.RowTooltipFor summary)
                            true
                        | _ -> false
                    | _ -> false
                | _ -> false
            | _ -> false
        | _ -> false

    /// 已归档开关复用：只刷文案/人字形/无障碍名，展开/收起的点击与键盘处理与状态无关，沿用旧链。
    member private _.RefreshArchivedToggle(host: Border, expanded: bool, count: int) =
        let labelText = if expanded then "收起已归档会话" else sprintf "已归档会话 (%d)" count
        match host.Child with
        | :? DockPanel as row when row.Children.Count >= 2 ->
            match row.Children.[1] with
            | :? StackPanel as leading when leading.Children.Count >= 2 ->
                match leading.Children.[1] with
                | :? TextBlock as label -> label.Text <- labelText
                | _ -> ()
            | _ -> ()
            let chevron = (if expanded then Icons.chevronUp else Icons.chevronDown) Tokens.textMuted
            chevron.Width <- Tokens.iconGlyph
            chevron.Height <- Tokens.iconGlyph
            chevron.VerticalAlignment <- VerticalAlignment.Center
            DockPanel.SetDock(chevron, Dock.Right)
            row.Children.RemoveAt(0)
            row.Children.Insert(0, chevron)
        | _ -> ()
        archivedToggleHost <- Some host
        let accessibleName =
            if expanded then "收起已归档会话"
            else sprintf "%s，点击展开" labelText
        Avalonia.Automation.AutomationProperties.SetName(host, accessibleName)
        Avalonia.Automation.AutomationProperties.SetExpanded(host, expanded)
        ToolTip.SetTip(host, accessibleName)

    member private this.Rebuild() =
        // Rebuild 会整体替换 ItemsSource：焦点若在某行上先记住，刷新后仍可见就还回去；
        // 搜索框里打字时焦点不在行上，此时绝不抢焦点。
        let restoreFocusId =
            match focusedRowId with
            | Some id ->
                match rowHosts.TryGetValue id with
                | true, host when host.IsFocused -> Some id
                | _ -> None
            | None -> None

        // 快照刷新不许动滚动：换 ItemsSource 会把 ScrollViewer 偏移清零，
        // 先记住、换完原样贴回去（同步一次即可；异步再贴会压掉后续的焦点跟随滚动）。
        let scroller =
            conversationList.GetVisualDescendants()
            |> Seq.tryPick (function
                | :? ScrollViewer as sv -> Some sv
                | _ -> None)
        let savedOffset = scroller |> Option.map (fun sv -> sv.Offset)
        rowHosts.Clear()
        flatIndexByConversation.Clear()
        summaryById.Clear()
        for s in summaries do
            summaryById[s.id] <- s
        let query = if isNull searchBox.Text then "" else searchBox.Text
        searchQuery <- query
        let visible =
            summaries
            |> List.filter (fun s -> showArchived || not s.archived)
            |> List.filter (ConversationSummary.matches query)
        let groups = ConversationSummary.group DateTimeOffset.Now visible
        let flattened = ResizeArray<SidebarListItem>()
        for group in groups do
            flattened.Add(SectionHeader group.label)
            for item in group.items do
                flatIndexByConversation[item.id] <- flattened.Count
                flattened.Add(ConversationRow item)
        let archived = summaries |> List.filter (fun s -> s.archived)
        archivedCount <- archived.Length
        // 折叠时它是展开入口，展开时它是收起入口：两种状态都留在列表末尾，
        // 键盘 Up/Down 才能在行与它之间连续走通，展开后也有地方收起。

        hasArchivedToggle <- not (List.isEmpty archived)
        archivedToggleHost <- None
        archivedToggleFlatIndex <- None
        if hasArchivedToggle then
            archivedToggleFlatIndex <- Some flattened.Count
            flattened.Add ArchivedToggle
        conversationList.ItemsSource <- flattened
        visibleRowIds <-
            flattened
            |> Seq.choose (fun item ->
                if item.isItem then
                    match item with
                    | ConversationRow summary -> Some summary.id
                    | _ -> None
                else None)
            |> Array.ofSeq
        savedOffset
        |> Option.iter (fun offset ->
            match scroller with
            | Some sv -> sv.Offset <- offset
            | None -> ())
        // 删除流先记的邻位意图优先于常规焦点还原；消费一次即清。
        let targetRestoreId =
            match pendingFocusId with
            | Some id when visibleRowIds |> Array.contains id ->
                pendingFocusId <- None
                Some id
            | Some _ ->
                pendingFocusId <- None
                None
            | None -> None
        let targetRestoreId =
            match targetRestoreId, restoreFocusId with
            | Some id, _ -> Some id
            | None, Some id when visibleRowIds |> Array.contains id -> Some id
            | _ -> None
        match targetRestoreId with

        | Some id ->
            Dispatcher.UIThread.Post(
                (fun () ->
                    match rowHosts.TryGetValue id with
                    | true, host when not host.IsFocused -> host.Focus(NavigationMethod.Directional) |> ignore
                    | _ -> ()),
                DispatcherPriority.Input)
        | None -> ()
        let isSearchEmpty = not (String.IsNullOrWhiteSpace query) && List.isEmpty visible
        searchEmptyHint.IsVisible <- isSearchEmpty
        emptyStateHost.IsVisible <- List.isEmpty visible && not isSearchEmpty
        if List.isEmpty visible then
            // 空态三态互斥：未连接 / 列表加载中 / 确认无会话。
            // 「加载中」只在快照未达时出现——没有快照就没有资格宣判「还没有会话」。
            let setEmptyAction (label: string) (action: unit -> unit) =
                emptyPrimaryAction <- action
                Ui.setButtonText emptyActionButton label
                ToolTip.SetTip(emptyActionButton, label)
                Avalonia.Automation.AutomationProperties.SetName(emptyActionButton, label)
                Avalonia.Automation.AutomationProperties.SetHelpText(emptyActionButton, label)
                emptyActionButton.IsVisible <- true
            let variant =
                if not connected then EmptyVariant.NotConnected
                elif listLoading then EmptyVariant.ListLoading
                else EmptyVariant.NoConversations
            if currentEmptyVariant <> Some variant then
                // 种类翻转才换卡：同种类内的数据刷新不重建空态树，按钮焦点与读屏名
                // 跨刷新保持连续（Ui.emptyState 内按卡新建 logo/标题/说明文本）。
                currentEmptyVariant <- Some variant
                // 骨架与行动按钮是跨卡复用的持久实例：摘卡前先从旧卡摘下，
                // 否则 Avalonia 拒绝把已有 visual parent 的控件挂进新卡。
                let detachShared (control: Control) =
                    match control.Parent with
                    | :? Panel as panel -> panel.Children.Remove control |> ignore
                    | _ -> ()
                detachShared emptySkeleton
                detachShared emptyActionButton
                let logo = brandLogo Tokens.logoEmpty
                match variant with
                | EmptyVariant.NotConnected ->
                    emptySkeleton.IsVisible <- false
                    // V20: 与 ChatView NotConnected 同句式，不在 Sidebar 另造术语。
                    // 内嵌入口与底部状态行重连是同一动作，不新造连接路径。
                    setEmptyAction "连接服务器" actions.reconnect
                    emptyStateHost.Child <-
                        Ui.emptyState logo (Some "先连接一台万象服务器") "连接后即可看到会话记录。" (Some(emptySkeleton :> Control)) (Some emptyActionButton)
                | EmptyVariant.ListLoading ->
                    emptySkeleton.IsVisible <- true
                    // 行动按钮保持挂载但隐藏：读屏与自动化树里它始终在场（跨空态
                    // 种类焦点连续），只是加载中没有可执行动作。
                    emptyActionButton.IsVisible <- false
                    emptyStateHost.Child <-
                        Ui.emptyState logo (Some "正在加载会话…") "" (Some(emptySkeleton :> Control)) (Some emptyActionButton)
                | EmptyVariant.NoConversations ->
                    emptySkeleton.IsVisible <- false
                    // 内嵌入口与右上角常驻加号是同一动作，不新造新建路径。
                    setEmptyAction "新建会话" actions.newConversation
                    emptyStateHost.Child <-
                        Ui.emptyState logo (Some "还没有会话") "点右上角的加号开始第一次对话。" (Some(emptySkeleton :> Control)) (Some emptyActionButton)

    /// 更新列表数据。快照数据到达即加载结束：listLoading 只在此处随数据解除，
    /// 与 AppShell 置位点（发出 ObserveConversationList）构成单一写入链。
    member this.SetConversations(items: ConversationSummary list) =
        summaries <- items
        listLoading <- false
        this.Rebuild()

    member this.SetActive(id: Guid option) =
        if activeId <> id then
            let previous = activeId
            activeId <- id
            let refresh key =
                match key with
                | Some value ->
                    match rowHosts.TryGetValue value with
                    | true, host -> this.ApplyRowState(value, host)
                    | _ -> ()
                | None -> ()
            refresh previous
            refresh id
            // 从外部切换当前会话（Ctrl+1..9 / 搜索结果 / 新建）后，选中行若在视口外，
            // 侧栏上看不到正在进行的会话在哪里。这里补一次滚动，只滚屏不抢焦点：
            // 焦点归属照旧由调用方决定（Ctrl+数字进聊天区，搜索结果本就在列表里）。
            // 借 kelivo side_drawer 的 keep-selected-item-visible：选中项永远在视口内。
            match id with
            | Some targetId ->
                match flatIndexByConversation.TryGetValue targetId with
                | true, flatIndex -> conversationList.ScrollIntoView flatIndex
                | _ -> ()
            | None -> ()

    member this.SetShowArchived(value: bool) =
        if showArchived <> value then
            showArchived <- value
            this.Rebuild()

    member this.SetConnection(isConnected: bool, text: string) =
        connected <- isConnected
        // 连接状态翻转（含断连清空列表的既有路径）时，任何在途的列表加载都已失效：
        // 快照只可能属于某一代连接，跨连接的 loading 不成立。
        listLoading <- false
        statusDot.Fill <- if isConnected then Tokens.success :> IBrush else Tokens.textFaint :> IBrush
        statusText.Text <- text
        this.Rebuild()

    /// 会话列表加载中显式机制：AppShell 发出 ObserveConversationList 时置位。
    /// 解除走 SetConversations（快照到达）/ SetConnection（连接翻转），不在此重复清除。
    member this.SetConversationListLoading(isLoading: bool) =
        if listLoading <> isLoading then
            listLoading <- isLoading
            this.Rebuild()

    member this.SetCompactMode(value: bool) =
        compactMode <- value
        compactBackButton.IsVisible <- value
        let size = if value then LayoutPolicy.compactActionTarget else Tokens.iconButton
        for button in [ compactBackButton; newConversationButton; settingsButton; clearSearchButton ] do
            Ui.setSquareTarget button size
        // 已渲染的行同步 compact 态：行菜单按钮常驻弱显（可见性两种模式同一路径），
        // 命中面仍按模式分档（compact 抬到 compactActionTarget，桌面回 iconButton）。
        // 同时按当前 hover/focus 快照刷新不透明度。按钮槽位本来常驻，标题不跳动。
        for host in rowHosts.Values do
            match host.Child with
            | :? StackPanel as column when column.Children.Count >= 2 ->
                match column.Children.[0] with
                | :? DockPanel as titleRow when titleRow.Children.Count >= 3 ->
                    match titleRow.Children.[1] with
                    | :? Border as rowMoreButton ->
                        Ui.setSquareTarget rowMoreButton (if value then LayoutPolicy.compactActionTarget else Tokens.iconButton)
                        this.ApplyRowActionVisibility
                            rowMoreButton
                            (rowMoreButton.IsPointerOver || host.IsPointerOver || rowMoreButton.IsFocused || host.IsFocused)
                    | _ -> ()
                | _ -> ()
            | _ -> ()

    /// 搜索框聚焦。行内 Up 回来时保留插入点（selectAll = false）；
    /// Ctrl+K（AppShell）与空输入沿用全选，方便直接替换查询。
    member _.FocusSearch(?selectAll: bool) =
        let select = defaultArg selectAll true
        searchBox.Focus() |> ignore
        if select || String.IsNullOrEmpty searchBox.Text then
            searchBox.SelectAll()

    /// 契约 C2：当前从上到下展示的会话行 id（字符串），供 ShellFix Ctrl+1..9 索引。
    member _.GetVisibleOrder() : string list =
        visibleRowIds |> Array.map (fun id -> id.ToString()) |> Array.toList

    /// 契约 C3：删除后聚焦邻位（下一行优先），行空了就回搜索框。
    /// 删除前调用（id 还在）直接瞄邻位并记下意图，Rebuild 后补聚焦；
    /// 删除后调用（id 已不在）按上次行焦点位置钳制。
    /// 邻位口径与 FocusAfterDelete 完全一致：同一个 neighborIndex，
    /// 不另写一份挑选规则——两份规则必然漂移。
    member this.NeighborAfterDelete(removedId: string) : Guid option =
        this.NeighborIndexAfterDelete(removedId)
        |> Option.map (fun idx -> visibleRowIds.[idx])

    /// 邻位在 visibleRowIds 里的下标（下一行优先，其次上一行）；没有行时为 None。
    /// 抽出来是为了让「删除后打开谁」与「删除后焦点落谁」读同一个答案。
    member private this.NeighborIndexAfterDelete(removedId: string) : int option =
        if visibleRowIds.Length = 0 then
            None
        else
            match Guid.TryParse removedId with
            | true, removed ->
                match visibleRowIds |> Array.tryFindIndex ((=) removed) with
                | Some idx ->
                    if idx + 1 < visibleRowIds.Length then Some(idx + 1)
                    elif idx - 1 >= 0 then Some(idx - 1)
                    else None
                | None ->
                    Some(min (max focusedRowIndex 0) (visibleRowIds.Length - 1))
            | _ ->
                Some(min (max focusedRowIndex 0) (visibleRowIds.Length - 1))

    member this.FocusAfterDelete(removedId: string) =
        match this.NeighborIndexAfterDelete(removedId) with
        | Some idx ->
            let id = visibleRowIds.[idx]
            pendingFocusId <- Some id
            focusedRowId <- Some id
            focusedRowIndex <- idx
            this.FocusRowAt idx
        | None ->
            this.FocusSearch(selectAll = false)

    /// 进入批量选择模式：以 seedId 为第一项选中，底部换成批量操作条。
    /// 已在模式内则只把 seedId 并入选择（菜单入口可重复点）。
    member this.EnterSelection(seedId: Guid) =
        if not selectionMode then
            selectionMode <- true
            selectedIds.Clear()
            selectedIds.[seedId] <- ()
            this.RefreshSelectionChrome()
        elif not (selectedIds.ContainsKey seedId) then
            selectedIds.[seedId] <- ()
            this.RefreshSelectionChrome()

    /// 切换某一行的选中态。清空最后一项时退出模式——空选择留着操作条没有意义。
    member this.ToggleSelected(id: Guid) =
        if selectedIds.ContainsKey id then selectedIds.Remove id |> ignore
        else selectedIds.[id] <- ()
        if selectedIds.Count = 0 then this.ExitSelection()
        else this.RefreshSelectionChrome()

    /// 退出批量选择模式并清空选择。焦点不抢：留在当前行，操作条收起不动布局。
    member this.ExitSelection() =
        selectionMode <- false
        selectedIds.Clear()
        this.RefreshSelectionChrome()

    /// 全选 / 取消全选当前可见行。
    member this.ToggleSelectAllVisible() =
        let allSelected =
            visibleRowIds.Length > 0
            && visibleRowIds |> Array.forall selectedIds.ContainsKey
        if allSelected then this.ExitSelection()
        else
            if not selectionMode then selectionMode <- true
            selectedIds.Clear()
            for id in visibleRowIds do selectedIds.[id] <- ()
            this.RefreshSelectionChrome()

    /// 供 AppShell 读取当前选择（顺序与可见行一致，删除后可预期邻位行为）。
    member _.SelectedIds() : Guid list =
        visibleRowIds
        |> Array.filter selectedIds.ContainsKey
        |> Array.toList

    member _.IsSelectionMode = selectionMode

    /// 刷新批量操作条与计数文本；行视觉由各行事件里的 ApplyRowState 同步。
    member private this.RefreshSelectionChrome() =
        let count = selectedIds.Count
        selectionCountText.Text <- sprintf "已选 %d 项" count
        // 两个键的文案是当前选择的状态，不是固定标签：全选后同一键变成「取消全选」、
        // 全已置顶后变成「取消置顶」。此前两者只在 Build 时算一次，用户勾选/反选
        // 之后看到的仍是旧文案，再点会得到与预期相反的动作。
        Ui.setButtonText selectAllButton
            (if visibleRowIds.Length > 0 && visibleRowIds |> Array.forall selectedIds.ContainsKey then "取消全选" else "全选")
        Ui.setButtonText pinButton
            (if visibleRowIds.Length > 0 && visibleRowIds |> Array.forall selectedIds.ContainsKey && visibleRowIds.Length > 0
               && actions.selectionAllPinned(this.SelectedIds()) then "取消置顶" else "置顶")
        // 读屏与悬停提示跟着同一份状态走：文案换了，自动化名还是旧的话等于报错。
        Avalonia.Automation.AutomationProperties.SetHelpText(
            selectAllButton,
            if visibleRowIds.Length > 0 && visibleRowIds |> Array.forall selectedIds.ContainsKey then
                "取消全选（保留多选状态）"
            else "全选当前可见会话")
        selectionHeader.IsVisible <- selectionMode
        selectionActionBar.IsVisible <- selectionMode
        // 已渲染的行必须立刻重绘：选中/取消选中如果只改集合不改行视觉，
        // 用户点了行却看不出任何变化（ hover / 焦点事件不会为纯数据变化补刷）。
        for row in rowHosts do this.ApplyRowState(row.Key, row.Value)
        // 选择模式只留「取消 / 全选 / 计数」与底部批量条：品牌行与页脚让位，
        // 侧栏本来只有 232–420pt 宽，再叠两条常驻条会把列表压成几条。
        brandHeader.IsVisible <- not selectionMode
        sidebarFooter.IsVisible <- not selectionMode

    /// 批量删除：AppShell 负责逐个发命令与确认；这里只把选择交出去。
    member private this.DeleteSelected() = actions.deleteMany(this.SelectedIds())

    member this.Build() =
        this.Background <- Tokens.rail
        this.BorderBrush <- Tokens.borderSoft
        this.BorderThickness <- Thickness(0.0, 0.0, ControlMetrics.sidebarDividerWidth, 0.0)


        let brand = brandLogo Tokens.logoSidebar
        brand.VerticalAlignment <- VerticalAlignment.Center
        let wordmark =
            TextBlock(
                Text = "万象",
                FontSize = Tokens.fontBody,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text,
                LetterSpacing = Tokens.letterSpacingDisplay,
                VerticalAlignment = VerticalAlignment.Center)
        let brandRow = Ui.hstack Tokens.space2 [ brand; wordmark :> Control ]
        let leading = Ui.hstack Tokens.space1 [ compactBackButton :> Control; brandRow :> Control ]
        brandHeader <-
            let dock = DockPanel(LastChildFill = false, VerticalAlignment = VerticalAlignment.Center)
            DockPanel.SetDock(leading, Dock.Left)
            DockPanel.SetDock(newConversationButton, Dock.Right)
            dock.Children.Add leading
            dock.Children.Add newConversationButton
            Border(
                Height = Tokens.barHeight,
                Padding = Thickness(Tokens.space4, 0.0),
                Child = dock)

        searchBox.KeyDown.Add(fun e ->
            if e.Key = Key.Down then
                if visibleRowIds.Length > 0 then
                    e.Handled <- true
                    this.FocusRowAt 0
                elif hasArchivedToggle then
                    // 零结果也先落到已归档开关，键盘不断线。
                    e.Handled <- true
                    this.FocusArchivedToggle()
                elif searchEmptyHint.IsVisible then
                    e.Handled <- true
                    searchEmptyHint.Focus NavigationMethod.Directional |> ignore
            elif e.Key = Key.Enter then
                if visibleRowIds.Length > 0 then
                    e.Handled <- true
                    actions.openConversation visibleRowIds.[0]
                    // 焦点跟随选择：桌面回首行；compact 下 AppShell 收抽屉并把焦点交还输入区。
                    if not compactMode then
                        this.FocusRowAt 0
                elif searchEmptyHint.IsVisible then
                    // 无结果时 Enter 把焦点送到空态提示（再按 Enter/Space 清空），键盘不断线。
                    e.Handled <- true
                    searchEmptyHint.Focus NavigationMethod.Directional |> ignore
            elif e.Key = Key.Escape then
                e.Handled <- true
                if not (String.IsNullOrEmpty searchBox.Text) then
                    this.ResetSearch true
                else
                    this.FocusActiveConversation())
        searchDebounce.Tick.Add(fun _ ->
            searchDebounce.Stop()
            this.Rebuild()
            // 结果刷新不碰滚动：首项可见由 Down 通路的 FocusRowAt/BringIntoView 保证。
            ())
        searchBox.TextChanged.Add(fun _ ->
            Ui.setReservedActionVisible clearSearchButton (not (String.IsNullOrWhiteSpace searchBox.Text))
            searchDebounce.Stop()
            searchDebounce.Start())
        // 搜索输入归入「分组容器」：用 surfaceContainer 从 rail 中轻轻抬起一块次级面，
        // 顶部补一档呼吸，与上方品牌行拉开距离（输入控件本体仍是冻结的 Ui.textField）。
        // 有意不加边框：这是「无描边」分组面的例外（区别于 Ui.groupingCard 的发丝边档）。
        let searchArea =
            Border(
                Background = Tokens.surfaceContainer,
                Padding = Thickness(Tokens.space3, Tokens.space3, Tokens.space3, Tokens.space2),
                Child = searchShell)
        let statusRow =
            ActionBorder(
                Background = Brushes.Transparent,
                CornerRadius = CornerRadius Tokens.radiusSm,
                Cursor = handCursor,
                Focusable = true,
                Child = Ui.hstack Tokens.space2 [ statusDot :> Control; statusText :> Control ])
        // 状态行本身是可点击的重连入口：补上悬停/聚焦底色反馈，补间走共享表面过渡
        //（减弱动效瞬时到位；命中测试与「点击重连」行为都保持不变）。
        statusRow.Transitions <- Ui.surfaceTransitions ()
        statusRow.PointerEntered.Add(fun _ -> statusRow.Background <- Tokens.hover)
        statusRow.PointerExited.Add(fun _ ->
            if not statusRow.IsFocused then statusRow.Background <- Brushes.Transparent)
        statusRow.GotFocus.Add(fun _ -> statusRow.Background <- Tokens.hover)
        statusRow.LostFocus.Add(fun _ -> statusRow.Background <- Brushes.Transparent)
        ToolTip.SetTip(statusRow, "点击重新连接")
        Avalonia.Automation.AutomationProperties.SetName(statusRow, "重新连接服务器")
        Ui.onClick statusRow (fun () -> actions.reconnect ())
        sidebarFooter <-
            let dock = DockPanel(LastChildFill = false, VerticalAlignment = VerticalAlignment.Center)
            DockPanel.SetDock(statusRow, Dock.Left)
            DockPanel.SetDock(settingsButton, Dock.Right)
            dock.Children.Add statusRow
            dock.Children.Add settingsButton
            Border(
                Height = Tokens.barHeight,
                Padding = Thickness(Tokens.space4, 0.0, Tokens.space4, 0.0),
                // 页脚是侧栏内部的分隔线，用最轻的 hairline，比外框再退一档。
                BorderBrush = Tokens.hairline,
                BorderThickness = Thickness(0.0, ControlMetrics.sidebarDividerWidth, 0.0, 0.0),
                Child = dock)

        conversationList.ItemsPanel <- FuncTemplate<Panel>(fun () -> VirtualizingStackPanel() :> Panel)
        conversationList.ItemTemplate <-
            FuncDataTemplate<SidebarListItem>(
                (fun item existing ->
                    match item with
                    | SectionHeader label ->
                        // 分组表头：弱化小标签 + 下方一条 hairline 分组分隔线（更轻盈的分组边界）。
                        // 用 Border 包裹只为让分隔线拉满整宽；TextBlock 标签仍可回收——
                        // 复用到同型 TextBlock 直接改文案，遇到异型容器（回收到行容器）则重建。
                        let buildHeader () =
                            let header = Ui.sectionLabelWide label
                            header.Focusable <- false
                            let wrapper =
                                Border(
                                    Background = Brushes.Transparent,
                                    BorderBrush = Tokens.hairline,
                                    BorderThickness = Thickness(0.0, 0.0, 0.0, ControlMetrics.sidebarDividerWidth),
                                    Padding = Thickness(Tokens.space3, Tokens.space3, Tokens.space3, Tokens.space1),
                                    HorizontalAlignment = HorizontalAlignment.Stretch,
                                    Child = header)
                            wrapper :> Control
                        match existing with
                        | :? Border as host ->
                            match host.Child with
                            | :? TextBlock as header -> header.Text <- label; host :> Control
                            | _ -> buildHeader ()
                        | _ -> buildHeader ()
                    | ConversationRow summary ->
                        match existing with
                        | :? Border as host ->
                            match host.Tag with
                            | :? ConversationSummary as old when old.id = summary.id ->
                                // 同一会话的新快照：原地刷，不重建。
                                if this.RefreshRowHost(host, summary) then host :> Control
                                else this.RenderRow summary
                            | _ -> this.RenderRow summary
                        | _ -> this.RenderRow summary

                    | ArchivedToggle ->
                        match existing with
                        | :? Border as host when isNull host.Tag ->
                            this.RefreshArchivedToggle(host, showArchived, archivedCount)
                            host :> Control
                        | _ -> this.RenderArchivedToggle()),
                true)
        conversationList.ContainerPrepared.Add(fun args ->
            match args.Container with
            | :? ListBoxItem as item ->
                item.Focusable <- false
                item.Padding <- Thickness 0.0
                item.Margin <- Thickness 0.0
                item.Background <- Brushes.Transparent
                item.BorderThickness <- Thickness 0.0
                item.HorizontalContentAlignment <- HorizontalAlignment.Stretch
            | _ -> ())
        Avalonia.Automation.AutomationProperties.SetName(conversationList, "会话列表")

        let body = Grid()
        body.Children.Add conversationList
        body.Children.Add emptyStateHost
        body.Children.Add searchEmptyHint

        // 批量选择模式的头 / 底操作条：只切可见性，几何常驻同一位置（顶条替下品牌行
        // 之前它自己那行本来就占 barHeight；底条与页脚同 Dock.Bottom，模式内页脚让位）。
        selectionHeader.Child <-
            let dock = DockPanel(LastChildFill = false, VerticalAlignment = VerticalAlignment.Center)
            let cancelButton = Ui.iconButton Icons.close "退出多选（Esc）"
            Ui.onClick cancelButton (fun () -> this.ExitSelection())
            // 文案是状态：全选后同一键变成「取消全选」，与底部置顶键同语义。
            // 否则用户全选后看到还是「全选」，不知道再点是取消（此前就是取消）。
            selectAllButton <-
                Ui.button Ui.Ghost "全选" (fun () -> this.ToggleSelectAllVisible())
            selectAllButton.Margin <- Thickness 0.0
            selectAllButton.Padding <- Thickness(Tokens.space3, ControlMetrics.selectButtonPaddingY)
            DockPanel.SetDock(cancelButton, Dock.Left)
            DockPanel.SetDock(selectAllButton, Dock.Right)
            dock.Children.Add cancelButton
            dock.Children.Add selectAllButton
            dock.Children.Add selectionCountText
            dock
        selectionActionBar.Child <-
            // 置顶键的文案随当前选择实时判定：全已置顶 → 取消置顶，与单行菜单同语义。
            pinButton <-
                Ui.button Ui.Secondary "置顶"
                    (fun () ->
                        let ids = this.SelectedIds()
                        let pin = not (actions.selectionAllPinned ids)
                        actions.setPinnedMany ids pin)
            let archiveButton =
                Ui.button Ui.Secondary "归档" (fun () ->
                    let ids = this.SelectedIds()
                    actions.setArchivedMany ids true)
            let deleteButton =
                Ui.button Ui.Danger "删除" (fun () -> this.DeleteSelected())
            Ui.hstack
                Tokens.space2
                [ pinButton :> Control; archiveButton :> Control; deleteButton :> Control ]
        let layout = DockPanel()
        DockPanel.SetDock(brandHeader, Dock.Top)
        DockPanel.SetDock(searchArea, Dock.Top)
        DockPanel.SetDock(selectionHeader, Dock.Top)
        DockPanel.SetDock(selectionActionBar, Dock.Bottom)
        DockPanel.SetDock(sidebarFooter, Dock.Bottom)
        layout.Children.Add brandHeader
        layout.Children.Add searchArea
        layout.Children.Add selectionHeader
        layout.Children.Add selectionActionBar
        layout.Children.Add sidebarFooter
        layout.Children.Add body
        this.Child <- layout
        this.Rebuild()
