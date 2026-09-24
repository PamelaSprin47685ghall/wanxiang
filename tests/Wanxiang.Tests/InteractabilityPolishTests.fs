module Wanxiang.Tests.InteractabilityPolishTests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open System.Reflection
open Xunit
open System.Text.Json.Nodes

open Wanxiang.Core
open Wanxiang.UI

/// 第七轮收敛测试：键盘节律与多选模式下的单条入口收敛。
///
/// 1. 滚轮不论方向都该打断平滑滚动（上滚原本会打断，下滚漏了）；
/// 2. 服务商编辑表单的单行字段 Enter 被表单提交路径消费；
/// 3. 侧栏多选模式下，行右键不再开单条菜单，行内「更多」按钮整体下线；
/// 4. 多选模式下焦点落在搜索框时，Escape 退出多选而不是空转到「聚焦当前会话」。

let private toasts = ResizeArray<string * ToastTone>()

/// Ui 模块顶层建有 Cursor：触碰任何 Ui.* 成员前先确保无头平台就绪。
let private ensureHeadless () = Headless.ensure ()

let private pending = ResizeArray<JsonObject>()

let private settingsActions () : SettingsActions =
    { upsertProvider = fun body completed -> pending.Add body; completed true
      deleteProvider = ignore
      probeProvider = ignore
      upsertMcp = fun _ completed -> completed true
      deleteMcp = ignore
      updateGeneration = fun _ completed -> completed true
      savePrefs = ignore
      toast = fun message tone -> toasts.Add((message, tone)) }

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

let private byName (root: Control) (name: string) =
    descendants root
    |> Seq.tryFind (fun control -> AutomationProperties.GetName control = name)

let private byPlaceholder (root: Control) (placeholder: string) =
    descendants root
    |> Seq.tryFind (function
        | :? TextBox as tb -> (not (isNull tb.PlaceholderText)) && tb.PlaceholderText = placeholder
        | _ -> false)

/// 虚拟化 ListBox 的行只在挂树后生成容器：descendants 找不到，必须走已实现容器。
let rec private visualControls (visual: Visual) =
    seq {
        match visual with
        | :? Control as control -> yield control
        | _ -> ()
        for child in visual.GetVisualChildren() do
            yield! visualControls child
    }

let private rows (sidebar: Control) =
    match descendants sidebar |> Seq.tryPick (function :? ListBox as lb -> Some lb | _ -> None) with
    | None -> []
    | Some list -> list.GetRealizedContainers() |> Seq.cast<Control> |> Seq.collect (fun c -> visualControls (c :> Visual)) |> Seq.toList

/// 行容器即 Sidebar 给的那张 Border：自动化名 = 标题，Tag = ConversationSummary。
let private rowByName (sidebar: Control) (title: string) =
    rows sidebar
    |> Seq.tryFind (fun control ->
        AutomationProperties.GetName control = title
        && match control with
           | :? Border as border ->
               match border.Tag with
               | :? ConversationSummary -> true
               | _ -> false
           | _ -> false)
    |> Option.defaultWith (fun () -> failwith (sprintf "行 %s 缺失" title))

let private privateKey (ctrl: Control) (key: Key) =
    let e =
        KeyEventArgs(
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.None,
            Source = ctrl)
    ctrl.RaiseEvent(e)
    e

let private namedControl (chat: ChatView) (name: string) : Control =
    chat.GetVisualDescendants()
    |> Seq.choose (function :? Control as c -> Some c | _ -> None)
    |> Seq.find (fun c -> AutomationProperties.GetName c = name)

/// 以 ChatView 的 scroller 为目标合成一次滚轮脉冲；返回事件供调用方判 Handled。
let private raiseWheel (scroller: ScrollViewer) (delta: float) =
    let pointer = new Input.Pointer(1, PointerType.Mouse, true)
    let props = PointerPointProperties()
    let b = scroller.Bounds
    let e =
        PointerWheelEventArgs(
            scroller,
            pointer,
            scroller,
            Point(b.Width / 2.0, b.Height / 2.0),
            0UL,
            props,
            KeyModifiers.None,
            Vector(0.0, delta))
    e.RoutedEvent <- InputElement.PointerWheelChangedEvent
    e.Source <- scroller
    scroller.RaiseEvent(e)
    e

let private show (content: Control) width height =
    Headless.ensure ()
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

// ── 1. 滚轮打断平滑滚动 ────────────────────────────────────────────────────

/// ChatView 夹具：与 ExchangeNavTests 同源的最小构造（Round7 复用它自己的交互路径）。
let private buildChat (messages: MessageView list) =
    Headless.ensure ()
    // 平滑滚动路径在减弱动效下退化成立即跳转：这里要测动画中断，显式开启动效。
    MotionPolicy.setReduced false
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

let private chatParts (chat: ChatView) =
    let layout = Assert.IsAssignableFrom<DockPanel>(chat.Child)
    let body = Assert.IsAssignableFrom<Grid>(layout.Children.[1])
    let scroller = Assert.IsAssignableFrom<ScrollViewer>(body.Children.[0])
    scroller

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

[<Fact>]
let ``wheel moving either way stops an in-flight smooth scroll`` () =
    // 「上一条提问」是一场有帧节拍的平滑滚动（smoothScrollTimer）。
    // 中断判据此前只认上滚（Delta.Y > 0）：上滚途中改主意往下滚，
    // 两个动画争抢 Offset，手势输给动画。
    let chat, window = buildChat (exchanges 3)
    try
        let scroller = chatParts chat
        // 起点：顶部附近，让「下一条提问」有一段可滚的距离。
        scroller.Offset <- Vector(0.0, 0.0)
        Dispatcher.UIThread.RunJobs()
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height, "前置条件：内容高过视口")
        // 记录这一路径上的平滑滚动计时器：这是动画是否存活的直接证据，
        // 比观察 offset 像素更硬（offset 停在目标值时两种路径看起来一样）。
        let smoothField =
            typeof<ChatView>.GetField("smoothScrollTimer", Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic)
        Assert.True(not (isNull smoothField), "ChatView 应有 smoothScrollTimer 字段")
        // 从顶部往下跳：距离足够远，必然进入平滑滚动路径（近距离是立即落位）。
        let next = namedControl chat "下一条提问"
        Assert.True(next.IsEnabled, "前置条件：顶部时下一条可用")
        privateKey next Key.Enter |> ignore
        Dispatcher.UIThread.RunJobs()
        let running () : bool =
            not (isNull (smoothField.GetValue chat))
        Assert.True(running (), "前置条件：跳转触发了平滑滚动动画")
        // 下滚（Delta.Y < 0）：用户意图，必须打断动画（此前只认上滚）。
        let down = raiseWheel scroller -120.0
        Dispatcher.UIThread.RunJobs()
        Assert.False(running (), "下滚必须打断平滑滚动，动画不能与手势争抢 Offset")
        // 再补一次上滚：换「上一条」重新起一段动画，两侧都不得让动画死灰复燃。
        // 回顶部再按：锚点已是第 2 轮，目标第 1 轮离当前视口足够远，必走平滑路径
        // （近距离分支 abs(offset-target)<5 会立即落位，那不是动画被停，是没起播）。
        scroller.Offset <- Vector(0.0, scroller.Extent.Height - scroller.Viewport.Height)
        Dispatcher.UIThread.RunJobs()
        let prev = namedControl chat "上一条提问"
        Assert.True(prev.IsEnabled, "前置条件：锚点已前移，上一条可用")
        privateKey prev Key.Enter |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True(running (), "前置条件：再次发起跳转")
        let up = raiseWheel scroller 120.0
        Dispatcher.UIThread.RunJobs()
        Assert.False(running (), "上滚同样打断平滑滚动")
    finally
        window.Close()

// ── 2. 服务商表单：单行字段 Enter 被消费 ───────────────────────────────────

[<Fact>]
let ``provider form consumes enter from a single line field`` () =
    ensureHeadless ()
    let root = Grid()
    let overlay = OverlayHost(root)
    let providers = SettingsProviders(overlay, settingsActions ())
    providers.SetCatalog
        { Catalog.empty with
            providers =
                [ { id = "openai"
                    label = "OpenAI"
                    kind = "openai"
                    baseUrl = "https://api.openai.com/v1"
                    hasApiKey = false
                    models = []
                    defaultModel = ""
                    timeoutSeconds = 120
                    maxRetries = 2
                    enabled = true } ] }
    let built = providers.Build()
    root.Children.Add built
    let window = show root 520.0 640.0
    try
        let addButton =
            byName built "添加服务商"
            |> Option.defaultWith (fun () -> failwith "服务商区缺少添加入口")
        let addEvent = privateKey addButton Key.Enter
        Assert.True(addEvent.Handled, "前置条件：Enter 能打开编辑对话框")
        Dispatcher.UIThread.RunJobs()
        Assert.True(overlay.IsDialogOpen, "前置条件：编辑对话框已打开")
        // 对话框画在 overlay 上，不在 built 子树里：去整棵 overlay 根下找各字段。
        let field placeholder =
            byPlaceholder root placeholder
            |> Option.defaultWith (fun () -> failwith (sprintf "编辑器缺少字段 %s" placeholder))
            :?> TextBox
        let idBox = field "例如 openai"
        idBox.Text <- "round7"
        let labelBox = field "在界面上怎么称呼它"
        let urlBox = field "https://api.openai.com/v1"
        let modelsBox = field "每行一个模型名"
        // 预设把默认模型预填成 gpt-5，不在本测试的模型列表里：清掉让它自动取第一个。
        (field "留空则自动使用列表中第一个").Text <- ""
        labelBox.Text <- "Round7"
        urlBox.Text <- "https://round7.example.com/v1"
        modelsBox.Text <- "m-1"
        let e = privateKey idBox Key.Enter
        // Enter 必须被表单的提交路径消费并走通保存：此前这里只有 Ctrl+Enter 一条路，
        // 单行字段按 Enter 会把键事件漏给对话框层或干脆没人接。
        Assert.True(e.Handled, "单行字段 Enter 必须被表单提交路径消费")
        Dispatcher.UIThread.RunJobs()
        // 保存动作被调用、对话框关闭：Enter 与保存键同一条提交路径。
        Assert.Equal(1, pending.Count)
        Assert.False(overlay.IsDialogOpen, "字段齐备后 Enter 提交并关闭对话框")
    finally
        window.Close()

// ── 3/4. 侧栏多选模式 ──────────────────────────────────────────────────────

let private sidebarSummary id title =
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

let private buildSidebarForPolish () =
    Headless.ensure ()
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

[<Fact>]
let ``row context menu stays closed while the sidebar is in selection mode`` () =
    let root, overlay, sidebar = buildSidebarForPolish ()
    let id = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ sidebarSummary id "Alpha" ]
        Dispatcher.UIThread.RunJobs()
        let moreButton =
            rows sidebar
            |> Seq.tryFind (fun control -> AutomationProperties.GetName control = "会话“Alpha”的操作菜单")
            |> Option.defaultWith (fun () -> failwith "侧栏行缺少更多操作按钮")
        // 前置条件：非多选态下 Enter 打开单条菜单，证明探针有效。
        privateKey moreButton Key.Enter |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True(overlay.IsPopupOpen, "前置条件：非多选态下更多按钮会打开单条菜单")
        // 多选态下同一个入口必须拒绝：openMenu 首行就是 selectionMode 守卫。
        sidebar.EnterSelection id
        Dispatcher.UIThread.RunJobs()
        overlay.ClosePopup()
        Dispatcher.UIThread.RunJobs()
        Assert.False(overlay.IsPopupOpen, "前置条件：浮层已显式关闭")
        // 按钮已被 RefreshSelectionChrome 下线（IsHitTestVisible = false），
        // 但事件仍可合成——守卫必须挡在事件路径上，而不是只靠可见性。
        privateKey moreButton Key.Enter |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.False(overlay.IsPopupOpen, "多选态下更多按钮不得打开单条菜单")
    finally
        window.Close()

[<Fact>]
let ``row more button leaves the hit tree in selection mode`` () =
    let root, _, sidebar = buildSidebarForPolish ()
    let id = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ sidebarSummary id "Alpha" ]
        Dispatcher.UIThread.RunJobs()
        let moreButton =
            rows sidebar
            |> Seq.tryFind (fun control -> AutomationProperties.GetName control = "会话“Alpha”的操作菜单")
            |> Option.defaultWith (fun () -> failwith (sprintf "行缺少更多操作按钮；已实现控件名=%A" (rows sidebar |> Seq.map (fun c -> AutomationProperties.GetName c) |> List.ofSeq)))
        Assert.True(moreButton.IsHitTestVisible, "前置条件：非多选态下更多按钮可命中")
        sidebar.EnterSelection id
        Dispatcher.UIThread.RunJobs()
        Assert.False(moreButton.IsVisible, "多选态下更多操作按钮应整体下线")
        Assert.False(moreButton.IsHitTestVisible, "下线即不可命中：不再诱导点击空转菜单")
    finally
        window.Close()

[<Fact>]
let ``escape in the search box leaves selection mode first`` () =
    let root, _, sidebar = buildSidebarForPolish ()
    let id = Guid.NewGuid()
    let window = show root 320.0 420.0
    try
        sidebar.SetConversations [ sidebarSummary id "Alpha" ]
        Dispatcher.UIThread.RunJobs()
        let row = rowByName sidebar "Alpha"
        row.Focus() |> ignore
        sidebar.EnterSelection id
        Dispatcher.UIThread.RunJobs()
        // 从列表首行按 Up 会落进搜索框：此时焦点确实可能停在搜索上。
        let searchBox =
            byPlaceholder root "搜索会话"
            |> Option.defaultWith (fun () -> failwith "侧栏缺少搜索框")
        searchBox.Focus() |> ignore
        Assert.True(sidebar.IsSelectionMode, "前置条件：仍在多选模式")
        privateKey searchBox Key.Escape |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.False(sidebar.IsSelectionMode, "搜索框中的 Escape 应先退出多选")
    finally
        window.Close()
