namespace Wanxiang.Tests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Automation.Provider
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.UI

/// PWA 审批对等：能力声明走共用的 AG-UI 编码（与平台无关），审批卡在手机宽度上可触控、可读。
module PwaToolApprovalProtocolTests =

    [<Fact>]
    let ``能力声明与审批决定经 AG-UI 编码往返不丢（PWA 与桌面共用同一编码路径）`` () =
        match WireAgui.tryDecode (WireAgui.encode (ToolApprovalCapability {| supported = true |})) with
        | Ok(Some(ToolApprovalCapability d)) -> Assert.True d.supported
        | other -> failwithf "unexpected %A" other
        let id = Guid.NewGuid()
        match WireAgui.tryDecode (WireAgui.encode (ToolApprovalDecision {| approvalId = id; approved = true |})) with
        | Ok(Some(ToolApprovalDecision d)) ->
            Assert.Equal(id, d.approvalId)
            Assert.True d.approved
        | other -> failwithf "unexpected %A" other

[<Trait("Category", "UI")>]
module PwaToolApprovalUiTests =

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
        descendants root |> Seq.find (fun c -> AutomationProperties.GetAutomationId c = id)

    let private invoke (control: Control) =
        let peer = ControlAutomationPeer.CreatePeerForElement control
        (Assert.IsAssignableFrom<IInvokeProvider> peer).Invoke()
        Dispatcher.UIThread.RunJobs()

    let private request () : ApprovalRequestView =
        { approvalId = Guid.NewGuid()
          conversationId = Guid.NewGuid()
          toolName = "builtin_file_read"
          summary = "读取文件 /home/a/very/long/path/that/should/wrap/on/a/phone/screen/notes-and-more-notes.txt"
          argumentsJson = """{"path":"/home/a/very/long/path/that/should/wrap/on/a/phone/screen/notes-and-more-notes.txt"}"""
          policy = "first" }

    let private layoutAt (width: float) (control: Control) =
        let host = Border(Width = width, Child = control)
        host.Measure(Size(width, infinity))
        host.Arrange(Rect(0.0, 0.0, width, host.DesiredSize.Height))
        Dispatcher.UIThread.RunJobs()

    [<Fact>]
    let ``审批卡：批准/拒绝/查看参数的命中高度不低于触控下限`` () =
        Headless.run (fun () ->
            let card = ToolApprovalCard.build (request ()) ignore
            layoutAt 360.0 card
            for id in [ "tool-approval-approve"; "tool-approval-deny"; "tool-approval-args-toggle" ] do
                let c = byId card id
                Assert.True(c.MinHeight >= Spacing.Hit.touch, id)
                Assert.True(c.Bounds.Height >= Spacing.Hit.touch, sprintf "%s height %f" id c.Bounds.Height))

    [<Fact>]
    let ``审批卡：手机宽度下摘要换行而不溢出，两个按钮等宽且都在卡内`` () =
        Headless.run (fun () ->
            let width = 320.0
            let card = ToolApprovalCard.build (request ()) ignore
            layoutAt width card
            let summary = byId card "tool-approval-summary"
            Assert.True(summary.Bounds.Width <= width, sprintf "summary width %f" summary.Bounds.Width)
            Assert.True(summary.Bounds.Height > 30.0, "长摘要应换成多行")
            let approve = byId card "tool-approval-approve"
            let deny = byId card "tool-approval-deny"
            Assert.Equal(approve.Bounds.Width, deny.Bounds.Width, 1)
            let right (c: Control) = (c.TranslatePoint(Point(c.Bounds.Width, 0.0), card)).Value.X
            Assert.True(right deny <= width + 0.5, sprintf "deny right %f" (right deny)))

    [<Fact>]
    let ``审批卡：触屏没有悬停提示，点「查看参数」展开完整参数，再点收起`` () =
        Headless.run (fun () ->
            let card = ToolApprovalCard.build (request ()) ignore
            let args = byId card "tool-approval-args" :?> TextBlock
            let toggle = byId card "tool-approval-args-toggle"
            Assert.Equal(Media.TextWrapping.NoWrap, args.TextWrapping)
            Assert.Equal("查看参数", AutomationProperties.GetName toggle)
            invoke toggle
            Assert.Equal(Media.TextWrapping.Wrap, args.TextWrapping)
            Assert.Equal(Media.TextTrimming.None, args.TextTrimming)
            Assert.Equal("收起参数", AutomationProperties.GetName toggle)
            invoke toggle
            Assert.Equal(Media.TextWrapping.NoWrap, args.TextWrapping))

    [<Fact>]
    let ``审批卡：没有参数时不显示「查看参数」`` () =
        Headless.run (fun () ->
            let card = ToolApprovalCard.build { request () with argumentsJson = "{}" } ignore
            Assert.False((byId card "tool-approval-args-toggle").IsVisible))

    [<Fact>]
    let ``审批面板：窄屏占满宽度，宽屏不超过 720`` () =
        Headless.run (fun () ->
            let panel = ToolApprovalPanel(fun _ _ -> ())
            Assert.Equal(HorizontalAlignment.Stretch, panel.HorizontalAlignment)
            Assert.Equal(720.0, panel.MaxWidth))
