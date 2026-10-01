namespace Wanxiang.Tests

open System
open System.Threading
open Avalonia
open Avalonia.Headless
open Avalonia.Markup.Xaml
open Avalonia.Styling
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
/// ## 并行纪律
///
/// `run` 把测试体编组到 Avalonia **唯一的 dispatcher 线程**上执行；测试体本身
/// 跑在 xunit 的工作线程上，因此：
/// - 碰 Avalonia 控件 / UI 类型的测试 **MUST** 用 `Headless.run (fun () -> ...)` 包住；
/// - 纯逻辑测试保持普通 `[<Fact>]`，由 xunit **真并行**调度；
/// - `PerAssembly` 复用同一个 Application/Dispatcher（进程内单实例），
///   避免每个 UI 测试都重建 Skia/字体。
///
/// **但一个进程内只能有一个这样的 session**：`HeadlessUnitTestSession` 在每次
/// dispatch 前校验「当前线程是不是 session owner」，同进程多线程轮流进来会偶发
/// 抛 `VerifyAccess`，一次就毒化整个 session（实测 981 个用例里 372 个连带失败、
/// 耗时从 44s 缩到 8s）。因此：
///
/// | 测试类型 | 并行粒度 | 原因 |
/// |---|---|---|
/// | UI 测试（碰 Avalonia） | **进程级**：由 `run-tests.sh` 拆到独立进程 | headless 平台是进程级单例 |
/// | 非 UI 测试（纯逻辑） | **线程级**：单进程内 xunit 默认并行 | 无共享可变状态，最快 |
///
/// 进程内的 xunit 因此关掉集合并行（`xunit.runner.json`
/// `parallelizeTestCollections: false`）——既然外部已经按进程切开了，
/// 进程内再并行只会重新引入 session 竞态。
type TestApp() =
    inherit Application()

    /// 没有主题就没有模板，ScrollViewer 之类的带模板控件量不出 Extent/Viewport。
    ///
    /// **这里不注册 `Interaction` 状态样式**：实测在 `Application.Initialize`
    /// 里写 `Styles` 会与 headless session 的初始化窗口交错，偶发毒化 session。
    /// 状态样式改为惰性注册——第一个挂 `surface` 类的控件触发 `ensureRegistered`，
    /// 那一定已经过了 session 初始化。生产 `App.fs` 仍走 Initialize（那是单线程）。
    ///
    /// 图标不需要任何包内主题：`Icons` 走 `LucideImageExtension` 的纯 API 路径
    /// （返回 `DrawingImage` 放进原生 `Image`），不依赖 ControlTheme。
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

/// 无显示测试的统一入口。
module Headless =

    /// 隔离级别：**PerTest**——每个 UI 测试方法重建 `Application` 与 `Dispatcher`。
    ///
    /// 为什么不用 `PerAssembly`（项目此前用的）：`HeadlessUnitTestSession.DispatchCore`
    /// 每次 dispatch 都要走一遍应用服务装配，而 xunit 的线程调度会让两个线程同时进入；
    /// 后到者拿到属别线程的 `DefaultRenderLoop`，抛
    /// `The calling thread cannot access this object because a different thread owns it`。
    /// **一次就毒化整个 session**——实测 983 个用例里 373 个连带失败、耗时从 34s 缩到
    /// 14s，看起来像业务代码坏了，实际是测试装置的竞态（基线同样存在，不是 UI 改动引入）。
    ///
    /// PerTest 是官方为此场景提供的正解（`Avalonia.Headless.xml`：*"This mode ensures
    /// complete test isolation"*）。代价是每个 UI 测试重建一次 Application/Dispatcher
    /// （Skia 字体缓存随之重建），**确定性优先于速度**——一个偶发崩溃的测试套件
    /// 无法作为回归防线。
    let private session =
        lazy
            (HeadlessUnitTestSession.StartNew(
                typeof<TestAppBuilder>,
                AvaloniaTestIsolationLevel.PerTest))

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


