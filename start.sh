#!/usr/bin/env bash
# 启动万象（服务端 + 桌面界面 + PWA）。
#
#   ./start.sh                       # 默认配置与数据目录
#   ./start.sh <config.toml> <data>  # 指定位置
#
# 环境变量：
#   WANXIANG_SKIP_BUILD=1   跳过构建，只运行仍匹配当前包源的已有产物
#   WANXIANG_ARGS="..."     追加参数，例如 --client=false
set -euo pipefail

ROOT=$(cd "$(dirname "$0")" && pwd)

# 系统 SDK 可能缺 ASP.NET Core 运行时或 wasm-tools；存在用户级 SDK 时优先用它。
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

# 默认目录由实际宿主的 LocalState 决定。显式相对路径先按调用目录展开，
# 然后从工程根启动；脚本不复制 Git 拓扑算法，也不提前创建运行数据。
LAUNCH_ARGS=()
if [ -n "${1:-}" ]; then
  LAUNCH_ARGS+=(--config "$(python3 -c 'import os,sys; print(os.path.abspath(sys.argv[1]))' "$1")")
fi
if [ -n "${2:-}" ]; then
  LAUNCH_ARGS+=(--data "$(python3 -c 'import os,sys; print(os.path.abspath(sys.argv[1]))' "$2")")
fi
if [ -n "${WANXIANG_HOME:-}" ]; then
  export WANXIANG_HOME="$(python3 -c 'import os,sys; print(os.path.abspath(sys.argv[1]))' "$WANXIANG_HOME")"
fi
export DISPLAY="${DISPLAY:-:0.0}"

BIN="$ROOT/src/Wanxiang.App/bin/Debug/net10.0/wanxiang"
BUILD_RECEIPT="$(dirname "$BIN")/wanxiang2-feed.sha256"

"$ROOT/tools/wanxiang2-feed.sh"
FEED=$("$ROOT/tools/wanxiang2-feed.sh" --fingerprint)

if [ "${WANXIANG_SKIP_BUILD:-0}" != "1" ]; then
  rm -f "$BUILD_RECEIPT"
  echo "building…" >&2
  if ! dotnet build "$ROOT/src/Wanxiang.slnx" -c Debug --verbosity minimal; then
    echo >&2
    echo "构建失败。常见原因：" >&2
    echo "  · 缺 wasm-tools workload   → dotnet workload install wasm-tools" >&2
    echo "  · 缺 ASP.NET Core 运行时   → curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0" >&2
    exit 1
  fi
  AFTER_BUILD=$("$ROOT/tools/wanxiang2-feed.sh" --fingerprint)
  if [ "$FEED" != "$AFTER_BUILD" ]; then
    echo "包源在构建期间变化，请重新运行。" >&2
    exit 1
  fi
  printf '%s\n' "$FEED" > "$BUILD_RECEIPT"
elif [ "$(cat "$BUILD_RECEIPT" 2>/dev/null)" != "$FEED" ]; then
  echo "现有应用没有当前包源的构建回执，请不设置 WANXIANG_SKIP_BUILD 重新运行。" >&2
  exit 1
fi

if [ ! -x "$BIN" ]; then
  echo "未找到可执行文件：$BIN" >&2
  echo "请先不带 WANXIANG_SKIP_BUILD 运行一次。" >&2
  exit 1
fi

echo "启动万象" >&2
echo "  默认状态：主工作树/.Herdchestra（显式目录由宿主校验）" >&2

cd "$ROOT"
# shellcheck disable=SC2086
exec "$BIN" "${LAUNCH_ARGS[@]}" --server --client --pwa ${WANXIANG_ARGS:-}
