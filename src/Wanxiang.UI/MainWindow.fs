namespace Wanxiang.UI

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Controls.Primitives
open Avalonia.Controls.Templates
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Controls.Shapes
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Media.Imaging
open Avalonia.Platform
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
open Markdig.Extensions.Tables

type ConvSummary = {
    Id: Guid
    Title: string
    Preview: string
    Running: bool
}

/// Markdown 行内片段：在 AppendMessage 中转为 Run / 可点击链接。
type RichSpan =
    | TextSpan of text: string * bold: bool * italic: bool * code: bool
    | LinkSpan of text: string * url: string
    | BreakSpan

type TextSegment =
    | NormalText of RichSpan list
    | CodeBlock of lang: string * code: string

module MarkdownParser =
    let pipeline = MarkdownPipelineBuilder().UseAdvancedExtensions().Build()

    let rec private plainInline (i: Inline) : string =
        match i with
        | null -> ""
        | :? LiteralInline as lit -> lit.Content.ToString()
        | :? CodeInline as c -> c.Content
        | :? LineBreakInline -> "\n"
        | :? ContainerInline as c -> String.Concat(seq { for ch in c do yield plainInline ch })
        | _ -> ""

    let private trimTrailingBreaks (acc: System.Collections.Generic.List<RichSpan>) =
        let mutable finished = false
        while (not finished) && acc.Count > 0 do
            match acc[acc.Count - 1] with
            | BreakSpan -> acc.RemoveAt(acc.Count - 1)
            | _ -> finished <- true

    let rec private collectInlines (i: Inline) (bold: bool) (italic: bool) (acc: System.Collections.Generic.List<RichSpan>) =
        match i with
        | null -> ()
        | :? LiteralInline as lit ->
            let t = lit.Content.ToString()
            if t.Length > 0 then acc.Add(TextSpan(t, bold, italic, false))
        | :? CodeInline as c ->
            let t = c.Content
            if not (String.IsNullOrEmpty t) then acc.Add(TextSpan(t, false, false, true))
        | :? LineBreakInline ->
            acc.Add(BreakSpan)
        | :? LinkInline as link ->
            let label = plainInline link
            let url = if isNull link.Url then "" else link.Url
            if link.IsImage then
                let alt = if String.IsNullOrEmpty label then "image" else label
                acc.Add(TextSpan(sprintf "[%s]" alt, false, false, false))
            elif String.IsNullOrEmpty url then
                if not (String.IsNullOrEmpty label) then
                    acc.Add(TextSpan(label, bold, italic, false))
            else
                let text = if String.IsNullOrEmpty label then url else label
                acc.Add(LinkSpan(text, url))
        | :? EmphasisInline as em ->
            let nextBold = bold || em.DelimiterCount >= 2
            let nextItalic = italic || em.DelimiterCount = 1
            for ch in em do collectInlines ch nextBold nextItalic acc
        | :? ContainerInline as c ->
            for ch in c do collectInlines ch bold italic acc
        | _ -> ()

    let private appendLeaf (leaf: LeafBlock) (prefix: string) (acc: System.Collections.Generic.List<RichSpan>) =
        if not (String.IsNullOrEmpty prefix) then
            acc.Add(TextSpan(prefix, false, false, false))
        if not (isNull leaf.Inline) then
            collectInlines leaf.Inline false false acc

    let rec private appendBlock (b: Block) (linePrefix: string) (acc: System.Collections.Generic.List<RichSpan>) =
        match b with
        | null -> ()
        | :? FencedCodeBlock as f ->
            if not (String.IsNullOrEmpty linePrefix) then
                acc.Add(TextSpan(linePrefix, false, false, false))
            let codeLines = [ for i = 0 to f.Lines.Count - 1 do yield f.Lines.Lines[i].ToString().TrimEnd('\r', '\n') ]
            let code = String.Join("\n", codeLines)
            if not (String.IsNullOrEmpty code) then
                acc.Add(TextSpan(code, false, false, true))
            acc.Add(BreakSpan)
        | :? HeadingBlock as h ->
            appendLeaf h linePrefix acc
            acc.Add(BreakSpan)
        | :? ParagraphBlock as p ->
            appendLeaf p linePrefix acc
            acc.Add(BreakSpan)
        | :? QuoteBlock as q ->
            for ch in q do appendBlock ch "> " acc
        | :? ListBlock as list ->
            let mutable n =
                match Int32.TryParse(list.OrderedStart) with
                | true, v -> v
                | _ -> 1
            for item in list do
                match item with
                | :? ListItemBlock as li ->
                    let bullet =
                        if list.IsOrdered then
                            let s = sprintf "%d. " n
                            n <- n + 1
                            s
                        else "- "
                    let mutable first = true
                    for ch in li do
                        let pfx = if first then bullet else "  "
                        first <- false
                        appendBlock ch pfx acc
                | other -> appendBlock other linePrefix acc
        | :? Table as table ->
            for rowObj in table do
                match rowObj with
                | :? TableRow as row ->
                    acc.Add(TextSpan("| ", false, false, false))
                    let mutable firstCell = true
                    for cellObj in row do
                        if not firstCell then acc.Add(TextSpan("| ", false, false, false))
                        firstCell <- false
                        match cellObj with
                        | :? TableCell as cell ->
                            for ch in cell do
                                appendBlock ch "" acc
                                trimTrailingBreaks acc
                        | _ -> ()
                    acc.Add(TextSpan(" |", false, false, false))
                    acc.Add(BreakSpan)
                | _ -> ()
        | :? LeafBlock as leaf ->
            appendLeaf leaf linePrefix acc
            acc.Add(BreakSpan)
        | :? ContainerBlock as c ->
            for ch in c do appendBlock ch linePrefix acc
        | _ -> ()

    let private spansOfRaw (raw: string) : RichSpan list =
        if String.IsNullOrEmpty raw then []
        else [ TextSpan(raw, false, false, false) ]

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
                        let codeLines = [ for i = 0 to f.Lines.Count - 1 do yield f.Lines.Lines[i].ToString().TrimEnd('\r', '\n') ]
                        let code = String.Join("\n", codeLines)
                        results.Add(CodeBlock(lang, code))
                    | block ->
                        let spans = System.Collections.Generic.List<RichSpan>()
                        appendBlock block "" spans
                        trimTrailingBreaks spans
                        if spans.Count > 0 then
                            results.Add(NormalText(List.ofSeq spans))
                if results.Count > 0 then List.ofSeq results
                else [ NormalText(spansOfRaw raw) ]
            with _ ->
                [ NormalText(spansOfRaw raw) ]

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
    let statusText = TextBlock(Text = "未连接", Foreground = Theme.muted, FontSize = 11.0, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = Thickness(Theme.space2, 0.0, 0.0, 0.0))
    let connDot = Ellipse(Width = 7.0, Height = 7.0, Fill = Theme.faint, VerticalAlignment = VerticalAlignment.Center)
    let connectButton = Button(Content = "连接", Height = 28.0, Padding = Thickness(Theme.space3, 0.0), FontSize = 12.0, CornerRadius = CornerRadius(Theme.radiusSm), Background = Theme.panel, Foreground = Theme.text, BorderBrush = Theme.outlineVariant, BorderThickness = Thickness(1.0), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center)
    do connectButton.Classes.Add("quiet")
    // 状态需先定义 authenticated 才能被下面的闭包捕获
    let mutable authenticated = false
    let updateConnectButton () =
        if client.IsConnected && authenticated then
            connectButton.Content <- "断开"
            ToolTip.SetTip(connectButton, "断开当前连接")
        else
            connectButton.Content <- "连接"
            ToolTip.SetTip(connectButton, "连接到万象服务器")

    let setStatus (text: string) (connected: bool) =
        statusText.Text <- text
        connDot.Fill <- if connected then Theme.primary else Theme.faint
        updateConnectButton ()
        if not connected then
            connectButton.IsEnabled <- true
    let newButton = Icons.createButton Icons.Outline (Icons.plus Theme.text)
    do ToolTip.SetTip(newButton, "新建会话")
    let searchButton = Icons.createButton Icons.Outline (Icons.search Theme.text)
    do ToolTip.SetTip(searchButton, "搜索会话")
    let searchBox =
        TextBox(
            PlaceholderText = "搜索会话…", CornerRadius = CornerRadius(Theme.radiusSm),
            Margin = Thickness(Theme.sidebarInset, Theme.space2, Theme.sidebarInset, Theme.space1),
            Padding = Thickness(Theme.space2, 5.0), BorderThickness = Thickness(0.0), Background = Brushes.Transparent,
            FontSize = 12.5, MinHeight = 32.0, IsVisible = false)
    do searchBox.Classes.Add("wx-input")
    do searchBox.Classes.Add("wx-input-field")
    let filteredCountLabel = TextBlock(Text = "", FontSize = 10.5, Foreground = Theme.muted, Margin = Thickness(Theme.sidebarInset, 0.0, Theme.sidebarInset, Theme.space1), IsVisible = false)
    let convList = ListBox(Background = Brushes.Transparent, BorderThickness = Thickness(0.0))
    // P1-1：显式 VirtualizingStackPanel；侧栏 Dock 给 ListBox 有界高度，由其自身滚动虚拟化（勿外包无限高 ScrollViewer）
    do convList.ItemsPanel <- FuncTemplate<Panel>(fun () -> VirtualizingStackPanel() :> Panel)
    let chatTitle = TextBlock(Text = "选择一个会话", FontSize = 14.5, FontWeight = FontWeight.SemiBold, Foreground = Theme.faint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis)
    let genStatus = TextBlock(Text = "", FontSize = 11.5, Foreground = Theme.primary)
    let genDot = Ellipse(Width = 6.0, Height = 6.0, Fill = Theme.primary)
    let genChip = Border(Background = Theme.tagBg, CornerRadius = CornerRadius(Theme.radiusSm), Padding = Thickness(10.0, 3.0, 12.0, 3.0), IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = Thickness(Theme.space2, 0.0, 0.0, 0.0), BorderThickness = Thickness(0.0))
    do
        // 后设置 Child：避免在 ctor 表达式中传 Children 数组（Avalonia StackPanel 不可）
        let genInner = StackPanel(Orientation = Orientation.Horizontal, Spacing = 6.0)
        genInner.Children.Add(genDot) |> ignore
        genInner.Children.Add(genStatus) |> ignore
        genChip.Child <- genInner
    // fork / cancel / settings 统一用图标，避免 header 文字按钮堆砌
    let forkButton = Icons.createButton Icons.Outline (Icons.forkIcon Theme.text)
    do forkButton.IsVisible <- false
    do ToolTip.SetTip(forkButton, "编辑并 fork")
    let cancelButton = Icons.createButton Icons.Outline (Icons.xIcon Theme.text)
    do cancelButton.IsVisible <- false
    do cancelButton.IsEnabled <- false
    do ToolTip.SetTip(cancelButton, "取消生成")
    let settingsButton = Icons.createButton Icons.Outline (Icons.gear Theme.text)
    do settingsButton.IsVisible <- false
    do ToolTip.SetTip(settingsButton, "会话设置")
    let messagesPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Theme.space3)
    let emptyHint = TextBlock(Text = "", Foreground = Theme.muted, FontSize = 13.0, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 320.0, LineHeight = 20.0)
    let emptyCta =
        Border(
            Height = 32.0, Padding = Thickness(16.0, 0.0), CornerRadius = CornerRadius(Theme.radiusSm),
            Background = Theme.panel, BorderBrush = Theme.outlineVariant, BorderThickness = Thickness(1.0),
            HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false, Margin = Thickness(0.0, Theme.space3, 0.0, 0.0),
            Cursor = Cursor(StandardCursorType.Hand),
            Child = TextBlock(Text = "新建会话", FontSize = 12.5, FontWeight = FontWeight.Medium, Foreground = Theme.text,
                              HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center))
    let listEmptyHint = TextBlock(Text = "没有匹配的会话", Foreground = Theme.muted, FontSize = 12.0, HorizontalAlignment = HorizontalAlignment.Center, Margin = Thickness(Theme.sidebarInset, Theme.space5, Theme.sidebarInset, 0.0), IsVisible = false, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center)
    let messagesHost = Grid()
    let scrollViewer = ScrollViewer(Content = messagesHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
    let inputBox = TextBox(PlaceholderText = "输入消息…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = Thickness(0.0), Background = Brushes.Transparent, FontSize = 14.0, VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 34.0, MaxHeight = 120.0, Padding = Thickness(Theme.space1, 0.0, 0.0, 0.0))
    do inputBox.Classes.Add("wx-input")
    do inputBox.Classes.Add("wx-input-shell")
    let sendButton = Icons.createButton Icons.Filled (Icons.sendUp Theme.onPrimary)
    let attachButton = Icons.createButton Icons.Outline (Icons.paperclip Theme.text)
    do Icons.setEnabled sendButton false
    do Icons.setEnabled attachButton false
    do ToolTip.SetTip(attachButton, "添加附件")
    do ToolTip.SetTip(sendButton, "发送（Enter）")
    do ToolTip.SetTip(inputBox, "Enter 发送，Shift+Enter 换行")

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
    /// 本机 UI 偏好（ui.json）
    let mutable uiPrefs = UiPrefs.load()
    /// 各会话最近一次完成的 token/耗时（generation.finished 透传）
    let mutable lastUsageByConv: Map<Guid, GenerationUsage> = Map.empty
    /// 当前生成开始时刻（推理/耗时展示）
    let mutable generationStartedAt: DateTimeOffset option = None
    /// 流式推理计时器（刷新思考时长标签）
    let mutable reasoningTickTimer: DispatcherTimer option = None

    let messageFontSize = uiPrefs.fontScale
    let smallFontSize = max 10.5 (uiPrefs.fontScale - 2.5)

    let formatReasoningDuration (ms: int64) =
        if ms >= 1000L then sprintf "%.1fs" (float ms / 1000.0)
        else sprintf "%dms" (int ms)

    let stopReasoningTimer () =
        match reasoningTickTimer with
        | Some t ->
            t.Stop()
            reasoningTickTimer <- None
        | None -> ()

    let startReasoningTimer () =
        stopReasoningTimer ()
        let t = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 400.0)
        t.Tick.Add(fun _ ->
            if activeConvId.IsSome && streamReasoning.Length > 0 then this.RenderMessages())
        t.Start()
        reasoningTickTimer <- Some t

    /// TopLevel 成员（Clipboard/StorageProvider）需经 TopLevel.GetTopLevel 获取（UserControl 非 TopLevel）。
    let topLevel () = TopLevel.GetTopLevel(this)

    // ---- 应用内对话框（overlay 遮罩；桌面与 PWA 共用，避免平台窗口差异）----
    let dialogOverlay = Grid(IsVisible = false, Background = Theme.overlayScrim)
    let dialogCard =
        Border(
            Background = Theme.panel, CornerRadius = CornerRadius(Theme.radiusLg), Padding = Thickness(Theme.space5),
            BorderBrush = Theme.border, BorderThickness = Thickness(1.0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center)
    let showDialog (content: Control) (width: float) =
        dialogCard.Child <- content
        dialogCard.Width <- width
        dialogOverlay.IsVisible <- true
    let closeDialog () =
        dialogOverlay.IsVisible <- false
        dialogCard.Child <- null
    do
        dialogCard.BoxShadow <- BoxShadows(BoxShadow(OffsetX = 0.0, OffsetY = 10.0, Blur = 32.0, Spread = -6.0, Color = Theme.shadowSoft))
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
        // PWA/browser-wasm 无宿主文件系统：优先 avares 内嵌 logo（与 splash / manifest 同源）
        try
            use stream = AssetLoader.Open(Uri("avares://Wanxiang.UI/Assets/logo.png"))
            Some(new Bitmap(stream))
        with _ ->
            try
                let candidates = [
                    Path.Combine(AppContext.BaseDirectory, "logo.png")
                    Path.Combine(AppContext.BaseDirectory, "pwa", "logo.png")
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

    let createBrandLogo (size: float) =
        let radius = size * Theme.logoRadiusRatio
        let tile =
            Border(
                Width = size,
                Height = size,
                CornerRadius = CornerRadius(radius),
                Background = Brushes.Transparent,
                ClipToBounds = true,
                BoxShadow = BoxShadows(BoxShadow(OffsetX = 0.0, OffsetY = 8.0, Blur = 24.0, Spread = -8.0, Color = Theme.logoShadowFar)))
        match logoBitmap with
        | Some bmp ->
            let img = Image(Source = bmp, Stretch = Stretch.UniformToFill)
            tile.Child <- img
        | None ->
            let fallback =
                Border(
                    Background = Theme.panel,
                    CornerRadius = CornerRadius(radius),
                    Child =
                        TextBlock(
                            Text = "万",
                            Foreground = Theme.text,
                            FontSize = size * 0.38,
                            FontWeight = FontWeight.SemiBold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center))
            tile.Child <- fallback
        tile

    // 空状态：品牌标 + 标题 + 提示；靠上对齐，避免 tall 聊天区垂直居中「养鱼」
    let emptyPanel = StackPanel(Spacing = Theme.space3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = Thickness(Theme.chatInset, Theme.shellGap * 4.0, Theme.chatInset, 0.0))
    let emptyOverlay = Grid(HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsVisible = false, Background = Brushes.Transparent)
    do emptyOverlay.Children.Add(emptyPanel)
    do
        let emptyLogo = createBrandLogo Theme.logoSizeEmpty
        emptyLogo.HorizontalAlignment <- HorizontalAlignment.Center
        let emptyTitle = TextBlock(Text = "万象", FontSize = 17.0, FontWeight = FontWeight.Medium, Foreground = Theme.text, HorizontalAlignment = HorizontalAlignment.Center, LetterSpacing = 0.5)
        emptyPanel.Children.Add(emptyLogo) |> ignore
        emptyPanel.Children.Add(emptyTitle) |> ignore
        emptyPanel.Children.Add(emptyHint) |> ignore
        emptyPanel.Children.Add(emptyCta) |> ignore

    /// 过滤后会话列表为空时，在侧栏展示「没有匹配的会话」（与聊天区 emptyPanel 分工）。
    let refreshListEmpty () =
        let q = if String.IsNullOrWhiteSpace searchBox.Text then "" else searchBox.Text.Trim()
        let count =
            match convList.ItemsSource with
            | :? (ConvSummary array) as arr -> arr.Length
            | _ -> convList.ItemCount
        listEmptyHint.IsVisible <- not (String.IsNullOrEmpty q) && count = 0
        // P1-1：搜索非空时显示「已筛选 N 条」（稳妥替代 ItemTemplate Run 高亮）
        if String.IsNullOrEmpty q then
            filteredCountLabel.IsVisible <- false
            filteredCountLabel.Text <- ""
        else
            filteredCountLabel.Text <- sprintf "已筛选 %d 条" count
            filteredCountLabel.IsVisible <- true

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

    let setSendEnabled (enabled: bool) =
        Icons.setEnabled sendButton enabled

    let setInputsEnabled (enabled: bool) =
        inputBox.IsEnabled <- enabled
        setSendEnabled enabled
        Icons.setEnabled attachButton enabled

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

    let showGenChip (visible: bool) =
        genChip.IsVisible <- visible
        cancelButton.IsVisible <- visible
        cancelButton.IsEnabled <- visible

    let setConversationChrome (hasConversation: bool) =
        forkButton.IsVisible <- hasConversation
        settingsButton.IsVisible <- hasConversation

    let formatSize (n: int64) =
        if n < 1024L then sprintf "%d B" n
        elif n < 1024L * 1024L then sprintf "%.1f KiB" (float n / 1024.0)
        else sprintf "%.1f MiB" (float n / (1024.0 * 1024.0))

    do
        this.Background <- Theme.bg

        // 品牌标（左上角）：小标 + 名称，右端新建 + 搜索
        // 搜索走“悬停即向左展开”：header 高度固定，搜索框 overlay 在动作区左侧，不推挤品牌/新建
        let brandTile = createBrandLogo Theme.logoSizeSidebar
        brandTile.VerticalAlignment <- VerticalAlignment.Center
        let appName = TextBlock(Text = "万象", FontSize = 14.0, FontWeight = FontWeight.Medium, Foreground = Theme.text, VerticalAlignment = VerticalAlignment.Center, LetterSpacing = 0.6, TextTrimming = TextTrimming.CharacterEllipsis)
        let sidebarHeader = Border(Height = Theme.barHeight, Padding = Thickness(Theme.sidebarInset, 0.0, Theme.sidebarInset, 0.0), BorderBrush = Theme.borderSubtle, BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0))
        // header 用 Grid：左列品牌，右列动作 + 悬浮搜索
        let headerGrid = Grid(VerticalAlignment = VerticalAlignment.Center)
        headerGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        headerGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        headerGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        let brandRow = StackPanel(Orientation = Orientation.Horizontal, Spacing = Theme.space2, VerticalAlignment = VerticalAlignment.Center)
        brandRow.Children.Add(brandTile) |> ignore
        brandRow.Children.Add(appName) |> ignore
        Grid.SetColumn(brandRow, 0)
        headerGrid.Children.Add(brandRow) |> ignore
        // 中间占位，确保右列贴边
        let headerSpacer2 = Border()
        Grid.SetColumn(headerSpacer2, 1)
        headerGrid.Children.Add(headerSpacer2) |> ignore
        // 右列：新建常驻，搜索为 overlay（不参与 Dock 排版）
        // 搜索胶囊（hover/focus 时可见，平时仅图标）
        let searchCapsule = Border(CornerRadius = CornerRadius(Theme.radiusPill), Background = Theme.panel, BorderBrush = Theme.outlineVariant, BorderThickness = Thickness(1.0), Padding = Thickness(8.0, 2.0, 4.0, 2.0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false)
        let searchCapsuleInner = StackPanel(Orientation = Orientation.Horizontal, Spacing = 4.0, VerticalAlignment = VerticalAlignment.Center)
        let searchCapsuleBox = TextBox(Background = Brushes.Transparent, BorderThickness = Thickness(0.0), FontSize = 12.0, MinWidth = 110.0, Width = 140.0, VerticalAlignment = VerticalAlignment.Center, Watermark = "搜索")
        searchCapsuleInner.Children.Add(searchCapsuleBox) |> ignore
        let searchCloseBtn = Border(Width = 22.0, Height = 22.0, CornerRadius = CornerRadius(11.0), Background = Brushes.Transparent, Cursor = Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center)
        searchCloseBtn.Child <- Viewbox(Width = 10.0, Height = 10.0, Child = Canvas(Width = 10.0, Height = 10.0))
        // 用简单 X 文本避免额外 Canvas 复杂度
        searchCloseBtn.Child <- TextBlock(Text = "×", FontSize = 14.0, Foreground = Theme.muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center)
        searchCapsuleInner.Children.Add(searchCloseBtn) |> ignore
        searchCapsule.Child <- searchCapsuleInner
        let headerActions =
            StackPanel(Orientation = Orientation.Horizontal, Spacing = Theme.space1, VerticalAlignment = VerticalAlignment.Center)
        headerActions.Children.Add(searchCapsule) |> ignore
        headerActions.Children.Add(Icons.slot searchButton) |> ignore
        headerActions.Children.Add(Icons.slot newButton) |> ignore
        Grid.SetColumn(headerActions, 2)
        headerGrid.Children.Add(headerActions) |> ignore
        sidebarHeader.Child <- headerGrid

        // 会话列表模板：两行（标题 + 预览）+ 右键菜单（重命名/删除，D15 桌面端会话管理）
        convList.ItemTemplate <-
            FuncDataTemplate(
                typeof<ConvSummary>,
                fun (item: obj) (_: INameScope) ->
                    let c = item :?> ConvSummary
                    let title = TextBlock(Text = c.Title, FontSize = 13.0, FontWeight = FontWeight.SemiBold, Foreground = Theme.text, TextTrimming = TextTrimming.CharacterEllipsis)
                    let preview = TextBlock(Text = c.Preview, FontSize = 11.5, Foreground = Theme.muted, TextTrimming = TextTrimming.CharacterEllipsis)
                    let panel = StackPanel(Spacing = 2.0)
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
        itemBase.Setters.Add(Setter(ListBoxItem.PaddingProperty, Thickness(Theme.space2, 7.0)))
        itemBase.Setters.Add(Setter(ListBoxItem.MarginProperty, Thickness(Theme.sidebarInset, 1.0)))
        itemBase.Setters.Add(Setter(ListBoxItem.CornerRadiusProperty, CornerRadius(Theme.radiusSm)))
        itemBase.Setters.Add(Setter(ListBoxItem.MinHeightProperty, 40.0))
        itemBase.Setters.Add(Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent))
        convList.Styles.Add(itemBase)
        // 选中：背景染色 + 左侧 3px 品牌色描边（只画左边、其它边为 None，避免抖动）
        let itemAccent = Style(fun x -> x.OfType<ListBoxItem>().Class(":selected"))
        itemAccent.Setters.Add(Setter(ListBoxItem.BorderThicknessProperty, Thickness(0.0)))
        itemAccent.Setters.Add(Setter(ListBoxItem.BackgroundProperty, Theme.primaryContainer))
        convList.Styles.Add(itemAccent)

        // 幽灵按钮（头部操作）悬停反馈：固定背景覆盖了 Fluent 默认 pointerover，手动补回
        let ghostHover = Style(fun x -> x.OfType<Button>().Class("ghost").Class(":pointerover"))
        ghostHover.Setters.Add(Setter(Button.BackgroundProperty, Theme.hover))
        ghostHover.Setters.Add(Setter(Button.ForegroundProperty, Theme.text))
        this.Styles.Add(ghostHover)
        let quietBase = Style(fun x -> x.OfType<Button>().Class("quiet"))
        quietBase.Setters.Add(Setter(Button.BackgroundProperty, Theme.panel))
        quietBase.Setters.Add(Setter(Button.ForegroundProperty, Theme.text))
        quietBase.Setters.Add(Setter(Button.BorderBrushProperty, Theme.outlineVariant))
        quietBase.Setters.Add(Setter(Button.BorderThicknessProperty, Thickness(1.0)))
        this.Styles.Add(quietBase)
        let quietHover = Style(fun x -> x.OfType<Button>().Class("quiet").Class(":pointerover"))
        quietHover.Setters.Add(Setter(Button.BackgroundProperty, Theme.hover))
        quietHover.Setters.Add(Setter(Button.BorderBrushProperty, Theme.muted))
        this.Styles.Add(quietHover)

        // 侧栏（无硬分割线，靠底色与主画布区分）
        let sidebar = DockPanel(Background = Theme.sidebar)
        let sidebarBorder = Border(Child = sidebar, BorderBrush = Theme.borderSubtle, BorderThickness = Thickness(0.0, 0.0, 1.0, 0.0))
        // LetterSpacing 单位是像素；原先 60 会把「会话」拉开到几乎不可见
        let listLabel = TextBlock(Text = "会话", FontSize = 10.5, FontWeight = FontWeight.Medium, Foreground = Theme.faint, Margin = Thickness(Theme.sidebarInset, Theme.space2, Theme.sidebarInset, Theme.space1))
        // P1-1：搜索/标题/计数固定在顶，ListBox 占剩余有界高度以启用虚拟化；空提示叠在列表上
        let listChrome = DockPanel()
        DockPanel.SetDock(searchBox, Dock.Top)
        DockPanel.SetDock(listLabel, Dock.Top)
        DockPanel.SetDock(filteredCountLabel, Dock.Top)
        let listBody = Grid()
        listBody.Children.Add(convList) |> ignore
        listBody.Children.Add(listEmptyHint) |> ignore
        listChrome.Children.Add(searchBox) |> ignore
        listChrome.Children.Add(listLabel) |> ignore
        listChrome.Children.Add(filteredCountLabel) |> ignore
        listChrome.Children.Add(listBody) |> ignore
        let footer = Border(Height = Theme.barHeight, BorderBrush = Theme.borderSubtle, BorderThickness = Thickness(0.0, 1.0, 0.0, 0.0), Padding = Thickness(Theme.sidebarInset, 0.0, Theme.sidebarInset, 0.0))
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
        sidebar.Children.Add(listChrome)

        // 聊天头部：标题 + 生成点，右侧图标组（不出现文字按钮）
        let headerPanel = DockPanel()
        let headerFiller = Border()
        let headerIcons = StackPanel(Orientation = Orientation.Horizontal, Spacing = Theme.space1, VerticalAlignment = VerticalAlignment.Center)
        headerIcons.Children.Add(Icons.slot cancelButton) |> ignore
        headerIcons.Children.Add(Icons.slot forkButton) |> ignore
        headerIcons.Children.Add(Icons.slot settingsButton) |> ignore
        DockPanel.SetDock(genChip, Dock.Left)
        DockPanel.SetDock(headerIcons, Dock.Right)
        headerPanel.Children.Add(chatTitle)
        headerPanel.Children.Add(Border(Width = Theme.space2))
        headerPanel.Children.Add(genChip)
        headerPanel.Children.Add(headerIcons)
        headerPanel.Children.Add(headerFiller)
        let chatHeaderInner =
            Border(HorizontalAlignment = HorizontalAlignment.Stretch, Child = headerPanel)
        let chatHeader =
            Border(
                Height = Theme.barHeight,
                Background = Theme.bg,
                BorderBrush = Theme.borderSubtle,
                BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0),
                Padding = Thickness(Theme.chatInset, 0.0),
                Child = chatHeaderInner)

        // 输入区：撑满聊天栏宽度（阅读列宽度仅约束消息气泡）
        let inputBar = DockPanel()
        let inputActions =
            StackPanel(
                Orientation = Orientation.Horizontal,
                Spacing = Theme.space2,
                VerticalAlignment = VerticalAlignment.Center,
                MaxHeight = Theme.iconBtn)
        inputActions.Children.Add(Icons.slot attachButton) |> ignore
        inputActions.Children.Add(Icons.slot sendButton) |> ignore
        let inputActionsHost = Border(Background = Brushes.Transparent, BorderThickness = Thickness(0.0), VerticalAlignment = VerticalAlignment.Center, Child = inputActions)
        DockPanel.SetDock(inputActionsHost, Dock.Right)
        inputBar.Children.Add(inputActionsHost)
        inputBar.Children.Add(inputBox)
        let inputShell =
            Border(
                Background = Theme.panel, BorderBrush = Theme.borderSubtle, BorderThickness = Thickness(1.0),
                CornerRadius = CornerRadius(Theme.radiusLg), Padding = Thickness(Theme.space2, Theme.space1, Theme.space1, Theme.space1),
                HorizontalAlignment = HorizontalAlignment.Stretch, Child = inputBar,
                BoxShadow = BoxShadows(BoxShadow(OffsetX = 0.0, OffsetY = 6.0, Blur = 18.0, Spread = -10.0, Color = Theme.shadowSoft)))
        let inputColumn = StackPanel(Orientation = Orientation.Vertical, Spacing = Theme.space1, HorizontalAlignment = HorizontalAlignment.Stretch)
        inputColumn.Children.Add(inputShell) |> ignore
        let inputWrap =
            Border(
                Background = Brushes.Transparent, Padding = Thickness(Theme.chatInset, Theme.shellGap, Theme.chatInset, Theme.shellGap),
                HorizontalAlignment = HorizontalAlignment.Stretch, Child = inputColumn)

        // 聊天区（消息居中阅读列；空状态 overlay 填满中间可视区）
        messagesPanel.MaxWidth <- Theme.readingWidth
        messagesPanel.HorizontalAlignment <- HorizontalAlignment.Center
        let chat = DockPanel()
        messagesHost.Children.Add(messagesPanel)
        scrollViewer.Padding <- Thickness(Theme.chatInset, Theme.shellGap, Theme.chatInset, Theme.shellGap)
        let chatBody = Grid()
        chatBody.Children.Add(scrollViewer)
        chatBody.Children.Add(emptyOverlay)
        DockPanel.SetDock(chatHeader, Dock.Top)
        DockPanel.SetDock(inputWrap, Dock.Bottom)
        chat.Children.Add(chatHeader)
        chat.Children.Add(inputWrap)
        chat.Children.Add(chatBody)

        // 左右分栏
        let split = Grid()
        split.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(Theme.sidebarWidth)))
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

        let applySearchFilterFrom (text: string) =
            let q = if String.IsNullOrWhiteSpace text then "" else text.Trim().ToLowerInvariant()
            if String.IsNullOrEmpty q then
                convList.ItemsSource <- rawSummaries
            else
                convList.ItemsSource <-
                    rawSummaries
                    |> Array.filter (fun c -> c.Title.ToLowerInvariant().Contains q || c.Preview.ToLowerInvariant().Contains q)
            syncSelection ()
            refreshListEmpty ()
        let applySearchFilter () =
            let t =
                if searchCapsule.IsVisible then searchCapsuleBox.Text
                elif searchBox.IsVisible then searchBox.Text
                else ""
            applySearchFilterFrom t
        // 旧 searchBox 保留为兼容（过滤逻辑统一走 applySearchFilterFrom）
        let closeSearch () =
            searchBox.IsVisible <- false
            searchBox.Text <- ""
            searchCapsule.IsVisible <- false
            searchCapsuleBox.Text <- ""
            Icons.setOutlineActive searchButton false
            applySearchFilterFrom ""
        let openSearch () =
            // 新交互：悬停/点击均展开胶囊，原地向左展开，不推品牌区
            searchCapsule.IsVisible <- true
            Icons.setOutlineActive searchButton true
            Dispatcher.UIThread.Post(fun () ->
                searchCapsuleBox.Focus() |> ignore
                searchCapsuleBox.CaretIndex <- if isNull searchCapsuleBox.Text then 0 else searchCapsuleBox.Text.Length)
        // 悬停即展开（向左，不腾地方）：鼠标进入动作区就展开
        let headerActionsGridHost = headerActions // StackPanel 已在作用域
        // 悬停展开：进入动作区展开，离开且未聚焦则收起
        let isSearchFocused () =
            searchCapsuleBox.IsFocused || searchCapsule.IsVisible && not (String.IsNullOrWhiteSpace searchCapsuleBox.Text)
        headerActions.PointerEntered.Add(fun _ ->
            if not searchCapsule.IsVisible then openSearch ())
        headerActions.PointerExited.Add(fun _ ->
            // 延迟判断，避免从按钮移到输入框的瞬间误收
            async {
                do! Async.Sleep 180
                Dispatcher.UIThread.Post(fun () ->
                    if not searchCapsuleBox.IsFocused && String.IsNullOrWhiteSpace searchCapsuleBox.Text then
                        // 保持胶囊可见但不强制关闭，点 × 或 Esc 才清；此处不自动收，避免闪
                        ())
            } |> Async.Start)
        searchButton.PointerPressed.Add(fun _ ->
            if searchCapsule.IsVisible && String.IsNullOrWhiteSpace searchCapsuleBox.Text then closeSearch () else openSearch ())
        searchCloseBtn.PointerPressed.Add(fun _ -> closeSearch ())
        searchCapsuleBox.KeyDown.Add(fun e ->
            if e.Key = Key.Escape then
                closeSearch ()
                e.Handled <- true)
        searchCapsuleBox.TextChanged.Add(fun _ -> applySearchFilterFrom (if isNull searchCapsuleBox.Text then "" else searchCapsuleBox.Text))
        // 兼容旧 searchBox 事件（保留但不再主用）
        searchBox.KeyDown.Add(fun e ->
            if e.Key = Key.Escape then
                closeSearch ()
                e.Handled <- true)
        searchBox.TextChanged.Add(fun _ ->
            applySearchFilterFrom (if isNull searchBox.Text then "" else searchBox.Text))
        let beginCreate () =
            this.CreateConversation()
            // 点击后把焦点交给输入框，避免按钮保留焦点时按 Space/Enter 误触发重复新建
            inputBox.Focus() |> ignore
        newButton.PointerPressed.Add(fun _ -> beginCreate ())
        emptyCta.PointerPressed.Add(fun _ -> beginCreate ())
        forkButton.PointerPressed.Add(fun _ -> this.ForkConversation())
        settingsButton.PointerPressed.Add(fun _ -> this.ShowSessionSettings())
        connectButton.PointerPressed.Add(fun _ ->
            if client.IsConnected && authenticated then
                try client.Disconnect() with _ -> ()
                authenticated <- false
                setStatus "已断开" false
            else
                this.ShowConnectDialog())
        convList.SelectionChanged.Add(fun _ ->
            match convList.SelectedItem with
            | :? ConvSummary as c -> this.OpenConversation c.Id
            | _ -> ())
        sendButton.PointerPressed.Add(fun _ -> this.SendMessage())
        attachButton.PointerPressed.Add(fun _ -> this.PickAttachment())
        cancelButton.PointerPressed.Add(fun _ ->
            match activeConvId, activeGenerationId with
            | Some convId, Some gid ->
                client.SendAsync(GenerationCancel {| conversationId = convId; generationId = gid |}) |> ignore
            | _ -> ())
        inputBox.KeyDown.Add(fun e ->
            if e.Key = Avalonia.Input.Key.Enter then
                let shift = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)
                let ctrl = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)
                if shift then
                    () // AcceptsReturn：Shift+Enter 换行
                elif ctrl || e.KeyModifiers = Avalonia.Input.KeyModifiers.None then
                    e.Handled <- true
                    this.SendMessage()
                    inputBox.Focus() |> ignore)
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

    /// 连接对话框（暖纸卡片：标题带品牌左缘 + 字段 + 主操作，配对收进可展开区）。
    member private _.ShowConnectDialog() =
        // --- 字段工厂：统一 8pt 圆角、ivory 底、暖纸描边、12.5px 字，focus 已由 App.fs 压为墨蓝 ---
        let fieldBox (text: string) (placeholder: string) =
            TextBox(Text = text, PlaceholderText = placeholder, CornerRadius = CornerRadius(Theme.radiusMd), Padding = Thickness(Theme.space3, 8.0), FontSize = 12.5, BorderBrush = Theme.outlineVariant, BorderThickness = Thickness(1.0), Background = Theme.panel)
        let urlBox = fieldBox (CredentialStore.defaultServerUrl ()) "例如 ws://127.0.0.1:8765/ws"
        let tokenBox = fieldBox "" "粘贴访问令牌（可留空走配对）"
        // 字段标签（小写 upper，暖灰）
        let label (s: string) = TextBlock(Text = s, FontSize = 10.5, FontWeight = FontWeight.Medium, Foreground = Theme.faint, Margin = Thickness(2.0, 0.0, 0.0, 2.0))
        let urlField =
            let col = StackPanel(Spacing = 4.0)
            col.Children.Add(label "服务器地址") |> ignore
            col.Children.Add(urlBox) |> ignore
            col
        let tokenField =
            let col = StackPanel(Spacing = 4.0)
            col.Children.Add(label "访问令牌") |> ignore
            col.Children.Add(tokenBox) |> ignore
            col
        // 配对区：默认收起，点击展开
        let pairCodeBox =
            TextBox(PlaceholderText = "输入 6 位配对码", MaxLength = 6, CornerRadius = CornerRadius(Theme.radiusMd), Padding = Thickness(Theme.space3, 8.0), FontSize = 14.0, HorizontalContentAlignment = HorizontalAlignment.Center, LetterSpacing = 6.0, BorderBrush = Theme.outlineVariant, BorderThickness = Thickness(1.0), Background = Theme.panel, FontFamily = FontFamily("ui-monospace, SFMono-Regular, Menlo, Consolas, monospace"))
        pairCodeBox.IsVisible <- false
        let pairHint = TextBlock(Text = "配对码显示在服务器终端（stderr），5 分钟内有效。", FontSize = 11.0, Foreground = Theme.muted, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = Thickness(2.0, 6.0, 2.0, 0.0))
        let pairSubmit =
            Button(Content = "提交配对码", CornerRadius = CornerRadius(Theme.radiusPill), Background = Theme.panel, Foreground = Theme.text, BorderBrush = Theme.outlineVariant, BorderThickness = Thickness(1.0), Padding = Thickness(16.0, 7.0), FontSize = 12.0, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, IsVisible = false, IsEnabled = false)
        let pairToggle =
            Button(Content = "没有令牌？使用配对码 →", Background = Brushes.Transparent, Foreground = Theme.primary, BorderThickness = Thickness(0.0), Padding = Thickness(2.0, 4.0), FontSize = 12.0, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursor(StandardCursorType.Hand))
        pairToggle.Click.Add(fun _ ->
            let show = not pairCodeBox.IsVisible
            pairCodeBox.IsVisible <- show
            pairHint.IsVisible <- show
            pairSubmit.IsVisible <- show
            pairSubmit.IsEnabled <- show && client.IsConnected
            pairToggle.Content <- if show then "收起配对 ↑" else "没有令牌？使用配对码 →"
            pairingRequestedBeforeConnect <- show
            if show && client.IsConnected then
                client.SendAsync(PairingRequested {| clientName = Some CredentialStore.clientName |}) |> ignore
            if show then Dispatcher.UIThread.Post(fun () -> pairCodeBox.Focus() |> ignore))
        pairSubmit.Click.Add(fun _ ->
            client.SendAsync(PairingAttempted {| code = pairCodeBox.Text; clientName = Some CredentialStore.clientName |}) |> ignore)
        // --- 标题区：品牌左缘 + 主标题 + 副标题 ---
        let titleRow =
            let t = TextBlock(Text = "连接万象", FontSize = 16.0, FontWeight = FontWeight.SemiBold, Foreground = Theme.text)
            Border(BorderBrush = Theme.brandLine, BorderThickness = Thickness(2.5, 0.0, 0.0, 0.0), CornerRadius = CornerRadius(1.5), Padding = Thickness(10.0, 2.0, 0.0, 2.0), Child = t)
        let subtitle = TextBlock(Text = "输入服务器地址与访问令牌；首次使用可走配对码。", FontSize = 11.5, Foreground = Theme.muted, TextWrapping = TextWrapping.Wrap, LineHeight = 16.0, Margin = Thickness(2.0, 0.0, 0.0, 0.0))
        let header = StackPanel(Spacing = 6.0)
        header.Children.Add(titleRow) |> ignore
        header.Children.Add(subtitle) |> ignore
        // --- 主操作：墨蓝 pill + 次操作取消 ---
        let mutable connectInProgress = false
        let btnConnect =
            Button(Content = "连接", CornerRadius = CornerRadius(Theme.radiusPill), Background = Theme.primary, Foreground = Theme.onPrimary, BorderThickness = Thickness(0.0), Padding = Thickness(18.0, 10.0), FontWeight = FontWeight.SemiBold, FontSize = 13.0, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center)
        let btnCancel =
            Button(Content = "取消", CornerRadius = CornerRadius(Theme.radiusPill), Background = Theme.panel, Foreground = Theme.muted, BorderBrush = Theme.outlineVariant, BorderThickness = Thickness(1.0), Padding = Thickness(18.0, 10.0), FontSize = 13.0, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = Thickness(0.0, 8.0, 0.0, 0.0))
        btnCancel.Click.Add(fun _ -> closeDialog ())
        btnConnect.Click.Add(fun _ ->
            if connectInProgress then ()
            else
                let url = if isNull urlBox.Text then "" else urlBox.Text.Trim()
                let token = if isNull tokenBox.Text then "" else tokenBox.Text.Trim()
                if String.IsNullOrWhiteSpace url then
                    urlBox.BorderBrush <- Theme.primary
                    Dispatcher.UIThread.Post(fun () -> urlBox.Focus() |> ignore)
                else
                    connectInProgress <- true
                    btnConnect.IsEnabled <- false
                    btnConnect.Content <- "连接中…"
                    lastUrl <- url
                    lastToken <- if String.IsNullOrWhiteSpace token then None else Some token
                    match reconnectCts with | Some c -> c.Cancel() | None -> ()
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
                                connectInProgress <- false
                                btnConnect.IsEnabled <- true
                                btnConnect.Content <- "连接"
                                setStatus (sprintf "连接失败 · %s" e.Message) false)
                    } |> Async.Start)
        // --- 组装 ---
        let panel = StackPanel(Spacing = 12.0, Width = 400.0)
        let sep = Border(Height = 1.0, Background = Theme.border, Opacity = 0.9, Margin = Thickness(0.0, 2.0, 0.0, 2.0))
        let pairSection = StackPanel(Spacing = 6.0)
        pairSection.Children.Add(pairToggle) |> ignore
        pairSection.Children.Add(pairCodeBox) |> ignore
        pairSection.Children.Add(pairHint) |> ignore
        pairSection.Children.Add(pairSubmit) |> ignore
        let actions = StackPanel(Spacing = 0.0, Margin = Thickness(0.0, 4.0, 0.0, 0.0))
        actions.Children.Add(btnConnect) |> ignore
        actions.Children.Add(btnCancel) |> ignore
        panel.Children.Add(header) |> ignore
        panel.Children.Add(urlField) |> ignore
        panel.Children.Add(tokenField) |> ignore
        panel.Children.Add(sep) |> ignore
        panel.Children.Add(pairSection) |> ignore
        panel.Children.Add(actions) |> ignore
        showDialog panel 420.0
        Dispatcher.UIThread.Post(fun () -> urlBox.Focus() |> ignore)

    /// 处理服务端事件（UI 线程）。
    member private this.HandleEvent(ev: WireEvent) =
        match ev with
        | Hello d -> ()
        | AuthAccepted d ->
            authenticated <- true
            let host =
                try
                    let u = Uri(lastUrl)
                    if String.IsNullOrWhiteSpace u.Host then lastUrl else u.Host
                with _ -> lastUrl
            setStatus (sprintf "已连接 · %s" host) true
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
            setStatus (sprintf "认证失败：%s · 请重新连接或重新配对" d.reason) false
            connectButton.IsEnabled <- true
            lastToken <- None
        | PairingStarted _ ->
            setStatus "配对码已输出到服务器终端（stderr）" false
        | PairingSucceeded d ->
            setStatus "配对成功，正在认证…" false
            lastToken <- Some d.token
            client.SendAsync(AuthPresent {| token = d.token |}) |> ignore
        | PairingFailed d ->
            if d.frozen then
                setStatus (sprintf "配对已冻结 %d 分钟：%s · 请稍后重试" d.freezeMinutes d.reason) false
            else
                setStatus (sprintf "配对失败：%s · 请核对配对码后重试" d.reason) false
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
            // P1-1：生成中会话置顶，其余保持服务端更新时间倒序
            rawSummaries <-
                Array.append
                    (rawSummaries |> Array.filter (fun c -> c.Running))
                    (rawSummaries |> Array.filter (fun c -> not c.Running))
            let q = if String.IsNullOrWhiteSpace searchBox.Text then "" else searchBox.Text.Trim().ToLowerInvariant()
            if String.IsNullOrEmpty q then
                convList.ItemsSource <- rawSummaries
            else
                convList.ItemsSource <- rawSummaries |> Array.filter (fun c -> c.Title.ToLowerInvariant().Contains q || c.Preview.ToLowerInvariant().Contains q)
            state.AdvanceCursor()
            syncSelection ()
            refreshListEmpty ()
            // 列表变空或尚无激活会话时刷新空态文案（无会话 vs 过滤无匹配）
            if activeConvId.IsNone then this.RenderMessages()
        | ConversationSnapshot d ->
            state.Handle ev
            state.AdvanceCursor()
            activeConvId <- Some d.conversationId
            chatTitle.Text <- d.title
            setConversationChrome true
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
            generationStartedAt <- Some DateTimeOffset.UtcNow
            cancelButton.IsEnabled <- true
            streamText.Clear() |> ignore
            streamReasoning.Clear() |> ignore
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
                if mv.reasoning.Length > 0 then startReasoningTimer ()
                this.RenderMessages()
        | GenerationFinished d ->
            state.Handle ev
            state.AdvanceCursor()
            activeGenerationId <- None
            let startedAt = generationStartedAt
            generationStartedAt <- None
            stopReasoningTimer ()
            cancelButton.IsEnabled <- false
            stopGenTimer ()
            showGenChip false
            streamText.Clear() |> ignore
            streamReasoning.Clear() |> ignore
            match d.usage with
            | Some u -> lastUsageByConv <- lastUsageByConv.Add(d.conversationId, u)
            | None ->
                match startedAt with
                | Some s ->
                    let ms = int64 (DateTimeOffset.UtcNow - s).TotalMilliseconds
                    lastUsageByConv <- lastUsageByConv.Add(d.conversationId, { GenerationUsage.empty with durationMs = Some ms })
                | None -> ()
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
            match d.requiredCommitId with
            | Some _ ->
                // 决策 36：stale-projection — 游标追赶已由 AuthorityCatchUp 驱动，提示用户重试即可
                setStatus "数据不是最新，已自动追赶请重试" (authenticated)
            | None ->
                setStatus (sprintf "命令被拒绝：%s · 请重试" d.message) (authenticated)
        | ServerError d ->
            setStatus (sprintf "服务器错误：%s · 请稍后重试" d.message) (authenticated)
            // P2-3/Q179：附件缺失标记（blob 被删时下载失败）
            if d.message.StartsWith "attachment " && d.message.Contains "not found" then
                let sha = d.message.Substring("attachment ".Length, 64)
                missingAttachments <- missingAttachments.Add sha
                this.RenderMessages()
        | _ -> ()

    member private this.AppendMessage(mv: MessageView, refs: AttachmentRef list, streaming: bool, ?commitId: uint64, ?usage: GenerationUsage, ?showUsage: bool) =
        let isUser = mv.role = "user"
        let isTool = mv.role = "tool"
        let usage = defaultArg usage GenerationUsage.empty
        let showUsage = defaultArg showUsage false
        // 用户：唯一保留的“气泡”，收窄柔和；助手/工具：无边框融入画布（避免大方块感）
        let bubble = Border(Padding = Thickness(0.0), MaxWidth = 760.0)
        if isUser then
            bubble.Padding <- Thickness(15.0, 10.0)
            bubble.CornerRadius <- CornerRadius(Theme.radiusLg, Theme.radiusLg, Theme.radiusSm, Theme.radiusLg)
            bubble.Background <- Theme.primary
            bubble.MaxWidth <- 520.0
            bubble.HorizontalAlignment <- HorizontalAlignment.Right
        elif isTool then
            bubble.CornerRadius <- CornerRadius(Theme.radiusLg, Theme.radiusLg, Theme.radiusLg, Theme.radiusSm)
            bubble.Background <- Theme.toolChip
            bubble.Padding <- Thickness(14.0, 6.0)
            bubble.MaxWidth <- 460.0
            bubble.HorizontalAlignment <- HorizontalAlignment.Center
        else
            bubble.Background <- Brushes.Transparent
            bubble.HorizontalAlignment <- HorizontalAlignment.Stretch
        let panel = StackPanel(Spacing = 6.0)
        // 思维链：左侧竖线引用式折叠（流式期间展开；完成后可自动收起）
        if not isUser && not isTool && not (String.IsNullOrEmpty mv.reasoning) then
            let reasoningMs =
                if streaming then
                    match generationStartedAt with
                    | Some s -> Some(int64 (DateTimeOffset.UtcNow - s).TotalMilliseconds)
                    | None -> None
                else usage.durationMs
            let durationSuffix =
                match reasoningMs with
                | Some ms -> " · " + formatReasoningDuration ms
                | None -> ""
            let collapsedDefault = not streaming && uiPrefs.autoCollapseReasoning
            let thinkBody = TextBlock(Text = mv.reasoning, TextWrapping = TextWrapping.Wrap, FontSize = smallFontSize, Foreground = Theme.muted, LineHeight = 19.0, IsVisible = not collapsedDefault, Margin = Thickness(0.0, 6.0, 0.0, 0.0))
            let chevron = TextBlock(Text = (if collapsedDefault then "▸" else "▾"), FontSize = 10.0, Foreground = Theme.primary, VerticalAlignment = VerticalAlignment.Center, Margin = Thickness(0.0, 0.0, 6.0, 0.0))
            let thinkLabel = TextBlock(Text = "思考过程" + durationSuffix, FontSize = smallFontSize, FontWeight = FontWeight.SemiBold, Foreground = Theme.muted, LetterSpacing = 0.4)
            let autoCollapseToggle =
                CheckBox(
                    Content = "完成后自动收起", FontSize = 10.5, Foreground = Theme.faint,
                    IsChecked = Nullable uiPrefs.autoCollapseReasoning, Margin = Thickness(0.0, 4.0, 0.0, 0.0), IsVisible = thinkBody.IsVisible)
            autoCollapseToggle.Click.Add(fun _ ->
                let isOn = autoCollapseToggle.IsChecked.GetValueOrDefault(true)
                uiPrefs <- { uiPrefs with autoCollapseReasoning = isOn }
                UiPrefs.save uiPrefs)
            let toggleRow = StackPanel(Orientation = Orientation.Horizontal, Cursor = Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand))
            toggleRow.Children.Add(chevron) |> ignore
            toggleRow.Children.Add(thinkLabel) |> ignore
            toggleRow.Focusable <- true
            Avalonia.Automation.AutomationProperties.SetName(toggleRow, "切换思考过程显示")
            let toggle () =
                thinkBody.IsVisible <- not thinkBody.IsVisible
                autoCollapseToggle.IsVisible <- thinkBody.IsVisible
                chevron.Text <- if thinkBody.IsVisible then "▾" else "▸"
            toggleRow.PointerPressed.Add(fun _ -> toggle ())
            toggleRow.KeyDown.Add(fun e ->
                if e.Key = Avalonia.Input.Key.Enter || e.Key = Avalonia.Input.Key.Space then
                    e.Handled <- true
                    toggle ())
            let thinkStack = StackPanel(Spacing = 0.0)
            thinkStack.Children.Add(toggleRow) |> ignore
            thinkStack.Children.Add(thinkBody) |> ignore
            thinkStack.Children.Add(autoCollapseToggle) |> ignore
            let thinkAccent =
                Border(
                    BorderBrush = Theme.primary, BorderThickness = Thickness(2.0, 0.0, 0.0, 0.0),
                    Padding = Thickness(14.0, 4.0, 0.0, 4.0), Margin = Thickness(0.0, 0.0, 0.0, 8.0),
                    Child = thinkStack)
            panel.Children.Add(thinkAccent) |> ignore
        let fg: IBrush = if isUser then Theme.userText :> IBrush else Theme.text :> IBrush
        
        if not (String.IsNullOrEmpty mv.text) then
            if isUser then
                panel.Children.Add(TextBlock(Text = mv.text, TextWrapping = TextWrapping.Wrap, Foreground = fg, FontSize = messageFontSize, LineHeight = 21.0))
            else
                let segments = MarkdownParser.parse mv.text
                for seg in segments do
                    match seg with
                    | NormalText spans ->
                        let tb = SelectableTextBlock(TextWrapping = TextWrapping.Wrap, Foreground = fg, FontSize = messageFontSize, LineHeight = 24.0)
                        for span in spans do
                            match span with
                            | TextSpan(t, bold, italic, code) ->
                                let run = Run(t)
                                if bold then run.FontWeight <- FontWeight.Bold
                                if italic then run.FontStyle <- FontStyle.Italic
                                if code then
                                    run.FontFamily <- FontFamily("ui-monospace, SFMono-Regular, Menlo, Consolas, monospace")
                                    run.Foreground <- Theme.inlineCodeFg
                                    run.Background <- Theme.inlineCodeBg
                                tb.Inlines.Add(run) |> ignore
                            | LinkSpan(label, url) ->
                                let linkText =
                                    TextBlock(
                                        Text = label,
                                        Foreground = Theme.primary,
                                        TextDecorations = TextDecorations.Underline,
                                        FontSize = messageFontSize,
                                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                                        VerticalAlignment = VerticalAlignment.Center)
                                linkText.PointerPressed.Add(fun ev ->
                                    ev.Handled <- true
                                    try
                                        Process.Start(ProcessStartInfo(FileName = url, UseShellExecute = true)) |> ignore
                                    with ex ->
                                        setStatus (sprintf "无法打开链接: %s" ex.Message) authenticated)
                                tb.Inlines.Add(InlineUIContainer(linkText)) |> ignore
                            | BreakSpan ->
                                tb.Inlines.Add(LineBreak()) |> ignore
                        panel.Children.Add(tb)
                    | CodeBlock(lang, code) ->
                        let card = Border(Background = Theme.codeBg, BorderBrush = Theme.codeBorder, BorderThickness = Thickness(1.0), CornerRadius = CornerRadius(Theme.radiusLg), Margin = Thickness(0.0, 8.0), Padding = Thickness(0.0))
                        let cardStack = StackPanel(Spacing = 0.0)
                        let headerDock = DockPanel()
                        let header = Border(Background = Theme.codeHeaderBg, BorderBrush = Theme.codeBorder, BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0), Padding = Thickness(14.0, 8.0), Child = headerDock)

                        let langLabel = TextBlock(Text = lang.ToLowerInvariant(), FontSize = 11.0, Foreground = Theme.codeHeaderText, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, LetterSpacing = 0.4)
                        let copyIconBtn = Icons.createButton Icons.Outline (Icons.copy Theme.codeText)
                        copyIconBtn.Width <- 26.0; copyIconBtn.Height <- 26.0; copyIconBtn.MinWidth <- 26.0; copyIconBtn.MinHeight <- 26.0; copyIconBtn.MaxWidth <- 26.0; copyIconBtn.MaxHeight <- 26.0
                        copyIconBtn.CornerRadius <- CornerRadius(6.0)
                        copyIconBtn.Background <- Theme.codeBorder
                        copyIconBtn.BorderBrush <- null
                        copyIconBtn.BorderThickness <- Thickness(0.0)
                        ToolTip.SetTip(copyIconBtn, "复制")
                        copyIconBtn.PointerPressed.Add(fun _ ->
                            try
                                (topLevel ()).Clipboard.SetTextAsync(code) |> ignore
                                copyIconBtn.Opacity <- 0.5
                                async {
                                    do! Async.Sleep 900
                                    Dispatcher.UIThread.Post(fun () -> copyIconBtn.Opacity <- 1.0)
                                } |> Async.Start
                            with _ -> ())

                        DockPanel.SetDock(langLabel, Dock.Left)
                        DockPanel.SetDock(copyIconBtn, Dock.Right)
                        headerDock.Children.Add(langLabel)
                        headerDock.Children.Add(copyIconBtn)
                        headerDock.Children.Add(Border()) // spacer

                        let codeText = TextBlock(Text = code, FontFamily = FontFamily("ui-monospace, SFMono-Regular, Menlo, Consolas, monospace"), FontSize = 12.5, Foreground = Theme.codeText, Margin = Thickness(14.0, 12.0, 14.0, 14.0))
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
                let link = Button(Content = sprintf "下载附件：%s (%s)" r.fileName (formatSize r.size), FontSize = 12.0, Padding = Thickness(10.0, 4.0), CornerRadius = CornerRadius(999.0), Background = Theme.panel, Foreground = Theme.text, BorderBrush = Theme.outlineVariant, BorderThickness = Thickness(1.0), HorizontalAlignment = HorizontalAlignment.Left, Margin = Thickness(0.0, 4.0, 0.0, 0.0))
                link.Click.Add(fun _ -> this.DownloadAttachment r.sha256)
                panel.Children.Add link
        // P1-2：token/耗时弱字展示（悬停出明细）
        if showUsage && not isUser && not isTool then
            match GenerationUsage.formatSummary usage with
            | Some summary ->
                let meta =
                    TextBlock(
                        Text = summary, FontSize = 11.0, Foreground = Theme.faint,
                        Margin = Thickness(0.0, 4.0, 0.0, 0.0), HorizontalAlignment = HorizontalAlignment.Left)
                ToolTip.SetTip(meta, GenerationUsage.formatDetail usage)
                panel.Children.Add meta
            | None -> ()
        bubble.Child <- panel

        // 排版不被微操作扰动：工具条不占文档流，点头像才在头像旁横向弹出，带遮罩
        // 1) 全局单例 popup + scrim（惰性创建，复用；用闭包持有引用，避免 FindName）
        let mutable messageScrim: Grid option = None
        let mutable messagePopup: Border option = None
        let ensureMessagePopup () : Border * Grid =
            match messagePopup, messageScrim with
            | Some p, Some s -> p, s
            | _ ->
                let scrim = Grid(Background = Brushes.Transparent, IsVisible = false, IsHitTestVisible = true)
                let popup = Border(Background = Theme.panel, BorderBrush = Theme.border, BorderThickness = Thickness(1.0), CornerRadius = CornerRadius(Theme.radiusMd), Padding = Thickness(6.0, 4.0), IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top)
                popup.BoxShadow <- BoxShadows(BoxShadow(OffsetX = 0.0, OffsetY = 6.0, Blur = 18.0, Spread = -8.0, Color = Theme.shadowSoft))
                let host = this.Content :?> Grid
                Grid.SetColumnSpan(scrim, 2)
                Grid.SetColumnSpan(popup, 2)
                host.Children.Add(scrim) |> ignore
                host.Children.Add(popup) |> ignore
                scrim.PointerPressed.Add(fun _ ->
                    scrim.IsVisible <- false
                    popup.IsVisible <- false)
                messageScrim <- Some scrim
                messagePopup <- Some popup
                popup, scrim

        let iconToolButton (icon: Control) (tip: string) =
            let b = Icons.createButton Icons.Outline icon
            b.Width <- 28.0; b.Height <- 28.0; b.MinWidth <- 28.0; b.MinHeight <- 28.0; b.MaxWidth <- 28.0; b.MaxHeight <- 28.0
            b.CornerRadius <- CornerRadius(8.0)
            ToolTip.SetTip(b, tip)
            b

        let bindIconCopy (btn: Border) (text: string) =
            btn.PointerPressed.Add(fun _ ->
                try
                    (topLevel ()).Clipboard.SetTextAsync(text) |> ignore
                    let prev = btn.Opacity
                    btn.Opacity <- 0.5
                    async {
                        do! Async.Sleep 900
                        Dispatcher.UIThread.Post(fun () -> btn.Opacity <- prev)
                    } |> Async.Start
                with _ -> ())

        // 2) 构造本次消息的动作集（图标）
        let actions = System.Collections.Generic.List<Border>()
        let copyBtn = iconToolButton (Icons.copy Theme.text) "复制"
        bindIconCopy copyBtn (if isNull mv.text then "" else mv.text)
        actions.Add(copyBtn)
        if not isUser && not isTool && not (String.IsNullOrEmpty mv.text) && mv.text.Contains("```") then
            let codeOnly =
                MarkdownParser.parse mv.text
                |> List.choose (function CodeBlock(_, code) -> Some code | _ -> None)
                |> fun parts -> if List.isEmpty parts then mv.text else String.Join("\n\n", parts)
            let codeBtn = iconToolButton (Icons.copy Theme.text) "复制代码"
            bindIconCopy codeBtn codeOnly
            // 用 tooltip 区分
            ToolTip.SetTip(codeBtn, "复制代码块")
            actions.Add(codeBtn)
        if isUser && not streaming then
            let forkBtn = iconToolButton (Icons.forkIcon Theme.text) "编辑并 fork"
            forkBtn.PointerPressed.Add(fun _ ->
                match messagePopup, messageScrim with
                | Some p, Some s -> p.IsVisible <- false; s.IsVisible <- false
                | _ -> ()
                this.ForkConversation(?commitId = commitId, editedSeed = mv))
            actions.Add(forkBtn)

        // 3) 头像（点击触发 popup），消息体保持原有布局
        let avatar: Control =
            if isUser then
                let b = Border(Width = 30.0, Height = 30.0, CornerRadius = CornerRadius(15.0), Background = Theme.primary, BorderBrush = Brushes.Transparent, BorderThickness = Thickness(0.0), Cursor = Cursor(StandardCursorType.Hand))
                b.Child <- TextBlock(Text = "你", FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Theme.onPrimary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center)
                b :> Control
            else
                let w = createBrandLogo Theme.logoSizeAvatar
                w.Cursor <- Cursor(StandardCursorType.Hand)
                w :> Control
        avatar.VerticalAlignment <- VerticalAlignment.Top
        avatar.Margin <- Thickness(0.0, 2.0, 0.0, 0.0)

        // 头像点击：横向向消息方向弹出（不改变排版，遮罩点外关闭）
        avatar.PointerPressed.Add(fun _ ->
            if isTool then ()
            else
                let popup, scrim = ensureMessagePopup ()
                let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = 6.0)
                for btn in actions do row.Children.Add(btn) |> ignore
                popup.Child <- row
                scrim.IsVisible <- true
                popup.IsVisible <- true
                Dispatcher.UIThread.Post(fun () ->
                    try
                        let host = this.Content :?> Grid
                        let nullablePt: Nullable<Point> = avatar.TranslatePoint(Point(0.0, 0.0), host)
                        if nullablePt.HasValue then
                            let pt = nullablePt.Value
                            let x = if isUser then pt.X - 130.0 else pt.X + 36.0
                            let y = pt.Y - 6.0
                            popup.Margin <- Thickness(x, y, 0.0, 0.0)
                    with _ -> ()))

        let messageBody: Control =
            if isTool then
                bubble :> Control
            else
                let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = 10.0)
                if isUser then
                    row.HorizontalAlignment <- HorizontalAlignment.Right
                    row.Children.Add(bubble) |> ignore
                    row.Children.Add(avatar) |> ignore
                else
                    row.HorizontalAlignment <- HorizontalAlignment.Left
                    row.Children.Add(avatar) |> ignore
                    row.Children.Add(bubble) |> ignore
                row :> Control

        // host 不再包含 toolbar，彻底不占流
        let host = StackPanel(Spacing = 0.0)
        if isUser then host.HorizontalAlignment <- HorizontalAlignment.Right
        elif isTool then host.HorizontalAlignment <- HorizontalAlignment.Center
        else host.HorizontalAlignment <- HorizontalAlignment.Stretch
        host.Children.Add(messageBody) |> ignore
        messagesPanel.Children.Add(host)

    /// 完整重绘消息面板（快照 / 新消息 / 流式增量 / 历史分页共用）。
    member private this.RenderMessages() =
        messagesPanel.Children.Clear()
        emptyOverlay.IsVisible <- false
        emptyCta.IsVisible <- false
        match activeConvId with
        | None ->
            emptyHint.Text <- "从侧栏新建，或点下方开始"
            emptyCta.IsVisible <- true
            emptyOverlay.IsVisible <- true
        | Some convId ->
            match state.Conversations.TryFind convId with
            | None ->
                // 会话切换等待快照：标题已是「加载中…」，此处保持空白阅读区
                ()
            | Some view ->
                if view.messages.Count = 0 && view.runtimeState <> "generating" then
                    emptyHint.Text <- "写一条消息开始"
                    emptyOverlay.IsVisible <- true
                let convUsage = lastUsageByConv |> Map.tryFind convId |> Option.defaultValue GenerationUsage.empty
                let msgArray = view.messages |> Seq.cast<JsonNode> |> Seq.toArray
                let lastAssistantIdx =
                    msgArray
                    |> Array.mapi (fun i node ->
                        let payload =
                            match node with
                            | :? JsonObject as o ->
                                let mutable p: JsonNode = null
                                if o.TryGetPropertyValue("payload", &p) && not (isNull p) then p else node
                            | _ -> node
                        let mv = MessageView.ofJson payload
                        if mv.role = "assistant" then Some i else None)
                    |> Array.choose id
                    |> Array.tryLast
                for i, m in msgArray |> Array.indexed do
                    let payload =
                        match m with
                        | :? JsonObject as o ->
                            let mutable p: JsonNode = null
                            if o.TryGetPropertyValue("payload", &p) && not (isNull p) then p else o
                        | node -> node
                    let cid =
                        match m with
                        | :? JsonObject as o ->
                            let mutable c: JsonNode = null
                            if o.TryGetPropertyValue("commitId", &c) && not (isNull c) && c :? JsonValue then
                                match (c :?> JsonValue).TryGetValue<uint64>() with
                                | true, v -> Some v
                                | _ -> None
                            else None
                        | _ -> None
                    let showUsage = lastAssistantIdx = Some i
                    let usage = if showUsage then convUsage else GenerationUsage.empty
                    match cid with
                    | Some id -> this.AppendMessage(MessageView.ofJson payload, AttachmentRef.extract payload, false, commitId = id, usage = usage, showUsage = showUsage)
                    | None -> this.AppendMessage(MessageView.ofJson payload, AttachmentRef.extract payload, false, usage = usage, showUsage = showUsage)
                // P1-4：流式增量（临时展示）
                if view.runtimeState = "generating" && (streamText.Length > 0 || streamReasoning.Length > 0) then
                    let liveUsage =
                        match generationStartedAt with
                        | Some s -> { GenerationUsage.empty with durationMs = Some(int64 (DateTimeOffset.UtcNow - s).TotalMilliseconds) }
                        | None -> GenerationUsage.empty
                    this.AppendMessage({ role = "assistant"; text = streamText.ToString(); reasoning = streamReasoning.ToString() }, [], true, usage = liveUsage, showUsage = false)
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
                inputBox.Focus() |> ignore
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
                    chatTitle.Text <- "选择一个会话"
                    chatTitle.Foreground <- Theme.faint
                    stopGenTimer ()
                    showGenChip false
                    setConversationChrome false
                    syncSelection ()
                    this.RenderMessages()
                closeDialog ())
            let cancelBtn = Button(Content = "取消", HorizontalAlignment = HorizontalAlignment.Right, Margin = Thickness(0.0, 8.0, 8.0, 0.0))
            cancelBtn.Click.Add(fun _ -> closeDialog ())
            let panel = StackPanel(Spacing = 8.0)
            panel.Children.Add(TextBlock(Text = "删除会话", FontSize = 15.0, FontWeight = FontWeight.Bold)) |> ignore
            panel.Children.Add(msg) |> ignore
            panel.Children.Add(okBtn) |> ignore
            panel.Children.Add(cancelBtn) |> ignore
            showDialog panel 400.0

    /// P0-4：会话设置最小入口——读写 SessionConfig，落 UpdateConversationConfig（不改 TOML）。
    member private this.ShowSessionSettings() =
        match activeConvId with
        | None -> setStatus "请先选择会话" (authenticated)
        | Some convId ->
            let current =
                match state.Conversations.TryFind convId with
                | Some view -> view.config
                | None -> SessionConfig.empty
            let labeledBox (label: string) (box: TextBox) =
                let col = StackPanel(Spacing = 4.0)
                col.Children.Add(TextBlock(Text = label, FontSize = 12.0, Foreground = Theme.muted)) |> ignore
                col.Children.Add(box) |> ignore
                col
            let fieldBox (text: string) (placeholder: string) =
                TextBox(
                    Text = text, PlaceholderText = placeholder, CornerRadius = CornerRadius(10.0),
                    Padding = Thickness(12.0, 9.0), FontSize = 13.0, BorderBrush = Theme.border, BorderThickness = Thickness(1.0))
            let providerBox = fieldBox current.provider "如 openai / ollama"
            let modelBox = fieldBox current.model "模型名称"
            let temperatureBox =
                fieldBox
                    (match current.temperature with Some t -> t.ToString("0.##") | None -> "")
                    "0–2，空为不设"
            let instructionsBox =
                TextBox(
                    Text = (current.instructions |> Option.defaultValue ""),
                    PlaceholderText = "系统指令（可选）",
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 80.0,
                    CornerRadius = CornerRadius(10.0),
                    Padding = Thickness(12.0, 9.0),
                    FontSize = 13.0,
                    BorderBrush = Theme.border,
                    BorderThickness = Thickness(1.0))
            let maxTokensBox =
                fieldBox
                    (match current.maxTokens with Some m -> string m | None -> "")
                    "最大 token（可选）"
            let cancelBtn =
                Button(
                    Content = "取消", CornerRadius = CornerRadius(Theme.radiusSm), Background = Theme.panel, Foreground = Theme.text, BorderBrush = Theme.outlineVariant, BorderThickness = Thickness(1.0),
                    Padding = Thickness(14.0, 8.0), Margin = Thickness(0.0, 0.0, 8.0, 0.0))
            let okBtn =
                Button(
                    Content = "确定", CornerRadius = CornerRadius(10.0), Background = Theme.primary,
                    Foreground = Theme.onPrimary, BorderThickness = Thickness(0.0), Padding = Thickness(14.0, 8.0))
            let btnRow = StackPanel(Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = Thickness(0.0, 4.0, 0.0, 0.0))
            btnRow.Children.Add(cancelBtn) |> ignore
            btnRow.Children.Add(okBtn) |> ignore
            let panel = StackPanel(Spacing = 10.0, Width = 420.0)
            panel.Children.Add(TextBlock(Text = "会话设置", FontSize = 15.0, FontWeight = FontWeight.Bold, Foreground = Theme.text)) |> ignore
            panel.Children.Add(TextBlock(Text = "修改本会话的模型与生成参数，不改 TOML。", FontSize = 12.5, Foreground = Theme.muted, TextWrapping = TextWrapping.Wrap)) |> ignore
            panel.Children.Add(labeledBox "Provider" providerBox) |> ignore
            panel.Children.Add(labeledBox "Model" modelBox) |> ignore
            panel.Children.Add(labeledBox "Temperature" temperatureBox) |> ignore
            panel.Children.Add(labeledBox "System Instructions" instructionsBox) |> ignore
            panel.Children.Add(labeledBox "MaxTokens" maxTokensBox) |> ignore
            panel.Children.Add(btnRow) |> ignore
            cancelBtn.Click.Add(fun _ -> closeDialog ())
            okBtn.Click.Add(fun _ ->
                let provider = if isNull providerBox.Text then "" else providerBox.Text.Trim()
                let model = if isNull modelBox.Text then "" else modelBox.Text.Trim()
                let instructions =
                    let s = if isNull instructionsBox.Text then "" else instructionsBox.Text
                    if String.IsNullOrWhiteSpace s then None else Some s
                let temperature =
                    let s = if isNull temperatureBox.Text then "" else temperatureBox.Text.Trim()
                    if String.IsNullOrWhiteSpace s then None
                    else
                        match Double.TryParse(s, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                        | true, t -> Some t
                        | _ ->
                            match Double.TryParse(s) with
                            | true, t -> Some t
                            | _ -> None
                let maxTokens =
                    let s = if isNull maxTokensBox.Text then "" else maxTokensBox.Text.Trim()
                    if String.IsNullOrWhiteSpace s then None
                    else
                        match Int32.TryParse(s) with
                        | true, m -> Some m
                        | _ -> None
                // 保留 tools/extraJson：设置弹窗只改可见字段，避免无意清空
                let cfg =
                    { provider = provider
                      model = model
                      instructions = instructions
                      tools = current.tools
                      temperature = temperature
                      maxTokens = maxTokens
                      extraJson = current.extraJson }
                if not (SessionConfig.isValid cfg) then
                    setStatus "会话配置无效：需填写 Provider/Model；Temperature 0–2；MaxTokens > 0" (authenticated)
                else
                    client.SendCommandAsync(
                        UpdateConversationConfig
                            {| invocationId = Guid.CreateVersion7()
                               conversationId = convId
                               config = cfg |})
                    |> ignore
                    // 本地立即反映，便于再次打开设置；重新 observe 后仍以服务端快照为准
                    match state.Conversations.TryFind convId with
                    | Some view -> view.config <- cfg
                    | None -> ()
                    closeDialog ()
                    setStatus "会话配置已更新" (authenticated)
                    // 重新 observe：验收「投影更新后重新 observe 可见生效」
                    client.SendAsync(ObserveConversation {| conversationId = convId |}) |> ignore)
            showDialog panel 480.0
            Dispatcher.UIThread.Post(fun () -> providerBox.Focus() |> ignore)

    /// 决策 74-77：编辑最后一条消息并 fork 新会话。
    member private this.ForkConversation(?commitId: uint64, ?editedSeed: MessageView) =
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
                    let resolvedCommitId =
                        match commitId with
                        | Some v -> v
                        | None ->
                            let mutable cid = 0UL
                            match last with
                            | :? JsonObject as o ->
                                let mutable c: JsonNode = null
                                if o.TryGetPropertyValue("commitId", &c) && not (isNull c) then
                                    match c.GetValueKind() with
                                    | JsonValueKind.Number ->
                                        match c :? JsonValue with
                                        | true ->
                                            match (c :?> JsonValue).TryGetValue<uint64>() with
                                            | true, v -> cid <- v
                                            | _ -> ()
                                        | _ -> ()
                                    | _ -> ()
                                else ()
                            | _ -> ()
                            cid
                    let mv = match editedSeed with Some seed -> seed | None -> MessageView.ofJson payload
                    let editBox = TextBox(Text = mv.text, AcceptsReturn = true, MinHeight = 120.0, TextWrapping = TextWrapping.Wrap, CornerRadius = CornerRadius(10.0), Padding = Thickness(10.0, 8.0))
                    let cancelBtn = Button(Content = "取消", CornerRadius = CornerRadius(10.0), Background = Theme.panel, Foreground = Theme.text, BorderBrush = Theme.outlineVariant, BorderThickness = Thickness(1.0), Padding = Thickness(14.0, 8.0), Margin = Thickness(0.0, 0.0, 8.0, 0.0))
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
                                        | true, v when v < resolvedCommitId && v > prev -> prev <- v
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
        // 会话切换：标题弱字「加载中…」；若目标会话正在生成则保持 genChip 生成态
        if activeConvId <> Some id then
            activeConvId <- Some id
            chatTitle.Text <- "加载中…"
            chatTitle.Foreground <- Theme.faint
            setConversationChrome true
            streamText.Clear() |> ignore
            streamReasoning.Clear() |> ignore
            match rawSummaries |> Array.tryFind (fun c -> c.Id = id) with
            | Some c when c.Running ->
                showGenChip true
                startGenTimer ()
            | _ ->
                stopGenTimer ()
                showGenChip false
            this.RenderMessages()
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

        // P1-5：窗口居中 + 极轻外阴影（收敛默认 Fluent 平板感）
        this.WindowStartupLocation <- WindowStartupLocation.CenterScreen
        let shell =
            Border(
                Background = Theme.bg,
                BoxShadow = BoxShadows.Parse "0 1 0 #E2E0DA, 0 16 48 -20 #1C1B191A",
                Child = MainView())
        this.Content <- shell
