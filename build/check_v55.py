# -*- coding: utf-8 -*-
"""v55 特征串核对。

v55 = **命令执行期间自动切英文**（修天正「任意布置」二级命令失灵）。
根因已钉死：命令执行期间输入法若停在中文态，A/E/D/F/X/Y 会被输入法吃掉，
天正收不到（四种按键投递方式 + 插件关闭，症状全同 ⇒ 与注入通道无关）。

核对四层：
  ① v55 新增的**配置项 / 判据 / 命令名 / 文案**必须全在；
  ② v51 的既有特征串（按窗口记账）必须仍在；
  ③ v54/v52/v53 的 TSF 通道**已撤回** —— 那些特征串必须**全部消失**；
  ④ v55 的**反向承诺**：v50 闸门串（等编辑框）必须一条都没有（不许复发）。

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

V55_NEW = [
    # ── 配置项（XimeConfig）──
    'ForceEnglishDuringCommand',            # 字段名（#Strings 堆）
    '<ForceEnglishDuringCommand>',          # Save 写出的 XML 节点名
    # ── 判据（XimeEngine）──
    'ShouldAutoSwitchIme',                  # ①无视模式要不要真切输入法
    'ShouldEnterTextEditModeForFocus',      # ②命令期间要不要进文字编辑态
    'ShouldApplySwitchNow',                 # ③★ v55-b 执行层闸门（请求层/执行层同口径）
    '当前模式不允许自动切换',                  # 执行层放弃时的日志文案
    # ── 命令（XimeCommands）──
    'XIMEFORCE',                            # ★ 只在 v55 存在的命令名
    '命令执行期间强制英文',                    # XIMESTATUS 字段 + 命令文案
    '若你绝不允许插件碰输入法状态',            # XIMEFORCE 说明文案（照抄原文）
]

V51_KEEP = [
    'IsAppliedStateUsableFor', '_switchAppliedHwnd', 'ClearAppliedState',
    '记录作废，重新动手', '不同 IME 上下文不能互认', '切换回读校验',
]

# ★ v55-b 反向承诺：这两条**旧的硬闸门**不许复发（正是"改了跟没改一样"的根源）。
V55B_GONE = [
    '_config.WorkMode != XimeWorkMode.SwitchIme',   # ApplySwitchIme 里那句 return
    '!isSwitchMode || !enabled',                    # DecidePendingFlush 里的旧 Discard
]

# TSF 只读通道（v52~v54）已**全部撤回**，这些串一条都不许出现。
TSF_GONE = [
    'TsfInterop', 'TsfReadEnabled', 'ReadImeMode', 'IsSwitchDetectable',
    'DiagnosticTsfSummary', 'XIMETSF', 'TsfWatchGate', 'XIMETSFWATCH',
    'CLSID_TF_ThreadMgr', '通道③ TSF',
]

# v50 的「必须打在编辑框上」闸门不许复发（铁律：等不到就不动手 = 失效）。
V50_GONE = [
    'CanApplySwitchOnTarget', '_switchRequireTextHost',
    '_pendingSwitchRequireTextHost', 'WaitForTextHostTarget',
]

# v55 反向承诺：这类标识不许出现。
# ★ 只禁「v55 新引入」的错误做法；**不**禁 ImmSetConversionStatus ——
#   它是 ImeController 里**早已存在**的兜底通道（见铁律 6 的说明：
#   对 TSF 报成功但无效 ⇒ 语义降级为未达成，绝不写 _switchApplied）。
#   拿它当"违规标识"会把一个正确的既有实现误判成 bug。
V55_FORBIDDEN = [
    'XIMEFORCE 后自动直设中英态',   # 占位：任何"新引入的直设捷径"都不许有
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
print('--- v55 新增特征串（必须全部在）---')
for s in V55_NEW:
    p = has(s)
    if not p:
        ok = False
    print('  %-40s %s' % (s, '✓ 在' if p else '✗ 丢了！'))

print('--- v51 既有特征串（必须仍在）---')
for s in V51_KEEP:
    p = has(s)
    if not p:
        ok = False
    print('  %-40s %s' % (s, '✓ 在' if p else '✗ 丢了！'))

print('--- v52~v54 TSF 通道（已撤回，必须全部消失）---')
for s in TSF_GONE:
    p = has(s)
    if p:
        ok = False
    print('  %-40s %s' % (s, '✗ 仍在！' if p else '✓ 已清除'))

print('--- v50 闸门（必须全部消失，不许复发）---')
for s in V50_GONE:
    p = has(s)
    if p:
        ok = False
    print('  %-40s %s' % (s, '✗ 仍在！' if p else '✓ 已清除'))

print('--- v55-b 旧硬闸门（必须全部消失，不许复发）---')
for s in V55B_GONE:
    p = has(s)
    if p:
        ok = False
    print('  %-40s %s' % (s, '✗ 仍在！' if p else '✓ 已清除'))

print('--- v55 反向承诺（不许出现的标识）---')
for s in V55_FORBIDDEN:
    p = has(s)
    if p:
        ok = False
    print('  %-40s %s' % (s, '✗ 出现了！' if p else '✓ 未出现'))

print()
print('结论:', '全部符合预期' if ok else '★ 有偏差，需复查')
sys.exit(0 if ok else 1)
