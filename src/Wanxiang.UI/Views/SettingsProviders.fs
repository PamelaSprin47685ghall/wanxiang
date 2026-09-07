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
open Avalonia.Threading
open System.Text.RegularExpressions

/// 设置界面对外暴露的动作。全部落到协议上，客户端不直接碰 TOML。
type SettingsActions = {
    upsertProvider: JsonObject -> (bool -> unit) -> unit
    deleteProvider: string -> unit
    probeProvider: string -> unit
    upsertMcp: JsonObject -> (bool -> unit) -> unit
    deleteMcp: string -> unit
    updateGeneration: JsonObject -> (bool -> unit) -> unit
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

    /// 探活结果按服务商 id 暂存，用于在编辑器里回填模型列表。
    let probeResults = System.Collections.Generic.Dictionary<string, string list>()

    /// 当前编辑器的探活挂钩：ApplyProbe 到达时清掉按钮 pending 并回填模型框，
    /// 避免用户用陈旧列表覆盖刚探到的结果。对话框关闭时清空。
    /// 失败时还在编辑器里留一行常驻标记：toast 一闪而过，这行留到下一次探活。
    let mutable activeProbe: (string * (bool -> unit) * (string list -> unit) * (string -> unit)) option = None

    /// 所有 loopback 写法都算本机：127.0.0.1、localhost、::1（含括号写法）。
    /// Uri.IsLoopback 覆盖标准解析；子串回退覆盖非常规输入。
    let isLoopbackUrl (baseUrl: string) =
        if String.IsNullOrWhiteSpace baseUrl then false
        else
            match Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute) with
            | true, uri when uri.IsLoopback -> true
            | _ ->
                let lower = baseUrl.Trim().ToLowerInvariant()
                lower.Contains("127.0.0.1") || lower.Contains("localhost") || lower.Contains("[::1]") || lower.Contains("::1")

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
        let mutable editorActive = true
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

        let _, idBox = Ui.textField "例如 openai"
        let idField = Ui.inputFieldGroup "稳定标识" "创建后不可修改；仅限字母、数字、下划线与连字符。" idBox
        let _, labelBox = Ui.textField "在界面上怎么称呼它"
        let labelField = Ui.inputFieldGroup "显示名称" "仅用于界面显示。" labelBox
        let _, urlBox = Ui.textField "https://api.openai.com/v1"
        let urlField = Ui.inputFieldGroup "端点地址" "https:// 开头的服务地址；原生与兼容接口均可。" urlBox
        let _, keyBox = Ui.textField "粘贴密钥"
        let keyField = Ui.inputFieldGroup "API Key" "只保存在服务端，界面不会回显。" keyBox
        keyBox.PasswordChar <- '●'
        let _, modelsBox = Ui.textField "每行一个模型名"
        let modelsField = Ui.inputFieldGroup "模型列表" "每行一个；也可用逗号分隔。" modelsBox
        modelsBox.AcceptsReturn <- true
        modelsBox.TextWrapping <- TextWrapping.Wrap
        modelsBox.MinHeight <- 92.0
        modelsBox.VerticalContentAlignment <- VerticalAlignment.Top
        let _, defaultModelBox = Ui.textField "留空则自动使用列表中第一个"
        let defaultModelField = Ui.inputFieldGroup "默认模型" "必须在模型列表中；留空自动选用第一个。" defaultModelBox
        let allBoxes = [ idBox; labelBox; urlBox; keyBox; modelsBox; defaultModelBox ]
        for box in allBoxes do attachFieldValidation box


        let presetHint =
            TextBlock(
                Text = "",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.textMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = ReadingRhythm.captionLineHeight)
        presetHint.IsVisible <- false

        let mutable currentPreset: ProviderPreset option = initialPreset
        let mutable applyingPreset = false
        let mutable setPresetText: string -> unit = ignore

        let syncPresetHint () =
            presetHint.IsVisible <- not (String.IsNullOrWhiteSpace presetHint.Text)

        /// 传输类型由预设决定，表单里只读展示，不可手动修改。
        let transportLabel kind =
            match kind with
            | "anthropic" -> "Anthropic 原生 · Messages API（支持思维链与 prompt caching）"
            | "gemini" -> "Google 原生 · generateContent（支持多模态与 systemInstruction）"
            | _ -> "OpenAI 兼容 · /v1/chat/completions"
        let kindCaption = Ui.caption ""
        let syncKindCaption () =
            kindCaption.Text <- "传输：" + transportLabel selectedKind

        let applyPreset (preset: ProviderPreset) =
            applyingPreset <- true
            try
                selectedKind <- preset.kind
                currentPreset <- Some preset
                urlBox.Text <- preset.baseUrl
                modelsBox.Text <- String.Join("\n", preset.models)
                defaultModelBox.Text <- preset.models |> List.tryHead |> Option.defaultValue ""
                presetHint.Text <- preset.hint
                syncPresetHint ()
                setPresetText preset.label
                syncKindCaption ()
                if existing.IsNone then
                    idBox.Text <- ProviderPresets.suggestId preset takenIds
                    labelBox.Text <- preset.label
            finally
                applyingPreset <- false

        /// 预设一旦被手改就不再是预设：按钮文案诚实回到“自定义”，
        /// 按钮位置与 hint 行都在原处，只换文案，视觉不断层。
        let markCustomIfDiverged () =
            if not applyingPreset then
                match currentPreset with
                | Some p ->
                    let normModels =
                        (if isNull modelsBox.Text then "" else modelsBox.Text)
                            .Split([| '\n'; '\r'; ',' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.map (fun s -> s.Trim())
                        |> Array.filter (String.IsNullOrWhiteSpace >> not)
                        |> Array.distinct
                    let normPreset =
                        p.models |> List.map (fun s -> s.Trim()) |> List.filter (String.IsNullOrWhiteSpace >> not) |> Array.ofList
                    let url = if isNull urlBox.Text then "" else urlBox.Text.Trim()
                    // 端点比较走归一化：末尾斜杠不算分歧，预设连续性不断。
                    if ProviderPresets.normalizeBaseUrl url <> ProviderPresets.normalizeBaseUrl p.baseUrl || normModels <> normPreset then
                        currentPreset <- None
                        // 回到自定义就不再是预设：hint 清空隐藏（传输类型不变，
                        // 只读行无需改动），都在原处换文案，不碰兄弟组。
                        presetHint.Text <- ""
                        syncPresetHint ()
                        setPresetText "自定义"
                | None -> ()
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
        urlBox.TextChanged.Add(fun _ -> markCustomIfDiverged ())
        modelsBox.TextChanged.Add(fun _ -> markCustomIfDiverged ())

        match existing with
        | Some p ->
            idBox.Text <- p.id
            idBox.IsEnabled <- false
            labelBox.Text <- p.label
            urlBox.Text <- p.baseUrl
            modelsBox.Text <- String.Join("\n", p.models)
            defaultModelBox.Text <- p.defaultModel
            keyBox.PlaceholderText <- if p.hasApiKey then "已保存（留空则不改动）" else "粘贴密钥"
            presetHint.Text <- initialPreset |> Option.map (fun x -> x.hint) |> Option.defaultValue ""
            syncPresetHint ()
        | None ->
            match initialPreset with
            | Some preset -> applyPreset preset
            | None -> ()

        // 编辑已有服务商时传输类型来自它自己，不走 applyPreset，这里补一次同步。
        syncKindCaption ()

        let probeIdleText = "从服务器获取模型列表"
        let mutable setProbePending: bool -> unit = ignore
        /// 探活失败的常驻行内标记：toast 一闪而过，这行留到下一次探活。
        let probeStatus =
            TextBlock(
                Text = "",
                FontSize = Tokens.fontCaption,
                Foreground = Tokens.danger,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = ReadingRhythm.captionLineHeight)
        probeStatus.IsVisible <- false
        let setProbeStatus (message: string) =
            probeStatus.Text <- message
            probeStatus.IsVisible <- not (String.IsNullOrWhiteSpace message)
        let runProbe () =
            let id = if isNull idBox.Text then "" else idBox.Text.Trim()
            if String.IsNullOrWhiteSpace id then
                actions.toast "先填写稳定标识再探测。" Warning
            elif not (takenIds |> List.contains id) then
                actions.toast "先保存服务商，然后再探测模型列表。" Warning
            else
                clearFieldError urlBox
                setProbeStatus ""
                setProbePending true
                activeProbe <-
                    Some (id, setProbePending, (fun models ->
                        modelsBox.Text <- String.Join("\n", models)
                        let currentDefault = if isNull defaultModelBox.Text then "" else defaultModelBox.Text.Trim()
                        if String.IsNullOrWhiteSpace currentDefault || not (List.contains currentDefault models) then
                            defaultModelBox.Text <- models |> List.tryHead |> Option.defaultValue ""), setProbeStatus)
                actions.probeProvider id
                actions.toast "正在向服务商请求模型列表…" Neutral
        let probeButton = Ui.button Ui.Secondary probeIdleText runProbe
        // pending 文案更长，预留最小宽度：label 切换不改变按钮几何。
        Ui.preparePendingButton probeButton
        setProbePending <- fun pending ->
            Ui.setButtonPending probeButton pending probeIdleText "正在获取…"

        let enabledToggle, readEnabled, _, _ =
            Ui.toggle (existing |> Option.map (fun p -> p.enabled) |> Option.defaultValue true) ignore
        Avalonia.Automation.AutomationProperties.SetName(enabledToggle, "启用这个服务商")
        let enabledRow =
            let row = DockPanel(LastChildFill = false)
            let caption = Ui.label "启用这个服务商"
            DockPanel.SetDock(caption, Dock.Left)
            DockPanel.SetDock(enabledToggle, Dock.Right)
            row.Children.Add caption
            row.Children.Add enabledToggle
            row

        let idleSaveText = if existing.IsSome then "保存" else "添加"
        let mutable setPending: bool -> unit = ignore
        /// 保存往返中的重入 guard：按钮已禁用，但 Ctrl+Enter 仍能进 save。
        let mutable savePending = false
        let saveCore () =
            for box in allBoxes do clearFieldError box
            let id = if isNull idBox.Text then "" else idBox.Text.Trim()
            let label = if isNull labelBox.Text then "" else labelBox.Text.Trim()
            // 同一端点只有一个身份：末尾斜杠在入库前归一，预设连续性不断。
            let url = ProviderPresets.normalizeBaseUrl (if isNull urlBox.Text then "" else urlBox.Text)
            let models =
                (if isNull modelsBox.Text then "" else modelsBox.Text)
                    .Split([| '\n'; '\r'; ',' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.distinct
                |> List.ofArray
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

            if String.IsNullOrWhiteSpace url then
                fail urlBox "端点地址不能为空。"
            elif not (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) then
                fail urlBox "端点地址必须以 http:// 或 https:// 开头。"
            else
                match Uri.TryCreate(url, UriKind.Absolute) with
                | true, uri when uri.Scheme = "http" || uri.Scheme = "https" -> ()
                | _ -> fail urlBox "请输入合法的 HTTP/HTTPS 地址（例如 https://api.openai.com/v1）。"

            if List.isEmpty models then
                fail modelsBox "至少填写一个模型名（每行一个）。"

            let rawDefaultModel = if isNull defaultModelBox.Text then "" else defaultModelBox.Text.Trim()
            let defaultModel =
                if String.IsNullOrWhiteSpace rawDefaultModel then
                    match models with
                    | head :: _ ->
                        defaultModelBox.Text <- head
                        actions.toast (sprintf "未指定默认模型，已自动选用“%s”。" head) Neutral
                        head
                    | [] -> ""
                elif not (List.isEmpty models) && not (List.contains rawDefaultModel models) then
                    fail defaultModelBox (sprintf "默认模型“%s”不在模型列表中，请从列表中选择或留空自动选用。" rawDefaultModel)
                    rawDefaultModel
                else
                    rawDefaultModel

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
                let payload =
                    providerPayload
                        id
                        label
                        selectedKind
                        url
                        (if isNull keyBox.Text then None else Some(keyBox.Text.Trim()))
                        models
                        defaultModel
                        (readEnabled ())
                        (existing |> Option.map (fun p -> p.timeoutSeconds) |> Option.defaultValue 120)
                        (existing |> Option.map (fun p -> p.maxRetries) |> Option.defaultValue 2)
                setPending true
                actions.upsertProvider payload (fun ok ->
                    setPending false
                    if ok && editorActive then overlay.CloseDialog())

        /// pending 中的 save 直接返回，不做二次提交。
        let save () =
            if savePending then () else saveCore ()

        let cancelButton = Ui.button Ui.Ghost "取消" (fun () -> overlay.CloseDialog())
        cancelButton.Margin <- Thickness 0.0
        cancelButton.Focusable <- true
        Avalonia.Automation.AutomationProperties.SetName(cancelButton, "取消")
        Avalonia.Automation.AutomationProperties.SetHelpText(cancelButton, "取消并关闭服务商设置 (Esc)")
        ToolTip.SetTip(cancelButton, "取消并关闭 (Esc)")
        let saveButton = Ui.button Ui.Primary idleSaveText save
        saveButton.Margin <- Thickness 0.0
        saveButton.Focusable <- true
        Avalonia.Automation.AutomationProperties.SetName(saveButton, idleSaveText)
        Avalonia.Automation.AutomationProperties.SetHelpText(saveButton, sprintf "确认%s服务商设置 (Ctrl+Enter)" idleSaveText)
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
            row.KeyDown.Add(fun e ->
                if e.Key = Key.Escape then
                    e.Handled <- true
                    overlay.CloseDialog())
            // Enter/Space 归 Ui.onClick：行级再处理会导致保存被触发两次，
            // 多行模型框里的回车也会误提交。这里只处理 Esc。
            row

        let form =
            Ui.vstack
                Tokens.space3
                [ Ui.title (if existing.IsSome then "编辑服务商" else "添加服务商") :> Control
                  Ui.controlFieldGroup "预设" "" (presetButton :> Control)
                  kindCaption :> Control
                  presetHint :> Control
                  idField
                  labelField
                  urlField
                  keyField
                  modelsField
                  defaultModelField
                  probeButton :> Control
                  probeStatus :> Control
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
        overlay.ShowDialog(scroller :> Control, 520.0, onClosed = (fun () -> editorActive <- false; activeProbe <- None))

    /// 探活结果回填：把服务商的模型列表直接更新到配置里。
    member this.ApplyProbe(providerId: string, ok: bool, models: string list, error: string option) =
        match activeProbe with
        | Some (pid, setPending, applyModels, setStatus) when pid = providerId ->
            activeProbe <- None
            setPending false
            if ok && not (List.isEmpty models) then
                setStatus ""
                applyModels models
            elif ok then
                setStatus "服务商返回了空模型列表，模型框未改动。"
            else
                setStatus (sprintf "探测失败：%s" (error |> Option.defaultValue "未知原因"))
        | _ -> ()
        if ok then
            probeResults[providerId] <- models
            match Catalog.tryProvider providerId catalog with
            | Some provider when not (List.isEmpty models) ->
                let defaultModel =
                    if List.contains provider.defaultModel models then provider.defaultModel else List.head models
                actions.toast (sprintf "已获取 %d 个模型，正在保存…" (List.length models)) Neutral
                actions.upsertProvider
                    (providerPayload
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
                    ignore
            | _ -> actions.toast "服务商返回了空模型列表。" Warning
        else
            actions.toast (sprintf "探测失败：%s" (error |> Option.defaultValue "未知原因")) Failure

    member private this.RenderRow(provider: ProviderInfo) : Control =
        // 点只反映配置完整度：没探活过就没资格显示「健康」。
        // 免密钥预设（本机推理）与所有 loopback 写法永远不缺密钥。
        let presetNeedsKey =
            match ProviderPresets.matchByBaseUrl provider.baseUrl with
            | Some preset -> preset.needsApiKey
            | None -> true
        let configured =
            provider.hasApiKey || not presetNeedsKey || isLoopbackUrl provider.baseUrl
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
        Ui.onClick editButton (fun () -> this.ShowEditor(Some provider))
        let moreButton = Ui.iconButton Icons.more "更多"
        Ui.onClick moreButton (fun () ->
            Menu.show
                overlay
                (moreButton :> Control)
                true
                [ MenuEntry.create "获取模型列表" (fun () ->
                      actions.toast "正在向服务商请求模型列表…" Neutral
                      actions.probeProvider provider.id)
                  |> MenuEntry.withIcon Icons.refresh
                  MenuEntry.create (if provider.enabled then "停用" else "启用") (fun () ->
                      actions.upsertProvider
                          (providerPayload
                              provider.id
                              provider.label
                              provider.kind
                              provider.baseUrl
                              None
                              provider.models
                              provider.defaultModel
                              (not provider.enabled)
                              provider.timeoutSeconds
                              provider.maxRetries)
                          ignore)
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
            Tokens.space6
            [ Ui.vstack
                  Tokens.space1
                  [ Ui.heading "服务商" :> Control
                    Ui.caption "万象通过服务商原生协议或 OpenAI 兼容接口访问模型。密钥保存在服务端配置里，界面不会回显。" :> Control ]
              :> Control
              addButton :> Control
              listPanel :> Control ]
        :> Control
