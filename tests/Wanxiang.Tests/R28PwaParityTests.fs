module Wanxiang.Tests.R28PwaParityTests

open System
open System.IO
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Xunit
open Wanxiang.UI

// 决策 450–454：桌面 / PWA 生命周期一致性。浏览器侧由 tools/parity_pwa.js 在 CI 冒烟里断言；
// 这里守住桌面侧与共享逻辑。

[<Fact>]
let ``probe counts only when enabled`` () =
    let before = ParityProbe.count "test.r28.off"
    let saved = ParityProbe.forceInProcess
    try
        ParityProbe.forceInProcess <- false
        ParityProbe.hit "test.r28.off"
        Assert.Equal(before, ParityProbe.count "test.r28.off")
        ParityProbe.forceInProcess <- true
        ParityProbe.hit "test.r28.on"
        ParityProbe.hit "test.r28.on"
        Assert.True(ParityProbe.count "test.r28.on" >= 2)
    finally
        ParityProbe.forceInProcess <- saved

[<Fact>]
let ``local text roundtrips through a file on desktop`` () =
    let dir = Path.Combine(Path.GetTempPath(), "wx-r28-" + Guid.NewGuid().ToString "N")
    let path = Path.Combine(dir, "ui.json")
    try
        Assert.Equal(None, LocalText.read path)
        Assert.True(LocalText.write path "{\"theme\":\"dark\"}")
        Assert.Equal(Some "{\"theme\":\"dark\"}", LocalText.read path)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Trait("Category", "UI")>]
[<Fact>]
let ``shortcuts reach the shell when focus is outside it like the PWA first screen`` () =
    Headless.run (fun () ->
    MotionPolicy.setReduced true
    let saved = ParityProbe.forceInProcess
    ParityProbe.forceInProcess <- true
    try
        let view = MainView()
        let window = Window(Width = 900.0, Height = 600.0, Content = view)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        view.Build()
        Dispatcher.UIThread.RunJobs()
        let before = ParityProbe.count "shortcut.resolved"
        window.RaiseEvent(
            KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1, KeyModifiers = KeyModifiers.None))
        Dispatcher.UIThread.RunJobs()
        Assert.True(ParityProbe.count "shortcut.resolved" > before)
        window.Close()
    finally
        ParityProbe.forceInProcess <- saved)
