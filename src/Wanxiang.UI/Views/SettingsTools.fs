namespace Wanxiang.UI

open System
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

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
        let idField, idBox = Ui.labeledField "稳定标识" "例如 filesystem"
        let labelField, labelBox = Ui.labeledField "显示名称" "在界面上怎么称呼它"
        let commandField, commandBox = Ui.labeledField "本地命令" "例如 npx"
        let argsField, argsBox = Ui.labeledField "命令参数" "每行一个参数"
        argsBox.AcceptsReturn <- true
        argsBox.MinHeight <- 72.0
        argsBox.VerticalContentAlignment <- VerticalAlignment.Top
        let urlField, urlBox = Ui.labeledField "远程端点" "https://example.com/mcp（与命令二选一）"
        let timeoutField, timeoutBox = Ui.labeledField "单次调用超时（秒）" "60"

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

        let save () =
            for box in [ idBox; commandBox; urlBox; timeoutBox ] do Ui.clearFieldError box
            let id = if isNull idBox.Text then "" else idBox.Text.Trim()
            let command = if isNull commandBox.Text then "" else commandBox.Text.Trim()
            let url = if isNull urlBox.Text then "" else urlBox.Text.Trim()
            let args =
                (if isNull argsBox.Text then "" else argsBox.Text)
                    .Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> List.ofArray
            let timeoutResult = Int32.TryParse(if isNull timeoutBox.Text then "" else timeoutBox.Text.Trim())
            if String.IsNullOrWhiteSpace id then
                Ui.setFieldError idBox "稳定标识不能为空。"
                idBox.Focus() |> ignore
            elif String.IsNullOrWhiteSpace command && String.IsNullOrWhiteSpace url then
                Ui.setFieldError commandBox "本地命令与远程端点至少填一个。"
                Ui.setFieldError urlBox "本地命令与远程端点至少填一个。"
                commandBox.Focus() |> ignore
            elif not (String.IsNullOrWhiteSpace command) && not (String.IsNullOrWhiteSpace url) then
                Ui.setFieldError commandBox "与远程端点只能填写一个。"
                Ui.setFieldError urlBox "与本地命令只能填写一个。"
                commandBox.Focus() |> ignore
            elif not (fst timeoutResult) || snd timeoutResult <= 0 then
                Ui.setFieldError timeoutBox "请输入大于 0 的秒数。"
                timeoutBox.Focus() |> ignore
            else
                let timeout = snd timeoutResult
                actions.upsertMcp(
                    mcpPayload id (if isNull labelBox.Text then "" else labelBox.Text.Trim()) command args url timeout (readEnabled ()))
                overlay.CloseDialog()

        let buttons =
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Right)
            row.Children.Add(Ui.button Ui.Ghost "取消" (fun () -> overlay.CloseDialog()))
            row.Children.Add(Ui.button Ui.Primary (if existing.IsSome then "保存" else "添加") save)
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
        let scroller =
            ScrollViewer(
                Content = form,
                MaxHeight = LayoutPolicy.dialogContentMaxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        overlay.ShowDialog(scroller :> Control, 520.0)

    member private _.RenderTool(tool: ToolInfo) : Control =
        let icon = if tool.source = "mcp" then Icons.server Tokens.textMuted else Icons.wrench Tokens.textMuted
        icon.VerticalAlignment <- VerticalAlignment.Top
        icon.Margin <- Thickness(0.0, 2.0, 0.0, 0.0)
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
                Margin = Thickness(0.0, 2.0, 0.0, 0.0))
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
                      actions.upsertMcp(
                          mcpPayload
                              server.id
                              server.label
                              (server.command |> Option.defaultValue "")
                              server.args
                              (server.url |> Option.defaultValue "")
                              server.callTimeoutSeconds
                              (not server.enabled)))
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
            Tokens.space5
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
