module Wanxiang.Tests.R17CapabilityTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Wanxiang.Config
open Wanxiang.Server

let private freePort () =
    let l = new TcpListener(IPAddress.Loopback, 0)
    l.Start()
    let p = (l.LocalEndpoint :?> IPEndPoint).Port
    l.Stop()
    p

/// 极简假模型服务：按路径返回固定内容，记录被请求的路径。
let private fakeServer (routes: Map<string, string>) =
    let port = freePort ()
    let listener = new HttpListener()
    listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
    listener.Start()
    let hits = Collections.Concurrent.ConcurrentQueue<string>()
    let loop =
        task {
            try
                while listener.IsListening do
                    let! ctx = listener.GetContextAsync()
                    let path = ctx.Request.Url.AbsolutePath
                    hits.Enqueue path
                    match routes.TryFind path with
                    | Some body ->
                        let bytes = Encoding.UTF8.GetBytes(body: string)
                        ctx.Response.ContentType <- "application/json"
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                    | None -> ctx.Response.StatusCode <- 404
                    ctx.Response.Close()
            with _ -> ()
        }
    ignore loop
    port, listener, hits

let private provider port kind : ProviderConfig =
    { id = "local"; kind = kind; label = "Local"; baseUrl = sprintf "http://127.0.0.1:%d/v1" port; apiKey = None
      models = [ "llava:13b"; "llama3.1:8b" ]; defaultModel = "llava:13b"
      timeoutSeconds = 10; maxRetries = 0; enabled = true; promptCaching = false; headers = Map.empty
      extraJson = None; visionOverrides = Map.empty }

let private models = """{"object":"list","data":[{"id":"llava:13b"},{"id":"llama3.1:8b"}]}"""

[<Fact>]
let ``ollama is detected via api version, capabilities persist across restart`` () =
    let showBody = """{"capabilities":["completion","vision"]}"""
    let port, listener, hits =
        fakeServer (Map.ofList [ "/v1/models", models; "/api/version", """{"version":"0.12.3"}"""; "/api/show", showBody ])
    try
        ModelCapabilities.clear ()
        let file = Path.Combine(Path.GetTempPath(), sprintf "wx-cap-%s.json" (Guid.NewGuid().ToString("N")))
        ModelCapabilities.configure file
        let p = provider port "openai"
        let r = (ProviderCatalog.probeModelsTyped p CancellationToken.None).Result
        Assert.True(Result.isOk r)
        Assert.Contains("/api/version", hits)
        Assert.Equal(Some true, ModelCapabilities.tryGet p "llava:13b")
        Assert.True(File.Exists file)
        Assert.True((ModelCapabilities.detectedAt p "llava:13b").IsSome)
        // 模拟重启：清内存后从同一文件读回
        ModelCapabilities.clear ()
        Assert.Equal(None, ModelCapabilities.tryGet p "llava:13b")
        ModelCapabilities.configure file
        Assert.Equal(Some true, ModelCapabilities.tryGet p "llava:13b")
        ModelCapabilities.clear ()
        File.Delete file
    finally
        listener.Stop()

[<Fact>]
let ``non-ollama server on any port is not asked for api show; refresh drops stale entries`` () =
    let port, listener, hits = fakeServer (Map.ofList [ "/v1/models", models ])
    try
        ModelCapabilities.clear ()
        let p = provider port "openai"
        ModelCapabilities.record p [ "old-model", true ]
        let r = (ProviderCatalog.probeModelsTyped p CancellationToken.None).Result
        Assert.True(Result.isOk r)
        Assert.DoesNotContain("/api/show", hits)
        Assert.Equal(None, ModelCapabilities.tryGet p "old-model")
    finally
        listener.Stop()
        ModelCapabilities.clear ()

[<Fact>]
let ``ollama version body parsing`` () =
    Assert.True(ModelCapabilities.ofOllamaVersion """{"version":"0.5.1"}""")
    Assert.False(ModelCapabilities.ofOllamaVersion """<html>""")
    Assert.False(ModelCapabilities.ofOllamaVersion """{"error":"x"}""")

let private bigImage () =
    use bmp = new SkiaSharp.SKBitmap(5000, 100)
    bmp.Erase(SkiaSharp.SKColors.Coral)
    use img = SkiaSharp.SKImage.FromBitmap bmp
    img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray()

[<Fact>]
let ``shrinkAsync prefers the platform shrinker and falls back to Skia when it declines`` () =
    let bytes = bigImage ()
    let saved = Wanxiang.UI.ImageShrink.platformShrinker
    try
        let mutable called = 0
        Wanxiang.UI.ImageShrink.platformShrinker <-
            Some(fun _ b mt ->
                async {
                    called <- called + 1
                    return
                        Some(
                            Ok
                                { Wanxiang.UI.ShrunkImage.bytes = [| 1uy |]; fileName = "x"; mediaType = "image/jpeg"
                                  originalBytes = 0L; originalWidth = 5000; originalHeight = 100; width = 4096; height = 82 }
                        )
                })
        match Wanxiang.UI.ImageShrink.shrinkAsync "wide.png" bytes |> Async.RunSynchronously with
        | Ok(Some r) ->
            Assert.Equal(1, called)
            Assert.Equal("wide.jpg", r.fileName)
            Assert.Equal(int64 bytes.Length, r.originalBytes)
        | other -> failwithf "unexpected %A" other
        Wanxiang.UI.ImageShrink.platformShrinker <- Some(fun _ _ _ -> async { return None })
        match Wanxiang.UI.ImageShrink.shrinkAsync "wide.png" bytes |> Async.RunSynchronously with
        | Ok(Some r) -> Assert.True(r.width <= 4096 && r.bytes.Length > 1)
        | other -> failwithf "unexpected %A" other
    finally
        Wanxiang.UI.ImageShrink.platformShrinker <- saved
