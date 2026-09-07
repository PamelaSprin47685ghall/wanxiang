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
