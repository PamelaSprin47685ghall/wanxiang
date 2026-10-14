namespace Wanxiang.UI

open System
open System.Threading
open Avalonia
open Avalonia.Media
open Avalonia.Threading

/// 设计令牌：全应用唯一的视觉常量来源。
///
/// 画笔是 **可变实例**：切主题时逐帧把 `Color` 从旧色补间到新色（时长消费
/// `MotionLedger.themeColorTransition`），已挂在视图树上的控件会自动重绘，
/// 不需要重建整棵树。补间只改颜色，不触及 transform、尺寸或位置。
/// 非画笔量（阴影颜色、字号）由 `Changed` 事件通知重建。
module Tokens =

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
    /// 页面主标题
    let fontDisplay = 25.0

    /// 不透明度阶梯：状态表达只切透明度，不改尺寸，避免布局跳动。
    let opacityDisabled = 0.5
    let opacitySubtle = 0.62
    let opacityPressed = 0.78

    /// hover 微暗：整条可点内容（消息头、操作条）hover 时从 1.0 压到 0.9。
    /// 有意不并入上面的状态阶梯：那几档服务禁用/按压/输入态，这一档服务 hover 提
    /// 示，比 opacityPressed 轻；MessageCard 曾有三处裸 0.9。
    let opacityHoverDim = 0.9

    /// 骨架不透明度组：骨架只切不透明度，不改尺寸（几何契约不变）。
    /// - base：静态骨架条（侧栏任务条等）的基准档；
    /// - breathMin：呼吸区间下限（上限历来是 1.0，见 MotionLedger.skeletonBreathFrame）；
    /// - reduced：减弱动效时的静态整屏值；此前该值只活在 ChatView 的注释里。
    let skeletonOpacityBase = 0.65
    let skeletonOpacityBreathMin = 0.55
    let skeletonOpacityReduced = 0.85


    // ---- 结构尺寸 ----
    let sidebarWidth = 284.0
    let sidebarMinWidth = 232.0
    let sidebarMaxWidth = 420.0
    /// 消息阅读列宽：CJK 正文每行 40–45 字最舒服
    let readingWidth = 748.0
    let barHeight = 52.0
    let iconButton = 30.0
    let iconGlyph = Spacing.Glyph.md
    let shellInset = Spacing.space2xl

    // ---- 品牌 ----
    let logoEmpty = 68.0
    let logoAvatar = 26.0

    let private theme = SharedTheme.manager

    let private toMode (m: ThemeMode) =
        match m with
        | Light -> Wanxiang2.ThemePalette.PaletteMode.Light
        | Dark -> Wanxiang2.ThemePalette.PaletteMode.Dark

    let mutable private mode = Light

    /// 旧视图与新组件共用 SharedTheme 的同一实例：补间只改这一批画笔。
    let private track (key: Wanxiang2.ThemePalette.PaletteKey) : SolidColorBrush = theme.Brush key




    // ---- 表面 ----
    let canvas = track Wanxiang2.ThemePalette.PaletteKey.Canvas
    let rail = track Wanxiang2.ThemePalette.PaletteKey.Rail
    let surface = track Wanxiang2.ThemePalette.PaletteKey.Surface
    let surfaceRaised = track Wanxiang2.ThemePalette.PaletteKey.SurfaceRaised
    /// rail 与 surface 之间的暖纸中间容器（分组容器、行条）：
    /// 比卡片轻、比侧栏亮，仍是暖纸层，不抢卡片层级
    let surfaceContainer = track Wanxiang2.ThemePalette.PaletteKey.SurfaceContainer
    let border = track Wanxiang2.ThemePalette.PaletteKey.Border
    let borderSoft = track Wanxiang2.ThemePalette.PaletteKey.BorderSoft
    /// 发丝线：最轻的分割/描边，比 borderSoft 再退一档；
    /// hairlineStrong 介于 borderSoft 与 border 之间，两档各自独立
    let hairline = track Wanxiang2.ThemePalette.PaletteKey.Hairline
    let hairlineStrong = track Wanxiang2.ThemePalette.PaletteKey.HairlineStrong
    let line = track Wanxiang2.ThemePalette.PaletteKey.Line

    // ---- 文字 ----
    let text = track Wanxiang2.ThemePalette.PaletteKey.Text
    let textMuted = track Wanxiang2.ThemePalette.PaletteKey.TextMuted
    let textFaint = track Wanxiang2.ThemePalette.PaletteKey.TextFaint
    let textOnAccent = track Wanxiang2.ThemePalette.PaletteKey.TextOnAccent

    // ---- 强调 ----
    let accent = track Wanxiang2.ThemePalette.PaletteKey.Accent
    let accentHover = track Wanxiang2.ThemePalette.PaletteKey.AccentHover
    let accentSoft = track Wanxiang2.ThemePalette.PaletteKey.AccentSoft
    let accentFaint = track Wanxiang2.ThemePalette.PaletteKey.AccentFaint
    let selected = track Wanxiang2.ThemePalette.PaletteKey.Selected
    let userBubble = track Wanxiang2.ThemePalette.PaletteKey.UserBubble
    let userBubbleText = track Wanxiang2.ThemePalette.PaletteKey.UserBubbleText

    /// 用户气泡选中底：半透明白只把深底提亮一档。气泡在浅/深两套调色板里都是深底
    /// （对比度测试锁住这一前提），叠加层因此主题无关，不随 Palette 换值。
    /// 移入 Tokens 是为了守住「视觉常量只出自 Tokens」的不变量，而非让它跟随主题。
    let userBubbleSelection = track Wanxiang2.ThemePalette.PaletteKey.UserBubbleSelection :> IBrush

    // ---- 状态 ----
    let success = track Wanxiang2.ThemePalette.PaletteKey.Success
    let warning = track Wanxiang2.ThemePalette.PaletteKey.Warning
    let danger = track Wanxiang2.ThemePalette.PaletteKey.Danger
    let dangerSoft = track Wanxiang2.ThemePalette.PaletteKey.DangerSoft
    let info = track Wanxiang2.ThemePalette.PaletteKey.Info
    let infoSoft = track Wanxiang2.ThemePalette.PaletteKey.InfoSoft

    // ---- 代码 ----
    let codeBg = track Wanxiang2.ThemePalette.PaletteKey.CodeBg
    let codeHeaderBg = track Wanxiang2.ThemePalette.PaletteKey.CodeHeaderBg
    let codeBorder = track Wanxiang2.ThemePalette.PaletteKey.CodeBorder
    let codeText = track Wanxiang2.ThemePalette.PaletteKey.CodeText
    let codeMuted = track Wanxiang2.ThemePalette.PaletteKey.CodeMuted
    let codeKeyword = track Wanxiang2.ThemePalette.PaletteKey.CodeKeyword
    let codeString = track Wanxiang2.ThemePalette.PaletteKey.CodeString
    let codeComment = track Wanxiang2.ThemePalette.PaletteKey.CodeComment
    let codeNumber = track Wanxiang2.ThemePalette.PaletteKey.CodeNumber
    let codeType = track Wanxiang2.ThemePalette.PaletteKey.CodeType
    let codeFunction = track Wanxiang2.ThemePalette.PaletteKey.CodeFunction
    let codeMeta = track Wanxiang2.ThemePalette.PaletteKey.CodeMeta
    let codeVariable = track Wanxiang2.ThemePalette.PaletteKey.CodeVariable
    let codeOperator = track Wanxiang2.ThemePalette.PaletteKey.CodeOperator
    let codeAddition = track Wanxiang2.ThemePalette.PaletteKey.CodeAddition
    let codeDeletion = track Wanxiang2.ThemePalette.PaletteKey.CodeDeletion

    // ---- 交互层 ----
    let scrim = track Wanxiang2.ThemePalette.PaletteKey.Scrim
    let hover = track Wanxiang2.ThemePalette.PaletteKey.Hover
    let pressed = track Wanxiang2.ThemePalette.PaletteKey.Pressed

    let tableStripe = track Wanxiang2.ThemePalette.PaletteKey.TableStripe
    let tableHeader = track Wanxiang2.ThemePalette.PaletteKey.TableHeader

    /// 主题变化通知：字号/阴影等非画笔量需要重建的地方订阅它。
    let Changed = Event<ThemeMode>()

    let current () = mode
    let colors () = Palette.ofMode mode

    let isDark () = mode = Dark

    /// 主题颜色补间由 SharedTheme.manager 驱动（同代际帧、减弱动效即时到位）。
    let apply (next: ThemeMode) : unit =
        mode <- next
        theme.Apply(toMode next)
        Changed.Trigger next

    /// 立即把进行中的补间收尾到目标色并停帧（决策 430）。必须在 UI 线程调用。
    let completeTransition () : unit = theme.CompleteTransition()

    let shadowPopup () = theme.ShadowPopup()

    let shadowDialog () = theme.ShadowDialog()

    /// 内嵌字体：browser-wasm 拿不到宿主系统字体，桌面与 PWA 必须用同一份内嵌字形。
    ///
    /// 正文用**比例宽度**的 Sarasa Gothic SC。此前用的是等宽的 Sarasa Term SC，
    /// 于是中文段落里的拉丁词全部被拉成等宽，观感像日志而非文章。
    let fontFamily = SharedTheme.manager.FontFamily.Value


    let thickness (all: float) = Thickness all
