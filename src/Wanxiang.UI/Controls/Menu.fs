namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

/// 浮层菜单里的一项。
type MenuEntry = {
    label: string
    /// 右侧弱字（快捷键、当前值等）
    hint: string
    icon: (IBrush -> Control) option
    /// 选中态打勾
    selected: bool
    danger: bool
    action: unit -> unit
}

module MenuEntry =

    let create (label: string) (action: unit -> unit) : MenuEntry =
        { label = label; hint = ""; icon = None; selected = false; danger = false; action = action }

    let withIcon (icon: IBrush -> Control) (entry: MenuEntry) = { entry with icon = Some icon }
    let withHint (hint: string) (entry: MenuEntry) = { entry with hint = hint }
    let markSelected (selected: bool) (entry: MenuEntry) = { entry with selected = selected }
    let asDanger (entry: MenuEntry) = { entry with danger = true }

/// 浮层菜单与下拉选择器。都渲染在 `OverlayHost` 上，桌面与 PWA 行为一致。
module Menu =

    let private handCursor = new Cursor(StandardCursorType.Hand)

    let private renderEntry (overlay: OverlayHost) (entry: MenuEntry) : Control =
        let foreground: IBrush = if entry.danger then Tokens.danger else Tokens.text
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, VerticalAlignment = VerticalAlignment.Center)
        match entry.icon with
        | Some icon ->
            let glyph = icon (if entry.danger then Tokens.danger else Tokens.textMuted)
            glyph.VerticalAlignment <- VerticalAlignment.Center
            row.Children.Add glyph
        | None -> ()
        row.Children.Add(
            TextBlock(
                Text = entry.label,
                FontSize = Tokens.fontSmall,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis))
        let dock = DockPanel(LastChildFill = false)
        DockPanel.SetDock(row, Dock.Left)
        dock.Children.Add row
        if entry.selected then
            let mark = Icons.check Tokens.accent
            mark.VerticalAlignment <- VerticalAlignment.Center
            DockPanel.SetDock(mark, Dock.Right)
            dock.Children.Add mark
        elif not (String.IsNullOrWhiteSpace entry.hint) then
            let hint =
                TextBlock(
                    Text = entry.hint,
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.textFaint,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = Thickness(Tokens.space4, 0.0, 0.0, 0.0))
            DockPanel.SetDock(hint, Dock.Right)
            dock.Children.Add hint
        let host =
            ActionBorder(
                Padding = Thickness(Tokens.space3, 7.0),
                CornerRadius = CornerRadius Tokens.radiusSm,
                Background = Brushes.Transparent,
                Cursor = handCursor,
                Focusable = true,
                Child = dock)
        Avalonia.Automation.AutomationProperties.SetName(host, entry.label)
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
            host,
            Nullable Avalonia.Automation.Peers.AutomationControlType.MenuItem)
        host.PointerEntered.Add(fun _ -> host.Background <- Tokens.hover)
        host.PointerExited.Add(fun _ -> host.Background <- Brushes.Transparent)
        host.GotFocus.Add(fun _ -> host.Background <- Tokens.hover)
        host.LostFocus.Add(fun _ -> host.Background <- Brushes.Transparent)
        Ui.onClick host (fun () ->
            overlay.ClosePopup()
            entry.action ())
        host :> Control

    /// 打开一个菜单。`entries` 为空时不打开。
    let show (overlay: OverlayHost) (anchor: Control) (alignRight: bool) (entries: MenuEntry list) =
        if not (List.isEmpty entries) then
            let panel = StackPanel(Orientation = Orientation.Vertical, Spacing = 1.0)
            for entry in entries do
                panel.Children.Add(renderEntry overlay entry)
            let scroller =
                ScrollViewer(
                    Content = panel,
                    HorizontalScrollBarVisibility = Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Primitives.ScrollBarVisibility.Auto)
            overlay.ShowPopup(anchor, scroller :> Control, alignRight, 200.0)

    /// 带分组标题的菜单（模型选择器：按服务商分组）。
    let showGrouped (overlay: OverlayHost) (anchor: Control) (alignRight: bool) (groups: (string * MenuEntry list) list) =
        let visible = groups |> List.filter (fun (_, items) -> not (List.isEmpty items))
        if not (List.isEmpty visible) then
            let panel = StackPanel(Orientation = Orientation.Vertical, Spacing = 1.0)
            visible
            |> List.iteri (fun index (groupLabel, items) ->
                if index > 0 then
                    panel.Children.Add(
                        Border(Height = 1.0, Background = Tokens.borderSoft, Margin = Thickness(Tokens.space2, Tokens.space1)))
                if not (String.IsNullOrWhiteSpace groupLabel) then
                    let header = Ui.sectionLabel groupLabel
                    header.Margin <- Thickness(Tokens.space3, Tokens.space2, Tokens.space3, Tokens.space1)
                    panel.Children.Add header
                for entry in items do
                    panel.Children.Add(renderEntry overlay entry))
            let scroller =
                ScrollViewer(
                    Content = panel,
                    MaxHeight = 380.0,
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
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, VerticalAlignment = VerticalAlignment.Center)
        row.Children.Add caption
        row.Children.Add chevron
        let host =
            ActionBorder(
                Background = Tokens.surface,
                BorderBrush = Tokens.border,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusMd,
                Padding = Thickness(Tokens.space3, 6.0),
                Cursor = handCursor,
                Focusable = true,
                VerticalAlignment = VerticalAlignment.Center,
                Child = row)
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
            host,
            Nullable Avalonia.Automation.Peers.AutomationControlType.ComboBox)
        host.PointerEntered.Add(fun _ -> host.BorderBrush <- Tokens.line)
        host.PointerExited.Add(fun _ -> host.BorderBrush <- Tokens.border)
        let openMenu () = showGrouped overlay (host :> Control) false (optionsOf ())
        Ui.onClick host openMenu
        host.KeyDown.Add(fun e ->
            if e.Key = Key.Down then
                e.Handled <- true
                openMenu ())
        host, (fun text -> caption.Text <- text)
