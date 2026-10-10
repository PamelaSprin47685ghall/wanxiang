module Wanxiang.Tests.SnapshotTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// S-003：快照（算法 S / 算法 L）。
/// - 快照 = 冻结投影副本 + checksum；不删除任何事件日志。
/// - 启动优先快照（L7 只重放 throughCommitId 之后）；损坏/超前回退完整重放（L8/L9）。
module Snapshot =

    let private writeCommits (dir: string) (day: DateTime) (commits: Events.Commit list) =
        let path = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let bytes = commits |> List.map (fun c -> Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c)) |> Array.concat
        File.WriteAllBytes(path, bytes)
        ()

    [<Fact>]
    let ``快照写读回环：投影完整恢复`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "快照"; config = testConfig () } ]
            let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddHours 1.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "你好" } ]
            writeCommits dir day [ c1; c2 ]
            // 完整重放得到投影
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            // 写快照（锚点 = 当前文件 + 末尾偏移）
            match Snapshot.write dir outcome.projection (Some "2026-08-01.jsonseq") 0L outcome.commits with
            | Error e -> failwith e
            | Ok path ->
                Assert.True(File.Exists path)
                // R5：写快照不得残留 `.tmp-*` 临时文件（失败路径清理 + 成功路径 Move 走）
                let tmpLeftovers =
                    Directory.GetFiles(Path.Combine(dir, "snapshots"), ".tmp-*")
                Assert.Empty(tmpLeftovers)
                // 读回
                match Snapshot.tryLoad path with
                | Error e -> failwith e
                | Ok info ->
                    Assert.Equal(2UL, info.throughCommitId)
                    Assert.Equal(1, info.projection.conversations.Count)
                    let conv = Projection.tryConversation info.projection convId
                    Assert.True conv.IsSome
                    Assert.Equal(1, conv.Value.messages.Length)
                    // 快照不删除任何事件日志
                    Assert.True(File.Exists(DataPaths.eventFilePath dir day))
        finally
            cleanup dir

    [<Fact>]
    let ``快照校验和被篡改时拒绝加载`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            DataPaths.ensureDataDirs dir |> ignore
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            writeCommits dir day [ c1 ]
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            match Snapshot.write dir outcome.projection None 0L outcome.commits with
            | Error e -> failwith e
            | Ok path ->
                // 篡改投影内容
                let text = File.ReadAllText path
                let tampered = text.Replace("\"title\":\"A\"", "\"title\":\"B\"")
                Assert.NotEqual<string>(text, tampered)
                File.WriteAllText(path, tampered)
                match Snapshot.tryLoad path with
                | Error e -> Assert.Contains("checksum", e)
                | Ok _ -> failwith "tampered snapshot must be rejected"
        finally
            cleanup dir

    [<Fact>]
    let ``findLatest 返回最新有效快照，损坏的旧快照被忽略`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            DataPaths.ensureDataDirs dir |> ignore
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddHours 1.0)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
            writeCommits dir day [ c1; c2 ]
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            // 先写一个 throughCommitId=1 的快照，再篡改它
            let proj1 = { outcome.projection with latestCommitId = 1UL }
            // 构造 throughCommitId=1 的投影（去掉第二个会话）
            let proj1 =
                let c1conv = Projection.conversationList outcome.projection |> List.find (fun c -> c.conversationId = convId)
                { conversations = Map.ofList [ convId, c1conv ]
                  idempotency = Map.empty
                  latestCommitId = 1UL }
            match Snapshot.write dir proj1 (Some "2026-08-01.jsonseq") 0L [ c1 ] with
            | Error e -> failwith e
            | Ok p1 ->
                // 篡改 p1（校验失败）
                let t = File.ReadAllText p1
                File.WriteAllText(p1, t.Replace("\"title\":\"A\"", "\"title\":\"X\""))
                // 再写 throughCommitId=2 的快照（有效）
                match Snapshot.write dir outcome.projection (Some "2026-08-01.jsonseq") 0L outcome.commits with
                | Error e -> failwith e
                | Ok _ ->
                    match Snapshot.findLatest dir with
                    | SnapshotUsed info -> Assert.Equal(2UL, info.throughCommitId)
                    | other -> failwithf "expected SnapshotUsed, got %A" other
        finally
            cleanup dir

    [<Fact>]
    let ``全部快照损坏时返回 SnapshotCorrupt`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            DataPaths.ensureDataDirs dir |> ignore
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            writeCommits dir day [ c1 ]
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            match Snapshot.write dir outcome.projection (Some "2026-08-01.jsonseq") 0L outcome.commits with
            | Error e -> failwith e
            | Ok path ->
                File.WriteAllText(path, "garbage")
                match Snapshot.findLatest dir with
                | SnapshotCorrupt reasons -> Assert.NotEmpty reasons
                | other -> failwithf "expected SnapshotCorrupt, got %A" other
        finally
            cleanup dir

    [<Fact>]
    let ``快照超前于日志（锚点文件被删）时回退完整重放`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            DataPaths.ensureDataDirs dir |> ignore
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddHours 1.0)) [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
            writeCommits dir day [ c1; c2 ]
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            // 写 throughCommitId=2 快照，锚点指向 2026-08-01.jsonseq
            match Snapshot.write dir outcome.projection (Some "2026-08-01.jsonseq") 0L outcome.commits with
            | Error e -> failwith e
            | Ok _ ->
                // 删除锚点文件（模拟日志被外部清理）
                File.Delete(DataPaths.eventFilePath dir day)
                match Snapshot.findLatest dir with
                | SnapshotStale reason -> Assert.Contains("missing", reason)
                | other -> failwithf "expected SnapshotStale, got %A" other
        finally
            cleanup dir

    [<Fact>]
    let ``快照启动：只重放 throughCommitId 之后的日志，投影与完整重放一致`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            DataPaths.ensureDataDirs dir |> ignore
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "快照启动"; config = testConfig () } ]
            let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddHours 1.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "你好" } ]
            writeCommits dir day [ c1; c2 ]
            // 完整重放（基准）
            let fullOutcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            Assert.Equal(2UL, fullOutcome.lastCommitId)
            // 模拟：c1 之后创建快照（throughCommitId=1）
            let proj1 =
                // 快照时刻 = c1 之后（会话已创建，尚无消息）
                let conv =
                    { conversationId = convId
                      title = "快照启动"
                      createdAtUtc = DateTimeOffset(day)
                      lastActivityAtUtc = DateTimeOffset(day)
                      deleted = false
                      pinned = false
                      archived = false
                      parent = None
                      forkBaseCommitId = None
                      config = testConfig ()
                      messages = []
                      lastCommitId = Some 1UL }
                { conversations = Map.ofList [ convId, conv ]
                  idempotency = Map.empty
                  latestCommitId = 1UL }
            // throughOffset = c1 帧末尾 = c2 起点（c1 已折叠进快照，c2 待续播）
            let c1FrameLen = int64 (Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)).Length
            match Snapshot.write dir proj1 (Some "2026-08-01.jsonseq") c1FrameLen [ c1 ] with
            | Error e -> failwith e
            | Ok _ ->
                // 快照启动：从 throughCommitId+1=2 续播
                let snap =
                    match Snapshot.findLatest dir with
                    | SnapshotUsed info -> info
                    | other -> failwithf "expected SnapshotUsed, got %A" other
                match Replay.inspectFrom
                        dir
                        { projection = snap.projection
                          throughCommitId = snap.throughCommitId
                          throughFile = snap.throughFile
                          throughOffset = snap.throughOffset
                          commits = snap.commits } with
                | Healthy outcome ->
                    Assert.Equal(2UL, outcome.lastCommitId)
                    // 投影与完整重放一致（场景 A）
                    let fullConv = Projection.conversationList fullOutcome.projection |> List.head
                    let snapConv = Projection.conversationList outcome.projection |> List.head
                    Assert.Equal(fullConv.messages.Length, snapConv.messages.Length)
                    Assert.Equal(1, snapConv.messages.Length)
                | _ -> failwith "snapshot-based replay must be healthy"
        finally
            cleanup dir

    [<Fact>]
    let ``快照启动续播时 id 空洞仍被检测`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            DataPaths.ensureDataDirs dir |> ignore
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            writeCommits dir day [ c1 ]
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            let proj1 =
                let conv =
                    match Projection.tryConversation outcome.projection convId with
                    | Some c -> c
                    | None -> failwith "missing"
                { conversations = Map.ofList [ convId, conv ]
                  idempotency = Map.empty
                  latestCommitId = 1UL }
            // throughOffset = c1 帧末尾（c1 已折叠进快照；后续追加内容待续播）
            let c1FrameLen = int64 (Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)).Length
            match Snapshot.write dir proj1 (Some "2026-08-01.jsonseq") c1FrameLen [ c1 ] with
            | Error e -> failwith e
            | Ok _ ->
                // 续播文件：id 3（缺 2）
                let c3 = Events.Commit.create 3UL (DateTimeOffset(day.AddHours 2.0)) [ ConversationCreated { conversationId = newConversationId (); title = "C"; config = testConfig () } ]
                File.AppendAllText(DataPaths.eventFilePath dir day, Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c3) |> Array.map byte |> fun b -> System.Text.Encoding.UTF8.GetString b)
                let snap =
                    match Snapshot.findLatest dir with
                    | SnapshotUsed info -> info
                    | other -> failwithf "expected SnapshotUsed, got %A" other
                match Replay.inspectFrom
                        dir
                        { projection = snap.projection
                          throughCommitId = snap.throughCommitId
                          throughFile = snap.throughFile
                          throughOffset = snap.throughOffset
                          commits = snap.commits } with
                | RepairRequired (outcome, _) -> Assert.Equal(1UL, outcome.lastCommitId)
                | _ -> failwith "id gap after snapshot must be detected"
        finally
            cleanup dir

    [<Fact>]
    let ``v1 旧快照兼容：无 commits 字段 → 读取成功且回退外壳收集`` () =
        // §14.2 格式升级：v2 快照新增 commits 字段，v1 旧快照必须可读（兼容）。
        // v1 快照无 commits → SnapshotInfo.commits = None → inspectFrom 回退
        // collect-only 外壳收集（扫描锚点前日志补全 commits，catch-up 任意游标可用）。
        // 本测试**手工构造 v1 JSON**（不依赖当前 write 生成），模拟历史数据。
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = Guid.NewGuid()
            let path = DataPaths.eventFilePath dir day
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            // 写 3 条提交（1 会话 + 2 消息）
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "v1"; config = SessionConfig.empty } ]
            let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddMinutes 1.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m1" } ]
            let c3 = Events.Commit.create 3UL (DateTimeOffset(day.AddMinutes 2.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m2" } ]
            File.WriteAllBytes(path, [ c1; c2; c3 ] |> List.map (fun c -> Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c)) |> Array.concat)
            // 完整重放（对照）
            let full = match Replay.inspect dir with Healthy o -> o | other -> failwithf "inspect %A" other
            // 手工构造 throughCommitId=2 的 v1 快照（无 commits 字段）：
            // 投影 = 真实前缀（c1+c2 折叠）
            let proj2 =
                let mutable p = Projection.empty
                for c in [ c1; c2 ] do
                    p <- match Projection.applyCommit p c with Ok p -> p | Error e -> failwithf "apply %A" e
                p
            let snapDir = Path.Combine(dir, "snapshots")
            Directory.CreateDirectory snapDir |> ignore
            let o = System.Text.Json.Nodes.JsonObject()
            o["snapshotFormatVersion"] <- 1
            o["throughCommitId"] <- "2"
            o["createdAt"] <- DateTimeOffset.UtcNow.UtcDateTime.ToString("o")
            o["throughFile"] <- Path.GetFileName path
            o["throughOffset"] <- int64 (Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)).Length
                                 + int64 (Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c2)).Length
            // 投影序列化：用公开 API 不可得（projectionToJson 私有）——用 v2 write 生成后改造
            // 更简单：先 write 一个 v2 快照（throughCommitId=2），再手工降级为 v1
            let snapPath =
                match Snapshot.write dir proj2 (Some(Path.GetFileName path)) (int64 (Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)).Length + int64 (Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c2)).Length) [ c1; c2 ] with
                | Ok p -> p
                | Error e -> failwithf "write v2 %s" e
            // 读 v2 JSON，删 commits 字段、改 formatVersion=1、重算 checksum
            let v2 = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText snapPath).AsObject()
            v2.Remove("commits") |> ignore
            v2["snapshotFormatVersion"] <- 1
            // 重算 checksum（envelopeChecksum 算法：SHA-256(JCS(去掉 checksum 的副本))）
            let copy = System.Text.Json.Nodes.JsonObject()
            for kv in v2 do
                if kv.Key <> "checksum" then copy[kv.Key] <- kv.Value.DeepClone()
            v2["checksum"] <- Wanxiang.Core.Ledger.Jcs.sha256Hex (copy.ToJsonString(System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.General)))
            File.WriteAllText(snapPath, v2.ToJsonString(System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.General)))
            // v1 快照读取：成功且 commits = None
            match Snapshot.findLatest dir with
            | SnapshotStartup.SnapshotUsed info ->
                Assert.Equal(2UL, info.throughCommitId)
                Assert.True(info.commits.IsNone, "v1 快照 commits 应为 None")
                // inspectFrom：commits=None → collect-only 回退收集外壳
                match Replay.inspectFrom
                          dir
                          { projection = info.projection
                            throughCommitId = info.throughCommitId
                            throughFile = info.throughFile
                            throughOffset = info.throughOffset
                            commits = info.commits } with
                | Healthy so ->
                    // 投影一致
                    Assert.Equal(full.projection.latestCommitId, so.projection.latestCommitId)
                    // commits 覆盖全历史（collect-only 回退补全锚点前外壳）
                    let ids = so.commits |> List.map (fun c -> c.id) |> List.sort
                    Assert.Equal<uint64 list>([ 1UL; 2UL; 3UL ], ids)
                | other -> failwithf "v1 snapshot startup %A" other
            | other -> failwithf "findLatest %A" other
        finally
            cleanup dir

    [<Fact>]
    let ``v2 快照 commits 字段解码失败时拒绝加载（不静默回退）`` () =
        // 场景 F 精神：v2 快照声明有 commits 字段但内容非法（如被截断/篡改）→
        // tryLoad 必须显式拒绝（Error "snapshot commits decode failed"），
        // 不能把 None 当 v1 静默回退（那会掩盖损坏并让 catch-up 缺历史）。
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = Guid.NewGuid()
            let path = DataPaths.eventFilePath dir day
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "bad"; config = SessionConfig.empty } ]
            let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddMinutes 1.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m" } ]
            File.WriteAllBytes(path, [ c1; c2 ] |> List.map (fun c -> Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c)) |> Array.concat)
            let proj2 =
                let mutable p = Projection.empty
                for c in [ c1; c2 ] do
                    p <- match Projection.applyCommit p c with Ok p -> p | Error e -> failwithf "apply %A" e
                p
            let offset2 =
                int64 (Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)).Length
                + int64 (Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c2)).Length
            let snapPath =
                match Snapshot.write dir proj2 (Some(Path.GetFileName path)) offset2 [ c1; c2 ] with
                | Ok p -> p
                | Error e -> failwithf "write %s" e
            // 篡改：commits[0] 换成非法对象（缺 id/type 字段），重算 checksum
            let v2 = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText snapPath).AsObject()
            let commitsArr = v2["commits"].AsArray()
            commitsArr[0] <- System.Text.Json.Nodes.JsonObject.Parse("""{"id": "not-a-commit"}""")
            let copy = System.Text.Json.Nodes.JsonObject()
            for kv in v2 do
                if kv.Key <> "checksum" then copy[kv.Key] <- kv.Value.DeepClone()
            v2["checksum"] <- Wanxiang.Core.Ledger.Jcs.sha256Hex (copy.ToJsonString(System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.General)))
            File.WriteAllText(snapPath, v2.ToJsonString(System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.General)))
            // tryLoad 必须拒绝（显式错误，非静默 None）
            match Wanxiang.Store.Snapshot.tryLoad snapPath with
            | Error msg -> Assert.Contains("commits decode failed", msg)
            | Ok _ -> failwith "corrupt v2 commits must be rejected"
            // findLatest 层面：该快照损坏 → 无有效快照 → SnapshotCorrupt（不误用坏快照）
            match Snapshot.findLatest dir with
            | SnapshotStartup.SnapshotCorrupt _ -> ()
            | other -> failwithf "expected SnapshotCorrupt, got %A" other
        finally
            cleanup dir
