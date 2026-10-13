#!/usr/bin/env bash
# 压力找竞态：重复跑全套（可选 CPU 占用），汇总每个失败测试的次数，输出 flake 报告。
# 用法：tools/stress.sh [-n 次数] [-j 进程] [--hog 线程数] [-o 报告目录]
set -uo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
N=10; J=8; HOG=0; OUT="${TMPDIR:-/tmp}/wx-stress"
while [ $# -gt 0 ]; do case "$1" in
  -n) N=$2; shift 2;; -j) J=$2; shift 2;; --hog) HOG=$2; shift 2;; -o) OUT=$2; shift 2;; *) shift;; esac; done
mkdir -p "$OUT"; : > "$OUT/failures.txt"
HOGS=()
for _ in $(seq 1 "$HOG"); do ( while :; do :; done ) & HOGS+=($!); done
trap '[ ${#HOGS[@]} -gt 0 ] && kill "${HOGS[@]}" 2>/dev/null' EXIT
"$ROOT/run-tests.sh" --ui-only -j 1 >/dev/null 2>&1 || true  # 预构建
RED=0
for i in $(seq 1 "$N"); do
  "$ROOT/run-tests.sh" --no-build -j "$J" > "$OUT/run-$i.log" 2>&1; rc=$?
  sed 's/\x1b\[[0-9;]*m//g' "$OUT/run-$i.log" | grep -oE "Wanxiang\.Tests\.[A-Za-z0-9_.+]+[^[]*\[FAIL\]" | sed 's/ *\[FAIL\]//' | sort -u | sed "s/^/$i\t/" >> "$OUT/failures.txt"
  grep -q "::error::" "$OUT/run-$i.log" && grep "::error::" "$OUT/run-$i.log" | sed "s/^/$i\t/" >> "$OUT/failures.txt"
  [ $rc -ne 0 ] && RED=$((RED+1))
  echo "第 $i/$N 轮 rc=$rc"
done
{ echo "# 万象 flake 报告（$N 轮，-j $J，CPU 占用 $HOG）"; echo; echo "红轮次：$RED/$N"; echo
  echo "| 次数 | 测试 |"; echo "|---|---|"
  cut -f2- "$OUT/failures.txt" | sort | uniq -c | sort -rn | awk '{c=$1;$1="";print "| "c" |"$0" |"}'; } > "$OUT/flake-report.md"
cat "$OUT/flake-report.md"
exit 0
