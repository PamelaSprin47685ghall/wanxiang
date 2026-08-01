---
description: 万象（wanxiang）项目 SSOT：任务前必读总纲 skill，按主题读对应 skill
alwaysApply: true
---

本仓库是「万象（wanxiang）」项目。项目的单一事实来源（SSOT）已拆分为一组 skill（原 AGENTS.md 重组，内容零丢失）：任何任务开始前，必须先读取 `skill://wanxiang`（总纲：硬约束、全局术语、平台范围、命名与 clean-room 边界、交付问答、首版完成定义）；再按任务主题读取对应 skill：

- 存储/NDJSON/幂等/投影 → `skill://wanxiang-store`
- 传输协议/observe/游标/写权限 → `skill://wanxiang-protocol`
- 会话/消息/生成/fork → `skill://wanxiang-chat`
- Tool/MCP → `skill://wanxiang-tools`
- 附件 → `skill://wanxiang-attachments`
- 配置/TOML → `skill://wanxiang-config`
- 认证/配对/令牌 → `skill://wanxiang-auth`
- 运行模式/doctor/进程 → `skill://wanxiang-runtime`
- 设计推导 → `skill://wanxiang-principles`
- 第三方框架 → `skill://agent-framework`、`skill://avalonia`、`skill://kelivo`

与本规则冲突时，以这些 skill 中保留的决策记录为准（唯一事实来源）。
