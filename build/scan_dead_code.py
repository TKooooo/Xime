# -*- coding: utf-8 -*-
"""
启发式死代码扫描：
  1. 抽取每个 .cs 里的成员声明（方法 / 字段 / 属性 / 常量 / 事件）
  2. 统计该标识符在整个 src/ 下的出现次数
  3. 只出现 1 次（即只有声明本身）→ 疑似无用
  4. 另外检查未使用的 using

这是启发式扫描，结果需人工确认（反射调用、序列化、XAML 绑定等会误报）。
"""

import os
import re
import sys
from collections import defaultdict

SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src")

FILES = []
for root, dirs, files in os.walk(SRC):
    dirs[:] = [d for d in dirs if d not in ("obj", "bin")]
    for f in files:
        if f.endswith(".cs"):
            FILES.append(os.path.join(root, f))

# ---------- 读全部源码 ----------
SOURCES = {}
for p in FILES:
    with open(p, "r", encoding="utf-8-sig", errors="replace") as fp:
        SOURCES[p] = fp.read()


def strip_comments(text):
    """
    只剥离注释，保留字符串字面量的**位置占位**（用空格替换，不参与标识符统计）。

    ★ 为什么必须区分「逐字字符串」：
       C# 里 @"..." 的反斜杠不是转义符。早先的实现一律把 \\ 当转义处理，
       于是 @"C:\\path\\" 这种结尾带反斜杠的逐字字符串会被误判为"字符串未结束"，
       把后面的真实代码一起吞掉 —— 直接后果是大量**假阳性**
       （实测：AutoLoadRegistration.cs 的 14 个方法被误报为死代码）。

    ★ 为什么要处理字符串里的 // ：
       字符串中可能出现 "http://..." 这类内容，若不先识别字符串，
       就会把 // 当成行注释，吞掉本行剩余的真实代码 —— 同样是假阳性。

    代码注释（// 与 /* */）用空格替换，但不跨行删除，保证行号不变。
    """
    out = []
    i, n = 0, len(text)

    def blank(j):
        return " " if text[j] != "\n" else "\n"

    while i < n:
        c = text[i]

        # ---- 行注释 ----
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            if j < 0:
                j = n
            out.append(" " * (j - i))
            i = j
            continue

        # ---- 块注释 ----
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("".join(blank(k) for k in range(i, j)))
            i = j
            continue

        # ---- 逐字字符串 @"..." （"" 表示一个引号，反斜杠不是转义）----
        if c == "@" and i + 1 < n and text[i + 1] == '"':
            j = i + 2
            while j < n:
                if text[j] == '"':
                    if j + 1 < n and text[j + 1] == '"':
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            out.append("".join(blank(k) for k in range(i, min(j, n))))
            i = j
            continue

        # ---- 普通字符串 "..." （含 $ 插值，按普通字符串处理即可）----
        if c == '"':
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == '"':
                    j += 1
                    break
                j += 1
            out.append("".join(blank(k) for k in range(i, min(j, n))))
            i = j
            continue

        # ---- 字符字面量 '...' ----
        if c == "'":
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == "'":
                    j += 1
                    break
                j += 1
            out.append("".join(blank(k) for k in range(i, min(j, n))))
            i = j
            continue

        out.append(c)
        i += 1

    return "".join(out)


# 去注释后的代码（用于统计引用次数）
CODE_ONLY = "\n".join(strip_comments(t) for t in SOURCES.values())

ALL_TEXT = "\n".join(SOURCES.values())


def rel(p):
    return os.path.relpath(p, SRC)


# ---------- 1) 成员声明扫描 ----------
# 匹配形如：
#   [特性]... 修饰符 [static] [readonly] [const] 返回类型 名称(
#   [特性]... 修饰符 [static] [readonly] [const] 类型 名称; / = / {
DECL = re.compile(
    r"^\s*"
    r"(?:(?:public|private|protected|internal|static|readonly|const|volatile|"
    r"sealed|override|virtual|abstract|extern|unsafe|new|partial|async)\s+)+"
    r"(?P<ret>[A-Za-z_][\w<>\[\],\.\? ]*)\s+"
    r"(?P<name>[A-Za-z_]\w*)\s*"
    r"(?P<kind>[\(;=\{])",
    re.M)

# 已知的误报 / 框架回调（由运行时反射或设计器调用）
IGNORE_NAMES = {
    # P/Invoke 与固有回调
    "DllImport", "StructLayout", "FieldOffset", "MarshalAs", "return",
    # 命令入口
}

# 已知的框架回调前缀（命令方法、事件处理）
FRAMEWORK_OK = re.compile(
    r"^(On[A-Z]|Button|Timer|Hook|Msg|Wnd|.*Handler$)")

findings = []
using_by_file = {}

for path, text in SOURCES.items():
    # ---- 成员声明 ----
    lines = text.split("\n")
    for m in DECL.finditer(text):
        name = m.group("name")
        ret = (m.group("ret") or "").strip()
        kind = m.group("kind")

        if name in IGNORE_NAMES:
            continue
        if len(name) <= 2:
            continue
        # 排除控制流关键字被误判
        if ret in ("return", "new", "throw", "else", "case", "do", "if", "while",
                   "using", "lock", "fixed", "yield", "await", "get", "set"):
            continue
        if name in ("get", "set", "add", "remove", "value"):
            continue

        # 统计全项目出现次数（按整词，且**忽略注释与字符串**）
        cnt = len(re.findall(r"\b" + re.escape(name) + r"\b", CODE_ONLY))
        if cnt <= 1:
            line_no = text[:m.start()].count("\n") + 1
            findings.append((rel(path), line_no, ret, name, kind, cnt))

# ---------- 输出 ----------
print("=" * 78)
print("疑似未使用的成员（全项目仅出现 1 次 = 只有声明）")
print("=" * 78)
if not findings:
    print("（无）")
for f, ln, ret, name, kind, cnt in sorted(findings):
    kinddesc = {"(": "方法", ";": "字段", "=": "字段", "{": "属性"}.get(kind, kind)
    print("  %-42s:%-5d  %-8s %-24s  %s" % (f, ln, kinddesc, name, ret))

print()
print("=" * 78)
print("未使用的 using")
print("=" * 78)
print("  （本项交给编译器判定，不再自行启发式猜测）")
print("  运行： dotnet build -c Release -p:EnforceCodeStyleInBuild=true")
print("         然后筛 IDE0005 即可得到权威结果。")
print()
print("总计: %d 个疑似无用成员" % len(findings))
