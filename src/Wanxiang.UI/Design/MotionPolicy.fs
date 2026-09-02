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
