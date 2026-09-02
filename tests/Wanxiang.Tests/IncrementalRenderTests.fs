module Wanxiang.Tests.IncrementalRenderTests

open System
open System.Diagnostics
open Avalonia.Controls
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Xunit.Abstractions
open Wanxiang.UI
open Wanxiang.Tests

/// 流式期间每批 delta 都会重绘一次消息列表。这一组测的是「只有真变了的卡才重建」。
type Incremental(output: ITestOutputHelper) =

    let sample =
        String.concat
            "\n\n"
            [ "### 小节"
              "一段中文正文，混 `行内代码` 与 [链接](https://example.com)。"
              "```fsharp\nlet x = 1\n```"
              "行内公式 $E = mc^2$。" ]

    let messageAt (index: int) : MessageView =
        { MessageView.empty with
            role = (if index % 2 = 0 then "user" else "assistant")
            text = $"{sample}\n\n第 {index} 条"
            commitId = Some(uint64 index)
            committedAt = Some DateTimeOffset.UtcNow }

    let noopMessageActions: MessageActions =
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore }

    let noopActions: ChatActions =
        { renameTitle = ignore
          openSessionSettings = ignore
          forkFromHere = ignore
          stopGeneration = ignore
          requestOlderHistory = ignore
          retryLast = ignore
          toggleSidebar = ignore
          message = noopMessageActions }

    /// 建一个挂在窗口里的 ChatView，并返回「取当前已挂载卡片」的函数。
    let mount () =
        Headless.ensure ()
        let view = ChatView(noopActions, fun size -> Border(Width = size, Height = size) :> Control)
        view.Build()
        let window = Window(Width = 900.0, Height = 700.0, Content = view)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        // 消息面板是滚动区里唯一的纵向 StackPanel
        let cards () =
            view.GetVisualDescendants()
            |> Seq.choose (fun v ->
                match v with
                | :? StackPanel as panel when panel.Orientation = Avalonia.Layout.Orientation.Vertical -> Some panel
                | _ -> None)
            |> Seq.tryFind (fun panel -> panel.Children.Count > 0)
            |> Option.map (fun panel -> panel.Children |> Seq.toList)
            |> Option.defaultValue []
        view, window, cards

    let render (view: ChatView) (messages: MessageView list) (streaming: MessageView option) (fontSize: float) =
        view.RenderMessages(messages, streaming, None, fontSize, true, None, Set.empty)
        Dispatcher.UIThread.RunJobs()

    [<Fact>]
    member _.``重绘时已提交的消息卡被复用而不是重建``() =
        let view, window, cards = mount ()
        try
            let messages = [ for i in 1 .. 40 -> messageAt i ]
            render view messages None Tokens.fontReading
            let first = cards ()
            Assert.Equal(40, first.Length)

            render view messages None Tokens.fontReading
            let second = cards ()
            Assert.Equal(40, second.Length)
            // 同一批消息重绘：必须是同一批控件实例
            for (a, b) in List.zip first second do
                Assert.Same(a, b)
        finally
            window.Close()

    [<Fact>]
    member _.``追加一条消息只新建一张卡``() =
        let view, window, cards = mount ()
        try
            let messages = [ for i in 1 .. 30 -> messageAt i ]
            render view messages None Tokens.fontReading
            let before = cards ()

            render view (messages @ [ messageAt 31 ]) None Tokens.fontReading
            let after = cards ()
            Assert.Equal(31, after.Length)
            // 原本的末条助手消息会失去用量脚注，那一张理应重建；其余必须原地不动
            let reused =
                List.zip before (after |> List.truncate 30)
                |> List.filter (fun (a, b) -> obj.ReferenceEquals(a, b))
                |> List.length
            Assert.True(reused >= 29, $"只复用了 {reused}/30 张，追加一条消息不该重建整表")
        finally
            window.Close()

    [<Fact>]
    member _.``流式卡每帧都是新的而已提交部分不动``() =
        let view, window, cards = mount ()
        try
            let messages = [ for i in 1 .. 25 -> messageAt i ]
            let streamingAt (text: string) =
                Some { MessageView.empty with role = "assistant"; text = text }

            render view messages (streamingAt "写作中") Tokens.fontReading
            let first = cards ()
            render view messages (streamingAt "写作中……继续") Tokens.fontReading
            let second = cards ()

            Assert.Equal(26, first.Length)
            Assert.Equal(26, second.Length)
            for (a, b) in List.zip (first |> List.truncate 25) (second |> List.truncate 25) do
                Assert.Same(a, b)
            // 流式那一张内容变了，必须是新控件
            Assert.NotSame(List.last first, List.last second)
        finally
            window.Close()

    [<Fact>]
    member _.``改字号会让全部卡片失效``() =
        // 字号进入卡片身份：否则改了字号旧卡还挂着，看起来「设置没生效」
        let view, window, cards = mount ()
        try
            let messages = [ for i in 1 .. 12 -> messageAt i ]
            render view messages None Tokens.fontReading
            let before = cards ()
            render view messages None (Tokens.fontReading + 2.0)
            let after = cards ()
            Assert.Equal(before.Length, after.Length)
            for (a, b) in List.zip before after do
                Assert.NotSame(a, b)
        finally
            window.Close()

    [<Fact>]
    member _.``丢弃缓存后重绘会换成新卡片``() =
        // 后台着色算完走的就是这条路：着色结果进了 Highlight 的缓存，
        // 但卡片身份没变，必须靠丢缓存才能把着色版换上屏
        let view, window, cards = mount ()
        try
            let messages = [ for i in 1 .. 15 -> messageAt i ]
            render view messages None Tokens.fontReading
            let before = cards ()
            view.InvalidateCards()
            render view messages None Tokens.fontReading
            let after = cards ()
            Assert.Equal(before.Length, after.Length)
            for (a, b) in List.zip before after do
                Assert.NotSame(a, b)
        finally
            window.Close()

    [<Fact>]
    member _.``长对话的流式刷新代价与消息条数无关``() =
        let view, window, _ = mount ()
        try
            let messages = [ for i in 1 .. 200 -> messageAt i ]
            let streamingAt (n: int) = Some { MessageView.empty with role = "assistant"; text = String('x', n) }

            render view messages (streamingAt 1) Tokens.fontReading
            let watch = Stopwatch.StartNew()
            for n in 2 .. 21 do
                render view messages (streamingAt n) Tokens.fontReading
            watch.Stop()
            let perFrame = watch.Elapsed.TotalMilliseconds / 20.0
            output.WriteLine(sprintf "200 条历史下，流式每帧 = %.2f ms" perFrame)
            // 整表重建实测约 100ms/帧；复用之后应当远低于此
            Assert.True(perFrame < 20.0, $"每帧 {perFrame:F2} ms，仍然太贵")
        finally
            window.Close()
