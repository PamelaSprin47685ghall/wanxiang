# KNOWN_BUGS — 万象（wanxiang）已知问题与缺口清单

> 本文档记录当前实现对照 AGENTS.md（SSOT，200 项决策）尚未解决的真实问题。
> 每条标注严重度、SSOT 决策引用、文件位置、现象与修复建议。
> 状态：`open` = 未修复；`partial` = 部分实现；`watched` = 低风险竞态，暂不处理。

---

## P1 — 功能缺口（首版完成定义要求但未实现）

### P1-1. PWA 无附件上传/下载入口
- **严重度**：P1（Q200 首版完成定义明确要求"PWA 能…上传附件"）
- **SSOT 引用**：决策 71-72、Q171-180、Q200
- **位置**：`src/Wanxiang.Pwa/wwwroot/app.js`（`grep attachment` = 0 处）、`src/Wanxiang.UI/MainWindow.fs`（桌面端同样无附件 UI）
- **现象**：服务端 `AttachmentStore` 与协议事件（`attachment.begin/chunk/complete`、`attachment.download-*`）已完整实现，但 PWA 与桌面客户端均无 UI/JS 入口调用之。msg 无法引用附件。
- **建议**：PWA 增加文件选择 → 分块（256 KiB）Base64 → `attachment.begin/chunk/complete`；成功后把 `AttachmentCommittedRef` 写入用户消息 payload；下载走 `attachment.download-request` 事件流。

### P1-2. 长会话历史分页未实现
- **严重度**：P1（Q127）
- **SSOT 引用**：Q127（"长会话按全局消息 commitID 反向分页；页面边界使用稳定 ID"）
- **位置**：`src/Wanxiang.Server/ServerModel.fs:conversationMessages`（一次性导出全部有效消息）、协议无分页事件
- **现象**：长会话快照/查询无分页，`ConversationSnapshot` 可能携带全部历史，造成大帧与内存压力。
- **建议**：协议增加 `history.request { conversationId, beforeCommitId, limit }` / `history.page`，服务端按 commitID 反向切片；快照仍全量（当前会话打开时），历史翻页走单独事件。

### P1-3. PWA 新版本刷新提示未实现
- **严重度**：P1（Q193）
- **SSOT 引用**：Q193（"静态资源版本变化后提示刷新；不在用户编辑或生成过程中强制 reload"）
- **位置**：`src/Wanxiang.Pwa/wwwroot/sw.js`（有 `skipWaiting()/clients.claim()`）、`src/Wanxiang.Pwa/wwwroot/app.js`（无 `updatefound`/`controllerchange` 监听）
- **现象**：Service Worker 更新时静默生效，用户无感知提示。
- **建议**：app.js 监听 `navigator.serviceWorker.addEventListener("controllerchange")` 与 `registration.onupdatefound`，检测到新版本时 toast"有新版本，点击刷新"（不强制 reload）。

### P1-4. 桌面 UI 缺流式文本渲染与取消/附件入口
- **严重度**：P1（Q200"流式展示"）
- **SSOT 引用**：决策 88-92（generationId、取消）、Q200
- **位置**：`src/Wanxiang.UI/MainWindow.fs`（`GenerationDelta` 分支仅置 `genStatus.Text`，不渲染流式文本；无 `GenerationCancel` 发送入口；无附件按钮）
- **现象**：桌面端生成期间看不到逐字输出，取消需依赖 PWA。
- **建议**：`GenerationDelta` 渲染临时消息气泡；工具栏加"取消生成"按钮发送 `generation.cancel`（带 `generationId`）；加附件上传按钮。

### P1-5. 断线重连不自动恢复观察
- **严重度**：P1（决策 26-27 仅约定"重连后重新 observe"，未要求自动；但桌面/PWA 均无自动重连）
- **SSOT 引用**：决策 26-27、Q136
- **位置**：`src/Wanxiang.Client/WsClient.fs`（`Closed` 事件暴露但调用方未实现自动重连）、`src/Wanxiang.Pwa/wwwroot/app.js`
- **现象**：连接断开后停留在断开状态，需用户手动点"连接"。
- **建议**：客户端加指数退避自动重连；重连成功后自动重新 `observe` 此前观察的会话并重置本地状态。

---

## P2 — 次级缺口 / 与 SSOT 的部分偏差

### P2-1. MCP 配置热更新不生效
- **严重度**：P2（决策 98）
- **SSOT 引用**：决策 98（"配置加载成功后立即切换注册表，旧子进程排空关闭"）、Q152
- **位置**：`src/Wanxiang.Server/ToolRegistry.fs:212-217`（`mcpClients` 用 `ConcurrentDictionary.GetOrAdd` 按配置快照缓存 `McpClient`，配置变更后继续用旧 command/args/env/maxConcurrency）
- **现象**：TOML 中修改 MCP 配置后，运行中的 `McpClient` 不更新；删除配置后新调用静默变空（工具消失无错误）。
- **建议**：`ToolRegistry` 每次 `BuildTools` 时比较最新配置与已缓存 `McpClient` 的配置；变化则停止旧客户端（排空后关闭，决策 98）并新建。

### P2-2. 附件 fileName 未清理、mediaType 未嗅探
- **严重度**：P2（Q175/Q176）
- **SSOT 引用**：Q175（"文件名绝不用于存储路径；需长度和字符清理"）、Q176（"媒体类型作为声明值保存，可轻量嗅探，冲突保留两者"）
- **位置**：`src/Wanxiang.Server/AttachmentStore.fs:59`（`Begin` 原样保存客户端 `fileName`）
- **现象**：fileName 直接入元数据（存储路径已用 SHA-256，无注入风险，但未做长度/字符清理）；mediaType 无嗅探。
- **建议**：`Begin` 内对 fileName 做长度截断（如 255）与不可打印字符清理；`Complete` 时可对前 512 字节做轻量 MIME 嗅探并存 `declaredType` + `sniffedType` 两字段。

### P2-3. 附件 blob 丢失无标记
- **严重度**：P2（Q179）
- **SSOT 引用**：Q179（"历史引用保留，查询中标记附件缺失，doctor 报告"）
- **位置**：`src/Wanxiang.Server/WsConnection.fs:385`（下载缺失只回 `ServerError "not found"`）
- **现象**：历史消息引用的附件文件被误删后，客户端无"附件缺失"标记；doctor 不报告引用缺失。
- **建议**：消息渲染时按引用 `sha256` 调 `AttachmentStore.Exists`，缺失时显示"附件缺失"；doctor 增加引用可达性检查（只读报告）。

### P2-4. chunk 大小上限未执行
- **严重度**：P2（Q172）
- **SSOT 引用**：Q172（"服务端可以在握手后公布上限"）
- **位置**：`src/Wanxiang.Server/AttachmentStore.fs:AppendChunk`（不校验单块字节数）、`ServerApp.fs:136`（`chunkSizeBytes` 配置解析了但未传入）
- **现象**：`network.chunkSizeBytes` 配置无效；服务端不拒绝超大 chunk。
- **建议**：`AttachmentStore` 构造接收 `chunkSizeBytes`，`AppendChunk` 校验 `base64` 解码长度 ≤ 上限。

### P2-5. 自动配对（Q190）不完善
- **严重度**：P2（决策 62-65、Q190）
- **SSOT 引用**：Q190（"TOML 保存带固定用途元数据的 hash，客户端侧保存对应原 token；任一侧丢失时创建新 token"）
- **位置**：`src/Wanxiang.App/Program.fs:216-247`
- **现象**：`server+client` 同进程时只写服务端 hash（`name = "wanxiang-desktop"`），**桌面客户端拿不到/不保存原 token**；`File.WriteAllText` 直接覆盖 TOML（绕过临时文件+rename+reload 原子路径）；首次启动 TOML 不存在时自动跳过配对。
- **建议**：桌面客户端令牌原文保存到本机客户端配置（如 `~/.config/wanxiang/client.toml`）；写 TOML 走 `ConfigStore.Rewrite` 原子路径；heuristic 判断"任一侧丢失"后重建。

### P2-6. 会话列表 runtimeState 不实时刷新
- **严重度**：P2（决策 28-30、Q125 摘要含"当前运行状态"）
- **SSOT 引用**：Q125
- **位置**：`src/Wanxiang.Server/WsConnection.fs`（`ConversationListSnapshot` 只在 observe 时发送；`ConversationUpdated` 广播后客户端需重新 observe 列表才刷新，PWA `conversation.updated` 分支仅对未观察会话触发 observe）
- **现象**：会话列表中"生成中"状态不随 `generation.started/finished` 实时更新。
- **建议**：`BroadcastCommit` 后对列表观察者推送 `conversation-list.updated`（轻量摘要），或在 `generation.*` 事件时同步推送列表摘要。

### P2-7. 附件下载丢声明元数据
- **严重度**：P2（Q176）
- **SSOT 引用**：Q176（"媒体类型作为声明值保存"）、决策 71-72
- **位置**：`src/Wanxiang.Server/WsConnection.fs:387`（下载时 `mediaType` 硬编码 `application/octet-stream`、`fileName = d.sha256`）
- **现象**：上传时 `AttachmentStore` 保存的声明 `mediaType`/`fileName` 在下载路径未回传，客户端收到的是通用类型与哈希文件名。
- **建议**：`AttachmentStore` 增加按 `sha256` 读取元数据的方法（或在消息引用中查找声明值），`AttachmentDownloadBegin` 使用真实声明值。

---

## P3 — 轻微 / 已 watch

### P3-1. await/appliedCursor 跨线程可见性（watched）
- **严重度**：P3（低风险竞态，客户端双端去重兜底）
- **SSOT 引用**：决策 33（游标语义）
- **位置**：`src/Wanxiang.Server/WsConnection.fs`（`awaitingCursor`/`appliedCursor` 被 recvTask 与 Task.Run 无锁读写）
- **现象**：`.NET` 内存模型不保证可见性，最坏读到旧游标起点 → 重发已确认批次；PWA `appliedAuthorityEvents` 与桌面 `commit.id > lastCommitId` 去重使重复发送安全。
- **建议（可选）**：加 `lock`（或 `Interlocked`）保护游标字段读写；当前不阻塞交付。

### P3-2. MCP stderr 密钥脱敏与子进程环境白名单
- **严重度**：P3（Q164/Q165）
- **SSOT 引用**：Q164（"已知密钥值必须脱敏"）、Q165（"敏感环境应尽可能白名单化"）
- **位置**：`src/Wanxiang.Server/ToolRegistry.fs:106-107`、`:95-101`
- **现象**：MCP 子进程 stderr 转发未对已知密钥替换；子进程继承父进程完整环境变量。
- **建议**：按 `providers.*.apiKey` 已知值做精确替换；环境按 TOML 显式配置 + 必要基础项构建。

### P3-3. MCP 排队调用取消后仍可能启动子进程（Q167）
- **严重度**：P3（Q167）
- **SSOT 引用**：Q167（"排队中的 MCP 调用被取消时是否启动子进程？不启动"）
- **位置**：`src/Wanxiang.Server/ToolRegistry.fs`（`McpClient.Request` 无取消感知；`ChatOrchestrator.ExecuteTools` 对全部 tool 直接 `Task.WhenAll`）
- **现象**：取消生成时排队中的 MCP 调用已开始执行（子进程已启动）。
- **建议**：`McpClient.Request` 接受 `CancellationToken`，取消未启动的排队调用。

### P3-4. Chunk 上限、附件 GC、快照分块等（设计内推迟项）
- **严重度**：P3（SSOT 明确推迟或留待压缩功能）
- **SSOT 引用**：决策 73（附件 GC 留待压缩）、Q134-135（快照分块）、决策 11（快照/压缩未来引入）
- **位置**：全局
- **现象**：附件不 GC（设计如此）；`ConversationSnapshot` 单帧携带全部消息（大会话有帧/内存压力）；无快照压缩。
- **建议**：随压缩功能一并设计。

### P3-5. MCP 同步握手可能阻塞会话生成
- **严重度**：P3（Q167 相关；MCP 子进程挂起时影响单会话，不跨会话）
- **SSOT 引用**：决策 96（本地 MCP 子进程按需启动）、Q167
- **位置**：`src/Wanxiang.Server/ToolRegistry.fs:111`（`ensureStarted` 内 `this.RequestRaw("initialize", ...) |> Async.RunSynchronously`）
- **现象**：`StartGeneration` 在 `lock rt` 内经 `BuildTools → GetMcpClient → ensureStarted` 同步执行 MCP initialize 握手；若 MCP 子进程挂起，会卡死该会话的生成并持 `rt` 锁。
- **建议**：初始化握手改为异步（`task` 链）或带超时；`ensureStarted` 不在锁内同步等待。

### P3-6. 桌面端会话列表摘要从不刷新
- **严重度**：P3（P2-6 的桌面端变体，影响更彻底）
- **SSOT 引用**：Q125
- **位置**：`src/Wanxiang.UI/MainWindow.fs:253-255`（`ConversationUpdated` 分支仅 `state.Handle ev` + `AdvanceCursor`，从不重新 observe 列表）
- **现象**：桌面端已观察会话的 `lastMessage`/`runtimeState` 摘要基本不更新（PWA 至少对未观察会话会 re-observe）。
- **建议**：与 P2-6 一并修复（服务端推送轻量列表摘要，或桌面端在 `ConversationUpdated` 时重新 observe 列表）。

---

## 已解决并回归验证的问题（历史记录，勿重复修改）

以下问题已在 2026-08-01 会话中修复并有测试覆盖，**不是** open 项：

| 问题 | 修复 |
|---|---|
| 游标推进死代码导致所有写命令被 `stale-projection` 拒绝（P0） | `WsConnection` 快照后建 `awaitingCursor`；`CursorAdvanced` 幂等推进（E2E 测试覆盖） |
| coordinator mailbox 自锁死锁（慢客户端 catch-up 在 mailbox 线程同步 `PostAndReply`） | `EnterSnapshotMode` 改 `Task.Run` 调度；守卫单会话释放 |
| catch-up 批次按观察范围过滤后 id 跳号导致客户端无限 re-observe（PWA） | PWA 校验放宽为严格递增；桌面 `AdvanceCursorTo` 按实际应用游标确认 |
| 配置热更新隐式取消在途生成（决策 87 违背） | `ChatOrchestrator.RebuildAgent` 同一 generation 重建 agent，不取消在途调用 |
| PWA fork 字段 `editedMessage` vs 服务端 `message` 不匹配导致断连 | PWA 改用 `message` 键 + `forkAfterId` 取消息 commitId；快照消息带 commitId |
| 会话列表摘要缺 lastMessage/runtimeState/config（Q125） | `ServerModel.conversationListItems` 补齐字段 |
| `CommandIdConflict` 未写 stderr（Q145） | `ServerApp.executeCommand` 增加 stderr 记录 |
| 桌面端不消费 `AuthorityCatchUp`（catch-up 死代码） | `MainWindow` 加分支；`ClientState.AdvanceCursorTo` |
| PWA catch-up 不识别 `message.deleted` 导致无法收敛 | `authorityCommitToEvents` 加删除分支 |
| 慢客户端被断开而非批式追赶（决策 32 违背） | `EnterSnapshotMode` 不再 `ForceClose`，改批式 catch-up |
