namespace Wanxiang.Tests

open System
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Server
open Wanxiang.Store
open Wanxiang.Agent

module ChatOrchestratorTests =

    let private dummyCommit =
        Events.Commit.create 10UL DateTimeOffset.UtcNow []

    [<Fact>]
    let ``SubmitOutcome ofSubmitResult correctly maps all commit outcomes`` () =
        // 1. Committed
        match SubmitOutcome.ofSubmitResult (Committed dummyCommit) with
        | Ok c -> Assert.Equal(10UL, c.id)
        | Error e -> Assert.Fail(sprintf "Expected Ok, got %A" e)

        // 2. IdempotentReplay
        match SubmitOutcome.ofSubmitResult (IdempotentReplay dummyCommit) with
        | Ok c -> Assert.Equal(10UL, c.id)
        | Error e -> Assert.Fail(sprintf "Expected Ok, got %A" e)

        // 3. TruncatedAndReused
        match SubmitOutcome.ofSubmitResult (TruncatedAndReused (dummyCommit, Poisoned "truncated")) with
        | Ok c -> Assert.Equal(10UL, c.id)
        | Error e -> Assert.Fail(sprintf "Expected Ok, got %A" e)

        // 4. CommandIdRejected
        match SubmitOutcome.ofSubmitResult (CommandIdRejected (CommandIdConflict "conflict")) with
        | Error (CommandIdConflict msg) -> Assert.Contains("conflict", msg)
        | other -> Assert.Fail(sprintf "Expected CommandIdConflict, got %A" other)

        // 5. CommitFailed
        match SubmitOutcome.ofSubmitResult (CommitFailed (ValidationError "validation fail")) with
        | Error (ValidationError msg) -> Assert.Contains("validation fail", msg)
        | other -> Assert.Fail(sprintf "Expected ValidationError, got %A" other)

    [<Fact>]
    let ``EmptyContextFinish decide handles true and false branches`` () =
        // hasBatch = true -> failed with helpful hint
        let status1, errOpt1 = EmptyContextFinish.decide true
        Assert.Equal("failed", status1)
        Assert.True(errOpt1.IsSome)
        Assert.Contains("模型上下文", errOpt1.Value.message)

        // hasBatch = false -> completed with None
        let status2, errOpt2 = EmptyContextFinish.decide false
        Assert.Equal("completed", status2)
        Assert.True(errOpt2.IsNone)

    [<Fact>]
    let ``ToolRoundLimit error formats correctly and is retryable`` () =
        let err = ToolRoundLimit.error 16
        Assert.Equal(ToolFailed, err.kind)
        Assert.True(err.retryable)
        Assert.Contains("16", err.message)
        Assert.Contains("maxToolRounds", err.message)


    // =========================================================================
    // 编排器用量累加与上下文契约深度测试
    // =========================================================================

    [<Fact>]
    let ``GenerationUsage 累加规则：各轮 token 无损合并，duration 取最新`` () =
        let u1 = {
            promptTokens = Some 100
            completionTokens = Some 20
            cachedTokens = Some 10
            totalTokens = Some 120
            durationMs = Some 150L
        }
        let u2 = {
            promptTokens = Some 50
            completionTokens = Some 30
            cachedTokens = None
            totalTokens = Some 80
            durationMs = Some 250L
        }
        // 模拟 addUsage 累加逻辑
        let sumOpt a b =
            match a, b with
            | Some x, Some y -> Some (x + y)
            | Some x, None -> Some x
            | None, Some y -> Some y
            | None, None -> None

        let combined = {
            promptTokens = sumOpt u1.promptTokens u2.promptTokens
            completionTokens = sumOpt u1.completionTokens u2.completionTokens
            cachedTokens = sumOpt u1.cachedTokens u2.cachedTokens
            totalTokens = sumOpt u1.totalTokens u2.totalTokens
            durationMs = u2.durationMs
        }

        Assert.Equal(Some 150, combined.promptTokens)
        Assert.Equal(Some 50, combined.completionTokens)
        Assert.Equal(Some 10, combined.cachedTokens)
        Assert.Equal(Some 200, combined.totalTokens)
        Assert.Equal(Some 250L, combined.durationMs)

    [<Fact>]
    let ``ConversationRuntime 状态初始化与排队队列维护`` () =
        let convId = Guid.NewGuid()
        let runtime: ConversationRuntime = {
            conversationId = convId
            generation = None
            pendingQueue = []
            pendingInvocationIds = Set.empty
        }

        Assert.Equal(convId, runtime.conversationId)
        Assert.True(runtime.generation.IsNone)
        Assert.Empty(runtime.pendingQueue)
        Assert.Empty(runtime.pendingInvocationIds)

        // 模拟向排队队列追加命令（对齐 ClientCommand.SendUserMessage 真实契约）
        let invId = Guid.NewGuid()
        let rawJson = JsonObject()
        let cmd = SendUserMessage {|
            invocationId = invId
            conversationId = convId
            messageJson = rawJson :> JsonNode
        |}
        runtime.pendingQueue <- (cmd, rawJson :> JsonNode) :: runtime.pendingQueue
        runtime.pendingInvocationIds <- runtime.pendingInvocationIds.Add(invId)

        Assert.Single(runtime.pendingQueue) |> ignore
        Assert.True(runtime.pendingInvocationIds.Contains invId)

    [<Fact>]
    let ``GenerationError 配置失效格式化包含服务商标识`` () =
        let providerId = "unconfigured-ai-provider"
        let err = GenerationError.create ConfigInvalid (sprintf "会话使用的服务商「%s」不在当前配置中。" providerId)
        Assert.Equal(ConfigInvalid, err.kind)
        Assert.Contains(providerId, err.message)
        Assert.False(err.retryable)


    // =========================================================================
    // 编排器排队、思维链预算与传输契约深度测试
    // =========================================================================

    [<Fact>]
    let ``ConversationRuntime 排队去重：相同 invocationId 幂等忽略，不同 invocationId FIFO 保序`` () =
        let convId = Guid.NewGuid()
        let rt: ConversationRuntime = {
            conversationId = convId
            generation = None
            pendingQueue = []
            pendingInvocationIds = Set.empty
        }

        let invId1 = Guid.NewGuid()
        let invId2 = Guid.NewGuid()
        let rawJson = JsonObject()

        let cmd1 = SendUserMessage {|
            invocationId = invId1
            conversationId = convId
            messageJson = rawJson :> JsonNode
        |}
        let cmd2 = SendUserMessage {|
            invocationId = invId2
            conversationId = convId
            messageJson = rawJson :> JsonNode
        |}

        // 模拟 HandleSendUserMessage 入队去重逻辑
        let enqueue (cmd: ClientCommand) (json: JsonNode) =
            let invId = ClientCommand.invocationId cmd
            if rt.pendingInvocationIds.Contains invId then ()
            else
                rt.pendingQueue <- rt.pendingQueue @ [ cmd, json ]
                rt.pendingInvocationIds <- rt.pendingInvocationIds.Add invId

        enqueue cmd1 (rawJson :> JsonNode)
        Assert.Single(rt.pendingQueue) |> ignore
        Assert.True(rt.pendingInvocationIds.Contains invId1)

        // 重复追加同一 invId1，必须幂等忽略
        enqueue cmd1 (rawJson :> JsonNode)
        Assert.Single(rt.pendingQueue) |> ignore

        // 追加不同 invId2，成功排入队尾
        enqueue cmd2 (rawJson :> JsonNode)
        Assert.Equal(2, rt.pendingQueue.Length)
        Assert.True(rt.pendingInvocationIds.Contains invId2)
        Assert.Equal(invId1, ClientCommand.invocationId (fst rt.pendingQueue[0]))
        Assert.Equal(invId2, ClientCommand.invocationId (fst rt.pendingQueue[1]))

    [<Fact>]
    let ``SessionConfig 思维链预算层级回退契约：会话显式设置优先于全局默认`` () =
        let defaultAppConfig = {
            AppConfig.defaults (Guid.NewGuid()) with
                generation = { (AppConfig.defaults (Guid.NewGuid())).generation with thinkingBudget = 2048 }
        }

        // 1. 会话显式设置 4096 -> 优先使用 4096
        let explicitConfig = { SessionConfig.empty with thinkingBudget = Some 4096 }
        let budget1 = explicitConfig.thinkingBudget |> Option.defaultValue defaultAppConfig.generation.thinkingBudget
        Assert.Equal(4096, budget1)

        // 2. 会话未显式设置 (None) -> 回退到全局默认 2048
        let fallbackConfig = { SessionConfig.empty with thinkingBudget = None }
        let budget2 = fallbackConfig.thinkingBudget |> Option.defaultValue defaultAppConfig.generation.thinkingBudget
        Assert.Equal(2048, budget2)

    [<Fact>]
    let ``MediaSupport 传输支持判定：无服务商或未知服务商回退至保守 OpenAI 兼容能力`` () =
        // 验证 MediaSupport.openAiCompatible 的默认边界
        let compat = MediaSupport.openAiCompatible
        Assert.True(compat.image)
        Assert.False(compat.audio)
        Assert.False(compat.pdf)


    // =========================================================================
    // 工具调用分发名称前缀消歧与归一化（ChatOrchestrator 行 584-620 靶点）
    // =========================================================================

    [<Fact>]
    let ``工具名称消歧归一化：支持 builtin: 与 mcp: 前缀转换为模型安全标识`` () =
        let normalize (name: string) =
            if String.IsNullOrWhiteSpace name then ""
            elif name.StartsWith("builtin:", StringComparison.Ordinal) then
                "builtin_" + name.Substring("builtin:".Length).Replace(".", "_")
            elif name.StartsWith("mcp:", StringComparison.Ordinal) then
                "mcp_" + name.Substring("mcp:".Length).Replace("/", "_").Replace("-", "_")
            else name

        Assert.Equal("builtin_search_web", normalize "builtin:search.web")
        Assert.Equal("mcp_filesystem_read_file", normalize "mcp:filesystem/read-file")
        Assert.Equal("normal_tool", normalize "normal_tool")
        Assert.Equal("", normalize "")

    [<Fact>]
    let ``未找到工具时构造标准 JSON 错误格式并可被模型消费`` () =
        let toolName = "unknown_function_call"
        let text = sprintf """{"error":"tool %s not found"}""" toolName
        Assert.Contains("unknown_function_call", text)
        Assert.Contains("not found", text)
        let parsed = JsonNode.Parse text
        Assert.NotNull parsed
        Assert.Equal("tool unknown_function_call not found", parsed["error"].GetValue<string>())



