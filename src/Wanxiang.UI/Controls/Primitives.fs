namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Animation
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Controls.Shapes
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading

/// 基础控件工厂：全应用的按钮、输入、标签、徽标都从这里出，
/// 保证同一种语义在任何界面里长得一样、反馈一样。
module Ui =

    /// 文字按钮的语义层级。层级决定视觉重量，不由调用方自由涂色。
    type ButtonTone =
        /// 页面主操作，一屏最多一个
        | Primary
        /// 常规操作
        | Secondary
        /// 弱操作，无边框
        | Ghost
        /// 破坏性操作
        | Danger

    let private handCursor = new Cursor(StandardCursorType.Hand)

    /// 给任意 Border 加上「悬停变亮、按下回弹」的即时反馈。
    /// 键盘焦点环。
    ///
    /// 用外阴影而不是改边框粗细：后者会让按钮内容跳一下，而焦点提示不该有位移。
    /// 只在**键盘**导航时出现——鼠标点完还留着一圈框是视觉噪音。
    let private attachFocusRing (host: Border) =
        let saved = host.BoxShadow
        let ring () = BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accent.Color))
        host.GotFocus.Add(fun e ->
            match e.NavigationMethod with
            | NavigationMethod.Tab
            | NavigationMethod.Directional -> host.BoxShadow <- ring ()
            | _ -> ())
        host.LostFocus.Add(fun _ -> host.BoxShadow <- saved)

    /// 统一点击与键盘激活绑定（支持鼠标释放与键盘 Enter/Space）。
    let onClick (host: Border) (action: unit -> unit) =
        host.PointerReleased.Add(fun e ->
            if host.IsHitTestVisible then
                e.Handled <- true
                action ())
        host.KeyDown.Add(fun e ->
            if e.Key = Key.Enter || e.Key = Key.Space then
                e.Handled <- true
                action ())

    let private attachSurfaceFeedback (host: Border) (idle: unit -> IBrush) (over: unit -> IBrush) =
        let transitions = Transitions()
        transitions.Add(DoubleTransition(Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds 120.0))
        transitions.Add(BrushTransition(Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds 120.0))
        host.Transitions <- transitions
        let mutable isOver = false
        let refresh () = host.Background <- (if isOver then over () else idle ())
        host.PointerEntered.Add(fun _ ->
            isOver <- true
            refresh ())
        host.PointerExited.Add(fun _ ->
            isOver <- false
            host.Opacity <- 1.0
            refresh ())
        host.PointerPressed.Add(fun _ -> host.Opacity <- 0.78)
        host.PointerReleased.Add(fun _ -> host.Opacity <- 1.0)
        attachFocusRing host
        refresh ()

    let private overlay (baseBrush: IBrush) (overlayBrush: IBrush) : IBrush =
        // Avalonia 不支持画笔叠加，直接取更亮/更暗的那一层做悬停底
        match baseBrush, overlayBrush with
        | (:? SolidColorBrush as b), (:? SolidColorBrush as o) ->
            let blend (bc: byte) (oc: byte) (alpha: byte) =
                byte (int bc + (int oc - int bc) * int alpha / 255)
            let a = o.Color.A
            SolidColorBrush(Color.FromArgb(b.Color.A, blend b.Color.R o.Color.R a, blend b.Color.G o.Color.G a, blend b.Color.B o.Color.B a))
            :> IBrush
        | _ -> baseBrush

    // ---------- 文字 ----------

    let label (text: string) : TextBlock =
        TextBlock(Text = text, FontSize = Tokens.fontBody, Foreground = Tokens.text, VerticalAlignment = VerticalAlignment.Center)

    let caption (text: string) : TextBlock =
        TextBlock(
            Text = text,
            FontSize = Tokens.fontCaption,
            Foreground = Tokens.textMuted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17.0)

    let fieldLabel (text: string) : TextBlock =
        TextBlock(
            Text = text,
            FontSize = Tokens.fontMicro,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.textFaint,
            Margin = Thickness(2.0, 0.0, 0.0, 3.0),
            LetterSpacing = 0.3)

    /// 区块小标题（侧栏分组、设置分节）。
    let sectionLabel (text: string) : TextBlock =
        TextBlock(
            Text = text,
            FontSize = Tokens.fontMicro,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.textFaint,
            LetterSpacing = 0.6)

    let heading (text: string) : TextBlock =
        TextBlock(Text = text, FontSize = Tokens.fontHeading, FontWeight = FontWeight.Medium, Foreground = Tokens.text)

    let title (text: string) : TextBlock =
        TextBlock(Text = text, FontSize = Tokens.fontTitle, FontWeight = FontWeight.Medium, Foreground = Tokens.text)

    // ---------- 结构 ----------

    let hstack (spacing: float) (children: Control seq) : StackPanel =
        let panel = StackPanel(Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center)
        for child in children do panel.Children.Add child
        panel

    let vstack (spacing: float) (children: Control seq) : StackPanel =
        let panel = StackPanel(Orientation = Orientation.Vertical, Spacing = spacing)
        for child in children do panel.Children.Add child
        panel

    let spacer () : Border = Border(Background = Brushes.Transparent)

    let hairline () : Border =
        Border(Height = 1.0, Background = Tokens.borderSoft, HorizontalAlignment = HorizontalAlignment.Stretch)

    let card (content: Control) : Border =
        Border(
            Background = Tokens.surface,
            BorderBrush = Tokens.border,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusLg,
            Padding = Thickness Tokens.space4,
            Child = content)

    // ---------- 徽标 / 状态 ----------

    /// 强调色浅底小标签。用于模型名、工具名、来源等。
    let chip (text: string) : Border =
        Border(
            Background = Tokens.accentSoft,
            CornerRadius = CornerRadius Tokens.radiusPill,
            Padding = Thickness(9.0, 2.0),
            VerticalAlignment = VerticalAlignment.Center,
            Child =
                TextBlock(
                    Text = text,
                    FontSize = Tokens.fontMicro,
                    FontWeight = FontWeight.Medium,
                    Foreground = Tokens.accent,
                    VerticalAlignment = VerticalAlignment.Center))

    /// 中性小标签（不抢强调色配额）。
    let tag (text: string) : Border =
        Border(
            Background = Tokens.surfaceRaised,
            BorderBrush = Tokens.border,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusSm,
            Padding = Thickness(7.0, 1.0),
            VerticalAlignment = VerticalAlignment.Center,
            Child =
                TextBlock(
                    Text = text,
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.textMuted,
                    VerticalAlignment = VerticalAlignment.Center))

    let statusDot (size: float) : Ellipse =
        Ellipse(Width = size, Height = size, Fill = Tokens.textFaint, VerticalAlignment = VerticalAlignment.Center)

    /// 旋转指示器：只在真正等待时出现。
    let spinner (size: float) : Control =
        let arc =
            Path(
                Data = Geometry.Parse "M 8 1.6 A 6.4 6.4 0 0 1 14.4 8",
                Stroke = Tokens.accent,
                StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round,
                Fill = Brushes.Transparent,
                Stretch = Stretch.None)
        let host = Canvas(Width = 16.0, Height = 16.0)
        host.Children.Add arc |> ignore
        let view =
            Viewbox(
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Child = host,
                RenderTransform = RotateTransform 0.0,
                RenderTransformOrigin = RelativePoint.Center)
        let timer = new DispatcherTimer(Interval = TimeSpan.FromMilliseconds 40.0)
        let mutable angle = 0.0
        timer.Tick.Add(fun _ ->
            angle <- (angle + 18.0) % 360.0
            view.RenderTransform <- RotateTransform angle)
        view.AttachedToVisualTree.Add(fun _ -> timer.Start())
        view.DetachedFromVisualTree.Add(fun _ -> timer.Stop())
        view :> Control

    // ---------- 图标按钮 ----------

    /// 图标按钮。`size` 默认 `Tokens.iconButton`；`tip` 为空则不挂提示。
    let iconButton (icon: IBrush -> Control) (tip: string) : Border =
        let host =
            Border(
                Width = Tokens.iconButton,
                Height = Tokens.iconButton,
                MinWidth = Tokens.iconButton,
                MinHeight = Tokens.iconButton,
                CornerRadius = CornerRadius Tokens.radiusMd,
                Background = Brushes.Transparent,
                Cursor = handCursor,
                Focusable = true,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center)
        let glyph = icon Tokens.textMuted
        glyph.HorizontalAlignment <- HorizontalAlignment.Center
        glyph.VerticalAlignment <- VerticalAlignment.Center
        host.Child <- glyph
        attachSurfaceFeedback host (fun () -> Brushes.Transparent :> IBrush) (fun () -> Tokens.hover :> IBrush)
        if not (String.IsNullOrWhiteSpace tip) then
            ToolTip.SetTip(host, tip)
            Avalonia.Automation.AutomationProperties.SetName(host, tip)
        host

    /// 强调色实心图标按钮（发送等主操作）。
    let iconButtonAccent (icon: IBrush -> Control) (tip: string) : Border =
        let host =
            Border(
                Width = Tokens.iconButton,
                Height = Tokens.iconButton,
                MinWidth = Tokens.iconButton,
                MinHeight = Tokens.iconButton,
                CornerRadius = CornerRadius Tokens.radiusPill,
                Background = Tokens.accent,
                Cursor = handCursor,
                Focusable = true,
                VerticalAlignment = VerticalAlignment.Center)
        let glyph = icon Tokens.textOnAccent
        glyph.HorizontalAlignment <- HorizontalAlignment.Center
        glyph.VerticalAlignment <- VerticalAlignment.Center
        host.Child <- glyph
        attachSurfaceFeedback host (fun () -> Tokens.accent :> IBrush) (fun () -> Tokens.accentHover :> IBrush)
        if not (String.IsNullOrWhiteSpace tip) then
            ToolTip.SetTip(host, tip)
            Avalonia.Automation.AutomationProperties.SetName(host, tip)
        host

    /// 禁用态保留足够对比度：过低的不透明度会让实心按钮上的图标彻底消失，
    /// 用户分不清「不可用」和「渲染坏了」。
    let setEnabled (host: Border) (enabled: bool) =
        host.Opacity <- if enabled then 1.0 else 0.5
        host.IsHitTestVisible <- enabled

    let setToggled (host: Border) (active: bool) =
        host.Background <- if active then Tokens.accentSoft :> IBrush else Brushes.Transparent :> IBrush

    /// 替换图标按钮的图形（发送 ↔ 停止等状态切换）。
    let setIcon (host: Border) (icon: IBrush -> Control) (brush: IBrush) =
        let glyph = icon brush
        glyph.HorizontalAlignment <- HorizontalAlignment.Center
        glyph.VerticalAlignment <- VerticalAlignment.Center
        host.Child <- glyph

    // ---------- 文字按钮 ----------

    let private toneBrushes (tone: ButtonTone) =
        match tone with
        | Primary -> Tokens.accent :> IBrush, Tokens.textOnAccent :> IBrush, null, Tokens.accentHover :> IBrush
        | Secondary -> Tokens.surface :> IBrush, Tokens.text :> IBrush, Tokens.border :> IBrush, overlay Tokens.surface Tokens.hover
        | Ghost -> Brushes.Transparent :> IBrush, Tokens.textMuted :> IBrush, null, Tokens.hover :> IBrush
        | Danger -> Tokens.dangerSoft :> IBrush, Tokens.danger :> IBrush, null, overlay Tokens.dangerSoft Tokens.hover

    /// 文字按钮。`onClick` 直接绑定，避免调用方重复处理指针事件。
    let button (tone: ButtonTone) (text: string) (action: unit -> unit) : Border =
        let bg, fg, stroke, hoverBg = toneBrushes tone
        let host =
            Border(
                CornerRadius = CornerRadius Tokens.radiusMd,
                Padding = Thickness(Tokens.space4, 7.0),
                Background = bg,
                BorderBrush = stroke,
                BorderThickness = (if isNull stroke then Thickness 0.0 else Thickness 1.0),
                Cursor = handCursor,
                Focusable = true,
                MinHeight = 32.0,
                VerticalAlignment = VerticalAlignment.Center,
                Child =
                    TextBlock(
                        Text = text,
                        FontSize = Tokens.fontSmall,
                        FontWeight = (match tone with Primary -> FontWeight.Medium | _ -> FontWeight.Medium),
                        Foreground = fg,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center))
        attachSurfaceFeedback host (fun () -> bg) (fun () -> hoverBg)
        onClick host action
        Avalonia.Automation.AutomationProperties.SetName(host, text)
        host

    let setButtonText (host: Border) (text: string) =
        match host.Child with
        | :? TextBlock as tb -> tb.Text <- text
        | _ -> ()

    // ---------- 输入 ----------

    /// 单行输入框。外壳负责描边与焦点环，TextBox 本身无边框。
    let textField (placeholder: string) : Border * TextBox =
        let box =
            TextBox(
                PlaceholderText = placeholder,
                BorderThickness = Thickness 0.0,
                Background = Brushes.Transparent,
                Foreground = Tokens.text,
                CaretBrush = Tokens.accent,
                FontSize = Tokens.fontBody,
                Padding = Thickness 0.0,
                MinHeight = 22.0,
                VerticalContentAlignment = VerticalAlignment.Center)
        let shell =
            Border(
                Background = Tokens.surface,
                BorderBrush = Tokens.border,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusMd,
                Padding = Thickness(Tokens.space3, 8.0),
                Child = box)
        let transitions = Transitions()
        transitions.Add(BrushTransition(Property = Border.BorderBrushProperty, Duration = TimeSpan.FromMilliseconds 120.0))
        shell.Transitions <- transitions
        box.GotFocus.Add(fun _ ->
            shell.BorderBrush <- Tokens.accent
            shell.BoxShadow <- BoxShadows(BoxShadow(Spread = 1.5, Color = Tokens.accentSoft.Color)))
        box.LostFocus.Add(fun _ ->
            shell.BorderBrush <- Tokens.border
            shell.BoxShadow <- BoxShadows())
        shell, box

    /// 带标签的输入行。
    let labeledField (labelText: string) (placeholder: string) : Control * TextBox =
        let shell, box = textField placeholder
        let column = vstack 0.0 [ fieldLabel labelText; shell ]
        column :> Control, box

    /// 多行输入。
    let textArea (placeholder: string) (minHeight: float) : Border * TextBox =
        let shell, box = textField placeholder
        box.AcceptsReturn <- true
        box.TextWrapping <- TextWrapping.Wrap
        box.MinHeight <- minHeight
        box.VerticalContentAlignment <- VerticalAlignment.Top
        shell, box

    /// 开关。返回容器与「读取/设置」访问器。
    let toggle (initial: bool) (onChanged: bool -> unit) : Control * (unit -> bool) * (bool -> unit) =
        let mutable value = initial
        let knob =
            Border(
                Width = 14.0,
                Height = 14.0,
                CornerRadius = CornerRadius Tokens.radiusPill,
                Background = Tokens.surfaceRaised,
                VerticalAlignment = VerticalAlignment.Center)
        let track =
            Border(
                Width = 34.0,
                Height = 20.0,
                CornerRadius = CornerRadius Tokens.radiusPill,
                Background = Tokens.line,
                Padding = Thickness(3.0, 0.0),
                Cursor = handCursor,
                Focusable = true,
                VerticalAlignment = VerticalAlignment.Center,
                Child = knob)
        let transitions = Transitions()
        transitions.Add(BrushTransition(Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds 150.0))
        track.Transitions <- transitions
        let render () =
            track.Background <- if value then Tokens.accent :> IBrush else Tokens.line :> IBrush
            knob.HorizontalAlignment <- if value then HorizontalAlignment.Right else HorizontalAlignment.Left
        let flip () =
            value <- not value
            render ()
            onChanged value
        track.PointerReleased.Add(fun e ->
            e.Handled <- true
            flip ())
        track.KeyDown.Add(fun e ->
            if e.Key = Key.Enter || e.Key = Key.Space then
                e.Handled <- true
                flip ())
        render ()
        track :> Control, (fun () -> value), (fun v ->
            value <- v
            render ())
