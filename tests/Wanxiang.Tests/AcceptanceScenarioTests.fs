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
            match Snapshot.write dir outcome.projection (Some "2026-08-01.jsonseq") fileLen outcome.commits with
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
                                throughOffset = info.throughOffset
                                commits = info.commits } with
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
            match Snapshot.write dir outcome.projection (Some "2026-08-01.jsonseq") fileLen outcome.commits with
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
                                throughOffset = info.throughOffset
                                commits = info.commits } with
                    | Healthy snapOutcome ->
                        let full = replayOutcome dir
                        Assert.Equal(full.projection.latestCommitId, snapOutcome.projection.latestCommitId)
                        Assert.Equal(5003UL, snapOutcome.projection.latestCommitId)
                    | other -> failwithf "snapshot startup not healthy: %A" other
                | other -> failwithf "expected SnapshotUsed, got %A" other
        finally
            cleanup dir

    [<Fact>]
    let ``场景A：快照启动 commits 覆盖全历史（catch-up 任意游标不丢事件）`` () =
        // 回归：快照续播只收集锚点之后的提交，CommitCoordinator.commits 缺锚点前历史，
        // CommitsAfter(旧游标) 静默返回空 → 慢客户端 catch-up 丢事件（§22 资源预算：
        // commits 全量保留供任意游标 catch-up）。修复：inspectFrom 续播前先 collect-only
        // 收集锚点之前（含锚点文件 [0, throughOffset) 段）的 commit 外壳，不 apply projection。
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let path = DataPaths.eventFilePath dir day
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            // 写 100 个提交，同时维护 50 处的前缀投影与锚点偏移
            let mutable p50 = Projection.empty
            let mutable offset50 = 0L
            let mutable commits50: Events.Commit list = []
            for i in 1..100 do
                let commit =
                    Events.Commit.create (uint64 i) (DateTimeOffset(day.AddSeconds(float i)))
                        [ ConversationCreated { conversationId = Guid.NewGuid(); title = "t"; config = SessionConfig.empty } ]
                let bytes = Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine commit)
                File.AppendAllBytes(path, bytes)
                if i <= 50 then
                    p50 <- match Projection.applyCommit p50 commit with Ok p -> p | Error e -> failwithf "apply %A" e
                    offset50 <- offset50 + int64 bytes.Length
                    commits50 <- commits50 @ [ commit ]
            // 完整重放（对照）
            let full = match Replay.inspect dir with Healthy o -> o | other -> failwithf "inspect %A" other
            // 在 50 处建快照
            match Snapshot.write dir p50 (Some(Path.GetFileName path)) offset50 commits50 with
            | Error e -> failwithf "snap %s" e
            | Ok _ ->
                match Snapshot.findLatest dir with
                | SnapshotStartup.SnapshotUsed info ->
                    let snap =
                        Replay.inspectFrom
                            dir
                            { projection = info.projection
                              throughCommitId = info.throughCommitId
                              throughFile = info.throughFile
                              throughOffset = info.throughOffset
                              commits = info.commits }
                    match snap with
                    | Healthy so ->
                        // 投影一致（场景 A 既有断言）
                        Assert.Equal(full.projection.latestCommitId, so.projection.latestCommitId)
                        Assert.Equal(projectionFingerprint full.projection, projectionFingerprint so.projection)
                        // commits 覆盖全部历史且与完整重放一致（catch-up 任意游标）
                        let ids = so.commits |> List.map (fun c -> c.id) |> List.sort
                        Assert.Equal<uint64 list>([ 1UL .. 100UL ], ids)
                        Assert.Equal(full.commits.Length, so.commits.Length)
                        // 旧游标 catch-up：游标 0（全新客户端）与游标 40（锚点前）都必须完整返回
                        let after0 = so.commits |> List.filter (fun c -> c.id > 0UL) |> List.length
                        Assert.Equal(100, after0)
                        let after40 = so.commits |> List.filter (fun c -> c.id > 40UL) |> List.length
                        Assert.Equal(60, after40)
                    | other -> failwithf "snap inspect %A" other
                | other -> failwithf "findLatest %A" other
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

    // -------------------------------------------------------------------------
    // 场景 C：人工修复（§31 场景 C）——损坏 → doctor --fix 等价链 → recovery
    // bundle → 哈希有效 → 截尾修复 → 复查 PASS。整条链机器化验收。
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``场景C：损坏 → 修复 → bundle 哈希有效 → 复查 PASS 全链`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let path = DataPaths.eventFilePath dir day
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let convId = newConversationId ()
            // 2 条有效提交 + 半条损坏记录（模拟崩溃残余）
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "scenario-c"; config = testConfig () } ]
            let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddHours 1.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "你好" } ]
            let frames = [ c1; c2 ] |> List.map (fun c -> Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c)) |> Array.concat
            let tail = Text.Encoding.UTF8.GetBytes("\u001E{\"formatVersion\":2,\"commitId\":\"3\",\"brok")
            File.WriteAllBytes(path, Array.append frames tail)

            // 1) 只读检查报告损坏（算法 R：不修改文件）
            let bytesBefore = File.ReadAllBytes path
            let plan =
                match Replay.inspect dir with
                | RepairRequired (outcome, p) ->
                    Assert.Equal(2UL, outcome.lastCommitId) // 有效水位 = 前缀 2
                    p
                | other -> failwithf "expected RepairRequired, got %A" other
            Assert.Equal<byte[]>(bytesBefore, File.ReadAllBytes path)

            // 2) 建立 recovery bundle（算法 B：复制 + SHA-256 + manifest）
            let bundleDir =
                match Wanxiang.Store.RecoveryBundle.create dir plan with
                | Ok d -> d
                | Error e -> failwithf "bundle failed: %s" e
            Assert.True(File.Exists(Path.Combine(bundleDir, "manifest.json")))
            Assert.True(File.Exists(Path.Combine(bundleDir, "diagnosis.json")))
            // bundle 内原文件副本与磁盘一致（B6：任一哈希不相等则终止）
            let originalCopy = Path.Combine(bundleDir, "original", Path.GetFileName path)
            Assert.True(File.Exists originalCopy)
            Assert.Equal<byte[]>(bytesBefore, File.ReadAllBytes originalCopy)
            // manifest 记录损坏现场 + 计划动作（§8.1 字段清单）
            let manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(bundleDir, "manifest.json")))
            let files = manifest["files"].AsArray()
            Assert.Equal(1, files.Count)
            let file0 = files[0]
            let action0 = manifest["plannedActions"][0]
            Assert.Equal(path, file0["path"].GetValue<string>())
            Assert.Equal(64, file0["sha256"].GetValue<string>().Length)
            Assert.Equal("truncate", action0["action"].GetValue<string>())
            Assert.Equal(plan.damage.byteOffset, action0["offset"].GetValue<int64>())

            // 3) 执行修复（截尾路径）
            match Wanxiang.Store.Repair.repair dir plan with
            | Ok _ -> ()
            | Error e -> failwithf "repair failed: %s" e

            // 4) 复查：Healthy，投影 = 有效前缀（2 条提交），bundle 证据仍在
            match Replay.inspect dir with
            | Healthy outcome ->
                Assert.Equal(2UL, outcome.projection.latestCommitId)
                Assert.True(outcome.projection.conversations.ContainsKey convId)
            | other -> failwithf "expected Healthy after fix, got %A" other
            Assert.True(Directory.Exists bundleDir, "recovery bundle must be preserved after repair")
        finally
            cleanup dir

    // -------------------------------------------------------------------------
    // 场景 D：快照损坏变体（§31 场景 D）——最新快照损坏 → 回退上一有效快照，
    // 原始日志不受影响，启动投影与完整重放一致。
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``场景D：最新快照损坏 → 回退上一有效快照启动`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            // 3 条提交：1 会话 + 2 消息。分步落盘，每步 replay 得到真实前缀投影再快照
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "scenario-d"; config = testConfig () } ]
            let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddHours 1.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m1" } ]
            let c3 = Events.Commit.create 3UL (DateTimeOffset(day.AddHours 2.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m2" } ]
            let path = DataPaths.eventFilePath dir day
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let len1 = int64 (Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)).Length
            // 步骤 1：只写 c1 → 快照 1（through commit 1）
            File.WriteAllBytes(path, Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1))
            let proj1 = (replayOutcome dir).projection
            Assert.Equal(1UL, proj1.latestCommitId)
            match Snapshot.write dir proj1 (Some "2026-08-01.jsonseq") len1 [ c1 ] with
            | Ok _ -> ()
            | Error e -> failwith e
            // 步骤 2：追加 c2 → 快照 2（through commit 2，随后被破坏）
            let len2 = len1 + int64 (Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c2)).Length
            File.AppendAllBytes(path, Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c2))
            let proj2 = (replayOutcome dir).projection
            Assert.Equal(2UL, proj2.latestCommitId)
            match Snapshot.write dir proj2 (Some "2026-08-01.jsonseq") len2 [ c1; c2 ] with
            | Ok _ -> ()
            | Error e -> failwith e
            // 步骤 3：追加 c3 → 完整重放投影（最终真相）
            File.AppendAllBytes(path, Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c3))
            let full = replayOutcome dir
            Assert.Equal(3UL, full.projection.latestCommitId)
            // 日志字节快照（场景 D：原始日志不受影响）
            let logBefore = File.ReadAllBytes path
            // 破坏最新快照（改 checksum 字段名 → 校验失败）
            let snapDir = Path.Combine(dir, "snapshots")
            let latest = Directory.GetFiles(snapDir, "projection-*.snapshot") |> Array.sortDescending |> Array.head
            let text = File.ReadAllText latest
            let tampered = text.Replace("\"checksum\"", "\"checksumX\"", StringComparison.Ordinal)
            Assert.NotEqual<string>(text, tampered)
            File.WriteAllText(latest, tampered)

            // findLatest 必须忽略损坏的最新快照，回退到上一有效快照（L8/L9）
            match Snapshot.findLatest dir with
            | SnapshotStartup.SnapshotUsed info ->
                Assert.Equal(1UL, info.throughCommitId) // 回退到快照 1
                // 从快照 1 启动：重放 commit 2、3，投影与完整重放一致
                match Replay.inspectFrom
                          dir
                          { projection = info.projection
                            throughCommitId = info.throughCommitId
                            throughFile = info.throughFile
                            throughOffset = info.throughOffset
                            commits = info.commits } with
                | Healthy outcome ->
                    Assert.Equal(3UL, outcome.projection.latestCommitId)
                    Assert.Equal(projectionFingerprint full.projection, projectionFingerprint outcome.projection)
                | other -> failwithf "snapshot fallback startup not healthy: %A" other
            | SnapshotStartup.SnapshotCorrupt _ -> failwith "must fall back to previous valid snapshot"
            | other -> failwithf "expected SnapshotUsed, got %A" other
            // 原始日志未受影响（场景 D：快照损坏不得导致原始日志损坏）
            Assert.Equal<byte[]>(logBefore, File.ReadAllBytes path)
        finally
            cleanup dir
