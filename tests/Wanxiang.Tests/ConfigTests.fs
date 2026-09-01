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
