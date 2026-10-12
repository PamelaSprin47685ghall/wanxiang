namespace Wanxiang.UI

open System
open System.Text.Json
open System.Text.Json.Nodes

/// 侧栏一行所需的对话摘要（`conversation-list.snapshot` 的解析结果）。
type ConversationSummary = {
    id: Guid
    title: string
    preview: string
    running: bool
    pinned: bool
    archived: bool
    createdAt: DateTimeOffset
    updatedAt: DateTimeOffset
    messageCount: int
    isFork: bool
    providerId: string
    model: string
    lastCommitId: uint64
}

/// 侧栏分组。置顶单独一组，其余按最近活动的时间粒度归组；order 是 UI 稳定键。
type ConversationGroup = {
    order: int
    label: string
    items: ConversationSummary list
}

module ConversationSummary =

    /// 生成中状态的唯一判定（C8：UiControllers 的旧副本收敛到这里）。
    let isGeneratingStatus (status: string) : bool = status = "generating"

    let private str (o: JsonObject) key fallback =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then
            n.GetValue<string>()
        else
            fallback

    let private boolOf (o: JsonObject) key =
        let mutable n: JsonNode = null
        o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.True

    let private intOf (o: JsonObject) key =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
            n.GetValue<int>()
        else
            0

    let private uint64Of (o: JsonObject) key =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
            match n with
            | :? JsonValue as v ->
                match v.TryGetValue<uint64>() with
                | true, value -> value
                | _ -> 0UL
            | _ -> 0UL
        else
            0UL

    let private parseItem (o: JsonObject) : ConversationSummary option =
        match Guid.TryParse(str o "conversationId" "") with
        | false, _ ->
            // 无有效 id 的行无法被选中或打开：如实丢弃并记 stderr，绝不静默（D6）。
            eprintfn "wanxiang: dropping conversation row with unparseable id '%s'" (str o "conversationId" "")
            None
        | true, id ->
            let providerId, model =
                let mutable n: JsonNode = null
                if o.TryGetPropertyValue("config", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Object then
                    let c = n.AsObject()
                    str c "provider" "", str c "model" ""
                else
                    "", ""
            let createdAt =
                match DateTimeOffset.TryParse(str o "createdAt" "") with
                | true, value -> value
                // 坏时间戳回退到 Unix 纪元：排最后、永不进“今天”，绝不冒充刚建的对话（D6）。
                | _ -> DateTimeOffset.UnixEpoch
            let updatedAt =
                match DateTimeOffset.TryParse(str o "updatedAt" "") with
                | true, value -> value
                | _ -> createdAt
            Some
                { id = id
                  title = (let t = str o "title" "" in if String.IsNullOrWhiteSpace t then "未命名对话" else t)
                  preview = str o "lastMessage" ""
                  running = isGeneratingStatus (str o "runtimeState" "idle")
                  pinned = boolOf o "pinned"
                  archived = boolOf o "archived"
                  createdAt = createdAt
                  updatedAt = updatedAt
                  messageCount = intOf o "messageCount"
                  isFork = boolOf o "isFork"
                  providerId = providerId
                  model = model
                  lastCommitId = uint64Of o "lastCommitId" }

    let parseList (items: JsonArray) : ConversationSummary list =
        [ for item in items do
              match item with
              | :? JsonObject as o ->
                  match parseItem o with
                  | Some summary -> summary
                  | None -> ()
              | _ -> () ]

    /// 搜索过滤：标题、预览、模型与模型服务均参与匹配，支持多关键词空格分词。
    let matches (query: string) (summary: ConversationSummary) =
        if String.IsNullOrWhiteSpace query then true
        else
            let terms = query.Trim().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            terms |> Array.forall (fun term ->
                summary.title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || summary.preview.Contains(term, StringComparison.OrdinalIgnoreCase)
                || summary.model.Contains(term, StringComparison.OrdinalIgnoreCase)
                || summary.providerId.Contains(term, StringComparison.OrdinalIgnoreCase))

    let private sortDescending (items: ConversationSummary list) =
        items
        |> List.sortWith (fun a b ->
            let c = b.updatedAt.CompareTo(a.updatedAt)
            if c <> 0 then c
            else
                let c2 = b.lastCommitId.CompareTo(a.lastCommitId)
                if c2 <> 0 then c2
                else b.createdAt.CompareTo(a.createdAt))

    /// 置顶/归档组按权威提交序号优先、再按最近活动排：
    /// 时间桶内仍走 sortDescending（updatedAt 优先），投影语义不动（D7）。
    let private sortPinnedArchived (items: ConversationSummary list) =
        items
        |> List.sortWith (fun a b ->
            let c = b.lastCommitId.CompareTo(a.lastCommitId)
            if c <> 0 then c
            else
                let c2 = b.updatedAt.CompareTo(a.updatedAt)
                if c2 <> 0 then c2
                else b.createdAt.CompareTo(a.createdAt))

    /// 一次活动落在哪个时间桶。用最近活动而非创建时间，符合「最近用过的排前面」直觉。
    let bucketOf (now: DateTimeOffset) (summary: ConversationSummary) =
        let updated = summary.updatedAt.ToOffset(now.Offset)
        let days = (now.Date - updated.Date).TotalDays
        if days < 1.0 then 0, "今天"
        elif days < 2.0 then 1, "昨天"
        elif days < 8.0 then 2, "过去 7 天"
        elif days < 31.0 then 3, "过去 30 天"
        elif updated.Year = now.Year then 4, "今年"
        else 5, "更早"

    /// 分组：置顶置首，归档单独一组放最后（默认隐藏时由调用方先过滤）。
    let group (now: DateTimeOffset) (summaries: ConversationSummary list) : ConversationGroup list =
        let pinned = summaries |> List.filter (fun s -> s.pinned && not s.archived)
        let archived = summaries |> List.filter (fun s -> s.archived)
        let regular = summaries |> List.filter (fun s -> not s.pinned && not s.archived)
        let byBucket =
            regular
            |> List.groupBy (bucketOf now)
            |> List.sortBy (fun ((order, _), _) -> order)
            |> List.map (fun ((order, label), items) ->
                { order = order
                  label = label
                  items = sortDescending items })
        [ if not (List.isEmpty pinned) then
              { order = -1
                label = "置顶"
                items = sortPinnedArchived pinned }
          yield! byBucket
          if not (List.isEmpty archived) then
              { order = 100
                label = "已归档"
                items = sortPinnedArchived archived } ]

// ---------------------------------------------------------------------------
// W4 对话库：分叉谱系、同一消息的版本切换、全文搜索命中（决策 201–205）。
// 全部是 `conversation-list.snapshot` / `search.results` 的**只读派生**，不新增任何持久事实；
// 与 `ConversationSummary` 记录分开放，原记录字段不变（旧构造点不受影响）。
// ---------------------------------------------------------------------------

/// 一个分叉的谱系（根对话没有）。字段缺省 = 旧主机没发，界面退回“只显示分叉标记”。
type ForkLineage = {
    parentId: Guid
    /// 分叉点：父对话里最后一条被继承的消息（None = 从第一条消息处分出）
    forkBaseMessageId: uint64 option
    /// 分叉里自己记录的第一条消息（被编辑后的那条）
    forkFirstMessageId: uint64 option
    parentTitle: string option
    parentDeleted: bool
    /// 被编辑的是父对话的第几条消息（1 起）
    forkOrdinal: int option
}

/// 顶栏面包屑。parentAvailable = false 时不可点击回到父对话（已删除）。
type ForkBreadcrumb = {
    parentId: Guid
    parentAvailable: bool
    text: string
    /// 无障碍与悬停提示
    description: string
}

/// 同一条消息位置上的全部版本（原文 + 各分叉）。`index` 是 0 起的当前位置，界面显示 `index+1 / count`。
type SiblingVersions = {
    index: int
    count: int
    /// 与版本一一对应的对话 id
    targets: Guid list
}

/// 一条全文搜索命中（`search.results.items` 的一项）。
type SearchHit = {
    conversationId: Guid
    title: string
    archived: bool
    /// 消息标识 = 消息 commitId
    messageId: uint64
    role: string
    snippet: string
    matchStart: int
    matchLength: int
}

/// 一页搜索结果的界面状态。
type SearchPageState = {
    queryId: Guid
    query: string
    hits: SearchHit list
    hasMore: bool
    /// 预算触顶，结果不完整——界面必须说出来
    truncated: bool
}

/// 数字节点的宽容读取：线上解析出的节点与进程内用 int / int64 构造的节点都能读（后者 `TryGetValue<uint64>` 会失败）。
module internal JsonNumbers =
    let tryUInt64 (n: JsonNode) : uint64 option =
        match n with
        | :? JsonValue as v ->
            match v.TryGetValue<uint64>() with
            | true, value -> Some value
            | _ ->
                match v.TryGetValue<int64>() with
                | true, value when value >= 0L -> Some(uint64 value)
                | _ ->
                    match v.TryGetValue<int>() with
                    | true, value when value >= 0 -> Some(uint64 value)
                    | _ -> None
        | _ -> None

module ForkLineage =

    let private tryStr (o: JsonObject) key =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then Some(n.GetValue<string>()) else None

    let private tryU64 (o: JsonObject) key =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
            JsonNumbers.tryUInt64 n
        else None

    let private tryBool (o: JsonObject) key =
        let mutable n: JsonNode = null
        o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.True

    /// 解析快照里每一行的谱系；没有 `parentId`（根对话、旧主机）的行不入表。
    let parse (items: JsonArray) : Map<Guid, ForkLineage> =
        items
        |> Seq.choose (fun item ->
            match item with
            | :? JsonObject as o ->
                match tryStr o "conversationId" |> Option.bind (fun s -> match Guid.TryParse s with | true, g -> Some g | _ -> None),
                      tryStr o "parentId" |> Option.bind (fun s -> match Guid.TryParse s with | true, g -> Some g | _ -> None) with
                | Some id, Some parentId ->
                    Some(
                        id,
                        { parentId = parentId
                          forkBaseMessageId = tryU64 o "forkBaseMessageId"
                          forkFirstMessageId = tryU64 o "forkFirstMessageId"
                          parentTitle = tryStr o "parentTitle"
                          parentDeleted = tryBool o "parentDeleted"
                          forkOrdinal = tryU64 o "forkOrdinal" |> Option.map int })
                | _ -> None
            | _ -> None)
        |> Map.ofSeq

    /// 面包屑文案。父对话已删除时只说“原对话已删除”，该分叉仍是完整可用的对话（决策 202）。
    let breadcrumb (summaries: ConversationSummary list) (lineage: Map<Guid, ForkLineage>) (conversationId: Guid) : ForkBreadcrumb option =
        match lineage.TryFind conversationId with
        | None -> None
        | Some l ->
            let parent = summaries |> List.tryFind (fun s -> s.id = l.parentId)
            let available = parent.IsSome && not l.parentDeleted
            if not available then
                Some
                    { parentId = l.parentId
                      parentAvailable = false
                      text = "原对话已删除"
                      description = "这个分叉的原对话已被删除，分叉本身仍可正常使用。" }
            else
                let title =
                    match parent with
                    | Some p -> p.title
                    | None -> l.parentTitle |> Option.defaultValue "原对话"
                let text =
                    match l.forkOrdinal with
                    | Some n -> sprintf "来自「%s」第 %d 条消息的分叉" title n
                    | None -> sprintf "来自「%s」的分叉" title
                Some
                    { parentId = l.parentId
                      parentAvailable = true
                      text = text
                      description = text + "，点击回到原对话" }

    /// 同一消息位置的版本。`messageCommitId` 是当前对话里这条用户消息，`prevCommitId` 是它前一条消息（第一条为 None）。
    /// - 这条消息是某个分叉自己的第一条消息（被编辑过的那条）：版本 = 父对话原文 + 同一分叉点的全部分叉；
    /// - 否则：版本 = 当前对话 + 以当前对话为父、分叉点正好在 `prevCommitId` 的全部分叉。
    /// 已删除的对话不在摘要里，自然不参与；不足 2 个版本时返回 None（不显示控件）。
    let siblings
        (summaries: ConversationSummary list)
        (lineage: Map<Guid, ForkLineage>)
        (conversationId: Guid)
        (messageCommitId: uint64)
        (prevCommitId: uint64 option)
        : SiblingVersions option =
        let byId = summaries |> List.map (fun s -> s.id, s) |> dict
        let owner, after, isForkOwn =
            match lineage.TryFind conversationId with
            | Some l when l.forkFirstMessageId = Some messageCommitId -> l.parentId, l.forkBaseMessageId, true
            | _ -> conversationId, prevCommitId, false
        let forks =
            lineage
            |> Map.toList
            |> List.filter (fun (id, l) -> l.parentId = owner && l.forkBaseMessageId = after && byId.ContainsKey id)
            // 创建顺序：被编辑那条消息的 commitId 单调递增（缺省排最后），再按创建时间、id 稳定
            |> List.sortBy (fun (id, l) -> defaultArg l.forkFirstMessageId UInt64.MaxValue, byId[id].createdAt, id)
            |> List.map fst
        let originalAvailable =
            byId.ContainsKey owner
            && (not isForkOwn || not (lineage.TryFind conversationId |> Option.exists (fun l -> l.parentDeleted)))
        let targets = (if originalAvailable then [ owner ] else []) @ forks
        let index = if isForkOwn then targets |> List.tryFindIndex ((=) conversationId) else Some 0
        match index with
        | Some i when List.length targets >= 2 -> Some { index = i; count = List.length targets; targets = targets }
        | _ -> None

module SearchHit =

    let private str (o: JsonObject) key =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then n.GetValue<string>() else ""

    let private num (o: JsonObject) key =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
            JsonNumbers.tryUInt64 n |> Option.defaultValue 0UL
        else 0UL

    /// 解析命中；没有有效对话 id 的项无法打开，丢弃。
    let parse (items: JsonArray) : SearchHit list =
        [ for item in items do
              match item with
              | :? JsonObject as o ->
                  match Guid.TryParse(str o "conversationId") with
                  | true, id ->
                      let snippet = str o "snippet"
                      // 越界的高亮范围一律收敛到片段内，绝不抛异常
                      let start = int (min (num o "matchStart") (uint64 snippet.Length))
                      let length = int (min (num o "matchLength") (uint64 (snippet.Length - start)))
                      yield
                          { conversationId = id
                            title = str o "title"
                            archived = (let mutable n: JsonNode = null in o.TryGetPropertyValue("archived", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.True)
                            messageId = num o "messageId"
                            role = str o "role"
                            snippet = snippet
                            matchStart = start
                            matchLength = length }
                  | _ -> ()
              | _ -> () ]

    /// 命中片段切成「前 / 命中 / 后」三段，供界面着色。
    let split (hit: SearchHit) : string * string * string =
        let s = hit.snippet
        s.Substring(0, hit.matchStart), s.Substring(hit.matchStart, hit.matchLength), s.Substring(hit.matchStart + hit.matchLength)

    /// 片段里的 Markdown 记号给人看是噪音（视觉走查 12 号截图：`### 关于…`、`**Markdown 渲染**`）。
    /// 只去掉标题井号、粗斜体星号/下划线对、行内代码反引号，并把换行折成空格；文字本身不动。
    let plainSnippet (text: string) : string =
        if String.IsNullOrEmpty text then text
        else
            let t = Text.RegularExpressions.Regex.Replace(text, @"(^|\n)\s{0,3}#{1,6}\s+", "$1")
            let t = Text.RegularExpressions.Regex.Replace(t, @"(\*\*|__|`+)", "")
            let t = Text.RegularExpressions.Regex.Replace(t, @"\s*\n\s*", " ")
            t

    /// 片段拆成「前 / 命中 / 后」三段，并各自去掉 Markdown 记号（命中位置不受影响）。
    let splitPlain (hit: SearchHit) : string * string * string =
        let before, matched, after = split hit
        plainSnippet before, matched, plainSnippet after

    /// 截断/不完整时给用户的说明；None = 结果完整。
    let truncationNotice (page: SearchPageState) : string option =
        if page.truncated then Some "结果已截断：只显示了最近的一部分匹配，请换更具体的词。" else None

/// 打开对话后要滚动到的消息。
type RevealTarget =
    /// 指定消息（搜索命中、分叉里被编辑的那条）
    | RevealMessage of uint64
    /// “某条消息的下一条”（切回原文版本：原对话里分叉点之后的那一条；None = 第一条）
    | RevealAfter of uint64 option

module RevealTarget =

    /// 在对话已加载的消息 commitId（升序）里解析出具体目标；None = 目标不在已加载窗口。
    let resolve (target: RevealTarget) (loadedCommits: uint64 list) : uint64 option =
        match target with
        | RevealMessage c -> if List.contains c loadedCommits then Some c else None
        | RevealAfter None -> List.tryHead loadedCommits
        | RevealAfter(Some prev) ->
            match List.tryFindIndex ((=) prev) loadedCommits with
            | Some i when i + 1 < List.length loadedCommits -> Some(List.item (i + 1) loadedCommits)
            | _ -> None
