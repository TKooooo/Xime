# -*- coding: utf-8 -*-
"""v58 负向对照工具。

用法：
  python _negctl_v58.py inject1     把 ShouldAutoSwitchIme 改成 `return true;`
                                    （变成"任何模式都允许真的切输入法" ——
                                      也就是"无视模式又会动用户输入法"）
  python _negctl_v58.py inject2     改成 `return false;`
                                    （变成"连切换输入法模式都不切了"）
  python _negctl_v58.py restore     还原原始实现
  python _negctl_v58.py show        打印当前实现

预期（见 MEMORY 验证方法论第 5 条）：
  inject1 → 恰好 **红 3**；inject2 → 恰好 **红 3**。
  两处都只该打中"无视模式必须禁止切换"这一侧的断言；
  ④ 的"逐格一致"哨兵应当**仍然绿**（请求层与执行层同时被改坏，口径依旧一致）

★ 还原必须 `touch` 源文件（见 MEMORY 验证方法论第 6 条）：
  仅靠写回字节会让 mtime 倒退，MSBuild 增量判断会跳过重编译，
  于是跑的还是注入版 —— 表现为"明明还原了、测试却还红"。
"""
import io
import os
import sys

SRC = r'C:/Users/XJH/WorkBuddy/2026-10-01-11-40-37/Xime/src/Xime.Core/XimeEngine.cs'
BAK = SRC + '.v58bak'

ORIG = """            return mode == XimeWorkMode.SwitchIme;"""

INJECT = {
    'inject1': "            return true;    // ★ v58 负向对照：任何模式都允许切输入法",
    'inject2': "            return false;   // ★ v58 负向对照：任何模式都不切输入法",
}


def read():
    with io.open(SRC, 'r', encoding='utf-8-sig') as f:
        return f.read()


def write(text, touch=True):
    with io.open(SRC, 'w', encoding='utf-8-sig', newline='') as f:
        f.write(text)
    if touch:
        os.utime(SRC, None)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else 'show'

    if mode == 'restore':
        if not os.path.exists(BAK):
            print('★ 没有备份可还原'); return 1
        with io.open(BAK, 'r', encoding='utf-8-sig') as f:
            write(f.read())
        print('已还原（并已 touch 强制重编译）')
        return 0

    if mode == 'show':
        for line in read().splitlines():
            if 'return mode == XimeWorkMode.SwitchIme' in line or '负向对照：' in line:
                print(repr(line))
        return 0

    if mode not in INJECT:
        print(__doc__); return 1

    text = read()
    if ORIG not in text:
        print('★ 未找到原始实现 —— 请先 restore'); return 1

    if not os.path.exists(BAK):
        with io.open(BAK, 'w', encoding='utf-8-sig', newline='') as f:
            f.write(text)
    write(text.replace(ORIG, INJECT[mode]))
    print('已注入 ' + mode + '（并已 touch）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
