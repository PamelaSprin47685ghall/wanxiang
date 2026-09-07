# 万象

一个把「对话」当成账本来管的 AI 聊天客户端。

每个实例既是服务端也是客户端：服务端跑模型、存会话、管密钥；客户端只是它的一个视图。
两者之间只有一条 WebSocket，本机自己连自己也走同一条路径——没有第二套隐藏行为。

- **桌面端**（Linux x64）：完整节点，服务端 + 界面
- **PWA**：纯前端客户端，由服务端托管，通过配对码接入

界面在桌面与浏览器之间共用同一套 F# 代码，所以两端长得一样、行为一样。

---

## 快速开始

```bash
./start.sh
```

首次运行会构建整个解决方案，然后在 `127.0.0.1:8765` 起服务并打开桌面界面。
浏览器访问同一地址即可得到 PWA。

界面里第一件事是**添加一个服务商**：设置 → 服务商 → 添加服务商。
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

- 桌面界面需要图形环境；只跑服务端与 PWA 不需要：

  ```bash
  wanxiang --server --pwa --client=false
  ```

---

## 运行模式

三个开关自由组合（命令行 > TOML > 程序默认）：

| 组合 | 效果 |
|---|---|
| `--server --client --pwa` | 默认：本机全功能节点 |
| `--server --pwa --client=false` | 无界面服务端，只服务浏览器与远程客户端 |
| `--client --server=false` | 纯客户端，连别人的服务端 |
| 三个全关 | 只跑 `doctor` 自检并退出 |

自检与修复：

```bash
wanxiang --server=false --client=false --pwa=false        # 只读检查
wanxiang --server=false --client=false --pwa=false --fix   # 检查并修复日志尾部
```

`doctor` 会逐项报告配置、数据锁、日志重放、监听端口与附件可达性。

---

## 连接与配对

服务端不接受匿名连接。首次接入走配对码：

1. 客户端点「连接」，选择「改用配对码」
2. 服务端终端（stderr）打印一个 6 位码，5 分钟内有效
3. 输入后客户端拿到长期令牌，之后自动重连

令牌即身份。桌面端存 `client.toml`，PWA 存 IndexedDB（按服务端 `instanceId` 索引，
URL 不是身份）。要吊销某个客户端，在配置里把对应 `[[auth.clients]]` 的 `revoked` 置为 `true`
——服务端会立刻断开它。

配对失败有限流：同一远端地址每分钟 5 次失败后冻结 5 分钟。

### TLS

要对外提供服务，给出 PEM 证书链与私钥即可，服务端自己起 HTTPS/WSS：

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
界面里改服务商、MCP 与生成默认值都会原子重写这个文件。

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
thinkingBudget = 0            # 思维链预算（token）；0 = 不开，可按会话覆盖

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
maxRetries = 2
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
配置非法时服务端继续用上一份有效配置运行，并把原因写到 stderr。

### 关于服务商

一个服务商是「一组凭据 + 一个端点 + 一批模型」。换模型不必改配置：
输入框左下角就能切，切换只影响当前会话。

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
重试只发生在收到响应头之前，一旦开始吐字就绝不重发，
不会让你看到同一段回答出现两次。密钥走请求头而不是 URL 查询参数。

---

## 数据

```
<data>/
  events/YYYY-MM-DD.ndjson      事件日志：一行 = 一次原子提交
  attachments/ab/cd/<sha256>    附件，内容寻址（按哈希前四位分片）
  attachments/.tmp/             在途上传
  lock                          单进程锁
```

事件日志是唯一权威，界面看到的一切都是它折叠出来的投影。
重启只靠 TOML + NDJSON + 附件目录就能恢复全部永久状态。

同一数据目录同时只允许一个服务端进程。日志尾部若因断电截断，
启动时自动截到最后一条完整提交并继续写入，不会静默丢中间数据。

删除会话后，只被它引用的附件会在后台被回收。回收刻意保守：
只要还有任何存活会话（含其分叉的祖先链，以及已删除的历史消息）
可能展示某个附件，它就留着——多占一点磁盘远好过把历史变成「内容已丢失」。

---

## 会话里能做什么

- **切模型**：输入框左下角，按服务商分组
- **改参数**：顶栏滑块图标，逐会话调 temperature / 最大输出 / 系统指令 / 工具
- **重新生成**：把上一轮模型回复作废后重跑
- **编辑并分叉**：改任意一条自己的消息，从那里长出一个新会话，原会话不动
- **置顶 / 归档 / 导出**：会话右键
- **附件**：文本文件内联为代码块；二进制按传输能力直接进模型，进不去的会写明原因

| 传输 | 图片 | PDF | 音频 |
|---|---|---|---|
| `openai` | 是 | — | — |
| `anthropic` | 是 | 是 | — |
| `gemini` | 是 | 是 | 是 |

  空格不是「悄悄丢掉」：传输吃不下的类型会附一句说明，
  不会让你以为模型看过那份文件。单个二进制附件的内联上限是 16 MiB。

每条消息都带时间；消息下方的操作条常驻淡显，鼠标悬停或键盘聚焦时变实，
触屏上同样可点。

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

会话菜单中的「导出」会读取导出开始时的完整已提交历史，不受聊天窗口默认
200 条快照和已加载页数影响；不必先打开会话或滚动到顶部。读取期间显示进度，
全部取齐后再点「保存 Markdown」。取消、断线、超时或服务端拒绝时，不会把
半份历史当成完整文件保存；重新导出会从新的时点开始。

导出遵循原会话的分叉与删除语义，保留消息时间、工具调用与结果，以及附件说明。
Markdown 不打包附件文件、不包含尚未提交的生成与排队内容，**不能代替完整数据备份**。
当前内存导出上限为单条消息 JSON 16 MiB、累计消息 JSON 和生成的 Markdown 各 64 MiB；
超过上限会明确失败，不会静默截断。客户端与服务端都需更新到支持完整导出的版本。

### 发送与草稿

发送后，内容会先保留在输入区上方，区分「发送中」「已排队，尚未保存」和「未确认」。
只有服务端确认提交后，这份待发送内容才会移除；断线或被拒绝时可以原样重试，不会重复保存。
生成过程中可用「排队发送」或发送快捷键补充消息，停止按钮仍单独保留。
切换会话会保留各自的文字和附件草稿，断线时仍能继续输入。

这些草稿与待发送记录只在当前客户端内存中保留，**关闭窗口或刷新网页后不会恢复**。
已排队但尚未插入的消息仍不是永久历史，只有提交确认才代表已保存。

### 快捷键

| 按键 | 作用 |
|---|---|
| `Ctrl` `N` | 新建会话 |
| `Ctrl` `K` | 搜索会话 |
| `Ctrl` `,` | 打开设置 |
| `Ctrl` `/` | 快捷键列表 |
| `Enter` | 发送（可在设置里改为 `Ctrl` `Enter`） |
| `Shift` `Enter` | 换行 |
| `Esc` | 关闭弹层 / 取消编辑 |

---

## 开发

```bash
dotnet build src/Wanxiang.slnx -c Debug
dotnet test tests/Wanxiang.Tests/Wanxiang.Tests.fsproj -c Debug
```

### 项目结构

| 项目 | 职责 |
|---|---|
| `Wanxiang.Core` | 领域类型、事件、投影、命令规划、幂等 |
| `Wanxiang.Store` | NDJSON 单写者、重放、数据锁 |
| `Wanxiang.Protocol` | WebSocket 事件编解码 |
| `Wanxiang.Config` | TOML 解析与原子重写 |
| `Wanxiang.Agent` | Provider 调用、消息映射、附件转内容 |
| `Wanxiang.Server` | 编排、工具与 MCP、附件、连接 |
| `Wanxiang.Client` | 协议客户端与客户端投影 |
| `Wanxiang.UI` | 共用界面（设计系统 / 组件 / 视图） |
| `Wanxiang.App` | 桌面入口 |
| `Wanxiang.Pwa` | 浏览器入口（browser-wasm） |

### 开发期工具

```bash
# 无需真实密钥的 mock，同时讲三种协议
# （/v1/chat/completions、/v1/messages、:streamGenerateContent）。
# 触发词：TOOL 调工具、REASON 出思维链、LONG 长排版、FAIL 上游报错
python3 tools/mock_openai.py --port 8799

# 端到端冒烟：配对、目录、建会话、流式、工具、失败、重新生成、置顶、配置写入
node tools/e2e_smoke.js

# 视觉回归：驱动真实交互并逐态截图
node tools/qa_shot.js --tag now --out .scratch/shots
node tools/qa_shot.js --tag now-dark --theme dark --out .scratch/shots

# 重建内嵌字体（正文比例字 + 代码等宽字，均已子集化）
python3 tools/build_fonts.py

# 重建内嵌的 highlight.js / KaTeX 与 KaTeX 字体
node tools/build_webassets.js
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
`src/Wanxiang.UI/Assets/Fonts/LICENSE-Sarasa.txt`）的子集，
以及 [KaTeX](https://katex.org) 的数学字体（MIT，见
`src/Wanxiang.UI/Assets/Fonts/LICENSE-KaTeX.txt`）。

代码着色用 [highlight.js](https://highlightjs.org)（BSD-3-Clause，见
`src/Wanxiang.UI/Assets/Js/LICENSE-highlight.js.txt`），公式排版用
[KaTeX](https://katex.org)；桌面端在进程内执行它们所依赖的
JS 解释器是 [Jint](https://github.com/sebastienros/jint)（BSD-2-Clause）。

模型访问经 [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)，
界面基于 [Avalonia](https://avaloniaui.net)。
