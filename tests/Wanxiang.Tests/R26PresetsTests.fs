module Wanxiang.Tests.R26PresetsTests

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

[<Fact>]
let ``catalog snapshot carries presets to clients`` () =
    let catalog = catalogWithPresets ()
    let p = List.exactlyOne catalog.presets
    Assert.Equal("写作助手", p.name)
    Assert.Equal(Some 0.4, p.temperature)
    Assert.Equal<string list>([ "builtin:time" ], p.tools)
    // 旧主机没有 presets 字段 → 空列表
    Assert.Empty((Catalog.parse (JsonArray()) (JsonArray()) (JsonObject())).presets)

[<Fact>]
let ``applying a preset falls back when its model or tools are gone`` () =
    let preset = List.exactlyOne (catalogWithPresets ()).presets
    let tool : ToolInfo = { id = "builtin:time"; label = "time"; description = ""; source = "builtin"; serverId = None }
    let ok = { Catalog.empty with providers = [ provider "openai" [ "gpt-4o"; "gpt-4o-mini" ] ]; tools = [ tool ] }
    let config, note = Presets.toSessionConfig ok preset
    Assert.Equal(("openai", "gpt-4o"), (config.provider, config.model))
    Assert.Equal<string list>([ "builtin:time" ], config.tools)
    Assert.Equal(Some 2048, config.maxTokens)
    Assert.True(note.IsNone)
    // 模型服务被删 + 工具消失：回落默认模型，并说明原因
    let gone = { Catalog.empty with providers = [ provider "anthropic" [ "claude" ] ] }
    let config2, note2 = Presets.toSessionConfig gone preset
    Assert.Equal(("anthropic", "claude"), (config2.provider, config2.model))
    Assert.Empty(config2.tools)
    Assert.Contains("已不可用", note2.Value)
    Assert.Contains("1 个工具", note2.Value)

[<Fact>]
let ``preset names are suggested from the title without collisions`` () =
    let existing = (catalogWithPresets ()).presets
    Assert.Equal("写作助手 2", Presets.suggestName existing "写作助手")
    Assert.Equal("我的预设", Presets.suggestName existing "新对话")
    Assert.Contains("gpt-4o", Presets.describe (catalogWithPresets ()) existing.Head)

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
        { copyText = ignore
          regenerate = ignore
          editAndFork = ignore
          deleteMessage = ignore
          downloadAttachment = ignore
          openLink = ignore } }

let private presetChips (root: Avalonia.Visual) =
    descendants root |> Seq.choose (function :? Control as b when b.Name = "preset-chip" -> Some b | _ -> None) |> List.ofSeq

[<Trait("Category", "UI")>]
[<Fact>]
let ``empty state shows preset chips only when presets exist`` () =
    Headless.run (fun () ->
    let chat = ChatView(chatActions, Brand.logo)
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
