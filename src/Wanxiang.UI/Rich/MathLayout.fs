namespace Wanxiang.UI

open System
open System.Collections.Concurrent
open System.Globalization
open Avalonia
open Avalonia.Media

/// 一条已定位的绘制图元。坐标相对公式盒的**基线左端**，y 向下为正。
///
/// 字形除了保留整形结果，还带着选中的 `Typeface`：字族选错是这套渲染
/// 最容易犯的错（数字该竖排、变量该斜排），必须能在测试里断言。
type MathItem =
    | MathGlyph of text: string * shaped: FormattedText * typeface: Typeface * topLeft: Point
    | MathRule of rect: Rect
    | MathPath of geometry: Geometry * transform: Matrix * clip: Rect

/// 一个排好的盒子：宽度，以及基线上下各占多少。
type MathBox =
    { width: float
      above: float
      below: float
      items: MathItem list }

/// 把 KaTeX 的 HTML 盒模型摆成 Avalonia 图元。
///
/// 纵向位置**全部**来自 KaTeX 算好的 em 数值（strut 的 height/vertical-align、
/// vlist 里的 top + pstrut），本模块不重新推导 TeX 的排版规则；
/// 只有横向推进需要真实字形宽度，那来自内嵌的 KaTeX 字体本身——
/// 与 KaTeX 假设的度量同源，所以宽度天然吻合。
module MathLayout =

    /// `.katex { font-size: 1.21em }`
    [<Literal>]
    let katexFontFactor = 1.21

    /// `.nulldelimiter { width: 0.12em }`：CSS 里的常量，HTML 里不带 style
    [<Literal>]
    let private nullDelimiterEm = 0.12

    let private families = ConcurrentDictionary<string, FontFamily>()

    let private familyOf (name: string) =
        // 与正文字体同一个目录 = 同一个内嵌字体集合。分成两个目录时
        // 桌面端正常、浏览器端解析不到，公式里的 − 与 ± 会变成豆腐块。
        families.GetOrAdd(name, fun key -> FontFamily $"avares://Wanxiang.UI/Assets/Fonts/#{key}")

    type Context =
        { size: float
          family: string
          brush: IBrush
          /// 要撑满父盒的元素（分数线、根号的 SVG）靠这个拿到父宽
          fill: float
          /// 分式与带上下限的算符要把堆叠的各层居中
          centerStack: bool }

    let private hasClass name (classes: string list) = List.contains name classes

    /// class → KaTeX 字族。KaTeX 用字族区分数学字母的语义（斜体变量、
    /// 黑板粗体的集合符号、花体……），所以这张表决定了公式认不认得出来。
    ///
    /// 字族名把样式包含进去（`KaTeX_MathItalic` 而不是 `KaTeX_Math` + Italic），
    /// 于是永远不必让排版栈按样式在字族内挑成员——它在浏览器端会挑错，
    /// 挑到缺减号与正负号的 `KaTeX_Main-Italic`。斜体由字体本身提供，
    /// 请求里一律 Normal，也就不会再有合成斜体。
    let private applyFont (classes: string list) (ctx: Context) =
        let mutable ctx = ctx
        let face name = ctx <- { ctx with family = name }
        for cls in classes do
            match cls with
            | "mathnormal" -> face "KaTeX_MathItalic"
            | "mathit" -> face "KaTeX_MainItalic"
            | "mathrm" | "mainrm" | "text" -> face "KaTeX_Main"
            | "mathbf" -> face "KaTeX_MainBold"
            | "boldsymbol" -> face "KaTeX_MathBoldItalic"
            | "mathbb" | "amsrm" -> face "KaTeX_AMS"
            | "mathcal" -> face "KaTeX_Caligraphic"
            | "mathscr" -> face "KaTeX_Script"
            | "mathfrak" -> face "KaTeX_Fraktur"
            | "mathsf" -> face "KaTeX_SansSerif"
            | "mathtt" -> face "KaTeX_Typewriter"
            // KaTeX 的字体里没有汉字，`\text{中文}` 必须回落到正文字体
            | "cjk_fallback" -> face ""
            | "small-op" -> face "KaTeX_Size1"
            | "large-op" -> face "KaTeX_Size2"
            | _ -> ()
        // 可伸缩括号：`delimsizing size1..4` 里的 sizeN 指字族而非缩放
        if hasClass "delimsizing" classes then
            for cls in classes do
                match cls with
                | "size1" | "size2" | "size3" | "size4" -> face ("KaTeX_Size" + cls.Substring 4)
                | _ -> ()
        ctx

    let private typefaceOf (ctx: Context) =
        let family = if String.IsNullOrEmpty ctx.family then Tokens.fontFamily else familyOf ctx.family
        Typeface family

    let private empty = { width = 0.0; above = 0.0; below = 0.0; items = [] }

    let private shift (dx: float) (dy: float) (items: MathItem list) =
        if dx = 0.0 && dy = 0.0 then items
        else
            items
            |> List.map (function
                | MathGlyph(text, shaped, typeface, at) ->
                    MathGlyph(text, shaped, typeface, Point(at.X + dx, at.Y + dy))
                | MathRule rect -> MathRule(rect.Translate(Vector(dx, dy)))
                | MathPath(geometry, transform, clip) ->
                    MathPath(geometry, transform * Matrix.CreateTranslation(dx, dy), clip.Translate(Vector(dx, dy))))

    /// 横向依次摆放。CSS 的 inline-block 语义：宽度含左右外边距。
    let private row (boxes: MathBox list) =
        let mutable x = 0.0
        let mutable above = 0.0
        let mutable below = 0.0
        let items = ResizeArray<MathItem>()
        for box in boxes do
            items.AddRange(shift x 0.0 box.items)
            x <- x + box.width
            above <- max above box.above
            below <- max below box.below
        { width = x; above = above; below = below; items = List.ofSeq items }

    let private childrenOf node =
        match node with
        | KElement(_, _, _, children) -> children
        | KText _ -> []

    let private classesOf node =
        match node with
        | KElement(_, classes, _, _) -> classes
        | KText _ -> []

    let private styleOf node =
        match node with
        | KElement(_, _, style, _) -> style
        | KText _ -> Map.empty

    let private findClass name nodes = nodes |> List.tryFind (fun n -> hasClass name (classesOf n))

    let rec private layout (ctx: Context) (node: KNode) : MathBox =
        match node with
        | KText text ->
            // 零宽空格只是 vlist 的表格撑高用，画出来没有意义
            let cleaned = text.Replace("\u200b", "")
            if cleaned.Length = 0 then empty
            else
                let typeface = typefaceOf ctx
                let shaped =
                    FormattedText(
                        cleaned,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        ctx.size,
                        ctx.brush)
                { width = shaped.WidthIncludingTrailingWhitespace
                  above = shaped.Baseline
                  below = max 0.0 (shaped.Height - shaped.Baseline)
                  items = [ MathGlyph(cleaned, shaped, typeface, Point(0.0, -shaped.Baseline)) ] }

        | KElement(tag, classes, style, children) ->
            if hasClass "katex-mathml" classes || hasClass "vlist-s" classes then empty
            elif tag = "svg" then layoutSvg ctx style children
            elif tag = "path" then empty
            else

            let inner =
                let scaled = { ctx with size = ctx.size * KatexHtml.fontScaleOf classes }
                // mfrac / op-limits 与它的 vlist 之间可能隔着若干层，所以要往下传；
                // 由 layoutVlist 消费掉后清零，不会再往更深处传染
                let staged =
                    { scaled with
                        centerStack =
                            ctx.centerStack || hasClass "mfrac" classes || hasClass "op-limits" classes }
                applyFont classes staged

            let em key = KatexHtml.emOf style key |> Option.map (fun v -> v * inner.size)

            let content =
                if hasClass "pstrut" classes then
                    // pstrut 只是给 vlist 定基线的垫片，本身不占可见空间
                    empty
                elif hasClass "katex-strut" classes then
                    let total = defaultArg (em "height") 0.0
                    let depth = defaultArg (em "vertical-align") 0.0 |> abs
                    { empty with above = total - depth; below = depth }
                elif hasClass "frac-line" classes || hasClass "overline-line" classes then
                    // 分数线／上划线撑满父盒；粗细来自 border-bottom-width
                    let thickness = defaultArg (em "border-bottom-width") 1.0 |> max 0.5
                    { width = inner.fill
                      above = thickness
                      below = 0.0
                      items = [ MathRule(Rect(0.0, -thickness, inner.fill, thickness)) ] }
                elif hasClass "nulldelimiter" classes then
                    { empty with width = nullDelimiterEm * inner.size }
                elif hasClass "vlist-t" classes then
                    layoutVlist inner classes children
                elif hasClass "hide-tail" classes then
                    // 根号：`.hide-tail { width:100%; overflow:hidden }`。SVG 被拉到
                    // 整条根号的宽度，右端那截长尾正是被开方式上方的横线。
                    let width = max (defaultArg (em "min-width") 0.0) inner.fill
                    let height = defaultArg (em "height") 0.0
                    let laid = children |> List.map (layout { inner with fill = width }) |> row
                    { laid with width = width; above = height; below = 0.0 }
                elif List.isEmpty children then
                    // 纯占位（arraycolsep / 根号给被开方式让出的位置）
                    let width =
                        [ em "width"; em "min-width"; em "padding-left" ]
                        |> List.tryPick id
                        |> Option.defaultValue 0.0
                    { empty with width = width; above = defaultArg (em "height") 0.0 }
                else
                    let declared = em "width"
                    // 根号的被开方式靠 padding-left 让开根号本身的位置
                    let padLeft = defaultArg (em "padding-left") 0.0
                    let laid =
                        children |> List.map (layout { inner with fill = defaultArg declared inner.fill }) |> row
                    let padded =
                        { laid with
                            width = padLeft + laid.width
                            items = shift padLeft 0.0 laid.items }
                    match declared with
                    | Some w -> { padded with width = w }
                    | None -> padded

            let marginLeft = defaultArg (em "margin-left") 0.0
            let marginRight = defaultArg (em "margin-right") 0.0
            { content with
                width = marginLeft + content.width + marginRight
                items = shift marginLeft 0.0 content.items }

    /// vlist：KaTeX 用「零高块 + pstrut 垫片 + top 偏移」堆叠上下标与分式。
    /// 某个子块的基线偏移 = pstrut.height + top（负值向上）。
    and private layoutVlist (ctx: Context) (classes: string list) (children: KNode list) : MathBox =
        let rows = children |> List.filter (fun n -> hasClass "vlist-r" (classesOf n))
        match rows |> List.tryHead |> Option.bind (fun r -> findClass "vlist" (childrenOf r)) with
        | None -> empty
        | Some vlist ->
            let above = defaultArg (KatexHtml.emOf (styleOf vlist) "height") 0.0 * ctx.size
            let below =
                if hasClass "vlist-t2" classes then
                    rows
                    |> List.tryItem 1
                    |> Option.bind (fun r -> findClass "vlist" (childrenOf r))
                    |> Option.bind (fun v -> KatexHtml.emOf (styleOf v) "height")
                    |> Option.defaultValue 0.0
                    |> (*) ctx.size
                else 0.0

            let stacked =
                childrenOf vlist
                |> List.filter (fun n -> not (hasClass "vlist-s" (classesOf n)))
                |> List.map (fun child ->
                    let style = styleOf child
                    let top = defaultArg (KatexHtml.emOf style "top") 0.0
                    let pstrut =
                        childrenOf child
                        |> findClass "pstrut"
                        |> Option.bind (fun p -> KatexHtml.emOf (styleOf p) "height")
                        |> Option.defaultValue 0.0
                    let body = childrenOf child |> List.filter (fun n -> not (hasClass "pstrut" (classesOf n)))
                    let margin = defaultArg (KatexHtml.emOf style "margin-right") 0.0 * ctx.size
                    (pstrut + top) * ctx.size, margin, body)

            // 先量一遍拿到 vlist 宽度，分数线与根号横线才知道该有多长；再按这个宽度摆一遍。
            // 量的时候必须把 fill 清零：分数线与根号横线是「撑满父盒」的元素，
            // 带着外层宽度参与测量，量出的栏宽就会被自己再撑大一轮，
            // 表现为整条公式向左右各溢出半个身位。
            let stackCtx = { ctx with centerStack = false; fill = 0.0 }
            let widthOf (body: KNode list) = (body |> List.map (layout stackCtx) |> row).width
            let contentWidth = stacked |> List.fold (fun acc (_, m, body) -> max acc (widthOf body + m)) 0.0
            let filled = { stackCtx with fill = contentWidth }

            let items =
                stacked
                |> List.collect (fun (dy, _, body) ->
                    let laid = body |> List.map (layout filled) |> row
                    // 分式的分子分母、算符的上下限都要相对整栏居中
                    let dx = if ctx.centerStack then (contentWidth - laid.width) / 2.0 else 0.0
                    shift dx dy laid.items)

            { width = contentWidth; above = above; below = below; items = items }

    /// 根号与可伸缩括号是 SVG 画的。按 viewBox 等比缩放到声明高度，
    /// 溢出交给裁剪——这正是 KaTeX 用 `preserveAspectRatio="xMinYMin slice"` 的效果。
    and private layoutSvg (ctx: Context) (style: Map<string, string>) (children: KNode list) : MathBox =
        try
            let viewBox =
                match style.TryFind "-viewbox" with
                | Some raw ->
                    let parts =
                        raw.Split([| ' '; ',' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.choose (fun p ->
                            match Double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture) with
                            | true, v -> Some v
                            | _ -> None)
                    if parts.Length = 4 then Some parts[3] else None
                | None -> None
            let height = defaultArg (KatexHtml.emOf style "height" |> Option.map ((*) ctx.size)) 0.0
            let path =
                children
                |> List.tryPick (function
                    | KElement("path", _, pathStyle, _) -> pathStyle.TryFind "-d"
                    | _ -> None)
            match viewBox, path with
            | Some vbHeight, Some d when vbHeight > 0.0 && height > 0.0 && ctx.fill > 0.0 ->
                let scale = height / vbHeight
                // SVG 的 fill-rule 默认 nonzero，而 Avalonia 路径语法默认 evenodd。
                // 不加 F1，根号那条长横线会被相交的根号轮廓异或掉——
                // 结果是只剩一个勾，被开方式的范围完全看不出来。
                let geometry = Geometry.Parse("F1 " + d)
                let transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(0.0, -height)
                { width = 0.0
                  above = height
                  below = 0.0
                  items = [ MathPath(geometry, transform, Rect(0.0, -height, ctx.fill, height)) ] }
            | _ -> { empty with above = height }
        with _ -> empty

    /// 排一整条公式。KaTeX 的可见子树由若干 `katex-base` 横向拼成。
    ///
    /// 失败带原因返回：静默 None 会让「公式排不出来」变成无从下手的故障。
    let tryRender (brush: IBrush) (baseFontSize: float) (html: string) : Result<MathBox, string> =
        try
            let nodes = KatexHtml.parse html
            match KatexHtml.visualSubtree nodes with
            | None -> Error "找不到 katex-html 子树"
            | Some visual ->
                let ctx =
                    { size = baseFontSize * katexFontFactor
                      family = "KaTeX_Main"
                      brush = brush
                      fill = 0.0
                      centerStack = false }
                let box = childrenOf visual |> List.map (layout ctx) |> row
                if box.width <= 0.0 || box.above + box.below <= 0.0 then
                    Error $"排版结果尺寸为零（w=%.2f{box.width} h=%.2f{box.above + box.below}）"
                else Ok box
        with ex -> Error $"{ex.GetType().Name}: {ex.Message}"

    let render (brush: IBrush) (baseFontSize: float) (html: string) : MathBox option =
        match tryRender brush baseFontSize html with
        | Ok box -> Some box
        | Error _ -> None
