namespace Wanxiang.UI

open System
open System.Text
open System.Threading.Tasks
open Avalonia.Automation
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
    // 不确定进度条是原生循环动画（Ui.spinner 的旋转指示同样持续不停，这里不声称唯一）：
    // 减弱动效时不能起环，退为确定态静态条（未知总数时 Value = 0）。构造与每次渲染都按当前偏好判断，
    // 用户在设置里切换 reduceMotion 后重新导出生效。
    let progress =
        ProgressBar(
            IsIndeterminate = not (MotionPolicy.isReduced ()),
            Minimum = 0.0,
            Maximum = 100.0)
    let mutable active = false
    let mutable saving = false
    let mutable saved = false
    let mutable saveError: string option = None
    let mutable output: byte[] option = None
    let timer = DispatcherTimer(Interval = MotionLedger.exportExpiryCheckTick)
    do
        timer.Tick.Add(fun _ ->
            if controller.Expire(DateTimeOffset.UtcNow, TimeSpan.FromSeconds 30.0) then this.Refresh(forceImmediate = true))
    // 进度文字节流：高频分页（ExportReading）到达时合并重排，窗口末尾再补一次当前状态；
    // 关键跳变与终态（阶段切换、完成、失败、取消、保存反馈）走急件通道，立即呈现。
    let throttleTimer = DispatcherTimer(Interval = MotionLedger.progressTextThrottle)
    let mutable lastProgressRefresh = DateTimeOffset.MinValue
    let mutable throttlePending = false
    do
        throttleTimer.Tick.Add(fun _ ->
            if throttlePending then this.RenderProgress())
    // 读取中点「取消导出」只中止本次读取并停在取消态：界面原地给出「重新导出」入口，
    // 用户不再必须关掉对话框重开；取消后同一按钮即「关闭」。Esc 走 OverlayHost 既有路径，始终关闭。
    let closeButton = Ui.button Ui.Ghost "取消导出" (fun () ->
        match controller.Status with
        | ExportReading _ -> this.Cancel()
        | _ -> overlay.CloseDialog())
    let retryButton = Ui.button Ui.Secondary "重新导出" (fun () -> this.Start())
    let saveButton = Ui.button Ui.Primary "保存 Markdown" (fun () -> this.Save())

    // 立即渲染一帧进度：记下刷新时刻，并取消尚未落地的节流尾巴。
    // 这一层只把 controller.Status 同步到控件，不做任何时间判断。
    member private this.RenderProgress() =
        throttlePending <- false
        throttleTimer.Stop()
        lastProgressRefresh <- DateTimeOffset.UtcNow
        let reading, ready, failed, isEmpty =
            match controller.Status with
            | ExportReading(received, total) ->
                // 读取中是等待态：文字退一档（textMuted），终态再切回前景色。
                status.Foreground <- Tokens.textMuted
                status.Text <- match total with None -> "正在读取完整历史…" | Some total -> sprintf "已读取 %d / %d 条消息…" received total
                // 未知总分才需要不确定态；减弱动效时同样退为确定态静态条。
                progress.IsIndeterminate <- total.IsNone && not (MotionPolicy.isReduced ())
                progress.Value <- total |> Option.filter ((<) 0) |> Option.map (fun total -> float received / float total * 100.0) |> Option.defaultValue 0.0
                true, false, false, false
            | ExportReady count ->
                let empty = count = 0
                status.Text <-
                    if empty then "当前会话没有已保存的消息，无需导出"
                    elif saving then "正在保存…"
                    elif saved then sprintf "已保存完整记录（%d 条消息）。" count
                    else saveError |> Option.defaultValue (sprintf "已完整读取 %d 条消息，可以保存。" count)
                // 终态文字色分级：
                // 状态只切文字色：已保存=success，保存出错=danger，其余=正文 text。空态同样经过这里：按钮保持可见但禁用。
                status.Foreground <-
                    if saved then Tokens.success
                    elif saveError.IsSome then Tokens.danger
                    else Tokens.text
                false, output.IsSome, output.IsNone, empty
            | ExportFailed message -> status.Text <- message; status.Foreground <- Tokens.danger; false, false, true, false
            // 取消是终态之一：重试入口与失败态同列可见（与失败只差文字语气：textMuted 而非 danger）。
            | ExportCancelled -> status.Text <- "导出已取消。"; status.Foreground <- Tokens.textMuted; false, false, true, false
            | ExportIdle -> status.Text <- "正在准备导出…"; status.Foreground <- Tokens.text; false, false, false, false
        progress.IsVisible <- reading
        retryButton.IsVisible <- failed
        saveButton.IsVisible <- ready && not saved
        Ui.setEnabled saveButton (ready && not saving && not isEmpty)
        Ui.setButtonText closeButton (if reading then "取消导出" else "关闭")

    // 进度刷新入口。
    // forceImmediate 是急件通道：阶段切换、完成、失败、取消、到期与保存反馈都不受节流拖延。
    // ExportReading 是唯一高频态：同一节流窗口内的多次分页到达合并为一次重排，并在窗口末尾
    // 补渲染一次（throttleTimer 尾巴），让最终计数也落地；非高频态（含全部终态）始终立即呈现。
    member private this.Refresh(?forceImmediate: bool) =
        let force = defaultArg forceImmediate false
        let now = DateTimeOffset.UtcNow
        let elapsed = now - lastProgressRefresh
        let highFrequency =
            match controller.Status with
            | ExportReading _ -> true
            | _ -> false
        if force || not highFrequency || elapsed >= MotionLedger.progressTextThrottle then
            this.RenderProgress()
        elif not throttlePending then
            throttlePending <- true
            throttleTimer.Interval <- MotionLedger.progressTextThrottle - elapsed
            throttleTimer.Start()

    member private this.Dispatch(query: ConversationExportQuery) =
        let sending = sendQuery query
        async {
            try
                let! sent = sending |> Async.AwaitTask
                if not sent then
                    Dispatcher.UIThread.Post(fun () -> this.Fail(query.exportId, "连接已断开，未完成导出；请重新连接后重试。"))
            with ex -> Dispatcher.UIThread.Post(fun () -> this.Fail(query.exportId, "读取历史失败：" + ex.Message))
        } |> Async.Start

    /// 用户主动取消：控制器放弃本次读取并进入取消态；立即渲染，
    /// 「重新导出」按钮随取消态一起出现，不必重开对话框。
    member private this.Cancel() =
        controller.Cancel()
        this.Refresh(forceImmediate = true)

    member private this.Start() =
        output <- None
        saveError <- None
        saved <- false
        let query = controller.Start()
        this.Refresh(forceImmediate = true)
        this.Dispatch query

    member _.IsOpen = active

    member this.Show() =
        let buttons = Ui.hstack Tokens.space2 [ closeButton :> Control; retryButton :> Control; saveButton :> Control ]
        buttons.HorizontalAlignment <- HorizontalAlignment.Right
        let content = Ui.vstack Tokens.space3
                          [ Ui.title "导出会话" :> Control; Ui.caption initialTitle :> Control
                            status :> Control; progress :> Control
                            // 进度区与说明/操作区之间一条最轻发丝线：上方是实时反馈，
                            // 下面是静态说明与按钮，分层不靠间距硬猜。
                            Border(Height = ControlMetrics.borderWidth, Background = Tokens.hairline, HorizontalAlignment = HorizontalAlignment.Stretch) :> Control
                            Ui.caption "导出开始时已保存的消息、工具记录和附件说明。未完成的生成、排队消息及附件文件本体不包含在内；这不是完整备份。" :> Control
                            buttons :> Control ]
        active <- true
        // D6：读屏需要按钮名称/说明与进度 live；初始焦点落在取消导出上（读取中唯一可用操作）。
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive)
        AutomationProperties.SetName(closeButton, "取消导出")
        AutomationProperties.SetHelpText(closeButton, "读取中取消导出（对话框保持打开，可重新导出）；取消后此按钮为关闭 (Esc 随时关闭)")
        AutomationProperties.SetName(retryButton, "重新导出")
        AutomationProperties.SetHelpText(retryButton, "重新读取并导出当前会话")
        AutomationProperties.SetName(saveButton, "保存 Markdown")
        AutomationProperties.SetHelpText(saveButton, "保存导出的 Markdown 文件")
        overlay.ShowDialog(content :> Control, 480.0, onClosed = (fun () ->
            active <- false
            timer.Stop()
            throttleTimer.Stop()
            throttlePending <- false
            controller.Cancel()
            output <- None))
        timer.Start()
        this.Start()
        Dispatcher.UIThread.Post(fun () ->
            if closeButton.IsEffectivelyVisible && closeButton.IsEnabled then
                closeButton.Focus() |> ignore)

    member this.Handle(page: ConversationExportPageData) =
        if active then
            let next = controller.Accept page
            match controller.Document, output with
            | Some document, None ->
                try
                    let markdown = Export.toMarkdown document.title document.messages
                    if int64 (Encoding.UTF8.GetByteCount markdown) > ConversationExportLimits.maxTranscriptBytes then
                        saveError <- Some "生成的 Markdown 超过 64 MiB 安全上限；没有保存不完整文件。"
                    else output <- Some(Encoding.UTF8.GetBytes markdown)
                with ex -> saveError <- Some("无法生成完整 Markdown：" + ex.Message)
            | _ -> ()
            this.Refresh()
            next |> Option.iter this.Dispatch

    member this.Fail(exportId: Guid, message: string) =
        if active then
            controller.Fail(exportId, message)
            this.Refresh(forceImmediate = true)

    member this.Disconnect() =
        if active then
            controller.Disconnect()
            this.Refresh(forceImmediate = true)

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
