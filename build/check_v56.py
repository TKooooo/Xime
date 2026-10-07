# -*- coding: utf-8 -*-
"""v56 特征串核对。

v56 = **切输入法优先走小狼毫（Rime/Weasel）自己的命令行接口**，不再"盲按 Shift"。

真因（2026-10-06 实机日志钉死）：
  Shift 是**翻转键**，切得对不对完全取决于"我们知道当前是什么态"；
  而小狼毫走 TSF、IMM32 读不回真实中英态（铁律 13）⇒ 这个前提不成立。
  实机证据：`TapShift 结果=True 方式=RealKey` → 121ms 后回读仍是 Chinese；
  且那次**失败**的盲按写下了 `_switchApplied = English` 的假记录，
  此后每一次英文请求都被"已置于 English → 无需动作"短路 ⇒ 用户按 A 被输入法吃掉。

修法：小狼毫 0.16.0+ 提供 `WeaselServer.exe /ascii`（英文）与 `/nascii`（中文），
  是**幂等设值**而非翻转 —— 重发无害、不依赖焦点窗口、不需要回读确认。

核对四层：
  ① v56 新增的**桥 / 判据 / 配置项 / 文案**必须全在；
  ② v55 与 v51 的既有特征串必须仍在；
  ③ v52~v54 的 TSF 通道 + v50 闸门 + v55-b 旧硬闸门必须**全部消失**；
  ④ v56 的**反向承诺**：不许出现"假装成功"这类静默假成功路径。

★ 规矩（见 probe_version.py 顶部注释）：照抄含符号的原文、UTF-16LE/UTF-8 各搜一遍、
  扫目录下所有 .dll（跨层文案会落在 Xime.dll）。
"""
import sys, os

ROOT = r'C:/Users/XJH/WorkBuddy/2026-10-01-11-40-37/Xime/dist/net48'
DLLS = ['Xime.Core.dll', 'Xime.dll']

data = b''
for n in DLLS:
    p = os.path.join(ROOT, n)
    if os.path.exists(p):
        data += open(p, 'rb').read()

V56_NEW = [
    # ── 桥本体（WeaselBridge）──
    'WeaselBridge',                          # 类型名
    'WeaselServer.exe',                      # ★ 命令行目标的进程名（照抄原文）
    'XIME_WEASEL_SERVER',                    # 环境变量覆盖（现场排查用）
    '/nascii',                               # ★ 中文方向（照抄原文，含斜杠）
    'weasel-',                               # 目录前缀（用于扫 <root>\\Rime\\weasel-*）
    # ── 达成方式（ImeController）──
    'WeaselCli',                             # ImeSwitchManner 新枚举值名
    # ── 配置项（XimeConfig）──
    'UseWeaselCli',                          # 属性名（#Strings 堆）
    '<UseWeaselCli>',                        # Save 写出的 XML 节点名
    '小狼毫命令行',                            # DescribeState / 日志文案
    # ── 引擎接入点（XimeEngine）──
    'SwitchImeOnce',                         # ★ 统一入口（两处调用点合一）
    '切换输入法（小狼毫命令行直接设值）',        # 成功时的日志文案（照抄原文）
    '小狼毫命令行桥未生效',                     # 桥失败时的日志文案（照抄原文）
    '小狼毫命令行桥跳过',                       # ★ 前台不是 AutoCAD 时的日志文案（v56 第二道闸门）
    # ── v56 的两道硬闸门（反"假成功"）──
    'ShouldAttemptCli',                      # ★ 闸门①：服务没跑时不许动手
    'IsServerRunning',                       # ★ 闸门①的实现（查 WeaselServer 常驻服务）
    'WeaselServer 未在运行',                  # ★ 闸门①的拒绝原因（照抄原文）
    'ShouldUseWeaselCli',                    # ★ 闸门②：前台不是 AutoCAD 时不走桥
]

V55_KEEP = [
    'ForceEnglishDuringCommand', '<ForceEnglishDuringCommand>',
    'ShouldAutoSwitchIme', 'ShouldEnterTextEditModeForFocus',
    'ShouldApplySwitchNow', '当前模式不允许自动切换',
    'XIMEFORCE', '命令执行期间强制英文', '若你绝不允许插件碰输入法状态',
]

V51_KEEP = [
    'IsAppliedStateUsableFor', '_switchAppliedHwnd', 'ClearAppliedState',
    '记录作废，重新动手', '不同 IME 上下文不能互认', '切换回读校验',
]

# TSF 只读通道（v52~v54）已**全部撤回**，这些串一条都不许出现。
TSF_GONE = [
    'TsfInterop', 'TsfReadEnabled', 'ReadImeMode', 'IsSwitchDetectable',
    'DiagnosticTsfSummary', 'XIMETSF', 'TsfWatchGate', 'XIMETSFWATCH',
    'CLSID_TF_ThreadMgr', '通道③ TSF',
]

# v50 的「必须打在编辑框上」闸门不许复发。
V50_GONE = [
    'CanApplySwitchOnTarget', '_switchRequireTextHost',
    '_pendingSwitchRequireTextHost', 'WaitForTextHostTarget',
]

# v55-b 的旧硬闸门不许复发（"请求层放行、执行层丢弃"两张皮）。
V55B_GONE = [
    '_config.WorkMode != XimeWorkMode.SwitchIme',
    '!isSwitchMode || !enabled',
]

# v56 反向承诺：不许出现的标识。
V56_FORBIDDEN = [
    # 占位：任何"桥失败却报成功"的静默假成功文案都不许有。
    '小狼毫命令行（已假定成功）',
]


def has(s):
    for enc in ('utf-16-le', 'utf-8'):
        try:
            if s.encode(enc) in data:
                return True
        except Exception:
            pass
    return False


ok = True


def section(title, items, must_exist):
    global ok
    print(title)
    for s in items:
        p = has(s)
        good = p if must_exist else (not p)
        if not good:
            ok = False
        label = ('✓ 在' if p else '✗ 丢了！') if must_exist else ('✗ 仍在！' if p else '✓ 已清除')
        print('  %-44s %s' % (s, label))


section('--- v56 新增特征串（必须全部在）---', V56_NEW, True)
section('--- v55 既有特征串（必须仍在）---', V55_KEEP, True)
section('--- v51 既有特征串（必须仍在）---', V51_KEEP, True)
section('--- v52~v54 TSF 通道（已撤回，必须全部消失）---', TSF_GONE, False)
section('--- v50 闸门（必须全部消失，不许复发）---', V50_GONE, False)
section('--- v55-b 旧硬闸门（必须全部消失，不许复发）---', V55B_GONE, False)
section('--- v56 反向承诺（不许出现的标识）---', V56_FORBIDDEN, False)

print()
print('结论:', '全部符合预期' if ok else '★ 有偏差，需复查')
sys.exit(0 if ok else 1)
