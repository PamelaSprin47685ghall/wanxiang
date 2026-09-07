namespace Wanxiang.UI

open System
open Avalonia.Controls

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
    sidebarWidth: unit -> float) =

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
            sidebar.IsVisible <- not state.sidebarCollapsed
            sidebarSplitter.IsVisible <- not state.sidebarCollapsed
            sidebar.ZIndex <- 0

    /// 用户拖拽后的宽度落定：钳制并写回第 0 列。列宽写操作只出自本控制器，
    /// AppShell 只负责把返回值存进 prefs。
    member _.NoteUserSidebarWidth(actualWidth: float) : float =
        let width = Math.Clamp(actualWidth, Tokens.sidebarMinWidth, Tokens.sidebarMaxWidth)
        shellGrid.ColumnDefinitions[0].Width <- GridLength width
        width

