namespace Wanxiang.UI

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Platform
open Avalonia.Platform.Storage
open Avalonia.Threading
open Wanxiang.Client
open Wanxiang.Core
open Wanxiang.Interop
open Wanxiang.Protocol

/// 主视图：应用的组装点。
///
/// 桌面窗口与 PWA 共用这一个 `UserControl`（决策 48），
/// 本机连接同样走 loopback WebSocket，不存在绕过协议的第二套行为路径。
type MainView() as this =
    inherit UserControl()

    let client = WsClient()
    let state = ClientState()

    let root = Grid()
    let overlay = OverlayHost(root)

    let mutable prefs = UiPrefs.load ()
    // 启动就把折行默认值交给渲染器：它是渲染期读的静态值，不走构造参数
    do
        MarkdownRenderer.DefaultCodeWrap <- prefs.codeWrap
        MotionPolicy.setReduced prefs.reduceMotion
    let mutable catalog = Catalog.empty
    let mutable authenticated = false
    let mutable instanceId = ""

    let mutable activeConvId: Guid option = None
    let runs = ConversationRuns()
    let mutable summaries: ConversationSummary list = []

    let drafts = ComposerDrafts()
    let outbox = MessageOutbox()
    let attachmentDraft () = (drafts.Get(instanceId, activeConvId)).attachments
    let downloadBuffers = System.Collections.Generic.Dictionary<string, MemoryStream>()
    let mutable downloadMeta: Map<string, string> = Map.empty
    let mutable missingAttachments: Set<string> = Set.empty

    let mutable lastUrl = CredentialStore.defaultServerUrl ()
    let mutable lastToken: string option = None
    let mutable reconnectCts: CancellationTokenSource option = None
    let mutable reconnectDelayMs = ReconnectBackoff.baseDelayMs
    let mutable pairingRequested = false
    let mutable connectAttempt = 0
    let mutable setConnectStatus: string -> unit = ignore
    let mutable pageLoading = false
    /// 设置关闭后焦点的回家点：打开设置时的触发控件（设置入口按钮等）。只记一个元素，
    /// 不经过 OverlayHost 的对话框记忆（对话框已有唯一的记忆路径，这里是视图显隐切换）。
    let mutable settingsReturnFocus: Control option = None
    /// 会话快照等待中的骨架屏延时器：300ms 内到达就不挂任何 loading chrome。
    let mutable skeletonTimer: DispatcherTimer option = None
    /// 骨架屏延时器已为哪个会话武装：Render 会被后台事件反复触发，不得重启它，一会话只武装一次（D1）。
    let mutable skeletonArmedFor: Guid option = None
    /// 浏览器宿主的 CSS 像素视口宽（`wxViewportWidth` 轮询写入）。
    /// Browser 后端高 DPR 下 `Bounds.Width` 会被 DPR 除一次，不可做断点依据；
    /// 有覆盖值时响应式布局只认它，桌面/测试走 Bounds 原路径。
    let mutable hostViewportWidth: float option = None
    let commandFeedback = CommandFeedbackTracker()
    let configFeedback = ConfigFeedbackTracker()
    let mutable exportDialog: ConversationExportDialog option = None

    let toast message tone = overlay.Toast(message, tone)
    let topLevel () = TopLevel.GetTopLevel this

    let copyToClipboard (text: string) =
        try
            match topLevel () with
            | null -> ()
            | top ->
                top.Clipboard.SetTextAsync text |> ignore
                toast "已复制到剪贴板" Success
        with _ -> ()

    let openLink (url: string) =
        try
            match topLevel () with
            | null -> ()
            | top -> top.Launcher.LaunchUriAsync(Uri url) |> ignore
        with _ ->
            toast (sprintf "无法打开链接：%s" url) Warning

    // ---- 视图 ----
    let mutable sidebar: Sidebar = Unchecked.defaultof<Sidebar>
    let mutable chat: ChatView = Unchecked.defaultof<ChatView>
    let mutable composer: Composer = Unchecked.defaultof<Composer>
    let mutable settings: SettingsView = Unchecked.defaultof<SettingsView>
    let mutable sidebarSplitter: GridSplitter = Unchecked.defaultof<GridSplitter>
    let mutable mainLayout: MainLayoutController = Unchecked.defaultof<MainLayoutController>
    let navigation = NavigationController(prefs.sidebarCollapsed)
    let workspace = Grid()
    let settingsHost = Grid(IsVisible = false)
    let mutable shellGrid: Grid = Unchecked.defaultof<Grid>
    let mutable chatColumn: DockPanel = Unchecked.defaultof<DockPanel>

    let send (ev: WireEvent) = client.SendAsync ev |> ignore
    let sendCommand (cmd: ClientCommand) =
        let epoch = client.ConnectionGeneration
        // 在 UI 操作发生时就进入发送队列，不能让线程池调度改变消息顺序。
        let sending = client.TrySendCommandAtGenerationAsync(epoch, cmd)
        async {
            let! sent = sending |> Async.AwaitTask
            if not sent then
                Dispatcher.UIThread.Post(fun () ->
                    if epoch = client.ConnectionGeneration then
                        let id = ClientCommand.invocationId cmd
                        commandFeedback.Reject id |> Option.iter (fun feedback -> feedback.onCompleted false)
                        outbox.SetState(id, UnconfirmedMessage "连接已断开；内容已保留，可重试。")
                        outbox.RejectCreation(id, "连接已断开；内容已保留，可重试。")
                        this.Render())
        } |> Async.Start
    let sendCommandWithFeedback (cmd: ClientCommand) (successMessage: string option) (onCommitted: unit -> unit) =
        commandFeedback.Track(cmd, successMessage, onCommitted)
        sendCommand cmd
    let sendCommandWithCompletion
        (cmd: ClientCommand)
        (successMessage: string option)
        (onCommitted: unit -> unit)
        (onCompleted: bool -> unit)
        =
        commandFeedback.TrackWithCompletion(cmd, successMessage, onCommitted, onCompleted)
        sendCommand cmd
    let newInvocation () = Guid.CreateVersion7()
    let sendConfigWithFeedback (successMessage: string) (eventOf: Guid -> WireEvent) (onCompleted: bool -> unit) =
        let requestId = newInvocation ()
        configFeedback.Track(requestId, successMessage, onCompleted)
        send (eventOf requestId)

    let activeConversation () =
        activeConvId |> Option.bind (fun id -> state.Conversations.TryFind id)

    let activeSummary () =
        activeConvId |> Option.bind (fun id -> summaries |> List.tryFind (fun s -> s.id = id))

    let activeConfig () =
        activeConversation () |> Option.map (fun view -> view.config) |> Option.defaultValue SessionConfig.empty

    let messagesOf (view: ConversationView) =
        view.messages |> Seq.cast<JsonNode> |> Seq.map MessageView.ofSnapshotItem |> List.ofSeq

    member private this.SetCompactNavigation(opened: bool) =
        let before = navigation.State
        let state = navigation.SetCompactNavigation opened
        if state.compactMode && not (isNull (box mainLayout)) then mainLayout.Apply state
        // compact 抽屉的焦点归宿：打开进抽屉（搜索框），收起回输入区（D3/D4）。
        if state.compactMode then
            if opened then sidebar.FocusSearch()
            elif before.compactNavigationOpen then composer.Focus()

    member private this.ApplyResponsiveLayout(width: float) =
        let effective = hostViewportWidth |> Option.defaultValue width
        if effective > 0.0 && not (isNull (box mainLayout)) then
            let needsApply, state = navigation.ApplyViewport(effective, activeConvId.IsSome)
            if needsApply then mainLayout.Apply state

    /// 浏览器 CSS 视口轮询：zoom、DPR、显示器移动都不会可靠触发 Bounds 更新，
    /// 但 innerWidth 永远是真相。桌面端不调用（保留 Bounds 路径）。
    member private this.WatchHostViewport() =
        if OperatingSystem.IsBrowser() then
            let timer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 500.0)
            timer.Tick.Add(fun _ ->
                try
                    let css = BrowserBridge.ViewportWidth()
                    if css > 0.0 && hostViewportWidth <> Some css then
                        hostViewportWidth <- Some css
                        this.ApplyResponsiveLayout css
                with _ -> ())
            timer.Start()

    member private _.StreamingMessage() : MessageView option =
        (runs.Get activeConvId).message
    /// 会话快照到达前的本地 loading：300ms 内不挂任何 chrome，超时才挂骨架 + 标题。
    /// 快照先到则定时器自证过期，不做任何事。
    member private this.ShowConversationLoadingDeferred(convId: Guid) =
        // 一会话只武装一次：后台事件触发的 Render 若重启计时器，骨架屏会被无限顺延（D1）。
        if skeletonArmedFor <> Some convId then
            skeletonArmedFor <- Some convId
            match skeletonTimer with
            | Some timer -> timer.Stop()
            | None -> ()
            let timer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 300.0)
            timer.Tick.Add(fun _ ->
                timer.Stop()
                if activeConvId = Some convId && (activeConversation ()).IsNone then
                    chat.SetTitle("加载中…", false)
                    chat.ShowSkeletonLoading())
            skeletonTimer <- Some timer
            timer.Start()

    /// 快照到达或切换会话：解除骨架屏武装并停表。
    member private _.DisarmSkeletonLoading() =
        match skeletonTimer with
        | Some timer -> timer.Stop()
        | None -> ()
        skeletonTimer <- None
        skeletonArmedFor <- None

    /// 断线时把所有上传中的附件标成失败：底层流已死，不会再有 Complete/Abort（C4）。
    member private this.FailUploadingAttachments() =
        for draft in drafts.All do
            draft.attachments.MarkAllUploadingAsFailed() |> ignore
        composer.SetAttachments((attachmentDraft ()).Items)

    member private this.Render() =
        let run = runs.Get activeConvId
        let generating = run.running
        match activeConvId, activeConversation () with
        | None, _ ->
            chat.SetConversationChrome false
            chat.SetTitle("", false)
            if not authenticated then
                chat.ShowEmpty(NotConnected, Some("连接服务器", fun () -> this.ShowConnectDialog()))
                composer.SetEnabled(false, "连接服务器后即可开始对话。")
                composer.SetModelLabel "连接已断开"
            elif not (Catalog.isReady catalog) then
                chat.ShowEmpty(NoProvider, Some("打开设置添加服务商", fun () -> this.ShowSettings()))
                composer.SetEnabled(false, "还没有可用的模型，先在设置里添加一个服务商。")
                composer.SetModelLabel "未配置模型"
            else
                // 输入区保持可用：直接打字就会自动建会话再发出，
                // 不必先点一次「新建」。空态里的按钮只是另一条同样有效的路径。
                chat.ShowEmpty(NoConversation, Some("新建会话", fun () -> this.CreateConversation() |> ignore))
                composer.SetEnabled(true, "")
                match Catalog.defaultSelection catalog with
                | Some(providerId, model) -> composer.SetModelLabel(Catalog.describeModel providerId model catalog)
                | None -> composer.SetModelLabel "未选择模型"
        | Some convId, None ->
            chat.SetConversationChrome true
            // Loading 分级：快照 300ms 内到达就不闪任何 loading chrome，超时才在本地挂骨架；
            // 流式文本本身就是进度，这里不叠加任何页级 loading。
            this.ShowConversationLoadingDeferred convId
            composer.SetEnabled(false, "正在加载会话…")
        | Some convId, Some view ->
            this.DisarmSkeletonLoading()
            let messages = messagesOf view
            let streaming = this.StreamingMessage()
            let summary = activeSummary ()
            chat.SetConversationChrome true
            chat.SetTitle((summary |> Option.map (fun s -> s.title) |> Option.defaultValue "会话"), true)
            composer.SetModelLabel(Catalog.describeModel view.config.provider view.config.model catalog)
            if List.isEmpty messages && streaming.IsNone && run.error.IsNone then
                chat.HideSkeletonLoading()
                chat.ShowEmpty(EmptyConversation, None)
            else
                chat.HideSkeletonLoading()
                chat.HideEmpty()
                let retry () = this.Regenerate()
                chat.RenderMessages(
                    messages,
                    streaming,
                    (run.error |> Option.map (fun e -> e, retry)),
                    prefs.fontScale,
                    prefs.autoCollapseReasoning,
                    run.usage,
                    missingAttachments)
            composer.SetEnabled(authenticated, (if authenticated then "" else "连接已断开，重新连接后可继续发送。"))
        composer.SetGenerating generating
        composer.SetCanStop(authenticated && run.generationId.IsSome)
        composer.SetAttachments((attachmentDraft ()).Items)
        composer.SetPendingMessages(
            outbox.ForInstance instanceId,
            activeConvId,
            authenticated,
            (fun id -> this.RetryPendingMessage id),
            (fun id ->
                this.SelectConversation(Some id)
                if outbox.Items(instanceId, Some id) |> List.forall _.creationConfirmed then
                    send (ObserveConversation {| conversationId = id |})))
        chat.SetGenerating(generating, (if generating then "生成中" else ""))
        chat.SetCanStop(authenticated && run.generationId.IsSome)

    /// 平台的深色偏好。桌面来自系统设置，浏览器来自 prefers-color-scheme。
    /// 不能用 Application.ActualThemeVariant——它被固定为 Light 以稳定 Fluent 模板。
    member private _.SystemPrefersDark() : bool =
        try
            match Application.Current with
            | null -> false
            | app ->
                match app.PlatformSettings with
                | null -> false
                | settings -> settings.GetColorValues().ThemeVariant = PlatformThemeVariant.Dark
        with _ ->
            false

    member private this.ApplyTheme() =
        Tokens.apply (UiPrefs.resolveTheme prefs (this.SystemPrefersDark()))

    member private this.SavePrefs(next: UiPrefs) =
        let themeChanged = next.theme <> prefs.theme
        let motionChanged = next.reduceMotion <> prefs.reduceMotion
        prefs <- next
        UiPrefs.save prefs
        if themeChanged then this.ApplyTheme()
        if motionChanged then MotionPolicy.setReduced prefs.reduceMotion
        sidebar.SetShowArchived prefs.showArchived
        // 代码块折行是渲染期读取的默认值，改完要立刻生效
        MarkdownRenderer.DefaultCodeWrap <- prefs.codeWrap
        composer.SetEnterSends prefs.enterSends
        settings.SetPrefs prefs
        this.Render()

    // ---- 连接 ----

    member private this.ShowConnectDialog() =
        setConnectStatus <-
            Dialogs.connect
                overlay
                lastUrl
                (fun request ->
                    lastUrl <- request.url
                    lastToken <- request.token
                    pairingRequested <- request.usePairing
                    // 用户手动发起：从最短等待重新开始
                    reconnectDelayMs <- ReconnectBackoff.baseDelayMs
                    this.Connect())
                (fun code -> send (PairingAttempted {| code = code; clientName = Some CredentialStore.clientName |}))

    member private this.Connect() =
        connectAttempt <- connectAttempt + 1
        let attempt = connectAttempt
        authenticated <- false
        runs.Disconnect()
        exportDialog |> Option.iter (fun dialog -> dialog.Disconnect())
        outbox.Disconnect instanceId
        commandFeedback.RejectAll()
        configFeedback.RejectAll()
        this.FailUploadingAttachments()
        this.Render()
        match reconnectCts with
        | Some cts -> cts.Cancel()
        | None -> ()
        reconnectCts <- None
        let url = lastUrl
        let token = lastToken
        let usePairing = pairingRequested
        let connecting =
            try client.ConnectWithGenerationAsync(Uri url, CancellationToken.None)
            with ex -> System.Threading.Tasks.Task.FromException<int> ex
        async {
            try
                let! epoch = connecting |> Async.AwaitTask
                let! helloSent = client.TrySendAtGenerationAsync(epoch, Hello {| protocol = "wanxiang"; version = Constants.ProtocolVersion; instanceId = None |}) |> Async.AwaitTask
                if helloSent then
                    match token with
                    | Some value ->
                        let! _ = client.TrySendAtGenerationAsync(epoch, AuthPresent {| token = value |}) |> Async.AwaitTask
                        ()
                    | None ->
                        if usePairing then
                            let! _ = client.TrySendAtGenerationAsync(epoch, PairingRequested {| clientName = Some CredentialStore.clientName |}) |> Async.AwaitTask
                            ()
                Dispatcher.UIThread.Post(fun () ->
                    if attempt = connectAttempt && not authenticated then sidebar.SetConnection(false, "正在连接"))
            with ex ->
                Dispatcher.UIThread.Post(fun () ->
                    if attempt = connectAttempt then
                        setConnectStatus (sprintf "连接已断开：%s" ex.Message)
                        sidebar.SetConnection(false, "连接已断开")
                        this.ScheduleReconnect())
        }
        |> Async.Start

    member private this.ScheduleReconnect() =
        match reconnectCts, lastToken with
        | Some _, _ -> ()
        | None, None -> ()
        | None, Some _ ->
            let cts = new CancellationTokenSource()
            reconnectCts <- Some cts
            let delay = reconnectDelayMs
            reconnectDelayMs <- ReconnectBackoff.next reconnectDelayMs
            // 连接状态一个词：倒计时不进壳状态栏，重连中只报“正在重连”。
            sidebar.SetConnection(false, "正在重连")
            async {
                do! Async.Sleep delay
                if not cts.IsCancellationRequested then
                    Dispatcher.UIThread.Post(fun () ->
                        if not cts.IsCancellationRequested then
                            reconnectCts <- None
                            pairingRequested <- false
                            this.Connect())
            }
            |> Async.Start

    // ---- 会话操作 ----

    member private this.SelectConversation(id: Guid option) =
        if activeConvId <> id then
            (drafts.Get(instanceId, activeConvId)).text <- composer.Text
            activeConvId <- id
            composer.SetText((drafts.Get(instanceId, id)).text)
            composer.SetAttachments((attachmentDraft ()).Items)
            chat.CancelHistoryPrependAnchor()
            pageLoading <- false
        this.DisarmSkeletonLoading()
        let navBefore = navigation.State
        this.SetCompactNavigation false
        sidebar.SetActive activeConvId
        this.Render()
        // compact 下收起导航会带走焦点（掉到页顶）：交还给输入区；阅读位置由 ChatView 的锚点语义保留。
        if navBefore.compactMode && navBefore.compactNavigationOpen then composer.Focus()

    member private _.MakeConversationCommand(conversationId: Guid) =
        let provider, model = Catalog.defaultSelection catalog |> Option.defaultValue ("", "")
        let config =
            { SessionConfig.empty with
                provider = provider; model = model
                instructions = catalog.generation.instructions
                temperature = catalog.generation.temperature
                topP = catalog.generation.topP
                maxTokens = catalog.generation.maxTokens }
        CreateConversation
            {| invocationId = newInvocation (); conversationId = conversationId; title = "新会话"; config = config |}

    member private this.CreateConversation() : Guid option =
        if not authenticated then
            toast "连接服务器后才能新建会话；草稿会保留。" Warning
            None
        elif not (Catalog.isReady catalog) then
            toast "先在设置里添加一个服务商。" Warning
            this.ShowSettings()
            None
        else
            let conversationId = Guid.CreateVersion7()
            this.SelectConversation(Some conversationId)
            sendCommandWithCompletion
                (this.MakeConversationCommand conversationId)
                None
                (fun () -> send (ObserveConversation {| conversationId = conversationId |}))
                (fun ok ->
                    if not ok && activeConvId = Some conversationId then this.SelectConversation None)
            // 新会话落定：焦点有家，直接进输入区（D3/D4）。
            composer.Focus()
            Some conversationId

    member private this.OpenConversation(id: Guid) =
        this.SelectConversation(Some id)
        send (ObserveConversation {| conversationId = id |})
        // 会话激活（键盘/可见顺序/分叉入口统一走这里）：焦点回家到输入区（D3/D4）。
        composer.Focus()

    member private this.DispatchPendingMessage(item: PendingMessage) =
        let id = PendingMessage.invocationId item
        if item.instanceId <> instanceId || not authenticated then
            outbox.SetState(id, UnconfirmedMessage "请连接原服务器后重试。")
        else
            match item.creation with
            | Some creation when not item.creationConfirmed ->
                outbox.SetState(id, PreparingConversation)
                sendCommandWithCompletion creation None
                    (fun () ->
                        outbox.ConversationReady(item.instanceId, item.conversationId)
                        send (ObserveConversation {| conversationId = item.conversationId |}))
                    (fun ok ->
                        if not ok then
                            outbox.SetState(id, UnconfirmedMessage "会话尚未确认创建；原文已保留。")
                            this.Render())
            | _ when not (state.Conversations.ContainsKey item.conversationId) ->
                outbox.SetState(id, PreparingConversation)
                send (ObserveConversation {| conversationId = item.conversationId |})
            | _ ->
                outbox.SetState(id, SendingMessage)
                sendCommand item.command

    member private this.RetryPendingMessage(id: Guid) =
        match outbox.TryFind id with
        | Some item when PendingMessage.canRetry item && item.instanceId = instanceId ->
            this.DispatchPendingMessage item
            this.Render()
            // 重试发出：错误行/重试按钮随状态机翻转，焦点先回家（D3/D4）。
            composer.Focus()
        | _ -> ()

    member private this.SendMessage(text: string) : bool =
        let draft = attachmentDraft ()
        // 失败态优先：失败的附件不会自动变好，继续报“上传中”等于指错路（C6）。
        if draft.HasFailed then
            toast "有上传失败的附件，请先移除再发送。" Warning
            false
        elif draft.HasUploading then
            toast "附件上传中，请等待上传完成后再发送。" Warning
            false
        elif not authenticated || not client.IsConnected then
            toast "连接已断开，草稿已保留。" Warning
            false
        elif String.IsNullOrWhiteSpace text && not draft.HasReady then false
        elif activeConvId.IsNone && not (Catalog.isReady catalog) then
            toast "还没有可用的模型，先添加一个服务商。" Warning
            false
        else
            let isNew = activeConvId.IsNone
            let id = activeConvId |> Option.defaultWith Guid.CreateVersion7
            let creation = if isNew then Some(this.MakeConversationCommand id) else None
            // 先建立完整的保留副本，再消费附件和清输入框。
            let item = outbox.Stage(instanceId, id, text, draft.Items, creation)
            draft.TryConsumeReady() |> ignore
            if isNew then
                this.SelectConversation(Some id)
                drafts.Move(instanceId, None, Some id)
                // SelectConversation 会载入新草稿，Submit 只清自己提交的文本。
                composer.SetText text
            runs.ClearError id
            this.DispatchPendingMessage item
            this.Render()
            true

    member private this.Regenerate() =
        match activeConvId with
        | None -> ()
        | Some convId ->
            runs.ClearError convId
            sendCommand (RegenerateResponse {| invocationId = newInvocation (); conversationId = convId |})
            this.Render()
            // 错误卡片随重试卸载：焦点无处可去会掉到页顶，先交还给输入区。
            composer.Focus()

    member private this.StopGeneration() =
        match activeConvId, (runs.Get activeConvId).generationId with
        | Some convId, Some generationId ->
            send (GenerationCancel {| conversationId = convId; generationId = generationId |})
            // 停止按钮随生成结束隐藏：同上，焦点先回家。
            composer.Focus()
        | _ -> ()

    member private this.ForkFrom(commitId: uint64 option, seedText: string) =
        match activeConvId, activeConversation () with
        | Some convId, Some view ->
            Dialogs.editText overlay "编辑并分叉" seedText "创建分叉" (fun edited ->
                let messages = messagesOf view
                let forkAfterId =
                    match commitId with
                    | Some target ->
                        messages
                        |> List.choose (fun m -> m.commitId)
                        |> List.filter (fun id -> id < target)
                        |> List.sortDescending
                        |> List.tryHead
                    | None -> messages |> List.choose (fun m -> m.commitId) |> List.sortDescending |> List.tryHead
                let editedMessage = JsonObject()
                editedMessage["role"] <- "user"
                let contents = JsonArray()
                let textContent = JsonObject()
                textContent["text"] <- edited
                contents.Add textContent
                editedMessage["contents"] <- contents
                let newId = Guid.CreateVersion7()
                let command =
                    ForkConversation
                        {| invocationId = newInvocation ()
                           conversationId = newId
                           parentConversationId = convId
                           forkAfterId = forkAfterId
                           config = SessionConfig.empty
                           editedMessageJson = editedMessage |}
                sendCommandWithFeedback command None (fun () ->
                    this.SelectConversation(Some newId)
                    send (ObserveConversation {| conversationId = newId |})))
        | _ -> toast "当前没有可分叉的会话。" Warning

    member private _.CurrentFocusControl() : Control option =
        try
            match TopLevel.GetTopLevel this with
            | null -> None
            | top ->
                match top.FocusManager with
                | null -> None
                | manager ->
                    match manager.GetFocusedElement() with
                    | :? Control as control -> Some control
                    | _ -> None
        with _ -> None

    /// 设置关闭回到设置入口触发点：触发控件仍在树上就还给它，否则落到输入区，绝不掉到页顶。
    member private this.RestoreSettingsFocus() =
        let fallback () =
            if workspace.IsVisible then composer.Focus()
        match settingsReturnFocus with
        | Some control ->
            settingsReturnFocus <- None
            Dispatcher.UIThread.Post(fun () ->
                try
                    if control.IsEnabled
                       && control.IsEffectivelyVisible
                       && not (isNull (TopLevel.GetTopLevel control)) then
                        control.Focus() |> ignore
                    else fallback ()
                with _ -> fallback ())
        | None -> fallback ()

    member private this.ShowSettings() =
        settingsReturnFocus <- this.CurrentFocusControl()
        settingsHost.IsVisible <- true
        workspace.IsVisible <- false
        send CatalogRequest

    member private this.CloseSettings() =
        settingsHost.IsVisible <- false
        workspace.IsVisible <- true
        this.RestoreSettingsFocus()

    member private this.ShowModelPicker(anchor: Control) =
        match activeConvId, activeConversation () with
        | Some convId, Some view ->
            let groups =
                [ for provider in Catalog.usableProviders catalog ->
                      provider.label,
                      [ for model in provider.models ->
                            MenuEntry.create model (fun () ->
                                let config = { view.config with provider = provider.id; model = model }
                                let command =
                                    UpdateConversationConfig
                                        {| invocationId = newInvocation ()
                                           conversationId = convId
                                           config = config |}
                                sendCommandWithFeedback command (Some(sprintf "已切换到 %s · %s" provider.label model)) (fun () ->
                                    view.config <- config
                                    this.Render()))
                            |> MenuEntry.markSelected (provider.id = view.config.provider && model = view.config.model) ] ]
            if List.isEmpty groups then
                toast "还没有可用的模型。" Warning
            else
                Menu.showGrouped overlay anchor false groups
        | _ -> toast "先选择一个会话。" Warning

    // ---- 附件 ----

    member private this.PickAttachment() =
        match topLevel () with
        | null -> ()
        | top ->
            let owner = attachmentDraft ()
            let ownerInstance = instanceId
            let ownerConnection = client.ConnectionGeneration
            async {
                try
                    let! picked =
                        top.StorageProvider.OpenFilePickerAsync(FilePickerOpenOptions(AllowMultiple = true))
                        |> Async.AwaitTask
                    for file in picked do
                        use! stream = file.OpenReadAsync() |> Async.AwaitTask
                        use buffer = new MemoryStream()
                        do! stream.CopyToAsync buffer |> Async.AwaitTask
                        let bytes = buffer.ToArray()
                        Dispatcher.UIThread.Post(fun () ->
                            if authenticated && instanceId = ownerInstance && client.ConnectionGeneration = ownerConnection then
                                this.BeginUpload(owner, file.Name, bytes)
                            else toast "连接已断开，请重新选择附件；文件没有发送到其他服务器。" Warning)
                    Dispatcher.UIThread.Post(fun () -> composer.Focus())
                with ex ->
                    Dispatcher.UIThread.Post(fun () ->
                        toast (sprintf "读取文件失败：%s" ex.Message) Failure
                        composer.Focus())
            }
            |> Async.Start

    member private this.DropFiles(items: IStorageItem list) =
        match topLevel () with
        | null -> ()
        | _ ->
            let owner = attachmentDraft ()
            let ownerInstance = instanceId
            let ownerConnection = client.ConnectionGeneration
            async {
                try
                    for item in items do
                        match item with
                        | :? IStorageFile as sf ->
                            use! stream = sf.OpenReadAsync() |> Async.AwaitTask
                            use buffer = new MemoryStream()
                            do! stream.CopyToAsync buffer |> Async.AwaitTask
                            let bytes = buffer.ToArray()
                            Dispatcher.UIThread.Post(fun () ->
                                if authenticated && instanceId = ownerInstance && client.ConnectionGeneration = ownerConnection then
                                    this.BeginUpload(owner, sf.Name, bytes)
                                else toast "连接已断开，请重新选择附件；文件没有发送到其他服务器。" Warning)
                        | _ ->
                            let path = item.TryGetLocalPath()
                            if not (String.IsNullOrEmpty path) && File.Exists path then
                                let bytes = File.ReadAllBytes path
                                let name = Path.GetFileName path
                                Dispatcher.UIThread.Post(fun () ->
                                    if authenticated && instanceId = ownerInstance && client.ConnectionGeneration = ownerConnection then
                                        this.BeginUpload(owner, name, bytes)
                                    else toast "连接已断开，请重新选择附件；文件没有发送到其他服务器。" Warning)
                    Dispatcher.UIThread.Post(fun () -> composer.Focus())
                with ex ->
                    Dispatcher.UIThread.Post(fun () ->
                        toast (sprintf "读取拖入文件失败：%s" ex.Message) Failure
                        composer.Focus())
            }
            |> Async.Start

    member private this.HandleClipboardPaste() : bool =
        match topLevel () with
        | null -> false
        | top ->
            try
                let clipboard = top.Clipboard
                if isNull clipboard then false
                else
                    let formatsTask = clipboard.GetDataFormatsAsync()
                    if formatsTask.Wait(100) && formatsTask.Result <> null then
                        let formats = formatsTask.Result
                        let hasFiles = formats |> Seq.exists (fun f -> f = DataFormat.File)
                        let hasBitmap = formats |> Seq.exists (fun f -> f = DataFormat.Bitmap)
                        if hasFiles then
                            async {
                                try
                                    let! files = clipboard.TryGetFilesAsync() |> Async.AwaitTask
                                    if files <> null && files.Length > 0 then
                                        Dispatcher.UIThread.Post(fun () -> this.DropFiles(List.ofArray files))
                                with ex ->
                                    Dispatcher.UIThread.Post(fun () ->
                                        toast (sprintf "读取剪贴板文件失败：%s" ex.Message) Failure
                                        composer.Focus())
                            } |> Async.Start
                            true
                        elif hasBitmap then
                            async {
                                try
                                    let! bitmap = clipboard.TryGetBitmapAsync() |> Async.AwaitTask
                                    if bitmap <> null then
                                        use buffer = new MemoryStream()
                                        let options = PngBitmapEncoderOptions()
                                        bitmap.Save(buffer, options)
                                        let bytes = buffer.ToArray()
                                        let name = sprintf "paste_%s.png" (DateTime.Now.ToString("yyyyMMdd_HHmmss"))
                                        let owner = attachmentDraft ()
                                        Dispatcher.UIThread.Post(fun () ->
                                            if authenticated then this.BeginUpload(owner, name, bytes)
                                            else toast "连接已断开，请重新选择附件；文件没有发送到其他服务器。" Warning
                                            composer.Focus())
                                with ex ->
                                    Dispatcher.UIThread.Post(fun () ->
                                        toast (sprintf "读取剪贴板图片失败：%s" ex.Message) Failure
                                        composer.Focus())
                            } |> Async.Start
                            true
                        else false
                    else false
            with _ ->
                false

    member private this.BeginUpload(owner: AttachmentDraftController, fileName: string, bytes: byte[]) =
        if bytes.Length = 0 then toast "空文件无法上传。" Warning
        elif int64 bytes.Length > 64L * 1024L * 1024L then toast "附件超过 64 MiB 上限。" Warning
        else
            let sha = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
            let mediaType = MediaTypes.ofFileName fileName
            let attachmentId = Guid.CreateVersion7()
            let upload =
                { attachmentId = attachmentId
                  fileName = fileName
                  mediaType = mediaType
                  size = int64 bytes.Length
                  sha256 = sha }
            owner.Begin upload |> ignore
            this.Render()
            send (
                AttachmentBegin
                    {| attachmentId = attachmentId
                       totalBytes = int64 bytes.Length
                       sha256 = sha
                       mediaType = mediaType
                       fileName = fileName |})
            let chunkSize = 256 * 1024
            let mutable offset = 0
            let mutable index = 0
            while offset < bytes.Length do
                let count = min chunkSize (bytes.Length - offset)
                send (
                    AttachmentChunk
                        {| attachmentId = attachmentId
                           index = index
                           dataBase64 = Convert.ToBase64String(bytes, offset, count) |})
                offset <- offset + count
                index <- index + 1
            send (AttachmentComplete {| attachmentId = attachmentId; sha256 = sha |})

    member private this.SaveDownload(fileName: string, bytes: byte[]) =
        match topLevel () with
        | null -> ()
        | top ->
            async {
                try
                    let! file =
                        top.StorageProvider.SaveFilePickerAsync(FilePickerSaveOptions(SuggestedFileName = fileName))
                        |> Async.AwaitTask
                    if not (isNull file) then
                        use! stream = file.OpenWriteAsync() |> Async.AwaitTask
                        do! stream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                        Dispatcher.UIThread.Post(fun () -> toast (sprintf "已保存 %s" fileName) Success)
                with ex ->
                    Dispatcher.UIThread.Post(fun () -> toast (sprintf "保存失败：%s" ex.Message) Failure)
            }
            |> Async.Start

    member private this.ExportConversation(summary: ConversationSummary) =
        if not authenticated then toast "连接服务器后才能读取完整历史。" Warning
        elif exportDialog |> Option.exists (fun dialog -> dialog.IsOpen) then ()
        else
            let ownerInstance = instanceId
            let dialog =
                ConversationExportDialog(
                    overlay, summary.id, summary.title,
                    (fun query ->
                        if authenticated && instanceId = ownerInstance then
                            client.TrySendAtGenerationAsync(client.ConnectionGeneration, ConversationExportRead query)
                        else System.Threading.Tasks.Task.FromResult false),
                    topLevel)
            exportDialog <- Some dialog
            dialog.Show()

    member this.ToggleSidebar() =
        let state = navigation.ToggleSidebar()
        mainLayout.Apply state
        if state.compactMode then
            // compact 抽屉开关的焦点归宿：打开进抽屉，收起回输入区（D3/D4）。
            if state.compactNavigationOpen then sidebar.FocusSearch()
            else composer.Focus()
        else
            prefs <- { prefs with sidebarCollapsed = state.sidebarCollapsed }
            UiPrefs.save prefs

    member this.HandleShortcut(e: KeyEventArgs) =
        match ShortcutRouter.resolve e with
        | Escape ->
            if overlay.HandleEscape() then e.Handled <- true
            elif settingsHost.IsVisible then
                e.Handled <- true
                this.CloseSettings()
            elif navigation.State.compactMode && navigation.State.compactNavigationOpen then
                // 抽屉内有焦点的分支（搜索/行/开关/空态）已先消费 Escape 并标记 Handled；
                // 落到这里说明焦点在抽屉外，直接收起并把焦点还给输入区，且先于生成停止分支（D3/D4）。
                e.Handled <- true
                this.SetCompactNavigation false
            elif (runs.Get activeConvId).generationId.IsSome then
                e.Handled <- true
                this.StopGeneration()
        | NoShortcut -> ()
        | _ when overlay.IsDialogOpen || overlay.IsPopupOpen -> ()
        | ToggleSidebar -> e.Handled <- true; this.ToggleSidebar()
        | NewConversation -> e.Handled <- true; this.CreateConversation() |> ignore
        | FocusSearch ->
            e.Handled <- true
            let state = navigation.EnsureSearchVisible()
            mainLayout.Apply state
            // 以返回的状态为准回写偏好，不读更新前的 prefs（D3/D4）。
            if not state.compactMode && prefs.sidebarCollapsed <> state.sidebarCollapsed then
                prefs <- { prefs with sidebarCollapsed = state.sidebarCollapsed }
                UiPrefs.save prefs
            sidebar.FocusSearch()
        | OpenSettings -> e.Handled <- true; this.ShowSettings()
        | ToggleTheme ->
            e.Handled <- true
            // 主题三态轮转：跟随系统 → 浅色 → 深色 → 回到跟随系统。
            let nextTheme =
                match prefs.theme with
                | FollowSystem -> AlwaysLight
                | AlwaysLight -> AlwaysDark
                | AlwaysDark -> FollowSystem
            this.SavePrefs { prefs with theme = nextTheme }
            toast
                (match nextTheme with
                 | FollowSystem -> "已切换为跟随系统主题"
                 | AlwaysDark -> "已切换为深色主题"
                 | AlwaysLight -> "已切换为浅色主题")
                Neutral
        | ExportConversation ->
            e.Handled <- true
            match activeSummary() with
            | Some summary -> this.ExportConversation summary
            | None -> toast "没有打开的会话可导出" Warning
        | OpenConversationAt index ->
            // Ctrl+1..9 走侧栏当前可见顺序（C2）；打开后焦点经 OpenConversation 回家（D3/D4）。
            let order = sidebar.GetVisibleOrder()
            if index >= 0 && index < order.Length then
                match Guid.TryParse order[index] with
                | true, id ->
                    e.Handled <- true
                    this.OpenConversation id
                | _ -> ()
        | ShowShortcuts -> e.Handled <- true; Dialogs.shortcuts overlay

    // ---- 协议事件 ----

    member private this.HandleEvent(ev: WireEvent) =
        match ev with
        | Hello _ -> ()
        | AuthAccepted d ->
            if instanceId <> "" && instanceId <> d.instanceId then
                if exportDialog |> Option.exists (fun dialog -> dialog.IsOpen) then overlay.CloseDialog()
                (drafts.Get(instanceId, activeConvId)).text <- composer.Text
                outbox.Disconnect instanceId
                state.Reset()
                runs.Clear()
                catalog <- Catalog.empty
                settings.SetCatalog catalog
                activeConvId <- None
                summaries <- []
                sidebar.SetConversations []
                composer.SetText((drafts.Get(d.instanceId, None)).text)
            authenticated <- true
            instanceId <- d.instanceId
            // 连接真的成功了，退避归零
            reconnectDelayMs <- ReconnectBackoff.baseDelayMs
            pairingRequested <- false
            // 重连成功：上一代连接残留的失败卡片已过期，清掉免得误导。
            for s in summaries do
                runs.ClearError s.id
            activeConvId |> Option.iter runs.ClearError
            if not (exportDialog |> Option.exists (fun dialog -> dialog.IsOpen)) then overlay.CloseDialog()
            let host = try Uri(lastUrl).Host with _ -> lastUrl
            sidebar.SetConnection(true, sprintf "已连接 · %s" host)
            settings.SetConnection(instanceId, lastUrl)
            if OperatingSystem.IsBrowser() then
                match lastToken with
                | Some token -> CredentialStore.saveBrowserConnectionAsync d.instanceId lastUrl token CredentialStore.clientName |> ignore
                | None -> ()
            send CatalogRequest
            send ObserveConversationList
            match activeConvId with
            | Some convId -> send (ObserveConversation {| conversationId = convId |})
            | None -> ()
            this.Render()
        | AuthRejected d ->
            authenticated <- false
            exportDialog |> Option.iter (fun dialog -> dialog.Disconnect())
            lastToken <- None
            sidebar.SetConnection(false, "连接已断开")
            // 失败只报一次：连接对话框内联呈现原因（紧邻重连入口），不再叠 toast。
            setConnectStatus (sprintf "连接已断开：%s" d.reason)
            this.Render()
        | UpgradeRequired d ->
            toast (sprintf "协议版本不匹配：服务端 %d，客户端 %d。请更新其中一端。" d.serverVersion d.clientVersion) Failure
        | PairingStarted _ -> setConnectStatus "配对码已打印到服务端终端，请输入后提交。"
        | PairingSucceeded d ->
            lastToken <- Some d.token
            setConnectStatus "配对成功，正在认证…"
            send (AuthPresent {| token = d.token |})
        | PairingFailed d ->
            let message =
                if d.frozen then sprintf "配对已冻结 %d 分钟：%s" d.freezeMinutes d.reason
                else sprintf "配对失败：%s" d.reason
            setConnectStatus message
            // 配对失败只报一次：对话框内联呈现（紧邻重试入口），不再叠 toast。
        | CatalogSnapshot d ->
            catalog <- Catalog.parse d.providers d.tools d.generation
            settings.SetCatalog catalog
            this.Render()
        | ConfigChanged _ -> send CatalogRequest
        | ProviderProbeResult d ->
            let models =
                [ for item in d.models do
                      if not (isNull item) && item.GetValueKind() = JsonValueKind.String then item.GetValue<string>() ]
            settings.ApplyProbe(d.providerId, d.ok, models, d.error)
        | ConfigApplied d ->
            match configFeedback.Resolve d.requestId with
            | Some feedback ->
                feedback.onCompleted d.ok
                if d.ok then
                    toast feedback.successMessage Success
                    send CatalogRequest
                else
                    toast (String.Join("；", d.errors)) Failure
            | None ->
                if d.ok then
                    toast "配置已保存" Success
                    send CatalogRequest
                else
                    toast (String.Join("；", d.errors)) Failure
        | ConversationListSnapshot d ->
            state.Handle ev
            summaries <- ConversationSummary.parseList d.items
            sidebar.SetConversations summaries
            sidebar.SetActive activeConvId
            state.AdvanceCursor()
            this.Render()
        | ConversationSnapshot d ->
            state.Handle ev
            state.AdvanceCursor()
            runs.Snapshot(d.conversationId, d.runtimeState, d.generationId)
            if activeConvId = Some d.conversationId then
                pageLoading <- false
                this.Render()
            outbox.ConversationReady(instanceId, d.conversationId)
            for item in outbox.Items(instanceId, Some d.conversationId) do
                if item.state = PreparingConversation then this.DispatchPendingMessage item
            if activeConvId = Some d.conversationId then this.Render()
        | MessageCommitted d ->
            state.Handle ev
            state.AdvanceCursor()
            let committed = MessageView.ofJson d.payload
            if not (MessageView.isUser committed) then runs.ClearMessage d.conversationId
            if activeConvId = Some d.conversationId then
                // 这条已成为权威消息，流式临时副本必须立刻作废；
                // 工具循环里会连续提交多条，留着旧缓冲就会看到重复的回复。
                this.Render()
        | ConversationUpdated d ->
            state.Handle ev
            state.AdvanceCursor()
            send ObserveConversationList
            if activeConvId = Some d.conversationId then
                this.Render()
        | AuthorityCatchUp _ -> state.Handle ev
        | HistoryPage d ->
            state.Handle ev
            if activeConvId = Some d.conversationId then
                pageLoading <- false
                this.Render()
                chat.RestoreHistoryPrependAnchorDeferred()
        | ConversationExportPage page -> exportDialog |> Option.iter (fun dialog -> dialog.Handle page)
        | ConversationExportFailed d -> exportDialog |> Option.iter (fun dialog -> dialog.Fail(d.exportId, d.message))
        | GenerationStarted d ->
            runs.Start(d.conversationId, d.generationId)
            if (runs.Get(Some d.conversationId)).generationId = Some d.generationId then
                state.Handle ev
                if activeConvId = Some d.conversationId then this.Render()
        | GenerationDelta d ->
            if runs.Delta(d.conversationId, d.generationId, MessageView.ofJson d.payload)
               && activeConvId = Some d.conversationId then
                this.Render()
        | GenerationFinished d ->
            // Fail-closed：未知状态与无错误详情的失败一律落成可重试的通用失败卡，绝不静默（D2）。
            let error =
                match d.status with
                | "completed" | "cancelled" -> None
                | "failed" ->
                    match d.error with
                    | Some _ as error -> error
                    | None ->
                        Some(GenerationError.create GenerationErrorKind.UnknownFailure "生成失败，但服务端没有返回错误详情。")
                | unknown ->
                    Some(
                        GenerationError.create GenerationErrorKind.UnknownFailure "生成结束状态未知，已按失败处理。"
                        |> GenerationError.withDetail (sprintf "未知状态：%s" unknown))
            if runs.Finish(d.conversationId, d.generationId, error, d.usage) then
                state.Handle ev
                match d.status, error with
                | "cancelled", _ when activeConvId = Some d.conversationId -> toast "已停止" Neutral
                // 非当前会话的失败只 toast（卡片看不见）；当前会话的失败只进卡片，不叠 toast。
                | _, Some err when activeConvId <> Some d.conversationId ->
                    toast (GenerationError.display err) Failure
                | _ -> ()
                if activeConvId = Some d.conversationId then this.Render()
        | AttachmentCommitted d ->
            match drafts.All |> Seq.tryPick (fun draft -> draft.attachments.Complete(d.attachmentId, d.size)) with
            // 完成 toast 只给当前可见会话：后台会话的翻转由 chip 自证，不叠 toast（C5）。
            | Some(upload, stillDrafted, key) when stillDrafted && key = (instanceId, activeConvId) ->
                composer.SetAttachments((attachmentDraft ()).Items)
                toast (sprintf "附件「%s」上传完成" upload.fileName) Success
            | Some _ -> ()
            | None -> ()
        | AttachmentAborted d ->
            match drafts.All |> Seq.tryPick (fun draft -> draft.attachments.Abort d.attachmentId) with
            // 失败恰报一次 toast（附件条同时内联呈现失败态，两处各报一次、不叠加）（C6）。
            | Some(upload, _, _) ->
                composer.SetAttachments((attachmentDraft ()).Items)
                toast (sprintf "上传失败：%s" upload.fileName) Warning
            | None -> ()
        | AttachmentDownloadBegin d ->
            let key = d.sha256.ToLowerInvariant()
            downloadBuffers[key] <- new MemoryStream()
            downloadMeta <- downloadMeta.Add(key, d.fileName)
        | AttachmentDownloadChunk d ->
            match downloadBuffers.TryGetValue(d.sha256.ToLowerInvariant()) with
            | true, buffer ->
                try
                    let bytes = Convert.FromBase64String d.dataBase64
                    buffer.Write(bytes, 0, bytes.Length)
                with _ -> ()
            | _ -> ()
        | AttachmentDownloadComplete d ->
            let key = d.sha256.ToLowerInvariant()
            match downloadBuffers.TryGetValue key with
            | true, buffer ->
                let fileName = downloadMeta.TryFind key |> Option.defaultValue d.sha256
                let bytes = buffer.ToArray()
                buffer.Dispose()
                downloadBuffers.Remove key |> ignore
                this.SaveDownload(fileName, bytes)
            | _ -> ()
        | CommandAccepted d ->
            outbox.Accept d.invocationId
            this.Render()
        | CommandCommitted d ->
            outbox.Commit d.invocationId
            match commandFeedback.Commit d.invocationId with
            | Some feedback ->
                feedback.onCommitted ()
                feedback.onCompleted true
                feedback.successMessage |> Option.iter (fun message -> toast message Success)
            | None -> ()
            this.Render()
        | CommandRejected d ->
            commandFeedback.Reject d.invocationId |> Option.iter (fun feedback -> feedback.onCompleted false)
            outbox.SetState(d.invocationId, RejectedMessage d.message)
            outbox.RejectCreation(d.invocationId, d.message)
            this.Render()
            // 拒绝只报一次：输入区上方的未确认行已内联呈现原因与重试，不再叠 toast。
        | ServerError d ->
            // 附件 blob 缺失时把该 sha256 标成缺失，消息里就会显示「内容已丢失」
            if d.message.StartsWith("attachment ", StringComparison.Ordinal) && d.message.Contains "not found" then
                let parts = d.message.Split(' ')
                if parts.Length > 1 then
                    missingAttachments <- missingAttachments.Add(parts[1].ToLowerInvariant())
                    this.Render()
            elif d.message.StartsWith("消息记账失败", StringComparison.Ordinal) && activeConvId.IsSome then
                // 可行动的服务端错误进持久面：错误卡片自带重试，不再叠 toast（D9）。
                // 若正有生成在跑（槽位被占），退回 toast，避免踩掉进行中的状态。
                let convId = activeConvId.Value
                let failure = GenerationError.create GenerationErrorKind.UnknownFailure d.message
                if runs.Finish(convId, Guid.CreateVersion7(), Some failure, None) then this.Render()
                else toast (sprintf "服务端错误：%s" d.message) Failure
            else
                toast (sprintf "服务端错误：%s" d.message) Failure
        | _ -> ()

    // ---- 组装 ----

    member private this.BuildSidebar() =
        let actions =
            { newConversation = fun () -> this.CreateConversation() |> ignore
              openConversation = fun id -> this.OpenConversation id
              renameConversation =
                fun summary ->
                    Dialogs.prompt overlay "重命名会话" "会话标题" summary.title "保存" (fun title onCompleted ->
                        let command =
                            RenameConversation
                                {| invocationId = newInvocation (); conversationId = summary.id; title = title |}
                        sendCommandWithCompletion command None ignore onCompleted)
              deleteConversation =
                fun summary ->
                    Dialogs.confirm
                        overlay
                        "删除会话"
                        (sprintf "「%s」及其消息将不再出现在列表里。此操作无法撤销。" summary.title)
                        "删除"
                        (fun () ->
                            let command =
                                DeleteConversation {| invocationId = newInvocation (); conversationId = summary.id |}
                            sendCommandWithFeedback
                                command
                                (Some(sprintf "会话「%s」已删除" summary.title))
                                (fun () ->
                                    // 删除后焦点有家：邻行，否则搜索框（C3）。
                                    sidebar.FocusAfterDelete(summary.id.ToString())
                                    if activeConvId = Some summary.id then
                                        this.SelectConversation None
                                        sidebar.SetActive None
                                        this.Render()))
              setPinned =
                fun summary pinned ->
                    let command =
                        SetConversationFlags
                            {| invocationId = newInvocation ()
                               conversationId = summary.id
                               pinned = pinned
                               archived = summary.archived |}
                    sendCommandWithFeedback
                        command
                        (Some(if pinned then sprintf "已置顶「%s」" summary.title else sprintf "已取消置顶「%s」" summary.title))
                        ignore
              setArchived =
                fun summary archived ->
                    let command =
                        SetConversationFlags
                            {| invocationId = newInvocation ()
                               conversationId = summary.id
                               pinned = summary.pinned
                               archived = archived |}
                    sendCommandWithFeedback
                        command
                        (Some(if archived then sprintf "已归档「%s」" summary.title else sprintf "已取消归档「%s」" summary.title))
                        ignore
              duplicateAsFork =
                fun summary ->
                    this.OpenConversation summary.id
                    toast "已打开会话，可从任意消息分叉。" Neutral
              exportConversation = fun summary -> this.ExportConversation summary
              openSettings = fun () -> this.ShowSettings()
              reconnect =
                fun () ->
                    if authenticated then toast "已连接" Neutral
                    elif lastToken.IsSome then this.Connect()
                    else this.ShowConnectDialog()
              toggleArchivedVisibility = fun () -> this.SavePrefs { prefs with showArchived = not prefs.showArchived }
              closeNavigation = fun () -> this.SetCompactNavigation false }
        sidebar <- Sidebar(overlay, actions, Brand.logo)
        sidebar.Build()
        sidebar.SetShowArchived prefs.showArchived

    member private this.BuildChat() =
        let messageActions =
            { copyText = copyToClipboard
              regenerate = fun () -> this.Regenerate()
              editAndFork = fun message -> this.ForkFrom(message.commitId, message.text)
              deleteMessage =
                fun commitId ->
                    match activeConvId with
                    | Some convId ->
                        Dialogs.confirm overlay "删除消息" "这条消息会从会话中移除，后续生成不再看到它。" "删除" (fun () ->
                            let command =
                                DeleteMessage
                                    {| invocationId = newInvocation ()
                                       conversationId = convId
                                       messageCommitId = commitId |}
                            sendCommandWithFeedback command (Some "消息已删除") ignore)
                    | None -> ()
              downloadAttachment = fun sha -> send (AttachmentDownloadRequest {| sha256 = sha |})
              openLink = openLink }
        let chatActions =
            { renameTitle =
                fun title ->
                    match activeConvId with
                    | Some convId ->
                        sendCommand (
                            RenameConversation {| invocationId = newInvocation (); conversationId = convId; title = title |})
                    | None -> ()
              openSessionSettings =
                fun () ->
                    match activeConvId with
                    | Some convId ->
                        Dialogs.sessionSettings overlay catalog (activeConfig ()) (fun config onCompleted ->
                            let command =
                                UpdateConversationConfig
                                    {| invocationId = newInvocation (); conversationId = convId; config = config |}
                            sendCommandWithCompletion command (Some "会话设置已更新") ignore onCompleted)
                    | None -> toast "先选择一个会话。" Warning
              forkFromHere =
                fun () ->
                    match activeConversation () with
                    | Some view ->
                        let messages = messagesOf view
                        let seed =
                            messages
                            |> List.filter MessageView.isUser
                            |> List.tryLast
                            |> Option.map (fun m -> m.text)
                            |> Option.defaultValue ""
                        this.ForkFrom(None, seed)
                    | None -> toast "当前没有可分叉的会话。" Warning
              stopGeneration = fun () -> this.StopGeneration()
              requestOlderHistory = fun () -> this.RequestOlderHistory()
              retryLast = fun () -> this.Regenerate()
              toggleSidebar = fun () -> this.ToggleSidebar()
              message = messageActions }
        chat <- ChatView(chatActions, Brand.logo)
        chat.Build()

    member private this.RequestOlderHistory() =
        match activeConvId, activeConversation () with
        | Some convId, Some view when view.pageHasMore && not pageLoading ->
            chat.BeginHistoryPrependAnchor()
            pageLoading <- true
            // 分页没有骨架屏（会重置滚动）：一条 transient toast 给加载反馈。
            toast "正在加载更早消息…" Neutral
            send (
                HistoryRequest
                    {| conversationId = convId
                       beforeCommitId = view.pageEarliest
                       limit = 100 |})
        | _ -> ()

    member private this.BuildComposer() =
        let actions =
            { submit = fun text -> this.SendMessage text
              stopGeneration = fun () -> this.StopGeneration()
              pickAttachment = fun () -> this.PickAttachment()
              removeAttachment =
                fun attachmentId ->
                    (attachmentDraft ()).Remove attachmentId |> composer.SetAttachments
              openModelPicker = fun anchor -> this.ShowModelPicker anchor
              dropFiles = fun files -> this.DropFiles files
              pasteFromClipboard = fun () -> this.HandleClipboardPaste () }
        composer <- Composer(actions)
        composer.Build()
        composer.SetEnterSends prefs.enterSends

    member private this.BuildSettings() =
        let actions =
            { upsertProvider =
                fun payload onCompleted ->
                    sendConfigWithFeedback
                        "服务商设置已保存"
                        (fun requestId -> ConfigUpsertProvider {| requestId = requestId; provider = payload |})
                        onCompleted
              deleteProvider =
                fun id ->
                    Dialogs.confirm overlay "删除服务商" (sprintf "「%s」将从配置中移除，使用它的会话需要重新选择模型。" id) "删除" (fun () ->
                        sendConfigWithFeedback
                            "服务商已删除"
                            (fun requestId -> ConfigDeleteProvider {| requestId = requestId; providerId = id |})
                            ignore)
              probeProvider = fun id -> send (ProviderProbeRequest {| requestId = newInvocation (); providerId = id |})
              upsertMcp =
                fun payload onCompleted ->
                    sendConfigWithFeedback
                        "MCP 设置已保存"
                        (fun requestId -> ConfigUpsertMcp {| requestId = requestId; server = payload |})
                        onCompleted
              deleteMcp =
                fun id ->
                    Dialogs.confirm overlay "删除 MCP 服务器" (sprintf "「%s」提供的工具将不再可用。" id) "删除" (fun () ->
                        sendConfigWithFeedback
                            "MCP 服务器已删除"
                            (fun requestId -> ConfigDeleteMcp {| requestId = requestId; serverId = id |})
                            ignore)
              updateGeneration =
                fun payload onCompleted ->
                    sendConfigWithFeedback
                        "生成设置已保存"
                        (fun requestId -> ConfigUpdateGeneration {| requestId = requestId; generation = payload |})
                        onCompleted
              savePrefs = fun next -> this.SavePrefs next
              toast = fun message tone -> toast message tone }
        settings <- SettingsView(overlay, actions, (fun next -> this.SavePrefs next), (fun () -> this.CloseSettings()))
        settings.Build()

    member this.Build() =
        this.FontFamily <- Tokens.fontFamily
        this.ApplyTheme()

        this.BuildSidebar()
        this.BuildChat()
        this.BuildComposer()
        this.BuildSettings()

        chatColumn <- DockPanel()
        DockPanel.SetDock(composer, Dock.Bottom)
        chatColumn.Children.Add composer
        chatColumn.Children.Add chat

        let initialNavigation = navigation.State
        shellGrid <- Grid()
        // 列宽唯一投影器是 MainLayoutController.Apply：这里只建空定义，初值由下方的 Apply 落定。
        shellGrid.ColumnDefinitions.Add(ColumnDefinition(MaxWidth = Tokens.sidebarMaxWidth))
        shellGrid.ColumnDefinitions.Add(ColumnDefinition())
        shellGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        sidebarSplitter <-
            GridSplitter(
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                KeyboardIncrement = 8.0,
                DragIncrement = 1.0,
                Focusable = true,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
                IsVisible = not initialNavigation.sidebarCollapsed)
        Avalonia.Automation.AutomationProperties.SetName(sidebarSplitter, "调整侧边栏宽度")
        sidebarSplitter.PointerEntered.Add(fun _ -> sidebarSplitter.Background <- Tokens.hover)
        sidebarSplitter.PointerExited.Add(fun _ ->
            sidebarSplitter.Background <-
                if sidebarSplitter.IsFocused then Tokens.accentFaint :> IBrush else Brushes.Transparent :> IBrush)
        sidebarSplitter.GotFocus.Add(fun _ -> sidebarSplitter.Background <- Tokens.accentFaint)
        sidebarSplitter.LostFocus.Add(fun _ ->
            sidebarSplitter.Background <-
                if sidebarSplitter.IsPointerOver then Tokens.hover :> IBrush else Brushes.Transparent :> IBrush)
        sidebarSplitter.DragCompleted.Add(fun _ ->
            let nav = navigation.State
            if not nav.compactMode && not nav.sidebarCollapsed then
                let width = mainLayout.NoteUserSidebarWidth shellGrid.ColumnDefinitions[0].ActualWidth
                prefs <- { prefs with sidebarWidth = width }
                UiPrefs.save prefs)
        shellGrid.Children.Add sidebar
        shellGrid.Children.Add sidebarSplitter
        shellGrid.Children.Add chatColumn
        mainLayout <- MainLayoutController(shellGrid, sidebar, sidebarSplitter, chatColumn, composer, chat, fun () -> prefs.sidebarWidth)
        mainLayout.Apply initialNavigation
        workspace.Children.Add shellGrid

        settingsHost.Children.Add settings

        root.Children.Insert(0, workspace)
        root.Children.Insert(1, settingsHost)
        this.Content <- root
        this.Background <- Tokens.canvas
        overlay.WireDismiss()

        client.ConnectionEventReceived.Add(fun (epoch, ev) ->
            Dispatcher.UIThread.Post(fun () ->
                if epoch = client.ConnectionGeneration then this.HandleEvent ev))
        state.CursorChanged.Add(fun _ -> send (state.CursorAdvancedEvent()))
        client.ConnectionClosed.Add(fun (epoch, error) ->
            Dispatcher.UIThread.Post(fun () ->
                if epoch = client.ConnectionGeneration then
                    authenticated <- false
                    runs.Disconnect()
                    exportDialog |> Option.iter (fun dialog -> dialog.Disconnect())
                    outbox.Disconnect instanceId
                    commandFeedback.RejectAll()
                    configFeedback.RejectAll()
                    this.FailUploadingAttachments()
                    let detail = match error with Some e -> e.Message | None -> ""
                    sidebar.SetConnection(false, "连接已断开")
                    if not (String.IsNullOrWhiteSpace detail) then toast (sprintf "连接已断开：%s" detail) Warning
                    this.Render()
                    this.ScheduleReconnect()))

        // 壳背景跟随主题一起换：内容容器全透明，任一层过期都会在窗口边缘漏出异色细线。
        Tokens.Changed.Publish.Add(fun _ ->
            Dispatcher.UIThread.Post(fun () ->
                this.Background <- Tokens.canvas
                this.Render()))
        // 后台着色算完：卡片身份没变，必须显式丢缓存才会换成着色版
        Highlight.Ready.Add(fun _ ->
            Dispatcher.UIThread.Post(fun () ->
                chat.InvalidateCards()
                this.Render()))
        match Application.Current with
        | null -> ()
        | app ->
            match app.PlatformSettings with
            | null -> ()
            | settings ->
                settings.ColorValuesChanged.Add(fun _ ->
                    if prefs.theme = FollowSystem then
                        Dispatcher.UIThread.Post(fun () -> this.ApplyTheme()))
        this.KeyDown.Add(fun e -> this.HandleShortcut e)
        this.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then
                this.ApplyResponsiveLayout this.Bounds.Width)

        this.Render()
        this.ApplyResponsiveLayout this.Bounds.Width
        this.WatchHostViewport()
        let deliveryTimer = DispatcherTimer(Interval = TimeSpan.FromSeconds 1.0)
        deliveryTimer.Tick.Add(fun _ ->
            if outbox.Expire(DateTimeOffset.UtcNow, TimeSpan.FromSeconds 30.0) then this.Render())
        this.AttachedToVisualTree.Add(fun _ -> deliveryTimer.Start())
        this.DetachedFromVisualTree.Add(fun _ -> deliveryTimer.Stop())
        this.AutoConnect()

    /// 已有凭据时自动连接：桌面读 client.toml，PWA 读 IndexedDB（决策 52/53、Q191）。
    member private this.AutoConnect() =
        match CredentialStore.tryLoadClientToml () with
        | Some(url, token) ->
            lastUrl <- url
            lastToken <- Some token
            this.Connect()
        | None ->
            if OperatingSystem.IsBrowser() then
                async {
                    let! stored = CredentialStore.tryLoadBrowserConnectionAsync () |> Async.AwaitTask
                    match stored with
                    | Some(url, token, _) ->
                        Dispatcher.UIThread.Post(fun () ->
                            lastUrl <- url
                            lastToken <- Some token
                            this.Connect())
                    | None -> Dispatcher.UIThread.Post(fun () -> this.ShowConnectDialog())
                }
                |> Async.Start
            else
                Dispatcher.UIThread.Post(fun () -> this.ShowConnectDialog())
