---
name: wanxiang
description: 万象（wanxiang）SSOT 总纲：项目硬约束、全局术语、平台范围、命名与 clean-room 边界、交付问答、首版完成定义。任何任务开始前必须先读本 skill。
---

# 项目硬约束

1. 用 F# 和 Avalonia 重写 kelivo, 采用 agent-framework 作为 Agent 层, UI 基本不变, 支持多平台.
2. 采用 C/S 架构, 每个实例对等, 既是 C 又是 S, 可以自己指挥自己, 或者自己的 C 指挥别人的 S, 跨平台兼容.

# SSOT — 万象（wanxiang）单一事实来源

> 本组 skill（`wanxiang` 及其余 `wanxiang-*` 主题 skill，由原 AGENTS.md 按主题拆分重组而来）是「万象」项目的**单一事实来源（Single Source of Truth）**。
> 内容整理自 2026-08-01 的项目决策讨论（132 轮问答），完整保留全部决策、用户回答、第一性原理、自动判断规则与 200 项自动问答，不丢失任何信息。

## 项目硬约束（背景）

- 用 **F# + Avalonia** 重写，UI 基本保持不变；
- 使用 **Microsoft Agent Framework** 作为 Agent 层；
- 采用每个实例同时充当客户端（C）和服务端（S）的**对等 C/S 架构**，可以自己指挥自己，或者自己的 C 指挥别人的 S；
- 跨平台兼容。
- 正式产品名：**万象**；英文及技术标识：**wanxiang**。新项目的一方命名、UI、协议、配置项、日志和文档不得出现旧名称（kelivo / agent-framework / avalonia 仅作为第三方依赖或灵感来源出现）。

## 对话来源与整理方式

- 原始记录：`ChatGPT-项目决策讨论.md`（132 轮问答，2026-08-01 11:11–13:38，ChatGPT Exporter 导出）。
- 初始会话采用 grilling 方式：AI 逐题提问（一次一问）、给出推荐答案，用户逐题确认；事实类问题由 AI 查证（含 Agent Framework 官方文档/源码核对），决策类问题由用户拍板。
- 首轮附带 `repomix-output(109).xml` 项目材料作为背景；硬约束（F#、Avalonia、Agent Framework、对等 C/S）由该材料确认。
- 用户在第 100 个决策后指示："你已经提了100个问题了。你根据我所有的回答，归纳反推出我决策的第一性原理，然后自动回答自己剩余问题，仅当置信度不够时候再问。"——由此产生 12 条第一性原理、16 条判断规则、5 条提问条件（41–43 号章节）。
- 用户随后指示"自动问答100个"——由此产生第 101–200 项自动问答（44–54 号章节）。
- 内容按主题重新组织为多个 skill；决策原文、用户原话、AI 建议与确认结论均完整保留。AI 的中间检索/思考过程（如"正在搜索网页"）属于过程噪音，未收录；凡引用过的关键外部来源链接保留在各主题章节内。

## 文档地图（各章节现所在 skill）

### A. 决策记录（01–40，按主题分组，覆盖对话中的决策 1–100）

| 所在 skill | 主题 | 覆盖决策 |
|---|---|---|
| `wanxiang` | 平台范围与总体架构 | 1–2 |
| `wanxiang-store` | 存储模型：NDJSON + CQRS、唯一权威 | 3 |
| `wanxiang-store` | 写入确认与 flush | 4 |
| `wanxiang-store` | 提交原子性 | 5 |
| `wanxiang-store` | 事件日志组织：全局流、UTC 分文件、全局 id | 6–7 |
| `wanxiang-store` | 日志恢复：空洞、静默截尾、损坏定义 | 8–10 |
| `wanxiang-store` | 状态快照 | 11 |
| `wanxiang-store` | 事件格式版本管理 | 12 |
| `wanxiang-store` | 命令幂等：commandId / invocationId | 13–16 |
| `wanxiang-chat` | 模型生成作为长任务、消息记账 | 17 |
| `wanxiang-chat` | 消息模型与 Agent Framework 透明映射 | 18–20 |
| `wanxiang-chat` | 会话归属与并行生成 | 21–22 |
| `wanxiang-chat` | 排队消息 | 22–24 |
| `wanxiang-protocol` | 传输协议：muxed WebSocket、fire-and-forget | 25 |
| `wanxiang-protocol` | 断线重连与同步 | 26–27 |
| `wanxiang-protocol` | 观察（Observe）模型 | 28–30 |
| `wanxiang-protocol` | 发送顺序、游标与慢客户端 | 31–34 |
| `wanxiang-protocol` | 写权限、陈旧检测与重试 | 35–38 |
| `wanxiang-store` | 提交协调器与故障恢复 | 39–40 |
| `wanxiang-config` | 配置持久化与密钥管理 | 41–42 |
| `wanxiang-config` | 配置热更新与冲突 | 42–44 |
| `wanxiang-auth` | 认证：配对与令牌 | 45–47 |
| `wanxiang` | 命名边界与 clean-room | 48 |
| `wanxiang-runtime` | 运行模式：单进程与三开关 | 49–50 |
| `wanxiang-runtime` | doctor 与 --fix | 51 |
| `wanxiang-auth` | PWA 模式与凭据存储 | 52–53 |
| `wanxiang-auth` | 令牌即身份与轮换 | 54 |
| `wanxiang-auth` | 令牌提交、吊销与并发连接 | 55–59 |
| `wanxiang-config` | 模式开关优先级与默认组合 | 60–61 |
| `wanxiang-auth` | 连接目标选择与自动配对 | 62–65 |
| `wanxiang-config` | TLS 与监听端点 | 66–67 |
| `wanxiang-protocol` | WebSocket 帧格式与版本协商 | 68–70 |
| `wanxiang-attachments` | 附件传输与内容寻址 | 71–72 |
| `wanxiang-attachments` | 附件清理与删除记账 | 73 |
| `wanxiang-chat` | 编辑即 fork | 74–77 |
| `wanxiang-chat` | fork 语义深化 | 78–82 |
| `wanxiang-chat` | 会话生命周期 | 83–87 |
| `wanxiang-chat` | 生成取消与 generationId | 88–92 |
| `wanxiang-tools` | Tool 执行 | 93–94 |
| `wanxiang-tools` | Tool 来源与 MCP | 95–100 |

### B. 原则与规则（41–43）

| 所在 skill | 主题 |
|---|---|
| `wanxiang-principles` | 反推出的第一性原理（12 条） |
| `wanxiang-principles` | 我将自动采用的后续判断规则（16 条） |
| `wanxiang-principles` | 只有这些情况我还会提问（5 条） |

### C. 自动问答（44–54，第 101–200 项，含结论）

| 所在 skill | 主题 | 条目 |
|---|---|---|
| `wanxiang-runtime` | 一、进程、启动与关闭 | Q101–110 |
| `wanxiang-store` | 二、日志格式与恢复 | Q111–120 |
| `wanxiang-store` | 三、投影与查询 | Q121–130 |
| `wanxiang-protocol` | 四、Observe、快照与慢客户端 | Q131–140 |
| `wanxiang-protocol` | 五、命令、幂等与错误 | Q141–150 |
| `wanxiang-chat` | 六、Provider 与生成 | Q151–160 |
| `wanxiang-tools` | 七、Tool 与 MCP | Q161–170 |
| `wanxiang-attachments` | 八、附件 | Q171–180 |
| `wanxiang-config` / `wanxiang-auth` | 九、配置、认证与配对 | Q181–190 |
| `wanxiang` | 十、PWA、UI、测试与交付 | Q191–200 |
| `wanxiang` | 自动问答结论与首版完成定义 | 汇总 |

> 编号 55–99 预留，供未来新增主题使用。本组 skill 是唯一权威来源；与任何其他文档冲突时，以本组 skill 为准。

## 全局术语（本 SSOT 内统一含义）

- **万象 / wanxiang**：正式产品名。
- **S / Server**：Linux x64 服务端，承担 Agent 执行、NDJSON、内存投影、TOML、WebSocket 端点。
- **C / Client**：客户端（Linux 桌面 Client 或 PWA），只通过 muxed WebSocket 连接 S。
- **NDJSON**：事件日志永久存储，一条完整行 = 一次原子提交。
- **CQRS**：命令写入 NDJSON，查询读取事件折叠后的内存投影。
- **单写者 / Commit Coordinator**：进程内唯一的永久提交组件，也是 NDJSON 的唯一写入者（见 19 节）。
- **id**：全局连续递增的无符号 64 位提交序号（从 1 开始，跨 UTC 日期文件不归零）。
- **TOML**：唯一配置文件，含明文密钥，配置的唯一权威来源。
- **clean-room**：万象独立实现，原项目仅作灵感来源。

# 01. 平台范围与总体架构

> 覆盖对话决策 1–2。会话时间：2026-08-01 11:11–11:14。

## 决策 1：首个可交付版本支持哪些平台？

**AI 建议：** 第一阶段只承诺 **Windows、macOS、Linux 桌面端**；架构和业务层保持可移植，但暂不承诺 Android、iOS 和浏览器端。原因是桌面端是 Avalonia 当前最稳妥的交付路径，而移动端涉及额外 workload、平台生命周期、后台生成、通知、密钥存储和控件兼容性。若一开始把移动端纳入验收范围，会显著放大重写风险，也会妨碍先验证数据模型、Agent 层和对等 C/S 架构。

**用户回答（11:13:09）：** "第一版本 Linux x64(C+S) & PWA(C) 即可"

**已确认（11:13:17）：**
- **Linux x64：完整节点，同时具备 C + S**
- **PWA：仅作为 C**
- 暂不纳入 Windows、macOS、Android、iOS
- 这与项目的对等 C/S 总体方向兼容，但 PWA 本身不承担服务端职责，属于受限客户端。

## 决策 2：PWA 客户端是否保持"纯前端"？

**AI 建议：是。** PWA 不内置 Agent Framework、不直接访问模型供应商，也不保存 API Key。它只通过 **HTTPS + WebSocket/SSE** 连接一个或多个 Linux S，由 Linux S 负责：

- Agent 执行与工具调用
- 模型供应商访问
- 会话持久化
- MCP 与搜索能力
- 凭据和权限管理
- 流式响应

这样能避免把密钥暴露在浏览器中，也不会要求 .NET Agent Framework 在浏览器环境运行。

**用户回答（11:14:30）：** "是的。PWA 是纯前端客户端"

**已确认（11:14:41）：** **PWA 只负责界面与网络通信，不运行 Agent Framework，不持有模型或服务端密钥。** Linux x64 节点承担 Agent、工具、存储和服务端职责。

# 23. 命名边界与 clean-room

> 覆盖对话决策 48（三个子决策）。会话时间：2026-08-01 12:26–12:29。

## 决策 48（第一问）：禁名规则是否覆盖第三方依赖声明？

这里存在一个实际冲突：项目文件、依赖锁文件、许可证清单及构建产物元数据通常必须出现第三方包的官方名称。若要求整个仓库逐字禁止三个旧名称，就无法正常声明当前选定的 UI 与 Agent 依赖。

**AI 建议：** 禁名规则覆盖所有一方内容，但豁免不可改写的第三方标识。允许出现的范围仅包括：包引用与锁文件；第三方许可证和 NOTICE；构建系统自动生成的依赖元数据；必须匹配第三方 API 的命名空间或类型名。除此之外，目录名、程序集名、命名空间、代码中的一方类型、协议事件、TOML 字段、UI、日志和项目文档全部只使用 `万象` / `wanxiang`。

**用户回答（12:27:30）：** "允许 avalonia agent-framework 依赖，可以提及但不主动提及。kelivo 只是灵感来源，会写入 Acknowledgement 但不是依赖，也不协调其许可证"

**已确认（12:27:40）命名与致谢边界：**
- **万象 / wanxiang** 是唯一的一方产品名称；
- Avalonia 与 Agent Framework 作为实际第三方依赖，可以在依赖声明、技术文档和必要代码位置出现，但不作为产品宣传重点；
- Kelivo 仅作为灵感来源，在 Acknowledgements 中说明，不作为依赖或产品组成部分。

## 决策 48（第二问）：是否采用 clean-room 边界？

背景：现有项目材料同时要求"UI 基本不变"，并建议直接阅读原项目的源码、数据模型和界面截图进行逐项移植；材料也注明其许可证为 AGPL-3.0。这与"仅是灵感来源、万象不按其许可证协调"不能同时保持模糊。

**AI 建议：采用。** 具体限制为：
- 不复制、翻译或机械改写原项目源码；
- 不复制其资源、图标、文案或生成代码；
- 不追求逐像素复刻；
- 只参考公开可观察的产品行为、功能类别和一般交互思想；
- 万象的数据模型、协议、UI 组件结构和实现独立设计；
- Acknowledgements 只说明灵感来源，不暗示派生或兼容关系。

**用户回答（12:28:43）：** "当然是按 clean-room 方式，只作为灵感来源，UI 也仅仅是启发而已"

**已确认（12:29:04）clean-room 边界：**
- 万象独立设计与实现；
- 原项目仅提供产品灵感和功能启发；
- 不复制、翻译或机械改写其源码、资源、数据结构及文案；
- UI 不做逐像素复刻；
- Acknowledgements 仅说明灵感来源，不建立依赖、兼容或衍生关系。

## 决策 48（第三问）：Linux 客户端与 PWA 是否共享 UI 代码？

**AI 建议：** 共享同一套 F# UI、ViewModel 和 WebSocket 客户端代码，仅保留不同的平台入口。
- Linux x64 入口同时启动本地 UI、客户端连接层和服务端；
- PWA 入口只启动 UI 与远程客户端连接层；
- Linux UI 即使连接本机服务，也走与远程客户端相同的 muxed WebSocket 协议，不直接调用服务端内存对象；
- 文件系统、进程、托盘等 Linux 专属功能放在平台适配层；
- 浏览器端不引用服务端、NDJSON、TOML 或本机工具实现。

这样能够持续验证"自己的 C 指挥自己的 S"与远程调用使用完全相同的协议路径，避免本机模式形成隐藏的第二套行为。

**用户回答（12:29:23）：** "同意 Linux C 与 PWA 共用一套 UI/客户端代码，并且 Linux 本机连接也强制经过 WebSocket"

**已确认（12:29:30）Linux x64 与 PWA 的客户端层：**
- 共用同一套 F# UI、ViewModel 与协议客户端代码；
- Linux 本机客户端也必须通过 loopback WebSocket 连接本机服务端；
- 禁止通过进程内对象、共享内存或特殊本地接口绕过协议；
- 因此"本机控制自己"和"远程客户端控制服务端"走同一条行为路径。

# 53. 自动问答 十：PWA、UI、测试与交付（Q191–200）

**191. 问：PWA 的 IndexedDB 按什么键保存连接？**
答：以 `instanceId` 为主键，保存 URL 列表、原 token 和展示名称；URL 不是身份。

**192. 问：PWA Service Worker 可以缓存什么？**
答：只缓存版本化静态资源，不缓存 API、WebSocket 内容、消息、附件或会话快照。

**193. 问：PWA 新版本何时生效？**
答：静态资源版本变化后提示刷新；不在用户编辑或生成过程中强制 reload。

**194. 问：Linux 桌面端允许启动多个窗口或进程吗？**
答：允许多个纯客户端进程；同一数据目录只能有一个 S。一个客户端进程是否多窗口属于 UI 层，不改变连接语义。

**195. 问：窗口尺寸和主题是否写入服务端 TOML？**
答：不写入业务 TOML。它们是本机客户端偏好，保存在客户端本地设置中，不经 NDJSON 同步。

**196. 问：UI 是否照搬原项目的模型和实现？**
答：不照搬。只参考信息架构和视觉体验，使用 clean-room F#/Avalonia 实现，不复制源代码、数据模型或 Flutter 依赖。

**197. 问：首版无权限控制是否意味着忽略基础可访问性？**
答：不意味着。键盘导航、焦点可见性、屏幕阅读标签、缩放和减少动画属于 UI 基础质量，应从首版保留。

**198. 问：运行日志采用什么格式？**
答：stderr 默认输出结构化 JSON Lines；错误对象、堆栈和原始损坏内容作为转义字段保存，便于人工读取和机器分析。

**199. 问：哪些测试是发布阻塞项？**
答：日志 replay 与截断、ID 连续性、单写者竞争、命令幂等、fork 可达性、配置原子 reload、cursor catch-up、配对撤销、MCP 崩溃和附件哈希验证必须有自动化测试。

**200. 问：首版达到什么条件才算完成？**
答：Linux x64 能以默认 UI+S 模式运行，也能独立 headless S 和 client-only；PWA 能托管、配对、观察、发送、流式展示、调用 Tool、上传附件、fork/edit；重启能只靠 TOML、NDJSON 和附件目录恢复全部永久业务状态。

# 54. 自动问答结论与首版完成定义

## 自动问答结论（200 项总览）

这 100 项（第 101–200 项自动问答）中：

- **97 项：高置信度**，可由既有原则直接推出；
- **3 项：中等置信度**，分别是：
  - 附件默认上限（64 MiB，Q171）；
  - 默认 chunk 大小（256 KiB，Q172）；
  - 配对限流数值（每远端地址每分钟 5 次失败、冻结 5 分钟，Q188）；
- **0 项：低置信度阻塞项**，因此本轮不需要继续提问。

数值型中置信度答案应被视为首版默认参数，而不是协议永久语义；以后可通过 TOML 调整，不需要修改历史或升级协议。

## 首版完成定义（Q200 的展开）

Linux x64 能以默认 UI+S 模式运行，也能独立 headless S 和 client-only；PWA 能托管、配对、观察、发送、流式展示、调用 Tool、上传附件、fork/edit；重启能只靠 TOML、NDJSON 和附件目录恢复全部永久业务状态。

## 本 SSOT 的后续使用规则

- 凡是 01–43 号章节已覆盖的决策，以及 44–53 号章节中的高置信度问答，都视为已定案，不再向用户重复提问；
- 中等置信度的三项数值（附件上限、chunk 大小、配对限流）是实现时的可调默认参数，具体数值在实现时可按 TOML 默认值确定；
- 新需求与已确认原则冲突、或需要人为选择数值/产品取舍时，才重新进入提问流程（见 `wanxiang-principles` 的 43 节）。

## Skill 索引（AGENTS.md 重组后）

原 AGENTS.md 已按主题拆分为以下 skill（内容零丢失，各 `# NN.` 小节即原决策编号，可交叉引用）：

| Skill | 内容 |
|---|---|
| `wanxiang` | 本总纲：硬约束、SSOT 说明与文档地图、全局术语、平台范围(01)、命名边界与 clean-room(23)、PWA/UI/测试/交付问答(53)、首版完成定义(54) |
| `wanxiang-store` | 存储与日志：02–09、19、45、46 |
| `wanxiang-protocol` | 传输协议：14–18、32、47、48 |
| `wanxiang-chat` | 会话与生成：10–13、35–38、49 |
| `wanxiang-tools` | Tool 与 MCP：39、40、50 |
| `wanxiang-attachments` | 附件：33、34、51 |
| `wanxiang-config` | 配置：20、21、29、31、52 配置部分（Q181–186） |
| `wanxiang-auth` | 认证与配对：22、26–28、30、52 认证部分（Q187–190） |
| `wanxiang-runtime` | 运行与运维：24、25、44 |
| `wanxiang-principles` | 原则与规则：41–43 |
| `agent-framework` / `avalonia` / `kelivo` | 原 AGENTS.md 尾部三份第三方框架参考文档，原样保留 |

> 默认加载：`.agent/rules/wanxiang-ssot.md`（`alwaysApply: true`）在每次会话注入"任务前先读 `skill://wanxiang`，再按主题读对应 skill"的规则。
