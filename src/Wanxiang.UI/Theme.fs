namespace Wanxiang.UI

open Avalonia.Media

/// 万象视觉主题 — Kami 纸感体系（warm parchment + ink-blue accent）。
/// Tokens 源自 `kami/references/tokens.json` + `CHEATSHEET.md` 十条不变量。
/// 桌面端固定浅色（PWA 深色壳由 wwwroot/app.css @media 承担）。
/// 原则：暖灰 only、单墨蓝强调、serif 承重、hairline 分割、whisper 阴影。
module Theme =

    let private hex (s: string) = SolidColorBrush(Color.Parse s)

    // ---- 间距 / 圆角 / 栏高（4pt 基准；Kami radius scale 4·6·8·12·16·24·32）----
    let space1 = 4.0
    let space2 = 8.0
    let space3 = 12.0
    let space4 = 16.0
    let space5 = 20.0
    let space6 = 24.0
    /// Kami 默认卡片 4pt，按钮/输入 8pt，hero 32pt；聊天气泡取 12pt 保证纸感
    let radiusSm = 6.0
    let radiusMd = 8.0
    let radiusLg = 12.0
    let radiusXl = 16.0
    let radiusPill = 999.0
    let sidebarWidth = 272.0
    let readingWidth = 720.0
    let barHeight = 48.0
    let iconBtn = 32.0
    /// 线稿图标画布边长（Icons.fs SSOT）；32px 按钮内留 9px 呼吸
    let iconGlyph = 14.0
    let iconStroke = 1.5

    /// 品牌 logo 尺寸 SSOT（与 PWA splash `.splash-logo` 76px / 22px 圆角同比例）
    let logoSplashSize = 76.0
    let logoRadiusRatio = 22.0 / logoSplashSize
    let logoSizeSidebar = 28.0
    let logoSizeEmpty = 72.0
    let logoSizeAvatar = 28.0
    /// splash 轻阴影：0 8px 24px -8px rgba(20,19,19,.10) + 0 1px 3px 同系
    let logoShadowFar = Color.Parse "#14141318"

    /// 壳层水平内边距 SSOT：侧栏/顶栏/消息/输入共用同一刻度
    let sidebarInset = space4
    let chatInset = space4
    /// 区块之间呼吸（头-内容、内容-输入）
    let shellGap = space3

    // ---- Kami Surface（暖纸基底，零冷灰）----
    /// Page background — parchment #f5f4ed（Kami token）
    let bg = hex "#f5f4ed"
    /// Lifted card / input — ivory #faf9f5
    let panel = hex "#faf9f5"
    /// 侧栏：parchment 加深半阶，靠纸色自身区分而非冷灰描边
    let sidebar = hex "#f2f0e6"
    /// Primary border #e8e6dc · Soft row divider #e5e3d8
    let border = hex "#e8e6dc"
    let borderSubtle = SolidColorBrush(Color.Parse "#e5e3d8")
    let outlineVariant = hex "#e5e3d8"
    let surfaceVariant = hex "#e8e6dc"
    /// 细分隔线 warm sand 实色（hairline 用）
    let line = hex "#d8d5c8"

    // ---- Kami Text（四阶暖灰：near-black > dark-warm > olive > stone）----
    let text = hex "#141413"
    let muted = hex "#504e49"
    let faint = hex "#6b6a64"
    /// 兼容旧命名：secondary text
    let darkWarm = hex "#3d3d3a"
    let olive = hex "#504e49"
    let stone = hex "#6b6a64"

    // ---- Kami Brand（唯一主色 ink-blue #1B365D，占面 ≤5%）----
    let primary = hex "#1B365D"
    let onPrimary = hex "#faf9f5"
    /// Tag 默认 #E4ECF5，极浅 tint #EEF2F7（Kami 仅此两档 tint）
    let primaryContainer = hex "#E4ECF5"
    let onPrimaryContainer = hex "#1B365D"
    let secondaryContainer = hex "#EEF2F7"
    let onSecondaryContainer = hex "#1B365D"
    let toolChip = hex "#e8e6dc"
    let brandTint = hex "#EEF2F7"
    let tagBg = hex "#E4ECF5"

    /// 用户气泡：墨蓝底 + ivory 字（Kami accent 唯一大块用法）
    let userBubble = hex "#1B365D"
    let userText = hex "#faf9f5"
    let assistantBubble = hex "#faf9f5"
    let assistantBorder = hex "#e8e6dc"

    let errorContainer = hex "#f0e0d8"
    let onErrorContainer = hex "#8b4513"

    // ---- 状态层（暖灰洗色，不用亮紫/亮蓝）----
    let hover = SolidColorBrush(Color.Parse "#141413", 0.04)
    let pressed = SolidColorBrush(Color.Parse "#141413", 0.06)
    let selectedHover = hex "#e8e6dc"
    let selectedPressed = hex "#e5e3d8"

    // ---- 代码块（Kami 深面：deep-dark #141413 为基，ivory 字）----
    let codeBg = hex "#141413"
    let codeHeaderBg = hex "#1e1e1c"
    let codeBorder = hex "#30302e"
    let codeHeaderText = hex "#b0aea5"
    let codeText = hex "#faf9f5"
    let inlineCodeBg = SolidColorBrush(Color.Parse "#1B365D", 0.07)
    let inlineCodeFg = hex "#1B365D"
    let link = hex "#1B365D"
    let overlayScrim = SolidColorBrush(Color.Parse "#141413", 0.32)

    /// 极轻阴影（对话框等）；日常控件无阴影，靠纸色抬升
    let shadowSoft = Color.Parse "#14141316"
    let shadowFocus = Color.Parse "#1B365D1F"
    /// 品牌细线（对话框/选中态品牌边）
    let brandLine = hex "#1B365D"
