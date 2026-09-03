namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading

/// 提示条语气。
type ToastTone =
    | Neutral
    | Success
    | Warning
    | Failure

/// 覆盖层宿主：对话框、浮层菜单、提示条都画在应用最外层 Grid 上。
///
/// 桌面与 PWA 共用同一套 UI，平台弹窗行为差异大，因此一律自绘。
/// 一个应用只有一个实例，由 AppShell 创建并注入给各视图。
type OverlayHost(root: Grid) =

    let viewportInset = Tokens.space3
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
            BorderBrush = Tokens.border,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusXl,
            Padding = Thickness Tokens.space6,
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
            BorderBrush = Tokens.border,
            BorderThickness = Thickness 1.0,
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
    let mutable previousDialogFocus: IInputElement option = None
    let mutable previousPopupFocus: IInputElement option = None
    let mutable dialogPreferredWidth = 0.0
    let mutable popupPreferredMinWidth = 180.0
    let mutable popupAnchor: Control option = None
    let mutable popupAlignRight = false

    let currentFocus () =
        match TopLevel.GetTopLevel root with
        | null -> None
        | top ->
            match top.FocusManager with
            | null -> None
            | manager -> manager.GetFocusedElement() |> Option.ofObj

    let restoreFocus (previous: IInputElement option) =
        match previous with
        | Some (:? Control as control) when control.IsVisible && control.IsEnabled ->
            Dispatcher.UIThread.Post(fun () -> control.Focus() |> ignore)
        | _ -> ()

    let rememberDialogFocus () =
        previousDialogFocus <- currentFocus ()

    let rememberPopupFocus () =
        previousPopupFocus <- currentFocus ()

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
        |> Seq.filter (fun c -> c.Focusable && c.IsVisible && c.IsEnabled)
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
        // 因此像层外点击一样直接关闭，避免浮层悬在错误位置。
        root.PointerWheelChanged.Add(fun _ ->
            if popupCard.IsVisible then
                popupCatcher.IsVisible <- false
                popupCard.IsVisible <- false
                popupCard.Child <- null
                popupAnchor <- None
                restorePopupFocus ())

    member private _.HideDialog() =
        scrim.IsVisible <- false
        dialogCard.IsVisible <- false
        dialogCard.Child <- null

    member _.Root = root

    member this.CloseDialog() =
        let callback = onDialogClosed
        onDialogClosed <- id
        this.HideDialog()
        restoreDialogFocus ()
        callback ()

    member this.ShowDialog(content: Control, width: float, ?onClosed: unit -> unit) =
        if not dialogCard.IsVisible then rememberDialogFocus ()
        onDialogClosed <- defaultArg onClosed id
        let hosted = wrapForDialog content
        dialogCard.Child <- hosted
        dialogPreferredWidth <- width
        fitDialogToViewport ()
        if root.Bounds.Width <= 0.0 then dialogCard.Width <- width
        dialogCard.IsVisible <- true
        scrim.IsVisible <- true
        hosted.KeyDown.Add(fun e -> trapTab hosted e)
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
        popupAlignRight <- alignRight
        popupPreferredMinWidth <- defaultArg minWidth 180.0
        fitPopupToViewport ()
        if root.Bounds.Width <= 0.0 then popupCard.MinWidth <- popupPreferredMinWidth
        popupCatcher.IsVisible <- true
        popupCard.IsVisible <- true
        content.KeyDown.Add(fun e -> trapTab content e)
        // 需要等一帧拿到浮层实际尺寸，才能算出不越界的位置
        Dispatcher.UIThread.Post(fun () ->
            positionPopup ()
            focusFirst content)

    member _.IsPopupOpen = popupCard.IsVisible

    /// 提示条：自动消失，可叠加多条。错误默认停留更久。
    member _.Toast(message: string, tone: ToastTone) =
        if not (String.IsNullOrWhiteSpace message) then
            let accentBrush =
                match tone with
                | Neutral -> Tokens.textMuted :> IBrush
                | Success -> Tokens.success :> IBrush
                | Warning -> Tokens.warning :> IBrush
                | Failure -> Tokens.danger :> IBrush
            let bar = Border(Width = ControlMetrics.toastAccentWidth, CornerRadius = CornerRadius Tokens.radiusPill, Background = accentBrush)
            let body =
                TextBlock(
                    Text = message,
                    FontSize = Tokens.fontSmall,
                    Foreground = Tokens.text,
                    TextWrapping = TextWrapping.Wrap,
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
                    BorderBrush = Tokens.border,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius Tokens.radiusLg,
                    Padding = Thickness(Tokens.space4, Tokens.space3),
                    Child = row,
                    Focusable = true)
            toast.BoxShadow <- Tokens.shadowPopup ()
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
            let mutable paused = false
            let mutable remainingMs = match tone with Failure -> 9000.0 | Warning -> 6500.0 | _ -> 4000.0
            let expire = new DispatcherTimer(Interval = TimeSpan.FromMilliseconds 200.0)
            let remove () =
                if not removed then
                    removed <- true
                    expire.Stop()
                    toastStack.Children.Remove toast |> ignore
            Ui.onClick toast remove
            toast.PointerEntered.Add(fun _ -> paused <- true)
            toast.PointerExited.Add(fun _ -> paused <- toast.IsFocused)
            toast.GotFocus.Add(fun _ -> paused <- true)
            toast.LostFocus.Add(fun _ -> paused <- toast.IsPointerOver)
            toastStack.Children.Add toast
            fitToastsToViewport ()
            expire.Tick.Add(fun _ ->
                if not paused then
                    remainingMs <- remainingMs - expire.Interval.TotalMilliseconds
                    if remainingMs <= 0.0 then remove ())
            expire.Start()

    /// Esc / 点击层外的统一关闭入口，由 AppShell 在根视图上转发按键。
    member this.HandleEscape() : bool =
        if popupCard.IsVisible then
            this.ClosePopup()
            true
        elif dialogCard.IsVisible then
            this.CloseDialog()
            true
        else
            false

    member this.WireDismiss() =
        popupCatcher.PointerPressed.Add(fun _ -> this.ClosePopup())
        scrim.PointerPressed.Add(fun _ -> this.CloseDialog())
        root.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then
                fitDialogToViewport ()
                fitPopupToViewport ()
                fitToastsToViewport ()
                if popupCard.IsVisible then Dispatcher.UIThread.Post positionPopup)
