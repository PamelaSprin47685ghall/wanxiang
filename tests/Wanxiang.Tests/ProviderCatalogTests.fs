namespace Wanxiang.Tests

open System
open System.IO
open System.Net
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Wanxiang.Config
open Wanxiang.Server

module ProviderCatalogTests =

    let private getFreePort () =
        let l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
        l.Start()
        let port = (l.LocalEndpoint :?> System.Net.IPEndPoint).Port
        l.Stop()
        port

    [<Fact>]
    let ``modelIdsFromProbe extracts openai and anthropic data arrays with sorting and deduplication`` () =
        let json = """{"data": [{"id": "gpt-4o"}, {"id": "gpt-3.5-turbo"}, {"id": "gpt-4o"}]} """
        match ProviderCatalog.modelIdsFromProbe json with
        | Ok models -> Assert.Equal<string list>(["gpt-3.5-turbo"; "gpt-4o"], models)
        | Error e -> Assert.Fail(sprintf "Expected Ok, got: %s" e)

    [<Fact>]
    let ``modelIdsFromProbe extracts gemini models array stripping prefix`` () =
        let json = """{"models": [{"name": "models/gemini-1.5-pro"}, {"name": "models/gemini-1.5-flash"}, {"name": "models/gemini-1.5-flash"}]} """
        match ProviderCatalog.modelIdsFromProbe json with
        | Ok models -> Assert.Equal<string list>(["gemini-1.5-flash"; "gemini-1.5-pro"], models)
        | Error e -> Assert.Fail(sprintf "Expected Ok, got: %s" e)

    [<Fact>]
    let ``modelIdsFromProbe handles empty or invalid json gracefully`` () =
        Assert.True(ProviderCatalog.modelIdsFromProbe "" |> Result.isError)
        Assert.True(ProviderCatalog.modelIdsFromProbe "not a json at all" |> Result.isError)
        Assert.True(ProviderCatalog.modelIdsFromProbe "{}" |> Result.isError)
        Assert.True(ProviderCatalog.modelIdsFromProbe """{"data": "not an array"}""" |> Result.isError)
        Assert.True(ProviderCatalog.modelIdsFromProbe """{"models": "not an array"}""" |> Result.isError)

    [<Fact>]
    let ``probeModels returns models when endpoint responds 200 OK`` () =
        let listener = new HttpListener()
        let port = getFreePort ()
        let prefix = sprintf "http://127.0.0.1:%d/" port
        listener.Prefixes.Add prefix
        listener.Start()

        let serverTask = Task.Run(fun () ->
            let ctx = listener.GetContext()
            let resp = ctx.Response
            let json = """{"data": [{"id": "model-a"}, {"id": "model-b"}]}"""
            let bytes = Encoding.UTF8.GetBytes(json)
            resp.ContentType <- "application/json"
            resp.StatusCode <- 200
            resp.OutputStream.Write(bytes, 0, bytes.Length)
            resp.OutputStream.Close()
        )

        try
            let provider: ProviderConfig = {
                id = "test-prov"
                kind = "openai"
                label = "Test Provider"
                baseUrl = prefix
                apiKey = Some "test-key"
                models = ["model-a"]
                defaultModel = "model-a"
                timeoutSeconds = 30
                maxRetries = 0
                enabled = true
                promptCaching = false
                headers = Map.empty
                extraJson = None
            }
            let res = ProviderCatalog.probeModels provider CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
            match res with
            | Ok models -> Assert.Equal<string list>(["model-a"; "model-b"], models)
            | Error e -> Assert.Fail(sprintf "Expected Ok but got Error: %s" e)
            serverTask.Wait(2000) |> ignore
        finally
            listener.Stop()
            (listener :> IDisposable).Dispose()

    [<Fact>]
    let ``probeModels handles http error status gracefully`` () =
        let listener = new HttpListener()
        let port = getFreePort ()
        let prefix = sprintf "http://127.0.0.1:%d/" port
        listener.Prefixes.Add prefix
        listener.Start()

        let serverTask = Task.Run(fun () ->
            let ctx = listener.GetContext()
            let resp = ctx.Response
            resp.StatusCode <- 401
            let bytes = Encoding.UTF8.GetBytes("Unauthorized")
            resp.OutputStream.Write(bytes, 0, bytes.Length)
            resp.OutputStream.Close()
        )

        try
            let provider: ProviderConfig = {
                id = "test-unauth"
                kind = "openai"
                label = "Unauth Provider"
                baseUrl = prefix
                apiKey = None
                models = ["m1"]
                defaultModel = "m1"
                timeoutSeconds = 30
                maxRetries = 0
                enabled = true
                promptCaching = false
                headers = Map.empty
                extraJson = None
            }
            let res = ProviderCatalog.probeModels provider CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
            match res with
            | Ok _ -> Assert.Fail("Expected Error for 401 status")
            | Error msg -> Assert.Contains("401", msg)
            serverTask.Wait(2000) |> ignore
        finally
            listener.Stop()
            (listener :> IDisposable).Dispose()

    [<Fact>]
    let ``providers snapshot hides api keys and formats models correctly`` () =
        let p1: ProviderConfig =
            { id = "p1"; kind = "openai"; label = "P1"; baseUrl = "http://api.p1.com"; apiKey = Some "secret-123"
              models = ["m1"]; defaultModel = "m1"; timeoutSeconds = 60; maxRetries = 0; enabled = true; promptCaching = false; headers = Map.empty; extraJson = None }
        let p2: ProviderConfig =
            { id = "p2"; kind = "anthropic"; label = ""; baseUrl = "http://api.p2.com"; apiKey = None
              models = ["m2"]; defaultModel = "m2"; timeoutSeconds = 60; maxRetries = 0; enabled = false; promptCaching = false; headers = Map.empty; extraJson = None }

        let sampleAppConfig = { AppConfig.defaults (Guid.NewGuid()) with providers = Map.ofList [ ("p1", p1); ("p2", p2) ] }

        let arr = ProviderCatalog.providers sampleAppConfig
        Assert.Equal(2, arr.Count)

        let p1Json = arr.[0].AsObject()
        Assert.Equal("p1", p1Json["id"].ToString())
        Assert.Equal("P1", p1Json["label"].ToString())
        Assert.Equal("openai", p1Json["kind"].ToString())
        Assert.True(p1Json["hasApiKey"].GetValue<bool>())
        Assert.False(p1Json.ContainsKey("apiKey"))
        Assert.True(p1Json["enabled"].GetValue<bool>())

        let p2Json = arr.[1].AsObject()
        Assert.Equal("p2", p2Json["id"].ToString())
        Assert.Equal("p2", p2Json["label"].ToString()) // label 回落到 id
        Assert.False(p2Json["hasApiKey"].GetValue<bool>())
        Assert.False(p2Json["enabled"].GetValue<bool>())

    [<Fact>]
    let ``mcpServers snapshot formats command and url correctly`` () =
        let m1: McpServerConfig =
            { id = "m1"; label = "Local MCP"; command = Some "node"; args = ["app.js"]; env = Map.ofList [("K", "V")]; url = None; maxConcurrency = Some 2; callTimeoutSeconds = 30; enabled = true }
        let m2: McpServerConfig =
            { id = "m2"; label = ""; command = None; args = []; env = Map.empty; url = Some "http://remote.mcp"; maxConcurrency = None; callTimeoutSeconds = 60; enabled = false }

        let sampleAppConfig = { AppConfig.defaults (Guid.NewGuid()) with mcpServers = Map.ofList [ ("m1", m1); ("m2", m2) ] }

        let arr = ProviderCatalog.mcpServers sampleAppConfig
        Assert.Equal(2, arr.Count)

        let m1Json = arr.[0].AsObject()
        Assert.Equal("m1", m1Json["id"].ToString())
        Assert.Equal("Local MCP", m1Json["label"].ToString())
        Assert.Equal("node", m1Json["command"].ToString())
        let envObj = m1Json["env"].AsObject()
        Assert.Equal("V", envObj["K"].ToString())

        let m2Json = arr.[1].AsObject()
        Assert.Equal("m2", m2Json["id"].ToString())
        Assert.Equal("m2", m2Json["label"].ToString())
        Assert.Equal("http://remote.mcp", m2Json["url"].ToString())

    [<Fact>]
    let ``generation snapshot formats tools and defaults correctly`` () =
        let gen: GenerationDefaults =
            { temperature = Some 0.7
              topP = Some 0.9
              maxTokens = Some 2048
              instructions = Some "System inst"
              maxContextMessages = 100
              maxContextTokens = 32000
              autoTitle = true
              maxToolRounds = 8
              thinkingBudget = 500 }

        let appCfg = { AppConfig.defaults (Guid.NewGuid()) with generation = gen }

        let json = ProviderCatalog.generation appCfg
        Assert.Equal(0.7, json["temperature"].GetValue<double>())
        Assert.Equal(0.9, json["topP"].GetValue<double>())
        Assert.Equal(2048, json["maxTokens"].GetValue<int>())
        Assert.Equal("System inst", json["instructions"].ToString())
        Assert.True(json.ContainsKey "tools")
        Assert.True(json.ContainsKey "mcpServers")
