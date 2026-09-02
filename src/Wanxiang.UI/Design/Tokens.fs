namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Media

/// 设计令牌：全应用唯一的视觉常量来源。
///
/// 画笔是 **可变实例**：切主题时只改 `Color`，已挂在视图树上的控件会自动重绘，
/// 不需要重建整棵树。非画笔量（阴影颜色、字号）由 `Changed` 事件通知重建。
module Tokens =

    // ---- 间距（4pt 基准）----
    let space1 = 4.0
    let space2 = 8.0
    let space3 = 12.0
    let space4 = 16.0
    let space5 = 20.0
    let space6 = 24.0
    let space8 = 32.0
    let space10 = 40.0

    // ---- 圆角 ----
    let radiusXs = 4.0
    let radiusSm = 6.0
    let radiusMd = 9.0
    let radiusLg = 14.0
    let radiusXl = 20.0
    let radiusPill = 999.0

    // ---- 字号阶梯 ----
    /// 元信息、角标
    let fontMicro = 10.5
    /// 辅助说明
    let fontCaption = 11.5
    /// 界面小字（按钮、标签）
    let fontSmall = 12.5
    /// 界面正文
    let fontBody = 13.5
    /// 阅读正文（消息）
    let fontReading = 14.5
    /// 小标题
    let fontTitle = 16.0
    /// 区块标题
    let fontHeading = 19.0
    /// 页面主标题
    let fontDisplay = 25.0

    /// 键盘焦点环的外扩宽度。用外阴影画而不是加边框：
    /// 改边框粗细会让按钮内容跳一下，而焦点本该是「无位移的提示」。
    let focusRingSpread = 2.0

    /// 带框块（代码块、表格）的统一内边距。两者常在同一段回答里前后出现，
    /// 各自取值会让左缘差几个像素，读起来像没对齐的两张卡片。
    let blockPaddingX = space3
    let blockPaddingY = space2

    // ---- 结构尺寸 ----
    let sidebarWidth = 284.0
    let sidebarMinWidth = 232.0
    let sidebarMaxWidth = 420.0
    /// 消息阅读列宽：CJK 正文每行 40–45 字最舒服
    let readingWidth = 748.0
    let barHeight = 52.0
    let iconButton = 30.0
    let iconGlyph = 15.0
    let iconStroke = 1.6
    let shellInset = space4

    // ---- 品牌 ----
    let logoSplash = 76.0
    let logoRadiusRatio = 22.0 / 76.0
    let logoSidebar = 26.0
    let logoEmpty = 68.0
    let logoAvatar = 26.0

    let mutable private palette = Palette.light
    let mutable private mode = Light

    let private brushes = System.Collections.Generic.List<SolidColorBrush * (Palette -> Color)>()

    let private track (pick: Palette -> Color) : SolidColorBrush =
        let brush = SolidColorBrush(pick palette)
        brushes.Add(brush, pick)
        brush

    // ---- 表面 ----
    let canvas = track (fun p -> p.canvas)
    let rail = track (fun p -> p.rail)
    let surface = track (fun p -> p.surface)
    let surfaceRaised = track (fun p -> p.surfaceRaised)
    let border = track (fun p -> p.border)
    let borderSoft = track (fun p -> p.borderSoft)
    let line = track (fun p -> p.line)

    // ---- 文字 ----
    let text = track (fun p -> p.text)
    let textMuted = track (fun p -> p.textMuted)
    let textFaint = track (fun p -> p.textFaint)
    let textOnAccent = track (fun p -> p.textOnAccent)

    // ---- 强调 ----
    let accent = track (fun p -> p.accent)
    let accentHover = track (fun p -> p.accentHover)
    let accentSoft = track (fun p -> p.accentSoft)
    let accentFaint = track (fun p -> p.accentFaint)
    let selected = track (fun p -> p.selected)
    let userBubble = track (fun p -> p.userBubble)
    let userBubbleText = track (fun p -> p.userBubbleText)

    // ---- 状态 ----
    let success = track (fun p -> p.success)
    let warning = track (fun p -> p.warning)
    let danger = track (fun p -> p.danger)
    let dangerSoft = track (fun p -> p.dangerSoft)

    // ---- 代码 ----
    let codeBg = track (fun p -> p.codeBg)
    let codeHeaderBg = track (fun p -> p.codeHeaderBg)
    let codeBorder = track (fun p -> p.codeBorder)
    let codeText = track (fun p -> p.codeText)
    let codeMuted = track (fun p -> p.codeMuted)
    let codeKeyword = track (fun p -> p.codeKeyword)
    let codeString = track (fun p -> p.codeString)
    let codeComment = track (fun p -> p.codeComment)
    let codeNumber = track (fun p -> p.codeNumber)
    let codeType = track (fun p -> p.codeType)
    let codeFunction = track (fun p -> p.codeFunction)
    let codeMeta = track (fun p -> p.codeMeta)
    let codeVariable = track (fun p -> p.codeVariable)
    let codeOperator = track (fun p -> p.codeOperator)
    let codeAddition = track (fun p -> p.codeAddition)
    let codeDeletion = track (fun p -> p.codeDeletion)

    // ---- 交互层 ----
    let scrim = track (fun p -> p.scrim)
    let hover = track (fun p -> p.hover)
    let pressed = track (fun p -> p.pressed)

    let inlineCodeBg = track (fun p -> p.inlineCode)
    let tableStripe = track (fun p -> p.tableStripe)
    let tableHeader = track (fun p -> p.tableHeader)

    /// 主题变化通知：字号/阴影等非画笔量需要重建的地方订阅它。
    let Changed = Event<ThemeMode>()

    let current () = mode
    let colors () = palette

    let isDark () = mode = Dark

    /// 应用主题。已有画笔实例原地改色，因此不必重建视图树。
    let apply (next: ThemeMode) : unit =
        mode <- next
        palette <- Palette.ofMode next
        for brush, pick in brushes do
            brush.Color <- pick palette
        Changed.Trigger next

    // ---- 阴影（跟随主题，需在重建时重新读取）----
    /// 极轻抬升：输入框、卡片
    let shadowSoft () =
        BoxShadows(BoxShadow(OffsetX = 0.0, OffsetY = 2.0, Blur = 10.0, Spread = -4.0, Color = palette.shadow))

    /// 中等抬升：下拉、气泡
    let shadowPopup () =
        BoxShadows(
            BoxShadow(OffsetX = 0.0, OffsetY = 8.0, Blur = 26.0, Spread = -8.0, Color = palette.shadowStrong))

    /// 对话框
    let shadowDialog () =
        BoxShadows(
            BoxShadow(OffsetX = 0.0, OffsetY = 18.0, Blur = 48.0, Spread = -12.0, Color = palette.shadowStrong))

    /// 内嵌字体：browser-wasm 拿不到宿主系统字体，桌面与 PWA 必须用同一份内嵌字形。
    ///
    /// 正文用**比例宽度**的 Sarasa Gothic SC。此前用的是等宽的 Sarasa Term SC，
    /// 于是中文段落里的拉丁词全部被拉成等宽，观感像日志而非文章。
    let fontFamily = FontFamily("avares://Wanxiang.UI/Assets/Fonts/#Sarasa Gothic SC")

    /// 代码与等宽场景。等宽子集只含拉丁与符号，因此把正文字体列为回落，
    /// 否则代码或工具参数里出现中文会渲染成豆腐块。
    let monoFontFamily =
        FontFamily("avares://Wanxiang.UI/Assets/Fonts/#Sarasa Term SC, Sarasa Gothic SC")

    let thickness (all: float) = Thickness all
    let corner (all: float) = CornerRadius all
