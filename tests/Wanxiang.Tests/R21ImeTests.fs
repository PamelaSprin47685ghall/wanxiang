namespace Wanxiang.Tests

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Presenters
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.UI

/// 决策 384–386：中文输入法组合期间回车/方向键/Esc/Tab 归输入法（全局 ImeGuard）。
module R21ImeTests =

    let private presenter (tb: TextBox) =
        tb.GetVisualDescendants() |> Seq.pick (function :? TextPresenter as p -> Some p | _ -> None)

    let private press (tb: TextBox) k =
        tb.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = KeyModifiers.None, Source = tb))

    let private show (c: Control) =
        let w = Window(Width = 800.0, Height = 600.0, Content = c)
        w.Show()
        w.GetLayoutManager().ExecuteLayoutPass()
        Dispatcher.UIThread.RunJobs()
        w

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``386 命令面板：组合中回车不执行、方向键不移动`` () =
        Headless.run (fun () ->
            let mutable ran = 0
            let sample : PaletteItem list = []
            let content, search = CommandPaletteView.build (fun () -> sample) (fun () -> []) (fun _ _ -> ran <- ran + 1)
            let w = show content
            search.Text <- "对话"
            Dispatcher.UIThread.RunJobs()
            search.Focus() |> ignore
            (presenter search).PreeditText <- "dui"
            press search Key.Down
            press search Key.Enter
            Assert.Equal(0, ran)
            (presenter search).PreeditText <- null
            press search Key.Enter
            Assert.Equal(1, ran)
            w.Close())

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``387 输入框：组合中回车不发送`` () =
        Headless.run (fun () ->
            let mutable sent = 0
            let composer =
                Composer(
                    { submit = (fun _ -> sent <- sent + 1; true); stopGeneration = ignore; pickAttachment = ignore
                      removeAttachment = ignore; openModelPicker = ignore; dropFiles = ignore
                      pasteFromClipboard = (fun () -> false) })
            composer.Build()
            composer.SetEnabled(true, "")
            let w = show composer
            let input = composer.GetVisualDescendants() |> Seq.pick (function :? TextBox as t -> Some t | _ -> None)
            input.Text <- "你好"
            input.Focus() |> ignore
            (presenter input).PreeditText <- "ma"
            press input Key.Enter
            Assert.Equal(0, sent)
            Assert.Equal("你好", input.Text)
            (presenter input).PreeditText <- null
            press input Key.Enter
            Assert.Equal(1, sent)
            w.Close())
