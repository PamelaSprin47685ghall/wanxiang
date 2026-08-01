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

    let reloadFromDisk () =
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
        | Error errs ->
            // 继续使用最后一次有效配置；stderr 由调用方记录
            onRejected(String.concat "; " errs)

    let mutable debounceTimer: Timer = null

    do
        watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName ||| NotifyFilters.Size
        watcher.Changed.Add(fun _ -> this.TriggerReload())
        watcher.Created.Add(fun _ -> this.TriggerReload())
        watcher.Renamed.Add(fun _ -> this.TriggerReload())
        watcher.EnableRaisingEvents <- true

    /// 文件系统通知合并（决策 43：可重置 debounce ~100ms）。
    member _.TriggerReload() =
        if isNull debounceTimer then
            debounceTimer <-
                new Timer(
                    (fun _ ->
                        debounceTimer.Dispose()
                        debounceTimer <- null
                        reloadFromDisk ()),
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
    /// 只有重新加载成功才返回 Ok；失败保留旧配置。
    member this.Rewrite(newConfig: AppConfig) : Result<unit, string> =
        let dir = Path.GetDirectoryName path
        let tmp = Path.Combine(dir, sprintf ".%s.tmp.%d" (Path.GetFileName path) Environment.ProcessId)
        try
            let text = TomlCodec.serialize newConfig
            use fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)
            let bytes = Encoding.UTF8.GetBytes text
            fs.Write(bytes, 0, bytes.Length)
            fs.Flush()
            File.Move(tmp, path, true)
            reloadFromDisk ()
            Ok()
        with e ->
            Error(sprintf "config rewrite failed: %s" e.Message)

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
            let cfg = AppConfig.defaults (Guid.NewGuid())
            let text = TomlCodec.serialize cfg
            let tmp = Path.Combine(dir, sprintf ".%s.tmp.%d" (Path.GetFileName path) Environment.ProcessId)
            use fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)
            let bytes = Encoding.UTF8.GetBytes text
            fs.Write(bytes, 0, bytes.Length)
            fs.Flush()
            File.Move(tmp, path, true)
        match TomlCodec.tryParse (File.ReadAllText path) with
        | Ok cfg -> Ok(new ConfigStore(path, cfg, onReloaded, onRejected))
        | Error errs -> Error(String.concat "; " errs)
