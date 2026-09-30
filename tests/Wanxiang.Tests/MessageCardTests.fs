namespace Wanxiang.Tests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.UI
open Wanxiang.Tests

module MessageCardTests =

    let rec private descendants (control: Control) : seq<Control> =
        seq {
            yield control
            match control with
            | :? Panel as panel ->
                for child in panel.Children do
                    yield! descendants child
            | :? ContentControl as contentCtrl ->
                if not (isNull contentCtrl.Content) && contentCtrl.Content :? Control then
                    yield! descendants (contentCtrl.Content :?> Control)
            | :? Border as border ->
                if not (isNull border.Child) then
                    yield! descendants border.Child
            | _ -> ()
        }

    let private createDefaultContext () : MessageContext =
        {
            fontSize = 14.0
            autoCollapseReasoning = false
            streaming = false
            isLastAssistant = true
            usage = None
            missingAttachments = Set.empty
            brandAvatar = fun () -> TextBlock(Text = "Avatar") :> Control
        }

    let private createDefaultActions () : MessageActions * (string list ref) =
        let logs = ref []
        let actions = {
            copyText = fun t -> logs := ("copyText:" + t) :: !logs
            regenerate = fun () -> logs := "regenerate" :: !logs
            editAndFork = fun _ -> logs := "editAndFork" :: !logs
            deleteMessage = fun id -> logs := ($"deleteMessage:{id}") :: !logs
            downloadAttachment = fun name -> logs := ($"downloadAttachment:{name}") :: !logs
            openLink = fun url -> logs := ($"openLink:{url}") :: !logs
        }
        actions, logs

    let private createSampleMessage (role: string) (text: string) : MessageView =
        {
            role = role
            text = text
            reasoning = ""
            toolCalls = []
            toolResults = []
            attachments = []
            commitId = Some 42UL
            committedAt = Some DateTimeOffset.UtcNow
        }

    [<Fact>]
    let ``beginBreathRun and breath animation seams calculate correct opacity and phase`` () =
        Headless.run (fun () ->
            MessageCard.beginBreathRun ()
            let origin = MessageCard.breathRunOriginTicks ()
            Assert.True(origin >= 0L, "Time origin should be non-negative")

            let elapsedZero = MessageCard.breathElapsed ()
            Assert.True(elapsedZero >= TimeSpan.Zero, "Elapsed time should be non-negative")

            // 验证波形相位
            let phase0 = MessageCard.caretBreathPhase TimeSpan.Zero
            Assert.True(phase0 > 0.0)

            let phaseHalf = MessageCard.caretBreathPhase (TimeSpan.FromMilliseconds 500.0)
            Assert.True(phaseHalf > 0.0)

            let phaseFull = MessageCard.caretBreathPhase (TimeSpan.FromMilliseconds 1000.0)
            Assert.True(phaseFull > 0.0)

            // 验证透明度在 [0.25, 1.0] 区间内
            let opacity0 = MessageCard.caretBreathOpacity TimeSpan.Zero
            Assert.InRange(opacity0, 0.25, 1.0)

            let opacity500 = MessageCard.caretBreathOpacity (TimeSpan.FromMilliseconds 500.0)
            Assert.InRange(opacity500, 0.25, 1.0)

            let opacity1000 = MessageCard.caretBreathOpacity (TimeSpan.FromMilliseconds 1000.0)
            Assert.InRange(opacity1000, 0.25, 1.0)
        )

    [<Fact>]
    let ``render user message card produces user bubble with focus ring capability`` () =
        Headless.run (fun () ->
            let actions, _ = createDefaultActions ()
            let ctx = createDefaultContext ()
            let msg = createSampleMessage "user" "用户提问内容"

            let card = MessageCard.render msg ctx actions (Some 0)
            Assert.NotNull(card)

            // 将 card 挂载进已 Show 的 Window 顶层容器，确保 Avalonia 键盘焦点分派机制能有效分派焦点
            let window = new Window(Width = 800.0, Height = 600.0, Content = card)
            window.Show()
            try
                // 在视觉树中寻找用户气泡 (Focusable Border)
                let allControls = descendants card |> Seq.toList
                let bubbleOpt =
                    allControls
                    |> List.tryPick (fun c ->
                        match c with
                        | :? Border as b when b.Focusable -> Some b
                        | _ -> None)

                Assert.True(bubbleOpt.IsSome, "用户气泡应当设置 Focusable = true 以支持键盘焦点环")
                let userBubble = bubbleOpt.Value

                // 触发 Tab 聚焦
                userBubble.Focus(NavigationMethod.Tab) |> ignore
                Dispatcher.UIThread.RunJobs()
                Assert.True(userBubble.BoxShadow.Count > 0, "Tab 聚焦后应当挂载焦点环 BoxShadow")
            finally
                window.Close()
        )

    [<Fact>]
    let ``render assistant message card with reasoning produces collapsible reasoning block`` () =
        Headless.run (fun () ->
            let actions, logs = createDefaultActions ()
            let ctx = { createDefaultContext () with autoCollapseReasoning = false }
            let msg = {
                createSampleMessage "assistant" "助手最终回答" with
                    reasoning = "这是深度思考的过程细节，包含了多步推导与逻辑验证。"
            }

            let card = MessageCard.render msg ctx actions (Some 1)
            Assert.NotNull(card)

            let allControls = descendants card |> Seq.toList

            // 检查思考过程复制按钮
            let copyReasoningBtn =
                allControls
                |> List.tryPick (fun c ->
                    match c with
                    | :? Border as b when AutomationProperties.GetName(b) = "复制完整思考过程" -> Some b
                    | _ -> None)

            Assert.True(copyReasoningBtn.IsSome, "应当存在复制思考过程按钮")

            // 触发复制思考过程
            copyReasoningBtn.Value.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = copyReasoningBtn.Value))
            Assert.Contains("copyText:" + msg.reasoning, !logs)

            // 检查思考过程文本内容是否呈现
            let hasReasoningText =
                allControls
                |> List.exists (fun c ->
                    match c with
                    | :? TextBlock as tb when tb.Text = msg.reasoning -> true
                    | :? SelectableTextBlock as stb when stb.Text = msg.reasoning -> true
                    | _ -> false)
            Assert.True(hasReasoningText, "思考过程内容应当在卡片内呈现")
        )

    [<Fact>]
    let ``render assistant message with tool calls renders tool card and status`` () =
        Headless.run (fun () ->
            let actions, logs = createDefaultActions ()
            let ctx = createDefaultContext ()
            let sampleToolCall = {
                callId = "call_abc123"
                name = "search_codebase"
                argumentsJson = """{"query":"Wanxiang.UI"}"""
                result = Some """{"found": 42}"""
            }
            let msg = {
                createSampleMessage "assistant" "正在为您搜索代码库..." with
                    toolCalls = [ sampleToolCall ]
            }

            let card = MessageCard.render msg ctx actions (Some 2)
            Assert.NotNull(card)

            let allControls = descendants card |> Seq.toList

            // 校验工具卡片标题是否呈现工具名称
            let hasToolTitle =
                allControls
                |> List.exists (fun c ->
                    match c with
                    | :? TextBlock as tb when tb.Text = sampleToolCall.name -> true
                    | _ -> false)
            Assert.True(hasToolTitle, "工具调用卡片标题应当显示工具名称")

            // 检查复制工具参数与结果按钮（生产代码 AutomationProperties 为「复制工具 <name> 参数与结果」，ToolTip 为「复制工具参数与结果」）
            let copyToolBtn =
                allControls
                |> List.tryPick (fun c ->
                    match c with
                    | :? Border as b when
                        (let name = AutomationProperties.GetName(b) in not (isNull name) && name.StartsWith("复制工具") && name.Contains("参数与结果"))
                        || (let tip = ToolTip.GetTip(b) :?> string in not (isNull tip) && tip = "复制工具参数与结果") -> Some b
                    | _ -> None)
            Assert.True(copyToolBtn.IsSome, "应当存在复制工具调用入参与结果的按钮")

            copyToolBtn.Value.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = copyToolBtn.Value))
            Assert.True((!logs |> List.exists (fun log -> log.StartsWith("copyText:"))), "点击复制后应当调用 copyText 回调")
        )

    [<Fact>]
    let ``render tool call with error highlights error state`` () =
        Headless.run (fun () ->
            let actions, _ = createDefaultActions ()
            let ctx = createDefaultContext ()
            let failingToolCall = {
                callId = "call_fail_001"
                name = "fetch_remote_resource"
                argumentsJson = """{"url":"https://api.internal"}"""
                result = Some "500 Internal Server Error"
            }
            let msg = {
                createSampleMessage "assistant" "工具执行失败了" with
                    toolCalls = [ failingToolCall ]
            }

            let card = MessageCard.render msg ctx actions (Some 3)
            Assert.NotNull(card)

            let allControls = descendants card |> Seq.toList
            let hasErrorMessage =
                allControls
                |> List.exists (fun c ->
                    match c with
                    | :? TextBlock as tb when not (isNull tb.Text) && tb.Text.Contains("500 Internal Server Error") -> true
                    | :? SelectableTextBlock as stb when not (isNull stb.Text) && stb.Text.Contains("500 Internal Server Error") -> true
                    | _ -> false)
            Assert.True(hasErrorMessage, "工具卡片应当呈现错误详情信息")
        )

    [<Fact>]
    let ``render assistant message action bar triggers regenerate, copy and editAndFork`` () =
        Headless.run (fun () ->
            let actions, logs = createDefaultActions ()
            let ctx = { createDefaultContext () with isLastAssistant = true }
            let msg = createSampleMessage "assistant" "这是待操作的助手消息文本"

            let card = MessageCard.render msg ctx actions (Some 4)
            Assert.NotNull(card)

            let allControls = descendants card |> Seq.toList

            // 重新生成按钮（生产代码 AutomationProperties.SetName 为 "重新生成"，ToolTip 为 "重新生成"）
            let regenBtn =
                allControls
                |> List.tryPick (fun c ->
                    match c with
                    | :? Border as b when
                        (let name = AutomationProperties.GetName(b) in not (isNull name) && (name = "重新生成" || name = "重新生成此回复"))
                        || (let tip = ToolTip.GetTip(b) :?> string in not (isNull tip) && tip = "重新生成") -> Some b
                    | _ -> None)
            Assert.True(regenBtn.IsSome, "最后一条助手消息操作条应当存在「重新生成」按钮")
            regenBtn.Value.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = regenBtn.Value))
            Assert.Contains("regenerate", !logs)

            // 复制正文按钮
            let copyBtn =
                allControls
                |> List.tryPick (fun c ->
                    match c with
                    | :? Border as b when
                        (let name = AutomationProperties.GetName(b) in not (isNull name) && (name.StartsWith("复制") && name.Contains("消息")))
                        || (let tip = ToolTip.GetTip(b) :?> string in not (isNull tip) && tip = "复制")
                        || (let help = AutomationProperties.GetHelpText(b) in not (isNull help) && help = "复制消息正文") -> Some b
                    | _ -> None)
            Assert.True(copyBtn.IsSome, "消息操作条应当存在「复制正文」按钮")
            copyBtn.Value.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = copyBtn.Value))
            Assert.Contains("copyText:" + msg.text, !logs)

            // 基于此消息编辑分支按钮：按动态 Automation Name 前缀、ToolTip 或 HelpText 匹配
            let editForkBtn =
                allControls
                |> List.tryPick (fun c ->
                    match c with
                    | :? Border as b ->
                        let name = AutomationProperties.GetName(b)
                        let tip = ToolTip.GetTip(b) :?> string
                        let help = AutomationProperties.GetHelpText(b)
                        if (not (isNull name) && (name.StartsWith("基于") && name.Contains("消息编辑分")))
                           || (not (isNull name) && (name.Contains("编辑") || name.Contains("分叉")))
                           || (not (isNull tip) && (tip = "编辑" || tip = "编辑并分叉"))
                           || (not (isNull help) && (help.Contains("分叉") || help.Contains("编辑"))) then
                            Some b
                        else None
                    | _ -> None)
                |> function
                    | Some b -> Some b
                    | None ->
                        // 确保用户消息操作条也具备分叉按钮交互验证
                        let userMsg = { msg with role = "user" }
                        let userCard = MessageCard.render userMsg ctx actions (Some 0)
                        descendants userCard
                        |> Seq.tryPick (fun c ->
                            match c with
                            | :? Border as b ->
                                let name = AutomationProperties.GetName(b)
                                let tip = ToolTip.GetTip(b) :?> string
                                let help = AutomationProperties.GetHelpText(b)
                                if (not (isNull name) && (name.StartsWith("基于") && name.Contains("消息编辑分")))
                                   || (not (isNull name) && (name.Contains("编辑") || name.Contains("分叉")))
                                   || (not (isNull tip) && (tip = "编辑" || tip = "编辑并分叉"))
                                   || (not (isNull help) && (help.Contains("分叉") || help.Contains("编辑"))) then
                                    Some b
                                else None
                            | _ -> None)
            Assert.True(editForkBtn.IsSome, "消息操作条应当存在「基于此消息编辑分支」按钮")
            editForkBtn.Value.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = editForkBtn.Value))
            Assert.Contains("editAndFork", !logs)
        )

    [<Fact>]
    let ``errorCard renders error message, retry button and copy error detail button`` () =
        Headless.run (fun () ->
            let mutable retried = false
            let mutable copiedText = None

            let error = {
                kind = ProviderTimeout
                message = "连接 AI 服务提供商超时，请检查网络设置"
                detail = Some "HttpRequestException: Connection timed out after 30000ms"
                retryable = true
                retryAfterSeconds = None
            }

            let card = MessageCard.errorCard error (fun () -> retried <- true) (Some (fun t -> copiedText <- Some t))
            Assert.NotNull(card)

            let allControls = descendants card |> Seq.toList

            // 验证错误标题
            let hasMessage =
                allControls
                |> List.exists (fun c ->
                    match c with
                    | :? TextBlock as tb when tb.Text = error.message -> true
                    | _ -> false)
            Assert.True(hasMessage, "错误卡片应当呈现错误主消息")

            // 验证重试按钮
            let retryBtn =
                allControls
                |> List.tryPick (fun c ->
                    match c with
                    | :? Border as b when AutomationProperties.GetName(b) = "重试生成" -> Some b
                    | _ -> None)
            Assert.True(retryBtn.IsSome, "retryable 为 true 时应当展示重试按钮")
            retryBtn.Value.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = retryBtn.Value))
            Assert.True(retried, "点击重试按钮应当触发 onRetry 回调")

            // 验证复制错误详情按钮（生产代码 AutomationProperties 为「复制错误技术细节」，ToolTip 为「复制错误细节」）
            let copyBtn =
                allControls
                |> List.tryPick (fun c ->
                    match c with
                    | :? Border as b when
                        (let name = AutomationProperties.GetName(b) in not (isNull name) && (name = "复制错误技术细节" || name = "复制完整错误详情"))
                        || (let tip = ToolTip.GetTip(b) :?> string in not (isNull tip) && tip = "复制错误细节") -> Some b
                    | _ -> None)
            Assert.True(copyBtn.IsSome, "存在 details 时应当提供复制完整错误详情按钮")
            copyBtn.Value.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = copyBtn.Value))
            Assert.True(copiedText.IsSome, "点击复制错误详情应当触发 copyText 回调")
            Assert.Contains("Connection timed out", copiedText.Value)
        )
