namespace Wanxiang.UI

open System
open System.Collections.Generic
open System.Net
open System.Text
open System.Threading.Tasks

/// 代码片段的着色类别。
///
/// 这一层刻意比 hljs 的 scope 粗：hljs 有近五十种 scope，全部各给一色
/// 会让代码块变成调色板。这里收敛到十二类，映射表在 `Highlight.kindOfScope`。
type CodeTokenKind =
    | CodePlain
    | CodeKeyword
    | CodeString
    | CodeComment
    | CodeNumber
    | CodeType
    | CodeFunction
    | CodeMeta
    | CodeVariable
    | CodeOperator
    | CodeAddition
    | CodeDeletion

type CodeToken = { text: string; kind: CodeTokenKind }

/// 代码着色：把 highlight.js 的 HTML 输出翻成一串带类别的文本片段。
///
/// 语法本身完全交给 hljs（90 种语言、多年打磨的语法定义），这里只做两件事：
/// 解析它吐出的 span 嵌套，以及把 scope 收敛到本项目的配色类别。
/// 任何一步失败都退化为整段纯文本——渲染绝不能因为着色失败而崩。
module Highlight =

    /// hljs scope → 本项目类别。
    ///
    /// hljs 用 `class="hljs-title function_"` 表达 `title.function` 这种子 scope，
    /// 所以要先还原成点号路径，再按「先精确、后退到首段」查表。
    let kindOfScope (scope: string) : CodeTokenKind option =
        match scope with
        | "keyword" | "literal" | "template-tag" -> Some CodeKeyword
        | "type" | "built_in" | "class" | "tag" | "name" | "selector-tag"
        | "title.class" | "title.class.inherited" -> Some CodeType
        | "string" | "regexp" | "char" | "char.escape" | "formula" | "meta.string" -> Some CodeString
        | "comment" | "quote" | "doctag" -> Some CodeComment
        | "number" -> Some CodeNumber
        | "title" | "title.function" | "title.function.invoke" | "section" -> Some CodeFunction
        | "meta" | "meta.prompt" | "meta.keyword" | "link" | "code" -> Some CodeMeta
        | "variable" | "variable.language" | "variable.constant" | "attr" | "attribute"
        | "property" | "symbol" | "params" | "subst" | "template-variable" | "bullet"
        | "selector-id" | "selector-class" | "selector-attr" | "selector-pseudo" -> Some CodeVariable
        | "operator" | "punctuation" -> Some CodeOperator
        | "addition" -> Some CodeAddition
        | "deletion" -> Some CodeDeletion
        | _ -> None

    /// `hljs-title function_` → `title.function`
    let private scopeOfClassAttribute (classes: string) =
        classes.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun cls ->
            let trimmed = if cls.StartsWith "hljs-" then cls.Substring 5 else cls
            trimmed.TrimEnd '_')
        |> Array.filter (fun s -> s.Length > 0)
        |> String.concat "."

    let private kindOfClassAttribute (classes: string) =
        let scope = scopeOfClassAttribute classes
        match kindOfScope scope with
        | Some kind -> Some kind
        // `title.function.invoke` 这类未登记的深层子 scope 退到首段（`title`）
        | None ->
            match scope.IndexOf '.' with
            | -1 -> None
            | cut -> kindOfScope (scope.Substring(0, cut))

    /// 解析 hljs 的 span 嵌套。内层 scope 优先：`string` 里嵌 `subst` 时，
    /// 插值部分该按变量着色，而不是继续当字符串。
    let parseHtml (html: string) : CodeToken list =
        let tokens = ResizeArray<CodeToken>()
        let buffer = StringBuilder()
        let stack = ResizeArray<CodeTokenKind>()
        let currentKind () = if stack.Count = 0 then CodePlain else stack[stack.Count - 1]

        let flush () =
            if buffer.Length > 0 then
                tokens.Add { text = WebUtility.HtmlDecode(buffer.ToString()); kind = currentKind () }
                buffer.Clear() |> ignore

        let mutable i = 0
        while i < html.Length do
            if html[i] = '<' then
                let close = html.IndexOf('>', i)
                if close < 0 then
                    // 标签没闭合：剩下的当文本，绝不丢内容
                    buffer.Append(html.Substring i) |> ignore
                    i <- html.Length
                else
                    let tag = html.Substring(i + 1, close - i - 1)
                    flush ()
                    if tag.StartsWith "/" then
                        if stack.Count > 0 then stack.RemoveAt(stack.Count - 1)
                    else
                        let kind =
                            let marker = "class=\""
                            match tag.IndexOf marker with
                            | -1 -> None
                            | at ->
                                let from = at + marker.Length
                                match tag.IndexOf('"', from) with
                                | -1 -> None
                                | until -> kindOfClassAttribute (tag.Substring(from, until - from))
                        // 未识别的 scope 继承外层，而不是打回纯文本
                        stack.Add(defaultArg kind (currentKind ()))
                    i <- close + 1
            else
                let next = html.IndexOf('<', i)
                let until = if next < 0 then html.Length else next
                buffer.Append(html, i, until - i) |> ignore
                i <- until
        flush ()

        // 合并同类相邻片段：每个 Run 都是一次文本整形，能省则省
        let merged = ResizeArray<CodeToken>()
        for token in tokens do
            if token.text.Length > 0 then
                if merged.Count > 0 && merged[merged.Count - 1].kind = token.kind then
                    let last = merged[merged.Count - 1]
                    merged[merged.Count - 1] <- { last with text = last.text + token.text }
                else
                    merged.Add token
        List.ofSeq merged

    let private cache = RichBackend.Cache<struct (string * string), CodeToken list>(192)

    let private ready = Event<unit>()

    /// 后台算完一段着色时触发。视图据此丢弃卡片缓存、重画一次。
    /// 事件在后台线程上触发，订阅方自己负责切回 UI 线程。
    let Ready = ready.Publish

    /// 正在后台着色的代码，避免同一段被反复排队
    let private inFlight = HashSet<struct (string * string)>()
    let private gate = obj ()

    /// 通知窗口（毫秒）。
    ///
    /// 打开一个含二十个代码块的旧会话会同时排出二十个后台任务，
    /// 各自算完各自触发一次全量重画 —— 屏幕会连抖几秒。
    /// 一个窗口内的完成合并成一次通知。
    [<Literal>]
    let private notifyWindowMs = 120

    let mutable private notifyScheduled = false

    /// 合并通知：窗口内第一个完成负责安排，其余搭车。
    let private notifyOnce () =
        let claimed =
            lock gate (fun () ->
                if notifyScheduled then false
                else
                    notifyScheduled <- true
                    true)
        if claimed then
            Task.Run(fun () ->
                Task.Delay(notifyWindowMs).GetAwaiter().GetResult()
                lock gate (fun () -> notifyScheduled <- false)
                ready.Trigger())
            |> ignore

    let private plainOf (code: string) = [ { text = code; kind = CodePlain } ]

    let private compute (backend: IRichBackend) (canonical: string) (code: string) =
        match backend.HighlightHtml(code, canonical) with
        | Some html ->
            match parseHtml html with
            | [] -> plainOf code
            | tokens -> tokens
        | None -> plainOf code

    /// 排到后台去算。
    ///
    /// 桌面端跑的是纯托管 JS 解释器：50 行代码约 140ms，1000 行约 0.9s，
    /// 5000 行要 6 秒。这些若发生在 UI 线程上就是整窗冻结，
    /// 所以先给纯文本，算好了再让视图重画一次。
    let private enqueue (backend: IRichBackend) (key: struct (string * string)) =
        let claimed = lock gate (fun () -> inFlight.Add key)
        if claimed then
            let struct (canonical, code) = key
            Task.Run(fun () ->
                let mutable ok = false
                try
                    cache.Set(key, compute backend canonical code)
                    ok <- true
                finally
                    lock gate (fun () -> inFlight.Remove key |> ignore)
                if ok then notifyOnce ())
            |> ignore

    /// 着色。`allowHighlight = false`（流式进行中）时直接给纯文本：
    /// 未写完的代码本来就着不准，而每个 token 都重新着色一遍会把机器点着。
    ///
    /// 语言认不出、后端没装、或 hljs 抛了，同样退化为整段纯文本。
    let tokenize (allowHighlight: bool) (language: string) (code: string) : CodeToken list =
        if String.IsNullOrEmpty code then []
        else
            match RichAssets.resolveLanguage language, RichBackend.current () with
            | Some canonical, Some backend when allowHighlight ->
                let key = struct (canonical, code)
                match cache.TryGet key with
                | Some hit -> hit
                | None ->
                    // 浏览器端是宿主 JS 引擎，比解释器快两个数量级，同步做即可；
                    // 浏览器里也没有可用的后台线程。
                    if OperatingSystem.IsBrowser() then
                        let tokens = compute backend canonical code
                        cache.Set(key, tokens)
                        tokens
                    else
                        enqueue backend key
                        plainOf code
            | _ -> plainOf code
