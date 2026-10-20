# -*- coding: utf-8 -*-
"""为每个目标框架包生成安装说明（UTF-8 BOM，Windows 记事本可正常显示）。"""

import io
import os

DIST = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "dist")

#: 对外显示版本号。★ 只此一处定义（模板里用 {version} 占位符引用）——
#: 写死在正文里的话，改版本号时必然漏掉这一份"用户看得最多"的文档。
VERSION = "1.0.5"

TEMPLATE = """============================================================
  X输入法 v{version}  安装说明
============================================================

适用环境
  目标框架：{net}
  AutoCAD ：{cad}
  操作系统：Windows 7 / 10 / 11

安装步骤
  1. 把本目录下的 Xime.dll 和 Xime.Core.dll 放到一个固定文件夹，
     例如：D:\\CAD插件\\X输入法\\
     注意：两个 DLL 必须放在同一目录。

  2. 打开 AutoCAD，输入命令：
        NETLOAD
     在弹窗中选择 Xime.dll，加载插件。

  3. 加载后插件**默认就是启用状态**，无需再做任何设置 ——
     命令行会提示「已自动启用（无视输入法模式）」，
     并告诉你可以用命令 xime 打开设置界面。
     此时在 CAD 窗口里即可直接使用（见下方「工作原理」）。

  4. 自动加载：加载成功时，插件会为**当前这一个 AutoCAD 版本**
     写入自动加载注册表项，下次启动该版本无需再手动 NETLOAD。
     ★ 只注册当前版本，不会影响你机器上安装的其它 AutoCAD 版本；
       其它版本如需使用，各自 NETLOAD 一次即可。
     （也可把 AutoLoad.lsp 加入 AutoCAD 支持路径，实现 LISP 方式加载。）

常用命令
  xime        - 打开设置界面（一级命令）
  XIMESTATUS  - 查看运行状态
  XIMEON      - 快速启用
  XIMEOFF     - 快速关闭
  XIMEDIAG    - 环境诊断
  XIMEREG     - 查看自动加载注册表状态（排查「重启后不自动加载」）
  XIMEAUTO    - 立刻为**当前** AutoCAD 版本写入自动加载注册
  XIMELOGO    - 图标资源诊断（排查「界面没有 logo」）
  XIMEFOCUS   - 查看当前键盘焦点控件
  XIMEMODE    - 查看/切换「工作模式」：0 = 无视输入法模式（★ 默认）/ 1 = 切换输入法模式
  XIMEKEY     - 查看/切换「命令执行期间按键的投递方式」（0/1/2/3）
  XIMEBYPASS  - 命令期间「无视输入法」的现场验证 / 开关
  XIMEPROBE   - 打印"切换动作靶点"与逐通道读数（排查"不起作用"）
  XIMEDEBUG   - 开启/关闭详细日志

工作原理
  · 默认工作模式 = **无视输入法模式**：**完全不修改你的输入法状态**。
  · 在 CAD 窗口内，命令栏始终可直接输入英文命令 —— 靠键盘钩子吞掉物理键并注入字符，
    不依赖"能否读出输入法状态"，因此在任何输入法下都可靠。
  · 执行需要输入文字的命令时（TEXT / MTEXT / 双击文字编辑 / 天正文字命令 dhwz、ttext 等）
    自动进入文字编辑态，可直接输入中文。
  · 在**对话框 / 面板里的输入框**输入文字时（例如图层特性管理器里新建图层、改图层名），
    插件完全让路：按键直接交给你的输入法，中文可正常输入。
  · **开启动态输入（DYN）**时也一样：光标旁那个小输入框里敲的字母仍然是命令，
    插件照旧接管（不会因为它长得像编辑框就把按键交给输入法）；
    ★ 若该框里字母显示为大写，是 AutoCAD 自己的显示习惯，不是插件改的（插件注入的是小写字母，调试日志里能看到 `SendUnicodeInput 'l'`）。
  · 命令执行期间自动「无视输入法」：临时解除 CAD 窗口与输入法上下文的关联，
    物理键照旧直达第三方命令（天正的 A/E/D/F 这类二级提示可用），
    输入法收不到键（**不弹候选窗**），命令结束后立刻恢复 —— 全程**不碰输入法状态**。
  · 开启大写锁定（CapsLock）时不干预；
  · 最小化 CAD 或切到其它程序时自动退出接管，并还原一切。

工作模式（两种，互斥，在 xime 界面里切换；也可用 `XIMEMODE 0` / `XIMEMODE 1`）
  ① 无视输入法模式（★ **当前默认**）
     绝不改变你的输入法状态，任何输入法、任何机器上都可靠。
     命令执行期间自动「无视输入法」：临时解除 CAD 窗口与输入法上下文的关联，
     物理键照旧直达第三方命令，而输入法收不到键（**不弹候选窗**）。

  ② 切换输入法模式（退路；`XIMEMODE 1`）
     真的去切输入法的中英状态（命令期间输入法显示为英文），
     需要能**读出**输入法状态；若你的输入法读不出（TSF 输入法常见），请用默认的①。
     ★ 若探针显示「已解除关联窗口 = 0」，说明旁路在你机器上无从下手 —— 这才需要退到它。

支持的平台
  AutoCAD 2010 ~ 2026
  天正建筑 / 电气 / 结构 / 给排水 / 暖通
    （8.0 / 8.2 / 8.5 / 2014 / T20 系列 / T30 系列）
  探索者系列设计软件
  鸿业暖通

支持的输入法
  Rime 引擎：小狼毫 / 鼠须管 / 小企鹅
  微软拼音（Windows 10 / 11 内置）
  搜狗拼音 / 搜狗五笔
  百度输入法 / 手心输入法 / 微信输入法 / 豆包输入法
  华宇拼音 / QQ 输入法 / 极品五笔

界面 logo
  插件内置 X输入法 logo，开箱即用，不需要额外文件。
  若想换成自己的图，把 XimeLogo.ico / XimeLogo.png 放到**DLL 同一目录**即可；
  删掉这些文件就恢复内置 logo。排查「界面没有 logo」可执行 XIMELOGO。

卸载
  在 xime 界面点击「卸载 X输入法」，会删除**当前这个 AutoCAD 版本**的自动加载注册项
  （本机其它版本如 2014 / 2022 的注册项**原样保留**，不会被牵连），
  并把配置重置为默认（相当于全新安装），然后手动删除 DLL 文件夹即可。
  ★ 想恢复某个版本的自动加载：在那个版本的 AutoCAD 里执行 XIMEAUTO 即可。
  ★ 「关闭」与「卸载」是两件不同的事：
    · 关闭 —— 状态被记住，以后每次加载都是【关闭】，直到你再手动开启（XIMEON）；
    · 卸载 —— 注册项清除 + 配置复位，之后再 NETLOAD 会当作**全新安装**。

注意事项
  1. 首次加载时若杀毒软件拦截，请选择「允许」。
  2. 若重启后未自动加载，可执行 XIMEREG 查看注册表写入情况，
     或查看启动日志：%TEMP%\\xime_startup.log
  3. 若命令行提示「当前为【关闭】状态」，说明你之前在设置界面点过「关闭 X输入法」，
     执行 XIMEON 即可恢复。
  4. 开启大写锁定（CapsLock）时，插件不会干预输入法状态。
  5. 最小化 CAD 窗口或切换到其他程序时，插件会自动退出并恢复本地输入法。
  6. ★ 换用新版本 DLL 时，请**完全退出 AutoCAD 再重新启动**，然后再 NETLOAD。
     AutoCAD 无法卸载已经加载的程序集：同一个 AutoCAD 会话里若先加载过旧版的
     Xime.Core.dll，之后即使换成新目录里的 Xime.dll，仍然会复用那份旧的
     Xime.Core —— 表现为「界面/图标还是老的」。执行 XIMELOGO 可看到实际加载路径。
============================================================
"""

TARGETS = [
    ("AutoCAD2010-2012", ".NET Framework 3.5", "AutoCAD 2010 - 2012"),
    ("AutoCAD2013-2024", ".NET Framework 4.0 - 4.8", "AutoCAD 2013 - 2024"),
    ("AutoCAD2025-2026", ".NET 8.0", "AutoCAD 2025 - 2026"),
]

# ============================================================
#  中望 CAD / 浩辰 CAD 的安装说明
#
#  ★ 为什么不复用上面的 TEMPLATE：
#    那份说明书里有大量 AutoCAD 专有的内容 —— NETLOAD 的用法虽相同，
#    但自动加载走 ApplicationPlugins 包、AutoLoad.lsp（LISP 加载路径）、
#    以及"acad.exe 窗口类名随版本变"这类记录，对中望/浩辰都不成立。
#    这两家的自动加载**只走注册表**（键路径见 HOST_TARGETS），
#    所以另起一份短说明，只讲它们真正需要知道的事。
# ============================================================
HOST_TEMPLATE = """============================================================
  X输入法 v{version}  安装说明（{host_name}）
============================================================

适用环境
  目标框架：{net}
  {host_name} ：{cad}
  操作系统：Windows 10 / 11（64 位）

安装步骤
  1. 把本目录下的 {dll} 和 Xime.Core.dll 放到一个固定文件夹，
     例如：D:\\CAD插件\\X输入法\\{folder}\\
     注意：两个 DLL 必须放在同一目录。

  2. 打开{host_name}，在命令行输入：
        NETLOAD
     在弹窗中选择 {dll}，加载插件。

  3. 加载后插件**默认就是启用状态**，无需再做任何设置 ——
     命令行会提示「已自动启用（无视输入法模式）」，
     并告诉你可以用命令 xime 打开设置界面。
     ★ 默认模式 = **无视输入法模式**（**绝不改变你的输入法状态**；想换成切换模式可用 `XIMEMODE 1`）。

  4. 自动加载：加载成功时，插件会为**当前这一个{host_name}版本**
     写入自动加载注册表项，下次启动无需再手动 NETLOAD。
       写入位置：HKCU\\{reg_path}
     ★ 只注册当前版本，不会影响你机器上安装的其它{host_name}版本；
       其它版本如需使用，各自 NETLOAD 一次即可。
     ★ 排查「重启后不自动加载」：执行 XIMEREG，它会把这几个候选分支
       和实际写入情况全列出来。

常用命令
  （与 AutoCAD 版完全一致）
  xime        - 打开设置界面（一级命令）
  XIMESTATUS  - 查看运行状态
  XIMEON      - 快速启用
  XIMEOFF     - 快速关闭
  XIMEDIAG    - 环境诊断
  XIMEREG     - 查看自动加载注册表状态
  XIMEAUTO    - 立刻为**当前**版本写入自动加载注册
  XIMELOGO    - 图标资源诊断（排查「界面没有 logo」）
  XIMEFOCUS   - 查看当前键盘焦点控件（排查文字编辑相关）
  XIMEMODE    - 查看/切换「工作模式」：0 = 无视输入法模式（★ 默认）/ 1 = 切换输入法模式
  XIMEKEY     - 查看/切换「命令执行期间按键的投递方式」
  XIMEBYPASS  - 命令期间「无视输入法」的现场验证 / 开关
  XIMEPROBE   - 打印"切换动作靶点"与逐通道读数（排查"不起作用"）
  XIMEDEBUG   - 开启/关闭详细日志

与 AutoCAD 版的差异
  1. 宿主的 .NET 版本不同：{host_name} 本版目标是 {net}，
     所以 DLL 不能与 AutoCAD 版混用，必须使用本目录下的这一份。
  2. 自动加载**只走注册表**，不使用 AutoCAD 的 ApplicationPlugins/.bundle 包，
     也没有 AutoLoad.lsp（LISP 自动加载是 AutoCAD 专有的路径）。
  3. 卸载：在 xime 界面点「卸载 X输入法」，会删除**当前这个{host_name}版本**
     的自动加载注册项；想恢复就在那个版本里执行 XIMEAUTO。

已知限制
  1. 插件只在 CAD 主窗口处于前台时工作；切到别的程序会自动退出并恢复输入法。
  2. 开启大写锁定（CapsLock）时，插件不会干预输入法状态。
  3. ★ 换用新版本 DLL 时，请**完全退出{host_name}再重新启动**，然后再 NETLOAD。
     .NET 程序集加载后无法卸载：同一个 CAD 会话里若先加载过旧版的 Xime.Core.dll，
     之后即使换成新目录里的 {dll}，仍然会复用那份旧的 Xime.Core ——
     表现为「界面/图标还是老的」。执行 XIMELOGO 可看到实际加载路径。
============================================================
"""

HOST_TARGETS = [
    dict(
        folder="ZWCAD2026",
        host_name="中望CAD",
        dll="Xime.Zwcad.dll",
        net=".NET Framework 4.0 - 4.8",
        cad="中望 CAD 2020 - 2026",
        # 实际路径形如 HKCU\Software\ZWSOFT\ZWCAD\2026\zh-CN\Applications\XIME
        reg_path="Software\\ZWSOFT\\ZWCAD\\<版本>\\<语言>\\Applications\\XIME",
    ),
    dict(
        folder="GsCAD2026",
        host_name="浩辰CAD",
        dll="Xime.GstarCad.dll",
        net=".NET 8.0",
        cad="浩辰 CAD 2024 - 2026",
        # 实际路径形如 HKCU\Software\Gstarsoft\GstarCAD\R26\zh-CN\Applications\XIME
        reg_path="Software\\Gstarsoft\\GstarCAD\\<系列>\\<语言>\\Applications\\XIME",
    ),
]


def main():
    for folder, net, cad in TARGETS:
        path = os.path.join(DIST, folder, "安装说明.txt")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with io.open(path, "w", encoding="utf-8-sig") as f:
            f.write(TEMPLATE.format(net=net, cad=cad, version=VERSION))
        print("  written: " + os.path.relpath(path, DIST))

    # 中望 / 浩辰：各一份宿主版说明
    for t in HOST_TARGETS:
        path = os.path.join(DIST, t["folder"], "安装说明.txt")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with io.open(path, "w", encoding="utf-8-sig") as f:
            f.write(HOST_TEMPLATE.format(version=VERSION, **t))
        print("  written: " + os.path.relpath(path, DIST))


if __name__ == "__main__":
    main()
