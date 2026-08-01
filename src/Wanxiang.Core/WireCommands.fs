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

    let invocationId (cmd: ClientCommand) : Guid =
        match cmd with
        | CreateConversation d -> d.invocationId
        | ForkConversation d -> d.invocationId
        | SendUserMessage d -> d.invocationId
        | RenameConversation d -> d.invocationId
        | DeleteConversation d -> d.invocationId
        | DeleteMessage d -> d.invocationId
        | UpdateConversationConfig d -> d.invocationId

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
            o["config"] <- Wanxiang.Core.CommitCodec.configToJson d.config
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
        CanonicalJson.serialize o
