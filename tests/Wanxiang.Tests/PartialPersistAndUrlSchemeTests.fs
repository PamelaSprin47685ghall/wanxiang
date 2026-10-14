module Wanxiang.Tests.PartialPersistAndUrlSchemeTests

// 决策 250 半截回复由主机保存 / 251 桌面 wanxiang:// 协议与单实例转发。

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Server
open Wanxiang.UI

let private partialMessage (text: string) : JsonNode =
    JsonNode.Parse(sprintf """{"role":"assistant","contents":[{"$type":"text","text":%s}]}""" (System.Text.Json.JsonSerializer.Serialize text))

let private tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "wx-r8-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    d

[<Fact>]
let ``250 generation.partial 编解码往返`` () =
    let conv, gen = Guid.NewGuid(), Guid.NewGuid()
    let err = GenerationError.create ProviderTimeout "超时"
    let ev = GenerationPartial {| conversationId = conv; generationId = gen; message = partialMessage "写到一半"; error = Some err |}
    match WireCodec.tryDecode (WireCodec.encode ev) with
    | Ok(GenerationPartial d) ->
        Assert.Equal(conv, d.conversationId)
        Assert.Equal(gen, d.generationId)
        Assert.Equal("写到一半", (MessageView.ofJson d.message).text)
        Assert.Equal(Some ProviderTimeout, d.error |> Option.map _.kind)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``251 第二实例把链接转发给主实例，主实例不在时转发失败`` () =
    let dir = tempDir ()
    let path = SingleInstance.socketPath dir
    Assert.False(SingleInstance.tryForward path (Some "wanxiang://open?c=x&m=1"))
    let got = System.Collections.Concurrent.BlockingCollection<string>()
    use _l = SingleInstance.listen path (fun url -> got.Add url) (fun () -> got.Add "<activate>")
    let link = sprintf "wanxiang://open?c=%O&m=7" (Guid.NewGuid())
    Assert.True(SingleInstance.tryForward path (Some link))
    let mutable received = ""
    Assert.True(got.TryTake(&received, 5000))
    Assert.Equal(link, received)
    Assert.True(SingleInstance.tryForward path None)
    Assert.True(got.TryTake(&received, 5000))
    Assert.Equal("<activate>", received)

[<Fact>]
let ``251 外部链接投给当前订阅者`` () =
    let link = "wanxiang://open?c=queued&m=1"
    let seen = ResizeArray<string>()
    ExternalLinks.subscribe seen.Add
    ExternalLinks.post link
    Assert.Contains(link, seen)

// ---------- 视觉走查 r8：右键正文要弹消息菜单，而不是主题自带的英文 Copy ----------
