namespace Wanxiang.UI

open System.Runtime.CompilerServices
open Wanxiang.Core
open Wanxiang.Client
open Wanxiang2.ConnectPane
open Wanxiang2.DialogsDesk

/// 连接对话框交给宿主的入口（DialogsDesk.ConnectDeskHandle 的旧名字段）。
type ConnectDialogHandle =
    { info: string -> unit
      fail: ConnectionIssue -> unit
      startPairing: unit -> unit }

/// 连接对话框的回调（ConnectPane.ConnectAsk 的旧名字段）。
type ConnectRequest =
    { url: string
      token: string option
      usePairing: bool }

/// 旧 Views/Dialogs.fs 的柯里化入口，全部转给 DialogsDesk（每个 OverlayHost 一个）。
module Dialogs =
    let private desks = ConditionalWeakTable<OverlayHost, DialogsDesk>()

    let desk (overlay: OverlayHost) =
        desks.GetValue(overlay, fun o -> DialogsDesk(SharedTheme.manager, o.Desk))

    let prompt overlay title hint initial confirmLabel onConfirm =
        (desk overlay).Prompt(title, hint, initial, confirmLabel, onConfirm) |> ignore

    let confirmWithFocus overlay title body confirmLabel onConfirm dangerFirst =
        (desk overlay).ConfirmWithFocus(title, body, confirmLabel, onConfirm, dangerFirst) |> ignore

    let confirm overlay title body confirmLabel onConfirm =
        (desk overlay).Confirm(title, body, confirmLabel, onConfirm) |> ignore

    let editText overlay title initial confirmLabel onConfirm =
        (desk overlay).EditText(title, initial, confirmLabel, onConfirm) |> ignore

    let connectWithClosed overlay defaultUrl (onConnect: ConnectRequest -> unit) (onSubmitCode: string -> unit) (onClosed: unit -> unit) : ConnectDialogHandle =
        let h =
            (desk overlay).ConnectWithClosed(
                defaultUrl,
                (fun (a: ConnectAsk) -> onConnect { url = a.Url; token = a.Token; usePairing = a.UsePairing }),
                onSubmitCode,
                onClosed)
        { info = h.Info; fail = h.Fail; startPairing = h.StartPairing }

    let connect overlay defaultUrl onConnect onSubmitCode =
        connectWithClosed overlay defaultUrl onConnect onSubmitCode ignore

    let sessionSettings overlay (catalog: Catalog) (current: SessionConfig) onSave =
        (desk overlay).SessionSettings(catalog, current, onSave) |> ignore

    let shortcuts overlay = (desk overlay).Shortcuts() |> ignore
