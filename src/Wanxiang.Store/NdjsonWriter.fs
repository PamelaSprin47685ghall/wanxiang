namespace Wanxiang.Store

open System
open System.IO
open System.Text
open Wanxiang.Core

/// NDJSON 写入器：按 UTC 日期分文件，append + 用户态 flush（不 fsync，UPS 兜底）。
/// 单写者：所有写操作由 Commit Coordinator 串行调用。
/// 故障语义（决策 40）：Write/Flush 失败必须回滚到写前偏移并抛出，
/// 由协调器按"截尾 + 复用 id"处理，不允许残留半行或未 flush 的整行。
type NdjsonWriter(dataDir: string, initialDateUtc: DateTime) =

    let mutable stream: FileStream = null
    let mutable currentDate: DateTime = initialDateUtc.Date

    let openFile (date: DateTime) : FileStream =
        let path = DataPaths.eventFilePath dataDir date
        let fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
        // Q118：日志文件最小用户权限（仅当前用户可读写）
        try File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
        fs

    do
        stream <- openFile currentDate

    /// 当前文件字节长度（用于截尾）。
    member _.Position : int64 = stream.Length

    /// 写入一条提交（完整一行 + 换行）并 flush。
    /// 返回写前偏移，供运行时截尾复用 id。
    /// 换日语义（决策 109）：日志日期不得回退——时钟回拨时保持最后持久化日期文件。
    member _.AppendCommit(commit: Events.Commit) : int64 =
        let nowUtc = commit.committedAtUtc.UtcDateTime.Date
        if nowUtc > currentDate then
            // 先打开新文件成功，再释放旧流；打开失败时保持旧流可用，避免当日全部写入不可恢复
            let newStream = openFile nowUtc
            let oldStream = stream
            stream <- newStream
            currentDate <- nowUtc
            oldStream.Dispose()
        let offsetBefore = stream.Length
        let line = CommitCodec.commitToJsonLine commit + "\n"
        let bytes = Encoding.UTF8.GetBytes(line)
        try
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush()
            offsetBefore
        with e ->
            // 失败：回滚到写前偏移，保证不残留半行；随后抛出由协调器按截尾复用 id 处理
            try
                stream.SetLength offsetBefore
                stream.Flush()
            with _ -> ()
            raise e

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
