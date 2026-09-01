namespace Wanxiang.UI

open System
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

    let headingSize (level: int) =
        match level with
        | 1 -> fontSize + 10.0
        | 2 -> fontSize + 6.5
        | 3 -> fontSize + 4.0
        | 4 -> fontSize + 2.0
        | _ -> fontSize + 1.0

    /// 代码块折行的当前默认值。来自 UI 偏好，块内的切换也会更新它，
    /// 于是同一屏里后续渲染的代码块跟着用户刚做的选择。
    static member val DefaultCodeWrap = false with get, set

    /// 行内片段渲染成一个可选中的文本块。
    member private _.RenderInlines(items: MdInline list, size: float, weight: FontWeight, brush: IBrush) : Control =
        let block =
            SelectableTextBlock(
                TextWrapping = TextWrapping.Wrap,
                FontSize = size,
                FontWeight = weight,
                Foreground = brush,
                LineHeight = size * 1.65,
                SelectionBrush = Tokens.accentSoft)
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
                block.Inlines.Add run
            | MdLink(text, url) ->
                let link = Run text
                link.Foreground <- Tokens.accent
                link.TextDecorations <- TextDecorations.Underline
                block.Inlines.Add link
                let hit =
                    TextBlock(
                        Text = "",
                        Cursor = handCursor,
                        Width = 0.0,
                        Height = 0.0)
                ignore hit
                block.PointerReleased.Add(fun _ -> ())
                ToolTip.SetTip(block, url)
            | MdMath(tex, display) ->
                match MathRender.tryInline (if display then size + 1.0 else size) (size * 1.65) tex with
                | Some visual -> block.Inlines.Add(MathRender.inlineContainer visual)
                | None ->
                    // 没有脚本后端或 TeX 有语法错：按行内代码呈现原式，
                    // 至少让读者看得见作者写了什么
                    let run = Run tex
                    run.FontFamily <- Tokens.monoFontFamily
                    run.Foreground <- Tokens.text
                    run.Background <- Tokens.inlineCodeBg
                    run.FontSize <- size - 0.5
                    block.Inlines.Add run
            | MdImage(alt, url) ->
                let run = Run(sprintf "[%s]" alt)
                run.Foreground <- Tokens.textFaint
                block.Inlines.Add run
                ToolTip.SetTip(block, url)
            | MdBreak -> block.Inlines.Add(LineBreak())
        block :> Control

    /// 链接需要能点。整段文本共用一个 TextBlock 时无法逐字命中，
    /// 因此只在段落里存在链接时，把段落拆成「文本 + 可点链接」的 WrapPanel。
    member private this.RenderInlineRow(items: MdInline list, size: float, brush: IBrush) : Control =
        let hasLink = items |> List.exists (function MdLink _ -> true | _ -> false)
        if not hasLink then
            this.RenderInlines(items, size, FontWeight.Normal, brush)
        else
            let wrap = WrapPanel(Orientation = Orientation.Horizontal)
            let mutable buffer: MdInline list = []
            let flush () =
                if not (List.isEmpty buffer) then
                    let control = this.RenderInlines(List.rev buffer, size, FontWeight.Normal, brush)
                    wrap.Children.Add control
                    buffer <- []
            for item in items do
                match item with
                | MdLink(text, url) ->
                    flush ()
                    let link =
                        TextBlock(
                            Text = text,
                            FontSize = size,
                            Foreground = Tokens.accent,
                            TextDecorations = TextDecorations.Underline,
                            Cursor = handCursor,
                            VerticalAlignment = VerticalAlignment.Center)
                    ToolTip.SetTip(link, url)
                    link.PointerReleased.Add(fun e ->
                        e.Handled <- true
                        openLink url)
                    wrap.Children.Add link
                | other -> buffer <- other :: buffer
            flush ()
            wrap :> Control

    member private _.RenderCode(language: string, code: string) : Control =
        let block =
            SelectableTextBlock(
                FontFamily = Tokens.monoFontFamily,
                FontSize = fontSize - 1.5,
                Foreground = Tokens.codeText,
                LineHeight = (fontSize - 1.5) * 1.6,
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
            button.Width <- 26.0
            button.Height <- 26.0
            button.MinWidth <- 26.0
            button.MinHeight <- 26.0
            Ui.setIcon button icon Tokens.codeMuted
            button

        let copyButton = headerButton Icons.copy "复制代码"
        let doCopy () =
            copyText code
            Ui.setIcon copyButton Icons.check Tokens.success
            ToolTip.SetTip(copyButton, "已复制！")
            let timer = new DispatcherTimer(Interval = TimeSpan.FromMilliseconds 1500.0)
            timer.Tick.Add(fun _ ->
                timer.Stop()
                Ui.setIcon copyButton Icons.copy Tokens.codeMuted
                ToolTip.SetTip(copyButton, "复制代码"))
            timer.Start()
        copyButton.PointerReleased.Add(fun e ->
            e.Handled <- true
            doCopy ())
        copyButton.KeyDown.Add(fun e ->
            if e.Key = Key.Enter || e.Key = Key.Space then
                e.Handled <- true
                doCopy ())

        // 长行原来只能横向滚动：一行长命令要么看不全，要么读一行拖一次。
        // 折行是逐块开关，初值取自 UI 偏好。
        let wrapButton = headerButton Icons.textWrap "长行折行 / 横向滚动"
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
        wrapButton.PointerReleased.Add(fun e ->
            e.Handled <- true
            toggleWrap ())
        wrapButton.KeyDown.Add(fun e ->
            if e.Key = Key.Enter || e.Key = Key.Space then
                e.Handled <- true
                toggleWrap ())
        applyWrap ()

        let header =
            let actions = StackPanel(Orientation = Orientation.Horizontal, Spacing = 2.0)
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
                Padding = Thickness(Tokens.blockPaddingX, 4.0, Tokens.space2, 4.0),
                Child = dock)

        let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
        stack.Children.Add header
        stack.Children.Add scroll
        Border(
            Background = Tokens.codeBg,
            BorderBrush = Tokens.codeBorder,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusMd,
            ClipToBounds = true,
            Margin = Thickness(0.0, Tokens.space2),
            Child = stack)
        :> Control

    member private this.RenderTable(header: MdInline list list, rows: MdInline list list list) : Control =
        let columnCount =
            (header :: rows) |> List.map List.length |> List.fold max 0
        if columnCount = 0 then
            Border() :> Control
        else
            let grid = Grid()
            // 列宽必须是 star：Auto 会让表格收缩到内容宽度，
            // 而外框是拉伸的，于是框内右侧留下一条空白带。
            for _ in 1 .. columnCount do
                grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
            let mutable rowIndex = 0
            let addRow (cells: MdInline list list) (isHeader: bool) =
                grid.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
                for columnIndex in 0 .. columnCount - 1 do
                    let content =
                        if columnIndex < List.length cells then cells[columnIndex] else []
                    let cell =
                        this.RenderInlines(
                            content,
                            fontSize - 0.5,
                            (if isHeader then FontWeight.Medium else FontWeight.Normal),
                            (if isHeader then Tokens.text else Tokens.textMuted))
                    let background: IBrush =
                        if isHeader then Tokens.tableHeader
                        // 隔行底色比逐行画线更轻：长表格里横线多了会变成网格纸
                        elif rowIndex % 2 = 0 then Tokens.tableStripe
                        else Brushes.Transparent
                    let host =
                        Border(
                            Padding = Thickness(Tokens.blockPaddingX, Tokens.blockPaddingY),
                            BorderBrush = Tokens.borderSoft,
                            BorderThickness = Thickness(0.0, 0.0, 0.0, (if isHeader then 1.0 else 0.0)),
                            Background = background,
                            Child = cell)
                    Grid.SetRow(host, rowIndex)
                    Grid.SetColumn(host, columnIndex)
                    grid.Children.Add host
                rowIndex <- rowIndex + 1
            if not (List.isEmpty header) then addRow header true
            for row in rows do addRow row false
            // 不再套横向 ScrollViewer：那会按内容宽度测量子元素，star 列会塌回内容宽。
            // 单元格文字自动换行即可，表格始终与阅读列同宽。
            Border(
                BorderBrush = Tokens.border,
                BorderThickness = Thickness 1.0,
                CornerRadius = CornerRadius Tokens.radiusMd,
                ClipToBounds = true,
                Margin = Thickness(0.0, Tokens.space2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = grid)
            :> Control

    member private this.RenderMath(tex: string) : Control list =
        match MathRender.tryBlock (fontSize + 1.0) tex with
        | Some control -> [ control ]
        | None -> [ this.RenderCode("tex", tex) ]

    member private this.RenderBlock(block: MdBlock) : Control list =
        match block with
        | MdMathBlock tex -> this.RenderMath tex
        // Markdig 把独立成段的 $$…$$ 也解析成行内公式；这里还原成块，否则不会居中
        | MdParagraph [ MdMath(tex, true) ] -> this.RenderMath tex
        | MdHeading(level, items) ->
            let size = headingSize level
            let control = this.RenderInlines(items, size, FontWeight.Medium, Tokens.text)
            control.Margin <- Thickness(0.0, (if level <= 2 then Tokens.space4 else Tokens.space3), 0.0, Tokens.space1)
            [ control ]
        | MdParagraph items ->
            let control = this.RenderInlineRow(items, fontSize, Tokens.text)
            control.Margin <- Thickness(0.0, 0.0, 0.0, Tokens.space2)
            [ control ]
        | MdList(ordered, items) ->
            let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 4.0, Margin = Thickness(0.0, 0.0, 0.0, Tokens.space2))
            for (depth, bullet, content) in items do
                // 有序号用文字（要显示数字），无序用绘制圆点：
                // 字形圆点在内嵌字体里又小又偏上，与上方数字标记明显不齐。
                let marker: Control =
                    if ordered then
                        TextBlock(
                            Text = bullet,
                            FontSize = fontSize - 0.5,
                            Foreground = Tokens.textMuted,
                            MinWidth = 20.0,
                            TextAlignment = TextAlignment.Right,
                            Margin = Thickness(float depth * 16.0, 1.0, Tokens.space2, 0.0),
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
                            Width = 20.0 + float depth * 16.0,
                            Padding = Thickness(0.0, 0.0, Tokens.space2, 0.0),
                            Margin = Thickness(0.0, fontSize * 0.55, 0.0, 0.0),
                            VerticalAlignment = VerticalAlignment.Top,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Child = dot)
                        :> Control
                let body = this.RenderInlineRow(content, fontSize, Tokens.text)
                let row = DockPanel()
                DockPanel.SetDock(marker, Dock.Left)
                row.Children.Add marker
                row.Children.Add body
                stack.Children.Add row
            [ stack :> Control ]
        | MdTask items ->
            let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 4.0, Margin = Thickness(0.0, 0.0, 0.0, Tokens.space2))
            for (isChecked, content) in items do
                let box =
                    Border(
                        Width = 14.0,
                        Height = 14.0,
                        CornerRadius = CornerRadius Tokens.radiusXs,
                        BorderBrush = (if isChecked then Tokens.accent else Tokens.line),
                        BorderThickness = Thickness 1.4,
                        Background = (if isChecked then Tokens.accent :> IBrush else Brushes.Transparent :> IBrush),
                        Margin = Thickness(0.0, 2.0, Tokens.space2, 0.0),
                        VerticalAlignment = VerticalAlignment.Top)
                if isChecked then
                    let mark = Icons.check Tokens.textOnAccent
                    mark.Width <- 10.0
                    mark.Height <- 10.0
                    box.Child <- mark
                let body = this.RenderInlineRow(content, fontSize, (if isChecked then Tokens.textMuted else Tokens.text))
                let row = DockPanel()
                DockPanel.SetDock(box, Dock.Left)
                row.Children.Add box
                row.Children.Add body
                stack.Children.Add row
            [ stack :> Control ]
        | MdQuote inner ->
            let stack = StackPanel(Orientation = Orientation.Vertical, Spacing = 0.0)
            for child in inner do
                for control in this.RenderBlock child do
                    stack.Children.Add control
            [ Border(
                  BorderBrush = Tokens.accentSoft,
                  BorderThickness = Thickness(3.0, 0.0, 0.0, 0.0),
                  Padding = Thickness(Tokens.space4, Tokens.space1, 0.0, 0.0),
                  Margin = Thickness(0.0, Tokens.space1, 0.0, Tokens.space3),
                  Child = stack)
              :> Control ]
        | MdCode(language, code) -> [ this.RenderCode(language, code) ]
        | MdRule ->
            [ Border(
                  Height = 1.0,
                  Background = Tokens.borderSoft,
                  Margin = Thickness(0.0, Tokens.space4),
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
