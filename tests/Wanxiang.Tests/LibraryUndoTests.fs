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
let ``§214 待删批次：隐去、撤销恢复、撤销后不再提交`` () =
    let a, b, c = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    let s1, batch = PendingDeletion.schedule [ a, "甲"; b, "乙" ] PendingDeletion.empty
    let batch = batch.Value
    Assert.Equal("已删除 2 个对话", batch.label)
    Assert.True(PendingDeletion.isPending a s1)
    Assert.False(PendingDeletion.isPending c s1)
    let s2, restored = PendingDeletion.undo batch.token s1
    Assert.Equal<Guid list>([ a; b ], restored)
    Assert.False(PendingDeletion.isPending a s2)
    // 已撤销的批次到期提交：什么都不删
    let _, committed = PendingDeletion.commit batch.token s2
    Assert.Empty committed

[<Fact>]
let ``§214 到期提交只交出一次；重复登记不重复删`` () =
    let a = Guid.NewGuid()
    let s1, batch = PendingDeletion.schedule [ a, "甲" ] PendingDeletion.empty
    Assert.Equal("已删除「甲」", batch.Value.label)
    let again, dup = PendingDeletion.schedule [ a, "甲" ] s1
    Assert.True dup.IsNone
    let s2, ids = PendingDeletion.commit batch.Value.token again
    Assert.Equal<Guid list>([ a ], ids)
    let _, twice = PendingDeletion.commit batch.Value.token s2
    Assert.Empty twice

[<Fact>]
let ``§215 每类不可自愈的错误都有恢复动作`` () =
    Assert.Equal(Some OpenProviderSettings, ErrorRecovery.forKind ProviderAuthFailed)
    Assert.Equal(Some OpenProviderSettings, ErrorRecovery.forKind ModelNotFound)
    Assert.Equal(Some OpenConversationSettings, ErrorRecovery.forKind ConfigInvalid)
    Assert.Equal(Some OpenConversationSettings, ErrorRecovery.forKind ProviderRateLimited)
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
