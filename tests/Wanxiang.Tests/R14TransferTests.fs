module Wanxiang.Tests.R14TransferTests

// 决策 310 更早一页超时 / 311 撤销提示条键盘可达 / 312 导出 JSON 与分叉信息 / 313 导入。

open System
open System.Reflection
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Server
open Wanxiang.Tests.Helpers
open Wanxiang.UI

let private flags = BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public

let private append events (proj: Projection) =
    let commit = Events.Commit.create (proj.latestCommitId + 1UL) DateTimeOffset.UtcNow events
    match Projection.applyCommit proj commit with
    | Ok updated -> updated
    | Error error -> failwith (WanxiangError.message error)

let private fixture count =
    let id = Guid.NewGuid()
    let mutable proj = Projection.empty |> append [ ConversationCreated { conversationId = id; title = "旅行计划"; config = testConfig () } ]
    for n in 1 .. count do
        proj <- proj |> append [ AgentMessageRecorded { conversationId = id; payloadJson = userMessageJson (sprintf "第 %d 条" n) } ]
    proj, id

let private complete proj (controller: ConversationExportController) query =
    let mutable next = Some query
    while next.IsSome do
        next <- controller.Accept(match ServerModel.exportPage proj next.Value with Ok p -> p | Error e -> failwith e)
    controller.Document.Value

let private forkLineage parent =
    { parentId = Some parent; parentTitle = Some "原对话"; parentDeleted = false; forkOrdinal = Some 3
      forkBaseMessageId = Some 4UL; forks = [ Guid.NewGuid(), "子分叉", Some 9UL ] }

// ---------- 312 ----------

[<Fact>]
let ``312 JSON 导出带格式版本、分叉信息和隐私说明，消息原样保留`` () =
    let proj, id = fixture 3
    let controller = ConversationExportController id
    let doc = complete proj controller (controller.Start())
    Assert.Equal(3, doc.raw.Length)
    let parent = Guid.NewGuid()
    let json = ConversationTransfer.toJson id doc.title doc.atCommitId DateTimeOffset.UtcNow (forkLineage parent) doc.raw
    let root = JsonNode.Parse(json).AsObject()
    Assert.Equal("wanxiang.conversation", root["format"].GetValue<string>())
    Assert.Equal(1, root["formatVersion"].GetValue<int>())
    Assert.Equal("fork", root["lineage"].["kind"].GetValue<string>())
    Assert.Equal(parent.ToString("D"), root["lineage"].["parentId"].GetValue<string>())
    Assert.Equal(3, root["lineage"].["forkOrdinal"].GetValue<int>())
    Assert.Equal(1, root["lineage"].["forks"].AsArray().Count)
    Assert.Contains("API 密钥", root["privacy"].GetValue<string>())
    Assert.Equal(3, root["messages"].AsArray().Count)
    Assert.Equal("user", root["messages"].[0].["payload"].["role"].GetValue<string>())

[<Fact>]
let ``312 载荷里出现密钥类字段一律去掉`` () =
    let raw = JsonObject()
    raw["commitId"] <- 5UL
    raw["payload"] <- JsonNode.Parse("""{"role":"assistant","apiKey":"sk-xx","contents":[{"type":"text","text":"hi","Authorization":"Bearer y"}]}""")
    let json = ConversationTransfer.toJson (Guid.NewGuid()) "t" 5UL DateTimeOffset.UtcNow ConversationTransfer.rootLineage [ raw ]
    Assert.DoesNotContain("sk-xx", json)
    Assert.DoesNotContain("Bearer y", json)
    Assert.Contains("\"hi\"", json)
    Assert.Contains("\"kind\": \"root\"", json)

[<Fact>]
let ``312 Markdown 头部写明来源、分叉和隐私说明`` () =
    let md = ConversationTransfer.withPreamble "# 标题\n\n## 用户\n\n你好\n" (ConversationTransfer.markdownPreamble (forkLineage (Guid.NewGuid())) 2 DateTimeOffset.UtcNow)
    Assert.StartsWith("# 标题\n\n> 导出自万象", md)
    Assert.Contains("「原对话」第 3 条消息的分叉", md)
    Assert.Contains("分出了 1 个分叉：「子分叉」", md)
    Assert.Contains("不含 API 密钥", md)
    Assert.Contains("## 用户", md)

[<Fact>]
let ``312 谱系从对话列表派生：分叉、原对话已删除、子分叉`` () =
    let root, fork, child = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    let summary id title : ConversationSummary =
        { id = id; title = title; preview = ""; running = false; pinned = false; archived = false
          createdAt = DateTimeOffset.UtcNow; updatedAt = DateTimeOffset.UtcNow; messageCount = 1
          isFork = false; providerId = ""; model = ""; lastCommitId = 1UL }
    let l parent : ForkLineage =
        { parentId = parent; forkBaseMessageId = Some 2UL; forkFirstMessageId = Some 7UL; parentTitle = None; parentDeleted = false; forkOrdinal = Some 2 }
    let lineage = Map [ fork, l root; child, l fork ]
    let summaries = [ summary root "根"; summary fork "分叉一"; summary child "分叉二" ]
    let lf = ConversationTransfer.lineageOf summaries lineage fork
    Assert.Equal(Some root, lf.parentId)
    Assert.Equal(Some "根", lf.parentTitle)
    Assert.False lf.parentDeleted
    Assert.Equal<string list>([ "分叉二" ], lf.forks |> List.map (fun (_, t, _) -> t))
    let gone = ConversationTransfer.lineageOf [ summary fork "分叉一" ] lineage fork
    Assert.True gone.parentDeleted
    Assert.Contains("原对话已删除", ConversationTransfer.lineageLines gone |> String.concat "")
    Assert.True((ConversationTransfer.lineageOf summaries lineage root).parentId.IsNone)

// ---------- 313 ----------

let private exported count =
    let proj, id = fixture count
    let controller = ConversationExportController id
    let doc = complete proj controller (controller.Start())
    ConversationTransfer.toJson id doc.title doc.atCommitId DateTimeOffset.UtcNow (forkLineage (Guid.NewGuid())) doc.raw

[<Fact>]
let ``313 导出的 JSON 能解析回预览：标题、条数、分叉说明`` () =
    let text = exported 4
    match ConversationTransfer.parseImport (int64 text.Length) text with
    | Ok p ->
        Assert.Equal("旅行计划", p.title)
        Assert.Equal(4, p.messages.Length)
        Assert.Equal(0, p.skipped)
        Assert.Contains("分叉关系不会恢复", p.lineageNote.Value)
        Assert.Equal("旅行计划（导入）", ConversationTransfer.importedTitle p.title)
    | Error e -> failwith e

[<Fact>]
let ``313 不认识的文件给出可读原因，不部分导入`` () =
    let expectError (text: string) (fragment: string) =
        match ConversationTransfer.parseImport (int64 text.Length) text with
        | Error e -> Assert.Contains(fragment, e)
        | Ok _ -> failwithf "should reject %s" text
    expectError "# 标题\n\n正文" "不是有效的 JSON"
    expectError """{"hello":1}""" "不是万象导出的对话文件"
    expectError """{"format":"wanxiang.conversation","formatVersion":9,"messages":[]}""" "更新版本"
    expectError """{"format":"wanxiang.conversation","formatVersion":1,"messages":[]}""" "没有可导入的消息"
    expectError """{"format":"wanxiang.conversation","formatVersion":1,"messages":[{"payload":{"role":"robot"}}]}""" "没有可导入的消息"
    match ConversationTransfer.parseImport (ConversationTransfer.maxImportBytes + 1L) "" with
    | Error e -> Assert.Contains("超过", e)
    | Ok _ -> failwith "oversize"

[<Fact>]
let ``313 conversation.import 编解码往返`` () =
    let inv, conv = Guid.NewGuid(), Guid.NewGuid()
    let msgs = JsonArray(userMessageJson "a", userMessageJson "b")
    let cmd = ImportConversation {| invocationId = inv; conversationId = conv; title = "T（导入）"; config = testConfig (); messages = msgs |}
    let encoded = WireCodec.encodeCommand cmd
    Assert.Contains("\"type\":\"conversation.import\"", encoded)
    match WireCodec.tryDecodeCommand encoded with
    | Ok(ImportConversation d) ->
        Assert.Equal(conv, d.conversationId)
        Assert.Equal("T（导入）", d.title)
        Assert.Equal(2, d.messages.Count)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``313 导入规划为新建 + 逐条记录，不接受空、坏角色或已存在的 id`` () =
    let proj, existing = fixture 1
    let plan id (msgs: JsonNode list) =
        CommandEngine.plan proj proj.latestCommitId
            (ImportConversation {| invocationId = Guid.NewGuid(); conversationId = id; title = " 导入 "; config = testConfig (); messages = JsonArray(Array.ofList msgs) |})
    match plan (Guid.NewGuid()) [ userMessageJson "问"; JsonNode.Parse("""{"role":"assistant","contents":[{"type":"text","text":"答"}]}""") ] with
    | Planned p ->
        match p.events with
        | [ ConversationCreated c; AgentMessageRecorded _; AgentMessageRecorded _ ] -> Assert.Equal("导入", c.title)
        | e -> failwithf "unexpected %A" e
    | other -> failwithf "expected plan %A" other
    match plan (Guid.NewGuid()) [] with Rejected(ValidationError _) -> () | o -> failwithf "%A" o
    match plan (Guid.NewGuid()) [ JsonNode.Parse("""{"role":"robot"}""") ] with Rejected(ValidationError _) -> () | o -> failwithf "%A" o
    match plan existing [ userMessageJson "x" ] with Rejected(ConversationIdTaken _) -> () | o -> failwithf "%A" o

[<Fact>]
let ``313 导入后对话可读：投影里有全部消息且不触发生成`` () =
    let proj, _ = fixture 0
    let id = Guid.NewGuid()
    match CommandEngine.plan proj proj.latestCommitId
            (ImportConversation {| invocationId = Guid.NewGuid(); conversationId = id; title = "x"; config = testConfig (); messages = JsonArray(userMessageJson "1", userMessageJson "2") |}) with
    | Planned p ->
        let after = proj |> append p.events
        Assert.Equal(2, (Projection.effectiveMessages after after.conversations[id]).Length)
    | o -> failwithf "%A" o

[<Trait("Category", "UI")>]
[<Fact>]
let ``313 导入对话框：选文件前不能导入，预览通过后才发命令，失败可重试`` () =
    Headless.run (fun () ->
    let root = Grid()
    let window = Window(Width = 900.0, Height = 700.0, Content = root)
    window.Show()
    let overlay = OverlayHost(root)
    let submitted = ResizeArray<ImportPreview>()
    let mutable finish: bool -> unit = ignore
    let dialog = ConversationImportDialog(overlay, (fun () -> window :> TopLevel), fun p onDone -> submitted.Add p; finish <- onDone)
    dialog.Show()
    Dispatcher.UIThread.RunJobs()
    Assert.True overlay.IsDialogOpen
    Assert.False dialog.ImportButton.IsVisible
    dialog.LoadText("bad.md", 10L, "# 不是 JSON")
    Assert.Contains("不是有效的 JSON", dialog.StatusText)
    Assert.False dialog.ImportButton.IsVisible
    let text = exported 2
    dialog.LoadText("旅行计划.json", int64 text.Length, text)
    Assert.True dialog.ImportButton.IsVisible
    Assert.Contains("2 条消息", dialog.StatusText)
    Assert.Contains("旅行计划（导入）", dialog.DetailsText)
    dialog.ImportButton.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = dialog.ImportButton))
    Assert.Equal(1, submitted.Count)
    finish false
    Assert.True overlay.IsDialogOpen
    Assert.Contains("没有完成", dialog.StatusText)
    Assert.True dialog.ImportButton.IsEnabled
    dialog.ImportButton.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = dialog.ImportButton))
    finish true
    Assert.False overlay.IsDialogOpen
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``312 导出对话框同一次读取备好 Markdown 和 JSON，并显示隐私说明`` () =
    Headless.run (fun () ->
    let proj, id = fixture 3
    let root = Grid()
    let window = Window(Width = 900.0, Height = 700.0, Content = root)
    window.Show()
    let overlay = OverlayHost(root)
    let requested = ResizeArray<ConversationExportQuery>()
    let dialog = ConversationExportDialog(overlay, id, "旅行计划", (fun q -> requested.Add q; Task.FromResult true), (fun () -> window :> TopLevel), forkLineage (Guid.NewGuid()))
    dialog.Show()
    Dispatcher.UIThread.RunJobs()
    let mutable i = 0
    while i < requested.Count do
        match ServerModel.exportPage proj requested[i] with
        | Ok p -> dialog.Handle p
        | Error e -> failwith e
        i <- i + 1
    Assert.Contains("「原对话」第 3 条消息的分叉", dialog.MarkdownText.Value)
    Assert.Contains("\"kind\": \"fork\"", dialog.JsonText.Value)
    let texts =
        window.GetVisualDescendants() |> Seq.choose (function :? TextBlock as t -> Some t.Text | _ -> None) |> String.concat "\n"
    Assert.Contains("不含 API 密钥", texts)
    Assert.Contains("保存 JSON", texts)
    overlay.CloseDialog()
    )

// ---------- 311 ----------

[<Trait("Category", "UI")>]
[<Fact>]
let ``311 撤销提示条按钮进 Tab 序，F6 直达，聚焦时暂停倒计时`` () =
    Headless.run (fun () ->
    let root = Grid()
    let before = Button(Content = "前一个")
    root.Children.Add before
    let window = Window(Width = 900.0, Height = 700.0, Content = root)
    window.Show()
    let overlay = OverlayHost(root)
    let mutable undone = false
    let mutable homed = false
    overlay.SetToastFocusHome(fun () -> homed <- true)
    overlay.ToastWith("已删除「x」", Info, Some("撤销", fun () -> undone <- true))
    Dispatcher.UIThread.RunJobs()
    let button = overlay.ToastActionButtons |> List.exactlyOne
    Assert.True button.IsTabStop
    Assert.True button.Focusable
    Assert.Equal(ShortcutAction.FocusToast, ShortcutRouter.resolve (KeyEventArgs(Key = Key.F6, KeyModifiers = KeyModifiers.None)))
    Assert.True(overlay.FocusToastAction())
    Dispatcher.UIThread.RunJobs()
    Assert.True button.IsFocused
    button.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = button))
    Assert.True undone
    Assert.True(homed, "带焦点的提示条消失后焦点要有归宿")
    Assert.False(overlay.FocusToastAction())
    )

// ---------- 310 ----------

let private openFailingShell () =
    MotionPolicy.setReduced true
    let view = MainView()
    view.Build()
    let window = Window(Width = 1000.0, Height = 800.0, Content = view)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    view, window

[<Trait("Category", "UI")>]
[<Fact>]
let ``310 更早一页超时：解除加载、撤销挂起跳转、给行内重试，滚动不再静默重发`` () =
    Headless.run (fun () ->
    let view, _ = openFailingShell ()
    let field name = typeof<MainView>.GetField(name, flags)
    let chat = field("chat").GetValue(view) :?> ChatView
    field("pageLoading").SetValue(view, true)
    field("pageRequestedAt").SetValue(view, Some(DateTimeOffset.UtcNow.AddSeconds -11.0))
    field("authenticated").SetValue(view, false)
    typeof<MainView>.GetMethod("CheckOlderPageTimeout", flags).Invoke(view, [| box DateTimeOffset.UtcNow |]) |> ignore
    Assert.False(field("pageLoading").GetValue(view) :?> bool)
    Assert.True chat.OlderPageFailureVisible
    Assert.Contains("超时", chat.OlderPageFailureText)
    Assert.False chat.PendingJumpPrevious
    // 未连接时点重试：不发请求，换成说明为什么
    let retry = chat.OlderPageRetryButton
    retry.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = retry))
    Assert.True chat.OlderPageFailureVisible
    Assert.False(field("pageLoading").GetValue(view) :?> bool)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``310 未超时不判失败；断线时在途请求立即判失败`` () =
    Headless.run (fun () ->
    let view, _ = openFailingShell ()
    let field name = typeof<MainView>.GetField(name, flags)
    let chat = field("chat").GetValue(view) :?> ChatView
    field("pageLoading").SetValue(view, true)
    field("pageRequestedAt").SetValue(view, Some DateTimeOffset.UtcNow)
    typeof<MainView>.GetMethod("CheckOlderPageTimeout", flags).Invoke(view, [| box (DateTimeOffset.UtcNow.AddSeconds 2.0) |]) |> ignore
    Assert.True(field("pageLoading").GetValue(view) :?> bool)
    Assert.False chat.OlderPageFailureVisible
    typeof<MainView>.GetMethod("FailOlderPage", flags).Invoke(view, [| box "连接已断开，更早的消息没有加载。" |]) |> ignore
    Assert.True chat.OlderPageFailureVisible
    Assert.Contains("断开", chat.OlderPageFailureText)
    chat.ResetScrollState()
    Assert.False(chat.OlderPageFailureVisible, "换对话后失败提示不残留")
    )

// ---------- 截图（仅在设置 WANXIANG_UX_ARTIFACT_DIR 时落盘） ----------

let private capture (window: Window) (name: string) =
    match Environment.GetEnvironmentVariable "WANXIANG_UX_ARTIFACT_DIR" with
    | null | "" -> ()
    | directory ->
        IO.Directory.CreateDirectory directory |> ignore
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick()
        Dispatcher.UIThread.RunJobs()
        use frame = window.CaptureRenderedFrame()
        frame.Save(IO.Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default)

[<Trait("Category", "UI")>]
[<Fact>]
let ``r14 截图：导出、导入、更早一页失败、撤销提示条`` () =
    Headless.run (fun () ->
    MotionPolicy.setReduced true
    Tokens.apply ThemeMode.Light
    let proj, id = fixture 6
    let shellWindow () =
        let root = Grid(Background = Tokens.canvas)
        let window = Window(Width = 960.0, Height = 680.0, Content = root)
        window.RequestedThemeVariant <- Avalonia.Styling.ThemeVariant.Light
        window.Show()
        root, window, OverlayHost(root)
    // 1 导出对话框（分叉）
    let _, w1, o1 = shellWindow ()
    let requested = ResizeArray<ConversationExportQuery>()
    let export = ConversationExportDialog(o1, id, "旅行计划", (fun q -> requested.Add q; Task.FromResult true), (fun () -> w1 :> TopLevel), forkLineage (Guid.NewGuid()))
    export.Show()
    Dispatcher.UIThread.RunJobs()
    let mutable i = 0
    while i < requested.Count do
        (match ServerModel.exportPage proj requested[i] with Ok p -> export.Handle p | Error e -> failwith e)
        i <- i + 1
    capture w1 "01-export-dialog-markdown-json-privacy"
    w1.Close()
    // 2 导入预览 / 3 导入错误
    let _, w2, o2 = shellWindow ()
    let import = ConversationImportDialog(o2, (fun () -> w2 :> TopLevel), fun _ _ -> ())
    import.Show()
    Dispatcher.UIThread.RunJobs()
    capture w2 "02-import-dialog-before-pick"
    let text = exported 6
    import.LoadText("旅行计划.json", int64 text.Length, text)
    capture w2 "03-import-dialog-preview"
    import.LoadText("旅行计划.md", 20L, "# 旅行计划\n\n正文")
    capture w2 "04-import-dialog-rejects-markdown"
    w2.Close()
    // 5 更早一页失败 / 6 撤销提示条聚焦
    let view = MainView()
    view.Build()
    let w3 = Window(Width = 1100.0, Height = 720.0, Content = view)
    w3.RequestedThemeVariant <- Avalonia.Styling.ThemeVariant.Light
    w3.Show()
    Dispatcher.UIThread.RunJobs()
    let field name = typeof<MainView>.GetField(name, flags)
    field("pageLoading").SetValue(view, true)
    field("pageRequestedAt").SetValue(view, Some(DateTimeOffset.UtcNow.AddSeconds -11.0))
    typeof<MainView>.GetMethod("CheckOlderPageTimeout", flags).Invoke(view, [| box DateTimeOffset.UtcNow |]) |> ignore
    Dispatcher.UIThread.RunJobs()
    capture w3 "05-older-page-timeout-inline-retry"
    let overlay = field("overlay").GetValue(view) :?> OverlayHost
    overlay.ToastWith("已删除「旅行计划」", Info, Some("撤销", ignore))
    Dispatcher.UIThread.RunJobs()
    overlay.FocusToastAction() |> ignore
    capture w3 "06-undo-toast-focused-via-f6"
    w3.Close()
    )
