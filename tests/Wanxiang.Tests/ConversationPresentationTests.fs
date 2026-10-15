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
// 这些断言把「对话呈现面收敛」赖以成立的令牌阶梯钉住：任何一处值漂移都会在此变红，
// 从而阻止调用点又回到魔法数。

[<Fact>]
let edgeWidthTiersKeepAccentAndQuoteDistinct () =
    Assert.True(approx ControlMetrics.accentEdgeWidth Spacing.Stroke.thick, "accentEdgeWidth 应读 Stroke.thick")
    Assert.True(approx ControlMetrics.quoteEdgeWidth Spacing.Stroke.quote, "quoteEdgeWidth 应读 Stroke.quote")
    // 两档有意保留：引用块印刷条比通用强调条宽一档（依据 Spacing.Stroke 注释）。
    Assert.True(ControlMetrics.quoteEdgeWidth > ControlMetrics.accentEdgeWidth, "quote 档应宽于 accent 档")

[<Fact>]
let rowPaddingLadderFollowsFluentRamp () =
    // 旧的 tight(1)/compact(2)/field(3) 三档是自制刻度，clean-break 后全部删除。
    // 现在单行小控件的纵向呼吸统一读 ramp 的 size20(2)，贴字段留白读 size60(6)。
    Assert.True(approx Spacing.spaceXXs 2.0, "单行小控件纵向内衬应收敛到 ramp 的 size20(2)")
    Assert.True(approx Spacing.spaceXs 4.0, "紧凑堆叠间距应收敛到 ramp 的 size40(4)")
    Assert.True(approx Spacing.spaceSm 6.0, "贴字段留白应收敛到 ramp 的 size60(6)")
    Assert.True(Spacing.spaceXXs < Spacing.spaceXs, "内衬 < 堆叠间距")
    Assert.True(Spacing.spaceXs < Spacing.spaceSm, "堆叠间距 < 贴字段留白")

[<Fact>]
let skeletonOpacityTiersOrderedForBaseBreathAndReduced () =
    Assert.True(approx Tokens.skeletonOpacityBase 0.65, "skeletonOpacityBase 应为 0.65")
    Assert.True(approx Tokens.skeletonOpacityBreathMin 0.55, "skeletonOpacityBreathMin 应为 0.55")
    Assert.True(approx Tokens.skeletonOpacityReduced 0.85, "skeletonOpacityReduced 应为 0.85")
    Assert.True(Tokens.skeletonOpacityBreathMin < Tokens.skeletonOpacityBase, "呼吸下限应低于静态 base")
    Assert.True(Tokens.skeletonOpacityBase < Tokens.skeletonOpacityReduced, "reduced 静态值应高于 base")

[<Fact>]
let letterSpacingKeepsEmphasisBelowDisplay () =
    Assert.True(approx Spacing.Tracking.emphasis 0.4, "letterSpacingEmphasis 应为 0.4")
    Assert.True(approx Spacing.Tracking.display 0.8, "letterSpacingDisplay 应为 0.8")
    Assert.True(Spacing.Tracking.emphasis < Spacing.Tracking.display, "emphasis < display")

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
let chatViewGeneratingChipTokenized () =
    let text = source "vendor/wanxiang2/compose/ChatScreen/Looks.fs"
    // 顶栏外观由组合者从同一主题取值，不再扫描已退出的旧视图。
    Assert.Contains("ChipPadX", text)
    Assert.Contains("ChipPaddingX", text)
