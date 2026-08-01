module Wanxiang.Tests.CodecTests

open System
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Tests.Helpers

[<Fact>]
let ``test_13241`` () =
    let convId = newConversationId ()
    let commit =
        Events.Commit.create 1UL DateTimeOffset.UtcNow
            [ ConversationCreated { conversationId = convId; title = "测试"; config = testConfig () }
              AgentMessageRecorded { conversationId = convId; payloadJson = userMessageJson "你好" } ]
        |> Events.Commit.withCommand "cmd-1" "conversation.create" "hash-1"
    let line = CommitCodec.commitToJsonLine commit
    match CommitCodec.tryCommitFromJsonLine line with
    | None -> failwith "decode failed"
    | Some decoded ->
        Assert.Equal(commit.id, decoded.id)
        Assert.Equal(2, decoded.events.Length)
        Assert.Equal("cmd-1", decoded.commandId |> Option.defaultValue "")

[<Fact>]
let ``test_93037`` () =
    let payload1 = """{"b":2,"a":{"c":1},"arr":[1,2]}"""
    let payload2 = """{ "arr" : [1,2], "a": { "c" : 1 }, "b" : 2 }"""
    let n1 = CanonicalJson.tryNormalize payload1
    let n2 = CanonicalJson.tryNormalize payload2
    Assert.Equal(n1, n2)
    let inv = Guid.NewGuid()
    let id1 = CommandId.compute inv "test.cmd" n1.Value
    let id2 = CommandId.compute inv "test.cmd" n2.Value
    Assert.Equal(id1, id2)
    // 不同载荷 → 不同 commandId
    let id3 = CommandId.compute inv "test.cmd" """{"a":2}"""
    Assert.NotEqual<string>(id1, id3)

[<Fact>]
let ``test_9659`` () =
    let convId = newConversationId ()
    let ev = MessageCommitted {| conversationId = convId; commitId = 42UL; payload = userMessageJson "hi" |}
    let json = WireCodec.encode ev
    match WireCodec.tryDecode json with
    | Ok (MessageCommitted d) ->
        Assert.Equal(convId, d.conversationId)
        Assert.Equal(42UL, d.commitId)
    | _ -> failwith "decode mismatch"

[<Fact>]
let ``test_18323`` () =
    let convId = newConversationId ()
    let cmd = SendUserMessage {| invocationId = Guid.NewGuid(); conversationId = convId; messageJson = userMessageJson "hello" |}
    let json = WireCodec.encodeCommand cmd
    match WireCodec.tryDecodeCommand json with
    | Ok (SendUserMessage d) ->
        Assert.Equal(convId, d.conversationId)
    | _ -> failwith "command decode mismatch"

[<Fact>]
let ``test_authority_catch_up_roundtrip`` () =
    let items = System.Text.Json.Nodes.JsonArray()
    items.Add("{\"id\":1,\"events\":[]}")
    let encoded = WireCodec.encode (AuthorityCatchUp {| fromCursor = 0UL; toCommitId = 1UL; items = items |})
    match WireCodec.tryDecode encoded with
    | Ok (AuthorityCatchUp d) ->
        Assert.Equal(0UL, d.fromCursor)
        Assert.Equal(1UL, d.toCommitId)
        Assert.Single(d.items)
[<Fact>]
let ``attachment download events roundtrip in order`` () =
    let hash = "a".PadRight(64, 'a')
    let events : WireEvent list =
        [ AttachmentDownloadBegin {| sha256 = hash; size = 3L; mediaType = "application/octet-stream"; fileName = hash |}
          AttachmentDownloadChunk {| sha256 = hash; index = 0; dataBase64 = Convert.ToBase64String([| 1uy; 2uy; 3uy |]) |}
          AttachmentDownloadComplete {| sha256 = hash |} ]
    let decoded = events |> List.map (WireCodec.encode >> WireCodec.tryDecode)
    Assert.All(decoded, fun result -> Assert.True(result.IsOk))
    match decoded with
    | [ Ok (AttachmentDownloadBegin b); Ok (AttachmentDownloadChunk c); Ok (AttachmentDownloadComplete e) ] ->
        Assert.Equal(hash, b.sha256)
        Assert.Equal(3L, b.size)
        Assert.Equal(0, c.index)
        Assert.Equal(hash, e.sha256)
[<Fact>]
let ``test_11279`` () =
    // MAF ChatMessage 序列化后应能原样 roundtrip（结构不被万象解释）
    let original = """{"role":"user","contents":[{"$type":"text","text":"你好"}]}"""
    let node = System.Text.Json.Nodes.JsonNode.Parse original
    let commit = Events.Commit.create 1UL DateTimeOffset.UtcNow [ AgentMessageRecorded { conversationId = Guid.NewGuid(); payloadJson = node } ]
    let line = CommitCodec.commitToJsonLine commit
    match CommitCodec.tryCommitFromJsonLine line with
    | Some c ->
        match c.events.Head with
        | AgentMessageRecorded d ->
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(node, d.payloadJson))
        | _ -> failwith "wrong event"
    | None -> failwith "decode failed"
