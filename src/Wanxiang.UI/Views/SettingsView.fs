namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading

/// 设置界面分区。
type SettingsSection =
    | Providers
    | Tools
    | Generation
    | Appearance
    | About

module SettingsSection =

    let all = [ Providers; Tools; Generation; Appearance; About ]

    let label (section: SettingsSection) =
        match section with
        | Providers -> "服务商"
        | Tools -> "工具与 MCP"
        | Generation -> "生成"
        | Appearance -> "外观"
        | About -> "关于"

    let icon (section: SettingsSection) =
        match section with
        | Providers -> Icons.server
        | Tools -> Icons.wrench
        | Generation -> Icons.sliders
        | Appearance -> Icons.sun
        | About -> Icons.info

/// 设置界面：左侧分区导航 + 右侧内容。
///
/// 存在的意义很直接——在此之前，加一个服务商必须手改 TOML 并重启。
type SettingsView(overlay: OverlayHost, actions: SettingsActions, onPrefsChanged: UiPrefs -> unit, onClose: unit -> unit) =
    inherit Border()

    let providersPanel = SettingsProviders(overlay, actions)
    let toolsPanel = SettingsTools(overlay, actions)
    let generalPanel = SettingsGeneral(overlay, actions, onPrefsChanged)

    let contentHost = ContentControl()
    let mutable contentScroll: ScrollViewer option = None
    let navPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space1)
    let mutable current = Providers
    let mutable instanceId = ""
    let mutable serverUrl = ""
    let navButtons = System.Collections.Generic.Dictionary<SettingsSection, Border>()
    let navCaptions = System.Collections.Generic.Dictionary<SettingsSection, TextBlock>()
    let navGlyphs = System.Collections.Generic.Dictionary<SettingsSection, Control>()
    /// 选中左缘强调条：与侧栏选中同一语言（sidebarSelectedEdgeWidth），独立元素叠画、零几何位移。
    let navAccents = System.Collections.Generic.Dictionary<SettingsSection, Border>()
    let scrollPositions = System.Collections.Generic.Dictionary<SettingsSection, float>()
    let mutable responsiveCompact: bool option = None

    /// 每个分区只构建一次并缓存。
    /// 面板内部复用同一批输入控件实例，重复 Build 会把它们挂到第二个父级上，
    /// Avalonia 直接抛「already has a visual parent」。
    let builtSections = System.Collections.Generic.Dictionary<SettingsSection, Control>()

    let buildSection (section: SettingsSection) : Control =
        match section with
        | Providers -> providersPanel.Build()
        | Tools -> toolsPanel.Build()
        | Generation -> generalPanel.BuildGeneration()
        | Appearance -> generalPanel.BuildAppearance()
        | About -> generalPanel.BuildAbout(instanceId, serverUrl)

    let renderSection (section: SettingsSection) : Control =
        match builtSections.TryGetValue section with
        | true, existing -> existing
        | _ ->
            let built = buildSection section
            builtSections[section] <- built
            built

    /// 数据变化后需要重建的分区：丢弃缓存，下次切进去时重新构建。
    let invalidate (sections: SettingsSection list) =
        for section in sections do
            builtSections.Remove section |> ignore

    member private this.UpdateNavButtonStates() =
        for KeyValue(key, button) in navButtons do
            let selected = key = current
            let bg =
                if selected then Tokens.selected :> IBrush
                elif button.IsPointerOver || button.IsFocused then Tokens.hover :> IBrush
                else Brushes.Transparent :> IBrush
            button.Background <- bg
            // 选中左缘强调条：非色线索，宽度同侧栏选中态；只切可见性，不动任何几何。
            match navAccents.TryGetValue key with
            | true, accent -> accent.IsVisible <- selected
            | _ -> ()
            Avalonia.Automation.AutomationProperties.SetItemStatus(button, if selected then "当前分区" else "")
            let label = SettingsSection.label key
            Avalonia.Automation.AutomationProperties.SetHelpText(button, if selected then label + "，当前分区" else label)
            match navCaptions.TryGetValue key with
            | true, caption ->
                caption.Foreground <- (if selected then Tokens.text else Tokens.textMuted) :> IBrush
                caption.FontWeight <- (if selected then FontWeight.Bold else FontWeight.Medium)
            | _ -> ()
            match navGlyphs.TryGetValue key with
            | true, glyph -> glyph.Opacity <- (if selected then 1.0 else Tokens.opacitySubtle)
            | _ -> ()

    member private this.Select(section: SettingsSection) =
        let changed = current <> section
        if changed then
            contentScroll |> Option.iter (fun scroller ->
                if scroller.Offset.Y > 0.0 || not (scrollPositions.ContainsKey current) then
                    scrollPositions[current] <- scroller.Offset.Y)
        current <- section
        this.UpdateNavButtonStates()
        contentHost.Content <- renderSection section
        if changed then
            let targetY =
                match scrollPositions.TryGetValue section with
                | true, y -> y
                | _ -> 0.0
            contentScroll |> Option.iter (fun scroller ->
                scroller.Offset <- Vector(0.0, targetY))
        match navButtons.TryGetValue section with
        | true, button -> button.BringIntoView()
        | _ -> ()

    member private this.NavigateSection(direction: int) =
        let sections = SettingsSection.all
        let count = sections.Length
        let idx = sections |> List.tryFindIndex ((=) current) |> Option.defaultValue 0
        let nextIdx =
            let raw = (idx + direction) % count
            if raw < 0 then raw + count else raw
        let target = sections[nextIdx]
        this.Select target
        match navButtons.TryGetValue target with
        | true, button -> button.Focus(NavigationMethod.Directional) |> ignore
        | _ -> ()

    /// 打开设置时的焦点归宿：分区导航按钮。此前 ShowSettings 只切可见性，
    /// 焦点悬在已隐藏的 workspace 里（或脱落顶层），键盘用户按 Tab 前无处可去。
    /// kelivo 打开设置/历史浮层即 autofocus（chat_history_dialog.dart:192、
    /// settings_search_view.dart:147-150），这里对齐同一条体感。
    member this.FocusInitial() =
        let target =
            match navButtons.TryGetValue current with
            | true, button -> button
            | _ ->
                // 极端情形（分区还没建出来）：退回第一个分区。
                match navButtons.TryGetValue SettingsSection.all.[0] with
                | true, button -> button
                | _ -> null
        if not (isNull target) then
            target.Focus(NavigationMethod.Directional) |> ignore

    member private this.FocusSection(section: SettingsSection) =
        this.Select section
        match navButtons.TryGetValue section with
        | true, button -> button.Focus(NavigationMethod.Directional) |> ignore
        | _ -> ()

    member this.GetSectionScrollOffset(section: SettingsSection) =
        match scrollPositions.TryGetValue section with
        | true, y -> y
        | _ -> 0.0

    member this.SetSectionScrollOffset(section: SettingsSection, y: float) =
        scrollPositions[section] <- y
        if current = section then
            contentScroll |> Option.iter (fun s -> s.Offset <- Vector(0.0, y))

    member private this.NavButton(section: SettingsSection) : Border =
        let glyph = (SettingsSection.icon section) Tokens.textMuted
        glyph.VerticalAlignment <- VerticalAlignment.Center
        let caption =
            TextBlock(
                Text = SettingsSection.label section,
                FontSize = Tokens.fontSmall,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text,
                VerticalAlignment = VerticalAlignment.Center)
        // 选中左缘强调条：2px 独立元素，叠在按钮左缘的内边距区里（OverlayHost 里
        // toastAccentWidth 的同一思路），不进布局流、不抢焦点、不改变任何几何；
        // 键盘焦点环仍归 ActionBorder 的外阴影，两者可区分。
        let accentBar =
            Border(
                Width = ControlMetrics.sidebarSelectedEdgeWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Tokens.accent,
                IsHitTestVisible = false,
                Focusable = false,
                IsVisible = false)
        let contentGrid = Grid()
        contentGrid.Children.Add accentBar
        contentGrid.Children.Add(Ui.hstack Tokens.space2 [ glyph; caption :> Control ])
        let host =
            ActionBorder(
                Padding = Thickness(Tokens.space3, Tokens.space2),
                CornerRadius = CornerRadius Tokens.radiusMd,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = true,
                MinHeight = ControlMetrics.settingsNavMinHeight,
                Child = contentGrid)
        host.Transitions <- Ui.surfaceTransitions ()
        host.PointerEntered.Add(fun _ -> this.UpdateNavButtonStates())
        host.PointerExited.Add(fun _ -> this.UpdateNavButtonStates())
        host.GotFocus.Add(fun _ -> this.UpdateNavButtonStates())
        host.LostFocus.Add(fun _ -> this.UpdateNavButtonStates())
        host.KeyDown.Add(fun e ->
            // Enter/Space 由 Ui.onClick 拥有（选中是幂等的，但双通道无意义）；
            // 选中态靠 Tokens.selected 底 + 标题前景/字重 + 图标不透明度 + 左缘强调条，
            // 键盘焦点另有 focusRingSpread 外环，两者可区分。
            if e.Key = Key.Up then
                e.Handled <- true
                this.NavigateSection -1
            elif e.Key = Key.Down then
                e.Handled <- true
                this.NavigateSection 1
            elif e.Key = Key.Left then
                e.Handled <- true
                this.NavigateSection -1
            elif e.Key = Key.Right then
                e.Handled <- true
                this.NavigateSection 1
            elif e.Key = Key.Home then
                e.Handled <- true
                this.FocusSection SettingsSection.all.Head
            elif e.Key = Key.End then
                e.Handled <- true
                this.FocusSection (List.last SettingsSection.all))
        navCaptions[section] <- caption
        navGlyphs[section] <- glyph
        navAccents[section] <- accentBar
        glyph.Opacity <- Tokens.opacitySubtle
        Avalonia.Automation.AutomationProperties.SetName(host, SettingsSection.label section)
        host.Tag <- section
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
            host,
            Nullable Avalonia.Automation.Peers.AutomationControlType.TabItem)
        Ui.onClick host (fun () -> this.Select section)
        navButtons[section] <- host
        host

    member this.SetCatalog(catalog: Catalog) =
        providersPanel.SetCatalog catalog
        toolsPanel.SetCatalog catalog
        generalPanel.SetCatalog catalog
        // 服务商与工具面板自己维护列表内容，无需重建；生成面板的字段已被 SetCatalog 回填。
        // 只有「关于」依赖构建时的快照，交给切换时重建。
        invalidate [ About ]
        if current = About then contentHost.Content <- renderSection current

    member this.SetPrefs(prefs: UiPrefs) =
        generalPanel.SetPrefs prefs
        // Appearance 面板不能在用户点一次开关/字号后把自己销毁再重建：
        // 那会丢焦点、重置滚动位置，并重复注册主题事件。面板内部通过 SetPrefs
        // 同步现有控件；只有尚未构建时才在首次进入时读取最新 prefs。

    member this.SetConnection(instance: string, url: string) =
        instanceId <- instance
        serverUrl <- url
        invalidate [ About ]
        if current = About then contentHost.Content <- renderSection current

    member this.ApplyProbe(providerId: string, ok: bool, models: string list, error: string option) =
        providersPanel.ApplyProbe(providerId, ok, models, error)

    member this.Build() =
        this.Background <- Tokens.canvas

        let closeButton = Ui.iconButton Icons.close "关闭设置（Esc）"
        Ui.onClick closeButton onClose
        let header =
            let dock = DockPanel(LastChildFill = false, VerticalAlignment = VerticalAlignment.Center)
            // 窗口标题用页面主标题档（fontDisplay=25），明确高于分区内的小节标题（Ui.heading=19）；
            // 此前用 Ui.title(16) 造成「窗口标题 < 小节标题」的层级倒挂。
            let caption =
                TextBlock(
                    Text = "设置",
                    FontSize = Tokens.fontDisplay,
                    FontWeight = FontWeight.Medium,
                    Foreground = Tokens.text,
                    VerticalAlignment = VerticalAlignment.Center)
            DockPanel.SetDock(caption, Dock.Left)
            DockPanel.SetDock(closeButton, Dock.Right)
            dock.Children.Add caption
            dock.Children.Add closeButton
            Border(
                Height = Tokens.barHeight,
                // 头部内边距与导航轴对齐且左右对称：左缘落在导航列内容左缘（nav 横向 space3）上。
                Padding = Thickness(Tokens.space3, 0.0, Tokens.space3, 0.0),
                BorderBrush = Tokens.hairline,
                BorderThickness = Thickness(0.0, 0.0, 0.0, ControlMetrics.borderWidth),
                Child = dock)

        for section in SettingsSection.all do
            navPanel.Children.Add(this.NavButton section)
        let navScroll =
            ScrollViewer(
                Content = navPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden)
        // 分区导航轴是有意声明的边界表面，不做成 Ui.groupingCard：它贯穿内容区
        // 全高（分组卡高度由内容决定），无圆角（rail 与右侧内容区齐边，不是悬浮卡），
        // 且只朝向内容的那一边（窄模式为底边）描一根 hairline 分隔线，而分组卡是
        // 四向等宽描边 + 圆角。surfaceContainer / hairline / borderWidth 三件仍取自
        // 共享令牌，几何差异在此声明，读的人不必再猜它为什么长得像卡却不是卡。
        let nav =
            Border(
                Width = ControlMetrics.settingsNavWidth,
                Padding = Thickness(Tokens.space3, Tokens.space4),
                Background = Tokens.surfaceContainer,
                BorderBrush = Tokens.hairline,
                BorderThickness = Thickness(0.0, 0.0, ControlMetrics.borderWidth, 0.0),
                Child = navScroll)
        Avalonia.Automation.AutomationProperties.SetName(nav, "设置分区导航")
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
            nav,
            Nullable Avalonia.Automation.Peers.AutomationControlType.Tab)

        let contentFrame =
            Border(
                Padding = Thickness(Tokens.space8, Tokens.space6, Tokens.space8, Tokens.space10),
                Child = contentHost,
                MaxWidth = ControlMetrics.settingsContentMaxWidth,
                // 宽屏下内容列居中：MaxWidth 仍封顶 settingsContentMaxWidth，超宽窗口不再右侧留大片空白；
                // 窄屏内容未触顶时居中与拉伸等价（可用宽度小于 MaxWidth 时两者渲染相同）。
                HorizontalAlignment = HorizontalAlignment.Center)
        let content =
            ScrollViewer(
                Content = contentFrame,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalContentAlignment = HorizontalAlignment.Stretch)
        contentScroll <- Some content

        let split = DockPanel()
        DockPanel.SetDock(nav, Dock.Left)
        split.Children.Add nav
        split.Children.Add content

        let applyResponsive width =
            if width > 0.0 then
                let compact = width < LayoutPolicy.compactBreakpoint
                if responsiveCompact <> Some compact then
                    let previousOffset = content.Offset
                    responsiveCompact <- Some compact
                    if compact then
                        DockPanel.SetDock(nav, Dock.Top)
                        nav.Width <- Double.NaN
                        nav.Height <- Tokens.barHeight
                        nav.Padding <- Thickness(Tokens.space3, Tokens.space2)
                        navPanel.Spacing <- Tokens.space2
                        nav.BorderThickness <- Thickness(0.0, 0.0, 0.0, ControlMetrics.borderWidth)
                        navPanel.Orientation <- Orientation.Horizontal
                        navScroll.HorizontalScrollBarVisibility <- ScrollBarVisibility.Auto
                        contentFrame.Padding <- Thickness(Tokens.space4, Tokens.space4, Tokens.space4, Tokens.space8)
                    else
                        DockPanel.SetDock(nav, Dock.Left)
                        nav.Width <- ControlMetrics.settingsNavWidth
                        nav.Height <- Double.NaN
                        nav.Padding <- Thickness(Tokens.space3, Tokens.space4)
                        navPanel.Spacing <- Tokens.space1
                        nav.BorderThickness <- Thickness(0.0, 0.0, ControlMetrics.borderWidth, 0.0)
                        navPanel.Orientation <- Orientation.Vertical
                        navScroll.HorizontalScrollBarVisibility <- ScrollBarVisibility.Hidden
                        contentFrame.Padding <- Thickness(Tokens.space8, Tokens.space6, Tokens.space8, Tokens.space10)
                    // 断点切换会改变 extent 与视口高度：恢复值先按新边界钳制再落位。
                    // 旧写法直接恢复旧 offset，若超过新 extent 上界会被 ScrollViewer 二次钳制，
                    // 用户先看到一次错位又被拉回；在此钳制一次到位即消除这记瞬跳。
                    Dispatcher.UIThread.Post(
                        (fun () ->
                            let maxY = max 0.0 (content.Extent.Height - content.Viewport.Height)
                            content.Offset <- Vector(previousOffset.X, min previousOffset.Y maxY)),
                        DispatcherPriority.Background)

        let layout = DockPanel()
        DockPanel.SetDock(header, Dock.Top)
        layout.Children.Add header
        layout.Children.Add split
        this.Child <- layout
        this.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then applyResponsive this.Bounds.Width)
        applyResponsive this.Bounds.Width
        this.Select current
