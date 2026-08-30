#!/usr/bin/env python3
"""构建万象内嵌字体：从系统 Sarasa 字体集抽出所需字族并做子集化。

为什么需要这一步：
  1. 原先内嵌的是 `Sarasa Term SC`（等宽终端字体），于是中文段落里的
     拉丁文字全部以等宽呈现，正文观感像日志而不像文章；
  2. 那个 TTF 有 45 MB。PWA 会把它整个下载下来，是交付级不可接受的体积。

产物：
  SarasaGothicSC-Regular.subset.ttf   正文与界面（比例宽度，完整 CJK）
  SarasaTermSC-Regular.subset.ttf     代码与等宽场景（仅拉丁与符号，CJK 回落正文字体）

界面层级靠字号与颜色区分，不额外内嵌 SemiBold：
多一个字重就多十几 MB，而 PWA 要把它整个下载下来。

用法：
  python3 tools/build_fonts.py [--source /usr/share/fonts/sarasa-gothic] [--out src/Wanxiang.UI/Assets/Fonts]
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from fontTools.ttLib import TTCollection, TTFont

# 覆盖范围取「中文界面 + 代码 + 常见符号」的最小完备集。
# CJK 统一表意文字整段保留：聊天内容无法预测，缺字会直接变成豆腐块。
UNICODE_RANGES = ",".join(
    [
        "U+0020-007E",  # 基本拉丁
        "U+00A0-00FF",  # 拉丁补充
        "U+0100-017F",  # 拉丁扩展 A
        "U+02C7,U+02C9,U+02D9",  # 声调符号
        "U+0370-03FF",  # 希腊（数学与科学文本常见）
        "U+0400-04FF",  # 西里尔
        "U+2000-206F",  # 通用标点
        "U+2070-209F",  # 上下标
        "U+20A0-20BF",  # 货币符号
        "U+2100-214F",  # 字母式符号
        "U+2190-21FF",  # 箭头
        "U+2200-22FF",  # 数学运算符
        "U+2460-24FF",  # 带圈数字
        "U+2500-257F",  # 制表符
        "U+25A0-25FF",  # 几何图形
        "U+2600-26FF",  # 杂项符号
        "U+2E80-2EFF",  # CJK 部首补充
        "U+3000-303F",  # CJK 标点
        "U+3040-309F",  # 平假名
        "U+30A0-30FF",  # 片假名
        "U+3105-312F",  # 注音符号
        "U+3200-32FF",  # 带圈 CJK
        "U+4E00-9FFF",  # CJK 统一表意文字
        "U+F900-FA6D",  # CJK 兼容表意文字
        "U+FE10-FE1F",  # 竖排标点
        "U+FE30-FE4F",  # CJK 兼容形式
        "U+FF00-FFEF",  # 半角与全角形式
    ]
)

# 等宽字体只服务代码：CJK 交给正文字体回落，省下十几 MB。
LATIN_RANGES = ",".join(
    [
        "U+0020-007E",
        "U+00A0-00FF",
        "U+0100-017F",
        "U+0370-03FF",
        "U+0400-04FF",
        "U+2000-206F",
        "U+2070-209F",
        "U+20A0-20BF",
        "U+2100-214F",
        "U+2190-21FF",
        "U+2200-22FF",
        "U+2500-257F",
        "U+25A0-25FF",
    ]
)

# 正文需要 liga/calt 来正确整形 CJK 与拉丁混排。
PROSE_FEATURES = "kern,liga,calt,vert,vrt2,ccmp,locl,mark,mkmk,palt,halt"

# 代码视图必须**关掉**编程连字：Sarasa Term 会把 `->` 显示成 `→`，
# 而代码块要如实呈现源字符，否则用户照屏抄写会抄错。
CODE_FEATURES = "kern,ccmp,locl,mark,mkmk"

TARGETS = [
    ("Sarasa-Regular.ttc", "Sarasa Gothic SC", "SarasaGothicSC-Regular.subset.ttf", UNICODE_RANGES, PROSE_FEATURES),
    ("Sarasa-Regular.ttc", "Sarasa Term SC", "SarasaTermSC-Regular.subset.ttf", LATIN_RANGES, CODE_FEATURES),
]


def extract(collection: Path, family: str, destination: Path) -> None:
    """从 .ttc 集合里按字族名取出单个字体另存为 .ttf。"""
    fonts = TTCollection(str(collection), lazy=False).fonts
    for font in fonts:
        if font["name"].getDebugName(1) == family:
            font.save(str(destination))
            return
    families = sorted({f["name"].getDebugName(1) or "?" for f in fonts})
    raise SystemExit(f"family {family!r} not found in {collection}; available: {families}")


def subset(source: Path, destination: Path, unicodes: str, features: str) -> None:
    command = [
        sys.executable,
        "-m",
        "fontTools.subset",
        str(source),
        f"--unicodes={unicodes}",
        f"--layout-features={features}",
        "--name-IDs=*",
        "--name-legacy",
        "--notdef-outline",
        "--recalc-bounds",
        "--drop-tables+=DSIG",
        f"--output-file={destination}",
    ]
    subprocess.run(command, check=True)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default="/usr/share/fonts/sarasa-gothic")
    parser.add_argument("--out", default="src/Wanxiang.UI/Assets/Fonts")
    args = parser.parse_args()

    source = Path(args.source)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory() as scratch:
        scratch_path = Path(scratch)
        for collection_name, family, output_name, unicodes, features in TARGETS:
            collection = source / collection_name
            if not collection.exists():
                raise SystemExit(f"missing {collection}")
            raw = scratch_path / f"{output_name}.raw.ttf"
            print(f"extracting {family} from {collection_name}", flush=True)
            extract(collection, family, raw)
            target = out / output_name
            print(f"subsetting -> {target}", flush=True)
            subset(raw, target, unicodes, features)
            size = target.stat().st_size / (1024 * 1024)
            print(f"  {target.name}: {size:.1f} MiB", flush=True)

    print("done", flush=True)


if __name__ == "__main__":
    main()
