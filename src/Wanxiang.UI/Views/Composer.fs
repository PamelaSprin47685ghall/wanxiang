namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Wanxiang.Core
open Wanxiang2.ComposerDesk

/// 输入区对外暴露的动作。
type ComposerActions = {
    /// true 表示内容已由本地待发送记录接管，不代表主机已经保存。
    submit: string -> bool
    stopGeneration: unit -> unit
    pickAttachment: unit -> unit
    removeAttachment: Guid -> unit
    openModelPicker: Control -> unit
    dropFiles: IStorageItem list -> unit
    pasteFromClipboard: unit -> bool
}

/// 消息输入区：输入框、附件托盘、模型芯片与发送门控全部交给 Wanxiang2.ComposerDesk；
/// 待发送面板与根留白也在库里；宿主这里只留发送历史召回（PromptHistory）、IME 守护、图像解码与上下文摘要。
/// 适配器不是控件：Host 就是 Desk.Host，由外层 Workspace 唯一挂载，不另包一层 Border。
type Composer(actions: ComposerActions) =
    let history = PromptHistory()
    let mutable openSessionSettings: unit -> unit = ignore
    let mutable visionSupport = VisionUnknown
    let mutable visionModelLabel = ""
    let mutable attachments: PendingAttachment list = []

    let desk =
        ComposerDesk(
            ComposerLooks.ofTheme SharedTheme.manager SharedTheme.styles,
            { Submit = fun text -> if actions.submit text then (history.Record text; true) else false
              StopGeneration = fun () -> actions.stopGeneration ()
              PickAttachment = fun () -> actions.pickAttachment ()
              RemoveAttachment = actions.removeAttachment
              OpenModelPicker = actions.openModelPicker
              DropFiles = actions.dropFiles
              PasteFromClipboard = actions.pasteFromClipboard
              OpenSessionSettings = fun () -> openSessionSettings () })

    let input = desk.Box.Input

    let recall (text: string option) =
        match text with
        | Some t ->
            input.Text <- t
            input.CaretIndex <- (if isNull input.Text then 0 else input.Text.Length)
            true
        | None -> false

    /// 旧 Composer 的 Esc / 上下键：选区先清、召回中先退回草稿、否则把焦点交出；Up/Down 走发送历史。
    let keyHook (e: KeyEventArgs) =
        let hasSelection = input.SelectionStart <> input.SelectionEnd
        if e.Key = Key.Escape then
            if hasSelection then
                input.ClearSelection()
                true
            elif recall (history.Escape()) then true
            elif input.IsFocused then
                match TopLevel.GetTopLevel input with
                | null -> ()
                | top ->
                    match top.FocusManager with
                    | null -> ()
                    | fm -> fm.Focus(null, NavigationMethod.Unspecified, KeyModifiers.None) |> ignore
                true
            else false
        elif e.Key = Key.Up && e.KeyModifiers = KeyModifiers.None then
            recall (history.Up(input.Text, input.CaretIndex, hasSelection))
        elif e.Key = Key.Down && e.KeyModifiers = KeyModifiers.None then
            recall (history.Down(input.Text, input.CaretIndex, hasSelection))
        else false

    do
        desk.SetImeProbe(fun box -> Wanxiang2.ImeShield.ImeShield.isComposing box)
        desk.SetKeyHook keyHook
        desk.SetEnterSends true
        desk.SetCompactMode false

    member _.Desk = desk
    member _.Host = desk.Host

    /// 与发送键同一路径（旧 Composer.Submit）：门控通过才交给宿主，宿主接管才清空；宿主异常照常抛出。
    member _.Submit() = desk.Submit()
    member _.Input = input

    member _.SetThumbnail(attachmentId: Guid, bytes: byte[]) =
        try
            use ms = new IO.MemoryStream(bytes)
            desk.SetThumbnail(attachmentId, Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, 96))
        with _ -> ()

    member _.HasThumbnail(attachmentId: Guid) = desk.HasThumbnail attachmentId
    member _.SetShrinkNote(attachmentId: Guid, note: string) = desk.SetShrinkNote(attachmentId, note)
    member _.ShrinkNote(attachmentId: Guid) = desk.ShrinkNote attachmentId

    member _.SetModelVision(support: VisionSupport, modelLabel: string) =
        visionSupport <- support
        visionModelLabel <- modelLabel
        desk.SetModelVision(support, modelLabel)

    member _.VisionNoticeText =
        if visionSupport = VisionNo && attachments |> List.exists (fun a -> MediaTypes.isImage a.mediaType) then
            ModelVision.blockedMessage visionModelLabel
        else ""

    member _.SetAttachments(items: PendingAttachment list) =
        attachments <- items
        desk.SetAttachments items

    /// 历史召回中输入框显示的是旧发送，真正的半截草稿由 history 保管。
    member _.Text = history.Draft desk.Text

    member _.SetCanStop(value: bool) = desk.SetCanStop value

    member _.SetPendingMessages(items: PendingMessage list, activeConversationId: Guid option, canRetry: bool, retry: Guid -> unit, openConversation: Guid -> unit) =
        desk.SetPendingMessages(items, activeConversationId, canRetry, retry, openConversation)

    member _.SetGenerating(value: bool) = desk.SetGenerating value
    member _.IsGenerating = desk.IsGenerating
    member _.SetEnabled(value: bool, reason: string) = desk.SetEnabled(value, reason)
    member _.SetModelLabel(text: string) = desk.SetModelLabel text

    member _.SetSessionContext(config: SessionConfig option, canEdit: bool, onOpen: unit -> unit) =
        openSessionSettings <- onOpen
        // 决策 364：工具开关不算「特殊设置」；只有特殊设置时输入区才显示摘要。
        let isCustomized (value: SessionConfig) =
            value.instructions.IsSome
            || value.temperature.IsSome || value.topP.IsSome || value.maxTokens.IsSome
            || value.thinkingBudget.IsSome || value.extraJson.IsSome
        match config with
        | Some value when isCustomized value ->
            let label = if value.instructions.IsSome then "系统指令" else "生成参数"
            let detail =
                sprintf "%s，%d 项工具。修改只影响当前对话。"
                    (if value.instructions.IsSome then "已设系统指令" else "默认系统指令") value.tools.Length
            desk.SetContext(Some(label, detail), canEdit)
        | _ -> desk.SetContext(None, canEdit)

    member _.ModelAnchor() : Control = desk.ModelAnchor()
    member _.SetAccessory(control: Control) = desk.SetAccessory control
    member _.SetEnterSends(value: bool) = desk.SetEnterSends value

    /// 窄屏收紧与按宽取档的根留白归 ComposerDesk（旧 ApplyComposerPadding）。
    member _.SetCompactMode(value: bool) = desk.SetCompactMode value

    member _.Focus() = desk.Focus()

    /// 把文本塞进输入框（编辑重发、提示词模板）。
    member _.SetText(text: string) =
        desk.SetText text
        history.Reset()
