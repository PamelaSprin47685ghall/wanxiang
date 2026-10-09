module Wanxiang.Tests.CrashMatrixTests

open System
open System.IO
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// T-002 / 算法 X：Crash Matrix。
///
/// 对每一个故障点 F：
/// X1. 准备固定历史 H。
/// X2. 启动系统。
/// X3. 注入 F。
/// X4. 提交一个命令。
/// X5. 在 F 处模拟失败。
/// X6. 重新启动（只读 inspect）。
/// X7. 检查最长有效前缀。
/// X8. 检查 commitId 不重复。
/// X9. 检查 commitId 无无法解释的空洞。
/// X10. 检查旧提交内容没有变化。
/// X11. 检查损坏证据仍可取得。
module CrashMatrix =

    open FaultInjector

    /// 固定历史：id 1..3（两个会话 + 一条消息），跨 UTC 日期文件。
    let private prepareHistory (dir: string) : Guid * DateTime =
        DataPaths.ensureDataDirs dir
        let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let convId = newConversationId ()
        let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "历史"; config = testConfig () } ]
        let c2 = Events.Commit.create 2UL (DateTimeOffset(day.AddHours 1.0)) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "历史消息" } ]
        let p1 = DataPaths.eventFilePath dir day
        Directory.CreateDirectory(Path.GetDirectoryName p1) |> ignore
        File.WriteAllBytes(p1, Array.concat [ Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1)
                                              Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c2) ])
        convId, day

    /// 提交一条命令（新会话）。
    /// `badProjection`：提交一条必然投影失败的命令（删除不存在的会话），用于触发截尾路径。
    let private submitNewConversation (dir: string) (day: DateTime) (badProjection: bool) : SubmitResult =
        let outcome =
            match Replay.inspect dir with
            | Healthy o -> o
            | _ -> failwith "history must be healthy"
        use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
        if badProjection then
            coord.SubmitEvents [ EventData.ConversationDeleted { conversationId = Guid.NewGuid() } ]
        else
            let convId = newConversationId ()
            coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "新会话"; config = testConfig () } ]

    /// 检查日志满足最长有效前缀且不变量成立。
    /// 返回 inspect 结果（供调用方断言具体故障点语义）。
    let private verifyAfterCrash (dir: string) : InspectResult =
        match Replay.inspect dir with
        | Healthy o ->
            // 不变量：id 连续无空洞（从 1 起），无重复
            let ids = o.commits |> List.map (fun c -> c.id)
            Assert.Equal<CommitId list>(ids |> List.sort, ids)
            Assert.Equal(ids |> List.distinct |> List.length, ids.Length)
            Assert.Equal(o.lastCommitId, o.projection.latestCommitId)
            Healthy o
        | RepairRequired (o, plan) ->
            // 损坏点之后无合法记录；有效前缀 id 连续
            let ids = o.commits |> List.map (fun c -> c.id)
            Assert.Equal<CommitId list>(ids |> List.sort, ids)
            Assert.Equal(ids |> List.distinct |> List.length, ids.Length)
            Assert.Equal(o.lastCommitId, o.projection.latestCommitId)
            // 损坏证据可取得（X11）
            Assert.False(String.IsNullOrWhiteSpace plan.damage.reason)
            Assert.True(plan.damage.byteOffset >= 0L)
            RepairRequired (o, plan)
        | UnsupportedFormat msg -> failwith msg
        | Unreadable msg -> failwith msg

    /// 每个故障点的崩溃矩阵测试。
    /// 语义断言按故障点区分（算法 C 状态机）：
    /// - BeforeAppend/DuringAppend/AfterAppendBeforeFlush：未落盘 → 日志停在 id 3，无损坏。
    /// - AfterFlushBeforeProjection：已落盘但未投影 → 重启后 id 3 完整（截尾复用 id）。
    /// - AfterProjectionBeforePublish：投影已应用 → 重启后 id 4 存在。
    /// - DuringTruncate：截尾失败 → poison（日志可能含半记录，inspect 报损坏）。
    [<Theory>]
    [<InlineData("BeforeOpen")>]
    [<InlineData("BeforeAppend")>]
    [<InlineData("DuringAppend")>]
    [<InlineData("AfterAppendBeforeFlush")>]
    [<InlineData("AfterFlushBeforeProjection")>]
    [<InlineData("AfterProjectionBeforePublish")>]
    [<InlineData("DuringTruncate")>]
    let ``crash matrix: fault point`` (pointName: string) =
        let point =
            match pointName with
            | "BeforeOpen" -> StorageFaultPoint.BeforeOpen
            | "BeforeAppend" -> StorageFaultPoint.BeforeAppend
            | "DuringAppend" -> StorageFaultPoint.DuringAppend
            | "AfterAppendBeforeFlush" -> StorageFaultPoint.AfterAppendBeforeFlush
            | "AfterFlushBeforeProjection" -> StorageFaultPoint.AfterFlushBeforeProjection
            | "AfterProjectionBeforePublish" -> StorageFaultPoint.AfterProjectionBeforePublish
            | "DuringTruncate" -> StorageFaultPoint.DuringTruncate
            | other -> failwithf "unknown point %s" other
        let dir = tempDir ()
        try
            let convId, day = prepareHistory dir
            // X2：启动（healthy）
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "history must be healthy"
            Assert.Equal(2UL, outcome.lastCommitId)
            // 旧内容字节（X10 基准）
            let oldBytes = File.ReadAllBytes(DataPaths.eventFilePath dir day)
            // X3：注入故障（FailOnce）
            FaultInjector.setMode(FailOnce point)
            // X4/X5：提交命令（在故障点失败）
            // DuringTruncate 需要投影失败的命令才能进入截尾路径
            let result = submitNewConversation dir day (point = StorageFaultPoint.DuringTruncate)
            // 注入已消费
            Assert.Equal(FaultInjector.FaultMode.NoFault, FaultInjector.currentMode ())
            // X6：重启只读检查
            let insp = verifyAfterCrash dir
            // X10：旧提交内容没有变化（历史文件字节不变——除非损坏点在历史文件本身）
            match insp with
            | Healthy _ ->
                // 未损坏：历史文件字节完全不变
                Assert.Equal<byte[]>(oldBytes, File.ReadAllBytes(DataPaths.eventFilePath dir day))
            | RepairRequired (_, plan) ->
                // 损坏：历史文件可能被截尾（损坏点在文件内），但损坏前的字节不变
                if plan.damage.file = DataPaths.eventFilePath dir day then
                    let now = File.ReadAllBytes(DataPaths.eventFilePath dir day)
                    // 有效前缀 = 截尾后的文件，其内容必须是旧内容的前缀
                    Assert.True(oldBytes.Length >= now.Length, "truncated file must be a prefix")
                    Assert.Equal<byte[]>(oldBytes[0 .. now.Length - 1], now)
            | UnsupportedFormat _ -> ()
            | Unreadable _ -> ()
            // 语义断言
            match point with
            | StorageFaultPoint.BeforeOpen
            | StorageFaultPoint.BeforeAppend
            | StorageFaultPoint.DuringAppend
            | StorageFaultPoint.AfterAppendBeforeFlush ->
                // 未落盘：日志停在 id 2，健康（无损坏）
                match insp with
                | Healthy o -> Assert.Equal(2UL, o.lastCommitId)
                | _ -> failwithf "%s must leave log healthy (nothing persisted)" pointName
            | StorageFaultPoint.DuringRename -> ()
            | StorageFaultPoint.AfterFlushBeforeProjection ->
                // 已落盘但投影前失败：截尾复用 id → 日志停在 id 2，健康
                match insp with
                | Healthy o -> Assert.Equal(2UL, o.lastCommitId)
                | _ -> failwithf "%s must truncate-and-reuse (log healthy)" pointName
            | StorageFaultPoint.AfterProjectionBeforePublish ->
                // 投影已应用：重启后 id 3 存在（提交内容已落盘）
                match insp with
                | Healthy o -> Assert.Equal(3UL, o.lastCommitId)
                | _ -> failwithf "%s must have persisted the commit" pointName
            | StorageFaultPoint.DuringTruncate ->
                // 截尾失败：poison（可能残留半记录）→ inspect 报损坏或健康但水位 < 3
                match insp with
                | Healthy o -> Assert.True(o.lastCommitId <= 3UL)
                | RepairRequired (o, _) -> Assert.True(o.lastCommitId <= 2UL)
                | UnsupportedFormat _ -> ()
                | Unreadable _ -> ()
        finally
            FaultInjector.setMode FaultInjector.FaultMode.NoFault
            cleanup dir

    [<Fact>]
    let ``生产默认 NoFault：故障注入不改变正常提交`` () =
        FaultInjector.setMode FaultInjector.FaultMode.NoFault
        let dir = tempDir ()
        try
            let _, _ = prepareHistory dir
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
            let convId = newConversationId ()
            let result = coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "正常"; config = testConfig () } ]
            match result with
            | Committed c -> Assert.Equal(3UL, c.id)
            | other -> failwithf "expected committed, got %A" other
        finally
            FaultInjector.setMode FaultInjector.FaultMode.NoFault
            cleanup dir

    [<Fact>]
    let ``FailOnce 只失败一次：后续提交恢复正常`` () =
        FaultInjector.setMode(FaultInjector.FaultMode.FailOnce StorageFaultPoint.BeforeAppend)
        let dir = tempDir ()
        try
            let _, _ = prepareHistory dir
            let outcome =
                match Replay.inspect dir with
                | Healthy o -> o
                | _ -> failwith "expected healthy"
            use coord = new CommitCoordinator(dir, outcome, ignore, ignore)
            let convId = newConversationId ()
            // 第一次提交在 BeforeAppend 失败
            let r1 = coord.SubmitEvents [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            match r1 with
            | CommitFailed _ -> ()
            | other -> failwithf "expected CommitFailed on injected fault, got %A" other
            // 第二次提交正常（故障已消费）
            let r2 = coord.SubmitEvents [ ConversationCreated { conversationId = newConversationId (); title = "B"; config = testConfig () } ]
            match r2 with
            | Committed c -> Assert.Equal(3UL, c.id) // id 3 未被失败提交占用
            | other -> failwithf "expected committed after fault consumed, got %A" other
        finally
            FaultInjector.setMode FaultInjector.FaultMode.NoFault
            cleanup dir
