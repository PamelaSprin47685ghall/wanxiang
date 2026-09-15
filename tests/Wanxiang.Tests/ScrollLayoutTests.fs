module Wanxiang.Tests.ScrollLayoutTests

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.UI
open Wanxiang.Tests

/// 量一遍：把留白放在 Padding 上 / 放在内容 Margin 上，可滚动范围各是多少。
let private extentWith (padding: Thickness) (childMargin: Thickness) =
    Headless.ensure ()
    let child = Border(Height = 2000.0, Background = Brushes.Red, Margin = childMargin)
    let scroller = ScrollViewer(Content = child, Padding = padding)
    let window = Window(Width = 400.0, Height = 500.0, Content = scroller)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    scroller.ScrollToEnd()
    Dispatcher.UIThread.RunJobs()
    let extent = scroller.Extent.Height
    window.Close()
    extent

[<Fact>]
let ``ScrollViewer 的下内边距计入可滚动范围`` () =
    // Avalonia 12.0 时代曾不计入（导致末条留白必须加在内容 Margin 上）；
    // 升级至 Avalonia 12.1+ 后已修复，下内边距会计入可滚动范围。
    let bare = extentWith (Thickness 0.0) (Thickness 0.0)
    let padded = extentWith (Thickness(0.0, 0.0, 0.0, 200.0)) (Thickness 0.0)
    Assert.Equal(bare + 200.0, padded)

[<Fact>]
let ``内容自身的下边距会计入可滚动范围`` () =
    let bare = extentWith (Thickness 0.0) (Thickness 0.0)
    let margined = extentWith (Thickness 0.0) (Thickness(0.0, 0.0, 0.0, 200.0))
    Assert.Equal(bare + 200.0, margined)

[<Fact>]
let ``嵌套 ScrollViewer 与草稿/待发送滚动视口具备右侧安全留白与视觉分层`` () =
    Headless.ensure ()
    // 1. MessageCard detailViewport 拥有 LayoutPolicy.nestedScrollGutter 右边距
    let ctx: MessageContext =
        { fontSize = Tokens.fontReading
          autoCollapseReasoning = false
          streaming = false
          isLastAssistant = true
          usage = None
          missingAttachments = Set.empty
          brandAvatar = fun () -> Border() :> Control }
    let msgActions: MessageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }
    let msgWithReasoning =
        { MessageView.empty with
            role = "assistant"
            reasoning = "思考过程长文\n" + String.replicate 20 "深度思考中..."
            text = "正式回复"
            commitId = Some 1UL }
    let card = MessageCard.render msgWithReasoning ctx msgActions (Some 0)
    
    let rec findControls (c: Control) =
        seq {
            yield c
            match c with
            | :? Panel as p -> for child in p.Children do yield! findControls child
            | :? Decorator as d when not (isNull d.Child) -> yield! findControls d.Child
            | :? ContentControl as cc when not (isNull cc.Content) && (cc.Content :? Control) ->
                yield! findControls (cc.Content :?> Control)
            | _ -> ()
        }

    let detailScroller =
        findControls card
        |> Seq.pick (function :? ScrollViewer as s when s.MaxHeight = LayoutPolicy.expandedDetailMaxHeight -> Some s | _ -> None)
    Assert.Equal(LayoutPolicy.nestedScrollGutter, detailScroller.Margin.Right)

    // 2. Composer attachmentScroller 与 pendingScroller 拥有嵌套 gutter 与分层样式
    let composerActions: ComposerActions =
        { submit = fun _ -> true
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = ignore
          pasteFromClipboard = fun () -> false }
    let composer = Composer(composerActions)
    composer.Build()

    let attachmentScroller =
        findControls composer
        |> Seq.pick (function :? ScrollViewer as s when s.MaxHeight = LayoutPolicy.attachmentDraftMaxHeight -> Some s | _ -> None)
    Assert.Equal(LayoutPolicy.nestedScrollGutter, attachmentScroller.Margin.Right)

    let pendingScroller =
        findControls composer
        |> Seq.pick (function :? ScrollViewer as s when s.MaxHeight = LayoutPolicy.pendingMessagesMaxHeight -> Some s | _ -> None)
    Assert.Equal(LayoutPolicy.nestedScrollGutter, pendingScroller.Margin.Right)
