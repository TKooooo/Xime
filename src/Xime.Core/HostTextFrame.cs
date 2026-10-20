using System;

namespace Xime.Core
{
    /// <summary>
    /// 宿主文本窗口定位器的**契约**（本次扩展新增）。
    ///
    /// ★ 为什么要抽这一层：
    ///   引擎（<see cref="XimeEngine"/>）里判定"命令栏在哪 / 这个窗口像不像文本输入框"
    ///   的逻辑，在 AutoCAD 上是靠 `acad.exe` 的导出函数 `AcadGetTextFrameHandle`
    ///   加一套类名词汇表（`Edit` / `RichEdit` / `HwndWrapper` / `Afx:` / `CLI Palette`）实现的 ——
    ///   而这些**全是 AutoCAD 专有**的。中望 / 浩辰虽然 API 形状与 AutoCAD 高度相似，
    ///   窗口体系却不一样：它们没有那个导出函数，主框架也不是 MFC `Afx:` 类。
    ///
    ///   把这份"宿主词汇表"抽成接口后：
    ///     · 引擎只认本接口，与具体宿主彻底解耦；
    ///     · AutoCAD 沿用原有实现（<see cref="AcadTextFrameProvider"/> 原样转发，行为零变化）；
    ///     · 中望 / 浩辰注入通用实现（<see cref="GenericTextFrameProvider"/>），
    ///       可随现场实测逐步补类名，不必动引擎。
    /// </summary>
    internal interface ITextFrameProvider
    {
        /// <summary>解析"命令栏输入控件"的窗口句柄；失败返回 <see cref="IntPtr.Zero"/>。</summary>
        IntPtr Resolve();

        /// <summary>丢弃解析缓存（切换文档 / 焦点大变动后调用）。</summary>
        void Invalidate();

        /// <summary>是否是"文本窗口"（AutoCAD 里是按 F2 弹出的命令行历史控制台）。</summary>
        bool IsTextWindow(IntPtr hwnd);

        /// <summary>是否属于命令行面板（自身或祖先链上带命令行特征）。</summary>
        bool IsCommandLineWindow(IntPtr hwnd);

        /// <summary>窗口是否可见。</summary>
        bool IsVisible(IntPtr hwnd);

        /// <summary>窗口宽度；取不到返回 0。</summary>
        int WidthOf(IntPtr hwnd);

        /// <summary>导出窗口树（诊断用）。</summary>
        string DumpTree(IntPtr root, int maxDepth);

        /// <summary>类名是否"像输入框"（弱特征也算）。</summary>
        bool IsEditLike(IntPtr hwnd);

        /// <summary>按句柄给"像输入框"程度打分：2=强特征，1=弱特征，0=不像。</summary>
        int MatchStrength(IntPtr hwnd);

        /// <summary>按类名打分（纯函数，可被自测断言钉住）。</summary>
        int MatchStrength(string cls);

        /// <summary>是否是**真正承载文本输入**的控件（强特征）。</summary>
        bool IsTextInputHost(IntPtr hwnd);

        /// <summary>按类名判断是否真正承载文本输入（供自测断言）。</summary>
        bool IsTextInputHostClass(string cls);

        /// <summary>安全读取窗口类名。</summary>
        string GetClassNameSafe(IntPtr hwnd);

        /// <summary>安全读取窗口标题。</summary>
        string GetWindowTextSafe(IntPtr hwnd);
    }

    /// <summary>
    /// 引擎实际调用的**静态门面**。
    ///
    /// ★ 命名与分工（本次扩展）：
    ///   · <c>AcadTextFrame</c> —— AutoCAD 的**具体实现**（原有代码，一行未改），
    ///     自测工具（XimeTest 的 FocusProbe 等）仍直接用它；
    ///   · <c>HostTextFrame</c> —— **引擎**用的门面，按当前宿主转发到
    ///     <see cref="AcadTextFrameProvider"/> 或 <see cref="GenericTextFrameProvider"/>。
    ///
    ///   这样切分的好处：引擎里 30 处调用点只需要把 `AcadTextFrame.` 换成 `HostTextFrame.`，
    ///   而那份经过现场反复打磨（v35 / v36 两次事故）的 AutoCAD 逻辑**完全不动**，
    ///   不会被"顺手重构"改坏。
    ///
    ///   默认实现是 AutoCAD —— 保证原有宿主行为在未显式切换时**逐字节一致**。
    /// </summary>
    internal static class HostTextFrame
    {
        private static ITextFrameProvider _current;

        /// <summary>
        /// 当前生效的宿主实现。
        /// 默认 AutoCAD；浩辰 / 中望 插件在 <c>Initialize()</c> 里换成
        /// <see cref="GenericTextFrameProvider"/>（非 AutoCAD 宿主没有 acad.exe 导出，
        /// 也没有 MFC 类名，必须换）。
        /// </summary>
        internal static ITextFrameProvider Current
        {
            get
            {
                if (_current == null) _current = new AcadTextFrameProvider();
                return _current;
            }
            set { _current = value ?? new AcadTextFrameProvider(); }
        }

        public static IntPtr Resolve() { return Current.Resolve(); }

        public static void Invalidate() { Current.Invalidate(); }

        public static bool IsTextWindow(IntPtr hwnd) { return Current.IsTextWindow(hwnd); }

        public static bool IsCommandLineWindow(IntPtr hwnd) { return Current.IsCommandLineWindow(hwnd); }

        /// <summary>
        /// ★★★ v63：是否属于「动态输入（DYN）提示框」家族
        /// （<c>Afx:…:803:…</c> / 标题 <c>CAcDynInputWndControl</c>）。
        ///
        /// ★ 为什么不放进 <see cref="ITextFrameProvider"/>：这是**窗口结构判据**，
        ///   不是宿主 API 差异。中望 / 浩辰的窗口树里不存在该 MFC 类名与控件 ID ⇒
        ///   本方法自然返回 false，不会误伤（也不需要每个 provider 各写一遍）。
        /// </summary>
        public static bool IsDynamicInputWindow(IntPtr hwnd)
        {
            return AcadTextFrame.IsDynamicInputWindow(hwnd);
        }

        public static bool IsVisible(IntPtr hwnd) { return Current.IsVisible(hwnd); }

        public static int WidthOf(IntPtr hwnd) { return Current.WidthOf(hwnd); }

        public static string DumpTree(IntPtr root, int maxDepth) { return Current.DumpTree(root, maxDepth); }

        public static bool IsEditLike(IntPtr hwnd) { return Current.IsEditLike(hwnd); }

        public static int MatchStrength(IntPtr hwnd) { return Current.MatchStrength(hwnd); }

        public static int MatchStrength(string cls) { return Current.MatchStrength(cls); }

        public static bool IsTextInputHost(IntPtr hwnd) { return Current.IsTextInputHost(hwnd); }

        public static bool IsTextInputHostClass(string cls) { return Current.IsTextInputHostClass(cls); }

        public static string GetClassNameSafe(IntPtr hwnd) { return Current.GetClassNameSafe(hwnd); }

        public static string GetWindowTextSafe(IntPtr hwnd) { return Current.GetWindowTextSafe(hwnd); }
    }
}
