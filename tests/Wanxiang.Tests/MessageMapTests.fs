namespace Wanxiang.Tests

open System.Text.Json.Nodes
open Microsoft.Extensions.AI
open Microsoft.Extensions.AI
open Xunit
open Wanxiang.Agent
open Wanxiang.Core

/// MAF `ChatMessage` ⇄ AG-UI 消息往返（决策 19/20 现行：AG-UI 为权威表示）。
module MessageMapTests =

    // ---------- 往返无损 ----------

    [<Fact>]
    let ``user text round trips through agui`` () =
        let original = ChatMessage(ChatRole.User, "你好，世界")
        let agui = MessageMap.toAgui original
        Assert.Equal("user", agui.["role"].GetValue<string>())
        Assert.Equal("你好，世界", agui.["content"].GetValue<string>())
        let back = MessageMap.tryToMaf agui
        Assert.True(back.IsSome, "必须能读回")
        Assert.Equal(original.Text, back.Value.Text)

    [<Fact>]
    let ``assistant text round trips through agui`` () =
        let original = ChatMessage(ChatRole.Assistant, "回复内容")
        let agui = MessageMap.toAgui original
        Assert.Equal("assistant", agui.["role"].GetValue<string>())
        let back = MessageMap.tryToMaf agui
        Assert.True(back.IsSome)
        Assert.Equal(original.Text, back.Value.Text)

    [<Fact>]
    let ``system message round trips through agui`` () =
        let original = ChatMessage(ChatRole.System, "你是一个助手")
        let agui = MessageMap.toAgui original
        Assert.Equal("system", agui.["role"].GetValue<string>())
        let back = MessageMap.tryToMaf agui
        Assert.True(back.IsSome)
        Assert.Equal(original.Text, back.Value.Text)

    // ---------- 陷阱回归 ----------

    [<Fact>]
    let ``content survives serialization`` () =
        // 陷阱回归：按运行期类型序列化会静默丢 content（实测），
        // 必须按 typeof<AGUIMessage>。产物缺 content 即回归。
        let msg = ChatMessage(ChatRole.User, "内容必须在")
        let agui = MessageMap.toAgui msg
        Assert.NotNull(agui.["content"])

    [<Fact>]
    let ``garbage node returns none instead of throwing`` () =
        Assert.True(MessageMap.tryToMaf (JsonValue.Create 42) |> Option.isNone)
