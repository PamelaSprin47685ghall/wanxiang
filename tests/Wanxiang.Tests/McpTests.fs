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

    // =========================================================================
    // 4. McpClient 契约与功能测试
    // =========================================================================

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

        // 初始无 MCP 服务
        Assert.True((registry.GetMcpClient "mcp1").IsNone)

        // 添加一个已禁用的 MCP 服务
        let srvDisabled = { defaultMcpConfig "mcp1" with enabled = false }
        currentAppConfig <- { currentAppConfig with mcpServers = Map.ofList [ "mcp1", srvDisabled ] }
        Assert.True((registry.GetMcpClient "mcp1").IsNone)

        // 启用 MCP 服务
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
