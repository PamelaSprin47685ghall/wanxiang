module Wanxiang.Tests.IconCenteringTests

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media.Imaging
open Avalonia.Media
open Xunit
open Wanxiang.UI
open Wanxiang.Tests

/// 图标**排版**的锁定测试。
///
/// 万象的图标是 Lucide 官方控件（`IconPacks.Avalonia.Lucide`，ISC）。
/// 分工明确：
/// - **上游负责**：字形路径怎么画、在 24×24 画布里如何定位、像素网格对齐、
///   视觉重量平衡。我们对这些一无所知，也不该知道。
/// - **我们负责**：槽位尺寸落在阶梯上、对齐是居中不是偏移、颜色走令牌、
///   图标确实能渲染出东西（接线故障）。
///
/// 因此这里**不**断言墨迹落在位图第几像素——那是上游的契约。
/// 只断言「官方主题没漏加载」与「槽位排版符合规范」。
/// 见 `.agents/skills/wanxiang/SKILL.md` 第 58 章规则 2。
[<Trait("Category", "UI")>]
type IconCenteringTests() =

    /// F# 函数值禁做 :?> 动态强转，这里保持 obj、走反射 Invoke。
    let invokeIcon (icon: obj) (brush: IBrush) : Control =
        icon.GetType().GetMethod("Invoke", [| typeof<IBrush> |]).Invoke(icon, [| box brush |]) :?> Control

    /// 反射枚举 Icons 模块的语义图标工厂（IBrush -> Control）。
    /// 加新图标不必登记——自动纳入验证面；反射面塌缩（一个都找不到）会被计数断言拦下。
    let factories () : (string * (IBrush -> Control)) list =
        // F# 模块名不能进 typeof<>：借同命名空间的 ThemeMode 锚到程序集，再按名取模块类。
        let moduleType = typeof<ThemeMode>.Assembly.GetType("Wanxiang.UI.Icons", throwOnError = true)
        let flags = BindingFlags.Public ||| BindingFlags.Static
        // 模块级 let 绑定的函数值编译成静态属性；语法函数编译成静态方法。两条都扫。
        let fromProperties =
            moduleType.GetProperties flags
            |> Seq.filter (fun p -> p.PropertyType = typeof<FSharpFunc<IBrush, Control>>)
            |> Seq.map (fun p -> (p.Name, invokeIcon (p.GetValue null)))
        let fromMethods =
            moduleType.GetMethods flags
            |> Seq.filter (fun m ->
                let parameters = m.GetParameters()
                not m.IsSpecialName
                && parameters.Length = 1
                && parameters.[0].ParameterType = typeof<IBrush>
                && m.ReturnType = typeof<Control>)
            |> Seq.map (fun m -> (m.Name, fun (brush: IBrush) -> m.Invoke(null, [| box brush |]) :?> Control))
        List.ofSeq (Seq.append fromProperties fromMethods)

    /// 仓库根：测试运行目录是 bin/Debug/net10.0，向上找 wanxiang.slnx。
    let repoRoot () =
        let mutable dir = DirectoryInfo(Directory.GetCurrentDirectory())
        while not (isNull dir) && not (File.Exists(Path.Combine(dir.FullName, "wanxiang.slnx"))) do
            dir <- dir.Parent
        if isNull dir then "." else dir.FullName

    [<Trait("Category", "UI")>]
    /// 白底宿主 + 亮度扫描：统计图标真正画出来的墨迹像素。
    ///
    /// **为什么必须有这一条**：`PackIconLucide` 是透明底的描边字形，
    /// 「控件在活树里取得了尺寸」只证明布局通了，不证明**画出来了**。
    /// 实测：光有尺寸而零墨迹（字形几何坐标系错位、模板没应用、主题漏加载）
    /// 全部会通过尺寸断言——产品面表现是「按钮是个空色块」。
    /// 这是**接线故障**，属于我们自己的契约（规范 §1 与 SSOT 第 58 章规则 2）。
    let inkOnPaper (control: Control) (pixels: int) =
        let host = Border(
            Width = float pixels,
            Height = float pixels,
            Background = SolidColorBrush(Color.Parse "#FFFFFF"))
        host.Child <- control
        host.Measure(Size(float pixels, float pixels))
        host.Arrange(Rect(0.0, 0.0, float pixels, float pixels))
        let px = PixelSize(pixels, pixels)
        use bitmap = new RenderTargetBitmap(px, Vector(96.0, 96.0))
        bitmap.Render host
        let stride = px.Width * 4
        let buffer = Array.zeroCreate<byte> (stride * px.Height)
        let handle = GCHandle.Alloc(buffer, GCHandleType.Pinned)
        try
            bitmap.CopyPixels(PixelRect px, handle.AddrOfPinnedObject(), buffer.Length, stride)
        finally
            handle.Free()
        let mutable count = 0
        for i in 0 .. px.Width * px.Height - 1 do
            let o = i * 4
            if buffer.[o] < 231uy && buffer.[o + 1] < 231uy && buffer.[o + 2] < 231uy then
                count <- count + 1
        count

    [<Fact>]
    member _.``全部语义图标都真正画出墨迹`` () =
        Headless.run (fun () ->
            // 零墨迹 = 按钮变空色块。这是接线故障，必须红。
            let silent = ResizeArray<string>()
            for name, icon in factories () do
                let control = icon Brushes.Black
                control.Width <- 48.0
                control.Height <- 48.0
                let ink = inkOnPaper control 48
                if ink = 0 then silent.Add name
            Assert.True(
                silent.Count = 0,
                "以下图标零墨迹（字形没画出来，产品面是空色块）：\n  " + String.Join("\n  ", silent))
        )

    [<Fact>]
    member _.``官方图标主题已加载：图标在活视觉树里取得尺寸`` () =
        Headless.run (fun () ->
            // 官方主题由 Headless.fs 的 TestApp 加载，与生产 App.fs 同一路径。
            // 漏加载 → PackIconLucide 无模板 → 在树里量不到尺寸 → 这里变红。
            // 只断言「有没有」不断言「在哪」：字形内部怎么画是上游的契约。
            let icons = factories ()
            Assert.True(icons.Length >= 40, $"反射只找到 {icons.Length} 个图标，Icons 模块结构可能变了")
            let window = Window(Width = 400.0, Height = 300.0)
            let panel = StackPanel(Orientation = Orientation.Horizontal, Spacing = 0.0)
            for _, icon in icons do
                panel.Children.Add(icon Brushes.Black)
            window.Content <- panel
            window.Show()
            Avalonia.Threading.Dispatcher.UIThread.RunJobs()
            try
                for index in 0 .. icons.Length - 1 do
                    let name, _ = icons.[index]
                    let control = panel.Children.[index]
                    let size = control.Bounds
                    Assert.True(
                        size.Width > 0.0 && size.Height > 0.0,
                        $"{name} 在活视觉树里尺寸为 {size.Width}×{size.Height}：官方 Lucide 主题没加载")
            finally
                window.Close()
        )

    [<Trait("Category", "UI")>]
    [<Fact>]
    member _.``全部语义图标槽位尺寸落 Glyph 阶梯且正方形`` () =
        // 构造 Avalonia 控件必须有无显示平台，因此本用例是 UI 测试
        // （`Unable to locate 'Avalonia.Platform.IAssetLoader'` 就是漏包 Headless.run 的症状）。
        Headless.run (fun () ->
        // 规范 §1.3：字形只有三档，全落 Spacing.Glyph，且三档互不相等。
        Assert.Equal(3, Spacing.Glyph.all.Length)
        Assert.True(Spacing.Glyph.sm < Spacing.Glyph.md, "小档应小于常规档")
        Assert.True(Spacing.Glyph.md < Spacing.Glyph.lg, "常规档应小于大档")
        Assert.NotEqual(Spacing.Glyph.sm, Spacing.Glyph.md)
        Assert.NotEqual(Spacing.Glyph.md, Spacing.Glyph.lg)
        Assert.NotEqual(Spacing.Glyph.sm, Spacing.Glyph.lg)
        for name, icon in factories () do
            let control = icon Brushes.Black
            control.Measure(Size(infinity, infinity))
            let size = control.DesiredSize
            Assert.True(
                Spacing.isGlyph size.Width && size.Width = size.Height,
                $"{name} 槽位 {size.Width}×{size.Height} 不在 Spacing.Glyph 阶梯或非正方形")
        )

    [<Trait("Category", "UI")>]
    [<Fact>]
    member _.``全部语义图标水平垂直都居中排版`` () =
        // 同上：控件构造需要无显示平台。
        Headless.run (fun () ->
        // 规范 §1.5：图标与文字同排时垂直居中，MUST NOT 用 Margin 偏移微调。
        // 这是我们自己的契约，因此由我们断言；字形内部怎么画不在此列。
        for name, icon in factories () do
            let control = icon Brushes.Black
            Assert.True(
                control.HorizontalAlignment = HorizontalAlignment.Center,
                $"{name} 未水平居中（图标在槽位里偏移会让同行文字对不齐）")
            Assert.True(
                control.VerticalAlignment = VerticalAlignment.Center,
                $"{name} 未垂直居中")
            Assert.True(
                control.Margin = Thickness 0.0,
                $"{name} 带 Margin 偏移：靠偏移做基线对齐是旧反模式，规范禁止")
        )

    [<Fact>]
    member _.``图标模块不自绘几何`` () =
        // 规则 1（SSOT 第 58 章）：上游负责的字形几何 MUST NOT 在本地重实现。
        // 「用官方数据 + 自家解析器」同样是山寨，因此连 `Geometry.Parse` 都不许出现。
        // 只看代码行：注释里提到这些名字是**说明禁令**，不是违规。
        let source =
            File.ReadAllLines(Path.Combine(repoRoot (), "src/Wanxiang.UI/Design/Icons.fs"))
            |> Array.filter (fun line ->
                let t = line.Trim()
                not (t.StartsWith "//") && not (t.StartsWith "*") && not (t.StartsWith "///"))
            |> String.concat "\n"
        Assert.DoesNotContain("Geometry.Parse", source)
        Assert.DoesNotContain("Shapes.Path", source)
        Assert.DoesNotContain("StrokeThickness", source)
        Assert.DoesNotContain("StrokeLineCap", source)
        Assert.DoesNotContain("Viewbox", source)