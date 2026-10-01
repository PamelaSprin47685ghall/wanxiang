namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Media
open Avalonia.Styling
open Avalonia.Threading

/// 控件交互状态：**Avalonia 原生伪类**的唯一注册处。
///
/// 规范（`.agents/skills/wanxiang-ui/SKILL.md` §4.1）：状态视觉 MUST 由官方
/// 伪类驱动，**MUST NOT** 手写 `GotFocus` / `PointerEntered` / `PointerPressed`
/// 去改颜色或阴影。理由是机制性的，不是"更优雅"：
///
/// 1. **伪类只能由控件自己维护**——`Classes.Set(":pointerover", true)` 抛
///    `may only be added by the control itself`。这条框架守卫从根上排除了
///    「漏了某个状态分支」的写法。
/// 2. **覆盖手写路径会漏的场景**：`:focus-within`（容器内任一子控件聚焦）、
///    `:disabled` 由 `IsEnabled` 驱动、`:focus-visible` 自动区分键盘与指针。
/// 3. **手写副本散在 8 处焦点环 + 20 余处 hover**，改一处漏七处
///    ——这是本模块存在的直接原因。
///
/// ## 实测得到的官方机制边界（Avalonia 12.1.2 + Skia）
///
/// | 聚焦方式 | `:focus` | `:focus-visible` |
/// |---|---|---|
/// | `NavigationMethod.Tab`（Tab 键） | ✓ | ✓ |
/// | `NavigationMethod.Directional`（方向键） | ✓ | ✓ |
/// | `NavigationMethod.Unspecified`（对话框初始焦点、程序化 `Focus()`） | ✓ | ✗ |
/// | `NavigationMethod.Pointer`（鼠标点击） | ✓ | ✗ |
///
/// 结论：`:focus-visible` 精确等于「键盘导航来的焦点」。程序化初始焦点需要
/// 额外照顾，但**官方伪类无法区分程序化与指针**——两者都只有 `:focus`。
/// 对此用一个普通类名（不是伪类，故框架允许我们增删）记录聚焦来源：
/// `reportFocusOrigin` 只**报告事实**（这次聚焦怎么来的），视觉仍由样式表查表。
///
/// ## 只注册一次
///
/// 生产 `App.Initialize` 与无头 `TestApp.Initialize` 各调一次，
/// 全应用 `Styles` 里就有了唯一的状态来源。
module Interaction =

    // ---------- 类名（状态的声明侧）----------

    /// 控件状态集合：全应用可交互表面都挂这个类名。
    ///
    /// 用类名（而非 `OfType<Border>`）是因为自绘的 `ActionBorder` /
    /// `ToggleBorder` / 各种 `Border` 行容器类型不同，按类型选会漏。
    /// **前提**：这些自定义类型 MUST 声明
    /// `override _.StyleKeyOverride = typeof<Border>`——否则 `OfType<Border>()`
    /// 选不中它们，所有状态样式静默失配（症状：类名与伪类都在，视觉不变）。
    let private surfaceClass = "surface"

    /// 焦点环变体：输入壳（它自己已有一层实色描边，双重实色会压过内容）。
    let private focusSoftVariant = "focus-soft"

    /// 「聚焦来源是程序化 / 初始」的类名。
    ///
    /// `:focus-visible` 只覆盖键盘（见上表），而对话框首按钮必须有焦点环——
    /// 焦点明明在按钮上（Enter 能激活）却看不见，键盘用户无从判断。
    /// 指针点击则不该留环（已有按压反馈，画环是视觉噪声）。
    /// 官方伪类区分不了这两者，因此用普通类名记录来源。
    let private programmaticFocusClass = "focus-by-program"

    /// 校验态类名。由附加属性同步（见 `setInvalid` / `syncInvalid`）。
    let private invalidClass = "invalid"

    /// hover 变体：不同表面的 hover 底色不同，但「hover 时只改底色、
    /// 不改尺寸与边框粗细」这条规则对所有表面一致。
    ///
    /// 变体是**样式表的一行**，不是一个事件处理器——调用方只报自己属于哪一档，
    /// 具体 hover 成什么色由这里查表。表外新增一档 MUST 在此登记。
    module HoverVariant =

        /// 透明底面上的中性 hover（图标按钮、Ghost 按钮）
        let plain = "hover-plain"

        /// 实心强调面上的 hover（Primary 按钮：accent → accentHover）
        let accent = "hover-accent"

        /// 抬起面（surface）上的 hover：半透明 overlay 叠在 surface 上
        let raised = "hover-raised"

        /// 危险面（dangerSoft）上的 hover
        let danger = "hover-danger"

        /// 描边即状态的表面：hover 时换描边（Menu.selectButton 等）
        let bordered = "hover-bordered"

    // ---------- 校验态（业务事实，不是框架状态）----------

    /// 「该表面处于校验错误」的**附加属性**。
    ///
    /// 为什么是附加属性而不是直接写 `BorderBrush`：一旦控件挂了
    /// `surface` 类，样式系统就接管了 `BorderBrush`——代码侧写进去的值会在
    /// 样式重算时被覆盖（实测：`setFieldError` 写完 danger，下一次重算又回到
    /// border，字段红框一闪就没）。校验态**无法用伪类表达**（它是业务事实，
    /// 不是框架状态），因此用附加属性声明事实、由样式查表决定视觉。
    let private invalidProperty =
        AvaloniaProperty.RegisterAttached<Border, bool>("IsInvalid", typeof<Border>, false)

    /// 读取校验态。
    let isInvalid (control: Border) = control.GetValue invalidProperty

    /// 把校验态从附加属性同步到样式类名（可观测标记，便于测试与调试定位）。
    /// 视觉由 `applyInvalidStroke` 负责——见上方「描边色不进样式表」的实测依据。
    let syncInvalid (control: Border) =
        if isInvalid control then control.Classes.Add invalidClass |> ignore
        else control.Classes.Remove invalidClass |> ignore

    /// 设置校验态并应用描边。**MUST** 用它而不是直接写 `BorderBrush`。
    let setInvalid (control: Border) (invalid: bool) =
        control.SetValue(invalidProperty, invalid)
        syncInvalid control
        control.BorderBrush <- (if invalid then Tokens.danger else Tokens.border)

    // ---------- 颜色工具 ----------

    /// 半透明 overlay 笔按 alpha 叠到 base 笔上（Avalonia 无原生画笔叠加）。
    ///
    /// 放在这里是因为「hover 底色怎么算」是状态体系的一部分：调用方只报
    /// 自己属于哪个 hover 档，不自己算颜色。
    let blendOver (baseBrush: IBrush) (overlayBrush: IBrush) : IBrush =
        match baseBrush, overlayBrush with
        | (:? SolidColorBrush as b), (:? SolidColorBrush as o) ->
            let blend (bc: byte) (oc: byte) (alpha: byte) =
                byte (int bc + (int oc - int bc) * int alpha / 255)
            SolidColorBrush(
                Color.FromArgb(
                    b.Color.A,
                    blend b.Color.R o.Color.R o.Color.A,
                    blend b.Color.G o.Color.G o.Color.A,
                    blend b.Color.B o.Color.B o.Color.A))
            :> IBrush
        | _ -> baseBrush

    /// 焦点环：外扩 `Stroke.thick` 实色环。用**外扩阴影**而不是边框——
    /// 加边框会让按钮内容跳一下，而焦点本该是「无位移的提示」。
    let private ringSpread = Spacing.Stroke.thick

    // ---------- 过渡 ----------

    /// 只补间底色。
    let backgroundOnlyTransitions () : Avalonia.Animation.Transitions =
        let background = Avalonia.Animation.BrushTransition()
        background.Property <- Border.BackgroundProperty
        background.Duration <- MotionLedger.controlStateDuration ()
        background.Easing <- MotionPolicy.easeOutCubic
        let transitions = Avalonia.Animation.Transitions()
        transitions.Add background
        transitions

    /// 状态过渡：**只补间底色**，不含描边 / transform / 尺寸 / 边宽。
    ///
    /// 几何动画全面禁止（`MotionLedger.geometryAnimationAllowed = false`）：
    /// 状态反馈 MUST NOT 让控件位移或改变尺寸，否则布局会跳。
    ///
    /// 描边色不进补间——实测依据见 `surface` 里的注释（补间会改写 `Tokens` 的
    /// 可变笔实例，让它与全局令牌脱钩）。
    let stateTransitions () : Avalonia.Animation.Transitions =
        backgroundOnlyTransitions ()

    // ---------- 样式表 ----------

    /// 键盘焦点环（`:focus-visible` = Tab / 方向键，实测精确等于键盘导航）。
    let private keyboardFocusRingStyle () : IStyle =
        let style =
            Style(fun selector ->
                selector.OfType<Border>().Class(surfaceClass).Class(":focus-visible"))
        style.Setters.Add(
            Setter(
                Border.BoxShadowProperty,
                BoxShadows(BoxShadow(Spread = ringSpread, Color = Tokens.accent.Color))))
        style :> IStyle

    /// 程序化 / 初始焦点环（`:focus` + 来源类名）。
    ///
    /// 只对「非指针来源的 `:focus`」生效，因此鼠标点过的按钮不留环，
    /// 而对话框首按钮（裸 `Focus()` → Unspecified）有环。
    let private programmaticFocusRingStyle () : IStyle =
        let style =
            Style(fun selector ->
                selector
                    .OfType<Border>()
                    .Class(surfaceClass)
                    .Class(programmaticFocusClass)
                    .Class(":focus"))
        style.Setters.Add(
            Setter(
                Border.BoxShadowProperty,
                BoxShadows(BoxShadow(Spread = ringSpread, Color = Tokens.accent.Color))))
        style :> IStyle

    /// 输入壳焦点环（`:focus-within` + accentSoft）。
    ///
    /// 颜色比其它表面浅一档：输入壳已有一层实色描边（`focusedStrokeStyle`），
    /// 外扩环若再用 accent 就是双重实色，重量压过输入内容本身。
    let private inputFocusRingStyle () : IStyle =
        let style =
            Style(fun selector ->
                selector
                    .OfType<Border>()
                    .Class(surfaceClass)
                    .Class(focusSoftVariant)
                    .Class(":focus-within"))
        style.Setters.Add(
            Setter(
                Border.BoxShadowProperty,
                BoxShadows(BoxShadow(Spread = ringSpread, Color = Tokens.accentSoft.Color))))
        style :> IStyle

    // 描边色（聚焦 / 校验）**不进样式表**。
    //
    // 实测（Avalonia 12.1.2）：`Setter` 里的可变 `SolidColorBrush` 会被冻结，
    // 且该 Setter 在样式重算时以注册时的笔引用为准——`.invalid` 类加上去也不生效。
    // 而描边色恰好是「每次都可能换一个 Tokens 笔」的值（Tokens 的笔是可变实例，
    // 切主题时原地改色），本来就不适合塞进 Setter。
    //
    // 焦点环 / hover / 按压 / 禁用仍然走样式：它们设的是 BoxShadow / Background /
    // Opacity，这些**不是** Tokens 的可变画笔实例，样式重算正常。



    /// hover 变体：底色提一档，**不改尺寸、不改边框粗细**。
    let private hoverStyle (variant: string) (overBrush: unit -> IBrush) : IStyle =
        let style =
            Style(fun selector ->
                selector
                    .OfType<Border>()
                    .Class(surfaceClass)
                    .Class(variant)
                    .Class(":pointerover"))
        style.Setters.Add(Setter(Border.BackgroundProperty, overBrush ()))
        style :> IStyle

    /// 「描边即状态」的表面：hover 换描边而非底色（Menu.selectButton 等）。
    let private borderedSurfaceStyle () : IStyle =
        let style =
            Style(fun selector ->
                selector
                    .OfType<Border>()
                    .Class(surfaceClass)
                    .Class(HoverVariant.bordered)
                    .Class(":pointerover"))
        style.Setters.Add(Setter(Border.BorderBrushProperty, Tokens.line))
        style :> IStyle

    /// 按压：瞬时不透明度脉冲。
    ///
    /// 瞬时而非补间：`setReservedActionVisible` / `setEnabled` 对 `Opacity` 的写值
    /// 被 `UiStabilityTests` 精确断言，而本项目的无显示测试制度下 Avalonia 原生
    /// 过渡不推进。因此 `Opacity` MUST NOT 进入任何基控件的过渡集合。
    let private pressedStyle () : IStyle =
        let style =
            Style(fun selector ->
                selector.OfType<Border>().Class(surfaceClass).Class(":pressed"))
        style.Setters.Add(Setter(Border.OpacityProperty, Tokens.opacityPressed))
        style :> IStyle

    /// 禁用：统一不透明度并关命中。保留足够对比度——过低会让实心按钮上的图标
    /// 彻底消失，用户分不清「不可用」和「渲染坏了」。
    let private disabledStyle () : IStyle =
        let style =
            Style(fun selector ->
                selector.OfType<Border>().Class(surfaceClass).Class(":disabled"))
        style.Setters.Add(Setter(Border.OpacityProperty, Tokens.opacityDisabled))
        style.Setters.Add(Setter(Border.IsHitTestVisibleProperty, false))
        style :> IStyle

    // ---------- 注册与使用 ----------

    /// 把状态样式注册进应用级 `Styles`。幂等。
    ///
    /// MUST 在 `Application.Initialize` 里调，且**在视图树构建之前**——
    /// 样式晚于控件挂载会导致首帧无反馈。
    ///
    /// ## 为什么注册体不能跨线程
    ///
    /// `Styles` 与 `Tokens` 的画笔都是 Avalonia 对象，读写要过
    /// `Dispatcher.VerifyAccess`。实测两个坑：
    ///
    /// - post 到 UI 线程 + 同步等待落地：与 `HeadlessUnitTestSession`
    ///   （PerAssembly）的初始化窗口打架，整套测试偶发崩溃
    ///   （981 个用例里 372 个连带失败、耗时从 44s 缩到 8s；基线连跑 6 次
    ///   稳定 3-4 失败、31-38s）。
    /// - 从非 UI 线程直接注册：读 `Tokens.surface.Color` 时抛
    ///   `VerifyAccess`。
    ///
    /// 因此这里**不做线程调度**，只在调用方已经位于 Avalonia 允许写入的时刻
    /// （`Application.Initialize`）原样执行。真正的保护是幂等判据——它让
    /// 重复调用无害，于是「多调几次」总比「猜时机」可靠。

    /// 幂等判据：本模块注册的样式里是否已有标记条目。
    ///
    /// 三种候选的实测取舍：
    /// - 模块级 `mutable registered`：PerAssembly session 可能重建 `Application`
    ///   （于是传入新 `Styles` 实例），而 F# 模块状态不重置 → 第二次跳过 →
    ///   样式表空 → 类名在、视觉静默失配。
    /// - `styles.Count < n`：并发下会重复注册同一批样式。
    /// - 扫条目里的标记 setter：判据与被管对象在同一个集合里，无跨 session
    ///   存活的模块状态，重复调用天然无害。
    let private markerSetterValue = "wanxiang-interaction"

    let private alreadyRegistered (styles: Styles) =
        styles
        |> Seq.exists (fun (style: IStyle) ->
            match style with
            | :? Style as st ->
                st.Setters
                |> Seq.exists (fun (setter: SetterBase) ->
                    match setter with
                    | :? Setter as sv ->
                        match sv.Value with
                        | :? string as text -> text = markerSetterValue
                        | _ -> false
                    | _ -> false)
            | _ -> false)

    /// 实际注册。幂等由 `alreadyRegistered` 保证。
    let private addStyles (styles: Styles) =
        if alreadyRegistered styles then
            ()
        else
            let marker =
                let style = Style(fun selector -> selector.OfType<Border>().Class(surfaceClass))
                style.Setters.Add(Setter(Border.TagProperty, markerSetterValue))
                style :> IStyle
            let hoverStyles =
                [ hoverStyle HoverVariant.plain (fun () -> Tokens.hover :> IBrush)
                  hoverStyle HoverVariant.accent (fun () -> Tokens.accentHover :> IBrush)
                  hoverStyle HoverVariant.raised (fun () -> blendOver Tokens.surface Tokens.hover)
                  hoverStyle HoverVariant.danger (fun () -> blendOver Tokens.dangerSoft Tokens.hover) ]
            // 注册顺序 = 优先级：**后注册者覆盖先注册者**（实测 Avalonia 12）。
            // 高优先级 MUST 排在后面：焦点 > hover > 按压/禁用。
            for style in
                [ yield pressedStyle ()
                  yield disabledStyle ()
                  yield! hoverStyles
                  yield borderedSurfaceStyle ()
                  yield inputFocusRingStyle ()
                  yield keyboardFocusRingStyle ()
                  yield programmaticFocusRingStyle ()
                  yield marker ] do
                styles.Add style

    let register (styles: Styles) = addStyles styles

    /// 惰性注册：**每次真正需要状态样式时才确保已注册**。
    ///
    /// 为什么不在 `Application.Initialize` 里注册（那是生产做法）：
    /// 无显示测试的 `Application.Initialize` 与 Avalonia session 初始化存在
    /// 交错窗口，在那里写 `Styles` 会偶发毒化 session（实测 981 个用例里
    /// 372 个连带失败、耗时从 44s 缩到 8s；基线连跑 6 次稳定 3-4 失败）。
    /// `alreadyRegistered` 的幂等判据让「多调几次」无害，因此改为在
    /// **第一个控件挂 surface 时**确保注册——那一定已经过了 session 初始化。
    let private ensureRegistered () =
        match Avalonia.Application.Current with
        | null -> ()
        | app -> addStyles app.Styles

    /// 标记一个表面为「参与统一状态反馈」，并声明它的 hover 档。
    ///
    /// 调用方只管建控件与配色，状态由样式系统接管：
    ///     let button = Ui.button Ui.ButtonTone.Primary "保存" save
    ///     // ↑ 按钮工厂内部已调 surface host (HoverVariant.accent)，此处无需再写
    ///
    /// `variant` MUST 取自 `HoverVariant`；传空串表示该表面无 hover 底色变化。
    /// 描边档过渡：底色 + 描边双补间。
    ///
    /// **仅供"描边色永不单独改写"的表面**用（`Tokens.line` / `Tokens.border` 这类
    /// 主题色在 hover/失焦之间切一次，补间到终点即停）。
    ///
    /// **MUST NOT** 用于承载**离散状态**的描边（校验红 `Tokens.danger`、
    /// 聚焦实色 `Tokens.accent`）——实测 `BrushTransition` 作用于
    /// `BorderBrushProperty` 时会**改写 `Tokens` 的可变画笔实例**：校验红框读到的是
    /// 补间中间色（#ffd2cabc）而非终点色（#ff8e3b2f），且切主题后该笔再也不变红
    /// （`Tokens` 的笔是「原地改色」的活对象，被过渡写一次就与全局令牌脱钩）。
    /// 离散状态描边因此走 `setFocusedStroke` / `setInvalid` 的**值**路径，不进补间。
    let borderedTransitions () : Avalonia.Animation.Transitions =
        let transitions = backgroundOnlyTransitions ()
        let border = Avalonia.Animation.BrushTransition()
        border.Property <- Border.BorderBrushProperty
        border.Duration <- MotionLedger.controlStateDuration ()
        border.Easing <- MotionPolicy.easeOutCubic
        transitions.Add border
        transitions

    let surface (control: Control) (variant: string) =
        ensureRegistered ()
        control.Classes.Add surfaceClass
        if not (String.IsNullOrEmpty variant) then control.Classes.Add variant
        // 描边档补双过渡（描边只切主题色），其余只补底色——理由见 borderedTransitions。
        control.Transitions <-
            (if variant = HoverVariant.bordered then borderedTransitions () else backgroundOnlyTransitions ())

    /// 挂到一个可聚焦控件上：报告聚焦来源（键盘 / 程序化 / 指针）。
    ///
    /// 这是状态体系的一部分，**不是**调用点各自订阅 `GotFocus` 的副本。
    /// 事件只报告事实——「这次聚焦怎么来的」，与 `NavigationMethod` 的语义一致；
    /// 视觉仍由样式表查表决定。
    /// 输入壳聚焦时的实色描边。
    ///
    /// 与校验描边同理：笔引用会变（Tokens 可变画笔），因此由值承担而非样式。
    /// 失焦时回到普通 `border`——由 `Ui.textField` 的失焦订阅负责。
    let setFocusedStroke (control: Border) (focused: bool) (invalid: bool) =
        control.BorderBrush <-
            (if invalid then Tokens.danger elif focused then Tokens.accent else Tokens.border)

    let reportFocusOrigin (control: Control) =
        control.GotFocus.Add(fun e ->
            match e.NavigationMethod with
            | NavigationMethod.Pointer -> control.Classes.Remove programmaticFocusClass |> ignore
            | _ -> control.Classes.Add programmaticFocusClass |> ignore)
        control.LostFocus.Add(fun _ -> control.Classes.Remove programmaticFocusClass |> ignore)