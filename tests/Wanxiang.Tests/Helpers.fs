module Wanxiang.Tests.Helpers

open System
open System.IO
open Wanxiang.Core

/// 测试辅助：临时目录。
let tempDir () =
    let dir = Path.Combine(Path.GetTempPath(), "wanxiang-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let cleanup (dir: string) =
    try Directory.Delete(dir, true) with _ -> ()

let userMessageJson (text: string) : System.Text.Json.Nodes.JsonNode =
    let o = System.Text.Json.Nodes.JsonObject()
    o["role"] <- "user"
    let contents = System.Text.Json.Nodes.JsonArray()
    let tc = System.Text.Json.Nodes.JsonObject()
    tc["text"] <- text
    contents.Add tc
    o["contents"] <- contents
    o

let assistantMessageJson (text: string) : System.Text.Json.Nodes.JsonNode =
    let o = System.Text.Json.Nodes.JsonObject()
    o["role"] <- "assistant"
    let contents = System.Text.Json.Nodes.JsonArray()
    let tc = System.Text.Json.Nodes.JsonObject()
    tc["text"] <- text
    contents.Add tc
    o["contents"] <- contents
    o

let testConfig () =
    { provider = "openai"
      model = "test-model"
      instructions = None
      tools = []
      temperature = None
      topP = None
      maxTokens = None
      thinkingBudget = None
      extraJson = None }

let newConversationId () = Guid.NewGuid()

/// 判定一个控件是不是万象的图标。
///
/// **单一判据**：`Icons` 模块产出的都是原生 `Image`，其 `Source` 是官方
/// `LucideImageExtension` 返回的 `DrawingImage`（见 `Design/Icons.fs`）。
///
/// 测试 MUST 用本函数而不是自己写类型匹配：图标实现换过一次
/// （自绘 `Path` → 官方 `Path` → 官方 `DrawingImage`），每换一次
/// 分散在用例里的类型判断就会集体失配。判据集中在这里，换实现只改一处。
let isIcon (control: Avalonia.Controls.Control) : bool =
    match control with
    | :? Avalonia.Controls.Image as image -> image.Source :? Avalonia.Media.DrawingImage
    | _ -> false
