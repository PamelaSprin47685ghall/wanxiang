# 耐久等级（第四阶段）

> 回答「一次提交成功后，数据在多大程度上不会丢失」。
> 本文件是耐久语义的唯一权威；实现不得让耐久语义保持隐含。

## 两个概念

| 概念 | 定义 | 保证 |
|---|---|---|
| **Committed** | 提交已写入日志文件并完成**用户态 `Flush()`**，进程内可见 | 进程正常退出、进程崩溃（OS 未崩溃）后，数据仍在文件系统页缓存中，重启可恢复 |
| **DurablyCommitted** | 提交已写入日志文件并完成**内核级 `fsync`/`Flush(flushToDisk: true)`** | 操作系统掉电/崩溃后，数据仍在磁盘上 |

## 万象当前语义（2026-10-10 定案）

- 万象采用 **Committed**（`append + Flush()`），**不**执行 `fsync`。
- 依据：决策 4（2026-08-01 用户拍板）——「flush 即可，假设我有 UPS」。
- 因此：**本产品不承诺掉电后一定保留最近写入的提交**。操作系统掉电时，
  最近一次 `Flush()` 之后的数据可能仍在页缓存中而丢失。
- 若未来需要掉电耐久，必须实现明确的 durable flush（`FileStream.Flush(true)` 或
  `fsync`），并同步更新本文件与 `JsonSeqWriter`。

## 实现位置

| 层 | 文件 | 语义 |
|---|---|---|
| 写入器 | `Wanxiang.Store/JsonSeqWriter.fs` → `AppendCommit`：`stream.Write` + `stream.Flush()` | Committed |
| 提交协调器 | `Wanxiang.Store/CommitCoordinator.fs`：append+flush 成功后应用投影、发布 committed | 对外承诺 = Committed |
| 运行时截尾 | `JsonSeqWriter.TruncateTo`：`SetLength` + `Flush()` | 截尾同样不 fsync（掉电可能残留半记录，重启按最长有效前缀处理） |

## 恢复语义与耐久的关系

- 启动 `Replay.inspect`（只读）按 RFC 7464 分帧 + id 连续性，恢复**最长有效前缀**。
- 掉电导致的半记录/未 flush 记录：视为尾部截断，`RepairRequired`，由 `doctor --fix` 显式修复。
- **不承诺**：掉电后最近提交一定在；**承诺**：已恢复的前缀与历史完全一致，且修复前有证据包。

## 决策记录

- 2026-08-01：用户拍板 `flush 即可，假设我有 UPS`（决策 4）。
- 2026-10-10：本文件把该语义写成显式契约；`Committed`/`DurablyCommitted` 两词入库。
