namespace Wanxiang.Tests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.UI

/// 决策 240 / 242 界面：快速切换浮层、错误卡倒计时。
[<Trait("Category", "UI")>]
module ModelChipUiTests =

    let rec private descendants (control: Control) : seq<Control> =
        seq {
            yield control
            match control with
            | :? Panel as panel ->
                for child in panel.Children do
                    yield! descendants child
            | :? ContentControl as host ->
                match host.Content with
                | :? Control as child -> yield! descendants child
                | _ -> ()
            | :? Decorator as decorator when not (isNull decorator.Child) -> yield! descendants decorator.Child
            | _ -> ()
        }

    let private options =
        [ { providerId = "oa"; providerLabel = "OpenAI"; model = "gpt-4o"; isCurrent = true; isDefault = false }
          { providerId = "oa"; providerLabel = "OpenAI"; model = "o3"; isCurrent = false; isDefault = false }
          { providerId = "ds"; providerLabel = "DeepSeek"; model = "deepseek-chat"; isCurrent = false; isDefault = true } ]

    let private rowNames (root: Control) =
        descendants root
        |> Seq.choose (function
            | :? Border as b when b.Focusable -> Some(AutomationProperties.GetName b + "，" + AutomationProperties.GetHelpText b)
            | _ -> None)
        |> List.ofSeq

    [<Fact>]
    let ``switcher shows provider, default and current, plus the next reply note`` () =
        Headless.run (fun () ->
            let overlay = OverlayHost(Grid())
            let root = ModelSwitcherView.build overlay options "切换本对话的模型" ignore
            let names = rowNames root
            Assert.Equal(3, names.Length)
            Assert.Contains("gpt-4o，OpenAI，当前", names)
            Assert.Contains("deepseek-chat，DeepSeek，默认", names)
            let texts = descendants root |> Seq.choose (function :? TextBlock as t -> Some t.Text | _ -> None) |> List.ofSeq
            Assert.Contains(ModelSwitcher.appliesNote, texts)
            Assert.Contains("默认", texts))

    [<Fact>]
    let ``typing filters rows and enter picks the first hit`` () =
        Headless.run (fun () ->
            let overlay = OverlayHost(Grid())
            let mutable picked: ModelOption option = None
            let root = ModelSwitcherView.build overlay options "t" (fun o -> picked <- Some o)
            let search = descendants root |> Seq.pick (function :? TextBox as t -> Some t | _ -> None)
            search.Text <- "deep"
            Dispatcher.UIThread.RunJobs()
            Assert.Equal<string list>([ "deepseek-chat，DeepSeek，默认" ], rowNames root)
            search.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = search))
            Assert.Equal(Some "deepseek-chat", picked |> Option.map _.model))

    [<Fact>]
    let ``picking the current model is a no-op`` () =
        Headless.run (fun () ->
            let overlay = OverlayHost(Grid())
            let mutable picked = 0
            let root = ModelSwitcherView.build overlay options "t" (fun _ -> picked <- picked + 1)
            let search = descendants root |> Seq.pick (function :? TextBox as t -> Some t | _ -> None)
            search.Text <- "gpt"
            Dispatcher.UIThread.RunJobs()
            search.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = search))
            Assert.Equal(0, picked))

    [<Fact>]
    let ``rate limited error card shows a live countdown instead of a static hint`` () =
        Headless.run (fun () ->
            let err = { GenerationError.create ProviderRateLimited "「OpenAI」限流了这次请求。" with retryAfterSeconds = Some 90 }
            let card = MessageCard.errorCard err ignore None
            let countdown =
                descendants card
                |> Seq.tryPick (function :? TextBlock as t when AutomationProperties.GetName t = "重试倒计时" -> Some t | _ -> None)
            match countdown with
            | Some t ->
                Assert.EndsWith("后可重试。", t.Text)
                Assert.StartsWith("1 分", t.Text)
            | None -> Assert.Fail "countdown missing"
            // 同一错误实例重建卡片：截止时刻不变（不会从头数）
            let again = MessageCard.errorCard err ignore None
            Assert.True(descendants again |> Seq.exists (function :? TextBlock as t -> AutomationProperties.GetName t = "重试倒计时" | _ -> false)))

    [<Fact>]
    let ``chat header model chip is visible with the model and names the provider`` () =
        Headless.run (fun () ->
            let actions : ChatActions =
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
            let chat = ChatView(actions, fun _ -> Border() :> Control)
            chat.Build()
            chat.SetConversationChrome true
            chat.SetHeaderModel("o3", "OpenAI", true)
            Assert.True chat.HeaderModelVisible
            Assert.Equal("o3", chat.HeaderModelText)
            Assert.Contains("OpenAI", AutomationProperties.GetName chat.HeaderModelAnchor)
            let mutable opened = false
            chat.SetModelSwitcherHandler(fun _ -> opened <- true)
            chat.HeaderModelAnchor.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = chat.HeaderModelAnchor))
            Assert.True opened
            chat.SetHeaderModel("", "", false)
            Assert.False chat.HeaderModelVisible)
