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
        argsBox.TextWrapping <- TextWrapping.Wrap
        argsBox.MinHeight <- ControlMetrics.textAreaMinHeight
        argsBox.VerticalContentAlignment <- VerticalAlignment.Top
        let _, urlBox = Ui.textField "https://example.com/mcp"
        let urlField = Ui.inputFieldGroup "远程端点" "http(s) 地址；与本地命令二选一。" urlBox
        let _, timeoutBox = Ui.textField "60"
        let timeoutField = Ui.inputFieldGroup "单次调用超时（秒）" "1–3600 秒。" timeoutBox
        let allBoxes = [ idBox; labelBox; commandBox; argsBox; urlBox; timeoutBox ]
        for box in allBoxes do attachFieldValidation box
        let errorSummary =
            TextBlock(
                Text = "",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.danger,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = ReadingRhythm.captionLineHeight,
                IsVisible = false,
                Focusable = true)
        Avalonia.Automation.AutomationProperties.SetName(errorSummary, "表单错误摘要")
        Avalonia.Automation.AutomationProperties.SetLiveSetting(
            errorSummary,
            Avalonia.Automation.AutomationLiveSetting.Assertive)
        // 用户动手修正任一字段即收起摘要：与字段行内「一改即清」同一时机，
        // 不再让 assertive live region 播报已经修正的旧错误。
        Ui.bindValidationSummaryReset (Some errorSummary) allBoxes
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

        // 启用开关行统一走 Ui.switchRow：行容器 / hover / CheckBox 语义 / ItemStatus /
        // 行最小高与其它设置页同一来源；读取端取值与开关语义不变。
        let enabledRow, readEnabled, _ =
            Ui.switchRow
                "启用这个服务器"
                ""
                (existing |> Option.map (fun s -> s.enabled) |> Option.defaultValue true)
                ignore

        let idleSaveText = if existing.IsSome then "保存" else "添加"
        let mutable setPending: bool -> unit = ignore
        /// 保存往返中的重入 guard：按钮已禁用，但 Ctrl+Enter 仍能进 save。
        let mutable savePending = false
        let saveCore () =
            for box in allBoxes do clearFieldError box
            errorSummary.IsVisible <- false
            errorSummary.Text <- ""
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
            let errors = ResizeArray<TextBox * string>()
            let fail (box: TextBox) msg =
                applyFieldError box msg
                errors.Add(box, msg)

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

            if List.isEmpty (List.ofSeq errors) then
                let timeout = match Int32.TryParse rawTimeout with true, t -> t | _ -> 60
                setPending true
                actions.upsertMcp
                    (mcpPayload id label command args url timeout (readEnabled ()))
                    (fun ok ->
                        setPending false
                        if ok && editorActive then overlay.CloseDialog()
                        elif not ok then actions.toast "保存失败，请重试。" Failure)
            else
                Ui.applyValidationFeedback (List.ofSeq errors) (Some errorSummary) (fun message -> actions.toast message Warning)

        /// pending 中的 save 直接返回，不做二次提交。
        let save () =
            if savePending then () else saveCore ()

        // 单行字段 Enter 即提交：与连接对话框（Dialogs.fs:337-343）同一约定。
        // 多行参数框不接——那里的回车是换行，属于输入自己的语义。
        let submitOnEnter () =
            let ctrl (e: KeyEventArgs) =
                e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
            for box in [ idBox; labelBox; commandBox; urlBox; timeoutBox ] do
                box.KeyDown.Add(fun e ->
                    if e.Key = Key.Enter && not (ctrl e) then
                        e.Handled <- true
                        save ())

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
            savePending <- pending
            Ui.setButtonPending saveButton pending idleSaveText "正在保存…"
            Ui.setEnabled cancelButton (not pending)
        let buttons =
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Right)
            row.Margin <- Thickness 0.0
            row.Children.Add cancelButton
            row.Children.Add saveButton
            // Enter/Space 归 Ui.onClick：行级再处理会导致保存被触发两次，
            // 多行参数框里的回车也会误提交。Escape 不再本地处理，
            // 统一走 OverlayHost.HandleEscape（pending 保存中被 canDismiss 守卫拦截）。
            // 页脚左右键导航：与 Dialogs.fs / SettingsProviders 同款节律。
            cancelButton.KeyDown.Add(fun e ->
                if e.Key = Key.Right && saveButton.IsEnabled then
                    e.Handled <- true
                    saveButton.Focus(NavigationMethod.Directional) |> ignore)
            saveButton.KeyDown.Add(fun e ->
                if e.Key = Key.Left && cancelButton.IsEnabled then
                    e.Handled <- true
                    cancelButton.Focus(NavigationMethod.Directional) |> ignore)
            submitOnEnter ()
            row

        let form =
            Ui.vstack
                Tokens.space3
                [ Ui.title (if existing.IsSome then "编辑 MCP 服务器" else "添加 MCP 服务器") :> Control
                  Ui.caption "本地服务器以子进程方式启动（stdio）；远程服务器走 Streamable HTTP。" :> Control
                  errorSummary :> Control
                  idField
                  labelField
                  commandField
                  argsField
                  urlField
                  timeoutField
                  enabledRow
                  Ui.hairline () :> Control
                  buttons :> Control ]
        form.KeyDown.Add(fun e ->
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
            // Escape 统一走 OverlayHost.HandleEscape：本地分支会让 pending 保存绕过 canDismiss 守卫。
            if e.Key = Key.Enter && ctrl then
                e.Handled <- true
                save ())
        let scroller =
            ScrollViewer(
                Content = form,
                MaxHeight = LayoutPolicy.dialogContentMaxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        overlay.ShowDialog(
            scroller :> Control,
            520.0,
            // 首焦落在主输入框（新建 → ID；编辑 → 名称，ID 取只读），而不是
            // 被禁用的标识字段——与服务商编辑器同一 contract。
            initialFocus =
                (match existing with
                 | Some _ -> labelBox
                 | None -> idBox),
            onClosed = (fun () -> editorActive <- false),
            canDismiss = (fun () -> not savePending))

    member private _.RenderTool(tool: ToolInfo) : Control =
        let icon = if tool.source = "mcp" then Icons.server Tokens.textMuted else Icons.wrench Tokens.textMuted
        icon.VerticalAlignment <- VerticalAlignment.Top
        icon.Margin <- Thickness(0.0, Tokens.iconBaselineNudge, 0.0, 0.0)
        let name =
            TextBlock(
                Text = tool.label,
                FontSize = Tokens.fontBody,
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
        let column = Ui.vstack Tokens.tightRowPaddingY [ name :> Control; identifier :> Control; description :> Control ]
        let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space3)
        row.Children.Add icon
        row.Children.Add column
        // 列表行卡内边距走行卡档 (space4, space3)：与同页 MCP 行、服务商行同一档。
        Ui.groupingCard (Thickness(Tokens.space4, Tokens.space3)) row Tokens.radiusMd
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
        // 已停用卡弱化：只把行文字列的前景转 textMuted（tag 与编辑 / 更多按钮不压暗）。
        // meta 已是 textFaint，无需再降。
        if not server.enabled then name.Foreground <- Tokens.textMuted
        let titleRow = Ui.hstack Tokens.space2 [ name :> Control ]
        titleRow.Children.Add(Ui.tag(sprintf "%d 个工具" toolCount))
        if not server.enabled then titleRow.Children.Add(Ui.tag "已停用")
        let meta =
            TextBlock(
                Text = transport,
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textFaint,
                TextTrimming = TextTrimming.CharacterEllipsis)
        let column = Ui.vstack Tokens.compactRowPaddingY [ titleRow :> Control; meta :> Control ]
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
        Ui.groupingCard (Thickness(Tokens.space4, Tokens.space3)) dock Tokens.radiusMd
        :> Control

    member this.SetCatalog(next: Catalog) =
        catalog <- next
        builtinPanel.Children.Clear()
        for tool in next.tools do
            builtinPanel.Children.Add(this.RenderTool tool)
        if List.isEmpty next.tools then
            builtinPanel.Children.Add(
                Ui.emptyState
                    (Brand.logo Tokens.logoEmpty)
                    None
                    "当前没有可用工具。内置文件工具需要服务端配置沙箱根目录后才会出现。"
                    None
                    None)
        mcpPanel.Children.Clear()
        for server in next.mcpServers do
            mcpPanel.Children.Add(this.RenderMcp server)
        if List.isEmpty next.mcpServers then
            mcpPanel.Children.Add(
                Ui.emptyState
                    (Brand.logo Tokens.logoEmpty)
                    None
                    "还没有 MCP 服务器。添加后它提供的工具会自动出现在上面的清单里。"
                    None
                    None)
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
              // 内容分区之间用内容档分隔线（Ui.hairline = borderSoft），与编辑器表单内的分隔线同一档；
              // 窗口 / 导航这类 chrome 分隔才用更轻的 Tokens.hairline。
              Ui.hairline () :> Control
              Ui.vstack
                  Tokens.space2
                  [ Ui.sectionLabel "MCP 服务器" :> Control; addButton :> Control; mcpPanel :> Control ]
              :> Control ]
        :> Control
