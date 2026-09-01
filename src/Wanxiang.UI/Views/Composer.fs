namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Animation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Media

/// 待发送的附件（已上传并拿到 sha256）。
type PendingAttachment = {
    sha256: string
    size: int64
    mediaType: string
    fileName: string
    /// 上传中时为 false
    ready: bool
}

/// 输入区对外暴露的动作。
type ComposerActions = {
    submit: string -> unit
    stopGeneration: unit -> unit
    pickAttachment: unit -> unit
    removeAttachment: string -> unit
    openModelPicker: Control -> unit
    dropFiles: (string * byte[]) list -> unit
}

/// 消息输入区。
///
/// 关键取舍：
/// - 无会话时整块禁用并说明原因，而不是留一个点了没反应的输入框；
/// - 生成中把发送键换成停止键，同一位置同一手势；
/// - 附件以可删除的小卡呈现，而不是一行状态文字；
/// - 模型选择就放在输入框里，换模型不必开设置。
type Composer(actions: ComposerActions) as this =
    inherit Border()

    let input =
        TextBox(
            PlaceholderText = "输入消息…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = Thickness 0.0,
            Background = Brushes.Transparent,
            Foreground = Tokens.text,
            CaretBrush = Tokens.accent,
            FontSize = Tokens.fontReading,
            Padding = Thickness 0.0,
            MinHeight = 26.0,
            MaxHeight = 240.0,
            VerticalContentAlignment = VerticalAlignment.Center)

    let attachmentStrip =
        StackPanel(
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.space2,
            IsVisible = false,
            Margin = Thickness(0.0, 0.0, 0.0, Tokens.space2))

    let sendButton = Ui.iconButtonAccent Icons.send "发送"
    let attachButton = Ui.iconButton Icons.paperclip "添加附件"
    let modelCaption =
        TextBlock(
            Text = "未选择模型",
            FontSize = Tokens.fontMicro,
            Foreground = Tokens.textMuted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 240.0)
    let modelChip =
        Border(
            Background = Brushes.Transparent,
            CornerRadius = CornerRadius Tokens.radiusSm,
            Padding = Thickness(Tokens.space2, 3.0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = true,
            VerticalAlignment = VerticalAlignment.Center)
    let hintText =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontMicro,
            Foreground = Tokens.textFaint,
            VerticalAlignment = VerticalAlignment.Center)
    let counterText =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontMicro,
            Foreground = Tokens.textFaint,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false)

    let shell =
        Border(
            Background = Tokens.surface,
            BorderBrush = Tokens.border,
            BorderThickness = Thickness 1.0,
            CornerRadius = CornerRadius Tokens.radiusLg)

    let disabledNotice =
        TextBlock(
            Text = "",
            FontSize = Tokens.fontSmall,
            Foreground = Tokens.textFaint,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false)

    let mutable enterSends = true
    let mutable generating = false
    let mutable enabled = false
    let mutable attachments: PendingAttachment list = []

    let estimateTokens (text: string) : int =
        if String.IsNullOrEmpty text then 0
        else
            let mutable cjk = 0
            let mutable other = 0
            for ch in text do
                if int ch >= 0x4e00 && int ch <= 0x9fa5 then cjk <- cjk + 1
                elif not (Char.IsWhiteSpace ch) then other <- other + 1
            int (Math.Ceiling(float cjk * 0.7 + float other * 0.3))

    let refreshSendState () =
        let rawText = if isNull input.Text then "" else input.Text
        let hasText = not (String.IsNullOrWhiteSpace rawText)
        let hasAttachment = attachments |> List.exists (fun a -> a.ready)
        if rawText.Length > 0 then
            let tokens = estimateTokens rawText
            counterText.Text <- sprintf "%d 字 · ~%d tok" rawText.Length (max 1 tokens)
            counterText.IsVisible <- true
        else
            counterText.Text <- ""
            counterText.IsVisible <- false
        Ui.setEnabled sendButton (generating || (enabled && (hasText || hasAttachment)))

    do
        modelChip.Child <-
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space1, VerticalAlignment = VerticalAlignment.Center)
            let glyph = Icons.sparkle Tokens.textFaint
            glyph.VerticalAlignment <- VerticalAlignment.Center
            row.Children.Add glyph
            row.Children.Add modelCaption
            let chevron = Icons.chevronDown Tokens.textFaint
            chevron.VerticalAlignment <- VerticalAlignment.Center
            row.Children.Add chevron
            row
        modelChip.PointerEntered.Add(fun _ -> modelChip.Background <- Tokens.hover)
        modelChip.PointerExited.Add(fun _ -> modelChip.Background <- Brushes.Transparent)
        Ui.onClick modelChip (fun () -> actions.openModelPicker(modelChip :> Control))
        ToolTip.SetTip(modelChip, "切换本会话使用的模型")

        Ui.onClick attachButton (fun () -> actions.pickAttachment ())

        Ui.onClick sendButton (fun () ->
            if generating then actions.stopGeneration () else this.Submit())

        input.TextChanged.Add(fun _ -> refreshSendState ())
        // TextBox 的 AcceptsReturn 会在自己的 OnKeyDown 里吃掉 Enter，
        // 冒泡阶段的监听器再拦已经太晚，必须走隧道阶段。
        input.AddHandler(
            InputElement.KeyDownEvent,
            EventHandler<KeyEventArgs>(fun _ e ->
                if e.Key = Key.Enter then
                    let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
                    let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
                    if shift then ()
                    elif enterSends || ctrl then
                        e.Handled <- true
                        this.Submit()),
            RoutingStrategies.Tunnel)

        let transitions = Transitions()
        transitions.Add(BrushTransition(Property = Border.BorderBrushProperty, Duration = TimeSpan.FromMilliseconds 120.0))
        shell.Transitions <- transitions

        DragDrop.SetAllowDrop(shell, true)
        shell.AddHandler(
            DragDrop.DragEnterEvent,
            EventHandler<DragEventArgs>(fun _ e ->
                let files = e.DataTransfer.TryGetFiles()
                if not (isNull files) && files.Length > 0 then
                    shell.BorderBrush <- Tokens.accent
                    shell.BoxShadow <- BoxShadows(BoxShadow(Spread = 2.0, Color = Tokens.accent.Color))),
            RoutingStrategies.Bubble)
        shell.AddHandler(
            DragDrop.DragLeaveEvent,
            EventHandler<RoutedEventArgs>(fun _ _ ->
                if not input.IsFocused then
                    shell.BorderBrush <- Tokens.border
                    shell.BoxShadow <- Tokens.shadowSoft ()),
            RoutingStrategies.Bubble)
        shell.AddHandler(
            DragDrop.DragOverEvent,
            EventHandler<DragEventArgs>(fun _ e ->
                let files = e.DataTransfer.TryGetFiles()
                if not (isNull files) && files.Length > 0 then
                    e.DragEffects <- DragDropEffects.Copy
                else
                    e.DragEffects <- DragDropEffects.None),
            RoutingStrategies.Bubble)
        shell.AddHandler(
            DragDrop.DropEvent,
            EventHandler<DragEventArgs>(fun _ e ->
                if not input.IsFocused then
                    shell.BorderBrush <- Tokens.border
                    shell.BoxShadow <- Tokens.shadowSoft ()
                let files = e.DataTransfer.TryGetFiles()
                if not (isNull files) && files.Length > 0 then
                    let list = ResizeArray<string * byte[]>()
                    for item in files do
                        match item with
                        | :? Avalonia.Platform.Storage.IStorageFile as file ->
                            try
                                use stream = file.OpenReadAsync().GetAwaiter().GetResult()
                                use ms = new System.IO.MemoryStream()
                                stream.CopyTo(ms :> System.IO.Stream)
                                list.Add(file.Name, ms.ToArray())
                            with _ -> ()
                        | _ -> ()
                    if list.Count > 0 then
                        actions.dropFiles (List.ofSeq list)),
            RoutingStrategies.Bubble)

    member private this.Submit() =
        if enabled && not generating then
            let text = if isNull input.Text then "" else input.Text
            if not (String.IsNullOrWhiteSpace text) || attachments |> List.exists (fun a -> a.ready) then
                input.Text <- ""
                refreshSendState ()
                actions.submit text
                input.Focus() |> ignore

    member private this.RenderAttachments() =
        attachmentStrip.Children.Clear()
        for attachment in attachments do
            let icon =
                if attachment.ready then
                    let glyph = if attachment.mediaType.StartsWith("image/", StringComparison.Ordinal) then Icons.image else Icons.file
                    glyph Tokens.textMuted
                else
                    Ui.spinner 13.0
            icon.VerticalAlignment <- VerticalAlignment.Center
            let name =
                TextBlock(
                    Text = attachment.fileName,
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.text,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 150.0,
                    VerticalAlignment = VerticalAlignment.Center)
            let size =
                TextBlock(
                    Text = (if attachment.ready then AttachmentRef.formatSize attachment.size else "上传中"),
                    FontSize = Tokens.fontMicro,
                    Foreground = Tokens.textFaint,
                    VerticalAlignment = VerticalAlignment.Center)
            let remove = Ui.iconButton Icons.close "移除"
            remove.Width <- 20.0
            remove.Height <- 20.0
            remove.MinWidth <- 20.0
            remove.MinHeight <- 20.0
            Ui.onClick remove (fun () -> actions.removeAttachment attachment.sha256)
            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = Tokens.space1, VerticalAlignment = VerticalAlignment.Center)
            row.Children.Add icon
            row.Children.Add name
            row.Children.Add size
            row.Children.Add remove
            attachmentStrip.Children.Add(
                Border(
                    Background = Tokens.surfaceRaised,
                    BorderBrush = Tokens.border,
                    BorderThickness = Thickness 1.0,
                    CornerRadius = CornerRadius Tokens.radiusSm,
                    Padding = Thickness(Tokens.space2, 3.0),
                    Child = row))
        attachmentStrip.IsVisible <- not (List.isEmpty attachments)

    member this.SetAttachments(items: PendingAttachment list) =
        attachments <- items
        this.RenderAttachments()
        refreshSendState ()

    member this.SetGenerating(value: bool) =
        generating <- value
        if value then
            Ui.setIcon sendButton Icons.stop Tokens.textOnAccent
            ToolTip.SetTip(sendButton, "停止生成")
        else
            Ui.setIcon sendButton Icons.send Tokens.textOnAccent
            ToolTip.SetTip(sendButton, "发送")
        refreshSendState ()

    /// 启用输入。`reason` 在禁用时说明为什么不能发。
    member this.SetEnabled(value: bool, reason: string) =
        enabled <- value
        input.IsEnabled <- value
        Ui.setEnabled attachButton value
        shell.Opacity <- if value then 1.0 else 0.72
        disabledNotice.Text <- reason
        disabledNotice.IsVisible <- not value && not (String.IsNullOrWhiteSpace reason)
        refreshSendState ()

    member _.SetModelLabel(text: string) = modelCaption.Text <- text

    member _.SetEnterSends(value: bool) =
        enterSends <- value
        hintText.Text <- if value then "Enter 发送 · Shift+Enter 换行" else "Ctrl+Enter 发送 · Enter 换行"

    member _.Focus() = input.Focus() |> ignore

    /// 把文本塞进输入框（编辑重发、提示词模板）。
    member _.SetText(text: string) =
        input.Text <- text
        input.CaretIndex <- if isNull text then 0 else text.Length
        input.Focus() |> ignore

    member this.Build() =
        this.Background <- Brushes.Transparent
        // 顶部留白是**确定**的间距：滚动区能留多少取决于 Extent 与 ScrollToEnd 的行为，
        // 而这里的 padding 一定在，末条消息的脚注行不会贴到输入卡上。
        this.Padding <- Thickness(Tokens.shellInset, Tokens.space4, Tokens.shellInset, Tokens.space3)

        let actionRow = Ui.hstack Tokens.space1 [ attachButton :> Control; sendButton :> Control ]
        actionRow.VerticalAlignment <- VerticalAlignment.Bottom

        let inputRow = DockPanel(LastChildFill = true)
        DockPanel.SetDock(actionRow, Dock.Right)
        inputRow.Children.Add actionRow
        inputRow.Children.Add input

        let rightMeta = Ui.hstack Tokens.space2 [ counterText :> Control; hintText :> Control ]
        let footerRow = DockPanel(LastChildFill = false)
        DockPanel.SetDock(modelChip, Dock.Left)
        DockPanel.SetDock(rightMeta, Dock.Right)
        footerRow.Children.Add modelChip
        footerRow.Children.Add rightMeta

        let column = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2)
        column.Children.Add attachmentStrip
        column.Children.Add inputRow
        column.Children.Add(Border(Height = 1.0, Background = Tokens.borderSoft))
        column.Children.Add footerRow

        shell.Padding <- Thickness(Tokens.space3, Tokens.space2, Tokens.space2, Tokens.space2)
        shell.Child <- column
        shell.BoxShadow <- Tokens.shadowSoft ()

        input.GotFocus.Add(fun _ ->
            shell.BorderBrush <- Tokens.accent
            shell.BoxShadow <- BoxShadows(BoxShadow(Spread = 1.5, Color = Tokens.accentSoft.Color)))
        input.LostFocus.Add(fun _ ->
            shell.BorderBrush <- Tokens.border
            shell.BoxShadow <- Tokens.shadowSoft ())

        let outer = StackPanel(Orientation = Orientation.Vertical, Spacing = Tokens.space2, MaxWidth = Tokens.readingWidth)
        outer.Children.Add disabledNotice
        outer.Children.Add shell
        this.Child <- outer
        this.SetEnterSends true
        refreshSendState ()
