namespace Wanxiang.Tests

open System
open System.Reflection
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Xunit
open Wanxiang.UI

/// Ctrl+W 关窗：桌面应用最基础的键盘出口。
///
/// 此前关窗只有指针一条路：右上角叉号。键盘用户按 Ctrl+W 什么都不会发生，
/// 而这是所有桌面应用（含 kelivo：hotkey_provider 的 close_window）的通用基约。
/// 这里锁三条可观察契约：
/// - Ctrl+W 解析成关窗意图，且不与既有快捷键抢键（Ctrl+Shift+W、裸 W 都不占）；
/// - 壳层真的把宿主窗口合上（走 Window.Close，MainWindow.Closing 的几何持久化才跑得到）；
/// - 键被标记已处理，宿主浏览器不会再拿 Ctrl+W 去解释成自己的关标签语义；
/// - PWA 没有 Window（决策 48：根视图是 Control）：宿主缺失时安全降级，不抛。
module CloseWindowTests =

    let private flags = BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public

    let private keyEvent key modifiers = KeyEventArgs(Key = key, KeyModifiers = modifiers)

    /// 建一个真实壳：MainView.HandleShortcut 是壳内唯一把快捷键翻译成动作的地方，
    /// 壳本身又通过 TopLevel.GetTopLevel 反查宿主窗口。
    let private buildShell () =
        Headless.ensure ()
        MotionPolicy.setReduced true
        let shell = MainView()
        shell.Build()
        let window = Window(Width = 1000.0, Height = 800.0, Content = shell)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        shell, window

    let private handle (shell: MainView) (e: KeyEventArgs) =
        typeof<MainView>.GetMethod("HandleShortcut", flags).Invoke(shell, [| box e |]) |> ignore

    // ---------- 快捷键解析 ----------

    [<Fact>]
    let ``ctrl w resolves to close window`` () =
        Assert.Equal(CloseWindow, ShortcutRouter.resolve (keyEvent Key.W KeyModifiers.Control))

    [<Fact>]
    let ``close window accepts either ctrl or meta`` () =
        // ⌘W 在 macOS 上与 Ctrl+W 同权：与其它 Ctrl/* 快捷键同一判据。
        Assert.Equal(CloseWindow, ShortcutRouter.resolve (keyEvent Key.W KeyModifiers.Meta))

    [<Fact>]
    let ``close window does not steal neighbouring key bindings`` () =
        // Ctrl+Shift+W 留给浏览器/未来的标签语义；裸 W 是输入框正文，绝不能占。
        Assert.Equal(NoShortcut, ShortcutRouter.resolve (keyEvent Key.W KeyModifiers.None))
        Assert.Equal(
            NoShortcut,
            ShortcutRouter.resolve (keyEvent Key.W (KeyModifiers.Control ||| KeyModifiers.Shift)))
        // 同一批键位不许把既有动作挤掉。
        Assert.Equal(NewConversation, ShortcutRouter.resolve (keyEvent Key.N KeyModifiers.Control))
        Assert.Equal(ToggleSidebar, ShortcutRouter.resolve (keyEvent Key.B KeyModifiers.Control))
        Assert.Equal(ScrollToEnd, ShortcutRouter.resolve (keyEvent Key.End KeyModifiers.Control))
        Assert.Equal(ScrollToBeginning, ShortcutRouter.resolve (keyEvent Key.Home KeyModifiers.Control))

    // ---------- 壳层行为 ----------

    [<Fact>]
    let ``ctrl w closes the hosting window`` () =
        let shell, window = buildShell ()
        // Avalonia 的 Close 是终态的（"Cannot re-show a closed window"），
        // 所以这里不用 try/finally 回收窗口——每个用例自建一个，无共享状态。
        Assert.True(window.IsActive, "前置条件：窗口已显示，TopLevel 可解析")
        handle shell (keyEvent Key.W KeyModifiers.Control)
        Assert.False(window.IsActive, "Ctrl+W 应经 Window.Close 关掉宿主窗口")

    [<Fact>]
    let ``ctrl w is marked handled so the host cannot act on it twice`` () =
        let shell, window = buildShell ()
        let e = keyEvent Key.W KeyModifiers.Control
        handle shell e
        // 不置 Handled 的话，除了 Avalonia 还会有人（浏览器）把 Ctrl+W
        // 当自己的关标签/关窗口语义再解释一次。
        Assert.True(e.Handled, "Ctrl+W 必须被标记已处理，阻止宿主重复执行")
        Assert.False(window.IsActive, "同一路径也把窗口关掉了")

    [<Fact>]
    let ``ctrl w without a window host degrades to a no op`` () =
        // PWA（决策 48：browser 宿主下根视图是 Control，没有 Window）：
        // 关窗快捷键在那里必须安静地什么都不做，而不是抛。
        Headless.ensure ()
        MotionPolicy.setReduced true
        let shell = MainView()
        shell.Build()
        let e = keyEvent Key.W KeyModifiers.Control
        let raised = ResizeArray<exn> ()
        try
            handle shell e
        with ex ->
            raised.Add ex
        Assert.Empty(raised)
        // 没有宿主可关：不声称已处理，把按键交回给宿主。
        Assert.False(e.Handled)
