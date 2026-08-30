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

// ---------------------------------------------------------------- 代码着色

// 语法本身是 highlight.js 的职责，不在这里重测。这里测本项目**自己**写的那两段：
// 从 hljs 的 HTML 里还原文本与 scope，以及语言标注的别名解析。

let private textOf (html: string) =
    Highlight.parseHtml html |> List.map (fun t -> t.text) |> String.concat ""

let private kindOfFirst (html: string) (text: string) =
    Highlight.parseHtml html |> List.tryFind (fun t -> t.text = text) |> Option.map (fun t -> t.kind)

[<Fact>]
let ``span parser restores the source text exactly`` () =
    let html =
        "<span class=\"hljs-keyword\">let</span> <span class=\"hljs-keyword\">rec</span> fib n "
        + "<span class=\"hljs-operator\">=</span>\n  <span class=\"hljs-number\">0</span>"
    Assert.Equal("let rec fib n =\n  0", textOf html)

[<Fact>]
let ``html entities are decoded back to source characters`` () =
    // hljs 会把 -> 转义成 -&gt;；不解码的话代码块里会显示实体本身
    let html = "<span class=\"hljs-operator\">-&gt;</span> &amp; &lt;T&gt; &quot;q&quot;"
    Assert.Equal("-> & <T> \"q\"", textOf html)

[<Fact>]
let ``the innermost scope wins over the enclosing one`` () =
    // 字符串里的插值该按变量着色，否则整个模板串糊成一块
    let html =
        "<span class=\"hljs-string\">`a</span><span class=\"hljs-subst\">${x}</span>"
        + "<span class=\"hljs-string\">b`</span>"
    Assert.Equal(Some CodeString, kindOfFirst html "`a")
    Assert.Equal(Some CodeVariable, kindOfFirst html "${x}")

[<Fact>]
let ``unknown scopes inherit the enclosing kind instead of resetting to plain`` () =
    let html = "<span class=\"hljs-comment\">// <span class=\"hljs-unheard-of\">x</span></span>"
    // 继承成功的证据正是它与外层合并成了一段注释
    Assert.Equal(Some CodeComment, kindOfFirst html "// x")

[<Fact>]
let ``hljs sub scopes map through their dotted path`` () =
    Assert.Equal(Some CodeFunction, Highlight.kindOfScope "title.function")
    Assert.Equal(Some CodeType, Highlight.kindOfScope "title.class")
    Assert.Equal(Some CodeString, Highlight.kindOfScope "char.escape")
    Assert.Equal(None, Highlight.kindOfScope "emphasis")

[<Fact>]
let ``a deep unregistered sub scope falls back to its first segment`` () =
    // hljs 的 class 写法是 `hljs-title function_ invoke__`
    let html = "<span class=\"hljs-title function_ invoke__\">f</span>"
    Assert.Equal(Some CodeFunction, kindOfFirst html "f")

[<Fact>]
let ``adjacent tokens of one kind are merged`` () =
    let html = "<span class=\"hljs-keyword\">le</span><span class=\"hljs-keyword\">t</span>"
    Assert.Single(Highlight.parseHtml html) |> ignore

[<Fact>]
let ``malformed markup never throws and never drops visible text`` () =
    for html in [ "<span class=\"hljs-keyword\">let"; "</span></span>x"; "<span"; ""; "<>"; "&notanentity;" ] do
        let rebuilt = textOf html
        Assert.NotNull rebuilt
    Assert.Equal("let", textOf "<span class=\"hljs-keyword\">let")
    Assert.Equal("x", textOf "</span></span>x")

[<Fact>]
let ``language annotations resolve through hljs aliases`` () =
    Assert.Equal(Some "typescript", RichAssets.resolveLanguage "TS")
    Assert.Equal(Some "fsharp", RichAssets.resolveLanguage "F#")
    Assert.Equal(Some "python", RichAssets.resolveLanguage "py")
    Assert.Equal(Some "bash", RichAssets.resolveLanguage "sh")
    Assert.Equal(Some "latex", RichAssets.resolveLanguage "tex")
    // shell 是 hljs 里独立的一门语言（shell 会话），不是 bash 的别名
    Assert.Equal(Some "shell", RichAssets.resolveLanguage "shell")

[<Fact>]
let ``an unknown language annotation resolves to nothing`` () =
    Assert.Equal(None, RichAssets.resolveLanguage "")
    Assert.Equal(None, RichAssets.resolveLanguage "   ")
    Assert.Equal(None, RichAssets.resolveLanguage "沒有這種語言")

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
let ``garbage collection removes only unreferenced blobs`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L * 1024L)
        let kept = upload store "keep me"
        let dropped = upload store "drop me"
        Assert.True(store.Exists kept)
        Assert.True(store.Exists dropped)
        let removed, freed = store.CollectGarbage(Set.ofList [ kept ], TimeSpan.Zero)
        Assert.Equal(1, removed)
        Assert.True(freed > 0L)
        Assert.True(store.Exists kept)
        Assert.False(store.Exists dropped)
    finally
        cleanup dir

[<Fact>]
let ``the grace window protects blobs that no message references yet`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L * 1024L)
        // 分块上传完成与消息提交之间存在窗口，此刻孤儿 blob 是合法的
        let fresh = upload store "just uploaded"
        let removed, _ = store.CollectGarbage(Set.empty, TimeSpan.FromMinutes 10.0)
        Assert.Equal(0, removed)
        Assert.True(store.Exists fresh)
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
        store.CollectGarbage(Set.empty, TimeSpan.Zero) |> ignore
        // 未完成的上传住在 .tmp 里，清扫不该碰它，后续 chunk/complete 仍要能跑通
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
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coordinator = new CommitCoordinator(dir, outcome, ignore, ignore)
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

        // 父会话已删，但子分支仍继承那条带附件的消息
        let referenced = ServerModel.referencedAttachments coordinator.Projection
        Assert.Contains(sha, referenced)
    finally
        cleanup dir

[<Fact>]
let ``attachments of a fully deleted conversation become unreferenced`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coordinator = new CommitCoordinator(dir, outcome, ignore, ignore)
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
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coordinator = new CommitCoordinator(dir, outcome, ignore, ignore)
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
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coordinator = new CommitCoordinator(dir, outcome, ignore, ignore)
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
        let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use coordinator = new CommitCoordinator(dir, outcome, ignore, ignore)
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
            let outcome = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
            use coordinator = new CommitCoordinator(dir, outcome, ignore, ignore)
            coordinator.SubmitEvents [ ConversationCreated { conversationId = convId; title = "T"; config = testConfig () } ]
            |> ignore
            coordinator.SubmitEvents [ AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "hi" } ]
            |> ignore
            let conv = Projection.tryConversation coordinator.Projection convId |> Option.get
            (Projection.effectiveMessages coordinator.Projection conv |> List.exactlyOne).committedAtUtc
        let replayed = Replay.replay dir false |> function Ok o -> o | Error e -> failwith e
        use reopened = new CommitCoordinator(dir, replayed, ignore, ignore)
        let conv = Projection.tryConversation reopened.Projection convId |> Option.get
        let restored = (Projection.effectiveMessages reopened.Projection conv |> List.exactlyOne).committedAtUtc
        // NDJSON 只保留到毫秒，比较时按毫秒对齐
        Assert.Equal(recorded.ToUnixTimeMilliseconds(), restored.ToUnixTimeMilliseconds())
    finally
        cleanup dir

