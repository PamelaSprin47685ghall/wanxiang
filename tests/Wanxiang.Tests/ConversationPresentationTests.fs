module Wanxiang.Tests.ConversationPresentationTests

open System
open System.IO
open Xunit
open Wanxiang.UI

/// 统计 needle 在 hay 中的出现次数（Ordinal 匹配）。
let private occurrenceCount (hay: string) (needle: string) =
    let mutable total = 0
    let mutable idx = hay.IndexOf(needle, StringComparison.Ordinal)
    while idx >= 0 do
        total <- total + 1
        idx <- hay.IndexOf(needle, idx + needle.Length, StringComparison.Ordinal)
    total

/// 以测试源文件所在目录为锚定位仓库源码：编译期确定、运行期稳定。
let private source (relativePath: string) : string =
    File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", relativePath))

let private approx (expectedValue: float) (actual: float) = abs (expectedValue - actual) < 1e-9

// ---------- 单一来源令牌的取值契约 ----------
// 这些断言把「会话呈现面收敛」赖以成立的令牌阶梯钉住：任何一处值漂移都会在此变红，
// 从而阻止调用点又回到魔法数。

[<Fact>]
let edgeWidthTiersKeepAccentAndQuoteDistinct () =
    Assert.True(approx ControlMetrics.accentEdgeWidth 2.0, "accentEdgeWidth 应收敛到 2.0（思考竖线）")
    Assert.True(approx ControlMetrics.quoteEdgeWidth 3.0, "quoteEdgeWidth 应收敛到 3.0（引用块左缘）")
    // 两档有意保留：引用块印刷条比通用强调条宽一档（依据 Metrics.fs 注释）。
    Assert.True(ControlMetrics.quoteEdgeWidth > ControlMetrics.accentEdgeWidth, "quote 档应宽于 accent 档")

[<Fact>]
let rowPaddingLadderKeepsTightBelowCompactBelowField () =
    Assert.True(approx Tokens.tightRowPaddingY 1.0, "tightRowPaddingY 应为 1.0")
    Assert.True(approx Tokens.compactRowPaddingY 2.0, "compactRowPaddingY 应为 2.0")
    Assert.True(approx Tokens.fieldRowPaddingY 3.0, "fieldRowPaddingY 应为 3.0")
    Assert.True(Tokens.tightRowPaddingY < Tokens.compactRowPaddingY, "tight < compact")
    Assert.True(Tokens.compactRowPaddingY < Tokens.fieldRowPaddingY, "compact < field")

[<Fact>]
let skeletonOpacityTiersOrderedForBaseBreathAndReduced () =
    Assert.True(approx Tokens.skeletonOpacityBase 0.65, "skeletonOpacityBase 应为 0.65")
    Assert.True(approx Tokens.skeletonOpacityBreathMin 0.55, "skeletonOpacityBreathMin 应为 0.55")
    Assert.True(approx Tokens.skeletonOpacityReduced 0.85, "skeletonOpacityReduced 应为 0.85")
    Assert.True(Tokens.skeletonOpacityBreathMin < Tokens.skeletonOpacityBase, "呼吸下限应低于静态 base")
    Assert.True(Tokens.skeletonOpacityBase < Tokens.skeletonOpacityReduced, "reduced 静态值应高于 base")

[<Fact>]
let letterSpacingKeepsEmphasisBelowDisplay () =
    Assert.True(approx Tokens.letterSpacingEmphasis 0.4, "letterSpacingEmphasis 应为 0.4")
    Assert.True(approx Tokens.letterSpacingDisplay 0.8, "letterSpacingDisplay 应为 0.8")
    Assert.True(Tokens.letterSpacingEmphasis < Tokens.letterSpacingDisplay, "emphasis < display")

[<Fact>]
let hoverDimStaysSingleDimGrade () =
    Assert.True(approx Tokens.opacityHoverDim 0.9, "opacityHoverDim 应为 0.9")

[<Fact>]
let componentGeometryConstantsMatchPinnedCallSites () =
    Assert.True(approx ControlMetrics.spinnerSize 14.0, "spinnerSize 应为 14")
    Assert.True(approx ControlMetrics.spinnerCompactSize 11.0, "spinnerCompactSize 应为 11")
    Assert.True(ControlMetrics.spinnerCompactSize < ControlMetrics.spinnerSize, "compact spinner 应小于常规")
    Assert.True(approx ControlMetrics.sidebarRunningDotSize 6.0, "sidebarRunningDotSize 应为 6")
    Assert.True(approx ControlMetrics.caretWidth 7.0, "caretWidth 应为 7")
    Assert.True(approx ControlMetrics.caretHeight 15.0, "caretHeight 应为 15")
    Assert.True(approx ControlMetrics.caretRadius 1.5, "caretRadius 应为 1.5")
    Assert.True(approx ControlMetrics.listMarkerDotSize 4.0, "listMarkerDotSize 应为 4")
    Assert.True(approx ControlMetrics.listMarkerDotRadius 2.0, "listMarkerDotRadius 应为 2")
    Assert.True(approx ControlMetrics.taskBoxSize 14.0, "taskBoxSize 应为 14")
    Assert.True(approx ControlMetrics.taskBoxBorderWidth 1.4, "taskBoxBorderWidth 应为 1.4")
    Assert.True(approx ControlMetrics.taskCheckGlyphSize 10.0, "taskCheckGlyphSize 应为 10")
    Assert.True(approx ControlMetrics.emptyStateActionMinWidth 136.0, "emptyStateActionMinWidth 应为 136")
    Assert.True(approx ControlMetrics.emptyStateActionHeight 38.0, "emptyStateActionHeight 应为 38")
    Assert.True(approx ControlMetrics.emptyStateTitleMinWidth 120.0, "emptyStateTitleMinWidth 应为 120")
    Assert.True(approx ControlMetrics.scrollToBottomMinWidth 96.0, "scrollToBottomMinWidth 应为 96")
    Assert.True(approx ControlMetrics.retryButtonMinWidth 80.0, "retryButtonMinWidth 应为 80")

// ---------- 源码守卫：调用点必须引用命名来源，不得回到魔法数 ----------

[<Fact>]
let composerShellPaddingSymmetricAndChipFadeUsesNamedLedger () =
    let text = source "src/Wanxiang.UI/Views/Composer.fs"
    Assert.True(occurrenceCount text "ControlMetrics.composerShellPaddingX" >= 2, "输入壳左右应共用 composerShellPaddingX")
    Assert.True(occurrenceCount text "MotionLedger.controlRowFade" >= 1, "附件 chip 淡入应引 MotionLedger.controlRowFade")
    Assert.DoesNotContain("MotionPolicy.duration 150", text)
    // 次级图标按钮（附件/排队）不再有一次性 hairline 轮廓，与所有 Ui.iconButton 一致。
    Assert.DoesNotContain("ghostAction", text)

[<Fact>]
let sidebarFooterPaddingAndDisclosureAndSkeletonConverged () =
    let text = source "src/Wanxiang.UI/Views/Sidebar.fs"
    Assert.Contains("Thickness(Tokens.space4, 0.0, Tokens.space4, 0.0)", text)
    Assert.True(occurrenceCount text "ControlMetrics.sidebarRunningDotSize" >= 1, "状态点应引 sidebarRunningDotSize")
    // 分组表头改走共享宽字距原语 Ui.sectionLabelWide（letterSpacingDisplay 收进原语）；词标 wordmark 仍直接引 letterSpacingDisplay。
    Assert.True(occurrenceCount text "Ui.sectionLabelWide" >= 1, "分组表头应改走 Ui.sectionLabelWide 共享原语")
    Assert.True(occurrenceCount text "Tokens.letterSpacingDisplay" >= 1, "词标字距应引 letterSpacingDisplay")
    Assert.DoesNotContain("header.Foreground <- Tokens.textMuted", text)
    Assert.Contains("Tokens.skeletonOpacityBase", text)
    Assert.DoesNotContain("Opacity = 0.65", text)
    // 侧栏两处 up/down 披露箭头与消息卡披露箭头同属一类 affordance，统一到 textMuted。
    Assert.True(occurrenceCount text "else Icons.chevronDown) Tokens.textMuted" >= 2, "侧栏披露箭头应统一到 textMuted")
    Assert.DoesNotContain("Icons.chevronDown) Tokens.textFaint", text)

[<Fact>]
let messageCardMotionHoverGeometryAndDisclosureColorConverged () =
    let text = source "src/Wanxiang.UI/Views/MessageCard.fs"
    Assert.DoesNotContain("MotionPolicy.duration 150", text)
    Assert.True(occurrenceCount text "MotionPolicy.duration MotionLedger.disclosureChevronRotate.TotalMilliseconds" >= 3, "三处箭头旋转应引 disclosureChevronRotate")
    Assert.True(occurrenceCount text "MotionPolicy.duration MotionLedger.controlRowFade.TotalMilliseconds" >= 1, "操作条淡入应引 controlRowFade")
    Assert.DoesNotContain("Opacity <- 0.9", text)
    Assert.True(occurrenceCount text "Tokens.opacityHoverDim" >= 3, "三处 hover 变暗应引 opacityHoverDim")
    Assert.DoesNotContain("Icons.chevronRight Tokens.textFaint", text)
    Assert.Contains("let toolChevronGlyph = Icons.chevronRight Tokens.textMuted", text)
    Assert.True(occurrenceCount text "Padding = Thickness(Tokens.space1, Tokens.fieldRowPaddingY)" >= 2, "两处披露头纵向内边距应引 fieldRowPaddingY")
    Assert.Contains("Padding = Thickness(Tokens.space2, Tokens.compactRowPaddingY)", text)
    Assert.Contains("Spacing = Tokens.fieldRowPaddingY", text)
    Assert.Contains("Thickness(ControlMetrics.accentEdgeWidth, 0.0, 0.0, 0.0)", text)
    Assert.Contains("Ui.spinner ControlMetrics.spinnerSize", text)
    Assert.Contains("Ui.spinner ControlMetrics.spinnerCompactSize", text)
    Assert.DoesNotContain("Ui.spinner 14.0", text)
    Assert.DoesNotContain("Ui.spinner 11.0", text)
    Assert.Contains("retryButton.MinWidth <- ControlMetrics.retryButtonMinWidth", text)
    Assert.Contains("Width = ControlMetrics.caretWidth", text)
    Assert.Contains("Height = ControlMetrics.caretHeight", text)
    Assert.Contains("CornerRadius = CornerRadius ControlMetrics.caretRadius", text)

[<Fact>]
let chatViewSkeletonOpacitiesAndEmptyStateGeometryTokenized () =
    let text = source "src/Wanxiang.UI/Views/ChatView.fs"
    Assert.True(occurrenceCount text "Tokens.skeletonOpacityReduced" >= 5, "减弱动效静态 0.85 应引 skeletonOpacityReduced")
    Assert.Contains("Tokens.skeletonOpacityBase", text)
    Assert.DoesNotContain("Opacity = 0.65", text)
    Assert.True(occurrenceCount text "Tokens.skeletonOpacityBreathMin" >= 2, "呼吸下限应引 skeletonOpacityBreathMin")
    Assert.DoesNotContain("Opacity = 0.55", text)
    Assert.True(occurrenceCount text "ControlMetrics.emptyStateTitleMinWidth" >= 2, "空态标题与编辑壳最小宽应共用 emptyStateTitleMinWidth")
    Assert.Contains("ControlMetrics.emptyStateActionMinWidth", text)
    Assert.Contains("ControlMetrics.emptyStateActionHeight", text)
    Assert.Contains("ControlMetrics.scrollToBottomMinWidth", text)
    // 空态收敛到共享原语：ChatView 经 Ui.emptyStateWith 生成空态卡，标题/说明排版不再本地手写
    // （letterSpacingEmphasis 等移入 Primitives 的空态原语持有）。
    Assert.Contains("Ui.emptyStateWith", text)
    // 「生成中」chip 收敛到 chip 密度唯一真源（此前是 12/3 的离群值）。
    Assert.Contains("ControlMetrics.chipPaddingX", text)

[<Fact>]
let markdownQuoteListAndTaskGeometryTokenizedButColumnLadderStaysContentPolicy () =
    let text = source "src/Wanxiang.UI/Controls/MarkdownRenderer.fs"
    Assert.Contains("ControlMetrics.quoteEdgeWidth", text)
    Assert.DoesNotContain("Thickness(3.0, 0.0, 0.0, 0.0)", text)
    Assert.Contains("Tokens.letterSpacingEmphasis", text)
    Assert.True(occurrenceCount text "ControlMetrics.listMarkerDotSize" >= 2, "列表圆点直径应引 listMarkerDotSize")
    Assert.Contains("ControlMetrics.listMarkerDotRadius", text)
    Assert.True(occurrenceCount text "ControlMetrics.taskBoxSize" >= 2, "任务框尺寸应引 taskBoxSize")
    Assert.Contains("ControlMetrics.taskBoxBorderWidth", text)
    Assert.True(occurrenceCount text "ControlMetrics.taskCheckGlyphSize" >= 2, "勾形尺寸应引 taskCheckGlyphSize")
    // 列宽阶梯是内容策略而非视觉令牌：保留 100/90/80/120 并就地声明，不强行令牌化。
    Assert.Contains("不是视觉令牌", text)
    Assert.Contains("| 1 | 2 -> 100.0", text)
    Assert.Contains("| 3 -> 90.0", text)
    Assert.Contains("| 4 -> 80.0", text)
    Assert.Contains("| _ -> 120.0", text)
