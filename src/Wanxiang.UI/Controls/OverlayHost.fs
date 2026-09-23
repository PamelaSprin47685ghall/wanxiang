namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading

/// 提示条语气。Info 承载信息性进展播报（“正在…”“已切换…”），
/// 与 Neutral 的弱存在提示分开：既不夸大为成功，也不拔高为警告。
type ToastTone =
    | Neutral
    | Success
    | Warning
    | Failure
    | Info

/// 覆盖层宿主：对话框、浮层菜单、提示条都画在应用最外层 Grid 上。
///
/// 桌面与 PWA 共用同一套 UI，平台弹窗行为差异大，因此一律自绘。
/// 一个应用只有一个实例，由 AppShell 创建并注入给各视图。
type OverlayHost(root: Grid) =

    // 对话框 / 浮层 / 提示条统一的视口留白：space4 在小窗口与高 DPI 下仍能保证
    // 卡片边缘与阴影不贴边；fit*ToViewport 与 popup 钳位都以它为唯一依据。
    let viewportInset = Tokens.space4
    let popupGap = Tokens.space2

    let scrim =
        Border(
            Background = Tokens.scrim,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch)

    let dialogCard =
        Border(
            Background = Tokens.surfaceRaised,
            // 瞬态浮层边缘用最轻 hairline，抬升感交给 shadowDialog，避免重描边。
            BorderBrush = Tokens.hairline,
            BorderThickness = Thickness ControlMetrics.borderWidth,
            CornerRadius = CornerRadius Tokens.radiusXl,
            Padding = Thickness Tokens.space6,
            // Center + Margin 双保险：MaxWidth/MaxHeight 负责钳制，Margin 保证
            // 首次测量前或 bounds 为 0 的过渡帧也不贴边。
            Margin = Thickness viewportInset,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false)

    /// 浮层菜单的透明捕获层：点击层外即关闭。
    let popupCatcher =
        Border(
            Background = Brushes.Transparent,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch)

    let popupCard =
        Border(
            Background = Tokens.surfaceRaised,
            // 与对话框同一套：hairline 描边 + shadowPopup 抬升。
            BorderBrush = Tokens.hairline,
            BorderThickness = Thickness ControlMetrics.borderWidth,
            CornerRadius = CornerRadius Tokens.radiusLg,
            Padding = Thickness(Tokens.space1, Tokens.space1),
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top)

    let toastStack =
        StackPanel(
            Orientation = Orientation.Vertical,
            Spacing = Tokens.space2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = Thickness(Tokens.space6, 0.0, Tokens.space6, Tokens.space8))

    let mutable onDialogClosed: unit -> unit = id
    // 嵌套对话框栈：后打开的对话框只盖住先打开的，关闭时原样恢复
    // （子内容 + onClosed + 关闭守卫），守卫闭包（如提交 pending 标记）原样保留。
    let dialogStack = ResizeArray<Control * float * (unit -> unit) * (unit -> bool)>()
    // 顶层对话框的关闭守卫：提交 pending 期间 Esc / scrim 不得关闭，
    // 由 HandleEscape 与 scrim 统一检查，程序化的 CloseDialog（提交成功）不受影响。
    let mutable dialogGuard: unit -> bool = fun () -> true
    let mutable previousDialogFocus: IInputElement option = None
    let mutable previousPopupFocus: IInputElement option = None
    let mutable lastPopupFocus: IInputElement option = None
    let mutable lastPopupAnchor: Control option = None
    let mutable dialogPreferredWidth = 0.0
    let mutable popupPreferredMinWidth = 180.0
    let mutable popupAnchor: Control option = None
    let mutable popupAlignRight = false
    // scrim 淡出在途定时器：关闭对话框不阻塞同步关闭与焦点恢复，只让遮罩淡到 0
    // 后自行隐藏；再次打开（ShowDialog）会停掉在途淡出。减弱动效时不启用。
    let mutable scrimFadeOut : DispatcherTimer option = None

    let stopScrimFadeOut () =
        match scrimFadeOut with
        | Some t ->
            t.Stop()
            scrimFadeOut <- None
        | None -> ()
    // 同时可见的提示条上限与去重登记：提示条只配给跨上下文确认与短暂系统状态，
    // 堆叠盖住内容或重复刷屏就违背了它存在的理由，因此新条挤掉最旧的、同文只留最新一条。
    let toastCap = 3
    let toastEntries = ResizeArray<string * (unit -> unit)>()

    let currentFocus () =
        match TopLevel.GetTopLevel root with
        | null -> None
        | top ->
            match top.FocusManager with
            | null -> None
            | manager -> manager.GetFocusedElement() |> Option.ofObj

    /// 可恢复的触发源：可用、有效可见且仍挂在视觉树上。Esc / scrim / catcher 关闭时
    /// 触发控件可能已被移除（detach 后 IsEffectivelyVisible 仍可能为 true），
    /// 因此再用 TopLevel 校验是否仍在树上，避免把焦点抢到游离控件上。
    let isRestorable (control: Control) =
        control.IsEnabled
        && control.IsEffectivelyVisible
        && not (isNull (TopLevel.GetTopLevel control))

    let restoreFocus (previous: IInputElement option) =
        match previous with
        | Some (:? Control as control) when isRestorable control ->
            // 关闭与 Post 之间树可能又变了：真正聚焦前再检查一次。
            Dispatcher.UIThread.Post(fun () ->
                if isRestorable control then
                    control.Focus() |> ignore)
        | _ -> ()

    let rememberDialogFocus () =
        // 唯一的对话框焦点记忆：当前焦点仍可恢复就记它；从浮层菜单里打开对话框时，
        // 菜单项在 ClosePopup 后已脱离视觉树，同步读到的焦点不可恢复，此时回退到
        // 浮层锚点 / 上次浮层焦点，保证关闭对话框后焦点有处可去。Dialogs 只传业务
        // onClosed，不再自带第二套记忆。
        let cur = currentFocus ()
        let usable =
            match cur with
            | Some (:? Control as c) when isRestorable c -> true
            | _ -> false
        previousDialogFocus <-
            if usable then cur
            else
                match lastPopupAnchor with
                | Some a when isRestorable a -> Some (a :> IInputElement)
                | _ ->
                    match lastPopupFocus with
                    | Some (:? Control as c) when isRestorable c -> lastPopupFocus
                    | _ -> None

    let rememberPopupFocus () =
        let cur = currentFocus ()
        previousPopupFocus <- cur
        if cur.IsSome then lastPopupFocus <- cur

    let restoreDialogFocus () =
        let previous = previousDialogFocus
        previousDialogFocus <- None
        restoreFocus previous

    let restorePopupFocus () =
        let previous = previousPopupFocus
        previousPopupFocus <- None
        restoreFocus previous

    let rec descendants (control: Control) =
        seq {
            yield control
            match control with
            | :? Panel as panel ->
                for child in panel.Children do
                    yield! descendants child
            | :? Decorator as decorator when not (isNull decorator.Child) ->
                yield! descendants decorator.Child
            | :? ContentControl as host ->
                match host.Content with
                | :? Control as child -> yield! descendants child
                | _ -> ()
            | _ -> ()
        }

    let focusables (content: Control) =
        descendants content
        // 有效可见（IsEffectivelyVisible）而非本地 IsVisible：折叠容器（如配对码区）
        // 内的子控件本地仍可见，必须跳过，Tab 才不会落到隐藏字段上。
        |> Seq.filter (fun c -> c.Focusable && c.IsEffectivelyVisible && c.IsEnabled)
        |> Array.ofSeq

    let focusFirst (content: Control) =
        focusables content
        |> Array.tryHead
        |> Option.iter (fun c -> Dispatcher.UIThread.Post(fun () -> c.Focus() |> ignore))

    let wrapForDialog (content: Control) : Control =
        match content with
        | :? ScrollViewer -> content
        | _ ->
            ScrollViewer(
                Content = content,
                HorizontalScrollBarVisibility = Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Primitives.ScrollBarVisibility.Auto)
            :> Control

    let trapTab (content: Control) (e: KeyEventArgs) =
        if e.Key = Key.Tab then
            let items = focusables content
            if items.Length > 0 then
                let focused =
                    match TopLevel.GetTopLevel root with
                    | null -> null
                    | top ->
                        match top.FocusManager with
                        | null -> null
                        | manager -> manager.GetFocusedElement()
                let current =
                    items
                    |> Array.tryFindIndex (fun item -> obj.ReferenceEquals(item, focused))
                    |> Option.defaultValue -1
                let backwards = e.KeyModifiers.HasFlag KeyModifiers.Shift
                let next =
                    if backwards then
                        if current <= 0 then items.Length - 1 else current - 1
                    else if current < 0 || current >= items.Length - 1 then 0
                    else current + 1
                e.Handled <- true
                items[next].Focus(NavigationMethod.Tab) |> ignore

    let fitDialogToViewport () =
        let width = max 0.0 (root.Bounds.Width - viewportInset * 2.0)
        let height = max 0.0 (root.Bounds.Height - viewportInset * 2.0)
        if width > 0.0 then
            dialogCard.MaxWidth <- width
            if dialogPreferredWidth > 0.0 then dialogCard.Width <- min dialogPreferredWidth width
        if height > 0.0 then dialogCard.MaxHeight <- height

    let fitPopupToViewport () =
        let width = max 0.0 (root.Bounds.Width - viewportInset * 2.0)
        let height = max 0.0 (root.Bounds.Height - viewportInset * 2.0)
        if width > 0.0 then
            popupCard.MaxWidth <- width
            popupCard.MinWidth <- min popupPreferredMinWidth width
        if height > 0.0 then popupCard.MaxHeight <- height

    let fitToastsToViewport () =
        let width = max 0.0 (root.Bounds.Width - viewportInset * 2.0)
        if width > 0.0 then
            toastStack.MaxWidth <- min ContentMetrics.toastMaxWidth width
            for child in toastStack.Children do
                child.MaxWidth <- toastStack.MaxWidth
        // 底部已有 space8 外边距：再按视口高度钳住 MaxHeight，
        // 多条堆叠时顶部仍保留 viewportInset，不顶到视口上缘。
        let height = max 0.0 (root.Bounds.Height - Tokens.space8 - viewportInset)
        if height > 0.0 then
            toastStack.MaxHeight <- height

    let positionPopup () =
        match popupAnchor with
        | Some anchor when popupCard.IsVisible ->
            try
                let point = anchor.TranslatePoint(Point(0.0, anchor.Bounds.Height + popupGap), root)
                if point.HasValue then
                    let p = point.Value
                    fitPopupToViewport ()
                    let width = min popupCard.MaxWidth (max popupCard.Bounds.Width popupCard.MinWidth)
                    let height = min popupCard.MaxHeight popupCard.Bounds.Height
                    let x = if popupAlignRight then p.X + anchor.Bounds.Width - width else p.X
                    let clampedX = Math.Clamp(x, viewportInset, max viewportInset (root.Bounds.Width - width - viewportInset))
                    let flipUp = p.Y + height > root.Bounds.Height - viewportInset
                    let candidateY =
                        if flipUp then p.Y - anchor.Bounds.Height - height - popupGap * 2.0 else p.Y
                    let maxY = max viewportInset (root.Bounds.Height - height - viewportInset)
                    let clampedY = Math.Clamp(candidateY, viewportInset, maxY)
                    popupCard.Margin <- Thickness(clampedX, clampedY, 0.0, 0.0)
            with _ -> ()
        | _ -> ()

    do
        dialogCard.BoxShadow <- Tokens.shadowDialog ()
        popupCard.BoxShadow <- Tokens.shadowPopup ()
        Tokens.Changed.Publish.Add(fun _ ->
            dialogCard.BoxShadow <- Tokens.shadowDialog ()
            popupCard.BoxShadow <- Tokens.shadowPopup ())
        for layer in [ scrim :> Control; dialogCard :> Control; popupCatcher :> Control; popupCard :> Control; toastStack :> Control ] do
            Grid.SetColumnSpan(layer, 8)
            Grid.SetRowSpan(layer, 8)
            root.Children.Add layer |> ignore
        // 底层滚动时浮层锚点会脱节：滚轮不经过 popupCatcher 的点击捕获，
        // 因此层外滚轮像层外点击一样直接关闭，避免浮层悬在错误位置；
        // 落在 popupCard 内的滚轮放行给内部 ScrollViewer，长菜单可在浮层内滚动。
        root.PointerWheelChanged.Add(fun e ->
            if popupCard.IsVisible then
                let inside =
                    try
                        popupCard.Bounds.Contains(e.GetPosition root)
                    with _ -> false
                if not inside then
                    popupCatcher.IsVisible <- false
                    popupCard.IsVisible <- false
                    popupCard.Child <- null
                    popupAnchor <- None
                    restorePopupFocus ())

    // scrim 只做不透明度补间（与 MessageCard 复制确认同一机制）：淡入淡出都
    // 不碰几何；Duration 经 MotionPolicy 降级，减弱动效时时间零，等同瞬时切换。
    do
        let fade = Avalonia.Animation.DoubleTransition()
        fade.Property <- Visual.OpacityProperty
        fade.Duration <- (if MotionPolicy.isReduced () then TimeSpan.Zero else MotionLedger.scrimFade)
        fade.Easing <- MotionPolicy.easeOutCubic
        let fadeTransitions = Avalonia.Animation.Transitions()
        fadeTransitions.Add fade
        scrim.Transitions <- fadeTransitions
        scrim.Opacity <- 0.0

    member private _.HideDialog() =
        dialogCard.IsVisible <- false
        dialogCard.Child <- null
        // scrim 对称淡出：卡片与焦点恢复同步完成，遮罩只是淡到 0 后自行隐藏；
        // 淡出期间不可命中，点击穿回聊天区。减弱动效时立即隐藏。
        stopScrimFadeOut ()
        if MotionPolicy.isReduced () then
            scrim.IsVisible <- false
            scrim.IsHitTestVisible <- true
            scrim.Opacity <- 0.0
        else
            scrim.IsHitTestVisible <- false
            scrim.Opacity <- 0.0
            let fade = DispatcherTimer(Interval = MotionLedger.scrimFade)
            fade.Tick.Add(fun _ ->
                fade.Stop()
                if scrimFadeOut = Some fade then scrimFadeOut <- None
                scrim.IsVisible <- false)
            scrimFadeOut <- Some fade
            fade.Start()

    member _.Root = root

    member this.CloseDialog() =
        let callback = onDialogClosed
        onDialogClosed <- id
        if dialogStack.Count > 0 then
            // 嵌套关闭：恢复被盖住的子内容 + onClosed + 守卫，守卫闭包原样保留；
            // 焦点记忆不动（仍指向最初的触发源），只把焦点送回恢复后的内容。
            let child, width, closed, guard = dialogStack[dialogStack.Count - 1]
            dialogStack.RemoveAt(dialogStack.Count - 1)
            dialogCard.Child <- child
            dialogPreferredWidth <- width
            onDialogClosed <- closed
            dialogGuard <- guard
            fitDialogToViewport ()
            if not (isNull child) then focusFirst child
            callback ()
        else
            this.HideDialog()
            restoreDialogFocus ()
            callback ()

    member this.ShowDialog(content: Control, width: float, ?onClosed: unit -> unit, ?canDismiss: unit -> bool) =
        if dialogCard.IsVisible then
            // 嵌套打开：保留当前子内容 + onClosed + 守卫，关闭时原样恢复。
            dialogStack.Add((dialogCard.Child, dialogPreferredWidth, onDialogClosed, dialogGuard))
        else rememberDialogFocus ()
        onDialogClosed <- defaultArg onClosed id
        dialogGuard <- defaultArg canDismiss (fun () -> true)
        let hosted = wrapForDialog content
        dialogCard.Child <- hosted
        dialogPreferredWidth <- width
        fitDialogToViewport ()
        if root.Bounds.Width <= 0.0 then dialogCard.Width <- width
        dialogCard.IsVisible <- true
        scrim.IsVisible <- true
        // 停掉在途淡出并恢复命中：0→1 淡入由 scrim 上的 opacity 补间完成；
        // 减弱动效时 Duration 为零，等同瞬时全显。
        stopScrimFadeOut ()
        scrim.IsHitTestVisible <- true
        scrim.Opacity <- 1.0
        hosted.KeyDown.Add(fun e -> this.TrapTab(hosted, e))
        focusFirst content

    member _.IsDialogOpen = dialogCard.IsVisible

    member this.ClosePopup() =
        popupCatcher.IsVisible <- false
        popupCard.IsVisible <- false
        popupCard.Child <- null
        popupAnchor <- None
        restorePopupFocus ()

    /// 在 `anchor` 附近打开浮层。`alignRight` 表示浮层右边缘与锚点右边缘对齐。
    member this.ShowPopup(anchor: Control, content: Control, alignRight: bool, ?minWidth: float) =
        if not popupCard.IsVisible then rememberPopupFocus ()
        popupCard.Child <- content
        popupAnchor <- Some anchor
        lastPopupAnchor <- Some anchor
        popupAlignRight <- alignRight
        popupPreferredMinWidth <- defaultArg minWidth 180.0
        fitPopupToViewport ()
        if root.Bounds.Width <= 0.0 then popupCard.MinWidth <- popupPreferredMinWidth
        popupCatcher.IsVisible <- true
        popupCard.IsVisible <- true
        content.KeyDown.Add(fun e -> this.TrapTab(content, e))
        // 需要等一帧拿到浮层实际尺寸，才能算出不越界的位置
        Dispatcher.UIThread.Post(fun () ->
            positionPopup ()
            focusFirst content
            // 首帧 Bounds 可能仍是旧尺寸（翻转判断用错高度）：布局完成后再重算一次位置。
            Dispatcher.UIThread.Post(fun () ->
                if popupCard.IsVisible then positionPopup ()))

    member _.IsPopupOpen = popupCard.IsVisible

    member _.LastPopupFocus: IInputElement option = lastPopupFocus
    member _.LastPopupAnchor: Control option = lastPopupAnchor

    member _.Descendants(control: Control) = descendants control
    member _.Focusables(content: Control) = focusables content

    /// Tab 键盘包含：把 Tab / Shift+Tab 圈在 content 的可聚焦集合内循环，到头回绕；
    /// 只拦 Tab，其余键原样透出，集合为空时不做任何事。对话框 / 浮层与 compact 抽屉
    /// （Sidebar）共用这同一份实现，不再各处维护一份循环逻辑。
    member _.TrapTab(content: Control, e: KeyEventArgs) = trapTab content e

    /// 提示条：只给跨上下文的成功确认与无更好归宿的短暂系统状态。
    /// 表单校验走行内错误、可恢复的错误走常驻入口、正文已有表达的不再弹条；
    /// 同文去重且最多同时保留 toastCap 条，失败默认停留更久以便读完。
    member _.Toast(message: string, tone: ToastTone) =
        if not (String.IsNullOrWhiteSpace message) then
            let accentBrush =
                match tone with
                | Neutral -> Tokens.textMuted :> IBrush
                | Success -> Tokens.success :> IBrush
                | Warning -> Tokens.warning :> IBrush
                | Info -> Tokens.info :> IBrush
                | Failure -> Tokens.danger :> IBrush
            // 语气条只做 2px 的弱提示：不抢正文，用现有不透明度阶梯压暗，无位移、无新 token。
            let bar = Border(Width = ControlMetrics.toastAccentWidth, CornerRadius = CornerRadius Tokens.radiusPill, Background = accentBrush, Opacity = Tokens.opacitySubtle)
            let body =
                TextBlock(
                    Text = message,
                    FontSize = Tokens.fontSmall,
                    Foreground = Tokens.text,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    LineHeight = ReadingRhythm.helperLineHeight,
                    VerticalAlignment = VerticalAlignment.Center)
            let bodyScroller =
                ScrollViewer(
                    Content = body,
                    MaxHeight = LayoutPolicy.toastBodyMaxHeight,
                    HorizontalScrollBarVisibility = Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Primitives.ScrollBarVisibility.Auto)
            let row = Grid(ColumnSpacing = Tokens.space3)
            row.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
            row.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
            Grid.SetColumn(bar, 0)
            Grid.SetColumn(bodyScroller, 1)
            row.Children.Add bar
            row.Children.Add bodyScroller
            let toast =
                ActionBorder(
                    Background = Tokens.surfaceRaised,
                    // 提示条归入同一浮层家族：hairline 描边 + shadowPopup 抬升。
                    BorderBrush = Tokens.hairline,
                    BorderThickness = Thickness ControlMetrics.borderWidth,
                    CornerRadius = CornerRadius Tokens.radiusLg,
                    MaxWidth = ContentMetrics.toastMaxWidth,
                    Padding = Thickness(Tokens.space4, Tokens.space3),
                    Child = row,
                    // 瞬态提示条不进 Tab 序：读屏经由下面的 live region 公告，键盘关闭走全局 Escape。
                    Focusable = false)
            toast.BoxShadow <- Tokens.shadowPopup ()
            // toast 进场淡入 / 退场淡出：只补间 Opacity（约 toastFade=300ms），走 MotionPolicy 门控，
            // 共用统一缓动 easeOutCubic；不做任何位移/尺寸动画。减弱动效时时长为零，等同瞬时。
            let toastFadeT = Avalonia.Animation.DoubleTransition()
            toastFadeT.Property <- Visual.OpacityProperty
            toastFadeT.Duration <- (if MotionPolicy.isReduced () then TimeSpan.Zero else MotionLedger.toastFade)
            toastFadeT.Easing <- MotionPolicy.easeOutCubic
            let toastTransitions = Avalonia.Animation.Transitions()
            toastTransitions.Add toastFadeT
            toast.Transitions <- toastTransitions
            toast.Opacity <- 0.0
            toast.Cursor <- new Cursor(StandardCursorType.Hand)
            ToolTip.SetTip(toast, "点击关闭")
            AutomationProperties.SetName(toast, message)
            AutomationProperties.SetLiveSetting(
                toast,
                match tone with
                | Failure
                | Warning -> AutomationLiveSetting.Assertive
                | _ -> AutomationLiveSetting.Polite)
            let mutable removed = false
            // 默认停留时长按语气取 MotionLedger：毫秒唯一来源是 toastDwell{Failure,Warning,Default}；
            // 时间跨度转毫秒沿用既有 remainingMs 的浮点递减算法，不再写 9000.0/6500.0/4000.0 字面量。
            let mutable paused = false
            let mutable remainingMs =
                match tone with
                | Failure -> MotionLedger.toastDwellFailure.TotalMilliseconds
                | Warning -> MotionLedger.toastDwellWarning.TotalMilliseconds
                | _ -> MotionLedger.toastDwellDefault.TotalMilliseconds
            let expire = new DispatcherTimer(Interval = MotionLedger.toastDismissTick)
            let remove () =
                if not removed then
                    removed <- true
                    expire.Stop()
                    // 去重/计数立即释放：同文再出或后续入条不再受本条约限。
                    toastEntries.RemoveAll(fun (text, _) -> text = message) |> ignore
                    // 同步移除节点：本仓库无显示测试制度无法确定性泵动 DispatcherTimer，
                    // 异步退场没有可重复验收路径（Manager 决策：退场改回同步，仅保留入场淡入）。
                    // Escape / 点击 / 超时 / 上限挤出四条路径行为一致。
                    toastStack.Children.Remove toast |> ignore
            // 同文只留最新一条：先清掉旧的，计数里也不再占位。
            toastEntries
            |> Seq.filter (fun (text, _) -> text = message)
            |> Seq.map snd
            |> Array.ofSeq
            |> Array.iter (fun dismiss -> dismiss ())
            // 新条挤掉最旧的：提示条永远是少数派，不盖住内容。
            while toastEntries.Count >= toastCap do
                let _, dismissOldest = toastEntries[0]
                dismissOldest ()
            toastEntries.Add(message, remove)
            Ui.onClick toast remove
            // Escape 唯一归属 HandleEscape（topmost-first）：这里不再自带局部 Esc 处理。
            // 提示条 Focusable=false（见上）：永远无法获得焦点，GotFocus / LostFocus 以及
            // PointerExited 里按焦点读 paused 的分支永不触发，均为死代码，一并删除；
            // 暂停计时只保留 hover 驱动——指针进入即暂停、离开即恢复（IsFocused 恒为 false）。
            toast.PointerEntered.Add(fun _ -> paused <- true)
            toast.PointerExited.Add(fun _ -> paused <- false)
            toastStack.Children.Add toast
            // 触发进场淡入（0→1）；减弱动效时 transition 时长为零，等同瞬时全显。
            toast.Opacity <- 1.0
            fitToastsToViewport ()
            expire.Tick.Add(fun _ ->
                if not paused then
                    remainingMs <- remainingMs - expire.Interval.TotalMilliseconds
                    if remainingMs <= 0.0 then remove ())
            expire.Start()

    /// Esc / 点击层外的统一关闭入口，由 AppShell 在根视图上转发按键。
    /// 唯一的 Escape 归属（topmost-first：popup → dialog → toast）：调用方消费返回的
    /// true，不再落到 AppShell 的 StopGeneration；守卫拦截的关闭同样消费，避免误停生成。
    member this.HandleEscape() : bool =
        if popupCard.IsVisible then
            this.ClosePopup()
            true
        elif dialogCard.IsVisible then
            if dialogGuard () then this.CloseDialog()
            // 守卫拦截（提交 pending 中）也不透出：不关闭但已消费。
            true
        elif toastEntries.Count > 0 then
            let _, dismissNewest = toastEntries[toastEntries.Count - 1]
            dismissNewest ()
            true
        else
            false

    member this.WireDismiss() =
        popupCatcher.PointerPressed.Add(fun _ -> this.ClosePopup())
        // scrim 与 Escape 同守卫：提交 pending 中点 scrim 也不关闭。
        scrim.PointerPressed.Add(fun _ ->
            if dialogGuard () then this.CloseDialog())
        root.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then
                fitDialogToViewport ()
                fitPopupToViewport ()
                fitToastsToViewport ()
                if popupCard.IsVisible then Dispatcher.UIThread.Post positionPopup)
