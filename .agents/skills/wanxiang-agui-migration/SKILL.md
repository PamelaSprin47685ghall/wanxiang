---
name: wanxiang-agui-migration
description: 万象 AG-UI 化改造的保姆级实施指导：分帧/账本内核代码、服务端与客户端改造步骤、验收标准、实测陷阱清单。做 AG-UI 改造或改动物化协议/落盘格式时读。
---

# 万象 AG-UI 化改造 · 实施指导

> **格式规范不在这里**：线上格式看 `wanxiang-protocol` 第 55 节；落盘格式看 `wanxiang-store` 第 47 节；
> 决策与 RFC 依据看 `wanxiang` 第 56 节。本 skill 只管**怎么做**。
> 用词遵循 RFC 2119/8174：**MUST** = 必须，**SHOULD** = 应当，**MAY** = 可以。

---

## 0. 开工前准备

### 0.1 记录基线

```bash
cd <产品仓根>
git status --porcelain          # 期望空
git rev-parse HEAD              # 记下，回滚要用
dotnet build src/Wanxiang.slnx -c Debug 2>&1 | tail -3
dotnet test tests/Wanxiang.Tests/Wanxiang.Tests.fsproj -c Debug 2>&1 | tail -3
```

**已知基线（2026-09-29 实测）**：build 成功；测试 **539 passed / 0 failed**（约 19s）。
**完成判据**：两个数字与上面一致，`git status` 干净。

### 0.2 备份数据目录 + 建分支

```bash
cp -a <你的数据目录> /tmp/wanxiang-backup-$(date +%F)
git checkout -b agui-migration
```

**为什么备份**：clean-break 后旧格式读不出来（见 §7）。

---

## 1. 新增依赖

在 `Wanxiang.Core`、`Wanxiang.Agent`、`Wanxiang.Server` 的 fsproj 里加（版本固定，不用通配）：

```xml
<!-- Core：JCS 规范化 -->
<PackageReference Include="Jcs.Net" Version="0.1.1" />
<!-- Agent：AG-UI 消息模型与 MEAI 双向转换 -->
<PackageReference Include="AGUI.Abstractions" Version="1.0.0" />
<!-- Server：AG-UI 服务端适配 -->
<PackageReference Include="AGUI.Server" Version="1.0.0" />
```

> `AGUI.Abstractions` 依赖 `Microsoft.Extensions.AI.Abstractions` 10.6.0，项目现有 10.9.0
> —— 实测兼容，**不要**降级现有版本。

**验证**（三个目标框架都要过）：

```bash
dotnet build src/Wanxiang.Core/Wanxiang.Core.fsproj -c Debug 2>&1 | tail -3
dotnet build src/Wanxiang.Server/Wanxiang.Server.fsproj -c Debug 2>&1 | tail -3
dotnet build src/Wanxiang.Pwa/Wanxiang.Pwa.fsproj -c Debug 2>&1 | tail -3
```

**已实测**：`AGUI.Abstractions` + `AGUI.Client` + `Jcs.Net` 在 `net10.0-browser` 下编译通过（2026-09-29）。

---

## 2. 账本内核（`src/Wanxiang.Core/Ledger/`）

> **为什么不单独建工程**：`CommitCodec.fs` 就在 `Wanxiang.Core`；独立工程会形成 `Core` ↔ 新工程
> 的**循环依赖**（新工程要用 `CommitId`）。这块内核只需 `Jcs.Net` + 标准库，放进 `Core` 最干净。

### 2.1 登记编译顺序

`mkdir -p src/Wanxiang.Core/Ledger`，在 `Wanxiang.Core.fsproj` 里按依赖顺序插入
（放在 `Types.fs` 之后、`CommitCodec.fs` 之前）：

```xml
<Compile Include="Ledger\Jcs.fs" />
<Compile Include="Ledger\Frame.fs" />
<Compile Include="Ledger\Record.fs" />
<Compile Include="Ledger\Recovery.fs" />
```

### 2.2 `Ledger/Jcs.fs` —— RFC 8785 规范化

```fsharp
namespace Wanxiang.Core.Ledger

open System
open System.Security.Cryptography
open Jcs.Net

/// RFC 8785 JSON 规范化（JCS）。全项目唯一的规范化入口。
module Jcs =
    /// 规范化 UTF-8 字节。输入必须是 JSON 对象或数组。
    let canonicalize (json: string) : byte[] =
        JsonCanonicalizer.CanonicalizeToUtf8 json

    /// 规范化后转字符串（调试/测试用）。
    let canonicalizeToString (json: string) : string =
        JsonCanonicalizer.Canonicalize json

    /// SHA-256(JCS(payload)) 的十六进制小写。
    let sha256Hex (json: string) : string =
        canonicalize json
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun s -> s.ToLowerInvariant()

    /// 容错版：非法输入返回 None（不抛）。
    let trySha256Hex (json: string) : string option =
        try Some(sha256Hex json) with _ -> None
```

### 2.3 `Ledger/Frame.fs` —— RFC 7464 分帧

```fsharp
namespace Wanxiang.Core.Ledger

open System
open System.Text

/// RFC 7464 JSON Text Sequence 分帧。
/// 记录 = RS(0x1E) + JSON 对象 + LF(0x0A)
module Frame =
    [<Literal>]
    let RS = 0x1Euy
    [<Literal>]
    let LF = 0x0Auy

    /// 把一条已序列化的 JSON 文本包成一个记录（含 RS 与 LF）。
    let encode (json: string) : byte[] =
        let body = Encoding.UTF8.GetBytes json
        let buf = Array.zeroCreate<byte> (body.Length + 2)
        buf.[0] <- RS
        Array.blit body 0 buf 1 body.Length
        buf.[body.Length + 1] <- LF
        buf

    /// 切成记录边界。依据 RFC 7464 §2.1：RS 唯一确定边界；连续 RS 之间为空段，忽略；
    /// 末尾无 RS 的残留内容即截断。
    /// 返回 (载荷, 是否以 LF 正常结尾)。
    let split (data: ReadOnlySpan<byte>) : (byte[] * bool) list =
        let result = ResizeArray<byte[] * bool>()
        let mutable i = 0
        while i < data.Length do
            if data.[i] = RS then
                let mutable j = i + 1
                while j < data.Length && data.[j] <> RS do j <- j + 1
                let len = j - i - 1
                if len > 0 then
                    let seg = data.Slice(i + 1, len).ToArray()
                    let endsWithLf = seg.Length > 0 && seg.[seg.Length - 1] = LF
                    let payload = if endsWithLf then seg.[0 .. seg.Length - 2] else seg
                    result.Add(payload, endsWithLf)
                i <- j
            else
                i <- i + 1
        List.ofSeq result
```

### 2.4 `Ledger/Record.fs` —— 记录结构

```fsharp
namespace Wanxiang.Core.Ledger

open System
open System.Text.Json
open System.Text.Json.Nodes

type LedgerRecord = {
    formatVersion: int
    commitId: uint64
    bootId: Guid
    source: string
    at: DateTimeOffset
    commandId: string option
    commandType: string option
    commandHash: string option
    events: JsonArray
}

module Record =
    let FormatVersion = 2

    /// 显式用 byref 重载，避免 F# 重载歧义（FS0041）。
    let private tryProp (o: JsonObject) (k: string) : JsonNode option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(k, &n) && not (isNull n) then Some n else None

    let private tryStr (o: JsonObject) (k: string) : string option =
        match tryProp o k with
        | Some v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
        | _ -> None

    /// formatVersion 是 JSON **数字**，按数字读（写读务必同型）。
    let private tryInt (o: JsonObject) (k: string) : int option =
        match tryProp o k with
        | Some v when v.GetValueKind() = JsonValueKind.Number ->
            match v with
            | :? JsonValue as jv ->
                match jv.TryGetValue<int>() with
                | true, i -> Some i
                | _ -> None
            | _ -> None
        | _ -> None

    let toJson (r: LedgerRecord) : string =
        let o = JsonObject()
        o["formatVersion"] <- FormatVersion
        // 大整数必须字符串承载（RFC 8785 附录 D）
        o["commitId"] <- r.commitId.ToString()
        o["bootId"] <- r.bootId.ToString("D")
        o["source"] <- r.source
        o["at"] <- r.at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
        r.commandId |> Option.iter (fun v -> o["commandId"] <- v)
        r.commandType |> Option.iter (fun v -> o["commandType"] <- v)
        r.commandHash |> Option.iter (fun v -> o["commandHash"] <- v)
        o["events"] <- r.events.DeepClone()
        o.ToJsonString()

    let tryFromJson (json: string) : Result<LedgerRecord, string> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as o ->
                let fv = tryInt o "formatVersion"
                let cid = tryStr o "commitId"
                let bid = tryStr o "bootId"
                let src = tryStr o "source"
                let at = tryStr o "at"
                let ev = tryProp o "events"
                match fv, cid, bid, src, at, ev with
                | Some f, Some c, Some b, Some s, Some t, Some e ->
                    if f <> FormatVersion then Error(sprintf "unsupported formatVersion %d" f)
                    elif e.GetValueKind() <> JsonValueKind.Array then Error "events is not an array"
                    else
                        match UInt64.TryParse(c: string), Guid.TryParse(b: string), DateTimeOffset.TryParse(t: string) with
                        | (true, cv), (true, bv), (true, tv) ->
                            Ok { formatVersion = FormatVersion
                                 commitId = cv
                                 bootId = bv
                                 source = s
                                 at = tv
                                 commandId = tryStr o "commandId"
                                 commandType = tryStr o "commandType"
                                 commandHash = tryStr o "commandHash"
                                 events = e.AsArray() }
                        | _ -> Error "malformed field (commitId/bootId/at)"
                | _ -> Error "missing required field"
            | _ -> Error "top-level value is not a JSON object"
        with e -> Error(sprintf "parse failed: %s" e.Message)
```

### 2.5 `Ledger/Recovery.fs` —— 恢复算法

```fsharp
namespace Wanxiang.Core.Ledger

open System
open System.Text

type RecoveryOutcome = {
    records: LedgerRecord list
    truncatedBytes: int
    skippedSegments: int
    warnings: string list
}

module Recovery =
    /// RFC 7464 §2.1（解析失败继续）/§2.3（跳过截断）/§2.4（顶层必须自定界）。
    let recover (data: byte[]) : RecoveryOutcome =
        let segs = Frame.split (ReadOnlySpan<byte> data)
        let warnings = ResizeArray<string>()
        let records = ResizeArray<LedgerRecord>()
        let mutable skipped = 0
        let mutable truncated = 0
        let mutable lastCommit = 0UL
        let mutable lastBoot = Guid.Empty
        let mutable firstInBoot = true
        let total = segs.Length
        segs |> List.iteri (fun idx (payload, endsWithLf) ->
            let json = Encoding.UTF8.GetString payload
            match Record.tryFromJson json with
            | Ok r ->
                if firstInBoot then firstInBoot <- false
                elif r.bootId = lastBoot then
                    if r.commitId <> lastCommit + 1UL then
                        warnings.Add(sprintf "commitId gap: %d -> %d" lastCommit r.commitId)
                else
                    if r.commitId <= lastCommit then
                        warnings.Add(sprintf "commitId regression across bootId: %d <= %d" r.commitId lastCommit)
                lastCommit <- r.commitId
                lastBoot <- r.bootId
                records.Add r
            | Error msg ->
                let isTail = (idx = total - 1)
                if isTail && not endsWithLf then
                    truncated <- payload.Length
                    warnings.Add(sprintf "truncated tail record dropped (%d bytes)" payload.Length)
                else
                    skipped <- skipped + 1
                    warnings.Add(sprintf "skipped malformed segment #%d: %s" idx msg))
        { records = List.ofSeq records
          truncatedBytes = truncated
          skippedSegments = skipped
          warnings = List.ofSeq warnings }
```

**验证**：`dotnet build src/Wanxiang.Core/Wanxiang.Core.fsproj -c Debug`

### 2.6 单测（**必做**）

新建 `tests/Wanxiang.Tests/EventLogTests.fs`（登记进 fsproj），至少覆盖：

1. `Frame` 单条往返（首字节 = RS，末字节 = LF）
2. 尾部截断被识别（第二段 `endsWithLf = false`）
3. `Recovery` 丢弃尾部截断但保留前面
4. `Recovery` 跳过中间损坏段并继续
5. `Jcs` 大整数护栏（字符串原样、裸数字被改写——把这个差异钉成测试）

**完成判据**：539 + 新增 = 全绿。

---

## 3. AG-UI 语义层（`src/Wanxiang.Agui/`）

### 3.1 建工程

`net10.0`，引用 `AGUI.Abstractions`、`AGUI.Server`、`Wanxiang.Core`、`Wanxiang.Agent`。
编译顺序：`Capabilities.fs` → `MessageMap.fs` → `Envelope.fs` → `TolerantReader.fs` → `Outbound.fs`。
登记进 `src/Wanxiang.slnx`。

### 3.2 `MessageMap.fs` —— MAF ⇄ AG-UI 消息

```fsharp
namespace Wanxiang.Agui

open Microsoft.Extensions.AI
open AGUI.Abstractions
open System.Text.Json
open System.Text.Json.Nodes

/// MAF ChatMessage ⇄ AG-UI 消息对象。实测往返无损。
module MessageMap =
    let private opts = AGUIJsonSerializerContext.Default.Options

    /// MAF → AG-UI 消息对象（JsonNode）。
    /// **MUST 按 typeof<AGUIMessage> 序列化**（2026-09-29 实测修正，方向与初稿相反）：
    /// 按运行期类型（如 AGUIUserMessage）会静默丢掉 content；按基类 AGUIMessage 反而保留。
    /// 官方 converter 挂在基类上，子类各自的序列化上下文没带它。
    let toAgui (msg: ChatMessage) : JsonNode =
        let list = AGUIChatMessageExtensions.AsAGUIMessages([msg], opts) |> List.ofSeq
        match list with
        | [m] -> JsonNode.Parse(JsonSerializer.Serialize(m, typeof<AGUIMessage>, opts))
        | _ -> failwithf "expected exactly one AG-UI message, got %d" list.Length

    /// AG-UI 消息对象 → MAF ChatMessage。
    let tryToMaf (node: JsonNode) : ChatMessage option =
        try
            let json = node.ToJsonString()
            let msg = JsonSerializer.Deserialize<AGUIMessage>(json, opts)
            AGUIChatMessageExtensions.AsChatMessages([msg]) |> Seq.tryHead
        with _ -> None
```

### 3.3 `TolerantReader.fs` —— 宽容读层（**最关键**）

> 官方 SDK 对未知 `type` 抛 `AGUIUnknownEventTypeException`，与规范矛盾 ⇒ 必须自建。

```fsharp
namespace Wanxiang.Agui

open System.Text.Json
open System.Text.Json.Nodes
open AGUI.Abstractions

/// 不认识的 type 不得终止；已知字段畸形必须致命。对官方 SDK 的合规补丁。
module TolerantReader =
    type Parsed =
        | Known of BaseEvent
        | Unrecognized of typeName: string
        | Malformed of reason: string
        | Custom of name: string * value: JsonNode

    let private knownTypes : Set<string> =
        Set.ofArray
            [| for f in typeof<AGUIEventTypes>.GetFields() do
                   match f.GetValue(null) with
                   | :? string as s -> yield s
                   | _ -> () |]

    /// 显式用 byref 重载，避免 F# 重载歧义（FS0041）。
    let private tryProp (o: JsonObject) (k: string) : JsonNode option =
        let mutable n: JsonNode = null
        if o.TryGetPropertyValue(k, &n) && not (isNull n) then Some n else None

    let parse (json: string) : Parsed =
        try
            match JsonNode.Parse json with
            | :? JsonObject as o ->
                match tryProp o "type" with
                | Some t when t.GetValueKind() = JsonValueKind.String ->
                    let name = t.GetValue<string>()
                    if name = "CUSTOM" then
                        match tryProp o "name" with
                        | Some n when n.GetValueKind() = JsonValueKind.String ->
                            let v =
                                match tryProp o "value" with
                                | Some vl -> vl.DeepClone()
                                | None -> JsonNode.Parse "null"
                            Custom(n.GetValue<string>(), v)
                        | _ -> Malformed "CUSTOM without name"
                    elif Set.contains name knownTypes then
                        try Known(JsonSerializer.Deserialize<BaseEvent>(json, AGUIJsonSerializerContext.Default.Options))
                        with e -> Malformed(sprintf "known type %s failed to deserialize: %s" name e.Message)
                    else
                        Unrecognized name
                | _ -> Malformed "missing or non-string 'type'"
            | _ -> Malformed "event is not a JSON object"
        with e -> Malformed(sprintf "invalid JSON: %s" e.Message)
```

### 3.4 单测

1. MAF → AG-UI → MAF 往返（文本 / 工具调用 / 工具结果 / 推理 / 图片），断言内容不丢
2. **未知 type 返回 `Unrecognized` 而非抛异常**（合规护栏）
3. `CUSTOM` 带未知 `name` 返回 `Custom`（合法流量）
4. 已知 type 但字段畸形 → `Malformed`
5. `MessageMap.toAgui` 产物**含 `content` 字段**（防静默丢 content 回归）

---

## 4. 改造服务端

**动文件**：`src/Wanxiang.Server/WsConnection.fs`

### 4.1 出站

`SendLoop`（约 236–261 行）原用 `WireCodec.encode ev`。改为：

1. 先实现 `Wanxiang.Agui.Outbound.toEvents : WireEvent -> BaseEvent list`，按
   `wanxiang-protocol` 第 55.4/55.5 节的映射表逐条实现；
2. **每条映射必须有对应单测**（映射表就是测试清单）；
3. `SendLoop` 改为 `Envelope.encode event`（按运行期类型序列化）后发送字节。

### 4.2 入站

`HandleEvent`（约 310–610 行）原用 `WireCodec.tryDecode`。改为：

```fsharp
match TolerantReader.parse text with
| TolerantReader.Malformed reason ->
    logInfo(sprintf "malformed inbound event: %s" reason)
    do! this.CloseWith(WebSocketCloseStatus.ProtocolError, "malformed event")
| TolerantReader.Unrecognized name ->
    logInfo(sprintf "unrecognized event type ignored: %s" name)   // 不断连
| TolerantReader.Custom(name, value) -> (* 按 wanxiang.dev/* 前缀分发 *)
| TolerantReader.Known ev -> (* 标准 AG-UI 事件处理 *)
```

### 4.3 握手与版本

`Types.fs` + `WsConnection.fs`：删除 `protocol.hello` / `protocol.upgrade-required` 与旧 `ProtocolVersion` 语义；
版本改由 `RunAgentInput.protocolVersion` / `RUN_STARTED.protocolVersion` 承载；
**保留**认证前拒绝业务事件的约束。

### 4.4 e2e 测试

`tests/Wanxiang.Tests/ServerE2eTests.fs`（768 行）按用例逐个改写为 AG-UI 版本：
连接/认证、导出、幂等重放、快照游标后写入、推送顺序、历史分页、附件往返、陈旧拒绝。

---

## 5. 改造客户端

- `src/Wanxiang.Client/WsClient.fs`：收发换成 `TolerantReader` + `Envelope`；
  `ClientState` 的游标逻辑与 catch-up 应用逻辑**保留**（语义不变，仅换承载）。
- `src/Wanxiang.UI/AppShell.fs`：`HandleEvent`（1039–1265 行）匹配对象从 `WireEvent`
  改为 AG-UI 事件 + `wanxiang.dev/*`。**不要重写**——每个分支只是「换匹配模式」。

---

## 6. 改造落盘

**动文件**：`src/Wanxiang.Core/CommitCodec.fs`、`CanonicalJson.fs`、`CommandId.fs`

1. `commitToJsonLine` → 构造 `LedgerRecord` 再 `Record.toJson` + `Frame.encode`
2. `tryCommitFromJsonLine` → `Frame.split` + `Record.tryFromJson`
3. 删除手写规范化，改调 `Jcs.sha256Hex`
4. `CommandId.fs` 顶部加注释：*规范化算法 = RFC 8785 JCS，输入不含信封字段*
5. 文件扩展名 `.ndjson` → `.jsonseq`；**同时改 `doctor` 与启动 replay 里的文件名硬编码**

> ⚠️ 3、4 会让所有既有 `commandId` 失效。既然已 clean-break，这是零额外代价的时机。

---

## 7. 旧数据

**已定：不迁移**（负责人 2026-09-29 拍板）。

- `doctor` **MUST** 识别旧 `.ndjson` 并**明确报错说明原因**，**MUST NOT** 静默忽略。

---

## 7.5 实施现状（2026-09-29 · 全部落地）

| 流 | 状态 | 证据 |
|---|---|---|
| A 账本内核 | ✅ 完成 | `src/Wanxiang.Core/Ledger/`，EventLogTests 18 个全绿 |
| B AG-UI 语义层 | ✅ 完成 | `src/Wanxiang.Agui/` + `Wanxiang.Agent/MessageMap.fs`，测试全绿 |
| C 服务端线协议 | ✅ 完成 | WsConnection 收发走 `WireAgui`（WireAguiTests 12 个全绿 + e2e 9 场景全绿） |
| D 客户端/UI | ✅ 完成 | WsClient 收发走 `WireAgui`；AppShell 语义分发面**未动**（语义层不换，承载层换——这正是方案 Y 的意义） |
| E 落盘切换 | ✅ 完成 | `.jsonseq` + RS 分帧 + bootId/source；CanonicalJson 已删，Jcs 上位 |
| F 收口 | ✅ 完成 | 587/587 全绿；并行测试 8/8 稳定 |

### 验收 7 条的实测状态

1. build 0 警告 0 错误 ✅
2. test 全绿（587/587）✅
3. **互操作（官方 AGUIChatClient 直连）**：⚠️ **未达**。官方客户端是 run 请求-响应模型（`IChatClient` + `IAGUITransport`），万象是 fire-and-forget 对称事件流（决策 25）——语义面不对齐。直连需要万象暴露 run 语义端点（首版范围外）；`IAGUITransport` 可自定义传输，写 WS 桥接是明确的后续项。
4. 超集（observe/catch-up/幂等/附件/配置）✅（e2e 9 场景）
5. 宽容性（未知 type 不断连）✅（WireAguiTests 3 个护栏 + 服务端 stderr 告警路径）
6. 截断恢复 ✅（EventLogTests：尾部截断/中间损坏/序号空洞/跨代回退）
7. 大整数 ✅（EventLogTests：字符串承载/裸数字被改写钉成回归）

### 标准面覆盖（2026-09-29 第二轮：能用 AG-UI 语义的都用）

| 万象语义 | AG-UI 标准事件 |
|---|---|
| 文本 delta | `TEXT_MESSAGE_CHUNK` |
| functionCall 内容 | `TOOL_CALL_START` |
| functionResult 内容 | `TOOL_CALL_RESULT` |
| cancelled 生成 | `RUN_FINISHED` + `RunFinishedCancelledOutcome` |
| completed 生成（含 usage） | `RUN_FINISHED` + `TokenUsage` |
| failed 生成 | `RUN_ERROR` |
| 生成开始 | `RUN_STARTED` |

**CUSTOM 面**（万象设计特色决策，保留语义但仿标准面风格）：
- value 是**扁平字段**，不套外壳
- 身份字段用 `threadId`/`runId`/`messageId`（AG-UI 命名）
- 消息/增量载荷叫 `content`（与 AG-UI 消息内容一致）

### 命名原生化（无迁移痕迹）

线上字段、错误串、测试断言全部统一 `threadId`/`runId`/`messageId`/`content`；
外壳判定按「有无 `type` 键」；注释描述设计而非改动史。

### 架构落点（与原方案的差异，实测驱动）

- **方案 Y（外壳适配）取代方案 X（全量重写）**：56 个 WireEvent 变体保留为语义层，
  `WireAgui` 在边界做承载映射。WsConnection/AppShell/WsClient 的语义分发**零改动**。
- **`Avalonia.Headless.XUnit` 不可用**（上游 #21467 死锁 / #22021 毒化），
  改用裸 xunit.v3 + `HeadlessUnitTestSession`（见 `tests/Wanxiang.Tests/Headless.fs`）。
- **E2 陷阱方向修正**：AGUI 消息按**基类** `typeof<AGUIMessage>` 序列化才保留 content，
  按运行期类型会丢（与初稿相反）。

## 8. 验收标准

1. `dotnet build src/Wanxiang.slnx -c Debug` → **0 警告 0 错误**
2. `dotnet test` → **全绿**
3. **互操作证明**：用官方 `AGUI.Client` 的 `AGUIChatClient` 作独立客户端，连上万象服务端跑通
   `initialize → session/new → session/prompt → 流式文本 → 工具调用展示 → RUN_FINISHED`
   （这是「真的用了 AG-UI」的唯一硬证据）
4. **超集证明**：官方客户端跑通标准面；我方客户端在标准面之外仍能 observe / catch-up /
   幂等重发 / 附件上传 / 配置写入
5. **宽容性证明**：注入未知 `type` 事件 → 客户端忽略并告警、**连接不断**
6. **截断恢复证明**：截断文件尾部 → 重启后丢弃尾部、前面全恢复，`doctor` 有报告
7. **大整数证明**：`commitId` 为字符串；JCS 往返后数值不变

## 9. 回滚

```bash
git checkout <§0.1 记下的分支或哈希>
cp -a /tmp/wanxiang-backup-YYYY-MM-DD <数据目录>
```

---

## 10. 实测陷阱清单（**全部实机踩过**）

| # | 坑 | 症状 | 对策 |
|---|---|---|---|
| 1 | AG-UI 消息序列化方向 | 按运行期类型（AGUIUserMessage 等）序列化会**静默丢 `content`** | **一律用 `typeof<AGUIMessage>`**（converter 挂在基类上）|
| 2 | 官方 SDK 未知事件抛异常 | 收到新事件就断连 | 用 `TolerantReader` 包一层 |
| 3 | JCS 改写大整数 | `18446744073709551615` → `...52000` | 大整数一律 JSON 字符串 |
| 4 | RFC 7464 裸数字截断 | `RS` + 裸数字被误判 | 记录顶层恒为对象 |
| 5 | `commandHash` 输入含信封字段 | 换格式后幂等键漂移、命令重复执行 | 哈希输入与信封解耦 |
| 6 | 把账本内核建成独立工程 | 循环依赖 | 放 `Core/Ledger/` |
| 7 | 忘记改 `doctor` 的文件名假设 | 读不到日志却报正常 | §6.5 一起改 |
| 8 | 把 `CUSTOM` 用在账本层 | 账本记录可被合法忽略 | 账本用自有记录结构 |
| 9 | **字段读出/写入类型不一致** | 写 `formatVersion` 为数字、读按字符串 ⇒ 所有记录被判「缺字段」、replay 全空 | 写读同型；数字字段用 `JsonValueKind.Number`（§2.4 的 `tryInt`）。**此坑在行为验证中真实踩到** |
| 10 | **F# 重载歧义** | `TryGetPropertyValue` / `UInt64.TryParse` 报 FS0041 | 显式 `let mutable n: JsonNode = null` + byref；`TryParse` 加类型标注 `(c: string)` |
| 11 | `object.GetType()` 的 F# 调用 | `AGUIEventTypes.GetType()` 报 FS3214 | 用 `typeof<AGUIEventTypes>.GetFields()` |

### 10.1 已验证行为（照着写不会错）

本文 §2、§3 的代码已在 `net10.0` 编译通过并跑过行为验证：

| 验证项 | 实测结果 |
|---|---|
| 三记录正常往返 | `records=3, warnings=0`，commitId `[1;2;3]` |
| 尾部截断 | `records=2, truncated=144`，前面全恢复 |
| 中间损坏段 | `records=2, skipped=1`，继续解析 |
| 序号空洞 | 告警 `commitId gap: 1 -> 5` |
| 帧边界 | `RS...LF` → 1 段，`endsWithLf=true` |
| 宽容读：未知 type | `Unrecognized`（**不抛异常**） |
| 宽容读：CUSTOM | `Custom(wanxiang.dev/cursor, ...)` |
| 宽容读：已知类型 | `Known(RunStartedEvent)` |
| 宽容读：畸形 | `Malformed` |
| JCS 稳定性 | `{"b":"2","a":"1"}` → `{"a":"1","b":"2"}` |
| JCS 大整数 | 裸数字被改写为 `18446744073709552000`；**字符串原样保留** |

> §4–§6 是「改哪个文件、改成什么形状」的描述，**未逐行验证**。
> §2、§3 的写法是修正后版本，**请照抄，不要自行改写**（改了会踩 §10 的坑）。
