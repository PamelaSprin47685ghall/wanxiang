namespace Wanxiang.Pwa

open System
open System.Runtime.Versioning
open Avalonia
open Avalonia.Browser
open Avalonia.Controls
open Avalonia.Threading
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Client
open Wanxiang.Interop
open Wanxiang.UI

/// 万象 PWA 入口（决策 1/2：PWA 仅作为 C，纯前端；决策 48：与 Linux C 共用同一套 F# UI 代码）。
/// 编译目标 net10.0-browser（browser-wasm），由 wwwroot/main.js 引导并渲染进 #out 容器。
/// 宿主侧入口：消费 Wanxiang.UI（ProjectReference）与 Wanxiang2.* 交付包，按 wanxiang2
/// EntryShells 的静态端口注入模型启动（App.Root / App.Build）。
module Program =

    [<assembly: SupportedOSPlatform("browser")>]
    do ()

    // ---- 计时（浏览器端 DispatcherTimer，UI 线程）----
    let private every (span: TimeSpan) (action: unit -> unit) : IDisposable =
        let timer = DispatcherTimer(Interval = span)
        timer.Tick.Add(fun _ -> action ())
        timer.Start()
        { new IDisposable with member _.Dispose() = timer.Stop() }

    let private after (span: TimeSpan) (action: unit -> unit) : IDisposable =
        let timer = DispatcherTimer(Interval = span)
        timer.Tick.Add(fun _ -> timer.Stop(); action ())
        timer.Start()
        { new IDisposable with member _.Dispose() = timer.Stop() }

    // ---- WebSocket 客户端（WsClient）----
    let private ws = WsClient()
    let mutable private connectionEpoch = 0

    let private connectAsync () : Async<int> =
        async {
            let uri = Uri(CredentialStore.defaultServerUrl ())
            let! epoch = ws.ConnectWithGenerationAsync(uri, Threading.CancellationToken.None) |> Async.AwaitTask
            connectionEpoch <- epoch
            return epoch
        }

    let private sendAuth (epoch: int) (ev: WireEvent) =
        if epoch = connectionEpoch then ws.TrySendAtGenerationAsync(epoch, ev) |> ignore

    let private send (ev: WireEvent) = ws.SendAsync ev |> ignore
    let private sendCommand (cmd: ClientCommand) = ws.SendCommandAsync cmd |> ignore

    // ---- 浏览器端口（Wanxiang.Interop.BrowserPage / BrowserLocalStore）----
    let private browserViewportWidth () = BrowserPage.ViewportWidth()
    let private browserResumeEpoch () = BrowserPage.ResumeEpoch()
    let private browserPageUrl () = BrowserPage.Url()
    let private browserConversationSucceeded () = BrowserPage.ConversationSucceeded()
    let private browserAddHidden (handler: unit -> unit) = BrowserPage.add_Hidden(fun () -> handler ())

    /// TODO(默认端口)：系统深色偏好无 Browser 桥（matchMedia 未在 main.js 导出）；默认浅色。
    let private systemPrefersDark () : bool = false

    /// TODO(默认端口)：剪贴板写 / 打开链接无 Browser 桥（navigator.clipboard / window.open 未导出）；
    /// 用 LocalStore 占位，待 main.js 增补 JSImport 后替换为真实实现。
    let private copyToClipboard (_text: string) = ()
    let private openLink (_url: string) = ()

    // TODO(默认端口)：文件保存 / 选择 / 拖入 / 粘贴无 Browser 桥（main.js 未导出）；空实现。
    let private saveDownload (_name: string) (_bytes: byte[]) = ()
    let private pickFiles () = ()
    let private dropFiles (_items: obj list) = ()
    let private pasteFromClipboard () : bool = false

    // ---- 本地落点（浏览器 IndexedDB / LocalStore）----
    let private applyDraftWrite (write: DraftWrite) =
        match write with
        | DraftWrite.Unchanged -> ()
        | DraftWrite.Write text -> BrowserDrafts.Put(text) |> ignore
        | DraftWrite.Delete -> BrowserDrafts.Delete() |> ignore

    /// TODO(默认端口)：偏好读写用 LocalStore 占位；真实 IndexedDB 由 BrowserLocalStore 扩展。
    let private prefsLoad () : UiPrefs =
        try UiPrefs.parse UiPrefsFile.limits (Some(BrowserLocalStore.Get("wx-prefs")))
        with _ -> UiPrefsFile.defaults
    let private prefsSave (prefs: UiPrefs) =
        try BrowserLocalStore.Set("wx-prefs", UiPrefs.render None prefs) with _ -> ()

    // ---- 凭据（浏览器 IndexedDB，经 BrowserCredentials / CredentialStore）----
    let private tryLoadClientToml () : (string * string) option = None
    let private tryLoadBrowserConnection () : Async<(string * string * string) option> =
        CredentialStore.tryLoadBrowserConnectionAsync () |> Async.AwaitTask
    let private saveBrowserConnection (instanceId: string) (url: string) (token: string) (name: string) =
        CredentialStore.saveBrowserConnectionAsync instanceId url token name |> ignore

    // ---- 视图组装（ShellViews）：全部 Desk 用主题活实例构造 ----
    let mutable private overlay: Wanxiang2.OverlayDesk.OverlayDesk = Unchecked.defaultof<_>
    let mutable private shell: ShellComposer = Unchecked.defaultof<_>
    let mutable private rootGrid: Grid = null

    let private overlayLook () : Wanxiang2.OverlayDesk.OverlayDeskLook =
        let theme = SharedTheme.manager
        let brush k = theme.Brush k :> Avalonia.Media.IBrush
        let metric m = theme.Metric m :> IObservable<float>
        let space s = theme.Space s :> IObservable<float>
        { Layer =
            { Scrim = brush Wanxiang2.ThemePalette.PaletteKey.Scrim
              Surface = brush Wanxiang2.ThemePalette.PaletteKey.Surface
              Edge = brush Wanxiang2.ThemePalette.PaletteKey.Border
              EdgeWidth = Wanxiang2.Theme.Live.map Thickness (metric Wanxiang2.ThemeMetrics.Metric.BorderWidth)
              DialogRadius = Wanxiang2.Theme.Live.map CornerRadius (space Wanxiang2.Theme.Space.RadiusLg)
              PopupRadius = Wanxiang2.Theme.Live.map CornerRadius (space Wanxiang2.Theme.Space.RadiusMd)
              DialogPadding = Wanxiang2.Theme.Live.map (fun v -> Thickness(v)) (space Wanxiang2.Theme.Space.Xl)
              PopupPadding = Wanxiang2.Theme.Live.map (fun v -> Thickness(v)) (space Wanxiang2.Theme.Space.Xs)
              DialogShadow = Wanxiang2.Theme.LiveValue(Avalonia.Media.BoxShadows()) :> IObservable<_>
              PopupShadow = Wanxiang2.Theme.LiveValue(Avalonia.Media.BoxShadows()) :> IObservable<_>
              ViewportInset = Wanxiang2.Theme.LiveValue 12.0 :> IObservable<_>
              PopupGap = Wanxiang2.Theme.LiveValue 4.0 :> IObservable<_>
              PopupMinWidth = Wanxiang2.Theme.LiveValue 180.0 :> IObservable<_>
              ScrimFade = Wanxiang2.Theme.LiveValue TimeSpan.Zero :> IObservable<_>
              ScrimEasing = Avalonia.Animation.Easings.CubicEaseOut() }
          Toasts =
            { Surface = brush Wanxiang2.ThemePalette.PaletteKey.SurfaceRaised
              Edge = brush Wanxiang2.ThemePalette.PaletteKey.Border
              Text = brush Wanxiang2.ThemePalette.PaletteKey.Text
              Accent = fun _ -> brush Wanxiang2.ThemePalette.PaletteKey.Accent
              AccentWidth = Wanxiang2.Theme.LiveValue 2.0 :> IObservable<_>
              AccentRadius = Wanxiang2.Theme.LiveValue(CornerRadius 999.0) :> IObservable<_>
              AccentOpacity = Wanxiang2.Theme.LiveValue 0.6 :> IObservable<_>
              EdgeWidth = Wanxiang2.Theme.Live.map Thickness (metric Wanxiang2.ThemeMetrics.Metric.BorderWidth)
              Radius = Wanxiang2.Theme.LiveValue(CornerRadius 8.0) :> IObservable<_>
              Padding = Wanxiang2.Theme.LiveValue(Thickness(16.0, 12.0)) :> IObservable<_>
              Shadow = Wanxiang2.Theme.LiveValue(Avalonia.Media.BoxShadows()) :> IObservable<_>
              FontSize = metric Wanxiang2.ThemeMetrics.Metric.FontBody
              LineHeight = Wanxiang2.Theme.LiveValue 18.0 :> IObservable<_>
              ColumnSpacing = Wanxiang2.Theme.LiveValue 12.0 :> IObservable<_>
              Spacing = Wanxiang2.Theme.LiveValue 8.0 :> IObservable<_>
              Margin = Wanxiang2.Theme.LiveValue(Thickness(24.0, 0.0, 24.0, 32.0)) :> IObservable<_>
              MaxWidth = Wanxiang2.Theme.LiveValue 480.0 :> IObservable<_>
              BodyMaxHeight = Wanxiang2.Theme.LiveValue 160.0 :> IObservable<_>
              Fade = Wanxiang2.Theme.LiveValue TimeSpan.Zero :> IObservable<_>
              FadeEasing = Avalonia.Animation.Easings.CubicEaseOut() }
          Menu =
            { Text = brush Wanxiang2.ThemePalette.PaletteKey.Text
              Muted = brush Wanxiang2.ThemePalette.PaletteKey.TextMuted
              Faint = brush Wanxiang2.ThemePalette.PaletteKey.TextFaint
              Danger = brush Wanxiang2.ThemePalette.PaletteKey.Danger
              Accent = brush Wanxiang2.ThemePalette.PaletteKey.Accent
              Check = fun b -> Wanxiang2.IconSet.IconSet.image Wanxiang2.IconSet.Glyph.Check b (metric Wanxiang2.ThemeMetrics.Metric.IconGlyph)
              Chevron = fun b -> Wanxiang2.IconSet.IconSet.image Wanxiang2.IconSet.Glyph.ChevronRight b (metric Wanxiang2.ThemeMetrics.Metric.IconGlyph)
              FontSize = metric Wanxiang2.ThemeMetrics.Metric.FontBody
              HintFontSize = metric Wanxiang2.ThemeMetrics.Metric.FontCaption
              IconSlotWidth = Wanxiang2.Theme.LiveValue 16.0 :> IObservable<_>
              IconHeight = Wanxiang2.Theme.LiveValue 16.0 :> IObservable<_>
              RightSlotMinWidth = Wanxiang2.Theme.LiveValue 16.0 :> IObservable<_>
              HintMargin = Wanxiang2.Theme.LiveValue(Thickness(12.0, 0.0, 0.0, 0.0)) :> IObservable<_>
              ColumnSpacing = Wanxiang2.Theme.LiveValue 6.0 :> IObservable<_>
              ItemPadding = Wanxiang2.Theme.LiveValue(Thickness(8.0, 4.0)) :> IObservable<_>
              ItemRadius = Wanxiang2.Theme.LiveValue(CornerRadius 4.0) :> IObservable<_>
              ItemMinHeight = Wanxiang2.Theme.LiveValue 32.0 :> IObservable<_>
              ItemSpacing = Wanxiang2.Theme.LiveValue 2.0 :> IObservable<_>
              MaxHeight = Wanxiang2.Theme.LiveValue 200.0 :> IObservable<_>
              DividerMargin = Wanxiang2.Theme.LiveValue(Thickness(6.0, 2.0)) :> IObservable<_>
              HeaderMargin = Wanxiang2.Theme.LiveValue(Thickness(8.0, 6.0, 8.0, 2.0)) :> IObservable<_>
              SelectPadding = Wanxiang2.Theme.LiveValue(Thickness(8.0, 4.0)) :> IObservable<_>
              SelectRadius = Wanxiang2.Theme.LiveValue(CornerRadius 8.0) :> IObservable<_>
              SelectEdgeWidth = Wanxiang2.Theme.LiveValue(Thickness 1.0) :> IObservable<_>
              SelectCaptionMaxWidth = Wanxiang2.Theme.LiveValue 260.0 :> IObservable<_> }
          MenuMinWidth = Wanxiang2.Theme.LiveValue 200.0 :> IObservable<_>
          GroupedMenuMinWidth = Wanxiang2.Theme.LiveValue 240.0 :> IObservable<_>
          DwellFailureMs = Wanxiang2.Theme.LiveValue 9000.0 :> IObservable<_>
          DwellWarningMs = Wanxiang2.Theme.LiveValue 6500.0 :> IObservable<_>
          DwellDefaultMs = Wanxiang2.Theme.LiveValue 4000.0 :> IObservable<_>
          DismissTick = TimeSpan.FromMilliseconds 100.0 }

    let buildViews () : ShellViews =
        let theme = SharedTheme.manager
        let styles = SharedTheme.styles
        let root = Grid()
        rootGrid <- root
        let ov = Wanxiang2.OverlayDesk.OverlayDesk(root, overlayLook ())
        overlay <- ov
        let brandLogo = SharedTheme.logo

        let composer = Wanxiang2.ComposerDesk.ComposerDesk(Wanxiang2.ComposerDesk.ComposerLooks.ofTheme theme styles,
            { Submit = fun _ -> false
              StopGeneration = fun () -> shell.Commands.HandleShortcut Wanxiang.UI.ShortcutAction.Escape |> ignore
              PickAttachment = fun () -> pickFiles ()
              RemoveAttachment = fun _ -> ()
              OpenModelPicker = fun _ -> ()
              DropFiles = fun _ -> ()
              PasteFromClipboard = pasteFromClipboard
              OpenSessionSettings = fun () -> () })

        let approval = Wanxiang2.ApprovalDesk.ApprovalDesk(theme, ov, send)

        let tintHost: Wanxiang2.CodeTint.CodeTintHost =
            { HighlightHtml = fun (code, lang) ->
                  match RichBackend.current () with
                  | Some backend -> backend.HighlightHtml(code, lang)
                  | None -> None
              Background = fun work -> work ()
              After = fun ms action -> after (TimeSpan.FromMilliseconds(float ms)) action |> ignore
              Synchronous = true }
        let tint = Wanxiang2.CodeTint.CodeTint(tintHost)
        let cards = Wanxiang2.MessageCardView.MessageCards(Wanxiang2.MessageCardView.CardLooks.ofTheme theme styles,
            { Tint = tint
              MathHtml = fun (tex, display) ->
                  match RichBackend.current () with
                  | Some backend -> backend.MathHtml(tex, display)
                  | None -> None
              Wrap = Wanxiang2.MarkdownView.CodeWrapMemory(false)
              Clock = Wanxiang2.MessageCardView.CaretClock()
              Touch = Wanxiang2.MessageCardView.CardTouch()
              Now = fun () -> DateTimeOffset.UtcNow })

        let chat = Wanxiang2.ChatScreen.ChatScreen(Wanxiang2.ChatScreen.ChatScreenLooks.ofTheme theme styles,
            cards, composer, approval, ov, brandLogo,
            { Card = { CopyText = copyToClipboard
                       Regenerate = fun () -> ()
                       EditAndFork = fun _ -> ()
                       DeleteMessage = fun _ -> ()
                       DownloadAttachment = fun _ -> ()
                       OpenLink = openLink
                       CopyLink = None
                       Recover = None }
              RenameTitle = fun _ -> ()
              ToggleSidebar = fun () -> shell.Commands.HandleShortcut Wanxiang.UI.ShortcutAction.ToggleSidebar |> ignore
              OpenSessionSettings = fun () -> ()
              ForkFromHere = fun () -> ()
              ExportConversation = fun () -> ()
              StopGeneration = fun () -> shell.Commands.HandleShortcut Wanxiang.UI.ShortcutAction.Escape |> ignore
              RequestOlderHistory = fun () -> ()
              FocusHome = fun () -> composer.Focus()
              OpenParent = fun _ -> ()
              SiblingSelected = fun _ -> ()
              After = after })

        let sidebar = Wanxiang2.SidebarDesk.SidebarDesk(theme, ov,
            { newConversation = fun () -> shell.Commands.HandleShortcut Wanxiang.UI.ShortcutAction.NewConversation |> ignore
              openConversation = fun _id -> shell.Commands.HandleShortcut(Wanxiang.UI.ShortcutAction.OpenConversationAt 0) |> ignore
              renameConversation = fun _ -> ()
              deleteConversation = fun _ -> ()
              setPinned = fun _ _ -> ()
              setArchived = fun _ _ -> ()
              selectionAllPinned = fun _ -> false
              setPinnedMany = fun _ _ -> ()
              setArchivedMany = fun _ _ -> ()
              deleteMany = fun _ -> ()
              duplicateAsFork = fun _ -> ()
              exportConversation = fun _ -> ()
              openSettings = fun () -> shell.Commands.HandleShortcut Wanxiang.UI.ShortcutAction.OpenSettings |> ignore
              reconnect = fun () -> shell.ResumeReconnectNow()
              toggleArchivedVisibility = fun () -> ()
              closeNavigation = fun () -> () },
            brandLogo)

        let settingsHooks: Wanxiang2.SettingsScreen.SettingsHooks =
            { UpsertProvider = fun _ _ -> ()
              DeleteProvider = ignore
              ProbeProvider = fun _ -> ()
              UpsertMcp = fun _ _ -> ()
              DeleteMcp = ignore
              UpdateGeneration = fun _ _ -> ()
              SavePrefs = prefsSave }
        let settings = Wanxiang2.SettingsScreen.SettingsScreen(theme, ov, settingsHooks, prefsLoad ())

        let onboardingHooks: Wanxiang2.SettingsScreen.OnboardingHooks =
            { UpsertProvider = fun _ _ -> ()
              DiscardProvider = ignore
              ProbeProvider = fun _ -> ()
              TakenIds = fun () -> []
              DiscoverLocal = fun () -> System.Threading.Tasks.Task.FromResult(None)
              OpenAdvanced = fun () -> () }
        let onboarding = Wanxiang2.SettingsScreen.OnboardingDesk(theme, ov, onboardingHooks)

        let workspace = Wanxiang2.Workspace.Workspace(theme, root, sidebar, chat, composer,
            { PrefersTouch = fun () -> SharedTheme.input.PrefersTouch
              SidebarWidth = fun () -> UiPrefsFile.limits.sidebarWidth
              InitialSidebarCollapsed = false })

        let palette = Wanxiang2.PaletteDesk.PaletteDesk(theme, ov, fun () -> SharedTheme.input.PrefersTouch)
        let modelSwitch = Wanxiang2.ModelSwitchDesk.ModelSwitchDesk(theme, ov)

        { Sidebar = sidebar
          Chat = chat
          Composer = composer
          Settings = settings
          Onboarding = onboarding
          Workspace = workspace
          Approval = approval
          Palette = palette
          ModelSwitch = modelSwitch
          SettingsHostVisible = fun visible -> settings.View.IsVisible <- visible
          WorkspaceHostVisible = fun visible -> workspace.Host.IsVisible <- visible
          SettingsFocusInitial = fun () -> settings.FocusInitial()
          SettingsShowSection = fun section ->
              section |> Option.iter (fun s -> Wanxiang2.SettingsScreen.SettingsPart.ofKey s |> Option.iter settings.ShowSection) }

    /// 根视图工厂（App.Root）：MainView 由 ShellComposer 驱动，这里给根 Grid。
    let private rootFactory () : Control =
        if isNull rootGrid then buildViews () |> ignore
        rootGrid :> Control

    // ---- 提示条（OverlayDesk）----
    let private toast (text: string) (tone: ShellToastTone) =
        match tone with
        | ShellToastTone.Info -> overlay.Toast(text, Wanxiang2.ToastStrip.ToastTone.Info)
        | ShellToastTone.Success -> overlay.Toast(text, Wanxiang2.ToastStrip.ToastTone.Success)
        | ShellToastTone.Warning -> overlay.Toast(text, Wanxiang2.ToastStrip.ToastTone.Warning)
        | ShellToastTone.Failure -> overlay.Toast(text, Wanxiang2.ToastStrip.ToastTone.Failure)

    let private toastUndo (text: string) (undo: unit -> unit) =
        overlay.ToastWith(text, Wanxiang2.ToastStrip.ToastTone.Info, Some("撤销", undo))

    /// ShellPorts 全字段。
    let private ports: ShellPorts =
        { Connect = connectAsync
          SendAuth = sendAuth
          Send = send
          SendCommand = sendCommand
          After = after
          Every = every
          Toast = toast
          ToastUndo = toastUndo
          CopyToClipboard = copyToClipboard
          OpenLink = openLink
          SaveDownload = saveDownload
          PickFiles = pickFiles
          DropFiles = dropFiles
          PasteFromClipboard = pasteFromClipboard
          ApplyDraftWrite = applyDraftWrite
          PrefsLoad = prefsLoad
          PrefsSave = prefsSave
          TryLoadClientToml = tryLoadClientToml
          TryLoadBrowserConnection = tryLoadBrowserConnection
          SaveBrowserConnection = saveBrowserConnection
          BrowserViewportWidth = browserViewportWidth
          BrowserResumeEpoch = browserResumeEpoch
          BrowserPageUrl = browserPageUrl
          BrowserConversationSucceeded = browserConversationSucceeded
          BrowserAddHidden = browserAddHidden
          SystemPrefersDark = systemPrefersDark
          BuildViews = buildViews }

    /// App.Build：视图挂载后调用 ShellComposer.Build。
    let private buildApp (_view: Control) =
        if obj.ReferenceEquals(shell, null) then
            shell <- ShellComposer(ports)
        shell.Build() |> ignore
        ws.EventReceived.Add(fun ev -> Dispatcher.UIThread.Post(fun () -> shell.ApplyHostEvent ev))
        shell.AutoConnect()

    [<EntryPoint>]
    let main _argv =
        task {
            // 强制 WebGL（不准软渲染）：WebGL2 → WebGL1，不回退 Software2D。
            let options =
                BrowserPlatformOptions(
                    RenderingMode =
                        [| BrowserRenderingMode.WebGL2
                           BrowserRenderingMode.WebGL1 |],
                    FrameworkAssetPathResolver = fun fileName -> $"./{fileName}?v=20261012b"
                )
            // 代码着色与公式排版交给浏览器自带的 JS 引擎；脚本正文仍是 .NET 侧的内嵌资源
            let rich, backend = RichBrowserAdapter.create ()
            RichBackend.install backend
            rich.Warmup()
            // 决策 342：图片缩小交给浏览器（原生解码 + Canvas，异步），不占 wasm 单线程。
            // 宿主版恢复接线：Wanxiang2.ImageShrinkIo 已提供 platformShrinker 槽位。
            ImageShrink.platformShrinker <- Some BrowserImageShrink.shrink
            // App 静态端口（EntryShells.App）：PWA 分支只挂 SingleView。
            App.Root <- Some rootFactory
            App.Build <- Some buildApp
            App.FlushDrafts <- Some(fun () -> if not (obj.ReferenceEquals(shell, null)) then shell.FlushDrafts())
            App.LoadPrefs <- Some prefsLoad
            App.SavePrefs <- Some prefsSave
            do!
                AppBuilder
                    .Configure<App>()
                    .StartBrowserAppAsync("out", options)
        }
        |> ignore
        0
