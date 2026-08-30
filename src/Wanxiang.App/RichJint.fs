namespace Wanxiang.App

open System.Collections.Generic
open Jint
open Wanxiang.UI

/// 桌面端的脚本后端：在进程内跑 highlight.js 与 KaTeX。
///
/// 用 Jint（纯托管 JS 解释器）而非 V8 绑定，是为了不引入原生依赖；
/// 代价是要自己垫 `console`（hljs 遇到未注册语言会 warn，裸引擎里会抛穿）。
/// 这个类型刻意留在桌面入口而不是 Wanxiang.UI：PWA 引用 UI，
/// 放进 UI 就等于把 1MB 的 Jint 塞进 WASM 包，而浏览器本来就有 JS 引擎。
type JintRichBackend() =

    // Jint 的 Engine 不是线程安全的：着色发生在 UI 线程，预热在后台线程
    let gate = obj ()
    let mutable engine: Engine option = None
    let mutable hljsLoaded = false
    let mutable katexLoaded = false
    let onDemandLoaded = HashSet<string>()

    let ensureEngine () =
        match engine with
        | Some existing -> existing
        | None ->
            let created = new Engine(fun options -> options.Strict(false).LimitRecursion 10000 |> ignore)
            created.Execute(
                "var console={log:function(){},warn:function(){},error:function(){},"
                + "debug:function(){},info:function(){},trace:function(){}};")
            |> ignore
            engine <- Some created
            created

    let ensureHljs (host: Engine) =
        if not hljsLoaded then
            host.Execute(RichAssets.hljsCoreScript ()) |> ignore
            hljsLoaded <- true

    let ensureKatex (host: Engine) =
        if not katexLoaded then
            host.Execute(RichAssets.katexScript ()) |> ignore
            katexLoaded <- true

    let ensureLanguage (host: Engine) (canonical: string) =
        if RichAssets.requiresLoad canonical && not (onDemandLoaded.Contains canonical) then
            host.Execute(RichAssets.hljsLanguageScript canonical) |> ignore
            onDemandLoaded.Add canonical |> ignore

    /// 预热。基座脚本解析要几百毫秒，放到后台线程先付掉，
    /// 免得用户第一次收到代码块时卡在 UI 线程上。
    member _.Warmup() =
        lock gate (fun () ->
            try
                let host = ensureEngine ()
                ensureHljs host
                ensureKatex host
                // 空跑一次：解释器首次执行语法定义比稳态慢一个量级
                host.SetValue("__warm", "let x = 1") |> ignore
                host.Evaluate("hljs.highlight(__warm,{language:'javascript',ignoreIllegals:true}).value") |> ignore
                host.Evaluate("katex.renderToString('x',{throwOnError:false})") |> ignore
            with _ -> ())

    interface IRichBackend with

        member _.HighlightHtml(code, canonicalLanguage) =
            lock gate (fun () ->
                try
                    let host = ensureEngine ()
                    ensureHljs host
                    ensureLanguage host canonicalLanguage
                    // 走 SetValue 而不是把源码拼进表达式：代码里什么引号都可能有
                    host.SetValue("__code", code) |> ignore
                    host.SetValue("__lang", canonicalLanguage) |> ignore
                    host
                        .Evaluate("hljs.highlight(__code,{language:__lang,ignoreIllegals:true}).value")
                        .AsString()
                    |> Some
                with _ -> None)

        member _.MathHtml(tex, displayMode) =
            lock gate (fun () ->
                try
                    let host = ensureEngine ()
                    ensureKatex host
                    host.SetValue("__tex", tex) |> ignore
                    host.SetValue("__display", displayMode) |> ignore
                    host
                        .Evaluate(
                            "katex.renderToString(__tex,{displayMode:__display,throwOnError:false,strict:false})")
                        .AsString()
                    |> Some
                with _ -> None)
