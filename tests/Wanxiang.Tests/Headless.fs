namespace Wanxiang.Tests

open Avalonia
open Avalonia.Headless

/// 无显示环境下的 Avalonia 平台。
///
/// 公式排版靠真实字形宽度定位，测试必须能量文本，所以要
/// 关掉 headless 默认的假绘制后端、换成 Skia——否则量出来全是 0。
///
/// 只做 `SetupWithoutStarting`：字体度量不需要消息循环，
/// 起循环反而会让测试进程挂住。
type HeadlessApp() =
    inherit Application()

    /// 没有主题就没有模板，ScrollViewer 之类的带模板控件量不出 Extent/Viewport。
    override this.Initialize() =
        this.Styles.Add(Avalonia.Themes.Fluent.FluentTheme())

module Headless =

    let private session =
        lazy
            (AppBuilder
                .Configure<HeadlessApp>()
                .UseSkia()
                .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false))
                .SetupWithoutStarting())

    /// 在需要字体／绘制的测试开头调一次。
    let ensure () = session.Force() |> ignore

/// 关掉集合级并行：无屏 Avalonia 的调度器与窗口有线程亲和性，并行跑会让
/// 涉及布局的测试随机失败——单独跑却通过，属于最难查的一类。
/// 全量套件只需两秒，串行的代价可以忽略。
module AssemblyConfig =
    [<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
    do ()
