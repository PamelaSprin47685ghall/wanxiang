module Wanxiang.Tests.R16ImageVisionTests

open System
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Threading
open Avalonia.VisualTree
open SkiaSharp
open Xunit
open Wanxiang.Core
open Wanxiang.Config
open Wanxiang.Server
open Wanxiang.UI
open Wanxiang.Tests

// 决策 330–334：超大图片自动缩小、看图能力的三层来源、附件选择器筛选、输入框深色底。

let private noiseImage (w: int) (h: int) (format: SKEncodedImageFormat) (quality: int) =
    use bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque)
    let rnd = Random(42)
    let pixels = Array.zeroCreate<byte> (w * h * 4)
    rnd.NextBytes pixels
    for i in 0 .. 4 .. pixels.Length - 1 do pixels.[i + 3] <- 255uy
    let handle = Runtime.InteropServices.GCHandle.Alloc(pixels, Runtime.InteropServices.GCHandleType.Pinned)
    try
        bmp.InstallPixels(bmp.Info, handle.AddrOfPinnedObject(), bmp.RowBytes) |> ignore
        use image = SKImage.FromBitmap bmp
        use data = image.Encode(format, quality)
        data.ToArray()
    finally
        handle.Free()

let private flatImage (w: int) (h: int) =
    use bmp = new SKBitmap(w, h)
    use canvas = new SKCanvas(bmp)
    canvas.Clear(SKColors.SteelBlue)
    use image = SKImage.FromBitmap bmp
    use data = image.Encode(SKEncodedImageFormat.Png, 100)
    data.ToArray()

[<Fact>]
let ``oversize photo is downscaled under 8 MiB with a before-after note`` () =
    let original = noiseImage 3000 2400 SKEncodedImageFormat.Png 100
    Assert.True(int64 original.Length > ImageShrink.maxBytes, sprintf "fixture too small: %d" original.Length)
    Assert.True(ImageShrink.needsShrink "photo.png" original)
    match ImageShrink.shrink "photo.png" original with
    | Ok(Some r) ->
        Assert.True(int64 r.bytes.Length <= ImageShrink.maxBytes)
        Assert.Equal("photo.jpg", r.fileName)
        Assert.Equal("image/jpeg", r.mediaType)
        Assert.Equal((3000, 2400), (r.originalWidth, r.originalHeight))
        Assert.True(max r.width r.height <= ImageShrink.maxDimension)
        let note = ImageShrink.note r
        Assert.Contains("原图", note)
        Assert.Contains("→ 发送", note)
        Assert.Contains("3000×2400", note)
        Assert.Equal(Some(r.width, r.height), ImageShrink.tryDimensions r.bytes)
    | other -> failwithf "expected shrink, got %A" other

[<Fact>]
let ``huge dimensions shrink to 4096 even when the file is small`` () =
    let original = flatImage 9000 1200
    Assert.True(int64 original.Length < ImageShrink.maxBytes)
    match ImageShrink.shrink "wide.png" original with
    | Ok(Some r) ->
        Assert.Equal(4096, r.width)
        Assert.Equal(546, r.height)
    | other -> failwithf "expected shrink, got %A" other

[<Fact>]
let ``normal images and non-images are left alone`` () =
    let small = flatImage 800 600
    Assert.False(ImageShrink.needsShrink "a.png" small)
    Assert.Equal(Ok None, ImageShrink.shrink "a.png" small)
    Assert.False(ImageShrink.needsShrink "big.pdf" (Array.zeroCreate (9 * 1024 * 1024)))
    // 读不出来的“图片”不抛异常。
    Assert.False(ImageShrink.needsShrink "broken.png" [| 1uy; 2uy; 3uy |])

[<Fact>]
let ``provider metadata is read from openai-compatible, ollama and anthropic sources`` () =
    let body =
        """{"data":[
            {"id":"or/vision","architecture":{"input_modalities":["text","image"]}},
            {"id":"or/text","architecture":{"modality":"text->text"}},
            {"id":"gw/caps","capabilities":["completion","vision"]},
            {"id":"gw/obj","capabilities":{"vision":false}},
            {"id":"plain"}]}"""
    let pairs = ModelCapabilities.ofModelsBody body |> Map.ofList
    Assert.Equal(Some true, pairs.TryFind "or/vision")
    Assert.Equal(Some false, pairs.TryFind "or/text")
    Assert.Equal(Some true, pairs.TryFind "gw/caps")
    Assert.Equal(Some false, pairs.TryFind "gw/obj")
    Assert.Equal(None, pairs.TryFind "plain")
    Assert.Equal(Some true, ModelCapabilities.ofOllamaShow """{"capabilities":["completion","vision"]}""")
    Assert.Equal(Some false, ModelCapabilities.ofOllamaShow """{"capabilities":["completion"]}""")
    Assert.Equal(None, ModelCapabilities.ofOllamaShow """{"modelfile":"x"}""")
    Assert.Equal(Some true, ModelCapabilities.anthropicKnown "claude-sonnet-4-5")
    Assert.Equal(Some false, ModelCapabilities.anthropicKnown "claude-2.1")
    Assert.Equal(None, ModelCapabilities.anthropicKnown "something")

let private providerInfo overrides detected : ProviderInfo =
    { id = "local"; label = "Ollama"; kind = "openai"; baseUrl = "http://127.0.0.1:11434/v1"; hasApiKey = false
      models = [ "llama3.1:8b"; "my-vl"; "llava:13b" ]; defaultModel = "llama3.1:8b"; timeoutSeconds = 60; maxRetries = 0
      enabled = true; visionOverrides = overrides; visionDetected = detected }

[<Fact>]
let ``vision precedence is user override, then provider metadata, then name`` () =
    let catalog detected overrides = { Catalog.empty with providers = [ providerInfo overrides detected ] }
    // 只有名字：llama3.1 推断为只吃文字。
    Assert.Equal((VisionNo, VisionFromName), Catalog.visionOf "local" "llama3.1:8b" (catalog Map.empty Map.empty))
    // 服务信息说能看图：压过名字。
    Assert.Equal((VisionYes, VisionFromProvider), Catalog.visionOf "local" "llama3.1:8b" (catalog (Map.ofList [ "llama3.1:8b", true ]) Map.empty))
    // 用户指定：压过服务信息。
    Assert.Equal(
        (VisionNo, VisionFromUser),
        Catalog.visionOf "local" "llama3.1:8b" (catalog (Map.ofList [ "llama3.1:8b", true ]) (Map.ofList [ "llama3.1:8b", false ])))
    Assert.Equal((VisionUnknown, VisionFromName), Catalog.visionOf "local" "my-model" (catalog Map.empty Map.empty))

[<Fact>]
let ``catalog snapshot carries overrides and detected capability, and the client parses them`` () =
    ModelCapabilities.clear ()
    let p: ProviderConfig =
        { id = "claude"; kind = "anthropic"; label = "Claude"; baseUrl = "https://api.anthropic.com"; apiKey = None
          models = [ "claude-sonnet-4-5"; "claude-2.1"; "mystery" ]; defaultModel = "claude-sonnet-4-5"
          timeoutSeconds = 60; maxRetries = 0; enabled = true; promptCaching = false; headers = Map.empty
          extraJson = None; visionOverrides = Map.ofList [ "mystery", true ] }
    let cfg = { AppConfig.defaults (Guid.NewGuid()) with providers = Map.ofList [ p.id, p ] }
    let catalog = Catalog.parse (ProviderCatalog.providers cfg) (JsonArray()) (ProviderCatalog.generation cfg)
    let info = catalog.providers |> List.exactlyOne
    Assert.Equal<Map<string, bool>>(Map.ofList [ "mystery", true ], info.visionOverrides)
    Assert.Equal<Map<string, bool>>(Map.ofList [ "claude-sonnet-4-5", true; "claude-2.1", false ], info.visionDetected)
    Assert.Equal((VisionYes, VisionFromUser), Catalog.visionOf "claude" "mystery" catalog)
    Assert.Equal((VisionNo, VisionFromProvider), Catalog.visionOf "claude" "claude-2.1" catalog)

[<Fact>]
let ``per-model overrides round-trip through TOML and config mutation drops stale models`` () =
    let tomlTemplate =
        """configVersion = 3
instanceId = "IID"
[network]
listen = "127.0.0.1:0"
[providers.local]
kind = "openai"
baseUrl = "http://127.0.0.1:11434/v1"
models = ["llama3.1:8b", "my-vl"]
visionModels = ["my-vl"]
textOnlyModels = ["llama3.1:8b"]
"""
    let toml = tomlTemplate.Replace("IID", Guid.NewGuid().ToString())
    match TomlCodec.tryParse toml with
    | Error e -> failwith (String.concat "; " e)
    | Ok cfg ->
        let p = cfg.providers["local"]
        Assert.Equal<Map<string, bool>>(Map.ofList [ "my-vl", true; "llama3.1:8b", false ], p.visionOverrides)
        let text = TomlCodec.serialize cfg
        Assert.Contains("visionModels = [", text)
        Assert.Contains("textOnlyModels = [", text)
        match TomlCodec.tryParse text with
        | Ok again -> Assert.Equal<Map<string, bool>>(p.visionOverrides, again.providers["local"].visionOverrides)
        | Error e -> failwith (String.concat "; " e)
        // 设置界面整份提交：只保留仍在模型列表里的模型。
        let payload = JsonObject()
        payload["id"] <- "local"
        payload["kind"] <- "openai"
        payload["baseUrl"] <- "http://127.0.0.1:11434/v1"
        payload["models"] <- JsonArray([| JsonNode.op_Implicit "my-vl" |])
        payload["defaultModel"] <- "my-vl"
        let o = JsonObject()
        o["my-vl"] <- false
        o["gone"] <- true
        payload["visionOverrides"] <- o
        match ConfigMutation.upsertProvider cfg payload with
        | Ok updated -> Assert.Equal<Map<string, bool>>(Map.ofList [ "my-vl", false ], updated.providers["local"].visionOverrides)
        | Error e -> failwith (String.concat "; " e)
        // 不带 visionOverrides 的旧客户端：保持原值。
        let legacy = payload.DeepClone().AsObject()
        legacy.Remove "visionOverrides" |> ignore
        legacy["models"] <- JsonArray([| JsonNode.op_Implicit "my-vl"; JsonNode.op_Implicit "llama3.1:8b" |])
        match ConfigMutation.upsertProvider cfg legacy with
        | Ok updated -> Assert.Equal<Map<string, bool>>(p.visionOverrides, updated.providers["local"].visionOverrides)
        | Error e -> failwith (String.concat "; " e)

[<Fact>]
let ``file picker offers all files first, then images and text`` () =
    let labels = MediaTypes.pickerFilters |> List.map (fun (l, _, _) -> l)
    Assert.Equal("所有文件", List.head labels)
    let _, imagePatterns, imageMimes = MediaTypes.pickerFilters.[1]
    Assert.Contains("*.webp", imagePatterns)
    Assert.DoesNotContain("*.heic", imagePatterns)
    Assert.Contains("image/png", imageMimes)
    let _, textPatterns, _ = MediaTypes.pickerFilters.[2]
    for ext in [ "*.txt"; "*.md"; "*.pdf"; "*.json"; "*.py" ] do Assert.Contains(ext, textPatterns)
    Assert.DoesNotContain("*.zip", textPatterns)
    Assert.DoesNotContain("*.png", textPatterns)

// ---------- UI ----------

let private descendantsOf (root: Control) =
    root.GetVisualDescendants() |> Seq.choose (function :? Control as c -> Some c | _ -> None)

let private show (content: Control) width height =
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

let private actions =
    { submit = (fun _ -> true)
      stopGeneration = ignore
      pickAttachment = ignore
      removeAttachment = ignore
      openModelPicker = ignore
      dropFiles = ignore
      pasteFromClipboard = fun () -> false }

let private capture (window: Window) (name: string) =
    match Environment.GetEnvironmentVariable "WANXIANG_UX_ARTIFACT_DIR" with
    | null | "" -> ()
    | directory ->
        IO.Directory.CreateDirectory directory |> ignore
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick()
        Dispatcher.UIThread.RunJobs()
        use frame = window.CaptureRenderedFrame()
        frame.Save(IO.Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default)

let private pixelAt (window: Window) (x: int) (y: int) =
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick()
    Dispatcher.UIThread.RunJobs()
    use frame = window.CaptureRenderedFrame()
    use ms = new IO.MemoryStream()
    frame.Save(ms)
    use bmp = SKBitmap.Decode(ms.ToArray())
    let scale = float bmp.Width / window.Width
    bmp.GetPixel(int (float x * scale), int (float y * scale))

[<Trait("Category", "UI")>]
[<Fact>]
let ``shrunk attachment shows a badge whose name is the before-after note`` () =
    Headless.run (fun () ->
    let composer = Composer(actions)
    composer.Build()
    let window = show composer 720.0 320.0
    try
        composer.SetEnabled(true, "")
        let id = Guid.NewGuid()
        let note = "已自动缩小：原图 12.4 MiB（6000×4000）→ 发送 2.1 MiB（4096×2731）"
        composer.SetShrinkNote(id, note)
        composer.SetAttachments
            [ { attachmentId = id; sha256 = "s"; size = 2_200_000L; mediaType = "image/jpeg"; fileName = "IMG_0042.jpg"; ready = true; failed = false } ]
        Dispatcher.UIThread.RunJobs()
        let badge = descendantsOf composer |> Seq.find (fun c -> AutomationProperties.GetAutomationId c = "attachment-shrink-note")
        Assert.Equal(note, AutomationProperties.GetName badge)
        Assert.True(descendantsOf composer |> Seq.exists (fun c ->
            let n = AutomationProperties.GetName c
            not (isNull n) && n.Contains note && n.StartsWith "附件："))
        composer.SetAttachments []
        Assert.Equal("", composer.ShrinkNote id)
    finally
        window.Close()
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``focused composer input keeps the dark surface instead of a white plate`` () =
    Headless.run (fun () ->
    MotionPolicy.setReduced true
    Tokens.apply ThemeMode.Dark
    try
        let composer = Composer(actions)
        composer.Build()
        let host = Grid(Background = Tokens.canvas)
        host.Children.Add(Border(Padding = Thickness 16.0, VerticalAlignment = Layout.VerticalAlignment.Bottom, Child = composer))
        let window = show host 720.0 240.0
        try
            composer.SetEnabled(true, "")
            composer.SetText "深色输入框"
            composer.Focus()
            Dispatcher.UIThread.RunJobs()
            let input = descendantsOf composer |> Seq.find (fun c -> c :? TextBox) :?> TextBox
            Assert.True input.IsFocused
            let p = input.TranslatePoint(Point(input.Bounds.Width - 30.0, input.Bounds.Height / 2.0), window).Value
            let px = pixelAt window (int p.X) (int p.Y)
            let luminance = (int px.Red + int px.Green + int px.Blue) / 3
            Assert.True(luminance < 90, sprintf "focused input background too light: %A" px)
        finally
            window.Close()
    finally
        Tokens.apply ThemeMode.Light
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``r16 截图：自动缩小提示、深色输入框聚焦、看图能力设置`` () =
    Headless.run (fun () ->
    MotionPolicy.setReduced true
    let photoBytes =
        use bmp = new SKBitmap(160, 120)
        use canvas = new SKCanvas(bmp)
        use paint = new SKPaint(Shader = SKShader.CreateLinearGradient(SKPoint(0.0f, 0.0f), SKPoint(160.0f, 120.0f), [| SKColor.Parse "#F2A65A"; SKColor.Parse "#4A7FB5" |], SKShaderTileMode.Clamp))
        canvas.DrawRect(0.0f, 0.0f, 160.0f, 120.0f, paint)
        use image = SKImage.FromBitmap bmp
        use data = image.Encode(SKEncodedImageFormat.Jpeg, 90)
        data.ToArray()
    let composerScene (theme: ThemeMode) (width: float) (shot: string) =
        Tokens.apply theme
        let composer = Composer(actions)
        composer.Build()
        let host = Grid(Background = Tokens.canvas)
        host.Children.Add(Border(Padding = Thickness 16.0, VerticalAlignment = Layout.VerticalAlignment.Bottom, Child = composer))
        let window = show host width 260.0
        try
            composer.SetEnabled(true, "")
            composer.SetModelLabel "Ollama · llava:13b"
            composer.SetModelVision(VisionYes, "Ollama · llava:13b")
            composer.SetText "这张照片里的植物是什么？"
            let id = Guid.NewGuid()
            composer.SetThumbnail(id, photoBytes)
            composer.SetShrinkNote(id, "已自动缩小：原图 14.8 MiB（6000×4000）→ 发送 2.3 MiB（4096×2731）")
            composer.SetAttachments
                [ { attachmentId = id; sha256 = "s"; size = 2_411_724L; mediaType = "image/jpeg"; fileName = "IMG_0042.jpg"; ready = true; failed = false } ]
            composer.Focus()
            Dispatcher.UIThread.RunJobs()
            capture window shot
        finally
            window.Close()
    composerScene ThemeMode.Light 760.0 "01-shrunk-image-note"
    composerScene ThemeMode.Dark 760.0 "02-dark-composer-focused"
    composerScene ThemeMode.Dark 390.0 "03-mobile-dark-shrunk"
    // 设置：看图能力（编辑模型服务 → 高级设置）。
    for theme, shot in [ ThemeMode.Light, "04-provider-vision-settings"; ThemeMode.Dark, "05-provider-vision-settings-dark" ] do
        Tokens.apply theme
        let root = Grid(Background = Tokens.canvas)
        let overlay = OverlayHost(root)
        let settingsActions: SettingsActions =
            { upsertProvider = fun _ completed -> completed true
              deleteProvider = ignore
              probeProvider = ignore
              upsertMcp = fun _ completed -> completed true
              deleteMcp = ignore
              updateGeneration = fun _ completed -> completed true
              savePrefs = ignore
              toast = fun _ _ -> () }
        let providers = SettingsProviders(overlay, settingsActions)
        let info = providerInfo (Map.ofList [ "my-vl", true ]) (Map.ofList [ "llava:13b", true ])
        providers.SetCatalog { Catalog.empty with providers = [ info ] }
        let window = show root 760.0 1100.0
        try
            let m = typeof<SettingsProviders>.GetMethod("ShowEditor", Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic)
            m.Invoke(providers, [| box (Some info) |]) |> ignore
            Dispatcher.UIThread.RunJobs()
            let yesBox = descendantsOf root |> Seq.find (fun c -> AutomationProperties.GetAutomationId c = "provider-vision-yes") :?> TextBox
            Assert.Equal("my-vl", yesBox.Text)
            let current = descendantsOf root |> Seq.find (fun c -> AutomationProperties.GetAutomationId c = "provider-vision-current") :?> TextBlock
            Assert.Contains("llama3.1:8b：只吃文字（按名字推断）", current.Text)
            Assert.Contains("my-vl：能看图（你的指定）", current.Text)
            Assert.Contains("llava:13b：能看图（模型服务提供）", current.Text)
            // 展开高级设置，再把看图区域滚进视野。
            let mutable node: StyledElement = yesBox
            while not (isNull node) do
                match node with
                | :? StackPanel as sp when not sp.IsVisible -> sp.IsVisible <- true
                | _ -> ()
                node <- node.Parent
            Dispatcher.UIThread.RunJobs()
            let noBox = descendantsOf root |> Seq.find (fun c -> AutomationProperties.GetAutomationId c = "provider-vision-no")
            noBox.BringIntoView() |> ignore
            Dispatcher.UIThread.RunJobs()
            capture window shot
        finally
            window.Close()
    Tokens.apply ThemeMode.Light
    )
