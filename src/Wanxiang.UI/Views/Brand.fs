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
    /// 资产按方形安全区绘制；Stretch 用 Uniform 而非 UniformToFill，
    /// 非方形图只露出底色而不裁剪。底色与字色都是 Tokens 跟踪画笔，
    /// 切主题时原地换色，无需另订 Tokens.Changed。
    let logo (size: float) : Control =
        let tile =
            Border(
                Width = size,
                Height = size,
                CornerRadius = Avalonia.CornerRadius(size * Tokens.logoRadiusRatio),
                Background = Tokens.surface,
                ClipToBounds = true)
        match bitmap with
        | Some image ->
            // 资产本身带透明底：不再垫白色圆角方块（视觉走查：深色/纸色背景上露出白方块，第 209 节）
            tile.Background <- Brushes.Transparent
            tile.Child <- Image(Source = image, Stretch = Stretch.Uniform)
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

/// 导出对话为 Markdown：文件名与转录正文由 Wanxiang2.ExportFileName / TranscriptMarkdown 生成。
module Export =

    let safeFileName (title: string) = ExportFileName.ofTitle title

    let private toTranscript (m: MessageView) : TranscriptMessage =
        { role = m.role
          committedAt = m.committedAt
          reasoning = m.reasoning
          toolCalls = m.toolCalls |> List.map (fun c -> { name = c.name; argumentsJson = c.argumentsJson; result = c.result })
          toolResults = m.toolResults
          text = m.text
          attachments =
            m.attachments
            |> List.map (fun a -> { fileName = a.fileName; sizeText = AttachmentRef.formatSize a.size }) }

    /// 可见性判断在 TranscriptMarkdown 里，这里不再过滤。
    let toMarkdown (title: string) (messages: MessageView list) : string =
        TranscriptMarkdown.render title (messages |> List.map toTranscript)
