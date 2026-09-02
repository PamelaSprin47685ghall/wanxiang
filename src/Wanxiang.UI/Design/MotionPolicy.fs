namespace Wanxiang.UI

open System

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

/// Motion Ledger：精品阶段明确“什么允许动、什么只切状态”。
/// - busy spinner：唯一持续运动；
/// - copy confirmation：不是动画，只是稳定保持状态后复原；
/// - popup/dialog/sidebar/message geometry：禁止 transform 动画，保持空间 contract。
module MotionLedger =

    let busySpinnerFrame = TimeSpan.FromMilliseconds 40.0
    let copyConfirmationHold = TimeSpan.FromMilliseconds 1500.0
    let geometryAnimationAllowed = false
