namespace Wanxiang.Server

open System.Text.Json
open System.Text.Json.Nodes
open Wanxiang.Core
open Wanxiang.Protocol

/// 服务器模型辅助：投影 → 线上视图。
module ServerModel =

    /// 从 Agent Framework 消息 JSON 中提取文本（用于会话摘要，决策 125）。
    let rec private messageTextOf (node: System.Text.Json.Nodes.JsonNode) (sb: System.Text.StringBuilder) : unit =
        match node with
        | null -> ()
        | :? JsonObject as o ->
            // 思维链（reasoning）不并入会话摘要预览——预览应该让人能识别对话主题
            let mutable typeNode: JsonNode = null
            let isReasoning =
                (o.TryGetPropertyValue("$type", &typeNode) && not (isNull typeNode) && typeNode.GetValueKind() = JsonValueKind.String && typeNode.GetValue<string>() = "reasoning")
                || (let mutable tn2: JsonNode = null in
                    o.TryGetPropertyValue("type", &tn2) && not (isNull tn2) && tn2.GetValueKind() = JsonValueKind.String && tn2.GetValue<string>() = "reasoning")
            if not isReasoning then
                let mutable textNode: JsonNode = null
                if o.TryGetPropertyValue("text", &textNode) && not (isNull textNode) && textNode.GetValueKind() = JsonValueKind.String then
                    sb.Append(textNode.GetValue<string>()) |> ignore
                let mutable contentsNode: JsonNode = null
                if o.TryGetPropertyValue("contents", &contentsNode) && not (isNull contentsNode) && contentsNode.GetValueKind() = JsonValueKind.Array then
                    for c in contentsNode.AsArray() do messageTextOf c sb
        | :? JsonArray as arr ->
            for item in arr do messageTextOf item sb
        | _ -> ()

    let private lastMessageText (proj: Projection) (conv: Conversation) : string =
        match Projection.effectiveMessages proj conv |> List.rev |> List.tryHead with
        | None -> ""
        | Some m ->
            let sb = System.Text.StringBuilder()
            messageTextOf m.payloadJson sb
            PlainText.summarize 100 (sb.ToString())

    /// 会话列表摘要（决策 125：含最后可见消息摘要、当前运行状态、有效配置摘要，不含完整消息正文）。
    let conversationListItems (proj: Projection) (runtimeStateOf: System.Guid -> string) : JsonArray =
        let arr = JsonArray()
        for c in Projection.conversationList proj do
            let o = JsonObject()
            o["conversationId"] <- c.conversationId.ToString("D")
            o["title"] <- c.title
            o["lastCommitId"] <- c.lastCommitId |> Option.defaultValue 0UL
            o["deleted"] <- c.deleted
            o["pinned"] <- c.pinned
            o["archived"] <- c.archived
            o["createdAt"] <- c.createdAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            o["messageCount"] <- (Projection.effectiveMessages proj c |> List.length)
            o["isFork"] <- c.parent.IsSome
            o["lastMessage"] <- lastMessageText proj c
            o["runtimeState"] <- runtimeStateOf c.conversationId
            let cfg = JsonObject()
            cfg["provider"] <- c.config.provider
            cfg["model"] <- c.config.model
            o["config"] <- cfg
            arr.Add o
        arr

    /// 会话完整快照消息数组（决策 27/79：每条消息带全局 commitId 作为消息标识）。
    let conversationMessages (proj: Projection) (conv: Conversation) : JsonArray =
        let arr = JsonArray()
        for m in Projection.effectiveMessages proj conv do
            let o = JsonObject()
            o["commitId"] <- m.commitId
            o["committedAt"] <- m.committedAtUtc.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            o["payload"] <- m.payloadJson.DeepClone()
            arr.Add o
        arr

    /// 快照尾部消息（Q127/P1-2：长会话快照只携带尾部，避免大帧；返回 items、最早 commitId、是否还有更早）。
    /// limit <= 0 表示不截断。
    let conversationMessagesTail (proj: Projection) (conv: Conversation) (limit: int) : JsonArray * CommitId * bool =
        let all = conversationMessages proj conv
        if limit <= 0 || all.Count <= limit then
            all, 0UL, false
        else
            let tail = JsonArray()
            for i in all.Count - limit .. all.Count - 1 do
                tail.Add (all[i].DeepClone())
            let earliest =
                match tail[0] with
                | :? JsonObject as o ->
                    let mutable c: JsonNode = null
                    if o.TryGetPropertyValue("commitId", &c) && c <> null then c.GetValue<uint64>() else 0UL
                | _ -> 0UL
            tail, earliest, true

    /// 历史分页切片（Q127：按 commitID 反向；页边界 = beforeCommitId 之前的稳定 ID）。
    /// 返回 (items 升序数组, hasMore)。
    let historyPageItems (proj: Projection) (conv: Conversation) (beforeCommitId: CommitId) (limit: int) : JsonArray * bool =
        let limit = max 1 (min limit 200)
        let untilId = if beforeCommitId = 0UL then proj.latestCommitId else beforeCommitId - 1UL
        let msgs =
            Projection.effectiveMessagesAt proj conv.conversationId untilId
            |> List.filter (fun m -> m.commitId < beforeCommitId)
        let page = msgs |> List.rev |> List.truncate limit |> List.rev
        let items = JsonArray()
        for m in page do
            let o = JsonObject()
            o["commitId"] <- m.commitId
            o["committedAt"] <- m.committedAtUtc.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            o["payload"] <- m.payloadJson.DeepClone()
            items.Add o
        items, List.length msgs > limit

    /// 导出固定可见性水位；分页边界只截取消息，不改变删除／分叉语义。
    let exportPage (proj: Projection) (query: ConversationExportQuery) : Result<ConversationExportPageData, string> =
        let at = query.atCommitId |> Option.defaultValue proj.latestCommitId
        match Projection.tryConversation proj query.conversationId with
        | None -> Error "会话不存在，无法导出。"
        | Some conv when conv.deleted -> Error "会话已删除，导出已停止；没有保存不完整文件。"
        | Some _ when at > proj.latestCommitId -> Error "导出水位已失效，请重新开始导出。"
        | Some _ when query.atCommitId.IsNone && query.beforeCommitId <> 0UL -> Error "导出首页不能指定分页边界。"
        | Some conv ->
            let all = Projection.effectiveMessagesAt proj conv.conversationId at
            if query.beforeCommitId <> 0UL && not (all |> List.exists (fun m -> m.commitId = query.beforeCommitId)) then
                Error "导出分页边界不属于这份会话，请重新开始导出。"
            else
                let mutable rest =
                    all |> List.rev
                    |> List.skipWhile (fun m -> query.beforeCommitId <> 0UL && m.commitId >= query.beforeCommitId)
                let mutable selected: JsonNode list = []
                let mutable bytes, count = 0, 0
                let mutable donePage = false
                let mutable failure = None
                while not donePage && not (List.isEmpty rest) && count < ConversationExportLimits.pageMessages do
                    let m = rest.Head
                    let node = JsonObject()
                    node["commitId"] <- m.commitId
                    node["committedAt"] <- m.committedAtUtc.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                    node["payload"] <- m.payloadJson.DeepClone()
                    let size = System.Text.Encoding.UTF8.GetByteCount(node.ToJsonString())
                    if size > ConversationExportLimits.maxMessageBytes then
                        failure <- Some "单条消息超过 16 MiB 导出安全上限；导出已停止，没有截断内容。"
                        donePage <- true
                    elif count > 0 && bytes + size > ConversationExportLimits.pageBytes then donePage <- true
                    else
                        selected <- (node :> JsonNode) :: selected
                        bytes <- bytes + size
                        count <- count + 1
                        rest <- rest.Tail
                match failure with
                | Some message -> Error message
                | None ->
                    Ok { exportId = query.exportId; conversationId = conv.conversationId
                         atCommitId = at; beforeCommitId = query.beforeCommitId
                         title = conv.title; totalMessages = all.Length
                         items = JsonArray(List.toArray selected); hasMore = not (List.isEmpty rest) }
    /// 从消息 payload 提取附件引用（客户端写入消息 contents 的 `{"type":"attachment",...}` 项）。
    /// 用于 doctor 可达性检查（Q179）、客户端展示与附件回收。
    let attachmentRefsOf (payload: JsonNode) : (string * string * string * int64) list =
        let results = System.Collections.Generic.List<string * string * string * int64>()
        let rec walk (node: JsonNode) =
            match node with
            | null -> ()
            | :? JsonArray as a ->
                for item in a do walk item
            | :? JsonObject as o ->
                let mutable t: JsonNode = null
                if o.TryGetPropertyValue("type", &t) && t <> null && t.GetValueKind() = System.Text.Json.JsonValueKind.String && t.GetValue<string>() = "attachment" then
                    let getStr k =
                        let mutable n: JsonNode = null
                        if o.TryGetPropertyValue(k, &n) && n <> null && n.GetValueKind() = System.Text.Json.JsonValueKind.String then n.GetValue<string>() else ""
                    let size =
                        let mutable n: JsonNode = null
                        if o.TryGetPropertyValue("size", &n) && n <> null && n.GetValueKind() = System.Text.Json.JsonValueKind.Number then
                            match n with
                            | :? JsonValue as v ->
                                match v.TryGetValue<int64>() with true, val64 -> val64 | _ -> 0L
                            | _ -> 0L
                        else 0L
                    results.Add(getStr "sha256", getStr "mediaType", getStr "fileName", size)
                else
                    for kv in o do walk kv.Value
            | _ -> ()
        walk payload
        List.ofSeq results

    /// 仍需保留附件的会话集合。
    ///
    /// 「未删除的会话」不够：fork 只继承父会话在 forkBaseCommitId 时刻的消息，
    /// 父会话被删后子分支照样展示那些消息，因此祖先链必须一并算作存活。
    let private conversationsKeepingAttachments (proj: Projection) : Set<System.Guid> =
        let rec walkUp (acc: Set<System.Guid>) (id: System.Guid) =
            if acc.Contains id then acc
            else
                let acc = acc.Add id
                match Projection.tryConversation proj id with
                | Some conv ->
                    match conv.parent with
                    | Some (parentId, _) -> walkUp acc parentId
                    | None -> acc
                | None -> acc
        proj.conversations
        |> Map.toSeq
        |> Seq.filter (fun (_, conv) -> not conv.deleted)
        |> Seq.fold (fun acc (id, _) -> walkUp acc id) Set.empty

    /// 仍被引用的附件 sha256（小写）。
    ///
    /// 刻意保守：连已被 tombstone 的消息也算引用。历史分页可以按更早的
    /// commitId 回看那条消息，删了 blob 就会把历史变成「内容已丢失」。
    let referencedAttachments (proj: Projection) : Set<string> =
        conversationsKeepingAttachments proj
        |> Seq.choose (Projection.tryConversation proj)
        |> Seq.collect (fun conv -> conv.messages)
        |> Seq.collect (fun message -> attachmentRefsOf message.payloadJson)
        |> Seq.map (fun (sha, _, _, _) -> sha.ToLowerInvariant())
        |> Seq.filter (System.String.IsNullOrWhiteSpace >> not)
        |> Set.ofSeq
