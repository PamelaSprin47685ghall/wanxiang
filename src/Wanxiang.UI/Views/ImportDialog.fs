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

    /// 构造时所在的 UI 调度器；后台续延一律投递到这里，不在调用方线程上新建调度器（决策 421）。
    let ui = Avalonia.Threading.Dispatcher.UIThread
    let heading = Ui.title "导入对话"
    let intro =
        Ui.caption "选择从万象「导出（Markdown / JSON）」里保存的 JSON 文件。导入后是这台主机上的一个新对话，不会自动生成回复。"
    let status = TextBlock(TextWrapping = TextWrapping.Wrap, FontSize = Tokens.fontBody, Foreground = Tokens.text, Name = "import-status")
    let details = TextBlock(TextWrapping = TextWrapping.Wrap, FontSize = Tokens.fontSmall, Foreground = Tokens.textMuted, IsVisible = false, Name = "import-details")
    let privacy = Ui.caption ConversationTransfer.privacyNote
    let mutable preview: ImportPreview option = None
    /// 忙标记与「只认最新一次提交的回执」归 ImportAttempt。
    let attempts = ImportAttempt()
    let mutable active = false
    let closeButton = Ui.button Ui.Ghost "取消" (fun () -> overlay.CloseDialog())
    let pickButton = Ui.button Ui.Secondary "选择 JSON 文件…" (fun () -> this.Pick())
    let importButton = Ui.button Ui.Primary "导入为新对话" (fun () -> this.Confirm())

    member private this.Refresh() =
        let chrome = ImportView.chrome preview.IsSome attempts.Busy
        importButton.IsVisible <- chrome.importVisible
        Ui.setEnabled importButton chrome.importEnabled
        Ui.setEnabled pickButton chrome.pickEnabled
        Ui.setButtonText pickButton chrome.pickText

    member _.IsOpen = active
    member _.Preview = preview
    member _.StatusText = status.Text
    member _.DetailsText = details.Text
    member _.ImportButton = importButton
    member _.PickButton = pickButton

    member this.Show() =
        privacy.Name <- "import-privacy-note"
        status.Text <- ImportView.noFile
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
        attempts.Reset()
        this.Refresh()
        overlay.ShowDialog(content :> Control, 480.0, onClosed = (fun () -> active <- false; preview <- None))
        ui.Post(fun () -> if pickButton.IsEffectivelyVisible then pickButton.Focus() |> ignore)

    /// 读到文件内容后的唯一入口（测试也走这里）：解析、给预览或说明为什么不行。
    member this.LoadText(fileName: string, byteCount: int64, text: string) =
        if active then
            match ConversationTransfer.parseImport byteCount text with
            | Ok p ->
                preview <- Some p
                status.Text <- ImportView.previewStatus p.title p.messages.Length
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
                      match ImportView.skippedNote p.skipped with
                      | Some note -> yield note
                      | None -> ()
                      yield "模型使用这台主机的对话默认值，导入后可以在对话顶部更换。" ]
                details.Text <- String.Join("\n", lines)
                details.IsVisible <- true
                ui.Post(fun () -> if importButton.IsEffectivelyVisible then importButton.Focus() |> ignore)
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
                            ui.Post(fun () -> this.LoadText(file.Name, int64 bytes.Length, text))
                    with ex ->
                        ui.Post(fun () ->
                            if active then
                                status.Text <- ImportView.readFailed ex.Message
                                status.Foreground <- Tokens.danger)
                } |> Async.Start
            with ex ->
                status.Text <- ImportView.pickerFailed ex.Message
                status.Foreground <- Tokens.danger

    member private this.Confirm() =
        match preview with
        | Some p ->
            match attempts.Begin() with
            | None -> ()
            | Some attempt ->
                status.Text <- ImportView.importing
                this.Refresh()
                let finish ok (message: string) =
                    if attempts.Finish attempt then
                        if ok then overlay.CloseDialog()
                        elif active then
                            status.Text <- message
                            status.Foreground <- Tokens.danger
                            this.Refresh()
                // 旧主机不认识 conversation.import 时不会回应：等待有上限，不让对话框永远停在「正在导入…」。
                DispatcherTimer.RunOnce((fun () -> finish false ImportView.timedOut), MotionLedger.importResponseTimeout) |> ignore
                submit p (fun ok -> finish ok ImportView.rejected)
        | None -> ()
