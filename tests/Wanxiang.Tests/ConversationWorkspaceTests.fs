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
open Avalonia.Styling
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
                      deleteMessage = ignore; downloadAttachment = ignore; openLink = ignore } }, SharedTheme.logo)
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

let private capture (window: Window) name (light: bool) =
    match Environment.GetEnvironmentVariable "WANXIANG_UX_ARTIFACT_DIR" with
    | null | "" -> ()
    | directory ->
        Directory.CreateDirectory directory |> ignore
        AvaloniaHeadlessPlatform.ForceRenderTimerTick()
        use frame = window.CaptureRenderedFrame()
        Assert.NotNull frame
        let fileName = if light then name + "-light.png" else name + ".png"
        frame.Save(Path.Combine(directory, fileName), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default)

let private applyTheme (window: Window) (light: bool) =
    MotionPolicy.setReduced true
    let themeMode = if light then ThemeMode.Light else ThemeMode.Dark
    Tokens.apply themeMode
    window.RequestedThemeVariant <- (if light then ThemeVariant.Light else ThemeVariant.Dark)
    Dispatcher.UIThread.RunJobs()

[<Trait("Category", "UI"); Fact>]
let ``a normal conversation start is an unboxed prompt followed by the sole composer`` () =
    Headless.run (fun () ->
        let chat = chat ()
        let composer = composer ()
        let surface = Wanxiang2.ShellLayout.WorkspaceStack(chat, composer, Wanxiang2.ShellDesk.ShellLook.layoutOf SharedTheme.manager)
        let window = Window(Width = 900.0, Height = 780.0, Content = surface)
        window.Show()
        try
            chat.ShowEmpty(NoConversation, None)
            surface.SetStarting true
            Dispatcher.UIThread.RunJobs()
            let headline =
                descendants chat
                |> Seq.pick (function :? TextBlock as text when text.Text = "今天想讨论什么？" -> Some text | _ -> None)
            Assert.Equal(Tokens.fontDisplay, headline.FontSize)
            // 起步文案直接位于空态容器，不再被一张带描边的卡片包围。
            Assert.IsType<StackPanel>(headline.Parent.Parent) |> ignore
            Assert.Single(descendants surface |> Seq.filter (fun c -> obj.ReferenceEquals(c, composer))) |> ignore
        finally window.Close())

[<Trait("Category", "UI"); Fact>]
let ``composer shows custom context only when useful and reserves no idle queue slot`` () =
    Headless.run (fun () ->
        let view = composer ()
        view.SetText "保留的草稿"
        view.SetSessionContext(Some SessionConfig.empty, true, ignore)
        let window = Window(Width = 800.0, Height = 220.0, Content = view)
        window.Show()
        try
            Dispatcher.UIThread.RunJobs()
            let context = named view "对话上下文"
            let queue = named view "排队发送"
            let send = named view "发送"
            Assert.False(context.IsVisible)
            Assert.False(queue.IsVisible)
            let idleRightEdge = send.TranslatePoint(Point(send.Bounds.Width, 0.0), view).Value.X
            // 决策 364：只开了工具不算特殊设置，底栏不显示「工具 N 项」。
            view.SetSessionContext(Some { SessionConfig.empty with tools = [ "example-tool" ] }, true, ignore)
            Assert.False(context.IsVisible)
            view.SetSessionContext(Some { SessionConfig.empty with instructions = Some "简短回答" }, true, ignore)
            Assert.True(context.IsVisible)
            // 手机（compact）底栏只留模型、附件、发送。
            view.SetCompactMode true
            Assert.False(context.IsVisible)
            view.SetCompactMode false
            Assert.True(context.IsVisible)
            view.SetGenerating true
            Dispatcher.UIThread.RunJobs()
            Assert.True(queue.IsVisible)
            let stop = named view "停止生成"
            Assert.Same(send, stop)
            let activeRightEdge = stop.TranslatePoint(Point(stop.Bounds.Width, 0.0), view).Value.X
            Assert.InRange(abs(activeRightEdge - idleRightEdge), 0.0, 0.5)
            view.SetGenerating false
            view.SetSessionContext(Some SessionConfig.empty, true, ignore)
            Assert.False(queue.IsVisible)
            Assert.False(context.IsVisible)
            Assert.Equal("保留的草稿", view.Text)
        finally window.Close())

[<Trait("Category", "UI"); Theory; InlineData(false); InlineData(true)>]
let ``conversation first desktop composition keeps history secondary to the current draft`` (lightTheme: bool) =
    Headless.run (fun () ->
        let initialTheme = Tokens.current ()
        // 用合成历史检查整幅画面的关系；不连接主机，不读取真实对话。
        let root = Grid(Background = Tokens.canvas)
        root.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength 280.0))
        root.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength ControlMetrics.sidebarDividerWidth))
        root.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        let actions: SidebarActions =
            { newConversation = ignore
              openConversation = ignore
              renameConversation = ignore
              deleteConversation = ignore
              setPinned = fun _ _ -> ()
              setArchived = fun _ _ -> ()
              selectionAllPinned = fun _ -> false
              setPinnedMany = fun _ _ -> ()
              setArchivedMany = fun _ _ -> ()
              deleteMany = ignore
              duplicateAsFork = ignore
              exportConversation = ignore
              openSettings = ignore
              reconnect = ignore
              toggleArchivedVisibility = ignore
              closeNavigation = ignore }
        let sidebar = Sidebar(OverlayHost(Grid()), actions, SharedTheme.logo)
        sidebar.Build()
        let now = DateTimeOffset.Now
        let summary title preview pinned : ConversationSummary =
            { id = Guid.NewGuid(); title = title; preview = preview
              running = false; pinned = pinned; archived = false
              createdAt = now; updatedAt = now; messageCount = 4
              isFork = false; providerId = "sample"; model = "sample-model"; lastCommitId = 3UL }
        sidebar.SetConnection(true, "已连接")
        sidebar.SetServerUrl("ws://localhost:9000")
        sidebar.SetConversations
            [ summary "重整对话体验" "讨论页面结构和操作层级" true
              summary "一些随手想法" "留下一点想法，稍后继续" false
              summary "阅读笔记" "整理一段长文的要点" false ]
        let chat = chat ()
        chat.SetConversationChrome false
        chat.SetTitle("新对话", false)
        chat.ShowEmpty(NoConversation, None)
        let composer = composer ()
        let surface = Wanxiang2.ShellLayout.WorkspaceStack(chat, composer, Wanxiang2.ShellDesk.ShellLook.layoutOf SharedTheme.manager)
        Grid.SetColumn(sidebar, 0)
        Grid.SetColumn(surface, 2)
        root.Children.Add sidebar
        root.Children.Add surface
        let window = Window(Width = 1120.0, Height = 780.0, Content = root)
        applyTheme window lightTheme
        window.Show()
        try
            surface.SetStarting true
            Dispatcher.UIThread.RunJobs()
            Assert.True((named sidebar "新建对话（Ctrl / ⌘ + N）").IsEffectivelyVisible)
            Assert.True((named composer "切换本对话使用的模型").IsEffectivelyVisible)
            capture window "full-conversation-desktop" lightTheme
        finally
            Tokens.apply initialTheme
            window.Close())

[<Trait("Category", "UI"); Theory;
  InlineData(375.0, false); InlineData(375.0, true);
  InlineData(520.0, false); InlineData(520.0, true);
  InlineData(1000.0, false); InlineData(1000.0, true)>]
let ``request context and execution actions stay within the composer`` (width: float, lightTheme: bool) =
    Headless.run (fun () ->
        let initialTheme = Tokens.current ()
        let view = composer ()
        view.SetSessionContext(Some { SessionConfig.empty with instructions = Some "简短回答" }, true, ignore)
        let compact = width < LayoutPolicy.compactBreakpoint
        view.SetCompactMode compact
        view.SetModelLabel(String.replicate 12 "模型名称")
        let window = Window(Width = width, Height = 620.0, Content = view)
        applyTheme window lightTheme
        window.Show()
        try
            view.SetText "这里有一段可以继续编辑的草稿。"
            view.SetGenerating true
            view.SetCanStop true
            Dispatcher.UIThread.RunJobs()
            for name in [ (if compact then "添加附件" else "对话上下文"); "停止生成"; "排队发送"; "添加附件" ] do
                let control = named view name
                let point = control.TranslatePoint(Point(), view).Value
                Assert.True(point.X >= 0.0, name)
                Assert.True(point.X + control.Bounds.Width <= view.Bounds.Width + 1.0, name)
                Assert.True(control.IsEffectivelyVisible, name)
            if width = 375.0 then capture window "composer-compact" lightTheme
            view.SetEnabled(false, "连接已断开，内容仍在当前窗口。")
            Assert.Equal("这里有一段可以继续编辑的草稿。", view.Text)
            Assert.False((view.ModelAnchor()).IsEnabled)
        finally
            Tokens.apply initialTheme
            window.Close())

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
            let trigger = named root "对话操作"
            press trigger
            Assert.True((named root "从此分叉").IsEffectivelyVisible)
            Assert.True((named root "对话上下文").IsEffectivelyVisible)
            Assert.True((named root "回到对话开头").IsEffectivelyVisible)
            press (named root "导出（Markdown / JSON）…")
            Assert.Equal(1, exports)
            Assert.True(trigger.IsEffectivelyVisible)
        finally window.Close())

[<Trait("Category", "UI"); Theory;
  InlineData(700.0, 780.0, false); InlineData(700.0, 780.0, true);
  InlineData(375.0, 520.0, false); InlineData(375.0, 520.0, true)>]
let ``session parameters reveal validation errors and leave save outside the scroll area`` (width: float, height: float, lightTheme: bool) =
    Headless.run (fun () ->
        let initialTheme = Tokens.current ()
        let root = Grid()
        let overlay = OverlayHost root
        let window = Window(Width = width, Height = height, Content = root)
        applyTheme window lightTheme
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
            capture window (if width > 400.0 then "session-context" else "session-context-compact") lightTheme
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
        finally
            Tokens.apply initialTheme
            window.Close())

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
        press (named form "保存默认值")
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
        press (named form "保存默认值")
        general.SetGenerationScope "server-b"
        general.SetCatalog { Catalog.empty with generation = { Catalog.empty.generation with maxContextMessages = 222 } }
        complete true
        press (named form "保存默认值")
        Assert.Equal(1, count)
        Assert.Equal("345", context.Text)
        Assert.True(general.HasUnsavedGeneration)
        Assert.Contains("不能保存", (named form "生成设置保存状态" :?> TextBlock).Text)
        general.DiscardGenerationChanges()
        Assert.Equal("222", context.Text)
        context.Text <- "223"
        press (named form "保存默认值")
        Assert.Equal(2, count))

