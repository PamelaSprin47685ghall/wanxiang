module Wanxiang.Tests.DoctorTests

open System
open System.IO
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Server
open Wanxiang.Tests.Helpers

/// O-003：doctor 扩展检查（AGENTS.md §19）。
/// 所有断言针对 Doctor.run 的检查项（只读，不修改文件）。
module Doctor =

    let private writeConfig (dir: string) : string =
        let path = Path.Combine(dir, "config.toml")
        let cfg = AppConfig.defaults (Guid.NewGuid())
        let withKey =
            { cfg with
                providers =
                    Map.ofList
                        [ "openai",
                          { id = "openai"
                            kind = "openai"
                            label = "OpenAI"
                            baseUrl = "https://api.openai.com/v1"
                            apiKey = Some "sk-secret-key-123456789"
                            models = [ "gpt-4o" ]
                            defaultModel = "gpt-4o"
                            timeoutSeconds = 120
                            maxRetries = 0
                            promptCaching = false
                            enabled = true
                            headers = Map.empty
                            extraJson = None; visionOverrides = Map.empty } ] }
        File.WriteAllText(path, Wanxiang.Config.TomlCodec.serialize withKey)
        path

    let private check (d: Wanxiang.Server.Doctor.Diagnosis) (name: string) : Wanxiang.Server.Doctor.CheckResult =
        d.checks |> List.find (fun c -> c.name = name)

    [<Fact>]
    let ``健康数据目录：全部检查通过`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let configPath = writeConfig dir
            let d = Wanxiang.Server.Doctor.run (configPath, dir)
            for name in [ "config"; "format-version"; "data-lock"; "data-permissions"; "disk-free-space"; "snapshot"; "recovery-bundle"; "log-replay"; "missing-attachments"; "orphan-attachments"; "provider-config"; "mcp-config"; "listen-port" ] do
                let c = check d name
                Assert.True(c.ok, sprintf "%s: %s" name c.detail)
        finally
            cleanup dir

    [<Fact>]
    let ``format-version 检查报告当前权威版本`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let configPath = writeConfig dir
            let d = Wanxiang.Server.Doctor.run (configPath, dir)
            let c = check d "format-version"
            Assert.True(c.ok)
            Assert.Contains(sprintf "%d" Constants.FormatVersion, c.detail)
        finally
            cleanup dir

    [<Fact>]
    let ``provider-config 检查发现启用但无 apiKey`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let path = Path.Combine(dir, "config.toml")
            let cfg = AppConfig.defaults (Guid.NewGuid())
            let bad =
                { cfg with
                    providers =
                        Map.ofList
                            [ "openai",
                              { id = "openai"
                                kind = "openai"
                                label = "OpenAI"
                                baseUrl = "https://api.openai.com/v1"
                                apiKey = None
                                models = [ "gpt-4o" ]
                                defaultModel = "gpt-4o"
                                timeoutSeconds = 120
                                maxRetries = 0
                                promptCaching = false
                                enabled = true
                                headers = Map.empty
                                extraJson = None; visionOverrides = Map.empty } ] }
            File.WriteAllText(path, Wanxiang.Config.TomlCodec.serialize bad)
            let d = Wanxiang.Server.Doctor.run (path, dir)
            let c = check d "provider-config"
            Assert.False(c.ok)
            Assert.Contains("no apiKey", c.detail)
        finally
            cleanup dir

    [<Fact>]
    let ``mcp-config 检查发现启用的 command 文件不存在`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let path = Path.Combine(dir, "config.toml")
            let cfg = AppConfig.defaults (Guid.NewGuid())
            let bad =
                { cfg with
                    mcpServers =
                        Map.ofList
                            [ "fs",
                              { id = "fs"
                                label = "FS"
                                command = Some "/nonexistent/mcp-server"
                                args = []
                                env = Map.empty
                                url = None
                                maxConcurrency = Some 4
                                callTimeoutSeconds = 20
                                enabled = true } ] }
            File.WriteAllText(path, Wanxiang.Config.TomlCodec.serialize bad)
            let d = Wanxiang.Server.Doctor.run (path, dir)
            let c = check d "mcp-config"
            Assert.False(c.ok)
            Assert.Contains("/nonexistent/mcp-server", c.detail)
        finally
            cleanup dir

    [<Fact>]
    let ``orphan-attachments 检查发现未被引用的 blob`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let configPath = writeConfig dir
            // 制造一个孤儿 blob（无任何消息引用）
            let orphanDir = Path.Combine(DataPaths.attachmentsDir dir, "ab", "cd")
            Directory.CreateDirectory orphanDir |> ignore
            File.WriteAllText(Path.Combine(orphanDir, "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890"), "orphan-content")
            let d = Wanxiang.Server.Doctor.run (configPath, dir)
            let c = check d "orphan-attachments"
            Assert.False(c.ok)
            Assert.Contains("1 unreferenced", c.detail)
        finally
            cleanup dir

    [<Fact>]
    let ``recovery-bundle 检查识别证据包存在`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let configPath = writeConfig dir
            // 无证据包：ok（detail 说明 none）
            let d0 = Wanxiang.Server.Doctor.run (configPath, dir)
            let c0 = check d0 "recovery-bundle"
            Assert.True(c0.ok)
            Assert.Contains("none", c0.detail)
            // 制造一个证据包目录
            let recoveryDir = Path.Combine(dir, "recovery", "2026-08-01T00-00-00Z")
            Directory.CreateDirectory recoveryDir |> ignore
            File.WriteAllText(Path.Combine(recoveryDir, "manifest.json"), "{}")
            let d1 = Wanxiang.Server.Doctor.run (configPath, dir)
            let c1 = check d1 "recovery-bundle"
            Assert.True(c1.ok)
            Assert.Contains("present", c1.detail)
        finally
            cleanup dir

    [<Fact>]
    let ``损坏日志：log-replay FAIL 且附件检查跳过`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let configPath = writeConfig dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            let p = DataPaths.eventFilePath dir day
            Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
            File.WriteAllBytes(p, Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1))
            File.AppendAllText(p, "garbage-no-lf")
            let d = Wanxiang.Server.Doctor.run (configPath, dir)
            let c = check d "log-replay"
            Assert.False(c.ok)
            Assert.Contains("damaged log", c.detail)
        finally
            cleanup dir

    [<Fact>]
    let ``doctor 检查绝不修改任何文件`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let configPath = writeConfig dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            let p = DataPaths.eventFilePath dir day
            Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
            File.WriteAllBytes(p, Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1))
            File.AppendAllText(p, "garbage-no-lf")
            // 排除 lock 文件（DataLock.Acquire 会写入 PID，属运行时工件，非 doctor 修改）
            let snapshotFiles () =
                Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                |> Array.filter (fun f -> Path.GetFileName f <> "lock")
                |> Array.map (fun f -> f, File.ReadAllBytes f)
            let before = snapshotFiles ()
            Wanxiang.Server.Doctor.run (configPath, dir) |> ignore
            let after = snapshotFiles ()
            Assert.Equal<string * byte[]>((before :> seq<string * byte[]>), (after :> seq<string * byte[]>))
        finally
            cleanup dir
