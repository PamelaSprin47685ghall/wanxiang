namespace Wanxiang.Server

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Wanxiang.Protocol

/// 主机的可见状态（W5，决策 82/83）：只存内存，不入事件日志，不落盘。
///
/// - 配置被拒绝：主机继续使用上一份有效配置（决策 44），但客户端必须知道，
///   否则用户会以为自己改好了。原因摘要经脱敏、截断后下发。
/// - 检查主机：把只读 doctor 的结果交给已认证客户端；诊断本身运行在主机进程内。
type HostStatus() =
    let sync = obj ()
    let mutable rejection: (string * string list) option = None

    /// 单条原因的最大长度、最多条数：摘要面向人读，不是日志转储。
    static member MaxErrors = 5
    static member MaxErrorChars = 300

    static member Sanitize (redact: string -> string) (errors: string list) : string list =
        errors
        |> List.filter (fun e -> not (String.IsNullOrWhiteSpace e))
        |> List.truncate HostStatus.MaxErrors
        |> List.map (fun e ->
            let text = (redact e).Replace('\r', ' ').Replace('\n', ' ').Trim()
            if text.Length > HostStatus.MaxErrorChars then text.Substring(0, HostStatus.MaxErrorChars) + "…" else text)

    /// 记录一次拒绝并返回要广播的事件。
    member _.Reject(redact: string -> string, errors: string list) : WireEvent =
        let clean =
            match HostStatus.Sanitize redact errors with
            | [] -> [ "配置文件无法读取" ]
            | xs -> xs
        let summary = "配置有误，仍在使用上一份有效配置：" + List.head clean
        lock sync (fun () -> rejection <- Some(summary, clean))
        HostConfigRejected {| summary = summary; errors = clean |}

    /// 配置重新加载成功：状态消失（客户端由随后的 config.changed 一并清除）。
    member _.Clear() = lock sync (fun () -> rejection <- None)

    member _.Current: WireEvent option =
        lock sync (fun () ->
            rejection |> Option.map (fun (summary, errors) -> HostConfigRejected {| summary = summary; errors = errors |}))

    /// 「检查主机」的实现，由 ServerApp 注入；缺省表示主机尚未就绪。
    member val DoctorRunner: (unit -> Doctor.Diagnosis) option = None with get, set

    /// 在线程池上运行诊断（磁盘与日志读取可能较慢，绝不占用接收循环）。
    member this.RunDoctor(requestId: Guid, redact: string -> string) : Task<WireEvent> =
        Task.Run(fun () ->
            match this.DoctorRunner with
            | None ->
                let o = JsonObject()
                o["name"] <- "doctor"
                o["ok"] <- false
                o["detail"] <- "主机尚未就绪"
                DoctorReport {| requestId = requestId; ok = false; checks = JsonArray(o) |}
            | Some run ->
                try
                    let d = run ()
                    DoctorReport {| requestId = requestId; ok = Doctor.isHealthy d; checks = Doctor.toChecksJson redact d |}
                with e ->
                    let o = JsonObject()
                    o["name"] <- "doctor"
                    o["ok"] <- false
                    o["detail"] <- redact e.Message
                    DoctorReport {| requestId = requestId; ok = false; checks = JsonArray(o) |})
