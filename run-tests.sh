#!/usr/bin/env bash
# 万象测试执行器：UI 测试**进程级**并行，非 UI 测试**线程级**并行。
#
# ## 为什么分两层
#
# Avalonia 的 headless 平台（`DefaultRenderLoop` / `Compositor`）是**进程级单例**，
# `HeadlessUnitTestSession.DispatchCore` 每次 dispatch 都要重装应用服务。同进程内让
# 多个线程轮流进同一 session，会偶发抛
# `The calling thread cannot access this object because a different thread owns it`，
# 一次就毒化整个 session——实测 983 个用例里 373 个连带失败、耗时从 34s 缩到 14s，
# 看起来像业务代码坏了，实际是装置竞态（基线同样存在，不是 UI 改动引入）。
#
# 因此：
# | 测试 | 分组依据 | 并行粒度 | 理由 |
# |---|---|---|---|
# | UI（碰 Avalonia） | `[<Trait("Category","UI")>]` | **进程**：拆成 N 个独立进程 | headless 平台进程级单例 |
# | 非 UI（纯逻辑） | 无该 trait | **线程**：单进程内 xunit 并行 | 无共享可变状态，最快 |
#
# trait 由脚本生成器打（见 `tools/tag_ui_tests.py`），判据是**该测试体里是否出现
# `Headless.run`**——那是碰 Avalonia 的唯一入口，不靠文件名或命名猜。
#
# ## 用法
#
#   ./run-tests.sh              # 按 CPU 数分组
#   ./run-tests.sh -j 8         # 指定进程数
#   ./run-tests.sh --ui-only    # 只跑 UI
#   ./run-tests.sh --no-ui      # 只跑非 UI
#   ./run-tests.sh --no-build   # 跳过构建
set -uo pipefail

ROOT=$(cd "$(dirname "$0")" && pwd)
PROJ="$ROOT/tests/Wanxiang.Tests/Wanxiang.Tests.fsproj"
OUT="$ROOT/tests/Wanxiang.Tests/bin/Debug/net10.0"
BIN="$OUT/Wanxiang.Tests"

JOBS=$(nproc 2>/dev/null || echo 4)
MODE=all
BUILD=1
while [ $# -gt 0 ]; do
  case "$1" in
    -j) JOBS="$2"; shift 2 ;;
    --ui-only) MODE=ui; shift ;;
    --no-ui) MODE=noui; shift ;;
    --no-build) BUILD=0; shift ;;
    *) shift ;;
  esac
done

if [ "$BUILD" = "1" ] || [ ! -x "$BIN" ]; then
  echo "构建…" >&2
  dotnet build "$PROJ" -c Debug --verbosity quiet || exit 1
fi
[ -x "$BIN" ] || { echo "找不到 $BIN" >&2; exit 1; }
cd "$OUT" || exit 1

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
FAILED=0

# UI 分组：按测试类轮转分到 N 个进程。
# 类清单来自程序集自身枚举（`-list classes`），不是猜文件名。
if [ "$MODE" != "noui" ]; then
  ./Wanxiang.Tests -list classes 2>/dev/null \
    | grep -E '^Wanxiang\.Tests\.' | sort -u > "$TMP/classes.txt"

  # 只有**带 UI trait**的类才进 UI 进程。混合类清单会让纯逻辑类被 -trait 过滤成
  # "0 个用例"（实测 81 类里 3 组如此），看起来像丢测试。
  ./Wanxiang.Tests -trait "Category=UI" -list classes 2>/dev/null \
    | grep -E '^Wanxiang\.Tests\.' | sort -u > "$TMP/classes.txt"

  # 每个类单独跑一个进程过于碎；这里按类分成 N 组，组内串行（一个进程一个 session）。
  total=$(wc -l < "$TMP/classes.txt")
  if [ "$total" -gt 0 ]; then
    per=$(( (total + JOBS - 1) / JOBS ))
    echo "UI：$total 个测试类 → $JOBS 个进程（每进程约 $per 类）" >&2

    split -l "$per" -d "$TMP/classes.txt" "$TMP/ui-group-"
    pids=()
    for g in "$TMP"/ui-group-*; do
      [ -s "$g" ] || continue
      (
        args=()
        while read -r c; do
          [ -n "$c" ] || continue
          # xunit 的 -class 用点分（`Ns.Outer+Inner` 的 `+` 要换成 `.`）；
          # 保留原样会被当成不存在的类，进程静默跑成 0 个用例。
          args+=(-class "${c//+/.}")
        done < "$g"
        ./Wanxiang.Tests -trait "Category=UI" "${args[@]}" > "$g.out" 2>&1
      ) &
      pids+=($!)
    done
    for p in "${pids[@]}"; do wait "$p" || FAILED=1; done

    for g in "$TMP"/ui-group-*; do
      [ -f "$g.out" ] || continue
      # 只汇总结论行与失败明细，抑制 xunit 的进度噪音。
      grep -E "Total:|^\s+Wanxiang.*\[FAIL\]" "$g.out" || true
      grep -qE "Failed: 0," "$g.out" || FAILED=1
    done
  fi
fi

if [ "$MODE" != "ui" ]; then
  echo "非 UI：单进程内线程级并行" >&2
  ./Wanxiang.Tests -trait- "Category=UI" > "$TMP/noui.out" 2>&1
  grep -E "Total:|^\s+Wanxiang.*\[FAIL\]" "$TMP/noui.out" || true
  grep -qE "Failed: 0," "$TMP/noui.out" || FAILED=1
fi

exit $FAILED
