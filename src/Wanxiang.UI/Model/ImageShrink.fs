namespace Wanxiang.UI

open System
open System.IO
open SkiaSharp

/// 决策 332：超大图片在客户端先缩小 / 重新压缩再上传。
/// 宿主只留 Skia 解码、摆正、缩放、编码；判定、几何、候选格式、命名与文案归包 ImageFit / ImageShrink（同名模块合并）。
/// 触发：超过 8 MiB（主机内联上限，模型收不到），或最长边超过 4096 px（模型服务会自己缩，白占上传与额度）。
/// 产物：最长边不超过 4096 px；有透明通道的 PNG/WebP 先试 PNG，其余（或 PNG 仍太大）转 JPEG，质量从 85 往下试，
/// 仍超限再按 0.75 缩边。动图 GIF 不处理（压成单帧会丢动画），仍按原规则拒绝。
/// 手机照片的 EXIF 方向会在重新编码前摆正，否则去掉 EXIF 后图会躺倒。
module ImageShrink =

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

    /// 需要缩小吗？超过上限时不必再读尺寸。
    let needsShrink (fileName: string) (bytes: byte[]) : bool =
        let byteCount = int64 bytes.Length
        let dims = if byteCount > ImageShrink.maxBytes then None else tryDimensions bytes
        ImageShrink.needsShrinkFor fileName byteCount dims

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

    /// 缩小。Ok None = 不需要；Ok(Some r) = 已缩小；Error = 做不到（给用户的完整说明）。
    let shrink (fileName: string) (bytes: byte[]) : Result<ShrunkImage option, string> =
        if not (needsShrink fileName bytes) then Ok None
        elif isAnimated bytes then
            if int64 bytes.Length > ImageShrink.maxBytes then
                Error(ImageFit.animatedTooLarge fileName)
            else Ok None
        else
            try
                use data = SKData.CreateCopy bytes
                use codec = SKCodec.Create data
                if isNull codec then Error(ImageFit.unreadable fileName)
                else
                    let info = codec.Info
                    use decoded = SKBitmap.Decode codec
                    if isNull decoded then Error(ImageFit.unreadable fileName)
                    else
                        let upright = orient decoded codec.EncodedOrigin
                        let ow, oh = upright.Width, upright.Height
                        let hasAlpha = ImageFit.keepsAlpha (info.AlphaType = SKAlphaType.Opaque) (MediaTypes.ofFileName fileName)
                        let mutable limit = ImageFit.maxDimension
                        let mutable result: ShrunkImage option = None
                        let mutable attempts = 0
                        while result.IsNone && attempts < ImageFit.maxAttempts do
                            attempts <- attempts + 1
                            let w, h = ImageFit.fit (ow, oh) limit
                            use scaled = if w = ow && h = oh then upright.Copy() else resize upright w h
                            let candidates =
                                ImageFit.candidates hasAlpha
                                |> List.map (fun (encoding, ext, mt) ->
                                    let make () =
                                        match encoding with
                                        | ImageEncoding.Png -> encode scaled SKEncodedImageFormat.Png (ImageFit.quality encoding)
                                        | ImageEncoding.Jpeg q when hasAlpha ->
                                            // JPEG 没有透明通道：铺白底再编码。
                                            use flat = flatten scaled
                                            encode flat SKEncodedImageFormat.Jpeg q
                                        | ImageEncoding.Jpeg q -> encode scaled SKEncodedImageFormat.Jpeg q
                                    make, ext, mt)
                            let fitted =
                                candidates
                                |> Seq.map (fun (make, ext, mt) -> make (), ext, mt)
                                |> Seq.tryFind (fun (b, _, _) -> b.Length > 0 && int64 b.Length <= ImageShrink.maxBytes)
                            match fitted with
                            | Some(b, ext, mt) ->
                                result <-
                                    Some
                                        { bytes = b
                                          fileName = ImageFit.renamed fileName ext
                                          mediaType = mt
                                          originalBytes = int64 bytes.Length
                                          originalWidth = ow
                                          originalHeight = oh
                                          width = w
                                          height = h }
                            | None -> limit <- ImageFit.nextLimit (w, h)
                        if not (obj.ReferenceEquals(upright, decoded)) then upright.Dispose()
                        match result with
                        | Some r -> Ok(Some r)
                        | None -> Error(ImageFit.stillTooLarge fileName)
            with ex ->
                Error(ImageFit.unreadableBecause fileName ex.Message)

    /// 决策 342：平台可以换上异步的缩小实现（PWA 用浏览器原生解码与 Canvas，不卡 wasm 单线程）。
    /// 参数：文件名、原始字节、媒体类型；返回 None 表示平台做不到，回退到上面的 Skia 实现。
    let mutable platformShrinker: (string -> byte[] -> string -> Async<Result<ShrunkImage, string> option>) option = None

    /// 异步缩小：先做与 `shrink` 相同的前置判断（不需要 / 动图），再优先交给平台实现。
    let shrinkAsync (fileName: string) (bytes: byte[]) : Async<Result<ShrunkImage option, string>> =
        async {
            if not (needsShrink fileName bytes) then return Ok None
            elif isAnimated bytes then return shrink fileName bytes
            else
                match platformShrinker with
                | Some f ->
                    let! r = f fileName bytes (MediaTypes.ofFileName fileName)
                    match r with
                    | Some(Ok s) -> return Ok(Some { s with fileName = ImageFit.renamedFor fileName s.mediaType; originalBytes = int64 bytes.Length })
                    | Some(Error e) -> return Error e
                    | None -> return shrink fileName bytes
                | None -> return shrink fileName bytes
        }
