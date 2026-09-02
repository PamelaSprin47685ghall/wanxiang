namespace Wanxiang.UI

/// 精品阶段的“语义尺寸”，只定义设计节奏，不承担布局。
/// Avalonia 仍负责测量、换行、排列、滚动和虚拟化；这里避免不同 View
/// 为同一语义各自发明 17 / 18 / 19 之类的近似值。
module ReadingRhythm =

    /// 长文正文：适合 CJK + 拉丁混排的持续阅读节奏。
    let proseLineHeight (fontSize: float) = fontSize * 1.62

    /// 思考过程与次级长文本略紧于正文，保持“辅助材料”层级。
    let secondaryLineHeight (fontSize: float) = fontSize * 1.56

    /// Mono / 工具详情强调扫描效率，不应像正文一样松。
    let technicalLineHeight (fontSize: float) = fontSize * 1.52

    let uiBodyLineHeight = 20.0
    let captionLineHeight = 17.0
    let validationLineHeight = 16.0
    let helperLineHeight = 18.0
    let emptyStateLineHeight = 21.0

    /// Markdown 标题字号也属于阅读节奏，而不是组件临时放大的视觉特效。
    let headingFontSize (baseSize: float) (level: int) =
        baseSize
        + match level with
          | 1 -> 10.0
          | 2 -> 6.5
          | 3 -> 4.0
          | 4 -> 2.0
          | _ -> 1.0

    let paragraphGap = Tokens.space2
    let listItemGap = Tokens.space1
    let listBlockGap = Tokens.space2
    let blockGap = Tokens.space2
    let quoteBottomGap = Tokens.space3

    let headingBefore (level: int) =
        match level with
        | 1 -> Tokens.space6
        | 2 -> Tokens.space5
        | 3 -> Tokens.space4
        | _ -> Tokens.space3

    let headingAfter (level: int) =
        if level <= 2 then Tokens.space2 else Tokens.space1

    let listIndentStep = Tokens.space4
    let listMarkerWidth = Tokens.space5

module ControlMetrics =

    /// 常规文本按钮用 MinHeight，让 Avalonia 自己处理字体 / scale 后的真实高度。
    let textButtonMinHeight = 34.0
    let textButtonPaddingY = 7.0
    /// “保存”→“正在保存…”时按钮外框不能扩张，避免 footer 横向跳动。
    let pendingActionMinWidth = 96.0

    let textFieldTextMinHeight = 22.0
    let textFieldPaddingY = Tokens.space2

    let sidebarRowMinHeight = 44.0
    let sidebarRowPaddingY = 7.0
    let sidebarStatusLineHeight = ReadingRhythm.helperLineHeight
    let sidebarStateSlotWidth = 19.0
    let sidebarStateGlyphSize = 11.0
    let sidebarRunningDotSize = 6.0

    let composerInputMinHeight = 26.0
    let composerInputMaxHeight = 240.0
    let composerShellPaddingX = Tokens.space3
    let composerShellPaddingY = Tokens.space2
    let composerAttachmentNameMaxWidth = 160.0
    let composerAttachmentStateWidth = 54.0
    let composerAttachmentIconSlot = 15.0
    let composerModelMaxWidth = 240.0
    let composerModelCompactMaxWidth = 150.0

    let compactChipPaddingX = Tokens.space2
    let compactChipPaddingY = Tokens.space1

    let attachmentRowPaddingY = 6.0
    let attachmentNameMaxWidth = 260.0

    let menuItemMinHeight = 34.0
    let menuItemPaddingY = 7.0
    let menuIconSlotWidth = 18.0
    let menuRightSlotMinWidth = 16.0

    let toastAccentWidth = 2.0

    let settingsNavWidth = 196.0
    let settingsNavMinHeight = 38.0
    let settingsContentMaxWidth = 980.0

module ContentMetrics =

    let messageGap = Tokens.space6
    let messageEndBreathing = 48.0
    let scrollBottomThreshold = 48.0
    let scrollBottomRevealThreshold = 120.0
    let emptyStateMaxWidth = 420.0
    let toastMaxWidth = 560.0

