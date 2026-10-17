# 抽吸至 wanxiang2 并扶正——S1 底账

> 本底稿是规划任务的唯一持久化产物。当前阶段：S1 事实与取舍。
> 材料来源：用户输入 U（2026-10-16 三段：初始目标 + 外包思维补充 + 四项拍板）、DevOps 三轮只读调查（2026-10-16）。

## 0. 任务与来源

**U（用户目标，原话）**：
> 我正在按照 wanxiang2/AGENTS.md 的要求，逐步抽吸至 wanxiang2。我们来做计划，一直做到抽吸完工，wanxiang 退役，并把 wanxiang2 扶正。

**U 补充指令（2026-10-16）**：
> 本仓代码还有抽吸空间。要有"外包"思维和组件思维，然后进一步抽空，方可扶正 wanxiang2

**U 拍板回答（2026-10-16，对四项关键取舍）**：
> 把有用的迁走以后 wanxiang 旧仓库整体就废弃。
> 扶正指能独立运行，以后人不需要 wanxiang 仓库作为入口。
> 外包给 wanxiang2 而不是让 wanxiang 来自己干活。这样逐渐就让 wanxiang 越来越轻最后消失。
> 抽到连入口都不再管，让 wanxiang2 能自己运转为止。

**解读**：
1. 「外包」= **外包给 wanxiang2 组件**（组件思维），不是委托第三方。wanxiang 宿主的一切职责（含入口）都外包给 wanxiang2，wanxiang 越来越轻直至消失。
2. 「扶正」= wanxiang2 **能独立运行**，人不需要 wanxiang 仓库作为入口。
3. 终点形态 = wanxiang 旧仓库**整体废弃**（有用资产先迁走），wanxiang2 自己运转。

## 1. 目标形式化

目标状态：抽吸完工、wanxiang 退役、wanxiang2 扶正三者全部达成。以下判据均为可复查语句。

### 1.1 抽吸完工

**宿主职责全部外包给 wanxiang2**（旧仓库根目录 src/ 三工程，当前 34 个 .fs 文件共 6183 行 F#）：
- [ ] Wanxiang.UI/AppShell.fs（2299 行）中的真实逻辑（认证成功步骤序列 Apply、宿主给组合者的插槽/计时器/视口轮询等）已全部移入 wanxiang2 对应库/组合者，宿主不再自持
- [ ] Wanxiang.UI/Controls/Primitives.fs（736 行）、Interaction.fs（381 行）、OverlayHost.fs（178 行）的残余实现已抽走或删除
- [ ] Wanxiang.UI/Design/ 六个文件（Icons 198、Palette 234、Metrics 130、Tokens 180、MotionPolicy 94、SharedTheme 19，共 855 行）的职责已并入 wanxiang2 主题库（ThemePalette/ThemeMetrics/IconSet）
- [ ] Wanxiang.UI/Rich/（189 行）的接口定义与适配逻辑已移入 wanxiang2 的 Rich 组合者；App 的 RichJintAdapter.fs（13 行）、Pwa 的 RichBrowserAdapter.fs（13 行）只留调用点或随壳废弃
- [ ] Wanxiang.UI/Views/Composer.fs（159 行）经核对后抽走或删除
- [ ] Pwa 的 BrowserImageShrink.fs（36 行）与 wanxiang2 的 BrowserImage 库合并
- [ ] Wanxiang.UI/Model/ 九文件（684 行）的 IO 壳职责已明确归属：逻辑已进 wanxiang2 库（DraftStore/UiPrefs/LinkInbox/ProbeCounter/StartupTrace/ImageFit/ImageShrink/MessageView/CommandPalette），IO 壳（文件读写、IndexedDB、注册表、套接字、stderr/env）按「有用迁走」原则移入 wanxiang2 组合者或随宿主废弃
- [ ] Wanxiang.UI/CredentialStore.fs（80 行）的 IndexedDB 封装与 client.toml 文件读取已移入 wanxiang2 组合者（承接库：BrowserCredentials/BrowserPage），宿主不再自持凭据 IO
- [ ] **入口职责外包**：wanxiang2 拥有自己的启动入口（桌面 Program 入口、PWA 入口），能独立启动、独立运行，不依赖 wanxiang 仓库任何文件
- [ ] 宿主壳三工程不再承担任何业务逻辑；其存在只是过渡，最终随 wanxiang 整体废弃

**组件思维落地**（外包 = 外包给 wanxiang2 组件）：
- [ ] wanxiang 宿主中每一处「自己干活」的代码都有对应的 wanxiang2 组件承接，宿主只做转发或什么都不做
- [ ] wanxiang2 内凡已有成熟上游方案覆盖的职责，消费第三方包而非自持实现（AGENTS.md 56.2「有库就用库」）；候选清单见 §2.1.5 与 §4.4
- [ ] SPEC 2「仓库外消费」获得自动化验证（当前无自动化，见 wanxiang2/AGENTS.md 证据现状）

### 1.2 wanxiang 退役

- [ ] 有用资产已全部迁走（迁入 wanxiang2 或明确保留位置），旧仓库根目录**整体废弃**——不是删/归档三分，是「迁移后废弃」
- [ ] 人不再需要 wanxiang 仓库作为入口：启动、构建、测试、发布全部经 wanxiang2
- [ ] STATUS.md（66 行，当前完全不提 wanxiang2/退役）与 README.md 不再把旧仓库描述为产品本体；退役表述落地或文档随仓库废弃
- [ ] 历史资产（kelivo/、site/、shots*/、docs/ 迁移文档等）在废弃前已按「保留 / 归档 / 删除」逐项明确并执行

### 1.3 wanxiang2 扶正

- [ ] wanxiang2 **能独立运行**：有自己的入口、自己的构建/测试/发布流程，不依赖 wanxiang 仓库（当前缺口：无 release/publish workflow，见 §2.1.11）
- [ ] 以后人不需要 wanxiang 仓库作为入口：文档、脚本、CI 全部指向 wanxiang2
- [ ] wanxiang2 成为仓库唯一权威；wanxiang 仓库整体废弃后，wanxiang2 独立存续

> 注：以上判据中「行数阈值」「外包候选是否真外包」依赖 §2.2 待核实项与 §4 取舍；§4 中已拍板项以用户回答为准。

## 2. 事实

### 2.1 已核实的事实

出处：DevOps 三轮只读调查（2026-10-16，find/ls/cat/grep/head，全程未修改文件）；wanxiang2/AGENTS.md（子模块 pin 7a13032f）；docs/handoffs/*.md。

**2.1.1 物理布局与 git 状态**
- [已核实] 工作目录 /home/kunweiz/Desktop/vibe/wanxiang 本身是 Git 仓库（旧仓库），分支 main，工作树干净，与 origin/main 同步
- [已核实] wanxiang2/ 是 git 子模块：.gitmodules 指明 path=wanxiang2、url=git@github.com:PamelaSprin47685ghall/wanxiang2.git、branch=main；当前 pin 7a13032f（heads/main）
- [已核实] 根目录无 AGENTS.md；全仓库 AGENTS.md 位于 wanxiang2/ 内部（顶层 1 份 240 行 + libs/compose/tools 下 100+ 份）
- [已核实] 顶层共 40 条目：wanxiang2/、src/、docs/、tests/、tools/、site/、kelivo/、packaging/、shots*/、.agents/、.github/、.Herdchestra/ 等
- [已核实] 最近提交显示迁移主线：cc35785「Move wanxiang2 to repo root as submodule」、f40b6ef「point NuGet feed at the wanxiang2 submodule」、d9708b2「check the wanxiang2 submodule instead of the old vendor path」等

**2.1.2 宿主壳工程（src/）**
- [已核实] 六个旧工程（Wanxiang.{Config,Core,Server,Client,Store,Agent}）已不存在；src/ 仅剩 Wanxiang.App（3 文件）、Wanxiang.Pwa（16 文件）、Wanxiang.UI（31 文件）
- [已核实] 三工程共 6183 行 F#（App 262、Pwa 90、UI 5831；34 个 .fs 文件，`find -exec cat {} + | wc -l` 逐行真实计数口径）
- [已核实] Wanxiang.App.fsproj 引用 25 个 Wanxiang2.* 包 + Avalonia.Desktop + ProjectReference Wanxiang.UI
- [已核实] Wanxiang.UI 引用 59 个 wanxiang2 本地包
- [已核实] Wanxiang.UI 残留明细：AppShell.fs 2299 行（最大残留）、Controls/Primitives.fs 736 行、Controls/Interaction.fs 381 行、Controls/OverlayHost.fs 178 行、Design/ 六文件 855 行、Model/ 九文件 684 行（CommandPalette 23、DesktopLinks 145、DraftStore 93、ImageShrink 156、LocalState 112、ParityProbe 72、StartupTrace 37、UiPrefs 38、UnansweredReply 8）、Wanxiang.UI/ 根下 CredentialStore.fs 80 行（真实文件/IndexedDB 读写，不在 Model/）、Rich/ 四文件 189 行、Views/Composer.fs 159 行、Views/MainWindow.fs 61 行、App.fs 158 行、Controls/KeyShortcut.fs 12 行（纯包消费壳）
- [已核实] Wanxiang.App/Program.fs 249 行（Avalonia 启动、单实例、信号注册——**当前入口在宿主**）、RichJintAdapter.fs 13 行（适配器壳）
- [已核实] Wanxiang.Pwa/Program.fs 41 行、RichBrowserAdapter.fs 13 行、BrowserImageShrink.fs 36 行、wwwroot/ 9 个静态资产——**PWA 入口当前也在宿主**

**2.1.3 迁移状态表与交接文档**
- [已核实] wanxiang2/AGENTS.md 迁移状态表（174–224 行）48 个数据行全部标注「已接线」
- [已核实] docs/handoffs/2026-10-15-migration-table-audit.md 以静态凭据（[E1]–[E7]）+ 动态凭据双证确认 48 行全部应更新为「已接线」；「真欠接线」与「库侧独立能力未做」清单均为空
- [已核实] docs/handoffs/ 共 11 篇交接文档（2026-10-15）：migration-table-audit、migration-table-cutover、package-handoff、source-and-state、chat-screen-cutover、deletion-cutover、draft-cutover、outbox-cutover、paging-cutover、reconnect-cutover、workspace-cutover
- [已核实] wanxiang2/AGENTS.md 证据现状：SPEC 1–6 各有 [TODO] 边界；SPEC 2 仓库外消费无自动化；SPEC 4 写失败但确知未写无真实故障可触发
- [已核实] wanxiang2/AGENTS.md 抽取路线文本（226–240 行）末尾「下一步是路线图 10：UI 纯模型」——与状态表（10.x 已接线）存在文档滞后

**2.1.4 wanxiang2 内部结构**
- [已核实] wanxiang2 子模块：2478 文件、861 子目录；顶层 AGENTS.md、compose/、libs/、tools/、Directory.Build.props、Directory.Packages.props、global.json、wanxiang2.slnx、x 脚本
- [已核实] libs/ 250 个库（ActionStrip 到 WsHandshake，完整清单已由 DevOps 取得）；compose/ 66 个组合者 + nuget.config 配置文件
- [已核实] libs 无依赖边、compose 只经交付包消费库、./x pack 打出交付包——组件化/独立交付的物理基础已存在
- [已核实] wanxiang2 内部**没有**桌面/PWA 启动入口（Program 类入口在宿主 src/），也没有 run-tests.sh/start.sh——「wanxiang2 独立运转」需要补入口与运行脚本

**2.1.5 第三方依赖清单**（wanxiang2/Directory.Packages.props 权威登记）
- [已核实] FSharp.Core 10.1.401、Microsoft.Extensions.AI.Abstractions 10.9.0、Jint 4.16.1、Markdig 1.3.2、QRCoder 1.8.0
- [已核实] AGUI.Abstractions 1.0.0、Microsoft.Agents.AI 1.20.0、Microsoft.Agents.AI.OpenAI 1.20.0、System.ClientModel 1.8.1
- [已核实] Jcs.Net 0.1.1、Tomlyn 2.10.1、xunit.v3 3.2.2
- [已核实] Avalonia 12.1.2（含 Headless/Skia/Themes.Fluent/Browser）、IconPacks.Avalonia.Core/Lucide 2.0.0、SkiaSharp 3.119.4

**2.1.6 非 wanxiang2 资产**
- [已核实] tests/ 91 文件（Wanxiang.Tests 86 + fixtures 4 + baseline 1）——宿主残留
- [已核实] tools/ 31 文件——混合：tag_ui_tests.py、mock_openai.py、patch_avalonia_js.py 等构建基础设施（宿主残留）；qa_*.js、perf_*.js、pwa_smoke.*、e2e_smoke.js、old_host_proxy.js、parity_pwa.js 等 QA/回归脚本（历史资产）；package_release.sh、stress.sh、perf_desktop.sh 发布/压力工具
- [已核实] site/ 2 文件（index.html + logo.png）——历史资产
- [已核实] kelivo/ 2796 文件——独立 Flutter 产品线（package:Kelivo，有完整 AGENTS.md/PRODUCT.md/pubspec.yaml），与 F# 重写是两条产品线
- [已核实] packaging/ 5 文件——发布打包配置（宿主残留）
- [已核实] docs/ 26 文件——混合：handoffs/（11 篇）+ contracts/（4 篇）是迁移历史资产；STATUS.md、ux/（7 篇）、security/、adr/ 是产品文档（宿主残留）

**2.1.7 文档与现实脱节**
- [已核实] docs/STATUS.md（66 行）自称「收敛状态（2026-10-13）」，内容全为旧产品功能清单/已知局限/发版步骤，完全不提 wanxiang2 或退役
- [已核实] README.md 把旧仓库描述为独立产品（「一个把『对话』当成账本来管的 AI 聊天客户端」），不提 wanxiang2 子模块、抽吸计划或退役安排

**2.1.8 根目录与 wanxiang2 的重复/边界**
- [已核实] Directory.Build.props 两处都有但内容不同（根目录只设 FSharpCoreImplicitPackageVersion；wanxiang2 内部设全套编译设置+中央包管理）——不重复但废弃后根目录这份随仓库废弃
- [已核实] Directory.Packages.props 只在 wanxiang2 内部——不重复
- [已核实] run-tests.sh/start.sh 只在根目录；wanxiang2 内部只有 x 脚本——**wanxiang2 独立运转需补自己的 run/start 入口**
- [已核实] wanxiang.slnx 根目录与 wanxiang2 各有——不重复（宿主应用 sln vs 库/组合者 sln）
- [已核实] tools/wanxiang2-feed.sh + wanxiang2_feed.py 是包源管理工具，与 wanxiang2 的 ./x pack 配合——不重复但强耦合
- [待核实] global.json/NuGet.config 两处版本一致性——核实路径：diff 两处文件

**2.1.9 用户拍板记录（2026-10-16）**
- [已拍板] 退役物理边界：有用资产迁走后，wanxiang 旧仓库整体废弃
- [已拍板] 扶正形态：能独立运行，以后人不需要 wanxiang 仓库作为入口
- [已拍板] 外包边界：外包给 wanxiang2（组件），不是让 wanxiang 自己干活；wanxiang 越来越轻直至消失
- [已拍板] 宿主壳终点：抽到连入口都不再管，让 wanxiang2 能自己运转为止

**2.1.10 宿主 Model/ 与 CredentialStore 的承接方**（DevOps 第三轮只读调查，2026-10-16）
- [已核实] wanxiang2 抽吸模式 = 「逻辑进包、IO 留宿主」：包内库承接编解码、判定、几何、文案、计数等纯逻辑；文件/IndexedDB/注册表/套接字/stderr/env 等平台 IO 明确留在宿主
- [已核实] Model/ 九文件（CommandPalette 23、DesktopLinks 145、DraftStore 93、ImageShrink 156、LocalState 112、ParityProbe 72、StartupTrace 37、UiPrefs 38、UnansweredReply 8）均为「逻辑已抽走后留下的 IO 壳」
- [已核实] CredentialStore.fs 位于 src/Wanxiang.UI/ 根下（非 Model/），80 行，含 client.toml 文件读取（桌面）+ IndexedDB 连接凭据读写（PWA，经 BrowserCredentials.List/Put/Delete）+ defaultServerUrl 同源推导（经 BrowserPage.Url()）；解析函数来自包，宿主只做 IO
- [已核实] wanxiang2 承接库清单：BrowserCredentials（PWA 凭据 JSImport）、BrowserDrafts（PWA 草稿）、BrowserLocalStore（localStorage）、BrowserPage（页面/生命周期）、BrowserProbe（探针）、BrowserImage（图片）、DraftStore（草稿编解码/落盘闸门，明确不迁 IDraftBackend 与后端）、UiPrefs（偏好 parse/render，明确读写留宿主）、LinkInbox（wanxiang:// 收件箱）、ProbeCounter、StartupTrace、ImageFit、ImageShrink、MessageView（ReplyTail）、CommandPalette（PaletteRecent）
- [已核实] 未找到承接方的宿主职责（「宿主保留」而非「可抽走」）：LocalState 的 .Herdchestra 位置发现与 Git 拓扑查询、DesktopLinks 的 UrlSchemeRegistration（注册表/desktop 文件）与 SingleInstance（Unix 套接字）、CredentialStore 的 tryLoadClientToml、StartupTrace/ParityProbe 的 stderr/env 输出、DraftStore 的 IDraftBackend 三后端与 LocalDraftStore 外壳、UiPrefs 的文件读写与权限收紧

**2.1.11 CI 现状**（DevOps 第三轮只读调查，2026-10-16）
- [已核实] 旧仓库 .github/workflows/ 三个 workflow 全部引用 wanxiang2 子模块：ci.yml（187 行，push main/extract-ledger + PR + 每周定时；checkout submodules + tools/wanxiang2-feed.sh 准备 pin 包 + 包回执失败测试 + Release 构建 + PWA/App 冒烟 + 格式/UI trait 门禁 + 测试矩阵）、release.yml（94 行，v* 标签 + workflow_dispatch；四 RID 矩阵发布 + tools/package_release.sh 打包 + 草稿 Release）、stress.yml（46 行，每日定时；tools/stress.sh 压力全套）
- [已核实] wanxiang2 内部有独立 ci.yml（约 60 行，push + PR；四个 job：check/test/compose/widget-template，按 lib 矩阵跑 ./x test）
- [已核实] wanxiang2 **没有** release/publish workflow：./x pack 只打本地 artifacts/feed 供 ./x compose 干净消费，远端包发布流程不存在
- [已核实] packaging/ 5 文件为 wanxiang:// URL 协议注册静态资产（linux desktop 文件、windows reg 模板、macos Info.plist），不指向 wanxiang2
- [已核实] tools/wanxiang2-feed.sh + tools/wanxiang2_feed.py 是旧仓库 CI 消费 wanxiang2 的唯一入口（pin 包、校验 gitlink/vendor HEAD/工作区、产包回执、SHA-256 核验）

### 2.2 待核实的假设

| 假设 | 核实路径 |
|---|---|
| AppShell.fs 2299 行中真实逻辑占比（认证成功步骤序列、宿主插槽、计时器、视口轮询） | 逐段审读 AppShell.fs，对照 wanxiang2 包公开 API，列出每段是「包消费转发」还是「宿主自持逻辑」 |
| Views/Composer.fs（159 行）与 Controls/Primitives.fs（736 行）是残余壳而非真实逻辑 | 审读文件内容，对照迁移表「对应旧代码已删」声明，确认文件保留原因 |
| BrowserImageShrink.fs 与 wanxiang2 BrowserImage 库职责重叠 | 对比两文件实现，列出重叠函数 |
| 可外包候选的替代可行性（AGUI.Abstractions 是否覆盖 Agui/AguiWire/AguiMessageMap；Microsoft.Agents.AI 是否覆盖 Sse/HttpRetry/Anthropic/Gemini） | 查官方包 API 覆盖度，对照 wanxiang2 现有自持实现，列出「可替换 / 不可替换」清单 |
| global.json / NuGet.config 两处版本一致性 | diff 两文件 |
| 迁移表「已接线」与宿主文件残留并存的原因（接线判据 vs 物理迁移判据的差异） | 对照 migration-table-audit.md 的 [E1]–[E7] 静态凭据，确认其只验证「包消费接线」而未验证「宿主壳纯净化」 |
| wanxiang2 独立运转所需的入口形态（桌面 Program、PWA 静态资产、run/start 脚本） | 对照 wanxiang2/AGENTS.md 与宿主入口实现，列出需迁入 wanxiang2 的入口组件清单 |
| IO 壳进一步抽空的组合者归属（LocalDraftStore 外壳、CredentialStore IndexedDB 封装、UrlSchemeRegistration/SingleInstance） | 对照 wanxiang2 组合者模式（compose/），逐项列出可移入组合者的 IO 壳 |
| wanxiang2 独立发布通道（本地 feed / 远端 feed / ProjectReference） | 对照 wanxiang2/AGENTS.md 与 Directory.Build.props 的 IsPackable 设置，列出发布通道选项 |

### 2.3 不可核实项

| 项 | 原因 |
|---|---|
| wanxiang2 内部第三方外包的具体取舍（哪些自持实现可委托官方包） | 需逐项查官方包 API 覆盖度后才能判定，属 S2 候选路径工作；S1 只登记候选清单 |
| kelivo/ 等历史资产在「整体废弃」前的具体保留位置 | 用户已拍板「有用迁走、整体废弃」；具体哪些算「有用」需逐项核对，属执行细节 |

## 3. 根因

对每个问题追问「为什么」到不可改边界。

### 3.1 为什么还需要进一步抽空？（宿主壳残留的根因）

- 现象：迁移表 48 行全部「已接线」，但宿主壳仍有约 6183 行代码（AppShell 2299 行是最大残留），且入口（Program）仍在宿主
- 问：为什么接线了代码还在？
- 答：迁移表的「已接线」判据是「宿主经 Wanxiang2.* 包消费该职责」（migration-table-audit 的 [E1]–[E7] 静态凭据验证的是包引用与接线，不是宿主文件行数）
- 问：为什么接线判据不包含物理迁移？
- 答：抽吸路线（AGENTS.md 226–240 行）的每一步以「职责可经包消费」为完成线，宿主文件是否清空不是当时的验收项；「接线」与「物理清空」被当成同一件事
- 问：为什么当时没把物理清空纳入验收？
- 答：抽吸的推进节奏是「先接线保行为、再清壳」，清壳步骤尚未排入路线图；路线图文本停留在「下一步 UI 纯模型」，而状态表已显示 10.x 完成——清壳是路线图与状态表之间的真空地带
- **根因**：抽吸的验收标准只覆盖「包消费接线」，未覆盖「宿主职责外包（含入口）与壳纯净化」；清壳与入口迁移从未被形式化为路线图步骤。修复症状（改状态表）不是方案，把「宿主职责全部外包给 wanxiang2、wanxiang2 独立运转」形式化为抽吸完工的判据才是。

### 3.2 为什么文档滞后（STATUS.md/README.md 不提及 wanxiang2）？

- 现象：STATUS.md 自称「收敛状态」却把旧仓库写成独立产品
- 问：为什么文档没跟上？
- 答：抽吸以代码接线为主线，文档更新没有作为同一验收标准的一部分
- 问：为什么文档更新没进验收？
- 答：退役决策尚未拍板——「wanxiang 退役」是目标词，但退役的物理动作（删什么、留什么）未定，文档不敢写
- **根因**：退役的物理边界未拍板 → 文档无法表述终态 → 文档滞后。根因是决策缺口，不是写作懒惰。修复症状（改 STATUS.md 措辞）不是方案，拍板退役边界才是——用户已拍板「有用迁走、整体废弃」，文档更新成为执行项。

### 3.3 为什么 wanxiang2 还是子模块？

- 现象：wanxiang2 以 git submodule 形态挂在旧仓库根目录
- 问：为什么用子模块？
- 答：抽吸过程需要双轨并存——旧仓库消费新包、新库独立演进；子模块提供独立提交史与 pin 能力
- 问：双轨并存的终点是什么？
- 答：抽吸完工后双轨不再需要。用户已拍板终点 = wanxiang2 独立运转、wanxiang 整体废弃；子模块形态只是抽吸过程的过渡工具，不是终点形态
- **根因**：子模块是抽吸过程的过渡态；扶正就是把过渡态收束为终态。用户拍板后，子模块的去留由「wanxiang2 能否独立运转」决定：能独立运转，则 wanxiang 仓库（含子模块挂载关系）整体废弃，wanxiang2 仓库独立存续。

### 3.4 为什么会有「外包」空间？

- 现象：wanxiang 宿主壳自持了入口（Program）、AppShell（2299 行）、控件、主题、Rich 适配等实现，而 wanxiang2 已有 250 库 + 66 组合者可承接
- 问：为什么宿主还在自己干活？
- 答：抽吸时以「职责可经包消费」为完成线，宿主保留了「调用点」甚至「部分实现」；入口（Program）从未被纳入抽吸范围
- 问：为什么入口没被纳入？
- 答：AGENTS.md 的抽取路线以「库/组合者」为抽吸单元，没有「应用入口」这一单元；「wanxiang2 独立运转」从未被形式化为目标
- **根因**：抽吸是「内聚」运动（旧代码 → wanxiang2 组件），外包是「外放」运动（宿主职责 → wanxiang2 组件 + 入口）；两者是同一枚硬币的两面，但抽吸只完成了内聚，外放（含入口）未启动。不可改边界：用户拍板的架构约束（F# + Avalonia、AG-UI 协议、clean-room、对等 C/S）与第三方包的可用性。

## 4. 取舍

### 4.1 退役物理边界（已拍板）

**用户回答**：把有用的迁走以后 wanxiang 旧仓库整体就废弃。

| 资产 | 处置方向（执行时逐项核对「有用」） |
|---|---|
| src/ 宿主壳（6183 行） | 职责全部外包给 wanxiang2 后废弃 |
| tests/（91 文件） | 有用用例迁入 wanxiang2 各库，其余随仓库废弃 |
| tools/（31 文件） | 构建基础设施迁入 wanxiang2；QA 脚本按「有用」判定迁/归档/废弃 |
| docs/（26 文件） | 迁移历史文档归档；产品文档迁入 wanxiang2 |
| kelivo/（2796 文件） | 独立产品线——按「有用」判定保留位置（独立仓库/归档），不随 wanxiang 消失 |
| site/、shots*/、packaging/ | 逐项判定保留/归档/废弃 |
| STATUS.md、README.md | 不再作为产品入口文档；退役表述或随仓库废弃 |

### 4.2 扶正形态（已拍板）

**用户回答**：扶正指能独立运行，以后人不需要 wanxiang 仓库作为入口。

- wanxiang2 必须拥有自己的入口（桌面 Program、PWA 静态资产、run/start 脚本、CI），能独立启动/构建/测试/发布
- 人不再需要 wanxiang 仓库作为入口——文档、脚本、CI 全部指向 wanxiang2
- 子模块形态是过渡工具：wanxiang2 独立运转后，wanxiang 仓库整体废弃，wanxiang2 独立存续

### 4.3 宿主壳终点（已拍板）

**用户回答**：抽到连入口都不再管，让 wanxiang2 能自己运转为止。

- 宿主壳连「入口」职责都不再承担——入口迁入 wanxiang2
- 抽吸终点 = wanxiang2 自己运转，宿主壳不再有任何必须存在的理由

### 4.4 外包范围（语义已澄清：外包给 wanxiang2 组件）

**用户回答**：外包给 wanxiang2 而不是让 wanxiang 来自己干活。

| 宿主职责 | 承接方（wanxiang2 组件） | 备注 |
|---|---|---|
| 桌面入口（App/Program.fs 249 行） | wanxiang2 新增入口组件（如 compose/Wanxiang.App） | 需新建，当前不在 wanxiang2 |
| PWA 入口（Pwa/Program.fs + wwwroot/） | wanxiang2 新增 PWA 入口组件 | 需新建，当前不在 wanxiang2 |
| AppShell（2299 行） | wanxiang2 已有 AppShell 相关库/组合者 | 逐段核对后迁移 |
| 控件（Primitives 736/Interaction 381/OverlayHost 178） | wanxiang2 控件库 | 逐段核对后迁移 |
| 主题（Design/ 855 行） | wanxiang2 ThemePalette/ThemeMetrics/IconSet | 核对重叠后并入 |
| Rich 适配（189 + 13 + 13 行） | wanxiang2 Rich 组合者 | 接口+适配整体迁入 |
| BrowserImageShrink（36 行） | wanxiang2 BrowserImage | 合并 |
| Model/ 九文件（684 行，IO 壳） | wanxiang2 对应库已承接逻辑（DraftStore/UiPrefs/LinkInbox/ProbeCounter/StartupTrace/ImageFit/MessageView/CommandPalette）；IO 壳（文件/IndexedDB/注册表/套接字）移入组合者或随宿主废弃 | 逐文件核对「有用」后处置 |
| CredentialStore（80 行，Wanxiang.UI/ 根下） | BrowserCredentials + BrowserPage（IndexedDB/同源推导）+ 组合者承接 client.toml 读取 | 需新建组合者封装 |
| 运行脚本（run-tests.sh/start.sh） | wanxiang2 自己的 run/start 入口 | 需新建 |

**wanxiang2 内部第三方外包**（组件思维的另一面，S2 候选）：
| 候选 | 现有自持 | 替代方案 | 代价/风险 |
|---|---|---|---|
| AG-UI 协议层 | Agui/AguiWire/AguiMessageMap | AGUI.Abstractions/Server/Client 1.0.0（已引用） | 需核实官方包覆盖度；AGENTS.md 56.2 已定「有库就用库」 |
| 模型传输 | Sse/HttpRetry/JsonPick/Anthropic/Gemini | Microsoft.Agents.AI(.OpenAI) 1.20.0（已引用） | 需核实覆盖度；可能只覆盖部分 provider |
| 渲染后端 | HljsHtml/KatexHtml/JintScriptHost | 外部服务/CDN/官方 JS 引擎 | 依赖外部可用性；离线场景退化 |
| 主题/连接层 | Theme、WsConnection/WsClient、ServerApp | 保持独立交付包（组件思维）而非外包 | 无第三方可替代，属「组件化」而非「外包」 |

### 4.5 测试与文档归属

| 项 | 选项 |
|---|---|
| tests/ 91 文件 | 有用用例迁入 wanxiang2 各库；571 项基线已在 wanxiang2 侧验证 |
| STATUS.md/README.md | 不再作为产品入口；退役表述或随仓库废弃 |
| tools/ 31 文件 | 构建基础设施迁入 wanxiang2；QA 脚本按「有用」判定 |
| SPEC 1–6 [TODO] | 扶正前闭合（证据完备性）/ 扶正后作为 wanxiang2 自身演进项 |

### 4.6 待拍板项（已全部拍板，2026-10-16）

1. ~~退役物理边界~~ → 有用迁走、整体废弃
2. ~~扶正形态~~ → 能独立运行、不需要 wanxiang 仓库作为入口
3. ~~外包边界~~ → 外包给 wanxiang2 组件、wanxiang 越来越轻直至消失
4. ~~宿主壳终点~~ → 抽到连入口都不再管、wanxiang2 自己运转

> 四项已拍板，S1 不再需要 ask。执行阶段如遇「有用」判定歧义（如 kelivo/ 归属），按用户拍板原则「有用迁走、整体废弃」逐项核对。

## 5. 验收

S1 的验收标准：
- [ ] 目标状态已形式化（§1 可判定判据，含「wanxiang2 独立运转」）
- [ ] 关键事实已核实或标注（§2 三分记录 + 用户拍板记录）
- [ ] 根因已追到底（§3 到不可改边界）
- [ ] 取舍已记录（§4 选项 + 代价 + 拍板结果）

## 6. 退役执行进展（2026-10-17）

GAP 执行记录。出处三分：[实测] = 本次 Engineer 文件系统直接观察（read/glob，未执行任何命令）；
[执行报告] = DevOps 运行记录，经 Manager 转述（Engineer 不执行命令，未独立复验）；
[报告口径] = 委托背景给出的数字，本次未逐项复核。

### 6.1 已完成

- [实测] **入口外包**：`wanxiang2/app/Desktop/`（Program.fs、RichJintAdapter.fs、Wanxiang.App.fsproj）
  与 `wanxiang2/app/Pwa/`（Program.fs、App.fs、RichBrowserAdapter.fs、BrowserImageShrink.fs、
  Wanxiang.Pwa.fsproj、runtimeconfig.template.json）存在，且已纳入 `wanxiang2.slnx` 的 `/App`
  folder（1018–1021 行）。§1.1「入口职责外包」的结构性事实成立。
- [实测] **wanxiang2 独立运转（入口与脚本）**：`run-desktop.sh`（产包→构建→启动，头注明示不依赖
  宿主 start.sh / tools/wanxiang2-feed.sh）、`run-pwa.sh`（产包→构建 PWA→python 静态托管，默认 8000）、
  `publish.sh`（四 RID 自包含 publish→tar.gz/zip+SHA-256→`--draft` 挂草稿 Release，校验内嵌
  `pwa/_framework/avalonia.js`，对应宿主 PublishPwaBundle 的等价物）、`x` 均在 wanxiang2 根。
- [实测] **独立构建发布闭环的工具链**：`wanxiang2/tools/patch_avalonia_js.py` +
  `verify_avalonia_patch.py` 存在（宿主根 `tools/patch_avalonia_js.py` 同名副本仍在，处置见 6.3）。
- [实测] **自有 CI 与测试基建**：`wanxiang2/.github/workflows/ci.yml` + `release-publish.yml`；
  `wanxiang2.slnx` 实测含 254 个库目录（每目录 lib+tests 两工程），与报告口径「254 libs」一致；
  compose「73 个」为 [报告口径]（§2.1.4 基线 66，compose 不在 slnx 内，本次未复核）。
- [实测] **宿主壳净化（未删除）**：`src/` 现存 10 个 .fs 文件（App：Program、RichJintAdapter；
  Pwa：Program、RichBrowserAdapter、BrowserImageShrink；UI：Controls/KeyShortcut、
  Model/{UnansweredReply,StartupTrace,DraftStore,CommandPalette}）+ UI/Assets/logo.png；
  UI 的 Design/、Rich/、Views/ 目录已无 .fs；`Wanxiang.UI.fsproj` 经 182 个 `Wanxiang2.*`
  本地包消费（36–217 行），宿主不再自持对应实现。对比 §2.1.2 基线（34 文件、6183 行）。
- [实测] **文档退役表述落地**：README.md 加文首退役声明（唯一入口 = wanxiang2 及其
  run-desktop.sh / run-pwa.sh / publish.sh / x），快速开始、开发、发布、项目结构等入口指向
  全部改指 wanxiang2，失效宿主路径引用（WireAgui.fs、Jcs.fs、MessageMap.fs、
  LICENSE-highlight 等）改指 wanxiang2 现行位置；docs/STATUS.md 由「收敛状态」改为
  「退役状态」表述。**§1.2 第三判据（STATUS/README 退役表述）就此达成。**

### 6.2 运行验证（执行报告，未独立复验）

- [执行报告] 双入口 0 错误编译；`dotnet publish` 含嵌套 PublishPwaBundle 全通过、EXIT=0；
  `x check` 架构合规全绿。
- [执行报告] 571 项基线已在 wanxiang2 侧验证——与 AGENTS.md 迁移状态注一致
  （记录于 assist/deletion-cutover 分支：570 通过、1 既有跳过、0 失败）。

### 6.3 遗留（未完成，不得表述为已废弃/已退役）

- 宿主壳 `src/` 物理删除未执行：三工程、`wanxiang.slnx`、`src/Directory.Build.props` 仍在。
- 根目录旧入口链未处置：`start.sh`、`run-tests.sh`、根 `tests/Wanxiang.Tests/`（实测仍有 40 个
  .fs 文件，完整口径待 DevOps 复核）、根 `tools/`（含 patch_avalonia_js.py 同名副本、
  package_release.sh、stress.sh 等）仍在。
- §1.2 第四判据未完成：kelivo/、site/、shots*/、packaging/、docs/（除 STATUS.md 的
  退役表述）的「保留 / 归档 / 删除」逐项判定未做（§4.1 处置表仍是方向，不是执行记录）。
- §1.3 整条（wanxiang2 成为唯一权威、wanxiang 仓库整体废弃后独立存续）在上述遗留
  与仓库级废弃动作（含 .gitmodules 挂载关系处置、归档/下线决定）完成前不成立。
