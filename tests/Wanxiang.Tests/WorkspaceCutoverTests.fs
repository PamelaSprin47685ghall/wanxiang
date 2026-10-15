module Wanxiang.Tests.WorkspaceCutoverTests

open System
open System.IO
open System.Reflection
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.UI
open Wanxiang2.ChatScreen
open Wanxiang2.Workspace

let private flags = BindingFlags.Instance ||| BindingFlags.NonPublic
let private field (view: MainView) name = typeof<MainView>.GetField(name, flags).GetValue view
let private call (view: MainView) name = typeof<MainView>.GetMethod(name, flags).Invoke(view, [||]) |> ignore
let private settle () = Dispatcher.UIThread.RunJobs()

// 真实宿主组装，不连接主机、不读凭据；本机偏好恢复到用例开始前的原始字节。
let private withWorkspace width body =
    let prefsPath = UiPrefsFile.filePath ()
    let saved = if File.Exists prefsPath then Some(File.ReadAllBytes prefsPath) else None
    let view = MainView()
    typeof<MainView>.GetField("prefs", flags).SetValue(
        view, { UiPrefsFile.defaults with sidebarCollapsed = false; sidebarWidth = 280.0 })
    for name in [ "BuildSidebar"; "BuildComposer"; "BuildChat"; "BuildSettings"; "BuildWorkspace" ] do
        call view name
    let workspace = field view "workspace" :?> Workspace
    let composer = field view "composer" :?> Composer
    let chat = field view "chat" :?> ChatScreen
    let window = Window(Width = width, Height = 800.0, Content = view)
    window.Show()
    try
        settle ()
        composer.SetEnabled(true, "")
        body view workspace composer chat window
    finally
        window.Close()
        SharedTheme.input.Reset()
        match saved with
        | Some bytes -> File.WriteAllBytes(prefsPath, bytes)
        | None -> if File.Exists prefsPath then File.Delete prefsPath

[<Fact>]
let ``the host owns one Workspace and no second layout or viewport state`` () =
    Assert.Equal(typeof<Workspace>, typeof<MainView>.GetField("workspace", flags).FieldType)
    for name in [ "shellDesk"; "shellGrid"; "sidebarSplitter"; "hostViewportWidth" ] do
        Assert.Null(typeof<MainView>.GetField(name, flags))
    Assert.Null(typeof<MainView>.GetMethod("ApplyResponsiveLayout", flags))
    Assert.False(typeof<Control>.IsAssignableFrom typeof<Composer>)
    Assert.Null(typeof<Composer>.GetMethod "Build")

[<Trait("Category", "UI")>]
[<Fact>]
let ``desktop resize keeps the one input and its draft inside the real workspace`` () =
    Headless.run (fun () ->
        withWorkspace 1200.0 (fun view workspace composer chat window ->
            let root = view.Content :?> Grid
            Assert.Same(workspace.Host, root.Children[0])
            Assert.Same(composer.Desk.Host, composer.Host)
            let parent = composer.Host.Parent
            Assert.NotNull parent
            Assert.False(workspace.State.compactMode)
            Assert.True(workspace.Splitter.IsVisible)
            composer.SetText "缩放后仍是这份草稿"
            let input = composer.Input
            for width, compact in [ 600.0, true; 1200.0, false; 500.0, true; 1000.0, false ] do
                window.Width <- width
                settle ()
                Assert.Equal(compact, workspace.State.compactMode)
                Assert.Equal(not compact, workspace.Splitter.IsVisible)
                Assert.Same(parent, composer.Host.Parent)
                Assert.Same(input, composer.Input)
                Assert.Equal("缩放后仍是这份草稿", composer.Text)
                Assert.Single(view.GetVisualDescendants() |> Seq.filter (fun v -> obj.ReferenceEquals(v, composer.Host))) |> ignore
                Assert.Single(view.GetVisualDescendants() |> Seq.filter (fun v -> obj.ReferenceEquals(v, chat.Host))) |> ignore))

[<Trait("Category", "UI")>]
[<Fact>]
let ``CSS viewport wins over physical bounds and settings preserve the mounted input and focus`` () =
    Headless.run (fun () ->
        withWorkspace 1200.0 (fun view workspace composer chat window ->
            workspace.ObserveViewport 480.0
            settle ()
            Assert.True(workspace.State.compactMode)
            Assert.Equal(LayoutPolicy.compactActionTarget, chat.Header.SidebarButton.Width)
            let parent = composer.Host.Parent
            window.Width <- 1500.0
            settle ()
            Assert.True(workspace.State.compactMode)
            workspace.ObserveViewport 0.0
            Assert.True(workspace.State.compactMode)
            composer.SetText "设置页不能丢掉输入"
            composer.Focus()
            settle ()
            Assert.True(composer.Input.IsFocused)
            call view "ShowSettings"
            settle ()
            Assert.False(workspace.Host.IsVisible)
            call view "CloseSettings"
            settle ()
            Assert.True(workspace.Host.IsVisible)
            Assert.True(workspace.State.compactMode)
            Assert.Same(parent, composer.Host.Parent)
            Assert.Equal("设置页不能丢掉输入", composer.Text)
            Assert.True(composer.Input.IsFocused)
            workspace.ObserveViewport 1200.0
            settle ()
            Assert.False(workspace.State.compactMode)))

[<Trait("Category", "UI")>]
[<Fact>]
let ``the real header routes desktop collapse and compact focus without changing the desktop preference`` () =
    Headless.run (fun () ->
        withWorkspace 1200.0 (fun _ workspace composer chat window ->
            let press () =
                let button = chat.Header.SidebarButton
                button.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = button))
                settle ()
            press ()
            Assert.True(workspace.State.sidebarCollapsed)
            Assert.True((UiPrefsFile.load()).sidebarCollapsed)
            workspace.ObserveViewport 480.0
            SharedTheme.input.Report false
            press ()
            Assert.True(workspace.State.compactNavigationOpen)
            Assert.False(composer.Input.IsFocused)
            press ()
            Assert.False(workspace.State.compactNavigationOpen)
            Assert.True(composer.Input.IsFocused)
            Assert.True((UiPrefsFile.load()).sidebarCollapsed)
            // 触屏打开导航不能自行弹出键盘。
            window.FocusManager.Focus(null, NavigationMethod.Unspecified, KeyModifiers.None) |> ignore
            SharedTheme.input.Report true
            workspace.SetCompactNavigation true |> ignore
            settle ()
            Assert.True(workspace.State.compactNavigationOpen)
            Assert.Null(window.FocusManager.GetFocusedElement())
            workspace.SetCompactNavigation false |> ignore
            workspace.ObserveViewport 1200.0
            settle ()
            Assert.True(workspace.State.sidebarCollapsed)
            Assert.False(workspace.Splitter.IsVisible)))

[<Trait("Category", "UI")>]
[<Fact>]
let ``dragging the actual splitter saves the width and compact mode does not overwrite it`` () =
    Headless.run (fun () ->
        withWorkspace 1200.0 (fun _ workspace _ _ window ->
            let splitter = workspace.Splitter
            let grid = splitter.Parent :?> Grid
            let before = grid.ColumnDefinitions[0].ActualWidth
            let start = splitter.TranslatePoint(Point(splitter.Bounds.Width / 2.0, 200.0), window).Value
            let finish = Point(start.X + 48.0, start.Y)
            window.MouseMove(start, RawInputModifiers.None)
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None)
            window.MouseMove(finish, RawInputModifiers.LeftMouseButton)
            window.MouseUp(finish, MouseButton.Left, RawInputModifiers.None)
            settle ()
            let resized = grid.ColumnDefinitions[0].ActualWidth
            Assert.InRange(resized - before, 47.0, 49.0)
            Assert.Equal(resized, (UiPrefsFile.load()).sidebarWidth, 2)
            workspace.ObserveViewport 480.0
            settle ()
            Assert.Equal(resized, (UiPrefsFile.load()).sidebarWidth, 2)
            workspace.ObserveViewport 1200.0
            settle ()
            Assert.Equal(resized, grid.ColumnDefinitions[0].ActualWidth, 2)))
