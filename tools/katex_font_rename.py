#!/usr/bin/env python3
"""把 KaTeX 字体改成「一个字族一张字形表」。

为什么必须这么做：KaTeX_Main 一个字族下有 Regular / Bold / Italic / BoldItalic
四张表，而 `KaTeX_Main-Italic` **没有** U+2212（减号）与 U+00B1（正负号）。
Avalonia 在浏览器端按 (字族, 字重, 字形) 选成员时会挑到 Italic，
于是公式里的减号与正负号变成豆腐块——桌面端选得对，两端表现不一致。

把字族名改成含样式的唯一名（KaTeX_MainItalic 之类），调用方只按名字取字体、
永不依赖样式匹配，这类歧义就从根上没有了。顺带也不会触发合成斜体。
"""

from __future__ import annotations

import sys
from pathlib import Path

from fontTools.ttLib import TTFont

# name 表里与「这是哪个字族的哪个样式」有关的记录
FAMILY_ID = 1
SUBFAMILY_ID = 2
FULL_NAME_ID = 4
POSTSCRIPT_ID = 6
TYPOGRAPHIC_FAMILY_ID = 16
TYPOGRAPHIC_SUBFAMILY_ID = 17


def face_name(path: Path) -> str:
    """KaTeX_Main-BoldItalic.ttf → KaTeX_MainBoldItalic；Regular 不留后缀。"""
    stem = path.stem
    if "-" not in stem:
        return stem
    family, style = stem.rsplit("-", 1)
    return family if style == "Regular" else f"{family}{style}"


def rewrite(path: Path) -> tuple[str, str]:
    font = TTFont(str(path))
    table = font["name"]
    before = table.getDebugName(FAMILY_ID) or "?"
    unique = face_name(path)

    for record in list(table.names):
        if record.nameID in (TYPOGRAPHIC_FAMILY_ID, TYPOGRAPHIC_SUBFAMILY_ID):
            table.names.remove(record)

    for name_id, value in (
        (FAMILY_ID, unique),
        (SUBFAMILY_ID, "Regular"),
        (FULL_NAME_ID, unique),
        (POSTSCRIPT_ID, unique),
    ):
        table.setName(value, name_id, 3, 1, 0x409)
        table.setName(value, name_id, 1, 0, 0)

    # OS/2 与 head 里也记着样式位；留着会让排版栈再次「聪明地」替你选字体
    font["OS/2"].fsSelection = (font["OS/2"].fsSelection & ~0x21) | 0x40
    font["head"].macStyle = 0

    font.save(str(path))
    return before, unique


def main() -> None:
    if len(sys.argv) < 2:
        raise SystemExit("用法: katex_font_rename.py <字体目录>")
    directory = Path(sys.argv[1])
    files = sorted(directory.glob("KaTeX_*.ttf"))
    if not files:
        raise SystemExit(f"{directory} 下没有 KaTeX_*.ttf")
    for path in files:
        before, after = rewrite(path)
        if before != after:
            print(f"  {path.name:32} {before} -> {after}")
    print(f"  共 {len(files)} 张，字族名已唯一化")


if __name__ == "__main__":
    main()
