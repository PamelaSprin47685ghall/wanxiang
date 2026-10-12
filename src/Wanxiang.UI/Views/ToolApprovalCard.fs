namespace Wanxiang.UI

open System
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Wanxiang.Core

/// 一条等待用户决定的工具审批（运行时状态，只存在于内存；第 71 节）。
type ApprovalRequestView = {
    approvalId: Guid
    conversationId: Guid
    toolName: string
    /// 人话摘要（主机用 ToolSummary 生成，两端同一份文案）
    summary: string
    argumentsJson: string
    /// "first"（首次询问）或 "always"（每次询问）
    policy: string
}

module ToolApprovalCard =

    /// 审批卡上给用户看的策略说明：批准会产生什么后续效果要说清楚。
    let policyNote (policy: string) : string =
        match policy with
        | "first" -> "批准后，这个对话里再用同一个工具不会再问。"
        | _ -> "每次调用这个工具都会询问。"

    /// 状态文案：用户点了按钮、等主机确认期间。
    let sentText (approved: bool) = if approved then "已批准，正在继续…" else "已拒绝，正在通知模型…"

    /// 「已处理」说明（晚到的决定，或服务重启后失效的审批）。
    let alreadyHandledText (status: string) : string =
        match status with
        | "approved" -> "这个审批已经被批准。"
        | "denied" -> "这个审批已经被拒绝。"
        | "expired" -> "这个审批已超时或已失效。"
        | "cancelled" -> "这次生成已取消，审批作废。"
        | _ -> "这个审批已经处理过了。"

    /// 渲染一张审批卡。`decide true` = 批准，`decide false` = 拒绝。
    /// 点击后按钮立即置灰并显示「已发送」状态，真正的结果由主机的 resolved 事件移除卡片。
    let build (req: ApprovalRequestView) (decide: bool -> unit) : Control =
        let icon = Icons.wrench Tokens.warning
        icon.VerticalAlignment <- VerticalAlignment.Top
        let title =
            TextBlock(
                Text = "模型想使用工具，需要你批准",
                FontSize = Tokens.fontBody,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text,
                TextWrapping = TextWrapping.Wrap)
        let summary =
            TextBlock(
                Text = req.summary,
                FontSize = Tokens.fontBody,
                Foreground = Tokens.text,
                TextWrapping = TextWrapping.Wrap)
        Avalonia.Automation.AutomationProperties.SetAutomationId(summary, "tool-approval-summary")
        let note =
            TextBlock(
                Text = policyNote req.policy,
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = ReadingRhythm.captionLineHeight)
        let status =
            TextBlock(
                Text = "",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                IsVisible = false)
        Avalonia.Automation.AutomationProperties.SetAutomationId(status, "tool-approval-status")
        Avalonia.Automation.AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Polite)
        let mutable decided = false
        let mutable approveButton: Border = null
        let mutable denyButton: Border = null
        let choose (approved: bool) () =
            if not decided then
                decided <- true
                Ui.setEnabled approveButton false
                Ui.setEnabled denyButton false
                status.Text <- sentText approved
                status.IsVisible <- true
                decide approved
        approveButton <- Ui.button Ui.Primary "批准" (choose true)
        denyButton <- Ui.button Ui.Secondary "拒绝" (choose false)
        Avalonia.Automation.AutomationProperties.SetAutomationId(approveButton, "tool-approval-approve")
        Avalonia.Automation.AutomationProperties.SetAutomationId(denyButton, "tool-approval-deny")
        Avalonia.Automation.AutomationProperties.SetName(approveButton, sprintf "批准工具调用：%s" req.summary)
        Avalonia.Automation.AutomationProperties.SetName(denyButton, sprintf "拒绝工具调用：%s" req.summary)
        let buttons = Ui.hstack Spacing.spaceMd [ approveButton :> Control; denyButton :> Control ]
        // 参数默认折叠在一行里：想核对原始参数的人展开，其余人看摘要即可。
        let hasArgs = not (String.IsNullOrWhiteSpace req.argumentsJson) && req.argumentsJson <> "{}"
        let argsLine =
            TextBlock(
                Text = (if hasArgs then req.argumentsJson else ""),
                FontFamily = Tokens.monoFontFamily,
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textFaint,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsVisible = hasArgs)
        ToolTip.SetTip(argsLine, req.argumentsJson)
        let column =
            Ui.vstack
                Spacing.spaceMd
                [ title :> Control
                  summary :> Control
                  argsLine :> Control
                  note :> Control
                  buttons :> Control
                  status :> Control ]
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Spacing.spaceXl)
        row.Children.Add icon
        row.Children.Add column
        let card = Ui.groupingCard (Thickness(Spacing.space2xl, Spacing.spaceXl)) row Spacing.Radius.md
        Avalonia.Automation.AutomationProperties.SetAutomationId(card, sprintf "tool-approval-%O" req.approvalId)
        Avalonia.Automation.AutomationProperties.SetName(card, sprintf "工具审批：%s" req.summary)
        card :> Control

/// 对话内的审批卡片宿主：浮在输入框上方，只显示当前对话的待审批项。
/// 内容完全由主机事件驱动，不持久化；断线重连后主机重发，卡片自然回来。
type ToolApprovalPanel(decide: Guid -> bool -> unit) as this =
    inherit StackPanel()

    let pending = Collections.Generic.List<ApprovalRequestView>()
    let mutable conversation: Guid option = None

    let refresh () =
        this.Children.Clear()
        for req in pending do
            if conversation = Some req.conversationId then
                this.Children.Add(ToolApprovalCard.build req (fun approved -> decide req.approvalId approved))
        this.IsVisible <- this.Children.Count > 0

    do
        this.Orientation <- Orientation.Vertical
        this.Spacing <- Spacing.spaceMd
        this.VerticalAlignment <- VerticalAlignment.Bottom
        this.HorizontalAlignment <- HorizontalAlignment.Center
        this.MaxWidth <- 720.0
        this.Margin <- Thickness(Spacing.space2xl, 0.0, Spacing.space2xl, Spacing.spaceXl)
        this.IsVisible <- false

    member _.Pending : ApprovalRequestView list = List.ofSeq pending

    /// 新增（或按 approvalId 覆盖）一条待审批。
    member _.Upsert(req: ApprovalRequestView) =
        let i = pending.FindIndex(fun r -> r.approvalId = req.approvalId)
        if i >= 0 then pending[i] <- req else pending.Add req
        refresh ()

    /// 主机已处理（任何结果）：卡片消失。
    member _.Remove(approvalId: Guid) =
        if pending.RemoveAll(fun r -> r.approvalId = approvalId) > 0 then refresh ()

    /// 断线：主机会在重连并重新观察后重发，先清空避免显示失效卡片。
    member _.Clear() =
        pending.Clear()
        refresh ()

    /// 切换当前对话（只显示该对话的卡片）。
    member _.SetConversation(convId: Guid option) =
        if conversation <> convId then
            conversation <- convId
            refresh ()
