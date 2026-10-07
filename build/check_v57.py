# -*- coding: utf-8 -*-
"""v57 特征串核对。

v57 = 两件事：
  ① **幂等直设优先** —— 小狼毫命令行桥可用时，越过 `_switchApplied`（可能为假的）记忆
     与探测，直接下发 `/ascii` / `/nascii`。修「v56 之后仍偶发输出小写 a」。
     ★ 真因：`/ascii` 是**设值**不是翻转，v56 却把它挂在
       「`_switchApplied == target` → 无需动作」**之后** ——
       一条在小狼毫上**无法验证**的记忆就能让功能永久静默失效
       （现场 13:44:35：RYBZ 期间日志里只有「本模式已置于 English → 无需动作」，
       一次 CLI 都没走，用户按 A 照样被中文输入法吃掉）。
  ② **XIMEAUTO** —— 为当前 AutoCAD 版本一键写入自动加载注册。
     修「AutoCAD 2014 重启电脑后不会自动加载」（实测 R19.1 从未被注册过；
     根因是自动加载按版本隔离 + 面板「卸载」会清掉**所有版本**的注册项）。

核对五层：
  ① v57 新增的**判据 / 文案 / 命令**必须全在；
  ② v56/v55/v51 的既有特征串必须仍在；
  ③ v52~v54 的 TSF 通道 + v50 闸门 + v55-b 旧硬闸门必须**全部消失**；
  ④ 反向承诺：不许出现"假装成功"这类静默假成功路径；
  ⑤ v57 的反向承诺：不许把"幂等直设"退回成"先看记录再决定"。

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

V57_NEW = [
    # ── ① 幂等直设优先（XimeEngine）──
    'ShouldPreferIdempotentSet',              # ★ 纯判据名
    '方式=幂等直设',                           # ★ 成功时的日志文案（照抄原文）
    '不看当前态',                              # ★ 同一行的说明（强调"不需要知道当前态"）
    '幂等直设未生效',                          # ★ 桥这一趟失败时的"始终记录"文案
    # ── ② XIMEAUTO（XimeCommands）──
    'XIMEAUTO',                              # 命令名（AttributeCommandMethod 里的字符串）
    '自动加载注册（XIMEAUTO）',                # 命令标题（照抄原文）
    '下次启动【这个版本】的 AutoCAD 时会自动加载本插件',   # 成功文案
    '在那个版本的 AutoCAD 里执行',             # 卸载提示里指出的补救路径
]

# v56 的全部特征串必须仍在（v57 是在它之上的修正，不是替换）。
V56_KEEP = [
    'WeaselBridge', 'WeaselServer.exe', 'XIME_WEASEL_SERVER', '/nascii',
    'weasel-', 'WeaselCli', 'UseWeaselCli', '<UseWeaselCli>', '小狼毫命令行',
    'SwitchImeOnce', '切换输入法（小狼毫命令行直接设值）',
    '小狼毫命令行桥未生效', '小狼毫命令行桥跳过',
    'ShouldAttemptCli', 'IsServerRunning', 'WeaselServer 未在运行',
    'ShouldUseWeaselCli',
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

# v56 反向承诺：不许"桥失败却报成功"的静默假成功文案。
V56_FORBIDDEN = [
    '小狼毫命令行（已假定成功）',
]

# v57 反向承诺：幂等直设不许再被"状态记忆"挡住。
V57_FORBIDDEN = [
    # 若有人把幂等直设挪回「记录短路之后」，会出现这样的文案组合 —— 它表示
    # "明明桥可用、却因为一条记忆而一个动作都不做"。
    '已置于 English → 无需动作（幂等直设跳过）',
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
        print('  %-46s %s' % (s, label))


section('--- v57 新增特征串（必须全部在）---', V57_NEW, True)
section('--- v56 既有特征串（必须仍在）---', V56_KEEP, True)
section('--- v55 既有特征串（必须仍在）---', V55_KEEP, True)
section('--- v51 既有特征串（必须仍在）---', V51_KEEP, True)
section('--- v52~v54 TSF 通道（已撤回，必须全部消失）---', TSF_GONE, False)
section('--- v50 闸门（必须全部消失，不许复发）---', V50_GONE, False)
section('--- v55-b 旧硬闸门（必须全部消失，不许复发）---', V55B_GONE, False)
section('--- v56 反向承诺（不许出现的标识）---', V56_FORBIDDEN, False)
section('--- v57 反向承诺（不许退回"先看记忆再决定"）---', V57_FORBIDDEN, False)

print()
print('结论:', '全部符合预期' if ok else '★ 有偏差，需复查')
sys.exit(0 if ok else 1)
