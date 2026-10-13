namespace Wanxiang.Store

/// 存储故障注入（测试用）。只剩写日志前后这几处：
/// 新账本先折叠后写盘，「已落盘但投影失败」这一类故障点在结构上不存在了。
module FaultInjector =

    type StorageFaultPoint =
        | BeforeAppend
        /// 记录已写入，返回前失败：账本必须截回写前偏移。
        | AfterAppend

    type FaultMode =
        | NoFault
        | FailOnce of StorageFaultPoint

    let mutable private mode: FaultMode = NoFault
    let private modeLock = obj ()

    let setMode (m: FaultMode) : unit = lock modeLock (fun () -> mode <- m)

    let currentMode () : FaultMode = lock modeLock (fun () -> mode)

    /// 命中即消费：恰好失败一次后恢复。
    let tryFail (point: StorageFaultPoint) : bool =
        lock modeLock (fun () ->
            match mode with
            | FailOnce p when p = point ->
                mode <- NoFault
                true
            | _ -> false)
