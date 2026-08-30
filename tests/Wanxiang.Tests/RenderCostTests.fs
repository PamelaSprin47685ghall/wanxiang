module Wanxiang.Tests.RenderCostTests

open System
open System.Diagnostics
open Avalonia.Controls
open Xunit
open Xunit.Abstractions
open Wanxiang.UI
open Wanxiang.Tests

/// 量一次「整表重建」的代价。
///
/// 流式期间每收到一批 delta 就会重绘全部消息，所以这个数字直接等于
/// 长对话里每个 token 的卡顿量。
type Cost(output: ITestOutputHelper) =

    let sampleText =
        String.concat
            "\n\n"
            [ "### 小节标题"
              "一段中文正文，混一点 `行内代码` 和 [链接](https://example.com)。"
              "- 列表项一\n- 列表项二"
              "```fsharp\nlet rec fib n =\n    match n with\n    | 0 | 1 -> n\n    | _ -> fib (n - 1) + fib (n - 2)\n```"
              "行内公式 $E = mc^2$ 收尾。" ]

    let messageAt (index: int) : MessageView =
        { MessageView.empty with
            role = (if index % 2 = 0 then "user" else "assistant")
            text = sampleText
            commitId = Some(uint64 index)
            committedAt = Some DateTimeOffset.UtcNow }

    let actions: MessageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }

    let context: MessageContext =
        { fontSize = Tokens.fontReading
          autoCollapseReasoning = true
          streaming = false
          isLastAssistant = false
          usage = None
          missingAttachments = Set.empty
          brandAvatar = fun () -> Border() :> Control }

    [<Fact>]
    member _.``报告整表重建的代价``() =
        Headless.ensure ()
        // 预热：首次要付 Markdig / 字体 / 主题的一次性成本
        MessageCard.render (messageAt 0) context actions |> ignore

        for count in [ 20; 60; 200 ] do
            let messages = [ for i in 1 .. count -> messageAt i ]
            let watch = Stopwatch.StartNew()
            for message in messages do
                MessageCard.render message context actions |> ignore
            watch.Stop()
            output.WriteLine(
                sprintf "%3d 条消息重建一次 = %6.1f ms（每条 %.2f ms）"
                    count watch.Elapsed.TotalMilliseconds (watch.Elapsed.TotalMilliseconds / float count))
