namespace Wanxiang.UI

open System
open Avalonia.Controls
open Wanxiang.Core
open Wanxiang2.MarkdownView
open Wanxiang2.MessageCardView

/// 消息卡的宿主一侧（旧 Views/MessageCard.fs 的模块级状态）：整个应用一份 MessageCards，
/// 着色、公式、折行沿用 Reading；流式光标的呼吸时钟与触屏选中跨卡片重建持久。
/// 控件、外观与行为归 Wanxiang2.MessageCardView。
module Cards =

    /// 流式光标呼吸原点（旧 breathOriginTicks / breathRunLive）
    let clock = CaretClock()

    /// 触屏选中（整个列表一份）
    let touch = CardTouch()

    let private cards =
        lazy
            // 正文触屏收起悬停提示的类处理器随 Reading 的外观一起装上
            Reading.look.Force() |> ignore
            MessageCards(
                CardLooks.ofTheme SharedTheme.manager SharedTheme.styles,
                { Tint = Reading.tint
                  MathHtml = Reading.mathHtml
                  Wrap = Reading.wrap
                  Clock = clock
                  Touch = touch
                  Now = fun () -> DateTimeOffset.Now })

    let instance () = cards.Force()

    /// 卡外层高亮承载面的名字（跳转闪烁找它）
    let highlightName = MessageCards.HighlightName

    let renderWithStream (message: MessageView) (ctx: CardContext) (actions: CardActions) (index: int option) : Control * MarkdownStream option =
        cards.Force().Render(message, ctx, actions, index)

    let render message ctx actions index = fst (renderWithStream message ctx actions index)

    let errorCard (error: GenerationError) (onRetry: unit -> unit) (actions: CardActions) (onContinue: (unit -> unit) option) : Control =
        cards.Force().ErrorCard(error, onRetry, actions, onContinue)

    let incompleteMarker () : Control = cards.Force().IncompleteMarker()

    /// 没有任何出口的动作（测试与尚未接线时）
    let noActions: CardActions =
        { CopyText = ignore
          Regenerate = ignore
          EditAndFork = ignore
          DeleteMessage = ignore
          DownloadAttachment = ignore
          OpenLink = ignore
          CopyLink = None
          Recover = None }
