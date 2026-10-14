module Wanxiang.Tests.WindowedNavigationTests

open System
open Avalonia.Controls
open Avalonia.Threading
open Xunit
open Wanxiang.UI
open Wanxiang2.MessageCardView
open Wanxiang.Tests

/// 决策 300：挂载窗口（尾部 40 条）下，「上一轮」按需挂载 / 向主机要更早的页，总能走到对话开头。
[<Trait("Category", "UI")>]
type Nav() =
    let messageAt (i: int) : MessageView =
        { MessageView.empty with
            role = (if i % 2 = 1 then "user" else "assistant")
            text = sprintf "消息 %d" i
            commitId = Some(uint64 i)
            committedAt = Some DateTimeOffset.UtcNow }

    let noopMessageActions: Wanxiang2.MessageCardView.CardActions =
        { CopyText = ignore; Regenerate = ignore; EditAndFork = ignore; DeleteMessage = ignore; DownloadAttachment = ignore; OpenLink = ignore; CopyLink = None; Recover = None }

    let mount (requestOlder: unit -> unit) =
        let actions: ChatActions =
            { renameTitle = ignore; openSessionSettings = ignore; forkFromHere = ignore; stopGeneration = ignore
              requestOlderHistory = requestOlder; retryLast = ignore; toggleSidebar = ignore; focusHome = ignore; message = noopMessageActions }
        let view = ChatView(actions, fun size -> Border(Width = size, Height = size) :> Control)
        view.Build()
        let window = Window(Width = 1000.0, Height = 760.0, Content = view)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        view, window

    let render (view: ChatView) messages =
        view.RenderMessages(messages, None, None, Tokens.fontReading, true, None, Set.empty)
        Dispatcher.UIThread.RunJobs()

    [<Fact>]
    member _.``上一轮越过挂载窗口时先挂更早的消息，一路走到开头``() =
        Headless.run (fun () ->
            let view, window = mount ignore
            try
                render view [ for i in 1 .. 200 -> messageAt i ]
                Assert.True(view.HiddenMessageCount > 0)
                let mutable steps = 0
                while view.CanJumpPrevious && steps < 300 do
                    view.JumpExchange -1
                    Dispatcher.UIThread.RunJobs()
                    steps <- steps + 1
                Assert.Equal(0, view.HiddenMessageCount)
                Assert.False(view.CanJumpPrevious)
            finally
                window.Close())

    [<Fact>]
    member _.``已加载的都看完了且主机还有更早的页：上一轮去要下一页，页到后续跳``() =
        Headless.run (fun () ->
            let mutable requests = 0
            let view, window = mount (fun () -> requests <- requests + 1)
            try
                let tail = [ for i in 101 .. 130 -> messageAt i ]
                render view tail
                view.SetOlderAvailable true
                let mutable steps = 0
                while not view.PendingJumpPrevious && steps < 100 do
                    Assert.True(view.CanJumpPrevious)
                    view.JumpExchange -1
                    Dispatcher.UIThread.RunJobs()
                    steps <- steps + 1
                // 滚到顶本身也会预取（AppShell 用 pageLoading 去重）；这里只要求确实要过页。
                Assert.True(requests >= 1)
                Assert.True(view.PendingJumpPrevious)
                view.BeginHistoryPrependAnchor()
                render view ([ for i in 1 .. 100 -> messageAt i ] @ tail)
                view.RestoreHistoryPrependAnchorDeferred()
                Dispatcher.UIThread.RunJobs()
                Assert.False(view.PendingJumpPrevious)
                view.SetOlderAvailable false
                Assert.True(view.CanJumpPrevious)
            finally
                window.Close())

    [<Fact>]
    member _.``搜索命中 / 消息链接指向窗口外的消息：挂上并高亮``() =
        Headless.run (fun () ->
            let view, window = mount ignore
            try
                render view [ for i in 1 .. 200 -> messageAt i ]
                Assert.True(view.RevealAndHighlight 2UL)
                Dispatcher.UIThread.RunJobs()
                Assert.Equal(Some 2UL, view.LastFlashedCommit)
                Assert.True(view.HiddenMessageCount < 2)
            finally
                window.Close())
