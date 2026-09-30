namespace Wanxiang.Tests

open System
open System.Text.Json.Nodes
open System.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Agui
open Wanxiang.Client

/// WireEvent ⇄ AG-UI 承载适配（SSOT `wanxiang-protocol` 第 55 节）。
///
/// 核心契约：**语义层不换，承载层换**。56 个 WireEvent 变体保持原样，
/// 线上字节从私有 `{type, payload}` 外壳换成 AG-UI 事件对象
/// （标准面 + `wanxiang.dev/*` CUSTOM 面）。
module WireAguiTests =

    let private convId = Guid.NewGuid()
    let private genId = Guid.NewGuid()

    // ---------- 标准面（生成流 → AG-UI 标准事件） ----------

    [<Fact>]
    let ``generation started maps to RUN_STARTED`` () =
        let json =
            WireAgui.encode (GenerationStarted {| conversationId = convId; generationId = genId; providerId = "p"; model = "m" |})
        Assert.Contains("\"type\":\"RUN_STARTED\"", json)
        Assert.Contains(sprintf "\"threadId\":\"%O\"" convId, json)
        Assert.Contains(sprintf "\"runId\":\"%O\"" genId, json)

    /// MAF ChatMessage JSON 形状（编排层真实出站形态）
    let private mafText (text: string) : JsonObject =
        let p = JsonObject()
        p["role"] <- "assistant"
        let cs = JsonArray()
        let c = JsonObject()
        c["$type"] <- "text"
        c["text"] <- text
        cs.Add c
        p["contents"] <- cs
        p

    [<Fact>]
    let ``text delta maps to TEXT_MESSAGE_CHUNK`` () =
        let json = WireAgui.encode (GenerationDelta {| conversationId = convId; generationId = genId; payload = mafText "你好" |})
        Assert.Contains("\"type\":\"TEXT_MESSAGE_CHUNK\"", json)

    [<Fact>]
    let ``non text delta maps to CUSTOM`` () =
        // MAF 形状但 $type 无法映射（如 DataContent 的 dataUri）
        let payload = JsonObject()
        payload["role"] <- "assistant"
        let cs = JsonArray()
        let c = JsonObject()
        c["$type"] <- "dataUri"
        c["dataUri"] <- "data:image/png;base64,AAAA"
        cs.Add c
        payload["contents"] <- cs
        let json = WireAgui.encode (GenerationDelta {| conversationId = convId; generationId = genId; payload = payload |})
        Assert.Contains("\"type\":\"CUSTOM\"", json)
        Assert.Contains("wanxiang.dev/generation-delta", json)
        // AG-UI 序列化对非 ASCII 做 \uXXXX 转义（实测），断言用转义形式。
        Assert.Contains("data:image/png;base64,AAAA", json)

    [<Fact>]
    let ``completed generation maps to RUN_FINISHED`` () =
        let json =
            WireAgui.encode (GenerationFinished {| conversationId = convId; generationId = genId; status = "completed"; error = None; usage = None |})
        Assert.Contains("\"type\":\"RUN_FINISHED\"", json)

    [<Fact>]
    let ``failed generation maps to RUN_ERROR`` () =
        let err = { kind = GenerationErrorKind.ProviderTimeout; message = "超时"; detail = None; retryable = false; retryAfterSeconds = None }
        let json =
            WireAgui.encode (GenerationFinished {| conversationId = convId; generationId = genId; status = "failed"; error = Some err; usage = None |})
        Assert.Contains("\"type\":\"RUN_ERROR\"", json)

    // ---------- CUSTOM 面（其余语义事件） ----------

    [<Fact>]
    let ``cursor advanced rides CUSTOM with wanxiang.dev prefix`` () =
        let json = WireAgui.encode (CursorAdvanced {| id = 42UL |})
        Assert.Contains("\"type\":\"CUSTOM\"", json)
        Assert.Contains("wanxiang.dev/cursor.advanced", json)

    [<Fact>]
    let ``custom round trips back to the same semantic event`` () =
        // 出站 → 入站：CUSTOM 面必须无损还原语义事件
        let original = ObserveConversation {| conversationId = convId |}
        let json = WireAgui.encode original
        match WireAgui.tryDecode json with
        | Ok(Some (ObserveConversation d)) -> Assert.Equal(convId, d.conversationId)
        | other -> failwithf "round trip failed: %A" other

    [<Fact>]
    let ``run started round trips back`` () =
        let original = GenerationStarted {| conversationId = convId; generationId = genId; providerId = "p"; model = "m" |}
        match WireAgui.tryDecode (WireAgui.encode original) with
        | Ok(Some (GenerationStarted d)) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(genId, d.generationId)
        | other -> failwithf "expected GenerationStarted, got %A" other

    // ---------- 宽容性（AG-UI 增量安全） ----------

    [<Fact>]
    let ``an unknown standard event type is ignored not fatal`` () =
        match WireAgui.tryDecode """{"type":"SOME_FUTURE_EVENT","x":1}""" with
        | Ok None -> ()    // 合法忽略
        | Ok(Some _) -> failwith "未知类型不得映射成语义事件"
        | Error e -> failwithf "未知类型不得判为协议违规：%s" e

    [<Fact>]
    let ``a foreign CUSTOM namespace is ignored`` () =
        match WireAgui.tryDecode """{"type":"CUSTOM","name":"someone.else/thing","value":{}}""" with
        | Ok None -> ()
        | other -> failwithf "别家 CUSTOM 应忽略，得到 %A" other

    [<Fact>]
    let ``invalid json is a protocol violation`` () =
        match WireAgui.tryDecode "not json" with
        | Error _ -> ()
        | Ok _ -> failwith "非法 JSON 必须判为协议违规"

    [<Fact>]
    let ``handshake events ride the custom face not a dedicated type`` () =
        // 协议无独立握手事件面：Hello 语义走 CUSTOM wanxiang.dev/protocol.hello
        let helloJson = WireAgui.encode (Hello {| protocol = "wanxiang"; version = 1; instanceId = None |})
        Assert.Contains("\"type\":\"CUSTOM\"", helloJson)
        Assert.Contains("wanxiang.dev/protocol.hello", helloJson)


/// 客户端写命令承载契约（ClientCommand → AG-UI CUSTOM，满足 FullyQualifiedName~WireAguiCommandTests 过滤）
module WireAguiCommandTests =

    let private pickPort () =
        let listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> System.Net.IPEndPoint).Port
        listener.Stop()
        port

    [<Fact>]
    let ``encodeCommand produces AG-UI custom event with wanxiang dev namespace and preserves command shell`` () =
        let invId = Guid.NewGuid()
        let convId = Guid.NewGuid()
        let cmd = RenameConversation {| invocationId = invId; conversationId = convId; title = "new-title" |}
        let json = WireAgui.encodeCommand cmd
        Assert.Contains("\"type\":\"CUSTOM\"", json)
        Assert.Contains("\"name\":\"wanxiang.dev/conversation.rename\"", json)
        Assert.Contains("\"value\":", json)
        Assert.Contains("\"type\":\"conversation.rename\"", json)
        Assert.Contains("\"payload\":", json)
        Assert.Contains("\"title\":\"new-title\"", json)
        let root = JsonNode.Parse(json).AsObject()
        let value = root["value"].AsObject()
        let payload = value["payload"].AsObject()
        Assert.Equal("new-title", payload["title"].GetValue<string>())

    [<Fact>]
    let ``encodeCommand produces AG-UI CUSTOM carrier for CreateConversation`` () =
        let invId = Guid.NewGuid()
        let convId = Guid.NewGuid()
        let cmd =
            CreateConversation {|
                invocationId = invId
                conversationId = convId
                title = "新契约会话"
                config = SessionConfig.empty
            |}
        let json = WireAgui.encodeCommand cmd
        let root = JsonNode.Parse(json).AsObject()
        // 顶层必须是 AG-UI CUSTOM 事件对象
        Assert.Equal("CUSTOM", root["type"].GetValue<string>())
        Assert.Equal("wanxiang.dev/conversation.create", root["name"].GetValue<string>())
        // value 包含完整旧外壳 {type, payload}
        let value = root["value"].AsObject()
        Assert.Equal("conversation.create", value["type"].GetValue<string>())
        let payload = value["payload"].AsObject()
        Assert.Equal(invId.ToString("D"), payload["invocationId"].GetValue<string>())
        Assert.Equal(convId.ToString("D"), payload["threadId"].GetValue<string>())
        Assert.Equal("新契约会话", payload["title"].GetValue<string>())

    [<Fact>]
    let ``encodeCommand produces AG-UI CUSTOM carrier for SendUserMessage`` () =
        let invId = Guid.NewGuid()
        let convId = Guid.NewGuid()
        let msgJson = JsonNode.Parse("""{"role":"user","contents":[{"text":"你好，万象"}]}""")
        let cmd =
            SendUserMessage {|
                invocationId = invId
                conversationId = convId
                messageJson = msgJson
            |}
        let json = WireAgui.encodeCommand cmd
        let root = JsonNode.Parse(json).AsObject()
        Assert.Equal("CUSTOM", root["type"].GetValue<string>())
        Assert.Equal("wanxiang.dev/chat.user-message.enqueue", root["name"].GetValue<string>())
        let value = root["value"].AsObject()
        Assert.Equal("chat.user-message.enqueue", value["type"].GetValue<string>())
        let payload = value["payload"].AsObject()
        Assert.Equal(invId.ToString("D"), payload["invocationId"].GetValue<string>())
        Assert.Equal(convId.ToString("D"), payload["threadId"].GetValue<string>())
        let msgObj = payload["message"].AsObject()
        let contents = msgObj["contents"].AsArray()
        let firstContent = contents.[0].AsObject()
        let textNode = firstContent["text"]
        Assert.Equal("你好，万象", textNode.GetValue<string>())

    [<Fact>]
    let ``encodeCommand output round trips through TolerantReader and server normalization to original command`` () =
        let invId = Guid.NewGuid()
        let convId = Guid.NewGuid()
        let cmd =
            CreateConversation {|
                invocationId = invId
                conversationId = convId
                title = "往返测试会话"
                config = SessionConfig.empty
            |}
        let wireJson = WireAgui.encodeCommand cmd
        // 1. 经 TolerantReader.parse 必须是 Custom，绝非 Unrecognized
        match TolerantReader.parse wireJson with
        | ParsedEvent.Custom(name, value) ->
            Assert.Equal("wanxiang.dev/conversation.create", name)
            Assert.True(name.StartsWith Capabilities.Namespace)
            // 2. 模拟服务端入站命令归一路径（WsConnection.HandleJson 核心逻辑）
            let normalizedPayload =
                match value with
                | :? JsonObject as vo when vo.ContainsKey "type" -> value.ToJsonString()
                | :? JsonObject as vo ->
                    let wrapped = JsonObject()
                    wrapped["type"] <- name.Substring Capabilities.Namespace.Length
                    wrapped["payload"] <- vo.DeepClone()
                    wrapped.ToJsonString()
                | _ -> failwith "value 不是 JsonObject"
            // 3. 归一后还原为原语义命令
            match WireCodec.tryDecodeCommand normalizedPayload with
            | Ok (CreateConversation decoded) ->
                Assert.Equal(invId, decoded.invocationId)
                Assert.Equal(convId, decoded.conversationId)
                Assert.Equal("往返测试会话", decoded.title)
            | other -> failwithf "tryDecodeCommand 解码失败: %A" other
        | other -> failwithf "WireAgui.encodeCommand 输出未能被 TolerantReader 解析为 Custom: %A" other

    [<Fact>]
    let ``regression guard: legacy type payload shell is unrecognized while new carrier is recognized`` () =
        let invId = Guid.NewGuid()
        let convId = Guid.NewGuid()
        let cmd =
            CreateConversation {|
                invocationId = invId
                conversationId = convId
                title = "回归防线会话"
                config = SessionConfig.empty
            |}
        // 旧外壳（WireCodec.encodeCommand 输出）：{ "type": "conversation.create", "payload": { ... } }
        let legacyJson = WireCodec.encodeCommand cmd
        // 新承载（WireAgui.encodeCommand 输出）：{ "type": "CUSTOM", "name": "wanxiang.dev/conversation.create", "value": { ... } }
        let newCarrierJson = WireAgui.encodeCommand cmd

        // 验证缺陷根因：旧外壳被 TolerantReader 判定为 Unrecognized（导致服务端静默忽略，前端完全不可用）
        match TolerantReader.parse legacyJson with
        | ParsedEvent.Unrecognized typeName ->
            Assert.Equal("conversation.create", typeName)
        | other -> failwithf "旧外壳必须被 TolerantReader 判为 Unrecognized，实际得到: %A" other

        // 验证修复契约：新承载被 TolerantReader 正确识别为 Custom
        match TolerantReader.parse newCarrierJson with
        | ParsedEvent.Custom(name, _) ->
            Assert.Equal("wanxiang.dev/conversation.create", name)
        | other -> failwithf "新承载必须被 TolerantReader 判为 Custom，实际得到: %A" other

        // 服务端入站识别判定器（忠实复刻 WsConnection.HandleJson 中的分支行为）：
        // 若客户端退回旧编码，该判定器返回 false；新编码则必须返回 true。
        let serverAcceptsCommand (rawJson: string) : bool =
            match TolerantReader.parse rawJson with
            | ParsedEvent.Custom(name, value) when name.StartsWith Capabilities.Namespace ->
                let payloadText =
                    match value with
                    | :? JsonObject as vo when vo.ContainsKey "type" -> value.ToJsonString()
                    | :? JsonObject as vo ->
                        let wrapped = JsonObject()
                        wrapped["type"] <- name.Substring Capabilities.Namespace.Length
                        wrapped["payload"] <- vo.DeepClone()
                        wrapped.ToJsonString()
                    | _ -> ""
                match WireCodec.tryDecodeCommand payloadText with
                | Ok _ -> true
                | Error _ -> false
            | _ -> false

        Assert.False(serverAcceptsCommand legacyJson)
        Assert.True(serverAcceptsCommand newCarrierJson)

    // =========================================================================
    // 覆盖补充：encodeCommand 对全部 ClientCommand 变体的编码契约
    // =========================================================================

    [<Fact>]
    let ``encodeCommand encodes all remaining ClientCommand variants with custom carrier`` () =
        let invId = Guid.NewGuid()
        let convId = Guid.NewGuid()
        let parentConvId = Guid.NewGuid()
        let commitId = 42UL

        // 1. ForkConversation (with forkAfterId = Some 10UL)
        let forkCmd1 =
            ForkConversation {|
                invocationId = invId
                conversationId = convId
                parentConversationId = parentConvId
                forkAfterId = Some 10UL
                config = SessionConfig.empty
                editedMessageJson = JsonObject()
            |}
        let forkJson1 = WireAgui.encodeCommand forkCmd1
        Assert.Contains("\"type\":\"CUSTOM\"", forkJson1)
        Assert.Contains("wanxiang.dev/conversation.fork", forkJson1)
        Assert.Contains("10", forkJson1)

        // ForkConversation (with forkAfterId = None)
        let forkCmd2 =
            ForkConversation {|
                invocationId = invId
                conversationId = convId
                parentConversationId = parentConvId
                forkAfterId = None
                config = SessionConfig.empty
                editedMessageJson = JsonObject()
            |}
        let forkJson2 = WireAgui.encodeCommand forkCmd2
        Assert.Contains("wanxiang.dev/conversation.fork", forkJson2)

        // 2. RenameConversation
        let renameCmd =
            RenameConversation {|
                invocationId = invId
                conversationId = convId
                title = "新标题"
            |}
        let renameJson = WireAgui.encodeCommand renameCmd
        Assert.Contains("wanxiang.dev/conversation.rename", renameJson)
        Assert.Contains("\\u65B0\\u6807\\u9898", renameJson)

        // 3. DeleteConversation
        let deleteConvCmd =
            DeleteConversation {|
                invocationId = invId
                conversationId = convId
            |}
        let deleteConvJson = WireAgui.encodeCommand deleteConvCmd
        Assert.Contains("wanxiang.dev/conversation.delete", deleteConvJson)

        // 4. DeleteMessage
        let deleteMsgCmd =
            DeleteMessage {|
                invocationId = invId
                conversationId = convId
                messageCommitId = commitId
            |}
        let deleteMsgJson = WireAgui.encodeCommand deleteMsgCmd
        Assert.Contains("wanxiang.dev/message.delete", deleteMsgJson)
        Assert.Contains("42", deleteMsgJson)

        // 5. UpdateConversationConfig
        let updateCfgCmd =
            UpdateConversationConfig {|
                invocationId = invId
                conversationId = convId
                config = SessionConfig.empty
            |}
        let updateCfgJson = WireAgui.encodeCommand updateCfgCmd
        Assert.Contains("wanxiang.dev/conversation.config-update", updateCfgJson)

        // 6. SetConversationFlags
        let flagsCmd =
            SetConversationFlags {|
                invocationId = invId
                conversationId = convId
                pinned = true
                archived = false
            |}
        let flagsJson = WireAgui.encodeCommand flagsCmd
        Assert.Contains("wanxiang.dev/conversation.flags-set", flagsJson)
        Assert.Contains("\"pinned\":true", flagsJson)

        // 7. RegenerateResponse
        let regenCmd =
            RegenerateResponse {|
                invocationId = invId
                conversationId = convId
            |}
        let regenJson = WireAgui.encodeCommand regenCmd
        Assert.Contains("wanxiang.dev/chat.regenerate", regenJson)

    // =========================================================================
    // 覆盖补充：WireAgui.encode 对 GenerationFinished / GenerationDelta / GenerationCancel 的各种分支
    // =========================================================================

    let private convId = Guid.NewGuid()
    let private genId = Guid.NewGuid()

    [<Fact>]
    let ``encode GenerationFinished with usage encodes TokenUsage details`` () =
        let usage: GenerationUsage =
            { promptTokens = Some 100
              completionTokens = Some 50
              cachedTokens = None
              totalTokens = Some 150
              durationMs = None }
        let ev =
            GenerationFinished {|
                conversationId = convId
                generationId = genId
                status = "completed"
                error = None
                usage = Some usage
            |}
        let json = WireAgui.encode ev
        Assert.Contains("\"type\":\"RUN_FINISHED\"", json)
        Assert.Contains("\"inputTokens\":100", json)
        Assert.Contains("\"outputTokens\":50", json)
        Assert.Contains("\"totalTokens\":150", json)

    [<Fact>]
    let ``encode GenerationFinished with cancelled status maps to RUN_FINISHED`` () =
        let ev =
            GenerationFinished {|
                conversationId = convId
                generationId = genId
                status = "cancelled"
                error = None
                usage = None
            |}
        let json = WireAgui.encode ev
        Assert.Contains("\"type\":\"RUN_FINISHED\"", json)

    [<Fact>]
    let ``encode GenerationFinished with other status maps to RUN_ERROR`` () =
        let ev =
            GenerationFinished {|
                conversationId = convId
                generationId = genId
                status = "custom_unknown_status"
                error = None
                usage = None
            |}
        let json = WireAgui.encode ev
        Assert.Contains("\"type\":\"RUN_ERROR\"", json)
        Assert.Contains("custom_unknown_status", json)

    [<Fact>]
    let ``encode GenerationCancel maps to CUSTOM generation.cancel`` () =
        let ev =
            GenerationCancel {|
                conversationId = convId
                generationId = genId
            |}
        let json = WireAgui.encode ev
        Assert.Contains("\"type\":\"CUSTOM\"", json)
        Assert.Contains("wanxiang.dev/generation.cancel", json)

    [<Fact>]
    let ``encode GenerationDelta with functionCall maps to TOOL_CALL_START`` () =
        let payloadStart = JsonObject()
        payloadStart["role"] <- "assistant"
        let csStart = JsonArray()
        let cStart = JsonObject()
        cStart["$type"] <- "functionCall"
        cStart["callId"] <- "call-123"
        cStart["name"] <- "calc_sum"
        csStart.Add cStart
        payloadStart["contents"] <- csStart

        let jsonStart = WireAgui.encode (GenerationDelta {| conversationId = convId; generationId = genId; payload = payloadStart |})
        Assert.Contains("\"type\":\"TOOL_CALL_START\"", jsonStart)
        Assert.Contains("\"toolCallId\":\"call-123\"", jsonStart)
        Assert.Contains("\"toolCallName\":\"calc_sum\"", jsonStart)

    [<Fact>]
    let ``encode GenerationDelta with tool result role maps to TOOL_CALL_RESULT`` () =
        let payloadResult = JsonObject()
        payloadResult["role"] <- "tool"
        let cs = JsonArray()
        let c = JsonObject()
        c["$type"] <- "functionResult"
        c["callId"] <- "call-123"
        c["result"] <- "success result"
        cs.Add c
        payloadResult["contents"] <- cs

        let jsonResult = WireAgui.encode (GenerationDelta {| conversationId = convId; generationId = genId; payload = payloadResult |})
        Assert.Contains("\"type\":\"TOOL_CALL_RESULT\"", jsonResult)
        Assert.Contains("\"toolCallId\":\"call-123\"", jsonResult)
        Assert.Contains("success result", jsonResult)

    [<Fact>]
    let ``encode GenerationDelta with reasoning text maps to CUSTOM delta`` () =
        let payload = JsonObject()
        payload["role"] <- "assistant"
        let cs = JsonArray()
        let c = JsonObject()
        c["$type"] <- "reasoning"
        c["text"] <- "思考中..."
        cs.Add c
        payload["contents"] <- cs

        let json = WireAgui.encode (GenerationDelta {| conversationId = convId; generationId = genId; payload = payload |})
        Assert.Contains("\"type\":\"CUSTOM\"", json)
        Assert.Contains("wanxiang.dev/generation-delta", json)

    // =========================================================================
    // 覆盖补充：WireAgui.tryDecode 对入站标准事件与异常的解析
    // =========================================================================

    [<Fact>]
    let ``tryDecode decodes REASONING_MESSAGE_CHUNK correctly`` () =
        let validJson = sprintf """{"type":"REASONING_MESSAGE_CHUNK","threadId":"%O","messageId":"%O","delta":"思考过程"}""" convId genId
        match WireAgui.tryDecode validJson with
        | Ok (Some (GenerationDelta d)) ->
            Assert.Equal(genId, d.generationId)
            Assert.NotNull(d.payload)
        | other -> failwithf "Expected GenerationDelta, got %A" other

    [<Fact>]
    let ``tryDecode decodes TOOL_CALL_START and validates structure`` () =
        let validJson = sprintf """{"type":"TOOL_CALL_START","toolCallId":"c1","toolCallName":"search"}"""
        match WireAgui.tryDecode validJson with
        | Ok (Some (GenerationDelta d)) ->
            Assert.Equal(Guid.Empty, d.conversationId)
            Assert.NotNull(d.payload)
        | other -> failwithf "Expected GenerationDelta, got %A" other

    [<Fact>]
    let ``tryDecode decodes TOOL_CALL_RESULT and validates structure`` () =
        let validJson = sprintf """{"type":"TOOL_CALL_RESULT","messageId":"%O","toolCallId":"c1","content":"ok result"}""" genId
        match WireAgui.tryDecode validJson with
        | Ok (Some (GenerationDelta d)) ->
            Assert.Equal(genId, d.generationId)
            Assert.NotNull(d.payload)
        | other -> failwithf "Expected GenerationDelta, got %A" other

    [<Fact>]
    let ``tryDecode decodes RUN_FINISHED and RUN_ERROR`` () =
        // 1. RUN_FINISHED
        let validFinished = sprintf """{"type":"RUN_FINISHED","threadId":"%O","runId":"%O"}""" convId genId
        match WireAgui.tryDecode validFinished with
        | Ok (Some (GenerationFinished d)) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(genId, d.generationId)
            Assert.Equal("completed", d.status)
        | other -> failwithf "Expected GenerationFinished, got %A" other

        // 2. RUN_ERROR
        let validError = sprintf """{"type":"RUN_ERROR","message":"failed","code":"timeout"}"""
        match WireAgui.tryDecode validError with
        | Ok (Some (GenerationFinished d)) ->
            Assert.Equal("failed", d.status)
            Assert.True(d.error.IsSome)
            Assert.Equal("failed", d.error.Value.message)
        | other -> failwithf "Expected GenerationFinished error, got %A" other

    [<Fact>]
    let ``tryDecode ignores other standard AG-UI events as Ok None`` () =
        let json = """{"type":"MESSAGES_SNAPSHOT","messages":[]}"""
        match WireAgui.tryDecode json with
        | Ok None -> ()
        | other -> failwithf "Expected Ok None for MESSAGES_SNAPSHOT, got %A" other

    [<Fact>]
    let ``tryDecode ignores other namespace CUSTOM events as Ok None`` () =
        let otherCustom = """{"type":"CUSTOM","name":"other.vendor/custom.event","value":{"key":"val"}}"""
        match WireAgui.tryDecode otherCustom with
        | Ok None -> ()
        | other -> failwithf "Expected Ok None for foreign custom event, got %A" other

    [<Fact>]
    let ``tryDecode handles malformed JSON and Malformed parsed events as Error`` () =
        match WireAgui.tryDecode "{" with
        | Error _ -> ()
        | other -> failwithf "Expected Error for broken json, got %A" other

    [<Fact>]
    let ``tryDecode handles unrecognized event type as Ok None`` () =
        let json = """{"type":"SOME_FUTURE_AGUI_EVENT","data":123}"""
        match WireAgui.tryDecode json with
        | Ok None -> ()
        | other -> failwithf "Expected Ok None for unrecognized event, got %A" other

    [<Fact>]
    let ``tryDecode handles CUSTOM event with flat payload`` () =
        let goodCustom = """{"type":"CUSTOM","name":"wanxiang.dev/cursor.advanced","value":{"id":99}}"""
        match WireAgui.tryDecode goodCustom with
        | Ok (Some (CursorAdvanced d)) -> Assert.Equal(99UL, d.id)
        | other -> failwithf "Expected Ok CursorAdvanced 99UL, got %A" other
