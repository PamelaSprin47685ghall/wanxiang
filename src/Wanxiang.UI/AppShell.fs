namespace Wanxiang.UI

open System
open System.IO
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
        Reading.wrap.Wrapped <- prefs.codeWrap
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
    let search = SearchInFlight()
    let probes = PendingProbes()
    let runs = ConversationRuns()
    let mutable summaries: ConversationSummary list = []
    let mutable paletteRecent: string list = PaletteRecent.loadRecent ()
    /// 「检查主机」的有界等待（旧主机不认识这个事件会直接忽略）。
    let doctorWait = BoundedWait(BoundedWaits.doctorLimit)
    /// W4：分叉谱系（随列表快照更新）、最近一次搜索的 queryId、待滚动到的消息。
    let mutable lineage: Map<Guid, ForkLineage> = Map.empty
    let mutable pendingReveal: (Guid * RevealTarget) option = None

    let drafts = ComposerDrafts()
    let outbox = MessageOutbox()
    // ---- 本地草稿（W5，决策 80/81）：只存本机，不入日志、不上传、不同步 ----
    let draftStore = LocalDraftStore.Default(fun () -> prefs.persistDrafts)
    /// 已存快照、读完标志、已恢复主机、认得的对话与落盘闸门归 DraftSession；读写落点在 draftStore。
    let draftSession = DraftSession()
    let attachmentDraft () = (drafts.Get(instanceId, activeConvId)).attachments
    // 附件下载在途缓冲：容量上限 + 断连整体丢弃，替代此前无上限的裸字典（R1）。
    let downloads = DownloadBuffers()
    let mutable protocolDispatch: ProtocolDispatch option = None
    let mutable shellCommands: ShellCommands option = None
    let mutable missingAttachments: Set<string> = Set.empty

    let mutable lastUrl = CredentialStore.defaultServerUrl ()
    let mutable lastToken: string option = None
    let mutable hasSavedCredentials = CredentialStore.tryLoadClientToml().IsSome
    let mutable connectionError: string option = None
    /// 类型化的连接失败原因：连接对话框的重试动作由它决定，不由 connectionError 的文案推断。
    let mutable connectionIssue: ConnectionIssue option = None
    let mutable enteredSettingsForFirstProvider = false
    /// 连接尝试编号、挂起的重连与退避时长归 ReconnectLoop；计时与真正的连接在这里。
    let reconnect = ReconnectLoop(ReconnectBackoff.baseDelayMs, ReconnectBackoff.next)
    let mutable pairingRequested = false
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
    /// 骨架屏延时器为哪个对话武装归 DeferredReveal：Render 会被后台事件反复触发，不得重启它，一对话只武装一次（D1）。
    let skeletonReveal = DeferredReveal<Guid>(MotionLedger.conversationSkeletonDelay)
    /// 浏览器宿主的 CSS 像素视口宽（`wxViewportWidth` 轮询写入）。
    /// Browser 后端高 DPR 下 `Bounds.Width` 会被 DPR 除一次，不可做断点依据；
    /// 有覆盖值时响应式布局只认它，桌面/测试走 Bounds 原路径。
    let hostViewportWidth = ChangeWatch<float>()
    /// 浏览器 CSS 视口轮询计时器：仅在 Browser 环境启动，卸载时停止。
    let mutable viewportTimer: DispatcherTimer option = None
    /// 上次看到的浏览器“恢复”计数（online / 回到前台，第 213 节）。
    let resumeEpoch = ChangeWatch<int>(Some 0)
    let mutable lastPageLink = ""
    let mutable pendingLink: (Guid * uint64) option = None
    let mutable listReceived = false
    let commandFeedback = CommandFeedbackTracker()
    let configFeedback = ConfigFeedbackTracker()
    let mutable exportDialog: Wanxiang2.TransferDesk.ExportDesk option = None
    let mutable importDialog: Wanxiang2.TransferDesk.ImportDesk option = None

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
    let mutable sidebar: Wanxiang2.SidebarDesk.SidebarDesk = Unchecked.defaultof<_>
    let mutable chat: ChatView = Unchecked.defaultof<ChatView>
    let mutable composer: Composer = Unchecked.defaultof<Composer>
    let mutable settings: Wanxiang2.SettingsScreen.SettingsScreen = Unchecked.defaultof<Wanxiang2.SettingsScreen.SettingsScreen>
    /// 聊天主界面上的「连接一个模型」卡片（W1）：首次运行不进设置页。
    let mutable onboarding: Wanxiang2.SettingsScreen.OnboardingDesk = Unchecked.defaultof<Wanxiang2.SettingsScreen.OnboardingDesk>
    let mutable sidebarSplitter: GridSplitter = Unchecked.defaultof<GridSplitter>
    let mutable shellDesk: Wanxiang2.ShellDesk.ShellDesk = Unchecked.defaultof<Wanxiang2.ShellDesk.ShellDesk>
    let workspace = Grid()
    let settingsHost = Grid(IsVisible = false)
    let mutable shellGrid: Grid = Unchecked.defaultof<Grid>

    let send (ev: WireEvent) = client.SendAsync ev |> ignore

    /// 工具审批卡（第 71 节）：对话内、浮在输入框上方；状态只在内存里，由主机事件驱动。
    let approvalPanel = Wanxiang2.ApprovalDesk.ApprovalDesk(SharedTheme.manager, overlay.Desk, send)
    let palette = Wanxiang2.PaletteDesk.PaletteDesk(SharedTheme.manager, overlay.Desk, fun () -> SharedTheme.input.PrefersTouch)
    let modelSwitch = Wanxiang2.ModelSwitchDesk.ModelSwitchDesk(SharedTheme.manager, overlay.Desk)
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
    /// §214：删除、撤销、批次与到期协作归 DeletionDesk；宿主只接真实界面、计时与发送。
    let deletion =
        DeletionDesk(commandFeedback,
            { Now = fun () -> DateTimeOffset.UtcNow
              ActiveConversation = fun () -> activeConvId
              TitleOf = fun id -> summaries |> List.tryFind (fun summary -> summary.id = id) |> Option.map _.title
              VisibleOrder = fun () -> sidebar.GetVisibleOrder()
              PendingChanged = fun pending -> sidebar.SetConversations(PendingDeletion.visible summaries pending)
              FocusAfterDelete = fun id -> sidebar.FocusAfterDelete(string id)
              Open = fun id ->
                  this.SelectConversation id
                  sidebar.SetActive activeConvId
              Render = fun () -> this.Render()
              Toast = fun text tone ->
                  toast text (match tone with
                              | DeletionTone.Info -> Info
                              | DeletionTone.Success -> Success
                              | DeletionTone.Warning -> Warning)
              ToastUndo = fun label undo -> overlay.ToastWith(label, Info, Some("撤销", undo))
              After = fun delay action ->
                  let timer = DispatcherTimer(Interval = delay)
                  timer.Tick.Add(fun _ ->
                      timer.Stop()
                      action ())
                  timer.Start()
                  { new IDisposable with member _.Dispose() = timer.Stop() }
              SendCommand = sendCommand
              NewInvocation = newInvocation })

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

    /// 导航状态归 ShellDesk 的 NavigationController；外壳建好之前按偏好给初值。
    member private this.Navigation : NavigationSnapshot =
        if isNull (box shellDesk) then NavigationController(prefs.sidebarCollapsed, LayoutPolicy.compactBreakpoint).State
        else shellDesk.State

    /// compact 抽屉开合；焦点归宿（打开进搜索框、收起回输入区，触屏不自动聚焦）在 ShellDesk。
    member private this.SetCompactNavigation(opened: bool) =
        if not (isNull (box shellDesk)) then shellDesk.SetCompactNavigation opened |> ignore

    member private this.ApplyResponsiveLayout(width: float) =
        let effective = hostViewportWidth.Last |> Option.defaultValue width
        if effective > 0.0 && not (isNull (box shellDesk)) then shellDesk.ApplyViewport effective |> ignore


    /// 浏览器 CSS 视口轮询：zoom、DPR、显示器移动都不会可靠触发 Bounds 更新，
    /// 但 innerWidth 永远是真相。桌面端不调用（保留 Bounds 路径）。
    member private this.WatchHostViewport() =
        if OperatingSystem.IsBrowser() then
            let timer = DispatcherTimer(Interval = MotionLedger.hostViewportPoll)
            timer.Tick.Add(fun _ ->
                ParityProbe.hit "timer.viewport"
                try
                    let css = BrowserPage.ViewportWidth()
                    if css > 0.0 && hostViewportWidth.Observe css then
                        this.ApplyResponsiveLayout css
                with _ -> ()
                try
                    let epoch = BrowserPage.ResumeEpoch()
                    if resumeEpoch.Observe epoch then
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
        if skeletonReveal.Arm(convId, DateTimeOffset.UtcNow) then
            match skeletonTimer with
            | Some timer -> timer.Stop()
            | None -> ()
            let timer = DispatcherTimer(Interval = skeletonReveal.Delay)
            timer.Tick.Add(fun _ ->
                timer.Stop()
                if skeletonReveal.Expired convId && activeConvId = Some convId && (activeConversation ()).IsNone then
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
        skeletonReveal.Disarm()

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
            StartReady.isReady authenticated (Catalog.isReady catalog) generating run.error.IsSome
                (match activeConvId, activeConversation () with
                 | None, _ -> ActiveConversation.None
                 | Some _, Some view -> ActiveConversation.Loaded view.messages.Count
                 | Some _, None -> ActiveConversation.Loading)
        if not (isNull (box shellDesk)) then shellDesk.SetStarting startReady
        composer.SetSessionContext(activeConversation () |> Option.map _.config, authenticated, fun () -> this.ShowSessionContext())
        match activeConvId, activeConversation () with
        | None, _ ->
            chat.SetHeaderModel("", "", false)
            chat.SetConversationChrome false
            chat.SetBreadcrumb(None, ignore)
            chat.SetTitle("新对话", false)
            let rePair =
                match connectionIssue |> Option.bind ConnectionIssue.retry with
                | Some RePair -> true
                | _ -> false
            let idle =
                IdleSurface.decide authenticated connectionError rePair hasSavedCredentials
                    (Catalog.isReady catalog) this.Onboarding.IsBusy
            let primary = idle.primary |> Option.map (fun label -> label, fun () -> this.ShowConnectDialog())
            match idle.empty with
            | IdleEmpty.ConnectionFailed err -> chat.ShowEmpty(ChatEmptyState.ConnectionFailed err, primary)
            | IdleEmpty.NotConnected -> chat.ShowEmpty(ChatEmptyState.NotConnected, primary)
            | IdleEmpty.Onboarding ->
                // 首次运行：卡片就在聊天界面里，不必先进设置页。
                chat.ShowEmbeddedEmpty this.Onboarding.View
                this.Onboarding.StartDiscovery()
            | IdleEmpty.NoConversation -> chat.ShowEmpty(ChatEmptyState.NoConversation, None)
            composer.SetEnabled(idle.composerEnabled, idle.composerReason)
            let chip = Catalog.idleModelChip idle.modelLabel draftModelSelection catalog
            composer.SetModelLabel chip.label
            chip.vision |> Option.iter composer.SetModelVision
        | Some convId, None ->
            chat.SetConversationChrome true
            // Loading 分级：快照 300ms 内到达就不闪任何 loading chrome，超时才在本地挂骨架；
            // 流式文本本身就是进度，这里不叠加任何页级 loading。
            this.ShowConversationLoadingDeferred convId
            composer.SetEnabled(false, IdleSurface.LoadingReason)
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
            let surface =
                ConversationSurface.decide
                    (List.isEmpty messages) streaming.IsSome run.error.IsSome run.partial.IsSome generating
                    (ReplyTail.isUnanswered DateTimeOffset.UtcNow messages) authenticated run.generationId.IsSome
            if surface.showEmpty then
                chat.HideSkeletonLoading()
                chat.ShowEmpty(EmptyConversation, None)
            else
                chat.HideSkeletonLoading()
                chat.HideEmpty()
                // 决策 300：「上一轮」越过已加载历史时要知道主机上还有没有更早的页。
                chat.SetOlderAvailable view.pageHasMore
                let retry () = this.Regenerate()
                // 先同步生成状态：渲染时要据此区分工具调用「执行中」与「结果未知」
                chat.SetGenerating(generating, surface.generatingText)
                chat.SetPartialReply(run.partial |> Option.map (fun p -> p, fun () -> this.ContinueGeneration(convId, p)))
                chat.RenderMessages(
                    messages,
                    streaming,
                    (match surface.tailError, run.error with
                     | TailError.RunError, Some e -> Some(e, retry)
                     | TailError.Unanswered, _ -> Some(UnansweredReply.error, retry)
                     | _ -> None),
                    prefs.fontScale,
                    prefs.autoCollapseReasoning,
                    run.usage,
                    missingAttachments)
                this.TryReveal(convId, view, messages)
            composer.SetEnabled(surface.composerEnabled, surface.composerReason)
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

    /// 内存里的草稿与未确认消息（先把输入框文字同步进去）。
    member private this.LiveDraftSnapshot() : DraftSnapshot =
        this.SyncActiveDraftText()
        DraftPolicy.snapshotOf drafts outbox (fun id -> draftSession.SavedAt(id, DateTimeOffset.UtcNow))

    member private this.CurrentDraftSnapshot() : DraftSnapshot =
        draftSession.ForSave(this.LiveDraftSnapshot())

    member private this.UpdateDraftSummary() =
        if not (isNull (box settings)) then
            let snapshot = this.CurrentDraftSnapshot()
            let texts, unsent = DraftRestore.summaryCounts snapshot
            settings.LocalDrafts.Summary <-
                DraftSummaryText.text prefs.persistDrafts texts unsent

    /// 保存到本机。关闭保存、尚未读完磁盘、或内容没变时什么也不做。
    member this.FlushDrafts() =
        // 读完之前连快照都不算（不记保存时刻），与旧时序相同。
        if draftSession.IsLoaded && prefs.persistDrafts then
            ParityProbe.hit "drafts.flush"
            draftStore.Apply(draftSession.Flush(prefs.persistDrafts, this.LiveDraftSnapshot())) |> ignore

    /// 把本机保存的内容放回内存：只处理当前主机，且不覆盖用户这次已经输入的文字。
    member private this.RestoreLocalDrafts() =
        match draftSession.Restore(prefs.persistDrafts, instanceId) with
        | Some stored ->
            let unsentBefore = outbox.ForInstance instanceId |> List.length
            DraftPolicy.restoreInto stored instanceId drafts outbox |> ignore
            let unsentRestored = (outbox.ForInstance instanceId |> List.length) - unsentBefore
            let notice = draftSession.Restored(instanceId, unsentRestored)
            let live = drafts.Get(instanceId, activeConvId)
            if DraftRestore.fillComposer composer.Text live.text then composer.SetText live.text
            composer.SetAttachments((attachmentDraft ()).Items)
            notice |> Option.iter (fun message -> toast message Info)
            this.UpdateDraftSummary()
        | None -> ()

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
        let cleared = draftSession.Clear()
        composer.SetText ""
        composer.SetAttachments []
        draftStore.Clear() |> ignore
        this.UpdateDraftSummary()
        this.Render()
        toast cleared Success

    /// 列表里消失的对话 = 已被删除：它的草稿与未确认消息已无处可发，随之清理。
    member private this.PruneDeletedConversationDrafts() =
        if instanceId <> "" then
            let listed = summaries |> List.map _.id |> Set.ofList
            let inFlight =
                outbox.ForInstance instanceId
                |> List.filter (fun item -> not (PendingMessage.canRetry item))
                |> List.map _.conversationId
                |> Set.ofList
            let gone = draftSession.Prune(instanceId, listed, inFlight)
            for id in gone do
                if activeConvId <> Some id then
                    drafts.Remove(instanceId, Some id)
                outbox.DiscardConversation(instanceId, id)

    /// 主机状态行：连接正常时带上「配置有误」提示。
    member private this.UpdateHostStatusLine() =
        if authenticated then
            // 主机地址已在上一行（serverCaption）显示，状态行只说状态，不重复主机名
            sidebar.SetConnection(true, ConversationSurface.hostStatusLine settings.Health.ConfigIssue.IsSome)

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
        Reading.wrap.Wrapped <- prefs.codeWrap
        composer.SetEnterSends prefs.enterSends
        settings.SetPrefs prefs
        // 关闭保存：立即删除本机已存的内容（之后不再写）；重新打开：下一次保存即生效。
        if draftsToggled then
            if not prefs.persistDrafts then
                draftStore.Clear() |> ignore
                draftSession.StorageCleared()
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
                    reconnect.ResetDelay()
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
        // 新的连接让挂起的重连作废（票据随之失效）。
        let attempt = reconnect.Connect()
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
        this.Render()
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
                    if reconnect.IsCurrent attempt && not authenticated then sidebar.SetConnection(false, "正在连接"))
            with ex ->
                ui.Post(fun () ->
                    if reconnect.IsCurrent attempt then
                        this.ReportConnectionIssue(ConnectionIssue.ofException ex)
                        sidebar.SetConnection(false, "连接已断开")
                        this.ScheduleReconnect()
                        this.Render())
        }
        |> Async.Start

    /// 网络恢复或页面回到前台（第 213 节）：正在退避等待的重连立刻执行，并把退避归零；
    /// 没有挂起的重连（已连接，或从未拿到令牌）时什么也不做，不另起第二条连接。
    member private this.ResumeReconnectNow() =
        if reconnect.ResumeNow() then
            pairingRequested <- false
            this.Connect()

    member private this.ScheduleReconnect() =
        match reconnect.Schedule lastToken.IsSome with
        | None -> ()
        | Some(ticket, delay) ->
            // 连接状态一个词：倒计时不进壳状态栏，重连中只报“正在重连”。
            sidebar.SetConnection(false, "正在重连")
            async {
                do! Async.Sleep delay
                ui.Post(fun () ->
                    if reconnect.Fire ticket then
                        pairingRequested <- false
                        this.Connect())
            }
            |> Async.Start

    // ---- 对话操作 ----

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
        let navBefore = this.Navigation
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
        probes.Track(requestId, providerId, DateTimeOffset.UtcNow)
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
        if search.Expire(now, timeout) then sidebar.ApplySearchFailure(sidebar.SearchQueryText, message)
        for (_, pid) in probes.Expire(now, timeout) do
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
        let gate = ReplyGate.regenerate (activeConversation () |> Option.map messagesOf) (runs.Get activeConvId).generationId.IsSome
        match gate, ReplyGate.regenerateText gate with
        | RegenerateGate.Allowed, _ -> this.Regenerate()
        | RegenerateGate.Generating, Some text -> toast text Info
        | _, Some text -> toast text Warning
        | _, None -> ()

    /// Ctrl+Shift+C：复制最新一条有正文的回复。
    member private this.CopyLatestReply() =
        match activeConversation () |> Option.bind (fun view -> ReplyTail.latestText (messagesOf view)) with
        | Some text -> copyToClipboard text
        | None -> toast ReplyGate.nothingToCopyText Warning

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
                let forkAfterId = ForkAnchor.forkAfter (messagesOf view |> List.choose (fun m -> m.commitId)) commitId
                let editedMessage = ForkAnchor.editedMessage edited
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
        | _ -> toast ForkAnchor.noConversationText Warning

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
                settings.OpenAddProvider()),
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
                modelSwitch.Show(anchor, false, options, title, fun picked ->
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
                modelSwitch.Show(anchor, false, options, "新对话使用的模型", fun picked ->
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
                            if UploadFacts.stillOwned authenticated ownerInstance ownerConnection instanceId client.ConnectionGeneration then
                                this.BeginUpload(owner, file.Name, bytes)
                            else toast UploadFacts.disconnectedText Warning)
                    ui.Post(fun () -> composer.Focus())
                with ex ->
                    ui.Post(fun () ->
                        toast (UploadFacts.readFailedText ex.Message) Failure
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
                                if UploadFacts.stillOwned authenticated ownerInstance ownerConnection instanceId client.ConnectionGeneration then
                                    this.BeginUpload(owner, sf.Name, bytes)
                                else toast UploadFacts.disconnectedText Warning)
                        | _ ->
                            let path = item.TryGetLocalPath()
                            if not (String.IsNullOrEmpty path) && File.Exists path then
                                let bytes = File.ReadAllBytes path
                                let name = Path.GetFileName path
                                ui.Post(fun () ->
                                    if UploadFacts.stillOwned authenticated ownerInstance ownerConnection instanceId client.ConnectionGeneration then
                                        this.BeginUpload(owner, name, bytes)
                                    else toast UploadFacts.disconnectedText Warning)
                    ui.Post(fun () -> composer.Focus())
                with ex ->
                    ui.Post(fun () ->
                        toast (UploadFacts.dropReadFailedText ex.Message) Failure
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
                        match UploadFacts.pasteSource hasFiles hasBitmap with
                        | UploadFacts.PasteSource.Files ->
                            async {
                                try
                                    let! files = clipboard.TryGetFilesAsync() |> Async.AwaitTask
                                    if files <> null && files.Length > 0 then
                                        ui.Post(fun () -> this.DropFiles(List.ofArray files))
                                with ex ->
                                    ui.Post(fun () ->
                                        toast (UploadFacts.clipboardFilesFailedText ex.Message) Failure
                                        composer.Focus())
                            } |> Async.Start
                            true
                        | UploadFacts.PasteSource.Bitmap ->
                            let ownerInstance = instanceId
                            let ownerConnection = client.ConnectionGeneration
                            let owner = attachmentDraft ()
                            async {
                                try
                                    let! bitmap = clipboard.TryGetBitmapAsync() |> Async.AwaitTask
                                    if bitmap <> null then
                                        use buffer = new MemoryStream()
                                        let options = PngBitmapEncoderOptions()
                                        bitmap.Save(buffer, options)
                                        let bytes = buffer.ToArray()
                                        let name = UploadFacts.pasteFileName DateTime.Now
                                        ui.Post(fun () ->
                                            // 与选文件、拖入同一归属判断：读完位图时已换主机或重连就不上传。
                                            if UploadFacts.stillOwned authenticated ownerInstance ownerConnection instanceId client.ConnectionGeneration then
                                                this.BeginUpload(owner, name, bytes)
                                            else toast UploadFacts.disconnectedText Warning
                                            composer.Focus())
                                with ex ->
                                    ui.Post(fun () ->
                                        toast (UploadFacts.clipboardImageFailedText ex.Message) Failure
                                        composer.Focus())
                            } |> Async.Start
                            true
                        | UploadFacts.PasteSource.NotHandled -> false
                    else false
            with _ ->
                false

    member private this.BeginUpload(owner: AttachmentDraftController, fileName: string, bytes: byte[]) =
        // 决策 332：超大图片先在后台缩小再走原流程；缩小期间不占界面线程。
        if UploadFacts.shrinkFirst (ImageShrink.needsShrink fileName bytes) (int64 bytes.Length) then
            let ownerInstance = instanceId
            let ownerConnection = client.ConnectionGeneration
            toast (UploadFacts.shrinkingText fileName) Info
            async {
                let! result = ImageShrink.shrinkAsync fileName bytes
                ui.Post(fun () ->
                    if not (UploadFacts.stillOwned authenticated ownerInstance ownerConnection instanceId client.ConnectionGeneration) then
                        toast UploadFacts.disconnectedText Warning
                    else
                        match result with
                        | Error message -> toast message Warning
                        | Ok None -> this.BeginUploadCore(owner, fileName, bytes, None)
                        | Ok(Some shrunk) ->
                            toast (UploadFacts.shrunkText fileName (ImageShrink.note shrunk)) Info
                            this.BeginUploadCore(owner, shrunk.fileName, shrunk.bytes, Some shrunk))
            }
            |> Async.Start
        else this.BeginUploadCore(owner, fileName, bytes, None)

    member private this.BeginUploadCore(owner: AttachmentDraftController, fileName: string, bytes: byte[], shrunk: ShrunkImage option) =
        match MediaTypes.validate fileName (int64 bytes.Length) with
        | Error message -> toast message Warning
        | Ok() ->
            let sha = UploadFacts.sha256Hex bytes
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
            for index, dataBase64 in UploadFacts.chunks bytes do
                send (AttachmentChunk {| attachmentId = attachmentId; index = index; dataBase64 = dataBase64 |})
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
                        ui.Post(fun () -> toast (UploadFacts.savedText fileName) Success)
                with ex ->
                    ui.Post(fun () -> toast (UploadFacts.saveFailedText ex.Message) Failure)
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
                Wanxiang2.TransferDesk.ImportDesk(
                    SharedTheme.manager,
                    overlay.Desk,
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
                Wanxiang2.TransferDesk.ExportDesk(
                    SharedTheme.manager, overlay.Desk, summary.id, summary.title,
                    (fun query ->
                        if authenticated && instanceId = ownerInstance then
                            client.TrySendAtGenerationAsync(client.ConnectionGeneration, ConversationExportRead query)
                        else System.Threading.Tasks.Task.FromResult false),
                    topLevel,
                    ConversationTransfer.lineageOf summaries lineage summary.id)
            exportDialog <- Some dialog
            dialog.Show()

    member private this.CycleTheme() = this.Commands.CycleTheme()

    member private this.FocusSidebarSearch(query: string option) =
        let state = shellDesk.EnsureSearchVisible()
        if not state.compactMode && prefs.sidebarCollapsed <> state.sidebarCollapsed then
            prefs <- { prefs with sidebarCollapsed = state.sidebarCollapsed }
            UiPrefsFile.save prefs
        query |> Option.iter sidebar.SetSearchText
        sidebar.FocusSearch()

    /// 决策 370–379：命令面板条目的输入。每次输入现取，对话列表与当前对话状态永远是新的。
    member private this.PaletteInput() : ShellPaletteInput =
        { hasConversation = activeConvId.IsSome
          presets = [ for p in catalog.presets -> p.id, p.name, Presets.describe catalog p ]
          supportsPresets = HostFeatures.supports hostFeatures HostFeatures.Presets
          supportsImport = HostFeatures.supports hostFeatures HostFeatures.ConversationImport
          themeLabel = ThemePreference.label prefs.theme
          sections = [ for s in Wanxiang2.SettingsScreen.SettingsPart.all -> string s, Wanxiang2.SettingsScreen.SettingsPart.label s, Wanxiang2.SettingsScreen.SettingsPart.scope s ]
          conversations = summaries
          showArchived = prefs.showArchived }

    /// 快捷键、命令面板分派、主题轮转、Escape 链归 Wanxiang2.ShellCommands；这里只给事实与界面面。
    member private this.Commands: ShellCommands =
        match shellCommands with
        | Some c -> c
        | None ->
            let modelAnchor () = if chat.HeaderModelVisible then chat.HeaderModelAnchor else composer.ModelAnchor()
            let c =
                ShellCommands(
                    { OverlayEscape = fun () -> overlay.HandleEscape()
                      IsDialogOpen = fun () -> overlay.IsDialogOpen
                      IsPopupOpen = fun () -> overlay.IsPopupOpen
                      IsSettingsOpen = fun () -> settingsHost.IsVisible
                      IsDrawerOpen = fun () -> this.Navigation.compactMode && this.Navigation.compactNavigationOpen
                      IsGenerating = fun () -> (runs.Get activeConvId).generationId.IsSome
                      HasConversation = fun () -> (activeSummary ()).IsSome
                      VisibleOrder = fun () -> sidebar.GetVisibleOrder()
                      HasPreset = fun id -> catalog.presets |> List.exists (fun p -> p.id = id)
                      PaletteInput = fun () -> this.PaletteInput()
                      Theme = fun () -> prefs.theme
                      CloseSettings = fun () -> this.CloseSettings()
                      CloseDrawer = fun () -> this.SetCompactNavigation false
                      StopGeneration = fun () -> this.StopGeneration()
                      ToggleSidebar = fun () -> this.ToggleSidebar()
                      CloseWindow =
                          // 与右上角叉号同一路径：走 Window.Close 才会触发 MainWindow.Closing 里的几何持久化。
                          // PWA 没有 Window（决策 48），浏览器自身的关闭语义不由我们代理。
                          fun () ->
                              match topLevel () with
                              | :? Window as window -> window.Close(); true
                              | _ -> false
                      NewConversation = fun () -> this.CreateConversation() |> ignore
                      FocusSearch = fun query -> this.FocusSidebarSearch query
                      OpenSettings =
                          fun key ->
                              // 找不到分区时只打开设置页。
                              this.ShowSettings()
                              key
                              |> Option.bind (fun k -> Wanxiang2.SettingsScreen.SettingsPart.all |> List.tryFind (fun s -> (string s).ToLowerInvariant() = k))
                              |> Option.iter settings.ShowSection
                      OpenModelPicker = fun () -> this.ShowModelPicker(modelAnchor ())
                      JumpExchange = fun direction -> chat.JumpExchange direction
                      ScrollToBeginning = fun () -> chat.SmoothScrollToBeginning()
                      ScrollToEnd = fun () -> chat.SmoothScrollToEnd()
                      FocusComposer = fun () -> composer.Focus()
                      CopyLastReply = fun () -> this.CopyLatestReply()
                      RegenerateLast = fun () -> this.RegenerateLatest()
                      TryUndo =
                          // 决策 232：只在「撤销」提示条可见时接管；输入框里的 Ctrl+Z 仍归文本撤销。
                          fun () ->
                              let inTextBox =
                                  match topLevel () with
                                  | null -> false
                                  | tl -> (tl.FocusManager |> Option.ofObj |> Option.bind (fun fm -> fm.GetFocusedElement() |> Option.ofObj)) |> Option.exists (fun f -> match f with :? TextBox as tb -> not (String.IsNullOrEmpty tb.Text) | _ -> false)
                              not inTextBox && overlay.TryInvokeToastAction "撤销"
                      FocusToast = fun () -> overlay.FocusToastAction()
                      ExportConversation = fun () -> activeSummary () |> Option.iter this.ExportConversation
                      OpenConversation = fun id -> this.OpenConversation id
                      ShowShortcuts = fun () -> Dialogs.shortcuts overlay
                      ApplyPreset =
                          fun id -> catalog.presets |> List.tryFind (fun p -> p.id = id) |> Option.iter this.ApplyPreset
                      SavePreset = fun () -> this.SaveConversationAsPreset()
                      Fork =
                          fun () ->
                              match activeConversation () with
                              | Some view ->
                                  let seed = messagesOf view |> List.filter MessageView.isUser |> List.tryLast |> Option.map (fun m -> m.text) |> Option.defaultValue ""
                                  this.ForkFrom(None, seed)
                              | None -> toast ForkAnchor.noConversationText Warning
                      Import = fun () -> this.ImportConversation()
                      AddProvider = fun () -> this.ShowAddProvider()
                      ShowPalette = fun items recent -> palette.Show(items, recent)
                      SaveRecent = fun recent -> paletteRecent <- recent; PaletteRecent.saveRecent recent
                      SaveTheme = fun theme -> this.SavePrefs { prefs with theme = theme }
                      Toast = fun text tone -> toast text (match tone with ShellTone.Info -> Info | ShellTone.Warning -> Warning) },
                    paletteRecent)
            shellCommands <- Some c
            c

    member private this.RunPaletteItem(item: PaletteItem, query: string) = this.Commands.RunPaletteItem(item, query)

    member this.ShowCommandPalette() = this.Commands.ShowPalette()

    member this.ToggleSidebar() =
        // 焦点归宿（compact 打开进抽屉、收起回输入区；桌面折叠焦点在侧栏时回输入区）在 ShellDesk。
        let state = shellDesk.ToggleSidebar()
        if not state.compactMode then
            prefs <- { prefs with sidebarCollapsed = state.sidebarCollapsed }
            UiPrefsFile.save prefs

    member this.HandleShortcut(e: KeyEventArgs) =
        let shortcut = ShortcutRouter.resolveKey (KeyShortcut.ofEvent e)
        (match shortcut with
         | ShortcutAction.NoShortcut -> ()
         | _ -> ParityProbe.hit "shortcut.resolved")
        if this.Commands.HandleShortcut shortcut then e.Handled <- true

    // ---- 协议事件 ----

    /// 协议事件入口：模型部分归 Wanxiang2.ProtocolDispatch，这里只是转交（测试经反射调用）。
    member private this.HandleEvent(ev: WireEvent) = this.Dispatch.Handle ev

    /// ProtocolDispatch 做完模型部分后交回的界面面（认证、配对、目录、健康、设备、审批、
    /// 探测落点、列表快照、历史页、搜索结果、导出、能力表、附件上传、浏览器事务）。
    member private this.Dispatch: ProtocolDispatch =
        match protocolDispatch with
        | Some d -> d
        | None ->
            let d =
                ProtocolDispatch(
                    { State = state; Runs = runs; Outbox = outbox; Commands = commandFeedback
                      Configs = configFeedback; Downloads = downloads; Search = search; Probes = probes },
                    { ActiveConversation = fun () -> activeConvId
                      InstanceId = fun () -> instanceId
                      Send = send
                      Render = fun () -> this.Render()
                      Toast = fun text tone ->
                          toast text (match tone with
                                      | DispatchTone.Info -> Info
                                      | DispatchTone.Success -> Success
                                      | DispatchTone.Warning -> Warning
                                      | DispatchTone.Failure -> Failure)
                      ObserveList = fun () -> observeConversationList ()
                      DispatchPending = fun item -> this.DispatchPendingMessage item
                      Downloaded = fun name bytes -> this.SaveDownload(name, bytes)
                      MissingAttachment = fun sha -> missingAttachments <- missingAttachments.Add sha
                      Apply = fun ev -> this.ApplyHostEvent ev })
            protocolDispatch <- Some d
            d

    member private this.ApplyHostEvent(ev: WireEvent) =
        match ev with
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
            reconnect.ResetDelay()
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
            setConnectStatus ConnectionIssue.pairingStartedStatus
        | PairingSucceeded d ->
            lastToken <- Some d.token
            hasSavedCredentials <- true
            connectionError <- None
            connectionIssue <- None
            setConnectStatus ConnectionIssue.pairingSucceededStatus
            send (AuthPresent {| token = d.token |})
        | PairingFailed d ->
            this.ReportConnectionIssue(ConnectionIssue.ofPairingFailure d.frozen d.freezeMinutes d.reason)
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
            // 配置重新加载成功：之前的「配置有误」随之消失。只在从有误恢复时提示一次。
            match ConfigIssueNotice.recovered (settings.Health.ClearConfigIssue()) with
            | Some text ->
                this.UpdateHostStatusLine()
                toast text Success
            | None -> ()
            // 设备列表可见时跟着刷新（别的设备被吊销、重命名）
            if settings.Devices.IsShown then send DevicesRequest
        | HostConfigRejected d ->
            // 主机拒绝了一份配置并继续用上一份有效的；客户端必须看得见（决策 82）。
            // 先算是否首次有误，再写入。
            let notice = ConfigIssueNotice.rejected settings.Health.ConfigIssue.IsSome d.summary
            settings.Health.SetConfigIssue(d.summary, d.errors)
            this.UpdateHostStatusLine()
            notice |> Option.iter (fun text -> toast text Warning)
        | DoctorReport d ->
            doctorWait.Settle() |> ignore
            settings.Health.SetReport(d.ok, DoctorCheck.parseChecks d.checks)
        | DevicesSnapshot d -> settings.Devices.SetDevices(DevicesModel.parse d.items, d.canInvite)
        | PairingInviteIssued d -> settings.Devices.SetInvite(d.code, d.expiresAtUtc, DateTimeOffset.UtcNow)
        | PairingInviteRejected d -> settings.Devices.SetInviteRejected d.reason
        | ToolApprovalRequested _
        | ToolApprovalResolved _ -> approvalPanel.Handle ev |> ignore
        | ProviderProbeResult d ->
            let models = ProbeModels.ofJson d.models
            // 首次运行卡片自己发起的探测由它处理；其余交给设置页。
            if not (this.Onboarding.ApplyProbe(d.providerId, d.ok, models, d.error)) then
                settings.ApplyProbe(d.providerId, d.ok, models, d.error)
        | ConversationListSnapshot d ->
            summaries <- ConversationSummary.parseList d.items
            lineage <- ForkLineage.parse d.items
            sidebar.SetLineage lineage
            // SetConversations 即快照到达：侧栏加载中状态随数据一并解除。
            sidebar.SetConversations(PendingDeletion.visible summaries deletion.Pending)
            sidebar.SetActive activeConvId
            this.PruneDeletedConversationDrafts()
            listReceived <- true
            this.Render()
            pendingLink |> Option.iter this.OpenMessageLink
            if StartupTrace.mark "listReady" && StartupTrace.exitAfter then
                Avalonia.Threading.DispatcherTimer.RunOnce((fun () -> StartupTrace.requestExit ()), TimeSpan.FromSeconds 3.0) |> ignore
        | ConversationSnapshot d ->
            // 发件箱放行与第二次重画在 ProtocolDispatch 里（次数与旧代码相同）。
            if activeConvId = Some d.conversationId then
                pageLoading <- false
                this.Render()
                if StartupTrace.enabled then
                    ui.Post(
                        (fun () ->
                            if StartupTrace.mark "chatReady" && StartupTrace.exitAfter then StartupTrace.requestExit ()),
                        Avalonia.Threading.DispatcherPriority.Background)
        | HistoryPage d ->
            if activeConvId = Some d.conversationId then
                pageLoading <- false
                pageRequestedAt <- None
                chat.HideOlderPageFailure()
                this.Render()
                chat.RestoreHistoryPrependAnchorDeferred()
        | SearchResults d ->
            // 迟到的旧结果已由 ProtocolDispatch 丢掉。
            sidebar.ApplySearchResults(
                { queryId = d.queryId
                  query = d.query
                  hits = SearchHit.parse d.items
                  hasMore = d.hasMore
                  truncated = d.truncated },
                d.offset)
        | SearchFailed d ->
            sidebar.ApplySearchFailure(sidebar.SearchQueryText, d.message)
        | ConversationExportPage page -> exportDialog |> Option.iter (fun dialog -> dialog.Handle page)
        | ConversationExportFailed d -> exportDialog |> Option.iter (fun dialog -> dialog.Fail(d.exportId, d.message))
        | GenerationFinished d ->
            // PWA 安装引导推迟到第一次成功对话之后（第 210 节）：首次运行时不和引导卡 / 输入框抢位置。
            if d.status = "completed" && OperatingSystem.IsBrowser() then
                try BrowserPage.ConversationSucceeded() with _ -> ()
        | HostCapabilities d ->
            hostFeatures <- Some(Set.ofList d.features)
            this.SyncPresetSurfaces()
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
                toast (UploadFacts.uploadFailedText upload.fileName) Warning
            | None -> ()
        | _ -> ()

    // ---- 组装 ----

    member private this.BuildSidebar() =
        let actions: Wanxiang2.SidebarDesk.SidebarDeskActions =
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
              deleteConversation = fun summary -> deletion.Schedule [ summary.id ]
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
                        deletion.Schedule ids
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
        sidebar <- Wanxiang2.SidebarDesk.SidebarDesk(SharedTheme.manager, overlay.Desk, actions, SharedTheme.logo)
        // W4：消息正文搜索。查询在主机内存投影上完成；每次发出都换一个 queryId，旧结果迟到即丢。
        sidebar.SearchHandler <-
            Some(fun query includeArchived offset ->
              match MessageLink.tryParse query with
              | Some link ->
                sidebar.ResetSearch()
                this.OpenMessageLink link
              | None ->
                let queryId = Guid.CreateVersion7()
                search.Sent(queryId, DateTimeOffset.UtcNow)
                send (
                    SearchQuery
                        { queryId = queryId
                          query = query
                          includeArchived = includeArchived
                          offset = offset
                          limit = SearchLimits.defaultPageSize }))
        sidebar.OpenSearchHit <- fun hit -> this.OpenSearchHit hit
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
        let copyMessageLink (commitId: uint64) =
            match activeConvId with
            | Some convId ->
                let pageUrl = if OperatingSystem.IsBrowser() then (try Some(BrowserPage.Url()) with _ -> None) else None
                copyToClipboard (MessageLink.format pageUrl convId commitId)
            | None -> ()
        let recover (recovery: ErrorRecovery) =
            match recovery with
            | OpenProviderSettings ->
                settings.ShowSection Wanxiang2.SettingsScreen.SettingsPart.Providers
                this.ShowSettings()
            | OpenConversationSettings -> this.ShowSessionContext()
            | SwitchModel -> this.ShowModelPicker(if chat.HeaderModelVisible then chat.HeaderModelAnchor else composer.ModelAnchor())
            | StartNewConversation -> this.CreateConversation() |> ignore
        let messageActions: Wanxiang2.MessageCardView.CardActions =
            { CopyText = copyToClipboard
              Regenerate = fun () -> this.Regenerate()
              EditAndFork = fun message -> this.ForkFrom(message.commitId, message.text)
              CopyLink = Some copyMessageLink
              Recover = Some recover
              DeleteMessage =
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
              DownloadAttachment = fun sha -> send (AttachmentDownloadRequest {| sha256 = sha |})
              // 决策 231：消息里的消息链接在应用内打开，不交给系统浏览器。
              OpenLink = fun url -> match MessageLink.tryParse url with Some link -> this.OpenMessageLink link | None -> openLink url }
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
                    | None -> toast ForkAnchor.noConversationText Warning
              stopGeneration = fun () -> this.StopGeneration()
              requestOlderHistory = fun () -> this.RequestOlderHistory()
              retryLast = fun () -> this.Regenerate()
              toggleSidebar = fun () -> this.ToggleSidebar()
              focusHome = fun () -> composer.Focus()
              message = messageActions }
        chat <- ChatView(chatActions, SharedTheme.logo)
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
        if OlderPageLoad.inFlight pageLoading pageRequestedAt then
            pageLoading <- false
            pageRequestedAt <- None
            chat.CancelHistoryPrependAnchor()
            this.ShowOlderPageRetry message

    member private this.ShowOlderPageRetry(message: string) =
        chat.ShowOlderPageFailure(message, fun () ->
            if authenticated then this.RequestOlderHistory()
            else this.ShowOlderPageRetry OlderPageLoad.offlineText)

    /// 超时检查：只在有请求在途时有事做；tick 本身很轻，不必频繁启停。
    member private this.CheckOlderPageTimeout(now: DateTimeOffset) =
        if OlderPageLoad.timedOut pageLoading pageRequestedAt now MotionLedger.olderPageTimeout then
            this.FailOlderPage OlderPageLoad.timeoutText

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
        let actions: Wanxiang2.SettingsScreen.SettingsHooks =
            { UpsertProvider =
                fun payload onCompleted ->
                    sendConfigWithFeedback
                        "模型服务设置已保存"
                        (fun requestId -> ConfigUpsertProvider {| requestId = requestId; provider = payload |})
                        onCompleted
              DeleteProvider =
                fun id ->
                    Dialogs.confirm overlay "删除模型服务" (sprintf "「%s」将从配置中移除，使用它的对话需要重新选择模型。" id) "删除" (fun () ->
                        sendConfigWithFeedback
                            "模型服务已删除"
                            (fun requestId -> ConfigDeleteProvider {| requestId = requestId; providerId = id |})
                            ignore)
              ProbeProvider = fun id -> this.ProbeProvider id
              UpsertMcp =
                fun payload onCompleted ->
                    sendConfigWithFeedback
                        "MCP 设置已保存"
                        (fun requestId -> ConfigUpsertMcp {| requestId = requestId; server = payload |})
                        onCompleted
              DeleteMcp =
                fun id ->
                    Dialogs.confirm overlay "删除 MCP 服务" (sprintf "「%s」提供的工具将不再可用。" id) "删除" (fun () ->
                        sendConfigWithFeedback
                            "MCP 服务已删除"
                            (fun requestId -> ConfigDeleteMcp {| requestId = requestId; serverId = id |})
                            ignore)
              UpdateGeneration =
                fun payload onCompleted ->
                    sendConfigWithFeedback
                        "生成设置已保存"
                        (fun requestId -> ConfigUpdateGeneration {| requestId = requestId; generation = payload |})
                        onCompleted
              SavePrefs = fun next -> this.SavePrefs next }
        settings <- Wanxiang2.SettingsScreen.SettingsScreen(SharedTheme.manager, overlay.Desk, actions, prefs)
        settings.CloseRequested.Add(fun () -> this.CloseSettings())
        // 检查主机：只读请求；旧主机不认识这个事件会直接忽略，所以要有超时说明。
        settings.Health.CheckRequested.Add(fun () ->
                if authenticated && client.IsConnected then
                    send (DoctorRequest {| requestId = newInvocation () |})
                    doctorWait.Begin DateTimeOffset.UtcNow
                    let timeout = DispatcherTimer(Interval = doctorWait.Limit)
                    timeout.Tick.Add(fun _ ->
                        timeout.Stop()
                        if doctorWait.TimeOut() then
                            match settings.Health.Doctor with
                            | Running -> settings.Health.SetUnavailable BoundedWaits.doctorTimeoutText
                            | _ -> ())
                    timeout.Start()
                else
                    settings.Health.SetUnavailable BoundedWaits.doctorOfflineText)
        settings.LocalDrafts.ClearRequested.Add(fun () ->
                Dialogs.confirm
                    overlay
                    "清除本地草稿"
                    "这台设备上保存的未发送文字、附件名称和未确认的消息会被删除，无法恢复。已发送并保存的对话不受影响。"
                    "清除"
                    (fun () -> this.ClearLocalDrafts()))
        this.UpdateDraftSummary()
        settings.Devices.ListRequested.Add(fun () -> send DevicesRequest)
        settings.Devices.InviteRequested.Add(fun () -> send PairingInvite)
        settings.Devices.RenameRequested.Add(fun (deviceId, name) ->
            sendConfigWithFeedback
                "设备已重命名"
                (fun requestId -> DeviceRename {| requestId = requestId; deviceId = deviceId; name = name |})
                ignore)
        settings.Devices.RevokeRequested.Add(fun device ->
            Dialogs.confirm
                overlay
                "吊销设备"
                (sprintf "「%s」会立即断开，之后需要重新配对才能再连这台主机。" device.name)
                "吊销"
                (fun () ->
                    sendConfigWithFeedback
                        "设备已吊销"
                        (fun requestId -> DeviceRevoke {| requestId = requestId; deviceId = device.id |})
                        ignore))

    /// 卡片按需构建：局部构建视图的调用方（只 Build 了部分区域）也不会撞上空引用。
    member private this.Onboarding: Wanxiang2.SettingsScreen.OnboardingDesk =
        if isNull (box onboarding) then this.BuildOnboarding()
        onboarding

    member private this.BuildOnboarding() =
        let actions: Wanxiang2.SettingsScreen.OnboardingHooks =
            { UpsertProvider =
                fun payload onCompleted ->
                    sendConfigWithFeedback
                        ""
                        (fun requestId -> ConfigUpsertProvider {| requestId = requestId; provider = payload |})
                        onCompleted
              DiscardProvider =
                fun id ->
                    sendConfigWithFeedback
                        ""
                        (fun requestId -> ConfigDeleteProvider {| requestId = requestId; providerId = id |})
                        ignore
              ProbeProvider = fun id -> this.ProbeProvider id
              TakenIds = fun () -> catalog.providers |> List.map (fun p -> p.id)
              DiscoverLocal =
                fun () ->
                    if OnboardingRules.canDiscoverLocal lastUrl then
                        OnboardingRules.discoverOllamaAt ProviderPresets.OllamaProbeUrl
                    else
                        System.Threading.Tasks.Task.FromResult None
              OpenAdvanced = fun () -> this.ShowAddProvider() }
        onboarding <- Wanxiang2.SettingsScreen.OnboardingDesk(SharedTheme.manager, overlay.Desk, actions)

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
        palette.Run.Add(fun (item, query) -> this.RunPaletteItem(item, query))
        approvalPanel.AwaitingChanged.Add(fun _ -> this.SyncAwaitingApproval())
        this.BuildSettings()
        settings.ConfigureImport(fun () ->
            this.CloseSettings()
            this.ImportConversation())
        settings.ConfigurePresets((fun p -> this.RenamePreset p), (fun p -> this.DeletePreset p))
        this.BuildOnboarding()

        // 审批卡放进输入框正上方的附属位：与输入框同列同宽、占布局高度，
        // 不再浮层盖住消息列表最后一条的操作行（视觉走查 11 号截图）。
        composer.SetAccessory approvalPanel.View

        let initialNavigation = this.Navigation
        shellGrid <- Grid()
        SharedTheme.input.Track this
        // 列定义、列宽投影、抽屉遮罩与起步居中都归 ShellDesk（下方构造时落定初值）。
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
            match shellDesk.NoteSidebarDragged() with
            | Some width ->
                prefs <- { prefs with sidebarWidth = width }
                UiPrefsFile.save prefs
            | None -> ())
        let shellHooks : Wanxiang2.ShellDesk.ShellHooks =
            { SetCompactMode =
                fun compact ->
                    composer.SetCompactMode compact
                    sidebar.SetCompactMode compact
                    chat.SetCompactMode compact
              SetStartMode = fun centered -> chat.SetStartMode centered
              FocusComposer = fun () -> composer.Focus()
              FocusSidebarSearch = fun () -> sidebar.FocusSearch()
              PrefersTouch = fun () -> SharedTheme.input.PrefersTouch }
        shellDesk <-
            Wanxiang2.ShellDesk.ShellDesk(
                SharedTheme.manager, shellGrid, sidebar.View, sidebarSplitter, chat, composer,
                shellHooks, initialNavigation.sidebarCollapsed, (fun () -> prefs.sidebarWidth))
        workspace.Children.Add shellGrid

        settingsHost.Children.Add settings.View

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
                    approvalPanel.Disconnected()
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
        Reading.tint.Ready.Add(fun _ ->
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
        let apply (text: string option) =
            ParityProbe.hit "drafts.loaded"
            draftSession.Loaded text
            this.RestoreLocalDrafts()
            this.UpdateDraftSummary()
        let task = draftStore.Load()
        if task.IsCompleted then
            apply (try task.Result with _ -> None)
        else
            task.ContinueWith(fun (t: System.Threading.Tasks.Task<string option>) ->
                let text = if t.IsFaulted then None else t.Result
                ui.Post(fun () -> apply text))
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
