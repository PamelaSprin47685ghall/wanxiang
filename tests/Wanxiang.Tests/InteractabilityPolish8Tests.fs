module Wanxiang.Tests.InteractabilityPolish8Tests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit

open Wanxiang.Core
open Wanxiang.UI

/// Round8 收敛补缺的可观察契约（焦点环部分并入 FocusRingTests）。
///
/// 三条主线：
/// - 空态 Down：列表真空且非搜索过滤时，搜索框按 Down 所有分支落空，
///   键盘用户被困在搜索框里（ListScout8 候选 3，kelivo side_drawer.dart:580-610
///   空态焦点流向下落至主按钮同语义）；
/// - 多选批量操作条：置顶 / 归档 / 删除左右方向键此前无路由，与其余对话框
///   页脚的 wireArrowNavigation（Dialogs.fs:48-57）节拍割裂（ListScout8 候选 2）；
/// - 问答锚点：当前轮此前取视口底端最后一张用户卡，大屏露出数轮时按「下一条」
///   一次跳过两三轮；改取视口顶端第一张（kelivo scroll_controller.dart:805-817
///   `_firstVisibleMessageBelowTopOverlay` 同向），端点禁用显式叠 atBottom 短路。

let private toasts = ResizeArray<string * ToastTone>()

/// Ui 模块顶层建有 Cursor：触碰任何 Ui.* 成员前先确保无头平台就绪。
let private ensureHeadless () = Headless.ensure ()

let rec private descendants (control: Control) = seq {
    match control with
    | :? Panel as panel -> yield! panel.Children |> Seq.collect descendants
    | :? Decorator as decorator ->
        match decorator.Child with
        | null -> ()
        | child -> yield! descendants child
    | :? ContentControl as host ->
        match host.Content with
        | :? Control as child -> yield! descendants child
        | _ -> ()
    | _ -> ()
    yield control
}

let private byNameOrFail (root: Control) (name: string) : Control =
    let matches =
        descendants root
        |> Seq.filter (fun control -> AutomationProperties.GetName control = name)
        |> Seq.toList
    match matches with
    | [ one ] -> one
    | many -> failwith (sprintf "名称「%s」匹配到 %d 个控件，预期唯一" name many.Length)

let private privateKey (ctrl: Control) (key: Key) =
    let e =
        KeyEventArgs(
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.None,
            Source = ctrl)
    ctrl.RaiseEvent(e)
    e

let private show (content: Control) width height =
    ensureHeadless ()
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

let private sidebarSummary (id: Guid) title =
    { id = id
      title = title
      preview = "preview"
      running = false
      pinned = false
      archived = false
      createdAt = DateTimeOffset.Now
      updatedAt = DateTimeOffset.Now
      messageCount = 2
      isFork = false
      providerId = "openai"
      model = "gpt"
      lastCommitId = 1UL }

let private buildSidebar () =
    ensureHeadless ()
    let root = Grid()
    let overlay = OverlayHost(root)
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
    let sidebar = Sidebar(overlay, actions, fun _ -> Border() :> Control)
    sidebar.Build()
    root.Children.Add sidebar
    root, overlay, sidebar

// ── 1. 空态：搜索框 Down 落进空态主按钮 ───────────────────────────────────

[<Fact>]
let ``down in the search box reaches the empty state action`` () =
    let root, _, sidebar = buildSidebar ()
    let window = show root 320.0 420.0
    try
        // 零会话：走 NoConversations 空态（connected 且不在加载），主按钮「新建会话」。
        sidebar.SetConnection(true, "已连接")
        sidebar.SetConversationListLoading false
        sidebar.SetConversations []
        Dispatcher.UIThread.RunJobs()
        let searchBox =
            descendants root
            |> Seq.tryPick (function
                | :? TextBox as tb when not (isNull tb.PlaceholderText) && tb.PlaceholderText = "搜索会话" -> Some tb
                | _ -> None)
            |> Option.defaultWith (fun () -> failwith "侧栏缺少搜索框")
        let actionButton = byNameOrFail root "新建会话"
        searchBox.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        let e = privateKey searchBox Key.Down
        Dispatcher.UIThread.RunJobs()
        Assert.True(e.Handled, "空态下 Down 必须被侧栏消费")
        Assert.True(actionButton.IsFocused, "焦点应落进空态主按钮（新建会话）")
    finally
        window.Close()

// ── 2. 多选批量操作条左右键 ───────────────────────────────────────────────

[<Fact>]
let ``selection action bar arrows walk the batch buttons`` () =
    let root, _, sidebar = buildSidebar ()
    let window = show root 320.0 420.0
    try
        let a = Guid.NewGuid()
        let b = Guid.NewGuid()
        sidebar.SetConversations [ sidebarSummary a "A"; sidebarSummary b "B" ]
        Dispatcher.UIThread.RunJobs()
        sidebar.EnterSelection a
        Dispatcher.UIThread.RunJobs()
        let pin = byNameOrFail root "置顶"
        let archive = byNameOrFail root "归档"
        let delete = byNameOrFail root "删除"
        Assert.True(pin.IsEnabled, "前置条件：置顶可用")
        pin.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        let forward = privateKey pin Key.Right
        Dispatcher.UIThread.RunJobs()
        Assert.True(forward.Handled, "多选操作条右方向键应被消费")
        Assert.True(archive.IsFocused, "置顶按右应落到归档")
        let again = privateKey archive Key.Right
        Dispatcher.UIThread.RunJobs()
        Assert.True(again.Handled, "归档按右应继续走到删除")
        Assert.True(delete.IsFocused, "最右端是删除键")
        // 反向：删除按 Left 回到归档，左右键闭环（不是单向陷阱）。
        let back = privateKey delete Key.Left
        Dispatcher.UIThread.RunJobs()
        Assert.True(back.Handled, "删除按左应回到归档")
        Assert.True(archive.IsFocused, "左方向键把焦点交回归档")
    finally
        window.Close()

// ── 3. 问答锚点取视口顶端 ─────────────────────────────────────────────────

let private buildChat (messages: MessageView list) =
    ensureHeadless ()
    // 平滑滚动在减弱动效下退化成立即跳转：锚点测试只看目标卡片落点，
    // 动画会增加中间态不确定性，直接走减弱档。
    MotionPolicy.setReduced true
    let chatActions =
        { renameTitle = ignore
          openSessionSettings = ignore
          forkFromHere = ignore
          stopGeneration = ignore
          requestOlderHistory = ignore
          retryLast = ignore
          toggleSidebar = ignore
          message =
            { copyText = ignore
              regenerate = ignore
              editAndFork = ignore
              deleteMessage = ignore
              downloadAttachment = ignore
              openLink = ignore } }
    let chat = ChatView(chatActions, Brand.logo)
    chat.Build()
    let window = Window(Width = 900.0, Height = 700.0, Content = chat)
    window.Show()
    chat.SetConversationChrome true
    chat.RenderMessages(messages, None, None, Tokens.fontReading, true, None, Set.empty)
    Dispatcher.UIThread.RunJobs()
    chat, window

let private chatPanel (chat: ChatView) =
    let layout = Assert.IsAssignableFrom<DockPanel>(chat.Child)
    let body = Assert.IsAssignableFrom<Grid>(layout.Children.[1])
    let scroller = Assert.IsAssignableFrom<ScrollViewer>(body.Children.[0])
    Assert.IsAssignableFrom<StackPanel>(scroller.Content), scroller

let private exchanges (count: int) : MessageView list =
    [ for round in 1 .. count do
        yield { MessageView.empty with
                 role = "user"
                 text = sprintf "第 %d 轮提问：%s" round (String.replicate 6 "填充内容以产生滚动高度。")
                 commitId = Some(uint64 (round * 2 - 1)) }
        yield { MessageView.empty with
                 role = "assistant"
                 text = sprintf "第 %d 轮回答：%s" round (String.replicate 12 "回复内容填充，需要足够高度才能形成滚动范围。")
                 commitId = Some(uint64 (round * 2)) } ]

let private visualDescendants (root: Control) =
    root.GetVisualDescendants()
    |> Seq.choose (function :? Control as c -> Some c | _ -> None)

/// 第 N 张（0 基）用户卡：host 是消息卡根的直接子节点，但 host 自身可能
/// 还嵌在分组容器里，所以走面板后代而不是 panel.Children。
/// 第 N 张（0 基）用户卡的正文里是否含这段文本：消息卡把 text 渲成若干
/// TextBlock，任一片段命中即算（与 ExchangeNavTests.cardWith 同源判据）。
let private userCardContains (card: Control) (needle: string) =
    card.GetVisualDescendants()
    |> Seq.choose (function :? TextBlock as t -> Some t.Text | _ -> None)
    |> Seq.exists (fun text -> not (isNull text) && text.Contains needle)

/// 第 N 张用户卡 = 面板 Children 里第 N 个正文含「第 N+1 轮提问」的子控件。
/// 不用自动化名：bubble 之外的 MessageCard 根才是挂上面板的那个节点。
let private userCardAt (chat: ChatView) (round: int) : Control =
    let panel = chatPanel chat |> fst
    let needle = sprintf "第 %d 轮提问" round
    panel.Children
    |> Seq.cast<Control>
    |> Seq.tryFind (fun card -> userCardContains card needle)
    |> Option.defaultWith (fun () -> failwith (sprintf "面板里找不到第 %d 轮提问卡" round))

[<Fact>]
let ``next question anchors on the topmost visible card`` () =
    let chat, window = buildChat (exchanges 3)
    try
        let panel, scroller = chatPanel chat
        panel |> ignore
        // 停在顶部：视口顶端第一张用户卡是第 1 轮，底端还压着第 2/3 轮的结尾。
        scroller.Offset <- Vector(0.0, 0.0)
        Dispatcher.UIThread.RunJobs()
        let next = byNameOrFail chat "下一条提问"
        Assert.True(next.IsEnabled, "前置条件：顶部时下一条可用")
        privateKey next Key.Enter |> ignore
        Dispatcher.UIThread.RunJobs()
        // 锚点语义：第一跳后第 2 轮用户卡必须已进入视口顶部
        // （smoothScrollToCard 的目标 = 卡片 Y - Tokens.space5，落在顶部带一点间距）。
        // 旧锚点（视口底端那张）会让这一跳直接从第 1 轮跨到第 3 轮，
        // 断言 offset ≈ 第 2 轮卡片顶部正好钉住这个差别。
        let second = userCardAt chat 2
        let third = userCardAt chat 3
        Assert.True(
            scroller.Offset.Y <= second.Bounds.Y + 2.0,
            sprintf "第一跳应落在第 2 轮用户卡（offset=%f cardTop=%f）" scroller.Offset.Y second.Bounds.Y)
        Assert.True(
            second.Bounds.Y > third.Bounds.Y || third.Bounds.Y > scroller.Offset.Y,
            sprintf "第 2 轮不得晚于第 3 轮（second=%f third=%f）" second.Bounds.Y third.Bounds.Y)
    finally
        window.Close()
