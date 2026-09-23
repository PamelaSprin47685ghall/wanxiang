namespace Wanxiang.UI

/// 跨页面共享的空间政策。这里放“什么时候换布局、临时区域最多占多少空间”，
/// 与颜色/字号等视觉 Tokens 分开，避免组件各自复制 magic breakpoint。
module LayoutPolicy =

    /// 主壳、Settings 共用的 compact 模式断点。
    let compactBreakpoint = 720.0

    /// 桌面窗口最小宽度（契约 C7）。必须小于 compactBreakpoint，
    /// 否则把窗口缩到最窄也进不了 compact 抽屉分支。
    let desktopMinWidth = 700.0

    /// 小型参数表单由双列退化为单列的局部断点。
    let formSingleColumnBreakpoint = 440.0

    /// 宽屏/大窗水平留白锚点：桌面从 compactBreakpoint(720) 起步，此档只用于
    /// horizontalInset 的最宽一档（space12）；不参与任何紧凑模式判定。
    let wideLayoutBreakpoint = 1024.0

    /// 桌面侧栏拖拽把手的布局占位；视觉本身保持透明。
    let sidebarSplitterWidth = 4.0

    /// compact / touch 场景主要 icon action 的点击面积下限（触控可达）：命中 surface
    /// 抬到 44，glyph 视觉不变。原 38 档提升为触控下限后，它是全应用图标动作
    /// hit surface 的唯一来源（ChatView / Composer / Sidebar 的 compact 档共用）。
    /// 与 ControlMetrics.rowMinHeight 同值但语义不同（命中下限 vs 行高），分记；
    /// 落地经 Ui.setSquareTarget（只调 hit surface 不改 glyph）。
    let compactActionTarget = 44.0

    /// 消息脚注、代码块工具栏的紧凑动作面积。
    let inlineActionTarget = 30.0

    /// 工具结果 / 技术细节首次展开时的内部滚动上限。
    let expandedDetailMaxHeight = 360.0

    /// Dialog 内部表单/帮助正文的阅读窗口；Overlay 外层仍会再按实时 viewport clamp。
    let dialogContentMaxHeight = 560.0

    /// Composer 附件草稿最多常驻的高度，更多附件由局部滚动承担。
    let attachmentDraftMaxHeight = 104.0
    let pendingMessagesMaxHeight = 168.0
    let pendingMessagePreviewMaxHeight = 76.0

    /// 嵌套滚动容器与外层滚动条之间的安全留白（避免滚动条紧贴或重叠）。
    let nestedScrollGutter = Tokens.space2

    /// Toast 长错误正文的局部阅读高度。
    let toastBodyMaxHeight = 160.0

    /// 浮层菜单与下拉选择器的内部滚动上限：show / showGrouped 共用，长菜单在浮层内
    /// 滚动，不把卡片撑出视口；OverlayHost 外层仍会再按实时 viewport clamp。
    let menuMaxHeight = 380.0

    /// 用户消息气泡比整条阅读列更窄，避免自己的长段落铺满整屏。
    let userMessageMaxWidth = 560.0

    /// 页面级水平留白（自适应）：窄屏紧凑省地、宽屏舒展留白。
    /// 四档锚定既有断点，与 compact 判定同源自洽：
    /// - < formSingleColumnBreakpoint(440) 手机窄屏：space4(16)
    /// - < compactBreakpoint(720) 中小窗/近桌面：space6(24)
    /// - < wideLayoutBreakpoint(1024) 桌面：space8(32)
    /// - 其余 宽屏/大窗：space12(48)
    /// desktopMinWidth(700) < compactBreakpoint(720) 已保证最窄窗口也进 compact，
    /// 故 24/32 的分界统一落在 compactBreakpoint，桌面留白从 720 起。
    /// 纯函数、无副作用；调用方按实时 viewport 宽度取用，不触发布局几何动画。
    let horizontalInset (width: float) : float =
        if width < formSingleColumnBreakpoint then Tokens.space4
        elif width < compactBreakpoint then Tokens.space6
        elif width < wideLayoutBreakpoint then Tokens.space8
        else Tokens.space12

