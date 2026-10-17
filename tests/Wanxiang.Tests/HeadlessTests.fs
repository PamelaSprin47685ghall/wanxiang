namespace Wanxiang.Tests

open Avalonia.Controls
open Avalonia.Threading
open Xunit

/// `Headless.run` 自身的行为契约。
///
/// 这是整套 UI 测试的**地基**：如果它不在 UI 线程上执行，所有 UI 测试都不可信。
/// 因此这里把「已进入 Avalonia UI 线程」显式钉成断言，而不是假设。
[<Trait("Category", "UI")>]
module HeadlessTests =

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``headless run executes the body on the avalonia ui thread`` () =
        let mutable onUiThread = false
        Headless.run (fun () ->
            onUiThread <- Dispatcher.UIThread.CheckAccess())
        Assert.True(onUiThread, "Headless.run 必须把测试体编组到 Avalonia UI 线程上")

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``avalonia controls can be constructed inside headless run`` () =
        // 这正是手写 lazy 装置会失败的场景：非 UI 线程碰带 Transitions 的控件。
        let mutable constructed = false
        Headless.run (fun () ->
            let border = Border()
            let window = Window(Content = border)
            window.Show()
            // 能走到 Show 而不抛 VerifyAccess 就是 UI 线程的证据；
            // Transitions 只有显式设置过才非 null，不能拿它当构造成功标志。
            constructed <- true)
        Assert.True(constructed)

    /// 决策 421 回归：程序集夹具必须真的在 UI 线程上触发 `Tokens` 的静态初始化。
    /// 旧的 `Tokens.canvas |> ignore` 被优化器整句消掉，夹具形同虚设，谁先碰 `Tokens`
    /// 谁就拥有整套画笔——工作线程先碰（本测试第一行）即毒化后续所有 UI 测试。
    /// 单独运行本测试即可确定性复现。
    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``Tokens are owned by the ui thread even when a worker thread touches them first`` () =
        // 工作线程上的纯逻辑读取（xunit 线程，不在 Headless.run 内）
        Assert.False(obj.ReferenceEquals(Wanxiang.UI.SharedTheme.manager.Brush(Wanxiang2.ThemePalette.PaletteKey.Canvas), null))
        let mutable owned = false
        Headless.run (fun () -> owned <- Wanxiang.UI.SharedTheme.manager.Brush(Wanxiang2.ThemePalette.PaletteKey.Canvas).CheckAccess())
        Assert.True(owned, "主题画笔必须归 headless UI 线程所有（决策 421）")
