namespace Wanxiang.Agent

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

/// 万象的 ChatHistoryProvider（决策 18/20）：
/// - Invoking：从投影加载该会话已提交的历史消息（应用托管消息历史）；
/// - Invoked：把本次调用产生的完整响应消息回调给编排层记账（NDJSON 透明持久化）。
/// 会话 ID 放在 AgentSession.StateBag["wanxiang.conversationId"]（运行时对象，可随进程退出丢弃）。
type WanxiangHistoryProvider(loadHistory: Guid -> ChatMessage list, onResponse: Guid -> ChatMessage list -> unit, onFailure: Guid -> exn -> unit) =
    inherit ChatHistoryProvider()

    static member ConversationIdKey = "wanxiang.conversationId"

    static member ConversationIdOf(session: AgentSession) : Guid option =
        let mutable v: string = null
        if session.StateBag.TryGetValue<string>(WanxiangHistoryProvider.ConversationIdKey, &v) then
            match Guid.TryParse v with
            | true, g -> Some g
            | _ -> None
        else
            None

    override _.InvokingCoreAsync(context: ChatHistoryProvider.InvokingContext, ct: CancellationToken) : ValueTask<Collections.Generic.IEnumerable<ChatMessage>> =
        // 返回值是本次调用的完整消息列表（AF 语义）：历史 + 调用方消息。
        // 万象由编排层显式构造上下文（决策 20），loadHistory 为空时也必须透传 RequestMessages。
        let history =
            match context.Session with
            | null -> []
            | session ->
                match WanxiangHistoryProvider.ConversationIdOf session with
                | Some cid -> loadHistory cid
                | None -> []
        let all = Seq.append history context.RequestMessages
        ValueTask<Collections.Generic.IEnumerable<ChatMessage>>(all)

    override _.InvokedCoreAsync(context: ChatHistoryProvider.InvokedContext, ct: CancellationToken) : ValueTask =
        match context.Session with
        | null -> ValueTask()
        | session ->
            match WanxiangHistoryProvider.ConversationIdOf session with
            | Some cid ->
                match context.InvokeException with
                | null ->
                    let msgs = context.ResponseMessages |> List.ofSeq
                    if not (List.isEmpty msgs) then
                        onResponse cid msgs
                    ValueTask()
                | ex ->
                    onFailure cid ex
                    ValueTask()
            | None -> ValueTask()
