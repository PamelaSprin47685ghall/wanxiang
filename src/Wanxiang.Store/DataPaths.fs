namespace Wanxiang.Store

open System
open System.IO

/// 数据目录布局：
///   data/
///     events/yyyy-MM-dd.ndjson     事件日志（按 UTC 日期分文件）
///     attachments/                 附件内容寻址存储
///     lock                         实例独占锁
module DataPaths =

    let eventsDir (dataDir: string) = Path.Combine(dataDir, "events")
    let attachmentsDir (dataDir: string) = Path.Combine(dataDir, "attachments")
    let lockFile (dataDir: string) = Path.Combine(dataDir, "lock")

    let eventFileName (dateUtc: DateTime) = sprintf "%04d-%02d-%02d.ndjson" dateUtc.Year dateUtc.Month dateUtc.Day

    let eventFilePath (dataDir: string) (dateUtc: DateTime) = Path.Combine(eventsDir dataDir, eventFileName dateUtc)

    let ensureDataDirs (dataDir: string) : unit =
        Directory.CreateDirectory(eventsDir dataDir) |> ignore
        Directory.CreateDirectory(attachmentsDir dataDir) |> ignore
        // Q118：事件日志与附件目录采用最小用户权限（仅当前用户可读写执行），
        // 不依赖 umask——默认 0755 会让组/其他用户读到含消息正文的事件日志。
        for dir in [ eventsDir dataDir; attachmentsDir dataDir ] do
            try
                File.SetUnixFileMode(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            with _ -> ()

    /// 解析事件日志文件名，返回 UTC 日期；命名非法返回 None。
    let tryParseEventFileName (fileName: string) : DateTime option =
        if not (fileName.EndsWith ".ndjson") then None
        else
            let stem = fileName.Substring(0, fileName.Length - ".ndjson".Length)
            match DateTime.TryParseExact(stem, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
            | true, d -> Some d
            | _ -> None
