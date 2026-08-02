module Wanxiang.Tests.BugfixTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core
open Wanxiang.Server
open Wanxiang.Store
open Wanxiang.Tests.Helpers

/// P2-4：chunk 大小上限执行（Q172）。
[<Fact>]
let ``attachment chunk exceeds configured limit is rejected`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L, 8) // chunkSizeBytes = 8
        let payload = "0123456789" |> Text.Encoding.UTF8.GetBytes
        let hash = Convert.ToHexString(SHA256.HashData payload).ToLowerInvariant()
        let aid = Guid.NewGuid()
        store.Begin(1, aid, int64 payload.Length, hash, "text/plain", "x.txt") |> function Ok () -> () | Error e -> failwith (WanxiangError.message e)
        match store.AppendChunk(aid, 0, Convert.ToBase64String payload) with
        | Error (ValidationError m) -> Assert.Contains("limit", m)
        | r -> failwithf "oversized chunk should be rejected, got %A" r
        // 合法块（≤8 字节）可以追加
        let aid2 = Guid.NewGuid()
        store.Begin(1, aid2, int64 payload.Length, hash, "text/plain", "x.txt") |> ignore
        let chunk1 = Convert.ToBase64String(payload[0..7])
        store.AppendChunk(aid2, 0, chunk1) |> function Ok () -> () | Error e -> failwith (WanxiangError.message e)
        store.AppendChunk(aid2, 1, Convert.ToBase64String(payload[8..])) |> function Ok () -> () | Error e -> failwith (WanxiangError.message e)
        store.Complete(aid2, hash) |> function Ok _ -> () | Error e -> failwith (WanxiangError.message e)
        store.Dispose()
    finally
        cleanup dir

/// P2-2：fileName 清理（Q175：控制字符清除 + 长度截断）。
[<Fact>]
let ``attachment fileName is sanitized`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L)
        let payload = "abc" |> Text.Encoding.UTF8.GetBytes
        let hash = Convert.ToHexString(SHA256.HashData payload).ToLowerInvariant()
        let aid = Guid.NewGuid()
        store.Begin(1, aid, 3L, hash, "text/plain", "\u0000bad\u0001name\u001f.txt") |> ignore
        store.AppendChunk(aid, 0, Convert.ToBase64String payload) |> ignore
        match store.Complete(aid, hash) with
        | Ok ref -> Assert.Equal("badname.txt", ref.fileName)
        | Error e -> failwith (WanxiangError.message e)
        // 超长文件名截断到 255
        let longName = String.replicate 300 "x" + ".txt"
        let aid2 = Guid.NewGuid()
        store.Begin(1, aid2, 3L, hash, "text/plain", longName) |> ignore
        store.AppendChunk(aid2, 0, Convert.ToBase64String payload) |> ignore
        match store.Complete(aid2, hash) with
        | Ok ref -> Assert.True(ref.fileName.Length <= 255)
        | Error e -> failwith (WanxiangError.message e)
        store.Dispose()
    finally
        cleanup dir

/// P2-2/P2-7：MIME 嗅探（Q176：声明值为 octet-stream 时采用嗅探结果）；
/// 元数据随 blob 落盘，下载可回传（Q176/P2-7）。
[<Fact>]
let ``attachment media type sniffed and metadata persisted`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L)
        // 1x1 PNG 魔数
        let png = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy; 0x00uy; 0x00uy; 0x00uy; 0x0Duy |]
        let hash = Convert.ToHexString(SHA256.HashData png).ToLowerInvariant()
        let aid = Guid.NewGuid()
        store.Begin(1, aid, int64 png.Length, hash, "application/octet-stream", "pic.png") |> ignore
        store.AppendChunk(aid, 0, Convert.ToBase64String png) |> ignore
        match store.Complete(aid, hash) with
        | Ok ref ->
            Assert.Equal("image/png", ref.mediaType)
            Assert.Equal("pic.png", ref.fileName)
        | Error e -> failwith (WanxiangError.message e)
        // 下载元数据回传（P2-7）
        match store.Metadata hash with
        | Some (mediaType, fileName, size) ->
            Assert.Equal("image/png", mediaType)
            Assert.Equal("pic.png", fileName)
            Assert.Equal(int64 png.Length, size)
        | None -> failwith "metadata missing"
        // 显式声明值不被嗅探覆盖
        let text = "hello" |> Text.Encoding.UTF8.GetBytes
        let thash = Convert.ToHexString(SHA256.HashData text).ToLowerInvariant()
        let aid3 = Guid.NewGuid()
        store.Begin(1, aid3, int64 text.Length, thash, "application/x-custom", "t.bin") |> ignore
        store.AppendChunk(aid3, 0, Convert.ToBase64String text) |> ignore
        match store.Complete(aid3, thash) with
        | Ok ref -> Assert.Equal("application/x-custom", ref.mediaType)
        | Error e -> failwith (WanxiangError.message e)
        store.Dispose()
    finally
        cleanup dir

/// P1-2：快照尾部截断（Q127：长会话快照不携带全部历史）。
[<Fact>]
let ``snapshot tail caps long conversation`` () =
    let dir = tempDir ()
    try
        let convId = newConversationId ()
        let commits =
            [ Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
              Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m1" } ]
              Events.Commit.create 3UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = assistantMessageJson "m2" } ]
              Events.Commit.create 4UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m3" } ]
              Events.Commit.create 5UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = assistantMessageJson "m4" } ] ]
        let proj =
            commits
            |> List.fold (fun p c -> Projection.applyCommit p c |> function Ok p -> p | Error e -> failwith (WanxiangError.message e)) Projection.empty
        let conv = Projection.tryConversation proj convId |> Option.get
        // 不截断：全量 + hasMore=false
        let all, e0, hasMore0 = ServerModel.conversationMessagesTail proj conv 0
        Assert.Equal(4, all.Count)
        Assert.False hasMore0
        Assert.Equal(0UL, e0)
        // 截断：只留尾部 2 条，earliest = 4，hasMore=true
        let tail, earliest, hasMore = ServerModel.conversationMessagesTail proj conv 2
        Assert.Equal(2, tail.Count)
        Assert.Equal(4UL, (tail[0].AsObject()["commitId"]).GetValue<uint64>())
        Assert.Equal(5UL, (tail[1].AsObject()["commitId"]).GetValue<uint64>())
        Assert.True hasMore
        Assert.Equal(4UL, earliest)
    finally
        ()

/// P1-2：历史分页切片（Q127：按 commitID 反向，稳定 ID 页边界）。
[<Fact>]
let ``history paging slices by commit id`` () =
    let convId = newConversationId ()
    let commits =
        [ Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = convId; title = "A"; config = testConfig () } ]
          Events.Commit.create 2UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m1" } ]
          Events.Commit.create 3UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = assistantMessageJson "m2" } ]
          Events.Commit.create 4UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m3" } ]
          Events.Commit.create 5UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = assistantMessageJson "m4" } ]
          Events.Commit.create 6UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "m5" } ] ]
    let proj =
        commits
        |> List.fold (fun p c -> Projection.applyCommit p c |> function Ok p -> p | Error e -> failwith (WanxiangError.message e)) Projection.empty
    let conv = Projection.tryConversation proj convId |> Option.get
    // beforeCommitId=6, limit=2 → [4,5], hasMore（还有 2,3）
    let items, hasMore = ServerModel.historyPageItems proj conv 6UL 2
    Assert.Equal(2, items.Count)
    Assert.Equal(4UL, (items[0].AsObject()["commitId"]).GetValue<uint64>())
    Assert.Equal(5UL, (items[1].AsObject()["commitId"]).GetValue<uint64>())
    Assert.True hasMore
    // 继续：beforeCommitId=4, limit=10 → [2,3], 无更多
    let items2, hasMore2 = ServerModel.historyPageItems proj conv 4UL 10
    Assert.Equal(2, items2.Count)
    Assert.Equal(2UL, (items2[0].AsObject()["commitId"]).GetValue<uint64>())
    Assert.Equal(3UL, (items2[1].AsObject()["commitId"]).GetValue<uint64>())
    Assert.False hasMore2
    // limit 钳制：≤200
    let items3, _ = ServerModel.historyPageItems proj conv 6UL 10000
    Assert.Equal(4, items3.Count)

/// P2-3：附件引用提取（Q179：doctor 可达性检查与客户端展示共用）。
[<Fact>]
let ``attachment refs are extracted from message payload`` () =
    let payload = JsonNode.Parse("""{"role":"user","contents":[{"text":"hi"},{"type":"attachment","sha256":"aabbccdd","size":42,"mediaType":"image/png","fileName":"x.png"}]}""")
    match ServerModel.attachmentRefsOf payload with
    | [ (sha, mediaType, fileName, size) ] ->
        Assert.Equal("aabbccdd", sha)
        Assert.Equal("image/png", mediaType)
        Assert.Equal("x.png", fileName)
        Assert.Equal(42L, size)
    | r -> failwithf "expected one ref, got %A" r

/// 决策 41：写 stderr 前对已知密钥值做精确替换（不泄漏 apiKey 等凭据）。
[<Fact>]
let ``stderr redacts registered secrets`` () =
    let secret = "sk-test-secret-value-123456"
    try
        Stderr.registerSecrets [ secret ]
        // redact 精确替换已知密钥
        Assert.Equal("error: *** occurred", Stderr.redact (sprintf "error: %s occurred" secret))
        // 未注册的字符串不受影响
        Assert.Equal("other text", Stderr.redact "other text")
        // 短于 8 字符的候选不注册（避免误伤普通词）
        Stderr.registerSecrets [ "short" ]
        Assert.Equal("short word", Stderr.redact "short word")
    finally
        Stderr.clearSecrets ()

/// Stderr 脱敏按长度降序替换：短密钥若是长密钥子串，先替换短的不破坏长的完整匹配。
[<Fact>]
let ``stderr redact replaces longer secrets first`` () =
    try
        let shortSecret = "short-key-123"
        let longSecret = "short-key-1234567890-long"
        Stderr.registerSecrets [ shortSecret; longSecret ]
        // 文本含长密钥：应整体替换为 ***（若短先替换则长密钥残余）
        let text = sprintf "error with %s inside" longSecret
        let result = Stderr.redact text
        Assert.DoesNotContain(longSecret, result)
        Assert.DoesNotContain(shortSecret, result)
        Assert.Contains("***", result)
        // 文本只含短密钥：正常替换
        Assert.Equal("x *** y", Stderr.redact (sprintf "x %s y" shortSecret))
    finally
        Stderr.clearSecrets ()
