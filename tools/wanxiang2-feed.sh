#!/usr/bin/env bash
# 产出 wanxiang2 交付包的本地源，旧仓库只经这些包使用 wanxiang2（restore 之前必须先跑）。
#   vendor/wanxiang2/x pack  -> vendor/wanxiang2/artifacts/feed/Wanxiang2.*.0.0.0-local.nupkg
# 版本号固定为 0.0.0-local，内容会变：每次都清掉本仓库独立包缓存（.nuget/packages）里的
# wanxiang2.*，避免 restore 拿到旧包。
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
[[ -x "$root/vendor/wanxiang2/x" ]] || git -C "$root" submodule update --init vendor/wanxiang2
"$root/vendor/wanxiang2/x" pack
rm -rf "$root"/.nuget/packages/wanxiang2.*
find "$root/src" "$root/tests" -path '*/obj/*' -name 'project.assets.json' -delete
echo "feed ready: $(ls "$root/vendor/wanxiang2/artifacts/feed" | tr '\n' ' ')"
