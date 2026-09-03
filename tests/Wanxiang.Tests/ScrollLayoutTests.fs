module Wanxiang.Tests.ScrollLayoutTests

open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Threading
open Xunit
open Wanxiang.Tests

/// 量一遍：把留白放在 Padding 上 / 放在内容 Margin 上，可滚动范围各是多少。
let private extentWith (padding: Thickness) (childMargin: Thickness) =
    Headless.ensure ()
    let child = Border(Height = 2000.0, Background = Brushes.Red, Margin = childMargin)
    let scroller = ScrollViewer(Content = child, Padding = padding)
    let window = Window(Width = 400.0, Height = 500.0, Content = scroller)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    scroller.ScrollToEnd()
    Dispatcher.UIThread.RunJobs()
    let extent = scroller.Extent.Height
    window.Close()
    extent

[<Fact>]
let ``ScrollViewer 的下内边距计入可滚动范围`` () =
    // Avalonia 12.0 时代曾不计入（导致末条留白必须加在内容 Margin 上）；
    // 升级至 Avalonia 12.1+ 后已修复，下内边距会计入可滚动范围。
    let bare = extentWith (Thickness 0.0) (Thickness 0.0)
    let padded = extentWith (Thickness(0.0, 0.0, 0.0, 200.0)) (Thickness 0.0)
    Assert.Equal(bare + 200.0, padded)

[<Fact>]
let ``内容自身的下边距会计入可滚动范围`` () =
    let bare = extentWith (Thickness 0.0) (Thickness 0.0)
    let margined = extentWith (Thickness 0.0) (Thickness(0.0, 0.0, 0.0, 200.0))
    Assert.Equal(bare + 200.0, margined)
