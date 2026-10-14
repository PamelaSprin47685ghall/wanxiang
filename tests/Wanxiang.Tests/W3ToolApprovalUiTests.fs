namespace Wanxiang.Tests

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
        let ctx: MessageContext =
            { fontSize = 14.0; autoCollapseReasoning = false; streaming = false; isLastAssistant = false
              usage = None; missingAttachments = Set.empty; awaitingApproval = Set.empty; brandAvatar = fun () -> TextBlock() :> Control }
        let actions: MessageActions =
            { copyText = ignore; regenerate = ignore; editAndFork = ignore; deleteMessage = ignore
              downloadAttachment = ignore; openLink = ignore }
        MessageCard.render msg ctx actions (Some 0)

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

    let private stubActions (updates: ResizeArray<JsonObject>) : SettingsActions =
        { upsertProvider = fun _ cb -> cb true
          deleteProvider = ignore
          probeProvider = ignore
          upsertMcp = fun _ cb -> cb true
          deleteMcp = ignore
          updateGeneration = fun payload cb -> updates.Add payload; cb true
          savePrefs = ignore
          toast = fun _ _ -> () }

    [<Fact>]
    let ``工具设置：每个工具有审批方式入口，授权文件夹有入口；空授权时文案指向入口`` () =
        Headless.run (fun () ->
            let updates = ResizeArray<JsonObject>()
            let tools = SettingsTools(OverlayHost(Grid()), stubActions updates)
            let built = tools.Build()
            tools.SetCatalog Catalog.empty
            Assert.True((byId built "tools-add-folder").IsSome)
            Assert.Contains(descendants built, fun c -> (textOf c).Contains "授权文件夹" )
            Assert.Contains(descendants built, fun c -> (textOf c).Contains "还没有授权任何文件夹")
            let tool: ToolInfo = { id = "builtin:time"; label = "当前时间"; description = "返回时间"; source = "builtin"; serverId = None }
            let catalog =
                { Catalog.empty with
                    tools = [ tool ]
                    generation = { Catalog.empty.generation with fileReadRoots = [ "/data" ]; toolPolicyDefault = "first"; toolPolicy = Map.ofList [ "builtin:time", "always" ] } }
            tools.SetCatalog catalog
            let policyButton = (byId built "tool-policy-builtin:time").Value
            Assert.Equal("当前时间 的审批方式：每次询问", AutomationProperties.GetName policyButton)
            // 已授权的文件夹出现，并可移除
            Assert.Contains(descendants built, fun c -> textOf c = "/data")
            let remove = (descendants built |> Seq.find (fun c -> AutomationProperties.GetName c = "移除文件夹授权 /data"))
            invoke remove
            Assert.Equal(1, updates.Count)
            let first = updates.[0]
            let tools0 = first["tools"].AsObject()
            let roots = tools0["fileReadRoots"].AsArray()
            Assert.Equal(0, roots.Count)
            // 移除授权时不会冲掉审批策略
            let policy = tools0["policy"].AsObject()
            Assert.Equal("first", policy["default"].GetValue<string>())
            Assert.Equal("always", policy["builtin:time"].GetValue<string>()))
