# KNOWN_BUGS — 万象（wanxiang）已知问题与缺口清单

> 本文档记录当前实现对照 skill://wanxiang（SSOT，200 项决策）尚未解决的真实问题。
> 状态：`open` = 未修复；`partial` = 部分实现；`watched` = 低风险竞态，暂不处理。

---

## 当前状态：**0 项 open（2026-08-01 全量修复完成）**

原 P1-1 至 P3-6 共 18 项已全部处理完毕，全部移入下方历史记录。后续发现的新问题按同样格式追加。

---

## 已解决并回归验证的问题（历史记录，勿重复修改）

### P1 — 功能缺口（首版完成定义）

| 问题 | 修复 |
|---|---|
| P1-1. PWA 无附件上传/下载入口（Q200） | `app.js` 增加文件选择 → SHA-256 → 256 KiB 分块 Base64 → `attachment.begin/chunk/complete`；`attachment.committed` 后引用随下一条用户消息写入（contents 内 `{"type":"attachment",...}`，透明映射不破坏 replay）；消息渲染附件为可点击下载链接（`attachment.download-request` 事件流 → Blob → `<a download>`）；浏览器实测上传/下载/落盘文件名完整。桌面端同步实现（P1-4）。 |
| P1-2. 长会话历史分页未实现（Q127） | 协议新增 `history.request {conversationId,beforeCommitId,limit}` / `history.page {items,hasMore}`；服务端按 commitID 反向切片（`ServerModel.historyPageItems`，limit 钳制 ≤200）；快照只携带尾部 200 条并返回 `snapshotEarliestCommitId`/`snapshotHasMore`；PWA 滚动到顶触发分页（保持滚动位置），桌面端 `ScrollViewer.ScrollChanged` 同逻辑；`ClientState.Handle(HistoryPage)` 前置去重拼接。E2E 覆盖。 |
| P1-3. PWA 新版本刷新提示未实现（Q193） | `sw.js` 移除自动 `skipWaiting()`，改为收到 `SKIP_WAITING` 消息才激活；`app.js` 注册 SW 并监听 `updatefound`/`statechange`，新版本 installed 时 toast「有新版本，点击刷新」，点击后 `postMessage(SKIP_WAITING)` → `controllerchange` → reload；不在编辑/生成中强制刷新。 |
| P1-4. 桌面 UI 缺流式文本渲染与取消/附件入口（Q200） | `GenerationDelta` 累积文本渲染临时助手气泡（`streamText` + 重绘）；工具栏「取消生成」按钮携带 `generationId` 发 `generation.cancel`；「附件」按钮走 Avalonia `StorageProvider` 文件选择 → 分块上传 → 引用随消息写入；消息附件可点击下载（`SaveFilePickerAsync` 保存）；生成中发送/附件按钮联动禁用。 |
| P1-5. 断线重连不自动恢复观察（决策 26-27） | 桌面端 `Closed` 后指数退避（1s→30s）自动重连，成功后重新 observe 列表与活动会话；PWA `onclose` 同样退避重连（`ws` 引用守卫防止旧连接误触发），`auth.accepted` 恢复观察并重置退避。浏览器实测杀服务→重启→自动恢复。 |

### P2 — 次级缺口 / 与 SSOT 的部分偏差

| 问题 | 修复 |
|---|---|
| P2-1. MCP 配置热更新不生效（决策 98） | `ToolRegistry` 缓存客户端时记录配置指纹（command/args/env/url/maxConcurrency）；配置变化或删除时 `Stop()` 旧客户端（在途 pending 调用以失败结果结束、信号量释放、排队调用抛 `ObjectDisposedException` 转失败结果）并新建；新调用立即使用新配置。 |
| P2-2. 附件 fileName 未清理、mediaType 未嗅探（Q175/Q176） | `AttachmentStore.Begin` 对 fileName 做长度截断（255）与不可打印字符清理；`Complete` 时对前 512 字节做轻量 MIME 嗅探（PNG/JPEG/GIF/WEBP/PDF/ZIP/文本），声明值为空或 `application/octet-stream` 时采用嗅探结果；显式声明不被覆盖。测试覆盖。 |
| P2-3. 附件 blob 丢失无标记（Q179） | 下载返回 `server.error "attachment ... not found"` 时，PWA/桌面端将对应引用渲染为「附件缺失（原文件已删除）」并 toast；`doctor` 新增只读附件引用可达性检查（replay 投影扫描 `attachmentRefsOf`，报告缺失 blob），实测 doctor 输出 `attachments OK/FAIL`。 |
| P2-4. chunk 大小上限未执行（Q172） | `AttachmentStore` 构造接收 `chunkSizeBytes`（`ServerApp` 传入配置值），`AppendChunk` 校验解码后长度 ≤ 上限；超限返回 ValidationError 并中止上传。测试覆盖。 |
| P2-5. 自动配对（Q190）不完善 | 重写配对块：TOML 不存在时先经 `ConfigStore.Open` 原子生成默认配置；客户端令牌原文写入 `~/.config/wanxiang/client.toml`（url + token）；服务端只存 SHA-256 哈希；写 TOML 走 `ConfigStore.Rewrite` 原子路径（临时文件+rename+reload）；「任一侧丢失」启发式（client.toml 令牌哈希不在服务端授权列表 → 重建双方）。实测：首启生成 client.toml，哈希与令牌一致，桌面端启动自动读取并连接。 |
| P2-6. 会话列表 runtimeState 不实时刷新（Q125） | `ConnectionRegistry.BroadcastTransient` 对 `generation.started/finished` 向列表观察者推送轻量 `conversation.updated {change:{runtimeState}}`（决策 28-30/Q125 摘要语义）；PWA `conversation.updated` 无条件重新 observe 列表（轻量摘要）；列表项渲染显示「● 生成中」与 lastMessage。E2E 覆盖。 |
| P2-7. 附件下载丢声明元数据（Q176） | `AttachmentStore.Complete` 将解析后的元数据（mediaType/declaredMediaType/sniffedMediaType/fileName/size）落盘 `<hash>.meta`；新增 `Metadata(sha256)`；下载路径用真实声明值回传 `attachment.download-begin`。E2E 覆盖（PNG 声明 octet-stream → 下载回传 image/png + 原名）。 |

### P3 — 轻微

| 问题 | 修复 |
|---|---|
| P3-1. await/appliedCursor 跨线程可见性（watched） | `WsConnection` 新增 `cursorLock`，`appliedCursor`/`awaitingCursor`/`advertisedCursor`/`snapshotMode` 的读写全部纳入锁保护（含 `ShouldReceiveAuthority`、`EnterSnapshotMode`、`SendCatchUp`、`CursorAdvanced`、`ObserveConversationList`、`PushTransient`、Command 游标读取）。 |
| P3-2. MCP stderr 密钥脱敏与环境白名单（Q164/Q165） | `ServerApp` 增加 `redactSecrets`（按 `providers.*.apiKey` 已知值精确替换为 `***`），MCP 子进程日志转发经其处理；`McpClient` 子进程环境白名单化（PATH/HOME/LANG/TMPDIR/TZ + TOML 显式 `env`），不再继承完整父环境。 |
| P3-3. MCP 排队调用取消后仍可能启动子进程（Q167） | `McpClient.Request/Call` 接受 `CancellationToken`（经 AIFunction 透传生成取消令牌）；`semaphore.WaitAsync(Timeout.Infinite, ct)` 使排队中未启动的调用在取消时直接返回 `{"error":"cancelled"}`，不启动子进程；`Stop()` 释放信号量使后续排队调用快速失败。 |
| P3-4. Chunk 上限、附件 GC、快照分块等（设计内推迟项） | 维持 SSOT 设计：附件 GC 留待未来压缩功能（决策 73）；快照大帧问题由 P1-2 的尾部截断 + 历史分页解决（不等同于 Q134 的 begin/item/end 分块，但消除了单帧携带全部历史的内存压力）；无快照压缩仍为设计内推迟。**结论：按设计不实现，条目移除。** |
| P3-5. MCP 同步握手可能阻塞会话生成（Q167 相关） | `RequestRaw` 支持超时参数；`ensureStarted` 的 initialize 握手带 15s 超时（挂起子进程不会无限阻塞 `lock rt` 内的 `BuildTools`）；超时清除 pending 并返回 `{"error":"mcp request timeout"}` 转失败 Tool Result。 |
| P3-6. 桌面端会话列表摘要从不刷新（Q125） | 桌面端 `ConversationUpdated` 分支增加重新 observe 列表；列表项渲染 title + 「● 生成中」 + lastMessage 摘要（与 P2-6 服务端推送配套）。 |

### 更早历史（2026-08-01 首轮会话）

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
