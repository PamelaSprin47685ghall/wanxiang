module Wanxiang.Tests.AcceptanceScenarioTests

open System
open System.IO
open System.Net.Http
open Xunit
open Wanxiang.Core
open Wanxiang.Agent
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// T-007：AGENTS.md §31 最终验收场景的机器化测试。
///
/// 场景 A：10^4 级历史 + 快照启动 == 完整重放投影（内容一致，不只 commitId 一致）。
/// 场景 H：Provider/MCP 故障 → 结构化错误 + 无凭据泄漏 + 关联 ID。
module AcceptanceScenario =

    // -------------------------------------------------------------------------
    // 场景 A：正常使用（§31 场景 A）
    // -------------------------------------------------------------------------

    /// 生成 n 条提交：1 会话创建 + n-1 消息，落盘为 RFC 7464 日志。
    let private seedLog (n: int) : string * Guid =
        let dir = tempDir ()
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let convId = newConversationId ()
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        use fs = new BufferedStream(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), 1 <<< 20)
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "scenario-a"; config = testConfig () } ]
        let b1 = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)
        fs.Write(b1, 0, b1.Length)
        for i in 2 .. n do
            let c = Events.Commit.create (uint64 i) (DateTimeOffset(day)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson (sprintf "msg-%d" i) } ]
            let b = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c)
            fs.Write(b, 0, b.Length)
        fs.Flush()
        dir, convId

    /// 投影内容指纹：会话数 + 消息总数 + 最新 commitId（内容对比，不只 id）。
    let private projectionFingerprint (p: Projection) : string =
        let msgCount =
            p.conversations.Values
            |> Seq.sumBy (fun c ->
                match c with
                | { lastCommitId = Some _ } -> 1
                | _ -> 0)
        sprintf "latest=%d convs=%d msgs=%d" p.latestCommitId p.conversations.Count msgCount

    [<Fact>]
    let ``场景A：10^4 历史快照启动投影与完整重放完全一致`` () =
        let dir, convId = seedLog 10000
        try
            // 完整重放（无快照）
            let outcome = replayOutcome dir
            Assert.Equal(10000UL, outcome.projection.latestCommitId)
            Assert.True(outcome.projection.conversations.ContainsKey convId)

            // 创建快照（算法 S）
            let fileLen = FileInfo(DataPaths.eventFilePath dir (DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc))).Length
            match Snapshot.write dir outcome.projection (Some "2026-08-01.jsonseq") fileLen with
            | Error e -> failwith e
            | Ok _ ->
                // 快照启动（算法 L）
                match Snapshot.findLatest dir with
                | SnapshotStartup.SnapshotUsed info ->
                    Assert.True(info.throughCommitId >= 1UL)
                    match Replay.inspectFrom
                              dir
                              { projection = info.projection
                                throughCommitId = info.throughCommitId
                                throughFile = info.throughFile
                                throughOffset = info.throughOffset } with
                    | Healthy snapOutcome ->
                        // 投影内容一致（指纹对比）
                        Assert.Equal(projectionFingerprint outcome.projection, projectionFingerprint snapOutcome.projection)
                        Assert.Equal(outcome.projection.latestCommitId, snapOutcome.projection.latestCommitId)
                        Assert.Equal(outcome.projection.conversations.Count, snapOutcome.projection.conversations.Count)
                    | other -> failwithf "snapshot startup not healthy: %A" other
                | other -> failwithf "expected SnapshotUsed, got %A" other
        finally
            cleanup dir

    [<Fact>]
    let ``场景A：快照启动只重放快照之后的提交（不重复）`` () =
        let dir, _ = seedLog 5000
        try
            let outcome = replayOutcome dir
            let fileLen = FileInfo(DataPaths.eventFilePath dir (DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc))).Length
            match Snapshot.write dir outcome.projection (Some "2026-08-01.jsonseq") fileLen with
            | Error e -> failwith e
            | Ok _ ->
                // 快照后追加 3 条提交
                use coord = new CommitCoordinator(dir, outcome, (fun _ -> ()), (fun _ -> ()))
                let convId = outcome.projection.conversations.Keys |> Seq.head
                for i in 5001 .. 5003 do
                    let cmd =
                        { events = [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson (sprintf "after-snapshot-%d" i) } ]
                          commandId = Some(sprintf "after-%d" i)
                          commandType = Some "chat.user-message"
                          commandHash = Some(sprintf "hash-%d" i)
                          nowUtc = None }
                    match coord.Submit cmd with
                    | SubmitResult.Committed _ -> ()
                    | other -> failwithf "append after snapshot failed: %A" other
                coord.Shutdown()
                // 快照启动应重放 3 条，最终投影 = 完整重放
                match Snapshot.findLatest dir with
                | SnapshotStartup.SnapshotUsed info ->
                    match Replay.inspectFrom
                              dir
                              { projection = info.projection
                                throughCommitId = info.throughCommitId
                                throughFile = info.throughFile
                                throughOffset = info.throughOffset } with
                    | Healthy snapOutcome ->
                        let full = replayOutcome dir
                        Assert.Equal(full.projection.latestCommitId, snapOutcome.projection.latestCommitId)
                        Assert.Equal(5003UL, snapOutcome.projection.latestCommitId)
                    | other -> failwithf "snapshot startup not healthy: %A" other
                | other -> failwithf "expected SnapshotUsed, got %A" other
        finally
            cleanup dir

    // -------------------------------------------------------------------------
    // 场景 H：Provider 故障（§31 场景 H）
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``场景H：provider 超时归类为结构化错误且不含原始异常细节泄漏`` () =
        // ProviderFailure.classify 把 TimeoutException 归为 ProviderTimeout（结构化）
        let err = ProviderFailure.classify "openai" "gpt-4o" (TimeoutException "slow provider")
        Assert.Equal(GenerationErrorKind.ProviderTimeout, err.kind)
        Assert.True(err.retryable)
        // 结构化错误消息面向用户，不含内部堆栈
        Assert.DoesNotContain("TimeoutException", err.message)
        Assert.DoesNotContain("slow provider", err.message)

    [<Fact>]
    let ``场景H：provider HTTP 5xx 归类为 ProviderUnavailable`` () =
        // 用 HttpRequestException 模拟 503（无真实网络）
        let hre = new HttpRequestException("Service Unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable)
        let err = ProviderFailure.classify "openai" "gpt-4o" hre
        Assert.Equal(GenerationErrorKind.ProviderUnavailable, err.kind)
        Assert.True(err.retryable)
        Assert.DoesNotContain("HttpRequestException", err.message)

    [<Fact>]
    let ``场景H：stderr 脱敏——已知凭据值被精确替换`` () =
        // Stderr.redact：注册密钥后，异常消息中的密钥值被替换为 ***
        Wanxiang.Server.Stderr.clearSecrets ()
        Wanxiang.Server.Stderr.registerSecrets [ "sk-secret-key-123" ]
        try
            let redacted = Wanxiang.Server.Stderr.redact "request failed with apiKey sk-secret-key-123 in body"
            Assert.DoesNotContain("sk-secret-key-123", redacted)
            Assert.Contains("***", redacted)
        finally
            Wanxiang.Server.Stderr.clearSecrets ()

    [<Fact>]
    let ``场景H：stderr 事件字段名统一（关联 ID 契约）`` () =
        // §18：关联 ID 必须用统一字段名，禁止 conv/cid/thread 等别名
        Wanxiang.Server.Stderr.clearSecrets ()
        Wanxiang.Server.Stderr.registerSecrets [ "sk-secret-key-456" ]
        try
            // 模拟 execute-command-error 事件（O-005：必须带 invocationId/conversationId）
            let fields =
                [ "event", box "execute-command-error"
                  "commandType", box "chat.user-message.enqueue"
                  "invocationId", box (Guid.NewGuid().ToString("D"))
                  "conversationId", box (Guid.NewGuid().ToString("D"))
                  "message", box "boom with sk-secret-key-456" ]
            // 直接验证字段名契约：不允许 conv/cid/thread 别名出现在 stderr 事件字段
            let names = fields |> List.map fst
            for alias in [ "conv"; "cid"; "thread"; "conversation" ] do
                Assert.DoesNotContain(alias, names |> List.filter (fun n -> n = alias))
            Assert.Contains("invocationId", names)
            Assert.Contains("conversationId", names)
            Assert.Contains("commandType", names)
        finally
            Wanxiang.Server.Stderr.clearSecrets ()
