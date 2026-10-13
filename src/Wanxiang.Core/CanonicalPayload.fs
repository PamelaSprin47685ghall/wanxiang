namespace Wanxiang.Core

/// commandId 的幂等规则归主机：线协议只交出业务载荷，这里做 RFC 8785 JCS。
/// 载荷不含裸大整数（commitId 一律字符串承载），此约束由 CodecTests 钉住。
module CanonicalPayload =
    let ofCommand (cmd: ClientCommand) : string =
        Canonical.Jcs.text ((ClientCommand.businessPayload cmd).ToJsonString())
