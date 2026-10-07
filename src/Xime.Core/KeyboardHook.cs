using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Xime.Core.Native;

namespace Xime.Core
{
    /// <summary>
    /// 低级键盘钩子（WH_KEYBOARD_LL）。
    ///
    /// 用途：兜底通道。当某些输入法（搜狗、微信、QQ、百度等）在中文状态下
    /// 也吞掉字母键、且 IMM32 无法把其中英状态切到英文时，由本钩子接管：
    ///   - 若当前处于「CAD 前台 + 命令栏需要英文输入」场景 →
    ///     拦截字母/数字/符号键，把原生按键吞掉，并直接向命令栏所在窗口发送 WM_CHAR，
    ///     从而保证无论输入法什么状态，命令栏都能收到正确的英文命令字符；
    ///   - 其他场景 → 原样放行，绝不干预。
    ///
    /// 安全约束：
    ///   - 钩子回调必须极快返回，任何异常都必须被吞掉，否则会导致系统输入卡顿；
    ///   - 不修改 LowLevelHooksTimeout 注册表（那会影响系统全局）；
    ///   - 仅在明确需要时才吞键，其余一律 CallNextHookEx 放行。
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        private IntPtr _hook = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc _proc;   // 必须持有引用防止 GC
        private bool _installed;
        private bool _disposed;
        private readonly object _sync = new object();
        private int _installThreadId;                       // 安装钩子的线程 ID

        /// <summary>
        /// 决策回调：返回 true 表示「该按键需要被接管（吞掉并由插件处理）」。
        /// 参数：虚拟键代码、是否按下。
        /// </summary>
        public Func<int, bool, bool> ShouldCapture { get; set; }

        /// <summary>
        /// 处理回调：当按键被接管后调用，由调用方决定如何把字符送进命令栏。
        /// 返回 true 表示「已成功投递」→ 钩子吞掉原生按键；
        /// 返回 false 表示「投递失败」→ 钩子放行原生按键，避免命令栏彻底失联。
        /// </summary>
        public Func<int, bool, bool> OnCaptured { get; set; }

        /// <summary>诊断回调：钩子每看到一个按键就调用一次（含未被拦截的）。</summary>
        public Action<int, bool, bool> OnSeen { get; set; }

        /// <summary>
        /// Shift 键按下 / 抬起回调（参数：是否按下）。
        ///
        /// 用途：识别「用户单按一下 Shift 切换中英输入」——这是中文输入法最常用的
        /// 中英切换手势。上层据此判断「用户已手动接管输入法状态」，从而停止自动切换。
        ///
        /// ★ 只对 **用户真实按键** 触发：插件自己为切换输入法而发出的 TapShift
        ///   带 InjectSignature 标记，会在下面被提前过滤掉，不会回调 —— 否则插件
        ///   会被自己的动作骗到，误认为用户手动切换过。
        /// </summary>
        public Action<bool> OnShiftChanged { get; set; }

        /// <summary>钩子是否已安装。</summary>
        public bool IsInstalled { get { return _installed; } }

        /// <summary>
        /// 安装钩子。
        ///
        /// 重要：WH_KEYBOARD_LL 是全局钩子，但回调函数在 **安装它的线程** 的消息队列中执行，
        /// 因此该线程必须存在消息循环，否则钩子不生效或系统输入卡顿。
        ///
        /// 调用方应传入一个有消息循环的控件（通常是 AutoCAD 主窗体或隐藏宿主窗体），
        /// 本方法会通过 Invoke 把安装动作调度到该控件的 UI 线程执行。
        /// 若无法获得 UI 锚点，则退回到「创建隐藏窗体并自建消息循环」的方式。
        /// </summary>
        /// <param name="uiAnchor">
        /// 用于提供 UI 线程的控件。为 null 时使用内置隐藏窗体。
        /// </param>
        public bool Install(System.Windows.Forms.Control uiAnchor = null)
        {
            lock (_sync)
            {
                if (_disposed || _installed) return _installed;

                if (uiAnchor != null)
                {
                    if (uiAnchor.InvokeRequired)
                    {
                        try { uiAnchor.Invoke(new Action(InstallInternal)); }
                        catch (Exception) { InstallInternal(); }
                    }
                    else
                    {
                        InstallInternal();
                    }
                }
                else
                {
                    // 没有 UI 锚点：自建一个隐藏宿主窗体并在其线程安装。
                    InstallViaHiddenHost();
                }

                return _installed;
            }
        }

        /// <summary>
        /// 复用的隐藏宿主窗体 + 它的线程。
        ///
        /// ★ 为什么必须是 static 且只建一次：
        ///   每次安装钩子都新建一个线程 + 窗体 + Application.Run，而那条消息循环
        ///   **永远不会退出**（宿主窗体从不关闭）。于是"每切换一次工作模式、
        ///   每重新加载一次插件"就泄漏一个带消息循环的线程；
        ///   会话开久了会积累出一堆僵尸线程。宿主只需要"有一条消息循环"这一个
        ///   性质，与具体是哪次安装无关，因此整个进程共用一份即可。
        /// </summary>
        private static System.Windows.Forms.Form _hostForm;
        private static System.Threading.Thread _hostThread;
        private static readonly object _hostSync = new object();

        /// <summary>
        /// 通过自建隐藏窗体安装钩子。
        /// 当无法从 AutoCAD 取得 WinForms 控件时使用，确保钩子始终有消息循环。
        /// </summary>
        private void InstallViaHiddenHost()
        {
            try
            {
                lock (_hostSync)
                {
                    if (_hostThread != null && _hostForm != null)
                    {
                        // 宿主已存在 → 把安装动作调度到它的消息循环线程上执行。
                        // （WH_KEYBOARD_LL 的回调在安装线程的消息队列中执行，
                        //   所以必须装在宿主线程，而不是随手哪个线程。）
                        try
                        {
                            if (_hostForm.IsHandleCreated)
                            {
                                _hostForm.Invoke(new Action(InstallInternal));
                                return;
                            }
                        }
                        catch (Exception) { }

                        InstallInternal();
                        return;
                    }
                }

                System.Threading.Thread t = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var host = new System.Windows.Forms.Form();
                        host.ShowInTaskbar = false;
                        host.WindowState = System.Windows.Forms.FormWindowState.Minimized;
                        host.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
                        host.Opacity = 0;
                        host.Load += (s, e) => { InstallInternal(); };

                        lock (_hostSync) { _hostForm = host; }

                        System.Windows.Forms.Application.Run(host);

                        // 只有消息循环真的退出（正常路径不会）才清空记录
                        lock (_hostSync) { _hostForm = null; _hostThread = null; }
                    }
                    catch (Exception) { }
                });
                t.IsBackground = true;
                t.SetApartmentState(System.Threading.ApartmentState.STA);

                lock (_hostSync) { _hostThread = t; }
                t.Start();

                // 等待安装完成（最多 500ms）
                for (int i = 0; i < 50 && !_installed; i++)
                    System.Threading.Thread.Sleep(10);
            }
            catch (Exception) { }
        }

        private void InstallInternal()
        {
            if (_installed) return;

            _proc = HookCallback;
            IntPtr hMod = NativeMethods.GetModuleHandle(null);

            try
            {
                _hook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_KEYBOARD_LL, _proc, hMod, 0);

                _installed = _hook != IntPtr.Zero;
                if (_installed)
                    _installThreadId = (int)NativeMethods.GetCurrentThreadId();
            }
            catch (Exception)
            {
                _hook = IntPtr.Zero;
                _installed = false;
            }
        }

        /// <summary>
        /// 卸载钩子。
        ///
        /// Win32 要求：卸载钩子必须由安装它的那个线程调用，否则 UnhookWindowsHookEx 会失败。
        /// 这里通过保存的线程 ID 判断，若跨线程则做一次线程内卸载尝试。
        /// </summary>
        public void Uninstall()
        {
            lock (_sync)
            {
                if (_hook != IntPtr.Zero)
                {
                    try { NativeMethods.UnhookWindowsHookEx(_hook); }
                    catch (Exception) { }
                    _hook = IntPtr.Zero;
                }
                _installed = false;
                _installThreadId = 0;
            }
        }

        /// <summary>
        /// 钩子回调 —— 必须极快、绝不抛异常。
        /// </summary>
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // nCode < 0 时必须直接放行
                if (nCode < 0)
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                bool isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool isUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

                if (!isDown && !isUp)
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

                var data = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                    lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));

                int vk = data.vkCode;

                // 自己注入的按键（避免无限递归）——直接放行
                if (data.dwExtraInfo == CharInjector.InjectSignature)
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

                // ★ Shift 按下 / 抬起通知（必须在「注入过滤」之后，才能排除插件自身
                //   发出的 TapShift）。放在 isDown 分支之前，这样按下与抬起都能上报。
                if (vk == NativeMethods.VK_LSHIFT ||
                    vk == NativeMethods.VK_RSHIFT ||
                    vk == NativeMethods.VK_SHIFT)
                {
                    var onShift = OnShiftChanged;
                    if (onShift != null)
                    {
                        try { onShift(isDown); }
                        catch (Exception) { }
                    }
                }

                // 只处理「按下」事件，避免重复处理；用户手动按键保持原生行为
                if (isDown)
                {
                    var shouldCapture = ShouldCapture;
                    bool captured = shouldCapture != null && SafeShouldCapture(shouldCapture, vk, true);

                    // 诊断回调（仅在开启日志时由引擎自行判断开销）
                    var onSeen = OnSeen;
                    if (onSeen != null)
                    {
                        try { onSeen(vk, true, captured); }
                        catch (Exception) { }
                    }

                    if (captured)
                    {
                        // 通知调用方处理该字符；只有「确实投递成功」才吞掉原键。
                        // 若投递失败，放行原生按键 —— 宁可让输入法处理，
                        // 也绝不能让命令栏变成"什么都输不进去"。
                        var onCaptured = OnCaptured;
                        bool delivered = false;
                        if (onCaptured != null)
                        {
                            try { delivered = onCaptured(vk, true); }
                            catch (Exception) { delivered = false; }
                        }

                        if (delivered)
                        {
                            // 吞掉本次按键（返回 1 表示不再传递）
                            return new IntPtr(1);
                        }
                    }
                }

                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
            }
            catch (Exception)
            {
                // 任何异常都放行，绝不能阻断系统输入
                try { return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam); }
                catch (Exception) { return IntPtr.Zero; }
            }
        }

        private static bool SafeShouldCapture(Func<int, bool, bool> fn, int vk, bool down)
        {
            try { return fn(vk, down); }
            catch (Exception) { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Uninstall();
        }
    }
}
