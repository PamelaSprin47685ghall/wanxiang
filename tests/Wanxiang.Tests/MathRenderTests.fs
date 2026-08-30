module Wanxiang.Tests.MathRenderTests

open System.IO
open System.Runtime.InteropServices
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Media.Imaging
open Xunit
open Wanxiang.UI
open Wanxiang.Tests

/// 喂预先用真 KaTeX 生成的 HTML，把脚本引擎从公式测试里摘出去：
/// 这里要验的是**排版**，不是 KaTeX 本身。
type private FixtureBackend(html: string) =
    interface IRichBackend with
        member _.HighlightHtml(_, _) = None
        member _.MathHtml(_, _) = Some html

module private Fixture =
    let load (name: string) =
        let dir = Path.GetDirectoryName(typeof<FixtureBackend>.Assembly.Location)
        File.ReadAllText(Path.Combine(dir, "fixtures", $"{name}.html"))

    let layout (name: string) =
        Headless.ensure ()
        match MathLayout.tryRender Brushes.Black 14.5 (load name) with
        | Ok box -> box
        | Error reason -> failwith $"{name} 排不出来：{reason}"

    let glyphs (box: MathBox) =
        box.items
        |> List.choose (function
            | MathGlyph(text, shaped, typeface, at) -> Some(text, typeface, at, shaped.Width)
            | _ -> None)

    let rules (box: MathBox) =
        box.items |> List.choose (function MathRule rect -> Some rect | _ -> None)

    let paths (box: MathBox) =
        box.items |> List.choose (function MathPath(g, _, clip) -> Some(g, clip) | _ -> None)

    /// 横向中心。居中是否正确只能靠数值判断——截图里几个像素的偏差看不出来。
    let centerOf (from: float) (width: float) = from + width / 2.0

// ---------------------------------------------------------------- 字族选择

[<Fact>]
let ``数字与运算符用直立的 KaTeX_Main`` () =
    // 数学里数字与运算符必须直立，变量才斜体；选错字族公式会「看起来不像数学」
    let upright =
        Fixture.layout "summation"
        |> Fixture.glyphs
        |> List.filter (fun (text, _, _, _) -> text = "1" || text = "2" || text = "=" || text = "+")
    Assert.NotEmpty upright
    for (text, typeface, _, _) in upright do
        Assert.Equal("KaTeX_Main", typeface.FontFamily.Name)
        Assert.False(System.String.IsNullOrEmpty text)

[<Fact>]
let ``变量用斜体的数学字体`` () =
    // 斜体由字族本身提供（KaTeX_MathItalic），不靠样式匹配也不靠合成倾斜：
    // 浏览器端按样式在字族内挑成员会挑错，挑到缺减号的那张表
    let italics =
        Fixture.layout "quadratic"
        |> Fixture.glyphs
        |> List.filter (fun (text, _, _, _) -> text = "a" || text = "b" || text = "c")
    Assert.NotEmpty italics
    for (_, typeface, _, _) in italics do
        Assert.Equal("KaTeX_MathItalic", typeface.FontFamily.Name)
        Assert.Equal(FontStyle.Normal, typeface.Style)

[<Fact>]
let ``大型算符取 Size 系列字体`` () =
    // ∑ 在正文字号下是小的，KaTeX 靠 KaTeX_Size2 换一个真正的大字形
    let sigma =
        Fixture.layout "summation"
        |> Fixture.glyphs
        |> List.tryFind (fun (text, _, _, _) -> text = "\u2211")
    match sigma with
    | Some(_, typeface, _, _) -> Assert.StartsWith("KaTeX_Size", typeface.FontFamily.Name)
    | None -> failwith "没有找到 ∑ 字形"

[<Fact>]
let ``减号与正负号落在有这些字形的字体上`` () =
    // KaTeX_Math 里没有 U+2212 / U+00B1，选错就是两个豆腐块
    let signs =
        Fixture.layout "quadratic"
        |> Fixture.glyphs
        |> List.filter (fun (text, _, _, _) -> text = "\u2212" || text = "\u00B1")
    Assert.Equal(3, signs.Length)
    for (_, typeface, _, _) in signs do
        Assert.Equal("KaTeX_Main", typeface.FontFamily.Name)

// ---------------------------------------------------------------- 居中

[<Fact>]
let ``分子分母与分数线共享同一条中轴`` () =
    let box = Fixture.layout "quadratic"
    let bar = Fixture.rules box |> List.maxBy (fun r -> r.Width)
    let denominator =
        Fixture.glyphs box
        |> List.filter (fun (_, _, at, _) -> at.Y > -10.0)
    Assert.NotEmpty denominator
    let left = denominator |> List.map (fun (_, _, at, _) -> at.X) |> List.min
    let right = denominator |> List.map (fun (_, _, at, w) -> at.X + w) |> List.max
    let barCenter = Fixture.centerOf bar.X bar.Width
    let denomCenter = Fixture.centerOf left (right - left)
    Assert.True(abs (barCenter - denomCenter) < 1.5, $"分母偏离中轴：分数线 {barCenter}，分母 {denomCenter}")

[<Fact>]
let ``上下限居中于大型算符`` () =
    let box = Fixture.layout "summation"
    let glyphs = Fixture.glyphs box
    let (_, _, sigmaAt, sigmaWidth) = glyphs |> List.find (fun (t, _, _, _) -> t = "\u2211")
    let sigmaCenter = Fixture.centerOf sigmaAt.X sigmaWidth
    // 上限在算符之上，下限在其下；分式部分在更右侧，用 x 范围隔开
    let limitBand upper =
        glyphs
        |> List.filter (fun (_, _, at, w) ->
            at.X + w <= sigmaAt.X + sigmaWidth + 8.0
            && (if upper then at.Y < sigmaAt.Y else at.Y > sigmaAt.Y))
    for upper in [ true; false ] do
        let band = limitBand upper
        Assert.NotEmpty band
        let left = band |> List.map (fun (_, _, at, _) -> at.X) |> List.min
        let right = band |> List.map (fun (_, _, at, w) -> at.X + w) |> List.max
        let center = Fixture.centerOf left (right - left)
        Assert.True(
            abs (center - sigmaCenter) < 1.5,
            $"""{(if upper then "上限" else "下限")}偏离算符中轴：算符 {sigmaCenter}，限 {center}""")

// ---------------------------------------------------------------- 根号

[<Fact>]
let ``根号画成 SVG 且保留上方横线`` () =
    let box = Fixture.layout "quadratic"
    match Fixture.paths box with
    | [] -> failwith "根号没有生成 SVG 图元"
    | (geometry, clip) :: _ ->
        // KaTeX 的根号路径末段 `M834 80h400000v40h-400000z` 就是被开方式上方那条横线。
        // Avalonia 的路径语法默认 evenodd，会把它与根号轮廓相交的部分异或掉，
        // 必须按 SVG 语义用 nonzero 解析。
        Assert.True(geometry.Bounds.Width > 100000.0, $"横线不在几何体里，Bounds={geometry.Bounds}")
        Assert.True(clip.Width > 0.0, "裁剪框为零宽，等于什么都不画")

// ---------------------------------------------------------------- 越界

/// 把控件画到位图上，返回墨迹的外接框（相对内容区左上角）。
let private inkBounds (control: Control) (pad: float) (size: Size) =
    let paper = Color.Parse "#FBFAF7"
    let host = Border(Background = SolidColorBrush paper, Padding = Thickness pad, Child = control)
    let full = Size(size.Width + pad * 2.0, size.Height + pad * 2.0)
    host.Measure full
    host.Arrange(Rect full)
    let pixels = PixelSize(int (ceil full.Width), int (ceil full.Height))
    use bitmap = new RenderTargetBitmap(pixels, Vector(96.0, 96.0))
    bitmap.Render host
    let stride = pixels.Width * 4
    let buffer = Array.zeroCreate<byte> (stride * pixels.Height)
    let handle = GCHandle.Alloc(buffer, GCHandleType.Pinned)
    try
        bitmap.CopyPixels(PixelRect(pixels), handle.AddrOfPinnedObject(), buffer.Length, stride)
    finally
        handle.Free()
    let inked =
        [ for y in 0 .. pixels.Height - 1 do
            for x in 0 .. pixels.Width - 1 do
                let at = y * stride + x * 4
                // 与纸色明显不同即视作墨迹
                if abs (int buffer[at] - int paper.B) > 40 then yield (x, y) ]
    if List.isEmpty inked then None
    else
        let xs = inked |> List.map fst
        let ys = inked |> List.map snd
        Some(Rect(Point(float (List.min xs), float (List.min ys)), Point(float (List.max xs), float (List.max ys))))

[<Theory>]
[<InlineData("quadratic", true)>]
[<InlineData("summation", true)>]
[<InlineData("inline-emc2", false)>]
[<InlineData("inline-greek", false)>]
let ``公式的墨迹不越出它自己声明的尺寸`` (name: string) (display: bool) =
    // 排版盒若比实际墨迹小，公式就会压到相邻文字上——这是最难从截图里看出来的错
    Headless.ensure ()
    RichBackend.install (FixtureBackend(Fixture.load name))
    let visual = MathVisual("(fixture)", display, 14.5, 14.5 * 1.65)
    visual.Measure(Size(infinity, infinity))
    let size = visual.DesiredSize
    Assert.True(size.Width > 0.0 && size.Height > 0.0, $"{name} 排不出尺寸")
    let pad = 24.0
    match inkBounds visual pad size with
    | None -> failwith $"{name} 画不出任何墨迹"
    | Some ink ->
        let slack = 1.5
        Assert.True(ink.X >= pad - slack, $"{name} 向左越界：墨迹 x={ink.X}，留白 {pad}")
        Assert.True(ink.Y >= pad - slack, $"{name} 向上越界：墨迹 y={ink.Y}，留白 {pad}")
        Assert.True(
            ink.Right <= pad + size.Width + slack,
            $"{name} 向右越界：墨迹右缘 {ink.Right}，应不超过 {pad + size.Width}")
        Assert.True(
            ink.Bottom <= pad + size.Height + slack,
            $"{name} 向下越界：墨迹下缘 {ink.Bottom}，应不超过 {pad + size.Height}")
