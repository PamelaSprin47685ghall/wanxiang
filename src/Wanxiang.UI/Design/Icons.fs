namespace Wanxiang.UI

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Shapes
open Avalonia.Media

/// 线稿图标集：统一 16×16 画布、圆头圆角描边。
///
/// 全部用路径数据描述，因此加图标只要加一条 `path`，
/// 不必再拼 `Line` / `Ellipse` 组合。
module Icons =

    /// 图标设计画布边长。渲染时由 Viewbox 缩放到 `Tokens.iconGlyph`。
    let private box = 16.0

    let private shape (data: string) (brush: IBrush) (thickness: float) : Control =
        let path =
            Path(
                Data = Geometry.Parse data,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Stretch = Stretch.None)
        let host = Canvas(Width = box, Height = box)
        host.Children.Add path |> ignore
        Viewbox(Width = Tokens.iconGlyph, Height = Tokens.iconGlyph, Stretch = Stretch.Uniform, Child = host) :> Control

    let private filled (data: string) (brush: IBrush) : Control =
        let path = Path(Data = Geometry.Parse data, Fill = brush, Stretch = Stretch.None)
        let host = Canvas(Width = box, Height = box)
        host.Children.Add path |> ignore
        Viewbox(Width = Tokens.iconGlyph, Height = Tokens.iconGlyph, Stretch = Stretch.Uniform, Child = host) :> Control

    let private line (data: string) (brush: IBrush) : Control = shape data brush Tokens.iconStroke

    let search : IBrush -> Control = line "M 7.2 2.4 A 4.8 4.8 0 1 1 7.2 12 A 4.8 4.8 0 1 1 7.2 2.4 M 10.8 10.8 L 13.8 13.8"
    let plus : IBrush -> Control = line "M 8 3 L 8 13 M 3 8 L 13 8"
    let minus : IBrush -> Control = line "M 3.5 8 L 12.5 8"
    let close : IBrush -> Control = line "M 4 4 L 12 12 M 12 4 L 4 12"
    let check : IBrush -> Control = line "M 3.4 8.4 L 6.4 11.4 L 12.8 4.8"
    let chevronDown : IBrush -> Control = line "M 4 6.4 L 8 10.4 L 12 6.4"
    let chevronUp : IBrush -> Control = line "M 4 9.6 L 8 5.6 L 12 9.6"
    let chevronRight : IBrush -> Control = line "M 6.4 4 L 10.4 8 L 6.4 12"
    let chevronLeft : IBrush -> Control = line "M 9.6 4 L 5.6 8 L 9.6 12"
    let arrowDown : IBrush -> Control = line "M 8 3.2 L 8 12.4 M 4.4 8.8 L 8 12.4 L 11.6 8.8"
    let arrowUp : IBrush -> Control = line "M 8 12.8 L 8 3.6 M 4.4 7.2 L 8 3.6 L 11.6 7.2"
    let arrowLeft : IBrush -> Control = line "M 12.6 8 L 3.4 8 M 7 3.6 L 3.4 8 L 7 12.4"

    let paperclip : IBrush -> Control =
        line "M 11.4 7.4 L 6.6 12.2 A 2.6 2.6 0 0 1 2.9 8.5 L 8.4 3 A 2.2 2.2 0 0 1 11.5 6.1 L 6.2 11.4 A 1 1 0 0 1 4.8 10 L 9.4 5.4"

    let send : IBrush -> Control = line "M 8 13.2 L 8 3.2 M 3.8 7.4 L 8 3.2 L 12.2 7.4"

    let stop : IBrush -> Control = filled "M 4.6 4.6 L 11.4 4.6 L 11.4 11.4 L 4.6 11.4 Z"

    let copy : IBrush -> Control =
        line "M 5.6 5.6 L 5.6 3.4 A 1 1 0 0 1 6.6 2.4 L 12.6 2.4 A 1 1 0 0 1 13.6 3.4 L 13.6 9.4 A 1 1 0 0 1 12.6 10.4 L 10.4 10.4 M 3.4 5.6 L 9.4 5.6 A 1 1 0 0 1 10.4 6.6 L 10.4 12.6 A 1 1 0 0 1 9.4 13.6 L 3.4 13.6 A 1 1 0 0 1 2.4 12.6 L 2.4 6.6 A 1 1 0 0 1 3.4 5.6 Z"

    let fork : IBrush -> Control =
        line "M 5 3.6 A 1.5 1.5 0 1 1 5 3.61 M 11 3.6 A 1.5 1.5 0 1 1 11 3.61 M 8 12.4 A 1.5 1.5 0 1 1 8 12.41 M 5 5.1 L 5 6.4 A 2 2 0 0 0 7 8.4 L 9 8.4 A 2 2 0 0 0 11 6.4 L 11 5.1 M 8 8.4 L 8 10.9"

    let refresh : IBrush -> Control =
        line "M 13.2 8 A 5.2 5.2 0 1 1 11.1 3.8 M 13.4 2.6 L 13.4 6 L 10 6"

    let trash : IBrush -> Control =
        line "M 3.2 5.2 L 12.8 5.2 M 6.4 5.2 L 6.4 3.6 A 0.8 0.8 0 0 1 7.2 2.8 L 8.8 2.8 A 0.8 0.8 0 0 1 9.6 3.6 L 9.6 5.2 M 4.6 5.2 L 5.2 12.6 A 0.9 0.9 0 0 0 6.1 13.4 L 9.9 13.4 A 0.9 0.9 0 0 0 10.8 12.6 L 11.4 5.2 M 6.9 7.6 L 7.1 11.2 M 9.1 7.6 L 8.9 11.2"

    let pencil : IBrush -> Control =
        line "M 11.2 2.9 A 1.4 1.4 0 0 1 13.1 4.8 L 5.6 12.3 L 2.6 13.4 L 3.7 10.4 Z M 10.2 4 L 12 5.8"

    let pin : IBrush -> Control =
        line "M 6.2 2.6 L 9.8 2.6 L 9.2 7 L 11.6 9 L 4.4 9 L 6.8 7 Z M 8 9 L 8 13.6"

    let archive : IBrush -> Control =
        line "M 2.6 4.2 L 13.4 4.2 L 13.4 6.4 L 2.6 6.4 Z M 3.8 6.4 L 3.8 12.6 A 0.8 0.8 0 0 0 4.6 13.4 L 11.4 13.4 A 0.8 0.8 0 0 0 12.2 12.6 L 12.2 6.4 M 6.6 9 L 9.4 9"

    let gear : IBrush -> Control =
        line "M 8 5.9 A 2.1 2.1 0 1 1 8 10.1 A 2.1 2.1 0 1 1 8 5.9 M 8 1.9 L 8 3.4 M 8 12.6 L 8 14.1 M 1.9 8 L 3.4 8 M 12.6 8 L 14.1 8 M 3.7 3.7 L 4.8 4.8 M 11.2 11.2 L 12.3 12.3 M 12.3 3.7 L 11.2 4.8 M 4.8 11.2 L 3.7 12.3"

    let sliders : IBrush -> Control =
        line "M 3 5 L 13 5 M 3 11 L 13 11 M 6.4 3.4 L 6.4 6.6 M 10 9.4 L 10 12.6"

    let sun : IBrush -> Control =
        line "M 8 6 A 2 2 0 1 1 8 10 A 2 2 0 1 1 8 6 M 8 2 L 8 3.4 M 8 12.6 L 8 14 M 2 8 L 3.4 8 M 12.6 8 L 14 8 M 3.8 3.8 L 4.8 4.8 M 11.2 11.2 L 12.2 12.2 M 12.2 3.8 L 11.2 4.8 M 4.8 11.2 L 3.8 12.2"

    let moon : IBrush -> Control = line "M 12.6 9.8 A 5.2 5.2 0 1 1 7 3.2 A 4.2 4.2 0 0 0 12.6 9.8 Z"

    let wrench : IBrush -> Control =
        line "M 9.4 3.2 A 3.2 3.2 0 0 1 12.8 6.6 L 8.2 11.2 A 3.2 3.2 0 0 1 4.8 7.8 Z M 4.8 7.8 L 2.6 10 A 1.6 1.6 0 0 0 5 12.4 L 7 10.4"

    let server : IBrush -> Control =
        line "M 3.4 3 L 12.6 3 A 0.8 0.8 0 0 1 13.4 3.8 L 13.4 6 A 0.8 0.8 0 0 1 12.6 6.8 L 3.4 6.8 A 0.8 0.8 0 0 1 2.6 6 L 2.6 3.8 A 0.8 0.8 0 0 1 3.4 3 Z M 3.4 9.2 L 12.6 9.2 A 0.8 0.8 0 0 1 13.4 10 L 13.4 12.2 A 0.8 0.8 0 0 1 12.6 13 L 3.4 13 A 0.8 0.8 0 0 1 2.6 12.2 L 2.6 10 A 0.8 0.8 0 0 1 3.4 9.2 Z M 5 4.9 L 5.02 4.9 M 5 11.1 L 5.02 11.1"

    let key : IBrush -> Control =
        line "M 10.4 2.8 A 3.2 3.2 0 1 1 7.4 7.6 L 6.6 8.4 L 5 8.4 L 5 10 L 3.4 10 L 3.4 11.6 L 2.4 12.6 L 2.4 10.6 L 7.4 5.6 A 3.2 3.2 0 0 1 10.4 2.8 Z"

    /// 软换行：两条文本行 + 一支绕回来的箭头
    let textWrap : IBrush -> Control =
        line
            "M 3 4.4 L 13 4.4 M 3 8.4 L 10.6 8.4 A 2.1 2.1 0 0 1 10.6 12.6 L 7.8 12.6 \
             M 9.3 11.1 L 7.8 12.6 L 9.3 14.1 M 3 12.6 L 5.4 12.6"

    let download : IBrush -> Control = line "M 8 2.8 L 8 10.4 M 4.8 7.2 L 8 10.4 L 11.2 7.2 M 3 13 L 13 13"

    let alert : IBrush -> Control =
        line "M 8 2.6 A 5.4 5.4 0 1 1 8 13.4 A 5.4 5.4 0 1 1 8 2.6 M 8 5.4 L 8 9 M 8 11.2 L 8.02 11.2"

    let info : IBrush -> Control =
        line "M 8 2.6 A 5.4 5.4 0 1 1 8 13.4 A 5.4 5.4 0 1 1 8 2.6 M 8 7.2 L 8 11 M 8 4.8 L 8.02 4.8"

    let panelLeft : IBrush -> Control =
        line "M 3.2 3 L 12.8 3 A 0.9 0.9 0 0 1 13.7 3.9 L 13.7 12.1 A 0.9 0.9 0 0 1 12.8 13 L 3.2 13 A 0.9 0.9 0 0 1 2.3 12.1 L 2.3 3.9 A 0.9 0.9 0 0 1 3.2 3 Z M 6.6 3 L 6.6 13"

    let message : IBrush -> Control =
        line "M 3.4 3 L 12.6 3 A 0.9 0.9 0 0 1 13.5 3.9 L 13.5 9.6 A 0.9 0.9 0 0 1 12.6 10.5 L 6.6 10.5 L 3.4 13.2 L 3.4 10.5 A 0.9 0.9 0 0 1 2.5 9.6 L 2.5 3.9 A 0.9 0.9 0 0 1 3.4 3 Z"

    let more : IBrush -> Control = line "M 4 8 L 4.02 8 M 8 8 L 8.02 8 M 12 8 L 12.02 8"

    let externalLink : IBrush -> Control =
        line "M 9.4 3 L 13 3 L 13 6.6 M 13 3 L 7.6 8.4 M 11.4 9.6 L 11.4 12.4 A 0.9 0.9 0 0 1 10.5 13.3 L 3.6 13.3 A 0.9 0.9 0 0 1 2.7 12.4 L 2.7 5.5 A 0.9 0.9 0 0 1 3.6 4.6 L 6.4 4.6"

    let file : IBrush -> Control =
        line "M 4.6 2.4 L 9.4 2.4 L 12.4 5.4 L 12.4 13 A 0.6 0.6 0 0 1 11.8 13.6 L 4.6 13.6 A 0.6 0.6 0 0 1 4 13 L 4 3 A 0.6 0.6 0 0 1 4.6 2.4 Z M 9.2 2.6 L 9.2 5.6 L 12.2 5.6"

    let image : IBrush -> Control =
        line "M 3.4 3.2 L 12.6 3.2 A 0.9 0.9 0 0 1 13.5 4.1 L 13.5 11.9 A 0.9 0.9 0 0 1 12.6 12.8 L 3.4 12.8 A 0.9 0.9 0 0 1 2.5 11.9 L 2.5 4.1 A 0.9 0.9 0 0 1 3.4 3.2 Z M 5.6 6.4 A 0.9 0.9 0 1 1 5.6 6.41 M 13.5 10.2 L 10.4 7.4 L 4.2 12.8"

    let clock : IBrush -> Control = line "M 8 2.8 A 5.2 5.2 0 1 1 8 13.2 A 5.2 5.2 0 1 1 8 2.8 M 8 5.4 L 8 8.2 L 10.2 9.6"

    let sparkle : IBrush -> Control =
        line "M 6.2 2.6 L 7.2 5.4 L 10 6.4 L 7.2 7.4 L 6.2 10.2 L 5.2 7.4 L 2.4 6.4 L 5.2 5.4 Z M 11.2 9.4 L 11.8 11 L 13.4 11.6 L 11.8 12.2 L 11.2 13.8 L 10.6 12.2 L 9 11.6 L 10.6 11 Z"

    let keyboard : IBrush -> Control =
        line "M 2.4 4.6 L 13.6 4.6 A 0.7 0.7 0 0 1 14.3 5.3 L 14.3 10.7 A 0.7 0.7 0 0 1 13.6 11.4 L 2.4 11.4 A 0.7 0.7 0 0 1 1.7 10.7 L 1.7 5.3 A 0.7 0.7 0 0 1 2.4 4.6 Z M 4.4 6.8 L 4.42 6.8 M 6.8 6.8 L 6.82 6.8 M 9.2 6.8 L 9.22 6.8 M 11.6 6.8 L 11.62 6.8 M 5.2 9.2 L 10.8 9.2"

    let logOut : IBrush -> Control =
        line "M 6.4 3 L 3.6 3 A 0.9 0.9 0 0 0 2.7 3.9 L 2.7 12.1 A 0.9 0.9 0 0 0 3.6 13 L 6.4 13 M 9.6 5.2 L 12.8 8 L 9.6 10.8 M 6 8 L 12.6 8"

    let database : IBrush -> Control =
        line "M 8 2.6 C 10.8 2.6 13 3.4 13 4.4 C 13 5.4 10.8 6.2 8 6.2 C 5.2 6.2 3 5.4 3 4.4 C 3 3.4 5.2 2.6 8 2.6 Z M 3 4.4 L 3 11.6 C 3 12.6 5.2 13.4 8 13.4 C 10.8 13.4 13 12.6 13 11.6 L 13 4.4 M 3 8 C 3 9 5.2 9.8 8 9.8 C 10.8 9.8 13 9 13 8"
