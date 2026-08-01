namespace Wanxiang.Store

open System
open System.IO
open System.Text
open Wanxiang.Core

/// NDJSON 写入器：按 UTC 日期分文件，append + 用户态 flush（不 fsync，UPS 兜底）。
/// 单写者：所有写操作由 Commit Coordinator 串行调用。
type NdjsonWriter(dataDir: string, initialDateUtc: DateTime) =

    let mutable stream: FileStream = null
    let mutable currentDate: DateTime = initialDateUtc.Date

    let openFile (date: DateTime) : FileStream =
        let path = DataPaths.eventFilePath dataDir date
        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)

    do
        stream <- openFile currentDate

    /// 当前文件字节长度（用于截尾）。
    member _.Position : int64 = stream.Length

    /// 写入一条提交（完整一行 + 换行）并 flush。
    /// 返回写前偏移，供运行时截尾复用 id。
    member _.AppendCommit(commit: Events.Commit) : int64 =
        let nowUtc = commit.committedAtUtc.UtcDateTime.Date
        // 决策 109：日志日期不得回退（时钟回拨时保持最后持久化日期）
        if nowUtc > currentDate then
            stream.Dispose()
            currentDate <- nowUtc
            stream <- openFile currentDate
        elif nowUtc < currentDate then
            // 时钟回拨：继续写入当前（较新的）日期文件，不打开更早文件
            ()
        let offsetBefore = stream.Length
        let line = CommitCodec.commitToJsonLine commit + "\n"
        let bytes = Encoding.UTF8.GetBytes(line)
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush()
        offsetBefore

    /// 运行时截尾：将日志截断到指定偏移（删除失败的尾提交）。
    /// 只在没有新写入发生的情况下由单写者调用。
    member _.TruncateTo(offset: int64) : unit =
        stream.SetLength offset
        stream.Flush()

    member _.Dispose() =
        if not (isNull stream) then
            stream.Dispose()
            stream <- null

    interface IDisposable with
        member this.Dispose() = this.Dispose()
