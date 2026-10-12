namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

/// 命令面板（决策 370–379）：Ctrl/⌘+K 打开，搜对话标题、操作与设置项。
/// 桌面与 PWA 同一实现（OverlayHost 对话层，居中、可读屏）。
module CommandPaletteView =

    let private handCursor = lazy (new Cursor(StandardCursorType.Hand))

    let private row (item: PaletteItem) (run: PaletteItem -> unit) : ActionBorder =
        let title =
            TextBlock(
                Text = item.title,
                FontSize = Tokens.fontSmall,
                Foreground = Tokens.text,
                TextTrimming = TextTrimming.CharacterEllipsis)
        let text = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0, VerticalAlignment = VerticalAlignment.Center)
        text.Children.Add title
        if not (String.IsNullOrWhiteSpace item.subtitle) then
            text.Children.Add(
                TextBlock(
                    Text = item.subtitle,
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.textMuted,
                    TextTrimming = TextTrimming.CharacterEllipsis))
        let right = StackPanel(Orientation = Orientation.Horizontal, Spacing = Spacing.spaceMd, VerticalAlignment = VerticalAlignment.Center)
        match item.shortcut with
        | Some keys ->
            right.Children.Add(
                TextBlock(Text = keys, FontSize = Tokens.fontMicro, FontFamily = Tokens.monoFontFamily, Foreground = Tokens.textFaint, VerticalAlignment = VerticalAlignment.Center))
        | None -> ()
        right.Children.Add(
            TextBlock(Text = CommandPalette.kindLabel item.kind, FontSize = Tokens.fontMicro, Foreground = Tokens.textMuted, VerticalAlignment = VerticalAlignment.Center))
        let grid = Grid(ColumnSpacing = Spacing.spaceMd)
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        Grid.SetColumn(right, 1)
        grid.Children.Add text
        grid.Children.Add right
        let host =
            ActionBorder(
                Padding = Thickness(Spacing.spaceXl, ControlMetrics.menuItemPaddingY),
                CornerRadius = CornerRadius Spacing.Radius.sm,
                Background = Brushes.Transparent,
                Cursor = handCursor.Value,
                Focusable = true,
                MinHeight = ControlMetrics.menuItemMinHeight,
                Child = grid)
        host.Classes.Add "palette-row"
        AutomationProperties.SetName(host, item.title)
        AutomationProperties.SetHelpText(
            host,
            String.Join("，", [ yield CommandPalette.kindLabel item.kind
                               if item.subtitle <> "" then yield item.subtitle
                               match item.shortcut with
                               | Some k -> yield "快捷键 " + k
                               | None -> () ]))
        AutomationProperties.SetControlTypeOverride(host, Nullable Avalonia.Automation.Peers.AutomationControlType.ListItem)
        host.Transitions <- Ui.surfaceTransitions ()
        Interaction.surface host Interaction.HoverVariant.plain |> ignore
        Interaction.reportFocusOrigin host
        Ui.onClick host (fun () -> run item)
        host

    /// 构建面板（测试直接调用）。`items` 每次输入都重新取，保证对话列表是最新的；
    /// `run` 由调用方关闭面板后执行。返回 (内容, 搜索框)。
    let build (items: unit -> PaletteItem list) (recent: unit -> string list) (run: PaletteItem -> string -> unit) : Control * TextBox =
        let shell, search = Ui.textField "搜索对话、操作或设置"
        search.Name <- "CommandPaletteSearch"
        AutomationProperties.SetName(search, "命令面板")
        AutomationProperties.SetHelpText(search, "输入筛选，支持拼音首字母；上下方向键选择，回车执行，Esc 关闭")
        let list = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceXXs, Name = "CommandPaletteList")
        AutomationProperties.SetName(list, "结果")
        let status =
            TextBlock(
                Name = "CommandPaletteStatus",
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textMuted,
                Margin = Thickness(Spacing.spaceXl, Spacing.spaceXs))
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite)
        let mutable shown: PaletteItem list = []
        let mutable selected = 0
        let rows () = list.Children |> Seq.choose (function :? ActionBorder as b -> Some b | _ -> None) |> Array.ofSeq
        let paint () =
            rows () |> Array.iteri (fun i r ->
                r.Background <- (if i = selected then Tokens.surfaceContainer :> IBrush else Brushes.Transparent :> IBrush)
                if i = selected then r.Classes.Add "selected" else r.Classes.Remove "selected" |> ignore)
        let execute (item: PaletteItem) = run item search.Text
        let render () =
            let q = search.Text |> Option.ofObj |> Option.defaultValue ""
            let ranked = CommandPalette.rank q (recent ()) (items ())
            let fullText =
                if String.IsNullOrWhiteSpace q then []
                else
                    [ { id = CommandPalette.fullTextId
                        kind = PaletteAction
                        title = CommandPalette.fullTextTitle q
                        subtitle = "交给侧栏搜索，查找消息正文"
                        keywords = []
                        shortcut = Some "Ctrl / ⌘ + Shift + F" } ]
            shown <- ranked @ fullText
            selected <- 0
            list.Children.Clear()
            let recentIds = recent () |> Set.ofList
            let mutable headerShown = false
            if String.IsNullOrWhiteSpace q && ranked |> List.exists (fun i -> recentIds.Contains i.id) then
                let h = Ui.sectionLabel "最近使用"
                h.Margin <- Thickness(Spacing.spaceXl, Spacing.spaceMd, Spacing.spaceXl, Spacing.spaceXs)
                list.Children.Add h
            for item in shown do
                if String.IsNullOrWhiteSpace q && not headerShown && not (recentIds.Contains item.id) && recentIds.Count > 0 then
                    headerShown <- true
                    let h = Ui.sectionLabel "操作"
                    h.Margin <- Thickness(Spacing.spaceXl, Spacing.spaceMd, Spacing.spaceXl, Spacing.spaceXs)
                    list.Children.Add h
                list.Children.Add(row item execute)
            status.Text <-
                if List.isEmpty ranked then "没有匹配的对话、操作或设置。"
                else sprintf "%d 条结果" ranked.Length
            paint ()
        render ()
        search.TextChanged.Add(fun _ -> render ())
        let move delta =
            let n = shown.Length
            if n > 0 then
                selected <- (selected + delta + n) % n
                paint ()
                let r = rows ()
                if selected < r.Length then
                    r[selected].BringIntoView()
                    // 读屏：选中项变化时播报其名字（焦点留在输入框，便于继续输入）。
                    status.Text <- sprintf "%s，%s，第 %d 项，共 %d 项" shown[selected].title (CommandPalette.kindLabel shown[selected].kind) (selected + 1) n
        search.KeyDown.Add(fun e ->
            match e.Key with
            | Key.Down -> e.Handled <- true; move 1
            | Key.Up -> e.Handled <- true; move -1
            | Key.Enter ->
                if selected < shown.Length then
                    e.Handled <- true
                    execute shown[selected]
            | _ -> ())
        // 行上也能用方向键（Tab 进入列表后）。
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
        let scroller =
            ScrollViewer(
                Content = list,
                MaxHeight = 380.0,
                HorizontalScrollBarVisibility = Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Primitives.ScrollBarVisibility.Auto)
        let hint =
            TextBlock(
                Text = "↑↓ 选择 · Enter 执行 · Esc 关闭 · 支持拼音首字母",
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textFaint,
                Margin = Thickness(Spacing.spaceXl, 0.0, Spacing.spaceXl, Spacing.spaceXs))
        let root = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceXs, Name = "CommandPalette")
        AutomationProperties.SetName(root, "命令面板")
        root.Children.Add shell
        root.Children.Add status
        root.Children.Add scroller
        root.Children.Add(Ui.hairline ())
        root.Children.Add hint
        root :> Control, search
