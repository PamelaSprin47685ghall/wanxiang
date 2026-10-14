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
        use coordinator = new EventStore(dir)
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

// ---------- 231 ----------

// ---------- 232 ----------

[<Trait("Category", "UI")>]
[<Fact>]
let ``232 Ctrl+Z 映射到撤销，并只触发可见提示条上的「撤销」`` () =
    Headless.run (fun () ->
        Assert.Equal(ShortcutAction.UndoPending, KeyShortcut.resolve (KeyEventArgs(Key = Key.Z, KeyModifiers = KeyModifiers.Control)))
        Assert.Equal(ShortcutAction.UndoPending, KeyShortcut.resolve (KeyEventArgs(Key = Key.Z, KeyModifiers = KeyModifiers.Meta)))
        Assert.NotEqual(ShortcutAction.UndoPending, KeyShortcut.resolve (KeyEventArgs(Key = Key.Z, KeyModifiers = (KeyModifiers.Control ||| KeyModifiers.Shift))))
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
