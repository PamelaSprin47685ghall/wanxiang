module Wanxiang.Tests.AsyncHighlightTests

open System
open System.Threading
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
    use subscription = Highlight.Ready.Subscribe(fun _ -> signal.Set())
    signal.Wait timeoutMs

let private kindsOf (tokens: CodeToken list) = tokens |> List.map (fun t -> t.kind)

[<Fact>]
let ``桌面端先给纯文本，后台算完再换成着色版`` () =
    let backend = CountingBackend()
    RichBackend.install backend
    let code = uniqueCode "delayed"

    // 第一次问：不能占着 UI 线程去算，先拿纯文本
    Assert.Equal<CodeTokenKind list>([ CodePlain ], kindsOf (Highlight.tokenize true "fsharp" code))
    Assert.True(waitForReady 5000, "等不到后台着色完成")

    let highlighted = Highlight.tokenize true "fsharp" code
    Assert.Contains(CodeKeyword, kindsOf highlighted)
    // 着色不能吃字
    Assert.Equal(code, highlighted |> List.map (fun t -> t.text) |> String.concat "")

[<Fact>]
let ``流式进行中一次也不调用着色`` () =
    // 未写完的代码着不准，而每个 token 重算一遍会把机器点着
    let backend = CountingBackend()
    RichBackend.install backend
    let code = uniqueCode "streaming"
    let before = backend.Calls
    for cut in 4 .. code.Length do
        Highlight.tokenize false "fsharp" (code.Substring(0, cut)) |> ignore
    Thread.Sleep 120
    Assert.Equal(before, backend.Calls)

[<Fact>]
let ``同一段代码只排队一次`` () =
    // 重绘很频繁；同一段被反复排队会灌满线程池
    let backend = CountingBackend()
    RichBackend.install backend
    let code = uniqueCode "once"
    for _ in 1 .. 20 do
        Highlight.tokenize true "fsharp" code |> ignore
    Assert.True(waitForReady 5000, "等不到后台着色完成")
    Thread.Sleep 150
    Assert.Equal(1, backend.Calls)

[<Fact>]
let ``认不出的语言不进后台队列`` () =
    let backend = CountingBackend()
    RichBackend.install backend
    let before = backend.Calls
    Assert.Equal<CodeTokenKind list>([ CodePlain ], kindsOf (Highlight.tokenize true "沒有這種語言" "任意内容"))
    Thread.Sleep 120
    Assert.Equal(before, backend.Calls)

[<Fact>]
let ``多段代码同时算完只通知一次`` () =
    // 打开含二十个代码块的旧会话时，每次通知都会触发一次全量重画。
    // 不合并的话屏幕要连抖几秒。
    let backend = CountingBackend()
    RichBackend.install backend
    let mutable notifications = 0
    use subscription = Highlight.Ready.Subscribe(fun _ -> Interlocked.Increment(&notifications) |> ignore)

    let blocks = [ for i in 1 .. 20 -> uniqueCode ("batch" + string i) ]
    for code in blocks do
        Highlight.tokenize true "fsharp" code |> ignore

    // 等到全部算完并过掉合并窗口
    Thread.Sleep 700
    Assert.Equal(20, backend.Calls)
    Assert.True(notifications <= 3, $"二十段代码触发了 {notifications} 次通知，没有合并")
    // 而且结果都进了缓存
    for code in blocks do
        Assert.Contains(CodeKeyword, kindsOf (Highlight.tokenize true "fsharp" code))
