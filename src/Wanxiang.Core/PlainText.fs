namespace Wanxiang.Core

open System
open System.Globalization
open System.Text
open System.Text.RegularExpressions

/// 把 Markdown 压成单行纯文本。
///
/// 会话标题和侧栏预览是纯文本位面：直接塞原始 Markdown 会露出
/// `###`、`**`、`[文字](链接)` 这些标记，看上去像渲染坏了。
module PlainText =

    /// 围栏代码块整段丢弃：预览里贴一段代码没有信息量。
    let private fencedCode = Regex(@"```[\s\S]*?```|~~~[\s\S]*?~~~", RegexOptions.Compiled)

    /// 图片先于链接处理，否则 `![alt](url)` 会残留一个前导 `!`。
    let private image = Regex(@"!\[(?<alt>[^\]]*)\]\([^)]*\)", RegexOptions.Compiled)
    let private link = Regex(@"\[(?<text>[^\]]*)\]\([^)]*\)", RegexOptions.Compiled)
    let private autoLink = Regex(@"<(?<url>https?://[^>]+)>", RegexOptions.Compiled)

    /// 行首结构标记：标题号、引用角、列表符、有序号、表格分隔行。
    let private leadingMarkers =
        Regex(@"^[ \t]*(?:#{1,6}[ \t]+|>[ \t]?|[-*+][ \t]+|\d+[.)][ \t]+)", RegexOptions.Compiled ||| RegexOptions.Multiline)

    let private tableDivider = Regex(@"^[ \t]*\|?[ \t]*:?-{2,}:?[ \t]*(\|[ \t]*:?-{2,}:?[ \t]*)*\|?[ \t]*$", RegexOptions.Compiled ||| RegexOptions.Multiline)

    let private thematicBreak = Regex(@"^[ \t]*(?:\*{3,}|-{3,}|_{3,})[ \t]*$", RegexOptions.Compiled ||| RegexOptions.Multiline)

    /// 行内强调与代码：只脱标记，保留文字。
    let private emphasis = Regex(@"(\*{1,3}|_{1,3}|~{2})(?=\S)(.*?\S)\1", RegexOptions.Compiled)
    let private inlineCode = Regex(@"`+([^`]+)`+", RegexOptions.Compiled)

    let private whitespace = Regex(@"\s+", RegexOptions.Compiled)

    /// 转成适合单行展示的纯文本。
    let ofMarkdown (raw: string) : string =
        if String.IsNullOrWhiteSpace raw then ""
        else
            let mutable text = raw
            text <- fencedCode.Replace(text, " ")
            text <- image.Replace(text, "${alt}")
            text <- link.Replace(text, "${text}")
            text <- autoLink.Replace(text, "${url}")
            text <- tableDivider.Replace(text, " ")
            text <- thematicBreak.Replace(text, " ")
            text <- leadingMarkers.Replace(text, "")
            text <- inlineCode.Replace(text, "$1")
            text <- emphasis.Replace(text, "$2")
            text <- text.Replace("|", " ")
            whitespace.Replace(text, " ").Trim()

    /// 转成纯文本并按显示宽度截断（CJK 按两格计）。
    /// 使用 StringInfo 遍历字符簇（grapheme clusters / text elements），
    /// 避免在 surrogate pair 或复杂 emoji / ZWJ 序列中间切断导致乱码。
    let summarize (maxChars: int) (raw: string) : string =
        let text = ofMarkdown raw
        if maxChars <= 0 then ""
        else
            let enumerator = StringInfo.GetTextElementEnumerator(text)
            let sb = StringBuilder()
            let mutable count = 0
            let mutable truncated = false
            while enumerator.MoveNext() do
                if count < maxChars then
                    sb.Append(enumerator.GetTextElement()) |> ignore
                    count <- count + 1
                else
                    truncated <- true
            if not truncated then text
            else sb.ToString().TrimEnd() + "…"
