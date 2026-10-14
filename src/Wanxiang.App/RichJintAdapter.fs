namespace Wanxiang.App

open Wanxiang.UI

/// 把 Wanxiang2.RichJint 的桌面脚本后端转接成界面的 IRichBackend（组合者不引用界面）。
module RichJintAdapter =

    let create () : JintRichBackend * IRichBackend =
        let rich = JintRichBackend()
        rich,
        { new IRichBackend with
            member _.HighlightHtml(code, canonicalLanguage) = rich.HighlightHtml(code, canonicalLanguage)
            member _.MathHtml(tex, displayMode) = rich.MathHtml(tex, displayMode) }
