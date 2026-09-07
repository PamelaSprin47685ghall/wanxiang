namespace Wanxiang.UI

open System
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open System.Text.RegularExpressions

/// 工具与 MCP 设置面板。
///
/// 工具清单由服务端发现后下发（内置 + 每个 MCP 服务器的 tools/list），
/// 因此这里展示的就是模型真正能调用的东西，不是一张手抄的名字表。
type SettingsTools(overlay: OverlayHost, actions: SettingsActions) =

    let builtinPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2)
    let mcpPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2)
    let sandboxNote =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontCaption,
            Foreground = Tokens.textMuted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = ReadingRhythm.captionLineHeight)
    let mutable catalog = Catalog.empty

    /// 外壳反馈只切描边颜色与外阴影（focusRingSpread），不动厚度与内边距。
    /// 错误行只在自己组内与 hint 互换（Ui.fieldGroup），不挤占兄弟。
    let syncShellVisual (box: TextBox) =
        let msg = Ui.fieldValidationMessage box
        match box.Parent with
        | :? Border as shell ->
            if msg.IsVisible then
                shell.BorderBrush <- Tokens.danger
                shell.BoxShadow <-
                    if box.IsFocused then
                        BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.dangerSoft.Color))
                    else
                        BoxShadows()
            elif box.IsFocused then
                shell.BorderBrush <- Tokens.accent
                shell.BoxShadow <- BoxShadows(BoxShadow(Spread = Tokens.focusRingSpread, Color = Tokens.accentSoft.Color))
            else
                shell.BorderBrush <- Tokens.border
                shell.BoxShadow <- BoxShadows()
        | _ -> ()

    let attachFieldValidation (box: TextBox) =
        // 错误文案的样式与“用户一改即清”由 Ui 拥有；这里只同步外壳，不重复清错。
        Ui.fieldValidationMessage box |> ignore
        box.GetObservable(TextBox.TextProperty).Subscribe(fun _ -> syncShellVisual box) |> ignore
        box.GotFocus.Add(fun _ -> syncShellVisual box)
        box.LostFocus.Add(fun _ -> syncShellVisual box)

    let applyFieldError (box: TextBox) (errorText: string) =
        Ui.setFieldError box errorText
        syncShellVisual box

    let clearFieldError (box: TextBox) =
        Ui.clearFieldError box
        syncShellVisual box

    let mcpPayload (id: string) (label: string) (command: string) (args: string list) (url: string) (timeout: int) (enabled: bool) =
        let o = JsonObject()
        o["id"] <- id
        o["label"] <- label
        if not (String.IsNullOrWhiteSpace command) then o["command"] <- command
        if not (List.isEmpty args) then
            o["args"] <- JsonArray([| for a in args -> JsonNode.op_Implicit a |])
        if not (String.IsNullOrWhiteSpace url) then o["url"] <- url
        o["callTimeoutSeconds"] <- timeout
        o["enabled"] <- enabled
        o

    member private this.ShowEditor(existing: McpInfo option) =
        let takenIds = catalog.mcpServers |> List.map (fun s -> s.id)
        let mutable editorActive = true
        let _, idBox = Ui.textField "例如 filesystem"
        let idField = Ui.inputFieldGroup "稳定标识" "仅限字母、数字、下划线与连字符。" idBox
        let _, labelBox = Ui.textField "在界面上怎么称呼它"
        let labelField = Ui.inputFieldGroup "显示名称" "仅用于界面显示。" labelBox
        let _, commandBox = Ui.textField "例如 npx"
        let commandField = Ui.inputFieldGroup "本地命令" "本地启动命令；与远程端点二选一。" commandBox
        let _, argsBox = Ui.textField "每行一个参数"
        let argsField = Ui.inputFieldGroup "命令参数" "每行一个参数。" argsBox
        argsBox.AcceptsReturn <- true
        argsBox.MinHeight <- 72.0
        argsBox.VerticalContentAlignment <- VerticalAlignment.Top
        let _, urlBox = Ui.textField "https://example.com/mcp"
        let urlField = Ui.inputFieldGroup "远程端点" "http(s) 地址；与本地命令二选一。" urlBox
        let _, timeoutBox = Ui.textField "60"
        let timeoutField = Ui.inputFieldGroup "单次调用超时（秒）" "1–3600 秒。" timeoutBox
        let allBoxes = [ idBox; labelBox; commandBox; argsBox; urlBox; timeoutBox ]
        for box in allBoxes do attachFieldValidation box


        match existing with
        | Some server ->
            idBox.Text <- server.id
            idBox.IsEnabled <- false
            labelBox.Text <- server.label
            commandBox.Text <- server.command |> Option.defaultValue ""
            argsBox.Text <- String.Join("\n", server.args)
            urlBox.Text <- server.url |> Option.defaultValue ""
            timeoutBox.Text <- string server.callTimeoutSeconds
        | None -> timeoutBox.Text <- "60"

        let enabledToggle, readEnabled, _, _ =
            Ui.toggle (existing |> Option.map (fun s -> s.enabled) |> Option.defaultValue true) ignore
        Avalonia.Automation.AutomationProperties.SetName(enabledToggle, "启用这个服务器")
        let enabledRow =
            let row = DockPanel(LastChildFill = false)
            let caption = Ui.label "启用这个服务器"
            DockPanel.SetDock(caption, Dock.Left)
            DockPanel.SetDock(enabledToggle, Dock.Right)
            row.Children.Add caption
            row.Children.Add enabledToggle
            row

        let idleSaveText = if existing.IsSome then "保存" else "添加"
        let mutable setPending: bool -> unit = ignore
        let save () =
            for box in allBoxes do clearFieldError box
            let id = if isNull idBox.Text then "" else idBox.Text.Trim()
            let label = if isNull labelBox.Text then "" else labelBox.Text.Trim()
            let command = if isNull commandBox.Text then "" else commandBox.Text.Trim()
            let url = if isNull urlBox.Text then "" else urlBox.Text.Trim()
            let args =
                (if isNull argsBox.Text then "" else argsBox.Text)
                    .Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> List.ofArray
            let rawTimeout = if isNull timeoutBox.Text then "" else timeoutBox.Text.Trim()
            let mutable firstInvalid: TextBox option = None
            let fail (box: TextBox) msg =
                applyFieldError box msg
                if firstInvalid.IsNone then firstInvalid <- Some box

            if String.IsNullOrWhiteSpace id then
                fail idBox "稳定标识不能为空。"
            elif existing.IsNone && List.contains id takenIds then
                fail idBox "该标识已存在，请换一个。"
            elif not (Regex.IsMatch(id, "^[a-zA-Z0-9_-]+$")) then
                fail idBox "稳定标识仅支持英文字母、数字、下划线(_)与连字符(-)。"

            if String.IsNullOrWhiteSpace label then
                fail labelBox "显示名称不能为空。"

            if String.IsNullOrWhiteSpace command && String.IsNullOrWhiteSpace url then
                fail commandBox "本地命令与远程端点至少填一个。"
                fail urlBox "本地命令与远程端点至少填一个。"
            elif not (String.IsNullOrWhiteSpace command) && not (String.IsNullOrWhiteSpace url) then
                fail commandBox "与远程端点只能填写一个。"
                fail urlBox "与本地命令只能填写一个。"
            elif not (String.IsNullOrWhiteSpace url) then
                if not (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) then
                    fail urlBox "远程端点地址必须以 http:// 或 https:// 开头。"
                else
                    match Uri.TryCreate(url, UriKind.Absolute) with
                    | true, uri when uri.Scheme = "http" || uri.Scheme = "https" -> ()
                    | _ -> fail urlBox "请输入合法的 HTTP/HTTPS 远程端点地址。"

            if String.IsNullOrWhiteSpace rawTimeout then
                fail timeoutBox "调用超时时间不能为空。"
            else
                match Int32.TryParse rawTimeout with
                | true, t when t >= 1 && t <= 3600 -> ()
                | true, _ -> fail timeoutBox "请输入 1 到 3600 之间的秒数。"
                | false, _ -> fail timeoutBox "请输入有效的正整数秒数（1–3600）。"

            match firstInvalid with
            | Some box ->
                let errorMsg = (Ui.fieldValidationMessage box).Text
                if not (String.IsNullOrWhiteSpace errorMsg) then
                    actions.toast errorMsg Warning
                // 错误行只展开自己所在的组（hint 与 error 互换），边框粗细不变；
                // 这里只把焦点送到第一个错误处，不碰兄弟。
                box.BringIntoView()
                box.Focus(NavigationMethod.Directional) |> ignore
                Dispatcher.UIThread.Post(fun () ->
                    if box.IsEffectivelyVisible && box.IsEnabled then
                        box.BringIntoView()
                        box.Focus(NavigationMethod.Directional) |> ignore)
            | None ->
                let timeout = match Int32.TryParse rawTimeout with true, t -> t | _ -> 60
                setPending true
                actions.upsertMcp
                    (mcpPayload id label command args url timeout (readEnabled ()))
                    (fun ok ->
                        setPending false
                        if ok && editorActive then overlay.CloseDialog())

        let cancelButton = Ui.button Ui.Ghost "取消" (fun () -> overlay.CloseDialog())
        cancelButton.Margin <- Thickness 0.0
        cancelButton.Focusable <- true
        Avalonia.Automation.AutomationProperties.SetName(cancelButton, "取消")
        Avalonia.Automation.AutomationProperties.SetHelpText(cancelButton, "取消并关闭 MCP 服务器编辑 (Esc)")
        ToolTip.SetTip(cancelButton, "取消并关闭 (Esc)")
        let saveButton = Ui.button Ui.Primary idleSaveText save
        saveButton.Margin <- Thickness 0.0
        saveButton.Focusable <- true
        Avalonia.Automation.AutomationProperties.SetName(saveButton, idleSaveText)
        Avalonia.Automation.AutomationProperties.SetHelpText(saveButton, sprintf "确认%s MCP 服务器设置 (Ctrl+Enter)" idleSaveText)
        ToolTip.SetTip(saveButton, sprintf "确认%s (Ctrl+Enter)" idleSaveText)
        Ui.preparePendingButton saveButton
        setPending <- fun pending ->
            Ui.setButtonPending saveButton pending idleSaveText "正在保存…"
            Ui.setEnabled cancelButton (not pending)
        let buttons =
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Right)
            row.Margin <- Thickness 0.0
            row.Children.Add cancelButton
            row.Children.Add saveButton
            row.KeyDown.Add(fun e ->
                if e.Key = Key.Escape then
                    e.Handled <- true
                    overlay.CloseDialog())
            // Enter/Space 归 Ui.onClick：行级再处理会导致保存被触发两次，
            // 多行参数框里的回车也会误提交。这里只处理 Esc。
            row

        let form =
            Ui.vstack
                Tokens.space3
                [ Ui.title (if existing.IsSome then "编辑 MCP 服务器" else "添加 MCP 服务器") :> Control
                  Ui.caption "本地服务器以子进程方式启动（stdio）；远程服务器走 Streamable HTTP。" :> Control
                  idField
                  labelField
                  commandField
                  argsField
                  urlField
                  timeoutField
                  enabledRow :> Control
                  Ui.hairline () :> Control
                  buttons :> Control ]
        form.KeyDown.Add(fun e ->
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
            if e.Key = Key.Escape then
                e.Handled <- true
                overlay.CloseDialog()
            elif e.Key = Key.Enter && ctrl then
                e.Handled <- true
                save ())
        let scroller =
            ScrollViewer(
                Content = form,
                MaxHeight = LayoutPolicy.dialogContentMaxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        overlay.ShowDialog(scroller :> Control, 520.0, onClosed = (fun () -> editorActive <- false))

    member private _.RenderTool(tool: ToolInfo) : Control =
        let icon = if tool.source = "mcp" then Icons.server Tokens.textMuted else Icons.wrench Tokens.textMuted
        icon.VerticalAlignment <- VerticalAlignment.Top
        icon.Margin <- Thickness(0.0, Tokens.iconBaselineNudge, 0.0, 0.0)
        let name =
            TextBlock(
                Text = tool.label,
                FontSize = Tokens.fontSmall,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text)
        let identifier =
            TextBlock(
                Text = tool.id,
                FontSize = Tokens.fontMicro,
                FontFamily = Tokens.monoFontFamily,
                Foreground = Tokens.textFaint)
        let description =
            TextBlock(
                Text = (if String.IsNullOrWhiteSpace tool.description then "（服务器未提供描述）" else tool.description),
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = ReadingRhythm.captionLineHeight,
                Margin = Thickness(0.0, Tokens.iconBaselineNudge, 0.0, 0.0))
        let column = Ui.vstack 1.0 [ name :> Control; identifier :> Control; description :> Control ]
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space3)
        row.Children.Add icon
        row.Children.Add column
        Border(
            Background = Tokens.surface,
            BorderBrush = Tokens.border,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusMd,
            Padding = Thickness(Tokens.space3, Tokens.space3),
            Child = row)
        :> Control

    member private this.RenderMcp(server: McpInfo) : Control =
        let toolCount = catalog.tools |> List.filter (fun t -> t.serverId = Some server.id) |> List.length
        let transport =
            match server.command, server.url with
            | Some command, _ -> sprintf "本地 · %s %s" command (String.Join(" ", server.args))
            | _, Some url -> sprintf "远程 · %s" url
            | _ -> "未配置传输"
        let name =
            TextBlock(
                Text = server.label,
                FontSize = Tokens.fontBody,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text,
                VerticalAlignment = VerticalAlignment.Center)
        let titleRow = Ui.hstack Tokens.space2 [ name :> Control ]
        titleRow.Children.Add(Ui.tag(sprintf "%d 个工具" toolCount))
        if not server.enabled then titleRow.Children.Add(Ui.tag "已停用")
        let meta =
            TextBlock(
                Text = transport,
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textFaint,
                TextTrimming = TextTrimming.CharacterEllipsis)
        let column = Ui.vstack 2.0 [ titleRow :> Control; meta :> Control ]
        let editButton = Ui.iconButton Icons.pencil "编辑"
        Ui.onClick editButton (fun () -> this.ShowEditor(Some server))
        let moreButton = Ui.iconButton Icons.more "更多"
        Ui.onClick moreButton (fun () ->
            Menu.show
                overlay
                (moreButton :> Control)
                true
                [ MenuEntry.create (if server.enabled then "停用" else "启用") (fun () ->
                      actions.upsertMcp
                          (mcpPayload
                              server.id
                              server.label
                              (server.command |> Option.defaultValue "")
                              server.args
                              (server.url |> Option.defaultValue "")
                              server.callTimeoutSeconds
                              (not server.enabled))
                          ignore)
                  MenuEntry.create "删除" (fun () -> actions.deleteMcp server.id)
                  |> MenuEntry.withIcon Icons.trash
                  |> MenuEntry.asDanger ])
        let actionsRow = Ui.hstack Tokens.space1 [ editButton :> Control; moreButton :> Control ]
        let dock = DockPanel(LastChildFill = true)
        DockPanel.SetDock(actionsRow, Dock.Right)
        dock.Children.Add actionsRow
        dock.Children.Add column
        Border(
            Background = Tokens.surface,
            BorderBrush = Tokens.border,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusMd,
            Padding = Thickness(Tokens.space4, Tokens.space3),
            Child = dock)
        :> Control

    member this.SetCatalog(next: Catalog) =
        catalog <- next
        builtinPanel.Children.Clear()
        for tool in next.tools do
            builtinPanel.Children.Add(this.RenderTool tool)
        if List.isEmpty next.tools then
            builtinPanel.Children.Add(
                Ui.card(Ui.caption "当前没有可用工具。内置文件工具需要服务端配置沙箱根目录后才会出现。") :> Control)
        mcpPanel.Children.Clear()
        for server in next.mcpServers do
            mcpPanel.Children.Add(this.RenderMcp server)
        if List.isEmpty next.mcpServers then
            mcpPanel.Children.Add(Ui.card(Ui.caption "还没有 MCP 服务器。添加后它提供的工具会自动出现在上面的清单里。") :> Control)
        sandboxNote.Text <-
            if List.isEmpty next.generation.fileReadRoots then
                "文件读取工具当前不可用：服务端未配置 tools.fileReadRoots 沙箱根目录。这是有意的——没有沙箱就等于把整台机器的文件交给模型。"
            else
                "文件读取工具的沙箱根目录：" + String.Join("、", next.generation.fileReadRoots)

    member this.Build() : Control =
        let addButton = Ui.button Ui.Primary "添加 MCP 服务器" (fun () -> this.ShowEditor None)
        addButton.HorizontalAlignment <- HorizontalAlignment.Left
        Ui.vstack
            Tokens.space6
            [ Ui.vstack
                  Tokens.space1
                  [ Ui.heading "工具与 MCP" :> Control
                    Ui.caption "会话设置里勾选的工具会随请求交给模型。工具清单来自服务端实际发现的结果。" :> Control ]
              :> Control
              Ui.vstack Tokens.space2 [ Ui.sectionLabel "可用工具" :> Control; builtinPanel :> Control ] :> Control
              sandboxNote :> Control
              Ui.hairline () :> Control
              Ui.vstack
                  Tokens.space2
                  [ Ui.sectionLabel "MCP 服务器" :> Control; addButton :> Control; mcpPanel :> Control ]
              :> Control ]
        :> Control
