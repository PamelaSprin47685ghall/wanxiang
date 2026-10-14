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

    /// 构造时所在的 UI 调度器。后台回调（模块级事件 Highlight.Ready、网络线程、Async 续延）
    /// 一律投递到这里，而不是事后去取 `Dispatcher.UIThread`：后者在调度器尚未建立/已被测试会话
    /// 重置时，会在调用方线程上**新建**一个 UI 调度器并永久归它所有（决策 421）。
    let ui = Dispatcher.UIThread

    let client = WsClient()
    let state = ClientState()

    let root = Grid()
    let overlay = OverlayHost(root)

    let mutable prefs = UiPrefsFile.load ()
    // 启动就把折行默认值交给渲染器：它是渲染期读的静态值，不走构造参数
    do
        MarkdownRenderer.DefaultCodeWrap <- prefs.codeWrap
        MotionPolicy.setReduced prefs.reduceMotion
    let mutable catalog = Catalog.empty
    /// 还没有对话时用户在输入区芯片上挑的模型：草稿模型的暂存位。
    /// 没有它，「空态里点芯片选模型」只能弹「先选择一个对话」，
    /// 而输入区此刻是可用且显示着模型名的（AppShell.Render：NoConversation 分支）。
    /// 选了之后仍走原有创建路径，只是把 defaultSelection 换成这个暂存值。
    let mutable draftModelSelection: (string * string) option = None
    let mutable authenticated = false
    let mutable instanceId = ""

    let mutable activeConvId: Guid option = None
    /// 决策 440：主机声明的功能表。None = 主机没声明（旧主机），照常显示、靠超时兜底。
    let mutable hostFeatures: Set<string> option = None
    /// 决策 449：周期计时器是否已启动（测试观察点）。
    let mutable timersStarted = false
    let mutable topLevelKeysHooked: TopLevel option = None
    /// 决策 444：搜索与模型服务探测也是「请求→回应」，同样设等待上限。
    let mutable searchSentAt: DateTimeOffset option = None
    let pendingProbes = System.Collections.Generic.Dictionary<Guid, string * DateTimeOffset>()
    let runs = ConversationRuns()
    let mutable summaries: ConversationSummary list = []
    let mutable paletteRecent: string list = PaletteRecent.loadRecent ()
    /// §214：宽限期内的待删批次——列表里隐去，过期才提交删除命令。
    let mutable pendingDeletes = PendingDeletion.empty
    /// W4：分叉谱系（随列表快照更新）、最近一次搜索的 queryId、待滚动到的消息。
    let mutable lineage: Map<Guid, ForkLineage> = Map.empty
    let mutable latestSearchQueryId: Guid option = None
    let mutable pendingReveal: (Guid * RevealTarget) option = None

    let drafts = ComposerDrafts()
    let outbox = MessageOutbox()
    // ---- 本地草稿（W5，决策 80/81）：只存本机，不入日志、不上传、不同步 ----
    let mutable draftStore = LocalDraftStore.Default(fun () -> prefs.persistDrafts)
    /// 启动时从本机读到的快照；保存时其中「尚未恢复的主机」原样保留。
    let mutable storedDrafts = DraftSnapshot.empty
    let mutable draftsLoaded = false
    let restoredInstances = System.Collections.Generic.HashSet<string>()
    /// 曾在列表或本地恢复里见过的对话 id：之后从列表里消失，才视为被删除并清理其草稿。
    let mutable knownConversationIds: Set<Guid> = Set.empty
    let unsentSavedAt = System.Collections.Generic.Dictionary<Guid, DateTimeOffset>()
    let attachmentDraft () = (drafts.Get(instanceId, activeConvId)).attachments
    // 附件下载在途缓冲：容量上限 + 断连整体丢弃，替代此前无上限的裸字典（R1）。
    let downloads = DownloadBuffers()
    let mutable downloadCappedNotified = false
    let mutable missingAttachments: Set<string> = Set.empty

    let mutable lastUrl = CredentialStore.defaultServerUrl ()
    let mutable lastToken: string option = None
    let mutable hasSavedCredentials = CredentialStore.tryLoadClientToml().IsSome
    let mutable connectionError: string option = None
    /// 类型化的连接失败原因：连接对话框的重试动作由它决定，不由 connectionError 的文案推断。
    let mutable connectionIssue: ConnectionIssue option = None
    let mutable enteredSettingsForFirstProvider = false
    let mutable reconnectCts: CancellationTokenSource option = None
    let mutable reconnectDelayMs = ReconnectBackoff.baseDelayMs
    let mutable pairingRequested = false
    let mutable connectAttempt = 0
    let mutable setConnectStatus: string -> unit = ignore
    let mutable failConnectStatus: ConnectionIssue -> unit = ignore
    let mutable pageLoading = false
    /// 决策 310：本次更早一页请求的发出时刻；超时检查据此判失败。
    let mutable pageRequestedAt: DateTimeOffset option = None
    let mutable olderPageWatchdog: Avalonia.Threading.DispatcherTimer option = None
    /// 设置关闭后焦点的回家点：打开设置时的触发控件（设置入口按钮等）。只记一个元素，
    /// 不经过 OverlayHost 的对话框记忆（对话框已有唯一的记忆路径，这里是视图显隐切换）。
    let mutable settingsReturnFocus: Control option = None
    /// 对话快照等待中的骨架屏延时器：300ms 内到达就不挂任何 loading chrome。
    let mutable skeletonTimer: DispatcherTimer option = None
    /// 骨架屏延时器已为哪个对话武装：Render 会被后台事件反复触发，不得重启它，一对话只武装一次（D1）。
    let mutable skeletonArmedFor: Guid option = None
    /// 浏览器宿主的 CSS 像素视口宽（`wxViewportWidth` 轮询写入）。
    /// Browser 后端高 DPR 下 `Bounds.Width` 会被 DPR 除一次，不可做断点依据；
    /// 有覆盖值时响应式布局只认它，桌面/测试走 Bounds 原路径。
    let mutable hostViewportWidth: float option = None
    /// 浏览器 CSS 视口轮询计时器：仅在 Browser 环境启动，卸载时停止。
    let mutable viewportTimer: DispatcherTimer option = None
    /// 上次看到的浏览器“恢复”计数（online / 回到前台，第 213 节）。
    let mutable lastResumeEpoch = 0
    let mutable lastPageLink = ""
    let mutable pendingLink: (Guid * uint64) option = None
    let mutable listReceived = false
    let commandFeedback = CommandFeedbackTracker()
    let configFeedback = ConfigFeedbackTracker()
    let mutable exportDialog: ConversationExportDialog option = None
    let mutable importDialog: ConversationImportDialog option = None

    let toast message tone = overlay.Toast(message, tone)
    let topLevel () = TopLevel.GetTopLevel this

    // 剪贴板写入可能因权限、焦点或宿主（PWA）被拒：必须等写入落地再报，成功与失败各自说话。
    // 签名保持 string -> unit，调用方（消息复制、Markdown 代码块复制）无感。
    let copyToClipboard (text: string) =
        ParityProbe.hit "clipboard.copy"
        try
            match topLevel () with
            | null -> toast "无法访问剪贴板，复制失败。" Failure
            | top ->
                match top.Clipboard with
                | null -> toast "无法访问剪贴板，复制失败。" Failure
                | clip ->
                    async {
                        try
                            do! clip.SetTextAsync text |> Async.AwaitTask
                            ui.Post(fun () -> toast "已复制到剪贴板" Success)
                        with _ ->
                            ui.Post(fun () -> toast "复制到剪贴板失败，请重试。" Failure)
                    }
                    |> Async.Start
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
    /// 聊天主界面上的「连接一个模型」卡片（W1）：首次运行不进设置页。
    let mutable onboarding: OnboardingCard = Unchecked.defaultof<OnboardingCard>
    let mutable sidebarSplitter: GridSplitter = Unchecked.defaultof<GridSplitter>
    let mutable mainLayout: MainLayoutController = Unchecked.defaultof<MainLayoutController>
    let navigation = NavigationController(prefs.sidebarCollapsed, LayoutPolicy.compactBreakpoint)
    let workspace = Grid()
    let settingsHost = Grid(IsVisible = false)
    let mutable shellGrid: Grid = Unchecked.defaultof<Grid>
    let mutable chatColumn: DockPanel = Unchecked.defaultof<DockPanel>

    let send (ev: WireEvent) = client.SendAsync ev |> ignore

    /// 工具审批卡（第 71 节）：对话内、浮在输入框上方；状态只在内存里，由主机事件驱动。
    let approvalPanel =
        ToolApprovalPanel(fun approvalId approved -> send (ToolApprovalDecision {| approvalId = approvalId; approved = approved |}))
    /// 发出对话列表观察：请求一经发出就置侧栏加载中，
    /// 快照到达时由 Sidebar.SetConversations 解除（AppShell 不重复清除，避免双写）。
    let observeConversationList () =
        sidebar.SetConversationListLoading true
        send ObserveConversationList
    let sendCommand (cmd: ClientCommand) =
        let epoch = client.ConnectionGeneration
        // 在 UI 操作发生时就进入发送队列，不能让线程池调度改变消息顺序。
        let sending = client.TrySendCommandAtGenerationAsync(epoch, cmd)
        async {
            let! sent = sending |> Async.AwaitTask
            if not sent then
                ui.Post(fun () ->
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
            // 触屏上不自动聚焦搜索框：软键盘会弹出遮住对话列表（第 212 节）
            if opened then (if not (InputModality.prefersTouch ()) then sidebar.FocusSearch())
            elif before.compactNavigationOpen then composer.Focus()

    member private this.ApplyResponsiveLayout(width: float) =
        let effective = hostViewportWidth |> Option.defaultValue width
        if effective > 0.0 && not (isNull (box mainLayout)) then
            let before = navigation.State
            let needsApply, state = navigation.ApplyViewport(effective)
            if needsApply then mainLayout.Apply state
            // compact 抽屉的焦点归宿与用户手动打开一致（D3/D4）：视口把抽屉自动带开时
            // （进入 compact 且无对话），也先把焦点送进抽屉（搜索框），避免 Tab 漏到背景聊天/输入区。
            // 触发条件与 SetCompactNavigation 同口径：抽屉由「未开」翻到「compact 打开」才聚焦——
            // 关闭态、非 compact、以及开合状态未变的普通缩放都不抢焦点。
            if state.compactMode && state.compactNavigationOpen
               && not (before.compactMode && before.compactNavigationOpen)
               && not (InputModality.prefersTouch ()) then
                sidebar.FocusSearch()

    /// 浏览器 CSS 视口轮询：zoom、DPR、显示器移动都不会可靠触发 Bounds 更新，
    /// 但 innerWidth 永远是真相。桌面端不调用（保留 Bounds 路径）。
    member private this.WatchHostViewport() =
        if OperatingSystem.IsBrowser() then
            let timer = DispatcherTimer(Interval = MotionLedger.hostViewportPoll)
            timer.Tick.Add(fun _ ->
                ParityProbe.hit "timer.viewport"
                try
                    let css = BrowserPage.ViewportWidth()
                    if css > 0.0 && hostViewportWidth <> Some css then
                        hostViewportWidth <- Some css
                        this.ApplyResponsiveLayout css
                with _ -> ()
                try
                    let epoch = BrowserPage.ResumeEpoch()
                    if epoch <> lastResumeEpoch then
                        lastResumeEpoch <- epoch
                        ParityProbe.hit "reconnect.resume"
                        this.ResumeReconnectNow()
                with _ -> ()
                this.CheckPageLink())
            viewportTimer <- Some timer
            timer.Start()

    member private this.StartViewportWatcher() =
        viewportTimer |> Option.iter (fun t -> t.Start())

    member private this.StopViewportWatcher() =
        viewportTimer |> Option.iter (fun t -> t.Stop())

    member private _.StreamingMessage() : MessageView option =
        (runs.Get activeConvId).message
    /// 对话快照到达前的本地 loading：300ms 内不挂任何 chrome，超时才挂骨架 + 标题。
    /// 快照先到则定时器自证过期，不做任何事。
    member private this.ShowConversationLoadingDeferred(convId: Guid) =
        // 一对话只武装一次：后台事件触发的 Render 若重启计时器，骨架屏会被无限顺延（D1）。
        if skeletonArmedFor <> Some convId then
            skeletonArmedFor <- Some convId
            match skeletonTimer with
            | Some timer -> timer.Stop()
            | None -> ()
            let timer = DispatcherTimer(Interval = MotionLedger.conversationSkeletonDelay)
            timer.Tick.Add(fun _ ->
                timer.Stop()
                if activeConvId = Some convId && (activeConversation ()).IsNone then
                    chat.SetTitle("加载中…", false)
                    chat.ShowSkeletonLoading())
            skeletonTimer <- Some timer
            timer.Start()

    /// 快照到达或切换对话：解除骨架屏武装并停表。
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

    /// 审批卡增减后同步消息里工具卡的「等待批准」状态（第 206 节）。
    member private this.SyncAwaitingApproval() =
        let ids = approvalPanel.AwaitingCallIds
        if ids <> chat.AwaitingApproval then
            chat.SetAwaitingApproval ids
            this.Render()

    member private this.Render() =
        let run = runs.Get activeConvId
        let generating = run.running
        sidebar.SetServerUrl lastUrl
        approvalPanel.SetConversation activeConvId
        let startReady =
            authenticated && Catalog.isReady catalog && not generating && run.error.IsNone
            && (match activeConvId, activeConversation () with
                | None, _ -> true
                | Some _, Some view -> view.messages.Count = 0
                | _ -> false)
        match chatColumn with
        | :? ConversationWorkspace as surface -> surface.SetStarting startReady
        | _ -> ()
        composer.SetSessionContext(activeConversation () |> Option.map _.config, authenticated, fun () -> this.ShowSessionContext())
        match activeConvId, activeConversation () with
        | None, _ ->
            chat.SetHeaderModel("", "", false)
            chat.SetConversationChrome false
            chat.SetBreadcrumb(None, ignore)
            chat.SetTitle("新对话", false)
            if not authenticated then
                match connectionError with
                | Some err ->
                    // 令牌失效时入口直接叫“重新配对”，而不是泛泛的“重新连接”
                    let entryLabel =
                        match connectionIssue |> Option.bind ConnectionIssue.retry with
                        | Some RePair -> "重新配对"
                        | _ -> "重新连接"
                    chat.ShowEmpty(ChatEmptyState.ConnectionFailed err, Some(entryLabel, fun () -> this.ShowConnectDialog()))
                    composer.SetEnabled(false, sprintf "连接失败：%s" err)
                    composer.SetModelLabel "未连接"
                | None ->
                    if not hasSavedCredentials then
                        chat.ShowEmpty(ChatEmptyState.NotConnected, Some("连接主机", fun () -> this.ShowConnectDialog()))
                        composer.SetEnabled(false, "可以先写草稿，连上主机后再发送。")
                        composer.SetModelLabel "未连接"
                    else
                        chat.ShowEmpty(ChatEmptyState.NotConnected, Some("连接主机", fun () -> this.ShowConnectDialog()))
                        composer.SetEnabled(false, "正在连接主机…草稿已保留。")
                        composer.SetModelLabel "正在连接"
            elif not (Catalog.isReady catalog) || this.Onboarding.IsBusy then
                // 首次运行：卡片就在聊天界面里，不必先进设置页。连接进行中即使目录暂时就绪
                // 也保留卡片，免得探测失败要撤销时它已被收起。
                chat.ShowEmbeddedEmpty this.Onboarding.Root
                this.Onboarding.StartDiscovery()
                composer.SetEnabled(false, "先在上方添加一个模型服务，就能在这里输入。")
                composer.SetModelLabel "未配置模型"
            else
                chat.ShowEmpty(ChatEmptyState.NoConversation, None)
                composer.SetEnabled(true, "")
                match draftModelSelection |> Option.orElse (Catalog.defaultSelection catalog) with
                | Some(providerId, model) ->
                    composer.SetModelLabel(Catalog.describeModel providerId model catalog)
                    composer.SetModelVision(fst (Catalog.visionOf providerId model catalog), Catalog.describeModel providerId model catalog)
                | None ->
                    composer.SetModelLabel "未选择模型"
                    composer.SetModelVision(VisionUnknown, "")
        | Some convId, None ->
            chat.SetConversationChrome true
            // Loading 分级：快照 300ms 内到达就不闪任何 loading chrome，超时才在本地挂骨架；
            // 流式文本本身就是进度，这里不叠加任何页级 loading。
            this.ShowConversationLoadingDeferred convId
            composer.SetEnabled(false, "正在加载对话…")
        | Some convId, Some view ->
            this.DisarmSkeletonLoading()
            let messages = messagesOf view
            let streaming = this.StreamingMessage()
            let summary = activeSummary ()
            chat.SetConversationChrome true
            chat.SetBreadcrumb(ForkLineage.breadcrumb summaries lineage convId, fun parentId -> this.OpenConversation parentId)
            chat.SetTitle((summary |> Option.map (fun s -> s.title) |> Option.defaultValue "对话"), true)
            composer.SetModelLabel(Catalog.describeModel view.config.provider view.config.model catalog)
            composer.SetModelVision(fst (Catalog.visionOf view.config.provider view.config.model catalog), Catalog.describeModel view.config.provider view.config.model catalog)
            chat.SetHeaderModel(
                ModelSwitcher.chipText view.config.provider view.config.model catalog,
                (Catalog.tryProvider view.config.provider catalog |> Option.map _.label |> Option.defaultValue ""),
                authenticated)
            if List.isEmpty messages && streaming.IsNone && run.error.IsNone then
                chat.HideSkeletonLoading()
                chat.ShowEmpty(EmptyConversation, None)
            else
                chat.HideSkeletonLoading()
                chat.HideEmpty()
                // 决策 300：「上一轮」越过已加载历史时要知道主机上还有没有更早的页。
                chat.SetOlderAvailable view.pageHasMore
                let retry () = this.Regenerate()
                // 先同步生成状态：渲染时要据此区分工具调用「执行中」与「结果未知」
                chat.SetGenerating(generating, (if generating then "生成中" else ""))
                chat.SetPartialReply(run.partial |> Option.map (fun p -> p, fun () -> this.ContinueGeneration(convId, p)))
                chat.RenderMessages(
                    messages,
                    streaming,
                    (match run.error with
                     | Some e -> Some(e, retry)
                     | None when not generating && streaming.IsNone && run.partial.IsNone
                                 && ReplyTail.isUnanswered DateTimeOffset.UtcNow messages ->
                         Some(UnansweredReply.error, retry)
                     | None -> None),
                    prefs.fontScale,
                    prefs.autoCollapseReasoning,
                    run.usage,
                    missingAttachments)
                this.TryReveal(convId, view, messages)
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
        Tokens.apply (UiPrefsFile.resolveTheme prefs (this.SystemPrefersDark()))

    // ---- 本地草稿 ----

    member private this.SyncActiveDraftText() =
        if instanceId <> "" && not (isNull (box composer)) then
            (drafts.Get(instanceId, activeConvId)).text <- composer.Text

    member private this.CurrentDraftSnapshot() : DraftSnapshot =
        this.SyncActiveDraftText()
        let savedAtOf (id: Guid) =
            match unsentSavedAt.TryGetValue id with
            | true, t -> t
            | _ ->
                let t = DateTimeOffset.UtcNow
                unsentSavedAt[id] <- t
                t
        let current = DraftPolicy.snapshotOf drafts outbox savedAtOf
        DraftPolicy.mergeForSave storedDrafts (Set.ofSeq restoredInstances) current

    member private this.UpdateDraftSummary() =
        if not (isNull (box settings)) then
            let snapshot = this.CurrentDraftSnapshot()
            let texts = snapshot.drafts |> List.filter (fun d -> not (String.IsNullOrWhiteSpace d.text) || not (List.isEmpty d.attachments)) |> List.length
            let unsent = snapshot.unsent |> List.length
            settings.LocalDrafts.Summary <-
                if not prefs.persistDrafts then "已关闭：不会在本机保存草稿。"
                elif texts = 0 && unsent = 0 then "当前没有保存的草稿。"
                else sprintf "已保存 %d 份草稿、%d 条未确认的消息。" texts unsent

    /// 保存到本机。关闭保存、尚未读完磁盘、或内容没变时什么也不做。
    member this.FlushDrafts() =
        if draftsLoaded && prefs.persistDrafts then
            ParityProbe.hit "drafts.flush"
            draftStore.Save(this.CurrentDraftSnapshot()) |> ignore

    /// 把本机保存的内容放回内存：只处理当前主机，且不覆盖用户这次已经输入的文字。
    member private this.RestoreLocalDrafts() =
        if draftsLoaded && prefs.persistDrafts && instanceId <> "" && restoredInstances.Add instanceId then
            let unsentBefore = outbox.ForInstance instanceId |> List.length
            DraftPolicy.restoreInto storedDrafts instanceId drafts outbox |> ignore
            let unsentRestored = (outbox.ForInstance instanceId |> List.length) - unsentBefore
            for d in storedDrafts.drafts do
                if d.instanceId = instanceId then
                    d.conversationId |> Option.iter (fun id -> knownConversationIds <- knownConversationIds.Add id)
            for u in storedDrafts.unsent do
                if u.instanceId = instanceId then knownConversationIds <- knownConversationIds.Add u.conversationId
            let live = drafts.Get(instanceId, activeConvId)
            if String.IsNullOrWhiteSpace composer.Text && not (String.IsNullOrWhiteSpace live.text) then
                composer.SetText live.text
            composer.SetAttachments((attachmentDraft ()).Items)
            let hasDrafts =
                storedDrafts.drafts |> List.exists (fun d -> d.instanceId = instanceId && (not (String.IsNullOrWhiteSpace d.text) || not (List.isEmpty d.attachments)))
            if hasDrafts || unsentRestored > 0 then
                let needsReselect =
                    storedDrafts.drafts |> List.exists (fun d -> d.instanceId = instanceId && not (List.isEmpty d.attachments))
                let message =
                    if unsentRestored > 0 then "已恢复上次未发送的内容；未确认的消息可重试，不会重复保存。"
                    else "已恢复上次未发送的草稿。"
                toast (if needsReselect then message + "附件需要重新选择。" else message) Info
            this.UpdateDraftSummary()

    /// 清除本地草稿：本机落盘内容、内存里各对话的草稿、未确认的消息一并清空。
    /// 已发送并保存的对话不受影响。
    member private this.ClearLocalDrafts() =
        for (instance, conversation), draft in drafts.Entries |> Seq.toList do
            draft.text <- ""
            for item in draft.attachments.Items |> Seq.toList do
                draft.attachments.Remove item.attachmentId |> ignore
            drafts.Remove(instance, conversation)
        for instance in outbox.AllInstances() do
            outbox.DiscardUnsent instance
        storedDrafts <- DraftSnapshot.empty
        unsentSavedAt.Clear()
        composer.SetText ""
        composer.SetAttachments []
        draftStore.Clear() |> ignore
        this.UpdateDraftSummary()
        this.Render()
        toast "本地草稿已清除。" Success

    /// 列表里消失的对话 = 已被删除：它的草稿与未确认消息已无处可发，随之清理。
    member private this.PruneDeletedConversationDrafts() =
        if instanceId <> "" then
            let listed = summaries |> List.map _.id |> Set.ofList
            let inFlight =
                outbox.ForInstance instanceId
                |> List.filter (fun item -> not (PendingMessage.canRetry item))
                |> List.map _.conversationId
                |> Set.ofList
            let gone = DraftPolicy.deletedConversations listed knownConversationIds inFlight
            for id in gone do
                if activeConvId <> Some id then
                    drafts.Remove(instanceId, Some id)
                outbox.DiscardConversation(instanceId, id)
                storedDrafts <-
                    { drafts = storedDrafts.drafts |> List.filter (fun d -> not (d.instanceId = instanceId && d.conversationId = Some id))
                      unsent = storedDrafts.unsent |> List.filter (fun u -> not (u.instanceId = instanceId && u.conversationId = id)) }
            knownConversationIds <- Set.union (Set.difference knownConversationIds gone) listed

    /// 主机状态行：连接正常时带上「配置有误」提示。
    member private this.UpdateHostStatusLine() =
        if authenticated then
            // 主机地址已在上一行（serverCaption）显示，状态行只说状态，不重复主机名
            let suffix = if settings.Health.ConfigIssue.IsSome then " · 配置有误" else ""
            sidebar.SetConnection(true, "已连接" + suffix)

    member private this.SavePrefs(next: UiPrefs) =
        let themeChanged = next.theme <> prefs.theme
        let draftsToggled = next.persistDrafts <> prefs.persistDrafts
        let motionChanged = next.reduceMotion <> prefs.reduceMotion
        prefs <- next
        UiPrefsFile.save prefs
        if themeChanged then this.ApplyTheme()
        if motionChanged then MotionPolicy.setReduced prefs.reduceMotion
        sidebar.SetShowArchived prefs.showArchived
        // 代码块折行是渲染期读取的默认值，改完要立刻生效
        MarkdownRenderer.DefaultCodeWrap <- prefs.codeWrap
        composer.SetEnterSends prefs.enterSends
        settings.SetPrefs prefs
        // 关闭保存：立即删除本机已存的内容（之后不再写）；重新打开：下一次保存即生效。
        if draftsToggled then
            if not prefs.persistDrafts then draftStore.Clear() |> ignore
            else this.FlushDrafts()
            this.UpdateDraftSummary()
        this.Render()

    // ---- 连接 ----

    member private this.ShowConnectDialog() =
        let handle =
            Dialogs.connectWithClosed
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
                (fun () ->
                    setConnectStatus <- ignore
                    failConnectStatus <- ignore)
        setConnectStatus <- handle.info
        failConnectStatus <- handle.fail
        match connectionIssue with
        | Some issue -> handle.fail issue
        | None ->
            connectionError |> Option.iter handle.info
            // 决策 260：本机从没连过（无令牌、无保存凭据）时，配对码就是唯一可走的路。
            if PairingHint.pairingFirst lastToken hasSavedCredentials connectionError then
                handle.startPairing ()

    /// 记录并呈现一次类型化的连接失败（对话框内联 + 空态入口）。
    member private this.ReportConnectionIssue(issue: ConnectionIssue) =
        connectionIssue <- Some issue
        connectionError <- Some(ConnectIssueText.describe issue)
        failConnectStatus issue

    member private this.Connect() =
        connectAttempt <- connectAttempt + 1
        let attempt = connectAttempt
        authenticated <- false
        connectionError <- None
        settings.Health.Disconnected()
        connectionIssue <- None
        runs.Disconnect()
        exportDialog |> Option.iter (fun dialog -> dialog.Disconnect())
        this.FailOlderPage "连接已断开，更早的消息没有加载。重新连接后点「重试」。"
        outbox.Disconnect instanceId
        commandFeedback.RejectAll()
        configFeedback.RejectAll()
        this.FailUploadingAttachments()
        // 断连后底层下载流已死，不会有 Complete 事件；缓冲必须整体丢弃（R1）。
        downloads.Clear()
        downloadCappedNotified <- false
        this.Render()
        match reconnectCts with
        | Some cts -> cts.Cancel()
        | None -> ()
        reconnectCts <- None
        let url = lastUrl
        let token = lastToken
        let usePairing = pairingRequested
        let start () =
            try client.ConnectWithGenerationAsync(Uri url, CancellationToken.None)
            with ex -> System.Threading.Tasks.Task.FromException<int> ex
        // 决策 291：同进程主机还在后台启动时，等它就绪再连（门已开则照旧立即连接）
        let gate = LocalHostGate.ready
        let connecting =
            if gate.IsCompleted then start ()
            else System.Threading.Tasks.TaskExtensions.Unwrap(gate.ContinueWith(fun (_: System.Threading.Tasks.Task) -> start ()))
        async {
            try
                let! epoch = connecting |> Async.AwaitTask
                // ADR-001 方案 B：版本协商完全 in-band（AG-UI RUN_STARTED.protocolVersion），
                // 客户端不再发送 Hello 握手事件；连接后第一个动作即认证。
                match token with
                | Some value ->
                    let! _ = client.TrySendAtGenerationAsync(epoch, AuthPresent {| token = value |}) |> Async.AwaitTask
                    ()
                | None ->
                    if usePairing then
                        let! _ = client.TrySendAtGenerationAsync(epoch, PairingRequested {| clientName = Some CredentialStore.clientName |}) |> Async.AwaitTask
                        ()
                ui.Post(fun () ->
                    if attempt = connectAttempt && not authenticated then sidebar.SetConnection(false, "正在连接"))
            with ex ->
                ui.Post(fun () ->
                    if attempt = connectAttempt then
                        this.ReportConnectionIssue(ConnectionIssue.ofException ex)
                        sidebar.SetConnection(false, "连接已断开")
                        this.ScheduleReconnect()
                        this.Render())
        }
        |> Async.Start

    /// 网络恢复或页面回到前台（第 213 节）：正在退避等待的重连立刻执行，并把退避归零；
    /// 没有挂起的重连（已连接，或从未拿到令牌）时什么也不做，不另起第二条连接。
    member private this.ResumeReconnectNow() =
        match reconnectCts with
        | Some cts ->
            cts.Cancel()
            reconnectCts <- None
            reconnectDelayMs <- ReconnectBackoff.baseDelayMs
            pairingRequested <- false
            this.Connect()
        | None -> ()

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
                    ui.Post(fun () ->
                        if not cts.IsCancellationRequested then
                            reconnectCts <- None
                            pairingRequested <- false
                            this.Connect())
            }
            |> Async.Start

    // ---- 对话操作 ----

    /// 批量删除后的主视图归宿：删的集合里含当前对话时，从侧栏可见顺序排除待删集合
    /// 取第一个存活对话（整列全删则 None → 欢迎页）。不含当前对话时 None = 不动主视图。
    /// 与单条删除的 NeighborAfterDelete 同一意图，只是口径从「单个 id」扩到「一批 id」。
    member private this.NeighborExcluding(removedIds: Guid list) : Guid option =
        match activeConvId with
        | Some currentId when List.contains currentId removedIds ->
            let removed = Set.ofList removedIds
            sidebar.GetVisibleOrder()
            |> List.tryPick (fun id ->
                match Guid.TryParse id with
                | true, g when not (removed.Contains g) -> Some g
                | _ -> None)
        | _ -> None

    /// §214：登记待删 → 隐去行 → 当前对话落邻位 → 带「撤销」的提示条 → 宽限期后提交。
    member private this.ScheduleDelete(ids: Guid list) =
        let titled =
            ids |> List.map (fun id ->
                id, (summaries |> List.tryFind (fun s -> s.id = id) |> Option.map _.title |> Option.defaultValue "对话"))
        let neighbor = this.NeighborExcluding ids
        let deletingCurrent = Option.exists (fun id -> List.contains id ids) activeConvId
        match ids with
        | [ single ] -> sidebar.FocusAfterDelete(single.ToString())
        | _ -> ()
        let next, batch = PendingDeletion.schedule titled pendingDeletes
        pendingDeletes <- next
        match batch with
        | None -> ()
        | Some batch ->
            sidebar.SetConversations(PendingDeletion.visible summaries pendingDeletes)
            if deletingCurrent then
                this.SelectConversation neighbor
                sidebar.SetActive activeConvId
            let timer = DispatcherTimer(Interval = PendingDeletion.grace)
            timer.Tick.Add(fun _ ->
                timer.Stop()
                this.CommitDelete batch.token)
            timer.Start()
            overlay.ToastWith(
                batch.label,
                Info,
                Some(
                    "撤销",
                    fun () ->
                        timer.Stop()
                        let restored, ids = PendingDeletion.undo batch.token pendingDeletes
                        pendingDeletes <- restored
                        if not (List.isEmpty ids) then
                            sidebar.SetConversations(PendingDeletion.visible summaries pendingDeletes)
                            if deletingCurrent && ids.Length = 1 then this.SelectConversation(Some ids.Head)
                            this.Render()
                            toast (if ids.Length = 1 then "已恢复对话" else sprintf "已恢复 %d 个对话" ids.Length) Success))
            this.Render()

    /// 宽限期到：真正提交删除。被拒的条目在下一次列表快照里自然回到列表。
    member private this.CommitDelete(token: Guid) =
        let next, ids = PendingDeletion.commit token pendingDeletes
        pendingDeletes <- next
        for id in ids do
            let command = DeleteConversation {| invocationId = newInvocation (); conversationId = id |}
            commandFeedback.TrackWithCompletion(
                command,
                None,
                ignore,
                (fun ok -> if not ok then toast "有对话没删掉：主机拒绝了这次请求，它已回到列表。" Warning))
            sendCommand command
        if not (List.isEmpty ids) then
            sidebar.SetConversations(PendingDeletion.visible summaries pendingDeletes)

    member private this.SelectConversation(id: Guid option) =
        if activeConvId <> id then
            (drafts.Get(instanceId, activeConvId)).text <- composer.Text
            activeConvId <- id
            composer.SetText((drafts.Get(instanceId, id)).text)
            composer.SetAttachments((attachmentDraft ()).Items)
            chat.CancelHistoryPrependAnchor()
            // 换对话：滚动状态归零（上一段对话的定位钉住不能带进新对话）
            chat.ResetScrollState()
            pageLoading <- false
            pageRequestedAt <- None
        this.DisarmSkeletonLoading()
        let navBefore = navigation.State
        this.SetCompactNavigation false
        sidebar.SetActive activeConvId
        this.Render()
        // compact 下收起导航会带走焦点（掉到页顶）：交还给输入区；阅读位置由 ChatView 的锚点语义保留。
        if navBefore.compactMode && navBefore.compactNavigationOpen then composer.Focus()

    member private this.MakeConversationCommand(conversationId: Guid) =
        this.MakeConversationCommandWith(conversationId, None)

    member private _.MakeConversationCommandWith(conversationId: Guid, preset: SessionConfig option) =
      match preset with
      | Some config ->
        CreateConversation
            {| invocationId = newInvocation (); conversationId = conversationId; title = "新对话"; config = config |}
      | None ->
        let provider, model =
            // 草稿里显式挑过的模型优先：没有对话时的选择不该被「默认」抹掉。
            draftModelSelection |> Option.orElse (Catalog.defaultSelection catalog) |> Option.defaultValue ("", "")
        let config =
            { SessionConfig.empty with
                provider = provider; model = model
                instructions = catalog.generation.instructions
                temperature = catalog.generation.temperature
                topP = catalog.generation.topP
                maxTokens = catalog.generation.maxTokens }
        CreateConversation
            {| invocationId = newInvocation (); conversationId = conversationId; title = "新对话"; config = config |}

    member private this.CreateConversation() : Guid option = this.CreateConversationWith None

    member private this.CreateConversationWith(preset: SessionConfig option) : Guid option =
        if not authenticated then
            toast "连接主机后才能新建对话；草稿会保留。" Warning
            None
        elif not (Catalog.isReady catalog) then
            toast "先添加一个模型服务。" Warning
            this.ShowAddProvider()
            None
        else
            let conversationId = Guid.CreateVersion7()
            // 草稿选择已在新对话的 config 里落地，暂存位清掉，
            // 否则下一个新对话会接着用上一个的残留（对话已有自己的 config）。
            draftModelSelection <- None
            this.SelectConversation(Some conversationId)
            sendCommandWithCompletion
                (this.MakeConversationCommandWith(conversationId, preset))
                None
                (fun () -> send (ObserveConversation {| conversationId = conversationId |}))
                (fun ok ->
                    if not ok && activeConvId = Some conversationId then this.SelectConversation None)
            // 新对话落定：焦点有家，直接进输入区（D3/D4）。
            composer.Focus()
            Some conversationId

    // ---- 对话预设（决策 431–436）----

    /// 套用预设：当前对话还是空的就直接改它的配置（不留下一段空对话），否则新建一段。
    member private this.ApplyPreset(preset: PresetInfo) =
        if not authenticated then toast "连接主机后才能使用预设。" Warning
        else
            let config, note = Presets.toSessionConfig catalog preset
            let emptyActive =
                match activeConvId, activeConversation () with
                | Some convId, Some view when List.isEmpty (messagesOf view) && not (runs.Get(Some convId)).running ->
                    Some(convId, view)
                | _ -> None
            match emptyActive with
            | Some(convId, view) ->
                let command =
                    UpdateConversationConfig {| invocationId = newInvocation (); conversationId = convId; config = config |}
                sendCommandWithFeedback command (Some(sprintf "已套用预设「%s」" preset.name)) (fun () ->
                    view.config <- config
                    this.Render())
                composer.Focus()
            | None ->
                match this.CreateConversationWith(Some config) with
                | Some _ -> toast (sprintf "已用预设「%s」新建对话" preset.name) Success
                | None -> ()
            note |> Option.iter (fun n -> toast n Warning)

    /// 「存为预设…」：取当前对话的完整会话配置，名字默认是对话标题。
    /// 决策 442：主机明确声明不支持时，发请求前就说清楚，而不是等它石沉大海。
    member private _.ProbeProvider(providerId: string) =
        let requestId = newInvocation ()
        pendingProbes[requestId] <- (providerId, DateTimeOffset.UtcNow)
        send (ProviderProbeRequest {| requestId = requestId; providerId = providerId |})

    /// 决策 443/444：到点仍没回应的请求统一收尾。
    member private this.ExpireHostRequests(now: DateTimeOffset) : bool =
        let timeout = MotionLedger.hostRequestTimeout
        let message = HostFeatures.noResponseMessage hostFeatures
        let expiredConfig = configFeedback.Expire(now, timeout)
        let expiredCommands = commandFeedback.Expire(now, timeout)
        for f in expiredConfig do f.onCompleted false
        for f in expiredCommands do f.onCompleted false
        if not (List.isEmpty expiredConfig && List.isEmpty expiredCommands) then toast message Warning
        match searchSentAt with
        | Some at when now - at >= timeout ->
            searchSentAt <- None
            latestSearchQueryId <- None
            sidebar.ApplySearchFailure(sidebar.SearchQueryText, message)
        | _ -> ()
        let dueProbes = [ for KeyValue(id, (pid, at)) in pendingProbes do if now - at >= timeout then id, pid ]
        for (id, pid) in dueProbes do
            pendingProbes.Remove id |> ignore
            let err = Some message
            if not (this.Onboarding.ApplyProbe(pid, false, [], err)) then settings.ApplyProbe(pid, false, [], err)
        not (List.isEmpty expiredCommands)

    member private _.RequireFeature(feature: string) : bool =
        if HostFeatures.supports hostFeatures feature then true
        else
            toast (HostFeatures.unsupportedMessage feature) Warning
            false

    member private this.SaveConversationAsPreset() =
        if not (this.RequireFeature HostFeatures.Presets) then () else
        match activeConversation () with
        | Some view when authenticated ->
            let title = activeSummary () |> Option.map (fun s -> s.title) |> Option.defaultValue ""
            let config = view.config
            Dialogs.prompt overlay "存为预设" "预设名称" (Presets.suggestName catalog.presets title) "保存" (fun name onCompleted ->
                sendConfigWithFeedback
                    (sprintf "已存为预设「%s」，新建对话时可以套用" (name.Trim()))
                    (fun requestId -> ConfigUpsertPreset {| requestId = requestId; preset = Presets.payloadOf None name config |})
                    onCompleted)
        | _ -> toast "打开一段对话后才能存为预设。" Warning

    member private this.RenamePreset(preset: PresetInfo) =
        if this.RequireFeature HostFeatures.Presets then
            Dialogs.prompt overlay "重命名预设" "预设名称" preset.name "保存" (fun name onCompleted ->
                sendConfigWithFeedback
                    "预设已重命名"
                    (fun requestId ->
                        ConfigUpsertPreset {| requestId = requestId; preset = Presets.payloadOf (Some preset.id) name (Presets.ofPreset preset) |})
                    onCompleted)

    member private this.DeletePreset(preset: PresetInfo) =
        if this.RequireFeature HostFeatures.Presets then
            Dialogs.confirm overlay "删除预设" (sprintf "「%s」将从这台主机上移除。已经用它建好的对话不受影响。" preset.name) "删除" (fun () ->
                sendConfigWithFeedback
                    "预设已删除"
                    (fun requestId -> ConfigDeletePreset {| requestId = requestId; presetId = preset.id |})
                    ignore)

    /// 预设入口随目录同步：侧栏下拉、空态芯片、顶栏菜单。没有预设时前两处都不出现。
    member private this.SyncPresetSurfaces() =
        let entries =
            catalog.presets
            |> List.map (fun p -> p.name, Presets.describe catalog p, (fun () -> this.ApplyPreset p))
        sidebar.SetPresets(entries, fun anchor items ->
            Menu.show overlay anchor false
                [ for (name, _, run) in items -> MenuEntry.create name run ])
        chat.SetPresetStarters(entries)
        chat.SetSavePresetAction(
            if HostFeatures.supports hostFeatures HostFeatures.Presets then
                Some(fun () -> this.SaveConversationAsPreset())
            else None)

    /// 点击一条搜索命中：打开那段对话，并滚动到命中的消息（目标在更早历史里则先加载）。
    member private this.OpenSearchHit(hit: SearchHit) =
        pendingReveal <- Some(hit.conversationId, RevealMessage hit.messageId)
        this.OpenConversation hit.conversationId
        // 已经在这段对话里：不会有新快照来触发 Render，主动重试一次
        if activeConversation().IsSome then this.Render()

    /// 在同一消息位置的另一个版本（原文或某个分叉）之间切换，并滚到该位置。
    member private this.SwitchVersion(target: Guid) =
        let reveal =
            match lineage.TryFind target with
            | Some l -> l.forkFirstMessageId |> Option.map RevealMessage
            | None ->
                // 回到原文：定位到当前分叉的分叉点之后那一条
                activeConvId
                |> Option.bind lineage.TryFind
                |> Option.map (fun l -> RevealAfter l.forkBaseMessageId)
        pendingReveal <- reveal |> Option.map (fun r -> target, r)
        this.OpenConversation target
        if activeConvId = Some target && activeConversation().IsSome then this.Render()

    /// 决策 231：打开消息链接——与搜索命中同一路径（滚到那条消息并短暂着色）。
    /// 对话列表还没到时先记下，快照到达后再开；对话已不存在则如实说。
    member private this.OpenMessageLink((convId: Guid, commitId: uint64)) =
        if not authenticated || List.isEmpty summaries && not listReceived then
            pendingLink <- Some(convId, commitId)
        else
            pendingLink <- None
            if summaries |> List.exists (fun s -> s.id = convId) then
                pendingReveal <- Some(convId, RevealMessage commitId)
                this.OpenConversation convId
                if activeConversation().IsSome then this.Render()
            else
                toast "链接指向的对话不存在或已删除。" Warning

    /// PWA：页面地址带 `#open?…` 时打开（启动、以及运行中地址栏改了片段）。
    member private this.CheckPageLink() =
        if OperatingSystem.IsBrowser() then
            try
                let href = BrowserPage.Url()
                if href <> lastPageLink then
                    lastPageLink <- href
                    MessageLink.tryParse href
                    |> Option.iter (fun link ->
                        ParityProbe.hit "deeplink.open"
                        this.OpenMessageLink link)
            with _ -> ()

    member private this.OpenConversation(id: Guid) =
        this.SelectConversation(Some id)
        send (ObserveConversation {| conversationId = id |})
        // 对话激活（键盘/可见顺序/分叉入口统一走这里）：焦点回家到输入区（D3/D4）。
        composer.Focus()

    member private this.DispatchPendingMessage(item: PendingMessage) =
        let id = PendingMessage.invocationId item
        if item.instanceId <> instanceId || not authenticated then
            outbox.SetState(id, UnconfirmedMessage "请连接原主机后重试。")
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
                            outbox.SetState(id, UnconfirmedMessage "对话尚未确认创建；原文已保留。")
                            this.Render())
            | _ when not (state.Conversations.ContainsKey item.conversationId) ->
                outbox.SetState(id, PreparingConversation)
                send (ObserveConversation {| conversationId = item.conversationId |})
            | _ ->
                outbox.SetState(id, SendingMessage)
                sendCommand item.command

    /// 决策 230：把半截回复落盘并从它接着生成。与「重试」相反，已写出的部分保留。
    member private this.ContinueGeneration(convId: Guid, partial: MessageView) =
        if not authenticated then toast "连接已断开，重新连接后可继续生成。" Warning
        elif not (this.RequireFeature HostFeatures.ContinueResponse) then ()
        else
            runs.ClearError convId
            sendCommandWithCompletion
                (ContinueResponse
                    {| invocationId = newInvocation ()
                       conversationId = convId
                       messageJson = ConversationRuns.PartialMessageJson partial |})
                None ignore ignore
            this.Render()
            composer.Focus()

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
            toast "还没有可用的模型，先添加一个模型服务。" Warning
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
            let fire () =
                runs.ClearError convId
                sendCommand (RegenerateResponse {| invocationId = newInvocation (); conversationId = convId |})
                this.Render()
                // 错误卡片随重试卸载：焦点无处可去会掉到页顶，先交还给输入区。
                composer.Focus()
            // 重新生成不可恢复：主机会删掉尾部整段回复链再重新生成，所以有内容时必须确认
            // （与删对话 / 删消息同规则，kelivo 同样默认确认）。但生成失败、尾部什么都没
            // 留下时，确认框只是在「重试」上多加一步，问的还是一句不成立的「将丢弃当前回复」
            // ——直接重试（决策 221）。
            let losable =
                activeConversation ()
                |> Option.map (fun view -> ReplyTail.hasLosableContent (messagesOf view))
                |> Option.defaultValue false
            if not losable then fire ()
            else
                Dialogs.confirm
                    overlay
                    "重新生成"
                    "将丢弃当前这条回复并重新生成。此操作无法撤销。"
                    "重新生成"
                    fire

    /// Ctrl+Shift+G：与卡片菜单的「重新生成」同一路径；生成中、未连接、尾部不是回复时不动作。
    member private this.RegenerateLatest() =
        match activeConversation () with
        | Some view when not (runs.Get activeConvId).generationId.IsSome ->
            let messages = messagesOf view
            match List.tryLast messages with
            | Some last when last.role = "user" || not (List.isEmpty (ReplyTail.chain messages)) ->
                this.Regenerate()
            | _ -> toast "这段对话还没有可以重新生成的回复" Warning
        | Some _ -> toast "正在生成；先按 Esc 停止，再重新生成" Info
        | None -> toast "没有打开的对话" Warning

    /// Ctrl+Shift+C：复制最新一条有正文的回复。
    member private this.CopyLatestReply() =
        match activeConversation () |> Option.bind (fun view -> ReplyTail.latestText (messagesOf view)) with
        | Some text -> copyToClipboard text
        | None -> toast "还没有可以复制的回复" Warning

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
                    send (ObserveConversation {| conversationId = newId |}))
                // 分叉落定与新建/打开对话同一归宿：焦点回家到输入区（D3/D4）。
                // 漏这一句时，分叉后焦点悬在已关闭的编辑弹窗原位（对话框关掉时
                // 焦点宿主一并消失），键盘用户得摸鼠标点回输入区；NewConversation
                // 与 OpenConversation 两条兄弟路径都带着这条无条件的一行。
                // 同步而非 Post：editText 的 submit 先 CloseDialog() 再 onConfirm，
                // 还原焦点发生在内、发生在前，这一行随后落地不为空转（实测验证）。
                composer.Focus())
        | _ -> toast "当前没有可分叉的对话。" Warning

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
            ui.Post(fun () ->
                try
                    if control.IsEnabled
                       && control.IsEffectivelyVisible
                       && not (isNull (TopLevel.GetTopLevel control)) then
                        control.Focus() |> ignore
                    else fallback ()
                with _ -> fallback ())
        | None -> fallback ()

    /// 首次运行卡片连接成功、目录已就绪：选定模型，输入框预填第一句话并聚焦（用户只需回车）。
    member private this.CompleteOnboarding() =
        match this.Onboarding.TakeConnected catalog with
        | Some(providerId, model) ->
            this.Onboarding.Reset()
            draftModelSelection <- Some(providerId, model)
            this.Render()
            if String.IsNullOrWhiteSpace composer.Text then composer.SetText OnboardingRules.FirstPrompt
            composer.Focus()
            toast (sprintf "已连接 %s" (Catalog.describeModel providerId model catalog)) Success
        | None -> ()

    member private this.ShowAddProvider() =
        enteredSettingsForFirstProvider <- not (Catalog.isReady catalog)
        this.ShowSettings()
        ui.Post(
            (fun () ->
                let rec findAddButton (control: Control) : ActionBorder option =
                    match control with
                    | :? ActionBorder as ab when
                        (match ab.Child with
                         | :? TextBlock as tb -> tb.Text = "添加模型服务"
                         | _ -> false) -> Some ab
                    | :? Panel as panel ->
                        panel.Children |> Seq.tryPick findAddButton
                    | :? ContentControl as cc ->
                        match cc.Content with
                        | :? Control as c -> findAddButton c
                        | _ -> None
                    | :? Border as b ->
                        if not (isNull b.Child) then findAddButton b.Child else None
                    | _ -> None
                match findAddButton settings with
                | Some btn -> btn.InvokeFromAutomation()
                | None -> ()),
            DispatcherPriority.Render)

    member private this.ShowSettings() =
        settingsReturnFocus <- this.CurrentFocusControl()
        settingsHost.IsVisible <- true
        workspace.IsVisible <- false
        // 打开即有焦点归宿：分区导航按钮。刚翻可见性时控件还没过布局，
        // 直接 Focus 会落空，所以延一帧到 Render 之后。
        ui.Post(
            (fun () -> settings.FocusInitial()),
            DispatcherPriority.Render)
        send CatalogRequest

    member private this.CloseSettings() =
        enteredSettingsForFirstProvider <- false
        settingsHost.IsVisible <- false
        workspace.IsVisible <- true
        this.RestoreSettingsFocus()

    member private this.ShowModelPicker(anchor: Control) =
        // 决策 240：顶栏芯片、输入区芯片与 Ctrl+M 共用同一个可搜索的快速切换。
        match activeConvId, activeConversation () with
        | Some convId, Some view ->
            let options = ModelSwitcher.options catalog (Some(view.config.provider, view.config.model))
            if List.isEmpty options then
                // 空态不是死路：点模型入口就直接进入“添加模型服务”，与空态主按钮同一出口。
                toast "还没有可用的模型，先添加一个模型服务。" Warning
                this.ShowAddProvider()
            else
                let generating = (runs.Get(Some convId)).running
                let title =
                    if generating then "正在生成的这条回复不受影响，切换从下一条开始。"
                    else "切换本对话的模型"
                ModelSwitcherView.show overlay anchor false options title (fun picked ->
                    let config = { view.config with provider = picked.providerId; model = picked.model }
                    let command =
                        UpdateConversationConfig
                            {| invocationId = newInvocation ()
                               conversationId = convId
                               config = config |}
                    sendCommandWithFeedback command (Some(ModelSwitcher.switchedToast picked.providerLabel picked.model)) (fun () ->
                        view.config <- config
                        this.Render()))
        | _ ->
            // 没有对话也要能挑模型：这一刻输入区是启用的（Render 的空态分支），
            // 芯片上正亮着默认模型名，点了却弹「先选择一个对话」是把可用面拔掉。
            // 选择只落草稿暂存位，建对话时作为 config 的 provider/model 起手。
            let current = draftModelSelection |> Option.orElse (Catalog.defaultSelection catalog)
            let options = ModelSwitcher.options catalog current
            if List.isEmpty options then
                toast "还没有可用的模型，先添加一个模型服务。" Warning
                this.ShowAddProvider()
            else
                ModelSwitcherView.show overlay anchor false options "新对话使用的模型" (fun picked ->
                    draftModelSelection <- Some(picked.providerId, picked.model)
                    composer.SetModelLabel(Catalog.describeModel picked.providerId picked.model catalog)
                    toast (sprintf "已选择 %s · %s" picked.providerLabel picked.model) Info)

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
                        (ParityProbe.hit "file.open"; top.StorageProvider).OpenFilePickerAsync(
                            FilePickerOpenOptions(
                                AllowMultiple = true,
                                Title = "添加附件",
                                FileTypeFilter =
                                    [ for (label, patterns, mimes) in MediaTypes.pickerFilters ->
                                          FilePickerFileType(label, Patterns = patterns, MimeTypes = mimes) ]))
                        |> Async.AwaitTask
                    for file in picked do
                        use! stream = file.OpenReadAsync() |> Async.AwaitTask
                        use buffer = new MemoryStream()
                        do! stream.CopyToAsync buffer |> Async.AwaitTask
                        let bytes = buffer.ToArray()
                        ui.Post(fun () ->
                            if authenticated && instanceId = ownerInstance && client.ConnectionGeneration = ownerConnection then
                                this.BeginUpload(owner, file.Name, bytes)
                            else toast "连接已断开，请重新选择附件；文件没有发送到其他主机。" Warning)
                    ui.Post(fun () -> composer.Focus())
                with ex ->
                    ui.Post(fun () ->
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
                            ui.Post(fun () ->
                                if authenticated && instanceId = ownerInstance && client.ConnectionGeneration = ownerConnection then
                                    this.BeginUpload(owner, sf.Name, bytes)
                                else toast "连接已断开，请重新选择附件；文件没有发送到其他主机。" Warning)
                        | _ ->
                            let path = item.TryGetLocalPath()
                            if not (String.IsNullOrEmpty path) && File.Exists path then
                                let bytes = File.ReadAllBytes path
                                let name = Path.GetFileName path
                                ui.Post(fun () ->
                                    if authenticated && instanceId = ownerInstance && client.ConnectionGeneration = ownerConnection then
                                        this.BeginUpload(owner, name, bytes)
                                    else toast "连接已断开，请重新选择附件；文件没有发送到其他主机。" Warning)
                    ui.Post(fun () -> composer.Focus())
                with ex ->
                    ui.Post(fun () ->
                        toast (sprintf "读取拖入文件失败：%s" ex.Message) Failure
                        composer.Focus())
            }
            |> Async.Start

    member private this.HandleClipboardPaste() : bool =
        ParityProbe.hit "clipboard.paste"
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
                                        ui.Post(fun () -> this.DropFiles(List.ofArray files))
                                with ex ->
                                    ui.Post(fun () ->
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
                                        ui.Post(fun () ->
                                            if authenticated then this.BeginUpload(owner, name, bytes)
                                            else toast "连接已断开，请重新选择附件；文件没有发送到其他主机。" Warning
                                            composer.Focus())
                                with ex ->
                                    ui.Post(fun () ->
                                        toast (sprintf "读取剪贴板图片失败：%s" ex.Message) Failure
                                        composer.Focus())
                            } |> Async.Start
                            true
                        else false
                    else false
            with _ ->
                false

    member private this.BeginUpload(owner: AttachmentDraftController, fileName: string, bytes: byte[]) =
        // 决策 332：超大图片先在后台缩小再走原流程；缩小期间不占界面线程。
        if ImageShrink.needsShrink fileName bytes && int64 bytes.Length <= 64L * 1024L * 1024L then
            let ownerInstance = instanceId
            let ownerConnection = client.ConnectionGeneration
            toast (sprintf "「%s」太大，正在自动缩小…" fileName) Info
            async {
                let! result = ImageShrink.shrinkAsync fileName bytes
                ui.Post(fun () ->
                    if not (authenticated && instanceId = ownerInstance && client.ConnectionGeneration = ownerConnection) then
                        toast "连接已断开，请重新选择附件；文件没有发送到其他主机。" Warning
                    else
                        match result with
                        | Error message -> toast message Warning
                        | Ok None -> this.BeginUploadCore(owner, fileName, bytes, None)
                        | Ok(Some shrunk) ->
                            toast (sprintf "「%s」%s" fileName (ImageShrink.note shrunk)) Info
                            this.BeginUploadCore(owner, shrunk.fileName, shrunk.bytes, Some shrunk))
            }
            |> Async.Start
        else this.BeginUploadCore(owner, fileName, bytes, None)

    member private this.BeginUploadCore(owner: AttachmentDraftController, fileName: string, bytes: byte[], shrunk: ShrunkImage option) =
        match MediaTypes.validate fileName (int64 bytes.Length) with
        | Error message -> toast message Warning
        | Ok() ->
            let sha = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
            let mediaType = MediaTypes.ofFileName fileName
            let attachmentId = Guid.CreateVersion7()
            if MediaTypes.isModelImage mediaType then composer.SetThumbnail(attachmentId, bytes)
            match shrunk with
            | Some r -> composer.SetShrinkNote(attachmentId, ImageShrink.note r)
            | None -> ()
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
                        (ParityProbe.hit "file.save"; top.StorageProvider).SaveFilePickerAsync(FilePickerSaveOptions(SuggestedFileName = fileName))
                        |> Async.AwaitTask
                    if not (isNull file) then
                        use! stream = file.OpenWriteAsync() |> Async.AwaitTask
                        do! stream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                        ui.Post(fun () -> toast (sprintf "已保存 %s" fileName) Success)
                with ex ->
                    ui.Post(fun () -> toast (sprintf "保存失败：%s" ex.Message) Failure)
            }
            |> Async.Start

    /// 决策 313：导入对话。未连接时不打开（导入要写到主机上）。
    member private this.ImportConversation() =
        if not authenticated then toast "连接主机后才能导入对话。" Warning
        elif not (this.RequireFeature HostFeatures.ConversationImport) then ()
        elif importDialog |> Option.exists (fun d -> d.IsOpen) then ()
        else
            let ownerInstance = instanceId
            let dialog =
                ConversationImportDialog(
                    overlay,
                    topLevel,
                    fun preview onDone ->
                        if not authenticated || instanceId <> ownerInstance then onDone false
                        else
                            let conversationId = Guid.CreateVersion7()
                            let config =
                                match this.MakeConversationCommand conversationId with
                                | CreateConversation d -> d.config
                                | _ -> SessionConfig.empty
                            let messages = System.Text.Json.Nodes.JsonArray()
                            for m in preview.messages do messages.Add(m.DeepClone())
                            let cmd =
                                ImportConversation
                                    {| invocationId = newInvocation ()
                                       conversationId = conversationId
                                       title = ConversationTransfer.importedTitle preview.title
                                       config = config
                                       messages = messages |}
                            sendCommandWithCompletion
                                cmd
                                (Some(sprintf "已导入「%s」，共 %d 条消息" preview.title preview.messages.Length))
                                (fun () ->
                                    this.OpenConversation conversationId
                                    send (ObserveConversation {| conversationId = conversationId |}))
                                onDone)
            importDialog <- Some dialog
            dialog.Show()

    member private this.ExportConversation(summary: ConversationSummary) =
        if not authenticated then toast "连接主机后才能读取完整历史。" Warning
        elif not (this.RequireFeature HostFeatures.ConversationExport) then ()
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
                    topLevel,
                    ConversationTransfer.lineageOf summaries lineage summary.id)
            exportDialog <- Some dialog
            dialog.Show()

    member private this.CycleTheme() =
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
            Info

    member private this.FocusSidebarSearch(query: string option) =
        let state = navigation.EnsureSearchVisible()
        mainLayout.Apply state
        if not state.compactMode && prefs.sidebarCollapsed <> state.sidebarCollapsed then
            prefs <- { prefs with sidebarCollapsed = state.sidebarCollapsed }
            UiPrefsFile.save prefs
        query |> Option.iter sidebar.SetSearchText
        sidebar.FocusSearch()

    /// 决策 370–379：命令面板条目。每次输入现取，对话列表与当前对话状态永远是新的。
    member private this.PaletteItems() : PaletteItem list =
        let act id title subtitle keywords shortcut =
            { id = "act:" + id; kind = PaletteAction; title = title; subtitle = subtitle; keywords = keywords; shortcut = shortcut }
        let hasConv = activeConvId.IsSome
        let actions =
            [ act "new" "新建对话" "" [ "new"; "chat"; "xinjian" ] (Some "Ctrl / ⌘ + N")
              act "model" "切换模型" (if hasConv then "本对话" else "新对话使用的模型") [ "model"; "switch" ] (Some "Ctrl / ⌘ + M")
              if hasConv then
                  act "export" "导出当前对话" "Markdown 或 JSON" [ "export"; "download" ] (Some "Ctrl / ⌘ + Shift + E")
                  act "fork" "从最新提问分叉" "编辑后另开一个分叉" [ "fork"; "branch"; "分支" ] None
                  act "copy-reply" "复制最新回复" "" [ "copy" ] (Some "Ctrl / ⌘ + Shift + C")
                  act "regenerate" "重新生成最新回复" "" [ "regenerate"; "retry"; "重试" ] (Some "Ctrl / ⌘ + Shift + G")
              for p in catalog.presets do
                  { id = "preset:" + p.id; kind = PaletteAction; title = "用预设新建：" + p.name
                    subtitle = Presets.describe catalog p; keywords = [ "preset"; "yushe"; "ys"; p.name ]; shortcut = None }
              if hasConv && HostFeatures.supports hostFeatures HostFeatures.Presets then
                  act "save-preset" "存为预设" "记下本对话的模型、指令、参数和工具" [ "preset"; "save"; "yushe" ] None
              if HostFeatures.supports hostFeatures HostFeatures.ConversationImport then
                act "import" "导入对话" "从导出的 JSON 文件" [ "import"; "upload" ] None
              act "theme" "切换主题" (sprintf "当前：%s" (ThemePreference.label prefs.theme)) [ "theme"; "dark"; "light"; "深色"; "浅色" ] (Some "Ctrl / ⌘ + Shift + S")
              act "sidebar" "显示 / 隐藏侧栏" "" [ "sidebar" ] (Some "Ctrl / ⌘ + B")
              act "shortcuts" "键盘快捷键" "" [ "shortcuts"; "keyboard"; "help"; "帮助" ] (Some "F1")
              act "settings" "打开设置" "" [ "settings"; "preferences" ] (Some "Ctrl / ⌘ + ,")
              act "add-provider" "添加模型服务" "" [ "provider"; "api key"; "密钥" ] None ]
        let sectionItems =
            SettingsSection.all
            |> List.map (fun section ->
                { id = "set:" + (string section).ToLowerInvariant()
                  kind = PaletteSetting
                  title = "设置 › " + SettingsSection.label section
                  subtitle = SettingsSection.scope section
                  keywords = [ string section; SettingsSection.label section ]
                  shortcut = None })
        let settingItems =
            [ "appearance", "主题", [ "theme" ]
              "appearance", "消息字号", [ "font"; "字体" ]
              "appearance", "发送键（Enter / Ctrl / ⌘ + Enter）", [ "enter"; "send" ]
              "appearance", "减弱动态效果", [ "motion"; "动画" ]
              "appearance", "本机保存草稿", [ "draft" ]
              "appearance", "代码块折行", [ "wrap"; "code" ]
              "generation", "默认模型与温度", [ "temperature"; "default model" ]
              "generation", "导入对话", [ "import" ]
              "devices", "配对新设备", [ "pair"; "qr"; "二维码" ]
              "tools", "工具审批", [ "approval"; "tool" ] ]
            |> List.map (fun (section, title, keywords) ->
                { id = "set:" + section + ":" + title
                  kind = PaletteSetting
                  title = title
                  subtitle = "设置 › " + (SettingsSection.all |> List.find (fun s -> (string s).ToLowerInvariant() = section) |> SettingsSection.label)
                  keywords = keywords
                  shortcut = None })
        let conversations =
            summaries
            |> List.filter (fun s -> not s.archived || prefs.showArchived)
            |> List.map (fun s ->
                { id = "conv:" + string s.id
                  kind = PaletteConversation
                  title = (if String.IsNullOrWhiteSpace s.title then "新对话" else s.title)
                  subtitle =
                      let tags =
                          [ (s.pinned, "已置顶"); (s.archived, "已归档"); (s.isFork, "分叉") ]
                          |> List.filter fst |> List.map snd
                      String.Join(" · ", tags @ [ s.updatedAt.ToLocalTime().ToString("M月d日 HH:mm") ])
                  keywords = []
                  shortcut = None })
        actions @ sectionItems @ settingItems @ conversations

    member private this.RunPaletteItem(item: PaletteItem, query: string) =
        paletteRecent <- CommandPalette.pushRecent item.id paletteRecent
        PaletteRecent.saveRecent paletteRecent
        let closeDrawer () =
            if navigation.State.compactMode && navigation.State.compactNavigationOpen then this.SetCompactNavigation false
        let sectionOf (key: string) =
            SettingsSection.all |> List.tryFind (fun s -> (string s).ToLowerInvariant() = key)
        match item.id with
        | id when id = CommandPalette.fullTextId -> this.FocusSidebarSearch(Some(query.Trim()))
        | id when id.StartsWith "conv:" ->
            match Guid.TryParse(id.Substring 5) with
            | true, convId -> closeDrawer (); this.OpenConversation convId
            | _ -> ()
        | id when id.StartsWith "set:" ->
            let key = id.Substring(4).Split(':')[0]
            closeDrawer ()
            this.ShowSettings()
            sectionOf key |> Option.iter settings.ShowSection
        | "act:new" -> closeDrawer (); this.CreateConversation() |> ignore
        | id when id.StartsWith "preset:" ->
            match catalog.presets |> List.tryFind (fun p -> "preset:" + p.id = id) with
            | Some p -> closeDrawer (); this.ApplyPreset p
            | None -> toast "这个预设已经不存在。" Warning
        | "act:save-preset" -> this.SaveConversationAsPreset()
        | "act:model" ->
            closeDrawer ()
            this.ShowModelPicker(if chat.HeaderModelVisible then chat.HeaderModelAnchor else composer.ModelAnchor())
        | "act:export" ->
            match activeSummary () with
            | Some summary -> this.ExportConversation summary
            | None -> toast "没有打开的对话可导出" Warning
        | "act:fork" ->
            match activeConversation () with
            | Some view ->
                let seed = messagesOf view |> List.filter MessageView.isUser |> List.tryLast |> Option.map (fun m -> m.text) |> Option.defaultValue ""
                this.ForkFrom(None, seed)
            | None -> toast "当前没有可分叉的对话。" Warning
        | "act:copy-reply" -> this.CopyLatestReply()
        | "act:regenerate" -> closeDrawer (); this.RegenerateLatest()
        | "act:import" -> this.ImportConversation()
        | "act:theme" -> this.CycleTheme()
        | "act:sidebar" -> this.ToggleSidebar()
        | "act:shortcuts" -> Dialogs.shortcuts overlay
        | "act:settings" -> closeDrawer (); this.ShowSettings()
        | "act:add-provider" -> closeDrawer (); this.ShowAddProvider()
        | _ -> ()

    member this.ShowCommandPalette() =
        if not overlay.IsDialogOpen then
            if overlay.IsPopupOpen then overlay.ClosePopup()
            let content, search =
                CommandPaletteView.build
                    (fun () -> this.PaletteItems())
                    (fun () -> paletteRecent)
                    (fun item query ->
                        overlay.CloseDialog()
                        // 先让焦点归还、对话层关闭，再执行（执行可能再开对话框或切页面）。
                        ui.Post(fun () -> this.RunPaletteItem(item, query)))
            overlay.ShowDialog(content, 560.0, initialFocus = search, scrollWholeContent = false)

    member this.ToggleSidebar() =
        let state = navigation.ToggleSidebar()
        mainLayout.Apply state
        if state.compactMode then
            // compact 抽屉开关的焦点归宿：打开进抽屉，收起回输入区（D3/D4）。
            if state.compactNavigationOpen then sidebar.FocusSearch()
            else composer.Focus()
        else
            // 桌面态折叠的焦点归宿由 MainLayout.Apply 收口（侧栏隐藏的必然副作用），
            // 与 compact 收起分支同一归宿——焦点回输入区。
            prefs <- { prefs with sidebarCollapsed = state.sidebarCollapsed }
            UiPrefsFile.save prefs

    member this.HandleShortcut(e: KeyEventArgs) =
        let shortcut = ShortcutRouter.resolveKey (KeyShortcut.ofEvent e)
        (match shortcut with
         | ShortcutAction.NoShortcut -> ()
         | _ -> ParityProbe.hit "shortcut.resolved")
        match shortcut with
        | ShortcutAction.Escape ->
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
        | ShortcutAction.NoShortcut -> ()
        | _ when overlay.IsDialogOpen || overlay.IsPopupOpen -> ()
        | ShortcutAction.ToggleSidebar -> e.Handled <- true; this.ToggleSidebar()
        | ShortcutAction.CloseWindow ->
            // 与右上角叉号同一路径：走 Window.Close 才会触发 MainWindow.Closing
            // 里的几何持久化，快捷键关窗不会丢窗口位置。
            // PWA 没有 Window（决策 48：browser 宿主下根视图是 Control），
            // 浏览器自身的关闭语义不由我们代理，这里安全降级为空操作。
            match topLevel () with
            | :? Window as window ->
                e.Handled <- true
                window.Close()
            | _ -> ()
        | ShortcutAction.NewConversation -> e.Handled <- true; this.CreateConversation() |> ignore
        | ShortcutAction.FocusSearch -> e.Handled <- true; this.FocusSidebarSearch None
        | ShortcutAction.OpenSettings -> e.Handled <- true; this.ShowSettings()
        | ShortcutAction.OpenModelPicker ->
            e.Handled <- true
            // 焦点在输入框时不必先找到左下角芯片：锚点取芯片本身，菜单位置与点击一致。
            this.ShowModelPicker(if chat.HeaderModelVisible then chat.HeaderModelAnchor else composer.ModelAnchor())
        | ShortcutAction.JumpExchange direction ->
            e.Handled <- true
            chat.JumpExchange direction
        | ShortcutAction.ScrollToBeginning ->
            e.Handled <- true
            // 锚点作废由入口自己负责（见 ScrollToBeginning）：这里是快捷键，
            // 不需要 shell 再补一遍。
            chat.SmoothScrollToBeginning()
        | ShortcutAction.ScrollToEnd ->
            e.Handled <- true
            chat.SmoothScrollToEnd()
        | ShortcutAction.FocusComposer ->
            e.Handled <- true
            // 与重新生成/重试后「焦点回家」同一个入口（Composer.Focus 自带线程与可见性守卫）。
            composer.Focus()
        | ShortcutAction.ToggleTheme -> e.Handled <- true; this.CycleTheme()
        | ShortcutAction.OpenCommandPalette -> e.Handled <- true; this.ShowCommandPalette()
        | ShortcutAction.CopyLastReply -> e.Handled <- true; this.CopyLatestReply()
        | ShortcutAction.RegenerateLast -> e.Handled <- true; this.RegenerateLatest()
        | ShortcutAction.UndoPending ->
            // 决策 232：只在「撤销」提示条可见时接管；输入框里的 Ctrl+Z 仍归文本撤销。
            let inTextBox =
                match topLevel () with
                | null -> false
                | tl -> (tl.FocusManager |> Option.ofObj |> Option.bind (fun fm -> fm.GetFocusedElement() |> Option.ofObj)) |> Option.exists (fun f -> match f with :? TextBox as tb -> not (String.IsNullOrEmpty tb.Text) | _ -> false)
            if not inTextBox && overlay.TryInvokeToastAction "撤销" then e.Handled <- true
        | ShortcutAction.FocusToast -> if overlay.FocusToastAction() then e.Handled <- true
        | ShortcutAction.ExportConversation ->
            e.Handled <- true
            match activeSummary() with
            | Some summary -> this.ExportConversation summary
            | None -> toast "没有打开的对话可导出" Warning
        | ShortcutAction.OpenConversationAt index ->
            // Ctrl+1..9 走侧栏当前可见顺序（C2）；打开后焦点经 OpenConversation 回家（D3/D4）。
            let order = sidebar.GetVisibleOrder()
            if index >= 0 && index < order.Length then
                match Guid.TryParse order[index] with
                | true, id ->
                    e.Handled <- true
                    this.OpenConversation id
                | _ -> ()
        | ShortcutAction.ShowShortcuts -> e.Handled <- true; Dialogs.shortcuts overlay

    // ---- 协议事件 ----

    member private this.HandleEvent(ev: WireEvent) =
        match ev with
        // ADR-001 方案 B：Hello/UpgradeRequired 无生产路径（版本 in-band）。
        // 保留匹配分支仅为兼容 reader（旧主机可能发送）；不改变任何状态。
        | Hello _ | UpgradeRequired _ -> ()
        | AuthAccepted d ->
            if instanceId <> "" && instanceId <> d.instanceId then
                if exportDialog |> Option.exists (fun dialog -> dialog.IsOpen) then overlay.CloseDialog()
                (drafts.Get(instanceId, activeConvId)).text <- composer.Text
                outbox.Disconnect instanceId
                state.Reset()
                runs.Clear()
                catalog <- Catalog.empty
                settings.SetCatalog catalog
                settings.Health.Reset()
                activeConvId <- None
                summaries <- []
                sidebar.SetConversations []
                composer.SetText((drafts.Get(d.instanceId, None)).text)
            authenticated <- true
            // 新连接：能力表等主机重新声明；旧主机不会声明，保持「未知」。
            hostFeatures <- None
            // 声明本客户端能处理工具审批；旧客户端不发这条，主机据此对需要审批的调用直接拒绝
            send (ToolApprovalCapability {| supported = true |})
            hasSavedCredentials <- true
            connectionError <- None
            connectionIssue <- None
            instanceId <- d.instanceId
            // 连接真的成功了，退避归零
            reconnectDelayMs <- ReconnectBackoff.baseDelayMs
            pairingRequested <- false
            // 重连成功：上一代连接残留的失败卡片已过期，清掉免得误导。
            for s in summaries do
                runs.ClearError s.id
            activeConvId |> Option.iter runs.ClearError
            if not (exportDialog |> Option.exists (fun dialog -> dialog.IsOpen)) then overlay.CloseDialog()
            sidebar.SetConnection(true, "已连接")
            settings.SetConnection(instanceId, lastUrl)
            this.RestoreLocalDrafts()
            if settings.Devices.IsShown then settings.Devices.Refresh()
            if OperatingSystem.IsBrowser() then
                match lastToken with
                | Some token -> CredentialStore.saveBrowserConnectionAsync d.instanceId lastUrl token CredentialStore.clientName |> ignore
                | None -> ()
            send CatalogRequest
            observeConversationList ()
            match activeConvId with
            | Some convId -> send (ObserveConversation {| conversationId = convId |})
            | None -> ()
            this.Render()
        | AuthRejected d ->
            authenticated <- false
            exportDialog |> Option.iter (fun dialog -> dialog.Disconnect())
            lastToken <- None
            hasSavedCredentials <- false
            sidebar.SetConnection(false, "未认证")
            // 失败只报一次：连接对话框内联呈现原因（紧邻重连入口），不再叠 toast。
            // 认证被拒就是令牌失效：入口是“重新配对”，不是“连接失败”。
            this.ReportConnectionIssue ConnectionIssue.TokenInvalid
            this.Render()
        | PairingStarted _ ->
            connectionError <- None
            connectionIssue <- None
            setConnectStatus "配对码已生成，请输入后提交。"
        | PairingSucceeded d ->
            lastToken <- Some d.token
            hasSavedCredentials <- true
            connectionError <- None
            connectionIssue <- None
            setConnectStatus "配对成功，正在认证…"
            send (AuthPresent {| token = d.token |})
        | PairingFailed d ->
            this.ReportConnectionIssue(
                if d.frozen then ConnectionIssue.PairingFrozen d.freezeMinutes else ConnectionIssue.PairingRejected d.reason)
            this.Render()
        | CatalogSnapshot d ->
            catalog <- Catalog.parse d.providers d.tools d.generation
            settings.SetCatalog catalog
            this.SyncPresetSurfaces()
            if enteredSettingsForFirstProvider && Catalog.isReady catalog then
                enteredSettingsForFirstProvider <- false
                this.CloseSettings()
                composer.Focus()
            this.Render()
            this.CompleteOnboarding()
        | ConfigChanged _ ->
            send CatalogRequest
            // 配置重新加载成功：之前的「配置有误」随之消失。只在从有误恢复时提示一次。
            if settings.Health.ClearConfigIssue() then
                this.UpdateHostStatusLine()
                toast "主机配置已恢复正常。" Success
            // 设备列表可见时跟着刷新（别的设备被吊销、重命名）
            if settings.Devices.IsShown then send DevicesRequest
        | HostConfigRejected d ->
            // 主机拒绝了一份配置并继续用上一份有效的；客户端必须看得见（决策 82）。
            let first = settings.Health.ConfigIssue.IsNone
            settings.Health.SetConfigIssue(d.summary, d.errors)
            this.UpdateHostStatusLine()
            if first then toast d.summary Warning
        | DoctorReport d ->
            settings.Health.SetReport(d.ok, DoctorCheck.parseChecks d.checks)
        | DoctorRequest _ -> ()
        | DevicesSnapshot d -> settings.Devices.SetDevices(DevicesModel.parse d.items, d.canInvite)
        | PairingInviteIssued d -> settings.Devices.SetInvite(d.code, d.expiresAtUtc, DateTimeOffset.UtcNow)
        | PairingInviteRejected d -> settings.Devices.SetInviteRejected d.reason
        | ToolApprovalRequested d ->
            approvalPanel.Upsert
                { approvalId = d.approvalId
                  conversationId = d.conversationId
                  toolName = d.toolName
                  callId = d.callId
                  summary = d.summary
                  argumentsJson = (if isNull d.arguments then "" else d.arguments.ToJsonString())
                  policy = d.policy }
            if activeConvId <> Some d.conversationId then toast "另一个对话里有工具调用在等你批准。" Info
            this.SyncAwaitingApproval()
        | ToolApprovalResolved d ->
            approvalPanel.Remove d.approvalId
            this.SyncAwaitingApproval()
            if d.alreadyHandled then toast (ApprovalCardText.alreadyHandledText d.status) Info
        | ToolApprovalCapability _ | ToolApprovalDecision _ | ConfigUpdateTools _ -> ()
        | ProviderProbeResult d ->
            pendingProbes.Remove d.requestId |> ignore
            let models =
                [ for item in d.models do
                      if not (isNull item) && item.GetValueKind() = JsonValueKind.String then item.GetValue<string>() ]
            // 首次运行卡片自己发起的探测由它处理；其余交给设置页。
            if not (this.Onboarding.ApplyProbe(d.providerId, d.ok, models, d.error)) then
                settings.ApplyProbe(d.providerId, d.ok, models, d.error)
        | ConfigApplied d ->
            match configFeedback.Resolve d.requestId with
            | Some feedback ->
                feedback.onCompleted d.ok
                if d.ok then
                    // 空文案 = 调用方（首次运行卡片）自己会说话，这里不再叠 toast。
                    if not (String.IsNullOrEmpty feedback.successMessage) then
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
            lineage <- ForkLineage.parse d.items
            sidebar.SetLineage lineage
            // SetConversations 即快照到达：侧栏加载中状态随数据一并解除。
            sidebar.SetConversations(PendingDeletion.visible summaries pendingDeletes)
            sidebar.SetActive activeConvId
            state.AdvanceCursor()
            this.PruneDeletedConversationDrafts()
            listReceived <- true
            this.Render()
            pendingLink |> Option.iter this.OpenMessageLink
            if StartupTrace.mark "listReady" && StartupTrace.exitAfter then
                Avalonia.Threading.DispatcherTimer.RunOnce((fun () -> StartupTrace.requestExit ()), TimeSpan.FromSeconds 3.0) |> ignore
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
            if activeConvId = Some d.conversationId then
                this.Render()
                if StartupTrace.enabled then
                    ui.Post(
                        (fun () ->
                            if StartupTrace.mark "chatReady" && StartupTrace.exitAfter then StartupTrace.requestExit ()),
                        Avalonia.Threading.DispatcherPriority.Background)
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
            observeConversationList ()
            if activeConvId = Some d.conversationId then
                this.Render()
        | AuthorityCatchUp _ -> state.Handle ev
        | HistoryPage d ->
            state.Handle ev
            if activeConvId = Some d.conversationId then
                pageLoading <- false
                pageRequestedAt <- None
                chat.HideOlderPageFailure()
                this.Render()
                chat.RestoreHistoryPrependAnchorDeferred()
        | SearchResults d ->
            // 只认最近一次发出的查询：更早的结果（用户又改了词）迟到就丢掉。
            if latestSearchQueryId = Some d.queryId then
                searchSentAt <- None
                sidebar.ApplySearchResults(
                    { queryId = d.queryId
                      query = d.query
                      hits = SearchHit.parse d.items
                      hasMore = d.hasMore
                      truncated = d.truncated },
                    d.offset)
        | SearchFailed d ->
            if latestSearchQueryId = Some d.queryId then
                searchSentAt <- None
                sidebar.ApplySearchFailure(sidebar.SearchQueryText, d.message)
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
                        Some(GenerationError.create GenerationErrorKind.UnknownFailure "生成失败，主机没有说明原因。可以重试；反复出现时请在设置里「检查主机」。")
                | unknown ->
                    Some(
                        GenerationError.create GenerationErrorKind.UnknownFailure "生成结束状态未知，已按失败处理。"
                        |> GenerationError.withDetail (sprintf "未知状态：%s" unknown))
            error |> Option.iter (fun err -> RetryCountdown.attach d.generationId err DateTimeOffset.UtcNow)
            if runs.Finish(d.conversationId, d.generationId, error, d.usage) then
                state.Handle ev
                // PWA 安装引导推迟到第一次成功对话之后（第 210 节）：首次运行时不和引导卡 / 输入框抢位置。
                if d.status = "completed" && OperatingSystem.IsBrowser() then
                    try BrowserPage.ConversationSucceeded() with _ -> ()
                match d.status, error with
                | "cancelled", _ when activeConvId = Some d.conversationId -> toast "已停止" Info
                // 非当前对话的失败只 toast（卡片看不见）；当前对话的失败只进卡片，不叠 toast。
                | _, Some err when activeConvId <> Some d.conversationId ->
                    toast (GenerationError.display err) Failure
                | _ -> ()
                if activeConvId = Some d.conversationId then this.Render()
        | HostCapabilities d ->
            hostFeatures <- Some(Set.ofList d.features)
            this.SyncPresetSurfaces()
        | GenerationPartial d ->
            if runs.RestorePartial(d.conversationId, d.generationId, MessageView.ofJson d.message, d.error)
               && activeConvId = Some d.conversationId then
                this.Render()
        | ProviderRateLimitNotice d ->
            // 决策 241：限流信号先到，等对应的 generation.finished 再挂到错误卡上。
            RetryCountdown.notice d.generationId d.limit DateTimeOffset.UtcNow
        | AttachmentCommitted d ->
            match drafts.All |> Seq.tryPick (fun draft -> draft.attachments.Complete(d.attachmentId, d.size)) with
            // 决策 351：上传完成不再 toast——附件条上的 chip 已自证完成，每个文件一条
            // 「上传完成」叠在输入框上、盖住模型选择，还会跟进设置页。失败仍恰报一次（C6）。
            | Some(_, stillDrafted, key) when stillDrafted && key = (instanceId, activeConvId) ->
                composer.SetAttachments((attachmentDraft ()).Items)
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
            // 重试同键 Begin 会重置缓冲；新下载开始时也解除一次性的超限提示记忆。
            downloads.Begin(d.sha256, d.fileName)
            downloadCappedNotified <- false
        | AttachmentDownloadChunk d ->
            try
                let bytes = Convert.FromBase64String d.dataBase64
                match downloads.Append(d.sha256, bytes) with
                | DownloadChunkResult.Capped when not downloadCappedNotified ->
                    downloadCappedNotified <- true
                    toast "附件过大，下载已中止。" Warning
                | _ -> ()
            with _ -> ()
        | AttachmentDownloadComplete d ->
            match downloads.Take(d.sha256) with
            | Some(fileName, bytes) -> this.SaveDownload(fileName, bytes)
            | None -> ()
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
                // 可行动的主机错误进持久面：错误卡片自带重试，不再叠 toast（D9）。
                // 若正有生成在跑（槽位被占），退回 toast，避免踩掉进行中的状态。
                let convId = activeConvId.Value
                let failure = GenerationError.create GenerationErrorKind.UnknownFailure d.message
                if runs.Finish(convId, Guid.CreateVersion7(), Some failure, None) then this.Render()
                else toast (sprintf "主机报告了错误：%s" d.message) Failure
            else
                toast (sprintf "主机报告了错误：%s" d.message) Failure
        | _ -> ()

    // ---- 组装 ----

    member private this.BuildSidebar() =
        let actions =
            { newConversation = fun () -> this.CreateConversation() |> ignore
              openConversation = fun id -> this.OpenConversation id
              renameConversation =
                fun summary ->
                    Dialogs.prompt overlay "重命名对话" "对话标题" summary.title "保存" (fun title onCompleted ->
                        let command =
                            RenameConversation
                                {| invocationId = newInvocation (); conversationId = summary.id; title = title |}
                        sendCommandWithCompletion command None ignore onCompleted)
              // §214：删除即撤销——不再弹「无法撤销」确认框；先隐去，提示条给「撤销」。
              deleteConversation = fun summary -> this.ScheduleDelete [ summary.id ]
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
              /// 批量选择：置顶 / 归档按当前选择逐个发命令——每条命令独立可确认，
              /// 失败的那条不会被同伴的成功掩盖。删除先统一确认，再逐个删除。
              selectionAllPinned =
                fun ids ->
                    let selected = summaries |> List.filter (fun s -> List.contains s.id ids)
                    not (List.isEmpty selected) && selected |> List.forall (fun s -> s.pinned)
              setPinnedMany =
                fun ids pin ->
                    for id in ids do
                        match summaries |> List.tryFind (fun s -> s.id = id) with
                        | Some summary ->
                            let command =
                                SetConversationFlags
                                    {| invocationId = newInvocation ()
                                       conversationId = summary.id
                                       pinned = pin
                                       archived = summary.archived |}
                            sendCommand command
                        | None -> ()
                    toast (sprintf "%s %d 个对话" (if pin then "已置顶" else "已取消置顶") (List.length ids)) Info
                    sidebar.ExitSelection()
              setArchivedMany =
                fun ids archived ->
                    for id in ids do
                        match summaries |> List.tryFind (fun s -> s.id = id) with
                        | Some summary ->
                            let command =
                                SetConversationFlags
                                    {| invocationId = newInvocation ()
                                       conversationId = summary.id
                                       pinned = summary.pinned
                                       archived = archived |}
                            sendCommand command
                        | None -> ()
                    toast (sprintf "%s %d 个对话" (if archived then "已归档" else "已取消归档") (List.length ids)) Info
                    sidebar.ExitSelection()
              deleteMany =
                fun ids ->
                    if not (List.isEmpty ids) then
                        sidebar.ExitSelection()
                        this.ScheduleDelete ids
              duplicateAsFork =
                fun summary ->
                    this.OpenConversation summary.id
                    toast "已打开对话，可从任意消息分叉。" Info
              exportConversation = fun summary -> this.ExportConversation summary
              openSettings = fun () -> this.ShowSettings()
              reconnect =
                fun () ->
                    if authenticated then toast "已连接" Info
                    elif lastToken.IsSome then this.Connect()
                    else this.ShowConnectDialog()
              toggleArchivedVisibility = fun () -> this.SavePrefs { prefs with showArchived = not prefs.showArchived }
              closeNavigation = fun () -> this.SetCompactNavigation false }
        sidebar <- Sidebar(overlay, actions, Brand.logo)
        // W4：消息正文搜索。查询在主机内存投影上完成；每次发出都换一个 queryId，旧结果迟到即丢。
        sidebar.SearchHandler <-
            Some(fun query includeArchived offset ->
              match MessageLink.tryParse query with
              | Some link ->
                sidebar.ResetSearch()
                this.OpenMessageLink link
              | None ->
                let queryId = Guid.CreateVersion7()
                latestSearchQueryId <- Some queryId
                searchSentAt <- Some DateTimeOffset.UtcNow
                send (
                    SearchQuery
                        { queryId = queryId
                          query = query
                          includeArchived = includeArchived
                          offset = offset
                          limit = SearchLimits.defaultPageSize }))
        sidebar.OpenSearchHit <- fun hit -> this.OpenSearchHit hit
        sidebar.Build()
        sidebar.SetShowArchived prefs.showArchived

    member private this.ShowSessionContext() =
        match activeConvId, activeConversation () with
        | Some convId, Some view when authenticated ->
            Dialogs.sessionSettings overlay catalog view.config (fun config onCompleted ->
                let command =
                    UpdateConversationConfig
                        {| invocationId = newInvocation (); conversationId = convId; config = config |}
                sendCommandWithCompletion command (Some "对话设置已更新") ignore onCompleted)
        | _ -> toast "连接并选择对话后，可以调整上下文。" Warning

    member private this.BuildChat() =
        MessageCard.setLinkSink (fun commitId ->
            match activeConvId with
            | Some convId ->
                let pageUrl = if OperatingSystem.IsBrowser() then (try Some(BrowserPage.Url()) with _ -> None) else None
                copyToClipboard (MessageLink.format pageUrl convId commitId)
            | None -> ())
        MessageCard.setRecoverySink (fun recovery ->
            match recovery with
            | OpenProviderSettings ->
                settings.ShowSection SettingsSection.Providers
                this.ShowSettings()
            | OpenConversationSettings -> this.ShowSessionContext()
            | SwitchModel -> this.ShowModelPicker(if chat.HeaderModelVisible then chat.HeaderModelAnchor else composer.ModelAnchor())
            | StartNewConversation -> this.CreateConversation() |> ignore)
        let messageActions =
            { copyText = copyToClipboard
              regenerate = fun () -> this.Regenerate()
              editAndFork = fun message -> this.ForkFrom(message.commitId, message.text)
              deleteMessage =
                fun commitId ->
                    match activeConvId with
                    | Some convId ->
                        Dialogs.confirm overlay "删除消息" "这条消息会从对话中移除，后续生成不再看到它。" "删除" (fun () ->
                            let command =
                                DeleteMessage
                                    {| invocationId = newInvocation ()
                                       conversationId = convId
                                       messageCommitId = commitId |}
                            sendCommandWithFeedback command (Some "消息已删除") ignore)
                    | None -> ()
              downloadAttachment = fun sha -> send (AttachmentDownloadRequest {| sha256 = sha |})
              // 决策 231：消息里的消息链接在应用内打开，不交给系统浏览器。
              openLink = fun url -> match MessageLink.tryParse url with Some link -> this.OpenMessageLink link | None -> openLink url }
        let chatActions =
            { renameTitle =
                fun title ->
                    match activeConvId with
                    | Some convId ->
                        sendCommand (
                            RenameConversation {| invocationId = newInvocation (); conversationId = convId; title = title |})
                    | None -> ()
              openSessionSettings = fun () -> this.ShowSessionContext()
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
                    | None -> toast "当前没有可分叉的对话。" Warning
              stopGeneration = fun () -> this.StopGeneration()
              requestOlderHistory = fun () -> this.RequestOlderHistory()
              retryLast = fun () -> this.Regenerate()
              toggleSidebar = fun () -> this.ToggleSidebar()
              focusHome = fun () -> composer.Focus()
              message = messageActions }
        chat <- ChatView(chatActions, Brand.logo)
        chat.SiblingProvider <-
            fun commit prev ->
                match activeConvId with
                | Some convId -> ForkLineage.siblings summaries lineage convId commit prev
                | None -> None
        chat.SiblingSelected <- fun target -> this.SwitchVersion target
        chat.ConfigureMenu(overlay, fun () -> activeSummary () |> Option.iter (fun summary -> this.ExportConversation summary))
        chat.Build()

    /// 搜索命中 / 版本切换后把目标消息带进视口：目标在已加载窗口内就滚过去，
    /// 更早就继续加载更早历史（HistoryPage 回来会再进 Render 重试）；到头仍找不到则放弃，不循环。
    member private this.TryReveal(convId: Guid, view: ConversationView, messages: MessageView list) =
        match pendingReveal with
        | Some(target, reveal) when target = convId ->
            let loaded = messages |> List.choose _.commitId
            match RevealTarget.resolve reveal loaded with
            | Some commit ->
                pendingReveal <- None
                match reveal with
                | RevealMessage _ -> chat.RevealAndHighlight commit |> ignore
                | RevealAfter _ -> chat.ScrollToMessage commit |> ignore
            | None ->
                let wanted = match reveal with RevealMessage c -> Some c | RevealAfter _ -> None
                match wanted with
                | Some c when view.pageHasMore && c < view.pageEarliest ->
                    if not pageLoading then this.RequestOlderHistory()
                | _ -> pendingReveal <- None
        | _ -> ()

    member private this.RequestOlderHistory() =
        match activeConvId, activeConversation () with
        | Some convId, Some view when view.pageHasMore && not pageLoading ->
            chat.BeginHistoryPrependAnchor()
            chat.HideOlderPageFailure()
            pageLoading <- true
            pageRequestedAt <- Some DateTimeOffset.UtcNow
            this.EnsureOlderPageWatchdog()
            // 分页没有骨架屏（会重置滚动）：一条 transient toast 给加载反馈。
            toast "正在加载更早消息…" Info
            send (
                HistoryRequest
                    {| conversationId = convId
                       beforeCommitId = view.pageEarliest
                       limit = 100 |})
        | _ -> ()

    /// 决策 310：更早一页的等待有上限。超时 / 断线都收口到这里：
    /// 解除 pageLoading、丢掉分页锚点、撤销挂起的「上一轮」，消息区顶部给原因 + 「重试」。
    member private this.FailOlderPage(message: string) =
        if pageLoading || pageRequestedAt.IsSome then
            pageLoading <- false
            pageRequestedAt <- None
            chat.CancelHistoryPrependAnchor()
            this.ShowOlderPageRetry message

    member private this.ShowOlderPageRetry(message: string) =
        chat.ShowOlderPageFailure(message, fun () ->
            if authenticated then this.RequestOlderHistory()
            else this.ShowOlderPageRetry "还没有连接主机，更早的消息无法加载。连接后再点「重试」。")

    /// 超时检查：只在有请求在途时有事做；tick 本身很轻，不必频繁启停。
    member private this.CheckOlderPageTimeout(now: DateTimeOffset) =
        match pageRequestedAt with
        | Some at when pageLoading && now - at >= MotionLedger.olderPageTimeout ->
            this.FailOlderPage "加载更早消息超时，主机没有回应。可以点「重试」。"
        | _ -> ()

    member private this.EnsureOlderPageWatchdog() =
        if olderPageWatchdog.IsNone then
            let timer = Avalonia.Threading.DispatcherTimer(Interval = MotionLedger.olderPageCheckTick)
            timer.Tick.Add(fun _ ->
                this.CheckOlderPageTimeout DateTimeOffset.UtcNow
                if pageRequestedAt.IsNone then timer.Stop())
            olderPageWatchdog <- Some timer
        olderPageWatchdog |> Option.iter (fun t -> if not t.IsEnabled then t.Start())

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
                        "模型服务设置已保存"
                        (fun requestId -> ConfigUpsertProvider {| requestId = requestId; provider = payload |})
                        onCompleted
              deleteProvider =
                fun id ->
                    Dialogs.confirm overlay "删除模型服务" (sprintf "「%s」将从配置中移除，使用它的对话需要重新选择模型。" id) "删除" (fun () ->
                        sendConfigWithFeedback
                            "模型服务已删除"
                            (fun requestId -> ConfigDeleteProvider {| requestId = requestId; providerId = id |})
                            ignore)
              probeProvider = fun id -> this.ProbeProvider id
              upsertMcp =
                fun payload onCompleted ->
                    sendConfigWithFeedback
                        "MCP 设置已保存"
                        (fun requestId -> ConfigUpsertMcp {| requestId = requestId; server = payload |})
                        onCompleted
              deleteMcp =
                fun id ->
                    Dialogs.confirm overlay "删除 MCP 服务" (sprintf "「%s」提供的工具将不再可用。" id) "删除" (fun () ->
                        sendConfigWithFeedback
                            "MCP 服务已删除"
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
        // 检查主机：只读请求；旧主机不认识这个事件会直接忽略，所以要有超时说明。
        settings.Health.CheckRequested.Add(fun () ->
                if authenticated && client.IsConnected then
                    send (DoctorRequest {| requestId = newInvocation () |})
                    let timeout = DispatcherTimer(Interval = TimeSpan.FromSeconds 30.0)
                    timeout.Tick.Add(fun _ ->
                        timeout.Stop()
                        match settings.Health.Doctor with
                        | Running ->
                            settings.Health.SetUnavailable "没有拿到检查结果：主机可能版本较旧，或检查超时了。请稍后重试，仍不行就升级主机。"
                        | _ -> ())
                    timeout.Start()
                else
                    settings.Health.SetUnavailable "还没连接主机，无法检查。连接后再试。")
        settings.LocalDrafts.ClearRequested.Add(fun () ->
                Dialogs.confirm
                    overlay
                    "清除本地草稿"
                    "这台设备上保存的未发送文字、附件名称和未确认的消息会被删除，无法恢复。已发送并保存的对话不受影响。"
                    "清除"
                    (fun () -> this.ClearLocalDrafts()))
        settings.Build()
        this.UpdateDraftSummary()
        settings.Devices.Bind
            { request = fun () -> send DevicesRequest
              invite = fun () -> send PairingInvite
              rename =
                fun deviceId name ->
                    sendConfigWithFeedback
                        "设备已重命名"
                        (fun requestId -> DeviceRename {| requestId = requestId; deviceId = deviceId; name = name |})
                        ignore
              revoke =
                fun device ->
                    Dialogs.confirm
                        overlay
                        "吊销设备"
                        (sprintf "「%s」会立即断开，之后需要重新配对才能再连这台主机。" device.name)
                        "吊销"
                        (fun () ->
                            sendConfigWithFeedback
                                "设备已吊销"
                                (fun requestId -> DeviceRevoke {| requestId = requestId; deviceId = device.id |})
                                ignore)
              toast = fun message tone -> toast message tone }

    /// 卡片按需构建：局部构建视图的调用方（只 Build 了部分区域）也不会撞上空引用。
    member private this.Onboarding: OnboardingCard =
        if isNull (box onboarding) then this.BuildOnboarding()
        onboarding

    member private this.BuildOnboarding() =
        let actions: OnboardingActions =
            { upsertProvider =
                fun payload onCompleted ->
                    sendConfigWithFeedback
                        ""
                        (fun requestId -> ConfigUpsertProvider {| requestId = requestId; provider = payload |})
                        onCompleted
              discardProvider =
                fun id ->
                    sendConfigWithFeedback
                        ""
                        (fun requestId -> ConfigDeleteProvider {| requestId = requestId; providerId = id |})
                        ignore
              probeProvider = fun id -> this.ProbeProvider id
              takenIds = fun () -> catalog.providers |> List.map (fun p -> p.id)
              discoverLocal =
                fun () ->
                    if OnboardingRules.canDiscoverLocal lastUrl then
                        OnboardingRules.discoverOllamaAt ProviderPresets.OllamaProbeUrl
                    else
                        System.Threading.Tasks.Task.FromResult None
              openAdvanced = fun () -> this.ShowAddProvider()
              toast = fun message tone -> toast message tone }
        onboarding <- OnboardingCard(overlay, actions)

    member this.Build() =
        this.FontFamily <- Tokens.fontFamily
        this.ApplyTheme()

        this.BuildSidebar()
        this.BuildChat()
        // 决策 251：系统打开的 wanxiang:// 链接（启动参数 / 第二实例转发 / macOS 激活）
        if not (OperatingSystem.IsBrowser()) then
            ExternalLinks.subscribe (fun url ->
                ui.Post(fun () ->
                    match MessageLink.tryParse url with
                    | Some link -> this.OpenMessageLink link
                    | None -> toast "无法识别这个链接。" Warning))
        this.BuildComposer()
        overlay.SetToastFocusHome(fun () -> composer.Focus())
        sidebar.ConfigureImport(fun () -> this.ImportConversation())
        sidebar.ConfigurePalette(fun () -> this.ShowCommandPalette())
        this.BuildSettings()
        settings.ConfigureImport(fun () ->
            this.CloseSettings()
            this.ImportConversation())
        settings.ConfigurePresets((fun p -> this.RenamePreset p), (fun p -> this.DeletePreset p))
        this.BuildOnboarding()

        chatColumn <- ConversationWorkspace(chat, composer)
        // 审批卡放进输入框正上方的附属位：与输入框同列同宽、占布局高度，
        // 不再浮层盖住消息列表最后一条的操作行（视觉走查 11 号截图）。
        composer.SetAccessory approvalPanel

        let initialNavigation = navigation.State
        shellGrid <- Grid()
        InputModality.track this
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
        // splitter 悬停/聚焦底色交给 Interaction 的原生伪类
        // （`:pointerover` / `:focus-visible`），拖动行为不变。
        Interaction.surface sidebarSplitter Interaction.HoverVariant.plain |> ignore
        Interaction.reportFocusOrigin sidebarSplitter
        sidebarSplitter.DragCompleted.Add(fun _ ->
            let nav = navigation.State
            if not nav.compactMode && not nav.sidebarCollapsed then
                let width = mainLayout.NoteUserSidebarWidth shellGrid.ColumnDefinitions[0].ActualWidth
                prefs <- { prefs with sidebarWidth = width }
                UiPrefsFile.save prefs)
        shellGrid.Children.Add sidebar
        shellGrid.Children.Add sidebarSplitter
        shellGrid.Children.Add chatColumn
        mainLayout <- MainLayoutController(shellGrid, sidebar, sidebarSplitter, chatColumn, composer, chat, fun () -> prefs.sidebarWidth)
        // 抽屉遮罩与 Escape / 侧栏行同一归宿：SetCompactNavigation 的收起分支（D3/D4）。
        mainLayout.OnScrimPressed <- Some (fun () -> this.SetCompactNavigation false)
        mainLayout.Apply initialNavigation
        workspace.Children.Add shellGrid

        settingsHost.Children.Add settings

        root.Children.Insert(0, workspace)
        root.Children.Insert(1, settingsHost)
        this.Content <- root
        this.Background <- Tokens.canvas
        overlay.WireDismiss()

        client.ConnectionEventReceived.Add(fun (epoch, ev) ->
            ui.Post(fun () ->
                if epoch = client.ConnectionGeneration then this.HandleEvent ev))
        state.CursorChanged.Add(fun _ -> send (state.CursorAdvancedEvent()))
        client.ConnectionClosed.Add(fun (epoch, error) ->
            ui.Post(fun () ->
                if epoch = client.ConnectionGeneration then
                    authenticated <- false
                    approvalPanel.Clear()
                    chat.SetAwaitingApproval Set.empty
                    runs.Disconnect()
                    exportDialog |> Option.iter (fun dialog -> dialog.Disconnect())
                    this.FailOlderPage "连接已断开，更早的消息没有加载。重新连接后点「重试」。"
                    outbox.Disconnect instanceId
                    commandFeedback.RejectAll()
                    configFeedback.RejectAll()
                    this.FailUploadingAttachments()
                    settings.Devices.NotifyDisconnected()
                    // 与主动断开同路径：连接死了，在途下载缓冲一并丢弃（R1）。
                    downloads.Clear()
                    downloadCappedNotified <- false
                    let detail = match error with Some e -> e.Message | None -> ""
                    sidebar.SetConnection(false, "连接已断开")
                    if not (String.IsNullOrWhiteSpace detail) then toast (sprintf "连接已断开：%s" detail) Warning
                    this.Render()
                    this.ScheduleReconnect()))

        // 壳背景跟随主题一起换：内容容器全透明，任一层过期都会在窗口边缘漏出异色细线。
        Tokens.Changed.Publish.Add(fun _ ->
            ui.Post(fun () ->
                this.Background <- Tokens.canvas
                this.Render()))
        // 后台着色算完：卡片身份没变，必须显式丢缓存才会换成着色版
        Highlight.Ready.Add(fun _ ->
            ui.Post(fun () ->
                chat.InvalidateCards()
                this.Render()))
        match Application.Current with
        | null -> ()
        | app ->
            match app.PlatformSettings with
            | null -> ()
            | settings ->
                ParityProbe.hit "theme.systemSubscribed"
                settings.ColorValuesChanged.Add(fun _ ->
                    if prefs.theme = FollowSystem then
                        ui.Post(fun () -> this.ApplyTheme()))
        this.KeyDown.Add(fun e -> this.HandleShortcut e)
        chat.SetStarterHandler(fun seed -> composer.SetText seed)
        chat.SetModelSwitcherHandler(fun anchor -> this.ShowModelPicker anchor)
        this.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then
                this.ApplyResponsiveLayout this.Bounds.Width)

        this.Render()
        this.ApplyResponsiveLayout this.Bounds.Width
        this.WatchHostViewport()
        let deliveryTimer = DispatcherTimer(Interval = MotionLedger.outboxDeliveryCheckTick)
        deliveryTimer.Tick.Add(fun _ ->
            ParityProbe.hit "timer.delivery"
            let now = DateTimeOffset.UtcNow
            // 决策 443：旧主机静默丢弃它不认识的请求——到点就让界面退出等待并说明原因。
            let commandsExpired = this.ExpireHostRequests now
            if outbox.Expire(now, TimeSpan.FromSeconds 30.0) || commandsExpired then this.Render())
        // 本地草稿自动保存：内容没变不写盘，所以高频 tick 的代价只是一次快照比较。
        let draftTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 800.0)
        draftTimer.Tick.Add(fun _ ->
            ParityProbe.hit "timer.draft"
            this.FlushDrafts())
        let startTimers () =
            if not timersStarted then ParityProbe.hit "lifecycle.shellAttached"
            timersStarted <- true
            // 决策 454：焦点不在本视图里时（PWA 首屏、点了画布空白处），按键只落到 TopLevel，
            // 不会冒泡经过本视图——快捷键在浏览器里因此失灵。TopLevel 上补一个入口，
            // 只处理来源不在本视图内的按键，避免与 this.KeyDown 重复。
            match TopLevel.GetTopLevel this with
            | null -> ()
            | top when topLevelKeysHooked = Some top -> ()
            | top ->
                topLevelKeysHooked <- Some top
                top.KeyDown.Add(fun e ->
                    let inside =
                        match e.Source with
                        | :? Visual as v -> obj.ReferenceEquals(v, this) || Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(this, v)
                        | _ -> false
                    if not e.Handled && not inside then this.HandleShortcut e)
            deliveryTimer.Start()
            draftTimer.Start()
            this.StartViewportWatcher()
        this.AttachedToVisualTree.Add(fun _ -> startTimers ())
        // 决策 449：PWA 先把视图挂上 SingleView 再 Build()，AttachedToVisualTree 早已触发过——
        // 只挂事件会让投递确认、请求超时、草稿自动保存在浏览器里从不运行。已挂上就立刻启动（Start 幂等）。
        if not (isNull (TopLevel.GetTopLevel this)) then startTimers ()
        // 决策 451：PWA 没有关窗口事件——页面转入后台 / 卸载时同样立刻落草稿。
        if OperatingSystem.IsBrowser() then
            BrowserPage.add_Hidden(fun () ->
                ParityProbe.hit "drafts.flushOnHide"
                this.FlushDrafts())
        this.DetachedFromVisualTree.Add(fun _ ->
            this.FlushDrafts()
            deliveryTimer.Stop()
            draftTimer.Stop()
            this.StopViewportWatcher())
        this.LoadLocalDrafts()
        this.AutoConnect()

    /// 读本机保存的草稿。桌面是同步完成的文件读取；PWA 的 IndexedDB 读取完成后回到 UI 线程。
    /// 读完之前不写盘，避免用空内存覆盖尚未读到的内容。
    member private this.LoadLocalDrafts() =
        let apply (snapshot: DraftSnapshot) =
            ParityProbe.hit "drafts.loaded"
            storedDrafts <- snapshot
            draftsLoaded <- true
            this.RestoreLocalDrafts()
            this.UpdateDraftSummary()
        let task = draftStore.Load()
        if task.IsCompleted then
            apply (try task.Result with _ -> DraftSnapshot.empty)
        else
            task.ContinueWith(fun (t: System.Threading.Tasks.Task<DraftSnapshot>) ->
                let snapshot = if t.IsFaulted then DraftSnapshot.empty else t.Result
                ui.Post(fun () -> apply snapshot))
            |> ignore

    /// 已有凭据时自动连接：桌面读 client.toml，PWA 读 IndexedDB（决策 52/53、Q191）。
    member private this.AutoConnect() =
        ParityProbe.hit "connect.auto"
        match CredentialStore.tryLoadClientToml () with
        | Some(url, token) ->
            hasSavedCredentials <- true
            lastUrl <- url
            lastToken <- Some token
            this.Connect()
        | None ->
            if OperatingSystem.IsBrowser() then
                async {
                    let! stored = CredentialStore.tryLoadBrowserConnectionAsync () |> Async.AwaitTask
                    match stored with
                    | Some(url, token, _) ->
                        ui.Post(fun () ->
                            hasSavedCredentials <- true
                            lastUrl <- url
                            lastToken <- Some token
                            this.Connect())
                    | None ->
                        ui.Post(fun () ->
                            hasSavedCredentials <- false
                            this.Render())
                }
                |> Async.Start
            else
                hasSavedCredentials <- false
                this.Render()
