namespace Wanxiang.Tests

open System
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Agui

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
    let ``removed handshake events are not produced`` () =
        // Hello/UpgradeRequired 已删除（SSOT 55.4）：出站不再产生这两个名字
        let helloJson = WireAgui.encode (Hello {| protocol = "wanxiang"; version = 1; instanceId = None |})
        Assert.DoesNotContain("protocol.hello", helloJson)
