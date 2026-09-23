namespace Wanxiang.Tests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.UI

/// 顶栏「上一条 / 下一条」问答跳转。
///
/// 借 kelivo scroll_nav_buttons 的 jump-to-question 语义（锚点跟随，连续按键一排排翻），
/// 但收敛成本项目既有的顶栏按钮组，不引入第二套悬浮按钮层。
/// 这里锁三条可观察契约：
/// - 锚点语义：第二次「上一条」翻到的是倒数第三轮，而不是重新按当前视口算一次；
/// - 端点禁用：翻到头不能再翻，按钮槽位保留（宽度不跳）；
/// - 手动滚动会刷新端点态（无锚点时锚点由视口决定）。
module ExchangeNavTests =

    let private approx (a: float) (b: float) = abs (a - b) < 0.5

    let private buildChat (messages: MessageView list) =
        Headless.ensure ()
        // 本文件只锁「跳到哪一轮」；动效路径由既有 smoothScroll 用例覆盖。
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

    let private named (chat: ChatView) (name: string) : Control =
        chat.GetVisualDescendants()
        |> Seq.choose (function :? Control as c -> Some c | _ -> None)
        |> Seq.find (fun c -> AutomationProperties.GetName(c) = name)

    /// round 轮问答，每轮 user + assistant；正文足够高以产生滚动。
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

    let private parts (chat: ChatView) =
        let layout = Assert.IsAssignableFrom<DockPanel>(chat.Child)
        let body = Assert.IsAssignableFrom<Grid>(layout.Children.[1])
        let scroller = Assert.IsAssignableFrom<ScrollViewer>(body.Children.[0])
        let panel = Assert.IsAssignableFrom<StackPanel>(scroller.Content)
        scroller, panel

    /// 卡片正文里是否含这段文字：消息卡把 text 渲成若干 TextBlock，任一片段命中即算。
    let private cardWith (panel: StackPanel) (needle: string) : Control =
        let contains (card: Control) =
            card.GetVisualDescendants()
            |> Seq.choose (function :? TextBlock as t -> Some t.Text | _ -> None)
            |> Seq.exists (fun text -> not (isNull text) && text.Contains needle)
        panel.Children
        |> Seq.cast<Control>
        |> Seq.find contains

    /// 点顶栏按钮。自动化 Invoke 走 Dispatcher.Post，先跑两轮把它排空。
    /// 动画路径由既有 smoothScroll 用例覆盖；这里关掉动效，让跳转落位瞬时可见，
    /// 断言的是「跳到哪一轮」而不是「动画播了多少」。
    let private invoke (chat: ChatView) (name: string) =
        let button = named chat name
        let peer = Automation.Peers.ControlAutomationPeer.CreatePeerForElement button
        Assert.IsAssignableFrom<Automation.Provider.IInvokeProvider>(peer).Invoke()
        Dispatcher.UIThread.RunJobs()
        Dispatcher.UIThread.RunJobs()

    /// 目标卡是否完整落在视口内。这是用户真正在乎的「跳过去」，
    /// 比断言 offset 像素值更稳（残留的 auto-follow 位移与之无关）。
    let private cardInViewport (chat: ChatView) (needle: string) =
        let scroller, panel = parts chat
        let card = cardWith panel needle
        let top = scroller.Offset.Y
        let bottom = top + scroller.Viewport.Height
        card.Bounds.Y >= top - 1.0 && card.Bounds.Y + card.Bounds.Height <= bottom + 1.0

    [<Fact>]
    let ``首屏贴底后下一条禁用、上一条可用`` () =
        let chat, window =
            buildChat (exchanges 3)
        try
            let scroller, _ = parts chat
            // 贴到真正的底部（首屏 follow 落在中段是有意的：末条助手消息刚入库）。
            scroller.Offset <- Vector(0.0, scroller.Extent.Height - scroller.Viewport.Height)
            Dispatcher.UIThread.RunJobs()
            let next = named chat "下一条提问"
            Assert.True(next.IsVisible, "下一条按钮应可见")
            Assert.False(next.IsEnabled, "已在最后一条时应禁用")
            Assert.True(approx next.Width Tokens.iconButton, "禁用态不得收缩宽度")
            Assert.Equal<string>("已经是最后一条提问", ToolTip.GetTip(next) :?> string)

            let prev = named chat "上一条提问"
            Assert.True(prev.IsVisible)
            Assert.True(prev.IsEnabled, "前面还有提问，上一条应可用")
        finally
            window.Close()

    [<Fact>]
    let ``手动滚回顶部后端点禁用互换`` () =
        let chat, window =
            buildChat (exchanges 3)
        try
            let scroller, _ = parts chat
            scroller.Offset <- Vector(0.0, 0.0)
            Dispatcher.UIThread.RunJobs()
            Dispatcher.UIThread.RunJobs()
            Assert.True(scroller.Offset.Y < 1.0, sprintf "应已到顶：offset=%f" scroller.Offset.Y)
            Assert.True((named chat "下一条提问").IsEnabled, "滚到顶部后下一条应可用")
            // 到顶时当前轮是第 2 轮（第一屏看得见的第二轮），所以「上一条」还有第 1 轮可去；
            // 再按一次跳到第 1 轮之后才真的没有更早的提问。
            invoke chat "上一条提问"
            Assert.False((named chat "上一条提问").IsEnabled, "跳到第 1 轮后不应再有更早的提问")
            Assert.True((named chat "下一条提问").IsEnabled, "第 1 轮之后还有下一轮")
        finally
            window.Close()

    [<Fact>]
    let ``上一条把上一轮提问完整带进视口`` () =
        let chat, window =
            buildChat (exchanges 8)
        try
            let scroller, _ = parts chat
            // 停在中段：既不是底部（避开自动跟随），也不是顶部（有得可跳）。
            scroller.Offset <- Vector(0.0, scroller.Extent.Height * 0.5)
            Dispatcher.UIThread.RunJobs()
            invoke chat "上一条提问"
            Dispatcher.UIThread.RunJobs()
            // 中点（约 50% 高度）所在的那一轮是第 6 轮，往前一格是第 5 轮：
            // 它的提问卡必须完整落进视口。
            Assert.True(
                cardInViewport chat "第 5 轮提问",
                sprintf "上一条后第 5 轮提问未完整可见：offset=%f" scroller.Offset.Y)
        finally
            window.Close()

    [<Fact>]
    let ``连续按上一条沿锚点顺序往回翻`` () =
        let chat, window =
            buildChat (exchanges 8)
        try
            let scroller, _ = parts chat
            scroller.Offset <- Vector(0.0, scroller.Extent.Height * 0.5)
            Dispatcher.UIThread.RunJobs()
            invoke chat "上一条提问"
            invoke chat "上一条提问"
            Dispatcher.UIThread.RunJobs()
            // 锚点语义：第 6 → 第 5 → 第 4。
            // 若每次都以当前视口重算，第二次会停在第一次的目标附近（第 5 轮），到不了第 4 轮。
            Assert.True(
                cardInViewport chat "第 4 轮提问",
                sprintf "第二次上一条应落在第 4 轮：offset=%f" scroller.Offset.Y)
        finally
            window.Close()

    [<Fact>]
    let ``只有用户消息算问答边界`` () =
        // 一屏全是助手消息时没有任何边界：两个按钮都该禁用。
        let chat, window =
            buildChat [ for i in 1 .. 6 -> { MessageView.empty with role = "assistant"; text = sprintf "回复 %d：%s" i (String.replicate 8 "填充。"); commitId = Some(uint64 i) } ]
        try
            Assert.False((named chat "上一条提问").IsEnabled)
            Assert.False((named chat "下一条提问").IsEnabled)
        finally
            window.Close()

    [<Fact>]
    let ``compact 档收起顶栏跳转按钮`` () =
        let chat, window =
            buildChat (exchanges 3)
        try
            let prev = named chat "上一条提问"
            let next = named chat "下一条提问"
            Assert.True(prev.IsVisible && next.IsVisible)
            chat.SetCompactMode true
            chat.SetConversationChrome true
            Dispatcher.UIThread.RunJobs()
            Assert.False(prev.IsVisible, "compact 档顶栏让位")
            Assert.False(next.IsVisible)
        finally
            window.Close()

    [<Fact>]
    let ``跳转清掉未读计数并放下 atBottom`` () =
        let chat, window =
            buildChat (exchanges 6)
        try
            let scroller, _ = parts chat
            // 停在中间：既有契约会为随后到达的新消息计未读。
            scroller.Offset <- Vector(0.0, scroller.Extent.Height * 0.4)
            Dispatcher.UIThread.RunJobs()
            chat.RenderMessages(
                exchanges 6
                @ [ for i in 13 .. 14 -> { MessageView.empty with role = "assistant"; text = sprintf "新 %d" i; commitId = Some(uint64 i) } ],
                None, None, Tokens.fontReading, true, None, Set.empty)
            Dispatcher.UIThread.RunJobs()

            let layout = Assert.IsAssignableFrom<DockPanel>(chat.Child)
            let body = Assert.IsAssignableFrom<Grid>(layout.Children.[1])
            let backToBottom = Assert.IsAssignableFrom<Border>(body.Children.[2])
            let label () =
                Assert.IsAssignableFrom<TextBlock>(Assert.IsAssignableFrom<StackPanel>(backToBottom.Child).Children.[1]).Text
            // 先确认未读真的计上了（否则下面「清零」什么也没证明）。
            Assert.Contains("新消息", label ())

            invoke chat "上一条提问"
            Dispatcher.UIThread.RunJobs()
            // 不在底部：跳转后「回到最新」应重新出现，且未读已清零（文案回到默认）。
            Assert.True(backToBottom.IsVisible, "跳转后应能一键回底部")
            Assert.Equal("回到最新", label ())
        finally
            window.Close()

    // ---------- 用量角标的缓存命中行 ----------

    [<Fact>]
    let ``cachedCount 只在有命中时给数`` () =
        // 0 与 None 一样不显示：命中 0 不是信息。
        Assert.Equal(None, GenerationUsage.cachedCount { GenerationUsage.empty with cachedTokens = None })
        Assert.Equal(None, GenerationUsage.cachedCount { GenerationUsage.empty with cachedTokens = Some 0 })
        Assert.Equal(Some 512, GenerationUsage.cachedCount { GenerationUsage.empty with cachedTokens = Some 512 })

    [<Fact>]
    let ``用量脚注把缓存命中显示出来`` () =
        Headless.ensure ()
        let message =
            { MessageView.empty with
                role = "assistant"
                text = "回复内容"
                commitId = Some 1UL
                committedAt = Some(DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero)) }
        let actions =
            { copyText = ignore
              regenerate = ignore
              editAndFork = ignore
              deleteMessage = ignore
              downloadAttachment = ignore
              openLink = ignore }
        let ctx =
            { fontSize = Tokens.fontReading
              autoCollapseReasoning = true
              streaming = false
              isLastAssistant = true
              usage = Some { GenerationUsage.empty with promptTokens = Some 1200; completionTokens = Some 380; cachedTokens = Some 900; totalTokens = Some 1580 }
              missingAttachments = Set.empty
              brandAvatar = fun () -> Brand.logo Tokens.logoAvatar }
        let card = MessageCard.render message ctx actions (Some 0)
        let window = Window(Width = 700.0, Height = 400.0, Content = card)
        window.Show()
        try
            Dispatcher.UIThread.RunJobs()
            let texts =
                card.GetVisualDescendants()
                |> Seq.choose (function :? TextBlock as t -> Some t.Text | _ -> None)
                |> Seq.filter (fun text -> not (isNull text))
                |> Seq.toList
            let footerText =
                card.GetVisualDescendants()
                |> Seq.choose (function :? TextBlock as t -> Some t | _ -> None)
                |> Seq.collect (fun t -> t.Inlines)
                |> Seq.choose (fun i ->
                    match i with
                    | :? Avalonia.Controls.Documents.Run as r -> Some r.Text
                    | _ -> None)
                |> String.concat ""
            Assert.Contains("缓存", footerText)
            Assert.Contains("900", footerText)
            // 脚注仍是同一行：缓存段没有另起一个 TextBlock。
            Assert.True(texts |> List.forall (fun text -> not (text.Contains "缓存")), "缓存不应另起一行")
        finally
            window.Close()

    [<Fact>]
    let ``没有缓存命用量脚注不提缓存`` () =
        Headless.ensure ()
        let message =
            { MessageView.empty with
                role = "assistant"
                text = "回复内容"
                commitId = Some 1UL }
        let actions =
            { copyText = ignore
              regenerate = ignore
              editAndFork = ignore
              deleteMessage = ignore
              downloadAttachment = ignore
              openLink = ignore }
        let ctx =
            { fontSize = Tokens.fontReading
              autoCollapseReasoning = true
              streaming = false
              isLastAssistant = true
              usage = Some { GenerationUsage.empty with promptTokens = Some 1200; completionTokens = Some 380 }
              missingAttachments = Set.empty
              brandAvatar = fun () -> Brand.logo Tokens.logoAvatar }
        let card = MessageCard.render message ctx actions (Some 0)
        let window = Window(Width = 700.0, Height = 400.0, Content = card)
        window.Show()
        try
            Dispatcher.UIThread.RunJobs()
            let footerText =
                card.GetVisualDescendants()
                |> Seq.choose (function :? TextBlock as t -> Some t | _ -> None)
                |> Seq.collect (fun t -> t.Inlines)
                |> Seq.choose (fun i ->
                    match i with
                    | :? Avalonia.Controls.Documents.Run as r -> Some r.Text
                    | _ -> None)
                |> String.concat ""
            Assert.DoesNotContain("缓存", footerText)
            Assert.Contains("入", footerText)
        finally
            window.Close()

    // ---------- 快捷键解析：Ctrl+上 / 下 ----------

    [<Fact>]
    let ``ctrl+up/down resolve to exchange jumps`` () =
        Headless.ensure ()
        let resolve (key: Key) =
            ShortcutRouter.resolve(
                KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.Control))
        Assert.Equal(JumpExchange -1, resolve Key.Up)
        Assert.Equal(JumpExchange 1, resolve Key.Down)
        // 不带 Ctrl 的上下键归输入框历史召回，不能被全局吞掉。
        let plain =
            ShortcutRouter.resolve(
                KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up, KeyModifiers = KeyModifiers.None))
        Assert.Equal(ShortcutAction.NoShortcut, plain)
