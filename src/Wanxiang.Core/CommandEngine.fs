namespace Wanxiang.Core

open System

/// 命令执行规划：一次命令 -> 事件列表 + 幂等信息 + 涉及投影水位。
type CommandPlan = {
    commandId: string
    commandType: string
    canonicalHash: string
    events: EventData list
    /// 该命令涉及的投影最后相关提交 id（陈旧检测水位）
    watermark: CommitId
}

type PlanResult =
    | Planned of CommandPlan
    /// 同一 commandId 已提交过（安全重试），返回原提交 id
    | IdempotentReplay of CommitId
    | Rejected of WanxiangError

module CommandEngine =

    let private checkStale (clientCursor: CommitId) (watermark: CommitId) (commandId: string) (commandType: string) (canonicalHash: string) : PlanResult =
        if clientCursor < watermark then
            Rejected(StaleProjection watermark)
        else
            Planned
                { commandId = commandId
                  commandType = commandType
                  canonicalHash = canonicalHash
                  events = []
                  watermark = watermark }

    let private convOrErr (proj: Projection) (id: Guid) : Result<Conversation, WanxiangError> =
        match Projection.tryConversation proj id with
        | None -> Error(ConversationNotFound id)
        | Some c when c.deleted -> Error(WanxiangError.ConversationDeleted id)
        | Some c -> Ok c

    /// 校验客户端游标并规划命令。纯函数，不执行副作用。
    /// clientCursor：该连接已确认应用的全局提交 id。
    let plan (proj: Projection) (clientCursor: CommitId) (cmd: ClientCommand) : PlanResult =
        let canonicalPayload = ClientCommand.canonicalPayload cmd
        let canonicalHash = CommandId.sha256Hex canonicalPayload
        let ctype = ClientCommand.commandType cmd
        let commandId = CommandId.compute (ClientCommand.invocationId cmd) ctype canonicalPayload

        match Projection.tryIdem proj commandId with
        | Some idemRec ->
            // 幂等：同一 commandId 必须对应同一规范化载荷
            if idemRec.canonicalHash = canonicalHash then
                IdempotentReplay idemRec.commitId
            else
                Rejected(CommandIdConflict(sprintf "commandId %s reused with different payload" commandId))
        | None ->

        let plannedWith (p: CommandPlan) (events: EventData list) : PlanResult = Planned { p with events = events }
        let conversationWatermark (conv: Conversation) : CommitId =
            conv.lastCommitId |> Option.defaultValue proj.latestCommitId

        match cmd with
        | CreateConversation d ->
            if not (SessionConfig.isValid d.config) then
                Rejected(ValidationError "invalid session config")
            elif Projection.tryConversation proj d.conversationId |> Option.isSome then
                Rejected(ConversationIdTaken d.conversationId)
            else
                match checkStale clientCursor proj.latestCommitId commandId ctype canonicalHash with
                | Planned p ->
                    plannedWith
                        p
                        [ ConversationCreated
                            { conversationId = d.conversationId
                              title = d.title.Trim()
                              config = d.config } ]
                | r -> r

        | ForkConversation d ->
            if not (SessionConfig.isValid d.config) then
                Rejected(ValidationError "invalid session config")
            elif Projection.tryConversation proj d.conversationId |> Option.isSome then
                Rejected(ConversationIdTaken d.conversationId)
            else
                match convOrErr proj d.parentConversationId with
                | Error e -> Rejected e
                | Ok parent ->
                    // forkAfterId 必须位于父会话当前可见前缀
                    let forkOk =
                        match d.forkAfterId with
                        | None -> true
                        | Some afterId ->
                            Projection.effectiveMessages proj parent
                            |> List.exists (fun m -> m.commitId = afterId)
                    if not forkOk then
                        Rejected(ForkPointInvalid(sprintf "fork point %A not visible in parent conversation" d.forkAfterId))
                    else
                        match checkStale clientCursor (conversationWatermark parent) commandId ctype canonicalHash with
                        | Planned p ->
                            // fork 继承父会话当前配置快照（SSOT：按当时状态复制）
                            plannedWith
                                p
                                [ ConversationForked
                                    { conversationId = d.conversationId
                                      parentConversationId = d.parentConversationId
                                      forkAfterId = d.forkAfterId }
                                  AgentMessageRecorded
                                    { conversationId = d.conversationId
                                      payloadJson = d.editedMessageJson } ]
                        | r -> r

        | SendUserMessage d ->
            match convOrErr proj d.conversationId with
            | Error e -> Rejected e
            | Ok conv ->
                match checkStale clientCursor (conversationWatermark conv) commandId ctype canonicalHash with
                | Planned p ->
                    plannedWith
                        p
                        [ AgentMessageRecorded
                            { conversationId = d.conversationId
                              payloadJson = d.messageJson } ]
                | r -> r

        | RenameConversation d ->
            let t = d.title.Trim()
            if String.IsNullOrEmpty t || t.Length > 200 then
                Rejected(ValidationError "title must be 1-200 characters")
            else
                match convOrErr proj d.conversationId with
                | Error e -> Rejected e
                | Ok conv ->
                    match checkStale clientCursor (conversationWatermark conv) commandId ctype canonicalHash with
                    | Planned p ->
                        plannedWith p [ ConversationRenamed { conversationId = d.conversationId; title = t } ]
                    | r -> r

        | DeleteConversation d ->
            match Projection.tryConversation proj d.conversationId with
            | None -> Rejected(ConversationNotFound d.conversationId)
            | Some conv when conv.deleted -> Rejected(ValidationError "conversation already deleted")
            | Some conv ->
                match checkStale clientCursor (conversationWatermark conv) commandId ctype canonicalHash with
                | Planned p -> plannedWith p [ ConversationDeleted { conversationId = d.conversationId } ]
                | r -> r

        | DeleteMessage d ->
            match convOrErr proj d.conversationId with
            | Error e -> Rejected e
            | Ok conv ->
                let visible = Projection.effectiveMessages proj conv
                if not (visible |> List.exists (fun m -> m.commitId = d.messageCommitId)) then
                    Rejected(ValidationError(sprintf "message %d not visible in conversation" d.messageCommitId))
                else
                    match checkStale clientCursor (conversationWatermark conv) commandId ctype canonicalHash with
                    | Planned p ->
                        plannedWith
                            p
                            [ MessageDeleted
                                { conversationId = d.conversationId
                                  messageCommitId = d.messageCommitId } ]
                    | r -> r

        | UpdateConversationConfig d ->
            if not (SessionConfig.isValid d.config) then
                Rejected(ValidationError "invalid session config")
            else
                match convOrErr proj d.conversationId with
                | Error e -> Rejected e
                | Ok conv ->
                    match checkStale clientCursor (conversationWatermark conv) commandId ctype canonicalHash with
                    | Planned p ->
                        plannedWith
                            p
                            [ ConversationConfigUpdated
                                { conversationId = d.conversationId
                                  config = d.config } ]
                    | r -> r
