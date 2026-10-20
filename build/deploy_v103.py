# -*- coding: utf-8 -*-
"""把 v1.0.5「无感修订版」部署到「桌面交付目录」与「插件安装目录」。

★ 关键点：
  1. **保留用户 140 条命令白名单**（那是用户资产，不属于"设置项"）。
  2. 配置写成 v6 口径：`WorkMode=IgnoreIme`（★ 用户明确要求：默认必须是"无视输入法模式"）
     + `<CommandKeyMode>PassThrough</CommandKeyMode>` + `<BypassImeDuringCommand>true</BypassImeDuringCommand>`
     + `ConfigVersion=6`、无 `ForceEnglishDuringCommand`。
     ★★★ **只改代码默认值不够**（v1.0.3 就栽在这里）：已部署的配置里写死了旧值，
     `Load()` 会照它跑 ⇒ 改了默认却毫无效果。所以这里必须**把已部署配置直接改成新默认并升到 v6**；
     同时引擎里也加了出厂值迁移链（v5 出厂值 SwitchIme→IgnoreIme、v5 之前出厂值 PostKey→PassThrough）作双保险。
     ★ 迁移规则与引擎 `XimeConfig.MigrateWorkMode` / `Load()` **逐字对齐**：
       只对"旧格式"里的**出厂值**动手；用户显式选过的值一律原样沿用（否则会把他的选择盖掉）。
  3. 安装目录先备份旧配置（`Xime.config.xml.v103bak` 保留最早那份；
     本轮另存 `Xime.config.xml.postkeybak`，便于回退到"默认档=PostKey"那一版）。
"""
import io
import os
import re
import shutil
from xml.sax.saxutils import escape, unescape

# ★ 仓库根**自相对**（脚本在 <repo>/build/ 下）：
#   不把某个人的绝对路径写死进仓库，clone 到任何位置都能跑。
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = ROOT + "/dist/AutoCAD2013-2024"
# 下面两个是"部署到哪儿"，天生是本机路径 —— 允许用环境变量覆盖，默认值即本机常用位置。
INST = os.environ.get("XIME_INST_DIR", "C:/X输入法/net48")
DESK = os.environ.get("XIME_DESK_DIR", "C:/Users/XJH/Desktop/X输入法测试_v102")
OLD_CFG = INST + "/Xime.config.xml"

FILES = ("Xime.dll", "Xime.Core.dll", "AutoLoad.lsp", "安装说明.txt")

CUR_VER = 6
DEF_WORKMODE = "IgnoreIme"
DEF_KEYMODE = "PassThrough"
DEF_BYPASS = "true"


def read(p):
    return io.open(p, encoding="utf-8-sig").read()


def strip_comments(t):
    """去掉 XML 注释。

    ★★ 为什么必须先做这一步（2026-10-06 踩坑，配置被写坏过两次）：
       注释里也会出现 `<WorkMode>SwitchIme</WorkMode>` 这种**同名字符串**
       （用来解释"默认配合 SwitchIme 使用才是三全"）。不剥注释，
       `field()` 可能取到注释里的那个值，或把注释当成元素。
    """
    return re.sub(r"<!--.*?-->", "", t, flags=re.S)


def field(t, key):
    """取 `<key>值</key>` 的值（只认**完整且值不含空白/尖括号**的元素）。

    ★★ 为什么值要限定成 `[^<>\\s]+`（2026-10-06 事故现场）：
       旧实现写的是 `<%s>(.*?)</%s>` + `re.S`。一旦配置里有一个**漏了闭合标签的
       畸形行**（例如 `  <CommandKeyMode>PassThrough， -->`），它就会一路吞到
       下一个 `</CommandKeyMode>`，把一个**整块多行文本**当成"值"取回来，
       于是写出的新配置又是畸形的 —— **错误自我延续，越写越烂**。
       限定成"单个 token"后，畸形行不再匹配，正则自然退到后面那个**规范元素**上。
    """
    m = re.search(r"<%s>\s*([^<>\s]+)\s*</%s>" % (key, key), strip_comments(t), re.S)
    return m.group(1).strip() if m else None


def count_elem(t, key):
    """数**规范形式**的 `<key>…</key>` 出现次数（用于写入前的自检）。"""
    return len(re.findall(r"<%s>\s*[^<>\s]+\s*</%s>" % (key, key),
                          strip_comments(t), re.S))


# ── 1) 从旧配置里取出需要沿用的值（并按 v5 迁移规则换算）────────
old = read(OLD_CFG)
enabled = field(old, "Enabled") or "true"
enabled_user = field(old, "EnabledByUser") or "false"
weasel = field(old, "UseWeaselCli") or "true"
old_ver_s = field(old, "ConfigVersion") or "0"
old_ver = int(old_ver_s) if old_ver_s.isdigit() else 0

workmode = field(old, "WorkMode") or DEF_WORKMODE
keymode = field(old, "CommandKeyMode") or DEF_KEYMODE

# 与引擎 XimeConfig.MigrateWorkMode / Load() 的迁移规则**逐字对齐**（出厂值迁移链）
if old_ver < 5 and workmode == "IgnoreIme":
    workmode = "SwitchIme"          # v5 的出厂值
if old_ver < 6 and workmode == "SwitchIme":
    workmode = "IgnoreIme"          # v6 的出厂值（当前默认）
if old_ver < 5 and keymode == "PostKey":
    keymode = "PassThrough"

cmds = [unescape(c.strip())
        for c in re.findall(r"<Command>\s*([^<>\s]+)\s*</Command>",
                            strip_comments(old), re.S)]

print("旧配置: ConfigVersion=%s Enabled=%s EnabledByUser=%s WorkMode=%s CommandKeyMode=%s "
      "UseWeaselCli=%s 白名单=%d 条"
      % (old_ver_s, enabled, enabled_user, workmode, keymode, weasel, len(cmds)))
if len(cmds) < 100:
    raise SystemExit("★ 白名单只读到 %d 条，明显不对 —— 终止，避免覆盖用户资产。" % len(cmds))

# ── 2) 重建配置（格式 = XimeConfig.Save()，v1.0.5 无感修订版口径的注释）────
L = []
A = L.append
A('<?xml version="1.0" encoding="utf-8"?>')
A('<!-- X输入法 配置文件。修改后重启 AutoCAD 或重新执行 xime 命令生效。 -->')
A('<Xime>')
A('  <!-- 配置格式版本：仅供人工识别与一次性迁移，请勿手工修改 -->')
A('  <ConfigVersion>%d</ConfigVersion>' % CUR_VER)
A('  <!-- 是否启用。仅当 <EnabledByUser>true</EnabledByUser> 时本项才生效； -->')
A('  <!-- 否则（用户从未手动开关过）加载时一律启用。 -->')
A('  <Enabled>%s</Enabled>' % enabled)
A('  <!-- 用户是否曾显式开关过：true 后加载时不再覆盖其选择（永远尊重） -->')
A('  <EnabledByUser>%s</EnabledByUser>' % enabled_user)
A('  <!-- 工作模式（两种，互斥）： -->')
A('  <!--   IgnoreIme (0) ★ **当前默认**（用户明确要求）：无视输入法模式。 -->')
A('  <!--                 **绝不修改你的输入法状态**（中/英、键盘布局、状态栏都不动）。 -->')
A('  <!--                 天正二级命令只认真实物理按键 ⇒ 靠下面的 <CommandKeyMode>PassThrough -->')
A('  <!--                 放行物理键；它的副作用（输入法也收到键 ⇒ 弹候选窗）由 -->')
A('  <!--                 <BypassImeDuringCommand> 消除（让 CAD 窗口无视输入法，状态仍不动）。 -->')
A('  <!--   SwitchIme (1) 退路：切换输入法模式。命令期间输入法**真的**在英文 ⇒ 物理键直达天正。 -->')
A('  <!--                 但**会改你的输入法状态**（命令期间显示英文）—— 与「绝不碰输入法」冲突。 -->')
A('  <!-- 现场用命令 XIMEMODE 0|1 即时切换（立即生效并保存），无需重启。 -->')
A('  <WorkMode>%s</WorkMode>' % workmode)
A('  <!-- 命令执行期间（选项/二级提示）按键的投递方式，四档： -->')
A('  <!--   PassThrough  (2) ★ **当前默认**：完全放行物理键 —— 天正这类走 acedGrRead -->')
A('  <!--                   **原始按键通道**的命令**唯一认的档**。 -->')
A('  <!--                   副作用（你自己的输入法也会看到这些键、弹候选窗）由 -->')
A('  <!--                   <BypassImeDuringCommand> 消除；插件绝不修改输入法状态。 -->')
A('  <!--   PostKey      (1) 吞键 + 窗口消息投给**焦点控件**（v1.0.2 上旬短暂当过默认）。 -->')
A('  <!--                   ★ 已现场证伪：消息进 AutoCAD 主线程后**被当命令栏文本**处理 ⇒ -->')
A('  <!--                   命令栏留一个小写 a、天正读原始按键的通道收不到。 -->')
A('  <!--   PostKeyFrame (3) 吞键 + 窗口消息投给**前台顶层窗口**（主框架）： -->')
A('  <!--                   实测天正收不到（主框架不是按键处理入口）。★ 已证伪。 -->')
A('  <!--   ReplayVk     (0) 吞键 + 真 VK/扫描码重放：输入法仍会看到，实测对天正无效。★ 已证伪。 -->')
A('  <!-- 现场用命令 XIMEKEY 0|1|2|3 即时切换（立即生效并保存），无需重启。 -->')
A('  <CommandKeyMode>%s</CommandKeyMode>' % keymode)
A('  <!-- v58 起「命令期间强制英文」开关已整体撤除： -->')
A('  <!--   「无视输入法模式」**绝不修改你的输入法状态**，英文输入靠钩子把按键直接投递给 -->')
A('  <!--   命令栏实现（绕过输入法），而不是去切输入法。 -->')
A('  <!-- v56：切输入法时优先用**小狼毫自己的命令行接口**，而不是盲按 Shift（默认 true）。 -->')
A('  <!--   为什么：Shift 是翻转键，要靠"知道当前态"才切得对；小狼毫走 TSF， -->')
A('  <!--   IMM32 读不回真实中英态，盲按会不会切对纯属碰运气。 -->')
A('  <!--   小狼毫 0.16.0+ 提供：WeaselServer.exe /ascii（英文）、/nascii（中文）， -->')
A('  <!--   是**幂等设值**而非翻转，不依赖焦点窗口，也不需要回读确认。 -->')
A('  <!--   找不到 WeaselServer.exe 时会自动退回真实 Shift，非小狼毫用户不受影响。 -->')
A('  <!--   关掉它（false）→ 行为回到 v55（仅真实 Shift）。 -->')
A('  <UseWeaselCli>%s</UseWeaselCli>' % weasel)
A('  <!-- v6：命令执行期间让 CAD 窗口**无视输入法**（默认 true）。 -->')
A('  <!--   为什么需要：天正这类第三方命令的二级选项只认真实物理按键，所以必须放行物理键 -->')
A('  <!--   （见上面的 PassThrough）；而放行时**输入法也会收到这些键** ⇒ 中文态下弹候选窗。 -->')
A('  <!--   本项解除 CAD 主窗口及其子窗口与**输入法上下文的关联**（ImmAssociateContext， -->')
A('  <!--   微软 KB Q171154），命令结束立刻精确还原 ⇒ 物理键照旧直达天正、输入法收不到键。 -->')
A('  <!--   ★ 它**不修改输入法状态**（中/英、布局、状态栏一律不动）—— 这正是「无视输入法模式」的字面含义。 -->')
A('  <!--   生效范围：仅「无视输入法模式」+ 命令执行期间 + 非文字类命令； -->')
A('  <!--   文字类命令（TEXT/MTEXT/天正文字…）与文字编辑态**刻意不生效**，保证打标注时输入法照常工作。 -->')
A('  <!--   现场验证：XIMEBYPASS（查看）/ XIMEBYPASS 1（立刻解除试一次）/ XIMEBYPASS 0（还原）。 -->')
A('  <!--   改 false → 回到「输入法会弹候选窗」的旧行为（天正照样能用）。 -->')
A('  <BypassImeDuringCommand>%s</BypassImeDuringCommand>' % DEF_BYPASS)
A('  <!-- 执行完毕后需要输入中文的命令白名单（小写，不含前导横线） -->')
A('  <ChineseCommands>')
for c in cmds:
    A('    <Command>%s</Command>' % escape(c))
A('  </ChineseCommands>')
A('</Xime>')
new_cfg = "\r\n".join(L) + "\r\n"

# ── 2b) 写入前自检：**绝不落一份解析不了的配置** ─────────────────────
#   ★★★ 这是 2026-10-06 那次"配置被写坏"事故的正面防线：
#     当时旧配置里有一个漏闭合标签的畸形行，`field()` 把一个多行整块文本当成
#     `<CommandKeyMode>` 的值，于是新配置也被写坏 —— 而且**会自我延续**。
#     从此以后：生成完先解析一遍，任何一个键不是"恰好一次"就**拒绝写入**。
import xml.etree.ElementTree as ET

KEYS = ("ConfigVersion", "Enabled", "EnabledByUser", "WorkMode", "CommandKeyMode",
        "UseWeaselCli", "BypassImeDuringCommand")

try:
    _root = ET.fromstring(new_cfg)
except Exception as _e:
    raise SystemExit("★ 生成的配置不是合法 XML（%s）—— 已终止，绝不落盘。" % _e)

for _k in KEYS:
    _n = count_elem(new_cfg, _k)
    if _n != 1:
        raise SystemExit("★ 自检失败：<%(k)s> 出现 %(n)d 次（应为 1 次）—— 已终止，绝不落盘。"
                         % {"k": _k, "n": _n})
_n_cmd = len(_root.findall("./ChineseCommands/Command"))
if _n_cmd != len(cmds):
    raise SystemExit("★ 自检失败：白名单写出 %d 条、读出 %d 条 —— 已终止，绝不落盘。"
                     % (_n_cmd, len(cmds)))
print("自检通过：XML 合法、七个键各 1 次、白名单 %d 条。" % _n_cmd)


def deploy(dst, backups=()):
    if not os.path.isdir(dst):
        os.makedirs(dst)
    for f in FILES:
        shutil.copy2(os.path.join(SRC, f), os.path.join(dst, f))
    p = os.path.join(dst, "Xime.config.xml")
    for b in backups:
        bp = os.path.join(dst, b)
        if os.path.exists(p) and not os.path.exists(bp):
            shutil.copy2(p, bp)
            print("  已备份旧配置 →", b)
    io.open(p, "w", encoding="utf-8", newline="").write(new_cfg)
    print("  已写入", dst)


BAK = ("Xime.config.xml.v103bak", "Xime.config.xml.postkeybak")
deploy(DESK, backups=BAK)
deploy(INST, backups=BAK)

# ── 3) 复核 ──────────────────────────────────────────────────
print("")
for d in (DESK, INST):
    print(d)
    for f in sorted(os.listdir(d)):
        print("   %-28s %d 字节" % (f, os.path.getsize(os.path.join(d, f))))
    t = read(os.path.join(d, "Xime.config.xml"))
    try:
        _r = ET.fromstring(t)
        _ok = all(count_elem(t, k) == 1 for k in KEYS)
        print("   → 回读自检：XML 合法 ✓  七个键各 1 次 = %s  白名单 %d 条"
              % ("✓" if _ok else "✗", len(_r.findall("./ChineseCommands/Command"))))
    except Exception as _e:
        print("   → 回读自检：★ 解析失败（%s）" % _e)
    print("   → WorkMode               =", field(t, "WorkMode"))
    print("   → CommandKeyMode         =", field(t, "CommandKeyMode"))
    print("   → BypassImeDuringCommand =", field(t, "BypassImeDuringCommand"))
    print("   → ConfigVersion          =", field(t, "ConfigVersion"))
    print("   → ForceEnglishDuringCommand 残留 =",
          ("有（不该有！）" if "ForceEnglishDuringCommand>" in t else "无 ✓"))
    print("   → 白名单 =", len(re.findall(r"<Command>", t)), "条")
print("")
print("部署完成。")
