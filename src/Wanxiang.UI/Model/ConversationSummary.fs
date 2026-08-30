namespace Wanxiang.UI

open System
open System.Text.Json
open System.Text.Json.Nodes

/// 侧栏一行所需的会话摘要（`conversation-list.snapshot` 的解析结果）。
type ConversationSummary = {
    id: Guid
    title: string
    preview: string
    running: bool
    pinned: bool
    archived: bool
    createdAt: DateTimeOffset
    messageCount: int
    isFork: bool
    providerId: string
    model: string
    lastCommitId: uint64
}

/// 侧栏分组。置顶单独一组，其余按最近活动的时间粒度归组。
type ConversationGroup = {
    label: string
    items: ConversationSummary list
}

module ConversationSummary =

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
        | false, _ -> None
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
                | _ -> DateTimeOffset.UtcNow
            Some
                { id = id
                  title = (let t = str o "title" "" in if String.IsNullOrWhiteSpace t then "未命名会话" else t)
                  preview = str o "lastMessage" ""
                  running = str o "runtimeState" "idle" = "generating"
                  pinned = boolOf o "pinned"
                  archived = boolOf o "archived"
                  createdAt = createdAt
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

    /// 搜索过滤：标题与预览都参与，不区分大小写。
    let matches (query: string) (summary: ConversationSummary) =
        if String.IsNullOrWhiteSpace query then true
        else
            let q = query.Trim()
            summary.title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || summary.preview.Contains(q, StringComparison.OrdinalIgnoreCase)

    /// 一次活动落在哪个时间桶。用最近活动而非创建时间，符合「最近用过的排前面」直觉。
    let private bucketOf (now: DateTimeOffset) (summary: ConversationSummary) =
        let days = (now.Date - summary.createdAt.ToLocalTime().Date).TotalDays
        if days < 1.0 then 0, "今天"
        elif days < 2.0 then 1, "昨天"
        elif days < 8.0 then 2, "过去 7 天"
        elif days < 31.0 then 3, "过去 30 天"
        elif summary.createdAt.ToLocalTime().Year = now.Year then 4, "今年"
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
            |> List.map (fun ((_, label), items) ->
                { label = label
                  items = items |> List.sortByDescending (fun s -> s.lastCommitId) })
        [ if not (List.isEmpty pinned) then
              { label = "置顶"
                items = pinned |> List.sortByDescending (fun s -> s.lastCommitId) }
          yield! byBucket
          if not (List.isEmpty archived) then
              { label = "已归档"
                items = archived |> List.sortByDescending (fun s -> s.lastCommitId) } ]
