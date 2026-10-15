/// 视觉复核 v4：真浏览器走查发现的问题（第 206、211 节补强）。
module Wanxiang.Tests.VisualPassV4Tests

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.UI
open Wanxiang2.MessageCardView
open Wanxiang.Tests.Helpers

let private descendantsOf (control: Control) =
    seq {
        yield control
        for child in control.GetVisualDescendants() do
            match child with
            | :? Control as c -> yield c
            | _ -> ()
    }

let private chatActions : ChatActions =
    { renameTitle = ignore
      openSessionSettings = ignore
      forkFromHere = ignore
      stopGeneration = ignore
      requestOlderHistory = ignore
      retryLast = ignore
      toggleSidebar = ignore
      focusHome = ignore
      message =
        { CopyText = ignore
          Regenerate = ignore
          EditAndFork = ignore
          DeleteMessage = ignore
          DownloadAttachment = ignore
          OpenLink = ignore; CopyLink = None; Recover = None } }

let private view (role: string) (text: string) (commit: uint64) : MessageView =
    let payload = if role = "user" then userMessageJson text else assistantMessageJson text
    { MessageView.ofJson payload with commitId = Some commit }

let private pump (ms: int) =
    let until = DateTime.UtcNow.AddMilliseconds(float ms)
    while DateTime.UtcNow < until do
        Dispatcher.UIThread.RunJobs()
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick 1
        Threading.Thread.Sleep 10
    Dispatcher.UIThread.RunJobs()

/// 因果等待：Extent（虚拟化补测量）与 Offset（平滑滚动动画）连续三轮泵送都不再变化才算布局落定。
/// 决策 461 的墙钟上限保留为防挂死的安全网，不再参与成败判定：旧写法拿固定 pump 时长赌
/// 「跳转切挂载窗口引起的补测量 + 动画」先于断言结束，负载不同判据就分叉（ui-group 高并发下
/// Extent 事后增长 548px 时 offset 被推到底，误报回归）。稳定性判定交给观察对象本身。
let private awaitSettled (scroller: ScrollViewer) =
    let deadline = DateTime.UtcNow.AddSeconds 5.0
    let mutable lastOffset = scroller.Offset.Y
    let mutable lastExtent = scroller.Extent.Height
    let mutable stable = 0
    while stable < 3 && DateTime.UtcNow < deadline do
        pump 50
        let o = scroller.Offset.Y
        let e = scroller.Extent.Height
        if o = lastOffset && e = lastExtent then stable <- stable + 1
        else (lastOffset <- o; lastExtent <- e; stable <- 0)

let private longHistory () =
    [ for i in 1 .. 30 do
        let commit = uint64 (i * 2)
        yield view "user" (sprintf "第 %d 个问题" i) commit
        yield view "assistant" (String.replicate 12 (sprintf "第 %d 段回答，用来把列表撑高。" i)) (commit + 1UL) ]

[<Trait("Category", "UI")>]
[<Fact>]
let ``搜索命中跳转：滚到目标消息并短暂高亮，随后复原`` () =
    Headless.run (fun () ->
        let chat = ChatView(chatActions, fun _ -> Border() :> Control)
        chat.Build()
        chat.SetConversationChrome true
        let window = Window(Width = 800.0, Height = 600.0, Content = chat)
        window.Show()
        try
            chat.RenderMessages(longHistory (), None, None, 14.0, false, None, Set.empty)
            pump 300
            let scroller = descendantsOf chat |> Seq.choose (function :? ScrollViewer as s -> Some s | _ -> None) |> Seq.maxBy (fun s -> s.Extent.Height)
            scroller.Offset <- Vector(0.0, scroller.Extent.Height)
            pump 100
            let before = scroller.Offset.Y
            Assert.True(before > 500.0, sprintf "precondition: scrolled to bottom (%f)" before)
            Assert.True(chat.RevealAndHighlight 10UL)
            pump 900
            Assert.True(scroller.Offset.Y < before - 300.0, sprintf "should scroll up to the hit: %f -> %f" before scroller.Offset.Y)
            // 平滑动画完整跑完：若第一帧负增量被当成用户上滚，动画会被掐掉，offset 几乎不动（无显示环境不锁 TranslatePoint）。
            Assert.Equal(Some 10UL, chat.LastFlashedCommit)
            Assert.Equal(Some 10UL, chat.HighlightedCommit)
            let tinted =
                descendantsOf chat
                |> Seq.exists (fun c ->
                    match c with
                    | :? Border as b -> obj.ReferenceEquals(b.Background, Tokens.accentSoft) && b.Name = "reveal-highlight"
                    | _ -> false)
            Assert.True(tinted, "the hit card carries a visible tint surface")
            // 计时器到点走的同一条复原路径（DispatcherTimer 在无显示测试里不可确定泵出）
            chat.EndRevealHighlight()
            Dispatcher.UIThread.RunJobs()
            Assert.Equal(None, chat.HighlightedCommit)
            Assert.DoesNotContain(descendantsOf chat, fun c ->
                match c with
                | :? Border as b -> b.Name = "reveal-highlight" && obj.ReferenceEquals(b.Background, Tokens.accentSoft)
                | _ -> false)
        finally
            window.Close())

let private msgActions : Wanxiang2.MessageCardView.CardActions = chatActions.message

let private ctx (awaiting: Set<string>) : Wanxiang2.MessageCardView.CardContext =
    { FontSize = 14.0
      AutoCollapseReasoning = false
      Streaming = true
      IsLastAssistant = true
      Usage = None
      MissingAttachments = Set.empty
      AwaitingApproval = awaiting
      BrandAvatar = fun () -> Border() :> Control }

let private overviewRun (card: Control) =
    descendantsOf card
    |> Seq.choose (function :? TextBlock as t when not (isNull t.Inlines) -> Some t | _ -> None)
    |> Seq.collect (fun t -> t.Inlines |> Seq.choose (function :? Avalonia.Controls.Documents.Run as r -> Some r | _ -> None))
    |> Seq.find (fun r -> not (isNull r.Text) && r.Text.StartsWith "调用 ")

[<Trait("Category", "UI")>]
[<Fact>]
let ``定位后的下一次重绘不把视口拉回底部`` () =
    // 真浏览器走查：点搜索命中后 AppShell 立刻再 Render 一次（HideEmpty + RenderMessages），
    // HideEmpty 每次都复位滚动状态 → atBottom=true、动画被停，视口回到底部，命中「点了没反应」。
    Headless.run (fun () ->
        let chat = ChatView(chatActions, fun _ -> Border() :> Control)
        chat.Build()
        chat.SetConversationChrome true
        let window = Window(Width = 800.0, Height = 600.0, Content = chat)
        window.Show()
        try
            let history = longHistory ()
            chat.HideEmpty()
            chat.RenderMessages(history, None, None, 14.0, false, None, Set.empty)
            pump 300
            let scroller = descendantsOf chat |> Seq.choose (function :? ScrollViewer as s -> Some s | _ -> None) |> Seq.maxBy (fun s -> s.Extent.Height)
            awaitSettled scroller
            scroller.Offset <- Vector(0.0, scroller.Extent.Height)
            pump 100
            let bottom = scroller.Offset.Y
            Assert.True(chat.RevealAndHighlight 10UL)
            // 与 AppShell.Render 同序：HideEmpty → RenderMessages
            chat.HideEmpty()
            chat.RenderMessages(history, None, None, 14.0, false, None, Set.empty)
            // 决策 461 修订：先因果等待跳转生效（视口离开底部区），再等 Offset 与 Extent 双双落定，
            // 最后按同一判据断言。墙钟 5 秒只作防挂死安全网；旧写法在跳转生效后仅再 pump 固定 200ms
            // 就判定，高并发分组下延迟的补测量/后续写入会落进断言窗口，判据随调度分叉。
            let deadline = DateTime.UtcNow.AddSeconds 5.0
            while scroller.Offset.Y >= bottom - 300.0 && DateTime.UtcNow < deadline do pump 50
            awaitSettled scroller
            Assert.True(chat.RevealPinned, "reveal stays pinned across a re-render")
            Assert.True(scroller.Offset.Y < bottom - 300.0, sprintf "viewport stays at the hit: bottom=%f now=%f" bottom scroller.Offset.Y)
            // 换对话（AppShell 显式复位）后钉住解除
            chat.ResetScrollState()
            Assert.False(chat.RevealPinned)
        finally
            window.Close())
