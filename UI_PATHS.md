# UI_PATHS — 万象 UI Stability System 地图与验证协议

> Phase 0 产物。任何 UI 任务开始前先读本文件；改动后按 §6 验证协议回归。
> 上游 SSOT 见 `.agent/rules/wanxiang-ssot.md` 与 `.agent/skills/wanxiang/SKILL.md`；
> 本文件只覆盖 UI 层的结构与稳定性，与 SSOT 冲突时以 SSOT 为准。

## 1. 技术形态

- **语言/框架**：F# + Avalonia 12.1（Fluent 模板 + 全自绘视觉层），.NET 10。
- **双宿主，单棵 UI 树**：`Wanxiang.App`（Linux x64 桌面窗口）与 `Wanxiang.Pwa`
  （browser-wasm，`SingleViewApplicationLifetime`）共用 `Wanxiang.UI` 全部代码（SSOT 决策 48）。
  桌面与浏览器之间没有第二套行为路径；本机连接也走 loopback WebSocket。
- **无 XAML**：全部视图用 F# 代码构建（`Build()` 方法 + 代码式布局）。
- **无路由**：导航是「workspace ↔ settingsHost 显隐切换 + NavigationController 状态机」，
  不存在 URL 路由层；会话切换是数据选择（`activeConvId`），不是页面跳转。

## 2. 目录地图（UI 相关）

```
src/
  Wanxiang.UI/                  ← 全部共享 UI（本项目 UI 稳定性的主战场）
    App.fs                      ← Application：Fluent 压制、TextBox chrome 扁平化、入口装配
    AppShell.fs                 ← MainView：组装点 + 协议事件处理 + 全部业务动作（1487 行，最大文件）
    CredentialStore.fs          ← 桌面 client.toml / 浏览器 IndexedDB 凭据
    Design/
      Tokens.fs                 ← 视觉常量唯一来源（间距/圆角/字号/画笔/阴影/字体）
      Palette.fs                ← 明暗两套调色板，结构相同只换值
      Icons.fs                  ← 矢量图标 glyph
      LayoutPolicy.fs           ← 断点与空间政策（compactBreakpoint=720 等）
      Metrics.fs                ← ReadingRhythm / ControlMetrics / ContentMetrics 语义尺寸
      MotionPolicy.fs           ← 动效策略（reduced motion、时长表）
    Controls/
      Primitives.fs             ← Ui 模块（button/iconButton/textField/toggle/spinner…）+ ActionBorder/ToggleBorder
      OverlayHost.fs            ← 对话框/浮层/Toast 统一宿主（scrim、dialogStack、焦点记忆）
      Menu.fs                   ← 浮层菜单（分组、键盘导航、滚动钳制）
      MarkdownRenderer.fs       ← Markdown → Avalonia 控件（代码高亮、KaTeX、表格、链接）
    Rich/
      RichBackend.fs / Highlight.fs      ← highlight.js 进程内执行
      KatexHtml.fs / MathLayout.fs / MathVisual.fs ← KaTeX 公式排版
    Model/
      UiControllers.fs          ← ConversationRuns/AttachmentDraft/ComposerDrafts/MessageOutbox/
                                   CommandFeedback/ConfigFeedback/NavigationController/ShortcutRouter
      UiPrefs.fs                ← 本机偏好（主题/字号/折行/侧栏宽度/减少动效）
      MessageModel.fs / Markdown.fs / Catalog.fs / ConversationSummary.fs /
      ProviderPresets.fs / MediaTypes.fs / ConversationExport.fs
    Views/
      MainWindow.fs             ← 桌面窗口壳
      MainLayout.fs             ← MainLayoutController：导航状态 → Grid 列宽/可见性的唯一投影器
      Sidebar.fs / ChatView.fs / Composer.fs / MessageCard.fs
      SettingsView.fs / SettingsProviders.fs / SettingsTools.fs / SettingsGeneral.fs
      Dialogs.fs / Brand.fs / ExportDialog.fs
  Wanxiang.App/                 ← 桌面入口（RichJint.fs 提供进程内 JS 引擎）
  Wanxiang.Pwa/                 ← browser-wasm 入口
    wwwroot/                    ← index.html / main.js / app.css / sw.js / manifest
tests/Wanxiang.Tests/           ← 336 个测试（详见 §4）
tools/
  mock_openai.py                ← 无密钥 mock（三种协议；触发词 TOOL/REASON/LONG/FAIL）
  e2e_smoke.js                  ← WS 协议端到端冒烟
  qa_provision.js               ← QA 前置：给服务端写 mock provider
  qa_shot.js                    ← 7 态截图 + 几何门禁 + resize sweep
  qa_matrix.js                  ← 12 视口/主题/缩放组合的发布矩阵
  build_fonts.py / build_webassets.js / patch_avalonia_js.py / katex_font_rename.py
```

## 3. 关键不变量（改动时不得破坏）

1. **视觉常量只出自 `Tokens`**：视图里不得写死颜色/字号/间距。断点只出自 `LayoutPolicy`，
   语义尺寸只出自 `Metrics`。
2. **列宽唯一投影器**：主壳 Grid 的列宽/可见性/ZIndex 只能由 `MainLayoutController.Apply`
   写入；`AppShell` 只负责把拖拽结果存回 prefs。
3. **导航状态唯一持有者**：`NavigationController`（纯状态机）持有 compact/collapsed 状态；
   视图只消费 `NavigationSnapshot`。
4. **主题切换不重建视图树**：画笔是可变实例，`Tokens.apply` 原地改色；
   非画笔量（阴影/字号）订阅 `Tokens.Changed` 重建。
5. **Overlay 单宿主**：对话框/浮层/Toast 全部画在 `OverlayHost`；Esc 处理顺序
   toast → dialog → popup → 设置 → 抽屉 → 停止生成。
6. **焦点有家**（D3/D4）：抽屉开→搜索框；抽屉关/会话打开/发送/停止→Composer；
   设置关→触发控件（失效则 Composer）。任何新交互必须指定焦点归宿。
7. **桌面最小宽 700 < compact 断点 720**（契约 C7）：窗口缩到最窄必须能进 compact 抽屉分支。
8. **浏览器断点只认 CSS 视口**：Browser 后端高 DPR 下 `Bounds.Width` 不可信，
   用 `wxViewportWidth` 轮询（`hostViewportWidth`）覆盖；桌面走 Bounds 原路径。
9. **发送是原子消费**：附件草稿有 uploading 时拒绝发送；发送先 Stage 进 outbox，
   `command.committed` 才释放；重试复用原 invocationId。
10. **无几何动画**（MotionLedger）：状态表达只切透明度/颜色，不改尺寸，避免布局跳动。

## 4. 测试体系（当前基线：336 通过 / 0 失败）

| 文件 | 覆盖 |
|---|---|
| `Headless.fs` | Avalonia Headless + Skia（真实字形度量）；**禁用测试并行**（线程亲和） |
| `UiStabilityTests.fs` | 布局稳定性核心：阅读列宽、焦点环、对话框/浮层视口钳制、composer 附件槽位、侧栏 20k 行虚拟化、长标题不挤出 header、缩放等价视口、toast 钳制、对比度 |
| `ScrollLayoutTests.fs` / `IncrementalRenderTests.fs` / `RenderCostTests.fs` / `PathologicalRenderTests.fs` | 滚动锚点、增量渲染、渲染成本上限 |
| `MathRenderTests.fs` / `MathBaselineTests.fs` | KaTeX 公式排版与基线 |
| `AsyncHighlightTests.fs` / `FocusRingTests.fs` / `ReconnectBackoffTests.fs` | 异步高亮、焦点环、重连退避 |
| `InteractionReliabilityTests.fs` / `DeliveryPolishingTests.fs` / `CraftsmanshipPolishTests.fs` / `PolishTests.fs` / `ProductizationTests.fs` | 交互可靠性、投递状态机、工艺打磨 |
| `ConversationExportTests.fs` / `BugfixTests.fs` 等 | 导出语义、回归修复 |
| 其余（Store/Replay/Codec/Config/ServerE2e…） | 非 UI 层，不在本文件范围 |

**已知波动**：全量套件首次冷启动运行曾出现 50 个 UI 测试因
`PresentationSource..ctor`（Window 构造）批量失败；立即重跑即全绿，连续 4 次全绿。
冷启动 Skia 字体初始化疑似诱因。协议：基线判定以「连续 2 次全绿」为准；
若改动后出现同类批量失败，先原样重跑一次再定论。

## 5. 端到端视觉验证（浏览器真实渲染）

```bash
# 1) mock 模型服务（8799）
python3 tools/mock_openai.py --port 8799 &

# 2) 起服务端（干净数据目录），stderr 日志给配对码
./start.sh /tmp/qa-config.toml /tmp/qa-data 2>/tmp/wanxiang.log &

# 3) 写入 mock provider（幂等）
node tools/qa_provision.js

# 4) 七态截图 + 几何门禁（canvas 铺满视口、body 无滚动溢出、console 无错误）
node tools/qa_shot.js --tag now --out .scratch/shots

# 5) 12 组合发布矩阵（1440/1240/900/719/721/540/390、明暗、1.25/1.5 缩放）
node tools/qa_matrix.js
```

七态：01 已连接 / 02 空会话 / 03 流式中 / 04 LONG 排版 / 05 工具调用 / 06 错误卡 / 07 设置。
矩阵门禁：7 态齐全、03–06 与 07 内容不同（防「全同屏」假通过）、几何达标、无 console 错误。
纯 UI 点击必须像素自证（`clickExpectChange` 3 次重试）——坐标点击不可靠是历史教训。

## 6. 验证协议（每次 UI 改动后）

按改动层级递进，全部通过才算完成：

1. **构建**：`dotnet build src/Wanxiang.slnx -c Debug`（0 warning 0 error）。
2. **单元/Headless**：`dotnet test tests/Wanxiang.Tests/Wanxiang.Tests.fsproj -c Debug`
   —— 336 全绿（连续 2 次全绿才算，见 §4 波动协议）。
3. **视觉回归**（改动涉及布局/交互/主题时）：§5 全流程，矩阵全绿。
4. **桌面冒烟**（改动涉及桌面专属路径时）：`./start.sh` 起全功能节点人工过一遍七态。

## 7. Phase 0 基线记录（2026-09-08）

- 构建：✅ 0 警告 0 错误（含 wasm 链接）。
- 测试：✅ 336/336，连续 4 次全绿（首次冷启动 50 失败为环境波动，已记录协议）。
- 视觉矩阵：本轮未跑（需要 Chrome + 服务端长驻；Phase 1 起纳入常规回归）。

## 8. 结构风险登记表（Phase 0 扫描，随修复逐条销账）

| # | 风险 | 位置 | 严重度 | 状态 |
|---|---|---|---|---|
| R1 | `downloadBuffers` 字典无上限、无清理：附件下载开始后若连接断开或完成事件丢失，缓冲永久驻留 | `AppShell.fs`（`AttachmentDownloadBegin/Complete`） | 中 | ✅ 已修复（2026-09-08） |
| R2 | `ConversationRuns.retired` 集合只增不减：每次 `Start` 退休一个 generationId，长会话/多轮生成后内存单调增长 | `UiControllers.fs` | 低-中 | ✅ 已修复（2026-09-08） |
| R3 | 首轮冷启动偶发 50 个 UI 测试批量失败（`PresentationSource..ctor`），重跑即绿 | 测试基础设施 | 低（环境） | 已记录协议，待观察 |
| R4 | `MessageCard.userBubbleSelection` 硬编码颜色（有注释豁免：主题无关叠加层） | `MessageCard.fs:52` | 低 | 接受（豁免） |
| R5 | `Dialogs.fs:521` / `SettingsGeneral.fs:213` 局部 Grid 列宽写入（非主壳投影，属本地表单布局） | 视图局部 | 低 | 接受（本地布局） |
| R6 | `DialogContentMaxHeight` 560 与 `expandedDetailMaxHeight` 360 是文档化契约，但无运行时断言 | 各视图 | 低 | 测试已覆盖 |

**扫描结论**：架构不变量（Tokens 唯一来源、MainLayoutController 唯一列宽投影器、NavigationController 唯一状态持有者、Overlay 单宿主、焦点有家、计时器生命周期）全部成立；未发现结构性布局/交互崩溃风险。R1/R2 为仅有的两处真实内存风险，已进入修复。

## 8b. Phase 1 页面空间模型审计（2026-09-08）

对每个页面的空间契约逐项核对：契约定义 → 实现位置 → 测试证据。

| 契约 | 实现 | 测试证据 | 结论 |
|---|---|---|---|
| 阅读列 MaxWidth=748 + Stretch 收缩（CJK 40–45 字/行） | `ChatView.messagePanel`（MaxWidth=Tokens.readingWidth, Stretch） | `UiStabilityTests`：reading column ≤748；宽视口=748；窄视口>宽度-4×inset | ✅ |
| 末条呼吸留白由 content Margin 统一承担 | `messagePanel.Margin.bottom=48`（ScrollViewer 下 Padding=0 防叠加） | `ScrollLayoutTests`：下内边距与内容下边距都计入可滚动范围 | ✅ |
| 顶栏标题 ellipsis 不挤右侧动作（Grid Star 列） | `ChatView` leftGroup：Auto/Star/Auto；注释明确记录 StackPanel 无限宽测量教训 | `UiStabilityTests`：长标题 540px 下动作在界内；compact 下 Fork 隐藏 | ✅ |
| Composer compact 折叠提示行，footer 高度不塌陷 | `Composer.SetCompactMode`：hint 隐藏、modelChip.MinHeight=38 兜底 | `UiStabilityTests`：125%/150% 缩放等价视口动作在界内 | ✅ |
| 侧栏 20k 行虚拟化、行状态不换布局槽 | `Sidebar`：VirtualizingStackPanel + 状态只切透明度 | `UiStabilityTests`：realized<90、标题原点不变、running/pin 不位移 | ✅ |
| Settings 两态：左侧导航 ↔ 顶部横向导航 | `SettingsView.applyResponsive`（Dock Left→Top，navPanel 横排） | `UiStabilityTests`：980 max-width、600px 下收缩；`CraftsmanshipPolishTests`：分区箭头遍历 | ✅ |
| 对话框/浮层钳制实时视口 + 焦点恢复 | `OverlayHost`：Center+Margin 双保险、fit*ToViewport | `UiStabilityTests`：320→240 resize 后仍钳制、关闭恢复锚点焦点 | ✅ |

**结论**：Phase 1 未发现新的空间契约缺口；现有测试已覆盖矩阵全部边界宽度。

## 8c. Phase 2 布局契约检查（2026-09-08）

| 契约 | 核对结果 | 测试证据 |
|---|---|---|
| C7：桌面 MinWidth 700 < compact 断点 720 | ✅ `MainWindow.MinWidth = LayoutPolicy.desktopMinWidth`；窗口缩到最窄必进 compact | 既有 navigation controller 测试 + 新增 MainLayout 测试 |
| 列宽唯一投影器 MainLayoutController | ✅ 全仓库列宽写入仅 3 处：MainLayout（主壳唯一投影）+ Dialogs/SettingsGeneral（各自局部表单 Grid，非主壳） | **新增**：`main layout controller projects every navigation state onto the shell grid`（宽/折叠/compact/拖拽钳制/往返序列） |
| OverlayHost 视口钳制 | ✅ dialog/popup/toast 三层都有 fit*ToViewport，resize 时经 root.PropertyChanged 重钳制 | 既有 dialog/popup clamp 测试（320→240 resize） |
| Splitter 生命周期 | ✅ 仅 wide 未折叠时可见；DragCompleted 后钳制写回；compact 下隐藏 | 新增 MainLayout 测试覆盖折叠/compact/往返 |
| 测试缺口发现与销账 | MainLayoutController 此前零直接单测（唯一投影器无契约锁定） | 已补 1 个测试，全量 340 全绿 |

## 8d. Phase 3 交互可靠性审计（2026-09-08）

| 契约 | 核对结果 | 测试证据 |
|---|---|---|
| Escape 唯一归属 topmost-first（popup → dialog → toast） | ✅ `OverlayHost.HandleEscape` 单入口；toast 不自带局部 Esc | **新增**：`escape precedence closes topmost overlay first and guards pending dialog`（三层同开逐层关 + 守卫拦截/放行） |
| 模态守卫：提交 pending 中 Esc/scrim 不关闭但已消费 | ✅ `dialogGuard` 统一供 HandleEscape 与 scrim 检查 | 同上（守卫拦截断言） |
| 焦点有家（D3/D4） | ✅ 抽屉开→搜索框；抽屉关/发送/停止/重试→Composer；设置关→触发控件；删除→邻行/搜索框 | 既有：settings focus restore、composer Escape/删除恢复、sidebar delete-neighbor、CraftsmanshipPolish 键盘链 |
| 快捷键纯解析无副作用 | ✅ `ShortcutRouter.resolve` 只做按键→意图映射 | 既有：`shortcut router maps global keys without executing UI effects` |
| 对话框 Tab 圈闭 + 焦点首元素 | ✅ `trapTab` 用有效可见过滤（跳过折叠容器内字段） | 既有 dialog/popup focus restore 测试 |
| 测试缺口发现与销账 | Escape 全优先级链此前只有单层测试，无「popup 盖 dialog」叠加场景 | 已补 1 个测试，全量 341 全绿 ×2 |

**结论**：Phase 3 未发现新的交互可靠性缺口；Escape 栈语义（含嵌套对话框栈与守卫）经叠加场景测试确认。

## 8e. Phase 4 设计系统清理（2026-09-08）

| 项 | 处置 | 证据 |
|---|---|---|
| R4：`MessageCard.userBubbleSelection` 硬编码颜色 | ✅ 移入 `Tokens.userBubbleSelection`（保留主题无关语义，注释说明理由）；不变量 #1 恢复无例外 | 构建 0 警告 0 错误；`MessageCard` 改引用 Tokens |
| 叠加层前提量化 | **新增**测试 `user bubble stays dark in both palettes...`：气泡 vs 文字 ≥7.0（实测 11.61/8.05）；混合选中底（α-blend）vs 文字 ≥3.0（实测 4.08/3.14）。气泡变浅或白变浓立即报警 | 全量测试 |
| R5：`Dialogs.fs` / `SettingsGeneral.fs` 重复的双列表单布局闭包 | ✅ 提取共享 `Ui.twoColumnForm(columnSpacing, rowSpacing, rowCount, fields)`：列宽写入 + 行/列放置唯一实现，断点只出自 `LayoutPolicy.formSingleColumnBreakpoint`；两处调用点各从 ~30 行缩到 3–5 行 | 全量测试；主壳列宽投影器不变量不受影响（局部表单 Grid 非主壳） |

**结论**：Tokens 不变量恢复零例外；表单布局契约去重完成。

## 8f. Phase 5/6 组件系统审查与最终销账（2026-09-08）

**Phase 5 组件系统审查结论**：
- `Ui` 模块 API 一致性良好：ButtonTone 四级语气、focusRing 外环无位移、validation 本地化、toggle 自动化 pattern 均有测试锁定。
- 计时器生命周期审计（Phase 0 发现的 12 处 DispatcherTimer）全部有 Stop 路径：骨架呼吸（Detach 停）、平滑滚动（Detach/用户滚轮打断）、复制确认（DetachedFromVisualTree）、Toast 过期（remove 即停）、导出进度（onClosed 停）、delivery/viewport/skeleton（AppShell Attached/Detached 对称）。
- 事件订阅无泄漏模式：Sidebar rowHosts/archivedToggleHost 用 DetachedFromVisualTree 清理；ChatView motionSubscription Dispose。

**Phase 6 验证矩阵与最终账目**：

| 风险/缺口 | 最终状态 |
|---|---|
| R1 下载缓冲无上限 | ✅ DownloadBuffers（128 MiB 上限 + 断连清空 + 重试重置） |
| R2 retired 台账无上限 | ✅ FIFO 1024 上限 + RetiredCount 可观测 |
| R3 冷启动 flake | 记录协议（连续 2 次全绿判定） |
| R4 硬编码颜色 | ✅ 移入 Tokens + 前提量化测试 |
| R5 局部 Grid 契约重复 | ✅ Ui.twoColumnForm 去重 |
| R6 文档化契约无断言 | 既有测试已覆盖（dialog/detail 高度） |
| MainLayoutController 零单测 | ✅ 新增投影契约测试（宽/折叠/compact/拖拽钳制/往返） |
| Escape 优先级链无叠加测试 | ✅ 新增三层叠加 + 守卫测试 |

**最终验证基线**：构建 0 警告 0 错误；测试 342/342 连续双绿；改动文件 8 个 + 新增 UI_PATHS.md。

**维护规则**：后续任何 UI 改动按 §6 验证协议执行；新风险记入 §8 表格并在此销账；本文件与代码同步演进，UI 结构性变更必须更新 §2/§3。

## 9. 修复记录

| 提交/变更 | 内容 | 验证 |
|---|---|---|
| R1 修复 | 新 `DownloadBuffers` 控制器（`UiControllers.fs`）：单下载硬上限 128 MiB 触顶即丢缓冲并一次提示；同键重试 Begin 重置不叠加；`Clear()` 断连整体丢弃（AppShell 主动/被动断开双路径调用） | 339/339 全绿 ×2 + 新增 2 个单测 |
| R2 修复 | `ConversationRuns` 退休台账 FIFO 上限 1024：`retire` 统一入口，超限淘汰最旧；`RetiredCount` 可观测；`Clear()` 清空队列 | 339/339 全绿 ×2 + 新增 1 个单测 |
