# -*- coding: utf-8 -*-
"""v1.0.2 交付前自查：产物、版本资源、关键特征串（正/反两面）。

用法： python build/check_v102.py

★ 为什么"反证"和"正证"一样重要：这一版有三处是**撤除/收窄**语义
  （卸载不再动其它版本、Disengage 不再抹命令、旧实现删除），
  只看"新串在不在"会漏掉"旧行为还在"这种情况 —— 所以每条都要有反证。
"""
import hashlib
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, "dist")
TFMS = ["net35", "net48", "net8"]

ok_all = True


def say(ok, msg):
    global ok_all
    print(("  [✓] " if ok else "  [✗] ") + msg)
    if not ok:
        ok_all = False


def blob_of(folder):
    blob = b""
    for name in sorted(os.listdir(folder)):
        if name.lower().endswith(".dll"):
            blob += open(os.path.join(folder, name), "rb").read()
    return blob


def hit(blob, s):
    return (s.encode("utf-8") in blob) or (s.encode("utf-16-le") in blob)


def main():
    print("")
    print("=" * 62)
    print("  X输入法 v1.0.2 交付前自查")
    print("=" * 62)

    # ---------- 1) 三套产物 ----------
    print("\n[1] 三套 TFM 产物")
    md5s = {}
    for t in TFMS:
        d = os.path.join(DIST, t)
        for f in ("Xime.dll", "Xime.Core.dll", "安装说明.txt"):
            say(os.path.exists(os.path.join(d, f)), "%s/%s" % (t, f))
        h = hashlib.md5(open(os.path.join(d, "Xime.dll"), "rb").read()).hexdigest()
        md5s[t] = h
    say(len(set(md5s.values())) == 3,
        "三套 Xime.dll 的 md5 互不相同（说明分别按各自 TFM 编译）: " +
        ", ".join("%s=%s" % (k, v[:8]) for k, v in md5s.items()))

    # ---------- 2) 版本资源 ----------
    # ★ 这里必须用 hit()（双编码）而不是裸 `in`：
    #   程序集元数据里的版本串是 **UTF-16**（实测 `1.0.0.0` 只在 UTF-16 下可见），
    #   裸字节搜 UTF-8 会得到"没有"的假阴性 —— 工具撒谎比没工具更糟。
    print("\n[2] 版本资源（★ AssemblyVersion 必须仍是 1.0.0.0）")
    for t in TFMS:
        d = os.path.join(DIST, t)
        raw = open(os.path.join(d, "Xime.dll"), "rb").read()
        say(hit(raw, "1.0.2.0"), "%s Xime.dll 含 FileVersion 1.0.2.0" % t)
        say(hit(raw, "1.0.0.0"), "%s Xime.dll 仍含 AssemblyVersion 1.0.0.0（刻意保留）" % t)
    print("      注：AssemblyVersion 的「精确值」由 XimeTest config 断言")
    print("          （typeof(XimeConfig).Assembly.GetName().Version == 1.0.0.0），")
    print("          字节扫描只用来捕捉「整份元数据被换掉」这类粗错。")

    # ---------- 3) 特征串（正/反） ----------
    print("\n[3] 关键特征串")
    b48 = blob_of(os.path.join(DIST, "net48"))

    must_have = [
        ("PostKeyFrame", "第 4 档枚举成员（#Strings）"),
        ("ShouldRemoveOnUninstall", "卸载范围纯判据"),
        ("ShouldClearCommandOnDisengage", "Disengage 不抹命令的纯判据"),
        ("ShouldPostToTopLevelWindow", "投顶层窗口的纯判据"),
        ("PassThrough 放行物理键（★ 默认；天正唯一认的档；候选窗由「命令期间无视输入法」消除）", "XIMESTATUS 文案（默认档 = PassThrough）"),
        ("1=投窗口消息给焦点控件 2=放行物理键(默认) 3=投窗口消息给前台顶层窗口", "XIMEKEY 提示（默认档 = 2）"),
        ("PostKey 投给焦点控件（★ 已证伪：天正不认，字母进命令栏）", "PostKey 文案（已现场证伪标注）"),
        ("PostKeyFrame 投给前台顶层窗口（★ 已证伪：天正收不到）", "PostKeyFrame 文案（已现场证伪标注）"),
        ("XIMEMODE", "工作模式切换命令（默认 = 无视输入法模式）"),
        ("默认无视输入法模式", "卸载提示：默认模式口径（v6：用户明确要求）"),
        ("四条合成通道一律不认", "配置模板：解释为何只能靠 PassThrough"),
        ("IgnoreIme   = 无视输入法模式（★ **当前默认**、用户明确要求）", "配置模板：默认模式口径（v6）"),
        ("<BypassImeDuringCommand>", "★ v6：配置模板里必须写出新开关的节点"),
        ("XIMEBYPASS", "★ v6：命令期间「无视输入法」诊断命令"),
        ("命令期间无视输入法", "★ v6：状态行 / 卸载提示 / 探针口径"),
        ("ImmAssociateContext", "★ v6：解除窗口与输入法上下文关联的原生 API"),
        ("ImeWindowBypass", "★ v6：执行器类型名"),
        ("已解除关联窗口", "★ v6：探针必须报出「咬不咬得动」的关键计数"),
        ("IsOwnPluginCommand", "★ v6.1：诊断命令不许触发本机制（自我干扰修复）"),
        ("ShouldHonorManualOverride", "★ v6.1：手动强制也必须过安全闸门（文字编辑态一律否决）"),
        ("手动强制", "★ v6.1：XIMEBYPASS 1 解除后**保持住**，不被空闲自动撤销"),
        ("[旁路诊断]", "★ v6.2：旁路「本该生效却没生效」必须留现场证据（始终落盘，不依赖调试开关）"),
        ("**未能解除关联**", "★ v6.2：旁路诊断文案（一眼看出是哪条判据挡下的）"),
        ("ShouldDiagnoseBypassSkip", "★ v6.3：旁路诊断的判据（引擎没在跑 / 已生效 / 无活动命令 / 本插件命令 / 文字类命令 / 机制关 / 退路模式 ⇒ 一律不记，避免刷屏）"),
        ("[引擎] 命令事件已挂接（CommandWillStart/Ended/Cancelled/Failed）",
         "★ v6.3：加载完成即挂命令事件，并把这件事写进「始终记录的关键事件」通道（自查工具按 [引擎] 抽它）"),
        ("命令事件钩子", "★ v6.3：XIMESTATUS 必须能直接看到钩子挂没挂（「开机后第一条命令整条失效」的唯一可见出口）"),
        ("未挂接（异常 → 命令期间无视输入法会整条失效）", "★ v6.3：钩子没挂上时的明确告警文案"),
        ("从加载那一刻起生效；无需先敲一次本命令", "★ v6.3：钩子已挂时的口径（用户不需要每次开机先敲一遍 XIMESTATUS）"),
        ("[卸载] 自动加载注册：**只清当前这一个版本**（", "卸载落盘日志"),
        ("**只清当前这一个版本**", "卸载文案（用户可见）"),
        ("注册项**原样保留**，不会被牵连。", "卸载文案：其它版本原样保留"),
        ("FallbackVersion", "界面兜底版本号常量（宿主没传时用）"),
        ("ShouldPostToTopLevelWindow", "投顶层窗口判据（保证两档目标真的不同）"),
    ]
    for s, why in must_have:
        say(hit(b48, s), "有：%s   —— %s" % (s, why))

    must_not_have = [
        ("ForceEnglishDuringCommand", "v58 已撤除的配置项，不许回来"),
        ("EnumerateAutoCadApplicationKeys", "旧「遍历所有版本」的卸载实现，必须已删除"),
        ("**所有版本**（2014 / 2022", "旧卸载文案（宣称清所有版本）"),
        ("v1.0.0", "旧版本号字面量（任何一处残留都会自相矛盾）"),
        ("默认切换输入法模式", "★ 已被否决的默认口径（v6 默认 = 无视输入法模式）"),
        ("PostKey 投给焦点控件（输入法看不到）", "旧默认档文案（把 PostKey 说成默认且无警示）"),
        ("SwitchIme (1) ★ **当前默认**", "★ 旧的配置模板口径（SwitchIme 不再是默认）"),
    ]
    for s, why in must_not_have:
        say(not hit(b48, s), "无：%s   —— %s" % (s, why))

    # ---------- 4) 安装说明 ----------
    print("\n[4] 安装说明")
    for t in TFMS:
        p = os.path.join(DIST, t, "安装说明.txt")
        txt = io.open(p, encoding="utf-8-sig").read()
        say("X输入法 v1.0.2" in txt, "%s 标题为 v1.0.2" % t)
        say("原样保留" in txt, "%s 卸载段说明其它版本原样保留" % t)
        say("所有版本**的自动加载注册项" not in txt, "%s 不再宣称清所有版本" % t)

    # ---------- 5) 默认值提醒 ----------
    print("\n[5] 与回归测试的分工（这里不做，避免两处各判一遍）")
    print("      · 「默认 WorkMode == IgnoreIme」「默认 CommandKeyMode == PassThrough」")
    print("        「默认 BypassImeDuringCommand == true」")
    print("        由 XimeTest config 的新装默认 / ⑤-c 断言守")
    print("      · 「旧配置的出厂值迁移链」：ver<5 && IgnoreIme → SwitchIme，")
    print("        随后 ver<6 && SwitchIme → IgnoreIme ⇒ 净效果 = 谁都回不到「切换输入法模式」；")
    print("        用户显式选过的值一律保留。由 XimeTest config 的纯函数迁移断言")
    print("        + v∈{1..5} 端到端迁移断言守")
    print("        （已做负向对照：短路 key 迁移 → 恰好红 4；短路 work 迁移 → 恰好红 15；")
    print("          默认工作模式整体回退 → 恰好红 5）")
    print("      · 「ImeWindowBypass.ShouldEngage 七条判据 + 执行器边界」")
    print("        由 XimeTest config 的 RunImeBypassTest 守")
    print("        （已做负向对照：判据恒 false → 恰好红 1；判据恒 true → 恰好红 7；")
    print("          默认 BypassImeDuringCommand 改 false → 恰好红 5）")
    print("      · ★ v6.2：「记命令名 + 同步旁路」不受『此刻是否已接管』限制 ——")
    print("        现场日志实测：接管状态随用户切窗一秒一组地抖，命令开始恰好落在")
    print("        『未接管』窗口里就会被提前 return 掉，整条命令都没有旁路（候选窗照弹）。")
    print("        旁路判据内部本来就查 _isEngaged，调用方再挡一道纯属重复且会掐死补做机会。")
    print("        守：`[旁路诊断]` 一行（本该生效却没生效时**始终落盘**，不依赖 XIMEDEBUG）")
    print("      · 「卸载只清当前版本 / Disengage 不抹命令 / 第 4 档投顶层窗口」")
    print("        由 XimeTest config 的 v1.0.2 判据块断言，并已做负向对照（_negctl_v102.py）")
    print("      · ★ v6.3：修掉「开机后第一条命令整条没有旁路（= 必须先敲一次 XIMESTATUS）」——")
    print("        根因是 NETLOAD 之后命令事件**根本没挂上**，只有用户敲过插件自己的命令才挂。")
    print("        现在 XimeApplication.Initialize() 末尾直接 EnsureHooked()；")
    print("        并给「命令开始时引擎还没接管」补一次迟到接管、给失效句柄加 IsWindow 校验")
    print("        （失效句柄会留下**假激活态**，让后面整条命令都不再尝试解除关联）。")
    print("        守：XIMESTATUS 的「命令事件钩子: 已挂接」一行 + 启动日志的 `[引擎] …` 行")
    print("        + ShouldDiagnoseBypassSkip 的七条判据断言（已做负向对照）")

    print("")
    print("=" * 62)
    print("  结论：" + ("全部符合预期 ✓" if ok_all else "★ 有不符合项，见上面 [✗]"))
    print("=" * 62)
    print("")
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
