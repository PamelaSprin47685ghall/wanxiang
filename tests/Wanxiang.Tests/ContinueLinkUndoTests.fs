module Wanxiang.Tests.ContinueLinkUndoTests

// 决策 230 继续生成 / 231 消息链接 / 232 Ctrl+Z 撤销删除。

open System
open System.Text.Json.Nodes
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Store
open Wanxiang.UI
open Wanxiang.Tests.Helpers

let private msg role text = { MessageView.empty with role = role; text = text }

// ---------- 230 ----------

[<Fact>]
let ``230 失败时保留有正文的半截回复，重试或重新开始即清掉`` () =
    let runs = ConversationRuns()
    let conv, gen = Guid.NewGuid(), Guid.NewGuid()
    runs.Start(conv, gen)
    runs.Delta(conv, gen, msg "assistant" "写到一半  ") |> ignore
    let err = GenerationError.create ProviderTimeout "超时"
    Assert.True(runs.Finish(conv, gen, Some err, None))
    Assert.Equal(Some "写到一半  ", (runs.Get(Some conv)).partial |> Option.map _.text)
    runs.ClearError conv
    Assert.True((runs.Get(Some conv)).partial.IsNone)

[<Fact>]
let ``230 成功结束或空回复不留半截`` () =
    let runs = ConversationRuns()
    let conv = Guid.NewGuid()
    let g1 = Guid.NewGuid()
    runs.Start(conv, g1)
    runs.Delta(conv, g1, msg "assistant" "完整") |> ignore
    runs.Finish(conv, g1, None, None) |> ignore
    Assert.True((runs.Get(Some conv)).partial.IsNone)
    let g2 = Guid.NewGuid()
    runs.Start(conv, g2)
    runs.Delta(conv, g2, msg "assistant" "   ") |> ignore
    runs.Finish(conv, g2, Some(GenerationError.create ProviderTimeout "x"), None) |> ignore
    Assert.True((runs.Get(Some conv)).partial.IsNone)

[<Fact>]
let ``230 半截回复编成去尾空白的助手消息`` () =
    let json = ConversationRuns.PartialMessageJson(msg "assistant" "半句\n\n ")
    Assert.Equal("assistant", json["role"].GetValue<string>())
    Assert.Equal("半句", json["contents"].[0].["text"].GetValue<string>())

[<Fact>]
let ``230 chat.continue 编解码往返`` () =
    let inv, conv = Guid.NewGuid(), Guid.NewGuid()
    let cmd = ContinueResponse {| invocationId = inv; conversationId = conv; messageJson = ConversationRuns.PartialMessageJson(msg "assistant" "半句") |}
    let encoded = WireCodec.encodeCommand cmd
    Assert.Contains("\"type\":\"chat.continue\"", encoded)
    match WireCodec.tryDecodeCommand encoded with
    | Ok(ContinueResponse d) ->
        Assert.Equal(conv, d.conversationId)
        Assert.Equal(inv, d.invocationId)
        Assert.Equal("半句", d.messageJson["contents"].[0].["text"].GetValue<string>())
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``230 继续生成只接受有正文的助手消息`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        use coordinator = new CommitCoordinator(dir, replayOutcome dir, ignore, ignore)
        let convId = newConversationId ()
        coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ] |> ignore
        coordinator.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "问" } ] |> ignore
        let plan json =
            CommandEngine.planStamped coordinator.Stamps coordinator.Projection coordinator.Projection.latestCommitId
                (ContinueResponse {| invocationId = Guid.NewGuid(); conversationId = convId; messageJson = json |})
        match plan (userMessageJson "冒充") with
        | Rejected(ValidationError _) -> ()
        | other -> failwithf "user role should be rejected, got %A" other
        match plan (ConversationRuns.PartialMessageJson(msg "assistant" "   ")) with
        | Rejected(ValidationError _) -> ()
        | other -> failwithf "empty should be rejected, got %A" other
        match plan (ConversationRuns.PartialMessageJson(msg "assistant" "半句")) with
        | Planned p ->
            match p.events with
            | [ AgentMessageRecorded d ] -> Assert.Equal(convId, d.conversationId)
            | e -> failwithf "unexpected events %A" e
        | other -> failwithf "expected plan, got %A" other
    finally
        cleanup dir

let private findNamed (root: Control) (name: string) =
    let rec walk (c: Control) : seq<Control> =
        seq {
            yield c
            match c with
            | :? Panel as p -> for ch in p.Children do yield! walk ch
            | :? ContentControl as cc when (cc.Content :? Control) -> yield! walk (cc.Content :?> Control)
            | :? Decorator as d when not (isNull d.Child) -> yield! walk d.Child
            | _ -> ()
        }
    walk root |> Seq.tryFind (fun c -> AutomationProperties.GetName c = name)

[<Trait("Category", "UI")>]
[<Fact>]
let ``230 错误卡有半截回复时先给「继续生成」，没有时不给`` () =
    Headless.run (fun () ->
        let err = GenerationError.create ProviderTimeout "超时"
        let mutable continued = false
        let card = MessageCard.errorCardWith err ignore None (Some(fun () -> continued <- true))
        let btn = findNamed card "继续生成"
        Assert.True(btn.IsSome)
        Assert.True((findNamed card "重试生成").IsSome, "重试仍在")
        btn.Value.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = btn.Value))
        Assert.True continued
        Assert.True((findNamed (MessageCard.errorCard err ignore None) "继续生成").IsNone)
        Assert.True((findNamed (MessageCard.incompleteMarker ()) "回复未完成").IsSome))

// ---------- 231 ----------

[<Fact>]
let ``231 消息链接两种写法往返，普通搜索词不误认`` () =
    let conv = Guid.NewGuid()
    let desktop = MessageLink.format None conv 42UL
    Assert.StartsWith("wanxiang://open?", desktop)
    Assert.Equal(Some(conv, 42UL), MessageLink.tryParse desktop)
    let web = MessageLink.format (Some "https://host.example/app/#old") conv 7UL
    Assert.Equal(sprintf "https://host.example/app/#open?c=%O&m=7" conv, web)
    Assert.Equal(Some(conv, 7UL), MessageLink.tryParse ("  " + web + " "))
    Assert.Equal(None, MessageLink.tryParse "如何打开链接")
    Assert.Equal(None, MessageLink.tryParse "https://example.com/#open?c=bad&m=1")
    Assert.Equal(None, MessageLink.tryParse "https://example.com/page")

// ---------- 232 ----------

[<Trait("Category", "UI")>]
[<Fact>]
let ``232 Ctrl+Z 映射到撤销，并只触发可见提示条上的「撤销」`` () =
    Headless.run (fun () ->
        Assert.Equal(UndoPending, ShortcutRouter.resolve (KeyEventArgs(Key = Key.Z, KeyModifiers = KeyModifiers.Control)))
        Assert.Equal(UndoPending, ShortcutRouter.resolve (KeyEventArgs(Key = Key.Z, KeyModifiers = KeyModifiers.Meta)))
        Assert.NotEqual(UndoPending, ShortcutRouter.resolve (KeyEventArgs(Key = Key.Z, KeyModifiers = (KeyModifiers.Control ||| KeyModifiers.Shift))))
        let root = Grid()
        let overlay = OverlayHost(root)
        Assert.False(overlay.TryInvokeToastAction "撤销")
        let mutable undone = 0
        overlay.ToastWith("已删除「甲」", Info, Some("撤销", fun () -> undone <- undone + 1))
        Assert.True(overlay.HasToastAction "撤销")
        Assert.True(overlay.TryInvokeToastAction "撤销")
        Assert.Equal(1, undone)
        // 提示条已关：再按不会重复撤销
        Assert.False(overlay.TryInvokeToastAction "撤销")
        Assert.Equal(1, undone))
