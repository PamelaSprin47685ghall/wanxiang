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
open Wanxiang2.MessageCardView
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
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

let private brandLogo : float -> Control =
    fun _ -> Border(Width = 26.0, Height = 26.0) :> Control

let private messageActions : Wanxiang2.MessageCardView.CardActions =
    { CopyText = ignore
      Regenerate = ignore
      EditAndFork = ignore
      DeleteMessage = ignore
      DownloadAttachment = ignore
      OpenLink = ignore; CopyLink = None; Recover = None }

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

[<Trait("Category", "UI")>]
[<Fact>]
let ``user bubble keeps hairline strong one pixel border in both palettes`` () =
    Headless.run (fun () ->
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
                CornerRadius(Spacing.Radius.lg, Spacing.Radius.lg, Spacing.Radius.sm, Spacing.Radius.lg),
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``settings appearance grouping sits on surface container with hairline border`` () =
    Headless.run (fun () ->
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``chat header divider uses the lightest hairline tier`` () =
    Headless.run (fun () ->
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``standard hairline helper stays on the border soft tier`` () =
    Headless.run (fun () ->
    // Ui.hairline 是「标准分隔」档（borderSoft）；最轻档是 Tokens.hairline，两档分层。
    // 这里防止将来有人把 Ui.hairline 改成追更浅的色——那会让所有调用点一起变浅。
    // Ui 模块顶层建有 Cursor（handCursor）：触碰其任何成员前必须先 ensure。
    let divider = Ui.hairline ()
    Assert.True(Object.ReferenceEquals(divider.Background, Tokens.borderSoft))
    Assert.False(Object.ReferenceEquals(divider.Background, Tokens.hairline))
    )

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
