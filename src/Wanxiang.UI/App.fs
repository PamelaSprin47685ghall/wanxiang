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
        // wx-input：统一压过 Fluent TextBox 模板 focus/hover 大黑边（PART_BorderElement）
        let wxInputCtrl = Style(fun s -> s.OfType<TextBox>().Class("wx-input"))
        wxInputCtrl.Setters.Add(Setter(TemplatedControl.BorderThicknessProperty, Thickness(0.0)))
        wxInputCtrl.Setters.Add(Setter(TextBox.BorderBrushProperty, Brushes.Transparent))
        this.Styles.Add(wxInputCtrl)
        let addWxInputTplStyles (variantClass: string) (borderBrush: IBrush) (thickness: Thickness) (background: IBrush) =
            for pseudo in [| ""; ":focus"; ":pointerover" |] do
                let style =
                    Style(fun s ->
                        let sel = s.OfType<TextBox>().Class("wx-input").Class(variantClass)
                        let sel' = if pseudo = "" then sel else sel.Class(pseudo)
                        sel'.Template().OfType<Border>().Name("PART_BorderElement"))
                style.Setters.Add(Setter(Border.BorderBrushProperty, borderBrush))
                style.Setters.Add(Setter(Border.BorderThicknessProperty, thickness))
                style.Setters.Add(Setter(Border.BackgroundProperty, background))
                this.Styles.Add(style)
        // 壳内输入：描边由外层 Border 承担
        addWxInputTplStyles "wx-input-shell" Brushes.Transparent (Thickness(0.0)) Brushes.Transparent
        // 独立字段（侧栏搜索等）：浅描边，各态同色，不叠 Fluent 强调环
        addWxInputTplStyles "wx-input-field" Theme.outlineVariant (Thickness(1.0)) Theme.panel

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow()
        | :? ISingleViewApplicationLifetime as singleView ->
            // PWA（决策 48：Linux C 与 PWA 共用同一套 F# UI 代码；browser 不支持 Window，根视图必须是 Control）
            singleView.MainView <- MainView()
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
