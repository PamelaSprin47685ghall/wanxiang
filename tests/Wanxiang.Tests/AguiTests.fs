namespace Wanxiang.Tests

open System.Text.Json.Nodes
open Xunit
open AGUI.Abstractions
open Wanxiang.Agui

/// AG-UI 能力与协议基础测试（SSOT `wanxiang-protocol` 第 55 节）。
///
/// 覆盖：
/// 1. Capabilities 模块全部常量与扩展能力集合定义；
/// 2. TolerantReader 宽容读取器全部可测分支与边界。
module AguiTests =

    // =========================================================================
    // 1. Capabilities 契约测试
    // =========================================================================

    [<Fact>]
    let ``Capabilities protocol version is 1.0`` () =
        Assert.Equal("1.0", Capabilities.ProtocolVersion)

    [<Fact>]
    let ``Capabilities namespace prefix is wanxiang.dev/`` () =
        Assert.Equal("wanxiang.dev/", Capabilities.Namespace)

    [<Fact>]
    let ``Capabilities extensions list contains all documented capabilities`` () =
        // Capabilities.extensions 是运行时 list，对其断言直接执行模块初始化代码（提升行覆盖）
        let exts = Capabilities.extensions
        Assert.NotEmpty(exts)
        Assert.Equal(7, exts.Length)
        Assert.Contains("observe", exts)
        Assert.Contains("cursor", exts)
        Assert.Contains("catch-up", exts)
        Assert.Contains("idempotent-send", exts)
        Assert.Contains("attachments", exts)
        Assert.Contains("config-write", exts)
        Assert.Contains("snapshot", exts)

    [<Fact>]
    let ``Capabilities extensions elements are distinct and non-empty`` () =
        let exts = Capabilities.extensions
        let set = Set.ofList exts
        Assert.Equal(exts.Length, set.Count)
        for ext in exts do
            Assert.False(System.String.IsNullOrWhiteSpace ext)
            Assert.False(ext.StartsWith Capabilities.Namespace)

    // =========================================================================
    // 2. TolerantReader 契约与全分支覆盖测试
    // =========================================================================

    [<Fact>]
    let ``TolerantReader parses standard known AG-UI events`` () =
        let json = """{"type":"RUN_STARTED","threadId":"t1","runId":"r1"}"""
        match TolerantReader.parse json with
        | ParsedEvent.Known ev ->
            Assert.Equal("RUN_STARTED", ev.Type)
            Assert.IsAssignableFrom<BaseEvent>(ev)
        | other -> failwithf "预期 Known，实际得到 %A" other

    [<Fact>]
    let ``TolerantReader handles unrecognized event type gracefully without exception`` () =
        let json = """{"type":"SOME_FUTURE_UNKNOWN_TYPE","foo":"bar"}"""
        match TolerantReader.parse json with
        | ParsedEvent.Unrecognized name ->
            Assert.Equal("SOME_FUTURE_UNKNOWN_TYPE", name)
        | other -> failwithf "预期 Unrecognized，实际得到 %A" other

    [<Fact>]
    let ``TolerantReader parses CUSTOM event with name and value`` () =
        let json = """{"type":"CUSTOM","name":"wanxiang.dev/cursor","value":{"commitId":"42"}}"""
        match TolerantReader.parse json with
        | ParsedEvent.Custom(name, value) ->
            Assert.Equal("wanxiang.dev/cursor", name)
            Assert.NotNull(value)
            Assert.Equal("42", value.["commitId"].GetValue<string>())
        | other -> failwithf "预期 Custom，实际得到 %A" other

    [<Fact>]
    let ``TolerantReader parses CUSTOM event without value defaults to null JsonNode`` () =
        // 覆盖 TolerantReader.fs 中 value 为 None 时的 `JsonNode.Parse "null"` 分支
        let json = """{"type":"CUSTOM","name":"wanxiang.dev/heartbeat"}"""
        match TolerantReader.parse json with
        | ParsedEvent.Custom(name, value) ->
            Assert.Equal("wanxiang.dev/heartbeat", name)
            // value 是一个 JSON null 节点，其 JsonValueKind 应为 Null
            Assert.Null(value)
        | other -> failwithf "预期 Custom，实际得到 %A" other

    [<Fact>]
    let ``TolerantReader parses CUSTOM event with explicit null value`` () =
        let json = """{"type":"CUSTOM","name":"wanxiang.dev/signal","value":null}"""
        match TolerantReader.parse json with
        | ParsedEvent.Custom(name, value) ->
            Assert.Equal("wanxiang.dev/signal", name)
            Assert.Null(value)
        | other -> failwithf "预期 Custom，实际得到 %A" other

    [<Fact>]
    let ``TolerantReader rejects CUSTOM event without name`` () =
        let json = """{"type":"CUSTOM","value":{"data":1}}"""
        match TolerantReader.parse json with
        | ParsedEvent.Malformed reason ->
            Assert.Contains("CUSTOM event without a string 'name'", reason)
        | other -> failwithf "预期 Malformed，实际得到 %A" other

    [<Fact>]
    let ``TolerantReader rejects CUSTOM event with non-string name`` () =
        // 覆盖 name 字段不是 string 的分支
        let json = """{"type":"CUSTOM","name":12345,"value":{}}"""
        match TolerantReader.parse json with
        | ParsedEvent.Malformed reason ->
            Assert.Contains("CUSTOM event without a string 'name'", reason)
        | other -> failwithf "预期 Malformed，实际得到 %A" other

    [<Fact>]
    let ``TolerantReader rejects CUSTOM event with null name`` () =
        let json = """{"type":"CUSTOM","name":null,"value":{}}"""
        match TolerantReader.parse json with
        | ParsedEvent.Malformed reason ->
            Assert.Contains("CUSTOM event without a string 'name'", reason)
        | other -> failwithf "预期 Malformed，实际得到 %A" other

    [<Fact>]
    let ``TolerantReader rejects non-object root JSON`` () =
        // 覆盖 match JsonNode.Parse json with :? JsonObject 之外的模式
        let cases = [ "[1, 2, 3]"; "\"a bare string\""; "12345"; "true"; "false"; "null" ]
        for case in cases do
            match TolerantReader.parse case with
            | ParsedEvent.Malformed reason ->
                Assert.Contains("event is not a JSON object", reason)
            | other -> failwithf "用例 %s 预期 Malformed，实际得到 %A" case other

    [<Fact>]
    let ``TolerantReader rejects completely invalid JSON`` () =
        // 覆盖外层 try ... with e -> Malformed("invalid JSON...") 分支
        let cases = [ "{ broken json"; "not json"; ""; "   "; "{\"" ]
        for case in cases do
            match TolerantReader.parse case with
            | ParsedEvent.Malformed reason ->
                Assert.Contains("invalid JSON", reason)
            | other -> failwithf "用例 %s 预期 Malformed，实际得到 %A" case other

    [<Fact>]
    let ``TolerantReader rejects object missing type property`` () =
        // 覆盖 tryProp o "type" 为 None 的分支
        let json = """{"threadId":"t1","foo":"bar"}"""
        match TolerantReader.parse json with
        | ParsedEvent.Malformed reason ->
            Assert.Contains("missing or non-string 'type'", reason)
        | other -> failwithf "预期 Malformed，实际得到 %A" other

    [<Fact>]
    let ``TolerantReader rejects object with non-string type property`` () =
        // 覆盖 type 属性为数字、布尔、数组或对象的分支
        let cases = [
            """{"type": 123}"""
            """{"type": true}"""
            """{"type": ["RUN_STARTED"]}"""
            """{"type": {"name":"RUN_STARTED"}}"""
            """{"type": null}"""
        ]
        for case in cases do
            match TolerantReader.parse case with
            | ParsedEvent.Malformed reason ->
                Assert.Contains("missing or non-string 'type'", reason)
            | other -> failwithf "用例 %s 预期 Malformed，实际得到 %A" case other

    [<Fact>]
    let ``TolerantReader catches deserialization failure for known type with invalid payload`` () =
        // 覆盖 knownTypes 分支中的 try ... with e -> Malformed(...) 异常捕获分支
        // 官方 SDK 对已知类型（如 RUN_STARTED）在 threadId 期望为 string 时收到数字/嵌套对象会抛异常
        let json = """{"type":"RUN_STARTED","threadId":{"invalid":"object"}}"""
        match TolerantReader.parse json with
        | ParsedEvent.Malformed reason ->
            Assert.Contains("known type 'RUN_STARTED' failed to deserialize", reason)
        | other -> failwithf "预期 Malformed，实际得到 %A" other

    [<Fact>]
    let ``TolerantReader recognizes known type with valid optional payload`` () =
        let json = """{"type":"RUN_STARTED","threadId":"thread-99","runId":"run-99"}"""
        match TolerantReader.parse json with
        | ParsedEvent.Known ev ->
            Assert.Equal("RUN_STARTED", ev.Type)
        | other -> failwithf "预期 Known，实际得到 %A" other

    [<Fact>]
    let ``ParsedEvent discrimination union variants can be matched`` () =
        // 确保 DU 的各标签能被正常匹配
        let dummyJson = JsonObject()
        let customVal = ParsedEvent.Custom("wanxiang.dev/test", dummyJson)
        let unrecVal = ParsedEvent.Unrecognized "UNKNOWN"
        let malVal = ParsedEvent.Malformed "bad"

        match customVal with
        | ParsedEvent.Custom(n, _) -> Assert.Equal("wanxiang.dev/test", n)
        | _ -> failwith "DU 匹配错误"

        match unrecVal with
        | ParsedEvent.Unrecognized n -> Assert.Equal("UNKNOWN", n)
        | _ -> failwith "DU 匹配错误"

        match malVal with
        | ParsedEvent.Malformed r -> Assert.Equal("bad", r)
        | _ -> failwith "DU 匹配错误"
