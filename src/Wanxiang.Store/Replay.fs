namespace Wanxiang.Store

open System
open System.IO
open System.Text
open Wanxiang.Core

/// 启动 replay 结果。
type ReplayOutcome = {
    projection: Projection
    /// 启动 replay 读取到的所有有效提交，按全局 id 升序保存，供连接 catch-up 读取。
    commits: Events.Commit list
    /// 被静默截尾的文件列表（诊断用；stderr 由调用方负责记录）
    truncatedFiles: string list
    /// 最后有效提交 id
    lastCommitId: CommitId
    /// 日志中最后写入的 UTC 日期（无日志时为文件系统 UTC 今天）
    lastDateUtc: DateTime
}

/// 启动 replay：完整重放全部 NDJSON，构建内存投影。
/// 损坏规则（决策 10）：JSON 无法解析、id 不连续、缺字段、未知事件类型、事件无法反序列化、
/// 结构性不变量失败、投影失败 —— 均视为损坏点，静默截尾（保留最长有效前缀，删除后续文件）。
module Replay =

    type private Damage =
        | DamageAt of file: string * byteOffset: int64 * reason: string

    let private truncateFile (path: string) (offset: int64) : unit =
        use fs = new FileStream(path, FileMode.Open, FileAccess.Write)
        fs.SetLength offset

    /// 重放数据目录中的全部事件日志。
    /// fix=false（doctor 只读）：不修改文件，仅在发现损坏时报告第一个损坏点。
    let replay (dataDir: string) (fix: bool) : Result<ReplayOutcome, string> =
        let eventsDir = DataPaths.eventsDir dataDir
        // 空数据目录：确保目录结构存在（协调器随后会写入）
        Directory.CreateDirectory eventsDir |> ignore
        if not (Directory.Exists eventsDir) then
            Ok { projection = Projection.empty
                 commits = []
                 truncatedFiles = []
                 lastCommitId = 0UL
                 lastDateUtc = DateTime.UtcNow.Date }
        else
            let files =
                Directory.GetFiles(eventsDir, "*.ndjson")
                |> Array.map (fun p -> Path.GetFileName p, p)
                |> Array.sortBy fst
                |> Array.toList

            if List.isEmpty files then
                Ok { projection = Projection.empty
                     commits = []
                     truncatedFiles = []
                     lastCommitId = 0UL
                     lastDateUtc = DateTime.UtcNow.Date }
            else
                let mutable projection = Projection.empty
                let mutable commits: Events.Commit list = []
                let mutable expectedId = 1UL
                let mutable lastDate = DateTime.UtcNow.Date
                let mutable truncatedPaths: string list = []
                let mutable stop = false
                let mutable currentDamage: Damage option = None

                let processLine (path: string) (lineNumber: int) (line: string) : bool =
                    // 行内可能含尾部 \r（跨平台文件）——NDJSON 以 \n 结尾
                    let line = line.TrimEnd('\r', '\n')
                    if String.IsNullOrWhiteSpace line then
                        true
                    else
                        match CommitCodec.tryCommitFromJsonLine line with
                        | None ->
                            currentDamage <-
                                Some(DamageAt(path, 0L, sprintf "line %d: unparseable commit" lineNumber))
                            false
                        | Some commit ->
                            if commit.id <> expectedId then
                                currentDamage <-
                                    Some
                                        (DamageAt(path, 0L, sprintf "line %d: id %d out of order; expected %d" lineNumber commit.id expectedId))
                                false
                            else
                                match Projection.applyCommit projection commit with
                                | Ok p ->
                                    projection <- p
                                    commits <- commits @ [ commit ]
                                    expectedId <- expectedId + 1UL
                                    lastDate <- commit.committedAtUtc.UtcDateTime.Date
                                    true
                                | Error err ->
                                    currentDamage <-
                                        Some
                                            (DamageAt(path, 0L, sprintf "line %d: projection failed: %s" lineNumber (WanxiangError.message err)))
                                    false

                for (name, path) in files do
                    if not stop then
                        match DataPaths.tryParseEventFileName name with
                        | None ->
                            // 文件名非法：视为损坏点（该文件之后的全部丢弃）
                            currentDamage <- Some(DamageAt(path, 0L, sprintf "invalid event file name %s" name))
                            stop <- true
                        | Some _ ->
                            use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                            use reader = new StreamReader(fs, Encoding.UTF8)
                            let mutable lineNumber = 0
                            let mutable line = reader.ReadLine()
                            let mutable lineOk = true
                            // 逐行记录字节偏移以支持精确截尾
                            let mutable bytesRead = 0L
                            while not (isNull line) && lineOk && not stop do
                                lineNumber <- lineNumber + 1
                                let lineBytes = int64 (Encoding.UTF8.GetByteCount line) + 1L (* \n *)
                                if processLine path lineNumber line then
                                    bytesRead <- bytesRead + lineBytes
                                    line <- reader.ReadLine()
                                else
                                    lineOk <- false
                            if not lineOk then
                                currentDamage <- Some(DamageAt(path, bytesRead, sprintf "file %s line %d" name lineNumber))
                                stop <- true

                // 处理损坏：截尾当前文件 + 删除后续文件（fix=true），或仅报告（fix=false）
                match currentDamage with
                | None ->
                    Ok { projection = projection
                         commits = commits
                         truncatedFiles = []
                         lastCommitId = projection.latestCommitId
                         lastDateUtc = lastDate }
                | Some (DamageAt(path, offset, reason)) ->
                    let fileDate = DataPaths.tryParseEventFileName (Path.GetFileName path)
                    let deleteAfter (date: DateTime) =
                        for (name, p) in files do
                            match DataPaths.tryParseEventFileName name with
                            | Some d when d > date -> File.Delete p
                            | _ -> ()

                    if fix then
                        let truncated =
                            try
                                truncateFile path offset
                                true
                            with e ->
                                false
                        if truncated then
                            match fileDate with
                            | Some d -> deleteAfter d
                            | None -> ()
                            truncatedPaths <- [ path ]
                            Ok { projection = projection
                                 commits = commits
                                 truncatedFiles = truncatedPaths
                                 lastCommitId = projection.latestCommitId
                                 lastDateUtc = lastDate }
                        else
                            Error(sprintf "damaged log at %s (%s) and truncation failed" path reason)
                    else
                        Error(sprintf "damaged log at %s (%s); fix not enabled" path reason)
