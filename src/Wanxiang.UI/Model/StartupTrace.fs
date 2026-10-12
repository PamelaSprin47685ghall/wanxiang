namespace Wanxiang.UI

open System
open System.Diagnostics

/// 决策 290：启动感知性能打点。只在环境变量 WANXIANG_STARTUP_TRACE 存在时输出，
/// 默认零开销（一次环境变量读取）。每个里程碑只记一次，单位是「距进程启动」的毫秒。
///   firstFrame   主窗口第一帧画出（用户看到的不再是空白）
///   listReady    对话列表快照到达并渲染（侧栏可用）
///   chatReady    当前对话快照到达并渲染（可读可写）
/// 值为 "exit" 时在 chatReady（或 listReady 后 3 秒无对话）后退出进程，供 tools/perf_desktop.sh 采样。
module StartupTrace =
    let private mode =
        try Environment.GetEnvironmentVariable "WANXIANG_STARTUP_TRACE" with _ -> null
    let enabled = not (String.IsNullOrEmpty mode)
    let exitAfter = mode = "exit"
    let private seen = Collections.Generic.HashSet<string>()
    let private origin =
        lazy
            (try Process.GetCurrentProcess().StartTime.ToUniversalTime()
             with _ -> DateTime.UtcNow)

    /// 记录里程碑；返回是否首次记录。
    let mark (name: string) : bool =
        if not enabled then false
        else
            lock seen (fun () ->
                if seen.Add name then
                    let ms = (DateTime.UtcNow - origin.Force()).TotalMilliseconds
                    eprintfn "startup.%s uptime_ms=%.0f" name ms
                    true
                else false)

    /// 退出回调由宿主（桌面窗口）安装。
    let mutable requestExit: unit -> unit = ignore

/// 决策 291：同进程主机的就绪门。桌面入口在后台重放日志、启动主机时把它换成那个任务；
/// 客户端第一次连接前等它完成。默认已完成（PWA、纯客户端、测试）。
module LocalHostGate =
    let mutable ready: System.Threading.Tasks.Task = System.Threading.Tasks.Task.CompletedTask
