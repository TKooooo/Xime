# -*- coding: utf-8 -*-
"""v51 特征串核对：v50 的字符串必须**全部消失**，v49 的必须**全部还在**。"""
import sys, io, glob, os

DLL = r'C:/Users/XJH/WorkBuddy/2026-10-01-11-40-37/Xime/dist/net48/Xime.Core.dll'
data = open(DLL, 'rb').read()

V50 = [
    'CanApplySwitchOnTarget',
    '_switchRequireTextHost',
    '_pendingSwitchRequireTextHost',
    'WaitForTextHostTarget',
    '本次必须打在文字编辑框上，',
    '等不到编辑框 → ',
    '**登记待补**（绝不打在命令栏上',
]
V49 = [
    'IsAppliedStateUsableFor',
    '_switchAppliedHwnd',
    '记录作废，重新动手',
    '不同 IME 上下文不能互认',
    '切换回读校验',
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
print('--- v50 特征串（必须全部消失）---')
for s in V50:
    p = has(s)
    if p: ok = False
    print('  %-40s %s' % (s, '✗ 仍在！' if p else '✓ 已清除'))

print('--- v49 特征串（必须全部还在）---')
for s in V49:
    p = has(s)
    if not p: ok = False
    print('  %-40s %s' % (s, '✓ 在' if p else '✗ 丢了！'))

print()
print('结论:', '全部符合预期' if ok else '★ 有偏差，需复查')
sys.exit(0 if ok else 1)
