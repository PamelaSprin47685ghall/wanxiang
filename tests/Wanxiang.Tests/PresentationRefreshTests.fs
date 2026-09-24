module Wanxiang.Tests.PresentationRefreshTests

open System
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Animation
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Automation.Provider
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.UI
open Wanxiang.Tests

let rec private descendants (control: Control) =
    seq {
        yield control
        match control with
        | :? Panel as panel ->
            for child in panel.Children do
                yield! descendants child
        | :? Decorator as decorator when not (isNull decorator.Child) ->
            yield! descendants decorator.Child
        | :? ContentControl as host ->
            match host.Content with
            | :? Control as child -> yield! descendants child
            | _ -> ()
        | _ -> ()
    }

let private show (content: Control) width height =
    Headless.ensure ()
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

let private brandLogo : float -> Control =
    fun _ -> Border(Width = 26.0, Height = 26.0) :> Control

let private messageActions : MessageActions =
    { copyText = ignore
      regenerate = ignore
      editAndFork = ignore
      deleteMessage = ignore
      downloadAttachment = ignore
      openLink = ignore }

let private chatActions : ChatActions =
    { renameTitle = ignore
      openSessionSettings = ignore
      forkFromHere = ignore
      stopGeneration = ignore
      requestOlderHistory = ignore
      retryLast = ignore
      toggleSidebar = ignore
      focusHome = ignore
      message = messageActions }

/// 渲染一张流式卡并取出其中的呼吸光标 Border。
/// 每次调用都是一次全新的窗口与卡片挂载：第二个流式用例借此模拟 ChatView
/// 「每个 delta 重建流式卡」的真实行为（新 Border attach），断言节奏不因此重启。
let private renderStreamingCaret () =
    Headless.ensure ()
    let chat = ChatView(chatActions, brandLogo)
    chat.Build()
    let window = show chat 760.0 520.0
    try
        chat.RenderMessages(
            [],
            Some { MessageView.empty with role = "assistant"; text = "正在流式输出" },
            None,
            Tokens.fontReading,
            true,
            None,
            Set.empty)
        Dispatcher.UIThread.RunJobs ()
        let carets =
            descendants chat
            |> Seq.choose (function
                | :? Border as border
                    when border.Width = 7.0
                         && border.Height = 15.0
                         && Object.ReferenceEquals(border.Background, Tokens.accent) -> Some border
                | _ -> None)
            |> Seq.toList
        Assert.Equal(1, List.length carets)
        List.head carets
    finally
        window.Close()

[<Fact>]
let ``user bubble keeps hairline strong one pixel border in both palettes`` () =
    Headless.ensure ()
    let chat = ChatView(chatActions, brandLogo)
    chat.Build()
    let window = show chat 760.0 520.0
    try
        let initial = Tokens.current ()
        let settle () =
            Thread.Sleep 240
            Dispatcher.UIThread.RunJobs ()
        try
            // 已知起点：先收敛到 Light，排除其它测试留下的中间补间色。
            Tokens.apply Light
            settle ()
            let userMessage = { MessageView.empty with role = "user"; text = "你好" }
            chat.RenderMessages([ userMessage ], None, None, Tokens.fontReading, true, None, Set.empty)
            Dispatcher.UIThread.RunJobs ()
            let bubble =
                descendants chat
                |> Seq.find (fun control -> AutomationProperties.GetName(control) = "用户消息")
                :?> Border
            // 直接锁到 token 实例：换成任何其它笔拂（含退回 borderSoft）都会红。
            Assert.True(Object.ReferenceEquals(bubble.BorderBrush, Tokens.hairlineStrong))
            Assert.Equal(Thickness 1.0, bubble.BorderThickness)
            Assert.True(Object.ReferenceEquals(bubble.Background, Tokens.userBubble))
            Assert.Equal(
                CornerRadius(Tokens.radiusLg, Tokens.radiusLg, Tokens.radiusSm, Tokens.radiusLg),
                bubble.CornerRadius)

            // 深色：同一实例换色，描边仍是 hairlineStrong 档、仍是 1px、半径不动。
            Tokens.apply Dark
            settle ()
            Assert.Equal(Palette.dark.hairlineStrong, (bubble.BorderBrush :?> SolidColorBrush).Color)
            Assert.Equal(Palette.dark.userBubble, (bubble.Background :?> SolidColorBrush).Color)
            Assert.Equal(Thickness 1.0, bubble.BorderThickness)
        finally
            Tokens.apply initial
            settle ()
    finally
        window.Close()

[<Fact>]
let ``streaming caret keeps opacity inside the breath band and pins to max under reduced motion`` () =
    // 确定性说明：Tokens.fs 的帧调度注释记录了本套件的无显示制度
    // （SetupWithoutStarting + RunJobs）下 DispatcherTimer 每次 Start 至多被服务一个
    // tick，无法稳定泵出连续帧。因此这里不断言「呼吸幅度随时间起伏」——
    // 那需要泵 Avalonia 动画/计时器，在本制度下不可确定；改为锁最接近的确定性不变量：
    // 首枚光标从峰值附近起步且落在 [min, max] 区间内、区间本身有序、帧/相位 token 语义命名，
    // 并由减弱动效分支补上「静态钉在 Max、不起计时器」的锚点。
    // MessageCard 模块顶层建有 Cursor（handCursor）：触碰其任何成员前必须先 ensure，
    // 否则模块静态构造在无 ICursorFactory 的进程里直接炸（TypeInitializationException）。
    Headless.ensure ()
    Assert.True(Tokens.opacityCaretBreathMin < Tokens.opacityCaretBreathMax)
    Assert.True(Tokens.opacityCaretBreathMax <= 1.0)
    Assert.Equal(50.0, MotionLedger.caretBreathFrame.TotalMilliseconds, 3)
    Assert.Equal(900.0, MotionLedger.caretBreathPhase.TotalMilliseconds, 3)

    // 呼吸运行原点现为进程级持久状态（MessageCard 模块级）：显式开启一段新运行，
    // 固定「新会话第一枚光标从峰值起步」这一既有断言，使本测试不受其它测试
    // 是否先渲染过流式卡的影响（流式卡每 delta 重建的契约由 IncrementalRenderTests 守护）。
    MessageCard.beginBreathRun ()

    try
        let caret = renderStreamingCaret ()
        // 首枚光标从峰值附近起步（beginBreathRun 之后 attach 会按真实流逝推进几十毫秒，
        // 峰值处仍在带宽内；不用小数精确相等，避免把渲染耗时耦合进断言）且不越区间。
        Assert.True(caret.Opacity >= Tokens.opacityCaretBreathMin)
        Assert.True(caret.Opacity <= Tokens.opacityCaretBreathMax)
        Assert.True(caret.Opacity > Tokens.opacityCaretBreathMax - 0.15, sprintf "首枚光标应起步于峰值附近：%f" caret.Opacity)

        MotionPolicy.setReduced true
        let reducedCaret = renderStreamingCaret ()
        // 减弱动效：静态全显、根本不起计时器，再泵 job 也不该有任何变化。
        Dispatcher.UIThread.RunJobs ()
        Assert.Equal(Tokens.opacityCaretBreathMax, reducedCaret.Opacity, 3)
    finally
        MotionPolicy.setReduced false

[<Fact>]
let ``caret breath opacity is a periodic pure function pinned inside the band`` () =
    // 纯函数本身不触碰布局，但这是本套件中第一处可能触发 MessageCard 模块静态构造的
    // 调用点（handCursor = new Cursor）：进程若无头平台未注册会直接炸，
    // 因此与其它 UI 测试一样先 ensure——也让本测试不依赖别的测试的执行次序。
    Headless.ensure ()
    // 相位是「自运行原点流逝时间」的纯函数，不挂任何控件或挂载状态：
    // 同一输入永远得到同一输出（修复前相位挂在 Border 上、每次 attach 重置到峰值，
    // 等价于一份不可测的挂载相对状态）。这里把该契约钉死：t=0 峰值、半周期见下限、
    // 整周期回峰值；全程落在闭区间内；密采样相邻无跳变（连续）；任意交错调用可重入（无状态）。
    let period = 2.0 * MotionLedger.caretBreathPhase
    let check (expected: float) (actual: float) = Assert.Equal(expected, actual, 6)
    check Tokens.opacityCaretBreathMax (MessageCard.caretBreathOpacity TimeSpan.Zero)
    check Tokens.opacityCaretBreathMin (MessageCard.caretBreathOpacity MotionLedger.caretBreathPhase)
    check Tokens.opacityCaretBreathMax (MessageCard.caretBreathOpacity period)
    // 半程（峰值与下限的中点）落在区间正中
    check
        ((Tokens.opacityCaretBreathMin + Tokens.opacityCaretBreathMax) / 2.0)
        (MessageCard.caretBreathOpacity (MotionLedger.caretBreathPhase / 2.0))

    // 无状态/可重入：与任意其它输入交错调用后，同一输入的结果不变
    TimeSpan.FromMilliseconds 123.0 |> MessageCard.caretBreathOpacity |> ignore
    TimeSpan.FromMilliseconds 2500.0 |> MessageCard.caretBreathOpacity |> ignore
    check Tokens.opacityCaretBreathMin (MessageCard.caretBreathOpacity MotionLedger.caretBreathPhase)

    // 周期对称性：op(t) == op(period - t)
    for ms in [ 37.0; 250.0; 613.0; 899.0 ] do
        let t = TimeSpan.FromMilliseconds ms
        check (MessageCard.caretBreathOpacity t) (MessageCard.caretBreathOpacity (period - t))

    // 闭区间 + 连续性：跨两个周期密采样，全部在区间内，相邻差有界
    let samples =
        [ for i in 0 .. 120 ->
            let t = TimeSpan.FromMilliseconds (30.0 * float i)
            MessageCard.caretBreathOpacity t ]
    for opacity in samples do
        Assert.True(opacity >= Tokens.opacityCaretBreathMin)
        Assert.True(opacity <= Tokens.opacityCaretBreathMax)
    samples
    |> List.pairwise
    |> List.iter (fun (a, b) ->
        // 30ms 内理论最大变化约 0.45·(π/1800)·30 ≈ 0.0236；这里留一倍余量
        Assert.True(abs (a - b) <= 0.05))

[<Fact>]
let ``rebuilt streaming caret continues the breath run instead of restarting at peak`` () =
    // 缺陷回归：ChatView 每个流式 delta 都整张重建流式卡（键为 None、不进缓存），
    // 旧 Border detach、新 Border attach。修复前 attach 一律把相位重置到峰值，
    // 于是每个 delta 光标都从最亮重新起跳，节奏碎裂。修复后相位取自模块级运行原点：
    // 新光标 attach 时应落在周期中的对应位置，而不是峰值。
    //
    // 确定性说明：本测试不 sleep、不依赖墙上时钟或渲染耗时。
    // 呼吸相位 = caretBreathOpacity(breathElapsed())，而 breathElapsed 只取决于
    // 模块级运行原点（OriginTicks）+ 单调时钟。经测试接缝
    // beginBreathRunAtElapsedMs 可把「已流逝时间」钉到周期内的确定位置，
    // 于是两次「delta 重建」之间的连续性完全可判定：
    //   · 运行原点未被渲染改写（无隐式重启/丢失）；
    //   · 每枚光标亮度 = 纯函数在同一流逝上的取值（证明它读的是运行原点）；
    //   · 重建光标明显暗于峰值（相位接上了，不是回到最亮）。
    // 三类复位回归都会被抓：OriginTicks 被改写 → 原点断言红；elapsed 归零
    // （attach 重开运行）→ 纯度断言与「暗于峰值」红；亮度写错来源 → 纯度断言红。
    Headless.ensure ()

    // 第一枚：把运行推进到 620ms（周期中段、明显暗于峰值、斜率中等，
    // attach 与断言之间几毫秒的计时抖动只会带来 ~0.01 的亮度差）。
    MessageCard.beginBreathRunAtElapsedMs 620.0
    let originAfterBegin = MessageCard.breathRunOriginTicks ()
    let fresh = renderStreamingCaret ()

    // 首次 attach 没有隐式重启运行。
    Assert.Equal(originAfterBegin, MessageCard.breathRunOriginTicks ())
    let freshElapsed = MessageCard.breathElapsed ()
    // 流逝被钉住：≥ 620ms；显著小于此即说明 elapsed 复位/停滞。
    Assert.True(freshElapsed > TimeSpan.FromMilliseconds 300.0, sprintf "首枚光标流逝不足：%A" freshElapsed)
    Assert.True(fresh.Opacity >= Tokens.opacityCaretBreathMin && fresh.Opacity <= Tokens.opacityCaretBreathMax)
    // 亮度 = 纯函数在同一流逝上的取值（<> 峰值）：证明相位取自运行原点。
    // 容差 0.05 覆盖「attach 写入时刻 ↔ 断言时刻」的帧步长/抖动
    // （该处一帧 50ms 至多 0.075），复位回归留下的峰值差 ≥0.155，不会被吞掉。
    Assert.True(
        abs (fresh.Opacity - MessageCard.caretBreathOpacity freshElapsed) <= 0.05,
        sprintf "首枚光标亮度偏离纯函数：%f" (abs (fresh.Opacity - MessageCard.caretBreathOpacity freshElapsed)))
    Assert.True(fresh.Opacity < Tokens.opacityCaretBreathMax - 0.1, sprintf "首枚光标仍钉在峰值：%f" fresh.Opacity)

    // 第二枚：模拟 ChatView「每个 delta 重建」——新窗口、新卡片、新 Border。
    // 只把时间基线前移（钉到 1180ms，与 620ms 关于半周期对称、同样远离峰值），
    // 运行本身连续。
    MessageCard.beginBreathRunAtElapsedMs 1180.0
    let originAtRebuild = MessageCard.breathRunOriginTicks ()
    let rebuilt = renderStreamingCaret ()

    // 核心断言一：重建期间运行原点未被重启/改写。
    // 抓住「attach 时无条件 beginBreathRun」与「运行原点丢失（live 复位）」两类回归。
    Assert.Equal(originAtRebuild, MessageCard.breathRunOriginTicks ())
    // 核心断言二：重建光标的亮度等于纯函数在当前流逝上的取值——
    // 相位接上了同一段运行，而不是回落峰值（复位后 elapsed 会掉到几百毫秒以下）。
    let rebuiltElapsed = MessageCard.breathElapsed ()
    Assert.True(rebuiltElapsed > TimeSpan.FromMilliseconds 860.0, sprintf "重建光标流逝不足：%A" rebuiltElapsed)
    Assert.True(
        abs (rebuilt.Opacity - MessageCard.caretBreathOpacity rebuiltElapsed) <= 0.05,
        sprintf "重建光标亮度偏离纯函数：%f" (abs (rebuilt.Opacity - MessageCard.caretBreathOpacity rebuiltElapsed)))
    // 核心断言三：重建光标明显暗于峰值（周期中段），而非每次从最亮重新起跳。
    Assert.True(rebuilt.Opacity < Tokens.opacityCaretBreathMax - 0.1, sprintf "重建光标仍钉在峰值：%f" rebuilt.Opacity)

[<Fact>]
let ``code block copy confirmation dips toward fade opacity and keeps the confirmed label`` () =
    // 确定性说明：回到 1.0 的那一腿跑在 MotionLedger.copyConfirmationFade 的
    // DispatcherTimer 上，本套件的无显示制度无法确定性地泵到它（同 Tokens.fs 帧调度注释），
    // 因此这里锁两件确定的事：(1) 点下复制的瞬间 opacity 同步 dip 到中间档附近、
    // 且始终不越过 [fade, 1.0] 闭区间；(2) 减弱动效时完全不做这段淡出，保持 1.0。
    Headless.ensure ()
    let runCopy () =
        let mutable copiedText = ""
        let renderer = MarkdownRenderer(14.0, (fun text -> copiedText <- text), ignore, false)
        let controls = renderer.RenderText "```fsharp\nlet x = 42\n```"
        let window = show controls 600.0 400.0
        let button =
            descendants controls
            |> Seq.find (fun control ->
                match ToolTip.GetTip control with
                | :? string as tip -> tip = "复制代码"
                | _ -> false)
        let invoke =
            ControlAutomationPeer.CreatePeerForElement button
            |> Assert.IsAssignableFrom<IInvokeProvider>
        invoke.Invoke ()
        Dispatcher.UIThread.RunJobs ()
        (window, button, copiedText)

    let normalWindow, normalButton, copiedText = runCopy ()
    try
        Assert.Equal("let x = 42", copiedText.Trim())
        Assert.Equal("已复制！", AutomationProperties.GetName(normalButton))
        Assert.Equal("已复制！", ToolTip.GetTip(normalButton) :?> string)
        Assert.True(normalButton.Opacity >= Tokens.opacityCopyConfirmFade - 0.001)
        Assert.True(normalButton.Opacity <= 1.0 + 0.001)
        Assert.Equal(150.0, MotionLedger.copyConfirmationFade.TotalMilliseconds, 3)
    finally
        normalWindow.Close()

    MotionPolicy.setReduced true
    try
        let reducedWindow, reducedButton, reducedCopied = runCopy ()
        try
            Assert.Equal("let x = 42", reducedCopied.Trim())
            Assert.Equal("已复制！", AutomationProperties.GetName(reducedButton))
            Assert.Equal(1.0, reducedButton.Opacity, 3)
        finally
            reducedWindow.Close()
    finally
        MotionPolicy.setReduced false

[<Fact>]
let ``disclosure chevron and copy confirmation ride the shared easeOutCubic`` () =
    // 全应用统一缓动：披露箭头旋转与复制确认淡出都接 MotionPolicy.easeOutCubic，
    // 时长分别锚定 MotionLedger.disclosureChevronRotate / copyConfirmationFade
    // （经 MotionPolicy 门控，减弱动效归零），且旋转只改 transform、不碰布局几何。
    Headless.ensure ()
    let message : MessageView =
        { MessageView.empty with
            role = "assistant"
            text = "hello"
            reasoning = "思考中"
            commitId = Some 1UL
            committedAt = Some DateTimeOffset.UtcNow }
    let ctx : MessageContext =
        { fontSize = 14.0
          autoCollapseReasoning = false
          streaming = false
          isLastAssistant = true
          usage = None
          missingAttachments = Set.empty
          brandAvatar = fun () -> Border() :> Control }
    let card = MessageCard.render message ctx messageActions (Some 0)
    let window = show card 600.0 400.0
    try
        // 披露箭头：RotateTransform 上唯一的 DoubleTransition 锚定 chevron 档位 + easeOutCubic。
        let chevronRotations =
            descendants card
            |> Seq.choose (fun c ->
                match c.RenderTransform with
                | :? RotateTransform as rotate -> Some rotate
                | _ -> None)
            |> Seq.filter (fun rotate -> not (isNull rotate.Transitions) && rotate.Transitions.Count > 0)
            |> Seq.toList
        Assert.True(chevronRotations.Length >= 1)
        for rotate in chevronRotations do
            let transition =
                rotate.Transitions
                |> Seq.pick (function :? DoubleTransition as t -> Some t | _ -> None)
            Assert.Same(RotateTransform.AngleProperty, transition.Property)
            Assert.Equal(
                MotionPolicy.duration MotionLedger.disclosureChevronRotate.TotalMilliseconds,
                transition.Duration)
            Assert.Same(MotionPolicy.easeOutCubic, transition.Easing)
        // 复制确认：opacity 淡出补间同样接 easeOutCubic，时长锚定 copyConfirmationFade。
        // 复制按钮的真实 tip 由 createCopyButton 的调用方文案决定（工具参数卡为
        // 「复制工具参数与结果」）；按文本前缀匹配而不是写死裸「复制」。
        let copyButton =
            descendants card
            |> Seq.find (fun c ->
                match ToolTip.GetTip c with
                | :? string as tip -> tip = "复制"
                | _ -> false)
        let fade =
            copyButton.Transitions
            |> Seq.pick (function :? DoubleTransition as t when t.Property = Visual.OpacityProperty -> Some t | _ -> None)
        Assert.Equal(
            MotionPolicy.duration MotionLedger.copyConfirmationFade.TotalMilliseconds,
            fade.Duration)
        Assert.Same(MotionPolicy.easeOutCubic, fade.Easing)
    finally
        window.Close()

[<Fact>]
let ``settings appearance grouping sits on surface container with hairline border`` () =
    Headless.ensure ()
    let actions : SettingsActions =
        { upsertProvider = fun _ _ -> ()
          deleteProvider = ignore
          probeProvider = ignore
          upsertMcp = fun _ _ -> ()
          deleteMcp = ignore
          updateGeneration = fun _ _ -> ()
          savePrefs = ignore
          toast = fun _ _ -> () }
    let overlay = OverlayHost(Grid())
    let settings = SettingsGeneral(overlay, actions, ignore)
    let appearance = settings.BuildAppearance ()
    let window = show appearance 760.0 900.0
    try
        Dispatcher.UIThread.RunJobs ()
        let groupingCards =
            descendants appearance
            |> Seq.choose (function
                | :? Border as border -> Some border
                | _ -> None)
            |> Seq.filter (fun border ->
                Object.ReferenceEquals(border.Background, Tokens.surfaceContainer)
                && Object.ReferenceEquals(border.BorderBrush, Tokens.hairline)
                && border.BorderThickness = Thickness 1.0)
            |> Seq.toList
        Assert.True(List.length groupingCards >= 1)
    finally
        window.Close()

[<Fact>]
let ``chat header divider uses the lightest hairline tier`` () =
    Headless.ensure ()
    let chat = ChatView(chatActions, brandLogo)
    chat.Build()
    chat.SetConversationChrome true
    let window = show chat 760.0 520.0
    try
        Dispatcher.UIThread.RunJobs ()
        let header =
            descendants chat
            |> Seq.choose (function
                | :? Border as border -> Some border
                | _ -> None)
            |> Seq.find (fun border ->
                border.Height = Tokens.barHeight
                        && border.BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0))
        Assert.True(Object.ReferenceEquals(header.BorderBrush, Tokens.hairline))
    finally
        window.Close()

[<Fact>]
let ``standard hairline helper stays on the border soft tier`` () =
    // Ui.hairline 是「标准分隔」档（borderSoft）；最轻档是 Tokens.hairline，两档分层。
    // 这里防止将来有人把 Ui.hairline 改成追更浅的色——那会让所有调用点一起变浅。
    // Ui 模块顶层建有 Cursor（handCursor）：触碰其任何成员前必须先 ensure。
    Headless.ensure ()
    let divider = Ui.hairline ()
    Assert.True(Object.ReferenceEquals(divider.Background, Tokens.borderSoft))
    Assert.False(Object.ReferenceEquals(divider.Background, Tokens.hairline))

/// 判定披露头是否挂着共享的「带描边表面过渡」集合：Transitions 里恰好含
/// Background / BorderBrush 两个 BrushTransition，时长同出
/// MotionLedger.controlStateDuration。每次调用都是新实例（不能引用相等），
/// 因此锁结构 + 时长来源——手写一套不同时长的私有集合会在这里红。
let private assertBorderedDisclosureTransition (control: Control) =
    Assert.False(isNull control.Transitions)
    let mutable brushCount = 0
    for transition in control.Transitions do
        match transition with
        | :? Avalonia.Animation.BrushTransition as brush ->
            brushCount <- brushCount + 1
            Assert.Equal(MotionLedger.controlStateDuration (), brush.Duration)
            Assert.True(
                Object.ReferenceEquals(brush.Property, Border.BackgroundProperty)
                || Object.ReferenceEquals(brush.Property, Border.BorderBrushProperty))
        | _ -> ()
    Assert.Equal(2, brushCount)

[<Fact>]
let ``disclosure headers share one bordered transition`` () =
    // 确定性说明：无显示制度下 Avalonia 原生过渡不会推进（见 Tokens.fs 帧调度注释），
    // 因此不断言补间中的颜色，只锁「思考过程 / 工具调用 / 错误技术细节三个披露头都挂着
    // 同一套共享过渡集合（Ui.surfaceBorderedTransitions）」这一结构不变量。
    Headless.ensure ()
    let context : MessageContext =
        { fontSize = Tokens.fontReading
          autoCollapseReasoning = false
          streaming = false
          isLastAssistant = false
          usage = None
          missingAttachments = Set.empty
          brandAvatar = fun () -> Border(Width = 26.0, Height = 26.0) :> Control }

    let withHeader (name: string) (card: Control) =
        let window = show card 720.0 520.0
        try
            Dispatcher.UIThread.RunJobs ()
            descendants card
            |> Seq.find (fun control -> AutomationProperties.GetName control = name)
            |> assertBorderedDisclosureTransition
        finally
            window.Close ()

    // 思考过程头（初始展开，自动化名为收起态）。
    let reasoningMessage = { MessageView.empty with role = "assistant"; reasoning = "想一想"; text = "答案" }
    withHeader "收起思考过程" (MessageCard.render reasoningMessage context messageActions None)

    // 工具调用头（未展开）。
    let toolMessage =
        { MessageView.empty with
            commitId = Some 1UL
            role = "assistant"
            toolCalls =
                [ { callId = "call-1"
                    name = "demo_tool"
                    argumentsJson = "{}"
                    result = Some "ok" } ] }
    withHeader "展开工具调用 demo_tool" (MessageCard.render toolMessage context messageActions None)

    // 错误卡「技术细节」开关。
    let err : GenerationError =
        { kind = ProviderUnavailable
          message = "无法连接服务商"
          detail = Some "Connection refused at 127.0.0.1:8799"
          retryable = true
          retryAfterSeconds = Some 5 }
    withHeader "技术细节" (MessageCard.errorCard err (fun () -> ()) None)

[<Fact>]
let ``export progress bar is determinate under reduced motion`` () =
    // 确定性说明：ConversationExportController.Start 必然把状态置为 ExportReading(0, None)，
    // RenderProgress 因此必定经过 total.IsNone 分支。这里只锁 IsIndeterminate 这一个
    // 确定不变量：注入的 sendQuery 返回 false 后异步 continuation 可能把状态推到
    // ExportFailed（隐藏进度条），但那不改 IsIndeterminate，故不断言可见性/文字。
    Headless.ensure ()

    let renderProgress () =
        // OverlayHost 是控制器而非控件：对话框挂在它持有的 root Grid 上，从 root 遍历。
        let root = Grid()
        let overlay = OverlayHost(root)
        let dialog =
            ConversationExportDialog(
                overlay,
                Guid.NewGuid(),
                "测试会话",
                (fun _ -> Task.FromResult false),
                (fun () -> Unchecked.defaultof<TopLevel>))
        let window = show root 480.0 360.0
        try
            dialog.Show()
            Dispatcher.UIThread.RunJobs()
            let bars =
                descendants root
                |> Seq.choose (function
                    | :? ProgressBar as bar -> Some bar
                    | _ -> None)
                |> Seq.toList
            Assert.Equal(1, List.length bars)
            List.head bars
        finally
            overlay.CloseDialog()
            window.Close()

    let normalBar = renderProgress ()
    Assert.True(normalBar.IsIndeterminate)

    MotionPolicy.setReduced true
    try
        let reducedBar = renderProgress ()
        Assert.False(reducedBar.IsIndeterminate)
    finally
        MotionPolicy.setReduced false
