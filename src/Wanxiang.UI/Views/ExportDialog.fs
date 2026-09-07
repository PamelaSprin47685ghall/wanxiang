namespace Wanxiang.UI

open System
open System.Text
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Threading
open Wanxiang.Protocol

/// 导出只使用自己的分页状态；获取完整内容后由用户再次点击保存，保留浏览器用户手势。
type ConversationExportDialog(
    overlay: OverlayHost,
    conversationId: Guid,
    initialTitle: string,
    sendQuery: ConversationExportQuery -> Task<bool>,
    topLevel: unit -> TopLevel) as this =

    let controller = ConversationExportController(conversationId)
    let status = TextBlock(TextWrapping = TextWrapping.Wrap, FontSize = Tokens.fontBody, Foreground = Tokens.text)
    let progress = ProgressBar(IsIndeterminate = true, Minimum = 0.0, Maximum = 100.0)
    let mutable active = false
    let mutable saving = false
    let mutable saved = false
    let mutable saveError: string option = None
    let mutable output: byte[] option = None
    let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds 1.0)
    do
        timer.Tick.Add(fun _ ->
            if controller.Expire(DateTimeOffset.UtcNow, TimeSpan.FromSeconds 30.0) then this.Refresh())
    let closeButton = Ui.button Ui.Ghost "取消导出" (fun () -> overlay.CloseDialog())
    let retryButton = Ui.button Ui.Secondary "重新导出" (fun () -> this.Start())
    let saveButton = Ui.button Ui.Primary "保存 Markdown" (fun () -> this.Save())

    member private _.Refresh() =
        let reading, ready, failed =
            match controller.Status with
            | ExportReading(received, total) ->
                status.Text <- match total with None -> "正在读取完整历史…" | Some total -> sprintf "已读取 %d / %d 条消息…" received total
                progress.IsIndeterminate <- total.IsNone
                progress.Value <- total |> Option.filter ((<) 0) |> Option.map (fun total -> float received / float total * 100.0) |> Option.defaultValue 0.0
                true, false, false
            | ExportReady count ->
                status.Text <-
                    if saving then "正在保存…"
                    elif saved then sprintf "已保存完整记录（%d 条消息）。" count
                    else saveError |> Option.defaultValue (sprintf "已完整读取 %d 条消息，可以保存。" count)
                false, output.IsSome, output.IsNone
            | ExportFailed message -> status.Text <- message; false, false, true
            | ExportCancelled -> status.Text <- "导出已取消。"; false, false, false
        progress.IsVisible <- reading
        retryButton.IsVisible <- failed
        saveButton.IsVisible <- ready && not saved
        Ui.setEnabled saveButton (ready && not saving)
        Ui.setButtonText closeButton (if reading then "取消导出" else "关闭")

    member private this.Dispatch(query: ConversationExportQuery) =
        let sending = sendQuery query
        async {
            try
                let! sent = sending |> Async.AwaitTask
                if not sent then
                    Dispatcher.UIThread.Post(fun () -> this.Fail(query.exportId, "连接已断开，未完成导出；请重新连接后重试。"))
            with ex -> Dispatcher.UIThread.Post(fun () -> this.Fail(query.exportId, "读取历史失败：" + ex.Message))
        } |> Async.Start

    member private this.Start() =
        output <- None
        saveError <- None
        saved <- false
        let query = controller.Start()
        this.Refresh()
        this.Dispatch query

    member _.IsOpen = active

    member this.Show() =
        let buttons = Ui.hstack Tokens.space2 [ closeButton :> Control; retryButton :> Control; saveButton :> Control ]
        buttons.HorizontalAlignment <- HorizontalAlignment.Right
        let content = Ui.vstack Tokens.space3
                          [ Ui.title "导出会话" :> Control; Ui.caption initialTitle :> Control
                            status :> Control; progress :> Control
                            Ui.caption "导出开始时已保存的消息、工具记录和附件说明。未完成的生成、排队消息及附件文件本体不包含在内；这不是完整备份。" :> Control
                            buttons :> Control ]
        active <- true
        overlay.ShowDialog(content :> Control, 480.0, onClosed = (fun () ->
            active <- false
            timer.Stop()
            controller.Cancel()
            output <- None))
        timer.Start()
        this.Start()

    member this.Handle(page: ConversationExportPageData) =
        if active then
            let next = controller.Accept page
            match controller.Document, output with
            | Some document, None ->
                try
                    let markdown = Export.toMarkdown document.title document.messages
                    if int64 (Encoding.UTF8.GetByteCount markdown) > ConversationExportLimits.maxTranscriptBytes then
                        saveError <- Some "生成的 Markdown 超过 64 MiB 安全上限；没有保存截断的文件。"
                    else output <- Some(Encoding.UTF8.GetBytes markdown)
                with ex -> saveError <- Some("无法生成完整 Markdown：" + ex.Message)
            | _ -> ()
            this.Refresh()
            next |> Option.iter this.Dispatch

    member this.Fail(exportId: Guid, message: string) =
        if active then
            controller.Fail(exportId, message)
            this.Refresh()

    member this.Disconnect() =
        if active then
            controller.Disconnect()
            this.Refresh()

    member private this.Save() =
        match controller.Document, output with
        | Some document, Some bytes when not saving ->
            let top = topLevel ()
            if isNull top then
                saveError <- Some "当前窗口不支持保存文件。"
                this.Refresh()
            else
                try
                    // 文件选择器在点击回调内立即调用，不在网络／异步操作之后补开。
                    let picking = top.StorageProvider.SaveFilePickerAsync(FilePickerSaveOptions(SuggestedFileName = Export.safeFileName document.title))
                    saving <- true
                    saveError <- None
                    this.Refresh()
                    async {
                        try
                            let! file = picking |> Async.AwaitTask
                            if not (isNull file) then
                                use file = file
                                use! stream = file.OpenWriteAsync() |> Async.AwaitTask
                                if stream.CanSeek then stream.SetLength 0L
                                do! stream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                                do! stream.FlushAsync() |> Async.AwaitTask
                            Dispatcher.UIThread.Post(fun () ->
                                if active then
                                    saving <- false
                                    saved <- not (isNull file)
                                    this.Refresh())
                        with ex ->
                            Dispatcher.UIThread.Post(fun () ->
                                if active then
                                    saving <- false
                                    saveError <- Some("保存失败：" + ex.Message + "。完整内容仍保留，可以再次保存。")
                                    this.Refresh())
                    } |> Async.Start
                with ex ->
                    saving <- false
                    saveError <- Some("无法打开保存窗口：" + ex.Message)
                    this.Refresh()
        | _ -> ()
