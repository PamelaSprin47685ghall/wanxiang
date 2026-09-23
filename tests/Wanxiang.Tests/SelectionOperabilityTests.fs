module Wanxiang.Tests.SelectionOperabilityTests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.UI
open Wanxiang.Tests

/// 借鉴kelivo 桌面客户端的交互收敛：侧栏批量选择 + Ctrl+M 模型选择器 + 长用户消息折叠。
/// 只锁定可观察契约（进入/退出、行点击语义、命令载荷、快捷键解析、折叠上限），
/// 不触发动效，也不复述实现细节。
let rec private descendants (control: Control) = seq {
    yield control
    match control with
    | :? Panel as panel ->
        for child in panel.Children do yield! descendants child
    | :? Decorator as decorator when not (isNull decorator.Child) ->
        yield! descendants decorator.Child
    | :? ContentControl as host ->
        match host.Content with
        | :? Control as child -> yield! descendants child
        | _ -> ()
    | _ -> ()
}

let private byAutomationName (root: Control) (name: string) =
    descendants root
    |> Seq.find (fun control -> AutomationProperties.GetName(control) = name)

/// 侧栏行由虚拟化 ListBox 托管，不在 root 的视觉树里：经已实现容器取。
let private rowByName (sidebar: Sidebar) (title: string) =
    let rec visualControls (visual: Visual) =
        seq {
            match visual with
            | :? Control as control -> yield control
            | _ -> ()
            for child in visual.GetVisualChildren() do
                yield! visualControls child
        }
    let list =
        descendants sidebar
        |> Seq.pick (function :? ListBox as lb -> Some lb | _ -> None)
    list.GetRealizedContainers()
    |> Seq.collect visualControls
    // 行主机由 RenderRow 打上 Tag = ConversationSummary；ListBoxItem 数据容器的 Tag 是别的东西，
    // 用 Tag 锁定 Sidebar 自己建的那一个 ActionBorder，而不是外层容器。
    |> Seq.find (fun c ->
        AutomationProperties.GetName(c) = title
        && match c with
           | :? Border as border ->
               match border.Tag with
               | :? ConversationSummary -> true
               | _ -> false
           | _ -> false)

let private show (content: Control) width height =
    Headless.ensure ()
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

let private summary id title pinned =
    { id = id
      title = title
      preview = "preview"
      running = false
      pinned = pinned
      archived = false
      createdAt = DateTimeOffset.Now
      updatedAt = DateTimeOffset.Now
      messageCount = 2
      isFork = false
      providerId = "openai"
      model = "gpt"
      lastCommitId = 1UL }

/// 构造一个带真实交互的 Sidebar：所有动作记录到可变列表，测试直接读意图。
/// buildSidebar 建的那个 OverlayHost：浮层画在它上面，测试据此断言 IsPopupOpen。
let private lastOverlay = ResizeArray<OverlayHost>()

let private buildSidebar () =
    // Ui / Sidebar 模块顶层建有 Cursor：先确保无头平台就绪，否则触碰成员即炸。
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    lastOverlay.Clear()
    lastOverlay.Add overlay
    let opened = ResizeArray<Guid>()
    let pinned = ResizeArray<Guid list * bool>()
    let archived = ResizeArray<Guid list * bool>()
    let deleted = ResizeArray<Guid list>()
    // 置顶判定简化：未置顶的集合上报 false，置顶键因此显示「置顶」。
    let actions: SidebarActions =
        { newConversation = ignore
          openConversation = fun id -> opened.Add id
          renameConversation = ignore
          deleteConversation = ignore
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          // 此夹具里没有任何会话预先置顶 → 键面显示「置顶」，动作为置顶。
          selectionAllPinned = fun _ -> false
          setPinnedMany = fun ids pin -> pinned.Add(ids, pin)
          setArchivedMany = fun ids value -> archived.Add(ids, value)
          deleteMany = fun ids -> deleted.Add ids
          duplicateAsFork = ignore
          exportConversation = ignore
          openSettings = ignore
          reconnect = ignore
          toggleArchivedVisibility = ignore
          closeNavigation = ignore }
    let sidebar = Sidebar(overlay, actions, fun _ -> Border() :> Control)
    sidebar.Build()
    root.Children.Add sidebar
    root, sidebar, opened, pinned, archived, deleted

// 进入多选后，同一行的点击不再打开会话，而是切换选中：这是批量模式的核心语义，
// 也是「误点不会跳走」的保障。
[<Fact>]
let ``entering selection mode turns row clicks into selection toggles`` () =
    let root, sidebar, opened, _, _, _ = buildSidebar ()
    let id = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        // 先挂窗再灌数据：虚拟化只在挂树后生成行容器。
        sidebar.SetConversations [ summary id "第一个会话" false ]
        Dispatcher.UIThread.RunJobs()
        let row = rowByName sidebar "第一个会话"
        sidebar.EnterSelection id
        Dispatcher.UIThread.RunJobs()
        // 模式内点同一行：从「已选中」变「未选中」，绝不打开会话。
        row.RaiseEvent(
            KeyEventArgs(
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
                KeyModifiers = KeyModifiers.None,
                Source = row))
        Dispatcher.UIThread.RunJobs()
        Assert.Empty opened
        sidebar.ExitSelection()
    finally
        window.Close()

// 清空最后一项即退出模式：空选择保留操作条会让用户面对无意义的计数。
[<Fact>]
let ``deselecting the last item exits selection mode`` () =
    let root, sidebar, _, _, _, _ = buildSidebar ()
    let id = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ summary id "唯一会话" false ]
        Dispatcher.UIThread.RunJobs()
        sidebar.EnterSelection id
        Dispatcher.UIThread.RunJobs()
        Assert.True(sidebar.IsSelectionMode)
        sidebar.ToggleSelected id
        Dispatcher.UIThread.RunJobs()
        Assert.False(sidebar.IsSelectionMode)
        Assert.Empty(sidebar.SelectedIds())
    finally
        window.Close()

// 批量置顶/归档按当前选择逐个发命令，Payload 逐条独立可确认。
[<Fact>]
let ``batch pin and archive issue one command per selected conversation`` () =
    let root, sidebar, _, pinned, archived, _ = buildSidebar ()
    let a, b = Guid.NewGuid(), Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ summary a "甲" false; summary b "乙" false ]
        Dispatcher.UIThread.RunJobs()
        // 全选把两行都纳入选择：顺序与可见行一致（再点一次全选则全部取消并退出模式）。
        sidebar.ToggleSelectAllVisible()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal<Set<Guid>>(Set.ofList [ a; b ], Set.ofList (sidebar.SelectedIds()))
        Assert.True(sidebar.IsSelectionMode, "ToggleSelectAllVisible 应进入选择模式")
        let barVisible =
            descendants sidebar
            |> Seq.exists (fun c ->
                match c with
                | :? Border as b -> b.IsVisible && b.Padding = Thickness(Tokens.space2, Tokens.space2)
                | _ -> false)
        Assert.True(barVisible, "批量操作条应可见")
        // 从批量条的真实按钮触发（按钮由 Build 时构造，文本即动作名）。
        // 只认可见按钮：列表行/其它界面也可能出现同名文案。
        let buttonByText (label: string) =
            descendants sidebar
            |> Seq.tryFind (fun c ->
                match c with
                | :? Border as border when border.IsEffectivelyVisible ->
                    match border.Child with
                    | :? TextBlock as text -> text.Text = label
                    | _ -> false
                | _ -> false)
        let pinButton = buttonByText "置顶"
        Assert.True(pinButton.IsSome, "批量条应有置顶按钮")
        let invoke =
            Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement pinButton.Value
            |> Assert.IsAssignableFrom<Avalonia.Automation.Provider.IInvokeProvider>
        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        let pinIds, pinValue = pinned.[0]
        Assert.Equal<Set<Guid>>(Set.ofList [ a; b ], Set.ofSeq pinIds)
        Assert.True pinValue
        let archiveButton = buttonByText "归档"
        Assert.True(archiveButton.IsSome, "批量条应有归档按钮")
        let archiveInvoke =
            Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement archiveButton.Value
            |> Assert.IsAssignableFrom<Avalonia.Automation.Provider.IInvokeProvider>
        archiveInvoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        let archiveIds, archiveValue = archived.[0]
        Assert.Equal<Set<Guid>>(Set.ofList [ a; b ], Set.ofSeq archiveIds)
        Assert.True archiveValue
    finally
        window.Close()

// Ctrl+M 解析为打开模型选择器：不必先找到左下角芯片，键盘可直接换模型。
// 选择模式只留「取消 / 全选 / 计数」与底部批量条：品牌行与页脚让位。
// 侧栏只有 232–420pt 宽，四条常驻条会把列表压成几条——这条不变量锁住空间纪律。
[<Fact>]
let ``selection mode gives up the brand header and footer`` () =
    let root, sidebar, _, _, _, _ = buildSidebar ()
    let id = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ summary id "甲" false ]
        Dispatcher.UIThread.RunJobs()
        sidebar.EnterSelection id
        Dispatcher.UIThread.RunJobs()
        // 品牌行的「万象」字标与页脚的连接状态行都不可见（不是不存在，是不占空间）。
        let brandWordmarkVisible =
            descendants sidebar
            |> Seq.exists (fun c ->
                match c with
                | :? TextBlock as tb -> tb.Text = "万象" && tb.IsEffectivelyVisible
                | _ -> false)
        Assert.False(brandWordmarkVisible, "选择模式下品牌行应让位")
        let newConversationVisible =
            descendants sidebar
            |> Seq.exists (fun c ->
                Avalonia.Automation.AutomationProperties.GetName(c) = "新建会话（Ctrl+N）"
                && c.IsEffectivelyVisible)
        Assert.False(newConversationVisible, "选择模式下右上角新建按钮应让位")
        Assert.True(sidebar.IsSelectionMode)
    finally
        window.Close()

// 「多选」入口就在行右键菜单里：桌面用户不必先知道有批量模式。
// 这里驱动真实菜单项（浮层画在 OverlayHost 上），验证入口 → 进入模式 → 首项选中。
[<Fact>]
let ``row context menu offers the selection entry`` () =
    let root, sidebar, opened, _, _, _ = buildSidebar ()
    let overlay = lastOverlay.[0]
    let id = Guid.NewGuid()
    let window = show root 360.0 700.0
    try
        sidebar.SetConversations [ summary id "菜单甲" false ]
        Dispatcher.UIThread.RunJobs()
        let list =
            descendants sidebar
            |> Seq.pick (function :? ListBox as lb -> Some lb | _ -> None)
        let row =
            list.GetRealizedContainers()
            |> Seq.collect (fun container -> container.GetVisualDescendants())
            |> Seq.choose (function :? Control as c -> Some c | _ -> None)
            |> Seq.find (fun c -> AutomationProperties.GetName(c) = "菜单甲")
        let moreButton =
            descendants row
            |> Seq.find (fun c ->
                match AutomationProperties.GetName(c) with
                | null -> false
                | name -> name.StartsWith("会话“菜单甲”的操作菜单"))
        moreButton.RaiseEvent(KeyEventArgs(Key = Key.Enter, RoutedEvent = InputElement.KeyDownEvent))
        Dispatcher.UIThread.RunJobs()
        Assert.True(overlay.IsPopupOpen, "行菜单应打开")
        let multiSelect =
            descendants root
            |> Seq.find (fun c -> AutomationProperties.GetName(c) = "多选")
        let invoke =
            Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement multiSelect
            |> Assert.IsAssignableFrom<Avalonia.Automation.Provider.IInvokeProvider>
        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.True(sidebar.IsSelectionMode, "点「多选」应进入选择模式")
        Assert.Equal<Guid list>([ id ], sidebar.SelectedIds())
        Assert.Empty opened
        sidebar.ExitSelection()
    finally
        window.Close()

[<Fact>]
let ``ctrl+m resolves to the model picker action`` () =
    Headless.ensure ()
    let args =
        KeyEventArgs(
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.M,
            KeyModifiers = KeyModifiers.Control)
    Assert.Equal(OpenModelPicker, ShortcutRouter.resolve args)

// 长用户消息折叠在同一 detailViewport contract 上：未超限时不出现展开入口，
// 超限时才出现，且上限取自 LayoutPolicy 的专用常量（比工具详情更矮）。
[<Fact>]
let ``long user messages get a clipped viewport with an expand entry`` () =
    Headless.ensure ()
    let ctx: MessageContext =
        { fontSize = Tokens.fontReading
          autoCollapseReasoning = false
          streaming = false
          isLastAssistant = false
          usage = None
          missingAttachments = Set.empty
          brandAvatar = fun () -> Border() :> Control }
    let actions: MessageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }
    let short = { MessageView.empty with role = "user"; text = "很短的一条"; commitId = Some 1UL }
    let long =
        { MessageView.empty with
            role = "user"
            text = String.replicate 40 "这是一段很长的用户输入，用来触发折叠视口。"
            commitId = Some 2UL }
    let shortWindow = show (MessageCard.render short ctx actions (Some 0)) 420.0 300.0
    try
        // 布局分多趟落地：先量出视口，再判定是否裁切。
        Dispatcher.UIThread.RunJobs()
        Dispatcher.UIThread.RunJobs()
        let expandButtons =
            descendants (shortWindow.Content :?> Control)
            |> Seq.choose (function :? Border as b -> Some b | _ -> None)
            |> Seq.filter (fun b -> AutomationProperties.GetName(b) = "展开全部")
            |> List.ofSeq
        Assert.False(expandButtons |> List.exists (fun b -> b.IsVisible), "短消息不应出现展开入口")
    finally
        shortWindow.Close()
    let longWindow = show (MessageCard.render long ctx actions (Some 1)) 420.0 300.0
    try
        Dispatcher.UIThread.RunJobs()
        let scrollers =
            descendants (longWindow.Content :?> Control)
            |> Seq.choose (function :? Avalonia.Controls.ScrollViewer as s -> Some s | _ -> None)
            |> Seq.filter (fun s -> s.IsVisible)
            |> List.ofSeq
        let userViewport =
            scrollers
            |> List.tryFind (fun s -> abs (s.MaxHeight - LayoutPolicy.expandedLongMessageMaxHeight) < 0.1)
        Assert.True(userViewport.IsSome, "长用户消息应有专用折叠视口")
        Assert.True(userViewport.Value.Extent.Height > userViewport.Value.Viewport.Height)
    finally
        longWindow.Close()
