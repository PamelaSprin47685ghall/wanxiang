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

module private SidebarHelpers =
    let blendOver (baseBrush: IBrush) (overlayBrush: IBrush) : IBrush =
        match baseBrush, overlayBrush with
        | (:? SolidColorBrush as b), (:? SolidColorBrush as o) ->
            let blend (bc: byte) (oc: byte) (alpha: byte) =
                byte (int bc + (int oc - int bc) * int alpha / 255)
            let a = o.Color.A
            SolidColorBrush(Color.FromArgb(b.Color.A, blend b.Color.R o.Color.R a, blend b.Color.G o.Color.G a, blend b.Color.B o.Color.B a))
            :> IBrush
        | _ -> overlayBrush

type private RowToolTip(text: string) as this =
    inherit TextBlock()
    do
        this.Text <- text
        this.FontFamily <- Tokens.fontFamily
    override _.ToString() = text

/// 会话侧栏。
///
/// 旧版是一个平铺 ListBox：没有分组、没有置顶、没有空态、右键只有两项。
/// 这里按时间分组、置顶提前、搜索常驻、每行可右键操作，
/// 并且在「没有会话」「搜不到」「未连接」三种空态下说不同的话。
type Sidebar(overlay: OverlayHost, actions: SidebarActions, brandLogo: float -> Control) as this =
    inherit Border()

    let handCursor = new Cursor(StandardCursorType.Hand)

    let searchShell, searchBox = Ui.textField "搜索会话"
    let clearSearchButton = Ui.iconButton Icons.close "清空搜索"
    let compactBackButton = Ui.iconButton Icons.arrowLeft "返回对话"
    let newConversationButton = Ui.iconButtonAccent Icons.plus "新建会话（Ctrl+N）"
    let settingsButton = Ui.iconButton Icons.gear "设置（Ctrl+,）"
    let searchDebounce = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 160.0)
    let conversationList =
        ListBox(
            Background = Brushes.Transparent,
            BorderThickness = Thickness 0.0,
            Focusable = false)
    let emptyState = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2, IsVisible = false, Margin = Thickness(Tokens.space5, Tokens.space8, Tokens.space5, Tokens.space8), VerticalAlignment = VerticalAlignment.Center)
    let emptyTitle =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontSmall,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.textMuted,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap)
    let emptyHint =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontCaption,
            Foreground = Tokens.textFaint,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = ControlMetrics.sidebarStatusLineHeight)
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

    let statusDot = Ui.statusDot 7.0
    let statusText =
        TextBlock(
            Text = "未连接",
            FontSize = Tokens.fontCaption,
            Foreground = Tokens.textMuted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis)

    let mutable summaries: ConversationSummary list = []
    let mutable activeId: Guid option = None
    let mutable showArchived = false
    let mutable connected = false
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

        emptyState.Children.Add emptyTitle
        emptyState.Children.Add emptyHint
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

    member private _.ApplyRowState(summary: ConversationSummary, host: Border) =
        let isActive = activeId = Some summary.id
        host.Background <- if isActive then Tokens.selected :> IBrush else Brushes.Transparent :> IBrush
        // 键盘焦点环由 ActionBorder 统一绘制（outline 语义、零位移）；
        // 这里只管选中底与左缘，悬停由事件处理补，避免两套阴影互相覆盖。
        host.BorderBrush <- if isActive then Tokens.accent :> IBrush else Brushes.Transparent :> IBrush
        let status =
            match isActive, summary.running with
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
                host.Background <- if isActive then Tokens.selected :> IBrush else Brushes.Transparent :> IBrush
                // 焦点环同上：归 ActionBorder，避免与选中态阴影打架。
                host.BorderBrush <- if isActive then Tokens.accent :> IBrush else Brushes.Transparent :> IBrush
                Avalonia.Automation.AutomationProperties.SetItemStatus(host, if isActive then "当前会话" else "")

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
        let chevronGlyph = (if expanded then Icons.chevronUp else Icons.chevronDown) Tokens.textFaint
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
        host.DetachedFromVisualTree.Add(fun _ ->
            match archivedToggleHost with
            | Some current when obj.ReferenceEquals(current, host) -> archivedToggleHost <- None
            | _ -> ())
        let accessibleName =
            if expanded then "收起已归档会话"
            else sprintf "已归档会话，共 %d 个，点击展开" archivedCount
        Avalonia.Automation.AutomationProperties.SetName(host, accessibleName)
        Avalonia.Automation.AutomationProperties.SetRole(host, AutomationRole.Button)
        Avalonia.Automation.AutomationProperties.SetExpanded(host, expanded)
        ToolTip.SetTip(host, accessibleName)
        host.PointerEntered.Add(fun _ -> host.Background <- Tokens.hover)
        host.PointerExited.Add(fun _ ->
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
        Ui.setReservedActionVisible moreButton false
        DockPanel.SetDock(moreButton, Dock.Right)
        titleRow.Children.Add moreButton
        titleRow.Children.Add title
        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0, VerticalAlignment = VerticalAlignment.Center)
        column.Children.Add titleRow
        column.Children.Add preview
        let host =
            ActionBorder(
                Padding = Thickness(Tokens.space3, ControlMetrics.sidebarRowPaddingY),
                CornerRadius = CornerRadius Tokens.radiusMd,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = Thickness(2.0, 0.0, 0.0, 0.0),
                Cursor = handCursor,
                Focusable = true,
                MinHeight = ControlMetrics.sidebarRowMinHeight,
                Child = column)
        host.Tag <- summary
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
                  MenuEntry.create (if live.pinned then "取消置顶" else "置顶") (fun () -> actions.setPinned live (not live.pinned))
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
            Ui.setReservedActionVisible moreButton true)
        moreButton.LostFocus.Add(fun _ ->
            if not host.IsFocused then Ui.setReservedActionVisible moreButton false)
        host.PointerEntered.Add(fun _ ->
            // 悬停只动背景：选中行的强调左缘不动；悬停在已选项上层叠高亮，绝不比已选项更暗。
            let isActive = activeId = Some summary.id
            host.Background <-
                if isActive then SidebarHelpers.blendOver Tokens.selected Tokens.hover
                else Tokens.hover :> IBrush
            Ui.setReservedActionVisible moreButton true)
        host.PointerExited.Add(fun _ ->
            // hover 还原走 id + 实时快照，不用渲染期闭包里的旧 summary。
            this.ApplyRowState(summary.id, host)
            if not moreButton.IsFocused then Ui.setReservedActionVisible moreButton false)
        host.GotFocus.Add(fun _ ->
            focusedRowId <- Some summary.id
            focusedRowIndex <-
                visibleRowIds
                |> Array.tryFindIndex ((=) summary.id)
                |> Option.defaultValue focusedRowIndex
            this.ApplyRowState(summary.id, host)
            if activeId <> Some summary.id then host.Background <- Tokens.hover
            Ui.setReservedActionVisible moreButton true)
        host.LostFocus.Add(fun _ ->
            this.ApplyRowState(summary.id, host)
            if not moreButton.IsFocused then Ui.setReservedActionVisible moreButton false)
        Ui.onClick host (fun () -> actions.openConversation summary.id)
        // Enter/Space 由 Ui.onClick 统一接管（同一按键只打开一次）；
        // 这里只处理行内导航与行级快捷键。
        host.KeyDown.Add(fun e ->
            if e.Key = Key.F2 then
                e.Handled <- true
                match summaryById.TryGetValue summary.id with
                | true, live -> actions.renameConversation live
                | _ -> actions.renameConversation summary
            elif e.Key = Key.Delete then
                e.Handled <- true
                match summaryById.TryGetValue summary.id with
                | true, live -> actions.deleteConversation live
                | _ -> actions.deleteConversation summary
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
            let chevron = (if expanded then Icons.chevronUp else Icons.chevronDown) Tokens.textFaint
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
            else sprintf "已归档会话，共 %d 个，点击展开" count
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
        emptyState.IsVisible <- List.isEmpty visible && not isSearchEmpty
        if List.isEmpty visible then
            if not connected then
                emptyTitle.Text <- "尚未连接服务器"
                emptyHint.Text <- "连接后即可看到会话记录。"
            else
                emptyTitle.Text <- "还没有会话"
                emptyHint.Text <- "点右上角的加号开始第一次对话。"

    /// 更新列表数据。
    member this.SetConversations(items: ConversationSummary list) =
        summaries <- items
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

    member this.SetShowArchived(value: bool) =
        if showArchived <> value then
            showArchived <- value
            this.Rebuild()

    member this.SetConnection(isConnected: bool, text: string) =
        connected <- isConnected
        statusDot.Fill <- if isConnected then Tokens.success :> IBrush else Tokens.textFaint :> IBrush
        statusText.Text <- text
        this.Rebuild()

    member _.SetCompactMode(value: bool) =
        compactMode <- value
        compactBackButton.IsVisible <- value
        let size = if value then LayoutPolicy.compactActionTarget else Tokens.iconButton
        for button in [ compactBackButton; newConversationButton; settingsButton; clearSearchButton ] do
            Ui.setSquareTarget button size

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
    member this.FocusAfterDelete(removedId: string) =
        let neighborIndex () =
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
        match neighborIndex () with
        | Some idx ->
            let id = visibleRowIds.[idx]
            pendingFocusId <- Some id
            focusedRowId <- Some id
            focusedRowIndex <- idx
            this.FocusRowAt idx
        | None ->
            this.FocusSearch(selectAll = false)

    member this.Build() =
        this.Background <- Tokens.rail
        this.BorderBrush <- Tokens.borderSoft
        this.BorderThickness <- Thickness(0.0, 0.0, 1.0, 0.0)

        let brand = brandLogo Tokens.logoSidebar
        brand.VerticalAlignment <- VerticalAlignment.Center
        let wordmark =
            TextBlock(
                Text = "万象",
                FontSize = Tokens.fontBody,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text,
                LetterSpacing = 0.8,
                VerticalAlignment = VerticalAlignment.Center)
        let brandRow = Ui.hstack Tokens.space2 [ brand; wordmark :> Control ]
        let leading = Ui.hstack Tokens.space1 [ compactBackButton :> Control; brandRow :> Control ]
        let header =
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
        let searchArea = Border(Padding = Thickness(Tokens.space3, 0.0, Tokens.space3, Tokens.space2), Child = searchShell)
        let statusRow =
            ActionBorder(
                Background = Brushes.Transparent,
                CornerRadius = CornerRadius Tokens.radiusSm,
                Cursor = handCursor,
                Focusable = true,
                Child = Ui.hstack Tokens.space2 [ statusDot :> Control; statusText :> Control ])
        ToolTip.SetTip(statusRow, "点击重新连接")
        Avalonia.Automation.AutomationProperties.SetName(statusRow, "重新连接服务器")
        Ui.onClick statusRow (fun () -> actions.reconnect ())
        let footer =
            let dock = DockPanel(LastChildFill = false, VerticalAlignment = VerticalAlignment.Center)
            DockPanel.SetDock(statusRow, Dock.Left)
            DockPanel.SetDock(settingsButton, Dock.Right)
            dock.Children.Add statusRow
            dock.Children.Add settingsButton
            Border(
                Height = Tokens.barHeight,
                Padding = Thickness(Tokens.space4, 0.0, Tokens.space3, 0.0),
                BorderBrush = Tokens.borderSoft,
                BorderThickness = Thickness(0.0, 1.0, 0.0, 0.0),
                Child = dock)

        conversationList.ItemsPanel <- FuncTemplate<Panel>(fun () -> VirtualizingStackPanel() :> Panel)
        conversationList.ItemTemplate <-
            FuncDataTemplate<SidebarListItem>(
                (fun item existing ->
                    match item with
                    | SectionHeader label ->
                        match existing with
                        | :? TextBlock as header ->
                            header.Text <- label
                            header :> Control
                        | _ ->
                            let header = Ui.sectionLabel label
                            header.Margin <- Thickness(Tokens.space3, Tokens.space3, Tokens.space3, Tokens.space1)
                            header.LetterSpacing <- 0.8
                            header.Foreground <- Tokens.textMuted
                            header.FontSize <- Tokens.fontMicro
                            header.Focusable <- false
                            header :> Control
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
        body.Children.Add emptyState
        body.Children.Add searchEmptyHint

        let layout = DockPanel()
        DockPanel.SetDock(header, Dock.Top)
        DockPanel.SetDock(searchArea, Dock.Top)
        DockPanel.SetDock(footer, Dock.Bottom)
        layout.Children.Add header
        layout.Children.Add searchArea
        layout.Children.Add footer
        layout.Children.Add body
        this.Child <- layout
        this.Rebuild()
