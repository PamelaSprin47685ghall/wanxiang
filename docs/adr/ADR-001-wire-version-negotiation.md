# ADR-001：Wire 版本协商收敛（第八阶段）

- 状态：**已定案（方案 B）**，2026-10-10
- 关联：`wanxiang-protocol` 第 32 节（决策 69/70 现行决议）、`docs/contracts/version-authority.md`

## 问题

协议类型保留 `Hello` 与 `UpgradeRequired`，但：

- **客户端**仍发送 `Hello {protocol, version, instanceId}`（`AppShell.fs` 连接路径）；
- **服务端** `HandleEvent` 直接忽略 `Hello` 与 `UpgradeRequired`（`WsConnection.fs`：
  `| Hello _ | UpgradeRequired _ -> ()`），并注明版本协商已转 in-band；
- **服务端**从不发送 `UpgradeRequired`（无任何发送路径）；
- **客户端** UI 仍处理 `UpgradeRequired`（toast 提示）；
- 握手状态机 `HandshakeState` 含 `HelloSeen`，但服务端从不置它（Hello 被忽略），
  `NotStarted | HelloSeen` 实际等价于 `NotStarted`。

这是手册禁止的中间态：**客户端发，服务端忽略**。必须收敛。

## 方案

### 方案 A：Hello 是正式握手

- 服务端必须校验 `Hello.version == Constants.ProtocolVersion`，不等则发 `UpgradeRequired` 并关闭；
- `UpgradeRequired` 有真实发送路径；
- 代价：与 AG-UI in-band 版本字段（`RUN_STARTED.protocolVersion`）双轨并存，
  且 `Constants.ProtocolVersion`（整数 1）与 AG-UI `"1.0"`（字符串）是两套版本。

### 方案 B：版本完全 in-band（采纳）

- **删除生产路径**：客户端不再发送 `Hello`；服务端不再把 `Hello` 当作任何状态前提；
  `UpgradeRequired` 无生产发送路径，客户端 UI 删除其处理；
- **保留兼容 reader**：`WireEvent.Hello` / `UpgradeRequired` 变体与 `WireCodec` 编解码
  保留，仅用于读取旧客户端/旧服务端事件（AG-UI 宽容性要求：未知事件忽略，已知事件不致命）；
- 版本协商唯一权威：AG-UI `RunAgentInput.protocolVersion` / `RUN_STARTED.protocolVersion`
  与 `Capabilities.ProtocolVersion = "1.0"`；
- `HandshakeState` 收敛为 `NotStarted | Authenticated`（`HelloSeen` 删除）；
- `Constants.ProtocolVersion`（整数 1）降级为**诊断展示**用途，不再参与任何线上校验
  （`version-authority.md` 已记录）。

## 理由

- 与 `wanxiang-protocol` 第 32 节现行决议一致（决策 69/70 修订：删除 `protocol.hello` /
  `protocol.upgrade-required`，改 in-band）；
- AG-UI 规范对未知事件要求宽容（MUST NOT abort），保留 reader 即满足；
- 消除「客户端发、服务端忽略」的隐藏行为，避免未来有人误以为 Hello 有校验语义。

## 兼容性

- 旧客户端（发送 Hello）：服务端忽略之，认证照常 → **兼容**（不破坏）。
- 旧服务端（发送 UpgradeRequired）：新客户端忽略 → 兼容。
- 新客户端（不发送 Hello）：旧服务端若要求 Hello 会拒绝 → **不兼容**，但旧服务端
  已随本 ADR 一并升级；N-1 组合由 CI 兼容测试覆盖（当前无旧服务端部署）。

## 影响面

- `src/Wanxiang.Server/WsConnection.fs`：删除 `HelloSeen`，状态机收敛；
  `Hello`/`UpgradeRequired` 分支保留（兼容 reader，注释说明）。
- `src/Wanxiang.UI/AppShell.fs`：删除连接时发送 `Hello`；删除 `UpgradeRequired` UI 处理。
- `src/Wanxiang.Protocol/WireEvent.fs`、`WireCodec.fs`：变体与编解码保留（兼容 reader），
  加注释。
- `tests/Wanxiang.Tests/ServerConnectionsTests.fs`：`HandshakeState` 结构断言更新。
