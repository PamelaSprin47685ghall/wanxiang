namespace Wanxiang.Tests

// r20 命令面板：决策 370–379。

open System
open System.Reflection
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.UI

module R20PaletteTests =

    let private item id kind title keywords =
        { id = id; kind = kind; title = title; subtitle = ""; keywords = keywords; shortcut = None }

    let private sample =
        [ item "act:new" PaletteAction "新建对话" [ "new" ]
          item "act:theme" PaletteAction "切换主题" [ "theme" ]
          item "set:appearance" PaletteSetting "设置 › 外观与操作" []
          item "conv:1" PaletteConversation "周报草稿" []
          item "conv:2" PaletteConversation "Rust 学习笔记" [] ]

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``372 Ctrl+K 打开面板，Ctrl+Shift+F 归侧栏搜索，其他键不变`` () =
        Headless.run (fun () ->
            let resolve key mods = ShortcutRouter.resolve(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods))
            Assert.Equal(OpenCommandPalette, resolve Key.K KeyModifiers.Control)
            Assert.Equal(OpenCommandPalette, resolve Key.K KeyModifiers.Meta)
            Assert.Equal(FocusSearch, resolve Key.F (KeyModifiers.Control ||| KeyModifiers.Shift))
            Assert.Equal(OpenModelPicker, resolve Key.M KeyModifiers.Control)
            Assert.Equal(NoShortcut, resolve Key.F KeyModifiers.Control))

    let private texts (root: Control) =
        root.GetVisualDescendants() |> Seq.choose (function :? TextBlock as t -> Option.ofObj t.Text | _ -> None) |> List.ofSeq

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``376 面板键盘：方向键选择、回车执行所选项、全文交接在末尾`` () =
        Headless.run (fun () ->
            let mutable ran: (string * string) option = None
            let content, search = CommandPaletteView.build (fun () -> sample) (fun () -> []) (fun i q -> ran <- Some(i.id, q))
            let window = Window(Width = 700.0, Height = 600.0, Content = content)
            window.Show()
            Dispatcher.UIThread.RunJobs()
            search.Text <- "对话"
            Dispatcher.UIThread.RunJobs()
            let all = texts content
            Assert.Contains("在全部消息中搜索「对话」", all)
            let press k = search.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = k, KeyModifiers = KeyModifiers.None))
            press Key.Down
            press Key.Enter
            Assert.True(ran.IsSome)
            Assert.Equal("对话", snd ran.Value)
            // 读屏：状态行是 polite 活动区域，并播报所选项位置。
            let status = content.GetVisualDescendants() |> Seq.pick (function :? TextBlock as t when t.Name = "CommandPaletteStatus" -> Some t | _ -> None)
            Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Polite, Avalonia.Automation.AutomationProperties.GetLiveSetting status)
            search.Text <- "zzzzq"
            Dispatcher.UIThread.RunJobs()
            Assert.Contains("没有匹配", status.Text)
            window.Close())

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``374 抽屉里有命令面板入口，Ctrl+K 在主界面打开面板`` () =
        Headless.run (fun () ->
            MotionPolicy.setReduced true
            let shell = MainView()
            shell.Build()
            let window = Window(Width = 1000.0, Height = 800.0, Content = shell)
            window.Show()
            Dispatcher.UIThread.RunJobs()
            let sidebar = typeof<MainView>.GetField("sidebar", BindingFlags.NonPublic ||| BindingFlags.Instance).GetValue(shell) :?> Sidebar
            Assert.True(sidebar.PaletteButton.IsVisible)
            let e = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.K, KeyModifiers = KeyModifiers.Control)
            shell.HandleShortcut e
            Dispatcher.UIThread.RunJobs()
            Assert.True(e.Handled)
            let overlay = typeof<MainView>.GetField("overlay", BindingFlags.NonPublic ||| BindingFlags.Instance).GetValue(shell) :?> OverlayHost
            Assert.True(overlay.IsDialogOpen)
            Assert.Contains("新建对话", texts shell)
            window.Close())
