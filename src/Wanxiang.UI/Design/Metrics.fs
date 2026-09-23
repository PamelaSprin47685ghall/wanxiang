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
    let headingLineHeight = 26.0

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
    /// 字段标签/校验行/hint 的左缘内缩：让说明文字与控件内容左缘对齐而不是贴边框。
    let fieldInsetX = 2.0

    /// 设置页字号值的固定槽宽（SettingsGeneral 原裸 56.0）：「关于」页键名列槽宽（110.0）。
    let fontSizeValueMinWidth = 56.0
    let aboutKeyMinWidth = 110.0

    /// 常规行的最小高度：设置页开关行与侧栏会话行同一行高语义的唯一真源。
    /// 此前 44.0 在 SettingsGeneral 与这里各写一份、同值不同源。
    let rowMinHeight = 44.0

    let sidebarRowMinHeight = rowMinHeight
    /// 设置页开关行的最小高度：与侧栏行同一语义，引用 rowMinHeight。
    let settingsRowMinHeight = rowMinHeight
    let sidebarRowPaddingY = 7.0
    let sidebarStatusLineHeight = ReadingRhythm.helperLineHeight
    let sidebarStateSlotWidth = 19.0
    let sidebarStateGlyphSize = 11.0
    let sidebarRunningDotSize = 6.0
    /// 行内/分节状态点直径：侧栏运行点、分节状态点同一语义的唯一真源
    ///（此前只有侧栏一处命名，其它位置各写裸 6.0）。
    let statusDotSize = 6.0
    /// chip 内部小圆点直径：比状态点小一档，标记 chip 的附属语义。
    let chipDotSize = 5.0
    /// 左缘强调条统一档：侧栏选中行、toast、思考竖线共用同一 2.0。
    /// 引用块用更宽的 quoteEdgeWidth（印刷引用条的视觉重量，两档有意保留）。
    let accentEdgeWidth = 2.0
    /// 选中行左缘强调条宽：非色线索，与悬停态区分；统一档见 accentEdgeWidth。
    let sidebarSelectedEdgeWidth = accentEdgeWidth
    /// 引用块左缘强调条：比通用 accentEdgeWidth 宽一档，Markdown 引用的既有意图。
    let quoteEdgeWidth = 3.0
    /// 侧栏外框/页脚分隔线宽：唯一来源，避免各处自发明 1.0。
    let sidebarDividerWidth = 1.0
    /// 控件/发丝线描边宽的全应用唯一档：tag、输入壳、按钮、chip、分组卡的 1px
    /// 边框都引用它；此前这些位置各自写 Thickness 1.0 / Height = 1.0。
    let borderWidth = 1.0
    /// 行内标题+预览垂直间距：紧凑堆叠是语义，不是“顺手写 0”。
    let sidebarRowContentSpacing = 0.0

    let composerInputMinHeight = 30.0
    let composerMaxHeight = 240.0
    let composerInputMaxHeight = 240.0
    let composerShellPaddingX = Tokens.space3
    let composerShellPaddingY = Tokens.space2
    let composerAttachmentNameMaxWidth = 160.0
    let composerAttachmentStateWidth = 54.0
    let composerAttachmentIconSlot = 15.0
    let composerModelMaxWidth = 240.0
    let composerModelCompactMaxWidth = 150.0

    /// chip 内边距的唯一真源（横向 8 = space2、纵向 2 = compactRowPaddingY）。
    /// 此前只有 Composer 的 chip 写着 padding、没有语义名；现收敛为一对语义名，
    /// 旧的 compactChipPadding* 降为别名（见下），全应用 chip 只有这一处定义。
    let chipPaddingX = Tokens.space2
    let chipPaddingY = Tokens.compactRowPaddingY

    /// 兼容别名：仍被 Composer 的附件 chip / 模型 chip 引用，引用同源于上面的
    /// chipPadding*——别名不持有自己的值，只是同一个量的旧名字。
    /// 纵向前者是 space1(4)，现统一到 compactRowPaddingY(2)：chip 与标签同为单行
    /// 小控件，纵向密度不应分叉。旧值 4 不由任何测试固定；反过来，
    /// DesignConvergenceTests.ChipDensityHasSingleSourceUnderBothNames 钉住的是
    /// compactChipPaddingX/Y 与 chipPaddingX/Y 的相等关系（别名必须跟真源走）。
    let compactChipPaddingX = chipPaddingX
    let compactChipPaddingY = chipPaddingY

    /// tag（小标签）的横向内边距：7.0 是它的既有外观值，DesignConvergence
    /// 测试钉住 Padding(7.0, tightRowPaddingY)。chip 横向用更宽的 space2，
    /// tag 纵向仍是 Tokens.tightRowPaddingY：tag/chip 横向差一档是有意的两档。
    let tagPaddingX = 7.0

    let attachmentRowPaddingY = 6.0
    let attachmentNameMaxWidth = 260.0

    let menuItemMinHeight = 34.0
    let menuItemPaddingY = 7.0
    let menuIconSlotWidth = 18.0
    let menuRightSlotMinWidth = 16.0

    let toastAccentWidth = accentEdgeWidth

    let settingsNavWidth = 196.0
    let settingsNavMinHeight = 38.0
    let settingsContentMaxWidth = 980.0

    /// Menu.selectButton 的垂直内边距：只读按钮比菜单项（7）再紧一档的手感。
    let selectButtonPaddingY = 6.0

    /// 多行输入最小高度：表单指令类文本框的标准档。
    let textAreaMinHeight = 96.0
    /// 长文本特例（消息内容、大段说明）：160 是"长文本"语义而非 96 的近似值，有意保留。
    let textAreaLongMinHeight = 160.0

    /// 忙指示 spinner：常规 14、紧凑 11，同一组件两档尺寸（非近似值）。
    let spinnerSize = 14.0
    let spinnerCompactSize = 11.0
    /// spinner 的笔画宽与承载弧线的画布尺寸：笔画决定 spinner 的视觉重量，
    /// 画布是弧线几何（半径 6.4 的圆）被缩放前所在的坐标系，不改数值只挪 literals。
    let spinnerStrokeWidth = 1.8
    let spinnerCanvasSize = 16.0

    /// Ui.toggle 组件几何：track 34×20、knob 14、水平内插 3。
    let toggleTrackWidth = 34.0
    let toggleTrackHeight = 20.0
    let toggleKnobSize = 14.0
    let toggleKnobInsetX = 3.0

    /// 空态主操作按钮（136×38）与空态标题最小宽。
    let emptyStateActionMinWidth = 136.0
    let emptyStateActionHeight = 38.0
    let emptyStateTitleMinWidth = 120.0

    /// 「回到底」按钮最小宽：与 pendingActionMinWidth 同为 96 但语义不同
    ///（滚动定位 vs 保存占位），按 MotionLedger 纪律分记，不合档。
    let scrollToBottomMinWidth = 96.0

    /// 错误卡片「重试」按钮最小宽。
    let retryButtonMinWidth = 80.0

    /// 流式光标几何（呼吸区间在 Tokens.opacityCaretBreath*）：宽 7、高 15、圆角 1.5。
    let caretWidth = 7.0
    let caretHeight = 15.0
    let caretRadius = 1.5

    /// Markdown 无序列表圆点：直径 4、圆角 2。
    let listMarkerDotSize = 4.0
    let listMarkerDotRadius = 2.0

    /// Markdown 任务框：14 见方、1.4 描边、勾形 10。
    let taskBoxSize = 14.0
    let taskBoxBorderWidth = 1.4
    let taskCheckGlyphSize = 10.0

    /// 骨架屏头像边长：与真实列表头像同一 28 见方，骨架换成真卡时轮廓不变、
    /// 不产生跳变。此前 ChatView 骨架屏的头像在两处各写裸 28.0，无语义名。
    let skeletonAvatarSize = 28.0

module ContentMetrics =

    let messageGap = Tokens.space6
    let messageEndBreathing = 48.0
    let scrollBottomThreshold = 48.0
    let scrollBottomRevealThreshold = 120.0
    let emptyStateMaxWidth = 480.0
    let toastMaxWidth = 560.0

