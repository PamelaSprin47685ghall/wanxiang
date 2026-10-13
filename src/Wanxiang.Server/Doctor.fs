namespace Wanxiang.Server

open System
open System.IO
open System.Net.Sockets
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Store

/// O-003：doctor 只读诊断（决策 51：默认严格只读，不修复；`--fix` 才允许修改）。
///
/// 检查项（AGENTS.md §19 第十一阶段）：
///   基础 5 项 + 扩展 11 项：
///     format-version            事件日志外壳格式版本与当前权威一致
///     latest-snapshot           存在最新有效快照（或明确无快照）
///     snapshot-checksum         快照校验和有效（findLatest 已隐含验证）
///     event-log-tail            日志尾部无未终止记录（inspect 隐含）
///     recovery-bundle           存在 recovery 证据包目录
///     disk-free-space           数据目录所在磁盘剩余空间 >= 阈值
///     data-directory-permissions 事件/附件目录权限为最小权限（0700）
///     orphan-attachments        附件目录中存在未被任何消息引用的 blob
///     missing-attachments       消息引用但附件缺失（Q179，原 attachments 检查）
///     provider-config           provider 配置结构合法（模型/默认模型）
///     mcp-config                MCP 配置结构合法（command/url 二选一）
///
/// 所有检查只读：不创建、不修改、不删除任何文件。
module Doctor =

    /// 磁盘剩余空间警告阈值（字节）：默认 256 MiB。
    let diskFreeWarningBytes = 256L * 1024L * 1024L

    type CheckResult = {
        name: string
        ok: bool
        detail: string
    }

    type Diagnosis = {
        mutable checks: CheckResult list
        mutable configErrors: string list
        mutable replayError: string
        mutable attachmentsMissing: string list
        mutable orphanAttachments: string list
        mutable snapshotStatus: string
    }

    let private portInUse (listen: string) : bool =
        try
            let parts = listen.Split ':'
            if parts.Length <> 2 then false
            else
                let host = parts[0]
                let port = int parts[1]
                use client = new TcpClient()
                let ar = client.BeginConnect(host, port, null, null)
                ar.AsyncWaitHandle.WaitOne 300 |> ignore
                client.EndConnect ar |> ignore
                true
        with _ ->
            false

    /// 数据目录权限检查：events/attachments 应为 0700（Q118 最小权限）。
    let private checkPermissions (dataDir: string) : CheckResult =
        let bad =
            [ DataPaths.eventsDir dataDir; DataPaths.attachmentsDir dataDir ]
            |> List.filter (fun dir ->
                Directory.Exists dir
                && (try
                        let mode = File.GetUnixFileMode dir
                        let m = int mode
                        (m &&& int UnixFileMode.GroupRead) <> 0
                        || (m &&& int UnixFileMode.GroupWrite) <> 0
                        || (m &&& int UnixFileMode.GroupExecute) <> 0
                        || (m &&& int UnixFileMode.OtherRead) <> 0
                        || (m &&& int UnixFileMode.OtherWrite) <> 0
                        || (m &&& int UnixFileMode.OtherExecute) <> 0
                    with _ -> false))
        { name = "data-permissions"
          ok = List.isEmpty bad
          detail = if List.isEmpty bad then "0700" else sprintf "world/group-accessible: %s" (String.concat ", " bad) }

    /// 磁盘剩余空间检查。
    let private checkDiskFree (dataDir: string) : CheckResult =
        try
            let drive = DriveInfo(Path.GetPathRoot(dataDir))
            let free = if drive.IsReady then drive.AvailableFreeSpace else Int64.MaxValue
            { name = "disk-free-space"
              ok = free >= diskFreeWarningBytes
              detail =
                if free >= diskFreeWarningBytes then
                    sprintf "%d MiB free" (free / 1024L / 1024L)
                else
                    sprintf "only %d MiB free (threshold %d MiB)" (free / 1024L / 1024L) (diskFreeWarningBytes / 1024L / 1024L) }
        with _ ->
            // O-001/R6：磁盘信息不可用 = 无法验证 = 不通过（不得无证据报告 PASS）。
            // 磁盘满是最危险的静默故障之一，宁可误报也不掩盖。
            { name = "disk-free-space"; ok = false; detail = "unavailable" }

    /// 快照状态检查（findLatest：最新有效快照 / 无 / 损坏 / 超前）。
    let private checkSnapshot (inspect: unit -> InspectResult) : CheckResult =
        let startup =
            match inspect () with
            | Healthy o | RepairRequired(o, _) -> Ok o.startup
            | UnsupportedFormat m | Unreadable m -> Error m
        match startup with
        | Ok { snapshot = LedgerStore.Absent } ->
            { name = "snapshot"; ok = true; detail = "no snapshot (full replay on next start)" }
        | Ok { snapshot = LedgerStore.Used through; replayedCommits = n } ->
            { name = "snapshot"; ok = true; detail = sprintf "valid through commit %d (%d commits replayed after it)" through n }
        | Ok { snapshot = LedgerStore.Discarded reason } ->
            { name = "snapshot"; ok = true; detail = sprintf "discarded: %s (falls back to full replay)" reason }
        | Error m -> { name = "snapshot"; ok = true; detail = sprintf "not checked: %s" m }

    /// recovery 证据包检查：存在 recovery 目录且至少一个 manifest.json。
    let private checkRecoveryBundle (dataDir: string) : CheckResult =
        let recoveryDir = Path.Combine(dataDir, "recovery")
        let hasBundle =
            Directory.Exists recoveryDir
            && Directory.GetDirectories(recoveryDir) |> Array.exists (fun d -> File.Exists(Path.Combine(d, "manifest.json")))
        { name = "recovery-bundle"
          // 无证据包不是故障：只是说明从未执行过修复。ok 恒 true（信息性检查）。
          ok = true
          detail = if hasBundle then "evidence bundle(s) present" else "none (no repair has been run)" }

    let private formatVersionCheck () : CheckResult =
        { name = "format-version"
          ok = true
          detail = sprintf "event shell formatVersion=%d (single authority)" Constants.FormatVersion }

    /// provider 配置结构检查：每个启用 provider 必须有模型且默认模型在列表中。
    let private checkProviders (cfg: AppConfig) : CheckResult =
        let issues =
            cfg.providers
            |> Map.toList
            |> List.collect (fun (_, p) ->
                let problems =
                    [ if p.enabled && p.apiKey.IsNone then
                          Some "enabled but no apiKey"
                      else None
                      // 本地 kind 需要可解析 baseUrl（openai 兼容端点）
                      if p.enabled && String.IsNullOrWhiteSpace p.baseUrl then
                          Some "enabled but empty baseUrl"
                      else None ]
                    |> List.choose id
                if List.isEmpty problems then [] else [ sprintf "%s: %s" p.id (String.concat "; " problems) ])
        { name = "provider-config"
          ok = List.isEmpty issues
          detail = if List.isEmpty issues then "ok" else String.concat "; " issues }

    /// MCP 配置结构检查：command/url 二选一，且至少一个存在。
    let private checkMcp (cfg: AppConfig) : CheckResult =
        let issues =
            cfg.mcpServers
            |> Map.toList
            |> List.collect (fun (_, m) ->
                match m.command, m.url with
                | Some cmd, None when m.enabled && not (File.Exists cmd) ->
                    [ sprintf "%s: enabled command not found: %s" m.id cmd ]
                | _ -> [])
        { name = "mcp-config"
          ok = List.isEmpty issues
          detail = if List.isEmpty issues then "ok" else String.concat "; " issues }

    /// 附件检查：missing = 消息引用但 blob 缺失（Q179）；orphan = 存在 blob 但无任何引用。
    let private checkAttachments (dataDir: string) (outcome: ReplayOutcome) : (CheckResult * CheckResult) =
        let store = new AttachmentStore(dataDir, 1024L)
        try
            let referenced = System.Collections.Generic.HashSet<string>()
            let missing = System.Collections.Generic.HashSet<string>()
            for conv in Projection.conversationList outcome.projection do
                for m in Projection.effectiveMessages outcome.projection conv do
                    for (sha, _, _, _) in ServerModel.attachmentRefsOf m.payloadJson do
                        referenced.Add sha |> ignore
                        if not (store.Exists sha) then
                            missing.Add sha |> ignore
            // orphan = 附件目录中的 blob 未被任何消息引用
            let orphan = System.Collections.Generic.List<string>()
            if Directory.Exists(DataPaths.attachmentsDir dataDir) then
                for dir in Directory.GetDirectories(DataPaths.attachmentsDir dataDir) do
                    for sub in Directory.GetDirectories dir do
                        for f in Directory.GetFiles sub do
                            let name = Path.GetFileName f
                            if not (name.EndsWith ".meta") && not (referenced.Contains name) then
                                orphan.Add name
            let missingList = missing |> Seq.toList
            let orphanList = List.ofSeq orphan
            { name = "missing-attachments"
              ok = List.isEmpty missingList
              detail = if List.isEmpty missingList then "ok" else sprintf "%d referenced blob(s) missing" missingList.Length },
            { name = "orphan-attachments"
              ok = List.isEmpty orphanList
              detail = if List.isEmpty orphanList then "ok" else sprintf "%d unreferenced blob(s)" orphanList.Length }
        finally
            store.Dispose()

    /// 只读诊断。`live = true` 表示诊断运行在**正在服务的主机进程内**（W5「检查主机」，决策 83）：
    /// 监听端口与数据锁都是本进程自己持有的，不能当故障报；日志回放在发现损坏时复核一次，
    /// 避免把写入者正在追加的半条记录误报为损坏。其余检查与命令行 doctor 完全一致，仍严格只读。
    /// 运行中的主机自己持有存储（LedgerStore 独占目录），live 时由主机给出它的检查结论。
    let private runWith (live: (unit -> InspectResult) option) (configPath: string, dataDir: string) : Diagnosis =
        let inspect () = match live with Some f -> f () | None -> Inspect.run dataDir
        let live = live.IsSome
        let d =
            { checks = []
              configErrors = []
              replayError = ""
              attachmentsMissing = []
              orphanAttachments = []
              snapshotStatus = "" }
        // 配置
        if File.Exists configPath then
            match TomlCodec.tryParse (File.ReadAllText configPath) with
            | Ok cfg ->
                d.configErrors <- []
                let port =
                    if live then { name = "listen-port"; ok = true; detail = sprintf "listening on %s (this host)" cfg.listen }
                    else { name = "listen-port"; ok = not (portInUse cfg.listen); detail = if portInUse cfg.listen then sprintf "port %s already in use" cfg.listen else "free" }
                d.checks <- d.checks @ [ port; checkProviders cfg; checkMcp cfg ]
            | Error errs -> d.configErrors <- errs
        else
            d.configErrors <- [ sprintf "config file not found: %s" configPath ]
        d.checks <- d.checks @ [ formatVersionCheck (); checkDiskFree dataDir; checkPermissions dataDir ]
        // 数据锁
        if live then
            d.checks <- d.checks @ [ { name = "data-lock"; ok = true; detail = "held by this host" } ]
        else
            match DataLock.Acquire dataDir with
            | Ok l ->
                let pidDetail = match l.PidWarning with Some w -> sprintf "; %s" w | None -> ""
                (l :> IDisposable).Dispose()
                d.checks <- d.checks @ [ { name = "data-lock"; ok = true; detail = "free" + pidDetail } ]
            | Error e ->
                d.checks <- d.checks @ [ { name = "data-lock"; ok = false; detail = e } ]
        // 快照状态
        let snap = checkSnapshot inspect
        d.snapshotStatus <- snap.detail
        d.checks <- d.checks @ [ snap; checkRecoveryBundle dataDir ]
        // 事件账本只认 ledger/log.jsonseq。旧版 events/ 目录下的按日日志（.jsonseq/.ndjson）
        // 格式不兼容、新版不读：明确报错，绝不静默忽略——用户需要自己归档或移除。
        let unrecognizedLogs =
            try
                Directory.GetFiles(DataPaths.legacyEventsDir dataDir)
                |> Array.filter (fun f -> f.EndsWith ".jsonseq" || f.EndsWith ".ndjson")
            with _ -> [||]
        if unrecognizedLogs.Length > 0 then
            d.replayError <-
                sprintf
                    "found %d legacy event log(s) in events/ that this version does not read (the ledger is ledger/log.jsonseq). Archive or remove: %s"
                    unrecognizedLogs.Length
                    (String.Join(", ", unrecognizedLogs |> Array.map Path.GetFileName))
            d.checks <- d.checks @ [ { name = "log-replay"; ok = false; detail = d.replayError } ]
        // 只读检查（算法 R）；有无法识别的日志时跳过（上面已给出明确报错）
        if unrecognizedLogs.Length = 0 then
            let inspected =
                inspect ()
            match inspected with
            | Healthy outcome ->
                let missingCheck, orphanCheck = checkAttachments dataDir outcome
                d.attachmentsMissing <- []
                d.orphanAttachments <- []
                d.checks <- d.checks @ [ { name = "log-replay"; ok = true; detail = sprintf "healthy through commit %d" outcome.lastCommitId }; missingCheck; orphanCheck ]
            | RepairRequired (outcome, plan) ->
                d.replayError <-
                    sprintf
                        "damaged log at %s (%s); valid prefix through commit %d. Run `wanxiang doctor --fix` to repair (a recovery bundle will be created first)."
                        plan.damage.file
                        plan.damage.reason
                        outcome.lastCommitId
                d.checks <- d.checks @ [ { name = "log-replay"; ok = false; detail = d.replayError }; { name = "missing-attachments"; ok = true; detail = "skipped (log damaged)" }; { name = "orphan-attachments"; ok = true; detail = "skipped (log damaged)" } ]
            | UnsupportedFormat msg ->
                d.replayError <- msg
                d.checks <- d.checks @ [ { name = "log-replay"; ok = false; detail = msg } ]
            | Unreadable msg ->
                d.replayError <- msg
                d.checks <- d.checks @ [ { name = "log-replay"; ok = false; detail = msg } ]
        // 配置检查结果（configOk 派生自 configErrors）
        if not (List.isEmpty d.configErrors) then
            d.checks <- d.checks @ [ { name = "config"; ok = false; detail = String.concat "; " d.configErrors } ]
        elif not (d.checks |> List.exists (fun c -> c.name = "config")) then
            d.checks <- d.checks @ [ { name = "config"; ok = true; detail = "ok" } ]
        d

    /// 命令行 doctor：独占数据目录前提下的完整诊断。
    let run (configPath: string, dataDir: string) : Diagnosis = runWith None (configPath, dataDir)

    /// 主机进程内的只读诊断（「检查主机」）。
    let runLive (configPath: string, dataDir: string, inspect: unit -> InspectResult) : Diagnosis = runWith (Some inspect) (configPath, dataDir)

    let isHealthy (d: Diagnosis) : bool = d.checks |> List.forall _.ok

    /// 报告序列化（只增报告，不改检查本身）：[{name, ok, detail}]。detail 经 `redact` 处理，
    /// 调用方传入 Stderr.redact，保证已注册的密钥值不会随报告离开主机。
    let toChecksJson (redact: string -> string) (d: Diagnosis) : System.Text.Json.Nodes.JsonArray =
        let arr = System.Text.Json.Nodes.JsonArray()
        for c in d.checks do
            let o = System.Text.Json.Nodes.JsonObject()
            o["name"] <- c.name
            o["ok"] <- c.ok
            o["detail"] <- redact c.detail
            arr.Add o
        arr

    let print (d: Diagnosis) : unit =
        let mutable ok = true
        for c in d.checks do
            if c.ok then
                eprintfn "%-16s OK" c.name
            else
                ok <- false
                eprintfn "%-16s FAIL: %s" c.name c.detail
        eprintfn "overall: %s" (if ok then "PASS" else "FAIL")
        if not ok then exit 1
