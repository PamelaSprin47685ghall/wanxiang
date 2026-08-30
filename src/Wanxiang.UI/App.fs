namespace Wanxiang.UI

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Controls.Primitives
open Avalonia.Media
open Avalonia.Styling
open Avalonia.Themes.Fluent

/// 万象应用（clean-room：仅以原项目为灵感，UI 独立设计）。
///
/// 视觉体系完全由 `Tokens` 决定，Fluent 只提供控件模板。
/// 因此这里做两件事：把 Fluent 的强调色与选中色压成万象自己的，
/// 以及把 TextBox 模板那圈默认描边彻底去掉——万象的输入框描边由外层承担。
type App() =
    inherit Application()

    /// Fluent 的 TextBox 在各状态下用主题资源画自己的底色和描边，
    /// 与万象「外壳负责描边、输入框自身透明」的结构冲突。
    /// 压模板 Border 只在名字对得上时有效，因此这里同时改写资源键——
    /// 少了这一步，输入框会在浅纸背景上呈现一块更深的灰板。
    member private this.FlattenTextBoxChrome() =
        let transparent = Brushes.Transparent :> IBrush
        for key in
            [ "TextControlBackground"
              "TextControlBackgroundPointerOver"
              "TextControlBackgroundFocused"
              "TextControlBackgroundDisabled"
              "TextControlBorderBrush"
              "TextControlBorderBrushPointerOver"
              "TextControlBorderBrushFocused"
              "TextControlBorderBrushDisabled" ] do
            this.Resources[key] <- transparent
        this.Resources["TextControlBorderThemeThickness"] <- Thickness 0.0
        this.Resources["TextControlBorderThemeThicknessFocused"] <- Thickness 0.0
        this.Resources["TextControlThemePadding"] <- Thickness 0.0
        this.Resources["TextControlForeground"] <- Tokens.text
        this.Resources["TextControlForegroundPointerOver"] <- Tokens.text
        this.Resources["TextControlForegroundFocused"] <- Tokens.text
        this.Resources["TextControlPlaceholderForeground"] <- Tokens.textFaint
        this.Resources["TextControlPlaceholderForegroundPointerOver"] <- Tokens.textFaint
        this.Resources["TextControlPlaceholderForegroundFocused"] <- Tokens.textFaint
        this.Resources["TextControlSelectionHighlightColor"] <- Tokens.accentSoft
        for pseudo in [ ""; ":focus"; ":pointerover"; ":disabled" ] do
            let style =
                Style(fun selector ->
                    let baseSelector = selector.OfType<TextBox>()
                    let stated = if pseudo = "" then baseSelector else baseSelector.Class pseudo
                    stated.Template().OfType<Border>().Name "PART_BorderElement")
            style.Setters.Add(Setter(Border.BorderThicknessProperty, Thickness 0.0))
            style.Setters.Add(Setter(Border.BorderBrushProperty, transparent))
            style.Setters.Add(Setter(Border.BackgroundProperty, transparent))
            this.Styles.Add style

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        // 画布主题固定为浅色变体：万象自己的深色由 Tokens 提供，
        // 若让 Fluent 也切深色，两套配色会互相打架。
        this.RequestedThemeVariant <- ThemeVariant.Light

        let colors = Palette.light
        this.Resources["SystemAccentColor"] <- colors.accent
        this.Resources["SystemAccentColorDark1"] <- colors.accentHover
        this.Resources["SystemAccentColorLight1"] <- colors.accentSoft

        this.FlattenTextBoxChrome()

        // 选中/悬停一律走万象的强调浅底与暖灰，不用 Fluent 的糖果蓝
        this.Resources["SystemControlHighlightListAccentLowBrush"] <- Tokens.accentSoft
        this.Resources["SystemControlHighlightListAccentMediumBrush"] <- Tokens.accentSoft
        this.Resources["SystemControlHighlightListAccentHighBrush"] <- Tokens.accentSoft
        this.Resources["SystemControlHighlightListLowBrush"] <- Tokens.hover
        this.Resources["SystemControlHighlightListMediumBrush"] <- Tokens.pressed

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop -> desktop.MainWindow <- MainWindow()
        | :? ISingleViewApplicationLifetime as singleView ->
            // PWA：browser 没有 Window，根视图必须是 Control（决策 48）
            let view = MainView()
            singleView.MainView <- view
            view.Build()
        | _ -> ()
        base.OnFrameworkInitializationCompleted()
