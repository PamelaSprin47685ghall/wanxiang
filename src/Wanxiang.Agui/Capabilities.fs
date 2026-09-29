namespace Wanxiang.Agui

/// AG-UI 协议版本与本端能力声明。
///
/// 线协议权威规范见 `wanxiang-protocol` 第 55 节；此处只固化可执行的常量，
/// 避免版本字符串散落在收发两端。
[<RequireQualifiedAccess>]
module Capabilities =

    /// 支持的 AG-UI 协议版本（带内协商：`RunAgentInput.protocolVersion` /
    /// `RUN_STARTED.protocolVersion`），不使用私有握手事件。
    [<Literal>]
    let ProtocolVersion = "1.0"

    /// 万象扩展命名空间前缀。所有非标准事件/字段走这里，
    /// 使标准客户端可合法忽略（AG-UI 对 `CUSTOM` 规定 `MUST ignore`）。
    [<Literal>]
    let Namespace = "wanxiang.dev/"

    /// 本端在标准面之外提供的扩展能力名（去掉命名空间前缀）。
    let extensions: string list =
        [ "observe"        // 只读订阅：游标增量观察
          "cursor"         // 全局提交序号游标
          "catch-up"       // 断线补齐
          "idempotent-send"// 幂等重发（commandId）
          "attachments"    // 附件分块传输
          "config-write"   // 配置热更新
          "snapshot" ]     // 会话快照
