using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using Xime.Core;
using Xime.Core.Native;

// 避免与 System.Windows.Forms.ImeMode 冲突
using ImeMode = Xime.Core.ImeMode;
// 待补切换的处置判据（嵌套类型，只能用别名引用）
using PendingFlush = Xime.Core.XimeEngine.PendingFlushAction;

namespace Xime.CliTest
{
    /// <summary>
    /// X输入法 核心逻辑验证工具（控制台）。
    ///
    /// 用途：在没有 AutoCAD 的环境下验证
    ///   1. IMM32 输入法状态读取与切换是否可用；
    ///   2. 键盘钩子能否正确安装 / 卸载；
    ///   3. 配置读写与命令白名单判定是否正确；
    ///   4. 焦点监控是否能识别窗口状态。
    ///
    /// 用法：
    ///   XimeTest diag          —— 输出环境与输入法诊断信息
    ///   XimeTest mode          —— 读取当前输入法中英模式
    ///   XimeTest to-english    —— 尝试切到英文模式
    ///   XimeTest to-chinese    —— 尝试切到中文模式
    ///   XimeTest hook          —— 测试键盘钩子安装（10 秒后自动退出）
    ///   XimeTest config        —— 测试配置读写与白名单判定
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "diag";

            try
            {
                switch (cmd)
                {
                    case "diag": return RunDiag();
                    case "mode": return RunMode();
                    case "to-english": return RunSwitch(ImeMode.English);
                    case "to-chinese": return RunSwitch(ImeMode.Chinese);
                    case "hook": return RunHookTest();
                    case "hooklive": HookLiveTest.Run(args); return 0;
                    case "wpfprobe": return WpfProbe.Run(args);
                    case "acadauto": return AcadAutoTest.Run(args);
                    case "config": return RunConfigTest();
                    case "configpath": return RunConfigPath(args);
                    case "logo": return RunLogoDiag();
                    case "iconcheck": return IconProbe.Run(args);
                    case "focusdiag": return FocusProbe.Run(args);
                    case "esctest": return EscProbe.Run(args);
                    case "keyprobe": return KeyProbe.Run(args);
                    case "imeprobe": return RunImeProbe();
                    case "bench": return Bench.Run(args);
                    case "ui": UiPreview.Show(); return 0;
                    default:
                        PrintUsage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("执行异常：" + ex);
                return 2;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("X输入法 核心验证工具");
            Console.WriteLine("  diag         环境与输入法诊断");
            Console.WriteLine("  mode         读取当前输入法中英模式");
            Console.WriteLine("  to-english   切到英文模式（不换键盘布局）");
            Console.WriteLine("  to-chinese   切到中文模式");
            Console.WriteLine("  hook         测试键盘钩子安装");
            Console.WriteLine("  config       测试配置读写与白名单");
            Console.WriteLine("  configpath   读取指定的 Xime.config.xml（现场排障）");
            Console.WriteLine("  logo         图标 / Logo 资源加载诊断");
            Console.WriteLine("  iconcheck    窗体标题栏/任务栏图标是否真的挂上了（确定性检查）");
            Console.WriteLine("  focusdiag    焦点窗口解析（PID/TID 混用回归探针）");
            Console.WriteLine("  esctest      设置界面按 Esc 是否真的关闭");
            Console.WriteLine("  keyprobe     按键注入通道探针（UNICODE=VK_PACKET vs ReplayVk=真按键）");
            Console.WriteLine("  imeprobe     输入法通道探针（IMM32 vs WM_IME_CONTROL，切换输入法模式能否用）");
            Console.WriteLine("  ui           预览设置界面（不依赖 AutoCAD）");
        }

        /// <summary>
        /// 输入法通道探针：逐通道列出「读得出中英状态吗」。
        ///
        /// 这是「切换输入法模式」可用性的唯一前提 —— 该模式的全部动作
        /// （进入 CAD 切英文、命令期间切中文、退出还原）都建立在能读出状态之上。
        /// 先在控制台把"这台机器到底行不行"测清楚，再去 CAD 里试，省一轮来回。
        /// </summary>
        private static int RunImeProbe()
        {
            var fg = NativeMethods.GetForegroundWindow();

            Console.WriteLine("===== X输入法 输入法通道探针 =====");
            Console.WriteLine("操作系统      : " + Environment.OSVersion.VersionString);
            Console.WriteLine("前台窗口      : 0x" + fg.ToInt64().ToString("X") +
                              "  类名=" + FocusMonitor.GetWindowClass(fg) +
                              "  标题=" + FocusMonitor.GetWindowText(fg));

            var hkl = NativeMethods.GetKeyboardLayout(0);
            int langId = (int)(hkl.ToInt64() & 0xFFFF);
            Console.WriteLine("键盘布局 HKL  : 0x" + hkl.ToInt64().ToString("X") +
                              " (LangID=0x" + langId.ToString("X") + ")");

            var ime = new ImeController();
            var ch = ime.ProbeChannels(fg);

            Console.WriteLine();
            Console.WriteLine("通道① IMM32（ImmGetConversionStatus）  : " + DescribeMode(ch.ViaImm32));
            Console.WriteLine("通道② IME 消息（WM_IME_CONTROL）        : " + DescribeMode(ch.ViaImeControl)
                              + (ch.ImeWindowAvailable ? "" : "   [IME 窗口未取到]"));

            bool conflict = ch.ViaImm32 != ImeMode.Unknown
                         && ch.ViaImeControl != ImeMode.Unknown
                         && ch.ViaImm32 != ch.ViaImeControl;
            if (conflict)
            {
                Console.WriteLine();
                Console.WriteLine("⚠ 两条通道读数不一致 —— TSF 输入法的典型特征：IMM32 缓存旧状态、IME 消息才是真实状态。");
                Console.WriteLine("  本插件以「IME 消息通道」为准（下方综合结果）。");
            }

            Console.WriteLine("综合结果（GetMode）                    : " + DescribeMode(ime.GetModeOnWindow(fg)));
            Console.WriteLine("前台窗口模式（含候选回退 GetMode）     : " + DescribeMode(ime.GetCurrentMode()));

            Console.WriteLine();
            if (!ch.Usable)
            {
                Console.WriteLine("判定：★ 两条通道都读不出状态 → 「切换输入法模式」在本机无法工作。");
                Console.WriteLine("      请使用「无视输入法模式」（它不依赖此项）。");
                Console.WriteLine("      注意：如果前台窗口不是编辑器窗口，也可能读不出来 ——");
                Console.WriteLine("      请在 AutoCAD 里执行 XIMEPROBE 再确认一次。");
            }
            else if (ch.ViaImm32 == ImeMode.Unknown)
            {
                Console.WriteLine("判定：✓ 可用（IMM32 兼容层被关闭，但 IME 消息通道有效）。");
            }
            else
            {
                Console.WriteLine("判定：✓ 两条通道都可用。");
            }
            Console.WriteLine("==================================");
            return ch.Usable ? 0 : 1;
        }

        /// <summary>诊断用的"是/否"。</summary>
        private static string YesNo(bool b)
        {
            return b ? "✓ 是" : "✗ 否";
        }

        /// <summary>
        /// 读取并打印指定配置文件 —— 用于现场排障（用户把自己的 Xime.config.xml 发过来时）。
        ///
        /// 用法：XimeTest configpath "C:\...\Xime.config.xml"
        /// </summary>
        private static int RunConfigPath(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法：XimeTest configpath <Xime.config.xml 的完整路径>");
                return 1;
            }

            var p = args[1];
            Console.WriteLine("配置文件      : " + p);
            Console.WriteLine("存在          : " + (System.IO.File.Exists(p) ? "是" : "否"));

            string fileEnabled = "(无此节点)", fileByUser = "(无此节点)";
            if (System.IO.File.Exists(p))
            {
                var raw = System.IO.File.ReadAllText(p);
                fileEnabled = ExtractTag(raw, "Enabled");
                fileByUser = ExtractTag(raw, "EnabledByUser");
                Console.WriteLine("文件里的 <Enabled>       : " + fileEnabled);
                Console.WriteLine("文件里的 <EnabledByUser> : " + fileByUser);
            }

            var cfg = XimeConfig.Load(p);
            Console.WriteLine("加载后 Enabled   : " + cfg.Enabled);
            Console.WriteLine("加载后 EnabledByUser : " + cfg.EnabledByUser);
            Console.WriteLine("WorkMode      : " + cfg.WorkMode +
                              (XimeEngine.IsSupportedWorkMode(cfg.WorkMode)
                                  ? "（已实现，插件会按该模式工作）"
                                  : "（**未知模式**，引擎会拒绝接管）"));
            Console.WriteLine("命令白名单数  : " + cfg.ChineseCommands.Count);
            Console.WriteLine();
            Console.WriteLine("  → 判定：" + (cfg.Enabled
                              ? "启用（插件加载后即可用）"
                              : "★ 关闭（用户曾显式关过，会一直被尊重；"
                                + "执行 XIMEON 或界面里勾选「启用」可恢复）"));
            return 0;
        }

        /// <summary>从 XML 文本里粗取某个标签的内容（仅用于诊断输出，不解析结构）。</summary>
        private static string ExtractTag(string xml, string tag)
        {
            try
            {
                // ★ 先剥掉注释再找 —— 配置文件里会用注释解释同名标签的语义
                //   （例如「仅当 <EnabledByUser>true</EnabledByUser> 时生效」），
                //   直接 IndexOf 会命中注释里的那一个，诊断输出就变成假的。
                var clean = System.Text.RegularExpressions.Regex.Replace(
                    xml, "<!--.*?-->", string.Empty,
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                var open = clean.IndexOf("<" + tag + ">", StringComparison.Ordinal);
                if (open < 0) return "(无此节点)";
                open += tag.Length + 2;
                var close = clean.IndexOf("</" + tag + ">", open, StringComparison.Ordinal);
                if (close < 0) return "(节点未闭合)";
                return clean.Substring(open, close - open).Trim();
            }
            catch (Exception) { return "(读取失败)"; }
        }

        /// <summary>图标 / Logo 资源诊断：与 AutoCAD 里的 XIMELOGO 命令同一份逻辑。</summary>
        private static int RunLogoDiag()
        {
            Console.WriteLine(Xime.Core.UI.XimeResources.Diagnose());

            var icon = Xime.Core.UI.XimeResources.AppIcon;
            var logo = Xime.Core.UI.XimeResources.Logo;
            return (icon != null && logo != null) ? 0 : 1;
        }

        private static int RunDiag()
        {
            Console.WriteLine("===== X输入法 环境诊断 =====");
            Console.WriteLine("操作系统      : " + Environment.OSVersion.VersionString);
            Console.WriteLine("CLR 版本      : " + Environment.Version);
            Console.WriteLine("进程架构      : " + (IntPtr.Size == 8 ? "x64" : "x86"));
            Console.WriteLine("64 位系统     : " + (Environment.Is64BitOperatingSystem ? "是" : "否"));

            var fg = NativeMethods.GetForegroundWindow();
            Console.WriteLine("前台窗口句柄  : 0x" + fg.ToInt64().ToString("X"));

            uint pid;
            NativeMethods.GetWindowThreadProcessId(fg, out pid);
            Console.WriteLine("前台进程 ID   : " + pid);

            var ime = new ImeController();
            var mode = ime.GetCurrentMode();
            Console.WriteLine("输入法中英模式: " + DescribeMode(mode));

            // CapsLock 状态
            // ★ 两个通道都打印 —— 它们**语义不同**，混用是 v34 的真凶（见 NativeMethods.IsCapsLockOn）：
            //   · GetAsyncKeyState = 全局实时状态（本插件采用）；
            //   · GetKeyState      = **调用线程消息队列**里的快照，在钩子线程上可能永远停在
            //                        线程创建那一刻 → 恒报"开启" → 无视输入法模式完全失效。
            int asyncCaps = NativeMethods.GetAsyncKeyState(NativeMethods.VK_CAPITAL);
            int queueCaps = NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL);
            Console.WriteLine("CapsLock      : " + (NativeMethods.IsCapsLockOn() ? "开启" : "关闭")
                + "   [GetAsyncKeyState=" + (asyncCaps & 1) + "  GetKeyState=" + (queueCaps & 1)
                + (asyncCaps != queueCaps ? "  ← 两者不一致！" : "") + "]");

            // 键盘布局
            var hkl = NativeMethods.GetKeyboardLayout(0);
            int langId = (int)(hkl.ToInt64() & 0xFFFF);
            Console.WriteLine("键盘布局 HKL  : 0x" + hkl.ToInt64().ToString("X") + " (LangID=0x" + langId.ToString("X") + ")");

            Console.WriteLine();
            Console.WriteLine("说明：");
            Console.WriteLine("  English = 输入法处于英文模式，命令栏可直接输入英文");
            Console.WriteLine("  Chinese = 输入法处于中文模式");
            Console.WriteLine("  Unknown = 当前输入法不响应 IMM32 接口（如部分自绘输入法），");
            Console.WriteLine("            将自动改用键盘钩子兜底方案。");
            return 0;
        }

        private static int RunMode()
        {
            var ime = new ImeController();
            var mode = ime.GetCurrentMode();
            Console.WriteLine("当前输入法中英模式：" + DescribeMode(mode));
            return 0;
        }

        private static int RunSwitch(ImeMode target)
        {
            var ime = new ImeController();

            Console.WriteLine("切换前：" + DescribeMode(ime.GetCurrentMode()));
            Console.WriteLine("目标  ：" + DescribeMode(target));

            var hwnd = NativeMethods.GetForegroundWindow();
            bool ok = ime.SwitchTo(hwnd, target);

            Console.WriteLine("切换结果：" + (ok ? "成功" : "失败"));
            Console.WriteLine("切换后：" + DescribeMode(ime.GetCurrentMode()));
            return ok ? 0 : 1;
        }

        private static int RunHookTest()
        {
            Console.WriteLine("测试键盘钩子安装...");

            var hook = new KeyboardHook();
            hook.ShouldCapture = (vk, down) => false;   // 不拦截任何键，仅测试安装
            hook.OnCaptured = (vk, down) => false;

            bool ok = hook.Install();
            Console.WriteLine("钩子安装：" + (ok ? "成功" : "失败 (GetLastError 可能非 0)"));

            if (!ok)
            {
                Console.WriteLine("提示：低级键盘钩子需要在有消息循环的线程安装。");
                Console.WriteLine("      本工具为控制台程序，仍会创建消息循环进行验证。");
            }

            hook.Uninstall();
            Console.WriteLine("钩子已卸载。");
            hook.Dispose();
            return ok ? 0 : 1;
        }

        private static int RunConfigTest()
        {
            Console.WriteLine("===== 配置与白名单测试 =====");

            var cfg = new XimeConfig();
            cfg.ConfigPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Xime.test.config.xml");

            Console.WriteLine("默认命令数：" + cfg.ChineseCommands.Count);

            // 白名单判定测试
            var cases = new[]
            {
                // AutoCAD 原生
                "TEXT", "text", "_TEXT", "-LAYER", "MTEXT", "DDEDIT",
                "LINE", "CIRCLE", "T81_TTEXT", "SOMEUNKNOWN",
                // 双击对象编辑（原生 DDEDIT / 天正 TOBJEDIT）
                "TOBJEDIT", "tobjedit", "T73_TOBJEDIT", "T20_TObjEdit", "OBJEDIT",
                // 天正（别名）
                "DHWZ", "dhwz", "TMTEXT", "ZYWZ", "DQWZ", "XGWZ", "DD",
                "WZZH", "WZHB", "WZYS", "CZTH", "YCBZ", "ZFBZ", "TMBZ",
                // 天正（注册名，带命名空间前缀）
                "T73_TMText", "T73_TText", "T73_TStyleEx", "T73_TTextConv",
                "T20_TMText", "T81_TMText",
                // 第三方批注
                "GMNOTE", "GMBZ", "MxET_DrawComment",
                // 绘图类（必须保持英文，验证不误伤）
                "PLINE", "SPLINE", "RECTANG", "HATCH", "OFFSET", "MIRROR",
                "TRIM", "ARRAY", "ZOOM", "SAVE"
            };

            Console.WriteLine();
            Console.WriteLine("白名单判定：");
            foreach (var c in cases)
            {
                Console.WriteLine(string.Format("  {0,-14} -> {1}",
                    c, cfg.NeedChinese(c) ? "需要中文" : "保持英文"));
            }

            // ★ 误伤断言：这些命令**全程不需要中文**，必须保持英文。
            //   RYBZ（任意布置）曾被两字母词根 "bz" 兜底误判为需要中文，
            //   导致命令期间输入法被切成中文，选中块后敲二级命令
            //   （A 旋转90° / E 放大 / D 缩小 / F 左右翻转 / X·Y 偏移）
            //   全部失灵 —— AutoCAD 2022 + 天正 T20 v10 上用户实测复现。
            Console.WriteLine();
            Console.WriteLine("误伤检查（必须保持英文）：");
            int misfire = 0;
            foreach (var c in new[]
            {
                "RYBZ",      // 天正 任意布置
                "TZHMOVE",   // 通用移动
                "OFFSET",
                "LINE",
                "CIRCLE"
            })
            {
                bool bad = cfg.NeedChinese(c);
                Console.WriteLine(string.Format("  [{0}] {1,-12} -> {2}",
                    bad ? "★ 失败" : "通过", c, bad ? "需要中文（误伤！）" : "保持英文"));
                if (bad) misfire++;
            }
            if (misfire > 0) return 1;

            // 保存 / 读取
            cfg.Enabled = true;
            cfg.WorkMode = XimeWorkMode.IgnoreIme;
            cfg.Save();
            Console.WriteLine();
            Console.WriteLine("已保存配置到：" + cfg.ConfigPath);

            var loaded = XimeConfig.Load(cfg.ConfigPath);
            Console.WriteLine("重新读取：启用=" + loaded.Enabled +
                              " 模式=" + loaded.WorkMode +
                              " 命令数=" + loaded.ChineseCommands.Count);

            try { System.IO.File.Delete(cfg.ConfigPath); } catch (Exception) { }

            return RunEnabledSemanticsTest();
        }

        /// <summary>
        /// 启用状态语义矩阵测试。
        ///
        /// 规则（这条语义来回改过两次，用测试钉死，防止再被误改）：
        ///   · 用户**曾显式关/开过**（EnabledByUser=true）→ 永远尊重文件里的值；
        ///   · 从未开关过（EnabledByUser=false）→ **一律启用**。
        ///
        /// ★ v25 改动：去掉了「ConfigVersion &lt; 2」这个条件。
        ///   旧规则靠版本号判断「false 是历史默认值还是用户意图」，一旦文件
        ///   版本已升到 2、又带着 Enabled=false 且缺 EnabledByUser 节点，
        ///   就会**永久停在关闭状态** —— 用户两次反馈「加载后不是默认启动」。
        ///   现在只认 EnabledByUser 这一个证据，规则简单到不会被带偏。
        /// </summary>
        private static int RunEnabledSemanticsTest()
        {
            Console.WriteLine();
            Console.WriteLine("===== 启用状态语义矩阵 =====");

            int pass = 0, fail = 0;
            void Check(string what, bool ok)
            {
                Console.WriteLine("  [" + (ok ? "通过" : "★ 失败") + "] " + what);
                if (ok) pass++; else fail++;
            }

            // 无配置文件 → 新装默认启用
            var none = XimeConfig.Load(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Xime.__not_exist__.xml"));
            Check("新装（无配置文件）应默认启用", none.Enabled);

            // (版本, 文件里的 Enabled, EnabledByUser, 期望加载后)
            var matrix = new[]
            {
                new object[] { 2, true,  true,  true,  "v2 + 开过   → 永远尊重，保持开启" },
                new object[] { 2, false, true,  false, "v2 + 关过   → 永远尊重，保持关闭" },
                new object[] { 1, false, true,  false, "v1 + 关过   → 旧版本也不覆盖用户意图" },
                new object[] { 1, false, false, true,  "v1 + 没关过 → 旧默认值，启用" },
                new object[] { 2, false, false, true,  "v2 + 没关过 + 文件里 false → 启用（v25 关键修复）" },
                new object[] { 2, true,  false, true,  "v2 + 没关过 + 文件里 true  → 启用" }
            };

            foreach (var m in matrix)
            {
                int ver = (int)m[0];
                bool enabled = (bool)m[1];
                bool byUser = (bool)m[2];
                bool expect = (bool)m[3];
                string what = (string)m[4];

                var path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "Xime.semantic.test.xml");
                try
                {
                    System.IO.File.WriteAllText(path, string.Format(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Xime>\r\n" +
                        "  <ConfigVersion>{0}</ConfigVersion>\r\n" +
                        "  <Enabled>{1}</Enabled>\r\n" +
                        "  <EnabledByUser>{2}</EnabledByUser>\r\n" +
                        "  <WorkMode>IgnoreIme</WorkMode>\r\n" +
                        "  <ChineseCommands><Command>text</Command></ChineseCommands>\r\n" +
                        "</Xime>\r\n",
                        ver, enabled ? "true" : "false", byUser ? "true" : "false"),
                        new System.Text.UTF8Encoding(false));

                    var loaded = XimeConfig.Load(path);
                    Check(what + "  （期望 " + expect + "，实际 " + loaded.Enabled + "）",
                          loaded.Enabled == expect);
                }
                finally
                {
                    try { System.IO.File.Delete(path); } catch (Exception) { }
                }
            }

            var lifecycleFail = RunLoadLifecycleTest(Check);
            var passThroughFail = RunPassThroughTest(Check);
            var workModeFail = RunWorkModeTest(Check);
            var bypassFail = RunImeBypassTest(Check);
            var mannerFail = RunSwitchMannerTest(Check);
            var targetFail = RunImeTargetClassTest(Check);
            var hostFail = RunAcadFrameHostTest(Check);
            var logFail = RunLogSafetyTest(Check);

            Console.WriteLine();
            Console.WriteLine("合计: " + pass + " 项通过, " + fail + " 项失败");
            return (fail == 0 && lifecycleFail == 0 && passThroughFail == 0
                    && workModeFail == 0 && bypassFail == 0 && mannerFail == 0
                    && targetFail == 0 && hostFail == 0 && logFail == 0) ? 0 : 1;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        /// <summary>
        /// 「IME 靶点窗口类名」判据的可执行反证（v35 新增）。
        ///
        /// ★★ 本轮实机事故（v34 日志）：
        ///     18:52:06.241  CommandWillStart: TTEXT（需要中文）
        ///     18:52:06.344  切换输入法（需要中文）：探测已是 Chinese，无需动作（窗口=0x3C24D2）
        ///   0x3C24D2 是 AutoCAD 的**绘图区**窗口（类名 `Afx:00007FF...`）。
        ///   IME 上下文不在它身上，`ImmGetContext` 给出的是兼容层**默认值**（恒为中文），
        ///   于是"需要中文"永远被判成"已经是中文，无需动作" → TTEXT 里打不出中文。
        ///
        ///   这几条断言把"哪些类名算真正承接受输入的控件"钉死 ——
        ///   尤其是把绘图区 `Afx:...`（带冒号）与 MFC 编辑控件 `AfxWnd...` 区分开，
        ///   这个区别只看前缀 "Afx" 是分不出来的，靠注释也记不住。
        /// </summary>
        private static int RunImeTargetClassTest(Action<string, bool> check)
        {
            Console.WriteLine();
            Console.WriteLine("===== IME 靶点窗口类名判据（FocusMonitor.IsEditBoxClass）=====");

            int fail = 0;
            void Check(string what, bool ok)
            {
                check(what, ok);
                if (!ok) fail++;
            }

            // ---- 必须是"承接受输入的控件"（有 IME 上下文，读数可信）----
            // 注：AutoCAD 的**在位文字编辑器**类名就是 `Edit`（现场日志实测），
            //     命令栏控件虽然也是 WindowsForms10.EDIT，但它走的是靶点解析的
            //     第 ② 条（AcadTextFrame.Resolve()），不依赖这里的类名判据。
            var hosts = new[]
            {
                new object[] { "Edit", "AutoCAD 在位文字编辑器（现场日志实测就是这个类名）" },
                new object[] { "RichEdit20W", "RichEdit" },
                new object[] { "HwndWrapper[DefaultDomain;;f4d2d347-6fc8-4b08-b685-ca02feb96650]", "AutoCAD 命令栏宿主（HwndWrapper）" },
                new object[] { "AfxWnd140u", "MFC 编辑控件（天正对话框）" }
            };
            foreach (var h in hosts)
            {
                var cls = (string)h[0];
                var what = (string)h[1];
                Check("是编辑控件: " + cls + "（" + what + "）",
                      FocusMonitor.IsEditBoxClass(cls));
            }

            // ---- ★ 绝不能当 IME 靶点的窗口（没有 IME 上下文，读数恒为默认「中文」）----
            var notHosts = new[]
            {
                new object[] { "Afx:00007FF642700000:28:0000000000000000:0000000000000002:000000000A170F33",
                               "★ 本轮真凶：AutoCAD 绘图区（Afx: 带冒号）" },
                new object[] { "AfxMDIFrame100u",                     "★ AutoCAD 主窗口（MFC 框架）" },
                new object[] { "MDIClient",                           "MDI 客户区" },
                new object[] { "#32770",                              "模态对话框" },
                new object[] { "AfxFrameOrView140u",                  "MFC 视图框架" },
                new object[] { "WindowsForms10.RichEdit20W.app.0.a3e249_r63_ad1",
                               "★ 本插件面板的 RichEdit（必须排除，否则靶点又跑到面板上）" },
                new object[] { "WindowsForms10.EDIT.app.0.a3e249_r63_ad1",
                               "★ 本插件面板的 EDIT（同上；AutoCAD 命令栏控件走靶点解析第 ② 条）" },
                new object[] { "ComboBox",                            "内嵌 Edit 子控件挂在它的子窗口上" },
                new object[] { "",                                    "空类名" }
            };
            foreach (var c in notHosts)
            {
                var cls = (string)c[0];
                var what = (string)c[1];
                Check("不是编辑控件: " + (cls.Length == 0 ? "(空)" : cls) + "（" + what + "）",
                      !FocusMonitor.IsEditBoxClass(cls));
            }

            // ---- ★ 契约：靶点读数不可信时，绝不因此判定"已经是目标态" ----
            Console.WriteLine();
            Console.WriteLine("----- 不可信靶点的读数契约（ImeController.SwitchTo）-----");

            var ime = new ImeController();
            var hwnd = FindWindow("Shell_TrayWnd", null);   // 稳定存在、且肯定不是 IME 承接窗口
            if (hwnd == IntPtr.Zero)
            {
                Check("（前置）找得到用于对照的系统窗口 Shell_TrayWnd", false);
            }
            else
            {
                var probe = ime.GetModeOnWindow(hwnd);
                if (probe == ImeMode.Unknown)
                {
                    // 读不出来 → 无法做"采信 vs 不采信"的对照，但"不可信"路径仍可断言
                    bool r2 = ime.SwitchTo(hwnd, ImeMode.Chinese, null, false, () => false,
                                           true, out var m2);
                    Check("靶点不可信 + 不能发真实按键 → 必须报失败（不得凭空记成已达成）",
                          !r2 && m2 == ImeController.ImeSwitchManner.Failed);
                }
                else
                {
                    // ① 采信它 → 会被误判成"已经是目标态"（**这正是 v35 的 bug 机制**）
                    bool r1 = ime.SwitchTo(hwnd, probe, null, false, () => false,
                                           false, out var m1);
                    Check("（背景）采信不可信靶点的读数 → 被误判为「已经是目标态」"
                          + "（读数=" + probe + "，这就是 v35 的 bug 机制）",
                          r1 && m1 == ImeController.ImeSwitchManner.AlreadyAtTarget);

                    // ② ★ 不采信 → 同一读数、同一目标，结论必须反过来
                    bool r2 = ime.SwitchTo(hwnd, probe, null, false, () => false,
                                           true, out var m2);
                    Check("★ 靶点不可信 → 同一读数、同一目标，绝不判成「已达成」（结论必须反转）",
                          !r2 && m2 == ImeController.ImeSwitchManner.Failed);
                }
            }

            return fail;
        }

        /// <summary>
        /// 「命令栏控件解析必须要求**强特征**」的可执行反证（v36 新增）。
        ///
        /// ★★ 本轮实机事故（v35 日志，已确证）：
        ///     19:03:53.487  Engage：进入接管，CAD窗口=0x1F111E 命令栏控件=0x2C0940
        ///                   类名=Afx:00007FF642700000:28:...:110A06F3
        ///     19:03:53.490  焦点变化: 0x2C0940 类名=Afx:...:28:... 是编辑框=False 是命令栏=False
        ///     19:04:26.611  切换输入法（需要中文（文字输入））：探测已是 Chinese，无需动作（窗口=0x2C0940）
        ///   0x2C0940 是 AutoCAD 的**绘图区**（`FocusMonitor` 当时就明确判它「是编辑框=False」），
        ///   但 <c>AcadTextFrame.Resolve()</c> 判的是 <c>MatchStrength &gt; 0</c>（**弱特征也算**），
        ///   于是把它当成命令栏控件返回并缓存 3 秒；`ResolveAcadImeTarget` 第 ② 条
        ///   又无条件采信 <c>Resolve()</c> 的返回值并置 `trustworthy = true`，
        ///   结果 IME 靶点落到一个**没有 IME 上下文**的窗口上 —— 读数是兼容层
        ///   **默认值**（恒为「中文」）。于是"需要中文"永远被判成"已经是中文，无需动作"：
        ///   「探测已是 Chinese，无需动作」在日志里出现 6 次，用户一次都没切成。
        ///
        ///   这几条断言把"强弱特征的分界"钉死，防止日后又把 `Afx:...` 放回来 ——
        ///   靠注释是记不住的（`Afx:` 与 `AfxWnd...` 只差几个字符，语感上都是"Afx 框架"）。
        /// </summary>
        private static int RunAcadFrameHostTest(Action<string, bool> check)
        {
            Console.WriteLine();
            Console.WriteLine("===== 命令栏控件解析特征（AcadTextFrame.MatchStrength / IsTextInputHost）=====");

            int fail = 0;
            void Check(string what, bool ok)
            {
                check(what, ok);
                if (!ok) fail++;
            }

            const string DRAWING_AREA =
                "Afx:00007FF642700000:28:0000000000000000:0000000000000002:00000000110A06F3";

            // ---- ★ 本轮真凶：绘图区必须被"强特征"拒之门外 ----
            Check("★ 绘图区 Afx:...:28:... → 不算文本输入宿主（v35 事故窗口）",
                  !AcadTextFrame.IsTextInputHostClass(DRAWING_AREA));
            Check("★ 绘图区 → 但仍算「弱特征」（所以旧的 IsEditLike 会命中它，这正是 bug 的来源）",
                  AcadTextFrame.MatchStrength(DRAWING_AREA) > 0);
            Check("★ 绘图区打分为弱特征 1（强特征阈值是 >= 2）",
                  AcadTextFrame.MatchStrength(DRAWING_AREA) == 1);

            // ---- 其余必须排除的 AutoCAD 框架窗口 ----
            foreach (var cls in new[]
                     {
                         "AfxMDIFrame100u",
                         "MDIClient",
                         "Afx:00007FF642700000:b:0000000000010003:0000000000000006:000000002A190157",
                         "#32770",
                         "ComboBox",
                         ""
                     })
            {
                Check("不是文本输入宿主: " + (cls.Length == 0 ? "(空)" : cls),
                      !AcadTextFrame.IsTextInputHostClass(cls));
            }

            // ---- 必须认得出来的真输入控件（AutoCAD 命令栏 / 在位编辑器）----
            foreach (var cls in new[]
                     {
                         "Edit",                                            // 在位文字编辑器（现场日志实测）
                         "RichEdit20W",
                         "WindowsForms10.RichEdit20W.app.0.a3e249_r60_ad1",  // 命令栏内的 RichEdit
                         "WindowsForms10.EDIT.app.0.a3e249_r60_ad1",         // 命令栏控件（日志实见）
                         "HwndWrapper[DefaultDomain;;1b285baa-47a1-496e-be16-3ceb3df22306]"
                     })
            {
                Check("是文本输入宿主: " + cls, AcadTextFrame.IsTextInputHostClass(cls));
            }

            Check("强特征阈值：MatchStrength(\"Edit\") == 2", AcadTextFrame.MatchStrength("Edit") == 2);
            Check("强特征阈值：MatchStrength(\"AfxMDIFrame100u\") == 1（弱特征，够不上宿主）",
                  AcadTextFrame.MatchStrength("AfxMDIFrame100u") == 1);

            // ---- ★ 与 FocusMonitor.IsEditBoxClass 的**有意差异**（必须记下来，否则会被当成 bug 修掉）----
            //   差异：AcadTextFrame 判 "含 edit 子串" 即为强特征，FocusMonitor 则要求类名明确、
            //         不接受 WindowsForms10.* 前缀。
            //   为什么不能统一成 FocusMonitor 那一套：AutoCAD 的命令栏控件**就是**
            //   `WindowsForms10.EDIT...`（日志实见），靶点解析第 ② 条要求它必须通过
            //   强特征判据；本插件自己面板里的同名控件由 `!IsOwnWindow(...)` 挡掉，
            //   不靠类名区分。若统一成 FocusMonitor 的判据，命令栏靶点会全部落空。
            const string WINFORMS_EDIT = "WindowsForms10.EDIT.app.0.a3e249_r60_ad1";
            Check("★ 有意差异：AcadTextFrame 认 WindowsForms10.EDIT 为宿主（命令栏需要它）",
                  AcadTextFrame.IsTextInputHostClass(WINFORMS_EDIT));
            Check("★ 有意差异：FocusMonitor 不认 WindowsForms10.EDIT（它只服务编辑态判定）",
                  !FocusMonitor.IsEditBoxClass(WINFORMS_EDIT));

            return fail;
        }

        /// <summary>
        /// 「调试日志开关不许抹掉现场」的可执行反证（v39 新增）。
        ///
        /// ★★ 事故：用户跑完一轮 v37 实机测试（19:32~19:36），然后敲 `XIMEDEBUG`
        ///   想看日志 —— 而旧实现用 `File.WriteAllText` 打开日志文件，
        ///   把整份现场**覆盖成一行标题**，只剩 2892 字节的会话尾巴
        ///   （还正好是"切模式 + 退 出CAD"）。故障恰好在"用户想看日志"的那一刻被抹掉，
        ///   整个诊断轮次只能作废。
        ///
        /// 契约：**再次开启日志只能追加，绝不能删旧内容。**
        /// 这条一旦回退，损失不可挽回（现场只存在于那一瞬间），所以必须用断言钉住。
        /// </summary>
        private static int RunLogSafetyTest(Action<string, bool> check)
        {
            Console.WriteLine();
            Console.WriteLine("===== 调试日志开关的安全性（不许抹掉现场）=====");

            int fail = 0;
            void Check(string what, bool ok)
            {
                check(what, ok);
                if (!ok) fail++;
            }

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "xime_logtest_" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "xime_engine.log");

                // 模拟"上一轮实机测试留下的现场"
                const string EARLIER = "EARLIER-SCENE-本轮测试现场-TAIL";
                System.IO.File.WriteAllText(path, EARLIER + "\r\n");

                XimeEngine.PrepareLogFile(path);      // ← 相当于用户敲 XIMEDEBUG
                var after = System.IO.File.ReadAllText(path);

                Check("★ 再次开启日志后，上一轮现场必须还在（旧实现会整份抹掉）",
                      after.IndexOf(EARLIER, StringComparison.Ordinal) >= 0);
                Check("★ 开启日志会写一条会话分隔/标题（便于区分两段）",
                      after.IndexOf("引擎日志", StringComparison.Ordinal) >= 0);
                Check("★ 旧现场必须排在新会话标题之前（说明是追加，不是重写）",
                      after.IndexOf(EARLIER, StringComparison.Ordinal) >= 0
                      && after.IndexOf(EARLIER, StringComparison.Ordinal)
                         < after.IndexOf("引擎日志", StringComparison.Ordinal));
                Check("首次创建时写入标题（文件不存在也能开日志）",
                      System.IO.File.Exists(path));

                // ── v45：并发写入**不许丢行** ──────────────────────────────────
                //
                // ★★ 这是本轮实机事故的可执行反证。
                //   旧实现每次 `Dbg` 直接 `File.AppendAllText`（FileShare.Read），
                //   多个线程同时写会撞车 → IOException → 被 `catch {}` 静默吞掉 →
                //   **整整一行凭空消失**。而 Dbg 的调用方至少来自四个线程：
                //   键盘钩子回调、焦点事件回调、500ms 轮询、线程池切换任务。
                //   危害：① 排查时把"丢行"当成"插件没动作"；
                //         ② "全日志 0 次 结果=False" 这种计数结论可能是**假阴性**。
                //
                //   下面这条断言在旧实现上**必红**（实测会丢几十行），
                //   它就是"日志再也不会悄悄丢行"的证明。
                var racePath = System.IO.Path.Combine(dir, "race.log");
                const int Threads = 8, PerThread = 300;
                var threads = new System.Threading.Thread[Threads];
                for (int t = 0; t < Threads; t++)
                {
                    int id = t;
                    threads[t] = new System.Threading.Thread(delegate ()
                    {
                        for (int k = 0; k < PerThread; k++)
                            XimeEngine.TryAppendLogLine(racePath,
                                "T" + id + "-" + k.ToString() + "\r\n");
                    });
                    threads[t].Start();
                }
                foreach (var th in threads) th.Join();

                int expect = Threads * PerThread;
                int actual = System.IO.File.ReadAllLines(racePath).Length;
                Check("★★ 多线程并发写日志不许丢行（" + Threads + " 线程 × " + PerThread
                      + " 行 = " + expect + " 行，旧实现会丢）",
                      actual == expect);
                Check("★ 丢失计数器必须为 0（实测 " + XimeEngine.DebugLogDropped + " 行），"
                      + "它是「日志本身丢过行」的唯一可见信号",
                      XimeEngine.DebugLogDropped == 0);
            }
            catch (Exception ex)
            {
                Check("日志安全测试执行异常：" + ex.Message, false);
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch (Exception) { }
            }

            return fail;
        }

        /// <summary>
        /// 「达成方式」可信度的契约测试（v34 新增）。
        ///
        /// ★★ 这是 v33 实机事故的**可执行反证**：
        ///   现场日志显示
        ///     切换输入法（进入 CAD）：Chinese → English 结果=True 可发真实按键=False
        ///   那次"成功"其实是走了**直设通道**（<c>IMC_SETCONVERSIONMODE</c> /
        ///   <c>ImmSetConversionStatus</c>），它对 TSF / 国产输入法报成功但真实状态不变。
        ///   调用方却把它记成"已处于英文"，此后每一次英文请求都在
        ///   「本模式已置于 English → 无需动作」处被短路 —— 再也没有发过那唯一有效的
        ///   真实 Shift。用户看到的就是「切换输入法模式还是不行」。
        ///
        ///   这几条断言把"ImeSwitchManner 的可信度分类"钉死，防止日后有人
        ///   顺手把 DirectSet 也算成成功（那样上面这个 bug 会原样回来，而且更难查）。
        /// </summary>
        private static int RunSwitchMannerTest(Action<string, bool> check)
        {
            Console.WriteLine();
            Console.WriteLine("===== 输入法切换达成方式（ImeSwitchManner）=====");

            int fail = 0;
            void Check(string what, bool ok)
            {
                check(what, ok);
                if (!ok) fail++;
            }

            var T = ImeController.ImeSwitchManner.AlreadyAtTarget;
            var R = ImeController.ImeSwitchManner.RealKey;
            var D = ImeController.ImeSwitchManner.DirectSet;
            var F = ImeController.ImeSwitchManner.Failed;
            var W = ImeController.ImeSwitchManner.WeaselCli;   // ★ v56

            // 可信来源：只有这三种能写入"当前确信状态"
            Check("探测确认已是目标 → 可信（可记为已达成）",
                  ImeController.IsReliableManner(T));
            Check("真的发出了模拟 Shift → 可信（与用户手动按 Shift 等价）",
                  ImeController.IsReliableManner(R));
            Check("★ v56 小狼毫命令行直接设值 → 可信（幂等设值，不是翻转，不存在'重发会切反'）",
                  ImeController.IsReliableManner(W));

            // ★ 本轮根因：直设"成功"绝不能被当成已达成
            Check("★ 直设转换状态（DirectSet）→ 不可信（必须登记待补，等能发真实按键时补做）",
                  !ImeController.IsReliableManner(D));
            Check("失败 → 不可信", !ImeController.IsReliableManner(F));

            // 语义不得混淆：五个取值必须互不相同（防止有人把它们写成同值后"全可信"）
            var all = new[] { T, R, D, F, W };
            var uniq = new System.Collections.Generic.HashSet<int>();
            foreach (var m in all) uniq.Add((int)m);
            Check("五个达成方式取值互不相同（不会被折叠成同一个值）", uniq.Count == 5);

            // ★ v56 负向对照：把 WeaselCli 从可信集合里摘掉 → 上面那条"可信"断言必须变红。
            //   （用等价表达式复现旧实现，证明该断言真的在盯 IsReliableManner 的实现。）
            bool reliableBeforeV56 =
                (W == ImeController.ImeSwitchManner.AlreadyAtTarget)
                || (W == ImeController.ImeSwitchManner.RealKey);
            Check("★ v56 负向对照：旧实现（只认 AlreadyAtTarget/RealKey）确实判小狼毫命令行**不可信** —— "
                  + "证明上面那条断言真的钉在实现上，而不是恒真",
                  !reliableBeforeV56 && ImeController.IsReliableManner(W));

            // ---- 小狼毫命令行桥的安全契约（v56）----
            Console.WriteLine();
            Console.WriteLine("----- 小狼毫命令行桥（WeaselBridge）安全契约 -----");
            Console.WriteLine("  （探测结果：IsAvailable=" + WeaselBridge.IsAvailable
                              + " 路径=" + (WeaselBridge.ServerPath ?? "<未找到>") + "）");

            // ★ 契约：目标态必须是明确的中/英之一。未知态**绝不能**去动用户的输入法 ——
            //   否则会拿着一个没定义的语义去切。这条不依赖机器上是否装了输入法。
            //
            //   ★★ 注意：这里**只做否定性检查**（Unknown → 必须拒绝），**绝不**在自测里
            //     真的发 `/ascii` 或 `/nascii` —— 那会把跑测试的人自己的输入法切走。
            //     桥的"正向可用性"由现场日志（`切换输入法（小狼毫命令行直接设值）` 行）验证。
            string why;
            Check("★ 目标态 Unknown → 一律拒绝（绝不拿未定义的目标去动输入法）",
                  !WeaselBridge.TrySetAscii(ImeMode.Unknown, out why)
                  && !string.IsNullOrEmpty(why));
            Check("★ 拒绝时必须带回可读原因（便于现场日志定位，不许静默）",
                  !string.IsNullOrEmpty(why));

            // ---- ★★ v56 的两道硬闸门（各配负向对照）----
            //
            //   为什么必须钉住：命令行桥最大的风险是**"假成功"** ——
            //   `/ascii` 在服务没跑时什么都不做却照样返回 0（WeaselServer.cpp 无条件
            //   `return 0`），前台不是 AutoCAD 时它又会去切**别的**程序的会话。
            //   两种情况都会让我们写下 `_switchApplied` 的假记录，从而封死整个会话的
            //   重试 —— 正是 v55 失败的同一个坑。所以两条闸门都要有可执行断言。
            Console.WriteLine();
            Console.WriteLine("----- 命令行桥的两道闸门（v56）-----");

            // 闸门①：服务没跑 → 不许动手（否则 rc=0 会被误当成成功）。
            Check("★ 服务未运行 → 拒绝执行命令行桥（堵死\"rc=0 假成功\"）",
                  !WeaselBridge.ShouldAttemptCli(ImeMode.English, false)
                  && !WeaselBridge.ShouldAttemptCli(ImeMode.Chinese, false));

            // 闸门①的反面：服务在跑且目标明确 → 允许。
            Check("服务在运行 + 目标 English/Chinese → 允许执行命令行桥",
                  WeaselBridge.ShouldAttemptCli(ImeMode.English, true)
                  && WeaselBridge.ShouldAttemptCli(ImeMode.Chinese, true));

            Check("★ 目标 Unknown → 即使服务在跑也拒绝",
                  !WeaselBridge.ShouldAttemptCli(ImeMode.Unknown, true));

            // 闸门②：前台不是 AutoCAD → 不许走命令行桥（会把别的程序切了）。
            Check("★ 前台不是 AutoCAD → 不走命令行桥（/ascii 会落到别的程序上）",
                  !XimeEngine.ShouldUseWeaselCli(true, false));

            Check("前台是 AutoCAD + 开关打开 → 走命令行桥",
                  XimeEngine.ShouldUseWeaselCli(true, true));

            Check("配置里关掉（UseWeaselCli=false）→ 不走命令行桥（回到纯 Shift）",
                  !XimeEngine.ShouldUseWeaselCli(false, true)
                  && !XimeEngine.ShouldUseWeaselCli(false, false));

            // 闸门②的负向对照：把实现改成 `return useWeaselCli;`（不看 canKey）
            //   → 上面「前台不是 AutoCAD → 不走命令行桥」那条必须变红。
            //   闸门①的负向对照：改成 `return target == English || target == Chinese;`
            //   （不看 serverRunning）→ 上面「服务未运行 → 拒绝」那条必须变红。

            // ---- ★★★ v57：「幂等直设优先」判据 ----
            //
            //   为什么必须钉住（v56 实机事故）：
            //     `/ascii` / `/nascii` 是**设值**不是翻转 —— 这是本插件里**唯一**
            //     不依赖"知道当前是什么态"的切换手段。可 v56 把它挂在
            //     「`_switchApplied == target` → 无需动作」**之后**，等于让一条
            //     （小狼毫上根本无法验证的）记忆来决定"还要不要设值"。
            //     那条记忆一旦为假，功能就**永久静默失效**：
            //     现场日志 13:44:35 —— RYBZ 命令期间需要英文，日志里却只有
            //     「本模式已置于 English → 无需动作」，一次 CLI 都没走，
            //     用户按 A 照样被中文输入法吃掉。
            //   v57 的修法：桥可用时**越过**那条记忆与探测，直接下发幂等设值。
            Console.WriteLine();
            Console.WriteLine("----- 幂等直设优先（v57）-----");

            Check("★ 四条全满足（开关开 + 找到 exe + 服务在跑 + 前台是 CAD）→ 优先幂等直设",
                  XimeEngine.ShouldPreferIdempotentSet(true, true, true, true));

            Check("★ 前台不是 AutoCAD → 不幂等直设（/ascii 会切到别的程序的会话上）",
                  !XimeEngine.ShouldPreferIdempotentSet(true, true, true, false));

            Check("★ 服务没在跑 → 不幂等直设（rc=0 是假成功，会写下假记录）",
                  !XimeEngine.ShouldPreferIdempotentSet(true, true, false, true));

            Check("没找到 WeaselServer.exe → 不幂等直设（原样退回真实 Shift）",
                  !XimeEngine.ShouldPreferIdempotentSet(true, false, true, true));

            Check("配置里关掉命令行桥 → 不幂等直设（行为与 v51 逐字节相同）",
                  !XimeEngine.ShouldPreferIdempotentSet(false, true, true, true));

            // 负向对照：把实现改成 `return canKey;`（丢掉"桥可用"这一条）
            //   → 上面「没找到 WeaselServer.exe → 不幂等直设」那条必须变红。
            //   改成 `return true;`（无条件）→ 另外三条必须一起变红。

            // ---- 待补切换的处置判据（越界护栏的可执行反证）----
            Console.WriteLine();
            Console.WriteLine("----- 待补切换（_pendingSwitch）处置判据 -----");

            Check("没有待补 → 什么都不做",
                  XimeEngine.DecidePendingFlush(-1, true, true, true, true) == PendingFlush.None);

            Check("★ 有待补但前台不是 AutoCAD → 继续等待、一个按键都不发（越界护栏）",
                  XimeEngine.DecidePendingFlush((int)ImeMode.English, true, true, true, false)
                      == PendingFlush.Wait);

            Check("有待补且前台是 AutoCAD → 立刻补做真实 Shift",
                  XimeEngine.DecidePendingFlush((int)ImeMode.English, true, true, true, true)
                      == PendingFlush.Flush);

            Check("已切回无视输入法模式 → 作废待补（不要再去动输入法）",
                  XimeEngine.DecidePendingFlush((int)ImeMode.English, false, true, true, true)
                      == PendingFlush.Discard);
            Check("已停用 → 作废待补",
                  XimeEngine.DecidePendingFlush((int)ImeMode.English, true, false, true, true)
                      == PendingFlush.Discard);
            Check("已退出接管（CAD 不是前台）→ 作废待补",
                  XimeEngine.DecidePendingFlush((int)ImeMode.English, true, true, false, true)
                      == PendingFlush.Discard);

            // ---- ★★ 过期请求闸门（v37 新增）----
            //
            // 现场事故（v36 实机日志，19:22:06 那一轮）——「退出文字编辑后敲命令，
            // 概率切不回英文」的真正机理解在这里：
            //   19:22:06.831  CommandWillStart: TTEXT → 请求中文（_switchWanted=Chinese）
            //   19:22:06.955  切换（需要中文）开始执行（探测 + 靶点解析要跑上百毫秒）
            //   19:22:06.992  用户已 Esc 退出编辑 → ForceEnglish → _switchWanted=English
            //   19:22:07.013  那个**过期的中文切换**才落地：结果=True 方式=RealKey
            //                 → 输入法被硬生生翻回中文
            //   19:22:07.125  切换（需要英文）：另一处正在切换 → 跳过（被丢掉）
            //   19:22:07.166  切换（需要英文）：实际=Chinese → English（170ms 后才纠回来）
            //   这 170ms 里用户敲的命令名全进了中文输入法 → "不跟手、概率不切"。
            //
            // 所以：**已经在执行中的请求，一旦被更新的请求取代，绝不许再动手。**
            // 这条契约靠注释是记不住的（它只在日志里隔着一两百毫秒才看得出来），
            // 必须用断言钉住。
            Console.WriteLine();
            Console.WriteLine("----- 过期请求闸门（IsSupersededRequest）-----");

            Check("最新请求就是自己 → 不是过期，照常动手",
                  !XimeEngine.IsSupersededRequest((int)ImeMode.Chinese, ImeMode.Chinese));
            Check("★ 最新请求已改成 English，而自己在切 Chinese → 必须判为过期",
                  XimeEngine.IsSupersededRequest((int)ImeMode.English, ImeMode.Chinese));
            Check("★ 最新请求已改成 Chinese，而自己在切 English → 必须判为过期（同样会翻反）",
                  XimeEngine.IsSupersededRequest((int)ImeMode.Chinese, ImeMode.English));
            Check("★ 没有任何请求了（-1，例如刚 Engage/Disengage）→ 一律判为过期，不许动手",
                  XimeEngine.IsSupersededRequest(-1, ImeMode.Chinese));
            Check("（同上）自己就是最新请求时不得误判为过期", 
                  !XimeEngine.IsSupersededRequest((int)ImeMode.Chinese, ImeMode.Chinese));

            // ---- ★★ 「确保中文」的决策判据（v38 新增）----
            //
            // 现场事故（v36 日志，无视输入法模式）：
            //   确保中文（进入文字编辑态）：当前模式=Chinese 用户手动切过=False 窗口=0x6A05E0
            //   确保中文（进入文字编辑态）：已是中文，无需动作
            //   而 0x6A05E0 的类名是 `Afx:00007FF642700000:28:...` —— **绘图区**。
            //   绘图区没有 IME 上下文，`ImmGetContext` 给回的是兼容层**默认值**（恒为中文），
            //   于是"已经是中文，无需动作"永远是错的答案：用户双击文字后怎么都打不出中文
            //   （"双击修改，输入法模式就有可能失效"）。而且旧代码在那一支里还把
            //   `_userImeToggleTick` 清掉了 —— 连"用户刚手动切过、必须切回中文"这条
            //   唯一的补救线索也一并毁掉。
            //
            // 契约：**没有 IME 上下文的窗口读回的中文，不算中文。**
            Console.WriteLine();
            Console.WriteLine("----- 「确保中文」决策判据（ShouldEnsureChineseTapShift）-----");

            Check("可信读数 + 已是中文 → 不动作",
                  !XimeEngine.ShouldEnsureChineseTapShift(ImeMode.Chinese, true, false));
            Check("可信读数 + 英文 → 必须发一次真实 Shift",
                  XimeEngine.ShouldEnsureChineseTapShift(ImeMode.English, true, false));

            Check("★ 不可信读数 + 「中文」→ 绝不能当成已是中文（v38 事故机制；无手动记录时不盲切）",
                  !XimeEngine.ShouldEnsureChineseTapShift(ImeMode.Chinese, false, false));
            Check("★ 不可信读数 + 用户刚手动切过 → 必须切（这条正是修好「双击修改打不出中文」的关键）",
                  XimeEngine.ShouldEnsureChineseTapShift(ImeMode.Chinese, false, true));

            Check("靶点有上下文但两条通道都读不出 + 无手动记录 → 保持现状",
                  !XimeEngine.ShouldEnsureChineseTapShift(ImeMode.Unknown, true, false));
            Check("靶点有上下文但两条通道都读不出 + 用户刚手动切过 → 允许盲切",
                  XimeEngine.ShouldEnsureChineseTapShift(ImeMode.Unknown, true, true));
            Check("完全读不出来 + 无手动记录 → 不盲切",
                  !XimeEngine.ShouldEnsureChineseTapShift(ImeMode.Unknown, false, false));

            Console.WriteLine();
            Console.WriteLine("----- 「退出接管时该不该还原输入法」（ShouldRestoreOriginalIme，v39）-----");

            Check("可信原态=中文、我们切过英文 → 必须还原（★ 本轮主修：退出时把用户的中文还给他）",
                  XimeEngine.ShouldRestoreOriginalIme(ImeMode.Chinese, true, ImeMode.English));
            Check("可信原态=英文、我们切过中文 → 同样必须还原（方向对称，不许写死一边）",
                  XimeEngine.ShouldRestoreOriginalIme(ImeMode.English, true, ImeMode.Chinese));
            Check("可信原态=中文、我们自己没切过 → 不还原（没动过手就不猜方向）",
                  !XimeEngine.ShouldRestoreOriginalIme(ImeMode.Chinese, true, ImeMode.Unknown));
            Check("★ 原态不可信（进入时靶点只是主窗口兜底、读到的是默认「中文」）→ 绝不还原",
                  !XimeEngine.ShouldRestoreOriginalIme(ImeMode.Chinese, false, ImeMode.English));
            Check("当前态与原态相同 → 无需还原",
                  !XimeEngine.ShouldRestoreOriginalIme(ImeMode.Chinese, true, ImeMode.Chinese));
            Check("原态读不出来（该输入法不支持状态读写）→ 不还原",
                  !XimeEngine.ShouldRestoreOriginalIme(ImeMode.Unknown, true, ImeMode.English));
            Check("原态不可信 且 没切过 → 不还原（两条否决各自独立生效）",
                  !XimeEngine.ShouldRestoreOriginalIme(ImeMode.English, false, ImeMode.Unknown));

            Console.WriteLine();
            Console.WriteLine("----- 切换请求的排队延迟契约（v40「不跟手」）-----");

            Check("★ 「焦点已稳定」的路径必须真的不等待：SwitchDelayNone == 0",
                  XimeEngine.SwitchDelayNone == 0);
            Check("★ 默认路径仍要等焦点迁移：SwitchDelayMs > 0",
                  XimeEngine.SwitchDelayMs > 0);
            Check("默认延迟不得被随手放大（现场 ~170ms 已到用户能感知的边界）",
                  XimeEngine.SwitchDelayMs <= 120);

            Console.WriteLine();
            Console.WriteLine("----- 「确保中文」的靶点契约（IsUsableEnsureChineseTarget，v41）-----");

            Check("可信 + 文本输入宿主 + 不是命令栏 → 可用（焦点就在编辑框，这是正解）",
                  XimeEngine.IsUsableEnsureChineseTarget(true, true, false));
            Check("★ 命令栏控件**不算**可用靶点（它的中英读数与编辑框可以不同 —— 本轮回归的根因）",
                  !XimeEngine.IsUsableEnsureChineseTarget(true, true, true));
            Check("不是文本输入宿主（绘图区 / MDI 框架）→ 不可用",
                  !XimeEngine.IsUsableEnsureChineseTarget(true, false, false));
            Check("读数不可信 → 不可用（绝不采信没有 IME 上下文窗口的「中文」）",
                  !XimeEngine.IsUsableEnsureChineseTarget(false, true, false));
            Check("两条否决叠加（不可信 + 命令栏）→ 不可用",
                  !XimeEngine.IsUsableEnsureChineseTarget(false, true, true));

            Console.WriteLine();
            Console.WriteLine("----- ★★ v42：无视模式「确保中文」必须真的会动作 -----");
            Console.WriteLine("    背景：v41 实机日志里 `已发送一次 Shift` 出现 **0 次** ——");
            Console.WriteLine("    这套机制从未真正动作过，所以它无力修复「输入法停在英文」。");

            // 允许"盲切"的正面证据 = 用户刚手动切过 ∪ **我们自己把输入法留在了英文**。
            // 后一条正是本轮的关键：刚从切换模式切过来时，靶点窗口会给出与事实相反的
            // 「Chinese」读数，唯一可信的信息是"我们刚亲手把它按在了英文上"。
            Check("★ 可信读到中文 → 不动（唯一可以不放手的正解）",
                  !XimeEngine.ShouldEnsureChineseTapShift(ImeMode.Chinese, true, false));
            Check("★ 可信读到英文 → 必须切",
                  XimeEngine.ShouldEnsureChineseTapShift(ImeMode.English, true, false));
            Check("★ 读数不可信 + 有「我们留下过英文」的证据 → 必须切（本轮修复点）",
                  XimeEngine.ShouldEnsureChineseTapShift(ImeMode.Unknown, false, true));
            Check("读数不可信 + 无任何证据 → 不许盲切（盲切有 50% 概率把中文切成英文）",
                  !XimeEngine.ShouldEnsureChineseTapShift(ImeMode.Unknown, false, false));
            Check("★ 可信读到中文时，即使有证据也不许切（读数已证明无需动作）",
                  !XimeEngine.ShouldEnsureChineseTapShift(ImeMode.Chinese, true, true));

            Console.WriteLine();
            Console.WriteLine("----- ★★ v42：等不到编辑控件要「登记待补」而不是放弃 -----");
            Console.WriteLine("    背景：v41 20:31:38，天正 TTEXT 弹框时焦点被 CAD 主框架抢走约 880ms，");
            Console.WriteLine("    而轮询只等 150ms → 直接放弃 → 一个 Shift 都不发 → 打不出中文。");

            const int TTL = 5000;
            const int GAP = 80;

            Check("没有待补 → 什么都不做",
                  XimeEngine.DecidePendingEnsure(0, 1000, true, true, true, true, TTL, GAP)
                      == XimeEngine.PendingEnsureAction.None);
            Check("★ 编辑控件迟迟不出现不构成放弃理由：间隔够了就必须补做",
                  XimeEngine.DecidePendingEnsure(1000, 1400, true, true, true, true, TTL, GAP)
                      == XimeEngine.PendingEnsureAction.Flush);
            Check("刚登记（焦点还在抖动）→ 等下一次触发，不清除待补",
                  XimeEngine.DecidePendingEnsure(1000, 1030, true, true, true, true, TTL, GAP)
                      == XimeEngine.PendingEnsureAction.Wait);
            Check("★ 超时（>5s）→ 作废，不许乱切",
                  XimeEngine.DecidePendingEnsure(1000, 7000, true, true, true, true, TTL, GAP)
                      == XimeEngine.PendingEnsureAction.Discard);
            Check("时钟回绕（now < tick）→ 作废，不许乱切",
                  XimeEngine.DecidePendingEnsure(5000, 100, true, true, true, true, TTL, GAP)
                      == XimeEngine.PendingEnsureAction.Discard);
            Check("★ 编辑态已结束 → 作废（这段文字早打完了，再切会打乱用户下一段输入）",
                  XimeEngine.DecidePendingEnsure(1000, 1400, true, true, true, false, TTL, GAP)
                      == XimeEngine.PendingEnsureAction.Discard);
            Check("已切到切换输入法模式 → 作废（那边有自己的状态模型）",
                  XimeEngine.DecidePendingEnsure(1000, 1400, false, true, true, true, TTL, GAP)
                      == XimeEngine.PendingEnsureAction.Discard);
            Check("已关闭 / 已退出接管 → 作废",
                  XimeEngine.DecidePendingEnsure(1000, 1400, true, false, true, true, TTL, GAP)
                      == XimeEngine.PendingEnsureAction.Discard
                  && XimeEngine.DecidePendingEnsure(1000, 1400, true, true, false, true, TTL, GAP)
                      == XimeEngine.PendingEnsureAction.Discard);

            Console.WriteLine();
            Console.WriteLine("----- ★★ v42：被 Stop 过的引擎必须能真正重启（半死状态回归）-----");
            Console.WriteLine("    背景：卸载流程内部会 Stop（停轮询 + 焦点监听），随后配置被复位成");
            Console.WriteLine("    Enabled=true；若 Refresh 只 Engage 而不重启定时器，就变成");
            Console.WriteLine("    「钩子在、焦点监听停」—— 无视模式下双击文字**永远进不了编辑态**。");

            Check("★ 该运行却没在运行 → 必须重启（本条就是半死状态的回归证明）",
                  XimeEngine.ShouldRestartStoppedEngine(true, false, false));
            Check("本来就在运行 → 不要重复启动",
                  !XimeEngine.ShouldRestartStoppedEngine(true, true, false));
            Check("配置要求不运行 → 不启动",
                  !XimeEngine.ShouldRestartStoppedEngine(false, false, false));
            Check("★ 已 Dispose → 一律不启动",
                  !XimeEngine.ShouldRestartStoppedEngine(true, false, true));

            Console.WriteLine();
            Console.WriteLine("----- ★★★ v44：「我们留下过英文」这条证据的寿命 -----");
            Console.WriteLine("    背景（AutoCAD 2022 + 天正实机，v43 日志）：");
            Console.WriteLine("      21:39:28.451 记下证据：输入法可能仍停在英文（还原被推迟执行）");
            Console.WriteLine("      21:39:29.918 补做延迟还原 → Chinese（真实 Shift 翻转）结果=True 方式=RealKey");
            Console.WriteLine("      21:39:30.823 确保中文：靶点读回 Chinese，但…判该读数不可信  ← 证据还活着！");
            Console.WriteLine("      21:39:30.868 确保中文：已发送一次 Shift 切换  ← 把刚切好的中文又翻成了英文");
            Console.WriteLine("    ⇒ 还原**已经成功**，可那条证据没人撤销 → 下一次双击文字被盲切一次。");
            Console.WriteLine("      用户现象：切回无视模式后有概率打不出中文；用一次 text 命令就好了");
            Console.WriteLine("      （那次 Shift 把证据消费掉了）。");

            Check("★★ 还原成功 → 必须撤销证据（本条就是本次故障的回归证明，旧行为返回 true）",
                  !XimeEngine.ResolveImeLeftEnglishAfterRestore(
                      ImeMode.Chinese, restoreSucceeded: true, hadEvidence: true));
            Check("★ 还原没成功 → 证据仍然成立（确实可能还停在英文）",
                  XimeEngine.ResolveImeLeftEnglishAfterRestore(
                      ImeMode.Chinese, restoreSucceeded: false, hadEvidence: true));
            Check("★ 成功与失败必须被区分对待（防止有人把实现改回「一律保留」）",
                  XimeEngine.ResolveImeLeftEnglishAfterRestore(ImeMode.Chinese, true, true)
                  != XimeEngine.ResolveImeLeftEnglishAfterRestore(ImeMode.Chinese, false, true));
            Check("目标本来就是英文 → 不算「我们弄脏了」，不留证据",
                  !XimeEngine.ResolveImeLeftEnglishAfterRestore(
                      ImeMode.English, restoreSucceeded: false, hadEvidence: true));
            Check("本来就没有证据 → 结果只能是 false（不许凭空造出证据）",
                  !XimeEngine.ResolveImeLeftEnglishAfterRestore(
                      ImeMode.Chinese, restoreSucceeded: false, hadEvidence: false));

            Console.WriteLine();
            Console.WriteLine("----- ★★★ v46：焦点落到「新的文字编辑控件」必须重新确保中文 -----");
            Console.WriteLine("    背景（AutoCAD 2022 + 天正实机，v45 引擎日志，切换输入法模式）：");
            Console.WriteLine("      22:18:49.646  进入文字编辑态（焦点在编辑框）→ 允许中文   ← 唯一的一次");
            Console.WriteLine("      22:18:52.367  焦点变化: 0x981DE0 类名=Edit 是编辑框=True  ← 被吞");
            Console.WriteLine("      22:18:54.907  焦点变化: 0x8024F2 类名=Edit 是编辑框=True  ← 被吞");
            Console.WriteLine("      …（共 9 次被吞，「需要中文（文字输入）」请求 0 次）");
            Console.WriteLine("      22:19:12.038  退出文字编辑态（兜底巡检：长时间未在编辑框）← 卡了 22.4 秒");
            Console.WriteLine("    ⇒ 旧实现把 EnterTextEditMode 当成**一次性闩锁**（首行 `if (_inTextEditMode) return;`），");
            Console.WriteLine("      而退出只有「焦点明确回到命令栏」和「按键时焦点已不在编辑框」两条路 ——");
            Console.WriteLine("      用户最自然的「双击文字A→打字→直接双击文字B」两条都不满足（鼠标双击不产生按键），");
            Console.WriteLine("      于是闩锁一直卡住，后续每一次双击都不再触发任何中文切换。");
            Console.WriteLine("      切换模式下这条通路**故意把输入法按在英文上** → 用户双击文字后打出来全是英文；");
            Console.WriteLine("      无视模式下则不再执行「确保中文」。这正是「有概率切换失效、多试几次又能恢复」。");

            Check("★★ 不在编辑态 → 正常进入（该动作）",
                  XimeEngine.ShouldRunTextEditEntry(alreadyInEditMode: false, isNewEditHost: false));
            Check("★★★ 已在编辑态但**换了一个编辑控件** → 必须动作（本条就是本次故障的回归证明，旧行为返回 false）",
                  XimeEngine.ShouldRunTextEditEntry(alreadyInEditMode: true, isNewEditHost: true));
            Check("★ 已在编辑态且是同一个编辑控件（控件内部焦点抖动）→ 不重复动作",
                  !XimeEngine.ShouldRunTextEditEntry(alreadyInEditMode: true, isNewEditHost: false));
            Check("★ 首次进入即使拿不到编辑控件句柄（isNewEditHost=False）也必须动作（不能因为拿不到句柄就不切）",
                  XimeEngine.ShouldRunTextEditEntry(alreadyInEditMode: false, isNewEditHost: false));
            Check("★★ 「已进入 + 换控件」与「已进入 + 同控件」必须被区分对待（防止有人把实现改回一概吞掉）",
                  XimeEngine.ShouldRunTextEditEntry(true, true)
                  != XimeEngine.ShouldRunTextEditEntry(true, false));

            Console.WriteLine();
            Console.WriteLine("----- ★★★ v47：盲切之后的回读判定（DecideBlindShiftVerdict）-----");
            Console.WriteLine("    背景（AutoCAD 2022 + 天正实机，v46 引擎日志）：");
            Console.WriteLine("      整轮 58 次切换，日志**全部**写着「结果=True 方式=RealKey」——");
            Console.WriteLine("      因为 `TapShift()` 的返回值只代表 SendInput 把按键**塞进了系统输入队列**，");
            Console.WriteLine("      从不代表输入法真的翻转了（候选窗开着 / 控件没开 IME 上下文 /");
            Console.WriteLine("      down-up 之间焦点被抢走，都会让那一下落空）。");
            Console.WriteLine("      而铁律 4 让 `_switchApplied` 的假值**一直保活** → 一旦落空一次，");
            Console.WriteLine("      parity 整体反到最后，用户看到的就是「有一定概率切换逻辑不对」。");
            Console.WriteLine("    ⇒ v47 每次真实切换后回读一次，按本判据判定，失败就补发、再失败就作废记录。");
            Console.WriteLine("    ★ 判据的基石是一条**天然不对称**：兼容层对「没有 IME 上下文的窗口」");
            Console.WriteLine("      返回的默认值恒为「中文」⇒ **读到英文必为真**，读到中文不足为凭。");

            Check("★★★ 想要中文、靶点可信地读回英文 → Failed（这是本版唯一能看见的「盲切落空」证据）",
                  XimeEngine.DecideBlindShiftVerdict(
                      ImeMode.Chinese, ImeMode.English, probeCapable: true)
                  == XimeEngine.BlindShiftVerdict.Failed);
            Check("★★★ 想要英文、读回英文 → Confirmed（英文读数不可能是兼容层默认值）",
                  XimeEngine.DecideBlindShiftVerdict(
                      ImeMode.English, ImeMode.English, probeCapable: true)
                  == XimeEngine.BlindShiftVerdict.Confirmed);
            Check("★★ 想要英文、却读回中文 → Inconclusive（中文可能是默认值/滞后值，不许当成失败）",
                  XimeEngine.DecideBlindShiftVerdict(
                      ImeMode.English, ImeMode.Chinese, probeCapable: true)
                  == XimeEngine.BlindShiftVerdict.Inconclusive);
            Check("★★ 想要中文、读回中文 → Inconclusive（可能是默认值，不许当成成功）",
                  XimeEngine.DecideBlindShiftVerdict(
                      ImeMode.Chinese, ImeMode.Chinese, probeCapable: true)
                  == XimeEngine.BlindShiftVerdict.Inconclusive);
            Check("★★★ 靶点不可信（无 IME 上下文）→ 一律 Inconclusive（默认值恒为「中文」，读数毫无信息）",
                  XimeEngine.DecideBlindShiftVerdict(
                      ImeMode.Chinese, ImeMode.English, probeCapable: false)
                  == XimeEngine.BlindShiftVerdict.Inconclusive
                  && XimeEngine.DecideBlindShiftVerdict(
                      ImeMode.English, ImeMode.English, probeCapable: false)
                  == XimeEngine.BlindShiftVerdict.Inconclusive);
            Check("★ 读不出来（Unknown）→ Inconclusive",
                  XimeEngine.DecideBlindShiftVerdict(
                      ImeMode.Chinese, ImeMode.Unknown, probeCapable: true)
                  == XimeEngine.BlindShiftVerdict.Inconclusive);

            // ★★ 穷举 2(目标) × 3(回读) × 2(可信) = 12 种组合，Failed 只能出现 **1** 次。
            //    这条断言防的是"有人把失败面扩大"——例如把「想要中文、读回中文」也判成
            //    失败：那会让补发逻辑在**正确的中文**上再发一次 Shift，把中文反手切成英文
            //    （v42/v43 就是这么坏的）。失败面只允许有一条：可信的英文读数。
            {
                var modes = new[] { ImeMode.English, ImeMode.Chinese, ImeMode.Unknown };
                int failedCount = 0, confirmedCount = 0;
                foreach (var tgt in new[] { ImeMode.English, ImeMode.Chinese })
                    foreach (var after in modes)
                        foreach (bool capable in new[] { true, false })
                        {
                            var v = XimeEngine.DecideBlindShiftVerdict(tgt, after, capable);
                            if (v == XimeEngine.BlindShiftVerdict.Failed) failedCount++;
                            if (v == XimeEngine.BlindShiftVerdict.Confirmed) confirmedCount++;
                        }
                Check("★★★ 穷举 12 种组合：Failed 恰好 1 次（=想要中文 + 可信英文），"
                      + "不许把「读回中文」也判成失败（那会把正确的中文反手切成英文）",
                      failedCount == 1);
                Check("★★ 穷举 12 种组合：Confirmed 恰好 1 次（=想要英文 + 可信英文）",
                      confirmedCount == 1);
            }

            Console.WriteLine();
            Console.WriteLine("----- ★★★ v48：「回读通道到底能不能用」（IsReadbackChannelUseless）-----");
            Console.WriteLine("    背景（AutoCAD 2022 + 天正 T20 + **小狼毫** 实机，v47 引擎日志）：");
            Console.WriteLine("      小狼毫走 **TSF**，中英态存在 TSF 的 GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION 里；");
            Console.WriteLine("      插件用的 IMM32 两条通道读到的**恒为兼容层默认值「中文」**——");
            Console.WriteLine("      实测 111 次回读**全部** Inconclusive，读到 English **0 次**。");
            Console.WriteLine("      后果：v47 那把校验的基石「English 读数天然可信」在小狼毫上**不成立** ⇒");
            Console.WriteLine("      Failed 永不触发 ⇒ 校验形同虚设，切换只能靠「猜」，而日志看起来一切正常。");
            Console.WriteLine("    ⇒ v48 把「回读恒为同一值且与目标无关」**本身**当成通道失效的正面证据，始终记录一次。");

            // 6 条真值表：①②必须为真；③④⑤⑥必须为假。
            Check("★★★ 小狼毫现场：8 次回读全 Inconclusive、读数恒为同一值 → 判「通道失效」",
                  XimeEngine.IsReadbackChannelUseless(
                      totalSamples: 8, inconclusiveCount: 8, distinctReads: 1, minSamples: 8));
            Check("★★★ 111 次全 Inconclusive、读数恒为同一值 → 判「通道失效」（v47 实机就是这一行）",
                  XimeEngine.IsReadbackChannelUseless(
                      totalSamples: 111, inconclusiveCount: 111, distinctReads: 1, minSamples: 8));
            Check("★★★ 只要出现过一次 Confirmed（读到过 English）→ **不许**判通道失效"
                  + "（通道能给出可信读数，只是还没确认到，不是坏掉）",
                  !XimeEngine.IsReadbackChannelUseless(
                      totalSamples: 8, inconclusiveCount: 7, distinctReads: 2, minSamples: 8));
            Check("★★ 回读出现过两个不同值（状态确实在变）→ **不许**判通道失效",
                  !XimeEngine.IsReadbackChannelUseless(
                      totalSamples: 20, inconclusiveCount: 20, distinctReads: 2, minSamples: 8));
            Check("★★ 样本不足（7 < 8）→ **不许**下结论（一次两次读不出来很正常，别误报）",
                  !XimeEngine.IsReadbackChannelUseless(
                      totalSamples: 7, inconclusiveCount: 7, distinctReads: 1, minSamples: 8));
            Check("★★ 恰好够样本（8 = 8）→ 判「通道失效」（下界必须闭合，不许用 < 写成 9）",
                  XimeEngine.IsReadbackChannelUseless(
                      totalSamples: 8, inconclusiveCount: 8, distinctReads: 1, minSamples: 8));

            // ★ 负向对照用的"边界哨兵"：把 distinctReads 从 1 抬到 2 就必须翻面。
            //   这条防的是"有人把 distinctReads 判据写成恒真/恒假"这种典型漂移。
            Check("★★ 边界哨兵：同一样本量下 distinctReads 1→2 必须让判定翻面",
                  XimeEngine.IsReadbackChannelUseless(20, 20, 1, 8)
                  && !XimeEngine.IsReadbackChannelUseless(20, 20, 2, 8));

            Console.WriteLine();
            Console.WriteLine("----- ★★★ v49：状态记录能不能跨窗口用（IsAppliedStateUsableFor）-----");
            Console.WriteLine("    背景（AutoCAD 2022 + 天正 T20 + 小狼毫 v48 实机，15:25~15:27 日志）：");
            Console.WriteLine("      切换靶点在**两个不同的 IME 上下文**之间来回走 ——");
            Console.WriteLine("        · 命令栏控件 HwndWrapper[…0x40A82]（「需要英文（命令栏）」落这里）；");
            Console.WriteLine("        · 文字编辑框 Edit（「需要中文（文字输入）」落这里，每次换新句柄）。");
            Console.WriteLine("      IME 状态挂在**具体窗口**上，在 A 上按的 Shift 不保证 B 也跟着变；");
            Console.WriteLine("      而 `_switchApplied` 是**全局单一变量** —— 于是在两个窗口之间");
            Console.WriteLine("      来回写，必然与实际漂移，表现为「有一定概率切换逻辑不对」。");
            Console.WriteLine("    ⇒ v49：记录连同「在哪个窗口上成立」一起存；靶点窗口不同 → 记录作废、重新动手。");

            var hA = new IntPtr(0x40A82);
            var hB = new IntPtr(0x170930);

            Check("★★★ 记录说已置于 Chinese，但那是**另一个窗口**写的 → 不许据此跳过（必须重新动手）",
                  !XimeEngine.IsAppliedStateUsableFor(
                      ImeMode.Chinese, hA, ImeMode.Chinese, hB));
            Check("★★★ 同一个窗口 + 记录等于目标 → 允许跳过（正常路径，不能被误伤）",
                  XimeEngine.IsAppliedStateUsableFor(
                      ImeMode.Chinese, hA, ImeMode.Chinese, hA));
            Check("★★ 记录不等于目标 → 不许跳过（本来就要动手，与窗口无关）",
                  !XimeEngine.IsAppliedStateUsableFor(
                      ImeMode.English, hA, ImeMode.Chinese, hA));
            Check("★★ 旧记录没绑窗口（IntPtr.Zero）→ 沿用（不改变既有行为，兼容旧路径）",
                  XimeEngine.IsAppliedStateUsableFor(
                      ImeMode.Chinese, IntPtr.Zero, ImeMode.Chinese, hA));
            Check("★★ 拿不到靶点窗口（Zero）→ 不许跳过（那时也动不了手，交给上游分支）",
                  !XimeEngine.IsAppliedStateUsableFor(
                      ImeMode.Chinese, hA, ImeMode.Chinese, IntPtr.Zero));
            Check("★★ No-Effect 哨兵：仅把「记录窗口」从 hA 换成 hB 就必须翻面（防判据恒真/恒假）",
                  XimeEngine.IsAppliedStateUsableFor(ImeMode.Chinese, hA, ImeMode.Chinese, hA)
                  && !XimeEngine.IsAppliedStateUsableFor(ImeMode.Chinese, hB, ImeMode.Chinese, hA));

            Console.WriteLine();
            Console.WriteLine("----- v51：v50 的「必须打在编辑框上」硬闸门已撤除（回归 v49 行为）-----");
            Console.WriteLine("    背景（v50 实机 16:09:08 逐行钉死）：天正编辑框有『两段生存期』——");
            Console.WriteLine("      ① 创建瞬间只闪 1ms 随即被 AfxMDIFrame140u 抢走；");
            Console.WriteLine("      ② 稳定期要等到 ~877ms 之后才开始。");
            Console.WriteLine("    而 v50 的轮询预算只有 150ms（10×15ms）→ 结构性等不到 →");
            Console.WriteLine("      12 次拦截里 11 次空等、3 次重试耗尽放弃 → **切换彻底不发生**（比 v49 更糟）。");
            Console.WriteLine("    ⇒ v51：撤掉闸门。靶点还按 v49 的规则解析（含按窗口记账），");
            Console.WriteLine("      但**不再为了等编辑框而空转**；真·根治留待 TSF 通道。");
            Console.WriteLine("    ★ 判据 CanApplySwitchOnTarget 已随之删除 —— 它唯一的存在理由就是那道闸门。");

            Console.WriteLine();
            Console.WriteLine("----- v55：命令执行期间不许进文字编辑态（RYBZ 真根因之一）-----");
            Console.WriteLine("    背景（2026-10-05 实机日志钉死）：天正 RYBZ 选块后弹一个内部 `Edit` 框，");
            Console.WriteLine("      旧逻辑把它当成『用户要输入中文文字』→ EnterTextEditMode");
            Console.WriteLine("      → SwitchToChinese + EnsureChinese ⇒ **在二级命令期间主动把输入法切中文**");
            Console.WriteLine("      ⇒ 用户按 A/E/D/F/X/Y 全被输入法吃掉，命令栏只剩残留小写 a。");

            // —— 判据①：命令执行期间一律不进编辑态（除非是文字类命令）——
            Check("① 空闲（无活动命令）→ 照旧允许进入文字编辑态（双击文字改字的路径不受影响）",
                  XimeEngine.ShouldEnterTextEditModeForFocus(hasActiveCommand: false,
                                                            commandNeedsChinese: false));
            Check("① 命令执行期间 + 非文字命令（RYBZ）→ **禁止**进入文字编辑态（核心修复）",
                  !XimeEngine.ShouldEnterTextEditModeForFocus(hasActiveCommand: true,
                                                             commandNeedsChinese: false));
            Check("① 命令执行期间 + 文字类命令（TEXT 等）→ 允许进入（否则打字幕/标注的编辑框会被误挡）",
                  XimeEngine.ShouldEnterTextEditModeForFocus(hasActiveCommand: true,
                                                            commandNeedsChinese: true));
            Check("① No-Effect 哨兵：仅把 hasActiveCommand 从 false 翻成 true 就必须翻面（防判据恒真）",
                  XimeEngine.ShouldEnterTextEditModeForFocus(false, false)
                  != XimeEngine.ShouldEnterTextEditModeForFocus(true, false));

            Console.WriteLine();
            Console.WriteLine("----- ★★★ v58：无视模式**绝不碰输入法状态**（撤除 v55 的强制英文例外）-----");
            Console.WriteLine("    用户要求：「无视输入法模式不要动我的输入法状态」——");
            Console.WriteLine("      该模式的英文输入靠钩子把按键直接投递到命令栏实现（绕过 IME），");
            Console.WriteLine("      而不是去改输入法。v55 的 ForceEnglishDuringCommand 已整体撤除。");
            Console.WriteLine("    RYBZ 二级命令改由 XimeConfig.CommandKeyMode 负责 ——");
            Console.WriteLine("      ★ 2026-10-06 现场实测定案：天正二级命令**只认真实物理按键**，");
            Console.WriteLine("        故默认档 = PassThrough（放行物理键）；工作模式仍是默认的**无视输入法模式**");
            Console.WriteLine("        （用户明确要求：绝不改他的输入法状态）。");
            Console.WriteLine("        放行物理键的唯一副作用（输入法也收到键 ⇒ 弹候选窗）由 v6 的");
            Console.WriteLine("        「命令期间无视输入法」（ImeWindowBypass）消除：解除窗口与输入法上下文的");
            Console.WriteLine("        关联 ⇒ 物理键照旧直达天正，输入法收不到键，而**输入法状态一个字节不动**。");
            Console.WriteLine("        PostKey/PostKeyFrame/ReplayVk 四条合成通道天正一律不认，已现场证伪。");

            // —— 判据②（v58 恢复）：**只有切换输入法模式才允许真的切输入法** ——
            Check("② 切换输入法模式 → 允许切（职责所在）",
                  XimeEngine.ShouldAutoSwitchIme(XimeWorkMode.SwitchIme));
            Check("② ★ 无视输入法模式 → **一律禁止**切输入法（绝不碰用户输入法状态）",
                  !XimeEngine.ShouldAutoSwitchIme(XimeWorkMode.IgnoreIme));
            Check("② No-Effect 哨兵：两种模式必须给出不同结果（防判据恒真/恒假）",
                  XimeEngine.ShouldAutoSwitchIme(XimeWorkMode.SwitchIme)
                  != XimeEngine.ShouldAutoSwitchIme(XimeWorkMode.IgnoreIme));

            // —— 判据③：待补处置必须与请求层同一口径 ——
            //   （v55-b 的教训：请求层放行、执行层消散 ⇒ 修复只在队列里蒸发。
            //     v58 恢复成"只有切换模式才 Flush"，无视模式一律 Discard。）
            Check("③ 待补处置：切换模式 + 前台是 CAD → 必须补做",
                  XimeEngine.DecidePendingFlush((int)ImeMode.English,
                                                autoSwitchAllowed: true, enabled: true,
                                                engaged: true, canSendRealKey: true)
                      == PendingFlush.Flush);
            Check("③ ★ 待补处置：无视模式 → 作废（绝不碰输入法）",
                  XimeEngine.DecidePendingFlush((int)ImeMode.English,
                                                autoSwitchAllowed: false, enabled: true,
                                                engaged: true, canSendRealKey: true)
                      == PendingFlush.Discard);
            Check("③ 待补处置 No-Effect 哨兵：autoSwitchAllowed true→false 必须翻面（Flush↔Discard）",
                  XimeEngine.DecidePendingFlush((int)ImeMode.English, true, true, true, true)
                  != XimeEngine.DecidePendingFlush((int)ImeMode.English, false, true, true, true));
            Check("③ 越界护栏未受影响：autoSwitch 允许但前台不是 AutoCAD → 仍然只 Wait（一个键都不发）",
                  XimeEngine.DecidePendingFlush((int)ImeMode.English, true, true, true, false)
                      == PendingFlush.Wait);

            // —— 判据④：**执行层那道闸门**本身（ApplySwitchIme 开头的 return）——
            Check("④ 执行层放行：切换模式 → 允许真的动手",
                  XimeEngine.ShouldApplySwitchNow(XimeWorkMode.SwitchIme));
            Check("④ ★ 执行层放行：无视模式 → 禁止动手（请求根本到不了执行层）",
                  !XimeEngine.ShouldApplySwitchNow(XimeWorkMode.IgnoreIme));
            Check("④ ★ 执行层与请求层**逐格一致**（杜绝『放行/执行两张皮』复发）",
                  XimeEngine.ShouldApplySwitchNow(XimeWorkMode.SwitchIme)
                      == XimeEngine.ShouldAutoSwitchIme(XimeWorkMode.SwitchIme)
                  && XimeEngine.ShouldApplySwitchNow(XimeWorkMode.IgnoreIme)
                      == XimeEngine.ShouldAutoSwitchIme(XimeWorkMode.IgnoreIme));

            Console.WriteLine();
            return fail;
        }

        /// <summary>
        /// <summary>
        /// ★ v6：命令期间「让 CAD 窗口无视输入法」的判据测试。
        ///
        /// ★★ 为什么必须有这一段：这个机制一旦**该开不开**，症状就是老样子
        ///   （输入法弹候选窗）；而一旦**该关不关**，症状是**用户在 AutoCAD 里
        ///   打不出中文** —— 后者是事故级的，且症状与原因离得很远、极难排查。
        ///   所以"四条全过才动手"这条契约必须由可执行断言守着，而不是靠注释。
        /// </summary>
        private static int RunImeBypassTest(Action<string, bool> check)
        {
            Console.WriteLine();
            Console.WriteLine("===== 命令期间「无视输入法」（v6）=====");

            int fail = 0;
            void Check(string what, bool ok)
            {
                check(what, ok);
                if (!ok) fail++;
            }

            // ---- 基准：七条全过 → 动手 ----
            Check("★ 判据: 无视模式 + 已接管 + 非本插件命令 + 命令执行中 + 非文字类命令 → **动手**"
                  + "（解除窗口与输入法的关联）",
                  ImeWindowBypass.ShouldEngage(
                      true, XimeWorkMode.IgnoreIme, true, true, false, false, false));

            // ---- 逐条否决（每条单独关掉，结果都必须变 false）----
            Check("★ 判据否决①: 配置开关关掉 → 不动手（用户可一键关掉整个机制）",
                  !ImeWindowBypass.ShouldEngage(
                      false, XimeWorkMode.IgnoreIme, true, true, false, false, false));

            Check("★ 判据否决②: 「切换输入法模式」→ 不动手（那个模式靠真的切输入法，用不着）",
                  !ImeWindowBypass.ShouldEngage(
                      true, XimeWorkMode.SwitchIme, true, true, false, false, false));

            Check("★ 判据否决③: 引擎未接管 → 不动手",
                  !ImeWindowBypass.ShouldEngage(
                      true, XimeWorkMode.IgnoreIme, false, true, false, false, false));

            Check("★ 判据否决④: 没有命令在执行（空闲敲命令名）→ 不动手（命令栏要能正常输入）",
                  !ImeWindowBypass.ShouldEngage(
                      true, XimeWorkMode.IgnoreIme, true, false, false, false, false));

            Check("★★ 判据否决⑤: 文字类命令（TEXT/MTEXT/天正文字…）→ 不动手（命令期间要打中文）",
                  !ImeWindowBypass.ShouldEngage(
                      true, XimeWorkMode.IgnoreIme, true, true, true, false, false));

            Check("★★ 判据否决⑥: 文字编辑态（焦点在编辑框）→ 不动手（绝不能让用户打不出中文）",
                  !ImeWindowBypass.ShouldEngage(
                      true, XimeWorkMode.IgnoreIme, true, true, false, true, false));

            // ★★★ v6.1：诊断命令不许改变被诊断的状态
            //   现场事实（2026-10-06 23:2x 用户日志）：XIMEBYPASS 自己就是个命令 ⇒
            //   一进来就把关联解除了，于是
            //     · XIMEBYPASS 1 报「已解除 122 个窗口（**枚举 0 个**）」（进来时已生效，走幂等早退）
            //     · XIMEBYPASS 0 那句"现在可以正常打中文了"当场失效（命令结束又被解除）
            //     · XIMESTATUS 永远报「当前生效=是」，看的是**被自己污染过的**状态
            Check("★★★ 判据否决⑦: 本插件自己的命令（XIMESTATUS / XIMEBYPASS …）→ 不动手"
                  + "（诊断命令不许改变被诊断的状态）",
                  !ImeWindowBypass.ShouldEngage(
                      true, XimeWorkMode.IgnoreIme, true, true, false, false, true));

            // ---- "是不是七个条件都在起作用"——逐位翻转，只允许基准那一组为真 ----
            var baseFlags = new[] { true, true, true, true, false, false, false };
            int trueCount = 0;
            for (int i = 0; i < baseFlags.Length; i++)
            {
                var f = (bool[])baseFlags.Clone();
                // 只允许"配置=true / 模式=无视 / 已接管 / 非本插件命令 / 有命令 / 非中文 / 非编辑态"
                if (i == 0) f[0] = false;
                if (i == 1) f[1] = false;   // 模式位（下面单独处理）
                if (i == 2) f[2] = false;
                if (i == 3) f[3] = false;   // 有命令在执行位：翻转成 false
                if (i == 4) f[4] = true;    // 文字类命令位：翻转成 true
                if (i == 5) f[5] = true;    // 文字编辑态位：翻转成 true
                if (i == 6) f[6] = true;    // 本插件命令位：翻转成 true

                bool got = ImeWindowBypass.ShouldEngage(
                    f[0],
                    i == 1 ? XimeWorkMode.SwitchIme : XimeWorkMode.IgnoreIme,
                    f[2], f[3], f[4], f[5], f[6]);
                if (got) trueCount++;
            }
            Check("★★ 判据: 七个条件里任意一条不成立 ⇒ 一律不动手（只有全过才动手）",
                  trueCount == 0);

            // ---- "是不是本插件命令"这个判据本身（纯前缀，可自测）----
            Console.WriteLine();
            Console.WriteLine("----- 本插件命令识别（ImeWindowBypass.IsOwnPluginCommand）-----");
            Check("★ 本插件命令识别: XIMEBYPASS → 是（★ 它是诊断命令，必须排除）",
                  ImeWindowBypass.IsOwnPluginCommand("XIMEBYPASS"));
            Check("本插件命令识别: xime（一级设置命令，小写）→ 是",
                  ImeWindowBypass.IsOwnPluginCommand("xime"));
            Check("本插件命令识别: XIMESTATUS → 是", ImeWindowBypass.IsOwnPluginCommand("XIMESTATUS"));
            Check("本插件命令识别: XIMEPROBE → 是", ImeWindowBypass.IsOwnPluginCommand("XIMEPROBE"));
            Check("本插件命令识别: 带前导下划线（_XIMEBYPASS，国际化前缀）→ 仍是",
                  ImeWindowBypass.IsOwnPluginCommand("_XIMEBYPASS"));
            Check("本插件命令识别: 两边带空白（\" XIMEBYPASS \"）→ 仍是",
                  ImeWindowBypass.IsOwnPluginCommand("  XIMEBYPASS  "));
            Check("★ 本插件命令识别: RYBZ（天正二级命令）→ **不是**（它必须触发本机制）",
                  !ImeWindowBypass.IsOwnPluginCommand("RYBZ"));
            Check("★ 本插件命令识别: TEXT / MTEXT / LINE → 不是",
                  !ImeWindowBypass.IsOwnPluginCommand("TEXT")
                  && !ImeWindowBypass.IsOwnPluginCommand("MTEXT")
                  && !ImeWindowBypass.IsOwnPluginCommand("LINE"));
            Check("★ 本插件命令识别: 空串 / null → 不是（不能误伤\"没有命令\"这个状态）",
                  !ImeWindowBypass.IsOwnPluginCommand("")
                  && !ImeWindowBypass.IsOwnPluginCommand(null));
            Check("★ 本插件命令识别: XIMEX（恶意构造：以 xime 开头的第三方命令）→ 是"
                  + "（宁可漏掉一个第三方命令，也不能让诊断命令污染状态）",
                  ImeWindowBypass.IsOwnPluginCommand("XIMEX"));

            // ---- 手动强制（XIMEBYPASS 1）的安全闸门 ----
            //   ★ 为什么要它：手动解除必须**保持**到用户回 CAD 里试完，否则一空闲就被撤掉；
            //     但"保持"绝不能把用户卡在打不出中文。
            Console.WriteLine();
            Console.WriteLine("----- 手动强制解除的安全闸门（ImeWindowBypass.ShouldHonorManualOverride）-----");
            Check("★ 手动强制: 开关开 + 已接管 + 非文字编辑态 → **允许保持**",
                  ImeWindowBypass.ShouldHonorManualOverride(true, true, false));
            Check("★★ 手动强制否决①: 文字编辑态 → **绝不允许**（那是用户打中文的唯一入口）",
                  !ImeWindowBypass.ShouldHonorManualOverride(true, true, true));
            Check("★★ 手动强制否决②: 机制被 XIMEBYPASS off 关掉 → 作废（不许绕过开关）",
                  !ImeWindowBypass.ShouldHonorManualOverride(false, true, false));
            Check("★★ 手动强制否决③: 引擎未接管（离开 CAD / 停引擎）→ 必须还原、不留残留",
                  !ImeWindowBypass.ShouldHonorManualOverride(true, false, false));

            // ---- 执行器本身的边界行为（不碰任何真实窗口）----
            var bypass = new ImeWindowBypass();
            Check("执行器: 初始未激活", !bypass.IsActive);
            Check("执行器: 未激活时 Disengage 返回 0 且不抛异常", bypass.Disengage() == 0);
            Check("执行器: 未激活时无待还原窗口", bypass.PendingRestoreCount == 0);

            int visited;
            int detached = bypass.Engage(IntPtr.Zero, out visited);
            Check("执行器: 主窗口句柄为 0（拿不到窗口）→ 不动手、不进入激活态",
                  detached == 0 && visited == 0 && !bypass.IsActive);

            // ★ v6.3：失效句柄绝不允许留下"假激活态"。
            //   假激活的后果很具体：`SyncImeBypass` 看到 `IsActive == true` 就以为
            //   "已经生效了"，于是**后面整条命令都不会再尝试解除关联** ——
            //   用户侧表现正是"命令期间照旧弹候选窗"，而日志上什么都看不出来。
            Check("执行器: 一枚**已失效**的窗口句柄 → 不动手、不进入激活态"
                  + "（绝不留「假激活」）",
                  bypass.Engage(new IntPtr(1), out visited) == 0
                  && visited == 0 && !bypass.IsActive);

            // ---- 「[旁路诊断] 该不该记一行」的判据（★ v6.3）----
            //
            //   ★ 为什么要钉死它：`[旁路诊断]` 是**唯一**能查出「旁路本该生效却没生效」
            //     这条静默失效通路的通道（用户表现为"命令期间仍弹候选窗"），
            //     所以它必须真的记；但反过来，只要漏掉一条"**正常的**不生效"，
            //     日志就会被无意义诊断淹没 —— **刷屏的诊断等于没有诊断**。
            //
            //   ★ 现场真踩过一次（2026-10-07 10:50:16 用户日志）：
            //     面板点了「卸载」之后引擎已 Stop（`_isEngaged` 必然为 false），
            //     而命令事件仍然挂着 ⇒ 每敲一条命令都记一行
            //     `[旁路诊断] 命令=quit **未能解除关联**：接管=False 启用=True …`
            //     —— 引擎都没在跑了，报这个毫无意义。
            Console.WriteLine();
            Console.WriteLine("----- 旁路诊断该不该记（XimeEngine.ShouldDiagnoseBypassSkip）-----");
            Check("★ 旁路诊断: 引擎在跑 + 旁路没生效 + 有非文字类命令 + 机制开着 + 无视模式"
                  + " → **要记**（否则静默失效无从查起）",
                  XimeEngine.ShouldDiagnoseBypassSkip(
                      true, false, true, false, false, true, true));

            Check("★★ 旁路诊断否决①: 引擎没在跑（面板点了「卸载」）→ **不记**"
                  + "（引擎已 Stop，`接管` 必然为 false，报它纯属噪音）",
                  !XimeEngine.ShouldDiagnoseBypassSkip(
                      false, false, true, false, false, true, true));
            Check("旁路诊断否决②: 旁路已经生效 → 不记（无事可报）",
                  !XimeEngine.ShouldDiagnoseBypassSkip(
                      true, true, true, false, false, true, true));
            Check("旁路诊断否决③: 没有活动命令（敲命令之前那一刻）→ 不记",
                  !XimeEngine.ShouldDiagnoseBypassSkip(
                      true, false, false, false, false, true, true));
            Check("★ 旁路诊断否决④: 本插件自己的命令 → 不记（诊断命令不许有副作用）",
                  !XimeEngine.ShouldDiagnoseBypassSkip(
                      true, false, true, true, false, true, true));
            Check("★ 旁路诊断否决⑤: 文字类命令 → 不记（它本就该让输入法工作）",
                  !XimeEngine.ShouldDiagnoseBypassSkip(
                      true, false, true, false, true, true, true));
            Check("★ 旁路诊断否决⑥: 机制被 XIMEBYPASS off 关掉 → 不记（用户自己要求不生效）",
                  !XimeEngine.ShouldDiagnoseBypassSkip(
                      true, false, true, false, false, false, true));
            Check("★ 旁路诊断否决⑦: 「切换输入法模式」（退路模式）→ 不记（本就不靠它）",
                  !XimeEngine.ShouldDiagnoseBypassSkip(
                      true, false, true, false, false, true, false));

            // 逐位翻转：七位里任意一位不成立 ⇒ 一律不记（只有基准那一组才记）
            var diagBase = new[] { true, false, true, false, false, true, true };
            int diagTrue = 0;
            for (int i = 0; i < diagBase.Length; i++)
            {
                var f = (bool[])diagBase.Clone();
                f[i] = !f[i];
                if (XimeEngine.ShouldDiagnoseBypassSkip(
                        f[0], f[1], f[2], f[3], f[4], f[5], f[6]))
                    diagTrue++;
            }
            Check("★★ 旁路诊断: 七个条件里任意一条不成立 ⇒ 一律不记（只有全过才记）",
                  diagTrue == 0);

            return fail;
        }

        /// <summary>
        /// 工作模式（<see cref="XimeWorkMode"/>）的读写与判据测试。
        ///
        /// 背景：SwitchIme（切换输入法模式）在 v29 之前只是**保留值** ——
        ///   界面里被禁用，<c>XimeConfig.Load</c> 会显式拒绝它（退回 IgnoreIme），
        ///   引擎的每个分支都要求 IgnoreIme。现在它已完整实现，
        ///   这几条断言就是"它真的被当作一个正常模式接受了"的证据。
        ///
        /// 同时钉死"写错模式名不会被静默接受"—— 配置文件是鼓励用户手工编辑的，
        /// 拼错一个字母就静默失效，排查成本极高。
        /// </summary>
        private static int RunWorkModeTest(Action<string, bool> check)
        {
            Console.WriteLine();
            Console.WriteLine("===== 工作模式（WorkMode）=====");

            int fail = 0;
            void Check(string what, bool ok)
            {
                check(what, ok);
                if (!ok) fail++;
            }

            // ★★ 默认值 = 「无视输入法模式」（v6 定案；用户明确否决"默认切换输入法模式"）。
            //   一个模式的核心承诺就是它的契约 —— 本插件承诺"绝不修改你的输入法状态"，
            //   默认就必须是守约的那个模式。
            //   ★ 老配置（ConfigVersion<6）里的 SwitchIme 由下面的迁移链负责迁回来
            //     （别只改代码默认值 —— v1.0.3 正栽在这）。
            var def = new XimeConfig();
            Check("★ 新装默认 = IgnoreIme（无视输入法模式：绝不碰用户输入法状态）",
                  def.WorkMode == XimeWorkMode.IgnoreIme);
            Check("★ 新装默认 = 命令期间无视输入法（消除 PassThrough 的候选窗副作用）",
                  def.BypassImeDuringCommand);

            // 引擎支持的模式集合（生产代码与自测共用同一处判据）
            Check("引擎判据: IgnoreIme 被支持", XimeEngine.IsSupportedWorkMode(XimeWorkMode.IgnoreIme));
            Check("引擎判据: SwitchIme 被支持（v29 起已实现）",
                  XimeEngine.IsSupportedWorkMode(XimeWorkMode.SwitchIme));
            Check("引擎判据: 非法值不被支持（不会静默接受）",
                  !XimeEngine.IsSupportedWorkMode((XimeWorkMode)99));

            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Xime.workmode.test.xml");

            // ---------- 迁移链（纯函数，先单独钉住）----------
            // 规则：**只动"当时那个版本号的出厂值"，用户显式选过的值一律保留。**
            Check("★ 迁移链 v4+IgnoreIme（v4 出厂值）→ IgnoreIme（绕一圈回到当前默认）",
                  XimeConfig.MigrateWorkMode(4, XimeWorkMode.IgnoreIme) == XimeWorkMode.IgnoreIme);
            Check("★★ 迁移链 v5+SwitchIme（v5 出厂值）→ IgnoreIme（回到当前默认）",
                  XimeConfig.MigrateWorkMode(5, XimeWorkMode.SwitchIme) == XimeWorkMode.IgnoreIme);
            Check("★★ 迁移链 v5+IgnoreIme（用户在 v5 里显式选过）→ 原样保留",
                  XimeConfig.MigrateWorkMode(5, XimeWorkMode.IgnoreIme) == XimeWorkMode.IgnoreIme);
            Check("★★ 迁移链 v6+SwitchIme（新格式 = 用户显式选择）→ 原样保留，绝不覆盖",
                  XimeConfig.MigrateWorkMode(6, XimeWorkMode.SwitchIme) == XimeWorkMode.SwitchIme);
            Check("★ 迁移链幂等：已是最新格式再迁一次不变",
                  XimeConfig.MigrateWorkMode(6, XimeConfig.MigrateWorkMode(6, XimeWorkMode.SwitchIme))
                  == XimeWorkMode.SwitchIme);

            // (文件里的 WorkMode 值, 期望加载后, 说明)
            // ★ 这里一律用 ConfigVersion=6（当前格式）—— 新格式里的值一律视为**用户显式选择**。
            var cases = new[]
            {
                new object[] { "SwitchIme", XimeWorkMode.SwitchIme,
                               "★ v6 格式里的 SwitchIme = 用户用 XIMEMODE 显式选过 → 原样保留" },
                new object[] { "IgnoreIme", XimeWorkMode.IgnoreIme,
                               "WorkMode=IgnoreIme → 被接受（当前默认）" },
                new object[] { "ignoreime", XimeWorkMode.IgnoreIme,
                               "WorkMode 大小写不敏感（ignoreime → IgnoreIme）" },
                new object[] { "BogusMode", XimeWorkMode.IgnoreIme,
                               "WorkMode 写错 → 退回默认 IgnoreIme（不静默失效）" }
            };

            foreach (var c in cases)
            {
                var text = (string)c[0];
                var expect = (XimeWorkMode)c[1];
                var what = (string)c[2];

                try
                {
                    System.IO.File.WriteAllText(path,
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Xime>\r\n" +
                        "  <ConfigVersion>6</ConfigVersion>\r\n" +
                        "  <Enabled>true</Enabled>\r\n" +
                        "  <EnabledByUser>true</EnabledByUser>\r\n" +
                        "  <WorkMode>" + text + "</WorkMode>\r\n" +
                        "</Xime>\r\n",
                        new System.Text.UTF8Encoding(false));

                    var loaded = XimeConfig.Load(path);
                    Check(what + "（期望 " + expect + "，实际 " + loaded.WorkMode + "）",
                          loaded.WorkMode == expect);
                }
                finally
                {
                    try { System.IO.File.Delete(path); } catch (Exception) { }
                }
            }

            // ---------- 端到端迁移：老配置（v1~v5）里的两种出厂值 ----------
            // ★ v5 之前的出厂默认 = IgnoreIme；v5 那一版的出厂默认 = SwitchIme。
            //   两者都是**出厂值、不是用户选择** ⇒ v6 一律回到 IgnoreIme
            //   （前者绕一圈回来，后者是被用户否决的那一版默认）。
            foreach (var v in new[] { 1, 2, 3, 4, 5 })
            {
                foreach (var pair in new[]
                {
                    new object[] { "IgnoreIme", XimeWorkMode.IgnoreIme,
                                   "出厂值 IgnoreIme（v5 之前）" },
                    new object[] { "SwitchIme", XimeWorkMode.IgnoreIme,
                                   "出厂值 SwitchIme（v5 那一版）" }
                })
                {
                    try
                    {
                        System.IO.File.WriteAllText(path,
                            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Xime>\r\n" +
                            "  <ConfigVersion>" + v + "</ConfigVersion>\r\n" +
                            "  <Enabled>true</Enabled>\r\n" +
                            "  <EnabledByUser>true</EnabledByUser>\r\n" +
                            "  <WorkMode>" + (string)pair[0] + "</WorkMode>\r\n" +
                            "</Xime>\r\n",
                            new System.Text.UTF8Encoding(false));

                        var old = XimeConfig.Load(path);
                        Check("★★ 迁移 v" + v + ": " + (string)pair[2] + " → 自动回到新默认 IgnoreIme",
                              old.WorkMode == XimeWorkMode.IgnoreIme);
                    }
                    finally
                    {
                        try { System.IO.File.Delete(path); } catch (Exception) { }
                    }
                }
            }

            // ---------- 用户显式选择必须保住（当前格式 = v6）----------
            try
            {
                System.IO.File.WriteAllText(path,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Xime>\r\n" +
                    "  <ConfigVersion>6</ConfigVersion>\r\n" +
                    "  <Enabled>true</Enabled>\r\n" +
                    "  <EnabledByUser>true</EnabledByUser>\r\n" +
                    "  <WorkMode>SwitchIme</WorkMode>\r\n" +
                    "</Xime>\r\n",
                    new System.Text.UTF8Encoding(false));

                var kept = XimeConfig.Load(path);
                Check("★★ v6 格式里 SwitchIme（用户显式选的退路）→ 原样保留，不被迁走",
                      kept.WorkMode == XimeWorkMode.SwitchIme);
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch (Exception) { }
            }

            // ---------- 命令期间"无视输入法"开关的读写 ----------
            try
            {
                System.IO.File.WriteAllText(path,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Xime>\r\n" +
                    "  <ConfigVersion>6</ConfigVersion>\r\n" +
                    "  <Enabled>true</Enabled>\r\n" +
                    "  <EnabledByUser>true</EnabledByUser>\r\n" +
                    "  <BypassImeDuringCommand>false</BypassImeDuringCommand>\r\n" +
                    "</Xime>\r\n",
                    new System.Text.UTF8Encoding(false));

                var off = XimeConfig.Load(path);
                Check("BypassImeDuringCommand=false 写盘 → 读回仍为 false（可关掉）",
                      !off.BypassImeDuringCommand);
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch (Exception) { }
            }

            try
            {
                System.IO.File.WriteAllText(path,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Xime>\r\n" +
                    "  <ConfigVersion>4</ConfigVersion>\r\n" +
                    "  <Enabled>true</Enabled>\r\n" +
                    "  <EnabledByUser>true</EnabledByUser>\r\n" +
                    "</Xime>\r\n",
                    new System.Text.UTF8Encoding(false));

                var miss = XimeConfig.Load(path);
                Check("★★ 旧配置**没有** BypassImeDuringCommand 节点 → 保持默认 true（修 bug 的机制必须生效）",
                      miss.BypassImeDuringCommand);
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch (Exception) { }
            }

            // 写盘 / 读回往返：模式必须能持久化
            try
            {
                var c = new XimeConfig { ConfigPath = path };
                c.WorkMode = XimeWorkMode.SwitchIme;
                c.BypassImeDuringCommand = false;
                c.Save();

                var back = XimeConfig.Load(path);
                Check("SwitchIme 写盘 → 读回仍为 SwitchIme（可持久化）",
                      back.WorkMode == XimeWorkMode.SwitchIme);
                Check("BypassImeDuringCommand=false 写盘 → 读回仍为 false（可持久化）",
                      !back.BypassImeDuringCommand);
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch (Exception) { }
            }

            // ---------- 复位是否与构造函数逐项一致（含新字段）----------
            var fresh = new XimeConfig();
            var reset = new XimeConfig();
            reset.EnabledByUser = true; reset.Enabled = false;
            reset.WorkMode = XimeWorkMode.SwitchIme;
            reset.BypassImeDuringCommand = false;
            reset.ResetToFreshInstall();
            Check("★★ 卸载复位后的状态行 == 全新安装的状态行（含 BypassImeDuringCommand）",
                  reset.DescribeState() == fresh.DescribeState());
            Check("★ 复位后 WorkMode 回到默认 IgnoreIme",
                  reset.WorkMode == XimeWorkMode.IgnoreIme);
            Check("★ 复位后 BypassImeDuringCommand 回到默认 true",
                  reset.BypassImeDuringCommand);

            return fail;
        }

        /// <summary>
        /// <c>CommandKeyMode</c>（命令执行期间按键的投递方式）的读写与判据测试。
        ///
        /// 这一项是修复天正「任意布置」二级命令的最终开关，四档：
        ///   · PassThrough  （★ **当前默认**）放行物理键 → 天正这类走 `acedGrRead` 原始按键通道的
        ///                  第三方命令**唯一认的档**；副作用 = 输入法也会看到这些键，
        ///                  该副作用由 v6 的「命令期间无视输入法」(<see cref="ImeWindowBypass"/>)
        ///                  消除 —— 注意它**不改输入法状态**，只是让窗口不接收输入法；
        ///   · PostKey      （v1.0.2 上旬短暂当过默认，**已现场证伪**）吞键 + 投窗口消息给焦点控件 →
        ///                  消息进 AutoCAD 主线程队列后被当命令栏文本（命令栏留小写 a、天正收不到）；
        ///   · PostKeyFrame（v1.0.2 新增，**已现场证伪**）吞键 + 投窗口消息给前台顶层窗口；
        ///   · ReplayVk     （**已现场证伪**）吞键 + 重放真实 VK/扫描码 → 输入法仍会看到，对天正也无优势。
        ///
        /// 断言：默认值、写盘/读回、v25 旧节点迁移、以及**接管判据的真值表**。
        /// </summary>
        private static int RunPassThroughTest(Action<string, bool> check)
        {
            Console.WriteLine();
            Console.WriteLine("===== 命令执行期间按键方式（CommandKeyMode）=====");

            int fail = 0;
            void Check(string what, bool ok)
            {
                check(what, ok);
                if (!ok) fail++;
            }

            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Xime.keymode.test.xml");

            // ★ 当前默认档 = PassThrough：放行物理键（2026-10-06 现场实测，天正唯一认的档）。
            //   PostKey 投绘图区 ⇒ 消息被当命令栏文本，天正收不到 —— 已现场证伪。
            var def = new XimeConfig();
            Check("★ 新装默认 = PassThrough（天正唯一认的档：放行真实物理键）",
                  def.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PassThrough);
            var resetProbe = new XimeConfig { CommandKeyMode = XimeConfig.XimeCommandKeyMode.ReplayVk };
            resetProbe.ResetToFreshInstall();
            Check("★ 复位到全新安装后同样是 PassThrough（与构造函数逐项对齐）",
                  resetProbe.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PassThrough);

            // ---- v25 旧节点必须被**忽略**（按当前默认走，不按它的名字猜）----
            //
            // v25 的 <PassThroughDuringCommand> 默认值就是 true，凡是 v25 写过的配置
            // 都会带上 true —— 它反映的是"用过 v25"，不是"用户选了放行物理键"。
            // 所以**一律忽略**，按当前默认走（当前默认是 PassThrough，
            // 但**结论不能来自这个旧节点** —— 否则将来默认再变时又会跟着漂）。
            foreach (var legacyVal in new[] { "true", "false" })
            {
                try
                {
                    System.IO.File.WriteAllText(path,
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Xime>\r\n" +
                        "  <ConfigVersion>2</ConfigVersion>\r\n" +
                        "  <Enabled>true</Enabled>\r\n" +
                        "  <EnabledByUser>true</EnabledByUser>\r\n" +
                        "  <WorkMode>IgnoreIme</WorkMode>\r\n" +
                        "  <PassThroughDuringCommand>" + legacyVal + "</PassThroughDuringCommand>\r\n" +
                        "</Xime>\r\n",
                        new System.Text.UTF8Encoding(false));

                    var legacy = XimeConfig.Load(path);
                    Check("v25 旧节点 PassThroughDuringCommand=" + legacyVal +
                          " → 忽略，按当前默认走（不按旧节点的名字猜）",
                          legacy.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PassThrough);
                }
                finally
                {
                    try { System.IO.File.Delete(path); } catch (Exception) { }
                }
            }

            // 新节点读写往返
            try
            {
                var c = new XimeConfig { ConfigPath = path };
                c.CommandKeyMode = XimeConfig.XimeCommandKeyMode.PassThrough;
                c.Save();

                var back = XimeConfig.Load(path);
                Check("改为 PassThrough 后写盘 → 读回仍为 PassThrough（可持久化）",
                      back.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PassThrough);
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch (Exception) { }
            }

            // ---------- ★★ v5 迁移：旧配置（ConfigVersion<5）的默认档 ----------
            // 这是「默认档从 PostKey 改回 PassThrough」能真正生效的关键：
            //   老配置文件里写着 PostKey，它是 v1.0.2 上旬的**出厂值**、不是用户选择，
            //   必须迁到新默认 PassThrough（PostKey 已现场证伪：天正不认）；
            //   否则改了默认却会被文件里的旧值盖回去
            //   （v1.0.3 就是栽在这里，用户实机看到「跟没改一样」）。
            //   ★ 同一条桩上顺带验 WorkMode 也必须一起迁（**两个字段是一对**：
            //     只迁档位不迁模式，或反过来，都会出现"改了跟没改一样"）。
            Console.WriteLine();
            foreach (var v in new[] { 1, 2, 3, 4 })
            {
                try
                {
                    System.IO.File.WriteAllText(path,
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Xime>\r\n" +
                        "  <ConfigVersion>" + v + "</ConfigVersion>\r\n" +
                        "  <Enabled>true</Enabled>\r\n" +
                        "  <EnabledByUser>true</EnabledByUser>\r\n" +
                        "  <WorkMode>IgnoreIme</WorkMode>\r\n" +
                        "  <CommandKeyMode>PostKey</CommandKeyMode>\r\n" +
                        "</Xime>\r\n",
                        new System.Text.UTF8Encoding(false));

                    var old = XimeConfig.Load(path);
                    Check("★★ 迁移 v" + v + ": 旧配置的 PostKey（出厂值）→ 自动迁到新默认 PassThrough",
                          old.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PassThrough);
                    Check("★★ 迁移 v" + v + ": 同一条旧配置的 WorkMode IgnoreIme（出厂值）"
                          + "→ 绕一圈仍回到当前默认 IgnoreIme",
                          old.WorkMode == XimeWorkMode.IgnoreIme);
                }
                finally
                {
                    try { System.IO.File.Delete(path); } catch (Exception) { }
                }
            }
            // 用户显式选过的档（非 PostKey）绝不能被迁移覆盖
            foreach (var mode in new[] { "PassThrough", "PostKeyFrame", "ReplayVk" })
            {
                try
                {
                    System.IO.File.WriteAllText(path,
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Xime>\r\n" +
                        "  <ConfigVersion>3</ConfigVersion>\r\n" +
                        "  <Enabled>true</Enabled>\r\n" +
                        "  <EnabledByUser>true</EnabledByUser>\r\n" +
                        "  <WorkMode>IgnoreIme</WorkMode>\r\n" +
                        "  <CommandKeyMode>" + mode + "</CommandKeyMode>\r\n" +
                        "</Xime>\r\n",
                        new System.Text.UTF8Encoding(false));

                    var kept = XimeConfig.Load(path);
                    Check("★★ 迁移: 旧配置里用户显式选的 " + mode + " → 原样保留（绝不覆盖）",
                          kept.CommandKeyMode.ToString() == mode);
                }
                finally
                {
                    try { System.IO.File.Delete(path); } catch (Exception) { }
                }
            }
            // 新格式（ConfigVersion>=5）里的 PostKey 是用户的显式选择 → 必须保留
            try
            {
                System.IO.File.WriteAllText(path,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Xime>\r\n" +
                    "  <ConfigVersion>5</ConfigVersion>\r\n" +
                    "  <Enabled>true</Enabled>\r\n" +
                    "  <EnabledByUser>true</EnabledByUser>\r\n" +
                    "  <WorkMode>IgnoreIme</WorkMode>\r\n" +
                    "  <CommandKeyMode>PostKey</CommandKeyMode>\r\n" +
                    "</Xime>\r\n",
                    new System.Text.UTF8Encoding(false));

                var v5File = XimeConfig.Load(path);
                Check("★★ 迁移: 新格式(v5)里的 PostKey 是**用户显式选择** → 保留（不迁移）",
                      v5File.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PostKey);
                Check("★★ 迁移: 新格式(v5)里的 IgnoreIme 是**用户显式选择** → 保留（不迁移）",
                      v5File.WorkMode == XimeWorkMode.IgnoreIme);
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch (Exception) { }
            }

            // 断言真正的接管判据（生产代码与自测共用 XimeEngine.ShouldPassThroughKey）
            Console.WriteLine();
            var replay = new XimeConfig { CommandKeyMode = XimeConfig.XimeCommandKeyMode.ReplayVk };
            var post = new XimeConfig { CommandKeyMode = XimeConfig.XimeCommandKeyMode.PostKey };
            var pass = new XimeConfig { CommandKeyMode = XimeConfig.XimeCommandKeyMode.PassThrough };

            Check("判据: ReplayVk + 命令执行中 → 仍然接管（吞键后重放真实按键）",
                  !XimeEngine.ShouldPassThroughKey(replay, "RYBZ"));
            Check("判据: PostKey + 命令执行中 → 仍然接管（吞键后投窗口消息；但天正不认 —— 已证伪）",
                  !XimeEngine.ShouldPassThroughKey(post, "RYBZ"));
            Check("判据: PassThrough + 命令执行中 → 放行物理键（天正唯一认的档）",
                  XimeEngine.ShouldPassThroughKey(pass, "RYBZ"));
            Check("判据: PassThrough + 空闲提示符(null) → 仍然接管",
                  !XimeEngine.ShouldPassThroughKey(pass, null));
            Check("判据: PassThrough + 空闲提示符(空串) → 仍然接管",
                  !XimeEngine.ShouldPassThroughKey(pass, string.Empty));
            Check("★ 判据: 新装默认档(PassThrough) + 命令执行中 → **放行物理键**",
                  XimeEngine.ShouldPassThroughKey(def, "RYBZ"));

            // ---------- ★ v1.0.2：默认档 = PassThrough，第 4 档 PostKeyFrame 在列 ----------
            Console.WriteLine();
            Check("★★ 判据 v1.0.2: 新装默认 = PassThrough（天正唯一认的档；已现场实测定案）",
                  def.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PassThrough);
            Check("★ 判据 v1.0.2: 枚举含第 4 档 PostKeyFrame 且值为 3（投前台顶层窗口）",
                  Enum.IsDefined(typeof(XimeConfig.XimeCommandKeyMode), "PostKeyFrame")
                  && (int)XimeConfig.XimeCommandKeyMode.PostKeyFrame == 3);
            // 两档投递目标必须真的不同（v1.0.2 引入 ShouldPostToTopLevelWindow 纯判据）：
            // 只有 PostKeyFrame 打顶层窗口，其余档一律打焦点控件 —— 防止两档悄悄退化成一档。
            Check("判据 v1.0.2: ShouldPostToTopLevelWindow(PostKeyFrame) → true（第 4 档打顶层窗口）",
                  XimeEngine.ShouldPostToTopLevelWindow(XimeConfig.XimeCommandKeyMode.PostKeyFrame));
            Check("判据 v1.0.2: ShouldPostToTopLevelWindow(PostKey) → false（打焦点控件）",
                  !XimeEngine.ShouldPostToTopLevelWindow(XimeConfig.XimeCommandKeyMode.PostKey));
            Check("判据 v1.0.2: ShouldPostToTopLevelWindow(PassThrough) → false",
                  !XimeEngine.ShouldPostToTopLevelWindow(XimeConfig.XimeCommandKeyMode.PassThrough));
            Check("★ 判据 v1.0.2: 四档都能写盘/读回",
                  RoundTripKeyMode(XimeConfig.XimeCommandKeyMode.PostKey)
                  && RoundTripKeyMode(XimeConfig.XimeCommandKeyMode.PassThrough)
                  && RoundTripKeyMode(XimeConfig.XimeCommandKeyMode.ReplayVk)
                  && RoundTripKeyMode(XimeConfig.XimeCommandKeyMode.PostKeyFrame));

            // ---------- ★ v1.0.2：退出接管不许抹掉「当前命令」 ----------
            Console.WriteLine();
            Check("判据 v1.0.2: Disengage（前台抖一下）→ **保留**当前命令",
                  !XimeEngine.ShouldClearCommandOnDisengage(false));
            Check("判据 v1.0.2: Stop / 配置停用（场景结束）→ 清掉当前命令",
                  XimeEngine.ShouldClearCommandOnDisengage(true));

            // ---------- ★ v1.0.2：「卸载只清当前版本」的注册表路径判据 ----------
            Console.WriteLine();
            const string cur2022 = @"Software\Autodesk\AutoCAD\R24.1\ACAD-5101:804\Applications\XIME";
            const string other2014 = @"Software\Autodesk\AutoCAD\R19.1\ACAD-D001:804\Applications\XIME";
            // 同一版本分支下的"空壳键"（历史版本曾把注册项误写进这里）
            const string shell2022 = @"Software\Autodesk\AutoCAD\R24.1\ACAD-5101\Applications\XIME";

            Check("判据 v1.0.2: 卸载**必须**删当前版本（2022 在 2022 里）",
                  RegPath.ShouldRemoveOnUninstall(cur2022, cur2022));
            Check("★★ 判据 v1.0.2: 卸载**绝不许**动其它版本（在 2022 里卸载，2014 必须留下）",
                  !RegPath.ShouldRemoveOnUninstall(other2014, cur2022));
            Check("★★ 判据 v1.0.2: 在 2014 里卸载，2022 必须留下",
                  !RegPath.ShouldRemoveOnUninstall(cur2022, other2014));
            Check("★ 判据 v1.0.2: 同一版本分支下的**空壳键**也要一起清（R24.1 的两种产品键）",
                  RegPath.ShouldRemoveOnUninstall(shell2022, cur2022));
            Check("判据 v1.0.2: 定位不到当前版本 → 一个键都不删（绝不猜）",
                  !RegPath.ShouldRemoveOnUninstall(cur2022, null)
                  && !RegPath.ShouldRemoveOnUninstall(cur2022, string.Empty)
                  && !RegPath.ShouldRemoveOnUninstall(null, cur2022));
            Check("判据 v1.0.2: VersionPathOf 取出的是版本分支（不是产品键）",
                  RegPath.VersionPathOf(cur2022) == @"Software\Autodesk\AutoCAD\R24.1");
            Check("判据 v1.0.2: ProductPathOfAppKey 取出的是产品键（含 :804 语言后缀）",
                  RegPath.ProductPathOfAppKey(cur2022) == @"Software\Autodesk\AutoCAD\R24.1\ACAD-5101:804");
            Check("判据 v1.0.2: 畸形路径不许被误判成「同一版本」（一律返回空串/不删）",
                  RegPath.VersionPathOf(@"XIME") == string.Empty
                  && !RegPath.ShouldRemoveOnUninstall(@"XIME", @"XIME"));

            // ---------- ★ v1.0.2：对外显示版本号 ----------
            Console.WriteLine();
            Check("★★ 对外显示版本号 = 1.0.2（发版时这一行要与 csproj 的 FileVersion 同步改）",
                  Xime.Core.UI.XimeSettingsForm.FallbackVersion == "1.0.2");
            Check("★ 帮助文本里的版本号必须由宿主注入（不许在正文里写死）",
                  HelpTextContainsHardcodedVersion());

            // ---------- ★ v1.0.2：程序集身份 vs 对外显示版本 ----------
            //  这两者**必须是两个不同的东西**，而且各自被钉死：
            //    AssemblyVersion = CLR 程序集身份，恒 1.0.0.0（改了 → 同一次 AutoCAD
            //      会话里换目录 NETLOAD 会变成两套 Xime.Core 并存，补丁打不上）
            //    FileVersion     = 对外显示版本（XimeApplication.Version / 界面标题）
            var asm = typeof(XimeConfig).Assembly;
            Check("★★ AssemblyVersion 必须恒为 1.0.0.0（CLR 程序集身份，不许跟着发版改）",
                  asm.GetName().Version == new Version(1, 0, 0, 0));
            var fvAttr = (System.Reflection.AssemblyFileVersionAttribute)Attribute
                .GetCustomAttribute(asm, typeof(System.Reflection.AssemblyFileVersionAttribute));
            Check("★★ FileVersion = 1.0.2.0（对外显示版本就是它 → 界面显示 v1.0.2）",
                  fvAttr != null && fvAttr.Version == "1.0.2.0");

            return fail;
        }

        /// <summary>把某一档 CommandKeyMode 写盘再读回，验证往返不丢档（v1.0.2 引入）。</summary>
        private static bool RoundTripKeyMode(XimeConfig.XimeCommandKeyMode mode)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Xime.keymode.test.xml");
            try
            {
                var c = new XimeConfig { ConfigPath = path, CommandKeyMode = mode };
                c.Save();
                var back = XimeConfig.Load(path);
                return back.CommandKeyMode == mode;
            }
            catch (Exception) { return false; }
            finally { try { System.IO.File.Delete(path); } catch (Exception) { } }
        }

        /// <summary>
        /// 帮助文本里**不许**出现写死的版本号（v1.0.2）。
        ///
        /// ★ 这条断言来自一次真实事故：帮助正文里写死了 "v1.0.0"，
        ///   改版本号时标题栏改了、帮助没改 —— 用户一眼就看到自相矛盾。
        ///   现在版本号由宿主注入（<c>BuildHelpText(version)</c>）：
        ///   传一个假版本号进去，帮助文本就必须回显这个假版本号
        ///   （回显了 ⇒ 正文没写死；没回显 ⇒ 一定还写着某个字面量）。
        /// </summary>
        private static bool HelpTextContainsHardcodedVersion()
        {
            try
            {
                var help = Xime.Core.UI.XimeSettingsForm.BuildHelpText("9.9.9");
                return help != null
                    && help.Contains("9.9.9")
                    && !help.Contains("1.0.2")
                    && !help.Contains("1.0.0");
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 加载生命周期测试 —— 逐字对应需求：
        ///   ① 首次加载（无配置）  → 默认启动
        ///   ② 重启再次加载        → 默认启动
        ///   ③ 手动关闭后重启加载  → 默认关闭
        ///   ④ 手动开启后重启加载  → 默认开启
        ///   ⑤ 卸载后重新加载      → 视为**全新安装**：默认启动 + 默认无视输入法模式
        ///
        /// 每次「重启」= 重新 Load 同一个文件，完全等价于 AutoCAD 重启后
        /// Initialize() 重新读取配置（Initialize 在 EnabledByUser=false 时还会
        /// 顺手把归一化结果落盘，这里一并模拟）。
        /// </summary>
        private static int RunLoadLifecycleTest(Action<string, bool> check)
        {
            Console.WriteLine();
            Console.WriteLine("===== 加载生命周期（首次 → 重启 → 关 → 重启 → 开 → 重启 → 卸载 → 重启）=====");

            int fail = 0;
            void Check(string what, bool ok)
            {
                check(what, ok);
                if (!ok) fail++;
            }

            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Xime.lifecycle.test.xml");
            try { System.IO.File.Delete(path); } catch (Exception) { }

            try
            {
                // ① 首次加载：文件不存在
                var c1 = XimeConfig.Load(path);
                Check("① 首次加载（无配置文件）→ 默认启动", c1.Enabled);

                // 模拟 XimeApplication.Initialize() 的落盘
                if (!c1.EnabledByUser) c1.Save();

                // ② 重启：文件已存在，用户从未手动动过
                var c2 = XimeConfig.Load(path);
                Check("② 重启后（从未手动关闭过）→ 仍是默认启动", c2.Enabled);

                // ③ 用户手动关闭（界面按钮 / XIMEOFF）
                c2.Enabled = false;
                c2.EnabledByUser = true;
                c2.Save();

                var c3 = XimeConfig.Load(path);
                Check("③ 手动关闭后重启 → 保持关闭（尊重用户选择）", !c3.Enabled);

                // ④ 用户手动开启（界面勾选 / XIMEON）
                c3.Enabled = true;
                c3.EnabledByUser = true;
                c3.Save();

                var c4 = XimeConfig.Load(path);
                Check("④ 手动开启后重启 → 保持开启", c4.Enabled);

                // ⑤ 用户「卸载」（界面按钮）→ 配置文件复位为全新安装
                //    用户实测反馈（v42 复述）：加载-改设置-卸载-重新加载，
                //    重新加载必须回到**初始状态**（默认启用 + 默认无视输入法模式）。
                //    而卸载走的是与「关闭」不同的复位路径（ResetToFreshInstall）。
                c4.Enabled = false;                 // 先把状态弄成"关着、且用户关过"
                c4.EnabledByUser = true;
                c4.WorkMode = XimeWorkMode.SwitchIme;   // ★ 先改成**非默认**值，才能验出复位真的发生
                // ★ v42：把"其他用户可改项"也改成非默认值，验证复位是否**全面**
                //    （早先漏掉 CommandKeyMode，卸载后重新加载会带着它，就不算初始状态）
                c4.CommandKeyMode = XimeConfig.XimeCommandKeyMode.PostKey;
                c4.BypassImeDuringCommand = false;      // ★ v6：同上（命令期间无视输入法）

                int whitelistBefore = c4.ChineseCommands.Count;
                c4.ResetToFreshInstall();
                c4.Save();

                var c5 = XimeConfig.Load(path);
                Check("⑤ 卸载后重新加载 → 默认开启（不再记得'用户关过'）", c5.Enabled);
                Check("⑤ 卸载后重新加载 → 默认无视输入法模式",
                    c5.WorkMode == XimeWorkMode.IgnoreIme);
                Check("⑤ 卸载后重新加载 → EnabledByUser 复位为 false",
                    !c5.EnabledByUser);
                Check("★ ⑤ 卸载后重新加载 → 命令期间按键方式也回到默认（= PassThrough）",
                    c5.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PassThrough);
                Check("★ ⑤ 卸载后重新加载 → 「命令期间无视输入法」回到默认（开）",
                    c5.BypassImeDuringCommand);
                Check("★ ⑤ 卸载**不得**抹掉用户积累的命令白名单（那是用户资产，不是设置项）",
                    c5.ChineseCommands.Count == whitelistBefore && whitelistBefore > 0);

                // ⑤-b ★★ v42：复位必须与"全新安装的默认值"**逐项一致** ——
                //     这两者一旦漂移，就会出现"卸载后重新加载 ≠ 全新安装"这种
                //     说不清的偏差。用可执行断言把这份契约钉住。
                var fresh = new XimeConfig();
                var probe = new XimeConfig();
                probe.Enabled = false;
                probe.EnabledByUser = true;
                probe.WorkMode = XimeWorkMode.SwitchIme;                            // 非默认
                probe.CommandKeyMode = XimeConfig.XimeCommandKeyMode.PostKey;       // 非默认
                probe.UseWeaselCli = false;                // v56：同上（小狼毫命令行桥）
                probe.BypassImeDuringCommand = false;      // v6：同上（命令期间无视输入法）
                probe.ResetToFreshInstall();
                Check("★ ⑤-b ResetToFreshInstall 与「全新安装默认值」逐项一致：Enabled",
                    probe.Enabled == fresh.Enabled);
                Check("★ ⑤-b 逐项一致：EnabledByUser",
                    probe.EnabledByUser == fresh.EnabledByUser);
                Check("★ ⑤-b 逐项一致：WorkMode",
                    probe.WorkMode == fresh.WorkMode);
                Check("★ ⑤-b 逐项一致：CommandKeyMode",
                    probe.CommandKeyMode == fresh.CommandKeyMode);
                Check("★ ⑤-b 逐项一致：UseWeaselCli（v56）",
                    probe.UseWeaselCli == fresh.UseWeaselCli);
                Check("★ ⑤-b 逐项一致：BypassImeDuringCommand（v6）",
                    probe.BypassImeDuringCommand == fresh.BypassImeDuringCommand);
                // ★ v58：<ForceEnglishDuringCommand> 已整体撤除 —— 用"编译期不存在"来钉住它。
                //   若有人把它加回来，这一行会**编译失败**（而不是悄悄放行）。
                //   （保留在注释里作为契约说明，代码层面靠上面几条 DescribeState 比对兜底。）
                Check("★ ⑤-c 全新安装默认「命令期间按键 = PassThrough」（天正唯一认的档）",
                    fresh.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PassThrough);
                Check("★ ⑤-c 全新安装默认「优先走小狼毫命令行 = true」（切换输入法模式时用）",
                    fresh.UseWeaselCli);
                Check("★ ⑤-c 全新安装默认「命令期间无视输入法 = true」（v6：消除候选窗）",
                    fresh.BypassImeDuringCommand);
                Check("★ ⑤-b 全新安装默认就是「无视输入法模式」（v6：用户明确要求改回默认）",
                    fresh.WorkMode == XimeWorkMode.IgnoreIme);
                Check("★ ⑤-b 全新安装默认就是「启用」（EnabledByUser=false）",
                    fresh.Enabled && !fresh.EnabledByUser);

                // ★★ v43：把上面那 4 条"逐字段"比对**升级成"整行相等"**。
                //    理由：`DescribeState()` 是卸载"前/后快照"与启动日志**唯一**的状态描述
                //    定义（面板卸载、宿主写日志、自测三处共用）。只要有人往配置里加了
                //    一个"用户可改项"却忘了加进 DescribeState，这里立刻红 ——
                //    而逐字段比对是**列不出未知字段**的，永远发现不了这种遗漏。
                Check("★★ v43 复位后的状态行必须等于全新安装的状态行（DescribeState 覆盖全部可改项）",
                    probe.DescribeState() == fresh.DescribeState());
                Check("★★ v43/v56/v6 状态行必须报出全部 7 个可改项"
                    + "（Enabled/EnabledByUser/WorkMode/命令期间按键/小狼毫命令行/命令期间无视输入法/白名单）",
                    fresh.DescribeState().Contains("Enabled=")
                    && fresh.DescribeState().Contains("EnabledByUser=")
                    && fresh.DescribeState().Contains("WorkMode=")
                    && fresh.DescribeState().Contains("命令期间按键=")
                    && fresh.DescribeState().Contains("小狼毫命令行=")
                    && fresh.DescribeState().Contains("命令期间无视输入法=")
                    && fresh.DescribeState().Contains("白名单="));
                // ★ v58：v55 的「命令期间强制英文」必须从状态行里**消失** ——
                //   它已整体撤除；若又冒出来说明有人把它加回去了。
                Check("★★ v58 「命令期间强制英文」已从状态行撤除（无视模式绝不碰输入法）",
                    !fresh.DescribeState().Contains("命令期间强制英文"));
                // 反证：状态行必须能反映"脏值"，否则卸载后它永远是同一行、证明不了复位发生了
                //   ★ 脏值取「与当前默认相反的那个模式」—— 写死某个值会在默认反转后失效
                //     （默认已是 SwitchIme；写死 SwitchIme 就不再是脏值，断言恒假失败）。
                var dirty = new XimeConfig();
                dirty.WorkMode = fresh.WorkMode == XimeWorkMode.IgnoreIme
                    ? XimeWorkMode.SwitchIme : XimeWorkMode.IgnoreIme;
                Check("★★ v43 状态行必须能区分出被改过的值（否则'前后快照'证明不了任何事）",
                    dirty.DescribeState() != fresh.DescribeState()
                    && dirty.DescribeState().Contains("WorkMode=" + dirty.WorkMode));
                // 只读契约：DescribeState 绝不允许修改状态（它出现在"卸载前"快照里，
                // 一旦它顺手改了什么，"卸载前"就变成"半卸载后"，整个证据链就废了）。
                // 逐字段比对，别拿 DescribeState 自己比自己（那是同义反复，证明不了什么）。
                var ro = new XimeConfig();
                ro.Enabled = false;
                ro.EnabledByUser = true;
                ro.WorkMode = XimeWorkMode.SwitchIme;
                ro.CommandKeyMode = XimeConfig.XimeCommandKeyMode.PostKey;
                bool roEnabled = ro.Enabled, roByUser = ro.EnabledByUser;
                var roMode = ro.WorkMode;
                var roKey = ro.CommandKeyMode;
                int roVer = ro.ConfigVersion;
                int roWl = ro.ChineseCommands.Count;
                string roLine1 = ro.DescribeState();
                string roLine2 = ro.DescribeState();
                Check("★★ v43 DescribeState 必须是纯只读（改了 5 项后再读两次，字段与输出都不许变）",
                    ro.Enabled == roEnabled && ro.EnabledByUser == roByUser
                    && ro.WorkMode == roMode && ro.CommandKeyMode == roKey
                    && ro.ConfigVersion == roVer && ro.ChineseCommands.Count == roWl
                    && roLine1 == roLine2);

                // ⑤-a 反证：卸载复位必须**真的**与「关闭」区分开 ——
                //     若把卸载也写成 EnabledByUser=true，本断言会失败。
                c5.Enabled = false;
                c5.EnabledByUser = true;
                c5.Save();
                var c6 = XimeConfig.Load(path);
                Check("⑤ 反证：手动关闭仍必须保持关闭（卸载复位没有污染关闭语义）",
                    !c6.Enabled);
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch (Exception) { }
            }

            return fail;
        }

        private static string DescribeMode(ImeMode mode)
        {
            switch (mode)
            {
                case ImeMode.English: return "English（英文）";
                case ImeMode.Chinese: return "Chinese（中文）";
                default: return "Unknown（不响应 IMM32）";
            }
        }
    }
}
