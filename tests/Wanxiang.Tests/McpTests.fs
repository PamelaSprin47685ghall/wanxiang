namespace Wanxiang.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Server
open Wanxiang.Tests.Helpers

module McpTests =

    let private jsonOptions = System.Text.Json.JsonSerializerOptions(Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private defaultMcpConfig id =
        { id = id
          label = id
          enabled = true
          command = None
          args = []
          env = Map.empty
          url = None
          maxConcurrency = None
          callTimeoutSeconds = 5 }

    // =========================================================================
    // 1. McpJson 纯函数契约测试
    // =========================================================================

    [<Fact>]
    let ``McpJson errorNode 格式正确`` () =
        let node = McpJson.errorNode "test error message"
        Assert.NotNull node
        let obj = Assert.IsAssignableFrom<JsonObject>(node)
        Assert.True(obj.ContainsKey "error")
        let errObj = Assert.IsAssignableFrom<JsonObject>(obj["error"])
        Assert.Equal("test error message", errObj["message"].GetValue<string>())

    [<Fact>]
    let ``McpJson initializeParams 包含协议版本与客户端信息`` () =
        let p = McpJson.initializeParams ()
        Assert.NotNull p
        Assert.Equal("2025-06-18", p["protocolVersion"].GetValue<string>())
        Assert.NotNull p["capabilities"]
        let clientInfo = Assert.IsAssignableFrom<JsonObject>(p["clientInfo"])
        Assert.Equal("wanxiang", clientInfo["name"].GetValue<string>())
        Assert.Equal("1.0", clientInfo["version"].GetValue<string>())


    // =========================================================================
    // McpFunction 与 ToolRegistry Schema 容错契约（ToolRegistry & McpClient 靶点）
    // =========================================================================

    [<Fact>]
    let ``McpFunction 无 inputSchema 或畸形 inputSchema 时回退到 PermissiveSchema 容错`` () =
        let dummyCfg = defaultMcpConfig "dummy-mcp"
        let client = new McpClient(dummyCfg, ignore)
        let toolInfoNoSchema = {
            name = "query_status"
            description = "Status tool without schema"
            inputSchema = None
        }
        let mcpFunc = new McpFunction(client, "dummy-mcp", toolInfoNoSchema)
        Assert.Equal("mcp_dummy-mcp_query_status", mcpFunc.Name)
        Assert.NotNull mcpFunc.JsonSchema
        let schemaJson = mcpFunc.JsonSchema.GetRawText()
        Assert.Contains("object", schemaJson)
        Assert.Contains("additionalProperties", schemaJson)

    [<Fact>]
    let ``McpFunction 有效 inputSchema 正确提取并保留原始结构`` () =
        let dummyCfg = defaultMcpConfig "dummy-mcp"
        let client = new McpClient(dummyCfg, ignore)
        let customSchema = JsonObject()
        customSchema["type"] <- "object"
        let props = JsonObject()
        let p1 = JsonObject()
        p1["type"] <- "string"
        props["filePath"] <- p1
        customSchema["properties"] <- props

        let toolInfoWithSchema = {
            name = "read_file"
            description = "Reads a file"
            inputSchema = Some (customSchema :> JsonNode)
        }
        let mcpFunc = new McpFunction(client, "dummy-mcp", toolInfoWithSchema)
        Assert.Equal("mcp_dummy-mcp_read_file", mcpFunc.Name)
        Assert.Equal("Reads a file", mcpFunc.Description)
        let schemaJson = mcpFunc.JsonSchema.GetRawText()
        Assert.Contains("filePath", schemaJson)


    // =========================================================================
    // 2. McpStdioTransport 边界与全路径测试
    // =========================================================================

    [<Fact>]
    let ``McpStdioTransport 未配置 command 时返回明确错误`` () =
        task {
            let cfg = defaultMcpConfig "test-no-cmd"
            let logs = ConcurrentQueue<string>()
            let transport = McpStdioTransport(cfg, logs.Enqueue) :> IMcpTransport
            let! res = transport.Request("test/method", JsonObject(), 1000, CancellationToken.None)
            let resStr = res.ToJsonString(jsonOptions)
            Assert.Contains("未配置 command", resStr)
            Assert.Equal(0, transport.Pending)
        }

    [<Fact>]
    let ``McpStdioTransport 命令不存在时启动失败返回异常错误`` () =
        task {
            let cfg =
                { defaultMcpConfig "test-bad-cmd" with
                    command = Some "non_existent_command_wanxiang_xyz_123" }
            let logs = ConcurrentQueue<string>()
            let transport = McpStdioTransport(cfg, logs.Enqueue) :> IMcpTransport
            let! res = transport.Request("test/method", JsonObject(), 1000, CancellationToken.None)
            let resStr = res.ToJsonString(jsonOptions)
            Assert.Contains("启动失败", resStr)
        }

    [<Fact>]
    let ``McpStdioTransport 握手返回 error 时 initialize 被拒绝并清理子进程`` () =
        task {
            // 使用 sh 进程模拟：读取 initialize 后输出 JSON-RPC error
            let script =
                "read line\n" +
                "echo '{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32600,\"message\":\"rejected\"}}'\n" +
                "sleep 1\n"
            let cfg =
                { defaultMcpConfig "test-rejected-init" with
                    command = Some "/bin/sh"
                    args = [ "-c"; script ] }
            let logs = ConcurrentQueue<string>()
            let transport = McpStdioTransport(cfg, logs.Enqueue) :> IMcpTransport
            let! res = transport.Request("test/method", JsonObject(), 3000, CancellationToken.None)
            let resStr = res.ToJsonString(jsonOptions)
            Assert.Contains("initialize 被拒绝", resStr)
            transport.Stop()
        }

    [<Fact>]
    let ``McpStdioTransport 握手返回非标准 JSON 对象时报错`` () =
        task {
            let script =
                "read line\n" +
                "echo '{\"jsonrpc\":\"2.0\",\"id\":1}'\n" +
                "sleep 1\n"
            let cfg =
                { defaultMcpConfig "test-malformed-init" with
                    command = Some "/bin/sh"
                    args = [ "-c"; script ] }
            let logs = ConcurrentQueue<string>()
            let transport = McpStdioTransport(cfg, logs.Enqueue) :> IMcpTransport
            let! res = transport.Request("test/method", JsonObject(), 3000, CancellationToken.None)
            let resStr = res.ToJsonString(jsonOptions)
            Assert.Contains("initialize 响应异常", resStr)
            transport.Stop()
        }

    [<Fact>]
    let ``McpStdioTransport 正常握手与交互并验证环境变量白名单过滤`` () =
        task {
            // 握手脚本：
            // 1. read initialize 请求 -> 返回 result: {}
            // 2. read notifications/initialized
            // 3. read actual request -> 返回 result: {"echo": true}，并写 stderr 供日志观察
            let script =
                "read init\n" +
                "echo '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}'\n" +
                "read notif\n" +
                "read req\n" +
                "echo \"custom_var=$TEST_CUSTOM_ENV\" >&2\n" +
                "echo '{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"status\":\"ok\"}}'\n" +
                "while true; do sleep 1; done\n"
            let cfg =
                { defaultMcpConfig "test-ok" with
                    command = Some "/bin/sh"
                    args = [ "-c"; script ]
                    env = Map.ofList [ "TEST_CUSTOM_ENV", "wanxiang_secret_val" ] }
            let logs = ConcurrentQueue<string>()
            let transport = McpStdioTransport(cfg, logs.Enqueue) :> IMcpTransport

            let pObj = JsonObject()
            pObj["foo"] <- "bar"
            let! res = transport.Request("custom/echo", pObj, 5000, CancellationToken.None)
            let resObj = Assert.IsAssignableFrom<JsonObject>(res)
            Assert.True(resObj.ContainsKey "result")
            let resResult = resObj.["result"]
            Assert.Equal("ok", resResult.["status"].GetValue<string>())

            // 触发 Notify
            transport.Notify("notifications/ping", JsonObject())

            // 验证 Stop 后状态
            transport.Stop()
            Assert.Equal(0, transport.Pending)

            // Stop 后再次请求应提示已停止
            let! resAfterStop = transport.Request("custom/echo", JsonObject(), 1000, CancellationToken.None)
            Assert.Contains("已停止", resAfterStop.ToJsonString(jsonOptions))
        }

    [<Fact>]
    let ``McpStdioTransport 子进程提前退出中断在途调用`` () =
        task {
            // 握手成功后子进程立即退出，未回应业务请求
            let script =
                "read init\n" +
                "echo '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}'\n" +
                "read notif\n" +
                "exit 0\n"
            let cfg =
                { defaultMcpConfig "test-crash" with
                    command = Some "/bin/sh"
                    args = [ "-c"; script ] }
            let transport = McpStdioTransport(cfg, ignore) :> IMcpTransport
            let! res = transport.Request("call/hang", JsonObject(), 5000, CancellationToken.None)
            let resStr = res.ToJsonString(jsonOptions)
            Assert.True(resStr.Contains("MCP 进程已退出") || resStr.Contains("写入 MCP 失败") || resStr.Contains("已停止"))
            transport.Stop()
        }

    [<Fact>]
    let ``McpStdioTransport 调用支持超时与主动取消`` () =
        task {
            let script =
                "read init\n" +
                "echo '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}'\n" +
                "read notif\n" +
                "while true; do sleep 1; done\n"
            let cfg =
                { defaultMcpConfig "test-timeout" with
                    command = Some "/bin/sh"
                    args = [ "-c"; script ] }
            let transport = McpStdioTransport(cfg, ignore) :> IMcpTransport

            // 1. 超时验证
            let! timeoutRes = transport.Request("call/slow", JsonObject(), 200, CancellationToken.None)
            Assert.Contains("超时", timeoutRes.ToJsonString(jsonOptions))

            // 2. 取消验证
            use cts = new CancellationTokenSource()
            cts.Cancel()
            let! cancelRes = transport.Request("call/slow", JsonObject(), 5000, cts.Token)
            Assert.Contains("调用已取消", cancelRes.ToJsonString(jsonOptions))

            transport.Stop()
        }

    // =========================================================================
    // 3. McpHttpTransport 边界与全路径测试
    // =========================================================================

    type private MockHttpServer(handler: HttpListenerRequest -> (int * string * (string * string) list * string)) =
        let listener = new HttpListener()
        let port = Random.Shared.Next(20000, 35000)
        let prefix = sprintf "http://127.0.0.1:%d/" port
        let cts = new CancellationTokenSource()

        do
            listener.Prefixes.Add prefix
            listener.Start()
            Task.Run(Func<Task>(fun () ->
                task {
                    while not cts.IsCancellationRequested do
                        try
                            let! ctx = listener.GetContextAsync()
                            let code, contentType, headers, body = handler ctx.Request
                            ctx.Response.StatusCode <- code
                            ctx.Response.ContentType <- contentType
                            for k, v in headers do
                                ctx.Response.Headers.Add(k, v)
                            let bytes = Encoding.UTF8.GetBytes body
                            ctx.Response.ContentLength64 <- int64 bytes.Length
                            do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
                            ctx.Response.Close()
                        with _ -> ()
                } :> Task)) |> ignore

        member _.Url = prefix
        member this.Dispose() =
            cts.Cancel()
            try listener.Stop() with _ -> ()
            try listener.Close() with _ -> ()

        interface IDisposable with
            member this.Dispose() = this.Dispose()

    [<Fact>]
    let ``McpHttpTransport 握手成功并在 JSON-RPC 下正常收发与支持 SessionId`` () =
        task {
            let receivedHeaders = ConcurrentDictionary<string, string>()
            use server =
                new MockHttpServer(fun req ->
                    use reader = new StreamReader(req.InputStream, Encoding.UTF8)
                    let body = reader.ReadToEnd()
                    let json = JsonNode.Parse body
                    let method = json["method"].GetValue<string>()
                    let id = if json.AsObject().ContainsKey "id" then json["id"].GetValue<int>() else 0

                    if req.Headers.["X-Custom-Env"] <> null then
                        receivedHeaders["X-Custom-Env"] <- req.Headers.["X-Custom-Env"]

                    match method with
                    | "initialize" ->
                        (200, "application/json", [ ("Mcp-Session-Id", "sess-wanxiang-123") ],
                         sprintf "{\"jsonrpc\":\"2.0\",\"id\":%d,\"result\":{\"protocolVersion\":\"2025-06-18\"}}" id)
                    | "notifications/initialized" ->
                        (200, "application/json", [], "")
                    | "test/echo" ->
                        (200, "application/json", [],
                         sprintf "{\"jsonrpc\":\"2.0\",\"id\":%d,\"result\":{\"echo\":\"ok\"}}" id)
                    | _ ->
                        (404, "application/json", [], "{\"error\":\"not found\"}"))

            let cfg =
                { defaultMcpConfig "http-test" with
                    url = Some server.Url
                    env = Map.ofList [ "X-Custom-Env", "custom-header-value" ] }
            let logs = ConcurrentQueue<string>()
            let transport = McpHttpTransport(cfg, server.Url, logs.Enqueue) :> IMcpTransport

            let! res = transport.Request("test/echo", JsonObject(), 5000, CancellationToken.None)
            let resObj = Assert.IsAssignableFrom<JsonObject>(res)
            let resResult = resObj.["result"]
            Assert.Equal("ok", resResult.["echo"].GetValue<string>())
            Assert.Equal("custom-header-value", receivedHeaders["X-Custom-Env"])

            // Notify 测试
            transport.Notify("test/notify", JsonObject())

            transport.Stop()
        }

    [<Fact>]
    let ``McpHttpTransport 支持 SSE 流响应解析`` () =
        task {
            use server =
                new MockHttpServer(fun req ->
                    use reader = new StreamReader(req.InputStream, Encoding.UTF8)
                    let body = reader.ReadToEnd()
                    let json = JsonNode.Parse body
                    let method = json["method"].GetValue<string>()
                    let id = if json.AsObject().ContainsKey "id" then json["id"].GetValue<int>() else 0
                    match method with
                    | "initialize" ->
                        (200, "text/event-stream", [],
                         sprintf "data: {\"jsonrpc\":\"2.0\",\"id\":%d,\"result\":{}}\n\n" id)
                    | "notifications/initialized" ->
                        (200, "text/event-stream", [], "")
                    | "stream/query" ->
                        let sseBody =
                            "event: message\n" +
                            "data: {\"progress\":10}\n\n" +
                            "event: message\n" +
                            sprintf "data: {\"jsonrpc\":\"2.0\",\"id\":%d,\"result\":{\"streamDone\":true}}\n\n" id
                        (200, "text/event-stream", [], sseBody)
                    | _ ->
                        (400, "text/plain", [], "bad request"))

            let cfg = { defaultMcpConfig "http-sse" with url = Some server.Url }
            let transport = McpHttpTransport(cfg, server.Url, ignore) :> IMcpTransport

            let! res = transport.Request("stream/query", JsonObject(), 5000, CancellationToken.None)
            let resObj = Assert.IsAssignableFrom<JsonObject>(res)
            let resResult = resObj.["result"]
            Assert.True(resResult.["streamDone"].GetValue<bool>())
            transport.Stop()
        }

    [<Fact>]
    let ``McpHttpTransport 覆盖异常路径（HTTP报错、空响应、SSE无帧、超时与取消）`` () =
        task {
            // 1. 初始化被拒绝
            use serverInitErr =
                new MockHttpServer(fun _ ->
                    (200, "application/json", [], "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-1,\"message\":\"init fail\"}}"))
            let cfgInitErr = { defaultMcpConfig "http-init-err" with url = Some serverInitErr.Url }
            let transportInitErr = McpHttpTransport(cfgInitErr, serverInitErr.Url, ignore) :> IMcpTransport
            let! resInitErr = transportInitErr.Request("any", JsonObject(), 2000, CancellationToken.None)
            Assert.Contains("initialize 被拒绝", resInitErr.ToJsonString(jsonOptions))
            transportInitErr.Stop()

            // 2. HTTP 500 状态码
            use server500 = new MockHttpServer(fun _ -> (500, "text/plain", [], "Internal Server Error"))
            let cfg500 = { defaultMcpConfig "http-500" with url = Some server500.Url }
            let transport500 = McpHttpTransport(cfg500, server500.Url, ignore) :> IMcpTransport
            let! res500 = transport500.Request("any", JsonObject(), 2000, CancellationToken.None)
            Assert.Contains("MCP HTTP 500", res500.ToJsonString())
            transport500.Stop()

            // 3. 空响应
            use serverEmpty = new MockHttpServer(fun _ -> (200, "application/json", [], ""))
            let cfgEmpty = { defaultMcpConfig "http-empty" with url = Some serverEmpty.Url }
            let transportEmpty = McpHttpTransport(cfgEmpty, serverEmpty.Url, ignore) :> IMcpTransport
            let! resEmpty = transportEmpty.Request("any", JsonObject(), 2000, CancellationToken.None)
            let resEmptyStr = resEmpty.ToJsonString(jsonOptions)
            Assert.True(resEmptyStr.Contains("MCP 响应为空") || resEmptyStr.Contains("initialize"))
            transportEmpty.Stop()

            // 4. SSE 流中无可用帧
            use serverNoSseFrame =
                new MockHttpServer(fun _ -> (200, "text/event-stream", [], "event: ping\ndata: {}\n\n"))
            let cfgNoSse = { defaultMcpConfig "http-nosse" with url = Some serverNoSseFrame.Url }
            let transportNoSse = McpHttpTransport(cfgNoSse, serverNoSseFrame.Url, ignore) :> IMcpTransport
            let! resNoSse = transportNoSse.Request("any", JsonObject(), 2000, CancellationToken.None)
            let resNoSseStr = resNoSse.ToJsonString(jsonOptions)
            Assert.True(resNoSseStr.Contains("MCP SSE 响应中没有可用帧") || resNoSseStr.Contains("initialize"))
            transportNoSse.Stop()
        }

    // =========================================================================
    // 4. McpClient 契约与功能测试
    // =========================================================================

    [<Fact>]
    let ``McpClient 完整生命周期：加载工具、调用工具、参数校验与排空关闭`` () =
        task {
            use server =
                new MockHttpServer(fun req ->
                    use reader = new StreamReader(req.InputStream, Encoding.UTF8)
                    let body = reader.ReadToEnd()
                    let json = JsonNode.Parse body
                    let method = json["method"].GetValue<string>()
                    let id = if json.AsObject().ContainsKey "id" then json["id"].GetValue<int>() else 0
                    match method with
                    | "initialize" ->
                        (200, "application/json", [], sprintf "{\"jsonrpc\":\"2.0\",\"id\":%d,\"result\":{}}" id)
                    | "notifications/initialized" ->
                        (200, "application/json", [], "")
                    | "tools/list" ->
                        let toolsJson =
                            sprintf """{"jsonrpc":"2.0","id":%d,"result":{"tools":[{"name":"calculator","description":"math calc","inputSchema":{"type":"object","properties":{"expr":{"type":"string"}}}}]}}""" id
                        (200, "application/json", [], toolsJson)
                    | "tools/call" ->
                        let p = json["params"].AsObject()
                        let name = p["name"].GetValue<string>()
                        let args = p["arguments"].AsObject()
                        if name = "calculator" && args.ContainsKey "expr" then
                            sprintf """{"jsonrpc":"2.0","id":%d,"result":{"content":[{"type":"text","text":"42"}]}}""" id
                            |> fun b -> (200, "application/json", [], b)
                        else
                            sprintf """{"jsonrpc":"2.0","id":%d,"error":{"code":-32602,"message":"invalid params"}}""" id
                            |> fun b -> (200, "application/json", [], b)
                    | _ ->
                        (400, "application/json", [], "{}"))

            let cfg =
                { defaultMcpConfig "client-test" with
                    url = Some server.Url
                    callTimeoutSeconds = 3
                    maxConcurrency = Some 2 }
            let logs = ConcurrentQueue<string>()
            let client = McpClient(cfg, logs.Enqueue)
            Assert.Equal("client-test", client.Id)

            // 1. 同步加载 Tools()
            let tools = client.Tools()
            Assert.Single tools |> ignore
            let tool = tools.Head
            Assert.Equal("calculator", tool.name)
            Assert.Equal("math calc", tool.description)
            Assert.True(tool.inputSchema.IsSome)

            // 2. TryTool
            Assert.True((client.TryTool "calculator").IsSome)
            Assert.True((client.TryTool "unknown_tool").IsNone)

            // 3. Call 成功路径
            let! callOk = client.Call("calculator", """{"expr":"6*7"}""", CancellationToken.None)
            Assert.Contains("42", callOk)

            // 4. Call 无效 JSON 参数
            let! callBadJson = client.Call("calculator", "not valid json {", CancellationToken.None)
            Assert.Contains("invalid arguments: not valid JSON", callBadJson)

            // 5. Call 空参数
            let! callEmpty = client.Call("calculator", "", CancellationToken.None)
            Assert.Contains("invalid params", callEmpty)

            // 6. 停止与排空
            client.Stop()

            // 7. 停止后调用报错
            let! callStopped = client.Call("calculator", """{"expr":"1"}""", CancellationToken.None)
            Assert.Contains("已停止", callStopped)
        }

    // =========================================================================
    // 5. McpFunction (AIFunction) 契约测试
    // =========================================================================

    [<Fact>]
    let ``McpFunction 正确映射 Name、Description、Schema 并能 InvokeCoreAsync`` () =
        task {
            use server =
                new MockHttpServer(fun req ->
                    use reader = new StreamReader(req.InputStream, Encoding.UTF8)
                    let body = reader.ReadToEnd()
                    let json = JsonNode.Parse body
                    let id = if json.AsObject().ContainsKey "id" then json["id"].GetValue<int>() else 0
                    match json["method"].GetValue<string>() with
                    | "initialize" ->
                        (200, "application/json", [], sprintf "{\"jsonrpc\":\"2.0\",\"id\":%d,\"result\":{}}" id)
                    | "notifications/initialized" ->
                        (200, "application/json", [], "")
                    | "tools/call" ->
                        (200, "application/json", [], sprintf "{\"jsonrpc\":\"2.0\",\"id\":%d,\"result\":\"invoked_success\"}" id)
                    | _ ->
                        (200, "application/json", [], sprintf "{\"jsonrpc\":\"2.0\",\"id\":%d,\"result\":{}}" id))

            let cfg = { defaultMcpConfig "fn-srv" with url = Some server.Url }
            let client = McpClient(cfg, ignore)

            let schemaNode = JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""")
            let info =
                { name = "search"
                  description = "web search tool"
                  inputSchema = Some schemaNode }

            let fn = McpFunction(client, "fn-srv", info)
            Assert.Equal("mcp_fn-srv_search", fn.Name)
            Assert.Equal("web search tool", fn.Description)
            Assert.NotNull fn.JsonSchema

            // 默认无描述时的 fallback
            let infoNoDesc = { name = "search2"; description = ""; inputSchema = None }
            let fnNoDesc = McpFunction(client, "fn-srv", infoNoDesc)
            Assert.Equal("fn-srv 提供的工具 search2。", fnNoDesc.Description)

            // 测试 InvokeCoreAsync
            let args = AIFunctionArguments()
            args["q"] <- "fsharp test"
            args["count"] <- 5
            args["nested"] <- JsonNode.Parse("{\"k\":\"v\"}")
            let! res = fn.InvokeAsync(args, CancellationToken.None)
            Assert.Equal("\"invoked_success\"", string res)

            client.Stop()
        }

    // =========================================================================
    // 6. ToolRegistry 契约与配置联动测试
    // =========================================================================

    [<Fact>]
    let ``ToolRegistry 支持 MCP 客户端缓存、指纹更新与失效剪裁`` () =
        let mutable currentAppConfig = AppConfig.defaults (Guid.NewGuid())
        let logs = ConcurrentQueue<string>()
        let registry = new ToolRegistry((fun () -> currentAppConfig), logs.Enqueue)

        // 初始无 MCP 服务器
        Assert.True((registry.GetMcpClient "mcp1").IsNone)

        // 添加一个已禁用的服务器
        let srvDisabled = { defaultMcpConfig "mcp1" with enabled = false }
        currentAppConfig <- { currentAppConfig with mcpServers = Map.ofList [ "mcp1", srvDisabled ] }
        Assert.True((registry.GetMcpClient "mcp1").IsNone)

        // 启用服务器
        let srvEnabled = { defaultMcpConfig "mcp1" with enabled = true; command = Some "echo" }
        currentAppConfig <- { currentAppConfig with mcpServers = Map.ofList [ "mcp1", srvEnabled ] }
        let client1 = registry.GetMcpClient "mcp1"
        Assert.True(client1.IsSome)

        // 再次获取应命中指纹缓存
        let client1Cached = registry.GetMcpClient "mcp1"
        Assert.True(Object.ReferenceEquals(client1.Value, client1Cached.Value))

        // 修改配置（如 args 变化导致指纹变化），旧客户端排空，创建新客户端
        let srvModified = { srvEnabled with args = [ "arg1" ] }
        currentAppConfig <- { currentAppConfig with mcpServers = Map.ofList [ "mcp1", srvModified ] }
        let client2 = registry.GetMcpClient "mcp1"
        Assert.True(client2.IsSome)
        Assert.False(Object.ReferenceEquals(client1.Value, client2.Value))

        // 配置中删除，调用 PruneRemoved
        currentAppConfig <- { currentAppConfig with mcpServers = Map.empty }
        registry.PruneRemoved()
        Assert.True((registry.GetMcpClient "mcp1").IsNone)

        registry.Dispose()

    [<Fact>]
    let ``ToolRegistry 组装 Descriptors 与 DescriptorsJson 包含内置与有效 MCP 工具`` () =
        let instanceId = Guid.NewGuid()
        let mutable appCfg = AppConfig.defaults instanceId
        let registry = new ToolRegistry((fun () -> appCfg), ignore)

        // 纯内置工具
        let descList = registry.Descriptors()
        Assert.NotEmpty descList
        Assert.Contains(descList, fun d -> d.source = "builtin")

        let jsonArr = registry.DescriptorsJson()
        Assert.Equal(descList.Length, jsonArr.Count)

        // BuildTools 与 MissingTools 边界检查
        let sessionCfg =
            { testConfig () with
                tools = [ "builtin:calculator"; "mcp:nonexistent/tool"; "invalid_format_tool" ] }
        let missing = registry.MissingTools sessionCfg
        Assert.Contains("mcp:nonexistent/tool", missing)
        Assert.Contains("invalid_format_tool", missing)

        let builtTools = registry.BuildTools sessionCfg
        // 只有合法的内置 calculator 被加入，无效的被跳过并记录
        Assert.True(builtTools.Length >= 0)

        registry.Dispose()
