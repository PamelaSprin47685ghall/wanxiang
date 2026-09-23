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
open Avalonia.Automation.Peers

[<RequireQualifiedAccess>]
type internal AutomationRole =
    | Button

[<AutoOpen>]
module internal AutomationExtensions =
    type AutomationProperties with
        static member SetRole(element: Control, role: AutomationRole) =
            let controlType =
                match role with
                | AutomationRole.Button -> AutomationControlType.Button
            AutomationProperties.SetControlTypeOverride(element, Nullable controlType)

        static member SetExpanded(element: Control, expanded: bool) =
            AutomationProperties.SetItemStatus(element, if expanded then "已展开" else "已折叠")

/// 保留万象自绘 Border 的几何与视觉，但把“按钮”语义暴露给自动化层。
/// 这样屏幕阅读器得到真正的 Invoke pattern，而不是只看到一个可聚焦容器。
type internal ActionBorder() as this =
    inherit Border()

    let mutable invokeAction: unit -> unit = ignore
    let mutable savedShadow = BoxShadows()
    let mutable keyboardFocusRing = false

    do
        // 键盘焦点环：实色 accent 外扩（焦点提示要最高可辨度）。
        // 输入壳 Ui.textField 是唯一用 accentSoft 的例外——它已有一层实色描边，理由见其注释。
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
                    Margin = Thickness(ControlMetrics.fieldInsetX, Tokens.fieldRowPaddingY, 0.0, 0.0))
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

    /// 控件状态反馈的统一过渡集合（hover/press/focus/selected）：对底色做补间。
    ///
    /// 单一时长来源 `MotionLedger.controlStateDuration ()`
    /// （= MotionLedger.controlStateTransition 经 MotionPolicy 降级）：
    /// 减弱动效时为零时长，即历史上的瞬时硬切。
    /// 只补间颜色，不含 transform/尺寸/边宽——几何契约与焦点环纪律不变。
    ///
    /// 使用者：文字按钮（所有 ButtonTone）、图标按钮、ToggleBorder、菜单项、输入壳；
    /// 行/tab 式选中底由调用方在本地挂同一集合。描边即状态的表面用 surfaceBorderedTransitions。
    let surfaceTransitions () : Avalonia.Animation.Transitions =
        let background = Avalonia.Animation.BrushTransition()
        background.Property <- Border.BackgroundProperty
        background.Duration <- MotionLedger.controlStateDuration ()
        // 统一缓动：所有经门控的状态底色补间共用 easeOutCubic，收尾手感一致。
        background.Easing <- MotionPolicy.easeOutCubic
        let transitions = Avalonia.Animation.Transitions()
        transitions.Add background
        transitions

    /// 与 surfaceTransitions 同一机制、同一时长，另外过渡描边色：
    /// 供「边框即状态」的表面（下拉选择钮悬停把 border 换成 line）。
    let surfaceBorderedTransitions () : Avalonia.Animation.Transitions =
        let transitions = surfaceTransitions ()
        let border = Avalonia.Animation.BrushTransition()
        border.Property <- Border.BorderBrushProperty
        border.Duration <- MotionLedger.controlStateDuration ()
        // 描边补间与底色共用同一条 easeOutCubic。
        border.Easing <- MotionPolicy.easeOutCubic
        transitions.Add border
        transitions

    let private attachSurfaceFeedback (host: Border) (idle: unit -> IBrush) (over: unit -> IBrush) =
        // hover/press 底色补间统一走 surfaceTransitions（时长出自 MotionLedger，含减弱动效降级）。
        // 按压的透明度脉冲保持瞬时：setReservedActionVisible / setEnabled 对 Opacity 的写值
        // 被 UiStabilityTests 精确断言，而本项目的无显示测试制度下 Avalonia 原生过渡
        // 不会推进；因此 Opacity 不进入任何基控件的过渡集合。
        host.Transitions <- surfaceTransitions ()
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

    /// 把半透明 overlay 笔拂按 alpha 叠到 base 笔拂上（Avalonia 无原生画笔叠加）。
    /// 全应用唯一的混合实现：SidebarHelpers 里的同名私有副本已删除，统一走这里。
    /// 非实色输入退回 base；现有调用点传的都是实色 token。
    let blendOverlay (baseBrush: IBrush) (overlayBrush: IBrush) : IBrush =
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
            Margin = Thickness(ControlMetrics.fieldInsetX, 0.0, 0.0, Tokens.fieldRowPaddingY),
            LetterSpacing = Tokens.letterSpacingLabel)

    /// 表单字段的统一垂直结构：label → control → validation → hint。
    /// 这里只组合 Avalonia 原生控件，不接管测量/校验状态；调用方仍拥有字段本身。
    let fieldGroup (labelText: string) (hint: string) (control: Control) (validation: Control option) : Control =
        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        column.Children.Add(fieldLabel labelText)
        column.Children.Add control
        validation |> Option.iter column.Children.Add
        if not (String.IsNullOrWhiteSpace hint) then
            let note = caption hint
            note.Margin <- Thickness(ControlMetrics.fieldInsetX, Tokens.space1, 0.0, 0.0)
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
            LetterSpacing = Tokens.letterSpacingSection)

    /// 分节标签的宽字距档：Sidebar 的分组表头此前在本地把 Ui.sectionLabel 的返回
    /// 实例覆盖成 textMuted + letterSpacingDisplay（两处手改同一型文本）。该外观
    /// （弱化一档 + 最宽字距）是侧栏分组的既有意图，收进共享原语后调用方不再本地
    /// 改色/改字距；设置与菜单分组仍用默认 sectionLabel，两档有意分层。
    let sectionLabelWide (text: string) : TextBlock =
        TextBlock(
            Text = text,
            FontSize = Tokens.fontMicro,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.textMuted,
            LetterSpacing = Tokens.letterSpacingDisplay)

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

    /// 标准分隔线：一像素、borderSoft 档，是全应用「常规分隔」的默认档。
    /// 需要更轻的分割/描边时不要改这里——那会让所有调用点一起变浅；
    /// 在当地直接写 Border(Background = Tokens.hairline)（最轻档，见 Tokens.hairline 注释）。
    let hairline () : Border =
        Border(Height = ControlMetrics.borderWidth, Background = Tokens.borderSoft, HorizontalAlignment = HorizontalAlignment.Stretch)

    /// 分组卡：暖纸中间容器（surfaceContainer）+ 最轻发丝线 + 圆角 + 调用方内边距。
    /// Settings* / Dialogs / Composer 里「次级表面」分组容器的唯一来源；
    /// 外部分组传 Tokens.radiusLg，内层小卡传 Tokens.radiusMd。
    /// 只覆盖这一种几何：描边档不同或无描边的容器不在此列，当地保留。
    let groupingCard (padding: Thickness) (child: Control) (radius: float) : Border =
        Border(
            Background = Tokens.surfaceContainer,
            BorderBrush = Tokens.hairline,
            BorderThickness = Thickness ControlMetrics.borderWidth,
            CornerRadius = CornerRadius radius,
            Padding = padding,
            Child = child)

    /// 空态三件套：品牌 logo 视觉锚点 + 标题 + 说明（+ 可选的加载骨架与主操作按钮），
    /// 统一包在分组卡内。提取自 SettingsProviders 的空态范式（logoEmpty + label + caption），
    /// SettingsTools 与 Sidebar 的空态共用同一锚点与节律；
    /// logo 由调用方传入（本模块不依赖视图层的 Brand），对齐沿用调用方给的原样。
    /// title = None 表示只有一行说明（保留 Tools 现有文案，不新增标题）；
    /// caption 为空串时不占位（加载态只有标题与骨架）。
    ///
    /// 标题有两档：`prominentTitle = false`（默认、与提取前的 Ui.emptyState 逐字一致：
    /// label 档小号正文，不加宽字距、不加最小宽）与 `prominentTitle = true`
    ///（title 档 + letterSpacingEmphasis，供 surfaces lane 请求「这页真的空着」的
    /// 更强主标题）。两档的卡片几何（圆角、内边距、间距）完全相同，档位只改标题外观。
    ///
    /// 两档的分工：标准档（false）服务行内 / 分节内的空态——卡片只是所在区块的局部
    /// 内容，标题保持正文 label 档即可；强调档（true）服务整屏主空态（如聊天主区
    /// 没有会话时占满内容区的那一张），此时标题是页面级主标题，用 title 档 + 宽字距。
    let emptyStateWith (prominentTitle: bool) (logo: Control) (title: string option) (hint: string) (extra: Control option) (action: Border option) : Control =
        let titleOf text =
            if prominentTitle then
                TextBlock(
                    Text = text,
                    FontSize = Tokens.fontTitle,
                    Foreground = Tokens.text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    LetterSpacing = Tokens.letterSpacingEmphasis)
            else
                label text
        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space3)
        column.Children.Add logo
        match title with
        | Some text -> column.Children.Add(titleOf text)
        | None -> ()
        if not (String.IsNullOrWhiteSpace hint) then column.Children.Add(caption hint)
        match extra with
        | Some control -> column.Children.Add control
        | None -> ()
        match action with
        | Some button -> column.Children.Add button
        | None -> ()
        groupingCard (Thickness(Tokens.space6, Tokens.space6)) column Tokens.radiusLg :> Control

    /// 默认空态：标题档与提取前的 Ui.emptyState 逐字一致（现有调用点无需改动）。
    let emptyState (logo: Control) (title: string option) (hint: string) (extra: Control option) (action: Border option) : Control =
        emptyStateWith false logo title hint extra action

    // ---------- 徽标 / 状态 ----------

    /// 标签语气档。Neutral 为默认外观（既有 surfaceRaised + border + textMuted）；
    /// Warning / Danger 复用 Tokens.warning / Tokens.danger / Tokens.dangerSoft 既有笔，
    /// 不引入新色值，也不扩大调色板的使用范围。
    type TagTone =
        | Neutral
        | Warning
        | Danger

    let private tagToneStroke (tone: TagTone) =
        match tone with
        | Neutral -> Tokens.border :> IBrush, Tokens.textMuted :> IBrush, Tokens.surfaceRaised :> IBrush
        | Warning -> Tokens.warning :> IBrush, Tokens.warning :> IBrush, Tokens.surfaceRaised :> IBrush
        | Danger -> Tokens.danger :> IBrush, Tokens.danger :> IBrush, Tokens.dangerSoft :> IBrush

    /// 带语气的小标签。Neutral 行为与原 Ui.tag 逐字一致；语气只换描边/文字/底色笔，
    /// 几何不变，同排标签不会因语气不同而错位。
    ///
    /// 密度说明：tag 横纵分别是 tagPaddingX（7）与 Tokens.tightRowPaddingY（1）；
    /// 与 chip 用的 ControlMetrics.chipPadding*（8 × 2）是两档，因为它们服务的是不同
    /// 控件形态（静态小标签 vs 可装状态点的行内 chip），各自有语义名，不合档。
    let tagWith (tone: TagTone) (text: string) : Border =
        let stroke, ink, background = tagToneStroke tone
        Border(
            Background = background,
            BorderBrush = stroke,
            BorderThickness = Thickness ControlMetrics.borderWidth,
            CornerRadius = CornerRadius Tokens.radiusSm,
            Padding = Thickness(ControlMetrics.tagPaddingX, Tokens.tightRowPaddingY),
            VerticalAlignment = VerticalAlignment.Center,
            Child =
                TextBlock(
                    Text = text,
                    FontSize = Tokens.fontMicro,
                    Foreground = ink,
                    VerticalAlignment = VerticalAlignment.Center))

    /// 中性小标签（不抢强调色配额）。
    let tag (text: string) : Border = tagWith Neutral text

    let statusDot (size: float) : Ellipse =
        Ellipse(Width = size, Height = size, Fill = Tokens.textFaint, VerticalAlignment = VerticalAlignment.Center)

    /// 旋转指示器：只在真正等待时出现。
    let spinner (size: float) : Control =
        let track =
            Path(
                Data = Geometry.Parse "M 8 1.6 A 6.4 6.4 0 1 1 7.99 1.6",
                Stroke = Tokens.border,
                StrokeThickness = ControlMetrics.spinnerStrokeWidth,
                Fill = Brushes.Transparent,
                Stretch = Stretch.None,
                IsVisible = false)
        let arc =
            Path(
                Data = Geometry.Parse "M 8 1.6 A 6.4 6.4 0 0 1 14.4 8",
                Stroke = Tokens.accent,
                StrokeThickness = ControlMetrics.spinnerStrokeWidth,
                StrokeLineCap = PenLineCap.Round,
                Fill = Brushes.Transparent,
                Stretch = Stretch.None)
        // 弧几何以 16×16 画布为坐标系（半径 6.4 的圆 + 1.8 描边），再经 Viewbox 缩到目标 size；
        // 画布尺寸是几何常量而非布局量，故也走 ControlMetrics（见 spinnerCanvasSize 注释）。
        let host = Canvas(Width = ControlMetrics.spinnerCanvasSize, Height = ControlMetrics.spinnerCanvasSize)
        host.Children.Add track |> ignore
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
                track.IsVisible <- true
            elif attached then
                track.IsVisible <- false
                timer.Start()
            else
                track.IsVisible <- false
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
        // 槽位保留但感官与键盘均不可达时，同样移出 AT 控制视图；布局占位与键盘阻断不受影响。
        AutomationProperties.SetAccessibilityView(host, if visible then AccessibilityView.Default else AccessibilityView.Raw)



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
        | Secondary -> Tokens.surface :> IBrush, Tokens.text :> IBrush, Tokens.border :> IBrush, blendOverlay Tokens.surface Tokens.hover
        | Ghost -> Brushes.Transparent :> IBrush, Tokens.textMuted :> IBrush, null, Tokens.hover :> IBrush
        | ButtonTone.Danger -> Tokens.dangerSoft :> IBrush, Tokens.danger :> IBrush, null, blendOverlay Tokens.dangerSoft Tokens.hover

    /// 文字按钮。`onClick` 直接绑定，避免调用方重复处理指针事件。
    let button (tone: ButtonTone) (text: string) (action: unit -> unit) : Border =
        let bg, fg, stroke, hoverBg = toneBrushes tone
        let host =
            ActionBorder(
                CornerRadius = CornerRadius Tokens.radiusMd,
                Padding = Thickness(Tokens.space4, ControlMetrics.textButtonPaddingY),
                Background = bg,
                BorderBrush = stroke,
                BorderThickness = (if isNull stroke then Thickness 0.0 else Thickness ControlMetrics.borderWidth),
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
                BorderThickness = Thickness ControlMetrics.borderWidth,
                // 输入/托盘类控件圆角统一到 radiusLg，与卡片一致。
                CornerRadius = CornerRadius Tokens.radiusLg,
                Padding = Thickness(Tokens.space3, ControlMetrics.textFieldPaddingY),
                Child = box)
        // 输入壳挂状态反馈过渡：焦点/校验描边保持瞬时切换（焦点环纪律），
        // 底色状态（后续 lane 若引入 hover/selected 底）自动补间。
        shell.Transitions <- surfaceTransitions ()
        // 输入壳焦点环为什么是 accentSoft 而不是 accent：聚焦时壳的 1px 描边已换成实色
        // accent，外扩环若再用 accent 就是双重实色，重量压过输入内容本身；accentSoft 外环
        // + 实色描边给出的焦点语义已足够清晰。可聚焦控件（ActionBorder/ToggleBorder）
        // 没有这层描边，焦点环直接用实色 accent。
        // hover：只把底色从 surface 提亮到 hover 档（与其它可交互表面的同一语言），
        // 描边/焦点环/几何一概不动——输入壳的 1px 描边在聚焦时已经承担状态语义。
        shell.PointerEntered.Add(fun _ ->
            if not box.IsFocused then shell.Background <- Tokens.hover)
        shell.PointerExited.Add(fun _ -> shell.Background <- Tokens.surface)
        box.GotFocus.Add(fun _ ->
            shell.BorderBrush <- Tokens.accent
            shell.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accentSoft.Color)))
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
                Width = ControlMetrics.toggleKnobSize,
                Height = ControlMetrics.toggleKnobSize,
                CornerRadius = CornerRadius Tokens.radiusPill,
                Background = Tokens.surfaceRaised,
                VerticalAlignment = VerticalAlignment.Center)
        let track =
            ToggleBorder(
                Width = ControlMetrics.toggleTrackWidth,
                Height = ControlMetrics.toggleTrackHeight,
                CornerRadius = CornerRadius Tokens.radiusPill,
                Background = Tokens.line,
                Padding = Thickness(ControlMetrics.toggleKnobInsetX, 0.0),
                Cursor = handCursor,
                Focusable = true,
                VerticalAlignment = VerticalAlignment.Center,
                Child = knob)
        // on/off 底色（line ↔ accent）补间：与基控件同一状态反馈机制，只动颜色。
        track.Transitions <- surfaceTransitions ()
        let render () =
            track.Background <- if value then Tokens.accent :> IBrush else Tokens.line :> IBrush
            knob.HorizontalAlignment <- if value then HorizontalAlignment.Right else HorizontalAlignment.Left
            track.SetAutomationValue value
        let flip () =
            value <- not value
            render ()
            onChanged value
        track.SetToggleAction flip
        // hover：本控件没有独立「表面」层，hover 直接作用在 track 底色上——
        // 关闭态 line → hover 提亮一档，开启态保持 accent（accent 已是实色，再叠 hover 会更糊）。
        // 只改颜色（仍走上面的 BrushTransition，时长出自 MotionLedger），不碰几何。
        // 离开时回到当前值的底色（re-render 而不是写 line：on 态离开不该退到 line）。
        track.PointerEntered.Add(fun _ ->
            if not value then track.Background <- Tokens.hover)
        track.PointerExited.Add(fun _ -> render ())
        // 按压：与 attachSurfaceFeedback 同一节奏——瞬时 Opacity 脉冲，不进过渡集合
        //（本项目无显示测试制度下 Avalonia 原生过渡不推进，UiStabilityTests 已钉这一纪律）。
        track.PointerPressed.Add(fun _ -> track.Opacity <- Tokens.opacityPressed)
        track.PointerReleased.Add(fun e ->
            track.Opacity <- 1.0
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

    /// 双列表单网格：宽时两列并排，窄时（< formSingleColumnBreakpoint）退化为单列。
    /// 返回网格本身与「按当前宽度重新投影」的函数；调用方把后者挂到
    /// PropertyChanged(Bounds) 上并在构建末尾调用一次。
    ///
    /// 两个调用点（Dialogs 参数表单、SettingsGeneral 生成表单）此前各写了一份
    /// 几乎相同的 apply*Layout 闭包（R5）：列宽写入与行/列放置是同一份契约，
    /// 提取到这里后，两处共享唯一实现，断点只出自 LayoutPolicy。
    let twoColumnForm
        (columnSpacing: float)
        (rowSpacing: float)
        (rowCount: int)
        (fields: Control list)
        : Grid * (unit -> unit) =
        let grid = Grid(ColumnSpacing = columnSpacing, RowSpacing = rowSpacing)
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        for _ in 1 .. rowCount do grid.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        for field in fields do grid.Children.Add field
        let apply () =
            let width = grid.Bounds.Width
            let single = width > 0.0 && width < LayoutPolicy.formSingleColumnBreakpoint
            grid.ColumnDefinitions[1].Width <-
                if single then GridLength(0.0) else GridLength(1.0, GridUnitType.Star)
            let place (control: Control) row column =
                Grid.SetRow(control, row)
                Grid.SetColumn(control, column)
            if single then
                fields |> List.iteri (fun row control -> place control row 0)
            else
                // 双列：奇数个字段时最后一个字段占左列，右列留空。
                fields
                |> List.iteri (fun index control -> place control (index / 2) (index % 2))
        grid.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then apply ())
        grid, apply

    /// 设置行内容：左文（label + caption 竖列）右控件（Dock 右停靠、末子填充）的
    /// DockPanel 行内容。开关行与自定义右侧控件的设置行共用这同一结构；
    /// 行容器按需另套：Ui.switchRow 套 ActionBorder 交互层，Ui.settingsRowShell
    /// 套静态 Border 行壳。字号行此前手抄的 DockPanel 即此结构。
    let settingsRowContent (title: string) (hint: string) (right: Control) : DockPanel =
        let column = vstack Tokens.space1 [ label title :> Control; caption hint :> Control ]
        let dock = DockPanel(LastChildFill = true)
        DockPanel.SetDock(right, Dock.Right)
        dock.Children.Add right
        dock.Children.Add column
        dock

    /// 自定义右侧控件的设置行外壳：与开关行逐字同构的行壳（面板内容档 padding
    /// space2、共享行最小高 ControlMetrics.settingsRowMinHeight、settingsRowContent
    /// 的左文右控件结构），但不带开关交互层——hover/press、CheckBox 自动化语义与
    /// Enter/Space 行代理仍归 Ui.switchRow。抽出自 SettingsGeneral 字号行的私有
    /// Border + DockPanel（几何逐字保留），供自定义右侧控件的设置行共用。
    let settingsRowShell (title: string) (hint: string) (right: Control) : Border =
        Border(
            Padding = Thickness(Tokens.space2, Tokens.space2),
            MinHeight = ControlMetrics.settingsRowMinHeight,
            Child = settingsRowContent title hint right)

    /// 设置行开关：ActionBorder 行容器 + 行级 hover/press 底 + CheckBox 自动化语义 +
    /// ItemStatus 开关状态 + Enter/Space 行代理 + 统一行最小高度。
    /// 一行只有一个 Tab 停靠点：内层开关不进 Tab 序，行容器统一代理键盘。
    /// 提炼自 SettingsGeneral 的私有副本（行为逐字保留），供所有设置页共用；
    /// 原私有副本由调用方切换后删除。
    let switchRow (title: string) (hint: string) (initial: bool) (onChanged: bool -> unit) : Control * (unit -> bool) * (bool -> unit) =
        let toggle, read, write, flip = toggle initial onChanged
        // 一行只有一个 Tab 停靠点：内层开关不进 Tab 序，行容器统一代理键盘，
        // Enter/Space 只走 onClick 一次，单次翻转不变。鼠标点开关本身仍有效。
        toggle.Focusable <- false
        Avalonia.Automation.AutomationProperties.SetName(toggle, title)
        Avalonia.Automation.AutomationProperties.SetHelpText(toggle, hint)
        // 行内容（左文右控件 Dock）走共享 settingsRowContent：与自定义右侧控件的
        // 设置行同一结构，行壳交互层仍是这里的 ActionBorder。
        let dock = settingsRowContent title hint toggle
        let row =
            ActionBorder(
                Padding = Thickness(Tokens.space2, Tokens.space2),
                CornerRadius = CornerRadius Tokens.radiusMd,
                Background = Brushes.Transparent,
                Cursor = handCursor,
                Focusable = true,
                MinHeight = ControlMetrics.settingsRowMinHeight,
                Child = dock)
        row.Transitions <- surfaceTransitions ()
        Avalonia.Automation.AutomationProperties.SetName(row, title)
        Avalonia.Automation.AutomationProperties.SetHelpText(row, hint)
        Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
            row,
            Nullable Avalonia.Automation.Peers.AutomationControlType.CheckBox)
        let updateRowBackground () =
            row.Background <- if row.IsPointerOver || row.IsFocused then Tokens.hover :> IBrush else Brushes.Transparent :> IBrush
        row.PointerEntered.Add(fun _ -> updateRowBackground ())
        row.PointerExited.Add(fun _ ->
            row.Opacity <- 1.0
            updateRowBackground ())
        row.PointerPressed.Add(fun _ -> row.Opacity <- Tokens.opacityPressed)
        row.PointerReleased.Add(fun _ -> row.Opacity <- 1.0)
        row.GotFocus.Add(fun _ -> updateRowBackground ())
        row.LostFocus.Add(fun _ -> updateRowBackground ())
        let toggleAndRefresh () =
            flip ()
            updateRowBackground ()
            let currentState = if read () then "开启" else "关闭"
            Avalonia.Automation.AutomationProperties.SetItemStatus(row, currentState)
        Avalonia.Automation.AutomationProperties.SetItemStatus(row, if initial then "开启" else "关闭")
        // Enter/Space 由 onClick 拥有：这里再绑一次会翻转两次，键盘用户将永远打不开开关。
        onClick row toggleAndRefresh
        let syncWrite v =
            write v
            Avalonia.Automation.AutomationProperties.SetItemStatus(row, if v then "开启" else "关闭")
        row :> Control, read, syncWrite

    /// 表单校验反馈的统一节律（SettingsGeneral / SettingsTools / SettingsProviders /
    /// Dialogs 共用）：多错 → 顶部摘要 live region + 「有 N 处需要修正」toast +
    /// 聚焦摘要（Dispatcher 二次稳焦）；单错 → 该字段消息 toast + 聚焦字段。
    /// errors 按字段提交顺序给出，首元素即首个失败字段；summary 可为 None
    /// （无摘要位时多错退化为单错节律）；toast 通道由调用方传入
    ///（各视图固定 Warning 档），本辅助不拥有 OverlayHost。
    let applyValidationFeedback (errors: (TextBox * string) list) (summary: TextBlock option) (toast: string -> unit) : unit =
        let focusField (box: TextBox) =
            // 错误行只展开自己所在的组（hint 与 error 互换），边框粗细不变；
            // 单错时焦点仍送到字段本身，不打断摘要节律。
            box.BringIntoView()
            box.Focus(NavigationMethod.Directional) |> ignore
            Dispatcher.UIThread.Post(fun () ->
                if box.IsEffectivelyVisible && box.IsEnabled then
                    box.BringIntoView()
                    box.Focus(NavigationMethod.Directional) |> ignore)
        match errors with
        | [] ->
            // 错误清零：收起可能残留的摘要（与单错节律一致），不出 toast、不动焦点。
            summary |> Option.iter (fun region -> region.IsVisible <- false)
        | first :: _ ->
            if List.length errors > 1 then
                match summary with
                | Some region ->
                    let summaryText = String.Join("；", errors |> List.map snd)
                    region.Text <- sprintf "有 %d 处需要修正：%s" (List.length errors) summaryText
                    region.IsVisible <- true
                    Avalonia.Automation.AutomationProperties.SetName(region, region.Text)
                    toast (sprintf "有 %d 处需要修正，请查看表单顶部摘要。" (List.length errors))
                    // 多错时聚焦摘要：逐字段内联错误保留，焦点只落一处，不逐个跳转。
                    region.BringIntoView()
                    region.Focus(NavigationMethod.Directional) |> ignore
                    Dispatcher.UIThread.Post(fun () ->
                        if region.IsEffectivelyVisible then
                            region.BringIntoView()
                            region.Focus(NavigationMethod.Directional) |> ignore)
                | None ->
                    // 没有摘要位时退化为单错节律：只提示并聚焦首个失败字段。
                    let box, message = first
                    if not (String.IsNullOrWhiteSpace message) then toast message
                    focusField box
            else
                let box, message = first
                summary |> Option.iter (fun region -> region.IsVisible <- false)
                if not (String.IsNullOrWhiteSpace message) then toast message
                focusField box
