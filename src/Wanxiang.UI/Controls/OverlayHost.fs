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
/// 旧 OverlayHost 的门面：对话框 / 浮层 / 提示条全部交给 Wanxiang2.OverlayDesk，
/// 外观由 SharedTheme 的活画笔与活值给出（主题补间、减弱动效都跟着走）。
type OverlayHost(root: Grid) =

    static let live (v: 'T) = Wanxiang2.Theme.LiveValue v
    static let thick (v: float) = live (Thickness v)
    static let reducedTo (span: TimeSpan) =
        let value = live (if MotionPolicy.isReduced () then TimeSpan.Zero else span)
        MotionPolicy.Changed.Add(fun reduced -> value.Set(if reduced then TimeSpan.Zero else span))
        value
    static let followTheme (read: unit -> BoxShadows) =
        let value = live (read ())
        Tokens.Changed.Publish.Add(fun _ -> value.Set(read ()))
        value

    static let toTone (tone: ToastTone) =
        match tone with
        | Neutral -> Wanxiang2.ToastStrip.ToastTone.Neutral
        | Success -> Wanxiang2.ToastStrip.ToastTone.Success
        | Warning -> Wanxiang2.ToastStrip.ToastTone.Warning
        | Failure -> Wanxiang2.ToastStrip.ToastTone.Failure
        | Info -> Wanxiang2.ToastStrip.ToastTone.Info

    static let look () : Wanxiang2.OverlayDesk.OverlayDeskLook =
        let popupShadow = followTheme Tokens.shadowPopup
        { Layer =
            { Scrim = Tokens.scrim
              Surface = Tokens.surfaceRaised
              Edge = Tokens.hairline
              EdgeWidth = thick ControlMetrics.borderWidth
              DialogRadius = live (CornerRadius Spacing.Radius.xl)
              PopupRadius = live (CornerRadius Spacing.Radius.lg)
              DialogPadding = thick Spacing.space4xl
              PopupPadding = live (Thickness(Spacing.spaceXs, Spacing.spaceXs))
              DialogShadow = followTheme Tokens.shadowDialog
              PopupShadow = popupShadow
              ViewportInset = live Spacing.space2xl
              PopupGap = live Spacing.spaceMd
              PopupMinWidth = live 180.0
              ScrimFade = reducedTo MotionLedger.scrimFade
              ScrimEasing = MotionPolicy.easeOutCubic }
          Toasts =
            { Surface = Tokens.surfaceRaised
              Edge = Tokens.hairline
              Text = Tokens.text
              Accent =
                (fun tone ->
                    match tone with
                    | Wanxiang2.ToastStrip.ToastTone.Neutral -> Tokens.textMuted :> IBrush
                    | Wanxiang2.ToastStrip.ToastTone.Success -> Tokens.success :> IBrush
                    | Wanxiang2.ToastStrip.ToastTone.Warning -> Tokens.warning :> IBrush
                    | Wanxiang2.ToastStrip.ToastTone.Info -> Tokens.info :> IBrush
                    | Wanxiang2.ToastStrip.ToastTone.Failure -> Tokens.danger :> IBrush)
              AccentWidth = live ControlMetrics.toastAccentWidth
              AccentRadius = live (CornerRadius Spacing.Radius.pill)
              AccentOpacity = live Tokens.opacitySubtle
              EdgeWidth = thick ControlMetrics.borderWidth
              Radius = live (CornerRadius Spacing.Radius.lg)
              Padding = live (Thickness(Spacing.space2xl, Spacing.spaceXl))
              Shadow = popupShadow
              FontSize = live Tokens.fontSmall
              LineHeight = live ReadingRhythm.helperLineHeight
              ColumnSpacing = live Spacing.spaceXl
              Spacing = live Spacing.spaceMd
              Margin = live (Thickness(Spacing.space4xl, 0.0, Spacing.space4xl, Spacing.space6xl))
              MaxWidth = live ContentMetrics.toastMaxWidth
              BodyMaxHeight = live LayoutPolicy.toastBodyMaxHeight
              Fade = reducedTo MotionLedger.toastFade
              FadeEasing = MotionPolicy.easeOutCubic }
          Menu =
            { Text = Tokens.text
              Muted = Tokens.textMuted
              Faint = Tokens.textFaint
              Danger = Tokens.danger
              Accent = Tokens.accent
              Check = (fun brush -> Icons.check brush)
              Chevron = (fun brush -> Icons.chevronDown brush)
              FontSize = live Tokens.fontSmall
              HintFontSize = live Tokens.fontMicro
              IconSlotWidth = live ControlMetrics.menuIconSlotWidth
              IconHeight = live Tokens.iconGlyph
              RightSlotMinWidth = live ControlMetrics.menuRightSlotMinWidth
              HintMargin = live (Thickness(Spacing.space2xl, 0.0, 0.0, 0.0))
              ColumnSpacing = live Spacing.spaceMd
              ItemPadding = live (Thickness(Spacing.spaceXl, ControlMetrics.menuItemPaddingY))
              ItemRadius = live (CornerRadius Spacing.Radius.sm)
              ItemMinHeight = live ControlMetrics.menuItemMinHeight
              ItemSpacing = live Spacing.spaceXXs
              MaxHeight = live LayoutPolicy.menuMaxHeight
              DividerMargin = live (Thickness(Spacing.spaceMd, Spacing.spaceXs))
              HeaderMargin = live (Thickness(Spacing.spaceXl, Spacing.spaceMd, Spacing.spaceXl, Spacing.spaceXs))
              SelectPadding = live (Thickness(Spacing.spaceXl, ControlMetrics.selectButtonPaddingY))
              SelectRadius = live (CornerRadius Spacing.Radius.lg)
              SelectEdgeWidth = thick ControlMetrics.borderWidth
              SelectCaptionMaxWidth = live 260.0 }
          MenuMinWidth = live 200.0
          GroupedMenuMinWidth = live 240.0
          DwellFailureMs = live MotionLedger.toastDwellFailure.TotalMilliseconds
          DwellWarningMs = live MotionLedger.toastDwellWarning.TotalMilliseconds
          DwellDefaultMs = live MotionLedger.toastDwellDefault.TotalMilliseconds
          DismissTick = MotionLedger.toastDismissTick }

    let desk = Wanxiang2.OverlayDesk.OverlayDesk(root, look ())
    let layer = desk.Layer

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

    member _.Root = root
    member _.Desk = desk

    member _.CloseDialog() = layer.CloseDialog()

    member _.ShowDialog(content: Control, width: float, ?onClosed: unit -> unit, ?canDismiss: unit -> bool, ?initialFocus: Control, ?scrollWholeContent: bool) =
        layer.ShowDialog(content, width, ?onClosed = onClosed, ?canDismiss = canDismiss, ?initialFocus = initialFocus, ?scrollWholeContent = scrollWholeContent)

    member _.IsDialogOpen = layer.IsDialogOpen
    member _.ClosePopup() = layer.ClosePopup()

    member _.ShowPopup(anchor: Control, content: Control, alignRight: bool, ?minWidth: float) =
        layer.ShowPopup(anchor, content, alignRight, ?minWidth = minWidth)

    member _.IsPopupOpen = layer.IsPopupOpen
    member _.LastPopupFocus: IInputElement option = layer.LastPopupFocus
    member _.LastPopupAnchor: Control option = layer.LastPopupAnchor
    member _.Descendants(control: Control) = descendants control
    member _.Focusables(content: Control) = layer.Focusables content
    member _.TrapTab(content: Control, e: KeyEventArgs) = layer.TrapTab(content, e)

    member _.Toast(message: string, tone: ToastTone) = desk.Toast(message, toTone tone)

    member _.ToastWith(message: string, tone: ToastTone, action: (string * (unit -> unit)) option) =
        desk.ToastWith(message, toTone tone, action)

    member _.TryInvokeToastAction(label: string) : bool = desk.TryInvokeToastAction label
    member _.FocusToastAction() : bool = desk.FocusToastAction()
    member _.SetToastFocusHome(home: unit -> unit) = desk.SetToastFocusHome home
    member _.ToastActionButtons: Control list = desk.ToastActionButtons
    member _.HasToastAction(label: string) : bool = desk.HasToastAction label
    member _.HandleEscape() : bool = desk.HandleEscape()
    member _.WireDismiss() = desk.WireDismiss()
