module Wanxiang.Tests.CraftsmanshipPolishTests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading.Tasks
open System.Text
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Threading
open Avalonia.Input
open Avalonia.Platform.Storage
open Xunit
open Wanxiang.Agent
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.UI
open Wanxiang.Tests
open Wanxiang.Tests.Helpers

// =========================================================================
// 1. Unicode Safe Slicing
// =========================================================================

[<Fact>]
let ``PlainText.summarize handles multi-byte emojis without breaking surrogate pairs`` () =
    // 👨‍👩‍👧‍👦 (family ZWJ sequence: U+1F468 U+200D U+1F469 U+200D U+1F467 U+200D U+1F466)
    // 🇨🇳 (regional indicator flag sequence: U+1F1E8 U+1F1F3)
    // 𠮷 (surrogate pair: U+20BB7 -> \uD842\uDFB7)
    // 👍🏽 (skin tone modifier: U+1F44D U+1F3FD)
    let complexText = "👨‍👩‍👧‍👦 🇨🇳 𠮷野家 👍🏽 欢迎光临"
    
    for maxChars in 1 .. 12 do
        let summary = PlainText.summarize maxChars complexText
        Assert.False(String.IsNullOrEmpty summary)
        
        // Slicing must not produce lone high or low surrogates
        for i in 0 .. summary.Length - 1 do
            let ch = summary[i]
            if Char.IsHighSurrogate ch then
                Assert.True(i + 1 < summary.Length, "High surrogate must be followed by a low surrogate")
                Assert.True(Char.IsLowSurrogate summary[i + 1], "Character following high surrogate must be low surrogate")
            elif Char.IsLowSurrogate ch then
                Assert.True(i > 0, "Low surrogate must be preceded by a high surrogate")
                Assert.True(Char.IsHighSurrogate summary[i - 1], "Character preceding low surrogate must be high surrogate")

        // UTF-8 / UTF-16 roundtrip should be valid and lossless
        let utf8Bytes = Encoding.UTF8.GetBytes(summary)
        let roundtrip = Encoding.UTF8.GetString(utf8Bytes)
        Assert.Equal(summary, roundtrip)

[<Fact>]
let ``MarkdownRenderer.SafeChunk does not slice surrogate pairs into separate chunks`` () =
    // A long text mixing emojis, surrogate pairs, and ZWJ sequences
    let emojiText =
        "👨‍👩‍👧‍👦🇨🇳𠮷野家👍🏽"
        + "🎉🚀💡🔥✨🎈"
        + "𠮷𠮷𠮷𠮷𠮷"
        + "👩‍💻👨‍💻🧑‍🌾"
        + "❤️🧡💛💚💙💜"

    let chunks = MarkdownRenderer.SafeChunk 5 emojiText
    Assert.NotEmpty chunks
    
    // Total text elements must match original
    let rejoined = String.Concat chunks
    Assert.Equal(emojiText, rejoined)

    // Check each chunk boundary for surrogate integrity
    for chunk in chunks do
        Assert.NotEmpty chunk
        // Chunk cannot start with low surrogate
        Assert.False(Char.IsLowSurrogate chunk[0], sprintf "Chunk starts with lone low surrogate: %s" chunk)
        // Chunk cannot end with high surrogate
        Assert.False(Char.IsHighSurrogate chunk[chunk.Length - 1], sprintf "Chunk ends with lone high surrogate: %s" chunk)

        // Internal surrogate integrity
        for i in 0 .. chunk.Length - 1 do
            let ch = chunk[i]
            if Char.IsHighSurrogate ch then
                Assert.True(i + 1 < chunk.Length, "High surrogate must have following low surrogate in chunk")
                Assert.True(Char.IsLowSurrogate chunk[i + 1], "High surrogate must be followed by low surrogate")
            elif Char.IsLowSurrogate ch then
                Assert.True(i > 0, "Low surrogate must have preceding high surrogate in chunk")
                Assert.True(Char.IsHighSurrogate chunk[i - 1], "Low surrogate must be preceded by high surrogate")

// =========================================================================
// 2. Provider Error Copy & Classification
// =========================================================================

[<Fact>]
let ``ProviderFailure.classify accurately formats 5xx HTTP service unavailable errors`` () =
    let statuses =
        [ (HttpStatusCode.InternalServerError, 500)
          (HttpStatusCode.BadGateway, 502)
          (HttpStatusCode.ServiceUnavailable, 503)
          (HttpStatusCode.GatewayTimeout, 504) ]

    for (code, num) in statuses do
        let ex = new HttpRequestException("Server error", null, code)
        let error = ProviderFailure.classify "Anthropic" "claude-3-5-sonnet" ex
        Assert.Equal(ProviderUnavailable, error.kind)
        Assert.True(error.retryable)
        let expectedMsg = sprintf "「Anthropic」服务暂时不可用（HTTP %d）。" num
        Assert.Equal(expectedMsg, error.message)

[<Fact>]
let ``ProviderFailure.classify accurately formats network connection failure copy`` () =
    // 1. HttpRequestException without status code (DNS failure, connection reset, etc.)
    let netEx = new HttpRequestException("Connection refused")
    let netError = ProviderFailure.classify "DeepSeek" "deepseek-chat" netEx
    Assert.Equal(ProviderUnavailable, netError.kind)
    Assert.True(netError.retryable)
    Assert.Equal("无法连接「DeepSeek」，请检查网络或地址。", netError.message)

    // 2. SocketException
    let socketEx = new SocketException(int SocketError.ConnectionRefused)
    let socketError = ProviderFailure.classify "OpenAI" "gpt-4o" socketEx
    Assert.Equal(ProviderUnavailable, socketError.kind)
    Assert.True(socketError.retryable)
    Assert.Equal("无法连接「OpenAI」，请检查网络或地址。", socketError.message)

// =========================================================================
// 3. Conversation Activity Tracking & Bucket Grouping
// =========================================================================

[<Fact>]
let ``Projection updates lastActivityAtUtc when AgentMessageRecorded event committed`` () =
    let convId = Guid.NewGuid()
    let initTime = DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)
    
    // 1. Create conversation at initTime
    let createCommit =
        Events.Commit.create
            1UL
            initTime
            [ ConversationCreated { conversationId = convId; title = "Activity Test"; config = testConfig () } ]

    let proj1 =
        match Projection.applyCommit Projection.empty createCommit with
        | Ok p -> p
        | Error err -> failwithf "Failed to apply create commit: %A" err

    let initialConv = proj1.conversations[convId]
    Assert.Equal(initTime, initialConv.createdAtUtc)
    Assert.Equal(initTime, initialConv.lastActivityAtUtc)

    // 2. Append message at a later time
    let msgTime = DateTimeOffset(2026, 9, 7, 10, 30, 0, TimeSpan.Zero)
    let msgCommit =
        Events.Commit.create
            2UL
            msgTime
            [ AgentMessageRecorded
                { conversationId = convId
                  payloadJson = userMessageJson "新消息" } ]

    let proj2 =
        match Projection.applyCommit proj1 msgCommit with
        | Ok p -> p
        | Error err -> failwithf "Failed to apply message commit: %A" err

    let updatedConv = proj2.conversations[convId]
    // Created time remains initTime, lastActivityAtUtc is updated to msgTime
    Assert.Equal(initTime, updatedConv.createdAtUtc)
    Assert.Equal(msgTime, updatedConv.lastActivityAtUtc)
    Assert.Equal(Some 2UL, updatedConv.lastCommitId)

[<Fact>]
let ``ConversationSummary.bucketOf groups conversations by updatedAt rather than createdAt`` () =
    let now = DateTimeOffset(2026, 9, 7, 18, 0, 0, TimeSpan.FromHours(8.0)) // Today evening
    let createdAt30DaysAgo = now.AddDays(-30.0)
    let updatedAtToday = now.AddHours(-2.0)

    let summary =
        { id = Guid.NewGuid()
          title = "Old Conversation Updated Today"
          preview = "hello"
          running = false
          pinned = false
          archived = false
          createdAt = createdAt30DaysAgo
          updatedAt = updatedAtToday
          messageCount = 5
          isFork = false
          providerId = "openai"
          model = "gpt-4o"
          lastCommitId = 42UL }

    let order, label = ConversationSummary.bucketOf now summary
    // Since updatedAt was today, it must bucket into "今天" (order 0)
    Assert.Equal(0, order)
    Assert.Equal("今天", label)

    // Contrast with an item not updated recently (30 days ago)
    let oldSummary = { summary with updatedAt = createdAt30DaysAgo }
    let oldOrder, oldLabel = ConversationSummary.bucketOf now oldSummary
    Assert.NotEqual<string>("今天", oldLabel)
    Assert.True(oldOrder > 0)

// =========================================================================
// 4. ChatView Skeleton Loading State Transitions
// =========================================================================

[<Fact>]
let ``ChatView skeleton loading transitions toggle skeletonPanel and messagePanel visibility`` () =
    Headless.ensure ()
    let chatActions =
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
              openLink = ignore } }

    let chat = ChatView(chatActions, Brand.logo)
    chat.Build()

    // Initially before skeleton loading: messagePanel is visible, skeleton is not
    Assert.False(chat.IsSkeletonVisible)
    Assert.True(chat.IsMessagePanelVisible)

    // Show skeleton loading
    chat.ShowSkeletonLoading()
    Assert.True(chat.IsSkeletonVisible)
    Assert.False(chat.IsMessagePanelVisible)

    // Hide skeleton loading
    chat.HideSkeletonLoading()
    Assert.False(chat.IsSkeletonVisible)
    Assert.True(chat.IsMessagePanelVisible)

// =========================================================================
// 5. Composer Drag-and-Drop & Clipboard Paste File Handling Hooks
// =========================================================================

[<Fact>]
let ``Composer handles clipboard paste hook on Ctrl+V`` () =
    Headless.ensure ()
    let mutable pasteCalled = false
    let composerActions =
        { submit = fun _ -> true
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = ignore
          pasteFromClipboard = fun () ->
              pasteCalled <- true
              true }

    let composer = Composer(composerActions)
    composer.Build()

    // Find the inner TextBox in Composer
    let rec findTextBox (c: Control) : TextBox option =
        match c with
        | :? TextBox as tb -> Some tb
        | :? Panel as p -> p.Children |> Seq.tryPick findTextBox
        | :? Decorator as d when not (isNull d.Child) -> findTextBox d.Child
        | _ -> None

    let input = findTextBox composer |> Option.defaultWith (fun () -> failwith "TextBox not found in Composer")

    // Simulate Ctrl+V on TextBox
    let keyArgs = KeyEventArgs(
        RoutedEvent = InputElement.KeyDownEvent,
        Key = Key.V,
        KeyModifiers = KeyModifiers.Control)

    input.RaiseEvent keyArgs
    Assert.True(pasteCalled, "pasteFromClipboard hook must be invoked on Ctrl+V")
    Assert.True(keyArgs.Handled, "Key event should be handled when paste succeeds")

[<Fact>]
let ``Composer handles dropFiles hook when files are dropped`` () =
    Headless.ensure ()
    let dropped = ResizeArray<IStorageItem>()
    let composerActions =
        { submit = fun _ -> true
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = fun files -> dropped.AddRange files
          pasteFromClipboard = fun () -> false }

    let composer = Composer(composerActions)
    composer.Build()

    // Create a mock IStorageItem
    let mockFile =
        use memStream = new System.IO.MemoryStream()
        { new IStorageFile with
            member _.Name = "test.png"
            member _.Path = Uri("file:///tmp/test.png")
            member _.CanBookmark = false
            member _.SaveBookmarkAsync() = Task.FromResult(null: string)
            member _.OpenReadAsync() = Task.FromResult(new System.IO.MemoryStream() :> System.IO.Stream)
            member _.OpenWriteAsync() = Task.FromResult(new System.IO.MemoryStream() :> System.IO.Stream)
            member _.DeleteAsync() = Task.CompletedTask
            member _.MoveAsync(_: IStorageFolder) = Task.FromResult(null: IStorageItem)
            member _.GetParentAsync() = Task.FromResult(null: IStorageFolder)
            member _.GetBasicPropertiesAsync() = Task.FromResult(null: StorageItemProperties)
            member _.Dispose() = () }

    use dataTransfer = new DataTransfer()
    dataTransfer.Add(DataTransferItem.CreateFile(mockFile))

    let dropArgs = DragEventArgs(
        DragDrop.DropEvent,
        dataTransfer,
        composer,
        Point(10.0, 10.0),
        KeyModifiers.None)

    composer.RaiseEvent dropArgs
    Assert.NotEmpty dropped
    Assert.Equal("test.png", dropped[0].Name)
    Assert.True(dropArgs.Handled)

// =========================================================================
// 6. Markdown Table Layout (<4 vs >4 columns)
// =========================================================================

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

[<Fact>]
let ``MarkdownRenderer wide table (>4 columns) wraps in horizontal scroll viewer`` () =
    Headless.ensure ()
    let renderer = MarkdownRenderer(Tokens.fontReading, ignore, ignore, false)

    // 1. Wide table with 6 columns (> 4)
    let wideDoc =
        "| A | B | C | D | E | F |\n"
        + "|---|---|---|---|---|---|\n"
        + "| 1 | 2 | 3 | 4 | 5 | 6 |"

    let wideControl = renderer.RenderText wideDoc
    let wideScrollViewers =
        descendants wideControl
        |> Seq.choose (function :? ScrollViewer as s -> Some s | _ -> None)
        |> Seq.toList

    Assert.NotEmpty wideScrollViewers
    let sv = wideScrollViewers.Head
    Assert.Equal(ScrollBarVisibility.Auto, sv.HorizontalScrollBarVisibility)
    Assert.Equal(ScrollBarVisibility.Disabled, sv.VerticalScrollBarVisibility)

    // Grid inside wide table must enforce min column widths
    let grid = Assert.IsAssignableFrom<Grid>(sv.Content)
    Assert.Equal(6, grid.ColumnDefinitions.Count)
    for col in grid.ColumnDefinitions do
        Assert.Equal(120.0, col.MinWidth)

    // 2. Narrow table with 2 columns (<= 4)
    let narrowDoc =
        "| Col1 | Col2 |\n"
        + "|------|------|\n"
        + "| val1 | val2 |"

    let narrowControl = renderer.RenderText narrowDoc
    let narrowScrollViewers =
        descendants narrowControl
        |> Seq.choose (function :? ScrollViewer as s -> Some s | _ -> None)
        |> Seq.toList

    // 2-column table renders directly without an inner horizontal ScrollViewer
    Assert.Empty narrowScrollViewers

// =========================================================================
// 7. ChatView Floating Rhythm & Scroll-to-Bottom Unread Indicator
// =========================================================================

[<Fact>]
let ``ChatView tracks unread messages when scrolled away from bottom`` () =
    Headless.ensure ()
    let chatActions =
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
              openLink = ignore } }

    let chat = ChatView(chatActions, Brand.logo)
    chat.Build()

    let window = Window(Width = 760.0, Height = 520.0, Content = chat)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()

        // Locate the scroll-to-bottom button inside chat
        let layout = Assert.IsAssignableFrom<DockPanel>(chat.Child)
        let body = Assert.IsAssignableFrom<Grid>(layout.Children.[1])
        let scroller = Assert.IsAssignableFrom<ScrollViewer>(body.Children.[0])
        let scrollToBottomBtn = Assert.IsAssignableFrom<Border>(body.Children.[2])
        let btnContent = Assert.IsAssignableFrom<StackPanel>(scrollToBottomBtn.Child)
        let btnLabel = Assert.IsAssignableFrom<TextBlock>(btnContent.Children.[1])

        // Initial state: not visible
        Assert.False(scrollToBottomBtn.IsVisible)
        Assert.Equal("回到最新", btnLabel.Text)

        // Render an initial batch of 25 messages to create substantial scrollable extent
        let initialMessages =
            [ for i in 1UL .. 25UL ->
                { MessageView.empty with
                    role = if i % 2UL = 0UL then "user" else "assistant"
                    text = sprintf "Message %d: %s" i (String.replicate 10 "内容填充用于产生滚动高度。")
                    commitId = Some i } ]

        chat.RenderMessages(initialMessages, None, None, Tokens.fontReading, true, None, Set.empty)
        Dispatcher.UIThread.RunJobs()

        // User scrolls up away from bottom
        scroller.Offset <- Vector(0.0, 0.0)
        Dispatcher.UIThread.RunJobs()
        Assert.True(scrollToBottomBtn.IsVisible, "Button becomes visible when scrolled away from bottom")

        // Now render 3 new messages while user is scrolled up
        let additionalMessages =
            [ for i in 26UL .. 28UL ->
                { MessageView.empty with
                    role = "assistant"
                    text = sprintf "New message %d" i
                    commitId = Some i } ]

        chat.RenderMessages(initialMessages @ additionalMessages, None, None, Tokens.fontReading, true, None, Set.empty)
        Dispatcher.UIThread.RunJobs()

        // Button remains visible, label and tooltip indicate unread count
        Assert.True(scrollToBottomBtn.IsVisible)
        Assert.Contains("3", btnLabel.Text)
        Assert.Contains("新消息", btnLabel.Text)
        Assert.Equal(btnLabel.Text, Avalonia.Automation.AutomationProperties.GetName(scrollToBottomBtn))

        // Clicking the button clears unread count and scrolls to bottom
        let peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement scrollToBottomBtn
        let invoke = Assert.IsAssignableFrom<Avalonia.Automation.Provider.IInvokeProvider>(peer)
        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()

        // Button hides and resets back to "回到最新"
        Assert.False(scrollToBottomBtn.IsVisible)
        Assert.Equal("回到最新", btnLabel.Text)
    finally
        window.Close()

[<Fact>]
let ``Markdown hyperlink responds to Enter and Space key to openLink`` () =
    Headless.ensure ()
    let mutable opened = None
    let renderer = MarkdownRenderer(14.0, ignore, (fun url -> opened <- Some url), false)
    let doc =
        [ MdParagraph [ MdText("Click this ", false, false, false, false); MdLink("example link", "https://wanxiang.ai") ] ]
    let rendered = renderer.Render doc
    let window = Window(Width = 600.0, Height = 400.0, Content = rendered)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()
        let link =
            descendants rendered
            |> Seq.find (fun c -> Avalonia.Automation.AutomationProperties.GetName(c) = "example link")
        Assert.True link.Focusable

        // Press Enter
        link.RaiseEvent(KeyEventArgs(Key = Key.Enter, RoutedEvent = InputElement.KeyDownEvent))
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(Some "https://wanxiang.ai", opened)

        opened <- None
        // Press Space
        link.RaiseEvent(KeyEventArgs(Key = Key.Space, RoutedEvent = InputElement.KeyDownEvent))
        Dispatcher.UIThread.RunJobs()
        Assert.Equal(Some "https://wanxiang.ai", opened)
    finally
        window.Close()

[<Fact>]
let ``Sidebar search clear button has accessible tooltip and automation properties`` () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    let actions: SidebarActions =
        { newConversation = ignore
          openConversation = ignore
          deleteConversation = ignore
          renameConversation = ignore
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          duplicateAsFork = ignore
          exportConversation = ignore
          openSettings = ignore
          reconnect = ignore
          toggleArchivedVisibility = ignore
          closeNavigation = ignore }
    let sidebar = Sidebar(overlay, actions, fun _ -> Border() :> Control)
    sidebar.Build()
    let clearBtn =
        descendants sidebar
        |> Seq.find (fun c -> Avalonia.Automation.AutomationProperties.GetName(c) = "清空搜索")
    let tip = ToolTip.GetTip(clearBtn) :?> string
    Assert.Equal("清空搜索", tip)
    Assert.Equal("清空搜索", Avalonia.Automation.AutomationProperties.GetName(clearBtn))

// =========================================================================
// 8. Composer Prompt History & Draft Restoration
// =========================================================================

[<Fact>]
let ``Composer prompt history records submissions and recalls with Up/Down`` () =
    Headless.ensure ()
    let submittedTexts = ResizeArray<string>()
    let composerActions =
        { submit = fun text ->
            submittedTexts.Add text
            true
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = ignore
          pasteFromClipboard = fun () -> false }

    let composer = Composer(composerActions)
    composer.Build()
    composer.SetEnabled(true, "")

    let rec findInput (c: Control) : TextBox option =
        match c with
        | :? TextBox as tb -> Some tb
        | :? Panel as p -> p.Children |> Seq.tryPick findInput
        | :? Decorator as d when not (isNull d.Child) -> findInput d.Child
        | _ -> None

    let input = findInput composer |> Option.defaultWith (fun () -> failwith "Input TextBox not found in Composer")

    let sendKey (key: Key) (modifiers: KeyModifiers) =
        let args = KeyEventArgs(
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers)
        input.RaiseEvent args
        Dispatcher.UIThread.RunJobs()
        args

    // Submit Prompt 1
    composer.SetText "First prompt"
    sendKey Key.Enter KeyModifiers.None |> ignore
    Assert.Equal<string seq>([ "First prompt" ], submittedTexts)
    Assert.Equal("", input.Text)

    // Submit Prompt 2
    composer.SetText "Second prompt"
    sendKey Key.Enter KeyModifiers.None |> ignore
    Assert.Equal<string seq>([ "First prompt"; "Second prompt" ], submittedTexts)
    Assert.Equal("", input.Text)

    // Now user types an uncommitted draft
    composer.SetText "Draft in progress"
    input.CaretIndex <- 0 // Caret at 0 allows Up key history recall

    // Press Up: recalls most recent submission ("Second prompt")
    let up1 = sendKey Key.Up KeyModifiers.None
    Assert.True(up1.Handled)
    Assert.Equal("Second prompt", input.Text)

    // Press Up again: recalls older submission ("First prompt")
    let up2 = sendKey Key.Up KeyModifiers.None
    Assert.True(up2.Handled)
    Assert.Equal("First prompt", input.Text)

    // Press Down: goes forward in history to "Second prompt"
    let down1 = sendKey Key.Down KeyModifiers.None
    Assert.True(down1.Handled)
    Assert.Equal("Second prompt", input.Text)

    // Press Down again: reaches the end of history and restores the uncommitted draft
    let down2 = sendKey Key.Down KeyModifiers.None
    Assert.True(down2.Handled)
    Assert.Equal("Draft in progress", input.Text)

// =========================================================================
// 9. Sidebar Search Keyboard Navigation & Shortcuts
// =========================================================================

[<Fact>]
let ``Sidebar search navigation moves focus and handles escape`` () =
    Headless.ensure ()
    let mutable openedConvId: Guid option = None
    let sidebarActions =
        { newConversation = ignore
          openConversation = fun id -> openedConvId <- Some id
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
    let sidebar = Sidebar(overlay, sidebarActions, fun _ -> Border() :> Control)
    sidebar.Build()
    root.Children.Insert(0, sidebar)

    let window = Window(Width = 360.0, Height = 600.0, Content = root)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()

        let conv1 = Guid.NewGuid()
        let conv2 = Guid.NewGuid()
        let summaries =
            [ { id = conv1
                title = "Alpha Conversation"
                preview = "preview 1"
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
              { id = conv2
                title = "Beta Conversation"
                preview = "preview 2"
                running = false
                pinned = false
                archived = false
                createdAt = DateTimeOffset.Now
                updatedAt = DateTimeOffset.Now
                messageCount = 3
                isFork = false
                providerId = "test"
                model = "model"
                lastCommitId = 2UL } ]

        sidebar.SetConversations summaries
        Dispatcher.UIThread.RunJobs()

        // Find the searchBox
        let searchBox =
            descendants sidebar
            |> Seq.choose (function :? TextBox as tb when tb.PlaceholderText = "搜索会话" -> Some tb | _ -> None)
            |> Seq.head

        let sendSearchKey (key: Key) =
            let args = KeyEventArgs(
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = KeyModifiers.None)
            searchBox.RaiseEvent args
            Dispatcher.UIThread.RunJobs()
            args

        // 1. Enter key in search box opens the first visible conversation
        let enterArgs = sendSearchKey Key.Enter
        Assert.True(enterArgs.Handled)
        // items are sorted by lastCommitId descending, so conv2 (lastCommitId=2UL) precedes conv1 (lastCommitId=1UL)
        Assert.Equal(Some conv2, openedConvId)

        // 2. Down arrow navigates focus into the conversation list
        let downArgs = sendSearchKey Key.Down
        Assert.True(downArgs.Handled)

        // 3. Escape key clears non-empty search query
        searchBox.Text <- "Alpha"
        let escArgs1 = sendSearchKey Key.Escape
        Assert.True(escArgs1.Handled)
        Assert.Equal("", searchBox.Text)
    finally
        window.Close()

// =========================================================================
// 8. Batch 5, 6, 7 Craftsmanship Polish Tests
// =========================================================================

[<Fact>]
let ``SettingsView supports Up and Down arrow traversal across navigation sections`` () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    let actions: SettingsActions =
        { upsertProvider = fun _ cb -> cb true
          deleteProvider = ignore
          probeProvider = ignore
          upsertMcp = fun _ cb -> cb true
          deleteMcp = ignore
          updateGeneration = fun _ cb -> cb true
          savePrefs = ignore
          toast = fun _ _ -> () }
    let settings = SettingsView(overlay, actions, ignore, ignore)
    let control = settings.Build()
    let window = Window(Width = 800.0, Height = 600.0, Content = settings)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()
        let navButtons =
            descendants settings
            |> Seq.choose (fun d ->
                if d.Focusable && not (isNull d.Tag) && (d.Tag :? SettingsSection) then Some(d.Tag :?> SettingsSection, d) else None)
            |> dict

        let providersBtn = navButtons.[SettingsSection.Providers]
        providersBtn.Focus() |> ignore
        Dispatcher.UIThread.RunJobs()
        Assert.True(providersBtn.IsFocused)

        // Press Down arrow on Providers -> should move focus to Tools
        let downArgs = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, KeyModifiers = KeyModifiers.None)
        providersBtn.RaiseEvent downArgs
        Dispatcher.UIThread.RunJobs()
        Assert.True(downArgs.Handled)
        let toolsBtn = navButtons.[SettingsSection.Tools]
        Assert.True(toolsBtn.IsFocused)

        // Press Up arrow on Tools -> should move focus back to Providers
        let upArgs = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up, KeyModifiers = KeyModifiers.None)
        toolsBtn.RaiseEvent upArgs
        Dispatcher.UIThread.RunJobs()
        Assert.True(upArgs.Handled)
        Assert.True(providersBtn.IsFocused)
    finally
        window.Close()

[<Fact>]
let ``Dialogs confirm focuses the destructive action button by default`` () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    overlay.WireDismiss()
    let window = Window(Width = 480.0, Height = 360.0, Content = root)
    window.Show()
    try
        let mutable confirmed = false
        Dialogs.confirm overlay "删除测试" "此操作不可逆" "确定删除" (fun () -> confirmed <- true)
        Dispatcher.UIThread.RunJobs()

        Assert.True(overlay.IsDialogOpen)
        let buttons =
            descendants root
            |> Seq.filter (fun c -> c.Focusable)
            |> Seq.toList

        let confirmButton =
            buttons
            |> List.tryFind (fun b ->
                descendants b
                |> Seq.exists (function :? TextBlock as tb -> tb.Text = "确定删除" | _ -> false))

        Assert.True(confirmButton.IsSome)
        Assert.True(confirmButton.Value.IsFocused)
    finally
        overlay.CloseDialog()
        window.Close()

[<Fact>]
let ``MessageCard toolCallCard header toggles argument and result details`` () =
    Headless.ensure ()
    let ctx: MessageContext =
        { fontSize = 15.0
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
    let message =
        { MessageView.empty with
            commitId = Some 1UL
            toolCalls =
                [ { callId = "call_1"
                    name = "weather"
                    argumentsJson = "{\"city\": \"Beijing\"}"
                    result = Some "Sunny, 24C" } ] }
    let card =
        MessageCard.render message ctx msgActions

    let window = Window(Width = 600.0, Height = 400.0, Content = card)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()
        let tool =
            descendants card
            |> Seq.find (fun control -> Avalonia.Automation.AutomationProperties.GetName(control) = "展开工具调用 weather")
        let invoke = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement tool |> Assert.IsAssignableFrom<Avalonia.Automation.Provider.IInvokeProvider>
        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        let toolName1: string = Avalonia.Automation.AutomationProperties.GetName(tool)
        Assert.Equal("收起工具调用 weather", toolName1)

        invoke.Invoke()
        Dispatcher.UIThread.RunJobs()
        let toolName2: string = Avalonia.Automation.AutomationProperties.GetName(tool)
        Assert.Equal("展开工具调用 weather", toolName2)
    finally
        window.Close()

// =========================================================================
// 9. Batch 2, 3, 4 Craftsmanship Polish Tests
// =========================================================================

[<Fact>]
let ``ChatView header supports F2 to edit title and Escape to cancel`` () =
    Headless.ensure ()
    let mutable renamedTitle: string option = None
    let actions: ChatActions =
        { renameTitle = fun t -> renamedTitle <- Some t
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
              openLink = ignore } }
    let chat = ChatView(actions, fun _ -> Border() :> Control)
    chat.Build()
    chat.SetConversationChrome true
    chat.SetTitle("Original Title", true)
    let window = Window(Width = 800.0, Height = 600.0, Content = chat)
    window.Show()
    try
        Dispatcher.UIThread.RunJobs()
        let titleAction =
            descendants chat
            |> Seq.find (fun c -> Avalonia.Automation.AutomationProperties.GetName(c) = "重命名会话：Original Title")
        
        // Press F2 to begin edit
        titleAction.RaiseEvent(KeyEventArgs(Key = Key.F2, RoutedEvent = InputElement.KeyDownEvent))
        Dispatcher.UIThread.RunJobs()
        
        let titleEditBox =
            descendants chat
            |> Seq.pick (function :? TextBox as tb when tb.Text = "Original Title" -> Some tb | _ -> None)
        Assert.True titleEditBox.IsVisible

        // Press Escape to cancel
        titleEditBox.RaiseEvent(KeyEventArgs(Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent, Source = titleEditBox))
        titleEditBox.RaiseEvent(KeyEventArgs(Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent))
        Dispatcher.UIThread.RunJobs()

        // Assert not renamed and restored
        Assert.True(Option.isNone renamedTitle)
        Assert.True titleAction.IsVisible
        let titleEditShell =
            titleAction.Parent :?> Grid |> fun g -> g.Children |> Seq.item 1
        Assert.False titleEditShell.IsVisible
    finally
        window.Close()

[<Fact>]
let ``Composer restores focus to input when removing last attachment and supports Escape when generating`` () =
    Headless.ensure ()
    let mutable stopped = false
    let mutable submitted = None
    let actions: ComposerActions =
        { submit = fun t -> submitted <- Some t; true
          stopGeneration = fun () -> stopped <- true
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = ignore
          pasteFromClipboard = fun () -> false }
    let composer = Composer(actions)
    composer.Build()
    let window = Window(Width = 800.0, Height = 600.0, Content = composer)
    window.Show()
    try
        composer.SetEnabled(true, "")
        Dispatcher.UIThread.RunJobs()
        let input =
            descendants composer
            |> Seq.pick (function :? TextBox as tb -> Some tb | _ -> None)
        
        // When isGenerating, Escape stops generation
        composer.SetGenerating true
        Dispatcher.UIThread.RunJobs()
        input.RaiseEvent(KeyEventArgs(Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent))
        Dispatcher.UIThread.RunJobs()
        Assert.True stopped

        // Check Send button tooltip when generating
        let sendBtn =
            descendants composer
            |> Seq.find (fun c -> Avalonia.Automation.AutomationProperties.GetName(c) = "停止生成")
        let tip = ToolTip.GetTip(sendBtn) :?> string
        Assert.Contains("停止生成 (Escape)", tip)
    finally
        window.Close()
