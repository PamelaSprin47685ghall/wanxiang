namespace Wanxiang.Client

/// 断线重连的等待时长。
///
/// 这里刻意做成独立的纯逻辑，因为它曾经悄悄失效过：原实现把「下次等多久」
/// 重置在**每一次连接尝试**上，而重连本身就要走那条尝试路径，
/// 于是服务器宕着时每一轮都从 1 秒重新开始——写了指数退避却从未退避。
///
/// 重置只应发生在两处：连接真的成功，以及用户手动发起连接。
module ReconnectBackoff =

    /// 首次重连的等待时长
    [<Literal>]
    let baseDelayMs = 1000

    /// 上限。再长用户会以为程序不再尝试了。
    [<Literal>]
    let maxDelayMs = 30000

    /// 下一次的等待时长：翻倍并封顶。
    let next (current: int) : int =
        let grown = (max baseDelayMs current) * 2
        min grown maxDelayMs

    /// 从一次失败开始，连续失败若干次后各次的等待时长。
    let schedule (failures: int) : int list =
        List.init failures id
        |> List.scan (fun delay _ -> next delay) baseDelayMs
        |> List.truncate failures
