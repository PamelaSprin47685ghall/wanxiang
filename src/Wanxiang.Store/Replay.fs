namespace Wanxiang.Store

open System
open System.IO
open System.Text
open Wanxiang.Core

/// 启动 replay 结果（只读检查与修复共用）。
type ReplayOutcome = {
    projection: Projection
    /// 重放读取到的所有有效提交，按全局 id 升序保存，供连接 catch-up 读取。
    commits: Events.Commit list
    /// 被修复（截尾/删除）的文件列表（诊断用；stderr 由调用方负责记录）。
    /// 只读检查（Inspect）恒为空。
    truncatedFiles: string list
    /// 最后有效提交 id
    lastCommitId: CommitId
    /// 日志中最后写入的 UTC 日期（无日志时为文件系统 UTC 今天）
    lastDateUtc: DateTime
}

/// 日志损坏点（只读检查产物；不携带修改动作）。
type Damage = {
    /// 损坏所在文件完整路径
    file: string
    /// 损坏字节偏移（有效前缀末尾；文件内）
    byteOffset: int64
    reason: string
}

/// 修复计划：只读检查发现的损坏点 + 建议动作。
/// 由 `Repair.repair` 消费；`RepairRequired` 分支不得自行修改任何文件。
type RepairPlan = {
    damage: Damage
    /// 修复会修改/删除的全部文件的完整路径（供修复前备份证据）。
    affectedFiles: string list
}

/// 只读检查结论（算法 R：Read-Only Recovery）。
/// Inspect 绝不修改、绝不删除任何文件。
type InspectResult =
    /// 日志健康：可直接启动。
    | Healthy of ReplayOutcome
    /// 发现损坏：需要显式修复（doctor --fix）后才能正常写入。
    /// outcome 是**最长有效前缀**的投影；启动方必须进入只读降级模式。
    | RepairRequired of ReplayOutcome * RepairPlan
    /// 未知/不支持的格式版本：拒绝启动写服务，保留所有数据。
    | UnsupportedFormat of string
    /// 数据目录不可读（权限/IO 等阻断性问题）：拒绝启动写服务，保留所有数据。
    | Unreadable of string

/// 启动 replay：完整重放全部 .jsonseq，构建内存投影。
/// 损坏规则（决策 8/10）：JSON 无法解析、id 不连续、缺字段、未知事件类型、事件无法反序列化、
/// 结构性不变量失败、投影失败、日期文件缺失 —— 均视为损坏点。
/// 决策 7：按文件名日期排序逐行重放，检查 id 严格连续，并检测日期文件缺失（防止静默断层）。
///
/// 整改（S-001，2026-10-10）：**检查与修复分离**。
/// - `Replay.inspect`：只读。发现损坏返回 `RepairRequired`，不修改文件。
/// - `Repair.repair`：显式修复（doctor --fix 持锁后调用）。修复前由调用方建立证据包。
/// - 正常启动**不再**自动截尾/删除；损坏时进入只读降级模式（STOP-1）。
module Replay =

    /// 快照续播基线（算法 L7）：投影 + 已重放到的提交水位 + 锚点文件/偏移。
    type SnapshotBase = {
        projection: Projection
        throughCommitId: CommitId
        throughFile: string option
        throughOffset: int64
        /// 快照固化的全部提交外壳（v2，按 id 升序）；None = v1 旧快照，
        /// inspectFrom 回退 collect-only 外壳收集（扫描锚点前日志）。
        commits: Events.Commit list option
    }

    /// 重放数据目录中的全部事件日志（只读，算法 R 的 R1–R12）。
    /// 不创建目录（空数据目录由协调器/写入路径负责）；不修改任何文件。
    /// 返回 Healthy 或 RepairRequired（携带最长有效前缀投影与修复计划）。
    /// `base_`：快照启动（算法 L7）——以快照投影为基线，从 throughCommitId+1 续播；
    /// None = 完整重放（expectedId 从 1 起）。
    let inspectFrom (dataDir: string) (base_: SnapshotBase) : InspectResult =
        let startFrom = Some base_
        let baseProjection, baseCommitId, baseFile, baseOffset, baseCommits =
            match startFrom with
            | Some b -> b.projection, b.throughCommitId, b.throughFile, b.throughOffset, b.commits
            | None -> Projection.empty, 0UL, None, 0L, None
        let eventsDir = DataPaths.eventsDir dataDir
        if not (Directory.Exists eventsDir) then
            Healthy { projection = baseProjection
                      commits = []
                      truncatedFiles = []
                      lastCommitId = baseCommitId
                      lastDateUtc =
                        match startFrom with
                        | Some _ -> DateTime.UtcNow.Date
                        | None -> DateTime.UtcNow.Date }
        else
            let filesResult =
                try
                    Directory.GetFiles(eventsDir, "*.jsonseq")
                    |> Array.map (fun p -> Path.GetFileName p, p)
                    |> Array.sortBy fst
                    |> Array.toList
                    |> Ok
                with e -> Error(sprintf "cannot list %s: %s" eventsDir e.Message)

            match filesResult with
            | Error msg -> Unreadable msg
            | Ok [] ->
                Healthy { projection = baseProjection
                          commits = []
                          truncatedFiles = []
                          lastCommitId = baseCommitId
                          lastDateUtc = DateTime.UtcNow.Date }
            | Ok files ->
                let mutable projection = baseProjection
                // v2 快照固化的提交外壳直接作为初始列表（按 id 升序）；
                // v1 旧快照（None）→ 空列表，由 collect-only 扫描补全锚点前历史。
                let mutable commits: Events.Commit list = baseCommits |> Option.defaultValue []
                let mutable expectedId = baseCommitId + 1UL
                // 快照续播（算法 L7）时，锚点之前的外壳收集用独立计数器（从 1 起），
                // 校验锚点前日志 id 连续；收集结束时必须等于 baseCommitId+1（与续播衔接）。
                let mutable shellExpectedId = 1UL
                let mutable lastDate = DateTime.UtcNow.Date
                let mutable stop = false
                let mutable currentDamage: Damage option = None
                // 场景 F（N-1）：未来格式版本 → UnsupportedFormat（拒绝启动写服务，保留数据）
                let mutable futureFormat: string option = None

                /// 处理一条记录。collectOnly=true（快照续播锚点前外壳收集）时：
                /// 只解码 + id 连续性校验 + 收集 commit 外壳，**不 apply projection**——
                /// 投影构建成本保持 O(快照后增量)，但 commits 覆盖全历史，
                /// 保证 catch-up 任意游标可用（§22 资源预算：CommitCoordinator.commits 全量保留）。
                /// 锚点之前的日志在快照创建时已健康且单写者只追加，损坏检测聚焦锚点之后。
                let processRecord (path: string) (recordNumber: int) (record: string) (collectOnly: bool) : bool =
                    // 一条 RFC 7464 记录（已剥去 RS/LF）
                    if String.IsNullOrWhiteSpace record then
                        true
                    else
                        // 未来格式版本：外壳可识别但版本未知——绝不截尾（R3：不猜格式）。
                        // 与「损坏」区分：损坏是当前格式内的非法内容（可修复），
                        // 未来版本是当前实现无法理解的格式（必须保留数据并拒绝启动）。
                        let shellVersion = CommitCodec.tryShellFormatVersion record
                        let isFuture =
                            match shellVersion with
                            | Some v when v <> Constants.FormatVersion -> true
                            | _ -> false
                        if isFuture then
                            futureFormat <-
                                Some(
                                    sprintf
                                        "record %d in %s: formatVersion %d not supported (expected %d)"
                                        recordNumber
                                        path
                                        shellVersion.Value
                                        Constants.FormatVersion)
                            false
                        else
                        match CommitCodec.tryCommitFromJsonLine record with
                        | None ->
                            currentDamage <-
                                Some { file = path
                                       byteOffset = 0L
                                       reason = sprintf "record %d: unparseable commit" recordNumber }
                            false
                        | Some commit ->
                            if collectOnly && commit.id > baseCommitId then
                                // 外壳收集阶段越过锚点（续播区）：跳过，不校验、不收集
                                //（续播阶段负责解码/校验/收集，避免重复）。
                                true
                            else
                                let expected = if collectOnly then shellExpectedId else expectedId
                                if commit.id <> expected then
                                    currentDamage <-
                                        Some { file = path
                                               byteOffset = 0L
                                               reason = sprintf "record %d: id %d out of order; expected %d" recordNumber commit.id expected }
                                    false
                                else
                                    if collectOnly then
                                        // 外壳收集：不 apply。收集锚点（throughCommitId）之内的外壳。
                                        commits <- commit :: commits
                                        shellExpectedId <- shellExpectedId + 1UL
                                        lastDate <- commit.committedAtUtc.UtcDateTime.Date
                                        true
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
                                                Some { file = path
                                                       byteOffset = 0L
                                                       reason = sprintf "record %d: projection failed: %s" recordNumber (WanxiangError.message err) }
                                            false

                // 逐字节扫描单个文件，按 RFC 7464 分帧：记录 = RS(0x1E) + JSON + LF(0x0A)。
                // 保留偏移语义：损坏记录回退到该记录的 RS 起点（= 有效前缀末尾）。
                // 返回 true 表示文件完整处理；false 表示发现损坏。
                // startOffset > 0 表示快照续播（算法 L7）：从该字节起扫描，跳过已折叠进快照的前缀。
                let scanFileFrom (name: string) (path: string) (startOffset: int64) (collectOnly: bool) : bool =
                    use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    let buf = Array.zeroCreate<byte> (64 * 1024)
                    // 当前记录字节缓冲（RS 到 LF 之间，含 LF；不含 RS）
                    let recBytes = new System.IO.MemoryStream()
                    // 快照续播：recordNumber/bytesRead 从锚点起算（诊断偏移相对锚点）
                    let mutable recordNumber = 0
                    let mutable bytesRead = startOffset
                    let mutable recordStartOffset = startOffset
                    let mutable recordOk = true
                    let mutable eof = false
                    if startOffset > 0L then
                        fs.Seek(startOffset, SeekOrigin.Begin) |> ignore
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
                                    Some { file = path
                                           byteOffset = recordStartOffset
                                           reason = sprintf "record %d: invalid UTF-8 sequence" (recordNumber + 1) }
                                ""
                        recordNumber <- recordNumber + 1
                        if currentDamage.IsSome then false
                        else
                            let payload = if terminated && record.EndsWith "\n" then record.TrimEnd '\n' else record
                            processRecord path recordNumber payload collectOnly
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
                            | Some d -> d.reason
                            | None -> sprintf "file %s record %d" name recordNumber
                        currentDamage <- Some { file = path; byteOffset = bytesRead; reason = reason }
                        stop <- true
                        false
                    else
                        true

                // 快照启动（算法 L7）：跳过 throughFile 及其之前的文件（已折叠进快照投影），
                // 从 throughFile 的 throughOffset 字节续播。throughFile 为 None（快照无文件锚点）
                // 时从第一个文件开始，但 expectedId 已从 throughCommitId+1 起，id 连续性仍受校验。
                let skipBeforeFile =
                    match baseFile with
                    | Some f ->
                        match DataPaths.tryParseEventFileName f with
                        | Some d -> Some d
                        | None -> None
                    | None -> None
                let resumeFile = baseFile
                let resumeOffset = baseOffset

                let scanFile (name: string) (path: string) : bool = scanFileFrom name path 0L false

                for (name, path) in files do
                    if not stop then
                        match DataPaths.tryParseEventFileName name with
                        | None ->
                            // 文件名非法：视为损坏点（该文件之后的全部丢弃）
                            currentDamage <- Some { file = path; byteOffset = 0L; reason = sprintf "invalid event file name %s" name }
                            stop <- true
                        | Some _ ->
                            // 快照锚点之前的文件：仅 v1 旧快照（commits 未固化）需要
                            // collect-only 外壳收集回退——不 apply projection，只解码 +
                            // 校验 id 连续 + 收集 commit 外壳，使 commits 覆盖全历史
                            //（catch-up 任意游标可用）。v2 快照 commits 已固化，直接跳过。
                            let skipFile =
                                match skipBeforeFile, DataPaths.tryParseEventFileName name with
                                | Some anchor, Some thisDate -> thisDate < anchor
                                | _ -> false
                            if skipFile then
                                match baseCommits with
                                | Some _ -> () // v2：commits 已固化在快照，跳过
                                | None ->
                                    try
                                        scanFileFrom name path 0L true |> ignore
                                    with e ->
                                        currentDamage <- Some { file = path; byteOffset = 0L; reason = sprintf "cannot read file: %s" e.Message }
                                        stop <- true
                            else
                                let isResume =
                                    match resumeFile with
                                    | Some rf -> name = rf
                                    | None -> false
                                try
                                    if isResume && resumeOffset > 0L then
                                        // 锚点文件：v1 旧快照先 collect-only 收集
                                        // [0, throughOffset) 段外壳（同文件内已折叠进快照的提交），
                                        // 再从 throughOffset 续播（算法 L7）。
                                        match baseCommits with
                                        | Some _ -> ()
                                        | None -> scanFileFrom name path 0L true |> ignore
                                        scanFileFrom name path resumeOffset false |> ignore
                                    else
                                        scanFile name path |> ignore
                                with e ->
                                    currentDamage <- Some { file = path; byteOffset = 0L; reason = sprintf "cannot read file: %s" e.Message }
                                    stop <- true

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
                            // 不能按日期 deleteAfter d2（会误删已重放的 d1 及更晚文件）。
                            currentDamage <-
                                Some { file = files |> List.tryFind (fun (n, _) -> DataPaths.tryParseEventFileName n = Some d2) |> Option.map snd |> Option.defaultValue (DataPaths.eventFilePath dataDir d2)
                                       byteOffset = 0L
                                       reason = sprintf "event file date regression: %s after %s" (DataPaths.eventFileName d2) (DataPaths.eventFileName d1) }
                            false
                        | _ :: rest -> checkDateOrder rest
                        | [] -> true
                    if not (checkDateOrder dates) then stop <- true

                // 场景 F：未来格式版本优先于损坏报告——绝不截尾未知格式（R3）。
                match futureFormat with
                | Some msg -> UnsupportedFormat msg
                | None ->
                match currentDamage with
                | None ->
                    Healthy { projection = projection
                              commits = List.rev commits
                              truncatedFiles = []
                              lastCommitId = projection.latestCommitId
                              lastDateUtc = lastDate }
                | Some damage ->
                    // 计算修复会触碰的全部文件（截尾目标 + 其后按日期/字典序级联删除的候选），
                    // 供修复前建立证据包（算法 B）。只读阶段不执行任何动作。
                    let affected =
                        let fileDate = DataPaths.tryParseEventFileName (Path.GetFileName damage.file)
                        let isDateRegression = damage.reason.StartsWith "event file date regression"
                        match fileDate with
                        | Some d when not isDateRegression ->
                            damage.file
                            :: (files
                                |> List.choose (fun (n, p) ->
                                    match DataPaths.tryParseEventFileName n with
                                    | Some dd when dd > d -> Some p
                                    | _ -> None))
                        | _ ->
                            // 非法文件名或降序损坏点：字典序在该文件之后的全部文件
                            let index = files |> List.tryFindIndex (fun (n, _) -> n = Path.GetFileName damage.file)
                            match index with
                            | Some i when i >= 0 -> damage.file :: (files |> List.skip (i + 1) |> List.map snd)
                            | _ -> [ damage.file ]
                    RepairRequired(
                        { projection = projection
                          commits = List.rev commits
                          truncatedFiles = []
                          lastCommitId = projection.latestCommitId
                          lastDateUtc = lastDate },
                        { damage = damage
                          affectedFiles = affected }
                    )

    /// 完整重放（无快照基线）：expectedId 从 1 起，重放全部日志。
    /// 等价于 `inspectFrom dataDir { projection = Projection.empty; throughCommitId = 0UL; throughFile = None; throughOffset = 0L }`。
    let inspect (dataDir: string) : InspectResult =
        inspectFrom
            dataDir
            { projection = Projection.empty
              throughCommitId = 0UL
              throughFile = None
              throughOffset = 0L
              commits = None }

/// 显式修复（算法：截尾 + 级联删除）。**必须**在持锁后调用，且修复前应已建立证据包。
/// 本模块只执行确定性修复（决策 51/120）：截断损坏点、移除更晚日志内容。
module Repair =

    let private truncateFile (path: string) (offset: int64) : unit =
        use fs = new FileStream(path, FileMode.Open, FileAccess.Write)
        fs.SetLength offset

    /// 执行修复计划：截尾损坏文件 + 删除损坏点之后的所有文件。
    /// 返回被修改（截尾/删除）的文件完整路径列表（供 stderr 忠实记录）。
    /// 任一截尾失败即返回 Error（不部分成功静默继续）。
    let repair (dataDir: string) (plan: RepairPlan) : Result<string list, string> =
        let files =
            try
                Directory.GetFiles(DataPaths.eventsDir dataDir, "*.jsonseq")
                |> Array.map (fun p -> Path.GetFileName p, p)
                |> Array.sortBy fst
                |> Array.toList
            with e -> raise (IOException(sprintf "cannot list events dir: %s" e.Message))
        let damage = plan.damage
        let path = damage.file
        let fileDate = DataPaths.tryParseEventFileName (Path.GetFileName path)
        let isDateRegression = damage.reason.StartsWith "event file date regression"
        let truncatedPaths = System.Collections.Generic.List<string>()
        let mutable deleteError: string option = None

        // 截尾目标文件：若文件不存在（如日期 gap 指向缺失文件），无需截尾，直接删除更晚文件
        let truncated =
            if File.Exists path then
                try
                    truncateFile path damage.byteOffset
                    true
                with e ->
                    deleteError <- Some(sprintf "cannot truncate %s at offset %d: %s" path damage.byteOffset e.Message)
                    false
            else
                true

        if truncated then
            // 降序损坏（date regression）：d1 已完整重放，只删 d2 自身，绝不按日期级联
            // （deleteAfter 会误删已重放的更晚文件）；其后 id 空洞由 expectedId 兜底。
            match fileDate with
            | Some d when not isDateRegression ->
                for (_, p) in files do
                    match DataPaths.tryParseEventFileName (Path.GetFileName p) with
                    | Some dd when dd > d ->
                        if deleteError.IsNone then
                            try
                                File.Delete p
                                truncatedPaths.Add p
                            with e ->
                                deleteError <- Some(sprintf "cannot delete %s after damage point: %s" p e.Message)
                    | _ -> ()
            | None ->
                // 非法文件名：按重放顺序（字典序）删除该文件之后的所有文件（决策 9）。
                // 若不级联，损坏点之后的合法文件保留在磁盘，而 lastCommitId 停在损坏点之前，
                // 运行期从 lastCommitId+1 重新分配 id 会与保留文件中的旧 id 冲突，
                // 下次启动重放按 id 连续性截尾 → 运行期写入的新数据被静默丢弃，且 fix 不幂等。
                let index = files |> List.tryFindIndex (fun (n, _) -> n = Path.GetFileName path)
                match index with
                | Some i when i >= 0 ->
                    for (_, p) in files |> List.skip (i + 1) do
                        if deleteError.IsNone then
                            try
                                File.Delete p
                                truncatedPaths.Add p
                            with e ->
                                deleteError <- Some(sprintf "cannot delete %s (after invalid file name): %s" p e.Message)
                | _ -> ()
            | Some _ -> ()
            // 非法文件名（无日期）或降序损坏点，且 offset=0（无可保留内容）：删除文件自身，
            // 避免每次启动 fix 重复命中并写 stderr（合法日期文件的截空行为不受影响）
            if damage.byteOffset = 0L && (fileDate.IsNone || isDateRegression) then
                // 先记录再删除（诊断告知 fix 处理了该文件）
                if File.Exists path then truncatedPaths.Add path
                if deleteError.IsNone then
                    try
                        File.Delete path
                    with e ->
                        deleteError <- Some(sprintf "cannot delete damaged file %s: %s" path e.Message)
            elif File.Exists path then
                truncatedPaths.Add path
        match deleteError with
        | Some err -> Error(err)
        | None -> Ok(List.ofSeq truncatedPaths)
