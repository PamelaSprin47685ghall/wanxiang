namespace Wanxiang.Tests

open System
open Xunit
open Wanxiang.Core
open Wanxiang.UI

module UiControllersTests =

    // ==========================================
    // 1. ConversationRuns Tests
    // ==========================================

    [<Fact>]
    let ``ConversationRuns initial state is empty and idle`` () =
        let runs = ConversationRuns()
        let r = runs.Get(None)
        Assert.False(r.running)
        Assert.True(Option.isNone r.generationId)
        Assert.True(Option.isNone r.message)
        Assert.True(Option.isNone r.error)

    [<Fact>]
    let ``ConversationRuns start transitions to generating and resets message`` () =
        let runs = ConversationRuns()
        let convId = Guid.NewGuid()
        let genId = Guid.NewGuid()

        runs.Start(convId, genId)
        let r = runs.Get(Some convId)
        Assert.True(r.running)
        Assert.Equal(Some genId, r.generationId)
        Assert.True(Option.isNone r.message)
        Assert.True(Option.isNone r.error)

    [<Fact>]
    let ``ConversationRuns delta updates streaming message`` () =
        let runs = ConversationRuns()
        let convId = Guid.NewGuid()
        let genId = Guid.NewGuid()
        runs.Start(convId, genId)

        let msg = { MessageView.empty with role = "assistant"; text = "Hello " }
        let ok = runs.Delta(convId, genId, msg)
        Assert.True(ok)
        Assert.Equal("Hello ", runs.Get(Some convId).message.Value.text)

        let msg2 = { msg with text = "Hello world!" }
        let ok2 = runs.Delta(convId, genId, msg2)
        Assert.True(ok2)
        Assert.Equal("Hello world!", runs.Get(Some convId).message.Value.text)

    [<Fact>]
    let ``ConversationRuns finish without error archives entry to retired ledger with capacity limit`` () =
        let runs = ConversationRuns()
        let convId = Guid.NewGuid()
        let genId = Guid.NewGuid()
        runs.Start(convId, genId)
        let ok = runs.Finish(convId, genId, None, None)
        Assert.True(ok)

        let r = runs.Get(Some convId)
        Assert.False(r.running)
        Assert.True(Option.isNone r.generationId)
        Assert.True(Option.isNone r.message)

        // Verifying retirement FIFO queue bounds (capacity = 1024)
        for _ in 1 .. 1050 do
            let gid = Guid.NewGuid()
            runs.Start(convId, gid)
            runs.Finish(convId, gid, None, None) |> ignore

        // The ledger should strictly not exceed 1024 entries
        Assert.Equal(1024, runs.RetiredCount)

    [<Fact>]
    let ``ConversationRuns finish with error records error in state`` () =
        let runs = ConversationRuns()
        let convId = Guid.NewGuid()
        let genId = Guid.NewGuid()
        runs.Start(convId, genId)

        let genErr = {
            kind = GenerationErrorKind.ProviderTimeout
            message = "Network timeout"
            detail = None
            retryable = true
            retryAfterSeconds = Some 5
        }
        runs.Finish(convId, genId, Some genErr, None) |> ignore

        let r = runs.Get(Some convId)
        Assert.False(r.running)
        Assert.True(r.error.IsSome)
        Assert.Equal("Network timeout", r.error.Value.message)

        runs.ClearError(convId)
        Assert.True(Option.isNone (runs.Get(Some convId).error))

    [<Fact>]
    let ``ConversationRuns disconnect clears in-flight run but preserves retired ledger`` () =
        let runs = ConversationRuns()
        let convId = Guid.NewGuid()
        let genId = Guid.NewGuid()
        runs.Start(convId, genId)
        runs.Disconnect()

        let r = runs.Get(Some convId)
        Assert.False(r.running)
        Assert.True(Option.isNone r.generationId)

    // ==========================================
    // 2. AttachmentDraftController Tests
    // ==========================================

    [<Fact>]
    let ``AttachmentDraftController begin draft, complete and remove lifecycle`` () =
        let controller = AttachmentDraftController()
        let attId = Guid.NewGuid()
        let upload: AttachmentUpload = {
            attachmentId = attId
            fileName = "file1.txt"
            mediaType = "text/plain"
            size = 100L
            sha256 = "dummy-sha256"
        }

        controller.Begin upload |> ignore

        Assert.Single(controller.Items) |> ignore
        Assert.False(controller.Items.[0].ready)
        Assert.Equal("file1.txt", controller.Items.[0].fileName)
        Assert.True(controller.HasUploading)

        let completed = controller.Complete(attId, 100L)
        Assert.True(completed.IsSome)
        Assert.True(controller.Items.[0].ready)
        Assert.True(controller.HasReady)

        controller.Remove(attId) |> ignore
        Assert.Empty(controller.Items)
