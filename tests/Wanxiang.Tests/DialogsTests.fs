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

module DialogsTests =

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

    let private click (control: Control) =
        control.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = control))

    let private findButton (root: Control) (text: string) : Border option =
        descendants root
        |> Seq.tryPick (fun c ->
            match c with
            | :? Border as b when AutomationProperties.GetName(b) = text -> Some b
            | _ -> None)

    [<Fact>]
    let ``prompt dialog validates input and handles confirm and cancel`` () =
        Headless.run (fun () ->
            let root = Grid()
            let overlay = OverlayHost(root)
            let mutable confirmedValue = None

            let mutable confirmedValue = None

            Dialogs.prompt
                overlay
                "重命名会话"
                "请输入新的会话标题"
                "旧标题"
                "确定"
                (fun v onComplete ->
                    confirmedValue <- Some v
                    onComplete true)

            Dispatcher.UIThread.RunJobs()
            Assert.True(overlay.IsDialogOpen, "对话框应当已挂载并处于打开状态")

            let allControls = descendants overlay.Root |> Seq.toList

            // 查找输入框
            let input =
                allControls
                |> List.pick (fun c ->
                    match c with
                    | :? TextBox as tb when tb.Text = "旧标题" -> Some tb
                    | _ -> None)

            // 校验清空后提交应当触发错误提示且不触发 confirmedValue
            input.Text <- "   "
            Dispatcher.UIThread.RunJobs()

            let okBtn = findButton overlay.Root "确定"
            Assert.True(okBtn.IsSome, "应当存在确定按钮")
            click okBtn.Value
            Dispatcher.UIThread.RunJobs()
            Assert.True(confirmedValue.IsNone, "校验失败时不应触发 onConfirm 回调")
            Assert.True(overlay.IsDialogOpen, "校验失败时对话框应当保持打开")

            // 检查错误文本提示是否展示（匹配真实文案 "请输入非空内容" 或 "输入内容不能全为空白字符。" 或包含 "非空" / "空白"）
            let hasValidationError =
                descendants overlay.Root
                |> Seq.exists (fun c ->
                    match c with
                    | :? TextBlock as tb when
                        not (String.IsNullOrEmpty tb.Text)
                        && (tb.Text.Contains("非空") || tb.Text.Contains("空白") || tb.Text.Contains("不能为空") || tb.Text.Contains("有效文本")) -> true
                    | _ -> false)
            Assert.True(hasValidationError, "输入空白字符时应当呈现非空提示")

            // 校验彻底清空输入时呈现内容不能为空提示
            input.Text <- ""
            Dispatcher.UIThread.RunJobs()
            click okBtn.Value
            Dispatcher.UIThread.RunJobs()
            Assert.True(confirmedValue.IsNone, "校验失败时不应触发 onConfirm 回调")
            let hasEmptyError =
                descendants overlay.Root
                |> Seq.exists (fun c ->
                    match c with
                    | :? TextBlock as tb when
                        not (String.IsNullOrEmpty tb.Text)
                        && (tb.Text.Contains("不能为空") || tb.Text.Contains("非空") || tb.Text.Contains("有效文本")) -> true
                    | _ -> false)
            Assert.True(hasEmptyError, "清空输入时应当呈现内容不能为空提示")

            // 重新填入合法值并提交
            input.Text <- "全新合规标题"
            click okBtn.Value
            Assert.Equal(Some "全新合规标题", confirmedValue)
            Assert.False(overlay.IsDialogOpen, "合规提交后对话框应当关闭")
        )

    [<Fact>]
    let ``confirm and confirmWithFocus dialog trigger callbacks correctly`` () =
        Headless.run (fun () ->
            let root = Grid()
            let overlay = OverlayHost(root)
            let mutable confirmed = false

            // 1. 验证取消按钮
            Dialogs.confirm
                overlay
                "删除会话"
                "确定要删除此会话吗？此操作无法撤销。"
                "删除"
                (fun () -> confirmed <- true)

            Dispatcher.UIThread.RunJobs()
            Assert.True(overlay.IsDialogOpen)

            let cancelBtn = findButton overlay.Root "取消"
            Assert.True(cancelBtn.IsSome)
            click cancelBtn.Value
            Assert.False(confirmed, "点击取消不应触发回调")
            Assert.False(overlay.IsDialogOpen, "点击取消后对话框应当关闭")

            // 2. 验证 confirmWithFocus 确定操作
            let mutable confirmedWithFocus = false
            Dialogs.confirmWithFocus
                overlay
                "确认重置"
                "重置所有配置项？"
                "确认重置"
                (fun () -> confirmedWithFocus <- true)
                true

            Dispatcher.UIThread.RunJobs()
            Assert.True(overlay.IsDialogOpen)

            let confirmBtn = findButton overlay.Root "确认重置"
            Assert.True(confirmBtn.IsSome)
            click confirmBtn.Value
            Assert.True(confirmedWithFocus, "点击确认应触发回调")
            Assert.False(overlay.IsDialogOpen, "点击确认后对话框应当关闭")
        )

    [<Fact>]
    let ``editText dialog loads text and submits on confirm`` () =
        Headless.run (fun () ->
            let root = Grid()
            let overlay = OverlayHost(root)
            let mutable resultText = None

            Dialogs.editText
                overlay
                "编辑消息"
                "初始草稿正文"
                "确定"
                (fun t -> resultText <- Some t)

            Dispatcher.UIThread.RunJobs()
            Assert.True(overlay.IsDialogOpen)

            let tb =
                descendants overlay.Root
                |> Seq.pick (fun c ->
                    match c with
                    | :? TextBox as box when box.Text = "初始草稿正文" -> Some box
                    | _ -> None)

            tb.Text <- "修改后的草稿正文"
            let okBtn = findButton overlay.Root "确定"
            Assert.True(okBtn.IsSome)
            click okBtn.Value

            Assert.Equal(Some "修改后的草稿正文", resultText)
            Assert.False(overlay.IsDialogOpen)
        )

    [<Fact>]
    let ``connect dialog validates pairing code and token connections`` () =
        Headless.run (fun () ->
            let root = Grid()
            let overlay = OverlayHost(root)
            let mutable connectReq: ConnectRequest option = None
            let mutable codeSubmitted: string option = None

            let announce =
                Dialogs.connect
                    overlay
                    "ws://127.0.0.1:8765/ws"
                    (fun req -> connectReq <- Some req)
                    (fun code -> codeSubmitted <- Some code)

            Dispatcher.UIThread.RunJobs()
            Assert.True(overlay.IsDialogOpen)

            // 1. 普通模式连接提交
            let connectBtn = findButton overlay.Root "连接"
            Assert.True(connectBtn.IsSome)
            click connectBtn.Value

            Assert.True(connectReq.IsSome)
            Assert.Equal("ws://127.0.0.1:8765/ws", connectReq.Value.url)
            Assert.False(connectReq.Value.usePairing)
            Assert.True(overlay.IsDialogOpen, "发起连接后等待结果期间对话框保持打开")

            // 2. 配对码模式验证
            let root2 = Grid()
            let overlay2 = OverlayHost(root2)
            let mutable pairingConnectReq: ConnectRequest option = None
            let mutable pairingCodeSubmitted: string option = None

            Dialogs.connect
                overlay2
                "ws://127.0.0.1:8765/ws"
                (fun req -> pairingConnectReq <- Some req)
                (fun code -> pairingCodeSubmitted <- Some code)
                |> ignore

            Dispatcher.UIThread.RunJobs()

            // 展开配对码面板
            let pairingToggle = findButton overlay2.Root "没有令牌？改用配对码"
            Assert.True(pairingToggle.IsSome)
            click pairingToggle.Value

            // 校验切换到配对码时触发了 usePairing = true
            Assert.True(pairingConnectReq.IsSome)
            Assert.True(pairingConnectReq.Value.usePairing)

            // 查找配对码输入框
            let codeBox =
                descendants overlay2.Root
                |> Seq.pick (fun c ->
                    match c with
                    | :? TextBox as tb when tb.MaxLength = 6 -> Some tb
                    | _ -> None)

            let pairOkBtn = findButton overlay2.Root "提交配对码"
            Assert.True(pairOkBtn.IsSome)

            // 校验非 6 位输入拒绝
            codeBox.Text <- "123"
            click pairOkBtn.Value
            Assert.True(pairingCodeSubmitted.IsNone, "配对码位数不足时不得触发提交")

            // 校验合规 6 位通过
            codeBox.Text <- "123456"
            click pairOkBtn.Value
            Assert.Equal(Some "123456", pairingCodeSubmitted)
        )

    [<Fact>]
    let ``sessionSettings dialog validates numeric bounds and updates config`` () =
        Headless.run (fun () ->
            let root = Grid()
            let overlay = OverlayHost(root)
            let mutable savedConfig: SessionConfig option = None

            let sampleCatalog = {
                Catalog.empty with
                    providers = [
                        {
                            id = "openai"
                            label = "OpenAI"
                            kind = "openai"
                            baseUrl = "https://api.openai.com"
                            hasApiKey = true
                            models = [ "gpt-4o"; "gpt-4o-mini" ]
                            defaultModel = "gpt-4o"
                            timeoutSeconds = 60
                            maxRetries = 2
                            enabled = true
                        }
                    ]
                    tools = [
                        {
                            id = "builtin:fs"
                            label = "文件系统"
                            description = "读写本地文件"
                            source = "builtin"
                            serverId = None
                        }
                    ]
            }

            let initialConfig = {
                SessionConfig.empty with
                    provider = "openai"
                    model = "gpt-4o"
                    temperature = Some 0.7
                    topP = Some 0.9
                    maxTokens = Some 2048
                    thinkingBudget = Some 1000
                    tools = [ "builtin:fs" ]
                    instructions = Some "遵循用户指示"
            }

            Dialogs.sessionSettings
                overlay
                sampleCatalog
                initialConfig
                (fun cfg onComplete ->
                    savedConfig <- Some cfg
                    onComplete true)

            Dispatcher.UIThread.RunJobs()
            Assert.True(overlay.IsDialogOpen)

            let allBoxes = descendants overlay.Root |> Seq.choose (fun c -> match c with :? TextBox as tb -> Some tb | _ -> None) |> Seq.toList

            let tempBox = allBoxes |> List.find (fun b -> b.Text = "0.7")
            let topPBox = allBoxes |> List.find (fun b -> b.Text = "0.9")
            let maxTokensBox = allBoxes |> List.find (fun b -> b.Text = "2048")
            let budgetBox = allBoxes |> List.find (fun b -> b.Text = "1000")

            let saveBtn = findButton overlay.Root "保存"
            Assert.True(saveBtn.IsSome)

            // 1. 测试温度超出 0-2 范围
            tempBox.Text <- "3.5"
            click saveBtn.Value
            Assert.True(savedConfig.IsNone, "温度超出合法范围不得保存")
            Assert.True(overlay.IsDialogOpen)

            // 恢复温度，测试 topP 超出 0-1 范围
            tempBox.Text <- "0.8"
            topPBox.Text <- "1.5"
            click saveBtn.Value
            Assert.True(savedConfig.IsNone, "topP 超出合法范围不得保存")

            // 恢复 topP，测试 maxTokens 非正整数
            topPBox.Text <- "0.95"
            maxTokensBox.Text <- "-50"
            click saveBtn.Value
            Assert.True(savedConfig.IsNone, "maxTokens 小于等于 0 不得保存")

            // 恢复 maxTokens，测试 thinkingBudget 负数
            maxTokensBox.Text <- "4096"
            budgetBox.Text <- "-1"
            click saveBtn.Value
            Assert.True(savedConfig.IsNone, "thinkingBudget 小于 0 不得保存")

            // 恢复合法 thinkingBudget 并保存
            budgetBox.Text <- "2000"
            click saveBtn.Value

            Assert.True(savedConfig.IsSome, "所有表单项均合法时应当成功保存")
            Assert.Equal(Some 0.8, savedConfig.Value.temperature)
            Assert.Equal(Some 0.95, savedConfig.Value.topP)
            Assert.Equal(Some 4096, savedConfig.Value.maxTokens)
            Assert.Equal(Some 2000, savedConfig.Value.thinkingBudget)
            Assert.False(overlay.IsDialogOpen)
        )

    [<Fact>]
    let ``shortcuts dialog displays sections and closes on button click`` () =
        Headless.run (fun () ->
            let root = Grid()
            let overlay = OverlayHost(root)

            Dialogs.shortcuts overlay
            Dispatcher.UIThread.RunJobs()
            Assert.True(overlay.IsDialogOpen)

            // 验证关键快捷键说明存在
            let hasNewSessionShortcut =
                descendants overlay.Root
                |> Seq.exists (fun c ->
                    match c with
                    | :? TextBlock as tb when not (isNull tb.Text) && tb.Text.Contains("新建会话") -> true
                    | _ -> false)
            Assert.True(hasNewSessionShortcut, "应当显示全局快捷键帮助")

            // 点击关闭
            let closeBtn = findButton overlay.Root "关闭"
            Assert.True(closeBtn.IsSome)
            click closeBtn.Value
            Assert.False(overlay.IsDialogOpen, "点击关闭后对话框应当关闭")
        )
