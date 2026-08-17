namespace Wanxiang.UI

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Shapes
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

/// 线稿图标 SSOT：16×16 画布、1.5px 描边；outline / filled 按钮壳与 Theme.iconBtn 对齐。
module Icons =

    let glyph = Theme.iconGlyph
    let stroke = Theme.iconStroke
    let inset = 2.5

    let private canvasIcon (draw: Canvas -> unit) : Viewbox =
        let c = Canvas(Width = glyph, Height = glyph)
        draw c
        Viewbox(Width = glyph, Height = glyph, Stretch = Stretch.Uniform, Child = c)

    let search (brush: IBrush) =
        canvasIcon (fun c ->
            let d = glyph - inset * 2.0
            let ring = Ellipse(Width = d, Height = d, Stroke = brush, StrokeThickness = stroke, Fill = Brushes.Transparent)
            Canvas.SetLeft(ring, inset)
            Canvas.SetTop(ring, inset)
            let r = inset + d
            let handle = Line(StartPoint = Point(r - 1.0, r - 1.0), EndPoint = Point(glyph - inset, glyph - inset), Stroke = brush, StrokeThickness = stroke)
            c.Children.Add(ring) |> ignore
            c.Children.Add(handle) |> ignore)

    let plus (brush: IBrush) =
        canvasIcon (fun c ->
            let mid = glyph * 0.5
            let v = Line(StartPoint = Point(mid, inset), EndPoint = Point(mid, glyph - inset), Stroke = brush, StrokeThickness = stroke)
            let h = Line(StartPoint = Point(inset, mid), EndPoint = Point(glyph - inset, mid), Stroke = brush, StrokeThickness = stroke)
            c.Children.Add(v) |> ignore
            c.Children.Add(h) |> ignore)

    let paperclip (brush: IBrush) =
        canvasIcon (fun c ->
            let p =
                Path(
                    Data =
                        Geometry.Parse(
                            "M 10.8 4.1 C 10.8 2.9 9.7 2.1 8.4 2.1 C 6.6 2.1 5.2 3.5 5.2 5.3 L 5.2 11.3 C 5.2 12.7 6.3 13.8 7.7 13.8 C 9.1 13.8 10.2 12.7 10.2 11.3 L 10.2 6.9 C 10.2 6.1 9.5 5.4 8.7 5.4 C 7.9 5.4 7.2 6.1 7.2 6.9 L 7.2 10.5"),
                    Stroke = brush,
                    StrokeThickness = stroke,
                    Fill = Brushes.Transparent,
                    Stretch = Stretch.None)
            c.Children.Add(p) |> ignore)

    let sendUp (brush: IBrush) =
        canvasIcon (fun c ->
            let mid = glyph * 0.5
            let top = inset
            let bottom = glyph - inset
            let wing = (glyph - inset * 2.0) * 0.36
            c.Children.Add(Line(StartPoint = Point(mid, bottom), EndPoint = Point(mid, top), Stroke = brush, StrokeThickness = stroke)) |> ignore
            c.Children.Add(Line(StartPoint = Point(mid, top), EndPoint = Point(mid - wing, mid), Stroke = brush, StrokeThickness = stroke)) |> ignore
            c.Children.Add(Line(StartPoint = Point(mid, top), EndPoint = Point(mid + wing, mid), Stroke = brush, StrokeThickness = stroke)) |> ignore)

    let copy (brush: IBrush) =
        canvasIcon (fun c ->
            // 前景矩形 + 背景矩形错位，线稿
            let r1 = Rectangle(Width = 7.0, Height = 8.0, Stroke = brush, StrokeThickness = stroke, Fill = Brushes.Transparent, RadiusX = 1.2, RadiusY = 1.2)
            Canvas.SetLeft(r1, 5.0)
            Canvas.SetTop(r1, 3.0)
            let r2 = Rectangle(Width = 7.0, Height = 8.0, Stroke = brush, StrokeThickness = stroke, Fill = Brushes.Transparent, RadiusX = 1.2, RadiusY = 1.2)
            Canvas.SetLeft(r2, 2.0)
            Canvas.SetTop(r2, 5.0)
            // 遮挡处用 parchment 小块覆盖，避免双线重叠
            c.Children.Add(r2) |> ignore
            c.Children.Add(r1) |> ignore)

    let forkIcon (brush: IBrush) =
        canvasIcon (fun c ->
            let p =
                Path(
                    Data = Geometry.Parse "M 7.0 2.2 C 7.0 2.2 7.0 6.0 3.8 7.4 M 7.0 11.8 C 7.0 11.8 7.0 8.0 10.2 6.6 M 7.0 2.2 L 7.0 11.8",
                    Stroke = brush, StrokeThickness = stroke, Fill = Brushes.Transparent, Stretch = Stretch.None)
            c.Children.Add(p) |> ignore
            // 三个端点圆
            for (x, y) in [ (7.0, 2.2); (3.8, 7.4); (10.2, 6.6) ] do
                let dot = Ellipse(Width = 3.0, Height = 3.0, Stroke = brush, StrokeThickness = stroke, Fill = Brushes.Transparent)
                Canvas.SetLeft(dot, x - 1.5)
                Canvas.SetTop(dot, y - 1.5)
                c.Children.Add(dot) |> ignore)

    let gear (brush: IBrush) =
        canvasIcon (fun c ->
            let p =
                Path(
                    Data = Geometry.Parse "M 7 2.6 L 7 3.9 M 7 10.1 L 7 11.4 M 2.6 7 L 3.9 7 M 10.1 7 L 11.4 7 M 4.1 4.1 L 5.0 5.0 M 9.0 9.0 L 9.9 9.9 M 9.9 4.1 L 9.0 5.0 M 5.0 9.0 L 4.1 9.9",
                    Stroke = brush, StrokeThickness = stroke, Fill = Brushes.Transparent, Stretch = Stretch.None, StrokeLineCap = PenLineCap.Round)
            c.Children.Add(p) |> ignore
            let ring = Ellipse(Width = 5.0, Height = 5.0, Stroke = brush, StrokeThickness = stroke, Fill = Brushes.Transparent)
            Canvas.SetLeft(ring, 4.5)
            Canvas.SetTop(ring, 4.5)
            c.Children.Add(ring) |> ignore)

    let xIcon (brush: IBrush) =
        canvasIcon (fun c ->
            c.Children.Add(Line(StartPoint = Point(inset, inset), EndPoint = Point(glyph - inset, glyph - inset), Stroke = brush, StrokeThickness = stroke)) |> ignore
            c.Children.Add(Line(StartPoint = Point(glyph - inset, inset), EndPoint = Point(inset, glyph - inset), Stroke = brush, StrokeThickness = stroke)) |> ignore)

    let check (brush: IBrush) =
        canvasIcon (fun c ->
            let p = Path(Data = Geometry.Parse "M 3.2 7.0 L 6.0 9.8 L 10.8 4.6", Stroke = brush, StrokeThickness = stroke, Fill = Brushes.Transparent, Stretch = Stretch.None, StrokeLineCap = PenLineCap.Round)
            c.Children.Add(p) |> ignore)

    type IconBtnVariant =
        | Outline
        | Filled

    let private centeredHost (icon: Control) =
        let host = Grid()
        host.RowDefinitions.Add(RowDefinition(Height = GridLength(1.0, GridUnitType.Star)))
        host.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        host.RowDefinitions.Add(RowDefinition(Height = GridLength(1.0, GridUnitType.Star)))
        host.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        host.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        host.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        Grid.SetRow(icon, 1)
        Grid.SetColumn(icon, 1)
        host.Children.Add(icon) |> ignore
        host

    let createButton (variant: IconBtnVariant) (icon: Control) : Border =
        Border(
            Width = Theme.iconBtn,
            Height = Theme.iconBtn,
            MinWidth = Theme.iconBtn,
            MinHeight = Theme.iconBtn,
            MaxWidth = Theme.iconBtn,
            MaxHeight = Theme.iconBtn,
            CornerRadius = CornerRadius(Theme.radiusMd),
            Padding = Thickness(0.0),
            ClipToBounds = false,
            Cursor = Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (match variant with Outline -> Theme.panel | Filled -> Theme.primary),
            BorderBrush = (match variant with Outline -> Theme.outlineVariant | Filled -> null),
            BorderThickness = (match variant with Outline -> Thickness(1.0) | Filled -> Thickness(0.0)),
            Child = centeredHost icon)

    let setOutlineActive (btn: Border) (active: bool) =
        if active then
            btn.Background <- Theme.primaryContainer
            btn.BorderBrush <- Theme.muted
        else
            btn.Background <- Theme.panel
            btn.BorderBrush <- Theme.outlineVariant

    let setEnabled (btn: Border) (enabled: bool) =
        btn.Opacity <- if enabled then 1.0 else 0.35
        btn.IsHitTestVisible <- enabled

    /// 固定 32×32 槽位，避免 StackPanel/DockPanel 交叉轴拉伸导致图标偏位
    let slot (btn: Border) : Border =
        Border(
            Width = Theme.iconBtn,
            Height = Theme.iconBtn,
            MinWidth = Theme.iconBtn,
            MinHeight = Theme.iconBtn,
            MaxWidth = Theme.iconBtn,
            MaxHeight = Theme.iconBtn,
            Padding = Thickness(0.0),
            Background = Brushes.Transparent,
            BorderThickness = Thickness(0.0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = btn)
