module Wanxiang.Tests.UiStabilityTests

open System
open System.Diagnostics
open System.Threading
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Automation.Provider
open Avalonia.Controls
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
        Ui.clearFieldError box
        Dispatcher.UIThread.RunJobs()
        Assert.False message.IsVisible
        Assert.Equal(before, shell.BorderThickness)
    finally
        window.Close()

[<Fact>]
let ``composer blocks send while any attachment is uploading`` () =
    Headless.ensure ()
    let mutable submitted = 0
    let actions =
        { submit = fun _ -> submitted <- submitted + 1
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore }
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
        { submit = ignore
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore }
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
    Assert.True(tracker.Commit(firstInvocation).IsNone)

    let rejectedInvocation = Guid.NewGuid()
    let rejected = RenameConversation {| invocationId = rejectedInvocation; conversationId = conversationId; title = "B" |}
    tracker.Track(rejected, None, fun () -> committed <- committed + 1)
    tracker.Reject rejectedInvocation
    Assert.Equal(0, tracker.Count)
    Assert.Equal(1, committed)

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

        overlay.ClosePopup()
        Dispatcher.UIThread.RunJobs()
        Assert.True anchor.IsFocused
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

let private summary id title =
    { id = id
      title = title
      preview = "preview"
      running = false
      pinned = false
      archived = false
      createdAt = DateTimeOffset.Now
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
        { submit = ignore
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore }

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
let ``appearance preference update keeps the same focused control instance`` () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    let settingsActions =
        { upsertProvider = ignore
          deleteProvider = ignore
          probeProvider = ignore
          upsertMcp = ignore
          deleteMcp = ignore
          updateGeneration = ignore
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

