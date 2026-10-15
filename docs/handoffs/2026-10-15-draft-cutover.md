# 草稿保存与恢复：抽吸盘点与收口（draft cutover）

- 分支：`assist/deletion-cutover`
- 起始提交：`ac3c969`（本轮工作基线）
- 最终提交：`b1cd9d3`（`b1cd9d396fe58e34d3fa0963d40c4ac7d20964a6`，含本文件、两个源码文件与基线下调；本回填小提交另在其后）
- 日期：2026-10-15

## 结论先行

**库侧草稿能力足够，且应用侧接线在前一轮（DraftSession 组合）已经完成。** 本轮盘点核实了这一点，没有发现宿主侧与库重复的草稿实现、没有死分支。`Model/DraftStore.fs` 不是待删的旧实现——它是库侧设计里明确留给宿主的「落点执行层」。本轮唯一的实质收敛是删除一条应用侧纯库函数测试（其反例已由库金标准逐例覆盖）。

## 库侧能力（vendor pin c3a2e74，即 `vendor/wanxiang2/`）

| 包 | 职责 | 证据 |
| --- | --- | --- |
| `Wanxiang2.DraftStore` | DraftSnapshot/DraftCodec（drafts.json 编解码）、DraftWrite、去重闸门 DraftStore 对象、DraftPolicy/DraftRestore 纯函数 | `libs/DraftStore/AGENTS.md`：金标准 `tests/golden/expected.txt` 由旧 DraftCodec 生成，逐字节相同；`tests/golden/inputs.jsonl` 覆盖 garbage / `[]` / version 2 / version 0 / version 1.5 / `future:1` 未知字段 / 坏条目（无 instanceId、非对象条目、坏 guid） |
| `Wanxiang2.DraftSession` | 会话状态：已存快照、读完标志、已恢复主机、已知对话、SavedAt；Loaded/Restore/Restored/Flush/Prune/Clear/StorageCleared | `compose/DraftSession/AGENTS.md`：迁自旧 AppShell 字段与方法，旧用例按名迁入 |
| `Wanxiang2.ComposerDrafts` | 内存草稿表 + 附件草稿状态机（PendingAttachment/AttachmentDraftController/RestoredAttachments） | `libs/ComposerDrafts/AGENTS.md` |
| `Wanxiang2.BrowserDrafts` | PWA IndexedDB 桥（main.js draftGet/draftPut/draftDelete） | C# 库，应用 fsproj 已引用 |

**有意不迁**（`libs/DraftStore/AGENTS.md` 原文）：旧 `IDraftBackend` 与三个后端（文件/内存/浏览器）是端口式注入，库不迁；`DraftStore.Save` 返回 `DraftWrite`，宿主按返回值写自己的落点；「是否启用」由宿主判断。

## 应用侧盘点结果

### `src/Wanxiang.UI/Model/DraftStore.fs`（96 行）——保留，非重复实现

内容：`IDraftBackend` 端口 + `FileDraftBackend`（桌面原子写、0600 权限、WANXIANG_HOME）+ `MemoryDraftBackend`（测试替身）+ `BrowserDraftBackend`（经 Wanxiang2.BrowserDrafts）+ `LocalDraftStore` 外壳（收拢启用开关、执行 DraftWrite）。

债权人核查：
- `LocalDraftStore.Default`：AppShell.fs:78 唯一生产调用点。
- `MemoryDraftBackend`：ComposerReliabilityTests:181（`when turned off nothing is written...`，库 AGENTS.md 明言「开关归宿主，留在旧仓库随宿主改写」）。
- `FileDraftBackend` / `BrowserDraftBackend`：`LocalDraftStore.Default` 按平台二选一。
- 无 speculative fallback；端口有两个真实后端实现 + 一个测试替身。**不删。**

### `src/Wanxiang.UI/AppShell.fs` 草稿接线——已完整，无死分支

- 字段（75-81）：`drafts = ComposerDrafts()`、`draftStore = LocalDraftStore.Default`、`draftSession = DraftSession()`。旧 `storedDrafts`/`draftsLoaded`/`restoredInstances`/`knownConversationIds`/`unsentSavedAt` 字段已不存在（grep 核实）。
- 保存：`FlushDrafts`（456）走 `draftSession.Flush` → `draftStore.Apply`；800ms `draftTimer`（2213）、`DetachedFromVisualTree`（2246）、PWA `BrowserPage.add_Hidden`（2243）三处触发，与旧语义相同。
- 恢复：`LoadLocalDrafts`（2256，读完才写盘）→ `RestoreLocalDrafts`（463，连上主机放回、不覆盖已输入文字、附件标「需重新选择」）。
- 切换对话恢复草稿：651-654（`drafts.Get(...).text` 双向同步）；发送后 `drafts.Move(instanceId, None, Some id)`（940）；断连 `MarkAllUploadingAsFailed`（315-317）；删除对话 `PruneDeletedConversationDrafts`（496）；清除 `ClearLocalDrafts`（479）；开关切换 `SavePrefs`（530-535）。**行为语义全部保留，无变化。**

## 本轮改动

1. **删除** `tests/Wanxiang.Tests/ComposerReliabilityTests.fs` 中 `damaged or newer draft files are ignored instead of blocking startup`：它测的是 `Wanxiang2.DraftStore` 包的公开纯函数 `DraftCodec.tryParse`，四个断言的反例（not json、`[]`、version:99、未知字段+坏条目）与库金标准 `inputs.jsonl`（garbage、`[]`、version 2、`future:1`、坏条目）同类且被逐例覆盖。删除处留注释注明承接。未为旧测试反射保留任何生产方法。
   - 过程反例：首次替换因切片边界算错留下残骸（注释缩进翻倍 + 孤 `)`），已用第二次精确 edit 修复并验证区域干净。教训：删除整段时应把行尾换行与闭合括号一并计入 find。
2. **注释修正** `Model/DraftStore.fs` 头注释：原文「去重闸门与会话状态在 Wanxiang2.DraftSession」不准确——去重闸门是 `Wanxiang2.DraftStore` 的 `DraftStore` 对象（DraftSession 组合它）；会话状态才在 DraftSession。已改为准确表述。

### 未删除的候选及其理由（Manager 决定项）

- `Model/DraftStore.fs` 整文件：库无落点能力（有意设计），删除即破坏桌面/PWA 草稿持久化。**不需要改库、不需要新 pin**——除非 Manager 决定把「落点后端」也抽成库（见下一候选）。

## 行为变化

无。草稿保存/恢复/切换/清空语义与旧实现相同；库侧金标准（codec 字节、restore 文案、save 决策）与旧实现逐字节/逐例一致。

## 测试迁移账目

- 删除：`damaged or newer draft files are ignored`（应用侧）→ 承接：`vendor/wanxiang2/libs/DraftStore/tests/Tests.fs` 金标准 `golden: codec bytes, save decisions and merges match old code` + `inputs.jsonl` 反例集。
- 保留（宿主组合级，正当）：`closing and reopening restores...`、`deleting a conversation removes...`、`the draft file carries no tokens or keys...`、`when turned off nothing is written...`、`switching the setting off deletes the file and clear drafts empties everything`——都驱动真实 MainView / 真实文件落点 / 开关，是宿主职责的证据，库测试不覆盖这一层。

## 验证（DevOps 已执行，全部通过）

- `python3 -B -m unittest discover -s tools/tests -p test_wanxiang2_feed.py -v`：17/17 通过（exit 0）。
- `tools/wanxiang2-feed.sh`：feed ready，pin `c3a2e74`，316 包（exit 0）。
- `python3 tools/tag_ui_tests.py --check`：58 文件 / 290 方法一致（exit 0）。
- 最小相关用例：测试二进制 `-class "*ComposerReliabilityTests*"`，14/14 通过（删除前 15 个，数目吻合；`dotnet test --filter` 语法不适用于本项目 xUnit v3 自托管 runner，实测 exit 1，故改用 `-class`）。
- `./run-tests.sh -j 4 --large-history`：UI 4 进程 134+95+44+72、非 UI 225（含 LargeHistory），0 失败，exit 0。
- `dotnet build wanxiang.slnx -c Release`：0 警告 0 错误（exit 0）。
- `dotnet format wanxiang.slnx --verify-no-changes`：exit 0（仅 workspace 加载常规警告）。
- `git diff --check`：干净。

**配套门禁修改**：首次 `run-tests.sh` 报红——`NonUI 用例数 222 低于基线 223`，即本次有意删除一个测试的预期后果。按脚本提示把 `tests/test-counts.baseline` 的 `NonUI=223` 下调为 `222`（有意删除，同提交说明），下调后重跑全套件 exit 0。此改动随 `b1cd9d3` 一并提交。

## 未覆盖范围

- 库侧 `./x test DraftStore` / `./x compose` 未在本轮跑（vendor checkout 的验证归 DevOps/Manager）。
- PWA 真实 IndexedDB 路径（BrowserDraftBackend → main.js）无自动化测试覆盖（现状如此，非本轮引入）。
- `MemoryDraftBackend` 注释里「宿主没有任何持久存储时的退路」这一说法在生产代码中无使用方（Default 只选 File/Browser）；它是测试替身。若要收紧，可把注释里的退路说法删掉——留给下一轮，避免顺手改动。

## 下一候选

1. 落点后端抽库（可选，需 Manager 决策）：把 `FileDraftBackend`（原子写+权限）与 `LocalDraftStore` 外壳抽成 wanxiang2 库，应用只留平台选择与 BrowserDraftBackend 接线。收益：文件原子写/权限逻辑获得独立测试；成本：新 pin 流程。
2. AppShell 下一切口：连接收尾 / 历史分页 / 发件箱（按任务约束本轮未碰）。
