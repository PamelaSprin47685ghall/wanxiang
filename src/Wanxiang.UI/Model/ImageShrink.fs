namespace Wanxiang.UI

open System
open System.IO
open SkiaSharp

/// 一次自动缩小的结果（决策 332）。界面用它告诉用户「原图多大、实际发出去多大」。
type ShrunkImage = {
    bytes: byte[]
    fileName: string
    mediaType: string
    originalBytes: int64
    originalWidth: int
    originalHeight: int
    width: int
    height: int
}

/// 决策 332：超大图片在客户端先缩小 / 重新压缩再上传。
/// 触发：超过 8 MiB（主机内联上限，模型收不到），或最长边超过 4096 px（模型服务会自己缩，白占上传与额度）。
/// 产物：最长边不超过 4096 px；有透明通道的 PNG/WebP 先试 PNG，其余（或 PNG 仍太大）转 JPEG，质量从 85 往下试，
/// 仍超限再按 0.75 缩边。动图 GIF 不处理（压成单帧会丢动画），仍按原规则拒绝。
/// 手机照片的 EXIF 方向会在重新编码前摆正，否则去掉 EXIF 后图会躺倒。
module ImageShrink =

    let maxBytes = MediaTypes.maxImageBytes
    let maxDimension = 4096

    /// 只读文件头拿尺寸，不解码像素。读不出返回 None。
    let tryDimensions (bytes: byte[]) : (int * int) option =
        try
            use data = SKData.CreateCopy bytes
            use codec = SKCodec.Create data
            if isNull codec then None
            else
                let info = codec.Info
                match codec.EncodedOrigin with
                | SKEncodedOrigin.LeftTop | SKEncodedOrigin.RightTop | SKEncodedOrigin.RightBottom | SKEncodedOrigin.LeftBottom ->
                    Some(info.Height, info.Width)
                | _ -> Some(info.Width, info.Height)
        with _ -> None

    let private isAnimated (bytes: byte[]) =
        try
            use data = SKData.CreateCopy bytes
            use codec = SKCodec.Create data
            not (isNull codec) && codec.FrameCount > 1
        with _ -> false

    /// 需要缩小吗？只看模型收的四种图片。
    let needsShrink (fileName: string) (bytes: byte[]) : bool =
        let mediaType = MediaTypes.ofFileName fileName
        if not (MediaTypes.isModelImage mediaType) then false
        elif int64 bytes.Length > maxBytes then true
        else
            match tryDimensions bytes with
            | Some(w, h) -> max w h > maxDimension
            | None -> false

    /// 按 EXIF 方向摆正。
    let private orient (bitmap: SKBitmap) (origin: SKEncodedOrigin) : SKBitmap =
        let rotate (degrees: float32) (swap: bool) =
            let w, h = if swap then bitmap.Height, bitmap.Width else bitmap.Width, bitmap.Height
            let result = new SKBitmap(SKImageInfo(w, h, bitmap.ColorType, bitmap.AlphaType))
            use canvas = new SKCanvas(result)
            canvas.Translate(float32 w / 2.0f, float32 h / 2.0f)
            canvas.RotateDegrees degrees
            canvas.Translate(-(float32 bitmap.Width) / 2.0f, -(float32 bitmap.Height) / 2.0f)
            canvas.DrawBitmap(bitmap, 0.0f, 0.0f)
            result
        match origin with
        | SKEncodedOrigin.BottomRight -> rotate 180.0f false
        | SKEncodedOrigin.RightTop -> rotate 90.0f true
        | SKEncodedOrigin.LeftBottom -> rotate 270.0f true
        | _ -> bitmap

    let private resize (bitmap: SKBitmap) (w: int) (h: int) =
        bitmap.Resize(SKImageInfo(w, h, bitmap.ColorType, bitmap.AlphaType), SKSamplingOptions(SKCubicResampler.Mitchell))

    let private encode (bitmap: SKBitmap) (format: SKEncodedImageFormat) (quality: int) : byte[] =
        use image = SKImage.FromBitmap bitmap
        use data = image.Encode(format, quality)
        if isNull data then [||] else data.ToArray()

    /// JPEG 没有透明通道：铺白底再编码，免得透明处变黑。
    let private flatten (bitmap: SKBitmap) =
        let result = new SKBitmap(SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque))
        use canvas = new SKCanvas(result)
        canvas.Clear SKColors.White
        canvas.DrawBitmap(bitmap, 0.0f, 0.0f)
        result

    let private renamed (fileName: string) (extension: string) =
        let stem = Path.GetFileNameWithoutExtension fileName
        (if String.IsNullOrWhiteSpace stem then "image" else stem) + extension

    /// 缩小。Ok None = 不需要；Ok(Some r) = 已缩小；Error = 做不到（给用户的完整说明）。
    let shrink (fileName: string) (bytes: byte[]) : Result<ShrunkImage option, string> =
        if not (needsShrink fileName bytes) then Ok None
        elif isAnimated bytes then
            if int64 bytes.Length > maxBytes then
                Error(sprintf "没有添加「%s」：动图超过 8 MiB，缩小会丢掉动画。请先压缩动图，或换成静态图片。" fileName)
            else Ok None
        else
            try
                use data = SKData.CreateCopy bytes
                use codec = SKCodec.Create data
                if isNull codec then Error(sprintf "没有添加「%s」：图片无法读取，可能已损坏。请换一张图片。" fileName)
                else
                    let info = codec.Info
                    use decoded = SKBitmap.Decode codec
                    if isNull decoded then Error(sprintf "没有添加「%s」：图片无法读取，可能已损坏。请换一张图片。" fileName)
                    else
                        let upright = orient decoded codec.EncodedOrigin
                        let ow, oh = upright.Width, upright.Height
                        let hasAlpha =
                            info.AlphaType <> SKAlphaType.Opaque
                            && (MediaTypes.ofFileName fileName = "image/png" || MediaTypes.ofFileName fileName = "image/webp")
                        let fit (limit: int) =
                            let longest = max ow oh
                            if longest <= limit then ow, oh
                            else
                                let scale = float limit / float longest
                                max 1 (int (Math.Round(float ow * scale))), max 1 (int (Math.Round(float oh * scale)))
                        let mutable limit = maxDimension
                        let mutable result: ShrunkImage option = None
                        let mutable attempts = 0
                        while result.IsNone && attempts < 8 do
                            attempts <- attempts + 1
                            let w, h = fit limit
                            use scaled = if w = ow && h = oh then upright.Copy() else resize upright w h
                            let candidates =
                                [ if hasAlpha then yield (fun () -> encode scaled SKEncodedImageFormat.Png 100), ".png", "image/png"
                                  for q in [ 85; 75; 65 ] do
                                      yield (fun () ->
                                                  if hasAlpha then
                                                      use flat = flatten scaled
                                                      encode flat SKEncodedImageFormat.Jpeg q
                                                  else encode scaled SKEncodedImageFormat.Jpeg q),
                                            ".jpg",
                                            "image/jpeg" ]
                            let fitted =
                                candidates
                                |> Seq.map (fun (make, ext, mt) -> make (), ext, mt)
                                |> Seq.tryFind (fun (b, _, _) -> b.Length > 0 && int64 b.Length <= maxBytes)
                            match fitted with
                            | Some(b, ext, mt) ->
                                result <-
                                    Some
                                        { bytes = b
                                          fileName = renamed fileName ext
                                          mediaType = mt
                                          originalBytes = int64 bytes.Length
                                          originalWidth = ow
                                          originalHeight = oh
                                          width = w
                                          height = h }
                            | None -> limit <- int (float (max w h) * 0.75)
                        if not (obj.ReferenceEquals(upright, decoded)) then upright.Dispose()
                        match result with
                        | Some r -> Ok(Some r)
                        | None -> Error(sprintf "没有添加「%s」：自动缩小后仍超过 8 MiB。请截取一部分后再添加。" fileName)
            with ex ->
                Error(sprintf "没有添加「%s」：图片无法读取（%s）。请换一张图片。" fileName ex.Message)

    /// 给用户看的一句话：原图 vs 实际发送。
    let note (r: ShrunkImage) =
        sprintf "已自动缩小：原图 %s（%d×%d）→ 发送 %s（%d×%d）"
            (AttachmentRef.formatSize r.originalBytes) r.originalWidth r.originalHeight
            (AttachmentRef.formatSize (int64 r.bytes.Length)) r.width r.height
