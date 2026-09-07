namespace Wanxiang.UI

open System
open System.Globalization
open System.Text
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading

/// Markdown 块模型 → Avalonia 控件。
///
/// 标题有真实字号、引用有左缘、列表有悬挂缩进、表格是真表格、
/// 代码块是带语言标签和复制按钮的卡片。
/// `allowHighlight = false` 用于流式进行中的消息：未写完的代码着不准，
/// 且每来一个 token 都重新着色一次会把机器点着。
type MarkdownRenderer(
    fontSize: float,
    copyText: string -> unit,
    openLink: string -> unit,
    allowHighlight: bool) =

    let handCursor = new Cursor(StandardCursorType.Hand)

    let headingSize (level: int) = ReadingRhythm.headingFontSize fontSize level

    /// 代码块折行的当前默认值。来自 UI 偏好，块内的切换也会更新它，
    /// 于是同一屏里后续渲染的代码块跟着用户刚做的选择。
    static member val DefaultCodeWrap = false with get, set

    /// 行内片段渲染成一个可选中的文本块。
    member private _.RenderInlines(items: MdInline list, size: float, weight: FontWeight, brush: IBrush, ?lineHeight: float, ?fontFamily: FontFamily, ?textAlignment: TextAlignment) : Control =
        let lh = defaultArg lineHeight (ReadingRhythm.proseLineHeight size)
        let block =
            SelectableTextBlock(
                TextWrapping = TextWrapping.Wrap,
                FontSize = size,
                FontWeight = weight,
                Foreground = brush,
                LineHeight = lh,
                SelectionBrush = Tokens.accentSoft)
        match fontFamily with
        | Some ff -> block.FontFamily <- ff
        | None -> ()
        match textAlignment with
        | Some align -> block.TextAlignment <- align
        | None -> ()
        for item in items do
            match item with
            | MdText(text, bold, italic, strike, code) ->
                let run = Run text
                // 不用合成加粗：内嵌字体只有 Regular，Skia 合成时 CJK 前进宽度会算错，
                // 字形互相叠压。强调改由强调色承担，可读且不会渲染错乱。
                if bold then run.Foreground <- Tokens.accent
                if italic then run.FontStyle <- FontStyle.Italic
                if strike then run.TextDecorations <- TextDecorations.Strikethrough
                if code then
                    // 行内代码靠「等宽 + 底色」区分；颜色留给链接，
                    // 否则同一行里的行内代码和链接看起来是一回事。
                    run.FontFamily <- Tokens.monoFontFamily
                    run.Foreground <- Tokens.text
                    run.Background <- Tokens.inlineCodeBg
                    run.FontSize <- size - 0.5
                    run.BaselineAlignment <- BaselineAlignment.Center
                block.Inlines.Add run
            | MdLink(text, url) ->
                let link = Run text
                link.Foreground <- Tokens.accent
                link.TextDecorations <- TextDecorations.Underline
                block.Inlines.Add link
                block.Cursor <- handCursor
                block.PointerReleased.Add(fun e ->
                    if block.IsEnabled && e.InitialPressMouseButton = MouseButton.Left then
                        e.Handled <- true
                        openLink url)
                block.KeyDown.Add(fun e ->
                    if block.IsEnabled && (e.Key = Key.Enter || e.Key = Key.Space) then
                        e.Handled <- true
                        openLink url)
                ToolTip.SetTip(block, url)
                Avalonia.Automation.AutomationProperties.SetName(block, text)
                Avalonia.Automation.AutomationProperties.SetHelpText(block, url)
                Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
                    block,
                    Nullable Avalonia.Automation.Peers.AutomationControlType.Hyperlink)
            | MdMath(tex, display) ->
                match MathRender.tryInline (if display then size + 1.0 else size) lh tex with
                | Some visual ->
                    visual.Margin <- Thickness(Tokens.space1, 0.0)
                    visual.VerticalAlignment <- VerticalAlignment.Center
                    let container = MathRender.inlineContainer visual
                    container.BaselineAlignment <- BaselineAlignment.Center
                    block.Inlines.Add container
                | None ->
                    // 没有脚本后端或 TeX 有语法错：按行内代码呈现原式，
                    // 至少让读者看得见作者写了什么
                    let run = Run tex
                    run.FontFamily <- Tokens.monoFontFamily
                    run.FontStyle <- FontStyle.Italic
                    run.Foreground <- Tokens.text
                    run.Background <- Tokens.inlineCodeBg
                    run.FontSize <- size - 0.5
                    run.BaselineAlignment <- BaselineAlignment.Center
                    block.Inlines.Add run
            | MdImage(alt, url) ->
                // 默认不主动请求远程 Markdown 图片：避免阅读模型回复时向第三方
                // 泄露网络信息或触发 tracking pixel。把来源域名直接写在正文里，
                // 让它看起来像明确策略，而不是一张“坏掉的图片”。
                let label = if String.IsNullOrWhiteSpace alt then "远程图片" else alt
                let source =
                    match Uri.TryCreate(url, UriKind.Absolute) with
                    | true, uri when not (String.IsNullOrWhiteSpace uri.Host) -> uri.Host
                    | _ -> url
                let run = Run(sprintf "[图片未加载：%s · %s]" label source)
                run.Foreground <- Tokens.textFaint
                block.Inlines.Add run
                ToolTip.SetTip(block, sprintf "为保护隐私，默认不加载远程图片。来源：%s" url)
            | MdBreak -> block.Inlines.Add(LineBreak())
        block :> Control

    /// 将长字符串按字素簇（grapheme clusters / text elements）安全切片，
    /// 避免在 surrogate pair 或复杂 emoji 序列中间拆分造成畸变。
    static member SafeChunk (chunkSize: int) (str: string) : string list =
        if String.IsNullOrEmpty str || chunkSize <= 0 then
            []
        else
            let enumerator = StringInfo.GetTextElementEnumerator(str)
            let chunks = ResizeArray<string>()
            let current = StringBuilder()
            let mutable count = 0
            while enumerator.MoveNext() do
                current.Append(enumerator.GetTextElement()) |> ignore
                count <- count + 1
                if count = chunkSize then
                    chunks.Add(current.ToString())
                    current.Clear() |> ignore
                    count <- 0
            if current.Length > 0 then
                chunks.Add(current.ToString())
            chunks |> Seq.toList

    /// 链接需要能点。整段文本共用一个 TextBlock 时无法逐字命中，
    /// 因此只在段落里存在链接时，把段落拆成「文本 + 可点链接」的 WrapPanel。
    member private this.RenderInlineRow(items: MdInline list, size: float, weight: FontWeight, brush: IBrush, ?lineHeight: float, ?fontFamily: FontFamily, ?textAlignment: TextAlignment) : Control =
        let hasLink = items |> List.exists (function MdLink _ -> true | _ -> false)
        if not hasLink then
            this.RenderInlines(items, size, weight, brush, ?lineHeight = lineHeight, ?fontFamily = fontFamily, ?textAlignment = textAlignment)
        else
            let wrap = WrapPanel(Orientation = Orientation.Horizontal)
            let mutable buffer: MdInline list = []
            let flush () =
                if not (List.isEmpty buffer) then
                    let control = this.RenderInlines(List.rev buffer, size, weight, brush, ?lineHeight = lineHeight, ?fontFamily = fontFamily, ?textAlignment = textAlignment)
                    wrap.Children.Add control
                    buffer <- []
            let addLinkChunk (fullText: string) (chunk: string) (url: string) (focusable: bool) =
                let linkText =
                    TextBlock(
                        Text = chunk,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = size,
                        FontWeight = weight,
                        LineHeight = defaultArg lineHeight (ReadingRhythm.proseLineHeight size),
                        Foreground = Tokens.accent,
                        TextDecorations = TextDecorations.Underline,
                        VerticalAlignment = VerticalAlignment.Center)
                match fontFamily with
                | Some ff -> linkText.FontFamily <- ff
                | None -> ()
                let link =
                    ActionBorder(
                        Background = Brushes.Transparent,
                        Padding = Thickness 0.0,
                        Cursor = handCursor,
                        Focusable = focusable,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = linkText)
                if focusable then Avalonia.Automation.AutomationProperties.SetName(link, fullText)
                Avalonia.Automation.AutomationProperties.SetHelpText(link, url)
                Avalonia.Automation.AutomationProperties.SetControlTypeOverride(
                    link,
                    Nullable Avalonia.Automation.Peers.AutomationControlType.Hyperlink)
                ToolTip.SetTip(link, url)
                link.KeyDown.Add(fun e ->
                    if link.IsEnabled && (e.Key = Key.Enter || e.Key = Key.Space) then
                        e.Handled <- true
                        openLink url)
                Ui.onClick link (fun () -> openLink url)
                wrap.Children.Add link
            for item in items do
                match item with
                | MdLink(text, url) ->
                    flush ()
                    // WrapPanel 只能在 child 之间换行。超长 URL / hash 如果作为一个 child，
                    // 就会撑破窄屏；按小段拆成多个同 URL 的 action，几何上可自然换行。
                    if text.Length <= 40 then
                        addLinkChunk text text url true
                    else
                        let chunks = MarkdownRenderer.SafeChunk 40 text
                        for i in 0 .. chunks.Length - 1 do
                            let chunk = chunks.[i]
                            // 视觉上仍可逐段命中，但一个长链接只占一个 Tab stop，
                            // 避免键盘用户在同一 URL 上重复停留多次。
                            addLinkChunk text chunk url (i = 0)
                | other -> buffer <- other :: buffer
            flush ()
            wrap :> Control

    member private _.RenderCode(language: string, code: string) : Control =
        let block =
            SelectableTextBlock(
                FontFamily = Tokens.monoFontFamily,
                FontSize = fontSize - 1.5,
                Foreground = Tokens.codeText,
                LineHeight = ReadingRhythm.technicalLineHeight (fontSize - 1.5),
                Margin = Thickness(Tokens.blockPaddingX, Tokens.blockPaddingY),
                SelectionBrush = Tokens.accentSoft)
        let brushOf kind : IBrush =
            match kind with
            | CodeKeyword -> Tokens.codeKeyword
            | CodeString -> Tokens.codeString
            | CodeComment -> Tokens.codeComment
            | CodeNumber -> Tokens.codeNumber
            | CodeType -> Tokens.codeType
            | CodeFunction -> Tokens.codeFunction
            | CodeMeta -> Tokens.codeMeta
            | CodeVariable -> Tokens.codeVariable
            | CodeOperator -> Tokens.codeOperator
            | CodeAddition -> Tokens.codeAddition
            | CodeDeletion -> Tokens.codeDeletion
            | CodePlain -> Tokens.codeText
        for token in Highlight.tokenize allowHighlight language code do
            let run = Run token.text
            run.Foreground <- brushOf token.kind
            if token.kind = CodeComment then run.FontStyle <- FontStyle.Italic
            block.Inlines.Add run

        let scroll =
            ScrollViewer(
                Content = block,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled)

        let langLabel =
            TextBlock(
                Text = (if String.IsNullOrWhiteSpace language then "text" else language.ToLowerInvariant()),
                FontSize = Tokens.fontMicro,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.codeMuted,
                VerticalAlignment = VerticalAlignment.Center,
                LetterSpacing = 0.4)

        let headerButton (icon: IBrush -> Control) (tip: string) =
            let button = Ui.iconButton icon tip
            Ui.setSquareTarget button LayoutPolicy.inlineActionTarget
            Ui.setIcon button icon Tokens.codeMuted
            button

        let copyButton = headerButton Icons.copy "复制代码"
        copyButton.Focusable <- true
        copyButton.Cursor <- handCursor
        Avalonia.Automation.AutomationProperties.SetName(copyButton, "复制代码")
        ToolTip.SetTip(copyButton, "复制代码")

        let mutable copyTimer: DispatcherTimer option = None
        let stopTimer () =
            match copyTimer with
            | Some t ->
                t.Stop()
                copyTimer <- None
            | None -> ()

        let restoreDefaultState () =
            stopTimer ()
            Ui.setIcon copyButton Icons.copy Tokens.codeMuted
            ToolTip.SetTip(copyButton, "复制代码")
            Avalonia.Automation.AutomationProperties.SetName(copyButton, "复制代码")

        let doCopy () =
            copyText code
            stopTimer ()
            Ui.setIcon copyButton Icons.check Tokens.success
            ToolTip.SetTip(copyButton, "已复制！")
            Avalonia.Automation.AutomationProperties.SetName(copyButton, "已复制！")
            let timer = new DispatcherTimer(Interval = MotionLedger.copyConfirmationHold)
            timer.Tick.Add(fun _ ->
                if copyTimer = Some timer then
                    restoreDefaultState ())
            copyTimer <- Some timer
            timer.Start()
        Ui.onClick copyButton doCopy

        copyButton.DetachedFromVisualTree.Add(fun _ ->
            restoreDefaultState ())

        // 长行原来只能横向滚动：一行长命令要么看不全，要么读一行拖一次。
        // 折行是逐块开关，初值取自 UI 偏好。
        let wrapButton = headerButton Icons.textWrap "长行折行 / 横向滚动"
        Avalonia.Automation.AutomationProperties.SetName(wrapButton, "长行折行 / 横向滚动")
        let mutable wrapped = MarkdownRenderer.DefaultCodeWrap
        let applyWrap () =
            block.TextWrapping <- (if wrapped then TextWrapping.Wrap else TextWrapping.NoWrap)
            scroll.HorizontalScrollBarVisibility <-
                (if wrapped then ScrollBarVisibility.Disabled else ScrollBarVisibility.Auto)
            Ui.setIcon wrapButton Icons.textWrap (if wrapped then Tokens.accent else Tokens.codeMuted)
        let toggleWrap () =
            wrapped <- not wrapped
            MarkdownRenderer.DefaultCodeWrap <- wrapped
            applyWrap ()
        Ui.onClick wrapButton toggleWrap
        applyWrap ()

        let header =
            let actions = StackPanel(Orientation = Orientation.Horizontal, Spacing = 2.0, VerticalAlignment = VerticalAlignment.Center)
            actions.Children.Add wrapButton
            actions.Children.Add copyButton
            let dock = DockPanel(LastChildFill = false)
            DockPanel.SetDock(langLabel, Dock.Left)
            DockPanel.SetDock(actions, Dock.Right)
            dock.Children.Add langLabel
            dock.Children.Add actions
            Border(
                Background = Tokens.codeHeaderBg,
                BorderBrush = Tokens.codeBorder,
                BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0),
                Padding = Thickness(Tokens.blockPaddingX, Tokens.space1, Tokens.space1, Tokens.space1),
                Child = dock)

        let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        stack.Children.Add header
        stack.Children.Add scroll
        let root =
            Border(
                Background = Tokens.codeBg,
                BorderBrush = Tokens.codeBorder,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusMd,
                ClipToBounds = true,
                Margin = Thickness(0.0, ReadingRhythm.codeBlockVerticalMargin),
                Child = stack)
        root.DetachedFromVisualTree.Add(fun _ -> restoreDefaultState ())
        root
        :> Control

    member private this.RenderTable(header: MdInline list list, rows: MdInline list list list) : Control =
        let columnCount =
            (header :: rows) |> List.map List.length |> List.fold max 0
        if columnCount = 0 then
            Border() :> Control
        else
            let grid = Grid()
            // 窄表（<= 4 列）采用 1.0 Star 自然撑满阅读列宽；
            // 多列宽表（> 4 列）设置每列最小宽度（120px），并放入横向 ScrollViewer，
            // 避免在 748px 阅读宽度下被强行压成挤压错乱的细条。
            let minColumnWidth =
                match columnCount with
                | 1 | 2 -> 100.0
                | 3 -> 90.0
                | 4 -> 80.0
                | _ -> 120.0
            for _ in 1 .. columnCount do
                let col = ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star))
                col.MinWidth <- minColumnWidth
                grid.ColumnDefinitions.Add col

            let isNumericCell (items: MdInline list) =
                match items with
                | [ MdText(text, _, _, _, false) ] ->
                    let s = text.Trim()
                    if String.IsNullOrEmpty s then false
                    else
                        let clean =
                            s.TrimStart('$', '¥', '€', '£', '+', '-')
                             .TrimEnd('%')
                             .Replace(",", "")
                             .Trim()
                        match Double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture) with
                        | true, _ -> true
                        | _ -> false
                | _ -> false

            let isNumericColumn =
                Array.init columnCount (fun c ->
                    if List.isEmpty rows then false
                    else
                        rows
                        |> List.forall (fun row ->
                            if c < List.length row then
                                match row[c] with
                                | [] -> true
                                | cells -> isNumericCell cells
                            else true))

            let mutable gridRowIndex = 0
            let mutable dataRowIndex = 0
            let addRow (cells: MdInline list list) (isHeader: bool) =
                grid.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
                for columnIndex in 0 .. columnCount - 1 do
                    let content =
                        if columnIndex < List.length cells then cells[columnIndex] else []
                    let isNumeric =
                        if isHeader then
                            isNumericColumn[columnIndex] && (isNumericCell content || List.length content <= 1)
                        else
                            isNumericCell content
                    let alignment = if isNumeric then HorizontalAlignment.Right else HorizontalAlignment.Left
                    let textAlignment = if isNumeric then Some TextAlignment.Right else None
                    let cell =
                        this.RenderInlineRow(
                            content,
                            fontSize - 0.5,
                            (if isHeader then FontWeight.Medium else FontWeight.Normal),
                            (if isHeader then Tokens.text else Tokens.textMuted),
                            ?fontFamily = (if isNumeric && not isHeader then Some Tokens.monoFontFamily else None),
                            lineHeight = ReadingRhythm.proseLineHeight (fontSize - 0.5),
                            ?textAlignment = textAlignment)
                    let hasBreak = content |> List.exists (function MdBreak -> true | _ -> false)
                    cell.VerticalAlignment <-
                        if isHeader then VerticalAlignment.Center
                        elif hasBreak then VerticalAlignment.Top
                        else VerticalAlignment.Center
                    cell.HorizontalAlignment <- alignment
                    let background: IBrush =
                        if isHeader then Tokens.tableHeader
                        // 隔行底色比逐行画线更轻：长表格里横线多了会变成网格纸
                        elif dataRowIndex % 2 = 1 then Tokens.tableStripe
                        else Brushes.Transparent
                    let hasRowsBelow = not (List.isEmpty rows)
                    let host =
                        Border(
                            Padding = Thickness(Tokens.blockPaddingX, Tokens.blockPaddingY),
                            BorderBrush = Tokens.borderSoft,
                            BorderThickness = Thickness(0.0, 0.0, 0.0, (if isHeader && hasRowsBelow then 1.0 else 0.0)),
                            Background = background,
                            ClipToBounds = true,
                            UseLayoutRounding = true,
                            Child = cell)
                    Grid.SetRow(host, gridRowIndex)
                    Grid.SetColumn(host, columnIndex)
                    grid.Children.Add host
                gridRowIndex <- gridRowIndex + 1
                if not isHeader then
                    dataRowIndex <- dataRowIndex + 1
            if not (List.isEmpty header) then addRow header true
            for row in rows do addRow row false
            let tableContent: Control =
                if columnCount > 4 then
                    ScrollViewer(
                        Content = grid,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled)
                    :> Control
                else
                    grid :> Control

            Border(
                BorderBrush = Tokens.border,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusMd,
                ClipToBounds = true,
                UseLayoutRounding = true,
                Margin = Thickness(0.0, ReadingRhythm.tableVerticalMargin),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = tableContent)
            :> Control

    member private this.RenderMath(tex: string) : Control list =
        match MathRender.tryBlock (fontSize + 1.0) tex with
        | Some control ->
            control.HorizontalAlignment <- HorizontalAlignment.Center
            control.Margin <- Thickness(0.0, Tokens.space3)
            [ control ]
        | None -> [ this.RenderCode("tex", tex) ]

    member private this.RenderBlock(block: MdBlock, ?inQuote: bool) : Control list =
        let insideQuote = defaultArg inQuote false
        let textColor = if insideQuote then Tokens.textMuted else Tokens.text
        let textLineHeight = if insideQuote then ReadingRhythm.secondaryLineHeight fontSize else ReadingRhythm.proseLineHeight fontSize
        match block with
        | MdMathBlock tex -> this.RenderMath tex
        // Markdig 把独立成段的 $$…$$ 也解析成行内公式；这里还原成块，否则不会居中
        | MdParagraph [ MdMath(tex, true) ] -> this.RenderMath tex
        | MdHeading(level, items) ->
            let size = headingSize level
            let control = this.RenderInlineRow(items, size, FontWeight.Medium, textColor)
            control.Margin <- Thickness(0.0, ReadingRhythm.headingBefore level, 0.0, ReadingRhythm.headingAfter level)
            [ control ]
        | MdParagraph items ->
            let control = this.RenderInlineRow(items, fontSize, FontWeight.Normal, textColor, lineHeight = textLineHeight)
            control.Margin <- Thickness(0.0, 0.0, 0.0, ReadingRhythm.paragraphGap)
            [ control ]
        | MdList(ordered, items) ->
            let stack =
                StackPanel(
                    Orientation = Orientation.Vertical,
                    Spacing = ReadingRhythm.listItemGap,
                    Margin = Thickness(0.0, 0.0, 0.0, ReadingRhythm.listBlockGap))
            for (depth, bullet, content) in items do
                // 有序号用文字（要显示数字），无序用绘制圆点：
                // 字形圆点在内嵌字体里又小又偏上，与上方数字标记明显不齐。
                let marker: Control =
                    if ordered then
                        TextBlock(
                            Text = bullet,
                            FontSize = fontSize - 0.5,
                            Foreground = Tokens.textMuted,
                            MinWidth = ReadingRhythm.listMarkerWidth,
                            TextAlignment = TextAlignment.Right,
                            Margin = Thickness(float depth * ReadingRhythm.listIndentStep, 1.0, Tokens.space2, 0.0),
                            VerticalAlignment = VerticalAlignment.Top)
                        :> Control
                    else
                        let dot =
                            Border(
                                Width = 4.0,
                                Height = 4.0,
                                CornerRadius = CornerRadius 2.0,
                                Background = Tokens.textMuted,
                                VerticalAlignment = VerticalAlignment.Center)
                        Border(
                            Width = ReadingRhythm.listMarkerWidth + float depth * ReadingRhythm.listIndentStep,
                            Padding = Thickness(0.0, 0.0, Tokens.space2, 0.0),
                            Margin = Thickness(0.0, fontSize * 0.55, 0.0, 0.0),
                            VerticalAlignment = VerticalAlignment.Top,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Child = dot)
                        :> Control
                let body = this.RenderInlineRow(content, fontSize, FontWeight.Normal, textColor, lineHeight = textLineHeight)
                let row = DockPanel()
                DockPanel.SetDock(marker, Dock.Left)
                row.Children.Add marker
                row.Children.Add body
                stack.Children.Add row
            [ stack :> Control ]
        | MdTask items ->
            let stack =
                StackPanel(
                    Orientation = Orientation.Vertical,
                    Spacing = ReadingRhythm.listItemGap,
                    Margin = Thickness(0.0, 0.0, 0.0, ReadingRhythm.listBlockGap))
            for (isChecked, content) in items do
                let box =
                    Border(
                        Width = 14.0,
                        Height = 14.0,
                        CornerRadius = CornerRadius Tokens.radiusXs,
                        BorderBrush = (if isChecked then Tokens.accent else Tokens.line),
                        BorderThickness = Thickness 1.4,
                        Background = (if isChecked then Tokens.accent :> IBrush else Brushes.Transparent :> IBrush),
                        Margin = Thickness(0.0, Tokens.iconBaselineNudge, Tokens.space2, 0.0),
                        VerticalAlignment = VerticalAlignment.Top)
                if isChecked then
                    let mark = Icons.check Tokens.textOnAccent
                    mark.Width <- 10.0
                    mark.Height <- 10.0
                    box.Child <- mark
                let body = this.RenderInlineRow(content, fontSize, FontWeight.Normal, (if isChecked then Tokens.textMuted else textColor), lineHeight = textLineHeight)
                let row = DockPanel()
                DockPanel.SetDock(box, Dock.Left)
                row.Children.Add box
                row.Children.Add body
                stack.Children.Add row
            [ stack :> Control ]
        | MdQuote inner ->
            let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
            for child in inner do
                for control in this.RenderBlock(child, inQuote = true) do
                    stack.Children.Add control
            // 引用块首子项清空顶外边距，末子项清空底外边距，保证内部留白紧凑一致
            if stack.Children.Count > 0 then
                let first = stack.Children[0]
                let fm = first.Margin
                first.Margin <- Thickness(fm.Left, 0.0, fm.Right, fm.Bottom)
                let last = stack.Children[stack.Children.Count - 1]
                let lm = last.Margin
                last.Margin <- Thickness(lm.Left, lm.Top, lm.Right, 0.0)
            [ Border(
                  Background = Tokens.hover,
                  CornerRadius = CornerRadius(0.0, Tokens.radiusSm, Tokens.radiusSm, 0.0),
                  BorderBrush = Tokens.accent,
                  BorderThickness = Thickness(3.0, 0.0, 0.0, 0.0),
                  Padding = Thickness(Tokens.space3, Tokens.space2, Tokens.space3, Tokens.space2),
                  Margin = Thickness(0.0, (if insideQuote then Tokens.space2 else ReadingRhythm.quoteTopGap), 0.0, (if insideQuote then Tokens.space2 else ReadingRhythm.quoteBottomGap)),
                  Child = stack)
              :> Control ]
        | MdCode(language, code) -> [ this.RenderCode(language, code) ]
        | MdRule ->
            [ Border(
                  Height = 1.0,
                  Background = Tokens.borderSoft,
                  Margin = Thickness(0.0, ReadingRhythm.headingBefore 3, 0.0, ReadingRhythm.headingBefore 3),
                  HorizontalAlignment = HorizontalAlignment.Stretch)
              :> Control ]
        | MdTable(header, rows) -> [ this.RenderTable(header, rows) ]

    /// 渲染整段 Markdown 到一个纵向面板。
    member this.Render(blocks: MdBlock list) : Control =
        let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        for block in blocks do
            for control in this.RenderBlock block do
                stack.Children.Add control
        // 末块的下边距会在气泡里留下多余空隙
        if stack.Children.Count > 0 then
            let last = stack.Children[stack.Children.Count - 1]
            let m = last.Margin
            last.Margin <- Thickness(m.Left, m.Top, m.Right, 0.0)
        stack :> Control

    member this.RenderText(raw: string) : Control = this.Render(Markdown.parse raw)
