namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

/// 浮层菜单里的一项（Wanxiang2.PopupMenu.PopupEntry）。
type MenuEntry = Wanxiang2.PopupMenu.PopupEntry

module MenuEntry =

    let create (label: string) (action: unit -> unit) : MenuEntry = Wanxiang2.PopupMenu.PopupEntry.create label action
    let withIcon (icon: IBrush -> Control) (entry: MenuEntry) = Wanxiang2.PopupMenu.PopupEntry.withIcon icon entry
    let withHint (hint: string) (entry: MenuEntry) = Wanxiang2.PopupMenu.PopupEntry.withHint hint entry
    let markSelected (selected: bool) (entry: MenuEntry) = Wanxiang2.PopupMenu.PopupEntry.markSelected selected entry
    let asDanger (entry: MenuEntry) = Wanxiang2.PopupMenu.PopupEntry.asDanger entry

/// 菜单渲染暂留本地：Wanxiang2.PopupMenu 的条目是普通 Border，读屏拿不到 Invoke（NoneAutomationPeer），
/// 旧 ActionBorder 条目可被辅助技术直接激活。库补上 Invoke 之前不切换，浮层本身走 OverlayDesk 的 OverlayLayer。
module Menu =

    let private handCursor = new Cursor(StandardCursorType.Hand)

    let private renderEntry (overlay: OverlayHost) (entry: MenuEntry) : Control =
        let foreground: IBrush = if entry.Danger then Tokens.danger else Tokens.text
        let iconSlot =
            Border(
                Width = ControlMetrics.menuIconSlotWidth,
                Height = Tokens.iconGlyph,
                VerticalAlignment = VerticalAlignment.Center)
        match entry.Icon with
        | Some icon ->
            let glyph = icon (if entry.Danger then Tokens.danger else Tokens.textMuted)
            glyph.HorizontalAlignment <- HorizontalAlignment.Center
            glyph.VerticalAlignment <- VerticalAlignment.Center
            iconSlot.Child <- glyph
        | None -> ()
        let caption =
            TextBlock(
                Text = entry.Label,
                FontSize = Tokens.fontSmall,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis)
        let left = Grid(ColumnSpacing = Spacing.spaceMd, VerticalAlignment = VerticalAlignment.Center)
        left.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        left.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        Grid.SetColumn(iconSlot, 0)
        Grid.SetColumn(caption, 1)
        left.Children.Add iconSlot
        left.Children.Add caption
        let rightSlot =
            Border(
                MinWidth = ControlMetrics.menuRightSlotMinWidth,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right)
        if entry.Selected then
            let mark = Icons.check Tokens.accent
            mark.VerticalAlignment <- VerticalAlignment.Center
            mark.HorizontalAlignment <- HorizontalAlignment.Right
            rightSlot.Child <- mark
        elif not (String.IsNullOrWhiteSpace entry.Hint) then
            let hint =
                TextBlock(
                    Text = entry.Hint,
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.textFaint,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = Thickness(Spacing.space2xl, 0.0, 0.0, 0.0))
            rightSlot.Child <- hint
        let row = Grid(ColumnSpacing = Spacing.spaceMd, VerticalAlignment = VerticalAlignment.Center)
        row.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        row.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        Grid.SetColumn(left, 0)
        Grid.SetColumn(rightSlot, 1)
        row.Children.Add left
        row.Children.Add rightSlot
        let host =
            ActionBorder(
                Padding = Thickness(Spacing.spaceXl, ControlMetrics.menuItemPaddingY),
                CornerRadius = CornerRadius Spacing.Radius.sm,
                Background = Brushes.Transparent,
                Cursor = handCursor,
                Focusable = true,
                MinHeight = ControlMetrics.menuItemMinHeight,
                Child = row)
        Avalonia.Automation.AutomationProperties.SetName(host, entry.Label)
        // 长文案被 CharacterEllipsis 截断时补原生 tooltip（悬停即读全文，不新增 type-ahead）。
        ToolTip.SetTip(host, entry.Label)
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
            host,
            Nullable Avalonia.Automation.Peers.AutomationControlType.MenuItem)
        // hover/focus 底色补间走 Ui.surfaceTransitions：与全应用基控件同一机制、同一时长。
        host.Transitions <- Ui.surfaceTransitions ()
        // 危险项（删除对话等）用 danger 变体（dangerSoft 叠 hover 底），与常规 hover 拉开差距；
        // 键盘激活（Enter/Space）由 Ui.onClick 统一绑定，Tab 焦点环由 ActionBorder 绘制。
        let hoverVariant = if entry.Danger then Interaction.HoverVariant.danger else Interaction.HoverVariant.plain
        Interaction.surface host hoverVariant |> ignore
        Interaction.reportFocusOrigin host
        // 按压反馈由 Interaction 的 `:pressed` 伪类负责（不透明度脉冲，不碰几何）。
        Ui.onClick host (fun () ->
            overlay.ClosePopup()
            entry.Action ())
        host :> Control

    /// 菜单用 Up/Down/Home/End 做 roving focus；几何仍完全交给 Avalonia。
    let private wireDirectionalNavigation (panel: StackPanel) =
        panel.KeyDown.Add(fun e ->
            if e.Key = Key.Up || e.Key = Key.Down || e.Key = Key.Home || e.Key = Key.End || e.Key = Key.PageUp || e.Key = Key.PageDown then
                let items =
                    panel.Children
                    |> Seq.choose (function :? ActionBorder as item when item.Focusable && item.IsEnabled -> Some item | _ -> None)
                    |> Array.ofSeq
                if items.Length > 0 then
                    let current = items |> Array.tryFindIndex _.IsFocused |> Option.defaultValue -1
                    let key =
                        match e.Key with
                        | Key.Home -> RovingKey.Home
                        | Key.PageUp -> RovingKey.PageUp
                        | Key.End -> RovingKey.End
                        | Key.PageDown -> RovingKey.PageDown
                        | Key.Up -> RovingKey.Up
                        | _ -> RovingKey.Down
                    match RovingIndex.menu key current items.Length with
                    | Some next ->
                        e.Handled <- true
                        items[next].Focus(NavigationMethod.Directional) |> ignore
                        items[next].BringIntoView()
                    | None -> ())

    /// 打开一个菜单。`entries` 为空时不打开。
    let show (overlay: OverlayHost) (anchor: Control) (alignRight: bool) (entries: MenuEntry list) =
        if not (List.isEmpty entries) then
            let panel = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceXXs)
            for entry in entries do
                panel.Children.Add(renderEntry overlay entry)
            wireDirectionalNavigation panel
            let scroller =
                ScrollViewer(
                    Content = panel,
                    MaxHeight = LayoutPolicy.menuMaxHeight,
                    HorizontalScrollBarVisibility = Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Primitives.ScrollBarVisibility.Auto)
            overlay.ShowPopup(anchor, scroller :> Control, alignRight, 200.0)

    /// 带分组标题的菜单（模型选择器：按模型服务分组）。
    let showGrouped (overlay: OverlayHost) (anchor: Control) (alignRight: bool) (groups: (string * MenuEntry list) list) =
        let visible = groups |> List.filter (fun (_, items) -> not (List.isEmpty items))
        if not (List.isEmpty visible) then
            let panel = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceXXs)
            visible
            |> List.iteri (fun index (groupLabel, items) ->
                if index > 0 then
                    // 组间分隔与全应用同一拼写：Ui.hairline（borderSoft 标准档），只补分组边距。
                    let divider = Ui.hairline ()
                    divider.Margin <- Thickness(Spacing.spaceMd, Spacing.spaceXs)
                    panel.Children.Add divider
                if not (String.IsNullOrWhiteSpace groupLabel) then
                    let header = Ui.sectionLabel groupLabel
                    header.Margin <- Thickness(Spacing.spaceXl, Spacing.spaceMd, Spacing.spaceXl, Spacing.spaceXs)
                    panel.Children.Add header
                for entry in items do
                    panel.Children.Add(renderEntry overlay entry))
            wireDirectionalNavigation panel
            let scroller =
                ScrollViewer(
                    Content = panel,
                    MaxHeight = LayoutPolicy.menuMaxHeight,
                    HorizontalScrollBarVisibility = Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Primitives.ScrollBarVisibility.Auto)
            overlay.ShowPopup(anchor, scroller :> Control, alignRight, 240.0)

    /// 下拉选择器按钮：显示当前值，点击弹出候选。
    /// 返回外壳与「设置显示文本」的回调。
    let selectButton
        (overlay: OverlayHost)
        (initialText: string)
        (optionsOf: unit -> (string * MenuEntry list) list)
        : Border * (string -> unit) =
        let caption =
            TextBlock(
                Text = initialText,
                FontSize = Tokens.fontSmall,
                Foreground = Tokens.text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260.0)
        let chevron = Icons.chevronDown Tokens.textFaint
        chevron.VerticalAlignment <- VerticalAlignment.Center
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Spacing.spaceMd, VerticalAlignment = VerticalAlignment.Center)
        row.Children.Add caption
        row.Children.Add chevron
        let host =
            ActionBorder(
                Background = Tokens.surface,
                BorderBrush = Tokens.border,
                BorderThickness = Thickness ControlMetrics.borderWidth,
                // 下拉/托盘类控件圆角统一到 radiusLg，与卡片一致。
                CornerRadius = CornerRadius Spacing.Radius.lg,
                // 垂直内边距引 ControlMetrics.selectButtonPaddingY（单一来源），水平仍用 space3，不再写裸 6.0。
                Padding = Thickness(Spacing.spaceXl, ControlMetrics.selectButtonPaddingY),
                Cursor = handCursor,
                Focusable = true,
                VerticalAlignment = VerticalAlignment.Center,
                Child = row)
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
            host,
            Nullable Avalonia.Automation.Peers.AutomationControlType.ComboBox)
        // 悬停只换描边色：与 hover 底色同一时长，走带描边的过渡集合。
        host.Transitions <- Ui.surfaceBorderedTransitions ()
        // 「描边即状态」档：hover 换描边而非底色（Interaction 的 bordered 变体）。
        Interaction.surface host Interaction.HoverVariant.bordered |> ignore
        // 按压反馈由 Interaction 的 `:pressed` 伪类负责。
        let openMenu () = showGrouped overlay (host :> Control) false (optionsOf ())
        Ui.onClick host openMenu
        host.KeyDown.Add(fun e ->
            // Down / Up 都能展开：只读按钮上两个方向都符合直觉，且与菜单内 Up/Down 对称。
            if e.Key = Key.Down || e.Key = Key.Up then
                e.Handled <- true
                openMenu ())
        // 当前值被 CharacterEllipsis 截断时补原生 tooltip；显示文本切换时同步刷新。
        ToolTip.SetTip(host, initialText)
        // 上一次由本按钮写入的显示文本：调用方若后来另起了名，就与它不同，
        // 此后不再覆盖（见下方回调里的比较）。
        let mutable lastText = initialText
        // 读屏可达：ComboBox 没有一个可播报的名字，盲用户 Tab 到这个下拉只会听到
        // 空的控件名。名字随显示文本同步刷新（Kelivo custom_dropdown.dart:45-52 同位语义）。
        // 区分「自己写的」与「调用方命名过的」：后者优先且此后不再被刷新（模型卡片
        // 的按钮名是「切换本对话使用的模型」这种动作名，不是当前文件名）。
        Avalonia.Automation.AutomationProperties.SetName(host, initialText)
        host, (fun text ->
            caption.Text <- text
            ToolTip.SetTip(host, text)
            if Avalonia.Automation.AutomationProperties.GetName host = lastText then
                Avalonia.Automation.AutomationProperties.SetName(host, text)
            lastText <- text)
