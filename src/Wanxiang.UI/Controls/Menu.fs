namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

/// 浮层菜单里的一项（Wanxiang2.PopupMenu.PopupEntry）。
type MenuEntry = Wanxiang2.PopupMenu.PopupEntry

module MenuEntry =

    let create (label: string) (action: unit -> unit) : MenuEntry = Wanxiang2.PopupMenu.PopupEntry.create label action
    let withIcon (icon: IBrush -> Control) (entry: MenuEntry) = Wanxiang2.PopupMenu.PopupEntry.withIcon icon entry
    let withHint (hint: string) (entry: MenuEntry) = Wanxiang2.PopupMenu.PopupEntry.withHint hint entry
    let markSelected (selected: bool) (entry: MenuEntry) = Wanxiang2.PopupMenu.PopupEntry.markSelected selected entry
    let asDanger (entry: MenuEntry) = Wanxiang2.PopupMenu.PopupEntry.asDanger entry

/// 菜单与下拉选择器由 Wanxiang2.PopupMenu 渲染（InvokeBorder 条目，读屏可 Invoke），浮层与激活后关浮层归 OverlayDesk。
module Menu =

    /// 打开一个菜单。`entries` 为空时不打开。
    let show (overlay: OverlayHost) (anchor: Control) (alignRight: bool) (entries: MenuEntry list) =
        overlay.Desk.ShowMenu(anchor, alignRight, entries)

    /// 带分组标题的菜单（模型选择器：按模型服务分组）。
    let showGrouped (overlay: OverlayHost) (anchor: Control) (alignRight: bool) (groups: (string * MenuEntry list) list) =
        overlay.Desk.ShowGroupedMenu(anchor, alignRight, groups)

    /// 下拉选择器按钮：显示当前值，点击或 Down/Up 弹出候选。返回外壳与「设置显示文本」的回调。
    let selectButton
        (overlay: OverlayHost)
        (initialText: string)
        (optionsOf: unit -> (string * MenuEntry list) list)
        : Border * (string -> unit) =
        let b = overlay.Desk.SelectButton(initialText, optionsOf)
        b.View, b.SetText
