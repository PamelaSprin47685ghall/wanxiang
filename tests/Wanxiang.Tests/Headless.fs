namespace Wanxiang.Tests

open System
open System.Threading
open Avalonia
open Avalonia.Headless
open Avalonia.Themes.Fluent
open Wanxiang.UI

/// 无显示环境下的 Avalonia 平台 + UI 线程编组。
///
/// ## 为什么不用 `Avalonia.Headless.XUnit`
///
/// 那个包在 .NET 10 上会**死锁**：`AvaloniaTestCase.Run` 永久阻塞，
/// 即使 `maxParallelThreads=1` 也一样（上游 #21467），且首个 `Dispatch` 可能抛
/// 跨线程 `VerifyAccess` 并毒化整个进程（上游 #22021）。所以这里直接用
/// Avalonia 官方的底层公开 API `HeadlessUnitTestSession`，绕开那层封装。
///
/// ## 并行是安全的
///
/// `run` 把测试体编组到 Avalonia **唯一的 dispatcher 线程**上执行；测试体本身
/// 跑在 xunit 的工作线程上，因此：
/// - 碰 Avalonia 控件/UI 类型的测试 **MUST** 用 `Headless.run (fun () -> ...)` 包住；
/// - 纯逻辑测试保持普通 `[<Fact>]`，由 xunit **真并行**调度；
/// - `PerAssembly` 复用同一个 Application/Dispatcher（进程内单实例），
///   避免每个 UI 测试都重建 Skia/字体。
///
/// 因此**不需要**为了线程安全而关掉并行。
type TestApp() =
    inherit Application()

    /// 没有主题就没有模板，ScrollViewer 之类的带模板控件量不出 Extent/Viewport。
    override this.Initialize() =
        this.Styles.Add(FluentTheme())

/// 测试用 Avalonia 装配：Skia 真实字形度量 + headless 平台。
///
/// 公式排版靠真实字形宽度定位，测试必须能量文本，所以要关掉 headless 默认的
/// 假绘制后端、换成 Skia——否则量出来全是 0。
type TestAppBuilder =
    static member BuildAvaloniaApp() =
        AppBuilder
            .Configure<TestApp>()
            .UseSkia()
            .UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false))

module Headless =

    let private session =
        lazy
            (HeadlessUnitTestSession.StartNew(
                typeof<TestAppBuilder>,
                AvaloniaTestIsolationLevel.PerAssembly))

    /// 在 Avalonia 的 UI 线程上同步执行测试体。
    ///
    /// 用法（**无需重新缩进测试体**，保持 diff 最小）：
    /// ```fsharp
    /// [<Fact>]
    /// let ``some ui test`` () =
    ///     Headless.run (fun () ->
    ///     // 原测试体整体多一层括号，缩进原样保留
    ///     let view = MainView()
    ///     view.Build())
    /// ```
    let run (body: unit -> unit) : unit =
        session.Value.Dispatch(
            Action(fun () ->
                try
                    body ()
                finally
                    // 全局可变状态兑底：测试体里改过的减弱动效偏好一律归零，
                    // 避免泄漏给后续在 UI 线程上串行执行的其它测试。
                    // （Tokens/MotionPolicy/RichBackend 都是模块级可变全局，
                    // 并行化后这类泄漏从「偶发」变成「必现交叉污染」。）
                    Wanxiang.UI.MotionPolicy.setReduced false
                    Wanxiang.UI.Tokens.apply (Wanxiang.UI.Tokens.current ())),
            CancellationToken.None
        ).GetAwaiter().GetResult()
