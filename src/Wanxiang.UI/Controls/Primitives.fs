namespace Wanxiang.UI

open System
open System.Runtime.CompilerServices
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Automation.Provider
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Controls.Shapes
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading

/// 保留万象自绘 Border 的几何与视觉，但把“按钮”语义暴露给自动化层。
/// 这样屏幕阅读器得到真正的 Invoke pattern，而不是只看到一个可聚焦容器。
type internal ActionBorder() as this =
    inherit Border()

    let mutable invokeAction: unit -> unit = ignore
    let mutable savedShadow = BoxShadows()
    let mutable keyboardFocusRing = false

    do
        this.GotFocus.Add(fun e ->
            match e.NavigationMethod with
            | NavigationMethod.Tab
            | NavigationMethod.Directional ->
                savedShadow <- this.BoxShadow
                keyboardFocusRing <- true
                this.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accent.Color))
            | _ -> ())
        this.LostFocus.Add(fun _ ->
            if keyboardFocusRing then
                keyboardFocusRing <- false
                this.BoxShadow <- savedShadow)

    member _.SetInvokeAction(action: unit -> unit) = invokeAction <- action

    member internal _.InvokeFromAutomation() =
        if this.IsEnabled then invokeAction ()

    override _.OnCreateAutomationPeer() = ActionBorderAutomationPeer(this) :> AutomationPeer

and internal ActionBorderAutomationPeer(owner: ActionBorder) =
    inherit ControlAutomationPeer(owner)

    override _.GetAutomationControlTypeCore() = AutomationControlType.Button

    interface IInvokeProvider with
        member _.Invoke() = Dispatcher.UIThread.Post(fun () -> owner.InvokeFromAutomation())

/// Toggle 同理：视觉仍使用现有 track/knob，但自动化层得到 CheckBox + Toggle pattern。
type internal ToggleBorder() as this =
    inherit Border()

    let mutable toggleAction: unit -> unit = ignore
    let mutable value = false
    let mutable savedShadow = BoxShadows()
    let mutable keyboardFocusRing = false

    let stateOf v = if v then ToggleState.On else ToggleState.Off

    do
        this.GotFocus.Add(fun e ->
            match e.NavigationMethod with
            | NavigationMethod.Tab
            | NavigationMethod.Directional ->
                savedShadow <- this.BoxShadow
                keyboardFocusRing <- true
                this.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accent.Color))
            | _ -> ())
        this.LostFocus.Add(fun _ ->
            if keyboardFocusRing then
                keyboardFocusRing <- false
                this.BoxShadow <- savedShadow)

    member _.SetToggleAction(action: unit -> unit) = toggleAction <- action
    member _.AutomationValue = value

    member _.SetAutomationValue(next: bool) =
        if value <> next then
            let previous = stateOf value
            value <- next
            match ControlAutomationPeer.FromElement(this) with
            | null -> ()
            | peer ->
                peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty, previous, stateOf next)

    member internal _.ToggleFromAutomation() =
        if this.IsEnabled then toggleAction ()

    override _.OnCreateAutomationPeer() = ToggleBorderAutomationPeer(this) :> AutomationPeer

and internal ToggleBorderAutomationPeer(owner: ToggleBorder) =
    inherit ControlAutomationPeer(owner)

    override _.GetAutomationControlTypeCore() = AutomationControlType.CheckBox

    interface IToggleProvider with
        member _.ToggleState = if owner.AutomationValue then ToggleState.On else ToggleState.Off
        member _.Toggle() = Dispatcher.UIThread.Post(fun () -> owner.ToggleFromAutomation())

/// 基础控件工厂：全应用的按钮、输入、标签、徽标都从这里出，
/// 保证同一种语义在任何界面里长得一样、反馈一样。
module Ui =

    let private validationMessages = ConditionalWeakTable<TextBox, TextBlock>()

    let private validationMessageFor (box: TextBox) =
        match validationMessages.TryGetValue box with
        | true, message -> message
        | _ ->
            let message =
                TextBlock(
                    Text = "",
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.danger,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = ReadingRhythm.validationLineHeight,
                    IsVisible = false,
                    Margin = Thickness(2.0, 3.0, 0.0, 0.0))
            validationMessages.Add(box, message)
            // 一旦用户开始修正输入，旧错误就不应继续“指控”当前内容。
            // 如果仍不合法，下一次提交会重新给出准确错误。
            box.TextChanged.Add(fun _ ->
                if message.IsVisible then
                    message.Text <- ""
                    message.IsVisible <- false
                    Avalonia.Automation.AutomationProperties.SetHelpText(box, "")
                    match box.Parent with
                    | :? Border as shell when not box.IsFocused -> shell.BorderBrush <- Tokens.border
                    | _ -> ())
            message

    let private isInvalid (box: TextBox) =
        match validationMessages.TryGetValue box with
        | true, message -> message.IsVisible
        | _ -> false

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

    /// 统一点击与键盘激活绑定（支持鼠标释放与键盘 Enter/Space）。
    let onClick (host: Border) (action: unit -> unit) =
        match host with
        | :? ActionBorder as semantic -> semantic.SetInvokeAction action
        | _ -> ()
        host.PointerReleased.Add(fun e ->
            if host.IsEnabled && host.IsHitTestVisible && e.InitialPressMouseButton = MouseButton.Left then
                e.Handled <- true
                action ())
        host.KeyDown.Add(fun e ->
            if host.IsEnabled && (e.Key = Key.Enter || e.Key = Key.Space) then
                e.Handled <- true
                action ())

    let private attachSurfaceFeedback (host: Border) (idle: unit -> IBrush) (over: unit -> IBrush) =
        // 装饰性 hover/press 直接切状态，不做缓动；减少动态效果时无需额外分支，
        // 正常模式本身也保持低动态。持续运动只保留在真正等待的 spinner。
        let mutable isOver = false
        let refresh () = host.Background <- (if isOver then over () else idle ())
        host.PointerEntered.Add(fun _ ->
            isOver <- true
            refresh ())
        host.PointerExited.Add(fun _ ->
            isOver <- false
            host.Opacity <- 1.0
            refresh ())
        host.PointerPressed.Add(fun _ -> host.Opacity <- Tokens.opacityPressed)
        host.PointerReleased.Add(fun _ -> host.Opacity <- 1.0)
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
            LineHeight = ReadingRhythm.captionLineHeight)

    let fieldLabel (text: string) : TextBlock =
        TextBlock(
            Text = text,
            FontSize = Tokens.fontMicro,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.textFaint,
            Margin = Thickness(2.0, 0.0, 0.0, 3.0),
            LetterSpacing = 0.3)

    /// 表单字段的统一垂直结构：label → control → validation → hint。
    /// 这里只组合 Avalonia 原生控件，不接管测量/校验状态；调用方仍拥有字段本身。
    let fieldGroup (labelText: string) (hint: string) (control: Control) (validation: Control option) : Control =
        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        column.Children.Add(fieldLabel labelText)
        column.Children.Add control
        validation |> Option.iter column.Children.Add
        if not (String.IsNullOrWhiteSpace hint) then
            let note = caption hint
            note.Margin <- Thickness(2.0, Tokens.space1, 0.0, 0.0)
            match validation with
            | Some error ->
                note.IsVisible <- not error.IsVisible
                error.PropertyChanged.Add(fun args ->
                    if args.Property = Visual.IsVisibleProperty then note.IsVisible <- not error.IsVisible)
            | None -> ()
            column.Children.Add note
        column :> Control

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
        let timer = new DispatcherTimer(Interval = MotionLedger.busySpinnerFrame)
        let mutable angle = 0.0
        let mutable attached = false
        let mutable motionSubscription: IDisposable option = None
        let refreshMotion () =
            if MotionPolicy.isReduced () then
                timer.Stop()
                angle <- 0.0
                view.RenderTransform <- RotateTransform 0.0
            elif attached then
                timer.Start()
            else
                timer.Stop()
        timer.Tick.Add(fun _ ->
            angle <- (angle + 18.0) % 360.0
            view.RenderTransform <- RotateTransform angle)
        view.AttachedToVisualTree.Add(fun _ ->
            attached <- true
            motionSubscription |> Option.iter (fun subscription -> subscription.Dispose())
            motionSubscription <- Some(MotionPolicy.Changed.Publish.Subscribe(fun _ -> refreshMotion ()))
            refreshMotion ())
        view.DetachedFromVisualTree.Add(fun _ ->
            attached <- false
            timer.Stop()
            motionSubscription |> Option.iter (fun subscription -> subscription.Dispose())
            motionSubscription <- None)
        view :> Control

    // ---------- 图标按钮 ----------

    /// 图标按钮。`size` 默认 `Tokens.iconButton`；`tip` 为空则不挂提示。
    let iconButton (icon: IBrush -> Control) (tip: string) : Border =
        let host =
            ActionBorder(
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
            ActionBorder(
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
        host.IsEnabled <- enabled
        host.Opacity <- if enabled then 1.0 else Tokens.opacityDisabled
        host.IsHitTestVisible <- enabled
        host.Focusable <- enabled

    /// 只调整 hit surface，不改变内部 glyph 尺寸。
    let setSquareTarget (host: Border) (size: float) =
        host.Width <- size
        host.Height <- size
        host.MinWidth <- size
        host.MinHeight <- size

    /// 保留控件的布局槽位，只切换可见反馈和可操作性。
    /// 适合 clear / overflow 等上下文动作：出现时不会让标题或输入框突然变窄。
    let setReservedActionVisible (host: Border) (visible: bool) =
        host.IsVisible <- true
        host.Opacity <- if visible then 1.0 else 0.0
        host.IsHitTestVisible <- visible
        host.Focusable <- visible

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
            ActionBorder(
                CornerRadius = CornerRadius Tokens.radiusMd,
                Padding = Thickness(Tokens.space4, ControlMetrics.textButtonPaddingY),
                Background = bg,
                BorderBrush = stroke,
                BorderThickness = (if isNull stroke then Thickness 0.0 else Thickness 1.0),
                Cursor = handCursor,
                Focusable = true,
                MinHeight = ControlMetrics.textButtonMinHeight,
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
        | :? TextBlock as tb ->
            tb.Text <- text
            Avalonia.Automation.AutomationProperties.SetName(host, text)
        | _ -> ()

    let preparePendingButton (host: Border) =
        host.MinWidth <- max host.MinWidth ControlMetrics.pendingActionMinWidth

    /// 异步提交按钮保持同一几何，只切换文案和 enabled state。
    let setButtonPending (host: Border) (pending: bool) (idleText: string) (pendingText: string) =
        setButtonText host (if pending then pendingText else idleText)
        setEnabled host (not pending)
        Avalonia.Automation.AutomationProperties.SetHelpText(host, if pending then pendingText else "")

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
                MinHeight = ControlMetrics.textFieldTextMinHeight,
                VerticalContentAlignment = VerticalAlignment.Center)
        let shell =
            Border(
                Background = Tokens.surface,
                BorderBrush = Tokens.border,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusMd,
                Padding = Thickness(Tokens.space3, ControlMetrics.textFieldPaddingY),
                Child = box)
        box.GotFocus.Add(fun _ ->
            shell.BorderBrush <- Tokens.accent
            shell.BoxShadow <- BoxShadows(BoxShadow(Spread = 1.5, Color = Tokens.accentSoft.Color)))
        box.LostFocus.Add(fun _ ->
            shell.BorderBrush <- if isInvalid box then Tokens.danger else Tokens.border
            shell.BoxShadow <- BoxShadows())
        shell, box

    /// 为任意输入框创建/取得字段级错误行。调用方把它放在对应 field group 内即可。
    let fieldValidationMessage (box: TextBox) : TextBlock = validationMessageFor box

    /// 字段级验证状态：不改 padding / border thickness，因此出现错误时不会横向漂移。
    let setFieldError (box: TextBox) (message: string) =
        let validation = validationMessageFor box
        validation.Text <- message
        validation.IsVisible <- not (String.IsNullOrWhiteSpace message)
        match box.Parent with
        | :? Border as shell when not box.IsFocused -> shell.BorderBrush <- Tokens.danger
        | _ -> ()
        Avalonia.Automation.AutomationProperties.SetHelpText(box, message)

    let clearFieldError (box: TextBox) =
        let validation = validationMessageFor box
        validation.Text <- ""
        validation.IsVisible <- false
        match box.Parent with
        | :? Border as shell when not box.IsFocused -> shell.BorderBrush <- Tokens.border
        | _ -> ()
        Avalonia.Automation.AutomationProperties.SetHelpText(box, "")

    /// 带标签的输入行。
    let labeledField (labelText: string) (placeholder: string) : Control * TextBox =
        let shell, box = textField placeholder
        fieldGroup labelText "" shell (Some(fieldValidationMessage box :> Control)), box

    /// 带 label / hint / validation 的标准输入组。
    let inputFieldGroup (labelText: string) (hint: string) (box: TextBox) : Control =
        fieldGroup labelText hint (box.Parent :?> Control) (Some(fieldValidationMessage box :> Control))

    /// 非 TextBox 控件（select、textarea 外壳、只读值等）的标准字段组。
    let controlFieldGroup (labelText: string) (hint: string) (control: Control) : Control =
        fieldGroup labelText hint control None

    /// 多行输入。
    let textArea (placeholder: string) (minHeight: float) : Border * TextBox =
        let shell, box = textField placeholder
        box.AcceptsReturn <- true
        box.TextWrapping <- TextWrapping.Wrap
        box.MinHeight <- minHeight
        box.VerticalContentAlignment <- VerticalAlignment.Top
        shell, box

    /// 开关。返回容器与「读取/设置」访问器。
    let toggle (initial: bool) (onChanged: bool -> unit) : Control * (unit -> bool) * (bool -> unit) * (unit -> unit) =
        let mutable value = initial
        let knob =
            Border(
                Width = 14.0,
                Height = 14.0,
                CornerRadius = CornerRadius Tokens.radiusPill,
                Background = Tokens.surfaceRaised,
                VerticalAlignment = VerticalAlignment.Center)
        let track =
            ToggleBorder(
                Width = 34.0,
                Height = 20.0,
                CornerRadius = CornerRadius Tokens.radiusPill,
                Background = Tokens.line,
                Padding = Thickness(3.0, 0.0),
                Cursor = handCursor,
                Focusable = true,
                VerticalAlignment = VerticalAlignment.Center,
                Child = knob)
        let render () =
            track.Background <- if value then Tokens.accent :> IBrush else Tokens.line :> IBrush
            knob.HorizontalAlignment <- if value then HorizontalAlignment.Right else HorizontalAlignment.Left
            track.SetAutomationValue value
        let flip () =
            value <- not value
            render ()
            onChanged value
        track.SetToggleAction flip
        track.PointerReleased.Add(fun e ->
            if e.InitialPressMouseButton = MouseButton.Left then
                e.Handled <- true
                flip ())
        track.KeyDown.Add(fun e ->
            if e.Key = Key.Enter || e.Key = Key.Space then
                e.Handled <- true
                flip ())
        render ()
        track :> Control,
        (fun () -> value),
        (fun v ->
            value <- v
            render ()),
        flip
