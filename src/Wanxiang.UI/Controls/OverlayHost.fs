namespace Wanxiang.UI

open System
open Avalonia
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
            IsVisible = false,
            MaxHeight = 720.0)

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

    member private _.HideDialog() =
        scrim.IsVisible <- false
        dialogCard.IsVisible <- false
        dialogCard.Child <- null

    member _.Root = root

    member this.CloseDialog() =
        let callback = onDialogClosed
        onDialogClosed <- id
        this.HideDialog()
        callback ()

    member this.ShowDialog(content: Control, width: float, ?onClosed: unit -> unit) =
        onDialogClosed <- defaultArg onClosed id
        dialogCard.Child <- content
        dialogCard.Width <- width
        dialogCard.IsVisible <- true
        scrim.IsVisible <- true

    member _.IsDialogOpen = dialogCard.IsVisible

    member this.ClosePopup() =
        popupCatcher.IsVisible <- false
        popupCard.IsVisible <- false
        popupCard.Child <- null

    /// 在 `anchor` 附近打开浮层。`alignRight` 表示浮层右边缘与锚点右边缘对齐。
    member this.ShowPopup(anchor: Control, content: Control, alignRight: bool, ?minWidth: float) =
        popupCard.Child <- content
        popupCard.MinWidth <- defaultArg minWidth 180.0
        popupCatcher.IsVisible <- true
        popupCard.IsVisible <- true
        // 需要等一帧拿到浮层实际尺寸，才能算出不越界的位置
        Dispatcher.UIThread.Post(fun () ->
            try
                let point = anchor.TranslatePoint(Point(0.0, anchor.Bounds.Height + 6.0), root)
                if point.HasValue then
                    let p = point.Value
                    let width = max popupCard.Bounds.Width popupCard.MinWidth
                    let height = popupCard.Bounds.Height
                    let x =
                        if alignRight then p.X + anchor.Bounds.Width - width else p.X
                    let clampedX = Math.Clamp(x, Tokens.space2, max Tokens.space2 (root.Bounds.Width - width - Tokens.space2))
                    let flipUp = p.Y + height > root.Bounds.Height - Tokens.space2
                    let y =
                        if flipUp then max Tokens.space2 (p.Y - anchor.Bounds.Height - height - 12.0) else p.Y
                    popupCard.Margin <- Thickness(clampedX, y, 0.0, 0.0)
            with _ -> ())

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
            let bar = Border(Width = 3.0, CornerRadius = CornerRadius Tokens.radiusPill, Background = accentBrush)
            let body =
                TextBlock(
                    Text = message,
                    FontSize = Tokens.fontSmall,
                    Foreground = Tokens.text,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 560.0,
                    VerticalAlignment = VerticalAlignment.Center)
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space3)
            row.Children.Add bar
            row.Children.Add body
            let toast =
                Border(
                    Background = Tokens.surfaceRaised,
                    BorderBrush = Tokens.border,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius Tokens.radiusLg,
                    Padding = Thickness(Tokens.space4, Tokens.space3),
                    Child = row,
                    Opacity = 0.0)
            toast.BoxShadow <- Tokens.shadowPopup ()
            toast.Cursor <- new Cursor(StandardCursorType.Hand)
            ToolTip.SetTip(toast, "点击关闭")
            toast.PointerReleased.Add(fun _ -> toastStack.Children.Remove toast |> ignore)
            toastStack.Children.Add toast
            let lifetimeMs = match tone with Failure -> 6500 | Warning -> 5000 | _ -> 3200
            let fadeIn = new DispatcherTimer(Interval = TimeSpan.FromMilliseconds 16.0)
            fadeIn.Tick.Add(fun _ ->
                toast.Opacity <- min 1.0 (toast.Opacity + 0.12)
                if toast.Opacity >= 1.0 then fadeIn.Stop())
            fadeIn.Start()
            let expire = new DispatcherTimer(Interval = TimeSpan.FromMilliseconds(float lifetimeMs))
            expire.Tick.Add(fun _ ->
                expire.Stop()
                let fadeOut = new DispatcherTimer(Interval = TimeSpan.FromMilliseconds 16.0)
                fadeOut.Tick.Add(fun _ ->
                    toast.Opacity <- toast.Opacity - 0.1
                    if toast.Opacity <= 0.0 then
                        fadeOut.Stop()
                        toastStack.Children.Remove toast |> ignore)
                fadeOut.Start())
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
