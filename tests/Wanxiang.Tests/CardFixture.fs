namespace Wanxiang.Tests

open System
open Avalonia.Controls
open Wanxiang.UI
open Wanxiang2.CodeTint
open Wanxiang2.MarkdownView
open Wanxiang2.MessageCardView

/// 测试夹具：旧宿主 `Cards` 模块的兼容壳。
///
/// 宿主旧 `Cards.render` / `Cards.noActions` 已随 MessageCard.fs 迁入 wanxiang2 的
/// `MessageCards` 实例（`MessageCards(looks, host)`，`Render` 返回 `Control * MarkdownStream option`）。
/// 本壳用真实主题（SharedTheme.manager / SharedTheme.styles）构造 MessageCards，
/// 让旧测试调用点 `Cards.render message ctx actions (Some n)` 原样可用，返回 `Control`。
/// 不复制渲染、缓存、滚动或菜单规则——只做签名适配。
module Cards =

    /// 无着色、公式查表为空的后端（与 wanxiang2 MessageCardView/tests/Fixture.fs 同款）
    let private noTint () =
        CodeTint
            { HighlightHtml = (fun _ -> None)
              Background = (fun f -> f ())
              After = (fun _ f -> f ())
              Synchronous = true }

    /// 惰性构造一次 MessageCards 实例（SharedTheme.manager 是进程级单例，构造一次即可）
    let private instance =
        lazy
            (let host: CardHost =
                { Tint = noTint ()
                  MathHtml = (fun _ -> None)
                  Wrap = CodeWrapMemory false
                  Clock = CaretClock()
                  Touch = CardTouch()
                  Now = fun () -> DateTimeOffset.UtcNow }
             MessageCards(CardLooks.ofTheme SharedTheme.manager SharedTheme.styles, host))

    /// 渲染一条消息（旧 Cards.render）：返回卡控件本身（流式更新入口弃用）
    let render (message: MessageView) (ctx: CardContext) (actions: CardActions) (index: int option) : Control =
        instance.Value.Render(message, ctx, actions, index) |> fst

    /// 空动作集（旧 Cards.noActions）
    let noActions : CardActions =
        { CopyText = ignore
          Regenerate = ignore
          EditAndFork = ignore
          DeleteMessage = ignore
          DownloadAttachment = ignore
          OpenLink = ignore
          CopyLink = None
          Recover = None }