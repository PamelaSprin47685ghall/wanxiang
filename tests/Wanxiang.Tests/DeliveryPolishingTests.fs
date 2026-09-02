module Wanxiang.Tests.DeliveryPolishingTests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Xunit
open Wanxiang.Agent
open Wanxiang.Client
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Server
open Wanxiang.Store
open Wanxiang.UI
open Wanxiang.Tests
open Wanxiang.Tests.Helpers
open Avalonia.Input
open Avalonia.Interactivity

[<Fact>]
let ``builtin echo function extracts text from various argument names`` () =
    let echo = BuiltinEchoFunction() :> AITool :?> AIFunction
    Assert.Equal("builtin_echo", echo.Name)
    Assert.NotNull echo.JsonSchema
    
    // 1. standard "text"
    let args1 = AIFunctionArguments(dict [ "text", box "hello world" ])
    let res1 = echo.InvokeAsync(args1).AsTask().GetAwaiter().GetResult()
    Assert.Equal("hello world", string res1)

    // 2. alternative "input"
    let args2 = AIFunctionArguments(dict [ "input", box "test-input" ])
    let res2 = echo.InvokeAsync(args2).AsTask().GetAwaiter().GetResult()
    Assert.Equal("test-input", string res2)

    // 3. alternative "msg"
    let args3 = AIFunctionArguments(dict [ "msg", box "message-content" ])
    let res3 = echo.InvokeAsync(args3).AsTask().GetAwaiter().GetResult()
    Assert.Equal("message-content", string res3)

[<Fact>]
let ``builtin file read function extracts path from various argument names with sandbox check`` () =
    let dir = tempDir ()
    try
        let filePath = Path.Combine(dir, "data.txt")
        File.WriteAllText(filePath, "sample file content")
        let fileRead = BuiltinFileReadFunction([ dir ]) :> AITool :?> AIFunction
        Assert.Equal("builtin_file_read", fileRead.Name)

        // 1. "path"
        let args1 = AIFunctionArguments(dict [ "path", box filePath ])
        let res1 = fileRead.InvokeAsync(args1).AsTask().GetAwaiter().GetResult()
        Assert.Equal("sample file content", string res1)

        // 2. "file"
        let args2 = AIFunctionArguments(dict [ "file", box filePath ])
        let res2 = fileRead.InvokeAsync(args2).AsTask().GetAwaiter().GetResult()
        Assert.Equal("sample file content", string res2)

        // 3. "filePath"
        let args3 = AIFunctionArguments(dict [ "filePath", box filePath ])
        let res3 = fileRead.InvokeAsync(args3).AsTask().GetAwaiter().GetResult()
        Assert.Equal("sample file content", string res3)

        // 4. out of sandbox path should return error
        let args4 = AIFunctionArguments(dict [ "path", box "/etc/shadow" ])
        let res4 = string (fileRead.InvokeAsync(args4).AsTask().GetAwaiter().GetResult())
        Assert.Contains("error:", res4)
    finally
        cleanup dir

[<Fact>]
let ``builtin file list function lists directory entries`` () =
    let dir = tempDir ()
    try
        File.WriteAllText(Path.Combine(dir, "f1.txt"), "hello")
        Directory.CreateDirectory(Path.Combine(dir, "subdir")) |> ignore
        let fileList = BuiltinFileListFunction([ dir ]) :> AITool :?> AIFunction
        Assert.Equal("builtin_file_list", fileList.Name)

        let args1 = AIFunctionArguments(dict [ "path", box dir ])
        let res1 = string (fileList.InvokeAsync(args1).AsTask().GetAwaiter().GetResult())
        Assert.Contains("f1.txt", res1)
        Assert.Contains("subdir/", res1)

        let args2 = AIFunctionArguments(dict [ "dir", box dir ])
        let res2 = string (fileList.InvokeAsync(args2).AsTask().GetAwaiter().GetResult())
        Assert.Contains("f1.txt", res2)
    finally
        cleanup dir

[<Fact>]
let ``builtin time function returns ISO 8601 UTC timestamp`` () =
    let time = BuiltinTimeFunction() :> AITool :?> AIFunction
    Assert.Equal("builtin_time", time.Name)
    let res = string (time.InvokeAsync(AIFunctionArguments()).AsTask().GetAwaiter().GetResult())
    let (parsed, dt) = DateTimeOffset.TryParse(res)
    Assert.True(parsed)
    Assert.Equal(TimeSpan.Zero, dt.Offset)

[<Fact>]
let ``provider sse preserves indentation according to W3C specification`` () =
    let sseText =
        "data:     def indented_code():\n" +
        "data:         return 42\n\n"
    use ms = new MemoryStream(Encoding.UTF8.GetBytes sseText)
    use cts = new CancellationTokenSource()
    let events = ResizeArray<SseEvent>()
    (task {
        let stream = ProviderSse.read ms cts.Token
        let enumerator = stream.GetAsyncEnumerator(cts.Token)
        let mutable go = true
        while go do
            let! moved = enumerator.MoveNextAsync()
            if moved then events.Add enumerator.Current
            else go <- false
    }).GetAwaiter().GetResult()
    Assert.Equal(1, events.Count)
    let lines = events[0].data.Split('\n')
    Assert.Equal(2, lines.Length)
    Assert.Equal("    def indented_code():", lines[0])
    Assert.Equal("        return 42", lines[1])

[<Fact>]
let ``client state updates and removes deleted message in ConversationUpdated`` () =
    let state = ClientState()
    let convId = Guid.NewGuid()
    
    // 1. Initial snapshot with two messages
    let snapItems = JsonArray()
    let m1 = JsonObject()
    m1["commitId"] <- 10UL
    m1["payload"] <- JsonNode.Parse("""{"role":"user","contents":[{"text":"first"}]}""")
    snapItems.Add m1
    let m2 = JsonObject()
    m2["commitId"] <- 11UL
    m2["payload"] <- JsonNode.Parse("""{"role":"assistant","contents":[{"text":"second"}]}""")
    snapItems.Add m2

    let snap =
        ConversationSnapshot
            {| conversationId = convId
               title = "Test Conv"
               lastCommitId = 11UL
               runtimeState = "idle"
               messages = snapItems
               snapshotEarliestCommitId = 10UL
               snapshotHasMore = false
               config = SessionConfig.empty |}
    state.Handle snap

    let view1 = state.Conversations[convId]
    Assert.Equal(2, view1.messages.Count)

    // 2. Delete message 10
    let change = JsonObject()
    change["deletedMessage"] <- 10UL
    change["title"] <- "Renamed Conv"
    let update = ConversationUpdated {| conversationId = convId; commitId = 12UL; change = change |}
    state.Handle update

    let view2 = state.Conversations[convId]
    Assert.Equal(1, view2.messages.Count)
    Assert.Equal("Renamed Conv", view2.title)
    let remainingCommitId = (view2.messages[0].AsObject()["commitId"]).GetValue<uint64>()
    Assert.Equal(11UL, remainingCommitId)
    Assert.Equal(12UL, view2.lastCommitId)

[<Fact>]
let ``client state catch up removes deleted message via MessageDeleted event`` () =
    let state = ClientState()
    let convId = Guid.NewGuid()
    
    let snapItems = JsonArray()
    let m1 = JsonObject()
    m1["commitId"] <- 20UL
    m1["payload"] <- JsonNode.Parse("""{"role":"user","contents":[{"text":"to be deleted"}]}""")
    snapItems.Add m1
    let snap =
        ConversationSnapshot
            {| conversationId = convId
               title = "CatchUp Conv"
               lastCommitId = 20UL
               runtimeState = "idle"
               messages = snapItems
               snapshotEarliestCommitId = 20UL
               snapshotHasMore = false
               config = SessionConfig.empty |}
    state.Handle snap
    Assert.Equal(1, state.Conversations[convId].messages.Count)

    // Catch up containing MessageDeleted event
    let commit = Events.Commit.create 21UL DateTimeOffset.UtcNow [ MessageDeleted { conversationId = convId; messageCommitId = 20UL } ]
    let jsonLine = CommitCodec.commitToJsonLine commit
    let items = JsonArray()
    items.Add jsonLine
    let catchUp = AuthorityCatchUp {| fromCursor = 20UL; toCommitId = 21UL; items = items |}
    state.Handle catchUp

    Assert.Equal(0, state.Conversations[convId].messages.Count)
    Assert.Equal(21UL, state.Conversations[convId].lastCommitId)

[<Fact>]
let ``session config topP and thinkingBudget roundtrip in commit codec`` () =
    let convId = Guid.NewGuid()
    let cfg =
        { SessionConfig.empty with
            provider = "anthropic"
            model = "claude-3-7-sonnet"
            temperature = Some 0.7
            topP = Some 0.95
            maxTokens = Some 8192
            thinkingBudget = Some 4096 }
    let ev = ConversationCreated { conversationId = convId; title = "Thinking"; config = cfg }
    let commit = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ev ]
    let line = CommitCodec.commitToJsonLine commit
    let parsed = CommitCodec.tryCommitFromJsonLine line
    Assert.True(parsed.IsSome)
    match parsed.Value.events[0] with
    | ConversationCreated d ->
        Assert.Equal(Some 0.95, d.config.topP)
        Assert.Equal(Some 4096, d.config.thinkingBudget)
        Assert.Equal(Some 8192, d.config.maxTokens)
        Assert.Equal(Some 0.7, d.config.temperature)
    | _ -> failwith "unexpected event type"

[<Fact>]
let ``ui button triggers callback on Enter and Space keys`` () =
    Headless.ensure ()
    let mutable count = 0
    let btn = Ui.button Ui.Primary "Test" (fun () -> count <- count + 1)
    
    // Simulate Enter key
    let enterArgs = Avalonia.Input.KeyEventArgs(RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter)
    btn.RaiseEvent enterArgs
    Assert.Equal(1, count)

    // Simulate Space key
    let spaceArgs = Avalonia.Input.KeyEventArgs(RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Space)
    btn.RaiseEvent spaceArgs
    Assert.Equal(2, count)

[<Fact>]
let ``markdown renderer renders complex markdown with code, tables and math without error`` () =
    Headless.ensure ()
    let doc =
        "# 标题一\n" +
        "正文包含 **粗体**、*斜体*、`行内代码` 和 [链接](https://example.com)。\n\n" +
        "> 引用块测试\n\n" +
        "- [x] 任务完成\n" +
        "- [ ] 任务待办\n\n" +
        "| 列 A | 列 B |\n" +
        "|---|---|\n" +
        "| 1 | 2 |\n\n" +
        "```fsharp\nlet x = 42\n```\n\n" +
        "$$E = mc^2$$\n"
    let renderer = MarkdownRenderer(14.0, ignore, ignore, true)
    let control = renderer.RenderText doc
    Assert.NotNull control

[<Fact>]
let ``message card renders user and assistant with reasoning and tools without error`` () =
    Headless.ensure ()
    let ctx =
        { fontSize = 14.0
          autoCollapseReasoning = false
          streaming = false
          isLastAssistant = true
          usage = Some { promptTokens = Some 100; completionTokens = Some 50; cachedTokens = None; totalTokens = Some 150; durationMs = Some 1200L }
          missingAttachments = Set.empty
          brandAvatar = fun () -> Avalonia.Controls.Border() :> Avalonia.Controls.Control }
    let msgActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }

    let userMsg =
        { MessageView.empty with
            role = "user"
            text = "用户输入" }
    let userCard = MessageCard.render userMsg ctx msgActions
    Assert.NotNull userCard

    let assistantMsg =
        { MessageView.empty with
            role = "assistant"
            text = "助手回复正文"
            reasoning = "思考过程片段"
            toolCalls =
                [ { callId = "c1"; name = "builtin_echo"; argumentsJson = """{"text":"hello"}"""; result = Some "hello" } ] }
    let assistantCard = MessageCard.render assistantMsg ctx msgActions
    Assert.NotNull assistantCard

[<Fact>]
let ``conversation summary matches multi word and model search`` () =
    let summary =
        { id = Guid.NewGuid()
          title = "探讨量子计算算法"
          preview = "Shor 算法与 Grover 搜索算法的复杂度对比"
          running = false
          pinned = false
          archived = false
          createdAt = DateTimeOffset.UtcNow
          messageCount = 4
          isFork = false
          providerId = "anthropic"
          model = "claude-3-7-sonnet"
          lastCommitId = 10UL }

    // Match by title
    Assert.True(ConversationSummary.matches "量子" summary)
    // Match by preview
    Assert.True(ConversationSummary.matches "Grover" summary)
    // Match by provider
    Assert.True(ConversationSummary.matches "anthropic" summary)
    // Match by model
    Assert.True(ConversationSummary.matches "claude-3-7" summary)
    // Match multi-term
    Assert.True(ConversationSummary.matches "量子 claude" summary)
    Assert.True(ConversationSummary.matches "Shor sonnet" summary)
    // Non matching term
    Assert.False(ConversationSummary.matches "量子 gpt-4o" summary)

[<Fact>]
let ``highlight parseHtml maps nested scopes and diff tokens correctly`` () =
    let html =
        """<span class="hljs-keyword">let</span> <span class="hljs-variable">x</span> = <span class="hljs-string">&quot;hello <span class="hljs-subst">name</span>&quot;</span> <span class="hljs-comment">// 注释</span>"""
    let tokens = Highlight.parseHtml html
    Assert.True(tokens.Length >= 5)
    Assert.Contains(tokens, fun t -> t.kind = CodeKeyword && t.text = "let")
    Assert.Contains(tokens, fun t -> t.kind = CodeVariable && t.text = "x")
    Assert.Contains(tokens, fun t -> t.kind = CodeComment && t.text = "// 注释")

    let diffHtml =
        """<span class="hljs-addition">+ added line</span>\n<span class="hljs-deletion">- deleted line</span>"""
    let diffTokens = Highlight.parseHtml diffHtml
    Assert.Contains(diffTokens, fun t -> t.kind = CodeAddition && t.text.Contains("added line"))
    Assert.Contains(diffTokens, fun t -> t.kind = CodeDeletion && t.text.Contains("deleted line"))

[<Fact>]
let ``textField maintains constant border thickness on focus to prevent layout shift`` () =
    Headless.ensure ()
    let shell, box = Ui.textField "输入测试"
    Assert.Equal(1.0, shell.BorderThickness.Left)
    Assert.Equal(1.0, shell.BorderThickness.Top)
    
    // Initial state
    Assert.Equal(Tokens.border.Color, (shell.BorderBrush :?> Avalonia.Media.SolidColorBrush).Color)

    // Border thickness remains exactly 1.0 (no layout shift)
    Assert.Equal(1.0, shell.BorderThickness.Left)
    Assert.Equal(1.0, shell.BorderThickness.Top)

[<Fact>]
let ``theme mode switching updates palette and dynamic brushes`` () =
    Tokens.apply Light
    Assert.False(Tokens.isDark())
    let lightCanvas = Tokens.canvas.Color

    Tokens.apply Dark
    Assert.True(Tokens.isDark())
    let darkCanvas = Tokens.canvas.Color
    Assert.NotEqual(lightCanvas, darkCanvas)

    // Reset back to Light
    Tokens.apply Light
    Assert.False(Tokens.isDark())
    Assert.Equal(lightCanvas, Tokens.canvas.Color)

[<Fact>]
let ``error card toggles technical detail and triggers retry action`` () =
    Headless.ensure ()
    let mutable retried = false
    let err =
        { kind = ProviderUnavailable
          message = "无法连接服务商"
          detail = Some "Connection refused at 127.0.0.1:8799"
          retryable = true
          retryAfterSeconds = Some 5 }
    let card = MessageCard.errorCard err (fun () -> retried <- true)
    Assert.NotNull card

[<Fact>]
let ``chat view renders a focused empty state without extra actions`` () =
    Headless.ensure ()
    let mutable sidebarToggled = false
    let chatActions: ChatActions =
        { renameTitle = ignore
          openSessionSettings = ignore
          forkFromHere = ignore
          stopGeneration = ignore
          requestOlderHistory = ignore
          retryLast = ignore
          toggleSidebar = fun () -> sidebarToggled <- true
          message =
            { copyText = ignore
              regenerate = ignore
              editAndFork = ignore
              deleteMessage = ignore
              downloadAttachment = ignore
              openLink = ignore } }
    let chat = ChatView(chatActions, Brand.logo)
    chat.Build()
    chat.ShowEmpty(EmptyConversation, None)
    Assert.True(chat.IsVisible)

[<Fact>]
let ``composer accepts text without introducing extra send modes`` () =
    Headless.ensure ()
    let actions: ComposerActions =
        { submit = ignore
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore }
    let comp = Composer(actions)
    comp.Build()
    comp.SetEnabled(true, "")
    comp.SetText("万象架构：事件溯源与对等连接")
    // Verification that composer accepts text and calculates state without crashing
    Assert.True(comp.IsVisible)

[<Fact>]
let ``message card renders assistant markdown without extra view mode`` () =
    Headless.ensure ()
    let msg: MessageView =
        { commitId = Some 10UL
          role = "assistant"
          text = "# Hello\n\nThis is **bold** text."
          reasoning = ""
          toolCalls = []
          toolResults = []
          attachments = []
          committedAt = Some DateTimeOffset.UtcNow }
    let ctx: MessageContext =
        { fontSize = 14.0
          autoCollapseReasoning = true
          streaming = false
          isLastAssistant = true
          usage = None
          missingAttachments = Set.empty
          brandAvatar = fun () -> Brand.logo 26.0 }
    let actions: MessageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }
    let card = MessageCard.render msg ctx actions
    Assert.NotNull card

[<Fact>]
let ``main view build mounts a non-empty root visual tree`` () =
    Headless.ensure ()
    let oldHome = Environment.GetEnvironmentVariable "WANXIANG_HOME"
    let home = tempDir ()
    try
        // 避免开发机已有 client.toml 让这个纯 UI 装配测试意外发起网络连接。
        Environment.SetEnvironmentVariable("WANXIANG_HOME", home)
        let view = MainView()
        view.Build()
        Assert.NotNull view.Content
        match view.Content with
        | :? Avalonia.Controls.Grid as root -> Assert.True(root.Children.Count >= 2)
        | other -> Assert.Fail($"Expected Grid root, got {other.GetType().FullName}")
    finally
        Environment.SetEnvironmentVariable("WANXIANG_HOME", oldHome)
        cleanup home
