namespace Wanxiang.UI

open System
open System.Text.Json
open System.Text.Json.Nodes

/// doctor 的一项检查结果（只读展示）。
type DoctorCheck = {
    name: string
    ok: bool
    detail: string
}

type DoctorState =
    | NotRun
    | Running
    | Report of ok: bool * checks: DoctorCheck list * at: DateTimeOffset
    /// 请求发出后连接断开等原因没有拿到报告。
    | Unavailable of reason: string

module DoctorCheck =

    /// 把线上 checks 数组读成列表；缺字段的项被跳过（宽容读）。
    let parseChecks (items: JsonArray) : DoctorCheck list =
        [ for item in items do
              if not (isNull item) && item.GetValueKind() = JsonValueKind.Object then
                  let o = item.AsObject()
                  let str key =
                      let mutable n: JsonNode = null
                      if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then n.GetValue<string>() else ""
                  let ok =
                      let mutable n: JsonNode = null
                      o.TryGetPropertyValue("ok", &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.True
                  match str "name" with
                  | "" -> ()
                  | name -> yield { name = name; ok = ok; detail = str "detail" } ]

    /// 检查项的用户词（只改可见层；协议名与 doctor 命令行输出不变）。
    let label (name: string) : string =
        match name with
        | "config" -> "配置文件"
        | "provider-config" -> "模型服务配置"
        | "mcp-config" -> "工具配置"
        | "listen-port" -> "监听端口"
        | "format-version" -> "日志格式版本"
        | "disk-free-space" -> "磁盘剩余空间"
        | "data-permissions" -> "数据目录权限"
        | "data-lock" -> "数据目录锁"
        | "snapshot" -> "状态快照"
        | "recovery-bundle" -> "恢复证据包"
        | "log-replay" -> "事件日志"
        | "missing-attachments" -> "附件完整性"
        | "orphan-attachments" -> "未引用附件"
        | other -> other

/// 主机的可见状态（W5，决策 82/83），只存内存：
/// 「配置有误」来自 `config.rejected`，被随后的 `config.changed`（重新加载成功）清除；
/// 「检查主机」的结果来自 `doctor.report`。设置页订阅 Changed 重绘。
type HostHealth() =
    let changed = Event<unit>()
    let mutable configIssue: (string * string list) option = None
    let mutable doctor = NotRun

    /// 设置页点「检查主机」时的发送动作，由 AppShell 注入。
    member val RequestCheck: unit -> unit = ignore with get, set

    [<CLIEvent>]
    member _.Changed = changed.Publish

    member _.ConfigIssue = configIssue
    member _.Doctor = doctor

    member _.SetConfigIssue(summary: string, errors: string list) =
        configIssue <- Some(summary, errors)
        changed.Trigger()

    /// 配置重新加载成功：返回此前是否处于「配置有误」，便于只在恢复时提示一次。
    member _.ClearConfigIssue() : bool =
        let had = configIssue.IsSome
        configIssue <- None
        if had then changed.Trigger()
        had

    member this.BeginCheck() =
        doctor <- Running
        changed.Trigger()
        this.RequestCheck()

    member _.SetReport(ok: bool, checks: DoctorCheck list) =
        doctor <- Report(ok, checks, DateTimeOffset.Now)
        changed.Trigger()

    member _.SetUnavailable(reason: string) =
        doctor <- Unavailable reason
        changed.Trigger()

    member _.Disconnected() =
        match doctor with
        | Running ->
            doctor <- Unavailable "连接已断开，没有拿到检查结果。重新连接后可再试。"
            changed.Trigger()
        | _ -> ()

    /// 换了主机：上一台主机的检查结果与配置问题都不属于新主机。
    member _.Reset() =
        configIssue <- None
        doctor <- NotRun
        changed.Trigger()

/// 设置页「本机草稿」区与外壳之间的桥：清除动作与当前概况。
type LocalDraftControls() =
    let changed = Event<unit>()
    let mutable summary = ""

    /// 点「清除本地草稿」后触发；由 AppShell 执行实际清除。
    member val ClearRequested: unit -> unit = ignore with get, set

    [<CLIEvent>]
    member _.Changed = changed.Publish

    member _.Summary
        with get () = summary
        and set (value: string) =
            if summary <> value then
                summary <- value
                changed.Trigger()
