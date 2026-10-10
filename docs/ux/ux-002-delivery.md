# UX-002 实施验收记录

> 本文件记录 UX-002 实施的交付成果、事实凭据与未决事项。UX-002 将万象客户端推进至“对话优先”产品边界，所有改动均在不改变 AG-UI 协议、C/S 架构与落盘格式的前提下完成。

## 1. 范围概述

- **产品边界**：在不改变 AG-UI 协议、C/S 架构、落盘格式的前提下，将万象客户端推进至“对话优先”产品边界。坚持会话为主线，不引入项目、任务、工作区等额外持久化对象。
- **并行推进**：包含四路并行改动的实施、集成与两轮审查修复：
  1. 首次接入（onboard）：覆盖无凭据连接、配对引导、无可用模型直达编辑器与草稿保护。
  2. 服务商编辑分层（provider-forms）：快速接入与高级设置分层展示，取消探测自动 upsert，强化显式提交与校验定位。
  3. 工具四状态（tool-status）：精细化工具运行状态判定与展示，区分执行中、成功、失败与结果未知；第二轮修复第三方 MCP 工具输出的“假成功”缝隙。
  4. 视觉节奏收敛（visual-audit）：清理双重卡片描边、统一原生控件伪类契约、精简菜单项与渲染器多余视觉修饰。

## 2. 已成立的事实（按 AGENTS.md W1–W6）

| 阶段 | 改动文件 | 实质成果 |
| --- | --- | --- |
| W1 基线 | docs/ux/conversation-first.md | UX-001 差异已保护 |
| W2 首次接入 | AppShell.fs, ChatView.fs, Dialogs.fs | 无凭据→内联连接入口+草稿保护；无模型→直达服务商编辑器；配对失败→当场错误+重试 |
| W2.4–W2.5 服务商分层 | SettingsProviders.fs, SettingsView.fs | 快速接入/高级设置两层编辑器；ApplyProbe 移除自动 upsert，改为草稿回填+显式保存确认；保存时高级字段错误自动展开并聚焦 |
| W2.7 返回路径 | AppShell.fs, ChatView.fs | 首次接入引导→设置→保存→返回会话，恢复原草稿和焦点 |
| W3 会话安静 | Composer.fs | 发送/停止共槽；排队仅生成时可见；上下文摘要仅非默认时展示 |
| W4 工具状态 | MessageModel.fs, MessageCard.fs, ToolCallStatusTests.fs | ToolCallStatus 四状态（Running/Success/Failed/Unknown）；未知状态不再误标“执行中”；成功详情默认收起，失败摘要保持可见；闭环第三方 MCP 错误识别 |
| W6 视觉收敛 | Composer.fs, Sidebar.fs, Controls/Menu.fs, Controls/MarkdownRenderer.fs | 移除双重卡片描边、手写 hover/focus 伪类覆写、Menu 危险项 hover 变体、Markdown 引用块双环 |

### 第二轮审查与“假成功”缝隙修复

在第二轮独立审查中，针对工具四状态的错误识别边界进行了深入审计与修复：

- **只读审计确认**：`MessageCard.fs` 中的 `tryExtractToolError` 原具备 6 类启发式模式，可覆盖服务端内部错误路径（如 `ChatOrchestrator`、`BuiltinTools`、`McpClient` 构造的 `{"error":...}` 或 `"error: ..."`）；但第三方 MCP 工具直接返回的典型失败文本（如 HTTP `403 Forbidden` / `404 Not Found` / `401 Unauthorized`、`Permission denied` / `access denied`、`command not found`、`fatal:`、`{"status":"error"}`、`{"success":false}` 等）曾会被误标为 `Success` 绿标。
- **保守修复**：在 `MessageCard.fs` 的 `tryExtractToolError` 中保守扩充识别模式，涵盖标准 HTTP 状态行、Shell 边界失败格式、结构化 JSON 错误字段以及 `fatal:` 行首匹配；同时补充防误判断言，确保正常 JSON 状态（如 `{"status":"ok"}`）或技术文档中偶发提及错误单词的输出不被误判为失败。
- **严格边界约束**：此项改动严格局限在客户端 UI 投影层（`MessageCard.fs` 与相关测试），未触碰任何协议定义、落盘权威格式或服务端实现。

## 3. 验证凭据

- **全量构建验证**：
  - 执行 `dotnet build wanxiang.slnx -c Debug`。
  - 结果：全量项目编译通过，0 错误，0 警告。
- **自动化测试回归**：
  - 执行 `./run-tests.sh -j 8`。
  - 结果：全套 1131 项自动化测试全部通过（748 项非 UI 测试 + 8 批 UI 隔离进程测试），0 错误，0 失败，0 跳过。
  - 双主题画面导出的 `ConversationWorkspaceTests` 独立执行 19 项测试，0 失败。
- **工具四状态单元测试覆盖闭环**：
  - 新增 `tests/Wanxiang.Tests/ToolCallStatusTests.fs`。
  - 通过反射穷尽验证 `resolveToolCallStatus` 在流式中、非流式、有结果、无结果及包含错误模式下的四种状态映射；
  - 穷尽验证 `badgeText` 与 `isTerminal` 契约，覆盖新增的错误特征提取断言与正向成功防误判断言。
- **视觉存证产出**：
  - 在 `artifacts/ux-baseline/` 目录下已实际产出 10 幅合成画面（5 幅暗色主题 + 5 幅亮色主题，覆盖宽视口与窄视口）；
  - 画面由 `ConversationWorkspaceTests` 通过双主题参数化测试（`[<Theory; InlineData(false); InlineData(true)>]`）捕获导出，全部为合成数据，不包含真实用户隐私。
- **集成与缺陷修复记录**：
  - 第一轮 DevOps 集成阶段修复 4 处普通缺陷（`SettingsProviders.fs` 作用域可见性、`Dialogs.fs` 可选参数语法、`AppShell.fs` 回调参数对齐、`SettingsViewTests.fs` 反射路径）；
  - 第二轮审查阶段修复 `tryExtractToolError` 第三方 MCP 错误识别缝隙，并补齐 `ToolCallStatusTests.fs` 反射契约测试；
  - 均不涉及协议、存储或架构变更。

## 4. 未决事项

- **视觉回归矩阵（V6）**：`tools/qa_matrix.js` 视觉矩阵回归脚本包含清理外部 headless Chrome 进程的操作，在共享开发环境中被明令禁止执行，仍须留待独立图形桌面环境中执行。
- **真人全流程走查（V7）**：AGENTS.md V7 定义的真人走查项（覆盖无凭据到首条消息的首次接入全流程、复杂多工具调用状态转换、最终视觉收敛效果、屏幕阅读器及键盘导航可达性）依赖真实模型服务商凭据与真实桌面/PWA 环境，尚未完成。

## 5. 后续接力

- **当前状态**：源码修改、第二轮缺陷修复、单元测试与既有自动化回归已全部就位，编译门禁（0 错误 0 警告）、自动化测试门禁（1131 项全绿）与合成视觉存证（10 幅双主题截图）均已完全达到交付要求。
- **接力行动**：在具备图形界面的独立 Linux 桌面与 PWA 宿主环境中执行 `tools/qa_matrix.js`，并由人工跟进完成 V1 至 V7 的真人全流程交互走查与最终验收。
