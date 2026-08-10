namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Controls.Primitives
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Styling
open Avalonia.Threading
open Avalonia.Themes.Fluent

/// 万象桌面应用（clean-room：仅以原项目为灵感，UI 独立设计）。
type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        // 固定浅色（与 Theme.fs 纸感中性板一致）。桌面端不做 prefers-color-scheme 自动切。
        // PWA 端 Avalonia RequestedThemeVariant 同样保持 Light，避免 Fluent 整套画布误切深色；
        // 深色壳层对比度（html/body、splash、toast、滚动条）由 wwwroot/app.css 的
        // @media (prefers-color-scheme: dark) 承担。
        this.RequestedThemeVariant <- ThemeVariant.Light
        // 压过 Fluent 默认强调色：避免未显式设色的控件落回糖果靛蓝
        this.Resources["SystemAccentColor"] <- Color.Parse "#2E343E"
        this.Resources["SystemAccentColorDark1"] <- Color.Parse "#2E343E"
        this.Resources["SystemAccentColorLight1"] <- Color.Parse "#E8E7E2"
        // 会话列表：暖灰选中态 + 克制圆角（覆盖 Fluent 高亮资源，作用域仅本应用）
        this.Resources["ListBoxItemPadding"] <- Thickness(Theme.space2, 7.0)
        this.Resources["SystemControlHighlightListAccentLowBrush"] <- Theme.primaryContainer
        this.Resources["SystemControlHighlightListAccentMediumBrush"] <- Theme.selectedHover
        this.Resources["SystemControlHighlightListAccentHighBrush"] <- Theme.selectedPressed
        this.Resources["SystemControlHighlightListLowBrush"] <- Theme.hover
        this.Resources["SystemControlHighlightListMediumBrush"] <- Theme.pressed
        let itemStyle = Style(Selector = Selectors.Is<ListBoxItem>(null))
        itemStyle.Setters.Add(Setter(Control.MarginProperty, Thickness(Theme.sidebarInset, 1.0)))
        itemStyle.Setters.Add(Setter(ContentControl.CornerRadiusProperty, CornerRadius(Theme.radiusSm)))
        this.Styles.Add(itemStyle)
        // 输入壳内 TextBox：无边框，焦点由外层 Border 单独表达，避免双重大黑边
        let inputInner = Style(fun s -> s.OfType<TextBox>().Class("wx-input-inner"))
        inputInner.Setters.Add(Setter(TemplatedControl.BorderThicknessProperty, Thickness(0.0)))
        inputInner.Setters.Add(Setter(TextBox.BorderBrushProperty, Brushes.Transparent))
        this.Styles.Add(inputInner)
        let inputInnerFocus = Style(fun s -> s.OfType<TextBox>().Class("wx-input-inner").Class(":focus"))
        inputInnerFocus.Setters.Add(Setter(TemplatedControl.BorderThicknessProperty, Thickness(0.0)))
        inputInnerFocus.Setters.Add(Setter(TextBox.BorderBrushProperty, Brushes.Transparent))
        this.Styles.Add(inputInnerFocus)
        // 侧栏搜索框：聚焦时保持浅描边，不叠 Fluent 墨色强调环
        let fieldFocus = Style(fun s -> s.OfType<TextBox>().Class("wx-field").Class(":focus"))
        fieldFocus.Setters.Add(Setter(TextBox.BorderBrushProperty, Theme.muted))
        fieldFocus.Setters.Add(Setter(TemplatedControl.BorderThicknessProperty, Thickness(1.0)))
        this.Styles.Add(fieldFocus)

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow()
        | :? ISingleViewApplicationLifetime as singleView ->
            // PWA（决策 48：Linux C 与 PWA 共用同一套 F# UI 代码；browser 不支持 Window，根视图必须是 Control）
            singleView.MainView <- MainView()
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
