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

    // ---- 间距（4pt 基准）----
    let space1 = 4.0
    let space2 = 8.0
    let space3 = 12.0
    let space4 = 16.0
    let space5 = 20.0
    let space6 = 24.0
    let space8 = 32.0
    let space10 = 40.0
    /// 宽屏水平留白顶档：4pt 基准延伸，供 LayoutPolicy.horizontalInset 的最宽一档。
    let space12 = 48.0

    /// 紧凑纵向微节奏族："同一行密度"曾散成 1.0 / 2.0 / 3.0 三种裸值
    ///（tag / chip / 状态 pill、校验消息、字段标签、hint、toggle、披露头等）。
    /// 三档各自对应真实共享语义，保留三档、不强行压平。除行内边距外，
    /// tight / compact 两档还充当紧凑堆叠（菜单面板、操作行、紧凑纵向栈）
    /// 的 mini 垂直间距：
    /// - tight（1）：单行小控件（tag、chip、pill）的纵向呼吸，以及紧凑
    ///   堆叠（菜单等紧排纵向栈）的 mini 间距；
    /// - compact（2）：紧凑行容器的上下内边距，以及紧凑堆叠（操作行、
    ///   紧凑纵向栈）的 mini 垂直间距；
    /// - field（3）：字段标签 / 校验行 / hint 这类贴字段的纵向留白。
    let tightRowPaddingY = 1.0
    let compactRowPaddingY = 2.0
    let fieldRowPaddingY = 3.0

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

    /// 字距档：0.2–0.8 的裸 letterSpacing 曾散在字段标签、空态标题、区块标签、
    /// 侧栏测量标签等处。四档各有既有调用点与不同视觉重量，保留四档：
    /// label（字段标签）→ emphasis（空态标题等强调）→ section（区块标签）
    /// → display（侧栏测量标签，最宽）。
    let letterSpacingLabel = 0.3
    let letterSpacingEmphasis = 0.4
    let letterSpacingSection = 0.6
    let letterSpacingDisplay = 0.8

    /// 键盘焦点环的外扩宽度。用外阴影画而不是加边框：
    /// 改边框粗细会让按钮内容跳一下，而焦点本该是「无位移的提示」。
    let focusRingSpread = 2.0

    /// 图文基线微调：15~16pt 图标与 14pt 文本在 StackPanel/DockPanel 顶对齐时约 2pt 视差。
    /// 统一用它代替散落的 Margin(0,2,0,0)，避免“差不多对齐”。
    let iconBaselineNudge = 2.0

    /// 不透明度阶梯：状态表达只切透明度，不改尺寸，避免布局跳动。
    let opacityDisabled = 0.5
    let opacitySubtle = 0.62
    let opacityComposerDisabled = 0.72
    let opacityPressed = 0.78
    /// 流式光标呼吸的不透明度区间：只改 opacity、不改尺寸；
    /// 节奏由 MotionLedger.caretBreathPhase + caretBreathFrame 驱动（MessageCard 已消费）
    let opacityCaretBreathMin = 0.55
    let opacityCaretBreathMax = 1.0
    /// 复制确认反馈淡出时经过的中间不透明度（随后回到 1.0），
    /// 节奏消费 MotionLedger.copyConfirmationFade（MessageCard 复制确认已消费）
    let opacityCopyConfirmFade = 0.6

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
    /// rail 与 surface 之间的暖纸中间容器（分组容器、行条）：
    /// 比卡片轻、比侧栏亮，仍是暖纸层，不抢卡片层级
    let surfaceContainer = track (fun p -> p.surfaceContainer)
    /// 柔和表面微底色（思考过程、次级容器背景）
    let surfaceSoft = track (fun p -> Color.FromArgb(0x55uy, p.surface.R, p.surface.G, p.surface.B))
    let border = track (fun p -> p.border)
    let borderSoft = track (fun p -> p.borderSoft)
    /// 发丝线：最轻的分割/描边，比 borderSoft 再退一档；
    /// hairlineStrong 介于 borderSoft 与 border 之间，两档各自独立
    let hairline = track (fun p -> p.hairline)
    let hairlineStrong = track (fun p -> p.hairlineStrong)
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

    /// 用户气泡选中底：半透明白只把深底提亮一档。气泡在浅/深两套调色板里都是深底
    /// （对比度测试锁住这一前提），叠加层因此主题无关，不随 Palette 换值。
    /// 移入 Tokens 是为了守住「视觉常量只出自 Tokens」的不变量，而非让它跟随主题。
    let userBubbleSelection = SolidColorBrush(Color.FromArgb(0x59uy, 255uy, 255uy, 255uy)) :> IBrush

    // ---- 状态 ----
    let success = track (fun p -> p.success)
    let warning = track (fun p -> p.warning)
    let danger = track (fun p -> p.danger)
    let dangerSoft = track (fun p -> p.dangerSoft)
    let info = track (fun p -> p.info)
    let infoSoft = track (fun p -> p.infoSoft)

    // ---- 代码 ----
    let codeBg = track (fun p -> p.codeBg)
    let codeBlockBackground = codeBg
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

    // ---- 主题颜色补间 ----
    // 只补间 Color：不触碰 RenderTransform、尺寸、边距与 BoxShadow，
    // 因此 MotionLedger.geometryAnimationAllowed 保持 false，不引入任何几何/位移/尺寸动画。

    /// 补间帧节奏：由主题过渡令牌推导（200ms / 12 帧 ≈ 16.7ms）。散落裸毫秒只写在
    /// MotionLedger，这里不再发出新的裸毫秒。
    let private colorTransitionFrame =
        TimeSpan.FromTicks(max 1L (MotionLedger.themeColorTransition.Ticks / 12L))

    /// 补间有效时长：尊重 MotionPolicy 的减弱动效偏好，降级为零（同步到位）。
    let private colorTransitionDuration () =
        if MotionPolicy.isReduced () then TimeSpan.Zero else MotionLedger.themeColorTransition

    /// 单通道插值（纯逻辑）。t = 1 时四舍五入精确落在目标通道，收尾无偏差。
    let private lerpChannel (from: byte) (target: byte) (t: float) =
        let raw = Math.Round(float from + (float target - float from) * t) |> int
        if raw < 0 then 0uy elif raw > 255 then 255uy else byte raw

    let private lerpColor (from: Color) (target: Color) (t: float) : Color =
        Color.FromArgb(
            lerpChannel from.A target.A t,
            lerpChannel from.R target.R t,
            lerpChannel from.G target.G t,
            lerpChannel from.B target.B t)

    /// 每支画笔本段补间的起点色。apply 时从 brush.Color 现读：补间途中再切换时
    /// 起点就是当前中间色，后来的切换因此接管前一段补间，两段不会叠加。
    let mutable private transitionFrom = System.Collections.Generic.Dictionary<SolidColorBrush, Color>()
    let mutable private transitionStarted = DateTime.UtcNow
    /// 当前补间段的时长（ms）。接管时按它计算前段已滚到哪个逻辑进度。
    let mutable private transitionSegmentMs = MotionLedger.themeColorTransition.TotalMilliseconds

    // ---- 帧调度 ----
    // 为什么不用 DispatcherTimer 驱动补间：本项目的无显示测试制度是
    // `SetupWithoutStarting` + `Dispatcher.UIThread.RunJobs()`，不起消息循环。
    // 实测（Avalonia 12.1.2，headless + Skia）：DispatcherTimer 在此制度下
    // 每个实例每次 `Start` 至多被服务一个 tick 就再也不响（进程里是否曾挂载过
    // 视图树还会影响它甚至一次都不响）。补间需要 200ms 内约 12 帧连续推进，
    // DispatcherTimer 在这里不可依赖；只用真实时钟的计时器逐帧往 dispatcher
    // 投递 job：生产环境按墙钟平滑逐帧、颜色计算与状态修改全部收敛在 UI 线程，
    // 无显示测试里这些 job 也能被 `RunJobs` 稳定泵起，补间进度因此可验证。
    let mutable private frameTimer : System.Threading.Timer option = None
    /// 补间代次：apply 接管（中途重新计时）时递增；过期帧不负责收尾停帧，
    /// 免得上一段补间的迟到一帧误停新补间。
    let mutable private frameGeneration = 0

    let private stopFrames () =
        match frameTimer with
        | Some timer ->
            frameTimer <- None
            timer.Dispose()
        | None -> ()

    /// 在 UI 线程上按墙钟推进一帧。t 由 `transitionStarted` 实时计算，
    /// 因此中途被接管时迟到的帧也只是按新起点画一帧，不会叠加两段补间。
    let private advanceFrame generation =
        if transitionFrom.Count > 0 then
            let duration = colorTransitionDuration ()
            if duration.Ticks <= 0L then
                // 运行中被降级：立即到位并收尾，避免除零。
                for brush, pick in brushes do
                    brush.Color <- pick palette
                transitionFrom.Clear()
                if generation = frameGeneration then stopFrames ()
            else
                let elapsed = (DateTime.UtcNow - transitionStarted).TotalMilliseconds
                let t = min 1.0 (elapsed / transitionSegmentMs)
                for brush, pick in brushes do
                    brush.Color <- lerpColor transitionFrom[brush] (pick palette) t
                if t >= 1.0 then
                    transitionFrom.Clear()
                    if generation = frameGeneration then stopFrames ()

    let private startFrames () =
        frameGeneration <- frameGeneration + 1
        stopFrames ()
        let generation = frameGeneration
        // 计时器在线程池上走真实时钟；每帧把推进逻辑 post 到 UI 线程执行，
        // 补间状态与画笔只在 dispatcher 上被读改写，不跨线程共享可变状态。
        frameTimer <-
            Some(
                new System.Threading.Timer(
                    (fun _ ->
                        Dispatcher.UIThread.InvokeAsync(fun () -> advanceFrame generation)
                        |> ignore),
                    null,
                    colorTransitionFrame,
                    colorTransitionFrame))

    /// 应用主题。已有画笔实例原地改色，因此不必重建视图树。
    ///
    /// mode/palette 同步更新、`Changed` 即刻触发：非画笔量（阴影、字号）的
    /// 重建语义与触发时机完全不变。变的是画笔：`Color` 不再硬切，而是从当前色
    /// 向新色补间，时长消费 `MotionLedger.themeColorTransition`。
    ///
    /// 补间途中再次 apply：起点重取为 brush 的当前色、计时重新起算，后来的切换
    /// 接管前一段补间。减弱动效或同主题重放则同步到位。
    let apply (next: ThemeMode) : unit =
        let sameMode = (mode = next)
        let duration = colorTransitionDuration ()
        if sameMode || duration.Ticks <= 0L then
            mode <- next
            palette <- Palette.ofMode next
            stopFrames ()
            transitionFrom.Clear()
            for brush, pick in brushes do
                brush.Color <- pick palette
        else
            // 接管前一段未完成的补间：起点取前段的「逻辑当前色」——按前段已经历的
            // 墙上时间把它的 from/target 滚到这一刻应处的位置，而不是读最后一次
            // 落笔的物理画笔色。真实应用里帧一直在跑，两者本就一致；无 Pump 的
            // 无显示测试环境里（两次 apply 之间没有 RunJobs），逻辑进度依然定义
            // 良好，接管因此不丢进度、两段也不会叠加。
            let rolloverT =
                if transitionFrom.Count > 0 then
                    let elapsed = (DateTime.UtcNow - transitionStarted).TotalMilliseconds
                    min 1.0 (elapsed / transitionSegmentMs)
                else
                    0.0
            let previousFrom = transitionFrom
            let previousPalette = palette
            mode <- next
            palette <- Palette.ofMode next
            transitionFrom <- System.Collections.Generic.Dictionary<SolidColorBrush, Color>()
            for brush, pick in brushes do
                let startColor =
                    if previousFrom.Count > 0 then
                        lerpColor previousFrom.[brush] (pick previousPalette) rolloverT
                    else
                        brush.Color
                transitionFrom.Add(brush, startColor)
            transitionStarted <- DateTime.UtcNow
            transitionSegmentMs <- duration.TotalMilliseconds
            startFrames ()
        Changed.Trigger next

    // ---- 阴影（跟随主题，需在重建时重新读取）----
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
