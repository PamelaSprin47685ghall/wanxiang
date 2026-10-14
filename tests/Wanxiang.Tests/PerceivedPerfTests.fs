module Wanxiang.Tests.PerceivedPerfTests

open System
open System.Diagnostics
open Avalonia.Controls
open Avalonia.Threading
open Xunit
open Wanxiang.UI
open Wanxiang2.MessageCardView
open Wanxiang.Tests

/// 决策 290：感知性能基准（r12）。数字写进测试输出（`PERF ...` 行），上界只防大幅回归。
/// 打开长对话 / 侧栏 1000+ 对话 / 长回复流式渲染——都在真实窗口里量「数据到达 → 布局完成」。
[<Trait("Category", "UI")>]
type Bench(output: ITestOutputHelper) =

    let reply (i: int) =
        sprintf "### 第 %d 段\n\n一段中文正文，混 `行内代码` 与 [链接](https://example.com)，以及公式 $E = mc^2$。\n\n- 列表一\n- 列表二\n\n```fsharp\nlet f%d x =\n    x * %d + 1\n```\n\n$$\\int_0^1 x^2 \\, dx = \\frac{1}{3}$$" i i i

    let messageAt (i: int) : MessageView =
        { MessageView.empty with
            role = (if i % 2 = 1 then "user" else "assistant")
            text = (if i % 2 = 1 then sprintf "追问 %d" i else reply i)
            commitId = Some(uint64 i)
            committedAt = Some DateTimeOffset.UtcNow }

    let noopMessageActions: Wanxiang2.MessageCardView.CardActions =
        { CopyText = ignore; Regenerate = ignore; EditAndFork = ignore; DeleteMessage = ignore; DownloadAttachment = ignore; OpenLink = ignore; CopyLink = None; Recover = None }

    let noopActions: ChatActions =
        { renameTitle = ignore; openSessionSettings = ignore; forkFromHere = ignore; stopGeneration = ignore
          requestOlderHistory = ignore; retryLast = ignore; toggleSidebar = ignore; focusHome = ignore; message = noopMessageActions }

    let report (name: string) (ms: float) =
        output.WriteLine(sprintf "PERF %s = %.1f ms" name ms)
        eprintfn "PERF %s = %.1f ms" name ms

    let timed (f: unit -> unit) =
        let w = Stopwatch.StartNew()
        f ()
        Dispatcher.UIThread.RunJobs()
        w.Stop()
        w.Elapsed.TotalMilliseconds

    let mountChat () =
        let view = ChatView(noopActions, fun size -> Border(Width = size, Height = size) :> Control)
        view.Build()
        let window = Window(Width = 1000.0, Height = 760.0, Content = view)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        view, window

    let render (view: ChatView) messages streaming =
        view.RenderMessages(messages, streaming, None, Tokens.fontReading, true, None, Set.empty)

    let warm () =
        // 一次性成本（Markdig / 字体 / 公式引擎）不算进被测场景
        let view, window = mountChat ()
        render view [ for i in 1 .. 4 -> messageAt i ] None
        Dispatcher.UIThread.RunJobs()
        window.Close()

    [<Fact>]
    member _.``打开长对话：快照尾部 200 条到首屏可见``() =
        Headless.run (fun () ->
            warm ()
            let view, window = mountChat ()
            try
                let messages = [ for i in 1 .. 200 -> messageAt i ]
                let ms = timed (fun () -> render view messages None)
                report "open-long-conversation-200" ms
                eprintfn "PERF hidden-after-open = %d" view.HiddenMessageCount
                // 再往前翻两页（共 400 条）：分页回来后的重绘
                let more = [ for i in -199 .. 200 -> messageAt (i + 200) ]
                let ms2 = timed (fun () -> render view more None)
                report "prepend-history-to-400" ms2
                Assert.True(ms < 20000.0, sprintf "打开长对话 %.0f ms" ms)
            finally
                window.Close())

    [<Fact>]
    member _.``长回复流式：每批 delta 的重绘代价``() =
        Headless.run (fun () ->
            warm ()
            let view, window = mountChat ()
            try
                let history = [ for i in 1 .. 20 -> messageAt i ]
                render view history None
                Dispatcher.UIThread.RunJobs()
                let full = String.concat "\n\n" [ for i in 1 .. 40 -> reply (1000 + i) ]
                let steps = 80
                let costs = Collections.Generic.List<float>()
                for s in 1 .. steps do
                    let len = full.Length * s / steps
                    let streaming =
                        { MessageView.empty with role = "assistant"; text = full.Substring(0, len) }
                    costs.Add(timed (fun () -> render view history (Some streaming)))
                let ctx: Wanxiang2.MessageCardView.CardContext =
                    { FontSize = Tokens.fontReading; AutoCollapseReasoning = true; Streaming = true; IsLastAssistant = true
                      Usage = None; MissingAttachments = Set.empty; AwaitingApproval = Set.empty; BrandAvatar = fun () -> Border() :> Control }
                let w = Stopwatch.StartNew()
                for _ in 1 .. 10 do Markdown.parse full |> ignore
                report "probe-parse-20k" (w.Elapsed.TotalMilliseconds / 10.0)
                let w = Stopwatch.StartNew()
                for _ in 1 .. 10 do Cards.render { MessageView.empty with role = "assistant"; text = full } ctx noopMessageActions None |> ignore
                report "probe-card-build-20k" (w.Elapsed.TotalMilliseconds / 10.0)
                let st = Some { MessageView.empty with role = "assistant"; text = full + " 尾" }
                let w = Stopwatch.StartNew()
                render view history st
                report "probe-render-call" w.Elapsed.TotalMilliseconds
                let w = Stopwatch.StartNew()
                window.UpdateLayout()
                report "probe-layout" w.Elapsed.TotalMilliseconds
                let w = Stopwatch.StartNew()
                Dispatcher.UIThread.RunJobs()
                report "probe-runjobs" w.Elapsed.TotalMilliseconds
                let last10 = costs |> Seq.skip (steps - 10) |> Seq.average
                report "stream-delta-avg" (Seq.average costs)
                report "stream-delta-last10-avg" last10
                report "stream-total-80-deltas" (Seq.sum costs)
                Assert.True(last10 < 5000.0, sprintf "流式末段每批 %.0f ms" last10)
            finally
                window.Close())

    [<Fact>]
    member _.``侧栏 2000 个对话：首次填充与刷新``() =
        Headless.run (fun () ->
            let actions: SidebarActions =
                { newConversation = ignore; openConversation = ignore; renameConversation = ignore; deleteConversation = ignore
                  setPinned = (fun _ _ -> ()); setArchived = (fun _ _ -> ()); selectionAllPinned = (fun _ -> false)
                  setPinnedMany = (fun _ _ -> ()); setArchivedMany = (fun _ _ -> ()); deleteMany = ignore
                  duplicateAsFork = ignore; exportConversation = ignore; openSettings = ignore; reconnect = ignore
                  toggleArchivedVisibility = ignore; closeNavigation = ignore }
            let sidebar = Sidebar(OverlayHost(Grid()), actions, SharedTheme.logo)
            sidebar.Build()
            let window = Window(Width = 300.0, Height = 900.0, Content = sidebar)
            window.Show()
            try
                let now = DateTimeOffset.Now
                let items n =
                    [ for i in 1 .. 2000 ->
                        { id = Guid(i, 0s, 0s, Array.zeroCreate 8); title = sprintf "对话 %d：关于项目计划的讨论" i; preview = sprintf "预览 %d %d" i n
                          running = false; pinned = (i % 97 = 0); archived = false
                          createdAt = now.AddMinutes(float -i); updatedAt = now.AddMinutes(float -i); messageCount = 4
                          isFork = false; providerId = "p"; model = "m"; lastCommitId = uint64 (i + n) } ]
                sidebar.SetConnection(true, "已连接")
                // 预热：首次排字 / 模板实例化的一次性成本不算进场景
                sidebar.SetConversations(items 0 |> List.truncate 20)
                Dispatcher.UIThread.RunJobs()
                sidebar.SetConversations []
                Dispatcher.UIThread.RunJobs()
                let first = timed (fun () -> sidebar.SetConversations(items 0))
                report "sidebar-2000-first" first
                let refresh = timed (fun () -> sidebar.SetConversations(items 1))
                report "sidebar-2000-refresh" refresh
                Assert.True(first < 20000.0)
            finally
                window.Close())

    [<Fact>]
    member _.``长对话只挂尾部窗口，滚到顶 / 跳转再往前挂（决策 293）``() =
        Headless.run (fun () ->
            let view, window = mountChat ()
            try
                let messages = [ for i in 1 .. 200 -> messageAt i ]
                render view messages None
                Dispatcher.UIThread.RunJobs()
                Assert.Equal(200 - ChatView.InitialMountWindow, view.HiddenMessageCount)
                // 往上扩一步：本机已加载的消息先挂
                Assert.True(view.ExpandMountWindow())
                Dispatcher.UIThread.RunJobs()
                Assert.Equal(200 - ChatView.InitialMountWindow - ChatView.MountWindowStep, view.HiddenMessageCount)
                // 跳到窗口外的一条：窗口扩到它
                Assert.True(view.ScrollToMessage 3UL)
                Dispatcher.UIThread.RunJobs()
                Assert.True(view.HiddenMessageCount < 3)
                // 换对话：窗口复位
                view.ResetScrollState()
                render view messages None
                Assert.Equal(200 - ChatView.InitialMountWindow, view.HiddenMessageCount)
            finally
                window.Close())

/// 决策 290：主机侧感知性能（非 UI）：打开长对话的快照切片、全文搜索。
module HostBench =
    open System.IO
    open Wanxiang.Core
    open Wanxiang.Store
    open Wanxiang.Tests.Helpers

    let private seed (conversations: int) (longMessages: int) =
        let dir = tempDir ()
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
        let path = DataPaths.logFile dir
        use fs = new BufferedStream(new FileStream(path, FileMode.Create), 1 <<< 20)
        let mutable id = 0UL
        let write evs =
            id <- id + 1UL
            let b = Wanxiang.Store.Assembly.frame ((Events.Commit.create id (DateTimeOffset(day)) evs))
            fs.Write(b, 0, b.Length)
        for n in 1 .. conversations do
            let c = newConversationId ()
            write [ ConversationCreated { conversationId = c; title = sprintf "对话 %d" n; config = testConfig () } ]
            write [ AgentMessageRecorded { conversationId = c; payloadJson = userMessageJson (sprintf "问题 %d needle%d" n n) } ]
            write [ AgentMessageRecorded { conversationId = c; payloadJson = assistantMessageJson (sprintf "回答 %d：一段普通的中文正文，带 `代码` 与公式 $x^2$。" n) } ]
        let longConv = newConversationId ()
        write [ ConversationCreated { conversationId = longConv; title = "超长对话"; config = testConfig () } ]
        for i in 1 .. longMessages do
            write [ AgentMessageRecorded { conversationId = longConv; payloadJson = (if i % 2 = 1 then userMessageJson (sprintf "追问 %d" i) else assistantMessageJson (sprintf "第 %d 段回答，内容是一些没有特别含义的文字。" i)) } ]
        fs.Flush()
        dir, longConv

    let private ms (f: unit -> 'a) =
        f () |> ignore // 预热
        let w = Stopwatch.StartNew()
        for _ in 1 .. 5 do f () |> ignore
        w.Elapsed.TotalMilliseconds / 5.0

    [<Fact>]
    let ``主机：打开 2 万条的长对话（快照尾部 200 条）与 1500 对话全文搜索`` () =
        let dir, longConv = seed 1500 20000
        try
            let proj = (replayOutcome dir).projection
            let conv = proj.conversations[longConv]
            let tail = ms (fun () -> Wanxiang.Server.ServerModel.conversationMessagesTail proj conv 200)
            eprintfn "PERF host-snapshot-tail-200-of-20000 = %.1f ms" tail
            let items, earliest, hasMore = Wanxiang.Server.ServerModel.conversationMessagesTail proj conv 200
            Assert.Equal(200, items.Count)
            Assert.True hasMore
            Assert.True(earliest > 0UL)
            let q text : Wanxiang.Protocol.SearchQueryData =
                { queryId = Guid.NewGuid(); query = text; includeArchived = false; offset = 0; limit = 20 }
            let search text () =
                match Wanxiang.Server.SearchService.search proj (q text) with
                | Ok page -> page
                | Error e -> failwith e
            let rare = ms (search "needle7")
            let common = ms (search "普通")
            eprintfn "PERF host-search-rare = %.1f ms" rare
            eprintfn "PERF host-search-common = %.1f ms" common
            Assert.True(tail < 2000.0 && rare < 5000.0 && common < 5000.0)
        finally
            cleanup dir
