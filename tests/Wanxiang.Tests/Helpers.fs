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
      maxTokens = None
      extraJson = None }

let newConversationId () = Guid.NewGuid()
