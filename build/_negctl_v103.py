# -*- coding: utf-8 -*-
"""v1.0.3「无感修订版」负向对照工具（一次性跑完：注入 → 重建 → 跑回归 → 还原 → 报红几条）。

用法：
  python _negctl_v103.py all      逐个跑完全部对照并打印结果表
  python _negctl_v103.py restore  还原全部被注入的源文件

对照项与**预期**（预期来自"每条新断言都恰好打中它要守的那条契约"）：
  version      把 FallbackVersion 改回 "1.0.0"                        → 红 1
  uninstall    把 ShouldRemoveOnUninstall 改成 `return true;`
               （= 旧行为：只要能解析出版本分支就删 ⇒ 连别的版本一起删） → 红 2
  disengage    把 ShouldClearCommandOnDisengage 改成 `return true;`      → 红 1
  disengage2   改成 `return false;`（= 引擎停了也不清）                → 红 1
  toplevel     把 ShouldPostToTopLevelWindow 改成 `return true;`         → 红 2
  migrate_key  短路「旧配置 PostKey → PassThrough」的迁移条件
               （= v1.0.3 的做法：只改默认值、不管已部署的旧配置）      → 红 4
  migrate_work ★ 短路出厂值迁移链的**第二级**（v5 出厂值 SwitchIme → v6 出厂值 IgnoreIme）
               —— 这正是"默认从 SwitchIme 改回 IgnoreIme"能生效的关键      → 红 15
               （红多是因为这条契约被"迁移链纯函数 + 端到端 v1~v5 + 配对桩"三处反复钉住；
                 红的**全部**指向同一件事：老配置没被迁到新默认）
  defmode      ★ 把两处默认 WorkMode（构造函数 + ResetToFreshInstall）改回 SwitchIme → 红 5
  bypass_off   ★ 把 ShouldEngage 的最终结论改成 false
               （= 该动手时不动手 ⇒ 候选窗又回来）                       → 红 1
  bypass_danger ★ 把 ShouldEngage 改成无条件 `return true;`               → 红 8
               （★ 这是**危险方向**：文字编辑态/文字类命令下也会解除输入法关联 ⇒
                 用户在标注里打不出中文。7 条"逐条否决"断言 + 1 条逐位翻转断言命中）
  bypassdef    ★ 把默认 BypassImeDuringCommand 改成 false                  → 红 5
               （= 修 bug 的机制默认不开 ⇒ 老用户"改了跟没改一样"）
  owncmd_off   ★★ 把"是不是本插件命令"恒判为 false                        → 红 7
               （= 诊断命令又被当成普通命令去触发本机制 = 刚修掉的自我干扰现场）
  override_danger ★★ 让手动强制（XIMEBYPASS 1）**绕过所有安全闸门**       → 红 3
               （★ 危险方向：用户双击文字要打中文时关联仍是解除状态 ⇒ 打不出中文）
  diag_silent  ★★ v6.3：把 ShouldDiagnoseBypassSkip 改成**恒 false**      → 红 1
               （★ 危险方向：旁路"本该生效却没生效"又回到**查不出来**的状态 ——
                 这正是当初加这条诊断行要解决的病）
  diag_noise   ★★ v6.3：去掉"引擎没在跑就不诊断"那条排除                   → 红 2
               （= 面板点了「卸载」之后，每敲一条命令都刷一行
                 `[旁路诊断] … 接管=False`。刷屏的诊断等于没有诊断）
  fake_active  ★★ v6.3：去掉 Engage 对 root 的 IsWindow 校验               → 红 1
               （= 失效句柄会留下"假激活态"⇒ SyncImeBypass 以为已生效
                 ⇒ **后面整条命令都不再尝试解除关联**，日志上还看不出来）

实测（2026-10-06，v6.1）：13 项全部命中（`bypass_danger` 由 7 升为 8 ——
因为新增了「判据否决⑦ 本插件命令」，它同样属于"不该动手却动手"），
还原后基线复跑 **291 / 0**。
（v6 初版为 11 项：`1/2/1/1/2/4/15/5/1/7/5`，基线 276。）
★ 2026-10-07（v6.3）：新增 `diag_silent` / `diag_noise` / `fake_active` 三项
（对应"开机后第一条命令整条没有旁路、必须先敲一次 XIMESTATUS"那轮的加固），
共 16 项；基线因新增 10 条断言升到 **301 / 0**。

★ 还原必须 `touch` 源文件（MEMORY 验证方法论第 4 条）：
  只写回字节会让 mtime 倒退，MSBuild 增量判断跳过重编译，
  跑的还是注入版 —— 表现为"明明还原了、测试却还红"。
★ 还要先删 `src/Xime.CliTest/bin/Release/net48/Xime.Core.dll`：
  dotnet build 会跳过"把新 Core 拷进 CliTest bin"这一步。
★ restore 走**两趟**：disengage / disengage2 改的是同一个方法体，
  单趟还原会在第一项上误报"找不到原文"。
★ 开跑前有 `selfcheck()`（v6.3 加）：逐项检查「`inj` 不是 `orig` 的子串」与
  「`orig` 锚点在源码里找得到」。这两条任一不满足都会**静默**毁掉整轮对照：
    · `inj` 是 orig 子串 → 还原自检永远假阳，且每趟还原**多复制一行**（真踩过：复制 31 遍）
    · 锚点找不到 → 注入没发生，结果是"红 0 条"这种**看起来通过、实际什么都没测**的假绿
"""
import io
import os
import subprocess
import sys

ROOT = r'C:/Users/XJH/WorkBuddy/2026-10-01-11-40-37/Xime'

TARGETS = {
    'version': {
        'src': ROOT + r'/src/Xime.Core/UI/SettingsForm.cs',
        'orig': 'internal const string FallbackVersion = "1.0.5";',
        'inj': 'internal const string FallbackVersion = "1.0.0";  // ★负向对照',
    },
    'uninstall': {
        'src': ROOT + r'/src/Xime.Core/RegPath.cs',
        'orig': '            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);',
        'inj': '            return true;   // ★负向对照：旧行为=删所有版本',
    },
    'disengage': {
        'src': ROOT + r'/src/Xime.Core/XimeEngine.cs',
        'orig': '''        internal static bool ShouldClearCommandOnDisengage(bool engineStopping)
        {
            return engineStopping;
        }''',
        'inj': '''        internal static bool ShouldClearCommandOnDisengage(bool engineStopping)
        {
            return true;   // ★负向对照：任何退出接管都抹掉"当前命令"
        }''',
    },
    'disengage2': {
        'src': ROOT + r'/src/Xime.Core/XimeEngine.cs',
        'orig': '''        internal static bool ShouldClearCommandOnDisengage(bool engineStopping)
        {
            return engineStopping;
        }''',
        'inj': '''        internal static bool ShouldClearCommandOnDisengage(bool engineStopping)
        {
            return false;  // ★负向对照：引擎停了也不清
        }''',
    },
    'toplevel': {
        'src': ROOT + r'/src/Xime.Core/XimeEngine.cs',
        'orig': '            return mode == XimeConfig.XimeCommandKeyMode.PostKeyFrame;',
        'inj': '            return true;   // ★负向对照：每一档都投顶层窗口',
    },
    # ★ v1.0.2 无感修订版：旧配置的**默认档**迁移（PostKey → PassThrough）
    'migrate_key': {
        'src': ROOT + r'/src/Xime.Core/XimeConfig.cs',
        'orig': '                            if (ver < 5 && parsed == XimeCommandKeyMode.PostKey)',
        'inj': '                            if (false && parsed == XimeCommandKeyMode.PostKey)   // ★负向对照',
    },
    # ★ v6：出厂值迁移链的**第二级**（v5 出厂值 SwitchIme → v6 出厂值 IgnoreIme）。
    #   短路它 = 老用户（配置里写着 v5 出厂值 SwitchIme）永远回不到新默认。
    'migrate_work': {
        'src': ROOT + r'/src/Xime.Core/XimeConfig.cs',
        'orig': '            if (ver < 6 && v == XimeWorkMode.SwitchIme) v = XimeWorkMode.IgnoreIme; // v6 的出厂值',
        'inj': '            if (false && v == XimeWorkMode.SwitchIme) v = XimeWorkMode.IgnoreIme;    // ★负向对照',
    },
    # ★ v6：默认工作模式整体回退（构造函数 + ResetToFreshInstall 两处一起）
    #   ★ 锚点必须带**行首缩进** —— 裸的 `WorkMode = XimeWorkMode.IgnoreIme;` 还会命中
    #     Load() 里注释掉的说明行与迁移目标，改了它就等于把迁移也一起短路，红数会串味。
    'defmode': {
        'src': ROOT + r'/src/Xime.Core/XimeConfig.cs',
        'orig': '\n            WorkMode = XimeWorkMode.IgnoreIme;',
        'inj': '\n            WorkMode = XimeWorkMode.SwitchIme;   // ★负向对照',
    },
    # ★ v6 新机制：命令期间「无视输入法」的判据 —— 该动手时不动手（候选窗又回来）
    'bypass_off': {
        'src': ROOT + r'/src/Xime.Core/ImeWindowBypass.cs',
        'orig': '''            if (commandNeedsChinese) return false;
            return true;
        }''',
        'inj': '''            if (commandNeedsChinese) return false;
            return false;   // ★负向对照：该动手时不动手
        }''',
    },
    # ★ v6 新机制：**危险方向** —— 无条件动手（文字编辑态 / 文字类命令下也会解除关联，
    #   用户在标注里会打不出中文）。这条对照用来证明"六条逐条否决"断言真的有牙齿。
    'bypass_danger': {
        'src': ROOT + r'/src/Xime.Core/ImeWindowBypass.cs',
        'orig': '''            // ① 配置开关（用户可一键关掉整个机制）
            if (!switchOn) return false;''',
        'inj': '''            // ① 配置开关（用户可一键关掉整个机制）
            return true;   // ★负向对照：一律动手（危险方向）
            if (!switchOn) return false;''',
    },
    # ★ v6：默认 BypassImeDuringCommand 被改成 false（修 bug 的机制默认不开）
    'bypassdef': {
        'src': ROOT + r'/src/Xime.Core/XimeConfig.cs',
        'orig': '\n            BypassImeDuringCommand = true;',
        'inj': '\n            BypassImeDuringCommand = false;   // ★负向对照',
    },
    # ★★ v6.1：把"是不是本插件命令"恒判为 false
    #   ⇒ 诊断命令（XIMESTATUS / XIMEBYPASS）又会被当成普通命令去触发本机制
    #     （= 刚修掉的那个自我干扰现场）。
    'owncmd_off': {
        'src': ROOT + r'/src/Xime.Core/ImeWindowBypass.cs',
        'orig': '            return s.StartsWith("xime", StringComparison.OrdinalIgnoreCase);',
        'inj': '            return false;   // ★负向对照：本插件命令识别失效',
    },
    # ★★ v6.1：手动强制（XIMEBYPASS 1）**绕过所有安全闸门**（最危险的方向）
    #   ⇒ 用户双击文字要打中文时关联仍是解除状态 ⇒ 打不出中文。
    'override_danger': {
        'src': ROOT + r'/src/Xime.Core/ImeWindowBypass.cs',
        'orig': '            if (!switchOn) return false;       // 机制关了 ⇒ 强制作废（不许绕过开关）',
        'inj': '''            return true;   // ★负向对照：手动强制绕过全部闸门（危险方向）
            if (!switchOn) return false;       // 机制关了 ⇒ 强制作废（不许绕过开关）''',
    },
    # ★★ v6.3：旁路诊断**永不记录** —— 危险方向：那类"本该生效却没生效"的
    #   静默失效又回到查不出来的状态（这正是加这条诊断行要解决的病）。
    'diag_silent': {
        'src': ROOT + r'/src/Xime.Core/XimeEngine.cs',
        'orig': '            if (!started) return false;              // 引擎没在跑 → 命令事件与我们无关',
        'inj': '            return false;   // ★负向对照：旁路诊断永不记录',
    },
    # ★★ v6.3：去掉"引擎没在跑就不诊断"这条排除
    #   ⇒ 面板点了「卸载」之后每敲一条命令都刷一行 `[旁路诊断] … 接管=False`。
    #   ★ 注意 inj 必须自带 `★负向对照` 标记且**不得是原文的子串** ——
    #     否则 restore_all 的"是否还残留"自检会永远报"未还原"，
    #     而它替换回的 orig 里的那一行又恰好等于 inj ⇒ 每趟还原都**多复制一行**。
    #     （本项第一版就栽在这里：把方法体里的一行复制了 31 遍。）
    'diag_noise': {
        'src': ROOT + r'/src/Xime.Core/XimeEngine.cs',
        'orig': '''            if (!started) return false;              // 引擎没在跑 → 命令事件与我们无关
            if (bypassActive) return false;           // 已经生效，无事可报''',
        'inj': '''            if (bypassActive) return false;           // ★负向对照：不看引擎是否在跑''',
    },
    # ★★ v6.3：去掉 Engage 对 root 的 IsWindow 校验
    #   ⇒ 失效句柄走进"枚举 0 个子孙 + 保存 0 个 HIMC"的分支，却仍置 `_active = true`
    #     ⇒ 留下**假激活态**：SyncImeBypass 以为已经生效 ⇒ 后面整条命令都不再尝试
    #       解除关联（用户侧表现是"命令期间照旧弹候选窗"，日志上什么都看不出来）。
    'fake_active': {
        'src': ROOT + r'/src/Xime.Core/ImeWindowBypass.cs',
        'orig': '            if (!NativeMethods.IsWindow(root)) return 0;',
        'inj': '            // ★负向对照：不校验句柄（会留下假激活态）',
    },
}

ORDER = ['version', 'uninstall', 'disengage', 'disengage2', 'toplevel',
         'migrate_key', 'migrate_work', 'defmode',
         'bypass_off', 'bypass_danger', 'bypassdef',
         'owncmd_off', 'override_danger',
         'diag_silent', 'diag_noise', 'fake_active']


def selfcheck():
    """★ 跑之前先自检 —— 这两类缺陷都会**静默**毁掉整轮对照（本次真踩到）：

    ① `inj` 是 `orig` 的子串（或本身是原文里就有的普通代码行）
       ⇒ `restore_all` 的"是否还残留"自检永远报"未还原"，
         而且它把 `inj` 替换回 `orig` 时**每趟多复制一行**
         （第一版 `diag_noise` 就这样把方法体里的一行复制了 31 遍）。
    ② 某个 `orig` 锚点在源码里找不到
       ⇒ 该注入根本没发生，结果是"红 0 条"这种**看起来通过、实际什么都没测**的假绿。

    所以在开跑前一次性把两件事都干掉：任一条不过就直接退出，绝不带病跑。
    """
    bad = False
    for name in ORDER:
        t = TARGETS[name]
        if t['inj'] in t['orig']:
            print('★ 自检失败 [%s]：inj 是 orig 的子串 ⇒ 还原自检会假阳性，且会复制行' % name)
            bad = True
        text = read(t['src'])
        if t['orig'] not in text:
            print('★ 自检失败 [%s]：源码里找不到 orig 锚点（%s）' % (name, t['src']))
            bad = True
    return not bad


def read(p):
    with io.open(p, 'r', encoding='utf-8-sig') as f:
        return f.read()


def write(p, text):
    with io.open(p, 'w', encoding='utf-8-sig', newline='') as f:
        f.write(text)
    os.utime(p, None)          # ★ 必须 touch，否则 MSBuild 跳过重编译


def restore_all(verbose=True):
    """★ 两趟还原：disengage / disengage2 改的是同一个方法体，
    单趟还原会在先处理的那一项上误报"找不到原文"。"""
    for _ in range(2):
        for name, t in TARGETS.items():
            text = read(t['src'])
            if t['inj'] in text:
                write(t['src'], text.replace(t['inj'], t['orig']))
                if verbose:
                    print('  已还原 ' + name)
    ok = True
    for name, t in TARGETS.items():
        if t['inj'] in read(t['src']):
            print('  ★ ' + name + ' 仍未还原（人工检查）')
            ok = False
    return ok


def build_and_test():
    clitest = ROOT + '/src/Xime.CliTest'
    # ★ 先删 CliTest bin 里的旧 Core —— 否则 dotnet build 不会把新 Core 拷过来
    stale = clitest + '/bin/Release/net48/Xime.Core.dll'
    try:
        if os.path.exists(stale):
            os.remove(stale)
    except OSError:
        pass

    r = subprocess.run(['dotnet', 'build', '-c', 'Release'],
                       cwd=clitest, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if b'error' in r.stdout.lower():
        print(r.stdout.decode('utf-8', 'replace')[-1500:])
        return None
    exe = ROOT + '/src/Xime.CliTest/bin/Release/net48/XimeTest.exe'
    r2 = subprocess.run([exe, 'config'], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out = r2.stdout.decode('utf-8', 'replace')
    reds = [ln.strip() for ln in out.splitlines() if '[★ 失败]' in ln]
    total = [ln.strip() for ln in out.splitlines() if '合计:' in ln]
    return len(reds), (total[-1] if total else '?'), reds


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else 'all'

    if mode == 'restore':
        return 0 if restore_all() else 1

    if mode != 'all':
        print(__doc__)
        return 1

    # ★★ 自检必须先于任何注入 —— 这两类缺陷都会**静默**毁掉整轮对照（本次真踩到）。
    if not selfcheck():
        print('★ 自检未通过：先修好注入表，再跑对照。')
        return 1

    print('===== 先跑一遍基线（应当是 301 / 0） =====')
    base = build_and_test()
    print('   ' + str(base[1]) if base else '   构建失败')
    if not base or base[0] != 0:
        print('★ 基线不绿，先别做负向对照'); return 1

    results = []
    for name in ORDER:
        t = TARGETS[name]
        text = read(t['src'])
        if t['orig'] not in text:
            print('★ ' + name + ' 找不到原文 —— 跳过')
            results.append((name, '?', None))
            continue
        write(t['src'], text.replace(t['orig'], t['inj']))
        r = build_and_test()
        if r is None:
            results.append((name, '构建失败', None))
        else:
            results.append((name, r[0], r[2]))
            print('  ' + name + ' → 红 ' + str(r[0]) + ' 条 | ' + r[1])
            for ln in r[2]:
                print('      ' + (ln[10:] if len(ln) > 10 else ln))
        restore_all(verbose=False)

    print()
    print('===== 还原后复跑基线 =====')
    base2 = build_and_test()
    print('   ' + str(base2[1]) if base2 else '   构建失败')

    print()
    print('===== 负向对照汇总 =====')
    for name, n, _ in results:
        print('  %-13s 红 %s 条' % (name, n))
    return 0


if __name__ == '__main__':
    sys.exit(main())
