module Wanxiang.Tests.SupportBundleTests

open System
open System.IO
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Server
open Wanxiang.Tests.Helpers

/// O-002：Support Bundle（`wanxiang doctor --bundle <path>`）。
/// 只收集元数据；禁止包含 apiKey/token/完整用户消息/完整附件。
module SupportBundle =

    let private writeConfig (dir: string) : string * Guid =
        let path = Path.Combine(dir, "config.toml")
        let cfg = AppConfig.defaults (Guid.NewGuid())
        let instanceId = cfg.instanceId
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
        path, instanceId

    [<Fact>]
    let ``bundle 生成全部必需文件且不含密钥`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let configPath, cfgInstanceId = writeConfig dir
            let outPath = Path.Combine(dir, "bundle")
            match SupportBundle.create dir configPath outPath with
            | Error e -> failwith e
            | Ok _ ->
                // 必需文件
                for f in [ "diagnosis.json"; "versions.json"; "recent-errors.jsonl"; "config.redacted.toml"; "storage-layout.json"; "snapshot-status.json" ] do
                    Assert.True(File.Exists(Path.Combine(outPath, f)), sprintf "%s missing" f)
                // 配置脱敏：apiKey 值被替换
                let cfgText = File.ReadAllText(Path.Combine(outPath, "config.redacted.toml"))
                Assert.DoesNotContain("sk-secret-key-123456789", cfgText)
                Assert.Contains("***", cfgText)
                // versions.json 含各格式版本
                let versions = File.ReadAllText(Path.Combine(outPath, "versions.json"))
                Assert.Contains(sprintf "\"storageFormatVersion\":%d" Constants.FormatVersion, versions)
                Assert.Contains(sprintf "\"configFormatVersion\":%d" AppConfig.CurrentVersion, versions)
                // diagnosis.json 可追溯到来源实例（§18 关联 ID）
                let diag = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(outPath, "diagnosis.json"))) :?> System.Text.Json.Nodes.JsonObject
                Assert.True(diag.ContainsKey "instanceId", "diagnosis.json missing instanceId")
                Assert.Equal(string cfgInstanceId, string diag["instanceId"])
                // snapshot-status.json
                let snap = File.ReadAllText(Path.Combine(outPath, "snapshot-status.json"))
                Assert.Contains("no-snapshot", snap)
        finally
            cleanup dir

    [<Fact>]
    let ``bundle 的 diagnosis 反映损坏日志状态`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let configPath, _ = writeConfig dir
            // 制造损坏日志
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
            let p = DataPaths.eventFilePath dir day
            Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
            File.WriteAllBytes(p, Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1))
            File.AppendAllText(p, "garbage-no-lf")
            let outPath = Path.Combine(dir, "bundle")
            match SupportBundle.create dir configPath outPath with
            | Error e -> failwith e
            | Ok _ ->
                let diag = File.ReadAllText(Path.Combine(outPath, "diagnosis.json"))
                Assert.Contains("repair-required", diag)
                Assert.Contains("\"lastCommitId\":1", diag)
                Assert.Contains("unparseable", diag) // reason 含损坏描述（元数据，非业务内容）
        finally
            cleanup dir

    [<Fact>]
    let ``recent-errors.jsonl 收集 Stderr 错误且已脱敏`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let configPath, _ = writeConfig dir
            // 触发一条错误事件（含密钥值——必须被脱敏）
            Wanxiang.Server.Stderr.registerSecrets [ "sk-secret-key-123456789" ]
            Wanxiang.Server.Stderr.write "test-error" [ "message", "failed with sk-secret-key-123456789" ]
            let outPath = Path.Combine(dir, "bundle")
            match SupportBundle.create dir configPath outPath with
            | Error e -> failwith e
            | Ok _ ->
                let lines = File.ReadAllLines(Path.Combine(outPath, "recent-errors.jsonl"))
                Assert.True(lines.Length >= 1)
                Assert.Contains(lines, fun l -> l.Contains "test-error")
                // 密钥被脱敏
                Assert.DoesNotContain("sk-secret-key-123456789", String.concat "\n" lines)
        finally
            Wanxiang.Server.Stderr.clearSecrets ()
            cleanup dir

    [<Fact>]
    let ``storage-layout.json 列出事件文件但不含内容`` () =
        let dir = tempDir ()
        try
            DataPaths.ensureDataDirs dir
            let configPath, _ = writeConfig dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let convId = newConversationId ()
            let c1 = Events.Commit.create 1UL (DateTimeOffset(day)) [ ConversationCreated { conversationId = convId; title = "机密对话"; config = testConfig () } ]
            let p = DataPaths.eventFilePath dir day
            Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
            File.WriteAllBytes(p, Wanxiang.Core.Ledger.Frame.encode (CommitCodec.commitToJsonLine c1))
            let outPath = Path.Combine(dir, "bundle")
            match SupportBundle.create dir configPath outPath with
            | Error e -> failwith e
            | Ok _ ->
                let layout = File.ReadAllText(Path.Combine(outPath, "storage-layout.json"))
                Assert.Contains("2026-08-01.jsonseq", layout)
                // 不包含业务内容（消息正文/对话标题）
                Assert.DoesNotContain("机密对话", layout)
        finally
            cleanup dir
