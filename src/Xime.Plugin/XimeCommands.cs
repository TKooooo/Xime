using System;
using System.Text;

// ★★ 宿主 API 命名空间 —— 与 XimeApplication.cs 同一套编译符号，说明见那边。
//   一份源码编三种宿主：Autodesk.AutoCAD.* / ZwSoft.ZwCAD.* / Gssoft.Gscad.*
#if HOST_ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Runtime;
#elif HOST_GSTARCAD
using Gssoft.Gscad.ApplicationServices;
using Gssoft.Gscad.Runtime;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
#endif
using Xime.Core;
using Xime.Core.Native;

namespace Xime.Plugin
{
    /// <summary>
    /// X输入法 命令集合。
    ///
    /// 一级命令：xime —— 打开可视化设置界面。
    /// 辅助命令：XIMESTATUS / XIMEON / XIMEOFF / XIMEDIAG / XIMEPROBE /
    ///           XIMEMODE / XIMEKEY /
    ///           <c>XIMEAUTO</c>（v57 新增：为当前 AutoCAD 版本写入自动加载注册）。
    ///           ★ v58：v55 的 <c>XIMEFORCE</c> 已整体撤除 ——
    ///             「无视输入法模式」不再、也绝不允许切换用户输入法状态。
    /// </summary>
    public sealed class XimeCommands
    {
        private static bool _hooked;
        private static readonly object _hookLock = new object();

        /// <summary>
        /// 命令事件钩子是否已经挂上（诊断用，★ v6.3）。
        /// XIMESTATUS 直接报它 —— 这是"开机后第一条命令整条失效"那条根因
        /// 唯一的可见出口，以前只能靠"敲一次 XIMESTATUS 就好了"这类现象反推。
        /// </summary>
        internal static bool Hooked { get { return _hooked; } }

        #region 一级命令

        /// <summary>
        /// 一级命令：xime —— 打开 X输入法 设置界面。
        ///
        /// ★ 命令名从 <c>ime</c> 改为 <c>xime</c>（v27，用户要求）：
        ///   机器上往往同时装着多个 CAD 插件，而 <c>ime</c> 这种三字母通用名
        ///   极易与别人抢注 —— AutoCAD 的同名命令会互相覆盖，
        ///   表现为「执行 ime 打开的是别的插件的界面」或者干脆没反应。
        ///   改成带插件前缀的 <c>xime</c> 后与 XIMEON / XIMEOFF / XIMESTATUS
        ///   等等一脉相承，冲突面大幅缩小。
        ///   **刻意不保留旧名 ime 作为别名** —— 保留就等于没解决冲突。
        /// </summary>
        [CommandMethod("xime", CommandFlags.Modal | CommandFlags.Session)]
        public void ShowSettings()
        {
            try
            {
                EnsureHooked();

                // 设置界面位于 Xime.Core.UI（不依赖 AutoCAD 程序集），
                // 这样预览工具能复用同一份界面代码。宿主相关能力通过委托注入。
                var form = new Xime.Core.UI.XimeSettingsForm(
                    XimeApplication.Config,
                    XimeApplication.Engine,
                    XimeApplication.Version,
                    msg => XimeApplication.SafeWriteMessage(msg),
                    () => UninstallHelper.RemoveAutoLoadRegistration());

                Application.ShowModalDialog(form);

                // ★★ v42：用户刚在面板里点了「卸载」——
                //   此时插件必须**保持停止**。旧实现在这里无条件 Refresh()，
                //   而卸载按钮刚把配置复位成 Enabled=true，于是 Refresh 会把
                //   刚卸载掉的插件又拉起来（旧版还会变成"钩子在、焦点监听停"的
                //   半死状态，无视模式下双击文字打不出中文）。
                //   正确语义：卸载 = 停止 + 清注册 + 配置复位；
                //   之后重新 NETLOAD 才按全新安装（启用 + 无视输入法模式）启动。
                if (form.Uninstalled)
                {
                    // ★ v43：把卸载的前后状态落进**始终记录**的启动日志。
                    //   用户验收标准是"卸载后重新加载必须回到默认（无视输入法模式）"，
                    //   而这条只看最终值判不出来 —— 必须同时留下"卸载前是什么"，
                    //   才能证明改过的脏值确实被抹平了。日志里有了这一对，
                    //   自测工具（xime_check）就能给出机器判定，不用靠肉眼读命令行。
                    XimeApplication.WriteStartupLog("[卸载] 面板点了「卸载 X输入法」");
                    XimeApplication.WriteStartupLog("[卸载] 卸载前配置: "
                        + (form.UninstallSnapshot ?? "(未取到)"));
                    XimeApplication.WriteStartupLog(
                        "[卸载] 复位后配置: " + XimeApplication.Config.DescribeState());
                    XimeApplication.WriteStartupLog(
                        "[卸载] 插件已停止。下一步：完全退出 " + Compat.HostShortName + " 后重新 NETLOAD，"
                        + "那次加载应当报「无视输入法模式」");

                    XimeApplication.WriteStartupLog(
                        "[卸载] 自动加载注册：**只清当前这一个版本**（"
                        + (UninstallHelper.FindCurrentVersionApplicationKey()
                           ?? "(定位不到当前版本 → 未动注册表，其它版本更不会被动)")
                        + "）；其它 CAD 版本一律保留");

                    XimeApplication.SafeWriteMessage(
                        "\n[X输入法] 已卸载：插件已停止，本版本的自动加载注册已清除，配置已复位为全新安装。\r\n"
                        + "          ★ 自动加载注册是**按 " + Compat.HostShortName + " 版本各写一份**的，卸载**只清当前这一个版本**\r\n"
                        + "            （即现在正开着的这个 " + Compat.HostShortName + "）；本机其它版本（如 2014 / 2022）的\r\n"
                        + "            注册项**原样保留**，不会被牵连。\r\n"
                        + "          想让某个版本重新自动加载：在那个版本的 " + Compat.HostShortName + " 里执行\r\n"
                        + "            XIMEAUTO，或 NETLOAD 一次本 DLL。\r\n"
                        + "          重新 NETLOAD 加载本 DLL 会当作全新安装：默认启用 + 默认无视输入法模式。");

                    // 配置的复位与落盘都在卸载流程里做完了，这里不再 Refresh / Save。
                    return;
                }

                if (XimeApplication.Engine != null)
                {
                    XimeApplication.Engine.Refresh();
                    XimeApplication.Config.Save();
                }

                XimeApplication.SafeWriteMessage(
                    "\n[X输入法] 设置已更新：" +
                    (XimeApplication.Config.Enabled ? "已启用" : "已关闭") +
                    " | 模式：" + ModeText(XimeApplication.Config.WorkMode));
            }
            catch (System.Exception ex)
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 打开设置界面失败：" + ex.Message);
            }
        }

        #endregion

        #region 辅助命令

        [CommandMethod("XIMESTATUS", CommandFlags.Modal)]
        public void Status()
        {
            EnsureHooked();
            var e = XimeApplication.Engine;
            if (e == null) { XimeApplication.SafeWriteMessage("\n[X输入法] 引擎未启动。"); return; }

            var frame = Xime.Core.HostTextFrame.Resolve();
            var cur = string.IsNullOrEmpty(e.CurrentCommand) ? "-" : e.CurrentCommand;
            string needCn = "无";
            if (e.CurrentCommand != null)
            {
                var lv = XimeApplication.Config.MatchChinese(e.CurrentCommand);
                if (lv == XimeConfig.ChineseNeed.Exact) needCn = "是（精确命中白名单）";
                else if (lv == XimeConfig.ChineseNeed.Keyword) needCn = "可能（关键词猜测）";
                else needCn = "否";
            }
            XimeApplication.SafeWriteMessage(string.Format(
                "\n[X输入法 v{0}] 启用={1} | 模式={2} | 接管中={3} | 钩子={4} | 命令栏需英文={5} | 文字编辑态={6} | 命令期间按键={7} | 已投递={8} | 已吞键={9} | 命令栏控件={10} | 当前命令={11} | 该命令需中文={12} | 输入法可探测={13} | 进入前状态={14}",
                XimeApplication.Version,
                XimeApplication.Config.Enabled ? "是" : "否",
                ModeText(XimeApplication.Config.WorkMode),
                e.IsEngaged ? "是" : "否",
                e.Hook.IsInstalled ? "已安装" : "未安装",
                e.CommandLineNeedsEnglish ? "是" : "否",
                e.InTextEditMode ? "是" : "否",
                KeyModeText(XimeApplication.Config.CommandKeyMode),
                e.CapturedCount,
                e.SwallowCount,
                frame != IntPtr.Zero ? "0x" + frame.ToInt64().ToString("X") : "未定位",
                cur,
                needCn,
                e.SwitchImeDetectable ? "是" : "否",
                e.SwitchImeOriginalMode));

            // ★ v6：把「命令期间无视输入法」单列一行。
            //   为什么必须单独报：这是默认档（PassThrough）能"不弹候选窗"的**唯一依靠**，
            //   出问题时第一件事就是看它到底有没有生效、咬住了几个窗口。
            //   措辞与 XIMEPROBE 保持一字不差，避免两处口径打架。
            bool bypassOn = XimeApplication.Config.BypassImeDuringCommand;
            int detached = e.ImeBypassDetachedCount;
            XimeApplication.SafeWriteMessage(
                "\n   └ 命令期间无视输入法: " + (bypassOn ? "开" : "关")
                + "   当前生效=" + (e.ImeBypassActive ? "是" : "否")
                + (e.ImeBypassActive && e.ManualImeBypass ? "(手动强制)" : "")
                + "   已解除关联窗口=" + detached
                + (e.ImeBypassActive ? "（物理键直达命令、输入法收不到键 ⇒ 不弹候选窗）" : ""));

            // ★★★ v6.3：**命令事件钩子是否已挂接** —— 这是本轮修掉的那条
            //   "开机后第一条命令整条失效、必须先敲一次 XIMESTATUS 才正常"的根因。
            //   为什么必须单列一行给它：它以前只存在于"用户的口述现象"里
            //   （"敲一次 XIMESTATUS 就好了"），日志上完全看不出来 ——
            //   而这一类**静默失效**正是本项目最贵的一类 bug。
            //   现在：看到"已挂接"就能确认「命令期间无视输入法」从加载那一刻起在工作，
            //   用户**不需要、也不应该**每次开机先敲一遍本命令。
            XimeApplication.SafeWriteMessage(
                "\n   └ 命令事件钩子: " + (Hooked ? "已挂接" : "★ 未挂接（异常 → 命令期间无视输入法会整条失效）")
                + (Hooked
                    ? "   （从加载那一刻起生效；无需先敲一次本命令）"
                    : "   （请完全退出 " + Compat.HostShortName + " 后重新 NETLOAD）"));

            if (!bypassOn)
            {
                XimeApplication.SafeWriteMessage(
                    "\n      ★ 已关掉 ⇒ 命令期间输入法会看到物理键（中文态下弹候选窗）。"
                    + "执行 XIMEBYPASS on 可打开。");
            }
            else if (!e.ImeBypassActive)
            {
                // ★ v6.2：口径与 XIMEBYPASS 的"生效条件（六条全过）"**一字对齐** ——
                //   此前这里只列了四条，两处说法不一致，用户对着看会以为是两种判据。
                XimeApplication.SafeWriteMessage(
                    "\n      ★ 现在没生效（正常。生效条件共六条，全过才动手）："
                    + "\n         ① 工作模式 = 无视输入法模式  ② 引擎已接管  ③ 非本插件命令"
                    + "\n         ④ 命令执行期间  ⑤ 非文字类命令  ⑥ 不在文字编辑态。"
                    + "\n        你现在看到的是「④ 命令执行期间」这条不成立 —— 敲命令前那一刻本来就不该生效。");
                XimeApplication.SafeWriteMessage(
                    "\n        本命令自己**不会**触发它 —— 所以你看到的就是真实状态。"
                    + "想立刻验证它咬不咬得动：XIMEBYPASS 1 → 试 RYBZ → 选块 → 按 A → XIMEBYPASS 0。");
                XimeApplication.SafeWriteMessage(
                    "\n        ★ 若某条命令执行期间**仍然弹候选窗**，去 %TEMP% 目录下的 xime_startup.log 找"
                    + "\n          「[旁路诊断]」行 —— 它会写明被上面哪一条挡下的。");
            }
        }

        /// <summary>命令执行期间按键投递方式的中文说明（XIMESTATUS / XIMEKEY 共用）。</summary>
        internal static string KeyModeText(XimeConfig.XimeCommandKeyMode mode)
        {
            switch (mode)
            {
                case XimeConfig.XimeCommandKeyMode.PostKey:
                    return "PostKey 投给焦点控件（★ 已证伪：天正不认，字母进命令栏）";
                case XimeConfig.XimeCommandKeyMode.PassThrough:
                    return "PassThrough 放行物理键（★ 默认；天正唯一认的档；候选窗由「命令期间无视输入法」消除）";
                case XimeConfig.XimeCommandKeyMode.PostKeyFrame:
                    return "PostKeyFrame 投给前台顶层窗口（★ 已证伪：天正收不到）";
                default:
                    return "ReplayVk 重放真实按键（★ 已证伪：天正不认）";
            }
        }

        /// <summary>
        /// 查看 / 切换「命令执行期间按键的投递方式」。四档都编在同一个 DLL 里，
        /// 现场可以用本命令逐一切换验证，无需换 DLL：
        ///   XIMEKEY        查看当前方式
        ///   XIMEKEY 0      吞键 + 重放真实 VK（★ 已证伪：天正不认，输入法照样看到）
        ///   XIMEKEY 1      吞键 + 投递给**焦点控件**（★ 已证伪：消息变成命令栏文本，
        ///                  天正读原始按键的通道收不到 ⇒ 二级命令毫无反应）
        ///   XIMEKEY 2      ★ **当前默认**：完全放行物理键 —— **天正唯一认的档**。
        ///                  代价：输入法会看到按键、中文态会弹候选窗。
        ///   XIMEKEY 3      吞键 + 投递给**前台顶层窗口**（★ 已证伪：天正收不到）
        /// </summary>
        [CommandMethod("XIMEKEY", CommandFlags.Modal)]
        public void KeyMode()
        {
            EnsureHooked();
            var cfg = XimeApplication.Config;

            string arg = null;
            try
            {
                var res = Application.DocumentManager.MdiActiveDocument.Editor.GetString(
                    "\n[输入法] 命令执行期间按键方式 0=VK重放 1=窗口消息(焦点) 2=放行物理键 3=窗口消息(顶层)（直接回车保持 "
                    + KeyModeText(cfg.CommandKeyMode) + "）: ");
                if (res != null) arg = res.StringResult;
            }
            catch (System.Exception) { }

            if (string.IsNullOrEmpty(arg))
            {
                XimeApplication.SafeWriteMessage(
                    "\n[X输入法] 命令执行期间按键方式 = " + KeyModeText(cfg.CommandKeyMode)
                    + "（" + (int)cfg.CommandKeyMode + "）"
                    +                     "\n  重跑 XIMEKEY 并输入 0/1/2/3 即可切换：0=重放真实按键 "
                    + "1=投窗口消息给焦点控件 2=放行物理键(默认) 3=投窗口消息给前台顶层窗口"
                    + "\n  （若在提示里输入数字没反应，可直接编辑 Xime.config.xml 的 <CommandKeyMode>）");
                return;
            }

            XimeConfig.XimeCommandKeyMode target;
            switch (arg.Trim())
            {
                case "0": target = XimeConfig.XimeCommandKeyMode.ReplayVk; break;
                case "1": target = XimeConfig.XimeCommandKeyMode.PostKey; break;
                case "2": target = XimeConfig.XimeCommandKeyMode.PassThrough; break;
                case "3": target = XimeConfig.XimeCommandKeyMode.PostKeyFrame; break;
                default:
                    XimeApplication.SafeWriteMessage("\n[X输入法] 只接受 0 / 1 / 2 / 3。");
                    return;
            }

            cfg.CommandKeyMode = target;
            cfg.Save();
            if (XimeApplication.Engine != null) XimeApplication.Engine.Refresh();
            XimeApplication.SafeWriteMessage(
                "\n[X输入法] 命令执行期间按键方式已切换为：" + KeyModeText(target)
                + "（已保存，重启后保持）。请回到 CAD 里试一次天正的二级命令。");
        }

        /// <summary>
        /// 查看 / 切换工作模式。两种模式都编在同一个 DLL 里，可当场来回切换验证：
        ///   XIMEMODE        查看当前模式
        ///   XIMEMODE 0      ★ **当前默认**：无视输入法模式（**绝不碰**你的输入法状态；
        ///                   配 XIMEKEY 2 放行物理键 ⇒ 天正二级命令可用，且由
        ///                   <c>BypassImeDuringCommand</c> 消除候选窗）
        ///   XIMEMODE 1      退路：切换输入法模式（真的切输入法中英文状态）——
        ///                   天正也能三全，但**改了你的输入法状态**（命令期间显示英文）
        /// </summary>
        [CommandMethod("XIMEMODE", CommandFlags.Modal)]
        public void WorkModeSwitch()
        {
            EnsureHooked();
            var cfg = XimeApplication.Config;

            string arg = null;
            try
            {
                var res = Application.DocumentManager.MdiActiveDocument.Editor.GetString(
                    "\n[输入法] 工作模式 0=无视输入法 1=切换输入法（直接回车保持 "
                    + ModeText(cfg.WorkMode) + "）: ");
                if (res != null) arg = res.StringResult;
            }
            catch (System.Exception) { }

            if (string.IsNullOrEmpty(arg))
            {
                XimeApplication.SafeWriteMessage(
                    "\n[X输入法] 当前模式 = " + ModeText(cfg.WorkMode)
                    + "（" + (int)cfg.WorkMode + "）"
                    + "\n  重跑 XIMEMODE 并输入 0/1 即可切换："
                    + "\n    0 = 无视输入法模式 ★ 默认（**不改变你的输入法状态**；"
                    + "天正二级命令靠 XIMEKEY 2 放行物理键，候选窗由「命令期间无视输入法」消除）"
                    + "\n    1 = 切换输入法模式（★ 退路：真的把输入法切成英文/中文，退出时还原）"
                    + "——天正也三全，但会改你的输入法状态（命令期间显示英文）");
                return;
            }

            XimeWorkMode target;
            switch (arg.Trim())
            {
                case "0": target = XimeWorkMode.IgnoreIme; break;
                case "1": target = XimeWorkMode.SwitchIme; break;
                default:
                    XimeApplication.SafeWriteMessage("\n[X输入法] 只接受 0 / 1。");
                    return;
            }

            cfg.WorkMode = target;
            cfg.Save();
            if (XimeApplication.Engine != null) XimeApplication.Engine.Refresh();

            XimeApplication.SafeWriteMessage(
                "\n[X输入法] 工作模式已切换为：" + ModeText(target) + "（已保存，重启后保持）。");

            // 切换输入法模式有个硬前提：输入法得能读写状态。
            // 当场替他验一次，比让他回去试半天更省事。
            if (target == XimeWorkMode.SwitchIme)
                XimeApplication.SafeWriteMessage("\n" + BuildImeProbeReport());
        }

        /// <summary>
        /// ★ v6：查看 / 现场验证「命令期间无视输入法」。
        ///
        ///   XIMEBYPASS        查看开关、生效状态、被解除关联的窗口数
        ///   XIMEBYPASS 1      **手动强制解除**并保持住（回到 CAD 敲天正二级命令，看还弹不弹候选窗）
        ///   XIMEBYPASS 0      立刻还原 + 取消手动强制
        ///   XIMEBYPASS on/off 打开 / 关闭这个机制本身（写盘，重启后保持）
        ///
        /// 这个机制的来龙去脉见 Xime.Core.ImeWindowBypass：它把 AutoCAD 窗口与
        /// **输入法上下文的关联**解除，于是物理键照旧直达天正，而输入法收不到键、
        /// 不弹候选窗 —— 并且**完全不改你输入法的状态**。
        ///
        /// ★ v6.1 两处修正（2026-10-06 现场日志暴露）：
        ///   ① 插件自己的命令**不再触发**本机制 ⇒ XIMESTATUS 看到的是**真实状态**
        ///      （原来它"自己就是个命令"，永远报「当前生效=是」）；
        ///   ② `1` 变成**手动强制并保持** ⇒ 用户离开命令行回 CAD 里试键的那段空闲期
        ///      不会被自动同步逻辑撤掉（原来一空闲就还原，等于白按）。
        /// </summary>
        [CommandMethod("XIMEBYPASS", CommandFlags.Modal)]
        public void ImeBypass()
        {
            EnsureHooked();
            var cfg = XimeApplication.Config;
            var engine = XimeApplication.Engine;

            string arg = null;
            try
            {
                var res = Application.DocumentManager.MdiActiveDocument.Editor.GetString(
                    "\n[输入法] 命令期间无视输入法（消除候选窗）1=强制解除并保持 0=还原 "
                    + "on/off=开关此机制（直接回车只看状态）: ");
                if (res != null) arg = res.StringResult;
            }
            catch (System.Exception) { }

            string a = (arg ?? string.Empty).Trim().ToLowerInvariant();

            if (a == "1" || a == "0")
            {
                if (engine == null)
                {
                    XimeApplication.SafeWriteMessage("\n[X输入法] 引擎未启动，无法执行。");
                    return;
                }
                if (a == "1")
                {
                    // ★ 进来之前是不是已经生效 —— 决定文案怎么讲实话（见下）。
                    bool wasActive = engine.ImeBypassActive;
                    int visited;
                    int n = engine.ForceImeBypass(out visited);

                    // 试法的提示只在"真的咬住了"时才给（n == 0 时给的是"换方案"的建议）。
                    const string howToTest =
                        "\n  现在回到 CAD 里试：RYBZ → 选块 → 按 A。三项都该过"
                        + "（天正有动作 / 命令栏无 a / 不弹候选窗）。";

                    string head;
                    if (wasActive)
                    {
                        head = "\n[X输入法] 本机制**本来就已经生效**（已解除 " + n + " 个窗口），无需重复解除。"
                             + howToTest;
                    }
                    else
                    {
                        head = "\n[X输入法] 已解除 " + n + " 个窗口的输入法关联"
                             + "（枚举 " + visited + " 个 = 主窗口 + 全部子孙）。";
                        head += (n == 0)
                            ? "\n  ★ 0 个 = 这些窗口本来就没有输入法上下文 ⇒ 这套机制在你机器上无从下手，"
                              + "候选窗改由第二层兜底处理：焦点不在命令行输入框时，按键一律放行给系统。"
                            : howToTest;
                    }

                    XimeApplication.SafeWriteMessage(head);
                    XimeApplication.SafeWriteMessage(
                        "\n  ★ 已**保持住**（不会因为空闲而被自动还原）。试完请执行 XIMEBYPASS 0 还原，"
                        + "\n    否则打不出中文；双击文字改字时它会自动让开（文字编辑态优先级最高）。");
                }
                else
                {
                    int n = engine.ReleaseImeBypass();
                    XimeApplication.SafeWriteMessage(
                        "\n[X输入法] 已还原 " + n + " 个窗口的输入法关联（现在可以正常打中文了）。");
                    XimeApplication.SafeWriteMessage(
                        "\n  说明：还原的是**手动强制**。本机制仍会在**命令执行期间**自动生效"
                        + "（那是它的本职工作，天正二级命令靠它不弹候选窗）；"
                        + "\n  想彻底关掉它请用 XIMEBYPASS off。");
                }
                return;
            }

            if (a == "on" || a == "off")
            {
                cfg.BypassImeDuringCommand = (a == "on");
                cfg.Save();
                // ★ off 时必须把"手动强制"一并撤掉：否则标志还在，用户再 on 回来时
                //   会在空闲期也立刻解除关联 —— 一个看不见的残留状态。
                if (a == "off" && engine != null) engine.ReleaseImeBypass();
                if (engine != null) engine.Refresh();
                XimeApplication.SafeWriteMessage(
                    "\n[X输入法] 「命令期间无视输入法」已 " + (a == "on" ? "打开" : "关闭")
                    + "（已保存，重启后保持）。");
                if (a == "off")
                    XimeApplication.SafeWriteMessage(
                        "\n  关闭后：天正二级命令照旧可用，但中文态下输入法会弹候选窗（旧行为）。");
                return;
            }

            // 只看状态
            bool active = engine != null && engine.ImeBypassActive;
            bool manual = engine != null && engine.ManualImeBypass;
            XimeApplication.SafeWriteMessage(
                "\n[X输入法] 命令期间无视输入法 = "
                + (cfg.BypassImeDuringCommand ? "开" : "关")
                + "\n  当前生效 = " + (active ? "是" : "否")
                + (active && manual ? "（手动强制保持中）" : "")
                + "  已解除关联窗口 = " + (engine != null ? engine.ImeBypassDetachedCount : 0)
                + "\n  工作模式 = " + ModeText(cfg.WorkMode)
                + "  命令期间按键 = " + KeyModeText(cfg.CommandKeyMode)
                + "\n  生效条件（六条全过）：无视输入法模式 + 引擎已接管 + 非本插件命令"
                + "\n                    + 命令执行期间 + 非文字类命令 + 不在文字编辑态"
                + "\n  说明：它只解除**窗口与输入法的关联**，输入法自身的状态（中/英、布局、状态栏）一个字节都不动。"
                + "\n  XIMEBYPASS 1 / 0 可现场试开试关（1 = 强制解除并保持）；on / off 可长期开关这个机制。");
        }

        /// <summary>
        /// 输入法通道探测：XIMEPROBE
        ///
        /// 「切换输入法模式」的全部功能都建立在"能读出输入法中英状态"之上。
        /// 本命令逐条列出两条通道的可用性，让"这个模式在我机器上到底行不行"
        /// 变成当场可验证的结论，而不是靠反复试。
        /// </summary>
        [CommandMethod("XIMEPROBE", CommandFlags.Modal)]
        public void ImeProbe()
        {
            try
            {
                EnsureHooked();
                XimeApplication.SafeWriteMessage("\n" + BuildImeProbeReport());
            }
            catch (System.Exception ex)
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 输入法探测失败：" + ex.Message);
            }
        }

        /// <summary>生成输入法通道探测报告（XIMEPROBE / XIMEMODE 共用）。</summary>
        private static string BuildImeProbeReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== X输入法 输入法通道探测 =====");
            sb.AppendLine("当前用户输入法: " + GetActiveImeName());

            var fg = NativeMethods.GetForegroundWindow();
            var acad = XimeApplication.GetHostMainWindow();
            var engine = XimeApplication.Engine;
            var probe = engine != null ? engine.Ime : new ImeController();

            // ★ 刻意取 CAD 自己的窗口，而不是前台窗口 ——
            //   "退出还原"用的就是这个靶点，测它才测到点子上。
            var ch = probe.ProbeChannels(acad);
            var acadMode = probe.GetModeOnWindow(acad);

            // ★★ 并列打印「切换动作真正的靶点」与「CAD 主窗口」两处的读数。
            //   这两处一旦不一致，"进入前状态"与"实际切换"就永远对不上 ——
            //   这是历史上"切换输入法模式不起作用"的头号根因，必须当场看得见。
            IntPtr target = IntPtr.Zero;
            bool targetTrusted = false;
            if (engine != null) target = engine.DiagnosticImeTarget(out targetTrusted);
            var tch = target != IntPtr.Zero ? probe.ProbeChannels(target) : new ImeController.ChannelProbe();
            var targetMode = target != IntPtr.Zero ? probe.GetModeOnWindow(target) : ImeMode.Unknown;

            sb.AppendLine("前台窗口: 0x" + fg.ToInt64().ToString("X")
                + " 类名=" + FocusMonitor.GetWindowClass(fg)
                + (fg == acad ? "  ← CAD 主窗口" : "")
                + (XimeEngine.IsOwnWindow(fg) ? "  ← 本插件自己的窗口" : ""));
            sb.AppendLine("切换动作靶点: " + (target != IntPtr.Zero
                ? "0x" + target.ToInt64().ToString("X") + " 类名=" + FocusMonitor.GetWindowClass(target)
                : "(无 —— 当前没有属于 " + Compat.HostShortName + " 的窗口)")
                + (target != IntPtr.Zero
                    ? (targetTrusted ? "  [可信：IME 上下文在此]" : "  ★ 不可信：不是 IME 承接窗口")
                    : ""));
            if (target != IntPtr.Zero && !targetTrusted)
            {
                sb.AppendLine("   ★★ 该靶点不是真正承接受输入的控件（命令栏编辑框 / 在位文字编辑器），");
                sb.AppendLine("      它读出来的中英状态是兼容层的**默认值**（恒为「中文」），不能采信。");
                sb.AppendLine("      插件已自动忽略它、改由「我们自己动作的结果」驱动切换；");
                sb.AppendLine("      若此状态长期存在，请把本屏内容发回以便定位。");
            }
            sb.AppendLine("此刻可发真实按键（模拟 Shift）: "
                + (engine != null ? (engine.DiagnosticCanSendRealKey() ? "是" : "否（前台不是 CAD，或前台是本插件面板）") : "(引擎未就绪)"));
            sb.AppendLine("CAD 主窗口句柄: 0x" + acad.ToInt64().ToString("X"));
            // ★ v6：命令期间「让 CAD 窗口无视输入法」的状态 —— 这是
            //   "天正二级命令能用、又不弹候选窗"的关键闸门，出问题第一个看这里。
            //   「已解除关联窗口 = 0」意味着这些窗口本来就没有输入法上下文
            //   ⇒ 这套机制在你这台机器上无从下手（改由「焦点不在命令行输入框时放行」这一层兜底）。
            sb.AppendLine("命令期间无视输入法: "
                + (XimeApplication.Config.BypassImeDuringCommand ? "开" : "关")
                + (engine != null
                    ? "   当前生效=" + (engine.ImeBypassActive ? "是" : "否")
                      + "   已解除关联窗口=" + engine.ImeBypassDetachedCount
                    : "   (引擎未就绪)"));
            if (fg != acad)
                sb.AppendLine("（注：此刻前台窗口不是 CAD，读数仍取自 CAD 自己的窗口 ——"
                            + " 这正是「退出还原」时使用的靶点）");
            sb.AppendLine();
            sb.AppendLine("通道① IMM32（ImmGetConversionStatus）  : " + DescribeIme(ch.ViaImm32));
            sb.AppendLine("通道② IME 消息（WM_IME_CONTROL）        : " + DescribeIme(ch.ViaImeControl)
                + (ch.ImeWindowAvailable ? "" : "   [IME 窗口未取到]"));

            if (target != IntPtr.Zero && target != acad)
            {
                sb.AppendLine();
                sb.AppendLine("—— 靶点窗口（0x" + target.ToInt64().ToString("X") + "）的读数 ——");
                sb.AppendLine("通道① IMM32                            : " + DescribeIme(tch.ViaImm32));
                sb.AppendLine("通道② IME 消息                          : " + DescribeIme(tch.ViaImeControl));
                sb.AppendLine("综合结果                                : " + DescribeIme(targetMode));
                if (targetMode != acadMode)
                {
                    sb.AppendLine("⚠ 靶点与 CAD 主窗口读数**不一致**（"
                        + DescribeIme(targetMode) + " vs " + DescribeIme(acadMode) + "）。");
                    sb.AppendLine("   这是历史上「切换模式不起作用」的根因：采样端与应用端靶点不同。");
                    sb.AppendLine("   本版本已把采样/应用/还原三处统一到「靶点窗口」，请以靶点读数为准。");
                }
            }

            // ★ 双通道读数冲突提示（TSF 输入法的典型特征）：
            //   通道①（IMM32 兼容层缓存）常停在旧值，通道②（直接问 IME 窗口）才是真实状态。
            bool conflict = ch.ViaImm32 != ImeMode.Unknown
                         && ch.ViaImeControl != ImeMode.Unknown
                         && ch.ViaImm32 != ch.ViaImeControl;
            if (conflict)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ 两条通道读数不一致 —— 这是 TSF 输入法（微软拼音/搜狗等）的典型特征：");
                sb.AppendLine("   IMM32 兼容层缓存的是旧状态，IME 消息通道读到的才是真实状态。");
                sb.AppendLine("   本插件以「IME 消息通道」为准（下方综合结果），这才是能正确切换的依据。");
            }

            sb.AppendLine("综合结果                                : " + DescribeIme(acadMode));

            sb.AppendLine();
            if (!ch.Usable)
            {
                sb.AppendLine("判定：★ 两条通道都读不出状态 → **「切换输入法模式」无法工作**，");
                sb.AppendLine("      请执行 XIMEMODE 输入 0 改用「无视输入法模式」，并把 XIMEKEY 设为 2（放行物理键）");
                sb.AppendLine("      —— 天正这类第三方命令只认物理键，合成通道一律不认。");
            }
            else if (ch.ViaImm32 == ImeMode.Unknown)
            {
                sb.AppendLine("判定：✓ 可用 —— IMM32 兼容层被关掉了，但 IME 消息通道有效，");
                sb.AppendLine("      「切换输入法模式」照常工作。");
            }
            else
            {
                sb.AppendLine("判定：✓ 可用 —— 「切换输入法模式」可正常工作。");
            }

            if (engine != null)
            {
                sb.AppendLine();
                sb.AppendLine("引擎内部记录: 模式=" + engine.Config.WorkMode
                    + " | 已接管=" + (engine.IsEngaged ? "是" : "否")
                    + " | 可探测=" + (engine.SwitchImeDetectable ? "是" : "否"));
                sb.AppendLine("              进入前原始状态=" + engine.SwitchImeOriginalMode
                    + " | 当前确信状态=" + engine.SwitchImeAppliedMode);

                // ★ 待补切换：这是"切换输入法模式"能否生效的关键中间态。
                //   面板上点模式切换的那一瞬间发不出真实 Shift，只能登记待补，
                //   等 CAD 回到前台后补做（见 XimeEngine.TryFlushPendingSwitch）。
                if (engine.SwitchImePendingMode != ImeMode.Unknown)
                {
                    sb.AppendLine("              ⚠ 有待补的切换：目标=" + engine.SwitchImePendingMode
                        + "（已尝试 " + engine.SwitchImePendingTries + " 次）"
                        + " → 回到命令栏后会自动补做一次真实 Shift");
                }
                else if (engine.Config.WorkMode == XimeWorkMode.SwitchIme
                         && engine.SwitchImeAppliedMode == ImeMode.Unknown)
                {
                    sb.AppendLine("              提示：本模式下当前**没有**确信的输入法状态"
                        + "（既没探测确认、也没成功发过真实 Shift）——"
                        + "此时按一次 Shift 或执行任意绘图命令都会重新对齐。");
                }
            }

            // ★ v45：把"日志本身可不可信"也报出来。
            //   理由：排查全部建立在日志之上，而旧实现的 `File.AppendAllText`
            //   在多线程下会**静默丢行**（自测里 8 线程并发 2400 行丢了 1433 行）。
            //   丢行会让"某动作没发生"这种结论变成假象，必须当场可见。
            sb.AppendLine();
            sb.AppendLine("── 调试日志状态（v45）──");
            if (XimeEngine.DebugLogPath == null)
                sb.AppendLine("日志：★ 本次进程**从未开启过**调试日志 → 引擎日志为空，"
                              + "任何「某动作没发生」的结论都不可用。");
            else
                sb.AppendLine("日志：已开启，本次会话写入 " + XimeEngine.DebugLogLines + " 行"
                              + "；写入失败 " + XimeEngine.DebugLogDropped + " 行"
                              + (XimeEngine.DebugLogDropped > 0
                                  ? "  ★ 有丢行！计数型结论需按「至少这么多」理解" : "（无丢行）"));
            sb.AppendLine("提示：**关键事件**（切换失败 / 是否把输入法留在英文 / 还原结果 / 接管与模式）"
                          + "始终记录在 %TEMP%\\xime_startup.log，无需开日志。");

            sb.AppendLine("==================================");
            return sb.ToString();
        }

        /// <summary>把 ImeMode 描述成中文（诊断输出用）。</summary>
        private static string DescribeIme(ImeMode mode)
        {
            switch (mode)
            {
                case ImeMode.English: return "English（英文）";
                case ImeMode.Chinese: return "Chinese（中文）";
                default: return "Unknown（读不出）";
            }
        }

        [CommandMethod("XIMEON", CommandFlags.Modal)]
        public void Enable()
        {
            EnsureHooked();
            XimeApplication.Config.Enabled = true;
            XimeApplication.Config.EnabledByUser = true;   // 用户显式开启，之后加载不再覆盖
            // ★ 刻意**不**重置 WorkMode：
            //   早先这里写死成 IgnoreIme，结果是"用户选了切换输入法模式 → 执行一次
            //   XIMEON → 模式被悄悄改回无视输入法"，而且没有任何提示。
            //   启用/关闭与模式选择是两件事，互不干涉。
            XimeApplication.Config.Save();
            XimeApplication.Engine.Refresh();
            XimeApplication.SafeWriteMessage(
                "\n[X输入法] 已启用（" + ModeText(XimeApplication.Config.WorkMode) + "）。");
        }

        [CommandMethod("XIMEOFF", CommandFlags.Modal)]
        public void Disable()
        {
            EnsureHooked();
            XimeApplication.Config.Enabled = false;
            XimeApplication.Config.EnabledByUser = true;   // 用户显式关闭，重启后保持关闭
            XimeApplication.Config.Save();
            XimeApplication.Engine.Refresh();
            XimeApplication.SafeWriteMessage(
                "\n[X输入法] 已关闭（已恢复本地输入法状态）。"
                + "该状态会被保存，重启 " + Compat.HostShortName + " 后仍是关闭；"
                + "需要恢复请执行 XIMEON 或在界面里勾选「启用 X输入法」。");
        }

        /// <summary>诊断：列出当前环境、输入法兼容性、命令栏句柄探测结果。</summary>
        [CommandMethod("XIMEDIAG", CommandFlags.Modal)]
        public void Diagnose()
        {
            try
            {
                EnsureHooked();
                var sb = new StringBuilder();
                sb.AppendLine("\n===== X输入法 诊断 =====");

                string cadVer = "未知";
                try { cadVer = Application.Version.ToString(); } catch (System.Exception) { }

                sb.AppendLine(Compat.HostShortName + " 版本: " + cadVer);
                sb.AppendLine("操作系统: " + System.Environment.OSVersion.Version);
                sb.AppendLine("CLR: " + System.Environment.Version);
                sb.AppendLine("进程架构: " + (Compat.ProcessArch));

                // 输入法状态（仅「切换输入法模式」依赖它；无视输入法模式不依赖）
                var ime = XimeApplication.Engine != null ? XimeApplication.Engine.Ime : null;
                if (ime != null)
                {
                    var mode = ime.GetCurrentMode();
                    sb.AppendLine("工作模式: " + ModeText(XimeApplication.Config.WorkMode));
                    sb.AppendLine("输入法中英模式: " + mode + "（无视输入法模式不依赖此项）");
                    sb.AppendLine("切换输入法模式可探测: "
                        + (XimeApplication.Engine.SwitchImeDetectable ? "是" : "否（该模式将无效）"));
                    sb.AppendLine("钩子状态: " + (XimeApplication.Engine.Hook.IsInstalled ? "已安装" : "未安装"));
                    sb.AppendLine("接管状态: " + (XimeApplication.Engine.IsEngaged ? "已接管" : "未接管"));
                }

                // 命令栏句柄
                var mainHwnd = XimeApplication.GetHostMainWindow();
                sb.AppendLine("CAD 主窗口句柄: 0x" + mainHwnd.ToInt64().ToString("X"));
                sb.AppendLine("前台窗口句柄: 0x" + NativeMethods.GetForegroundWindow().ToInt64().ToString("X"));

                var frame = Xime.Core.HostTextFrame.Resolve();
                sb.AppendLine("命令栏控件句柄: " + (frame != IntPtr.Zero
                    ? "0x" + frame.ToInt64().ToString("X") + " (" + Xime.Core.HostTextFrame.GetClassNameSafe(frame) + ")"
                    : "定位失败"));

                sb.AppendLine("当前用户输入法: " + GetActiveImeName());

                // ---- 关键命令判定自检 ----
                // 排查「某命令执行期间输入法被切成中文 / 选项键失灵」时看这里。
                // ★ 特别地：RYBZ（天正任意布置）必须显示「保持英文」——
                //   它曾被两字母词根 "bz" 误判为标注类命令，导致命令期间输入法被切走，
                //   选中块后敲二级命令（A/E/D/F/X/Y）全部失灵（2022+T20v10 实测）。
                sb.AppendLine();
                sb.AppendLine("关键命令判定（文字类命令请加进 xime 界面的白名单）:");
                var probe = new[]
                {
                    "RYBZ", "TEXT", "MTEXT", "DDEDIT", "TOBJEDIT",
                    "T73_TTEXT", "T20_TMText", "LINE", "OFFSET", "CIRCLE"
                };
                foreach (var p in probe)
                {
                    var lv = XimeApplication.Config.MatchChinese(p);
                    string verdict;
                    if (lv == XimeConfig.ChineseNeed.Exact) verdict = "需要中文（精确命中白名单）";
                    else if (lv == XimeConfig.ChineseNeed.Keyword) verdict = "可能需中文（关键词猜测，不提前切）";
                    else verdict = "保持英文";
                    sb.AppendLine("  " + p.PadRight(14) + " -> " + verdict);
                }

                sb.AppendLine("=========================");
                XimeApplication.SafeWriteMessage(sb.ToString());
            }
            catch (System.Exception ex)
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 诊断失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 诊断：图标 / Logo 资源加载情况。
        ///
        /// 排查"标题栏左上角没有 logo"时先跑这个。它会打印 **Xime.Core.dll 的实际加载路径** ——
        /// AutoCAD 无法卸载已加载的程序集，同一会话里先 NETLOAD 过旧版本的话，
        /// 之后即使换成新目录的 Xime.dll，仍然会复用旧的 Xime.Core（旧的里面没有图标资源）。
        /// </summary>
        [CommandMethod("XIMELOGO", CommandFlags.Modal)]
        public void LogoDiag()
        {
            try
            {
                EnsureHooked();
                XimeApplication.SafeWriteMessage("\n" + Xime.Core.UI.XimeResources.Diagnose());
            }
            catch (System.Exception ex)
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 图标诊断失败：" + ex.Message);
            }
        }

        /// <summary>调试日志开关：XIMEDEBUG 开 / XIMEDEBUG 关。</summary>
        [CommandMethod("XIMEDEBUG", CommandFlags.Modal)]
        public void DebugToggle()
        {
            EnsureHooked();

            var ed = Application.DocumentManager.MdiActiveDocument;

            // ★ v45：把**当前状态**如实报出来。
            //   实机教训：用户在**测试结束之后**才敲 XIMEDEBUG 想看日志，
            //   而那一轮从头到尾就没开过日志 —— 提示语却只问"开还是关"，
            //   用户以为自己"生成"了日志，实际什么也没有。
            bool wasOn = XimeEngine.DebugLogPath != null && XimeEngine.DebugLogLines > 0;
            XimeApplication.SafeWriteMessage(
                "\n[X输入法] 调试日志当前：" + (wasOn
                    ? "已开启（本次会话已写入 " + XimeEngine.DebugLogLines + " 行）"
                    : "★ 未开启 —— 本次会话的引擎日志是空的，你现在复现的问题**没有被记录**"));

            bool on = true;
            try
            {
                var res = ed.Editor.GetString(
                    "\n[X输入法] 开启调试日志？[开/关] <开>: ");
                var s = (res.StringResult ?? string.Empty).Trim().ToLowerInvariant();
                if (s == "关" || s == "off" || s == "0" || s == "n" || s == "no")
                    on = false;
            }
            catch (System.Exception) { }

            int linesBefore = XimeEngine.DebugLogLines;
            bool pathBefore = XimeEngine.DebugLogPath != null;
            XimeEngine.SetDebugLog(on);

            // ★ v43：把"开关动作"本身也记进**始终记录**的启动日志。
            //   这条解决一个具体的歧义：引擎日志里没有内容，到底是
            //     (a) 用户从没开过调试日志，还是
            //     (b) 开了但写不进去（路径不可写 / 被占用）？
            //   两年前(a)是常态 —— 一个诊断工具在"用户忘了开开关"时会给出
            //   "插件没动作"的错误结论，必须从数据上把这两种情况分开。
            try
            {
                XimeApplication.WriteStartupLog("[调试日志] " + (on ? "已开启" : "已关闭")
                    + " → " + (XimeEngine.DebugLogPath
                        ?? "(路径不可用：本次进程从未开启过日志)"));
            }
            catch (System.Exception) { }

            if (on)
            {
                XimeApplication.SafeWriteMessage(
                    "\n[X输入法] 调试日志已开启。日志文件：\n" + XimeEngine.DebugLogPath +
                    "\n请**先切到 CAD 窗口复现问题**（切模式 / 双击文字），然后再回来执行 XIMEDEBUG 关闭。");
            }
            else
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 调试日志已关闭。");

                // ★★★ v45：关闭时做一次"这一轮到底有没有被记录"的核对。
                //   这是本轮实机事故的直接产物：用户以为"日志已经生成了"，
                //   实际那一轮从头到尾没开过日志，排查只能靠猜。
                if (linesBefore <= 0)
                {
                    XimeApplication.SafeWriteMessage(
                        "\n⚠⚠ [X输入法] 注意：本次会话的调试日志**一行都没有写入**\n"
                        + "（" + (pathBefore ? "日志文件已就绪" : "本次进程从未开启过日志")
                        + "）。也就是说，你刚才的复现**没有被记录**，这份日志帮不上忙。\n"
                        + "请严格按下面顺序重做一遍：\n"
                        + "  ① 先执行 XIMEDEBUG → 提示时**直接回车**（默认「开」）；\n"
                        + "  ② 切到 CAD 窗口复现问题（天正二级命令 + 双击文字修改交替做，\n"
                        + "     各 20 次以上；若开关过 XIMEBYPASS 也请顺手带上）；\n"
                        + "  ③ 再执行 XIMEDEBUG → 输入「关」；\n"
                        + "  ④ 然后运行桌面的 xime_check\\双击自查.bat，把报告发出来。\n"
                        + "提示：即使忘了开日志，插件现在也会把**关键事件**（切换失败、\n"
                        + "我们是否把输入法留在了英文、还原成功与否）记进始终记录的\n"
                        + "%TEMP%\\xime_startup.log —— 自查报告会读它。");
                }
                else
                {
                    XimeApplication.SafeWriteMessage(
                        "\n[X输入法] 本次会话共写入 " + linesBefore + " 行引擎日志"
                        + (XimeEngine.DebugLogDropped > 0
                            ? "（★ 另有 " + XimeEngine.DebugLogDropped + " 行写入失败）" : "")
                        + "。\n现在可以运行桌面的 xime_check\\双击自查.bat 生成报告。");
                }
            }
        }

        /// <summary>
        /// 焦点自检：XIMEFOCUS
        ///
        /// 打印当前键盘焦点所在控件的类名、是否被识别为「文字编辑框」、是否属于命令栏，
        /// 以及引擎当前的「文字编辑态」标志。
        ///
        /// 用法：**双击要编辑的文字（不要点命令栏）**，然后立刻执行 XIMEFOCUS，
        /// 观察「焦点控件类名」与「识别为编辑框」是否为"是"。
        /// 若为"否"，说明该编辑框不在识别范围内，把输出发给我即可精确补规则。
        /// </summary>
        [CommandMethod("XIMEFOCUS", CommandFlags.Modal)]
        public void FocusProbe()
        {
            try
            {
                EnsureHooked();
                var e = XimeApplication.Engine;
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("===== X输入法 焦点自检 =====");

                var fg = NativeMethods.GetForegroundWindow();
                sb.AppendLine("前台窗口     : 0x" + fg.ToInt64().ToString("X") +
                    "  类名=" + Xime.Core.HostTextFrame.GetClassNameSafe(fg) +
                    "  标题=" + Xime.Core.FocusMonitor.GetWindowText(fg));

                var focus = Xime.Core.CharInjector.GetRealFocusWindow();
                sb.AppendLine("真实焦点控件 : 0x" + focus.ToInt64().ToString("X") +
                    "  类名=" + Xime.Core.FocusMonitor.GetWindowClass(focus) +
                    "  标题=" + Xime.Core.FocusMonitor.GetWindowText(focus));

                // 沿父链打印，便于看清对话框结构
                var cur = focus;
                for (int i = 0; i < 6 && cur != IntPtr.Zero; i++)
                {
                    sb.AppendLine(string.Format("  ↑[{0}] 0x{1} 类名={2} 标题={3}",
                        i, cur.ToInt64().ToString("X"),
                        Xime.Core.FocusMonitor.GetWindowClass(cur),
                        Xime.Core.FocusMonitor.GetWindowText(cur)));
                    cur = NativeMethods.GetParent(cur);
                }

                sb.AppendLine();
                sb.AppendLine("识别为编辑框 : " + (Xime.Core.FocusMonitor.IsEditBoxWindow(focus) ? "是 ✓" : "否 ✗"));
                sb.AppendLine("属于命令栏   : " + (Xime.Core.HostTextFrame.IsCommandLineWindow(focus) ? "是（会排除）" : "否"));
                if (e != null)
                {
                    sb.AppendLine("引擎文字编辑态: " + (e.InTextEditMode ? "是（允许中文）" : "否（英文接管）"));
                    sb.AppendLine("命令栏需英文 : " + (e.CommandLineNeedsEnglish ? "是" : "否"));
                }

                XimeApplication.SafeWriteMessage(sb.ToString());
            }
            catch (System.Exception ex)
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 焦点自检失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 自动加载注册（一键）：XIMEAUTO
        ///
        /// ★ 为什么需要它（本轮用户实测：「AutoCAD2014 重启电脑后不会自动加载插件」）：
        ///   自动加载是**按 AutoCAD 版本隔离**的（见 UninstallHelper 的说明）——
        ///   只有"在某个版本里加载过插件"才会给那个版本写注册项。于是多版本机器上
        ///   会出现一个极容易被误解的现象：
        ///     · 在 2022 里 NETLOAD 过 → 只写 R24.1 → 2022 能自动加载；
        ///     · 2014 从来没在里面 NETLOAD 过 → R19.1 一个 XIME 注册项都没有
        ///       → 重启电脑后 2014 自然**不会**自动加载。
        ///   更隐蔽的是：面板上的「卸载」过去会把**所有版本**的注册项一起清掉
        ///   （旧版 UninstallHelper.RemoveAutoLoadRegistration 就是这么写的），
        ///   于是"以前还能自动加载的 2014"会突然不行 —— 而用户在 2022 里根本看不到。
        ///   ★ v1.0.2 已修正：卸载**只清当前这一个版本**，其它版本原样保留。
        ///
        ///   本命令 = 为**当前正在运行的这个 AutoCAD 版本**立即写入自动加载项（幂等，
        ///   已写过再写一次也无害）。在 2014 里执行一次 → 以后 2014 就能自动加载。
        /// </summary>
        [CommandMethod("XIMEAUTO", CommandFlags.Modal)]
        public void WriteAutoLoad()
        {
            try
            {
                EnsureHooked();

                var dir = PluginPaths.PluginDirectory;
                var dllPath = System.IO.Path.Combine(dir, "Xime.dll");
                if (!System.IO.File.Exists(dllPath))
                {
                    // 可能被重命名，退回到当前程序集位置（与 TryRegisterAutoLoad 同一套回退）
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    if (!string.IsNullOrEmpty(asm.Location)) dllPath = asm.Location;
                }

                string acadVer = "(读取失败)";
                try
                {
                    var o = Application.GetSystemVariable("ACADVER");
                    acadVer = o == null ? "(空)" : o.ToString();
                }
                catch (System.Exception) { }

                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("===== X输入法 自动加载注册（XIMEAUTO）=====");
                sb.AppendLine("ACADVER  : " + acadVer);
                sb.AppendLine("插件 DLL : " + dllPath);

                var written = UninstallHelper.RegisterAutoLoad(dllPath);
                if (written.Count > 0)
                {
                    sb.AppendLine("已写入   : " + string.Join(" | ", written.ToArray()));
                    sb.AppendLine("→ 下次启动【这个版本】的 " + Compat.HostShortName + " 时会自动加载本插件。");
                    sb.AppendLine("  ★ 注意：只对当前版本生效。其它版本（如 2014 / 2022 的另一边）");
                    sb.AppendLine("    需要在那个版本里各跑一次 XIMEAUTO（或 NETLOAD 一次）。");
                }
                else
                {
                    sb.AppendLine("★ 未能写入（可能权限不足，或没能定位到当前版本的产品键）。");
                    sb.AppendLine("  请接着执行 XIMEREG，把它的完整输出发给我。");
                }
                XimeApplication.SafeWriteMessage(sb.ToString());
            }
            catch (System.Exception ex)
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 自动加载注册失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 自动加载注册表自检：XIMEREG
        ///
        /// 显示 ACADVER、定位到的目标注册表键、同版本下各候选产品键的
        /// 「真实产品键 / 空壳键」判定，以及 XIME 项实际写在哪里。
        ///
        /// 用途：诊断「重启 AutoCAD 后插件不自动加载」——
        /// 若 XIME 被写进了「空壳键」（如 R19.1\ACAD-D001 而非 R19.1\ACAD-D001:804），
        /// AutoCAD 不会读取它，表现为注册成功但不加载。
        /// </summary>
        [CommandMethod("XIMEREG", CommandFlags.Modal)]
        public void RegProbe()
        {
            try
            {
                EnsureHooked();
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("===== X输入法 自动加载注册表自检 =====");
                sb.Append(UninstallHelper.Describe());
                sb.AppendLine();
                sb.Append("若上面「当前版本是否已注册 = 否」→ 执行 XIMEAUTO 立即为当前版本写入注册项。");
                XimeApplication.SafeWriteMessage(sb.ToString());
            }
            catch (System.Exception ex)
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 注册表自检失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 窗口定位自检：XIMETESTCN
        ///
        /// 验证新的「绕过输入法」机制所需的命令栏控件是否能够定位成功。
        /// 这是本插件能否正常工作的先决条件 —— 若此处失败，
        /// 说明 acad.exe 的 AcadGetTextFrameHandle 导出或窗口结构不匹配，
        /// 需要走回退路径。
        /// </summary>
        [CommandMethod("XIMETESTCN", CommandFlags.Modal)]
        public void TestSwitch()
        {
            try
            {
                EnsureHooked();

                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("===== X输入法 命令栏定位自检 =====");

                var mainHwnd = XimeApplication.GetHostMainWindow();
                sb.AppendLine("CAD 主窗口   : 0x" + mainHwnd.ToInt64().ToString("X"));
                sb.AppendLine("前台窗口     : 0x" + NativeMethods.GetForegroundWindow().ToInt64().ToString("X"));

                // 真实焦点控件（投递首选）
                var focus = Xime.Core.CharInjector.GetRealFocusWindow();
                sb.AppendLine("真实焦点控件 : 0x" + focus.ToInt64().ToString("X") +
                    (focus != IntPtr.Zero
                        ? "  类名=" + Xime.Core.HostTextFrame.GetClassNameSafe(focus) +
                          (Xime.Core.HostTextFrame.IsTextWindow(focus) ? "  ⚠文本窗口" : "") +
                          "  强度=" + Xime.Core.HostTextFrame.MatchStrength(focus)
                        : ""));

                // 核心：命令栏输入控件定位
                var frame = Xime.Core.HostTextFrame.Resolve();
                if (frame == IntPtr.Zero)
                {
                    sb.AppendLine("命令栏控件   : 定位失败 ✗");
                    sb.AppendLine();
                    sb.AppendLine("【影响】无法把英文命令投递给命令栏，核心功能将不生效。");
                    sb.AppendLine("【排查】1) 确认插件已加载到 acad.exe 进程；");
                    sb.AppendLine("        2) 确认命令栏（Ctrl+9）处于显示状态；");
                    sb.AppendLine("        3) 把本输出截图反馈。");
                }
                else
                {
                    bool isText = Xime.Core.HostTextFrame.IsTextWindow(frame);
                    sb.AppendLine("命令栏控件   : 0x" + frame.ToInt64().ToString("X") +
                        (isText ? " ✗ 误命中文本窗口" : " ✓"));
                    sb.AppendLine("  窗口类名   : " + Xime.Core.HostTextFrame.GetClassNameSafe(frame));
                    sb.AppendLine("  窗口标题   : " + Xime.Core.HostTextFrame.GetWindowTextSafe(frame));
                    sb.AppendLine("  匹配强度   : " + Xime.Core.HostTextFrame.MatchStrength(frame) +
                        "（2=Edit/RichEdit 强特征 / 1=Afx 弱特征）");
                    sb.AppendLine("  是否可见   : " + (Xime.Core.HostTextFrame.IsVisible(frame) ? "是" : "否"));
                    sb.AppendLine("  窗口宽度   : " + Xime.Core.HostTextFrame.WidthOf(frame) + " px");

                    if (isText)
                    {
                        sb.AppendLine();
                        sb.AppendLine("【警告】定位到了「" + Compat.HostShortName + " 文本窗口」(F2 控制台)，这是错误的窗口！");
                        sb.AppendLine("        投递到它会导致命令栏无任何反应。请把此输出反馈给开发者。");
                    }
                    else
                    {
                        sb.AppendLine();
                        sb.AppendLine("【结论】定位成功。命令栏将可直接接收英文命令（绕过输入法）。");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("机制说明（单通道 UNICODE）：");
                sb.AppendLine("  主通道：SendInput(KEYEVENTF_UNICODE) —— 适配 2013+ 的 WPF 命令栏；");
                sb.AppendLine("          直接送入确定的英文字符，不经过键盘布局、不经过输入法，");
                sb.AppendLine("          也不修改用户的输入法状态。");
                sb.AppendLine("==================================");

                XimeApplication.SafeWriteMessage(sb.ToString());
            }
            catch (System.Exception ex)
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 自检失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 投递实测：XIMETESTKEY
        ///
        /// 向命令栏实际注入字符串 "XIME123"，用于当场验证投递通道是否生效。
        ///
        /// 本版为**单通道**（SendInput KEYEVENTF_UNICODE）。
        /// 执行后请观察命令行输入位置，应出现 XIME123 —— 每个字符只出现一次。
        ///   · 出现一次      → 投递正常、无重复 ✓
        ///   · 出现多次      → 吞键未生效，原生字符与注入叠加 ✗
        ///   · 完全没出现    → 投递通道被特殊环境阻断 ✗
        /// </summary>
        [CommandMethod("XIMETESTKEY", CommandFlags.Modal)]
        public void TestInject()
        {
            try
            {
                EnsureHooked();

                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("===== X输入法 投递实测（单通道 UNICODE）=====");

                var frame = Xime.Core.HostTextFrame.Resolve();
                sb.AppendLine("目标控件 : " + (frame != IntPtr.Zero
                    ? "0x" + frame.ToInt64().ToString("X") + " (" + Xime.Core.HostTextFrame.GetClassNameSafe(frame) + ")"
                    : "定位失败（不影响注入）"));

                // 正式插件使用的唯一通道：SendInput(KEYEVENTF_UNICODE)
                bool sendOk = false;
                foreach (char c in "XIME123")
                    sendOk = Xime.Core.CharInjector.SendUnicodeInput(c);

                sb.AppendLine("SendInput UNICODE : " + (sendOk ? "已投递" : "未投递"));
                sb.AppendLine();
                sb.AppendLine("【请观察】命令行输入位置应出现 XIME123：");
                sb.AppendLine("  各一次  → 投递正常、无重复 ✓");
                sb.AppendLine("  有重复  → 吞键未生效（请执行 XIMEHOOKTEST 进一步确认）");
                sb.AppendLine("  没出现  → 请把本输出反馈给开发者");
                sb.AppendLine("===========================================");

                XimeApplication.SafeWriteMessage(sb.ToString());
            }
            catch (System.Exception ex)
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 投递实测失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 窗口树导出：XIMETREE [深度]
        ///
        /// 把 AutoCAD 主窗口的子孙窗口结构打印出来（类名 / 标题 / 相似度 / 可见 / 宽度），
        /// 用于在自动定位失败或误命中时，人工确认真正的命令栏输入控件是哪一个。
        /// 结果同时写入 %TEMP%\xime_tree.txt，便于完整复制。
        /// </summary>
        [CommandMethod("XIMETREE", CommandFlags.Modal)]
        public void DumpWindowTree()
        {
            try
            {
                EnsureHooked();

                int depth = 6;
                try
                {
                    var ed = Application.DocumentManager.MdiActiveDocument;
                    var res = ed.Editor.GetString("\n[X输入法] 导出窗口树深度 <6>: ");
                    var s = (res.StringResult ?? string.Empty).Trim();
                    int parsed;
                    if (int.TryParse(s, out parsed) && parsed > 0 && parsed <= 12)
                        depth = parsed;
                }
                catch (System.Exception) { }

                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("===== X输入法 窗口树（深度 " + depth + "）=====");

                var mainHwnd = XimeApplication.GetHostMainWindow();
                sb.AppendLine("主窗口 = 0x" + mainHwnd.ToInt64().ToString("X"));
                sb.AppendLine("（格式：句柄 [类名] 强度=… 可见 宽=… \"标题\"）");
                sb.AppendLine();
                sb.Append(Xime.Core.HostTextFrame.DumpTree(mainHwnd, depth));
                sb.AppendLine("=========================================");

                var full = sb.ToString();

                // 同时写文件，避免命令行缓冲截断
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "xime_tree.txt");
                try { System.IO.File.WriteAllText(path, full); }
                catch (System.Exception) { }

                // 命令行里最多显示前 60 行，其余请看文件
                var lines = full.Split('\n');
                int show = System.Math.Min(lines.Length, 60);
                var head = new StringBuilder();
                for (int i = 0; i < show; i++) head.AppendLine(lines[i]);
                if (lines.Length > show)
                    head.AppendLine("…（其余 " + (lines.Length - show) + " 行见文件）");

                head.AppendLine("完整窗口树已写入：\n" + path);
                XimeApplication.SafeWriteMessage(head.ToString());
            }
            catch (System.Exception ex)
            {
                XimeApplication.SafeWriteMessage("\n[X输入法] 导出窗口树失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 吞键诊断：XIMEHOOKTEST
        ///
        /// 验证「低级键盘钩子吞键」在真实 AutoCAD 命令栏上是否生效。
        /// 这是判断字符是否会重复出现的关键 —— 若吞键失效，注入的字符会与
        /// 原生字符叠加，命令栏出现 ll/ii/nn 这类重复。
        ///
        /// 执行后会进入 5 秒的「只吞键、不注入」状态：
        ///   请在倒计时内，**保持 AutoCAD 窗口在前台**并敲几个字母（如 lin）。
        ///   倒计时结束后：
        ///     · 命令栏**没有任何字符**  → 吞键生效 ✓ 注入通道可放心使用；
        ///     · 命令栏**出现了字符**    → 吞键失效 ✗ 需要改用其它方案。
        /// </summary>
        [CommandMethod("XIMEHOOKTEST", CommandFlags.Modal)]
        public void HookSwallowTest()
        {
            try
            {
                EnsureHooked();
                var e = XimeApplication.Engine;
                if (e == null) { XimeApplication.SafeWriteMessage("\n[X输入法] 引擎未启动。"); return; }

                XimeApplication.SafeWriteMessage(
                    "\n[X输入法] 吞键诊断开始：接下来 5 秒处于「只吞键、不注入」状态。" +
                    "\n  请保持 " + Compat.HostShortName + " 在前台，敲几个字母（例如 lin）。");

                int before = e.SwallowCount;
                XimeEngine.SwallowOnly = true;

                // 用 AutoCAD 的定时器/延时（同步等待，Modal 命令下不阻塞 UI 消息循环）
                var end = System.DateTime.Now.AddSeconds(5);
                while (System.DateTime.Now < end)
                {
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                }

                XimeEngine.SwallowOnly = false;
                int swallowed = e.SwallowCount - before;

                XimeApplication.SafeWriteMessage(
                    "\n[X输入法] 吞键诊断结束。钩子共吞掉 " + swallowed + " 次按键。" +
                    "\n【请判断】刚才那 5 秒内，命令栏是否出现了你敲的字母？" +
                    "\n  没有出现 → 吞键生效 ✓（注入通道可放心使用）" +
                    "\n  出现了   → 吞键失效 ✗（请把此结果反馈给开发者）");
            }
            catch (System.Exception ex)
            {
                XimeEngine.SwallowOnly = false;
                XimeApplication.SafeWriteMessage("\n[X输入法] 吞键诊断失败：" + ex.Message);
            }
        }

        #endregion

        #region 命令事件钩子

        /// <summary>
        /// 挂接 AutoCAD 命令事件。
        ///
        /// 使用 CommandWillStart / CommandEnded / CommandCancelled / CommandFailed，
        /// 这是 AutoCAD .NET API 中驱动命令生命周期最直接、最可靠的方式。
        ///
        /// ★★★ v6.3：本方法**必须在加载完成时就调用一次**（见
        ///   <see cref="XimeApplication.Initialize"/>）—— 绝不能"等用户第一次
        ///   敲本插件自己的命令时才挂"。
        ///
        ///   为什么（2026-10-07 现场日志钉死）：
        ///     NETLOAD 之后、用户还没敲过 XIMESTATUS / XIMEBYPASS 之前，
        ///     命令事件是**没挂上**的 ⇒ `OnCommandWillStart` 根本不触发 ⇒
        ///     `_currentCommand` 永远是 null ⇒ 整个「命令期间无视输入法」在
        ///     「开机后的第一条命令」上完全失效（候选窗照弹、命令栏留小写字母、
        ///     要再按一次空格才执行二级命令）。
        ///
        ///   用户实测原话：
        ///     「加载后立马 RYBZ-选块-A，还是在命令栏里面输入小写 a，这时候如果
        ///      我按下空格键会执行二级命令 A。但是按你的测试方案，我加载后先输入
        ///      命令 XIMESTATUS 后测试它就是能正常执行二级命令 A，总不能每次开机
        ///      我都先执行一遍 XIMESTATUS 命令吧，这也太愚蠢了。」
        ///     —— 完全对得上：`XIMESTATUS` 命令体第一行就是本方法。
        ///
        ///   ★ 日志里最硬的证据（那一轮失败测试）：
        ///     `10:53:39 加载 → 10:53:48 Engage → 10:54:03 Disengage`，
        ///     中间 15 秒足够跑完 `ry RYBZ → 选块 → 按 A`，而
        ///     `%TEMP%\xime_startup.log` **一条「解除/还原」都没有**，
        ///     连专门用来抓"该生效却没生效"的 `[旁路诊断]` 也没有 ——
        ///     只能说明 `OnCommandWillStart` 压根没被调用（事件没挂上）。
        /// </summary>
        internal static void EnsureHooked()
        {
            if (XimeApplication.Engine == null) return;
            if (_hooked) return;

            lock (_hookLock)
            {
                if (_hooked) return;

                try
                {
                    var dm = Application.DocumentManager;
                    // 先 `-=` 再 `+=`：即使上一次挂到一半失败过，也不会重复订阅
                    dm.DocumentBecameCurrent -= OnDocumentBecameCurrent;
                    dm.DocumentBecameCurrent += OnDocumentBecameCurrent;

                    // ★ v6.3：当前**已打开的全部文档**都要挂，不能只挂活动文档 ——
                    //   多文档下在别的图纸里执行命令同样必须被看见。
                    //   AttachToDocument 内部先 `-=` 再 `+=`，重复调用不会叠加。
                    foreach (Document d in dm)
                        AttachToDocument(d);
                    AttachToDocument(MdiActiveDocumentSafe());

                    _hooked = true;

                    // ★ 前缀必须是 `[引擎]` —— 这不是"写错了层"：
                    //   本项目的启动日志里，`[引擎] …` 这个前缀的实际含义是
                    //   **「始终记录的关键事件」**（引擎通过 KeyEventSink 写进来的那些行也是它），
                    //   桌面自查工具（xime_check.py）正是按 `[引擎]` 抽取这批关键事件去做判定的。
                    //   换任何别的前缀（例如 `[钩子]`）都会让这一行**看不见** ——
                    //   而"看不见"恰恰是本次要修的那类病（静默失效）。
                    XimeApplication.WriteStartupLog(
                        "[引擎] 命令事件已挂接（CommandWillStart/Ended/Cancelled/Failed）"
                        + " —— 从这一刻起每条命令都会被记录，"
                        + "「命令期间无视输入法」无需先敲任何一条插件命令");
                }
                catch (System.Exception ex)
                {
                    // 挂接失败必须留痕 —— 否则又回到"静默失效"这条老路
                    //（用户表现为"开机后第一条命令失效，敲一次插件命令就好了"）。
                    // 但绝不能因它影响插件加载。
                    XimeApplication.WriteStartupLog("[引擎] 命令事件挂接失败：" + ex.Message);
                }
            }
        }

        private static Document MdiActiveDocumentSafe()
        {
            try { return Application.DocumentManager.MdiActiveDocument; }
            catch (System.Exception) { return null; }
        }

        private static void OnDocumentBecameCurrent(object sender, DocumentCollectionEventArgs e)
        {
            // 切换文档时重新挂接事件并刷新窗口
            try
            {
                AttachToDocument(e.Document);
                if (XimeApplication.Engine != null)
                {
                    XimeApplication.Engine.UpdateAcadWindow(XimeApplication.GetHostMainWindow());
                    XimeApplication.Engine.Ime.ResetCache();
                }
            }
            catch (System.Exception) { }
        }

        /// <summary>把命令事件挂到指定文档上（幂等）。</summary>
        private static void AttachToDocument(Document doc)
        {
            if (doc == null) return;
            try
            {
                // 先移除避免重复
                doc.CommandWillStart -= OnCommandWillStart;
                doc.CommandEnded -= OnCommandEnded;
                doc.CommandCancelled -= OnCommandCancelled;
                doc.CommandFailed -= OnCommandFailed;

                doc.CommandWillStart += OnCommandWillStart;
                doc.CommandEnded += OnCommandEnded;
                doc.CommandCancelled += OnCommandCancelled;
                doc.CommandFailed += OnCommandFailed;
            }
            catch (System.Exception) { }
        }

        private static void OnCommandWillStart(object sender, CommandEventArgs e)
        {
            try
            {
                var engine = XimeApplication.Engine;
                if (engine == null) return;
                engine.OnCommandWillStart(e.GlobalCommandName);
            }
            catch (System.Exception) { }
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            try
            {
                var engine = XimeApplication.Engine;
                if (engine == null) return;
                engine.OnCommandEnded(e.GlobalCommandName, cancelledOrFailed: false);
            }
            catch (System.Exception) { }
        }

        private static void OnCommandCancelled(object sender, CommandEventArgs e)
        {
            try
            {
                var engine = XimeApplication.Engine;
                if (engine == null) return;
                engine.OnCommandCancelled(e.GlobalCommandName);
            }
            catch (System.Exception) { }
        }

        private static void OnCommandFailed(object sender, CommandEventArgs e)
        {
            try
            {
                var engine = XimeApplication.Engine;
                if (engine == null) return;
                engine.OnCommandEnded(e.GlobalCommandName, cancelledOrFailed: true);
            }
            catch (System.Exception) { }
        }

        #endregion

        /// <summary>获取当前活动输入法的名称（用于诊断）。</summary>
        private static string GetActiveImeName()
        {
            try
            {
                var hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "未知";

                var hkl = NativeMethods.GetKeyboardLayout(
                    NativeMethods.GetWindowThreadProcessId(hwnd, out uint _));

                var sb = new StringBuilder(256);
                int immWnd = NativeMethods.ImmGetDefaultIMEWnd(hwnd).ToInt32();

                // 通过 GetKeyboardLayout 的 LANGID 判断是否中文
                int langId = (int)(hkl.ToInt64() & 0xFFFF);
                string culture;
                try { culture = new System.Globalization.CultureInfo(langId).DisplayName; }
                catch (System.Exception) { culture = "0x" + langId.ToString("X"); }

                return culture + " (HKL=0x" + hkl.ToInt64().ToString("X") + ")";
            }
            catch (System.Exception)
            {
                return "获取失败";
            }
        }

        /// <summary>
        /// 模式的中文名。★ 必须**如实**反映传入的枚举值 —— 加载提示、状态查询、
        /// 模式切换提示三处共用这一份文案，任何一处写死都会让用户看到的与实际不符
        /// （v42 修：加载提示曾经把模式写死成"无视输入法模式"）。
        /// </summary>
        internal static string ModeText(XimeWorkMode mode)
        {
            return mode == XimeWorkMode.IgnoreIme ? "无视输入法模式" : "切换输入法模式";
        }
    }
}
