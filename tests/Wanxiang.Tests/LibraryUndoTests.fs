module Wanxiang.Tests.LibraryUndoTests

// §214 删除即撤销 / §215 错误卡就地恢复动作。

open System
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.UI
open Wanxiang.Tests

[<Fact>]
let ``§215 每类不可自愈的错误都有恢复动作`` () =
    Assert.Equal(Some OpenProviderSettings, ErrorRecovery.forKind ProviderAuthFailed)
    Assert.Equal(Some OpenProviderSettings, ErrorRecovery.forKind ModelNotFound)
    Assert.Equal(Some OpenConversationSettings, ErrorRecovery.forKind ConfigInvalid)
    Assert.Equal(Some SwitchModel, ErrorRecovery.forKind ProviderRateLimited)
    Assert.Equal("换个模型", ErrorRecovery.label SwitchModel)
    Assert.Equal(Some StartNewConversation, ErrorRecovery.forKind ContextTooLong)
    Assert.Equal(None, ErrorRecovery.forKind ProviderTimeout)
    Assert.Contains("换个模型", GenerationErrorKind.hint ProviderRateLimited)

let private findNamed (root: Control) (name: string) =
    let rec walk (c: Control) : seq<Control> =
        seq {
            yield c
            match c with
            | :? Panel as p -> for ch in p.Children do yield! walk ch
            | :? ContentControl as cc when (cc.Content :? Control) -> yield! walk (cc.Content :?> Control)
            | :? Decorator as d when not (isNull d.Child) -> yield! walk d.Child
            | _ -> ()
        }
    walk root
    |> Seq.tryPick (function
        | :? Control as c when AutomationProperties.GetName c = name -> Some c
        | _ -> None)

[<Trait("Category", "UI")>]
[<Fact>]
let ``§215 密钥失效的错误卡给出「打开模型服务设置」并路由到 sink`` () =
    Headless.run (fun () ->
        let mutable got = None
        MessageCard.setRecoverySink (fun r -> got <- Some r)
        let err = GenerationError.create ProviderAuthFailed "密钥无效"
        let card = MessageCard.errorCard err ignore None
        let btn = findNamed card "打开模型服务设置"
        Assert.True(btn.IsSome, "不可重试的密钥错误应有就地恢复按钮")
        btn.Value.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = btn.Value))
        Assert.Equal(Some OpenProviderSettings, got))

[<Trait("Category", "UI")>]
[<Fact>]
let ``§214 带「撤销」的提示条：点动作即执行并关条`` () =
    Headless.run (fun () ->
        let root = Grid()
        let overlay = OverlayHost(root)
        let mutable undone = false
        overlay.ToastWith("已删除「甲」", Info, Some("撤销", fun () -> undone <- true))
        let window = Window(Content = root, Width = 600.0, Height = 400.0)
        window.Show()
        try
            let btn = findNamed root "撤销"
            Assert.True(btn.IsSome)
            btn.Value.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = btn.Value))
            Assert.True undone
            Assert.True((findNamed root "撤销").IsNone, "执行动作后提示条应关闭")
        finally
            window.Close())

/// 视觉走查 r6：PWA 里点「撤销」只关掉了提示条，对话没回来——指针点击落到了
/// 提示条自身的「点击关闭」上。这里用真实的指针按下/抬起而不是键盘激活来锁。
[<Trait("Category", "UI")>]
[<Fact>]
let ``§214 指针点「撤销」执行动作而不只是关条`` () =
    Headless.run (fun () ->
        let root = Grid()
        let overlay = OverlayHost(root)
        let mutable undone = false
        overlay.ToastWith("已删除「甲」", Info, Some("撤销", fun () -> undone <- true))
        let window = Window(Content = root, Width = 600.0, Height = 400.0)
        window.Show()
        try
            Avalonia.Threading.Dispatcher.UIThread.RunJobs()
            let btn = (findNamed root "撤销").Value
            let center = Avalonia.VisualExtensions.TranslatePoint(btn, Avalonia.Point(btn.Bounds.Width / 2.0, btn.Bounds.Height / 2.0), window).Value
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, center, MouseButton.Left, RawInputModifiers.None)
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, center, MouseButton.Left, RawInputModifiers.None)
            Avalonia.Threading.Dispatcher.UIThread.RunJobs()
            Assert.True(undone, "指针点「撤销」必须执行撤销")
        finally
            window.Close())
