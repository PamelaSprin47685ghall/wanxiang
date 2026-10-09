module Wanxiang.Tests.ContractTests

open System
open Xunit
open Wanxiang.Agui
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Core.Ledger
open Wanxiang.Store

/// P-001：版本权威表钉死测试。
/// 任一版本常量被改动，这里立即红——版本权威不是注释，是可执行断言。
module VersionAuthority =

    [<Fact>]
    let ``提交外壳版本常量与生产编解码一致`` () =
        // 生产路径（Replay/JsonSeqWriter/CommitCoordinator）只认 CommitCodec + Constants.FormatVersion
        Assert.Equal(1, Constants.FormatVersion)
        // 编码入口产出外壳版本 = 常量
        let commit = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = Guid.NewGuid(); title = "v"; config = SessionConfig.empty } ]
        let line = CommitCodec.commitToJsonLine commit
        Assert.Contains(sprintf "\"formatVersion\":%d" Constants.FormatVersion, line)
        // 解码入口只接受常量版本
        let parsed = CommitCodec.tryCommitFromJsonLine line
        Assert.True parsed.IsSome
        Assert.Equal(Constants.FormatVersion, parsed.Value.formatVersion)

    [<Fact>]
    let ``Ledger.Record 是同一逻辑格式的重复定义（结论 C），版本不同步即红`` () =
        // 两个外壳字段语义一一对应，但版本号与字段名不同——这是 P-001 记录的重复定义。
        // 本断言钉住现状：任何一边漂移都会让「差异」章节失实。
        Assert.Equal(2, Record.FormatVersion)
        Assert.NotEqual(Constants.FormatVersion, Record.FormatVersion)

    [<Fact>]
    let ``配置版本常量唯一且 TomlCodec 拒绝未知版本`` () =
        Assert.Equal(2, AppConfig.CurrentVersion)
        // v1/v2 显式识别；未知版本必须拒绝（不猜格式）
        match TomlCodec.tryParse "configVersion = 3\n" with
        | Ok _ -> failwith "unknown config version must be rejected"
        | Error errs -> Assert.Contains("not supported", String.concat "; " errs)

    [<Fact>]
    let ``AG-UI 版本常量`` () =
        Assert.Equal("1.0", Capabilities.ProtocolVersion)

    [<Fact>]
    let ``协议版本常量与客户端 Hello 载荷一致`` () =
        // 客户端仍发送 Hello{version=Constants.ProtocolVersion}；服务端不校验（in-band）。
        // 见 docs/adr/ADR-001-wire-version-negotiation.md。本断言钉住常量本身。
        Assert.Equal(1, Constants.ProtocolVersion)

    [<Fact>]
    let ``大整数必须以 JSON 字符串承载（RFC 8785 附录 D）`` () =
        let commit = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = Guid.NewGuid(); title = "v"; config = SessionConfig.empty } ]
        let line = CommitCodec.commitToJsonLine commit
        // id 是字符串（"id":"1"），不是数字
        Assert.Matches("\"id\":\"1\"", line)

    [<Fact>]
    let ``事件级版本常量`` () =
        Assert.Equal(1, Constants.EventVersion1)
