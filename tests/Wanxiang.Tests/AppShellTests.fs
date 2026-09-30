namespace Wanxiang.Tests

open System
open System.Reflection
open System.Text.Json.Nodes
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Xunit
open Wanxiang.Client
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.UI

module AppShellTests =

    let private handleEventMethod =
        lazy
            (typeof<MainView>.GetMethod("HandleEvent", BindingFlags.NonPublic ||| BindingFlags.Instance))

    let private dispatchEvent (view: MainView) (ev: WireEvent) =
        handleEventMethod.Value.Invoke(view, [| box ev |]) |> ignore

    let private getField<'T> (view: MainView) (fieldName: string) : 'T =
        let field = typeof<MainView>.GetField(fieldName, BindingFlags.NonPublic ||| BindingFlags.Instance)
        field.GetValue(view) :?> 'T

    let private setField<'T> (view: MainView) (fieldName: string) (value: 'T) =
        let field = typeof<MainView>.GetField(fieldName, BindingFlags.NonPublic ||| BindingFlags.Instance)
        field.SetValue(view, box value)

    let private invokeMethod<'T> (view: MainView) (methodName: string) (args: obj array) : 'T =
        let m = typeof<MainView>.GetMethod(methodName, BindingFlags.NonPublic ||| BindingFlags.Instance)
        m.Invoke(view, args) :?> 'T

    /// 构建并挂载完整的 MainView 视觉树。
    /// MainView 的子控件 (sidebar, chat, composer, settings, mainLayout 等) 均由 Build() 初始化；
    /// 放入 Window 并调用 Show() 确保 TopLevel 及焦点管理器就绪，杜绝裸测环境下的 NullReferenceException。
    let private createShell () =
        MotionPolicy.setReduced true
        let shell = MainView()
        shell.Build()
        let window = Window(Width = 1000.0, Height = 800.0, Content = shell)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        shell, window

    let private createSummary (id: Guid) (title: string) : ConversationSummary =
        { id = id
          title = title
          preview = ""
          running = false
          pinned = false
          archived = false
          createdAt = DateTimeOffset.UtcNow
          updatedAt = DateTimeOffset.UtcNow
          messageCount = 0
          isFork = false
          providerId = "openai"
          model = "gpt-4o"
          lastCommitId = 0UL }

    let private createSummaryWithTime (id: Guid) (title: string) (updatedAt: DateTimeOffset) : ConversationSummary =
        { id = id
          title = title
          preview = ""
          running = false
          pinned = false
          archived = false
          createdAt = updatedAt
          updatedAt = updatedAt
          messageCount = 0
          isFork = false
          providerId = "openai"
          model = "gpt-4o"
          lastCommitId = 0UL }

    // ==========================================
    // 1. 主题与偏好变更传播 (Theme & Prefs)
    // ==========================================

    [<Fact>]
    let ``SavePrefs cycles theme and updates tokens and renderer settings`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let initialPrefs: UiPrefs = getField view "prefs"

            // 切换主题：从 FollowSystem -> AlwaysLight -> AlwaysDark -> FollowSystem
            let nextPrefs = { initialPrefs with theme = AlwaysLight; codeWrap = true; reduceMotion = true }
            invokeMethod<unit> view "SavePrefs" [| box nextPrefs |]

            let updated: UiPrefs = getField view "prefs"
            Assert.Equal(AlwaysLight, updated.theme)
            Assert.True(updated.codeWrap)
            Assert.True(updated.reduceMotion)
            Assert.True(MarkdownRenderer.DefaultCodeWrap)
        )

    [<Fact>]
    let ``HandleShortcut ToggleTheme cycles through FollowSystem, AlwaysLight and AlwaysDark`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            // 确保起始状态是 FollowSystem
            let initialPrefs: UiPrefs = getField view "prefs"
            invokeMethod<unit> view "SavePrefs" [| box { initialPrefs with theme = FollowSystem } |]

            // 1. 第一次 ToggleTheme (Ctrl+Shift+S): FollowSystem -> AlwaysLight
            let keyEvent1 = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.S, KeyModifiers = (KeyModifiers.Control ||| KeyModifiers.Shift))
            view.HandleShortcut keyEvent1
            Assert.True(keyEvent1.Handled)
            let prefs1: UiPrefs = getField view "prefs"
            Assert.Equal(AlwaysLight, prefs1.theme)

            // 2. 第二次 ToggleTheme (Ctrl+Shift+S): AlwaysLight -> AlwaysDark
            let keyEvent2 = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.S, KeyModifiers = (KeyModifiers.Control ||| KeyModifiers.Shift))
            view.HandleShortcut keyEvent2
            Assert.True(keyEvent2.Handled)
            let prefs2: UiPrefs = getField view "prefs"
            Assert.Equal(AlwaysDark, prefs2.theme)

            // 3. 第三次 ToggleTheme (Ctrl+Shift+S): AlwaysDark -> FollowSystem
            let keyEvent3 = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.S, KeyModifiers = (KeyModifiers.Control ||| KeyModifiers.Shift))
            view.HandleShortcut keyEvent3
            Assert.True(keyEvent3.Handled)
            let prefs3: UiPrefs = getField view "prefs"
            Assert.Equal(FollowSystem, prefs3.theme)
        )

    // ==========================================
    // 2. 侧边栏展开与收起状态折叠 (Sidebar Toggling)
    // ==========================================

    [<Fact>]
    let ``ToggleSidebar toggles sidebarCollapsed and persists into prefs`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let initialPrefs: UiPrefs = getField view "prefs"
            let initialCollapsed = initialPrefs.sidebarCollapsed

            view.ToggleSidebar()
            let toggledPrefs: UiPrefs = getField view "prefs"
            Assert.Equal(not initialCollapsed, toggledPrefs.sidebarCollapsed)

            view.ToggleSidebar()
            let restoredPrefs: UiPrefs = getField view "prefs"
            Assert.Equal(initialCollapsed, restoredPrefs.sidebarCollapsed)
        )

    [<Fact>]
    let ``HandleShortcut ToggleSidebar triggers sidebar collapse toggle`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let initialPrefs: UiPrefs = getField view "prefs"
            let initialCollapsed = initialPrefs.sidebarCollapsed

            // Ctrl+B 触发 ToggleSidebar
            let keyEvent = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.B, KeyModifiers = KeyModifiers.Control)
            view.HandleShortcut keyEvent
            Assert.True(keyEvent.Handled)

            let toggledPrefs: UiPrefs = getField view "prefs"
            Assert.Equal(not initialCollapsed, toggledPrefs.sidebarCollapsed)
        )

    // ==========================================
    // 3. 快捷键分发逻辑 (Shortcuts Routing)
    // ==========================================

    [<Fact>]
    let ``HandleShortcut OpenSettings shows settingsHost and hides workspace`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let settingsHost: Avalonia.Controls.Grid = getField view "settingsHost"
            let workspace: Avalonia.Controls.Grid = getField view "workspace"

            Assert.False(settingsHost.IsVisible)
            Assert.True(workspace.IsVisible)

            // Ctrl+, 打开设置
            let keyEvent = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemComma, KeyModifiers = KeyModifiers.Control)
            view.HandleShortcut keyEvent
            Assert.True(keyEvent.Handled)

            Assert.True(settingsHost.IsVisible)
            Assert.False(workspace.IsVisible)

            // Escape 关闭设置
            let escEvent = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, KeyModifiers = KeyModifiers.None)
            view.HandleShortcut escEvent
            Assert.True(escEvent.Handled)

            Assert.False(settingsHost.IsVisible)
            Assert.True(workspace.IsVisible)
        )

    [<Fact>]
    let ``HandleShortcut FocusSearch ensures sidebar visible and focuses search`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            // 预设侧栏为收起
            let prefs: UiPrefs = getField view "prefs"
            invokeMethod<unit> view "SavePrefs" [| box { prefs with sidebarCollapsed = true } |]

            // Ctrl+K 触发搜索 (ShortcutRouter.resolve: Ctrl+K)
            let keyEvent = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.K, KeyModifiers = KeyModifiers.Control)
            view.HandleShortcut keyEvent
            Assert.True(keyEvent.Handled)

            let updatedPrefs: UiPrefs = getField view "prefs"
            Assert.False(updatedPrefs.sidebarCollapsed)
        )

    [<Fact>]
    let ``HandleShortcut StopGeneration when run is generating invokes generation cancellation`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let convId = Guid.NewGuid()
            let genId = Guid.NewGuid()
            setField view "activeConvId" (Some convId)

            let runs: ConversationRuns = getField view "runs"
            runs.Start(convId, genId)

            // 按 Escape 停止生成
            let escEvent = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, KeyModifiers = KeyModifiers.None)
            view.HandleShortcut escEvent
            Assert.True(escEvent.Handled)
        )

    [<Fact>]
    let ``HandleShortcut JumpExchange and Scroll dispatches smoothly without errors`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()

            // Ctrl+Up / Ctrl+Down 轮转段落 (ShortcutRouter: ctrl && not shift && e.Key = Key.Up/Down)
            let upEvent = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up, KeyModifiers = KeyModifiers.Control)
            view.HandleShortcut upEvent
            Assert.True(upEvent.Handled)

            let downEvent = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, KeyModifiers = KeyModifiers.Control)
            view.HandleShortcut downEvent
            Assert.True(downEvent.Handled)

            // Ctrl+Home / Ctrl+End 滚动 (ShortcutRouter: ctrl && not shift && e.Key = Key.Home/End)
            let homeEvent = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home, KeyModifiers = KeyModifiers.Control)
            view.HandleShortcut homeEvent
            Assert.True(homeEvent.Handled)

            let endEvent = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.End, KeyModifiers = KeyModifiers.Control)
            view.HandleShortcut endEvent
            Assert.True(endEvent.Handled)

            // FocusComposer (Ctrl+L)
            let focusEvent = KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.L, KeyModifiers = KeyModifiers.Control)
            view.HandleShortcut focusEvent
            Assert.True(focusEvent.Handled)
        )

    // ==========================================
    // 4. 断网、重连与状态机流转 (Connection & Protocol Events)
    // ==========================================

    [<Fact>]
    let ``HandleEvent AuthAccepted transitions authenticated to true and resets connection state`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let instId = "inst-" + Guid.NewGuid().ToString()

            let authAccepted = AuthAccepted {| instanceId = instId |}

            dispatchEvent view authAccepted

            let authenticated: bool = getField view "authenticated"
            let actualInstId: string = getField view "instanceId"
            let reconnectDelayMs: int = getField view "reconnectDelayMs"

            Assert.True(authenticated)
            Assert.Equal(instId, actualInstId)
            Assert.Equal(ReconnectBackoff.baseDelayMs, reconnectDelayMs)
        )

    [<Fact>]
    let ``HandleEvent AuthRejected transitions authenticated to false and resets token`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            setField view "authenticated" true
            setField view "lastToken" (Some "stale-token")

            let authRejected = AuthRejected {| reason = "token expired" |}

            dispatchEvent view authRejected

            let authenticated: bool = getField view "authenticated"
            let lastToken: string option = getField view "lastToken"

            Assert.False(authenticated)
            Assert.True(Option.isNone lastToken)
        )

    [<Fact>]
    let ``HandleEvent CatalogSnapshot parses providers and sets catalog`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let snapshot =
                CatalogSnapshot
                    {| providers = JsonArray()
                       tools = JsonArray()
                       generation = JsonObject() |}

            dispatchEvent view snapshot

            let catalog: Catalog = getField view "catalog"
            Assert.True(List.isEmpty catalog.providers)
        )

    [<Fact>]
    let ``HandleEvent GenerationStarted, GenerationDelta, and GenerationFinished flow`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let convId = Guid.NewGuid()
            let genId = Guid.NewGuid()
            setField view "activeConvId" (Some convId)

            // 1. GenerationStarted
            let startEv =
                GenerationStarted
                    {| conversationId = convId
                       generationId = genId
                       providerId = "openai"
                       model = "gpt-4o" |}
            dispatchEvent view startEv

            let runs: ConversationRuns = getField view "runs"
            Assert.True(runs.Get(Some convId).running)

            // 2. GenerationDelta
            let payload = JsonObject()
            payload["role"] <- "assistant"
            payload["text"] <- "Hello delta"
            let deltaEv =
                GenerationDelta
                    {| conversationId = convId
                       generationId = genId
                       payload = payload |}
            dispatchEvent view deltaEv

            Assert.Equal("Hello delta", runs.Get(Some convId).message.Value.text)

            // 3. GenerationFinished (completed)
            let finishEv =
                GenerationFinished
                    {| conversationId = convId
                       generationId = genId
                       status = "completed"
                       error = None
                       usage = None |}
            dispatchEvent view finishEv

            Assert.False(runs.Get(Some convId).running)
            Assert.True(Option.isNone (runs.Get(Some convId).error))
        )

    [<Fact>]
    let ``HandleEvent GenerationFinished with error preserves error in runs`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let convId = Guid.NewGuid()
            let genId = Guid.NewGuid()
            setField view "activeConvId" (Some convId)

            let runs: ConversationRuns = getField view "runs"
            runs.Start(convId, genId)

            let genError = GenerationError.create GenerationErrorKind.ProviderTimeout "Server timeout"
            let finishEv =
                GenerationFinished
                    {| conversationId = convId
                       generationId = genId
                       status = "failed"
                       error = Some genError
                       usage = None |}
            dispatchEvent view finishEv

            Assert.False(runs.Get(Some convId).running)
            Assert.True(runs.Get(Some convId).error.IsSome)
            Assert.Equal("Server timeout", runs.Get(Some convId).error.Value.message)
        )

    // ==========================================
    // 5. 命令反馈与拒绝处理 (Command Feedback & Rejection)
    // ==========================================

    [<Fact>]
    let ``HandleEvent CommandCommitted resolves feedback and commits outbox`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let outbox: MessageOutbox = getField view "outbox"
            let tracker: CommandFeedbackTracker = getField view "commandFeedback"

            let convId = Guid.NewGuid()
            let staged = outbox.Stage("inst1", convId, "Test message", [], None)
            let invId = PendingMessage.invocationId staged

            let mutable committed = false
            let mutable completed = false
            tracker.TrackWithCompletion(staged.command, Some "Success toast", (fun () -> committed <- true), (fun ok -> completed <- ok))

            let ev = CommandCommitted {| invocationId = invId; commandId = "cmd-1"; commitId = 1UL |}
            dispatchEvent view ev

            Assert.True(committed)
            Assert.True(completed)
            // CommandCommitted 会从 outbox 中移除该暂存项
            Assert.True(Option.isNone (outbox.TryFind invId))
        )

    [<Fact>]
    let ``HandleEvent CommandRejected marks outbox as rejected and informs feedback`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let outbox: MessageOutbox = getField view "outbox"
            let tracker: CommandFeedbackTracker = getField view "commandFeedback"

            let convId = Guid.NewGuid()
            let staged = outbox.Stage("inst1", convId, "Rejected message", [], None)
            let invId = PendingMessage.invocationId staged

            let mutable completed = true
            tracker.TrackWithCompletion(staged.command, None, ignore, (fun ok -> completed <- ok))

            let ev = CommandRejected {| invocationId = invId; code = "ERR_POLICY"; message = "Policy violation"; requiredCommitId = None |}
            dispatchEvent view ev

            Assert.False(completed)
            match (outbox.TryFind invId).Value.state with
            | RejectedMessage reason -> Assert.Equal("Policy violation", reason)
            | other -> Assert.Fail(sprintf "Expected RejectedMessage, got %A" other)
        )

    // ==========================================
    // 6. 会话选择、邻行计算与可见顺序 (Conversation Selection & Neighbor)
    // ==========================================

    [<Fact>]
    let ``NeighborExcluding returns next visible neighbor when active conversation is removed`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let t0 = DateTimeOffset.UtcNow
            let id1 = Guid.NewGuid()
            let id2 = Guid.NewGuid()
            let id3 = Guid.NewGuid()

            // 侧栏按 updatedAt 降序排列：t0 + 3分 (id3) 在最前，t0 + 2分 (id2) 居中，t0 + 1分 (id1) 在最后
            // 可见顺序为 [ id3; id2; id1 ]
            let summaries =
                [ createSummaryWithTime id1 "Conv 1" (t0.AddMinutes 1.0)
                  createSummaryWithTime id2 "Conv 2" (t0.AddMinutes 2.0)
                  createSummaryWithTime id3 "Conv 3" (t0.AddMinutes 3.0) ]

            let sidebar: Sidebar = getField view "sidebar"
            sidebar.SetConversations summaries
            setField view "summaries" summaries
            setField view "activeConvId" (Some id2)

            // 当移除当前会话 id2 时，从可见顺序自顶向下排除 id2，首个存活的可见会话为 id3
            let neighbor: Guid option = invokeMethod view "NeighborExcluding" [| box [ id2 ] |]
            Assert.True(neighbor.IsSome)
            Assert.Equal(id3, neighbor.Value)

            // 若同时移除 [ id3; id2 ]，则下一个可见会话落到 id1
            let neighbor2: Guid option = invokeMethod view "NeighborExcluding" [| box [ id3; id2 ] |]
            Assert.True(neighbor2.IsSome)
            Assert.Equal(id1, neighbor2.Value)

            // 若全部可见会话都被移除，则返回 None（主视图落回欢迎页）
            let noNeighborAll: Guid option = invokeMethod view "NeighborExcluding" [| box [ id1; id2; id3 ] |]
            Assert.True(noNeighborAll.IsNone)

            // 若当前未激活被删除的会话，则返回 None（不触碰主视图）
            setField view "activeConvId" (Some id1)
            let noNeighbor: Guid option = invokeMethod view "NeighborExcluding" [| box [ id3 ] |]
            Assert.True(noNeighbor.IsNone)
        )

    [<Fact>]
    let ``SelectConversation switches active conversation and sets composer text from draft`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let drafts: ComposerDrafts = getField view "drafts"
            let composer: Composer = getField view "composer"

            let convId1 = Guid.NewGuid()
            let convId2 = Guid.NewGuid()

            (drafts.Get("", Some convId2)).text <- "Draft for conv 2"

            invokeMethod<unit> view "SelectConversation" [| box (Some convId1) |]
            composer.SetText "Draft for conv 1"

            // 切换到 convId2
            invokeMethod<unit> view "SelectConversation" [| box (Some convId2) |]

            let activeId: Guid option = getField view "activeConvId"
            Assert.Equal(Some convId2, activeId)
            Assert.Equal("Draft for conv 2", composer.Text)

            // 切换回 convId1，保存的草稿应恢复
            invokeMethod<unit> view "SelectConversation" [| box (Some convId1) |]
            Assert.Equal("Draft for conv 1", composer.Text)
        )

    [<Fact>]
    let ``ServerError marking missing attachment updates missingAttachments set`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let sha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

            let ev = ServerError {| message = sprintf "attachment %s not found in store" sha |}
            dispatchEvent view ev

            let missing: Set<string> = getField view "missingAttachments"
            Assert.True(missing.Contains sha)
        )
