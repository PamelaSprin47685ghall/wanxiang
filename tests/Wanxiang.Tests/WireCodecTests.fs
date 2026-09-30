namespace Wanxiang.Tests

open System
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Tests.Helpers

module WireCodecTests =

    let private convId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let private genId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let private invId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let private attId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    let private reqId = Guid.Parse("55555555-5555-5555-5555-555555555555")
    let private expId = Guid.Parse("66666666-6666-6666-6666-666666666666")
    let private fixedTime = DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero)

    let private sampleConfig () =
        { SessionConfig.empty with provider = "test-provider"; model = "test-model" }

    // ==========================================
    // 1. ClientCommand 正常编解码 Roundtrip
    // ==========================================

    [<Fact>]
    let ``CreateConversation command roundtrips perfectly`` () =
        let cfg = sampleConfig ()
        let cmd = CreateConversation {| invocationId = invId; conversationId = convId; title = "新会话"; config = cfg |}
        let encoded = WireCodec.encodeCommand cmd
        Assert.Contains("\"type\":\"conversation.create\"", encoded)
        Assert.Contains("\"title\":\"新会话\"", encoded)
        match WireCodec.tryDecodeCommand encoded with
        | Ok (CreateConversation d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal(convId, d.conversationId)
            Assert.Equal("新会话", d.title)
            Assert.Equal(cfg.provider, d.config.provider)
            Assert.Equal(cfg.model, d.config.model)
        | other -> failwithf "Unexpected result: %A" other

    [<Fact>]
    let ``ForkConversation command roundtrips with forkAfterId Some and None`` () =
        let parentId = Guid.NewGuid()
        let editedJson = JsonNode.Parse("""{"role":"user","content":"forked"}""")
        let cfg = sampleConfig ()

        // 1. Some forkAfterId
        let cmd1 = ForkConversation {| invocationId = invId; conversationId = convId; parentConversationId = parentId; forkAfterId = Some 10UL; config = cfg; editedMessageJson = editedJson |}
        let enc1 = WireCodec.encodeCommand cmd1
        Assert.Contains("\"forkAfterId\":10", enc1)
        match WireCodec.tryDecodeCommand enc1 with
        | Ok (ForkConversation d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(parentId, d.parentConversationId)
            Assert.Equal(Some 10UL, d.forkAfterId)
            Assert.Equal("forked", d.editedMessageJson["content"].GetValue<string>())
        | other -> failwithf "Unexpected result: %A" other

        // 2. None forkAfterId
        let cmd2 = ForkConversation {| invocationId = invId; conversationId = convId; parentConversationId = parentId; forkAfterId = None; config = cfg; editedMessageJson = editedJson |}
        let enc2 = WireCodec.encodeCommand cmd2
        Assert.DoesNotContain("\"forkAfterId\"", enc2)
        match WireCodec.tryDecodeCommand enc2 with
        | Ok (ForkConversation d) ->
            Assert.Equal(None, d.forkAfterId)
        | other -> failwithf "Unexpected result: %A" other

    [<Fact>]
    let ``SendUserMessage command roundtrips perfectly`` () =
        let msgJson = JsonNode.Parse("""{"role":"user","text":"你好万象"}""")
        let cmd = SendUserMessage {| invocationId = invId; conversationId = convId; messageJson = msgJson |}
        let encoded = WireCodec.encodeCommand cmd
        Assert.Contains("\"type\":\"chat.user-message.enqueue\"", encoded)
        match WireCodec.tryDecodeCommand encoded with
        | Ok (SendUserMessage d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal(convId, d.conversationId)
            Assert.Equal("你好万象", d.messageJson["text"].GetValue<string>())
        | other -> failwithf "Unexpected result: %A" other

    [<Fact>]
    let ``RenameConversation command roundtrips perfectly`` () =
        let cmd = RenameConversation {| invocationId = invId; conversationId = convId; title = "重命名" |}
        let encoded = WireCodec.encodeCommand cmd
        Assert.Contains("\"type\":\"conversation.rename\"", encoded)
        match WireCodec.tryDecodeCommand encoded with
        | Ok (RenameConversation d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal(convId, d.conversationId)
            Assert.Equal("重命名", d.title)
        | other -> failwithf "Unexpected result: %A" other

    [<Fact>]
    let ``DeleteConversation command roundtrips perfectly`` () =
        let cmd = DeleteConversation {| invocationId = invId; conversationId = convId |}
        let encoded = WireCodec.encodeCommand cmd
        Assert.Contains("\"type\":\"conversation.delete\"", encoded)
        match WireCodec.tryDecodeCommand encoded with
        | Ok (DeleteConversation d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal(convId, d.conversationId)
        | other -> failwithf "Unexpected result: %A" other

    [<Fact>]
    let ``DeleteMessage command roundtrips perfectly`` () =
        let cmd = DeleteMessage {| invocationId = invId; conversationId = convId; messageCommitId = 42UL |}
        let encoded = WireCodec.encodeCommand cmd
        Assert.Contains("\"type\":\"message.delete\"", encoded)
        Assert.Contains("\"messageCommitId\":42", encoded)
        match WireCodec.tryDecodeCommand encoded with
        | Ok (DeleteMessage d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(42UL, d.messageCommitId)
        | other -> failwithf "Unexpected result: %A" other

    [<Fact>]
    let ``UpdateConversationConfig command roundtrips perfectly`` () =
        let cfg = sampleConfig ()
        let cmd = UpdateConversationConfig {| invocationId = invId; conversationId = convId; config = cfg |}
        let encoded = WireCodec.encodeCommand cmd
        Assert.Contains("\"type\":\"conversation.config-update\"", encoded)
        match WireCodec.tryDecodeCommand encoded with
        | Ok (UpdateConversationConfig d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(cfg.provider, d.config.provider)
        | other -> failwithf "Unexpected result: %A" other

    [<Fact>]
    let ``SetConversationFlags command roundtrips perfectly`` () =
        let cmd1 = SetConversationFlags {| invocationId = invId; conversationId = convId; pinned = true; archived = false |}
        let enc1 = WireCodec.encodeCommand cmd1
        Assert.Contains("\"type\":\"conversation.flags-set\"", enc1)
        Assert.Contains("\"pinned\":true", enc1)
        Assert.Contains("\"archived\":false", enc1)
        match WireCodec.tryDecodeCommand enc1 with
        | Ok (SetConversationFlags d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal(convId, d.conversationId)
            Assert.True(d.pinned)
            Assert.False(d.archived)
        | other -> failwithf "Unexpected result: %A" other

        let cmd2 = SetConversationFlags {| invocationId = invId; conversationId = convId; pinned = false; archived = true |}
        let enc2 = WireCodec.encodeCommand cmd2
        match WireCodec.tryDecodeCommand enc2 with
        | Ok (SetConversationFlags d) ->
            Assert.False(d.pinned)
            Assert.True(d.archived)
        | other -> failwithf "Unexpected result: %A" other

    [<Fact>]
    let ``RegenerateResponse command roundtrips perfectly`` () =
        let cmd = RegenerateResponse {| invocationId = invId; conversationId = convId |}
        let encoded = WireCodec.encodeCommand cmd
        Assert.Contains("\"type\":\"chat.regenerate\"", encoded)
        match WireCodec.tryDecodeCommand encoded with
        | Ok (RegenerateResponse d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal(convId, d.conversationId)
        | other -> failwithf "Unexpected result: %A" other

    // ==========================================
    // 2. ClientCommand 解码边界与错误分支
    // ==========================================

    [<Fact>]
    let ``tryDecodeCommand handles malformed JSON and non-object roots`` () =
        match WireCodec.tryDecodeCommand "{" with
        | Error msg -> Assert.Contains("command decode failed", msg)
        | Ok _ -> failwith "Expected error for malformed json"

        match WireCodec.tryDecodeCommand "123" with
        | Error msg -> Assert.Equal("not a JSON object", msg)
        | Ok _ -> failwith "Expected error for number json"

        match WireCodec.tryDecodeCommand "[1, 2, 3]" with
        | Error msg -> Assert.Equal("not a JSON object", msg)
        | Ok _ -> failwith "Expected error for array json"

    [<Fact>]
    let ``tryDecodeCommand handles missing or invalid type field`` () =
        match WireCodec.tryDecodeCommand "{}" with
        | Error msg -> Assert.Equal("missing type", msg)
        | Ok _ -> failwith "Expected error for missing type"

        match WireCodec.tryDecodeCommand """{"type": 12345}""" with
        | Error msg -> Assert.Equal("missing type", msg)
        | Ok _ -> failwith "Expected error for non-string type"

        match WireCodec.tryDecodeCommand """{"type": "non.existent.command"}""" with
        | Error msg -> Assert.Equal("not a command event", msg)
        | Ok _ -> failwith "Expected error for unknown type"

    [<Fact>]
    let ``tryDecodeCommand handles missing payload for all commands`` () =
        let types =
            [ "conversation.create"
              "conversation.fork"
              "chat.user-message.enqueue"
              "conversation.rename"
              "conversation.delete"
              "message.delete"
              "conversation.config-update"
              "conversation.flags-set"
              "chat.regenerate" ]
        for t in types do
            let json = sprintf """{"type": "%s"}""" t
            match WireCodec.tryDecodeCommand json with
            | Error msg -> Assert.Equal(sprintf "%s: missing payload" t, msg)
            | Ok _ -> failwithf "Type %s: expected missing payload error" t

            let jsonNonObj = sprintf """{"type": "%s", "payload": "not an object"}""" t
            match WireCodec.tryDecodeCommand jsonNonObj with
            | Error msg -> Assert.Equal(sprintf "%s: missing payload" t, msg)
            | Ok _ -> failwithf "Type %s: expected missing payload error" t

    [<Fact>]
    let ``tryDecodeCommand handles missing required fields inside payloads`` () =
        // conversation.create missing invocationId / threadId
        let noInv = sprintf """{"type":"conversation.create","payload":{"threadId":"%O"}}""" convId
        match WireCodec.tryDecodeCommand noInv with
        | Error msg -> Assert.Equal("conversation.create: missing invocationId/threadId", msg)
        | Ok _ -> failwith "Expected error"

        let noThread = sprintf """{"type":"conversation.create","payload":{"invocationId":"%O"}}""" invId
        match WireCodec.tryDecodeCommand noThread with
        | Error msg -> Assert.Equal("conversation.create: missing invocationId/threadId", msg)
        | Ok _ -> failwith "Expected error"

        // chat.user-message.enqueue missing message
        let noMsg = sprintf """{"type":"chat.user-message.enqueue","payload":{"invocationId":"%O","threadId":"%O"}}""" invId convId
        match WireCodec.tryDecodeCommand noMsg with
        | Error msg -> Assert.Equal("chat.user-message.enqueue: missing message", msg)
        | Ok _ -> failwith "Expected error"

        // message.delete missing messageCommitId
        let noMsgId = sprintf """{"type":"message.delete","payload":{"invocationId":"%O","threadId":"%O"}}""" invId convId
        match WireCodec.tryDecodeCommand noMsgId with
        | Error msg -> Assert.Equal("message.delete: missing messageCommitId", msg)
        | Ok _ -> failwith "Expected error"

        // conversation.fork missing parentThreadId / editedMessage
        let noParent = sprintf """{"type":"conversation.fork","payload":{"invocationId":"%O","threadId":"%O","editedMessage":{"x":1}}}""" invId convId
        match WireCodec.tryDecodeCommand noParent with
        | Error msg -> Assert.Equal("conversation.fork: missing parentThreadId", msg)
        | Ok _ -> failwith "Expected error"

        let noEdit = sprintf """{"type":"conversation.fork","payload":{"invocationId":"%O","threadId":"%O","parentThreadId":"%O"}}""" invId convId convId
        match WireCodec.tryDecodeCommand noEdit with
        | Error msg -> Assert.Equal("conversation.fork: missing editedMessage", msg)
        | Ok _ -> failwith "Expected error"

    [<Fact>]
    let ``tryDecodeCommand fallback for missing optional fields`` () =
        // conversation.create without title or config
        let minCreate = sprintf """{"type":"conversation.create","payload":{"invocationId":"%O","threadId":"%O"}}""" invId convId
        match WireCodec.tryDecodeCommand minCreate with
        | Ok (CreateConversation d) ->
            Assert.Equal("", d.title)
            Assert.Equal("", d.config.provider)
            Assert.Equal("", d.config.model)
        | other -> failwithf "Unexpected: %A" other

        // conversation.rename without title
        let minRename = sprintf """{"type":"conversation.rename","payload":{"invocationId":"%O","threadId":"%O"}}""" invId convId
        match WireCodec.tryDecodeCommand minRename with
        | Ok (RenameConversation d) ->
            Assert.Equal("", d.title)
        | other -> failwithf "Unexpected: %A" other

        // conversation.flags-set without flags
        let minFlags = sprintf """{"type":"conversation.flags-set","payload":{"invocationId":"%O","threadId":"%O"}}""" invId convId
        match WireCodec.tryDecodeCommand minFlags with
        | Ok (SetConversationFlags d) ->
            Assert.False(d.pinned)
            Assert.False(d.archived)
        | other -> failwithf "Unexpected: %A" other

    // ==========================================
    // 3. WireEvent 全部 44 个变体编解码 Roundtrip
    // ==========================================

    [<Fact>]
    let ``WireEvent.Command throws InvalidOperationException on WireCodec.encode`` () =
        let cmd = RegenerateResponse {| invocationId = invId; conversationId = convId |}
        Assert.Throws<InvalidOperationException>(fun () -> WireCodec.encode (Command cmd) |> ignore)

    [<Fact>]
    let ``Handshake and Auth events roundtrip`` () =
        // Hello with Some instanceId and None
        let ev1 = Hello {| protocol = "wanxiang"; version = 1; instanceId = Some "inst-42" |}
        match WireCodec.tryDecode (WireCodec.encode ev1) with
        | Ok (Hello d) ->
            Assert.Equal("wanxiang", d.protocol)
            Assert.Equal(1, d.version)
            Assert.Equal(Some "inst-42", d.instanceId)
        | other -> failwithf "Unexpected: %A" other

        let ev1None = Hello {| protocol = "wanxiang"; version = 1; instanceId = None |}
        match WireCodec.tryDecode (WireCodec.encode ev1None) with
        | Ok (Hello d) ->
            Assert.Equal(None, d.instanceId)
        | other -> failwithf "Unexpected: %A" other

        // UpgradeRequired
        let ev2 = UpgradeRequired {| serverVersion = 2; clientVersion = 1 |}
        match WireCodec.tryDecode (WireCodec.encode ev2) with
        | Ok (UpgradeRequired d) ->
            Assert.Equal(2, d.serverVersion)
            Assert.Equal(1, d.clientVersion)
        | other -> failwithf "Unexpected: %A" other

        // AuthPresent
        let ev3 = AuthPresent {| token = "sec-token-123" |}
        match WireCodec.tryDecode (WireCodec.encode ev3) with
        | Ok (AuthPresent d) ->
            Assert.Equal("sec-token-123", d.token)
        | other -> failwithf "Unexpected: %A" other

        // AuthAccepted
        let ev4 = AuthAccepted {| instanceId = "inst-001" |}
        match WireCodec.tryDecode (WireCodec.encode ev4) with
        | Ok (AuthAccepted d) ->
            Assert.Equal("inst-001", d.instanceId)
        | other -> failwithf "Unexpected: %A" other

        // AuthRejected
        let ev5 = WireEvent.AuthRejected {| reason = "invalid token" |}
        match WireCodec.tryDecode (WireCodec.encode ev5) with
        | Ok (WireEvent.AuthRejected d) ->
            Assert.Equal("invalid token", d.reason)
        | other -> failwithf "Unexpected: %A" other

    [<Fact>]
    let ``Pairing events roundtrip`` () =
        // PairingRequested
        let pReq = PairingRequested {| clientName = Some "desktop-app" |}
        match WireCodec.tryDecode (WireCodec.encode pReq) with
        | Ok (PairingRequested d) -> Assert.Equal(Some "desktop-app", d.clientName)
        | other -> failwithf "Unexpected: %A" other

        let pReqNone = PairingRequested {| clientName = None |}
        match WireCodec.tryDecode (WireCodec.encode pReqNone) with
        | Ok (PairingRequested d) -> Assert.Equal(None, d.clientName)
        | other -> failwithf "Unexpected: %A" other

        // PairingStarted
        let pStart = PairingStarted {| expiresInSeconds = 120 |}
        match WireCodec.tryDecode (WireCodec.encode pStart) with
        | Ok (PairingStarted d) -> Assert.Equal(120, d.expiresInSeconds)
        | other -> failwithf "Unexpected: %A" other

        // PairingAttempted
        let pAtt = PairingAttempted {| code = "123456"; clientName = Some "web-app" |}
        match WireCodec.tryDecode (WireCodec.encode pAtt) with
        | Ok (PairingAttempted d) ->
            Assert.Equal("123456", d.code)
            Assert.Equal(Some "web-app", d.clientName)
        | other -> failwithf "Unexpected: %A" other

        // PairingSucceeded
        let pSucc = PairingSucceeded {| token = "tok-paired" |}
        match WireCodec.tryDecode (WireCodec.encode pSucc) with
        | Ok (PairingSucceeded d) -> Assert.Equal("tok-paired", d.token)
        | other -> failwithf "Unexpected: %A" other

        // PairingFailed (with frozen = true and false)
        let pFail1 = PairingFailed {| reason = "too many attempts"; frozen = true; freezeMinutes = 15 |}
        match WireCodec.tryDecode (WireCodec.encode pFail1) with
        | Ok (PairingFailed d) ->
            Assert.Equal("too many attempts", d.reason)
            Assert.True(d.frozen)
            Assert.Equal(15, d.freezeMinutes)
        | other -> failwithf "Unexpected: %A" other

        let pFail2 = PairingFailed {| reason = "wrong code"; frozen = false; freezeMinutes = 0 |}
        match WireCodec.tryDecode (WireCodec.encode pFail2) with
        | Ok (PairingFailed d) ->
            Assert.False(d.frozen)
        | other -> failwithf "Unexpected: %A" other

    [<Fact>]
    let ``Observation events roundtrip`` () =
        // ObserveConversationList & UnobserveConversationList
        match WireCodec.tryDecode (WireCodec.encode ObserveConversationList) with
        | Ok ObserveConversationList -> ()
        | other -> failwithf "Unexpected: %A" other

        match WireCodec.tryDecode (WireCodec.encode UnobserveConversationList) with
        | Ok UnobserveConversationList -> ()
        | other -> failwithf "Unexpected: %A" other

        // ConversationListSnapshot
        let items = JsonArray()
        items.Add(JsonObject())
        let listSnap = ConversationListSnapshot {| items = items; lastCommitId = 100UL |}
        match WireCodec.tryDecode (WireCodec.encode listSnap) with
        | Ok (ConversationListSnapshot d) ->
            Assert.Equal(100UL, d.lastCommitId)
            Assert.Equal(1, d.items.Count)
        | other -> failwithf "Unexpected: %A" other

        // ObserveConversation & UnobserveConversation
        match WireCodec.tryDecode (WireCodec.encode (ObserveConversation {| conversationId = convId |})) with
        | Ok (ObserveConversation d) -> Assert.Equal(convId, d.conversationId)
        | other -> failwithf "Unexpected: %A" other

        match WireCodec.tryDecode (WireCodec.encode (UnobserveConversation {| conversationId = convId |})) with
        | Ok (UnobserveConversation d) -> Assert.Equal(convId, d.conversationId)
        | other -> failwithf "Unexpected: %A" other

        // ConversationSnapshot
        let snap =
            ConversationSnapshot
                {| conversationId = convId
                   title = "会话快照"
                   lastCommitId = 50UL
                   runtimeState = "active"
                   generationId = Some genId
                   messages = JsonArray()
                   snapshotEarliestCommitId = 1UL
                   snapshotHasMore = true
                   config = sampleConfig () |}
        match WireCodec.tryDecode (WireCodec.encode snap) with
        | Ok (ConversationSnapshot d) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal("会话快照", d.title)
            Assert.Equal(50UL, d.lastCommitId)
            Assert.Equal("active", d.runtimeState)
            Assert.Equal(Some genId, d.generationId)
            Assert.True(d.snapshotHasMore)
            Assert.Equal("test-provider", d.config.provider)
        | other -> failwithf "Unexpected: %A" other

        // ConversationUpdated
        let changeObj = JsonObject()
        changeObj["pinned"] <- true
        let updated = ConversationUpdated {| conversationId = convId; commitId = 51UL; change = changeObj |}
        match WireCodec.tryDecode (WireCodec.encode updated) with
        | Ok (ConversationUpdated d) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(51UL, d.commitId)
            Assert.True(d.change["pinned"].GetValue<bool>())
        | other -> failwithf "Unexpected: %A" other

        // MessageCommitted
        let msgPayload = JsonObject()
        msgPayload["text"] <- "已提交消息"
        let msgComm =
            MessageCommitted
                {| conversationId = convId
                   commitId = 52UL
                   committedAt = fixedTime
                   payload = msgPayload |}
        match WireCodec.tryDecode (WireCodec.encode msgComm) with
        | Ok (MessageCommitted d) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(52UL, d.commitId)
            Assert.Equal(fixedTime.UtcDateTime, d.committedAt.UtcDateTime)
            Assert.Equal("已提交消息", d.payload["text"].GetValue<string>())
        | other -> failwithf "Unexpected: %A" other

    [<Fact>]
    let ``History and Export events roundtrip`` () =
        // HistoryRequest
        let hReq = HistoryRequest {| conversationId = convId; beforeCommitId = 100UL; limit = 20 |}
        match WireCodec.tryDecode (WireCodec.encode hReq) with
        | Ok (HistoryRequest d) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(100UL, d.beforeCommitId)
            Assert.Equal(20, d.limit)
        | other -> failwithf "Unexpected: %A" other

        // HistoryPage
        let hPage = HistoryPage {| conversationId = convId; beforeCommitId = 80UL; items = JsonArray(); hasMore = true |}
        match WireCodec.tryDecode (WireCodec.encode hPage) with
        | Ok (HistoryPage d) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(80UL, d.beforeCommitId)
            Assert.True(d.hasMore)
        | other -> failwithf "Unexpected: %A" other

        // ConversationExportRead
        let expRead =
            ConversationExportRead
                { exportId = expId
                  conversationId = convId
                  atCommitId = Some 90UL
                  beforeCommitId = 100UL }
        match WireCodec.tryDecode (WireCodec.encode expRead) with
        | Ok (ConversationExportRead d) ->
            Assert.Equal(expId, d.exportId)
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(Some 90UL, d.atCommitId)
            Assert.Equal(100UL, d.beforeCommitId)
        | other -> failwithf "Unexpected: %A" other

        // ConversationExportPage
        let expPage =
            ConversationExportPage
                { exportId = expId
                  conversationId = convId
                  atCommitId = 90UL
                  beforeCommitId = 100UL
                  title = "导出标题"
                  items = JsonArray()
                  totalMessages = 5
                  hasMore = false }
        match WireCodec.tryDecode (WireCodec.encode expPage) with
        | Ok (ConversationExportPage d) ->
            Assert.Equal(expId, d.exportId)
            Assert.Equal("导出标题", d.title)
            Assert.Equal(5, d.totalMessages)
            Assert.False(d.hasMore)
        | other -> failwithf "Unexpected: %A" other

        // ConversationExportFailed
        let expFail = ConversationExportFailed {| exportId = expId; message = "权限不足" |}
        match WireCodec.tryDecode (WireCodec.encode expFail) with
        | Ok (ConversationExportFailed d) ->
            Assert.Equal(expId, d.exportId)
            Assert.Equal("权限不足", d.message)
        | other -> failwithf "Unexpected: %A" other

    [<Fact>]
    let ``Command status and Authority events roundtrip`` () =
        // CommandAccepted
        let cAcc = CommandAccepted {| invocationId = invId |}
        match WireCodec.tryDecode (WireCodec.encode cAcc) with
        | Ok (CommandAccepted d) -> Assert.Equal(invId, d.invocationId)
        | other -> failwithf "Unexpected: %A" other

        // CommandCommitted
        let cComm = CommandCommitted {| invocationId = invId; commandId = "cmd-xyz"; commitId = 77UL |}
        match WireCodec.tryDecode (WireCodec.encode cComm) with
        | Ok (CommandCommitted d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal("cmd-xyz", d.commandId)
            Assert.Equal(77UL, d.commitId)
        | other -> failwithf "Unexpected: %A" other

        // CommandRejected (with Some and None requiredCommitId)
        let cRej1 = CommandRejected {| invocationId = invId; code = "CONFLICT"; message = "版本过旧"; requiredCommitId = Some 80UL |}
        match WireCodec.tryDecode (WireCodec.encode cRej1) with
        | Ok (CommandRejected d) ->
            Assert.Equal(invId, d.invocationId)
            Assert.Equal("CONFLICT", d.code)
            Assert.Equal("版本过旧", d.message)
            Assert.Equal(Some 80UL, d.requiredCommitId)
        | other -> failwithf "Unexpected: %A" other

        let cRej2 = CommandRejected {| invocationId = invId; code = "BAD_REQUEST"; message = "参数错误"; requiredCommitId = None |}
        match WireCodec.tryDecode (WireCodec.encode cRej2) with
        | Ok (CommandRejected d) ->
            Assert.Equal(None, d.requiredCommitId)
        | other -> failwithf "Unexpected: %A" other

        // CursorAdvanced
        let curAdv = CursorAdvanced {| id = 1001UL |}
        match WireCodec.tryDecode (WireCodec.encode curAdv) with
        | Ok (CursorAdvanced d) -> Assert.Equal(1001UL, d.id)
        | other -> failwithf "Unexpected: %A" other

        // AuthorityCatchUp
        let items = JsonArray()
        items.Add(JsonNode.Parse("""{"commitId":1}"""))
        let authCatch = AuthorityCatchUp {| fromCursor = 1UL; toCommitId = 2UL; items = items |}
        match WireCodec.tryDecode (WireCodec.encode authCatch) with
        | Ok (AuthorityCatchUp d) ->
            Assert.Equal(1UL, d.fromCursor)
            Assert.Equal(2UL, d.toCommitId)
            Assert.Equal(1, d.items.Count)
        | other -> failwithf "Unexpected: %A" other

    [<Fact>]
    let ``Generation stream events roundtrip with full error and usage models`` () =
        // GenerationDelta
        let deltaPayload = JsonObject()
        deltaPayload["text"] <- "流式增量"
        let genDelta = GenerationDelta {| conversationId = convId; generationId = genId; payload = deltaPayload |}
        match WireCodec.tryDecode (WireCodec.encode genDelta) with
        | Ok (GenerationDelta d) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(genId, d.generationId)
            Assert.Equal("流式增量", d.payload["text"].GetValue<string>())
        | other -> failwithf "Unexpected: %A" other

        // GenerationStarted
        let genStart = GenerationStarted {| conversationId = convId; generationId = genId; providerId = "anthropic"; model = "claude-3-5" |}
        match WireCodec.tryDecode (WireCodec.encode genStart) with
        | Ok (GenerationStarted d) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(genId, d.generationId)
            Assert.Equal("anthropic", d.providerId)
            Assert.Equal("claude-3-5", d.model)
        | other -> failwithf "Unexpected: %A" other

        // GenerationFinished with Success & Usage
        let usage = { promptTokens = Some 100; completionTokens = Some 50; cachedTokens = Some 20; totalTokens = Some 150; durationMs = Some 1200L }
        let genFin1 = GenerationFinished {| conversationId = convId; generationId = genId; status = "completed"; error = None; usage = Some usage |}
        match WireCodec.tryDecode (WireCodec.encode genFin1) with
        | Ok (GenerationFinished d) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(genId, d.generationId)
            Assert.Equal("completed", d.status)
            Assert.Equal(None, d.error)
            Assert.True(d.usage.IsSome)
            let u = d.usage.Value
            Assert.Equal(Some 100, u.promptTokens)
            Assert.Equal(Some 50, u.completionTokens)
            Assert.Equal(Some 150, u.totalTokens)
            Assert.Equal(Some 1200L, u.durationMs)
        | other -> failwithf "Unexpected: %A" other

        // GenerationFinished with Error (all GenerationErrorKind variants)
        let errorKinds =
            [ GenerationErrorKind.ProviderTimeout, "timeout"
              GenerationErrorKind.ProviderRateLimited, "rate_limit"
              GenerationErrorKind.ContextTooLong, "context_overflow"
              GenerationErrorKind.ContentFiltered, "content_policy"
              GenerationErrorKind.ProviderUnavailable, "upstream_unavailable"
              GenerationErrorKind.ModelNotFound, "model_missing"
              GenerationErrorKind.ProviderBadRequest, "invalid_payload"
              GenerationErrorKind.UnknownFailure, "generic_error" ]

        for (kind, kindStr) in errorKinds do
            let err =
                { kind = kind
                  message = sprintf "错误: %s" kindStr
                  detail = Some "堆栈详细信息"
                  retryable = true
                  retryAfterSeconds = Some 30 }
            let genFinErr = GenerationFinished {| conversationId = convId; generationId = genId; status = "failed"; error = Some err; usage = None |}
            match WireCodec.tryDecode (WireCodec.encode genFinErr) with
            | Ok (GenerationFinished d) ->
                Assert.Equal("failed", d.status)
                Assert.True(d.error.IsSome)
                let e = d.error.Value
                Assert.Equal(kind, e.kind)
                Assert.Equal(sprintf "错误: %s" kindStr, e.message)
                Assert.Equal(Some "堆栈详细信息", e.detail)
                Assert.True(e.retryable)
                Assert.Equal(Some 30, e.retryAfterSeconds)
            | other -> failwithf "Unexpected: %A" other

        // GenerationCancel
        let genCancel = GenerationCancel {| conversationId = convId; generationId = genId |}
        match WireCodec.tryDecode (WireCodec.encode genCancel) with
        | Ok (GenerationCancel d) ->
            Assert.Equal(convId, d.conversationId)
            Assert.Equal(genId, d.generationId)
        | other -> failwithf "Unexpected: %A" other

    [<Fact>]
    let ``Attachment events roundtrip`` () =
        let sha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

        // AttachmentBegin
        let aBegin = AttachmentBegin {| attachmentId = attId; totalBytes = 1024L; sha256 = sha; mediaType = "image/png"; fileName = "img.png" |}
        match WireCodec.tryDecode (WireCodec.encode aBegin) with
        | Ok (AttachmentBegin d) ->
            Assert.Equal(attId, d.attachmentId)
            Assert.Equal(1024L, d.totalBytes)
            Assert.Equal(sha, d.sha256)
            Assert.Equal("image/png", d.mediaType)
            Assert.Equal("img.png", d.fileName)
        | other -> failwithf "Unexpected: %A" other

        // AttachmentChunk
        let aChunk = AttachmentChunk {| attachmentId = attId; index = 1; dataBase64 = "AQID" |}
        match WireCodec.tryDecode (WireCodec.encode aChunk) with
        | Ok (AttachmentChunk d) ->
            Assert.Equal(attId, d.attachmentId)
            Assert.Equal(1, d.index)
            Assert.Equal("AQID", d.dataBase64)
        | other -> failwithf "Unexpected: %A" other

        // AttachmentComplete
        let aComp = AttachmentComplete {| attachmentId = attId; sha256 = sha |}
        match WireCodec.tryDecode (WireCodec.encode aComp) with
        | Ok (AttachmentComplete d) ->
            Assert.Equal(attId, d.attachmentId)
            Assert.Equal(sha, d.sha256)
        | other -> failwithf "Unexpected: %A" other

        // AttachmentCommitted
        let aComm = AttachmentCommitted {| attachmentId = attId; sha256 = sha; size = 1024L |}
        match WireCodec.tryDecode (WireCodec.encode aComm) with
        | Ok (AttachmentCommitted d) ->
            Assert.Equal(attId, d.attachmentId)
            Assert.Equal(sha, d.sha256)
            Assert.Equal(1024L, d.size)
        | other -> failwithf "Unexpected: %A" other

        // AttachmentAborted
        let aAbort = AttachmentAborted {| attachmentId = attId; reason = "user cancelled" |}
        match WireCodec.tryDecode (WireCodec.encode aAbort) with
        | Ok (AttachmentAborted d) ->
            Assert.Equal(attId, d.attachmentId)
            Assert.Equal("user cancelled", d.reason)
        | other -> failwithf "Unexpected: %A" other

        // AttachmentDownloadRequest
        let dReq = AttachmentDownloadRequest {| sha256 = sha |}
        match WireCodec.tryDecode (WireCodec.encode dReq) with
        | Ok (AttachmentDownloadRequest d) -> Assert.Equal(sha, d.sha256)
        | other -> failwithf "Unexpected: %A" other

        // AttachmentDownloadBegin
        let dBegin = AttachmentDownloadBegin {| sha256 = sha; size = 2048L; mediaType = "text/plain"; fileName = "doc.txt" |}
        match WireCodec.tryDecode (WireCodec.encode dBegin) with
        | Ok (AttachmentDownloadBegin d) ->
            Assert.Equal(sha, d.sha256)
            Assert.Equal(2048L, d.size)
            Assert.Equal("text/plain", d.mediaType)
            Assert.Equal("doc.txt", d.fileName)
        | other -> failwithf "Unexpected: %A" other

        // AttachmentDownloadChunk
        let dChunk = AttachmentDownloadChunk {| sha256 = sha; index = 0; dataBase64 = "BAUG" |}
        match WireCodec.tryDecode (WireCodec.encode dChunk) with
        | Ok (AttachmentDownloadChunk d) ->
            Assert.Equal(sha, d.sha256)
            Assert.Equal(0, d.index)
            Assert.Equal("BAUG", d.dataBase64)
        | other -> failwithf "Unexpected: %A" other

        // AttachmentDownloadComplete
        let dComp = AttachmentDownloadComplete {| sha256 = sha |}
        match WireCodec.tryDecode (WireCodec.encode dComp) with
        | Ok (AttachmentDownloadComplete d) -> Assert.Equal(sha, d.sha256)
        | other -> failwithf "Unexpected: %A" other

    [<Fact>]
    let ``Catalog, Provider Probe, and Config events roundtrip`` () =
        // CatalogRequest & CatalogSnapshot
        match WireCodec.tryDecode (WireCodec.encode CatalogRequest) with
        | Ok CatalogRequest -> ()
        | other -> failwithf "Unexpected: %A" other

        let catSnap = CatalogSnapshot {| providers = JsonArray(); tools = JsonArray(); generation = JsonObject() |}
        match WireCodec.tryDecode (WireCodec.encode catSnap) with
        | Ok (CatalogSnapshot d) ->
            Assert.Empty(d.providers)
            Assert.Empty(d.tools)
        | other -> failwithf "Unexpected: %A" other

        // ProviderProbeRequest
        let pReq = ProviderProbeRequest {| requestId = reqId; providerId = "openai" |}
        match WireCodec.tryDecode (WireCodec.encode pReq) with
        | Ok (ProviderProbeRequest d) ->
            Assert.Equal(reqId, d.requestId)
            Assert.Equal("openai", d.providerId)
        | other -> failwithf "Unexpected: %A" other

        // ProviderProbeResult (success and failure)
        let models = JsonArray()
        models.Add("gpt-4o")
        let pRes1 = ProviderProbeResult {| requestId = reqId; providerId = "openai"; ok = true; models = models; error = None |}
        match WireCodec.tryDecode (WireCodec.encode pRes1) with
        | Ok (ProviderProbeResult d) ->
            Assert.True(d.ok)
            Assert.Equal(1, d.models.Count)
            Assert.Equal(None, d.error)
        | other -> failwithf "Unexpected: %A" other

        let pRes2 = ProviderProbeResult {| requestId = reqId; providerId = "openai"; ok = false; models = JsonArray(); error = Some "认证失败" |}
        match WireCodec.tryDecode (WireCodec.encode pRes2) with
        | Ok (ProviderProbeResult d) ->
            Assert.False(d.ok)
            Assert.Equal(Some "认证失败", d.error)
        | other -> failwithf "Unexpected: %A" other

        // ConfigUpsertProvider & ConfigDeleteProvider
        let provObj = JsonObject()
        provObj["id"] <- "prov-1"
        let cuProv = ConfigUpsertProvider {| requestId = reqId; provider = provObj |}
        match WireCodec.tryDecode (WireCodec.encode cuProv) with
        | Ok (ConfigUpsertProvider d) ->
            Assert.Equal(reqId, d.requestId)
            Assert.Equal("prov-1", d.provider["id"].GetValue<string>())
        | other -> failwithf "Unexpected: %A" other

        let cdProv = ConfigDeleteProvider {| requestId = reqId; providerId = "prov-1" |}
        match WireCodec.tryDecode (WireCodec.encode cdProv) with
        | Ok (ConfigDeleteProvider d) ->
            Assert.Equal(reqId, d.requestId)
            Assert.Equal("prov-1", d.providerId)
        | other -> failwithf "Unexpected: %A" other

        // ConfigUpsertMcp & ConfigDeleteMcp
        let mcpObj = JsonObject()
        mcpObj["name"] <- "filesystem"
        let cuMcp = ConfigUpsertMcp {| requestId = reqId; server = mcpObj |}
        match WireCodec.tryDecode (WireCodec.encode cuMcp) with
        | Ok (ConfigUpsertMcp d) ->
            Assert.Equal(reqId, d.requestId)
            Assert.Equal("filesystem", d.server["name"].GetValue<string>())
        | other -> failwithf "Unexpected: %A" other

        let cdMcp = ConfigDeleteMcp {| requestId = reqId; serverId = "mcp-fs" |}
        match WireCodec.tryDecode (WireCodec.encode cdMcp) with
        | Ok (ConfigDeleteMcp d) ->
            Assert.Equal(reqId, d.requestId)
            Assert.Equal("mcp-fs", d.serverId)
        | other -> failwithf "Unexpected: %A" other

        // ConfigUpdateGeneration
        let genObj = JsonObject()
        genObj["temperature"] <- 0.7
        let cuGen = ConfigUpdateGeneration {| requestId = reqId; generation = genObj |}
        match WireCodec.tryDecode (WireCodec.encode cuGen) with
        | Ok (ConfigUpdateGeneration d) ->
            Assert.Equal(reqId, d.requestId)
            Assert.Equal(0.7, d.generation["temperature"].GetValue<double>())
        | other -> failwithf "Unexpected: %A" other

        // ConfigApplied
        let cApp = ConfigApplied {| requestId = reqId; ok = true; errors = [ "warning: deprecated field" ] |}
        match WireCodec.tryDecode (WireCodec.encode cApp) with
        | Ok (ConfigApplied d) ->
            Assert.Equal(reqId, d.requestId)
            Assert.True(d.ok)
            Assert.Single(d.errors) |> ignore
        | other -> failwithf "Unexpected: %A" other

        // ConfigChanged & ServerError & Ping & Pong
        let cChanged = ConfigChanged {| reason = "hot reload" |}
        match WireCodec.tryDecode (WireCodec.encode cChanged) with
        | Ok (ConfigChanged d) -> Assert.Equal("hot reload", d.reason)
        | other -> failwithf "Unexpected: %A" other

        let sErr = ServerError {| message = "内部崩溃" |}
        match WireCodec.tryDecode (WireCodec.encode sErr) with
        | Ok (ServerError d) -> Assert.Equal("内部崩溃", d.message)
        | other -> failwithf "Unexpected: %A" other

        match WireCodec.tryDecode (WireCodec.encode Ping) with
        | Ok Ping -> ()
        | other -> failwithf "Unexpected: %A" other

        match WireCodec.tryDecode (WireCodec.encode Pong) with
        | Ok Pong -> ()
        | other -> failwithf "Unexpected: %A" other

    // ==========================================
    // 4. WireEvent 解码边界与错误分支
    // ==========================================

    [<Fact>]
    let ``tryDecode handles malformed JSON and non-object roots`` () =
        match WireCodec.tryDecode "{ incomplete json" with
        | Error msg -> Assert.Contains("event decode failed", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode "42" with
        | Error msg -> Assert.Equal("not a JSON object", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode "[1, 2]" with
        | Error msg -> Assert.Equal("not a JSON object", msg)
        | Ok _ -> failwith "Expected error"

    [<Fact>]
    let ``tryDecode handles missing or unknown event type`` () =
        match WireCodec.tryDecode "{}" with
        | Error msg -> Assert.Equal("missing type", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type": 123}""" with
        | Error msg -> Assert.Equal("missing type", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type": "non.existent.event"}""" with
        | Error msg -> Assert.Equal("unknown event type non.existent.event", msg)
        | Ok _ -> failwith "Expected error"

    [<Fact>]
    let ``tryDecode handles missing required fields for events`` () =
        // conversation.observe missing threadId
        match WireCodec.tryDecode """{"type":"conversation.observe","payload":{}}""" with
        | Error msg -> Assert.Equal("conversation.observe: missing threadId", msg)
        | Ok _ -> failwith "Expected error"

        // conversation.unobserve missing threadId
        match WireCodec.tryDecode """{"type":"conversation.unobserve","payload":{}}""" with
        | Error msg -> Assert.Equal("conversation.unobserve: missing threadId", msg)
        | Ok _ -> failwith "Expected error"

        // conversation.snapshot missing threadId
        match WireCodec.tryDecode """{"type":"conversation.snapshot","payload":{}}""" with
        | Error msg -> Assert.Equal("conversation.snapshot: missing threadId", msg)
        | Ok _ -> failwith "Expected error"

        // conversation.updated missing threadId
        match WireCodec.tryDecode """{"type":"conversation.updated","payload":{}}""" with
        | Error msg -> Assert.Equal("conversation.updated: missing threadId", msg)
        | Ok _ -> failwith "Expected error"

        // conversation.message-committed missing threadId or payload
        match WireCodec.tryDecode """{"type":"conversation.message-committed","payload":{}}""" with
        | Error msg -> Assert.Equal("conversation.message-committed: missing threadId", msg)
        | Ok _ -> failwith "Expected error"

        let noPayloadMsg = sprintf """{"type":"conversation.message-committed","payload":{"threadId":"%O"}}""" convId
        match WireCodec.tryDecode noPayloadMsg with
        | Error msg -> Assert.Equal("conversation.message-committed: missing payload", msg)
        | Ok _ -> failwith "Expected error"

        // history.request & history.page missing threadId
        match WireCodec.tryDecode """{"type":"history.request","payload":{}}""" with
        | Error msg -> Assert.Equal("history.request: missing threadId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"history.page","payload":{}}""" with
        | Error msg -> Assert.Equal("history.page: missing threadId", msg)
        | Ok _ -> failwith "Expected error"

        // command.accepted / committed / rejected missing invocationId
        match WireCodec.tryDecode """{"type":"command.accepted","payload":{}}""" with
        | Error msg -> Assert.Equal("command.accepted: missing invocationId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"command.committed","payload":{}}""" with
        | Error msg -> Assert.Equal("command.committed: missing invocationId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"command.rejected","payload":{}}""" with
        | Error msg -> Assert.Equal("command.rejected: missing invocationId", msg)
        | Ok _ -> failwith "Expected error"

        // generation.delta missing threadId/runId or content
        match WireCodec.tryDecode """{"type":"generation.delta","payload":{}}""" with
        | Error msg -> Assert.Equal("generation.delta: missing threadId/runId", msg)
        | Ok _ -> failwith "Expected error"

        let noContentDelta = sprintf """{"type":"generation.delta","payload":{"threadId":"%O","runId":"%O"}}""" convId genId
        match WireCodec.tryDecode noContentDelta with
        | Error msg -> Assert.Equal("generation.delta: missing payload", msg)
        | Ok _ -> failwith "Expected error"

        // generation.started / finished / cancel missing threadId/runId
        match WireCodec.tryDecode """{"type":"generation.started","payload":{}}""" with
        | Error msg -> Assert.Equal("generation.started: missing threadId/runId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"generation.finished","payload":{}}""" with
        | Error msg -> Assert.Equal("generation.finished: missing threadId/runId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"generation.cancel","payload":{}}""" with
        | Error msg -> Assert.Equal("generation.cancel: missing threadId/runId", msg)
        | Ok _ -> failwith "Expected error"

        // attachments missing attachmentId
        match WireCodec.tryDecode """{"type":"attachment.begin","payload":{}}""" with
        | Error msg -> Assert.Equal("attachment.begin: missing attachmentId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"attachment.chunk","payload":{}}""" with
        | Error msg -> Assert.Equal("attachment.chunk: missing attachmentId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"attachment.complete","payload":{}}""" with
        | Error msg -> Assert.Equal("attachment.complete: missing attachmentId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"attachment.committed","payload":{}}""" with
        | Error msg -> Assert.Equal("attachment.committed: missing attachmentId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"attachment.aborted","payload":{}}""" with
        | Error msg -> Assert.Equal("attachment.aborted: missing attachmentId", msg)
        | Ok _ -> failwith "Expected error"

        // provider & config requests missing requestId
        match WireCodec.tryDecode """{"type":"provider.probe","payload":{}}""" with
        | Error msg -> Assert.Equal("provider.probe: missing requestId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"provider.probe-result","payload":{}}""" with
        | Error msg -> Assert.Equal("provider.probe-result: missing requestId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"config.provider-upsert","payload":{}}""" with
        | Error msg -> Assert.Equal("config.provider-upsert: missing requestId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"config.provider-delete","payload":{}}""" with
        | Error msg -> Assert.Equal("config.provider-delete: missing requestId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"config.mcp-upsert","payload":{}}""" with
        | Error msg -> Assert.Equal("config.mcp-upsert: missing requestId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"config.mcp-delete","payload":{}}""" with
        | Error msg -> Assert.Equal("config.mcp-delete: missing requestId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"config.generation-update","payload":{}}""" with
        | Error msg -> Assert.Equal("config.generation-update: missing requestId", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"config.applied","payload":{}}""" with
        | Error msg -> Assert.Equal("config.applied: missing requestId", msg)
        | Ok _ -> failwith "Expected error"

        // export errors
        match WireCodec.tryDecode """{"type":"conversation.export-read","payload":{}}""" with
        | Error msg -> Assert.Equal("conversation.export-read: invalid identity or boundary", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"conversation.export-page","payload":{}}""" with
        | Error msg -> Assert.Equal("conversation.export-page: incomplete or invalid page", msg)
        | Ok _ -> failwith "Expected error"

        match WireCodec.tryDecode """{"type":"conversation.export-failed","payload":{}}""" with
        | Error msg -> Assert.Equal("conversation.export-failed: missing exportId/message", msg)
        | Ok _ -> failwith "Expected error"

    // ==========================================
    // 5. 内部 Helper 辅助函数边界与容错测试
    // ==========================================

    [<Fact>]
    let ``tryDecode defaults optional fields correctly when missing`` () =
        // protocol.hello with minimal payload
        match WireCodec.tryDecode """{"type":"protocol.hello","payload":{}}""" with
        | Ok (Hello d) ->
            Assert.Equal("", d.protocol)
            Assert.Equal(0, d.version)
            Assert.Equal(None, d.instanceId)
        | other -> failwithf "Unexpected: %A" other

        // pairing.started defaults expiresInSeconds to 300
        match WireCodec.tryDecode """{"type":"pairing.started","payload":{}}""" with
        | Ok (PairingStarted d) -> Assert.Equal(300, d.expiresInSeconds)
        | other -> failwithf "Unexpected: %A" other

        // pairing.failed defaults freezeMinutes to 0 and frozen to false
        match WireCodec.tryDecode """{"type":"pairing.failed","payload":{}}""" with
        | Ok (PairingFailed d) ->
            Assert.False(d.frozen)
            Assert.Equal(0, d.freezeMinutes)
        | other -> failwithf "Unexpected: %A" other

        // conversation.snapshot defaults
        let minSnap = sprintf """{"type":"conversation.snapshot","payload":{"threadId":"%O"}}""" convId
        match WireCodec.tryDecode minSnap with
        | Ok (ConversationSnapshot d) ->
            Assert.Equal("", d.title)
            Assert.Equal(0UL, d.lastCommitId)
            Assert.Equal("idle", d.runtimeState)
            Assert.Equal(None, d.generationId)
            Assert.False(d.snapshotHasMore)
            Assert.Empty(d.messages)
        | other -> failwithf "Unexpected: %A" other

        // generation.finished with unknown error kind falls back to Unknown
        let unkErrJson =
            sprintf """{"type":"generation.finished","payload":{"threadId":"%O","runId":"%O","status":"failed","error":{"kind":"alien_error","message":"unknown"}}}"""
                convId genId
        match WireCodec.tryDecode unkErrJson with
        | Ok (GenerationFinished d) ->
            Assert.True(d.error.IsSome)
            Assert.Equal(GenerationErrorKind.UnknownFailure, d.error.Value.kind)
        | other -> failwithf "Unexpected: %A" other

        // generation.finished with usage string numbers or missing usage
        let missingUsageJson =
            sprintf """{"type":"generation.finished","payload":{"threadId":"%O","runId":"%O","status":"completed"}}""" convId genId
        match WireCodec.tryDecode missingUsageJson with
        | Ok (GenerationFinished d) ->
            Assert.Equal(None, d.usage)
        | other -> failwithf "Unexpected: %A" other
