module Wanxiang.Tests.SelectionOperabilityTests

open System
open System.Diagnostics
open System.Reflection
open System.Threading
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

/// 按钮上的文字。Ui.button 的壳是 Border，里面挂一个 TextBlock；
/// 无头测试不到绘制，但文本是纯数据，可直接读。
let private textOf (button: Control) =
    match button with
    | :? Border as border ->
        match border.Child with
        | :? TextBlock as tb -> tb.Text
        | _ -> ""
    | :? TextBlock as tb -> tb.Text
    | _ -> ""

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

/// 可选版：过滤后行可能不在已实现容器里（`rowByName` 的 pick/find 会抛），
/// 这里要的正是「取不到」这个信号。
let private rowByNameOpt (sidebar: Sidebar) (title: string) : Control option =
    let rec visualControls (visual: Visual) =
        seq {
            match visual with
            | :? Control as control -> yield control
            | _ -> ()
            for child in visual.GetVisualChildren() do
                yield! visualControls child
        }
    match descendants sidebar |> Seq.tryPick (function :? ListBox as lb -> Some lb | _ -> None) with
    | None -> None
    | Some list ->
        list.GetRealizedContainers()
        |> Seq.collect visualControls
        |> Seq.tryFind (fun c ->
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

// 多选期内 Esc 一律先退多选，不因焦点落在列表底部已归档开关上而失灵：
// 归档开关行也是列表内的 Tab 停靠点（up/down/home 四处键位都通得到），
// 会话行（Sidebar.fs:860）与搜索框（Sidebar.fs:1383）上的 Esc 都已退多选，
// 唯独这里漏了——用户 End 滚到底再按 Esc 毫无反应，被困在批量态。
// kelivo interactive_drawer.dart:399-407 同序：抽屉级返回先问「是不是在多选」。
[<Fact>]
let ``escape exits selection mode from the archived toggle row`` () =
    let root, sidebar, _, _, _, _ = buildSidebar ()
    let live = Guid.NewGuid()
    let archived = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations
            [ summary live "活跃会话" false
              { summary archived "归档会话" false with archived = true } ]
        Dispatcher.UIThread.RunJobs()
        sidebar.EnterSelection live
        Dispatcher.UIThread.RunJobs()
        Assert.True(sidebar.IsSelectionMode, "前置条件：处于多选态")
        // 已归档开关：列表底部与行同高的弱操作行，纵向键位链的最后一站。
        let toggle =
            root.GetVisualDescendants()
            |> Seq.choose (function :? Control as c -> Some c | _ -> None)
            |> Seq.find (fun c -> AutomationProperties.GetName c = "已归档会话 (1)，点击展开")
        Assert.True(toggle.Focusable, "前置条件：已归档开关可聚焦")
        toggle.Focus(NavigationMethod.Directional) |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True(toggle.IsFocused, "前置条件：焦点落在已归档开关上")
        let escape =
            KeyEventArgs(
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Escape,
                KeyModifiers = KeyModifiers.None,
                Source = toggle)
        toggle.RaiseEvent escape
        Dispatcher.UIThread.RunJobs()
        Assert.True(escape.Handled, "已归档开关上的 Escape 必须被本行消费")
        Assert.False(sidebar.IsSelectionMode, "多选态下焦点在已归档开关按 Escape 应退出多选")
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

// 批量模式里 Escape 只做退出：在搜索结果中进入多选的用户，Esc 的意图是离开
// 批量态，而不是被清掉筛选用词。此前有词的守卫让退出失效，Esc 落到普通态的
// 清词分支——用户丢失筛选条件还没退出多选。
[<Fact>]
let ``escape in selection mode exits the mode even with a search query`` () =
    let root, sidebar, _, _, _, _ = buildSidebar ()
    let a, b = Guid.NewGuid(), Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ summary a "甲" false; summary b "乙" false ]
        Dispatcher.UIThread.RunJobs()
        // 先键入搜索词：行 handler 据此把 Escape 让给「清空搜索」分支。
        let searchBox =
            descendants sidebar
            |> Seq.find (fun c -> c :? TextBox) :?> TextBox
        searchBox.Text <- "甲"
        Dispatcher.UIThread.RunJobs()
        sidebar.EnterSelection a
        Dispatcher.UIThread.RunJobs()
        Assert.True(sidebar.IsSelectionMode)
        let row = rowByName sidebar "甲"
        row.RaiseEvent(
            KeyEventArgs(
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Escape,
                KeyModifiers = KeyModifiers.None,
                Source = row))
        Dispatcher.UIThread.RunJobs()
        Assert.False(sidebar.IsSelectionMode, "有搜索词时行上的 Escape 也必须退出多选")
        Assert.True(searchBox.Text = "甲", sprintf "退出多选不得顺手清掉搜索词，实际 %s" searchBox.Text)
    finally
        window.Close()

// 批量条三个动作键随选中数翻面：0 项时禁用。既有守卫只挡住点击，键盘用户仍能
// Tab 上去按 Enter 得到静默无反应——按钮的启用态本身就是可操作性的承诺。
[<Fact>]
let ``selection action buttons disable themselves while nothing is selected`` () =
    let root, sidebar, _, _, _, _ = buildSidebar ()
    let a = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ summary a "甲" false ]
        Dispatcher.UIThread.RunJobs()
        sidebar.EnterSelection a
        Dispatcher.UIThread.RunJobs()
        // 只认可见按钮：批量条在进入模式后才上屏。
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
        let archiveButton = buttonByText "归档"
        let deleteButton = buttonByText "删除"
        Assert.True(pinButton.IsSome && archiveButton.IsSome && deleteButton.IsSome, "批量条应齐备")
        Assert.True(pinButton.Value.IsEnabled && archiveButton.Value.IsEnabled && deleteButton.Value.IsEnabled, "有选中项时三个动作键都必须可用")
        // 0 个可见选中：多选期间列表被换成不含已选项的内容（换筛选/搜索结果/
        // 外部刷新）。selectedIds 仍留着旧 id，可见行为空——计数说谎，按键按
        // 下去静默无反应。这正是按键启用态翻面的可达路径。
        let a2 = Guid.NewGuid()
        sidebar.EnterSelection a2
        Dispatcher.UIThread.RunJobs()
        sidebar.SetConversations [ summary (Guid.NewGuid()) "丙" false ]
        Dispatcher.UIThread.RunJobs()
        Assert.True(sidebar.IsSelectionMode, "选中项不可见时模式仍开着")
        Assert.True(sidebar.SelectedIds() |> List.isEmpty, "反证前提：没有可见的选中项")
        Assert.False(pinButton.Value.IsEnabled, "置顶键在没有可见选中项时必须禁用")
        Assert.False(archiveButton.Value.IsEnabled, "归档键在没有可见选中项时必须禁用")
        Assert.False(deleteButton.Value.IsEnabled, "删除键在没有可见选中项时必须禁用")
        Assert.False(pinButton.Value.Focusable, "禁用同时必须退出 Tab 序")
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

// 批量键的文案是当前选择的状态，不是固定标签：全选后同一键变成「取消全选」、
// 全已置顶后变成「取消置顶」。此前两者只在 Build 时算一次，勾选变化后只说旧话，
// 用户再点会得到与预期相反的动作。锁按钮文本随勾选变化翻转。
[<Fact>]
let ``selection buttons rename themselves as the selection changes`` () =
    let root, sidebar, _, _, _, _ = buildSidebar ()
    let a = Guid.NewGuid()
    let b = Guid.NewGuid()
    let mutable ids = [ a; b ]
    let window = show root 320.0 520.0
    try
        sidebar.SetConversations [ summary a "A" false; summary b "B" false ]
        Dispatcher.UIThread.RunJobs()
        let selectAll =
            descendants root |> Seq.find (fun c -> AutomationProperties.GetName(c) = "全选")
        sidebar.EnterSelection a
        Dispatcher.UIThread.RunJobs()
        // 只勾一项：仍是「全选」。
        Assert.Equal("全选", textOf selectAll)
        sidebar.ToggleSelected b
        Dispatcher.UIThread.RunJobs()
        // 全部勾上：键面翻成「取消全选」（动作仍是取消，见 ToggleSelectAllVisible）。
        Assert.Equal("取消全选", textOf selectAll)
        sidebar.ToggleSelected a
        Dispatcher.UIThread.RunJobs()
        // 退一项又变回「全选」：标签跟随选择实时判定，不是一次性装饰。
        Assert.Equal("全选", textOf selectAll)
    finally
        window.Close()

// 发送键与换行键严格互补：Enter 发送模式下 Ctrl+Enter 归换行，Ctrl+Enter 发送模式下
// 裸 Enter 归换行。此前裸 Enter 发送模式把 Ctrl+Enter 也发出去，用户改行只剩 Shift+Enter
// 一条路。锁：发送永远只在当前模式被声明的那个键上发生，另一个键不许提交。
[<Fact>]
let ``composer sends only on the mode's declared key`` () =
    Headless.ensure ()
    let submitted = ResizeArray<string>()
    let composerActions =
        { submit = fun text ->
            submitted.Add text
            true
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = ignore
          pasteFromClipboard = fun () -> false }
    let composer = Composer(composerActions)
    composer.Build()
    composer.SetEnabled(true, "")
    let rec findInput (c: Control) : TextBox option =
        match c with
        | :? TextBox as tb -> Some tb
        | :? Panel as p -> p.Children |> Seq.tryPick findInput
        | :? Decorator as d when not (isNull d.Child) -> findInput d.Child
        | _ -> None
    let input = findInput composer |> Option.defaultWith (fun () -> failwith "input missing")
    let sendKey (mods: KeyModifiers) =
        composer.SetText "payload"
        let args = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = mods)
        input.RaiseEvent args
        Dispatcher.UIThread.RunJobs()
        args
    try
        // 模式一：Enter 发送。裸 Enter 提交，Ctrl+Enter 不提交。
        sendKey KeyModifiers.None |> ignore
        Assert.Equal<string seq>([ "payload" ], submitted)
        let inEnterMode = sendKey KeyModifiers.Control
        Assert.False inEnterMode.Handled, "Ctrl+Enter 在 Enter 发送模式下必须留给 TextBox 换行"
        // 未被提交即留在草稿里：Composer 只在不该发时把事件放行给 TextBox。
        Assert.Equal(1, submitted.Count)
        // 模式二：Ctrl+Enter 发送。裸 Enter 不提交。
        submitted.Clear()
        composer.SetEnterSends false
        Dispatcher.UIThread.RunJobs()
        let plainInCtrlMode = sendKey KeyModifiers.None
        Assert.False plainInCtrlMode.Handled, "裸 Enter 在 Ctrl+Enter 发送模式下必须留给 TextBox 换行"
        Assert.Empty submitted
        sendKey KeyModifiers.Control |> ignore
        Assert.Equal<string seq>([ "payload" ], submitted)
    finally
        // composer 未挂窗，无根可关：测完只撤销其副作用。
        composer.SetEnabled(false, "")

// 从外部切换当前会话（Ctrl+1..9 / 搜索结果 / 新建）后，选中行若在视口外，
// 侧栏上看不到正在进行的会话。SetActive 只滚屏不抢焦点：焦点归属照旧由调用方决定。
// 锁：激活的行必须被滚进可视区。
[<Fact>]
let ``setting the active conversation scrolls it into view`` () =
    let root, sidebar, _, _, _, _ = buildSidebar ()
    let ids = [ for i in 1 .. 30 -> Guid.NewGuid() ]
    let window = show root 320.0 300.0
    try
        sidebar.SetConversations [ for id in ids -> summary id "会话" false ]
        Dispatcher.UIThread.RunJobs()
        // 先激活第一个：列表滚回顶部。
        sidebar.SetActive (List.tryHead ids)
        Dispatcher.UIThread.RunJobs()
        let list = descendants root |> Seq.pick (function :? ListBox as lb -> Some lb | _ -> None)
        // ListBox 自带内部 ScrollViewer（虚拟化滚动容器）：从视觉树里找。
        let scroller =
            list.GetVisualDescendants()
            |> Seq.choose (function :? ScrollViewer as s -> Some s | _ -> None)
            |> Seq.tryHead
        // 再把最后一项设为当前：它在长列表里远在视口外，必须被滚进来。
        sidebar.SetActive (List.tryLast ids)
        Dispatcher.UIThread.RunJobs()
        Dispatcher.UIThread.RunJobs()
        match scroller with
        | Some s ->
            // 视图顶部应从 0 移下来：未滚动时列表在 0，滚动后 Offset.Y > 0。
            Assert.True(s.Offset.Y > 0.0, "激活列表末端的会话应把列表滚出顶部")
        | None -> Assert.Fail("找不到会话列表的滚动容器")
    finally
        window.Close()

// 置顶键的文案只按「已选项是否全部置顶」判定。此前混入「必须全选可见行」的前提，
// 于是只挑两个已置顶的会话时键面仍说「置顶」，点下去发出的却是取消置顶——
// 文案与动作相反，用户要么不敢点，要么点错。
[<Fact>]
let ``pin key follows the selected items' pinned state alone`` () =
    // Ui / Sidebar 模块顶层建有 Cursor：先确保无头平台就绪，否则触碰成员即炸。
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    let pinnedMany = ResizeArray<Guid list * bool>()
    // 夹具上报：传入的任何选择都算「全部已置顶」——模拟两个已置顶会话被挑中的情形。
    let actions: SidebarActions =
        { newConversation = ignore
          openConversation = ignore
          renameConversation = ignore
          deleteConversation = ignore
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          selectionAllPinned = fun _ -> true
          setPinnedMany = fun ids pin -> pinnedMany.Add(ids, pin)
          setArchivedMany = fun _ _ -> ()
          deleteMany = fun _ -> ()
          duplicateAsFork = ignore
          exportConversation = ignore
          openSettings = ignore
          reconnect = ignore
          toggleArchivedVisibility = ignore
          closeNavigation = ignore }
    let sidebar = Sidebar(overlay, actions, fun _ -> Border() :> Control)
    sidebar.Build()
    root.Children.Add sidebar
    let a = Guid.NewGuid()
    let b = Guid.NewGuid()
    let c = Guid.NewGuid()
    let window = show root 320.0 520.0
    try
        sidebar.SetConversations [ summary a "A" true; summary b "B" true; summary c "C" true ]
        Dispatcher.UIThread.RunJobs()
        let pinKey =
            descendants root |> Seq.find (fun control -> AutomationProperties.GetName(control) = "置顶")
        // 只挑两项（第三项不在选择里）：仍是「取消置顶」——判定只看已选项。
        sidebar.EnterSelection a
        sidebar.ToggleSelected b
        Dispatcher.UIThread.RunJobs()
        Assert.Equal("取消置顶", textOf pinKey)
        // 动作与文案一致：发出去的就是取消置顶。
        let invoke (control: Control) =
            control.RaiseEvent(
                KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.None))
        invoke pinKey
        Dispatcher.UIThread.RunJobs()
        // 载荷只按已选两项算，顺序随可见行走：比对集合而非序列。
        let sentIds = pinnedMany |> Seq.map fst |> Seq.head |> List.ofSeq |> List.sort
        Assert.Equal<Guid list>([ a; b ] |> List.sort, sentIds)
        Assert.False(pinnedMany |> Seq.map snd |> Seq.head)
    finally
        window.Close()

// 空选择不进删除流：SelectedIds 只报可见行里的已选项，搜索把选中行滤掉后
// 选择计数非 0 而批量载荷为空——行上按 Delete 仍会调 deleteMany，确认框弹出
// 「选中的 0 个会话」。kelivo 侧栏以 selectedCount > 0 为启用条件
// （sidebar_selection_bars.dart:186），同一约定。
[<Fact>]
let ``batch delete with an empty selection never reaches the batch action`` () =
    let root, sidebar, _, _, _, deleted = buildSidebar ()
    let a = Guid.NewGuid()
    let b = Guid.NewGuid()
    let window = show root 320.0 520.0
    try
        sidebar.SetConversations [ summary a "Alpha" false; summary b "Beta" false ]
        Dispatcher.UIThread.RunJobs()
        let row = rowByName sidebar "Alpha"
        sidebar.EnterSelection a
        Dispatcher.UIThread.RunJobs()
        // 搜索框过滤到只剩 Beta：选中项 Alpha 不可见了。
        let search =
            descendants root
            |> Seq.choose (function :? TextBox as tb when tb.PlaceholderText = "搜索会话" -> Some tb | _ -> None)
            |> Seq.head
        search.Text <- "Beta"
        // 搜索有 160ms 防抖（MotionLedger.searchInputDebounce）：测试线程就是
        // UI 线程，睡着等不到 tick，直接把手里的 timer 到期推进。
        let timer =
            let field =
                typeof<Sidebar>.GetField("searchDebounce", Reflection.BindingFlags.NonPublic ||| Reflection.BindingFlags.Instance)
            field.GetValue(sidebar) :?> DispatcherTimer
        timer.Interval <- TimeSpan.FromMilliseconds(1.0)
        // 睡固定 20ms 在并行负载下会踩空：timer 到期回调排在别的淡入后面。
        // 改成「观察到过滤效果再往下走」，最多 1s 兜底。
        let mutable waited = 0
        let hasOnlyBeta () =
            sidebar.SelectedIds().Length = 0
            && (rowByNameOpt sidebar "Alpha").IsNone
            && (rowByNameOpt sidebar "Beta").IsSome
        while (not (hasOnlyBeta ())) && waited < 40 do
            timer.Interval <- TimeSpan.FromMilliseconds(1.0)
            Dispatcher.UIThread.RunJobs()
            Thread.Sleep 25
            Dispatcher.UIThread.RunJobs()
            waited <- waited + 1
        Assert.True(hasOnlyBeta (), sprintf "搜索过滤没落地：selected=%A" (sidebar.SelectedIds()))
        row.RaiseEvent(
            KeyEventArgs(
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Delete,
                KeyModifiers = KeyModifiers.None,
                Source = row))
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(0, deleted.Count)
    finally
        window.Close()
