namespace Wanxiang.UI

open System
open System.Globalization
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Layout
open Avalonia.Media
open Wanxiang.Core

/// 生成默认值、外观偏好与关于信息。
///
/// 生成参数落到服务端配置（新会话继承）；外观偏好只存本机
/// （Q195：窗口尺寸与主题是客户端偏好，不进业务 TOML、不经 NDJSON）。
type SettingsGeneral(overlay: OverlayHost, actions: SettingsActions, onPrefsChanged: UiPrefs -> unit) =

    let mutable catalog = Catalog.empty
    let mutable prefs = UiPrefs.defaults

    let temperatureBox = snd (Ui.textField "留空 = 使用服务商默认")
    let topPBox = snd (Ui.textField "0–1，留空不设")
    let maxTokensBox = snd (Ui.textField "留空不限制")
    let contextBox = snd (Ui.textField "200")
    let toolRoundsBox = snd (Ui.textField "12")
    let instructionsBox = snd (Ui.textArea "新会话的默认系统指令（可留空）" 96.0)

    let mutable readAutoTitle: unit -> bool = fun () -> true
    let mutable setAutoTitle: bool -> unit = ignore

    let floatText (value: float option) =
        match value with
        | Some v -> v.ToString("0.##", CultureInfo.InvariantCulture)
        | None -> ""

    let parseFloatOpt (raw: string) =
        if String.IsNullOrWhiteSpace raw then None
        else
            match Double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, v -> Some v
            | _ -> None

    let parseIntOpt (raw: string) =
        if String.IsNullOrWhiteSpace raw then None
        else
            match Int32.TryParse(raw.Trim()) with
            | true, v -> Some v
            | _ -> None

    let labeled (text: string) (hint: string) (control: Control) : Control =
        let column = Ui.vstack 0.0 [ Ui.fieldLabel text :> Control; control ]
        if String.IsNullOrWhiteSpace hint then column :> Control
        else
            let note = Ui.caption hint
            note.Margin <- Thickness(2.0, 3.0, 0.0, 0.0)
            column.Children.Add note
            column :> Control

    let switchRow (title: string) (hint: string) (initial: bool) (onChanged: bool -> unit) : Control * (unit -> bool) * (bool -> unit) =
        let toggle, read, write = Ui.toggle initial onChanged
        let caption = Ui.label title
        let note = Ui.caption hint
        let column = Ui.vstack 1.0 [ caption :> Control; note :> Control ]
        let row = DockPanel(LastChildFill = true)
        DockPanel.SetDock(toggle, Dock.Right)
        row.Children.Add toggle
        row.Children.Add column
        row :> Control, read, write

    member this.SetCatalog(next: Catalog) =
        catalog <- next
        let g = next.generation
        temperatureBox.Text <- floatText g.temperature
        topPBox.Text <- floatText g.topP
        maxTokensBox.Text <- (match g.maxTokens with Some v -> string v | None -> "")
        contextBox.Text <- string g.maxContextMessages
        toolRoundsBox.Text <- string g.maxToolRounds
        instructionsBox.Text <- g.instructions |> Option.defaultValue ""
        setAutoTitle g.autoTitle

    member this.SetPrefs(next: UiPrefs) = prefs <- next

    member private this.SaveGeneration() =
        let payload = JsonObject()
        match parseFloatOpt temperatureBox.Text with
        | Some v -> payload["temperature"] <- v
        | None -> payload["temperature"] <- null
        match parseFloatOpt topPBox.Text with
        | Some v -> payload["topP"] <- v
        | None -> payload["topP"] <- null
        match parseIntOpt maxTokensBox.Text with
        | Some v -> payload["maxTokens"] <- v
        | None -> payload["maxTokens"] <- null
        let instructions = if isNull instructionsBox.Text then "" else instructionsBox.Text.Trim()
        payload["instructions"] <- if String.IsNullOrWhiteSpace instructions then null else JsonNode.op_Implicit instructions
        payload["maxContextMessages"] <- (parseIntOpt contextBox.Text |> Option.defaultValue catalog.generation.maxContextMessages)
        payload["maxToolRounds"] <- (parseIntOpt toolRoundsBox.Text |> Option.defaultValue catalog.generation.maxToolRounds)
        payload["autoTitle"] <- readAutoTitle ()
        actions.updateGeneration payload

    member this.BuildGeneration() : Control =
        let autoTitleRow, readAuto, writeAuto =
            switchRow "自动生成会话标题" "首轮对话后用同一个模型起一个短标题；失败时回落到你的第一句话。" true ignore
        readAutoTitle <- readAuto
        setAutoTitle <- writeAuto
        let saveButton = Ui.button Ui.Primary "保存生成设置" (fun () -> this.SaveGeneration())
        saveButton.HorizontalAlignment <- HorizontalAlignment.Left
        let grid = Grid(ColumnSpacing = Tokens.space3, RowSpacing = Tokens.space3)
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        let add (control: Control) (row: int) (column: int) =
            Grid.SetRow(control, row)
            Grid.SetColumn(control, column)
            grid.Children.Add control
        grid.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        grid.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        grid.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        add (labeled "Temperature" "越高越发散。留空则用服务商默认值。" (temperatureBox.Parent :?> Control)) 0 0
        add (labeled "Top P" "核采样阈值，0–1。" (topPBox.Parent :?> Control)) 0 1
        add (labeled "最大输出 token" "限制单次回复长度。" (maxTokensBox.Parent :?> Control)) 1 0
        add (labeled "上下文消息上限" "每次请求最多携带多少条历史，防止长会话撞上模型上限。" (contextBox.Parent :?> Control)) 1 1
        add (labeled "工具调用轮数上限" "模型连续调用工具超过这个轮数就中止本次生成。" (toolRoundsBox.Parent :?> Control)) 2 0
        Ui.vstack
            Tokens.space4
            [ Ui.vstack
                  Tokens.space1
                  [ Ui.heading "生成" :> Control
                    Ui.caption "这些是新会话的默认值。单个会话可以在会话设置里单独调整。" :> Control ]
              :> Control
              grid :> Control
              labeled "默认系统指令" "会作为 system 消息随每次请求发送。" (instructionsBox.Parent :?> Control)
              autoTitleRow
              Ui.hairline () :> Control
              saveButton :> Control ]
        :> Control

    member this.BuildAppearance() : Control =
        let themeButton, setThemeText =
            Menu.selectButton
                overlay
                (ThemePreference.label prefs.theme)
                (fun () ->
                    [ "主题",
                      [ FollowSystem; AlwaysLight; AlwaysDark ]
                      |> List.map (fun option ->
                          MenuEntry.create (ThemePreference.label option) (fun () ->
                              prefs <- { prefs with theme = option }
                              onPrefsChanged prefs)
                          |> MenuEntry.markSelected (prefs.theme = option)) ])
        themeButton.HorizontalAlignment <- HorizontalAlignment.Left
        Tokens.Changed.Publish.Add(fun _ -> setThemeText (ThemePreference.label prefs.theme))

        let fontSizeCaption =
            TextBlock(
                Text = sprintf "%.1f pt" prefs.fontScale,
                FontSize = Tokens.fontSmall,
                Foreground = Tokens.textMuted,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 56.0)
        let adjust (delta: float) =
            let next = Math.Clamp(prefs.fontScale + delta, UiPrefs.minFontScale, UiPrefs.maxFontScale)
            if abs (next - prefs.fontScale) > 0.01 then
                prefs <- { prefs with fontScale = next }
                fontSizeCaption.Text <- sprintf "%.1f pt" next
                onPrefsChanged prefs
        let smaller = Ui.iconButton Icons.minus "更小"
        Ui.onClick smaller (fun () -> adjust -0.5)
        let larger = Ui.iconButton Icons.plus "更大"
        Ui.onClick larger (fun () -> adjust 0.5)
        let fontRow =
            let controls = Ui.hstack Tokens.space1 [ smaller :> Control; fontSizeCaption :> Control; larger :> Control ]
            let column = Ui.vstack 1.0 [ Ui.label "消息字号" :> Control; Ui.caption "影响对话正文与代码块。" :> Control ]
            let row = DockPanel(LastChildFill = true)
            DockPanel.SetDock(controls, Dock.Right)
            row.Children.Add controls
            row.Children.Add column
            row :> Control

        let enterRow, _, _ =
            switchRow "Enter 直接发送" "关闭后用 Ctrl+Enter 发送，Enter 换行。" prefs.enterSends (fun value ->
                prefs <- { prefs with enterSends = value }
                onPrefsChanged prefs)
        let collapseRow, _, _ =
            switchRow "完成后收起思考过程" "生成结束时自动折叠模型的思维链。" prefs.autoCollapseReasoning (fun value ->
                prefs <- { prefs with autoCollapseReasoning = value }
                onPrefsChanged prefs)
        let codeWrapRow, _, _ =
            switchRow "代码块长行折行" "关闭时长行横向滚动；开启后折行显示，一屏读完。" prefs.codeWrap (fun value ->
                prefs <- { prefs with codeWrap = value }
                onPrefsChanged prefs)
        let archivedRow, _, _ =
            switchRow "在侧栏显示已归档会话" "归档会话默认收起，避免长列表。" prefs.showArchived (fun value ->
                prefs <- { prefs with showArchived = value }
                onPrefsChanged prefs)

        Ui.vstack
            Tokens.space4
            [ Ui.vstack
                  Tokens.space1
                  [ Ui.heading "外观与交互" :> Control
                    Ui.caption "这些偏好只保存在本机，不会同步到服务端。" :> Control ]
              :> Control
              labeled "主题" "深色主题是同一套纸感在低光下的版本。" (themeButton :> Control)
              fontRow
              Ui.hairline () :> Control
              enterRow
              collapseRow
              codeWrapRow
              archivedRow ]
        :> Control

    member this.BuildAbout(instanceId: string, serverUrl: string) : Control =
        let row (key: string) (value: string) : Control =
            let k = TextBlock(Text = key, FontSize = Tokens.fontSmall, Foreground = Tokens.textMuted, MinWidth = 110.0)
            let v =
                SelectableTextBlock(
                    Text = value,
                    FontSize = Tokens.fontSmall,
                    FontFamily = Tokens.monoFontFamily,
                    Foreground = Tokens.text,
                    TextWrapping = TextWrapping.Wrap,
                    SelectionBrush = Tokens.accentSoft)
            Ui.hstack Tokens.space3 [ k :> Control; v :> Control ] :> Control
        let platformDesc = if OperatingSystem.IsBrowser() then "WebAssembly (PWA)" else sprintf "Desktop (.NET %s)" (Environment.Version.ToString())
        let osDesc = Environment.OSVersion.ToString()
        let copyDiagButton = Ui.button Ui.Secondary "复制诊断报告" (fun () ->
            let diag =
                sprintf "万象系统诊断报告\n- 协议版本: %d\n- 日志格式: %d\n- 运行环境: %s\n- 操作系统: %s\n- 实例标识: %s\n- 连接端点: %s\n- 报告时间: %s"
                    Constants.ProtocolVersion
                    Constants.FormatVersion
                    platformDesc
                    osDesc
                    (if String.IsNullOrWhiteSpace instanceId then "未连接" else instanceId)
                    (if String.IsNullOrWhiteSpace serverUrl then "未连接" else serverUrl)
                    (DateTimeOffset.UtcNow.ToString("O"))
            actions.toast "诊断报告已生成" Success
            match TopLevel.GetTopLevel overlay.Root with
            | null -> ()
            | top ->
                match top.Clipboard with
                | null -> ()
                | clip -> clip.SetTextAsync diag |> ignore)
        copyDiagButton.HorizontalAlignment <- HorizontalAlignment.Left
        Ui.vstack
            Tokens.space4
            [ Ui.vstack Tokens.space1 [ Ui.heading "关于与系统诊断" :> Control ] :> Control
              Ui.card(
                  Ui.vstack
                      Tokens.space2
                      [ row "协议版本" (string Constants.ProtocolVersion)
                        row "日志格式版本" (string Constants.FormatVersion)
                        row "客户端平台" platformDesc
                        row "操作系统" osDesc
                        row "服务器实例" (if String.IsNullOrWhiteSpace instanceId then "未连接" else instanceId)
                        row "连接地址" (if String.IsNullOrWhiteSpace serverUrl then "未连接" else serverUrl)
                        Border(Height = Tokens.space2) :> Control
                        copyDiagButton :> Control ])
              :> Control
              Ui.card(
                  Ui.vstack
                      Tokens.space2
                      [ Ui.label "设计与实现" :> Control
                        Ui.caption
                            "万象是独立实现的对等 C/S 聊天客户端：每个实例既是服务端也是客户端，"
                        :> Control
                        Ui.caption "会话以 NDJSON 事件日志为唯一权威，界面只是投影的一个视图。" :> Control ])
              :> Control
              Ui.card(
                  Ui.vstack
                      Tokens.space2
                      [ Ui.label "致谢" :> Control
                        Ui.caption "界面思路受 Kelivo 启发，代码与数据模型均为独立实现。" :> Control
                        Ui.caption "内嵌字体 Sarasa Term SC 采用 OFL-1.1 许可。" :> Control ])
              :> Control ]
        :> Control
