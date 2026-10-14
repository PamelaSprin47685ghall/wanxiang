module Wanxiang.Tests.AttachmentVisionTests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.UI
open Wanxiang.Tests

// 决策 320–323：图片附件的模型能力检查、加入前的格式/大小校验、缩略图。

let private descendantsOf (root: Control) =
    root.GetVisualDescendants() |> Seq.choose (function :? Control as c -> Some c | _ -> None)

let private byAutomationName (root: Control) (name: string) =
    descendantsOf root |> Seq.find (fun control -> AutomationProperties.GetName(control) = name)

let private show (content: Control) width height =
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

let private png1x1 =
    Convert.FromBase64String "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="

let private actions submitted =
    { submit = (fun _ -> submitted (); true)
      stopGeneration = ignore
      pickAttachment = ignore
      removeAttachment = ignore
      openModelPicker = ignore
      dropFiles = ignore
      pasteFromClipboard = fun () -> false }

let private image id ready =
    { attachmentId = id; sha256 = "s"; size = 2048L; mediaType = "image/png"; fileName = "shot.png"; ready = ready; failed = false }

[<Trait("Category", "UI")>]
[<Fact>]
let ``image on text-only model blocks send with a clear notice`` () =
    Headless.run (fun () ->
    let mutable submitted = 0
    let composer = Composer(actions (fun () -> submitted <- submitted + 1))
    composer.Build()
    let window = show composer 720.0 320.0
    try
        composer.SetEnabled(true, "")
        composer.SetModelVision(VisionNo, "DeepSeek · deepseek-chat")
        composer.SetText "看看这张图"
        composer.SetAttachments [ image (Guid.NewGuid()) true ]
        Dispatcher.UIThread.RunJobs()
        let send = byAutomationName composer "发送"
        Assert.False send.IsEnabled
        Assert.Contains("不能识别图片", composer.VisionNoticeText)
        Assert.Contains("deepseek-chat", composer.VisionNoticeText)
        // 换成能看图的模型：提示消失，可以发送。
        composer.SetModelVision(VisionYes, "OpenAI · gpt-4o")
        Dispatcher.UIThread.RunJobs()
        Assert.True send.IsEnabled
        Assert.Equal("", composer.VisionNoticeText)
        // 认不出的模型放行（由模型服务决定），不误拦。
        composer.SetModelVision(VisionUnknown, "x")
        Assert.True send.IsEnabled
        // 只吃文字的模型 + 文本附件：不拦。
        composer.SetModelVision(VisionNo, "DeepSeek · deepseek-chat")
        composer.SetAttachments
            [ { attachmentId = Guid.NewGuid(); sha256 = "t"; size = 10L; mediaType = "text/plain"; fileName = "a.txt"; ready = true; failed = false } ]
        Dispatcher.UIThread.RunJobs()
        Assert.True send.IsEnabled
        Assert.Equal("", composer.VisionNoticeText)
    finally
        window.Close()
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``image attachment renders a thumbnail with remove button`` () =
    Headless.run (fun () ->
    let composer = Composer(actions ignore)
    composer.Build()
    let window = show composer 720.0 320.0
    try
        composer.SetEnabled(true, "")
        let id = Guid.NewGuid()
        composer.SetThumbnail(id, png1x1)
        composer.SetAttachments [ image id true ]
        Dispatcher.UIThread.RunJobs()
        Assert.True(composer.HasThumbnail id)
        let thumb = byAutomationName composer "图片预览 shot.png"
        Assert.True thumb.IsVisible
        Assert.NotNull(byAutomationName composer "移除附件 shot.png")
        // 移除后缩略图释放。
        composer.SetAttachments []
        Assert.False(composer.HasThumbnail id)
        // 损坏的图片不报错，退回文件图标。
        let bad = Guid.NewGuid()
        composer.SetThumbnail(bad, [| 1uy; 2uy; 3uy |])
        Assert.False(composer.HasThumbnail bad)
    finally
        window.Close()
    )

// ---------- 截图（仅在设置 WANXIANG_UX_ARTIFACT_DIR 时落盘） ----------

let private capture (window: Window) (name: string) =
    match Environment.GetEnvironmentVariable "WANXIANG_UX_ARTIFACT_DIR" with
    | null | "" -> ()
    | directory ->
        IO.Directory.CreateDirectory directory |> ignore
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick()
        Dispatcher.UIThread.RunJobs()
        use frame = window.CaptureRenderedFrame()
        frame.Save(IO.Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default)

/// 画一张有内容的“照片”当缩略图素材。
let private samplePhoto () =
    let canvas = Avalonia.Controls.Border(Width = 160.0, Height = 120.0)
    canvas.Background <-
        Avalonia.Media.LinearGradientBrush(
            StartPoint = RelativePoint(0.0, 0.0, RelativeUnit.Relative),
            EndPoint = RelativePoint(1.0, 1.0, RelativeUnit.Relative),
            GradientStops =
                (let stops = Avalonia.Media.GradientStops()
                 stops.Add(Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse "#F2A65A", 0.0))
                 stops.Add(Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse "#4A7FB5", 1.0))
                 stops))
    canvas.Measure(Size(160.0, 120.0)); canvas.Arrange(Rect(0.0, 0.0, 160.0, 120.0))
    use rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(PixelSize(160, 120))
    rtb.Render canvas
    use ms = new IO.MemoryStream()
    rtb.Save(ms)
    ms.ToArray()

[<Trait("Category", "UI")>]
[<Fact>]
let ``r15 截图：图片缩略图、只吃文字的模型拦下、深色、手机宽度`` () =
    Headless.run (fun () ->
    MotionPolicy.setReduced true
    let scene (theme: ThemeMode) (width: float) (vision: VisionSupport) (label: string) (shot: string) =
        Tokens.apply theme
        let composer = Composer(actions ignore)
        composer.Build()
        let host = Grid(Background = Tokens.canvas)
        host.Children.Add(Border(Padding = Thickness 16.0, VerticalAlignment = Layout.VerticalAlignment.Bottom, Child = composer))
        let window = show host width 300.0
        try
            composer.SetEnabled(true, "")
            composer.SetModelLabel label
            composer.SetModelVision(vision, label)
            composer.SetText "这张截图里的报错是什么意思？"
            let photo = Guid.NewGuid()
            composer.SetThumbnail(photo, samplePhoto ())
            composer.SetAttachments
                [ { image photo true with fileName = "screenshot.png"; size = 184_320L }
                  { attachmentId = Guid.NewGuid(); sha256 = "t"; size = 3_200L; mediaType = "text/plain"; fileName = "build.log"; ready = true; failed = false } ]
            Dispatcher.UIThread.RunJobs()
            capture window shot
        finally
            window.Close()
    scene ThemeMode.Light 760.0 VisionYes "OpenAI · gpt-4o" "01-thumbnails-vision-model"
    scene ThemeMode.Light 760.0 VisionNo "DeepSeek · deepseek-chat" "02-text-only-model-blocks-image"
    scene ThemeMode.Dark 760.0 VisionNo "Ollama · llama3.1:8b" "03-blocked-dark"
    scene ThemeMode.Light 390.0 VisionYes "Ollama · llava:13b" "04-mobile-thumbnails"
    Tokens.apply ThemeMode.Light
    )
