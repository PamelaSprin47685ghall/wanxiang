module Wanxiang.Tests.MathBaselineTests

open System
open System.IO
open System.Runtime.InteropServices
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Threading
open Xunit
open Xunit.Abstractions
open Wanxiang.UI
open Wanxiang.Tests

type private FixtureBackend(html: string) =
    interface IRichBackend with
        member _.HighlightHtml(_, _) = None
        member _.MathHtml(_, _) = Some html

/// 行内公式与正文的基线是否重合。
///
/// 只能靠像素判定：控件的「基线」属性与容器的行度量同源，
/// 拿它们互相比较是同义反复。`A` 与 `E` 都坐在基线上、都没有下伸部分，
/// 所以两者墨迹的**最低一行**必须落在同一处。
type Baseline(output: ITestOutputHelper) =

    [<Literal>]
    let scale = 4.0

    member private _.Compose() =
        Headless.ensure ()
        let html =
            let dir = Path.GetDirectoryName(typeof<FixtureBackend>.Assembly.Location)
            File.ReadAllText(Path.Combine(dir, "fixtures", "inline-emc2.html"))
        RichBackend.install (FixtureBackend html)

        let size = Tokens.fontReading
        let lineHeight = size * 1.65
        let math =
            match MathRender.tryInline size lineHeight "(fixture)" with
            | Some control -> control
            | None -> failwith "公式排不出来"

        let block =
            TextBlock(
                FontFamily = Tokens.fontFamily,
                FontSize = size,
                LineHeight = lineHeight,
                Foreground = Brushes.Black,
                TextWrapping = TextWrapping.NoWrap)
        block.Inlines.Add(Run "AA")
        block.Inlines.Add(MathRender.inlineContainer math)
        block.Inlines.Add(Run "AA")

        let host =
            Border(Background = Brushes.White, Padding = Thickness 8.0, Child = block)
        let window = Window(Width = 600.0, Height = 120.0, Content = host)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        host.Measure(Size(600.0, 120.0))
        host.Arrange(Rect(0.0, 0.0, 600.0, 120.0))
        Dispatcher.UIThread.RunJobs()

        let pixels = PixelSize(int (600.0 * scale), int (120.0 * scale))
        use bitmap = new RenderTargetBitmap(pixels, Vector(96.0 * scale, 96.0 * scale))
        bitmap.Render host
        let stride = pixels.Width * 4
        let buffer = Array.zeroCreate<byte> (stride * pixels.Height)
        let handle = GCHandle.Alloc(buffer, GCHandleType.Pinned)
        try
            bitmap.CopyPixels(PixelRect pixels, handle.AddrOfPinnedObject(), buffer.Length, stride)
        finally
            handle.Free()

        /// 某个横向区间里墨迹的最低一行（像素行号）。
        let inkBottom (fromX: float) (toX: float) =
            let x0 = max 0 (int (fromX * scale))
            let x1 = min (pixels.Width - 1) (int (toX * scale))
            let mutable bottom = -1
            for y in 0 .. pixels.Height - 1 do
                for x in x0 .. x1 do
                    let at = y * stride + x * 4
                    if int buffer[at] < 128 then bottom <- y
            bottom

        let mathBox = math.Bounds
        let blockLeft = block.Bounds.X + host.Padding.Left
        let textBottom = inkBottom blockLeft (blockLeft + mathBox.X - 1.0)
        let mathBottom = inkBottom (blockLeft + mathBox.X + 1.0) (blockLeft + mathBox.Right - 1.0)
        window.Close()
        textBottom, mathBottom

    [<Fact>]
    member this.``行内公式与正文共用同一条基线``() =
        let textBottom, mathBottom = this.Compose()
        let delta = float (mathBottom - textBottom) / scale
        output.WriteLine(
            sprintf "正文墨迹底=%d 公式墨迹底=%d（%.1fx）→ 基线落差 %+.2fpt" textBottom mathBottom scale delta)
        Assert.True(textBottom > 0, "没量到正文墨迹")
        Assert.True(mathBottom > 0, "没量到公式墨迹")
        Assert.True(abs delta <= 1.0, $"基线落差 {delta:F2}pt，应当在 1pt 以内")
