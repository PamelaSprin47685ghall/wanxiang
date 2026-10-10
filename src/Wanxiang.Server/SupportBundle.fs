namespace Wanxiang.Server

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Store

/// O-002：Support Bundle（`wanxiang doctor --bundle <path>`）。
///
/// 生成（默认只收集元数据，禁止业务内容）：
///   diagnosis.json          只读 doctor 诊断结果
///   versions.json           各格式版本常量（version-authority.md 的可执行快照）
///   recent-errors.jsonl     最近错误事件（Stderr 环形缓冲，已脱敏）
///   config.redacted.toml    配置（apiKey 等凭据值替换为 ***）
///   storage-layout.json     数据目录布局（文件清单 + 大小，不含内容）
///   snapshot-status.json    快照状态（最新有效快照 / 损坏 / 无）
///
/// 禁止包含：apiKey、token、完整用户消息、完整附件（R1/§20）。
/// 用户明确选择后（未来版本），才允许附加业务内容。
module SupportBundle =

    let private jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.General)

    let private writeJson (path: string) (o: JsonObject) : unit =
        File.WriteAllText(path, o.ToJsonString jsonOptions)

    /// 版本快照（P-001/P-002 可执行化）。
    let private versionsJson () : JsonObject =
        let o = JsonObject()
        o["storageFormatVersion"] <- Constants.FormatVersion
        o["configFormatVersion"] <- AppConfig.CurrentVersion
        o["wireProtocolVersion"] <- Constants.ProtocolVersion
        o["aguiProtocolVersion"] <- Wanxiang.Agui.Capabilities.ProtocolVersion
        // McpJson 定义在本文件之后编译（McpTransport.fs）；此处用字面量，
        // ContractTests 已钉住 McpJson.ProtocolVersion = "2025-06-18"（P-002 单一权威）。
        o["mcpProtocolVersion"] <- "2025-06-18"
        o["snapshotFormatVersion"] <- Wanxiang.Store.Snapshot.FormatVersion
        o

    /// 存储布局：events/attachments/snapshots/recovery 的文件清单（名称 + 大小，不含内容）。
    let private storageLayoutJson (dataDir: string) : JsonObject =
        let o = JsonObject()
        let dirs =
            [ "events", DataPaths.eventsDir dataDir
              "attachments", DataPaths.attachmentsDir dataDir
              "snapshots", Wanxiang.Store.Snapshot.snapshotsDir dataDir
              "recovery", Path.Combine(dataDir, "recovery") ]
        let filesArr = JsonArray()
        for (label, dir) in dirs do
            if Directory.Exists dir then
                for f in Directory.GetFiles(dir) do
                    let entry = JsonObject()
                    entry["section"] <- label
                    entry["file"] <- Path.GetFileName f
                    entry["size"] <- FileInfo(f).Length
                    filesArr.Add entry
        o["files"] <- filesArr
        o

    /// 快照状态（算法 L 的当前结论）。
    let private snapshotStatusJson (dataDir: string) : JsonObject =
        let o = JsonObject()
        match Wanxiang.Store.Snapshot.findLatest dataDir with
        | Wanxiang.Store.SnapshotStartup.NoSnapshot -> o["status"] <- "no-snapshot"
        | Wanxiang.Store.SnapshotStartup.SnapshotUsed info ->
            o["status"] <- "valid"
            o["throughCommitId"] <- info.throughCommitId
            o["createdAt"] <- info.createdAtUtc.UtcDateTime.ToString("o")
            o["file"] <- Path.GetFileName info.path
        | Wanxiang.Store.SnapshotStartup.SnapshotCorrupt reasons ->
            o["status"] <- "corrupt"
            o["reasons"] <- JsonArray([| for r in reasons -> JsonNode.op_Implicit r |])
        | Wanxiang.Store.SnapshotStartup.SnapshotStale reason -> o["status"] <- "stale"; o["reason"] <- reason
        o

    /// 脱敏配置：apiKey/token 值替换为 ***（与 Stderr.redact 同一规则）。
    let private redactedConfig (cfg: AppConfig) : string =
        let text = Wanxiang.Config.TomlCodec.serialize cfg
        // 注册的密钥值精确替换（与 stderr 一致）
        let secrets =
            cfg.providers
            |> Seq.choose (fun kv -> kv.Value.apiKey)
            |> Seq.filter (fun k -> not (String.IsNullOrWhiteSpace k))
            |> Seq.toList
        let mutable s = text
        for secret in secrets do
            if s.Contains secret then s <- s.Replace(secret, "***")
        // 兜底：任何形如 apiKey = "..." 的行直接打码（防止未注册的密钥泄漏）
        s.Split('\n')
        |> Array.map (fun line ->
            let t = line.TrimStart()
            if t.StartsWith("apiKey") || t.StartsWith("token") || t.StartsWith("secret") then
                let idx = line.IndexOf '='
                if idx >= 0 then line.Substring(0, idx + 1) + " \"***\""
                else line
            else line)
        |> String.concat "\n"

    /// 创建 support bundle。`configPath` 不存在时 config.redacted.toml 写错误说明。
    /// 返回 bundle 目录完整路径。
    let create (dataDir: string) (configPath: string) (outPath: string) : Result<string, string> =
        try
            Directory.CreateDirectory outPath |> ignore
            // diagnosis.json：只读 doctor 结果（O-003：Doctor 是诊断单一权威）
            let diag = JsonObject()
            diag["createdAt"] <- DateTimeOffset.UtcNow.UtcDateTime.ToString("o")
            diag["dataDir"] <- dataDir
            // §18 关联 ID：bundle 必须可追溯到来源实例（多实例部署诊断时区分）。
            // 配置不可读（损坏）时省略——诊断仍可进行，不因缺 instanceId 失败。
            if File.Exists configPath then
                match Wanxiang.Config.TomlCodec.tryParse (File.ReadAllText configPath) with
                | Ok cfg -> diag["instanceId"] <- string cfg.instanceId
                | Error _ -> ()
            let diagnosis = Doctor.run (configPath, dataDir)
            diag["configOk"] <- diagnosis.checks |> List.exists (fun c -> c.name = "config" && c.ok)
            diag["configErrors"] <- JsonArray([| for e in diagnosis.configErrors -> JsonNode.op_Implicit e |])
            let logStatus =
                match Replay.inspect dataDir with
                | Healthy _ -> "healthy"
                | RepairRequired (o, plan) ->
                    diag["lastCommitId"] <- o.lastCommitId
                    diag["damageFile"] <- plan.damage.file
                    diag["damageOffset"] <- plan.damage.byteOffset
                    diag["reason"] <- plan.damage.reason
                    "repair-required"
                | UnsupportedFormat msg ->
                    diag["message"] <- msg
                    "unsupported-format"
                | Unreadable msg ->
                    diag["message"] <- msg
                    "unreadable"
            diag["logStatus"] <- logStatus
            diag["lockOk"] <- diagnosis.checks |> List.exists (fun c -> c.name = "data-lock" && c.ok)
            let checksArr = JsonArray()
            for c in diagnosis.checks do
                let co = JsonObject()
                co["name"] <- c.name
                co["ok"] <- c.ok
                co["detail"] <- c.detail
                checksArr.Add(co :> JsonNode)
            diag["checks"] <- checksArr
            writeJson (Path.Combine(outPath, "diagnosis.json")) diag
            // versions.json
            writeJson (Path.Combine(outPath, "versions.json")) (versionsJson ())
            // recent-errors.jsonl（已脱敏）
            File.WriteAllLines(Path.Combine(outPath, "recent-errors.jsonl"), Wanxiang.Server.Stderr.recentErrorLines ())
            // config.redacted.toml
            let configOut =
                if File.Exists configPath then
                    match TomlCodec.tryParse (File.ReadAllText configPath) with
                    | Ok cfg -> redactedConfig cfg
                    | Error errs -> "# config parse failed:\n" + String.concat "\n" errs
                else
                    sprintf "# config file not found: %s" configPath
            File.WriteAllText(Path.Combine(outPath, "config.redacted.toml"), configOut)
            // storage-layout.json
            writeJson (Path.Combine(outPath, "storage-layout.json")) (storageLayoutJson dataDir)
            // snapshot-status.json
            writeJson (Path.Combine(outPath, "snapshot-status.json")) (snapshotStatusJson dataDir)
            Ok outPath
        with e -> Error(sprintf "support bundle creation failed: %s" e.Message)
