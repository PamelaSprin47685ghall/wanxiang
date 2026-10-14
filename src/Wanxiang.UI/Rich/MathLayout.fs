namespace Wanxiang.UI

open System
open System.Collections.Concurrent
open System.Globalization
open Avalonia
open Avalonia.Media

/// 一条已转成 Avalonia 类型的绘制图元。坐标相对公式盒的**基线左端**，y 向下为正。
///
/// 字形保留整形结果和选中的 `Typeface`：字族选错（数字该竖排、变量该斜排）
/// 必须能在测试里断言。
type MathDrawItem =
    | MathGlyph of text: string * shaped: FormattedText * typeface: Typeface * topLeft: Point
    | MathRule of rect: Rect
    | MathPath of geometry: Geometry * transform: Matrix * clip: Rect

/// 排好的公式：宽度，以及基线上下各占多少。
type MathDrawing =
    { width: float
      above: float
      below: float
      items: MathDrawItem list }

/// 公式排版由 Wanxiang2.MathTypeset 完成；这里只提供 FormattedText 量尺，
/// 并把与界面框架无关的图元转成 Avalonia 图元。
module MathLayout =

    let katexFontFactor = MathTypesetter.KatexFontFactor

    let private families = ConcurrentDictionary<string, FontFamily>()

    let private typefaceOf (family: string) =
        if String.IsNullOrEmpty family then Typeface Tokens.fontFamily
        else
            // 与正文字体同一个目录 = 同一个内嵌字体集合。分成两个目录时
            // 桌面端正常、浏览器端解析不到，公式里的 − 与 ± 会变成豆腐块。
            Typeface(families.GetOrAdd(family, fun key -> FontFamily $"avares://Wanxiang.UI/Assets/Fonts/#{key}"))

    let private shape (text: string) (family: string) (size: float) (brush: IBrush) =
        let typeface = typefaceOf family
        FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush), typeface

    /// 量尺与绘制用同一套 FormattedText，宽度与绘制结果同源。
    let private meter =
        { new IGlyphMeter with
            member _.Measure(text, family, size) =
                let shaped, _ = shape text family size Brushes.Black
                { width = shaped.WidthIncludingTrailingWhitespace
                  baseline = shaped.Baseline
                  height = shaped.Height } }

    let private typesetter = MathTypesetter meter

    let private toAvalonia (brush: IBrush) (item: MathItem) : MathDrawItem =
        match item with
        | MathItem.Glyph(text, family, size, x, y) ->
            let shaped, typeface = shape text family size brush
            MathGlyph(text, shaped, typeface, Point(x, y))
        | MathItem.Rule(x, y, w, h) -> MathRule(Rect(x, y, w, h))
        | MathItem.Path(d, scale, dx, dy, cx, cy, cw, ch) ->
            // SVG 的 fill-rule 默认 nonzero，而 Avalonia 路径语法默认 evenodd。
            // 不加 F1，根号那条长横线会被相交的根号轮廓异或掉。
            MathPath(
                Geometry.Parse("F1 " + d),
                Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy),
                Rect(cx, cy, cw, ch))

    /// 排一整条公式；失败带原因返回。
    let tryRender (brush: IBrush) (baseFontSize: float) (html: string) : Result<MathDrawing, string> =
        match typesetter.TryRender(baseFontSize, html) with
        | Error e -> Error e
        | Ok box ->
            try
                Ok
                    { width = box.width
                      above = box.above
                      below = box.below
                      items = box.items |> List.map (toAvalonia brush) }
            with ex -> Error $"{ex.GetType().Name}: {ex.Message}"

    let render (brush: IBrush) (baseFontSize: float) (html: string) : MathDrawing option =
        match tryRender brush baseFontSize html with
        | Ok box -> Some box
        | Error _ -> None
