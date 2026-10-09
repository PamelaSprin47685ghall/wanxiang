# 版本权威表（P-001）

> 本表回答「每个格式的版本常量在哪里定义、是否仍在使用、属于哪种结论」。
> 结论只允许三种：**A 独立格式保留 / B 旧格式迁移后删除 / C 同一格式重复定义需合并**。
> 禁止「暂时都留着」。

| 对象 | 当前版本 | 权威代码位置 | 生产路径 | 结论 |
|---|---:|---|---|---|
| Event Commit（落盘提交外壳） | 1 | `Wanxiang.Core/Types.fs` → `Constants.FormatVersion = 1`；编码 `CommitCodec.commitToJsonLine` / 解码 `CommitCodec.tryCommitFromJsonLine`（`Wanxiang.Core/CommitCodec.fs`） | 是（`Replay` / `JsonSeqWriter` / `CommitCoordinator` 全部走 `CommitCodec`） | **A 独立格式**。字段：`formatVersion`(number)、`id`(string)、`bootId`(string)、`source`、`committedAtUtc`(RFC3339)、`commandId/Type/Hash`(可选)、`events`(array)。 |
| Ledger Record（账本外壳） | 2 | `Wanxiang.Core/Ledger/Record.fs` → `Record.FormatVersion = 2`；编码 `Record.toJson` / 解码 `Record.tryFromJson` | **否**（仅 `tests/Wanxiang.Tests/EventLogTests.fs` 消费） | **C 同一格式的重复定义（需合并）**。字段：`formatVersion`(number)、`commitId`(string)、`bootId`、`source`、`at`(RFC3339)、`commandId/Type/Hash`、`events`。与 Commit 外壳同构，仅字段改名（`id`→`commitId`、`committedAtUtc`→`at`）。**待 S+P 联合批准后删除或统一**；删除前保留（`wanxiang-store` 第 47 节的「现行决议」描述的是 Record 形态，而生产实现是 Commit 形态——两者必须收敛，见下方「差异」）。 |
| Config | 2 | `Wanxiang.Config/ConfigModel.fs` → `AppConfig.CurrentVersion = 2`；`TomlCodec.tryParse` 显式识别 v1/v2 并拒绝未知版本（`Wanxiang.Config/TomlCodec.fs`） | 是 | **A 独立格式**。v1→v2 迁移路径在 `TomlCodec` 内。 |
| Wanxiang Wire（语义协议版本） | 1 | `Wanxiang.Core/Types.fs` → `Constants.ProtocolVersion = 1` | 是（仅诊断展示 `SettingsGeneral.fs` 与客户端 Hello 载荷；**线上版本已改 in-band**，见 ADR） | **A/B 过渡**：`Constants.ProtocolVersion` 仍是客户端 `Hello` 事件携带的整数版本，但服务端已不校验（版本协商 in-band）。见 `docs/adr/ADR-001-wire-version-negotiation.md` 的收敛方案。 |
| AG-UI | 1.0 | `Wanxiang.Agui/Capabilities.fs` → `Capabilities.ProtocolVersion = "1.0"` | 是（`WireAgui` 承载；`RUN_STARTED.protocolVersion` 带内声明） | **A 独立格式**（外部标准，只读）。 |
| MCP | 2025-06-18 | `Wanxiang.Server/McpTransport.fs` → `McpJson.initializeParams` 硬编码 `"2025-06-18"` | 是 | **A 独立格式**（外部标准）。版本字符串目前是局部字面量，建议提升为 `McpJson.ProtocolVersion` 常量（单一权威，P-002 范围）。 |

## 差异：生产 Commit 外壳 vs 文档/测试 Record 外壳

`wanxiang-store` 第 47 节（落盘格式唯一权威）描述的是 **Record 形态**：

```json
RS{"formatVersion":2,"commitId":"1843","bootId":"…","source":"wanxiang","at":"…","events":[…]}
```

而生产实现（`CommitCodec` + `JsonSeqWriter`）实际写的是：

```json
RS{"formatVersion":1,"id":"1","bootId":"00000000-…","source":"wanxiang","committedAtUtc":"…","events":[…]}
```

两者是**同一逻辑格式的两份实现**：字段语义一一对应（`id`≡`commitId`、`committedAtUtc`≡`at`），
版本号不同、字段名不同。这是「同一格式的重复定义」（结论 C）。

**收敛方向（待 S+P 联合批准，不在本 PR 执行）**：

- 方案 1（推荐，最小破坏）：以生产实现为权威，把 `wanxiang-store` 第 47 节与
  `wanxiang-agui-migration` 的样例改为 Commit 形态；`Ledger.Record`/`Recovery` 保留为
  「账本内核参考实现」，但**不得**再被生产路径引用；后续删除。
- 方案 2（对齐文档）：生产 `CommitCodec` 改为产出 Record 形态（`commitId`/`at`、`formatVersion=2`），
  需要迁移既有 `.jsonseq`（违反「不迁移旧数据」拍板，不推荐）。
- 方案 3：把 `CommitCodec` 与 `Record` 合并为一个编解码入口（R3：一个编码入口、一个解码入口），
  字段名以生产为权威。这是 P-002「一个版本只定义一次」的最终目标。

## 约束

- `formatVersion` 只接受**等于现值**；未知版本**拒绝启动写服务、保留数据**（`Replay.inspect` → `UnsupportedFormat`）。
- 事件级 `version`（`Constants.EventVersion1 = 1`）语义永久固定，不参与外壳版本。
- 大整数（`id`/`commitId`/`messageCommitId`/`forkAfterId`）**MUST** JSON 字符串承载（RFC 8785 附录 D）。
- 本表由 `tests/Wanxiang.Tests/ContractTests.fs` 的断言钉死：任一常量被改，测试立即红。
