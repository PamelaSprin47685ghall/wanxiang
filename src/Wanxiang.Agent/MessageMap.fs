namespace Wanxiang.Agent

open System
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.Extensions.AI
open AGUI.Abstractions
open Wanxiang.Core

/// MAF `ChatMessage` ⇄ AG-UI 消息对象。
///
/// 决策 19/20（现行）：**AG-UI 消息对象是落盘与线上的权威表示**，
/// MAF `ChatMessage` 只是 Agent 运行时的内存形态。
///
/// 放在 Agent 层（而不是 Wanxiang.Agui）是因为它需要 `Microsoft.Extensions.AI`，
/// 而线协议工程不该为此背上 Agent 栈。
module MessageMap =

    let private aguiOptions = AGUIJsonSerializerContext.Default.Options

    /// MAF → AG-UI 消息对象（`JsonNode`，可直接进 `MESSAGES_SNAPSHOT` / 账本）。
    ///
    /// **序列化陷阱（实测）**：必须按 `typeof<AGUIMessage>` 序列化。
    /// 按运行期类型（`AGUIUserMessage` 等）会静默丢掉 `content`——
    /// converter 挂在基类上，子类自己的序列化上下文没带它。
    let toAgui (msg: ChatMessage) : JsonNode =
        let list = AGUIChatMessageExtensions.AsAGUIMessages([ msg ], aguiOptions) |> List.ofSeq
        match list with
        | [ m ] -> JsonNode.Parse(JsonSerializer.Serialize(m, typeof<AGUIMessage>, aguiOptions))
        | _ -> failwithf "expected exactly one AG-UI message, got %d" list.Length

    /// AG-UI 消息对象 → MAF `ChatMessage`。
    let tryToMaf (node: JsonNode) : ChatMessage option =
        try
            let json = node.ToJsonString()
            let msg = JsonSerializer.Deserialize<AGUIMessage>(json, aguiOptions)
            AGUIChatMessageExtensions.AsChatMessages([ msg ]) |> Seq.tryHead
        with _ -> None
