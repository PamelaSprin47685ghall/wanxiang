#!/usr/bin/env bash
set -e

ROOT=$(cd "$(dirname "$0")" && pwd)
WANXIANG_HOME="${WANXIANG_HOME:-$ROOT/.scratch/pwa-run}"
export WANXIANG_HOME
CONFIG="${1:-$WANXIANG_HOME/config.toml}"
DATA_DIR="${2:-$(dirname "$CONFIG")/data}"
WANXIANG_BIN="${ROOT}/src/Wanxiang.App/bin/Debug/net10.0/wanxiang"

export DISPLAY="${DISPLAY:-:0.0}"

if [ ! -x "$WANXIANG_BIN" ]; then
  echo "error: wanxiang binary not found at $WANXIANG_BIN" >&2
  echo "run: dotnet build src/Wanxiang.slnx" >&2
  exit 1
fi

echo "starting wanxiang (server + client + pwa)" >&2
echo "  config:  $CONFIG" >&2
echo "  data:    $DATA_DIR" >&2
echo "  binary:  $WANXIANG_BIN" >&2
echo "  display: $DISPLAY" >&2

exec "$WANXIANG_BIN" \
  --config "$CONFIG" \
  --data "$DATA_DIR" \
  --server --client --pwa
