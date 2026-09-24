module Wanxiang.Tests.DismissFocusTests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Input
open Avalonia.Threading
open Xunit
open Wanxiang.UI
open Wanxiang.Tests

/// 本轮借鉴 kelivo 的收敛性改动，全部落在「一次手势只该得到一次反应」
/// 与「首焦落在最需要补齐的那一项」上：
///   - 层外滚轮取消浮层时必须消费事件，否则同一次滚动脉冲穿透到底层继续滚；
///   - 连接对话框首焦落在第一个还缺内容的字段（地址已记住时聚焦地址只是多一次 Tab）。
///
/// 另一条候选（导出对话框补 Enter 确认）被证伪后删除：Ui.button 的 Ui.onClick
/// 早已把 Enter/Space 绑在按钮自身，焦点不在按钮上时又没有别的可聚焦元素，
/// 再加一层只会是死代码。测试也不该为它存在。
/// 不触发动效、不碰网络、不复述实现。
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

let private byName (root: Control) (name: string) =
    descendants root
    |> Seq.tryFind (fun control -> AutomationProperties.GetName(control) = name)

/// 把内容装进一个真实窗口并跑一次布局，令浮层 / 定位 / 焦点的 UI 线程作业落地。
let private show (content: Control) width height =
    Headless.ensure ()
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

// ── 层外滚轮取消浮层，且不穿透 ─────────────────────────────────────────────

// 层外滚轮在一次脉冲里只会命中最靠近指针的那个控件，而 root 是承载全部层级的宿主
// Grid（消息区 / 侧栏都在它子树里）。以 root 为目标构造一次滚轮事件，即可判定处理器
// 有没有把事件消费掉：未消费时它会继续冒泡到 root 里的滚动区，表现为「先收起菜单、
// 正文又猛地一滚」。
let private wheelAt (target: Control) (root: Grid) =
    let pointer = new Pointer(1, PointerType.Mouse, true)
    let properties = PointerPointProperties()
    Dispatcher.UIThread.RunJobs()
    // 目标中心换算到 root 坐标：处理器读的就是相对 root 的点。目标自身（浮层内的
    // 菜单项）换算后天然落在 popupCard 内；root 自身换算后天然落在浮层外。
    let b = target.Bounds
    let translated = target.TranslatePoint(Point(b.Width / 2.0, b.Height / 2.0), root)
    let center =
        if translated.HasValue then translated.Value
        else Point(b.X + b.Width / 2.0, b.Y + b.Height / 2.0)
    let e =
        PointerWheelEventArgs(
            target,
            pointer,
            root,
            center,
            0UL,
            properties,
            KeyModifiers.None,
            Vector(0.0, -120.0))
    e.RoutedEvent <- InputElement.PointerWheelChangedEvent
    e.Source <- target
    root.RaiseEvent(e)
    e

[<Fact>]
let ``wheel outside an open popup dismisses it without scrolling the content`` () =
    let root = Grid()
    root.Children.Add(Border(Width = 400.0, Height = 300.0))
    let overlay = OverlayHost(root)
    overlay.WireDismiss()
    let anchor =
        Border(
            Width = 120.0,
            Height = 40.0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right)
    root.Children.Add anchor
    let entry = MenuEntry.create "重命名" ignore
    let window = show root 400.0 300.0
    try
        Menu.show overlay anchor false [ entry ]
        Dispatcher.UIThread.RunJobs()
        Assert.True(overlay.IsPopupOpen, "前置条件：浮层已打开")
        let e = wheelAt root root
        Assert.False(overlay.IsPopupOpen, "层外滚轮应取消浮层")
        // 关键：同一事件被消费。漏了这一行，取消浮层的同时底层滚动区也会收到同一次脉冲。
        Assert.True(e.Handled, "层外滚轮取消浮层后必须消费事件，避免穿透到底层滚动")
    finally
        window.Close()

[<Fact>]
let ``wheel inside an open popup is left to the popup itself`` () =
    let root = Grid()
    root.Children.Add(Border(Width = 400.0, Height = 300.0))
    let overlay = OverlayHost(root)
    overlay.WireDismiss()
    let anchor =
        Border(
            Width = 120.0,
            Height = 40.0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right)
    root.Children.Add anchor
    let entry = MenuEntry.create "重命名" ignore
    let window = show root 400.0 300.0
    try
        Menu.show overlay anchor false [ entry ]
        Dispatcher.UIThread.RunJobs()
        Assert.True(overlay.IsPopupOpen, "前置条件：浮层已打开")
        // 命中的是浮层卡自己：长菜单要能在浮层内滚动，处理器必须放行（不关闭、不消费）。
        let menuItem = byName root "重命名"
        let e = wheelAt menuItem.Value root
        Assert.True(overlay.IsPopupOpen, "命中浮层内的滚轮不应关闭浮层")
        Assert.False(e.Handled, "命中浮层内的滚轮应放行给浮层自身的滚动条")
    finally
        window.Close()

// ── 连接对话框：首焦落在第一个还缺内容的字段 ────────────────────────────────

[<Theory>]
[<InlineData("", true, false)>]
[<InlineData("ws://127.0.0.1:8765/ws", false, true)>]
let ``connect focuses the first field that still needs input`` defaultUrl expectUrl expectToken =
    Headless.ensure ()
    let root = Grid()
    root.Children.Add(Border(Width = 460.0, Height = 460.0))
    let overlay = OverlayHost(root)
    let window = show root 460.0 460.0
    try
        // connect 的最后一步是 Dispatcher.Post 里聚焦：需要两次 RunJobs 让它落地。
        Dialogs.connect overlay defaultUrl (fun _ -> ()) ignore |> ignore
        Dispatcher.UIThread.RunJobs()
        Dispatcher.UIThread.RunJobs()
        let manager = window.FocusManager
        let focused =
            if isNull manager then null
            else
                let element = manager.GetFocusedElement()
                if isNull element then null else element :?> Control
        Assert.True(not (isNull focused), "连接对话框打开后必须有焦点")
        // 可聚焦的是 TextBox 本身（label 是 TextBlock、不进 Tab 序），用占位符区分两个字段：
        // Ui.textField 的 placeholder 对 url 是「ws://127.0.0.1:8765/ws」，对 token 是
        // 「有令牌就粘贴，没有就用配对码」。断言焦点落在这两个 TextBox 之一上。
        let focusedBox =
            match focused with
            | :? TextBox as box -> Some box
            | _ -> None
        Assert.True(focusedBox.IsSome, "DIAG focused=" + (focused.GetType().FullName))
        printfn "FOCUSED: %s ph=[%s] focusable=%b effvis=%b enabled=%b" (focused.GetType().FullName) focusedBox.Value.PlaceholderText focusedBox.Value.Focusable focusedBox.Value.IsEffectivelyVisible focusedBox.Value.IsEnabled
        printfn "ALL FOCUSABLES: %s" (String.Join(", ", (descendants root |> Seq.choose (function :? TextBox as b -> Some (b.PlaceholderText) | _ -> None))))
        let placeholder = focusedBox.Value.PlaceholderText
        let isUrlField = placeholder = "ws://127.0.0.1:8765/ws"
        let isTokenField = placeholder = "有令牌就粘贴，没有就用配对码"
        // 直接按期望分支断言。
        if expectUrl then
            Assert.True(isUrlField, sprintf "地址为空时首焦应落在地址字段（defaultUrl=%s）" defaultUrl)
            Assert.False(isTokenField, "地址为空时不该跳过地址直接聚焦令牌")
        else
            Assert.True(isTokenField, sprintf "地址已记住时首焦应落在令牌字段（defaultUrl=%s）" defaultUrl)
            Assert.False(isUrlField, "地址已记住时再聚焦地址只是让用户多按一次 Tab")
    finally
        overlay.CloseDialog()
        window.Close()
