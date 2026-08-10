namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
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
        // 固定浅色（与 Theme.fs 浅色板一致）。桌面端不做 prefers-color-scheme 自动切。
        // PWA 端 Avalonia RequestedThemeVariant 同样保持 Light，避免 Fluent 整套画布误切深色；
        // 深色壳层对比度（html/body、splash、toast、滚动条）由 wwwroot/app.css 的
        // @media (prefers-color-scheme: dark) 承担。
        this.RequestedThemeVariant <- ThemeVariant.Light
        // 会话列表：靛蓝选择态 + 圆角条目（覆盖 Fluent 高亮资源，作用域仅本应用）
        this.Resources["ListBoxItemPadding"] <- Thickness(12.0, 10.0)
        this.Resources["SystemControlHighlightListAccentLowBrush"] <- Theme.primaryContainer
        this.Resources["SystemControlHighlightListAccentMediumBrush"] <- Theme.selectedHover
        this.Resources["SystemControlHighlightListAccentHighBrush"] <- Theme.selectedPressed
        this.Resources["SystemControlHighlightListLowBrush"] <- Theme.hover
        this.Resources["SystemControlHighlightListMediumBrush"] <- Theme.pressed
        let itemStyle = Style(Selector = Selectors.Is<ListBoxItem>(null))
        itemStyle.Setters.Add(Setter(Control.MarginProperty, Thickness(10.0, 2.0)))
        itemStyle.Setters.Add(Setter(ContentControl.CornerRadiusProperty, CornerRadius(12.0)))
        this.Styles.Add(itemStyle)

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow()
        | :? ISingleViewApplicationLifetime as singleView ->
            // PWA（决策 48：Linux C 与 PWA 共用同一套 F# UI 代码；browser 不支持 Window，根视图必须是 Control）
            singleView.MainView <- MainView()
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
