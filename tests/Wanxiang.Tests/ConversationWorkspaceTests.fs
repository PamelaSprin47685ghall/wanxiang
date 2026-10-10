module Wanxiang.Tests.ConversationWorkspaceTests

open System
open System.IO
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Automation.Provider
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.UI
open Wanxiang.Tests

let rec private descendants (control: Control) = seq {
    yield control
    match control with
    | :? Panel as panel -> for child in panel.Children do yield! descendants child
    | :? Decorator as border when not (isNull border.Child) -> yield! descendants border.Child
    | :? ContentControl as host ->
        match host.Content with :? Control as child -> yield! descendants child | _ -> ()
    | _ -> () }

let private named root name =
    descendants root |> Seq.find (fun control -> AutomationProperties.GetName control = name)

let private press (control: Control) =
    control.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter))
    Dispatcher.UIThread.RunJobs()

let private composer () =
    let view = Composer({ submit = (fun _ -> true); stopGeneration = ignore; pickAttachment = ignore
                          removeAttachment = ignore; openModelPicker = ignore; dropFiles = ignore
                          pasteFromClipboard = fun () -> false })
    view.Build()
    view.SetEnabled(true, "")
    view.SetEnterSends true
    view.SetModelLabel "模型 / 示例"
    view

let private chat () =
    let view = ChatView(
        { renameTitle = ignore; openSessionSettings = ignore; forkFromHere = ignore
          stopGeneration = ignore; requestOlderHistory = ignore; retryLast = ignore
          toggleSidebar = ignore; focusHome = ignore
          message = { copyText = ignore; regenerate = ignore; editAndFork = ignore
                      deleteMessage = ignore; downloadAttachment = ignore; openLink = ignore } }, Brand.logo)
    view.Build()
    view.SetConversationChrome true
    view.SetTitle("讨论一个问题", true)
    view

let private actions update : SettingsActions =
    { upsertProvider = (fun _ cb -> cb true); deleteProvider = ignore; probeProvider = ignore
      upsertMcp = (fun _ cb -> cb true); deleteMcp = ignore; updateGeneration = update
      savePrefs = ignore; toast = fun _ _ -> () }

let private field root placeholder =
    descendants root |> Seq.choose (function :? TextBox as box -> Some box | _ -> None)
    |> Seq.find (fun box -> box.PlaceholderText = placeholder)

let private capture (window: Window) name =
    match Environment.GetEnvironmentVariable "WANXIANG_UX_ARTIFACT_DIR" with
    | null | "" -> ()
    | directory ->
        Directory.CreateDirectory directory |> ignore
        AvaloniaHeadlessPlatform.ForceRenderTimerTick()
        use frame = window.CaptureRenderedFrame()
        Assert.NotNull frame
        frame.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default)

[<Trait("Category", "UI"); Fact>]
let ``starting and continuing share the same focused draft and attachment controls`` () =
    Headless.run (fun () ->
        let chat = chat ()
        chat.ShowEmpty(NoConversation, None)
        let composer = composer ()
        let surface = ConversationWorkspace(chat, composer)
        let window = Window(Width = 1000.0, Height = 820.0, Content = surface)
        window.Show()
        try
            surface.SetStarting true
            composer.SetText "保留这段尚未发送的想法"
            Dispatcher.UIThread.RunJobs()
            let input = descendants composer |> Seq.pick (function :? TextBox as b when b.AcceptsReturn -> Some b | _ -> None)
            let parent = composer.Parent
            Assert.True(input.IsFocused)
            let before = composer.TranslatePoint(Point(), surface).Value.Y
            Assert.True(before < surface.Bounds.Height * 0.75, "起步输入应在画面中部，而非底边")
            capture window "start-desktop"
            surface.SetStarting false
            Dispatcher.UIThread.RunJobs()
            Assert.Same(parent, composer.Parent)
            Assert.True(input.IsFocused)
            Assert.Equal("保留这段尚未发送的想法", composer.Text)
            let after = composer.TranslatePoint(Point(), surface).Value.Y
            Assert.True(after > before)
            window.Height <- 460.0
            surface.SetStarting true
            Dispatcher.UIThread.RunJobs()
            let at = composer.TranslatePoint(Point(), surface).Value
            Assert.True(at.Y + composer.Bounds.Height <= surface.Bounds.Height + 1.0)
            Assert.True(input.IsEffectivelyVisible)
        finally window.Close())

[<Trait("Category", "UI"); Theory; InlineData(375.0); InlineData(520.0); InlineData(1000.0)>]
let ``request context and execution actions stay within the composer`` width =
    Headless.run (fun () ->
        let view = composer ()
        view.SetSessionContext(Some { SessionConfig.empty with tools = [ "test-tool" ] }, true, ignore)
        view.SetCompactMode(width < LayoutPolicy.compactBreakpoint)
        view.SetModelLabel(String.replicate 12 "模型名称")
        let window = Window(Width = width, Height = 620.0, Content = view)
        window.Show()
        try
            view.SetText "这里有一段可以继续编辑的草稿。"
            view.SetGenerating true
            view.SetCanStop true
            Dispatcher.UIThread.RunJobs()
            for name in [ "会话上下文"; "停止生成"; "排队发送"; "添加附件" ] do
                let control = named view name
                let point = control.TranslatePoint(Point(), view).Value
                Assert.True(point.X >= 0.0, name)
                Assert.True(point.X + control.Bounds.Width <= view.Bounds.Width + 1.0, name)
                Assert.True(control.IsEffectivelyVisible, name)
            if width = 375.0 then capture window "composer-compact"
            view.SetEnabled(false, "连接已断开，内容仍在当前窗口。")
            Assert.Equal("这里有一段可以继续编辑的草稿。", view.Text)
            Assert.False((view.ModelAnchor()).IsEnabled)
        finally window.Close())

[<Trait("Category", "UI"); Fact>]
let ``compact conversation menu keeps fork export and reading actions reachable`` () =
    Headless.run (fun () ->
        let root = Grid()
        let overlay = OverlayHost root
        let view = chat ()
        let mutable exports = 0
        view.ConfigureMenu(overlay, fun () -> exports <- exports + 1)
        root.Children.Insert(0, view)
        view.SetCompactMode true
        let window = Window(Width = 375.0, Height = 700.0, Content = root)
        window.Show()
        try
            view.RenderMessages([ { MessageView.empty with role = "user"; text = "第一条问题"; commitId = Some 1UL } ], None, None, Tokens.fontReading, true, None, Set.empty)
            Dispatcher.UIThread.RunJobs()
            let trigger = named root "会话操作"
            press trigger
            Assert.True((named root "从当前分叉").IsEffectivelyVisible)
            Assert.True((named root "会话上下文").IsEffectivelyVisible)
            Assert.True((named root "回到会话开头").IsEffectivelyVisible)
            press (named root "导出为 Markdown")
            Assert.Equal(1, exports)
            Assert.True(trigger.IsEffectivelyVisible)
        finally window.Close())

[<Trait("Category", "UI"); Theory; InlineData(700.0, 780.0); InlineData(375.0, 520.0)>]
let ``session parameters reveal validation errors and leave save outside the scroll area`` (width: float, height: float) =
    Headless.run (fun () ->
        let root = Grid()
        let overlay = OverlayHost root
        let window = Window(Width = width, Height = height, Content = root)
        window.Show()
        try
            let mutable submissions = 0
            Dialogs.sessionSettings overlay Catalog.empty { SessionConfig.empty with temperature = Some 3.0 }
                (fun _ cb -> submissions <- submissions + 1; cb true)
            Dispatcher.UIThread.RunJobs()
            let advanced = named root "生成参数" :?> Expander
            Assert.False advanced.IsExpanded
            let save = named root "保存"
            Assert.False(save.GetVisualAncestors() |> Seq.exists (fun v -> v :? ScrollViewer))
            capture window (if width > 400.0 then "session-context" else "session-context-compact")
            press save
            Assert.Equal(0, submissions)
            Assert.True advanced.IsExpanded
            let temperature = field root "0–2，留空跟随默认"
            Assert.True temperature.IsEffectivelyVisible
            let at = save.TranslatePoint(Point(), root).Value
            Assert.True(at.Y + save.Bounds.Height <= root.Bounds.Height + 1.0, "展开错误参数后保存按钮仍在视口内")
            temperature.Text <- "0.7"
            press save
            Assert.Equal(1, submissions)
        finally window.Close())

[<Trait("Category", "UI"); Fact>]
let ``save acknowledgement never overwrites edits made while the request is pending`` () =
    Headless.run (fun () ->
        let mutable complete: bool -> unit = ignore
        let mutable count = 0
        let general = SettingsGeneral(OverlayHost(Grid()), actions (fun _ cb -> count <- count + 1; complete <- cb), ignore)
        general.SetGenerationScope "server-a"
        let form = general.BuildGeneration()
        general.SetCatalog Catalog.empty
        let context = field form "200"
        context.Text <- "300"
        press (named form "保存生成设置")
        Assert.Equal(1, count)
        context.Text <- "400"
        complete true
        Dispatcher.UIThread.RunJobs()
        general.SetCatalog { Catalog.empty with generation = { Catalog.empty.generation with maxContextMessages = 300 } }
        Assert.True(general.HasUnsavedGeneration)
        Assert.Equal("400", context.Text)
        Assert.Contains("未保存", (named form "生成设置保存状态" :?> TextBlock).Text)
        general.DiscardGenerationChanges()
        Assert.Equal("300", context.Text)
        Assert.False(general.HasUnsavedGeneration))

[<Trait("Category", "UI"); Fact>]
let ``switching servers keeps the old draft but cannot submit it to the new server`` () =
    Headless.run (fun () ->
        let mutable complete: bool -> unit = ignore
        let mutable count = 0
        let general = SettingsGeneral(OverlayHost(Grid()), actions (fun _ cb -> count <- count + 1; complete <- cb), ignore)
        let form = general.BuildGeneration()
        general.SetGenerationScope "server-a"
        general.SetCatalog Catalog.empty
        let context = field form "200"
        context.Text <- "345"
        press (named form "保存生成设置")
        general.SetGenerationScope "server-b"
        general.SetCatalog { Catalog.empty with generation = { Catalog.empty.generation with maxContextMessages = 222 } }
        complete true
        press (named form "保存生成设置")
        Assert.Equal(1, count)
        Assert.Equal("345", context.Text)
        Assert.True(general.HasUnsavedGeneration)
        Assert.Contains("不能保存", (named form "生成设置保存状态" :?> TextBlock).Text)
        general.DiscardGenerationChanges()
        Assert.Equal("222", context.Text)
        context.Text <- "223"
        press (named form "保存生成设置")
        Assert.Equal(2, count))

