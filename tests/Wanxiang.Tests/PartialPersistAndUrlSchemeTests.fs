module Wanxiang.Tests.PartialPersistAndUrlSchemeTests

// 决策 250 半截回复由主机保存 / 251 桌面 wanxiang:// 协议与单实例转发。

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Server
open Wanxiang.UI

let private partialMessage (text: string) : JsonNode =
    JsonNode.Parse(sprintf """{"role":"assistant","contents":[{"$type":"text","text":%s}]}""" (System.Text.Json.JsonSerializer.Serialize text))

let private tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "wx-r8-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    d

[<Fact>]
let ``250 generation.partial 编解码往返`` () =
    let conv, gen = Guid.NewGuid(), Guid.NewGuid()
    let err = GenerationError.create ProviderTimeout "超时"
    let ev = GenerationPartial {| conversationId = conv; generationId = gen; message = partialMessage "写到一半"; error = Some err |}
    match WireCodec.tryDecode (WireCodec.encode ev) with
    | Ok(GenerationPartial d) ->
        Assert.Equal(conv, d.conversationId)
        Assert.Equal(gen, d.generationId)
        Assert.Equal("写到一半", (MessageView.ofJson d.message).text)
        Assert.Equal(Some ProviderTimeout, d.error |> Option.map _.kind)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``250 只有助手正文才算可续写的半截`` () =
    Assert.True(ChatOrchestrator.PartialText(partialMessage "有字"))
    Assert.False(ChatOrchestrator.PartialText(partialMessage "   "))
    Assert.False(ChatOrchestrator.PartialText(JsonNode.Parse """{"role":"user","contents":[{"text":"x"}]}"""))

[<Fact>]
let ``250 刷新后客户端从主机恢复半截与错误卡；正在生成时不覆盖`` () =
    let runs = ConversationRuns()
    let conv, gen = Guid.NewGuid(), Guid.NewGuid()
    let err = GenerationError.create ProviderTimeout "超时"
    let msg = { MessageView.empty with role = "assistant"; text = "半截" }
    Assert.True(runs.RestorePartial(conv, gen, msg, Some err))
    let run = runs.Get(Some conv)
    Assert.Equal(Some "半截", run.partial |> Option.map _.text)
    Assert.True(run.error.IsSome)
    // 迟到的同代 started 不应复活这次生成
    runs.Start(conv, gen)
    Assert.False((runs.Get(Some conv)).running)
    let conv2, g2 = Guid.NewGuid(), Guid.NewGuid()
    runs.Start(conv2, g2)
    Assert.False(runs.RestorePartial(conv2, Guid.NewGuid(), msg, Some err))

[<Fact>]
let ``251 Linux 桌面文件声明 x-scheme-handler 并以 %u 接收链接`` () =
    let entry = UrlSchemeRegistration.desktopEntry "/opt/wanxiang/wanxiang"
    Assert.Contains("MimeType=x-scheme-handler/wanxiang;", entry)
    Assert.Contains("Exec=/opt/wanxiang/wanxiang %u", entry)
    Assert.Contains("NoDisplay=true", entry)
    Assert.Equal("\"/a b/w\"", UrlSchemeRegistration.quote "/a b/w")

[<Fact>]
let ``251 第二实例把链接转发给主实例，主实例不在时转发失败`` () =
    let dir = tempDir ()
    let path = SingleInstance.socketPath dir
    Assert.False(SingleInstance.tryForward path (Some "wanxiang://open?c=x&m=1"))
    let got = System.Collections.Concurrent.BlockingCollection<string>()
    use _l = SingleInstance.listen path (fun url -> got.Add url) (fun () -> got.Add "<activate>")
    let link = sprintf "wanxiang://open?c=%O&m=7" (Guid.NewGuid())
    Assert.True(SingleInstance.tryForward path (Some link))
    let mutable received = ""
    Assert.True(got.TryTake(&received, 5000))
    Assert.Equal(link, received)
    Assert.True(SingleInstance.tryForward path None)
    Assert.True(got.TryTake(&received, 5000))
    Assert.Equal("<activate>", received)

[<Fact>]
let ``251 外部链接投给当前订阅者`` () =
    let link = "wanxiang://open?c=queued&m=1"
    let seen = ResizeArray<string>()
    ExternalLinks.subscribe seen.Add
    ExternalLinks.post link
    Assert.Contains(link, seen)

// ---------- 视觉走查 r8：右键正文要弹消息菜单，而不是主题自带的英文 Copy ----------

[<Trait("Category", "UI")>]
[<Fact>]
let ``r8 右键消息正文弹消息菜单（含复制消息链接）`` () =
    Wanxiang.Tests.Headless.run (fun () ->
        let message =
            { MessageView.empty with role = "assistant"; text = "一段可以右键的正文。"; commitId = Some 42UL }
        let ctx =
            { fontSize = Tokens.fontReading
              autoCollapseReasoning = true
              streaming = false
              isLastAssistant = true
              usage = None
              missingAttachments = Set.empty
              awaitingApproval = Set.empty
              brandAvatar = fun () -> Avalonia.Controls.Border() }
        let actions =
            { copyText = ignore; regenerate = ignore; editAndFork = ignore
              deleteMessage = ignore; downloadAttachment = ignore; openLink = ignore }
        MessageCard.setLinkSink ignore
        let card = MessageCard.render message ctx actions (Some 0)
        let window = Avalonia.Controls.Window(Width = 420.0, Height = 360.0, Content = card)
        window.Show()
        try
            Avalonia.Threading.Dispatcher.UIThread.RunJobs()
            let text =
                Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(card)
                |> Seq.pick (function :? Avalonia.Controls.SelectableTextBlock as t -> Some t | _ -> None)
            let args = Avalonia.Input.ContextRequestedEventArgs()
            args.RoutedEvent <- Avalonia.Controls.Control.ContextRequestedEvent
            text.RaiseEvent args
            Assert.Null(text.ContextFlyout)
            Assert.True(card.ContextMenu.IsOpen, "应弹出消息菜单")
            let headers =
                card.ContextMenu.ItemsSource |> Seq.cast<obj>
                |> Seq.choose (function :? Avalonia.Controls.MenuItem as i -> Some(string i.Header) | _ -> None)
                |> List.ofSeq
            Assert.Contains("复制消息链接", headers)
            card.ContextMenu.Close()
        finally
            window.Close())
