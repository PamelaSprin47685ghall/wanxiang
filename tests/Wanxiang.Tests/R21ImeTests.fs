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

    [<Fact>]
    let ``384 判定：组合中吞掉回车方向键Esc Tab，ImeProcessed 任何时候都吞，普通字符放行`` () =
        for k in [ Key.Enter; Key.Up; Key.Down; Key.Escape; Key.Tab ] do
            Assert.True(ImeGuard.shouldSwallow true k)
            Assert.False(ImeGuard.shouldSwallow false k)
        Assert.True(ImeGuard.shouldSwallow false Key.ImeProcessed)
        Assert.False(ImeGuard.shouldSwallow true Key.A)
        Assert.False(ImeGuard.shouldSwallow true Key.Back)

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
    let ``385 任意 TextBox 的回车处理器在组合中不触发，结束后照常触发；预编辑可见且提交不重复`` () =
        Headless.run (fun () ->
            let tb = TextBox()
            let mutable submits = 0
            tb.KeyDown.Add(fun e -> if e.Key = Key.Enter then submits <- submits + 1; e.Handled <- true)
            let w = show tb
            tb.Focus() |> ignore
            let p = presenter tb
            p.PreeditText <- "ni"
            Assert.True(ImeGuard.isComposing tb)
            press tb Key.Enter
            press tb Key.Escape
            Assert.Equal(0, submits)
            Assert.Equal("ni", p.PreeditText)
            // 输入法提交：预编辑清空，正文只出现一次提交字。
            p.PreeditText <- null
            tb.RaiseEvent(TextInputEventArgs(RoutedEvent = InputElement.TextInputEvent, Text = "你", Source = tb))
            Assert.Equal("你", tb.Text)
            press tb Key.Enter
            Assert.Equal(1, submits)
            w.Close())

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
