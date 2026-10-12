namespace Wanxiang.Server

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Wanxiang.Core
open Wanxiang.Protocol

/// 全文搜索（W4，`wanxiang-chat` 决策 75）。
///
/// 只读：输入是不可变 `Projection` 快照，输出是一页命中。不建数据库、不建持久索引、
/// 不写日志、不创建文件——内存投影是唯一事实来源，搜索只是它的一次查询。
///
/// 语义：
/// - 查询按空白分词，**全部**词（大小写不敏感）都出现在同一条消息正文里才算命中，
///   与侧栏标题过滤、高亮使用同一套分词（`SidebarText.terms`）。
/// - 只匹配消息自己记录的正文（`text` 内容项）；思维链、工具参数与工具结果不参与。
/// - 只搜每个对话**自己记录**的消息，不搜继承自父对话的前缀：同一条历史消息不会因
///   被多个分叉共享而重复命中。
/// - 已删除对话、已 tombstone 的消息永不出现；归档对话由 `includeArchived` 决定。
/// - 顺序：最近活动的对话优先，同一对话内新消息优先（"最近优先"，预算耗尽时丢的是最老的）。
/// - 预算见 `SearchLimits`：命中数、扫描消息数、单条检查字符数都有上限，
///   触顶时 `truncated = true`，绝不静默丢结果。
module SearchService =

    /// 与 `SidebarText.terms` 同一规则：空格、制表符分词并去重。
    let terms (query: string) : string list =
        if String.IsNullOrWhiteSpace query then []
        else
            query.Trim().Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.distinct
            |> List.ofArray

    /// 提取消息的可搜索正文：只取 `text` 内容项，跳过思维链；单条上限 `maxMessageChars`。
    let textOf (payload: JsonNode) : string =
        let sb = StringBuilder()
        let rec walk (node: JsonNode) =
            if sb.Length < SearchLimits.maxMessageChars then
                match node with
                | null -> ()
                | :? JsonObject as o ->
                    let kind (key: string) =
                        let mutable n: JsonNode = null
                        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then n.GetValue<string>()
                        else null
                    if kind "$type" <> "reasoning" && kind "type" <> "reasoning" then
                        match kind "text" with
                        | null -> ()
                        | t ->
                            if sb.Length > 0 then sb.Append '\n' |> ignore
                            sb.Append t |> ignore
                        let mutable contents: JsonNode = null
                        if o.TryGetPropertyValue("contents", &contents) && not (isNull contents) && contents.GetValueKind() = JsonValueKind.Array then
                            for c in contents.AsArray() do walk c
                | :? JsonArray as a -> for item in a do walk item
                | _ -> ()
        walk payload
        if sb.Length > SearchLimits.maxMessageChars then sb.ToString(0, SearchLimits.maxMessageChars) else sb.ToString()

    /// 命中位置：所有词都存在时，返回最靠前的那个词的 (下标, 长度)。
    let private firstMatch (words: string list) (text: string) : (int * int) option =
        let mutable best = (Int32.MaxValue, 0)
        let mutable all = true
        for w in words do
            if all then
                let i = text.IndexOf(w, StringComparison.OrdinalIgnoreCase)
                if i < 0 then all <- false
                elif i < fst best then best <- (i, w.Length)
        if all then Some best else None

    /// 以命中为中心截取片段：换行压成空格（一对一替换，下标不变），两端不够时加省略号。
    /// 返回 (片段, 命中在片段中的起点)。
    let snippetAround (text: string) (matchStart: int) (matchLength: int) : string * int =
        let total = SearchLimits.snippetChars
        let lead = 30
        let mutable start = max 0 (matchStart - lead)
        if start > 0 && Char.IsLowSurrogate text[start] then start <- start - 1
        let mutable stop = min text.Length (start + max total (matchLength + lead))
        if stop < text.Length && stop > 0 && Char.IsHighSurrogate text[stop - 1] then stop <- stop - 1
        let body = text.Substring(start, stop - start).Replace('\n', ' ').Replace('\r', ' ')
        let prefix = if start > 0 then "…" else ""
        let suffix = if stop < text.Length then "…" else ""
        prefix + body + suffix, prefix.Length + (matchStart - start)

    /// 对内存投影做一次查询。
    let search (proj: Projection) (q: SearchQueryData) : Result<SearchResultsData, string> =
        if q.query.Length > SearchLimits.maxQueryChars then
            Error(sprintf "搜索词太长（上限 %d 个字符），请缩短后重试。" SearchLimits.maxQueryChars)
        else
            let words = terms q.query
            let offset = max 0 q.offset
            let limit = q.limit |> max 1 |> min SearchLimits.maxPageSize
            let items = JsonArray()
            let mutable hits = 0
            let mutable scanned = 0
            let mutable truncated = false
            let mutable hasMore = false
            let mutable stop = List.isEmpty words
            let conversations =
                proj.conversations.Values
                |> Seq.filter (fun c -> not c.deleted && (q.includeArchived || not c.archived))
                |> Seq.sortByDescending (fun c -> c.lastCommitId |> Option.defaultValue 0UL)
            use e = conversations.GetEnumerator()
            while not stop && e.MoveNext() do
                let c = e.Current
                let mutable rest = List.rev c.messages
                while not stop && not (List.isEmpty rest) do
                    let m = rest.Head
                    rest <- rest.Tail
                    if m.deletedAtCommitId.IsNone then
                        if scanned >= SearchLimits.maxScannedMessages then
                            truncated <- true
                            stop <- true
                        else
                            scanned <- scanned + 1
                            let text = textOf m.payloadJson
                            match firstMatch words text with
                            | None -> ()
                            | Some(index, length) ->
                                if hits >= SearchLimits.maxHits then
                                    truncated <- true
                                    stop <- true
                                elif hits >= offset + limit then
                                    hasMore <- true
                                    stop <- true
                                else
                                    if hits >= offset then
                                        let snippet, start = snippetAround text index length
                                        let o = JsonObject()
                                        o["conversationId"] <- c.conversationId.ToString("D")
                                        o["title"] <- c.title
                                        o["archived"] <- c.archived
                                        o["messageId"] <- m.commitId
                                        o["commitId"] <- m.commitId
                                        o["role"] <-
                                            (let mutable r: JsonNode = null
                                             if (match m.payloadJson with
                                                 | :? JsonObject as po -> po.TryGetPropertyValue("role", &r) && not (isNull r) && r.GetValueKind() = JsonValueKind.String
                                                 | _ -> false)
                                             then r.GetValue<string>() else "")
                                        o["snippet"] <- snippet
                                        o["matchStart"] <- start
                                        o["matchLength"] <- length
                                        items.Add o
                                    hits <- hits + 1
            Ok { queryId = q.queryId
                 query = q.query
                 offset = offset
                 items = items
                 hasMore = hasMore
                 truncated = truncated
                 atCommitId = proj.latestCommitId }
