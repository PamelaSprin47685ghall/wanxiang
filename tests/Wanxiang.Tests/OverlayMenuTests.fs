module Wanxiang.Tests.OverlayMenuTests

open System.Threading
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Threading
open Xunit
open Wanxiang.UI
open Wanxiang.Tests

let rec private descendants (control: Control) = seq {
    yield control
    match control with
    | :? Panel as panel ->
        for child in panel.Children do yield! descendants child
    | :? Decorator as decorator when not (isNull decorator.Child) ->
        yield! descendants decorator.Child
    | :? ContentControl as host ->
        match host.Content with
        | :? Control as child -> yield! descendants child
        | _ -> ()
    | _ -> ()
}

let private byAutomationName (root: Control) (name: string) =
    descendants root
    |> Seq.find (fun control -> AutomationProperties.GetName(control) = name)

/// 把内容装进一个真实窗口并跑一次布局，令浮层 / 焦点 / 定位的 UI 线程作业落地。
let private show (content: Control) width height =
    Headless.ensure ()
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

// toast 默认停留时长按语气取 MotionLedger：毫秒唯一来源为 toastDwell{Failure/Warning/Default}。
// 这里钉住令牌数值，既防止有人改回内联字面量，也防止 token 漂移。
[<Fact>]
let toast_dwell_timings_are_owned_by_MotionLedger () =
    Headless.ensure ()
    Assert.Equal(9000.0, MotionLedger.toastDwellFailure.TotalMilliseconds, 3)
    Assert.Equal(6500.0, MotionLedger.toastDwellWarning.TotalMilliseconds, 3)
    Assert.Equal(4000.0, MotionLedger.toastDwellDefault.TotalMilliseconds, 3)
    Assert.Equal(300.0, MotionLedger.toastFade.TotalMilliseconds, 3)

// 下拉钮垂直内边距引 ControlMetrics.selectButtonPaddingY（单一来源），水平仍是 space3。
// 数值恰为 6.0、与旧内联一致，故此测试锁的是「取值随 token 走」这一权威来源。
[<Fact>]
let select_button_vertical_padding_is_anchored_to_ControlMetrics () =
    Headless.ensure ()
    let host, setText = Menu.selectButton (OverlayHost(Grid())) "模型" (fun () -> [])
    Assert.Equal(ControlMetrics.selectButtonPaddingY, host.Padding.Top, 3)
    Assert.Equal(ControlMetrics.selectButtonPaddingY, host.Padding.Bottom, 3)
    Assert.Equal(Tokens.space3, host.Padding.Left, 3)
    Assert.Equal(Tokens.space3, host.Padding.Right, 3)
    setText "已切换"

// 按压反馈只写不透明度、不改几何契约：非按压态整层不透明度满值，MinHeight 仍由 ControlMetrics 供给。
// 注：本无头套件不注入原始指针事件，「按下→0.78 / 松开→1.0」的迁移不在此自动化覆盖，交真机/DevOps 判定。
[<Fact>]
let select_button_and_menu_items_keep_full_opacity_until_pressed () =
    Headless.ensure ()
    let root = Grid()
    let anchor = Border()
    root.Children.Add anchor
    let overlay = OverlayHost(root)
    overlay.WireDismiss()
    let selectHost, _ = Menu.selectButton overlay "模型" (fun () -> [])
    Assert.Equal(1.0, selectHost.Opacity, 3)
    let entry = MenuEntry.create "重命名" ignore |> MenuEntry.withHint "F2"
    let window = show root 360.0 320.0
    try
        Menu.show overlay anchor true [ entry ]
        Dispatcher.UIThread.RunJobs()
        let item = byAutomationName root "重命名" :?> Border
        Assert.Equal(1.0, item.Opacity, 3)
        Assert.Equal(ControlMetrics.menuItemMinHeight, item.MinHeight, 3)
    finally
        window.Close()

// 删除 focus 死代码后行为不变：提示条仍 Focusable=false、Escape 仍按 topmost-first 解散最新一条。
[<Fact>]
let toast_stays_non_focusable_and_escape_dismisses_it () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    overlay.Toast("已保存", ToastTone.Neutral)
    let toast =
        descendants root
        |> Seq.choose (function :? Border as border -> Some border | _ -> None)
        |> Seq.tryFind (fun border -> AutomationProperties.GetName(border) = "已保存")
    Assert.True(toast.IsSome)
    Assert.False(toast.Value.Focusable)
    // Escape 触发退场同步移除（仅入场淡入保留：无显示制度无法确定性泵动退场计时器）。
    Assert.True(overlay.HandleEscape())
    // 同步移除：节点即时从树中消失，无需等待任何计时器。
    let stillPresent =
        descendants root
        |> Seq.exists (fun control -> AutomationProperties.GetName(control) = "已保存")
    Assert.False(stillPresent)

// 减弱动效下 toast 退场即时移除（无淡出、零等待），保持可预期的关闭手感。
[<Fact>]
let toast_removes_immediately_under_reduced_motion () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    MotionPolicy.setReduced true
    try
        overlay.Toast("即时", ToastTone.Warning)
        Assert.True(overlay.HandleEscape())
        let present =
            descendants root
            |> Seq.exists (fun control -> AutomationProperties.GetName(control) = "即时")
        Assert.False(present)
    finally
        MotionPolicy.setReduced false
