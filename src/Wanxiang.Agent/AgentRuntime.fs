namespace Wanxiang.Agent

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Wanxiang.Config

/// 单次生成调用（一次 Provider 往返）。
type AgentCallResult =
    | Completed of usage: UsageDetails option
    | Failed of exn
    | Cancelled

/// Agent 运行时：构建与执行。
/// 每个会话一个运行时 agent + session；消息历史由应用托管（投影），
/// 不依赖 Provider 服务端会话状态（决策 20）。
type AgentRuntime(provider: ProviderConfig, instructions: string option, tools: AITool list, historyProvider: ChatHistoryProvider) =

    let chatClient =
        let clientOptions = OpenAI.OpenAIClientOptions()
        clientOptions.Endpoint <- Uri(provider.baseUrl)
        let credential = System.ClientModel.ApiKeyCredential(provider.apiKey |> Option.defaultValue "unused")
        let client = new OpenAI.OpenAIClient(credential, clientOptions)
        client.GetChatClient(provider.model).AsIChatClient()

    let agentOptions = ChatClientAgentOptions()

    do
        agentOptions.Name <- provider.id
        let chatOptions = ChatOptions()
        chatOptions.Instructions <- instructions |> Option.defaultValue null
        chatOptions.Tools <- ResizeArray<AITool>(tools)
        chatOptions.Temperature <- match provider.extraJson |> Option.bind (fun n -> if isNull n then None else Some n) with _ -> Nullable()
        agentOptions.ChatOptions <- chatOptions
        agentOptions.ChatHistoryProvider <- historyProvider
        // 禁用默认中间件（FunctionInvokingChatClient 等）：工具循环由万象编排层控制（决策 92/93）
        agentOptions.UseProvidedChatClientAsIs <- true

    let agent = chatClient.AsAIAgent(agentOptions)

    /// 创建会话并绑定 conversationId（运行时对象）。
    member _.CreateSession(conversationId: Guid) : AgentSession =
        let session = agent.CreateSessionAsync().AsTask().GetAwaiter().GetResult()
        session.StateBag.SetValue<string>(WanxiangHistoryProvider.ConversationIdKey, conversationId.ToString("D"))
        session

    /// 流式执行一次调用（上下文消息列表由编排层显式构造）。delta 通过回调转发（临时展示，不记账）。
    member _.RunStreaming(session: AgentSession, messages: ChatMessage list, onDelta: ChatMessage -> unit, ct: CancellationToken) : Task<AgentCallResult> =
        task {
            try
                let updates = agent.RunStreamingAsync(messages, session, cancellationToken = ct)
                let mutable deltaMsg: ChatMessage = null
                let collected = ResizeArray<AgentResponseUpdate>()
                let enumerator = updates.GetAsyncEnumerator(ct)
                let mutable running = true
                while running do
                    let! hasNext = enumerator.MoveNextAsync()
                    if hasNext then
                        let update = enumerator.Current
                        collected.Add(update)
                        if update.Role.HasValue && update.Role.Value = ChatRole.Assistant then
                            // 正文与思维链都转发（TextReasoningContent；决策 18 透明映射）
                            let pieces =
                                update.Contents
                                |> Seq.choose (fun c ->
                                    match c with
                                    | :? TextContent as t when not (String.IsNullOrEmpty t.Text) ->
                                        Some(TextContent(t.Text) :> AIContent)
                                    | :? TextReasoningContent as r when not (String.IsNullOrEmpty r.Text) ->
                                        Some(TextReasoningContent(r.Text) :> AIContent)
                                    | _ -> None)
                                |> List.ofSeq
                            if not pieces.IsEmpty then
                                if isNull deltaMsg then
                                    deltaMsg <- ChatMessage()
                                    deltaMsg.Role <- ChatRole.Assistant
                                for p in pieces do deltaMsg.Contents.Add p
                                onDelta deltaMsg
                    else
                        running <- false
                let usage =
                    try
                        let response = AgentResponseExtensions.ToAgentResponse(collected)
                        if isNull response || isNull response.Usage then None else Some response.Usage
                    with _ ->
                        None
                return Completed usage
            with
            | :? OperationCanceledException -> return Cancelled
            | ex -> return Failed ex
        }

    /// 非流式执行一次调用（工具结果回传、无界面生成等）。
    member _.Run(session: AgentSession, message: ChatMessage, ct: CancellationToken) : Task<AgentCallResult> =
        task {
            try
                let! agentResponse = agent.RunAsync(message, session, cancellationToken = ct)
                let usage = if isNull agentResponse || isNull agentResponse.Usage then None else Some agentResponse.Usage
                return Completed usage
            with
            | :? OperationCanceledException -> return Cancelled
            | ex -> return Failed ex
        }
