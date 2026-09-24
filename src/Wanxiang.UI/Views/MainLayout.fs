namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.VisualTree

/// 主工作区唯一的空间投影器。
/// NavigationController 决定“当前是什么状态”，这里决定“这个状态在 Grid 上怎么成立”。
/// AppShell 不再直接改列宽 / Grid.Column / ZIndex，避免响应式规则散落成 patch。
type MainLayoutController(
    shellGrid: Grid,
    sidebar: Sidebar,
    sidebarSplitter: GridSplitter,
    chatColumn: DockPanel,
    composer: Composer,
    chat: ChatView,
    sidebarWidth: unit -> float) as this =

    /// compact 抽屉遮罩：shellGrid 里位于内容(ZIndex 0)与侧栏(2)之间的一整层，
    /// 与侧栏同在 shellGrid。只做不透明度淡入（MotionLedger.scrimFade，经
    /// MotionPolicy 降级），不碰列宽/尺寸/transform——几何动画被 MotionPolicy 钉死。
    /// 打开才可见并可命中：按下即收起 compact 抽屉（与 Escape/侧栏行同一归宿）。
    let scrim =
        Border(
            Background = Tokens.scrim,
            IsVisible = false,
            Opacity = 0.0,
            ZIndex = 1)

    do
        let fade = Avalonia.Animation.DoubleTransition()
        fade.Property <- Avalonia.Visual.OpacityProperty
        fade.Duration <- (if MotionPolicy.isReduced () then TimeSpan.Zero else MotionLedger.scrimFade)
        let fadeTransitions = Avalonia.Animation.Transitions()
        fadeTransitions.Add fade
        scrim.Transitions <- fadeTransitions
        Grid.SetColumn(scrim, 0)
        shellGrid.Children.Add scrim
        scrim.PointerPressed.Add(fun e ->
            e.Handled <- true
            match this.OnScrimPressed with
            | Some dismiss -> dismiss ()
            | None -> ())

    /// 由 AppShell 注入：点击遮罩时收起 compact 抽屉。
    member val OnScrimPressed : (unit -> unit) option = None with get, set

    member _.Apply(state: NavigationSnapshot) =
        composer.SetCompactMode state.compactMode
        sidebar.SetCompactMode state.compactMode
        chat.SetCompactMode state.compactMode

        if state.compactMode then
            shellGrid.ColumnDefinitions[0].MinWidth <- 0.0
            shellGrid.ColumnDefinitions[0].Width <- GridLength.Star
            shellGrid.ColumnDefinitions[1].Width <- GridLength 0.0
            shellGrid.ColumnDefinitions[2].Width <- GridLength 0.0
            Grid.SetColumn(sidebar, 0)
            Grid.SetColumn(chatColumn, 0)
            sidebarSplitter.IsVisible <- false
            sidebar.IsVisible <- state.compactNavigationOpen
            sidebar.ZIndex <- 2
            // 抽屉 scrim 跟随 compactNavigationOpen：打开淡入，关闭立即隐藏并复位
            // 不透明度（收起是状态切换，不做淡出）。几何一律不碰。
            if state.compactNavigationOpen then
                scrim.IsVisible <- true
                scrim.Opacity <- 1.0
            else
                scrim.IsVisible <- false
                scrim.Opacity <- 0.0
        else
            shellGrid.ColumnDefinitions[0].MinWidth <- if state.sidebarCollapsed then 0.0 else Tokens.sidebarMinWidth
            shellGrid.ColumnDefinitions[0].Width <-
                if state.sidebarCollapsed then GridLength 0.0 else GridLength(sidebarWidth ())
            shellGrid.ColumnDefinitions[1].Width <-
                if state.sidebarCollapsed then GridLength 0.0 else GridLength LayoutPolicy.sidebarSplitterWidth
            shellGrid.ColumnDefinitions[2].Width <- GridLength.Star
            Grid.SetColumn(sidebar, 0)
            Grid.SetColumn(sidebarSplitter, 1)
            Grid.SetColumn(chatColumn, 2)
            // 折叠让侧栏 IsVisible=false：焦点若停在侧栏行/搜索/归档开关上会被
            // Avalonia 直接清成 null，键盘用户按完 Ctrl+B 就失焦。必须在置隐藏
            // 之前读出旧焦点（隐藏后已无从判断它原来在哪），必要时收起后补回输入区。
            let focusWasInSidebar =
                match Avalonia.Controls.TopLevel.GetTopLevel(sidebar) with
                | null -> false
                | top ->
                    match top.FocusManager with
                    | null -> false
                    | manager ->
                        match manager.GetFocusedElement() with
                        | :? Control as focused -> (sidebar :> Visual).IsVisualAncestorOf focused
                        | _ -> false
            sidebar.IsVisible <- not state.sidebarCollapsed
            sidebarSplitter.IsVisible <- not state.sidebarCollapsed
            sidebar.ZIndex <- 0
            if state.sidebarCollapsed && focusWasInSidebar then composer.Focus()
            // 非 compact 布局不挂遮罩：离开 compact 时收起遗留层并复位不透明度。
            scrim.IsVisible <- false
            scrim.Opacity <- 0.0

    /// 用户拖拽后的宽度落定：钳制并写回第 0 列。列宽写操作只出自本控制器，
    /// AppShell 只负责把返回值存进 prefs。
    member _.NoteUserSidebarWidth(actualWidth: float) : float =
        let width = Math.Clamp(actualWidth, Tokens.sidebarMinWidth, Tokens.sidebarMaxWidth)
        shellGrid.ColumnDefinitions[0].Width <- GridLength width
        width

