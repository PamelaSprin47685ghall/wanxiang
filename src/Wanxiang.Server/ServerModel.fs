namespace Wanxiang.Server

open System.Text.Json
open System.Text.Json.Nodes
open Wanxiang.Core

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
            let text = sb.ToString()
            if text.Length > 100 then text.Substring(0, 100) + "…" else text

    /// 会话列表摘要（决策 125：含最后可见消息摘要、当前运行状态、有效配置摘要，不含完整消息正文）。
    let conversationListItems (proj: Projection) (runtimeStateOf: System.Guid -> string) : JsonArray =
        let arr = JsonArray()
        for c in Projection.conversationList proj do
            let o = JsonObject()
            o["conversationId"] <- c.conversationId.ToString("D")
            o["title"] <- c.title
            o["lastCommitId"] <- c.lastCommitId |> Option.defaultValue 0UL
            o["deleted"] <- c.deleted
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
            o["payload"] <- m.payloadJson.DeepClone()
            items.Add o
        items, List.length msgs > limit

    /// 从消息 payload 提取附件引用（客户端写入消息 contents 的 `{"type":"attachment",...}` 项）。
    /// 用于 doctor 可达性检查（Q179）与客户端展示。
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
