namespace Wanxiang.Tests

open Wanxiang2.SettingsScreen
open System
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Automation.Provider
open Avalonia.Controls
open Avalonia.Threading
open Xunit
open Wanxiang.UI
open Wanxiang2.MessageCardView
open Wanxiang.Tests

/// W3 界面层：审批卡片、人话摘要、工具设置页（第 70–72 节）。
[<Trait("Category", "UI")>]
module W3ToolApprovalUiTests =

    let rec private descendants (control: Control) : seq<Control> =
        seq {
            yield control
            match control with
            | :? Panel as panel -> for child in panel.Children do yield! descendants child
            | :? Decorator as d when not (isNull d.Child) -> yield! descendants d.Child
            | :? ContentControl as host ->
                match host.Content with
                | :? Control as child -> yield! descendants child
                | _ -> ()
            | _ -> ()
        }

    let private byId (root: Control) (id: string) =
        descendants root |> Seq.tryFind (fun c -> AutomationProperties.GetAutomationId c = id)

    let private invoke (control: Control) =
        let peer = ControlAutomationPeer.CreatePeerForElement control
        (Assert.IsAssignableFrom<IInvokeProvider> peer).Invoke()
        Dispatcher.UIThread.RunJobs()

    let private textOf (c: Control) = match c with :? TextBlock as t when not (isNull t.Text) -> t.Text | _ -> ""

    [<Fact>]
    let ``审批卡：说明批准的后果（首次询问 / 每次询问）`` () =
        Assert.Contains("不会再问", ApprovalCardText.policyNote "first")
        Assert.Contains("每次", ApprovalCardText.policyNote "always")
        Assert.Contains("已经被批准", ApprovalCardText.alreadyHandledText "approved")
        Assert.Contains("已经被拒绝", ApprovalCardText.alreadyHandledText "denied")
        Assert.Contains("失效", ApprovalCardText.alreadyHandledText "expired")

    let private card (name: string) (args: string) =
        let msg: MessageView =
            { role = "assistant"; text = ""; reasoning = ""
              toolCalls = [ { callId = "c1"; name = name; argumentsJson = args; result = Some "ok" } ]
              toolResults = []; attachments = []; commitId = Some 1UL; committedAt = Some DateTimeOffset.UtcNow }
        let ctx: Wanxiang2.MessageCardView.CardContext =
            { FontSize = 14.0; AutoCollapseReasoning = false; Streaming = false; IsLastAssistant = false
              Usage = None; MissingAttachments = Set.empty; AwaitingApproval = Set.empty; BrandAvatar = fun () -> TextBlock() :> Control }
        let actions: Wanxiang2.MessageCardView.CardActions =
            { CopyText = ignore; Regenerate = ignore; EditAndFork = ignore; DeleteMessage = ignore
              DownloadAttachment = ignore; OpenLink = ignore; CopyLink = None; Recover = None }
        Cards.render msg ctx actions (Some 0)

    [<Fact>]
    let ``工具调用卡：先给人话摘要，原始参数仍保留`` () =
        Headless.run (fun () ->
            let time = card "builtin_time" "{}"
            Assert.Equal("查看当前时间", textOf (byId time "tool-human-summary").Value)
            let file = card "builtin_file_read" """{"path":"/etc/hosts"}"""
            Assert.Equal("读取文件 /etc/hosts", textOf (byId file "tool-human-summary").Value)
            Assert.Contains(descendants file, fun c -> (textOf c).Contains "/etc/hosts" && AutomationProperties.GetAutomationId c <> "tool-human-summary")
            let mcp = card "mcp_fs_read_file" """{"p":1}"""
            Assert.Equal("调用 fs_read_file（展开看参数）", textOf (byId mcp "tool-human-summary").Value))

    let private stubActions (updates: ResizeArray<JsonObject>) : Wanxiang2.SettingsScreen.SettingsHooks =
        { UpsertProvider = fun _ cb -> cb true
          DeleteProvider = ignore
          ProbeProvider = ignore
          UpsertMcp = fun _ cb -> cb true
          DeleteMcp = ignore
          UpdateGeneration = fun payload cb -> updates.Add payload; cb true
          SavePrefs = ignore }
