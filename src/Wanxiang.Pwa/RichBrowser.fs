namespace Wanxiang.Pwa

open System.Collections.Generic
open Wanxiang.Interop
open Wanxiang.UI

/// PWA 的脚本后端：直接用浏览器自带的 JS 引擎跑 highlight.js 与 KaTeX。
///
/// 浏览器里没有必要再背一个解释器（桌面端用的 Jint 约 1MB 托管代码），
/// 宿主引擎又快得多。脚本正文仍来自 .NET 侧的清单资源，
/// 所以两端用的是同一份 hljs / KaTeX，不会出现「浏览器和桌面着色不一样」。
type BrowserRichBackend() =

    let mutable hljsLoaded = false
    let mutable katexLoaded = false
    let onDemandLoaded = HashSet<string>()

    let ensureHljs () =
        if not hljsLoaded then
            BrowserBridge.RichLoad(RichAssets.hljsCoreScript ())
            hljsLoaded <- true

    let ensureKatex () =
        if not katexLoaded then
            BrowserBridge.RichLoad(RichAssets.katexScript ())
            katexLoaded <- true

    let ensureLanguage (canonical: string) =
        if RichAssets.requiresLoad canonical && not (onDemandLoaded.Contains canonical) then
            BrowserBridge.RichLoad(RichAssets.hljsLanguageScript canonical)
            onDemandLoaded.Add canonical |> ignore

    /// 预热。浏览器引擎解析这两个库只要几十毫秒，启动时顺手做掉。
    member _.Warmup() =
        try
            ensureHljs ()
            ensureKatex ()
        with _ -> ()

    interface IRichBackend with

        member _.HighlightHtml(code, canonicalLanguage) =
            try
                ensureHljs ()
                ensureLanguage canonicalLanguage
                match BrowserBridge.RichHighlight(code, canonicalLanguage) with
                | "" -> None
                | html -> Some html
            with _ -> None

        member _.MathHtml(tex, displayMode) =
            try
                ensureKatex ()
                match BrowserBridge.RichMath(tex, displayMode) with
                | "" -> None
                | html -> Some html
            with _ -> None
