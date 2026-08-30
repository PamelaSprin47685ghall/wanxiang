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
