namespace Wanxiang.Store

open System

/// 数据目录独占锁：同一数据目录只允许一个 S 进程打开（严格单进程）。
/// 获取失败即启动失败，不尝试多写者协调。机制由 wanxiang2 DirLock 库提供。
type DataLock private (inner: DirLock.DirLock) =

    /// 非致命诊断。DirLock 自己写 pid 旁注，这里恒为 None；保留字段让调用方不变。
    member val PidWarning: string option = None with get, set

    static member Acquire(dataDir: string) : Result<DataLock, string> =
        DataPaths.ensureDataDirs dataDir
        DirLock.DirLock.TryAcquire dataDir |> Result.map (fun l -> new DataLock(l))

    member _.Dispose() = (inner :> IDisposable).Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()
