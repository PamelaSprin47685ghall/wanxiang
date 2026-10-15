# 抽吸接续交接：删除与撤销

## 项目与起点

- 项目：`~/Desktop/vibe/wanxiang`。
- 工作分支：`assist/deletion-cutover`，从 `extract-ledger@8f39e84` 创建。没有合入或移动 `main`、`extract-ledger`。
- 消费的新库：`vendor/wanxiang2@c3a2e74abafd0daf98db28b6b8ede750f331e53c`，原来是 `aa883a2`。
- 仓库根的 `wanxiang2/` 是工程师的独立开发 checkout，接手时已作为未跟踪目录存在。本轮没有修改它，也没有把它加入应用仓库。
- 这是一个应用接线闭环，不是新的大批库拆分；没有修改协议、持久格式或删除的八秒撤销窗口。

## 本轮改动

### 1. 接回 DeletionDesk，退出旧实现

`src/Wanxiang.UI/AppShell.fs` 使用 `Wanxiang2.DeletionDesk` 交付包的组合者。单条和批量删除都走 `Schedule`；新列表快照从同一个 `Pending` 过滤待删行。

退出的旧实现：`NeighborExcluding`、`ScheduleDelete`、`ArmDeleteTimer`、`CommitDelete`；退出的字段：`pendingDeletes`、`deleteTimer`。宿主只保留真实侧栏/提示条/导航、一次性 DispatcherTimer、命令发送的接线。没有把旧方法留成测试用转发层。

原有三个 UI 集成测试保留：批量反馈与命令登记、实际撤销按钮、当前对话删除后的去向。最后一个从只反射邻位计算，改成实际 `DeletionDesk.Schedule` 加宿主导航和侧栏断言；另补“列表快照不能让待删行闪回”的断言。

### 2. 补齐包消费的构建前置

CI 的 pr/main/large-history、release 的 package、stress 五个任务都检出锁定的 submodule，并在 restore/build/publish 之前调用已有 `tools/wanxiang2-feed.sh`。没有另造打包器或版本体系。

`extract-ledger` 的 push 也触发应用门禁；PR 只更新 `vendor/wanxiang2` 或包源脚本时，仍选中大历史回归。README 的构建路径从不存在的 `src/Wanxiang.slnx` 改为根 `wanxiang.slnx`，补上包源准备和更新 pin 后清缓存的说明；项目结构表与现有三个宿主工程对齐。

干净消费实际复现了 `NU1605`：新库明确要求 `FSharp.Core >= 10.1.401`，旧应用却用 SDK 默认的 `10.0.111`。新增根 `Directory.Build.props` 固定 `FSharpCoreImplicitPackageVersion=10.1.401`，`src/Directory.Build.props` 显式继承；测试工程也使用同一值。没有忽略包降级，没有升级机器 SDK，也没有让新库引用旧仓。修复后测试工程成功编译。

### 3. 恢复一个未被发现的旧测试

`3bb8dc8` 移除了 `PathologicalRenderTests` 中唯一测试的 `Fact`，但留下方法和 UI trait。接手预检因此报“无 UI 测试却带 UI trait”。本轮恢复测试标记，并把主题读取移入 `Headless.run` 内，继续检查实际宿主 `Cards.render` 的六类病态输入。没有下调阈值、删测试或接受新 skip。实际发现 UI 用例从 339 恢复到 340 后，将 `tests/test-counts.baseline` 的 UI 下限同步上调为 340。

## 验证

本轮从未初始化的 vendor、不存在的本地 feed 和应用 NuGet 缓存开始。先检出锁定版本，再从源码产出包，应用只经包消费。

| 验证 | 结果 |
|---|---|
| `vendor/wanxiang2/x check` | 通过：250 个独立库，66 个包组合者 |
| `python3 tools/tag_ui_tests.py --check` | 通过；恢复病态渲染测试后，UI trait 与发现入口一致 |
| 工作流顺序检查 | 五个构建任务的 submodule/包源前置正确；移除准备步骤的反例被检查拒绝；vendor pin 会选中历史回归 |
| `tools/wanxiang2-feed.sh` | 通过：从新检出的锁定源码构建 316 个交付包 |
| DeletionDesk 组合测试 | 6 通过，0 失败，0 skip |
| 应用测试工程 Debug 构建 | 通过；11 条警告均在本轮未修改的测试文件（未关闭警告） |
| 应用定向 UI 回归 | 29 通过，0 失败，0 skip；包含 InteractionReliability、InteractabilityPolish8 和恢复的 Pathological 用例 |
| 应用默认全套 `./run-tests.sh --no-build -j 4` | 561 通过（UI 340、非 UI 221），0 失败，0 skip；不含 3 项 LargeHistory 分级 |
| 大历史分级 `./run-tests.sh --no-build --no-ui --large-history` | 223 通过，0 失败，1 个既有 skip；其中 221 项是非 UI 重跑，另 2 项大历史通过。10⁶ 提交的手动基准沿用原 skip |
| Release 解决方案构建 | 通过，0 错误；11 条测试文件警告，与 Debug 构建相同；本轮修改的源码与测试文件无警告 |
| `dotnet format wanxiang.slnx --verify-no-changes` | 退出码 0；工具提示工作区加载存在警告，不能称为无警告验收 |
| Release App/PWA 发布 | 通过；发布目录包含 PWA，现有 avalonia.js 补丁校验通过 |
| 发布产物真实 Chrome 冒烟与 parity | 通过：实际画布、DPR 2、文本上屏、探针默认关闭、启动/计时/恢复事件、快捷键、偏好写入与刷新恢复，无未捕获页面异常 |

环境：Linux，.NET SDK `10.0.111`，`wasm-tools` 已安装。准备浏览器测试时，`npm ci --ignore-scripts --no-audit --no-fund` 成功；npm `12.2.0` 对当前 Node `25.9.0` 给出支持范围警告，本轮没有升级机器工具链。

应用日志、发布目录与截图在 `.scratch/assist-deletion-2026-10-15/`（不纳入 Git）：`feed.log`、`deletion-library.log`、`build-tests.log`（首次 NU1605 失败）、`build-tests-pinned.log`、`focused-ui.log`、`application-tests.log`、`large-history.log`、`build-release.log`、`publish.log`、`smoke.log`、`format.log`、`smoke/`。检查明细而不是累加重复运行的测试数；561 项默认用例 + 2 项大历史是 563 项不同应用用例，另 1 项手动基准跳过。

## 复验

```sh
git submodule update --init vendor/wanxiang2
tools/wanxiang2-feed.sh
dotnet test --project vendor/wanxiang2/compose/DeletionDesk/tests/DeletionDesk.Tests.fsproj
python3 tools/tag_ui_tests.py --check
./run-tests.sh -j 4
./run-tests.sh --no-build --no-ui --large-history
dotnet build wanxiang.slnx -c Release
dotnet publish src/Wanxiang.App/Wanxiang.App.fsproj -c Release -o "$PWD/.scratch/recheck-publish"
npm ci --ignore-scripts --no-audit --no-fund
tools/smoke_published.sh "$PWD/.scratch/recheck-publish"
```

切换库 pin 后不要只调用 `run-tests.sh`：它目前只检查 LedgerStore 包是否存在，不能识别所有同版本旧包。显式运行 feed 脚本会重建并清理应用的同版本缓存；本轮没有改变此版本策略。

## 交接边界与下一步

本文件与实现、测试、包引用、构建修补一并提交到本地 `assist/deletion-cutover`；最新交接提交用 `git log -1 assist/deletion-cutover` 查看。代码改动共用这个提交，不需要从聊天里手工拼补丁。

没有修改 `wanxiangshu` 或 lifecycle-cutover；没有推送、向 registry 发布包、建 Release 或合并主分支。工程师回归时先用 `git diff extract-ledger...assist/deletion-cutover` 检查本轮差异，再决定如何接纳，不要直接覆盖其他新提交。完成后除接手前已存在的 `?? wanxiang2/` 外，不应有本轮未提交文件；两个新库 checkout 均应保持源码干净。

下一项仍建议接已有的 `ChatScreen` / `Workspace`，不要为本轮的删除接线继续扩张接口。独立新库开发树的迁移表仍有过期状态；本文件只记录本轮已经接回的职责，不替所有条目宣布完成。

真实手机输入法、真实模型服务、跨平台发行包和远端 Actions 不在本轮证明范围内。包检查、UI headless、整应用测试、发布目录冒烟的结论应分别读取。
