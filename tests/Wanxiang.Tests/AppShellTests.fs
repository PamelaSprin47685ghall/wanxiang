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

[<Trait("Category", "UI")>]
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

    [<Trait("Category", "UI")>]
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
            Assert.True(Reading.wrap.Wrapped)
        )

    // ==========================================
    // 2. 侧边栏展开与收起状态折叠 (Sidebar Toggling)
    // ==========================================

    [<Trait("Category", "UI")>]
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

    // ==========================================
    // 3. 快捷键分发逻辑 (Shortcuts Routing)
    // ==========================================

    // ==========================================
    // 4. 断网、重连与状态机流转 (Connection & Protocol Events)
    // ==========================================

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``HandleEvent AuthAccepted transitions authenticated to true and resets connection state`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let instId = "inst-" + Guid.NewGuid().ToString()

            let authAccepted = AuthAccepted {| instanceId = instId |}

            dispatchEvent view authAccepted

            let authenticated: bool = getField view "authenticated"
            let actualInstId: string = getField view "instanceId"
            let reconnectDelayMs = (getField<Wanxiang.Client.ReconnectLoop> view "reconnect").DelayMs

            Assert.True(authenticated)
            Assert.Equal(instId, actualInstId)
            Assert.Equal(ReconnectBackoff.baseDelayMs, reconnectDelayMs)
        )

    [<Trait("Category", "UI")>]
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

    [<Trait("Category", "UI")>]
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

    // ==========================================
    // 5. 命令反馈与拒绝处理 (Command Feedback & Rejection)
    // ==========================================

    // ==========================================
    // 6. 对话选择、邻行计算与可见顺序 (Conversation Selection & Neighbor)
    // ==========================================

    [<Trait("Category", "UI")>]
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

    [<Trait("Category", "UI")>]
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

    // ==========================================
    // 7. 历史分页功能门控 (History Paging Feature Gate)
    // ==========================================

    /// 决策 440 的反例：主机明确声明了功能表却不含 history-paging 时，
    /// RequestOlderHistory 必须早失败（不置 pageLoading、不记发出时刻），
    /// 而不是发出注定无回应的请求、空等超时。hostFeatures = None 照常放行。
    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``RequestOlderHistory gated when host declares features without history-paging`` () =
        Headless.run (fun () ->
            let view, _ = createShell ()
            let convId = Guid.NewGuid()

            // 经 ClientState 的公开行为填充一个「还有更早历史」的活动对话。
            let state: ClientState = getField view "state"
            state.Handle(
                ConversationSnapshot
                    {| conversationId = convId
                       title = "分页门控"
                       lastCommitId = 10UL
                       runtimeState = "idle"
                       generationId = None
                       messages = JsonArray()
                       snapshotEarliestCommitId = 5UL
                       snapshotHasMore = true
                       config = SessionConfig.empty |})
            setField view "activeConvId" (Some convId)

            // 主机声明了功能表但不含 history-paging：点击即被门控挡住。
            dispatchEvent view (HostCapabilities {| hostVersion = "old-host"; features = [ HostFeatures.Search ] |})
            invokeMethod<unit> view "RequestOlderHistory" [||]

            Assert.False(getField<bool> view "pageLoading")
            Assert.True(Option.isNone (getField<DateTimeOffset option> view "pageRequestedAt"))

            // hostFeatures = None（未声明功能表的旧主机）：照常放行，请求进入在途状态。
            setField view "hostFeatures" None
            invokeMethod<unit> view "RequestOlderHistory" [||]

            Assert.True(getField<bool> view "pageLoading")
            Assert.True(Option.isSome (getField<DateTimeOffset option> view "pageRequestedAt"))
        )
