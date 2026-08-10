namespace Wanxiang.Server

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Wanxiang.Agent
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Store

/// 会话运行时的生成状态。
type GenerationRuntime = {
    generationId: Guid
    startedAtUtc: DateTimeOffset
    cts: CancellationTokenSource
    mutable runtime: AgentRuntime
    mutable agentSession: AgentSession
    mutable agentConfig: SessionConfig
    /// 配置引用的 Provider 已不存在时记录 provider id（Q154：下一次调用失败结束）
    mutable invalidConfig: string option
    mutable cancelled: bool
    mutable lastProviderMessages: ChatMessage list
}

/// 会话运行时（内存态，可随进程退出丢弃；排队消息允许丢失，决策 22）。
type ConversationRuntime = {
    conversationId: Guid
    mutable generation: GenerationRuntime option
    mutable pendingQueue: (ClientCommand * JsonNode) list
    mutable pendingInvocationIds: Set<Guid>
}

/// 工具执行结果：完整结果（成功或失败，需记账）或已取消（无完整结果，不记账）。
type private ToolOutcome =
    | ToolResult of string
    | ToolCancelled

/// 聊天编排器（决策 12/13/22-24/37/38/87-92）：
/// - 每会话单生成（串行），多会话并行；
/// - 排队消息只在插入点落盘；
/// - 一个 generationId 覆盖整段 Provider/Tool 循环；
/// - 工具并行执行，全部完成后统一返回 Provider（保持原顺序）；
/// - 取消为合作式取消；迟到的 Provider 输出不记账，实际完成的 Tool Result 仍记账。
type ChatOrchestrator(
    coordinator: CommitCoordinator,
    getProjection: unit -> Projection,
    broadcastToConversation: Guid -> WireEvent -> unit,
    toolRegistry: ToolRegistry,
    getConfig: unit -> AppConfig,
    logInfo: string -> unit) =

    let runtimes = ConcurrentDictionary<Guid, ConversationRuntime>()
    let mutable disposed = false

    let getRuntime (convId: Guid) : ConversationRuntime =
        runtimes.GetOrAdd(convId, fun id -> { conversationId = id; generation = None; pendingQueue = []; pendingInvocationIds = Set.empty })

    let nullableInt (n: Nullable<int64>) =
        if n.HasValue then Some(int n.Value) else None

    let usageFromDetails (usage: UsageDetails option) (startedAt: DateTimeOffset) : GenerationUsage option =
        match usage with
        | None -> None
        | Some u ->
            let durationMs = int64 (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
            Some
                { promptTokens = nullableInt u.InputTokenCount
                  completionTokens = nullableInt u.OutputTokenCount
                  cachedTokens = nullableInt u.CachedInputTokenCount
                  totalTokens = nullableInt u.TotalTokenCount
                  durationMs = Some durationMs }

    let finishedEvent (convId: Guid) (generationId: Guid) (status: string) (error: string option) (startedAt: DateTimeOffset) (usage: UsageDetails option) =
        GenerationFinished
            {| conversationId = convId
               generationId = generationId
               status = status
               error = error
               usage = usageFromDetails usage startedAt |}

    let tryGetProjectionConversation (convId: Guid) : Conversation option =
        Projection.tryConversation (getProjection ()) convId

    let loadContextMessages (convId: Guid) : ChatMessage list =
        let proj = getProjection ()
        match Projection.tryConversation proj convId with
        | None -> []
        | Some conv ->
            Projection.effectiveMessages proj conv
            |> List.choose (fun m -> MessageSerde.fromJsonNode m.payloadJson)

    /// 会话当前运行状态（供 snapshot 的 runtimeState 字段）。
    member _.RuntimeStateOf(convId: Guid) : string =
        let rt = getRuntime convId
        lock rt (fun () ->
            match rt.generation with
            | Some g when not g.cancelled -> "generating"
            | _ -> "idle")

    /// 提交一条消息事件（Agent 响应 / 工具结果记账）。
    /// 决策 16：Agent 内部命令也使用独立 invocationId，与普通命令一样带 commandId 提交——
    /// 记账提交因此纳入单写者的幂等索引：重试/重放不重复落盘，重启 replay 后索引可恢复，
    /// 与 Server 层所有写路径保持同一提交形态。
    member private _.SubmitMessage(convId: Guid, payloadJson: JsonNode) : Result<Events.Commit, WanxiangError> =
        let invocationId = Guid.CreateVersion7()
        let canonicalPayload = payloadJson.ToJsonString()
        let commandId = CommandId.compute invocationId "agent.message" canonicalPayload
        let canonicalHash = CommandId.sha256Hex canonicalPayload
        match coordinator.Submit
            { events = [ AgentMessageRecorded { conversationId = convId; payloadJson = payloadJson } ]
              commandId = Some commandId
              commandType = Some "agent.message"
              commandHash = Some canonicalHash
              nowUtc = None } with
        | Committed c -> Ok c
        | IdempotentReplay c -> Ok c
        | TruncatedAndReused (c, _) -> Ok c // 已截尾：调用方忽略
        | CommandIdRejected e -> Error e
        | CommitFailed e -> Error e

    /// 记账提交结果处理：失败必须可见（决策 10/17：只有 committed 才算成功），
    /// 记 stderr 并向观察该会话的客户端广播 ServerError。
    member private this.RecordSubmitOutcome(convId: Guid, submitResult: Result<Events.Commit, WanxiangError>) : unit =
        match submitResult with
        | Ok _ -> ()
        | Error e ->
            logInfo(sprintf "message accounting failed for conversation %O: %s" convId (WanxiangError.message e))
            broadcastToConversation convId (ServerError {| message = sprintf "消息记账失败: %s" (WanxiangError.message e) |})

    /// 提交一条排队消息（插入点；带命令标识，幂等安全）。
    member private this.SubmitQueuedMessage(convId: Guid, cmd: ClientCommand, payloadJson: JsonNode) : SubmitResult =
        let canonicalPayload = ClientCommand.canonicalPayload cmd
        let commandId = CommandId.compute (ClientCommand.invocationId cmd) (ClientCommand.commandType cmd) canonicalPayload
        let canonicalHash = CommandId.sha256Hex canonicalPayload
        coordinator.Submit
            { events = [ AgentMessageRecorded { conversationId = convId; payloadJson = payloadJson } ]
              commandId = Some commandId
              commandType = Some(ClientCommand.commandType cmd)
              commandHash = Some canonicalHash
              nowUtc = None }

    /// 处理发送消息命令：入队；会话空闲时启动生成。
    member this.HandleSendUserMessage(cmd: ClientCommand) : unit =
        let data =
            match cmd with
            | SendUserMessage d -> d
            | _ -> failwith "unreachable"
        let rt = getRuntime data.conversationId
        let invId = ClientCommand.invocationId cmd
        lock rt (fun () ->
            if rt.pendingInvocationIds.Contains invId then
                () // 已在队列（重试去重）
            else
                rt.pendingQueue <- rt.pendingQueue @ [ cmd, data.messageJson ]
                rt.pendingInvocationIds <- rt.pendingInvocationIds.Add invId)
        this.MaybeStartGeneration data.conversationId

    /// 排队消息是否已全部提交（供客户端状态展示）。
    member _.HasPending(convId: Guid, invocationId: Guid) : bool =
        let rt = getRuntime convId
        lock rt (fun () -> rt.pendingInvocationIds.Contains invocationId)

    /// 插入点：排空队列并逐条提交（决策 22-24）。返回批次的命令标识列表。
    member private this.DrainQueue(convId: Guid) : (Guid * CommitId) list =
        let rt = getRuntime convId
        let batch =
            lock rt (fun () ->
                let q = rt.pendingQueue
                rt.pendingQueue <- []
                rt.pendingInvocationIds <- Set.empty
                q)
        [ for (cmd, msgJson) in batch do
              let result = this.SubmitQueuedMessage(convId, cmd, msgJson)
              let invId = ClientCommand.invocationId cmd
              let commandId = CommandId.compute invId (ClientCommand.commandType cmd) (ClientCommand.canonicalPayload cmd)
              match result with
              | Committed c ->
                  broadcastToConversation convId (CommandCommitted {| invocationId = invId; commandId = commandId; commitId = c.id |})
                  yield invId, c.id
              | _ -> () ]

    /// 启动生成（若会话空闲且队列非空）。
    member this.MaybeStartGeneration(convId: Guid) : unit =
        let rt = getRuntime convId
        let shouldStart =
            lock rt (fun () ->
                match rt.generation with
                | Some g when not g.cancelled -> false
                | _ -> not (List.isEmpty rt.pendingQueue))
        if shouldStart then
            this.StartGeneration convId

    member private this.StartGeneration(convId: Guid) : unit =
        let rt = getRuntime convId
        // 防止并发启动
        let acquired =
            lock rt (fun () ->
                match rt.generation with
                | Some g when not g.cancelled -> false
                | _ ->
                    let cts = new CancellationTokenSource()
                    let generationId = Guid.CreateVersion7()
                    // 构建 agent（按会话当前配置）
                    let proj = getProjection ()
                    let conv = Projection.tryConversation proj convId
                    let config =
                        match conv with
                        | Some c when not c.deleted -> c.config
                        | _ -> SessionConfig.empty
                    let provider =
                        (getConfig ()).providers.TryFind config.provider
                    match provider with
                    | None ->
                        // 配置缺失：排队消息仍按插入点语义提交（决策 22/23），随后失败结束
                        this.DrainQueue(convId) |> ignore
                        let startedAt = DateTimeOffset.UtcNow
                        let errEv =
                            finishedEvent convId generationId "failed" (Some(sprintf "provider %s not configured" config.provider)) startedAt None
                        broadcastToConversation convId errEv
                        false
                    | Some p ->
                        let tools = toolRegistry.BuildTools config
                        let historyProvider =
                            WanxiangHistoryProvider(
                                (fun _ -> []), // 历史由编排层显式构造（决策 20：应用托管消息历史）
                                (fun cid msgs -> this.OnAgentResponse(cid, msgs)),
                                (fun cid ex -> this.OnAgentFailure(cid, ex)))
                        let agentRuntime = AgentRuntime(p, config.instructions, tools, historyProvider)
                        let session = agentRuntime.CreateSession convId
                        let startedAt = DateTimeOffset.UtcNow
                        rt.generation <-
                            Some
                                { generationId = generationId
                                  startedAtUtc = startedAt
                                  cts = cts
                                  runtime = agentRuntime
                                  agentSession = session
                                  agentConfig = config
                                  invalidConfig = None
                                  cancelled = false
                                  lastProviderMessages = [] }
                        broadcastToConversation convId (GenerationStarted {| conversationId = convId; generationId = generationId |})
                        let task = this.RunGenerationLoop(convId, generationId)
                        // 防止 task 未观察异常
                        task.ContinueWith(fun (t: Task) -> t.Exception |> ignore, TaskContinuationOptions.OnlyOnFaulted) |> ignore
                        true)
        if not acquired then
            ()

    /// 配置变更（决策 87）：同一 generation 内重建 agent 与 session，
    /// 下一次 Provider 调用使用最新配置；不取消在途调用、不篡改已完成结果。
    /// 若新配置引用的 Provider 已不存在（Q153/Q154）：不再用旧 agent 发起新调用，
    /// 标记 agentConfig 为无效并在下一轮调用时失败结束，不篡改会话配置。
    member private this.RebuildAgent(rt: ConversationRuntime, g: GenerationRuntime, newConfig: SessionConfig) : unit =
        let provider =
            (getConfig ()).providers.TryFind newConfig.provider
        match provider with
        | None ->
            // Provider 缺失：标记无效配置；下一轮 RunGenerationLoop 检测到后广播失败并结束（Q154）
            // 注意：不重置 g.cancelled——若用户已取消，RuntimeStateOf 应继续报告非 generating
            g.agentConfig <- newConfig
            // 记录一个特殊标记：使用 InvalidProviderConfig 使下一轮直接失败
            g.invalidConfig <- Some newConfig.provider
        | Some p ->
            let tools = toolRegistry.BuildTools newConfig
            let historyProvider =
                WanxiangHistoryProvider(
                    (fun _ -> []),
                    (fun cid msgs -> this.OnAgentResponse(cid, msgs)),
                    (fun cid ex -> this.OnAgentFailure(cid, ex)))
            let agentRuntime = AgentRuntime(p, newConfig.instructions, tools, historyProvider)
            // 会话 ID（conversationId）保留在 StateBag；新 session 绑定同一 conversationId
            let conversationId =
                match WanxiangHistoryProvider.ConversationIdOf g.agentSession with
                | Some cid -> cid
                | None -> Guid.Empty
            let session = agentRuntime.CreateSession conversationId
            g.runtime <- agentRuntime
            g.agentSession <- session
            g.agentConfig <- newConfig
            g.invalidConfig <- None
            // 与 OnAgentResponse 同锁：避免旧调用回调把消息追加到新 agent 名下（决策 89）
            lock rt (fun () -> g.lastProviderMessages <- [])

    /// 生成主循环：插入点 → Provider 调用 → 工具执行，直到完成/失败/取消。
    member private this.RunGenerationLoop(convId: Guid, generationId: Guid) : Task =
        task {
            let rt = getRuntime convId
            let mutable running = true
            while running do
                let gen =
                    lock rt (fun () ->
                        match rt.generation with
                        | Some g when g.generationId = generationId -> Some g
                        | _ -> None)
                match gen with
                | None -> running <- false
                | Some g ->
                    // 1. 取消检查（决策 88：取消只停止当前运行；内存中排队但尚未插入的用户消息
                    //    继续保留，等待下一个插入点——绝不先提交再取消，否则消息落盘却无回复）
                    if g.cts.IsCancellationRequested then
                        let ev = finishedEvent convId generationId "cancelled" None g.startedAtUtc None
                        broadcastToConversation convId ev
                        lock rt (fun () -> if (rt.generation |> Option.map (fun x -> x.generationId)) = Some generationId then rt.generation <- None)
                        running <- false
                    else
                        // 2. 插入点：排空队列（决策 24：全部 FIFO 提交，一次 Provider 调用）
                        let batch = this.DrainQueue convId
                        // 3. 构造上下文（历史 + 已提交新消息）
                        let context = loadContextMessages convId
                        if List.isEmpty context && List.isEmpty batch then
                            running <- false
                        else
                            // 4. Provider 调用（配置变更只影响下一次调用，决策 87：
                            //    已经发出的调用继续按其启动时配置完成并记账）
                            let latestConfig =
                                match tryGetProjectionConversation convId with
                                | Some c -> Some c.config
                                | None -> None
                            if g.invalidConfig.IsSome then
                                // Q153/Q154：配置引用的 Provider 已不存在 → 不发起新调用，失败结束（不篡改会话配置）
                                logInfo(sprintf "generation %O failed: provider %s not configured" generationId g.invalidConfig.Value)
                                let ev =
                                    finishedEvent convId generationId "failed" (Some(sprintf "provider %s not configured" g.invalidConfig.Value)) g.startedAtUtc None
                                broadcastToConversation convId ev
                                lock rt (fun () -> if (rt.generation |> Option.map (fun x -> x.generationId)) = Some generationId then rt.generation <- None)
                                running <- false
                            elif latestConfig.IsSome && latestConfig.Value <> g.agentConfig then
                                // 配置变化：同一 generation 内重建 agent（新 session），
                                // 下一次 Provider 调用使用最新配置；不取消、不篡改已完成的结果。
                                this.RebuildAgent(rt, g, latestConfig.Value)
                                // 继续循环（下一次迭代用新配置发起调用）
                            else
                                let onDelta (deltaMsg: ChatMessage) =
                                    let deltaEv =
                                        GenerationDelta
                                            {| conversationId = convId
                                               generationId = generationId
                                               payload = MessageSerde.toJsonNode deltaMsg |}
                                    broadcastToConversation convId deltaEv
                                // 多条消息合并为一次请求（按消息列表传入）
                                let! result =
                                    g.runtime.RunStreaming(g.agentSession, context, onDelta, g.cts.Token)
                                match result with
                                | AgentCallResult.Cancelled ->
                                    let ev = finishedEvent convId generationId "cancelled" None g.startedAtUtc None
                                    broadcastToConversation convId ev
                                    lock rt (fun () -> if (rt.generation |> Option.map (fun x -> x.generationId)) = Some generationId then rt.generation <- None)
                                    running <- false
                                | Failed ex ->
                                    logInfo(sprintf "generation %O failed: %s" generationId ex.Message)
                                    let ev = finishedEvent convId generationId "failed" (Some ex.Message) g.startedAtUtc None
                                    broadcastToConversation convId ev
                                    lock rt (fun () -> if (rt.generation |> Option.map (fun x -> x.generationId)) = Some generationId then rt.generation <- None)
                                    running <- false
                                | Completed usage ->
                                    // 响应消息已由 HistoryProvider 回调提交；检查工具调用
                                    // （OnAgentResponse 在 rt 锁内写 lastProviderMessages，读取必须同锁）
                                    let responses = lock rt (fun () -> g.lastProviderMessages)
                                    let calls =
                                        responses
                                        |> List.collect MessageSerde.toolCalls
                                    if List.isEmpty calls then
                                        // 决策 22/24：idle 是可插入点——若流式期间有新消息入队，则继续循环排空，
                                        // 不立即结束 generation（避免排队消息需等下次显式动作才处理）
                                        let hasQueued =
                                            lock rt (fun () -> not (List.isEmpty rt.pendingQueue))
                                        if hasQueued then
                                            lock rt (fun () -> g.lastProviderMessages <- [])
                                            // 继续 while 循环（下一轮 DrainQueue 排空 + 再次调 Provider）
                                            ()
                                        else
                                            let ev = finishedEvent convId generationId "completed" None g.startedAtUtc usage
                                            broadcastToConversation convId ev
                                            lock rt (fun () -> if (rt.generation |> Option.map (fun x -> x.generationId)) = Some generationId then rt.generation <- None)
                                            running <- false
                                    else
                                        // 5. 并行执行工具（决策 92），全部完成后统一返回 Provider（保持原顺序）
                                        //    每条完整 Tool Result 在完成时已分别记账并推送（ExecuteTools 内）
                                        let! _ = this.ExecuteTools(convId, generationId, calls, g)
                                        // 继续循环（下一轮再调 Provider）
                                        lock rt (fun () -> g.lastProviderMessages <- [])
        }

    /// 并行执行一批工具调用。返回按原顺序的 (call, outcome) 列表。
    /// 决策 89：Tool 成功取消且没有完整结果时，不补写任何消息（NDJSON 无伪造结果）；
    /// 决策 92：每个完整 Tool Result 一完成就分别记账并实时推送给客户端（不等待整批）。
    member private this.ExecuteTools(convId: Guid, generationId: Guid, calls: FunctionCallContent list, g: GenerationRuntime) : Task<(FunctionCallContent * ToolOutcome) list> =
        task {
            // 同一批调用使用同一工具快照
            let allTools = toolRegistry.BuildTools g.agentConfig
            let findTool (call: FunctionCallContent) : AITool option =
                // M.E.AI 生成的函数名与注册名一致；先尝试直接匹配
                allTools |> List.tryFind (fun t -> t.Name = call.Name || t.Name = sprintf "builtin_%s" (call.Name.Replace("builtin:", "").Replace(".", "_")))
            let runOne (call: FunctionCallContent) : Task<(FunctionCallContent * ToolOutcome)> =
                task {
                    match findTool call with
                    | None ->
                        let text = sprintf """{"error":"tool %s not found"}""" call.Name
                        this.RecordSubmitOutcome(convId, this.SubmitMessage(convId, MessageSerde.toJsonNode (MessageSerde.toolResultMessage call text)))
                        return call, ToolResult text
                    | Some tool ->
                        try
                            match tool with
                            | :? AIFunction as f ->
                                let args = AIFunctionArguments(call.Arguments)
                                let! result = f.InvokeAsync(args, g.cts.Token)
                                let text =
                                    match result with
                                    | null -> """{"result":null}"""
                                    | :? string as s -> s
                                    | other -> JsonSerializer.Serialize(other)
                                // 完成即记账（决策 92）：不等待整批
                                this.RecordSubmitOutcome(convId, this.SubmitMessage(convId, MessageSerde.toJsonNode (MessageSerde.toolResultMessage call text)))
                                return call, ToolResult text
                            | _ ->
                                let text = """{"error":"tool is not an AIFunction"}"""
                                this.RecordSubmitOutcome(convId, this.SubmitMessage(convId, MessageSerde.toJsonNode (MessageSerde.toolResultMessage call text)))
                                return call, ToolResult text
                        with
                        | :? OperationCanceledException ->
                            // 决策 89：Tool 成功取消且没有完整结果 → 不补写任何消息
                            return call, ToolCancelled
                        | e ->
                            let text = sprintf """{"error":"%s"}""" (e.Message.Replace("\"", "'"))
                            this.RecordSubmitOutcome(convId, this.SubmitMessage(convId, MessageSerde.toJsonNode (MessageSerde.toolResultMessage call text)))
                            return call, ToolResult text
                }
            let! results = Task.WhenAll [ for c in calls -> runOne c ]
            return List.ofArray results
        }

    /// HistoryProvider 回调：Agent 响应消息 → 记账（完整消息单独提交）。
    member private this.OnAgentResponse(convId: Guid, msgs: ChatMessage list) =
        let rt = getRuntime convId
        let shouldCommit =
            lock rt (fun () ->
                match rt.generation with
                | Some g when not g.cancelled ->
                    // 取消后迟到的 Provider 输出不记账（决策 89）
                    g.lastProviderMessages <- g.lastProviderMessages @ msgs
                    true
                | _ -> false)
        if shouldCommit then
            for m in msgs do
                this.RecordSubmitOutcome(convId, this.SubmitMessage(convId, MessageSerde.toJsonNode m))

    member private this.OnAgentFailure(convId: Guid, ex: exn) =
        logInfo(sprintf "agent failure for %O: %s" convId ex.Message)

    /// 取消生成（决策 88/89：合作式取消；不记账；已提交消息保留）。
    member this.CancelGeneration(convId: Guid, generationId: Guid) : Result<unit, WanxiangError> =
        let rt = getRuntime convId
        lock rt (fun () ->
            match rt.generation with
            | Some g when g.generationId = generationId ->
                g.cancelled <- true
                g.cts.Cancel()
                Ok()
            | _ -> Error(GenerationNotFound(convId, generationId)))

    /// 关闭：取消所有生成。
    member _.Dispose() =
        if not disposed then
            disposed <- true
            for kv in runtimes do
                lock kv.Value (fun () ->
                    match kv.Value.generation with
                    | Some g -> g.cts.Cancel()
                    | None -> ())
