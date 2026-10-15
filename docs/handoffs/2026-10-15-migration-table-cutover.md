# 收尾交接：迁移状态表如实化落地（移交项 a）

## 起点与范围

- 项目：`~/Desktop/vibe/wanxiang`（旧仓库工作根），分支 `assist/deletion-cutover`（HEAD `d00d59d`）。
- 本轮只落地 `docs/handoffs/2026-10-15-migration-table-audit.md` 的移交项 a（权威文档更新）：把工作根 `wanxiang2/AGENTS.md`「## 迁移状态」表中过期的「未开始」如实化为「已接线」，并更正 174 行（事件账本）的行内 commit hash。
- 不动旧仓库 `main`，不动 `vendor/wanxiang2` submodule pin（`c3a2e74abafd0daf98db28b6b8ede750f331e53c`），不改 vendor 下任何文件，不改既有 handoff（含 migration-table-audit.md 本身）。
- 更正落在 wanxiang2 开发仓库的 `assist/migration-table-cutover` 分支，由 DevOps 提交；本道路不执行任何命令。

## 为什么先做这一项

- 审计结论：48 个数据行的「未开始」是文档过期而非接线缺口（「真欠接线」A 清单与「库侧未做」C 清单均为空）。不先把权威文档如实化，后续按这些标注立项就是重复核对已过期的事实，正是 stale-documentation 要避免的假修复。
- 迁移表权威在新仓库一侧（`vendor/wanxiang2` main 的上游 `github.com:PamelaSprin47685ghall/wanxiang2`）；工作根 `wanxiang2/` 是其完整 clone 的开发副本（main=`c3a2e74`），更正在此分支落地后由职权方合入。

## 已完成

- `wanxiang2/AGENTS.md`「## 迁移状态」：
  - 175–222 共 48 个数据行的「接入旧仓库并退出旧实现」列由「未开始：…」改为「已接线：…」，逐行与审计逐行核对表（行号→结论→证据缩写 [E1]–[E7]）一致，并保留各行点名职责/文件信息作为当初的接线范围说明。
  - 表头上方新增一行总证据指引（接线状态属 `assist/deletion-cutover`；Release 构建与全量 571 项用例已验证；逐行证据指向本审计）。
  - 174 行 commit hash `extract-ledger@84017ef` 更正为 `extract-ledger@8f39e84`；该行 `[TODO] 合入旧仓库 main` 与其余内容按审计移交项 c 保留。
  - 全文搜索「未开始」：迁移表数据行清零；「抽取路线」「例外清单」等其余章节本无该词，无历史叙述被误改。
- 例外清单核对结论：`compose/*.fsproj VersionOverride` 项仍成立、保留；「已解除」段（2026-10-13）作为历史叙述保留；迁移状态不属例外清单语义，无需新增条目；本次如实化与例外清单不构成冲突。

## 运行登记（DevOps 执行）

- wanxiang2 开发仓库 `assist/migration-table-cutover` 分支提交：`7a13032f202c78bbca0ed411c98a567038f0e29a`（`Cutover migration table: record wired state as audited`，AGENTS.md 52 插/50 删；含 Manager 裁定 1 的 120 行 `84017ef→8f39e84` 更正，与 174 行同源；裁定 2 复核：222 行现行措辞已是「已接线、映射经 compose/AguiWire」，无自相矛盾，未改动；裁定 3 按指示忽略）。该分支 main 未动，`8f39e84` 更正以本节登记为准。
- [TODO] 旧仓库登记提交 hash（本 handoff 与 AGENTS.md 更正的落地提交）。
- [TODO] 门禁运行结果：Release 构建、`./run-tests.sh -j4 --large-history`、`dotnet format --verify-no-changes`、`git diff --check`、`tools/wanxiang2-feed.sh --check`、`vendor/wanxiang2` pin 复核。

## 用户待决（属职权边界，本道路不动）

- [TODO] wanxiang2 开发仓库 `assist/migration-table-cutover` 分支合入其 `main`。
- [TODO] 旧仓库 `vendor/wanxiang2` pin 前进到合入后的 wanxiang2 `main`。
- [TODO] 旧仓库 `assist/deletion-cutover` 合入旧仓库 `main`（`main` 只由用户批准合并，分叉需显式裁决）。

## 复验与本机证据

- 接线与构建凭据引用 `2026-10-15-migration-table-audit.md` 的「动态运行凭据」：Release 0 错误、全量 571 用例（UI 346 + 非 UI 225）、570 通过、1 既有跳过、0 失败；数字基线以该审计为准（571/570/1），本文件不另造第二套真相。
- 本轮只改 markdown 文档，不引入源码或测试变化；上列门禁在 DevOps 执行时对本次工作树复跑确认。

## 边界声明

- 本文件是 `2026-10-15-migration-table-audit.md` 移交项 a 的落地登记，不推翻其任何结论；其逐行核对表、证据缩写与数字基线为本文权威出处。
- 只新增本 markdown、只更正工作根 `wanxiang2/AGENTS.md` 的迁移状态一节（含表头说明行）；不改动 vendor、不改动既有 handoff、不执行命令、不跑 build/git/test。提交与门禁运行归 DevOps，文档权威合入归用户批准。
- 如实登记的待决不一致（交回裁定，本道路未擅改）：
  1. `AGENTS.md`「已解除」段仍写 `extract-ledger@84017ef`，与 174 行更正后的 `8f39e84` 不同源；审计移交项 c 仅点名 174 行，未覆盖此处。
  2. 222 行原「AG-UI 映射（旧 `WireAgui.fs`）暂留旧仓库」与 180 行原句及抽取路线 4（`compose/AguiWire` 已完成）矛盾；审计 108 行已声明此类历史措辞改以静态凭据为准，本次按「已接线、映射经 compose/AguiWire」落地。
  3. 审计第 6 行叙述「表头在 173 行」与文件实际表头行 172 偏移 1；逐行核对表的数据行号（174–222）与文件一致，不影响结论。
