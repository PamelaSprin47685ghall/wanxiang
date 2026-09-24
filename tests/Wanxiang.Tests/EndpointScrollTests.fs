namespace Wanxiang.Tests

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

/// 端点滚动：回到开头 / 回到最新。
///
/// 借 kelivo 滚动条两侧对称可达的做法（scroll_nav_buttons 的回到顶部 + message_list_view
/// 的 Home/End），但收敛到「两端的快捷键语义」，不加第二套悬浮按钮。
///
/// 此前端位只有一个方向有路：右下角「回到最新」。想看最早的提问只能一路拖滚轮，
/// 长会话里滚回去还要自己记住停在哪儿。这里锁三条可观察契约：
/// - Ctrl+Home / Ctrl+End 解析成两个端点意图（且不与既有快捷键抢键）；
/// - 两端都落位：到顶 offset = 0，到底 offset = 最大可滚量；
/// - 端点位移会让「上一条 / 下一条」重新按新视口起算（不能接着半途的锚点跳）。
module EndpointScrollTests =

    let private homeFocus = ref 0
    let private buildChat (messages: MessageView list) =
        homeFocus := 0
        Headless.ensure ()
        // 只锁落位，不锁动画曲线；动效路径由既有 smoothScroll 用例覆盖。
        MotionPolicy.setReduced true
        let chatActions =
            { renameTitle = ignore
              openSessionSettings = ignore
              forkFromHere = ignore
              stopGeneration = ignore
              requestOlderHistory = ignore
              retryLast = ignore
              toggleSidebar = ignore
              focusHome = fun () -> incr homeFocus
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

    let private scrollerOf (chat: ChatView) : ScrollViewer * StackPanel =
        let layout = Assert.IsAssignableFrom<DockPanel>(chat.Child)
        let body = Assert.IsAssignableFrom<Grid>(layout.Children.[1])
        let scroller = Assert.IsAssignableFrom<ScrollViewer>(body.Children.[0])
        let panel = Assert.IsAssignableFrom<StackPanel>(scroller.Content)
        scroller, panel

    /// 一个可视节点上所有的可读文本。
    /// 助手正文走 MarkdownRenderer：文本住在 SelectableTextBlock 的 Inlines 里，
    /// .Text 是 null；只读 .Text 会漏掉所有助手卡。
    let private textsOf (visual: Visual) =
        seq {
            match visual with
            | :? TextBlock as t ->
                if not (isNull t.Text) then t.Text
                for run in t.Inlines do
                    match run with
                    | :? Avalonia.Controls.Documents.Run as r -> r.Text
                    | _ -> ()
            | _ -> () }

    let private containsText (card: Control) (needle: string) : bool =
        card.GetVisualDescendants()
        |> Seq.collect textsOf
        |> Seq.exists (fun text -> not (isNull text) && text.Contains needle)

    /// 目标卡是否完整落在视口内。用户真正在乎的是「开头那轮看得见」，
    /// 比断言 offset 像素值更稳（首屏 follow 的残留位移与之无关）。
    /// needle 命中的是「那一轮」而非某张卡：提问与回答卡都含轮次号，取最上面
    /// 那一张（= 该轮的用户卡），否则 Seq.find 可能命中下面的回答卡。
    let private cardInViewport (chat: ChatView) (needle: string) : bool =
        let scroller, panel = scrollerOf chat
        let card =
            panel.Children
            |> Seq.cast<Control>
            |> Seq.filter (fun c -> containsText c needle)
            |> Seq.minBy (fun c -> c.Bounds.Y)
        let top = scroller.Offset.Y
        let bottom = top + scroller.Viewport.Height
        card.Bounds.Y >= top - 1.0 && card.Bounds.Y + card.Bounds.Height <= bottom + 1.0

    /// 卡是否至少有部分落在视口内：只要求「看得见」，不要求整卡完整可见。
    /// 末条助手卡贴底且高于一屏时（本文件的填充量就是这样），完整可见做不到，
    /// 而用户要的是「最新内容在屏幕上」。
    let private cardVisible (chat: ChatView) (needle: string) : bool =
        let scroller, panel = scrollerOf chat
        let card =
            panel.Children
            |> Seq.cast<Control>
            |> Seq.filter (fun c -> containsText c needle)
            |> Seq.minBy (fun c -> c.Bounds.Y)
        let top = scroller.Offset.Y
        let bottom = top + scroller.Viewport.Height
        card.Bounds.Y < bottom && card.Bounds.Y + card.Bounds.Height > top

    /// 等 ScrollChanged 把位移落到 scroller 上：Offset 写入是同步的，
    /// 但 animate=false 的 scrollToHome 仍要一帧才反映到 Extent。
    let private settle (chat: ChatView) =
        Dispatcher.UIThread.RunJobs()
        Dispatcher.UIThread.RunJobs()

    // 键盘激活「回到最新」后焦点不得掉 null：按钮随 atBottom 隐藏，Avalonia 隐藏
    // 控件会直接清焦点（探针实测 after=<null>），键盘用户接着按 Tab 会从窗口根
    // 部重走。归宿走 AppShell 注入的 focusHome（输入区），与停止键/生成收尾同一条路。
    [<Fact>]
    let ``keyboard activated scroll to bottom hands focus home`` () =
        let chat, window = buildChat (exchanges 8)
        try
            let scroller, _ = scrollerOf chat
            // 远离底部：ScrollChanged 让按钮自己出现（用户向上滚的自然路径）。
            scroller.Offset <- Vector(0.0, scroller.Extent.Height * 0.5)
            settle chat
            let layout = Assert.IsAssignableFrom<DockPanel>(chat.Child)
            let body = Assert.IsAssignableFrom<Grid>(layout.Children.[1])
            let btn = Assert.IsAssignableFrom<Border>(body.Children.[2])
            Assert.True(btn.IsVisible, "反证前提：离开底部后按钮必须出现")
            btn.Focus() |> ignore
            settle chat
            let before = window.FocusManager.GetFocusedElement()
            Assert.True(System.Object.ReferenceEquals(before, btn), "反证前提：焦点必须在按钮上")
            btn.RaiseEvent(
                KeyEventArgs(
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Enter,
                    KeyModifiers = KeyModifiers.None,
                    Source = btn))
            settle chat
            Assert.False(btn.IsVisible, "激活后按钮按既有约定隐藏")
            Assert.True(!homeFocus = 1, sprintf "键盘激活必须把焦点交还给输入区，实际 %d" !homeFocus)
        finally
            window.Close()

    // ---------- 快捷键解析 ----------

    [<Fact>]
    let ``ctrl home and ctrl end resolve to endpoint scroll intents`` () =
        let event key modifiers = KeyEventArgs(Key = key, KeyModifiers = modifiers)
        Assert.Equal(ScrollToBeginning, ShortcutRouter.resolve (event Key.Home KeyModifiers.Control))
        // ⌘ 与 Ctrl 同权：macOS 上 ⌘Home 就是同一意图。
        Assert.Equal(ScrollToBeginning, ShortcutRouter.resolve (event Key.Home KeyModifiers.Meta))
        Assert.Equal(ScrollToEnd, ShortcutRouter.resolve (event Key.End KeyModifiers.Control))

    [<Fact>]
    let ``plain home end and shifted variants stay unbound`` () =
        let event key modifiers = KeyEventArgs(Key = key, KeyModifiers = modifiers)
        // 裸 Home/End 必须是 NoShortcut：消息区正在读文本，端点键该归内容自己。
        Assert.Equal(NoShortcut, ShortcutRouter.resolve (event Key.Home KeyModifiers.None))
        Assert.Equal(NoShortcut, ShortcutRouter.resolve (event Key.End KeyModifiers.None))
        // Ctrl+Shift+Home 也不占用：端点键只设一个组合，避免与将来可能的选区快捷键相撞。
        Assert.Equal(NoShortcut, ShortcutRouter.resolve (event Key.Home (KeyModifiers.Control ||| KeyModifiers.Shift)))

    [<Fact>]
    let ``endpoint keys do not collide with exchange navigation`` () =
        let event key modifiers = KeyEventArgs(Key = key, KeyModifiers = modifiers)
        // 问答跳转占 Up/Down，端点占 Home/End：两条键位互不遮挡。
        Assert.Equal(JumpExchange -1, ShortcutRouter.resolve (event Key.Up KeyModifiers.Control))
        Assert.Equal(JumpExchange 1, ShortcutRouter.resolve (event Key.Down KeyModifiers.Control))
        Assert.Equal(ScrollToBeginning, ShortcutRouter.resolve (event Key.Home KeyModifiers.Control))
        Assert.Equal(ScrollToEnd, ShortcutRouter.resolve (event Key.End KeyModifiers.Control))

    // ---------- 落位 ----------

    [<Fact>]
    let ``scroll to beginning lands at offset zero with the first round visible`` () =
        let chat, window =
            buildChat (exchanges 5)
        try
            let scroller, _ = scrollerOf chat
            // 先制造一个「人在历史中段」的起点：只断言位移，不依赖首屏 follow。
            scroller.Offset <- Vector(0.0, scroller.Extent.Height * 0.5)
            settle chat
            Assert.True(scroller.Offset.Y > 1.0, "前置条件：离开顶部")
            chat.ScrollToBeginning()
            settle chat
            Assert.True(scroller.Offset.Y < 1.0, sprintf "回到开头后应贴顶：offset=%f" scroller.Offset.Y)
            Assert.True(cardInViewport chat "第 1 轮提问", "最早的提问应完整可见")
        finally
            window.Close()

    [<Fact>]
    let ``scroll to end lands at the maximum scrollable offset`` () =
        let chat, window =
            buildChat (exchanges 5)
        try
            let scroller, _ = scrollerOf chat
            scroller.Offset <- Vector(0.0, 0.0)
            settle chat
            Assert.True(scroller.Offset.Y < 1.0, "前置条件：已在顶部")
            chat.ScrollToEnd()
            settle chat
            let maxOffset = scroller.Extent.Height - scroller.Viewport.Height
            Assert.True(
                abs (scroller.Offset.Y - maxOffset) < 1.0,
                sprintf "回到结尾后应贴底：offset=%f max=%f" scroller.Offset.Y maxOffset)
            // 末轮助手卡本身高于一屏（测试填充量决定的），不可能整卡可见；
            // 用户要的是「最新内容在屏幕上」，所以断言可见而不是完整可见。
            Assert.True(cardVisible chat "第 5 轮", "最新一轮应可见")
        finally
            window.Close()

    [<Fact>]
    let ``round trip between endpoints is stable`` () =
        let chat, window =
            buildChat (exchanges 4)
        try
            let scroller, _ = scrollerOf chat
            chat.ScrollToBeginning()
            settle chat
            let atTop = scroller.Offset.Y
            chat.ScrollToEnd()
            settle chat
            let atBottom = scroller.Offset.Y
            chat.ScrollToBeginning()
            settle chat
            // 两端往返后仍贴顶：说明回到底没有留下会粘住后续位移的状态。
            Assert.True(
                abs (scroller.Offset.Y - atTop) < 1.0,
                sprintf "往返后应仍贴顶：offset=%f firstTop=%f" scroller.Offset.Y atTop)
            Assert.True(atTop < atBottom, "顶部与底部偏移应不同")
        finally
            window.Close()

    // ---------- 与问答跳转的耦合 ----------

    [<Fact>]
    let ``scrolling to beginning clears the exchange anchor`` () =
        let chat, window =
            buildChat (exchanges 6)
        try
            let scroller, _ = scrollerOf chat
            // 起点在底部，然后把锚点一路钉到第 3 轮（6 → 5 → 4 → 3）。
            scroller.Offset <- Vector(0.0, scroller.Extent.Height - scroller.Viewport.Height)
            settle chat
            for _ in 1 .. 3 do
                chat.JumpExchange -1
                settle chat
            // 回到开头：锚点作废，下面的导航必须从新视口起算。
            chat.ScrollToBeginning()
            settle chat
            Assert.True(scroller.Offset.Y < 1.0, "应已回到开头")
            // 锚点作废后，当前轮由视口定：首屏看得见第 1、2 两轮，\
            // 锚点作废后，当前轮由视口定：首屏看得见第 1、2 两轮，
            // 「开头已进入视口的最后一张用户卡」= 第 2 轮，于是「下一条」= 第 3 轮。
            // 注意这个目标与旧锚点重合（旧锚点本来就在第 3 轮），所以单看下一条
            // 分辨不出锚点在不在；要接着往回走——从视口起算时序列的开头是第 1 轮，
            // 翻到第 1 轮就该停住，不会越过最早那一轮继续找。
            chat.JumpExchange 1
            settle chat
            Assert.True(cardInViewport chat "第 3 轮提问", "视口基准的下一条应到第 3 轮")
            chat.JumpExchange -1
            settle chat
            Assert.True(cardInViewport chat "第 2 轮提问", "上一条应回到第 2 轮")
            chat.JumpExchange -1
            settle chat
            // 序列开头就是第 1 轮：到这里必须停住。锚点若残留，上一条会越过
            // 第 1 轮去找不存在的前一轮，位移会与第 1 轮不同。
            Assert.True(cardInViewport chat "第 1 轮提问", "继续上一条应到第 1 轮")
        finally
            window.Close()

    [<Fact>]
    let ``shortening content while away from the top still recovers`` () =
        let chat, window =
            buildChat (exchanges 5)
        try
            let scroller, _ = scrollerOf chat
            scroller.Offset <- Vector(0.0, scroller.Extent.Height - scroller.Viewport.Height)
            settle chat
            // 内容突然变短（例如切到只剩两轮的会话）：offset 可能超出新的最大可滚量。
            chat.RenderMessages(exchanges 2, None, None, Tokens.fontReading, true, None, Set.empty)
            settle chat
            chat.ScrollToBeginning()
            settle chat
            Assert.True(scroller.Offset.Y < 1.0, sprintf "内容变短后仍应能回到开头：offset=%f" scroller.Offset.Y)
        finally
            window.Close()
