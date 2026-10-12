# 无障碍与 UX 一致性审计

本文件是 `ux/redesign-4-a11y-tests` 的交付：对全应用的无障碍、键盘导航、焦点顺序、对比度与文案一致性做一次**静态审计**，并把能机械判定的规则固化为测试（`tests/Wanxiang.Tests/A11yRegressionTests.fs`）与 CI 步骤。
本轮不改任何具体屏幕的布局或交互，只动共享令牌；屏幕级问题记在 §7，留给各屏幕分支或后续处理。

## 1. 方法与边界

- 依据：读 `src/Wanxiang.UI` 与 `src/Wanxiang.Pwa/wwwroot` 源码，按调色板数值计算 WCAG 2.x 对比度。
- 未做：真机屏幕阅读器（NVDA / VoiceOver / TalkBack）实测、PWA 的 Lighthouse 跑分、真实 Tab 轨迹录像。以下凡写“待实测”的条目都属此类。
- 判据：文字 ≥ 4.5:1（WCAG 1.4.3）；焦点环等非文本指示器 ≥ 3:1（1.4.11）；所有可交互控件有可访问名称（4.1.2）；键盘可达且焦点可见（2.1.1 / 2.4.7）。

## 2. 对比度

沿用既有测试（`DesignConvergenceTests`、`UiStabilityTests`）覆盖：text / textMuted / textFaint / accent / 状态色 在五种基础表面上，以及 textOnAccent on accent。本轮补上以下此前未验的组合：

| 组合 | 结果 |
| --- | --- |
| 代码着色与 codeMuted 在 codeBg、codeHeaderBg 上 | **两处不达标，已修**：codeComment（浅 3.84 / 深 3.53，在代码头上）、深色 codeMuted 在代码头上 4.15 |
| text / textMuted 在 selected、inlineCode、tableStripe、tableHeader、accentSoft、accentFaint 上 | 全部 ≥ 5.6 |
| danger / info / accent 在各自的 Soft 底上 | 全部 ≥ 5.2 |
| textOnAccent on accentHover；userBubbleText on userBubble | 全部 ≥ 8 |
| 焦点环（accent）在五种基础表面上 | 浅 ≥ 9，深 ≥ 7.4，远高于 3:1 |

修复：`Design/Palette.fs` 中 `codeComment` 两套主题统一为 `#96938B`，深色 `codeMuted` 改为 `#9B9890`（与浅色同值）。修复后最低 4.86:1。注释色仍明显弱于 `codeText`，视觉层级不变。

已知不达标但未改：
- `border`（浅 1.33、深 1.92）、`line`（浅 1.6、深 2.1~2.5）低于 1.4.11 对“控件边界”的 3:1 要求。它们主要用作装饰性分割，但**以描边作为唯一边界的输入框**需要待实测确认；输入壳聚焦时另有 accent 描边加外扩环（见 `Interaction.fs`），聚焦态是达标的，静止态建议后续引入专用的 `controlStroke` 令牌（≥ 3:1）。这会改变视觉，所以只记录不动。

## 3. 键盘导航与焦点

- 可点击表面统一走 `Ui.iconButton` / `ActionBorder` 等原语，`Focusable = true`，焦点环只在 `:focus-visible`（Tab / 方向键）或程序化焦点时出现，鼠标点击不留环（`FocusRingTests` 已锁）。
- 全应用没有显式 `TabIndex`，Tab 顺序等于视觉树顺序。这是正确默认值，**规则：不要引入正的 TabIndex**；顺序问题应通过调整视觉树解决。
- 对话框 / 弹层 Esc 关闭、关闭后焦点回到触发处（`DismissFocusTests`、`OverlayHost.fs`）。规则：任何新弹层必须同时满足这两条。
- 全局快捷键见 `Dialogs.fs` 的快捷键帮助。待实测：快捷键与屏幕阅读器自身快捷键、浏览器（PWA）保留键（Ctrl+W、Ctrl+N 在浏览器标签页内会被浏览器抢占）的冲突。
- PWA 壳：`app.css` 保留了 `:focus-visible` 外扩环；Avalonia 画布内部的焦点由 Avalonia 绘制，壳层把 `#out *` 的 outline 关掉是有意为之。

## 4. 可访问名称与语义

- 共享原语 `iconButton` / `iconButtonAccent` 在 tip 非空时同时设置 `ToolTip` 与 `AutomationProperties.Name`；全仓库有 220 余处 `AutomationProperties` 使用。
- 规则（已测）：图标按钮调用不得传空字符串或 null 作为 tip，否则按钮对辅助技术是无名控件。
- PWA 启动页有 `role="status"`、`aria-live`、`aria-busy`，装饰条 `aria-hidden`，图片有 `alt`。入口页 `lang="zh-CN"`，viewport 不禁止缩放。

## 5. 动效

- 几何动画被禁止（`DesignConvergenceTests`），状态只补间颜色与不透明度。
- PWA 样式含 `prefers-reduced-motion`（已测存在）。待确认：Avalonia 画布内的补间是否同样读取系统“减少动态”设置（`MotionPolicy.fs` 值得核对）。

## 6. 文案一致性与术语表

规范写法（已测的禁用同义词在括号内）：

| 概念 | 统一写法 | 禁止 |
| --- | --- | --- |
| 模型接入方 | 模型服务 | 服务商、提供商、供应商、提供方 |
| 一次聊天的完整记录 | 对话 | 会话 |
| 万象实例（服务端） | 主机 | 服务器、服务端 |
| 从某条消息长出的新对话 | 分叉（同一条消息下的备选叫“版本”） | 分支 |
| 凭据 | 密钥 / API Key | 秘钥、Api Key、api key、Apikey |
| 偏好页 | 设置 | 设定 |
| 省略号 | …（中文省略号） | ... |
| 取消类控件的提示 | 须带「(Esc)」 | — |

当前界面没有违反上述规则（测试基线为 0 处）。以下是**未固化为测试**的不一致（第 1 条 W6 起已固化为测试），建议由各屏幕分支顺手统一：

1. **会话 / 对话**（已由 W6 解决）：名词统一为“对话”，“会话”不再出现在用户可见字符串里；术语表见 `redesign-proposal.md` §4.6，扫描见 `A11yRegressionTests`。
2. **快捷键书写**：同时存在 `Ctrl / ⌘ + N`（快捷键帮助）、`Ctrl+B / ⌘B`（侧栏按钮提示）、`Ctrl+Enter / ⌘Enter`（发送按钮提示）三种格式。建议统一为 `Ctrl / ⌘ + 键`。
3. **“聊天”仅 1 处**（设置页关于说明“聊天客户端”），其余产品文案用“对话”，可保留。

## 7. 屏幕级待办（不在本分支处理）

| 项 | 位置 | 说明 |
| --- | --- | --- |
| 输入框静止态边界对比度 | 全部输入壳 | 见 §2，需要新令牌 |
| ~~术语：会话→对话~~ | 已由 W6 完成 | 见 §6 |
| 快捷键格式统一 | Dialogs.fs、ChatView.fs、Composer.fs | 见 §6 |
| 屏幕阅读器实测 | 全部 | 见 §1 |
| 仓库 `NuGet.config` 的 `fallbackPackageFolders` 写死了某开发者家目录（`/home/kunweiz/.nuget/packages`），目录不存在时 restore 报 NU1301 | NuGet.config | 与 UX 无关，但挡住了新环境的构建；本分支不改 |

## 8. 回归测试清单

`A11yRegressionTests`（全部非 UI，毫秒级，随 `./run-tests.sh --no-ui` 运行；CI 通过现有 pr-gate 的非 UI 测试步骤执行）：

- 代码块 13 种着色在 codeBg / codeHeaderBg 上 ≥ 4.5
- text / textMuted 在 6 种内嵌底色上 ≥ 4.5
- 状态色在浅底上、textOnAccent 在 accent 与 accentHover 上、用户气泡文字 ≥ 4.5
- 焦点环色在 5 种基础表面上 ≥ 3
- 遮罩 alpha 下限
- 图标按钮无空名称；共享原语同时设置 ToolTip 与 AutomationProperties.Name
- 文案：禁用同义词、禁用 `...`、取消类提示含 Esc
- PWA：`lang`、`<title>`、可缩放、启动页 aria、`prefers-reduced-motion`、`:focus-visible`、manifest 名称与图标

新增调色板槽位或新的取消类控件时，先看是否需要补进这些测试。
