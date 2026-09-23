namespace Wanxiang.UI

open System
open Avalonia.Animation

/// Avalonia 画布内统一的动态效果策略。
/// PWA 壳层 CSS 已响应 prefers-reduced-motion；这里负责应用自身控件。
module MotionPolicy =

    let mutable private reduced = false
    let Changed = Event<bool>()

    let isReduced () = reduced

    let setReduced value =
        if reduced <> value then
            reduced <- value
            Changed.Trigger value

    let duration milliseconds =
        if reduced then TimeSpan.Zero else TimeSpan.FromMilliseconds(float milliseconds)

    /// 统一缓动曲线：easeOutCubic（cubic-bezier 0.215, 0.61, 0.355, 1）。
    /// 经 MotionPolicy 门控的 opacity / BrushTransition 状态补间共用这一条曲线，
    /// 让全应用状态过渡的起始/收尾手感一致（起步快、收尾稳），对应参照的交互态配方。
    /// Avalonia 未内置 cubic-bezier 四参构造，按 Easings.Easing 基类落一条同名曲线。
    type EaseOutCubicEasing() =
        inherit Avalonia.Animation.Easings.Easing()
        override _.Ease(t: float) =
            let p1 = 0.215
            let p2 = 0.61
            // 解出该三次贝塞尔在给定 t 处的显式近似：二分法求参数 u 使 x(u)=t，
            // 再代入 y(u)。纯逻辑，无外部副作用。
            let rec solveX lo hi =
                let mid = (lo + hi) / 2.0
                let x = 3.0 * p1 * mid * (1.0 - mid) * (1.0 - mid) + 3.0 * p2 * mid * mid * (1.0 - mid) + mid * mid * mid
                if hi - lo < 1e-9 then mid
                elif x < t then solveX mid hi
                else solveX lo mid
            let u = solveX 0.0 1.0
            3.0 * (1.0 - p1) * u * (1.0 - u) * (1.0 - u) + 3.0 * p2 * u * u * (1.0 - u) + u * u * u

    let easeOutCubic = EaseOutCubicEasing()

/// Motion Ledger：精品阶段明确“什么允许动、什么只切状态”。
/// - busy spinner：唯一持续运动；
/// - copy confirmation：不是动画，只是稳定保持状态后复原；
/// - popup/dialog/sidebar/message geometry：禁止 transform 动画，保持空间 contract；
/// - 其余业务 DispatcherTimer 时长一律在此以语义命名收敛，禁止散落裸毫秒；
///   数值相同而语义不同必须分开命名（如 outbox 与导出的过期检查同为 1000ms 也是两枚）。
module MotionLedger =

    let busySpinnerFrame = TimeSpan.FromMilliseconds 40.0
    let copyConfirmationHold = TimeSpan.FromMilliseconds 1500.0
    let geometryAnimationAllowed = false

    /// 搜索输入防抖：停止输入 160ms 后才触发过滤，避免逐键重建列表。
    let searchInputDebounce = TimeSpan.FromMilliseconds 160.0
    /// 骨架屏呼吸帧：50ms 一级，在 0.55~0.95 之间平滑起伏。
    let skeletonBreathFrame = TimeSpan.FromMilliseconds 50.0
    /// 流式光标呼吸帧：50ms 一级，驱动 opacity 在呼吸区间内起伏。
    /// 与 skeletonBreathFrame 数值相同但语义不同（光标不是骨架加载态），分记。
    let caretBreathFrame = TimeSpan.FromMilliseconds 50.0
    /// 会话切换骨架延迟：会话快照 300ms 内到达就不挂任何 loading chrome。
    let conversationSkeletonDelay = TimeSpan.FromMilliseconds 300.0
    /// 平滑滚动帧节奏：约 60fps 的 16ms tick。
    let smoothScrollFrame = TimeSpan.FromMilliseconds 16.0
    /// 平滑滚动总时长：16ms 帧驱动下的 180ms ease-out cubic 动画。
    let smoothScrollDuration = TimeSpan.FromMilliseconds 180.0
    /// 浏览器宿主 CSS 视口轮询周期：Browser 后端高 DPI 下取真实 CSS 像素宽。
    let hostViewportPoll = TimeSpan.FromMilliseconds 500.0
    /// toast 自动消失倒计时 tick：按剩余毫秒递减的 200ms 步进。
    let toastDismissTick = TimeSpan.FromMilliseconds 200.0

    /// toast 停留时长（自动消失倒计时前的停留）：OverlayHost 按语气分三档，
    /// 失败留最久让人读完、warning 次之、常规最短；三档语义名收拢在此，
    /// 递减步进仍是上面的 toastDismissTick。
    let toastDwellFailure = TimeSpan.FromMilliseconds 9000.0
    let toastDwellWarning = TimeSpan.FromMilliseconds 6500.0
    let toastDwellDefault = TimeSpan.FromMilliseconds 4000.0
    /// outbox 投递过期检查 tick：配合投递超时逐格清扫未 ACK 投递。
    let outboxDeliveryCheckTick = TimeSpan.FromMilliseconds 1000.0
    /// 导出过期检查 tick：配合导出有效期逐格清扫；与 outbox 同为 1000ms 但语义不同，分记。
    let exportExpiryCheckTick = TimeSpan.FromMilliseconds 1000.0
    /// 进度文字节流时长（预留）：传输事件可远快于人眼阅读，100ms 约每秒 10 次，既顺滑又可读。
    let progressTextThrottle = TimeSpan.FromMilliseconds 100.0
    /// 主题颜色过渡时长（预留）：200ms 约 60fps 下 12 帧，落在常见 150~300ms 状态色过渡区间，可感知而不拖沓。
    let themeColorTransition = TimeSpan.FromMilliseconds 200.0

    /// 控件状态反馈补间时长：hover/press/focus/selected 的底色过渡。
    /// 由 120ms 提到 200ms，与 themeColorTransition 同档形成统一的状态过渡区间：
    /// 短了瞬切失去缓冲，长了按钮发黏。只过渡颜色，不引入任何几何/位移/尺寸动画。
    let controlStateTransition = TimeSpan.FromMilliseconds 200.0

    /// 控件状态补间的实际时长：尊重 MotionPolicy 的减弱动效偏好，降级为零（瞬时切换）。
    let controlStateDuration () =
        MotionPolicy.duration controlStateTransition.TotalMilliseconds

    /// 流式光标呼吸单程时长（下限→上限→下限约 1.8s 一周期）。
    /// 呼吸只改 opacity，不改尺寸，几何契约不变。
    let caretBreathPhase = TimeSpan.FromMilliseconds 900.0

    /// 复制确认反馈的淡入淡出时长：copy → ✓ → 复原之间的过渡只改 opacity。
    let copyConfirmationFade = TimeSpan.FromMilliseconds 150.0

    /// scrim（compact 抽屉遮罩与对话框遮罩共用一层）的不透明度淡入淡出时长：
    /// 只改 opacity、不碰任何几何/位移动画；150ms 与复制确认同一手感区间。
    /// 生效时长由消费点经 MotionPolicy 降级（减弱动效归零，瞬时切换）。
    let scrimFade = TimeSpan.FromMilliseconds 150.0

    /// 披露箭头（chevron）旋转过渡：150ms，只改 RotateTransform.Angle，
    /// 不碰布局几何（MessageCard 三处披露箭头）。
    /// 与 controlRowFade 同为 150ms 但语义不同（transform vs opacity），分记。
    let disclosureChevronRotate = TimeSpan.FromMilliseconds 150.0

    /// 控件行/条级反馈淡入淡出：150ms，只补间 Opacity
    ///（MessageCard 操作条、Composer 附件 chip），与复制确认同一手感区间。
    let controlRowFade = TimeSpan.FromMilliseconds 150.0

    /// toast 进出场淡入淡出时长：约 300ms（200~300ms 状态过渡区间的舒缓档），
    /// 只补间 Opacity，不做任何位移/尺寸动画；经 MotionPolicy 门控，减弱动效归零即时切换。
    let toastFade = TimeSpan.FromMilliseconds 300.0
