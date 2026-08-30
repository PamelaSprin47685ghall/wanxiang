namespace Wanxiang.Core

open System
open System.Text.Json.Nodes

/// 客户端写命令（fire-and-forget 事件 payload 的解码结果）。
/// 所有命令由客户端预生成 invocationId（UUIDv7）；网络重试必须复用。
type ClientCommand =
    | CreateConversation of {| invocationId: Guid; conversationId: Guid; title: string; config: SessionConfig |}
    | ForkConversation of {| invocationId: Guid; conversationId: Guid; parentConversationId: Guid; forkAfterId: CommitId option; config: SessionConfig; editedMessageJson: JsonNode |}
    | SendUserMessage of {| invocationId: Guid; conversationId: Guid; messageJson: JsonNode |}
    | RenameConversation of {| invocationId: Guid; conversationId: Guid; title: string |}
    | DeleteConversation of {| invocationId: Guid; conversationId: Guid |}
    | DeleteMessage of {| invocationId: Guid; conversationId: Guid; messageCommitId: CommitId |}
    | UpdateConversationConfig of {| invocationId: Guid; conversationId: Guid; config: SessionConfig |}
    | SetConversationFlags of {| invocationId: Guid; conversationId: Guid; pinned: bool; archived: bool |}
    /// 重新生成：删除末尾的助手/工具消息（tombstone），再对同一批用户消息重跑一次生成。
    | RegenerateResponse of {| invocationId: Guid; conversationId: Guid |}

module ClientCommand =

    let commandType (cmd: ClientCommand) : string =
        match cmd with
        | CreateConversation _ -> "conversation.create"
        | ForkConversation _ -> "conversation.fork"
        | SendUserMessage _ -> "chat.user-message.enqueue"
        | RenameConversation _ -> "conversation.rename"
        | DeleteConversation _ -> "conversation.delete"
        | DeleteMessage _ -> "message.delete"
        | UpdateConversationConfig _ -> "conversation.config-update"
        | SetConversationFlags _ -> "conversation.flags-set"
        | RegenerateResponse _ -> "chat.regenerate"

    let invocationId (cmd: ClientCommand) : Guid =
        match cmd with
        | CreateConversation d -> d.invocationId
        | ForkConversation d -> d.invocationId
        | SendUserMessage d -> d.invocationId
        | RenameConversation d -> d.invocationId
        | DeleteConversation d -> d.invocationId
        | DeleteMessage d -> d.invocationId
        | UpdateConversationConfig d -> d.invocationId
        | SetConversationFlags d -> d.invocationId
        | RegenerateResponse d -> d.invocationId

    /// 命令针对的会话（写权限水位与广播目标）。
    let conversationId (cmd: ClientCommand) : Guid =
        match cmd with
        | CreateConversation d -> d.conversationId
        | ForkConversation d -> d.conversationId
        | SendUserMessage d -> d.conversationId
        | RenameConversation d -> d.conversationId
        | DeleteConversation d -> d.conversationId
        | DeleteMessage d -> d.conversationId
        | UpdateConversationConfig d -> d.conversationId
        | SetConversationFlags d -> d.conversationId
        | RegenerateResponse d -> d.conversationId

    /// 规范化业务载荷（不含传输元数据），用于 commandId 计算。
    let canonicalPayload (cmd: ClientCommand) : string =
        let o = JsonObject()
        match cmd with
        | CreateConversation d ->
            o["conversationId"] <- d.conversationId.ToString("D")
            o["title"] <- d.title
            o["config"] <- Wanxiang.Core.CommitCodec.configToJson d.config
        | ForkConversation d ->
            o["conversationId"] <- d.conversationId.ToString("D")
            o["parentConversationId"] <- d.parentConversationId.ToString("D")
            match d.forkAfterId with Some id -> o["forkAfterId"] <- id | None -> ()
            // fork 配置由父会话投影继承（决策 81），config 不参与 canonical/commandId（客户端可空）
            o["editedMessage"] <- d.editedMessageJson.DeepClone()
        | SendUserMessage d ->
            o["conversationId"] <- d.conversationId.ToString("D")
            o["message"] <- d.messageJson.DeepClone()
        | RenameConversation d ->
            o["conversationId"] <- d.conversationId.ToString("D")
            o["title"] <- d.title
        | DeleteConversation d ->
            o["conversationId"] <- d.conversationId.ToString("D")
        | DeleteMessage d ->
            o["conversationId"] <- d.conversationId.ToString("D")
            o["messageCommitId"] <- d.messageCommitId
        | UpdateConversationConfig d ->
            o["conversationId"] <- d.conversationId.ToString("D")
            o["config"] <- Wanxiang.Core.CommitCodec.configToJson d.config
        | SetConversationFlags d ->
            o["conversationId"] <- d.conversationId.ToString("D")
            o["pinned"] <- d.pinned
            o["archived"] <- d.archived
        | RegenerateResponse d ->
            o["conversationId"] <- d.conversationId.ToString("D")
        CanonicalJson.serialize o
