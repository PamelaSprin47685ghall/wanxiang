module Wanxiang.Tests.DesignConvergenceTests

open System
open System.IO
open System.Text.RegularExpressions
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Xunit
open Wanxiang.UI
open Wanxiang.Tests

/// UI 规范的**机械校验**：把 `.agents/skills/wanxiang-ui/SKILL.md` 里
/// 可机器判定的条款钉成测试。规范是权威，本文件只锁「违反即红」的那些：
/// 阶梯越界、槽位分叉、裸数字回流、调色板对比度不足。
/// 不锁字体度量一类无法机械判定的条款。
///
/// 静态扫描用源码文本而不是运行时控件树：违反阶梯是**源码层事实**，
/// 运行时只看得到最终布局值（已被布局消化/取整），扫源码才是真防线。
module private SpacingScan =

    /// 显式 Thickness(1..4 个数值)
    let thicknessPattern = Regex(@"Thickness\(\s*([0-9.]+)\s*(?:,\s*([0-9.]+)\s*){0,3}\)")

    /// 单值 Margin/Padding/Spacing/RowSpacing/ColumnSpacing 赋值
    let singleValuePattern =
        Regex(@"(?:^|[^A-Za-z])(?:Margin|Padding|Spacing|RowSpacing|ColumnSpacing)\s*=\s*([0-9.]+)\s*$")

    let scannedDirs =
        [| "src/Wanxiang.UI/Views"; "src/Wanxiang.UI/Controls"; "src/Wanxiang.UI/Rich" |]

    /// 仓库根：测试运行目录是 bin/Debug/net10.0，向上找 wanxiang.slnx。
    let repoRoot =
        let mutable dir = DirectoryInfo(Directory.GetCurrentDirectory())
        while not (isNull dir) && not (File.Exists(Path.Combine(dir.FullName, "wanxiang.slnx"))) do
            dir <- dir.Parent
        if isNull dir then "." else dir.FullName

    let filesUnder () =
        scannedDirs
        |> Array.collect (fun d ->
            let full = Path.Combine(repoRoot, d)
            if Directory.Exists full then Directory.GetFiles(full, "*.fs", SearchOption.AllDirectories)
            else [||])

    /// 间距 / 槽位越界描述（空列表 = 全部合规）。
    let violations () =
        let allowed = Set.ofList Spacing.ramp
        let found = ResizeArray<string>()
        for file in filesUnder () do
            let name = Path.GetFileName file
            let lines = File.ReadAllLines file
            for i in 0 .. lines.Length - 1 do
                let line = lines.[i]
                for m in thicknessPattern.Matches line do
                    for g in m.Groups |> Seq.skip 1 do
                        if g.Success then
                            let v = Double.Parse g.Value
                            if not (allowed.Contains v) then
                                found.Add(sprintf "%s:%d  Thickness 含阶梯外值 %g" name (i + 1) v)
                let mm = singleValuePattern.Match(line.TrimEnd())
                if mm.Success then
                    let v = Double.Parse mm.Groups.[1].Value
                    if not (allowed.Contains v) then
                        found.Add(sprintf "%s:%d  Margin/Padding/Spacing 阶梯外值 %g" name (i + 1) v)
        List.ofSeq found

    /// 行匹配器 → 越界描述。捕获组 1 为字面量，**零值放行**
    /// （`Thickness 0.0` 是"无描边"/"不缩进"，是合法几何，不是漏写的令牌）。
    let lineViolations (patterns: Regex list) =
        let found = ResizeArray<string>()
        for file in filesUnder () do
            let name = Path.GetFileName file
            let lines = File.ReadAllLines file
            for i in 0 .. lines.Length - 1 do
                for p in patterns do
                    let m = p.Match lines.[i]
                    if m.Success && Double.Parse m.Groups.[1].Value <> 0.0 then
                        found.Add(sprintf "%s:%d  %s" name (i + 1) (lines.[i].Trim()))
        List.ofSeq found

/// WCAG 相对亮度与对比度（规范 §5.2 的判定函数）。
/// alpha 一律当不透明：半透明笔只用于覆盖层，其上的文字另有专门 token。
module private Contrast =

    let private channel (v: float) =
        let c = v / 255.0
        if c <= 0.03928 then c / 12.92 else ((c + 0.055) / 1.055) ** 2.4

    let luminance (c: Color) =
        0.2126 * channel (float c.R) + 0.7152 * channel (float c.G) + 0.0722 * channel (float c.B)

    let ratio (a: Color) (b: Color) =
        let la, lb = luminance a, luminance b
        (max la lb + 0.05) / (min la lb + 0.05)

[<Trait("Category", "UI")>]
type DesignConvergenceTests() =

    // ---------- 阶梯本身 ----------

    [<Fact>]
    member _.``ramp 是 Fluent 2 官方取值`` () =
        // 官方 ramp：4px 基准 + 2/6/10 三档例外（图标内衬与像素网格对齐用）。
        // 这三条断言锁住「例外档不许被当成凑整删掉」。
        Assert.Equal(2.0, Spacing.spaceXXs)
        Assert.Equal(6.0, Spacing.spaceSm)
        Assert.Equal(10.0, Spacing.spaceLg)
        // 4px 步进的主干
        Assert.Equal(4.0, Spacing.spaceXs)
        Assert.Equal(8.0, Spacing.spaceMd)
        Assert.Equal(12.0, Spacing.spaceXl)
        Assert.Equal(16.0, Spacing.space2xl)

    [<Fact>]
    member _.``ramp 严格递增且无重复`` () =
        let ramp = Spacing.ramp
        Assert.True(ramp.Length > 10, $"ramp 档数 {ramp.Length} 过少，官方 ramp 是 17 档")
        for i in 1 .. ramp.Length - 1 do
            Assert.True(
                ramp.[i] > ramp.[i - 1],
                $"ramp 非严格递增：{ramp.[i - 1]} 之后是 {ramp.[i]}")
        Assert.Equal(ramp.Length, List.distinct ramp |> List.length)

    [<Fact>]
    member _.``组件默认圆角收敛到 Fluent 的 8`` () =
        // 旧值 9 是自制值，非任何标准。控件默认圆角统一到 radiusMd(8)。
        Assert.Equal(8.0, Spacing.Radius.md)
        Assert.True(Spacing.isRadius Spacing.Radius.md)

    [<Fact>]
    member _.``描边只有三档且焦点环与强调条同档`` () =
        Assert.Equal(1.0, Spacing.Stroke.thin)
        Assert.Equal(2.0, Spacing.Stroke.thick)
        Assert.Equal(3.0, Spacing.Stroke.quote)
        Assert.Equal(3, Spacing.Stroke.all.Length)
        // 焦点环外扩与左缘强调条同为 2 —— 同一档，不是巧合。
        Assert.Equal(Spacing.Stroke.thick, ControlMetrics.accentEdgeWidth)

    [<Fact>]
    member _.``字距四档顺序不乱`` () =
        Assert.Equal(0.3, Spacing.Tracking.label)
        Assert.Equal(0.4, Spacing.Tracking.emphasis)
        Assert.Equal(0.6, Spacing.Tracking.section)
        Assert.Equal(0.8, Spacing.Tracking.display)
        Assert.True(Spacing.Tracking.label < Spacing.Tracking.emphasis)
        Assert.True(Spacing.Tracking.emphasis < Spacing.Tracking.section)
        Assert.True(Spacing.Tracking.section < Spacing.Tracking.display)

    // ---------- 槽位收敛 ----------

    [<Fact>]
    member _.``同类槽位同值不再分叉`` () =
        // 此前 18 / 19 / 54 / 56 / 110 / 15 各写一份，是「对不齐」的直接来源。
        // 现在全应用只有 icon / wide / wide2 三档。
        Assert.Equal(Spacing.Slot.icon, ControlMetrics.menuIconSlotWidth)
        Assert.Equal(Spacing.Slot.icon, ControlMetrics.sidebarStateSlotWidth)
        Assert.Equal(Spacing.Slot.wide, ControlMetrics.composerAttachmentStateWidth)
        Assert.Equal(Spacing.Slot.wide, ControlMetrics.fontSizeValueMinWidth)
        Assert.Equal(Spacing.Slot.wide2, ControlMetrics.aboutKeyMinWidth)
        Assert.Equal(Tokens.iconGlyph, ControlMetrics.composerAttachmentIconSlot)
        Assert.Equal(3, Spacing.Slot.all.Length)

    [<Trait("Category", "UI")>]
    [<Fact>]
    member _.``tag 与 chip 纵向密度同档`` () =
        Headless.run (fun () ->
        // tag / chip 同为单行小控件，纵向密度不应分叉（规范 §2.1）。
        let tag = Ui.tag "示例"
        Assert.Equal(Spacing.spaceXXs, tag.Padding.Top)
        Assert.Equal(ControlMetrics.chipPaddingY, tag.Padding.Top)
        Assert.Equal(Spacing.spaceMd, ControlMetrics.chipPaddingX)
        Assert.Equal(Spacing.spaceSm, ControlMetrics.tagPaddingX)
        )

    // ---------- 静态扫描：阶梯越界 ----------

    [<Fact>]
    member _.``视图层内边距全部落在 Fluent ramp 上`` () =
        let v = SpacingScan.violations ()
        Assert.True(
            v.IsEmpty,
            "视图层内边距越界（须改用 Spacing ramp 档）：\n  " + String.Join("\n  ", v))

    [<Fact>]
    member _.``字号一律走令牌不写裸值`` () =
        // 规范 §3.2：视图层禁止 `FontSize = <数字>`。
        let v = SpacingScan.lineViolations [ Regex(@"FontSize\s*=\s*([0-9.]+)") ]
        Assert.True(v.IsEmpty, "字号裸值：\n  " + String.Join("\n  ", v))

    [<Fact>]
    member _.``圆角与描边一律走令牌不写裸值`` () =
        // 规范 §2.3 / §2.4：CornerRadius / BorderThickness 只读 Spacing.Radius / Spacing.Stroke。
        let v =
            SpacingScan.lineViolations
                [ Regex(@"CornerRadius\(\s*([0-9.]+)")
                  Regex(@"BorderThickness\s*=\s*Thickness\s*\(?\s*([0-9.]+)") ]
        Assert.True(v.IsEmpty, "圆角/描边裸值：\n  " + String.Join("\n  ", v))

    // ---------- 对比度契约 ----------

    [<Fact>]
    member _.``明暗两调色板全部前景过 WCAG AA`` () =
        // 规范 §5.2：正文 4.5:1。实测最低值 4.58（浅 textFaint on rail），
        // 门槛 4.5 只比实测下限低 0.08——是真实余量，不是宽放。
        let surfaces =
            [| "canvas"; "rail"; "surface"; "surfaceContainer"; "surfaceRaised" |]
        let inks =
            [| "text"; "textMuted"; "textFaint"; "accent"; "danger"; "warning"; "success"; "info" |]
        let pick (palette: Palette) (name: string) =
            match name with
            | "canvas" -> palette.canvas
            | "rail" -> palette.rail
            | "surface" -> palette.surface
            | "surfaceContainer" -> palette.surfaceContainer
            | "surfaceRaised" -> palette.surfaceRaised
            | "text" -> palette.text
            | "textMuted" -> palette.textMuted
            | "textFaint" -> palette.textFaint
            | "accent" -> palette.accent
            | "danger" -> palette.danger
            | "warning" -> palette.warning
            | "success" -> palette.success
            | "info" -> palette.info
            | _ -> failwithf "调色板没有槽位 %s（新增槽位必须同步进本表与规范 §5.1）" name
        let cases : (string * Palette) list = [ "light", Palette.light; "dark", Palette.dark ]
        for (label, palette) in cases do
            for surface in surfaces do
                for ink in inks do
                    let ratio = Contrast.ratio (pick palette ink) (pick palette surface)
                    Assert.True(
                        ratio >= 4.5,
                        sprintf "%s: %s on %s 对比度 %.2f < 4.5（WCAG AA）" label ink surface ratio)

    [<Fact>]
    member _.``强调色上的文字过 4.5 比`` () =
        // 实心强调按钮上的文字是 textOnAccent，单独验。
        let cases : (string * Palette) list = [ "light", Palette.light; "dark", Palette.dark ]
        for (label, palette) in cases do
            let ratio = Contrast.ratio palette.textOnAccent palette.accent
            Assert.True(
                ratio >= 4.5, sprintf "%s: textOnAccent on accent 对比度 %.2f < 4.5" label ratio)

    // ---------- 状态反馈单源 ----------

    [<Fact>]
    member _.``几何动画仍然全面禁止`` () =
        // 规范 §4.3：状态只补间颜色与不透明度。
        Assert.False(MotionLedger.geometryAnimationAllowed)