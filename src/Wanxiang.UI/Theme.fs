namespace Wanxiang.UI

open Avalonia.Media

/// 万象视觉主题（灵感：Material You 默认靛蓝；clean-room 独立实现）。
/// 桌面端固定浅色（深色由 PWA 通过 prefers-color-scheme 提供）。
/// 间距刻度：4 的倍数；桌面与 PWA 共用同一套常量，避免两端观感漂移。
module Theme =

    let private hex (s: string) = SolidColorBrush(Color.Parse s)

    // ---- 间距 / 圆角 / 栏高（主壳对齐 SSOT）----
    let space1 = 4.0
    let space2 = 8.0
    let space3 = 12.0
    let space4 = 16.0
    let space5 = 20.0
    let space6 = 24.0
    let radiusSm = 8.0
    let radiusMd = 12.0
    let radiusLg = 16.0
    let radiusXl = 20.0
    let radiusPill = 999.0
    /// 侧栏固定宽
    let sidebarWidth = 280.0
    /// 消息 / 输入 / 顶栏内容同宽阅读列
    let readingWidth = 720.0
    /// 侧栏顶栏、聊天顶栏、底栏统一高度
    let barHeight = 52.0
    /// 图标按钮边长（附件 / 发送 / 新建）
    let iconBtn = 34.0

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

    // ---- 轻量语义色（灵感：Material You 角色名；柔和灰调，单色而非全套调色板）----
    /// 输入壳等次级表面：略冷于 panel，避免纯白硬块
    let surfaceVariant = hex "#E8E7F0"
    /// 分割线 / 输入壳描边：比 border 略沉、比 text 远，深色壳层亦不刺眼
    let outlineVariant = hex "#C9C8D4"
    /// 错误条 / 状态警示底：柔和玫瑰灰，非高饱和红
    let errorContainer = hex "#F5E0DE"

    let hover = hex "#0D1A1B21"      // 5% 黑
    let pressed = hex "#141A1B21"    // 8% 黑
    let selectedHover = hex "#D4DAFF"
    let selectedPressed = hex "#C6CEFF"

    // ---- 代码块（与消息卡片同阶，克制暗面；不在 PWA 深色媒体查询里再翻转）----
    let codeBg = hex "#0D1117"
    let codeHeaderBg = hex "#161B22"
    let codeBorder = hex "#21262D"
    let codeHeaderText = hex "#8B949E"
    let codeText = hex "#C9D1D9"
    let inlineCodeBg = SolidColorBrush(Color.Parse "#12141A14") // 8% 靛灰底
    let link = primary
    let overlayScrim = SolidColorBrush(Color.Parse "#52000000") // 32% 黑，柔化遮罩
