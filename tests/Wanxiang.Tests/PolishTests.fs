module Wanxiang.Tests.PolishTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core
open Wanxiang.Server
open Wanxiang.Store
open Wanxiang.UI
open Wanxiang.Tests.Helpers

// ---------------------------------------------------------------- 附件引用与回收

let private attachmentMessage (sha256: string) : JsonNode =
    let o = JsonObject()
    o["role"] <- "user"
    let contents = JsonArray()
    let text = JsonObject()
    text["text"] <- "带附件"
    contents.Add text
    let attachment = JsonObject()
    attachment["type"] <- "attachment"
    attachment["sha256"] <- sha256
    attachment["size"] <- 3L
    attachment["mediaType"] <- "application/octet-stream"
    attachment["fileName"] <- "a.bin"
    contents.Add attachment
    o["contents"] <- contents
    o

let private upload (store: AttachmentStore) (payload: string) : string =
    let bytes = Text.Encoding.UTF8.GetBytes payload
    let sha = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let id = Guid.CreateVersion7()
    match store.Begin(1, id, int64 bytes.Length, sha, "application/octet-stream", "a.bin") with
    | Error e -> failwithf "begin failed: %s" (WanxiangError.message e)
    | Ok () -> ()
    match store.AppendChunk(id, 0, Convert.ToBase64String bytes) with
    | Error e -> failwithf "chunk failed: %s" (WanxiangError.message e)
    | Ok () -> ()
    match store.Complete(id, sha) with
    | Error e -> failwithf "complete failed: %s" (WanxiangError.message e)
    | Ok _ -> ()
    sha

[<Fact>]
let ``unreferenced blobs are retained without v1 gc`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L * 1024L)
        let kept = upload store "keep me"
        let dropped = upload store "drop me"
        // 决策 73：首版不做附件 GC——没有任何引用也必须留着，内容可读回
        Assert.True(store.Exists kept)
        Assert.True(store.Exists dropped)
        match store.OpenRead dropped with
        | None -> failwith "retained blob should be readable"
        | Some (stream, _) ->
            use stream = stream
            use reader = new StreamReader(stream)
            Assert.Equal("drop me", reader.ReadToEnd())
    finally
        cleanup dir

[<Fact>]
let ``in-flight uploads are never collected`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L * 1024L)
        let bytes = Text.Encoding.UTF8.GetBytes "half sent"
        let sha = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
        let id = Guid.CreateVersion7()
        store.Begin(1, id, int64 bytes.Length, sha, "application/octet-stream", "a.bin")
        |> function Ok () -> () | Error e -> failwithf "%s" (WanxiangError.message e)
        // 未完成的上传住在 .tmp 里（首版无任何清扫路径），后续 chunk/complete 仍要能跑通
        store.AppendChunk(id, 0, Convert.ToBase64String bytes)
        |> function Ok () -> () | Error e -> failwithf "%s" (WanxiangError.message e)
        match store.Complete(id, sha) with
        | Ok committed -> Assert.Equal(sha, committed.sha256)
        | Error e -> failwithf "complete failed after gc: %s" (WanxiangError.message e)
    finally
        cleanup dir

[<Fact>]
let ``a live fork keeps attachments owned by its deleted parent`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coordinator = new EventStore(dir)
        let parent = newConversationId ()
        let sha = String('a', 64)
        coordinator.SubmitEvents [ ConversationCreated { conversationId = parent; title = "P"; config = testConfig () } ]
        |> ignore
        let messageCommit =
            coordinator.SubmitEvents [ AgentMessageRecorded { conversationId = parent; payloadJson = attachmentMessage sha } ]
            |> function
                | SubmitResult.Committed c -> c.id
                | other -> failwithf "unexpected submit result: %A" other
        let child = newConversationId ()
        coordinator.SubmitEvents
            [ ConversationForked
                { conversationId = child
                  parentConversationId = parent
                  forkAfterId = Some messageCommit } ]
        |> ignore
        coordinator.SubmitEvents [ EventData.ConversationDeleted { conversationId = parent } ] |> ignore

        // 父对话已删，但子分支仍继承那条带附件的消息
        let referenced = ServerModel.referencedAttachments coordinator.Projection
        Assert.Contains(sha, referenced)
    finally
        cleanup dir

[<Fact>]
let ``attachments of a fully deleted conversation become unreferenced`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coordinator = new EventStore(dir)
        let convId = newConversationId ()
        let sha = String('b', 64)
        coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
        |> ignore
        coordinator.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = attachmentMessage sha } ]
        |> ignore
        Assert.Contains(sha, ServerModel.referencedAttachments coordinator.Projection)
        coordinator.SubmitEvents [ EventData.ConversationDeleted { conversationId = convId } ] |> ignore
        Assert.DoesNotContain(sha, ServerModel.referencedAttachments coordinator.Projection)
    finally
        cleanup dir

[<Fact>]
let ``a tombstoned message still protects its attachment`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coordinator = new EventStore(dir)
        let convId = newConversationId ()
        let sha = String('c', 64)
        coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
        |> ignore
        let messageCommit =
            coordinator.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = attachmentMessage sha } ]
            |> function
                | SubmitResult.Committed c -> c.id
                | other -> failwithf "unexpected submit result: %A" other
        coordinator.SubmitEvents [ MessageDeleted { conversationId = convId; messageCommitId = messageCommit } ] |> ignore
        // 历史分页能按更早的 commitId 回看这条消息，删 blob 会让历史变成「内容已丢失」
        Assert.Contains(sha, ServerModel.referencedAttachments coordinator.Projection)
    finally
        cleanup dir

// ---------------------------------------------------------------- 消息时间戳

[<Fact>]
let ``messages carry their commit timestamp through the projection`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coordinator = new EventStore(dir)
        let convId = newConversationId ()
        let before = DateTimeOffset.UtcNow.AddSeconds -5.0
        coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
        |> ignore
        coordinator.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "hi" } ]
        |> ignore
        let conv = Projection.tryConversation coordinator.Projection convId |> Option.get
        let message = Projection.effectiveMessages coordinator.Projection conv |> List.exactlyOne
        Assert.True(message.committedAtUtc >= before)
        Assert.True(message.committedAtUtc <= DateTimeOffset.UtcNow.AddSeconds 5.0)
    finally
        cleanup dir

[<Fact>]
let ``snapshot items expose committedAt so the client can show a time`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = replayOutcome dir
        use coordinator = new EventStore(dir)
        let convId = newConversationId ()
        coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
        |> ignore
        coordinator.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "hi" } ]
        |> ignore
        let conv = Projection.tryConversation coordinator.Projection convId |> Option.get
        let items = ServerModel.conversationMessages coordinator.Projection conv
        let item = items[0].AsObject()
        let mutable node: JsonNode = null
        Assert.True(item.TryGetPropertyValue("committedAt", &node))
        match DateTimeOffset.TryParse(node.GetValue<string>()) with
        | true, _ -> ()
        | _ -> failwithf "committedAt is not a parseable timestamp: %s" (node.ToJsonString())
    finally
        cleanup dir

[<Fact>]
let ``committed timestamps survive a restart replay`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let convId = newConversationId ()
        let recorded =
            let outcome = replayOutcome dir
            use coordinator = new EventStore(dir)
            coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
            |> ignore
            coordinator.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "hi" } ]
            |> ignore
            let conv = Projection.tryConversation coordinator.Projection convId |> Option.get
            (Projection.effectiveMessages coordinator.Projection conv |> List.exactlyOne).committedAtUtc
        let replayed = replayOutcome dir
        use reopened = new EventStore(dir)
        let conv = Projection.tryConversation reopened.Projection convId |> Option.get
        let restored = (Projection.effectiveMessages reopened.Projection conv |> List.exactlyOne).committedAtUtc
        // NDJSON 只保留到毫秒，比较时按毫秒对齐
        Assert.Equal(recorded.ToUnixTimeMilliseconds(), restored.ToUnixTimeMilliseconds())
    finally
        cleanup dir

