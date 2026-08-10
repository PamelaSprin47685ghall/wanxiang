namespace Wanxiang.Pwa

open System
open System.Runtime.Versioning
open Avalonia
open Avalonia.Browser
open Wanxiang.UI

/// 万象 PWA 入口（决策 1/2：PWA 仅作为 C，纯前端；决策 48：与 Linux C 共用同一套 F# UI 代码）。
/// 编译目标 net10.0-browser（browser-wasm），由 wwwroot/main.js 引导并渲染进 #out 容器。
module Program =

    [<assembly: SupportedOSPlatform("browser")>]
    do ()

    [<EntryPoint>]
    let main argv =
        task {
            // 强制 WebGL（不准软渲染）：WebGL2 → WebGL1，不回退 Software2D。
            // ES module 会相对 /_framework/ 解析 "./avalonia.js"；只追加版本查询打穿缓存。
            // 不要写成 "./_framework/..."，否则会变成 /_framework/_framework/ 404。
            let options =
                BrowserPlatformOptions(
                    RenderingMode =
                        [| BrowserRenderingMode.WebGL2
                           BrowserRenderingMode.WebGL1 |],
                    FrameworkAssetPathResolver = fun fileName -> $"./{fileName}?v=20260810m"
                )
            do!
                AppBuilder
                    .Configure<App>()
                    .StartBrowserAppAsync("out", options)
        }
        |> ignore
        0
