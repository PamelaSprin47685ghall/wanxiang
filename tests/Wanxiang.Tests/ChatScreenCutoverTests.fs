module Wanxiang.Tests.ChatScreenCutoverTests

open System
open System.Reflection
open System.Text.Json.Nodes
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.UI
open Wanxiang.Protocol
open Wanxiang2.ChatScreen
open Wanxiang2.ApprovalDesk

let private flags = BindingFlags.Instance ||| BindingFlags.NonPublic

// 不启动连接或读取用户凭据；构造顺序与 MainView.Build 一致，协作者均为真实对象。
let private parts () =
    let view = MainView()
    for name in [ "BuildSidebar"; "BuildComposer"; "BuildChat" ] do
        typeof<MainView>.GetMethod(name, flags).Invoke(view, [||]) |> ignore
    let field name = typeof<MainView>.GetField(name, flags).GetValue view
    field "chat" :?> ChatScreen, field "composer" :?> Composer, field "approvalPanel" :?> ApprovalDesk

[<Fact>]
let ``application assembly has no legacy chat view or rendering key`` () =
    let assembly = typeof<MainView>.Assembly
    Assert.Null(assembly.GetType "Wanxiang.UI.ChatView")
    Assert.Null(assembly.GetType "Wanxiang.UI.CardKey")
    Assert.Equal(typeof<ChatScreen>, typeof<MainView>.GetField("chat", flags).FieldType)

[<Trait("Category", "UI")>]
[<Fact>]
let ``approval changes redraw the existing screen frame through the actual composition`` () =
    Headless.run (fun () ->
        let screen, composer, approvals = parts ()
        let conversation = Guid.NewGuid()
        let approval = Guid.NewGuid()
        let root = DockPanel()
        DockPanel.SetDock(composer, Dock.Bottom)
        root.Children.Add composer
        root.Children.Add screen.Host
        let window = Window(Width = 900.0, Height = 700.0, Content = root)
        window.Show()
        try
            approvals.SetConversation(Some conversation)
            screen.SetGenerating(true, "生成中")
            screen.Render
                { Messages =
                    [ { MessageView.empty with
                          role = "assistant"
                          commitId = Some 1UL
                          toolCalls = [ { callId = "call-1"; name = "shell"; argumentsJson = "{}"; result = None } ] } ]
                  Streaming = None
                  Error = None
                  FontSize = Tokens.fontReading
                  AutoCollapseReasoning = true
                  Usage = None
                  MissingAttachments = Set.empty }
            let texts () =
                Dispatcher.UIThread.RunJobs()
                screen.Pane.Messages.GetVisualDescendants()
                |> Seq.choose (function :? TextBlock as t -> Some t.Text | _ -> None)
                |> Seq.filter (isNull >> not)
                |> Seq.toList
            Assert.DoesNotContain(texts (), fun text -> text.Contains "等待批准")
            Assert.True(approvals.Handle(
                ToolApprovalRequested
                    {| approvalId = approval; conversationId = conversation; generationId = Guid.NewGuid(); toolName = "shell"
                       callId = "call-1"; summary = "执行命令"; arguments = JsonObject() :> JsonNode
                       policy = "ask"; expiresInSeconds = 300 |}))
            // 不调用 MainView.Render 或 Screen.Render，必须由实际 AwaitingChanged 接线更新。
            Assert.Contains(texts (), fun text -> text.Contains "等待批准")
            Assert.Contains(composer.GetVisualDescendants(), fun child -> obj.ReferenceEquals(child, approvals.View))
            approvals.Disconnected()
            Assert.DoesNotContain(texts (), fun text -> text.Contains "等待批准")
        finally
            window.Close())
