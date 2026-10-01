namespace Wanxiang.UI

/// Fluent 2 global spacing ramp —— 全应用唯一的间距与槽位刻度。
///
/// 官方 ramp（fluent2.microsoft.design/layout）：4px 基准，明确包含 2 / 6 / 10
/// 三档例外——它们用于图标内衬与像素网格对齐，不是"凑整"。因此本刻度不是
/// 「4 的倍数」，而是**官方 ramp 的完整取值**；任何不在这张表里的数值都是违规。
///
/// 规范权威：`wanxiang-ui` skill §2。
module Spacing =

    // ---- ramp 本体（官方取值，顺序即 ramp 顺序）----

    let spaceNone = 0.0
    /// size20：图标内衬、hairline 内衬、chip / tag 纵向内衬
    let spaceXXs = 2.0
    /// size40：紧凑堆叠间距、单行小控件纵向呼吸
    let spaceXs = 4.0
    /// size60：字段标签 / 校验行 / hint 的贴字段留白
    let spaceSm = 6.0
    /// size80：组件内部主间距、chip 横向内衬
    let spaceMd = 8.0
    /// size100：紧排 chip 横向内衬（需与 8 视觉等重时）
    let spaceLg = 10.0
    /// size120：字段外壳内边距、图标与文字间距
    let spaceXl = 12.0
    /// size160：组件之间、按钮横向内衬
    let space2xl = 16.0
    /// size200：侧栏行内边距、宽档组件间距
    let space3xl = 20.0
    /// size240：页面留白（中小窗档）、分组卡内边距、消息间距
    let space4xl = 24.0
    /// size280：宽屏档留白过渡
    let space5xl = 28.0
    /// size320：区块之间、桌面档页面留白
    let space6xl = 32.0
    /// size360：宽屏留白过渡
    let space7xl = 36.0
    /// size400：主分节之前
    let space8xl = 40.0
    /// size480：宽屏档页面留白
    let space10xl = 48.0
    /// size520：分节最大档
    let space11xl = 52.0
    /// size560：槽位宽度上限档
    let space12xl = 56.0

    /// ramp 全档（收敛校验用：任何间距、槽位、内边距必须 ∈ 本集合）。
    let ramp : float list =
        [ spaceNone; spaceXXs; spaceXs; spaceSm; spaceMd; spaceLg; spaceXl
          space2xl; space3xl; space4xl; space5xl; space6xl; space7xl; space8xl
          space10xl; space11xl; space12xl ]

    /// 槽位宽度档。槽位是**布局列宽**不是间距，但对不齐的根因正是它们各自
    /// 发明的 18 / 19 / 54 / 56 / 110，故与 ramp 同源收进阶梯。
    module Slot =

        /// 行内 / 菜单 / 状态点槽：全应用只此一值。
        /// 此前侧栏状态槽 19、菜单图标槽 18 分叉 1px——现在都读这里。
        let icon = space3xl

        /// 宽槽：附件状态槽与设置页数值槽此前同为 56 但各写一份。
        let wide = space12xl

        /// 更宽一档：关于页键名列（此前 110 → 112，进阶梯）。
        let wide2 = 112.0

        /// 槽位档全表（收敛校验用）。
        let all : float list = [ icon; wide; wide2 ]

    /// 圆角（Fluent 2 corner radius）。控件默认 8，卡片 12，浮层 16。
    module Radius =

        let xs = 4.0
        let sm = 6.0
        /// 控件默认圆角（按钮 / 输入壳 / 图标按钮 / 行）
        let md = 8.0
        /// 卡片 / 分组卡 / 浮层
        let lg = 12.0
        /// 对话框 / Hero 卡片
        let xl = 16.0
        let pill = 999.0

        let all : float list = [ xs; sm; md; lg; xl; pill ]

    /// 描边宽度：全应用只有四档。
    module Stroke =

        /// 1px：tag / 输入壳 / 按钮 / 分组卡 / 分隔线的唯一描边宽
        let thin = 1.0
        /// 2px：左缘强调条、选中行标记、焦点环外扩
        let thick = 2.0
        /// 3px：Markdown 引用块左缘（印刷重量，有意更宽一档）
        let quote = 3.0

        let all : float list = [ thin; thick; quote ]

    /// 字距档：层级靠字号与颜色，字距只做微调。
    module Tracking =

        /// 字段标签、tag
        let label = 0.3
        /// 空态主标题等强调文案
        let emphasis = 0.4
        /// 区块标签
        let section = 0.6
        /// 页面级测量标签（最宽）
        let display = 0.8

        let all : float list = [ label; emphasis; section; display ]

    /// 图标尺寸档：字形与命中区分离，落地经 Ui.setSquareTarget 只调命中区。
    module Glyph =

        /// 行内图标、chip / tag 内图标
        let sm = 14.0
        /// 按钮图标、工具栏、状态点
        let md = 16.0
        /// 空态锚点、功能入口、侧栏品牌区
        let lg = 20.0

        let all : float list = [ sm; md; lg ]

    /// 命中区下限（与字形分离）。依据见 `wanxiang-ui` skill §1.3。
    module Hit =

        /// 行内 / 脚注动作
        let inlineTarget = 20.0
        /// 桌面常规动作
        let regular = 24.0
        /// compact / 触控动作（Fluent 2 移动触控下限）
        let touch = 44.0

        let all : float list = [ inlineTarget; regular; touch ]

    /// 是否落在官方 ramp 上。收敛测试对所有间距 / 槽位断言本函数为真。
    let isRamp (value: float) = List.contains value ramp

    /// 槽位 / 圆角 / 描边 / 字距 / 字形 / 命中区各自是否合规。
    let isSlot (value: float) = List.contains value Slot.all
    let isRadius (value: float) = List.contains value Radius.all
    let isStroke (value: float) = List.contains value Stroke.all
    let isTracking (value: float) = List.contains value Tracking.all
    let isGlyph (value: float) = List.contains value Glyph.all
    let isHit (value: float) = List.contains value Hit.all

    /// 间距 / 槽位 / 圆角 / 描边的并集（几何扫描用）。
    let geometry : float list =
        ramp @ Slot.all @ Radius.all @ Stroke.all
