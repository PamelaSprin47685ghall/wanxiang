namespace Wanxiang.Tests

open Xunit
open Wanxiang.Agui

/// AG-UI 语义层的宽容读取契约。
///
/// 核心是那条**与官方 SDK 相矛盾**的规范要求：未识别的 `type` 必须可被安全忽略。
/// 官方 .NET SDK 对未知类型抛异常，所以这里把「不抛」钉成回归护栏。
module AguiMappingTests =

    [<Fact>]
    let ``a run started event is recognized`` () =
        let json =
            """{"type":"RUN_STARTED","threadId":"t1","runId":"r1"}"""
        match TolerantReader.parse json with
        | Known ev -> Assert.Equal("RUN_STARTED", ev.Type)
        | other -> failwithf "expected Known, got %A" other

    [<Fact>]
    let ``an unknown event type is tolerated rather than thrown on`` () =
        // 对端升级、多发了一种我们还不认识的事件——规范要求忽略并继续。
        let json = """{"type":"SOME_FUTURE_EVENT","payload":{"x":1}}"""
        match TolerantReader.parse json with
        | Unrecognized name -> Assert.Equal("SOME_FUTURE_EVENT", name)
        | other -> failwithf "expected Unrecognized, got %A" other

    [<Fact>]
    let ``a custom event exposes its name and value`` () =
        let json = """{"type":"CUSTOM","name":"wanxiang.dev/cursor","value":{"commitId":"7"}}"""
        match TolerantReader.parse json with
        | Custom(name, value) ->
            Assert.Equal("wanxiang.dev/cursor", name)
            Assert.Equal("7", value.["commitId"].GetValue<string>())
        | other -> failwithf "expected Custom, got %A" other

    [<Fact>]
    let ``a custom event without a name is malformed`` () =
        match TolerantReader.parse """{"type":"CUSTOM","value":{}}""" with
        | Malformed _ -> ()
        | other -> failwithf "expected Malformed, got %A" other

    [<Fact>]
    let ``a known type with a broken payload is malformed not unrecognized`` () =
        // 字段类型不匹配（数字出现在应为字符串的位置）：这是真的坏了。
        // 注意：**字段缺失**不算坏——官方 SDK 对缺失字段不做存在性校验
        // （实测 {"type":"RUN_STARTED"} 会反序列化成字段为 null 的事件），
        // 所以不能用缺字段当畸形样本。
        match TolerantReader.parse """{"type":"RUN_STARTED","threadId":123}""" with
        | Malformed _ -> ()
        | Unrecognized _ -> failwith "已知类型的畸形载荷必须判为 Malformed，不得混同未知类型"
        | Known _ -> failwith "载荷不完整不应被当作合法事件"
        | Custom _ -> failwith "不应被识别为 CUSTOM"

    [<Fact>]
    let ``a known type with merely absent optional fields is still known`` () =
        // SDK 不校验字段存在性：缺 threadId/runId 仍反序列化成 RunStartedEvent。
        // 把这个行为钉住，免得日后有人以为缺字段会被拒收。
        match TolerantReader.parse """{"type":"RUN_STARTED"}""" with
        | Known ev -> Assert.Equal("RUN_STARTED", ev.Type)
        | other -> failwithf "expected Known, got %A" other

    [<Fact>]
    let ``invalid json is malformed`` () =
        match TolerantReader.parse "not json at all" with
        | Malformed _ -> ()
        | other -> failwithf "expected Malformed, got %A" other

    [<Fact>]
    let ``a non object payload is malformed`` () =
        match TolerantReader.parse "[1,2,3]" with
        | Malformed _ -> ()
        | other -> failwithf "expected Malformed, got %A" other

    [<Fact>]
    let ``a missing type field is malformed`` () =
        match TolerantReader.parse """{"threadId":"t"}""" with
        | Malformed _ -> ()
        | other -> failwithf "expected Malformed, got %A" other

    [<Fact>]
    let ``the declared protocol version and namespace are stable`` () =
        Assert.Equal("1.0", Capabilities.ProtocolVersion)
        Assert.Equal("wanxiang.dev/", Capabilities.Namespace)
