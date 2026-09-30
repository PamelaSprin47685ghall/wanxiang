namespace Wanxiang.Tests

open System
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Media
open Xunit
open Wanxiang.Core
open Wanxiang.UI

module UiPureLogicTests =

    // =========================================================================
    // 1. MediaTypes.fs 纯函数测试（扩展名到 MIME 推断、边界与图片判断）
    // =========================================================================
    module MediaTypesTests =

        [<Fact>]
        let ``ofFileName should resolve known extensions correctly`` () =
            Assert.Equal("image/png", MediaTypes.ofFileName "avatar.PNG")
            Assert.Equal("image/jpeg", MediaTypes.ofFileName "photo.jpg")
            Assert.Equal("image/jpeg", MediaTypes.ofFileName "banner.jpeg")
            Assert.Equal("image/gif", MediaTypes.ofFileName "anim.gif")
            Assert.Equal("image/webp", MediaTypes.ofFileName "hero.webp")
            Assert.Equal("image/svg+xml", MediaTypes.ofFileName "icon.svg")
            Assert.Equal("application/pdf", MediaTypes.ofFileName "doc.pdf")
            Assert.Equal("text/markdown", MediaTypes.ofFileName "README.md")
            Assert.Equal("text/markdown", MediaTypes.ofFileName "spec.markdown")
            Assert.Equal("text/plain", MediaTypes.ofFileName "code.fs")
            Assert.Equal("text/plain", MediaTypes.ofFileName "script.py")
            Assert.Equal("application/json", MediaTypes.ofFileName "config.json")
            Assert.Equal("application/toml", MediaTypes.ofFileName "wanxiang.toml")
            Assert.Equal("application/zip", MediaTypes.ofFileName "archive.zip")
            Assert.Equal("application/gzip", MediaTypes.ofFileName "logs.gz")
            Assert.Equal("application/x-tar", MediaTypes.ofFileName "bundle.tar")

        [<Fact>]
        let ``ofFileName should handle empty, whitespace and unknown extensions`` () =
            Assert.Equal("application/octet-stream", MediaTypes.ofFileName null)
            Assert.Equal("application/octet-stream", MediaTypes.ofFileName "")
            Assert.Equal("application/octet-stream", MediaTypes.ofFileName "   ")
            Assert.Equal("application/octet-stream", MediaTypes.ofFileName "unknown_binary.bin")
            Assert.Equal("application/octet-stream", MediaTypes.ofFileName "no_extension_file")

        [<Fact>]
        let ``isImage should distinguish image media types correctly`` () =
            Assert.True(MediaTypes.isImage "image/png")
            Assert.True(MediaTypes.isImage "IMAGE/JPEG")
            Assert.True(MediaTypes.isImage "image/svg+xml")
            Assert.False(MediaTypes.isImage "application/pdf")
            Assert.False(MediaTypes.isImage "text/plain")
            Assert.False(MediaTypes.isImage null)
            Assert.False(MediaTypes.isImage "")

    // =========================================================================
    // 2. Catalog.fs 纯函数测试（模型名称过滤/能力标签分组/搜索匹配/解析）
    // =========================================================================
    module CatalogTests =

        let sampleCatalogJson () =
            let providers = JsonArray()
            let p1 = JsonObject()
            p1["id"] <- "openai"
            p1["label"] <- "OpenAI Official"
            p1["kind"] <- "openai"
            p1["baseUrl"] <- "https://api.openai.com/v1"
            p1["hasApiKey"] <- true
            let m1 = JsonArray()
            m1.Add("gpt-5")
            m1.Add("gpt-4o")
            p1["models"] <- m1
            p1["defaultModel"] <- "gpt-5"
            p1["timeoutSeconds"] <- 60
            p1["maxRetries"] <- 3
            p1["enabled"] <- true
            providers.Add(p1)

            let p2 = JsonObject()
            p2["id"] <- "disabled-prov"
            p2["label"] <- "Disabled Provider"
            p2["enabled"] <- false
            p2["models"] <- JsonArray()
            providers.Add(p2)

            let tools = JsonArray()
            let t1 = JsonObject()
            t1["id"] <- "builtin:web_search"
            t1["label"] <- "网页搜索"
            t1["description"] <- "搜索全网信息"
            t1["source"] <- "builtin"
            tools.Add(t1)

            let gen = JsonObject()
            gen["temperature"] <- 0.7
            gen["topP"] <- 0.95
            gen["maxTokens"] <- 4096
            gen["instructions"] <- "You are a helpful assistant."
            gen["maxContextMessages"] <- 50
            gen["autoTitle"] <- true
            gen["maxToolRounds"] <- 8
            let toolsObj = JsonObject()
            let roots = JsonArray()
            roots.Add("/home/user/workspace")
            toolsObj["fileReadRoots"] <- roots
            gen["tools"] <- toolsObj

            let mcpArr = JsonArray()
            let mcp1 = JsonObject()
            mcp1["id"] <- "fetch-server"
            mcp1["label"] <- "Fetch Server"
            mcp1["command"] <- "node"
            let mcpArgs = JsonArray()
            mcpArgs.Add("server.js")
            mcp1["args"] <- mcpArgs
            mcp1["callTimeoutSeconds"] <- 30
            mcp1["enabled"] <- true
            gen["mcpServers"] <- mcpArr

            Catalog.parse providers tools gen

        [<Fact>]
        let ``parse and query catalog properties`` () =
            let catalog = sampleCatalogJson ()
            Assert.True(Catalog.isReady catalog)
            Assert.Single(Catalog.usableProviders catalog) |> ignore

            let p = Catalog.tryProvider "openai" catalog
            Assert.True(p.IsSome)
            Assert.Equal("OpenAI Official", p.Value.label)

            let desc = Catalog.describeModel "openai" "gpt-5" catalog
            Assert.Equal("OpenAI Official · gpt-5", desc)

            let descFallback = Catalog.describeModel "unknown" "custom-model" catalog
            Assert.Equal("custom-model", descFallback)

            let descEmpty = Catalog.describeModel "unknown" "" catalog
            Assert.Equal("未选择模型", descEmpty)

            let defSel = Catalog.defaultSelection catalog
            Assert.Equal(Some("openai", "gpt-5"), defSel)

            Assert.Equal(Some 0.7, catalog.generation.temperature)
            Assert.Equal(Some 0.95, catalog.generation.topP)
            Assert.Equal(Some 4096, catalog.generation.maxTokens)
            Assert.Equal(Some "You are a helpful assistant.", catalog.generation.instructions)
            Assert.Equal(50, catalog.generation.maxContextMessages)
            Assert.Equal<string list>([ "/home/user/workspace" ], catalog.generation.fileReadRoots)

        [<Fact>]
        let ``empty catalog defaults`` () =
            let empty = Catalog.empty
            Assert.False(Catalog.isReady empty)
            Assert.Empty(Catalog.usableProviders empty)
            Assert.True(Catalog.tryProvider "openai" empty |> Option.isNone)
            Assert.True(Catalog.defaultSelection empty |> Option.isNone)

    // =========================================================================
    // 3. Markdown.fs 纯函数测试（Markdig 语法树节点抽离/行内公式/代码块解析）
    // =========================================================================
    module MarkdownTests =

        [<Fact>]
        let ``parse should parse headings, paragraphs, lists, and codes`` () =
            let md = """# 标题 1
这是一段包含 **粗体** 与 *斜体* 与 `代码` 的正文。

- 列表项 1
- 列表项 2

```fsharp
let x = 42
```
"""
            let blocks = Markdown.parse md
            Assert.True(blocks.Length >= 4)
            match blocks[0] with
            | MdHeading(1, inlines) ->
                Assert.NotEmpty(inlines)
            | other -> Assert.Fail(sprintf "期望 MdHeading，实际是 %A" other)

            let codes = Markdown.codeBlocks blocks
            Assert.Single(codes) |> ignore
            Assert.Contains("let x = 42", codes[0])

        [<Fact>]
        let ``parse math inlines and math blocks`` () =
            let md = """行内公式 $E = mc^2$ 以及独立公式块：

$$
\int_0^\infty e^{-x} dx = 1
$$
"""
            let blocks = Markdown.parse md
            Assert.NotEmpty(blocks)
            let hasMathInline =
                blocks
                |> List.exists (function
                    | MdParagraph inlines ->
                        inlines |> List.exists (function MdMath(tex, _) -> tex.Contains("mc^2") | _ -> false)
                    | _ -> false)
            Assert.True(hasMathInline, "段落中应当解析出行内公式")

            let hasMathBlock =
                blocks
                |> List.exists (function
                    | MdMathBlock tex -> tex.Contains("\int")
                    | _ -> false)
            Assert.True(hasMathBlock, "应当解析出独占行的公式块")

        [<Fact>]
        let ``parse empty string returns empty list`` () =
            Assert.Empty(Markdown.parse "")
            Assert.Empty(Markdown.parse null)

    // =========================================================================
    // 4. UiControllers.fs 纯函数测试（会话运行状态机、退休队列 FIFO 淘汰、附件草稿控制器）
    // =========================================================================
    module UiControllersTests =

        [<Fact>]
        let ``ConversationRuns state machine and FIFO retirement capacity`` () =
            let runs = ConversationRuns()
            let convId = Guid.NewGuid()
            let genId = Guid.NewGuid()

            // 1. 未开始时 Get 为空
            let r0 = runs.Get(Some convId)
            Assert.False(r0.running)
            Assert.True(r0.generationId.IsNone)

            // 2. Start
            runs.Start(convId, genId)
            let r1 = runs.Get(Some convId)
            Assert.True(r1.running)
            Assert.Equal(Some genId, r1.generationId)

            // 3. Delta
            let msg = { MessageView.empty with text = "生成中..." }
            let accepted = runs.Delta(convId, genId, msg)
            Assert.True(accepted)
            Assert.Equal("生成中...", runs.Get(Some convId).message.Value.text)

            // 4. Finish
            let finished = runs.Finish(convId, genId, None, None)
            Assert.True(finished)
            let r2 = runs.Get(Some convId)
            Assert.False(r2.running)
            Assert.True(r2.generationId.IsNone)

            // 5. 退休台账防止重复 Finish / 迟到 Start
            let dupStart = runs.Start(convId, genId)
            Assert.False(runs.Get(Some convId).running, "已退休的 generationId 不能再启动")

            // 6. 清理
            runs.Clear()
            Assert.Equal(0, runs.RetiredCount)

        [<Fact>]
        let ``AttachmentDraftController upload and draft state transitions`` () =
            let controller = AttachmentDraftController()
            let attId = Guid.NewGuid()
            let upload: AttachmentUpload =
                { attachmentId = attId
                  fileName = "test.png"
                  mediaType = "image/png"
                  size = 1024L
                  sha256 = "dummy-sha256" }

            // 1. Begin
            controller.Begin upload |> ignore
            Assert.True(controller.HasUploading)
            Assert.False(controller.HasReady)
            Assert.False(controller.HasFailed)
            Assert.Single(controller.Items) |> ignore

            // 2. Complete
            let completed = controller.Complete(attId, 1024L)
            Assert.True(completed.IsSome)
            let (_, stillDrafted, _) = completed.Value
            Assert.True(stillDrafted)
            Assert.True(controller.HasReady)
            Assert.False(controller.HasUploading)

            // 3. TryConsumeReady
            let consumed = controller.TryConsumeReady()
            Assert.True(consumed.IsSome)
            Assert.Single(consumed.Value) |> ignore

            // 4. 再次消费为空
            Assert.True(controller.TryConsumeReady().Value |> List.isEmpty)

    // =========================================================================
    // 5. MessageModel.fs 纯函数测试（AG-UI 消息模型与 UI 消息投影/ToolCall 解码）
    // =========================================================================
    module MessageModelTests =

        [<Fact>]
        let ``AttachmentRef formatSize formats correctly`` () =
            Assert.Equal("500 B", AttachmentRef.formatSize 500L)
            Assert.Equal("1.5 KiB", AttachmentRef.formatSize 1536L)
            Assert.Equal("2.0 MiB", AttachmentRef.formatSize (2L * 1024L * 1024L))

        [<Fact>]
        let ``AttachmentRef isImage distinguishes correctly`` () =
            let img = { sha256 = "abc"; size = 10L; mediaType = "image/jpeg"; fileName = "a.jpg" }
            let doc = { sha256 = "def"; size = 20L; mediaType = "application/pdf"; fileName = "b.pdf" }
            Assert.True(AttachmentRef.isImage img)
            Assert.False(AttachmentRef.isImage doc)

        [<Fact>]
        let ``ofJson parses text, reasoning, toolCalls, and attachments`` () =
            let obj = JsonObject()
            obj["role"] <- "assistant"
            obj["text"] <- "你好，我是助手。"
            let contents = JsonArray()

            let reasoningObj = JsonObject()
            reasoningObj["type"] <- "reasoning"
            reasoningObj["text"] <- "思考中..."
            contents.Add(reasoningObj)

            let callObj = JsonObject()
            callObj["type"] <- "functionCall"
            callObj["callId"] <- "call_123"
            callObj["name"] <- "get_weather"
            let callArgs = JsonObject()
            callArgs["city"] <- "Beijing"
            callObj["arguments"] <- callArgs
            contents.Add(callObj)

            obj["contents"] <- contents

            let msg = MessageView.ofJson obj
            Assert.Equal("assistant", msg.role)
            Assert.Equal("你好，我是助手。", msg.text)
            Assert.Equal("思考中...", msg.reasoning)
            Assert.Single(msg.toolCalls) |> ignore
            Assert.Equal("call_123", msg.toolCalls[0].callId)
            Assert.Equal("get_weather", msg.toolCalls[0].name)
            Assert.Contains("Beijing", msg.toolCalls[0].argumentsJson)

        [<Fact>]
        let ``mergeToolResults correlates callId into toolCalls`` () =
            let callMsg =
                { MessageView.empty with
                    role = "assistant"
                    toolCalls = [ { callId = "c1"; name = "search"; argumentsJson = "{}"; result = None } ] }
            let resMsg =
                { MessageView.empty with
                    role = "tool"
                    toolResults = [ ("c1", "search result payload") ] }

            let merged = MessageView.mergeToolResults [ callMsg; resMsg ]
            Assert.Single(merged) |> ignore
            Assert.Equal(Some "search result payload", merged[0].toolCalls[0].result)

    // =========================================================================
    // 6. MathLayout.fs 纯常量与盒模型纯计算
    // =========================================================================
    module MathLayoutTests =

        [<Fact>]
        let ``katexFontFactor constant value is verified`` () =
            Assert.Equal(1.21, MathLayout.katexFontFactor)

        [<Fact>]
        let ``MathBox structure can hold items and baselines`` () =
            let box: MathBox = { width = 100.0; above = 20.0; below = 10.0; items = [] }
            Assert.Equal(100.0, box.width)
            Assert.Equal(20.0, box.above)
            Assert.Equal(10.0, box.below)
            Assert.Empty(box.items)

    // =========================================================================
    // 7. RichBackend.fs & KatexHtml.fs 纯逻辑测试
    // =========================================================================
    module RichBackendAndKatexHtmlTests =

        [<Fact>]
        let ``KatexHtml fontScaleOf computes ratio of size classes`` () =
            // 默认无 class 为 1.0
            Assert.Equal(1.0, KatexHtml.fontScaleOf [])
            // reset-size6 size3 的缩放比率：size6 索引 5 (1.0)，size3 索引 2 (0.7)
            let scaled = KatexHtml.fontScaleOf [ "reset-size6"; "size3" ]
            Assert.Equal(0.7, scaled, 3)

        [<Fact>]
        let ``KatexHtml emOf parses em and px correctly`` () =
            let style = Map.ofList [ "height", "1.5em"; "margin-top", "-0.2em"; "width", "24px" ]
            Assert.Equal(Some 1.5, KatexHtml.emOf style "height")
            Assert.Equal(Some -0.2, KatexHtml.emOf style "margin-top")
            Assert.Equal(Some 24.0, KatexHtml.emOf style "width")
            Assert.True(KatexHtml.emOf style "nonexistent" |> Option.isNone)

        [<Fact>]
        let ``KatexHtml parse and visualSubtree filters katex-html`` () =
            let html = "<span class=\"katex\"><span class=\"katex-mathml\">text</span><span class=\"katex-html\"><span>x</span></span></span>"
            let nodes = KatexHtml.parse html
            Assert.NotEmpty(nodes)
            let vis = KatexHtml.visualSubtree nodes
            Assert.True(vis.IsSome)

        [<Fact>]
        let ``KatexHtml isDisplay detects display formulas`` () =
            let inlineHtml = "<span class=\"katex\"><span class=\"katex-html\">x</span></span>"
            let displayHtml = "<span class=\"katex-display\"><span class=\"katex-html\">x</span></span>"
            Assert.False(KatexHtml.isDisplay (KatexHtml.parse inlineHtml))
            Assert.True(KatexHtml.isDisplay (KatexHtml.parse displayHtml))

        [<Fact>]
        let ``RichBackend Cache stores and evicts by capacity`` () =
            let cache = RichBackend.Cache<string, int>(2)
            cache.Set("a", 1)
            cache.Set("b", 2)
            Assert.Equal(Some 1, cache.TryGet("a"))
            Assert.Equal(Some 2, cache.TryGet("b"))

            // 触发容量淘汰
            cache.Set("c", 3)
            Assert.Equal(Some 3, cache.TryGet("c"))
            // 超出容量 2，最早的 "a" 被淘汰
            Assert.True(cache.TryGet("a").IsNone)

    // =========================================================================
    // 8. ProviderPresets.fs + UiPrefs.fs + ConversationSummary.fs 纯逻辑测试
    // =========================================================================
    module PresetsAndPrefsTests =

        [<Fact>]
        let ``ProviderKind conversions and ProviderPresets all list`` () =
            Assert.Equal("openai", ProviderKind.toText OpenAiCompatible)
            Assert.Equal("anthropic", ProviderKind.toText AnthropicNative)
            Assert.Equal("gemini", ProviderKind.toText GeminiNative)
            Assert.Equal("custom", ProviderKind.toText (UnknownKind "custom"))

            Assert.Equal(OpenAiCompatible, ProviderKind.ofText "openai")
            Assert.Equal(AnthropicNative, ProviderKind.ofText "anthropic")
            Assert.Equal(GeminiNative, ProviderKind.ofText "gemini")
            Assert.Equal(OpenAiCompatible, ProviderKind.ofText "")

            Assert.NotEmpty(ProviderPresets.all)
            let openai = ProviderPresets.tryFind "openai"
            Assert.True(openai.IsSome)
            Assert.Equal("https://api.openai.com/v1", ProviderPresets.normalizeBaseUrl "https://api.openai.com/v1/")

        [<Fact>]
        let ``ThemePreference conversions and resolveTheme`` () =
            Assert.Equal("system", ThemePreference.toText FollowSystem)
            Assert.Equal("light", ThemePreference.toText AlwaysLight)
            Assert.Equal("dark", ThemePreference.toText AlwaysDark)

            Assert.Equal(FollowSystem, ThemePreference.ofText "system")
            Assert.Equal(AlwaysLight, ThemePreference.ofText "light")
            Assert.Equal(AlwaysDark, ThemePreference.ofText "dark")

            let prefs = { UiPrefs.defaults with theme = FollowSystem }
            Assert.Equal(Dark, UiPrefs.resolveTheme prefs true)
            Assert.Equal(Light, UiPrefs.resolveTheme prefs false)

            let darkPrefs = { UiPrefs.defaults with theme = AlwaysDark }
            Assert.Equal(Dark, UiPrefs.resolveTheme darkPrefs false)

        [<Fact>]
        let ``ConversationSummary matching, sorting and grouping`` () =
            let now = DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)
            let item1 =
                { id = Guid.NewGuid()
                  title = "项目讨论"
                  preview = "好的，我们来看一下架构"
                  running = false
                  pinned = true
                  archived = false
                  createdAt = now.AddHours(-2.0)
                  updatedAt = now.AddHours(-1.0)
                  messageCount = 5
                  isFork = false
                  providerId = "openai"
                  model = "gpt-5"
                  lastCommitId = 100UL }
            let item2 =
                { id = Guid.NewGuid()
                  title = "随手记"
                  preview = "买菜清单"
                  running = false
                  pinned = false
                  archived = false
                  createdAt = now.AddDays(-3.0)
                  updatedAt = now.AddDays(-3.0)
                  messageCount = 2
                  isFork = false
                  providerId = "deepseek"
                  model = "deepseek-chat"
                  lastCommitId = 90UL }

            // 搜索匹配
            Assert.True(ConversationSummary.matches "讨论" item1)
            Assert.True(ConversationSummary.matches "gpt-5" item1)
            Assert.True(ConversationSummary.matches "买菜" item2)
            Assert.False(ConversationSummary.matches "无关词" item1)

            // 分组
            let groups = ConversationSummary.group now [ item1; item2 ]
            Assert.NotEmpty(groups)
            Assert.Equal("置顶", groups[0].label)
            Assert.Single(groups[0].items) |> ignore
            Assert.Equal("项目讨论", groups[0].items[0].title)
