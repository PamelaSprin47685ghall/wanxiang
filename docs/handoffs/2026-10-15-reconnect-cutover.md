# 连接切口交接（reconnect-cutover）— 2026-10-15

分支：assist/deletion-cutover。前序：连接认证与断线收尾只读调查（结论见第 1 节）。

## 1. 连接切口调查结论

**库侧接线已完成，无需改库、无需新 pin。** 以当前调用路径为准（不照旧待办表）：

- 传输层：AppShell.fs `let client = WsClient()`（`open Wanxiang.Client`），命名空间整体来自 `Wanxiang2.WsClient` 包（fsproj:67）；旧 `src/Wanxiang.Client` 工程已不存在（src 仅剩 App / Pwa / UI）。
- 重连接记账：`Wanxiang2.ReconnectLoop`（Connect / IsCurrent / Schedule / Fire / ResumeNow / ResetDelay），fsproj:208；计时与真正连接有意留宿主。
- 退避：`Wanxiang2.ReconnectBackoff`（1s 翻倍封顶 30s），fsproj:83。
- 失败类型化：`Wanxiang2.ConnectionIssue` + `ConnectIssueText`，fsproj:84/100。
- 配对/凭据：协议事件经 WsClient；配对 UI 走 `Wanxiang2.DialogsDesk` ConnectPane；桌面自动配对 `Wanxiang2.DesktopPairing.ensure`（Program.fs:91）；凭据解析在 `Wanxiang2.ClientConnections`（fsproj:202），IndexedDB 在 `Wanxiang2.BrowserCredentials`（fsproj:73）。

### 职责归属摘要

| 职责 | owner | 备注 |
|---|---|---|
| WebSocket 传输 / 按代发送 | Wanxiang2.WsClient | 已消费 |
| 重连记账与退避 | Wanxiang2.ReconnectLoop / ReconnectBackoff | 已消费 |
| 失败类型化与文案 | Wanxiang2.ConnectionIssue / ConnectIssueText | 已消费 |
| 配对码/令牌流程 | 协议事件在库，首事件时序在宿主 | 有意组合职责 |
| 桌面自动配对 | Wanxiang2.DesktopPairing（Program.fs:91） | 已消费 |
| 凭据存储 | CredentialStore.fs（80 行 IO 薄壳）+ ClientConnections / BrowserCredentials | 壳是有意的 IO 边界 |
| 连接目标选择 | 宿主 AutoConnect | 选择顺序属组合职责 |
| 同进程主机就绪门（决策 291） | 宿主 LocalHostGate | 无对应库，有意 |
| 断线收尾编排 | 宿主 AppShell | 组合者职责，本属宿主 |

## 2. 本轮改动：断线收尾序列去重

AppShell.fs 原有两处几乎重复的断线收尾序列：主动 `Connect()` 前（原约 572–588 行）与 `client.ConnectionClosed` 处理（原约 2158–2178 行）。

### 两处序列差异结论

共同核心（九步，两处逐字一致、顺序一致）：

`authenticated <- false` → `runs.Disconnect()` → `exportDialog.Disconnect()` → `FailOlderPage "连接已断开…"` → `outbox.Disconnect instanceId` → `commandFeedback.RejectAll()` → `configFeedback.RejectAll()` → `FailUploadingAttachments()`（内部即草稿 `MarkAllUploadingAsFailed`，AppShell.fs:314）→ `downloads.Clear()`（R1 缓冲整体丢弃）。

入口独有、**保留在各自调用方**的语义：

- Connect 前：`reconnect.Connect()` 新票据、`connectionError / connectionIssue <- None`、`settings.Health.Disconnected()`、收尾后 `Render()`，随后发起连接（url / token / pairingRequested、LocalHostGate 门、AuthPresent / PairingRequested 首事件）。
- ConnectionClosed：epoch 守卫（陈代丢弃）、`approvalPanel.Disconnected()`、`chat.SetAwaitingApproval Set.empty`、`settings.Devices.NotifyDisconnected()`、错误 detail 提取与 `sidebar.SetConnection(false, "连接已断开")`、可选 toast、`Render()`、`ScheduleReconnect()`。

顺序归并说明（已核对无行为差异）：ConnectionClosed 侧 `authenticated <- false` 原在最前，现随公共块整体执行，位于 approvalPanel / chat 之后；`settings.Devices.NotifyDisconnected()` 原插在 `FailUploadingAttachments` 与 `downloads.Clear` 之间，现移至公共块之后。上述调用各自操作互不相干的组件状态重置，无共享可变状态，重排不改变可观察行为。

### 改动形态

- 新增私有成员 `TearDownConnection()`（置于 `Connect()` 之前），承载九步公共序列。
- `Connect()` 与 `ConnectionClosed` 处理均改为调用它；入口独有步骤原样保留。
- 不改任何公开行为，不碰 vendor 库，无新包引用。

## 3. 文档债（不属本轮）

vendor/wanxiang2 AGENTS.md 迁移状态表中「客户端连接」「UI 连接流程记账（ReconnectLoop）」仍记「未接线」，已过时（实际调用路径已在用库）。更新需工程师在其独立 checkout 决定，本轮不动 vendor 树。

## 4. 验证（交 DevOps）

- `dotnet build`（含 src/Wanxiang.UI）：预期全绿；本次改动为纯提取，无新符号依赖。
- 含连接生命周期的回归：vendor 侧 ClientStateAndWsClientTests（已迁入库）覆盖传输/重连原语；宿主侧断线收尾行为靠现有 E2E。预期断线后：草稿附件标记上传失败、命令/配置反馈全部拒绝、下载缓冲清空、重连照旧调度。

## 5. 下一候选

- vendor AGENTS.md 迁移状态表更新（文档债，独立 checkout）。
- 若继续宿主内收口：`Connect()` 首事件发送时序（token / pairingRequested 状态字段）可评估进一步收敛，属独立切口。
