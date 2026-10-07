using System;
using System.Runtime.InteropServices;
using System.Text;
using Xime.Core.Native;

namespace Xime.Core
{
    /// <summary>
    /// 字符注入器：把按键转换成字符并送入目标窗口。
    ///
    /// 用于键盘钩子兜底通道 —— 当原生按键被吞掉后，由本类负责把对应字符
    /// 送到 AutoCAD 命令栏（或当前焦点窗口），保证命令栏始终能收到英文命令。
    ///
    /// 实现要点：
    ///   - 使用 ToUnicodeEx 结合当前键盘布局与修饰键状态，把 VK 转成实际字符，
    ///     这样能正确处理 Shift 组合、大小写、数字符号等；
    ///   - 通过 PostMessage(WM_CHAR) 投递字符到目标窗口，不干扰前台焦点的输入法状态；
    ///   - 对退格/回车/空格等特殊键做专门处理。
    /// </summary>
    internal static class CharInjector
    {
        /// <summary>
        /// SendInput 用的输入数组缓冲（每线程一个）。
        ///
        /// 注入是每次按键都要做的事，之前每次都 new 一个 INPUT[2]
        /// （x64 下约 104 字节）纯属制造 Gen0 垃圾。复用同一个数组即可 ——
        /// SendInput 是同步调用，且每次都会把两个元素全部覆盖。
        /// </summary>
        [ThreadStatic] private static NativeMethods.INPUT[] _inputBuf;

        /// <summary>INPUT 结构体大小（SendInput 的 cbSize 参数）。缓存，免每次反射。</summary>
        private static readonly int InputSize = Marshal.SizeOf(typeof(NativeMethods.INPUT));

        private static NativeMethods.INPUT[] RentInputs()
        {
            var buf = _inputBuf;
            if (buf == null)
                buf = _inputBuf = new NativeMethods.INPUT[2];
            return buf;
        }

        /// <summary>
        /// 诊断用：最近一次 <see cref="GetRealFocusWindow"/> 实际传给
        /// GetGUIThreadInfo 的那个 ID。0 表示本次走了「与前台同线程」的快路径、
        /// 没有调用 GetGUIThreadInfo。
        ///
        /// ★ 存在的意义：这是**唯一能从外部验证「传的到底是线程 ID 还是进程 ID」**的手段。
        ///   v20 的缺陷（把 GetWindowThreadProcessId 的 out 参数 PID 当成线程 ID 传进去）
        ///   在外部无法观测 —— 函数只是静默退回返回前台窗口，看起来"能用"。
        ///   有了这个字段，XimeTest focusdiag 就能断言：
        ///     值必须等于窗口所属的**线程 ID**，且绝不能等于**进程 ID**。
        /// </summary>
        public static uint LastFocusQueryId { get; private set; }
        private const int WM_CHAR = 0x0102;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;

        /// <summary>
        /// 注入标记：写进 KEYBDINPUT.dwExtraInfo，键盘钩子据此识别「这是自己注入的按键」，
        /// 从而直接放行，避免无限递归。
        /// </summary>
        public static readonly IntPtr InjectSignature = new IntPtr(0x58494D45);   // 'XIME'

        [DllImport(NativeMethods.User32, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 获取按键对应的字符（考虑 Shift / CapsLock 与当前键盘布局）。
        /// 返回 '\0' 表示无法转换。
        ///
        /// ⚠ 注意：本方法依赖「当前线程的键盘布局」与真实修饰键状态，
        /// 在输入法处于中文态时可能返回非英文字符。命令栏英文输入请用
        /// <see cref="GetEnglishCharForKey"/>。
        /// </summary>
        public static char GetCharForKey(int vkCode)
        {
            try
            {
                var keyboardState = new byte[256];
                if (!NativeMethods.GetKeyboardState(keyboardState))
                    return '\0';

                var sb = new StringBuilder(8);
                var hkl = NativeMethods.GetKeyboardLayout(0);
                int n = NativeMethods.ToUnicodeEx(
                    (uint)vkCode, 0, keyboardState, sb, sb.Capacity, 0, hkl);

                if (n > 0 && sb.Length > 0)
                    return sb[0];
            }
            catch (Exception) { }
            return '\0';
        }

        /// <summary>
        /// ★ 获取按键在「US 英文键盘」下对应的字符（完全绕开 IME 与系统布局）。
        ///
        /// 为什么需要它：命令栏要的是**英文命令字符**。若用 <see cref="GetCharForKey"/>，
        /// 它读的是当前线程键盘布局 —— 用户输入法处于中文态（或装了非美式布局）时，
        /// 转换结果可能是中文标点或别的字符。本方法用**自建的 US 布局映射表**，
        /// 结果恒定、可预测，且不读任何 IME 状态。
        ///
        /// 覆盖范围：A-Z（含 Shift 大小写）、0-9（含 Shift 符号）、
        /// 常见 OEM 符号键、空格。其余返回 '\0'。
        /// </summary>
        public static char GetEnglishCharForKey(int vkCode)
        {
            bool shift = NativeMethods.IsShiftPressed();
            bool caps = NativeMethods.IsCapsLockOn();

            // ---- 字母 A-Z：Shift 与 CapsLock 异或决定大小写 ----
            if (vkCode >= 0x41 && vkCode <= 0x5A)
            {
                bool upper = shift ^ caps;
                char c = (char)vkCode;                      // 'A'..'Z'
                return upper ? c : char.ToLowerInvariant(c);
            }

            // ---- 主键盘数字 0-9（Shift 变符号）----
            if (vkCode >= 0x30 && vkCode <= 0x39)
            {
                const string plain = "0123456789";
                const string shift_ = ")!@#$%^&*(";
                return shift ? shift_[vkCode - 0x30] : plain[vkCode - 0x30];
            }

            // ---- OEM 符号键 ----
            switch (vkCode)
            {
                case 0x20: return ' ';                                        // Space
                case 0xBA: return shift ? ':' : ';';                          // ; :
                case 0xBB: return shift ? '+' : '=';                          // = +
                case 0xBC: return shift ? '<' : ',';                          // , <
                case 0xBD: return shift ? '_' : '-';                          // - _
                case 0xBE: return shift ? '>' : '.';                          // . >
                case 0xBF: return shift ? '?' : '/';                          // / ?
                case 0xC0: return shift ? '~' : '`';                          // ` ~
                case 0xDB: return shift ? '{' : '[';                          // [ {
                case 0xDC: return shift ? '|' : '\\';                         // \ |
                case 0xDD: return shift ? '}' : ']';                          // ] }
                case 0xDE: return shift ? '"' : '\'';                         // ' "
                default: return '\0';
            }
        }

        /// <summary>
        /// 获取系统当前真正拥有键盘焦点的控件句柄。
        ///
        /// GetFocus() 只对调用线程有效。跨线程时**不能**用 AttachThreadInput ——
        /// 那要把两个线程的输入队列临时挂到一起（还可能触发输入队列同步），
        /// 实测单次约 **200 微秒**；本方法位于「每按一个键都要走」的钩子路径上，
        /// 用不起。
        ///
        /// 改用 GetGUIThreadInfo：直接查询目标线程的 GUI 状态，取其 hwndFocus，
        /// 一次 P/Invoke 即可，实测从 200µs 降到 1µs 量级。
        /// 传入的线程 ID 是**前台窗口所属线程**，因此拿到的就是用户正在打字的控件。
        ///
        /// ★★ 血的教训 —— GetWindowThreadProcessId 有两个不同的"ID"：
        ///
        ///        uint tid = GetWindowThreadProcessId(hwnd, out uint pid);
        ///        //   ↑ 返回值 = 线程 ID        ↑ out 参数 = 进程 ID
        ///
        ///   两者是完全不同的编号空间，**绝不能混用**。
        ///   v20 曾写成 <c>GetWindowThreadProcessId(fg, out fgThread)</c> ——
        ///   变量名叫 fgThread，装进去的却是 PID。后果：
        ///     · 「与前台同线程」的快路径永远不成立（PID 比 TID 恒不等）；
        ///     · <c>GetGUIThreadInfo(PID)</c> 查不到对应线程，**必然失败**；
        ///   于是本函数**永远退回返回前台主窗口**，而不是真正持有焦点的控件。
        ///   连锁反应：AutoCAD 里所有「焦点是否在编辑框」的判断全部失效 ——
        ///   文字编辑态一按键就被退出、切中文落到错误的窗口上，
        ///   表现为「天正等第三方插件的文字类命令打不出中文」。
        ///   已加 XimeTest focusdiag 作为回归探针（用 PID 查必失败 / 用 TID 查必成功）。
        /// </summary>
        public static IntPtr GetRealFocusWindow()
        {
            try
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg == IntPtr.Zero) return IntPtr.Zero;

                uint fgPid;
                uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, out fgPid);
                if (fgThread == 0) return fg;

                // 与本线程相同 → GetFocus() 就是最准也最快的路径
                //（钩子由 HookUiAnchor 保证安装在 AutoCAD UI 线程上，前台为 CAD 时通常走这里）
                if (fgThread == NativeMethods.GetCurrentThreadId())
                {
                    LastFocusQueryId = 0;   // 诊断：未调用 GetGUIThreadInfo
                    return NativeMethods.GetFocus();
                }

                // 跨线程：查询目标线程的 hwndFocus。注意传的是**线程 ID**。
                LastFocusQueryId = fgThread;    // 诊断：记录实际传入的 ID（必须是 TID、绝不能是 PID）
                var gti = new NativeMethods.GUITHREADINFO();
                gti.cbSize = Marshal.SizeOf(typeof(NativeMethods.GUITHREADINFO));
                if (NativeMethods.GetGUIThreadInfo(fgThread, ref gti) &&
                    gti.hwndFocus != IntPtr.Zero)
                    return gti.hwndFocus;

                // 取不到就退回到前台窗口本身
                return fg;
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// 把字符投递到目标窗口。
        /// </summary>
        public static bool SendChar(IntPtr hwnd, char ch)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                return PostMessage(hwnd, WM_CHAR, new IntPtr(ch), IntPtr.Zero);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 向「当前真实焦点控件」投递字符（PostMessage WM_CHAR）。
        /// 这是兼容性最好的一招：无论目标是否为标准 Edit，只要它处理 WM_CHAR
        /// （AutoCAD 命令栏就是），就能收到字符。
        /// </summary>
        public static bool SendCharToFocus(char ch)
        {
            var hwnd = GetRealFocusWindow();
            if (hwnd == IntPtr.Zero) return false;
            return SendChar(hwnd, ch);
        }

        /// <summary>
        /// 模拟一次「单击 Shift」（按下 → 短暂停顿 → 抬起，不配合其它键）。
        ///
        /// 用途：绝大多数中文输入法把「单独轻按 Shift」作为中/英切换热键。
        /// 用 SendInput 产生真实按键事件，比 ImmSimulateHotKey 更底层、更通用，
        /// 且同样 **不切换键盘布局**。
        ///
        /// ★ 为什么按下与抬起之间要留间隔：
        ///   部分输入法（尤其微软拼音等 TSF 输入法）识别「单按 Shift 切换中英」
        ///   需要 Shift 按下后保持一小段时间；若 down 与 up 紧贴着连续发出，
        ///   会被当成一次无效连击，中英不切换。留 40ms 让输入法稳定识别。
        /// </summary>
        public static bool TapShift()
        {
            try
            {
                var inputs = RentInputs();

                // 用**左 Shift**：与用户实际按下的键一致（部分输入法区分左右 Shift，
                // 用户实测能切换的是左 Shift，模拟同一个键最稳妥）。

                // ① 按下
                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = (ushort)NativeMethods.VK_LSHIFT;
                inputs[0].U.ki.wScan = 0;
                inputs[0].U.ki.dwFlags = 0;
                inputs[0].U.ki.dwExtraInfo = InjectSignature;
                if (NativeMethods.SendInput(1, inputs, InputSize) != 1)
                    return false;

                // ② down/up 之间留间隔（见方法注释）
                System.Threading.Thread.Sleep(40);

                // ③ 抬起
                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = (ushort)NativeMethods.VK_LSHIFT;
                inputs[0].U.ki.wScan = 0;
                inputs[0].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
                inputs[0].U.ki.dwExtraInfo = InjectSignature;
                return NativeMethods.SendInput(1, inputs, InputSize) == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 用 SendInput + KEYEVENTF_UNICODE 注入字符。
        ///
        /// 这是最可靠的方式：直接投递 Unicode 字符，**绕过输入法**，
        /// 因此无论输入法处于中文还是英文态，目标控件都能收到该字符。
        ///
        /// 与 PostMessage(WM_CHAR) 的区别：
        ///   - PostMessage 只把消息塞进目标窗口队列，若目标不是标准 Edit 控件
        ///     （AutoCAD 命令栏即如此），消息会被丢弃，表现为「按键被吞了但没反应」；
        ///   - SendInput(UNICODE) 生成真实键盘输入事件，由系统派发到当前焦点控件，
        ///     任何接受键盘输入的控件都能正确处理。
        /// </summary>
        public static bool SendUnicodeChar(char ch)
        {
            try
            {
                var inputs = RentInputs();

                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = 0;
                inputs[0].U.ki.wScan = ch;                    // 要输入的 Unicode 字符
                inputs[0].U.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE;
                inputs[0].U.ki.dwExtraInfo = InjectSignature;

                inputs[1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = 0;
                inputs[1].U.ki.wScan = ch;
                inputs[1].U.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP;
                inputs[1].U.ki.dwExtraInfo = InjectSignature;

                int size = InputSize;
                uint n = NativeMethods.SendInput(2, inputs, size);
                return n == 2;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 向目标窗口直接投递一次「虚拟键」按下 + 抬起（**不经过系统输入队列**）。
        ///
        /// 唯一能让输入法「绝对看不到按键」的通道 —— 也正因如此它不会被输入法组字，
        /// 不会弹候选窗。
        ///
        /// WM_KEYDOWN / WM_KEYUP 的 lParam 是键盘消息的位域：
        ///   bit0-15   重复计数
        ///   bit16-23  扫描码 ★ 关键
        ///   bit24     扩展键标志
        ///   bit29     上下文码（Alt 是否按下）
        ///   bit30     前一次键状态
        ///   bit31     转换状态（1 = 正在释放）
        ///
        /// ★ 这里**必须带上真实扫描码**（MapVirtualKey 取）。
        ///   早期实现两个消息都传死值 0x10001，注释还把它解释成「重复计数 1、
        ///   扫描码 0、IME-neutral」—— 其实 0x10001 拆开是「重复计数 1 + 扫描码 1」，
        ///   而扫描码 1 是 Esc 的扫描码。对只读虚拟键码的消费者（命令栏文本输入）
        ///   无所谓，但对**读原始按键的消费者**（天正「任意布置」二级提示走
        ///   acedGrRead 一类接口）就是错的信息，会被直接丢弃。
        ///   所以按下/抬起都按真实值填，抬起消息再补上 bit30|bit31。
        ///
        /// 已知局限（实测记录）：AutoCAD 2013+ 的命令行是 WPF 实现，其 HwndSource
        /// 只处理少数窗口消息，**不把 PostMessage 送来的 WM_KEYDOWN 转成文本**；
        /// 因此本通道适合依赖消息读取的命令，不适合往命令栏"打字"。
        /// 往命令栏打字请用 <see cref="SendUnicodeInput"/>，命令期间回放按键请用
        /// <see cref="ReplayVk"/>。
        /// </summary>
        /// <param name="hwnd">目标窗口。</param>
        /// <param name="vkCode">虚拟键码。</param>
        /// <returns>两次投递是否都成功（PostMessage 返回值非 0 视为成功）。</returns>
        public static bool PostVkKey(IntPtr hwnd, int vkCode)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                // 真实扫描码 + 扩展键标志（与 ReplayVk 用同一套判断）
                int scan = (int)NativeMethods.MapVirtualKey((uint)vkCode, NativeMethods.MAPVK_VK_TO_VSC);
                long baseLParam = 1L | ((long)(scan & 0xFF) << 16);
                if (IsExtendedKey(vkCode)) baseLParam |= 0x01000000L;   // bit24 扩展键

                // 抬起消息：bit30（前一次键状态=1）+ bit31（转换状态=1）
                long upLParam = baseLParam | 0xC0000000L;

                bool down = PostMessage(hwnd, WM_KEYDOWN, new IntPtr(vkCode), new IntPtr(baseLParam));
                bool up = PostMessage(hwnd, WM_KEYUP, new IntPtr(vkCode), new IntPtr(upLParam));

                // PostMessage 返回 true 表示消息已入队
                return down || up;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// ★★ 主通道：用 SendInput(KEYEVENTF_UNICODE) 直接注入一个 Unicode 字符。
        ///
        /// 这是对 **AutoCAD 2013+ 的 WPF 命令栏**（宿主类名 HwndWrapper）唯一可靠的方案。
        ///
        /// 为什么 PostMessage 不行、而它能行：
        ///   · AutoCAD 2013 起命令行改用 WPF 实现，外层是 `HwndWrapper[DefaultDomain;;...]`
        ///     这类窗口。WPF 的 HwndSource 只处理少数窗口消息（绘制、尺寸、鼠标等），
        ///     **不把 PostMessage 送来的 WM_KEYDOWN 转成文本输入** —— 消息进了队列却无人处理，
        ///     表现为「投递返回成功，但字符一个都没出现」。
        ///   · SendInput 生成的是**系统级真实键盘事件**，会走完整的输入路由：
        ///     系统 → 当前焦点窗口 → WPF InputManager → 文本框。
        ///     因此无论命令栏是 MFC Edit 还是 WPF 控件，都能正确收到。
        ///
        /// 为什么它不破坏「无视输入法」：
        ///   · KEYEVENTF_UNICODE 注入的是**已经确定的 Unicode 码点**，
        ///     不经过键盘布局翻译、也**完全不经过输入法**（IME 不参与 UNICODE 消息）。
        ///   · 所以用户输入法保持中文态时，命令栏照样收到英文字母；
        ///     而用户输入法状态自始至终没有被读取或修改。
        ///
        /// 与 PostMessage 通道的分工：
        ///   · 老版 AutoCAD（MFC Edit/RichEdit 命令栏）：PostMessage 精准且能自动处理 Shift；
        ///   · 2013+ WPF 命令栏：只有本方法有效。
        ///   实际运行时两个通道都会走，谁生效由目标控件类型决定，互不冲突
        ///   （PostMessage 对 WPF 是空投，UNICODE 对 MFC 是叠加，但两者不会同时成功）。
        /// </summary>
        public static bool SendUnicodeInput(char ch)
        {
            try
            {
                var inputs = RentInputs();

                // 按下
                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = 0;
                inputs[0].U.ki.wScan = ch;
                inputs[0].U.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE;
                inputs[0].U.ki.time = 0;
                inputs[0].U.ki.dwExtraInfo = InjectSignature;

                // 抬起
                inputs[1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = 0;
                inputs[1].U.ki.wScan = ch;
                inputs[1].U.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP;
                inputs[1].U.ki.time = 0;
                inputs[1].U.ki.dwExtraInfo = InjectSignature;

                int size = InputSize;
                uint n = NativeMethods.SendInput(2, inputs, size);
                return n == 2;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 用 SendInput 发送一个「真实虚拟键」（按下 + 抬起）。
        ///
        /// 用途：编辑键（退格 / 回车 / Tab / 方向键 / 空格）—— 这些键不产生字符，
        /// 只表达「编辑动作」，且与输入法无关（IME 不拦这些键），
        /// 因此用真实 VK 发送最自然，MFC 与 WPF 命令栏都能正确处理。
        ///
        /// wVk 为 0 时用 wScan 走扫描码（用于极少数需要扫描码的键）。
        /// </summary>
        public static bool SendVkInput(int vkCode)
        {
            try
            {
                var inputs = RentInputs();

                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = (ushort)vkCode;
                inputs[0].U.ki.wScan = 0;
                inputs[0].U.ki.dwFlags = 0;
                inputs[0].U.ki.time = 0;
                inputs[0].U.ki.dwExtraInfo = InjectSignature;

                inputs[1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = (ushort)vkCode;
                inputs[1].U.ki.wScan = 0;
                inputs[1].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
                inputs[1].U.ki.time = 0;
                inputs[1].U.ki.dwExtraInfo = InjectSignature;

                int size = InputSize;
                uint n = NativeMethods.SendInput(2, inputs, size);
                return n == 2;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 【仅测试用】模拟一次「用户真实按键」（SendInput，带 InjectSignature）。
        ///
        /// 与 <see cref="SendKey"/> 的区别：带注入标记，便于钩子识别为"自己人"而放行，
        /// 从而在测试程序里模拟"用户敲键"的行为，不触发插件的接管逻辑递归。
        /// 正式插件代码不调用本方法。
        /// </summary>
        public static bool SendKeyForProbe(int vkCode)
        {
            try
            {
                var inputs = RentInputs();

                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = (ushort)vkCode;
                inputs[0].U.ki.dwFlags = 0;
                inputs[0].U.ki.dwExtraInfo = InjectSignature;

                inputs[1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = (ushort)vkCode;
                inputs[1].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
                inputs[1].U.ki.dwExtraInfo = InjectSignature;

                int size = InputSize;
                uint n = NativeMethods.SendInput(2, inputs, size);
                return n == 2;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// ★★ 真实按键重放：用 SendInput 把「原样的物理按键」再发一次
        /// （真 VK + 真扫描码 + 扩展键标志），并打上注入标记。
        ///
        /// 存在的意义（v26，天正「任意布置」二级命令一事的最终解）：
        ///   · <see cref="SendUnicodeInput"/> 注入的是 KEYEVENTF_UNICODE，
        ///     系统给的 wVk 恒为 VK_PACKET(0xE7)。**读命令行文本**的消费者
        ///     （命令名解析、AutoCAD 原生提示）没问题；但**读原始按键**的
        ///     消费者（天正 RYBZ 的 A/E/D/F/X/Y 二级提示，acedGrRead 一类）
        ///     认不出 VK_PACKET → 命令毫无反应，只在命令栏留个小写字母。
        ///   · 直接「放行物理键」虽然能让天正正常，但物理键会同时落到
        ///     输入法手里（候选窗弹出、字母被吃掉），于是把输入法又拖回来了。
        ///   · 本方法两者兼顾：**物理键已被钩子吞掉**（输入法看不到），
        ///     再由本方法把「与物理按键等价的真键事件」交给 AutoCAD。
        ///
        /// 与 <see cref="SendVkInput"/> 的区别：这里补上**真实扫描码**，
        /// 扩展键（小键盘、方向键等）还会带上 KEYEVENTF_EXTENDEDKEY，
        /// 使注入事件在键码层面与物理按键完全等价。
        /// </summary>
        public static bool ReplayVk(int vkCode)
        {
            try
            {
                var inputs = RentInputs();

                // 真实扫描码：缺了它，底层读取者仍可能认不出这次按键。
                ushort scan = (ushort)NativeMethods.MapVirtualKey(
                    (uint)vkCode, NativeMethods.MAPVK_VK_TO_VSC);
                uint baseFlags = IsExtendedKey(vkCode) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0u;

                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = (ushort)vkCode;
                inputs[0].U.ki.wScan = scan;
                inputs[0].U.ki.dwFlags = baseFlags;
                inputs[0].U.ki.time = 0;
                inputs[0].U.ki.dwExtraInfo = InjectSignature;

                inputs[1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = (ushort)vkCode;
                inputs[1].U.ki.wScan = scan;
                inputs[1].U.ki.dwFlags = baseFlags | NativeMethods.KEYEVENTF_KEYUP;
                inputs[1].U.ki.time = 0;
                inputs[1].U.ki.dwExtraInfo = InjectSignature;

                int size = InputSize;
                uint n = NativeMethods.SendInput(2, inputs, size);
                return n == 2;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 该虚拟键是否是「扩展键」（右 Ctrl/Alt、方向键、Insert/Delete/Home/End/
        /// PageUp/PageDown、小键盘除号与回车、NumLock、PrintScreen 等）。
        /// 重放时需带 KEYEVENTF_EXTENDEDKEY，否则目标可能收到另一个键。
        /// </summary>
        public static bool IsExtendedKey(int vkCode)
        {
            switch (vkCode)
            {
                case 0x21: case 0x22: case 0x23: case 0x24:          // PgUp / PgDn / End / Home
                case 0x25: case 0x26: case 0x27: case 0x28:          // ← ↑ → ↓
                case 0x2D: case 0x2E:                                // Insert / Delete
                case 0x6F:                                           // 小键盘 /
                case 0x90:                                           // NumLock
                case 0xA3: case 0xA5:                                // 右 Ctrl / 右 Alt
                case 0x2C:                                           // PrintScreen
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 判断某虚拟键是否是「可打印字符键」（字母、数字、常见符号）。
        /// 这些键在英文命令输入场景下才需要被接管。
        /// </summary>
        public static bool IsPrintableKey(int vkCode)
        {
            // 字母 A-Z
            if (vkCode >= 0x41 && vkCode <= 0x5A) return true;
            // 主键盘数字 0-9
            if (vkCode >= 0x30 && vkCode <= 0x39) return true;
            // 小键盘数字
            if (vkCode >= 0x60 && vkCode <= 0x69) return true;
            // OEM 符号键（分号、等号、逗号、减号、点、斜杠、反引号、括号、反斜杠、引号）
            if (vkCode == 0xBA || vkCode == 0xBB || vkCode == 0xBC ||
                vkCode == 0xBD || vkCode == 0xBE || vkCode == 0xBF ||
                vkCode == 0xC0 || vkCode == 0xDB || vkCode == 0xDC ||
                vkCode == 0xDD || vkCode == 0xDE)
                return true;
            // 小键盘运算符
            if (vkCode == 0x6A || vkCode == 0x6B || vkCode == 0x6D ||
                vkCode == 0x6E || vkCode == 0x6F)
                return true;
            // 空格
            if (vkCode == NativeMethods.VK_SPACE) return true;

            return false;
        }

        /// <summary>
        /// 判断某虚拟键是否是文字编辑键（退格、回车、Tab、Esc）。
        /// </summary>
        public static bool IsEditingKey(int vkCode)
        {
            return vkCode == NativeMethods.VK_BACK
                || vkCode == NativeMethods.VK_RETURN
                || vkCode == NativeMethods.VK_TAB
                || vkCode == NativeMethods.VK_ESCAPE
                || vkCode == 0x2E;   // Delete
        }
    }
}
