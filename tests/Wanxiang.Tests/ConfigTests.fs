module Wanxiang.Tests.ConfigTests

open System
open System.IO
open Xunit
open Wanxiang.Config

let private sampleToml =
    """
configVersion = 2
instanceId = "11111111-1111-1111-1111-111111111111"
[runtime]
server = true
client = false
pwa = true
fix = false
[network]
listen = "127.0.0.1:9999"
maxAttachmentBytes = 1048576
chunkSizeBytes = 65536
[generation]
temperature = 0.7
maxContextMessages = 120
autoTitle = true
maxToolRounds = 8
[tools]
callTimeoutSeconds = 20
[providers.openai]
kind = "openai"
label = "OpenAI"
baseUrl = "https://api.openai.com/v1"
apiKey = "sk-test"
models = ["gpt-4o-mini", "gpt-4o"]
defaultModel = "gpt-4o-mini"
timeoutSeconds = 90
maxRetries = 3
enabled = true
[mcp.fs]
command = "npx"
args = ["-y", "server"]
maxConcurrency = 2
callTimeoutSeconds = 45
[[auth.clients]]
tokenHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
name = "test"
createdAt = "2026-01-01T00:00:00Z"
revoked = false
"""

[<Fact>]
let ``TOML parses every section and survives a serialize roundtrip`` () =
    match TomlCodec.tryParse sampleToml with
    | Error errs -> failwith (String.concat "; " errs)
    | Ok cfg ->
        Assert.Equal("127.0.0.1:9999", cfg.listen)
        Assert.Equal(1048576L, cfg.maxAttachmentBytes)
        Assert.True cfg.runtime.server
        Assert.False cfg.runtime.client
        Assert.True cfg.runtime.pwa
        Assert.Equal(1, cfg.providers.Count)
        Assert.Equal("sk-test", cfg.providers["openai"].apiKey |> Option.defaultValue "")
        Assert.Equal<string list>([ "gpt-4o-mini"; "gpt-4o" ], cfg.providers["openai"].models)
        Assert.Equal("gpt-4o-mini", cfg.providers["openai"].defaultModel)
        Assert.Equal(90, cfg.providers["openai"].timeoutSeconds)
        Assert.Equal(3, cfg.providers["openai"].maxRetries)
        Assert.Equal("OpenAI", ProviderConfig.displayName cfg.providers["openai"])
        Assert.Equal(Some 0.7, cfg.generation.temperature)
        Assert.Equal(120, cfg.generation.maxContextMessages)
        Assert.Equal(8, cfg.generation.maxToolRounds)
        Assert.Equal(20, cfg.tools.callTimeoutSeconds)
        Assert.Equal(2, cfg.mcpServers["fs"].maxConcurrency |> Option.defaultValue 0)
        Assert.Equal(45, cfg.mcpServers["fs"].callTimeoutSeconds)
        Assert.Equal(1, cfg.authClients.Length)
        // roundtrip
        let text = TomlCodec.serialize cfg
        match TomlCodec.tryParse text with
        | Error errs -> failwith (String.concat "; " errs)
        | Ok cfg2 ->
            Assert.Equal(cfg.listen, cfg2.listen)
            Assert.Equal(cfg.providers.Count, cfg2.providers.Count)
            Assert.Equal<string list>(cfg.providers["openai"].models, cfg2.providers["openai"].models)
            Assert.Equal(cfg.generation, cfg2.generation)
            Assert.Equal(cfg.tools, cfg2.tools)
            Assert.Equal(cfg.mcpServers["fs"], cfg2.mcpServers["fs"])
            Assert.Equal(cfg.authClients.Length, cfg2.authClients.Length)
            Assert.Equal(cfg.instanceId, cfg2.instanceId)

[<Fact>]
let ``unknown TOML field rejects the whole config`` () =
    let bad = sampleToml + "\n[network]\ntypo = true\n"
    // 注意：重复 [network] 表——TOML 允许合并；未知字段检查应命中
    match TomlCodec.tryParse (sampleToml.Replace("[network]\nlisten", "[network]\ntypo = 1\nlisten")) with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "unknown field")
    | Ok _ -> failwith "unknown field should be rejected"

[<Fact>]
let ``malformed token hash rejects the whole config`` () =
    let bad = sampleToml.Replace("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "short")
    match TomlCodec.tryParse bad with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "tokenHash")
    | Ok _ -> failwith "invalid token hash should be rejected"

[<Fact>]
let ``MCP id colliding with a provider id is rejected`` () =
    let bad = sampleToml.Replace("[mcp.fs]", "[mcp.openai]")
    match TomlCodec.tryParse bad with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "conflicts")
    | Ok _ -> failwith "id conflict should be rejected"

[<Fact>]
let ``config store reload keeps last valid configuration on invalid file`` () =
    let dir = Path.Combine(Path.GetTempPath(), "wanxiang-config-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "wanxiang.toml")
    try
        let initial = AppConfig.defaults (Guid.NewGuid())
        File.WriteAllText(path, TomlCodec.serialize initial)
        let rejected = ResizeArray<string>()
        use store =
            match ConfigStore.Open(path, ignore, rejected.Add) with
            | Ok value -> value
            | Error e -> failwith e
        File.WriteAllText(path, "configVersion = 2\ninvalid = true\n")
        store.TriggerReload()
        System.Threading.Thread.Sleep 250
        Assert.Equal(initial.instanceId, store.Current.instanceId)
        Assert.NotEmpty rejected
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

[<Fact>]
let ``config store rewrite persists and reloads complete configuration`` () =
    let dir = Path.Combine(Path.GetTempPath(), "wanxiang-config-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "wanxiang.toml")
    try
        let initial = AppConfig.defaults (Guid.NewGuid())
        File.WriteAllText(path, TomlCodec.serialize initial)
        use store =
            match ConfigStore.Open(path, ignore, ignore) with
            | Ok value -> value
            | Error e -> failwith e
        let updated = { initial with listen = "127.0.0.1:9876" }
        match store.Rewrite updated with
        | Error e -> failwith e
        | Ok () ->
            let reloaded =
                match TomlCodec.tryParse (File.ReadAllText path) with
                | Ok value -> value
                | Error errors -> failwith (String.concat "; " errors)
            Assert.Equal("127.0.0.1:9876", reloaded.listen)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

    let cfg = AppConfig.defaults (Guid.NewGuid())
    let text = TomlCodec.serialize cfg
    match TomlCodec.tryParse text with
    | Error errs -> failwith (String.concat "; " errs)
    | Ok parsed ->
        Assert.Equal(cfg.instanceId, parsed.instanceId)
        Assert.True parsed.runtime.server
        Assert.True parsed.runtime.client
        Assert.True parsed.runtime.pwa
        Assert.False parsed.runtime.fix

[<Fact>]
let ``rewrite 在 reload 失败时返回 Error 并保留旧配置`` () =
    let dir = Path.Combine(Path.GetTempPath(), "wanxiang-config-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "wanxiang.toml")
    try
        let initial = AppConfig.defaults (Guid.CreateVersion7())
        File.WriteAllText(path, TomlCodec.serialize initial)
        use store =
            match ConfigStore.Open(path, ignore, ignore) with
            | Ok value -> value
            | Error e -> failwith e
        // 成功路径：Rewrite 后内存与磁盘一致（决策 44）
        let updated = { initial with listen = "127.0.0.1:12345" }
        match store.Rewrite updated with
        | Error e -> failwith e
        | Ok () -> Assert.Equal("127.0.0.1:12345", store.Current.listen)
        // 制造磁盘文件非法（reload 失败）：外部写入非法内容后 watcher 触发 reload，保留旧配置
        File.WriteAllText(path, "configVersion = 2\ninstanceId = 1\n") // instanceId 非法 → reload 失败
        store.TriggerReload()
        System.Threading.Thread.Sleep 250
        Assert.Equal("127.0.0.1:12345", store.Current.listen) // 保留最后有效配置
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

[<Fact>]
let ``首次创建的 instanceId 是 UUIDv7`` () =
    let dir = Path.Combine(Path.GetTempPath(), "wanxiang-config-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "wanxiang.toml")
    try
        use store =
            match ConfigStore.Open(path, ignore, ignore) with
            | Ok value -> value
            | Error e -> failwith e
        // UUIDv7 版本号 = 7（Guid.Version 属性在 .NET 8+ 可用）
        Assert.Equal(7, int (store.Current.instanceId.Version))
        // 再次打开仍保持同一 instanceId（稳定）
        let id = store.Current.instanceId
        store.Dispose()
        use store2 =
            match ConfigStore.Open(path, ignore, ignore) with
            | Ok value -> value
            | Error e -> failwith e
        Assert.Equal(id, store2.Current.instanceId)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

[<Fact>]
let ``TOML 配置文件权限为 0600`` () =
    if OperatingSystem.IsLinux() then
        let dir = Path.Combine(Path.GetTempPath(), "wanxiang-config-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        let path = Path.Combine(dir, "wanxiang.toml")
        try
            let initial = AppConfig.defaults (Guid.CreateVersion7())
            File.WriteAllText(path, TomlCodec.serialize initial)
            use store =
                match ConfigStore.Open(path, ignore, ignore) with
                | Ok value -> value
                | Error e -> failwith e
            let mode = File.GetUnixFileMode path
            Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite, mode &&& (UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite))
            // 写回后权限保持
            store.Rewrite { initial with listen = "127.0.0.1:54321" } |> ignore
            let mode2 = File.GetUnixFileMode path
            Assert.Equal(mode, mode2)
        finally
            if Directory.Exists dir then Directory.Delete(dir, true)

// ---------------------------------------------------------------- TLS

let private withNetwork (extra: string) =
    sampleToml.Replace("listen = \"127.0.0.1:9999\"", "listen = \"0.0.0.0:9999\"\n" + extra)

let private errorsOf (toml: string) =
    match TomlCodec.tryParse toml with
    | Ok _ -> []
    | Error errors -> errors

[<Fact>]
let ``只填证书或只填私钥都被拒绝`` () =
    // 半套配置会静默退回明文，而运维以为已经加密——必须在解析期就失败
    let certOnly = errorsOf (withNetwork "tlsCertPath = \"/tmp/nonexistent-cert.pem\"")
    Assert.Contains("network.tlsKeyPath: required when tlsCertPath is set", certOnly)
    let keyOnly = errorsOf (withNetwork "tlsKeyPath = \"/tmp/nonexistent-key.pem\"")
    Assert.Contains("network.tlsCertPath: required when tlsKeyPath is set", keyOnly)

[<Fact>]
let ``证书文件不存在时拒绝启动`` () =
    let errors =
        errorsOf (
            withNetwork "tlsCertPath = \"/tmp/does-not-exist.pem\"\ntlsKeyPath = \"/tmp/also-missing.pem\"")
    Assert.Contains("network.tlsCertPath: file not found: /tmp/does-not-exist.pem", errors)
    Assert.Contains("network.tlsKeyPath: file not found: /tmp/also-missing.pem", errors)

[<Fact>]
let ``证书路径齐备时被接受并能往返 TOML`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"wanxiang-tls-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore
    try
        let cert = Path.Combine(dir, "cert.pem")
        let key = Path.Combine(dir, "key.pem")
        File.WriteAllText(cert, "-----BEGIN CERTIFICATE-----")
        File.WriteAllText(key, "-----BEGIN PRIVATE KEY-----")
        match TomlCodec.tryParse (withNetwork $"tlsCertPath = \"{cert}\"\ntlsKeyPath = \"{key}\"") with
        | Error errors -> failwith $"应当接受，却报错：{errors}"
        | Ok cfg ->
            Assert.Equal(cert, cfg.tlsCertPath)
            Assert.Equal(key, cfg.tlsKeyPath)
            match TomlCodec.tryParse (TomlCodec.serialize cfg) with
            | Error errors -> failwith $"回写后无法重新解析：{errors}"
            | Ok again ->
                Assert.Equal(cfg.tlsCertPath, again.tlsCertPath)
                Assert.Equal(cfg.tlsKeyPath, again.tlsKeyPath)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``不配 TLS 时字段为空且配置合法`` () =
    match TomlCodec.tryParse sampleToml with
    | Error errors -> failwith $"{errors}"
    | Ok cfg ->
        Assert.Equal("", cfg.tlsCertPath)
        Assert.Equal("", cfg.tlsKeyPath)

// ---------------------------------------------------------------- 版本升级（决策 184）

let private sampleTomlV1 =
    """
configVersion = 1
instanceId = "22222222-2222-2222-2222-222222222222"
[runtime]
server = true
client = true
pwa = true
fix = false
[network]
listen = "127.0.0.1:8765"
maxAttachmentBytes = 67108864
chunkSizeBytes = 262144
[pairing]
failureWindowMinutes = 1
maxFailures = 5
freezeMinutes = 5
[providers.openai]
kind = "openai"
baseUrl = "https://api.openai.com/v1"
apiKey = "sk-v1-key"
model = "gpt-4o"
[mcp.fs]
command = "node"
args = ["fs-server.js"]
maxConcurrency = 4
[[auth.clients]]
tokenHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
name = "v1-client"
createdAt = "2026-01-01T00:00:00Z"
revoked = false
"""

[<Fact>]
let ``configVersion 1 配置能够成功解析并显式升级到当前模型`` () =
    match TomlCodec.tryParse sampleTomlV1 with
    | Error errs -> failwith (String.concat "; " errs)
    | Ok cfg ->
        // 升级后版本号为当前版本（2）
        Assert.Equal(AppConfig.CurrentVersion, cfg.configVersion)
        Assert.Equal(Guid.Parse "22222222-2222-2222-2222-222222222222", cfg.instanceId)
        Assert.Equal("127.0.0.1:8765", cfg.listen)
        Assert.Equal("", cfg.tlsCertPath)
        Assert.Equal("", cfg.tlsKeyPath)
        // v1 单 model 映射到 models 列表与 defaultModel
        Assert.True(cfg.providers.ContainsKey "openai")
        let p = cfg.providers["openai"]
        Assert.Equal<string list>([ "gpt-4o" ], p.models)
        Assert.Equal("gpt-4o", p.defaultModel)
        Assert.Equal(ProviderConfig.defaultTimeoutSeconds, p.timeoutSeconds)
        Assert.Equal(ProviderConfig.defaultMaxRetries, p.maxRetries)
        Assert.True p.enabled
        // v1 未配 generation 与 tools 时注入默认值
        Assert.Equal(GenerationDefaults.defaults, cfg.generation)
        Assert.Equal(ToolsConfig.defaults, cfg.tools)
        // v1 mcp 正确升级
        Assert.True(cfg.mcpServers.ContainsKey "fs")
        let m = cfg.mcpServers["fs"]
        Assert.Equal(Some "node", m.command)
        Assert.Equal(Some 4, m.maxConcurrency)
        Assert.Equal(McpServerConfig.defaultCallTimeoutSeconds, m.callTimeoutSeconds)
        Assert.True m.enabled
        // 写回统一输出当前版本
        let serialized = TomlCodec.serialize cfg
        Assert.Contains("configVersion = 2", serialized)
        match TomlCodec.tryParse serialized with
        | Error errs -> failwith (String.concat "; " errs)
        | Ok cfg2 ->
            Assert.Equal(cfg.instanceId, cfg2.instanceId)
            Assert.Equal(cfg.providers.Count, cfg2.providers.Count)

[<Fact>]
let ``不支持的 configVersion 返回明确错误`` () =
    let v0 = sampleToml.Replace("configVersion = 2", "configVersion = 0")
    match TomlCodec.tryParse v0 with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "configVersion 0 not supported")
    | Ok _ -> failwith "version 0 should be rejected"

    let v99 = sampleToml.Replace("configVersion = 2", "configVersion = 99")
    match TomlCodec.tryParse v99 with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "configVersion 99 not supported")
    | Ok _ -> failwith "version 99 should be rejected"

// ---------------------------------------------------------------- 默认值回归（D4/Q157，D7）

[<Fact>]
let ``provider 默认不自动重试计费调用`` () =
    Assert.Equal(0, ProviderConfig.defaultMaxRetries)

[<Fact>]
let ``context token 预算默认 128000`` () =
    Assert.Equal(128000, GenerationDefaults.defaults.maxContextTokens)

// ================================================================
// Auth.fs 深度测试（覆盖率补全）
// ================================================================

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
    | Ok () -> failwith "未激活会话不应允许消费"

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

// ================================================================
// TomlCodec.fs 深度测试（类型回退、未知键告警、全字段验证与序列化）
// ================================================================

[<Fact>]
let ``TOML 语法畸形时返回错误列表`` () =
    match TomlCodec.tryParse "[[[[[bad toml" with
    | Error errs -> Assert.NotEmpty errs
    | Ok _ -> failwith "语法畸形应当报错"

[<Fact>]
let ``顶层缺少或非法 instanceId 报错`` () =
    let noId = sampleToml.Replace("instanceId = \"11111111-1111-1111-1111-111111111111\"", "")
    match TomlCodec.tryParse noId with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "instanceId: required UUID")
    | Ok _ -> failwith "缺少 instanceId 应当报错"

    let badId = sampleToml.Replace("11111111-1111-1111-1111-111111111111", "not-a-valid-guid")
    match TomlCodec.tryParse badId with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "instanceId: required UUID")
    | Ok _ -> failwith "非法 instanceId 应当报错"

[<Fact>]
let ``runtime 节非表或未知字段报错`` () =
    let notTable = sampleToml.Replace("[runtime]\nserver = true", "runtime = \"bad\"")
    match TomlCodec.tryParse notTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "runtime: expected table")
    | Ok _ -> failwith "runtime 非表应报错"

    let unknownKey = sampleToml.Replace("[runtime]\nserver = true", "[runtime]\nunknown = true\nserver = true")
    match TomlCodec.tryParse unknownKey with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "runtime.unknown: unknown field")
    | Ok _ -> failwith "runtime 未知键应报错"

[<Fact>]
let ``network 节非表、缺少listen、非法尺寸与未知字段报错`` () =
    let notTable = "network = 123\n" + (sampleToml.Replace("[network]\nlisten", "#network\n#listen"))
    match TomlCodec.tryParse notTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "network: expected table")
    | Ok _ -> failwith "network 非表应报错"

    let emptyListen = sampleToml.Replace("listen = \"127.0.0.1:9999\"", "listen = \"   \"")
    match TomlCodec.tryParse emptyListen with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "network.listen: required")
    | Ok _ -> failwith "空 listen 应报错"

    let badMaxAtt = sampleToml.Replace("maxAttachmentBytes = 1048576", "maxAttachmentBytes = 0")
    match TomlCodec.tryParse badMaxAtt with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "network.maxAttachmentBytes: must be positive")
    | Ok _ -> failwith "maxAttachmentBytes <= 0 应报错"

    let badChunk = sampleToml.Replace("chunkSizeBytes = 65536", "chunkSizeBytes = -1")
    match TomlCodec.tryParse badChunk with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "network.chunkSizeBytes: must be positive")
    | Ok _ -> failwith "chunkSizeBytes <= 0 应报错"

    let unknownField = sampleToml.Replace("[network]\nlisten", "[network]\nextraNet = 1\nlisten")
    match TomlCodec.tryParse unknownField with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "network.extraNet: unknown field")
    | Ok _ -> failwith "network 未知字段应报错"

[<Fact>]
let ``pairing 节非表与未知字段报错`` () =
    let notTable = sampleToml.Replace("configVersion = 2\n", "configVersion = 2\npairing = false\n")
    match TomlCodec.tryParse notTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "pairing: expected table")
    | Ok _ -> failwith "pairing 非表应报错"
    let unknownField = sampleToml + "
[pairing]
unknown = 1
"
    match TomlCodec.tryParse unknownField with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "pairing.unknown: unknown field")
    | Ok _ -> failwith "pairing 未知键应报错"

[<Fact>]
let ``generation 节各参数边界校验与未知键报错`` () =
    let notTable = "generation = 1\n" + (sampleToml.Replace("[generation]\ntemperature", "#generation\n#temperature"))
    match TomlCodec.tryParse notTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation: expected table")
    | Ok _ -> failwith "generation 非表应报错"

    let unknownKey = sampleToml.Replace("[generation]\ntemperature", "[generation]\nfoo = 1\ntemperature")
    match TomlCodec.tryParse unknownKey with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation.foo: unknown field")
    | Ok _ -> failwith "generation 未知键应报错"

    // temperature < 0.0 或 > 2.0
    let tempLow = sampleToml.Replace("temperature = 0.7", "temperature = -0.1")
    match TomlCodec.tryParse tempLow with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation.temperature: must be within [0, 2]")
    | Ok _ -> failwith "temp < 0 应报错"

    let tempHigh = sampleToml.Replace("temperature = 0.7", "temperature = 2.1")
    match TomlCodec.tryParse tempHigh with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation.temperature: must be within [0, 2]")
    | Ok _ -> failwith "temp > 2 应报错"

    // topP <= 0.0 或 > 1.0
    let topPLow = sampleToml + "\n[generation]\ntopP = 0.0\n"
    match TomlCodec.tryParse (sampleToml.Replace("[generation]\n", "[generation]\ntopP = 0.0\n")) with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation.topP: must be within (0, 1]")
    | Ok _ -> failwith "topP <= 0 应报错"

    match TomlCodec.tryParse (sampleToml.Replace("[generation]\n", "[generation]\ntopP = 1.1\n")) with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation.topP: must be within (0, 1]")
    | Ok _ -> failwith "topP > 1 应报错"

    // maxTokens <= 0
    match TomlCodec.tryParse (sampleToml.Replace("[generation]\n", "[generation]\nmaxTokens = 0\n")) with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation.maxTokens: must be positive")
    | Ok _ -> failwith "maxTokens <= 0 应报错"

    // maxContextMessages < 0
    match TomlCodec.tryParse (sampleToml.Replace("maxContextMessages = 120", "maxContextMessages = -1")) with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation.maxContextMessages: must not be negative")
    | Ok _ -> failwith "maxContextMessages < 0 应报错"

    // maxContextTokens < 0
    match TomlCodec.tryParse (sampleToml.Replace("[generation]\n", "[generation]\nmaxContextTokens = -5\n")) with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation.maxContextTokens: must not be negative")
    | Ok _ -> failwith "maxContextTokens < 0 应报错"

    // maxToolRounds <= 0
    match TomlCodec.tryParse (sampleToml.Replace("maxToolRounds = 8", "maxToolRounds = 0")) with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation.maxToolRounds: must be positive")
    | Ok _ -> failwith "maxToolRounds <= 0 应报错"

    // thinkingBudget < 0
    match TomlCodec.tryParse (sampleToml.Replace("[generation]\n", "[generation]\nthinkingBudget = -1\n")) with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "generation.thinkingBudget: must not be negative")
    | Ok _ -> failwith "thinkingBudget < 0 应报错"

[<Fact>]
let ``tools 节非表、未知键、超时非正及相对路径报错`` () =
    let notTable = "tools = 1\n" + (sampleToml.Replace("[tools]\ncallTimeoutSeconds", "#tools\n#callTimeoutSeconds"))
    match TomlCodec.tryParse notTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "tools: expected table")
    | Ok _ -> failwith "tools 非表应报错"

    let unknownKey = sampleToml.Replace("[tools]\ncallTimeoutSeconds", "[tools]\nunknown = 1\ncallTimeoutSeconds")
    match TomlCodec.tryParse unknownKey with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "tools.unknown: unknown field")
    | Ok _ -> failwith "tools 未知键应报错"

    let badTimeout = sampleToml.Replace("callTimeoutSeconds = 20", "callTimeoutSeconds = 0")
    match TomlCodec.tryParse badTimeout with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "tools.callTimeoutSeconds: must be positive")
    | Ok _ -> failwith "callTimeoutSeconds <= 0 应报错"

    let relativeRoot = sampleToml.Replace("[tools]\n", "[tools]\nfileReadRoots = [\"relative/path\"]\n")
    match TomlCodec.tryParse relativeRoot with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "must be an absolute path")
    | Ok _ -> failwith "非绝对路径 fileReadRoots 应报错"

[<Fact>]
let ``providers 节非表、不支持类型、空/相对端点、空模型、默认模型不存在与参数校验`` () =
    let notTable =
        sampleToml.Replace("configVersion = 2\n", "configVersion = 2\nproviders = 1\n")
                  .Replace("[providers.openai]\nkind = \"openai\"\nlabel = \"OpenAI\"\nbaseUrl = \"https://api.openai.com/v1\"\napiKey = \"sk-test\"\nmodels = [\"gpt-4o-mini\", \"gpt-4o\"]\ndefaultModel = \"gpt-4o-mini\"\ntimeoutSeconds = 90\nmaxRetries = 3\nenabled = true\n", "")
    match TomlCodec.tryParse notTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "providers: expected table")
    | Ok _ -> failwith "providers 非表应报错"

    let providerNotTable = sampleToml.Replace("[providers.openai]\nkind", "[providers]\nopenai = \"bad\"\n#kind")
    match TomlCodec.tryParse providerNotTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "providers.openai: expected table")
    | Ok _ -> failwith "provider 项非表应报错"

    let unknownField = sampleToml.Replace("[providers.openai]\nkind", "[providers.openai]\nunknown = 1\nkind")
    match TomlCodec.tryParse unknownField with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "providers.openai.unknown: unknown field")
    | Ok _ -> failwith "provider 未知键应报错"

    let unsupportedKind = sampleToml.Replace("kind = \"openai\"", "kind = \"unsupported_xyz\"")
    match TomlCodec.tryParse unsupportedKind with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "unsupported kind")
    | Ok _ -> failwith "不支持 kind 应报错"

    let emptyBaseUrl = sampleToml.Replace("baseUrl = \"https://api.openai.com/v1\"", "baseUrl = \" \"")
    match TomlCodec.tryParse emptyBaseUrl with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "providers.openai.baseUrl: required")
    | Ok _ -> failwith "空 baseUrl 应报错"

    let notAbsoluteUrl = sampleToml.Replace("baseUrl = \"https://api.openai.com/v1\"", "baseUrl = \"api.openai.com/v1\"")
    match TomlCodec.tryParse notAbsoluteUrl with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "providers.openai.baseUrl: must be an absolute URL")
    | Ok _ -> failwith "非绝对 baseUrl 应报错"

    let emptyModels = sampleToml.Replace("models = [\"gpt-4o-mini\", \"gpt-4o\"]", "models = []")
    match TomlCodec.tryParse emptyModels with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "at least one model required")
    | Ok _ -> failwith "空 models 列表应报错"

    let badDefaultModel = sampleToml.Replace("defaultModel = \"gpt-4o-mini\"", "defaultModel = \"non-existent\"")
    match TomlCodec.tryParse badDefaultModel with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "is not in models")
    | Ok _ -> failwith "defaultModel 不在 models 里应报错"

    let badTimeout = sampleToml.Replace("timeoutSeconds = 90", "timeoutSeconds = 0")
    match TomlCodec.tryParse badTimeout with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "timeoutSeconds: must be positive")
    | Ok _ -> failwith "timeoutSeconds <= 0 应报错"

    let badRetries = sampleToml.Replace("maxRetries = 3", "maxRetries = -1")
    match TomlCodec.tryParse badRetries with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "maxRetries: must not be negative")
    | Ok _ -> failwith "maxRetries < 0 应报错"

[<Fact>]
let ``mcp 节非表、项非表、command与url互斥/均缺、url非绝对及并发超时校验`` () =
    let notTable =
        sampleToml.Replace("configVersion = 2\n", "configVersion = 2\nmcp = 1\n")
                  .Replace("[mcp.fs]\ncommand = \"npx\"\nargs = [\"-y\", \"server\"]\nmaxConcurrency = 2\ncallTimeoutSeconds = 45\n", "")
    match TomlCodec.tryParse notTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp: expected table")
    | Ok _ -> failwith "mcp 非表应报错"

    let mcpNotTable = sampleToml.Replace("[mcp.fs]\ncommand", "[mcp]\nfs = false\n#command")
    match TomlCodec.tryParse mcpNotTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp.fs: expected table")
    | Ok _ -> failwith "mcp.fs 非表应报错"

    let unknownKey = sampleToml.Replace("[mcp.fs]\ncommand", "[mcp.fs]\nunknown = 1\ncommand")
    match TomlCodec.tryParse unknownKey with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp.fs.unknown: unknown field")
    | Ok _ -> failwith "mcp.fs 未知字段应报错"

    // 缺少 command 与 url
    let neither = sampleToml.Replace("command = \"npx\"", "#no command")
    match TomlCodec.tryParse neither with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "either command or url is required")
    | Ok _ -> failwith "缺少 command 和 url 应报错"

    // 同时提供 command 与 url
    let both = sampleToml.Replace("command = \"npx\"", "command = \"npx\"\n" +
                                  "url = \"https://mcp.local\"")
    match TomlCodec.tryParse both with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mutually exclusive")
    | Ok _ -> failwith "command 和 url 互斥应报错"

    // url 不是绝对 URL
    let badUrl = sampleToml.Replace("command = \"npx\"", "url = \"bad-url\"")
    match TomlCodec.tryParse badUrl with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp.fs.url: must be an absolute URL")
    | Ok _ -> failwith "非绝对 URL 应报错"

    // callTimeoutSeconds <= 0
    let badTimeout = sampleToml.Replace("callTimeoutSeconds = 45", "callTimeoutSeconds = 0")
    match TomlCodec.tryParse badTimeout with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp.fs.callTimeoutSeconds: must be positive")
    | Ok _ -> failwith "callTimeoutSeconds <= 0 应报错"

    // maxConcurrency <= 0
    let badConcurrency = sampleToml.Replace("maxConcurrency = 2", "maxConcurrency = 0")
    match TomlCodec.tryParse badConcurrency with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp.fs.maxConcurrency: must be positive")
    | Ok _ -> failwith "maxConcurrency <= 0 应报错"

[<Fact>]
let ``auth 节非表、clients非表数组、未知键、tokenHash缺失及大小写格式校验`` () =
    let notTable =
        sampleToml.Replace("configVersion = 2\n", "configVersion = 2\nauth = 1\n")
                  .Replace("[[auth.clients]]\ntokenHash = \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"\nname = \"test\"\ncreatedAt = \"2026-01-01T00:00:00Z\"\nrevoked = false\n", "")
    match TomlCodec.tryParse notTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "auth: expected table")
    | Ok _ -> failwith "auth 非表应报错"

    let unknownAuthKey = sampleToml.Replace("[[auth.clients]]", "[auth]\nunknown = true\n[[auth.clients]]")
    match TomlCodec.tryParse unknownAuthKey with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "auth.unknown: unknown field")
    | Ok _ -> failwith "auth 未知键应报错"

    let clientsNotArray =
        sampleToml.Replace("[[auth.clients]]\ntokenHash = \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"\nname = \"test\"\ncreatedAt = \"2026-01-01T00:00:00Z\"\nrevoked = false",
                           "[auth]\nclients = \"string\"")
    match TomlCodec.tryParse clientsNotArray with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "auth.clients: expected array of tables")
    | Ok _ -> failwith "auth.clients 非表数组应报错"

    let unknownClientKey = sampleToml.Replace("[[auth.clients]]\ntokenHash", "[[auth.clients]]\nunknown = 1\ntokenHash")
    match TomlCodec.tryParse unknownClientKey with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "auth.clients[].unknown: unknown field")
    | Ok _ -> failwith "auth.clients 元素未知键应报错"

    let missingHash = sampleToml.Replace("tokenHash = \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "")
    match TomlCodec.tryParse missingHash with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "auth.clients[]: missing tokenHash")
    | Ok _ -> failwith "缺少 tokenHash 应报错"

    // tokenHash 大写
    let uppercaseHash = sampleToml.Replace("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
    match TomlCodec.tryParse uppercaseHash with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "auth.clients[]: tokenHash must be lowercase")
    | Ok _ -> failwith "大写 tokenHash 应报错"

    // tokenHash 含有非 16 进制字符
    let nonHexHash = sampleToml.Replace("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaz")
    match TomlCodec.tryParse nonHexHash with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "tokenHash must be lowercase hex sha256")
    | Ok _ -> failwith "非十六进制 tokenHash 应报错"

[<Fact>]
let ``v1 配置错误分支全面覆盖（缺少id、非表结构、字段越界、冲突校验）`` () =
    // v1 缺少 instanceId
    let noId = sampleTomlV1.Replace("instanceId = \"22222222-2222-2222-2222-222222222222\"", "")
    match TomlCodec.tryParse noId with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "instanceId: required UUID")
    | Ok _ -> failwith "v1 缺少 instanceId 应报错"

    // v1 network 非表
    let netNotTable = "network = false\n" + (sampleTomlV1.Replace("[network]\nlisten", "#network\n#listen"))
    match TomlCodec.tryParse netNotTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "network: expected table")
    | Ok _ -> failwith "v1 network 非表应报错"

    // v1 network 未知键
    let netUnknown = sampleTomlV1.Replace("[network]\nlisten", "[network]\nunknown = 1\nlisten")
    match TomlCodec.tryParse netUnknown with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "network.unknown: unknown field")
    | Ok _ -> failwith "v1 network 未知键应报错"

    // v1 network 参数越界
    let netBad = sampleTomlV1.Replace("listen = \"127.0.0.1:8765\"", "listen = \" \"")
                             .Replace("maxAttachmentBytes = 67108864", "maxAttachmentBytes = -1")
                             .Replace("chunkSizeBytes = 262144", "chunkSizeBytes = 0")
    match TomlCodec.tryParse netBad with
    | Error errs ->
        Assert.Contains(errs, fun e -> e.Contains "network.listen: required")
        Assert.Contains(errs, fun e -> e.Contains "network.maxAttachmentBytes: must be positive")
        Assert.Contains(errs, fun e -> e.Contains "network.chunkSizeBytes: must be positive")
    | Ok _ -> failwith "v1 network 越界应报错"

    // v1 pairing 非表与未知键
    let pairNotTable = "pairing = 1\n" + (sampleTomlV1.Replace("[pairing]\nfailureWindowMinutes", "#pairing\n#failureWindowMinutes"))
    match TomlCodec.tryParse pairNotTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "pairing: expected table")
    | Ok _ -> failwith "v1 pairing 非表应报错"

    let pairUnknown = sampleTomlV1.Replace("[pairing]\nfailureWindowMinutes", "[pairing]\nunknown = 1\nfailureWindowMinutes")
    match TomlCodec.tryParse pairUnknown with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "pairing.unknown: unknown field")
    | Ok _ -> failwith "v1 pairing 未知键应报错"

    // v1 providers 非表、项非表与未知键
    let provNotTable = "providers = 1\n" + (sampleTomlV1.Replace("[providers.openai]", "#[providers.openai]"))
    match TomlCodec.tryParse provNotTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "providers: expected table")
    | Ok _ -> failwith "v1 providers 非表应报错"

    let provItemNotTable = sampleTomlV1.Replace("[providers.openai]\nkind", "[providers]\nopenai = 1\n#kind")
    match TomlCodec.tryParse provItemNotTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "providers.openai: expected table")
    | Ok _ -> failwith "v1 providers 项非表应报错"

    let provUnknown = sampleTomlV1.Replace("[providers.openai]\nkind", "[providers.openai]\nunknown = 1\nkind")
    match TomlCodec.tryParse provUnknown with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "providers.openai.unknown: unknown field")
    | Ok _ -> failwith "v1 provider 未知键应报错"

    // v1 provider 错误校验
    let provBad = sampleTomlV1.Replace("kind = \"openai\"", "kind = \"bad_kind\"")
                              .Replace("baseUrl = \"https://api.openai.com/v1\"", "baseUrl = \"not-url\"")
                              .Replace("model = \"gpt-4o\"", "model = \" \"")
    match TomlCodec.tryParse provBad with
    | Error errs ->
        Assert.Contains(errs, fun e -> e.Contains "unsupported kind")
        Assert.Contains(errs, fun e -> e.Contains "baseUrl: must be an absolute URL")
        Assert.Contains(errs, fun e -> e.Contains "providers.openai.model: required")
    | Ok _ -> failwith "v1 provider 错误应报错"

    // v1 mcp 非表、项非表、未知键、二选一校验、url非绝对、并发非正
    let mcpNotTable = "mcp = 1\n" + (sampleTomlV1.Replace("[mcp.fs]", "#[mcp.fs]"))
    match TomlCodec.tryParse mcpNotTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp: expected table")
    | Ok _ -> failwith "v1 mcp 非表应报错"

    let mcpItemNotTable = sampleTomlV1.Replace("[mcp.fs]\ncommand", "[mcp]\nfs = 1\n#command")
    match TomlCodec.tryParse mcpItemNotTable with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp.fs: expected table")
    | Ok _ -> failwith "v1 mcp 项非表应报错"

    let mcpUnknown = sampleTomlV1.Replace("[mcp.fs]\ncommand", "[mcp.fs]\nunknown = 1\ncommand")
    match TomlCodec.tryParse mcpUnknown with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp.fs.unknown: unknown field")
    | Ok _ -> failwith "v1 mcp 未知键应报错"

    let mcpNeither = sampleTomlV1.Replace("command = \"node\"", "#neither")
    match TomlCodec.tryParse mcpNeither with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "either command or url is required")
    | Ok _ -> failwith "v1 mcp 无 command/url 应报错"

    let mcpBoth = sampleTomlV1.Replace("command = \"node\"", "command = \"node\"\n" +
                                      "url = \"https://mcp.local\"")
    match TomlCodec.tryParse mcpBoth with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mutually exclusive")
    | Ok _ -> failwith "v1 mcp 冲突应报错"

    let mcpBadUrl = sampleTomlV1.Replace("command = \"node\"", "url = \"not-url\"")
    match TomlCodec.tryParse mcpBadUrl with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp.fs.url: must be an absolute URL")
    | Ok _ -> failwith "v1 mcp 非绝对 URL 应报错"

    let mcpBadConc = sampleTomlV1.Replace("maxConcurrency = 4", "maxConcurrency = 0")
    match TomlCodec.tryParse mcpBadConc with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "mcp.fs.maxConcurrency: must be positive")
    | Ok _ -> failwith "v1 mcp 并发 <= 0 应报错"

    let mcpIdConflict = sampleTomlV1.Replace("[mcp.fs]", "[mcp.openai]")
    match TomlCodec.tryParse mcpIdConflict with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "id conflicts with provider id")
    | Ok _ -> failwith "v1 mcp id 冲突应报错"

[<Fact>]
let ``复杂配置往返：包含extraJson递归(对象/数组/值/数字/布尔/字符串)、环境变量、headers与自定义选项`` () =
    let tomlWithAll = """
configVersion = 2
instanceId = "33333333-3333-3333-3333-333333333333"
[runtime]
server = false
client = true
pwa = false
fix = true
[network]
listen = "0.0.0.0:8080"
maxAttachmentBytes = 33554432
chunkSizeBytes = 131072
[pairing]
failureWindowMinutes = 2
maxFailures = 10
freezeMinutes = 15
[generation]
temperature = 1
topP = 0.95
maxTokens = 4096
instructions = "You are a helpful assistant."
maxContextMessages = 150
maxContextTokens = 64000
autoTitle = false
maxToolRounds = 15
thinkingBudget = 1024
[tools]
fileReadRoots = ["/var/data", "/tmp/sandbox"]
callTimeoutSeconds = 40
[providers.anthropic]
kind = "anthropic"
label = "Claude"
baseUrl = "https://api.anthropic.com"
apiKey = "sk-ant-test"
models = ["claude-3-5-sonnet-20241022", "claude-3-5-haiku-20241022"]
defaultModel = "claude-3-5-sonnet-20241022"
timeoutSeconds = 180
maxRetries = 2
enabled = true
promptCaching = true
[providers.anthropic.headers]
X-Custom-Header = "custom-val"
[providers.anthropic.extra]
system_prompt = "custom"
cache_enabled = true
priority = 10
ratio = 0.75
tags = ["fast", "smart"]
nested = { depth = 2, active = false }
[mcp.remote]
label = "Remote MCP"
url = "https://mcp.remote.example.com/sse"
callTimeoutSeconds = 50
maxConcurrency = 8
enabled = false
[mcp.remote.env]
ENV_VAR_A = "val_a"
ENV_VAR_B = "val_b"
[[auth.clients]]
tokenHash = "1111111111111111111111111111111111111111111111111111111111111111"
name = "full-client"
createdAt = "2026-02-01T12:00:00Z"
lastSeen = "2026-02-02T15:30:00Z"
revoked = true
"""
    match TomlCodec.tryParse tomlWithAll with
    | Error errs -> failwith (String.concat "; " errs)
    | Ok cfg ->
        // 校验解析
        Assert.False cfg.runtime.server
        Assert.True cfg.runtime.fix
        Assert.Equal(2, cfg.pairingFailureWindowMinutes)
        Assert.Equal(10, cfg.pairingMaxFailures)
        Assert.Equal(15, cfg.pairingFreezeMinutes)
        Assert.Equal(Some 1.0, cfg.generation.temperature)
        Assert.Equal(Some 0.95, cfg.generation.topP)
        Assert.Equal(Some 4096, cfg.generation.maxTokens)
        Assert.Equal(Some "You are a helpful assistant.", cfg.generation.instructions)
        Assert.False cfg.generation.autoTitle
        Assert.Equal(1024, cfg.generation.thinkingBudget)
        Assert.Equal<string list>([ "/var/data"; "/tmp/sandbox" ], cfg.tools.fileReadRoots)
        Assert.Equal(40, cfg.tools.callTimeoutSeconds)
        Assert.True(cfg.providers.ContainsKey "anthropic")
        let p = cfg.providers["anthropic"]
        Assert.Equal("Claude", p.label)
        Assert.True p.promptCaching
        Assert.Equal("custom-val", p.headers["X-Custom-Header"])
        Assert.True p.extraJson.IsSome
        let m = cfg.mcpServers["remote"]
        Assert.Equal("Remote MCP", m.label)
        Assert.Equal(Some "https://mcp.remote.example.com/sse", m.url)
        Assert.Equal(None, m.command)
        Assert.False m.enabled
        Assert.Equal(8, m.maxConcurrency |> Option.defaultValue 0)
        Assert.Equal("val_a", m.env["ENV_VAR_A"])
        let c = cfg.authClients.Head
        Assert.True c.revoked
        Assert.True c.lastSeenUtc.IsSome

        // 序列化回写并再次解析
        let serialized = TomlCodec.serialize cfg
        Assert.Contains("promptCaching = true", serialized)
        Assert.Contains("thinkingBudget = 1024", serialized)
        Assert.Contains("X-Custom-Header", serialized)
        Assert.Contains("ENV_VAR_A", serialized)
        Assert.Contains("Remote MCP", serialized)
        match TomlCodec.tryParse serialized with
        | Error errs -> failwith (String.concat "; " errs)
        | Ok cfg2 ->
            Assert.Equal(cfg.instanceId, cfg2.instanceId)
            Assert.Equal(cfg.generation.thinkingBudget, cfg2.generation.thinkingBudget)
            Assert.Equal<string list>(cfg.tools.fileReadRoots, cfg2.tools.fileReadRoots)
            Assert.Equal(cfg.providers["anthropic"].promptCaching, cfg2.providers["anthropic"].promptCaching)
            Assert.Equal<Map<string, string>>(cfg.providers["anthropic"].headers, cfg2.providers["anthropic"].headers)
            Assert.Equal(cfg.mcpServers["remote"].url, cfg2.mcpServers["remote"].url)
            Assert.Equal<Map<string, string>>(cfg.mcpServers["remote"].env, cfg2.mcpServers["remote"].env)
            Assert.Equal(cfg.authClients.Head.revoked, cfg2.authClients.Head.revoked)

// ================================================================
// ConfigModel 辅助方法测试（ProviderConfig, McpServerConfig, AppConfig）
// ================================================================

[<Fact>]
let ``ProviderConfig 辅助函数：displayName, hasModel, resolveModel 覆盖`` () =
    let pNoLabel = {
        id = "my-p"
        kind = "openai"
        label = ""
        baseUrl = "https://example.com"
        apiKey = None
        models = [ "gpt-4"; "gpt-3.5" ]
        defaultModel = "gpt-4"
        timeoutSeconds = 60
        maxRetries = 0
        enabled = true
        promptCaching = false
        headers = Map.empty
        extraJson = None
    }
    Assert.Equal("my-p", ProviderConfig.displayName pNoLabel)

    let pWithLabel = { pNoLabel with label = "My Super Provider" }
    Assert.Equal("My Super Provider", ProviderConfig.displayName pWithLabel)

    Assert.True(ProviderConfig.hasModel "gpt-4" pNoLabel)
    Assert.True(ProviderConfig.hasModel "gpt-3.5" pNoLabel)
    Assert.False(ProviderConfig.hasModel "claude" pNoLabel)

    // resolveModel
    // 1. requested 在列表中
    Assert.Equal("gpt-3.5", ProviderConfig.resolveModel "gpt-3.5" pNoLabel)
    // 2. requested 为空 -> 回落 defaultModel
    Assert.Equal("gpt-4", ProviderConfig.resolveModel "" pNoLabel)
    Assert.Equal("gpt-4", ProviderConfig.resolveModel "not-exist" pNoLabel)
    // 3. defaultModel 也为空 -> 回落 models.head
    let pNoDefault = { pNoLabel with defaultModel = "" }
    Assert.Equal("gpt-4", ProviderConfig.resolveModel "not-exist" pNoDefault)
    // 4. models 为空 -> 回落空字符串
    let pEmptyModels = { pNoLabel with models = []; defaultModel = "" }
    Assert.Equal("", ProviderConfig.resolveModel "not-exist" pEmptyModels)

[<Fact>]
let ``McpServerConfig 辅助函数：displayName 覆盖`` () =
    let mNoLabel = {
        id = "mcp1"
        label = "   "
        command = Some "run"
        args = []
        env = Map.empty
        url = None
        maxConcurrency = None
        callTimeoutSeconds = 60
        enabled = true
    }
    Assert.Equal("mcp1", McpServerConfig.displayName mNoLabel)
    let mWithLabel = { mNoLabel with label = "MCP Server 1" }
    Assert.Equal("MCP Server 1", McpServerConfig.displayName mWithLabel)

[<Fact>]
let ``AppConfig usableProviders, defaultProvider, tryProvider 覆盖`` () =
    let id = Guid.NewGuid()
    let cfg = AppConfig.defaults id
    Assert.True(AppConfig.usableProviders cfg |> List.isEmpty)
    Assert.True(AppConfig.defaultProvider cfg |> Option.isNone)
    Assert.True(AppConfig.tryProvider "p1" cfg |> Option.isNone)

    let p1 = {
        id = "p-beta"
        kind = "openai"
        label = ""
        baseUrl = "https://b.com"
        apiKey = None
        models = [ "m1" ]
        defaultModel = "m1"
        timeoutSeconds = 60
        maxRetries = 0
        enabled = true
        promptCaching = false
        headers = Map.empty
        extraJson = None
    }
    let p2 = { p1 with id = "p-alpha" }
    let pDisabled = { p1 with id = "p-disabled"; enabled = false }
    let pNoModels = { p1 with id = "p-nomodels"; models = [] }

    let cfgWithProviders = {
        cfg with
            providers =
                Map.ofList [
                    p1.id, p1
                    p2.id, p2
                    pDisabled.id, pDisabled
                    pNoModels.id, pNoModels
                ]
    }
    // usableProviders 应过滤 disabled 和 无模型的，且按 id 排序（p-alpha 在前）
    let usable = AppConfig.usableProviders cfgWithProviders
    Assert.Equal(2, usable.Length)
    Assert.Equal("p-alpha", usable.[0].id)
    Assert.Equal("p-beta", usable.[1].id)

    // defaultProvider 为首个可用者
    let defP = AppConfig.defaultProvider cfgWithProviders
    Assert.True defP.IsSome
    Assert.Equal("p-alpha", defP.Value.id)

    // tryProvider
    Assert.True((AppConfig.tryProvider "p-beta" cfgWithProviders).IsSome)
    Assert.True((AppConfig.tryProvider "non-existent" cfgWithProviders).IsNone)

[<Fact>]
let ``ConfigStore Update 原子读改写与 Path 属性覆盖`` () =
    let dir = Path.Combine(Path.GetTempPath(), "wanxiang-config-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "wanxiang.toml")
    try
        let initial = AppConfig.defaults (Guid.NewGuid())
        File.WriteAllText(path, TomlCodec.serialize initial)
        use store =
            match ConfigStore.Open(path, ignore, ignore) with
            | Ok s -> s
            | Error e -> failwith e
        Assert.Equal(path, store.Path)
        match store.Update(fun c -> { c with listen = "127.0.0.1:4444" }) with
        | Ok () ->
            Assert.Equal("127.0.0.1:4444", store.Current.listen)
        | Error e -> failwith e
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)


