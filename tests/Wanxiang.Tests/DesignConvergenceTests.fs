module Wanxiang.Tests.DesignConvergenceTests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Media
open Xunit
open Wanxiang.UI
open Wanxiang.Tests
open Avalonia.Animation

/// 判定控件是否挂着共享的表面状态反馈集合：Transitions 非空，且每条都是补间底色的
/// BrushTransition、时长出自 MotionLedger.controlStateDuration（= Ui.surfaceTransitions 的产物）。
/// 只验结构，不触发动效——本项目无显示制度下原生过渡不推进（见 Tokens.fs 帧调度注释）。
let private hasSurfaceFeedback (control: Control) =
    not (isNull control.Transitions)
    && control.Transitions
       |> Seq.cast<Avalonia.Animation.ITransition>
       |> Seq.forall (fun transition ->
           match transition with
           | :? Avalonia.Animation.BrushTransition as brush ->
               MotionLedger.controlStateDuration () = brush.Duration
               && Object.ReferenceEquals(brush.Property, Border.BackgroundProperty)
           | _ -> false)

/// 共享设计基础设施收敛的锁定测试。
///
/// 这些常量是后续多条改动消费的单一真源：每个断言都把它钉在它替代的旧散落
/// 字面量上（见 Tokens / ControlMetrics / MotionLedger 各条注释），防止收敛后
/// 值漂移；同时守住「有意保留的两档」不被人日后随手压平。
/// 只验纯逻辑与控件属性，不触发动效（geometryAnimationAllowed = false 不变）。
type DesignConvergenceTests() =
    // 本类多处调用 Ui.* 原语（tag / tagWith / switchRow / toggle / emptyState /
    // applyValidationFeedback / textField …）构造控件。Ui 模块顶层建有 Cursor
    // （handCursor，Primitives.fs）：进程若无头平台未注册，触碰 Ui 任意成员都会在
    // 模块静态构造里炸掉（TypeInitializationException）。冷启动时若本类比
    // 首个 ensure 的测试先跑，整类一起红；放构造器里一劳永逸——xUnit 每个用例
    // new 一个实例，ensure 的 lazy 初始化之后近乎零成本。
    do Headless.ensure ()

    [<Fact>]
    member _.CompactRowPaddingFamilyMatchesScatteredLiterals() =
        // 替代散落的 1.0 / 2.0 / 3.0 纵向内边距（tag/chip/pill、行容器、字段留白）。
        Assert.Equal(1.0, Tokens.tightRowPaddingY)
        Assert.Equal(2.0, Tokens.compactRowPaddingY)
        Assert.Equal(3.0, Tokens.fieldRowPaddingY)

    [<Fact>]
    member _.LetterSpacingTracksMatchScatteredLiterals() =
        // 替代散落的 0.3 / 0.4 / 0.6 / 0.8。
        Assert.Equal(0.3, Tokens.letterSpacingLabel)
        Assert.Equal(0.4, Tokens.letterSpacingEmphasis)
        Assert.Equal(0.6, Tokens.letterSpacingSection)
        Assert.Equal(0.8, Tokens.letterSpacingDisplay)

    [<Fact>]
    member _.SkeletonOpacityGroupMatchesScatteredLiterals() =
        // 替代散落的 0.65（骨架条静态）/ 0.55（呼吸下限）/ 0.85（减弱动效静态）。
        Assert.Equal(0.65, Tokens.skeletonOpacityBase)
        Assert.Equal(0.55, Tokens.skeletonOpacityBreathMin)
        Assert.Equal(0.85, Tokens.skeletonOpacityReduced)

    [<Fact>]
    member _.HoverDimIsNamedOutsideStateLadder() =
        // 替代 MessageCard 三处裸 0.9（hover 微暗），有意不并入状态阶梯。
        Assert.Equal(0.9, Tokens.opacityHoverDim)

    [<Fact>]
    member _.AccentEdgeTiersKeepIntentionalSplit() =
        // 2.0 统一档：侧栏选中 / toast / 思考竖线；3.0 引用块有意宽一档。
        Assert.Equal(2.0, ControlMetrics.accentEdgeWidth)
        Assert.Equal(3.0, ControlMetrics.quoteEdgeWidth)
        Assert.Equal(ControlMetrics.accentEdgeWidth, ControlMetrics.sidebarSelectedEdgeWidth)
        Assert.Equal(ControlMetrics.accentEdgeWidth, ControlMetrics.toastAccentWidth)
        Assert.True(ControlMetrics.quoteEdgeWidth > ControlMetrics.accentEdgeWidth)

    [<Fact>]
    member _.RowMinHeightIsSingleSourceForBothRowKinds() =
        // 44.0 同值不同源 → 单一真源；两个旧名保留为别名，值不再分叉。
        Assert.Equal(44.0, ControlMetrics.rowMinHeight)
        Assert.Equal(ControlMetrics.rowMinHeight, ControlMetrics.sidebarRowMinHeight)
        Assert.Equal(ControlMetrics.rowMinHeight, ControlMetrics.settingsRowMinHeight)

    [<Fact>]
    member _.SelectButtonPaddingAndTextAreaHeights() =
        // 替代 Menu.selectButton 的 6.0；textArea 两档保留（长文本特例 > 标准档）。
        Assert.Equal(6.0, ControlMetrics.selectButtonPaddingY)
        Assert.Equal(96.0, ControlMetrics.textAreaMinHeight)
        Assert.Equal(160.0, ControlMetrics.textAreaLongMinHeight)
        Assert.True(ControlMetrics.textAreaLongMinHeight > ControlMetrics.textAreaMinHeight)

    [<Fact>]
    member _.ComponentGeometryConstants() =
        Assert.Equal(14.0, ControlMetrics.spinnerSize)
        Assert.Equal(11.0, ControlMetrics.spinnerCompactSize)
        Assert.Equal(34.0, ControlMetrics.toggleTrackWidth)
        Assert.Equal(20.0, ControlMetrics.toggleTrackHeight)
        Assert.Equal(14.0, ControlMetrics.toggleKnobSize)
        Assert.Equal(3.0, ControlMetrics.toggleKnobInsetX)
        Assert.Equal(7.0, ControlMetrics.caretWidth)
        Assert.Equal(15.0, ControlMetrics.caretHeight)
        Assert.Equal(1.5, ControlMetrics.caretRadius)
        Assert.Equal(136.0, ControlMetrics.emptyStateActionMinWidth)
        Assert.Equal(38.0, ControlMetrics.emptyStateActionHeight)
        Assert.Equal(120.0, ControlMetrics.emptyStateTitleMinWidth)
        Assert.Equal(96.0, ControlMetrics.scrollToBottomMinWidth)
        Assert.Equal(80.0, ControlMetrics.retryButtonMinWidth)
        Assert.Equal(4.0, ControlMetrics.listMarkerDotSize)
        Assert.Equal(2.0, ControlMetrics.listMarkerDotRadius)
        Assert.Equal(14.0, ControlMetrics.taskBoxSize)
        Assert.Equal(1.4, ControlMetrics.taskBoxBorderWidth)
        Assert.Equal(10.0, ControlMetrics.taskCheckGlyphSize)
        // 同为 96 但语义不同（滚动定位 vs 保存占位）：按 MotionLedger 纪律分记，
        // 两个名字必须独立存在，任何一方都不得被合档成一个"通用 96"。
        Assert.Equal(96.0, ControlMetrics.pendingActionMinWidth)
        Assert.Equal(96.0, ControlMetrics.scrollToBottomMinWidth)

    [<Fact>]
    member _.MotionLedgerToastDwellAndFadeNames() =
        // toast 三档对应 OverlayHost 的 9000 / 6500 / 4000；150ms 两枚语义名分记。
        Assert.Equal(TimeSpan.FromMilliseconds 9000.0, MotionLedger.toastDwellFailure)
        Assert.Equal(TimeSpan.FromMilliseconds 6500.0, MotionLedger.toastDwellWarning)
        Assert.Equal(TimeSpan.FromMilliseconds 4000.0, MotionLedger.toastDwellDefault)
        Assert.Equal(TimeSpan.FromMilliseconds 150.0, MotionLedger.disclosureChevronRotate)
        Assert.Equal(TimeSpan.FromMilliseconds 150.0, MotionLedger.controlRowFade)
        // 几何动画纪律不变：本批改动只收敛颜色/不透明度/命名。
        Assert.False(MotionLedger.geometryAnimationAllowed)

    [<Fact>]
    member _.ControlStateTransitionAlignsWithThemeTier() =
        // hover/press/focus/selected 底色补间从 120ms 提到 200ms，与 themeColorTransition 同档；
        // 消费时长仍单源经 controlStateDuration（= controlStateTransition 经 MotionPolicy 降级）。
        Assert.Equal(TimeSpan.FromMilliseconds 200.0, MotionLedger.controlStateTransition)
        Assert.Equal(MotionLedger.controlStateTransition, MotionLedger.themeColorTransition)
        MotionPolicy.setReduced false
        Assert.Equal(MotionLedger.controlStateTransition, MotionLedger.controlStateDuration ())
        MotionPolicy.setReduced true
        Assert.Equal(TimeSpan.Zero, MotionLedger.controlStateDuration ())
        MotionPolicy.setReduced false

    [<Fact>]
    member _.HorizontalInsetTracksCompactBreakpoints() =
        // 自适应水平留白四档单调不降，锚定既有断点；端点逐一钉住取值随 Tokens 留白阶梯走。
        Assert.Equal(Tokens.space4, LayoutPolicy.horizontalInset 0.0)
        Assert.Equal(Tokens.space4, LayoutPolicy.horizontalInset (LayoutPolicy.formSingleColumnBreakpoint - 1.0))
        Assert.Equal(Tokens.space6, LayoutPolicy.horizontalInset LayoutPolicy.formSingleColumnBreakpoint)
        Assert.Equal(Tokens.space6, LayoutPolicy.horizontalInset (LayoutPolicy.compactBreakpoint - 1.0))
        Assert.Equal(Tokens.space8, LayoutPolicy.horizontalInset LayoutPolicy.compactBreakpoint)
        Assert.Equal(Tokens.space8, LayoutPolicy.horizontalInset (LayoutPolicy.wideLayoutBreakpoint - 1.0))
        Assert.Equal(Tokens.space12, LayoutPolicy.horizontalInset LayoutPolicy.wideLayoutBreakpoint)
        // 单调性：留白随窗口变宽不减。
        Assert.True(LayoutPolicy.horizontalInset 1280.0 >= LayoutPolicy.horizontalInset 800.0)
        Assert.True(LayoutPolicy.horizontalInset 800.0 >= LayoutPolicy.horizontalInset 400.0)

    [<Fact>]
    member _.CompactActionTargetIsSingleTouchSource () =
        // 触控整合：compactActionTarget 提为触控下限 44，是全应用 compact/touch 图标
        // 动作 hit surface 的唯一来源（ChatView / Composer / Sidebar 的 compact 档共用）；
        // 地基曾加的重复 token（touchActionTarget / Ui.setIconTouchTarget）已删除。
        Assert.Equal(44.0, LayoutPolicy.compactActionTarget)

    [<Fact>]
    member _.TagDefaultBehaviourUnchanged() =
        // 默认 Neutral 外观与原 Ui.tag 逐字一致（含 1.0 的 tight 档 padding）。
        let neutral = Ui.tag "x"
        let child = neutral.Child :?> TextBlock
        Assert.Equal("x", child.Text)
        Assert.Same(Tokens.textMuted, child.Foreground)
        Assert.Same(Tokens.surfaceRaised, neutral.Background)
        Assert.Same(Tokens.border, neutral.BorderBrush)
        Assert.Equal(Thickness(7.0, 1.0), neutral.Padding)

    [<Fact>]
    member _.TagTonesReuseExistingPensAndKeepGeometry() =
        // 语气档只换既有 warning / danger / dangerSoft 笔，几何不变（同排不错位）。
        let warning = Ui.tagWith Ui.TagTone.Warning "w"
        let danger = Ui.tagWith Ui.TagTone.Danger "d"
        let warningChild = warning.Child :?> TextBlock
        let dangerChild = danger.Child :?> TextBlock
        Assert.Same(Tokens.warning, warningChild.Foreground)
        Assert.Same(Tokens.warning, warning.BorderBrush)
        Assert.Same(Tokens.danger, dangerChild.Foreground)
        Assert.Same(Tokens.danger, danger.BorderBrush)
        Assert.Same(Tokens.dangerSoft, danger.Background)
        Assert.Equal(Thickness(7.0, 1.0), warning.Padding)
        Assert.Equal(Thickness(7.0, 1.0), danger.Padding)

    [<Fact>]
    member _.SwitchRowKeepsAutomationAndRowHeight() =
        // 公共 Ui.switchRow 保留 SettingsGeneral 原行为的三个支点：
        // 行最小高度（单一真源）、CheckBox 自动化语义、ItemStatus 开关状态。
        let row, read, write = Ui.switchRow "标题" "说明" false ignore
        let border = row :?> Border
        Assert.Equal(44.0, border.MinHeight)
        Assert.Equal(
            Avalonia.Automation.Peers.AutomationControlType.CheckBox,
            AutomationProperties.GetControlTypeOverride(border).Value)
        Assert.Equal("关闭", AutomationProperties.GetItemStatus(border))
        write true
        Assert.True(read ())
        Assert.Equal("开启", AutomationProperties.GetItemStatus(border))
        write false
        Assert.False(read ())
        Assert.Equal("关闭", AutomationProperties.GetItemStatus(border))

    [<Fact>]
    member _.ValidationFeedbackMultiErrorSummaryAndToast() =
        // 多错：摘要 live region 文案 + 「有 N 处需要修正」toast，与两处旧实现逐字一致。
        let summary = TextBlock()
        let boxA = TextBox()
        let boxB = TextBox()
        let toasts = ResizeArray<string>()
        Ui.applyValidationFeedback [ boxA, "错A"; boxB, "错B" ] (Some summary) toasts.Add
        Assert.Equal("有 2 处需要修正：错A；错B", summary.Text)
        Assert.True(summary.IsVisible)
        Assert.Single toasts |> ignore
        Assert.Equal("有 2 处需要修正，请查看表单顶部摘要。", toasts.[0])

    [<Fact>]
    member _.ValidationFeedbackSingleErrorToastsFieldMessage() =
        // 单错：toast 字段消息并收起摘要。
        let summary = TextBlock()
        summary.IsVisible <- true
        let box = TextBox()
        let toasts = ResizeArray<string>()
        Ui.applyValidationFeedback [ box, "错一个" ] (Some summary) toasts.Add
        Assert.False(summary.IsVisible)
        Assert.Single toasts |> ignore
        Assert.Equal("错一个", toasts.[0])

    [<Fact>]
    member _.ValidationFeedbackEmptyErrorsAreNoOp() =
        let summary = TextBlock()
        let toasts = ResizeArray<string>()
        Ui.applyValidationFeedback [] (Some summary) toasts.Add
        Assert.False(summary.IsVisible)
        Assert.Empty toasts

    [<Fact>]
    member _.ControlSkinMetricsHaveNamedSources() =
        // 替代散落的 1.0 描边、6.0/6.0 行状态点、5.0 chip 内点。
        Assert.Equal(1.0, ControlMetrics.borderWidth)
        Assert.Equal(6.0, ControlMetrics.statusDotSize)
        Assert.Equal(6.0, ControlMetrics.sidebarRunningDotSize)
        Assert.Equal(5.0, ControlMetrics.chipDotSize)
        Assert.True(ControlMetrics.chipDotSize < ControlMetrics.statusDotSize)

    [<Fact>]
    member _.ChipDensityHasSingleSourceUnderBothNames() =
        // chip 内边距的唯一真源（8 × 2）；compactChipPadding* 只是同一对的兼容别名，
        // 值随真源走（旧纵向 4 已并入 compactRowPaddingY 的紧凑档，chip/tag 同为单行小控件）。
        Assert.Equal(8.0, ControlMetrics.chipPaddingX)
        Assert.Equal(2.0, ControlMetrics.chipPaddingY)
        Assert.Equal(Tokens.compactRowPaddingY, ControlMetrics.chipPaddingY)
        Assert.Equal(ControlMetrics.chipPaddingX, ControlMetrics.compactChipPaddingX)
        Assert.Equal(ControlMetrics.chipPaddingY, ControlMetrics.compactChipPaddingY)
        // chip 横向 8 与 tag 横向 7 是有意两档，不许被合回同一个值。
        Assert.True(ControlMetrics.chipPaddingX > ControlMetrics.tagPaddingX)

    [<Fact>]
    member _.ValuePreservingNamesForScatteredLiterals() =
        // 只把字面量换成名字，值一字不动（下游 lane 继续按这些名字收敛）。
        Assert.Equal(7.0, ControlMetrics.tagPaddingX)
        Assert.Equal(2.0, ControlMetrics.fieldInsetX)
        Assert.Equal(1.8, ControlMetrics.spinnerStrokeWidth)
        Assert.Equal(16.0, ControlMetrics.spinnerCanvasSize)
        Assert.Equal(56.0, ControlMetrics.fontSizeValueMinWidth)
        Assert.Equal(110.0, ControlMetrics.aboutKeyMinWidth)

    [<Fact>]
    member _.TagStillYieldsTheLockedPaddingFromNamedTokens() =
        // 取值改名不改值：Ui.tag 仍是 Padding(7.0, 1.0)，描边仍是 canonical 1.0。
        let neutral = Ui.tag "x"
        Assert.Equal(Thickness(7.0, 1.0), neutral.Padding)
        Assert.Equal(ControlMetrics.tagPaddingX, neutral.Padding.Left)
        Assert.Equal(Tokens.tightRowPaddingY, neutral.Padding.Top)
        Assert.Equal(ControlMetrics.borderWidth, neutral.BorderThickness.Left)
        Assert.Same(Tokens.border, neutral.BorderBrush)

    [<Fact>]
    member _.EmptyStateProminentTitleIsOptInAndDefaultUnchanged() =
        // 默认档与提取前逐字一致（label 档小号、无宽字距）；强调档只改标题字号与字距，
        // 卡片几何两档一致——档位不变成第二个契约。
        let plain = Ui.emptyState (Border()) (Some "还没有会话") "" None None
        let prominent = Ui.emptyStateWith true (Border()) (Some "先连接万象服务器") "" None None
        let plainColumn = (plain :?> Border).Child :?> StackPanel
        let prominentColumn = (prominent :?> Border).Child :?> StackPanel
        let plainTitle = plainColumn.Children.[1] :?> TextBlock
        let prominentTitle = prominentColumn.Children.[1] :?> TextBlock
        Assert.Equal(Tokens.fontBody, plainTitle.FontSize)
        Assert.Equal(0.0, plainTitle.LetterSpacing)
        Assert.Equal(Tokens.fontTitle, prominentTitle.FontSize)
        Assert.Equal(Tokens.letterSpacingEmphasis, prominentTitle.LetterSpacing)
        Assert.Equal((plain :?> Border).Padding, (prominent :?> Border).Padding)
        Assert.Equal((plain :?> Border).CornerRadius, (prominent :?> Border).CornerRadius)

    [<Fact>]
    member _.PrimitiveStatesShareTransitionCollectionAndKeepContracts() =
        // 新加的 hover/pressed 反馈挂在共享的表面过渡集合上（只补间底色、时长出自
        // MotionLedger），自动化语义与几何契约不变。无显示制度下过渡不推进，只锁结构。
        let toggleControl, read, write, _ = Ui.toggle false ignore
        let track = toggleControl :?> Border
        Assert.Equal(ControlMetrics.toggleTrackWidth, track.Width)
        Assert.Equal(ControlMetrics.toggleTrackHeight, track.Height)
        Assert.Equal(1.0, track.Opacity)
        Assert.True(hasSurfaceFeedback track)
        write true
        Assert.True(read ())

        let rowControl, _, _ = Ui.switchRow "标题" "说明" false ignore
        let row = rowControl :?> Border
        Assert.Equal(ControlMetrics.settingsRowMinHeight, row.MinHeight)
        Assert.Equal(1.0, row.Opacity)
        Assert.True(hasSurfaceFeedback row)
        Assert.Equal(
            Avalonia.Automation.Peers.AutomationControlType.CheckBox,
            AutomationProperties.GetControlTypeOverride(row).Value)
        Assert.Equal("关闭", AutomationProperties.GetItemStatus(row))

        let shell, _ = Ui.textField "输入"
        Assert.Same(Tokens.border, shell.BorderBrush)
        Assert.Equal(Thickness(ControlMetrics.borderWidth), shell.BorderThickness)
        Assert.True(hasSurfaceFeedback shell)
