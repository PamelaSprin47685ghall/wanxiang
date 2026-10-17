# 万象

> **项目本体已迁移至 `wanxiang2/` 子模块（2026-10-17）：本旧仓库不再是产品本体，退役进行中。**
> 万象——把「对话」当成账本来管的 AI 聊天客户端——唯一入口现在是 `wanxiang2/`：
> 启动、构建、测试、发布全部经它自己的入口——`wanxiang2/run-desktop.sh`（桌面）、
> `wanxiang2/run-pwa.sh`（PWA）、`wanxiang2/publish.sh`（自包含发布）、`wanxiang2/x`（库与组合者的
> 测试/检查/产包），自有 CI（`wanxiang2/.github/workflows/ci.yml`、`release-publish.yml`）。
> 本仓库宿主壳 `src/` 已净化为过渡入口薄壳（职责全部经 wanxiang2 本地包消费）；
> 宿主壳物理删除与历史资产逐项处置是遗留项，见 `PLAN.md` §1.2、§4.1 与 §6「退役执行进展」。
> 以下产品描述适用于 wanxiang2 中的现行实现，在本仓库保留作历史与导航参考。

一个把「对话」当成账本来管的 AI 聊天客户端。

每个实例既是主机也是客户端：主机跑模型、存对话、管密钥；客户端只是它的一个视图。
两者之间只有一条 WebSocket，本机自己连自己也走同一条路径——没有第二套隐藏行为。线上走 [AG-UI](https://agentclientprotocol.com) 1.0 事件流。

- **桌面端**（Linux x64 / Windows x64 / macOS arm64·x64）：完整节点，主机 + 界面
- **PWA**：纯前端客户端，由主机托管，通过配对码接入

界面在桌面与浏览器之间共用同一套 F# 代码，所以两端长得一样、行为一样。

---

## 快速开始

现行入口在 `wanxiang2/` 内运行（见文首声明）：

```bash
./run-desktop.sh    # 产包 → 构建 → 启动桌面（主机 + 界面）
./run-pwa.sh        # 产包 → 构建 PWA → 本机静态托管（默认端口 8000）
```

以下为旧仓库入口期的原始记录（入口已退役过渡，宿主壳删除与资产处置见 `PLAN.md` §6）：

```bash
./start.sh
```

首次运行会构建整个解决方案，然后在 `127.0.0.1:8765` 起服务并打开桌面界面。
浏览器访问同一地址即可得到 PWA。

界面里第一件事是**添加一个模型服务**：设置 → 模型服务 → 添加模型服务。
预设已经填好端点与常见模型，你只需要粘贴密钥。之后就能开始对话。

配置文件与数据默认落在 `.scratch/pwa-run/`；正式使用请指定自己的位置：

```bash
./start.sh ~/.config/wanxiang/config.toml ~/.local/share/wanxiang/data
```

### 环境要求

- .NET 10 SDK，且需要 **ASP.NET Core 运行时**与 **wasm-tools** workload

  ```bash
  dotnet workload install wasm-tools
  ```

  若系统 SDK 缺少 ASP.NET Core 运行时（`dotnet --list-runtimes` 里看不到
  `Microsoft.AspNetCore.App`），装一份用户级 SDK 即可，无需 root：

  ```bash
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
  export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
  dotnet workload install wasm-tools
  ```

- 桌面界面需要图形环境；只跑主机与 PWA 不需要：

  ```bash
  wanxiang --server --pwa --client=false
  ```

---

## 运行模式

三个开关自由组合（命令行 > TOML > 程序默认）：

| 组合 | 效果 |
|---|---|
| `--server --client --pwa` | 默认：本机全功能节点 |
| `--server --pwa --client=false` | 无界面主机，只服务浏览器与远程客户端 |
| `--client --server=false` | 纯客户端，连别人的主机 |
| 三个全关 | 只跑 `doctor` 自检并退出 |

自检与修复：

```bash
wanxiang --server=false --client=false --pwa=false        # 只读检查
wanxiang --server=false --client=false --pwa=false --fix   # 检查并修复日志尾部
```

`doctor` 会逐项报告配置、数据锁、日志重放、监听端口与附件可达性。

---

## 连接与配对

主机不接受匿名连接。首次接入走配对码：

1. 客户端点「连接主机」，默认就是配对码输入界面（访问令牌放在「更多」里）
2. 主机终端（stderr）打印一个 6 位码，5 分钟内有效；也可以在桌面端 设置 → 设备 生成配对邀请（含二维码）
3. 点「提交配对码」后客户端拿到长期令牌，之后自动重连

出于安全考虑，从主机本机打开 PWA 也**不会**免配对（见 `docs/security/loopback-pwa-pairing.md`）。

令牌即身份。桌面端存 `client.toml`，PWA 存 IndexedDB（按主机 `instanceId` 索引，
URL 不是身份）。要吊销某个客户端，在配置里把对应 `[[auth.clients]]` 的 `revoked` 置为 `true`
——主机会立刻断开它。

配对失败有限流：同一远端地址每分钟 5 次失败后冻结 5 分钟。

### TLS

要对外提供服务，给出 PEM 证书链与私钥即可，主机自己起 HTTPS/WSS：

```toml
[network]
listen = "0.0.0.0:8765"
tlsCertPath = "/etc/letsencrypt/live/example.com/fullchain.pem"
tlsKeyPath  = "/etc/letsencrypt/live/example.com/privkey.pem"
```

两个路径必须同时给出，且文件必须存在——只填一半会被判为配置错误而拒绝启动，
不会静默退回明文。置于反向代理之后同样可行，那时把 `listen` 留在回环地址。

> 监听非回环地址而**又没配证书**时，启动日志会打出 `insecure-listen` 告警：
> 访问令牌会以明文经过网络。这种组合请只用于完全可信的网段。

---

## 配置

唯一配置文件是 TOML，也是配置的唯一权威。**日常使用不需要手改它**——
界面里改模型服务、MCP 与生成默认值都会原子重写这个文件。

```toml
configVersion = 2
instanceId = "0198d3a1-0000-7000-8000-000000000001"

[runtime]
server = true
client = true
pwa = true

[network]
listen = "127.0.0.1:8765"
# 对外服务时给出 PEM 证书与私钥即可启用 HTTPS/WSS；留空则明文
tlsCertPath = ""
tlsKeyPath = ""
maxAttachmentBytes = 67108864
chunkSizeBytes = 262144

[generation]
temperature = 0.7
maxContextMessages = 200      # 单次请求最多携带多少条历史
autoTitle = true              # 首轮对话后自动起标题
maxToolRounds = 12            # 工具连环调用的上限
thinkingBudget = 0            # 思维链预算（token）；0 = 不开，可按对话覆盖

[tools]
callTimeoutSeconds = 30
# 文件工具默认不注册。只有配了沙箱根目录才会出现，
# 且只能读这些目录里的文件。
fileReadRoots = ["/home/me/notes"]

[providers.openai]
kind = "openai"               # openai | anthropic | gemini
label = "OpenAI"
baseUrl = "https://api.openai.com/v1"
apiKey = "sk-..."
models = ["gpt-5", "gpt-4o", "gpt-4o-mini"]
defaultModel = "gpt-4o"
timeoutSeconds = 120
maxRetries = 0
enabled = true
promptCaching = false         # 仅 anthropic 原生传输；默认关，因为缓存写入更贵

[mcp.filesystem]
label = "本地文件"
command = "npx"
args = ["-y", "@modelcontextprotocol/server-filesystem", "/home/me/notes"]
callTimeoutSeconds = 60

[mcp.remote]
label = "远程工具"
url = "https://example.com/mcp"   # 与 command 二选一
```

未知字段会导致**整份配置被拒绝**——拼错一个键不会静默生效。
配置非法时主机继续用上一份有效配置运行，并把原因写到 stderr。

### 关于模型服务

一个模型服务是「一组凭据 + 一个端点 + 一批模型」。换模型不必改配置：
输入框左下角就能切，切换只影响当前对话。

`kind` 决定用哪种传输：

| kind | 协议 | 适用 |
|---|---|---|
| `openai` | OpenAI 兼容 HTTP | OpenAI、DeepSeek、Kimi、智谱、通义、硅基流动、OpenRouter、Groq、xAI，Ollama / LM Studio 等本机推理，以及 Anthropic 与 Gemini 的兼容端点 |
| `anthropic` | Anthropic Messages API 原生 | Claude |
| `gemini` | Gemini generateContent 原生 | Gemini |

兼容端点够用时就用 `openai`。原生传输的意义在于兼容层丢掉的东西：

```toml
[providers.claude]
kind = "anthropic"
baseUrl = "https://api.anthropic.com"   # 带不带 /v1 都认
apiKey = "sk-ant-..."
models = ["claude-sonnet-4-5", "claude-opus-4-1"]
defaultModel = "claude-sonnet-4-5"

[providers.gemini]
kind = "gemini"
baseUrl = "https://generativelanguage.googleapis.com"   # 缺版本段时补 /v1beta
apiKey = "AIza..."
models = ["gemini-2.5-pro", "gemini-2.5-flash"]
defaultModel = "gemini-2.5-flash"
```

两种原生传输都支持流式、工具调用、多模态入参、思维链与用量统计，
`timeoutSeconds` / `maxRetries` / `headers` 与 OpenAI 传输同样生效——
`maxRetries` 默认 0：计费调用默认不自动重发。配成正数才会在收到响应头之前重试，
一旦开始吐字就绝不重发，
不会让你看到同一段回答出现两次。密钥走请求头而不是 URL 查询参数。

---

## 数据

```
<data>/
  events/YYYY-MM-DD.jsonseq    事件日志：一条 RFC 7464 记录 = 一次原子提交
  attachments/ab/cd/<sha256>    附件，内容寻址（按哈希前四位分片）
  attachments/.tmp/             在途上传
  lock                          单进程锁
```

事件日志是唯一权威，界面看到的一切都是它折叠出来的投影。
重启只靠 TOML + 事件日志 + 附件目录就能恢复全部永久状态。

同一数据目录同时只允许一个主机进程。日志尾部若因断电截断，
启动时自动丢弃尾部不完整记录并继续写入，不会静默丢中间数据。

> **格式已改变（2026-09-29）**：日志改用 [RFC 7464](https://www.rfc-editor.org/rfc/rfc7464) JSON Text Sequences
> （`RS` + JSON 对象 + `LF`，媒体类型 `application/json-seq`），消息载荷采用 [AG-UI](https://agentclientprotocol.com) 1.0 消息模型，
> 规范化哈希采用 [RFC 8785](https://www.rfc-editor.org/rfc/rfc8785) JCS。
> **旧 `.ndjson` 数据不兼容、不迁移**；`doctor` 会识别并明确指出。

### 协议与落盘已切换到标准承载（2026-09-29）

| 层 | 承载 | 实现（wanxiang2 现行位置；旧宿主路径已随工程退出） |
|---|---|---|
| 线上（WebSocket `/ws`） | **AG-UI 1.0** 事件（标准面 + `wanxiang.dev/*` CUSTOM 面） | `compose/AguiWire` + `libs/Wire` |
| 落盘（`events/*.jsonseq`） | **RFC 7464** JSON Text Sequence（`RS`+JSON+`LF`），`commitId`/`bootId` 字符串承载 | `libs/JsonSeq` + `libs/Ledger`（经 `compose/LedgerStore` 组合） |
| 幂等哈希 | **RFC 8785** JCS | 第三方 `Jcs.Net` + `libs/Canonical`（手写 `CanonicalJson` 已删） |
| 消息模型 | AG-UI 消息对象为落盘权威，MAF `ChatMessage` 为运行时 | `libs/AguiMessageMap` |

56 个 `WireEvent` 语义变体**保留**（语义层不换，承载层换）；
`protocol.hello` / `protocol.upgrade-required` 已删除（版本改 in-band）；
入站未知事件类型**宽容忽略**（AG-UI 增量安全），不再断连。

删除对话后，只被它引用的附件会在后台被回收。回收刻意保守：
只要还有任何存活对话（含其分叉的祖先链，以及已删除的历史消息）
可能展示某个附件，它就留着——多占一点磁盘远好过把历史变成「内容已丢失」。

---

## 对话里能做什么

- **改参数**：顶栏滑块图标，逐对话调 temperature / 最大输出 / 系统指令 / 工具
- **重新生成**：把上一轮模型回复作废后重跑
- **编辑并分叉**：改任意一条自己的消息，从那里长出一个新对话，原对话不动
- **批量管理**：对话右键 →「多选」，之后点行即切换选中，底部条可整批置顶 / 归档 / 删除
- **置顶 / 归档 / 导出**：对话右键
- **切模型**：输入框左下角，按模型服务分组；`Ctrl` `M` 直接打开选择器
- **附件**：文本文件内联为代码块；二进制按传输能力直接进模型，进不去的会写明原因

| 传输 | 图片 | PDF | 音频 |
|---|---|---|---|
| `openai` | 是 | — | — |
| `anthropic` | 是 | 是 | — |
| `gemini` | 是 | 是 | 是 |

  空格不是「悄悄丢掉」：传输吃不下的类型会附一句说明，
  不会让你以为模型看过那份文件。单个二进制附件的内联上限是 16 MiB。

每条消息都带时间；消息下方的操作条平时隐藏，鼠标悬停、键盘聚焦或触屏点一下消息时出现。
右键菜单另有「复制消息链接」（`wanxiang://` 深链接）与「从此分叉」。

长行默认横向滚动，代码块右上角可切换折行（设置里也有全局默认值）。
代码块由 [highlight.js](https://highlightjs.org) 着色，覆盖 90 种语言，
`ts` / `py` / `F#` 这类简写会自动归一到规范语言名。数学公式由
[KaTeX](https://katex.org) 排版：`$…$` 行内、`$$…$$` 独占一行居中，
分式、上下标、根号、求和上下限、矩阵都按 TeX 的比例呈现。
两者都跑在本机——桌面端用进程内的 JS 解释器，浏览器端用宿主引擎，
不联网、不把你的代码或公式发给任何人。

失败会就地显示一张卡片：说明是什么问题、该改哪里，可重试的给按钮。
不再只是一行看不懂的异常文本。

### 完整导出

对话菜单中的「导出」会读取导出开始时的完整已提交历史，不受聊天窗口默认
200 条快照和已加载页数影响；不必先打开对话或滚动到顶部。读取期间显示进度，
全部取齐后再点「保存 Markdown」。取消、断线、超时或主机拒绝时，不会把
半份历史当成完整文件保存；重新导出会从新的时点开始。

导出遵循原对话的分叉与删除语义，保留消息时间、工具调用与结果，以及附件说明。
Markdown 不打包附件文件、不包含尚未提交的生成与排队内容，**不能代替完整数据备份**。
当前内存导出上限为单条消息 JSON 16 MiB、累计消息 JSON 和生成的 Markdown 各 64 MiB；
超过上限会明确失败，不会静默截断。客户端与主机都需更新到支持完整导出的版本。

### 发送与草稿

发送后，内容会先保留在输入区上方，区分「发送中」「已排队，尚未保存」和「未确认」。
只有主机确认提交后，这份待发送内容才会移除；断线或被拒绝时可以原样重试，不会重复保存。
生成过程中可用「排队发送」或发送快捷键补充消息，停止按钮仍单独保留。
切换对话会保留各自的文字和附件草稿，断线时仍能继续输入。

**本地草稿默认开启**：输入框里没发出的文字、附件名称，以及尚未被主机确认保存的消息，
会保存在**这台设备**上，关闭窗口或刷新网页后可以恢复。

- 只存本机：桌面写 `$WANXIANG_HOME`（默认 `~/.config/wanxiang`）下的 `drafts.json`，仅当前用户可读写；
  PWA 写浏览器的 IndexedDB。不会上传，不会同步到主机或其他设备，也不会进入对话记录。
- 按主机和对话分开保存；对话被删除后，它的草稿会随之清理。
- 只保存附件的名称与大小，不保存文件内容；恢复后附件会显示「需重选」，要重新选择文件才能发送。
- 未确认的消息恢复后仍是「未确认，可重试」，重试沿用原来的标识，主机会核对，不会重复保存。
- 草稿可能包含敏感文字。想关闭：设置 → 外观与交互 → 本机草稿 → 关闭「保存未发送的草稿」，
  已保存的内容会立即删除。想清空：同一处的「清除本地草稿」。

已排队但尚未插入的消息仍不是永久历史，只有主机确认提交才代表已保存；
本地草稿只是这台设备上的便利副本，不是第二份对话数据库。

### 主机状态与检查主机

主机发现配置文件无效（例如手动编辑出错）时，会继续使用上一份有效配置，
并通知已连接的客户端：侧栏的主机状态行显示「配置有误」，设置 → 关于与系统诊断 → 主机状态里可以看到原因。
修好配置后提示会自动消失。原因摘要已去除密钥并限制长度。

同一处的「检查主机」按钮会让主机运行只读的 `doctor` 自检并显示每一项结果，
内容与命令行 `doctor` 一致（运行中的主机自己持有的端口和数据锁不会被当作故障）。

### 快捷键

macOS 上 `Ctrl` 换成 `⌘`。完整列表按 `Ctrl` `/` 查看。

| 按键 | 作用 |
|---|---|
| `Ctrl` `K` | 命令面板：搜对话、操作与设置（支持拼音首字母） |
| `Ctrl` `Shift` `F` | 在侧栏搜索全部消息 |
| `Ctrl` `N` | 新建对话 |
| `Ctrl` `,` | 打开设置 |
| `Ctrl` `M` | 切换模型 |
| `Ctrl` `B` | 展开 / 折叠侧栏 |
| `Ctrl` `1`~`9` | 跳到侧栏第 n 个对话 |
| `Ctrl` `↑` / `↓` | 跳到上一条 / 下一条提问 |
| `Ctrl` `L` | 聚焦消息输入框 |
| `Ctrl` `Shift` `C` / `G` | 复制最新回复 / 重新生成 |
| `Ctrl` `Shift` `E` | 导出当前对话为 Markdown |
| `Ctrl` `Shift` `S` | 切换主题 |
| `Ctrl` `Z` | 撤销刚才的删除（提示条还在时） |
| `Ctrl` `/` | 快捷键列表 |
| `Ctrl` `Enter` | 设置表单保存；或按偏好发送 |
| `Enter` / `Shift` `Enter` | 发送 / 换行（可在设置里互换为 `Ctrl` `Enter` 发送） |
| `P` / `F2` / `Delete` | 置顶 / 重命名 / 删除当前对话（对话行聚焦时） |
| `Esc` | 关闭弹层 / 取消编辑 / 停止生成 |

---

## 开发

现行开发入口在 `wanxiang2/` 内运行：`./x test <lib>` 逐库构建与测试、`./x check` 架构合规检查、
`./x pack` 打交付包、`./x compose` 清缓存后从 `artifacts/feed` 干净消费测组合者、`./x all` 依次执行全部；
CI 见 `wanxiang2/.github/workflows/ci.yml`。以下为旧仓库过渡期构建链的原始记录（宿主壳与这条链本身待处置）：

```bash
git submodule update --init -- wanxiang2
tools/wanxiang2-feed.sh
dotnet build wanxiang.slnx -c Release
./run-tests.sh -j 4
```

应用只通过 `wanxiang2/artifacts/feed` 中的 NuGet 包消费新库，不引用新库源码。
根目录 `wanxiang2/` 是正式 submodule，跟踪远端 `main`；宿主提交记录确切 gitlink。
`vendor/wanxiang2` 已退役，不再作为候选路径。切换抽吸分支或更新子模块提交后，重新执行 `tools/wanxiang2-feed.sh`：
当前包版本仍为 `0.0.0-local`，不能凭文件存在或版本号判断包是新的。脚本核对 Git 索引中的
submodule pin、干净源码、完整包集与逐包 SHA-256；都符合完成回执时才复用。
缺少回执、缺包、字节变化或 pin 更新时，仍调用现有的 `x pack` 完整产包，再清理本仓库的
同版本包缓存与 restore 产物。失败不留下成功回执，也不继续运行旧测试。

`tools/wanxiang2-feed.sh --check` 只核验，不构建或初始化；`--rebuild` 强制重建。
`--source-check` 只检查 Git 源码关系和二进制禁入，不要求已有包。
`--update` 是唯一主动取远端最新 `main` 的入口，只允许 fast-forward，不编译、不暂存。
有本地未发布提交、分叉或未提交改动时拒绝覆盖。先把新库提交发布到远端，再更新并用
`git add wanxiang2` 记录本次消费的 pin；否则干净 clone 可能取不到私有提交。
普通构建和运行不会联网更换源码。工具需要 Bash、Git 和 Python 3.11+。
两仓索引都禁止 `.tgz`、`.nupkg`、`.snupkg`，不因文件小而例外；本地 feed 只在忽略目录中生成。
本仓使用 `.nuget/packages`；重定向该目录或用 `NUGET_PACKAGES` 指向其他缓存会被拒绝，
而不是擅自清理用户的全局目录。
回执是本机包交付记录，不是签名、供应链认证或所有构建环境的证明。

`start.sh` 也先经过同一包源检查，再构建应用；产包或构建失败时不启动旧程序。
`WANXIANG_SKIP_BUILD=1` 仅复用由该入口构建且仍匹配当前包源的应用，不能绕过换 pin 后的重建。

### 本机运行状态

桌面与主机默认使用主工作树的 `.Herdchestra/`：`config.toml`、`client.toml`、`ui.json`、
`drafts.json` 与 `data/` 均在其中。linked worktree 使用同一主工作树状态；独立仓库、
submodule 和非 Git 工作目录各自隔离。应用只查询 Git 的 worktree 拓扑，不把业务数据写进
Git objects、refs、hooks、config 或 info/exclude，不读取或迁入旧 `.git/wanxiang*` 数据。

`./start.sh` 不再强制使用旧 XDG 默认目录。显式的 `--config`、`--data`、`--bundle` 与
`WANXIANG_HOME` 仍可指定独立的本机位置，但不得在 `.git` 内，也不能通过符号链接绕入。
不自动扫描、搬迁或删除已有的用户级配置与聊天数据。PWA 仍使用浏览器存储；远程 C/S 访问
不等于 Git 数据同步，本次不改其协议。规则和静态验收入口见 `docs/contracts/source-and-local-state.md`。

### 测试

```bash
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
./run-tests.sh -j 4                  # 界面分片 + 非界面全套
./run-tests.sh -j 4 --large-history  # 另跑大历史档（10^5 提交冷启动等）
tools/stress.sh -n 3 --hog 4         # 高负载下重复跑全套，输出 flake 报告
python3 -B -m unittest discover -s tools/tests -p test_wanxiang2_feed.py -v  # 包交付入口的故障回归
```

`run-tests.sh` 每次先核验包源，构建成功后把包清单指纹写到测试输出目录。
`--no-build` 只接受由该入口构建、且仍对应当前包源的二进制；缺回执或依赖变更时，
请不带 `--no-build` 重跑。测试期间切换 pin 或更改包源也会使本轮验收失败。
这不检查 `--no-build` 所跳过的应用源码变更；修改应用源码后仍应正常构建。

CI（`.github/workflows/ci.yml`）在每个 PR 及 `extract-ledger` 的 push 上跑应用门禁、核对各组测试数量不减少、
冒烟运行打包产物（含 PWA 补丁检查）；`stress.yml` 每晚跑一次压力测试。
CI、发布和压力任务都先检出锁定的新库并生成包源，再 restore/build；不能用新库自己的 CI 代替应用验收。

### 发布

现行发布线在 `wanxiang2/`：`./publish.sh [--draft] <rid|--all> [version]`——四个 RID
（linux-x64 / win-x64 / osx-arm64 / osx-x64）自包含发布、打包 tar.gz/zip + SHA-256，
`--draft` 把产物挂到 GitHub **草稿** Release；CI 侧见 `wanxiang2/.github/workflows/release-publish.yml`。

以下为旧仓库发布线记录：推 `v*` 标签会触发 `.github/workflows/release.yml`：构建 Linux / Windows / macOS（Intel、Apple 芯片）
四个自包含包（无需安装 .NET），挂到一个**草稿** Release，不会自动发布。`docs/STATUS.md` 已改为退役状态声明（原发版步骤见其 git 历史）。

### 项目结构

| 项目 | 职责 |
|---|---|
| `wanxiang2/libs` | 独立职责：账本、配置、协议、模型、传输、界面行为等；库之间不相互引用 |
| `wanxiang2/compose` | 经交付包组合独立职责，形成 EventStore、ServerApp、WsClient、各界面 Desk 等能力 |
| `wanxiang2/app/Desktop` | **现行桌面入口**（已纳入 `wanxiang2.slnx`） |
| `wanxiang2/app/Pwa` | **现行浏览器入口**（browser-wasm，已纳入 `wanxiang2.slnx`） |
| `Wanxiang.UI` | （退役中·过渡遗留）宿主 UI 壳已净化：仅剩 `Model/` 少量 IO 壳与 `Controls/KeyShortcut.fs`，实现全部经约 180 个 `Wanxiang2.*` 本地包消费；待物理删除 |
| `Wanxiang.App` | （退役中·过渡遗留）旧桌面入口薄壳，待物理删除 |
| `Wanxiang.Pwa` | （退役中·过渡遗留）旧浏览器入口薄壳（browser-wasm），待物理删除 |

### 开发期工具

```bash
# 无需真实密钥的 mock，同时讲三种协议
# （/v1/chat/completions、/v1/messages、:streamGenerateContent）。
# 触发词：TOOL 调工具、REASON 出思维链、LONG 长排版、FAIL 上游报错
python3 tools/mock_openai.py --port 8799

# 端到端冒烟：配对、目录、建对话、流式、工具、失败、重新生成、置顶、配置写入
node tools/e2e_smoke.js

# 视觉回归：驱动真实交互并逐态截图
node tools/qa_shot.js --tag now --out .scratch/shots
node tools/qa_shot.js --tag now-dark --theme dark --out .scratch/shots

# 内嵌字体（正文、等宽、KaTeX）归 wanxiang2 的 libs/ThemeFonts，由那边自己生成
```

### 界面约定

- 视觉常量只从 `Design/Tokens.fs` 取，不在视图里写死颜色与尺寸
- 明暗两套配色在 `Design/Palette.fs`，结构相同、只换值
- 按钮语气由 `Ui.ButtonTone` 决定（Primary 一屏最多一个）
- 对话框、浮层、提示条统一画在 `OverlayHost` 上，桌面与浏览器行为一致

---

## 致谢

界面思路受 [Kelivo](https://github.com/kelivo) 启发；万象的数据模型、协议与实现均为独立设计。

内嵌字体为 [Sarasa Gothic](https://github.com/be5invis/Sarasa-Gothic)（OFL-1.1，见
wanxiang2 `libs/ThemeFonts/Assets/Fonts/LICENSE-Sarasa.txt`）的子集，
以及 [KaTeX](https://katex.org) 的数学字体（MIT，见
wanxiang2 `libs/ThemeFonts/Assets/Fonts/LICENSE-KaTeX.txt`）。

代码着色用 [highlight.js](https://highlightjs.org)（BSD-3-Clause，见
wanxiang2 `libs/RichScripts/js/LICENSE-highlight.js.txt`），公式排版用
[KaTeX](https://katex.org)；桌面端在进程内执行它们所依赖的
JS 解释器是 [Jint](https://github.com/sebastienros/jint)（BSD-2-Clause）。

模型访问经 [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)，
界面基于 [Avalonia](https://avaloniaui.net)。
