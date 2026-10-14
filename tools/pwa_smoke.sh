#!/usr/bin/env bash
# PWA 真浏览器端到端冒烟：起 mock 模型（18800）与托管 PWA 的主机（18799）→ 写入 mock 提供方
# → 无头 Chrome 跑 tools/pwa_smoke.cjs（配对、代码块与公式渲染、草稿刷新后仍在）。截图在 .scratch/pwa-smoke/。
# 依赖：Release 构建（含 Wanxiang.Pwa AppBundle）、/usr/bin/google-chrome、playwright-core（NODE_PATH 可指定）。
set -euo pipefail
R=$(cd "$(dirname "$0")/.." && pwd)
H=$(mktemp -d)
LOG=$H/host.log
cleanup() { [ -n "${HOST:-}" ] && kill "$HOST" 2>/dev/null; [ -n "${MOCK:-}" ] && kill "$MOCK" 2>/dev/null; rm -rf "$H"; }
trap cleanup EXIT
printf '[network]\nlisten = "127.0.0.1:18799"\n' > "$H/config.toml"
python3 "$R/tools/mock_openai.py" --port 18800 > "$H/mock.log" 2>&1 &
MOCK=$!
dotnet "$R/src/Wanxiang.App/bin/Release/net10.0/wanxiang.dll" --server=true --client=false --pwa=true --config "$H/config.toml" > "$LOG" 2>&1 &
HOST=$!
for _ in $(seq 1 60); do curl -sf -o /dev/null http://127.0.0.1:18799/ && break; sleep 1; done
(cd "$R" && node --experimental-websocket tools/qa_provision.js --url ws://127.0.0.1:18799/ws --log "$LOG" --mock http://127.0.0.1:18800/v1)
NODE_PATH=${NODE_PATH:-/node_modules} node "$R/tools/pwa_smoke.cjs" "$LOG"
