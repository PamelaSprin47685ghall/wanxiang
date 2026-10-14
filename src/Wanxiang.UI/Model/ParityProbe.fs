namespace Wanxiang.UI

open System
open System.Collections.Generic
open Wanxiang.Interop

/// 决策 450：桌面 / PWA 生命周期一致性探针。默认关闭（零开销：一次布尔判断）。
///   桌面：环境变量 WANXIANG_PARITY_PROBE=1，每个探点首次命中时往 stderr 写一行 `parity.<名字>`；
///   PWA：页面地址带 `wxprobe=1`，命中计数写进 `globalThis.__wxProbe`（tools/smoke_published_pwa.js 读取断言）。
/// 探点只记「这条生命周期路径真的跑到了」，不带任何用户内容。
module ParityProbe =
    let private browserEnabled () =
        OperatingSystem.IsBrowser() && (try BrowserProbe.Enabled() with _ -> false)

    let private desktopEnabled () =
        not (OperatingSystem.IsBrowser())
        && (try Environment.GetEnvironmentVariable "WANXIANG_PARITY_PROBE" = "1" with _ -> false)

    /// 测试可以强制打开（只影响进程内计数，不写 stderr / JS）。
    let mutable forceInProcess = false
    let private external = lazy (browserEnabled () || desktopEnabled ())
    let enabled () = forceInProcess || external.Force()

    /// 计数归 ProbeCounter；开关条件与输出（stderr / JS）碰平台，留在宿主。
    let private counter = ProbeCounter()

    /// 记录一次命中。
    let hit (name: string) =
        if enabled () then
            counter.Enabled <- true
            let first = counter.Hit name
            if external.Force() then
                if OperatingSystem.IsBrowser() then
                    try BrowserProbe.Hit name with _ -> ()
                elif first then
                    eprintfn "parity.%s" name

    let count (name: string) = counter.Count name

    let reset () = counter.Reset()

/// 决策 452：小型本机文本（界面偏好、命令面板最近使用）的存取。
/// 桌面写文件；PWA 的 System.IO 只是内存文件系统，刷新即丢——改写 localStorage（键 `wanxiang.local:<文件名>`）。
module LocalText =
    let private key (path: string) =
        "wanxiang.local:" + IO.Path.GetFileName path

    let read (path: string) : string option =
        try
            if OperatingSystem.IsBrowser() then
                match BrowserLocalStore.Get(key path) with
                | null -> None
                | s -> Some s
            elif IO.File.Exists path then
                Some(IO.File.ReadAllText path)
            else
                None
        with _ ->
            None

    /// 返回是否写到了持久存储。
    let write (path: string) (text: string) : bool =
        try
            if OperatingSystem.IsBrowser() then
                BrowserLocalStore.Set(key path, text)
                true
            else
                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName path) |> ignore
                IO.File.WriteAllText(path, text)
                true
        with _ ->
            false
