---
name: wanxiang-concurrency-plan
description: 【临时·迁移完成后删除】万象 AG-UI 改造的并发开发步骤指南：工作流切分、契约冻结、worktree 隔离、合并顺序、冲突热点。仅迁移期间有效。
---

# ⚠️ 临时 skill · AG-UI 改造并发开发指南

> ## 🗑️ 本 skill 是临时的
>
> **适用期**：AG-UI 改造期间（自 2026-09-29 起）。
> **删除条件**：改造全部合入 `main` 且 §8 验收全绿之后**立即删除**。
> **删除动作**（照做，勿漏）：
> 1. `rm -rf .agents/skills/wanxiang-concurrency-plan/`
> 2. 从 `.agents/skills/wanxiang/SKILL.md` 的 skill 索引表里**删掉本 skill 那一行**
> 3. 检查无残留引用：`grep -rn "wanxiang-concurrency-plan" . --exclude-dir=.git`
>
> 保留它会变成「已过期但仍在被读的规范」——那是比没有更糟的状态。

**配套 skill**：格式规范见 `wanxiang-protocol` 第 55 节 + `wanxiang-store` 第 47 节；
实施代码与陷阱见 `wanxiang-agui-migration`。本 skill 只管**并发开发怎么不打架**。

---

## 1. 依赖分析（谁必须等谁）

```
Wave 0（单owner·串行·必做）
  建好所有工程骨架 + 包引用 + 全部 slnx/fsproj 登记
        │
        ├──────────────┬──────────────┐
        ▼              ▼              ▼
Wave 1  A 账本内核    B AG-UI语义层   (无第三项)
        │              │
        │              ├──────────────┐
        ▼              ▼              ▼
Wave 2  E 落盘切换    C 服务端线协议  D 客户端/UI
        │              │              │
        └──────────────┴──────┬───────┘
                              ▼
Wave 3                 F 测试重做（e2e）
```

| 流 | 范围 | 依赖 | 可并行于 |
|---|---|---|---|
| **0** | 工程骨架、包引用、slnx/fsproj 登记 | 无 | **不可并行（必须串行先做完）** |
| **A** | `Core/Ledger/*`（Jcs/Frame/Record/Recovery） | 0 | B |
| **B** | `Wanxiang.Agui/*`（MessageMap/TolerantReader/Envelope/Outbound/Capabilities） | 0 | A |
| **C** | `Wanxiang.Server` 收发边界 + e2e 服务端侧 | B | D、E |
| **D** | `Wanxiang.Client` + `Wanxiang.UI/AppShell` | B | C、E |
| **E** | `CommitCodec`/`CanonicalJson`/`CommandId`/文件名/doctor | A | C、D |
| **F** | 测试重做、互操作验收、端到端 | C、D、E | — |

---

## 2. ⭐ Wave 0：契约冻结（**这是整个方案能否并发的关键**）

> **为什么必须先做**：`Wanxiang.slnx` 与 `tests/Wanxiang.Tests/Wanxiang.Tests.fsproj` **每个流都要改**。
> 若并发进行，这两处必然冲突且难合。**把它们提前一次性做完，后续流就不碰它们。**

### 2.1 单一 owner 完成（**不许并行**）

1. 加包引用：`Jcs.Net` 0.1.1、`AGUI.Abstractions` 1.0.0、`AGUI.Server` 1.0.0
2. 建 `src/Wanxiang.Core/Ledger/` 与 `src/Wanxiang.Agui/`
3. 建**空文件骨架**（每个文件只放 `namespace` + 空 `module`，保证可编译）
4. 在 `src/Wanxiang.slnx` 登记 `Wanxiang.Agui`
5. 在各自的 fsproj 里**一次性登记全部** `<Compile Include>`（含尚未写的文件）
6. 在 `tests/Wanxiang.Tests/Wanxiang.Tests.fsproj` 登记 `EventLogTests.fs`、`AguiMappingTests.fs`

### 2.2 冻结的接口契约（已落地，见各自源文件）

> **现状**：Wave 0 起这批签名已用**实现**落进仓库（不是空骨架），
> 各流按此实现，**不得单方修改**。模块均带 `[<RequireQualifiedAccess>]`。

```fsharp
// ── 流 A 提供：src/Wanxiang.Core/Ledger/*.fs（namespace Wanxiang.Core.Ledger）──
module Jcs =                     // Ledger/Jcs.fs
    val canonicalizeUtf8 : string -> byte[]
    val canonicalize : string -> string
    val sha256Hex : string -> string
    val trySha256Hex : string -> string option

module Frame =                   // Ledger/Frame.fs
    val RS : byte                // 0x1E
    val LF : byte                // 0x0A
    val encode : string -> byte[]
    val split : ReadOnlySpan<byte> -> (byte[] * bool) list

type LedgerRecord = {            // Ledger/Record.fs
    formatVersion: int; commitId: uint64; bootId: Guid; source: string
    at: DateTimeOffset
    commandId: string option; commandType: string option; commandHash: string option
    events: System.Text.Json.Nodes.JsonArray   // 注意是 Array，不是 Object
}
module Record =
    val FormatVersion : int      // = 2
    val toJson : LedgerRecord -> string
    val tryFromJson : string -> Result<LedgerRecord, string>

type RecoveryOutcome = {         // Ledger/Recovery.fs
    records: LedgerRecord list
    truncatedBytes: int; skippedSegments: int; warnings: string list
}
module Recovery =
    val recover : byte[] -> RecoveryOutcome

// ── 流 B 提供：src/Wanxiang.Agui/*.fs（namespace Wanxiang.Agui）──
// 工程只依赖 AGUI.Abstractions + Core + Protocol（不引入未用包）。
module Capabilities =            // Capabilities.fs
    val ProtocolVersion : string // "1.0"
    val Namespace : string       // "wanxiang.dev/"
    val extensions : string list

type ParsedEvent =               // TolerantReader.fs
    | Known of AGUI.Abstractions.BaseEvent
    | Unrecognized of typeName: string
    | Malformed of reason: string
    | Custom of name: string * value: System.Text.Json.Nodes.JsonNode
module TolerantReader =
    val parse : string -> ParsedEvent
```

**消息模型映射（MAF ⇄ AG-UI）放哪**：`Wanxiang.Agui` **不**依赖 `Microsoft.Extensions.AI`；
MAF `ChatMessage` ⇄ AG-UI 消息对象的映射属于 **Agent 层**（`Wanxiang.Agent` 已持有 MAF）。
流 B 不再提供 `MessageMap`——它在流 D 范围内的 `Wanxiang.Agent` 中。
（此条修订了原计划把 MessageMap 放进 Agui 工程的设想：那会为了一个映射函数把
`Microsoft.Extensions.AI` 拖进线协议工程，属于不必要的重量。）

**冻结规则**：
- 任何流需要改契约 → **必须**先通知其余流并同步更新本节，**不许**各自改签名；
- 契约以**本节**为准，不以任何分支的现状为准。

---

## 3. 隔离：每个流一个 git worktree

```bash
cd <产品仓根>
git worktree add ../wt-agui-A agui/A
git worktree add ../wt-agui-B agui/B
git worktree add ../wt-agui-C agui/C
git worktree add ../wt-agui-D agui/D
git worktree add ../wt-agui-E agui/E
```

> 命名约定：`../wt-agui-<流字母>`，分支 `agui/<流字母>`。
> **不要**在同一个工作目录里并发改同一份文件——那是冲突的根源。

**看板核对**（随时可跑）：

```bash
git worktree list
for b in A B C D E; do printf "%s: " $b; git log --oneline -1 agui/$b 2>/dev/null || echo "(未开始)"; done
```

---

## 4. 各流的完成判据（**独立可验，不依赖其他流**）

| 流 | 完成判据（在**自己的 worktree** 里就能跑通） |
|---|---|
| **A** | `dotnet build src/Wanxiang.Core` 过；`EventLogTests` 全绿（含 §10 的 5 个用例） |
| **B** | `dotnet build src/Wanxiang.Agui` 过；`AguiMappingTests` 全绿（含**未知 type 不抛异常**护栏） |
| **C** | `dotnet build src/Wanxiang.Server` 过；发未知 type 事件**不断连**（实机验证，非推断） |
| **D** | `dotnet build` 过；客户端能连上并渲染（实机跑一次） |
| **E** | 落盘读写往返过；`doctor` 能识别旧 `.ndjson` 并**明确报错** |
| **F** | §8 的 7 条验收全过（含官方 `AGUIChatClient` 互操作） |

**禁止**：靠「其他流还没合，先临时改一下契约」来让自己编译通过——那是把冲突推给别人。

---

## 5. 合并顺序（**严格按下图，不得乱序**）

```
A ──┐
    ├─► E ──┐
B ──┼─► C ──┼─► F ──► main
    └─► D ──┘
```

1. **先合 A、B**（无依赖，二者之间也无冲突：分属不同目录）
2. **再合 E**（依赖 A）、**C 与 D**（依赖 B）——三者之间无文件重叠，可连续合
3. **最后合 F**
4. 每次合并前：`dotnet build src/Wanxiang.slnx -c Debug` + `dotnet test` 必须全绿

---

## 6. 冲突热点（**已知会撞的地方，提前分派归属**）

| 位置 | 谁会碰 | 处置 |
|---|---|---|
| `src/Wanxiang.slnx` | A、B | **Wave 0 一次性做完**，之后谁都不许改 |
| `tests/Wanxiang.Tests/Wanxiang.Tests.fsproj` | 全部（各自加测试文件） | **Wave 0 一次性登记全部**，之后谁都不许改 |
| `src/Wanxiang.Core/Wanxiang.Core.fsproj` | A、E | Wave 0 登记；若必须改，**只允许 A 改** |
| `commitId` 的类型（`uint64` vs `string`） | A、E | **契约已定**：内存中是 `uint64`，**序列化时转字符串**（RFC 8785 附录 D） |
| `Constants.FormatVersion` / `ProtocolVersion` | C、E | Wave 0 定好取值；改动走契约流程 |
| `README.md` | — | **由 F 在最后统一改**，其余流不许动 |

---

## 7. 每流的纪律

1. **只碰自己范围的文件**（见 §1 表的「范围」列）。
2. **改契约须广播**（§2.2），并同步更新本 skill 的契约段。
3. **不许跨流提交**：一个 commit 只含一个流的改动，便于回滚。
4. 每条映射/格式规则**必须有对应单测**（映射表 = 测试清单）。
5. 提交前跑：`dotnet build src/Wanxiang.slnx -c Debug` + `dotnet test`。

---

## 8. 最终验收（流 F 负责，全绿才算完工）

1. `dotnet build src/Wanxiang.slnx -c Debug` → **0 警告 0 错误**
2. `dotnet test` → **全绿**
3. **互操作**：官方 `AGUI.Client` 的 `AGUIChatClient` 作独立客户端，跑通
   `initialize → session/new → session/prompt → 流式文本 → 工具调用展示 → RUN_FINISHED`
4. **超集**：官方客户端跑通标准面；我方客户端仍能 observe / catch-up / 幂等重发 / 附件 / 配置写入
5. **宽容性**：注入未知 `type` 事件 → 忽略并告警、**连接不断**
6. **截断恢复**：截断文件尾部 → 重启后丢弃尾部、前面全恢复，`doctor` 有报告
7. **大整数**：`commitId` 为字符串；JCS 往返后数值不变

---

## 9. 收尾：拆工作树 + 删本 skill

```bash
# 1) 拆工作树
for b in A B C D E; do git worktree remove ../wt-agui-$b 2>/dev/null; done
git worktree prune
for b in A B C D E; do git branch -d agui/$b 2>/dev/null; done    # 已合入才删

# 2) 删本 skill（连同索引行）
rm -rf .agents/skills/wanxiang-concurrency-plan/
# 然后编辑 .agents/skills/wanxiang/SKILL.md，删掉指向本 skill 的那一行
grep -rn "wanxiang-concurrency-plan" . --exclude-dir=.git   # 必须为空
```
