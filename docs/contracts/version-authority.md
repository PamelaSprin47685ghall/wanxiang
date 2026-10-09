# 版本权威表（P-001）

> 本表回答「每个格式的版本常量在哪里定义、是否仍在使用、属于哪种结论」。
> 结论只允许三种：**A 独立格式保留 / B 旧格式迁移后删除 / C 同一格式重复定义需合并**。
> 禁止「暂时都留着」。

| 对象 | 当前版本 | 权威代码位置 | 生产路径 | 结论 |
|---|---|---|---|---|
| Event Commit（落盘提交外壳） | 1 | `Wanxiang.Core/Types.fs` → `Constants.FormatVersion = 1`；编码 `CommitCodec.commitToJsonLine` / 解码 `CommitCodec.tryCommitFromJsonLine`（`Wanxiang.Core/CommitCodec.fs`） | 是（`Replay` / `JsonSeqWriter` / `CommitCoordinator` 全部走 `CommitCodec`） | **A 独立格式**。字段：`formatVersion`(number)、`id`(string)、`bootId`(string)、`source`、`committedAtUtc`(RFC3339)、`commandId/Type/Hash`(可选)、`events`(array)。 |
| Ledger Record（账本外壳） | ~~2~~ | **已删除（P-002，2026-10-10）**：`Ledger/Record.fs`、`Ledger/Recovery.fs` 从生产与测试中移除；`EventLogTests.fs` 已迁移到生产 `CommitCodec` + `Frame` + `Jcs`。 | 否（曾是死代码，仅测试消费） | **C 已合并**。结论 C 执行完毕：唯一外壳版本 = `Constants.FormatVersion = 1`（CommitCodec 编码/解码同一常量，R3 满足）。 |
| Config | 2 | `Wanxiang.Config/ConfigModel.fs` → `AppConfig.CurrentVersion = 2`；`TomlCodec.tryParse` 显式识别 v1/v2 并拒绝未知版本（`Wanxiang.Config/TomlCodec.fs`） | 是 | **A 独立格式**。v1→v2 迁移路径在 `TomlCodec` 内。 |
| Wanxiang Wire（语义协议版本） | 1 | `Wanxiang.Core/Types.fs` → `Constants.ProtocolVersion = 1` | 否（仅诊断展示 `SettingsGeneral.fs`；客户端已不发送 Hello，ADR-001 方案 B） | **A 独立格式**（诊断展示用；线上版本协商 in-band = AG-UI `protocolVersion`）。见 ADR-001。 |
| AG-UI | 1.0 | `Wanxiang.Agui/Capabilities.fs` → `Capabilities.ProtocolVersion = "1.0"` | 是（`WireAgui` 承载；`RUN_STARTED.protocolVersion` 带内声明） | **A 独立格式**（外部标准，只读）。 |
| MCP | 2025-06-18 | `Wanxiang.Server/McpTransport.fs` → `McpJson.initializeParams` 硬编码 `"2025-06-18"` | 是 | **A 独立格式**（外部标准）。版本字符串目前是局部字面量，建议提升为 `McpJson.ProtocolVersion` 常量（单一权威，P-002 范围）。 |

## 差异：生产 Commit 外壳 vs 文档/测试 Record 外壳（已收敛）

P-002（2026-10-10）执行结论 C：以生产 `CommitCodec` 形态为唯一权威
（`id`、`committedAtUtc`、`formatVersion=1`），删除 `Ledger.Record`/`Ledger.Recovery`
重复实现。`wanxiang-store` 第 47 节与 `wanxiang-agui-migration` 中描述 Record 形态的
样例已不再对应任何代码；后续文档修订以 Commit 形态为准。

落盘格式唯一形态：

```json
RS{"formatVersion":1,"id":"1843","bootId":"…","source":"wanxiang","committedAtUtc":"…","events":[…]}
```

## 约束

- `formatVersion` 只接受**等于现值**；未知版本**拒绝启动写服务、保留数据**（`Replay.inspect` → `UnsupportedFormat`）。
- 事件级 `version`（`Constants.EventVersion1 = 1`）语义永久固定，不参与外壳版本。
- 大整数（`id`/`commitId`/`messageCommitId`/`forkAfterId`）**MUST** JSON 字符串承载（RFC 8785 附录 D）。
- 本表由 `tests/Wanxiang.Tests/ContractTests.fs` 的断言钉死：任一常量被改，测试立即红。
