namespace Wanxiang.UI

open System
open Markdig
open Markdig.Extensions.Mathematics
open Markdig.Extensions.TaskLists
open Markdig.Extensions.Tables
open Markdig.Syntax
open Markdig.Syntax.Inlines

/// 行内片段。
type MdInline =
    | MdText of text: string * bold: bool * italic: bool * strike: bool * code: bool
    | MdLink of text: string * url: string
    | MdImage of alt: string * url: string
    /// TeX 公式。`$…$` 为行内，`$$…$$` 为展示式。
    | MdMath of tex: string * display: bool
    | MdBreak

/// 块级元素。渲染器按块给出各自的排版，而不是把一切压成一段 Run。
type MdBlock =
    | MdHeading of level: int * MdInline list
    | MdParagraph of MdInline list
    /// (缩进层级, 项目符号文本, 内容)
    | MdList of ordered: bool * items: (int * string * MdInline list) list
    | MdTask of items: (bool * MdInline list) list
    | MdQuote of MdBlock list
    | MdCode of language: string * code: string
    | MdRule
    | MdTable of header: MdInline list list * rows: MdInline list list list
    /// 独占一行的展示式公式
    | MdMathBlock of tex: string

/// Markdown → 块级模型。
///
/// 之前整段消息被拍平成一串 `Run`，标题、引用、表格全靠字符前缀假装，
/// 结果长回答完全没有层次。这里保留块结构，交给渲染器做真正的排版。
module Markdown =

    let private pipeline =
        MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseTaskLists()
            .Build()

    let private plainOf (inline_: Inline) : string =
        let rec walk (i: Inline) =
            match i with
            | null -> ""
            | :? LiteralInline as lit -> lit.Content.ToString()
            | :? CodeInline as code -> code.Content
            | :? MathInline as math -> math.Content.ToString()
            | :? LineBreakInline -> " "
            | :? ContainerInline as container -> String.Concat(seq { for child in container -> walk child })
            | _ -> ""
        walk inline_

    let rec private collect (i: Inline) (bold: bool) (italic: bool) (strike: bool) (acc: ResizeArray<MdInline>) =
        match i with
        | null -> ()
        | :? LiteralInline as lit ->
            let text = lit.Content.ToString()
            if text.Length > 0 then acc.Add(MdText(text, bold, italic, strike, false))
        | :? CodeInline as code ->
            if not (String.IsNullOrEmpty code.Content) then
                acc.Add(MdText(code.Content, false, false, false, true))
        | :? LineBreakInline -> acc.Add MdBreak
        | :? MathInline as math ->
            let tex = math.Content.ToString()
            if not (String.IsNullOrWhiteSpace tex) then acc.Add(MdMath(tex, math.DelimiterCount >= 2))
        | :? LinkInline as link ->
            let label = plainOf link
            let url = if isNull link.Url then "" else link.Url
            if link.IsImage then
                acc.Add(MdImage((if String.IsNullOrWhiteSpace label then "图片" else label), url))
            elif String.IsNullOrWhiteSpace url then
                if not (String.IsNullOrEmpty label) then acc.Add(MdText(label, bold, italic, strike, false))
            else
                acc.Add(MdLink((if String.IsNullOrEmpty label then url else label), url))
        | :? EmphasisInline as em ->
            let nextBold = bold || (em.DelimiterChar = '*' || em.DelimiterChar = '_') && em.DelimiterCount >= 2
            let nextItalic = italic || (em.DelimiterChar = '*' || em.DelimiterChar = '_') && em.DelimiterCount = 1
            let nextStrike = strike || em.DelimiterChar = '~'
            for child in em do collect child nextBold nextItalic nextStrike acc
        | :? ContainerInline as container ->
            for child in container do collect child bold italic strike acc
        | _ -> ()

    let private inlinesOf (leaf: LeafBlock) : MdInline list =
        let acc = ResizeArray<MdInline>()
        if not (isNull leaf.Inline) then collect leaf.Inline false false false acc
        // 尾随换行不参与排版
        while acc.Count > 0 && acc[acc.Count - 1] = MdBreak do
            acc.RemoveAt(acc.Count - 1)
        List.ofSeq acc

    let private codeOf (fence: LeafBlock) : string =
        [ for i in 0 .. fence.Lines.Count - 1 -> fence.Lines.Lines[i].ToString().TrimEnd('\r', '\n') ]
        |> String.concat "\n"

    let private cellInlines (cell: TableCell) : MdInline list =
        let acc = ResizeArray<MdInline>()
        for child in cell do
            match child with
            | :? LeafBlock as leaf when not (isNull leaf.Inline) -> collect leaf.Inline false false false acc
            | _ -> ()
        List.ofSeq acc

    let private tableOf (table: Table) : MdBlock =
        let rows = [ for row in table do match row with :? TableRow as r -> r | _ -> () ]
        let cellsOf (row: TableRow) =
            [ for cell in row do
                  match cell with
                  | :? TableCell as c -> cellInlines c
                  | _ -> () ]
        match rows with
        | [] -> MdTable([], [])
        | first :: rest when first.IsHeader -> MdTable(cellsOf first, rest |> List.map cellsOf)
        | all -> MdTable([], all |> List.map cellsOf)

    let rec private blocksOf (container: Block) (depth: int) : MdBlock list =
        match container with
        | :? MathBlock as math ->
            let tex = codeOf math
            if String.IsNullOrWhiteSpace tex then [] else [ MdMathBlock tex ]
        | :? FencedCodeBlock as fence ->
            [ MdCode((if String.IsNullOrWhiteSpace fence.Info then "" else fence.Info), codeOf fence) ]
        | :? CodeBlock as code -> [ MdCode("", codeOf code) ]
        | :? HeadingBlock as heading -> [ MdHeading(heading.Level, inlinesOf heading) ]
        | :? ThematicBreakBlock -> [ MdRule ]
        | :? ParagraphBlock as para ->
            let items = inlinesOf para
            if List.isEmpty items then [] else [ MdParagraph items ]
        | :? QuoteBlock as quote ->
            [ MdQuote(quote |> Seq.collect (fun child -> blocksOf child depth) |> List.ofSeq) ]
        | :? ListBlock as list ->
            let taskItems = ResizeArray<bool * MdInline list>()
            let listItems = ResizeArray<int * string * MdInline list>()
            let nested = ResizeArray<MdBlock>()
            let mutable ordinal =
                match Int32.TryParse list.OrderedStart with
                | true, value -> value
                | _ -> 1
            for item in list do
                match item with
                | :? ListItemBlock as listItem ->
                    let bullet =
                        if list.IsOrdered then
                            let text = sprintf "%d." ordinal
                            ordinal <- ordinal + 1
                            text
                        else
                            "•"
                    let mutable firstLeaf = true
                    for child in listItem do
                        match child with
                        | :? ParagraphBlock as para when firstLeaf ->
                            firstLeaf <- false
                            let content = inlinesOf para
                            let checkedState =
                                if isNull para.Inline then None
                                else
                                    para.Inline
                                    |> Seq.tryPick (function
                                        | :? TaskList as task -> Some task.Checked
                                        | _ -> None)
                            match checkedState with
                            | Some state -> taskItems.Add(state, content)
                            | None -> listItems.Add(depth, bullet, content)
                        | other -> nested.AddRange(blocksOf other (depth + 1))
                | other -> nested.AddRange(blocksOf other depth)
            [ if taskItems.Count > 0 then MdTask(List.ofSeq taskItems)
              if listItems.Count > 0 then MdList(list.IsOrdered, List.ofSeq listItems)
              yield! nested ]
        | :? Table as table -> [ tableOf table ]
        | :? LeafBlock as leaf ->
            let items = inlinesOf leaf
            if List.isEmpty items then [] else [ MdParagraph items ]
        | :? ContainerBlock as container ->
            container |> Seq.collect (fun child -> blocksOf child depth) |> List.ofSeq
        | _ -> []

    /// 解析。任何异常都退化成一段纯文本，绝不让渲染崩掉。
    let parse (raw: string) : MdBlock list =
        if String.IsNullOrEmpty raw then []
        else
            try
                let document = Markdig.Markdown.Parse(raw, pipeline)
                let blocks = document |> Seq.collect (fun block -> blocksOf block 0) |> List.ofSeq
                if List.isEmpty blocks then [ MdParagraph [ MdText(raw, false, false, false, false) ] ] else blocks
            with _ ->
                [ MdParagraph [ MdText(raw, false, false, false, false) ] ]

    /// 提取全部代码块内容（「复制代码」用）。
    let codeBlocks (blocks: MdBlock list) : string list =
        let rec walk (list: MdBlock list) =
            list
            |> List.collect (function
                | MdCode(_, code) -> [ code ]
                | MdQuote inner -> walk inner
                | _ -> [])
        walk blocks
