#!/usr/bin/env python3
"""决策 403：断言某个 avalonia.js（或含 _framework/avalonia.js 的目录）带有当前版本的万象补丁。

用法：verify_avalonia_patch.py <avalonia.js | AppBundle 目录 | pwa 目录> ...
退出码非 0 表示缺补丁、版本过期或缺标记。CI 与 App 发布流程都会调用。
"""
from __future__ import annotations
import pathlib, subprocess, sys

HERE = pathlib.Path(__file__).resolve().parent
MARKERS = ["/*wx-dpr-guard*/", "/*wx-insert-bridge*/", "failIfMajorPerformanceCaveat:!1"]

def expected_version() -> str:
    out = subprocess.run([sys.executable, str(HERE / "patch_avalonia_js.py"), "--version"],
                         check=True, capture_output=True, text=True)
    return out.stdout.strip()

def resolve(p: pathlib.Path) -> pathlib.Path:
    return p / "_framework" / "avalonia.js" if p.is_dir() else p

def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__); return 2
    ver = expected_version(); bad = 0
    for arg in sys.argv[1:]:
        f = resolve(pathlib.Path(arg))
        if not f.exists():
            print(f"FAIL {f}: missing"); bad += 1; continue
        t = f.read_text()
        header = f"/*wx-patch-version:{ver}*/"
        missing = [m for m in MARKERS if m not in t]
        if not t.startswith(header):
            print(f"FAIL {f}: expected header {header}, got {t[:48]!r}"); bad += 1
        elif missing:
            print(f"FAIL {f}: missing markers {missing}"); bad += 1
        else:
            print(f"OK   {f}: patch v{ver}")
    return 1 if bad else 0

if __name__ == "__main__":
    raise SystemExit(main())
