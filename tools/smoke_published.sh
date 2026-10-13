#!/usr/bin/env bash
# 决策 411：启动「发布目录」里的主机（--pwa，无桌面 UI、显式空闲端口、临时配置/数据目录），
# 跑 tools/smoke_published_pwa.js，结束时一定杀掉主机。
# 用法：tools/smoke_published.sh <publish 目录> [port]
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
PUB=$(cd "$1" && pwd)
PORT="${2:-$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1])')}"
WORK=$(mktemp -d /tmp/wanxiang-smoke-XXXX)
mkdir -p "$WORK/data"
cat > "$WORK/config.toml" <<TOML
configVersion = 2
instanceId = "$(python3 -c 'import uuid;print(uuid.uuid4())')"
[runtime]
server = true
client = false
pwa = true
fix = false
[network]
listen = "127.0.0.1:$PORT"
TOML
BIN="$PUB/wanxiang"; [ -x "$BIN" ] || BIN="dotnet $PUB/wanxiang.dll"
WANXIANG_NO_URL_SCHEME=1 $BIN --config "$WORK/config.toml" --data "$WORK/data" --client=false --pwa > "$WORK/host.log" 2>&1 &
HOST=$!
trap 'kill $HOST 2>/dev/null || true; wait $HOST 2>/dev/null || true' EXIT
for _ in $(seq 1 120); do
    curl -fsS "http://127.0.0.1:$PORT/" -o /dev/null 2>/dev/null && break
    kill -0 $HOST 2>/dev/null || { echo "host exited early:"; cat "$WORK/host.log"; exit 1; }
    sleep 0.5
done
curl -fsS -o /dev/null "http://127.0.0.1:$PORT/_framework/avalonia.js" || { echo "FAIL avalonia.js not served"; cat "$WORK/host.log"; exit 1; }
echo "host up on $PORT (pid $HOST)"
rc=0
node "$ROOT/tools/smoke_published_pwa.js" --base "http://127.0.0.1:$PORT" --out "${SMOKE_OUT:-$WORK/shots}" || rc=$?
[ $rc -eq 0 ] || { echo "--- host.log (tail)"; tail -40 "$WORK/host.log"; }
exit $rc
