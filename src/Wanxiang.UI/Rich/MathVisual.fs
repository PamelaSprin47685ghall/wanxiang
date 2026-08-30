namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Media.TextFormatting
open Avalonia.VisualTree

/// 画一条公式的控件。
///
/// 排版结果由 `MathLayout` 一次算好，之后只是把图元按基线摆出来；
/// 画笔取自设计令牌的可变实例，切主题时公式跟着变色，不必重建。
/// `lineHeight` 是所在段落的行高（0 = 未设置）。它决定基线在行盒里的位置，
/// 因而决定「基线到行底」的距离——只看字体度量会差出一两个像素。
type MathVisual(tex: string, displayMode: bool, fontSize: float, lineHeight: float) =
    inherit Control()

    let box =
        lazy
            (RichBackend.current ()
             |> Option.bind (fun backend -> backend.MathHtml(tex, displayMode))
             |> Option.bind (MathLayout.render Tokens.text fontSize))

    member _.HasLayout = box.Value.IsSome

    /// 公式基线到控件顶缘的距离。行内排版要靠它把公式基线对到文字基线上。
    /// 公式基线到控件顶缘的距离。行内时以父级文本块的真实基线为准。
    member this.Baseline =
        let _, _, ownBaseline = this.Metrics
        if displayMode then ownBaseline
        else
            match this.HostBaseline with
            | Some hostBaseline -> hostBaseline - this.Bounds.Y
            | None -> ownBaseline

    /// 正文字体在同一字号下的下沉量。
    ///
    /// 行内排版用 `BaselineAlignment.Bottom`，Avalonia 把控件**底缘**对到行的
    /// 文字底缘。要让两条基线重合，控件底缘到基线的距离就必须等于正文的下沉量——
    /// 用公式自己的下沉量会让公式整体偏高（实测 3.37pt）。
    /// 所在段落文本块的**真实**首行基线（相对文本块顶缘）。
    ///
    /// 为什么必须问父级、而不是自己算：控件高度会反过来撑大行盒，
    /// 拿任何「预估行盒」去补偿都是循环——独立量出的行度量与 TextBlock
    /// 合成后的行盒并不一致（五种算法试下来落差在 3.3～7.4pt 之间跳）。
    /// 而绘制偏移不参与布局，问到多少就用多少，一次收敛。
    member private this.HostBaseline: float option =
        let rec climb (visual: Visual) =
            match visual with
            | null -> None
            | :? TextBlock as host ->
                match host.TextLayout with
                | null -> None
                | layout -> layout.TextLines |> Seq.tryHead |> Option.map (fun line -> line.Baseline)
            | other -> climb (other.GetVisualParent())
        climb (this.GetVisualParent())

    /// 盒宽、盒高、盒内基线位置。盒子只按公式自身的墨迹范围算，
    /// 与段落无关——对齐由绘制偏移负责，布局这一层保持简单。
    member private _.Metrics =
        match box.Value with
        | None -> 0.0, 0.0, 0.0
        | Some laid -> laid.width, laid.above + laid.below, laid.above

    override this.MeasureOverride(_) =
        let width, height, _ = this.Metrics
        Size(width, height)

    override this.Render(context) =
        match box.Value with
        | Some laid ->
            // 图元坐标以基线为原点。行内模式把基线对到父级文本块的真实基线上；
            // 问不到（不在文本块里、或还没排版）就退回自身的 above。
            let _, _, ownBaseline = this.Metrics
            let drawBaseline =
                if displayMode then ownBaseline
                else
                    match this.HostBaseline with
                    | Some hostBaseline -> hostBaseline - this.Bounds.Y
                    | None -> ownBaseline
            use _ = context.PushTransform(Matrix.CreateTranslation(0.0, drawBaseline))
            for item in laid.items do
                match item with
                | MathGlyph(_, shaped, _, topLeft) -> context.DrawText(shaped, topLeft)
                | MathRule rect -> context.FillRectangle(Tokens.text, rect)
                | MathPath(geometry, transform, clip) ->
                    use _ = context.PushClip clip
                    use _ = context.PushTransform transform
                    context.DrawGeometry(Tokens.text, null, geometry)
        | None -> ()


/// 公式的对外入口：能排就给控件，排不出来就让调用方退化。
module MathRender =

    /// 行内公式。字号与行高都跟随所在段落——行高影响基线对齐。
    let tryInline (fontSize: float) (lineHeight: float) (tex: string) : Control option =
        let visual = MathVisual(tex, false, fontSize, lineHeight)
        if visual.HasLayout then Some(visual :> Control) else None

    /// 行内公式的内联容器。对齐方式必须显式指定：
    /// 默认值随主题模板变化，而基线对齐是可见的正确性问题。
    let inlineContainer (control: Control) =
        let container = Documents.InlineUIContainer control
        container.BaselineAlignment <- BaselineAlignment.Bottom
        container

    /// 展示式公式：独占一行、水平居中，上下留出呼吸。
    let tryBlock (fontSize: float) (tex: string) : Control option =
        let visual = MathVisual(tex, true, fontSize, 0.0)
        if not visual.HasLayout then None
        else
            visual.HorizontalAlignment <- Layout.HorizontalAlignment.Center
            let host =
                Border(
                    Child = visual,
                    Padding = Thickness(0.0, Tokens.space2),
                    HorizontalAlignment = Layout.HorizontalAlignment.Stretch)
            Some(host :> Control)
