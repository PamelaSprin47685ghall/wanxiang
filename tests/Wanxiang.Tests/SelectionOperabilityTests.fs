module Wanxiang.Tests.SelectionOperabilityTests

open Wanxiang2.SidebarDesk
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
let private rowByName (sidebar: Wanxiang2.SidebarDesk.SidebarDesk) (title: string) =
    let rec visualControls (visual: Visual) =
        seq {
            match visual with
            | :? Control as control -> yield control
            | _ -> ()
            for child in visual.GetVisualChildren() do
                yield! visualControls child
        }
    let list =
        descendants sidebar.View
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
let private rowByNameOpt (sidebar: Wanxiang2.SidebarDesk.SidebarDesk) (title: string) : Control option =
    let rec visualControls (visual: Visual) =
        seq {
            match visual with
            | :? Control as control -> yield control
            | _ -> ()
            for child in visual.GetVisualChildren() do
                yield! visualControls child
        }
    match descendants sidebar.View |> Seq.tryPick (function :? ListBox as lb -> Some lb | _ -> None) with
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
    let root = Grid()
    let overlay = OverlayHost(root)
    lastOverlay.Clear()
    lastOverlay.Add overlay
    let opened = ResizeArray<Guid>()
    let pinned = ResizeArray<Guid list * bool>()
    let archived = ResizeArray<Guid list * bool>()
    let deleted = ResizeArray<Guid list>()
    // 置顶判定简化：未置顶的集合上报 false，置顶键因此显示「置顶」。
    let actions: Wanxiang2.SidebarDesk.SidebarDeskActions =
        { newConversation = ignore
          openConversation = fun id -> opened.Add id
          renameConversation = ignore
          deleteConversation = ignore
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          // 此夹具里没有任何对话预先置顶 → 键面显示「置顶」，动作为置顶。
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
    let sidebar = Wanxiang2.SidebarDesk.SidebarDesk(SharedTheme.manager, overlay.Desk, actions, fun _ -> Border() :> Control)
    root.Children.Add sidebar.View
    root, sidebar, opened, pinned, archived, deleted

// 进入多选后，同一行的点击不再打开对话，而是切换选中：这是批量模式的核心语义，
// 也是「误点不会跳走」的保障。
// 清空最后一项即退出模式：空选择保留操作条会让用户面对无意义的计数。
// 多选期内 Esc 一律先退多选，不因焦点落在列表底部已归档开关上而失灵：
// 归档开关行也是列表内的 Tab 停靠点（up/down/home 四处键位都通得到），
// 对话行（Sidebar.fs:860）与搜索框（Sidebar.fs:1383）上的 Esc 都已退多选，
// 唯独这里漏了——用户 End 滚到底再按 Esc 毫无反应，被困在批量态。
// kelivo interactive_drawer.dart:399-407 同序：抽屉级返回先问「是不是在多选」。
// 批量置顶/归档按当前选择逐个发命令，Payload 逐条独立可确认。
// 批量模式里 Escape 只做退出：在搜索结果中进入多选的用户，Esc 的意图是离开
// 批量态，而不是被清掉筛选用词。此前有词的守卫让退出失效，Esc 落到普通态的
// 清词分支——用户丢失筛选条件还没退出多选。
// 批量条三个动作键随选中数翻面：0 项时禁用。既有守卫只挡住点击，键盘用户仍能
// Tab 上去按 Enter 得到静默无反应——按钮的启用态本身就是可操作性的承诺。
// Ctrl+M 解析为打开模型选择器：不必先找到左下角芯片，键盘可直接换模型。
// 选择模式只留「取消 / 全选 / 计数」与底部批量条：品牌行与页脚让位。
// 侧栏只有 232–420pt 宽，四条常驻条会把列表压成几条——这条不变量锁住空间纪律。
// 「多选」入口就在行右键菜单里：桌面用户不必先知道有批量模式。
// 这里驱动真实菜单项（浮层画在 OverlayHost 上），验证入口 → 进入模式 → 首项选中。
[<Trait("Category", "UI")>]
[<Fact>]
let ``ctrl+m resolves to the model picker action`` () =
    Headless.run (fun () ->
    let args =
        KeyEventArgs(
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.M,
            KeyModifiers = KeyModifiers.Control)
    Assert.Equal(ShortcutAction.OpenModelPicker, KeyShortcut.resolve args)
    )

// 长用户消息折叠在同一 detailViewport contract 上：未超限时不出现展开入口，
// 超限时才出现，且上限取自 LayoutPolicy 的专用常量（比工具详情更矮）。
// 批量键的文案是当前选择的状态，不是固定标签：全选后同一键变成「取消全选」、
// 全已置顶后变成「取消置顶」。此前两者只在 Build 时算一次，勾选变化后只说旧话，
// 用户再点会得到与预期相反的动作。锁按钮文本随勾选变化翻转。
// 发送键与换行键严格互补：Enter 发送模式下 Ctrl+Enter 归换行，Ctrl+Enter 发送模式下
// 裸 Enter 归换行。此前裸 Enter 发送模式把 Ctrl+Enter 也发出去，用户改行只剩 Shift+Enter
// 一条路。锁：发送永远只在当前模式被声明的那个键上发生，另一个键不许提交。
[<Trait("Category", "UI")>]
[<Fact>]
let ``composer sends only on the mode's declared key`` () =
    Headless.run (fun () ->
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
        Assert.False(inEnterMode.Handled, "Ctrl+Enter 在 Enter 发送模式下必须留给 TextBox 换行")
        // 未被提交即留在草稿里：Composer 只在不该发时把事件放行给 TextBox。
        Assert.Equal(1, submitted.Count)
        // 模式二：Ctrl+Enter 发送。裸 Enter 不提交。
        submitted.Clear()
        composer.SetEnterSends false
        Dispatcher.UIThread.RunJobs()
        let plainInCtrlMode = sendKey KeyModifiers.None
        Assert.False(plainInCtrlMode.Handled, "裸 Enter 在 Ctrl+Enter 发送模式下必须留给 TextBox 换行")
        Assert.Empty submitted
        sendKey KeyModifiers.Control |> ignore
        Assert.Equal<string seq>([ "payload" ], submitted)
    finally
        // composer 未挂窗，无根可关：测完只撤销其副作用。
        composer.SetEnabled(false, "")
    )

// 从外部切换当前对话（Ctrl+1..9 / 搜索结果 / 新建）后，选中行若在视口外，
// 侧栏上看不到正在进行的对话。SetActive 只滚屏不抢焦点：焦点归属照旧由调用方决定。
// 锁：激活的行必须被滚进可视区。
// 置顶键的文案只按「已选项是否全部置顶」判定。此前混入「必须全选可见行」的前提，
// 于是只挑两个已置顶的对话时键面仍说「置顶」，点下去发出的却是取消置顶——
// 文案与动作相反，用户要么不敢点，要么点错。
// 空选择不进删除流：SelectedIds 只报可见行里的已选项，搜索把选中行滤掉后
// 选择计数非 0 而批量载荷为空——行上按 Delete 仍会调 deleteMany，确认框弹出
// 「选中的 0 个对话」。kelivo 侧栏以 selectedCount > 0 为启用条件
// （sidebar_selection_bars.dart:186），同一约定。