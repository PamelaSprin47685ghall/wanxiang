namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

/// 快速切换模型浮层（决策 240）：可搜索、每行标出模型服务、标出默认与当前，
/// 底部常驻「从下一条回复开始生效」。桌面与 PWA 同一实现（OverlayHost）。
module ModelSwitcherView =

    let private handCursor = lazy (new Cursor(StandardCursorType.Hand))

    let private tag (text: string) (foreground: IBrush) (background: IBrush) : Control =
        Border(
            Background = background,
            CornerRadius = CornerRadius Spacing.Radius.pill,
            Padding = Thickness(ControlMetrics.chipPaddingX, ControlMetrics.chipPaddingY),
            VerticalAlignment = VerticalAlignment.Center,
            Child = TextBlock(Text = text, FontSize = Tokens.fontMicro, Foreground = foreground))
        :> Control

    let private row (overlay: OverlayHost) (option: ModelOption) (pick: ModelOption -> unit) : ActionBorder =
        let name =
            TextBlock(
                Text = option.model,
                FontSize = Tokens.fontSmall,
                FontWeight = (if option.isCurrent then FontWeight.Medium else FontWeight.Normal),
                Foreground = Tokens.text,
                TextTrimming = TextTrimming.CharacterEllipsis)
        let provider =
            TextBlock(
                Text = option.providerLabel,
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textMuted,
                TextTrimming = TextTrimming.CharacterEllipsis)
        let text = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0, VerticalAlignment = VerticalAlignment.Center)
        text.Children.Add name
        text.Children.Add provider
        let marks = StackPanel(Orientation = Orientation.Horizontal, Spacing = Spacing.spaceXs, VerticalAlignment = VerticalAlignment.Center)
        if option.isDefault then marks.Children.Add(tag "默认" Tokens.textMuted Tokens.surfaceContainer)
        if option.isCurrent then
            let check = Icons.check Tokens.accent
            check.VerticalAlignment <- VerticalAlignment.Center
            marks.Children.Add check
        let grid = Grid(ColumnSpacing = Spacing.spaceMd)
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        Grid.SetColumn(marks, 1)
        grid.Children.Add text
        grid.Children.Add marks
        let host =
            ActionBorder(
                Padding = Thickness(Spacing.spaceXl, ControlMetrics.menuItemPaddingY),
                CornerRadius = CornerRadius Spacing.Radius.sm,
                Background = Brushes.Transparent,
                Cursor = handCursor.Value,
                Focusable = true,
                MinHeight = ControlMetrics.menuItemMinHeight,
                Child = grid)
        // 读屏名就是模型名（与旧菜单同一可达名，键盘/自动化按名查找不变）；服务与标记进 HelpText。
        let spoken =
            sprintf "%s%s%s" option.providerLabel
                (if option.isDefault then "，默认" else "")
                (if option.isCurrent then "，当前" else "")
        Avalonia.Automation.AutomationProperties.SetName(host, option.model)
        Avalonia.Automation.AutomationProperties.SetHelpText(host, spoken)
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
            host,
            Nullable Avalonia.Automation.Peers.AutomationControlType.MenuItem)
        ToolTip.SetTip(host, sprintf "%s · %s" option.providerLabel option.model)
        host.Transitions <- Ui.surfaceTransitions ()
        Interaction.surface host Interaction.HoverVariant.plain |> ignore
        Interaction.reportFocusOrigin host
        Ui.onClick host (fun () ->
            overlay.ClosePopup()
            pick option)
        host

    /// 构建浮层内容（测试直接调用）。`pick` 在选中后调用；选中当前项也会关闭但不触发 pick。
    let build (overlay: OverlayHost) (options: ModelOption list) (title: string) (pick: ModelOption -> unit) : Control =
        let shell, search = Ui.textField "搜索模型或模型服务"
        search.Name <- "ModelSwitcherSearch"
        Avalonia.Automation.AutomationProperties.SetName(search, "搜索模型")
        let list = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceXXs, Name = "ModelSwitcherList")
        let empty =
            TextBlock(
                Text = "没有匹配的模型。",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                Margin = Thickness(Spacing.spaceXl, Spacing.spaceMd),
                IsVisible = false)
        let choose (o: ModelOption) = if not o.isCurrent then pick o
        let rows () = list.Children |> Seq.choose (function :? ActionBorder as b -> Some b | _ -> None) |> Array.ofSeq
        let mutable shown: ModelOption list = options
        let render (query: string) =
            shown <- ModelSwitcher.filter query options
            list.Children.Clear()
            let grouped = shown |> List.groupBy (fun o -> o.providerLabel)
            let showHeaders = String.IsNullOrWhiteSpace query && grouped.Length > 1
            for (label, items) in grouped do
                if showHeaders then
                    let header = Ui.sectionLabel label
                    header.Margin <- Thickness(Spacing.spaceXl, Spacing.spaceMd, Spacing.spaceXl, Spacing.spaceXs)
                    list.Children.Add header
                for o in items do list.Children.Add(row overlay o choose)
            empty.IsVisible <- List.isEmpty shown
        render ""
        search.TextChanged.Add(fun _ -> render search.Text)
        search.KeyDown.Add(fun e ->
            match e.Key with
            | Key.Enter ->
                // 回车直接选第一条命中：搜到就切，不必再摸鼠标。
                match shown with
                | first :: _ ->
                    e.Handled <- true
                    overlay.ClosePopup()
                    choose first
                | [] -> ()
            | Key.Down ->
                match rows () with
                | [||] -> ()
                | items ->
                    e.Handled <- true
                    items[0].Focus(NavigationMethod.Directional) |> ignore
            | _ -> ())
        list.KeyDown.Add(fun e ->
            let items = rows ()
            if items.Length > 0 && (e.Key = Key.Up || e.Key = Key.Down) then
                let current = items |> Array.tryFindIndex _.IsFocused |> Option.defaultValue -1
                e.Handled <- true
                if e.Key = Key.Up && current <= 0 then search.Focus() |> ignore
                else
                    let next = if e.Key = Key.Up then current - 1 else min (items.Length - 1) (current + 1)
                    items[next].Focus(NavigationMethod.Directional) |> ignore
                    items[next].BringIntoView())
        let heading =
            TextBlock(
                Text = title,
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                Margin = Thickness(Spacing.spaceXl, Spacing.spaceXs, Spacing.spaceXl, 0.0))
        let note =
            TextBlock(
                Text = ModelSwitcher.appliesNote,
                Name = "ModelSwitcherNote",
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textFaint,
                TextWrapping = TextWrapping.Wrap,
                Margin = Thickness(Spacing.spaceXl, Spacing.spaceXs, Spacing.spaceXl, Spacing.spaceXs))
        let scroller =
            ScrollViewer(
                Content = (let p = StackPanel(Orientation = Orientation.Vertical) in p.Children.Add list; p.Children.Add empty; p),
                MaxHeight = LayoutPolicy.menuMaxHeight,
                HorizontalScrollBarVisibility = Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Primitives.ScrollBarVisibility.Auto)
        let root = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceXs, Width = 300.0)
        shell.Margin <- Thickness(Spacing.spaceXs, Spacing.spaceXs, Spacing.spaceXs, 0.0)
        root.Children.Add shell
        root.Children.Add heading
        root.Children.Add scroller
        root.Children.Add(Ui.hairline ())
        root.Children.Add note
        root :> Control

    let show (overlay: OverlayHost) (anchor: Control) (alignRight: bool) (options: ModelOption list) (title: string) (pick: ModelOption -> unit) =
        overlay.ShowPopup(anchor, build overlay options title pick, alignRight, 300.0)
