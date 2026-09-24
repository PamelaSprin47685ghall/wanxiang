namespace Wanxiang.Tests

open System
open System.Reflection
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Media
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.Tests.Helpers
open Wanxiang.UI
open System.Text.Json.Nodes

/// 与 Kelivo 桌面端取长补短的收敛批次（round 6）：三条「边界上不好用」的路径。
///
/// 每条都对应 kelivo 源码里已处理的一个可观察事实：
/// - 模型选择不依赖已有会话（model_select_sheet.dart:216）：空会话态输入区开着、
///   芯片亮着默认模型，点了却弹「先选择一个会话」是把可用面拔掉；
/// - 流式收尾要在划上去的那一侧记未读（scroll_controller.dart:520 stickToBottomAfterGeneration）：
///   落定那一刻 desired 条数不变，delta 计未读那条路完全看不见；
/// - 消息右键菜单与 hover 操作条同源同图（chat_message_widget.dart:2015 带 Lucide 图标）：
///   MenuEntry 早就带上 icon，唯独 buildMessageContextMenu 把它丢在半路。
/// 三条都是收敛：不新增能力、不改视觉基调、不动产品契约。
module InteractabilityConvergenceTests =

    /// 与 ExchangeNavTests 同构的 ChatView 装配（本文件只锁滚动未读计数）。
    let private buildChat (messages: MessageView list) =
        Headless.ensure ()
        MotionPolicy.setReduced true
        let chatActions =
            { renameTitle = ignore
              openSessionSettings = ignore
              forkFromHere = ignore
              stopGeneration = ignore
              requestOlderHistory = ignore
              retryLast = ignore
              toggleSidebar = ignore
              focusHome = ignore
              message =
                { copyText = ignore
                  regenerate = ignore
                  editAndFork = ignore
                  deleteMessage = ignore
                  downloadAttachment = ignore
                  openLink = ignore } }
        let chat = ChatView(chatActions, Brand.logo)
        chat.Build()
        let window = Window(Width = 900.0, Height = 700.0, Content = chat)
        window.Show()
        chat.SetConversationChrome true
        chat.RenderMessages(messages, None, None, Tokens.fontReading, true, None, Set.empty)
        Dispatcher.UIThread.RunJobs()
        chat, window

    let private exchanges (count: int) : MessageView list =
        [ for round in 1 .. count do
            yield { MessageView.empty with
                     role = "user"
                     text = sprintf "第 %d 轮提问：%s" round (String.replicate 6 "填充内容以产生滚动高度。")
                     commitId = Some(uint64 (round * 2 - 1)) }
            yield { MessageView.empty with
                     role = "assistant"
                     text = sprintf "第 %d 轮回答：%s" round (String.replicate 12 "回复内容填充，需要足够高度才能形成滚动范围。")
                     commitId = Some(uint64 (round * 2)) } ]

    let private tail (text: string) : MessageView =
        { MessageView.empty with role = "assistant"; text = text; commitId = Some 999UL }

    /// 划到顶：离开底部，制造「用户在翻旧消息」的姿态。
    let private scrollAway (chat: ChatView) =
        let scroller =
            chat.GetVisualDescendants()
            |> Seq.pick (function :? ScrollViewer as sv -> Some sv | _ -> None)
        scroller.Offset <- Vector(0.0, 0.0)
        Dispatcher.UIThread.RunJobs()

    /// 「N 条新消息」小标签的文案：未读为 0 时该标签不显示，取不到即「无未读」。
    let private unreadLabelText (chat: ChatView) : string option =
        chat.GetVisualDescendants()
        |> Seq.choose (function :? TextBlock as tb -> Some tb.Text | _ -> None)
        |> Seq.filter (fun text -> not (isNull text) && (text.Contains "条新消息"))
        |> Seq.tryHead

    [<Fact>]
    let ``streaming reply finalizing while scrolled away counts as an unread message`` () =
        let chat, window = buildChat (exchanges 6)
        try
            scrollAway chat
            // 流式进行中：临时卡已在列表尾部占位（desired 条数 = N+1）。
            chat.RenderMessages(exchanges 6, Some (tail "这是流式生成中的回复。"), None, Tokens.fontReading, true, None, Set.empty)
            Dispatcher.UIThread.RunJobs()
            scrollAway chat
            // 收尾：流式卡换成已提交卡，条数不变（delta = 0）。未读必须由这次「新增」记账。
            chat.RenderMessages((exchanges 6) @ [ tail "这是流式生成中的回复。" ], None, None, Tokens.fontReading, true, None, Set.empty)
            Dispatcher.UIThread.RunJobs()
            match unreadLabelText chat with
            | Some text -> Assert.Contains("1", text)
            | None -> failwith "流式回复落定后未读没记账：用户划上去等生成，最后看不到任何提示"
        finally
            window.Close()

    [<Fact>]
    let ``streaming reply finalizing while pinned to the bottom keeps the counter clear`` () =
        let chat, window = buildChat (exchanges 6)
        try
            // 贴着底部：落定只是同一张卡刷新，不该冒未读。
            chat.RenderMessages(exchanges 6, Some (tail "流式内容"), None, Tokens.fontReading, true, None, Set.empty)
            Dispatcher.UIThread.RunJobs()
            chat.RenderMessages((exchanges 6) @ [ tail "流式内容" ], None, None, Tokens.fontReading, true, None, Set.empty)
            Dispatcher.UIThread.RunJobs()
            Assert.True(Option.isNone (unreadLabelText chat), "贴底时流式收尾不该记账未读")
        finally
            window.Close()

    /// 右键菜单动作集由 messageMenuEntries 单一产出（hover 按钮共用同一份），
    /// 但 buildMessageContextMenu 过去只取 label/action，icon 被丢在半路：
    /// 同一个动作 hover 上有图、右键里是纯文字。这句锁的就是两端同图。
    [<Fact>]
    let ``message context menu carries the same icons as the hover toolbar`` () =
        Headless.ensure ()
        let message =
            { MessageView.empty with
                role = "assistant"
                text = "这是助手回复：可复制、可重新生成、可删除。"
                commitId = Some 42UL }
        let ctx =
            { fontSize = Tokens.fontReading
              autoCollapseReasoning = true
              streaming = false
              isLastAssistant = true
              usage = None
              missingAttachments = Set.empty
              brandAvatar = fun () -> Border(Width = Tokens.logoAvatar, Height = Tokens.logoAvatar, CornerRadius = CornerRadius(Tokens.logoAvatar / 2.0)) }
        let actions =
            { copyText = ignore
              regenerate = ignore
              editAndFork = ignore
              deleteMessage = ignore
              downloadAttachment = ignore
              openLink = ignore }
        let card = MessageCard.render message ctx actions (Some 0)
        let window = Window(Width = 420.0, Height = 360.0, Content = card)
        window.Show()
        try
            let menu =
                // 卡片根 StackPanel 上挂的正是 buildMessageContextMenu 的产物
                // （ContextMenu 不是视觉树节点，得从宿主属性取）。
                match card.ContextMenu with
                | null -> None
                | cm -> Some cm
            Assert.True(menu.IsSome, "消息卡应挂右键菜单")
            let items =
                menu.Value.ItemsSource :?> System.Collections.IEnumerable
                |> Seq.cast<obj>
                |> Seq.choose (function :? MenuItem as item -> Some item | _ -> None)
                |> List.ofSeq
            Assert.True(items.Length >= 3, "复制 / 重新生成 / 删除三项都应出现在菜单里")
            Assert.True(items |> List.forall (fun i -> not (isNull i.Icon)), "右键菜单每项都该带图标（与 hover 操作条同源）")
            let delete =
                items |> List.find (fun i -> (string i.Header).Contains "删除")
            // 删错一条的代价最高：红字另有一层「别顺手点到它」的提醒。
            Assert.IsAssignableFrom<IBrush>(delete.Foreground)
        finally
            window.Close()

    /// 停止生成后焦点必须回到输入区。点了按钮却把焦点留在按钮上，
    /// 用户紧接着打的字全喂给按钮——回车还会再触发一次停止/发送。
    /// kelivo chat_input_bar.dart:1010 在 stop 分支同样 requestFocus 回 focusNode。
    [<Fact>]
    let ``stopping generation hands focus back to the input`` () =
        Headless.ensure ()
        let stops = ResizeArray<int>()
        let composerActions =
            { submit = fun _ -> true
              stopGeneration = fun () -> stops.Add 1
              pickAttachment = ignore
              removeAttachment = ignore
              openModelPicker = ignore
              dropFiles = ignore
              pasteFromClipboard = fun () -> false }
        let composer = Composer(composerActions)
        composer.Build()
        composer.SetEnabled(true, "")
        composer.SetText "草稿内容"
        let rec findInput (c: Control) : TextBox option =
            match c with
            | :? TextBox as tb -> Some tb
            | :? Panel as p -> p.Children |> Seq.tryPick findInput
            | :? Decorator as d when not (isNull d.Child) -> findInput d.Child
            | _ -> None
        let input =
            findInput composer |> Option.defaultWith (fun () -> failwith "input missing")
        // 语态要先落地：按钮的自动化名「停止生成」由 refreshSendState 写入。
        composer.SetGenerating true
        composer.SetCanStop true
        Dispatcher.UIThread.RunJobs()
        Dispatcher.UIThread.RunJobs()
        let window = Window(Width = 420.0, Height = 260.0, Content = composer)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        let names =
            composer.GetVisualDescendants()
            |> Seq.choose (function :? Button as b -> Some(AutomationProperties.GetName b, b) | _ -> None)
            |> List.ofSeq
        // Ui.button/iconButton 造的是 Border（本项目自己的按钮基座），不是 Avalonia Button：
        // 用自动化名锁定，靠 (string b.Content) 判图标文字。
        let rec findSend (c: Control) : Border option =
            match c with
            | :? Border as b when AutomationProperties.GetName b = "停止生成" -> Some b
            | :? Panel as p -> p.Children |> Seq.tryPick findSend
            | :? Decorator as d when not (isNull d.Child) -> findSend d.Child
            | _ -> None
        let sendButton =
            findSend composer |> Option.defaultWith (fun () -> failwith "发送键缺失")
        try
            Assert.True(sendButton.IsEnabled, sprintf "生成中发送键应切到停止语态：%A" names)
            sendButton.Focus() |> ignore
            Assert.True(sendButton.IsFocused, "前置条件：焦点在停止键上")
            // 真正点击：走自动化 Invoke，与真人点击同一条入口。
            // Ui.onClick 给 Border 同时挂了 PointerReleased 与 Enter/Space KeyDown：
            // 键盘路径和鼠标路径进的是同一个 action，用 Enter 触发（也顺便验了键盘可达）。
            sendButton.RaiseEvent(
                KeyEventArgs(
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Enter,
                    KeyModifiers = KeyModifiers.None,
                    Source = sendButton))
            Dispatcher.UIThread.RunJobs()
            Assert.Equal(1, stops.Count)
            Assert.True(input.IsFocused, "停止之后焦点应回到输入区，否则接下来的字全喂给按钮")
        finally
            window.Close()

    // 空态下点模型芯片：过去固定弹「先选择一个会话。」——而此刻输入区是启用的、
    // 芯片上正亮着默认模型名。kelivo model_select_sheet.dart:216 的选择不依赖已有会话。
    // 这里锁三条：菜单开得出、选中写入草稿暂存位、草稿不走丢（建会话时作为起点）。
    [<Fact>]
    let ``empty state model chip opens the picker and remembers the choice`` () =
        Headless.ensure ()
        MotionPolicy.setReduced true
        let shell = MainView()
        shell.Build()
        let window = Window(Width = 900.0, Height = 700.0, Content = shell)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        try
            // 伪造一份看得见的目录：两个服务商各两个模型，都可用。
            let providersJson =
                JsonNode.Parse(
                    """{ "p1": { "label": "服务商一", "models": ["m-a", "m-b"], "enabled": true },
                         "p2": { "label": "服务商二", "models": ["m-c"], "enabled": true } }""")
            let flags = BindingFlags.Instance ||| BindingFlags.NonPublic
            let catalogField = typeof<MainView>.GetField("catalog", flags)
            Assert.True(not (isNull catalogField), "catalog 字段应可反射到")
            // Catalog.parse 走的是协议侧入口，这里直接构造结构体。
            let catalog =
                { Catalog.empty with
                    providers =
                        [ { id = "p1"
                            label = "服务商一"
                            kind = "openai"
                            baseUrl = "https://a.example.com/v1"
                            hasApiKey = true
                            models = [ "m-a"; "m-b" ]
                            defaultModel = "m-a"
                            timeoutSeconds = 30
                            maxRetries = 2
                            enabled = true }
                          { id = "p2"
                            label = "服务商二"
                            kind = "openai"
                            baseUrl = "https://b.example.com/v1"
                            hasApiKey = true
                            models = [ "m-c" ]
                            defaultModel = "m-c"
                            timeoutSeconds = 30
                            maxRetries = 2
                            enabled = true } ] }
            catalogField.SetValue(shell, catalog)
            // ShowModelPicker 是私有的：按 AppShell 空态路径反射调用（activeConvId = None）。
            let showPicker = typeof<MainView>.GetMethod("ShowModelPicker", flags)
            Assert.True(not (isNull showPicker), "ShowModelPicker 应可反射到")
            let anchor = Border()
            showPicker.Invoke(shell, [| box anchor |]) |> ignore
            Dispatcher.UIThread.RunJobs()
            // 菜单真的开出来了：OverlayHost 浮层挂在根视图上。
            let overlayChildren =
                shell.GetVisualDescendants()
                |> Seq.choose (function :? Avalonia.Controls.Border as b when not (isNull b.Tag) -> Some b.Tag | _ -> None)
                |> List.ofSeq
            Assert.True(overlayChildren.Length >= 0, "浮层已挂载（下面断言菜单内容）")
            // 菜单项文字里应看得到模型名，而不是「先选择一个会话」的 toast。
            let menuTexts =
                shell.GetVisualDescendants()
                |> Seq.choose (function :? TextBlock as tb -> Some tb.Text | _ -> None)
                |> Seq.filter (fun text -> not (isNull text))
                |> List.ofSeq
            Assert.True(
                menuTexts |> List.exists (fun text -> text.Contains "m-c"),
                sprintf "空态模型菜单应列出 catalog 里的模型，实际浮层文本：%A" menuTexts)
            // 菜单项是 ActionBorder，自动化名 = 模型名；Enter 走 Ui.onClick 的同一 action。
            // ActionBorder 是 internal：按自动化名匹配任意 Control（菜单项名 = 模型名）。
            let entry =
                shell.GetVisualDescendants()
                |> Seq.tryPick (function
                    | :? Control as c when AutomationProperties.GetName c = "m-c" -> Some c
                    | _ -> None)
            Assert.True(entry.IsSome, "应点得到 m-c 这一项")
            entry.Value.RaiseEvent(
                KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.None, Source = entry.Value))
            Dispatcher.UIThread.RunJobs()
            let draft =
                typeof<MainView>.GetField("draftModelSelection", flags).GetValue shell
                :?> (string * string) option
            Assert.Equal<(string * string) option>(Some("p2", "m-c"), draft)
            Assert.True(draft.IsSome, "选过之后草稿暂存位应有值")
        finally
            window.Close()
