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

    /// 桌面侧栏拖拽把手的布局占位；视觉本身保持透明。
    let sidebarSplitterWidth = 4.0

    /// compact / touch 场景主要 icon action 的点击面积；glyph 不随之放大。
    let compactActionTarget = 38.0

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

    /// Toast 长错误正文的局部阅读高度。
    let toastBodyMaxHeight = 160.0

    /// 用户消息气泡比整条阅读列更窄，避免自己的长段落铺满整屏。
    let userMessageMaxWidth = 560.0

