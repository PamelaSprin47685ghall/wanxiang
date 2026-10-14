namespace Wanxiang.UI

open Avalonia.Input

/// 组合者换算：Wanxiang2.KeyChord（Avalonia 按键 → 平台无关形状）→ ShortcutKeys 的 ShortcutKey → 意图。
module KeyShortcut =

    let ofEvent (e: KeyEventArgs) : ShortcutKey =
        let c = Wanxiang2.KeyChord.KeyChord.ofEvent e
        { key = c.key; ctrl = c.ctrl; shift = c.shift; noModifiers = c.noModifiers }

    let resolve (e: KeyEventArgs) : ShortcutAction = ShortcutRouter.resolveKey (ofEvent e)
