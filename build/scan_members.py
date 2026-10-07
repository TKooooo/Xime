# -*- coding: utf-8 -*-
"""
对指定文件里的 public/internal 成员，统计「在本文件之外」的使用次数。
用于精确判断 P/Invoke 声明、常量、字段是否为死代码。

结构体字段不参与判断（它们由运行时按布局封送，C# 里不会按名引用）。
"""

import os
import re
import sys

SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src")

TARGET = sys.argv[1] if len(sys.argv) > 1 else "Xime.Core/Native/NativeMethods.cs"
TARGET_PATH = os.path.join(SRC, TARGET.replace("/", os.sep))


def read(p):
    with open(p, "r", encoding="utf-8-sig", errors="replace") as fp:
        return fp.read()


target_text = read(TARGET_PATH)

# 其它所有文件拼起来
others = []
for root, dirs, files in os.walk(SRC):
    dirs[:] = [d for d in dirs if d not in ("obj", "bin")]
    for f in files:
        if not f.endswith(".cs"):
            continue
        p = os.path.join(root, f)
        if os.path.abspath(p) == os.path.abspath(TARGET_PATH):
            continue
        others.append(read(p))
OTHER_TEXT = "\n".join(others)

# 目标文件里的成员名：方法（extern ... 名( ）、常量/字段（const/static ... 名 = / ;）、属性
DECL = re.compile(
    r"^\s*(?:(?:public|private|protected|internal|static|readonly|const|volatile|"
    r"sealed|override|virtual|abstract|extern|unsafe|new|partial)\s+)+"
    r"(?P<ret>[A-Za-z_][\w<>\[\],\.\? ]*)\s+"
    r"(?P<name>[A-Za-z_]\w*)\s*(?P<kind>[\(;=])",
    re.M)

# 结构体字段：位于 struct 块内，跳过
struct_ranges = []
for m in re.finditer(r"\b(?:struct|class)\s+(\w+)[^\{]*\{", target_text):
    name = m.group(1)
    start = m.start()
    depth = 0
    i = target_text.index("{", start)
    j = i
    while j < len(target_text):
        if target_text[j] == "{":
            depth += 1
        elif target_text[j] == "}":
            depth -= 1
            if depth == 0:
                break
        j += 1
    struct_ranges.append((name, i, j))


def in_struct(pos):
    for name, a, b in struct_ranges:
        if a <= pos <= b:
            return name
    return None


used, unused = [], []
for m in DECL.finditer(target_text):
    name = m.group("name")
    kind = m.group("kind")
    if name in ("get", "set", "add", "remove", "value", "return", "new"):
        continue
    owner = in_struct(m.start())
    if owner is not None and kind in (";", "="):
        continue  # 结构体字段由布局封送，不算死代码

    n = len(re.findall(r"\b" + re.escape(name) + r"\b", OTHER_TEXT))
    line = target_text[:m.start()].count("\n") + 1
    (used if n > 0 else unused).append((line, m.group("ret"), name))

print("目标文件:", TARGET)
print()
print("=== 本文件之外**从未使用**（疑似死代码）=== ")
if not unused:
    print("（无）")
for line, ret, name in sorted(unused):
    print("  %-5d %-28s %s" % (line, ret, name))

print()
print("=== 仅被 CliTest 使用（生产代码不用，但测试工具在用）===")
test_only = []
for line, ret, name in sorted(used):
    pat = r"\b" + re.escape(name) + r"\b"
    hits = [l for l in OTHER_TEXT.split("\n") if re.search(pat, l)]
    prod = [h for h in hits
            if not any(k in h for k in ("CliTest", "XimeTest"))]
    # 逐行判断不够准，改用文件级：重新分文件统计
    cnt_prod = 0
    for root, dirs, files in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in ("obj", "bin")]
        for f in files:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(root, f)
            if "CliTest" in p or os.path.abspath(p) == os.path.abspath(TARGET_PATH):
                continue
            if re.search(pat, read(p)):
                cnt_prod += 1
    if cnt_prod == 0:
        test_only.append((line, ret, name))
if not test_only:
    print("（无）")
for line, ret, name in test_only:
    print("  %-5d %-28s %s" % (line, ret, name))
