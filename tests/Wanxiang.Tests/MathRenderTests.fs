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
        match MathLayout.tryRender Brushes.Black 14.5 (load name) with
        | Ok box -> box
        | Error reason -> failwith $"{name} 排不出来：{reason}"

    let glyphs (box: MathDrawing) =
        box.items
        |> List.choose (function
            | MathGlyph(text, shaped, typeface, at) -> Some(text, typeface, at, shaped.Width)
            | _ -> None)

    let rules (box: MathDrawing) =
        box.items |> List.choose (function MathRule rect -> Some rect | _ -> None)

    let paths (box: MathDrawing) =
        box.items |> List.choose (function MathPath(g, _, clip) -> Some(g, clip) | _ -> None)

    /// 横向中心。居中是否正确只能靠数值判断——截图里几个像素的偏差看不出来。
    let centerOf (from: float) (width: float) = from + width / 2.0

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

[<Trait("Category", "UI")>]
[<Theory>]
[<InlineData("quadratic", true)>]
[<InlineData("summation", true)>]
[<InlineData("inline-emc2", false)>]
[<InlineData("inline-greek", false)>]
let ``公式的墨迹不越出它自己声明的尺寸`` (name: string) (display: bool) =
    Headless.run (fun () ->
    // 排版盒若比实际墨迹小，公式就会压到相邻文字上——这是最难从截图里看出来的错
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
    )
