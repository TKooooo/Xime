# -*- coding: utf-8 -*-
"""v58 特征串核对。

v58 = 两件事（都是**回退 + 归位**，不是新功能）：

  ① **撤除 v55 的「命令期间强制切英文」**（`ForceEnglishDuringCommand` + `XIMEFORCE`）。
     用户的明确要求：「无视输入法模式不要动我的输入法状态」。
     该模式的英文输入本来就靠**钩子把按键直接投递到命令栏**实现（绕过 IME），
     而不是去改输入法。v55 加的窄例外把这条承诺破了 ⇒ 整条链路撤掉：
       · `ShouldAutoSwitchIme(mode, forceEnglish)` → `ShouldAutoSwitchIme(mode)`，
         **只有 SwitchIme 模式**才返回 true；
       · `ForceEnglish` / `SwitchToChinese` 恢复 v51 语义（非 SwitchIme 只置/清标志）；
       · `Engage` 不再 `CaptureOriginalIme()`、`Disengage` 不再还原；
       · 配置项 / 命令 / 状态行 / 启动日志里的字段全部删除。

  ② **命令期间按键方式默认改回 `PassThrough`（放行物理键）** —— 即 v25 当时的做法。
     实机证明 `ReplayVk`（吞键 + 重放合成按键）对天正 RYBZ **无效**：
     日志 `命令期间重放真实按键 VK=0x41 结果=True`，命令却毫无动作 ——
     第三方命令读的是**原始按键**通道，不认合成输入。
     代价（已与用户确认）：中文输入法会同时收到物理键、弹出候选窗 ——
     那是**用户自己的输入法**在响应，插件本身绝不修改输入法状态。

核对五层：
  ① v58 的**新文案 / 判据**必须全在；
  ② v57 / v56 / v51 的既有特征串必须仍在；
  ③ v55 的强制英文（配置项 / 命令 / 文案）必须**全部消失**；
  ④ v52~v54 TSF 通道 + v50 闸门必须**全部消失**；
  ⑤ 反向承诺：不许再出现"无视模式下命令期间强制英文"这类文案。

★ 规矩（见 probe_version.py 顶部注释）：照抄含符号的原文、UTF-16LE/UTF-8 各搜一遍、
  扫目录下所有 .dll（跨层文案会落在 Xime.dll）。
★ 注意：**注释里的文字不进 DLL** —— 只挑真正会进二进制字符串表的原文。
"""
import sys, os

ROOT = r'C:/Users/XJH/WorkBuddy/2026-10-01-11-40-37/Xime/dist/net48'
DLLS = ['Xime.Core.dll', 'Xime.dll']

data = b''
for n in DLLS:
    p = os.path.join(ROOT, n)
    if os.path.exists(p):
        data += open(p, 'rb').read()

V58_NEW = [
    # ── ① 无视模式不再碰输入法（ForceEnglish / SwitchToChinese 的日志文案）──
    'ForceEnglish: 仅置标志（无视输入法模式不切换输入法）',
    'ForceEnglish: 切输入法到英文（切换输入法模式）',
    'SwitchToChinese: 切输入法到中文（切换输入法模式）',
    # ── ② 命令期间默认 PassThrough ──
    '2=放行物理键(默认)',
    'PassThrough = 完全放行物理键',
    # ── ③ 配置文件里明说 v55 那项已撤除（写盘文案，进 DLL）──
    'v58：v55 那个「命令期间强制英文」开关已整体撤除',
]

# v57 的关键特征串必须仍在（幂等直设 / XIMEAUTO 都没动）
V57_KEEP = [
    'ShouldPreferIdempotentSet', '方式=幂等直设', '不看当前态', '幂等直设未生效',
    'XIMEAUTO', '自动加载注册（XIMEAUTO）',
]

# v56 的全部特征串必须仍在。
V56_KEEP = [
    'WeaselBridge', 'WeaselServer.exe', 'XIME_WEASEL_SERVER', '/nascii',
    'weasel-', 'WeaselCli', 'UseWeaselCli', '<UseWeaselCli>', '小狼毫命令行',
    'SwitchImeOnce', '切换输入法（小狼毫命令行直接设值）',
    '小狼毫命令行桥未生效', '小狼毫命令行桥跳过',
    'ShouldAttemptCli', 'IsServerRunning', 'WeaselServer 未在运行',
    'ShouldUseWeaselCli',
]

# v55 里**该留的**（v24/v26 的成果，与"强制切英文"无关）必须仍在。
V55_KEEP = [
    'ShouldAutoSwitchIme', 'ShouldEnterTextEditModeForFocus',
    'ShouldApplySwitchNow', '当前模式不允许自动切换',
    'v55 闸门',
]

V51_KEEP = [
    'IsAppliedStateUsableFor', '_switchAppliedHwnd', 'ClearAppliedState',
    '记录作废，重新动手', '不同 IME 上下文不能互认', '切换回读校验',
]

# ★ v58 撤除清单：v55 的「命令期间强制英文」一个残留都不许有。
V55_FORCE_GONE = [
    'ForceEnglishDuringCommand', '<ForceEnglishDuringCommand>',
    'XIMEFORCE', '命令执行期间强制英文',
    '无视模式下的命令期间强制英文',
    '若你绝不允许插件碰输入法状态',
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

# v56 / v57 的反向承诺。
V56_FORBIDDEN = [
    '小狼毫命令行（已假定成功）',
]
V57_FORBIDDEN = [
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
        print('  %-50s %s' % (s, label))


section('--- v58 新增（必须全部在）---', V58_NEW, True)
section('--- v57 既有（必须仍在）---', V57_KEEP, True)
section('--- v56 既有（必须仍在）---', V56_KEEP, True)
section('--- v55 该留的（必须仍在）---', V55_KEEP, True)
section('--- v51 既有（必须仍在）---', V51_KEEP, True)
section('--- ★ v55 的「命令期间强制英文」（已撤除，必须全部消失）---', V55_FORCE_GONE, False)
section('--- v52~v54 TSF 通道（已撤回，必须全部消失）---', TSF_GONE, False)
section('--- v50 闸门（必须全部消失，不许复发）---', V50_GONE, False)
section('--- v56/v57 反向承诺（不许出现的标识）---', V56_FORBIDDEN + V57_FORBIDDEN, False)

print()
print('结论:', '全部符合预期' if ok else '★ 有偏差，需复查')
sys.exit(0 if ok else 1)
