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
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

// toast 默认停留时长按语气取 MotionLedger：毫秒唯一来源为 toastDwell{Failure/Warning/Default}。
// 这里钉住令牌数值，既防止有人改回内联字面量，也防止 token 漂移。
[<Trait("Category", "UI")>]
[<Fact>]
let toast_dwell_timings_are_owned_by_MotionLedger () =
    Headless.run (fun () ->
    Assert.Equal(9000.0, MotionLedger.toastDwellFailure.TotalMilliseconds, 3)
    Assert.Equal(6500.0, MotionLedger.toastDwellWarning.TotalMilliseconds, 3)
    Assert.Equal(4000.0, MotionLedger.toastDwellDefault.TotalMilliseconds, 3)
    Assert.Equal(300.0, MotionLedger.toastFade.TotalMilliseconds, 3)
    )

// 下拉钮垂直内边距引 ControlMetrics.selectButtonPaddingY（单一来源），水平仍是 space3。
// 数值恰为 6.0、与旧内联一致，故此测试锁的是「取值随 token 走」这一权威来源。
[<Trait("Category", "UI")>]
[<Fact>]
let select_button_vertical_padding_is_anchored_to_ControlMetrics () =
    Headless.run (fun () ->
    let host, setText = Menu.selectButton (OverlayHost(Grid())) "模型" (fun () -> [])
    Assert.Equal(ControlMetrics.selectButtonPaddingY, host.Padding.Top, 3)
    Assert.Equal(ControlMetrics.selectButtonPaddingY, host.Padding.Bottom, 3)
    Assert.Equal(Spacing.spaceXl, host.Padding.Left, 3)
    Assert.Equal(Spacing.spaceXl, host.Padding.Right, 3)
    setText "已切换"
    )

// 下拉钮必须有可播报的自动化名：ComboBox 语义没有名字时读屏用户 Tab 过去只听到
// 空控件名，分不清这是什么设置。名字随显示文本同步刷新——调用方（如模型 chip）
// 已命名过的按钮不被覆盖。
[<Trait("Category", "UI")>]
[<Fact>]
let select_button_carries_an_automation_name_that_tracks_the_text () =
    Headless.run (fun () ->
    let host, setText = Menu.selectButton (OverlayHost(Grid())) "跟随系统" (fun () -> [])
    Assert.Equal("跟随系统", AutomationProperties.GetName host)
    setText "浅色"
    Assert.Equal("浅色", AutomationProperties.GetName host)
    // 调用方显式命名（模型卡片「切换本对话使用的模型」等）优先，不被下拉文本覆盖。
    let named, _ = Menu.selectButton (OverlayHost(Grid())) "gpt-4o" (fun () -> [])
    AutomationProperties.SetName(named, "切换模型")
    setText "无所谓" |> ignore
    Assert.Equal("切换模型", AutomationProperties.GetName named)
    )

// 按压反馈只写不透明度、不改几何契约：非按压态整层不透明度满值，MinHeight 仍由 ControlMetrics 供给。
// 注：本无头套件不注入原始指针事件，「按下→0.78 / 松开→1.0」的迁移不在此自动化覆盖，交真机/DevOps 判定。
[<Trait("Category", "UI")>]
[<Fact>]
let select_button_and_menu_items_keep_full_opacity_until_pressed () =
    Headless.run (fun () ->
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
    )

// 删除 focus 死代码后行为不变：提示条仍 Focusable=false、Escape 仍按 topmost-first 解散最新一条。
// 减弱动效下 toast 退场即时移除（无淡出、零等待），保持可预期的关闭手感。