# 抽吸接续交接：迁移状态表核对（静态 + 动态双证）

## 项目与起点

- 项目：`~/Desktop/vibe/wanxiang`（旧仓库应用）。
- 被核对对象：迁移状态表位于新仓库蓝图 `AGENTS.md` 的「## 迁移状态」一节，表头在 173 行，数据行 174–222；其中 **175–222 共 48 个数据行**为本次逐行核对范围。174 行（事件账本）按既有决议维持原样，仅在移交项 c 中登记两处更正。
- 表所在路径说明：旧仓库 `docs/` 下的工作根 `wanxiang2/` 是工程师独立开发 checkout（未跟踪），`vendor/wanxiang2/` 是锁定 submodule（pin `c3a2e74abafd0daf98db28b6b8ede750f331e53c`）。本文件只登记核对结论，**不改动** `wanxiang2/AGENTS.md` 与 `vendor/wanxiang2/`（原因见「边界声明」与移交项 a）。
- 这是一个文档与状态核对闭环：没有修改任何 `.fs`/`.fsproj` 源码或测试，没有执行合并、推送或分支操作；全部执行凭据来自 DevOps 已观察的运行结果（见「动态运行凭据」）。

## 结论摘要

175–222 共 48 个数据行，原「接入旧仓库并退出旧实现」列多标注为「未开始」。经静态凭据（账本核对）与动态凭据（DevOps 实跑）双证核对，**48 行全部应更新为「已接线」**：旧工程整体退出、应用经 `Wanxiang2.*` 交付包消费、宿主调用点已改调新包。

- 归入「真欠接线」（包已完成而应用消费为零）的职责清单：**空**。
- 归入「库侧独立能力未做」（表里「独立能力」列并非「完成」，或点名库在 `libs/compose` 下不存在）的职责清单：**空**。

换言之，迁移表的「未开始」是文档过期，不是接线缺口；判断后续接线批次时不应再按这些标注立项。

## 逐行核对表

证据缩写：
- **[E1]** `src/` 下 `Wanxiang.{Config,Core,Server,Client,Store,Agent}` 六个旧工程整体不存在。
- **[E2]** 宿主包引用：`Wanxiang.App.fsproj` 引用 25 个 `Wanxiang2.*` 包、`Wanxiang.UI.fsproj` 约 170 个、`Wanxiang.Pwa` 2 个。
- **[E3]** compose 包图闭合：`ServerApp → WsConnection → {Search, HostLog, Attachments, ChatOrchestrator, ServerModel, ProviderCatalog, ConfigMutation, HostStatus}`；`ToolApproval ← PartialReplies ← ChatOrchestrator`；`ToolRegistry ← AgentRuntime`。
- **[E4]** 宿主保留文件已是包别名壳：`Icons.fs:8 = Wanxiang2.IconSet.IconName`；`CredentialStore.fs:15 = Wanxiang2.BrowserCredentials.CredentialStore`。
- **[E5]** `AppShell.fs:95-96` 实构造 `ReconnectLoop`；`:22-23` `open Wanxiang2.ChatScreen / Wanxiang2.Workspace`；`:1514` `HandleEvent(ev: WireEvent)`（Wire 包命名空间沿用 `Wanxiang.Protocol` / `Wanxiang.Core`，宿主调用点无需 open 即直接消费包类型）。
- **[E6]** 211 行 CopyConfirm 库存在：`vendor/wanxiang2/libs/CopyConfirm/` 含 `CopyConfirm.fsproj`、`AGENTS.md`、`tests/golden/copyconfirm.txt`、`obj/Release/Wanxiang2.CopyConfirm.0.0.0-local.nuspec`；经 `MessageCardView` / `ComposerDesk` 组合消费，宿主不直接引用。
- **[E7]** 222 行 `libs/Wire/AGENTS.md` 无未落地的宿主接线义务：其 `[TODO]`「旧仓库接线时确认」属**待验证**（接线已发生，确认归旧仓库 CodecTests 执行），另一条 commitId 字符串化为条件性未来协议项。

| 行号 | 职责 | 原标注 | 核对结论 | 证据 |
|---|---|---|---|---|
| 175 | 配置（AtomicTextFile、AppConfig，组合 ConfigStore） | 未开始 | 已接线 | [E1] `Wanxiang.Config` 工程不存在；[E2] 宿主引用 `Wanxiang2.ConfigStore` |
| 176 | 认证与配对（ClientToken、DesktopClient、PairingCode、FailureLimiter、WsHandshake，组合 PairingDesk） | 未开始 | 已接线 | [E1] `Auth.fs` 随 `Wanxiang.Config` 整体退出；[E2] 引用 `Wanxiang2.PairingDesk` |
| 177 | 领域状态与命令（Conversations、CommandId、PlainText、ToolSummary，组合 ChatDomain） | 未开始 | 已接线 | [E1] `Wanxiang.Core` 工程不存在；[E2] 引用 ChatDomain/PlainText/ToolSummary；线协议前置（222 行）已接 |
| 178 | 模型提供方传输（Sse、HttpRetry、JsonPick、Anthropic、Gemini，组合 ProviderTransports） | 未开始 | 已接线 | 前置（配置、线协议）均已接；[E2] 宿主引用 `Wanxiang2.ProviderTransports` |
| 179 | MCP 客户端（JsonRpc、McpProtocol、LineProcess、StreamableHttp，组合 McpClient） | 未开始 | 已接线 | [E1] `McpTransport.fs`/`McpClient.fs` 随 Server/Agent 旧工程退出；[E2] 引用 `Wanxiang2.McpClient` |
| 180 | 服务端编排的独立部分（Agui、Approvals，组合 AguiWire、ToolApproval、PartialReplies） | 未开始 | 已接线 | [E1] Agui 旧工程消失；[E3] `ToolApproval ← PartialReplies ← ChatOrchestrator`；Server 侧旧文件退出 |
| 181 | 搜索、附件、主机日志（TextMatch、MessageText、MediaSniff、AttachmentBlobs、JsonLinesLog，组合 Search、HostLog、Attachments） | 未开始 | 已接线 | [E3] `WsConnection` 下游含 Search/HostLog/Attachments；[E1] `SearchService.fs`/`AttachmentStore.fs`/`Stderr.fs` 随 Server 工程退出 |
| 182 | 存储与诊断（HostProbe、AttachmentRefs，组合 EventStore、Doctor） | 未开始 | 已接线 | [E1] `Wanxiang.Store` 工程不存在；ServerModel 转发归入包图 [E3] |
| 183 | 消息与工具（MessageSerde、MediaContent、SandboxFiles、ToolNames、RefreshingPool，组合 AgentContent、BuiltinTools、ToolRegistry） | 未开始 | 已接线 | [E1] Agent/Server 旧工程退出；[E3] `ToolRegistry ← AgentRuntime` |
| 184 | Agent 运行时（HttpFailureFacts、SharedHttpClients、AgentHistory、AguiMessageMap，组合 ProviderFailure、AgentRuntime） | 未开始 | 已接线 | [E1] `Wanxiang.Agent` 工程整体不存在；[E3] AgentRuntime 在包图 |
| 185 | 聊天编排（ContextTrim、TitleText，组合 ChatOrchestrator） | 未开始 | 已接线 | [E3] ChatOrchestrator 在 ServerApp 包图；[E1] 旧 ChatOrchestrator 源文件随 Server 工程退出 |
| 186 | 服务端视图与配置（PayloadFields、ModelListing，组合 ServerModel、ProviderCatalog、ConfigMutation、HostStatus） | 未开始 | 已接线 | [E3] 四组合者均在 `WsConnection` 下游；[E1] 旧 Server 视图源文件不存在 |
| 187 | WebSocket 连接（DeviceSightings、DeviceRules，组合 WsConnection） | 未开始 | 已接线 | [E3] `ServerApp → WsConnection`；[E1] `WsConnection.fs` 旧文件随 Server 工程退出 |
| 188 | 主机（ListenAddress，组合 ServerApp） | 未开始 | 已接线 | [E1] Server 工程剩余源文件不存在；[E3] ServerApp 为组合根 |
| 189 | 客户端连接（ReconnectBackoff、ConnectionIssue、TextSocket、CommitTimeline，组合 WsClient） | 未开始 | 已接线 | [E1] `Wanxiang.Client` 工程不存在；[E2] 宿主引用 `Wanxiang2.WsClient` 等 |
| 190 | 程序入口的非界面逻辑（CliArgs、RunModes、PwaAssets、ShutdownSignals、ClientTomlFile，组合 LaunchPlan、StoreLaunch、DesktopPairing） | 未开始 | 已接线 | [E2] `Wanxiang.App.fsproj` 引用 25 个 `Wanxiang2.*` 包（入口消费面） |
| 191 | 富文本渲染后端（HljsHtml、KatexHtml、…、BrowserCredentials 等，组合 RichAssets、RichJint、RichBrowser） | 未开始 | 已接线 | [E2] UI/Pwa 包引用；[E4] `CredentialStore.fs:15 = Wanxiang2.BrowserCredentials.CredentialStore` 已是别名壳 |
| 192 | UI 视图层纯逻辑（组合 MathTypeset、DesignRhythm；库 MarkdownBlocks、LinkChunks、SpacingRamp、SearchSegments、ApprovalCardText、DevicesModel） | 未开始 | 已接线 | [E2] `Wanxiang.UI.fsproj` 约 170 个 `Wanxiang2.*` 包引用覆盖各点名库 |
| 193 | UI 视图层纯逻辑第二批（组合 ConnectIssueText；库 ToolErrorSummary、ToolArgsPreview、DurationText 等 15 库） | 未开始 | 已接线 | [E2] 同上，UI 约 170 包 |
| 194 | UI 视图层纯逻辑第三批（库 ToastQueue、ToastDwell、GenerationForm、GenerationDraft、IdleSurface、StartReady、DraftSummaryText） | 未开始 | 已接线 | [E2] 同上，UI 约 170 包 |
| 195 | UI 视图层纯逻辑第四批（库 ComposerSendState、ToolCallState、CaretBreath、SidebarEmpty） | 未开始 | 已接线 | [E2] 同上，UI 约 170 包 |
| 196 | UI 视图层纯逻辑第五批（库 McpForm、ToolsPolicy、ProviderForm） | 未开始 | 已接线 | [E2] 同上，UI 约 170 包 |
| 197 | UI 视图层纯逻辑第六批（库 MessageMenu、SidebarSelection、ProviderProbeText、ExportView、ImportView） | 未开始 | 已接线 | [E2] 同上，UI 约 170 包 |
| 198 | UI 视图层纯逻辑第七批（库 RovingIndex、StartLayout、PairingCodeInput、ConversationSurface） | 未开始 | 已接线 | [E2] 同上，UI 约 170 包 |
| 199 | UI 视图层纯逻辑第八批（库 ExchangeJump、RevealFlash、BreadcrumbAction、ModelPickerList） | 未开始 | 已接线 | [E2] 同上，UI 约 170 包 |
| 200 | UI 连接流程记账（库 ReconnectLoop） | 未开始 | 已接线 | [E5] `AppShell.fs:95-96` 实构造 `ReconnectLoop`（connectAttempt/reconnectCts/reconnectDelayMs 旧字段已换） |
| 201 | UI 视图层纯逻辑第九批（库 MountWindow、ScrollFollow、SidebarRowState、SearchRequery、ToolOverview） | 未开始 | 已接线 | [E2] 同上，UI 约 170 包 |
| 202 | UI 纯模型 10.1（PinyinInitials、FuzzyMatch、MessageLink、ModelVision、MediaTypes，组合 CommandPalette） | 未开始 | 已接线 | [E1] Model 旧文件随工程退出；[E2] UI 约 170 包含 CommandPalette |
| 203 | UI 纯模型 10.2（ConversationSummary、SearchHit、RevealTarget、ForkLineage、PendingDeletion、HostHealth、LocalDraftControls、ProviderPresets，组合 ConversationList） | 未开始 | 已接线 | [E2] UI 约 170 包含 ConversationList；旧 Model 点名文件不存在 |
| 204 | UI 纯模型 10.3 模型层（MessageView、UiPrefs、HostCatalog 等 12 库，组合 Catalog、ConversationRuns、Outbox） | 未开始 | 已接线 | [E2] UI 约 170 包含三组合者 |
| 205 | UI 非视图余项（ConversationTransfer、ErrorRecovery、UrlSchemeRegistration、ImageFit、ProbeCounter、StartupTrace、ClientConnections，组合 ConversationExport、ImageShrink） | 未开始 | 已接线 | [E2] UI 约 170 包；[E4] `CredentialStore.fs` 纯部分已为包别名壳 |
| 206 | UI 壳层 AppShell 纯逻辑（ServerErrorRoute、HostRequests、EscapeChain；PendingDeletion、ConfigFeedback 增补；组合 ConversationRuns 的 GenerationOutcome、CommandPalette 的 ShellPalette） | 未开始 | 已接线 | [E5] AppShell 已是包消费壳；[E2] UI 约 170 包 |
| 207 | UI 壳层 AppShell 第二批（UploadFacts；DraftStore 增补 DraftRestore；组合 Catalog 的 idleModelChip） | 未开始 | 已接线 | [E5][E2] 同上 |
| 208 | UI 壳层 AppShell 协议事件（ConnectionIssue.ofPairingFailure、DownloadBuffers.AppendBase64、HostCatalog.ProbeModels、ApprovalCardText、ConfigFeedback） | 未开始 | 已接线 | [E5] `AppShell.fs:1514` `HandleEvent(ev: WireEvent)` 事件面经包类型消费 |
| 209 | UI 壳层 AppShell 余项（ForkAnchor、OlderPageLoad；UiPrefs.next/switchedText；MessageView 的 RegenerateGate/ReplyGate；NavigationSnapshot.autofocusDrawer） | 未开始 | 已接线 | [E5][E2] 同上 |
| 210 | UI 有状态对象（DeferredReveal、LinkInbox、ChangeWatch、BoundedWait、OnboardingFlow、ProbeSlot、SectionMemory、SearchSession、RowFocus；组合 DraftSession） | 未开始 | 已接线 | [E2] UI 约 170 包；旧 AppShell 对应字段退出 |
| 211 | UI 有状态对象第二批（库 CopyConfirm、CountdownTicker、ScrollTween、BreathPhase、CardReconcile、BlockDiff、ThemeTween、ProgressThrottle、HighlightQueue） | 未开始 | 已接线 | [E6] CopyConfirm 库真实存在并产包，经 MessageCardView/ComposerDesk 组合消费；其余点名库在 UI 包引用面内 |
| 212 | UI 控件第一批（写手二：库 PopupMenu、OverlayLayer、ToastStrip、FieldCheck、CopyButton、ImeShield、KeyChord、FocusOrigin、InputSense；组合 OverlayDesk） | 未开始 | 已接线 | [E2] UI 约 170 包含 OverlayDesk |
| 213 | UI 控件第二批（写手二：库 BrandMark、ApprovalPanel、PalettePanel、ModelSwitchPanel、ImportSheet、ExportSheet、ShellLayout；组合 ApprovalDesk、PaletteDesk、ModelSwitchDesk、TransferDesk、ShellDesk） | 未开始 | 已接线 | [E2] UI 约 170 包含五组合者；旧七个 Views 随退出 |
| 214 | UI 控件第三批（写手二：库 SettingsFrame、ProvidersPane、ToolsPane、GeneralPane、DevicesPane、OnboardingPane；组合 SettingsScreen） | 未开始 | 已接线 | [E2] UI 约 170 包含 SettingsScreen |
| 215 | UI 控件第四批（写手二：库 ConversationRows、SidebarSearch、BatchBar、HostSwitcher、SidebarBrand、ListEmptyCard、ConfirmPane、PromptPane、TextEditPane、ConnectPane、SessionPrefsPane、ShortcutHelpPane；组合 SidebarDesk、DialogsDesk） | 未开始 | 已接线 | [E2] UI 约 170 包含 SidebarDesk/DialogsDesk；旧 Views/Sidebar.fs、Views/Dialogs.fs 退出 |
| 216 | UI 主题第一批（库 ThemePalette、ThemeMetrics、MotionSwitch、IconSet；组合 Theme） | 未开始 | 已接线 | [E4] `Icons.fs:8 = Wanxiang2.IconSet.IconName` 已是包别名壳；[E2] UI 包引用含 Theme |
| 217 | UI 正文第二批（库 ThemeFonts；组合 MathFigure、CodeTint、MarkdownView、ReadingLook） | 未开始 | 已接线 | [E2] UI 约 170 包含四组合者 |
| 218 | UI 消息卡第三批（库 RevealPane、FoldHeader、BusyRing、ThinkingBlock、ToolCallCard、AttachmentChip、ActionStrip、ErrorPanel、UserBubble、StreamCaret、UsageFoot；组合 MessageCardView） | 未开始 | 已接线 | [E2] UI 包引用含 MessageCardView；[E6] CopyConfirm 经其组合消费 |
| 219 | UI 控件第四批（写手一：库 SkeletonList、ChatEmpty、ChatScroller、ExchangeNav、ComposerBox、AttachmentTray、ModelChip；组合 ChatPane、ComposerDesk） | 未开始 | 已接线 | [E2] UI 包引用含 ChatPane/ComposerDesk |
| 220 | UI 第五批（写手一：库 ChatHeader；组合 ChatScreen、Workspace） | 未开始 | 已接线 | [E5] `AppShell.fs:22-23` `open Wanxiang2.ChatScreen / Wanxiang2.Workspace`，AppShell 改调两组合者 |
| 221 | UI 第五批（写手二：组合 ProtocolDispatch、ShellCommands） | 未开始 | 已接线 | [E5] `AppShell.fs:1514` `HandleEvent(ev: WireEvent)` 在宿主侧由协议事件包类型驱动；[E2] UI 包引用含两组合者 |
| 222 | 线协议（Wire） | 未开始 | 已接线 | [E7] `libs/Wire/AGENTS.md` 无未落地宿主接线义务；[E5] Wire 包命名空间沿用 `Wanxiang.Protocol`/`Wanxiang.Core`，宿主直接消费 `WireEvent`；src 无旧 Wire 工程/文件 |

## 动态运行凭据（已观察执行结果，来自 DevOps）

以下为在当前工作树实际跑出的结果，非静态推断：

- 分支：`assist/deletion-cutover`，HEAD `d00d59d`。
- `dotnet build wanxiang.slnx -c Release`：0 错误、11 既有测试警告（均为本轮未修改的测试文件）。
- `./run-tests.sh -j4 --large-history`：退出码 0；全量 571 用例（UI 346 + 非 UI 225），570 通过、1 个既有跳过（10⁶ 冷启动手动基准沿用原 skip）、0 失败。
- `dotnet format --verify-no-changes`：退出码 0。
- `git diff --check`：通过。
- `vendor/wanxiang2` pin = `c3a2e74abafd0daf98db28b6b8ede750f331e53c`，与预期一致。
- `tests/test-counts.baseline`（UI=346 / NonUI=222 / LargeHistory=3）与实测吻合：基线记录的是下限，实测 UI 346 持平、非 UI 225 ≥ 基线 222、大历史 3 项分级成立。

## 移交项（属新仓库/用户职权，本道路不擅动）

a) **权威更新归属**。迁移表的权威在**新仓库** `AGENTS.md`（`vendor/wanxiang2` main 一侧）；旧仓库任务按约束不得改动 submodule，也不得改工作根 `wanxiang2/AGENTS.md`（那是未跟踪的开发副本，改它会与权威处产生两份竞争的过期版本，正是 stale-documentation 要避免的假修复）。上述逐行结论应由职权方据此在权威处更新；本 handoff 提供可直接采纳的逐行清单。

b) **主干合并**。接线目前处于 `assist/deletion-cutover` 分支态：领先 `main`（`f873c40`）80 个提交、落后 1 个，`main` 上没有 `docs/handoffs/`。是否合入 `main` 属用户批准职权（项目约束：`main` 只由用户批准合并、分叉需显式裁决），本道路未动任何分支。

c) **174 行两处更正**。174 行（事件账本）的 `[TODO] 合入旧仓库 main` 经核实**仍然有效、并非过期**（`extract-ledger@8f39e84` 尚未并入 `main`）；但该行内引用的 commit hash `84017ef` 已过期，应更正为 `8f39e84`。另注：核对过程中一个曾流通的基线 hash `8f60b26` 经新旧两仓库 git 查证不存在，属无效前提；以 `d00d59d` 为真实 HEAD。

## 边界声明

- 本文件属旧仓库 `docs/handoffs/` 登记，只新增这一份 markdown；不改动 `vendor/wanxiang2/`（submodule 只读），不改动工作根 `wanxiang2/AGENTS.md`（未跟踪开发副本），不改动任何 `.fs`/`.fsproj` 源码或测试，不执行命令、不跑 build/git。
- 抽吸法工程内核（应用接线 + Release 构建 + 全量测试 + 格式检查）在当前分支已达成，并满足项目「完成定义」：应用退出旧实现、宿主改调新包，而非仅库侧绿。剩余收尾只有两项职权外事项：文档权威更新（a）与主干合并（b）。

## 核对中发现、上文未覆盖的不一致处

1. 174 行与 222 行等行的「未开始」措辞指向「本轮报告的接线步骤」一类当时文档，本次核对未逐份重读那些历史报告原文；判定改以「旧工程不存在 + 包引用 + 包图/宿主调用点」三类可复查的静态凭据为准，不影响逐行结论。
2. `docs/handoffs/2026-10-15-deletion-cutover.md` 当时记录默认全套为 561 通过（UI 340、非 UI 221）、大历史 223；本轮全量为 571（UI 346、非 UI 225）。数字上升是后续批次迁入测试的正常增量，不是矛盾，但引用旧 handoff 数字者应以本轮基线为准。
3. `tests/test-counts.baseline` 的 NonUI 记 222 是下限而非精确值，实测非 UI 为 225；更新权威文档或基线时不要把「吻合」误读成「相等」。
