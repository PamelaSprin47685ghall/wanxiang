namespace Wanxiang.Store

open System.IO

/// 数据目录布局（只是路径约定；机制都在 wanxiang2 的库里）：
///   data/
///     ledger/log.jsonseq           事件账本（RFC 7464，单文件，不再按日轮转）
///     snapshots/ledger.snapshot    账本续播基线（缓存，可删）
///     attachments/                 附件内容寻址存储
///     recovery/                    修复前的证据包
///     .lock                        实例独占锁（DirLock）
///     events/                      旧版日志目录：新版不读不写，doctor 会提示
module DataPaths =

    let eventsDir (dataDir: string) = Path.Combine(dataDir, "ledger")
    let logFile (dataDir: string) = Path.Combine(eventsDir dataDir, "log.jsonseq")
    let snapshotsDir (dataDir: string) = Path.Combine(dataDir, "snapshots")
    let snapshotFile (dataDir: string) = Path.Combine(snapshotsDir dataDir, "ledger.snapshot")
    let attachmentsDir (dataDir: string) = Path.Combine(dataDir, "attachments")
    let lockFile (dataDir: string) = Path.Combine(dataDir, ".lock")

    /// 旧版（按日分文件）日志目录。格式不兼容，新版不读；只用于诊断提示。
    let legacyEventsDir (dataDir: string) = Path.Combine(dataDir, "events")

    let ensureDataDirs (dataDir: string) : unit =
        // Q118：日志、快照与附件目录采用最小用户权限（仅当前用户），不依赖 umask。
        for dir in [ eventsDir dataDir; snapshotsDir dataDir; attachmentsDir dataDir ] do
            Directory.CreateDirectory dir |> ignore
            try
                File.SetUnixFileMode(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            with _ -> ()
