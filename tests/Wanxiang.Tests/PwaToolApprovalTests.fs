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

    let private layoutAt (width: float) (control: Control) =
        let host = Border(Width = width, Child = control)
        host.Measure(Size(width, infinity))
        host.Arrange(Rect(0.0, 0.0, width, host.DesiredSize.Height))
        Dispatcher.UIThread.RunJobs()

    [<Fact>]
    let ``审批面板：放在输入框正上方的附属位，与输入框同列同宽，不浮层遮挡消息`` () =
        Headless.run (fun () ->
            let panel = Wanxiang2.ApprovalDesk.ApprovalDesk(SharedTheme.manager, (OverlayHost(Grid())).Desk, ignore).View
            Assert.Equal(HorizontalAlignment.Stretch, panel.HorizontalAlignment)
            // 宽度由宿主列决定，面板自己不另设上限（此前 720 与输入框 748 错位）
            Assert.True(Double.IsPositiveInfinity panel.MaxWidth)
            let composer =
                Composer(
                    { submit = fun _ -> true
                      stopGeneration = ignore
                      pickAttachment = ignore
                      removeAttachment = ignore
                      openModelPicker = ignore
                      dropFiles = ignore
                      pasteFromClipboard = fun () -> false })
            composer.SetAccessory panel
            // 附属位在输入框外层列里，排在输入框之前：同一个 MaxWidth = 阅读宽度
            match (panel.Parent :?> Control).Parent with
            | :? StackPanel as outer ->
                Assert.Equal(Tokens.readingWidth, outer.MaxWidth)
                Assert.Equal(0, outer.Children.IndexOf(panel.Parent :?> Control))
            | other -> failwithf "accessory slot not inside composer column: %A" other)
