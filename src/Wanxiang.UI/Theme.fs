namespace Wanxiang.UI

open Avalonia.Media

/// 万象视觉主题：克制高级感（纸感中性 + 墨色点缀；clean-room 独立实现）。
/// 桌面端固定浅色；PWA 深色壳由 wwwroot/app.css 承担。
/// 原则：少填色、弱圆角、轻阴影、用字重/留白分层，避免糖果色塑料感。
module Theme =

    let private hex (s: string) = SolidColorBrush(Color.Parse s)

    // ---- 间距 / 圆角 / 栏高（主壳对齐 SSOT）----
    let space1 = 4.0
    let space2 = 8.0
    let space3 = 12.0
    let space4 = 16.0
    let space5 = 20.0
    let space6 = 24.0
    /// 克制圆角：偏建筑感，避免胶囊/玩具感
    let radiusSm = 6.0
    let radiusMd = 8.0
    let radiusLg = 10.0
    let radiusXl = 12.0
    let radiusPill = 999.0
    let sidebarWidth = 268.0
    let readingWidth = 720.0
    let barHeight = 48.0
    let iconBtn = 32.0

    // ---- 纸感中性面 ----
    let bg = hex "#F5F4F1"
    let panel = hex "#FBFBF9"
    let sidebar = hex "#EFEEEA"
    let border = hex "#E2E0DA"
    let text = hex "#1C1B19"
    let muted = hex "#6A6862"
    let faint = hex "#96948D"

    /// 墨色强调：只用于必要动作，不作大面积底色
    let primary = hex "#2E343E"
    let onPrimary = hex "#F7F6F3"
    /// 浅强调面：近中性，非糖果色容器
    let primaryContainer = hex "#E8E7E2"
    let onPrimaryContainer = hex "#2E343E"
    let secondaryContainer = hex "#EFEDEA"
    let onSecondaryContainer = hex "#3A3935"
    let toolChip = hex "#EBEAE5"

    let userBubble = hex "#2E343E"
    let userText = hex "#F7F6F3"
    let assistantBubble = hex "#FBFBF9"
    let assistantBorder = hex "#E2E0DA"

    let borderSubtle = SolidColorBrush(Color.Parse "#E2E0DA", 0.7)
    let surfaceVariant = hex "#EBEAE5"
    let outlineVariant = hex "#D4D2CB"
    let errorContainer = hex "#F0E4E1"

    let hover = SolidColorBrush(Color.Parse "#1C1B19", 0.04)
    let pressed = SolidColorBrush(Color.Parse "#1C1B19", 0.07)
    /// 选中态：暖灰洗色，不用亮紫/亮蓝
    let selectedHover = hex "#E4E2DC"
    let selectedPressed = hex "#DAD8D1"

    // ---- 代码块（克制暗面）----
    let codeBg = hex "#16171A"
    let codeHeaderBg = hex "#1C1D21"
    let codeBorder = hex "#2A2B30"
    let codeHeaderText = hex "#8E9098"
    let codeText = hex "#C8CAD0"
    let inlineCodeBg = SolidColorBrush(Color.Parse "#1C1B19", 0.06)
    let link = hex "#3A4554"
    let overlayScrim = SolidColorBrush(Color.Parse "#1C1B19", 0.28)

    /// 极轻阴影（对话框等）；日常控件尽量无阴影
    let shadowSoft = Color.Parse "#1C1B1922"
    let shadowFocus = Color.Parse "#2E343E28"
