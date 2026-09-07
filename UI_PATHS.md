# UI_PATHS — 万象前端稳定性证明材料

> 不是任务列表，而是系统稳定性的证明。截图发现问题即 FAIL，架构脆弱即 FAIL，不靠补丁成立。

## 0. IA（信息架构）

- **核心任务**：连接服务器 → 选/建会话 → 发消息 → 看流式/工具/错误 → 改配置/分叉/导出。浏览+监控为主，编辑为辅，危险操作（删除会话/消息/服务商/MCP）必须二次确认。
- **第一眼**：侧栏会话列表 + 聊天阅读列 + 底部输入区。**第二眼**：顶栏标题/模型/生成状态。**第三眼**：设置分区、工具细节、诊断报告。
- **常驻**：侧栏搜索+新建、聊天顶栏、输入区、模型 Chip。**按需**：思考折叠、工具参数/结果、错误技术细节、附件丢失条、归档列表、MCP/服务商编辑器。
- **显性 vs 菜单**：发送/停止、新建、重连显性；重命名/置顶/归档/分叉/导出进右键菜单；模型切换用 Chip 就近，不进设置。

## 1. Spatial Model（空间模型）

- **主壳 Grid**：`workspace`（聊天三列）与 `settingsHost`（全屏设置）互斥，`OverlayHost` 独占最顶 5 层。唯一空间投影器 `MainLayoutController.Apply(NavigationSnapshot)`，禁止散落改列宽。
- **三列**：Col0 Sidebar（232–420，可折叠→0），Col1 Splitter 4px，Col2 chatColumn Star。Compact<720 时单列 + Sidebar 变 ZIndex=2 抽屉覆盖，不压主内容。
- **聊天列 Dock**：Header Top 52px 常驻，Composer Bottom 常驻，中间 `scroller` 唯一获得剩余高度并滚动。阅读列 MaxWidth 748，用户气泡再收至 560。
- **滚动归属**：主聊天 scroller 纵向唯一；侧栏 ListBox 虚拟化纵向；消息内详情用限高 `detailViewport`（360）内部滚动；代码块横向仅在不折行时；附件草稿纵向 Max104；设置内容纵向；Dialog/Toast/Menu 各自限高内部滚动。禁止无意义 nested scroll。
- **层**：base workspace → sticky header/composer → compact drawer → scrim → dialog → popupCatcher → popup → toast → PWA DOM（SW 提示放顶部，与 Canvas toast 底部错开）。

## 2. Layout Contract

- **Width**：viewport 390–1440+，断点 720（compact）、440（表单双→单）。Sidebar 284 preferred，阅读列 748，设置内容 980，弹窗按 420/460/520 语义取 `dialogContentMaxHeight` 560 限高 + viewport clamp。长文本 wrap，超长链接 40 字符分块，表格 Star 列 wrap 不横滚（保阅读列同宽），代码块折行开则 wrap 关则横滚。
- **Height**：bar 52，输入 Min26 Max240，附件草稿 Max104，详情展开 Max360，Toast 正文 Max160。Header/Composer 固定，剩余全给 scroller。`messageEndBreathing` 48 放 messagePanel Margin（ScrollViewer Padding 不进可滚动范围）。
- **Overflow**：x 一律 Disabled 除代码块（Auto）；y Auto + 限高；浮层 `fitDialog/fitPopup/fitToastsToViewport` 按 `root.Bounds-2*inset` clamp，popup 支持右对齐 + 翻转（flipUp）+ clamp；禁止 `overflow:hidden` 掩盖结构问题。
- **Geometry invariants**：hover/selected/focus 用 outline/inner shadow/opacity，不改外部尺寸；焦点环用 `focusRingSpread` 外阴影；按钮 pending 用 `pendingActionMinWidth` 96；动作出现用 `setReservedActionVisible` 保槽位（generatingChip、clearSearch、more）；基线用 `iconBaselineNudge` 2.0；不透明度走 Tokens 阶梯。

## 3. Component Governance

- **Container 管空间，Component 管内容**：MainLayoutController 唯一改 Grid；子组件不设 viewport magic、不做页面级 fixed/absolute。`Ui` 工厂统一按钮/输入/徽标/开关，`Ui.hairline()` 统一 1px 分割，`Tokens` 唯一视觉来源，`Palette` 唯一色板，CSS 变量已对齐（border #D9D5C7/#E3E0D3/#C7C3B3，radius 6/9/14，行高 1.62）。
- **Overlay**：单 `OverlayHost` 自绘，scrim 点关 dialog，catcher 点关 popup，滚轮关 popup（防锚点漂移），Tab 陷阱 + 焦点记忆恢复 + Escape 优先关顶层 + 全局快捷键拦截。单 popup 排他：不支持二级嵌套，等待设计再扩展。
- **Markdown**：块级真控件，行内/代码/表格/公式/任务列表全覆盖，解析异常降级纯文本；远程图片默认不请求，显示来源域；流式 `allowHighlight=false`，后台 Jint/Browser 异步着色 + 120ms 防抖。

## 4. Paths（每条含目标/入口/步骤/状态/截图/结果/风险）

### P1 连接与配对
- 目标：连上 Linux S。入口：启动空态/侧栏状态行。步骤：填 URL → Token 或 6 位码 → 等 `pairing.succeeded`。状态：NotConnected/已连。截图：`.scratch/verify-round4/*-01-connected.png`（12 矩阵全含）。结果：PASS（matrix 12/12 ok，0 console/pageerror）。风险：loopback 与远程同一 WS 路径，已由决策48保证。

### P2 新建会话并首发消息
- 目标：打开即打字。入口：WS 建 `QA <tag>` 空会话 → 点首行选中（像素变化断言，重试 3 次）。步骤：选中后空态面板 + 可用输入区。状态：EmptyConversation。截图：`.scratch/verify-round4/*-02-new-conversation.png`。结果：PASS（12/12 与 01 逐字节不同；空态面板+输入区+模型 Chip 正常）。风险：compact 下抽屉行点击曾变成“选中旧会话”，现已改走 WS 建会话+首行选中+像素断言。

### P3 长回答 Markdown 全集
- 目标：代码+表格+公式+列表稳定。入口：WS 发 `LONG`（mock），等 `generation.started` 900ms 截流式，完等 `finished` 截终态。状态：streaming/loaded。截图：`.scratch/verify-round4/*-03-streaming.png`、`*-04-markdown.png`（含 390x844 窄屏）。结果：PASS（列表/引用/表格/代码高亮/行内+块级 KaTeX/用量脚注全渲染；390 geometry true 无横向撑破；1440 人工核对无错位/双边框/截断）。风险：多列表格窄屏压缩（wrap 策略，star 不横滚），已在 Contract 声明。

### P4 工具调用
- 目标：看懂调了什么。入口：WS 发 `TOOL`（会话预置 `builtin:echo`），等两轮 `finished`。状态：toolCallCard 限高/全文。截图：`.scratch/verify-round4/*-05-tool-call.png`。结果：PASS（人工核对：echo 卡已完成+参数 JSON+总结气泡+用量，1440 无跳动；detailViewport 限高不推主视口）。风险：嵌套 scroller 测量滞后，`ScrollToEndDeferred` 3 次重试兜底。

### P5 错误与重试
- 目标：失败可恢复。入口：WS 发 `FAIL`，等 `status=failed` 终态。状态：errorCard。截图：`.scratch/verify-round4/*-06-error-card.png`。结果：PASS（人工核对：人类可读错误+重试按钮+技术细节入口；文案把上游 500 写成“无法连接”略欠准，结构码+可重试位正确，记 copy nit 不判 FAIL）。风险：错误细节限高 360，内部滚动不推主视口。

### P6 设置全分区
- 目标：不改 TOML 改配置。入口：齿轮（compact 先开抽屉；点击带像素断言，3 次无变化则走抽屉回退路径）。状态：settingsHost 全屏。截图：`.scratch/verify-round4/*-07-settings.png`（明暗双主题；07 与 03–06 逐字节不同已由 matrix 内容门禁保证）。结果：PASS（1440-dark/light 全绿）。风险：分区缓存复用实例，`invalidate` 重建防 visual-parent 冲突。

### P7 响应式与缩放矩阵
- 目标：12 矩阵全绿。入口：`qa_matrix.js`（前置 `qa_provision.js` 配 mock）。步骤：1440明暗/1240/900明暗/900矮/719vs721/540/390/scale125/150 跑 7 态。状态：compact/wide。截图：`.scratch/verify-round4/`（84 态 + 12 probe）+ matrix JSON。结果：PASS（verify-round4 `ok:true`，12/12 sevenStates+noErrors+geometry+dpr+distinct；`dotnet test` 257/257 全绿；sln 0E（仅 2×NU1903：Tmds.DBus 0.90.3 已知漏洞提醒，见下）。风险：PWA 虚拟键盘改 viewport 高，`fitPopupToViewport` 重测兜底。
- 反证史：verify-round1 的 `ok:true` 作废——当时无 provider，LONG/TOOL/FAIL 零提交，03–07 全与 07-settings 同字节（matrix 只验存在性）。教训已固化为两道门：`qa_provision.js`（无 mock 不跑 matrix）+ matrix 内容门禁（03–06 全同 07 即 FAIL）。

## 5. 本轮已做结构修复（根因层）

- PWA CSS 对齐 Palette SSOT：border/line 色、radius 9/14、行高 1.62、hairline 变量；SW DOM toast 移顶部，与 Canvas toast 底部错层。
- Sidebar 底部分割统一 `borderSoft`；Composer 分割改 `Ui.hairline()`，聚焦环统一 `focusRingSpread`。
- `generatingChip` 改保留槽位（Opacity 占位），标题不再被挤压导致 ellipsis 跳变（scale150 流式截图可见 chip 常驻不挤标题）。
- 新增 `iconBaselineNudge` + 5 档 opacity tokens，替换 10+ 处 magic numbers。
- Overlay 滚轮关闭 popup，消除滚动锚点漂移。
- 桌面启动崩溃：删 `Wanxiang.App` 对 `Tmds.DBus.Protocol 0.95.0` 的直引（库内零引用；0.95 把 `Connection` 改名 `DBusConnection`，Avalonia.FreeDesktop 要 0.90.3，TypeLoad 直接炸客户端）。回落传递 0.90.3，能跑优先；NU1903 提醒留着（本地托盘 IPC，无远程暴露）。
- PWA 高 DPR 响应式错误：900 CSS @1.5 进 compact 抽屉（有效布局宽=CSS/DPR；1000 CSS @1.5 同样抽屉；420 封顶数学吻合）。修法：`AppShell` 响应式改认 JS `innerWidth`（`wxViewportWidth` 桥 + 500ms 轮询覆盖，桌面/测试走 Bounds 原路径）。已验证：900@1.5 回到三栏（边 639px=284×1.5），12 矩阵全绿。残留：该 DPR 下单元系仍按 CSS/DPR 走，比例偏大但一致可用；真修需动 framework 缩放，不在本轮。
- QA 链硬化（测试辅助）：`qa_provision.js`（配 mock+探活，幂等）；`qa_shot.js` 改协议驱动（WS 建会话/发消息/等终态）+ UI 点击像素变化断言（3 次无变化即抛）；`qa_matrix.js` 加内容门禁（03–06 全同 07 即 FAIL）+ 跑前清挂起 chrome；`qa_shot` 关浏览器 10s 超时放手。
- 骨架屏落地：ChatView 换会话过渡引入平滑骨架屏（头像槽位 + 助手/用户气泡占位 + 呼吸动画），杜绝白屏与“加载中…”生硬跳变。
- Emoji/ZWJ 切片安全：PlainText.summarize 与 MarkdownRenderer 安全长链接分块迁移至 StringInfo 字素簇（TextElement），彻底杜绝代理对（surrogate pair）与修饰符切断。
- FAIL 错误文案精准化：细分 HTTP 5xx 上游错误（「服务商」服务暂时不可用（HTTP 5xx））与物理网络断开（无法连接「服务商」，请检查网络或地址），保持协议 code 与 retryable 兼容。
- 侧栏时间桶动态准确化：会话追踪 lastActivityAtUtc 并通过 updatedAt 下发，使 Sidebar 分组准确按最近活动更新（“今天”、“昨天”等）。
- Composer 附件交互强化：支持文件拖拽悬停反馈（DragDrop Copy 状态）及系统剪贴板文件/截图粘贴上传。

## 6. 已知未做（下一轮入口）

- ChatView 非全虚拟化：百条以上重 Markdown + KaTeX 常驻，Measure 压力；当前 LCP+CardKey O(1) 已够用，全量虚拟化需大改，留待性能实测触发。
- 单 popup 排他：二级子菜单/悬停预览会顶替上级，需设计后再扩展。
- 高 DPR 单元系：响应式断点已对（CSS px），但布局单元仍按 CSS/DPR 走，HiDPI 下相对比例偏大；要根治需修 Browser 后端缩放（framework 层），本轮只修到“模式正确+可用”。
- QA 残留污染：各轮共享服务端攒下几十条 `QA *`/`新会话` 空会话，侧栏 N-data 很好但会越攒越多；下轮 matrix 前重建数据目录（provision 已支持干净启动）。

## 7. Verification（每次重要修改后执行）

- `dotnet test tests/Wanxiang.Tests` 全绿（257；重点 UiStability/Incremental/Pathological/ScrollLayout/FocusRing）。
- `python3 tools/mock_openai.py --port 8799` + `node tools/qa_provision.js`（mock upsert+探活 PASS）后跑 `node tools/qa_matrix.js` 12 矩阵：7 态完备 + 0 console/pageerror + canvas==viewport±0.75 + body 无越界 + DPR 匹配 + 内容 distinct。
- 截图人工抽核清单：横向滚动/遮挡/黑边/双边框/错位/截断/浮层/空白异常/layout shift（本轮已核 1440 markdown+tool+error、scale150 streaming、719 compact）。
- `node tools/e2e_smoke.js` 全链路（mock 8799）：配对→建会话→流式→工具→错误→重生成→配置（空数据目录需先 provision）。
