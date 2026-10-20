# -*- coding: utf-8 -*-
"""
X输入法 部署目录「版本指纹」核对（v43 起）

用途：拿到一个桌面上的 X输入法测试_vNN 目录，判断它到底是哪一版、装了哪些修改。
      —— 这原本是每轮交付都要手写一遍的临时代码，固化下来省得每次重造。

用法：
    python build/probe_version.py                  # 扫描桌面所有 X输入法测试_v* 目录
    python build/probe_version.py <插件目录> [...]   # 只查指定目录

--------------------------------------------------------------------------------
★ 教训（选"版本特征串"的四条规矩，违反任一条都会误判）：
  ① 必须**唯一**：`登记待补` 这个词 v40 就有，但那是**切换路径**的
     （`切换输入法：… 登记待补`），跟 v42 的「确保中文：… **登记待补**」不是一回事。
  ② 照抄**含符号的原文**：C# 相邻字符串字面量会在编译期折叠 —— 源码
     `"…→ **登记待补**，" + "焦点回到编辑控件后立即补做（"` 在 DLL 里是连成一条
     **带星号**的整串。照抄"去掉星号的人话"会得到假阴性。
  ③ **UTF-16LE 与 UTF-8 各搜一遍**：中文消息字面量在 `#US` 堆（UTF-16），
     类型/方法名在 `#Strings` 堆（UTF-8）。用原始字节 `in` 判断，
     别写状态机解析 UTF-16（长串会被截断，全是假阴性）。
  ④ **扫目录下所有 .dll**：跨层新增的文案会落在另一个程序集
     （引擎侧在 Xime.Core.dll，宿主侧在 Xime.dll）。只扫 Core 会把新版判成"旧版"。
"""

import os
import sys
import glob

# ── 版本 → 该版本**独有**的特征串 ──────────────────────────────────────
# 顺序 = 从新到旧；判定时优先匹配最新的那一档。
VERSIONS = [
    # ★ v1.0.5 = v1.0.4+v64 的全部内容 + **版本号升到 1.0.5**
    #   （`<Version>1.0.5</Version>` / `<FileVersion>1.0.5.0</FileVersion>`，
    #     `AssemblyVersion` 仍刻意恒为 1.0.0.0）。
    #   本档存在的意义只是"别把新产物认成旧版"。
    #   ★ 判定串 = `1.0.5.0` + v64 的两条核心判据（v1.0.4 及更早不会有）。
    ("v1.0.5", [
        "1.0.5.0",                              # ★ FileVersion（版本资源，UTF-16）
        "IsCommandLineFamily",                  # v64：命令栏家族纯判据（含 …Name 变体）
        "focusOnForeignEditBox",                # v64：ShouldSeizeCommandKeyAsChar 的新参数
        "ShouldYieldToForeignEditBox",          # v62：外部编辑框让路判据
    ]),

    # ★ v1.0.4 = v1.0.3+v63 的全部内容 + **版本号升到 1.0.4**
    #   （`<Version>1.0.4</Version>` / `<FileVersion>1.0.4.0</FileVersion>`，
    #     `AssemblyVersion` 仍刻意恒为 1.0.0.0）。行为与 v1.0.3+v63 完全一致，
    #   本档存在的意义只是"别把新产物认成旧版"。
    #   ★ 判定串用 `1.0.4.0`（UTF-16 版本资源里必有），再叠加 v63 的两条核心判据，
    #     避免插件目录里别的第三方 DLL 恰好带 1.0.4.0 时误判。
    ("v1.0.4", [
        "1.0.4.0",                              # ★ FileVersion（版本资源，UTF-16）
        "CAcDynInputWndControl",                # v63：DYN 提示框的内部名
        "IsDynamicInputMarker",                 # v63：DYN 纯判据
        "ShouldYieldToForeignEditBox",          # v62：外部编辑框让路判据
    ]),

    # ★ v1.0.3 + v63 = 修「开启动态输入（DYN）后，命令输入从第 2 个字母起变中文」：
    #   实测（探针 `_research/watch_dyn_input.py` + 引擎日志，AutoCAD 2022）：
    #     20D5A [POPUP] Afx:…:803:…  "CAcDynInputWndControl"  49x22
    #       └─ 30D58 [CHILD] Edit 「l」   ← **原生 Edit**，与真编辑框完全同形
    #   ⇒ v62 的「isEdit && !isCommandLine ⇒ 进文字编辑态」把它判成"在编辑文字"
    #     ⇒ `SwitchToChinese` ⇒ 标志清零 ⇒ 第 2 个字母起放行给输入法（用户症状原话）。
    #   ⇒ 新增纯判据 `AcadTextFrame.IsDynamicInputMarker`（`Afx:…:803:…` / `CAcDynInputWndControl`）
    #     + 链式 `IsDynamicInputWindow`；`ShouldYieldToForeignEditBox` 新增第三个参数
    #     `focusIsDynamicInput`（动态输入框 ⇒ **永不让路**）；`OnFocusObjectChanged`
    #     新增 v63 分支（不进文字编辑态，但仍按命令白名单给文字类命令让路）。
    #   ★ 专有串（v1.0.3+v62 及更早不会有）：
    ("v1.0.3+v63", [
        "CAcDynInputWndControl",                # ★ DYN 提示框的 AutoCAD 内部名
        "IsDynamicInputWindow",                 # ★ 新判据（链式）
        "IsDynamicInputMarker",                 # ★ 新判据（纯函数）
        "焦点在动态输入框（命令输入端，非文字内容）",   # 退出编辑态的 reason 文案（照抄原文）
    ]),
    # ★ v1.0.3 + v62（同日第三个 1.0.3 构建）= 两件事（用户两条要求）：
    #   ① 修「AutoCAD 的 LAYER 命令 → 新建图层 → 改图层名，打不出中文，手动也切不了」：
    #      实测（探针 `_research/watch_layer_edit.py`，AutoCAD 2022）图层特性管理器里那个
    #      **名称编辑框**类名是 `WindowsForms10.EDIT.app.0.1a4f6bb_r57_ad1`
    #      （父链 → `AfxWnd100u "Layer Palette"` → `AfxWnd100u "Layer"` → `AfxControlBar100u`，
    #       独立顶层窗口）。它**不以 "Edit" 开头**，所以 `FocusMonitor.IsEditBoxClass` 一直判它
    #      "不是编辑控件" ⇒ 焦点落在它上面时**不进文字编辑态** ⇒ `_commandLineNeedsEnglish`
    #      保持 true ⇒ 键盘钩子把这个框里敲的每个字母都吞掉、改注入英文字母 ⇒ 中文打不进、
    #      手动切输入法也没用。
    #      ⇒ `FocusMonitor.IsEditBoxClass` 新增 `WindowsForms10.EDIT` / `WindowsForms10.RICHEDIT`；
    #        新增纯判据 `ShouldYieldToForeignEditBox`（是编辑控件且不在命令栏族 ⇒ 钩子让路），
    #        并在按键路径与旁路判据上各接一处（`ImeWindowBypass.ShouldEngage` 新增第 ⑧ 条）；
    #        `OnFocusObjectChanged` 焦点一变就重评估旁路（不再等 500ms 轮询）。
    #   ② 工作模式**仍可切换**（用户随后要求撤回"只允许无视输入法"的锁定）：
    #      面板「切换输入法模式」恢复可选、`XIMEMODE 1` 恢复切换、配置文件照常读写；
    #      但**加载后默认 = 无视输入法模式**保持不变（`_workMode` 字段初值 +
    #      构造函数 + `ResetToFreshInstall()` 三处一致，比裸自动属性更稳 ——
    #      因为枚举里 `SwitchIme = 0` 正好是 CLR 默认值）。
    #   ★ 专有串（v1.0.3+v61 及更早不会有）：
    ("v1.0.3+v62", [
        "ShouldYieldToForeignEditBox",          # ★ 新纯判据（外部编辑框让路）
        "焦点在外部文本编辑控件上",                # 日志文案（照抄原文）
        "WindowsForms10.RICHEDIT",              # ★ 新增的类名前缀判据（照抄原文）
    ]),
    # ★ v1.0.3 + v61（同日第二个 1.0.3 构建）= 再修一件事：
    #   **切换输入法模式下「命令行必须是英文」的钩子兜底**。
    #   现场（报障用户 AutoCAD 2024 + 搜狗输入法，2026-10-14 14:17 会话日志）：
    #     整场测试跑在**切换输入法模式**下（Disengage 全是该模式措辞、
    #     `需要中文（文字输入）` 4 次、`需要英文（命令栏）` 0 次），
    #     而 `ShouldCaptureKeyCore` 的第一道闸门是
    #     `if (_config.WorkMode != XimeWorkMode.IgnoreIme) return false;`
    #     ⇒ 切换模式下钩子一个键都不接管，v59/v60 的修复在该模式下**是死代码**；
    #     该模式的英文完全押在"真的把输入法切到英文"上，而 v48 已判定这台机器的
    #     中英态通道不可信（回读恒为同一值、英语读数 0 次）⇒ 切换只能靠猜。
    #     视频里同一模式下两个方向都会错：命令行敲 `tt` 弹搜狗候选窗（该英文成中文）、
    #     在位文字编辑器里敲 `doubaoshurufa` 原样是拉丁字母（该中文成英文）。
    #   ⇒ 新增纯判据 `ShouldTakeOverKeys(mode, switchChannelUnusable)`：
    #     通道不可信时，切换模式也退回**钩子直接注入**（与输入法状态无关，确定性成立）。
    #   ★ 专有串（上一个 1.0.3 构建不会有）：
    ("v1.0.3+v61", [
        "ShouldTakeOverKeys",              # ★ 新纯判据（本模式下钩子是否接管按键）
        "兜底已启用（v61）",                 # 日志文案（照抄原文）
        "键盘钩子直接注入",                  # 日志文案（照抄原文）
    ]),
    # ★ v1.0.3 = 两件事：
    #   ① 版本号对外显示 v1.0.3（`<FileVersion>1.0.3.0</FileVersion>`；`AssemblyVersion` 仍恒 1.0.0.0）；
    #   ② 修「AutoCAD 平台 + 豆包输入法（纯 TSF 文本服务，无 IMM32 键盘布局、无 .ime）」下
    #      **命令名 / 二级命令被输入法吃掉变中文**：
    #      · v59「误判自愈」—— 命令栏英文标志被 CAD 的 WPF 浮层（命令自动完成下拉等，
    #        类名 `HwndWrapper`、父链拿不到 `CLI Palette`）误清时，当场按**真实焦点**复核并抢回接管；
    #      · v60「焦点分流」—— 命令期间按下的字母：焦点若在**可组字控件**（命令栏链 / CAD 的
    #        WPF 宿主）上，改走**吞键 + 注入 WM_CHAR**（TSF 按键回调看不到这枚键）；
    #        焦点在绘图区（天正 `acedGrRead` 场景）则**照旧放行物理键**，RYBZ 二级命令行为不变。
    #   ★ 判据/文案专有串（老版本 DLL 不会有）：
    #     `ShouldReclaimEnglishForIdlePrompt` / `ShouldSeizeCommandKeyAsChar`（#Strings）
    #     + 「误判自愈」/「命令期间改注入字符」（#US）。
    ("v1.0.3", [
        "ShouldReclaimEnglishForIdlePrompt",   # ★ 新纯判据（空闲期误判自愈）
        "ShouldSeizeCommandKeyAsChar",         # ★ 新纯判据（命令期间焦点分流）
        "误判自愈：命令栏英文标志曾被清",         # 日志文案（照抄原文）
        "命令期间改注入字符",                    # 日志文案（照抄原文）
    ]),
    # ★ v1.0.2 = 三件事（用户三条要求）：
    #   ① 「卸载」**只清当前这一个 AutoCAD 版本**的自动加载注册项 ——
    #      旧实现遍历 R18.0~R25.2 全版本删，用户在 2022 里卸一次就把 2014 的加载也干掉了
    #      （纯判据 `RegPath.ShouldRemoveOnUninstall`，Xime.Core 里）。
    #   ② 版本号对外显示 v1.0.2（`<FileVersion>1.0.2.0</FileVersion>`；
    #      `AssemblyVersion` **刻意仍为 1.0.0.0** —— 它是 CLR 程序集身份，改了会让
    #      "同一次 AutoCAD 会话里换目录 NETLOAD"变成两套 Core 并存）。
    #   ③ 新增第 4 条按键投递通道 `PostKeyFrame`（`XIMEKEY 3`）= 吞键后把真实按键投给
    #      **前台顶层窗口**；并修掉 `Disengage` 会抹掉"当前命令"的坑
    #      （纯判据 `ShouldClearCommandOnDisengage`）。
    #   ④ ★★ v6：默认工作模式回到 **`IgnoreIme`（无视输入法模式）**（用户明确否决"默认切换
    #      输入法模式"），并**新增「命令期间无视输入法」**（`ImmAssociateContext` 解除 CAD
    #      窗口与输入法上下文的关联）⇒ 无视模式下也能三全（天正认 + 不弹候选窗 + 命令栏干净），
    #      且**不改输入法状态**；新增诊断命令 `XIMEBYPASS`。
    #   ★ 注意：v1.0.2 **仍然保留** `ShouldPassThroughKey` / `ShouldPostToTopLevelWindow`
    #     等旧串，所以只靠"旧串在不在"区分不了版本；必须靠 `XIMEBYPASS` / `ImmAssociateContext`
    #     这类 **v6 独有串**来判。
    ("v1.0.2", [
        "PostKeyFrame",                                     # ★ 新枚举成员名（#Strings）
        "ShouldClearCommandOnDisengage",                    # ★ 新纯判据（Disengage 不许抹命令）
        "ShouldRemoveOnUninstall",                          # ★ 新纯判据（卸载只清当前版本）
        "PostKeyFrame 投给前台顶层窗口（★ 已证伪：天正收不到）",   # 文案（照抄原文）
        "1=投窗口消息给焦点控件 2=放行物理键(默认) 3=投窗口消息给前台顶层窗口",  # XIMEKEY 提示（照抄原文）
        "只接受 0 / 1 / 2 / 3。",                            # XIMEKEY 报错文案（照抄原文）
        "[卸载] 自动加载注册：**只清当前这一个版本**（",         # 卸载落盘日志（照抄原文）
        # ★★ v6 专有（老版本 DLL 不会有）
        "XIMEBYPASS",                                      # ★ 现场验证/开关命令名
        "ImmAssociateContext",                             # ★ 解除窗口-输入法关联的原生 API
        "命令期间无视输入法",                                 # ★ 状态行 / 探针 / 日志口径
    ]),
    # ★ v58 = 两件事，都是**回退 + 归位**：
    #   ① 撤除 v55 的「命令期间强制切英文」（`ForceEnglishDuringCommand` + `XIMEFORCE`）。
    #      用户明确要求：「无视输入法模式不要动我的输入法状态」。该模式的英文输入本来就靠
    #      **钩子把按键直接投递到命令栏**（绕过 IME）实现，v55 加的窄例外破了这条承诺 ⇒ 整条链路删掉。
    #   ② 命令期间按键方式默认由 `ReplayVk` 改回 **`PassThrough`（放行物理键）** —— 即 v25 的做法。
    #      实机证明 ReplayVk（吞键 + SendInput 合成）对天正 RYBZ 无效：
    #      第三方命令读的是**原始按键**通道，不认合成输入。
    #   ★ 注意：v58 **仍然保留** `ShouldAutoSwitchIme` / `ShouldApplySwitchNow`
    #     （它们现在只剩单参数、只对 SwitchIme 模式返回 true），所以那两条**不能**当 v58 独有串。
    ("v58", [
        "ForceEnglish: 仅置标志（无视输入法模式不切换输入法）",   # ★ 新日志文案（照抄原文）
        "ForceEnglish: 切输入法到英文（切换输入法模式）",          # ★ 新日志文案（照抄原文）
        "SwitchToChinese: 切输入法到中文（切换输入法模式）",        # ★ 新日志文案（照抄原文）
        "2=放行物理键(默认)",                                   # ★ 命令提示语改口（v58 起）
        "v58 起的默认",                                        # ★ 写盘注释里点名"v58 起"（进 DLL）
    ]),
    # ★ v57 = ① 幂等直设优先（修「v56 之后仍偶发输出小写 a」）+ ② XIMEAUTO（修「2014 不自动加载」）。
    #   真因①：`/ascii` 是**设值**不是翻转，v56 却把它挂在「`_switchApplied == target` → 无需动作」
    #   **之后** ⇒ 一条在小狼毫上**无法验证**的记忆就能让功能永久静默失效
    #   （现场 13:44:35：RYBZ 期间日志里只有「本模式已置于 English → 无需动作」，一次 CLI 都没走）。
    #   真因②：自动加载**按版本隔离**，而面板「卸载」会清掉**所有版本**的注册项
    #   ⇒ 实测 R19.1（AutoCAD 2014）从来/已被清空，重启后自然不自动加载。
    ("v57", [
        "ShouldPreferIdempotentSet",        # ★ 幂等直设优先判据
        "方式=幂等直设",                     # 成功日志文案（照抄原文）
        "不看当前态",                        # 同一行的说明
        "幂等直设未生效",                     # 桥失败时的"始终记录"文案
        "XIMEAUTO",                         # ★ 新命令名
        "自动加载注册（XIMEAUTO）",           # 命令标题（照抄原文）
        "下次启动【这个版本】的 AutoCAD 时会自动加载本插件",   # 成功文案
    ]),
    # ★ v56 = 切输入法**优先走小狼毫自己的命令行接口**（修"盲按 Shift 对小狼毫不生效"）。
    #   真因：Shift 是翻转键，切得对不对取决于"知道当前态"；小狼毫走 TSF、IMM32 读不回真实态
    #   （铁律 13）⇒ 前提不成立。实机证据：TapShift 结果=True 方式=RealKey → 121ms 后回读仍
    #   Chinese；且那次失败的盲按写下 `_switchApplied = English` 假记录 → 此后全被短路。
    #   修法：`WeaselServer.exe /ascii`(英) `/nascii`(中)，**幂等设值**、不依赖焦点、无需回读。
    ("v56", [
        "WeaselBridge",                     # ★ 桥类型名
        "WeaselServer.exe",                 # ★ 命令行目标进程名
        "XIME_WEASEL_SERVER",               # 环境变量覆盖（照抄原文）
        "/nascii",                          # 中文方向（照抄原文，含斜杠）
        "weasel-",                          # 目录前缀 <root>\Rime\weasel-*
        "UseWeaselCli",                     # ★ 新配置项字段名
        "<UseWeaselCli>",                   # Save 写出的 XML 节点名
        "SwitchImeOnce",                    # ★ 统一入口（原来两处 _ime.SwitchTo 合一）
        "切换输入法（小狼毫命令行直接设值）",    # 成功日志文案（照抄原文）
        "小狼毫命令行桥未生效",                # 桥失败日志文案（照抄原文）
    ]),
    # ★ v55 = 命令执行期间**自动切英文**（修天正「任意布置」二级命令失灵）。
    #   ★ v58 已把这条链路**整体撤除** —— 所以 v58 目录**不会**命中本档（`XIMEFORCE` 等已不存在），
    #     这正是我们要的：本档只用来认"真·v55 目录"。
    ("v55", [
        "ForceEnglishDuringCommand",        # ★ 新配置项字段名（XimeConfig）
        "ShouldAutoSwitchIme",              # ★ 判据①：无视模式要不要真切输入法
        "ShouldEnterTextEditModeForFocus",  # ★ 判据②：命令期间要不要进文字编辑态
        "ShouldApplySwitchNow",             # ★ 判据③（v55-b）：执行层闸门，与请求层同口径
        "当前模式不允许自动切换",              # 执行层放弃时的日志文案
        "XIMEFORCE",                        # ★ 只在 v55 存在的命令名
        "命令执行期间强制英文",                # XIMESTATUS 字段 + 命令文案
        "若你绝不允许插件碰输入法状态",        # XIMEFORCE 说明文案（照抄原文）
    ]),
    # ★ v51 = 撤除 v50 闸门（回归 v49 行为）。它**不含**任何 v50/v52~v54 特征串，
    #   所以判定顺序上放在 v50 之前没有意义 —— 它靠"v50 串全不在 + v49 串全在"
    #   被识别出来（v49 档会把 v51 认成 v49，这是**有意的**：
    #   两者行为等价，报 v49 不会误导排查）。
    # ★ 注意：v52~v54（TSF 只读通道）已**全部撤回**，指纹档一并删除 ——
    #   任何还带 TsfInterop 的目录都是"过期实验版"，不该再按 v52 报版本。
    ("v50", [
        "CanApplySwitchOnTarget",       # 这次切换允不允许打在这个靶点上（v50 主修，纯判据）
        "_switchRequireTextHost",       # 「需要中文」必须打在编辑框上（v50 新增）
        "_pendingSwitchRequireTextHost",# 补做时沿用当初的约束（v50 新增）
        "WaitForTextHostTarget",        # 等编辑框回来的短轮询（v50 新增）
        # ★ 只在 v50 会打印（照抄原文，见规矩②）
        "本次必须打在文字编辑框上，",
        "等不到编辑框 → ",
        "**登记待补**（绝不打在命令栏上",
    ]),
    ("v49", [
        "IsAppliedStateUsableFor",      # 状态记录能否跨窗口用（v49 主修，纯判据）
        "ClearAppliedState",            # _switchApplied 的唯一清除入口（v49 新增）
        "_switchAppliedHwnd",           # 记录"这个状态是在哪个窗口上写下的"
        # ★ 只在 v49 会打印（含全角括号/星号，照抄原文，见规矩②）
        "，但那是在另一个窗口上写下的（记录窗口=",
        "→ **记录作废，重新动手**（不同 IME 上下文不能互认）",
    ]),
    ("v48", [
        "IsReadbackChannelUseless",     # 回读通道是否失效（v48 主修，纯判据）
        "TrackReadbackHealth",          # 通道健康度累计（v48 新增）
        "_readbackChannelWarned",       # "只报一次"的闩锁字段
        "ReadbackChannelMinSamples",    # 最小样本数常量
        "★★★ 回读通道疑似失效（v48）",   # ★ 只在 v48 会打印（含全角括号，照抄原文）
        "本输入法在 IMM32 下**读不出真实中英态**",   # 通道失效警告正文
    ]),
    ("v47", [
        "DecideBlindShiftVerdict",      # 盲切回读判定纯判据（v47 主修）
        "BlindShiftVerdict",            # 判定结果枚举
        "VerifySwitchAfterUnlock",      # 切换后的锁外回读 + 自愈
        "VerifyRestoreAfterUnlock",     # 还原后的锁外回读
        "ReopenRestorePending",         # "还原没落地"重建待还原
        "切换回读校验（",                # 只在 v47 会打印（★ 含全角左括号，照抄原文）
        "**状态记录作废**",              # 自愈的最终手段，只在 v47 出现
    ]),
    ("v46", [
        "ShouldRunTextEditEntry",       # 文字编辑态入口纯判据（v46 主修）
        "_lastTextEditHost",            # "当前编辑控件"字段
        "VerifyBlindShiftLanded",       # 盲切后回读校验
        "AfterBlindShiftProbeMs",       # 回读等待时间常量
        "重新进入文字编辑态（换到新的编辑控件",   # 只在 v46 会打印
    ]),
    ("v45", [
        "TryAppendLogLine",             # 线程安全的日志写入原语
        "日志缺口通报",                  # 丢行计数通报
        "KeyEventSink",                 # 核心→宿主的关键事件出口
        "DebugLogDropped",              # 丢行计数属性
    ]),
    ("v44", [
        "★ v44 撤销证据：输入法已被确认回到 ",   # ApplyImeLeftEnglishResolution
        "ResolveImeLeftEnglishAfterRestore",   # 纯判据
        "ApplyImeLeftEnglishResolution",
        "_imeLeftEnglishWhy",                  # 审计留痕（v44 新增字段）
    ]),
    ("v43", [
        "主机窗口概览（v43",            # XimeEngine.BuildHostSurveyLines
        "[卸载] 复位后配置:",            # XimeCommands（宿主层）
        "[调试日志] ",                  # XimeCommands（宿主层）
        "DescribeState",               # XimeConfig
    ]),
    ("v42", [
        "**登记待补**",                 # RegisterPendingEnsure
        "补做确保中文",                  # TryFlushPendingEnsure
        "记下证据：输入法可能仍停在英文",   # MarkImeLeftEnglish
        "我们留下过英文=",               # ApplyEnsureChinese
        "DecidePendingEnsure",
        "ShouldRestartStoppedEngine",
    ]),
    ("v41", [
        "IsUsableEnsureChineseTarget",
        "WaitForTextEditHost",
    ]),
    ("v40", [
        "SwitchDelayNone",
    ]),
    ("v39", [
        "ShouldRestoreOriginalIme",
        "DoRestoreFlip",
    ]),
]
# ⚠ 只收录到 v39 为止。v38 及更早**故意不收录** ——
#   手上的特征串（如 `DecidePendingFlush`）在 v34~v38 都存在，
#   拿它当"v38 指纹"会把 v34~v37 全部误标成 v38。
#   ★ 宁可说"未收录"，也不给一个**看起来确定**的错误版本号 ——
#     这正是「会撒谎的工具比没有工具更糟」那条教训。


def load_blob(plugin_dir):
    """把目录下所有 .dll 的字节拼在一起（见规矩 ④）。"""
    blob = b""
    n = 0
    for name in sorted(os.listdir(plugin_dir)):
        if not name.lower().endswith(".dll"):
            continue
        try:
            blob += open(os.path.join(plugin_dir, name), "rb").read()
            n += 1
        except IOError:
            pass
    return blob, n


def hit(blob, s):
    """双编码匹配（见规矩 ③）。"""
    return (s.encode("utf-8") in blob) or (s.encode("utf-16-le") in blob)


def judge(plugin_dir):
    """-> (版本, 命中明细[(串, bool)], dll 个数)"""
    blob, n = load_blob(plugin_dir)
    if n == 0:
        return "(目录里没有 .dll)", [], 0

    hit_counts = []
    for ver, markers in VERSIONS:
        detail = [(m, hit(blob, m)) for m in markers]
        hit_counts.append((ver, detail, sum(1 for _, ok in detail if ok)))

    # 从新到旧：整档全中就认它
    for ver, detail, cnt in hit_counts:
        if cnt == len(detail):
            return ver, detail, n

    # 没有整档全中 → 取命中最多的，标"不完整"（通常＝混了旧 dll 或漏拷文件）
    ver, detail, cnt = max(hit_counts, key=lambda r: r[2])
    if cnt == 0:
        return "早期版本（未收录指纹）", detail, n
    return ver + "（不完整）", detail, n


def scan_desktop():
    desktop = os.path.join(os.environ.get("USERPROFILE", ""), "Desktop")
    dirs = sorted(glob.glob(os.path.join(desktop, "X输入法测试_v*")))
    def key(p):
        tail = os.path.basename(p).rsplit("_v", 1)[-1]
        return int(tail) if tail.isdigit() else 0
    return sorted(dirs, key=key)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dirs = args if args else scan_desktop()
    if not dirs:
        print("没找到任何 X输入法测试_v* 目录（也没给参数）。")
        return 1

    print("")
    print("=" * 70)
    print("  X输入法 部署目录「版本指纹」核对")
    print("=" * 70)
    bad = []
    for d in dirs:
        if not os.path.isdir(d):
            print("\n" + d + "  →  ★ 目录不存在")
            bad.append(d)
            continue
        ver, detail, n = judge(d)
        core = os.path.join(d, "Xime.Core.dll")
        acad = os.path.join(d, "Xime.dll")
        print("\n" + os.path.basename(d) + "   →   " + ver + "   (%d 个 dll)" % n)
        for f in ("Xime.Core.dll", "Xime.dll", "Xime.config.xml"):
            p = os.path.join(d, f)
            print("      %-18s %s" % (f, ("%d 字节" % os.path.getsize(p))
                                      if os.path.exists(p) else "★ 缺失"))
        print("      特征串命中：")
        for m, ok in detail:
            print("        %s %s" % ("✓" if ok else "✗", m))
        if "不完整" in ver:
            # 只有"某一档命中一半"才值得警惕：通常是混了旧 dll / 漏拷文件
            bad.append(os.path.basename(d))
            print("      ★ 注意：这一档没有全部命中 —— 可能是混了旧 dll、漏拷文件，")
            print("        也可能确实是更早的版本。请人工确认。")

    print("")
    print("=" * 70)
    if bad:
        print("  需要留意：" + "、".join(bad))
    else:
        print("  全部目录都完整命中某一档，指纹一致。")
    print("=" * 70)
    print("")
    return 0


if __name__ == "__main__":
    sys.exit(main())
