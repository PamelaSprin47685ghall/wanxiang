namespace Wanxiang.UI

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Controls.Templates
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Controls.Shapes
open Avalonia.Input.Platform
open Avalonia.Media.Imaging
open Avalonia.Styling
open Avalonia.Threading
open Wanxiang.Client
open Wanxiang.Core
open Wanxiang.Protocol

/// 消息视图（从 Agent Framework 消息 JSON 提取展示数据）。
type MessageView = {
    role: string
    text: string
    /// 思维链（reasoning）单独提取，折叠展示
    reasoning: string
}

module MessageView =

    let rec private walkParts (node: JsonNode) (text: StringBuilder) (reasoning: StringBuilder) =
        match node with
        | null -> ()
        | :? JsonObject as o ->
            let getStrProp (k: string) =
                let mutable n: JsonNode = null
                if o.TryGetPropertyValue(k, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then Some(n.GetValue<string>()) else None
            let typeName =
                match getStrProp "$type" with
                | Some t -> Some t
                | None -> getStrProp "type"
            match typeName with
            | Some "reasoning" ->
                match getStrProp "text" with
                | Some t -> reasoning.Append t |> ignore
                | None -> ()
            | _ ->
                match getStrProp "text" with
                | Some t -> text.Append t |> ignore
                | None -> ()
                let mutable contentsNode: JsonNode = null
                if o.TryGetPropertyValue("contents", &contentsNode) && not (isNull contentsNode) && contentsNode.GetValueKind() = JsonValueKind.Array then
                    for c in contentsNode.AsArray() do walkParts c text reasoning
        | :? JsonArray as arr ->
            for item in arr do walkParts item text reasoning
        | _ -> ()

    let ofJson (node: JsonNode) : MessageView =
        let sb = Text.StringBuilder()
        let rs = Text.StringBuilder()
        walkParts node sb rs
        let role =
            match node with
            | :? JsonObject as o ->
                let mutable r: JsonNode = null
                if o.TryGetPropertyValue("role", &r) && not (isNull r) && r.GetValueKind() = JsonValueKind.String then
                    r.GetValue<string>()
                else "unknown"
            | _ -> "unknown"
        { role = role; text = sb.ToString(); reasoning = rs.ToString() }

/// 消息中的附件引用（客户端写入 contents 的 `{"type":"attachment",...}` 项）。
type AttachmentRef = {
    sha256: string
    size: int64
    mediaType: string
    fileName: string
}

module AttachmentRef =

    let extract (payload: JsonNode) : AttachmentRef list =
        let results = System.Collections.Generic.List<AttachmentRef>()
        let rec walk (node: JsonNode) =
            match node with
            | null -> ()
            | :? JsonArray as a ->
                for item in a do walk item
            | :? JsonObject as o ->
                let mutable t: JsonNode = null
                if o.TryGetPropertyValue("type", &t) && not (isNull t) && t.GetValueKind() = JsonValueKind.String && t.GetValue<string>() = "attachment" then
                    let getStr k =
                        let mutable n: JsonNode = null
                        if o.TryGetPropertyValue(k, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then n.GetValue<string>() else ""
                    let size =
                        let mutable n: JsonNode = null
                        if o.TryGetPropertyValue("size", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
                            match n with
                            | :? JsonValue as v ->
                                match v.TryGetValue<int64>() with true, i -> i | _ -> 0L
                            | _ -> 0L
                        else 0L
                    results.Add({ sha256 = getStr "sha256"; size = size; mediaType = getStr "mediaType"; fileName = getStr "fileName" })
                else
                    for kv in o do walk kv.Value
            | _ -> ()
        walk payload
        List.ofSeq results

/// 会话列表条目（ListBox 模板数据）。
open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines

type ConvSummary = {
    Id: Guid
    Title: string
    Preview: string
    Running: bool
}

type TextSegment =
    | NormalText of string
    | CodeBlock of lang: string * code: string

module MarkdownParser =
    let pipeline = MarkdownPipelineBuilder().UseAdvancedExtensions().Build()

    let rec private inlineText (i: Inline) : string =
        match i with
        | :? LiteralInline as lit -> lit.Content.ToString()
        | :? LineBreakInline -> "\n"
        | :? ContainerInline as c -> String.Concat(seq { for ch in c do yield inlineText ch })
        | _ -> ""

    let rec private blockText (b: Block) : string =
        match b with
        | :? LeafBlock as leaf when not (isNull leaf.Inline) -> inlineText leaf.Inline
        | :? ContainerBlock as c -> String.Join("\n", [ for ch in c do yield blockText ch ])
        | _ -> ""

    let parse (raw: string) : TextSegment list =
        if String.IsNullOrEmpty raw then []
        else
            try
                let doc = Markdown.Parse(raw, pipeline)
                let results = System.Collections.Generic.List<TextSegment>()
                for node in doc do
                    match node with
                    | :? FencedCodeBlock as f ->
                        let lang = if String.IsNullOrEmpty f.Info then "code" else f.Info
                        let codeLines = [ for i = 0 to f.Lines.Count - 1 do yield f.Lines.Lines[i].ToString() ]
                        let code = String.Join("\n", codeLines)
                        results.Add(CodeBlock(lang, code))
                    | block ->
                        let text = blockText block
                        if not (String.IsNullOrWhiteSpace text) then
                            results.Add(NormalText text)
                if results.Count > 0 then List.ofSeq results
                else [ NormalText raw ]
            with _ ->
                [ NormalText raw ]

/// 主窗口：会话列表 + 聊天视图 + 连接管理。
/// 万象主视图（决策 48：桌面与 PWA 共用同一套 UI 代码；桌面由 MainWindow 窗口壳承载，PWA 直接作为单视图内容）。
/// 应用内对话框（连接/配对/重命名/删除/fork）统一用遮罩 overlay 实现，避免平台窗口差异。
type MainView() as this =
    inherit UserControl()

    // 内嵌 Sarasa Term SC（browser-wasm 无系统字体可访问；桌面与 PWA 统一字形）
    do this.FontFamily <- FontFamily("avares://Wanxiang.UI/Assets/Fonts/#Sarasa Term SC")

    let client = WsClient()
    let state = ClientState()

    // ---- 控件（视觉语言见 Theme.fs）----
    let statusText = TextBlock(Text = "未连接", Foreground = Theme.muted, FontSize = 12.0, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis)
    let connDot = Ellipse(Width = 8.0, Height = 8.0, Fill = Theme.faint, VerticalAlignment = VerticalAlignment.Center)
    let connectButton = Button(Content = "连接", Height = 32.0, Padding = Thickness(14.0, 0.0), FontSize = 12.5, CornerRadius = CornerRadius(9.0), Background = Theme.secondaryContainer, Foreground = Theme.onSecondaryContainer, BorderThickness = Thickness(0.0), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center)
    let newButton = Button(Content = "✦  新建", Height = 30.0, Padding = Thickness(10.0, 0.0), CornerRadius = CornerRadius(8.0), Background = Theme.secondaryContainer, Foreground = Theme.onSecondaryContainer, BorderThickness = Thickness(0.0), FontSize = 12.0, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center)
    do ToolTip.SetTip(newButton, "新建会话")
    let searchBox = TextBox(PlaceholderText = "搜索会话…", CornerRadius = CornerRadius(10.0), Margin = Thickness(12.0, 0.0, 12.0, 8.0), Padding = Thickness(10.0, 7.0), BorderThickness = Thickness(1.0), BorderBrush = Theme.border, Background = Theme.panel, FontSize = 12.5)
    let convList = ListBox(Background = Brushes.Transparent, BorderThickness = Thickness(0.0))
    let chatTitle = TextBlock(Text = "选择一个会话", FontSize = 15.0, FontWeight = FontWeight.SemiBold, Foreground = Theme.faint, VerticalAlignment = VerticalAlignment.Center)
    let genStatus = TextBlock(Text = "", FontSize = 12.0, Foreground = Theme.onPrimaryContainer)
    let genDot = Ellipse(Width = 6.0, Height = 6.0, Fill = Theme.primary)
    let genChip = Border(Background = Theme.primaryContainer, CornerRadius = CornerRadius(999.0), Padding = Thickness(10.0, 4.0, 12.0, 4.0), IsVisible = false, VerticalAlignment = VerticalAlignment.Center)
    do
        // 后设置 Child：避免在 ctor 表达式中传 Children 数组（Avalonia StackPanel 不可）
        let genInner = StackPanel(Orientation = Orientation.Horizontal, Spacing = 6.0)
        genInner.Children.Add(genDot) |> ignore
        genInner.Children.Add(genStatus) |> ignore
        genChip.Child <- genInner
    let forkButton = Button(Content = "编辑并 fork", Height = 30.0, Padding = Thickness(10.0, 0.0), FontSize = 12.5, CornerRadius = CornerRadius(8.0), Background = Brushes.Transparent, Foreground = Theme.muted, BorderThickness = Thickness(0.0), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = Thickness(0.0, 0.0, 4.0, 0.0))
    let cancelButton = Button(Content = "取消生成", Height = 30.0, Padding = Thickness(10.0, 0.0), FontSize = 12.5, CornerRadius = CornerRadius(8.0), Background = Brushes.Transparent, Foreground = Theme.muted, BorderThickness = Thickness(0.0), IsEnabled = false, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center)
    do forkButton.Classes.Add("ghost")
    do cancelButton.Classes.Add("ghost")
    let messagesPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = 20.0)
    let emptyHint = TextBlock(Text = "", Foreground = Theme.muted, FontSize = 13.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false)
    let messagesHost = Grid()
    let scrollViewer = ScrollViewer(Content = messagesHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
    let inputBox = TextBox(PlaceholderText = "输入消息，Enter 发送，Shift+Enter 换行", AcceptsReturn = false, BorderThickness = Thickness(0.0), Background = Brushes.Transparent, FontSize = 14.0, VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 36.0, Padding = Thickness(2.0, 0.0, 0.0, 0.0))
    let sendButton = Button(Content = "↵", Width = 36.0, Height = 36.0, Padding = Thickness(0.0), CornerRadius = CornerRadius(18.0), Background = Theme.border, Foreground = Theme.faint, BorderThickness = Thickness(0.0), FontSize = 18.0, FontWeight = FontWeight.Bold, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center)
    let attachButton = Button(Content = "⊕ 附件", Height = 36.0, Padding = Thickness(12.0, 0.0), FontSize = 12.5, CornerRadius = CornerRadius(18.0), Background = Theme.secondaryContainer, Foreground = Theme.onSecondaryContainer, BorderThickness = Thickness(0.0), IsEnabled = false, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = Thickness(0.0, 0.0, 6.0, 0.0))

    let mutable activeConvId: Guid option = None
    let mutable pairingRequestedBeforeConnect = false
    let mutable rawSummaries: ConvSummary array = [||]
    /// 当前生成（用于取消，决策 88-92）
    let mutable activeGenerationId: Guid option = None
    /// 流式累积文本（P1-4：生成期间临时渲染，不记账）
    let mutable streamText = Text.StringBuilder()
    /// 流式思维链累积（与 streamText 同步重建）
    let mutable streamReasoning = Text.StringBuilder()
    /// 待发送附件引用（上传完成后随下一条用户消息写入，决策 71-72）
    let mutable pendingAttachment: AttachmentRef option = None
    /// 附件下载缓冲（sha256 -> 内容）与元数据
    let downloadBuffers = System.Collections.Generic.Dictionary<string, MemoryStream>()
    let mutable downloadMeta: Map<string, string * string> = Map.empty // sha256 -> (fileName, mediaType)
    /// 下载确认缺失的附件（P2-3/Q179：渲染为“附件缺失”）
    let mutable missingAttachments: Set<string> = Set.empty
    /// 断线重连（P1-5：指数退避，重连后重新 observe）
    let mutable lastUrl = CredentialStore.defaultServerUrl ()
    let mutable lastToken: string option = None
    let mutable reconnectCts: CancellationTokenSource option = None
    let mutable reconnectDelayMs = 1000
    let mutable lastCloseInfo = ""
    /// 历史分页（P1-2/Q127）
    let mutable pageLoading = false
    /// 生成状态循环动画（“生成中… / 生成中···”）
    let mutable genTimer: DispatcherTimer option = None
    /// 认证状态（本地跟踪，驱动状态点颜色）
    let mutable authenticated = false

    /// TopLevel 成员（Clipboard/StorageProvider）需经 TopLevel.GetTopLevel 获取（UserControl 非 TopLevel）。
    let topLevel () = TopLevel.GetTopLevel(this)

    // ---- 应用内对话框（overlay 遮罩；桌面与 PWA 共用，避免平台窗口差异）----
    let dialogOverlay = Grid(IsVisible = false, Background = SolidColorBrush(Color.Parse "#66000000"))
    let dialogCard =
        Border(
            Background = Theme.panel, CornerRadius = CornerRadius(16.0), Padding = Thickness(24.0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center)
    let showDialog (content: Control) (width: float) =
        dialogCard.Child <- content
        dialogCard.Width <- width
        dialogOverlay.IsVisible <- true
    let closeDialog () =
        dialogOverlay.IsVisible <- false
        dialogCard.Child <- null
    do
        dialogCard.BoxShadow <- BoxShadows(BoxShadow(OffsetX = 0.0, OffsetY = 16.0, Blur = 48.0, Spread = -8.0, Color = Color.Parse "#1F1A1B2140"))
        dialogOverlay.Children.Add(dialogCard)
        // 点击遮罩（卡片外）关闭对话框
        dialogOverlay.PointerPressed.Add(fun e ->
            let pos = e.GetPosition(dialogCard)
            if pos.X < 0.0 || pos.Y < 0.0 || pos.X > dialogCard.Bounds.Width || pos.Y > dialogCard.Bounds.Height then
                closeDialog ())
        // Esc 关闭（键盘可达性，Q197）
        dialogOverlay.KeyDown.Add(fun e ->
            if e.Key = Avalonia.Input.Key.Escape then
                e.Handled <- true
                closeDialog ())

    // ---- 视觉辅助 ----
    let tryLoadLogo () : Bitmap option =
        try
            let candidates = [
                Path.Combine(AppContext.BaseDirectory, "logo.png")
                Path.Combine(AppContext.BaseDirectory, "pwa", "logo.png")
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "logo.png")
                Path.Combine(Environment.CurrentDirectory, "logo.png")
                let home = match Environment.GetEnvironmentVariable "WANXIANG_HOME" with s when not (String.IsNullOrWhiteSpace s) -> s | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")
                Path.Combine(home, "logo.png")
            ]
            candidates
            |> List.map Path.GetFullPath
            |> List.tryFind File.Exists
            |> Option.map (fun path -> new Bitmap(path))
        with _ -> None

    let logoBitmap = tryLoadLogo ()

    let createBrandTile (size: float) (radius: float) =
        let border = Border(Width = size, Height = size, CornerRadius = CornerRadius(radius), Background = Theme.primary, ClipToBounds = true)
        match logoBitmap with
        | Some bmp ->
            // 透明 PNG 在浅色主画布上会“消失”——加一层柔色描边 + 阴影，让品牌体现出来
            let img = Image(Source = bmp, Stretch = Stretch.Uniform)
            border.Child <- img
            border.BoxShadow <- BoxShadows(BoxShadow(OffsetX = 0.0, OffsetY = 1.0, Blur = 3.0, Spread = 0.0, Color = Color.Parse "#1A1B2129"))
        | None ->
            let txt = TextBlock(Text = "万", Foreground = Theme.onPrimary, FontSize = size * 0.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center)
            border.Child <- txt
        border

    // 空状态：品牌标 + 标题 + 提示（与 PWA 空状态一致）
    let emptyPanel = StackPanel(Spacing = 14.0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false)
    do
        let emptyLogo = createBrandTile 88.0 24.0
        emptyLogo.HorizontalAlignment <- HorizontalAlignment.Center
        let emptyTitle = TextBlock(Text = "万象 · 智能工作站", FontSize = 20.0, FontWeight = FontWeight.SemiBold, Foreground = Theme.text, HorizontalAlignment = HorizontalAlignment.Center)
        emptyPanel.Children.Add(emptyLogo) |> ignore
        emptyPanel.Children.Add(emptyTitle) |> ignore
        emptyPanel.Children.Add(emptyHint) |> ignore

    let setStatus (text: string) (connected: bool) =
        statusText.Text <- text
        connDot.Fill <- if connected then SolidColorBrush(Color.Parse "#34A853") else Theme.faint

    /// 会话列表选中项与 activeConvId 双向对齐的单一入口：
    /// - 有激活会话时，选中其在 rawSummaries 中的位置（找不到则不动，避免误跳）
    /// - 无激活会话时，若当前无选中且列表非空，则默认选中首项（冷启动体验）
    /// 调用点：activeConvId 改变后（ConversationSnapshot / ConfirmDelete / Reconnect 观察恢复）
    /// 以及 rawSummaries 改变后（ConversationListSnapshot / searchBox 过滤）
    /// 避免在同一次事件链中重复触发 SelectionChanged：用 `if SelectedIndex <> i` 短路。
    let syncSelection () =
        match activeConvId with
        | Some cid ->
            match rawSummaries |> Array.tryFindIndex (fun c -> c.Id = cid) with
            | Some i -> if convList.SelectedIndex <> i then convList.SelectedIndex <- i
            | None -> ()
        | None ->
            if convList.SelectedIndex < 0 && convList.ItemCount > 0 then
                convList.SelectedIndex <- 0

    let setInputsEnabled (enabled: bool) =
        inputBox.IsEnabled <- enabled
        sendButton.IsEnabled <- enabled
        attachButton.IsEnabled <- enabled
        sendButton.Background <- if enabled then Theme.primary else Theme.border
        sendButton.Foreground <- if enabled then Theme.onPrimary else Theme.faint

    let stopGenTimer () =
        match genTimer with
        | Some t -> t.Stop(); genTimer <- None
        | None -> ()

    let startGenTimer () =
        stopGenTimer ()
        let frames = [| "生成中"; "生成中."; "生成中.."; "生成中..." |]
        let mutable i = 0
        genStatus.Text <- "生成中"
        let t = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 550.0)
        t.Tick.Add(fun _ ->
            i <- (i + 1) % frames.Length
            genStatus.Text <- frames[i])
        t.Start()
        genTimer <- Some t

    let showGenChip (visible: bool) = genChip.IsVisible <- visible

    let formatSize (n: int64) =
        if n < 1024L then sprintf "%d B" n
        elif n < 1024L * 1024L then sprintf "%.1f KiB" (float n / 1024.0)
        else sprintf "%.1f MiB" (float n / (1024.0 * 1024.0))

    do
        this.Background <- Theme.bg

        // 品牌标（左上角）：小标 + 名称 + 副名（节点式身份），右端 + 新建
        let brandTile = createBrandTile 30.0 9.0
        let appName = TextBlock(Text = "万象", FontSize = 15.5, FontWeight = FontWeight.SemiBold, Foreground = Theme.text, VerticalAlignment = VerticalAlignment.Center)
        let headerSpacer = Border()
        let sidebarHeaderPanel = DockPanel()
        DockPanel.SetDock(brandTile, Dock.Left)
        DockPanel.SetDock(appName, Dock.Left)
        DockPanel.SetDock(newButton, Dock.Right)
        // 用 8px 留白代替紧贴，避免小标文字
        sidebarHeaderPanel.Children.Add(brandTile)
        sidebarHeaderPanel.Children.Add(Border(Width = 9.0))
        sidebarHeaderPanel.Children.Add(appName)
        sidebarHeaderPanel.Children.Add(newButton)
        sidebarHeaderPanel.Children.Add(headerSpacer)
        let sidebarHeader = Border(Height = 56.0, Padding = Thickness(16.0, 0.0, 12.0, 0.0), BorderBrush = Theme.border, BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0), Child = sidebarHeaderPanel)

        // 会话列表模板：两行（标题 + 预览）+ 右键菜单（重命名/删除，D15 桌面端会话管理）
        convList.ItemTemplate <-
            FuncDataTemplate(
                typeof<ConvSummary>,
                fun (item: obj) (_: INameScope) ->
                    let c = item :?> ConvSummary
                    let title = TextBlock(Text = c.Title, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Theme.text, TextTrimming = TextTrimming.CharacterEllipsis)
                    let preview = TextBlock(Text = c.Preview, FontSize = 12.0, Foreground = Theme.muted, TextTrimming = TextTrimming.CharacterEllipsis)
                    let panel = StackPanel(Spacing = 3.0)
                    panel.Children.Add(title) |> ignore
                    panel.Children.Add(preview) |> ignore
                    let renameItem = MenuItem(Header = "重命名")
                    renameItem.Click.Add(fun _ -> this.BeginRename c)
                    let deleteItem = MenuItem(Header = "删除")
                    deleteItem.Click.Add(fun _ -> this.ConfirmDelete c)
                    panel.ContextMenu <- ContextMenu()
                    panel.ContextMenu.Items.Add(renameItem) |> ignore
                    panel.ContextMenu.Items.Add(deleteItem) |> ignore
                    panel :> Control)

        // 会话列表条目容器：圆角、留白、选中态（与 PWA 视觉一致）
        let itemBase = Style(fun x -> x.OfType<ListBoxItem>())
        itemBase.Setters.Add(Setter(ListBoxItem.PaddingProperty, Thickness(14.0, 10.0)))
        itemBase.Setters.Add(Setter(ListBoxItem.MarginProperty, Thickness(10.0, 2.0)))
        itemBase.Setters.Add(Setter(ListBoxItem.CornerRadiusProperty, CornerRadius(12.0)))
        itemBase.Setters.Add(Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent))
        convList.Styles.Add(itemBase)
        // 选中：背景染色 + 左侧 3px 品牌色描边（只画左边、其它边为 None，避免抖动）
        let itemAccent = Style(fun x -> x.OfType<ListBoxItem>().Class(":selected"))
        itemAccent.Setters.Add(Setter(ListBoxItem.BorderBrushProperty, Theme.primary))
        itemAccent.Setters.Add(Setter(ListBoxItem.BorderThicknessProperty, Thickness(3.0, 0.0, 0.0, 0.0)))
        itemAccent.Setters.Add(Setter(ListBoxItem.BackgroundProperty, Theme.primaryContainer))
        convList.Styles.Add(itemAccent)

        // 幽灵按钮（头部操作）悬停反馈：固定背景覆盖了 Fluent 默认 pointerover，手动补回
        let ghostHover = Style(fun x -> x.OfType<Button>().Class("ghost").Class(":pointerover"))
        ghostHover.Setters.Add(Setter(Button.BackgroundProperty, Theme.hover))
        ghostHover.Setters.Add(Setter(Button.ForegroundProperty, Theme.text))
        this.Styles.Add(ghostHover)

        // 侧栏（无硬分割线，靠底色与主画布区分）
        let sidebar = DockPanel(Background = Theme.sidebar)
        let sidebarBorder = Border(Child = sidebar)
        let listLabel = TextBlock(Text = "会话", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Theme.faint, Margin = Thickness(24.0, 14.0, 0.0, 6.0), LetterSpacing = 60.0)
        let listPanel = StackPanel()
        listPanel.Children.Add(searchBox) |> ignore
        listPanel.Children.Add(listLabel) |> ignore
        listPanel.Children.Add(convList) |> ignore
        let listScroll = ScrollViewer(Content = listPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
        let footer = Border(Height = 56.0, BorderBrush = Theme.borderSubtle, BorderThickness = Thickness(0.0, 1.0, 0.0, 0.0), Padding = Thickness(20.0, 0.0))
        let footerPanel = DockPanel()
        let footerSpacer = Border()
        DockPanel.SetDock(connDot, Dock.Left)
        DockPanel.SetDock(connectButton, Dock.Right)
        footerPanel.Children.Add(connDot)
        footerPanel.Children.Add(connectButton)
        footerPanel.Children.Add(statusText)
        footerPanel.Children.Add(footerSpacer) // 占满剩余空间
        footer.Child <- footerPanel
        DockPanel.SetDock(sidebarHeader, Dock.Top)
        DockPanel.SetDock(footer, Dock.Bottom)
        sidebar.Children.Add(sidebarHeader)
        sidebar.Children.Add(footer)
        sidebar.Children.Add(listScroll)

        // 聊天头部（透明背景，极细分隔）。标题左右加节奏：左侧头像 + 文本，右侧操作三件套
        let chatHeader = Border(Height = 60.0, Background = Theme.bg, BorderBrush = Theme.borderSubtle, BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0), Padding = Thickness(24.0, 0.0))
        let headerSpacer1 = Border(Width = 12.0)
        let headerPanel = DockPanel()
        let headerFiller = Border()
        DockPanel.SetDock(genChip, Dock.Left)
        DockPanel.SetDock(cancelButton, Dock.Right)
        DockPanel.SetDock(forkButton, Dock.Right)
        chatHeader.Child <- headerPanel
        headerPanel.Children.Add(chatTitle)
        headerPanel.Children.Add(headerSpacer1)
        headerPanel.Children.Add(genChip)
        headerPanel.Children.Add(cancelButton)
        headerPanel.Children.Add(forkButton)
        headerPanel.Children.Add(headerFiller) // 占满剩余空间

        // 输入区（悬浮作曲器，与阅读列同宽居中）。送 Send/Stop 复用 sendButton 的样式与状态
        let inputBar = DockPanel()
        DockPanel.SetDock(sendButton, Dock.Right)
        DockPanel.SetDock(attachButton, Dock.Right)
        inputBar.Children.Add(sendButton)
        inputBar.Children.Add(attachButton)
        inputBar.Children.Add(inputBox)
        let inputShell =
            Border(
                Background = Theme.panel, BorderBrush = Theme.border, BorderThickness = Thickness(1.0),
                CornerRadius = CornerRadius(22.0), Padding = Thickness(14.0, 5.0, 6.0, 5.0),
                MaxWidth = 760.0, HorizontalAlignment = HorizontalAlignment.Stretch, Child = inputBar)
        inputShell.BoxShadow <- BoxShadows(BoxShadow(OffsetX = 0.0, OffsetY = 6.0, Blur = 24.0, Spread = -4.0, Color = Color.Parse "#1A1B2129"))
        // 焦点环：输入框获得焦点时，shell 描边换为品牌色，阴影染色——避免纯黑外发光
        inputBox.GotFocus.Add(fun _ ->
            inputShell.BorderBrush <- Theme.primary
            inputShell.BoxShadow <- BoxShadows(BoxShadow(OffsetX = 0.0, OffsetY = 6.0, Blur = 24.0, Spread = -2.0, Color = Color.Parse "#4D5C9238")))
        inputBox.LostFocus.Add(fun _ ->
            inputShell.BorderBrush <- Theme.border
            inputShell.BoxShadow <- BoxShadows(BoxShadow(OffsetX = 0.0, OffsetY = 6.0, Blur = 24.0, Spread = -4.0, Color = Color.Parse "#1A1B2129")))
        let inputWrap =
            Border(
                Background = Brushes.Transparent, Padding = Thickness(24.0, 10.0, 24.0, 22.0),
                HorizontalAlignment = HorizontalAlignment.Stretch, Child = inputShell)

        // 聊天区（消息居中阅读列，760px）
        messagesPanel.MaxWidth <- 760.0
        messagesPanel.HorizontalAlignment <- HorizontalAlignment.Center
        emptyPanel.MaxWidth <- 760.0
        let chat = DockPanel()
        messagesHost.Children.Add(messagesPanel)
        messagesHost.Children.Add(emptyPanel)
        scrollViewer.Padding <- Thickness(24.0, 24.0, 24.0, 12.0)
        DockPanel.SetDock(chatHeader, Dock.Top)
        DockPanel.SetDock(inputWrap, Dock.Bottom)
        chat.Children.Add(chatHeader)
        chat.Children.Add(inputWrap)
        chat.Children.Add(scrollViewer)

        // 左右分栏
        let split = Grid()
        split.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(264.0)))
        split.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        Grid.SetColumn(sidebarBorder, 0)
        Grid.SetColumn(chat, 1)
        split.Children.Add(sidebarBorder)
        split.Children.Add(chat)
        // 对话框遮罩覆盖整个视图（跨两列，Z 序最上）
        Grid.SetColumnSpan(dialogOverlay, 2)
        split.Children.Add(dialogOverlay)
        this.Content <- split
        // 启动即渲染空状态（无事件时也展示品牌区）
        this.RenderMessages()

        searchBox.TextChanged.Add(fun _ ->
            let q = if String.IsNullOrWhiteSpace searchBox.Text then "" else searchBox.Text.Trim().ToLowerInvariant()
            if String.IsNullOrEmpty q then
                convList.ItemsSource <- rawSummaries
            else
                convList.ItemsSource <-
                    rawSummaries
                    |> Array.filter (fun c -> c.Title.ToLowerInvariant().Contains q || c.Preview.ToLowerInvariant().Contains q)
            // 过滤可能把 activeConvId 项隐藏：保持头部仍指向原会话，选中态由 syncSelection 决定
            syncSelection ())
        newButton.Click.Add(fun _ ->
            this.CreateConversation()
            // 点击后把焦点交给输入框，避免按钮保留焦点时按 Space/Enter 误触发重复新建
            inputBox.Focus() |> ignore)
        forkButton.Click.Add(fun _ -> this.ForkConversation())
        connectButton.Click.Add(fun _ -> this.ShowConnectDialog())
        convList.SelectionChanged.Add(fun _ ->
            match convList.SelectedItem with
            | :? ConvSummary as c -> this.OpenConversation c.Id
            | _ -> ())
        sendButton.Click.Add(fun _ -> this.SendMessage())
        attachButton.Click.Add(fun _ -> this.PickAttachment())
        cancelButton.Click.Add(fun _ ->
            // 决策 88-92：携带 generationId 精确取消
            match activeConvId, activeGenerationId with
            | Some convId, Some gid ->
                client.SendAsync(GenerationCancel {| conversationId = convId; generationId = gid |}) |> ignore
            | _ -> ())
        inputBox.KeyDown.Add(fun e ->
            if e.Key = Avalonia.Input.Key.Enter then
                this.SendMessage())
        // P1-2：滚动到顶部时请求更早历史（页边界为稳定 commitID）
        scrollViewer.ScrollChanged.Add(fun e ->
            if scrollViewer.Offset.Y <= 0.0 && e.ExtentDelta.Y <= 0.0 then
                this.RequestHistory())

        client.EventReceived.Add(fun ev -> Dispatcher.UIThread.Post(fun () -> this.HandleEvent ev))
        state.CursorChanged.Add(fun _ ->
            client.SendAsync(state.CursorAdvancedEvent()) |> ignore)
        client.Closed.Add(fun err ->
            Dispatcher.UIThread.Post(fun () ->
                authenticated <- false
                match err with
                | Some e ->
                    lastCloseInfo <- e.GetType().Name + ": " + e.Message
                    setStatus ("连接已断开: " + lastCloseInfo) false
                | None ->
                    lastCloseInfo <- ""
                    setStatus "连接已断开" false
                this.ScheduleReconnect()))

        // P2-5：桌面客户端令牌（client.toml）存在时自动连接本机 S（决策 64：server+client 同进程自动配对）
        // PWA：IndexedDB 凭据（决策 52/53、Q191：按 instanceId 主键）异步读取后自动连接
        match CredentialStore.tryLoadClientToml () with
        | Some(url, token) ->
            lastUrl <- url
            lastToken <- Some token
            Dispatcher.UIThread.Post(fun () -> this.Reconnect())
        | None ->
            if OperatingSystem.IsBrowser() then
                async {
                    let! conn = CredentialStore.tryLoadBrowserConnectionAsync () |> Async.AwaitTask
                    match conn with
                    | Some(url, token, _) when not client.IsConnected ->
                        Dispatcher.UIThread.Post(fun () ->
                            lastUrl <- url
                            lastToken <- Some token
                            this.Reconnect())
                    | _ -> ()
                } |> Async.Start

    /// 连接对话框（URL + 令牌 / 配对）。
    member private _.ShowConnectDialog() =
        let urlBox = TextBox(Text = CredentialStore.defaultServerUrl (), PlaceholderText = "服务器地址", CornerRadius = CornerRadius(10.0), Padding = Thickness(12.0, 9.0), FontSize = 13.0)
        let tokenBox = TextBox(PlaceholderText = "访问令牌（首次使用可请求配对）", CornerRadius = CornerRadius(10.0), Padding = Thickness(12.0, 9.0), FontSize = 13.0)
        let pairCodeBox = TextBox(PlaceholderText = "6 位配对码", MaxLength = 6, CornerRadius = CornerRadius(10.0), Padding = Thickness(12.0, 9.0), FontSize = 14.0, HorizontalContentAlignment = HorizontalAlignment.Center, LetterSpacing = 240.0)
        pairCodeBox.IsVisible <- false
        let pairButton = Button(Content = "首次使用？请求配对", Margin = Thickness(0.0, 4.0, 0.0, 0.0), CornerRadius = CornerRadius(10.0), Background = Theme.secondaryContainer, Foreground = Theme.onSecondaryContainer, BorderThickness = Thickness(0.0), Padding = Thickness(12.0, 8.0), FontSize = 12.0, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center)
        let pairSubmit = Button(Content = "提交配对码", Margin = Thickness(0.0, 4.0, 0.0, 0.0), CornerRadius = CornerRadius(10.0), Background = Theme.secondaryContainer, Foreground = Theme.onSecondaryContainer, BorderThickness = Thickness(0.0), Padding = Thickness(12.0, 8.0), FontSize = 12.0, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center)
        pairSubmit.IsVisible <- false
        pairSubmit.IsEnabled <- false
        pairButton.Click.Add(fun _ ->
            pairCodeBox.IsVisible <- true
            pairSubmit.IsVisible <- true
            pairSubmit.IsEnabled <- client.IsConnected
            pairingRequestedBeforeConnect <- true
            if client.IsConnected then
                client.SendAsync(PairingRequested {| clientName = Some CredentialStore.clientName |}) |> ignore)
        pairSubmit.Click.Add(fun _ ->
            client.SendAsync(PairingAttempted {| code = pairCodeBox.Text; clientName = Some CredentialStore.clientName |}) |> ignore)
        let panel = StackPanel(Spacing = 12.0, Width = 400.0)
        let title = TextBlock(Text = "连接到万象服务器", FontSize = 17.0, FontWeight = FontWeight.SemiBold, Foreground = Theme.text)
        let subtitle = TextBlock(Text = "输入服务器地址与访问令牌；首次使用可请求配对。", FontSize = 12.5, Foreground = Theme.muted, TextWrapping = TextWrapping.Wrap, LineHeight = 18.0)
        // 分隔线：作为"主要输入 / 辅助操作"两个分区的视觉断点
        let sep = Border(Height = 1.0, Background = Theme.border, Margin = Thickness(0.0, 4.0, 0.0, 4.0))
        let fields = StackPanel(Spacing = 10.0)
        fields.Children.Add(urlBox) |> ignore
        fields.Children.Add(tokenBox) |> ignore
        let ok = Button(Content = "连接", HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = CornerRadius(10.0), Background = Theme.primary, Foreground = Theme.onPrimary, BorderThickness = Thickness(0.0), Padding = Thickness(12.0, 10.0), FontWeight = FontWeight.SemiBold, FontSize = 13.5)
        let aux = StackPanel(Spacing = 8.0)
        aux.Children.Add(pairButton) |> ignore
        aux.Children.Add(pairCodeBox) |> ignore
        aux.Children.Add(pairSubmit) |> ignore
        panel.Children.Add(title)
        panel.Children.Add(subtitle)
        panel.Children.Add(sep) |> ignore
        panel.Children.Add(fields) |> ignore
        panel.Children.Add(ok)
        panel.Children.Add(aux) |> ignore
        let ok = Button(Content = "连接", HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = CornerRadius(10.0), Background = Theme.primary, Foreground = Theme.onPrimary, BorderThickness = Thickness(0.0), Padding = Thickness(12.0, 9.0), FontWeight = FontWeight.SemiBold)
        ok.Click.Add(fun _ ->
            let url = urlBox.Text
            let token = tokenBox.Text
            if String.IsNullOrWhiteSpace url then ()
            else
                connectButton.IsEnabled <- false
                lastUrl <- url
                lastToken <- if String.IsNullOrWhiteSpace token then None else Some token
                // 手动连接取消待执行的重连
                match reconnectCts with
                | Some c -> c.Cancel()
                | None -> ()
                reconnectCts <- None
                reconnectDelayMs <- 1000
                async {
                    try
                        do! client.ConnectAsync(Uri url, Threading.CancellationToken.None) |> Async.AwaitTask
                        do! client.SendAsync(Hello {| protocol = "wanxiang"; version = Constants.ProtocolVersion; instanceId = None |}) |> Async.AwaitTask
                        if not (String.IsNullOrWhiteSpace token) then
                            do! client.SendAsync(AuthPresent {| token = token |}) |> Async.AwaitTask
                        elif pairingRequestedBeforeConnect then
                            do! client.SendAsync(PairingRequested {| clientName = Some CredentialStore.clientName |}) |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () -> pairSubmit.IsEnabled <- true)
                        Dispatcher.UIThread.Post(fun () -> setStatus "连接中…" false)
                    with e ->
                        Dispatcher.UIThread.Post(fun () ->
                            setStatus (sprintf "连接失败: %s" e.Message) false
                            connectButton.IsEnabled <- true)
                } |> Async.Start)
        showDialog panel 400.0

    /// 处理服务端事件（UI 线程）。
    member private this.HandleEvent(ev: WireEvent) =
        match ev with
        | Hello d -> ()
        | AuthAccepted d ->
            authenticated <- true
            setStatus (sprintf "已连接 · %s" d.instanceId) true
            connectButton.IsEnabled <- true
            closeDialog ()
            reconnectDelayMs <- 1000
            // PWA：认证成功后把连接凭据按 instanceId 写入 IndexedDB（决策 52/53、Q191）
            if OperatingSystem.IsBrowser() then
                match lastToken with
                | Some token ->
                    CredentialStore.saveBrowserConnectionAsync d.instanceId lastUrl token CredentialStore.clientName |> ignore
                | None -> ()
            match reconnectCts with
            | Some c -> c.Cancel()
            | None -> ()
            reconnectCts <- None
            client.SendAsync(ObserveConversationList) |> ignore
            // P1-5：重连后恢复此前观察的会话
            match activeConvId with
            | Some cid -> client.SendAsync(ObserveConversation {| conversationId = cid |}) |> ignore
            | None -> ()
        | AuthRejected d ->
            authenticated <- false
            setStatus ("认证失败: " + d.reason) false
            connectButton.IsEnabled <- true
            lastToken <- None
        | PairingStarted _ ->
            setStatus "配对码已输出到服务器终端（stderr）" false
        | PairingSucceeded d ->
            setStatus "配对成功，正在认证…" false
            lastToken <- Some d.token
            client.SendAsync(AuthPresent {| token = d.token |}) |> ignore
        | PairingFailed d ->
            setStatus ("配对失败: " + d.reason) false
        | ConversationListSnapshot d ->
            state.Handle ev
            rawSummaries <-
                [| for item in d.items do
                       if item <> null && item.GetValueKind() = JsonValueKind.Object then
                           let o = item.AsObject()
                           let mutable idNode: JsonNode = null
                           let mutable titleNode: JsonNode = null
                           let mutable runtimeNode: JsonNode = null
                           let mutable lastMsgNode: JsonNode = null
                           if o.TryGetPropertyValue("conversationId", &idNode) && not (isNull idNode) then
                               match Guid.TryParse(idNode.GetValue<string>()) with
                               | true, g ->
                                   let title =
                                       if o.TryGetPropertyValue("title", &titleNode) && not (isNull titleNode) then titleNode.GetValue<string>()
                                       else "(未命名)"
                                   let runtime =
                                       if o.TryGetPropertyValue("runtimeState", &runtimeNode) && not (isNull runtimeNode) && runtimeNode.GetValueKind() = JsonValueKind.String then runtimeNode.GetValue<string>()
                                       else "idle"
                                   let lastMsg =
                                       if o.TryGetPropertyValue("lastMessage", &lastMsgNode) && not (isNull lastMsgNode) && lastMsgNode.GetValueKind() = JsonValueKind.String then lastMsgNode.GetValue<string>()
                                       else ""
                                   yield { Id = g; Title = title; Preview = lastMsg; Running = (runtime = "generating") }
                               | _ -> () |]
            let q = if String.IsNullOrWhiteSpace searchBox.Text then "" else searchBox.Text.Trim().ToLowerInvariant()
            if String.IsNullOrEmpty q then
                convList.ItemsSource <- rawSummaries
            else
                convList.ItemsSource <- rawSummaries |> Array.filter (fun c -> c.Title.ToLowerInvariant().Contains q || c.Preview.ToLowerInvariant().Contains q)
            state.AdvanceCursor()
            syncSelection ()
        | ConversationSnapshot d ->
            state.Handle ev
            state.AdvanceCursor()
            activeConvId <- Some d.conversationId
            chatTitle.Text <- d.title
            chatTitle.Foreground <- Theme.text
            pageLoading <- false
            this.RenderMessages()
            showGenChip (d.runtimeState = "generating")
            if d.runtimeState = "generating" then startGenTimer () else stopGenTimer ()
            setInputsEnabled true
            // syncSelection 必须放在 chatTitle/RenderMessages 之后、UI 帧内：
            // 设置 SelectedIndex 会触发 SelectionChanged → OpenConversation，该服务端快照幂等。
            // 在某些事件序列下，列表快照可能尚未到达，目标不在 rawSummaries，syncSelection 会安全跳过。
            syncSelection ()
        | MessageCommitted d ->
            state.Handle ev
            state.AdvanceCursor()
            if activeConvId = Some d.conversationId then
                this.RenderMessages()
        | ConversationUpdated d ->
            state.Handle ev
            state.AdvanceCursor()
            // P2-6/P3-6：会话摘要（含 runtimeState）变化后重新 observe 列表
            client.SendAsync(ObserveConversationList) |> ignore
        | AuthorityCatchUp d ->
            // 慢客户端追赶（决策 32-34）：ClientState 应用批次并按实际应用游标推进；
            // state.CursorChanged 订阅会自动回发 cursor.advanced 驱动下一批
            state.Handle ev
        | HistoryPage d ->
            state.Handle ev
            pageLoading <- false
            if activeConvId = Some d.conversationId then
                this.RenderMessages()
        | GenerationStarted d ->
            state.Handle ev
            activeGenerationId <- Some d.generationId
            cancelButton.IsEnabled <- true
            streamText.Clear() |> ignore
            showGenChip true
            startGenTimer ()
            if activeConvId = Some d.conversationId then
                this.RenderMessages()
        | GenerationDelta d ->
            // P1-4：流式文本实时渲染（临时增量，不记账，决策 17）
            if activeConvId = Some d.conversationId then
                let mv = MessageView.ofJson d.payload
                streamText.Clear() |> ignore
                streamText.Append mv.text |> ignore
                streamReasoning.Clear() |> ignore
                streamReasoning.Append mv.reasoning |> ignore
                this.RenderMessages()
        | GenerationFinished d ->
            state.Handle ev
            state.AdvanceCursor()
            activeGenerationId <- None
            cancelButton.IsEnabled <- false
            stopGenTimer ()
            showGenChip false
            streamText.Clear() |> ignore
            if activeConvId = Some d.conversationId then
                match d.status with
                | "completed" -> ()
                | "cancelled" -> setStatus "已取消生成" (authenticated)
                | "failed" -> setStatus ("生成失败: " + (d.error |> Option.defaultValue "未知错误")) (authenticated)
                | other -> setStatus ("生成结束: " + other) (authenticated)
                this.RenderMessages()
        | AttachmentCommitted d ->
            // 上传完成：附件引用待下一条用户消息携带（P1-4/决策 71-72）
            let mediaType, fileName =
                match pendingAttachment with
                | Some r when r.sha256 = d.sha256 -> r.mediaType, r.fileName
                | _ -> "application/octet-stream", d.sha256
            pendingAttachment <- Some { sha256 = d.sha256; size = d.size; mediaType = mediaType; fileName = fileName }
            setStatus (sprintf "已选择附件: %s" fileName) (authenticated)
        | AttachmentAborted d ->
            pendingAttachment <- None
            setStatus ("附件上传失败: " + d.reason) (authenticated)
        | AttachmentDownloadBegin d ->
            downloadBuffers[d.sha256.ToLowerInvariant()] <- new MemoryStream()
            downloadMeta <- downloadMeta.Add(d.sha256.ToLowerInvariant(), (d.fileName, d.mediaType))
        | AttachmentDownloadChunk d ->
            match downloadBuffers.TryGetValue(d.sha256.ToLowerInvariant()) with
            | true, ms ->
                try
                    let bytes = Convert.FromBase64String d.dataBase64
                    ms.Write(bytes, 0, bytes.Length)
                with _ -> ()
            | _ -> ()
        | AttachmentDownloadComplete d ->
            let key = d.sha256.ToLowerInvariant()
            match downloadBuffers.TryGetValue key with
            | true, ms ->
                let fileName = downloadMeta.TryFind key |> Option.map fst |> Option.defaultValue d.sha256
                let bytes = ms.ToArray()
                ms.Dispose()
                downloadBuffers.Remove key |> ignore
                this.SaveDownload(fileName, bytes)
            | _ -> ()
        | CommandCommitted _ ->
            ()
        | CommandRejected d ->
            setStatus (sprintf "命令被拒绝: %s (%s)" d.message d.code) (authenticated)
        | ServerError d ->
            setStatus d.message (authenticated)
            // P2-3/Q179：附件缺失标记（blob 被删时下载失败）
            if d.message.StartsWith "attachment " && d.message.Contains "not found" then
                let sha = d.message.Substring("attachment ".Length, 64)
                missingAttachments <- missingAttachments.Add sha
                this.RenderMessages()
        | _ -> ()

    member private _.AppendMessage(mv: MessageView, refs: AttachmentRef list, streaming: bool) =
        let isUser = mv.role = "user"
        let isTool = mv.role = "tool"
        // 用户：唯一保留的“气泡”，收窄柔和；助手/工具：无边框融入画布（避免大方块感）
        let bubble = Border(Padding = Thickness(0.0), MaxWidth = 760.0)
        if isUser then
            bubble.Padding <- Thickness(15.0, 10.0)
            bubble.CornerRadius <- CornerRadius(18.0, 18.0, 6.0, 18.0)
            bubble.Background <- Theme.primary
            bubble.MaxWidth <- 520.0
            bubble.HorizontalAlignment <- HorizontalAlignment.Right
        elif isTool then
            bubble.CornerRadius <- CornerRadius(999.0)
            bubble.Background <- Theme.toolChip
            bubble.Padding <- Thickness(14.0, 6.0)
            bubble.MaxWidth <- 460.0
            bubble.HorizontalAlignment <- HorizontalAlignment.Center
        else
            bubble.Background <- Brushes.Transparent
            bubble.HorizontalAlignment <- HorizontalAlignment.Stretch
        let panel = StackPanel(Spacing = 6.0)
        // 思维链：左侧竖线引用式折叠（流式期间展开，提交后收起，与 PWA 思考链视觉一致）
        if not isUser && not isTool && not (String.IsNullOrEmpty mv.reasoning) then
            // 自定义折叠开关（轻量链接样本——Fluent 默认 Expander 带硬边框、与无边框基调不符）
            let thinkBody = TextBlock(Text = mv.reasoning, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Theme.muted, LineHeight = 19.0, IsVisible = streaming, Margin = Thickness(0.0, 6.0, 0.0, 0.0))
            let chevron = TextBlock(Text = (if streaming then "▾" else "▸"), FontSize = 10.0, Foreground = Theme.primary, VerticalAlignment = VerticalAlignment.Center, Margin = Thickness(0.0, 0.0, 6.0, 0.0))
            let thinkLabel = TextBlock(Text = "思考过程", FontSize = 12.0, FontWeight = FontWeight.SemiBold, Foreground = Theme.muted, LetterSpacing = 20.0)
            let toggleRow = StackPanel(Orientation = Orientation.Horizontal, Cursor = Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand))
            toggleRow.Children.Add(chevron) |> ignore
            toggleRow.Children.Add(thinkLabel) |> ignore
            // Q197：键盘可达——可聚焦（Tab），Enter/Space 切换折叠
            toggleRow.Focusable <- true
            Avalonia.Automation.AutomationProperties.SetName(toggleRow, "切换思考过程显示")
            let toggle () =
                thinkBody.IsVisible <- not thinkBody.IsVisible
                chevron.Text <- if thinkBody.IsVisible then "▾" else "▸"
            toggleRow.PointerPressed.Add(fun _ -> toggle ())
            toggleRow.KeyDown.Add(fun e ->
                if e.Key = Avalonia.Input.Key.Enter || e.Key = Avalonia.Input.Key.Space then
                    e.Handled <- true
                    toggle ())
            let thinkStack = StackPanel(Spacing = 0.0)
            thinkStack.Children.Add(toggleRow) |> ignore
            thinkStack.Children.Add(thinkBody) |> ignore
            let thinkAccent =
                Border(
                    BorderBrush = Theme.primary, BorderThickness = Thickness(2.0, 0.0, 0.0, 0.0),
                    Padding = Thickness(14.0, 4.0, 0.0, 4.0), Margin = Thickness(0.0, 0.0, 0.0, 8.0),
                    Child = thinkStack)
            panel.Children.Add(thinkAccent) |> ignore
        let fg: IBrush = if isUser then Theme.userText :> IBrush else Theme.text :> IBrush
        
        if not (String.IsNullOrEmpty mv.text) then
            if isUser then
                panel.Children.Add(TextBlock(Text = mv.text, TextWrapping = TextWrapping.Wrap, Foreground = fg, FontSize = 14.5, LineHeight = 21.0))
            else
                let segments = MarkdownParser.parse mv.text
                for seg in segments do
                    match seg with
                    | NormalText text ->
                        panel.Children.Add(TextBlock(Text = text, TextWrapping = TextWrapping.Wrap, Foreground = fg, FontSize = 14.5, LineHeight = 24.0))
                    | CodeBlock(lang, code) ->
                        let card = Border(Background = SolidColorBrush(Color.Parse "#0D1117"), BorderBrush = SolidColorBrush(Color.Parse "#21262D"), BorderThickness = Thickness(1.0), CornerRadius = CornerRadius(12.0), Margin = Thickness(0.0, 8.0), Padding = Thickness(0.0))
                        let cardStack = StackPanel(Spacing = 0.0)
                        let headerDock = DockPanel()
                        let header = Border(Background = SolidColorBrush(Color.Parse "#161B22"), BorderBrush = SolidColorBrush(Color.Parse "#21262D"), BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0), Padding = Thickness(14.0, 8.0), Child = headerDock)

                        // 语言标签：纯文本 + 字符间距，配合右上的复制按钮（去掉撞色的 mac-dot，让整体走同一套调色）
                        let langLabel = TextBlock(Text = lang.ToLowerInvariant(), FontSize = 11.0, Foreground = SolidColorBrush(Color.Parse "#8B949E"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, LetterSpacing = 40.0)
                        let copyBtn = Button(Content = "复制", FontSize = 11.0, Padding = Thickness(10.0, 3.0), CornerRadius = CornerRadius(6.0), Background = SolidColorBrush(Color.Parse "#21262D"), Foreground = SolidColorBrush(Color.Parse "#C9D1D9"), BorderThickness = Thickness(0.0), VerticalAlignment = VerticalAlignment.Center)
                        copyBtn.Click.Add(fun _ ->
                            try
                                (topLevel ()).Clipboard.SetTextAsync(code) |> ignore
                                copyBtn.Content <- "已复制 ✓"
                                async {
                                    do! Async.Sleep 1600
                                    Dispatcher.UIThread.Post(fun () -> copyBtn.Content <- "复制")
                                } |> Async.Start
                            with _ -> ())

                        DockPanel.SetDock(langLabel, Dock.Left)
                        DockPanel.SetDock(copyBtn, Dock.Right)
                        headerDock.Children.Add(langLabel)
                        headerDock.Children.Add(copyBtn)
                        headerDock.Children.Add(Border()) // spacer

                        let codeText = TextBlock(Text = code, FontFamily = FontFamily("ui-monospace, SFMono-Regular, Menlo, Consolas, monospace"), FontSize = 12.5, Foreground = SolidColorBrush(Color.Parse "#C9D1D9"), Margin = Thickness(14.0, 12.0, 14.0, 14.0))
                        let codeScroll = ScrollViewer(Content = codeText, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled)
                        cardStack.Children.Add(header)
                        cardStack.Children.Add(codeScroll)
                        card.Child <- cardStack
                        panel.Children.Add(card)
        // 附件引用（P1-4）：可点击下载；缺失时标记（P2-3）
        for r in refs do
            if missingAttachments.Contains r.sha256 then
                let missingText = TextBlock(Text = sprintf "附件缺失: %s（原文件已删除）" r.fileName, FontSize = 12.0, Foreground = Theme.faint, Margin = Thickness(0.0, 4.0, 0.0, 0.0))
                panel.Children.Add missingText
            else
                let link = Button(Content = sprintf "下载附件：%s (%s)" r.fileName (formatSize r.size), FontSize = 12.0, Padding = Thickness(10.0, 4.0), CornerRadius = CornerRadius(999.0), Background = Theme.secondaryContainer, Foreground = Theme.onSecondaryContainer, BorderThickness = Thickness(0.0), HorizontalAlignment = HorizontalAlignment.Left, Margin = Thickness(0.0, 4.0, 0.0, 0.0))
                link.Click.Add(fun _ -> this.DownloadAttachment r.sha256)
                panel.Children.Add link
        bubble.Child <- panel
        if isTool then
            messagesPanel.Children.Add(bubble)
        else
            // 头像行：助手用品牌标，用户用圆形文字徽标（与 PWA 一致）
            let avatar: Control =
                if isUser then
                    let b = Border(Width = 30.0, Height = 30.0, CornerRadius = CornerRadius(15.0), Background = Theme.primary, BorderBrush = Brushes.Transparent, BorderThickness = Thickness(0.0))
                    b.Child <- TextBlock(Text = "你", FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Theme.onPrimary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center)
                    b :> Control
                else
                    createBrandTile 30.0 8.0 :> Control
            avatar.VerticalAlignment <- VerticalAlignment.Top
            avatar.Margin <- Thickness(0.0, 2.0, 0.0, 0.0)
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = 10.0)
            if isUser then
                row.HorizontalAlignment <- HorizontalAlignment.Right
                row.Children.Add(bubble) |> ignore
                row.Children.Add(avatar) |> ignore
            else
                row.HorizontalAlignment <- HorizontalAlignment.Left
                row.Children.Add(avatar) |> ignore
                row.Children.Add(bubble) |> ignore
            messagesPanel.Children.Add(row)

    /// 完整重绘消息面板（快照 / 新消息 / 流式增量 / 历史分页共用）。
    member private this.RenderMessages() =
        messagesPanel.Children.Clear()
        emptyPanel.IsVisible <- false
        match activeConvId with
        | None ->
            emptyHint.Text <- "选择左侧会话，或点击 ＋ 新建一个"
            emptyPanel.IsVisible <- true
        | Some convId ->
            match state.Conversations.TryFind convId with
            | None -> ()
            | Some view ->
                if view.messages.Count = 0 && view.runtimeState <> "generating" then
                    emptyHint.Text <- "发送第一条消息，开始对话"
                    emptyPanel.IsVisible <- true
                for m in view.messages do
                    // 消息结构：{ commitId, payload }（决策 79）
                    let payload =
                        match m with
                        | :? JsonNode as node when node.GetValueKind() = JsonValueKind.Object ->
                            let mutable p: JsonNode = null
                            if node.AsObject().TryGetPropertyValue("payload", &p) && not (isNull p) then p
                            else node
                        | node -> node
                    this.AppendMessage(MessageView.ofJson payload, AttachmentRef.extract payload, false)
                // P1-4：流式增量（临时展示）
                if view.runtimeState = "generating" && streamText.Length > 0 then
                    this.AppendMessage({ role = "assistant"; text = streamText.ToString(); reasoning = streamReasoning.ToString() }, [], true)
        scrollViewer.ScrollToEnd()

    /// P1-2：请求更早历史（页边界 = 稳定 commitID）。
    member private this.RequestHistory() =
        match activeConvId with
        | None -> ()
        | Some convId ->
            match state.Conversations.TryFind convId with
            | Some view when view.pageHasMore && not pageLoading ->
                pageLoading <- true
                client.SendAsync(HistoryRequest {| conversationId = convId; beforeCommitId = view.pageEarliest; limit = 100 |}) |> ignore
            | _ -> ()

    /// P1-4：选择文件并分块上传（256 KiB 块，Base64；决策 71-72）。
    member private this.PickAttachment() =
        if not (client.IsConnected) then
            setStatus "未连接" (authenticated)
        else
            async {
                try
                    let files =
                        (topLevel ()).StorageProvider.OpenFilePickerAsync(FilePickerOpenOptions(AllowMultiple = false))
                        |> Async.AwaitTask
                    let! picked = files
                    if picked.Count > 0 then
                        let file = picked[0]
                        use! stream = file.OpenReadAsync() |> Async.AwaitTask
                        use ms = new MemoryStream()
                        do! stream.CopyToAsync(ms) |> Async.AwaitTask
                        let bytes = ms.ToArray()
                        if bytes.Length = 0 then
                            Dispatcher.UIThread.Post(fun () -> setStatus "空文件无法上传" (authenticated))
                        elif int64 bytes.Length > 64L * 1024L * 1024L then
                            Dispatcher.UIThread.Post(fun () -> setStatus "附件超过 64 MiB 上限" (authenticated))
                        else
                            let hash = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
                            let ext = Path.GetExtension file.Name |> fun e -> e.ToLowerInvariant()
                            let mediaType =
                                match ext with
                                | ".png" -> "image/png"
                                | ".jpg" | ".jpeg" -> "image/jpeg"
                                | ".gif" -> "image/gif"
                                | ".webp" -> "image/webp"
                                | ".pdf" -> "application/pdf"
                                | ".txt" | ".md" -> "text/plain"
                                | ".json" -> "application/json"
                                | _ -> "application/octet-stream"
                            let aid = Guid.CreateVersion7()
                            client.SendAsync(AttachmentBegin {| attachmentId = aid; totalBytes = int64 bytes.Length; sha256 = hash; mediaType = mediaType; fileName = file.Name |}) |> ignore
                            let chunkSize = 256 * 1024
                            let mutable index = 0
                            let mutable offset = 0
                            while offset < bytes.Length do
                                let count = min chunkSize (bytes.Length - offset)
                                let chunk = ArraySegment<byte>(bytes, offset, count).ToArray()
                                client.SendAsync(AttachmentChunk {| attachmentId = aid; index = index; dataBase64 = Convert.ToBase64String chunk |}) |> ignore
                                offset <- offset + count
                                index <- index + 1
                            client.SendAsync(AttachmentComplete {| attachmentId = aid; sha256 = hash |}) |> ignore
                with e ->
                    Dispatcher.UIThread.Post(fun () -> setStatus ("选择附件失败: " + e.Message) (authenticated))
            } |> Async.Start

    /// P2-3/P1-4：下载附件（事件流 → 保存对话框）。
    member private _.DownloadAttachment(sha256: string) =
        client.SendAsync(AttachmentDownloadRequest {| sha256 = sha256 |}) |> ignore

    member private this.SaveDownload(fileName: string, bytes: byte[]) =
        async {
            try
                let picked = (topLevel ()).StorageProvider.SaveFilePickerAsync(FilePickerSaveOptions(SuggestedFileName = fileName)) |> Async.AwaitTask
                let! file = picked
                if not (isNull file) then
                    use! stream = file.OpenWriteAsync() |> Async.AwaitTask
                    do! stream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                    Dispatcher.UIThread.Post(fun () -> setStatus (sprintf "附件已保存: %s" fileName) (authenticated))
            with e ->
                Dispatcher.UIThread.Post(fun () -> setStatus ("保存附件失败: " + e.Message) (authenticated))
        } |> Async.Start

    /// P1-5：断线自动重连（指数退避 1s→30s；重连成功后重新 observe）。
    member private this.ScheduleReconnect() =
        match reconnectCts, lastToken with
        | Some _, _ -> ()
        | None, None -> ()
        | None, Some _ ->
            let cts = new CancellationTokenSource()
            reconnectCts <- Some cts
            let delay = reconnectDelayMs
            reconnectDelayMs <- min (reconnectDelayMs * 2) 30000
            setStatus (sprintf "连接断开，%.1fs 后自动重连…%s" (float delay / 1000.0) (if String.IsNullOrEmpty lastCloseInfo then "" else " [" + lastCloseInfo + "]")) false
            async {
                do! Async.Sleep delay
                if not cts.IsCancellationRequested then
                    Dispatcher.UIThread.Post(fun () -> this.Reconnect())
            } |> Async.Start

    member private this.Reconnect() =
        reconnectCts <- None
        match lastToken with
        | None -> ()
        | Some token ->
            setStatus "重连中…" false
            async {
                try
                    do! client.ConnectAsync(Uri lastUrl, CancellationToken.None) |> Async.AwaitTask
                    do! client.SendAsync(Hello {| protocol = "wanxiang"; version = Constants.ProtocolVersion; instanceId = None |}) |> Async.AwaitTask
                    do! client.SendAsync(AuthPresent {| token = token |}) |> Async.AwaitTask
                with _ ->
                    Dispatcher.UIThread.Post(fun () -> this.ScheduleReconnect())
            } |> Async.Start

    member private this.SendMessage() =
        let text = inputBox.Text
        if String.IsNullOrWhiteSpace text && pendingAttachment.IsNone then ()
        elif not client.IsConnected then
            // 断线/未认证：保留输入内容并提示，不静默丢弃（Q36 语义：断网立即报错）；指示灯保持断开色（false）
            setStatus "未连接，无法发送（请先连接服务器）" false
        else
            match activeConvId with
            | None -> ()
            | Some convId ->
                inputBox.Text <- ""
                let invocationId = Guid.CreateVersion7()
                let msg = JsonObject()
                msg["role"] <- "user"
                let contents = JsonArray()
                if not (String.IsNullOrWhiteSpace text) then
                    let textContent = JsonObject()
                    textContent["text"] <- text
                    contents.Add textContent
                // P1-4：附件引用随消息写入（决策 71-72；透明映射，NDJSON 原样保存）
                match pendingAttachment with
                | Some r ->
                    let att = JsonObject()
                    att["type"] <- "attachment"
                    att["sha256"] <- r.sha256
                    att["size"] <- r.size
                    att["mediaType"] <- r.mediaType
                    att["fileName"] <- r.fileName
                    contents.Add att
                    pendingAttachment <- None
                    setStatus "" (authenticated)
                | None -> ()
                msg["contents"] <- contents
                client.SendCommandAsync(SendUserMessage {| invocationId = invocationId; conversationId = convId; messageJson = msg |}) |> ignore
                // 服务端提交后会广播权威 MessageCommitted；避免在此处乐观展示造成重复。

    member private this.CreateConversation() =
        let conversationId = Guid.CreateVersion7()
        let title = sprintf "会话 %s" (conversationId.ToString("N").Substring(0, 8))
        // 配置由服务端用 TOML 第一个 provider 填充（客户端不硬编码 provider/model）
        let cfg =
            { provider = ""
              model = ""
              instructions = None
              tools = []
              temperature = None
              maxTokens = None
              extraJson = None }
        client.SendCommandAsync(CreateConversation {| invocationId = Guid.CreateVersion7(); conversationId = conversationId; title = title; config = cfg |}) |> ignore
        // 延迟 observe（等提交完成）
        async {
            do! Async.Sleep 300
            client.SendAsync(ObserveConversation {| conversationId = conversationId |}) |> ignore
        } |> Async.Start

    /// 会话右键菜单：重命名（D15 桌面端会话管理；PWA 已有同名能力）
    member private this.BeginRename(c: ConvSummary) =
        if not (client.IsConnected && authenticated) then
            setStatus "未连接，无法重命名" (authenticated)
        else
            let title = c.Title
            let mutable next = title
            let box = TextBox(Text = title, Margin = Thickness(16.0, 16.0, 16.0, 8.0), FontSize = 13.5)
            box.SelectAll()
            let okBtn = Button(Content = "确定", HorizontalAlignment = HorizontalAlignment.Right, Margin = Thickness(0.0, 8.0, 16.0, 16.0))
            okBtn.Click.Add(fun _ ->
                next <- box.Text.Trim()
                if not (String.IsNullOrEmpty next) && next <> title then
                    client.SendCommandAsync(RenameConversation {| invocationId = Guid.CreateVersion7(); conversationId = c.Id; title = next |}) |> ignore
                closeDialog ())
            let cancelBtn = Button(Content = "取消", HorizontalAlignment = HorizontalAlignment.Right, Margin = Thickness(0.0, 8.0, 8.0, 16.0))
            cancelBtn.Click.Add(fun _ -> closeDialog ())
            let panel = StackPanel(Spacing = 8.0)
            panel.Children.Add(TextBlock(Text = "重命名会话", FontSize = 15.0, FontWeight = FontWeight.Bold)) |> ignore
            panel.Children.Add(box) |> ignore
            panel.Children.Add(okBtn) |> ignore
            panel.Children.Add(cancelBtn) |> ignore
            showDialog panel 420.0
            // 控件尚未布局时 Focus 无效：下一帧聚焦（Q197 键盘可达）
            Dispatcher.UIThread.Post(fun () -> box.Focus() |> ignore)

    /// 会话右键菜单：删除（tombstone，决策 73 第二问）
    member private this.ConfirmDelete(c: ConvSummary) =
        if not (client.IsConnected && authenticated) then
            setStatus "未连接，无法删除" (authenticated)
        else
            let msg = TextBlock(Text = sprintf "确定要删除会话“%s”吗？此操作不可撤销。" c.Title, TextWrapping = TextWrapping.Wrap, Margin = Thickness(0.0, 4.0, 0.0, 8.0), FontSize = 13.5)
            let okBtn = Button(Content = "删除", HorizontalAlignment = HorizontalAlignment.Right, Margin = Thickness(0.0, 8.0, 16.0, 0.0))
            okBtn.Click.Add(fun _ ->
                client.SendCommandAsync(DeleteConversation {| invocationId = Guid.CreateVersion7(); conversationId = c.Id |}) |> ignore
                if activeConvId = Some c.Id then
                    activeConvId <- None
                    syncSelection ()
                closeDialog ())
            let cancelBtn = Button(Content = "取消", HorizontalAlignment = HorizontalAlignment.Right, Margin = Thickness(0.0, 8.0, 8.0, 0.0))
            cancelBtn.Click.Add(fun _ -> closeDialog ())
            let panel = StackPanel(Spacing = 8.0)
            panel.Children.Add(TextBlock(Text = "删除会话", FontSize = 15.0, FontWeight = FontWeight.Bold)) |> ignore
            panel.Children.Add(msg) |> ignore
            panel.Children.Add(okBtn) |> ignore
            panel.Children.Add(cancelBtn) |> ignore
            showDialog panel 400.0

    /// 决策 74-77：编辑最后一条消息并 fork 新会话。
    member private this.ForkConversation() =
        match activeConvId with
        | None -> setStatus "当前没有可 fork 的会话" (authenticated)
        | Some convId ->
            match state.Conversations.TryFind convId with
            | None -> ()
            | Some view ->
                if view.messages.Count = 0 then
                    setStatus "当前会话没有可 fork 的消息" (authenticated)
                else
                    let last = view.messages[view.messages.Count - 1]
                    let payload =
                        match last with
                        | :? JsonNode as node when node.GetValueKind() = JsonValueKind.Object ->
                            let mutable p: JsonNode = null
                            if node.AsObject().TryGetPropertyValue("payload", &p) && not (isNull p) then p
                            else node
                        | node -> node
                    let mutable commitId = 0UL
                    match last with
                    | :? JsonObject as o ->
                        let mutable c: JsonNode = null
                        if o.TryGetPropertyValue("commitId", &c) && not (isNull c) then
                            match c.GetValueKind() with
                            | JsonValueKind.Number ->
                                match c :? JsonValue with
                                | true ->
                                    match (c :?> JsonValue).TryGetValue<uint64>() with
                                    | true, v -> commitId <- v
                                    | _ -> ()
                                | _ -> ()
                            | _ -> ()
                        else ()
                    | _ -> ()
                    let mv = MessageView.ofJson payload
                    let editBox = TextBox(Text = mv.text, AcceptsReturn = true, MinHeight = 120.0, TextWrapping = TextWrapping.Wrap, CornerRadius = CornerRadius(10.0), Padding = Thickness(10.0, 8.0))
                    let cancelBtn = Button(Content = "取消", CornerRadius = CornerRadius(10.0), Background = Theme.secondaryContainer, Foreground = Theme.onSecondaryContainer, BorderThickness = Thickness(0.0), Padding = Thickness(14.0, 8.0), Margin = Thickness(0.0, 0.0, 8.0, 0.0))
                    let okBtn = Button(Content = "fork", CornerRadius = CornerRadius(10.0), Background = Theme.primary, Foreground = Theme.onPrimary, BorderThickness = Thickness(0.0), Padding = Thickness(14.0, 8.0))
                    let btnRow = StackPanel(Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right)
                    btnRow.Children.Add(cancelBtn)
                    btnRow.Children.Add(okBtn)
                    let panel = StackPanel(Spacing = 10.0, Width = 420.0)
                    panel.Children.Add(TextBlock(Text = "编辑消息并 fork 新会话", FontSize = 15.0, FontWeight = FontWeight.Bold))
                    panel.Children.Add(editBox)
                    panel.Children.Add(btnRow)
                    cancelBtn.Click.Add(fun _ -> closeDialog ()) |> ignore
                    okBtn.Click.Add(fun _ ->
                        let edited = editBox.Text
                        closeDialog ()
                        let newId = Guid.CreateVersion7()
                        // 决策 75：fork 点 = 父对话中最后一条被继承消息的全局提交 id。
                        // 编辑消息 id=X 时继承其之前的历史，因此取可见消息中 < X 的最大 id（编辑首条则为空）。
                        let forkAfterId =
                            let mutable prev = 0UL
                            for m in view.messages do
                                match m with
                                | :? JsonObject as o ->
                                    let mutable c: JsonNode = null
                                    if o.TryGetPropertyValue("commitId", &c) && not (isNull c) && c :? JsonValue then
                                        match (c :?> JsonValue).TryGetValue<uint64>() with
                                        | true, v when v < commitId && v > prev -> prev <- v
                                        | _ -> ()
                                | _ -> ()
                            if prev > 0UL then Some prev else None
                        let editedMsg = JsonObject()
                        editedMsg["role"] <- mv.role
                        let contents = JsonArray()
                        let textContent = JsonObject()
                        textContent["text"] <- edited
                        contents.Add textContent
                        editedMsg["contents"] <- contents
                        // fork 继承父会话配置（决策 81 第二问：服务端投影用父会话 config），客户端无需提供
                        let cfg =
                            { provider = ""
                              model = ""
                              instructions = None
                              tools = []
                              temperature = None
                              maxTokens = None
                              extraJson = None }
                        client.SendCommandAsync(ForkConversation {| invocationId = Guid.CreateVersion7(); conversationId = newId; parentConversationId = convId; forkAfterId = forkAfterId; config = cfg; editedMessageJson = editedMsg |}) |> ignore
                        async {
                            do! Async.Sleep 300
                            client.SendAsync(ObserveConversation {| conversationId = newId |}) |> ignore
                        } |> Async.Start
                        // 与“新建会话”一致：fork 完通常接着输入，提前把焦点送进输入框
                        Dispatcher.UIThread.Post(fun () -> inputBox.Focus() |> ignore))
                    showDialog panel 480.0

    member private this.OpenConversation(id: Guid) =
        client.SendAsync(ObserveConversation {| conversationId = id |}) |> ignore

/// 桌面窗口壳（决策 48：桌面入口是 Window，UI 主体在 MainView；Q195 窗口尺寸偏好是本机客户端偏好）。
type MainWindow() as this =
    inherit Window()

    do
        this.Title <- "万象"
        this.Width <- 1180.0
        this.Height <- 760.0
        this.MinWidth <- 800.0
        this.MinHeight <- 560.0
        this.Background <- Theme.bg
        this.RequestedThemeVariant <- ThemeVariant.Light

        // Q195：窗口尺寸是本机客户端偏好，保存在本地（不经 NDJSON）；启动恢复上次尺寸
        let uiPrefsPath () =
            let home =
                match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
                | s when not (String.IsNullOrWhiteSpace s) -> s
                | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")
            Path.Combine(home, "ui.json")
        let loadPrefs () =
            try
                let path = uiPrefsPath ()
                if File.Exists path then
                    let node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText path)
                    if not (isNull node) then
                        let o = node.AsObject()
                        let getD k =
                            let mutable n: System.Text.Json.Nodes.JsonNode = null
                            if o.TryGetPropertyValue(k, &n) && n <> null then n.GetValue<double>() else nan
                        let w, h = getD "width", getD "height"
                        if not (Double.IsNaN w) && w >= 800.0 && not (Double.IsNaN h) && h >= 560.0 then
                            this.Width <- w
                            this.Height <- h
                        let getI k =
                            let mutable n: System.Text.Json.Nodes.JsonNode = null
                            if o.TryGetPropertyValue(k, &n) && n <> null then n.GetValue<int>() else 0
                        let getB k =
                            let mutable n: System.Text.Json.Nodes.JsonNode = null
                            o.TryGetPropertyValue(k, &n) && n <> null && n.GetValueKind() = System.Text.Json.JsonValueKind.True
                        let x, y = getI "x", getI "y"
                        // 用显式 hasPosition 标志区分“未保存”与合法的 (0,0) 停靠位置（Q195）
                        if getB "hasPosition" then this.Position <- PixelPoint(x, y)
            with _ -> ()
        loadPrefs ()
        this.Closed.Add(fun _ ->
            try
                let path = uiPrefsPath ()
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                let o = System.Text.Json.Nodes.JsonObject()
                o["width"] <- this.Width
                o["height"] <- this.Height
                o["x"] <- this.Position.X
                o["y"] <- this.Position.Y
                o["hasPosition"] <- true
                File.WriteAllText(path, o.ToJsonString())
                // Q118：本机偏好文件最小用户权限（与 TOML/日志/附件一致）
                try File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
            with _ -> ())

        this.Content <- MainView()
