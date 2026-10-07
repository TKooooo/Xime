using System;
using System.Collections.Generic;
using Xime.Core.Native;

namespace Xime.Core
{
    /// <summary>输入法当前的转换模式状态。</summary>
    public enum ImeMode
    {
        /// <summary>无法探测（当前输入法不响应 IMM32）。</summary>
        Unknown = -1,
        /// <summary>英文（字母数字）模式。</summary>
        English = 0,
        /// <summary>中文（本地语言）模式。</summary>
        Chinese = 1
    }

    /// <summary>
    /// 输入法控制器：读取 / 切换「中英转换模式」。
    ///
    /// 关键约束（务必遵守）：
    ///   1. 绝不调用 ActivateKeyboardLayout / LoadKeyboardLayout 切换键盘布局；
    ///   2. 切换中英一律通过「直接设置转换状态」或「模拟 Shift」实现，
    ///      这与用户手动按 Shift 完全等价，视觉上输入法图标不变，布局不变；
    ///   3. 部分输入法不响应 IMM32 查询接口（TSF 输入法），甚至响应了也是
    ///      兼容层缓存的旧值 —— 此时本类优先走
    ///      <see cref="NativeMethods.IMC_GETCONVERSIONMODE"/> 通道（直接问 IME 窗口）；
    ///      两条都不行才报告 Unknown，由调用方决定是否"盲按"一次 Shift。
    ///
    /// ★ 两条并行通道（顺序固定：先问 IME 窗口本身，再读 IMM32 兼容层兜底）：
    ///     ① IME 消息    —— <c>SendMessageTimeout(IME 窗口, WM_IME_CONTROL,
    ///                       IMC_GET/SETCONVERSIONMODE)</c>；
    ///     ② IMM32 上下文 —— <c>ImmGetContext</c> + <c>ImmGet/SetConversionStatus</c>。
    ///   通道①（IME 消息）是"切换输入法模式"在**国产 / TSF 输入法**上能否工作的关键：
    ///   这类输入法常把 IMM32 兼容层关掉（此时通道②恒为 Unknown），且即便兼容层
    ///   还开着，它缓存的也往往是旧状态 —— 只有直接问 IME 窗口才能拿到真实中英态。
    ///   所以读取/写入都优先走 IME 消息通道，读不出再退回 IMM32。
    /// </summary>
    public sealed class ImeController
    {
        private readonly object _sync = new object();

        /// <summary>最近一次已知的中英模式（缓存，减少 API 调用）。</summary>
        private ImeMode _lastKnown = ImeMode.Unknown;

        /// <summary>最近一次已知的中英模式。</summary>
        public ImeMode LastKnownMode
        {
            get { lock (_sync) { return _lastKnown; } }
        }

        #region 状态读取

        /// <summary>
        /// 读取指定窗口当前输入法的中英模式。
        /// 传入 IntPtr.Zero 时自动使用前台窗口。
        ///
        /// 重要：会依次尝试「给定窗口 → 其真实焦点控件 → 前台窗口」，
        /// 因为 AutoCAD 的输入法上下文挂在命令栏子控件上，而主框架窗口
        /// ImmGetContext 可能拿到一个与当前输入无关的 IMC。
        /// </summary>
        public ImeMode GetMode(IntPtr hwnd)
        {
            foreach (var h in CandidateWindows(hwnd))
            {
                var m = GetModeAt(h);
                if (m != ImeMode.Unknown)
                    return m;
            }
            return ImeMode.Unknown;
        }

        /// <summary>列出候选窗口：给定窗口 → 真实焦点 → 前台窗口。</summary>
        private static IEnumerable<IntPtr> CandidateWindows(IntPtr hwnd)
        {
            var seen = new HashSet<IntPtr>();

            if (hwnd != IntPtr.Zero && seen.Add(hwnd))
                yield return hwnd;

            IntPtr focus;
            try { focus = CharInjector.GetRealFocusWindow(); }
            catch (Exception) { focus = IntPtr.Zero; }
            if (focus != IntPtr.Zero && seen.Add(focus))
                yield return focus;

            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && seen.Add(fg))
                yield return fg;
        }

        /// <summary>
        /// 在**指定窗口本身**上读取中英模式：**不做**焦点 / 前台回退。
        ///
        /// 用途：AutoCAD 已经失焦或被最小化时，读取「它自己」的输入法状态。
        /// 此时若走 <see cref="GetMode"/> 的候选链，焦点/前台窗口已经变成
        /// **别的程序** —— 读到的是别人的输入法状态，据此去"还原"就会改错对象。
        /// </summary>
        public ImeMode GetModeOnWindow(IntPtr hwnd)
        {
            return GetModeAt(hwnd);
        }

        /// <summary>
        /// 在单个窗口上读取中英模式（不做候选回退）。
        ///
        /// ★ 通道优先级：**通道②（IME 消息）优先，通道①（IMM32 上下文）兜底**。
        ///
        /// 为什么反过来（此前是通道①优先）—— 用户实机反馈「切换输入法模式不起作用」：
        ///   现代输入法（微软拼音、搜狗等）普遍走 TSF。TSF 输入法的**真实**中英状态
        ///   由 IME 窗口自己维护，而 IMM32 的 <c>ImmGetConversionStatus</c> 读的是
        ///   兼容层的上下文缓存 —— 它可能停在「进入前」的旧值上，与真实状态脱节。
        ///   XIMEPROBE 实测：同一时刻通道①读 English、通道②读 Chinese，
        ///   而用户此刻明明处于中文输入。若信通道①，插件就会误判「已经是英文」，
        ///   于是进入 CAD 该切英文时不动作、执行文字命令该切中文时也不动作，
        ///   整个切换输入法模式形同虚设。
        ///   <c>WM_IME_CONTROL / IMC_GETCONVERSIONMODE</c> 是**直接问 IME 窗口本身**，
        ///   对 TSF 输入法而言这才是真实状态；通道②读不出（IME 窗口未取到 / 不支持）
        ///   时再退回通道①，两者语义相同（都是 IME_CMODE_NATIVE 位），互不冲突。
        /// </summary>
        private ImeMode GetModeAt(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return ImeMode.Unknown;

            // 通道②：IME 消息（直接问 IME 窗口，TSF 输入法的真实状态在这里）
            var viaIme = GetModeViaImeControl(hwnd);
            if (viaIme != ImeMode.Unknown)
                return viaIme;

            // 通道①：IMM32 上下文（通道②不可用时的兜底）
            return GetModeViaContext(hwnd);
        }

        /// <summary>通道①：通过 ImmGetContext + ImmGetConversionStatus 读取。</summary>
        private ImeMode GetModeViaContext(IntPtr hwnd)
        {
            IntPtr himc = IntPtr.Zero;
            try
            {
                himc = NativeMethods.ImmGetContext(hwnd);
                if (himc == IntPtr.Zero)
                {
                    // 该窗口没有输入法上下文，常见于输入法使用了 TSF 或自绘 UI。
                    return ImeMode.Unknown;
                }

                int conv, sentence;
                if (!NativeMethods.ImmGetConversionStatus(himc, out conv, out sentence))
                    return ImeMode.Unknown;

                return (conv & NativeMethods.IME_CMODE_NATIVE) != 0
                    ? ImeMode.Chinese
                    : ImeMode.English;
            }
            catch (Exception)
            {
                return ImeMode.Unknown;
            }
            finally
            {
                if (himc != IntPtr.Zero)
                    NativeMethods.ImmReleaseContext(hwnd, himc);
            }
        }

        /// <summary>
        /// 通道②：向 IME 窗口发 WM_IME_CONTROL / IMC_GETCONVERSIONMODE 读取中英模式。
        ///
        /// 返回值的低字是 conversion mode，<see cref="NativeMethods.IME_CMODE_NATIVE"/>
        /// 置位即中文。输入法不支持该子命令时会返回 -1（0xFFFFFFFF），一并按 Unknown 处理。
        /// </summary>
        private static ImeMode GetModeViaImeControl(IntPtr hwnd)
        {
            try
            {
                var imeWnd = NativeMethods.ImmGetDefaultIMEWnd(hwnd);
                if (imeWnd == IntPtr.Zero)
                    return ImeMode.Unknown;

                IntPtr unused;
                var ret = NativeMethods.SendMessageTimeout(
                    imeWnd,
                    NativeMethods.WM_IME_CONTROL,
                    new IntPtr(NativeMethods.IMC_GETCONVERSIONMODE),
                    IntPtr.Zero,
                    NativeMethods.SMTO_ABORTIFHUNG,
                    NativeMethods.ImeMsgTimeoutMs,
                    out unused);

                // 超时 / 目标线程挂起 → 调用失败，返回 0
                if (ret == IntPtr.Zero)
                    return ImeMode.Unknown;

                long v = ret.ToInt64();
                // 输入法不支持该子命令时的约定返回值
                if (v == -1 || v == 0xFFFFFFFFL)
                    return ImeMode.Unknown;

                int conv = (int)(v & 0xFFFF);
                return (conv & NativeMethods.IME_CMODE_NATIVE) != 0
                    ? ImeMode.Chinese
                    : ImeMode.English;
            }
            catch (Exception)
            {
                return ImeMode.Unknown;
            }
        }

        /// <summary>读取前台窗口的中英模式。</summary>
        public ImeMode GetCurrentMode()
        {
            var mode = GetMode(IntPtr.Zero);
            if (mode != ImeMode.Unknown)
            {
                lock (_sync) { _lastKnown = mode; }
            }
            return mode;
        }

        #endregion

        #region 模式切换

        /// <summary>
        /// 直接设置中英转换模式（优先使用 ImmSetConversionStatus）。
        /// 该方式不改变键盘布局，是最干净的做法；但并非所有输入法都支持。
        ///
        /// ★ 会把操作靶点换成「真实焦点控件」—— AutoCAD 命令栏的 IME 上下文
        ///   挂在焦点控件上，而不是主框架窗口。**CAD 已经失焦时不要用这个方法**，
        ///   那时焦点属于别的程序；请改用 <see cref="RestoreOnWindow"/>。
        /// </summary>
        /// <returns>true 表示已成功设置。</returns>
        public bool SetModeDirect(IntPtr hwnd, ImeMode mode)
        {
            if (hwnd == IntPtr.Zero)
                hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;

            // 优先在「真实焦点控件」上操作 —— AutoCAD 命令栏的 IME 上下文在这里
            var target = hwnd;
            try
            {
                var focus = CharInjector.GetRealFocusWindow();
                if (focus != IntPtr.Zero)
                    target = focus;
            }
            catch (Exception) { }

            return SetModeDirectOnWindow(target, mode);
        }

        /// <summary>
        /// 在**指定窗口本身**上直设中英模式，**不做**任何焦点 / 前台回退。
        ///
        /// ★ 通道优先级与读取端一致：**通道②（IME 消息）优先，通道①（IMM32）兜底**。
        ///   理由见 <see cref="GetModeAt"/>：TSF 输入法的真实状态在 IME 窗口里，
        ///   先写 <c>IMC_SETCONVERSIONMODE</c> 才能真的把状态切过去；若输入法
        ///   不走 TSF、不响应 IME 消息，再退回 <c>ImmSetConversionStatus</c>。
        ///   只读不猜：全程不会模拟 Shift（所以对"当前是不是目标态"没有把握时，
        ///   调用方应先探测；本方法内部也会探测一次，已是目标就原样返回成功）。
        /// </summary>
        public bool SetModeDirectOnWindow(IntPtr hwnd, ImeMode mode)
        {
            if (hwnd == IntPtr.Zero || mode == ImeMode.Unknown)
                return false;

            // ---------- 通道②：IME 消息（TSF 输入法的真实写入点）----------
            if (SetModeViaImeControl(hwnd, mode))
            {
                lock (_sync) { _lastKnown = mode; }
                return true;
            }

            // ---------- 通道①：IMM32（通道②不可用时的兜底）----------
            IntPtr himc = IntPtr.Zero;
            try
            {
                himc = NativeMethods.ImmGetContext(hwnd);

                // 初值必须有：短路求值下编译器无法证明 out 参数一定被赋值
                int conv = 0, sentence = 0;
                bool haveStatus = himc != IntPtr.Zero
                    && NativeMethods.ImmGetConversionStatus(himc, out conv, out sentence);

                if (haveStatus)
                {
                    int want = conv;
                    if (mode == ImeMode.Chinese)
                        want |= NativeMethods.IME_CMODE_NATIVE;
                    else
                        want &= ~NativeMethods.IME_CMODE_NATIVE;

                    if (want == conv)
                    {
                        // 已经是目标态 —— 一个字节都不写，严格保持现状
                        lock (_sync) { _lastKnown = mode; }
                        return true;
                    }

                    if (NativeMethods.ImmSetConversionStatus(himc, want, sentence))
                    {
                        lock (_sync) { _lastKnown = mode; }
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // 落入失败返回
            }
            finally
            {
                if (himc != IntPtr.Zero)
                    NativeMethods.ImmReleaseContext(hwnd, himc);
            }

            return false;
        }

        /// <summary>
        /// 通过 WM_IME_CONTROL / IMC_SETCONVERSIONMODE 设置中英模式。
        ///
        /// ★ 只改 <see cref="NativeMethods.IME_CMODE_NATIVE"/> 这一位：
        ///   转换模式里还装着全角/半角、中文标点等标志，整字覆写会顺手把
        ///   用户的标点习惯改掉（这是很隐蔽的"改了别的东西"）。
        ///   所以**先直接读回原始 conversion mode 低字**，只翻转目标位。
        ///
        ///   注意：不能用 <see cref="GetModeViaImeControl"/> 读"当前值"——
        ///   它返回的是已折叠的中英枚举（Chinese/English），把全角/半角、
        ///   标点等其它位全丢了；据此写回等于把那些位清零。必须读原始低字。
        /// </summary>
        private static bool SetModeViaImeControl(IntPtr hwnd, ImeMode mode)
        {
            try
            {
                var imeWnd = NativeMethods.ImmGetDefaultIMEWnd(hwnd);
                if (imeWnd == IntPtr.Zero)
                    return false;

                // ★ 直接读回原始 conversion mode 低字（保留全角/半角/标点等其它位）
                IntPtr unused;
                var cur = NativeMethods.SendMessageTimeout(
                    imeWnd,
                    NativeMethods.WM_IME_CONTROL,
                    new IntPtr(NativeMethods.IMC_GETCONVERSIONMODE),
                    IntPtr.Zero,
                    NativeMethods.SMTO_ABORTIFHUNG,
                    NativeMethods.ImeMsgTimeoutMs,
                    out unused);

                if (cur == IntPtr.Zero)
                    return false;
                long v = cur.ToInt64();
                if (v == -1 || v == 0xFFFFFFFFL)
                    return false;   // 不支持该子命令

                // 只翻转 NATIVE 位，其余位原样保留
                int conv = (int)(v & 0xFFFF);
                if (mode == ImeMode.Chinese)
                    conv |= NativeMethods.IME_CMODE_NATIVE;
                else
                    conv &= ~NativeMethods.IME_CMODE_NATIVE;

                var ret = NativeMethods.SendMessageTimeout(
                    imeWnd,
                    NativeMethods.WM_IME_CONTROL,
                    new IntPtr(NativeMethods.IMC_SETCONVERSIONMODE),
                    new IntPtr(conv & 0xFFFF),          // 低字 = conversion，高字 = sentence(0)
                    NativeMethods.SMTO_ABORTIFHUNG,
                    NativeMethods.ImeMsgTimeoutMs,
                    out unused);

                if (ret == IntPtr.Zero)
                    return false;

                // 复核：写进去是否真的生效
                return GetModeViaImeControl(hwnd) == mode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 把"**指定窗口**"的输入法还原到给定状态 —— 供 AutoCAD 退出 / 最小化 /
        /// 失焦时恢复用户的默认输入法状态使用。
        ///
        /// 与 <see cref="SwitchTo"/> 的三点关键差别：
        ///   1. **只碰传入的这个窗口**，绝不回退到焦点 / 前台窗口。
        ///      此刻 CAD 已经失去前台，焦点属于别的程序；若按焦点设置，
        ///      改的就是**别人**的输入法状态（这是必须避免的越界行为）。
        ///   2. **绝不模拟 Shift**。Shift 是系统级热键，会落到当前前台程序上，
        ///      同样等于去动别人的输入法。
        ///   3. 因此探测不可用时**直接返回 false**（做不到就如实说做不到），
        ///      绝不"盲切"。
        /// </summary>
        /// <returns>true 表示已确认落在目标状态。</returns>
        public bool RestoreOnWindow(IntPtr hwnd, ImeMode target)
        {
            if (hwnd == IntPtr.Zero || target == ImeMode.Unknown)
                return false;

            lock (_sync)
            {
                var actual = GetModeAt(hwnd);

                if (actual == target)
                {
                    _lastKnown = target;
                    return true;
                }

                if (actual == ImeMode.Unknown)
                {
                    // 读不出状态 → 不写。写进去也无从确认，而猜错会把用户的
                    // 输入法锁在错误状态上，比不动作更糟。
                    _lastKnown = ImeMode.Unknown;
                    return false;
                }

                if (SetModeDirectOnWindow(hwnd, target))
                    return GetModeAt(hwnd) == target;

                return false;
            }
        }

        /// <summary>
        /// 切换动作是**用哪种方式**达成的 —— 这个区分至关重要，不能只返回 true/false。
        ///
        /// ★★ 现场事故（v33 实机日志）：
        ///   `SwitchTo` 在"不能发真实按键"时会退回 <see cref="SetModeDirectOnWindow"/>，
        ///   而直设通道对这款输入法**报告成功、实际无效**（日志记 `结果=True`，
        ///   可命令栏依旧打出中文）。调用方却据此认定"已经切到英文了"，
        ///   于是后续所有请求都被"已在目标态"短路掉，**再也不去发那唯一有效的
        ///   真实 Shift** —— 用户看到的就是「切换输入法模式还是不行」。
        ///   所以：**"直设成功"与"真实 Shift 成功"的语义完全不同，必须分开上报。**
        /// </summary>
        public enum ImeSwitchManner
        {
            /// <summary>没能达成目标。</summary>
            Failed = 0,

            /// <summary>
            /// 探测确认**本来就已经是**目标态 —— 可靠证据（我们什么都没做）。
            /// </summary>
            AlreadyAtTarget = 1,

            /// <summary>
            /// 真的发出了**模拟 Shift** —— 可靠：与用户亲手按 Shift 完全等价，
            /// 输入法必定响应，因此可以放心地把它记成"当前确信状态"。
            /// </summary>
            RealKey = 2,

            /// <summary>
            /// 只写了转换状态（<c>IMC_SETCONVERSIONMODE</c> / <c>ImmSetConversionStatus</c>）。
            /// **不可靠**：对所有 TSF / 国产输入法可能"报成功但真实状态没变"，
            /// 因此调用方**不能**据此认定已处于目标态，只能记为"尽力而为"，
            /// 之后仍需在条件允许时补发一次真实 Shift。
            /// </summary>
            DirectSet = 3,

            /// <summary>
            /// ★ v56：通过**小狼毫自己的命令行接口**直接设定 ASCII 模式
            /// （<c>WeaselServer.exe /ascii|/nascii</c>，需 weasel 0.16.0+）。
            ///
            /// 与 <see cref="RealKey"/> 的本质区别：**这是设值，不是翻转键**。
            ///   · 盲按 Shift 之所以危险，是因为它只有"翻转"语义 —— 一旦我们对当前
            ///     状态判断有误（小狼毫下必然判断不了），它就会把状态切反，
            ///     而且失败时还会留下一条假的 `_switchApplied` 记录（已实机钉死）。
            ///   · 本方式直接说"请设为英文"，重复执行无副作用，也不依赖焦点窗口。
            ///
            /// 算作**可靠达成**：进程退出码 0 表示服务已受理；即便实际没生效，
            /// 因为它是幂等设值，下一次重发也没有任何副作用（不像翻转会切反）。
            /// </summary>
            WeaselCli = 4
        }

        /// <summary>
        /// 这次"达成"是否**可信** —— 即能否把它记成"当前确信的输入法状态"。
        ///
        /// ★★ 唯一的判据就是这里，调用方（XimeEngine.ApplySwitchIme）不许自己另写一份：
        ///   · <see cref="ImeSwitchManner.AlreadyAtTarget"/> / <see cref="ImeSwitchManner.RealKey"/>
        ///     → 可信（前者是探测实证，后者与用户亲手按 Shift 完全等价）；
        ///   · <see cref="ImeSwitchManner.DirectSet"/> → **不可信**。它对 TSF / 国产输入法
        ///     普遍"报成功但真实状态没变"；把它当成"已切好"会把后续所有请求短路掉，
        ///     用户看到的就是「切换输入法模式还是不行」（v33 实机事故）。
        ///   · <see cref="ImeSwitchManner.Failed"/> → 显然不可信。
        ///
        ///   抽成静态方法是为了让自测工具能用**可执行反证**钉住这条契约，
        ///   而不是只写在注释里靠人记。
        /// </summary>
        public static bool IsReliableManner(ImeSwitchManner manner)
        {
            return manner == ImeSwitchManner.AlreadyAtTarget
                || manner == ImeSwitchManner.RealKey
                // ★ v56：小狼毫命令行是**幂等设值**，"重复执行会切反"这个风险不存在，
                //   所以即便读不回真实态也可以放心采信（详见枚举注释）。
                || manner == ImeSwitchManner.WeaselCli;
        }

        /// <summary>
        /// 把输入法切换到目标中英模式。
        ///
        /// 策略（按优先级）：
        ///   1. 探测当前真实模式（IME 消息通道优先，见 <see cref="GetModeAt"/>）；
        ///   2. 已知且已符合目标 —— 不做任何动作，如实上报 AlreadyAtTarget；
        ///   3. 探测不可用（Unknown）—— 仅在调用方明确允许时才轻按一次 Shift；
        ///   4. 已知且不符合目标 —— 只发一次**真实 Shift**（等价用户手动按 Shift）。
        ///
        /// ★★ 为什么第 4 步「只发一次真实 Shift、不优先直设、不复核」：
        ///   现代输入法（微软拼音、搜狗等）走 TSF，<c>ImmSetConversionStatus</c> 与
        ///   <c>IMC_SETCONVERSIONMODE</c> 这类 IMM32 时代的 API **改不动真实的中英状态** ——
        ///   它们改的只是 IMM32 兼容层缓存，表现为「报告成功但实际没切」。
        ///   用户实测（XIMEPROBE）：切英文后通道①(IMM32)读 English、通道②(IME 消息)
        ///   仍读 Chinese，输入法实际还是中文。真实 Shift 才是对所有输入法都有效的
        ///   唯一手段。且模拟一次 Shift 与用户亲手按 Shift 完全等价、输入法必定响应，
        ///   若事后「读不到变化」就再按一次，反而把它又切回原状（这是历史上
        ///   「切了没用还是英文」的根因，见 XimeEngine.DoEnsureChinese 的注释）。
        /// </summary>
        /// <param name="hwnd">目标窗口，Zero 表示前台窗口。</param>
        /// <param name="target">目标模式（Chinese / English）。</param>
        /// <param name="cancelled">外部取消信号。</param>
        /// <param name="allowBlindToggle">
        /// 探测不可用时是否允许"盲按 Shift"。
        ///
        /// 默认 false（拒绝）—— 因为此时**无法判断当前到底是中文还是英文**，
        /// 盲按一下 Shift 有 50% 概率把本来就是目标的状态切反。
        /// 只有调用方明确知道"当前一定不是目标态"（例如刚观察到用户手动切换过）
        /// 时才应传 true。
        /// </param>
        /// <param name="allowRealKey">
        /// 允许发送**真实按键**（模拟 Shift）的判据，可为 null（表示允许）。
        ///
        /// ★ 为什么必须让调用方来判：真实 Shift 是**全局**按键事件，它会落到
        ///   "当前前台窗口所属的输入法"上，而**不是**我们传进来的这个 hwnd。
        ///   所以"前台此刻是不是 AutoCAD"这件事只有引擎层知道 —— 若前台已经
        ///   变成别的程序（或本插件自己的设置面板），按 Shift 要么改了**别人**的
        ///   输入法，要么白白落在面板上毫无作用（现场日志里这类动作全部返回失败）。
        ///   判据返回 false 时，本方法**只走窗口直设通道**，绝不发全局按键。
        /// </param>
        /// <param name="manner">
        /// 本次是**怎么达成**的 —— 见 <see cref="ImeSwitchManner"/>。
        /// 调用方必须区分 <see cref="ImeSwitchManner.RealKey"/> 与
        /// <see cref="ImeSwitchManner.DirectSet"/>，后者的"成功"不足以当作"已切好"。
        /// </param>
        /// <param name="distrustProbe">
        /// 是否**不采信**本次探测读数（v35 新增）。
        ///
        /// ★★ 为什么需要它：IME 上下文只挂在**真正承接受输入的子控件**上
        ///   （AutoCAD 的命令栏编辑控件、在位文字编辑器）。若靶点退化成
        ///   绘图区 / MDI 子框架这类窗口，<c>ImmGetContext</c> 会给出一个
        ///   **默认的**转换状态 —— 实测它恒为「中文」。
        ///   现场日志（v34）：
        ///     用户刚被切到英文，敲 t 空格成功启动 TTEXT（说明确实是英文），
        ///     紧接着「需要中文」的切换却报「探测已是 Chinese，无需动作（窗口=0x3C24D2）」
        ///     —— 0x3C24D2 是绘图区窗口，那个 Chinese 是假的。
        ///   结论：靶点不可信时，读数一律当作「读不出来」，改由 <c>_switchApplied</c>
        ///   （我们自己动作的结果）决定要不要盲切，**绝不能**用它去判定"已经是目标态"。
        /// </param>
        public bool SwitchTo(IntPtr hwnd, ImeMode target, Func<bool> cancelled,
                             bool allowBlindToggle, Func<bool> allowRealKey,
                             bool distrustProbe,
                             out ImeSwitchManner manner)
        {
            manner = ImeSwitchManner.Failed;

            if (target == ImeMode.Unknown)
                return false;
            if (hwnd == IntPtr.Zero)
                hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;

            bool realKeyAllowed = true;
            if (allowRealKey != null)
            {
                try { realKeyAllowed = allowRealKey(); }
                catch (Exception) { realKeyAllowed = false; }
            }

            lock (_sync)
            {
                // ★ 用 GetModeOnWindow（单窗口直读）而不是 GetMode（会回退到焦点/前台）：
                //   写入端是 SetModeDirectOnWindow(hwnd)，读取端必须采同一个窗口，
                //   否则"读的是 A 窗口、写的是 B 窗口"——现场日志证实 CAD 主窗口与
                //   设置面板控件在同一时刻可以读到相反的结果，据此判断必然出错。
                //
                // ★ distrustProbe：靶点没有 IME 上下文 → 读数不可信 → 装作读不出来。
                var actual = distrustProbe ? ImeMode.Unknown : GetModeOnWindow(hwnd);

                // ① 探测成功且已经是目标 —— 什么都不做，严格保持现状
                if (actual == target)
                {
                    _lastKnown = actual;
                    manner = ImeSwitchManner.AlreadyAtTarget;
                    return true;
                }

                // ② 探测不可用（TSF / 自绘输入法，如部分新版微信、搜狗）：
                //   无法判断当前状态，默认拒绝切换，避免"切反"。
                if (actual == ImeMode.Unknown)
                {
                    if (!allowBlindToggle || !realKeyAllowed)
                    {
                        _lastKnown = ImeMode.Unknown;
                        return false;
                    }

                    // 调用方确认需要切换 → 轻按一次 Shift 即等价于用户手动切换
                    if (CharInjector.TapShift())
                    {
                        _lastKnown = target;
                        manner = ImeSwitchManner.RealKey;
                        return true;
                    }
                    return false;
                }

                // ③ 探测到明确的反面状态 → 发一次真实 Shift（见方法注释）。
                //   但仅在前台确实属于 AutoCAD 时才允许（见 allowRealKey）。
                if (realKeyAllowed && CharInjector.TapShift())
                {
                    _lastKnown = target;
                    manner = ImeSwitchManner.RealKey;
                    return true;
                }

                // ④ 不能发全局键 / TapShift 发送失败 → 兜底：在**同一个窗口**上直接
                //   设置转换状态（不需要模拟按键，因此不存在越界问题）。
                //   对 IMM32 宿主（AutoCAD 2014 及更早）这条通道有时有效；
                //   对 TSF / 国产输入法则常常"报成功但真实状态没变" ——
                //   所以这里如实上报 DirectSet，交由调用方决定是否需要补一次真实 Shift。
                if (SetModeDirectOnWindow(hwnd, target))
                {
                    _lastKnown = target;
                    manner = ImeSwitchManner.DirectSet;
                    return true;
                }

                return false;
            }
        }

        /// <summary>兼容旧签名（靶点读数可信时使用）。</summary>
        public bool SwitchTo(IntPtr hwnd, ImeMode target, Func<bool> cancelled,
                             bool allowBlindToggle, Func<bool> allowRealKey,
                             out ImeSwitchManner manner)
        {
            return SwitchTo(hwnd, target, cancelled, allowBlindToggle, allowRealKey,
                            false, out manner);
        }

        /// <summary>兼容旧签名（不关心达成方式时使用）。</summary>
        public bool SwitchTo(IntPtr hwnd, ImeMode target, Func<bool> cancelled = null,
                             bool allowBlindToggle = false, Func<bool> allowRealKey = null)
        {
            ImeSwitchManner ignored;
            return SwitchTo(hwnd, target, cancelled, allowBlindToggle, allowRealKey, out ignored);
        }

        #endregion

        /// <summary>清空内部缓存（切换文档时调用）。</summary>
        public void ResetCache()
        {
            lock (_sync)
            {
                _lastKnown = ImeMode.Unknown;
            }
        }

        #region 诊断

        /// <summary>
        /// 逐通道探测的结果（诊断用）。
        ///
        /// 「切换输入法模式」的可用性完全取决于能否读出状态，而读不出时
        /// "两条通道各自是什么情况"是定位问题的唯一线索：
        ///   · 通道①通、通道②不通 → 正常（绝大多数输入法）
        ///   · 通道①不通、通道②通 → IMM32 兼容层被关掉的 TSF 输入法，
        ///                            **仍然可用**（这正是加通道②的意义）
        ///   · 两条都不通          → 该模式无法工作，应改用无视输入法模式
        /// </summary>
        public struct ChannelProbe
        {
            /// <summary>IME 窗口是否取到（决定 WM_IME_CONTROL 发不发得出去）。</summary>
            public bool ImeWindowAvailable;

            /// <summary>通道①（IMM32 ImmGetConversionStatus）读到的中英模式。</summary>
            public ImeMode ViaImm32;

            /// <summary>通道②（WM_IME_CONTROL / IMC_GETCONVERSIONMODE）读到的中英模式。</summary>
            public ImeMode ViaImeControl;

            /// <summary>是否有任一通道可用于读写状态。</summary>
            public bool Usable
            {
                get
                {
                    return ViaImm32 != ImeMode.Unknown
                        || ViaImeControl != ImeMode.Unknown;
                }
            }
        }

        /// <summary>逐通道探测指定窗口的输入法状态（**只读**，不写入任何东西）。</summary>
        public ChannelProbe ProbeChannels(IntPtr hwnd)
        {
            var p = new ChannelProbe();
            if (hwnd == IntPtr.Zero)
                return p;

            p.ViaImm32 = GetModeViaContext(hwnd);

            try { p.ImeWindowAvailable = NativeMethods.ImmGetDefaultIMEWnd(hwnd) != IntPtr.Zero; }
            catch (Exception) { p.ImeWindowAvailable = false; }

            p.ViaImeControl = GetModeViaImeControl(hwnd);
            return p;
        }

        #endregion
    }
}
