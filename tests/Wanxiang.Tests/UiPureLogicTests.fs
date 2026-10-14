namespace Wanxiang.Tests

open System
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Media
open Xunit
open Wanxiang.Core
open Wanxiang.UI

module UiPureLogicTests =

    // =========================================================================
    // 1. MediaTypes.fs 纯函数测试（扩展名到 MIME 推断、边界与图片判断）
    // =========================================================================
    module MediaTypesTests =

        [<Fact>]
        let ``ofFileName should resolve known extensions correctly`` () =
            Assert.Equal("image/png", MediaTypes.ofFileName "avatar.PNG")
            Assert.Equal("image/jpeg", MediaTypes.ofFileName "photo.jpg")
            Assert.Equal("image/jpeg", MediaTypes.ofFileName "banner.jpeg")
            Assert.Equal("image/gif", MediaTypes.ofFileName "anim.gif")
            Assert.Equal("image/webp", MediaTypes.ofFileName "hero.webp")
            Assert.Equal("image/svg+xml", MediaTypes.ofFileName "icon.svg")
            Assert.Equal("application/pdf", MediaTypes.ofFileName "doc.pdf")
            Assert.Equal("text/markdown", MediaTypes.ofFileName "README.md")
            Assert.Equal("text/markdown", MediaTypes.ofFileName "spec.markdown")
            Assert.Equal("text/plain", MediaTypes.ofFileName "code.fs")
            Assert.Equal("text/plain", MediaTypes.ofFileName "script.py")
            Assert.Equal("application/json", MediaTypes.ofFileName "config.json")
            Assert.Equal("application/toml", MediaTypes.ofFileName "wanxiang.toml")
            Assert.Equal("application/zip", MediaTypes.ofFileName "archive.zip")
            Assert.Equal("application/gzip", MediaTypes.ofFileName "logs.gz")
            Assert.Equal("application/x-tar", MediaTypes.ofFileName "bundle.tar")

        [<Fact>]
        let ``ofFileName should handle empty, whitespace and unknown extensions`` () =
            Assert.Equal("application/octet-stream", MediaTypes.ofFileName null)
            Assert.Equal("application/octet-stream", MediaTypes.ofFileName "")
            Assert.Equal("application/octet-stream", MediaTypes.ofFileName "   ")
            Assert.Equal("application/octet-stream", MediaTypes.ofFileName "unknown_binary.bin")
            Assert.Equal("application/octet-stream", MediaTypes.ofFileName "no_extension_file")

        [<Fact>]
        let ``isImage should distinguish image media types correctly`` () =
            Assert.True(MediaTypes.isImage "image/png")
            Assert.True(MediaTypes.isImage "IMAGE/JPEG")
            Assert.True(MediaTypes.isImage "image/svg+xml")
            Assert.False(MediaTypes.isImage "application/pdf")
            Assert.False(MediaTypes.isImage "text/plain")
            Assert.False(MediaTypes.isImage null)
            Assert.False(MediaTypes.isImage "")
