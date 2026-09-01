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
open Avalonia.Platform
open Avalonia.Platform.Storage
open Avalonia.Threading
open Wanxiang.Client
open Wanxiang.Core
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
    do MarkdownRenderer.DefaultCodeWrap <- prefs.codeWrap
    let mutable catalog = Catalog.empty
    let mutable authenticated = false
    let mutable instanceId = ""

    let mutable activeConvId: Guid option = None
    let mutable activeGenerationId: Guid option = None
    let mutable generationStartedAt: DateTimeOffset option = None
    let mutable lastError: GenerationError option = None
    let mutable summaries: ConversationSummary list = []

    let streamText = Text.StringBuilder()
    let streamReasoning = Text.StringBuilder()
    let mutable streamToolCalls: ToolCallView list = []

    /// 无会话时输入的第一条消息：先建会话，快照到达后再发出。
    /// 让用户「打开就能打字」，而不是先被要求点一次新建。
    let mutable pendingFirstMessage: (Guid * string) option = None
    let mutable pendingAttachments: PendingAttachment list = []
    let uploadsInFlight = System.Collections.Generic.Dictionary<Guid, AttachmentUpload>()
    let downloadBuffers = System.Collections.Generic.Dictionary<string, MemoryStream>()
    let mutable downloadMeta: Map<string, string> = Map.empty
    let mutable missingAttachments: Set<string> = Set.empty
    let mutable usageByConv: Map<Guid, GenerationUsage> = Map.empty

    let mutable lastUrl = CredentialStore.defaultServerUrl ()
    let mutable lastToken: string option = None
    let mutable reconnectCts: CancellationTokenSource option = None
    let mutable reconnectDelayMs = ReconnectBackoff.baseDelayMs
    let mutable pairingRequested = false
    let mutable setConnectStatus: string -> unit = ignore
    let mutable pageLoading = false

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
    let mutable splitLayout: Grid = Unchecked.defaultof<Grid>
    let mutable sidebarCollapsed = false
    let workspace = Grid()
    let settingsHost = Grid(IsVisible = false)

    let send (ev: WireEvent) = client.SendAsync ev |> ignore
    let sendCommand (cmd: ClientCommand) = client.SendCommandAsync cmd |> ignore
    let newInvocation () = Guid.CreateVersion7()

    let activeConversation () =
        activeConvId |> Option.bind (fun id -> state.Conversations.TryFind id)

    let activeSummary () =
        activeConvId |> Option.bind (fun id -> summaries |> List.tryFind (fun s -> s.id = id))

    let activeConfig () =
        activeConversation () |> Option.map (fun view -> view.config) |> Option.defaultValue SessionConfig.empty

    let messagesOf (view: ConversationView) =
        view.messages |> Seq.cast<JsonNode> |> Seq.map MessageView.ofSnapshotItem |> List.ofSeq

    member private _.StreamingMessage() : MessageView option =
        if streamText.Length = 0 && streamReasoning.Length = 0 && List.isEmpty streamToolCalls then None
        else
            Some
                { MessageView.empty with
                    role = "assistant"
                    text = streamText.ToString()
                    reasoning = streamReasoning.ToString()
                    toolCalls = streamToolCalls }

    member private this.Render() =
        let generating = activeGenerationId.IsSome
        match activeConvId, activeConversation () with
        | None, _ ->
            chat.SetConversationChrome false
            chat.SetTitle("", false)
            chat.SetModel("", false)
            if not authenticated then
                chat.ShowEmpty(NotConnected, Some("连接服务器", fun () -> this.ShowConnectDialog()))
                composer.SetEnabled(false, "连接服务器后即可开始对话。")
                composer.SetModelLabel "未连接"
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
            chat.SetTitle("加载中…", false)
            chat.HideEmpty()
            composer.SetEnabled(false, "")
            ignore convId
        | Some convId, Some view ->
            let messages = messagesOf view
            let streaming = this.StreamingMessage()
            let summary = activeSummary ()
            chat.SetConversationChrome true
            chat.SetTitle((summary |> Option.map (fun s -> s.title) |> Option.defaultValue "会话"), true)
            chat.SetModel(Catalog.describeModel view.config.provider view.config.model catalog, Catalog.isReady catalog)
            composer.SetModelLabel(Catalog.describeModel view.config.provider view.config.model catalog)
            if List.isEmpty messages && streaming.IsNone && lastError.IsNone then
                chat.ShowEmpty(EmptyConversation, None)
            else
                chat.HideEmpty()
                let retry () = this.Regenerate()
                chat.RenderMessages(
                    messages,
                    streaming,
                    (lastError |> Option.map (fun e -> e, retry)),
                    prefs.fontScale,
                    prefs.autoCollapseReasoning,
                    usageByConv.TryFind convId,
                    missingAttachments)
            composer.SetEnabled(authenticated, (if authenticated then "" else "连接断开，重新连接后可继续发送。"))
        composer.SetGenerating generating
        chat.SetGenerating(generating, (if generating then "生成中" else ""))

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
        prefs <- next
        UiPrefs.save prefs
        if themeChanged then this.ApplyTheme()
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
        match reconnectCts with
        | Some cts -> cts.Cancel()
        | None -> ()
        reconnectCts <- None
        let url = lastUrl
        let token = lastToken
        let usePairing = pairingRequested
        async {
            try
                do! client.ConnectAsync(Uri url, CancellationToken.None) |> Async.AwaitTask
                do! client.SendAsync(Hello {| protocol = "wanxiang"; version = Constants.ProtocolVersion; instanceId = None |}) |> Async.AwaitTask
                match token with
                | Some value -> do! client.SendAsync(AuthPresent {| token = value |}) |> Async.AwaitTask
                | None ->
                    if usePairing then
                        do! client.SendAsync(PairingRequested {| clientName = Some CredentialStore.clientName |}) |> Async.AwaitTask
                Dispatcher.UIThread.Post(fun () -> sidebar.SetConnection(false, "连接中…"))
            with ex ->
                Dispatcher.UIThread.Post(fun () ->
                    setConnectStatus (sprintf "连接失败：%s" ex.Message)
                    sidebar.SetConnection(false, "连接失败")
                    toast (sprintf "无法连接 %s" url) Failure)
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
            sidebar.SetConnection(false, sprintf "%.0f 秒后重连" (float delay / 1000.0))
            async {
                do! Async.Sleep delay
                if not cts.IsCancellationRequested then
                    Dispatcher.UIThread.Post(fun () ->
                        reconnectCts <- None
                        pairingRequested <- false
                        this.Connect())
            }
            |> Async.Start

    // ---- 会话操作 ----

    member private this.CreateConversation() : Guid option =
        if not (Catalog.isReady catalog) then
            toast "先在设置里添加一个服务商。" Warning
            this.ShowSettings()
            None
        else
            let conversationId = Guid.CreateVersion7()
            let provider, model =
                Catalog.defaultSelection catalog |> Option.defaultValue ("", "")
            let config =
                { SessionConfig.empty with
                    provider = provider
                    model = model
                    instructions = catalog.generation.instructions
                    temperature = catalog.generation.temperature
                    topP = catalog.generation.topP
                    maxTokens = catalog.generation.maxTokens }
            sendCommand (
                CreateConversation
                    {| invocationId = newInvocation ()
                       conversationId = conversationId
                       title = "新会话"
                       config = config |})
            activeConvId <- Some conversationId
            sidebar.SetActive activeConvId
            async {
                do! Async.Sleep 250
                Dispatcher.UIThread.Post(fun () ->
                    send (ObserveConversation {| conversationId = conversationId |})
                    composer.Focus())
            }
            |> Async.Start
            Some conversationId

    member private this.OpenConversation(id: Guid) =
        if activeConvId <> Some id then
            activeConvId <- Some id
            lastError <- None
            streamText.Clear() |> ignore
            streamReasoning.Clear() |> ignore
            streamToolCalls <- []
            activeGenerationId <- None
            sidebar.SetActive activeConvId
            this.Render()
        send (ObserveConversation {| conversationId = id |})

    member private this.SendMessage(text: string) =
        match activeConvId with
        | None ->
            if String.IsNullOrWhiteSpace text then ()
            elif not (Catalog.isReady catalog) then
                toast "还没有可用的模型，先在设置里添加一个服务商。" Warning
                this.ShowSettings()
            else
                match this.CreateConversation() with
                | Some conversationId -> pendingFirstMessage <- Some(conversationId, text)
                | None -> ()
        | Some convId ->
            if not client.IsConnected then
                toast "连接已断开，消息未发送。" Failure
            else
                let message = JsonObject()
                message["role"] <- "user"
                let contents = JsonArray()
                if not (String.IsNullOrWhiteSpace text) then
                    let textContent = JsonObject()
                    textContent["text"] <- text
                    contents.Add textContent
                for attachment in pendingAttachments do
                    if attachment.ready then
                        let node = JsonObject()
                        node["type"] <- "attachment"
                        node["sha256"] <- attachment.sha256
                        node["size"] <- attachment.size
                        node["mediaType"] <- attachment.mediaType
                        node["fileName"] <- attachment.fileName
                        contents.Add node
                message["contents"] <- contents
                pendingAttachments <- []
                composer.SetAttachments []
                lastError <- None
                sendCommand (
                    SendUserMessage
                        {| invocationId = newInvocation ()
                           conversationId = convId
                           messageJson = message |})

    member private this.Regenerate() =
        match activeConvId with
        | None -> ()
        | Some convId ->
            lastError <- None
            sendCommand (RegenerateResponse {| invocationId = newInvocation (); conversationId = convId |})
            this.Render()

    member private this.StopGeneration() =
        match activeConvId, activeGenerationId with
        | Some convId, Some generationId ->
            send (GenerationCancel {| conversationId = convId; generationId = generationId |})
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
                sendCommand (
                    ForkConversation
                        {| invocationId = newInvocation ()
                           conversationId = newId
                           parentConversationId = convId
                           forkAfterId = forkAfterId
                           config = SessionConfig.empty
                           editedMessageJson = editedMessage |})
                activeConvId <- Some newId
                async {
                    do! Async.Sleep 250
                    Dispatcher.UIThread.Post(fun () ->
                        send (ObserveConversation {| conversationId = newId |})
                        composer.Focus())
                }
                |> Async.Start)
        | _ -> toast "当前没有可分叉的会话。" Warning

    member private this.ShowSettings() =
        settingsHost.IsVisible <- true
        workspace.IsVisible <- false
        send CatalogRequest

    member private this.CloseSettings() =
        settingsHost.IsVisible <- false
        workspace.IsVisible <- true

    member private this.ShowModelPicker(anchor: Control) =
        match activeConvId, activeConversation () with
        | Some convId, Some view ->
            let groups =
                [ for provider in Catalog.usableProviders catalog ->
                      provider.label,
                      [ for model in provider.models ->
                            MenuEntry.create model (fun () ->
                                let config = { view.config with provider = provider.id; model = model }
                                view.config <- config
                                sendCommand (
                                    UpdateConversationConfig
                                        {| invocationId = newInvocation ()
                                           conversationId = convId
                                           config = config |})
                                toast (sprintf "已切换到 %s · %s" provider.label model) Success
                                this.Render())
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
                        Dispatcher.UIThread.Post(fun () -> this.BeginUpload(file.Name, bytes))
                with ex ->
                    Dispatcher.UIThread.Post(fun () -> toast (sprintf "读取文件失败：%s" ex.Message) Failure)
            }
            |> Async.Start

    member private this.BeginUpload(fileName: string, bytes: byte[]) =
        if bytes.Length = 0 then toast "空文件无法上传。" Warning
        elif int64 bytes.Length > 64L * 1024L * 1024L then toast "附件超过 64 MiB 上限。" Warning
        else
            let sha = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
            let mediaType = MediaTypes.ofFileName fileName
            let attachmentId = Guid.CreateVersion7()
            uploadsInFlight[attachmentId] <-
                { attachmentId = attachmentId
                  fileName = fileName
                  mediaType = mediaType
                  size = int64 bytes.Length
                  sha256 = sha }
            pendingAttachments <-
                pendingAttachments
                @ [ { sha256 = sha
                      size = int64 bytes.Length
                      mediaType = mediaType
                      fileName = fileName
                      ready = false } ]
            composer.SetAttachments pendingAttachments
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
        match state.Conversations.TryFind summary.id with
        | None -> toast "请先打开这个会话再导出。" Warning
        | Some view ->
            let markdown = Export.toMarkdown summary.title (messagesOf view)
            this.SaveDownload(summary.title + ".md", Text.Encoding.UTF8.GetBytes markdown)

    member this.ToggleSidebar() =
        sidebarCollapsed <- not sidebarCollapsed
        sidebar.IsVisible <- not sidebarCollapsed
        if not (isNull (box splitLayout)) && splitLayout.ColumnDefinitions.Count > 0 then
            splitLayout.ColumnDefinitions.[0].Width <- if sidebarCollapsed then GridLength(0.0) else GridLength prefs.sidebarWidth

    member this.HandleShortcut(e: KeyEventArgs) =
        let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
        let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
        if e.Key = Key.Escape then
            if overlay.HandleEscape() then e.Handled <- true
            elif settingsHost.IsVisible then
                e.Handled <- true
                this.CloseSettings()
            elif activeGenerationId.IsSome then
                e.Handled <- true
                this.StopGeneration()
        elif ctrl && e.Key = Key.B then
            e.Handled <- true
            this.ToggleSidebar()
        elif ctrl && e.Key = Key.N then
            e.Handled <- true
            this.CreateConversation() |> ignore
        elif ctrl && e.Key = Key.K then
            e.Handled <- true
            if sidebarCollapsed then this.ToggleSidebar()
            sidebar.FocusSearch()
        elif ctrl && (e.Key = Key.OemComma) then
            e.Handled <- true
            this.ShowSettings()
        elif ctrl && shift && e.Key = Key.S then
            e.Handled <- true
            let nextTheme = if Tokens.isDark() then AlwaysLight else AlwaysDark
            this.SavePrefs { prefs with theme = nextTheme }
            toast (if nextTheme = AlwaysDark then "已切换为深色主题" else "已切换为浅色主题") Neutral
        elif ctrl && shift && e.Key = Key.E then
            e.Handled <- true
            match activeSummary() with
            | Some summary -> this.ExportConversation summary
            | None -> toast "没有打开的会话可导出" Warning
        elif ctrl && not shift && e.Key >= Key.D1 && e.Key <= Key.D9 then
            let index = int e.Key - int Key.D1
            if index < summaries.Length then
                e.Handled <- true
                this.OpenConversation summaries[index].id
        elif ctrl && (e.Key = Key.OemQuestion || e.Key = Key.Divide) then
            e.Handled <- true
            Dialogs.shortcuts overlay
        elif e.Key = Key.F1 then
            e.Handled <- true
            Dialogs.shortcuts overlay

    // ---- 协议事件 ----

    member private this.HandleEvent(ev: WireEvent) =
        match ev with
        | Hello _ -> ()
        | AuthAccepted d ->
            authenticated <- true
            instanceId <- d.instanceId
            // 连接真的成功了，退避归零
            reconnectDelayMs <- ReconnectBackoff.baseDelayMs
            pairingRequested <- false
            overlay.CloseDialog()
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
            lastToken <- None
            sidebar.SetConnection(false, "认证失败")
            setConnectStatus (sprintf "认证失败：%s" d.reason)
            toast (sprintf "认证失败：%s" d.reason) Failure
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
            toast message Failure
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
            activeConvId <- Some d.conversationId
            pageLoading <- false
            sidebar.SetActive activeConvId
            if d.runtimeState = "generating" then
                if activeGenerationId.IsNone then activeGenerationId <- Some Guid.Empty
            else
                activeGenerationId <- None
            this.Render()
            match pendingFirstMessage with
            | Some(conversationId, text) when conversationId = d.conversationId ->
                pendingFirstMessage <- None
                this.SendMessage text
            | _ -> ()
        | MessageCommitted d ->
            state.Handle ev
            state.AdvanceCursor()
            if activeConvId = Some d.conversationId then
                // 这条已成为权威消息，流式临时副本必须立刻作废；
                // 工具循环里会连续提交多条，留着旧缓冲就会看到重复的回复。
                let committed = MessageView.ofJson d.payload
                if not (MessageView.isUser committed) then
                    streamText.Clear() |> ignore
                    streamReasoning.Clear() |> ignore
                    streamToolCalls <- []
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
            pageLoading <- false
            if activeConvId = Some d.conversationId then this.Render()
        | GenerationStarted d ->
            state.Handle ev
            activeGenerationId <- Some d.generationId
            generationStartedAt <- Some DateTimeOffset.UtcNow
            lastError <- None
            streamText.Clear() |> ignore
            streamReasoning.Clear() |> ignore
            streamToolCalls <- []
            this.Render()
        | GenerationDelta d ->
            if activeConvId = Some d.conversationId then
                let view = MessageView.ofJson d.payload
                streamText.Clear().Append(view.text) |> ignore
                streamReasoning.Clear().Append(view.reasoning) |> ignore
                streamToolCalls <- view.toolCalls
                this.Render()
        | GenerationFinished d ->
            state.Handle ev
            state.AdvanceCursor()
            activeGenerationId <- None
            generationStartedAt <- None
            streamText.Clear() |> ignore
            streamReasoning.Clear() |> ignore
            streamToolCalls <- []
            match d.usage with
            | Some usage -> usageByConv <- usageByConv.Add(d.conversationId, usage)
            | None -> ()
            match d.status, d.error with
            | "failed", Some error ->
                lastError <- Some error
                // 当前会话会就地显示错误卡片，再弹提示条只是同一句话说两遍，
                // 而且会盖住输入区。只有失败发生在别的会话时才需要提示条。
                if activeConvId <> Some d.conversationId then
                    toast (GenerationError.display error) Failure
                else
                    Dispatcher.UIThread.Post(fun () -> chat.ScrollToEnd())
            | "cancelled", _ -> toast "已停止生成" Neutral
            | _ -> lastError <- None
            this.Render()
        | AttachmentCommitted d ->
            match uploadsInFlight.TryGetValue d.attachmentId with
            | true, upload ->
                uploadsInFlight.Remove d.attachmentId |> ignore
                pendingAttachments <-
                    pendingAttachments
                    |> List.map (fun a -> if a.sha256 = upload.sha256 then { a with ready = true; size = d.size } else a)
                composer.SetAttachments pendingAttachments
                toast (sprintf "附件「%s」上传完成" upload.fileName) Success
            | _ -> ()
        | AttachmentAborted d ->
            match uploadsInFlight.TryGetValue d.attachmentId with
            | true, upload ->
                uploadsInFlight.Remove d.attachmentId |> ignore
                pendingAttachments <- pendingAttachments |> List.filter (fun a -> a.sha256 <> upload.sha256)
                composer.SetAttachments pendingAttachments
            | _ -> ()
            toast (sprintf "附件上传失败：%s" d.reason) Failure
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
        | CommandRejected d ->
            match d.requiredCommitId with
            | Some _ -> toast "本地数据不是最新，已自动追赶，请重试。" Warning
            | None -> toast (sprintf "操作被拒绝：%s" d.message) Failure
        | ServerError d ->
            // 附件 blob 缺失时把该 sha256 标成缺失，消息里就会显示「内容已丢失」
            if d.message.StartsWith("attachment ", StringComparison.Ordinal) && d.message.Contains "not found" then
                let parts = d.message.Split(' ')
                if parts.Length > 1 then
                    missingAttachments <- missingAttachments.Add(parts[1].ToLowerInvariant())
                    this.Render()
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
                    Dialogs.prompt overlay "重命名会话" "会话标题" summary.title "保存" (fun title ->
                        sendCommand (
                            RenameConversation
                                {| invocationId = newInvocation (); conversationId = summary.id; title = title |}))
              deleteConversation =
                fun summary ->
                    Dialogs.confirm
                        overlay
                        "删除会话"
                        (sprintf "「%s」及其消息将不再出现在列表里。此操作无法撤销。" summary.title)
                        "删除"
                        (fun () ->
                            sendCommand (
                                DeleteConversation {| invocationId = newInvocation (); conversationId = summary.id |})
                            if activeConvId = Some summary.id then
                                activeConvId <- None
                                sidebar.SetActive None
                                this.Render()
                            toast (sprintf "会话「%s」已删除" summary.title) Neutral)
              setPinned =
                fun summary pinned ->
                    sendCommand (
                        SetConversationFlags
                            {| invocationId = newInvocation ()
                               conversationId = summary.id
                               pinned = pinned
                               archived = summary.archived |})
                    toast (if pinned then sprintf "已置顶「%s」" summary.title else sprintf "已取消置顶「%s」" summary.title) Neutral
              setArchived =
                fun summary archived ->
                    sendCommand (
                        SetConversationFlags
                            {| invocationId = newInvocation ()
                               conversationId = summary.id
                               pinned = summary.pinned
                               archived = archived |})
                    toast (if archived then sprintf "已归档「%s」" summary.title else sprintf "已取消归档「%s」" summary.title) Neutral
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
              toggleArchivedVisibility = fun () -> this.SavePrefs { prefs with showArchived = not prefs.showArchived } }
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
                            sendCommand (
                                DeleteMessage
                                    {| invocationId = newInvocation ()
                                       conversationId = convId
                                       messageCommitId = commitId |})
                            toast "消息已删除" Success)
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
              openModelPicker = fun anchor -> this.ShowModelPicker anchor
              openSessionSettings =
                fun () ->
                    match activeConvId with
                    | Some convId ->
                        Dialogs.sessionSettings overlay catalog (activeConfig ()) (fun config ->
                            sendCommand (
                                UpdateConversationConfig
                                    {| invocationId = newInvocation (); conversationId = convId; config = config |})
                            toast "会话设置已更新" Success)
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
              sendPrompt = fun prompt -> this.SendMessage prompt
              toggleSidebar = fun () -> this.ToggleSidebar()
              message = messageActions }
        chat <- ChatView(chatActions, Brand.logo)
        chat.Build()

    member private this.RequestOlderHistory() =
        match activeConvId, activeConversation () with
        | Some convId, Some view when view.pageHasMore && not pageLoading ->
            pageLoading <- true
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
                fun sha ->
                    pendingAttachments <- pendingAttachments |> List.filter (fun a -> a.sha256 <> sha)
                    composer.SetAttachments pendingAttachments
              openModelPicker = fun anchor -> this.ShowModelPicker anchor
              dropFiles = fun files ->
                  for (name, bytes) in files do
                      this.BeginUpload(name, bytes)
                  if files.Length > 0 then
                      toast (sprintf "已添加 %d 个文件附件" files.Length) Neutral }
        composer <- Composer(actions)
        composer.Build()
        composer.SetEnterSends prefs.enterSends

    member private this.BuildSettings() =
        let actions =
            { upsertProvider = fun payload -> send (ConfigUpsertProvider {| requestId = newInvocation (); provider = payload |})
              deleteProvider =
                fun id ->
                    Dialogs.confirm overlay "删除服务商" (sprintf "「%s」将从配置中移除，使用它的会话需要重新选择模型。" id) "删除" (fun () ->
                        send (ConfigDeleteProvider {| requestId = newInvocation (); providerId = id |}))
              probeProvider = fun id -> send (ProviderProbeRequest {| requestId = newInvocation (); providerId = id |})
              upsertMcp = fun payload -> send (ConfigUpsertMcp {| requestId = newInvocation (); server = payload |})
              deleteMcp =
                fun id ->
                    Dialogs.confirm overlay "删除 MCP 服务器" (sprintf "「%s」提供的工具将不再可用。" id) "删除" (fun () ->
                        send (ConfigDeleteMcp {| requestId = newInvocation (); serverId = id |}))
              updateGeneration =
                fun payload ->
                    send (ConfigUpdateGeneration {| requestId = newInvocation (); generation = payload |})
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

        let chatColumn = DockPanel()
        DockPanel.SetDock(composer, Dock.Bottom)
        chatColumn.Children.Add composer
        chatColumn.Children.Add chat

        let split = Grid()
        split.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength prefs.sidebarWidth))
        split.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        Grid.SetColumn(sidebar, 0)
        Grid.SetColumn(chatColumn, 1)
        split.Children.Add sidebar
        split.Children.Add chatColumn
        splitLayout <- split
        workspace.Children.Add split

        settingsHost.Children.Add settings

        root.Children.Insert(0, workspace)
        root.Children.Insert(1, settingsHost)
        this.Content <- root
        this.Background <- Tokens.canvas
        overlay.WireDismiss()

        client.EventReceived.Add(fun ev -> Dispatcher.UIThread.Post(fun () -> this.HandleEvent ev))
        state.CursorChanged.Add(fun _ -> send (state.CursorAdvancedEvent()))
        client.Closed.Add(fun error ->
            Dispatcher.UIThread.Post(fun () ->
                authenticated <- false
                activeGenerationId <- None
                let detail = match error with Some e -> e.Message | None -> ""
                sidebar.SetConnection(false, "连接已断开")
                if not (String.IsNullOrWhiteSpace detail) then toast (sprintf "连接断开：%s" detail) Warning
                this.Render()
                this.ScheduleReconnect()))

        Tokens.Changed.Publish.Add(fun _ -> Dispatcher.UIThread.Post(fun () -> this.Render()))
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

        this.Render()
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
