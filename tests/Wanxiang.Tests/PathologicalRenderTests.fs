module Wanxiang.Tests.PathologicalRenderTests

open System
open System.Diagnostics
open Avalonia.Controls
open Xunit
open Xunit.Abstractions
open Wanxiang.UI
open Wanxiang.Tests

/// 病态输入下的渲染代价。模型完全可能吐出这些东西，界面不能因此卡住。
type Pathological(output: ITestOutputHelper) =

    let actions: MessageActions =
        { copyText = ignore; regenerate = ignore; editAndFork = ignore
          deleteMessage = ignore; downloadAttachment = ignore; openLink = ignore }

    let context: MessageContext =
        { fontSize = Tokens.fontReading
          autoCollapseReasoning = true
          streaming = false
          isLastAssistant = false
          usage = None
          missingAttachments = Set.empty
          brandAvatar = fun () -> Border() :> Control }

    let timeRender (label: string) (text: string) =
        Headless.ensure ()
        let message = { MessageView.empty with role = "assistant"; text = text; commitId = Some 1UL }
        let watch = Stopwatch.StartNew()
        let control = MessageCard.render message context actions
        watch.Stop()
        output.WriteLine(sprintf "%-26s 输入 %7d 字 → 渲染 %8.1f ms" label text.Length watch.Elapsed.TotalMilliseconds)
        Assert.NotNull control
        watch.Elapsed.TotalMilliseconds

    [<Fact>]
    member _.``报告病态输入的渲染代价``() =
        Headless.ensure ()
        timeRender "预热" "hello" |> ignore

        let hugeCode =
            let lines = [ for i in 1 .. 5000 -> $"    let value{i} = compute {i} |> Option.defaultValue 0" ]
            "```fsharp\n" + String.Join("\n", lines) + "\n```"
        let longSingleLine = "```\n" + String('x', 200000) + "\n```"
        let hugeTable =
            let rows = [ for i in 1 .. 800 -> $"| 行 {i} | 值 {i} | 说明 {i} |" ]
            "| A | B | C |\n|---|---|---|\n" + String.Join("\n", rows)
        let manyFormulas = String.Join(" ", [ for i in 1 .. 300 -> $"$x_{i}^2$" ])
        let deepNesting = String.replicate 200 "> " + "深嵌套引用"
        let manyParagraphs = String.Join("\n\n", [ for i in 1 .. 2000 -> $"第 {i} 段中文正文，带一点 `代码`。" ])

        let costs =
            [ "5000 行代码块", timeRender "5000 行代码块" hugeCode
              "20 万字符单行", timeRender "20 万字符单行" longSingleLine
              "800 行表格", timeRender "800 行表格" hugeTable
              "300 个行内公式", timeRender "300 个行内公式" manyFormulas
              "200 层嵌套引用", timeRender "200 层嵌套引用" deepNesting
              "2000 段正文", timeRender "2000 段正文" manyParagraphs ]

        for (label, ms) in costs do
            // 单条消息渲染超过两秒，用户会认为程序死了
            Assert.True(ms < 2000.0, $"{label} 渲染 {ms:F0} ms，太久")
