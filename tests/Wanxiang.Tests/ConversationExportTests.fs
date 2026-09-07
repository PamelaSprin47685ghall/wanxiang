module Wanxiang.Tests.ConversationExportTests

open System
open System.Reflection
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Server
open Wanxiang.Tests.Helpers
open Wanxiang.UI

let private flags = BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public

[<Fact>]
let ``export of an unopened conversation has its own progress view without navigating`` () =
    Headless.ensure ()
    let view = MainView()
    let invoke name args = typeof<MainView>.GetMethod(name, flags).Invoke(view, args)
    for name in [ "BuildSidebar"; "BuildChat"; "BuildComposer"; "BuildSettings" ] do
        invoke name [||] |> ignore
    typeof<MainView>.GetField("authenticated", flags).SetValue(view, true)
    let selected = Guid.NewGuid()
    typeof<MainView>.GetField("activeConvId", flags).SetValue(view, Some selected)
    let summary: ConversationSummary =
        { id = Guid.NewGuid(); title = "从未打开的长会话"; preview = ""; running = false
          pinned = false; archived = false; createdAt = DateTimeOffset.UtcNow; messageCount = 450
          isFork = false; providerId = "mock"; model = "mock"; lastCommitId = 451UL }
    invoke "ExportConversation" [| box summary |] |> ignore
    let overlay = typeof<MainView>.GetField("overlay", flags).GetValue(view) :?> OverlayHost
    Assert.True(overlay.IsDialogOpen, "导出必须有独立进度／失败视图，而不是要求用户先加载会话。")
    Assert.Equal(Some selected, typeof<MainView>.GetField("activeConvId", flags).GetValue(view) :?> Guid option)
    overlay.CloseDialog()

let private append events (proj: Projection) =
    let commit = Events.Commit.create (proj.latestCommitId + 1UL) DateTimeOffset.UtcNow events
    match Projection.applyCommit proj commit with
    | Ok updated -> updated
    | Error error -> failwith (WanxiangError.message error)

let private fixture count =
    let id = Guid.NewGuid()
    let mutable proj = Projection.empty |> append [ ConversationCreated { conversationId = id; title = "完整历史"; config = testConfig () } ]
    for n in 1 .. count do
        proj <- proj |> append [ AgentMessageRecorded { conversationId = id; payloadJson = userMessageJson (sprintf "message-%04d" n) } ]
    proj, id

let private page proj query =
    match ServerModel.exportPage proj query with Ok result -> result | Error error -> failwith error

let private complete proj (controller: ConversationExportController) query =
    let mutable next = Some query
    let mutable pages = 0
    while next.IsSome do
        pages <- pages + 1
        Assert.True(pages < 100, "分页必须终止")
        next <- controller.Accept(page proj next.Value)
    Assert.True(controller.Document.IsSome, sprintf "%A" controller.Status)
    controller.Document.Value

[<Fact>]
let ``export retrieves all 450 messages while normal snapshot still contains only 200`` () =
    let proj, id = fixture 450
    let normal, _, more = ServerModel.conversationMessagesTail proj proj.conversations[id] 200
    Assert.Equal(200, normal.Count)
    Assert.True more
    let controller = ConversationExportController id
    let document = complete proj controller (controller.Start())
    Assert.Equal(450, document.messages.Length)
    Assert.Equal("message-0001", document.messages.Head.text)
    Assert.Equal("message-0450", document.messages |> List.last |> _.text)
    Assert.Equal<CommitId list>([ 2UL .. 451UL ], document.messages |> List.choose _.commitId)

[<Fact>]
let ``later deletion insertion and renaming cannot alter an export already reading`` () =
    let original, id = fixture 250
    let controller = ConversationExportController id
    let first = page original (controller.Start())
    let next = controller.Accept first |> Option.get
    Assert.True controller.Document.IsNone
    let changed =
        original
        |> append [ MessageDeleted { conversationId = id; messageCommitId = 2UL } ]
        |> append [ AgentMessageRecorded { conversationId = id; payloadJson = userMessageJson "不能混入" } ]
        |> append [ ConversationRenamed { conversationId = id; title = "后来的标题" } ]
    let document = complete changed controller next
    Assert.Equal("完整历史", document.title)
    Assert.Equal(original.latestCommitId, document.atCommitId)
    Assert.Equal(250, document.messages.Length)
    Assert.Equal("message-0001", document.messages.Head.text)
    Assert.DoesNotContain(document.messages, fun message -> message.text = "不能混入")

[<Fact>]
let ``deletions before export stay hidden even on older pages`` () =
    let original, id = fixture 350
    let proj = original |> append [ MessageDeleted { conversationId = id; messageCommitId = 3UL } ]
    let controller = ConversationExportController id
    let document = complete proj controller (controller.Start())
    Assert.Equal(349, document.messages.Length)
    Assert.DoesNotContain(document.messages, fun message -> message.commitId = Some 3UL)

[<Fact>]
let ``nested fork export preserves its frozen ancestors after parent deletion`` () =
    let root, parentId = fixture 250
    let childId, grandchildId = Guid.NewGuid(), Guid.NewGuid()
    let withChild = root |> append [ ConversationForked { conversationId = childId; parentConversationId = parentId; forkAfterId = Some 221UL } ]
    let withMessage = withChild |> append [ AgentMessageRecorded { conversationId = childId; payloadJson = userMessageJson "子分支" } ]
    let proj =
        withMessage
        |> append [ ConversationForked { conversationId = grandchildId; parentConversationId = childId; forkAfterId = Some withMessage.latestCommitId } ]
        |> append [ AgentMessageRecorded { conversationId = grandchildId; payloadJson = userMessageJson "孙分支" } ]
        |> append [ MessageDeleted { conversationId = parentId; messageCommitId = 2UL } ]
        |> append [ EventData.ConversationDeleted { conversationId = parentId } ]
    let controller = ConversationExportController grandchildId
    let document = complete proj controller (controller.Start())
    Assert.Equal(222, document.messages.Length)
    Assert.Equal("message-0001", document.messages.Head.text)
    Assert.Equal<string list>([ "子分支"; "孙分支" ], document.messages |> List.skip 220 |> List.map _.text)

[<Fact>]
let ``empty conversation can be exported as an empty transcript`` () =
    let proj, id = fixture 0
    let controller = ConversationExportController id
    let document = complete proj controller (controller.Start())
    Assert.Empty document.messages
    Assert.Contains("完整历史", Export.toMarkdown document.title document.messages)

[<Fact>]
let ``cancel retry and disconnect cannot expose partial or stale documents`` () =
    let proj, id = fixture 250
    let controller = ConversationExportController id
    let first = page proj (controller.Start())
    controller.Accept first |> ignore
    controller.Cancel()
    Assert.True(controller.Accept(first).IsNone)
    Assert.True controller.Document.IsNone
    let restarted = controller.Start()
    Assert.NotEqual(first.exportId, restarted.exportId)
    Assert.True(controller.Accept(first).IsNone)
    Assert.Equal(ExportReading(0, None), controller.Status)
    controller.Accept(page proj restarted) |> ignore
    controller.Disconnect()
    Assert.True controller.Document.IsNone
    match controller.Status with ExportFailed _ -> () | other -> failwithf "%A" other

[<Theory>]
[<InlineData("short")>]
[<InlineData("empty")>]
[<InlineData("duplicate")>]
[<InlineData("foreign")>]
[<InlineData("boundary")>]
let ``invalid export pages never become saveable`` mode =
    let proj, id = fixture 250
    let controller = ConversationExportController id
    let first = page proj (controller.Start())
    let invalid =
        match mode with
        | "short" -> { first with hasMore = false }
        | "empty" -> { first with items = JsonArray() }
        | "foreign" -> { first with conversationId = Guid.NewGuid() }
        | "boundary" -> { first with atCommitId = 1UL }
        | _ -> { first with items = JsonArray(first.items[0].DeepClone(), first.items[0].DeepClone()) }
    Assert.True(controller.Accept(invalid).IsNone)
    Assert.True controller.Document.IsNone
    match controller.Status with ExportFailed _ -> () | other -> failwithf "%A" other

[<Fact>]
let ``changed watermark or total aborts a multi-page export`` () =
    let proj, id = fixture 250
    let alterations: (ConversationExportPageData -> ConversationExportPageData) list =
        [ (fun p -> { p with atCommitId = p.atCommitId + 1UL })
          (fun p -> { p with totalMessages = p.totalMessages + 1 }) ]
    for alter in alterations do
        let controller = ConversationExportController id
        let next = controller.Accept(page proj (controller.Start())) |> Option.get
        controller.Accept(alter (page proj next)) |> ignore
        Assert.True controller.Document.IsNone
        match controller.Status with ExportFailed _ -> () | other -> failwithf "%A" other

[<Fact>]
let ``timeout and size limit stop without offering a partial file`` () =
    let proj, id = fixture 250
    let small = ConversationExportController(id, maxBytes = 100L)
    small.Accept(page proj (small.Start())) |> ignore
    Assert.True small.Document.IsNone
    match small.Status with ExportFailed _ -> () | other -> failwithf "%A" other
    let slow = ConversationExportController id
    slow.Start() |> ignore
    Assert.True(slow.Expire(DateTimeOffset.UtcNow.AddMinutes 1.0, TimeSpan.FromSeconds 30.0))
    Assert.True slow.Document.IsNone

[<Fact>]
let ``page byte budget splits large messages without splitting or truncating a message`` () =
    let initial, id = fixture 0
    let text = String('x', 200000)
    let proj = [ 1 .. 5 ] |> List.fold (fun proj _ -> proj |> append [ AgentMessageRecorded { conversationId = id; payloadJson = userMessageJson text } ]) initial
    let controller = ConversationExportController id
    let query = controller.Start()
    let first = page proj query
    Assert.InRange(first.items.Count, 1, 2)
    Assert.True first.hasMore
    let document = complete proj controller query
    Assert.Equal(5, document.messages.Length)
    Assert.All(document.messages, fun message -> Assert.Equal(text, message.text))

[<Fact>]
let ``oversized message fails explicitly rather than returning a shortened message`` () =
    let initial, id = fixture 0
    let proj = initial |> append [ AgentMessageRecorded { conversationId = id; payloadJson = userMessageJson (String('x', ConversationExportLimits.maxMessageBytes)) } ]
    let query = (ConversationExportController id).Start()
    match ServerModel.exportPage proj query with
    | Error message -> Assert.Contains("没有截断", message)
    | Ok _ -> failwith "oversized export should fail"

[<Fact>]
let ``missing deleted and future-watermark exports are rejected`` () =
    let proj, id = fixture 1
    let query = (ConversationExportController id).Start()
    for source, query in
        [ proj, { query with conversationId = Guid.NewGuid() }
          (proj |> append [ EventData.ConversationDeleted { conversationId = id } ]), query
          proj, { query with atCommitId = Some(proj.latestCommitId + 10UL) } ] do
        Assert.True(Result.isError (ServerModel.exportPage source query))

[<Fact>]
let ``export wire roundtrips and cannot advance the chat cursor`` () =
    let proj, id = fixture 1
    let query = (ConversationExportController id).Start()
    let response = page proj query
    for event in [ ConversationExportRead query; ConversationExportPage response; ConversationExportFailed {| exportId = query.exportId; message = "读取失败" |} ] do
        let encoded = WireCodec.encode event
        match WireCodec.tryDecode encoded with
        | Ok decoded -> Assert.Equal(encoded, WireCodec.encode decoded)
        | Error error -> failwith error
        let state = Wanxiang.Client.ClientState()
        state.Handle event
        Assert.Equal(0UL, state.Cursor)
        Assert.Empty state.Conversations
    let invalid = JsonNode.Parse(WireCodec.encode(ConversationExportPage response)).AsObject()
    invalid["payload"].AsObject().Remove "hasMore" |> ignore
    Assert.True(Result.isError (WireCodec.tryDecode(invalid.ToJsonString())))

[<Fact>]
let ``Markdown keeps orphan tool results timestamps and embedded fences`` () =
    let timestamp = DateTimeOffset.Parse "2026-09-07T12:00:00Z"
    let tool = { MessageView.empty with role = "tool"; toolResults = [ "orphan", "result with ``` inside" ]; committedAt = Some timestamp }
    let markdown = Export.toMarkdown "工具记录" [ tool ]
    Assert.Contains("## 工具", markdown)
    Assert.Contains("result with ``` inside", markdown)
    Assert.Contains("````json", markdown)
    Assert.Contains("2026-09-07", markdown)

[<Fact>]
let ``unrenderable messages retain their raw content in export`` () =
    let initial, id = fixture 0
    let proj = initial |> append [ AgentMessageRecorded { conversationId = id; payloadJson = JsonNode.Parse("""{"role":"assistant","contents":[{"type":"future-content","value":"keep-me"}]}""") } ]
    let controller = ConversationExportController id
    let document = complete proj controller (controller.Start())
    Assert.Contains("keep-me", Export.toMarkdown document.title document.messages)

[<Fact>]
let ``export filenames stay a bounded filename including long emoji titles`` () =
    Assert.Equal("会话.md", Export.safeFileName "..")
    let name = Export.safeFileName "../a\\b\nfile"
    Assert.DoesNotContain("/", name)
    Assert.DoesNotContain("\\", name)
    Assert.DoesNotContain("\n", name)
    let emoji = Export.safeFileName(String.replicate 100 "👩🏽‍💻")
    Assert.True(Encoding.UTF8.GetByteCount emoji <= 223)
    let baseName = emoji.Substring(0, emoji.Length - 3)
    Assert.Equal(baseName, String.replicate (Globalization.StringInfo.ParseCombiningCharacters(baseName).Length) "👩🏽‍💻")

[<Theory>]
[<InlineData(390.0)>]
[<InlineData(900.0)>]
let ``export save action appears only after the last page and fits the window`` width =
    Headless.ensure ()
    let proj, id = fixture 250
    let root = Grid()
    let overlay = OverlayHost root
    let window = Window(Content = root, Width = width, Height = 600.0)
    let requested = ResizeArray<ConversationExportQuery>()
    let dialog = ConversationExportDialog(overlay, id, "完整导出", (fun query -> requested.Add query; Task.FromResult true), fun () -> window :> TopLevel)
    window.Show()
    try
        dialog.Show()
        Dispatcher.UIThread.RunJobs()
        let save = root.GetVisualDescendants() |> Seq.choose (function :? Control as c -> Some c | _ -> None) |> Seq.find (fun c -> AutomationProperties.GetName c = "保存 Markdown")
        Assert.False save.IsVisible
        dialog.Handle(page proj requested[0])
        Dispatcher.UIThread.RunJobs()
        Assert.False save.IsVisible
        dialog.Handle(page proj requested[1])
        Assert.False save.IsVisible
        dialog.Handle(page proj requested[2])
        Dispatcher.UIThread.RunJobs()
        Assert.True save.IsVisible
        Assert.True save.IsEnabled
        let origin = save.TranslatePoint(Point(0.0, 0.0), root).Value
        Assert.True(origin.X >= 0.0 && origin.X + save.Bounds.Width <= root.Bounds.Width + 0.5)
        Assert.True(origin.Y >= 0.0 && origin.Y + save.Bounds.Height <= root.Bounds.Height + 0.5)
        dialog.Disconnect()
        Assert.True save.IsEnabled // 已完整收到的文件可以在断线后保存。
    finally
        overlay.CloseDialog()
        window.Close()

[<Fact>]
let ``reconnecting the same instance keeps export recovery but changing instance closes it`` () =
    Headless.ensure ()
    let view = MainView()
    let invoke name args = typeof<MainView>.GetMethod(name, flags).Invoke(view, args)
    for name in [ "BuildSidebar"; "BuildChat"; "BuildComposer"; "BuildSettings" ] do invoke name [||] |> ignore
    typeof<MainView>.GetField("authenticated", flags).SetValue(view, true)
    typeof<MainView>.GetField("instanceId", flags).SetValue(view, "original")
    let summary: ConversationSummary =
        { id = Guid.NewGuid(); title = "导出"; preview = ""; running = false; pinned = false
          archived = false; createdAt = DateTimeOffset.UtcNow; messageCount = 250; isFork = false
          providerId = "mock"; model = "mock"; lastCommitId = 251UL }
    invoke "ExportConversation" [| box summary |] |> ignore
    let overlay = typeof<MainView>.GetField("overlay", flags).GetValue(view) :?> OverlayHost
    try
        invoke "HandleEvent" [| box (AuthAccepted {| instanceId = "original" |}) |] |> ignore
        Assert.True overlay.IsDialogOpen
        invoke "HandleEvent" [| box (AuthAccepted {| instanceId = "different" |}) |] |> ignore
        Assert.False overlay.IsDialogOpen
    finally overlay.CloseDialog()
