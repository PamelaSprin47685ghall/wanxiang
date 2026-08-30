namespace Wanxiang.UI

open System
open System.Collections.Generic
open System.Net

/// KaTeX 输出的 HTML 节点。
type KNode =
    | KElement of tag: string * classes: string list * style: Map<string, string> * children: KNode list
    | KText of text: string

/// 解析 KaTeX 的 HTML 输出。
///
/// 为什么解析 HTML 而不是自己排版 TeX：KaTeX 已经把所有位置算成了
/// `height` / `top` / `margin-right` 这些显式的 em 数值，而「比例对不对」
/// 正是数学排版里最难自己做对的部分。这里只负责把它算好的盒子摆到 Avalonia 上。
///
/// 只认 KaTeX 会吐的那点子集，不是通用 HTML 解析器。
module KatexHtml =

    /// 字号档位（KaTeX sizing：size1…size11）。
    /// `reset-size6 size3` 的含义是「从 size6 语境切到 size3」，
    /// 倍率是两档的比值，所以两个类名都得读。
    let private sizeScale =
        [| 0.5; 0.6; 0.7; 0.8; 0.9; 1.0; 1.2; 1.44; 1.728; 2.074; 2.488 |]

    let private sizeIndexOf (name: string) (prefix: string) =
        if name.StartsWith prefix then
            match Int32.TryParse(name.Substring prefix.Length) with
            | true, n when n >= 1 && n <= sizeScale.Length -> Some(n - 1)
            | _ -> None
        else None

    /// 一组 class 里蕴含的字号倍率。
    ///
    /// 只有 `reset-sizeA sizeB` **成对**出现时才是缩放：KaTeX 的 CSS 写作
    /// `.reset-size6.size3 { font-size: 0.7em }`。单独的 `size1`（如
    /// `delimsizing size1`）指的是 KaTeX_Size1 这个**字族**，不是缩放，
    /// 当成缩放会把可伸缩括号缩掉一半。
    let fontScaleOf (classes: string list) =
        let mutable from = None
        let mutable target = None
        for cls in classes do
            match sizeIndexOf cls "reset-size" with
            | Some i -> from <- Some i
            | None ->
                match sizeIndexOf cls "size" with
                | Some i -> target <- Some i
                | None -> ()
        match from, target with
        | Some f, Some t -> sizeScale[t] / sizeScale[f]
        | _ -> 1.0

    let private parseStyle (raw: string) =
        raw.Split ';'
        |> Array.choose (fun decl ->
            match decl.IndexOf ':' with
            | -1 -> None
            | at ->
                let key = decl.Substring(0, at).Trim().ToLowerInvariant()
                let value = decl.Substring(at + 1).Trim()
                if key.Length = 0 || value.Length = 0 then None else Some(key, value))
        |> Map.ofArray

    /// 取属性值。SVG 部分 KaTeX 用的是**单引号**（`d='M95,702…'`），
    /// 只认双引号会把根号与可伸缩括号的路径整条丢掉。
    let private attribute (tag: string) (name: string) =
        let pick (quote: char) =
            let marker = $"{name}={quote}"
            match tag.IndexOf(marker, StringComparison.Ordinal) with
            | -1 -> None
            | at ->
                let from = at + marker.Length
                match tag.IndexOf(quote, from) with
                | -1 -> None
                | until -> Some(tag.Substring(from, until - from))
        match pick '"' with
        | Some value -> Some value
        | None -> pick '\''

    /// em 数值。KaTeX 的长度全部以 em 计，且相对**当前元素**的字号。
    let emOf (style: Map<string, string>) (key: string) =
        match style.TryFind key with
        | None -> None
        | Some raw ->
            let trimmed = raw.Trim()
            let number =
                if trimmed.EndsWith "em" then trimmed.Substring(0, trimmed.Length - 2)
                elif trimmed.EndsWith "px" then trimmed.Substring(0, trimmed.Length - 2)
                else trimmed
            match Double.TryParse(number, Globalization.CultureInfo.InvariantCulture) with
            | true, value -> Some value
            | _ -> None

    /// 只认 span/svg/path 这几种；KaTeX 不吐别的可见标签。
    let parse (html: string) : KNode list =
        let roots = ResizeArray<KNode>()
        // 栈里存「标签名 / class / style / 已收集的子节点」
        let stack = Stack<string * string list * Map<string, string> * ResizeArray<KNode>>()

        let emit (node: KNode) =
            if stack.Count = 0 then roots.Add node
            else
                let (_, _, _, siblings) = stack.Peek()
                siblings.Add node

        let mutable i = 0
        while i < html.Length do
            if html[i] = '<' then
                match html.IndexOf('>', i) with
                | -1 ->
                    emit (KText(WebUtility.HtmlDecode(html.Substring i)))
                    i <- html.Length
                | close ->
                    let raw = html.Substring(i + 1, close - i - 1)
                    if raw.StartsWith "/" then
                        if stack.Count > 0 then
                            let (tag, classes, style, children) = stack.Pop()
                            emit (KElement(tag, classes, style, List.ofSeq children))
                    else
                        let selfClosing = raw.EndsWith "/"
                        let body = if selfClosing then raw.Substring(0, raw.Length - 1) else raw
                        let tag =
                            let cut = body.IndexOfAny [| ' '; '\t'; '\n' |]
                            (if cut < 0 then body else body.Substring(0, cut)).ToLowerInvariant()
                        let classes =
                            attribute body "class"
                            |> Option.map (fun v ->
                                v.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
                            |> Option.defaultValue []
                        let style = attribute body "style" |> Option.map parseStyle |> Option.defaultValue Map.empty
                        // SVG 的尺寸走**属性**而不是 CSS；并进 style 里，
                        // 下游只需要认一套取值方式
                        let style =
                            [ "width"; "height" ]
                            |> List.fold
                                (fun (acc: Map<string, string>) key ->
                                    match attribute body key with
                                    | Some value when not (acc.ContainsKey key) -> acc.Add(key, value)
                                    | _ -> acc)
                                style
                        // path 的 d 也要留下：根号与可伸缩括号是 SVG 画的
                        let style =
                            match attribute body "d" with
                            | Some d -> style.Add("-d", d)
                            | None -> style
                        let style =
                            match attribute body "viewBox" with
                            | Some v -> style.Add("-viewbox", v)
                            | None -> style
                        if selfClosing || tag = "path" || tag = "br" then
                            emit (KElement(tag, classes, style, []))
                        else
                            stack.Push(tag, classes, style, ResizeArray())
                    i <- close + 1
            else
                let next = html.IndexOf('<', i)
                let until = if next < 0 then html.Length else next
                let text = html.Substring(i, until - i)
                if text.Length > 0 then emit (KText(WebUtility.HtmlDecode text))
                i <- until

        // 标签没闭合也要把已收集的内容交出去，绝不整段丢弃
        while stack.Count > 0 do
            let (tag, classes, style, children) = stack.Pop()
            let node = KElement(tag, classes, style, List.ofSeq children)
            if stack.Count = 0 then roots.Add node
            else
                let (_, _, _, siblings) = stack.Peek()
                siblings.Add node

        List.ofSeq roots

    let private hasClass (name: string) (classes: string list) = List.contains name classes

    /// 取出可见的那棵子树。`katex-mathml` 是给读屏用的，画出来会重影。
    let visualSubtree (nodes: KNode list) : KNode option =
        let rec search (node: KNode) =
            match node with
            | KText _ -> None
            | KElement(_, classes, _, children) ->
                if hasClass "katex-html" classes then Some node
                else children |> List.tryPick search
        nodes |> List.tryPick search

    /// 是否展示式（`$$…$$`）。展示式要独占一行并居中。
    let isDisplay (nodes: KNode list) =
        let rec search (node: KNode) =
            match node with
            | KText _ -> false
            | KElement(_, classes, _, children) ->
                hasClass "katex-display" classes || children |> List.exists search
        nodes |> List.exists search
