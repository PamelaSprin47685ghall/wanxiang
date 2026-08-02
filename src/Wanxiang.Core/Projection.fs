namespace Wanxiang.Core

open System

/// 消息记录：一条完整 Agent Framework 消息，用全局提交 id 作为消息标识。
type MessageRecord = {
    commitId: CommitId
    conversationId: Guid
    payloadJson: System.Text.Json.Nodes.JsonNode
    /// tombstone：删除事件发生时的提交 id（None = 未删除）
    deletedAtCommitId: CommitId option
}

/// 会话投影。
type Conversation = {
    conversationId: Guid
    title: string
    createdAtUtc: DateTimeOffset
    deleted: bool
    /// (parentConversationId, forkAfterId)；None = 根会话
    parent: (Guid * CommitId option) option
    /// 创建该 fork 的提交 id（快照边界，由提交外壳决定）
    forkBaseCommitId: CommitId option
    config: SessionConfig
    /// 本会话直接记录的消息（不含父分支继承），按 commitId 升序
    messages: MessageRecord list
    /// 该会话投影最后相关提交 id（写权限水位）
    lastCommitId: CommitId option
}

/// 幂等记录。
type IdemRecord = {
    commandId: string
    invocationId: Guid
    commandType: string
    canonicalHash: string
    commitId: CommitId
}

/// 内存投影：NDJSON 折叠后的唯一日常查询来源。
type Projection = {
    conversations: Map<Guid, Conversation>
    idempotency: Map<string, IdemRecord>
    latestCommitId: CommitId
}

module Projection =

    let empty : Projection =
        { conversations = Map.empty
          idempotency = Map.empty
          latestCommitId = 0UL }

    let tryConversation (proj: Projection) (id: Guid) : Conversation option =
        proj.conversations.TryFind id

    let private visibleInScope (untilCommitId: CommitId) (m: MessageRecord) : bool =
        m.commitId <= untilCommitId
        && (match m.deletedAtCommitId with None -> true | Some d -> d > untilCommitId)

    /// 递归展开会话在 untilCommitId 时刻的有效可见消息（fork 结构共享）。
    /// 父分支只考虑其 forkBaseCommitId 时刻的状态；父分支之后的删除/新增不影响子分支。
    /// 决策 74/75：子分支只继承父分支截至 forkAfterId 的消息（被编辑消息之前的历史），
    /// 被编辑的旧消息被 fork 提交中的新消息"替代"，不进入子分支。
    let rec effectiveMessagesAtMap (conversations: Map<Guid, Conversation>) (convId: Guid) (untilCommitId: CommitId) : MessageRecord list =
        match conversations.TryFind convId with
        | None -> []
        | Some conv ->
            let own = conv.messages |> List.filter (visibleInScope untilCommitId)
            match conv.parent, conv.forkBaseCommitId with
            | Some (parentId, forkAfter), Some baseId when baseId <= untilCommitId ->
                let inherited = effectiveMessagesAtMap conversations parentId baseId
                match forkAfter with
                | Some afterId -> (inherited |> List.filter (fun m -> m.commitId <= afterId)) @ own
                | None -> own
            | _ -> own

    /// 递归展开会话在 untilCommitId 时刻的有效可见消息（fork 结构共享）。
    let effectiveMessagesAt (proj: Projection) (convId: Guid) (untilCommitId: CommitId) : MessageRecord list =
        effectiveMessagesAtMap proj.conversations convId untilCommitId

    /// 会话当前完整可见消息。
    let effectiveMessages (proj: Projection) (conv: Conversation) : MessageRecord list =
        effectiveMessagesAt proj conv.conversationId proj.latestCommitId

    let conversationList (proj: Projection) : Conversation list =
        proj.conversations.Values
        |> Seq.filter (fun c -> not c.deleted)
        |> Seq.sortByDescending (fun c -> c.lastCommitId)
        |> List.ofSeq

    /// 应用一条提交（纯函数）。投影失败返回错误，由 Store 层决定截尾/poison 策略。
    let applyCommit (proj: Projection) (commit: Events.Commit) : Result<Projection, WanxiangError> =
        let expectedId = proj.latestCommitId + 1UL
        if commit.id <> expectedId then
            Error(ValidationError(sprintf "commit id %d out of order; expected %d" commit.id expectedId))
        else
            let mutable conversations = proj.conversations
            let mutable failed: WanxiangError option = None

            let applyOne (ev: EventData) =
                if failed.IsSome then ()
                else
                    match ev with
                    | ConversationCreated d ->
                        if conversations.ContainsKey d.conversationId then
                            failed <- Some(ValidationError(sprintf "duplicate conversation %O" d.conversationId))
                        else
                            let conv =
                                { conversationId = d.conversationId
                                  title = d.title
                                  createdAtUtc = commit.committedAtUtc
                                  deleted = false
                                  parent = None
                                  forkBaseCommitId = None
                                  config = d.config
                                  messages = []
                                  lastCommitId = Some commit.id }
                            conversations <- conversations.Add(d.conversationId, conv)
                    | ConversationForked d ->
                        if conversations.ContainsKey d.conversationId then
                            failed <- Some(ValidationError(sprintf "duplicate conversation %O" d.conversationId))
                        else
                            match conversations.TryFind d.parentConversationId with
                            | None -> failed <- Some(ValidationError(sprintf "fork parent %O not found" d.parentConversationId))
                            | Some parent when parent.deleted -> failed <- Some(ValidationError(sprintf "fork parent %O deleted" d.parentConversationId))
                            | Some parent ->
                                // forkAfterId 必须是父会话（或父会话可见前缀中）一条已提交消息
                                let prefixOk =
                                    match d.forkAfterId with
                                    | None -> true
                                    | Some afterId ->
                                        // 父会话在 forkBase（=commit.id）时刻的可见消息中必须存在 afterId
                                        let msgs = effectiveMessagesAtMap conversations d.parentConversationId commit.id
                                        msgs |> List.exists (fun m -> m.commitId = afterId)
                                if not prefixOk then
                                    failed <- Some(ValidationError(sprintf "fork point %A not in parent prefix" d.forkAfterId))
                                else
                                    let conv =
                                        { conversationId = d.conversationId
                                          title = parent.title + " (fork)"
                                          createdAtUtc = commit.committedAtUtc
                                          deleted = false
                                          parent = Some(d.parentConversationId, d.forkAfterId)
                                          forkBaseCommitId = Some commit.id
                                          config = parent.config
                                          messages = []
                                          lastCommitId = Some commit.id }
                                    conversations <- conversations.Add(d.conversationId, conv)
                    | ConversationRenamed d ->
                        match conversations.TryFind d.conversationId with
                        | None -> failed <- Some(ValidationError(sprintf "conversation %O not found" d.conversationId))
                        | Some conv -> conversations <- conversations.Add(d.conversationId, { conv with title = d.title; lastCommitId = Some commit.id })
                    | ConversationConfigUpdated d ->
                        match conversations.TryFind d.conversationId with
                        | None -> failed <- Some(ValidationError(sprintf "conversation %O not found" d.conversationId))
                        | Some conv -> conversations <- conversations.Add(d.conversationId, { conv with config = d.config; lastCommitId = Some commit.id })
                    | ConversationDeleted d ->
                        match conversations.TryFind d.conversationId with
                        | None -> failed <- Some(ValidationError(sprintf "conversation %O not found" d.conversationId))
                        | Some conv -> conversations <- conversations.Add(d.conversationId, { conv with deleted = true; lastCommitId = Some commit.id })
                    | AgentMessageRecorded d ->
                        match conversations.TryFind d.conversationId with
                        | None -> failed <- Some(ValidationError(sprintf "conversation %O not found" d.conversationId))
                        | Some conv ->
                            let msg =
                                { commitId = commit.id
                                  conversationId = d.conversationId
                                  payloadJson = d.payloadJson
                                  deletedAtCommitId = None }
                            conversations <- conversations.Add(d.conversationId, { conv with messages = conv.messages @ [ msg ]; lastCommitId = Some commit.id })
                    | MessageDeleted d ->
                        match conversations.TryFind d.conversationId with
                        | None -> failed <- Some(ValidationError(sprintf "conversation %O not found" d.conversationId))
                        | Some conv ->
                            let mutable found = false
                            let msgs =
                                conv.messages
                                |> List.map (fun m ->
                                    if m.commitId = d.messageCommitId && m.deletedAtCommitId.IsNone then
                                        found <- true
                                        { m with deletedAtCommitId = Some commit.id }
                                    else m)
                            if not found then
                                failed <- Some(ValidationError(sprintf "message %d not found in conversation %O" d.messageCommitId d.conversationId))
                            else
                                conversations <- conversations.Add(d.conversationId, { conv with messages = msgs; lastCommitId = Some commit.id })

            for ev in commit.events do
                applyOne ev

            match failed with
            | Some err -> Error err
            | None ->
                // 幂等索引
                let idem =
                    match commit.commandId, commit.commandType, commit.commandHash with
                    | Some cid, Some ctype, Some hash ->
                        match commit.events with
                        | _ :: _ ->
                            proj.idempotency.Add(cid, { commandId = cid; invocationId = Guid.Empty; commandType = ctype; canonicalHash = hash; commitId = commit.id })
                        | [] -> proj.idempotency
                    | _ -> proj.idempotency
                Ok { conversations = conversations
                     idempotency = idem
                     latestCommitId = commit.id }

    let tryIdem (proj: Projection) (commandId: string) : IdemRecord option =
        proj.idempotency.TryFind commandId
