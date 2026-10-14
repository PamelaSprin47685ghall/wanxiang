namespace Wanxiang.Pwa

open System
open System.Text.Json.Nodes
open Wanxiang.Interop
open Wanxiang.UI

/// 决策 342：PWA 的图片缩小实现。浏览器解码失败或返回异常时给 None，由调用方回退到 Skia。
module BrowserImageShrink =

    let shrink (fileName: string) (bytes: byte[]) (mediaType: string) : Async<Result<ShrunkImage, string> option> =
        async {
            try
                let! json =
                    BrowserImage.Shrink(bytes, mediaType, ImageShrink.maxDimension, float ImageShrink.maxBytes)
                    |> Async.AwaitTask
                match JsonNode.Parse json with
                | :? JsonObject as o when o["ok"].GetValue<bool>() ->
                    let out = Convert.FromBase64String(o["b64"].GetValue<string>())
                    return
                        Some(
                            Ok
                                { bytes = out
                                  fileName = fileName
                                  mediaType = o["mediaType"].GetValue<string>()
                                  originalBytes = int64 bytes.Length
                                  originalWidth = o["originalWidth"].GetValue<int>()
                                  originalHeight = o["originalHeight"].GetValue<int>()
                                  width = o["width"].GetValue<int>()
                                  height = o["height"].GetValue<int>() }
                        )
                | :? JsonObject as o when (match o["error"] with null -> false | e -> e.GetValue<string>() = "too-large") ->
                    return Some(Error(sprintf "没有添加「%s」：自动缩小后仍超过 8 MiB。请截取一部分后再添加。" fileName))
                | _ -> return None
            with _ -> return None
        }
