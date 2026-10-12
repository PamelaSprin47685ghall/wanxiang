namespace Wanxiang.UI

open System
open System.IO

/// 由文件名推断 MIME 类型。
///
/// 主机会再嗅探一次真实内容，这里只是给上传声明一个合理的初值，
/// 让「这是图片还是文档」在界面上立刻正确。
module MediaTypes =

    let private table =
        [ ".png", "image/png"
          ".jpg", "image/jpeg"
          ".jpeg", "image/jpeg"
          ".gif", "image/gif"
          ".webp", "image/webp"
          ".bmp", "image/bmp"
          ".svg", "image/svg+xml"
          ".heic", "image/heic"
          ".heif", "image/heif"
          ".pdf", "application/pdf"
          ".txt", "text/plain"
          ".md", "text/markdown"
          ".markdown", "text/markdown"
          ".csv", "text/csv"
          ".json", "application/json"
          ".xml", "application/xml"
          ".yaml", "application/yaml"
          ".yml", "application/yaml"
          ".toml", "application/toml"
          ".html", "text/html"
          ".htm", "text/html"
          ".css", "text/css"
          ".js", "text/javascript"
          ".ts", "text/plain"
          ".fs", "text/plain"
          ".fsx", "text/plain"
          ".cs", "text/plain"
          ".py", "text/plain"
          ".rs", "text/plain"
          ".go", "text/plain"
          ".java", "text/plain"
          ".sh", "text/plain"
          ".sql", "text/plain"
          ".log", "text/plain"
          ".zip", "application/zip"
          ".gz", "application/gzip"
          ".tar", "application/x-tar" ]
        |> Map.ofList

    let ofFileName (fileName: string) : string =
        if String.IsNullOrWhiteSpace fileName then "application/octet-stream"
        else
            let extension = Path.GetExtension(fileName).ToLowerInvariant()
            table.TryFind extension |> Option.defaultValue "application/octet-stream"

    let isImage (mediaType: string) =
        not (isNull mediaType) && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)

    /// 决策 321：模型服务普遍接受的图片格式。其它图片（HEIC、BMP、SVG）模型收不到。
    let isModelImage (mediaType: string) =
        match (if isNull mediaType then "" else mediaType.ToLowerInvariant()) with
        | "image/png" | "image/jpeg" | "image/gif" | "image/webp" -> true
        | _ -> false

    /// 图片单张上限：与主机内联上限（8 MiB）一致，超过的图模型收不到，加入时就拒绝。
    let maxImageBytes = 8L * 1024L * 1024L

    /// 加入附件前的检查。Error 是给用户看的完整说明（问题 + 出路）。
    let validate (fileName: string) (size: int64) : Result<unit, string> =
        let mediaType = ofFileName fileName
        if size = 0L then Error "没有添加附件：文件是空的。请换一个文件。"
        elif size > 64L * 1024L * 1024L then Error "没有添加附件：文件超过 64 MiB 上限。请压缩或拆分后再试。"
        elif isImage mediaType && not (isModelImage mediaType) then
            Error(sprintf "没有添加「%s」：模型只能识别 PNG、JPG、GIF、WebP 图片。请转换格式后再添加（iPhone 可在 设置 › 相机 › 格式 选「兼容性最佳」）。" fileName)
        elif isModelImage mediaType && size > maxImageBytes then
            Error(sprintf "没有添加「%s」：图片超过 8 MiB，模型收不到。请压缩或截一部分后再添加。" fileName)
        else Ok()
