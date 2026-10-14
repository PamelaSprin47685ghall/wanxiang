namespace Wanxiang.UI

open Avalonia.Input

/// Avalonia 按键 → 与平台无关的 ShortcutKey（Wanxiang2.ShortcutKeys 做「键 → 意图」）。
/// ⌘ 与 Ctrl 同义。
module KeyShortcut =

    let ofEvent (e: KeyEventArgs) : ShortcutKey =
        { key = string e.Key
          ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control || e.KeyModifiers.HasFlag KeyModifiers.Meta
          shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
          noModifiers = e.KeyModifiers = KeyModifiers.None }

    let resolve (e: KeyEventArgs) : ShortcutAction = ShortcutRouter.resolveKey (ofEvent e)
