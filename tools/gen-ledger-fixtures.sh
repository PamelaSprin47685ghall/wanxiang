#!/usr/bin/env bash
# T-001 黄金样本生成器（一次性）。
#
# 用法：tools/gen-ledger-fixtures.sh
# 输出：tests/Wanxiang.Tests/fixtures/Ledger/<case>/<file>.jsonseq + fixture.manifest
#
# 规则（算法 F）：
# - 用**当前生产编码器**（CommitCodec + Frame）生成一次样本；
# - 生成后立即提交，测试**不得**再调用当前 serializer 重建这些文件；
# - fixture.manifest 记录每个文件的 SHA-256，测试按 manifest 校验。
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT="$ROOT/tests/Wanxiang.Tests/fixtures/Ledger"
rm -rf "$OUT"
mkdir -p "$OUT/current" "$OUT/damaged-tail" "$OUT/malformed-record" "$OUT/id-gap" "$OUT/invalid-utf8"

# 用 dotnet fsi 跑生成脚本，复用生产 CommitCodec/Frame（引用 Wanxiang.Core）。
cat > /tmp/gen-ledger.fsx <<'EOF'
#r "/home/kunweiz/Desktop/vibe/wanxiang/src/Wanxiang.Core/bin/Debug/net10.0/Wanxiang.Core.dll"
open System
open System.IO
open System.Text
open Wanxiang.Core
open Wanxiang.Core.Ledger

let out = "/home/kunweiz/Desktop/vibe/wanxiang/tests/Wanxiang.Tests/fixtures/Ledger"
let cfg = { provider = "openai"; model = "test-model"; instructions = None; tools = []; temperature = None; topP = None; maxTokens = None; thinkingBudget = None; extraJson = None }
let convId = Guid.Parse "11111111-2222-3333-4444-555555555555"
let userMsg (text: string) : System.Text.Json.Nodes.JsonNode =
    let o = System.Text.Json.Nodes.JsonObject()
    o["role"] <- "user"
    let contents = System.Text.Json.Nodes.JsonArray()
    let tc = System.Text.Json.Nodes.JsonObject()
    tc["text"] <- text
    contents.Add tc
    o["contents"] <- contents
    o :> System.Text.Json.Nodes.JsonNode
let c1 = Events.Commit.create 1UL (DateTimeOffset(DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc))) [ ConversationCreated { conversationId = convId; title = "黄金样本"; config = cfg } ]
let c2 = Events.Commit.create 2UL (DateTimeOffset(DateTime(2026, 8, 1, 10, 1, 0, DateTimeKind.Utc))) [ AgentMessageRecorded { conversationId = convId; payloadJson = userMsg "你好，万象！" } ]
let c3 = Events.Commit.create 3UL (DateTimeOffset(DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc))) [ ConversationCreated { conversationId = Guid.Parse "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"; title = "次日会话"; config = cfg } ]

let frame (c: Events.Commit) = Frame.encode (CommitCodec.commitToJsonLine c)

// current：三条完整记录（跨 UTC 日期文件）
File.WriteAllBytes(Path.Combine(out, "current", "2026-08-01.jsonseq"), Array.concat [ frame c1; frame c2 ])
File.WriteAllBytes(Path.Combine(out, "current", "2026-08-02.jsonseq"), frame c3)

// damaged-tail：完整记录 + 尾部半条（模拟崩溃残余）
let tail = Encoding.UTF8.GetBytes("\u001E{\"formatVersion\":1,\"id\":\"4\",\"committedAtUtc\":\"2026-08-02T09:01:00Z\",\"events\":[{\"type\":\"agent-message-recorded\",\"version\":1,\"data\":{\"conversationId\":\"11111111-2222-3333-4444-555555555555\",\"payload\":{\"role\":\"user\",\"conten")
// 08-01 含 c1/c2（id 1,2），08-02 含 c3（id 3）+ 尾部半条（id 4 未完成）
File.WriteAllBytes(Path.Combine(out, "damaged-tail", "2026-08-01.jsonseq"), Array.concat [ frame c1; frame c2 ])
File.WriteAllBytes(Path.Combine(out, "damaged-tail", "2026-08-02.jsonseq"), Array.append (frame c3) tail)

// malformed-record：中间一条坏 JSON（RS + 非 JSON 文本 + LF）
let bad = Encoding.UTF8.GetBytes("\u001Ethis-is-not-json\u000A")
File.WriteAllBytes(Path.Combine(out, "malformed-record", "2026-08-02.jsonseq"), Array.concat [ frame c3; bad ])

// id-gap：id 1、3（缺 2）
let c3b = Events.Commit.create 3UL (DateTimeOffset(DateTime(2026, 8, 1, 11, 0, 0, DateTimeKind.Utc))) [ ConversationCreated { conversationId = Guid.Parse "bbbbbbbb-cccc-dddd-eeee-ffffffffffff"; title = "gap"; config = cfg } ]
File.WriteAllBytes(Path.Combine(out, "id-gap", "2026-08-01.jsonseq"), Array.concat [ frame c1; frame c3b ])

// invalid-utf8：合法记录 + 非法 UTF-8 字节序列
let badUtf8 = Array.concat [ Encoding.UTF8.GetBytes("\u001E{\"formatVersion\":1,\"id\":\"2\",\"committedAtUtc\":\"2026-08-01T12:00:00Z\",\"events\":[]}"); [| 0xFFuy; 0xFEuy |]; Encoding.UTF8.GetBytes "\u000A" ]
File.WriteAllBytes(Path.Combine(out, "invalid-utf8", "2026-08-01.jsonseq"), Array.concat [ frame c1; badUtf8 ])

printfn "fixtures written"
EOF
dotnet fsi /tmp/gen-ledger.fsx

# 生成 fixture.manifest（SHA-256）
cd "$OUT"
{
  echo "# T-001 黄金样本清单（算法 F 冻结，不得由当前实现重新生成）"
  echo "# 生成日期：2026-10-10；生成器：tools/gen-ledger-fixtures.sh"
  echo "# 每个文件 SHA-256 由测试按此清单校验。"
  echo ""
} > fixture.manifest
find . -type f -name "*.jsonseq" | sort | while read -r f; do
  sha256sum "$f" | sed 's|  ./|  |' >> fixture.manifest
done
echo "--- fixture.manifest ---"
cat fixture.manifest
