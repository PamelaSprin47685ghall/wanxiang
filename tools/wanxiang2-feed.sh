#!/usr/bin/env bash
# 产出 wanxiang2 交付包的本地源，旧仓库只经这些包使用 wanxiang2（restore 之前必须先跑）。
#   vendor/wanxiang2/x pack  -> vendor/wanxiang2/artifacts/feed/Wanxiang2.*.0.0.0-local.nupkg
# 同版本不同字节不能混用；只有锁定提交、完整包集、逐包摘要相符才复用。
# --check 只核验；--rebuild 强制重新产包。详情见 tools/wanxiang2_feed.py。
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
exec python3 "$root/tools/wanxiang2_feed.py" "$@"
