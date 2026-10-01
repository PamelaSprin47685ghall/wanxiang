#!/usr/bin/env python3
"""给碰 Avalonia 的测试打 `[<Trait("Category", "UI")>]`。

## 为什么需要这个

Avalonia 的 headless 平台（`DefaultRenderLoop` / `Compositor`）是**进程级单例**，
`HeadlessUnitTestSession.DispatchCore` 每次 dispatch 都要重装应用服务。同进程内
多个线程轮流进同一 session 会偶发抛
`The calling thread cannot access this object because a different thread owns it`，
一次就毒化整个 session——实测 983 个用例里 373 个连带失败、耗时从 34s 缩到 14s。

因此 `run-tests.sh` 把 UI 测试拆到独立进程并行（每进程一个 session），非 UI 测试
留在单进程里做线程级并行。分组依据就是这个 trait。

## 判据：唯一可靠的入口

**测试体里出现 `Headless.run`**。那是碰 Avalonia 的唯一入口——不碰 Avalonia 的测试
根本不需要它（裸构造控件会抛 `Unable to locate 'Avalonia.Platform.IAssetLoader'`
或 `IPlatformRenderInterface`，这是判据的反向验证）。

不采用的两种判据：
- **文件名像不像 UI 测试**：命名不统一（`UiStabilityTests` / `AppShellTests` /
  `PresentationRefreshTests` 都是 UI），靠名字猜必然错。
- **正则扒源码里的 `open Avalonia`**：测试类名与源文件名的对应关系不规则
  （81 个类里 35 个对不上），且 `open Avalonia` 出现在辅助文件里不等于该文件的
  每个测试都碰 UI。

## 用法

    python3 tools/tag_ui_tests.py            # 打/更新
    python3 tools/tag_ui_tests.py --check    # 只校验，不改（CI 用；退出码非 0 表示有漏/多）

## 幂等

已打过的不会重复打。`--check` 的判定是「重新计算出的期望集合 == 文件现状」，
因此新增 UI 测试忘了跑本脚本、或误删了 trait，都会被 CI 抓到。
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent.parent / "tests" / "Wanxiang.Tests"
MARKER = '[<Trait("Category", "UI")>]'
ENTRY = "Headless.run"

# 不参与标记：装置本身（不含测试），以及纯逻辑聚合文件（内含嵌套类但整体不碰 UI）。
SKIP_FILES = {"Helpers.fs", "Headless.fs"}

# 显式豁免：某些测试断言的正是「不碰 UI 线程」，打上 trait 会让它自证失败。
EXEMPT_TESTS = {
    # 文件名 -> 测试名片段
    "HeadlessTests.fs": ["does not require the ui thread"],
}

FACT_RE = re.compile(r"^\s*\[<(Fact|Theory)>\]")


def _touches_avalonia(src: str) -> bool:
    return ENTRY in src


def _exempt(name: str, body: str) -> bool:
    for fname, fragments in EXEMPT_TESTS.items():
        if name == fname and any(frag in body for frag in fragments):
            return True
    return False


def _split_facts(lines: list[str]) -> list[tuple[int, str, bool]]:
    """返回 [(起始行号, 测试体文本, 是否已打 trait)]。

    从每个 `[<Fact>]` / `[<Theory>]` 切到下一个同类标记（或文件末）。
    扫描窗口按「下一个 [<Fact>] / [<Theory>]」截断，不按固定行数——
    固定窗口会误把相邻用例的 `Headless.run` 算进当前用例。
    """
    starts = [i for i, line in enumerate(lines) if FACT_RE.match(line)]
    out: list[tuple[int, str, bool]] = []
    for idx, start in enumerate(starts):
        end = starts[idx + 1] if idx + 1 < len(starts) else len(lines)
        chunk = lines[start:end]
        # trait 贴在 `[<Fact>]` **之前**（F# 的属性惯例），所以判定窗口要往前看一行。
        probe = lines[start - 1] if start > 0 else ""
        tagged = probe.strip() == MARKER
        out.append((start, "\n".join(chunk), tagged))
    return out


def _class_container(lines: list[str]) -> tuple[int, str] | None:
    """找到承载 UI 测试的容器（`type X() =` 或 `module X =`），返回 (行号, 类型名)。

    容器级 trait 覆盖类里所有方法；方法级 trait 只覆盖单个 `[<Fact>]`。
    类级 `[<Fact>]`（`type X() = member _.``...```）必须用容器级，否则方法级
    属性贴在 `let` 上不生效。
    """
    for i, line in enumerate(lines):
        s = line.strip()
        # 只认测试容器：`type X() =` / `type X(arg) =`。测试装置常写成
        # `type private FixtureBackend(...)`（MathBaselineTests 就是），
        # 它不是容器，匹配到它会让「容器级 trait 覆盖范围」算错。
        m = re.match(r"^type\s+(?!private\b|internal\b)([A-Za-z0-9_]+)\s*(?:\([^)]*\))?\s*=", s)
        if m:
            return i, m.group(1)
        m = re.match(r"^module\s+([A-Za-z0-9_.]+)\s*=", s)
        if m:
            return i, m.group(1)
    return None


def process(path: Path, check: bool) -> tuple[bool, str]:
    src = path.read_text(encoding="utf-8")
    lines = src.split("\n")
    facts = _split_facts(lines)

    ui_facts = [
        (start, body, tagged)
        for start, body, tagged in facts
        if ENTRY in body and not _exempt(path.name, body)
    ]

    if not ui_facts:
        if MARKER in src:
            return False, f"{path.name}: 无 UI 测试却带 UI trait"
        return True, ""

    # 容器级 trait（贴在 `type X() =` / `module X =` 上）覆盖该容器内**全部**方法，
    # 因此它同样是"每个 UI 测试都被标注"——`IconCenteringTests` 三个方法共用一个
    # 容器级 trait 就是这种情况。反过来，容器内**混有**非 UI 方法时不能只用容器级，
    # 那种情况由本脚本的方法级分支兜住。
    all_tagged = all(tagged for _, _, tagged in ui_facts)
    method_marks = sum(1 for _, _, t in ui_facts if t)

    # 容器级 trait 只覆盖**容器内部**的方法。文件里同时还有顶层 `[<Fact>]`
    # （MathBaselineTests 就是这种：容器 Baseline + 一个顶层 fact）时，顶层那个
    # 必须自己带 trait——否则它会被漏进 UI 分组之外的进程。
    container = _class_container(lines)
    container_range = None
    if container is not None:
        idx, _ = container
        end = len(lines)
        for j in range(idx + 1, len(lines)):
            stripped = lines[j].strip()
            indent = len(lines[j]) - len(lines[j].lstrip())
            # 容器在缩进 0 处结束；`[<Trait>]` / `[<Fact>]` 在缩进 0 处是
            # **容器自己的**属性或新的顶层测试，不能据此判定容器结束。
            if stripped and indent == 0 and (stripped.startswith("type") or stripped.startswith("module")):
                end = j
                break
        container_range = (idx, end)

    def covered_by_container(start: int) -> bool:
        if container_range is None:
            return False
        lo, hi = container_range
        probe = lines[lo - 1] if lo > 0 else ""
        return lo < start < hi and probe.strip() == MARKER

    uncovered = [start for start, _, tagged in ui_facts if not tagged and not covered_by_container(start)]
    if not uncovered:
        return True, ""
    if check:
        return False, f"{path.name}: {len(ui_facts)} 个 UI 测试缺少 {MARKER}（跑 tools/tag_ui_tests.py）"

    if check:
        return False, (
            f"{path.name}: {len(uncovered)} 个 UI 测试缺少 {MARKER}"
            f"（跑 tools/tag_ui_tests.py）"
        )

    # 方法级插入：从后往前，避免行号漂移。
    for start in sorted(uncovered, reverse=True):
        indent = len(lines[start]) - len(lines[start].lstrip())
        lines.insert(start, " " * indent + MARKER)
    else:
        return False, f"{path.name}: trait 标注不一致，需人工确认"

    path.write_text("\n".join(lines), encoding="utf-8")
    return True, f"{path.name}: 标注 {len(ui_facts)} 个 UI 测试"


def main() -> int:
    check = "--check" in sys.argv
    ok = True
    scanned = 0
    marked = 0
    changed = 0
    for path in sorted(TESTS_DIR.glob("*.fs")):
        if path.name in SKIP_FILES:
            continue
        src = path.read_text(encoding="utf-8")
        if not _touches_avalonia(src):
            continue
        scanned += 1
        marked += src.count(MARKER)
        good, msg = process(path, check)
        if msg:
            if not good:
                print("✗ " + msg, file=sys.stderr)
            else:
                print("  " + msg)
                changed += 1
        ok = ok and good

    if check:
        if ok:
            print(f"✓ UI trait 标注一致（{scanned} 个文件含 UI 测试，{marked} 个方法已标注）")
        else:
            print("✗ UI trait 标注不一致", file=sys.stderr)
        return 0 if ok else 1

    print(f"✓ {scanned} 个含 UI 测试的文件，新标注 {changed} 个方法")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())