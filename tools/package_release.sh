#!/usr/bin/env bash
# 决策 413：把一个 RID 的自包含发布目录打成发行包（linux/osx：tar.gz；win：zip），
# 附 wanxiang:// 注册模板（packaging/）与简短 README。只打包，不发布。
# 用法：tools/package_release.sh <rid> <publish 目录> <输出目录> <版本，如 v0.1.0>
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
RID="$1"; PUB=$(cd "$2" && pwd); OUT="$3"; VER="$4"
NAME="wanxiang-$VER-$RID"
STAGE=$(mktemp -d /tmp/wanxiang-pkg-XXXX)
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/$NAME" "$OUT"
cp -a "$PUB/." "$STAGE/$NAME/"
mkdir -p "$STAGE/$NAME/packaging"
cp "$ROOT/packaging/README.md" "$STAGE/$NAME/packaging/"
case "$RID" in
  linux-*) cp "$ROOT/packaging/linux/"* "$STAGE/$NAME/packaging/" ;;
  win-*)   cp "$ROOT/packaging/windows/"* "$STAGE/$NAME/packaging/" ;;
  osx-*)   cp "$ROOT/packaging/macos/"* "$STAGE/$NAME/packaging/" ;;
  *) echo "unknown rid $RID" >&2; exit 2 ;;
esac
case "$RID" in win-*) EXE='wanxiang.exe' ;; *) EXE='./wanxiang' ;; esac
sed -e "s|@VERSION@|$VER|g" -e "s|@RID@|$RID|g" -e "s|@EXE@|$EXE|g" "$ROOT/packaging/RELEASE-README.md" > "$STAGE/$NAME/README.md"
[ -f "$STAGE/$NAME/pwa/_framework/avalonia.js" ] || { echo "pwa bundle missing in $PUB" >&2; exit 1; }
python3 "$ROOT/tools/verify_avalonia_patch.py" "$STAGE/$NAME/pwa"
case "$RID" in
  win-*) (cd "$STAGE" && python3 -c "import shutil,sys;shutil.make_archive(sys.argv[1],'zip','.',sys.argv[2])" "$(cd "$OUT" && pwd)/$NAME" "$NAME") ; ART="$OUT/$NAME.zip" ;;
  *)     chmod +x "$STAGE/$NAME/wanxiang"; tar -C "$STAGE" -czf "$OUT/$NAME.tar.gz" "$NAME"; ART="$OUT/$NAME.tar.gz" ;;
esac
(cd "$OUT" && sha256sum "$(basename "$ART")" > "$(basename "$ART").sha256")
echo "$ART"
