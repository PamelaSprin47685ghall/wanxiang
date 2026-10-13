namespace Wanxiang.Core

open System
open System.Text.Json.Nodes

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

    /// 从原始 payload 读 role。Core 不依赖 Agent Framework，
    /// 因此直接读透明保存的 JSON 字段而不反序列化成 ChatMessage。
    let private roleOfPayload (payload: System.Text.Json.Nodes.JsonNode) : string =
        match payload with
        | :? System.Text.Json.Nodes.JsonObject as o ->
            let mutable n: System.Text.Json.Nodes.JsonNode = null
            if o.TryGetPropertyValue("role", &n) && not (isNull n) && n.GetValueKind() = System.Text.Json.JsonValueKind.String then
                n.GetValue<string>()
            else
                ""
        | _ -> ""

    /// 会话末尾连续的模型侧消息（assistant / tool）的提交 id，按原顺序。
    /// 「重新生成」就是把这一段作废后重跑，因此遇到第一条用户消息即停止。
    let trailingModelMessages (visible: MessageRecord list) : CommitId list =
        visible
        |> List.rev
        |> List.takeWhile (fun m ->
            match roleOfPayload m.payloadJson with
            | "assistant" | "tool" -> true
            | _ -> false)
        |> List.rev
        |> List.map (fun m -> m.commitId)

    /// 校验客户端游标并规划命令。纯函数，不执行副作用。
    /// clientCursor：该连接已确认应用的全局提交 id。
    let plan (proj: Projection) (clientCursor: CommitId) (cmd: ClientCommand) : PlanResult =
        let canonicalPayload = CanonicalPayload.ofCommand cmd
        let canonicalHash = CommandId.sha256Hex canonicalPayload
        let ctype = ClientCommand.commandType cmd
        let commandId = CommandId.compute (ClientCommand.invocationId cmd) ctype canonicalPayload


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

        | ImportConversation d ->
            // 决策 313：导入 = 新建 + 逐条记录；载荷只收 role 合法的消息对象，条数有上限。
            let validRoles = set [ "user"; "assistant"; "tool"; "system" ]
            let messageOk (n: JsonNode) =
                match n with
                | :? JsonObject as o ->
                    match o["role"] with
                    | :? JsonValue as v ->
                        match v.TryGetValue<string>() with
                        | true, r -> validRoles.Contains r
                        | _ -> false
                    | _ -> false
                | _ -> false
            if not (SessionConfig.isValid d.config) then
                Rejected(ValidationError "invalid session config")
            elif Projection.tryConversation proj d.conversationId |> Option.isSome then
                Rejected(ConversationIdTaken d.conversationId)
            elif isNull d.messages || d.messages.Count = 0 then
                Rejected(ValidationError "import has no messages")
            elif d.messages.Count > ImportLimits.maxMessages then
                Rejected(ValidationError (sprintf "import exceeds %d messages" ImportLimits.maxMessages))
            elif not (d.messages |> Seq.forall messageOk) then
                Rejected(ValidationError "import contains an invalid message")
            else
                match checkStale clientCursor proj.latestCommitId commandId ctype canonicalHash with
                | Planned p ->
                    plannedWith
                        p
                        (ConversationCreated
                            { conversationId = d.conversationId
                              title = d.title.Trim()
                              config = d.config }
                         :: [ for m in d.messages ->
                                AgentMessageRecorded { conversationId = d.conversationId; payloadJson = m.DeepClone() } ])
                | r -> r

        | ForkConversation d ->
            // fork 继承父会话配置（决策 81 第二问：投影用 parent.config），
            // 客户端无需提供有效 config——移除 isValid 校验（空/过时 config 不影响 fork）
            if Projection.tryConversation proj d.conversationId |> Option.isSome then
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
                Rejected(ValidationError(String.Join("; ", SessionConfig.validate d.config)))
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

        | SetConversationFlags d ->
            match convOrErr proj d.conversationId with
            | Error e -> Rejected e
            | Ok conv ->
                if conv.pinned = d.pinned && conv.archived = d.archived then
                    Rejected(ValidationError "conversation flags unchanged")
                else
                    match checkStale clientCursor (conversationWatermark conv) commandId ctype canonicalHash with
                    | Planned p ->
                        plannedWith
                            p
                            [ ConversationFlagsChanged
                                { conversationId = d.conversationId
                                  pinned = d.pinned
                                  archived = d.archived } ]
                    | r -> r

        | ContinueResponse d ->
            // 决策 230：只接受有正文的助手消息——这是把屏幕上已经看见的半截回复落盘，不是伪造任意角色。
            let role, hasText =
                match d.messageJson with
                | :? System.Text.Json.Nodes.JsonObject as o ->
                    let r = match o["role"] with null -> "" | n -> (try n.GetValue<string>() with _ -> "")
                    let t =
                        match o["contents"] with
                        | :? System.Text.Json.Nodes.JsonArray as a ->
                            a |> Seq.exists (fun c ->
                                match c with
                                | :? System.Text.Json.Nodes.JsonObject as co ->
                                    match co["text"] with
                                    | null -> false
                                    | n -> (try not (String.IsNullOrWhiteSpace(n.GetValue<string>())) with _ -> false)
                                | _ -> false)
                        | _ -> false
                    r, t
                | _ -> "", false
            if role <> "assistant" || not hasText then
                Rejected(ValidationError "continue requires a non-empty assistant message")
            else
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

        | RegenerateResponse d ->
            match convOrErr proj d.conversationId with
            | Error e -> Rejected e
            | Ok conv ->
                let trailing = trailingModelMessages (Projection.effectiveMessages proj conv)
                if List.isEmpty trailing then
                    Rejected(ValidationError "no model response to regenerate")
                else
                    match checkStale clientCursor (conversationWatermark conv) commandId ctype canonicalHash with
                    | Planned p ->
                        plannedWith
                            p
                            [ for commitId in trailing ->
                                  MessageDeleted { conversationId = d.conversationId; messageCommitId = commitId } ]
                    | r -> r

    /// 带幂等检查的规划。stamps：commandId -> (规范化载荷哈希, 提交 id)，
    /// 由账本维护（wanxiang2 Ledger 的 Checkpoint.stamps），投影不再持有幂等索引。
    /// 幂等检查必须先于领域校验：重发一条已成功的「新建对话」应得到原提交，而不是「对话已存在」。
    let planStamped (stamps: Map<string, string * CommitId>) (proj: Projection) (clientCursor: CommitId) (cmd: ClientCommand) : PlanResult =
        let canonicalPayload = CanonicalPayload.ofCommand cmd
        let canonicalHash = CommandId.sha256Hex canonicalPayload
        let commandId = CommandId.compute (ClientCommand.invocationId cmd) (ClientCommand.commandType cmd) canonicalPayload
        match stamps.TryFind commandId with
        | Some(hash, commitId) when hash = canonicalHash -> IdempotentReplay commitId
        | Some _ -> Rejected(CommandIdConflict(sprintf "commandId %s reused with different payload" commandId))
        | None -> plan proj clientCursor cmd
