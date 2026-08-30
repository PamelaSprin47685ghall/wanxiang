namespace Wanxiang.UI

open System
open System.Text.Json.Nodes
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Platform
open Avalonia.Layout

/// 品牌标识渲染。
///
/// 内嵌 avares 资源优先：browser-wasm 没有宿主文件系统，
/// 只有内嵌资源在桌面与 PWA 两端都能拿到同一张图。
module Brand =

    let private tryLoad () : Bitmap option =
        try
            use stream = AssetLoader.Open(Uri "avares://Wanxiang.UI/Assets/logo.png")
            Some(new Bitmap(stream))
        with _ ->
            try
                let home =
                    match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
                    | s when not (String.IsNullOrWhiteSpace s) -> s
                    | _ -> IO.Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")
                [ IO.Path.Combine(AppContext.BaseDirectory, "logo.png")
                  IO.Path.Combine(AppContext.BaseDirectory, "pwa", "logo.png")
                  IO.Path.Combine(home, "logo.png") ]
                |> List.map IO.Path.GetFullPath
                |> List.tryFind IO.File.Exists
                |> Option.map (fun path -> new Bitmap(path))
            with _ ->
                None

    let private bitmap = tryLoad ()

    /// 圆角方形品牌标。找不到图时退化成「万」字。
    let logo (size: float) : Control =
        let tile =
            Border(
                Width = size,
                Height = size,
                CornerRadius = Avalonia.CornerRadius(size * Tokens.logoRadiusRatio),
                Background = Tokens.surface,
                ClipToBounds = true)
        match bitmap with
        | Some image -> tile.Child <- Image(Source = image, Stretch = Stretch.UniformToFill)
        | None ->
            tile.Child <-
                TextBlock(
                    Text = "万",
                    Foreground = Tokens.accent,
                    FontSize = size * 0.42,
                    FontWeight = FontWeight.Medium,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center)
        tile :> Control

/// 附件上传状态机所需的一点点状态。
type AttachmentUpload = {
    attachmentId: Guid
    fileName: string
    mediaType: string
    size: int64
    sha256: string
}

/// 导出会话为 Markdown。
module Export =

    let private roleTitle (role: string) =
        match role with
        | "user" -> "用户"
        | "assistant" -> "助手"
        | "tool" -> "工具"
        | "system" -> "系统"
        | other -> other

    /// 生成可读的 Markdown 转录。工具调用与思考过程也保留，方便存档与复盘。
    let toMarkdown (title: string) (messages: MessageView list) : string =
        let builder = Text.StringBuilder()
        builder.AppendLine("# " + title).AppendLine() |> ignore
        for message in MessageView.mergeToolResults messages do
            if MessageView.hasVisibleBody message then
                builder.AppendLine("## " + roleTitle message.role).AppendLine() |> ignore
                if not (String.IsNullOrWhiteSpace message.reasoning) then
                    builder.AppendLine("> 思考过程").AppendLine(">") |> ignore
                    for line in message.reasoning.Split '\n' do
                        builder.AppendLine("> " + line.TrimEnd()) |> ignore
                    builder.AppendLine() |> ignore
                for call in message.toolCalls do
                    builder.AppendLine(sprintf "**调用工具 `%s`**" call.name).AppendLine() |> ignore
                    if not (String.IsNullOrWhiteSpace call.argumentsJson) then
                        builder.AppendLine("```json").AppendLine(call.argumentsJson).AppendLine("```").AppendLine() |> ignore
                    match call.result with
                    | Some result when not (String.IsNullOrWhiteSpace result) ->
                        builder.AppendLine("```json").AppendLine(result).AppendLine("```").AppendLine() |> ignore
                    | _ -> ()
                if not (String.IsNullOrWhiteSpace message.text) then
                    builder.AppendLine(message.text).AppendLine() |> ignore
                for attachment in message.attachments do
                    builder.AppendLine(sprintf "*附件：%s（%s）*" attachment.fileName (AttachmentRef.formatSize attachment.size)).AppendLine()
                    |> ignore
        builder.ToString()
