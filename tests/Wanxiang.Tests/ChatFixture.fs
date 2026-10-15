namespace Wanxiang.Tests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Automation.Provider
open Avalonia.Controls
open Avalonia.Threading
open Avalonia.VisualTree
open Wanxiang.Core
open Wanxiang.UI
open Wanxiang2.ChatScreen
open Wanxiang2.MessageCardView

/// 旧应用场景的装配参数，只存在于测试程序集。
type ChatActions =
    { renameTitle: string -> unit
      openSessionSettings: unit -> unit
      forkFromHere: unit -> unit
      stopGeneration: unit -> unit
      requestOlderHistory: unit -> unit
      retryLast: unit -> unit
      toggleSidebar: unit -> unit
      focusHome: unit -> unit
      message: CardActions }

/// 测试夹具：旧用例继续驱动真实交付包，不复制渲染、缓存、滚动或菜单规则。
/// 应用直接用 ChatScreen。本类型不是应用兼容层，也不在 Wanxiang.UI 程序集里。
type ChatView(actions: ChatActions, brandLogo: float -> Control, ?overlay: OverlayHost, ?exportConversation: unit -> unit) as this =
    inherit Border()

    let overlay = defaultArg overlay (OverlayHost(Grid()))
    let composer =
        Wanxiang2.ComposerDesk.ComposerDesk(
            Wanxiang2.ComposerDesk.ComposerLooks.ofTheme SharedTheme.manager SharedTheme.styles,
            { Submit = fun _ -> true
              StopGeneration = actions.stopGeneration
              PickAttachment = ignore
              RemoveAttachment = ignore
              OpenModelPicker = ignore
              DropFiles = ignore
              PasteFromClipboard = fun () -> false
              OpenSessionSettings = actions.openSessionSettings })
    let approvals = Wanxiang2.ApprovalDesk.ApprovalDesk(SharedTheme.manager, overlay.Desk, ignore)
    let mutable openParent: Guid -> unit = ignore
    let mutable siblingSelected: Guid -> unit = ignore
    let mutable focusHome = actions.focusHome
    let screen =
        ChatScreen(ChatScreenLooks.ofTheme SharedTheme.manager SharedTheme.styles,
                   Cards.instance(), composer, approvals, overlay.Desk, brandLogo,
                   { Card = actions.message
                     RenameTitle = actions.renameTitle
                     ToggleSidebar = actions.toggleSidebar
                     OpenSessionSettings = actions.openSessionSettings
                     ForkFromHere = actions.forkFromHere
                     ExportConversation = defaultArg exportConversation ignore
                     StopGeneration = actions.stopGeneration
                     RequestOlderHistory = actions.requestOlderHistory
                     FocusHome = fun () -> focusHome ()
                     OpenParent = fun id -> openParent id
                     SiblingSelected = fun id -> siblingSelected id
                     After = fun delay action ->
                         let timer = DispatcherTimer(Interval = delay)
                         timer.Tick.Add(fun _ -> timer.Stop(); action ())
                         timer.Start()
                         { new IDisposable with member _.Dispose() = timer.Stop() } })

    do this.Child <- screen.Host

    member _.Screen = screen
    member _.Build() = ()
    member _.RenderMessages(messages, streaming, error, fontSize, collapse, usage, missing) =
        screen.Render { Messages = messages; Streaming = streaming; Error = error; FontSize = fontSize
                        AutoCollapseReasoning = collapse; Usage = usage; MissingAttachments = missing }
    member _.SetTitle(text, editable) = screen.SetTitle(text, editable)
    member _.SetGenerating(value, text, ?focusHome: unit -> unit) =
        focusHome |> Option.iter (fun home -> this.SetFocusHome home)
        screen.SetGenerating(value, text)
    member private _.SetFocusHome(home) = focusHome <- home
    member _.SetCanStop(value) = screen.SetCanStop value
    member _.SetConversationChrome(value) = screen.SetConversationChrome value
    member _.SetBreadcrumb(crumb, handler) = openParent <- handler; screen.SetBreadcrumb crumb
    member _.BreadcrumbText = screen.BreadcrumbText
    member _.ActivateBreadcrumb() =
        let button = screen.Header.CrumbAction
        if button.IsEffectivelyVisible && button.Focusable then
            let peer = box (ControlAutomationPeer.CreatePeerForElement button) :?> IInvokeProvider
            peer.Invoke()
            Dispatcher.UIThread.RunJobs()
            true
        else false
    member _.SiblingProvider with set value = screen.SetSiblingProvider value
    member _.SiblingSelected with set value = siblingSelected <- value
    member _.SetCompactMode(value) = screen.SetCompactMode value
    member _.SetStartMode(value) = screen.SetStartMode value
    member _.SetStarterHandler(pick) = screen.Pane.SetStarterPick(Some pick)
    member _.SetPresetStarters(items) = screen.Pane.SetPresets items
    member _.PresetStarterCount =
        screen.Pane.Empty.Host.GetVisualDescendants()
        |> Seq.filter (function :? Control as c -> c.Name = "preset-chip" | _ -> false)
        |> Seq.length
    member _.SetSavePresetAction(action) = screen.SetSavePresetAction action
    member _.ShowEmpty(state, primary) = screen.Pane.ShowEmpty(state, primary)
    member _.ShowEmbeddedEmpty(content) = screen.Pane.ShowEmbedded content
    member _.HideEmpty() = screen.Pane.HideEmpty()
    member _.SetOlderAvailable(value) = screen.Pane.SetOlderAvailable value
    member _.ShowOlderPageFailure(text, retry) = screen.Pane.ShowOlderFailure(text, retry)
    member _.HideOlderPageFailure() = screen.Pane.HideOlderFailure()
    member _.OlderPageFailureVisible = screen.Pane.Nav.OlderFailureVisible
    member _.OlderPageFailureText = screen.Pane.Nav.OlderFailureText
    member _.OlderPageRetryButton = screen.Pane.Nav.RetryButton
    member _.CanJumpPrevious = screen.Pane.Nav.CanJumpPrevious
    member _.CanJumpNext = screen.Pane.Nav.CanJumpNext
    member _.PendingJumpPrevious = screen.Pane.Jumps.PendingPrevious
    member _.HiddenMessageCount = screen.HiddenMessageCount
    static member InitialMountWindow = MountWindow.Initial
    static member MountWindowStep = MountWindow.Step
    member _.ExpandMountWindow() = screen.ExpandMountWindow()
    member _.ShowSkeletonLoading() = screen.Pane.ShowSkeleton()
    member _.HideSkeletonLoading() = screen.Pane.HideSkeleton()
    member _.IsSkeletonVisible = screen.Pane.IsSkeletonShown
    member _.IsMessagePanelVisible = screen.Pane.Messages.IsVisible
    member _.SetAwaitingApproval(ids) = screen.SetAwaitingApproval ids
    member _.SetPartialReply(partial) = screen.SetPartialReply partial
    member _.InvalidateCards() = screen.InvalidateCards()
    member _.RevealPinned = screen.Pane.Follow.RevealPinned
    member _.HighlightedCommit = screen.HighlightedCommit
    member _.LastFlashedCommit = screen.LastFlashedCommit
    member _.RevealAndHighlight(commit) = screen.RevealAndHighlight commit
    member _.EndRevealHighlight() = screen.EndRevealHighlight()
    member _.ScrollToMessage(commit) = screen.ScrollToMessage commit
    member _.ScrollToEndDeferred() = screen.ScrollToEndDeferred()
    member _.ScrollToEnd() = screen.ScrollToEndDeferred()
    member _.SmoothScrollToEnd() = screen.SmoothScrollToEnd()
    member _.ScrollToBeginning() = screen.ScrollToBeginning()
    member _.ScrollToBeginningDeferred() = screen.Pane.Scroller.ScrollToHomeDeferred()
    member _.SmoothScrollToBeginning() = screen.SmoothScrollToBeginning()
    member _.ResetScrollState() = screen.ResetScrollState()
    member _.BeginHistoryPrependAnchor() = screen.BeginHistoryPrependAnchor()
    member _.RestoreHistoryPrependAnchorDeferred() = screen.RestoreHistoryPrependAnchorDeferred()
    member _.CancelHistoryPrependAnchor() = screen.CancelHistoryPrependAnchor()
    member _.MessageScroller = screen.Pane.Scroller.Viewer
    member _.MessagesPanel = screen.Pane.Messages
    member _.LatestButton = screen.Pane.Scroller.LatestButton
    member _.IsSmoothScrolling = screen.Pane.Scroller.IsSmoothing
    member _.JumpExchange(direction) = screen.JumpExchange direction
