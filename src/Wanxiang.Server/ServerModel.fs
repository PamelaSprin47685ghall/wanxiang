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
