namespace Wanxiang.UI

/// 末尾用户消息久无回复时补出的失败卡（决策 263）。文案在 Wanxiang2.MessageView 的 ReplyTail。
module UnansweredReply =

    let error : Wanxiang.Core.GenerationError =
        { Wanxiang.Core.GenerationError.create Wanxiang.Core.GenerationErrorKind.UnknownFailure ReplyTail.unansweredMessage with
            detail = Some ReplyTail.unansweredDetail }
