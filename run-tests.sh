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
#   ./run-tests.sh --large-history  # 额外跑 LargeHistory 分级（默认排除；§24 定期分级）
set -uo pipefail

ROOT=$(cd "$(dirname "$0")" && pwd)
PROJ="$ROOT/tests/Wanxiang.Tests/Wanxiang.Tests.fsproj"
OUT="$ROOT/tests/Wanxiang.Tests/bin/Debug/net10.0"
BIN="$OUT/Wanxiang.Tests"

JOBS=$(nproc 2>/dev/null || echo 4)
MODE=all
BUILD=1
LARGE_HISTORY=0
while [ $# -gt 0 ]; do
  case "$1" in
    -j) JOBS="$2"; shift 2 ;;
    --ui-only) MODE=ui; shift ;;
    --no-ui) MODE=noui; shift ;;
    --no-build) BUILD=0; shift ;;
    --large-history) LARGE_HISTORY=1; shift ;;
    *) shift ;;
  esac
done

if [ "$BUILD" = "1" ] || [ ! -x "$BIN" ]; then
  echo "构建…" >&2
  dotnet build "$PROJ" -c Debug --verbosity quiet || exit 1
fi
[ -x "$BIN" ] || { echo "找不到 $BIN" >&2; exit 1; }
cd "$OUT" || exit 1

# 每个测试进程会起多个 ASP.NET 主机，reloadOnChange 的配置监视各占 inotify 实例；
# -j 8 时可能撞上 Linux 默认 max_user_instances=128。测试里改用轮询并关掉主机配置
# 热加载（只影响测试进程，不改产品默认）。ConfigStore 自己的 FileSystemWatcher 不受
# 这两个变量影响，若仍有关停期崩溃，下方按分组打印的退出码与日志尾部会指名是谁。
export DOTNET_USE_POLLING_FILE_WATCHER=${DOTNET_USE_POLLING_FILE_WATCHER:-1}
export DOTNET_hostBuilder__reloadConfigOnChange=${DOTNET_hostBuilder__reloadConfigOnChange:-false}

TMP=$(mktemp -d)
KEEP_TMP=${KEEP_TMP:-0}
trap '[ "$KEEP_TMP" = 1 ] || rm -rf "$TMP"' EXIT
FAILED=0

# 用例清点（决策 380–383）：发现清单是每个分组“应跑多少”的唯一来源。
# 源码静态清点 + 基线下限任一不符即失败——测试不许静默消失。
INV="$ROOT/tools/test_inventory.py"
./Wanxiang.Tests -preEnumerateTheories -list full/json 2>/dev/null > "$TMP/all.json"
if ! python3 "$INV" check "$TMP/all.json" "$ROOT/tests/test-counts.baseline"; then
  FAILED=1
fi
# 从汇总行取 Total（去色）。
total_of() { sed 's/\x1b\[[0-9;]*m//g' "$1" | grep -oE "Total: [0-9]+" | tail -1 | grep -oE "[0-9]+" || echo 0; }

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
          # r20 实测（xunit v3）：-class 要的是反射全名，嵌套类保留 `+`（`Outer+Inner`）。
          # 换成 `.` 反而匹配不到，那几个用例一直被静默跳过，直到它单独成组跑出 Total: 0 才暴露。
          args+=(-class "$c")
        done < "$g"
        ./Wanxiang.Tests -trait "Category=UI" "${args[@]}" > "$g.out" 2>&1
        echo $? > "$g.rc"
      ) &
      pids+=($!)
    done
    for p in "${pids[@]}"; do wait "$p" || true; done

    for g in "$TMP"/ui-group-*; do
      [ -f "$g.out" ] || continue
      # 只汇总结论行与失败明细，抑制 xunit 的进度噪音。
      grep -E "Total:|^\s+Wanxiang.*\[FAIL\]" "$g.out" || true
      if ! grep -qE "Failed: 0," "$g.out"; then
        FAILED=1
        sed 's/\x1b\[[0-9;]*m//g' "$g.out" | grep -A30 "\[FAIL\]" >&2 || true
      fi
      # 进程退出码与汇总行分开判：全部用例通过但进程退出非 0（关停期崩溃、
      # 未处理异常、被信号杀死）同样算失败，但要指名是哪个分组、打印尾部日志，
      # 否则 CI 只剩一句 "exit code 1" 无从追查。
      # 分组期望 = 发现清单里这些类的用例数；实际 Total 不等即有用例被过滤掉没跑。
      mapfile -t gcls < "$g"
      expect=$(python3 "$INV" groups "$TMP/all.json" "${gcls[@]}")
      got=$(total_of "$g.out")
      if [ "$expect" != "$got" ]; then
        FAILED=1
        echo "::error::UI 分组 $(basename "$g") 应跑 $expect 个用例，实际 $got 个——过滤条件漏掉了用例" >&2
      fi
      rc=$(cat "$g.rc" 2>/dev/null || echo missing)
      if [ "$rc" != "0" ]; then
        FAILED=1
        echo "::error::UI 分组 $(basename "$g") 进程退出码 $rc（汇总行之外的失败，见下方日志尾部）" >&2
        echo "--- $(basename "$g") 类清单:" >&2; cat "$g" >&2
        echo "--- $(basename "$g") 输出尾部:" >&2
        sed 's/\x1b\[[0-9;]*m//g' "$g.out" | tail -n 80 >&2
      fi
    done
  fi
fi

if [ "$MODE" != "ui" ]; then
  echo "非 UI：单进程内线程级并行" >&2
  # 默认排除 LargeHistory 分级（§24：10^5/10^6 定期跑，不进 PR gate）
  ARGS=(-trait- "Category=UI")
  if [ "$LARGE_HISTORY" = "0" ]; then
    ARGS+=(-trait- "Category=LargeHistory")
  fi
  ./Wanxiang.Tests "${ARGS[@]}" > "$TMP/noui.out" 2>&1
  noui_rc=$?
  if [ "$LARGE_HISTORY" = "0" ]; then
    expect=$(python3 -c 'import json,sys;print(sum(1 for t in json.load(open(sys.argv[1])) if not (t.get("Traits") or {}).get("Category")))' "$TMP/all.json")
  else
    expect=$(python3 -c 'import json,sys;print(sum(1 for t in json.load(open(sys.argv[1])) if "UI" not in (t.get("Traits") or {}).get("Category",[])))' "$TMP/all.json")
  fi
  got=$(total_of "$TMP/noui.out")
  if [ "$expect" != "$got" ]; then
    FAILED=1
    echo "::error::非 UI 应跑 $expect 个用例，实际 $got 个——过滤条件漏掉了用例" >&2
  fi
  grep -E "Total:|^\s+Wanxiang.*\[FAIL\]" "$TMP/noui.out" || true
  if ! grep -qE "Failed: 0," "$TMP/noui.out"; then
    FAILED=1
    # 失败时输出明细（去掉 ANSI 颜色码再匹配），CI 日志里才看得到原因。
    sed 's/\x1b\[[0-9;]*m//g' "$TMP/noui.out" | grep -A30 "\[FAIL\]" >&2 || true
  fi
  if [ "$noui_rc" != "0" ]; then
    FAILED=1
    echo "::error::非 UI 进程退出码 $noui_rc，输出尾部：" >&2
    sed 's/\x1b\[[0-9;]*m//g' "$TMP/noui.out" | tail -n 80 >&2
  fi
fi

exit $FAILED
