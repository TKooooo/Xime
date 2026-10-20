using System;
using System.Windows.Forms;
using Xime.Core.Native;

namespace Xime.Core
{
    /// <summary>
    /// X 输入法 核心调度器（与 AutoCAD 解耦，便于测试与复用）。
    ///
    /// ================== 模式一：无视输入法模式（★ v1.0.2 无感修订版起**已非默认**，退路） ==================
    ///
    ///   完整工作流程：
    ///
    ///   [进入 CAD 窗口]
    ///     → 只开启键盘钩子，**不修改用户输入法的任何状态**；
    ///     → 命令栏进入「需要英文」状态标记。
    ///
    ///   [命令栏键入]
    ///     → 钩子吞掉原生按键，改由 SendInput(KEYEVENTF_UNICODE) 把
    ///       确定的英文字符直接送进命令栏输入控件；
    ///     → 字符不经过键盘布局、不经过输入法，因此无论用户输入法处于
    ///       中文还是英文态，命令名都是英文；
    ///     → CapsLock 开启时**照常接管**（大小写由 Shift^CapsLock 算出）。
    ///       v35 修正：旧版"CapsLock 开启时完全放行"会让本模式彻底失效 ——
    ///       现场日志里 CapsLock 一开，每个按键都记「跳过: CapsLock 开启」。
    ///
    ///   [命令结束]
    ///     → 清除「需要英文」标记；
    ///     → 若命中中文白名单（TEXT/MTEXT/DDEDIT...），停止拦截，
    ///       用户输入法本就保持其原始状态（通常是中文），可直接打中文。
    ///
    ///   [CAD 最小化 / 失焦]
    ///     → 卸载钩子、退出接管。用户输入法状态自始至终未被改动。
    ///
    /// ================== 模式二：切换输入法模式（★ **当前默认**） ==================
    ///
    ///   ★ 与模式一**完全互斥**：本模式**不吞键、不注入**，
    ///     改为「真的去切输入法的中英状态」——
    ///     不是切键盘布局（绝不调 ActivateKeyboardLayout），
    ///     而是等价于用户手动按一下 Shift 的那种中英切换。
    ///     （键盘钩子仍会安装，但接管判据在本模式下恒为 false ——
    ///       它只是"只看不动"的观察者，用来感知用户自己按的 Shift。）
    ///
    ///   [进入 CAD 窗口]
    ///     → 先记录进入前的中英状态（用于退出时还原）；
    ///     → 把输入法保持在**英文**状态。
    ///
    ///   [执行白名单命令]
    ///     → 命中文白名单（或焦点进入文字编辑框）→ 切成**中文**；
    ///     → 命令结束 / 退出编辑 → 切回**英文**并保持。
    ///
    ///   [CAD 最小化 / 失焦 / 退出]
    ///     → 把输入法还原成进入前的状态（即用户平时的默认输入法状态）。
    ///     → ★ 只针对 AutoCAD 自己的窗口做还原，绝不借用焦点/前台窗口，
    ///       也绝不模拟 Shift —— 那时 CAD 已经失去前台，按焦点操作改的就是
    ///       别的程序的输入法（详见 ImeController.RestoreOnWindow）。
    ///
    ///   ★ 已知局限（如实说明，不夸大）：
    ///     状态读写走 IMM32 与 WM_IME_CONTROL 两条通道，绝大多数输入法
    ///     （微软拼音、搜狗、QQ、微信、极点等）都能用；
    ///     若两条通道都读不出状态，本模式**不会盲切**（盲按 Shift 有 50%
    ///     概率切反），此时界面与 XIMESTATUS 会明确提示改用无视输入法模式。
    ///
    /// ★ 模式一的历史包袱（别再犯）：
    ///     早期版本用 IMM32 去切输入法状态来实现英文，实测在 AutoCAD 命令栏上
    ///     「报告成功但实际不生效」，且会出现中英状态反转 —— 这正是模式一
    ///     改为**完全绕开 IME**、只做按键投递的原因。
    ///     模式二之所以现在能做，是因为换了做法（先探测、已符合就不动、
    ///     探测不到就拒绝），并且能明确告知用户它是否可用。
    /// </summary>
    public sealed class XimeEngine : IDisposable
    {
        private readonly ImeController _ime = new ImeController();
        private readonly FocusMonitor _focus;
        private readonly KeyboardHook _hook = new KeyboardHook();
        private readonly Timer _pollTimer;

        private XimeConfig _config;
        private bool _isEngaged;
        private string _currentCommand;
        private bool _disposed;

        /// <summary>
        /// ★ v6：命令执行期间"让 CAD 窗口无视输入法"的执行器
        /// （消除 <see cref="XimeConfig.XimeCommandKeyMode.PassThrough"/> 的候选窗副作用）。
        /// 判据与安全说明见 <see cref="ImeWindowBypass"/>。
        /// </summary>
        private readonly ImeWindowBypass _imeBypass = new ImeWindowBypass();

        /// <summary>
        /// ★ v6.1：「手动强制解除」态 —— 现场命令 `XIMEBYPASS 1` 置位、`XIMEBYPASS 0` 清除。
        ///
        /// 为什么要这个标志：`XIMEBYPASS 1` 的用途是"让用户回到 CAD 里按几个键试试"，
        /// 而自动同步逻辑在**空闲**（没有命令在执行）时会判定"该还原" ——
        /// 于是用户一离开命令行，刚解除的关联就被撤了，试不成。
        /// 有了这个标志，手动解除会一直保持到用户自己还原。
        ///
        /// ★ 但它**不绕过任何安全闸门**：文字编辑态 / 机制被 off / 未接管时一律作废
        /// （见 <see cref="ImeWindowBypass.ShouldHonorManualOverride"/>）——
        /// 绝不允许把用户卡在"打不出中文"。
        /// </summary>
        private bool _manualImeBypass;

        /// <summary>
        /// 引擎的"定时器 + 焦点监听"是否处于运行态（v42）。
        ///
        /// ★★ 为什么必须单独记：<see cref="Stop"/> 会停掉 500ms 轮询与
        ///   <c>FocusMonitor</c>，但**不**改变配置。卸载流程正是"先 Stop、
        ///   再把配置复位成 Enabled=true"，接着设置面板一关，
        ///   <c>XimeCommands.ShowSettings</c> 会调 <see cref="Refresh"/> ——
        ///   此时若只看 <c>Enabled &amp;&amp; WorkMode</c>，就会 **Engage 一个已经停掉的引擎**：
        ///     · 键盘钩子被重新装上 → 按键仍被吞掉、注入英文；
        ///     · 焦点监听与轮询却是停的 → **永远进不了文字编辑态** →
        ///       用户双击文字怎么都打不出中文。
        ///   这就是"卸载/改设置之后，无视输入法模式也跟着失效"的成因之一。
        ///   现在 <see cref="Refresh"/> 见到"该跑但没跑"会把引擎真正启动起来。
        /// </summary>
        private bool _started;

        /// <summary>
        /// 本次接管所使用的工作模式。
        ///
        /// ★ 为什么不能直接用 <c>_config.WorkMode</c>：用户在设置界面里一改模式，
        ///   配置对象立刻就是新值了，而**撤销上一个模式所做的动作**（卸载钩子、
        ///   还原输入法状态）必须依据"当初是按哪个模式接管的"。
        ///   用配置里的新值去判断，就会出现「从切换输入法模式切到无视输入法模式后，
        ///   用户的输入法被永久留在英文」这种事故。
        /// </summary>
        private XimeWorkMode _engagedMode = XimeWorkMode.IgnoreIme;

        // 调试日志开关（诊断用，写入 %TEMP%\xime_engine.log）
        private static bool _debugLog;
        private static string _debugLogPath;

        /// <summary>开启 / 关闭引擎调试日志。</summary>
        public static void SetDebugLog(bool on)
        {
            _debugLog = on;
            if (!on) return;

            // ★ v45：每次"开启"都从零开始计数 —— XIMEDEBUG 关闭时会如实回报
            //   "本次会话写了多少行"，行数为 0 就是"你这一轮没被记录"的硬证据。
            _dbgLines = 0;
            _dbgDropped = 0;

            try
            {
                _debugLogPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "xime_engine.log");
                PrepareLogFile(_debugLogPath);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 打开（或继续追加）调试日志文件。
        ///
        /// ★★ 绝不能把上一份日志**直接清掉**（v38 事故，务必保留这段记录）：
        ///
        ///   现场就是这样丢掉证据的 —— 用户先跑完一轮实机测试（19:32~19:36），
        ///   然后敲 `XIMEDEBUG` 想看日志，而旧实现一句 `WriteAllText` 把整份现场
        ///   覆盖成一行标题，只剩 2892 字节的"会话尾巴"（还正好是切模式 + 退出 CAD）。
        ///   故障恰好在"用户想看日志"的那一刻被抹掉，是排查中最不该发生的事。
        ///
        ///   策略：
        ///     · 文件已存在 → **追加**一行会话分隔，旧内容一字不删；
        ///     · 文件超过 4MB → 先轮转一份 `xime_engine.prev.log` 再开新的（防无限增长）。
        ///
        /// ★ 抽成带路径参数的独立方法是为了让自测工具能用**可执行断言**钉住
        ///   "再次开启日志不会抹掉现场" —— 这条契约一旦回退，损失是无法挽回的
        ///   （现场只存在于那一瞬间），靠注释记住不可靠。
        /// </summary>
        internal static void PrepareLogFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            const string Banner = "==== X输入法 引擎日志 ";
            const long MaxBytes = 4L * 1024 * 1024;

            // ★ v45：与 Dbg 共用同一把锁 —— 否则"开日志"这一刻正在写的那些行
            //   会与本次追加撞车（旧实现就是这么丢行的）。
            lock (DbgSync)
            {
                bool exists = System.IO.File.Exists(path);
                long size = 0;
                if (exists)
                {
                    try { size = new System.IO.FileInfo(path).Length; }
                    catch (Exception) { }
                }

                // 过大 → 轮转一份 prev 再开新文件（旧文件不删，改名为 prev）
                if (exists && size >= MaxBytes)
                {
                    try
                    {
                        System.IO.File.Copy(path,
                            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path),
                                                   "xime_engine.prev.log"), true);
                    }
                    catch (Exception) { }
                    exists = false;
                }

                if (exists)
                {
                    System.IO.File.AppendAllText(path,
                        "\r\n" + Banner + DateTime.Now + "（日志开关被再次打开，以下为本次会话）====\r\n");
                }
                else
                {
                    System.IO.File.WriteAllText(path,
                        Banner + DateTime.Now + " ====\r\n"
                        + "（本文件按会话追加；超过 4MB 会轮转一份 xime_engine.prev.log）\r\n");
                }
            }
        }

        /// <summary>
        /// 把**一行**写进日志文件 —— 线程安全、带重试（v45 新增，是"日志丢行"的根治点）。
        ///
        /// ★★★ 为什么要专门抽这一个方法（v45 实机事故）：
        ///
        ///   旧实现每次 `Dbg` 直接调用 `File.AppendAllText`。它内部以
        ///   `FileShare.Read` 打开文件，**两次并发调用会撞车**，后到的那次抛
        ///   IOException —— 而调用点的 `catch (Exception) { }` 把它**静默吞掉**。
        ///   于是**整整一行日志凭空消失**。
        ///
        ///   `Dbg` 的调用方至少来自四个线程：
        ///     · 低级键盘钩子回调（每次击键）
        ///     · SetWinEventHook 焦点回调
        ///     · 500ms 轮询定时器（线程池）
        ///     · ThreadPool.QueueUserWorkItem 的切换任务
        ///   所以"写字的时候刚好在打字"就会丢行 —— 而**丢的往往正是最关键的那一行**：
        ///   实机 v44 日志里 `21:55:08.933 切换输入法：本次未能通过「真实 Shift」达成 → 登记待补`
        ///   这一行的**前因**（"读不出当前状态 → 不盲动"或"另一处正在切换"）就
        ///   一个字都没有，凭空出现。
        ///
        ///   危害远不止"少了一行"：
        ///     ① 排查时会把"丢行"当成"插件没动作"，方向直接跑偏；
        ///     ② 计数型结论（"全日志 0 次 结果=False"）可能是**假阴性** ——
        ///        恰好在失败那一刻丢行，看起来就像没失败过。
        ///
        ///   修法：进程内共用一把 `DbgSync` 锁串行化 + 失败重试 3 次；
        ///   实在写不进去就**计数**，并在下一次成功写入时补一条通报行，
        ///   让"日志本身丢过行"这件事**可见**，而不是悄悄发生。
        /// </summary>
        /// <returns>是否写入成功。</returns>
        internal static bool TryAppendLogLine(string path, string line)
        {
            if (string.IsNullOrEmpty(path)) return false;

            lock (DbgSync)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        System.IO.File.AppendAllText(path, line);

                        // 上一次有丢行 → 在本行之后补一条通报，让缺口可见。
                        if (_dbgDropped > 0)
                        {
                            int n = _dbgDropped;
                            _dbgDropped = 0;
                            try
                            {
                                System.IO.File.AppendAllText(path,
                                    DateTime.Now.ToString("HH:mm:ss.fff")
                                    + "  ⚠ 日志缺口通报：此前有 " + n
                                    + " 行日志因文件被占用写入失败（v45 起已加锁 + 重试；"
                                    + "若仍出现，说明有外部程序占用该文件）\r\n");
                            }
                            catch (Exception) { _dbgDropped = n; }
                        }
                        _dbgLines++;
                        return true;
                    }
                    catch (Exception)
                    {
                        System.Threading.Thread.Sleep(2);
                    }
                }

                _dbgDropped++;
                return false;
            }
        }

        /// <summary>调试日志路径。</summary>
        public static string DebugLogPath { get { return _debugLogPath; } }

        /// <summary>本次开启日志以来**成功写入**的行数（v45，供 XIMEDEBUG 如实回报）。</summary>
        public static int DebugLogLines { get { return _dbgLines; } }

        /// <summary>本次开启日志以来**写入失败**的行数（v45；正常情况下应恒为 0）。</summary>
        public static int DebugLogDropped { get { return _dbgDropped; } }

        // ★ v45：日志写入的进程内串行化锁与计数（见 TryAppendLogLine 的注释）。
        private static readonly object DbgSync = new object();
        private static volatile int _dbgLines;
        private static volatile int _dbgDropped;

        private static void Dbg(string msg)
        {
            if (!_debugLog) return;
            TryAppendLogLine(_debugLogPath,
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
        }

        /// <summary>
        /// 「关键事件」出口：既进引擎日志，也进**始终记录**的启动日志（v45）。
        ///
        /// 动机（本轮实机教训）：用户复现故障时**常常忘了先开 XIMEDEBUG**，
        /// 于是一整轮测试一行日志都没有 —— 排查只能靠猜。
        /// 但把整份引擎日志都倒进启动日志又不行（量太大，启动日志只有 256KB 上限，
        /// 会挤掉 `[卸载]` 那类关键证据）。
        ///
        /// 所以只放行**低频、高价值**的几类事件：
        ///   · 输入法切换的**失败/降级**（结果=False 或 方式 不可靠）
        ///   · 「我们可能把输入法留在了英文」这条证据的**记下与撤销**
        ///   · 还原的三种结局（成功 / 未达成 / 拿不到靶点）
        ///   · Engage / Disengage（带模式）
        /// 一次会话通常只有几行到几十行。
        /// </summary>
        private void Note(string msg)
        {
            Dbg(msg);
            var sink = KeyEventSink;
            if (sink == null) return;
            try { sink("  [引擎] " + msg); }
            catch (Exception) { }
        }

        /// <summary>
        /// 关键事件的落地出口，由宿主（XimeApplication）注入为 WriteStartupLog（v45）。
        /// 为 null 时退化为"只写引擎日志"。
        /// </summary>
        public Action<string> KeyEventSink { get; set; }

        // 命令栏当前是否需要英文（由命令状态驱动）
        private bool _commandLineNeedsEnglish;

        /// <summary>
        /// v59：上一次「误判自愈」（<see cref="ReclaimEnglishForIdlePrompt"/>）写日志的时刻。
        /// 自愈发生在**每次按键**的路径上，日志必须限流，否则一次敲命令能刷几十行。
        /// </summary>
        private int _reclaimLogTick;

        /// <summary>v59：自愈日志的最小间隔（毫秒）。</summary>
        private const int ReclaimLogGapMs = 2000;

        /// <summary>
        /// v60：上一次「命令期间改注入字符」（<see cref="ShouldSeizeCommandKeyAsCharNow"/>）
        /// 写日志的时刻。
        ///
        /// 与 v59 的自愈一样，这个判定位于**每次按键**的路径上（命令执行期间每个字母都会走），
        /// 日志必须限流，否则敲一条命令就能刷几十行。
        /// </summary>
        private int _seizeLogTick;

        /// <summary>v60：上述日志的最小间隔（毫秒）。</summary>
        private const int SeizeLogGapMs = 2000;

        /// <summary>
        /// 是否处于「文字编辑框」编辑态。
        ///
        /// 场景：双击文字对象进入编辑时，部分第三方插件（如**天正单行文字**）
        /// **不经过 AutoCAD 命令系统**，`CommandWillStart` 完全不触发，
        /// 因此无法靠命令白名单识别。此时只能靠「焦点落入文字编辑框」来判断：
        /// 一旦焦点进入 AutoCAD 进程内的可编辑控件（且不是命令栏），
        /// 就说明用户正在输入文字内容 → 应允许中文。
        ///
        /// 该标志为 true 期间，键盘钩子一律不接管（把输入权交还输入法）。
        /// </summary>
        private bool _inTextEditMode;

        /// <summary>
        /// ★★★ v46：最近一次"焦点落入的那个**文字编辑控件**"（<see cref="IntPtr.Zero"/> = 当前不在编辑控件上）。
        ///
        /// 它解决的是一条被漏掉的时序契约：**"焦点落到一个新的文字编辑控件"就是一次全新的
        /// 「马上要输入中文」场景**，必须重新执行一次"确保中文"。
        ///
        /// 旧实现把"进入文字编辑态"当成**一次性闩锁**（`if (_inTextEditMode) return;`），
        /// 而"退出编辑态"只有两条触发路径：
        ///   · 焦点**明确回到命令栏**（<see cref="OnFocusObjectChanged"/>）；
        ///   · **按键时**焦点已不在编辑框（<see cref="ShouldCaptureKey"/> 的实时校验）。
        /// 用户最自然的操作 —— 「双击文字 A → 打字 → **直接双击文字 B**」—— 两条都不满足
        /// （焦点走的是 编辑框 → 绘图区 → 编辑框，鼠标双击本身不产生按键事件），
        /// 于是闩锁一直卡住，后续每一次双击都被 `return` 吞掉：
        ///   · 无视输入法模式 → 不再执行「确保中文」；
        ///   · 切换输入法模式 → 不再请求「需要中文（文字输入）」，
        ///     而那条通路平时**故意把输入法按在英文上**，于是用户双击文字后打出来的全是英文。
        /// 这正好是用户反馈的「有概率切换失效、多试几次又能恢复」。
        ///
        /// 实机铁证（v45 引擎日志，切换输入法模式）：
        ///   22:18:49.646  进入文字编辑态（焦点在编辑框）→ 允许中文     ← 唯一的一次
        ///   22:18:52.367  焦点变化: 0x981DE0 类名=Edit 是编辑框=True   ← 被吞
        ///   22:18:54.907  焦点变化: 0x8024F2 类名=Edit 是编辑框=True   ← 被吞
        ///   …（共 9 次被吞，"需要中文（文字输入）"请求 0 次）
        ///   22:19:12.038  退出文字编辑态（兜底巡检：长时间未在编辑框）  ← 卡了 22.4 秒
        /// </summary>
        private IntPtr _lastTextEditHost = IntPtr.Zero;

        // 防抖：焦点事件可能高频触发，记录最近一次判定结果避免重复刷日志
        private IntPtr _lastFocusHwnd = IntPtr.Zero;

        // 编辑态兜底巡检计数器（见 EditModeSecuritySweep）
        private int _editModeIdleTicks;

        /// <summary>
        /// 用户最近一次**手动切换输入法**（单按 Shift）的时刻（Environment.TickCount），
        /// 0 表示从未手动切换过。
        ///
        /// 背景：插件从不主动改输入法状态（这是「无视输入法模式」的根基），于是留下
        /// 一个副作用 —— 用户一旦自己按 Shift 切成英文，插件不会替他切回来，之后
        /// 双击文字 / 执行 TEXT 类命令时就一直打不出中文（用户实测反馈的 bug）。
        ///
        /// 解决思路（关键词：**只在入口处动手，输入过程中绝不干预**）：
        ///   · 用户单按 Shift 的那一刻，记下时间戳；
        ///   · 之后**进入新的中文输入场景**（进入文字编辑态 / 文字类命令开始）时，
        ///     探测当前输入法 —— 若是英文就主动切回中文；
        ///   · 用户正在输入的那一段文字里，插件一次都不切换，
        ///     他要按 Shift 切英文打英文标注，插件完全不干扰。
        ///
        /// 带 60 秒时效（<see cref="UserToggleValidMs"/>）：超时即视作用户已自行
        /// 处理，避免陈旧标记在很久以后造成误切。
        /// </summary>
        private volatile int _userImeToggleTick;

        /// <summary>「用户手动切换过输入法」标记的有效期（毫秒）。</summary>
        private const int UserToggleValidMs = 60000;

        /// <summary>
        /// 「确保中文」最近一次**真正执行切换**的时刻（Environment.TickCount）。
        ///
        /// 同一次操作会从两个入口几乎同时发出请求 —— `OnCommandWillStart`（文字类命令）
        /// 与 `EnterTextEditMode`（焦点落入编辑框）。若两次都真的各按一下 Shift，
        /// 等于连按两下，刚切好的中文又被切回英文。
        /// 因此规定该间隔内只允许真正切换一次。
        /// </summary>
        private int _lastEnsureChineseTick;

        /// <summary>两次真实切换之间的最小间隔（毫秒），见 <see cref="_lastEnsureChineseTick"/>。</summary>
        private const int EnsureChineseMinGapMs = 1500;

        /// <summary>
        /// ★★★ v42：「我们自己把输入法留在了英文」的硬证据。
        ///
        /// 为什么必须有它 —— 这是「改成无视输入法模式也无法输入中文」的根治点：
        ///
        ///   实机日志（v41，20:32:05~20:32:26）连续 5 次双击修改，无视模式下的
        ///   「确保中文」每次都读到「Chinese」→ 判「已是中文，无需动作」→ 一个 Shift
        ///   都不发；而用户**打不出中文**。整个日志里 `已发送一次 Shift` 出现 **0 次**：
        ///   这套机制从未真正动作过，所以它根本没能力修复"输入法停在英文"。
        ///
        ///   为什么会停在英文？用户刚从「切换输入法模式」切到「无视输入法模式」——
        ///   切换模式整段期间是把输入法按在英文上的，退出时的还原靠
        ///   <see cref="TryFlushPendingRestore"/> 补做，而**补做是否真的生效无法自证**
        ///   （兼容层读数会滞后、靶点窗口的读数可能是默认值）。
        ///
        ///   所以：只要本次接管明确把输入法切成了英文（<c>_switchApplied == English</c>），
        ///   就记下这条硬证据。无视模式下的第一次「确保中文」据此**无视那个可疑的
        ///   Chinese 读数**，发一次真实 Shift —— 与切换模式铁律「自己的动作结果优先于
        ///   探测」完全同源。
        ///
        /// 生命周期（防止误切）：
        ///   · 置位：切换模式接管结束时 `_switchApplied == English`（见 Disengage）；
        ///   · 清除：① 无视模式下据此动作过一次（证据已消费）；
        ///           ② 切换模式重新接管（它有自己的状态模型）；
        ///           ③ 可信地读到中文（说明确实无需动作）。
        ///   全新安装后直接进无视模式时本项恒为 false —— 行为与过去完全一致，零回归。
        /// </summary>
        /// <summary>「我们可能把输入法留在了英文」这条证据的来源描述（v44，仅用于日志审计）。</summary>
        private string _imeLeftEnglishWhy;
        /// <summary>该证据被记下的时刻（v44，TickCount）。</summary>
        private int _imeLeftEnglishTick;

        private volatile bool _imeLeftEnglish;

        /// <summary>
        /// ★ v42：「确保中文」待补重试的登记时刻（0 = 无待补）。
        ///
        /// 为什么需要它（实机日志铁证，v41 20:31:38）：
        ///   天正 TTEXT 弹出编辑框的瞬间，焦点会在
        ///     编辑框(0xA01188) → AfxMDIFrame100u → MDIClient → 绘图区(0xC51032)
        ///   之间被抢走，直到 **880ms** 后才回到编辑框。而 v41 的
        ///   <see cref="WaitForTextEditHost"/> 只等 150ms → 靶点解析失败 →
        ///   「状态不可探测且无手动切换记录 → 保持现状」→ 一个 Shift 都不发。
        ///   用户看到的就是"这一回双击修改打不出中文"（有点概率性）。
        ///
        ///   现在：等不到就**登记待补**，由焦点事件与 500ms 轮询在编辑控件真正到位时
        ///   补做 —— 与 <see cref="_pendingSwitch"/> / <see cref="_restorePendingMode"/>
        ///   同一套思路（"焦点事件 + 轮询双通道，任一生效即可"）。
        /// </summary>
        private int _ensurePendingTick;

        /// <summary>待补「确保中文」的原因（仅用于日志）。</summary>
        private string _ensurePendingReason;

        /// <summary>待补「确保中文」的有效期（毫秒）—— 超时丢弃，避免陈旧请求乱切。</summary>
        private const int EnsurePendingTtlMs = 5000;

        /// <summary>待补「确保中文」两次重试之间的最小间隔（毫秒），焦点抖动时防止高频重试。</summary>
        private const int EnsurePendingMinGapMs = 80;

        // Shift 单按识别用：记录按下时刻，用于区分
        //   · 单按 Shift    → 切换中英输入（用户手动接管）
        //   · Shift + 字母  → 打大写字母（不是切换）
        private int _shiftDownTick;
        private bool _shiftAlone;

        /// <summary>输入法切换任务门闩（0=空闲 1=进行中），避免并发重复切换。</summary>
        private int _imeSwitchBusy;

        // 防止钩子重入
        private int _injectGuard;

        #region 切换输入法模式（SwitchIme）状态

        /// <summary>
        /// 进入 CAD 接管**之前**用户自己的中英状态。
        ///
        /// 用途：CAD 退出 / 最小化 / 失焦时还原 —— 也就是用户说的
        /// 「切回电脑的默认输入法状态」。
        ///
        /// ★ 为什么是"进入前的状态"而不是"某个写死的默认值"：
        ///   用户平时的默认状态就是他自己切出来的那个状态，
        ///   凭空指定一个（例如永远还原成中文）反而会覆盖他的选择。
        /// </summary>
        private ImeMode _switchOriginalMode = ImeMode.Unknown;

        /// <summary>是否已经记录过原始状态（未记录时不做还原）。</summary>
        private bool _switchOriginalCaptured;

        /// <summary>
        /// 记录下来的原始状态是否来自**可信的** IME 承接窗口（v35 新增）。
        ///
        /// ★ 为什么必须单独记：靶点只剩主窗口兜底时，读到的值是 IME 兼容层
        ///   的**默认值**（实测恒为「中文」）。拿这个默认值去"还原"，
        ///   等于把用户的输入法锁死在一个我们从没真正读到过的状态上 ——
        ///   与「探测不出状态就什么都不做」这条铁律直接冲突。
        /// </summary>
        private bool _switchOriginalTrusted;

        /// <summary>
        /// 「进入前的原始状态」还没采到**可信**值 —— 等一次可信读数补记（v39 新增）。
        ///
        /// ★★ 为什么需要它（v39 实机日志定案）：
        ///   用户切工作模式的唯一入口是设置面板，点下按钮的**那一刻前台就是面板**，
        ///   于是 <see cref="ResolveAcadImeTarget"/> 只能兜底到 CAD 主窗口，
        ///   读回来的「中文」是兼容层默认值 —— `_switchOriginalTrusted` 被置 false。
        ///   随后的"切英文"要等 CAD 回到前台才补做，那次**读到的是可信值**，
        ///   可代码当时并没有把它记下来 —— 于是退出时 `RestoreOriginalIme`
        ///   一律"采样不可信 → 跳过"，**输入法就永久停在英文上**。
        ///   用户现象：「来回切换模式，双击修改就打不出中文」。
        ///
        ///   现在：进入时采不到就标 pending；等我们**真的要动手切**的那一刻
        ///   （靶点已可信、且我们还没改过输入法）把读数补记成"用户的原始状态"。
        /// </summary>
        private volatile bool _switchOriginalPending;

        /// <summary>
        /// 待**延迟还原**的目标（-1 表示没有）（v39 新增）。
        ///
        /// ★ 为什么还原必须能延迟：
        ///   还原发生在 <see cref="Disengage"/>（CAD 失去前台）—— 那一刻前台
        ///   **必然是设置面板或别的程序**，`CanSendRealKey()` 恒为 false，
        ///   发不出真实 Shift。而"直设转换状态"对 TSF/国产输入法**报成功但实际无效**
        ///   （见 <see cref="ImeController"/> 的 manner 契约）——
        ///   于是"还原"从来就没有真正生效过。
        ///   现在登记下来，等焦点回到 AutoCAD（焦点事件 / 500ms 轮询）立刻补一次真实 Shift。
        /// </summary>
        private volatile int _restorePendingMode = -1;

        /// <summary>延迟还原的登记时刻（TickCount），用于超时放弃，避免陈年记录乱翻。</summary>
        private volatile int _restorePendingTick;

        /// <summary>延迟还原的有效期（毫秒）：超过就放弃，宁可不动也不猜。</summary>
        private const int RestorePendingTtlMs = 15000;

        /// <summary>
        /// 我们**确信**输入法当前所处的状态；Unknown 表示不确定。
        ///
        /// 它是"能否盲切"的唯一依据：只有当它明确等于目标的反面时，
        /// 探测不可用才允许轻按一次 Shift（见 <see cref="ApplySwitchIme"/>）。
        /// </summary>
        private ImeMode _switchApplied = ImeMode.Unknown;

        // ★★★ v48：回读通道健康度统计（**纯诊断**，不参与任何决策）。
        //
        //   起因（AutoCAD 2022 + 天正 T20 + 小狼毫 v47 实机）：小狼毫走 TSF，
        //   IMM32 两条通道读到的**恒为兼容层默认值「中文」**，111 次回读
        //   全部 Inconclusive、读到 English 0 次 —— 回读校验在那台机器上
        //   **完全失效**，却一条警告都没有。
        //
        //   这里只累计：总次数 / Inconclusive 次数 / 出现过的不同读数个数。
        //   够样本后由 IsReadbackChannelUseless 判"通道失效"，用 Note 报一次
        //   （始终记录、只报一次，不刷屏）。**不改任何切换行为**。
        private int _readbackSamples;
        private int _readbackInconclusive;
        private int _readbackDistinctMask;   // 位 0 = 读到过 Unknown，位 1 = English，位 2 = Chinese

        /// <summary>
        /// ★★★ v61：本标志已从"纯诊断"**升格为决策输入**（见
        /// <see cref="ShouldTakeOverKeys"/>）。
        ///
        /// 它在**输入法探测线程**（<see cref="TrackReadbackHealth"/>）上写，
        /// 却在**键盘钩子线程**（<see cref="ShouldCaptureKeyCore"/>）上读 ——
        /// 因此必须是 volatile，否则兜底可能迟迟不生效（JIT 可以把读到的值缓存住）。
        /// </summary>
        private volatile bool _readbackChannelWarned;
        private const int ReadbackChannelMinSamples = 8;

        /// <summary>
        /// ★★★ v49：<see cref="_switchApplied"/> 这条记录是**在哪个窗口上**写下的。
        ///
        /// 起因（AutoCAD 2022 + 天正 T20 + 小狼毫 v48 实机，日志 15:25~15:27）：
        ///   切换的真正靶点会在**两个不同的 IME 上下文**之间切换 ——
        ///     · 命令栏控件 `HwndWrapper[…0x40A82]`（"需要英文（命令栏）"落在这里）；
        ///     · 文字编辑框 `Edit`（"需要中文（文字输入）"落在这里，每次都换一个新句柄）。
        ///   IME 状态（尤其是小狼毫这类 TSF 输入法）是**挂在具体窗口/上下文上**的，
        ///   在一个窗口上按的 Shift，**另一个窗口不一定跟着变**。
        ///
        ///   而 `_switchApplied` 是**全局单一变量** —— 于是它必然与实际漂移：
        ///   在命令栏上写 English、又在编辑框上写 Chinese，两个窗口各自可能停在原处。
        ///   更糟的是回读通道在这台机器上读不出真实态（v48 已钉住：45/45 Inconclusive），
        ///   所以这个漂移**无声无息地累积**，最后表现为"有一定概率切换逻辑不对"。
        ///
        /// 修法：记录"这条记录是在哪个窗口上写下的"。当靶点窗口与它**不一致**时，
        ///   说明换了一个 IME 上下文 —— 那条记录**不能再用**（`IsAppliedStateStale`），
        ///   必须重新动手，而不是走"本模式已置于 X → 无需动作"的短路。
        /// </summary>
        private IntPtr _switchAppliedHwnd = IntPtr.Zero;

        /// <summary>
        /// ★★★ v49：把状态记录连同"它是在哪个窗口上成立的"一起清掉。
        /// **唯一**的清除入口 —— 散在各处手写 `_switchApplied = Unknown` 迟早会
        /// 漏掉 `_switchAppliedHwnd`，那就又回到"拿 A 窗口的记录给 B 窗口用"。
        /// </summary>
        private void ClearAppliedState()
        {
            _switchApplied = ImeMode.Unknown;
            _switchAppliedHwnd = IntPtr.Zero;
        }

        /// <summary>
        /// 最近一次被请求的目标状态（-1 表示"没有待处理请求"）。
        ///
        /// 作用：切换动作在线程池里延迟执行，执行前用它复核"我是不是最新的那个请求"，
        /// 避免「命令已经结束了、早先排队的'切中文'才刚跑到」这种过期动作。
        /// </summary>
        private volatile int _switchWanted = -1;

        /// <summary>
        /// **待补**的输入法切换目标（-1 表示没有）。
        ///
        /// ★★ 为什么需要它 —— 这是 v33 实机事故的直接根治点：
        ///
        ///   用户切换工作模式的唯一入口就是设置面板。点下「切换输入法模式」的那一刻，
        ///   前台是**面板**，于是：
        ///     · 真实 Shift 被前台护栏挡下（这是对的，Shift 会落到面板上）；
        ///     · 只剩"直设转换状态"一条路，而它对这类输入法**报成功但实际无效**；
        ///     · 结果用户一合上面板、回到 CAD 敲第一个字符，输入法还是中文 ——
        ///       命令名根本打不出来，用户看到的就是「切换输入法模式还是不行」。
        ///
        ///   所以"这次没切成"必须被**记住**，等 CAD 重新成为前台时立刻补一次真实 Shift。
        ///   不能只等下一个命令事件：用户很可能合上面板就直接敲命令，
        ///   而那时按键已经先被中文输入法吃掉了，命令事件永远不会到来（死循环）。
        ///
        /// 补做时机：焦点回到 CAD、以及 500ms 轮询兜底（见 TryFlushPendingSwitch）。
        /// </summary>
        private volatile int _pendingSwitch = -1;

        /// <summary>待补切换的剩余重试次数与它对应的目标（换目标即重新计数）。</summary>
        private ImeMode _pendingTriesTarget = ImeMode.Unknown;
        private int _pendingTries;

        /// <summary>待补切换最多重试几次（只在"真的尝试过"时才计数，等待期间不计）。</summary>
        private const int PendingSwitchMaxTries = 4;

        /// <summary>
        /// 当前输入法是否支持状态读写。
        ///
        /// false 表示 IMM32 与 WM_IME_CONTROL 两条通道都读不出来 ——
        /// 此时切换输入法模式**无法工作**，界面与 XIMESTATUS 会据此明确提示
        /// 「请改用无视输入法模式」，而不是让用户面对一个静默失效的开关。
        /// </summary>
        private bool _switchDetectable = true;

        /// <summary>进入 CAD 前的原始中英状态（诊断 / 状态栏用）。</summary>
        public ImeMode SwitchImeOriginalMode { get { return _switchOriginalMode; } }

        /// <summary>当前输入法是否可探测（诊断用；false = 本模式下无法工作）。</summary>
        public bool SwitchImeDetectable { get { return _switchDetectable; } }

        /// <summary>当前输入法所处的状态（我们确信的值，诊断用）。</summary>
        public ImeMode SwitchImeAppliedMode { get { return _switchApplied; } }

        /// <summary>
        /// 诊断用：**待补**的输入法切换目标（Unknown = 没有待补）。
        ///
        /// ★ 为什么必须让用户看得见：切换模式的入口是设置面板，那一刻前台是面板、
        ///   发不出真实 Shift，只能把"没切成"记下来等 CAD 回到前台再补。
        ///   若这个补做一直不成功，用户看到的仍然是「切换输入法模式不行」，
        ///   而日志里只能看到"登记待补"、看不到后续 —— 它到底还在等（正常）
        ///   还是已经放弃了（异常），必须能在现场一眼看出来。
        /// </summary>
        public ImeMode SwitchImePendingMode
        {
            get { int p = _pendingSwitch; return p < 0 ? ImeMode.Unknown : (ImeMode)p; }
        }

        /// <summary>诊断用：待补切换已真正尝试过的次数（上限 PendingSwitchMaxTries）。</summary>
        public int SwitchImePendingTries { get { return _pendingTries; } }

        /// <summary>
        /// 诊断用：切换输入法时**实际操作的靶点窗口**，以及它的读数**是否可信**。
        ///
        /// XIMEPROBE 会把靶点与 CAD 主窗口的读数**并列**打印出来 ——
        /// 这两处读数一旦不一致，就说明"采样端 / 应用端 / 还原端"的靶点没对齐，
        /// 那正是历史上"切换输入法模式不起作用"的头号根因。
        ///
        /// ★ 为什么还要 `trustworthy`：靶点落在没有 IME 上下文的窗口上时
        ///   （绘图区 / 主窗口），读数是兼容层的默认值（恒为中文）。v34 现场
        ///   日志里"需要中文→探测已是 Chinese 无需动作"就是它造成的。
        ///   打印出来，用户一眼就能看出这次的读数该不该信。
        ///
        /// ★ 为什么必须一次解析同时返回：分两次调用的话，期间焦点/编辑态可能
        ///   已经变了，打印出的"窗口"与"可信度"会对不上 —— 诊断输出自相矛盾
        ///   比不输出更糟。
        /// </summary>
        internal IntPtr DiagnosticImeTarget(out bool trustworthy)
        {
            return ResolveAcadImeTarget(out trustworthy);
        }

        /// <summary>
        /// 诊断用：此刻是否允许发送真实按键（模拟 Shift）。
        ///
        /// 判据见 <see cref="CanSendRealKey"/>：前台必须是 AutoCAD，且不能是本插件
        /// 自己的面板（否则 Shift 落在面板上，对命令栏毫无作用，属于白费一次按键）。
        /// </summary>
        internal bool DiagnosticCanSendRealKey() { return CanSendRealKey(); }

        #endregion

        /// <summary>
        /// 诊断开关：置位后钩子**只吞键、不注入**。
        ///
        /// 用途（XIMEHOOKTEST）：验证「低级键盘钩子吞键」在真实 AutoCAD 上是否生效。
        ///   · 若命令栏出现字符 → 吞键失效（AutoCAD 可能走 Raw Input 等旁路）；
        ///   · 若命令栏毫无反应 → 吞键生效，注入通道可以放心使用。
        /// </summary>
        public static bool SwallowOnly { get; set; }

        /// <summary>钩子吞键成功的累计次数（诊断用）。</summary>
        public int SwallowCount { get; private set; }

        public XimeEngine(IntPtr acadMainWindow, XimeConfig config)
        {
            _config = config ?? new XimeConfig();
            _focus = new FocusMonitor(acadMainWindow);
            _focus.CadActivated += (s, e) => Engage();
            _focus.CadDeactivated += (s, e) => Disengage();
            // 焦点落到可编辑控件 → 可能进入「文字编辑」状态（覆盖双击文字等
            // 不经过命令系统的编辑场景，如天正单行文字）。
            _focus.FocusObjectChanged += (hwnd) => OnFocusObjectChanged(hwnd);

            // 键盘钩子决策：是否需要接管该按键
            _hook.ShouldCapture = ShouldCaptureKey;
            // 键盘钩子处理：把字符投递给命令栏
            _hook.OnCaptured = OnKeyCaptured;
            // 键盘钩子诊断：记录看到的每个按键
            _hook.OnSeen = (vk, down, captured) =>
            {
                // ★ 先做手势识别（Shift / Esc）——不依赖调试日志开关，
                //   因为它们关系到正确的接管行为，而非仅仅打日志。
                TrackUserGesture(vk, down);

                if (!_debugLog || !down) return;
                // ★ 把「当前命令」一并打出来：排查「某命令执行期间按键行为异常」
                //   （如天正 RYBZ 选中块后的二级命令失灵）时，这几项就是定案依据 ——
                //     · 拦截=True  → 按键被插件接管并注入，问题在注入通道；
                //     · 拦截=False → 按键原样交给 AutoCAD，问题在输入法抢键。
                Dbg(string.Format("按键 vk=0x{0:X2}({1}) 拦截={2} 命令={3} 需英文={4} 编辑态={5}",
                    vk, (Keys)vk, captured,
                    _currentCommand ?? "-",
                    _commandLineNeedsEnglish ? "是" : "否",
                    _inTextEditMode ? "是" : "否"));
                if (!captured) DbgWhyNot(vk);
            };
            // Shift 抬起事件（钩子的诊断回调只在「按下」时触发，抬起只能靠这里）
            _hook.OnShiftChanged = OnShiftChanged;

            _pollTimer = new Timer { Interval = 500 };
            _pollTimer.Tick += (s, e) =>
            {
                _focus.EvaluateState(raiseEvents: true);
                // 编辑态兜底巡检：防止「焦点不回命令栏」导致编辑态永久卡住
                EditModeSecuritySweep();
                // ★ 待补输入法切换的兜底补做。
                //   这是「切换输入法模式」能否真的生效的**最后一道保险**：
                //   用户切模式的唯一入口是设置面板，那一刻发不出真实 Shift，
                //   只能把"没切成"记下来；等 CAD 回到前台后补一次真 Shift。
                //   焦点事件在"关掉面板、CAD 重获前台"时未必可靠（面板是自有
                //   窗口，可能不触发 FocusObjectChanged），500ms 轮询是唯一不漏的通道。
                TryFlushPendingSwitch("轮询兜底");
                // ★ v39：延迟还原同样靠这条"唯一不漏的通道"补做 ——
                //   面板是自有窗口，关掉它未必触发 FocusObjectChanged。
                TryFlushPendingRestore("轮询兜底");
                // ★ v42：无视模式下"等不到编辑控件"的确保中文同样靠它兜底 ——
                //   天正 TTEXT 弹框时焦点会被 CAD 主框架抢走近 1 秒，
                //   150ms 的短轮询等不到，必须留一条不漏的补做通道。
                TryFlushPendingEnsure("轮询兜底");
                // ★ v6：命令期间"无视输入法"的状态同步。
                //   放在这条"唯一不漏的通道"上是刻意的 —— 命令事件偶尔会漏
                //   （第三方命令不走 CommandEnded、Esc 取消、面板抢焦点…），
                //   而"输入法与窗口的关联没还原"会让用户打不出中文，
                //   必须有兜底把它拉回来。
                SyncImeBypass("轮询兜底");
            };
        }

        public ImeController Ime { get { return _ime; } }
        public KeyboardHook Hook { get { return _hook; } }
        public FocusMonitor Focus { get { return _focus; } }

        /// <summary>命令栏当前是否处于「需要英文输入」状态（诊断用）。</summary>
        public bool CommandLineNeedsEnglish { get { return _commandLineNeedsEnglish; } }

        /// <summary>是否处于文字编辑态（焦点在编辑框，允许中文）。</summary>
        public bool InTextEditMode { get { return _inTextEditMode; } }

        /// <summary>最近一次应捕获判断的结果（诊断用）。</summary>
        public bool LastShouldCapture { get; private set; }

        /// <summary>钩子实际捕获并注入字符的累计次数（诊断用）。</summary>
        public int CapturedCount { get; private set; }
        public XimeConfig Config
        {
            get { return _config; }
            set { _config = value ?? new XimeConfig(); }
        }
        public bool IsEngaged { get { return _isEngaged; } }
        public string CurrentCommand { get { return _currentCommand; } }

        /// <summary>
        /// 键盘钩子的 UI 线程锚点。
        /// 由 CAD 层设置（通常为 AutoCAD 主窗体的 WinForms 包装），
        /// 保证钩子在有消息循环的 UI 线程上安装，避免系统输入卡顿。
        /// </summary>
        public System.Windows.Forms.Control HookUiAnchor { get; set; }

        #region 生命周期

        public void Start()
        {
            if (_disposed) return;
            _started = true;

            _focus.Start();
            _pollTimer.Start();

            // ★ 先主动评估一次前台状态：SetWinEventHook 只在**前台发生变化**时回调，
            //   而 NETLOAD 通常是在 CAD 已经是前台的情况下执行的 —— 那时不会有
            //   任何事件到来，只能靠 500ms 轮询兜底，插件会「慢半拍」才生效。
            //   这里显式评估一次，保证「加载完成 = 立刻接管」。
            _focus.EvaluateState(raiseEvents: true);

            if (_focus.IsCadActive)
                Engage();
        }

        public void Stop()
        {
            // ★ v1.0.2：走 "engineStopping: true" —— 引擎停止是**场景结束**，
            //   这是唯一该把"当前命令"抹掉的路径（见 Disengage 的注释）。
            Disengage(engineStopping: true);
            _pollTimer.Stop();
            _focus.Stop();
            _started = false;

            // ★★ v6 硬保险：不管上面那条路径有没有走到（Disengage 在"未接管"时会早退），
            //   停引擎就**绝不允许**把"输入法与窗口的关联被解除"的状态留在用户机器上 ——
            //   那会让用户在 AutoCAD 里彻底打不出中文。
            //   ★ v6.1：连"手动强制"标志一起清掉，否则 Refresh() 重启引擎后
            //     SyncImeBypass 会立刻因为旧标志又把关联解除一遍（用户莫名其妙打不出中文）。
            _manualImeBypass = false;
            try { _imeBypass.Disengage(); }
            catch (Exception) { }
        }

        /// <summary>
        /// 「退出接管时该不该把"当前命令"一并抹掉」—— 纯判据，自测钉死（v1.0.2）。
        ///
        /// 只有一种情况该清：**引擎真的停了**（<see cref="Stop"/> / 配置停用）。
        /// "当前前台不是 CAD"这类暂时退出**一律保留** ——
        /// 命令是否在执行属于 AutoCAD 的事实，不随我们的接管状态变化。
        ///
        /// ★ 抽成纯函数的理由：这条契约失效时的表现是"命令期间按键从"放行"
        ///   悄悄退回"吞键注入""，而**日志上只多一行 Disengage**，看不出因果关系。
        ///   实机症状就是「天正任意布置二级命令时好时坏」——
        ///   靠注释守不住，必须有可执行断言。
        /// </summary>
        internal static bool ShouldClearCommandOnDisengage(bool engineStopping)
        {
            return engineStopping;
        }

        public void UpdateAcadWindow(IntPtr hwnd)
        {
            _focus.UpdateAcadWindow(hwnd);
        }

        #endregion

        #region 接管 / 退出

        /// <summary>
        /// 当前配置里的工作模式是否被引擎支持。
        ///
        /// ★ 单独抽成一个方法，是为了让「引擎的判据」和「自测工具的断言」
        ///   共用同一处定义 —— 否则两边各写一遍，日后一定会各自漂移
        ///   （配置文件是允许用户手工编辑的，写错模式名是很常见的事）。
        /// </summary>
        internal static bool IsSupportedWorkMode(XimeWorkMode mode)
        {
            return mode == XimeWorkMode.IgnoreIme || mode == XimeWorkMode.SwitchIme;
        }

        /// <summary>
        /// ★★★ v61：本模式下，键盘钩子是否应当**接管命令栏按键**。
        ///
        /// ─────────────── 为什么会有这条判据 ───────────────
        ///
        /// 两个工作模式的**英文来源**完全不同：
        ///   · 无视输入法模式（IgnoreIme）—— 英文来自**钩子**：吞掉物理键、
        ///     用 <c>SendInput(KEYEVENTF_UNICODE)</c> 直接注入命令栏，**不碰输入法状态**。
        ///   · 切换输入法模式（SwitchIme）—— 英文来自**真的把输入法切到英文**，
        ///     钩子只做观察者（历史实现：`WorkMode != IgnoreIme` 直接早退）。
        ///
        /// 第二条路有一个此前被默认成立的隐含前提：**这台机器上"切换输入法中英态"
        /// 这件事是能成的**。v48 的诊断已经把这个前提打碎了一半 ——
        /// 它钉住了「回读通道失效」（`Inconclusive` 占满样本、English 读数出现 0 次），
        /// 也就是**切没切成根本不知道**，切换只能靠猜。
        ///
        /// 现场证据（报障用户 AutoCAD 2024 + 搜狗输入法，随测试视频一起提供的
        /// `xime_startup.log` 2026-10-14 14:17 会话）：
        ///   · 整个会话 `Disengage` 全是"切换输入法模式"措辞、
        ///     `需要中文（文字输入）` 出现 4 次、`需要英文（命令栏）` 出现 **0 次**；
        ///   · 视频里同一个"切换输入法模式"下**两个方向都出错**：
        ///     命令行敲 `tt` 弹出的是搜狗候选窗（该英文却成了中文），
        ///     而在位文字编辑器里敲 `doubaoshurufa` 却原样留在拉丁字母（该中文却成了英文）。
        ///   ⇒ 这正是"只能靠猜"的典型表现：切换成不成功完全随机。
        ///
        /// ─────────────── 本判据做什么 ───────────────
        ///
        /// **当该输入法的中英态通道已被证实不可信时，让切换输入法模式也退回钩子兜底。**
        /// 命令栏的英文从此由钩子直接注入 —— 与输入法状态无关，**确定性成立**；
        /// 输入法状态的切换照旧尽力而为（成了更快，不成为无损）。
        ///
        /// ★ 为什么可以安全地在切换模式下启用钩子：接管与否的**真正闸门**并不是模式，
        ///   而是 <c>_commandLineNeedsEnglish</c> —— 它只在这两种时刻为 true：
        ///   空闲命令提示符 / 非文字类命令期间；而进入文字编辑（
        ///   <see cref="SwitchToChinese(bool)"/>）与"需要中文"的命令都会把它清掉，
        ///   此时钩子一律放手、输入法照常组字打中文。
        ///   天正 RYBZ 那类"焦点在绘图区读原始按键"的场景，另由 v60 的
        ///   <c>ShouldSeizeCommandKeyAsCharNow</c>（焦点必须在可组字控件上）挡在外面。
        ///
        /// ★ 抽成纯函数的理由：这条契约一旦写错，失效形式是"打不出中文"或
        ///   "命令被吃成拼音"，靠注释守不住，必须有可执行断言（XimeTest v61 组）。
        /// </summary>
        /// <param name="mode">当前工作模式（配置里的值）。</param>
        /// <param name="switchChannelUnusable">
        /// 该输入法的 IMM32 中英态通道是否**已被证实不可信**
        /// （<see cref="IsReadbackChannelUseless"/> 判定通过，即 <c>_readbackChannelWarned</c>）。
        /// </param>
        internal static bool ShouldTakeOverKeys(XimeWorkMode mode, bool switchChannelUnusable)
        {
            // 无视输入法模式：本来就是钩子通路，契约一字不变。
            if (mode == XimeWorkMode.IgnoreIme) return true;

            // 切换输入法模式：只有"切不动也读不出"时才退回钩子兜底。
            //   ★ 通道还能用时**不要**接管 —— 那等于把切换模式偷偷变成无视模式，
            //     白白多出"动用户输入法状态"的副作用。
            if (mode == XimeWorkMode.SwitchIme) return switchChannelUnusable;

            return false;   // 未知模式一律不接管（与 Engage 的守门一致）
        }

        /// <summary>
        /// <see cref="ShouldTakeOverKeys"/> 的**事实采集端**：配置里的模式 +
        /// v48 已判定的"该输入法中英态通道不可信"。
        ///
        /// 生产代码只走这一个入口（决策 / 执行 / 诊断共用），避免日后漂移。
        /// </summary>
        private bool KeysTakeOverEnabled
        {
            get { return ShouldTakeOverKeys(_config.WorkMode, _readbackChannelWarned); }
        }

        public void Engage()
        {
            if (_disposed) return;
            if (!_config.Enabled) { Dbg("Engage 跳过：未启用"); return; }
            if (!IsSupportedWorkMode(_config.WorkMode))
            { Dbg("Engage 跳过：未知的工作模式 " + _config.WorkMode); return; }
            if (_isEngaged) return;

            _isEngaged = true;
            _engagedMode = _config.WorkMode;

            // ================= 切换输入法模式 =================
            //   ★ 本模式**不吞键、不注入**：它靠"真的切输入法状态"打英文，
            //     按键必须原样交给输入法（否则用户打不了中文）。
            //     但**仍然安装键盘钩子**，且钩子的接管判据在本模式下恒为 false
            //     （见 ShouldCaptureKeyCore 的 `_config.WorkMode != IgnoreIme` 早退），
            //     因此它是"只看不动"的观察者。
            //
            //   ★ 为什么要装：本模式需要知道"用户自己按了 Shift 切中英"。
            //     Shift 是全局热键，钩子是**唯一**能可靠观察到它的地方。
            //     不知道这件事的后果很具体：我们自己记录的状态（_switchApplied）
            //     会因为用户的一次手动切换而失效，若不及时作废，
            //     插件就会拿着过期的记录判断"无需动作"，用户会感觉"不跟手"。
            if (_config.WorkMode == XimeWorkMode.SwitchIme)
            {
                _currentCommand = null;
                _inTextEditMode = false;
                _lastTextEditHost = IntPtr.Zero;   // ★ v46：连同"当前编辑控件"一起归零
                _lastFocusHwnd = IntPtr.Zero;
                _shiftAlone = false;
                _commandLineNeedsEnglish = true;

                // ★★★ v39：先把"上一轮退出时欠下的还原"补掉，**再**采样。
                //   顺序至关重要：若上一轮的延迟还原还没做（当时前台是面板、发不出真实
                //   Shift），此刻输入法可能还停在英文；直接往下走会把"英文"当成用户
                //   的新原始状态记下来，用户以后切出 CAD 就永久停在英文上了。
                //   ★ 补不成（前台仍是面板）也**必须**作废这条旧记录 ——
                //     新模式自己管状态，留着它会在"焦点回到 CAD"时把输入法翻成中文，
                //     正好和切换模式要的英文打架。跨模式欠下的账到此为止。
                TryFlushPendingRestore("重新进入切换模式前");
                _restorePendingMode = -1;

                // ★ v42：切换模式有自己完整的状态模型（_switchApplied），
                //   "我们留下过英文"是给**无视模式**用的证据，跨模式作废。
                _imeLeftEnglish = false;

                // ① 先记录"进入前的状态"——退出/最小化时要还原成它
                CaptureOriginalIme();

                // ② 观察用户手势（只装钩子，不接管任何按键）
                TryInstallHook();

                // ③ 把输入法保持在英文
                RequestSwitchIme(ImeMode.English, "进入 CAD：把输入法保持在英文");

                Dbg("Engage（切换输入法模式）：不接管按键（仅观察用户手势）；"
                    + "钩子=" + (_hook.IsInstalled ? "已安装" : "未安装")
                    + " 进入前状态=" + _switchOriginalMode
                    + " 可探测=" + _switchDetectable);
                return;
            }

            // ================= 无视输入法模式（原有通路，保持不变）=================

            // ★★★ v39：把上一个"切换模式"会话欠下的还原补掉。
            //   用户"来回切换模式"时，退出切换模式那一刻前台是设置面板 → 发不出真实
            //   Shift → 还原被推迟；等他切回无视模式，输入法就停在英文上，双击文字
            //   修改**打不出中文**。这里补一次；补不成（前台还是面板）就留着，
            //   由焦点事件 / 500ms 轮询在焦点回到 CAD 时补做。
            TryFlushPendingRestore("切回无视模式");

            // ★★★ v42：如果上一个"切换模式"会话把输入法留在了英文（或还原没能落地），
            //   这里**必须**把这条证据带进无视模式 —— 否则用户切回无视模式后双击文字
            //   依然打不出中文（他反馈的原话：「一旦没切换回中文模式，即使改成无视
            //   输入法模式也无法输入中文」）。证据在下次进入文字编辑时被消费一次。
            if (_imeLeftEnglish)
                Dbg("Engage（无视输入法模式）：带着「我们可能把输入法留在了英文」的证据进入"
                    + " —— 下次进入文字编辑时会无条件切回中文一次");

            // ★ 每次接管都是"全新场景"：待补「确保中文」的重试预算从零开始，
            //   上一轮登记的待补也随之作废（属于上一轮编辑态的事）。
            _ensurePendingTick = 0;
            _ensurePendingReason = null;

            // 解析一次命令栏输入控件，供后续投递使用
            var frame = HostTextFrame.Resolve();
            // ★ v45：接管/模式是"用户到底在哪个模式"的唯一权威记录，改走 Note。
            Note("Engage：进入接管（" + _engagedMode + "），CAD窗口=0x"
                + _focus.AcadMainWindow.ToInt64().ToString("X") +
                " 命令栏控件=0x" + frame.ToInt64().ToString("X") +
                (frame != IntPtr.Zero ? " 类名=" + HostTextFrame.GetClassNameSafe(frame) : ""));

            // ★ v58：删掉了 v55 在这里加的 `CaptureOriginalIme()`。
            //   无视输入法模式的承诺是"**绝不碰**用户输入法状态"，既然不再切，
            //   也就没有"进入前的状态"需要记，更没有退出时的还原义务。
            //   —— 这条正是用户明确要求回退的：不要动我的输入法。
            TryInstallHook();
            Dbg("Engage 后 钩子=" + (_hook.IsInstalled ? "已安装" : "未安装") +
                " 锚点=" + (HookUiAnchor != null ? HookUiAnchor.GetType().Name : "null"));

            // 命令栏默认需要英文（绘图命令为主）
            _commandLineNeedsEnglish = true;
            _inTextEditMode = false;
            _lastTextEditHost = IntPtr.Zero;   // ★ v46：连同"当前编辑控件"一起归零
            _lastFocusHwnd = IntPtr.Zero;
            _shiftAlone = false;

            // ★ 同上：每次接管都是"全新场景"，待补的重试预算必须从零开始。
            _pendingSwitch = -1;
            _pendingTries = 0;
            _pendingTriesTarget = ImeMode.Unknown;

            // ★ v48：回读通道健康度也按"每次接管 = 全新场景"重新累计 ——
            //   它的判据是"连续多次读不到真实态"，跨接管累计会把
            //   "上个会话读不出来"与"这个会话读不出来"混成一锅。
            //   只清计数、**不清** _readbackChannelWarned（一次会话报一次就够）。
            _readbackSamples = 0;
            _readbackInconclusive = 0;
            _readbackDistinctMask = 0;
            // 注：_userImeToggleTick 不在此清零 —— 用户切走 CAD 再切回来的间隙里
            //     输入法状态并没有被恢复，保留标记才能在其后正确切回中文；
            //     陈旧的标记由 60 秒时效自然淘汰。

            // ★ v6：进入接管时同步一次（通常什么都不做；只保证状态机的起点是干净的）。
            SyncImeBypass("进入接管");
        }

        public void Disengage()
        {
            Disengage(engineStopping: false);
        }

        /// <summary>
        /// 退出接管。
        ///
        /// ★★ v1.0.2：`_currentCommand`（"当前正在执行的命令"）**不再无条件抹掉**。
        ///
        ///   为什么（实测日志钉死）：`Disengage` 原来会清 `_currentCommand`，而
        ///   `Disengage` 的触发点包括"前台不是 CAD 了"这种**暂时**退出 ——
        ///   用户切出去看一眼再切回来（实测启动日志里 Engage/Disengage 一秒一组地抖），
        ///   命令状态就被抹掉了。而"命令正在执行"恰恰是
        ///   <see cref="ShouldPassThroughKey"/> 的唯一依据 ——
        ///   于是命令期间按键会从"完全放行"**悄悄退回**"吞键 + 注入字符"，
        ///   而注入的字符第三方命令（天正任意布置）认不出来：
        ///   现场表现正是"命令栏留个小写字母、命令毫无动作"。
        ///
        ///   语义上本来就不该清：**"AutoCAD 有没有命令在执行"是 AutoCAD 的事实，
        ///   不是我们接管状态的一部分**。我们退出接管，(AutoCAD 的)命令还在跑。
        ///   真正"场景结束"的只有两种：引擎停止（<see cref="Stop"/>）与配置停用。
        /// </summary>
        /// <param name="engineStopping">
        ///   true = 引擎真的停了（Stop / 配置停用）→ 这次退出是**场景结束**，清命令状态；
        ///   false = 只是"当前前台不是 CAD"这类**暂时**退出 → 命令状态原样保留。
        /// </param>
        private void Disengage(bool engineStopping)
        {
            if (!_isEngaged)
            {
                // 即使未接管也要确保钩子被卸载
                SafeUninstallHook();
                return;
            }

            // ★ 用「本次接管所用的模式」判断，而不是配置里的当前值：
            //   用户可能刚在界面里把模式从"切换输入法"改成了"无视输入法"，
            //   此时仍必须按旧模式去收尾（还原输入法状态），否则他的输入法
            //   会永远留在英文。详见 _engagedMode 的注释。
            bool switchMode = _engagedMode == XimeWorkMode.SwitchIme;

            // ★ v45：改走 Note —— "退出的是哪个模式 / 有没有还原义务"是判定
            //   "输入法会不会被留在英文"的第一现场。
            Note(switchMode
                ? "Disengage：退出接管（切换输入法模式 → 还原用户默认输入法状态）"
                : "Disengage：退出接管（不触碰输入法状态）");

            _isEngaged = false;
            // ★★ 见本方法的注释：只有"场景结束"才清当前命令。
            if (ShouldClearCommandOnDisengage(engineStopping))
                _currentCommand = null;
            _commandLineNeedsEnglish = false;
            _inTextEditMode = false;
            _lastTextEditHost = IntPtr.Zero;   // ★ v46：连同"当前编辑控件"一起归零
            _lastFocusHwnd = IntPtr.Zero;

            // ★★ v6：退出接管 = 必须把"无视输入法"的状态**还原干净**。
            //   这一步是"用户切走 CAD 后仍打不出中文"的最后一道防线：
            //   判据里 `engaged=false` ⇒ 一定会走还原分支。
            SyncImeBypass("退出接管");

            // ★ v42：待补的「确保中文」属于上一轮编辑态，退出接管即作废。
            _ensurePendingTick = 0;
            _ensurePendingReason = null;

            // ★ 无论是哪种模式退出，都要把"我们确信的输入法状态"记录作废。
            //   它是模式二用来避免重复切换的可信记录，一旦退出接管就失去意义；
            //   留着它会让下一次接管（尤其是从切换模式切回无视模式、再切回来）
            //   拿着过期记录判定"无需动作"，表现为"切换不跟手"。
            _switchWanted = -1;
            // ★ `_switchApplied` **不在这里**清（v39）：切换模式的收尾（还原）还要靠它
            //   判断"我们到底把输入法切成了什么" —— 见 RestoreOriginalIme /
            //   ShouldRestoreOriginalIme。无视模式无需还原，在下面分支里直接作废。

            // ★ 待补的切换同样作废：退出接管意味着 CAD 已不是前台，
            //   RestoreOriginalIme 会把用户的输入法状态还原回去 ——
            //   此时再补做一次"切英文"既无意义，还可能作用到用户已经切过去的
            //   别的程序上。重新 Engage 时会按新场景重新登记。
            _pendingSwitch = -1;

            // ★★ 重试预算也必须一起清零（v38）—— 这是"来回切换模式后，切换模式
            //   就有可能失效"的直接成因之一：
            //   `_pendingTries` 是"针对某个目标连续失败了几次"的计数，达到
            //   `PendingSwitchMaxTries` 后 `MarkPendingSwitch` 会**直接放弃**、
            //   连待补都不再登记。既然 Disengage 已经把"待补"本身清掉了，
            //   计数就成了孤儿 —— 用户下一次切进切换模式时，会拿着上一轮的失败
            //   次数继续往上加，于是"刚用就报已重试 4 次、直接放弃"，
            //   切换模式从此再也补不回来（这就是 ClearPendingSwitch 注释里
            //   写明的"必须连计数一起清"，只是漏在了模式切换这条路径上）。
            _pendingTries = 0;
            _pendingTriesTarget = ImeMode.Unknown;

            SafeUninstallHook();

            // ★ 切换输入法模式：CAD 失焦 / 最小化 → 立刻还原用户的输入法状态。
            //   （只要还挂在 CAD 的英文态上，用户切到别的程序就打不出中文了。）
            //   ★ v39：此刻前台多半就是设置面板 → 发不出真实 Shift，
            //     RestoreOriginalIme 会登记"延迟还原"，等焦点回到 CAD 再补做。
            // ★ v58：恢复 v51 的收尾语义 —— 只有切换模式才有还原义务。
            //   无视输入法模式从不改输入法状态，因此这里**绝不还原**：
            //   还原动作本身就是在动用户的输入法（用户明确要求不要这样）。
            if (switchMode) RestoreOriginalIme();
            else
            {
                // ★ 无视输入法模式：从不修改输入法状态，也**没有**还原义务。
                //   这里保留待延迟还原（若有）：它是上一个"切换模式"会话欠下的账，
                //   由 Engage（切回无视模式）或焦点事件/轮询补做 —— 不补的话，
                //   用户切回无视模式后输入法还停在英文，双击文字就打不出中文。
                ClearAppliedState();
            }

            // ★★★ v42：切换模式整段期间是把输入法按在英文上的。只要退出时我们确信的
            //   最终状态仍是英文，就留下这条硬证据 —— 供无视模式的「确保中文」在下次
            //   进入文字编辑时**据此真正发一次 Shift**。
            //
            //   为什么**不看还原是否报成功**：还原只有真实 Shift 才算数，而那一刻前台
            //   常常是设置面板 → 发不出 → 登记延迟还原；即便报成功，靶点窗口的读数也可能
            //   是兼容层默认值，无法自证。实机日志（v41）正是如此：`补做延迟还原 … 结果=True`，
            //   用户随后双击修改依然打不出中文。所以一律留证据，由无视模式消费一次即清。
            //
            //   ★ v58：恢复"只有切换模式才可能把输入法留在英文"这一前提。
            if (switchMode && _switchApplied == ImeMode.English)
                MarkImeLeftEnglish("退出接管时，我们确信的最终状态仍是英文");

            HostTextFrame.Invalidate();
        }

        private void TryInstallHook()
        {
            try
            {
                if (!_hook.IsInstalled)
                    _hook.Install(HookUiAnchor);
            }
            catch (Exception) { }
        }

        private void SafeUninstallHook()
        {
            try { _hook.Uninstall(); }
            catch (Exception) { }
        }

        /// <summary>
        /// 命令栏进入「需要英文」状态。
        ///
        /// 两种模式下的动作完全不同：
        ///   · 无视输入法模式 —— **不切换输入法**，只置标志。真正的英文输入由钩子
        ///     把按键直接投递给命令栏实现（绕过 IME）。
        ///     好处：用户输入法始终保持其原始状态，绝不被打乱；
        ///           不存在「切换报告成功但实际无效」的问题；退出插件后一切原样。
        ///   · 切换输入法模式 —— 置标志之外，还**真的把输入法切到英文并保持**。
        ///     这是该模式下"命令结束后回到英文"的统一出口：
        ///     命令结束 / 命令取消 / 退出文字编辑态 三处都汇聚到这里，
        ///     所以只需要在这一个地方挂切换动作，三条路径就都覆盖到了。
        /// </summary>
        private void ForceEnglish()
        {
            ForceEnglish(false);
        }

        /// <param name="urgent">
        /// 焦点**已经稳定**（刚退出文字编辑态 / 命令已结束）→ true：
        /// 请求不再排队等待，立刻执行。见 <see cref="SwitchDelayMs"/> 的说明。
        /// </param>
        private void ForceEnglish(bool urgent)
        {
            _commandLineNeedsEnglish = true;

            // ★★ 铁律（v58 恢复）：无视输入法模式**只置标志，绝不动输入法状态**。
            //   真正的英文输入由钩子把按键直接投递给命令栏实现（绕过 IME）——
            //   用户输入法始终保持其原始状态，退出插件后一切原样。
            //
            //   v55 曾在这里"命令期间强制切英文"（`ForceEnglishDuringCommand`），
            //   v58 已整体撤除：RYBZ 二级命令改由"命令期间放行物理键"解决
            //   （见 XimeConfig.CommandKeyMode = PassThrough）。那才是"不动输入法"
            //   就能修好的方向，也是用户明确要求的方向。
            if (_config.WorkMode != XimeWorkMode.SwitchIme)
            {
                Dbg("ForceEnglish: 仅置标志（无视输入法模式不切换输入法）");
                return;
            }

            Dbg("ForceEnglish: 切输入法到英文（切换输入法模式）");
            RequestSwitchIme(ImeMode.English, "需要英文（命令栏）",
                             urgent ? SwitchDelayNone : SwitchDelayMs);
        }

        /// <summary>
        /// 命令栏进入「允许中文」状态（文字命令 / 文字编辑态）。
        ///
        /// 两种模式下的动作：
        ///   · 无视输入法模式 —— **不做任何切换**：用户输入法本就保持自己的状态
        ///     （通常是中文），此时停止拦截即可，命令栏不再被投递英文按键，
        ///     输入法自然接管，用户可直接打中文。
        ///   · 切换输入法模式 —— **真的切成中文**。
        /// </summary>
        private void SwitchToChinese()
        {
            SwitchToChinese(false);
        }

        /// <param name="urgent">
        /// 焦点**已经**落在编辑控件上（EnterTextEditMode）→ true：不排队，立刻执行。
        /// 命令刚触发的瞬间焦点还在命令栏，必须等它迁移完 → 用默认延迟。
        /// 见 <see cref="SwitchDelayMs"/> 的说明。
        /// </param>
        private void SwitchToChinese(bool urgent)
        {
            _commandLineNeedsEnglish = false;
            Dbg("SwitchToChinese: 仅清标志（停止拦截，交还给输入法）");

            // ★★ 铁律（v58 恢复）：无视输入法模式下**不做任何切换** ——
            //   用户输入法本就保持自己的状态（通常是中文），此时停止拦截即可，
            //   命令栏不再被投递英文按键，输入法自然接管，用户可直接打中文。
            //
            //   ★ v55 曾在这里对称地"也允许真的切中文"（因为 ForceEnglish 会切英文，
            //     不切回来的话后续文字命令会卡在英文）。现在 ForceEnglish 不切了，
            //     这里自然也不需要切 —— 整条"动输入法"的链路一起消失。
            if (_config.WorkMode != XimeWorkMode.SwitchIme) return;

            Dbg("SwitchToChinese: 切输入法到中文（切换输入法模式）");
            RequestSwitchIme(ImeMode.Chinese, "需要中文（文字输入）",
                             urgent ? SwitchDelayNone : SwitchDelayMs);
        }

        #endregion

        #region 切换输入法模式（SwitchIme）实现

        /// <summary>
        /// 解析做输入法操作时该用的**靶点窗口**。
        ///
        /// ★★ 只允许返回 AutoCAD 自己的窗口，理由（现场日志实测，务必保留这段记录）：
        ///
        ///   早期实现是「真实焦点控件优先」，结果引出两类事故：
        ///     ① 用户切换工作模式的唯一入口就是我们自己的设置面板。面板一打开，
        ///        "真实焦点控件"就变成了面板上的单选按钮，于是所有的输入法切换
        ///        动作全部打在面板上，AutoCAD 命令栏的输入法纹丝不动 ——
        ///        用户看到的就是「切换输入法模式根本不起作用、三种输入法都一样」。
        ///     ② 面板控件与 CAD 主窗口的 IME 状态可以**不同**：日志里同一瞬间
        ///        CAD 主窗口(0x161198) 读到 English、面板按钮(0xC114A) 读到 Chinese。
        ///        采样端用主窗口、应用端用焦点窗口，两端结论相反，
        ///        "进入前状态"和"实际切换"永远对不上，退出时还原的也是错的状态。
        ///
        ///   现在的优先级（**全部限定在 AutoCAD 进程内**）：
        ///     ① 文字编辑态 → 当前焦点**编辑控件**（用户正在打字，IME 上下文挂在它身上）；
        ///     ② 命令栏输入控件（AutoCAD 分发 IME 输入的地方）；
        ///     ③ AutoCAD 主窗口（最后兜底，**读数不可信**，见下）。
        ///   拿不到任何 CAD 窗口时返回 Zero —— **绝不返回别的程序或本插件自己的窗口**。
        ///
        /// ★★ v35 新增 `trustworthy`（读数是否可信）—— 这是本轮实机事故的直接根治点：
        ///
        ///   IME 上下文只挂在**真正承接受输入的子控件**上。若靶点落到绘图区
        ///   （`Afx:00007FF...`）、MDIClient、主窗口这类窗口上，`ImmGetContext` 会
        ///   给出一个**默认的**转换状态 —— 实测它**恒为「中文」**。于是：
        ///     · 需要中文时 → 「探测已是 Chinese，无需动作」→ **永远切不到中文**；
        ///     · 采样"进入前状态"恒定中文 → 退出还原的也是错的。
        ///
        ///   现场日志（v34，v33 同款事故）：
        ///     18:52:02.836  补做：Chinese → English 结果=True 方式=RealKey     ← 真切成英文了
        ///     18:52:03.781  按键 L → 18:52:03.899 CommandWillStart: LINE        ← 证明确实是英文
        ///     18:52:06.241  CommandWillStart: TTEXT（需要中文）
        ///     18:52:06.344  切换输入法（需要中文）：探测已是 Chinese，无需动作（窗口=0x3C24D2）
        ///                   ← 0x3C24D2 是绘图区窗口，这个 Chinese 是假的
        ///   结果：TTEXT 里**打不出中文**。
        ///
        ///   所以：只有 ①（编辑控件）与 ②（命令栏控件）的读数可信；
        ///   ③ 主窗口兜底时 `trustworthy = false`，其读数**只能**用于"读不出来"的用途，
        ///   绝不能拿来判定"已经是目标态"。
        /// </summary>
        private IntPtr ResolveAcadImeTarget()
        {
            bool ignored;
            return ResolveAcadImeTarget(out ignored);
        }

        /// <param name="trustworthy">
        /// 返回的窗口是否**真的承接着 IME 上下文**（即它的中英读数可采信）。
        /// false = 只剩主窗口兜底，读数恒为默认的「中文」，必须当作读不出来。
        /// </param>
        private IntPtr ResolveAcadImeTarget(out bool trustworthy)
        {
            trustworthy = false;

            // ① 文字编辑态：焦点在编辑控件上，IME 上下文就在它身上。
            //    ★ 必须限定"真的是**承载文本输入**的控件"（强特征）：只判"在 CAD 进程内"
            //      是不够的 —— 双击文字进入在位编辑时，焦点会在
            //        命令栏 ⇄ 在位编辑器 ⇄ **绘图区**(`Afx:00007FF...`) ⇄ MDI 子框架
            //      之间抖动，裸判会抓到绘图区窗口。而绘图区**没有 IME 上下文**，
            //      `ImmGetContext` 给出的是兼容层默认值（实测恒为「中文」）。
            if (_inTextEditMode)
            {
                IntPtr f = IntPtr.Zero;
                try { f = CharInjector.GetRealFocusWindow(); }
                catch (Exception) { }
                if (f != IntPtr.Zero && NativeMethods.IsWindow(f)
                    && IsAcadProcessWindow(f) && !IsOwnWindow(f)
                    && !HostTextFrame.IsCommandLineWindow(f)
                    && HostTextFrame.IsTextInputHost(f))
                {
                    trustworthy = true;
                    return f;
                }
                // 焦点不在编辑控件上（多半是绘图区）→ 落回命令栏
            }

            // ② 命令栏输入控件
            //   ★★ 同样必须要求**强特征**：`HostTextFrame.Resolve()` 的首选策略是
            //      "当前焦点控件（Edit-like）"，而"Edit-like"包含弱特征 `Afx:*` ——
            //      也就是说它**可能返回绘图区**（v35 日志里 15 次探测全是它）。
            //      用一个没有 IME 上下文的窗口当靶点，读数就是默认值（恒为中文），
            //      于是"需要中文"永远被判成"已经是中文，无需动作"。
            IntPtr cmd = IntPtr.Zero;
            try { cmd = HostTextFrame.Resolve(); }
            catch (Exception) { }
            if (cmd != IntPtr.Zero && NativeMethods.IsWindow(cmd) && !IsOwnWindow(cmd)
                && HostTextFrame.IsTextInputHost(cmd))
            {
                trustworthy = true;
                return cmd;
            }

            // ③ AutoCAD 主窗口 —— 兜底，但**它没有 IME 上下文，读数不可信**
            var main = _focus.AcadMainWindow;
            if (main != IntPtr.Zero && NativeMethods.IsWindow(main))
                return main;

            return IntPtr.Zero;
        }

        /// <summary>
        /// 主机窗口概览（v43）—— 加载时自动记录一次，由宿主写进
        /// <c>%TEMP%\xime_startup.log</c>。
        ///
        /// ★ 为什么必须在**加载时**就记，而不是等用户敲 <c>XIMEFOCUS</c>：
        ///   本插件的靶点识别完全建立在**窗口类名**上 ——
        ///   <see cref="HostTextFrame.MatchStrength"/> 认 `RichEdit` / `Edit` / `HwndWrapper`
        ///   为强特征，<see cref="HostTextFrame.IsCommandLineWindow"/> 认父链里的 `CLI Palette`。
        ///   而这些类名**随 AutoCAD 版本变**（2014 主框架 `AfxMDIFrame100u`、
        ///   2022 `AfxMDIFrame140u`），天正还会再套一层自己的框架。
        ///   换宿主后的第一次排查，没有这份"词汇表"就只能靠用户手敲命令，而用户通常不会敲。
        ///
        /// 只读、不改状态。唯一影响是给 <see cref="HostTextFrame.Resolve"/> 的缓存
        /// 填一次"当前命令栏控件"，焦点一变即被 <see cref="HostTextFrame.Invalidate"/> 清掉。
        /// </summary>
        internal string[] BuildHostSurveyLines()
        {
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                lines.Add("  主机窗口概览（v43，加载时自动记录，只读）:");

                var main = _focus.AcadMainWindow;
                lines.Add("    CAD 主窗口 : " + DescribeWindow(main));

                IntPtr cmd = IntPtr.Zero;
                try { cmd = HostTextFrame.Resolve(); } catch (Exception) { }
                lines.Add("    命令栏候选 : " + DescribeWindow(cmd));

                IntPtr foc = IntPtr.Zero;
                try { foc = CharInjector.GetRealFocusWindow(); } catch (Exception) { }
                lines.Add("    当前焦点   : " + DescribeWindow(foc));

                bool trusted;
                var target = ResolveAcadImeTarget(out trusted);
                lines.Add("    靶点(决策) : " + DescribeWindow(target)
                          + " 读数可信=" + trusted
                          + "（false = 只剩主窗口兜底，读数一律当「读不出来」）");
            }
            catch (Exception ex)
            {
                lines.Add("    (主机窗口概览异常: " + ex.Message + ")");
            }
            return lines.ToArray();
        }

        /// <summary>把一个窗口描述成一行诊断文本（v43，只读）。</summary>
        private static string DescribeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "(空)";
            string hex = "0x" + hwnd.ToInt64().ToString("X");
            try
            {
                string cls = HostTextFrame.GetClassNameSafe(hwnd);
                return hex
                     + " 类名=" + (string.IsNullOrEmpty(cls) ? "(空)" : cls)
                     + " 文本输入宿主=" + HostTextFrame.IsTextInputHost(hwnd)
                     + " 命令栏=" + HostTextFrame.IsCommandLineWindow(hwnd)
                     + " 强特征=" + HostTextFrame.MatchStrength(hwnd);
            }
            catch (Exception ex)
            {
                return hex + " (读取失败: " + ex.Message + ")";
            }
        }

        /// <summary>给定窗口是否属于 AutoCAD 进程（含本插件，故需另行排除自有窗口）。</summary>
        private bool IsAcadProcessWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                var acad = _focus.AcadMainWindow;
                if (acad != IntPtr.Zero && hwnd == acad) return true;

                uint pid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                return pid != 0 && pid == MyProcessId;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 是否允许发送**真实按键**（即模拟 Shift）。
        ///
        /// ★ 真实 Shift 是**全局**按键事件，会落到"当前前台窗口所属的输入法"上，
        ///   而不是我们指定的那个窗口。因此它有两个硬前提：
        ///     ① 前台必须属于 AutoCAD —— 否则改的是**别的程序**的输入法状态
        ///        （用户在这 100ms 延迟里 Alt-Tab 走了就会发生，这是必须避免的越界）；
        ///     ② 前台不能是本插件自己的窗口 —— 那时 Shift 只会落在面板上，
        ///        对 AutoCAD 命令栏的输入法毫无作用（现场日志里这类动作全部失败）。
        ///   两个条件任一不满足就**不发全局键**，退化为
        ///   <see cref="ImeController.SetModeDirectOnWindow"/>：直接在靶点窗口上设置
        ///   转换状态，不需要模拟按键，天然不越界。
        /// </summary>
        private bool CanSendRealKey()
        {
            try
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                if (IsOwnWindow(fg)) return false;

                // 模态对话框（#32770，例如 QUIT 的"是否保存修改"提示）里没有命令栏
                // 输入场景，在它上面按 Shift 只会改到对话框自己的输入法，与命令栏无关。
                if (FocusMonitor.GetWindowClass(fg) == "#32770") return false;

                if (fg == _focus.AcadMainWindow) return true;
                if (HostTextFrame.IsCommandLineWindow(fg)) return true;
                return IsAcadProcessWindow(fg);
            }
            catch (Exception) { return false; }
        }

        #region 本插件自己的窗口登记

        /// <summary>
        /// 本插件自己创建的顶层窗口（设置面板等）。
        ///
        /// ★ 为什么必须登记：这些窗口也属于 AutoCAD 进程，
        ///   若被当作输入法操作靶点，切换动作就会全部落在面板上、对命令栏无效；
        ///   若被当作"可以发真实按键"的前台，Shift 又会白白丢在面板里。
        ///   登记比"按类名猜"可靠（WindowsForms10.* 既是我们的面板，
        ///   也是 AutoCAD/天正 自己的命令栏控件，靠类名分不开）。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<IntPtr> OwnWindows =
            new System.Collections.Generic.HashSet<IntPtr>();
        private static readonly object OwnWindowsSync = new object();

        /// <summary>登记一个本插件自己的顶层窗口。</summary>
        public static void RegisterOwnWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            lock (OwnWindowsSync) { OwnWindows.Add(hwnd); }
        }

        /// <summary>注销一个本插件自己的顶层窗口。</summary>
        public static void UnregisterOwnWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            lock (OwnWindowsSync) { OwnWindows.Remove(hwnd); }
        }

        /// <summary>窗口（或它的任一祖先）是否属于本插件自己的窗口。</summary>
        public static bool IsOwnWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            lock (OwnWindowsSync)
            {
                if (OwnWindows.Count == 0) return false;

                var cur = hwnd;
                for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
                {
                    if (OwnWindows.Contains(cur)) return true;
                    cur = NativeMethods.GetParent(cur);
                }
            }
            return false;
        }

        #endregion

        /// <summary>
        /// 记录「进入 CAD 接管之前的输入法状态」，供退出 / 最小化时还原。
        ///
        /// 只记录一次：本次接管期间不会重新采样 —— 否则接管的第一个动作
        /// （切英文）会把采样值污染成英文，"还原"就变成"什么都不做"了。
        /// 每次 Disengage 还原成功后会清掉标记，下次重新进入时再采样。
        /// </summary>
        private void CaptureOriginalIme()
        {
            // ★ 靶点必须与 ApplySwitchIme / RestoreOriginalIme 完全一致 ——
            //   三处都走 ResolveAcadImeTarget()（只取 AutoCAD 自己的窗口）。
            //
            //   早期这里走 ResolveImeWindow()（优先真实焦点控件），应用端也走它，
            //   但还原端走 AcadMainWindow，三处靶点各不相同：
            //     · AutoCAD 的 IME 上下文挂在具体子控件上，不同窗口的读数可以**相反**
            //       （现场日志：主窗口 English / 设置面板 Chinese）；
            //     · 于是"采样自 A、应用到 B、还原到 C"，切换永远对不上。
            //   "采样 / 应用 / 还原"三者必须采同一个窗口，否则一切无从谈起。
            bool trustworthy;
            var hwnd = ResolveAcadImeTarget(out trustworthy);

            // ★ 用 GetModeOnWindow（单窗口直读）而不是 GetMode（会回退到焦点控件）：
            //   应用端与还原端内部用的都是单窗口直读，采样端必须与之匹配。
            //
            _switchOriginalMode = hwnd != IntPtr.Zero ? _ime.GetModeOnWindow(hwnd) : ImeMode.Unknown;
            _switchOriginalCaptured = true;
            ClearAppliedState();
            _switchWanted = -1;

            // ★ 靶点不可信（只有主窗口兜底）时，采到的值多半是默认的「中文」，
            //   不能据此宣称"可探测" —— 那会让界面显示"可用"、实际却切不动。
            _switchDetectable = trustworthy && _switchOriginalMode != ImeMode.Unknown;

            // ★ 记录"这一份原始状态是否来自可信窗口" —— 还原时要用它把关：
            //   不可信的采样（默认值）绝不能拿去还原，否则会把用户的输入法锁死
            //   在一个我们**从没真正读到过**的状态上（"猜错了比不动作更糟"）。
            _switchOriginalTrusted = trustworthy;

            // ★★★ v39：采不到可信值就标 pending，等一次可信读数补记。
            //   进入接管的那一刻前台往往是设置面板（用户刚点下模式按钮），
            //   靶点只能兜底到 CAD 主窗口，读到的「中文」是兼容层默认值。
            //   若就此认定"用户原本是中文"，退出时的还原既没有依据（trusted=false
            //   会直接跳过），就算强行还原也可能把用户锁死在错误状态上。
            //   正确做法：先不采，等我们**真的要动手切**那一刻再采 —— 那时靶点可信，
            //   而且我们还没改过输入法，读数就是用户真正的原始状态。
            _switchOriginalPending = !trustworthy || _switchOriginalMode == ImeMode.Unknown;
            Dbg("切换输入法模式：记录进入前状态 = " + _switchOriginalMode
                + (trustworthy ? "" : "（★ 靶点不是 IME 承接窗口，该读数不可信）")
                + "（窗口=0x" + (hwnd != IntPtr.Zero ? hwnd.ToInt64().ToString("X") : "0") + "）");
        }

        /// <summary>
        /// 请求把输入法切到目标状态（**异步**，立即返回）。
        ///
        /// 为什么异步 + 延迟 100ms：
        ///   调用点几乎都在「命令开始 / 焦点刚迁移」的瞬间，此刻键盘焦点正从
        ///   命令栏迁往文字编辑框，抢在迁移中途操作容易落到过渡窗口上
        ///   （实测表现为"切了却没生效"）。延迟一小段等焦点稳定再动手。
        ///   与「无视输入法模式」的 EnsureChineseForTextInput 同一套理由。
        ///
        /// 为什么先复核 <see cref="_switchWanted"/>：
        ///   排队期间情况可能已经变了（例如命令一闪而过、已经结束了）。
        ///   执行前确认"我还是最新的那个请求"，过期请求直接放弃 ——
        ///   否则会出现"命令已经结束、早先的'切中文'才落地"。
        /// </summary>
        /// <summary>
        /// 切换请求的**默认**排队延迟（毫秒）。
        ///
        /// ★ 为什么要延迟：调用点常常落在"命令刚触发、键盘焦点正从命令栏迁往编辑框"
        ///   的瞬间，抢在迁移中途操作容易落到过渡窗口上（实测表现为"切了却没生效"）。
        ///
        /// ★★ 但"焦点**已经**落在目标窗口上"的场景（进/出文字编辑态、命令结束）
        ///   **完全不需要**这段等待 —— 净白等 100ms，而用户打字比这更快：
        ///   现场日志（v39 实机）里从"进入文字编辑态"到切换落地要 **~170ms**
        ///   （100ms 排队 + ~30ms 探测 + 40ms Shift 间隔），用户在这段时间里敲的字
        ///   会落进**旧的**输入法状态（中文输入法吃掉命令名 / 英文输入法吃掉拼音），
        ///   表现就是"不跟手"。→ 这些路径一律用 <see cref="SwitchDelayNone"/>。
        /// </summary>
        internal const int SwitchDelayMs = 100;

        /// <summary>
        /// 不需要等待：焦点已经稳定在目标窗口上（见 <see cref="SwitchDelayMs"/> 的说明）。
        /// ★ 必须是 **0** —— 自测会钉住这条（"紧急"路径真的不等待）。
        /// </summary>
        internal const int SwitchDelayNone = 0;

        private void RequestSwitchIme(ImeMode target, string reason)
        {
            RequestSwitchIme(target, reason, SwitchDelayMs);
        }

        /// <summary>
        /// 本引擎此刻是否允许**真的去切换输入法状态**（纯判据，自测钉死）。
        ///
        /// ★★ 铁律：「无视输入法模式」**绝不碰用户输入法状态**。
        ///   该模式的承诺原话就是"用户输入法始终保持其原始状态，绝不被打乱"——
        ///   英文输入靠**钩子把按键直接投递给命令栏**实现（绕过 IME），不是去改输入法。
        ///   所以这里**只有** `mode == SwitchIme` 一条通路。
        ///
        /// ★★ v55 曾经给无视模式开过一个"命令期间强制切英文"的例外
        ///   （`ForceEnglishDuringCommand`）。**该例外已在 v58 整体撤除**：
        ///   用户明确要求"无视输入法模式不要动我的输入法状态"。
        ///   RYBZ 二级命令改由**命令期间放行物理键**（`CommandKeyMode=PassThrough`，
        ///   即 v25 当时的做法）解决 —— 那才是"不动输入法"就能修好的方向。
        /// </summary>
        internal static bool ShouldAutoSwitchIme(XimeWorkMode mode)
        {
            return mode == XimeWorkMode.SwitchIme;
        }

        /// <summary>
        /// **执行层**（<see cref="ApplySwitchIme"/> 开头）的放行判据。
        ///
        /// ★ 为什么要单独抽出来、而不是直接调 ShouldAutoSwitchIme：
        ///   v55 首次实施时只有**请求层**改了判据，执行层仍留着旧的一句
        ///   `if (WorkMode != SwitchIme) return;` —— 请求全部发得出去、
        ///   又在 ApplySwitchIme 里被整齐地 return 掉，实机表现就是"改了跟没改一样"。
        ///   这个 bug 的形态很隐蔽：**队列里能看见请求、日志里能看见排队**，
        ///   唯独看不到"动作没发生"。所以请求层与执行层必须共用**同一个**判据。
        /// </summary>
        internal static bool ShouldApplySwitchNow(XimeWorkMode mode)
        {
            return ShouldAutoSwitchIme(mode);
        }

        /// <param name="delayMs">
        /// 排队延迟。焦点已经稳定在目标窗口上时传 <see cref="SwitchDelayNone"/>。
        /// </param>
        private void RequestSwitchIme(ImeMode target, string reason, int delayMs)
        {
            if (!ShouldAutoSwitchIme(_config.WorkMode)) return;
            if (!_config.Enabled || !_isEngaged) return;
            if (target == ImeMode.Unknown) return;

            _switchWanted = (int)target;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (delayMs > 0) System.Threading.Thread.Sleep(delayMs);

                    // 已被更新的请求取代 → 本次作废（由更新的那次负责）
                    if (_switchWanted != (int)target) return;

                    ApplySwitchIme(target, reason);
                }
                catch (Exception ex)
                {
                    Dbg("切换输入法（" + reason + "）异常：" + ex.Message);
                }
            });
        }

        /// <summary>
        /// 登记一次"没切成、待补做"的输入法切换 —— 详见 <see cref="_pendingSwitch"/>。
        /// </summary>
        /// <param name="target">要补做的目标态。</param>
        /// <param name="lackRealKey">
        /// 是否因为**当前前台不是 AutoCAD**（发不出真实 Shift）才失败的。
        /// 仅用于日志措辞：两种情况的重试次数上限一致，因为计数器只在
        /// "真的去尝试了一次"时才自增，等待期间不会消耗配额。
        /// </param>
        private void MarkPendingSwitch(ImeMode target, bool lackRealKey)
        {
            if (target == ImeMode.Unknown) return;
            // ★ 与 RequestSwitchIme 同一口径（否则"切不成"就无法登记待补）。
            if (!ShouldAutoSwitchIme(_config.WorkMode)) return;

            // 换了目标 → 重新计数（例如"补英文"补不动，用户又去开文字命令要中文）
            if (_pendingTriesTarget != target)
            {
                _pendingTriesTarget = target;
                _pendingTries = 0;
            }

            if (_pendingTries >= PendingSwitchMaxTries)
            {
                // ★ v45：重试耗尽 = 这个输入法大概真的不支持中英状态读写，
                //   属于"切换永久失效"，必须进始终记录的日志。
                Note("切换输入法：待补切换已重试 " + _pendingTries + " 次仍未达成 → 放弃。"
                    + "请执行 XIMEPROBE 确认该输入法是否支持中英状态读写");
                _pendingSwitch = -1;
                return;
            }

            _pendingSwitch = (int)target;
            Dbg("切换输入法：本次未能通过「真实 Shift」达成 → 登记待补（目标=" + target
                + (lackRealKey ? "，原因是当前前台不是 AutoCAD、发不出真实按键" : "")
                + "）→ AutoCAD 回到前台后立即补做");
        }

        /// <summary>
        /// 清掉「待补」登记与它的重试计数。
        ///
        /// ★ 必须**连计数一起清**：计数是"针对某个目标失败了几次"的连续量。
        ///   若切换已经达成（或状态已作废）却只清目标、留着计数，
        ///   下一次换个场景再失败时就会从旧的计数继续往上加，
        ///   表现为"明明只失败了一次却提示已重试 4 次、直接放弃"。
        /// </summary>
        private void ClearPendingSwitch()
        {
            _pendingSwitch = -1;
            _pendingTries = 0;
            _pendingTriesTarget = ImeMode.Unknown;
        }

        /// <summary>
        /// 「待补输入法切换」在当前这一刻该怎么处理 —— 纯判据，无副作用。
        ///
        /// 抽出来的唯一目的：让"**前台不是 AutoCAD 时一个按键都不发**"这条护栏
        /// 可以被自测工具用可执行断言钉住。它关系到的是**越界风险**
        /// （真实 Shift 是全局键，会改到别的程序的输入法），
        /// 靠人读注释记住是靠不住的。
        /// </summary>
        internal enum PendingFlushAction
        {
            /// <summary>没有待补的切换。</summary>
            None = 0,

            /// <summary>有待补，但条件还不允许发键 → 继续等（不能作废，否则永远补不上）。</summary>
            Wait = 1,

            /// <summary>条件齐备 → 立刻补做一次真实 Shift。</summary>
            Flush = 2,

            /// <summary>模式 / 启用 / 接管状态已变 → 作废这次待补（重新 Engage 时会再登记）。</summary>
            Discard = 3
        }

        internal static PendingFlushAction DecidePendingFlush(
            int pending, bool autoSwitchAllowed, bool enabled, bool engaged, bool canSendRealKey)
        {
            if (pending < 0) return PendingFlushAction.None;
            // ★ v55：参数由「是不是切换输入法模式」改成「**是否允许自动切换**」
            //   （= ShouldAutoSwitchIme 的结果）。否则无视模式下登记了待补，
            //   补做时又被这条判据 Discard 掉 —— 与 ApplySwitchIme 那道硬闸门
            //   是同一类"请求层放行、执行层丢弃"的两张皮。
            if (!autoSwitchAllowed || !enabled || !engaged) return PendingFlushAction.Discard;
            // ★ 前台不是 AutoCAD（例如用户还停在设置面板上）→ 继续等，一个按键都不发。
            if (!canSendRealKey) return PendingFlushAction.Wait;
            return PendingFlushAction.Flush;
        }

        /// <summary>
        /// 一次**已经在执行中**的切换请求，是否已被后来的请求取代（取代了就必须放弃动作）。
        ///
        /// 抽成纯函数的唯一目的：让"过期请求绝不许动手"这条契约可以被自测工具
        /// 用可执行断言钉住 —— 它关系到的是**输入法被翻反**这种最难查的故障
        /// （现场表现只是"概率不跟手"，日志里却隔着一两百毫秒才看得出来）。
        /// </summary>
        /// <param name="wanted">当前最新的请求目标（<see cref="_switchWanted"/>，-1 表示没有请求）。</param>
        /// <param name="runningTarget">正在执行的那次请求的目标。</param>
        internal static bool IsSupersededRequest(int wanted, ImeMode runningTarget)
        {
            return wanted != (int)runningTarget;
        }

        /// <summary>
        /// 「确保中文」该不该发那一次真实 Shift —— 纯判据，无副作用。
        ///
        /// 抽成纯函数的唯一目的：让"**没有 IME 上下文的窗口读回的中文不算中文**"
        /// 这条契约可以被自测工具用可执行断言钉住。它是 v38 实机事故的根治点 ——
        /// AutoCAD 2014 双击文字进在位编辑时，裸焦点经常是绘图区
        /// （`Afx:...:28:...`），`ImmGetContext` 对它会给出兼容层默认值（恒为「中文」），
        /// 旧实现据此判"已是中文，无需动作"，用户双击文字后就怎么都打不出中文。
        /// </summary>
        /// <param name="actual">探测到的中英状态（<see cref="ImeMode.Unknown"/> = 读不出来）。</param>
        /// <param name="readingTrusted">这个读数是否来自**真正承接 IME 上下文**的窗口。</param>
        /// <param name="mayBeEnglish">
        /// 有任何**正面证据**表明"输入法此刻可能是英文"—— 这是唯一允许"盲切"的依据。
        /// 两个来源（v42 起）：
        ///   · 用户近期亲手切过中英（<see cref="_userImeToggleTick"/>，方向未知）；
        ///   · **我们自己把输入法留在了英文**（<see cref="_imeLeftEnglish"/>）。
        /// ★ 为什么必须扩到第二条：实机日志证明"刚从切换模式切过来"时，靶点窗口
        ///   会给出一个与事实相反的「Chinese」读数，而那时唯一可信的信息恰恰是
        ///   "我们刚把它按在英文上"。
        /// </param>
        internal static bool ShouldEnsureChineseTapShift(
            ImeMode actual, bool readingTrusted, bool mayBeEnglish)
        {
            // ① 可信地读到"已经是中文" → 不动（这是唯一可以不放手的正解）
            if (readingTrusted && actual == ImeMode.Chinese) return false;

            // ② 可信地读到英文 → 必须切
            if (readingTrusted && actual == ImeMode.English) return true;

            // ③ 读不出来 → 只有"有正面证据表明可能是英文"才允许盲切（50% 概率切反，不能赌）
            return mayBeEnglish;
        }

        /// <summary>盲切（真实 Shift）之后回读校验的判定结果（v47，纯判据的一部分）。</summary>
        internal enum BlindShiftVerdict
        {
            /// <summary>读不出来 / 读数可能只是兼容层默认值 → **不据此下任何结论**，保持原判。</summary>
            Inconclusive = 0,

            /// <summary>有可信依据证明已达目标态。</summary>
            Confirmed = 1,

            /// <summary>可信地证明**没达成**（该补一次）。</summary>
            Failed = 2
        }

        /// <summary>
        /// 「盲切之后回读，能不能判断它到底生效了没有」—— 纯判据，无副作用（v47，自测钉住）。
        ///
        /// ★★★ 为什么需要它：v44~v46 的所有真实切换都是**盲切** ——
        ///   <see cref="CharInjector.TapShift"/> 的返回值只是
        ///   `SendInput(...) == 1`，即"事件进了系统输入队列"，
        ///   **完全不代表输入法响应了**（候选窗开着、目标控件没开 IME 上下文、
        ///   down/up 之间的 40ms 里焦点被抢走，都会让那一下落空）。
        ///
        ///   而"我们切过了"这条记录（<see cref="_switchApplied"/>）一旦为假，
        ///   parity 就整体反了 —— 又因为铁律 4「自己的动作结果优先于探测」，
        ///   这个错误会**一直保留到会话结束**。实机 v46 日志（AutoCAD 2022 + 天正 T20）
        ///   里 58 次切换**全部**记着 `结果=True 方式=RealKey`，
        ///   而用户仍在报告"有一定的概率切换逻辑不对" —— 这份日志既证明不了成功，
        ///   也看不出失败，正是"没有校验"的直接后果。
        ///
        /// ★ 判据的基石是一条**天然不对称**的事实（本文件反复实测过）：
        ///   兼容层对"没有 IME 上下文的窗口"返回的**默认值恒为「中文」**。
        ///   于是：
        ///     · **「English」读数不可能是默认值** —— 读到英文必为真；
        ///     · 「Chinese」读数则可能是默认值 / 滞后值，**不足为凭**。
        ///
        ///   据此穷举（target × 回读）：
        ///     target=English & 读到 English → **Confirmed**（想要英文且确认在英文）
        ///     target=English & 读到 Chinese → Inconclusive（可能是真中文，也可能是默认值）
        ///     target=Chinese & 读到 English → **Failed**  （想要中文却可信地读到英文 ⇒ 没生效）
        ///     target=Chinese & 读到 Chinese → Inconclusive（可能是默认值，不能据此放心）
        ///     读不出来 / 靶点不可信       → Inconclusive（一个结论都不下）
        /// </summary>
        internal static BlindShiftVerdict DecideBlindShiftVerdict(
            ImeMode target, ImeMode afterRead, bool probeCapable)
        {
            if (!probeCapable) return BlindShiftVerdict.Inconclusive;
            if (afterRead == ImeMode.Unknown) return BlindShiftVerdict.Inconclusive;

            if (target == ImeMode.English)
            {
                return afterRead == ImeMode.English
                    ? BlindShiftVerdict.Confirmed       // 英文读数可信
                    : BlindShiftVerdict.Inconclusive;
            }

            // target == Chinese
            return afterRead == ImeMode.English
                ? BlindShiftVerdict.Failed              // ★ 这就是"盲切落空"的唯一可见证据
                : BlindShiftVerdict.Inconclusive;
        }

        /// <summary>
        /// 「回读通道到底能不能用」—— 纯判据，无副作用（v48，自测钉住）。
        ///
        /// ★★★ 为什么需要它（AutoCAD 2022 + 天正 T20 + 小狼毫实机，v47 日志）：
        ///   <see cref="DecideBlindShiftVerdict"/> 的基石是"English 读数天然可信、
        ///   Chinese 读数可能是兼容层默认值"。这条不对称在小狼毫上有一个致命后果：
        ///   **小狼毫走 TSF，IMM32 两条通道读到的恒为兼容层默认值「中文」**，
        ///   于是它**永远给不出 English 读数** ⇒ `Failed` 永远不触发 ⇒
        ///   回读校验**形同虚设**。实机日志里 111 次回读**全部** Inconclusive
        ///   （读到 English 0 次），却一条警告都没有 —— 用户只能看到
        ///   "有一定概率切换逻辑不对"，而日志看起来一切正常。
        ///
        ///   这正是"会撒谎的工具比没有工具更糟"：**回读恒为同一值**（且与目标无关）
        ///   本身就是"这条通道读不出真实态"的**正面证据**，必须报出来，
        ///   而不是堆一屏 Inconclusive 让人以为"只是没确认"。
        ///
        /// 判据（同时满足才算通道失效）：
        ///   ① 样本足够（<paramref name="totalSamples"/> ≥ <paramref name="minSamples"/>）；
        ///   ② **Inconclusive 占满全部样本**（一次 Confirmed / Failed 都没有）；
        ///   ③ 回读值**恒为同一个**、且**与目标无关**
        ///      —— 只要出现过一次"目标与读数相符或相反地变化"，就不算恒定。
        ///      具体：记录到的回读值为单一值 <paramref name="distinctReads"/> == 1。
        ///
        /// ★ 刻意**只做诊断**：本判据的结论**不参与任何切换决策**，
        ///   只用来决定"要不要在日志里明确报一次通道失效"。
        /// </summary>
        internal static bool IsReadbackChannelUseless(
            int totalSamples, int inconclusiveCount, int distinctReads, int minSamples)
        {
            if (totalSamples < minSamples) return false;
            if (inconclusiveCount < totalSamples) return false;
            return distinctReads <= 1;
        }

        /// <summary>
        /// ★★★ v49：<paramref name="applied"/> 这条记录能不能用来给
        /// <paramref name="targetHwnd"/> 上的"本模式已置于 X → 无需动作"做依据？
        /// —— 纯判据，无副作用（自测钉住）。
        ///
        /// ★ 为什么需要它：<see cref="_switchApplied"/> 是**全局单一变量**，
        ///   而它描述的状态**只对写下它的那个窗口成立**（见 `_switchAppliedHwnd`）。
        ///   切换靶点在"命令栏控件"与"文字编辑框"之间来回走，是两个不同的
        ///   IME 上下文；拿 A 窗口的记录去给 B 窗口做"无需动作"的依据，
        ///   就会在该切的时候不切 —— 这正是"有一定概率切换逻辑不对"。
        ///
        /// 判据（全部满足才算"可以据此跳过"）：
        ///   ① 记录本身不是 Unknown（没记录就谈不上跳过）；
        ///   ② 记录**等于**目标（不等就本来要动手）；
        ///   ③ 记录与本次靶点是**同一个窗口**；或记录没绑窗口（`IntPtr.Zero`，
        ///      兼容旧路径 / 靶点不可信时的兜底）。
        ///   ★ 靶点为空（拿不到窗口）时**不**跳过 —— 那时也动不了手，
        ///     由上游的"拿不到靶点"分支处理。
        /// </summary>
        internal static bool IsAppliedStateUsableFor(
            ImeMode applied, IntPtr appliedHwnd, ImeMode target, IntPtr targetHwnd)
        {
            if (applied != target) return false;
            if (targetHwnd == IntPtr.Zero) return false;
            if (appliedHwnd == IntPtr.Zero) return true;   // 旧记录没绑窗口 → 沿用（不改变既有行为）
            return appliedHwnd == targetHwnd;
        }

        /// <summary>
        /// 若存在**待补**的输入法切换、且此刻前台已经回到 AutoCAD → 立即补做一次。
        /// 由焦点事件与 500ms 轮询共同驱动（两条路任一生效即可，不怕漏事件）。
        /// </summary>
        private void TryFlushPendingSwitch(string why)
        {
            int pending = _pendingSwitch;
            if (pending < 0) return;

            var action = DecidePendingFlush(
                pending,
                // ★ 与请求层同一口径：只有切换模式才允许补做输入法切换。
                ShouldAutoSwitchIme(_config.WorkMode),
                _config.Enabled,
                _isEngaged,
                CanSendRealKey());

            switch (action)
            {
                case PendingFlushAction.None:
                    return;

                case PendingFlushAction.Discard:
                    ClearPendingSwitch();
                    return;

                case PendingFlushAction.Wait:
                    return;   // 一个按键都不发，等 CAD 回到前台
            }

            _pendingSwitch = -1;
            _pendingTries++;

            Dbg("补做待完成的输入法切换（" + why + "）：目标=" + (ImeMode)pending
                + "（第 " + _pendingTries + " 次尝试）");
            RequestSwitchIme((ImeMode)pending, "补做" + why, SwitchDelayMs);
        }

        /// <summary>
        /// 真正执行切换（线程池线程）。
        ///
        /// ★★ 判定「当前真实态」的唯一可信来源是 <see cref="_switchApplied"/>
        ///   （我们自己动作的**结果**），**不是**探测读数 —— 理由见方法体 ③ 的注释
        ///   （探测通道既会把"读不出来"报成默认的「中文」，又会滞后于真实状态）。
        ///
        /// 决策顺序（与 ImeController.SwitchTo 的约定一致，绝不越界）：
        ///   ① 本模式自己的记录就是目标 → 一个字节都不动，直接算成功。
        ///      （这条让本方法天然幂等：同一次操作从"命令开始"和"焦点进编辑框"
        ///        两个入口各来一次，第二次会自己发现无事可做。）
        ///   ② 记录缺失时才听探测：探测到明确状态 → 以此为准。
        ///   ③ 当前态 ≠ 目标 → 一律**强制发真实 Shift**（不允许 SwitchTo 再自行探测，
        ///      否则会被同一条滞后通道误导成"已经是目标"）。
        ///   ④ 既没有记录、探测也读不出来 → 拒绝动作，登记待补。
        ///      盲按有 50% 概率把本来就是目标的状态切反，代价是用户打不出字，
        ///      绝不能因为"想让它动一下"就赌。
        /// </summary>
        /// <summary>
        /// 统一的"切一次输入法"入口 —— **优先走小狼毫自己的命令行接口**。
        ///
        /// ★★★ v56（2026-10-06 实机定案）：在此之前，切换只有"盲按一次真实 Shift"
        ///   这一条路 —— 而 Shift 是**翻转键**，其正确性完全依赖"我们知道当前是什么态"。
        ///   小狼毫走 TSF、IMM32 读不回真实态（铁律 13），这个前提根本不成立：
        ///   实机日志里 `TapShift 结果=True 方式=RealKey` 之后回读仍是 Chinese，
        ///   用户按 A 依然被输入法吃掉。更糟的是那次**失败的**盲按还写下了
        ///   `_switchApplied = English` 的假记录，此后每次请求都被"无需动作"短路。
        ///
        ///   ⇒ 小狼毫 0.16.0+ 提供了正路：`WeaselServer.exe /ascii`（英文）与
        ///     `/nascii`（中文）—— **幂等的设值**，不是翻转，也不依赖焦点窗口。
        ///     走通它，上面两个问题（切不动 + 假记录）一并消失。
        ///
        /// 桥不可用（没装小狼毫 / 版本过旧 / 启动失败 / 超时）时**原样退回**真实 Shift，
        /// 非小狼毫用户的行为一个字节都不变。
        /// </summary>
        /// <summary>
        /// 这次切换要不要走"小狼毫命令行桥"（纯判据，便于自测钉住）。
        ///
        /// ★ 两条都必须成立：
        ///   · <paramref name="useWeaselCli"/> —— 用户在配置里没关掉它；
        ///   · <paramref name="canKey"/> —— **此刻前台是我们的宿主**。
        ///     小狼毫的 /ascii 最终落到服务端的 `m_active_session`
        ///     （`SetOption(session_id=0, …)` → `ipc_id == 0` 分支），
        ///     即"切哪个会话取决于谁在前台"。前台不是 AutoCAD 时动手，
        ///     只会去切别的程序、AutoCAD 纹丝不动，却让我们以为切好了。
        /// </summary>
        internal static bool ShouldUseWeaselCli(bool useWeaselCli, bool canKey)
        {
            return useWeaselCli && canKey;
        }

        /// <summary>
        /// ★★★ v57：这次切换能不能走「幂等直设」（纯判据，便于自测钉住）。
        ///
        /// 四条同时成立：
        ///   · <paramref name="useWeaselCli"/> —— 配置里没关掉命令行桥；
        ///   · <paramref name="bridgeAvailable"/> —— 找到了 <c>WeaselServer.exe</c>；
        ///   · <paramref name="serverRunning"/> —— 常驻服务在跑（不在跑时 <c>/ascii</c>
        ///     会静默失败却仍返回 0，见 <see cref="WeaselBridge.ShouldAttemptCli"/>）；
        ///   · <paramref name="canKey"/> —— 此刻前台是我们的宿主：<c>/ascii</c> 最终落到
        ///     服务端的 <c>m_active_session</c>，"切谁取决于谁在前台"，
        ///     前台不是 AutoCAD 时动手只会去切别的程序。
        ///
        /// ★ 为什么要为它单独写一条判据（v56 实机事故的直接产物）：
        ///   幂等直设是**唯一**不依赖"知道当前态"的切换手段 ——
        ///   "已经是英文了再设一次英文"与实际动作完全等价，也绝不会切反。
        ///   所以它一旦成立，<c>_switchApplied</c> 那条（小狼毫上**根本无法验证**的）
        ///   记忆就**不该**再参与决策；否则一条假记忆就能让功能永久静默失效
        ///   （现场：冷启动后 RYBZ 二级命令被中文输入法吃掉，日志里却只有
        ///    「本模式已置于 English → 无需动作」，一次 CLI 都没走）。
        /// </summary>
        internal static bool ShouldPreferIdempotentSet(bool useWeaselCli, bool bridgeAvailable,
                                                       bool serverRunning, bool canKey)
        {
            return useWeaselCli && bridgeAvailable && serverRunning && canKey;
        }

        private bool SwitchImeOnce(IntPtr hwnd, ImeMode target, bool canKey,
                                   out ImeController.ImeSwitchManner manner)
        {
            if (ShouldUseWeaselCli(_config.UseWeaselCli, canKey))
            {
                string detail;
                if (WeaselBridge.TrySetAscii(target, out detail))
                {
                    manner = ImeController.ImeSwitchManner.WeaselCli;
                    Dbg("切换输入法（小狼毫命令行直接设值）：" + target + " ← " + detail);
                    return true;
                }
                if (_debugLog && !string.IsNullOrEmpty(detail))
                    Dbg("小狼毫命令行桥未生效（" + detail + "）→ 退回真实 Shift");
            }
            else if (_config.UseWeaselCli && !canKey && _debugLog)
            {
                Dbg("小狼毫命令行桥跳过：此刻前台不是 AutoCAD（/ascii 会切到别的程序上）"
                    + " → 走原有路径（登记待补，等 CAD 回到前台再补）");
            }

            return _ime.SwitchTo(hwnd, target, null, true, () => canKey, true, out manner);
        }

        private void ApplySwitchIme(ImeMode target, string reason)
        {
            if (!_isEngaged) return;

            // ★ v47：锁外的"回读校验"开关（见本方法末尾）。放在最外层是为了让
            //   下面任何一个 return 分支都不会漏清它。
            bool verifyAfterUnlock = false;
            //   校验必须打在**同一扇窗**上（读写同靶点铁律 4）—— 所以把靶点留到锁外。
            IntPtr verifyHwnd = IntPtr.Zero;

            // ★ 关键：执行前复核「现在**还允不允许**做这次切换」。
            //   切换动作在线程池里延迟 100ms 才执行，这期间用户可能已经在
            //   界面里把模式切回了「无视输入法模式」。若照旧执行，这个残留的
            //   异步任务会真的去切输入法状态，干扰已经接管的无视输入法模式
            //   （用户反馈：切换模式用不了 → 换回无视模式 → 无视模式也跟着失效，
            //   根源之一就是这个残留任务还在后台切输入法）。
            //
            //   ★★★ 这条闸门是**唯一**的地方，"无视输入法模式绝不碰输入法"就靠它守住：
            //     `ForceEnglish` / `SwitchToChinese` / `RequestSwitchIme` /
            //     `MarkPendingSwitch` 四处全部同一口径 —— 无视模式下请求根本发不出去，
            //     就算发出去了，这里也会再挡一次。
            if (!ShouldApplySwitchNow(_config.WorkMode))
            {
                Dbg("切换输入法（" + reason + "）：当前模式不允许自动切换 → 放弃");
                return;
            }

            // 与「无视输入法模式」的确保中文共用同一个门闩：
            // 任何时刻只允许一个输入法操作在跑，避免两次切换互相抵消。
            if (System.Threading.Interlocked.Exchange(ref _imeSwitchBusy, 1) == 1)
            {
                // ★★ 绝不能**直接丢掉**（v37 定案）：
                //   被丢掉的可能正是"最后一个请求"（例如刚退出文字编辑、要切回英文），
                //   而此刻在跑的却往往是**过期的中文切换**。丢掉 = 就这样停在中文上，
                //   用户敲的下一个命令名会被中文输入法吃掉 → "不跟手、概率切不回英文"。
                //   登记待补，等当前动作一结束（500ms 轮询 / 焦点事件）立刻补做。
                Dbg("切换输入法（" + reason + "）：另一处正在切换 → 登记待补，切换一结束立即补做");
                MarkPendingSwitch(target, lackRealKey: !CanSendRealKey());
                return;
            }

            try
            {
                // ★★★ 第一道「过期请求」闸门（v37 新增，本轮不跟手的根因）。
                //
                //   `RequestSwitchIme` 只在**睡醒那一刻**查过一次 `_switchWanted`；
                //   可 `ApplySwitchIme` 自己还要跑几十~几百毫秒：
                //     · `HostTextFrame.Resolve()` 失败时会**遍历整棵窗口树**（上限 5000 节点）；
                //     · `GetModeAt` 的两条 IME 通道各是一次带超时的 `SendMessageTimeout`；
                //     · `TapShift` 的 down/up 之间固定 `Sleep(40)`。
                //   这段时间里用户完全可能已经**退出文字编辑**（Esc / 点绘图区），
                //   于是又有新的请求排队、`_switchWanted` 被改成 English。
                //   若不在动手前再查一次，那个**过期的中文切换**就会落在"已经该是英文"
                //   的时刻上，把输入法硬生生翻回中文 ——
                //   现场日志（v36 实测，19:22:06 那一轮）：
                //     19:22:06.831  CommandWillStart: TTEXT → 请求中文
                //     19:22:06.955  切换（需要中文）：探测读到 Chinese，但记录为 English → 不予采信
                //     19:22:06.992  ForceEnglish（用户已 Esc 退出）→ _switchWanted = English
                //     19:22:06.993  退出文字编辑态 → 恢复英文接管
                //     19:22:07.013  切换（需要中文）… 结果=True 方式=RealKey   ← ★ 过期请求落地，翻回中文！
                //     19:22:07.125  切换（需要英文）：另一处正在切换 → 跳过            ← 英文请求被丢
                //     19:22:07.166  切换（需要英文）：实际=Chinese → English          ← 170ms 后才纠回来
                //   这 170ms 的空窗正是用户"敲下命令名却没反应"的时刻。
                if (IsSupersededRequest(_switchWanted, target))
                {
                    Dbg("切换输入法（" + reason + "）：已被更新的请求取代（现在要 "
                        + (ImeMode)_switchWanted + "）→ 放弃本次动作");
                    return;
                }

                // ★★ ① 本模式**已经**把输入法置于目标态 → 不重复动作，连探测都不做。
                //
                //   这条是"输入法自己在那来回翻、完全跟不上手"的根治点（用户实测）。
                //   现场日志里能看到同一秒内连着三次「Chinese → English」：
                //   每次命令事件都重新探测一次，而 TSF/国产输入法的 IMM32 兼容层
                //   缓存**滞后**，立刻读回来还是旧值 → 判定"还没切成功"→ 再按一次
                //   Shift → 真的把刚切好的状态又翻转回去。命令越多翻得越勤。
                //   `_switchApplied` 记录的是**我们自己动作的结果**，它才是可信的；
                //   只有它被显式作废时才重新回到探测（见 InvalidateAppliedState）。
                //
                //   ★ 前提：`_switchApplied` 只由**可靠来源**写入（探测确认已是目标 /
                //     真的发出了模拟 Shift）。见下面 ⑥ 的分流 —— 绝不能把"直设报成功"
                //     也算进来，否则这里会拿一条假记录把后续所有动作全部短路掉。
                //
                //   ★★★ v49：**靶点窗口必须先解析**，因为"已置于 X"这条记录
                //     只对**写下它的那个窗口**成立（见 `_switchAppliedHwnd`）。
                //     v48 日志里切换靶点在"命令栏控件 0x40A82"与"文字编辑框 Edit"
                //     之间来回走 —— 那是两个不同的 IME 上下文，在 A 上按的 Shift
                //     不保证 B 也跟着变。拿 A 的记录去给 B 做"无需动作"的依据，
                //     就会在该切的时候不切（"有一定概率切换逻辑不对"）。
                bool targetTrusted;
                var hwnd = ResolveAcadImeTarget(out targetTrusted);

                // ★★★ v57：**幂等直设优先** —— 桥可用时，下面这一整套"当前到底是什么态"
                //   的判断（① 记录短路 / ② 30ms 探测 / ③④ 方向与盲动闸门）**整个用不上**。
                //
                //   为什么必须放在**最前面**（v56 实机事故的根因）：
                //     小狼毫的 `/ascii` / `/nascii` 是**设值**而不是翻转 ——
                //     "已经是英文了再设一次英文"与实际动作完全等价，也绝不会切反。
                //     可 v56 把它挂在下面 ①（`_switchApplied == target` → 无需动作）**之后**，
                //     等于让"一条记忆"来决定"还要不要设值"。而这条记忆在小狼毫上
                //     **根本无法验证**（铁律 13：IMM32 读不回真实态），
                //     一旦它错了（实机：冷启动后 `/ascii` 报 rc=0 却没真的生效），
                //     功能就**永久静默失效**：
                //       13:44:35 的引擎日志 —— RYBZ 命令期间需要英文，日志里却只有一行
                //       「本模式已置于 English → 无需动作」，一次 CLI 都没走；
                //       用户按 A 照样被中文输入法吃掉（命令栏只留一个小写 a）。
                //   ⇒ 幂等设值**不需要**知道当前态，就不该被当前态的判据挡住。
                //
                //   代价：每次"需要英文/中文"的事件都会起一个 `WeaselServer.exe` 客户端
                //   （实测 25~40ms，非翻转、无副作用、不依赖焦点窗口）。
                //   与"整个功能静默失效"相比，这代价可以忽略。
                //
                //   ★ 桥这一趟没成功（服务恰好退出等）→ **原样落回**下面的既有判据，
                //     非小狼毫用户、桥不可用时行为一个字节都不变。
                bool canKey = CanSendRealKey();
                if (ShouldPreferIdempotentSet(
                        _config.UseWeaselCli, WeaselBridge.IsAvailable,
                        WeaselBridge.IsServerRunning(), canKey))
                {
                    string cliDetail;
                    if (WeaselBridge.TrySetAscii(target, out cliDetail))
                    {
                        _switchApplied = target;
                        _switchAppliedHwnd = hwnd;   // ★ v49：记录连同"在哪个窗口上成立"一起写
                        ClearPendingSwitch();
                        Dbg("切换输入法（" + reason + "）：" + target
                            + " 方式=幂等直设（不看当前态）← " + cliDetail
                            + "（窗口=0x" + hwnd.ToInt64().ToString("X") + "）");
                        return;
                    }
                    // ★ 只在**异常路径**（服务在跑却仍失败）写"始终记录"的启动日志：
                    //   正常情况下这条一次都不会出现，不会淹没启动日志。
                    Note("切换输入法（" + reason + "）：幂等直设未生效（" + cliDetail
                        + "）→ 退回原有判据");
                }

                if (_switchApplied == target)
                {
                    if (IsAppliedStateUsableFor(_switchApplied, _switchAppliedHwnd, target, hwnd))
                    {
                        // 既然**可靠地**已经是目标态，那么针对同一目标登记的"待补"就过期了。
                        // 不清掉它，轮询/焦点事件会一次次把它捞出来重发真实 Shift ——
                        // 那会变成"明明已经是英文，还在反复按 Shift"，正是用户抱怨的横跳。
                        if (_pendingSwitch == (int)target) ClearPendingSwitch();
                        Dbg("切换输入法（" + reason + "）：本模式已置于 " + target + " → 无需动作");
                        return;
                    }

                    // ★ v49：记录说"已置于 target"，但它是在**另一个窗口**上写下的
                    //   —— 对当前靶点无效。这不是"无需动作"，而是"记录过期"。
                    Note("切换输入法（" + reason + "）：记录说已置于 " + target
                        + "，但那是在另一个窗口上写下的（记录窗口=0x"
                        + (_switchAppliedHwnd != IntPtr.Zero
                            ? _switchAppliedHwnd.ToInt64().ToString("X") : "0")
                        + " / 当前靶点=0x"
                        + (hwnd != IntPtr.Zero ? hwnd.ToInt64().ToString("X") : "0")
                        + "）→ **记录作废，重新动手**（不同 IME 上下文不能互认）");
                    InvalidateAppliedState("靶点窗口变了（" + reason + "）");
                }

                if (hwnd == IntPtr.Zero)
                {
                    Dbg("切换输入法（" + reason + "）：拿不到 CAD 靶点窗口（当前没有属于 AutoCAD 的窗口）→ 放弃");
                    return;
                }

                // ★② 读写必须落在**同一个窗口**上：应用端用 SetModeDirectOnWindow(hwnd)，
                //   探测端就必须是 GetModeOnWindow(hwnd)。用 GetMode（会回退到焦点/
                //   前台窗口）会读成"另一个窗口的状态"，据此判断必然出错。
                //
                // ★★★ v40：有可信记录时**跳过探测** —— 那是净白等的 ~30ms
                //   （两条 IMM32 通道各一次带超时的 SendMessageTimeout）。
                //   探测在本方法里只有两个用途：
                //     ① `_switchApplied` 缺失时充当依据（`actual = probed`）；
                //     ② 补记「用户进入前的原始状态」。
                //   两者都够不上时（记录已知、无 pending），`actual` 一定取
                //   `_switchApplied`，探测值只会被日志丢掉 —— 而用户在等这 30ms。
                //   现场日志（v39）：从触发到切换落地 ~170ms = 100ms 排队 + 30ms 探测 + 40ms Shift。
                bool needProbe = _switchApplied == ImeMode.Unknown || _switchOriginalPending;
                var probed = needProbe ? _ime.GetModeOnWindow(hwnd) : ImeMode.Unknown;

                // ★★ ②-b 靶点不可信（只有主窗口可兜底）→ 读数没用。
                //   IME 上下文不在主窗口上，ImmGetContext 会给出**默认的**转换状态
                //   （实测恒为「中文」）。若采信它：
                //     · "需要中文"会被判成"已经是中文，无需动作" → **永远切不到中文**；
                //     · 后面还会把假的 Chinese 写进 _switchApplied，污染后续所有判断。
                //   所以一律降级为"读不出来"，交给 _switchApplied（我们自己动作的结果）
                //   决定是否动手。
                if (!targetTrusted)
                {
                    if (probed != ImeMode.Unknown)
                    {
                        Dbg("切换输入法（" + reason + "）：靶点 0x"
                            + hwnd.ToInt64().ToString("X") + " 不是 IME 承接窗口，"
                            + "读数 " + probed + " 不可信 → 按「读不出来」处理");
                    }
                    probed = ImeMode.Unknown;
                }
                else if (probed != ImeMode.Unknown)
                {
                    _switchDetectable = true;
                }

                // ★★★ v39：补记「用户进入前的原始状态」——本轮"来回切换就失效"的根治点。
                //
                //   现场日志（v38 实机，19:49 / 19:50 / 19:51 各一次）：
                //     19:49:37.657  切换输入法模式：记录进入前状态 = Chinese
                //                   （★ 靶点不是 IME 承接窗口，该读数不可信）（窗口=0x96073E）
                //     19:49:37.658  Engage（切换输入法模式）：进入前状态=Chinese 可探测=False
                //     19:49:39.487  切换输入法（补做焦点回到 CAD）：实际=Chinese → English 结果=True
                //                   ↑ ★ 这一行读到的 Chinese **才是**用户的原始状态（可信靶点、
                //                     而且我们还没动过手），可它当时被丢掉了
                //     19:49:24.867  还原输入法状态：进入时的采样不可信 → 跳过，宁可不动作也不猜
                //     19:50:13.015  还原输入法状态：进入时的采样不可信 → 跳过
                //     19:50:48.396  还原输入法状态：进入时的采样不可信 → 跳过
                //   ⇒ 切换模式期间插件把输入法切成了英文，退出时**四次全部拒绝还原**，
                //     输入法就停在英文上；用户随后双击文字修改 → 打不出中文。
                //
                //   触发条件三条，缺一不可：
                //     · `_switchOriginalPending` —— 进入时确实没采到可信值；
                //     · `probed != Unknown`       —— 此刻靶点可信（上面的降级已把
                //                                    不可信读数改写为 Unknown）；
                //     · `_switchApplied == Unknown` —— 我们**还没动过手**，
                //                                    此刻的读数才等于"用户的原始状态"。
                if (_switchOriginalPending && probed != ImeMode.Unknown
                    && _switchApplied == ImeMode.Unknown)
                {
                    _switchOriginalMode = probed;
                    _switchOriginalTrusted = true;
                    _switchOriginalPending = false;
                    _switchDetectable = true;
                    Dbg("切换输入法（" + reason + "）：补记「用户进入前的原始状态」= " + probed
                        + "（进入时靶点不可信、此刻才读到；退出将据此还原）");
                }

                // ★★ ③ 「探测到的就是目标态」这条判据**从根上不可靠**，不能单独采信。
                //
                //   两个独立的原因，任一都足以让它失效：
                //     a) **"读不出来"与"真的是中文"无法区分**。IME 上下文只挂在真正
                //        承接文本输入的子控件上；靶点一旦退化成绘图区 / MDI 子框架，
                //        ImmGetContext 返回的是兼容层**默认值**，实测恒为「中文」。
                //        于是"需要中文"永远被判成"已经是中文，无需动作"。
                //     b) TSF / 国产输入法的 IMM32 兼容层读数**滞后**：我们刚用真实
                //        Shift 切到英文，紧接着读回来还是中文 —— 这正是历史上
                //        "每个命令事件再按一次 Shift、来回横跳"的根源。
                //
                //   现场日志（v35）把两者叠在一起：靶点 0x2C0940 经 Engage 日志确证
                //   是绘图区（类名 Afx:00007FF642700000:28:...，只算**弱特征**，
                //   `FocusMonitor` 同一时刻明确判它「是编辑框=False」），而
                //   「探测已是 Chinese，无需动作」出现了 6 次 —— 用户要中文时一次没切成。
                //
                //   所以：**只信 `_switchApplied`**（我们自己动作的可靠结果）。
                //     · 有记录 → 以记录为"当前真实态"，探测读数一律不采信；
                //     · 无记录（刚接管 / 刚被用户手动切换作废）→ 才允许探测帮忙。
                bool stateKnown = _switchApplied != ImeMode.Unknown;
                var actual = stateKnown ? _switchApplied : probed;

                if (stateKnown && probed != ImeMode.Unknown && probed != _switchApplied)
                {
                    Dbg("切换输入法（" + reason + "）：探测读到 " + probed + "，但本模式记录为 "
                        + _switchApplied + " → 判为兼容层滞后/默认值，不予采信（窗口=0x"
                        + hwnd.ToInt64().ToString("X") + "）");
                }

                // ③ 当前真实态（按上面的判据修正后）已经是目标 → 记下即可。
                //   ★ 有记录时 actual 就是记录，等于"我们自己动作的结果"，一定可靠；
                //     无记录时才会靠探测下这个结论（此时探测是唯一的依据）。
                if (actual == target)
                {
                    _switchApplied = target;
                    _switchAppliedHwnd = hwnd;   // ★ v49：连同"在哪个窗口上成立"一起记
                    ClearPendingSwitch();
                    Dbg("切换输入法（" + reason + "）：已是 " + target + "，无需动作" +
                        "（窗口=0x" + hwnd.ToInt64().ToString("X") + "）");
                    return;
                }

                // ④ 知道"当前不是目标态"吗？
                //   · 知道（actual != Unknown）→ 方向明确，允许动手；
                //   · 不知道（既没有记录、探测也读不出来）→ **拒绝盲动**。
                //     盲按一下 Shift 有 50% 概率把本来就是目标的状态切反，
                //     代价是用户打不出字。登记待补，等状态可判定时再切。
                if (actual == ImeMode.Unknown)
                {
                    Dbg("切换输入法（" + reason + "）：读不出当前状态、本模式也没有记录 → "
                        + "不盲动，登记待补（等状态可判定时再切）");
                    MarkPendingSwitch(target, lackRealKey: !CanSendRealKey());
                    return;
                }

                // ⑤ 方向已明确 → 必须真的动手。
                //   ★ `blind`（允许盲切）与 `distrustProbe`（不采信探测）必须同时打开：
                //     有记录时，探测这条通道已经证明会给出滞后/默认读数；再让 SwitchTo
                //     自己探一次，它就会把"读回来是目标"当成"已经切好"
                //     （AlreadyAtTarget 属于**可信**达成方式），于是白跑一趟、
                //     `_switchApplied` 还被写成目标 —— 正是 a)/b) 两条失效路径的合流。
                //     强制它跳过探测、直接发真实 Shift。
                //
                // ★★★ 第二道「过期请求」闸门：上面那一段（解析靶点 + 两条 IME 通道探测）
                //   已经花掉了不少时间，**发送真实 Shift 之前**必须再确认一次。
                //   这是最后一次机会 —— 一旦 Shift 发出去，就再也收不回来了。
                if (IsSupersededRequest(_switchWanted, target))
                {
                    Dbg("切换输入法（" + reason + "）：发键前发现已被更新的请求取代（现在要 "
                        + (ImeMode)_switchWanted + "）→ 一个按键都不发");
                    return;
                }

                // ★ v57：`canKey` 已在方法上方（幂等直设判据那里）取过一次 ——
                //   这里不再重复取，两个取值点会各自漂移，正是要避免的写法。

                ImeController.ImeSwitchManner manner;
                // ★ v56：统一入口 —— 小狼毫优先走命令行直接设值，其余原样退回真实 Shift。
                bool ok = SwitchImeOnce(hwnd, target, canKey, out manner);

                // ★★ ⑥ 只采信**可靠来源**的结果 —— 这是本轮实机事故的根治点。
                //
                //   现场日志（v33）：
                //     18:16:16.528  切换（进入 CAD）：Chinese → English 结果=True
                //                    可发真实按键=False（窗口=0x3F0B3C）
                //   那是用户在设置面板上点「切换输入法模式」的瞬间：前台是面板，
                //   于是只能走"直设转换状态"这条通道。它对这款输入法**报成功但实际无效**
                //   （`SetModeDirectOnWindow` 的复核是同一条通道自证，等于没校验）。
                //   结果 `_switchApplied` 被写成 English，此后每一次英文请求都在
                //   ① 处被短路成"无需动作"，**再也没有发过一次真正有效的 Shift** ——
                //   用户看到的正是「切换输入法模式还是不行」。
                //
                //   所以：只有「探测确认已是目标」与「真的发出了模拟 Shift」才算达成；
                //   直设成功一律降级为"尽力而为"，并登记待补，等 CAD 回到前台后补一次。
                //   判据集中在 ImeController.IsReliableManner（自测工具会钉住这条契约），
                //   这里不再另写一份 —— 两处各写一遍必然会在日后各自漂移。
                if (ok && !ImeController.IsReliableManner(manner))
                    ok = false;   // 语义降级：直设不算"已切好"

                if (ok)
                {
                    _switchApplied = target;
                    _switchAppliedHwnd = hwnd;   // ★ v49：这条记录**只对这个窗口成立**
                    ClearPendingSwitch();
                    // ★★★ v47：盲切只是"按键发出去了"，**不等于输入法真的翻转了**。
                    //   校验放到锁外执行（见下面的 finally 之后）—— 校验要等 ~120ms，
                    //   放在锁内会让每次切换都多背 120ms，那正是 v40 砍掉的"不跟手"。
                    //   只有靶点本身能承接 IME 上下文（targetTrusted）时才谈得上回读校验。
                    verifyAfterUnlock = targetTrusted && hwnd != IntPtr.Zero;
                    if (verifyAfterUnlock) verifyHwnd = hwnd;
                }
                // ★ 这里不再有 `else if (actual == ImeMode.Unknown) _switchDetectable = false;`
                //   —— 上面 ④ 已经把"读不出来"的情况提前返回了，走到这里 actual 必定
                //   是明确的态。留着那条分支只会给人"探测失败会在这里降级"的错觉。
                //   `_switchDetectable` 的权威设置点在 CaptureOriginalIme（每次接管时刷新）。

                // ⑦ 未通过真实 Shift 达成 → 登记"待补"，等 CAD 回到前台时立即补做。
                //   为什么不能就此算了：用户切模式的入口就是设置面板，那一刻前台
                //   必然是面板、必然发不出真实 Shift；不补这一次，用户合上面板后
                //   敲的第一个字符就还是中文，命令名根本打不出来。
                if (!ok) MarkPendingSwitch(target, lackRealKey: !canKey);

                // ★ v45：**失败才进"始终记录"的启动日志** —— 成功是常态
                //   （v44 实机日志里 128 次全是 RealKey），只记失败才不会淹没启动日志。
                string outcomeLine = "切换输入法（" + reason + "）：实际=" + actual
                    + (probed != actual ? "（探测读数 " + probed + " 已判为不可信、已忽略）" : "")
                    + " → " + target +
                    " 结果=" + ok + " 方式=" + manner +
                    " 可发真实按键=" + canKey +
                    "（窗口=0x" + hwnd.ToInt64().ToString("X") + "）";
                if (ok) Dbg(outcomeLine);
                else Note(outcomeLine);
            }
            catch (Exception ex)
            {
                Dbg("切换输入法（" + reason + "）异常：" + ex.Message);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _imeSwitchBusy, 0);
            }

            // ★★★ v47：**盲切回读校验，必须在锁外跑**。
            //
            //   为什么不能放在 try 里（顺手的写法）：
            //     · 校验要 `Sleep(120)` 等输入法把状态写完 —— 放在锁内 = 每次切换
            //       都多背 120ms，正是 v40 花力气砍掉的"不跟手"；
            //     · 锁内还在 `_imeSwitchBusy=1`，校验失败后的"补发 Shift"会撞自己的门闩，
            //       变成了"登记待补"而不是当场补 —— 那一下的时效性就丢了。
            //   校验本身带三道护栏（见 VerifySwitchAfterUnlock），绝不会乱发按键。
            if (verifyAfterUnlock)
                VerifySwitchAfterUnlock(target, verifyHwnd, reason);
        }

        /// <summary>
        /// 还原「电脑的默认输入法状态」—— 即本次接管之前用户自己的中英状态。
        ///
        /// 触发时机：CAD 最小化 / 切到别的程序 / AutoCAD 退出。
        ///
        /// ★★ 三条铁律（越界一次就会去改别的程序的输入法）：
        ///   1. **只对 AutoCAD 自己的窗口操作** —— 此刻 CAD 已失去前台，
        ///      按"焦点窗口"去设置，改的就是别人的输入法；
        ///   2. **绝不模拟 Shift** —— Shift 是系统级热键，会落到当前前台程序上；
        ///   3. **探测不出状态就什么都不做** —— 做不到就如实说做不到，
        ///      猜错了会把用户的输入法锁在错误状态上，比不动作更糟。
        ///
        /// 因此本方法对"输入法支持状态读写"的情况可靠；对两条通道都读不出的
        /// 输入法会自动跳过（此时 <see cref="SwitchImeDetectable"/> 早就是 false，
        /// 界面与 XIMESTATUS 会提示用户改用无视输入法模式）。
        /// </summary>
        /// <summary>
        /// 退出接管时**是否应该**把输入法还原成用户的原始状态（v39 新增，纯判据）。
        ///
        /// 依据只有一条：<paramref name="applied"/> —— **我们自己动作的结果**
        /// （<see cref="_switchApplied"/>）。绝不看探测读数：
        ///   · 探测会把"读不出来"报成默认的「中文」（靶点退回绘图区/主窗口时）；
        ///   · TSF/国产输入法的兼容层读数还会**滞后**。
        ///
        /// 三条判据：
        ///   ① 原始状态必须**可信**（`trusted`）—— 不可信的多半是兼容层默认值，
        ///      照它还原等于把用户锁死在一个我们从没读到过的状态上；
        ///   ② 我们这次**真的动过手**（`applied != Unknown`）—— 没动过手就无从判断
        ///      "现在是不是被我们改掉了"，宁可不动；
        ///   ③ 而且确实改成了**别的**状态（`applied != original`）—— 相等就无事可做。
        /// </summary>
        internal static bool ShouldRestoreOriginalIme(ImeMode original, bool trusted, ImeMode applied)
        {
            if (!trusted) return false;
            if (original == ImeMode.Unknown) return false;
            if (applied == ImeMode.Unknown) return false;
            return applied != original;
        }

        /// <summary>
        /// 退出接管：把用户的输入法状态还原回去（**用了真实 Shift**，见下）。
        ///
        /// ★★★ v39 重写。旧实现有三个各自都能致命的问题，合起来让"还原"从未生效过：
        ///   ① 用了 <see cref="ImeController.RestoreOnWindow"/> —— 它走的是
        ///      `SetModeDirectOnWindow`（直设转换状态）+ 同一条通道自证；
        ///      直设通道对 TSF / 国产输入法**报成功但实际无效**。
        ///      而真正对所有输入法都有效的只有**真实 Shift**。
        ///   ② 还原发生在 <see cref="Disengage"/> 的那一刻，而那一刻前台**必然是**
        ///      设置面板或别的程序 → `CanSendRealKey()` 恒 false →
        ///      既发不出真实 Shift，写进去的状态也没人认。
        ///   ③ `_switchOriginalTrusted` 因为"进入时前台是面板"被置 false → 直接跳过。
        ///
        ///   现在：
        ///     · 判据走 <see cref="ShouldRestoreOriginalIme"/>（只信 `_switchApplied`）；
        ///     · 需要还原时**必须发真实 Shift**（`blind: true` + `distrustProbe: true`）；
        ///     · 此刻发不出去（前台不是 AutoCAD）→ 登记 <see cref="_restorePendingMode"/>，
        ///       保留全部记录，等焦点回到 AutoCAD 由 <see cref="TryFlushPendingRestore"/> 补做；
        ///     · 没有可信依据 → 一律不动作（"猜错了比不动作更糟"）。
        /// </summary>
        private void RestoreOriginalIme()
        {
            // ★ 只看"当初有没有记录过"，**不看** _config.WorkMode ——
            //   用户随时可能在界面里把模式改掉，但只要这次接管记录过原始状态，
            //   收尾时就一定要还回去（详见 _engagedMode 的注释）。
            if (!_switchOriginalCaptured) return;

            var target = _switchOriginalMode;
            var applied = _switchApplied;   // 我们自己的动作结果（唯一可信来源）

            if (!ShouldRestoreOriginalIme(target, _switchOriginalTrusted, applied))
            {
                if (!_switchOriginalTrusted)
                    Note("还原输入法状态：进入时的采样不可信（靶点不是 IME 承接窗口）→ "
                        + "跳过，宁可不动作也不猜");
                else if (applied == ImeMode.Unknown)
                    Note("还原输入法状态：本模式没有切过输入法（无动作记录）→ 跳过，不猜方向");
                else
                    Note("还原输入法状态：当前已是 " + target + " → 无需动作");

                // ★ v44：走到这里说明"没什么可还原的"，即输入法**并没有被我们留在反面** ——
                //   那么"我们可能把它留在了英文"这条证据就不成立，必须撤销
                //   （否则下次双击文字会把正确的中文读数翻掉）。
                if (applied == target)
                    ApplyImeLeftEnglishResolution(target, true, "收尾时记录显示已在目标态");

                // ★★★ v42：这里"不还原"分两种，必须区分开 ——
                //   · 我们**确实**把输入法切成了英文，而用户的原始状态**不是**英文
                //     （或原始状态根本不可知）→ 输入法就被落在英文上了，用户打不出中文。
                //     记下证据，交给无视模式的「确保中文」去补救。
                //   · 我们没动过手，或原始状态本来就是英文 → 与我们无关，不留证据。
                if (applied == ImeMode.English
                    && !(_switchOriginalTrusted && _switchOriginalMode == ImeMode.English))
                    MarkImeLeftEnglish("退出切换模式时没有可信依据可还原，而输入法被我们切成了英文");

                FinishRestore(null);
                return;
            }

            // ★ 发不出真实 Shift（前台不是 AutoCAD，例如用户正对着设置面板点按钮）→
            //   绝不能退化成"直设转换状态"（对 TSF/国产输入法报成功但无效）。
            //   保留全部记录、登记延迟还原；焦点回到 AutoCAD 后补一次真实 Shift。
            if (!CanSendRealKey())
            {
                _restorePendingMode = (int)target;
                _restorePendingTick = Environment.TickCount;
                // ★ v42：还原被推迟 = 此刻输入法仍停在英文 → 留证据（目标本来是英文则不必）。
                if (target != ImeMode.English)
                    MarkImeLeftEnglish("退出切换模式时发不出真实按键，还原被推迟执行");
                Note("还原输入法状态：此刻发不出真实按键（前台不是 AutoCAD）→ "
                    + "保留记录、登记延迟还原（目标=" + target + "；原态=" + target
                    + " 当前态=" + applied + "），焦点回到 AutoCAD 后立即补做");
                return;   // ★ 不 FinishRestore：记录要留着给补做用
            }

            DoRestoreFlip(target, applied, "还原输入法状态");
        }

        /// <summary>
        /// 真正执行一次"还原翻转"：发一个**真实 Shift**（唯一对所有输入法都有效的手段）。
        /// </summary>
        private void DoRestoreFlip(ImeMode target, ImeMode appliedBefore, string tag)
        {
            // ★ v47：这里也要拿"靶点是否真的承接着 IME 上下文"—— 回读校验的合法性
            //   完全建立在这上面（没有 IME 上下文的窗口，读数是兼容层默认的「中文」）。
            bool restoreTrusted;
            var hwnd = ResolveAcadImeTarget(out restoreTrusted);
            if (hwnd == IntPtr.Zero)
            {
                Note(tag + "：拿不到属于 AutoCAD 的窗口 → 跳过");
                if (target != ImeMode.English) MarkImeLeftEnglish(tag + "：拿不到靶点窗口，还原未执行");
                ApplyImeLeftEnglishResolution(target, false, "拿不到靶点窗口");
                FinishRestore(null);
                return;
            }

            bool canKey = CanSendRealKey();

            ImeController.ImeSwitchManner manner;
            // ★ v56：还原同样走统一入口（小狼毫 → /nascii 幂等设值；其余 → 真实 Shift）。
            bool ok = SwitchImeOnce(hwnd, target, canKey, out manner);
            if (ok && !ImeController.IsReliableManner(manner))
                ok = false;   // 直设不算达成（与 ApplySwitchIme 同一条契约）

            if (ok)
            {
                _switchApplied = target;
                _switchAppliedHwnd = hwnd;   // ★ v49：还原同样**只对这个窗口成立**
            }

            // ★ v45：改写 Note —— "还原有没有真的成功"是判定
            //   「用户双击文字打不出中文」的第一现场，必须始终留痕。
            Note(tag + " → " + target + "（真实 Shift 翻转；还原前当前态=" + appliedBefore
                + "）结果=" + ok + " 方式=" + manner
                + "（窗口=0x" + hwnd.ToInt64().ToString("X") + "）");

            if (ok)
            {
                // ★★ v44：还原**成功**了 → 必须当场撤销那条"我们可能把输入法留在了英文"的证据。
                //
                //   实机日志（v43，AutoCAD 2022 + 天正）：
                //     21:39:28.451  记下证据：输入法可能仍停在英文（退出切换模式时发不出真实按键…）
                //     21:39:29.918  补做延迟还原 → Chinese（真实 Shift 翻转）结果=True 方式=RealKey
                //     21:39:30.823  确保中文：靶点 0x83055C 读回 Chinese，但…判该读数不可信   ← 证据还在！
                //     21:39:30.868  确保中文：已发送一次 Shift 切换（结果=True）              ← 把中文翻成了英文
                //
                //   也就是说：延迟还原**已经把中文切回来了**，可那条证据没人撤销 ——
                //   于是下一次双击文字时，一个**完全正确的**「Chinese」读数被当成不可信，
                //   又盲切一次真实 Shift，**把刚切好的中文翻回英文**。
                //   用户看到的现象：从切换模式切回无视模式后，有概率双击文字打不出中文；
                //   而且"用一次 text 命令输入文字后就好了"—— 因为那次 Shift 把证据消费掉了。
                //
                //   ★ 为什么"还原成功"可以当作可信事实：`SwitchTo` 达成 RealKey 的前提是
                //     **我们自己的记录明确显示当前是反面**（这里 appliedBefore=English），
                //     才发的那一次真实 Shift。既然发之前确定在英文，发一次就是中文。
                ApplyImeLeftEnglishResolution(target, true, "还原成功");
                FinishRestore(null);

                // ★★★ v47：**还原同样是"盲切"** —— 上面那次 `SwitchTo` 报 RealKey
                //   只说明 Shift 进了系统输入队列，不说输入法真的翻回去了。
                //   而还原失败的后果是最严重的那一种：输入法被永久留在英文，
                //   用户下次双击文字打不出中文（v39 花了整版修的就是这个现象）。
                //   所以：**回读到反面就把它当作"没还原"**，保留待还原、留证据，
                //   交给延迟还原 / 无视模式的「确保中文」去补救 —— 而不是就此宣告成功。
                //
                //   放在 FinishRestore 之后没关系：本方法不复用 `_switchApplied` 护栏，
                //   失败时自己重建 `_restorePendingMode` 等一整套待还原状态。
                if (restoreTrusted)
                    VerifyRestoreAfterUnlock(target, hwnd, tag);
            }
            else
            {
                // 没翻成（例如又被前台护栏挡下）→ 再登记一次，等下一轮补做
                _restorePendingMode = (int)target;
                _restorePendingTick = Environment.TickCount;
                if (target != ImeMode.English)
                    MarkImeLeftEnglish(tag + "：真实 Shift 没能达成，输入法可能仍在英文");
                ApplyImeLeftEnglishResolution(target, false, "还原未达成");
                Note(tag + "：本次没能通过真实 Shift 达成 → 保留待还原（目标=" + target + "）");
            }
        }

        /// <summary>
        /// 记下「我们可能把输入法留在了英文」这条**硬证据**（v42）。
        ///
        /// ★ 为什么必须有这个记号，而不是让无视模式自己去"读"：
        ///   实机日志（v41）证明，刚从切换模式切过来时，靶点窗口会给出一个与事实
        ///   相反的「Chinese」读数，于是「确保中文」判"已是中文，无需动作"→ 一个 Shift
        ///   都不发 → 用户双击文字打不出中文（整个日志里 `已发送一次 Shift` 出现 0 次）。
        ///   唯一可靠的信息是"**我们刚才亲手把它按在了英文上**"。
        ///
        /// ★ 只留一次、消费即清（见 <see cref="ApplyEnsureChinese"/>）：它只是
        ///   「下一次进入文字编辑时要主动切回中文」这一条动议，不是长期状态。
        ///
        /// ★★★ v44 补上它**另一半**契约（漏掉这一半造成了 v42/v43 的现场故障）：
        ///   **一旦输入法被确认切回了目标态，这条证据必须当场撤销**
        ///   （见 <see cref="ResolveImeLeftEnglishAfterRestore"/> 与
        ///    <see cref="ApplyImeLeftEnglishResolution"/>）。
        ///   只记不撤 → 证据比事实活得久 → 下一次双击文字时把**正确的**「中文」
        ///   读数误判为不可信、盲切一次 Shift，反而把中文翻成英文。
        /// </summary>
        private void MarkImeLeftEnglish(string why)
        {
            if (_imeLeftEnglish) return;
            _imeLeftEnglish = true;
            _imeLeftEnglishWhy = why;
            _imeLeftEnglishTick = Environment.TickCount;   // ★ v44：留痕，供"这条证据活了多久"审计
            // ★ v45：改走 Note —— 这条证据是"用户双击文字打不出中文"的直接因，
            //   必须出现在**始终记录**的启动日志里（哪怕用户忘了开 XIMEDEBUG）。
            Note("★ 记下证据：输入法可能仍停在英文（" + why + "）→ "
                + "无视输入法模式下第一次进入文字编辑时会据此**无条件切回中文**一次");
        }

        /// <summary>
        /// 一次"还原收尾"之后，`_imeLeftEnglish` 该取什么值（v44，**纯判据，自测钉住**）。
        ///
        /// 决策表：
        ///   还原动作**成功**             → false（我们亲手切回了目标态，"留在英文"不成立）
        ///   还原**没成功 / 被推迟 / 超时** → true （目标不是英文时，确实可能还停在英文）
        ///   本来就是英文（没什么可撤的）   → false
        ///
        /// ★ 抽成纯函数的理由：这是"证据的寿命"这一条时序契约，靠注释守不住 ——
        ///   v42 引入 `_imeLeftEnglish` 时只写了"何时记下"，漏了"何时撤销"，
        ///   结果在实机上表现为**有概率的双击文字打不出中文**（见
        ///   <see cref="DoRestoreFlip"/> 里的实测日志）。这条契约必须有可执行断言。
        /// </summary>
        internal static bool ResolveImeLeftEnglishAfterRestore(
            ImeMode restoredTo, bool restoreSucceeded, bool hadEvidence)
        {
            if (!hadEvidence) return false;
            if (restoreSucceeded) return false;          // ★ v44 的修复点：成功即撤销
            return restoredTo != ImeMode.English;        // 没成功 → 目标非英文时才成立
        }

        /// <summary>
        /// 按 <see cref="ResolveImeLeftEnglishAfterRestore"/> 的结果落地，并如实记录撤销动作
        /// （撤销是"扣掉一次多余的 Shift"，必须留痕，否则以后没人说得清它为什么没动手）。
        /// </summary>
        private void ApplyImeLeftEnglishResolution(ImeMode restoredTo, bool restoreSucceeded, string why)
        {
            bool before = _imeLeftEnglish;
            bool after = ResolveImeLeftEnglishAfterRestore(restoredTo, restoreSucceeded, before);
            if (before == after) return;
            _imeLeftEnglish = after;
            if (!after)
            {
                int age = unchecked(Environment.TickCount - _imeLeftEnglishTick);
                Note("★ v44 撤销证据：输入法已被确认回到 " + restoredTo + "（" + why + "）→ "
                    + "不再据此额外切一次（否则会把刚切好的中文又翻回英文）"
                    + "；这条证据原本来自「" + (_imeLeftEnglishWhy ?? "?") + "」，存活 " + age + "ms");
                _imeLeftEnglishWhy = null;
                _imeLeftEnglishTick = 0;
            }
        }

        /// <summary>
        /// 收尾：清掉本次接管留下的全部输入法状态记录（还原完成或确定无需还原时调用）。
        ///
        /// ★★ 刻意**不**在这里清 `_imeLeftEnglish`（v44 血泪教训）：
        ///   本方法有若干调用点是"**先记下证据、紧接着收尾**"
        ///   （不还原分支 / 拿不到靶点窗口 / 延迟还原超时 / 方向不可判定）——
        ///   如果在这里顺手清掉，就把**刚刚记下的**证据一起抹了，
        ///   那几条真正的补救路径会全部失效。
        ///   `_imeLeftEnglish` 的撤销只走 <see cref="ApplyImeLeftEnglishResolution"/>，
        ///   且只在"输入法被确认回到目标态"时发生。
        /// </summary>
        private void FinishRestore(string logLine)
        {
            if (logLine != null) Dbg(logLine);
            _switchOriginalCaptured = false;
            _switchOriginalPending = false;
            _switchOriginalTrusted = false;
            _switchWanted = -1;
            ClearAppliedState();
            _pendingSwitch = -1;   // 收尾阶段不再补做任何切换（同 Disengage 的理由）
            _restorePendingMode = -1;
        }

        /// <summary>
        /// 若存在**待延迟还原**、且此刻前台已回到 AutoCAD（能发真实 Shift）→ 立即补做。
        ///
        /// 由焦点事件与 500ms 轮询共同驱动 —— 与 <see cref="TryFlushPendingSwitch"/>
        /// 同源：面板是自有窗口，关掉面板时未必触发 FocusObjectChanged，轮询是唯一不漏的通道。
        /// </summary>
        private void TryFlushPendingRestore(string why)
        {
            int pending = _restorePendingMode;
            if (pending < 0) return;

            // 超时保护：陈年的还原记录不再执行 —— 期间用户可能已经自己切过好几次，
            // 照着旧目标翻一下反而会打乱他。（宁可不动手）
            if (unchecked(Environment.TickCount - _restorePendingTick) > RestorePendingTtlMs)
            {
                Dbg("补做延迟还原（" + why + "）：登记已超时（>" + RestorePendingTtlMs
                    + "ms）→ 放弃，不再翻转（避免打乱用户当前状态）");
                if ((ImeMode)pending != ImeMode.English)
                    MarkImeLeftEnglish("延迟还原超时被放弃，输入法可能仍停在英文");
                FinishRestore(null);
                return;
            }

            if (!CanSendRealKey()) return;   // 前台还不是 AutoCAD → 一个按键都不发

            var target = (ImeMode)pending;
            var applied = _switchApplied;

            if (applied == target)
            {
                Dbg("补做延迟还原（" + why + "）：当前态已是 " + target + " → 无需动作");
                // ★ v44：记录显示已经在目标态 → 证据不成立，撤销（理由同 RestoreOriginalIme）。
                ApplyImeLeftEnglishResolution(target, true, "补做时记录显示已在目标态");
                FinishRestore(null);
                return;
            }

            if (applied == ImeMode.Unknown)
            {
                // 期间状态记录被作废（用户手动切过 / 又进出过接管）→ 方向不明，放弃。
                Dbg("补做延迟还原（" + why + "）：当前态记录已被作废（用户手动切过？）→ "
                    + "放弃，不猜方向");
                if (target != ImeMode.English)
                    MarkImeLeftEnglish("延迟还原的方向已不可判定，输入法可能仍停在英文");
                FinishRestore(null);
                return;
            }

            Dbg("补做延迟还原（" + why + "）：目标=" + target);
            DoRestoreFlip(target, applied, "补做延迟还原（" + why + "）");
        }

        /// <summary>
        /// 作废「我们自己确信的输入法状态」记录。
        ///
        /// 触发时机：观察到用户**自己**按了 Shift 切换中英，或接管状态发生变化。
        ///
        /// ★ 为什么必须作废：<see cref="ApplySwitchIme"/> 的第一条判据就是
        ///   "若 <c>_switchApplied == target</c> 则不做任何动作" —— 它相信的是
        ///   我们**自己动作的结果**（因为 TSF/兼容层的探测读数会滞后，不可信）。
        ///   用户一旦手动切过，这份记录就过期了；不作废的话，插件会一直认为
        ///   "已经在英文了"，于是命令结束后不再把中文切回英文 —— 用户感觉"不跟手"。
        /// </summary>
        private void InvalidateAppliedState(string reason)
        {
            // ★ 用户亲手动了输入法 = 一次"全新场景"：把「待补」的重试预算一并重置。
            //   否则一个已被判"放弃"的目标（重试 4 次用尽）会永远卡在放弃态 ——
            //   哪怕用户刚刚手动按 Shift 证明了这台机器上按键是有效的，
            //   插件也再不会为它尝试一次。重置预算是安全的：它只影响"允许再试几次"。
            _pendingTries = 0;
            _pendingTriesTarget = ImeMode.Unknown;

            if (_switchApplied == ImeMode.Unknown) return;
            Dbg("作废输入法状态记录（" + reason + "）：原记录=" + _switchApplied + " → 下次重新探测");
            ClearAppliedState();
        }

        #endregion

        #region 键盘钩子处理

        /// <summary>钩子决策入口：记录诊断信息后转交核心判断。</summary>
        private bool ShouldCaptureKey(int vkCode, bool isDown)
        {
            // ★ 编辑态的「及时退出」——这是「编辑结束后能否立刻敲命令」的关键。
            //
            //   实测（天正单行文字）：用户「打完中文 → 按 Esc → 立刻敲 line」时，
            //   焦点事件（EVENT_OBJECT_FOCUS）要等约 0.5 秒后才报告"焦点回到命令栏"，
            //   在这个空窗期里敲下的第一个字母不会被接管（被输入法吃掉），
            //   表现为命令栏里出现中文或字母缺失。
            //
            //   这里改为**按键时实时校验焦点**：只要焦点已经不在文字编辑框上，
            //   立刻退出编辑态并让本次按键走正常接管流程 —— 同一个按键即可生效，
            //   无需等待焦点事件。
            //
            //   注意：**刻意不做 Esc 特判**。中文输入法里 Esc 常用于「取消拼音候选」，
            //   若一见 Esc 就退出编辑态，会打断用户继续输入中文。
            //   而实时校验天然区分了两种情况：
            //     · Esc 后编辑框仍开着（取消候选）→ 焦点还在编辑框 → 维持编辑态 ✓
            //     · Esc 结束编辑、编辑框关闭 → 焦点已离开 → 退出编辑态 ✓
            if (_inTextEditMode && isDown && !SwallowOnly && !IsFocusInEditBox())
                ExitTextEditMode("焦点已离开编辑框（实时校验）");

            bool result = ShouldCaptureKeyCore(vkCode, isDown);
            LastShouldCapture = result;
            return result;
        }

        /// <summary>
        /// 实时判断「键盘焦点当前是否仍在文字编辑框内」。
        /// 用于编辑态的快速退出 —— 比等待焦点事件及时得多。
        ///
        /// ★ 保守策略：拿不到焦点句柄或发生异常时返回 true（视为仍在编辑），
        ///   宁可多维持一会儿编辑态（有兜底巡检收尾），也不要误判打断用户输入。
        /// </summary>
        private bool IsFocusInEditBox()
        {
            try
            {
                var focus = CharInjector.GetRealFocusWindow();
                if (focus == IntPtr.Zero) return true;
                if (!FocusMonitor.IsEditBoxWindow(focus)) return false;
                if (HostTextFrame.IsCommandLineWindow(focus)) return false;
                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 「焦点刚落到一个文字编辑控件」时，该不该真的执行一次"确保中文"动作
        /// —— 纯判据，无副作用（v46，自测钉住）。
        ///
        /// 决策表：
        ///   本来就不在编辑态                 → true （正常进入）
        ///   已在编辑态，但换了一个编辑控件   → true （★ v46 的修复点：新的中文输入场景）
        ///   已在编辑态，还是同一个编辑控件   → false（控件内部焦点抖动，不重复动手）
        ///
        /// ★ 抽成纯函数的理由：这条契约的失效形式是"**一个 Shift 都不发**"，
        ///   而它看起来又像"插件什么都没做"（没有任何异常、没有任何失败日志），
        ///   实机上只会表现为"偶尔打不出中文"。靠注释守不住，必须有可执行断言。
        /// </summary>
        internal static bool ShouldRunTextEditEntry(bool alreadyInEditMode, bool isNewEditHost)
        {
            return !alreadyInEditMode || isNewEditHost;
        }

        /// <summary>
        /// ★★★ v55：命令执行期间**禁止**进入「文字编辑态」（纯判据，自测钉死）。
        ///
        /// 为什么（AutoCAD 2022 + 天正 T20 v10「任意布置」二级命令失灵的真根因）：
        ///
        ///   天正在 RYBZ 选块后会弹一个**内部参数输入框**（实测类名 = `Edit`），
        ///   它在 30~300ms 内抢到焦点又被主窗口 `AfxMDIFrame140u` 抢回去。
        ///   原实现只看「焦点落在可编辑控件、且不是命令栏」就判定为"用户要输入中文文字"——
        ///   于是 `EnterTextEditMode` → `SwitchToChinese` + `EnsureChineseForTextInput`
        ///   ✘✘ **在二级命令期间主动把输入法往中文切**。
        ///
        ///   后果（2026-10-05 实机日志钉死）：用户按 A/E/D/F/X/Y 时输入法正在中文态，
        ///   字母被输入法吃掉 —— 命令栏只剩一个残留的小写 `a`，天正收不到 → 命令失效。
        ///   2014 之所以"正常"，只是那次用户输入法**恰好是英文态**（命令栏输出大写 `A`）。
        ///   ⇒ 与"注入通道"无关：`ReplayVk` / `PostKey` / `PassThrough` / **插件完全关闭**
        ///     四种情况**全都是同一个症状**，因为祸首是输入法状态，不是按键通道。
        ///
        /// 判据很窄，不误伤真实场景：
        ///   · **命令执行期间**（`hasActiveCommand`）→ **绝不进入**文字编辑态。
        ///     真正要输入中文文字的命令（TEXT / MTEXT / DTEXT…）走的是
        ///     `OnCommandWillStart` 里 `ChineseNeed.Exact` 那条路（命令一开始就
        ///     `SwitchToChinese` + `EnsureChineseForTextInput`），**不依赖本判据**；
        ///     而它们编辑文字时用的是各自的 `Edit/RichEdit` 宿主，仍由
        ///     `EnterTextEditMode` 兜底（那时 `hasActiveCommand` 在编辑期同样为 true ——
        ///     所以**必须**放行"白名单文字命令"这一类，见下）。
        ///   · 命令结束 / 空闲（`hasActiveCommand == false`）→ 照旧进入 ——
        ///     「双击文字直接改」这条不经过命令系统的路径**完全不受影响**。
        ///
        /// ★ 结论：只在「有活动命令」**且该命令不是文字类命令**时禁止进入。
        /// </summary>
        /// <param name="hasActiveCommand">此刻是否处于某个命令的执行期间（`_currentCommand != null`）。</param>
        /// <param name="commandNeedsChinese">该命令是否是「需要中文」的文字类命令（白名单精确命中/关键词命中）。</param>
        internal static bool ShouldEnterTextEditModeForFocus(bool hasActiveCommand, bool commandNeedsChinese)
        {
            if (!hasActiveCommand) return true;       // 空闲 / 命令已结束 → 照旧（双击文字改字）
            return commandNeedsChinese;              // 命令期间：只有文字类命令才允许进编辑态
        }

        /// <summary>
        /// ★★★ v59：命令栏英文标志被「误判成文字编辑态」清掉之后，是否应当**当场抢回**。
        ///
        /// ─────────────── 事故现场（豆包输入法 · 2026-10-14） ───────────────
        ///
        /// 用户症状原话：「AutoCAD 平台下使用豆包输入法，输入命令，当命令只有一个字母的
        /// 时候是正常的，但是当命令是两个及以上字母的时候就会变成中文输入。」
        /// 视频逐帧可见：命令行里是 `T`（已提交）+ `t`（输入法组字中）+
        /// 豆包候选窗「1 他 2 她 3 太 …」，**只有第一个字母是英文字面量**。
        ///
        /// 三条结构性事实叠成这条链路：
        ///
        ///   ① **豆包是纯 TSF 文本服务**（实测注册表：CLSID
        ///      `{9D2B2E2B-…}`、实现 `C:\Windows\System32\tsf-oime.dll`、
        ///      `SubstituteLayout=0x08040804`，**没有任何 IMM32 键盘布局登记**）。
        ///      ⇒ <c>ImmAssociateContext(hwnd, IntPtr.Zero)</c> 那套旁路
        ///        对它**天然无效** —— 能不能保住英文，**完全取决于键盘钩子
        ///        有没有把字母吞掉**（吞掉后 `SendInput(KEYEVENTF_UNICODE)` 注入，
        ///        不经过输入法）。
        ///
        ///   ② 钩子吞不吞，**只由 <c>_commandLineNeedsEnglish</c> 一个布尔量决定**；
        ///      而这个量**唯一**被清 false 的路径是 <c>SwitchToChinese()</c>，
        ///      <c>SwitchToChinese()</c> **唯一**的调用者是
        ///      <c>EnterTextEditMode()</c>。
        ///
        ///   ③ <c>EnterTextEditMode()</c> 的准入判据是 `isEdit && !isCommandLine`，
        ///      它在 AutoCAD 上**结构性脆弱**：
        ///        · `FocusMonitor.IsEditBoxClass`（v59 前）把**所有** `HwndWrapper`
        ///          都算"文字编辑控件"；
        ///        · 而 AutoCAD 的命令栏**整个家族都是 `HwndWrapper`** ——
        ///          命令栏输入控件本体、**「命令自动完成」下拉框**（本机实测：
        ///          敲下第一个字母后立刻 `创建/显示`，类名
        ///          `HwndWrapper[…]`、尺寸 320×68 → 320×214）、信息中心、
        ///          快速访问工具栏搜索框、文档切换条……；
        ///        · 唯一能救它的 `IsCommandLineWindow`（v59 前）只走**父链**找
        ///          `CLI Palette` + 依赖 `Resolve()` 的 **3 秒缓存命中**。
        ///          命令栏本体父链确实带 `CLI Palette`（本机实测
        ///          `HwndWrapper < AfxWnd100u "CLI Palette" < …`），
        ///          但**派生浮层是"带属主的顶层窗口"，`GetParent()` 返回 NULL**
        ///          ⇒ 父链永远找不到；`Resolve()` 又会因为 3 秒缓存而给出
        ///          **过期答案**（缓存里存的是别的窗口）⇒ 判成"编辑框"。
        ///
        ///   ⇒ **完整症状解释**：
        ///      第 1 个字母——标志还是 true，钩子吞键 + 注入 ⇒ 命令名正常出现；
        ///      这个字母让 AutoCAD 弹出**自动完成下拉**（`HwndWrapper` 浮层）并
        ///      产生一次焦点事件 ⇒ 被判成"编辑框" ⇒ `EnterTextEditMode` ⇒
        ///      `SwitchToChinese` ⇒ **标志清空** ⇒
        ///      第 2 个字母起——钩子不再吞键，物理键直达豆包（中文态）⇒ 组字 + 候选窗。
        ///      **单字母命令**敲完一个字母就回车，**永远走不到第二步**，所以"看起来正常"。
        ///
        /// ─────────────── 本判据在做什么 ───────────────
        ///
        /// 不去猜"焦点那个窗口到底是不是编辑框"（那正是出错的地方），
        /// 而是问一个**当场就能回答**的问题：
        /// **「此刻用户可能正在输入文字内容吗？」** —— 证据不足就不认。
        ///
        ///   · 有活动命令（`hasActiveCommand`）→ **绝不插手**。
        ///     命令期间该不该接管由命令白名单 / PassThrough 那一套说话；
        ///     真正的文字类命令（TEXT / MTEXT / DDEDIT…）在 `OnCommandWillStart`
        ///     就已经 `SwitchToChinese` 了，不依赖本判据。
        ///   · 焦点明确在命令栏链上 → **这就是命令栏**，抢回（铁证）。
        ///   · 焦点**根本不在任何编辑控件上**（绘图区 `Afx:…` / 主框架 /
        ///     各种面板）→ 空闲期用户只可能是在敲命令，抢回。
        ///     ★ 本机实测支撑：用户每敲一个字母，焦点都会在
        ///       「命令栏 `HwndWrapper`」⇄「绘图区 `Afx:…:28:…`」之间抖一次。
        ///   · 焦点确实是编辑控件，**且是 WPF 宿主 `HwndWrapper`** → 空闲期
        ///     一律按"命令栏家族"处理，抢回。理由：AutoCAD / 天正 / 协同设计
        ///     里**真正承载文字输入**的控件最终都是**原生** `Edit` /
        ///     `RichEdit`（`WindowsForms10.EDIT…` 亦然），
        ///     纯 WPF 文字对话框在 CAD 生态里极罕见；而 `HwndWrapper` 在 CAD 进程里
        ///     绝大多数是主框架派生出来的面板/浮层。
        ///   · 焦点是**原生编辑控件**（`Edit*` / `RICHEDIT*` / `RichEdit*` /
        ///     `AfxWnd*`）→ 这是真的在编辑文字（典型：天正单行文字双击，
        ///     **不经过命令系统**）→ **不抢**，交还输入法打中文。
        ///
        /// ★ 抽成纯函数的理由：这条契约的失效形式是"偶发地打不出英文/打不出中文"，
        ///   靠注释守不住，必须有可执行断言（见 XimeTest 的 v59 组）。
        /// </summary>
        /// <param name="hasActiveCommand">此刻是否有活动命令（<c>_currentCommand != null</c>）。</param>
        /// <param name="focusIsCommandLine">真实焦点是否属于命令栏链。</param>
        /// <param name="focusIsEditBox">真实焦点是否被 <see cref="FocusMonitor.IsEditBoxClass"/> 判为编辑控件。</param>
        /// <param name="focusIsWpfHost">真实焦点的类名是否为 WPF 宿主（<c>HwndWrapper</c>）。</param>
        internal static bool ShouldReclaimEnglishForIdlePrompt(
            bool hasActiveCommand,
            bool focusIsCommandLine,
            bool focusIsEditBox,
            bool focusIsWpfHost)
        {
            if (hasActiveCommand) return false;   // 命令执行期间：由命令状态说话，绝不插手
            if (focusIsCommandLine) return true;  // 铁证：就在命令栏上
            if (!focusIsEditBox) return true;     // 焦点不在编辑控件上 ⇒ 空闲期只能是在敲命令
            if (focusIsWpfHost) return true;      // WPF 宿主 = CAD 命令栏家族 ⇒ 同上
            return false;                         // 原生 Edit/RichEdit ⇒ 真的在编辑文字，不抢
        }

        #region v6：命令期间「让 CAD 窗口无视输入法」

        /// <summary>命令期间"让 CAD 窗口无视输入法"当前是否生效（诊断用）。</summary>
        public bool ImeBypassActive { get { return _imeBypass.IsActive; } }

        /// <summary>被解除输入法关联的窗口数（诊断用）。</summary>
        public int ImeBypassDetachedCount { get { return _imeBypass.DetachedCount; } }

        /// <summary>
        /// 是否处于"手动强制解除"态（现场 `XIMEBYPASS 1` 之后，直到 `XIMEBYPASS 0`）。
        /// 诊断用：状态行要能说清"当前生效"是自动来的还是人手动按住的。
        /// </summary>
        public bool ManualImeBypass { get { return _manualImeBypass; } }

        /// <summary>执行器本体（诊断 / 自测用）。</summary>
        internal ImeWindowBypass ImeBypass { get { return _imeBypass; } }

        /// <summary>
        /// ★ v6 诊断（`XIMEBYPASS 1`）：**立刻**解除 CAD 窗口与输入法的关联，
        /// 并**按住不放**（进入"手动强制"态）—— 用户要离开命令行回 CAD 里按键试，
        /// 空闲期不能被自动同步逻辑撤掉。`XIMEBYPASS 0` 或引擎停止时解除。
        ///
        /// 返回 0 = 没有任何窗口关联着输入法 ⇒ 这套机制在你这台机器上无从下手。
        /// </summary>
        public int ForceImeBypass(out int visited)
        {
            _manualImeBypass = true;
            try
            {
                return _imeBypass.Engage(_focus.AcadMainWindow, out visited);
            }
            catch (Exception)
            {
                _manualImeBypass = false;
                throw;
            }
        }

        /// <summary>★ v6 诊断（`XIMEBYPASS 0`）：立刻还原（幂等），并**取消手动强制**。</summary>
        public int ReleaseImeBypass()
        {
            _manualImeBypass = false;
            return _imeBypass.Disengage();
        }

        /// <summary>
        /// ★ v6 唯一入口：把"命令期间让 CAD 窗口无视输入法"同步到**当前应有的状态**。
        ///
        /// ================== 它在修什么 ==================
        ///
        ///   天正二级命令只认真实物理按键 ⇒ 必须放行物理键（<c>PassThrough</c>）；
        ///   可放行物理键时**用户自己的输入法也收到了这些键** ⇒ 中文态下弹候选窗。
        ///   本方法在「命令执行期间 + 非文字类命令」时解除 AutoCAD 窗口与输入法上下文的
        ///   关联 ⇒ 物理键依旧直达天正，而输入法收不到键、不开组合、不弹候选窗。
        ///   ★ **输入法自身的状态（中/英、布局、状态栏）一个字节都不动**。
        ///
        /// ================== 为什么必须"单一入口 + 处处调用" ==================
        ///
        ///   这个机制一旦"只开不关"，用户会在 AutoCAD 里**打不出中文**
        ///   （窗口与输入法的关联被解除、还没还原）—— 那是最坏的事故形态，
        ///   而且症状（"输入法坏了"）与原因（"插件没还原"）离得很远，极难排查。
        ///   所以所有状态变化点都必须调它：命令开始 / 结束 / 取消、
        ///   进 / 出文字编辑态、离开 CAD、停引擎、500ms 轮询。
        ///   本方法**幂等**：算出"该开/该关"，与当前状态一致就直接返回。
        /// </summary>
        private void SyncImeBypass(string reason)
        {
            bool want;
            bool manualHonored = false;
            try
            {
                bool needsChinese = _currentCommand != null
                    && _config.MatchChinese(_currentCommand) != XimeConfig.ChineseNeed.None;

                bool auto = ImeWindowBypass.ShouldEngage(
                    _config.BypassImeDuringCommand,
                    _config.WorkMode,
                    _isEngaged,
                    _currentCommand != null,
                    needsChinese,
                    _inTextEditMode,
                    // ★ v6.1：本插件自己的命令（XIMESTATUS / XIMEBYPASS / XIMEPROBE …）
                    //   一样会走 OnCommandWillStart —— 若不排除，诊断命令会**自己把状态改掉**：
                    //   XIMEBYPASS 1 报「枚举 0 个」（进来时已生效、走幂等早退）、
                    //   XIMEBYPASS 0 那句"现在可以正常打中文了"当场失效、
                    //   XIMESTATUS 永远报「当前生效=是」。现场实测踩到，故补此条。
                    ImeWindowBypass.IsOwnPluginCommand(_currentCommand),
                    // ★★★ v62：焦点落在外部文本编辑控件上（图层管理器名称框、对话框输入框…）
                    //   ⇒ 不许解除关联。第 ⑤ 条（_inTextEditMode）依赖焦点事件送达，
                    //   这一条**直接就地读焦点**，堵住"焦点事件没来 / 命令先于焦点事件"
                    //   两种漏网（用户症状：图层名打不进中文）。
                    FocusIsForeignEditBox());

                // ★ v6.1：手动强制（XIMEBYPASS 1）—— 让"回到 CAD 里试着按键"这段空闲期
                //   也保持解除。但硬闸门必须过（文字编辑态 / 开关关闭 / 未接管 ⇒ 一律作废），
                //   否则用户会被卡在**打不出中文**。
                manualHonored = _manualImeBypass && ImeWindowBypass.ShouldHonorManualOverride(
                    _config.BypassImeDuringCommand, _isEngaged, _inTextEditMode);

                want = auto || manualHonored;
            }
            catch (Exception)
            {
                // 判据里用到配置对象（可能正被设置面板改写）—— 出错就按"该还原"处理。
                want = false;
                manualHonored = false;
            }

            if (want == _imeBypass.IsActive) return;   // 幂等：状态已一致，不做事、不记日志

            if (want)
            {
                int visited;
                int detached;
                try
                {
                    detached = _imeBypass.Engage(_focus.AcadMainWindow, out visited);
                }
                catch (Exception ex)
                {
                    Dbg("无视输入法（" + reason + "）失败：" + ex.Message);
                    return;
                }

                Note("命令期间无视输入法：解除 " + detached + " 个窗口的输入法关联"
                    + "（枚举 " + visited + " 个，命令=" + (_currentCommand ?? "-")
                    + "，触发=" + reason + (manualHonored ? "，手动强制" : "") + "）"
                    + (detached == 0 ? "  ★ 0 个 = 这些窗口本来就没有输入法上下文，本机制可能无效" : ""));
            }
            else
            {
                int restored;
                try { restored = _imeBypass.Disengage(); }
                catch (Exception ex)
                {
                    Dbg("还原输入法关联（" + reason + "）失败：" + ex.Message);
                    return;
                }
                if (restored > 0 || _imeBypass.PendingRestoreCount > 0)
                    Note("命令期间无视输入法：还原 " + restored + " 个窗口的输入法关联（触发=" + reason + "）");
            }
        }

        /// <summary>
        /// ★★ v6.2：旁路"**本来该生效、却没生效**"的现场取证（写 `Note`，始终落盘、不依赖
        /// `XIMEDEBUG`）。
        ///
        /// 为什么必须有：这套机制最贵的故障形态就是**静默不生效** —— 用户只看到"候选窗又弹了"，
        /// 而七条判据里究竟是哪一条挡下的，日志里一个字都没有（`Dbg` 只在用户开了调试开关时才写）。
        /// 现场实测正栽在这：第二条 `ry RYBZ` 全程没有"解除"日志，谁也说不清被哪条判据挡的。
        ///
        /// 只在"**本来该生效**却没生效"时记一行，刻意排除那些"**正常的**不生效"：
        ///   · 引擎没在跑（面板点了「卸载」= 用户明确要求插件停下来）
        ///   · 旁路已经生效（无事可报）
        ///   · 没有活动命令（就是敲命令前那一刻）
        ///   · 本插件自己的命令（诊断命令不许改变被诊断的状态）
        ///   · 文字类命令（本该让输入法工作，好让用户打中文）
        ///   · ★ 机制被 `XIMEBYPASS off` 关掉 / 用户选了「切换输入法模式」——
        ///     这两种是**用户自己要求不生效**，天天报就是刷屏（诊断行必须稀有才有人看）。
        /// 剩下的情形（接管中却仍被挡 / 命令开始时恰好在未接管窗口）才是要人看的异常。
        /// </summary>
        /// <returns>
        /// 判据抽成 `internal static`（v6.3）：这样自测工具能用**可执行断言**钉住
        /// "哪些情形不许刷诊断行" —— 这条契约失效的表现是"日志被无意义诊断淹没"，
        /// 靠注释守不住。
        /// </returns>
        internal static bool ShouldDiagnoseBypassSkip(
            bool started,
            bool bypassActive,
            bool hasCommand,
            bool ownCommand,
            bool commandNeedsChinese,
            bool bypassOn,
            bool ignoreImeMode)
        {
            if (!started) return false;              // 引擎没在跑 → 命令事件与我们无关
            if (bypassActive) return false;           // 已经生效，无事可报
            if (!hasCommand) return false;            // 敲命令前那一刻
            if (ownCommand) return false;             // 本插件自己的命令
            if (commandNeedsChinese) return false;    // 文字类命令：本就该让输入法工作
            if (!bypassOn) return false;              // 用户主动关的机制
            if (!ignoreImeMode) return false;         // 退路模式（切换输入法）本就不靠它
            return true;
        }

        private void DiagnoseBypassSkip(string why)
        {
            try
            {
                if (!ShouldDiagnoseBypassSkip(
                        _started,
                        _imeBypass.IsActive,
                        _currentCommand != null,
                        ImeWindowBypass.IsOwnPluginCommand(_currentCommand),
                        _currentCommand != null &&
                            _config.MatchChinese(_currentCommand) != XimeConfig.ChineseNeed.None,
                        _config.BypassImeDuringCommand,
                        _config.WorkMode == XimeWorkMode.IgnoreIme))
                    return;

                Note("[旁路诊断] 命令=" + _currentCommand + "（" + why + "）**未能解除关联**："
                    + "接管=" + _isEngaged
                    + " 启用=" + _config.Enabled
                    + " 机制开关=" + _config.BypassImeDuringCommand
                    + " 工作模式=" + _config.WorkMode
                    + (_manualImeBypass ? " 手动强制=是" : "")
                    + " 文字编辑态=" + _inTextEditMode
                    + " ※ 命令执行期间用户若看到候选窗，就是这一条挡下的");
            }
            catch (Exception) { }
        }

        #endregion

        /// <summary>进入文字编辑态：停止拦截，把输入权交还输入法（可打中文）。</summary>
        /// <param name="hwnd">
        /// 刚刚承接焦点的那个文字编辑控件（<see cref="IntPtr.Zero"/> = 未知）。
        /// 用于识别"换了一个编辑控件"= 一次全新的中文输入场景（见字段注释）。
        /// </param>
        private void EnterTextEditMode(IntPtr hwnd)
        {
            bool alreadyIn = _inTextEditMode;
            bool isNewHost = hwnd != IntPtr.Zero && hwnd != _lastTextEditHost;

            _inTextEditMode = true;
            _editModeIdleTicks = 0;
            if (hwnd != IntPtr.Zero) _lastTextEditHost = hwnd;

            // ★ v6：焦点落进文字编辑框 ⇒ 用户正在打中文内容，
            //   必须**马上**把输入法与窗口的关联还回去（在下面的早退之前！
            //   早退只是"不用重复切输入法"，不代表不用还原关联）。
            SyncImeBypass("进入文字编辑态");

            // ★★★ v46 主修：绝不再用 `if (_inTextEditMode) return;` 把后续进入吞掉。
            //   详见 _lastTextEditHost 的字段注释与 ShouldRunTextEditEntry 的判据说明。
            //   重复执行的代价是被下游自己吸收掉的：切换模式的 ApplySwitchIme 首判
            //   `_switchApplied == target` 直接"无需动作"；无视模式的 DoEnsureChinese
            //   有 EnsureChineseMinGapMs 抑制窗口 —— 都不会真的多发一个 Shift。
            if (!ShouldRunTextEditEntry(alreadyIn, isNewHost)) return;

            // ★ v40：此刻焦点**已经**在编辑控件上（本方法就是被焦点事件调起来的）
            //   → 不必排队等待，立刻切，尽量抢在用户敲下第一个字之前。
            // ★ v41：但焦点事件到达的**那一瞬间**焦点可能还在抖动（命令栏 ⇄ 编辑框），
            //   所以靶点必须限定"焦点所在的编辑控件"并要求短轮询 —— 见 WaitForTextEditHost。
            SwitchToChinese(true);
            if (alreadyIn)
                Dbg("重新进入文字编辑态（换到新的编辑控件 0x" + hwnd.ToInt64().ToString("X")
                    + "）→ 这是一次全新的中文输入场景，重新确保中文");
            else
                Dbg("进入文字编辑态（焦点在编辑框）→ 允许中文");

            // ★ 关键修复：输入法可能已被用户手动切到英文（他上一段标注打的是英文），
            //   此时只"停止拦截"是不够的 —— 必须主动切回中文，否则用户双击文字后
            //   怎么打都是英文（用户实测反馈的 bug）。
            EnsureChineseForTextInput("进入文字编辑态", true, true);
        }

        /// <summary>退出文字编辑态并恢复英文接管。</summary>
        private void ExitTextEditMode(string reason)
        {
            // ★ v46：先清"当前编辑控件"，**再**判 _inTextEditMode —— 后者为 false 时
            //   也要把靶点归零，否则下一次双击**同一个**控件会被误判成"同控件抖动"
            //   而被跳过（AutoCAD / 天正 可能复用同一个编辑 HWND）。
            _lastTextEditHost = IntPtr.Zero;
            if (!_inTextEditMode) return;
            _inTextEditMode = false;
            _editModeIdleTicks = 0;

            // ★ v6：出了编辑态、命令若仍在执行 → 需要重新"无视输入法"
            //   （判据会自动判断，这里只需同步一次）。
            SyncImeBypass("退出文字编辑态");
            // ★ v40：焦点**已经**离开编辑框（调用方做过实时校验）→ 立刻切英文，
            //   否则用户"退出编辑、马上敲命令"时，命令名的第一个字母会被中文输入法吃掉。
            ForceEnglish(true);
            Dbg("退出文字编辑态（" + reason + "）→ 恢复英文接管");
        }

        #region 用户手动接管识别（单按 Shift）

        /// <summary>用户最近是否手动切换过输入法（默认 60 秒内有效）。</summary>
        private bool UserRecentlyToggledIme()
        {
            int t = _userImeToggleTick;
            if (t == 0) return false;
            int age = unchecked(Environment.TickCount - t);
            return age >= 0 && age < UserToggleValidMs;
        }

        /// <summary>
        /// Shift 键按下 / 抬起通知（由键盘钩子回调，且只针对**用户真实按键** ——
        /// 插件自己为切换输入法发出的 TapShift 带注入标记，已在钩子里被过滤）。
        ///
        /// 单按一下 Shift 是中文输入法最通用的中/英切换热键。识别出来后认为
        /// 「输入法已由用户接管」：插件在他打这一段文字的期间完全不干预，
        /// 只在下一段文字输入的**入口**才可能重新切回中文。
        /// </summary>
        private void OnShiftChanged(bool isDown)
        {
            if (isDown)
            {
                _shiftDownTick = Environment.TickCount;
                _shiftAlone = true;
                return;
            }

            // ★ 必须先取出并复位：钩子的抬起回调之后可能紧跟着新的按下
            bool alone = _shiftAlone;
            _shiftAlone = false;
            if (!alone) return;

            // 时限只用于排除"按住 Shift 很久后松开"这类非切换动作。
            // 阈值放宽到 1.5 秒：输入法自身也是「Shift 抬起且期间无其它键」才切换，
            // 阈值太紧会把"按得稍慢的单按"漏掉，导致插件又把英文切回中文。
            int held = unchecked(Environment.TickCount - _shiftDownTick);
            if (held < 0 || held > 1500) return;

            _userImeToggleTick = Environment.TickCount;
            Dbg("检测到用户单按 Shift → 输入法交由用户接管（本段输入不自动切回）");

            // ★ 切换输入法模式下还要多做一件事：作废"我们自己确信的输入法状态"。
            //   本模式不像无视模式那样"从不碰输入法"，它是靠维护一份可信记录
            //   （_switchApplied）来避免重复切换的 —— 用户手动切过之后，
            //   这份记录就不再代表现实，必须作废，否则会一直判定"无需动作"。
            // ★ v58：恢复"只有切换模式才可能真的切过输入法"这一前提 ——
            //   无视模式从不碰输入法，它的记录也就不会因用户手动切换而过期。
            if (ShouldAutoSwitchIme(_engagedMode))
                InvalidateAppliedState("用户手动按 Shift 切换了中英");
        }

        /// <summary>
        /// 记录"用户按了 Shift 以外的键"，把「单按 Shift 切换中英」与
        /// 「Shift + 字母 打大写」区分开。
        ///
        /// 钩子的按键回调只在**按下**时上报，因此这里只需处理按下。
        /// </summary>
        private void TrackUserGesture(int vkCode, bool isDown)
        {
            if (!isDown) return;

            // ★ 另一种常见的中英切换手势：**Ctrl + 空格**（微软拼音、小狼毫等都支持）。
            //   它和单按 Shift 一样会改变输入法的中英状态，但**不会**触发钩子的
            //   Shift 通知 —— 不识别它，"我们自己确信的状态"（_switchApplied）
            //   就会在用户用 Ctrl+空格 切过之后过期，表现为「切换模式不跟手」。
            //   这里只做识别，不拦截：按键照旧原样交给系统。
            if (vkCode == NativeMethods.VK_SPACE && NativeMethods.IsCtrlPressed())
            {
                // Shift 按住期间敲的键同样不算"单按 Shift"
                _shiftAlone = false;
                if (ShouldAutoSwitchIme(_engagedMode))
                    InvalidateAppliedState("用户按了 Ctrl+空格 切换中英");
                return;
            }

            if (vkCode == NativeMethods.VK_LSHIFT ||
                vkCode == NativeMethods.VK_RSHIFT ||
                vkCode == NativeMethods.VK_SHIFT)
                return;

            // Shift 按住期间按了别的键 → 这不是"单按切换"
            _shiftAlone = false;
        }

        /// <summary>
        /// 请求"确保输入法处于中文态"（用于"马上要输入中文内容"的入口时刻）。
        ///
        /// ★ 仅服务于「无视输入法模式」：那条通路平时不碰输入法，于是留下一个
        ///   副作用 —— 用户一旦自己按 Shift 切成英文，插件不会替他切回来，
        ///   之后双击文字 / 执行 TEXT 类命令时就一直打不出中文。
        ///   这里在"即将输入中文"的入口处补救一次。
        ///
        ///   「切换输入法模式」不走这条路：它有自己的 <see cref="RequestSwitchIme"/>，
        ///   而且是**无条件**把状态设成中文（不依赖"用户最近手动切过"这个标记）。
        ///
        /// 本方法只负责排队：延迟一小段时间（等键盘焦点从命令栏稳定到编辑框）后
        /// 交给 <see cref="DoEnsureChinese"/> 干活。真正的动作在线程池里执行，
        /// 绝不阻塞钩子回调或 UI 线程。
        /// </summary>
        private void EnsureChineseForTextInput(string reason)
        {
            EnsureChineseForTextInput(reason, false, false);
        }

        /// <param name="urgent">
        /// 焦点**已经**在编辑控件上（EnterTextEditMode 直接触发）→ true：不再等 120ms。
        /// ★ v40：这段等待在"焦点已稳定"时纯属白等 —— 现场日志里从"进入文字编辑态"
        ///   到判定落地要 ~130ms，用户敲的字早就进去了（而且是从**旧的**输入法状态
        ///   接走的），正是"不跟手"的组成部分。
        ///   命令刚触发的路径（OnCommandWillStart 的"文字类命令 xxx"）必须保留等待。
        /// </param>
        /// <param name="requireEditHost">
        /// 靶点**必须**是焦点所在的编辑控件（v41）。"进入文字编辑态"传 true ——
        /// ★★ 理由见 <see cref="WaitForTextEditHost"/>：命令栏控件与编辑框的 IME 读数
        ///   可以不同，采信前者会让"确保中文"误判成"已是中文"。
        /// </param>
        private void EnsureChineseForTextInput(string reason, bool urgent, bool requireEditHost)
        {
            if (!_isEngaged || !_config.Enabled) return;
            if (_config.WorkMode != XimeWorkMode.IgnoreIme) return;

            bool userToggled = UserRecentlyToggledIme();

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // 等焦点稳定：命令刚触发时键盘焦点正从命令栏迁往编辑框，
                    // 抢在迁移中途操作容易落到过渡窗口上（实测表现为"切了却没生效"）。
                    // ★ 焦点已在编辑框（urgent）时不需要等，见上面的参数说明。
                    //   requireEditHost 时改由 WaitForTextEditHost 做**针对性**的短轮询
                    //   （一旦拿到编辑框就立刻走，比固定睡 120ms 又快又准）。
                    if (!urgent && !requireEditHost) System.Threading.Thread.Sleep(120);
                    DoEnsureChinese(reason, userToggled, requireEditHost);
                }
                catch (Exception ex)
                {
                    Dbg("确保中文（" + reason + "）异常：" + ex.Message);
                }
            });
        }

        /// <summary>等"焦点所在的编辑控件"时最多轮询几次、每次间隔多少毫秒（v41）。</summary>
        private const int EditHostWaitTries = 10;
        private const int EditHostWaitStepMs = 15;

        /// <summary>
        /// 该靶点能否用于「确保中文」的判定（v41 纯判据，自测钉住）。
        ///
        /// ★★ 命令栏控件（`isCommandLine = true`）**不算**：
        ///   AutoCAD 的 IME 上下文挂在**具体子控件**上，命令栏控件与文字编辑框的
        ///   中英读数**可以不同、甚至相反**。而"确保中文"要保证的是**编辑框**里
        ///   能打出中文 —— 拿命令栏的「中文」当依据，编辑框其实是英文时就漏判了。
        /// </summary>
        internal static bool IsUsableEnsureChineseTarget(bool readingTrusted,
                                                         bool isTextInputHost,
                                                         bool isCommandLine)
        {
            if (!readingTrusted) return false;
            if (!isTextInputHost) return false;
            return !isCommandLine;
        }

        /// <summary>
        /// 等"焦点所在的**编辑控件**"出现，最多 <see cref="EditHostWaitTries"/> 次、
        /// 每次间隔 <see cref="EditHostWaitStepMs"/> 毫秒（v41 新增）。
        ///
        /// ★★★ 为什么必须等、且必须**排除命令栏控件** —— 这是"有一定概率双击修改
        ///   切不成中文"的根治点：
        ///
        ///   1) AutoCAD 的 IME 上下文挂在**具体子控件**上，命令栏控件与文字编辑框的
        ///      中英读数**可以不同**（本文件 ResolveAcadImeTarget 的注释里就有现场实测：
        ///      CAD 主窗口 English / 设置面板 Chinese）。
        ///   2) 双击文字时焦点从命令栏迁到编辑框需要几~几十 ms，而 v40 把"确保中文"
        ///      的排队延迟从 120ms 砍到了 0 —— 于是它常常**抢在迁移完成前**执行，
        ///      `GetRealFocusWindow()` 读到的还不是编辑框 → 第①条落空 →
        ///      退回第②条拿到**命令栏控件**（类名是强特征，`trustworthy = true`）→
        ///      采信它的「中文」→ 判"已是中文，无需动作"。
        ///   现场日志（v39 实机，20:21:58 —— 本条的铁证）：
        ///     20:21:58.648  焦点变化: 0x7A0E2E 类名=Edit 是编辑框=True    ← 焦点已在编辑框
        ///     20:21:58.648  进入文字编辑态（焦点在编辑框）→ 允许中文
        ///     20:21:58.649  确保中文（进入文字编辑态）：当前模式=Chinese 窗口=0x400F58  ← 却是命令栏控件
        ///     20:21:58.649  焦点变化: 0x4812A2 类名=AfxMDIFrame100u        ← 焦点正在快速抖动
        ///   焦点迁移时快时慢，所以用户看到的是"**有一定概率**双击修改切不成中文"。
        ///
        ///   现在的做法：短轮询等编辑控件 —— 拿到就**立刻**判定（比固定睡 120ms
        ///   又快又准）；全程拿不到就返回 Zero 且 `trusted = false`，由调用方按
        ///   "读不出来"处理（只有"用户刚手动切过"才允许盲切）。
        ///   **绝不采信命令栏控件的中文读数** —— "猜错了比不动作更糟"。
        /// </summary>
        private IntPtr WaitForTextEditHost(out bool trusted, string reason)
        {
            trusted = false;
            for (int i = 0; i < EditHostWaitTries; i++)
            {
                bool t;
                var h = ResolveAcadImeTarget(out t);
                if (h != IntPtr.Zero
                    && IsUsableEnsureChineseTarget(t, HostTextFrame.IsTextInputHost(h),
                                                      HostTextFrame.IsCommandLineWindow(h)))
                {
                    trusted = true;
                    if (i > 0)
                        Dbg("确保中文（" + reason + "）：等了 " + (i * EditHostWaitStepMs)
                            + "ms 才等到焦点所在的编辑控件（窗口=0x" + h.ToInt64().ToString("X") + "）");
                    return h;
                }
                System.Threading.Thread.Sleep(EditHostWaitStepMs);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 真正执行"确保中文"（运行在线程池线程）。
        ///
        /// 决策顺序（判据集中在 <see cref="ShouldEnsureChineseTapShift"/>，自测可钉住）：
        ///   ① **可信地**读到已是中文 → 不动作
        ///   ② 可信地读到英文 → 发一次真实 Shift
        ///   ③ 读不出来（无可信靶点 / 两条通道都没读数）→ 只有"用户刚手动切过"才允许盲切
        ///      ★ "没有 IME 上下文的窗口读回中文"**不算**读到中文（v38）——
        ///        那只是兼容层默认值；照它不动作，用户双击文字后就打不出中文。
        ///
        /// ★★ 为什么是"一次、且不校验"：
        ///
        ///   现代输入法（微软拼音、搜狗、微信等）普遍走 TSF，IMM32 的
        ///   ImmGetConversionStatus **读不到**它们的真实中英状态变化。
        ///   早期实现用"探测结果没变就再按一次 Shift"的循环，实测后果是：
        ///   第一次 Shift 已经把输入法切到中文，只因探测不到便又按一次 —— 又切回英文。
        ///   用户看到的现象正是"切了没用，还是英文"，日志则记成"切换失败"。
        ///
        ///   模拟一次 Shift 与用户亲手按 Shift **完全等价**，输入法必定响应，
        ///   因此一次动作即确定结果：不需要事后校验，更不能重复。
        /// </summary>
        private void DoEnsureChinese(string reason, bool userToggled, bool requireEditHost)
        {
            // ★ 执行前复核模式：本方法由 EnsureChineseForTextInput 排队、延迟 120ms
            //   才执行，这期间用户可能已切到「切换输入法模式」。若照旧执行，
            //   这个残留任务会真的 TapShift 切中文，干扰切换模式的英文保持。
            if (_config.WorkMode != XimeWorkMode.IgnoreIme)
            {
                Dbg("确保中文（" + reason + "）：已切离无视输入法模式 → 放弃");
                return;
            }

            // 同一时刻只允许一个动作（两个入口可能并发请求）
            if (System.Threading.Interlocked.Exchange(ref _imeSwitchBusy, 1) == 1)
            {
                Dbg("确保中文（" + reason + "）：另一处正在切换 → 跳过");
                return;
            }

            try
            {
                // 抑制窗口：刚切过就不再切，避免"两下 Shift 又切回英文"
                int gap = unchecked(Environment.TickCount - _lastEnsureChineseTick);
                if (_lastEnsureChineseTick != 0 && gap >= 0 && gap < EnsureChineseMinGapMs)
                {
                    Dbg("确保中文（" + reason + "）：" + gap + "ms 前刚切换过 → 跳过");
                    return;
                }

                // ★★ 探测窗口必须与「切换输入法模式」那三处采**同一套靶点解析**（v38）。
                //
                //   旧实现用的是 `GetRealFocusWindow()` —— 那是一个**裸焦点**。
                //   在 AutoCAD 2014 上双击文字进在位编辑时，它经常就是**绘图区**
                //   (`Afx:00007FF...:28:...`)。绘图区没有 IME 上下文，`GetMode` 给回的是
                //   兼容层**默认值**（实测恒为「中文」）—— 于是每次都判"已是中文，无需动作"，
                //   而输入法其实还停在英文：双击文字后怎么打都打不出中文
                //   （用户反馈的"双击修改，输入法模式就有可能失效"）。
                //   更糟的是旧代码在 ① 分支里顺手把 `_userImeToggleTick` 清掉了 ——
                //   连"用户刚手动切过、所以必须切回中文"这条唯一的补救线索也一起毁掉。
                //   现场日志（v36）：
                //     确保中文（进入文字编辑态）：当前模式=Chinese 用户手动切过=False 窗口=0x6A05E0
                //     确保中文（进入文字编辑态）：已是中文，无需动作
                //   而 0x6A05E0 的类名正是 `Afx:00007FF642700000:28:...`（绘图区）。
                //
                //   现在只认「CAD 自己窗口里真正承接受输入」的控件；拿不到就按
                //   "读不出来"处理，**绝不采信**一个没有 IME 上下文的窗口给出的「中文」。
                bool readingTrusted;
                IntPtr hwnd;
                if (requireEditHost)
                {
                    // ★★★ v41：只认「焦点所在的编辑控件」，等不到就按"读不出来"处理。
                    //   绝对**不允许**退回到命令栏控件 —— 详见 WaitForTextEditHost。
                    hwnd = WaitForTextEditHost(out readingTrusted, reason);
                    if (hwnd == IntPtr.Zero)
                    {
                        // ★★★ v42：**不再直接放弃**（这是"有点概率双击修改打不出中文"的
                        //   直接成因——实机日志 v41 20:31:38：焦点被 CAD 主框架抢走 880ms，
                        //   而这里只等 150ms，等不到就"保持现状"，一个 Shift 都不发）。
                        //   登记待补，由焦点事件 / 500ms 轮询在编辑控件真正到位时补做。
                        if (_inTextEditMode) RegisterPendingEnsure(reason);
                        else Dbg("确保中文（" + reason + "）：等不到编辑控件，且编辑态已结束 → 放弃");
                        return;
                    }
                }
                else
                {
                    hwnd = ResolveAcadImeTarget(out readingTrusted);
                    if (hwnd == IntPtr.Zero)
                    {
                        Dbg("确保中文（" + reason + "）：拿不到属于 AutoCAD 的靶点窗口 → 放弃");
                        return;
                    }
                }

                ApplyEnsureChinese(reason, userToggled, hwnd, readingTrusted);
            }
            catch (Exception ex)
            {
                Dbg("确保中文（" + reason + "）异常：" + ex.Message);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _imeSwitchBusy, 0);
            }
        }

        /// <summary>
        /// 判定 + 动作（不含排队与门闩）—— 由 <see cref="DoEnsureChinese"/> 与
        /// <see cref="TryFlushPendingEnsure"/> 共用，避免两处各写一份判据日后漂移。
        /// </summary>
        private void ApplyEnsureChinese(string reason, bool userToggled, IntPtr hwnd, bool readingTrusted)
        {
            // ★★★ v46：先记下"这个靶点本身能不能承接 IME 上下文"。
            //   下面的"证据降级"会把 readingTrusted 改成 false（含义变成"读数不可信"），
            //   但"这个控件还能不能回读"这件事没变 —— 盲切后的校验要靠它。
            bool probeCapable = readingTrusted && hwnd != IntPtr.Zero;

            var actual = readingTrusted && hwnd != IntPtr.Zero
                         ? _ime.GetModeOnWindow(hwnd)
                         : ImeMode.Unknown;

            // ★ 靶点有 IME 上下文却读不出值 → 说明两条 IMM32 通道都失效，读数不可信。
            if (readingTrusted && actual == ImeMode.Unknown)
                readingTrusted = false;

            // ★★★ v42：自己的记录优先于探测（与切换模式的铁律完全同源）。
            //
            //   实机日志（v41，20:32:05~20:32:26）连续 5 次双击修改，靶点都是**真实的
            //   Edit 控件**（0x6D24DC / 0x5D2BF0 / 0x4E119C / 0x4D10EC / 0x610F78），
            //   读回的都是「Chinese」→ 判"已是中文，无需动作"→ 一个 Shift 都不发，
            //   而用户**打不出中文**。可信与不可信在这里已经分不出来了。
            //
            //   而此刻唯一确定的信息是：**我们刚从切换模式切过来，切换模式整段期间
            //   是把输入法按在英文上的**。既然存在这条硬证据，"读回中文"就与它矛盾 ——
            //   按铁律 4（自己的动作结果优先于探测），采信自己的记录，不予采信该读数。
            bool leftEnglish = _imeLeftEnglish;
            if (leftEnglish && readingTrusted && actual == ImeMode.Chinese)
            {
                // ★ v44：把"这条证据什么时候、因为什么记下的"一并打出来 ——
                //   v42/v43 的故障正是"证据比事实活得久"，而日志里只看到它被使用、
                //   看不到它从哪来，排查时无法判断它该不该还活着。留痕是必须的。
                int age = unchecked(Environment.TickCount - _imeLeftEnglishTick);
                Dbg("确保中文（" + reason + "）：靶点 0x" + hwnd.ToInt64().ToString("X")
                    + " 读回 Chinese，但本次接管**明确把输入法切成了英文**且没有可靠依据"
                    + "证明它已回到中文 → 判该读数不可信，不予采信"
                    + "（证据来自「" + (_imeLeftEnglishWhy ?? "?") + "」，已存活 " + age + "ms"
                    + "；★ 若这条证据本该在「还原成功」时被撤销，请查日志里有没有"
                    + "「v44 撤销证据」行）");
                readingTrusted = false;
                actual = ImeMode.Unknown;
            }

            bool mayBeEnglish = userToggled || leftEnglish;

            Dbg("确保中文（" + reason + "）：当前模式=" + actual
                + (readingTrusted ? "" : "（★ 读数不可信，按「读不出来」处理）")
                + " 用户手动切过=" + userToggled
                + " 我们留下过英文=" + leftEnglish
                + " 窗口=0x" + hwnd.ToInt64().ToString("X"));

            if (!ShouldEnsureChineseTapShift(actual, readingTrusted, mayBeEnglish))
            {
                if (readingTrusted && actual == ImeMode.Chinese)
                {
                    // ① 可信地读到"已经是中文"：
                    //    · 用户的手动切换标记完成使命；
                    //    · "我们留下过英文"这条证据也确实过期了（有人把它切回来了）。
                    _userImeToggleTick = 0;
                    _imeLeftEnglish = false;
                    Dbg("确保中文（" + reason + "）：已是中文，无需动作");
                }
                else
                {
                    // ② 状态读不出来、且**没有任何**"可能是英文"的正面证据 →
                    //    不敢盲切（盲切有 50% 概率把本来正确的中文切成英文）。
                    Dbg("确保中文（" + reason + "）：状态读不出来、且无任何「可能是英文」的"
                        + "正面证据 → 保持现状（不赌方向）");
                }
                return;
            }

            // ③ 切回中文：只发一次真实 Shift（见 DoEnsureChinese 的方法注释）
            _lastEnsureChineseTick = Environment.TickCount;
            bool ok = CharInjector.TapShift();
            _userImeToggleTick = 0;
            _imeLeftEnglish = false;      // 这条证据已被消费，不再重复据它动作
            _imeLeftEnglishWhy = null;
            _imeLeftEnglishTick = 0;
            Dbg("确保中文（" + reason + "）：已发送一次 Shift 切换（结果=" + ok
                + "；依据=" + (userToggled ? "用户刚手动切过中英" : "本次接管把输入法留在了英文")
                + "）");
            // ★ v45：这条是"输入法真的被切回中文了吗"的唯一正面证据
            //   （v41 及以前整份日志恒为 0 次）→ 必须进始终记录的日志。
            Note("确保中文（" + reason + "）：已发送一次 Shift 切换（结果=" + ok
                + "；依据=" + (userToggled ? "用户刚手动切过中英" : "本次接管把输入法留在了英文")
                + "）");

            // ★★★ v46：盲切之后必须回头看一眼（详见方法注释）。
            VerifyBlindShiftLanded(reason, hwnd, probeCapable, ok);
        }

        /// <summary>盲切后回读的等待时间（毫秒）——给输入法一点响应时间。</summary>
        private const int AfterBlindShiftProbeMs = 120;

        /// <summary>
        /// 盲切之后回读校验（v46 新增）。
        ///
        /// ★ 为什么必须校验：<see cref="CharInjector.TapShift"/> 的返回值只代表
        ///   **SendInput 把事件塞进了系统输入队列**（`SendInput(...) == 1`），
        ///   **不代表输入法真的响应了**。以下情况都会让那一下"发出去但没生效"：
        ///     · 输入法的候选窗正开着（Shift 有时被候选窗吃掉）；
        ///     · 目标控件此刻并没有打开 IME 上下文；
        ///     · 焦点在 down/up 之间被抢走（本方法两次 SendInput 之间固定 Sleep(40)）。
        ///   而"我们盲切了一下"这件事一旦为假，parity 就反了 —— 又因为铁律 4
        ///   （自己的动作结果优先于探测），这个错误会**一直保留下去**，
        ///   用户看到的就是「切了没用，还得再来一次」。
        ///
        /// ★ 验证用的是**一条天然不对称**的判据（这是本方法能成立的关键）：
        ///   · 兼容层对"没有 IME 上下文的窗口"返回的**默认值恒为「中文」**
        ///     （见 ResolveAcadImeTarget / 本文件的历次实测记录）——
        ///     所以**「English」读数不可能是默认值**，读到英文必为真；
        ///   · 「Chinese」读数则可能是默认值/滞后值，**不足为凭**。
        ///   因此：盲切后若靶点**可信地**读到 English → 说明那一下确实没生效 → 补发一次。
        ///   读到 Chinese/读不出来 → 不动手（宁可留下"可能还是英文"，也不能把
        ///   刚切好的中文反手翻成英文）。
        /// </summary>
        private void VerifyBlindShiftLanded(string reason, IntPtr hwnd, bool probeCapable, bool sent)
        {
            if (!sent || !probeCapable) return;
            try
            {
                var v = ReadBackVerdict(ImeMode.Chinese, hwnd, probeCapable, reason);
                if (v != BlindShiftVerdict.Failed) return;

                bool ok2 = CharInjector.TapShift();
                // ★ 这条是"我们刚才那一下没生效"的唯一正面证据 → 始终记录
                Note("确保中文（" + reason + "）：盲切后回读仍是 English（那一下没生效）→ "
                    + "补发一次 Shift（结果=" + ok2 + "）");
            }
            catch (Exception ex)
            {
                Dbg("确保中文（" + reason + "）：盲切后回读异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 回读一次并按 <see cref="DecideBlindShiftVerdict"/> 判定（v47）。
        /// **不发送任何按键** —— 只回答"刚才那一下到底生效了没有"。
        /// </summary>
        private BlindShiftVerdict ReadBackVerdict(
            ImeMode target, IntPtr hwnd, bool probeCapable, string tag)
        {
            System.Threading.Thread.Sleep(AfterBlindShiftProbeMs);
            var after = probeCapable ? _ime.GetModeOnWindow(hwnd) : ImeMode.Unknown;
            var v = DecideBlindShiftVerdict(target, after, probeCapable);
            Dbg("切换回读校验（" + tag + "）：目标=" + target + " 回读=" + after
                + " → 判定=" + v
                + "【靶点 " + DescribeWindow(hwnd) + "】"
                + "（★ English 读数天然可信；Chinese 读数可能是兼容层默认值，不作反证）");

            // ★ v48：累计通道健康度（纯诊断），够样本后由 IsReadbackChannelUseless 判失效。
            TrackReadbackHealth(after, v);
            return v;
        }

        /// <summary>
        /// v48：累计回读通道健康度，判定"这条通道读不出真实态"时**始终记录**一次警告。
        ///
        /// ★ 严格**只读不写决策** —— 不动 <see cref="_switchApplied"/>、
        ///   不动待补状态、不发任何按键。它的唯一副作用是日志。
        ///
        /// ★ 为什么用 Note 而不是 Dbg：这正是 v44 的教训 ——
        ///   关键证据不能依赖用户记得开 XIMEDEBUG。通道失效是"整个校验失效"，
        ///   必须无条件出现在启动日志里。
        ///
        /// ★ 只报一次（<see cref="_readbackChannelWarned"/>）：这类警告的价值在
        ///   "告诉你根本前提不成立"，报第二次不会有新信息，只会淹没别的行。
        /// </summary>
        private void TrackReadbackHealth(ImeMode afterRead, BlindShiftVerdict v)
        {
            _readbackSamples++;
            if (v == BlindShiftVerdict.Inconclusive) _readbackInconclusive++;

            int bit = afterRead == ImeMode.English ? 2
                    : afterRead == ImeMode.Chinese ? 4
                    : 1;   // Unknown
            _readbackDistinctMask |= bit;

            int distinct = 0;
            for (int i = 0; i < 3; i++) if ((_readbackDistinctMask & (1 << i)) != 0) distinct++;

            if (_readbackChannelWarned) return;
            if (!IsReadbackChannelUseless(
                    _readbackSamples, _readbackInconclusive, distinct, ReadbackChannelMinSamples))
                return;

            _readbackChannelWarned = true;
            Note("★★★ 回读通道疑似失效（v48）：已回读 " + _readbackSamples + " 次，"
                + "其中 Inconclusive " + _readbackInconclusive + " 次，"
                + "读数始终是同一个值（英语读数出现 0 次）"
                + " → 本输入法在 IMM32 下**读不出真实中英态**"
                + "（小狼毫/新版微软拼音等 TSF 输入法的典型表现）。"
                + "后果：v47 的盲切回读校验在这台机器上**无法判定成败**"
                + "（English 读数天然可信这条基石不成立 → Failed 永不触发），"
                + "切换只能靠「猜」，这就是「有一定概率切换逻辑不对」的根本原因。"
                + "★ 根治需要改走 TSF 通道"
                + "（ITfCompartmentMgr + GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION），"
                + "本版**暂未实现**。在此之前，建议改用「无视输入法模式」（它不依赖任何读数）。");

            // ★★★ v61：这条警告以前只是"告诉你别指望读数"，现在它**同时触发兜底** ——
            //   既然"切没切成"根本不知道，就不该再让"命令栏必须是英文"押在切换上。
            //   只有当当前模式**确实依赖切换**时才提示，避免在无视模式下刷一条无意义的日志。
            if (_config.WorkMode == XimeWorkMode.SwitchIme
                && ShouldTakeOverKeys(_config.WorkMode, switchChannelUnusable: true))
            {
                Note("★★★ 兜底已启用（v61）：当前是「切换输入法模式」，而本机输入法的中英态"
                    + "通道不可信 → 命令栏的英文改由**键盘钩子直接注入**"
                    + "（不经过输入法，与输入法状态无关，确定性成立）。"
                    + "输入法状态的切换照旧尽力而为；"
                    + "进入文字编辑 / 文字类命令时仍会把输入权交还输入法。");
            }
        }

        /// <summary>
        /// ★★★ v47 主修：**切换真实 Shift 之后的回读校验与自愈**（运行在**锁外**）。
        ///
        /// 为什么必须放在锁外：校验要等 <see cref="AfterBlindShiftProbeMs"/> 毫秒，
        /// 放在锁内会让每一次切换都多背 120ms —— 那正是 v40 好不容易砍掉的"不跟手"。
        ///
        /// 三道护栏（每一条都必要 —— 这 120ms 里世界完全可能变了）：
        ///   ① 用户已经要了别的状态（<see cref="IsSupersededRequest"/>）→ 一个键都不发；
        ///   ② 状态记录已经被改（别的切换完成 / 用户手动按了 Shift）→ 一个键都不发；
        ///   ③ 此刻发不出真实按键（前台不是 AutoCAD）→ 一个键都不发。
        ///
        /// 判定为 <see cref="BlindShiftVerdict.Failed"/>（想要中文却**可信地**读到英文）
        /// 时的处理：
        ///   第 1 次 → 补发一次 Shift（真实按键，唯一有效手段）；
        ///   第 2 次 → **把状态记录作废**。这是本方法最重要的一条：
        ///     带着一条假记录继续跑，后续每一次判断都会做反，而且日志上全是"成功"。
        ///     宁可回到"不知道"（下次重新探测、必要时登记待补），也绝不带假记录做反。
        /// </summary>
        private void VerifySwitchAfterUnlock(ImeMode target, IntPtr hwnd, string reason)
        {
            if (hwnd == IntPtr.Zero) return;

            // 护栏①②③ —— 顺序无关，任一不成立就立刻退出，一个键都不发
            if (IsSupersededRequest(_switchWanted, target))
            {
                Dbg("切换回读校验（" + reason + "）：已被更新的请求取代 → 放弃校验");
                return;
            }
            if (_switchApplied != target)
            {
                Dbg("切换回读校验（" + reason + "）：状态记录已变（=" + _switchApplied
                    + "）→ 放弃校验");
                return;
            }

            var v = ReadBackVerdict(target, hwnd, true, reason);

            if (v == BlindShiftVerdict.Confirmed)
            {
                Dbg("切换回读校验（" + reason + "）：✓ 确认已达成 " + target);
                return;
            }
            if (v == BlindShiftVerdict.Inconclusive)
            {
                // 读到 Chinese（可能是默认值）或读不出来 —— 不下结论，保持原判
                return;
            }

            // ---- Failed：想要中文，靶点却**可信地**读到英文 ⇒ 那一下确实没生效 ----
            //      发不出真实按键时**绝不能**在这里硬来（会去改别的程序的输入法），
            //      但仍要把假记录作废 —— 那是唯一马上能做的正确事。
            if (!CanSendRealKey())
            {
                ClearAppliedState();
                Note("切换输入法（" + reason + "）：这一下没生效（回读仍是 English），"
                    + "但此刻发不出真实按键 → **状态记录作废**、登记待补（焦点回到 AutoCAD 后补做）");
                MarkPendingSwitch(target, lackRealKey: true);
                return;
            }

            bool ok2 = CharInjector.TapShift();
            // ★ 这是"盲切会落空"的第一个直接证据 —— 必须始终记录，
            //   否则下一次排查又会像 v46 那样看到"58 次全成功"却查不出问题。
            Note("切换输入法（" + reason + "）：★ 盲切未生效（回读仍是 English，"
                + "窗口=0x" + hwnd.ToInt64().ToString("X") + "）→ 补发一次 Shift（结果="
                + ok2 + "）");

            if (ok2)
            {
                // 补发后**再回读一次** —— 只有仍然读到英文才作废记录。
                //   （作废记录是重手：它会让下一次切换重新走"探测 + 可能盲切"，
                //     代价是多一次 Shift。所以必须确认到最后一刻才用。）
                var v2 = ReadBackVerdict(target, hwnd, true, reason);
                if (v2 != BlindShiftVerdict.Failed)
                {
                    Dbg("切换回读校验（" + reason + "）：补发后回读=" + v2
                        + " → 不再判该次切换失败");
                    return;
                }
            }

            // 补发之后仍然读回英文 → 记录作废，自愈
            ClearAppliedState();
            Note("切换输入法（" + reason + "）：★ 补发后回读仍是 English → "
                + "**状态记录作废**（回到「不知道」，下次重新判定；"
                + "绝不带着一条假记录继续做反）→ 登记待补");
            MarkPendingSwitch(target, lackRealKey: !CanSendRealKey());
        }

        /// <summary>
        /// ★★★ v47：**"还原"这一下的回读确认**（运行在锁外）。
        ///
        /// 与 <see cref="VerifySwitchAfterUnlock"/> 的区别 —— 两个刻意的不对称：
        ///   1) **不复用 `_switchApplied` 护栏**。还原在 `FinishRestore` 之后就结束了，
        ///      记录可能已被清掉；这里要判的是"我刚刚那一下有没有落地"，
        ///      依据是**靶点读数**（English 天然可信），与记录无关。
        ///   2) **不查 `IsSupersededRequest`**。还原是"退出接管"的收尾动作，
        ///      此刻 `_switchWanted` 往往还停在某个陈旧值上 —— 拿它当护栏会
        ///      把**正当的还原校验**误判成过期。方向由 `target` 唯一决定。
        ///
        /// 失败（想要中文却可信地读到英文，补发一次后仍是英文）的处理：
        ///   **不宣告还原成功**，重建整套"待还原"状态 ——
        ///   目标模式 + 时间戳 + 证据（若目标不是英文），等下一轮补做。
        ///   这与 <see cref="DoRestoreFlip"/> 的失败分支口径完全一致。
        /// </summary>
        private void VerifyRestoreAfterUnlock(ImeMode target, IntPtr hwnd, string tag)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var v = ReadBackVerdict(target, hwnd, true, tag);
                if (v != BlindShiftVerdict.Failed) return;   // Confirmed / Inconclusive → 不动手

                if (!CanSendRealKey())
                {
                    Dbg("切换回读校验（" + tag + "）：还原这一下没生效，但此刻发不出真实按键 → 保留待还原");
                    ReopenRestorePending(target, tag);
                    return;
                }

                bool ok2 = CharInjector.TapShift();
                Note("还原输入法状态（" + tag + "）：★ 还原这一下没生效（回读仍是 English，"
                    + "窗口=0x" + hwnd.ToInt64().ToString("X") + "）→ 补发一次 Shift（结果="
                    + ok2 + "）");

                if (ok2)
                {
                    var v2 = ReadBackVerdict(target, hwnd, true, tag);
                    if (v2 != BlindShiftVerdict.Failed) return;
                }

                ReopenRestorePending(target, tag);
                Note("还原输入法状态（" + tag + "）：★ 补发后回读仍是 English → "
                    + "**不认定还原成功**，保留待还原（目标=" + target + "）");
            }
            catch (Exception ex)
            {
                Dbg("切换回读校验（" + tag + "）：还原回读异常：" + ex.Message);
            }
        }

        /// <summary>
        /// v47：把"还原没落地"重新登记成待还原（含证据），等下一轮补做。
        /// 与 <see cref="DoRestoreFlip"/> 的失败分支保持同一套状态，口径不许漂移。
        /// </summary>
        private void ReopenRestorePending(ImeMode target, string tag)
        {
            _restorePendingMode = (int)target;
            _restorePendingTick = Environment.TickCount;
            if (target != ImeMode.English)
                MarkImeLeftEnglish(tag + "：★ 回读确认还原未落地，输入法仍可能停在英文");
        }

        /// <summary>
        /// 登记一次「确保中文」待补（v42）—— 见 <see cref="_ensurePendingTick"/>。
        /// </summary>
        private void RegisterPendingEnsure(string reason)
        {
            _ensurePendingTick = Environment.TickCount;
            _ensurePendingReason = reason;
            Dbg("确保中文（" + reason + "）：等不到焦点所在的编辑控件 → **登记待补**，"
                + "焦点回到编辑控件后立即补做（" + EnsurePendingTtlMs + "ms 内有效）");
        }

        /// <summary>
        /// 「待补的确保中文」此刻该怎么处理 —— 纯判据，无副作用（v42，自测钉住）。
        ///
        /// 抽出来的目的：让"超时必须放弃""编辑态结束必须作废""模式变了必须作废"
        /// 这三条**不能再动手**的护栏可以用可执行断言钉住。
        /// 它们关系到的是**乱切输入法**这种最难查的故障（表现只是"偶尔打不出中文"）。
        /// </summary>
        internal enum PendingEnsureAction
        {
            /// <summary>没有待补。</summary>
            None = 0,

            /// <summary>有待补，但刚登记不久（焦点还在抖动）→ 等下一次触发，不清除。</summary>
            Wait = 1,

            /// <summary>条件齐备 → 立刻执行判定。</summary>
            Flush = 2,

            /// <summary>超时 / 编辑态已结束 / 模式或启用状态已变 → 作废。</summary>
            Discard = 3
        }

        internal static PendingEnsureAction DecidePendingEnsure(
            int pendingTick, int nowTick, bool isIgnoreMode, bool enabled, bool engaged,
            bool inTextEdit, int ttlMs, int minGapMs)
        {
            if (pendingTick == 0) return PendingEnsureAction.None;
            if (!isIgnoreMode || !enabled || !engaged) return PendingEnsureAction.Discard;

            int age = unchecked(nowTick - pendingTick);
            if (age < 0 || age > ttlMs) return PendingEnsureAction.Discard;

            // 编辑态已经结束 → 这一段文字早打完了，再切只会打乱用户下一段的输入
            if (!inTextEdit) return PendingEnsureAction.Discard;

            // 刚登记就来的焦点事件先放过 —— 此刻焦点往往还在抖动
            if (age < minGapMs) return PendingEnsureAction.Wait;

            return PendingEnsureAction.Flush;
        }

        /// <summary>
        /// 若存在**待补的「确保中文」**、且此刻编辑控件已经到位 → 立即补做一次判定。
        /// 由焦点事件与 500ms 轮询共同驱动（两条路任一生效即可，不怕漏事件）。
        /// </summary>
        private void TryFlushPendingEnsure(string why)
        {
            int pending = _ensurePendingTick;
            if (pending == 0) return;

            var action = DecidePendingEnsure(
                pending, Environment.TickCount,
                _config.WorkMode == XimeWorkMode.IgnoreIme,
                _config.Enabled, _isEngaged, _inTextEditMode,
                EnsurePendingTtlMs, EnsurePendingMinGapMs);

            switch (action)
            {
                case PendingEnsureAction.None:
                    return;

                case PendingEnsureAction.Discard:
                    _ensurePendingTick = 0;
                    _ensurePendingReason = null;
                    return;

                case PendingEnsureAction.Wait:
                    return;   // 焦点还在抖动 → 保留待补，等下一次触发
            }

            // 只做**一次**尝试（不在这里长时间轮询）：焦点事件 / 500ms 轮询会再来。
            bool trusted;
            var hwnd = ResolveAcadImeTarget(out trusted);
            if (hwnd == IntPtr.Zero
                || !IsUsableEnsureChineseTarget(trusted, HostTextFrame.IsTextInputHost(hwnd),
                                                         HostTextFrame.IsCommandLineWindow(hwnd)))
                return;   // 编辑控件还没到位 → 保留待补，等下一次触发

            // 抑制窗口：刚真正切换过 → 视为已经完成
            int gap = unchecked(Environment.TickCount - _lastEnsureChineseTick);
            if (_lastEnsureChineseTick != 0 && gap >= 0 && gap < EnsureChineseMinGapMs)
            {
                Dbg("补做确保中文（" + why + "）：" + gap + "ms 前刚切换过 → 视为已完成");
                _ensurePendingTick = 0;
                _ensurePendingReason = null;
                return;
            }

            string reason = _ensurePendingReason ?? "待补";
            _ensurePendingTick = 0;
            _ensurePendingReason = null;

            if (System.Threading.Interlocked.Exchange(ref _imeSwitchBusy, 1) == 1)
            {
                Dbg("补做确保中文（" + why + "）：另一处正在切换 → 跳过");
                return;
            }

            try
            {
                Dbg("补做确保中文（" + why + "）：编辑控件已到位（窗口=0x"
                    + hwnd.ToInt64().ToString("X") + "）→ 执行判定");
                ApplyEnsureChinese(reason, UserRecentlyToggledIme(), hwnd, trusted);
            }
            catch (Exception ex)
            {
                Dbg("补做确保中文（" + why + "）异常：" + ex.Message);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _imeSwitchBusy, 0);
            }
        }

        #endregion

        /// <summary>
        /// 判断该按键是否需要被插件接管（吞掉并改投到命令栏）。
        ///
        /// 仅在以下条件全部满足时接管：
        ///   1. 插件处于接管状态且已启用；
        ///   2. 当前不在「命令执行期间且设置为完全放行」（见 <see cref="XimeConfig.CommandKeyMode"/>）；
        ///   3. 命令栏当前需要英文输入（_commandLineNeedsEnglish）；
        ///   4. CapsLock 未开启（用户主动大写时不干预）；
        ///   5. 没有按住 Ctrl / Alt（避免拦截快捷命令）；
        ///   6. 只接管可打印字符键；
        ///   7. 前台窗口属于 AutoCAD（全局钩子的安全护栏）。
        ///
        /// 接管后由 <see cref="OnKeyCaptured"/> 把按键送给命令栏 ——
        /// 空闲提示符送**字符**（UNICODE，永远是英文），
        /// 命令执行期间送**真实按键**（VK/扫描码，第三方命令的二级提示才认得出）。
        /// 两条路径都绕过输入法 —— 因此**不需要探测输入法状态**。
        ///
        /// ★★ 第 2 条是「天正任意布置」事故的收尾（v26）：
        ///   v25 曾经在命令执行期间**完全不接管**（放行物理键）。天正确实好了，
        ///   但物理键同时落到输入法手里 —— 用户截图里左上角候选窗同步显示
        ///   「aaaaad」、字母还会被输入法吃掉。现在只有显式选择
        ///   <see cref="XimeConfig.XimeCommandKeyMode.PassThrough"/> 才会放行；
        ///   默认 <c>ReplayVk</c> 仍然接管，只是改用真实 VK 重放。
        /// </summary>
        private bool ShouldCaptureKeyCore(int vkCode, bool isDown)
        {
            // 诊断模式：只吞键不注入。此时放宽前面的状态判据，
            // 只要前台是 AutoCAD、且是可打印键，就接下（用于验证"吞键"是否生效）。
            if (SwallowOnly)
            {
                if (!_isEngaged) return false;
                if (!CharInjector.IsPrintableKey(vkCode)) return false;
                if (NativeMethods.IsCtrlPressed() || NativeMethods.IsAltPressed() || NativeMethods.IsWinPressed()) return false;
                return IsForegroundInAcad();
            }

            if (!_isEngaged) return false;
            if (!_config.Enabled) return false;

            // ★★★ v61：本模式下钩子是否接管按键 —— 见 ShouldTakeOverKeys。
            //   无视输入法模式：恒 true（原通路一字不变）。
            //   切换输入法模式：仅当该输入法的中英态通道已被 v48 判定不可信时，
            //   才退回钩子兜底。现场（AutoCAD 2024 + 搜狗输入法）：
            //   切换模式在两个方向上都会猜错 —— 命令行该英文却弹候选窗。
            if (!KeysTakeOverEnabled) return false;

            // ★ 只有显式选择「完全放行」时才在命令执行期间松手。
            //   ★★ v1.0.2 无感修订版：默认档 = PassThrough（放行物理键）——
            //   因为**它是天正唯一认的档**（现场证伪：PostKey/PostKeyFrame/ReplayVk/UNICODE
            //   四条合成通道天正一律不认，PostKey 只会把字母打进命令栏）。
            //   它的默认工作模式是「切换输入法模式」，那种模式下根本走不到这里。
            // ★★★ v60 第二道闸门（豆包 / TSF 输入法事故 · 见 ShouldSeizeCommandKeyAsChar）：
            //
            //   PassThrough 这一档的契约是「命令执行期间物理键原样交给系统」，
            //   之所以敢放行，是因为另有一条保险：ImeWindowBypass 会在命令期间
            //   用 ImmAssociateContext(hwnd, NULL) 解除 CAD 窗口与输入法的关联。
            //
            //   ★ 但 ImmAssociateContext 动的是 **IMM32 的 HIMC** —— 对**纯 TSF 文本服务**
            //     （豆包输入法：tsf-oime.dll，注册表里 Keyboard Layouts 命中 0 项、
            //      无任何 .ime 文件）**结构性无效**：TSF 的按键路由走 ITfThreadMgr /
            //      文档管理器，压根不看 HIMC。
            //   ⇒ 对这类输入法，命令期间"放行"= 字母直接喂给中文态输入法 ⇒ 组字 + 候选窗。
            //     症状就是用户报的：`undo` 之后打二级命令 `a`、`DRAWORDER` 之后打 `b`，
            //     第一次正常、删掉重打就变中文（焦点在命令行输入框时输入法才有组字上下文）。
            //
            //   ⇒ 所以放行之前多问一句：**此刻焦点是不是在"输入法能组字的地方"？**
            //     是 ⇒ 吞键 + 注入字符（WM_CHAR 通道，输入法看不到）；
            //     否（焦点在绘图区，天正 RYBZ 那种 acedGrRead 读原始按键的场景）
            //       ⇒ 照旧放行 —— 那是这一档存在的唯一理由，必须保住。
            if (ShouldPassThroughKey(_config, _currentCommand) && !ShouldSeizeCommandKeyAsCharNow())
                return false;

            // ★★ v35：**不再**因为 CapsLock 开启就整体放行。
            //
            //   旧策略是"CapsLock 开启时完全放行，绝不干预"，后果是：只要 CapsLock
            //   是开着的，无视输入法模式就**一个按键都不接管** —— 现场日志里 24 秒内
            //   每一个按键都记着 `跳过: CapsLock 开启`，用户看到的正是「不工作」。
            //
            //   而且这条策略本来就是多余的：注入的字符大小写**已经**由
            //   CharInjector.GetEnglishCharForKey 按 `Shift ^ CapsLock` 算好了，
            //   命令名本身也不区分大小写。CapsLock 开着时照常接管才是对的。
            //
            //   （保留一行诊断日志：CapsLock 状态会影响注入字符的大小写，
            //     排查"字母怎么都变大写了"时需要看到它。）
            if (_debugLog && NativeMethods.IsCapsLockOn())
                Dbg("  注: CapsLock 开启（注入字符按大写计算）");

            // 组合键一律放行（Ctrl / Alt / Win）。
            // ★ Win 键尤其关键：Win+D / Win+E / Win+R 等是系统级快捷键，
            //   吞掉其中任意一个字母键都会让整个快捷键失效（用户实测反馈）。
            if (NativeMethods.IsCtrlPressed() || NativeMethods.IsAltPressed() || NativeMethods.IsWinPressed()) return false;

            // 只接管可打印字符键
            if (!CharInjector.IsPrintableKey(vkCode)) return false;

            // 安全护栏：只在 AutoCAD 自身窗口处于前台时接管
            if (!IsForegroundInAcad()) return false;

            // ★★★ v62：焦点落在**外部文本编辑控件**上 ⇒ 彻底让路
            //   （见 ShouldYieldToForeignEditBox：AutoCAD 图层特性管理器的名称框、
            //    各类对话框输入框都是这种 —— 用户要在那里打中文）。
            //
            //   ★ 位置在 v59 自愈**之前**：本条比自愈更"硬" ——
            //     自愈只是把命令栏的英文标志抢回来，而这里要的是"压根不碰"。
            //     放在前面同时省掉一次自愈调用（两者都不会在编辑框上有意义）。
            //   ★ 也因此在 v60 的 PassThrough 早退之后：那一段处理的是
            //     "命令执行期间 + 焦点在可组字宿主"（命令栏/自动完成下拉），
            //     与本条处理的"焦点在真编辑框"互斥、互补。
            if (FocusIsForeignEditBox())
            {
                if (_debugLog)
                    Dbg("  跳过: 焦点在外部文本编辑控件上（非命令栏）⇒ 让路给输入法");
                return false;
            }

            // ★★★ v59 误判自愈（豆包输入法事故 · 见 ShouldReclaimEnglishForIdlePrompt）：
            //   走到这里说明：IgnoringIme 模式、非 PassThrough、前台是 CAD、
            //   而且按下的是一枚**可打印字符键**（没有 Ctrl/Alt/Win）——
            //   这就是"用户正在往命令栏敲命令"本身。
            //   此时若标志说"命令栏不需要英文"，那是**误判**：
            //   AutoCAD 的命令栏家族（命令栏本体 / 命令自动完成下拉 / 信息中心…）
            //   全是 `HwndWrapper`，被 `IsEditBoxClass` 一律当成"文字编辑框"，
            //   于是把"在敲命令"误判成"在编辑文字"，把命令名交给了输入法
            //   （症状：第一个字母正常，第二个字母起变中文）。
            //   ⇒ 不盲信标志：就地采一次真实焦点复核，证据不足就把英文接管抢回来。
            //
            // ★ 位置很关键：**放在所有廉价闸门之后** ——
            //   组合键（Ctrl/Alt/Win）、非可打印键、前台不是 CAD 都不会走到这里，
            //   因此自愈只可能被"真的在敲命令"的按键触发，不会被 Ctrl+C 之类误触发。
            // ★ 代价：仅在标志为 false 时多一次焦点读取；正常路径（标志为 true）零开销。
            if (!_commandLineNeedsEnglish)
            {
                IntPtr reclaimFocus;
                if (!ShouldReclaimEnglishNow(out reclaimFocus)) return false;
                ReclaimEnglishForIdlePrompt(reclaimFocus);
            }

            return true;
        }

        /// <summary>
        /// v59 自愈的**事实采集端**：询问「此刻是否应当把命令栏英文标志抢回来」，
        /// 并把用于记录的焦点句柄带出去。
        ///
        /// 与 <see cref="ShouldCaptureKeyCore"/> 一样位于**每次按键**的路径上，
        /// 因此只在"标志已被清"时被调用；正常路径（标志为 true）零额外开销。
        /// </summary>
        /// <param name="focus">真实焦点窗口；未取到时为 <see cref="IntPtr.Zero"/>。</param>
        private bool ShouldReclaimEnglishNow(out IntPtr focus)
        {
            focus = IntPtr.Zero;

            if (!_isEngaged) return false;
            if (!_config.Enabled) return false;
            if (!KeysTakeOverEnabled) return false;   // ★ v61：与 ShouldCaptureKeyCore 同一口径
            if (!IsForegroundInAcad()) return false;

            try { focus = CharInjector.GetRealFocusWindow(); }
            catch (Exception) { return false; }
            if (focus == IntPtr.Zero) return false;

            bool isCmdLine = false, isEdit = false, isWpf = false;
            try { isCmdLine = HostTextFrame.IsCommandLineWindow(focus); }
            catch (Exception) { }
            try { isEdit = FocusMonitor.IsEditBoxWindow(focus); }
            catch (Exception) { }
            try { isWpf = FocusMonitor.IsWpfHostClass(FocusMonitor.GetWindowClass(focus)); }
            catch (Exception) { }

            return ShouldReclaimEnglishForIdlePrompt(
                _currentCommand != null, isCmdLine, isEdit, isWpf);
        }

        /// <summary>
        /// v60 的**事实采集端**：命令执行期间，这一枚按键该不该从「放行物理键」
        /// 改成「吞键 + 注入字符」？
        ///
        /// ═══════════════ 为什么需要它 ═══════════════
        ///
        /// `CommandKeyMode.PassThrough` 的契约是「命令执行期间物理键原样交给系统」，
        /// 它敢放行是因为另有一条保险：<see cref="ImeWindowBypass"/> 会在命令期间
        /// 用 <c>ImmAssociateContext(hwnd, IntPtr.Zero)</c> 解除 CAD 窗口与输入法的关联。
        ///
        /// ★ 但那条保险**只对 IMM32 输入法有效**。它动的是 IMM32 的 HIMC；
        ///   而 **TSF 文本服务**的按键路由走 <c>ITfThreadMgr</c> / 文档管理器，
        ///   **完全不看 HIMC** ⇒ 对纯 TSF 输入法（实测：豆包输入法，实现 DLL
        ///   `System32\tsf-oime.dll`，注册表 `Keyboard Layouts` 命中 **0 项**、
        ///   无任何 `.ime` 文件）这条保险是**结构性失效**的。
        ///
        /// ⇒ 于是放行出去的字母会**直接喂给中文态的输入法** ⇒ 组字 + 候选窗。
        ///   用户报的症状正是它：`undo` 之后打二级命令 `a`、`DRAWORDER` 之后打 `b`，
        ///   **第一次正常、删掉重打就变中文**。
        ///
        /// ═══════════════ 为什么是"第一次正常" ═══════════════
        ///
        /// 输入法要组字，得先有"组字上下文"——也就是焦点落在真正的编辑控件上。
        /// 命令刚起来时焦点还在**绘图区**（`Afx:…:28:…`，不是 TSF 编辑上下文）：
        ///   · 第 1 个选项字母 → 焦点在绘图区 ⇒ 输入法不组字，字母直达 AutoCAD ⇒ **正常**；
        ///   · 但这个字母被 AutoCAD 回显进命令行输入框，**焦点随之移到命令行**
        ///     （本机 v59 实测：每敲一个字母，焦点都在「命令栏 HwndWrapper」⇄
        ///      「绘图区 Afx:…:28:…」之间抖一次）；
        ///   · 删掉字母（Backspace）后焦点**仍留在命令行输入框** ⇒
        ///     再打同一个字母时它落在一个 TSF 编辑上下文上 ⇒ 豆包组字 ⇒ **变中文**。
        ///
        /// 天正 RYBZ 之所以一直正常：它的二级命令提示留在**绘图区**上
        /// （天正用 acedGrRead 一类通道读**原始按键**），焦点没被搬到命令行输入框，
        /// 输入法也就没有组字上下文 ⇒ 物理键原样直达天正。
        /// **⇒ 判据必须按"焦点在哪"分流，而不是一刀切放行 —— 这正是本条判据的全部内容。**
        ///
        /// ═══════════════ 判据本体 ═══════════════
        ///
        /// 全部满足才改通道（任何一个不满足都退回放行 = 今天的既有行为）：
        ///   ① 档位确实是 PassThrough（其它档本来就是吞键 + 有自己的投递通道）；
        ///   ② 旁路开关开着（`BypassImeDuringCommand == false` = 用户显式弃用
        ///      "命令期间无视输入法"，那就连它的替代通道也不该擅自启用）；
        ///   ③ 确实有命令在执行（空闲提示符本来就走注入字符，无需本判据）；
        ///   ④ 不在文字编辑态（真的在改文字 —— 必须把输入权交还输入法打中文）；
        ///   ⑤ 当前命令不是"需要中文"的（TEXT/MTEXT/天正文字… 见白名单）；
        ///   ⑥ **焦点落在"输入法能组字的地方"**（唯一的分流条件，也是天正 RYBZ 的保命符）。
        /// </summary>
        /// <param name="mode">配置里的命令期间按键投递档。</param>
        /// <param name="bypassImeDuringCommand">「命令期间无视输入法」开关（见
        ///   <see cref="XimeConfig.BypassImeDuringCommand"/>）。</param>
        /// <param name="currentCommand">当前执行的命令名；空闲时为 null/空。</param>
        /// <param name="inTextEditMode">是否处于文字编辑态（<see cref="_inTextEditMode"/>）。</param>
        /// <param name="commandIsChinese">当前命令是否需要中文
        ///   （<c>MatchChinese(cmd) != None</c>）。</param>
        /// <param name="focusIsComposableHost">
        ///   真实焦点是否落在「输入法能组字的地方」——
        ///   即 <see cref="FocusIsComposableHost"/>：命令栏链（含"命令自动完成"下拉，
        ///   见 <see cref="HostTextFrame.IsCommandLineWindow"/>）**或** CAD 的 WPF 宿主
        ///   （`HwndWrapper`，AutoCAD 的命令行输入框 / 动态输入浮层的实际载体）。
        ///   ★ 判据故意用"是不是 CAD 的 WPF 面"而不是"具体是不是哪一个命令栏控件"：
        ///     豆包只需拿到**一个 TSF 编辑上下文**就会组字，而 AutoCAD 里这样的面
        ///     不止命令行输入框一个（动态输入、自动完成下拉……），逐个枚举必然漏。
        ///     反过来，第三方（天正）真正承载文字输入的都是**原生 MFC**控件
        ///     （`AfxWnd*` / `Afx:…`），以及 RYBZ 那种直接读**绘图区**原始按键的场景 ——
        ///     两者都不是 WPF 宿主，因此这条判据**碰不到 RYBZ**。
        /// </param>
        /// <param name="focusOnForeignEditBox">
        /// ★★★ v64：真实焦点是否落在**非命令栏的真编辑控件**上
        /// （<see cref="ShouldYieldToForeignEditBox"/> 的口径，事实由
        /// <see cref="FocusIsForeignEditBox"/> 采集）。
        ///
        /// ★ 为什么要单独一条：本判据的第 ⑥ 条只问"焦点是不是 WPF 宿主 / 命令栏链"，
        ///   而**第三方插件的 WPF 界面同样是 `HwndWrapper`** —— 现场实测：X批注
        ///   （纯 WPF，<c>XAnnotation.AutoCAD.dll</c> 引用 PresentationFramework）的批注
        ///   对话框输入框，类名 <c>HwndWrapper[DefaultDomain;;…]</c>。
        ///   于是这个框既"能组字"（该让输入法工作），又被判成"像命令栏"（该吞键注入）
        ///   —— 两条结论打架，最终按后写的赢 ⇒ 用户在批注框里打不出中文。
        ///   ⇒ 补这一条：**真编辑控件**一律否决"改注入字符"。
        ///
        /// ★ 为什么不影响命令栏：命令栏家族由
        ///   <see cref="HostTextFrame.IsCommandLineWindow"/> 排除在"真编辑控件"之外
        ///   （v64 起它的证据链必须含 CLI Palette，见 <c>AcadTextFrame.IsCommandLineFamily</c>）。
        /// </param>
        internal static bool ShouldSeizeCommandKeyAsChar(
            XimeConfig.XimeCommandKeyMode mode,
            bool bypassImeDuringCommand,
            string currentCommand,
            bool inTextEditMode,
            bool commandIsChinese,
            bool focusIsComposableHost,
            bool focusOnForeignEditBox = false)
        {
            if (mode != XimeConfig.XimeCommandKeyMode.PassThrough) return false;
            if (!bypassImeDuringCommand) return false;
            if (string.IsNullOrEmpty(currentCommand)) return false;
            if (inTextEditMode) return false;
            if (commandIsChinese) return false;
            // ★ v64：真编辑控件（第三方插件对话框 / 图层名称框…）—— 与"文字编辑态"同级否决
            if (focusOnForeignEditBox) return false;
            return focusIsComposableHost;
        }

        /// <summary>
        /// <see cref="ShouldSeizeCommandKeyAsChar"/> 的**事实采集端**：就地取真实焦点、
        /// 就地算"这条命令要不要中文"，然后把结论交给那条纯判据。
        ///
        /// ★ 调用点有两个（<see cref="ShouldCaptureKeyCore"/> 决策、<see cref="OnKeyCaptured"/>
        ///   执行），共用本方法 —— 与 v59 的 <see cref="ShouldReclaimEnglishNow"/> 同一手法，
        ///   避免"决策"与"执行"各写一遍、日后漂移。
        ///
        /// ★ 为什么顺序是"先探焦点、再问白名单"：焦点探针是唯一的分流条件，
        ///   而白名单匹配是一次集合查找。命令执行期间每个字母都会调到这里，
        ///   把**日常都会命中的分支**（焦点在绘图区 ⇒ 直接 false，正是天正 RYBZ 的常态）
        ///   挡在最前面，白名单只在"真的要对命令栏写字母"时才查一次。
        ///
        /// ★ 副作用（刻意保留的一处）：若最终结论是"改走字符注入"，说明此刻命令栏必须是英文；
        ///   而标志若为 false，只可能是被「误进文字编辑态」清掉的残留（v59 事故的另一种入口，
        ///   焦点在下拉浮层上而非真正的文字编辑框）⇒ 就地修回。
        ///   真正需要中文的命令在上面的 <c>commandIsChinese</c> 已被拦掉，不会被这里改写。
        /// </summary>
        private bool ShouldSeizeCommandKeyAsCharNow()
        {
            if (_config.CommandKeyMode != XimeConfig.XimeCommandKeyMode.PassThrough) return false;
            if (!_config.BypassImeDuringCommand) return false;
            if (string.IsNullOrEmpty(_currentCommand)) return false;
            if (_inTextEditMode) return false;

            if (!FocusIsComposableHost()) return false;   // ★ 天正 RYBZ 靠这一条活着

            bool commandIsChinese =
                _config.MatchChinese(_currentCommand) != XimeConfig.ChineseNeed.None;

            // ★★★ v64：焦点在**非命令栏的真编辑控件**上（X批注这类第三方 WPF 面板、
            //   图层名称框…）⇒ 既不吞键注入，也**不许**把 `_commandLineNeedsEnglish`
            //   置回 true —— 上面那句 FocusIsComposableHost 会因为"是 WPF 宿主"而成立，
            //   若不在这里否决，它就会一边说"能组字"一边把状态改成"要英文"。
            //   事故与日志原文见 <see cref="ShouldSeizeCommandKeyAsChar"/> 的 v64 注释。
            bool foreignEdit = FocusIsForeignEditBox();

            if (!ShouldSeizeCommandKeyAsChar(
                    _config.CommandKeyMode, _config.BypassImeDuringCommand,
                    _currentCommand, _inTextEditMode, commandIsChinese,
                    focusIsComposableHost: true, focusOnForeignEditBox: foreignEdit))
                return false;

            if (!_commandLineNeedsEnglish)
            {
                _commandLineNeedsEnglish = true;

                int now = Environment.TickCount;
                if (unchecked(now - _seizeLogTick) >= SeizeLogGapMs)
                {
                    _seizeLogTick = now;
                    Note("命令期间改注入字符：命令=" + _currentCommand
                         + "（焦点在命令栏 ⇒ 不放行物理键，避免 TSF 输入法组字）");
                }
            }

            return true;
        }

        /// <summary>
        /// 真实焦点当前是否落在**输入法能组字的地方** —— v60 判据里唯一的分流条件。
        ///
        /// 两条判据，命中任一即为"能组字"：
        ///   ① <see cref="HostTextFrame.IsCommandLineWindow"/> —— 命令栏链。
        ///      该谓词已包含 v59 补的两条（属主链匹配 + 与 <see cref="HostTextFrame.Resolve"/>
        ///      同族），因此「命令自动完成下拉」这类 **WPF Popup（带属主的顶层窗口，
        ///      GetParent() 返回 NULL）** 也能被正确认出来。
        ///   ② <see cref="FocusMonitor.IsWpfHostClass"/>（`HwndWrapper`）—— CAD 的 WPF 宿主。
        ///      ★ 为什么要带上它：豆包只需拿到**一个 TSF 编辑上下文**就会组字，而 AutoCAD 里
        ///        这种面不止命令行输入框一个（**动态输入浮层**同样会在命令期间接管按键显示、
        ///        且同样是 WPF）。只认 ① 那条父链会漏掉它们，漏掉就是"改不动"。
        ///
        /// ★ 为什么这两条碰不到天正 RYBZ：天正的二级命令提示留在**绘图区**
        ///   （`Afx:…:28:…`，天正用 acedGrRead 一类通道读原始按键），既不在 CLI Palette
        ///   父链上、也不是 WPF 宿主；天正真正承载文字输入的原生编辑控件同样不命中 ②。
        ///
        /// ★ 拿不到焦点（Zero）或异常 → 返回 false = **保守放行**（维持 PassThrough 原行为）。
        ///   理由：本判据的失效方向必须是"少接管"，绝不能反过来把物理键无端吞掉 ——
        ///   那会让第三方命令（天正 RYBZ 那类）直接失灵。
        /// </summary>
        private bool FocusIsComposableHost()
        {
            try
            {
                var f = CharInjector.GetRealFocusWindow();
                if (f == IntPtr.Zero) return false;              // 读不到 ⇒ 保守放行

                if (HostTextFrame.IsCommandLineWindow(f)) return true;

                return FocusMonitor.IsWpfHostClass(FocusMonitor.GetWindowClass(f));
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// ★★★ v62 纯判据：焦点落在**外部文本编辑控件**上时，键盘钩子一律**不接管**。
        ///
        /// ═══════════ 事故现场（AutoCAD + 图层特性管理器，2026-10-14） ═══════════
        ///
        /// 用户原话：「AutoCAD 下 layer 命令 - 新建图层 - 修改新建图层名称，
        ///            修改图层名称时候无法切换到中文，手动也无法切换。」
        ///
        /// 实测窗口结构（本机 AutoCAD 2022，探针 <c>_research/watch_layer_edit.py</c>）：
        /// <code>
        ///   焦点 = WindowsForms10.EDIT.app.0.1a4f6bb_r57_ad1   （标题「图层1」，75x23）
        ///   父链 = … → AfxWnd100u "Layer Palette" → AfxWnd100u "Layer"
        ///          → AfxControlBar100u "Layer"（**独立顶层窗口**）
        /// </code>
        ///
        /// 因果链（每一环都有实测或代码依据，不是推测）：
        ///   ① 该框是**真编辑框**，但类名不以 `Edit` 开头 ⇒ v62 之前
        ///      <see cref="FocusMonitor.IsEditBoxClass"/> 判它"不是编辑控件"；
        ///   ② 于是 <c>OnFocusObjectChanged</c> 的 `isEdit &amp;&amp; !isCommandLine` 不成立
        ///      ⇒ **不进文字编辑态** ⇒ <c>_commandLineNeedsEnglish</c> 保持 true
        ///      （空闲期由 `OnCommandEnded` / `ForceEnglish` 置起）；
        ///   ③ 钩子于是把这个框里敲的**每个字母**都吞掉、改注入英文字母
        ///      ⇒ 中文打不进；手动把输入法切到中文也没有用（字母照样被吞成英文）。
        ///
        /// ═══════════ 判据本体 ═══════════
        ///
        ///   是编辑控件 **且** 不在命令栏家族里 ⇒ 让路（返回 true）。
        ///
        ///   ★ 命令栏（含其自动完成下拉）由 <see cref="HostTextFrame.IsCommandLineWindow"/>
        ///     排除在外，因此命令栏的英文接管**分毫不动** —— 实测命令栏输入框是
        ///     `HwndWrapper[DefaultDomain;;…]`，父链含 `CLI Palette`。
        ///   ★ 与 <see cref="ShouldSeizeCommandKeyAsChar"/> 的关系：那条只管
        ///     「命令执行期间 + PassThrough 档」这一条路径；本条是**兜底**
        ///     （覆盖空闲期，以及焦点事件没到达、标志仍是 true 的情形）。两条互补。
        ///   ★ 失效方向：拿不到焦点 ⇒ 采集端返回 false ⇒ **照旧接管**（保守）。
        ///     本判据最多"少修一点"，绝不会把命令栏该有的英文弄丢。
        /// </summary>
        internal static bool ShouldYieldToForeignEditBox(
            bool focusIsEditBox, bool focusIsCommandLine, bool focusIsDynamicInput)
        {
            // ★★★ v63：动态输入框内部是**原生 `Edit`**（类名就叫 `Edit`），
            //   单看类名与"真编辑框"完全同形。但它是**命令输入端**：
            //   在那里敲的字母属于命令行，必须继续拦截并注入英文。
            //   ⇒ 这一条必须写在最前面，优先级高于"是编辑框就让路"。
            if (focusIsDynamicInput) return false;
            return focusIsEditBox && !focusIsCommandLine;
        }

        /// <summary>
        /// <see cref="ShouldYieldToForeignEditBox"/> 的**事实采集端**：
        /// 就地取真实焦点，判断"此刻是不是落在外部编辑控件上"。
        ///
        /// ★ 调用点两处，口径完全一致（避免两边各自漂移）：
        ///   · <see cref="ShouldCaptureKeyCore"/> —— 决定钩子要不要让路；
        ///   · <see cref="SyncImeBypass"/> —— 决定「命令期间无视输入法」要不要动手。
        ///     两处的失效后果是同一条：**用户在这些框里打不出中文**。
        ///
        /// ★ 代价：一次焦点读取 + 一次父链遍历（约 1~3 µs）。只在
        ///   "用户正在往 CAD 里敲可打印字符"时才会走到，可忽略。
        /// </summary>
        private bool FocusIsForeignEditBox()
        {
            try
            {
                var f = CharInjector.GetRealFocusWindow();
                if (f == IntPtr.Zero) return false;              // 读不到 ⇒ 保守（照旧接管）

                // ★★★ v63：动态输入（DYN）提示框 = 命令输入端，**绝不让路**。
                //   它内部是原生 `Edit`（类名 `Edit`），只看类名会把"敲命令"
                //   误判成"在对话框里打中文" ⇒ 第 1 个字母之后就把键放给输入法。
                //   实测见 HostTextFrame.IsDynamicInputWindow 的注释（含引擎日志原文）。
                bool isDynInput;
                try { isDynInput = HostTextFrame.IsDynamicInputWindow(f); }
                catch (Exception) { isDynInput = false; }
                if (isDynInput) return false;

                bool isCmdLine;
                try { isCmdLine = HostTextFrame.IsCommandLineWindow(f); }
                catch (Exception) { isCmdLine = false; }
                if (isCmdLine) return false;                     // 命令栏家族：必须保持英文接管

                bool isEdit;
                try { isEdit = FocusMonitor.IsEditBoxWindow(f); }
                catch (Exception) { isEdit = false; }

                return ShouldYieldToForeignEditBox(isEdit, isCmdLine, false);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// v59 自愈的**执行端**：把命令栏英文标志抢回来，并（**限流**）留一条
        /// 现场可查的日志。
        ///
        /// ★ 只动两个我们自己拥有的标志，**绝不碰用户输入法状态** ——
        ///   与 <see cref="ForceEnglish"/> 的铁律一致（无视输入法模式只置标志）。
        ///
        /// ★ 为什么必须留日志：这条自愈一旦触发，说明焦点判定**当时判错了**。
        ///   报障现场（用户机器）最需要的就是这一行 —— 它把"到底是哪个窗口
        ///   把我们骗进了编辑态"钉下来（句柄 + 类名 + 当时的命令状态）。
        /// </summary>
        private void ReclaimEnglishForIdlePrompt(IntPtr focus)
        {
            _inTextEditMode = false;
            _lastTextEditHost = IntPtr.Zero;
            _commandLineNeedsEnglish = true;

            int now = Environment.TickCount;
            if (unchecked(now - _reclaimLogTick) < ReclaimLogGapMs) return;
            _reclaimLogTick = now;

            string cls;
            try { cls = FocusMonitor.GetWindowClass(focus); }
            catch (Exception) { cls = "(读不到)"; }

            Note("误判自愈：命令栏英文标志曾被清（怀疑焦点 0x"
                 + focus.ToInt64().ToString("X") + " 类名=" + cls
                 + "），已抢回英文接管（命令=" + (_currentCommand ?? "-") + "）");
        }

        /// <summary>
        /// 本进程 ID（永不变化，只取一次）。
        ///
        /// ★ 用 <c>GetCurrentProcessId()</c> 而不是 <c>Process.GetCurrentProcess().Id</c>：
        ///   后者每次调用约 600ns（要构造 Process 对象），而本方法位于「每按一个键都要走」
        ///   的钩子判断里（还会被调用两次）。实测 613ns → 32ns，见 XimeTest bench。
        /// </summary>
        private static readonly uint MyProcessId = NativeMethods.GetCurrentProcessId();

        /// <summary>判断当前前台窗口是否属于 AutoCAD 进程（或其后代窗口）。</summary>
        private bool IsForegroundInAcad()
        {
            try
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;

                var acad = _focus.AcadMainWindow;
                if (acad != IntPtr.Zero && fg == acad) return true;

                uint fgPid;
                NativeMethods.GetWindowThreadProcessId(fg, out fgPid);
                if (fgPid == 0) return false;

                // 与 CAD 同进程即认为是 CAD 的窗口
                return fgPid == MyProcessId;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 接管按键：把按键送入 AutoCAD 命令栏（绕过输入法）。
        /// 返回 true 表示「已成功投递」，钩子据此决定是否吞掉原生按键；
        /// 返回 false 时钩子会放行原生按键，避免命令栏彻底无法输入。
        ///
        /// ★ 双通道策略 —— 按「命令是否在执行」分流
        ///
        /// 实测结论（用 WPF 目标窗口做的对照实验）：
        ///   · `PostMessage(WM_KEYDOWN)` 对 **WPF 控件无效** —— WPF 的 HwndSource
        ///     不把窗口消息翻译成文本输入，投递"成功"但字符不出现。
        ///     更糟的是：它**不会**替代原生按键，于是与 UNICODE 通道叠加，
        ///     导致每个字符重复出现（用户实测 `lin` 变成 `llliiinn`）。
        ///     —— 所以它只作为**命令执行期间**的可选方案（PostKey 模式），
        ///     且两种通道不会同时启用。
        ///   · `SendInput(KEYEVENTF_UNICODE)` 对 WPF **有效**，且对 MFC 控件同样有效。
        ///
        /// 本版的投递方式（详见 <see cref="XimeConfig.CommandKeyMode"/>）：
        ///   · **空闲命令提示符（敲命令名）** → SendInput(KEYEVENTF_UNICODE)
        ///       注入确定的 Unicode 码点：不经过键盘布局、不经过输入法，
        ///       MFC / WPF 命令栏通吃，永远是英文。
        ///   · **命令执行期间（选项 / 二级提示）** → 重放真实 VK + 扫描码
        ///       （或 PostMessage，取决于配置）。原因见下方注释与
        ///       <see cref="XimeConfig.CommandKeyMode"/> 的事故记录。
        ///
        /// 编辑键（退格 / 回车 / Tab / Esc）走 SendVkInput —— 它们不产生字符、
        /// 与输入法无关，用真实 VK 发送 MFC 与 WPF 都能正确处理。
        /// </summary>
        private bool OnKeyCaptured(int vkCode, bool isDown)
        {
            // 防重入：防止自己注入的按键再次回到这里
            if (System.Threading.Interlocked.Exchange(ref _injectGuard, 1) == 1)
                return true;

            try
            {
                // ---------- 诊断：只吞键不注入 ----------
                if (SwallowOnly)
                {
                    SwallowCount++;
                    if (_debugLog) Dbg("SwallowOnly 吞键 VK=0x" + vkCode.ToString("X2"));
                    return true;   // 吞掉，但不注入
                }

                // ---------- 编辑键：退格 / 回车 / Tab / Esc ----------
                if (CharInjector.IsEditingKey(vkCode))
                {
                    bool editOk = CharInjector.SendVkInput(vkCode);
                    // ★ Dbg 的实参在调用前就已拼好，即使日志关闭也要付出这份开销。
                    //   这里位于"每次按键"的路径上，必须先用 _debugLog 守卫。
                    if (_debugLog) Dbg("编辑键 VK=0x" + vkCode.ToString("X2") + " SendVkInput 结果=" + editOk);
                    if (editOk) CapturedCount++;
                    return editOk;
                }

                // ---------- 安全护栏：前台必须是 AutoCAD ----------
                //   注：ShouldCaptureKeyCore 里刚校验过一次，这里是**刻意保留**的第二道 ——
                //   从判定到真正注入之间现实时间极短，但注入是全局事件、会落到当时的
                //   前台窗口上，多一道校验的成本（约 1µs）远低于"字符跑到别的程序里"的代价。
                if (!IsForegroundInAcad())
                {
                    if (_debugLog) Dbg("投递放弃：前台窗口不属于 AutoCAD");
                    return false;
                }

                // ---------- ★★ 命令执行期间：重放「真实按键」而不是注入字符 ----------
                //
                // 为什么不能继续用 UNICODE 注入（天正任意布置事故）：
                //   命令执行期间的选项提示（A/E/D/F/X/Y）多由第三方用
                //   acedGrRead 之类的**原始按键**通道读取，而 UNICODE 注入
                //   事件的 wVk 恒为 VK_PACKET(0xE7) —— 它认不出，
                //   于是命令没反应，只在命令栏留一个小写字母。
                //
                // 为什么不能干脆放行物理键（v25 的做法）：
                //   物理键会同时落到输入法手里 —— 候选窗弹出、字母被吃掉。
                //   用户实测截图里「左上角候选窗同步显示 aaaaad」就是这个问题。
                //
                // 现在的做法：物理键照旧被钩子吞掉（输入法看不到），
                //   再按 CommandKeyMode 把等价的按键事件送给 AutoCAD。
                if (_currentCommand != null)
                {
                    // ★★★ v60：命令期间，若焦点在命令栏链上（输入法能在那里组字），
                    //   走**字符注入**而不是重放真实按键 —— 见 ShouldSeizeCommandKeyAsChar。
                    //
                    //   为什么要单独一条通道：这里其余几档（PostKey / PostKeyFrame /
                    //   ReplayVk）送出的都是**按键事件**，它们会进入 AutoCAD 的消息泵，
                    //   于是 **TSF 的按键路由照样能看到** ⇒ 中文态输入法照样组字。
                    //   而 SendInput(KEYEVENTF_UNICODE) 的 wVk = 0，最终只产生一个
                    //   **WM_CHAR**：它不经过 TSF 的按键回调（TSF 只处理有真实 VK 的键），
                    //   所以输入法**永远看不到**，命令栏拿到的却是一个货真价实的英文字符。
                    //   —— 这正是空闲提示符那条通道，此处只是把同一手法用到命令期间的一小片场景。
                    if (ShouldSeizeCommandKeyAsCharNow())
                    {
                        char chSeize = CharInjector.GetEnglishCharForKey(vkCode);
                        if (chSeize == '\0')
                        {
                            if (_debugLog) Dbg("命令期间字符注入跳过：VK=0x" + vkCode.ToString("X2") + " 无英文映射");
                            return false;
                        }

                        bool okSeize = CharInjector.SendUnicodeInput(chSeize);
                        if (_debugLog) Dbg("命令期间字符注入 '" + chSeize + "' 结果=" + okSeize);
                        if (okSeize) CapturedCount++;
                        return okSeize;
                    }

                    bool sent;
                    if (_config.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PostKey ||
                        _config.CommandKeyMode == XimeConfig.XimeCommandKeyMode.PostKeyFrame)
                    {
                        // 投递档：PostKey 投给焦点控件、PostKeyFrame 投给前台顶层窗口。
                        //   都不走系统输入队列 ⇒ 输入法看不到、不弹候选窗。
                        // ★ 把「实际投给了哪个窗口 + 它的类名」记全：
                        //   这一档的表现是"返回成功但命令没反应"时，唯一能判断
                        //   "是不是投错了窗口"的就是这两项。
                        var target = ResolvePostKeyTarget(_config.CommandKeyMode);
                        sent = CharInjector.PostVkKey(target, vkCode);
                        if (_debugLog) Dbg("命令期间投递窗口消息 VK=0x" + vkCode.ToString("X2") +
                                           " 档=" + _config.CommandKeyMode +
                                           " 目标=0x" + target.ToInt64().ToString("X") +
                                           " 类名=" + (target != IntPtr.Zero
                                               ? FocusMonitor.GetWindowClass(target) : "(空)") +
                                           " 结果=" + sent);
                    }
                    else
                    {
                        // 备选档：重放真 VK + 真扫描码，对 AutoCAD/天正 与物理按键等价。
                        //   ★ 注入事件仍进系统输入队列 ⇒ 输入法也会看到（可能弹候选窗）。
                        sent = CharInjector.ReplayVk(vkCode);
                        if (_debugLog) Dbg("命令期间重放真实按键 VK=0x" + vkCode.ToString("X2") +
                                           " 结果=" + sent);
                    }

                    // 投递失败时**必须放行原生按键**，否则这个键就彻底丢了。
                    if (sent) CapturedCount++;
                    return sent;
                }

                // ---------- 空闲命令提示符：SendInput(KEYEVENTF_UNICODE) ----------
                // 用 US 英文映射取字符 —— 不读布局、不读输入法，结果恒定可预测；
                // 而且 UNICODE 事件不经过输入法，命令名永远是英文。
                char ch = CharInjector.GetEnglishCharForKey(vkCode);
                if (ch == '\0')
                {
                    if (_debugLog) Dbg("跳过：VK=0x" + vkCode.ToString("X2") + " 无英文映射");
                    return false;
                }

                bool ok = CharInjector.SendUnicodeInput(ch);
                if (_debugLog) Dbg("SendUnicodeInput '" + ch + "' 结果=" + ok);
                if (ok) CapturedCount++;
                return ok;
            }
            catch (Exception) { return false; }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _injectGuard, 0);
            }
        }

        /// <summary>
        /// 「命令执行期间完全放行物理按键」的判据（即 <see cref="XimeConfig.XimeCommandKeyMode.PassThrough"/>）。
        ///
        /// 生产代码（<see cref="ShouldCaptureKeyCore"/> / <see cref="DbgWhyNot"/>）与
        /// 自测工具共用**同一处**逻辑，避免测试与实现各写一遍、日后各自漂移。
        ///
        /// 真值表（自测钉死）：
        ///   PostKey       + 命令执行中 → 接管（false，改由 OnKeyCaptured 投给焦点控件；★ 已证伪的旧默认档）
        ///   PassThrough   + 命令执行中 → 放行（true，物理键直达，输入法会看到）★ v1.0.2 无感修订版默认档
        ///   PassThrough   + 空闲提示符 → 接管（false）
        ///   PostKeyFrame  + 命令执行中 → 接管（false，改由 OnKeyCaptured 投给顶层窗口）
        ///   ReplayVk      + 命令执行中 → 接管（false，改由 OnKeyCaptured 重放真实 VK）
        ///   任意档        + 空闲提示符 → 接管（false）
        /// </summary>
        internal static bool ShouldPassThroughKey(XimeConfig config, string currentCommand)
        {
            if (config == null) return false;
            if (config.CommandKeyMode != XimeConfig.XimeCommandKeyMode.PassThrough) return false;
            return !string.IsNullOrEmpty(currentCommand);
        }

        /// <summary>
        /// 命令期间「投递窗口消息」的**目标窗口**（v1.0.2：<c>PostKeyFrame</c> 投顶层，其余投焦点）。
        ///
        ///   · PostKeyFrame → **前台顶层窗口**（`GetForegroundWindow()`），
        ///     取不到时退回 **AutoCAD 主窗口**（`_focus.AcadMainWindow`）；
        ///   · 其余（PostKey）→ 真实焦点控件（`GetRealFocusWindow()`），
        ///     拿不到时退回**命令栏控件**（`HostTextFrame.Resolve()`）。
        ///
        /// 两档目标不同的意义：PostKey 依赖"焦点控件可解析"，PostKeyFrame 不依赖 ——
        /// 焦点控件被第三方吃掉时，顶层窗口这一档仍能把按键送进消息泵。
        /// </summary>
        private IntPtr ResolvePostKeyTarget(XimeConfig.XimeCommandKeyMode mode)
        {
            if (ShouldPostToTopLevelWindow(mode))
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero) return fg;
                return _focus.AcadMainWindow;
            }
            var target = CharInjector.GetRealFocusWindow();
            return target != IntPtr.Zero ? target : HostTextFrame.Resolve();
        }

        /// <summary>是否把「投递窗口消息」打给**前台顶层窗口**（PostKeyFrame 档），
        /// 而不是焦点控件（PostKey 档）。纯判据，自测与实现共用。</summary>
        internal static bool ShouldPostToTopLevelWindow(XimeConfig.XimeCommandKeyMode mode)
        {
            return mode == XimeConfig.XimeCommandKeyMode.PostKeyFrame;
        }

        /// <summary>诊断：记录一次「按键被看到但未拦截」的原因。</summary>
        private void DbgWhyNot(int vkCode)
        {
            if (!_debugLog) return;
            if (!_isEngaged) { Dbg("  跳过: 未接管"); return; }
            if (!_config.Enabled) { Dbg("  跳过: 未启用"); return; }
            if (!KeysTakeOverEnabled)
            {
                Dbg("  跳过: 当前模式不接管按键（" + _config.WorkMode
                    + "；该输入法的中英态通道仍可用，不启用 v61 兜底）");
                return;
            }
            if (ShouldPassThroughKey(_config, _currentCommand))
            {
                // ★ v60：命令期间走到"放行"这一步有两种正当理由，日志要能区分：
                //   ① 焦点不在"输入法能组字的地方"（绘图区，天正 RYBZ 的常态）
                //      → 输入法没有组字上下文，放行是对的；
                //   ② 焦点虽然能组字，但该命令需要中文 / 正处于文字编辑态
                //      → 本来就必须把输入权交还输入法。
                //   两种都正确，这里只说清是哪一种（本方法只在 >= Debug 档被调用，
                //   多读一次焦点无妨；FocusIsComposableHost 是只读的，无副作用）。
                Dbg("  跳过: 命令执行期间放行物理键（" + _currentCommand + "，PassThrough 档；"
                    + (FocusIsComposableHost()
                        ? "焦点在命令栏/WPF 宿主上，但该命令需中文/处于文字编辑态 ⇒ 交还输入法"
                        : "焦点不在可组字控件上 ⇒ 输入法无从组字，按 v60 判据放行（天正 RYBZ 场景）")
                    + "）");
                return;
            }
            if (!_commandLineNeedsEnglish)
            {
                // ★ v59：与 ShouldCaptureKeyCore 共用同一套复核，防止"诊断"与"判据"漂移。
                IntPtr f;
                if (!ShouldReclaimEnglishNow(out f))
                    Dbg("  跳过: 命令栏不需英文（复核确认焦点确在文字编辑控件上）");
                else
                    Dbg("  跳过: 命令栏不需英文（复核认为本应抢回 —— 说明本键在更早的判据上就放行了）");
                return;
            }
            // ★ v35：CapsLock 不再是一票否决的理由（见 ShouldCaptureKeyCore 的说明）
            if (NativeMethods.IsCapsLockOn()) Dbg("  注: CapsLock 开启（注入字符按大写计算）");
            if (NativeMethods.IsCtrlPressed() || NativeMethods.IsAltPressed() || NativeMethods.IsWinPressed()) { Dbg("  跳过: 组合键"); return; }
            if (!CharInjector.IsPrintableKey(vkCode)) { Dbg("  跳过: 非可打印键"); return; }
            Dbg("  未拦截(原因未知)");
        }

        #endregion

        #region 命令事件

        /// <summary>
        /// 键盘焦点落到 AutoCAD 进程内的某个控件上。
        ///
        /// 用于识别「双击文字 → 进入编辑框输入内容」这类**不经过命令系统**的场景：
        /// 若焦点落在可编辑控件，且该控件**不是命令栏**，则判定为文字编辑态 → 允许中文。
        /// 若焦点回到命令栏，则退出编辑态 → 恢复英文接管。
        ///
        /// ★ 重要：退出条件必须**严格**。
        ///   实测（天正单行文字双击）焦点会在「编辑框 ⇄ 主窗口 ⇄ 子框架」之间
        ///   高频抖动，若一看到非编辑框就退出，会导致编辑态只维持几十毫秒，
        ///   用户根本来不及打字。因此：
        ///     · 进入：焦点 == 可编辑控件 且 不是命令栏
        ///     · 退出：**仅当焦点明确回到命令栏**（或编辑器被销毁）
        ///   中间态（主窗口 / MDIClient / 各种 Afx 框架）一律**维持**当前编辑态不变。
        /// </summary>
        private void OnFocusObjectChanged(IntPtr hwnd)
        {
            if (!_isEngaged || !_config.Enabled) return;
            if (!IsSupportedWorkMode(_config.WorkMode)) return;
            if (hwnd == IntPtr.Zero) return;
            if (hwnd == _lastFocusHwnd) return;      // 同一控件，忽略重复事件
            _lastFocusHwnd = hwnd;

            // 命令栏（命令行面板）整条窗口链必须排除 —— 否则「敲命令」会被误判成
            // 「编辑文字」，命令栏将无法输入英文（灾难性回归）。
            bool isCommandLine = HostTextFrame.IsCommandLineWindow(hwnd);
            bool isEdit = FocusMonitor.IsEditBoxWindow(hwnd);

            // ★★★ v63：动态输入（DYN）提示框 —— **必须先把它摘出来**。
            //   它内部是一枚**原生 `Edit`**（类名就叫 `Edit`），与"真编辑框"完全同形，
            //   但语义是**命令输入端**。不摘出来，下面两段都会把它误判：
            //     · `isEdit && !isCommandLine` 成立 ⇒ EnterTextEditMode ⇒ SwitchToChinese；
            //     · 钩子让路判据也会把它当成"外部文本编辑控件"。
            //   两者叠加就是用户症状：第 1 个字母正常、**第 2 个起变中文**。
            //   实测结构与日志原文见 HostTextFrame.IsDynamicInputWindow 的注释。
            bool isDynInput = HostTextFrame.IsDynamicInputWindow(hwnd);

            // 类名读取是一次 P/Invoke + 一次字符串分配，只在开日志时才做
            if (_debugLog)
            {
                Dbg("焦点变化: 0x" + hwnd.ToInt64().ToString("X") +
                    " 类名=" + FocusMonitor.GetWindowClass(hwnd) +
                    " 是编辑框=" + isEdit +
                    " 是命令栏=" + isCommandLine +
                    " 是动态输入=" + isDynInput);
            }

            if (isDynInput)
            {
                // ★★★ v63：动态输入框 = **命令输入端**，绝不进文字编辑态。
                //
                //   ★ 但**不能无条件切英文**：文字类命令（TEXT / MTEXT / DTEXT…）在
                //     动态输入框里输入的**就是文字内容本身**（DYN 打开时，
                //     "输入文字:" 这类提示与内容都在这个框里）。那时切英文 = 灾难。
                //   ⇒ 仍以命令白名单为准 —— 与 v55 闸门（RYBZ 那条）同一口径：
                //     只有「空闲 / 非文字类命令」才把它当命令输入端处理。
                bool needsChinese = _config.MatchChinese(_currentCommand)
                                    != XimeConfig.ChineseNeed.None;
                if (!needsChinese)
                {
                    // 若此前在真编辑框里留下的中文态还挂着，立刻收回来；
                    // 否则至少把"命令栏需要英文"的标志置起（ForceEnglish 幂等、只置标志）。
                    if (_inTextEditMode) ExitTextEditMode("焦点在动态输入框（命令输入端，非文字内容）");
                    else ForceEnglish();
                }
                else if (_debugLog)
                {
                    Dbg("焦点在动态输入框，但命令 " + _currentCommand + " 是文字类 → 保持中文");
                }
            }
            else if (isEdit && !isCommandLine)
            {
                // ★★★ v55 闸门：命令执行期间，天正的**内部参数输入框**（实测类名 `Edit`）
                //   绝不能被当成"用户要输入中文文字" —— 否则会在二级命令（A/E/D/F/X/Y）
                //   期间主动把输入法切成中文，把按键喂给输入法。
                //   详见 ShouldEnterTextEditModeForFocus 的说明（RYBZ 真根因）。
                bool needsChinese = _config.MatchChinese(_currentCommand)
                                    != XimeConfig.ChineseNeed.None;
                if (!ShouldEnterTextEditModeForFocus(_currentCommand != null, needsChinese))
                {
                    if (_debugLog)
                        Dbg("焦点落在编辑框，但命令 " + _currentCommand
                            + " 正在执行且非文字类 → **不进入文字编辑态**（v55 闸门）");
                    // 命令期间保持英文接管：确认命令栏处于英文（不切输入法，只置标志）。
                    if (_inTextEditMode) ExitTextEditMode("命令执行期间不进入文字编辑态（v55）");
                    else ForceEnglish();
                    return;   // ★ 直接返回：不执行下面的补做逻辑（避免又把输入法切走）
                }

                // ★ v46：把靶点一起传进去 —— 每次焦点落到一个**新的**文字编辑控件，
                //   都是一次全新的"马上要输入中文"场景，必须重新确保中文。
                //   旧实现在这里只调一个无参方法，而那个方法内部第一行就是
                //   `if (_inTextEditMode) return;` → 连续双击修改时后续全被吞掉。
                EnterTextEditMode(hwnd);
            }
            else if (isCommandLine && _inTextEditMode)
            {
                // ★ 只有焦点**明确回到命令栏**才退出编辑态。
                //   焦点短暂跳到主窗口 / MDIClient / Afx 框架时维持不变 ——
                //   这些是焦点抖动，不代表编辑结束。
                ExitTextEditMode("焦点回到命令栏");
            }
            // else: 中间态 → 维持当前编辑态不变

            // ★ 焦点在 CAD 内部发生了迁移 —— 此刻前台通常已经回到 AutoCAD，
            //   是补做"待补输入法切换"的最佳时机（比 500ms 轮询更及时，
            //   用户合上面板后敲的第一个字符就能吃到正确的状态）。
            //   不满足条件时本调用直接返回，代价可忽略。
            TryFlushPendingSwitch("焦点回到 CAD");
            // ★ v39：焦点回到 CAD 也正是补做"延迟还原"的最佳时机。
            TryFlushPendingRestore("焦点回到 CAD");
            // ★ v42：无视模式下"等不到编辑控件"的确保中文 —— 此刻焦点刚落到
            //   编辑控件上，正是补做的最佳时机（比 500ms 轮询快得多）。
            TryFlushPendingEnsure("焦点事件");

            // ★★★ v62：焦点一变就重新评估「命令期间无视输入法」。
            //   焦点移进/移出**外部文本编辑控件**（图层特性管理器的名称框、
            //   各类对话框输入框…）时必须**立刻**解除/还原那层关联：
            //     · 移进去 ⇒ 必须还原，否则用户在那个框里打不出中文；
            //     · 移出来 ⇒ 回到命令栏/绘图区，重新按判据决定。
            //   500ms 轮询虽然也能兜住，但那意味着"点进名称框后最多 0.5 s 内
            //   仍打不出中文"—— 用户手快就正好撞上（报告用户正是"改图层名"这种短操作）。
            //   SyncImeBypass 幂等（状态已一致时直接返回、不写日志），可放心常调。
            SyncImeBypass("焦点变化");
        }

        /// <summary>
        /// 命令开始 —— 两种模式共用的入口，靠 <see cref="ForceEnglish"/> /
        /// <see cref="SwitchToChinese"/> 这两个出口分流到各自的动作。
        /// </summary>
        public void OnCommandWillStart(string commandName)
        {
            if (_debugLog)
                Dbg("CommandWillStart: " + commandName +
                    " (接管=" + _isEngaged + " 启用=" + _config.Enabled + ")");
            if (!_config.Enabled) return;
            if (!IsSupportedWorkMode(_config.WorkMode)) return;
            // ★★ v6.3：引擎没在跑，命令事件与我们毫无关系。
            //   `_started` 为 false 只有两种场景：面板点了「卸载」（= 用户要求插件停下来）
            //   与引擎尚未 Start。此前这里不查，于是"卸载之后"每敲一条命令都会被当成
            //   一条"该生效却没生效"的命令去诊断 —— 现场日志里那条
            //   `[旁路诊断] 命令=quit **未能解除关联**：接管=False` 就是这么来的
            //   （卸载把引擎 Stop 了，`_isEngaged` 必然是 false，报它毫无意义，纯属刷屏）。
            //   诊断行必须稀有才有人看。
            if (!_started) return;

            var cmd = NormalizeCommand(commandName);
            _currentCommand = cmd;

            // ★★★ v6.3：**「有命令正在执行」本身就是"用户正在 CAD 里操作"的最硬证据**，
            //   比任何前台探测都可靠 —— 命令事件只会在本进程的活动文档上触发。
            //   若此刻我们还没接管（`_isEngaged == false`），旁路判据 `ShouldEngage`
            //   第 3 条会立刻把整条命令挡下，整条命令都没有旁路（候选窗照弹）。
            //   这种"命令在跑却没接管"的窗口真实存在：NETLOAD / 自动加载那一刻前台
            //   可能不是 CAD（现场日志：加载时 `当前焦点 = Chrome_WidgetWin_1`），
            //   于是 `Engage` 要等到用户把焦点切回 CAD 才发生；若用户在这之前就开始
            //   敲命令（含脚本、宏、启动组），命令开始时就是"未接管"。
            //   ★ 这里补做一次接管，语义与 `FocusMonitor.CadActivated` 完全一致
            //     （都走同一个 `Engage()`），只是把时机提前到"命令已经证明用户在 CAD 里"。
            //   ★ 必须排在 `SyncImeBypass` 之前 —— 否则旁路还是会被"未接管"挡下。
            if (!_isEngaged)
            {
                _focus.EvaluateState(raiseEvents: true);
                if (!_isEngaged && _focus.IsCadActive) Engage();
            }

            // ★★ v6.2：「记下命令名 + 同步旁路」**必须在"是否已接管"这道门之前**。
            //
            //   现场日志实测（2026-10-06 23:50）：用户执行 `XIMEBYPASS 0` 之后紧接着敲
            //   `ry RYBZ`，**整条命令没有任何"解除/还原"日志** —— 旁路全程没生效。
            //   根因就在这道门前：`Engage/Disengage` 会随用户切窗**一秒一组地抖**
            //   （启动日志里 23:50:22 退出 → 23:50:26 重新进入），命令开始那一刻恰好落在
            //   "未接管"窗口里就提前 return：
            //     · `_currentCommand` 没被记下 ⇒ 之后焦点回到 CAD、`进入接管` 与 500ms
            //       `轮询兜底` 两条补做路径**都看不到"有活动命令"** ⇒ 永远补不上
            //       ⇒ 整条命令都没有旁路 ⇒ **候选窗照弹**（用户报的就是这个症状）。
            //   ★ 而旁路判据 `ShouldEngage` 内部**本来就查 `_isEngaged`**，
            //     在调用方再挡一道纯属重复，且这道重复会**把补做的机会一并掐死**。
            SyncImeBypass("命令开始 " + cmd);
            DiagnoseBypassSkip("命令开始 " + cmd);

            if (!_isEngaged) return;

            var need = _config.MatchChinese(cmd);

            // ★ 关键：中文类命令（TEXT / MTEXT / DDEDIT ...）在「命令开始」时就
            //   停止拦截 —— 因为命令运行期间用户输入的是**中文文字内容**，
            //   此时必须把输入权交还给输入法，否则用户打的拼音会被当成英文字母。
            //   （此前是在 CommandEnded 才放手，导致命令运行期间打不了中文 —— 已修复）
            if (need != XimeConfig.ChineseNeed.None)
            {
                SwitchToChinese();
                // ★ 文字类命令 → 马上要输入中文内容。若输入法已被用户切到英文，
                //   在这一刻主动切回中文（用户实测：切过英文后 text/ttext 打不出中文）。
                //   注意：这里是"命令开始"的入口，命令运行期间不会再切换，
                //   用户在命令中自己按 Shift 切英文打标注不会被打断。
                //
                //   ★★ 但只有**精确命中白名单**的命令才在这里切。
                //
                //   白名单是作者/用户明确确认过的，提前切是对的。
                //   而「关键词兜底命中」只是按名字猜的，**绝不能提前切**：
                //   一旦猜错，输入法被切到中文，用户在命令执行期间按的选项键
                //   （如天正 RYBZ 选块后的 A 旋转90° / E 放大 / D 缩小 /
                //     F 左右翻转 / X·Y 偏移）会被输入法吃掉，天正根本收不到 →
                //   命令失效。这正是「AutoCAD 2022 + 天正 T20 v10 任意布置
                //   二级命令失灵、只在命令栏留个小写字母」的根因
                //   （RYBZ 曾被 "bz" 词根误判为标注类命令）。
                //   猜测命中的命令，等焦点真正落到文字编辑框时再切 —— 见 EnterTextEditMode。
                if (need == XimeConfig.ChineseNeed.Exact)
                    EnsureChineseForTextInput("文字类命令 " + cmd);
                else
                    Dbg("命令 " + cmd + " 仅由关键词兜底命中：不提前切中文，等进入文字编辑态再切");
            }
            else if (!_inTextEditMode)
                ForceEnglish();
            // 注：若正处于「文字编辑态」（焦点在编辑框），即使来了非中文命令
            // 也不夺回英文接管 —— 否则会把用户正在输入的中文内容打断。

            // 命令开始时刷新一次命令栏控件缓存（可能刚切换文档）
            HostTextFrame.Invalidate();
        }

        public void OnCommandEnded(string commandName, bool cancelledOrFailed)
        {
            if (_debugLog)
                Dbg("CommandEnded: " + commandName + " 取消或失败=" + cancelledOrFailed +
                    " (接管=" + _isEngaged + " 启用=" + _config.Enabled + ")");
            if (!_config.Enabled) return;
            if (!IsSupportedWorkMode(_config.WorkMode)) return;
            // ★★ v6.3：引擎没在跑（面板点了「卸载」/ 尚未 Start）→ 命令事件与我们无关。
            //   安全性：`_started == false` 必然意味着 `Stop()` 已经跑过，
            //   而 `Stop()` 末尾有**无条件**的 `_imeBypass.Disengage()` 硬保险，
            //   所以这里早退绝不会把"输入法关联被解除着"留在用户机器上。
            if (!_started) return;

            // ★★ v6.2：清命令名 + 还原旁路同样**不受"是否已接管"限制**（理由见 OnCommandWillStart）。
            //   若命令恰好在"未接管"窗口里结束，旧写法会把 `_currentCommand` 永久留在非空，
            //   而它正是 `ShouldPassThroughKey`（"命令正在执行"的唯一依据）的输入 ⇒
            //   按键会被一直放行。
            _currentCommand = null;

            // ★ v6：命令结束 → 立刻还原窗口与输入法的关联（**在下面的早退之前**，
            //   否则"命令结束但仍在文字编辑态"这条路径会把关联永久留在解除状态）。
            SyncImeBypass("命令结束 " + commandName);

            if (!_isEngaged) return;

            // ★ 编辑态由「焦点信号」负责退出（焦点回到命令栏即视为编辑结束），
            //   命令事件不越权干预 —— 否则「双击 ttext 编辑」过程中天正触发的
            //   CommandCancelled 会把编辑态打断，中文又打不出来了。
            //   兜底：若编辑态迟迟不退出，由定时器 SecuritySweep 清理（见下）。
            if (_inTextEditMode)
            {
                Dbg("CommandEnded: 处于文字编辑态 → 保持（等焦点回命令栏）");
                return;
            }

            // 命令结束后，命令栏回到「等待下一个命令」状态 —— 一律恢复英文拦截，
            // 保证下一个命令（无论当前输入法处于什么状态）都能正常输入英文。
            //   注：取消 / 失败同样按此处理，因为用户通常要重新敲命令。
            //   切换输入法模式下，这里就是"输入完成或退出 → 切回英文并保持"的落点。
            //   ★ v40：命令已结束 → 焦点不再迁移 → 立刻切，让用户紧接着敲的下一个
            //     命令名不会被中文输入法吃掉。
            ForceEnglish(true);
        }

        public void OnCommandCancelled(string commandName)
        {
            if (_debugLog) Dbg("CommandCancelled: " + commandName + " (接管=" + _isEngaged + ")");
            // ★★ v6.3：引擎没在跑 → 与我们无关（安全性同 OnCommandEnded）。
            if (!_started) return;

            // ★★ v6.2：同上（理由见 OnCommandWillStart）。
            _currentCommand = null;

            // ★ v6：取消同样要还原（用户按 Esc 时最常见）
            SyncImeBypass("命令取消 " + commandName);

            if (!_isEngaged) return;

            if (_inTextEditMode)
            {
                Dbg("CommandCancelled: 处于文字编辑态 → 保持（等焦点回命令栏）");
                return;
            }

            // 取消后回到英文，方便用户立刻重新敲命令。
            //   ★ v40：同上，不再排队等待 —— 用户按 Esc 后往往马上重敲。
            ForceEnglish(true);
        }

        /// <summary>
        /// 编辑态兜底巡检（由轮询定时器调用）。
        ///
        /// 目的：防止编辑态因「焦点不回命令栏」而永久卡住（例如用户点了主窗口
        /// 空白处结束编辑）。判定依据：当前前台焦点**已不在任何可编辑控件**上，
        /// 且已持续一段时间 → 判定编辑结束，恢复英文接管。
        /// </summary>
        private void EditModeSecuritySweep()
        {
            if (!_inTextEditMode) return;

            // 注：巡检用「严格判断」——拿不到焦点句柄也算作已离开编辑框
            //（与按键路径的保守策略相反），确保编辑态最终一定能退出。
            bool inEdit;
            try
            {
                var focus = CharInjector.GetRealFocusWindow();
                inEdit = focus != IntPtr.Zero
                      && FocusMonitor.IsEditBoxWindow(focus)
                      && !HostTextFrame.IsCommandLineWindow(focus);
            }
            catch (Exception) { inEdit = true; }

            if (inEdit)
            {
                _editModeIdleTicks = 0;
                return;
            }

            // 不在编辑框上：累计空闲，超过阈值才退出（容忍焦点抖动）
            _editModeIdleTicks++;
            if (_editModeIdleTicks >= 4)   // 约 2 秒（定时器 500ms）
            {
                ExitTextEditMode("兜底巡检：长时间未在编辑框");
            }
        }

        #endregion

        private static string NormalizeCommand(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            return name.Trim().TrimStart('_', '-').ToLowerInvariant();
        }

        /// <summary>
        /// 按当前配置重新对齐接管状态。设置界面每次改动（开关 / 模式）后都会调用。
        ///
        /// ★ 必须处理「接管期间切换模式」：两种模式的动作完全相反
        ///   （一边装钩子、一边不装），若只是简单地重新 Engage，
        ///   `_isEngaged` 已经为 true 会直接返回 —— 结果是旧模式的钩子继续挂着、
        ///   新模式的初始化从没跑过。所以**先把上一次彻底收尾**（Disengage，
        ///   里面会按旧模式卸载钩子 / 还原输入法）再按新模式接管。
        /// </summary>
        public void Refresh()
        {
            if (_disposed) return;

            bool wantRun = _config.Enabled && IsSupportedWorkMode(_config.WorkMode);

            // ★★ v42：该跑却没跑（被 Stop 过）→ 必须把轮询与焦点监听一并重新拉起来。
            //   否则会变成"钩子装上了、焦点监听却停着"的半死状态：无视模式下表现为
            //   **永远进不了文字编辑态**，用户双击文字打不出中文。
            //   典型触发路径：点「卸载」（内部 Stop）→ 面板关闭 → ShowSettings 里
            //   的 Refresh 拿着复位后的 Enabled=true 去 Engage。
            //   判据抽成 ShouldRestartStoppedEngine，自测可钉住。
            if (ShouldRestartStoppedEngine(wantRun, _started, _disposed))
            {
                Dbg("Refresh：引擎此前已停止（Stop）而现在配置要求运行 → 重新启动（含轮询/焦点监听）");
                Start();
            }

            if (wantRun)
            {
                if (_isEngaged && _engagedMode != _config.WorkMode)
                    Disengage();          // 模式变了 → 按旧模式收尾（命令状态保留）

                if (_focus.IsCadActive)
                    Engage();
                else
                    Disengage();          // 暂时不在 CAD 前台（命令状态保留）
            }
            else
            {
                // ★ v1.0.2：配置不让跑（被关掉 / 工作模式非法）＝ 场景结束 → 清命令状态。
                Disengage(engineStopping: true);
            }
        }

        /// <summary>
        /// 「该运行、却处于已停止态」→ 需要把引擎真正重启（v42 纯判据，自测钉住）。
        ///
        /// ★ 这条判据存在的唯一理由是那类"半死状态"：
        ///   <see cref="Stop"/> 停了轮询与焦点监听却不动配置，之后
        ///   <see cref="Refresh"/> 只看 Enabled 就会去 Engage 一个停掉的引擎 ——
        ///   键盘钩子重新装上（按键仍被吞），但焦点事件永远不会到来，
        ///   于是**永远进不了文字编辑态**：无视模式下用户双击文字打不出中文。
        /// </summary>
        /// <param name="wantRun">配置是否要求运行（Enabled 且工作模式合法）。</param>
        /// <param name="started">引擎的轮询/焦点监听当前是否在跑。</param>
        /// <param name="disposed">引擎是否已释放（已释放则一律不再启动）。</param>
        internal static bool ShouldRestartStoppedEngine(bool wantRun, bool started, bool disposed)
        {
            if (disposed) return false;
            return wantRun && !started;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _pollTimer.Dispose();
            _focus.Dispose();
            _hook.Dispose();
        }
    }
}
