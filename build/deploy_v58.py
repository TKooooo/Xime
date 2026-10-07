# -*- coding: utf-8 -*-
"""把 v58 部署到「桌面交付目录」与「插件安装目录」。

★ 关键点：
  1. **保留用户 140 条命令白名单**（那是用户资产，不属于"设置项"）。
  2. 配置按 v58 `XimeConfig.Save()` 的**新格式**重建 —— 尤其是
     `<CommandKeyMode>` 改成 `PassThrough`（v58 的默认档），
     并删掉 `<ForceEnglishDuringCommand>`（v58 已整体撤除）。
     ★ 只改默认值不够：已部署的配置里写死了 `ReplayVk`，Load 会照它跑。
  3. 安装目录先备份旧配置为 `Xime.config.xml.v57bak`。
"""
import io
import os
import re
import shutil
from xml.sax.saxutils import escape, unescape

ROOT = "C:/Users/XJH/WorkBuddy/2026-10-01-11-40-37/Xime"
SRC = ROOT + "/dist/net48"
OLD_CFG = "C:/X输入法/net48/Xime.config.xml"
DESK = "C:/Users/XJH/Desktop/X输入法测试_v58"
INST = "C:/X输入法/net48"

FILES = ("Xime.dll", "Xime.Core.dll", "AutoLoad.lsp", "安装说明.txt")


def read(p):
    return io.open(p, encoding="utf-8-sig").read()


def field(t, key):
    m = re.search(r"<%s>(.*?)</%s>" % (key, key), t, re.S)
    return m.group(1).strip() if m else None


# ── 1) 从旧配置里取出需要沿用的值 ─────────────────────────────
old = read(OLD_CFG)
enabled = field(old, "Enabled") or "true"
enabled_user = field(old, "EnabledByUser") or "false"
workmode = field(old, "WorkMode") or "IgnoreIme"
weasel = field(old, "UseWeaselCli") or "true"
cmds = [unescape(c.strip()) for c in re.findall(r"<Command>(.*?)</Command>", old, re.S)]

print("旧配置: Enabled=%s EnabledByUser=%s WorkMode=%s UseWeaselCli=%s 白名单=%d 条"
      % (enabled, enabled_user, workmode, weasel, len(cmds)))
if len(cmds) < 100:
    raise SystemExit("★ 白名单只读到 %d 条，明显不对 —— 终止，避免覆盖用户资产。" % len(cmds))

# ── 2) 按 v58 Save() 的格式重建配置 ───────────────────────────
L = []
A = L.append
A('<?xml version="1.0" encoding="utf-8"?>')
A('<!-- X输入法 配置文件。修改后重启 AutoCAD 或重新执行 xime 命令生效。 -->')
A('<Xime>')
A('  <!-- 配置格式版本：仅供人工识别，不参与任何判断，请勿手工修改 -->')
A('  <ConfigVersion>3</ConfigVersion>')
A('  <!-- 是否启用。仅当 <EnabledByUser>true</EnabledByUser> 时本项才生效； -->')
A('  <!-- 否则（用户从未手动开关过）加载时一律启用。 -->')
A('  <Enabled>%s</Enabled>' % enabled)
A('  <!-- 用户是否曾显式开关过：true 后加载时不再覆盖其选择（永远尊重） -->')
A('  <EnabledByUser>%s</EnabledByUser>' % enabled_user)
A('  <WorkMode>%s</WorkMode>' % workmode)
A('  <!-- 命令执行期间（选项/二级提示）按键的投递方式： -->')
A('  <!--   PassThrough = 完全放行物理键（**v58 起的默认**）。 -->')
A('  <!--                 第三方命令（天正 RYBZ 的 A/E/D/F/X/Y）读的是**原始按键**， -->')
A('  <!--                 只有物理键才认；合成输入一律不认。 -->')
A('  <!--                 代价：中文输入法会同时收到物理键、弹出候选窗 —— -->')
A('  <!--                 那是你自己的输入法在响应，插件本身绝不碰输入法状态。 -->')
A('  <!--   PostKey     = 吞键后用窗口消息投递，不进输入队列 → 输入法绝对看不到、 -->')
A('  <!--                 不弹候选窗；若该命令因此没反应，就退回 PassThrough（2）。 -->')
A('  <!--   ReplayVk    = 吞键后用真实 VK + 扫描码重放。输入法仍可能弹候选窗， -->')
A('  <!--                 且实测对天正 RYBZ **无效**（合成输入不被认）。 -->')
A('  <!-- 现场可用命令 XIMEKEY 0|1|2 即时切换，无需重启。 -->')
A('  <CommandKeyMode>PassThrough</CommandKeyMode>')
A('  <!-- v58：v55 那个「命令期间强制英文」开关已整体撤除。 -->')
A('  <!--   「无视输入法模式」**绝不修改你的输入法状态**：它的英文输入靠钩子 -->')
A('  <!--   把按键直接投递给命令栏实现（绕过输入法），而不是去切输入法。 -->')
A('  <!--   命令期间选项字母的可用性由上面的 <CommandKeyMode> 负责。 -->')
A('  <!-- v56：切输入法时优先用**小狼毫自己的命令行接口**，而不是盲按 Shift（默认 true）。 -->')
A('  <!--   为什么：Shift 是翻转键，要靠"知道当前态"才切得对；小狼毫走 TSF， -->')
A('  <!--   IMM32 读不回真实中英态，盲按会不会切对纯属碰运气（实机已钉死会失败， -->')
A('  <!--   而且失败还会留下假记录，导致整个会话再也切不动）。 -->')
A('  <!--   小狼毫 0.16.0+ 提供：WeaselServer.exe /ascii（英文）、/nascii（中文）， -->')
A('  <!--   是**幂等设值**而非翻转，不依赖焦点窗口，也不需要回读确认。 -->')
A('  <!--   找不到 WeaselServer.exe 时会自动退回真实 Shift，非小狼毫用户不受影响。 -->')
A('  <!--   关掉它（false）→ 行为回到 v55（仅真实 Shift）。 -->')
A('  <UseWeaselCli>%s</UseWeaselCli>' % weasel)
A('  <!-- 执行完毕后需要输入中文的命令白名单（小写，不含前导横线） -->')
A('  <ChineseCommands>')
for c in cmds:
    A('    <Command>%s</Command>' % escape(c))
A('  </ChineseCommands>')
A('</Xime>')
new_cfg = "\r\n".join(L) + "\r\n"


def deploy(dst, backup=None):
    if not os.path.isdir(dst):
        os.makedirs(dst)
    for f in FILES:
        shutil.copy2(os.path.join(SRC, f), os.path.join(dst, f))
    p = os.path.join(dst, "Xime.config.xml")
    if backup and os.path.exists(p):
        b = os.path.join(dst, backup)
        if not os.path.exists(b):
            shutil.copy2(p, b)
            print("  已备份旧配置 →", backup)
    io.open(p, "w", encoding="utf-8", newline="").write(new_cfg)
    print("  已写入", dst)


deploy(DESK)
deploy(INST, backup="Xime.config.xml.v57bak")

# ── 3) 复核 ──────────────────────────────────────────────────
print("")
for d in (DESK, INST):
    print(d)
    for f in sorted(os.listdir(d)):
        print("   %-28s %d 字节" % (f, os.path.getsize(os.path.join(d, f))))
    t = read(os.path.join(d, "Xime.config.xml"))
    print("   → CommandKeyMode =", field(t, "CommandKeyMode"))
    print("   → ForceEnglishDuringCommand 残留 =",
          ("有（不该有！）" if "ForceEnglishDuringCommand>" in t else "无 ✓"))
    print("   → 白名单 =", len(re.findall(r"<Command>", t)), "条")
print("")
print("部署完成。")
