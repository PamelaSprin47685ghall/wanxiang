namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

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
    let navPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = 2.0)
    let mutable current = Providers
    let mutable instanceId = ""
    let mutable serverUrl = ""
    let navButtons = System.Collections.Generic.Dictionary<SettingsSection, Border>()
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

    member private this.Select(section: SettingsSection) =
        current <- section
        for KeyValue(key, button) in navButtons do
            button.Background <- if key = section then Tokens.selected :> IBrush else Brushes.Transparent :> IBrush
        contentHost.Content <- renderSection section

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
        let host =
            Border(
                Padding = Thickness(Tokens.space3, 8.0),
                CornerRadius = CornerRadius Tokens.radiusMd,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = true,
                Child = Ui.hstack Tokens.space2 [ glyph; caption :> Control ])
        host.PointerReleased.Add(fun e ->
            e.Handled <- true
            this.Select section)
        host.KeyDown.Add(fun e ->
            if e.Key = Key.Enter || e.Key = Key.Space then
                e.Handled <- true
                this.Select section)
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
        invalidate [ Appearance ]
        if current = Appearance then contentHost.Content <- renderSection current

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
        closeButton.PointerReleased.Add(fun e ->
            e.Handled <- true
            onClose ())
        closeButton.KeyDown.Add(fun e ->
            if e.Key = Key.Enter || e.Key = Key.Space then
                e.Handled <- true
                onClose ())
        let header =
            let dock = DockPanel(LastChildFill = false, VerticalAlignment = VerticalAlignment.Center)
            let caption = Ui.title "设置"
            DockPanel.SetDock(caption, Dock.Left)
            DockPanel.SetDock(closeButton, Dock.Right)
            dock.Children.Add caption
            dock.Children.Add closeButton
            Border(
                Height = Tokens.barHeight,
                Padding = Thickness(Tokens.space5, 0.0, Tokens.space3, 0.0),
                BorderBrush = Tokens.borderSoft,
                BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0),
                Child = dock)

        for section in SettingsSection.all do
            navPanel.Children.Add(this.NavButton section)
        let navScroll =
            ScrollViewer(
                Content = navPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden)
        let nav =
            Border(
                Width = 196.0,
                Padding = Thickness(Tokens.space3, Tokens.space4),
                BorderBrush = Tokens.borderSoft,
                BorderThickness = Thickness(0.0, 0.0, 1.0, 0.0),
                Child = navScroll)

        let contentFrame =
            Border(
                Padding = Thickness(Tokens.space8, Tokens.space6, Tokens.space8, Tokens.space10),
                Child = contentHost,
                HorizontalAlignment = HorizontalAlignment.Center)
        let content =
            ScrollViewer(
                Content = contentFrame,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        // 居中并限宽。只设 MaxWidth 时 Border 会收缩到内容宽，
        // 卡片撑不开；必须按视口显式给宽度。
        content.PropertyChanged.Add(fun args ->
            if args.Property = ScrollViewer.ViewportProperty then
                let available = content.Viewport.Width
                if available > 240.0 then contentFrame.Width <- min 980.0 available)

        let split = DockPanel()
        DockPanel.SetDock(nav, Dock.Left)
        split.Children.Add nav
        split.Children.Add content

        let applyResponsive width =
            if width > 0.0 then
                let compact = width < 720.0
                if responsiveCompact <> Some compact then
                    responsiveCompact <- Some compact
                    if compact then
                        DockPanel.SetDock(nav, Dock.Top)
                        nav.Width <- Double.NaN
                        nav.Height <- 58.0
                        nav.Padding <- Thickness(Tokens.space2, Tokens.space2)
                        nav.BorderThickness <- Thickness(0.0, 0.0, 0.0, 1.0)
                        navPanel.Orientation <- Orientation.Horizontal
                        navScroll.HorizontalScrollBarVisibility <- ScrollBarVisibility.Auto
                        contentFrame.Padding <- Thickness(Tokens.space4, Tokens.space4, Tokens.space4, Tokens.space8)
                    else
                        DockPanel.SetDock(nav, Dock.Left)
                        nav.Width <- 196.0
                        nav.Height <- Double.NaN
                        nav.Padding <- Thickness(Tokens.space3, Tokens.space4)
                        nav.BorderThickness <- Thickness(0.0, 0.0, 1.0, 0.0)
                        navPanel.Orientation <- Orientation.Vertical
                        navScroll.HorizontalScrollBarVisibility <- ScrollBarVisibility.Hidden
                        contentFrame.Padding <- Thickness(Tokens.space8, Tokens.space6, Tokens.space8, Tokens.space10)

        let layout = DockPanel()
        DockPanel.SetDock(header, Dock.Top)
        layout.Children.Add header
        layout.Children.Add split
        this.Child <- layout
        this.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then applyResponsive this.Bounds.Width)
        applyResponsive this.Bounds.Width
        this.Select Providers
