namespace Wanxiang.UI

open System
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Shapes
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

/// 设置界面对外暴露的动作。全部落到协议上，客户端不直接碰 TOML。
type SettingsActions = {
    upsertProvider: JsonObject -> unit
    deleteProvider: string -> unit
    probeProvider: string -> unit
    upsertMcp: JsonObject -> unit
    deleteMcp: string -> unit
    updateGeneration: JsonObject -> unit
    savePrefs: UiPrefs -> unit
    toast: string -> ToastTone -> unit
}

/// 服务商设置面板。
///
/// 这是「不改配置文件也能用」的关键：预设一选、密钥一贴、模型一探，
/// 就能开始对话。密钥永不回显（服务端只下发 hasApiKey）。
type SettingsProviders(overlay: OverlayHost, actions: SettingsActions) =

    let listPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2)
    let mutable catalog = Catalog.empty

    /// 探活结果按服务商 id 暂存，用于在编辑器里回填模型列表。
    let probeResults = System.Collections.Generic.Dictionary<string, string list>()

    let providerPayload (id: string) (label: string) (kind: string) (baseUrl: string) (apiKey: string option) (models: string list) (defaultModel: string) (enabled: bool) (timeout: int) (retries: int) =
        let o = JsonObject()
        o["id"] <- id
        o["label"] <- label
        // 传输类型由预设决定（原生 Claude / 原生 Gemini / OpenAI 兼容）。
        // 写死 openai 会让原生传输在界面上根本选不到。
        o["kind"] <- (if System.String.IsNullOrWhiteSpace kind then "openai" else kind)
        o["baseUrl"] <- baseUrl
        match apiKey with
        | Some key when not (String.IsNullOrWhiteSpace key) -> o["apiKey"] <- key
        | _ -> ()
        o["models"] <- JsonArray([| for m in models -> JsonNode.op_Implicit m |])
        o["defaultModel"] <- defaultModel
        o["enabled"] <- enabled
        o["timeoutSeconds"] <- timeout
        o["maxRetries"] <- retries
        o

    /// 服务商编辑器。`existing = None` 表示新增。
    member private this.ShowEditor(existing: ProviderInfo option) =
        let takenIds = catalog.providers |> List.map (fun p -> p.id)
        let initialPreset =
            match existing with
            | Some p -> ProviderPresets.matchByBaseUrl p.baseUrl
            | None -> ProviderPresets.tryFind "openai"

        // 编辑已有服务商时沿用它的传输类型；新增时取预设的
        let mutable selectedKind =
            match existing with
            | Some p when not (System.String.IsNullOrWhiteSpace p.kind) -> p.kind
            | _ -> initialPreset |> Option.map (fun x -> x.kind) |> Option.defaultValue "openai"

        let idField, idBox = Ui.labeledField "稳定标识" "例如 openai"
        let labelField, labelBox = Ui.labeledField "显示名称" "在界面上怎么称呼它"
        let urlField, urlBox = Ui.labeledField "端点地址" "https://api.openai.com/v1"
        let keyField, keyBox = Ui.labeledField "API Key" "粘贴密钥"
        let modelsField, modelsBox = Ui.labeledField "模型列表" "每行一个模型名"
        modelsBox.AcceptsReturn <- true
        modelsBox.TextWrapping <- TextWrapping.Wrap
        modelsBox.MinHeight <- 92.0
        modelsBox.VerticalContentAlignment <- VerticalAlignment.Top

        let presetHint =
            TextBlock(
                Text = "",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17.0)

        let applyPreset (preset: ProviderPreset) =
            selectedKind <- preset.kind
            urlBox.Text <- preset.baseUrl
            modelsBox.Text <- String.Join("\n", preset.models)
            presetHint.Text <- preset.hint
            if existing.IsNone then
                idBox.Text <- ProviderPresets.suggestId preset takenIds
                labelBox.Text <- preset.label

        let mutable setPresetText: string -> unit = ignore
        let presetOptions () =
            [ "服务商预设",
              ProviderPresets.all
              |> List.map (fun preset ->
                  MenuEntry.create preset.label (fun () ->
                      setPresetText preset.label
                      applyPreset preset)) ]
        let presetButton, setText =
            Menu.selectButton
                overlay
                (match initialPreset with Some p -> p.label | None -> "自定义 OpenAI 兼容端点")
                presetOptions
        setPresetText <- setText
        presetButton.HorizontalAlignment <- HorizontalAlignment.Stretch

        match existing with
        | Some p ->
            idBox.Text <- p.id
            idBox.IsEnabled <- false
            labelBox.Text <- p.label
            urlBox.Text <- p.baseUrl
            modelsBox.Text <- String.Join("\n", p.models)
            keyBox.PlaceholderText <- if p.hasApiKey then "已保存（留空则不改动）" else "粘贴密钥"
            presetHint.Text <- initialPreset |> Option.map (fun x -> x.hint) |> Option.defaultValue ""
        | None ->
            match initialPreset with
            | Some preset -> applyPreset preset
            | None -> ()

        let probeButton =
            Ui.button Ui.Secondary "从服务器获取模型列表" (fun () ->
                let id = if isNull idBox.Text then "" else idBox.Text.Trim()
                if String.IsNullOrWhiteSpace id then
                    actions.toast "先填写稳定标识再探测。" Warning
                elif not (takenIds |> List.contains id) then
                    actions.toast "先保存服务商，然后再探测模型列表。" Warning
                else
                    actions.probeProvider id
                    actions.toast "正在向服务商请求模型列表…" Neutral)

        let enabledToggle, readEnabled, _ =
            Ui.toggle (existing |> Option.map (fun p -> p.enabled) |> Option.defaultValue true) ignore
        let enabledRow =
            let row = DockPanel(LastChildFill = false)
            let caption = Ui.label "启用这个服务商"
            DockPanel.SetDock(caption, Dock.Left)
            DockPanel.SetDock(enabledToggle, Dock.Right)
            row.Children.Add caption
            row.Children.Add enabledToggle
            row

        let save () =
            let id = if isNull idBox.Text then "" else idBox.Text.Trim()
            let models =
                (if isNull modelsBox.Text then "" else modelsBox.Text)
                    .Split([| '\n'; '\r'; ',' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.distinct
                |> List.ofArray
            if String.IsNullOrWhiteSpace id then actions.toast "稳定标识不能为空。" Warning
            elif String.IsNullOrWhiteSpace urlBox.Text then actions.toast "端点地址不能为空。" Warning
            elif List.isEmpty models then actions.toast "至少填写一个模型名。" Warning
            else
                let defaultModel =
                    match existing with
                    | Some p when List.contains p.defaultModel models -> p.defaultModel
                    | _ -> List.head models
                let payload =
                    providerPayload
                        id
                        (if isNull labelBox.Text then "" else labelBox.Text.Trim())
                        selectedKind
                        (urlBox.Text.Trim())
                        (if isNull keyBox.Text then None else Some(keyBox.Text.Trim()))
                        models
                        defaultModel
                        (readEnabled ())
                        (existing |> Option.map (fun p -> p.timeoutSeconds) |> Option.defaultValue 120)
                        (existing |> Option.map (fun p -> p.maxRetries) |> Option.defaultValue 2)
                actions.upsertProvider payload
                overlay.CloseDialog()

        let buttons =
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space2, HorizontalAlignment = HorizontalAlignment.Right)
            row.Children.Add(Ui.button Ui.Ghost "取消" (fun () -> overlay.CloseDialog()))
            row.Children.Add(Ui.button Ui.Primary (if existing.IsSome then "保存" else "添加") save)
            row

        let form =
            Ui.vstack
                Tokens.space3
                [ Ui.title (if existing.IsSome then "编辑服务商" else "添加服务商") :> Control
                  Ui.vstack 0.0 [ Ui.fieldLabel "预设" :> Control; presetButton :> Control ] :> Control
                  presetHint :> Control
                  idField
                  labelField
                  urlField
                  keyField
                  modelsField
                  probeButton :> Control
                  enabledRow :> Control
                  Ui.hairline () :> Control
                  buttons :> Control ]
        let scroller =
            ScrollViewer(
                Content = form,
                MaxHeight = 560.0,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        overlay.ShowDialog(scroller :> Control, 520.0)

    /// 探活结果回填：把服务商的模型列表直接更新到配置里。
    member this.ApplyProbe(providerId: string, ok: bool, models: string list, error: string option) =
        if ok then
            probeResults[providerId] <- models
            match Catalog.tryProvider providerId catalog with
            | Some provider when not (List.isEmpty models) ->
                let defaultModel =
                    if List.contains provider.defaultModel models then provider.defaultModel else List.head models
                actions.upsertProvider(
                    providerPayload
                        provider.id
                        provider.label
                        provider.kind
                        provider.baseUrl
                        None
                        models
                        defaultModel
                        provider.enabled
                        provider.timeoutSeconds
                        provider.maxRetries)
                actions.toast (sprintf "已获取 %d 个模型并保存。" (List.length models)) Success
            | _ -> actions.toast "服务商返回了空模型列表。" Warning
        else
            actions.toast (sprintf "探测失败：%s" (error |> Option.defaultValue "未知原因")) Failure

    member private this.RenderRow(provider: ProviderInfo) : Control =
        // 点只反映配置完整度：没探活过就没资格显示「健康」。
        let configured =
            provider.hasApiKey || provider.baseUrl.StartsWith("http://127.0.0.1", StringComparison.Ordinal)
        let statusBrush: IBrush =
            if not provider.enabled then Tokens.textFaint
            elif configured then Tokens.accent
            else Tokens.warning
        let dot = Ellipse(Width = 7.0, Height = 7.0, Fill = statusBrush, VerticalAlignment = VerticalAlignment.Center)
        let name =
            TextBlock(
                Text = provider.label,
                FontSize = Tokens.fontBody,
                FontWeight = FontWeight.Medium,
                Foreground = Tokens.text,
                VerticalAlignment = VerticalAlignment.Center)
        let meta =
            TextBlock(
                Text =
                    sprintf
                        "%s · %d 个模型%s"
                        provider.baseUrl
                        (List.length provider.models)
                        (if configured then "" else " · 未设置密钥"),
                FontSize = Tokens.fontMicro,
                Foreground = Tokens.textFaint,
                TextTrimming = TextTrimming.CharacterEllipsis)
        let stateLabel =
            if not provider.enabled then "已停用"
            elif configured then "已配置"
            else "缺少密钥"
        let titleRow = Ui.hstack Tokens.space2 [ dot :> Control; name :> Control ]
        titleRow.Children.Add(Ui.tag stateLabel)
        let column = Ui.vstack 2.0 [ titleRow :> Control; meta :> Control ]
        let editButton = Ui.iconButton Icons.pencil "编辑"
        editButton.PointerReleased.Add(fun e ->
            e.Handled <- true
            this.ShowEditor(Some provider))
        let moreButton = Ui.iconButton Icons.more "更多"
        moreButton.PointerReleased.Add(fun e ->
            e.Handled <- true
            Menu.show
                overlay
                (moreButton :> Control)
                true
                [ MenuEntry.create "获取模型列表" (fun () -> actions.probeProvider provider.id) |> MenuEntry.withIcon Icons.refresh
                  MenuEntry.create (if provider.enabled then "停用" else "启用") (fun () ->
                      actions.upsertProvider(
                          providerPayload
                              provider.id
                              provider.label
                              provider.kind
                              provider.baseUrl
                              None
                              provider.models
                              provider.defaultModel
                              (not provider.enabled)
                              provider.timeoutSeconds
                              provider.maxRetries))
                  MenuEntry.create "删除" (fun () -> actions.deleteProvider provider.id)
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
        listPanel.Children.Clear()
        if List.isEmpty next.providers then
            listPanel.Children.Add(
                Ui.card(
                    Ui.vstack
                        Tokens.space2
                        [ Ui.label "还没有任何服务商" :> Control
                          Ui.caption "添加一个服务商后就能开始对话。预设已经填好端点和常见模型，你只需要粘贴密钥。" :> Control ])
                :> Control)
        else
            for provider in next.providers do
                listPanel.Children.Add(this.RenderRow provider)

    member this.Build() : Control =
        let addButton = Ui.button Ui.Primary "添加服务商" (fun () -> this.ShowEditor None)
        addButton.HorizontalAlignment <- HorizontalAlignment.Left
        Ui.vstack
            Tokens.space4
            [ Ui.vstack
                  Tokens.space1
                  [ Ui.heading "服务商" :> Control
                    Ui.caption "万象通过 OpenAI 兼容接口访问模型。密钥保存在服务端配置里，界面不会回显。" :> Control ]
              :> Control
              addButton :> Control
              listPanel :> Control ]
        :> Control
