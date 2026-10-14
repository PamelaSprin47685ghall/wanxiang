namespace Wanxiang.UI

open System
open Avalonia.Controls
open Avalonia.Threading

/// 复制按钮的确认反馈：保持、淡出与复位的计时归 CopyConfirm；这里只有一个计时器，
/// 指向它给出的下一个到点时刻，并按它给出的形态改透明度、交给调用方改图标与文字。
module CopyFeedback =

    /// 返回「刚复制」时调用的函数。离开可视树立即复位。
    let attach (button: Control) (show: CopyVisual -> unit) : unit -> unit =
        let confirm = CopyConfirm()
        let mutable timer: DispatcherTimer option = None
        let stop () =
            timer |> Option.iter (fun t -> t.Stop())
            timer <- None
        let rec render () =
            stop ()
            let visual = confirm.Tick DateTimeOffset.UtcNow
            button.Opacity <- (match visual with CopyVisual.Fading -> Tokens.opacityCopyConfirmFade | _ -> 1.0)
            show visual
            match confirm.NextDue with
            | Some due ->
                let wait = due - DateTimeOffset.UtcNow
                let t = new DispatcherTimer(Interval = (if wait > TimeSpan.FromMilliseconds 1.0 then wait else TimeSpan.FromMilliseconds 1.0))
                t.Tick.Add(fun _ ->
                    t.Stop()
                    if timer = Some t then render ())
                timer <- Some t
                t.Start()
            | None -> ()
        button.DetachedFromVisualTree.Add(fun _ ->
            stop ()
            confirm.Detached()
            button.Opacity <- 1.0
            show CopyVisual.Default)
        fun () ->
            confirm.Copied(DateTimeOffset.UtcNow, MotionPolicy.isReduced ())
            render ()
