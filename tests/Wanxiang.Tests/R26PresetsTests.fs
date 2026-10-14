module Wanxiang.Tests.R26PresetsTests

open Wanxiang2.SettingsScreen
open System
open System.Text.Json.Nodes
open Avalonia.Controls
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.Config
open Wanxiang.Server
open Wanxiang.Protocol
open Wanxiang.UI
open Wanxiang2.MessageCardView

// 决策 431–436：对话预设——主机落盘、目录同步、套用回落、入口只在有预设时出现。

let private instance = Guid.Parse "0192b4a0-0000-7000-8000-0000000000aa"

let private samplePreset : ConversationPreset =
    { id = "writer"
      name = "写作助手"
      provider = "openai"
      model = "gpt-4o"
      instructions = Some "你是一位严谨的中文编辑。\n只改表达，不改原意。"
      tools = [ "builtin:time" ]
      temperature = Some 0.4
      topP = None
      maxTokens = Some 2048
      thinkingBudget = Some 0 }

[<Fact>]
let ``presets roundtrip through toml and bump configVersion to 4`` () =
    let cfg = { AppConfig.defaults instance with presets = Map.ofList [ "writer", samplePreset ] }
    let text = TomlCodec.serialize cfg
    Assert.Contains("configVersion = 4", text)
    match TomlCodec.tryParse text with
    | Ok back -> Assert.Equal(samplePreset, back.presets["writer"])
    | Error e -> failwithf "%A" e

[<Fact>]
let ``config without presets keeps old version and writes no presets table`` () =
    let text = TomlCodec.serialize (AppConfig.defaults instance)
    Assert.DoesNotContain("presets", text)
    Assert.Contains("configVersion = 2", text)

[<Fact>]
let ``invalid preset parameters reject the config`` () =
    let cfg = { AppConfig.defaults instance with presets = Map.ofList [ "writer", { samplePreset with temperature = Some 0.5 } ] }
    let text = (TomlCodec.serialize cfg).Replace("temperature = 0.5", "temperature = 5.0")
    match TomlCodec.tryParse text with
    | Ok _ -> failwith "expected rejection"
    | Error e -> Assert.Contains(e, fun m -> m.StartsWith "presets.writer.temperature")

[<Fact>]
let ``upsert preset validates names and generates ids`` () =
    let cfg = AppConfig.defaults instance
    let payload name =
        let o = JsonObject()
        o["name"] <- name
        o["model"] <- "gpt-4o"
        o["provider"] <- "openai"
        o
    match ConfigMutation.upsertPreset cfg (payload "写作助手") with
    | Error e -> failwithf "%A" e
    | Ok next ->
        let created = next.presets |> Map.toList |> List.exactlyOne |> snd
        Assert.Equal("写作助手", created.name)
        Assert.False(String.IsNullOrWhiteSpace created.id)
        // 不区分大小写的同名被拒；空名被拒
        Assert.True(Result.isError (ConfigMutation.upsertPreset next (payload "写作助手")))
        Assert.True(Result.isError (ConfigMutation.upsertPreset next (payload "  ")))
        // 同 id 覆盖（重命名）可以
        let rename = payload "编辑"
        rename["id"] <- created.id
        match ConfigMutation.upsertPreset next rename with
        | Ok renamed -> Assert.Equal("编辑", renamed.presets[created.id].name)
        | Error e -> failwithf "%A" e
        Assert.True(Result.isOk (ConfigMutation.deletePreset next created.id))
        Assert.True(Result.isError (ConfigMutation.deletePreset next "missing"))

[<Fact>]
let ``preset events survive a wire roundtrip`` () =
    let rid = Guid.NewGuid()
    let o = JsonObject()
    o["name"] <- "写作助手"
    match WireCodec.tryDecode (WireCodec.encode (ConfigUpsertPreset {| requestId = rid; preset = o |})) with
    | Ok(ConfigUpsertPreset d) ->
        Assert.Equal(rid, d.requestId)
        Assert.Equal("写作助手", d.preset["name"].GetValue<string>())
    | other -> failwithf "%A" other
    match WireCodec.tryDecode (WireCodec.encode (ConfigDeletePreset {| requestId = rid; presetId = "writer" |})) with
    | Ok(ConfigDeletePreset d) -> Assert.Equal("writer", d.presetId)
    | other -> failwithf "%A" other

let private provider id models : ProviderInfo =
    { id = id; label = id.ToUpperInvariant(); kind = "openai"; baseUrl = "http://x"; hasApiKey = true
      models = models; defaultModel = List.head models; timeoutSeconds = 60; maxRetries = 0; enabled = true
      visionOverrides = Map.empty; visionDetected = Map.empty }

let private catalogWithPresets () =
    let cfg =
        { AppConfig.defaults instance with
            presets = Map.ofList [ "writer", samplePreset ] }
    Catalog.parse (JsonArray()) (JsonArray()) (ProviderCatalog.generation cfg)

let rec private descendants (control: Avalonia.Visual) : seq<Avalonia.Visual> =
    seq {
        yield control
        for child in control.GetVisualChildren() do
            yield! descendants child
    }

let private chatActions : ChatActions =
    { renameTitle = ignore
      openSessionSettings = ignore
      forkFromHere = ignore
      stopGeneration = ignore
      requestOlderHistory = ignore
      retryLast = ignore
      toggleSidebar = ignore
      focusHome = ignore
      message =
        { CopyText = ignore
          Regenerate = ignore
          EditAndFork = ignore
          DeleteMessage = ignore
          DownloadAttachment = ignore
          OpenLink = ignore; CopyLink = None; Recover = None } }

let private presetChips (root: Avalonia.Visual) =
    descendants root |> Seq.choose (function :? Control as b when b.Name = "preset-chip" -> Some b | _ -> None) |> List.ofSeq

[<Trait("Category", "UI")>]
[<Fact>]
let ``empty state shows preset chips only when presets exist`` () =
    Headless.run (fun () ->
    let chat = ChatView(chatActions, SharedTheme.logo)
    chat.Build()
    let window = Window(Content = chat, Width = 900.0, Height = 700.0)
    window.Show()
    chat.ShowEmpty(EmptyConversation, None)
    window.UpdateLayout()
    Assert.Empty(presetChips window)
    let mutable applied = ""
    chat.SetPresetStarters [ "写作助手", "GPT-4o", (fun () -> applied <- "写作助手"); "代码审查", "Claude", ignore ]
    chat.ShowEmpty(EmptyConversation, None)
    window.UpdateLayout()
    let chips = presetChips window
    Assert.Equal(2, chips.Length)
    Assert.Equal("套用预设 写作助手", Avalonia.Automation.AutomationProperties.GetName chips.Head)
    chat.SetPresetStarters []
    chat.ShowEmpty(EmptyConversation, None)
    window.UpdateLayout()
    Assert.Empty(presetChips window)
    window.Close()
    )

// ---------- 截图（仅在设置 WANXIANG_UX_ARTIFACT_DIR 时落盘） ----------

open System.Reflection
open Avalonia.Threading
open Avalonia.Input
open Avalonia.Headless

let private flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

let private capture (window: Window) (name: string) =
    match Environment.GetEnvironmentVariable "WANXIANG_UX_ARTIFACT_DIR" with
    | null | "" -> ()
    | directory ->
        IO.Directory.CreateDirectory directory |> ignore
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick()
        Dispatcher.UIThread.RunJobs()
        use frame = window.CaptureRenderedFrame()
        frame.Save(IO.Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default)

let private hostConfig (withPresets: bool) =
    let p : ProviderConfig =
        { id = "openai"; kind = "openai"; label = "OpenAI"; baseUrl = "https://api.openai.com/v1"; apiKey = Some "k"
          models = [ "gpt-4o"; "gpt-4o-mini" ]; defaultModel = "gpt-4o"; timeoutSeconds = 60; maxRetries = 0
          enabled = true; promptCaching = false; headers = Map.empty; extraJson = None; visionOverrides = Map.empty }
    let review = { samplePreset with id = "review"; name = "代码审查"; model = "gpt-4o-mini"; instructions = Some "逐行审查代码，先列风险再给修改建议。"; tools = []; temperature = Some 0.2 }
    let translate = { samplePreset with id = "translate"; name = "中英翻译"; instructions = Some "把输入翻译成地道的英文，保留术语。"; tools = []; maxTokens = None }
    { AppConfig.defaults instance with
        providers = Map.ofList [ "openai", p ]
        presets = if withPresets then Map.ofList [ "writer", samplePreset; "review", review; "translate", translate ] else Map.empty }

let private shellWith (cfg: AppConfig) (dark: bool) =
    MotionPolicy.setReduced true
    Tokens.apply (if dark then ThemeMode.Dark else ThemeMode.Light)
    Tokens.completeTransition ()
    let view = MainView()
    view.Build()
    let window = Window(Width = 1180.0, Height = 760.0, Content = view)
    window.RequestedThemeVariant <- (if dark then Avalonia.Styling.ThemeVariant.Dark else Avalonia.Styling.ThemeVariant.Light)
    window.Show()
    typeof<MainView>.GetField("authenticated", flags).SetValue(view, true)
    typeof<MainView>.GetMethod("HandleEvent", flags).Invoke(
        view,
        [| box (CatalogSnapshot {| providers = ProviderCatalog.providers cfg; tools = JsonArray(); generation = ProviderCatalog.generation cfg |}) |])
    |> ignore
    typeof<MainView>.GetMethod("Render", flags, null, Type.EmptyTypes, null).Invoke(view, [||]) |> ignore
    Dispatcher.UIThread.RunJobs()
    view, window

let private pressEnter (c: Control) =
    c.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = c))
    Dispatcher.UIThread.RunJobs()

[<Trait("Category", "UI")>]
[<Fact>]
let ``r26 截图：预设入口、另存、设置管理`` () =
    Headless.run (fun () ->
    // 1 空态：从预设开始
    let view, w1 = shellWith (hostConfig true) false
    Assert.Equal(3, (typeof<MainView>.GetField("chat", flags).GetValue(view) :?> ChatView).PresetStarterCount)
    capture w1 "01-empty-state-preset-chips"
    // 2 侧栏 ▾：用预设新建
    let sidebar = typeof<MainView>.GetField("sidebar", flags).GetValue(view) :?> Sidebar
    Assert.True(sidebar.PresetMenuButton.IsVisible)
    pressEnter sidebar.PresetMenuButton
    capture w1 "02-sidebar-new-from-preset-menu"
    let overlay = typeof<MainView>.GetField("overlay", flags).GetValue(view) :?> OverlayHost
    overlay.CloseDialog()
    overlay.HandleEscape() |> ignore
    Dispatcher.UIThread.RunJobs()
    // 3 命令面板：输入「预设」
    view.ShowCommandPalette()
    Dispatcher.UIThread.RunJobs()
    let paletteBox =
        descendants w1 |> Seq.choose (function :? TextBox as t when t.IsEffectivelyVisible && t.IsFocused -> Some t | _ -> None) |> Seq.tryHead
    paletteBox |> Option.iter (fun t -> t.Text <- "预设")
    Dispatcher.UIThread.RunJobs()
    capture w1 "03-command-palette-presets"
    overlay.HandleEscape() |> ignore
    Dispatcher.UIThread.RunJobs()
    // 4 存为预设（名称默认取对话标题）
    Dialogs.prompt overlay "存为预设" "预设名称" "周报润色" "保存" (fun _ _ -> ())
    Dispatcher.UIThread.RunJobs()
    capture w1 "04-save-as-preset-dialog"
    overlay.CloseDialog()
    // 5 设置 › 对话默认值：预设列表
    typeof<MainView>.GetMethod("ShowSettings", flags).Invoke(view, [||]) |> ignore
    let settings = typeof<MainView>.GetField("settings", flags).GetValue(view) :?> Wanxiang2.SettingsScreen.SettingsScreen
    settings.ShowSection Wanxiang2.SettingsScreen.SettingsPart.Generation
    Dispatcher.UIThread.RunJobs()
    settings.General.PresetsPanel.BringIntoView()
    Dispatcher.UIThread.RunJobs()
    Assert.Equal(3, descendants settings.General.PresetsPanel |> Seq.filter (function :? Control as c -> c.Name = "preset-row" | _ -> false) |> Seq.length)
    capture w1 "05-settings-presets-list"
    w1.Close()
    // 6 首次使用：没有预设时空态与侧栏不多任何东西
    let view2, w2 = shellWith (hostConfig false) true
    let sidebar2 = typeof<MainView>.GetField("sidebar", flags).GetValue(view2) :?> Sidebar
    Assert.False(sidebar2.PresetMenuButton.IsVisible)
    Assert.Equal(0, (typeof<MainView>.GetField("chat", flags).GetValue(view2) :?> ChatView).PresetStarterCount)
    capture w2 "06-first-run-no-presets"
    w2.Close()
    Tokens.apply ThemeMode.Light
    Tokens.completeTransition ()
    )
