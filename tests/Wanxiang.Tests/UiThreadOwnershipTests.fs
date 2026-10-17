namespace Wanxiang.Tests

open System.Threading
open Xunit

/// 决策 420–422 回归：UI 静态状态的线程归属不受测试顺序影响。
/// 曾经：一个纯逻辑断言在 xunit 工作线程上首次碰 `Tokens`，整个 UI 分组 117 个用例连带失败。
[<Trait("Category", "UI")>]
type UiThreadOwnershipTests() =

    [<Fact>]
    member _.``tokens brushes are owned by the headless ui thread`` () =
        let mutable owned = false
        Headless.run (fun () -> owned <- Headless.tokensOwnedByUiThread ())
        Assert.True(owned, "Tokens 画笔必须归 headless UI 线程所有")

    [<Fact>]
    member _.``touching tokens off the ui thread first cannot poison later ui tests`` () =
        let t = Thread(fun () -> Wanxiang.UI.SharedTheme.manager.Brush(Wanxiang2.ThemePalette.PaletteKey.Canvas) |> ignore)
        t.Start()
        t.Join()
        Headless.run (fun () -> Wanxiang.UI.SharedTheme.manager.Apply(Wanxiang.UI.SharedTheme.manager.Mode))
        Headless.run (fun () -> Assert.True(Headless.tokensOwnedByUiThread ()))
