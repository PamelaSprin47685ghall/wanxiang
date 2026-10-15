# 2026-10-15 发件箱切口交接（outbox-cutover 收口）

## 调查结论：发件箱与命令反馈已接线

以当前调用路径、fsproj 包引用与已删除实现为准（不依赖 feed 打包来源）：

| 职责 | 当前 owner | 库侧能力 | 已消费？ | 可退出的旧路径 |
|---|---|---|---|---|
| Outbox 离线排队/重发/状态机 | `Wanxiang2.Outbox`（compose，`MessageOutbox`） | Stage/Restore/SetState/Expire/Disconnect/DiscardUnsent/DiscardConversation/ConversationReady/TryFind/ForInstance/Items/AllInstances/Accept/Commit/RejectCreation | 是：AppShell.fs 约 26 处直接调用 | 无（旧 `Model/UiControllers.fs`/`DraftStore.fs` 已不在 fsproj Compile 列表） |
| CommandFeedback 确认/拒绝/超时 | `Wanxiang2.CommandFeedback`（libs，`CommandFeedbackTracker`） | TrackId/TrackIdWithCompletion/Commit/Reject/RejectAll/Clear/Expire | 是（含经 Outbox 包 AutoOpen 扩展按命令登记） | 无 |
| CommandCommitted/Rejected/Queued 事件分派 | `Wanxiang2.ProtocolDispatch`（compose） | `Outbox.Accept/Commit` + `Commands.Commit/Reject` | 是：AppShell 把 outbox/commandFeedback 实例传入 Dispatch（AppShell.fs:1524–1525） | 无（旧 HandleEvent 已由 Dispatch 取代） |
| TearDownConnection 衔接 | 宿主（AppShell.fs:574–584） | 消费 `outbox.Disconnect` + `commandFeedback.RejectAll` + `configFeedback.RejectAll` | 是（AuthAccepted:1550 的第二处 Disconnect 为换 instanceId 清理，语义独立非重复） | 无 |

结论：闭环形态，无未接回能力。vendor AGENTS.md 迁移表 10.3 行（及 UI 壳层多行）的「未开始」已过期——这是文档债，不是缺口。

## 本轮改动：常量提取（零行为变化）

AppShell.fs:2211 的 `outbox.Expire(now, TimeSpan.FromSeconds 30.0)` 是宿主里唯一剩下的字面时长。同类等待上限（`hostRequestTimeout` 12s，决策 443）均收敛于 `MotionLedger`，且库侧 Outbox.fs:160 注释明说「消息投递类命令由发件箱自己的 30 秒确认超时负责」——但该 30 秒此前只活在调用点。

**放置位置的选择**：`MotionLedger` 位于应用侧本地文件 `src/Wanxiang.UI/Design/MotionPolicy.fs`，不是 vendor pin——无需改 vendor。该模块已有 `outboxDeliveryCheckTick`（1000ms）条目，`outboxConfirmTimeout` 与之同属发件箱语义族，直接相邻放置。

改动两处：

1. `src/Wanxiang.UI/Design/MotionPolicy.fs`：MotionLedger 新增
   ```fsharp
   let outboxConfirmTimeout = TimeSpan.FromSeconds 30.0
   ```
   （含注释：与 hostRequestTimeout 12s 语义不同，分记）
2. `src/Wanxiang.UI/AppShell.fs:2211`：`outbox.Expire(now, MotionLedger.outboxConfirmTimeout)`

值仍 30 秒，仅消除字面量，行为影响为零。

## 文档债（未处理，属工程师在独立 checkout 的决定）

- vendor/wanxiang2 AGENTS.md 迁移表 10.3 行「未开始」过期：vendor 是 pin，更新需在独立 checkout 中进行，本轮不动。
- 同表 UI 壳层多行「未开始」同样过期（与草稿/连接/分页三轮同模式）。

## 验证（DevOps 已于源码提交 99b68a6 上执行，全部通过）

- `git status` / `git diff --check`：工作树干净（仅预期中的 `wanxiang2/` 工程师独立未跟踪 checkout，未动），无未提交改动，exit 0。
- `dotnet build wanxiang.slnx -c Release`：0 错误，exit 0。
- `dotnet format wanxiang.slnx --verify-no-changes`：exit 0（仅 workspace 加载常规警告，基线噪音）。
- 针对性测试（测试二进制 `-class` 多选）：`InteractionReliabilityTests`（含 `outbox.Expire(now, 30s)` 语义断言）+ `ComposerReliabilityTests`（outbox 集成级）+ `UiStabilityTests`（含 `MotionLedger` 常量断言）+ `AppShellTests`，合计 90/90 通过，exit 0。基线数字未动（本轮无新增测试）。

以上验证针对源码提交 `99b68a6`（本回填提交另在其后）。构建成功同时佐证 fsproj 包引用在当前 feed 下解析正常。

## 四轮总览：本轮系列收口

| 切口 | 库侧接线 | 本轮系列唯一实事 |
|---|---|---|
| 草稿（draft-cutover） | 已完成 | 宿主内小改进 + 交接文档 |
| 连接/重连（reconnect-cutover） | 已完成 | 同上 |
| 历史分页（paging-cutover） | 已完成 | 同上 |
| 发件箱（本轮） | 已完成 | `outboxConfirmTimeout` 常量提取（本文档） |

四个切口均以「已接线 + 宿主内小实事」形态收口，无未接回能力，无 vendor pin 变更。

## 未决事项

本地 feed（0.0.0-local）的实际打包来源未验证（根目录 `wanxiang2/` 与 `vendor/wanxiang2/` 为同一上游两份 checkout，抽查内容一致）；需跑构建确认，归 DevOps。不动摇「已接线」判断——该判断依据是 fsproj 引用与已删除实现，不是 feed 内容。
