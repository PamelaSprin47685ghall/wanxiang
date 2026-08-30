namespace Wanxiang.Agent

open System
open System.Text
open Microsoft.Extensions.AI

/// 一个传输能直接消费的二进制媒体类型。
///
/// 三种传输能吃的东西差别很大，而「递过去被拒」和「悄悄退化成一行说明」
/// 都是坏结果，所以能力必须由调用方显式声明，转换层只按声明产出。
type MediaSupport =
    { image: bool
      pdf: bool
      audio: bool }

module MediaSupport =

    /// OpenAI 兼容传输：chat/completions 的 image_url 只吃图片。
    let openAiCompatible = { image = true; pdf = false; audio = false }

    /// Anthropic Messages API：图片与 PDF（document 块）；音频入参该协议没有。
    let anthropic = { image = true; pdf = true; audio = false }

    /// Gemini：inlineData 通吃图片、PDF、音频。
    let gemini = { image = true; pdf = true; audio = true }

    let ofProviderKind (kind: string) =
        match kind with
        | "anthropic" -> anthropic
        | "gemini" -> gemini
        | _ -> openAiCompatible

/// 把消息里的附件引用变成 Provider 能真正消费的内容。
///
/// 附件上传、内容寻址与下载早就打通，但历史上从没有人把 blob 递给模型——
/// 用户传了图片，模型看不到。这里补上这一步：
/// 图片、PDF、音频在传输支持时走 `DataContent`（多模态），
/// 文本类内联为带文件名的代码块，其余留一行元数据说明。
module AttachmentContent =

    /// 主流视觉模型普遍接受的图片类型。
    let private imageTypes =
        set [ "image/png"; "image/jpeg"; "image/jpg"; "image/gif"; "image/webp" ]

    /// 可直接内联为文本的类型。
    let private textTypes =
        set [ "application/json"
              "application/xml"
              "application/x-yaml"
              "application/yaml"
              "application/javascript"
              "application/typescript"
              "application/toml"
              "application/sql" ]

    /// 内联文本上限：超过则截断，保住上下文预算。
    let maxInlineTextBytes = 128 * 1024

    /// 图片上限：超过则降级为元数据说明（多数 Provider 也不接受更大的）。
    let maxInlineImageBytes = 8 * 1024 * 1024

    /// PDF 与音频上限。Gemini 的单请求内联上限是 20MiB，Anthropic 是 32MiB；
    /// 取 16MiB 兼顾两者，超过就老实说没发。
    let maxInlineBinaryBytes = 16 * 1024 * 1024

    /// 各家视觉模型普遍接受的文档类型。目前只有 PDF 是跨家共识。
    let private pdfTypes = set [ "application/pdf" ]

    /// Gemini 文档里列出的音频类型。
    let private audioTypes =
        set [ "audio/wav"; "audio/mpeg"; "audio/mp3"; "audio/aiff"
              "audio/aac"; "audio/ogg"; "audio/flac" ]

    let private normalize (mediaType: string) =
        if isNull mediaType then "" else mediaType.Trim().ToLowerInvariant()

    let isImage (mediaType: string) : bool =
        imageTypes.Contains(normalize mediaType)

    let isPdf (mediaType: string) : bool =
        pdfTypes.Contains(normalize mediaType)

    let isAudio (mediaType: string) : bool =
        let m = normalize mediaType
        audioTypes.Contains m || m.StartsWith("audio/", StringComparison.Ordinal)

    let isText (mediaType: string) : bool =
        let m = normalize mediaType
        m.StartsWith("text/", StringComparison.Ordinal) || textTypes.Contains m

    let private formatSize (n: int64) =
        if n < 1024L then sprintf "%d B" n
        elif n < 1024L * 1024L then sprintf "%.1f KiB" (float n / 1024.0)
        else sprintf "%.1f MiB" (float n / (1024.0 * 1024.0))

    /// 根据文件名推断 Markdown 代码块语言，帮模型正确理解内联文本。
    let private fenceLanguage (fileName: string) =
        let ext =
            if String.IsNullOrWhiteSpace fileName then ""
            else IO.Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant()
        match ext with
        | "md" | "markdown" -> "markdown"
        | "json" -> "json"
        | "yml" | "yaml" -> "yaml"
        | "toml" -> "toml"
        | "xml" | "html" | "htm" -> "xml"
        | "fs" | "fsx" -> "fsharp"
        | "cs" -> "csharp"
        | "ts" | "tsx" -> "typescript"
        | "js" | "jsx" -> "javascript"
        | "py" -> "python"
        | "rs" -> "rust"
        | "go" -> "go"
        | "sh" | "bash" -> "bash"
        | "sql" -> "sql"
        | "csv" -> "csv"
        | _ -> ""

    let private decodeText (bytes: byte[]) : string * bool =
        let text = Encoding.UTF8.GetString bytes
        if bytes.Length > maxInlineTextBytes then
            let cut = Encoding.UTF8.GetString(bytes, 0, maxInlineTextBytes)
            cut, true
        else
            text, false

    /// 解析单个附件引用。`loadBlob` 返回 None 表示 blob 已丢失。
    /// `support` 声明当前传输吃得下什么，吃不下的类型给出**说明原因**的占位。
    let resolve (support: MediaSupport) (loadBlob: string -> byte[] option) (r: AttachmentReference) : AIContent list =
        let describe (note: string) =
            [ TextContent(sprintf "【附件】%s（%s，%s）%s" r.fileName r.mediaType (formatSize r.size) note) :> AIContent ]
        /// 二进制媒体的共同路径：能力允许且不超限就真发，否则说清为什么没发。
        let binary (label: string) (allowed: bool) (limit: int) (bytes: byte[]) =
            if not allowed then
                describe " — 当前服务商的传输不接受该类型，模型只看到这条说明。"
            elif bytes.LongLength > int64 limit then
                describe (sprintf " — 超过 %s 内联上限，未发送给模型。" (formatSize(int64 limit)))
            else
                [ TextContent(sprintf "【%s】%s" label r.fileName) :> AIContent
                  DataContent(ReadOnlyMemory bytes, normalize r.mediaType) :> AIContent ]
        match loadBlob r.sha256 with
        | None -> describe " — 内容已丢失，无法读取。"
        | Some bytes when bytes.Length = 0 -> describe " — 空文件。"
        | Some bytes ->
            if isImage r.mediaType then
                binary "附件图片" support.image maxInlineImageBytes bytes
            elif isPdf r.mediaType then
                binary "附件文档" support.pdf maxInlineBinaryBytes bytes
            elif isAudio r.mediaType then
                binary "附件音频" support.audio maxInlineBinaryBytes bytes
            elif isText r.mediaType then
                let text, truncated = decodeText bytes
                let lang = fenceLanguage r.fileName
                let suffix = if truncated then sprintf "\n…（已截断，原文件 %s）" (formatSize r.size) else ""
                let body = sprintf "【附件】%s\n```%s\n%s\n```%s" r.fileName lang text suffix
                [ TextContent body :> AIContent ]
            else
                describe " — 二进制内容不进上下文（会挤掉真正有用的历史），模型只看到这条说明。"

    /// 解析一条消息的全部附件，追加到其内容末尾（原顺序）。
    let appendTo
        (support: MediaSupport)
        (loadBlob: string -> byte[] option)
        (refs: AttachmentReference list)
        (msg: ChatMessage)
        : ChatMessage =
        for r in refs do
            for content in resolve support loadBlob r do
                msg.Contents.Add content
        msg
