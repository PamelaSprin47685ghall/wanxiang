module Wanxiang.Tests.AsyncHighlightTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Wanxiang.UI

/// 记录被问过几次的桩后端，用来证明流式期间一次都没算。
type private CountingBackend() =
    let mutable calls = 0
    member _.Calls = calls
    interface IRichBackend with
        member _.HighlightHtml(code, _) =
            Interlocked.Increment(&calls) |> ignore
            let tail = if code.Length > 3 then code.Substring 3 else ""
            Some("<span class=\"hljs-keyword\">let</span>" + Net.WebUtility.HtmlEncode tail)
        member _.MathHtml(_, _) = None

let private uniqueCode (tag: string) =
    "let " + tag + Guid.NewGuid().ToString "N" + " = 1"

let private waitForReady (timeoutMs: int) =
    use signal = new ManualResetEventSlim(false)
    use subscription = Reading.tint.Ready.Subscribe(fun _ -> signal.Set())
    signal.Wait timeoutMs

let private kindsOf (tokens: CodeToken list) = tokens |> List.map (fun t -> t.kind)

/// 决策 421 续：`Reading.tint.Ready` 是模块级事件、在后台线程上触发；外壳订阅它后用
/// `Dispatcher.UIThread.Post` 切回 UI 线程。测试会话在每个 UI 测试结束时重置 UI 调度器，
/// 若此后才触发，`Dispatcher.UIThread` 就在线程池线程上**新建**一个调度器并归它所有，
/// 下一个 UI 测试的装配随即抛 VerifyAccess（导出页脚、分叉焦点两条偶发失败的根因）。
/// 外壳必须投递到自己构造时所在的调度器，而不是事后去取 `Dispatcher.UIThread`。
[<Trait("Category", "UI")>]
[<Fact>]
let ``a highlight finishing after the ui test ended does not create a dispatcher off the ui thread`` () =
    let code = uniqueCode "late"
    use signal = new ManualResetEventSlim(false)
    use _subscription = Reading.tint.Ready.Subscribe(fun _ -> signal.Set())
    Headless.run (fun () ->
        let view = MainView()
        view.Build()
        RichBackend.install (CountingBackend())
        Assert.Equal<CodeTokenKind list>([ CodePlain ], kindsOf (Reading.tint.Tokenize(true, "fsharp", code))))
    // 会话已收尾；后台着色此刻才完成，在线程池线程上触发 Ready
    Assert.True(signal.Wait 5000, "等不到后台着色完成")
    Thread.Sleep 100
    Assert.Equal(None, UiDispatcherGuardAttribute.UiDispatcherThreadId())

