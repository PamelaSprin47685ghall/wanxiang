namespace Wanxiang.UI

open System
open Avalonia.Controls
open Avalonia.Controls.Presenters
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.VisualTree

/// 决策 384：输入法组合（中文拼音/五笔候选）期间，回车、上下键、Esc、Tab 归输入法，
/// 绝不触发发送/提交/历史召回/关闭。全应用唯一守卫：TextBox 隧道阶段的类处理器，
/// 早于任何视图自己挂的 KeyDown（冒泡）与 TextBox 自身的回车换行处理。
/// 判据是 TextPresenter.PreeditText 非空（桌面 Avalonia 与 PWA 浏览器后端都经它显示预编辑），
/// 外加平台把按键标成 Key.ImeProcessed 的情形（Windows IMM / 浏览器 keyCode 229）。
module ImeGuard =
    let presenterOf (tb: TextBox) =
        tb.GetVisualDescendants()
        |> Seq.tryPick (function :? TextPresenter as p -> Some p | _ -> None)

    let isComposing (tb: TextBox) =
        presenterOf tb |> Option.exists (fun p -> not (String.IsNullOrEmpty p.PreeditText))

    /// 组合期间归输入法的键。
    let isImeOwnedKey (k: Key) =
        k = Key.Enter || k = Key.Up || k = Key.Down || k = Key.Escape || k = Key.Tab

    /// 纯判定，便于单元测试：是否应吞掉这次按键。
    let shouldSwallow (composing: bool) (k: Key) =
        k = Key.ImeProcessed || (composing && isImeOwnedKey k)

    let mutable private installed = false

    /// 幂等；App.Initialize（桌面与 PWA 共用）调用一次。
    let install () =
        if not installed then
            installed <- true
            InputElement.KeyDownEvent.AddClassHandler<TextBox>(
                (fun (tb: TextBox) (e: KeyEventArgs) ->
                    if not e.Handled && shouldSwallow (isComposing tb) e.Key then
                        e.Handled <- true),
                RoutingStrategies.Tunnel)
            |> ignore
