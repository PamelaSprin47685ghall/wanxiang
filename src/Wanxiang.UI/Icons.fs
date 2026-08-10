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

    let private wrap (content: Control) =
        Viewbox(
            Width = glyph, Height = glyph, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content)

    let private canvas () = Canvas(Width = glyph, Height = glyph)

    let search (brush: IBrush) =
        let c = canvas ()
        let ring = Ellipse(Width = 9.0, Height = 9.0, Stroke = brush, StrokeThickness = stroke, Fill = Brushes.Transparent)
        Canvas.SetLeft(ring, 1.5)
        Canvas.SetTop(ring, 1.5)
        let handle = Line(StartPoint = Point(9.0, 9.0), EndPoint = Point(13.5, 13.5), Stroke = brush, StrokeThickness = stroke)
        c.Children.Add(ring) |> ignore
        c.Children.Add(handle) |> ignore
        wrap c

    let plus (brush: IBrush) =
        let c = canvas ()
        let v = Line(StartPoint = Point(8.0, 3.5), EndPoint = Point(8.0, 12.5), Stroke = brush, StrokeThickness = stroke)
        let h = Line(StartPoint = Point(3.5, 8.0), EndPoint = Point(12.5, 8.0), Stroke = brush, StrokeThickness = stroke)
        c.Children.Add(v) |> ignore
        c.Children.Add(h) |> ignore
        wrap c

    let paperclip (brush: IBrush) =
        let p =
            Path(
                Data =
                    Geometry.Parse(
                        "M 11.5 4 C 11.5 2.6 10.3 1.5 8.8 1.5 C 6.7 1.5 5 3.2 5 5.3 L 5 11.2 C 5 12.8 6.2 14 7.8 14 C 9.4 14 10.6 12.8 10.6 11.2 L 10.6 6.8 C 10.6 5.9 9.9 5.2 9 5.2 C 8.1 5.2 7.4 5.9 7.4 6.8 L 7.4 10.5"),
                Stroke = brush,
                StrokeThickness = stroke,
                Fill = Brushes.Transparent,
                Stretch = Stretch.None)
        wrap p

    let sendUp (brush: IBrush) =
        let c = canvas ()
        let shaft = Line(StartPoint = Point(8.0, 12.5), EndPoint = Point(8.0, 4.0), Stroke = brush, StrokeThickness = stroke)
        let left = Line(StartPoint = Point(8.0, 4.0), EndPoint = Point(4.5, 8.0), Stroke = brush, StrokeThickness = stroke)
        let right = Line(StartPoint = Point(8.0, 4.0), EndPoint = Point(11.5, 8.0), Stroke = brush, StrokeThickness = stroke)
        c.Children.Add(shaft) |> ignore
        c.Children.Add(left) |> ignore
        c.Children.Add(right) |> ignore
        wrap c

    type IconBtnVariant =
        | Outline
        | Filled

    let createButton (variant: IconBtnVariant) (icon: Control) : Border =
        match variant with
        | Outline ->
            Border(
                Width = Theme.iconBtn,
                Height = Theme.iconBtn,
                CornerRadius = CornerRadius(Theme.radiusMd),
                Background = Theme.panel,
                BorderBrush = Theme.outlineVariant,
                BorderThickness = Thickness(1.0),
                Cursor = Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = icon)
        | Filled ->
            Border(
                Width = Theme.iconBtn,
                Height = Theme.iconBtn,
                CornerRadius = CornerRadius(Theme.radiusMd),
                Background = Theme.primary,
                BorderThickness = Thickness(0.0),
                Cursor = Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = icon)

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
