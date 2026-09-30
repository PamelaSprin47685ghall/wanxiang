namespace Wanxiang.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core
open Wanxiang.Config
open Wanxiang.Server

module ServerStoreMutationTests =

    let private computeSha256 (bytes: byte[]) =
        use sha = SHA256.Create()
        Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant()

    let private sampleAppConfig : AppConfig = AppConfig.defaults (Guid.NewGuid())

    // =========================================================================
    // 1. AttachmentStore 完整生命周期测试
    // =========================================================================

    [<Fact>]
    let ``AttachmentStore upload chunk complete and read workflow`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), sprintf "wanxiang-att-test-%s" (Guid.NewGuid().ToString("N")))
        try
            let store = new AttachmentStore(tempDir, 1024L * 1024L, 65536)
            let content = Encoding.UTF8.GetBytes("Hello, this is a test attachment file content.")
            let sha = computeSha256 content
            let uploadId = Guid.NewGuid()
            let connId = 1

            // 1. Begin
            match store.Begin(connId, uploadId, int64 content.Length, sha, "text/plain", "test.txt") with
            | Error e -> Assert.Fail(sprintf "Begin failed: %A" e)
            | Ok () -> ()

            // 2. AppendChunk (base64 string)
            let base64 = Convert.ToBase64String content
            match store.AppendChunk(uploadId, 0, base64) with
            | Error e -> Assert.Fail(sprintf "AppendChunk failed: %A" e)
            | Ok () -> ()

            // 3. Complete
            match store.Complete(uploadId, sha) with
            | Error e -> Assert.Fail(sprintf "Complete failed: %A" e)
            | Ok meta ->
                Assert.Equal(sha, meta.sha256)
                Assert.Equal(int64 content.Length, meta.size)
                Assert.Equal("test.txt", meta.fileName)
                Assert.Equal("text/plain", meta.mediaType)

            // 4. Verify exists and read back
            Assert.True(store.Exists sha)
            match store.Metadata sha with
            | None -> Assert.Fail("Metadata should exist")
            | Some (mediaType, fileName, size) ->
                Assert.Equal("text/plain", mediaType)
                Assert.Equal("test.txt", fileName)
                Assert.Equal(int64 content.Length, size)

            match store.OpenRead sha with
            | None -> Assert.Fail("OpenRead should return stream")
            | Some (stream, len) ->
                use s = stream
                use ms = new MemoryStream()
                s.CopyTo(ms)
                Assert.Equal(int64 content.Length, len)
                Assert.Equal<byte array>(content, ms.ToArray())
        finally
            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)

    [<Fact>]
    let ``AttachmentStore validates size limits and hashes strictly`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), sprintf "wanxiang-att-test-err-%s" (Guid.NewGuid().ToString("N")))
        try
            let store = new AttachmentStore(tempDir, 100L, 50)
            let sha = String.replicate 64 "a"
            let uploadId = Guid.NewGuid()

            // Size too large
            match store.Begin(1, uploadId, 200L, sha, "application/octet-stream", "big.bin") with
            | Error (AttachmentTooLarge _) -> ()
            | r -> Assert.Fail(sprintf "Expected AttachmentTooLarge, got %A" r)

            // Invalid sha256
            match store.Begin(1, uploadId, 50L, "short-sha", "application/octet-stream", "badsha.bin") with
            | Error (ValidationError msg) -> Assert.Contains("64", msg)
            | r -> Assert.Fail(sprintf "Expected ValidationError, got %A" r)

            // Correct begin, but invalid chunk sequence
            match store.Begin(1, uploadId, 40L, sha, "application/octet-stream", "file.bin") with
            | Error e -> Assert.Fail(sprintf "Unexpected begin error: %A" e)
            | Ok () -> ()

            // Duplicate begin
            match store.Begin(1, uploadId, 40L, sha, "application/octet-stream", "file.bin") with
            | Error (ValidationError msg) -> Assert.Contains("active", msg)
            | r -> Assert.Fail(sprintf "Expected ValidationError, got %A" r)

            // Out-of-order chunk (expecting 0, sending 1)
            let chunkBase64 = Convert.ToBase64String(Array.zeroCreate 20)
            match store.AppendChunk(uploadId, 1, chunkBase64) with
            | Error (ValidationError msg) -> Assert.Contains("out of order", msg)
            | r -> Assert.Fail(sprintf "Expected ValidationError out of order, got %A" r)

            // Chunk larger than maxChunkBytes (maxChunkBytes = 50)
            let hugeBase64 = Convert.ToBase64String(Array.zeroCreate 60)
            match store.AppendChunk(uploadId, 0, hugeBase64) with
            | Error (ValidationError msg) -> Assert.Contains("exceeds", msg)
            | r -> Assert.Fail(sprintf "Expected ValidationError exceeds, got %A" r)
        finally
            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)

    [<Fact>]
    let ``AttachmentStore abort cleans up temporary resources`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), sprintf "wanxiang-att-abort-%s" (Guid.NewGuid().ToString("N")))
        try
            let store = new AttachmentStore(tempDir, 1024L * 1024L, 1024)
            let sha = String.replicate 64 "b"
            let uploadId = Guid.NewGuid()

            store.Begin(10, uploadId, 50L, sha, "application/octet-stream", "temp.bin") |> ignore
            store.AppendChunk(uploadId, 0, Convert.ToBase64String(Array.zeroCreate 20)) |> ignore

            // Abort
            store.Abort(uploadId, "user cancelled")
            Assert.False(store.Exists sha)

            // Writing to aborted upload should fail
            match store.AppendChunk(uploadId, 1, Convert.ToBase64String(Array.zeroCreate 20)) with
            | Error (AttachmentIncomplete _) -> ()
            | r -> Assert.Fail(sprintf "Expected AttachmentIncomplete, got %A" r)

            // AbortByConnection
            let u2 = Guid.NewGuid()
            store.Begin(42, u2, 50L, sha, "application/octet-stream", "temp2.bin") |> ignore
            store.AbortByConnection 42
            match store.Complete(u2, sha) with
            | Error (AttachmentIncomplete _) -> ()
            | r -> Assert.Fail(sprintf "Expected AttachmentIncomplete, got %A" r)
        finally
            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)

    // =========================================================================
    // 2. ConfigMutation 配置变更验证测试
    // =========================================================================

    [<Fact>]
    let ``ConfigMutation upsertProvider validates inputs and preserves secrets`` () =
        let baseConfig = sampleAppConfig
        let args = JsonObject()

        // 1. Missing id
        match ConfigMutation.upsertProvider baseConfig args with
        | Error errs -> Assert.Contains(errs, fun e -> e.Contains "id: 必填")
        | Ok _ -> Assert.Fail("Should require id")

        // 2. Invalid id format
        args["id"] <- "invalid id with spaces"
        match ConfigMutation.upsertProvider baseConfig args with
        | Error errs -> Assert.Contains(errs, fun e -> e.Contains "只允许字母")
        | Ok _ -> Assert.Fail("Should validate id pattern")

        // 3. Missing baseUrl
        args["id"] <- "test-prov"
        match ConfigMutation.upsertProvider baseConfig args with
        | Error errs -> Assert.Contains(errs, fun e -> e.Contains "baseUrl: 必填")
        | Ok _ -> Assert.Fail("Should require baseUrl")

        // 4. Invalid kind
        args["baseUrl"] <- "https://api.example.com"
        args["kind"] <- "unsupported-proto"
        match ConfigMutation.upsertProvider baseConfig args with
        | Error errs -> Assert.Contains(errs, fun e -> e.Contains "暂不支持")
        | Ok _ -> Assert.Fail("Should validate kind")

        // 5. Valid provider creation
        args["kind"] <- "openai"
        args["apiKey"] <- "new-secret"
        let modelsArr = JsonArray()
        modelsArr.Add("m1")
        modelsArr.Add("m2")
        args["models"] <- modelsArr

        match ConfigMutation.upsertProvider baseConfig args with
        | Error errs -> Assert.Fail(String.concat "; " errs)
        | Ok cfg ->
            let prov = cfg.providers.["test-prov"]
            Assert.Equal("test-prov", prov.id)
            Assert.Equal("openai", prov.kind)
            Assert.Equal("https://api.example.com", prov.baseUrl)
            Assert.Equal(Some "new-secret", prov.apiKey)
            Assert.Equal<string list>(["m1"; "m2"], prov.models)

            // 6. Update existing provider keeping existing apiKey when null
            let updateArgs = JsonObject()
            updateArgs["id"] <- "test-prov"
            updateArgs["baseUrl"] <- "https://api.example.com/v2"
            updateArgs["models"] <- modelsArr.DeepClone()
            // apiKey is omitted/null -> should preserve "new-secret"
            match ConfigMutation.upsertProvider cfg updateArgs with
            | Error errs -> Assert.Fail(String.concat "; " errs)
            | Ok cfg2 ->
                let prov2 = cfg2.providers.["test-prov"]
                Assert.Equal("https://api.example.com/v2", prov2.baseUrl)
                Assert.Equal(Some "new-secret", prov2.apiKey)

    [<Fact>]
    let ``ConfigMutation deleteProvider handles active provider deletion safely`` () =
        let baseConfig = sampleAppConfig
        let args = JsonObject()
        args["id"] <- "test-del"
        args["baseUrl"] <- "https://api.example.com"
        args["kind"] <- "openai"
        let modelsArr = JsonArray()
        modelsArr.Add("m1")
        args["models"] <- modelsArr

        let cfgWithProv =
            match ConfigMutation.upsertProvider baseConfig args with
            | Ok c -> c
            | Error e -> failwith (String.concat "; " e)

        match ConfigMutation.deleteProvider cfgWithProv "test-del" with
        | Error e -> Assert.Fail(String.concat "; " e)
        | Ok cfgAfterDel ->
            Assert.False(cfgAfterDel.providers.ContainsKey "test-del")

    [<Fact>]
    let ``ConfigMutation upsertMcp and deleteMcp validates command and url exclusivity`` () =
        let baseConfig = sampleAppConfig
        let args = JsonObject()
        args["id"] <- "test-mcp"
        args["label"] <- "Test MCP"

        // Neither command nor url
        match ConfigMutation.upsertMcp baseConfig args with
        | Error errs -> Assert.Contains(errs, fun e -> e.Contains "command" && e.Contains "url")
        | Ok _ -> Assert.Fail("Should reject missing both transport options")

        // Both command and url
        args["command"] <- "npx"
        args["url"] <- "http://localhost:3000/mcp"
        match ConfigMutation.upsertMcp baseConfig args with
        | Error errs -> Assert.Contains(errs, fun e -> e.Contains "只能填一个")
        | Ok _ -> Assert.Fail("Should reject both transport options")

        // Valid stdio mcp
        let stdioArgs = JsonObject()
        stdioArgs["id"] <- "mcp-stdio"
        stdioArgs["label"] <- "Stdio Server"
        stdioArgs["command"] <- "node"
        let argsArray = JsonArray()
        argsArray.Add("server.js")
        stdioArgs["args"] <- argsArray
        stdioArgs["callTimeoutSeconds"] <- 45

        match ConfigMutation.upsertMcp baseConfig stdioArgs with
        | Error errs -> Assert.Fail(String.concat "; " errs)
        | Ok cfg ->
            let s = cfg.mcpServers.["mcp-stdio"]
            Assert.Equal(Some "node", s.command)
            Assert.Equal<string list>(["server.js"], s.args)
            Assert.Equal(45, s.callTimeoutSeconds)

            // Delete
            match ConfigMutation.deleteMcp cfg "mcp-stdio" with
            | Error errs -> Assert.Fail(String.concat "; " errs)
            | Ok finalCfg ->
                Assert.False(finalCfg.mcpServers.ContainsKey "mcp-stdio")

    [<Fact>]
    let ``ConfigMutation updateGeneration validates numerical ranges and parameters`` () =
        let baseConfig = sampleAppConfig
        let args = JsonObject()

        // Temperature out of range
        args["temperature"] <- 2.5
        match ConfigMutation.updateGeneration baseConfig args with
        | Error errs -> Assert.Contains(errs, fun e -> e.Contains "temperature")
        | Ok _ -> Assert.Fail("Should reject temperature > 2.0")

        // TopP out of range
        let args2 = JsonObject()
        args2["topP"] <- -0.1
        match ConfigMutation.updateGeneration baseConfig args2 with
        | Error errs -> Assert.Contains(errs, fun e -> e.Contains "topP")
        | Ok _ -> Assert.Fail("Should reject topP <= 0")

        // Valid generation update
        let validArgs = JsonObject()
        validArgs["temperature"] <- 0.8
        validArgs["topP"] <- 0.95
        validArgs["maxTokens"] <- 4096

        match ConfigMutation.updateGeneration baseConfig validArgs with
        | Error errs -> Assert.Fail(String.concat "; " errs)
        | Ok cfg ->
            Assert.Equal(Some 0.8, cfg.generation.temperature)
            Assert.Equal(Some 0.95, cfg.generation.topP)
            Assert.Equal(Some 4096, cfg.generation.maxTokens)
