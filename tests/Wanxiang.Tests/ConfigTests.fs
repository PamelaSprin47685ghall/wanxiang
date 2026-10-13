module Wanxiang.Tests.ConfigTests

open System
open System.IO
open Xunit
open Wanxiang.Config

[<Fact>]
let ``Auth generateToken 生成符合 base64url 且带前缀的高熵令牌`` () =
    let token1 = Auth.generateToken ()
    let token2 = Auth.generateToken ()
    Assert.True(token1.StartsWith(Auth.tokenPrefix))
    Assert.True(token2.StartsWith(Auth.tokenPrefix))
    Assert.NotEqual<string>(token1, token2)
    // 验证不包含 base64 填充与标准 + /
    let raw = token1.Substring(Auth.tokenPrefix.Length)
    Assert.False(raw.Contains "=")
    Assert.False(raw.Contains "+")
    Assert.False(raw.Contains "/")
    Assert.True(raw.Length >= 40)

[<Fact>]
let ``Auth hashToken 产出确定性 64 位小写十六进制 SHA256`` () =
    let token = "wanxiang_client_test_token_12345"
    let h1 = Auth.hashToken token
    let h2 = Auth.hashToken token
    Assert.Equal(h1, h2)
    Assert.Equal(64, h1.Length)
    Assert.Equal(h1.ToLowerInvariant(), h1)
    Assert.True(h1 |> Seq.forall (fun ch -> Uri.IsHexDigit ch))

[<Fact>]
let ``Auth constantTimeEquals 正确比对相同与不同字符串`` () =
    Assert.True(Auth.constantTimeEquals "" "")
    Assert.True(Auth.constantTimeEquals "abc" "abc")
    Assert.True(Auth.constantTimeEquals "123456" "123456")
    Assert.False(Auth.constantTimeEquals "abc" "abd")
    Assert.False(Auth.constantTimeEquals "abc" "ab")
    Assert.False(Auth.constantTimeEquals "ab" "abc")
    Assert.False(Auth.constantTimeEquals "" "a")

[<Fact>]
let ``Auth generatePairingCode 产出 6 位十进制数字`` () =
    let code = Auth.generatePairingCode ()
    Assert.Equal(6, code.Length)
    Assert.True(code |> Seq.forall Char.IsDigit)

[<Fact>]
let ``Auth PairingState 完整生命周期：未启动、启动、防重放、过期与错误校验`` () =
    let ps = Auth.PairingState()
    let now = DateTimeOffset.UtcNow
    let lifetime = TimeSpan.FromMinutes 5.0

    // 1. 未启动时尝试消费
    match ps.TryConsume(now, "123456") with
    | Error msg -> Assert.Equal("no pairing session active", msg)
    | Ok () -> failwith "未激活对话不应允许消费"

    Assert.True ps.ActiveSession.IsNone

    // 2. 启动配对
    let code1 = ps.Start(now, lifetime)
    Assert.Equal(6, code1.Length)
    Assert.True ps.ActiveSession.IsSome
    let s1 = ps.ActiveSession.Value
    Assert.Equal(code1, s1.code)
    Assert.False s1.used
    Assert.Equal(now + lifetime, s1.expiresAtUtc)

    // 3. 输错配对码：不消耗，可重试
    match ps.TryConsume(now, "000000") with
    | Error msg -> Assert.Equal("invalid pairing code", msg)
    | Ok () -> failwith "错误码不应通过"
    Assert.False ps.ActiveSession.Value.used

    // 4. 重启配对：旧码作废
    let code2 = ps.Start(now, lifetime)
    Assert.True ps.ActiveSession.IsSome
    Assert.Equal(code2, ps.ActiveSession.Value.code)
    // 尝试用旧码 code1
    match ps.TryConsume(now, code1) with
    | Error msg -> Assert.Equal("invalid pairing code", msg)
    | Ok () -> failwith "旧码已被顶替，不应通过"

    // 5. 成功消费新码
    match ps.TryConsume(now, code2) with
    | Ok () -> ()
    | Error e -> failwith $"正常消费失败: {e}"
    Assert.True ps.ActiveSession.Value.used

    // 6. 再次尝试消费已被单次使用的配对码
    match ps.TryConsume(now, code2) with
    | Error msg -> Assert.Equal("pairing code already used", msg)
    | Ok () -> failwith "已消费的配对码不应重复使用"

    // 7. 测试过期逻辑
    let code3 = ps.Start(now, lifetime)
    let expiredTime = now + lifetime + TimeSpan.FromSeconds 1.0
    match ps.TryConsume(expiredTime, code3) with
    | Error msg -> Assert.Equal("pairing code expired", msg)
    | Ok () -> failwith "过期配对码应被拒绝"
    // 过期后 session 被清空
    Assert.True ps.ActiveSession.IsNone

[<Fact>]
let ``Auth FailureTracker 失败追踪、窗口滑动、达到阈值冻结与手动清除`` () =
    let window = TimeSpan.FromMinutes 1.0
    let maxFailures = 3
    let freezeDuration = TimeSpan.FromMinutes 5.0
    let tracker = Auth.FailureTracker(window, maxFailures, freezeDuration)

    let ip1 = "192.168.1.100"
    let ip2 = "192.168.1.101"
    let t0 = DateTimeOffset.Parse("2026-01-01T12:00:00Z")

    // 初始状态均未冻结
    Assert.False(tracker.IsFrozen(t0, ip1))
    Assert.False(tracker.IsFrozen(t0, ip2))

    // ip1 第 1 次失败
    let f1 = tracker.RecordFailure(t0, ip1)
    Assert.False f1
    Assert.False(tracker.IsFrozen(t0, ip1))

    // ip1 第 2 次失败
    let f2 = tracker.RecordFailure(t0 + TimeSpan.FromSeconds 10.0, ip1)
    Assert.False f2
    Assert.False(tracker.IsFrozen(t0 + TimeSpan.FromSeconds 10.0, ip1))

    // ip1 第 3 次失败 -> 达到阈值，触发冻结
    let f3 = tracker.RecordFailure(t0 + TimeSpan.FromSeconds 20.0, ip1)
    Assert.True f3
    Assert.True(tracker.IsFrozen(t0 + TimeSpan.FromSeconds 20.0, ip1))

    // 此时 ip2 不受影响
    Assert.False(tracker.IsFrozen(t0 + TimeSpan.FromSeconds 20.0, ip2))

    // 在冻结期内（4 分钟后仍冻结）
    Assert.True(tracker.IsFrozen(t0 + TimeSpan.FromMinutes 4.0, ip1))

    // 超过冻结期（5 分钟零 1 秒后解除冻结）
    Assert.False(tracker.IsFrozen(t0 + TimeSpan.FromMinutes 5.0 + TimeSpan.FromSeconds 21.0, ip1))

    // 清除测试：再次冻结后手动 Clear
    let fAgain = tracker.RecordFailure(t0 + TimeSpan.FromMinutes 10.0, ip1)
    Assert.False fAgain
    let fAgain2 = tracker.RecordFailure(t0 + TimeSpan.FromMinutes 10.1, ip1)
    Assert.False fAgain2
    let fAgain3 = tracker.RecordFailure(t0 + TimeSpan.FromMinutes 10.2, ip1)
    Assert.True fAgain3
    Assert.True(tracker.IsFrozen(t0 + TimeSpan.FromMinutes 10.2, ip1))
    tracker.Clear ip1
    Assert.False(tracker.IsFrozen(t0 + TimeSpan.FromMinutes 10.2, ip1))

    // 滑动窗口测试：超过 window 的旧失败被丢弃，不累加
    let ip3 = "10.0.0.1"
    tracker.RecordFailure(t0, ip3) |> ignore
    tracker.RecordFailure(t0 + TimeSpan.FromSeconds 10.0, ip3) |> ignore
    // 下一次失败在 2 分钟后（超过 1 分钟 window），之前的两次已被裁剪，因此长度为 1，不触发冻结
    let fWindow = tracker.RecordFailure(t0 + TimeSpan.FromMinutes 2.0, ip3)
    Assert.False fWindow
    Assert.False(tracker.IsFrozen(t0 + TimeSpan.FromMinutes 2.0, ip3))

[<Fact>]
let ``Auth FailureTracker 超过阈值时清理过期条目（R5 有界内存）`` () =
    let window = TimeSpan.FromMinutes 1.0
    let tracker = Auth.FailureTracker(window, 3, TimeSpan.FromMinutes 5.0)
    let t0 = DateTimeOffset.Parse("2026-01-01T12:00:00Z")

    // 插入 1100 个不同地址（超过 1024 阈值），每个只失败一次（不触发冻结）
    for i in 1..1100 do
        let ip = sprintf "10.1.0.%d" i
        tracker.RecordFailure(t0, ip) |> ignore

    // 全部在窗口内：清理后仍应保留（未过期不删）
    Assert.False(tracker.IsFrozen(t0, "10.1.0.1"))

    // 时间推进到窗口外：再触发一次 RecordFailure 应触发清理，
    // 旧条目被移除——新地址的失败计数从零开始（不因残留条目受影响）
    let late = t0 + TimeSpan.FromMinutes 10.0
    // 新地址连续 3 次失败（窗口外时间）→ 触发冻结，说明旧条目未干扰
    let ipNew = "10.2.0.1"
    tracker.RecordFailure(late, ipNew) |> ignore
    tracker.RecordFailure(late + TimeSpan.FromSeconds 1.0, ipNew) |> ignore
    let frozen = tracker.RecordFailure(late + TimeSpan.FromSeconds 2.0, ipNew)
    Assert.True frozen
    Assert.True(tracker.IsFrozen(late + TimeSpan.FromSeconds 2.0, ipNew))

    // 清理已发生：旧地址的冻结状态不受影响（从未冻结）
    Assert.False(tracker.IsFrozen(late, "10.1.0.1"))

// ================================================================
// TomlCodec.fs 深度测试（配置回退、未知键告警、全字段验证与序列化）
// ================================================================

