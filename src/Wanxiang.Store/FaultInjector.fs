namespace Wanxiang.Store

/// 存储故障注入（T-002 / 算法 X：Crash Matrix）。
///
/// 生产默认 `NoFault`（零开销）；测试可注入 `FailOnce(point)`：
/// 在指定故障点**恰好失败一次**（抛 IOException），之后恢复无故障行为。
///
/// 故障点语义（对应算法 C：Commit 状态机）：
/// - BeforeOpen：打开日志文件前失败（写入路径完全未开始）。
/// - BeforeAppend：写记录前失败（未分配/未落盘）。
/// - DuringAppend：写记录中途失败（可能残留半记录——由写入器回滚偏移兜底）。
/// - AfterAppendBeforeFlush：append 完成但 flush 前失败（记录在页缓存，未保证耐久）。
/// - AfterFlushBeforeProjection：flush 成功但投影应用前失败（记录已落盘）。
/// - AfterProjectionBeforePublish：投影应用成功但对外发布前失败。
/// - DuringTruncate：运行时截尾失败（决策 40 的 poison 路径）。
/// - DuringRename：快照/配置原子 rename 失败（S-003 / 决策 42）。
module FaultInjector =

    type StorageFaultPoint =
        | BeforeOpen
        | BeforeAppend
        | DuringAppend
        | AfterAppendBeforeFlush
        | AfterFlushBeforeProjection
        | AfterProjectionBeforePublish
        | DuringTruncate
        | DuringRename

    type FaultMode =
        /// 无故障（生产默认）
        | NoFault
        /// 在指定点恰好失败一次
        | FailOnce of StorageFaultPoint

    let mutable private mode: FaultMode = NoFault
    let private modeLock = obj()

    /// 设置注入模式（测试用）。生产路径不得调用。
    let setMode (m: FaultMode) : unit =
        lock modeLock (fun () -> mode <- m)

    /// 当前模式（诊断/测试断言用）。
    let currentMode () : FaultMode =
        lock modeLock (fun () -> mode)

    /// 在指定故障点检查并消费一次性故障。
    /// 返回 true 表示应注入失败（抛异常由调用方执行）；false 表示无故障。
    let tryFail (point: StorageFaultPoint) : bool =
        lock modeLock (fun () ->
            match mode with
            | NoFault -> false
            | FailOnce p when p = point ->
                mode <- NoFault // 消费：恰好失败一次后恢复
                true
            | FailOnce _ -> false)

    /// 测试辅助：在故障点执行动作，若注入失败则捕获并返回。
    let runWithFault (point: StorageFaultPoint) (action: unit -> 'T) (onFault: unit -> 'T) : 'T =
        if tryFail point then onFault () else action ()
