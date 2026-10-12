#!/usr/bin/env python3
"""测试清点（决策 380–383）：防止用例“静默消失”。

子命令：
  groups <list.json> <classes...>   打印这些类在发现清单里的 UI 用例数（分组期望值）
  check  <list.json> <baseline>     源码静态清点 vs 程序集发现，及基线下限

r20 教训：-class 写错格式让 34 个嵌套类 UI 用例从未运行；r21 又发现一条
`// 注释[<Fact>]` 把属性吃进注释、整条用例从未被发现。
"""
import json, re, sys, glob, os

def load(p):
    # 运行器可能在 JSON 前后夹带横幅/诊断行（CI 实测 "Extra data"）：只取第一个以 [ 开头的 JSON 数组。
    text = open(p, encoding="utf-8").read()
    for line in text.splitlines():
        if line.lstrip().startswith("[{") or line.strip() == "[]":
            return json.loads(line)
    i = text.index("[")
    return json.JSONDecoder().raw_decode(text[i:])[0]

def cmd_groups(listp, classes):
    want = set(classes)
    print(sum(1 for t in load(listp) if t["Class"] in want and "UI" in ((t.get("Traits") or {}).get("Category") or [])))

ATTR = re.compile(r"\[<\s*(Fact|Theory)\b")

def source_inventory(root):
    n, problems = 0, []
    for f in sorted(glob.glob(os.path.join(root, "*.fs"))):
        for i, line in enumerate(open(f, encoding="utf-8"), 1):
            code = line.split("//", 1)
            if ATTR.search(code[0]):
                n += 1
            elif len(code) > 1 and ATTR.search(code[1]) and not line.lstrip().startswith("///"):
                # `//` 注释吃掉了测试属性（文档注释 /// 里的示例不算）
                problems.append(f"{os.path.basename(f)}:{i}: 注释里出现测试属性，用例不会被发现：{line.strip()}")
    return n, problems

def cmd_check(listp, baseline):
    data = load(listp)
    methods = {(t["Class"], t["Method"]) for t in data}
    cats = {}
    for t in data:
        for c in (t.get("Traits") or {}).get("Category", ["NonUI"]) or ["NonUI"]:
            cats[c] = cats.get(c, 0) + 1
    if "UI" not in cats: cats["UI"] = 0
    cats["NonUI"] = sum(1 for t in data if not (t.get("Traits") or {}).get("Category"))
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests", "Wanxiang.Tests")
    src, problems = source_inventory(root)
    ok = True
    for p in problems:
        print(f"::error::{p}"); ok = False
    if len(methods) < src:
        print(f"::error::源码有 {src} 个测试属性，程序集只发现 {len(methods)} 个测试方法"); ok = False
    floor = {}
    for line in open(baseline, encoding="utf-8"):
        line = line.split("#", 1)[0].strip()
        if "=" in line:
            k, v = line.split("=", 1); floor[k.strip()] = int(v)
    for k, v in floor.items():
        got = cats.get(k, 0)
        if got < v:
            print(f"::error::{k} 用例数 {got} 低于基线 {v}——有测试消失。确属有意删除请同步下调 tests/test-counts.baseline"); ok = False
    print("清点：" + "，".join(f"{k}={cats.get(k,0)}" for k in sorted(set(cats) | set(floor))) + f"，源码属性={src}", file=sys.stderr)
    return 0 if ok else 1

def cmd_noui(listp, large):
    data = load(listp)
    if large == "1":
        print(sum(1 for t in data if "UI" not in ((t.get("Traits") or {}).get("Category") or [])))
    else:
        print(sum(1 for t in data if not (t.get("Traits") or {}).get("Category")))

if __name__ == "__main__":
    if sys.argv[1] == "noui": cmd_noui(sys.argv[2], sys.argv[3])
    if sys.argv[1] == "groups": cmd_groups(sys.argv[2], sys.argv[3:])
    elif sys.argv[1] == "check": sys.exit(cmd_check(sys.argv[2], sys.argv[3]))
