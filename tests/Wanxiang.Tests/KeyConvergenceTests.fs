module Wanxiang.Tests.KeyConvergenceTests

open System
open System.Threading
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.UI
open Wanxiang.Tests

/// 本轮借鉴 kelivo（侧栏行直达操作、删除当前会话后的回退、打开设置的焦点归宿）
/// 的收敛性改动。只锁可观察契约：
///   - P 键在行焦点下切换置顶，且不吞 Ctrl/Meta 变体；
///   - 问出的邻位与 FocusAfterDelete 用的是同一个答案（删除后「打开谁」与「焦点落谁」一致）；
///   - 打开设置把焦点送到分区导航按钮上（此前焦点悬空）。
/// 不触发动效，不复述实现细节，不碰网络。
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

/// 侧栏按 updatedAt 降序排（ConversationSummary.sortDescending），
/// 夹具必须显式给出活动时间，否则「第几行是谁」不可预测。
let private summary id title updatedAt pinned =
    { id = id
      title = title
      preview = "preview"
      running = false
      pinned = pinned
      archived = false
      createdAt = updatedAt
      updatedAt = updatedAt
      messageCount = 2
      isFork = false
      providerId = "openai"
      model = "gpt"
      lastCommitId = 0UL }

let private show (content: Control) width height =
    Headless.ensure ()
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

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
    |> Seq.find (fun control ->
        AutomationProperties.GetName(control) = title
        && match control with
           | :? Border as border ->
               match border.Tag with
               | :? ConversationSummary -> true
               | _ -> false
           | _ -> false)

/// 造一棵真侧栏。夹具只记置顶调用，不验证命令载荷——那条由 CommandDispatch
/// 相关的用例覆盖，这里只关心「按键触发了哪个动作、参数是什么」。
let private buildSidebar () =
    Headless.ensure ()
    let root = Grid()
    let overlayHost = OverlayHost(root)
    // 只收行级置顶（P 键走这条），批量置顶留给别的夹具。
    let rowPins = ResizeArray<Guid * bool>()
    let actions: SidebarActions =
        { newConversation = ignore
          openConversation = ignore
          renameConversation = ignore
          deleteConversation = ignore
          setPinned = fun summary pinned -> rowPins.Add(summary.id, pinned)
          setArchived = fun _ _ -> ()
          selectionAllPinned = fun _ -> false
          setPinnedMany = fun _ _ -> ()
          setArchivedMany = fun _ _ -> ()
          deleteMany = fun _ -> ()
          duplicateAsFork = ignore
          exportConversation = ignore
          openSettings = ignore
          reconnect = ignore
          toggleArchivedVisibility = ignore
          closeNavigation = ignore }
    let sidebar = Sidebar(overlayHost, actions, fun _ -> Border() :> Control)
    sidebar.Build()
    // 侧栏自己不会挂树：这里是它进入视觉树的地方（虚拟化行容器由此才生成）。
    root.Children.Add sidebar
    root, sidebar, rowPins

let private pressRow (row: Control) (key: Key) (modifiers: KeyModifiers) =
    row.RaiseEvent(
        KeyEventArgs(
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = row))

// ── P 键切换置顶 ────────────────────────────────────────────────────────────

// 行已拿焦点时按 P 直接翻转置顶：此前置顶只有右键菜单一条路，
// 键盘用户得先唤菜单再找项。与 F2 / Delete 同一约定。
[<Fact>]
let ``p key on a focused row toggles pin`` () =
    let root, sidebar, rowPins = buildSidebar ()
    let id = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ summary id "第一个会话" (DateTimeOffset.Now.AddMinutes(-3.0)) false ]
        Dispatcher.UIThread.RunJobs()
        let row = rowByName sidebar "第一个会话"
        row.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        pressRow row Key.P KeyModifiers.None
        Dispatcher.UIThread.RunJobs()
        Assert.Equal<Guid * bool>([ (id, true) ], Seq.toList rowPins)
        // 再按一次翻回去：同一个键是切换，不是「置顶」单向动作。
        // 键面取自服务端已确认的状态（summaryById），所以中间必须真的有一次
        // 列表快照到达——真实链路里 pin 命令提交后 ConversationListSnapshot 会带回新状态。
        rowPins.Clear()
        sidebar.SetConversations [ summary id "第一个会话" (DateTimeOffset.Now.AddMinutes(-3.0)) true ]
        Dispatcher.UIThread.RunJobs()
        // 换 ItemsSource 后行容器重建：重新取那一行。
        let row = rowByName sidebar "第一个会话"
        row.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        pressRow row Key.P KeyModifiers.None
        Dispatcher.UIThread.RunJobs()
        Assert.Equal<Guid * bool>([ (id, false) ], Seq.toList rowPins)
    finally
        window.Close()

// P 不带修饰键才归侧栏：Ctrl+P 等组合键是全局快捷键表的领地，
// 行里吞掉会让全局键位出现不可解释的失效。
[<Fact>]
let ``ctrl p on a focused row is left for the global table`` () =
    let root, sidebar, rowPins = buildSidebar ()
    let id = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ summary id "第一个会话" (DateTimeOffset.Now.AddMinutes(-3.0)) false ]
        Dispatcher.UIThread.RunJobs()
        let row = rowByName sidebar "第一个会话"
        row.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        pressRow row Key.P KeyModifiers.Control
        Dispatcher.UIThread.RunJobs()
        Assert.Empty rowPins
    finally
        window.Close()

// ── 删除后的邻位：打开谁与焦点落谁同一个答案 ────────────────────────────────

// 删的正是正在看的会话时，落到邻行继续，不进空屏。邻位口径必须与
// FocusAfterDelete 一致，否则键盘用户与主区看到的是两个会话。
[<Fact>]
let ``neighbor after delete matches the row focus falls to`` () =
    let root, sidebar, _ = buildSidebar ()
    let first, second, third = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        // 时间戳显式错开：行序 = 一二三（updatedAt 降序），断言才有确定性。
        let now = DateTimeOffset.Now
        sidebar.SetConversations
            [ summary first "第一个会话" (now.AddMinutes(-3.0)) false
              summary second "第二个会话" (now.AddMinutes(-2.0)) false
              summary third "第三个会话" (now.AddMinutes(-1.0)) false ]
        Dispatcher.UIThread.RunJobs()
        // 行序由侧栏自己排（updatedAt 降序）：先读出真实顺序，
        // 断言邻位而不是断言「我认为的第几行」——组内顺序是实现细节。
        let order = sidebar.GetVisibleOrder() |> List.map Guid.Parse
        // 删中间那条：邻位是它下面那一行（下一行优先）。
        // 同一时间桶内按最近活动降序：最近用过的排前面 → 三二一。
        // 钉住这个前提：行序若变，说明分组/排序契约动了，邻位断言会全部失去意义。
        Assert.Equal<string list>([ third; second; first ] |> List.map string, order |> List.map string)
        // 删中间那条（second，visible 顺序里排正中）：邻位是它下面那一行（下一行优先）。
        let midIdx = 1
        Assert.Equal(Some order.[midIdx + 1], sidebar.NeighborAfterDelete(second.ToString()))
        // 删屏幕最下面那条（first，visible 顺序里排最后）：没有下一行，退回上一行。
        let lastVisible = order.[order.Length - 1]
        let expectedPrev = order.[order.Length - 2]
        Assert.Equal(Some expectedPrev, sidebar.NeighborAfterDelete(lastVisible.ToString()))
    finally
        window.Close()

// 只剩一条时没有邻位可言：必须给 None，让调用方退回欢迎页，
// 而不是随便挑一个不存在的会话。
[<Fact>]
let ``no neighbor when the list becomes empty`` () =
    let root, sidebar, _ = buildSidebar ()
    let only = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ summary only "唯一会话" (DateTimeOffset.Now.AddMinutes(-1.0)) false ]
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(None, sidebar.NeighborAfterDelete(only.ToString()))
    finally
        window.Close()

// ── 打开设置即有焦点归宿 ────────────────────────────────────────────────────

// 分区导航按钮的无障碍名即分区标签（SettingsSection.label）。
let private navSectionNames =
    Set.ofList [ "\u670d\u52a1\u5546"; "\u5de5\u5177\u4e0e MCP"; "\u751f\u6210"; "\u5916\u89c2"; "\u5173\u4e8e" ]

let private isNavFocused (control: Control) =
    not (isNull control) && navSectionNames.Contains(AutomationProperties.GetName control)

let private focusedControl (window: Window) =
    let manager = window.FocusManager
    if isNull manager then null
    else
        let focused = manager.GetFocusedElement()
        if isNull focused then null else focused :?> Control

// 打开设置后焦点落在分区导航按钮上：此前 ShowSettings 只切可见性，
// 焦点悬在已隐藏的 workspace 里，键盘用户按 Tab 前无处可去。
[<Fact>]
let ``focus initial lands on a settings section nav button`` () =
    let overlayRoot = Grid()
    let overlayHost = OverlayHost(overlayRoot)
    let actions: SettingsActions =
        { upsertProvider = fun _ cb -> cb true
          deleteProvider = ignore
          probeProvider = ignore
          upsertMcp = fun _ cb -> cb true
          deleteMcp = ignore
          updateGeneration = fun _ cb -> cb true
          savePrefs = ignore
          toast = fun _ _ -> () }
    let settings = SettingsView(overlayHost, actions, ignore, ignore)
    settings.Build()
    overlayRoot.Children.Add(settings :> Control)
    let window = show overlayRoot 900.0 700.0
    try
        // 先把焦点放到别处：初始焦点必须能「把焦点搬过来」，
        // 空焦点管理器下任何断言都会假阳性。
        let other =
            descendants settings
            |> Seq.tryFind (fun control -> control.Focusable && control <> settings)
            |> Option.defaultValue (settings :> Control)
        other.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.False(isNavFocused (focusedControl window), "\u524d\u7f6e\u6761\u4ef6\uff1a\u7126\u70b9\u4e0d\u5728\u5bfc\u822a\u5e26\u4e0a")
        settings.FocusInitial()
        Dispatcher.UIThread.RunJobs()
        Assert.True(isNavFocused (focusedControl window), "\u6253\u5f00\u8bbe\u7f6e\u540e\u7126\u70b9\u5e94\u843d\u5728\u5206\u533a\u5bfc\u822a\u6309\u94ae\u4e0a")
    finally
        window.Close()

// PageUp/PageDown 在侧栏行里与 Home/End 同义。行是定制 Border（容器 ListBoxItem
// 一律 Focusable=false），ListBox 自带的翻页导航对定制行不生效：未接线时按键从
// 默认路径穿过——探针实测 puHandled=true 而焦点原地不动（已消费，无位移），
// 键盘用户在侧栏里按翻页键毫无反应。Menu.fs:136-139 早已把 PageUp/PageDown 与
// Home/End 视作同义，侧栏补齐同口径。
// 落点断言走「ScrollIntoView 把目标行拉进虚拟化窗口」这条同步可见线索
// （UiStabilityTests 同手法）：一个 20k 会话的列表里，焦点在末行时按 Home/PageUp
// 必须把首行实现出来；未接线时行永远是末行附近那一窗。
[<Fact>]
let ``page up on a sidebar row scrolls the list to the first item`` () =
    let root, sidebar, _ = buildSidebar ()
    let window = show root 360.0 700.0
    try
        let count = 400
        let items =
            [ for index in 1 .. count ->
                summary (Guid.NewGuid()) (sprintf "会话 %05d" index) DateTimeOffset.Now false
                |> fun s -> { s with lastCommitId = uint64 index } ]
        sidebar.SetConversations items
        Dispatcher.UIThread.RunJobs()
        let list = byAutomationName sidebar "会话列表" :?> ListBox
        let realized () =
            list.GetRealizedContainers()
            |> Seq.collect (fun c -> c.GetVisualDescendants())
            |> Seq.choose (function :? Control as x -> Some x | _ -> None)
            |> Seq.map (fun x -> AutomationProperties.GetName x)
            |> Set.ofSeq
        let rec awaitRealized remaining name =
            Dispatcher.UIThread.RunJobs()
            let n = list.GetRealizedContainers() |> Seq.length
            if Set.contains name (realized ()) then name
            elif remaining > 0 then Thread.Sleep 10; awaitRealized (remaining - 1) name
            else failwithf "%s was not realized; realizedContainers=%d itemCount=%d names=%s"
                    name n list.ItemCount (String.Join("|", realized () |> Seq.truncate 5))
        // 先等首屏容器实现（UiStabilityTests 同手法），再滚到底：未挂载的
        // ItemsControl 上 ScrollIntoView 是空操作。
        awaitRealized 20 (sprintf "会话 %05d" count) |> ignore
        list.ScrollIntoView(list.ItemCount - 1)
        let bottomName = sprintf "会话 %05d" 1
        awaitRealized 20 bottomName |> ignore
        let lastRow =
            list.GetRealizedContainers()
            |> Seq.collect (fun c -> c.GetVisualDescendants())
            |> Seq.choose (function :? Control as x -> Some x | _ -> None)
            |> Seq.find (fun x -> AutomationProperties.GetName x = bottomName)
        Assert.True(Set.contains bottomName (realized ()), "前置条件：末行已在虚拟化窗口内")
        // PageUp：与 Home 同分支，必须把首行拉进虚拟化窗口（ScrollIntoView 0）。
        let pu = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageUp, KeyModifiers = KeyModifiers.None, Source = lastRow)
        lastRow.RaiseEvent pu
        Assert.True(pu.Handled, "PageUp 必须与 Home 同口径被行内消费")
        awaitRealized 20 (sprintf "会话 %05d" count) |> ignore
        // 变异不变量：未接线时按键从默认路径穿过（Handled=true 但无位移），
        // 首行永远实现不出来。
    finally
        window.Close()

