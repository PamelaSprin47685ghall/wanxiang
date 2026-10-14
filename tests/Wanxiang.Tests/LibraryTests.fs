module Wanxiang.Tests.LibraryTests

// W4 对话库：全文搜索（内存投影）、分叉谱系、同一消息的版本切换。
// 验收对应 docs/ux/redesign-proposal.md §W4 的 7 条；大历史分级见文件末尾（LargeHistory）。

open Wanxiang2.SidebarDesk
open System
open System.Diagnostics
open System.IO
open System.Net.WebSockets
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Server
open Wanxiang.UI
open Wanxiang2.MessageCardView
open Wanxiang.Tests
open Wanxiang.Tests.Helpers

let private now = DateTimeOffset.UtcNow

/// 顺序提交构造器：commit id 自增，保证投影严格连续。
type private Builder() =
    let mutable next = 0UL
    let commits = ResizeArray<Events.Commit>()
    member _.Next = next
    member _.Add(ev: EventData) : CommitId =
        next <- next + 1UL
        commits.Add(Events.Commit.create next now [ ev ])
        next
    member this.Conversation(title: string) : Guid =
        let id = Guid.NewGuid()
        this.Add(ConversationCreated { conversationId = id; title = title; config = testConfig () }) |> ignore
        id
    member this.User(conv: Guid, text: string) : CommitId =
        this.Add(AgentMessageRecorded { conversationId = conv; payloadJson = userMessageJson text })
    member this.Assistant(conv: Guid, text: string) : CommitId =
        this.Add(AgentMessageRecorded { conversationId = conv; payloadJson = assistantMessageJson text })
    member this.Fork(parent: Guid, after: CommitId option) : Guid =
        let id = Guid.NewGuid()
        this.Add(ConversationForked { conversationId = id; parentConversationId = parent; forkAfterId = after }) |> ignore
        id
    member this.Delete(conv: Guid) = this.Add(EventData.ConversationDeleted { conversationId = conv }) |> ignore
    member this.DeleteMessage(conv: Guid, commit: CommitId) =
        this.Add(MessageDeleted { conversationId = conv; messageCommitId = commit }) |> ignore
    member this.Archive(conv: Guid) =
        this.Add(ConversationFlagsChanged { conversationId = conv; pinned = false; archived = true }) |> ignore
    member _.Commits = List.ofSeq commits
    member this.Projection : Projection =
        this.Commits
        |> List.fold
            (fun p c ->
                match Projection.applyCommit p c with
                | Ok p -> p
                | Error e -> failwith (WanxiangError.message e))
            Projection.empty

let private query (text: string) : SearchQueryData =
    { queryId = Guid.NewGuid(); query = text; includeArchived = false; offset = 0; limit = 20 }

let private run (proj: Projection) (q: SearchQueryData) : SearchResultsData =
    match SearchService.search proj q with
    | Ok page -> page
    | Error e -> failwith e

let private str (o: JsonNode) (key: string) = o[key].GetValue<string>()

// ---------------------------------------------------------------------------
// 验收 1：500 个对话，罕见词只命中包含它的对话，片段含该词，命中位置正确
// ---------------------------------------------------------------------------

[<Fact>]
let ``search over 500 conversations returns only the conversation containing the rare word`` () =
    let b = Builder()
    let mutable rare = Guid.Empty
    for n in 1 .. 500 do
        let c = b.Conversation(sprintf "对话 %d" n)
        b.User(c, sprintf "普通问题 %d：今天天气怎么样，聊聊旅行与美食" n) |> ignore
        if n = 321 then
            rare <- c
            b.Assistant(c, "前面是一大段铺垫文字，用来让命中的位置离开开头。" + String('x', 200) + " 这里有一个罕见词 Quetzalcoatl 出现在正文里 " + String('y', 200)) |> ignore
        else
            b.Assistant(c, sprintf "回答 %d：这是一个很普通的回答。" n) |> ignore
    let page = run b.Projection (query "quetzalcoatl")
    Assert.Equal(1, page.items.Count)
    let hit = page.items[0]
    Assert.Equal(rare.ToString("D"), str hit "conversationId")
    let snippet = str hit "snippet"
    Assert.Contains("Quetzalcoatl", snippet)
    let start, length = hit["matchStart"].GetValue<int>(), hit["matchLength"].GetValue<int>()
    Assert.Equal("quetzalcoatl", snippet.Substring(start, length).ToLowerInvariant())
    Assert.Equal("assistant", str hit "role")
    Assert.False page.truncated
    Assert.False page.hasMore
    // 片段是窗口，不是整条消息
    Assert.True(snippet.Length <= SearchLimits.snippetChars + 4)
    // 多词按 AND：两个词都在同一条消息里才算
    Assert.Equal(1, (run b.Projection (query "quetzalcoatl 铺垫")).items.Count)
    Assert.Equal(0, (run b.Projection (query "quetzalcoatl 今天天气")).items.Count)

[<Fact>]
let ``search matches case-insensitively and ignores reasoning and tool payloads`` () =
    let b = Builder()
    let c = b.Conversation "t"
    let payload = JsonObject()
    payload["role"] <- "assistant"
    let contents = JsonArray()
    let reasoning = JsonObject()
    reasoning["$type"] <- "reasoning"
    reasoning["text"] <- "隐藏的思维链 secretword"
    contents.Add reasoning
    let tool = JsonObject()
    tool["$type"] <- "functionResult"
    tool["result"] <- "工具结果 toolword"
    contents.Add tool
    let text = JsonObject()
    text["$type"] <- "text"
    text["text"] <- "可见正文 VisibleWord"
    contents.Add text
    payload["contents"] <- contents
    b.Add(AgentMessageRecorded { conversationId = c; payloadJson = payload }) |> ignore
    let proj = b.Projection
    Assert.Equal(1, (run proj (query "visibleword")).items.Count)
    Assert.Equal(0, (run proj (query "secretword")).items.Count)
    Assert.Equal(0, (run proj (query "toolword")).items.Count)

// ---------------------------------------------------------------------------
// 验收 2：搜索不改动日志、不创建文件
// ---------------------------------------------------------------------------

let private directorySnapshot (dir: string) =
    Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories)
    |> Array.sort
    |> Array.map (fun p ->
        if File.Exists p then p, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes p)) else p, "<dir>")

[<Fact>]
let ``search neither touches the log directory nor the projection`` () =
    let dir = tempDir ()
    try
        let b = Builder()
        let c = b.Conversation "t"
        b.User(c, "needle in a haystack") |> ignore
        // 与真实数据目录同形的日志夹具：搜索前后逐字节一致
        Wanxiang.Store.DataPaths.ensureDataDirs dir
        let path = Wanxiang.Store.DataPaths.logFile dir
        File.WriteAllBytes(
            path,
            b.Commits
            |> List.map (fun c -> Wanxiang.Store.Assembly.frame c)
            |> Array.concat)
        let proj = b.Projection
        let before = directorySnapshot dir
        let page = run proj (query "needle")
        Assert.Equal(1, page.items.Count)
        Assert.Equal<(string * string) array>(before, directorySnapshot dir)
        Assert.Equal(proj.latestCommitId, page.atCommitId)
        Assert.Equal(2, (Projection.tryConversation proj c |> Option.get).messages.Length + 1)
    finally
        cleanup dir

// ---------------------------------------------------------------------------
// 验收 3：tombstone 消息/对话不出现；归档对话可选
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// 验收 4：超上限返回“已截断”，不静默丢；上限来自预算
// ---------------------------------------------------------------------------

[<Fact>]
let ``a small result set is complete and not truncated`` () =
    let b = Builder()
    let c = b.Conversation "t"
    for k in 1 .. 7 do b.User(c, sprintf "needle %d" k) |> ignore
    let page = run b.Projection { query "needle" with limit = 5 }
    Assert.Equal(5, page.items.Count)
    Assert.True page.hasMore
    Assert.False page.truncated
    let second = run b.Projection { query "needle" with limit = 5; offset = 5 }
    Assert.Equal(2, second.items.Count)
    Assert.False second.hasMore
    // 同一对话内新消息优先
    Assert.Equal("needle 7", str page.items[0] "snippet")

// ---------------------------------------------------------------------------
// 协议：编解码往返、旧客户端兼容
// ---------------------------------------------------------------------------

[<Fact>]
let ``search events roundtrip through the wire codec and the AG-UI carrier`` () =
    let q = { query "查询 词" with includeArchived = true; offset = 40; limit = 10 }
    match WireAgui.tryDecode (WireAgui.encode (SearchQuery q)) with
    | Ok(Some(SearchQuery d)) -> Assert.Equal(q, d)
    | other -> failwithf "bad query roundtrip: %A" other
    let items = JsonArray()
    let item = JsonObject()
    item["conversationId"] <- Guid.NewGuid().ToString("D")
    item["messageId"] <- 7UL
    item["snippet"] <- "abc"
    items.Add item
    let results =
        { queryId = q.queryId; query = q.query; offset = 40; items = items; hasMore = true; truncated = true; atCommitId = 99UL }
    match WireAgui.tryDecode (WireAgui.encode (SearchResults results)) with
    | Ok(Some(SearchResults d)) ->
        Assert.Equal(q.queryId, d.queryId)
        Assert.True d.hasMore
        Assert.True d.truncated
        Assert.Equal(99UL, d.atCommitId)
        Assert.Equal(40, d.offset)
        Assert.Equal(1, d.items.Count)
    | other -> failwithf "bad results roundtrip: %A" other
    match WireAgui.tryDecode (WireAgui.encode (SearchFailed {| queryId = q.queryId; message = "不行" |})) with
    | Ok(Some(SearchFailed d)) -> Assert.Equal("不行", d.message)
    | other -> failwithf "bad failed roundtrip: %A" other
    Assert.Equal("search.query", WireEvent.typeName (SearchQuery q))
    // 缺字段的旧写法：offset/limit/includeArchived 缺省
    match WireCodec.tryDecode (sprintf """{"type":"search.query","payload":{"queryId":"%O","query":"x"}}""" q.queryId) with
    | Ok(SearchQuery d) ->
        Assert.False d.includeArchived
        Assert.Equal(0, d.offset)
        Assert.Equal(SearchLimits.defaultPageSize, d.limit)
    | other -> failwithf "bad minimal query: %A" other

// ---------------------------------------------------------------------------
// 端到端：真主机 + WebSocket；搜索只读，日志条数不变；旧客户端不发搜索，行为不变
// ---------------------------------------------------------------------------

[<Fact>]
let ``e2e search over the wire is read-only and an old client never sees search events`` () =
    let port, token = ServerE2eTests.pickPort (), ClientToken.ClientToken.generate ()
    let b = Builder()
    for n in 1 .. 30 do
        let c = b.Conversation(sprintf "对话 %d" n)
        b.User(c, if n = 17 then "线上搜索 Zanzibar 目标" else sprintf "普通消息 %d" n) |> ignore
    let server = ServerE2eTests.E2e.ServerHandle(port, token, initialCommits = b.Commits)
    try
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 20.0)
        let ct = timeout.Token
        use ws = ServerE2eTests.E2e.conn port token ct
        let wait name = ServerE2eTests.E2e.waitFor ws ct (fun o -> o["type"].GetValue<string>() = name)
        wait "auth.accepted" |> ignore
        let before = server.CommitCount
        let q = query "zanzibar"
        ServerE2eTests.E2e.send ws (JsonNode.Parse(WireCodec.encode (SearchQuery q)).AsObject()) ct
        let ev = wait "search.results"
        match WireCodec.tryDecode (ev.ToJsonString()) with
        | Ok(SearchResults page) ->
            Assert.Equal(q.queryId, page.queryId)
            Assert.Equal(1, page.items.Count)
            Assert.Contains("Zanzibar", str page.items[0] "snippet")
            Assert.False page.truncated
        | other -> failwithf "bad results: %A" other
        // 超长查询：明确失败，连接仍可用
        let longQuery = { query (String('q', 500)) with queryId = Guid.NewGuid() }
        ServerE2eTests.E2e.send ws (JsonNode.Parse(WireCodec.encode (SearchQuery longQuery)).AsObject()) ct
        let failed = wait "search.failed"
        let failedPayload = failed["payload"].AsObject()
        let failedMessage = failedPayload["message"].GetValue<string>()
        Assert.Contains("太长", failedMessage)
        Assert.Equal(before, server.CommitCount)
        // 旧客户端路径：只观察列表，拿到的仍是原有字段（新增谱系字段缺省），事件序不变
        ServerE2eTests.E2e.send ws (JsonNode.Parse(WireCodec.encode ObserveConversationList).AsObject()) ct
        let list = wait "conversation-list.snapshot"
        let listPayload = list["payload"].AsObject()
        let rowItems = listPayload["items"].AsArray()
        let row = rowItems[0].AsObject()
        Assert.False(row.ContainsKey("parentId"))
        Assert.True(row.ContainsKey("isFork"))
    finally
        server.Dispose()

// ---------------------------------------------------------------------------
// 验收 5：谱系（多层分叉、父对话已删除）
// ---------------------------------------------------------------------------

/// 根 R：u2 a3 u4 a5；F1 从 a3 后分叉（编辑 u4）；G 从 F1 的第一条自有消息后分叉。
let private forkFixture () =
    let b = Builder()
    let r = b.Conversation "根对话"
    let _u2 = b.User(r, "问题一")
    let a3 = b.Assistant(r, "回答一")
    let _u4 = b.User(r, "问题二（原文）")
    let _a5 = b.Assistant(r, "回答二")
    let f1 = b.Fork(r, Some a3)
    let f1u = b.User(f1, "问题二（改写 1）")
    let f1a = b.Assistant(f1, "回答二（改写 1）")
    let g = b.Fork(f1, Some f1a)
    let _gu = b.User(g, "追问")
    b, r, f1, g, a3, f1u

let private listRows (proj: Projection) =
    ServerModel.conversationListItems proj (fun _ -> "idle")

let private byId (rows: JsonArray) (id: Guid) =
    rows |> Seq.pick (fun n -> if n["conversationId"].GetValue<string>() = id.ToString("D") then Some(n.AsObject()) else None)

// ---------------------------------------------------------------------------
// 验收 6：同一消息的版本 n/m 与切换
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// UI：侧栏命中、面包屑、版本切换条
// ---------------------------------------------------------------------------

let rec private descendantsOf (control: Control) : seq<Control> =
    seq {
        yield control
        for child in control.GetVisualDescendants() do
            match child with
            | :? Control as c -> yield c
            | _ -> ()
    }

let private invoke (c: Control) =
    let peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement c
    let provider = Assert.IsAssignableFrom<Avalonia.Automation.Provider.IInvokeProvider>(peer)
    provider.Invoke()
    Dispatcher.UIThread.RunJobs()

let private nameOf (c: Control) = Avalonia.Automation.AutomationProperties.GetName c

let private sidebarActions : Wanxiang2.SidebarDesk.SidebarDeskActions =
    { newConversation = ignore
      openConversation = ignore
      deleteConversation = ignore
      renameConversation = ignore
      setPinned = fun _ _ -> ()
      setArchived = fun _ _ -> ()
      selectionAllPinned = fun _ -> false
      setPinnedMany = fun _ _ -> ()
      setArchivedMany = fun _ _ -> ()
      deleteMany = fun _ -> ()
      duplicateAsFork = ignore
      exportConversation = ignore
      openSettings = ignore
      reconnect = ignore
      toggleArchivedVisibility = ignore
      closeNavigation = ignore }

let private summary (id: Guid) (title: string) : ConversationSummary =
    { id = id; title = title; preview = "预览"; running = false; pinned = false; archived = false
      createdAt = DateTimeOffset.Now; updatedAt = DateTimeOffset.Now; messageCount = 2; isFork = false
      providerId = "openai"; model = "m"; lastCommitId = 1UL }

let private pump (ms: float) =
    let watch = Stopwatch.StartNew()
    while watch.Elapsed.TotalMilliseconds < ms do
        Thread.Sleep 5
        Dispatcher.UIThread.RunJobs()

/// DispatcherTimer 在无显示测试里不可确定泵出（同 SelectionOperabilityTests 的注释）：
/// 直接触发防抖到期后的动作。
let private fireSearch (sidebar: Wanxiang2.SidebarDesk.SidebarDesk) =
    sidebar.BeginMessageSearch()
    Dispatcher.UIThread.RunJobs()

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

[<Trait("Category", "UI")>]
[<Fact>]
let ``chat header shows the fork breadcrumb and clicking it returns to the parent`` () =
    Headless.run (fun () ->
        let chat = ChatView(chatActions, fun _ -> Border() :> Control)
        chat.Build()
        chat.SetConversationChrome true
        chat.SetTitle("子", true)
        let parent = Guid.NewGuid()
        let mutable opened: Guid option = None
        let crumb = { parentId = parent; parentAvailable = true; text = "来自「根对话」第 3 条消息的分叉"; description = "回到原对话" }
        chat.SetBreadcrumb(Some crumb, fun id -> opened <- Some id)
        let window = Window(Width = 800.0, Height = 600.0, Content = chat)
        window.Show()
        try
            Dispatcher.UIThread.RunJobs()
            Assert.Equal(crumb.text, chat.BreadcrumbText)
            Assert.True(chat.ActivateBreadcrumb())
            Assert.Equal(Some parent, opened)
            // 父对话已删除：文案固定，不可点击，不跳转
            opened <- None
            chat.SetBreadcrumb(Some { crumb with parentAvailable = false; text = "原对话已删除" }, fun id -> opened <- Some id)
            Assert.Equal("原对话已删除", chat.BreadcrumbText)
            Assert.False(chat.ActivateBreadcrumb())
            Assert.True opened.IsNone
            // 根对话：面包屑消失
            chat.SetBreadcrumb(None, ignore)
            Assert.Equal("", chat.BreadcrumbText)
        finally
            window.Close())

[<Trait("Category", "UI")>]
[<Fact>]
let ``sibling bar shows one-of-three on the edited user message and switches versions`` () =
    Headless.run (fun () ->
        let chat = ChatView(chatActions, fun _ -> Border() :> Control)
        chat.Build()
        chat.SetConversationChrome true
        let targets = [ Guid.NewGuid(); Guid.NewGuid(); Guid.NewGuid() ]
        let mutable selected: Guid option = None
        let mutable asked: (uint64 * uint64 option) list = []
        chat.SiblingProvider <-
            fun commit prev ->
                asked <- (commit, prev) :: asked
                if commit = 4UL then Some { index = 0; count = 3; targets = targets } else None
        chat.SiblingSelected <- fun id -> selected <- Some id
        let window = Window(Width = 800.0, Height = 700.0, Content = chat)
        window.Show()
        try
            chat.RenderMessages(
                [ view "user" "开场" 2UL; view "assistant" "回答" 3UL; view "user" "原文" 4UL; view "assistant" "原回答" 5UL ],
                None, None, 14.0, false, None, Set.empty)
            Dispatcher.UIThread.RunJobs()
            // 前一条消息按原始顺序取：commit 4 的前一条是 3
            Assert.Contains((4UL, Some 3UL), asked)
            Assert.Contains((2UL, None), asked)
            Assert.Contains(descendantsOf chat, fun c -> nameOf c = "这条消息的版本 1 / 3")
            Assert.Equal(1, descendantsOf chat |> Seq.filter (fun c -> nameOf c = "这条消息的版本 1 / 3" && (c :? TextBlock)) |> Seq.length)
            let prev = descendantsOf chat |> Seq.find (fun c -> nameOf c = "上一个版本")
            let next = descendantsOf chat |> Seq.find (fun c -> nameOf c = "下一个版本")
            Assert.False prev.IsEnabled
            Assert.True next.IsEnabled
            invoke next
            Dispatcher.UIThread.RunJobs()
            Assert.Equal(Some targets[1], selected)
            // 版本变化时卡片被重建（缓存键含版本）：换成 2/3 后文案更新
            chat.SiblingProvider <- fun commit _ -> if commit = 4UL then Some { index = 1; count = 3; targets = targets } else None
            chat.RenderMessages(
                [ view "user" "开场" 2UL; view "assistant" "回答" 3UL; view "user" "原文" 4UL; view "assistant" "原回答" 5UL ],
                None, None, 14.0, false, None, Set.empty)
            Dispatcher.UIThread.RunJobs()
            Assert.Contains(descendantsOf chat, fun c -> nameOf c = "这条消息的版本 2 / 3")
            // 定位：已加载的消息可滚动到，更早的不在窗口里返回 false
            Assert.True(chat.ScrollToMessage 4UL)
            Assert.False(chat.ScrollToMessage 99UL)
        finally
            window.Close())

// ---------------------------------------------------------------------------
// 大历史：内存与延迟（LargeHistory 分级；默认不进 PR gate，见 run-tests.sh --large-history）
// ---------------------------------------------------------------------------

/// n 个对话 × perConv 条消息；最老的对话里放一个唯一词。
let private bigProjection (conversations: int) (perConv: int) (rareInOldest: string) : Projection =
    let b = Builder()
    for n in 1 .. conversations do
        let c = b.Conversation(sprintf "c%d" n)
        for k in 1 .. perConv do
            let text =
                if n = 1 && k = 1 then sprintf "最老的一条：%s。" rareInOldest
                else sprintf "对话 %d 的第 %d 条普通消息，内容是一些没有特别含义的文字" n k
            b.User(c, text) |> ignore
    b.Projection

[<Fact>]
let ``medium history: 20k messages search stays well under a second`` () =
    let proj = bigProjection 400 50 "uniquetoken20k"
    let watch = Stopwatch.StartNew()
    let page = run proj (query "uniquetoken20k")
    watch.Stop()
    Assert.Equal(1, page.items.Count)
    Assert.False page.truncated
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds 3.0, sprintf "20k 条消息搜索耗时 %A" watch.Elapsed)

[<Fact>]
[<Trait("Category", "LargeHistory")>]
let ``large history: 300k messages are scanned within budget, flagged truncated, and memory stays bounded`` () =
    // 3000 × 100 = 300k 条 > maxScannedMessages(200k)：最老对话里的唯一词落在预算之外——
    // 必须明说“已截断”，而不是静默返回空。
    let proj = bigProjection 3000 100 "uniquetoken300k"
    GC.Collect()
    let baseline = GC.GetTotalMemory true
    let watch = Stopwatch.StartNew()
    let page = run proj (query "uniquetoken300k")
    watch.Stop()
    let afterBytes = GC.GetTotalMemory false
    printfn "large-history search: %A elapsed, +%d KiB heap" watch.Elapsed ((afterBytes - baseline) / 1024L)
    Assert.Equal(0, page.items.Count)
    Assert.True(page.truncated, "扫描预算耗尽必须标记截断")
    // 延迟：只扫预算内的 20 万条
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds 30.0, sprintf "搜索耗时 %A" watch.Elapsed)
    // 内存：搜索不持有投影之外的大对象（只有一页命中 + 临时字符串）
    Assert.True(afterBytes - baseline < 256L * 1024L * 1024L, sprintf "搜索多占用 %d MiB" ((afterBytes - baseline) / 1048576L))
    // 最近的对话优先：只出现在最近对话里的词，在预算内即命中；
    // 因为总量超出扫描预算，无法证明没有更老的命中，所以仍如实标记截断。
    let recent = run proj (query "对话 3000 的第 100 条")
    Assert.Equal(1, recent.items.Count)
    Assert.True recent.truncated

[<Fact>]
let ``同一父对话的多个分叉标题可区分（第 207 节）`` () =
    let b = Builder()
    let r = b.Conversation "旅行计划"
    let _u = b.User(r, "问题")
    let a = b.Assistant(r, "回答")
    let f1 = b.Fork(r, Some a)
    let f2 = b.Fork(r, Some a)
    let f3 = b.Fork(r, Some a)
    let title id = b.Projection.conversations[id].title
    Assert.Equal("旅行计划（分叉）", title f1)
    Assert.Equal("旅行计划（分叉 2）", title f2)
    Assert.Equal("旅行计划（分叉 3）", title f3)
