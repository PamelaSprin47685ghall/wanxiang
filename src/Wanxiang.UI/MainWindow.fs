namespace Wanxiang.UI

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Wanxiang.Client
open Wanxiang.Core
open Wanxiang.Protocol

/// 消息视图（从 Agent Framework 消息 JSON 提取展示数据）。
type MessageView = {
    role: string
    text: string
}

module MessageView =

    let rec private walkText (node: JsonNode) (sb: StringBuilder) =
        match node with
        | null -> ()
        | :? JsonObject as o ->
            let mutable textNode: JsonNode = null
            if o.TryGetPropertyValue("text", &textNode) && not (isNull textNode) && textNode.GetValueKind() = JsonValueKind.String then
                sb.Append(textNode.GetValue<string>()) |> ignore
            let mutable contentsNode: JsonNode = null
            if o.TryGetPropertyValue("contents", &contentsNode) && not (isNull contentsNode) && contentsNode.GetValueKind() = JsonValueKind.Array then
                for c in contentsNode.AsArray() do walkText c sb
        | :? JsonArray as arr ->
            for item in arr do walkText item sb
        | _ -> ()

    let ofJson (node: JsonNode) : MessageView =
        let sb = Text.StringBuilder()
        walkText node sb
        let role =
            match node with
            | :? JsonObject as o ->
                let mutable r: JsonNode = null
                if o.TryGetPropertyValue("role", &r) && not (isNull r) && r.GetValueKind() = JsonValueKind.String then
                    r.GetValue<string>()
                else "unknown"
            | _ -> "unknown"
        { role = role; text = sb.ToString() }

/// 主窗口：会话列表 + 聊天视图 + 连接管理。
type MainWindow() as this =
    inherit Window()

    let client = WsClient()
    let state = ClientState()

    // 控件
    let statusText = TextBlock(Text = "未连接", Foreground = Brushes.Gray)
    let connectButton = Button(Content = "连接", Margin = Thickness(4.0, 0.0, 0.0, 0.0))
    let newButton = Button(Content = "新建会话", Margin = Thickness(4.0, 0.0, 0.0, 0.0))
    let convList = ListBox(Background = Brushes.Transparent)
    let chatTitle = TextBlock(Text = "万象", FontSize = 16.0, FontWeight = FontWeight.Bold)
    let genStatus = TextBlock(Text = "", Foreground = Brushes.Gray, Margin = Thickness(8.0, 0.0, 0.0, 0.0))
    let messagesPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = 8.0)
    let scrollViewer = ScrollViewer(Content = messagesPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
    let inputBox = TextBox(Watermark = "输入消息…", AcceptsReturn = false)
    let sendButton = Button(Content = "发送", IsEnabled = false)

    let mutable activeConvId: Guid option = None
    let mutable pairingRequestedBeforeConnect = false

    do
        this.Title <- "万象"
        this.Width <- 1100.0
        this.Height <- 720.0
        this.MinWidth <- 700.0
        this.MinHeight <- 480.0

        // 顶部工具栏
        let toolbar = StackPanel(Orientation = Orientation.Horizontal, Margin = Thickness(10.0, 8.0))
        toolbar.Children.Add(connectButton)
        toolbar.Children.Add(newButton)
        toolbar.Children.Add(statusText)

        // 左侧会话列表
        let sidebar = DockPanel(Width = 260.0, Background = Brushes.Transparent)
        let sidebarTitle = TextBlock(Text = "会话", Margin = Thickness(10.0, 8.0), FontWeight = FontWeight.Bold)
        DockPanel.SetDock(sidebarTitle, Dock.Top)
        let listScroll = ScrollViewer(Content = convList)
        DockPanel.SetDock(listScroll, Dock.Bottom)
        sidebar.Children.Add(sidebarTitle)
        sidebar.Children.Add(listScroll)

        // 右侧聊天
        let chatHeader = StackPanel(Orientation = Orientation.Horizontal, Margin = Thickness(10.0, 8.0))
        chatHeader.Children.Add(chatTitle)
        chatHeader.Children.Add(genStatus)

        let inputBar = DockPanel(Margin = Thickness(10.0, 8.0))
        DockPanel.SetDock(sendButton, Dock.Right)
        inputBar.Children.Add(sendButton)
        inputBar.Children.Add(inputBox)

        let chat = DockPanel()
        DockPanel.SetDock(chatHeader, Dock.Top)
        DockPanel.SetDock(inputBar, Dock.Bottom)
        chat.Children.Add(chatHeader)
        chat.Children.Add(inputBar)
        chat.Children.Add(scrollViewer)

        let split = Grid()
        split.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(260.0)))
        split.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        Grid.SetColumn(sidebar, 0)
        Grid.SetColumn(chat, 1)
        split.Children.Add(sidebar)
        split.Children.Add(chat)

        let root = DockPanel()
        DockPanel.SetDock(toolbar, Dock.Top)
        root.Children.Add(toolbar)
        root.Children.Add(split)
        this.Content <- root

        // 事件
        connectButton.Click.Add(fun _ -> this.ShowConnectDialog())
        newButton.Click.Add(fun _ -> this.CreateConversation())
        convList.SelectionChanged.Add(fun _ ->
            match convList.SelectedItem with
            | :? (string * Guid) as (_, id) -> this.OpenConversation id
            | _ -> ())
        sendButton.Click.Add(fun _ -> this.SendMessage())
        inputBox.KeyDown.Add(fun e ->
            if e.Key = Avalonia.Input.Key.Enter then
                this.SendMessage())

        client.EventReceived.Add(fun ev -> Dispatcher.UIThread.Post(fun () -> this.HandleEvent ev))
        state.CursorChanged.Add(fun _ ->
            client.SendAsync(state.CursorAdvancedEvent()) |> ignore)
        client.Closed.Add(fun _ -> Dispatcher.UIThread.Post(fun () -> statusText.Text <- "连接已断开"))

    /// 连接对话框（URL + 令牌 / 配对）。
    member private _.ShowConnectDialog() =
        let urlBox = TextBox(Text = "ws://127.0.0.1:8765/ws", Watermark = "服务器地址")
        let tokenBox = TextBox(Watermark = "访问令牌（首次使用可请求配对）")
        let pairCodeBox = TextBox(Watermark = "6 位配对码", MaxLength = 6)
        pairCodeBox.IsVisible <- false
        let pairButton = Button(Content = "首次使用：请求配对", Margin = Thickness(0.0, 4.0, 0.0, 0.0))
        let pairSubmit = Button(Content = "提交配对码", Margin = Thickness(0.0, 4.0, 0.0, 0.0))
        pairSubmit.IsEnabled <- false
        pairButton.Click.Add(fun _ ->
            pairCodeBox.IsVisible <- true
            pairSubmit.IsVisible <- true
            pairSubmit.IsEnabled <- client.IsConnected
            pairingRequestedBeforeConnect <- true
            if client.IsConnected then
                client.SendAsync(PairingRequested {| clientName = Some "wanxiang-desktop" |}) |> ignore)
        pairSubmit.Click.Add(fun _ ->
            client.SendAsync(PairingAttempted {| code = pairCodeBox.Text; clientName = Some "wanxiang-desktop" |}) |> ignore)
        let panel = StackPanel(Spacing = 8.0, Width = 380.0)
        panel.Children.Add(TextBlock(Text = "连接到万象服务器"))
        panel.Children.Add(urlBox)
        panel.Children.Add(tokenBox)
        panel.Children.Add(pairButton)
        panel.Children.Add(pairCodeBox)
        panel.Children.Add(pairSubmit)
        let ok = Button(Content = "连接", HorizontalAlignment = HorizontalAlignment.Stretch)
        ok.Click.Add(fun _ ->
            let url = urlBox.Text
            let token = tokenBox.Text
            if String.IsNullOrWhiteSpace url then ()
            else
                connectButton.Content <- "连接中…"
                async {
                    try
                        do! client.ConnectAsync(Uri url, Threading.CancellationToken.None) |> Async.AwaitTask
                        do! client.SendAsync(Hello {| protocol = "wanxiang"; version = Constants.ProtocolVersion; instanceId = None |}) |> Async.AwaitTask
                        if not (String.IsNullOrWhiteSpace token) then
                            do! client.SendAsync(AuthPresent {| token = token |}) |> Async.AwaitTask
                        elif pairingRequestedBeforeConnect then
                            do! client.SendAsync(PairingRequested {| clientName = Some "wanxiang-desktop" |}) |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () -> pairSubmit.IsEnabled <- true)
                        Dispatcher.UIThread.Post(fun () -> statusText.Text <- "连接中…")
                    with e ->
                        Dispatcher.UIThread.Post(fun () -> statusText.Text <- sprintf "连接失败: %s" e.Message)
                } |> Async.Start)
        panel.Children.Add(ok)
        let dialog = Window(Title = "连接", Content = panel, Width = 420.0, Height = 320.0, WindowStartupLocation = WindowStartupLocation.CenterOwner)
        dialog.ShowDialog(this) |> ignore

    /// 处理服务端事件（UI 线程）。
    member private this.HandleEvent(ev: WireEvent) =
        match ev with
        | Hello d -> ()
        | AuthAccepted d ->
            statusText.Text <- sprintf "已连接 %s" d.instanceId
            connectButton.Content <- "连接"
            client.SendAsync(ObserveConversationList) |> ignore
        | AuthRejected d ->
            statusText.Text <- "认证失败: " + d.reason
            connectButton.Content <- "连接"
        | PairingStarted _ ->
            statusText.Text <- "配对码已输出到服务器终端（stderr）"
        | PairingSucceeded d ->
            statusText.Text <- "配对成功，正在认证…"
            client.SendAsync(AuthPresent {| token = d.token |}) |> ignore
        | PairingFailed d ->
            statusText.Text <- "配对失败: " + d.reason
        | ConversationListSnapshot d ->
            state.Handle ev
            convList.ItemsSource <-
                [| for item in d.items do
                       if item <> null && item.GetValueKind() = JsonValueKind.Object then
                           let o = item.AsObject()
                           let mutable idNode: JsonNode = null
                           let mutable titleNode: JsonNode = null
                           if o.TryGetPropertyValue("conversationId", &idNode) && not (isNull idNode) then
                               match Guid.TryParse(idNode.GetValue<string>()) with
                               | true, g ->
                                   let title =
                                       if o.TryGetPropertyValue("title", &titleNode) && not (isNull titleNode) then titleNode.GetValue<string>()
                                       else "(未命名)"
                                   yield title, g
                               | _ -> () |]
            state.AdvanceCursor()
            if convList.ItemCount > 0 then convList.SelectedIndex <- 0
        | ConversationSnapshot d ->
            state.Handle ev
            state.AdvanceCursor()
            activeConvId <- Some d.conversationId
            chatTitle.Text <- d.title
            messagesPanel.Children.Clear()
            for m in d.messages do
                // 快照消息结构：{ commitId, payload }（决策 79）
                let payload =
                    match m with
                    | :? JsonNode as node when node.GetValueKind() = JsonValueKind.Object ->
                        let mutable p: JsonNode = null
                        if node.AsObject().TryGetPropertyValue("payload", &p) && not (isNull p) then p
                        else node
                    | node -> node
                this.AppendMessage(MessageView.ofJson payload)
            genStatus.Text <- if d.runtimeState = "generating" then "生成中…" else ""
            sendButton.IsEnabled <- true
            inputBox.IsEnabled <- true
        | MessageCommitted d ->
            state.Handle ev
            state.AdvanceCursor()
            if activeConvId = Some d.conversationId then
                this.AppendMessage(MessageView.ofJson d.payload)
        | ConversationUpdated d ->
            state.Handle ev
            state.AdvanceCursor()
        | AuthorityCatchUp d ->
            // 慢客户端追赶（决策 32-34）：ClientState 应用批次并按实际应用游标推进；
            // state.CursorChanged 订阅会自动回发 cursor.advanced 驱动下一批
            state.Handle ev
        | GenerationStarted d ->
            state.Handle ev
            if activeConvId = Some d.conversationId then genStatus.Text <- "生成中…"
        | GenerationDelta d ->
            if activeConvId = Some d.conversationId then
                genStatus.Text <- "生成中…"
        | GenerationFinished d ->
            state.Handle ev
            state.AdvanceCursor()
            if activeConvId = Some d.conversationId then
                genStatus.Text <-
                    match d.status with
                    | "completed" -> ""
                    | "cancelled" -> "已取消"
                    | "failed" -> "失败: " + (d.error |> Option.defaultValue "")
                    | _ -> d.status
        | CommandCommitted _ ->
            ()
        | CommandRejected d ->
            statusText.Text <- sprintf "命令被拒绝: %s (%s)" d.message d.code
        | ServerError d -> statusText.Text <- d.message
        | _ -> ()

    member private _.AppendMessage(mv: MessageView) =
        let roleText =
            match mv.role with
            | "user" -> "你"
            | "assistant" -> "助手"
            | "tool" -> "工具"
            | other -> other
        let bubble = Border(CornerRadius = CornerRadius(10.0), Padding = Thickness(10.0, 8.0), MaxWidth = 620.0)
        let horizontalAlignment =
            match mv.role with
            | "user" -> HorizontalAlignment.Right
            | _ -> HorizontalAlignment.Left
        bubble.HorizontalAlignment <- horizontalAlignment
        let bg =
            match mv.role with
            | "user" -> SolidColorBrush(Color.Parse("#6d5df6"))
            | "tool" -> SolidColorBrush(Color.Parse("#ece9fe"))
            | _ -> SolidColorBrush(Color.Parse("#ffffff"))
        bubble.Background <- bg
        let panel = StackPanel(Spacing = 2.0)
        panel.Children.Add(TextBlock(Text = roleText, FontSize = 11.0, Foreground = Brushes.Gray))
        let fg: IBrush = if mv.role = "user" then SolidColorBrush(Colors.White) :> IBrush else SolidColorBrush(Colors.Black) :> IBrush
        panel.Children.Add(TextBlock(Text = (if String.IsNullOrEmpty mv.text then "(空)" else mv.text), TextWrapping = TextWrapping.Wrap, Foreground = fg))
        bubble.Child <- panel
        messagesPanel.Children.Add(bubble)
        scrollViewer.ScrollToEnd()

    member private this.SendMessage() =
        let text = inputBox.Text
        if String.IsNullOrWhiteSpace text then ()
        else
            match activeConvId with
            | None -> ()
            | Some convId ->
                inputBox.Text <- ""
                let invocationId = Guid.NewGuid()
                let msg = JsonObject()
                msg["role"] <- "user"
                let contents = JsonArray()
                let textContent = JsonObject()
                textContent["text"] <- text
                contents.Add textContent
                msg["contents"] <- contents
                client.SendCommandAsync(SendUserMessage {| invocationId = invocationId; conversationId = convId; messageJson = msg |}) |> ignore
                // 服务端提交后会广播权威 MessageCommitted；避免在此处乐观展示造成重复。

    member private this.CreateConversation() =
        let conversationId = Guid.NewGuid()
        let title = sprintf "会话 %s" (conversationId.ToString("N").Substring(0, 8))
        let cfg =
            { provider = "openai"
              model = "gpt-4o-mini"
              instructions = None
              tools = []
              temperature = None
              maxTokens = None
              extraJson = None }
        client.SendCommandAsync(CreateConversation {| invocationId = Guid.NewGuid(); conversationId = conversationId; title = title; config = cfg |}) |> ignore
        // 延迟 observe（等提交完成）
        async {
            do! Async.Sleep 300
            client.SendAsync(ObserveConversation {| conversationId = conversationId |}) |> ignore
        } |> Async.Start

    member private this.OpenConversation(id: Guid) =
        client.SendAsync(ObserveConversation {| conversationId = id |}) |> ignore
