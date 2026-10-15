# 历史分页切口交接（paging-cutover）— 2026-10-15

分支：assist/deletion-cutover（含 d328fdc）。前序：历史分页与定位只读调查（结论见第 1 节）。

## 1. 调查结论：已接线

**库侧接线已完成，与草稿、reconnect 两切口一致；迁移表「未开始」再次过期。** 以当前调用路径为准：

- 翻页请求编排（RequestOlderHistory）在 AppShell 宿主；在途/超时/失败文案判定归 `Wanxiang2.OlderPageLoad`（fsproj:188，消费于 AppShell.fs 的 FailOlderPage / CheckOlderPageTimeout / 重试条）。
- watchdog `DispatcherTimer` 本体留宿主（olderPageWatchdog 字段 + EnsureOlderPageWatchdog）——符合 OlderPageLoad 库 AGENTS「计时器、请求、失败条留在宿主」的既定分工，不是欠账。
- 游标（pageEarliest / pageHasMore）归 compose/WsClient 的 ConversationView（从 snapshot 字段解析）；服务端分页在 compose/WsConnection → ServerModel.historyPageItems（稳定 commitID 页边界，Q127）。宿主只读。
- 定位：RevealTarget（fsproj:175）、滚动/高亮/前置锚点归 compose/ChatScreen；问答跳转 ExchangeJump 由 ChatScreen 接线；搜索命中 SearchHit parse 在宿主。
- FailOlderPage 失败条 UI 归 ChatPane（经 ChatScreen），断线收口在宿主 TearDownConnection。

### 职责归属摘要

| 职责 | owner | 已被消费？ |
|---|---|---|
| 翻页请求编排（HistoryRequest 发出、toast） | 宿主 AppShell | ✓ |
| 在途/超时/失败判定与文案 | Wanxiang2.OlderPageLoad | ✓ |
| watchdog 计时器本体 | 宿主（库 AGENTS 有意留宿主） | — |
| 游标解析 | compose/WsClient ConversationView | ✓ |
| 服务端分页 | compose/WsConnection → ServerModel | ✓ |
| 定位目标解析 / 滚动 / 高亮 / 跳转 | Wanxiang2.RevealTarget / ChatScreen / ExchangeJump | ✓ |
| HistoryPaging 能力声明与 unsupported 文案 | Wanxiang.Wire HostFeatures（label「加载更早的消息」） | 声明侧 ✓；**UI 门控本轮补上（见第 2 节）** |

## 2. 本轮改动：RequestOlderHistory 加 HostFeatures.HistoryPaging 门控

### 行为变化（单列）

- **变化**：主机明确声明了功能表（`host.capabilities`）且其中不含 `history-paging` 时，用户点「加载更早」（或搜索定位触发自动加载）从「发出注定无回应的 HistoryRequest、空等 `MotionLedger.olderPageTimeout` 超时后见到失败条」变为「点击即 toast 警告『主机版本较旧，不支持加载更早的消息。请更新主机后再试。』，不发请求」。
- **理由**：Wire 库的 `HostFeatures.label` / `unsupportedMessage` 文案与 `RequireFeature` 通道（AppShell.fs:769）都是现成的，Presets/Import/ContinueResponse/Export 已用同一通道；分页路径是唯一漏接的功能门。早失败早提示比让用户空等超时再失败更诚实。
- **触发条件（罕见路径）**：明确声明功能表、但功能表不含 history-paging 的较旧主机。`hostFeatures = None`（完全未声明功能表的旧主机）**行为不变**：照常放行、发出请求、靠超时兜底——这是 fs:58 注释记录的既定取舍，`RequireFeature` 的 `supports None → true` 语义正好承载它。
- **门控失败时的副作用边界**：不启动 watchdog、不置 pageLoading、不记 pageRequestedAt、不发 HistoryRequest；只走 toast（与既有 RequireFeature 用法完全一致的 Warning toast）。TryReveal 自动加载路径同样被门控覆盖（同一函数）；pendingReveal 保持原语义，不会额外循环。

### 改动形态

- AppShell.fs `RequestOlderHistory`：函数开头加 `if not (this.RequireFeature HostFeatures.HistoryPaging) then () else`，与 SaveConversationAsPreset（fs:776）的既有写法逐字同构。一个函数、一行级改动；不碰 vendor 库、无新包引用、无新文案。

## 3. 测试处理

AppShellTests.fs 既有反射模式（dispatchEvent / getField / setField / invokeMethod）可覆盖该门控，已补反例测试 `RequestOlderHistory gated when host declares features without history-paging`：

- 经 `ClientState.Handle(ConversationSnapshot)` 公开行为构造「还有更早历史」的活动对话（snapshotHasMore = true），反射设 activeConvId。
- dispatchEvent `HostCapabilities { features = [Search] }`（不含 history-paging）→ 调 RequestOlderHistory → 断言 pageLoading = false、pageRequestedAt = None（门控挡住，未发请求）。
- 再设 hostFeatures = None → 再调 → 断言 pageLoading = true、pageRequestedAt = Some（旧主机照常放行，对照分支）。

**未运行**：本轮不跑测试/git（按托付）。测试断言的观察点（pageLoading / pageRequestedAt 字段、HostCapabilities 事件分支 fs:1701、RequireFeature 语义）均已读源核对；编译与运行结果待 DevOps 验证。

## 4. 验证（DevOps 已执行，全部通过）

- `python3 -B -m unittest discover -s tools/tests -p test_wanxiang2_feed.py -v`：17/17（exit 0）。
- `tools/wanxiang2-feed.sh`：feed ready（pin `c3a2e74`，316 包，exit 0）。
- `python3 tools/tag_ui_tests.py --check`：一致，UI 标注方法 290→291，新测试被正确识别为 UI（exit 0）。
- 针对性 `Wanxiang.Tests -class "*AppShellTests*"`：8/8 通过（原 7 + 新 1，exit 0）。
- `./run-tests.sh -j 4 --large-history`：UI 4 进程 135+95+44+72、非 UI 225，0 失败，exit 0；清点 NonUI=222 / UI=346 / LargeHistory=3。
- `dotnet build wanxiang.slnx -c Release`：0 错误（exit 0）。
- `dotnet format wanxiang.slnx --verify-no-changes`：exit 0。
- `git diff --check`：干净。

**红/绿验证（DevOps 执行）**：临时注释掉 AppShell.fs 的门控行（RED-PROBE）后重构建并跑 AppShellTests——`Failed: 1`（exit 1），失败的正是新测试第一组断言（门控缺失时 pageLoading 被置位），证明测试真能抓旧行为；恢复门控行后重跑 8/8 通过（exit 0）。源码已恢复原状（diff 与 Engineer 交付一致）。

**基线同步**：UI 用例数 345→346（新增 1 个 UI 测试）。门禁只拦「低于基线」故未报红，按决策 381「新增测试后可上调」把 `tests/test-counts.baseline` 的 `UI=345` 上调为 `346`，随本轮提交。

**未覆盖**：第 3 节末条「行为抽查：连一台真实旧主机」属真实主机路径，本轮未执行（无浏览器/主机冒烟，按托付本轮未改浏览器桥）；该路径的自动化证据即上述新测试。

**偶发记录（诚实交代）**：基线上调后的确认重跑中，ui-group-03（72 用例组）出现过一次 `Failed: 1`（exit 1），失败测试名未捕获（输出被截尾）；随后 3 次重跑（含 2 次 UI-only）全部 0 失败。同一二进制在改动前一轮全绿、改动后多轮全绿，单次失败未复现，与 run-tests.sh 头注释记载的 headless 装置偶发竞态特征一致。未定位到具体 hidden input，不宣称已修复，仅记录在案。

## 5. 下一候选

- **发件箱与命令反馈**（任务指定的下一候选）：Outbox / CommandFeedback 的宿主内收口或文档债核对，同款「调查 → 唯一小实事」流程。
- 文档债（次选，未动）：vendor AGENTS.md 迁移状态表「UI 壳层 AppShell 余项（…OlderPageLoad…）」一行「未开始」已过期，实际已消费；更新需在独立 checkout 决定，本轮不动 vendor 树。
