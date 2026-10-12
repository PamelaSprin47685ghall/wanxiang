namespace Wanxiang.Server

open System
open System.Collections.Concurrent
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol

/// 一次审批的结果。
type ApprovalOutcome =
    /// 用户批准
    | Approved
    /// 被拒绝：`userChoice` 为 true 表示用户亲自拒绝；`reason` 是写进工具结果、给模型和用户看的人话。
    | Denied of reason: string * userChoice: bool
    /// 生成被取消，审批作废；不产生工具结果（决策 89）
    | ApprovalCancelled

type internal PendingApproval =
    { approvalId: Guid
      conversationId: Guid
      generationId: Guid
      toolId: string
      request: WireEvent
      expiresAt: DateTimeOffset
      policy: ToolPolicy
      tcs: TaskCompletionSource<ApprovalOutcome> }


/// 工具审批代理（第 71 节，补充决策 93）。
///
/// - 审批状态**只存内存**：不写日志、不进快照，进程重启即消失（原则 2、决策 22）。
/// - 先到者胜：`Resolve` 对每个审批只会成功一次，晚到的决定得到「已处理」。
/// - 所需审批但没有具备审批能力的客户端在线 → 立即拒绝，不死等。
/// - 到点（默认 5 分钟）自动拒绝。
type ToolApprovalBroker(getConfig: unit -> AppConfig, broadcast: Guid -> WireEvent -> unit) =

    let pending = ConcurrentDictionary<Guid, PendingApproval>()
    /// 「移出 pending」与「记入 recent」必须不可分，否则落败者可能在两步之间查不到结果。
    let resolveLock = obj ()
    /// 「首次询问」被批准过的 (对话, 工具)；只活在内存里。
    let grants = ConcurrentDictionary<struct (Guid * string), bool>()
    /// 最近处理过的审批，用于告诉晚到的决定者「已处理」。
    let recent = ConcurrentDictionary<Guid, string * string>()
    let recentOrder = ConcurrentQueue<Guid>()
    let recentLimit = 256

    let remember (id: Guid) (status: string) (reason: string) =
        recent[id] <- (status, reason)
        recentOrder.Enqueue id
        while recentOrder.Count > recentLimit do
            match recentOrder.TryDequeue() with
            | true, old -> recent.TryRemove old |> ignore
            | _ -> ()

    let statusOf (outcome: ApprovalOutcome) : string * string =
        match outcome with
        | Approved -> "approved", ""
        | Denied(reason, true) -> "denied", reason
        | Denied(reason, false) -> "expired", reason
        | ApprovalCancelled -> "cancelled", "生成已取消，审批作废。"

    /// 判定一次调用是否需要审批。
    member _.NeedsApproval(conversationId: Guid, toolId: string) : bool =
        match ToolsConfig.policyOf (getConfig ()).tools toolId with
        | Auto -> false
        | AskEach -> true
        | AskFirst -> not (grants.ContainsKey(struct (conversationId, toolId)))

    /// 是否有具备审批能力的客户端正在观察该对话。由服务端接线时设置；默认没有（保守地拒绝）。
    member val CapabilityProbe: Guid -> bool = (fun _ -> false) with get, set

    /// 尝试以第一个有效决定结束一个审批。返回是否由本次调用结束。
    member private _.Resolve(approvalId: Guid, outcome: ApprovalOutcome) : bool =
        let taken =
            lock resolveLock (fun () ->
                match pending.TryRemove approvalId with
                | true, p ->
                    let status, reason = statusOf outcome
                    remember approvalId status reason
                    Some(p, status, reason)
                | _ -> None)
        match taken with
        | Some(p, status, reason) ->
            (match outcome, p.policy with
             | Approved, AskFirst -> grants[struct (p.conversationId, p.toolId)] <- true
             | _ -> ())
            p.tcs.TrySetResult outcome |> ignore
            broadcast p.conversationId
                (ToolApprovalResolved
                    {| approvalId = approvalId
                       conversationId = p.conversationId
                       status = status
                       reason = reason
                       alreadyHandled = false |})
            true
        | None -> false

    /// 客户端的决定。`reply` 把「已处理」单独回给晚到的决定者。
    member this.Decide(approvalId: Guid, approved: bool, reply: WireEvent -> unit) : unit =
        let outcome = if approved then Approved else Denied(ToolSummary.userDeniedText, true)
        if not (this.Resolve(approvalId, outcome)) then
            let status, reason =
                lock resolveLock (fun () ->
                    match recent.TryGetValue approvalId with
                    | true, v -> v
                    | _ -> "expired", "这个审批已经失效（可能因为服务已重启）。")
            reply (
                ToolApprovalResolved
                    {| approvalId = approvalId
                       conversationId = Guid.Empty
                       status = status
                       reason = reason
                       alreadyHandled = true |})

    /// 取消一次生成时，作废它名下等待中的审批。
    member this.CancelGeneration(conversationId: Guid, generationId: Guid) : unit =
        for kv in pending |> Seq.toArray do
            if kv.Value.conversationId = conversationId && kv.Value.generationId = generationId then
                this.Resolve(kv.Key, ApprovalCancelled) |> ignore

    /// 对话被删除：作废全部等待中的审批并清掉「首次询问」记录。
    member this.ClearConversation(conversationId: Guid) : unit =
        for kv in pending |> Seq.toArray do
            if kv.Value.conversationId = conversationId then
                this.Resolve(kv.Key, ApprovalCancelled) |> ignore
        for k in grants.Keys |> Seq.toArray do
            let struct (c, _) = k
            if c = conversationId then grants.TryRemove k |> ignore

    /// 某对话里仍在等待的审批请求（重连或刚声明审批能力的客户端补收）。
    member _.PendingFor(conversationId: Guid) : WireEvent list =
        [ for kv in pending do
              if kv.Value.conversationId = conversationId then
                  match kv.Value.request with
                  | ToolApprovalRequested r ->
                      let left = int (max 0.0 (kv.Value.expiresAt - DateTimeOffset.UtcNow).TotalSeconds)
                      yield ToolApprovalRequested {| r with expiresInSeconds = left |}
                  | other -> yield other ]

    member _.PendingCount = pending.Count

    /// 发起审批并等待结果。
    member this.Request
        (conversationId: Guid, generationId: Guid, callId: string, toolName: string, toolId: string, args: JsonNode, ct: CancellationToken)
        : Task<ApprovalOutcome> =
        task {
            if not (this.CapabilityProbe conversationId) then
                return
                    Denied("这个工具需要先经用户批准，但此刻没有支持审批的客户端在线，所以调用被拒绝。请在支持审批的客户端里打开这个对话后重试。", false)
            else
                let cfg = (getConfig ()).tools
                let seconds = max 1 cfg.approvalTimeoutSeconds
                let approvalId = Guid.NewGuid()
                let policy = ToolsConfig.policyOf cfg toolId
                let request =
                    ToolApprovalRequested
                        {| approvalId = approvalId
                           conversationId = conversationId
                           generationId = generationId
                           callId = callId
                           toolName = toolName
                           summary = ToolSummary.describe toolName args
                           arguments = (if isNull args then null else args.DeepClone())
                           policy = ToolPolicy.toToml policy
                           expiresInSeconds = seconds |}
                let p =
                    { approvalId = approvalId
                      conversationId = conversationId
                      generationId = generationId
                      toolId = toolId
                      request = request
                      expiresAt = DateTimeOffset.UtcNow.AddSeconds(float seconds)
                      policy = policy
                      tcs = TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously) }
                pending[approvalId] <- p
                broadcast conversationId request
                use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource ct
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(float seconds))
                use _reg =
                    timeoutCts.Token.Register(fun () ->
                        if ct.IsCancellationRequested then this.Resolve(approvalId, ApprovalCancelled) |> ignore
                        else
                            this.Resolve(
                                approvalId,
                                Denied(
                                    sprintf "审批超时：%d 秒内没有人处理，工具调用已被拒绝。" seconds,
                                    false))
                            |> ignore)
                return! p.tcs.Task
        }
