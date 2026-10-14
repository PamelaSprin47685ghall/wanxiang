namespace Wanxiang.UI

open System
open System.Collections.Generic
open System.Net
open System.Text
open System.Threading.Tasks

/// 代码着色的排队、缓存与重画通知：HTML 解析与 scope 归类归 HljsHtml，
/// 语言解析归 RichAssets，记忆化归 BoundedCache；本模块只安排后台计算与通知节奏。
module Highlight =

    let private cache = BoundedCache<struct (string * string), CodeToken list>(192)

    let private ready = Event<unit>()

    /// 后台算完一段着色时触发。视图据此丢弃卡片缓存、重画一次。
    /// 事件在后台线程上触发，订阅方自己负责切回 UI 线程。
    let Ready = ready.Publish

    /// 正在后台着色的代码，避免同一段被反复排队
    let private inFlight = HashSet<struct (string * string)>()
    let private gate = obj ()

    /// 通知窗口（毫秒）。
    ///
    /// 打开一个含二十个代码块的旧对话会同时排出二十个后台任务，
    /// 各自算完各自触发一次全量重画 —— 屏幕会连抖几秒。
    /// 一个窗口内的完成合并成一次通知。
    [<Literal>]
    let private notifyWindowMs = 120

    let mutable private notifyScheduled = false

    /// 合并通知：窗口内第一个完成负责安排，其余搭车。
    let private notifyOnce () =
        let claimed =
            lock gate (fun () ->
                if notifyScheduled then false
                else
                    notifyScheduled <- true
                    true)
        if claimed then
            Task.Run(fun () ->
                Task.Delay(notifyWindowMs).GetAwaiter().GetResult()
                lock gate (fun () -> notifyScheduled <- false)
                ready.Trigger())
            |> ignore

    let private plainOf (code: string) = [ { text = code; kind = CodePlain } ]

    let private compute (backend: IRichBackend) (canonical: string) (code: string) =
        match backend.HighlightHtml(code, canonical) with
        | Some html ->
            match HljsHtml.parseHtml html with
            | [] -> plainOf code
            | tokens -> tokens
        | None -> plainOf code

    /// 排到后台去算。
    ///
    /// 桌面端跑的是纯托管 JS 解释器：50 行代码约 140ms，1000 行约 0.9s，
    /// 5000 行要 6 秒。这些若发生在 UI 线程上就是整窗冻结，
    /// 所以先给纯文本，算好了再让视图重画一次。
    let private enqueue (backend: IRichBackend) (key: struct (string * string)) =
        let claimed = lock gate (fun () -> inFlight.Add key)
        if claimed then
            let struct (canonical, code) = key
            Task.Run(fun () ->
                let mutable ok = false
                try
                    cache.Set(key, compute backend canonical code)
                    ok <- true
                finally
                    lock gate (fun () -> inFlight.Remove key |> ignore)
                if ok then notifyOnce ())
            |> ignore

    /// 着色。`allowHighlight = false`（流式进行中）时直接给纯文本：
    /// 未写完的代码本来就着不准，而每个 token 都重新着色一遍会把机器点着。
    ///
    /// 语言认不出、后端没装、或 hljs 抛了，同样退化为整段纯文本。
    let tokenize (allowHighlight: bool) (language: string) (code: string) : CodeToken list =
        if String.IsNullOrEmpty code then []
        else
            match RichAssets.resolveLanguage language, RichBackend.current () with
            | Some canonical, Some backend when allowHighlight ->
                let key = struct (canonical, code)
                match cache.TryGet key with
                | Some hit -> hit
                | None ->
                    // 浏览器端是宿主 JS 引擎，比解释器快两个数量级，同步做即可；
                    // 浏览器里也没有可用的后台线程。
                    if OperatingSystem.IsBrowser() then
                        let tokens = compute backend canonical code
                        cache.Set(key, tokens)
                        tokens
                    else
                        enqueue backend key
                        plainOf code
            | _ -> plainOf code
