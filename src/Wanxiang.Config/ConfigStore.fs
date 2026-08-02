namespace Wanxiang.Config

open System
open System.IO
open System.Text
open System.Threading

/// 配置存储：TOML 是唯一权威来源（决策 41/43）。
/// - 只有完整有效的新配置才替换内存配置；
/// - 应用自身修改也必须先落盘再重新加载（单一路径）；
/// - 文件变化后 debounce 100ms 重新读取；
/// - 写入采用临时文件 + flush + 原子 rename。
type ConfigStore private (path: string, initial: AppConfig, onReloaded: AppConfig -> unit, onRejected: string -> unit) as this =

    let lockObj = obj()
    let mutable current: AppConfig = initial
    let mutable disposed = false
    let watcher = new FileSystemWatcher(Path.GetDirectoryName path, Path.GetFileName path)

    let reloadFromDisk () : Result<AppConfig, string list> =
        let readResult =
            try
                let text = File.ReadAllText path
                TomlCodec.tryParse text
            with e ->
                Error [ sprintf "read failed: %s" e.Message ]
        match readResult with
        | Ok cfg ->
            lock lockObj (fun () -> current <- cfg)
            onReloaded cfg
            Ok cfg
        | Error errs ->
            // 继续使用最后一次有效配置；stderr 由调用方记录
            onRejected(String.concat "; " errs)
            Error errs

    let mutable debounceTimer: Timer = null

    do
        watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName ||| NotifyFilters.Size
        watcher.Changed.Add(fun _ -> this.TriggerReload())
        watcher.Created.Add(fun _ -> this.TriggerReload())
        watcher.Renamed.Add(fun _ -> this.TriggerReload())
        // 决策 41 第三问：文件被删除也向 stderr 报告（reload 读文件失败走 onRejected），
        // 并继续使用最后一次有效配置，而非静默沿用
        watcher.Deleted.Add(fun _ -> this.TriggerReload())
        watcher.EnableRaisingEvents <- true

    /// 文件系统通知合并（决策 43：可重置 debounce ~100ms）。
    member _.TriggerReload() =
        if isNull debounceTimer then
            debounceTimer <-
                new Timer(
                    (fun _ ->
                        debounceTimer.Dispose()
                        debounceTimer <- null
                        reloadFromDisk () |> ignore),
                    null,
                    100,
                    Timeout.Infinite
                )
        else
            debounceTimer.Change(100, Timeout.Infinite) |> ignore

    /// 当前生效配置（最后一次成功加载）。
    member _.Current : AppConfig =
        lock lockObj (fun () -> current)

    /// 完整重写 TOML（决策 42/43 单一路径）：生成 → 临时文件 → flush → rename → 重新加载。
    /// 只有重新加载成功才返回 Ok；失败保留旧配置（决策 44：不得在 reload 失败时视为成功）。
    /// 并发安全：同一进程内多个连接可能同时配对/吊销（决策 46/59），Rewrite 必须串行化，
    /// 否则并发写同一临时文件（.%s.tmp.<pid>）会互相截断、rename 出半写配置。
    member this.Rewrite(newConfig: AppConfig) : Result<unit, string> =
        lock lockObj (fun () ->
            let dir = Path.GetDirectoryName path
            let tmp = Path.Combine(dir, sprintf ".%s.tmp.%d" (Path.GetFileName path) Environment.ProcessId)
            try
                let text = TomlCodec.serialize newConfig
                // 临时文件写入后显式释放，再执行原子 rename（避免目标文件被占用）
                do
                    use fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)
                    // Q118：配置文件最小用户权限（仅当前用户可读写）
                    try File.SetUnixFileMode(tmp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
                    let bytes = Encoding.UTF8.GetBytes text
                    fs.Write(bytes, 0, bytes.Length)
                    fs.Flush()
                File.Move(tmp, path, true)
                match reloadFromDisk () with
                | Ok _ -> Ok()
                | Error errs -> Error(sprintf "config reload failed after rewrite: %s" (String.concat "; " errs))
            with e ->
                try if File.Exists tmp then File.Delete tmp with _ -> ()
                Error(sprintf "config rewrite failed: %s" e.Message))

    /// 锁内原子读-改-写（决策 46/59：多个连接并发配对/吊销/lastSeen 写回时，
    /// 各自基于同一旧快照派生新配置会互相覆盖——必须把"读最新值→改→落盘"放进同一把锁）。
    member this.Update(mutate: AppConfig -> AppConfig) : Result<unit, string> =
        lock lockObj (fun () -> this.Rewrite(mutate current))

    member _.Path = path

    member _.Dispose() =
        if not disposed then
            disposed <- true
            watcher.EnableRaisingEvents <- false
            watcher.Dispose()
            if not (isNull debounceTimer) then debounceTimer.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    /// 打开配置存储；文件不存在时生成完整默认 TOML（含稳定 instanceId，决策 182）。
    static member Open(path: string, onReloaded: AppConfig -> unit, onRejected: string -> unit) : Result<ConfigStore, string> =
        let dir = Path.GetDirectoryName path
        if not (String.IsNullOrWhiteSpace dir) then
            Directory.CreateDirectory dir |> ignore
        if not (File.Exists path) then
            let cfg = AppConfig.defaults (Guid.CreateVersion7())
            let text = TomlCodec.serialize cfg
            let tmp = Path.Combine(dir, sprintf ".%s.tmp.%d" (Path.GetFileName path) Environment.ProcessId)
            do
                use fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)
                try File.SetUnixFileMode(tmp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
                let bytes = Encoding.UTF8.GetBytes text
                fs.Write(bytes, 0, bytes.Length)
                fs.Flush()
            File.Move(tmp, path, true)
        match TomlCodec.tryParse (File.ReadAllText path) with
        | Ok cfg ->
            // Q118：确保配置文件始终为最小用户权限（首建或外部创建时 umask 可能放宽）
            try File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
            Ok(new ConfigStore(path, cfg, onReloaded, onRejected))
        | Error errs -> Error(String.concat "; " errs)
