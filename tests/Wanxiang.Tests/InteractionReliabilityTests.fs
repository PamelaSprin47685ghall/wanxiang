module Wanxiang.Tests.InteractionReliabilityTests

open System
open System.Reflection
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Input
open Avalonia.Automation.Provider
open Avalonia.Controls
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.UI
open Wanxiang2.MessageCardView
open Avalonia.VisualTree

let private flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic
let private field (target: obj) name = target.GetType().GetField(name, flags)
let private invoke (target: obj) name args = target.GetType().GetMethod(name, flags).Invoke(target, args)

let rec private controls (root: Control) = seq {
    yield root
    match root with
    | :? Panel as p -> for child in p.Children do yield! controls child
    | :? Decorator as d when not (isNull d.Child) -> yield! controls d.Child
    | :? ContentControl as c ->
        match c.Content with :? Control as child -> yield! controls child | _ -> ()
    | _ -> ()
}

// Build the real shell's components without AutoConnect or a live user's credentials.
let private shell () =
    let view = MainView()
    for method in [ "BuildSidebar"; "BuildChat"; "BuildComposer"; "BuildSettings" ] do
        invoke view method [||] |> ignore
    view

let private handle view ev = invoke view "HandleEvent" [| box ev |] |> ignore
let private select view id = (field view "activeConvId").SetValue(view, Some id)
let private snapshot id =
    ConversationSnapshot
        {| conversationId = id; title = "测试"; lastCommitId = 0UL; runtimeState = "idle"; generationId = None
           messages = JsonArray(); snapshotEarliestCommitId = 0UL; snapshotHasMore = false
           config = SessionConfig.empty |}
/// 尾部带一条有正文的回复：重新生成会丢掉它，必须确认（决策 221）。
let private snapshotWithReply id =
    let messages =
        JsonNode.Parse("""[{"commitId":1,"payload":{"role":"user","contents":[{"type":"text","text":"问"}]}},{"commitId":2,"payload":{"role":"assistant","contents":[{"type":"text","text":"已有的回答"}]}}]""") :?> JsonArray
    ConversationSnapshot
        {| conversationId = id; title = "测试"; lastCommitId = 2UL; runtimeState = "idle"; generationId = None
           messages = messages; snapshotEarliestCommitId = 1UL; snapshotHasMore = false
           config = SessionConfig.empty |}
let private start id generation =
    GenerationStarted {| conversationId = id; generationId = generation; providerId = "mock"; model = "mock" |}
let private finish id generation =
    GenerationFinished {| conversationId = id; generationId = generation; status = "completed"; error = None; usage = None |}
let private stopVisible view =
    let composer = (field view "composer").GetValue view :?> Composer
    controls composer |> Seq.exists (fun c -> AutomationProperties.GetName c = "停止生成")

[<Trait("Category", "UI")>]
[<Fact>]
let ``composer retains input when dispatch does not take ownership`` () =
    Headless.run (fun () ->
    let composer =
        Composer
            { submit = fun _ -> failwith "dispatch failed"
              stopGeneration = ignore; pickAttachment = ignore; removeAttachment = ignore; openModelPicker = ignore
              dropFiles = ignore; pasteFromClipboard = fun () -> false }
    composer.Build()
    composer.SetEnabled(true, "")
    composer.SetText "这段文字不能丢"
    try invoke composer "Submit" [||] |> ignore with :? TargetInvocationException -> ()
    let text = controls composer |> Seq.pick (function :? TextBox as t -> Some t.Text | _ -> None)
    Assert.Equal("这段文字不能丢", text)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``finishing a background conversation does not stop the foreground generation`` () =
    Headless.run (fun () ->
    let view = shell ()
    let a, b, ga, gb = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    select view a
    handle view (snapshot a)
    handle view (start a ga)
    Assert.True(stopVisible view)
    handle view (finish b gb)
    Assert.True(stopVisible view)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``finishing an old generation does not stop its replacement`` () =
    Headless.run (fun () ->
    let view = shell ()
    let a, oldGeneration, currentGeneration = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    select view a
    handle view (snapshot a)
    handle view (start a oldGeneration)
    handle view (start a currentGeneration)
    handle view (finish a oldGeneration)
    Assert.True(stopVisible view)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``late snapshot updates data without taking over navigation`` () =
    Headless.run (fun () ->
    let view = shell ()
    let a, b = Guid.NewGuid(), Guid.NewGuid()
    select view b
    handle view (snapshot a)
    Assert.Equal(Some b, (field view "activeConvId").GetValue(view) :?> Guid option)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``a declined submission leaves the composer editable and intact`` () =
    Headless.run (fun () ->
    let composer =
        Composer
            { submit = fun _ -> false
              stopGeneration = ignore
              pickAttachment = ignore
              removeAttachment = ignore
              openModelPicker = ignore
              dropFiles = ignore
              pasteFromClipboard = fun () -> false }
    composer.Build()
    composer.SetEnabled(true, "")
    composer.SetText "仍然是草稿"
    invoke composer "Submit" [||] |> ignore
    Assert.Equal("仍然是草稿", composer.Text)
    composer.SetEnabled(false, "连接断开")
    let input = controls composer |> Seq.pick (function :? TextBox as t -> Some t | _ -> None)
    Assert.True input.IsEnabled
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``generation accepts a queued message without turning send into cancel`` () =
    Headless.run (fun () ->
    let mutable submitted = ""
    let mutable stopped = false
    let composer =
        Composer
            { submit = fun text -> submitted <- text; true
              stopGeneration = fun () -> stopped <- true
              pickAttachment = ignore
              removeAttachment = ignore
              openModelPicker = ignore
              dropFiles = ignore
              pasteFromClipboard = fun () -> false }
    composer.Build()
    composer.SetEnabled(true, "")
    composer.SetGenerating true
    composer.SetText "补充一句"
    invoke composer "Submit" [||] |> ignore
    Assert.Equal("补充一句", submitted)
    Assert.Equal("", composer.Text)
    Assert.False stopped
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``switching conversations restores their separate drafts`` () =
    Headless.run (fun () ->
    let view = shell ()
    let a, b = Guid.NewGuid(), Guid.NewGuid()
    let composer = (field view "composer").GetValue view :?> Composer
    invoke view "SelectConversation" [| box (Some a) |] |> ignore
    composer.SetText "A 的草稿"
    invoke view "SelectConversation" [| box (Some b) |] |> ignore
    Assert.Equal("", composer.Text)
    composer.SetText "B 的草稿"
    invoke view "SelectConversation" [| box (Some a) |] |> ignore
    Assert.Equal("A 的草稿", composer.Text)
    invoke view "SelectConversation" [| box (Some b) |] |> ignore
    Assert.Equal("B 的草稿", composer.Text)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``outbox keeps the exact command and attachment references across a disconnect`` () =
    Headless.run (fun () ->
    let outbox = MessageOutbox()
    let attachment = { attachmentId = Guid.NewGuid(); sha256 = "abc"; size = 12L
                       mediaType = "text/plain"; fileName = "notes.txt"; ready = true; failed = false }
    let item = outbox.Stage("server-a", Guid.NewGuid(), "原文", [ attachment ], None)
    let id = PendingMessage.invocationId item
    let encoded = WireCodec.encodeCommand item.command
    outbox.Accept id
    Assert.Equal(1, outbox.Count)
    Assert.Equal(QueuedMessage, (outbox.TryFind id).Value.state)
    outbox.Disconnect "server-a"
    let retained = (outbox.TryFind id).Value
    Assert.True(PendingMessage.canRetry retained)
    Assert.Equal("原文", retained.text)
    Assert.Equal(encoded, WireCodec.encodeCommand retained.command)
    Assert.Equal<PendingAttachment list>([ attachment ], retained.attachments)
    outbox.SetState(id, SendingMessage)
    Assert.Equal(encoded, WireCodec.encodeCommand (outbox.TryFind id).Value.command)
    outbox.Commit id
    outbox.Accept id
    Assert.Equal(0, outbox.Count)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``preparing first message retains its original creation command for retry`` () =
    Headless.run (fun () ->
    let outbox = MessageOutbox()
    let conversationId = Guid.NewGuid()
    let creation = CreateConversation {| invocationId = Guid.NewGuid(); conversationId = conversationId
                                         title = "新对话"; config = SessionConfig.empty |}
    let item = outbox.Stage("server", conversationId, "第一条不能丢", [], Some creation)
    outbox.Disconnect "server"
    let retained = (outbox.TryFind(PendingMessage.invocationId item)).Value
    Assert.Equal(WireCodec.encodeCommand creation, WireCodec.encodeCommand retained.creation.Value)
    Assert.False retained.creationConfirmed
    outbox.ConversationReady("server", conversationId)
    Assert.True((outbox.TryFind(PendingMessage.invocationId item)).Value.creationConfirmed)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``unacknowledged sends expire but an explicitly queued message does not`` () =
    Headless.run (fun () ->
    let outbox = MessageOutbox()
    let sending = outbox.Stage("server", Guid.NewGuid(), "发送中", [], None)
    let queued = outbox.Stage("server", Guid.NewGuid(), "长任务后插入", [], None)
    outbox.Accept(PendingMessage.invocationId queued)
    Assert.True(outbox.Expire(DateTimeOffset.UtcNow.AddMinutes 2.0, TimeSpan.FromSeconds 30.0))
    Assert.True(PendingMessage.canRetry (outbox.TryFind(PendingMessage.invocationId sending)).Value)
    Assert.Equal(QueuedMessage, (outbox.TryFind(PendingMessage.invocationId queued)).Value.state)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``drafts and pending messages never cross server identities`` () =
    Headless.run (fun () ->
    let drafts = ComposerDrafts()
    let id = Guid.NewGuid()
    (drafts.Get("a", Some id)).text <- "只属于 a"
    Assert.Equal("", (drafts.Get("b", Some id)).text)
    let outbox = MessageOutbox()
    outbox.Stage("a", id, "私有内容", [], None) |> ignore
    Assert.Empty(outbox.Items("b", Some id))
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``stale deltas and finished snapshots cannot resurrect retired generations`` () =
    Headless.run (fun () ->
    let runs = ConversationRuns()
    let id, oldId, newId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    runs.Start(id, oldId)
    runs.Start(id, newId)
    Assert.False(runs.Delta(id, oldId, { MessageView.empty with text = "迟到的内容" }))
    Assert.False(runs.Finish(id, oldId, None, None))
    runs.Snapshot(id, "generating", Some oldId)
    Assert.Equal(Some newId, (runs.Get(Some id)).generationId)
    Assert.True(runs.Finish(id, newId, None, None))
    runs.Snapshot(id, "generating", Some newId)
    Assert.False((runs.Get(Some id)).running)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``configuration failure without a started event is still visible in its own conversation`` () =
    Headless.run (fun () ->
    let view = shell ()
    let id = Guid.NewGuid()
    select view id
    handle view (snapshot id)
    let error = GenerationError.create ConfigInvalid "模型服务已删除"
    handle view (
        GenerationFinished
            {| conversationId = id
               generationId = Guid.NewGuid()
               status = "failed"
               error = Some error
               usage = None |})
    let runs = (field view "runs").GetValue view :?> ConversationRuns
    Assert.Equal(Some error, (runs.Get(Some id)).error)
    Assert.True((runs.Get(Some(Guid.NewGuid()))).error.IsNone)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``snapshot generation identity roundtrips and legacy snapshots remain readable`` () =
    Headless.run (fun () ->
    let id, generation = Guid.NewGuid(), Guid.NewGuid()
    let json = JsonNode.Parse(WireCodec.encode(snapshot id)).AsObject()
    let payload = json["payload"].AsObject()
    payload["runtimeState"] <- "generating"
    payload["runId"] <- generation.ToString("D")
    match WireCodec.tryDecode(json.ToJsonString()) with
    | Ok (ConversationSnapshot d) ->
        Assert.Equal(Some generation, d.generationId)
        let run = ConversationRuns()
        run.Snapshot(id, d.runtimeState, d.generationId)
        Assert.Equal(Some generation, (run.Get(Some id)).generationId)
    | other -> failwithf "unexpected snapshot: %A" other
    json["payload"].AsObject().Remove "runId" |> ignore
    match WireCodec.tryDecode(json.ToJsonString()) with
    | Ok (ConversationSnapshot d) -> Assert.True d.generationId.IsNone
    | other -> failwithf "legacy snapshot rejected: %A" other
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``disconnected command transport reports failure instead of silent success`` () =
    Headless.run (fun () ->
    let client = Wanxiang.Client.WsClient()
    let command = RenameConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = "不会发出" |}
    Assert.False(client.TrySendCommandAsync(command).GetAwaiter().GetResult())
    let before = client.ConnectionGeneration
    client.Disconnect()
    Assert.True(client.ConnectionGeneration > before)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``failed first messages remain reachable after switching away`` () =
    Headless.run (fun () ->
    let id = Guid.NewGuid()
    let outbox = MessageOutbox()
    let creation = CreateConversation {| invocationId = Guid.NewGuid(); conversationId = id; title = "尚未创建"; config = SessionConfig.empty |}
    outbox.Stage("server", id, "保留的第一句话", [], Some creation) |> ignore
    outbox.RejectCreation(ClientCommand.invocationId creation, "连接中断")
    let mutable opened: Guid option = None
    let composer =
        Composer
            { submit = fun _ -> false
              stopGeneration = ignore; pickAttachment = ignore; removeAttachment = ignore; openModelPicker = ignore
              dropFiles = ignore; pasteFromClipboard = fun () -> false }
    composer.Build()
    composer.SetPendingMessages(outbox.ForInstance "server", Some(Guid.NewGuid()), true, ignore, fun target -> opened <- Some target)
    let window = Window(Content = composer, Width = 420.0, Height = 500.0)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()
        let button = controls composer |> Seq.find (fun c ->
            let name = AutomationProperties.GetName c
            not (String.IsNullOrEmpty name) && name.StartsWith("查看另一个对话"))
        let action = ControlAutomationPeer.CreatePeerForElement button |> Assert.IsAssignableFrom<IInvokeProvider>
        action.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(Some id, opened)
        Assert.Equal(1, outbox.Count)
    finally window.Close()
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``selecting an unloaded conversation does not display the previous conversation body`` () =
    Headless.run (fun () ->
    let view = shell ()
    let a, b, generation = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    select view a
    handle view (snapshot a)
    handle view (start a generation)
    handle view (
        GenerationDelta
            {  conversationId = a
               generationId = generation
               payload = JsonNode.Parse("""{"role":"assistant","contents":[{"text":"只属于对话 A 的正文"}]}""") })
    invoke view "SelectConversation" [| box (Some b) |] |> ignore
    let chat = (field view "chat").GetValue view :?> ChatView
    let visibleText =
        controls chat |> Seq.choose (function
            | :? SelectableTextBlock as text -> Some text.Text
            | :? TextBlock as text -> Some text.Text
            | _ -> None)
    Assert.DoesNotContain("只属于对话 A 的正文", visibleText)
    let runs = (field view "runs").GetValue view :?> ConversationRuns
    Assert.Equal("只属于对话 A 的正文", (runs.Get(Some a)).message.Value.text)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``generation ending hands keyboard focus back to the composer`` () =
    Headless.run (fun () ->
    let composer =
        Composer
            { submit = fun _ -> true
              stopGeneration = ignore; pickAttachment = ignore; removeAttachment = ignore; openModelPicker = ignore
              dropFiles = ignore; pasteFromClipboard = fun () -> false }
    composer.Build()
    let window = Window(Content = composer, Width = 600.0, Height = 400.0)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()
        composer.SetEnabled(true, "")
        composer.SetGenerating true
        Dispatcher.UIThread.RunJobs()
        let sendButton = controls composer |> Seq.find (fun c -> AutomationProperties.GetName c = "停止生成")
        Assert.True(sendButton.Focusable, "前置条件：生成态停止键可获得焦点")
        sendButton.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True(sendButton.IsFocused, "前置条件：焦点在停止键上")
        composer.SetGenerating false
        Dispatcher.UIThread.RunJobs()
        let input = controls composer |> Seq.pick (function :? TextBox as t -> Some t | _ -> None)
        Assert.True(input.IsFocused,
            "生成结束后焦点必须回家到输入框：停止键/排队键是两个常驻占位槽，失去生成态时"
            + "被复位成 Focusable=false（布局占位不变、只断键盘与命中），而 Avalonia 不会替"
            + "宿主失资格的控件自动迁焦——焦点悬停在那个 Opacity=0 的 Border 上，之后敲的字"
            + "没有任何承接。kelivo chat_input_bar.dart:1010 在生成结束一律回 focusNode。")
    finally
        window.Close()
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``the sole primary stop returns keyboard focus to input when a run finishes`` () =
    Headless.run (fun () ->
    // 主要停止入口移到输入区后，结束生成仍须把键盘输入交回文本框。
    let chat =
        ChatView(
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
                  OpenLink = ignore; CopyLink = None; Recover = None } },
            SharedTheme.logo)
    chat.Build()
    let composer = Composer(
        { submit = (fun _ -> true); stopGeneration = ignore; pickAttachment = ignore
          removeAttachment = ignore; openModelPicker = ignore; dropFiles = ignore
          pasteFromClipboard = (fun () -> false) })
    composer.Build()
    composer.SetEnabled(true, "")
    let host = DockPanel()
    DockPanel.SetDock(composer, Dock.Bottom)
    host.Children.Add composer
    host.Children.Add chat
    let window = Window(Content = host, Width = 900.0, Height = 700.0)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()
        // 与 AppShell 顺序一致：设置生成态，再确认是否可以停止。
        chat.SetConversationChrome true
        chat.SetGenerating(true, "生成中")
        chat.SetCanStop true
        composer.SetGenerating true
        composer.SetCanStop true
        Dispatcher.UIThread.RunJobs()
        // 首帧 layout 尚未落地前 Focus 不可能成功：Bounds 全 0、effVis 假。
        // 二次 RunJobs 让窗口完成 measure/arrange 再落焦点；headless 下窗口的
        // 首帧 layout 不随 window.Show() 同步落地，得手动推一轮 layout pass。
        window.GetLayoutManager().ExecuteLayoutPass()
        Dispatcher.UIThread.RunJobs()
        let stop =
            controls composer |> Seq.find (fun c -> AutomationProperties.GetName c = "停止生成")
        Assert.True(stop.Focusable, "前置条件：输入区停止键可获得焦点")
        let ok = stop.Focus()
        Dispatcher.UIThread.RunJobs()
        Assert.True(ok && stop.IsFocused, "前置条件：键盘焦点落在停止键上")
        chat.SetGenerating(false, "")
        composer.SetGenerating false
        Dispatcher.UIThread.RunJobs()
        Assert.False(stop.Focusable, "没有待发送内容时发送键不可聚焦")
        // 不允许焦点留在停止结束后已经失去操作资格的按钮上。
        let input = controls composer |> Seq.pick (function :? TextBox as b when b.AcceptsReturn -> Some b | _ -> None)
        Assert.True(input.IsFocused, "停止完成后键盘输入必须由文本框承接")
        // 同时覆盖自然完成时的回焦，而不只覆盖用户主动停止。
    finally
        window.Close()
    )

/// 分叉落定后焦点必须回家：三条对话切换路径（新建 / 打开 / 分叉）共用同一归宿，
/// 唯独 ForkFrom 漏了 composer.Focus()。兄弟路径（NewConversation:513 /
/// OpenConversation:520）都在 send 之后无条件把焦点送回输入区（D3/D4），
/// ForkFrom 此前不在其中：键盘用户在编辑弹窗确认后焦点悬在已关闭的对话框原位
/// （对话框关掉时其焦点宿主一并消失），得摸鼠标点回输入区。
[<Trait("Category", "UI")>]
[<Fact>]
let ``forking from a message hands keyboard focus back to the composer`` () =
    Headless.run (fun () ->
    let view = MainView()
    view.Build()
    let window = Window(Content = view, Width = 900.0, Height = 700.0)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()
        let id = Guid.NewGuid()
        handle view (snapshot id)
        (field view "activeConvId").SetValue(view, box (Some id))
        invoke view "ForkFrom" [| box None; box "测试分叉内容" |] |> ignore
        Dispatcher.UIThread.RunJobs()
        // 对话框挂在 OverlayHost 的 root Grid 上（层序：scrim / dialogCard /
        // popupCatcher / popupCard / toastStack）；dialogCard 是第 4 个子。
        let rootChildren = unbox<Grid>(view.Content).Children |> Seq.cast<Control> |> List.ofSeq
        let dialogCard = rootChildren[3]
        Assert.True(dialogCard.IsVisible, "ForkFrom 应打开编辑并分叉对话框")
        let cardTexts =
            dialogCard.GetVisualDescendants()
            |> Seq.choose (function
                | :? TextBlock as tb when not (isNull tb.Text) -> Some tb.Text
                | _ -> None) |> List.ofSeq
        Assert.True(cardTexts |> List.exists (fun t -> t.Contains "编辑并分叉"),
            sprintf "ForkFrom 应打开编辑对话框，实际文本：%A" cardTexts)
        // 提交 → onConfirm 切换对话、发出 fork 命令；焦点回家一行在其后无条件执行
        // （与 New/OpenConversation 同构，不依赖主机确认回调）。Ui.onClick 造的
        // ActionBorder 同时实现 IInvokeProvider（Primitives.fs:183-186），Invoke 与
        // 真人点击同一条 action，比合成指针事件更稳（合成事件对 dialog 层不可靠）。
        let confirm =
            dialogCard.GetVisualDescendants()
            |> Seq.choose (function :? Control as c -> Some c | _ -> None)
            |> Seq.find (fun c -> AutomationProperties.GetName c = "创建分叉")
        let peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement confirm
        let invoke = Assert.IsAssignableFrom<Avalonia.Automation.Provider.IInvokeProvider>(peer)
        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        let composer = (field view "composer").GetValue view :?> Composer
        let input =
            composer.GetVisualDescendants()
            |> Seq.pick (function :? TextBox as t -> Some t | _ -> None)
        Assert.True(input.IsFocused,
            "分叉确认后焦点必须回家到输入框——编辑弹窗关闭时其焦点宿主随之消失，"
            + "不显式移交焦点，键盘用户接下来敲的字没有任何承接（D3/D4）。")
    finally
        window.Close()
    )

// 单条删除有成功 toast（「对话「x」已删除」），批量删除此前一条命令都没挂
// Track、也没有任何提示：删完 N 个对话界面无反馈，命令被拒时同样无声
// （CommandRejected 对未 Track 的 id 是空操作），用户以为删掉了，行还在列表里。
// kelivo side_drawer.dart:752-756 删完即给汇总 snackbar，同一约定。
[<Trait("Category", "UI")>]
[<Fact>]
let ``batch delete reports what it did instead of staying silent`` () =
    Headless.run (fun () ->
    let view = shell ()
    let root = (field view "root").GetValue view :?> Control
    let overlay = (field view "overlay").GetValue view :?> OverlayHost
    let a = Guid.NewGuid()
    let b = Guid.NewGuid()
    let listItem (id: Guid) (title: string) =
        let o = JsonObject()
        o.Add("conversationId", JsonValue.Create(id.ToString()))
        o.Add("title", JsonValue.Create(title))
        o.Add("runtimeState", JsonValue.Create("idle"))
        o :> JsonNode
    let items = JsonArray()
    items.Add(listItem a "甲")
    items.Add(listItem b "乙")
    handle view (ConversationListSnapshot {| items = items; lastCommitId = 0UL |})
    // 进入多选并勾上两行。
    let sidebar = (field view "sidebar").GetValue view
    invoke sidebar "EnterSelection" [| box a |] |> ignore
    invoke sidebar "ToggleSelected" [| box b |] |> ignore
    Dispatcher.UIThread.RunJobs()
    // §214：批量删除不再弹确认框——立即隐去，提示条给汇总与「撤销」。
    // SidebarDesk 不公开 DeleteSelected：走多选条上的「删除」键，与用户同一路径。
    let deleteButton =
        ((sidebar :?> Wanxiang2.SidebarDesk.SidebarDesk).View :> Visual).GetVisualDescendants()
        |> Seq.pick (function
            | :? Control as c when c.IsEffectivelyVisible && c.Focusable
                                   && (string (Avalonia.Automation.AutomationProperties.GetName c)).StartsWith "删除" -> Some c
            | _ -> None)
    deleteButton.RaiseEvent(Avalonia.Input.KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter, Source = deleteButton))
    Dispatcher.UIThread.RunJobs()
    Assert.False(overlay.IsDialogOpen, "删除即撤销：不应再有确认框")
    let texts () =
        controls root
        |> Seq.choose (function :? TextBlock as tb -> Some tb.Text | _ -> None)
        |> List.ofSeq
    Assert.True(texts () |> List.exists (fun s -> s.Contains "已删除 2 个对话"), sprintf "批量删除必须给一条汇总反馈，实际 %A" (texts ()))
    Assert.True(controls root |> Seq.exists (fun c -> AutomationProperties.GetName c = "撤销"), "提示条应带「撤销」")
    // 宽限期内命令尚未提交
    let tracker = (field view "commandFeedback").GetValue view
    let pendingField = tracker.GetType().GetField("pending", flags)
    let pending = pendingField.GetValue tracker :?> System.Collections.IDictionary
    Assert.Equal(0, pending.Count)
    // 宽限期到：两条删除命令进入挂号
    let deletion = (field view "deletion").GetValue view :?> DeletionDesk
    deletion.Commit deletion.Pending.batches.Head.token
    Assert.Equal(2, pending.Count)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``§214 撤销把批量删除的行放回列表且不提交`` () =
    Headless.run (fun () ->
    let view = shell ()
    let root = (field view "root").GetValue view :?> Control
    let a = Guid.NewGuid()
    let items = JsonArray()
    let o = JsonObject()
    o.Add("conversationId", JsonValue.Create(a.ToString()))
    o.Add("title", JsonValue.Create("甲"))
    o.Add("runtimeState", JsonValue.Create("idle"))
    items.Add(o :> JsonNode)
    handle view (ConversationListSnapshot {| items = items; lastCommitId = 0UL |})
    let sidebar = (field view "sidebar").GetValue view
    invoke sidebar "EnterSelection" [| box a |] |> ignore
    Dispatcher.UIThread.RunJobs()
    // SidebarDesk 不公开 DeleteSelected：走多选条上的「删除」键，与用户同一路径。
    let deleteButton =
        ((sidebar :?> Wanxiang2.SidebarDesk.SidebarDesk).View :> Visual).GetVisualDescendants()
        |> Seq.pick (function
            | :? Control as c when c.IsEffectivelyVisible && c.Focusable
                                   && (string (Avalonia.Automation.AutomationProperties.GetName c)).StartsWith "删除" -> Some c
            | _ -> None)
    deleteButton.RaiseEvent(Avalonia.Input.KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter, Source = deleteButton))
    Dispatcher.UIThread.RunJobs()
    let order () = invoke sidebar "GetVisibleOrder" [| |] :?> string list
    Assert.DoesNotContain(a.ToString(), order ())
    // 新列表快照不能让宽限期里的条目闪回：列表与撤销必须读同一个待删 owner。
    handle view (ConversationListSnapshot {| items = items; lastCommitId = 1UL |})
    Assert.DoesNotContain(a.ToString(), order ())
    let undo =
        controls root
        |> Seq.tryFind (fun c -> AutomationProperties.GetName c = "撤销")
        |> Option.defaultWith (fun () -> failwith "缺少「撤销」")
    (undo :?> Border).RaiseEvent(
        KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.None))
    Dispatcher.UIThread.RunJobs()
    Assert.Contains(a.ToString(), order ())
    let deletion = (field view "deletion").GetValue view :?> DeletionDesk
    Assert.Empty deletion.Pending.batches
    )

// 重新生成主机语义 = 删掉尾部整段回复链再重来
// （CommandEngine.fs:220-232 连续 assistant/tool 消息全删）。本项目其余每一个
// 不可恢复操作（删对话、批量删、删消息、删模型服务、删 MCP）都有确认框，
// 唯独重新生成没有——误点一次，上一轮回复连内容带引用全部消失。
// kelivo_chat_message_widget.dart:1346-1375 _confirmRegeneration 同一约定。
[<Trait("Category", "UI")>]
[<Fact>]
let ``regenerate asks before discarding the last reply`` () =
    Headless.run (fun () ->
    let view = shell ()
    let root = (field view "root").GetValue view :?> Control
    let overlay = (field view "overlay").GetValue view :?> OverlayHost
    let composer = (field view "composer").GetValue view :?> Composer
    let a = Guid.NewGuid ()
    select view a
    handle view (snapshotWithReply a)
    // 焦点先在别处：确认通过后 composer.Focus() 是「命令已发出」的可观察代理
    // （shell 在 Regenerate 的确认回调里唯一做的用户可见动作）。
    (controls root |> Seq.tryFind (fun c -> AutomationProperties.GetName c = "新建对话"))
        |> Option.iter (fun b -> b.Focus() |> ignore)
    Dispatcher.UIThread.RunJobs()
    invoke view "Regenerate" [||] |> ignore
    Dispatcher.UIThread.RunJobs()
    // 确认框接管：直接输入区交出焦点、命令未发（焦点没回 composer）。
    Assert.True(overlay.IsDialogOpen, "重新生成前必须先确认")
    Assert.False(composer.IsFocused, "未确认不得驱动命令路径（composer.Focus 不执行）")
    let texts =
        controls overlay.Root
        |> Seq.choose (function :? TextBlock as tb -> Some tb.Text | _ -> None)
        |> List.ofSeq
    Assert.True(texts |> List.exists (fun s -> s.Contains "无法撤销"), "确认框须说明不可恢复")
    // 确认按钮在场：危险语气的确认动作（Dialogs.confirm 的 Danger 档）。
    let ok =
        controls overlay.Root
        |> Seq.filter (fun c -> AutomationProperties.GetName c = "重新生成" && c.IsVisible)
        |> Seq.tryLast
        |> Option.defaultWith (fun () -> failwith "确认框缺少重新生成按钮")
    Assert.True(ok.IsEnabled, "确认动作可用")
    // 取消：确认框关，命令路径仍未执行。
    let cancel =
        controls overlay.Root |> Seq.tryFind (fun c -> AutomationProperties.GetName c = "取消")
        |> Option.defaultWith (fun () -> failwith "确认框缺少取消按钮")
    (cancel :?> Border).RaiseEvent(
        KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.None))
    Dispatcher.UIThread.RunJobs()
    Assert.False(overlay.IsDialogOpen, "取消后确认框应关闭")
    Assert.False(composer.IsFocused, "取消不得发出命令")
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``retry after an empty failure regenerates without a confirmation`` () =
    Headless.run (fun () ->
    let view = shell ()
    let overlay = (field view "overlay").GetValue view :?> OverlayHost
    let a = Guid.NewGuid ()
    select view a
    handle view (snapshot a)
    invoke view "Regenerate" [||] |> ignore
    Dispatcher.UIThread.RunJobs()
    // 尾部什么都没有：「将丢弃当前回复」不成立，直接重试（决策 221）。
    Assert.False(overlay.IsDialogOpen, "没有可丢的内容时不该再问一次")
    )
