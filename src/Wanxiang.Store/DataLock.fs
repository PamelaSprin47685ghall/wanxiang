namespace Wanxiang.Store

open System
open System.IO

/// 数据目录独占锁：同一数据目录只允许一个 S 进程打开（严格单进程）。
/// 获取失败即启动失败，不尝试多写者协调。
type DataLock private (stream: FileStream) =

    static member Acquire(dataDir: string) : Result<DataLock, string> =
        DataPaths.ensureDataDirs dataDir
        let path = DataPaths.lockFile dataDir
        try
            let fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            // 写入 PID 便于诊断
            try
                fs.SetLength 0L
                let bytes = Text.Encoding.UTF8.GetBytes(sprintf "pid=%d\n" Environment.ProcessId)
                fs.Write(bytes, 0, bytes.Length)
                fs.Flush()
            with _ -> ()
            Ok(new DataLock(fs))        with :? IOException ->
            Error(sprintf "data directory %s is locked by another process" dataDir)

    member _.Dispose() = stream.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()
