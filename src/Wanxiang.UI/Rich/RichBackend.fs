namespace Wanxiang.UI

/// 富文本渲染所需的脚本能力。桌面端由 Jint 提供，浏览器端由宿主 JS 引擎提供。
///
/// 两端都只暴露「文本进、HTML 出」：布局与绘制全在 Avalonia 侧共享，
/// 所以两端的公式与代码长得完全一样。
type IRichBackend =
    /// hljs 的 `<span class="hljs-…">` HTML。失败时返回 None。
    abstract HighlightHtml: code: string * canonicalLanguage: string -> string option
    /// KaTeX 的 HTML。失败时返回 None。
    abstract MathHtml: tex: string * displayMode: bool -> string option


/// 后端注册处。桌面入口装 Jint 实现，PWA 入口装浏览器实现；
/// 没装时代码块与公式退化为纯文本，功能缺失但绝不崩。
module RichBackend =

    let mutable private backend: IRichBackend option = None

    let install (value: IRichBackend) = backend <- Some value
    let current () = backend
