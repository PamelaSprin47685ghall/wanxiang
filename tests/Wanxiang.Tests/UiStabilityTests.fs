module Wanxiang.Tests.UiStabilityTests

open System
open System.Diagnostics
open System.Threading
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Automation.Provider
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.UI
open Wanxiang.Tests

let rec private descendants (control: Control) =
    seq {
        yield control
        match control with
        | :? Panel as panel ->
            for child in panel.Children do
                yield! descendants child
        | :? Decorator as decorator when not (isNull decorator.Child) ->
            yield! descendants decorator.Child
        | :? ContentControl as host ->
            match host.Content with
            | :? Control as child -> yield! descendants child
            | _ -> ()
        | _ -> ()
    }

let private byAutomationName (root: Control) (name: string) =
    descendants root
    |> Seq.find (fun control -> AutomationProperties.GetName(control) = name)

let rec private visualControls (visual: Visual) =
    seq {
        match visual with
        | :? Control as control -> yield control
        | _ -> ()
        for child in visual.GetVisualChildren() do
            yield! visualControls child
    }

let private realizedByAutomationName (list: ListBox) (name: string) =
    list.GetRealizedContainers()
    |> Seq.collect visualControls
    |> Seq.find (fun control -> AutomationProperties.GetName(control) = name)

let private show (content: Control) width height =
    Headless.ensure ()
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

[<Fact>]
let ``craft metrics form a stable readable hierarchy`` () =
    let size = Tokens.fontReading
    Assert.True(ReadingRhythm.proseLineHeight size > ReadingRhythm.secondaryLineHeight size)
    Assert.True(ReadingRhythm.secondaryLineHeight size > ReadingRhythm.technicalLineHeight size)
    Assert.True(ReadingRhythm.headingBefore 1 > ReadingRhythm.headingBefore 2)
    Assert.True(ReadingRhythm.headingBefore 2 > ReadingRhythm.headingBefore 3)
    Assert.True(ControlMetrics.textButtonMinHeight >= Tokens.iconButton)
    Assert.True(ControlMetrics.sidebarRowMinHeight > ControlMetrics.textButtonMinHeight)

[<Fact>]
let ``markdown and primitive controls consume shared craft metrics`` () =
    Headless.ensure ()
    let renderer = MarkdownRenderer(Tokens.fontReading, ignore, ignore, false)
    let rendered = renderer.RenderText "一段用于验证阅读节奏的正文。"
    let button = Ui.button Ui.Secondary "保存" ignore
    let fieldShell, field = Ui.textField "输入内容"
    let root = StackPanel()
    root.Children.Add rendered
    root.Children.Add button
    root.Children.Add fieldShell
    let window = show root 620.0 260.0
    try
        let prose =
            descendants rendered
            |> Seq.choose (function :? SelectableTextBlock as text -> Some text | _ -> None)
            |> Seq.head
        Assert.Equal(ReadingRhythm.proseLineHeight Tokens.fontReading, prose.LineHeight, 3)
        Assert.Equal(ControlMetrics.textButtonMinHeight, button.MinHeight, 3)
        Assert.Equal(ControlMetrics.textFieldTextMinHeight, field.MinHeight, 3)
        Assert.Equal(ControlMetrics.textFieldPaddingY, fieldShell.Padding.Top, 3)
    finally
        window.Close()

[<Fact>]
let ``reserved contextual actions never change their layout slot`` () =
    Headless.ensure ()
    let action = Ui.iconButton Icons.more "更多"
    let window = show action 90.0 70.0
    try
        Dispatcher.UIThread.RunJobs()
        let width = action.Bounds.Width
        let height = action.Bounds.Height
        Ui.setReservedActionVisible action false
        Dispatcher.UIThread.RunJobs()
        Assert.True action.IsVisible
        Assert.Equal(width, action.Bounds.Width, 3)
        Assert.Equal(height, action.Bounds.Height, 3)
        Assert.Equal(0.0, action.Opacity, 3)
        Assert.False action.IsHitTestVisible
        Assert.False action.Focusable

        Ui.setReservedActionVisible action true
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(width, action.Bounds.Width, 3)
        Assert.Equal(height, action.Bounds.Height, 3)
        Assert.Equal(1.0, action.Opacity, 3)
        Assert.True action.IsHitTestVisible
        Assert.True action.Focusable
    finally
        window.Close()

[<Fact>]
let ``standard input field group keeps label validation and hint in one rhythm`` () =
    Headless.ensure ()
    let shell, box = Ui.textField "输入"
    let group = Ui.inputFieldGroup "字段" "辅助说明" box
    let window = show group 420.0 150.0
    try
        let panel = group :?> StackPanel
        Assert.Equal(4, panel.Children.Count)
        Assert.IsType<TextBlock>(panel.Children[0]) |> ignore
        Assert.True(obj.ReferenceEquals(shell, panel.Children[1]))
        Assert.True(obj.ReferenceEquals(Ui.fieldValidationMessage box, panel.Children[2]))
        let hint = Assert.IsType<TextBlock>(panel.Children[3])
        Assert.Equal(ReadingRhythm.captionLineHeight, hint.LineHeight, 3)
        Assert.Equal(Tokens.space1, hint.Margin.Top, 3)
    finally
        window.Close()

[<Fact>]
let ``chat reading column relies on avalonia stretch and max width across viewports`` () =
    Headless.ensure ()
    let messageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }
    let actions =
        { renameTitle = ignore
          openSessionSettings = ignore
          forkFromHere = ignore
          stopGeneration = ignore
          requestOlderHistory = ignore
          retryLast = ignore
          toggleSidebar = ignore
          message = messageActions }
    for width in [ 1000.0; 500.0 ] do
        let chat = ChatView(actions, fun _ -> Border(Width = 26.0, Height = 26.0) :> Control)
        chat.Build()
        let longMessage =
            { MessageView.empty with
                role = "assistant"
                text = String.replicate 40 "这是一段用来撑满阅读列的长正文。"
                commitId = Some 1UL }
        chat.RenderMessages(
            [ longMessage ],
            None,
            None,
            Tokens.fontReading,
            true,
            None,
            Set.empty)
        let window = show chat width 520.0
        try
            Dispatcher.UIThread.RunJobs()
            let scroller =
                descendants chat
                |> Seq.choose (function :? ScrollViewer as value -> Some value | _ -> None)
                |> Seq.head
            let readingColumn = scroller.Content :?> Control
            Assert.True(readingColumn.Bounds.Width <= Tokens.readingWidth + 0.5)
            if width > Tokens.readingWidth + Tokens.shellInset * 2.0 then
                Assert.True(abs (readingColumn.Bounds.Width - Tokens.readingWidth) < 1.0)
            else
                Assert.True(readingColumn.Bounds.Width > width - Tokens.shellInset * 4.0)
        finally
            window.Close()

[<Fact>]
let ``navigation controller owns compact and collapsed state transitions`` () =
    let navigation = NavigationController(true)
    let applyWide, wide = navigation.ApplyViewport(1200.0, false)
    Assert.True applyWide
    Assert.False wide.compactMode
    Assert.True wide.sidebarCollapsed

    let opened = navigation.EnsureSearchVisible()
    Assert.False opened.sidebarCollapsed

    let applyCompact, compact = navigation.ApplyViewport(600.0, false)
    Assert.True applyCompact
    Assert.True compact.compactMode
    Assert.True compact.compactNavigationOpen

    let toggled = navigation.ToggleSidebar()
    Assert.False toggled.compactNavigationOpen
    let closed = navigation.SetCompactNavigation(false)
    Assert.False closed.compactNavigationOpen

[<Fact>]
let ``shortcut router maps global keys without executing UI effects`` () =
    let event key modifiers = KeyEventArgs(Key = key, KeyModifiers = modifiers)
    Assert.Equal(ToggleSidebar, ShortcutRouter.resolve (event Key.B KeyModifiers.Control))
    Assert.Equal(NewConversation, ShortcutRouter.resolve (event Key.N KeyModifiers.Meta))
    Assert.Equal(ToggleTheme, ShortcutRouter.resolve (event Key.S (KeyModifiers.Control ||| KeyModifiers.Shift)))
    Assert.Equal(OpenConversationAt 2, ShortcutRouter.resolve (event Key.D3 KeyModifiers.Control))
    Assert.Equal(ShowShortcuts, ShortcutRouter.resolve (event Key.F1 KeyModifiers.None))
    Assert.Equal(NoShortcut, ShortcutRouter.resolve (event Key.A KeyModifiers.None))

[<Fact>]
let ``disabled action surface cannot be invoked through automation`` () =
    Headless.ensure ()
    let mutable invoked = 0
    let button = Ui.iconButton Icons.paperclip "添加附件"
    Ui.onClick button (fun () -> invoked <- invoked + 1)
    let window = show button 160.0 80.0
    try
        let peer = ControlAutomationPeer.CreatePeerForElement button
        let invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer)

        Ui.setEnabled button false
        Assert.False button.IsEnabled
        Assert.False button.Focusable
        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(0, invoked)

        Ui.setEnabled button true
        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(1, invoked)
    finally
        window.Close()

[<Fact>]
let ``custom toggle exposes toggle automation pattern and state`` () =
    Headless.ensure ()
    let mutable changed = false
    let toggle, read, _, _ = Ui.toggle false (fun value -> changed <- value)
    let window = show toggle 100.0 70.0
    try
        let peer = ControlAutomationPeer.CreatePeerForElement toggle
        let provider = Assert.IsAssignableFrom<IToggleProvider>(peer)
        Assert.Equal(ToggleState.Off, provider.ToggleState)
        provider.Toggle()
        Dispatcher.UIThread.RunJobs()
        Assert.True(read ())
        Assert.True changed
        Assert.Equal(ToggleState.On, provider.ToggleState)
    finally
        window.Close()

[<Fact>]
let ``field validation is local and does not change geometry`` () =
    Headless.ensure ()
    let field, box = Ui.labeledField "端点地址" "https://example.com"
    let window = show field 420.0 130.0
    try
        let shell = box.Parent :?> Border
        let before = shell.BorderThickness
        Ui.setFieldError box "端点地址不能为空。"
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(before, shell.BorderThickness)
        Assert.Equal(Tokens.danger.Color, (shell.BorderBrush :?> SolidColorBrush).Color)
        let message = Ui.fieldValidationMessage box
        Assert.True message.IsVisible
        Assert.Equal("端点地址不能为空。", message.Text)
        box.Text <- "https://example.com"
        Dispatcher.UIThread.RunJobs()
        Assert.False message.IsVisible
        Assert.Equal("", AutomationProperties.GetHelpText(box))
        Ui.setFieldError box "再次验证错误"
        Ui.clearFieldError box
        Dispatcher.UIThread.RunJobs()
        Assert.False message.IsVisible
        Assert.Equal(before, shell.BorderThickness)
    finally
        window.Close()

[<Fact>]
let ``field group shows hint or error but never both`` () =
    Headless.ensure ()
    let _, box = Ui.textField "输入"
    let group = Ui.inputFieldGroup "端点" "填写服务地址。" box
    let window = show group 420.0 150.0
    try
        let panel = group :?> StackPanel
        let error = Ui.fieldValidationMessage box
        let hint = panel.Children[3] :?> TextBlock
        Assert.True hint.IsVisible
        Assert.False error.IsVisible
        Ui.setFieldError box "地址无效。"
        Dispatcher.UIThread.RunJobs()
        Assert.True error.IsVisible
        Assert.False hint.IsVisible
        box.Text <- "https://example.com"
        Dispatcher.UIThread.RunJobs()
        Assert.False error.IsVisible
        Assert.True hint.IsVisible
    finally
        window.Close()

[<Fact>]
let ``composer blocks send while any attachment is uploading`` () =
    Headless.ensure ()
    let mutable submitted = 0
    let actions =
        { submit = fun _ -> submitted <- submitted + 1; true
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = ignore
          pasteFromClipboard = fun () -> false }
    let composer = Composer(actions)
    composer.Build()
    let window = show composer 720.0 260.0
    try
        composer.SetEnabled(true, "")
        composer.SetText "hello"
        composer.SetAttachments
            [ { attachmentId = Guid.NewGuid()
                sha256 = "pending"
                size = 1024L
                mediaType = "text/plain"
                fileName = "pending.txt"
                ready = false } ]
        Dispatcher.UIThread.RunJobs()

        let send = byAutomationName composer "发送" :?> Border
        Assert.False send.IsEnabled
        let provider =
            ControlAutomationPeer.CreatePeerForElement send
            |> Assert.IsAssignableFrom<IInvokeProvider>
        provider.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(0, submitted)

        composer.SetAttachments
            [ { attachmentId = Guid.NewGuid()
                sha256 = "ready"
                size = 1024L
                mediaType = "text/plain"
                fileName = "ready.txt"
                ready = true } ]
        Assert.True send.IsEnabled
        provider.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(1, submitted)
    finally
        window.Close()

[<Fact>]
let ``composer send stop state keeps automation name in sync`` () =
    Headless.ensure ()
    let actions =
        { submit = fun _ -> true
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = ignore
          pasteFromClipboard = fun () -> false }
    let composer = Composer(actions)
    composer.Build()
    let window = show composer 620.0 220.0
    try
        composer.SetEnabled(true, "")
        composer.SetText "hello"
        let send = byAutomationName composer "发送"
        composer.SetGenerating true
        Dispatcher.UIThread.RunJobs()
        Assert.Equal("停止生成", AutomationProperties.GetName(send))
        composer.SetGenerating false
        Dispatcher.UIThread.RunJobs()
        Assert.Equal("发送", AutomationProperties.GetName(send))
    finally
        window.Close()

[<Fact>]
let ``composer attachment slots stay stable across uploading ready and thirty items`` () =
    Headless.ensure ()
    let actions =
        { submit = fun _ -> true
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = ignore
          pasteFromClipboard = fun () -> false }
    let composer = Composer(actions)
    composer.Build()
    composer.SetEnabled(true, "")
    let id = Guid.NewGuid()
    let pending =
        { attachmentId = id
          sha256 = "stable"
          size = 1024L
          mediaType = "text/plain"
          fileName = "a-very-long-attachment-name-that-needs-ellipsis.txt"
          ready = false }
    let window = show composer 720.0 360.0
    try
        composer.SetAttachments [ pending ]
        Dispatcher.UIThread.RunJobs()
        let removePending = byAutomationName composer "移除"
        let pendingPoint = removePending.TranslatePoint(Point(0.0, 0.0), composer).Value
        let heightPending = composer.Bounds.Height

        composer.SetAttachments [ { pending with ready = true; size = 15L * 1024L * 1024L } ]
        Dispatcher.UIThread.RunJobs()
        let removeReady = byAutomationName composer "移除"
        let readyPoint = removeReady.TranslatePoint(Point(0.0, 0.0), composer).Value
        Assert.True(abs (pendingPoint.X - readyPoint.X) < 0.5, sprintf "remove x %.1f -> %.1f" pendingPoint.X readyPoint.X)
        Assert.True(abs (heightPending - composer.Bounds.Height) < 0.5)

        let many =
            [ for index in 1 .. 30 ->
                  { pending with
                      attachmentId = Guid.NewGuid()
                      sha256 = string index
                      fileName = sprintf "attachment-%02d-with-a-long-name.txt" index
                      ready = index % 3 <> 0
                      size = int64 index * 1024L } ]
        composer.SetAttachments many
        Dispatcher.UIThread.RunJobs()
        let attachmentScroller =
            descendants composer
            |> Seq.choose (function :? ScrollViewer as s when abs (s.MaxHeight - LayoutPolicy.attachmentDraftMaxHeight) < 0.1 -> Some s | _ -> None)
            |> Seq.head
        Assert.True(attachmentScroller.Extent.Height > attachmentScroller.Viewport.Height)
        Assert.True(attachmentScroller.Bounds.Height <= LayoutPolicy.attachmentDraftMaxHeight + 0.5)
    finally
        window.Close()

[<Fact>]
let ``attachment draft uses upload identity so duplicate files stay independent`` () =
    let draft = AttachmentDraftController()
    let firstId = Guid.NewGuid()
    let secondId = Guid.NewGuid()
    let upload id name =
        { attachmentId = id
          fileName = name
          mediaType = "text/plain"
          size = 100L
          sha256 = "same-sha" }
    draft.Begin(upload firstId "copy-1.txt") |> ignore
    draft.Begin(upload secondId "copy-2.txt") |> ignore
    Assert.Equal(2, draft.Items.Length)
    Assert.True draft.HasUploading

    draft.Remove firstId |> ignore
    Assert.Single draft.Items |> ignore
    Assert.Equal(secondId, draft.Items.Head.attachmentId)

    match draft.Complete(firstId, 100L) with
    | Some(_, stillDrafted) -> Assert.False stillDrafted
    | None -> failwith "removed upload should still complete transport bookkeeping"
    Assert.Single draft.Items |> ignore
    Assert.False draft.Items.Head.ready

    match draft.Complete(secondId, 120L) with
    | Some(_, stillDrafted) -> Assert.True stillDrafted
    | None -> failwith "second upload missing"
    Assert.True draft.Items.Head.ready
    Assert.Equal(120L, draft.Items.Head.size)
    let consumed = draft.TryConsumeReady() |> Option.get
    Assert.Single consumed |> ignore
    Assert.Empty draft.Items

[<Fact>]
let ``command feedback tracker resolves exactly once on commit or reject`` () =
    let tracker = CommandFeedbackTracker()
    let conversationId = Guid.NewGuid()
    let firstInvocation = Guid.NewGuid()
    let first = RenameConversation {| invocationId = firstInvocation; conversationId = conversationId; title = "A" |}
    let mutable committed = 0
    tracker.Track(first, Some "saved", fun () -> committed <- committed + 1)
    Assert.Equal(1, tracker.Count)
    let feedback = tracker.Commit firstInvocation |> Option.get
    feedback.onCommitted ()
    Assert.Equal(1, committed)
    Assert.Equal(Some "saved", feedback.successMessage)
    Assert.Equal(0, tracker.Count)
    let duplicateCommit = tracker.Commit firstInvocation
    Assert.True(duplicateCommit.IsNone)

    let rejectedInvocation = Guid.NewGuid()
    let rejected = RenameConversation {| invocationId = rejectedInvocation; conversationId = conversationId; title = "B" |}
    tracker.Track(rejected, None, fun () -> committed <- committed + 1)
    tracker.Reject rejectedInvocation |> ignore
    Assert.Equal(0, tracker.Count)
    Assert.Equal(1, committed)

[<Fact>]
let ``config feedback tracker resolves exactly once and rejects pending UI on disconnect`` () =
    let tracker = ConfigFeedbackTracker()
    let first = Guid.NewGuid()
    let mutable completions: bool list = []
    tracker.Track(first, "已保存", fun ok -> completions <- ok :: completions)
    let feedback = tracker.Resolve(first) |> Option.get
    Assert.Equal("已保存", feedback.successMessage)
    feedback.onCompleted true
    Assert.Single completions |> ignore
    Assert.True completions.Head
    let duplicateResolve = tracker.Resolve first
    Assert.True(duplicateResolve.IsNone)

    let second = Guid.NewGuid()
    let third = Guid.NewGuid()
    tracker.Track(second, "第二项", fun ok -> completions <- ok :: completions)
    tracker.Track(third, "第三项", fun ok -> completions <- ok :: completions)
    tracker.RejectAll()
    Assert.Equal(0, tracker.Count)
    Assert.Equal(3, completions.Length)
    Assert.Equal(2, completions |> List.filter not |> List.length)

[<Fact>]
let ``large tool detail is bounded first and can explicitly expand fully`` () =
    Headless.ensure ()
    let actions: MessageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }
    let context: MessageContext =
        { fontSize = Tokens.fontReading
          autoCollapseReasoning = true
          streaming = false
          isLastAssistant = false
          usage = None
          missingAttachments = Set.empty
          brandAvatar = fun () -> Border(Width = 26.0, Height = 26.0) :> Control }
    let hugeResult = String.Join("\n", [ for i in 1 .. 500 -> sprintf "line %03d: %s" i (String.replicate 8 "payload") ])
    let message =
        { MessageView.empty with
            commitId = Some 1UL
            toolCalls =
                [ { callId = "call-1"
                    name = "huge_tool"
                    argumentsJson = "{\"query\":\"demo\"}"
                    result = Some hugeResult } ] }
    let card = MessageCard.render message context actions
    let window = show card 720.0 520.0
    try
        let tool = byAutomationName card "展开工具调用 huge_tool"
        let invoke = ControlAutomationPeer.CreatePeerForElement tool |> Assert.IsAssignableFrom<IInvokeProvider>
        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal("收起工具调用 huge_tool", AutomationProperties.GetName(tool))

        let detailScroller =
            descendants card
            |> Seq.choose (function :? ScrollViewer as scroller -> Some scroller | _ -> None)
            |> Seq.find (fun scroller ->
                scroller.IsVisible && abs (scroller.MaxHeight - LayoutPolicy.expandedDetailMaxHeight) < 0.1)
        Assert.True(detailScroller.Extent.Height > detailScroller.Viewport.Height)
        let expand = byAutomationName card "展开全部"
        let expandInvoke = ControlAutomationPeer.CreatePeerForElement expand |> Assert.IsAssignableFrom<IInvokeProvider>
        expandInvoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.True(Double.IsPositiveInfinity detailScroller.MaxHeight)
        Assert.Equal("收回限高", AutomationProperties.GetName(expand))

        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal("展开工具调用 huge_tool", AutomationProperties.GetName(tool))
    finally
        window.Close()

[<Fact>]
let ``dialog is constrained to the live viewport and restores focus`` () =
    Headless.ensure ()
    let root = Grid()
    let trigger = Ui.button Ui.Secondary "打开" ignore
    root.Children.Add trigger
    let overlay = OverlayHost(root)
    overlay.WireDismiss()
    let window = show root 320.0 240.0
    try
        trigger.Focus NavigationMethod.Tab |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True trigger.IsFocused

        let fieldShell, field = Ui.textField "测试"
        let content = StackPanel()
        content.Children.Add fieldShell
        content.Children.Add(Border(Width = 900.0, Height = 900.0))
        overlay.ShowDialog(content, 520.0)
        Dispatcher.UIThread.RunJobs()

        let dialog = root.Children[2] :?> Border
        Assert.True(dialog.Width <= root.Bounds.Width - Tokens.space3 * 2.0 + 0.5)
        Assert.True(dialog.MaxHeight <= root.Bounds.Height - Tokens.space3 * 2.0 + 0.5)
        Assert.True field.IsFocused

        window.Width <- 240.0
        window.Height <- 180.0
        Dispatcher.UIThread.RunJobs()
        Assert.True(dialog.MaxWidth <= root.Bounds.Width - Tokens.space3 * 2.0 + 0.5)
        Assert.True(dialog.MaxHeight <= root.Bounds.Height - Tokens.space3 * 2.0 + 0.5)

        overlay.CloseDialog()
        Dispatcher.UIThread.RunJobs()
        Assert.True trigger.IsFocused
    finally
        window.Close()

[<Fact>]
let ``popup flips and clamps inside the viewport then restores anchor focus`` () =
    Headless.ensure ()
    let root = Grid()
    let anchor = Ui.button Ui.Secondary "锚点" ignore
    anchor.HorizontalAlignment <- HorizontalAlignment.Right
    anchor.VerticalAlignment <- VerticalAlignment.Bottom
    anchor.Margin <- Thickness 6.0
    root.Children.Add anchor
    let overlay = OverlayHost(root)
    overlay.WireDismiss()
    let window = show root 320.0 220.0
    try
        anchor.Focus NavigationMethod.Tab |> ignore
        Dispatcher.UIThread.RunJobs()
        let first = Ui.button Ui.Secondary "第一项" ignore
        let tall = StackPanel()
        tall.Children.Add first
        tall.Children.Add(Border(Width = 700.0, Height = 700.0))
        let content =
            ScrollViewer(
                Content = tall,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto)
        overlay.ShowPopup(anchor, content, true, 420.0)
        Dispatcher.UIThread.RunJobs()

        let popup = root.Children[4] :?> Border
        Assert.True(popup.MaxWidth <= root.Bounds.Width - Tokens.space3 * 2.0 + 0.5)
        Assert.True(popup.MaxHeight <= root.Bounds.Height - Tokens.space3 * 2.0 + 0.5)
        Assert.True(popup.Margin.Left >= Tokens.space3 - 0.5)
        Assert.True(popup.Margin.Top >= Tokens.space3 - 0.5)
        Assert.True(popup.Margin.Left + popup.Bounds.Width <= root.Bounds.Width - Tokens.space3 + 0.5)
        Assert.True(popup.Margin.Top + popup.Bounds.Height <= root.Bounds.Height - Tokens.space3 + 0.5)
        Assert.True first.IsFocused

        window.Width <- 240.0
        window.Height <- 160.0
        Dispatcher.UIThread.RunJobs()
        Assert.True(popup.MaxWidth <= root.Bounds.Width - Tokens.space3 * 2.0 + 0.5)
        Assert.True(popup.MaxHeight <= root.Bounds.Height - Tokens.space3 * 2.0 + 0.5)
        Assert.True(popup.Margin.Left >= Tokens.space3 - 0.5)
        Assert.True(popup.Margin.Top >= Tokens.space3 - 0.5)

        overlay.ClosePopup()
        Dispatcher.UIThread.RunJobs()
        Assert.True anchor.IsFocused
    finally
        window.Close()

[<Fact>]
let ``long markdown link has one keyboard stop and remote image states its source`` () =
    Headless.ensure ()
    let renderer = MarkdownRenderer(Tokens.fontReading, ignore, ignore, false)
    let longLabel = String.replicate 12 "very-long-link-segment-"
    let rendered = renderer.RenderText(sprintf "[%s](https://example.com/path)\n\n![架构图](https://cdn.example.com/diagram.png)" longLabel)
    let window = show rendered 420.0 220.0
    try
        let hyperlinkStops =
            descendants rendered
            |> Seq.filter (fun control ->
                control.Focusable
                && AutomationProperties.GetControlTypeOverride(control)
                   = Nullable<AutomationControlType>(AutomationControlType.Hyperlink))
            |> Seq.length
        Assert.Equal(1, hyperlinkStops)
        let imageNotice =
            descendants rendered
            |> Seq.choose (function :? SelectableTextBlock as text -> Some text | _ -> None)
            |> Seq.collect (fun text -> text.Inlines |> Seq.cast<Inline>)
            |> Seq.choose (function :? Run as run -> Some run.Text | _ -> None)
            |> String.concat ""
        Assert.Contains("图片未加载", imageNotice)
        Assert.Contains("cdn.example.com", imageNotice)
    finally
        window.Close()

[<Fact>]
let ``forty item menu keeps long labels bounded and supports directional focus`` () =
    Headless.ensure ()
    let root = Grid()
    let anchor = Ui.button Ui.Secondary "打开菜单" ignore
    anchor.HorizontalAlignment <- HorizontalAlignment.Right
    anchor.VerticalAlignment <- VerticalAlignment.Bottom
    root.Children.Add anchor
    let overlay = OverlayHost(root)
    overlay.WireDismiss()
    let window = show root 360.0 320.0
    try
        anchor.Focus NavigationMethod.Tab |> ignore
        let longLabel = "菜单项 01 " + String.replicate 18 "非常长的名称"
        let entries =
            [ for index in 1 .. 40 ->
                  let label = if index = 1 then longLabel else sprintf "菜单项 %02d" index
                  MenuEntry.create label ignore
                  |> (if index % 3 = 0 then MenuEntry.withIcon Icons.info else id) ]
        Menu.show overlay anchor true entries
        Dispatcher.UIThread.RunJobs()
        let first = byAutomationName root longLabel
        let second = byAutomationName root "菜单项 02"
        Assert.True first.IsFocused
        first.RaiseEvent(KeyEventArgs(Key = Key.Down, RoutedEvent = InputElement.KeyDownEvent))
        Dispatcher.UIThread.RunJobs()
        Assert.True second.IsFocused
        second.RaiseEvent(KeyEventArgs(Key = Key.End, RoutedEvent = InputElement.KeyDownEvent))
        Dispatcher.UIThread.RunJobs()
        let last = byAutomationName root "菜单项 40"
        Assert.True last.IsFocused

        let popup = root.Children[4] :?> Border
        Assert.True(first.Bounds.Width <= popup.Bounds.Width + 0.5)
        let menuScroller =
            visualControls popup
            |> Seq.choose (function :? ScrollViewer as s -> Some s | _ -> None)
            |> Seq.head
        Assert.True(menuScroller.Extent.Height > menuScroller.Viewport.Height)
    finally
        window.Close()

let private message commit text =
    { MessageView.empty with
        role = "assistant"
        text = text
        commitId = Some commit }

[<Fact>]
let ``history prepend preserves the reader viewport anchor`` () =
    Headless.ensure ()
    let messageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }
    let actions =
        { renameTitle = ignore
          openSessionSettings = ignore
          forkFromHere = ignore
          stopGeneration = ignore
          requestOlderHistory = ignore
          retryLast = ignore
          toggleSidebar = ignore
          message = messageActions }
    let chat = ChatView(actions, fun _ -> Border(Width = 26.0, Height = 26.0) :> Control)
    chat.Build()
    let window = show chat 760.0 520.0
    try
        let initial =
            [ for i in 1UL .. 18UL ->
                  message (100UL + i) (String.replicate 18 (sprintf "第 %d 条较长正文，用来稳定地产生可滚动高度。" i)) ]
        chat.RenderMessages(initial, None, None, Tokens.fontReading, true, None, Set.empty)
        Dispatcher.UIThread.RunJobs()
        let scroller = descendants chat |> Seq.choose (function :? ScrollViewer as s -> Some s | _ -> None) |> Seq.head
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height)

        scroller.Offset <- Vector(0.0, min 180.0 (scroller.Extent.Height - scroller.Viewport.Height - 20.0))
        Dispatcher.UIThread.RunJobs()
        let oldExtent = scroller.Extent.Height
        let oldOffset = scroller.Offset.Y
        Assert.True(oldOffset > 0.0)

        chat.BeginHistoryPrependAnchor()
        let older =
            [ for i in 1UL .. 6UL ->
                  message i (String.replicate 14 (sprintf "更早的第 %d 条历史。" i)) ]
        chat.RenderMessages(older @ initial, None, None, Tokens.fontReading, true, None, Set.empty)
        chat.RestoreHistoryPrependAnchorDeferred()
        Dispatcher.UIThread.RunJobs()

        let delta = scroller.Extent.Height - oldExtent
        let expected = oldOffset + delta
        Assert.True(abs (scroller.Offset.Y - expected) < 2.0, sprintf "offset %.1f expected %.1f" scroller.Offset.Y expected)
    finally
        window.Close()

[<Fact>]
let ``repeated streaming updates preserve committed card instances`` () =
    Headless.ensure ()
    let messageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }
    let actions =
        { renameTitle = ignore
          openSessionSettings = ignore
          forkFromHere = ignore
          stopGeneration = ignore
          requestOlderHistory = ignore
          retryLast = ignore
          toggleSidebar = ignore
          message = messageActions }
    let chat = ChatView(actions, fun _ -> Border(Width = 26.0, Height = 26.0) :> Control)
    chat.Build()
    let window = show chat 900.0 620.0
    try
        let committed =
            [ for index in 1UL .. 200UL ->
                  { MessageView.empty with
                      role = if index % 2UL = 0UL then "user" else "assistant"
                      text = sprintf "Committed message %d — %s" index (String.replicate 6 "稳定内容")
                      commitId = Some index } ]
        chat.RenderMessages(committed, None, None, Tokens.fontReading, true, None, Set.empty)
        Dispatcher.UIThread.RunJobs()
        let scroller =
            descendants chat
            |> Seq.choose (function :? ScrollViewer as s -> Some s | _ -> None)
            |> Seq.head
        let panel = scroller.Content :?> StackPanel
        let first = panel.Children[0]
        let watch = Stopwatch.StartNew()
        for update in 1 .. 30 do
            let streaming =
                { MessageView.empty with
                    role = "assistant"
                    text = String.replicate update "流式增量" }
            chat.RenderMessages(committed, Some streaming, None, Tokens.fontReading, true, None, Set.empty)
            Dispatcher.UIThread.RunJobs()
            Assert.True(obj.ReferenceEquals(first, panel.Children[0]))
        watch.Stop()
        Assert.True(watch.Elapsed.TotalMilliseconds < 2500.0, sprintf "30 streaming updates %.0f ms" watch.Elapsed.TotalMilliseconds)
    finally
        window.Close()

let private summary id title =
    { id = id
      title = title
      preview = "preview"
      running = false
      pinned = false
      archived = false
      createdAt = DateTimeOffset.Now
      updatedAt = DateTimeOffset.Now
      messageCount = 2
      isFork = false
      providerId = "test"
      model = "model"
      lastCommitId = 1UL }

[<Fact>]
let ``changing active sidebar row does not rebuild the conversation list`` () =
    Headless.ensure ()
    let actions =
        { newConversation = ignore
          openConversation = ignore
          renameConversation = ignore
          deleteConversation = ignore
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          duplicateAsFork = ignore
          exportConversation = ignore
          openSettings = ignore
          reconnect = ignore
          toggleArchivedVisibility = ignore
          closeNavigation = ignore }
    let root = Grid()
    let overlay = OverlayHost(root)
    let sidebar = Sidebar(overlay, actions, fun _ -> Border() :> Control)
    sidebar.Build()
    root.Children.Insert(0, sidebar)
    let window = show root 360.0 600.0
    try
        let firstId = Guid.NewGuid()
        let secondId = Guid.NewGuid()
        sidebar.SetConversations [ summary firstId "Alpha"; summary secondId "Beta" ]
        Dispatcher.UIThread.RunJobs()
        let list = byAutomationName sidebar "会话列表" :?> ListBox
        let alphaBefore = realizedByAutomationName list "Alpha"
        let betaBefore = realizedByAutomationName list "Beta"

        sidebar.SetActive(Some secondId)
        Dispatcher.UIThread.RunJobs()
        let alphaAfter = realizedByAutomationName list "Alpha"
        let betaAfter = realizedByAutomationName list "Beta"
        Assert.True(obj.ReferenceEquals(alphaBefore, alphaAfter))
        Assert.True(obj.ReferenceEquals(betaBefore, betaAfter))
    finally
        window.Close()

[<Fact>]
let ``sidebar keeps a bounded visual tree for 5000 conversations`` () =
    Headless.ensure ()
    let actions =
        { newConversation = ignore
          openConversation = ignore
          renameConversation = ignore
          deleteConversation = ignore
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          duplicateAsFork = ignore
          exportConversation = ignore
          openSettings = ignore
          reconnect = ignore
          toggleArchivedVisibility = ignore
          closeNavigation = ignore }
    let root = Grid()
    let overlay = OverlayHost(root)
    let sidebar = Sidebar(overlay, actions, fun _ -> Border() :> Control)
    sidebar.Build()
    root.Children.Insert(0, sidebar)
    let window = show root 360.0 700.0
    try
        let items = [ for i in 1 .. 5000 -> summary (Guid.NewGuid()) (sprintf "Conversation %04d" i) ]
        let watch = Stopwatch.StartNew()
        sidebar.SetConversations items
        Dispatcher.UIThread.RunJobs()
        watch.Stop()
        let rows () =
            visualControls sidebar
            |> Seq.filter (fun control ->
                let controlType = AutomationProperties.GetControlTypeOverride(control)
                controlType = Nullable<AutomationControlType>(AutomationControlType.ListItem))
            |> Seq.length
        let list = byAutomationName sidebar "会话列表" :?> ListBox
        let realized = list.GetRealizedContainers() |> Seq.length
        Assert.True(realized > 0 && realized < 80, sprintf "realized containers = %d" realized)
        Assert.True(rows () < 80, sprintf "conversation row visuals = %d" (rows ()))
        Assert.True(watch.Elapsed.TotalMilliseconds < 1500.0, sprintf "5000 conversations first render %.0f ms" watch.Elapsed.TotalMilliseconds)
        list.ScrollIntoView 4999
        Dispatcher.UIThread.RunJobs()
        let realizedAfterScroll = list.GetRealizedContainers() |> Seq.length
        Assert.True(realizedAfterScroll > 0 && realizedAfterScroll < 80, sprintf "realized after scroll = %d" realizedAfterScroll)
    finally
        window.Close()

[<Fact>]
let ``sidebar running pin and idle states keep the same title origin`` () =
    Headless.ensure ()
    let actions =
        { newConversation = ignore
          openConversation = ignore
          renameConversation = ignore
          deleteConversation = ignore
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          duplicateAsFork = ignore
          exportConversation = ignore
          openSettings = ignore
          reconnect = ignore
          toggleArchivedVisibility = ignore
          closeNavigation = ignore }
    let root = Grid()
    let overlay = OverlayHost(root)
    let sidebar = Sidebar(overlay, actions, fun _ -> Border() :> Control)
    sidebar.Build()
    root.Children.Insert(0, sidebar)
    let window = show root 360.0 600.0
    let id = Guid.NewGuid()
    let baseItem = summary id "Stable title"
    try
        let titleOrigin pinned running =
            sidebar.SetConversations [ { baseItem with pinned = pinned; running = running } ]
            Dispatcher.UIThread.RunJobs()
            let list = byAutomationName sidebar "会话列表" :?> ListBox
            let host = realizedByAutomationName list "Stable title"
            let title =
                visualControls host
                |> Seq.choose (function :? TextBlock as text when text.Text = "Stable title" -> Some text | _ -> None)
                |> Seq.head
            title.TranslatePoint(Point(0.0, 0.0), host).Value.X, host.Bounds.Height
        let idleX, idleH = titleOrigin false false
        let pinX, pinH = titleOrigin true false
        let runningX, runningH = titleOrigin false true
        Assert.True(abs (idleX - pinX) < 0.5)
        Assert.True(abs (idleX - runningX) < 0.5)
        for height in [ idleH; pinH; runningH ] do
            Assert.True(height >= ControlMetrics.sidebarRowMinHeight - 0.5)

        sidebar.SetActive(Some id)
        Dispatcher.UIThread.RunJobs()
        let list = byAutomationName sidebar "会话列表" :?> ListBox
        let selected = realizedByAutomationName list "Stable title"
        Assert.Equal("当前会话", AutomationProperties.GetItemStatus(selected))
    finally
        window.Close()

[<Fact>]
let ``sidebar recycles twenty thousand rows through scroll search active and theme changes`` () =
    Headless.ensure ()
    let actions =
        { newConversation = ignore
          openConversation = ignore
          renameConversation = ignore
          deleteConversation = ignore
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          duplicateAsFork = ignore
          exportConversation = ignore
          openSettings = ignore
          reconnect = ignore
          toggleArchivedVisibility = ignore
          closeNavigation = ignore }
    let root = Grid()
    let overlay = OverlayHost(root)
    let sidebar = Sidebar(overlay, actions, fun _ -> Border() :> Control)
    sidebar.Build()
    root.Children.Insert(0, sidebar)
    let window = show root 360.0 700.0
    let initialTheme = Tokens.current ()
    try
        let ids = [| for _ in 1 .. 20000 -> Guid.NewGuid() |]
        let items =
            [ for index in 1 .. 20000 ->
                  { summary ids[index - 1] (sprintf "Conversation %05d" index) with
                      lastCommitId = uint64 index } ]
        let watch = Stopwatch.StartNew()
        sidebar.SetConversations items
        Dispatcher.UIThread.RunJobs()
        watch.Stop()
        let list = byAutomationName sidebar "会话列表" :?> ListBox
        let realizedCount () = list.GetRealizedContainers() |> Seq.length
        let rec awaitRealized remaining name =
            Dispatcher.UIThread.RunJobs()
            let found =
                list.GetRealizedContainers()
                |> Seq.collect visualControls
                |> Seq.tryFind (fun control -> AutomationProperties.GetName(control) = name)
            match found with
            | Some control -> control
            | None when remaining > 0 ->
                Thread.Sleep 10
                awaitRealized (remaining - 1) name
            | None -> failwithf "%s was not realized after ScrollIntoView" name
        Assert.True(realizedCount () > 0 && realizedCount () < 90)
        Assert.True(watch.Elapsed.TotalMilliseconds < 3000.0, sprintf "20k render %.0f ms" watch.Elapsed.TotalMilliseconds)

        // 明确的 lastCommitId 排序保证 20000 在顶部、00001 在底部。
        Assert.NotNull(awaitRealized 8 "Conversation 20000")
        list.ScrollIntoView(list.ItemCount - 1)
        let last = awaitRealized 8 "Conversation 00001"
        Assert.Contains("Conversation 00001", string (ToolTip.GetTip last))
        sidebar.SetActive(Some ids[0])
        Dispatcher.UIThread.RunJobs()
        Assert.Equal("当前会话", AutomationProperties.GetItemStatus(last))

        Tokens.apply Dark
        Dispatcher.UIThread.RunJobs()
        Assert.True(realizedCount () < 90)
        Tokens.apply Light
        Dispatcher.UIThread.RunJobs()
        list.ScrollIntoView 0
        Dispatcher.UIThread.RunJobs()
        Assert.True(realizedCount () < 90)

        let search =
            descendants sidebar
            |> Seq.choose (function :? TextBox as box when box.PlaceholderText = "搜索会话" -> Some box | _ -> None)
            |> Seq.head
        let pumpSearchDebounce () =
            let watch = Stopwatch.StartNew()
            while watch.Elapsed.TotalMilliseconds < 220.0 do
                Thread.Sleep 5
                Dispatcher.UIThread.RunJobs()
        search.Text <- "Conversation 20000"
        pumpSearchDebounce ()
        Assert.NotNull(awaitRealized 8 "Conversation 20000")
        search.Text <- ""
        pumpSearchDebounce ()
        Assert.NotNull(awaitRealized 8 "Conversation 20000")
        Assert.True(realizedCount () < 90)
    finally
        Tokens.apply initialTheme
        window.Close()

[<Fact>]
let ``accelerated craft soak covers the long-session action ledger without visual growth`` () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    overlay.WireDismiss()
    let mutable opened = 0
    let sidebarActions =
        { newConversation = ignore
          openConversation = fun _ -> opened <- opened + 1
          renameConversation = ignore
          deleteConversation = ignore
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          duplicateAsFork = ignore
          exportConversation = ignore
          openSettings = ignore
          reconnect = ignore
          toggleArchivedVisibility = ignore
          closeNavigation = ignore }
    let sidebar = Sidebar(overlay, sidebarActions, fun _ -> Border() :> Control)
    sidebar.Build()
    let mutable submitted = 0
    let composer =
        Composer(
            { submit = fun _ -> submitted <- submitted + 1; true
              stopGeneration = ignore
              pickAttachment = ignore
              removeAttachment = ignore
              openModelPicker = ignore
              dropFiles = ignore
              pasteFromClipboard = fun () -> false })
    composer.Build()
    composer.SetEnabled(true, "")
    let chat =
        ChatView(
            { renameTitle = ignore
              openSessionSettings = ignore
              forkFromHere = ignore
              stopGeneration = ignore
              requestOlderHistory = ignore
              retryLast = ignore
              toggleSidebar = ignore
              message =
                { copyText = ignore
                  regenerate = ignore
                  editAndFork = ignore
                  deleteMessage = ignore
                  downloadAttachment = ignore
                  openLink = ignore } },
            fun _ -> Border(Width = 26.0, Height = 26.0) :> Control)
    chat.Build()
    let main = DockPanel()
    DockPanel.SetDock(composer, Dock.Bottom)
    main.Children.Add composer
    main.Children.Add chat
    let layout = Grid()
    layout.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength 300.0))
    layout.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
    Grid.SetColumn(sidebar, 0)
    Grid.SetColumn(main, 1)
    layout.Children.Add sidebar
    layout.Children.Add main
    root.Children.Insert(0, layout)
    let window = show root 1100.0 720.0
    let initialTheme = Tokens.current ()
    try
        let ids = [| for _ in 1 .. 300 -> Guid.NewGuid() |]
        sidebar.SetConversations
            [ for index in 1 .. 300 ->
                  { summary ids[index - 1] (sprintf "Soak %03d" index) with lastCommitId = uint64 (301 - index) } ]
        Dispatcher.UIThread.RunJobs()
        let list = byAutomationName sidebar "会话列表" :?> ListBox
        let initialLayerCount = root.Children.Count

        for index in 0 .. 19 do
            sidebar.SetActive(Some ids[index])
            Dispatcher.UIThread.RunJobs()
        let search =
            descendants sidebar
            |> Seq.choose (function :? TextBox as box when box.PlaceholderText = "搜索会话" -> Some box | _ -> None)
            |> Seq.head
        for index in 1 .. 20 do
            search.Text <- sprintf "Soak %03d" index
        search.Text <- ""
        Thread.Sleep 190
        Dispatcher.UIThread.RunJobs()

        let send = byAutomationName composer "发送"
        let sendInvoke = ControlAutomationPeer.CreatePeerForElement send |> Assert.IsAssignableFrom<IInvokeProvider>
        for index in 1 .. 30 do
            composer.SetText(sprintf "message %d" index)
            sendInvoke.Invoke()
            Dispatcher.UIThread.RunJobs()
        for _ in 1 .. 10 do
            composer.SetGenerating true
            composer.SetGenerating false
        Assert.Equal("发送", AutomationProperties.GetName(send))

        let anchor = Ui.button Ui.Secondary "soak popup" ignore
        root.Children.Insert(1, anchor)
        for index in 1 .. 20 do
            Menu.show overlay anchor false [ MenuEntry.create (sprintf "item %d" index) ignore ]
            Dispatcher.UIThread.RunJobs()
            overlay.ClosePopup()
            Dispatcher.UIThread.RunJobs()
        root.Children.Remove anchor |> ignore

        for index in 1 .. 10 do
            Tokens.apply(if index % 2 = 0 then Light else Dark)
            Dispatcher.UIThread.RunJobs()

        Assert.Equal(30, submitted)
        Assert.True(list.GetRealizedContainers() |> Seq.length < 90)
        Assert.Equal(initialLayerCount, root.Children.Count)
        Assert.False overlay.IsPopupOpen
    finally
        Tokens.apply initialTheme
        window.Close()

[<Fact>]
let ``long chat title never pushes header actions outside narrow viewport`` () =
    Headless.ensure ()
    let messageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }
    let actions =
        { renameTitle = ignore
          openSessionSettings = ignore
          forkFromHere = ignore
          stopGeneration = ignore
          requestOlderHistory = ignore
          retryLast = ignore
          toggleSidebar = ignore
          message = messageActions }
    let chat = ChatView(actions, fun _ -> Border(Width = 26.0, Height = 26.0) :> Control)
    chat.Build()
    chat.SetConversationChrome true
    chat.SetGenerating(true, "正在读取一个非常非常长的状态说明")
    let title = String.replicate 20 "极长标题WithoutAnyUsefulBreakPoint🙂"
    chat.SetTitle(title, true)
    let window = show chat 540.0 420.0
    try
        Dispatcher.UIThread.RunJobs()
        let settings = byAutomationName chat "会话设置"
        let stop = byAutomationName chat "停止生成"
        for control in [ settings; stop ] do
            let point = control.TranslatePoint(Point(0.0, 0.0), chat)
            Assert.True(point.HasValue)
            Assert.True(point.Value.X >= -0.5)
            Assert.True(point.Value.X + control.Bounds.Width <= chat.Bounds.Width + 0.5)

        chat.SetCompactMode true
        Dispatcher.UIThread.RunJobs()
        let fork = byAutomationName chat "从当前分叉"
        Assert.False fork.IsVisible
        Assert.Equal(LayoutPolicy.compactActionTarget, stop.Bounds.Width, 1)
    finally
        window.Close()

[<Fact>]
let ``desktop 125 and 150 percent scale equivalent viewports keep primary actions in bounds`` () =
    Headless.ensure ()
    let messageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }
    let chatActions =
        { renameTitle = ignore
          openSessionSettings = ignore
          forkFromHere = ignore
          stopGeneration = ignore
          requestOlderHistory = ignore
          retryLast = ignore
          toggleSidebar = ignore
          message = messageActions }
    let composerActions =
        { submit = fun _ -> true
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = ignore
          pasteFromClipboard = fun () -> false }

    // 900×620 物理窗口在 125% / 150% 系统缩放下，对应用布局最重要的是
    // 可用逻辑空间分别约 720×496 / 600×413。这里直接验证同一逻辑空间 contract。
    for scale in [ 1.25; 1.5 ] do
        let width = 900.0 / scale
        let height = 620.0 / scale
        let chat = ChatView(chatActions, fun _ -> Border(Width = 26.0, Height = 26.0) :> Control)
        chat.Build()
        chat.SetCompactMode(width < LayoutPolicy.compactBreakpoint)
        chat.SetConversationChrome true
        chat.SetGenerating(true, "正在生成一段很长很长的状态说明")
        chat.SetTitle(String.replicate 12 "这是一个非常长的会话标题-ABCDEFGHIJK-", true)
        let composer = Composer(composerActions)
        composer.Build()
        composer.SetEnabled(true, "")
        composer.SetCompactMode(width < LayoutPolicy.compactBreakpoint)
        composer.SetText "测试缩放下的输入区"
        let root = DockPanel()
        DockPanel.SetDock(composer, Dock.Bottom)
        root.Children.Add composer
        root.Children.Add chat
        let window = show root width height
        try
            Dispatcher.UIThread.RunJobs()
            for name in [ "切换侧边栏（Ctrl+B / ⌘B）"; "停止生成"; "会话设置"; "发送"; "添加附件" ] do
                let control = byAutomationName root name
                if control.IsVisible then
                    match control.TranslatePoint(Point(0.0, 0.0), root) with
                    | point when point.HasValue ->
                        let p = point.Value
                        Assert.True(p.X >= -0.5 && p.Y >= -0.5, sprintf "%s starts outside at %.1f,%.1f (scale %.2f)" name p.X p.Y scale)
                        Assert.True(p.X + control.Bounds.Width <= root.Bounds.Width + 0.5, sprintf "%s exceeds width at scale %.2f" name scale)
                        Assert.True(p.Y + control.Bounds.Height <= root.Bounds.Height + 0.5, sprintf "%s exceeds height at scale %.2f" name scale)
                    | _ -> failwithf "cannot translate %s at scale %.2f" name scale
        finally
            window.Close()

[<Fact>]
let ``toast stays inside narrow viewport and is keyboard dismissible live content`` () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    overlay.WireDismiss()
    let window = show root 320.0 220.0
    try
        let message = String.replicate 20 "很长的错误说明，需要在窄窗口内保持可访问。"
        overlay.Toast(message, Failure)
        Dispatcher.UIThread.RunJobs()
        let toast = byAutomationName root message
        Assert.True toast.Focusable
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(toast))
        Assert.True(toast.Bounds.Width <= root.Bounds.Width - Tokens.space3 * 2.0 + 0.5)
        let invoke = ControlAutomationPeer.CreatePeerForElement toast |> Assert.IsAssignableFrom<IInvokeProvider>
        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.DoesNotContain(toast, descendants root)
    finally
        window.Close()

[<Fact>]
let ``reduced motion keeps spinner static`` () =
    Headless.ensure ()
    MotionPolicy.setReduced true
    let spinner = Ui.spinner 16.0
    let window = show spinner 80.0 80.0
    try
        Thread.Sleep 140
        Dispatcher.UIThread.RunJobs()
        let view = spinner :?> Viewbox
        let rotation = Assert.IsType<RotateTransform>(view.RenderTransform)
        Assert.Equal(0.0, rotation.Angle, 3)
    finally
        window.Close()
        MotionPolicy.setReduced false

[<Fact>]
let ``motion ledger permits no geometry animation and reduced durations collapse to zero`` () =
    Assert.False MotionLedger.geometryAnimationAllowed
    Assert.Equal(40.0, MotionLedger.busySpinnerFrame.TotalMilliseconds, 3)
    Assert.Equal(1500.0, MotionLedger.copyConfirmationHold.TotalMilliseconds, 3)
    MotionPolicy.setReduced false
    Assert.True(MotionPolicy.duration(180).TotalMilliseconds > 0.0)
    MotionPolicy.setReduced true
    Assert.Equal(TimeSpan.Zero, MotionPolicy.duration 180)
    MotionPolicy.setReduced false

[<Fact>]
let ``tertiary text keeps readable contrast in both palette modes`` () =
    let relativeLuminance (color: Color) =
        let channel (value: byte) =
            let c = float value / 255.0
            if c <= 0.03928 then c / 12.92 else Math.Pow((c + 0.055) / 1.055, 2.4)
        0.2126 * channel color.R + 0.7152 * channel color.G + 0.0722 * channel color.B
    let contrast foreground background =
        let a = relativeLuminance foreground
        let b = relativeLuminance background
        (max a b + 0.05) / (min a b + 0.05)
    for palette in [ Palette.light; Palette.dark ] do
        Assert.True(contrast palette.textFaint palette.canvas >= 4.5)
        Assert.True(contrast palette.textFaint palette.surface >= 4.5)
        Assert.True(contrast palette.textMuted palette.canvas >= 4.5)

[<Fact>]
let ``appearance preference update keeps the same focused control instance`` () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    let settingsActions =
        { upsertProvider = fun _ completed -> completed true
          deleteProvider = ignore
          probeProvider = ignore
          upsertMcp = fun _ completed -> completed true
          deleteMcp = ignore
          updateGeneration = fun _ completed -> completed true
          savePrefs = ignore
          toast = fun _ _ -> () }
    let mutable settingsRef: SettingsView option = None
    let onPrefsChanged next = settingsRef |> Option.iter (fun settings -> settings.SetPrefs next)
    let settings = SettingsView(overlay, settingsActions, onPrefsChanged, ignore)
    settingsRef <- Some settings
    settings.Build()
    settings.SetPrefs UiPrefs.defaults
    root.Children.Insert(0, settings)
    let window = show root 900.0 680.0
    try
        let appearance = byAutomationName settings "外观"
        ControlAutomationPeer.CreatePeerForElement appearance
        |> Assert.IsAssignableFrom<IInvokeProvider>
        |> fun provider -> provider.Invoke()
        Dispatcher.UIThread.RunJobs()

        let largerBefore = byAutomationName settings "更大"
        largerBefore.Focus NavigationMethod.Tab |> ignore
        Assert.True largerBefore.IsFocused
        ControlAutomationPeer.CreatePeerForElement largerBefore
        |> Assert.IsAssignableFrom<IInvokeProvider>
        |> fun provider -> provider.Invoke()
        Dispatcher.UIThread.RunJobs()

        let largerAfter = byAutomationName settings "更大"
        Assert.True(obj.ReferenceEquals(largerBefore, largerAfter))
        Assert.True largerAfter.IsFocused
    finally
        window.Close()

[<Fact>]
let ``settings uses native stretch max width and exposes selected navigation status`` () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    let settingsActions =
        { upsertProvider = fun _ completed -> completed true
          deleteProvider = ignore
          probeProvider = ignore
          upsertMcp = fun _ completed -> completed true
          deleteMcp = ignore
          updateGeneration = fun _ completed -> completed true
          savePrefs = ignore
          toast = fun _ _ -> () }
    let settings = SettingsView(overlay, settingsActions, ignore, ignore)
    settings.Build()
    root.Children.Insert(0, settings)
    let window = show root 1200.0 720.0
    try
        Dispatcher.UIThread.RunJobs()
        let frame =
            descendants settings
            |> Seq.choose (function :? Border as border when abs (border.MaxWidth - ControlMetrics.settingsContentMaxWidth) < 0.1 -> Some border | _ -> None)
            |> Seq.head
        Assert.True(frame.Bounds.Width <= ControlMetrics.settingsContentMaxWidth + 0.5)
        Assert.True(frame.Bounds.Width > 700.0)

        let providers = byAutomationName settings "服务商"
        let appearance = byAutomationName settings "外观"
        Assert.Equal("当前分区", AutomationProperties.GetItemStatus(providers))
        let invokeAppearance = ControlAutomationPeer.CreatePeerForElement appearance |> Assert.IsAssignableFrom<IInvokeProvider>
        invokeAppearance.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.Equal("", AutomationProperties.GetItemStatus(providers))
        Assert.Equal("当前分区", AutomationProperties.GetItemStatus(appearance))

        window.Width <- 600.0
        Dispatcher.UIThread.RunJobs()
        Assert.True(frame.Bounds.Width <= 600.5)
        Assert.True(frame.Bounds.Width > 500.0)
    finally
        window.Close()

[<Fact>]
let ``provider editor stays pending until authoritative config result`` () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    overlay.WireDismiss()
    let mutable pendingCallback: (bool -> unit) option = None
    let settingsActions =
        { upsertProvider = fun _ completed -> pendingCallback <- Some completed
          deleteProvider = ignore
          probeProvider = ignore
          upsertMcp = fun _ completed -> completed true
          deleteMcp = ignore
          updateGeneration = fun _ completed -> completed true
          savePrefs = ignore
          toast = fun _ _ -> () }
    let providers = SettingsProviders(overlay, settingsActions)
    let view = providers.Build()
    root.Children.Insert(0, view)
    let window = show root 760.0 700.0
    try
        let addProvider = byAutomationName view "添加服务商"
        ControlAutomationPeer.CreatePeerForElement addProvider
        |> Assert.IsAssignableFrom<IInvokeProvider>
        |> fun invoke -> invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        Assert.True overlay.IsDialogOpen

        let add = byAutomationName root "添加"
        ControlAutomationPeer.CreatePeerForElement add
        |> Assert.IsAssignableFrom<IInvokeProvider>
        |> fun invoke -> invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        let pending = byAutomationName root "正在保存…"
        Assert.False pending.IsEnabled
        let cancel = byAutomationName root "取消"
        Assert.False cancel.IsEnabled
        Assert.True overlay.IsDialogOpen

        pendingCallback.Value false
        Dispatcher.UIThread.RunJobs()
        let retry = byAutomationName root "添加"
        Assert.True retry.IsEnabled
        Assert.True((byAutomationName root "取消").IsEnabled)
        Assert.True overlay.IsDialogOpen

        ControlAutomationPeer.CreatePeerForElement retry
        |> Assert.IsAssignableFrom<IInvokeProvider>
        |> fun invoke -> invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        pendingCallback.Value true
        Dispatcher.UIThread.RunJobs()
        Assert.False overlay.IsDialogOpen
    finally
        window.Close()

