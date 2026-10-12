namespace Wanxiang.Tests

open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Xunit
open Wanxiang.UI

/// 核心对话旅程（决策 220–222）：最新回复的键盘动作、失败后重试不再多问、空对话起手式。
module ConversationJourneyTests =

    let private keyEvent key modifiers = KeyEventArgs(Key = key, KeyModifiers = modifiers)

    let private msg role text =
        { MessageView.empty with role = role; text = text }

    // ---------- 纯逻辑 ----------

    [<Fact>]
    let ``latest reply text skips empty assistant tail and ignores earlier turns`` () =
        let messages =
            [ msg "user" "q1"; msg "assistant" "a1"
              msg "user" "q2"; msg "assistant" "a2"; msg "tool" ""; msg "assistant" "" ]
        Assert.Equal(Some "a2", ReplyTail.latestText messages)
        // 尾部是用户消息：没有可复制的「最新回复」，不回退去复制上一轮。
        Assert.Equal(None, ReplyTail.latestText (messages @ [ msg "user" "q3" ]))

    [<Fact>]
    let ``regenerate needs confirmation only when the tail has something to lose`` () =
        let failedEmpty = [ msg "user" "q"; msg "assistant" "" ]
        Assert.False(ReplyTail.hasLosableContent failedEmpty)
        Assert.False(ReplyTail.hasLosableContent [ msg "user" "q" ])
        Assert.True(ReplyTail.hasLosableContent [ msg "user" "q"; msg "assistant" "partial" ])
        Assert.True(ReplyTail.hasLosableContent [ msg "user" "q"; { msg "assistant" "" with reasoning = "thinking" } ])

    // ---------- 快捷键 ----------

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``copy and regenerate latest reply have shortcuts that avoid browser reload`` () =
        Headless.run (fun () ->
        Assert.Equal(CopyLastReply, ShortcutRouter.resolve (keyEvent Key.C (KeyModifiers.Control ||| KeyModifiers.Shift)))
        Assert.Equal(RegenerateLast, ShortcutRouter.resolve (keyEvent Key.G (KeyModifiers.Control ||| KeyModifiers.Shift)))
        Assert.Equal(RegenerateLast, ShortcutRouter.resolve (keyEvent Key.G (KeyModifiers.Meta ||| KeyModifiers.Shift)))
        // Ctrl+Shift+R 是浏览器强制刷新，绝不占用；裸 Ctrl+C 仍归文本复制。
        Assert.Equal(NoShortcut, ShortcutRouter.resolve (keyEvent Key.R (KeyModifiers.Control ||| KeyModifiers.Shift)))
        Assert.Equal(NoShortcut, ShortcutRouter.resolve (keyEvent Key.C KeyModifiers.Control))
        )

    // ---------- 空对话起手式 ----------

    let rec private descendants (c: Control) : Control seq =
        seq {
            yield c
            match c with
            | :? Panel as p -> for child in p.Children do yield! descendants child
            | :? Decorator as d when not (isNull d.Child) -> yield! descendants d.Child
            | :? ContentControl as cc when (cc.Content :? Control) -> yield! descendants (cc.Content :?> Control)
            | _ -> ()
        }

    let private chatView () =
        let actions =
            { renameTitle = ignore
              openSessionSettings = ignore
              forkFromHere = ignore
              stopGeneration = ignore
              requestOlderHistory = ignore
              retryLast = ignore
              toggleSidebar = ignore
              focusHome = ignore
              message =
                { copyText = ignore
                  regenerate = ignore
                  editAndFork = ignore
                  deleteMessage = ignore
                  downloadAttachment = ignore
                  openLink = ignore } }
        let chat = ChatView(actions, Brand.logo)
        chat.Build()
        chat

    let private chips (chat: ChatView) =
        descendants chat |> Seq.filter (fun c -> c.Name = "starter-chip" && c.IsVisible) |> List.ofSeq

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``empty conversation offers starters that seed the composer without sending`` () =
        Headless.run (fun () ->
        let chat = chatView ()
        let mutable seeded = []
        chat.SetStarterHandler(fun seed -> seeded <- seed :: seeded)
        chat.ShowEmpty(EmptyConversation, None)
        let found = chips chat
        Assert.Equal(ChatStarters.items.Length, found.Length)
        let first = found.Head
        first.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = first))
        Dispatcher.UIThread.RunJobs()
        Assert.Equal<string list>([ snd ChatStarters.items.Head ], seeded)
        // 未连接等阻断态不放起手式：先解决阻断，不让用户填了也发不出去。
        chat.ShowEmpty(NotConnected, Some("连接主机", ignore))
        Assert.Empty(chips chat)
        )

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``starters stay hidden when no handler is wired`` () =
        Headless.run (fun () ->
        let chat = chatView ()
        chat.ShowEmpty(EmptyConversation, None)
        Assert.Empty(chips chat)
        )
