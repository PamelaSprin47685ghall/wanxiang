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

/// 导出会话为 Markdown。
module Export =

    /// 标题不是路径；不同平台统一处理分隔符、控制字符和过长文件名。
    let safeFileName (title: string) =
        let source = if String.IsNullOrWhiteSpace title then "会话" else title
        let cleaned =
            source |> Seq.map (fun c -> if Char.IsControl c || "<>:\"/\\|?*".Contains c then '_' else c)
            |> Seq.toArray |> String
        let trimmed = cleaned.Trim().Trim('.')
        let trimmed = if String.IsNullOrWhiteSpace trimmed then "会话" else trimmed
        let elements = Globalization.StringInfo.GetTextElementEnumerator trimmed
        let builder = Text.StringBuilder()
        let mutable bytes = 0
        let mutable doneName = false
        while not doneName && elements.MoveNext() do
            let element = elements.GetTextElement()
            let size = Text.Encoding.UTF8.GetByteCount element
            if bytes + size > 220 then doneName <- true
            else
                builder.Append element |> ignore
                bytes <- bytes + size
        let bounded = if builder.Length = 0 then "会话" else builder.ToString()
        bounded + ".md"

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
        let appendCode (language: string) (text: string) =
            // 工具输出本身可能包含 Markdown 围栏，不能提前关闭导出的代码块。
            let mutable run, longest = 0, 0
            for c in text do
                run <- if c = '`' then run + 1 else 0
                longest <- max longest run
            let fence = String('`', max 3 (longest + 1))
            builder.AppendLine(fence + language).AppendLine(text).AppendLine(fence).AppendLine() |> ignore
        builder.AppendLine("# " + title).AppendLine() |> ignore
        // 导出按已提交消息逐条保留。不能沿用 UI 合并逻辑丢掉没有调用卡片的工具结果。
        for message in messages do
            if MessageView.hasVisibleBody message || not (List.isEmpty message.toolResults) then
                builder.AppendLine("## " + roleTitle message.role).AppendLine() |> ignore
                match message.committedAt with
                | Some at -> builder.AppendLine(at.ToString("o", Globalization.CultureInfo.InvariantCulture)).AppendLine() |> ignore
                | None -> ()
                if not (String.IsNullOrWhiteSpace message.reasoning) then
                    builder.AppendLine("> 思考过程").AppendLine(">") |> ignore
                    for line in message.reasoning.Split '\n' do
                        builder.AppendLine("> " + line.TrimEnd()) |> ignore
                    builder.AppendLine() |> ignore
                for call in message.toolCalls do
                    builder.AppendLine(sprintf "**调用工具 `%s`**" call.name).AppendLine() |> ignore
                    if not (String.IsNullOrWhiteSpace call.argumentsJson) then
                        appendCode "json" call.argumentsJson
                    match call.result with
                    | Some result when not (String.IsNullOrWhiteSpace result) ->
                        appendCode "json" result
                    | _ -> ()
                for callId, result in message.toolResults do
                    builder.AppendLine(sprintf "**工具结果 `%s`**" callId).AppendLine() |> ignore
                    appendCode "json" result
                if not (String.IsNullOrWhiteSpace message.text) then
                    builder.AppendLine(message.text).AppendLine() |> ignore
                for attachment in message.attachments do
                    builder.AppendLine(sprintf "*附件：%s（%s）*" attachment.fileName (AttachmentRef.formatSize attachment.size)).AppendLine()
                    |> ignore
        builder.ToString()
