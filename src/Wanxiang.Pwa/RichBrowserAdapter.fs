namespace Wanxiang.Pwa

open Wanxiang.UI

/// 把 Wanxiang2.RichBrowser 的浏览器脚本后端转接成界面的 IRichBackend（组合者不引用界面）。
module RichBrowserAdapter =

    let create () : BrowserRichBackend * IRichBackend =
        let rich = BrowserRichBackend()
        rich,
        { new IRichBackend with
            member _.HighlightHtml(code, canonicalLanguage) = rich.HighlightHtml(code, canonicalLanguage)
            member _.MathHtml(tex, displayMode) = rich.MathHtml(tex, displayMode) }
