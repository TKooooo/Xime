using System;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;
using Xime.Core.Native;

namespace Xime.Core
{
    /// <summary>
    /// 窗口焦点 / 最小化监控器。
    ///
    /// 职责：
    ///   - 监听系统前台窗口变化，判断当前是否处于 AutoCAD 主窗口；
    ///   - CAD 最小化或切到其他程序时触发 <see cref="CadDeactivated"/>，
    ///     让插件退出接管并恢复用户的输入法状态；
    ///   - CAD 重新回到前台时触发 <see cref="CadActivated"/>。
    /// </summary>
    public sealed class FocusMonitor : IDisposable
    {
        private readonly NativeMethods.WinEventDelegate _callback;   // 必须持有引用，防止被 GC
        private readonly NativeMethods.WinEventDelegate _focusCallback;
        private IntPtr _hook = IntPtr.Zero;
        private IntPtr _focusHook = IntPtr.Zero;

        private IntPtr _acadMainWindow = IntPtr.Zero;
        private uint _acadProcessId;
        private bool _isCadActive;
        private bool _disposed;

        /// <summary>AutoCAD 主窗口句柄。</summary>
        public IntPtr AcadMainWindow
        {
            get { return _acadMainWindow; }
        }

        /// <summary>AutoCAD 是否当前处于活动（前台且非最小化）状态。</summary>
        public bool IsCadActive
        {
            get { return _isCadActive; }
        }

        /// <summary>CAD 进入前台（且未最小化）。</summary>
        public event EventHandler CadActivated;

        /// <summary>CAD 失去前台或被最小化。</summary>
        public event EventHandler CadDeactivated;

        /// <summary>任意窗口前台变化（可用于刷新状态）。</summary>
        public event EventHandler ForegroundChanged;

        /// <summary>
        /// 键盘焦点落到了某个控件上（EVENT_OBJECT_FOCUS）。
        /// 参数为新获得焦点的窗口句柄。
        ///
        /// 用途：AutoCAD 双击文字后，部分第三方（如天正单行文字）**不经过命令系统**，
        /// 而是直接弹出文字编辑对话框 —— 此时 CommandWillStart 不会触发。
        /// 通过本事件捕获「焦点进入编辑框」，可在这种场景下主动切中文。
        /// </summary>
        public event Action<IntPtr> FocusObjectChanged;

        public FocusMonitor(IntPtr acadMainWindow)
        {
            _acadMainWindow = acadMainWindow;
            _callback = OnWinEvent;
            _focusCallback = OnFocusWinEvent;

            if (_acadMainWindow != IntPtr.Zero)
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(_acadMainWindow, out pid);
                _acadProcessId = pid;
            }
        }

        /// <summary>开始监听系统前台事件。</summary>
        public void Start()
        {
            if (_disposed) return;
            if (_hook != IntPtr.Zero) return;

            // 注意：这里刻意 **不** 使用 WINEVENT_SKIPOWNPROCESS。
            // 本插件以 DLL 形式运行在 acad.exe 进程内，"own process" 就是 AutoCAD 本身；
            // 若跳过本进程事件，则「AutoCAD 重新获得前台焦点」这一关键事件将被丢弃，
            // 导致 CAD 从其它程序切回来时无法及时 Engage。
            // 只监听 EVENT_SYSTEM_FOREGROUND（前台窗口变化）即可，语义清晰且足够。
            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _callback,
                0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);

            // 单独挂一个「焦点对象变化」钩子（EVENT_OBJECT_FOCUS）：
            // 用于捕获「双击文字 → 弹出编辑框」这类不经过命令系统的编辑场景。
            // 同样不使用 SKIPOWNPROCESS —— 本插件就在 acad.exe 进程内。
            _focusHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_FOCUS,
                NativeMethods.EVENT_OBJECT_FOCUS,
                IntPtr.Zero,
                _focusCallback,
                0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);

            // 立即评估一次当前状态
            EvaluateState(raiseEvents: true);
        }

        /// <summary>停止监听。</summary>
        public void Stop()
        {
            if (_hook != IntPtr.Zero)
            {
                try { NativeMethods.UnhookWinEvent(_hook); }
                catch (Exception) { /* ignore */ }
                _hook = IntPtr.Zero;
            }
            if (_focusHook != IntPtr.Zero)
            {
                try { NativeMethods.UnhookWinEvent(_focusHook); }
                catch (Exception) { /* ignore */ }
                _focusHook = IntPtr.Zero;
            }
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // 只在涉窗口本身（idObject == 0，即 OBJID_WINDOW）时处理
            if (idObject != 0)
                return;

            try
            {
                if (ForegroundChanged != null)
                    ForegroundChanged(this, EventArgs.Empty);

                EvaluateState(raiseEvents: true);
            }
            catch (Exception)
            {
                // 事件回调中绝不抛出异常
            }
        }

        /// <summary>
        /// 焦点对象变化回调（EVENT_OBJECT_FOCUS）。
        /// 只在焦点落在「AutoCAD 进程内的窗口」时才向外抛出，避免干扰其它软件。
        /// </summary>
        private void OnFocusWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero) return;

            try
            {
                // 只关心 AutoCAD 进程内的焦点变化
                uint pid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                if (_acadProcessId == 0 || pid != _acadProcessId)
                    return;

                var h = FocusObjectChanged;
                if (h != null) h(hwnd);
            }
            catch (Exception)
            {
                // 事件回调中绝不抛出异常
            }
        }

        /// <summary>
        /// 重新评估 CAD 是否活动，并按需触发事件。
        /// </summary>
        /// <param name="raiseEvents">是否触发事件（外部轮询调用时可传 false 只更新状态）。</param>
        public void EvaluateState(bool raiseEvents)
        {
            bool active = ComputeIsCadActive();

            if (active == _isCadActive)
                return;

            _isCadActive = active;

            if (!raiseEvents) return;

            if (active)
            {
                var h = CadActivated;
                if (h != null) h(this, EventArgs.Empty);
            }
            else
            {
                var h = CadDeactivated;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        /// <summary>判断 AutoCAD 是否为当前活动窗口。</summary>
        private bool ComputeIsCadActive()
        {
            if (_acadMainWindow == IntPtr.Zero || !NativeMethods.IsWindow(_acadMainWindow))
                return false;

            // 最小化 —— 视为非活动，插件必须退出接管
            if (NativeMethods.IsIconic(_acadMainWindow))
                return false;

            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero)
                return false;

            // 直接匹配主窗口
            if (fg == _acadMainWindow)
                return true;

            // 判断前台窗口是否属于 AutoCAD 进程（例如命令行浮动窗口、属性面板、
            // 以及天正/探索者等宿主在同一进程内创建的窗口）
            uint pid;
            NativeMethods.GetWindowThreadProcessId(fg, out pid);
            if (pid != 0 && pid == _acadProcessId)
                return true;

            // 向上追溯父窗口，处理 CAD 内部的子窗口（如命令行宿主）
            var parent = fg;
            int guard = 0;
            while (parent != IntPtr.Zero && guard++ < 10)
            {
                if (parent == _acadMainWindow)
                    return true;
                parent = NativeMethods.GetParent(parent);
            }

            return false;
        }

        /// <summary>
        /// 判断**指定窗口**是否为「文字编辑框」类控件。
        ///
        /// 用于双击文字编辑场景：天正单行文字双击后会弹出一个编辑框，
        /// 焦点落入该框时应视为「正在输入中文内容」。
        ///
        /// 覆盖的类名：
        ///   · 原生 Win32 Edit / RichEdit
        ///   · WPF TextBox（HwndWrapper[DefaultDomain;;...]）
        ///   · MFC 对话框里的编辑控件（AfxWnd / Afx:...，需配合 IsVisible+可编辑启发式）
        ///   · 常见第三方（天正/探索者在对话框里自绘的编辑区）
        /// </summary>
        public static bool IsEditBoxWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
                return false;

            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return IsEditBoxClass(sb.ToString());
        }

        /// <summary>
        /// 按**窗口类名**判断它是不是"真正承接受输入的控件"。
        ///
        /// ★ 抽成纯字符串判据（不碰 HWND）有两个用处：
        ///   1. 让自测工具能用**可执行断言**钉住"哪些类名算编辑控件" ——
        ///      本轮 v35 的事故窗口 `Afx:00007FF...`（AutoCAD 绘图区）
        ///      就是被它正确排除掉的，靠注释记不住；
        ///   2. IME 靶点解析与编辑态判定共用**同一处**定义，不会各自漂移。
        ///
        /// ★ 排查提示：AutoCAD 的类名分三类，别只看前缀 "Afx"：
        ///   · `Afx:00007FF...`（带冒号）= 绘图区 / 子框架 —— **不是**编辑控件；
        ///   · `AfxMDIFrame100u` / `AfxFrameOrView...` = 主窗口 / 视图框架 —— 不是；
        ///   · `AfxWnd140u` = MFC 编辑控件（天正对话框常见）—— **是**。
        ///
        /// ★ 为什么"不是编辑控件"的窗口绝不能当 IME 靶点：
        ///   IME 上下文只挂在真正承接受输入的控件上。给它一个没有 IME 上下文的
        ///   窗口，`ImmGetContext` 会给出**兼容层的默认转换状态**（实测恒为「中文」），
        ///   于是"需要中文"被判成"已经是中文，无需动作" → **永远切不到中文**。
        /// </summary>
        public static bool IsEditBoxClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;

            // 明确的可编辑控件类名
            if (cls.StartsWith("Edit", StringComparison.OrdinalIgnoreCase)) return true;
            if (cls.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase)) return true;
            if (cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) return true;
            if (cls.IndexOf("TextBox", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // WPF 控件宿主（AutoCAD 2013+ 的对话框常用 WPF 实现）
            if (cls.IndexOf("HwndWrapper", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // MFC 编辑控件（AfxWnd 系列，天正对话框常见）
            if (cls.StartsWith("AfxWnd", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>取窗口类名（诊断用，取不到返回空串）。</summary>
        public static string GetWindowClass(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>取窗口标题（诊断用）。</summary>
        public static string GetWindowText(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>更新 AutoCAD 主窗口句柄（切换文档或重开窗口后调用）。</summary>
        public void UpdateAcadWindow(IntPtr hwnd)
        {
            if (hwnd == _acadMainWindow) return;
            _acadMainWindow = hwnd;
            if (hwnd != IntPtr.Zero)
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                _acadProcessId = pid;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
