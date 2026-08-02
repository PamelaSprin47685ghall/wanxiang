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
            do!
                AppBuilder
                    .Configure<App>()
                    .StartBrowserAppAsync("out")
        }
        |> ignore
        0
