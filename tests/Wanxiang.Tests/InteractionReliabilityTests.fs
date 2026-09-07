module Wanxiang.Tests.InteractionReliabilityTests

open System
open System.Reflection
open System.Text.Json.Nodes
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Automation.Provider
open Avalonia.Controls
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.UI

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
    Headless.ensure ()
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
let private start id generation =
    GenerationStarted {| conversationId = id; generationId = generation; providerId = "mock"; model = "mock" |}
let private finish id generation =
    GenerationFinished {| conversationId = id; generationId = generation; status = "completed"; error = None; usage = None |}
let private stopVisible view =
    let composer = (field view "composer").GetValue view :?> Composer
    controls composer |> Seq.exists (fun c -> AutomationProperties.GetName c = "停止生成")

[<Fact>]
let ``composer retains input when dispatch does not take ownership`` () =
    Headless.ensure ()
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

[<Fact>]
let ``finishing a background conversation does not stop the foreground generation`` () =
    let view = shell ()
    let a, b, ga, gb = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    select view a
    handle view (snapshot a)
    handle view (start a ga)
    Assert.True(stopVisible view)
    handle view (finish b gb)
    Assert.True(stopVisible view)

[<Fact>]
let ``finishing an old generation does not stop its replacement`` () =
    let view = shell ()
    let a, oldGeneration, currentGeneration = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    select view a
    handle view (snapshot a)
    handle view (start a oldGeneration)
    handle view (start a currentGeneration)
    handle view (finish a oldGeneration)
    Assert.True(stopVisible view)

[<Fact>]
let ``late snapshot updates data without taking over navigation`` () =
    let view = shell ()
    let a, b = Guid.NewGuid(), Guid.NewGuid()
    select view b
    handle view (snapshot a)
    Assert.Equal(Some b, (field view "activeConvId").GetValue(view) :?> Guid option)

[<Fact>]
let ``a declined submission leaves the composer editable and intact`` () =
    Headless.ensure ()
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

[<Fact>]
let ``generation accepts a queued message without turning send into cancel`` () =
    Headless.ensure ()
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

[<Fact>]
let ``switching conversations restores their separate drafts`` () =
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

[<Fact>]
let ``outbox keeps the exact command and attachment references across a disconnect`` () =
    let outbox = MessageOutbox()
    let attachment = { attachmentId = Guid.NewGuid(); sha256 = "abc"; size = 12L
                       mediaType = "text/plain"; fileName = "notes.txt"; ready = true }
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

[<Fact>]
let ``preparing first message retains its original creation command for retry`` () =
    let outbox = MessageOutbox()
    let conversationId = Guid.NewGuid()
    let creation = CreateConversation {| invocationId = Guid.NewGuid(); conversationId = conversationId
                                         title = "新会话"; config = SessionConfig.empty |}
    let item = outbox.Stage("server", conversationId, "第一条不能丢", [], Some creation)
    outbox.Disconnect "server"
    let retained = (outbox.TryFind(PendingMessage.invocationId item)).Value
    Assert.Equal(WireCodec.encodeCommand creation, WireCodec.encodeCommand retained.creation.Value)
    Assert.False retained.creationConfirmed
    outbox.ConversationReady("server", conversationId)
    Assert.True((outbox.TryFind(PendingMessage.invocationId item)).Value.creationConfirmed)

[<Fact>]
let ``unacknowledged sends expire but an explicitly queued message does not`` () =
    let outbox = MessageOutbox()
    let sending = outbox.Stage("server", Guid.NewGuid(), "发送中", [], None)
    let queued = outbox.Stage("server", Guid.NewGuid(), "长任务后插入", [], None)
    outbox.Accept(PendingMessage.invocationId queued)
    Assert.True(outbox.Expire(DateTimeOffset.UtcNow.AddMinutes 2.0, TimeSpan.FromSeconds 30.0))
    Assert.True(PendingMessage.canRetry (outbox.TryFind(PendingMessage.invocationId sending)).Value)
    Assert.Equal(QueuedMessage, (outbox.TryFind(PendingMessage.invocationId queued)).Value.state)

[<Fact>]
let ``drafts and pending messages never cross server identities`` () =
    let drafts = ComposerDrafts()
    let id = Guid.NewGuid()
    (drafts.Get("a", Some id)).text <- "只属于 a"
    Assert.Equal("", (drafts.Get("b", Some id)).text)
    let outbox = MessageOutbox()
    outbox.Stage("a", id, "私有内容", [], None) |> ignore
    Assert.Empty(outbox.Items("b", Some id))

[<Fact>]
let ``stale deltas and finished snapshots cannot resurrect retired generations`` () =
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

[<Fact>]
let ``configuration failure without a started event is still visible in its own conversation`` () =
    let view = shell ()
    let id = Guid.NewGuid()
    select view id
    handle view (snapshot id)
    let error = GenerationError.create ConfigInvalid "服务商已删除"
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

[<Fact>]
let ``snapshot generation identity roundtrips and legacy snapshots remain readable`` () =
    let id, generation = Guid.NewGuid(), Guid.NewGuid()
    let json = JsonNode.Parse(WireCodec.encode(snapshot id)).AsObject()
    let payload = json["payload"].AsObject()
    payload["runtimeState"] <- "generating"
    payload["generationId"] <- generation.ToString("D")
    match WireCodec.tryDecode(json.ToJsonString()) with
    | Ok (ConversationSnapshot d) ->
        Assert.Equal(Some generation, d.generationId)
        let run = ConversationRuns()
        run.Snapshot(id, d.runtimeState, d.generationId)
        Assert.Equal(Some generation, (run.Get(Some id)).generationId)
    | other -> failwithf "unexpected snapshot: %A" other
    json["payload"].AsObject().Remove "generationId" |> ignore
    match WireCodec.tryDecode(json.ToJsonString()) with
    | Ok (ConversationSnapshot d) -> Assert.True d.generationId.IsNone
    | other -> failwithf "legacy snapshot rejected: %A" other

[<Fact>]
let ``disconnected command transport reports failure instead of silent success`` () =
    let client = Wanxiang.Client.WsClient()
    let command = RenameConversation {| invocationId = Guid.NewGuid(); conversationId = Guid.NewGuid(); title = "不会发出" |}
    Assert.False(client.TrySendCommandAsync(command).GetAwaiter().GetResult())
    let before = client.ConnectionGeneration
    client.Disconnect()
    Assert.True(client.ConnectionGeneration > before)

[<Fact>]
let ``failed first messages remain reachable after switching away`` () =
    Headless.ensure ()
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
            not (String.IsNullOrEmpty name) && name.StartsWith("查看另一个会话"))
        let action = ControlAutomationPeer.CreatePeerForElement button |> Assert.IsAssignableFrom<IInvokeProvider>
        action.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(Some id, opened)
        Assert.Equal(1, outbox.Count)
    finally window.Close()

[<Fact>]
let ``selecting an unloaded conversation does not display the previous conversation body`` () =
    let view = shell ()
    let a, b, generation = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    select view a
    handle view (snapshot a)
    handle view (start a generation)
    handle view (
        GenerationDelta
            {| conversationId = a
               generationId = generation
               payload = JsonNode.Parse("""{"role":"assistant","contents":[{"text":"只属于会话 A 的正文"}]}""") |})
    invoke view "SelectConversation" [| box (Some b) |] |> ignore
    let chat = (field view "chat").GetValue view :?> ChatView
    let visibleText =
        controls chat |> Seq.choose (function
            | :? SelectableTextBlock as text -> Some text.Text
            | :? TextBlock as text -> Some text.Text
            | _ -> None)
    Assert.DoesNotContain("只属于会话 A 的正文", visibleText)
    let runs = (field view "runs").GetValue view :?> ConversationRuns
    Assert.Equal("只属于会话 A 的正文", (runs.Get(Some a)).message.Value.text)
