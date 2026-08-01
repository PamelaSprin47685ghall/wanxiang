namespace Wanxiang.Core

open System
open System.Text.Json.Nodes

/// 领域事件数据。每个事件拥有稳定 type + version（v1 语义永久固定）。
type ConversationCreatedData = {
    conversationId: Guid
    title: string
    config: SessionConfig
}

type ConversationForkedData = {
    conversationId: Guid
    parentConversationId: Guid
    /// 父对话中被继承的最后一条消息的全局提交 id（编辑第一条消息时为 None）
    forkAfterId: CommitId option
}

type ConversationRenamedData = {
    conversationId: Guid
    title: string
}

type ConversationConfigUpdatedData = {
    conversationId: Guid
    config: SessionConfig
}

type ConversationDeletedData = {
    conversationId: Guid
}

/// Agent Framework 完整消息：payload 原样透明保存（MAF schema 的 ChatMessage JSON）。
type AgentMessageRecordedData = {
    conversationId: Guid
    payloadJson: JsonNode
}

type MessageDeletedData = {
    conversationId: Guid
    /// 被删除消息的全局提交 id（消息标识）
    messageCommitId: CommitId
}

type EventData =
    | ConversationCreated of ConversationCreatedData
    | ConversationForked of ConversationForkedData
    | ConversationRenamed of ConversationRenamedData
    | ConversationConfigUpdated of ConversationConfigUpdatedData
    | ConversationDeleted of ConversationDeletedData
    | AgentMessageRecorded of AgentMessageRecordedData
    | MessageDeleted of MessageDeletedData

module Events =

    let eventType (ev: EventData) : string =
        match ev with
        | ConversationCreated _ -> "conversation.created"
        | ConversationForked _ -> "conversation.forked"
        | ConversationRenamed _ -> "conversation.renamed"
        | ConversationConfigUpdated _ -> "conversation.config-updated"
        | ConversationDeleted _ -> "conversation.deleted"
        | AgentMessageRecorded _ -> "agent-message-recorded"
        | MessageDeleted _ -> "message.deleted"

    let eventVersion (_ev: EventData) : int = Constants.EventVersion1

    /// 一次原子提交：一条 NDJSON 行。events 数组顺序即领域应用顺序。
    type Commit = {
        formatVersion: int
        id: CommitId
        committedAtUtc: DateTimeOffset
        commandId: string option
        commandType: string option
        /// 规范化命令载荷的 SHA-256（用于幂等冲突检测）
        commandHash: string option
        events: EventData list
    }

    module Commit =
        let create (id: CommitId) (nowUtc: DateTimeOffset) (events: EventData list) : Commit =
            { formatVersion = Constants.FormatVersion
              id = id
              committedAtUtc = nowUtc
              commandId = None
              commandType = None
              commandHash = None
              events = events }

        let withCommand (commandId: string) (commandType: string) (commandHash: string) (commit: Commit) : Commit =
            { commit with
                commandId = Some commandId
                commandType = Some commandType
                commandHash = Some commandHash }
