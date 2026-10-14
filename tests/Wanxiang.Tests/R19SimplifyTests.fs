namespace Wanxiang.Tests

// r19 减法：决策 360–368。

open System
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.UI

module R19SimplifyTests =

    let private summary (title: string) (minutesAgo: float) : ConversationSummary =
        { id = Guid.NewGuid()
          title = title
          preview = ""
          running = false
          pinned = false
          archived = false
          createdAt = DateTimeOffset.Now.AddMinutes(-minutesAgo)
          updatedAt = DateTimeOffset.Now.AddMinutes(-minutesAgo)
          messageCount = 0
          isFork = false
          providerId = ""
          model = ""
          lastCommitId = 0UL }

    [<Fact>]
    let ``367 主机生成设置改名为对话默认值`` () =
        Assert.Equal("对话默认值", SettingsSection.label SettingsSection.Generation)
        Assert.NotEqual<string>(SettingsSection.label SettingsSection.Generation, SettingsSection.label SettingsSection.Appearance)

    let private ctx (isLastAssistant: bool) : MessageContext =
        { fontSize = Tokens.fontReading
          autoCollapseReasoning = true
          streaming = false
          isLastAssistant = isLastAssistant
          usage = None
          missingAttachments = Set.empty
          awaitingApproval = Set.empty; brandAvatar = fun () -> Border() :> Control }

    let private actions : MessageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }

    let private actionRow (control: Control) =
        control.GetVisualDescendants()
        |> Seq.choose (function
            | :? StackPanel as p when p.Orientation = Orientation.Horizontal && p.MinHeight = LayoutPolicy.inlineActionTarget -> Some p
            | _ -> None)
        |> Seq.head

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``360 消息操作闲置隐去但仍可聚焦，最新回复保留淡显`` () =
        Headless.run (fun () ->
            let user = { MessageView.empty with role = "user"; text = "你好"; commitId = Some 1UL }
            let older = { MessageView.empty with role = "assistant"; text = "旧回复"; commitId = Some 2UL }
            let latest = { MessageView.empty with role = "assistant"; text = "新回复"; commitId = Some 3UL }
            let host = StackPanel()
            let cards =
                [ MessageCard.render user (ctx false) actions (Some 0)
                  MessageCard.render older (ctx false) actions (Some 1)
                  MessageCard.render latest (ctx true) actions (Some 2) ]
            for c in cards do host.Children.Add c
            let window = Window(Content = host, Width = 800.0, Height = 600.0)
            window.Show()
            Avalonia.Threading.Dispatcher.UIThread.RunJobs()
            let rows = cards |> List.map actionRow
            Assert.Equal(0.0, rows.[0].Opacity, 3)
            Assert.Equal(0.0, rows.[1].Opacity, 3)
            Assert.Equal(Tokens.opacitySubtle, rows.[2].Opacity, 3)
            // 隐去不等于移除：按钮仍在、可聚焦、可命中（键盘与读屏照常抵达）。
            for r in rows do
                Assert.True(r.IsVisible)
                Assert.True(r.IsHitTestVisible)
                Assert.True(r.Children |> Seq.exists (fun b -> b.Focusable))
            window.Close())
