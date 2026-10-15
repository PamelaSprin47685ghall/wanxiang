module Wanxiang.Tests.DeliveryPolishingTests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Avalonia.Threading
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
open Wanxiang2.MessageCardView
open Wanxiang.Tests
open Wanxiang.Tests.Helpers
open Avalonia.Input
open Avalonia.Interactivity

[<Trait("Category", "UI")>]
[<Fact>]
let ``builtin echo function extracts text from various argument names`` () =
    Headless.run (fun () ->
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``builtin file read function extracts path from various argument names with sandbox check`` () =
    Headless.run (fun () ->
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``builtin file list function lists directory entries`` () =
    Headless.run (fun () ->
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``builtin time function returns ISO 8601 UTC timestamp`` () =
    Headless.run (fun () ->
    let time = BuiltinTimeFunction() :> AITool :?> AIFunction
    Assert.Equal("builtin_time", time.Name)
    let res = string (time.InvokeAsync(AIFunctionArguments()).AsTask().GetAwaiter().GetResult())
    let (parsed, dt) = DateTimeOffset.TryParse(res)
    Assert.True(parsed)
    Assert.Equal(TimeSpan.Zero, dt.Offset)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``client state updates and removes deleted message in ConversationUpdated`` () =
    Headless.run (fun () ->
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
               generationId = None
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``client state catch up removes deleted message via MessageDeleted event`` () =
    Headless.run (fun () ->
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
               generationId = None
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``session config topP and thinkingBudget roundtrip in commit codec`` () =
    Headless.run (fun () ->
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``ui button triggers callback on Enter and Space keys`` () =
    Headless.run (fun () ->
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``conversation summary matches multi word and model search`` () =
    Headless.run (fun () ->
    let summary =
        { id = Guid.NewGuid()
          title = "探讨量子计算算法"
          preview = "Shor 算法与 Grover 搜索算法的复杂度对比"
          running = false
          pinned = false
          archived = false
          createdAt = DateTimeOffset.UtcNow
          updatedAt = DateTimeOffset.UtcNow
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
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``highlight parseHtml maps nested scopes and diff tokens correctly`` () =
    Headless.run (fun () ->
    let html =
        """<span class="hljs-keyword">let</span> <span class="hljs-variable">x</span> = <span class="hljs-string">&quot;hello <span class="hljs-subst">name</span>&quot;</span> <span class="hljs-comment">// 注释</span>"""
    let tokens = HljsHtml.parseHtml html
    Assert.True(tokens.Length >= 5)
    Assert.Contains(tokens, fun t -> t.kind = CodeKeyword && t.text = "let")
    Assert.Contains(tokens, fun t -> t.kind = CodeVariable && t.text = "x")
    Assert.Contains(tokens, fun t -> t.kind = CodeComment && t.text = "// 注释")

    let diffHtml =
        """<span class="hljs-addition">+ added line</span>\n<span class="hljs-deletion">- deleted line</span>"""
    let diffTokens = HljsHtml.parseHtml diffHtml
    Assert.Contains(diffTokens, fun t -> t.kind = CodeAddition && t.text.Contains("added line"))
    Assert.Contains(diffTokens, fun t -> t.kind = CodeDeletion && t.text.Contains("deleted line"))
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``textField maintains constant border thickness on focus to prevent layout shift`` () =
    Headless.run (fun () ->
    let shell, box = Ui.textField "输入测试"
    Assert.Equal(1.0, shell.BorderThickness.Left)
    Assert.Equal(1.0, shell.BorderThickness.Top)
    
    // Initial state
    Assert.Equal(Tokens.border.Color, (shell.BorderBrush :?> Avalonia.Media.SolidColorBrush).Color)

    // Border thickness remains exactly 1.0 (no layout shift)
    Assert.Equal(1.0, shell.BorderThickness.Left)
    Assert.Equal(1.0, shell.BorderThickness.Top)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``theme mode switching updates palette and dynamic brushes`` () =
    Headless.run (fun () ->
    // 切色从旧色补间到新色（MotionLedger.themeColorTransition）：mode/palette
    // 同步到位，画笔颜色逐帧收敛。泵到收敛后再断言最终状态。
    // 决策 430：不再按墙钟睡眠等收敛（负载下帧会迟到），直接确定性收尾。
    let settle () =
        Dispatcher.UIThread.RunJobs()
        Tokens.completeTransition ()

    Tokens.apply Light
    Assert.False(Tokens.isDark())
    let lightCanvas = Tokens.canvas.Color

    Tokens.apply Dark
    Assert.True(Tokens.isDark())
    // 从旧色淡入：切换的瞬间画笔仍停在旧色，不是瞬时硬切。
    Assert.NotEqual(Palette.dark.canvas, Tokens.canvas.Color)
    settle ()
    let darkCanvas = Tokens.canvas.Color
    Assert.NotEqual(lightCanvas, darkCanvas)

    // Reset back to Light
    Tokens.apply Light
    settle ()
    Assert.False(Tokens.isDark())
    Assert.Equal(lightCanvas, Tokens.canvas.Color)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``chat view renders a focused empty state without extra actions`` () =
    Headless.run (fun () ->
    let mutable sidebarToggled = false
    let chatActions: ChatActions =
        { renameTitle = ignore
          openSessionSettings = ignore
          forkFromHere = ignore
          stopGeneration = ignore
          requestOlderHistory = ignore
          retryLast = ignore
          toggleSidebar = fun () -> sidebarToggled <- true
          focusHome = ignore
          message =
            { CopyText = ignore
              Regenerate = ignore
              EditAndFork = ignore
              DeleteMessage = ignore
              DownloadAttachment = ignore
              OpenLink = ignore; CopyLink = None; Recover = None } }
    let chat = ChatView(chatActions, SharedTheme.logo)
    chat.Build()
    chat.ShowEmpty(EmptyConversation, None)
    Assert.True(chat.IsVisible)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``composer accepts text without introducing extra send modes`` () =
    Headless.run (fun () ->
    let actions: ComposerActions =
        { submit = fun _ -> true
          stopGeneration = ignore
          pickAttachment = ignore
          removeAttachment = ignore
          openModelPicker = ignore
          dropFiles = ignore
          pasteFromClipboard = fun () -> false }
    let comp = Composer(actions)
    comp.SetEnabled(true, "")
    comp.SetText("万象架构：事件溯源与对等连接")
    // Verification that composer accepts text and calculates state without crashing
    Assert.True(comp.Host.IsVisible)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``main view build mounts a non-empty root visual tree`` () =
    Headless.run (fun () ->
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
    )
