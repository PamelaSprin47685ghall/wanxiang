module Wanxiang.Tests.ConfigTests

open System
open System.IO
open Xunit
open Wanxiang.Config

let private sampleToml =
    """
configVersion = 1
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
[providers.openai]
kind = "openai"
baseUrl = "https://api.openai.com/v1"
apiKey = "sk-test"
model = "gpt-4o-mini"
[mcp.fs]
command = "npx"
args = ["-y", "server"]
maxConcurrency = 2
[[auth.clients]]
tokenHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
name = "test"
createdAt = "2026-01-01T00:00:00Z"
revoked = false
"""

[<Fact>]
let ``test_42936`` () =
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
        Assert.Equal(2, cfg.mcpServers["fs"].maxConcurrency |> Option.defaultValue 0)
        Assert.Equal(1, cfg.authClients.Length)
        // roundtrip
        let text = TomlCodec.serialize cfg
        match TomlCodec.tryParse text with
        | Error errs -> failwith (String.concat "; " errs)
        | Ok cfg2 ->
            Assert.Equal(cfg.listen, cfg2.listen)
            Assert.Equal(cfg.providers.Count, cfg2.providers.Count)
            Assert.Equal(cfg.authClients.Length, cfg2.authClients.Length)
            Assert.Equal(cfg.instanceId, cfg2.instanceId)

[<Fact>]
let ``test_94616`` () =
    let bad = sampleToml + "\n[network]\ntypo = true\n"
    // 注意：重复 [network] 表——TOML 允许合并；未知字段检查应命中
    match TomlCodec.tryParse (sampleToml.Replace("[network]\nlisten", "[network]\ntypo = 1\nlisten")) with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "unknown field")
    | Ok _ -> failwith "unknown field should be rejected"

[<Fact>]
let ``test_90606`` () =
    let bad = sampleToml.Replace("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "short")
    match TomlCodec.tryParse bad with
    | Error errs -> Assert.Contains(errs, fun e -> e.Contains "tokenHash")
    | Ok _ -> failwith "invalid token hash should be rejected"

[<Fact>]
let ``test_81417`` () =
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
        File.WriteAllText(path, "configVersion = 1\ninvalid = true\n")
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
