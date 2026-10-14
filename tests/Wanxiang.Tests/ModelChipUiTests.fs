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
    let ``rate limited error card shows a live countdown instead of a static hint`` () =
        Headless.run (fun () ->
            let err = { GenerationError.create ProviderRateLimited "「OpenAI」限流了这次请求。" with retryAfterSeconds = Some 90 }
            let card = MessageCard.errorCard err ignore None
            let countdown =
                descendants card
                |> Seq.tryPick (function :? TextBlock as t when (string (AutomationProperties.GetName t)).StartsWith "重试倒计时" -> Some t | _ -> None)
            match countdown with
            | Some t ->
                Assert.EndsWith("后可重试。", t.Text)
                Assert.StartsWith("1 分", t.Text)
                // 决策 352：等待中秒数只在按钮上说一遍，这一行不可见。
                Assert.False t.IsVisible
            | None -> Assert.Fail "countdown missing"
            // 同一错误实例重建卡片：截止时刻不变（不会从头数）
            let again = MessageCard.errorCard err ignore None
            Assert.True(descendants again |> Seq.exists (function :? TextBlock as t -> (string (AutomationProperties.GetName t)).StartsWith "重试倒计时" | _ -> false)))

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
            // 决策 350：顶栏不再显示模型芯片（唯一入口在输入框）。
            Assert.False chat.HeaderModelVisible
            Assert.Equal("o3", chat.HeaderModelText)
            Assert.Contains("OpenAI", AutomationProperties.GetName chat.HeaderModelAnchor)
            let mutable opened = false
            chat.SetModelSwitcherHandler(fun _ -> opened <- true)
            chat.HeaderModelAnchor.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = chat.HeaderModelAnchor))
            Assert.True opened
            chat.SetHeaderModel("", "", false)
            Assert.False chat.HeaderModelVisible)
