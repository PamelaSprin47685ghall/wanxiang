#!/usr/bin/env bash
# 启动万象（服务端 + 桌面界面 + PWA）。
#
#   ./start.sh                       # 默认配置与数据目录
#   ./start.sh <config.toml> <data>  # 指定位置
#
# 环境变量：
#   WANXIANG_SKIP_BUILD=1   跳过构建，直接跑现有产物
#   WANXIANG_ARGS="..."     追加参数，例如 --client=false
set -euo pipefail

ROOT=$(cd "$(dirname "$0")" && pwd)

# 系统 SDK 可能缺 ASP.NET Core 运行时或 wasm-tools；存在用户级 SDK 时优先用它。
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

DEFAULT_HOME="${XDG_CONFIG_HOME:-$HOME/.config}/wanxiang"
CONFIG="${1:-$DEFAULT_HOME/config.toml}"
DATA="${2:-${XDG_DATA_HOME:-$HOME/.local/share}/wanxiang/data}"

# WANXIANG_HOME 决定本机偏好（ui.json）与客户端令牌（client.toml）的位置，
# 让它跟随配置文件所在目录，多套配置之间不会互相串。
export WANXIANG_HOME="${WANXIANG_HOME:-$(dirname "$CONFIG")}"
export DISPLAY="${DISPLAY:-:0.0}"

BIN="$ROOT/src/Wanxiang.App/bin/Debug/net10.0/wanxiang"

if [ "${WANXIANG_SKIP_BUILD:-0}" != "1" ]; then
  echo "building…" >&2
  if ! dotnet build "$ROOT/src/Wanxiang.slnx" -c Debug --verbosity minimal; then
    echo >&2
    echo "构建失败。常见原因：" >&2
    echo "  · 缺 wasm-tools workload   → dotnet workload install wasm-tools" >&2
    echo "  · 缺 ASP.NET Core 运行时   → curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0" >&2
    exit 1
  fi
fi

if [ ! -x "$BIN" ]; then
  echo "未找到可执行文件：$BIN" >&2
  echo "请先不带 WANXIANG_SKIP_BUILD 运行一次。" >&2
  exit 1
fi

mkdir -p "$(dirname "$CONFIG")" "$DATA"

echo "启动万象" >&2
echo "  配置：$CONFIG" >&2
echo "  数据：$DATA" >&2
echo "  偏好：$WANXIANG_HOME" >&2

# shellcheck disable=SC2086
exec "$BIN" --config "$CONFIG" --data "$DATA" --server --client --pwa ${WANXIANG_ARGS:-}
