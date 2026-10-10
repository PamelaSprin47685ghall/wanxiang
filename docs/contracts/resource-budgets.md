# 资源预算（Resource Budgets）

> 依据：AGENTS.md §22「建立资源预算」。
>
> 规则 R5：所有无界集合必须说明上界策略——什么时候清理、什么时候分页、
> 什么时候转入冷存储、最坏内存占用是什么。
> 本文件是万象全部有界/无界集合的权威清单。**未列入本表的集合视为无界，禁止新增。**

状态标记：

- ✅ 已实现（有代码边界）
- ⚠️ 已实现但策略是「增长到 X 后停止增长」（有界，不清理）
- ❌ 未实现（明确列为已知限制，见「未实现边界」）

---

## 1. 启动重放预算

| 对象 | 预算 | 策略 | 状态 |
|---|---|---|---|
| 启动重放量 | 无硬上限（完整重放全部 `.jsonseq`） | 快照启动（S-003）：`data/snapshots/projection-<id>.snapshot` 冻结投影 + 全部提交外壳（v2，2026-10-10），启动直接加载快照 + 只重放 `throughCommitId+1` 之后（算法 L7）——启动成本 O(快照加载 + 增量)，与历史长度解耦（§12 STOP-2）。v1 旧快照（无 commits 字段）回退 collect-only 外壳收集（解码 + 校验 + 收集，不 apply projection），保证 `CommitCoordinator.commits` 覆盖全历史、catch-up 任意游标可用。快照损坏/超前回退完整重放（L8/L9）。**第一版不删除历史日志** | ✅ |
| 快照创建时机 | 优雅关闭（ServerApp.Stop） | 只读降级模式不写（日志损坏时快照会固化损坏前投影） | ✅ |
| 最坏内存占用 | O(全部历史投影) | 未压缩；第二版 compaction（热→冷）后才受控。见「未实现边界」 | ❌ |

## 2. 快照

| 对象 | 预算 | 策略 | 状态 |
|---|---|---|---|
| 快照文件尺寸 | 无硬上限 | 与投影大小成正比；`checksum`（SHA-256 of JCS）保证完整性 | ⚠️ |
| 快照保留 | 不删除任何快照 | `findLatest` 按 `throughCommitId` 降序取最新有效；旧快照保留（回退路径） | ⚠️ |
| 快照数量 | 每次优雅关闭 +1 | 无上限（长期运行多次关闭会累积）。**未实现清理策略**（第二版） | ❌ |

## 3. Catch-up

| 对象 | 预算 | 策略 | 状态 |
|---|---|---|---|
| 单批最大事件数 | 64 | `WsConnection` 每批 `commits |> List.truncate 64`；客户端应用后发 `cursor.advanced` 驱动下一批（决策 34） | ✅ |
| 单批最大字节数 | 无显式字节上限 | 受 64 条事件约束；单条事件大小由消息正文决定 | ⚠️ |
| 会话快照消息上限 | 200 | `snapshotMessageLimit = 200`；超出进入慢客户端分批 catch-up（决策 32-34） | ✅ |

## 4. WebSocket

| 对象 | 预算 | 策略 | 状态 |
|---|---|---|---|
| 发送队列上限 | 2048 条 | `BoundedChannelOptions(2048, FullMode = Wait)`（`WsConnection`）——慢客户端不丢事件，发送阻塞 | ✅ |
| 队列满行为 | 发送阻塞 | `FullMode.Wait`：不静默丢弃；配合快照/catch-up 降级 | ✅ |

## 5. 会话 snapshot（内存投影）

| 对象 | 预算 | 策略 | 状态 |
|---|---|---|---|
| 会话消息数 | 无上限 | `Projection.conversations` 全量内存；`effectiveMessages` 递归展开 fork 链 | ⚠️ |
| 最坏内存占用 | O(全部消息) × O(fork 深度) | 未分页、未转冷存储。**未实现边界** | ❌ |

## 6. 附件

| 对象 | 预算 | 策略 | 状态 |
|---|---|---|---|
| 附件最大大小 | 64 MiB | `AppConfig.maxAttachmentBytes = 64 * 1024 * 1024`（Q171 中置信度默认值） | ✅ |
| 分块大小 | 256 KiB | `chunkSizeBytes = 256 * 1024`（Q172）；`AttachmentStore.maxChunkBytes` 校验 | ✅ |
| 上传中临时文件 | 每连接并发 | `uploads` ConcurrentDictionary；`AbortByConnection` 断线清理（决策 71） | ✅ |
| 孤儿附件 | 无自动清理 | doctor `orphan-attachments` 检查（O-003）只读报告；**不自动删除**（R1 用户数据优先） | ⚠️ |

## 7. MCP

| 对象 | 预算 | 策略 | 状态 |
|---|---|---|---|
| 并发上限 | `maxConcurrency`（默认不限流） | `McpClient` `SemaphoreSlim(config.maxConcurrency \|> Option.defaultValue Int32.MaxValue)` | ✅ |
| 调用超时 | `callTimeoutSeconds`（默认 20s，下限 1s） | `callTimeoutMs = max 1000 (config.callTimeoutSeconds * 1000)`；挂死服务器不冻结整场生成 | ✅ |

## 8. 内存 commit cache

| 对象 | 预算 | 策略 | 状态 |
|---|---|---|---|
| 提交缓存 | 无上限 | `CommitCoordinator.commits` 全量内存（供 `CommitsAfter` catch-up）；启动重放后常驻。快照启动时由 collect-only 外壳收集补全锚点前历史（2026-10-10 修复：此前只含锚点后提交，旧游标 catch-up 会静默丢事件） | ⚠️ |
| 保留策略 | 无清理 | 快照启动后仍保留全量（catch-up 需要任意游标）。**未实现分页/冷存储** | ⚠️ |
| catch-up 单批 | 64 条/批 | `CommitsAfter(cursor, limit)` 分页（2026-10-10）：mailbox 线程只复制最多 limit 条，慢客户端追赶长历史不再每批 O(n) 拷贝剩余全部。实测 10⁵ 提交追赶：旧 1013ms → 新 12ms（84x），且 mailbox 占用从全量降到 64 条 | ✅ |

## 9. 诊断

| 对象 | 预算 | 策略 | 状态 |
|---|---|---|---|
| 最近错误缓冲 | 200 条 | `Stderr` 环形缓冲（O-002）；`support bundle recent-errors.jsonl` 来源 | ✅ |
| recovery 证据包 | 每次修复 +1 | `data/recovery/<utc>/` 保留原始文件副本；**不自动清理**（审计证据） | ⚠️ |

---

## 未实现边界（第二版计划）

以下集合当前无界，已明确记录，**不视为「已解决规模问题」**：

1. **启动重放**：无快照时完整重放全部历史（快照已缓解，未删除日志）。
2. **内存 commit cache**：`CommitCoordinator.commits` 全量保留，无冷存储（catch-up 已分页，见 §8）。
3. **会话消息**：投影全量内存，未分页。
4. **快照/证据包累积**：不自动清理。

第二版 compaction 计划（AGENTS.md §13）：热日志 → 冷日志归档（不删除），
仅在「有效快照覆盖该段 + 完整恢复测试 + 备份政策 + 审计无依赖」条件下才允许删除原段。
删除后 fork/history/export 行为必须完全一致。

## 性能基线（§21.6）

固定规模与测量见 `PerformanceBaselineTests`（10³/10⁴ commits）：

| 规模 | 测量项 | 断言上界 |
|---|---|---|
| 10³ | cold-start 重放 | < 5s |
| 10⁴ | cold-start 重放 | < 10s |
| 10³ | 快照启动 vs 冷启动 | 快照启动 < 冷启动 |
| 10³ | 快照创建 | < 5s |
| 10⁴ | catch-up 中段游标 | < 100ms |
| 10⁴ | 启动后托管堆 | < 256 MiB |

10⁵/10⁶ 属于 large-history 分级（§24 CI：定期运行，不进 PR gate）。

**10⁵ 实测基线（2026-10-10，本机）**：纯 inspect 冷启动重放 ~102s（不含 seed 装置）。
这是当前实现的真实启动成本——正是快照要解决的问题（§12：第一版只优化启动，
不删除历史日志）。上界断言 150s（覆盖 CI 机器差异）。10⁶ 生成+重放 >300s，
CI 不可行，保留为手动基准（测试 `Skip`，理由引用 §21.6 + 第二版 compaction）。
