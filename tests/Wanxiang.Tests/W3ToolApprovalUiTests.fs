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

    let private request (conv: Guid) : ApprovalRequestView =
        { approvalId = Guid.NewGuid()
          conversationId = conv
          callId = "call-1"; toolName = "builtin_file_read"
          summary = "读取文件 /home/a/notes.txt"
          argumentsJson = """{"path":"/home/a/notes.txt"}"""
          policy = "always" }

    [<Fact>]
    let ``审批卡：展示人话摘要，批准与拒绝各只触发一次并立即置灰`` () =
        Headless.run (fun () ->
            for approve in [ true; false ] do
                let decisions = ResizeArray<bool>()
                let card = ToolApprovalCard.build (request (Guid.NewGuid())) decisions.Add
                Assert.Equal("读取文件 /home/a/notes.txt", textOf (byId card "tool-approval-summary").Value)
                let button = byId card (if approve then "tool-approval-approve" else "tool-approval-deny")
                invoke button.Value
                invoke button.Value // 重复点击不应再发
                invoke (byId card (if approve then "tool-approval-deny" else "tool-approval-approve")).Value
                Assert.Equal<bool list>([ approve ], List.ofSeq decisions)
                let status = byId card "tool-approval-status"
                Assert.True status.Value.IsVisible
                Assert.Equal(ApprovalCardText.sentText approve, textOf status.Value))

    [<Fact>]
    let ``审批卡：说明批准的后果（首次询问 / 每次询问）`` () =
        Assert.Contains("不会再问", ApprovalCardText.policyNote "first")
        Assert.Contains("每次", ApprovalCardText.policyNote "always")
        Assert.Contains("已经被批准", ApprovalCardText.alreadyHandledText "approved")
        Assert.Contains("已经被拒绝", ApprovalCardText.alreadyHandledText "denied")
        Assert.Contains("失效", ApprovalCardText.alreadyHandledText "expired")

    [<Fact>]
    let ``审批面板：只显示当前对话的卡片，已处理即移除，断线清空`` () =
        Headless.run (fun () ->
            let sent = ResizeArray<Guid * bool>()
            let panel = ToolApprovalPanel(fun id ok -> sent.Add((id, ok)))
            let a, b = Guid.NewGuid(), Guid.NewGuid()
            let ra, rb = request a, request b
            panel.SetConversation(Some a)
            panel.Upsert ra
            panel.Upsert rb
            Assert.Equal(2, panel.Pending.Length)
            Assert.Single(panel.Children) |> ignore
            Assert.True panel.IsVisible
            // 点卡片上的批准，决定带着正确的 approvalId 发出
            invoke (byId (panel :> Control) "tool-approval-approve").Value
            Assert.Equal<(Guid * bool) list>([ ra.approvalId, true ], List.ofSeq sent)
            // 同一审批重复到达（重连补发）不产生第二张卡
            panel.Upsert ra
            Assert.Equal(2, panel.Pending.Length)
            Assert.Single(panel.Children) |> ignore
            panel.SetConversation(Some b)
            let shownIds =
                descendants (panel :> Control)
                |> Seq.map (fun c -> match AutomationProperties.GetAutomationId c with null -> "" | id -> id)
                |> Seq.filter (fun id -> id.StartsWith "tool-approval-" && id.Length > 40)
                |> List.ofSeq
            Assert.Equal<string list>([ sprintf "tool-approval-%O" rb.approvalId ], shownIds)
            panel.Remove rb.approvalId
            Assert.Empty(panel.Children)
            Assert.False panel.IsVisible
            panel.SetConversation(Some a)
            Assert.Single(panel.Children) |> ignore
            panel.Clear()
            Assert.Empty(panel.Pending)
            Assert.False panel.IsVisible)

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
