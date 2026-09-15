module Wanxiang.Tests.IconCenteringTests

open System
open System.Reflection
open System.Runtime.InteropServices
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Shapes
open Avalonia.Media
open Avalonia.Media.Imaging
open Xunit
open Wanxiang.UI
open Wanxiang.Tests

/// 图标是按钮的眼睛：墨迹在 16×16 画布里偏心，按钮里的图标就整个歪掉。
/// 模型没有眼睛，用两层机械验证代替目检——
/// ① 几何层：路径包围盒 + 工厂平移量，中心必须恰为画布中心（锁工厂不变量）；
/// ② 像素层：真渲染成位图扫 alpha 通道，实际墨迹中心必须落在位图中心（锁视觉事实，
///    防止「平移量算了但渲染管线没吃进去」这类机制性失效）。

/// F# 函数值禁做 :?> 动态强转，这里保持 obj、走反射 Invoke。
let private invokeIcon (icon: obj) (brush: IBrush) : Control =
    icon.GetType().GetMethod("Invoke", [| typeof<IBrush> |]).Invoke(icon, [| box brush |]) :?> Control

/// 反射枚举 Icons 模块的全部图标工厂（IBrush -> Control）。
/// 加新图标不必登记——自动纳入验证面；反射面塌缩（一个都找不到）会被计数断言拦下。
let private factories () : (string * (IBrush -> Control)) list =
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
            && parameters[0].ParameterType = typeof<IBrush>
            && m.ReturnType = typeof<Control>)
        |> Seq.map (fun m -> (m.Name, fun (brush: IBrush) -> m.Invoke(null, [| box brush |]) :?> Control))
    List.ofSeq (Seq.append fromProperties fromMethods)

/// Viewbox → Canvas → Path，取图标内部那条路径。
let private pathOf (icon: Control) =
    let host = (icon :?> Viewbox).Child :?> Canvas
    host.Children[0] :?> Path

[<Fact>]
let ``全量图标几何墨迹中心恰在画布中心`` () =
    Headless.ensure ()
    let icons = factories ()
    Assert.True(List.length icons >= 40, $"反射只找到 {List.length icons} 个图标，Icons 模块结构可能变了")
    for name, icon in icons do
        let path = pathOf (icon Brushes.Black)
        let bounds = path.Data.Bounds
        match path.RenderTransform with
        | :? TranslateTransform as shift ->
            let centerX = bounds.X + bounds.Width / 2.0 + shift.X
            let centerY = bounds.Y + bounds.Height / 2.0 + shift.Y
            Assert.True(
                abs (centerX - 8.0) < 0.001 && abs (centerY - 8.0) < 0.001,
                $"{name} 墨迹中心=({centerX:F2},{centerY:F2})，不在 16×16 画布中心")
        | _ -> failwith $"{name} 工厂没有施加居中平移"

[<Fact>]
let ``全量图标渲染像素墨迹居中`` () =
    Headless.ensure ()
    for name, icon in factories () do
        let control = icon Brushes.Black
        control.Measure(Size(infinity, infinity))
        let size = control.DesiredSize
        control.Arrange(Rect size)
        let pixels = PixelSize(int (ceil size.Width), int (ceil size.Height))
        use bitmap = new RenderTargetBitmap(pixels, Vector(96.0, 96.0))
        bitmap.Render control
        let stride = pixels.Width * 4
        let buffer = Array.zeroCreate<byte> (stride * pixels.Height)
        let handle = GCHandle.Alloc(buffer, GCHandleType.Pinned)
        try
            bitmap.CopyPixels(PixelRect pixels, handle.AddrOfPinnedObject(), buffer.Length, stride)
        finally
            handle.Free()
        let mutable minX = pixels.Width
        let mutable minY = pixels.Height
        let mutable maxX = -1
        let mutable maxY = -1
        for y in 0 .. pixels.Height - 1 do
            for x in 0 .. pixels.Width - 1 do
                if buffer[y * stride + x * 4 + 3] > 0uy then
                    if x < minX then minX <- x
                    if x > maxX then maxX <- x
                    if y < minY then minY <- y
                    if y > maxY then maxY <- y
        Assert.True(maxX >= minX, $"{name} 渲染不出任何墨迹")
        // 抗锯齿边沿与像素栅格量化各容半像素。
        let centerX = float (minX + maxX) / 2.0
        let centerY = float (minY + maxY) / 2.0
        Assert.True(
            abs (centerX - float pixels.Width / 2.0) <= 1.0
            && abs (centerY - float pixels.Height / 2.0) <= 1.0,
            $"{name} 渲染墨迹中心=({centerX:F1},{centerY:F1})，偏离 {pixels.Width}×{pixels.Height} 位图中心")
