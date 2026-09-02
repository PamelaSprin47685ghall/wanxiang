namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Wanxiang.Core

/// 连接对话框的回调。
type ConnectRequest = {
    url: string
    token: string option
    /// true 表示走配对流程而不是直接用令牌
    usePairing: bool
}

/// 应用内对话框。桌面与 PWA 共用，全部画在 OverlayHost 上。
module Dialogs =

    let private actionRow (overlay: OverlayHost) (confirmLabel: string) (tone: Ui.ButtonTone) (onConfirm: unit -> unit) =
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Right)
        row.Children.Add(Ui.button Ui.Ghost "取消" (fun () -> overlay.CloseDialog()))
        row.Children.Add(Ui.button tone confirmLabel onConfirm)
        row

    /// 单行输入对话框（重命名等）。
    let prompt (overlay: OverlayHost) (title: string) (hint: string) (initial: string) (confirmLabel: string) (onConfirm: string -> unit) =
        let shell, box = Ui.textField hint
        box.Text <- initial
        let submit () =
            let value = if isNull box.Text then "" else box.Text.Trim()
            if not (String.IsNullOrWhiteSpace value) then
                overlay.CloseDialog()
                onConfirm value
        box.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                submit ())
        let content =
            Ui.vstack Tokens.space4 [ Ui.title title :> Control; shell :> Control; actionRow overlay confirmLabel Ui.Primary submit :> Control ]
        overlay.ShowDialog(content :> Control, 420.0)
        Dispatcher.UIThread.Post(fun () ->
            box.Focus() |> ignore
            box.SelectAll())

    /// 破坏性操作确认。
    let confirm (overlay: OverlayHost) (title: string) (body: string) (confirmLabel: string) (onConfirm: unit -> unit) =
        let message =
            TextBlock(
                Text = body,
                FontSize = Tokens.fontBody,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = ReadingRhythm.uiBodyLineHeight)
        let content =
            Ui.vstack
                Tokens.space4
                [ Ui.title title :> Control
                  message :> Control
                  actionRow overlay confirmLabel Ui.Danger (fun () ->
                      overlay.CloseDialog()
                      onConfirm ())
                  :> Control ]
        overlay.ShowDialog(content :> Control, 420.0)

    /// 长文本编辑（编辑消息并分叉）。
    let editText (overlay: OverlayHost) (title: string) (initial: string) (confirmLabel: string) (onConfirm: string -> unit) =
        let shell, box = Ui.textArea "消息内容" 160.0
        box.Text <- initial
        let content =
            Ui.vstack
                Tokens.space4
                [ Ui.title title :> Control
                  Ui.caption "会以你编辑后的内容新建一个分叉会话，原会话保持不变。" :> Control
                  shell :> Control
                  actionRow overlay confirmLabel Ui.Primary (fun () ->
                      let value = if isNull box.Text then "" else box.Text
                      overlay.CloseDialog()
                      onConfirm value)
                  :> Control ]
        overlay.ShowDialog(content :> Control, 520.0)
        Dispatcher.UIThread.Post(fun () -> box.Focus() |> ignore)

    /// 连接对话框：地址 + 令牌，或走配对码。
    let connect
        (overlay: OverlayHost)
        (defaultUrl: string)
        (onConnect: ConnectRequest -> unit)
        (onSubmitCode: string -> unit)
        : (string -> unit) =
        let urlField, urlBox = Ui.labeledField "服务器地址" "ws://127.0.0.1:8765/ws"
        urlBox.Text <- defaultUrl
        let tokenField, tokenBox = Ui.labeledField "访问令牌" "有令牌就粘贴，没有就用配对码"
        tokenBox.PasswordChar <- '●'

        let codeShell, codeBox = Ui.textField "6 位配对码"
        codeBox.MaxLength <- 6
        codeBox.FontSize <- Tokens.fontTitle
        codeBox.TextAlignment <- TextAlignment.Center
        codeBox.LetterSpacing <- 8.0
        codeBox.FontFamily <- Tokens.monoFontFamily
        let codeSection =
            Ui.vstack
                Tokens.space2
                [ Ui.fieldLabel "配对码" :> Control
                  codeShell :> Control
                  Ui.caption "配对码会打印在服务端终端（stderr），5 分钟内有效。" :> Control ]
        codeSection.IsVisible <- false

        let status =
            TextBlock(
                Text = "",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = false,
                LineHeight = ReadingRhythm.captionLineHeight)

        let submitCode () =
            let code = if isNull codeBox.Text then "" else codeBox.Text.Trim()
            if code.Length = 6 then onSubmitCode code
        codeBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                submitCode ())

        let codeButton = Ui.button Ui.Secondary "提交配对码" submitCode
        codeButton.IsVisible <- false

        let pairingToggle =
            Ui.button Ui.Ghost "没有令牌？改用配对码" (fun () ->
                let show = not codeSection.IsVisible
                codeSection.IsVisible <- show
                codeButton.IsVisible <- show
                if show then
                    onConnect
                        { url = (if isNull urlBox.Text then "" else urlBox.Text.Trim())
                          token = None
                          usePairing = true }
                    Dispatcher.UIThread.Post(fun () -> codeBox.Focus() |> ignore))
        pairingToggle.HorizontalAlignment <- HorizontalAlignment.Left

        let connectAction () =
            let url = if isNull urlBox.Text then "" else urlBox.Text.Trim()
            let token = if isNull tokenBox.Text then "" else tokenBox.Text.Trim()
            if String.IsNullOrWhiteSpace url then
                status.Text <- "请填写服务器地址。"
                status.IsVisible <- true
            else
                onConnect
                    { url = url
                      token = (if String.IsNullOrWhiteSpace token then None else Some token)
                      usePairing = false }

        let content =
            Ui.vstack
                Tokens.space4
                [ Ui.vstack
                      Tokens.space1
                      [ Ui.title "连接万象服务器" :> Control
                        Ui.caption "服务端负责运行模型、保存会话与管理密钥；客户端只通过 WebSocket 与它通信。" :> Control ]
                  :> Control
                  urlField
                  tokenField
                  status :> Control
                  Ui.hairline () :> Control
                  pairingToggle :> Control
                  codeSection :> Control
                  codeButton :> Control
                  (let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Right)
                   row.Children.Add(Ui.button Ui.Ghost "稍后再说" (fun () -> overlay.CloseDialog()))
                   row.Children.Add(Ui.button Ui.Primary "连接" connectAction)
                   row :> Control) ]
        overlay.ShowDialog(content :> Control, 460.0)
        Dispatcher.UIThread.Post(fun () -> urlBox.Focus() |> ignore)
        fun message ->
            status.Text <- message
            status.IsVisible <- not (String.IsNullOrWhiteSpace message)

    /// 会话设置：模型、生成参数、系统指令、工具勾选。
    let sessionSettings
        (overlay: OverlayHost)
        (catalog: Catalog)
        (current: SessionConfig)
        (onSave: SessionConfig -> unit)
        =
        let mutable providerId = current.provider
        let mutable model = current.model

        let mutable setModelText: string -> unit = ignore
        let modelOptions () =
            [ for provider in Catalog.usableProviders catalog ->
                  provider.label,
                  [ for candidate in provider.models ->
                        MenuEntry.create candidate (fun () ->
                            providerId <- provider.id
                            model <- candidate
                            setModelText (sprintf "%s · %s" provider.label candidate))
                        |> MenuEntry.markSelected (provider.id = providerId && candidate = model) ] ]
        let modelButton, setText =
            Menu.selectButton overlay (Catalog.describeModel providerId model catalog) modelOptions
        setModelText <- setText
        modelButton.HorizontalAlignment <- HorizontalAlignment.Stretch

        let instructionsShell, instructionsBox = Ui.textArea "留空则使用服务端默认指令" 96.0
        instructionsBox.Text <- current.instructions |> Option.defaultValue ""

        let _, temperatureBox = Ui.textField "0–2，留空跟随默认"
        temperatureBox.Text <-
            match current.temperature with
            | Some t -> t.ToString("0.##", Globalization.CultureInfo.InvariantCulture)
            | None -> ""
        let _, topPBox = Ui.textField "0–1，留空跟随默认"
        topPBox.Text <-
            match current.topP with
            | Some p -> p.ToString("0.##", Globalization.CultureInfo.InvariantCulture)
            | None -> ""
        let _, maxTokensBox = Ui.textField "留空不限制"
        maxTokensBox.Text <- (match current.maxTokens with Some m -> string m | None -> "")
        let _, thinkingBudgetBox = Ui.textField "留空跟随默认；0 关闭思维链"
        thinkingBudgetBox.Text <- (match current.thinkingBudget with Some b -> string b | None -> "")

        let selectedTools = System.Collections.Generic.HashSet<string>(current.tools)
        let toolsPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space1)
        if List.isEmpty catalog.tools then
            toolsPanel.Children.Add(Ui.caption "服务端当前没有可用工具。")
        else
            for tool in catalog.tools do
                let toggle, _, _, _ =
                    Ui.toggle
                        (selectedTools.Contains tool.id)
                        (fun value -> if value then selectedTools.Add tool.id |> ignore else selectedTools.Remove tool.id |> ignore)
                Avalonia.Automation.AutomationProperties.SetName(toggle, sprintf "启用工具 %s" tool.label)
                let caption =
                    TextBlock(
                        Text = tool.label,
                        FontSize = Tokens.fontSmall,
                        Foreground = Tokens.text,
                        VerticalAlignment = VerticalAlignment.Center)
                let source = Ui.tag (if tool.source = "mcp" then tool.serverId |> Option.defaultValue "mcp" else "内置")
                let left = Ui.hstack Tokens.space2 [ caption :> Control; source :> Control ]
                let row = DockPanel(LastChildFill = true)
                DockPanel.SetDock(toggle, Dock.Right)
                row.Children.Add toggle
                row.Children.Add left
                toolsPanel.Children.Add row

        let save () =
            for box in [ temperatureBox; topPBox; maxTokensBox; thinkingBudgetBox ] do Ui.clearFieldError box
            let mutable firstInvalid: TextBox option = None
            let invalid (box: TextBox) message =
                Ui.setFieldError box message
                if firstInvalid.IsNone then firstInvalid <- Some box
            let parseFloat (box: TextBox) (valid: float -> bool) message =
                let raw = if isNull box.Text then "" else box.Text.Trim()
                if String.IsNullOrWhiteSpace raw then None
                else
                    match Double.TryParse(raw, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                    | true, value when valid value -> Some value
                    | _ ->
                        invalid box message
                        None
            let parseInt (box: TextBox) (valid: int -> bool) message =
                let raw = if isNull box.Text then "" else box.Text.Trim()
                if String.IsNullOrWhiteSpace raw then None
                else
                    match Int32.TryParse raw with
                    | true, value when valid value -> Some value
                    | _ ->
                        invalid box message
                        None
            let temperature = parseFloat temperatureBox (fun v -> v >= 0.0 && v <= 2.0) "请输入 0–2 之间的数字。"
            let topP = parseFloat topPBox (fun v -> v >= 0.0 && v <= 1.0) "请输入 0–1 之间的数字。"
            let maxTokens = parseInt maxTokensBox (fun v -> v > 0) "请输入正整数。"
            let thinkingBudget = parseInt thinkingBudgetBox (fun v -> v >= 0) "请输入 0 或正整数。"
            let instructions =
                let text = if isNull instructionsBox.Text then "" else instructionsBox.Text.Trim()
                if String.IsNullOrWhiteSpace text then None else Some text
            match firstInvalid with
            | Some box -> box.Focus() |> ignore
            | None ->
                overlay.CloseDialog()
                onSave
                    { current with
                        provider = providerId
                        model = model
                        instructions = instructions
                        temperature = temperature
                        topP = topP
                        maxTokens = maxTokens
                        thinkingBudget = thinkingBudget
                        tools = List.ofSeq selectedTools }

        let paramGrid = Grid(ColumnSpacing = Tokens.space3, RowSpacing = Tokens.space3)
        paramGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        paramGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        for _ in 1 .. 4 do paramGrid.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        let temperatureColumn = Ui.inputFieldGroup "Temperature" "" temperatureBox
        let topPColumn = Ui.inputFieldGroup "Top P" "" topPBox
        let maxTokensColumn = Ui.inputFieldGroup "最大输出 token" "" maxTokensBox
        let thinkingBudgetColumn = Ui.inputFieldGroup "思维链预算（token）" "" thinkingBudgetBox
        paramGrid.Children.Add temperatureColumn
        paramGrid.Children.Add topPColumn
        paramGrid.Children.Add maxTokensColumn
        paramGrid.Children.Add thinkingBudgetColumn
        let applyParamLayout width =
            let single = width > 0.0 && width < LayoutPolicy.formSingleColumnBreakpoint
            paramGrid.ColumnDefinitions[1].Width <-
                if single then GridLength(0.0) else GridLength(1.0, GridUnitType.Star)
            let place (control: Control) row column =
                Grid.SetRow(control, row)
                Grid.SetColumn(control, column)
            if single then
                place temperatureColumn 0 0
                place topPColumn 1 0
                place maxTokensColumn 2 0
                place thinkingBudgetColumn 3 0
            else
                place temperatureColumn 0 0
                place topPColumn 0 1
                place maxTokensColumn 1 0
                place thinkingBudgetColumn 1 1
        paramGrid.PropertyChanged.Add(fun args ->
            if args.Property = Visual.BoundsProperty then applyParamLayout paramGrid.Bounds.Width)

        let content =
            Ui.vstack
                Tokens.space4
                [ Ui.vstack Tokens.space1 [ Ui.title "会话设置" :> Control; Ui.caption "只影响当前会话，不改服务端默认值。" :> Control ] :> Control
                  Ui.controlFieldGroup "模型" "" (modelButton :> Control)
                  Ui.controlFieldGroup "系统指令" "" (instructionsShell :> Control)
                  paramGrid :> Control
                  Ui.hairline () :> Control
                  Ui.vstack Tokens.space2 [ Ui.sectionLabel "可用工具" :> Control; toolsPanel :> Control ] :> Control
                  Ui.hairline () :> Control
                  actionRow overlay "保存" Ui.Primary save :> Control ]
        let scroller =
            ScrollViewer(
                Content = content,
                MaxHeight = LayoutPolicy.dialogContentMaxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        applyParamLayout paramGrid.Bounds.Width
        overlay.ShowDialog(scroller :> Control, 520.0)

    /// 快捷键帮助（分类清晰、对标桌面端成熟软件）。
    let shortcuts (overlay: OverlayHost) =
        let sections =
            [ "全局与导航",
              [ "Ctrl / ⌘ + N", "新建会话"
                "Ctrl / ⌘ + B", "切换侧边栏展开 / 折叠"
                "Ctrl / ⌘ + K", "快速聚焦搜索栏"
                "Ctrl / ⌘ + 1 ~ 9", "快速跳转至对应会话"
                "Ctrl / ⌘ + ,", "打开全局设置"
                "Ctrl / ⌘ + Shift + S", "切换深色 / 浅色主题"
                "Ctrl / ⌘ + /", "显示快捷键帮助" ]
              "输入与会话",
              [ "Enter", "发送消息（或换行，按偏好）"
                "Shift + Enter", "换行输入"
                "Ctrl / ⌘ + Enter", "强制发送消息"
                "Ctrl / ⌘ + Shift + E", "导出当前会话为 Markdown"
                "Esc", "关闭弹层 / 取消编辑 / 停止生成" ] ]
        let contentPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space3)
        for (category, items) in sections do
            contentPanel.Children.Add(Ui.sectionLabel category :> Control)
            let groupRows = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space1)
            for (keys, description) in items do
                let key =
                    Border(
                        Background = Tokens.surface,
                        BorderBrush = Tokens.border,
                        BorderThickness = Thickness 1.0,
                        CornerRadius = CornerRadius Tokens.radiusSm,
                        Padding = Thickness(Tokens.space2, 2.0),
                        MinWidth = 136.0,
                        Child =
                            TextBlock(
                                Text = keys,
                                FontSize = Tokens.fontMicro,
                                FontFamily = Tokens.monoFontFamily,
                                Foreground = Tokens.textMuted,
                                HorizontalAlignment = HorizontalAlignment.Center))
                let caption =
                    TextBlock(
                        Text = description,
                        FontSize = Tokens.fontSmall,
                        Foreground = Tokens.text,
                        VerticalAlignment = VerticalAlignment.Center)
                groupRows.Children.Add(Ui.hstack Tokens.space3 [ key :> Control; caption :> Control ])
            contentPanel.Children.Add groupRows
        let closeBtn = Ui.button Ui.Primary "关闭" (fun () -> overlay.CloseDialog())
        closeBtn.HorizontalAlignment <- HorizontalAlignment.Right
        let scroller =
            ScrollViewer(
                Content = Ui.vstack Tokens.space4 [ Ui.title "键盘快捷键" :> Control; contentPanel :> Control; closeBtn :> Control ],
                MaxHeight = 520.0,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        overlay.ShowDialog(scroller :> Control, 460.0)
