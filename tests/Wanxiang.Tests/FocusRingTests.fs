module Wanxiang.Tests.FocusRingTests

open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Threading
open Xunit
open Wanxiang.UI
open Wanxiang.Tests

/// 键盘焦点必须看得见，否则 Tab 过去用户不知道焦点在哪。
/// 同时鼠标点完不该留一圈框——那是视觉噪音。
let private withButton (act: Border -> unit) =
    Headless.ensure ()
    let button = Ui.iconButton Icons.copy "复制"
    let panel = StackPanel(Orientation = Orientation.Vertical)
    panel.Children.Add button
    let window = Window(Width = 200.0, Height = 120.0, Content = panel)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    try
        act button
    finally
        window.Close()

[<Fact>]
let ``键盘导航过来的按钮带焦点环`` () =
    withButton (fun button ->
        Assert.Equal(0, button.BoxShadow.Count)
        button.Focus NavigationMethod.Tab |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True(button.BoxShadow.Count > 0, "Tab 过去应当出现焦点环"))

[<Fact>]
let ``方向键导航同样带焦点环`` () =
    withButton (fun button ->
        button.Focus NavigationMethod.Directional |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True(button.BoxShadow.Count > 0, "方向键导航也该出现焦点环"))

[<Fact>]
let ``鼠标点出来的焦点不画环`` () =
    withButton (fun button ->
        button.Focus NavigationMethod.Pointer |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(0, button.BoxShadow.Count))

[<Fact>]
let ``程序化 / 初始焦点同样画焦点环`` () =
    // 对话框打开时的初始焦点一律走裸 Focus()——拿到的是 Unspecified：
    // OverlayHost.focusFirst（404 行 dialogCard 首可聚焦项）与
    // Dialogs.shortcuts 的首按钮都是这条路径。此前焦点环只认 Tab/Directional，
    // 结果焦点明明在按钮上（Enter 能激活）却没有环，键盘用户看不见焦点在哪。
    // 与上一条互为对偶：Pointer 不画（点击已有按压反馈），Unspecified 画。
    withButton (fun button ->
        button.Focus NavigationMethod.Unspecified |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True(button.BoxShadow.Count > 0, "程序化焦点也要有焦点环"))

[<Fact>]
let ``对话框初始焦点（裸 Focus）带焦点环`` () =
    // 走真实对话框路径：OverlayHost.ShowDialog → focusFirst post 裸 Focus()。
    // 断言落点即 ShowDialog 默认首焦（内容树第一个可聚焦项）。
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    let box = TextBox()
    let close = Ui.button Ui.Secondary "关闭" (fun () -> ())
    let panel = StackPanel(Orientation = Orientation.Vertical)
    panel.Children.Add box
    panel.Children.Add close
    overlay.ShowDialog(panel, 360.0)
    let window = Window(Width = 420.0, Height = 340.0, Content = root)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()
        Dispatcher.UIThread.RunJobs()
        let focused = window.FocusManager.GetFocusedElement()
        Assert.True(obj.ReferenceEquals(focused, box), "前置条件：ShowDialog 初始焦点落在内容树首项")
        Assert.True(box.IsFocused, "前置条件：确由 dialogCard 拿到焦点")
        // 关键断言：把焦点再交给 close 按钮（裸 Focus() = Unspecified）后必须有环。
        close.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True(obj.ReferenceEquals(window.FocusManager.GetFocusedElement(), close), "前置条件：close 拿到焦点")
        Assert.True(close.BoxShadow.Count > 0, "对话框首按钮（Unspecified 焦点）必须带焦点环")
    finally
        window.Close()

[<Fact>]
let ``失去焦点后焦点环消失`` () =
    withButton (fun button ->
        button.Focus NavigationMethod.Tab |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True(button.BoxShadow.Count > 0)
        // 把焦点交给别人
        let other = Ui.iconButton Icons.trash "删除"
        (button.Parent :?> StackPanel).Children.Add other
        Dispatcher.UIThread.RunJobs()
        other.Focus NavigationMethod.Tab |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(0, button.BoxShadow.Count))

/// compact 抽屉现场：背景放一个可聚焦的“背景输入区”（模拟被遮罩压住的 chat/composer），
/// 侧栏即抽屉本体。返回句柄供各用例驱动 Tab。
let private buildCompactDrawer () =
    let root = Grid()
    let overlay = OverlayHost(root)
    let background = TextBox(PlaceholderText = "背景输入区")
    let actions: SidebarActions =
        { newConversation = fun () -> ()
          openConversation = fun _ -> ()
          renameConversation = fun _ -> ()
          deleteConversation = fun _ -> ()
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          selectionAllPinned = fun _ -> false
          setPinnedMany = fun _ _ -> ()
          setArchivedMany = fun _ _ -> ()
          deleteMany = fun _ -> ()
          duplicateAsFork = fun _ -> ()
          exportConversation = fun _ -> ()
          openSettings = fun () -> ()
          reconnect = fun () -> ()
          toggleArchivedVisibility = fun () -> ()
          closeNavigation = fun () -> () }
    let sidebar = Sidebar(overlay, actions, fun _ -> Border() :> Control)
    sidebar.Build()
    root.Children.Add background
    root.Children.Add sidebar
    let window = Window(Width = 360.0, Height = 600.0, Content = root)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    sidebar, overlay, background, window

/// 抽屉内常驻的搜索框：作为 Tab 事件源，它自身不处理 Tab，必冒泡到侧栏的包含逻辑。
let private drawerSearchBox (ring: Control[]) =
    ring
    |> Array.find (fun c -> match c with :? TextBox as tb -> tb.PlaceholderText = "搜索会话" | _ -> false)

[<Fact>]
let ``compact 抽屉 Tab 只在侧栏内循环，两端回绕、不漏到背景`` () =
    Headless.ensure ()
    let sidebar, overlay, background, window = buildCompactDrawer ()
    try
        sidebar.SetCompactMode true
        Dispatcher.UIThread.RunJobs()

        // 抽屉的可聚焦集合沿用对话框 trapTab 的同一谓词 OverlayHost.Focusables。
        let ring = overlay.Focusables(sidebar)
        Assert.True(ring.Length >= 2, "抽屉内至少要有两个可聚焦项才谈得上循环")
        Assert.False(ring |> Array.contains background, "背景输入区不属于抽屉的可聚焦集合")

        let searchBox = drawerSearchBox ring
        let focused () = window.FocusManager.GetFocusedElement()
        let pressTab (mods: KeyModifiers) =
            let args = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = mods)
            searchBox.RaiseEvent args
            Dispatcher.UIThread.RunJobs()
            args
        let focusIs (target: Control) = obj.ReferenceEquals(focused (), target)
        let withinDrawer () = ring |> Array.exists (fun c -> obj.ReferenceEquals(c, focused ()))

        let last = ring.[ring.Length - 1]

        // 前进到末端再 Tab：回绕到首项；Tab 被包含逻辑消费，焦点不落到背景。
        last.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        let forwardWrap = pressTab KeyModifiers.None
        Assert.True(forwardWrap.Handled, "抽屉内 Tab 应由包含逻辑接管")
        Assert.True(focusIs ring.[0], "末端 Tab 应回绕到抽屉首项")
        Assert.False(background.IsFocused, "背景输入区不得获得焦点")

        // 首项按 Shift+Tab：反向回绕到末项。
        ring.[0].Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        let backwardWrap = pressTab KeyModifiers.Shift
        Assert.True(backwardWrap.Handled)
        Assert.True(focusIs last, "首项 Shift+Tab 应回绕到抽屉末项")

        // 完整走一圈：每步焦点都在抽屉内、背景自始至终不可聚焦；走满一圈回到首项——证明是循环而非单向泄漏。
        ring.[0].Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        for _ in 1 .. ring.Length do
            let step = pressTab KeyModifiers.None
            Assert.True(step.Handled)
            Assert.True(withinDrawer (), "Tab 焦点必须始终落在抽屉内")
            Assert.False(background.IsFocused, "抽屉打开时背景不可聚焦")
        Assert.True(focusIs ring.[0], "走满一圈应回到首项——循环闭环，两端都不外泄")

        // 包含逻辑只接管 Tab：其它键照旧透出，抽屉内部键盘行为（行 Up/Down/F2/Delete/Escape）不受影响。
        let nonTab = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.F5, KeyModifiers = KeyModifiers.None)
        searchBox.RaiseEvent nonTab
        Dispatcher.UIThread.RunJobs()
        Assert.False(nonTab.Handled, "包含逻辑只应接管 Tab，不应吞掉其它键")
    finally
        window.Close()

[<Fact>]
let ``非 compact 下侧栏不拦截 Tab，桌面态与主区互 Tab 如常`` () =
    Headless.ensure ()
    let sidebar, overlay, background, window = buildCompactDrawer ()
    try
        // 未进入 compact：侧栏是普通工作区面板，Tab 应在侧栏与主区间自由移动。
        // 判据取产品语义本身：焦点从侧栏搜索框出发连续 Tab 一圈多一点，
        // 必须越过侧栏边界落到背景输入区（与 compact 用例互为对偶：抽屉内永远到不了背景）。
        // 不再盯 args.Handled——那是窗口级焦点导航的框架信号，不等于侧栏消费 Tab。
        let ring = overlay.Focusables(sidebar)
        let searchBox = drawerSearchBox ring
        searchBox.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        let mutable reachedBackground = false
        for _ in 1 .. (ring.Length + 2) do
            let src = window.FocusManager.GetFocusedElement() :?> InputElement
            let args = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.None)
            src.RaiseEvent args
            Dispatcher.UIThread.RunJobs()
            if obj.ReferenceEquals(window.FocusManager.GetFocusedElement(), background) then reachedBackground <- true
        Assert.True(reachedBackground, "桌面态下 Tab 应越过侧栏落到背景输入区")
    finally
        window.Close()

/// 视口把抽屉自动带开的路径（AppShell.ApplyResponsiveLayout → NavigationController.ApplyViewport）
/// 也必须先聚焦进抽屉：进入 compact 且无会话时抽屉自动打开，焦点须落在抽屉搜索框，
/// 之后 Tab 全程在抽屉内循环，背景聊天/输入区键盘不可达——与用户手动打开（FocusSearch）同一归宿。
/// MainView.ApplyResponsiveLayout 是私有方法且依赖完整壳（client/composer/timers），无头测试不整体构造它；
/// 本用例把「真实状态迁移：NavigationController.ApplyViewport」与「焦点归宿契约：SetCompactMode 投影 + FocusSearch」
/// 在焦点环现场复现并对齐，并逐条钉住「聚焦进抽屉」的触发条件：仅当抽屉由「未开」翻到「compact 打开」。
[<Fact>]
let ``视口自动带开 compact 抽屉时焦点先落抽屉内，背景键盘不可达`` () =
    Headless.ensure ()
    let sidebar, overlay, background, window = buildCompactDrawer ()
    try
        let focused () = window.FocusManager.GetFocusedElement()
        let withinDrawer (ring: Control[]) = ring |> Array.exists (fun c -> obj.ReferenceEquals(c, focused ()))
        let pressTab (source: InputElement) =
            let args = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.None)
            source.RaiseEvent args
            Dispatcher.UIThread.RunJobs()
            args

        // 真实状态迁移：窄视口 + 无会话，ApplyViewport 自 compact 外把抽屉带开。
        let nav = NavigationController(false)
        let before = nav.State
        let needsApply, state = nav.ApplyViewport(360.0, false)
        Assert.True(needsApply, "进入 compact 应触发一次布局投影")
        Assert.True(state.compactMode, "窄视口下应为 compact")
        Assert.True(state.compactNavigationOpen, "无会话时视口应把抽屉自动带开")
        Assert.False(before.compactMode && before.compactNavigationOpen, "迁移前抽屉未开，构成「翻到打开」")

        // 壳层投影：MainLayout.Apply 把 compactMode 同步给侧栏，抽屉的 Tab 包含逻辑随 compactMode 启用。
        sidebar.SetCompactMode state.compactMode
        Dispatcher.UIThread.RunJobs()

        // 焦点归宿契约（即 AppShell 修复后新增的动作）：抽屉由「未开」翻到「compact 打开」才聚焦搜索框。
        let openedByViewport =
            state.compactMode && state.compactNavigationOpen
            && not (before.compactMode && before.compactNavigationOpen)
        Assert.True(openedByViewport, "该迁移应触发聚焦进抽屉")
        sidebar.FocusSearch()
        Dispatcher.UIThread.RunJobs()

        let ring = overlay.Focusables(sidebar)
        let searchBox = drawerSearchBox ring
        Assert.True(obj.ReferenceEquals(focused (), searchBox), "自动带开后焦点应落在抽屉搜索框")
        Assert.False(background.IsFocused, "焦点不应落在背景输入区")
        Assert.True(withinDrawer ring, "起点即在抽屉内")

        // 起点在抽屉内 → compact 抽屉的 Tab 包含逻辑生效：走满一圈焦点都在抽屉内，背景自始至终不可达。
        for _ in 1 .. ring.Length do
            let step = pressTab (focused () :?> InputElement)
            Assert.True(step.Handled, "抽屉内 Tab 应由包含逻辑接管")
            Assert.True(withinDrawer ring, "Tab 焦点必须始终落在抽屉内")
            Assert.False(background.IsFocused, "抽屉打开时背景键盘不可达")

        // 反向守卫：聚焦归宿只在抽屉真的翻到打开时成立。有会话时 compact 视口不自动开抽屉，
        // 非 compact 更不开——两种情形下触发条件为假，焦点不因缩放被抢。
        let withConversation = NavigationController(false)
        let convBefore = withConversation.State
        let _, convState = withConversation.ApplyViewport(360.0, true)
        Assert.False(convState.compactNavigationOpen, "有会话时 compact 视口不自动开抽屉")
        Assert.False(
            convState.compactMode && convState.compactNavigationOpen
            && not (convBefore.compactMode && convBefore.compactNavigationOpen),
            "抽屉未开时不得触发聚焦进抽屉")

        let wide = NavigationController(false)
        let wideBefore = wide.State
        let _, wideState = wide.ApplyViewport(1200.0, false)
        Assert.False(wideState.compactMode, "宽视口非 compact")
        Assert.False(
            wideState.compactMode && wideState.compactNavigationOpen
            && not (wideBefore.compactMode && wideBefore.compactNavigationOpen),
            "非 compact 不得触发聚焦进抽屉")
    finally
        window.Close()
