using System;
using System.Runtime.InteropServices;

namespace Xime.Core.Native
{
    /// <summary>
    /// 原生 API 封装。
    ///
    /// 两条并行的技术通道（互为补充）：
    ///
    /// 【通道 A】IMM32 状态控制 —— 首选，性能最好
    ///   通过 ImmSimulateHotKey(SHIFT) / ImmSetConversionStatus 切换当前输入法的
    ///   「中英转换模式」，**绝不切换键盘布局**。切换成功后输入法本身就在英文态，
    ///   命令栏可直接输入英文命令，无需任何按键拦截。
    ///
    /// 【通道 B】WH_KEYBOARD_LL 低级键盘钩子 —— 兜底，兼容国产输入法
    ///   部分输入法（搜狗、微信等）设置了「中文状态下也允许输入英文」或使用自绘
    ///   UI，IMM32 无法查询/设置其状态。此时用键盘钩子拦截字母键，判断若 CAD
    ///   命令栏需要英文，则吞掉原生按键并吞掉/放行相应字符，保证命令栏始终能输入英文命令。
    ///   采用「吞掉 + 放行」而非「吞掉 + SendInput」，避免按键重复。
    /// </summary>
    internal static class NativeMethods
    {
        internal const string Imm32 = "imm32.dll";
        internal const string User32 = "user32.dll";
        internal const string Kernel32 = "kernel32.dll";

        #region IMM32 转换模式位

        /// <summary>中文（本地语言）输入模式。</summary>
        public const int IME_CMODE_NATIVE = 0x0001;

        /// <summary>ImmSimulateHotKey 热键：中英切换（等价 Shift）。</summary>
        public const int IME_HOTKEY_SHIFT = 0x0001;

        /// <summary>
        /// 「输入法控制」消息。发给 **IME 窗口**（即
        /// <see cref="ImmGetDefaultIMEWnd"/> 的返回值），不是普通控件窗口。
        ///
        /// ★ 为什么需要它（这是「切换输入法模式」能否在国产输入法上工作的关键）：
        ///   部分新版输入法（微信 / 搜狗等 TSF 实现）**关掉**了 IMM32 的
        ///   ImmGetContext 兼容层，于是 ImmGetConversionStatus 连句柄都拿不到，
        ///   只能一路报告 Unknown —— 而 Unknown 意味着「既读不出状态、也不敢盲切」，
        ///   整个模式就废了。
        ///   但这些输入法通常**仍然响应 WM_IME_CONTROL**：这条消息走的是输入法
        ///   窗口自己的协议，能直接读写中英转换模式，绕过了 IMM32 上下文。
        ///   它是本模式在 TSF 输入法上的第二条（也常常是唯一可用的）通道。
        /// </summary>
        public const int WM_IME_CONTROL = 0x0283;

        /// <summary>WM_IME_CONTROL 子命令：读取转换模式（返回值低字即 conversion mode）。</summary>
        public const int IMC_GETCONVERSIONMODE = 0x0001;

        /// <summary>WM_IME_CONTROL 子命令：设置转换模式（低字 = conversion，高字 = sentence）。</summary>
        public const int IMC_SETCONVERSIONMODE = 0x0002;

        [DllImport(Imm32, SetLastError = true)]
        public static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport(Imm32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport(Imm32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ImmGetConversionStatus(IntPtr hIMC, out int lpfdwConversion, out int lpfdwSentence);

        [DllImport(Imm32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ImmSetConversionStatus(IntPtr hIMC, int fdwConversion, int fdwSentence);

        [DllImport(Imm32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ImmSimulateHotKey(IntPtr hWnd, int dwHotKeyID);

        [DllImport(Imm32, SetLastError = true)]
        public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

        /// <summary>
        /// 把一个输入法上下文（HIMC）关联到窗口；<b>传 NULL 表示"解除该窗口与输入法的关联"</b>，
        /// 效果 = 这个窗口用不了输入法（微软 KB Q171154「HOWTO: Disable IME for a Particular
        /// Window」的官方做法）。返回值是**原来关联的 HIMC**，必须存下来，回头用它还原。
        ///
        /// ★★ 为什么这正是「无视输入法模式」缺的那一块（2026-10-06 定案）：
        ///
        ///   天正二级命令（RYBZ 的 A/S/D/E 选项）走 <c>acedGrRead</c> 一类**原始按键通道**，
        ///   只认真实物理按键 ⇒ 唯一能让它生效的档是「放行物理键」(<c>PassThrough</c>)。
        ///   但物理键一放行，**输入法也就看到了** ⇒ 中文态下弹候选窗。
        ///   而「吞键再合成」（PostKey/PostKeyFrame/ReplayVk/UNICODE）天正一律不认。
        ///
        ///   ⇒ 于是只剩一条路：**让物理键原样流过去，但让输入法看不见它**。
        ///     这正是本函数的能力 —— 它**不改变输入法的任何状态**（中/英、键盘布局、
        ///     候选词库统统不动，输入法自己的状态栏毫无变化），只是让**指定窗口**
        ///     不再接收输入法处理。对用户而言就是"这个窗口无视输入法"。
        ///
        ///   ★ 与 <see cref="ImmSetConversionStatus"/> / <c>ImmSimulateHotKey</c> 的区别：
        ///     那两个是**改输入法状态**（切换输入法模式的做法，全局可见）；
        ///     本函数只改**窗口与输入法的关联**，是"无视输入法模式"唯一自洽的落点。
        ///
        /// ★ 可逆性：解除时返回旧 HIMC，还原时把旧 HIMC 传回去即可**精确复原**。
        ///   （旧 HIMC 为 0 = 该窗口本来就没有输入法上下文，无需还原。）
        /// </summary>
        [DllImport(Imm32, SetLastError = true)]
        public static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

        /// <summary>ImmAssociateContextEx 的关联范围：把上下文关联到<b>子窗口</b>。</summary>
        public const uint IACE_CHILDREN = 0x0001;

        /// <summary>ImmAssociateContextEx 的关联范围：恢复窗口的**默认**输入法上下文。</summary>
        public const uint IACE_DEFAULT = 0x0010;

        /// <summary>
        /// 变更「输入法上下文 ↔ 窗口（及其子窗口）」的关联。
        /// 与 <see cref="ImmAssociateContext"/> 相比多了"子窗口"这一档。
        /// </summary>
        [DllImport(Imm32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ImmAssociateContextEx(IntPtr hWnd, IntPtr hIMC, uint dwFlags);

        /// <summary>EnumChildWindows 的回调。返回 true 继续枚举。</summary>
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>
        /// 枚举窗口的所有**后代**窗口（含子窗口的子窗口）。
        ///
        /// ★ 为什么必须连同子孙一起解除 IME 关联：输入法处理按键用的是**当前持有焦点的
        ///   那个窗口**。命令执行期间焦点可能在绘图区、也可能落回命令栏控件
        ///   （AutoCAD 2022 的命令栏是 <c>WindowsForms10.EDIT…</c> 子控件），
        ///   只解除主窗口无法覆盖全部情形。
        /// </summary>
        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        #endregion

        #region 键盘钩子（WH_KEYBOARD_LL）

        public const int WH_KEYBOARD_LL = 13;

        // 键盘消息
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport(User32, SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        /// <summary>
        /// 取模块内导出函数的地址。
        ///
        /// 用途：acad.exe 的导出函数（如 AcadGetTextFrameHandle）在**非 AutoCAD 宿主**
        /// 里用 DllImport 直接调用会抛 DllNotFoundException —— 异常构造 + 栈展开实测
        /// 单次约 500 微秒，而这条路径在一次命令栏判定里就会被走一遍。
        /// 改为先用 GetModuleHandle + GetProcAddress 探测一次（命中则缓存函数指针），
        /// 探测失败也不再有异常开销。
        /// </summary>
        [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport(Kernel32)]
        public static extern uint GetCurrentThreadId();

        /// <summary>
        /// 取当前进程 ID（内核调用，约 100ns）。
        ///
        /// 用于替代 <c>System.Diagnostics.Process.GetCurrentProcess().Id</c> ——
        /// 后者每次调用都要构造 Process 对象并走性能计数器，在键盘钩子这种
        /// 每按键都要跑的路径上开销高出一个数量级（见 XimeTest bench）。
        /// </summary>
        [DllImport(Kernel32)]
        public static extern uint GetCurrentProcessId();

        #endregion

        #region SendInput（用于必要时补发按键）

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport(User32, SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        #endregion

        #region 窗口 / 线程

        [DllImport(User32, SetLastError = true)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport(User32, SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport(User32, SetLastError = true)]
        public static extern IntPtr GetParent(IntPtr hWnd);

        /// <summary><c>GetWindow(hwnd, GW_OWNER)</c>（v59）。</summary>
        public const uint GW_OWNER = 4;

        /// <summary>
        /// 取窗口的**属主**窗口（v59 新增）。
        ///
        /// ★ 为什么必须看属主链，不能只看父链：
        ///   WPF 的 <c>Popup</c> / 下拉浮层是**带属主的顶层窗口** ——
        ///   `GetParent()` 对它返回 **NULL**（它不是任何窗口的子窗口），
        ///   但 `GetWindow(GW_OWNER)` 会给出它挂靠的那个控件。
        ///   AutoCAD 的「命令自动完成」下拉框正是这一类：实测它是
        ///   `HwndWrapper[…]`（→ 被判成"编辑控件"），父链为空
        ///   （→ 父链里**永远**找不到 "CLI Palette"），
        ///   于是被误判成"文字编辑框" ⇒ 一进编辑态就把命令名交给输入法。
        ///   属主链能把这条路径补回来。
        /// </summary>
        [DllImport(User32, SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport(User32)]
        public static extern IntPtr GetFocus();

        [DllImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport(User32)]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        /// <summary>
        /// 把两个线程的输入队列临时挂到一起。
        ///
        /// 用途：<see cref="SetForegroundWindow"/> 只允许"前台进程"调用成功。
        /// 测试探针（XimeTest keyprobe）需要把自己的窗口变成前台才能验证
        /// SendInput 的真实投递效果，于是用经典做法：先 AttachThreadInput
        /// 到当前前台线程，再 SetForegroundWindow，然后解除挂接。
        ///
        /// ★ 注意性能：挂接会让两个线程共享输入队列，单次约 200 微秒
        ///   （见 CharInjector.GetRealFocusWindow 的注释）——
        ///   **绝不能**放进按键热路径，只用于测试/初始化这类一次性场景。
        /// </summary>
        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport(User32)]
        public static extern short GetKeyState(int nVirtKey);

        [DllImport(User32)]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport(User32)]
        public static extern bool GetKeyboardState(byte[] lpKeyState);

        #endregion

        #region 键盘 / 字符映射

        public const int VK_CAPITAL = 0x14;
        public const int VK_SHIFT = 0x10;
        public const int VK_LSHIFT = 0xA0;
        public const int VK_RSHIFT = 0xA1;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;      // Alt
        public const int VK_LWIN = 0x5B;      // 左 Win 键
        public const int VK_RWIN = 0x5C;      // 右 Win 键
        public const int VK_ESCAPE = 0x1B;
        public const int VK_SPACE = 0x20;
        public const int VK_RETURN = 0x0D;
        public const int VK_BACK = 0x08;
        public const int VK_TAB = 0x09;

        [DllImport(User32, CharSet = CharSet.Unicode)]
        public static extern short VkKeyScan(char ch);

        /// <summary>
        /// 虚拟键码 ⇄ 扫描码互转。
        ///
        /// ★ 为什么需要它：重放按键时如果只填 <c>wVk</c>、<c>wScan</c> 留 0，
        ///   部分底层读取者（AutoCAD 的 acedGrRead 一类）拿不到扫描码，
        ///   仍可能认不出这次按键。填上真实扫描码后，注入事件与物理按键
        ///   在键码层面完全等价。
        /// </summary>
        [DllImport(User32)]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        /// <summary>MapVirtualKey 的转换类型：虚拟键码 → 扫描码。</summary>
        public const uint MAPVK_VK_TO_VSC = 0;

        [DllImport(User32, CharSet = CharSet.Unicode)]
        public static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
            System.Text.StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        [DllImport(User32)]
        public static extern IntPtr GetKeyboardLayout(uint idThread);

        /// <summary>
        /// CapsLock 是否开启。
        ///
        /// ★★ v35 修正：必须用 <c>GetAsyncKeyState</c>，**不能**用 <c>GetKeyState</c>。
        ///
        ///   `GetKeyState` 读的是**调用线程消息队列**里的按键状态快照 ——
        ///   而这个方法是在键盘钩子回调里被调用的，钩子跑在 KeyboardHook 的
        ///   **隐藏宿主线程**上（它自己不接收任何键盘消息）。那个线程的状态快照
        ///   可能一直停留在**线程创建那一刻**，于是永远返回"开启"。
        ///
        ///   现场症状（v34 日志，24 秒内每一个按键都是这一行）：
        ///     `跳过: CapsLock 开启`
        ///   → 无视输入法模式**完全不接管任何按键**，用户看到的就是"不工作"。
        ///
        ///   本文件里读 Ctrl / Alt / Win 的另外三个方法本来就用 `GetAsyncKeyState`
        ///   （全局实时状态），只有这个 CapsLock 用了 `GetKeyState` —— 属于不一致造成的疏漏。
        /// </summary>
        public static bool IsCapsLockOn()
        {
            return (GetAsyncKeyState(VK_CAPITAL) & 0x0001) != 0;
        }

        /// <summary>Shift 是否按下。</summary>
        public static bool IsShiftPressed()
        {
            return (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        }

        /// <summary>Ctrl 是否按下。</summary>
        public static bool IsCtrlPressed()
        {
            return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        }

        /// <summary>Alt 是否按下。</summary>
        public static bool IsAltPressed()
        {
            return (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        }

        /// <summary>
        /// Win 键是否按下（左 / 右任意一个）。
        ///
        /// ★ Win 键是系统级修饰键（Win+D 显示桌面、Win+E 打开资源管理器、
        ///   Win+R 运行、Win+L 锁屏……）。插件在吞键时必须把 Win 组合键**原样放行**，
        ///   否则会把这些系统快捷键一起吞掉（用户实测反馈：Win+D / Win+E 失效）。
        /// </summary>
        public static bool IsWinPressed()
        {
            return (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
                || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
        }

        #endregion

        #region SetWinEventHook

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_OBJECT_FOCUS = 0x8005;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        // 注：刻意不提供 WINEVENT_SKIPOWNPROCESS —— 本插件就运行在 acad.exe 进程内，
        //     跳过本进程事件会丢掉「AutoCAD 重新获得前台焦点」，详见 FocusMonitor.Start。

        public delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport(User32, SetLastError = true)]
        public static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        #endregion

        #region 目标线程的 GUI 信息（取焦点窗口，免 AttachThreadInput）

        /// <summary>
        /// GUITHREADINFO —— GetGUIThreadInfo 的输出结构（Win32 布局，不可增删字段）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>
        /// 取指定线程的 GUI 状态，其中 <c>hwndFocus</c> 就是该线程的键盘焦点窗口。
        ///
        /// ★ 这是取「跨线程焦点窗口」的正确方式：直接查询目标线程，
        ///   不需要 AttachThreadInput。后者要把两个线程的输入队列临时挂到一起，
        ///   实测单次约 200 微秒 —— 放在每次按键都要走的路径上代价过高。
        /// </summary>
        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        #endregion

        #region 窗口图标
        public const int WM_SETICON = 0x0080;
        public const int ICON_SMALL = 0;    // 标题栏左上角（另有 16px）
        public const int ICON_BIG = 1;      // 任务栏 / Alt+Tab

        [DllImport(User32, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(
            IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport(User32, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(
            IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>
        /// 带超时的 SendMessage。
        ///
        /// ★ 为什么给 IME 窗口发消息**必须**带超时：
        ///   IME 窗口由第三方输入法自己实现，一旦它卡住（自绘 UI、联网词库、
        ///   弹候选框时都可能短暂无响应），普通 SendMessage 会把**调用线程**
        ///   无限期挂住。而调用点正好在「命令开始 / 命令结束」的路径上 ——
        ///   等于把 AutoCAD 一起拖死。
        ///   SMTO_ABORTIFHUNG + 短超时保证最坏情况只是"这次拿不到结果"。
        /// </summary>
        [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode,
            EntryPoint = "SendMessageTimeoutW")]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        /// <summary>SendMessageTimeout 标志：目标线程已挂起时立即返回，不再等待。</summary>
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        /// <summary>给 IME 窗口发消息时使用的超时（毫秒）。见 <see cref="SendMessageTimeout"/>。</summary>
        public const uint ImeMsgTimeoutMs = 120;

        #endregion
    }
}
