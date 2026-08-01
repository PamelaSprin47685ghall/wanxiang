namespace Wanxiang.UI

open Avalonia.Media

/// 万象视觉主题（灵感：Material You 默认靛蓝；clean-room 独立实现）。
/// 桌面端固定浅色（深色由 PWA 通过 prefers-color-scheme 提供）。
module Theme =

    let private hex (s: string) = SolidColorBrush(Color.Parse s)

    let bg = hex "#F6F6F9"
    let panel = hex "#FFFFFF"
    let sidebar = hex "#EFEFF5"
    let border = hex "#E3E3EC"
    let text = hex "#1A1B21"
    let muted = hex "#61626C"
    let faint = hex "#8B8C98"

    let primary = hex "#4D5C92"
    let onPrimary = hex "#FFFFFF"
    let primaryContainer = hex "#DCE1FF"
    let onPrimaryContainer = hex "#1A2A63"
    let secondaryContainer = hex "#DEE1F9"
    let onSecondaryContainer = hex "#161B2C"
    let toolChip = hex "#E8EAF6"

    let userBubble = hex "#4D5C92"
    let userText = hex "#FFFFFF"
    let assistantBubble = hex "#FFFFFF"
    let assistantBorder = hex "#E3E3EC"

    /// 极细分隔线（头尾栏用，比 border 更淡，避免硬分割的“大方块”感）
    let borderSubtle = SolidColorBrush(Color.Parse "#E3E3EC", 0.55)

    let hover = hex "#0D1A1B21"      // 5% 黑
    let pressed = hex "#141A1B21"    // 8% 黑
    let selectedHover = hex "#D4DAFF"
    let selectedPressed = hex "#C6CEFF"
