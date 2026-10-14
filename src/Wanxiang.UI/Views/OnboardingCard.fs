namespace Wanxiang.UI

open System
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading

/// 首次运行卡片对外的动作。全部落到与设置页相同的协议命令上：
/// 卡片不拥有第二套保存逻辑（载荷由 `ProviderPresets.payload` 构造，经同一个
/// `ConfigMutation.upsertProvider` 落盘）。
type OnboardingActions = {
    upsertProvider: JsonObject -> (bool -> unit) -> unit
    /// 删除（不弹确认）：只用于撤销卡片自己刚创建、但探测失败的模型服务。
    discardProvider: string -> unit
    probeProvider: string -> unit
    takenIds: unit -> string list
    /// 本机 Ollama 发现：Some 模型清单 = 在响应；None = 没有或不允许探测。
    discoverLocal: unit -> Task<string list option>
    /// 进入设置页的完整「添加模型服务」表单（高级配置）
    openAdvanced: unit -> unit
    toast: string -> ToastTone -> unit
}

/// 卡片所处阶段。
type OnboardingStep =
    | Idle
    /// 正在保存模型服务（第一次 upsert）
    | Saving
    /// 已保存，正在向对方请求模型清单
    | Probing
    /// 已拿到清单，正在把清单与默认模型写回
    | Applying

module Onboarding =

    /// 「试一句」预填的第一句话。
    [<Literal>]
    let FirstPrompt = "你好，介绍一下你自己"

    /// 解析 OpenAI 兼容 `/models` 响应（`{data:[{id}]}`）。
    let parseModelIds (body: string) : string list =
        try
            match JsonNode.Parse body with
            | :? JsonObject as root ->
                match root["data"] with
                | :? JsonArray as items ->
                    [ for item in items do
                          match item with
                          | :? JsonObject as o ->
                              match o["id"] with
                              | null -> ()
                              | v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>()
                              | _ -> ()
                          | _ -> () ]
                    |> List.filter (String.IsNullOrWhiteSpace >> not)
                    |> List.distinct
                | _ -> []
            | _ -> []
        with _ -> []

    /// 探测本机 Ollama（只在 loopback 上发一次 GET，短超时；失败一律视为没有）。
    /// 这是用户已确认允许的本机端口探测：不带任何凭据，不写任何文件。
    let discoverOllamaAt (url: string) : Task<string list option> =
        task {
            try
                use client = new HttpClient(Timeout = TimeSpan.FromMilliseconds 1500.0)
                use! response = client.GetAsync url
                if response.IsSuccessStatusCode then
                    let! body = response.Content.ReadAsStringAsync()
                    match parseModelIds body with
                    | [] -> return None
                    | ids -> return Some ids
                else
                    return None
            with _ -> return None
        }

    /// 只有主机在本机（loopback）时，「本机 Ollama」才是主机会用到的那个 Ollama。
    /// 浏览器（PWA）里既没有本机访问权，也不该替远端主机猜。
    let canDiscoverLocal (serverUrl: string) =
        not (OperatingSystem.IsBrowser())
        && (match Uri.TryCreate(serverUrl, UriKind.Absolute) with
            | true, uri -> uri.IsLoopback
            | _ -> false)

    /// 卡片里可选的预设：不含需要自填端点的「自定义」（那在高级设置里）。
    let selectablePresets =
        ProviderPresets.all |> List.filter (fun p -> not (String.IsNullOrWhiteSpace p.baseUrl))

    /// 从探测到的清单里选默认模型：优先预设推荐的第一个在清单里的；否则清单第一项。
    let pickDefaultModel (preset: ProviderPreset) (probed: string list) : string =
        preset.models
        |> List.tryFind (fun m -> List.contains m probed)
        |> Option.orElse (List.tryHead probed)
        |> Option.defaultValue (preset.models |> List.tryHead |> Option.defaultValue "")

/// 聊天主界面上的「连接一个模型」卡片：首次运行不必进设置页。
///
/// 动作数（OpenAI 已默认选中）：粘贴密钥 → 点「连接」→ 回车发送，最少 3 个；
/// 换一家多 1 个。连接成功后卡片收起，输入框预填并聚焦（由外壳完成）。
type OnboardingCard(overlay: OverlayHost, actions: OnboardingActions) =

    /// 构造时所在的 UI 调度器；后台续延一律投递到这里，不在调用方线程上新建调度器（决策 421）。
    let ui = Avalonia.Threading.Dispatcher.UIThread
    let mutable selected: ProviderPreset = List.head ProviderPresets.featured
    /// 是否改用同一家的另一条传输（兼容端点）：只在探测失败的下一步里出现。
    let mutable useAlternate = false
    let mutable step = Idle
    /// 本次尝试创建的模型服务 id（探测失败时据此撤销）
    let mutable attemptId: string option = None
    /// 连接成功、等目录快照到达后由外壳收尾的 (id, 模型)
    let mutable connected: (string * string) option = None
    let mutable discovered: string list option = None
    let mutable discoveryStarted = false
    let mutable disposed = false

    let chipRow = WrapPanel(Orientation = Orientation.Horizontal)
    // 决策 274：与「连接主机」分开说——这一步是给已连上的主机添加模型服务（AI 能力来源）。
    let title = Ui.title "添加一个模型服务"
    let subtitle = Ui.caption "主机已连上。再选一家模型服务、粘贴密钥，就能开始对话；密钥只保存在主机上。"
    /// 决策 274：兼容 OpenAI 的自定义地址不再藏在「更多」里，作为常驻选项直接给出地址框。
    let customPreset = ProviderPresets.tryFind "custom"
    let _, customUrlBox = Ui.textField "https://example.com/v1"
    let customUrlGroup = Ui.inputFieldGroup "服务地址" "兼容 OpenAI 的接口地址，通常以 /v1 结尾。" customUrlBox
    let _, keyBox = Ui.textField "粘贴 API Key"
    let keyGroup = Ui.inputFieldGroup "API Key" "只保存在主机上，界面不会回显。" keyBox
    let hintText =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontCaption,
            Foreground = Tokens.textMuted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = ReadingRhythm.captionLineHeight)
    let statusText =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontCaption,
            Foreground = Tokens.textMuted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = ReadingRhythm.captionLineHeight,
            IsVisible = false,
            Focusable = true)
    let discoveryPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceMd, IsVisible = false)
    let mutable connectButton: Border = null
    let mutable switchButton: Border = null
    let mutable chipsHost: Control = null

    let setStatus (message: string) (isError: bool) =
        statusText.Text <- message
        statusText.Foreground <- (if isError then Tokens.danger else Tokens.textMuted)
        statusText.IsVisible <- not (String.IsNullOrWhiteSpace message)

    /// 实际使用的预设：探测失败后可改用同一家的兼容端点。
    let effectivePreset () : ProviderPreset =
        match useAlternate, selected.alternate with
        | true, Some(kind, url) -> { selected with kind = kind; baseUrl = url }
        | _ when selected.key = "custom" ->
            { selected with label = "自定义 OpenAI 兼容端点"; baseUrl = (if isNull customUrlBox.Text then "" else customUrlBox.Text.Trim()) }
        | _ -> selected

    let syncControls () =
        let busy = step <> Idle
        Ui.setButtonPending connectButton busy "连接" "正在连接…"
        keyGroup.IsVisible <- selected.needsApiKey
        customUrlGroup.IsVisible <- (selected.key = "custom")
        customUrlBox.IsEnabled <- not busy
        keyBox.IsEnabled <- not busy
        hintText.Text <- selected.hint
        hintText.IsVisible <- not (String.IsNullOrWhiteSpace selected.hint)

    let fail (message: string) =
        step <- Idle
        attemptId <- None
        setStatus message true
        syncControls ()

    /// 第二次写回：把探测到的清单与默认模型落盘（同一个 upsert，apiKey 缺省即保持原值）。
    let applyProbed (id: string) (models: string list) =
        let preset = effectivePreset ()
        let defaultModel = Onboarding.pickDefaultModel preset models
        step <- Applying
        setStatus "已获取模型列表，正在选定默认模型…" false
        syncControls ()
        let payload =
            ProviderPresets.payload id preset.label preset.kind preset.baseUrl None models defaultModel true 120 2
        actions.upsertProvider payload (fun ok ->
            if ok then
                connected <- Some(id, defaultModel)
                step <- Idle
                attemptId <- None
                setStatus (sprintf "已连接 %s，默认模型 %s。" preset.label defaultModel) false
                syncControls ()
            else
                fail "保存模型列表失败：配置没有写入。请重试一次，仍不行就到设置里添加。")

    let connect () =
        if step = Idle then
            let preset = effectivePreset ()
            let key = if isNull keyBox.Text then "" else keyBox.Text.Trim()
            if preset.key = "custom" && String.IsNullOrWhiteSpace preset.baseUrl then
                setStatus "还没有填写服务地址。请先填入兼容 OpenAI 的接口地址，再点「连接」。" true
                customUrlBox.Focus() |> ignore
            elif preset.needsApiKey && String.IsNullOrWhiteSpace key then
                setStatus "还没有填写密钥。请先粘贴 API Key，再点「连接」。" true
                keyBox.Focus() |> ignore
            else
                let id = ProviderPresets.suggestId preset (actions.takenIds ())
                step <- Saving
                attemptId <- Some id
                setStatus "正在保存模型服务…" false
                syncControls ()
                let payload =
                    ProviderPresets.presetPayload preset id (if preset.needsApiKey then Some key else None) []
                actions.upsertProvider payload (fun ok ->
                    if ok then
                        step <- Probing
                        setStatus (sprintf "正在向 %s 获取模型列表…" preset.label) false
                        syncControls ()
                        actions.probeProvider id
                    else
                        fail "保存失败：配置没有写入。请检查密钥后重试，或到设置里查看详情。")

    let rec rebuildChips () =
        chipRow.Children.Clear()
        let chip (preset: ProviderPreset) =
            let tone = if preset.key = selected.key then Ui.Primary else Ui.Secondary
            let button =
                Ui.button tone preset.label (fun () ->
                    if step = Idle then
                        selected <- preset
                        useAlternate <- false
                        setStatus "" false
                        rebuildChips ()
                        syncControls ()
                        if preset.key = "custom" then customUrlBox.Focus() |> ignore
                        elif preset.needsApiKey then keyBox.Focus() |> ignore)
            button.Margin <- Thickness(0.0, 0.0, Spacing.spaceMd, Spacing.spaceMd)
            Avalonia.Automation.AutomationProperties.SetHelpText(button, "选择这家模型服务：" + preset.label)
            button
        for preset in ProviderPresets.featured do
            chipRow.Children.Add(chip preset)
        customPreset |> Option.iter (fun p -> chipRow.Children.Add(chip { p with label = "自定义地址" }))
        let moreLabel =
            if selected.key <> "custom" && ProviderPresets.more |> List.exists (fun p -> p.key = selected.key) then
                "更多：" + selected.label
            else
                "更多"
        let moreButton = Ui.button Ui.Ghost moreLabel ignore
        moreButton.Margin <- Thickness(0.0, 0.0, Spacing.spaceMd, Spacing.spaceMd)
        Avalonia.Automation.AutomationProperties.SetName(moreButton, "更多模型服务")
        Ui.onClick moreButton (fun () ->
            if step = Idle then
                Menu.show
                    overlay
                    (moreButton :> Control)
                    false
                    [ for preset in Onboarding.selectablePresets do
                          if not (List.contains preset ProviderPresets.featured) then
                              MenuEntry.create preset.label (fun () ->
                                  selected <- preset
                                  useAlternate <- false
                                  setStatus "" false
                                  rebuildChips ()
                                  syncControls ()
                                  if preset.needsApiKey then keyBox.Focus() |> ignore)
                              |> MenuEntry.markSelected (preset.key = selected.key) ])
        chipRow.Children.Add moreButton

    let root =
        let form = StackPanel(Orientation = Orientation.Vertical, Spacing = Spacing.spaceXl)
        let advanced =
            Ui.button Ui.Ghost "需要更多选项？打开设置" (fun () -> actions.openAdvanced ())
        advanced.HorizontalAlignment <- HorizontalAlignment.Left
        advanced.Margin <- Thickness 0.0
        Avalonia.Automation.AutomationProperties.SetName(advanced, "高级设置")
        connectButton <- Ui.button Ui.Primary "连接" connect
        connectButton.HorizontalAlignment <- HorizontalAlignment.Left
        Ui.preparePendingButton connectButton
        Avalonia.Automation.AutomationProperties.SetName(connectButton, "连接")
        switchButton <-
            Ui.button Ui.Secondary "改用兼容端点" (fun () ->
                if step = Idle then
                    useAlternate <- not useAlternate
                    switchButton.IsVisible <- false
                    setStatus "已切换传输，请再点「连接」重试。" false
                    syncControls ())
        switchButton.IsVisible <- false
        switchButton.HorizontalAlignment <- HorizontalAlignment.Left
        Avalonia.Automation.AutomationProperties.SetLiveSetting(
            statusText,
            Avalonia.Automation.AutomationLiveSetting.Assertive)
        Avalonia.Automation.AutomationProperties.SetName(statusText, "连接状态")
        Avalonia.Automation.AutomationProperties.SetName(keyBox, "API Key")
        keyBox.PasswordChar <- '●'
        keyBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                connect ())
        form.Children.Add title
        form.Children.Add subtitle
        form.Children.Add discoveryPanel
        form.Children.Add chipRow
        form.Children.Add hintText
        form.Children.Add customUrlGroup
        form.Children.Add keyGroup
        form.Children.Add statusText
        form.Children.Add switchButton
        form.Children.Add connectButton
        form.Children.Add advanced
        let card =
            Ui.groupingCard (Thickness(Spacing.space3xl, Spacing.space3xl)) form Spacing.Radius.lg
        card.MaxWidth <- ContentMetrics.emptyStateMaxWidth
        // 拉伸到 MaxWidth（受限后自动居中）：宽度不随内容变化，
        // 出现错误提示或切换服务商时卡片不再忽宽忽窄（视觉走查 06 号截图）。
        card.HorizontalAlignment <- HorizontalAlignment.Stretch
        Avalonia.Automation.AutomationProperties.SetName(card, "添加一个模型服务")
        card :> Control

    do
        rebuildChips ()
        syncControls ()

    /// 兼容端点切换：探测失败且这家有另一条传输时给出的下一步。
    member private this.OfferSwitch(failure: ProbeFailureKind) =
        let canSwitch = selected.alternate.IsSome && ProbeFailureKind.suggestsTransportSwitch failure
        switchButton.IsVisible <- canSwitch
        if canSwitch then
            Ui.setButtonText switchButton (if useAlternate then "改回原生接口" else "改用兼容端点")

    member _.Root: Control = root

    member _.Step = step

    /// 正在连接中：外壳据此在目录暂时「就绪」时仍保留卡片，避免中途被收起。
    member _.IsBusy = step <> Idle

    member _.SelectedPreset = selected

    member _.Discovered = discovered

    /// 探测结果入口。返回 true 表示这是本卡片发起的探测（设置页不应再处理）。
    member this.ApplyProbe(providerId: string, ok: bool, models: string list, error: string option) : bool =
        match step, attemptId with
        | Probing, Some id when id = providerId ->
            if ok then
                let usable = if List.isEmpty models then (effectivePreset ()).models else models
                applyProbed id usable
            else
                let failure, message = ProbeFailureKind.parse (error |> Option.defaultValue "未知原因")
                // 探测失败：撤销刚创建的模型服务，免得留下一个连不通的半成品。
                actions.discardProvider id
                fail message
                this.OfferSwitch failure
            true
        | _ -> false

    /// 本卡片刚连接成功且目录里已能用时，取走 (id, 模型) 交给外壳收尾（只取一次）。
    member _.TakeConnected(catalog: Catalog) : (string * string) option =
        match connected with
        | Some(id, model) ->
            match Catalog.tryProvider id catalog with
            | Some p when p.enabled && not (List.isEmpty p.models) ->
                connected <- None
                Some(id, model)
            | _ -> None
        | None -> None

    /// 外壳在卡片显示时调用：发现本机 Ollama（每次显示只探一次）。
    member this.StartDiscovery() =
        if not discoveryStarted then
            discoveryStarted <- true
            let task = actions.discoverLocal ()
            task.ContinueWith(fun (t: Task<string list option>) ->
                let result = if t.IsCompletedSuccessfully then t.Result else None
                ui.Post(fun () -> if not disposed then this.ShowDiscovered result))
            |> ignore

    member this.ShowDiscovered(result: string list option) =
        discovered <- result
        discoveryPanel.Children.Clear()
        match result with
        | Some models when not (List.isEmpty models) ->
            let use' =
                Ui.button Ui.Secondary "一键使用本机 Ollama" (fun () ->
                    if step = Idle then
                        let preset = ProviderPresets.tryFind "ollama" |> Option.get
                        let id = ProviderPresets.suggestId preset (actions.takenIds ())
                        selected <- preset
                        useAlternate <- false
                        attemptId <- Some id
                        step <- Saving
                        rebuildChips ()
                        setStatus "正在保存本机 Ollama…" false
                        syncControls ()
                        // 默认模型 = 本机已装的第一个：那是用户自己的选择，预设里的名字未必装了。
                        let defaultModel = List.head models
                        let payload =
                            ProviderPresets.payload id preset.label preset.kind preset.baseUrl None models defaultModel true 120 2
                        actions.upsertProvider payload (fun ok ->
                            if ok then
                                connected <- Some(id, defaultModel)
                                step <- Idle
                                attemptId <- None
                                setStatus (sprintf "已连接本机 Ollama，默认模型 %s。" defaultModel) false
                                syncControls ()
                            else
                                fail "保存失败：配置没有写入。请重试，或到设置里添加。"))
            use'.HorizontalAlignment <- HorizontalAlignment.Left
            Avalonia.Automation.AutomationProperties.SetName(use', "一键使用本机 Ollama")
            discoveryPanel.Children.Add(
                Ui.caption (sprintf "发现本机 Ollama 正在运行，有 %d 个模型。" (List.length models)))
            discoveryPanel.Children.Add use'
            discoveryPanel.IsVisible <- true
        | _ -> discoveryPanel.IsVisible <- false

    /// 卡片被收起（有可用模型）后复位，下次再显示是干净的。
    member _.Reset() =
        if step = Idle then
            keyBox.Text <- ""
            setStatus "" false
            useAlternate <- false
            attemptId <- None
            discoveryStarted <- false

    member _.FocusInitial() =
        if selected.needsApiKey then keyBox.Focus() |> ignore else connectButton.Focus() |> ignore

    member _.Dispose() = disposed <- true
