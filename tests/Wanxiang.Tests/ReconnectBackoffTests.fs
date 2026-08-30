module Wanxiang.Tests.ReconnectBackoffTests

open Xunit
open Wanxiang.Client

[<Fact>]
let ``等待时长逐次翻倍`` () =
    Assert.Equal(2000, ReconnectBackoff.next 1000)
    Assert.Equal(4000, ReconnectBackoff.next 2000)
    Assert.Equal(8000, ReconnectBackoff.next 4000)

[<Fact>]
let ``等待时长封顶在三十秒`` () =
    // 再长用户会以为程序已经放弃重连
    Assert.Equal(30000, ReconnectBackoff.next 16000)
    Assert.Equal(30000, ReconnectBackoff.next 30000)
    Assert.Equal(30000, ReconnectBackoff.next 120000)

[<Fact>]
let ``连续失败时真的退避而不是原地打转`` () =
    // 这条是为一个真实缺陷写的回归：原实现把重置放在「每次连接尝试」上，
    // 而重连自己就走那条路径，于是服务器宕着时永远是每秒一次。
    let delays = ReconnectBackoff.schedule 6
    Assert.Equal<int list>([ 1000; 2000; 4000; 8000; 16000; 30000 ], delays)
    // 序列必须单调不减，否则就是「退避」名不副实
    for (earlier, later) in List.pairwise delays do
        Assert.True(later >= earlier, $"{later} 比前一次 {earlier} 还短")

[<Fact>]
let ``异常输入不会退出合法区间`` () =
    for weird in [ 0; -1; -100000 ] do
        let value = ReconnectBackoff.next weird
        Assert.InRange(value, ReconnectBackoff.baseDelayMs, ReconnectBackoff.maxDelayMs)
