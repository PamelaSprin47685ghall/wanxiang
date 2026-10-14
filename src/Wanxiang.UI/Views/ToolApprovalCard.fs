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
    /// 对应的工具调用（消息里的工具卡据此显示「等待批准」）
    callId: string
    /// 人话摘要（主机用 ToolSummary 生成，两端同一份文案）
    summary: string
    argumentsJson: string
    /// "first"（首次询问）或 "always"（每次询问）
    policy: string
}

module ToolApprovalCard =

    /// 审批按钮与「查看参数」的命中高度下限：触控下限（PWA / 手机）。
    let touchTarget = Spacing.Hit.touch

    /// 按钮行最大宽度：宽屏上不把两个按钮拉成横贯整卡的长条。
    let buttonRowMaxWidth = 360.0

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
                Text = ApprovalCardText.policyNote req.policy,
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
                status.Text <- ApprovalCardText.sentText approved
                status.IsVisible <- true
                decide approved
        approveButton <- Ui.button Ui.Primary "批准" (choose true)
        denyButton <- Ui.button Ui.Secondary "拒绝" (choose false)
        Avalonia.Automation.AutomationProperties.SetAutomationId(approveButton, "tool-approval-approve")
        Avalonia.Automation.AutomationProperties.SetAutomationId(denyButton, "tool-approval-deny")
        Avalonia.Automation.AutomationProperties.SetName(approveButton, sprintf "批准工具调用：%s" req.summary)
        Avalonia.Automation.AutomationProperties.SetName(denyButton, sprintf "拒绝工具调用：%s" req.summary)
        // 触控优先（PWA / 手机与桌面同一张卡）：两个按钮等宽平分一行，命中高度不低于触控下限 44，
        // 窄屏上也是拇指一按就中的大块目标；桌面上只是略高一点，不另做分支。
        for b in [ approveButton; denyButton ] do
            b.MinHeight <- max b.MinHeight touchTarget
            b.HorizontalAlignment <- HorizontalAlignment.Stretch
        let buttons = Grid(ColumnSpacing = Spacing.spaceMd, HorizontalAlignment = HorizontalAlignment.Stretch)
        // 两个等宽星号列，各自封顶，宽屏上靠左而不是拉成横贯整卡的长条。
        for _ in 1..2 do
            buttons.ColumnDefinitions.Add(ColumnDefinition(GridLength.Star, MaxWidth = buttonRowMaxWidth / 2.0))
        Grid.SetColumn(approveButton, 0)
        Grid.SetColumn(denyButton, 1)
        buttons.Children.Add approveButton
        buttons.Children.Add denyButton
        // 参数默认折叠在一行里：想核对原始参数的人展开，其余人看摘要即可。
        let hasArgs = not (String.IsNullOrWhiteSpace req.argumentsJson) && req.argumentsJson <> "{}"
        // 原始参数只出现一处（第 208 节）：默认收起，摘要已说明要做什么；
        // 想核对的人点「查看参数」展开全文（触屏没有悬停提示，桌面同样可用）。此前一行预览 + 按钮是同一内容两遍。
        let argsLine =
            TextBlock(
                Text = (if hasArgs then ApprovalCardText.displayArguments req.argumentsJson else ""),
                FontFamily = Tokens.monoFontFamily,
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textFaint,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = false)
        Avalonia.Automation.AutomationProperties.SetAutomationId(argsLine, "tool-approval-args")
        let mutable argsExpanded = false
        let mutable argsToggle: Border = null
        let toggleArgs () =
            argsExpanded <- not argsExpanded
            argsLine.IsVisible <- argsExpanded
            Ui.setButtonText argsToggle (ApprovalCardText.argsToggleText argsExpanded)
        argsToggle <- Ui.button Ui.Ghost (ApprovalCardText.argsToggleText false) toggleArgs
        argsToggle.IsVisible <- hasArgs
        argsToggle.HorizontalAlignment <- HorizontalAlignment.Left
        argsToggle.MinHeight <- max argsToggle.MinHeight touchTarget
        Avalonia.Automation.AutomationProperties.SetAutomationId(argsToggle, "tool-approval-args-toggle")
        let column =
            Ui.vstack
                Spacing.spaceMd
                [ title :> Control
                  summary :> Control
                  argsLine :> Control
                  argsToggle :> Control
                  note :> Control
                  buttons :> Control
                  status :> Control ]
        // 图标 + 正文用 Auto/Star 网格而不是横向 StackPanel：横向 StackPanel 给子项无限宽，
        // 窄屏（手机 PWA）上摘要不会换行而是被裁掉。
        let row = Grid(ColumnSpacing = Spacing.spaceXl)
        row.ColumnDefinitions.Add(ColumnDefinition(GridLength.Auto))
        row.ColumnDefinitions.Add(ColumnDefinition(GridLength.Star))
        Grid.SetColumn(icon, 0)
        Grid.SetColumn(column, 1)
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
        // 宽度与边距跟随宿主（输入框所在列），自己不再另定 MaxWidth / 水平边距。
        this.HorizontalAlignment <- HorizontalAlignment.Stretch
        this.IsVisible <- false

    member _.Pending : ApprovalRequestView list = List.ofSeq pending

    /// 所有待审批项对应的工具调用 callId。
    member _.AwaitingCallIds : Set<string> = pending |> Seq.map (fun r -> r.callId) |> Set.ofSeq

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
