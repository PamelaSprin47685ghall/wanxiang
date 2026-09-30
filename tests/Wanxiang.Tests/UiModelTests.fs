namespace Wanxiang.Tests

open System
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core
open Wanxiang.UI

module UiModelTests =

    // ==========================================
    // 1. AttachmentRef Tests
    // ==========================================

    [<Fact>]
    let ``AttachmentRef detects image mime types and formats file sizes`` () =
        let imgAtt: AttachmentRef = {
            sha256 = "dummy-sha"
            fileName = "screenshot.PNG"
            size = 1048576L
            mediaType = "image/png"
        }
        Assert.True(AttachmentRef.isImage imgAtt)
        Assert.Equal("1.0 MiB", AttachmentRef.formatSize imgAtt.size)

        let textAtt: AttachmentRef = {
            sha256 = "dummy-sha-2"
            fileName = "notes.txt"
            size = 512L
            mediaType = "text/plain"
        }
        Assert.False(AttachmentRef.isImage textAtt)
        Assert.Equal("512 B", AttachmentRef.formatSize textAtt.size)

        let kbAtt: AttachmentRef = {
            sha256 = "dummy-sha-3"
            fileName = "doc.pdf"
            size = 2048L
            mediaType = "application/pdf"
        }
        Assert.Equal("2.0 KiB", AttachmentRef.formatSize kbAtt.size)

    // ==========================================
    // 2. MessageView Tests
    // ==========================================

    [<Fact>]
    let ``MessageView ofJson parses user and assistant roles, reasoning, and tools`` () =
        let userJson = JsonObject()
        userJson["role"] <- "user"
        let userContents = JsonArray()
        let textNode = JsonObject()
        textNode["type"] <- "text"
        textNode["text"] <- "Hello Wanxiang"
        userContents.Add textNode
        userJson["contents"] <- userContents

        let userView = MessageView.ofJson userJson
        Assert.True(MessageView.isUser userView)
        Assert.Equal("Hello Wanxiang", userView.text)
        Assert.Empty(userView.reasoning)
        Assert.Empty(userView.toolCalls)

        let assistantJson = JsonObject()
        assistantJson["role"] <- "assistant"
        let asstContents = JsonArray()
        let reasonNode = JsonObject()
        reasonNode["type"] <- "reasoning"
        reasonNode["text"] <- "Let me think..."
        asstContents.Add reasonNode

        let toolNode = JsonObject()
        toolNode["type"] <- "functionCall"
        toolNode["callId"] <- "call-1"
        toolNode["name"] <- "read_file"
        let argsObj = JsonObject()
        argsObj["path"] <- "test.txt"
        toolNode["arguments"] <- argsObj
        asstContents.Add toolNode

        let asstText = JsonObject()
        asstText["text"] <- "I am an AI assistant"
        asstContents.Add asstText
        assistantJson["contents"] <- asstContents

        let assistantView = MessageView.ofJson assistantJson
        Assert.False(MessageView.isUser assistantView)
        Assert.Equal("I am an AI assistant", assistantView.text)
        Assert.Equal("Let me think...", assistantView.reasoning)
        Assert.Single(assistantView.toolCalls) |> ignore
        Assert.Equal("read_file", assistantView.toolCalls.[0].name)
        Assert.Equal("call-1", assistantView.toolCalls.[0].callId)

    [<Fact>]
    let ``MessageView mergeToolResults correctly associates results and removes tool messages`` () =
        let assistantView = {
            MessageView.empty with
                role = "assistant"
                text = "Checking directory..."
                toolCalls = [
                    { callId = "call-dir"; name = "list_dir"; argumentsJson = "{}"; result = None }
                ]
        }

        let toolView = {
            MessageView.empty with
                role = "tool"
                toolResults = [ ("call-dir", "file1.fs, file2.fs") ]
        }

        let merged = MessageView.mergeToolResults [ assistantView; toolView ]
        Assert.Single(merged) |> ignore
        let finalAssistant = merged.[0]
        Assert.Single(finalAssistant.toolCalls) |> ignore
        Assert.Equal(Some "file1.fs, file2.fs", finalAssistant.toolCalls.[0].result)

    // ==========================================
    // 3. ConversationSummary Tests
    // ==========================================

    [<Fact>]
    let ``ConversationSummary parseList handles valid, empty and invalid records`` () =
        let arr = JsonArray()
        let cid1 = Guid.NewGuid()
        let cid2 = Guid.NewGuid()

        let item1 = JsonObject()
        item1["conversationId"] <- cid1.ToString("D")
        item1["title"] <- "Valid Title"
        item1["createdAt"] <- "2026-09-30T10:00:00Z"
        item1["updatedAt"] <- "2026-09-30T10:05:00Z"
        item1["archived"] <- false
        item1["pinned"] <- true
        item1["lastCommitId"] <- 15UL
        arr.Add item1

        let item2 = JsonObject()
        item2["conversationId"] <- cid2.ToString("D")
        item2["title"] <- "Bad Date"
        item2["createdAt"] <- "not-a-date"
        item2["updatedAt"] <- "invalid"
        item2["lastCommitId"] <- 0UL
        arr.Add item2

        let item3 = JsonObject()
        item3["conversationId"] <- "not-a-valid-guid"
        arr.Add item3

        let parsed = ConversationSummary.parseList arr
        // Invalid GUID item is dropped
        Assert.Equal(2, parsed.Length)
        Assert.Equal(cid1, parsed.[0].id)
        Assert.True(parsed.[0].pinned)
        Assert.Equal(DateTimeOffset.UnixEpoch, parsed.[1].createdAt)

    [<Fact>]
    let ``ConversationSummary matches searches single and multi-word keywords`` () =
        let s = {
            id = Guid.NewGuid()
            title = "Refactoring Wanxiang UI ViewModel Architecture"
            preview = "latest text"
            running = false
            createdAt = DateTimeOffset.UtcNow
            updatedAt = DateTimeOffset.UtcNow
            messageCount = 5
            isFork = false
            providerId = "test"
            model = "test-model"
            archived = false
            pinned = false
            lastCommitId = 1UL
        }

        Assert.True(ConversationSummary.matches "Wanxiang" s)
        Assert.True(ConversationSummary.matches "ui refactoring" s)
        Assert.False(ConversationSummary.matches "unrelated keyword" s)
