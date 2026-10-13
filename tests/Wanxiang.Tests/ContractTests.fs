module Wanxiang.Tests.ContractTests

open System
open System.IO
open Xunit
open Wanxiang.Agui
open Wanxiang.Config
open Wanxiang.Core
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
    let ``P-002：Ledger.Record 重复定义已删除，格式唯一权威是 CommitCodec + Constants`` () =
        // 结论 C 已执行：Record.fs / Recovery.fs 从生产与测试中删除。
        // 唯一外壳版本 = Constants.FormatVersion（CommitCodec 编码/解码同一常量）。
        Assert.Equal(1, Constants.FormatVersion)
        // 编码入口与解码入口读同一常量（R3：一个版本常量、一个编码入口、一个解码入口）
        let commit = Events.Commit.create 1UL DateTimeOffset.UtcNow [ ConversationCreated { conversationId = Guid.NewGuid(); title = "v"; config = SessionConfig.empty } ]
        let line = CommitCodec.commitToJsonLine commit
        Assert.Contains(sprintf "\"formatVersion\":%d" Constants.FormatVersion, line)
        let parsed = CommitCodec.tryCommitFromJsonLine line
        Assert.True parsed.IsSome
        Assert.Equal(Constants.FormatVersion, parsed.Value.formatVersion)

    [<Fact>]
    let ``配置版本常量唯一且 TomlCodec 拒绝未知版本`` () =
        Assert.Equal(2, AppConfig.CurrentVersion)
        // v1/v2/v3 显式识别（3 = 2 + 全局默认模型，见 wanxiang-config 决策 60）；未知版本必须拒绝（不猜格式）
        Assert.Equal(3, AppConfig.GlobalDefaultVersion)
        // 4 = 3 + 对话预设（决策 431）
        Assert.Equal(4, AppConfig.PresetsVersion)
        match TomlCodec.tryParse "configVersion = 5\n" with
        | Ok _ -> failwith "unknown config version must be rejected"
        | Error errs -> Assert.Contains("not supported", String.concat "; " errs)

    [<Fact>]
    let ``AG-UI 版本常量`` () =
        Assert.Equal("1.0", Capabilities.ProtocolVersion)

    [<Fact>]
    let ``MCP 协议版本常量（单一权威）`` () =
        Assert.Equal("2025-06-18", Wanxiang.Server.McpJson.ProtocolVersion)
        // initializeParams 必须携带同一常量（P-002：一个版本只定义一次）
        let p = Wanxiang.Server.McpJson.initializeParams ()
        Assert.Equal("2025-06-18", p["protocolVersion"].GetValue<string>())

    [<Fact>]
    let ``协议版本常量（诊断展示用；线上版本 in-band）`` () =
        // ADR-001 方案 B：客户端不再发送 Hello，线上版本协商 in-band（AG-UI protocolVersion）。
        // Constants.ProtocolVersion 仅用于诊断展示（SettingsGeneral）。本断言钉住常量本身。
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

    /// 场景 F（N-1）：未来版本数据 → 明确报告 unsupported format，不修改任何文件。
    [<Fact>]
    let ``未来 formatVersion 触发 UnsupportedFormat 且不修改文件`` () =
        let dir = Wanxiang.Tests.Helpers.tempDir ()
        try
            Wanxiang.Store.DataPaths.ensureDataDirs dir
            let day = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let path = Wanxiang.Store.DataPaths.logFile dir
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            // 未来版本外壳（formatVersion=99），结构完整
            // 注意：三引号字符串是 raw（\u 不转义）；用普通字符串 + 显式 RS/LF 字节
            let future =
                "\u001e"
                + """{"v":99,"id":"1","at":"2026-08-01T00:00:00.0000000+00:00","events":[]}"""
                + "\u000a"
            File.WriteAllText(path, future)
            let bytesBefore = File.ReadAllBytes path
            match Wanxiang.Store.Replay.inspect dir with
            | Wanxiang.Store.UnsupportedFormat msg ->
                Assert.Contains("99", msg)
                Assert.Contains("newer version", msg)
            | other -> failwithf "expected UnsupportedFormat, got %A" other
            // 文件未被修改（R2 / 场景 F：不修改任何文件）
            Assert.Equal<byte[]>(bytesBefore, File.ReadAllBytes path)
        finally
            Wanxiang.Tests.Helpers.cleanup dir
