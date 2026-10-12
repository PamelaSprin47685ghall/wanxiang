namespace Wanxiang.UI

open System
open System.IO
open System.Text
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Threading

/// 导入对话（决策 313）：一个对话框三步走——说明 → 选文件并预览 → 确认导入。
/// 选文件按钮在对话框里点：浏览器要求文件选择器由用户手势直接打开。
/// 预览通过前什么都不发给主机；确认后才发一条 `conversation.import`。
type ConversationImportDialog(
    overlay: OverlayHost,
    topLevel: unit -> TopLevel,
    submit: ImportPreview -> (bool -> unit) -> unit) as this =

    let heading = Ui.title "导入对话"
    let intro =
        Ui.caption "选择从万象「导出（Markdown / JSON）」里保存的 JSON 文件。导入后是这台主机上的一个新对话，不会自动生成回复。"
    let status = TextBlock(TextWrapping = TextWrapping.Wrap, FontSize = Tokens.fontBody, Foreground = Tokens.text, Name = "import-status")
    let details = TextBlock(TextWrapping = TextWrapping.Wrap, FontSize = Tokens.fontSmall, Foreground = Tokens.textMuted, IsVisible = false, Name = "import-details")
    let privacy = Ui.caption ConversationTransfer.privacyNote
    let mutable preview: ImportPreview option = None
    let mutable busy = false
    let mutable active = false
    let mutable currentAttempt = Guid.Empty
    let closeButton = Ui.button Ui.Ghost "取消" (fun () -> overlay.CloseDialog())
    let pickButton = Ui.button Ui.Secondary "选择 JSON 文件…" (fun () -> this.Pick())
    let importButton = Ui.button Ui.Primary "导入为新对话" (fun () -> this.Confirm())

    member private this.Refresh() =
        importButton.IsVisible <- preview.IsSome
        Ui.setEnabled importButton (preview.IsSome && not busy)
        Ui.setEnabled pickButton (not busy)
        Ui.setButtonText pickButton (if preview.IsSome then "换一个文件…" else "选择 JSON 文件…")

    member _.IsOpen = active
    member _.Preview = preview
    member _.StatusText = status.Text
    member _.DetailsText = details.Text
    member _.ImportButton = importButton
    member _.PickButton = pickButton

    member this.Show() =
        privacy.Name <- "import-privacy-note"
        status.Text <- "还没有选择文件。"
        status.Foreground <- Tokens.textMuted
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite)
        AutomationProperties.SetName(pickButton, "选择要导入的 JSON 文件")
        AutomationProperties.SetName(importButton, "导入为新对话")
        AutomationProperties.SetHelpText(importButton, "在这台主机上新建一个对话并写入文件里的消息")
        let buttons = Ui.hstack Spacing.spaceMd [ closeButton :> Control; pickButton :> Control; importButton :> Control ]
        buttons.HorizontalAlignment <- HorizontalAlignment.Right
        let content =
            Ui.vstack Spacing.spaceXl
                [ heading :> Control; intro :> Control; status :> Control; details :> Control
                  Border(Height = ControlMetrics.borderWidth, Background = Tokens.hairline, HorizontalAlignment = HorizontalAlignment.Stretch) :> Control
                  privacy :> Control; buttons :> Control ]
        active <- true
        preview <- None
        busy <- false
        this.Refresh()
        overlay.ShowDialog(content :> Control, 480.0, onClosed = (fun () -> active <- false; preview <- None))
        Dispatcher.UIThread.Post(fun () -> if pickButton.IsEffectivelyVisible then pickButton.Focus() |> ignore)

    /// 读到文件内容后的唯一入口（测试也走这里）：解析、给预览或说明为什么不行。
    member this.LoadText(fileName: string, byteCount: int64, text: string) =
        if active then
            match ConversationTransfer.parseImport byteCount text with
            | Ok p ->
                preview <- Some p
                status.Text <- sprintf "「%s」· %d 条消息" p.title p.messages.Length
                status.Foreground <- Tokens.text
                let lines =
                    [ yield sprintf "文件：%s" fileName
                      match p.exportedAt with
                      | Some at -> yield sprintf "导出时间：%s" (at.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                      | None -> ()
                      yield sprintf "导入后的标题：%s" (ConversationTransfer.importedTitle p.title)
                      match p.lineageNote with
                      | Some note -> yield note
                      | None -> ()
                      if p.hasAttachments then yield "文件里提到的附件只有名称，文件本体不在导出里，导入后无法打开。"
                      if p.skipped > 0 then yield sprintf "有 %d 条无法识别的记录会被跳过。" p.skipped
                      yield "模型使用这台主机的默认设置，导入后可以在对话顶部更换。" ]
                details.Text <- String.Join("\n", lines)
                details.IsVisible <- true
                Dispatcher.UIThread.Post(fun () -> if importButton.IsEffectivelyVisible then importButton.Focus() |> ignore)
            | Error message ->
                preview <- None
                status.Text <- message
                status.Foreground <- Tokens.danger
                details.IsVisible <- false
            this.Refresh()

    member private this.Pick() =
        match topLevel () with
        | null -> ()
        | top ->
            try
                let picking =
                    top.StorageProvider.OpenFilePickerAsync(
                        FilePickerOpenOptions(
                            Title = "导入对话",
                            AllowMultiple = false,
                            FileTypeFilter = [ FilePickerFileType("万象对话 JSON", Patterns = [| "*.json" |]) ]))
                async {
                    try
                        let! picked = picking |> Async.AwaitTask
                        if picked.Count > 0 then
                            let file = picked[0]
                            use! stream = file.OpenReadAsync() |> Async.AwaitTask
                            use buffer = new MemoryStream()
                            // 先只读到上限 + 1 字节：超限就不必把整份大文件读进内存。
                            let limit = ConversationTransfer.maxImportBytes + 1L
                            let chunk = Array.zeroCreate<byte> 65536
                            let mutable reading = true
                            while reading && buffer.Length < limit do
                                let! n = stream.ReadAsync(chunk, 0, chunk.Length) |> Async.AwaitTask
                                if n = 0 then reading <- false else buffer.Write(chunk, 0, n)
                            let bytes = buffer.ToArray()
                            let text = if int64 bytes.Length > ConversationTransfer.maxImportBytes then "" else Encoding.UTF8.GetString bytes
                            Dispatcher.UIThread.Post(fun () -> this.LoadText(file.Name, int64 bytes.Length, text))
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            if active then
                                status.Text <- "读取文件失败：" + ex.Message
                                status.Foreground <- Tokens.danger)
                } |> Async.Start
            with ex ->
                status.Text <- "无法打开文件选择窗口：" + ex.Message
                status.Foreground <- Tokens.danger

    member private this.Confirm() =
        match preview with
        | Some p when not busy ->
            busy <- true
            status.Text <- "正在导入…"
            this.Refresh()
            let attempt = Guid.NewGuid()
            currentAttempt <- attempt
            let finish ok (message: string) =
                if busy && currentAttempt = attempt then
                    busy <- false
                    if ok then overlay.CloseDialog()
                    elif active then
                        status.Text <- message
                        status.Foreground <- Tokens.danger
                        this.Refresh()
            // 旧主机不认识 conversation.import 时不会回应：等待有上限，不让对话框永远停在「正在导入…」。
            DispatcherTimer.RunOnce(
                (fun () -> finish false "主机没有回应导入请求，可能是主机版本过旧。升级主机后再试。"),
                MotionLedger.importResponseTimeout) |> ignore
            submit p (fun ok -> finish ok "导入没有完成，主机拒绝了这份内容或连接已断开。可以再试一次。")
        | _ -> ()
