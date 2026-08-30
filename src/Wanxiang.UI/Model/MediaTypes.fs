namespace Wanxiang.UI

open System
open System.IO

/// 由文件名推断 MIME 类型。
///
/// 服务端会再嗅探一次真实内容，这里只是给上传声明一个合理的初值，
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
