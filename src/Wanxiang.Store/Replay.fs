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
    /// 被静默截尾/删除的文件列表（诊断用；stderr 由调用方负责记录）
    truncatedFiles: string list
    /// 最后有效提交 id
    lastCommitId: CommitId
    /// 日志中最后写入的 UTC 日期（无日志时为文件系统 UTC 今天）
    lastDateUtc: DateTime
}

/// 启动 replay：完整重放全部 NDJSON，构建内存投影。
/// 损坏规则（决策 8/10）：JSON 无法解析、id 不连续、缺字段、未知事件类型、事件无法反序列化、
/// 结构性不变量失败、投影失败、日期文件缺失 —— 均视为损坏点，静默截尾（保留最长有效前缀，删除后续文件）。
/// 决策 7：按文件名日期排序逐行重放，检查 id 严格连续，并检测日期文件缺失（防止静默断层）。
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
                Directory.GetFiles(eventsDir, "*.jsonseq")
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

                let processRecord (path: string) (recordNumber: int) (record: string) : bool =
                    // 一条 RFC 7464 记录（已剥去 RS/LF）
                    if String.IsNullOrWhiteSpace record then
                        true
                    else
                        match CommitCodec.tryCommitFromJsonLine record with
                        | None ->
                            currentDamage <-
                                Some(DamageAt(path, 0L, sprintf "record %d: unparseable commit" recordNumber))
                            false
                        | Some commit ->
                            if commit.id <> expectedId then
                                currentDamage <-
                                    Some
                                        (DamageAt(path, 0L, sprintf "record %d: id %d out of order; expected %d" recordNumber commit.id expectedId))
                                false
                            else
                                match Projection.applyCommit projection commit with
                                | Ok p ->
                                    projection <- p
                                    // 前插避免 O(n²)；最终输出时 List.rev 恢复升序
                                    commits <- commit :: commits
                                    expectedId <- expectedId + 1UL
                                    lastDate <- commit.committedAtUtc.UtcDateTime.Date
                                    true
                                | Error err ->
                                    currentDamage <-
                                        Some
                                            (DamageAt(path, 0L, sprintf "record %d: projection failed: %s" recordNumber (WanxiangError.message err)))
                                    false

                // 逐字节扫描单个文件，按 RFC 7464 分帧：记录 = RS(0x1E) + JSON + LF(0x0A)。
                // 保留偏移语义：损坏记录回退到该记录的 RS 起点（= 有效前缀末尾）。
                // 返回 true 表示文件完整处理；false 表示发现损坏。
                let scanFile (name: string) (path: string) : bool =
                    use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    let buf = Array.zeroCreate<byte> (64 * 1024)
                    // 当前记录字节缓冲（RS 到 LF 之间，含 LF；不含 RS）
                    let recBytes = new System.IO.MemoryStream()
                    let mutable recordNumber = 0
                    let mutable bytesRead = 0L
                    let mutable recordStartOffset = 0L
                    let mutable recordOk = true
                    let mutable eof = false
                    let flushRecord (terminated: bool) : bool =
                        // 解码当前记录并交给 processRecord；terminated=false 表示尾部无 LF（截断）
                        let raw = recBytes.ToArray()
                        recBytes.SetLength 0L
                        // 严格 UTF-8 解码（决策 8/10）：非法字节序列视为损坏记录，触发截尾而非静默 U+FFFD 入库
                        let record =
                            try
                                (UTF8Encoding(false, true)).GetString(raw)
                            with :? System.Text.DecoderFallbackException ->
                                currentDamage <-
                                    Some(DamageAt(path, recordStartOffset, sprintf "record %d: invalid UTF-8 sequence" (recordNumber + 1)))
                                ""
                        recordNumber <- recordNumber + 1
                        if currentDamage.IsSome then false
                        else
                            let payload = if terminated && record.EndsWith "\n" then record.TrimEnd '\n' else record
                            processRecord path recordNumber payload
                    while not eof && recordOk do
                        let n = fs.Read(buf, 0, buf.Length)
                        if n = 0 then
                            eof <- true
                            if recBytes.Length > 0L then
                                // 尾部残留无 RS/LF 收尾：按截断记录处理（损坏判定交给 processRecord）
                                if not (flushRecord false) then
                                    bytesRead <- recordStartOffset
                                    recordOk <- false
                        else
                            let mutable i = 0
                            while i < n && recordOk do
                                let b = buf[i]
                                bytesRead <- bytesRead + 1L
                                if b = 30uy (* RS *) then
                                    if recBytes.Length > 0L then
                                        // 上一记录未正常收尾（无 LF）就遇到新 RS：按未终止处理
                                        if not (flushRecord false) then
                                            bytesRead <- recordStartOffset
                                            recordOk <- false
                                    if recordOk then
                                        recordStartOffset <- bytesRead - 1L
                                elif b = 10uy (* LF *) then
                                    if recBytes.Length > 0L then
                                        if flushRecord true then
                                            recordStartOffset <- bytesRead
                                        else
                                            // 损坏记录：回退到该记录 RS 起点（= 有效前缀末尾）
                                            bytesRead <- recordStartOffset
                                            recordOk <- false
                                    // LF 之前的空段（RS 后直接 LF）：RFC 7464 空段忽略
                                else
                                    recBytes.WriteByte b
                                i <- i + 1
                    if not recordOk then
                        // 用 processLine 设置的详细 reason；这里补上精确字节偏移
                        let reason =
                            match currentDamage with
                            | Some (DamageAt(_, _, r)) -> r
                            | None -> sprintf "file %s record %d" name recordNumber
                        currentDamage <- Some(DamageAt(path, bytesRead, reason))
                        stop <- true
                        false
                    else
                        true

                for (name, path) in files do
                    if not stop then
                        match DataPaths.tryParseEventFileName name with
                        | None ->
                            // 文件名非法：视为损坏点（该文件之后的全部丢弃）
                            currentDamage <- Some(DamageAt(path, 0L, sprintf "invalid event file name %s" name))
                            stop <- true
                        | Some _ -> scanFile name path |> ignore

                // 决策 7：文件日期单调性检查——NDJSON 只在"有提交的日子"生成文件，
                // 停机跨天（8-01 用、8-05 再用）是正常场景，中间日期无文件不是损坏。
                // 异常仅限：日期降序（更晚文件日期早于前一个，违反 Q109 不回退）。
                // 真正的文件缺失（中间日志被删）由 id 连续性检测覆盖：id 空洞必然触发截尾。
                if not stop then
                    let dates = files |> List.choose (fun (n, _) -> DataPaths.tryParseEventFileName n)
                    let rec checkDateOrder (ds: DateTime list) =
                        match ds with
                        | d1 :: d2 :: rest when d2 < d1 ->
                            // 降序损坏：d1（更晚日期）已完整重放，d2 是回退点。
                            // 不能按日期 deleteAfter d2（会误删已重放的 d1 及更晚文件）；
                            // 只能截空/删除 d2 自身，其后的 id 连续性由 expectedId 检测兜底。
                            currentDamage <-
                                Some
                                    (DamageAt(files |> List.tryFind (fun (n, _) -> DataPaths.tryParseEventFileName n = Some d2) |> Option.map snd |> Option.defaultValue (DataPaths.eventFilePath dataDir d2),
                                         0L,
                                         sprintf "event file date regression: %s after %s" (DataPaths.eventFileName d2) (DataPaths.eventFileName d1)))
                            false
                        | _ :: rest -> checkDateOrder rest
                        | [] -> true
                    if not (checkDateOrder dates) then stop <- true

                // 处理损坏：截尾当前文件 + 删除后续文件（fix=true），或仅报告（fix=false）
                match currentDamage with
                | None ->
                    Ok { projection = projection
                         commits = List.rev commits
                         truncatedFiles = []
                         lastCommitId = projection.latestCommitId
                         lastDateUtc = lastDate }
                | Some (DamageAt(path, offset, reason)) ->
                    let fileDate = DataPaths.tryParseEventFileName (Path.GetFileName path)
                    let deleteAfter (date: DateTime) =
                        for (name, p) in files do
                            match DataPaths.tryParseEventFileName name with
                            | Some d when d > date ->
                                try
                                    File.Delete p
                                    truncatedPaths <- p :: truncatedPaths
                                with _ -> ()
                            | _ -> ()

                    if fix then
                        // 截尾目标文件：若文件不存在（如日期 gap 指向缺失文件），无需截尾，直接删除更晚文件
                        let truncated =
                            if File.Exists path then
                                try
                                    truncateFile path offset
                                    true
                                with e ->
                                    false
                            else
                                true
                        if truncated then
                            // 降序损坏（date regression）：d1 已完整重放，只删 d2 自身，绝不按日期级联
                            // （deleteAfter 会误删已重放的更晚文件）；其后 id 空洞由 expectedId 兜底。
                            let isDateRegression = reason.StartsWith "event file date regression"
                            match fileDate with
                            | Some d when not isDateRegression -> deleteAfter d
                            | None ->
                                // 非法文件名：按重放顺序（字典序）删除该文件之后的所有文件（决策 9）。
                                // 若不级联，损坏点之后的合法文件保留在磁盘，而 lastCommitId 停在损坏点之前，
                                // 运行期从 lastCommitId+1 重新分配 id 会与保留文件中的旧 id 冲突，
                                // 下次启动重放按 id 连续性截尾 → 运行期写入的新数据被静默丢弃，且 fix 不幂等。
                                let index = files |> List.tryFindIndex (fun (n, _) -> n = Path.GetFileName path)
                                match index with
                                | Some i when i >= 0 ->
                                    for (_, p) in files |> List.skip (i + 1) do
                                        try
                                            File.Delete p
                                            truncatedPaths <- p :: truncatedPaths
                                        with _ -> ()
                                | _ -> ()
                            | Some _ -> ()
                            // 非法文件名（无日期）或降序损坏点，且 offset=0（无可保留内容）：删除文件自身，
                            // 避免每次启动 fix 重复命中并写 stderr（合法日期文件的截空行为不受影响）
                            if offset = 0L && (fileDate.IsNone || isDateRegression) then
                                // 先记录再删除（诊断告知 fix 处理了该文件）
                                if File.Exists path then truncatedPaths <- path :: truncatedPaths
                                try
                                    File.Delete path
                                with _ -> ()
                            elif File.Exists path then
                                truncatedPaths <- path :: truncatedPaths
                            Ok { projection = projection
                                 commits = List.rev commits
                                 truncatedFiles = truncatedPaths
                                 lastCommitId = projection.latestCommitId
                                 lastDateUtc = lastDate }
                        else
                            Error(sprintf "damaged log at %s (%s) and truncation failed" path reason)
                    else
                        Error(sprintf "damaged log at %s (%s); fix not enabled" path reason)
