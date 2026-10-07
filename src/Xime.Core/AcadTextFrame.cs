using System;
using System.Runtime.InteropServices;
using System.Text;
using Xime.Core.Native;

namespace Xime.Core
{
    /// <summary>
    /// AutoCAD 命令栏（命令行）输入窗口定位器。
    ///
    /// 背景与设计依据（来自对参考插件 AutomaticIME 的逆向分析）：
    ///
    ///   AutomaticIME 在「命令栏强制英文」这件事上**完全不使用 IMM32**，
    ///   而是：
    ///     1. 通过 acad.exe 导出函数 AcadGetTextFrameHandle() 拿到命令栏框架窗口；
    ///     2. 用 GetWindow(hwnd, GW_CHILD=5) 连下三级，钻到真正的 Edit 子控件；
    ///     3. 对该子控件 PostMessage(WM_KEYDOWN/WM_KEYUP) 直接投递 VK 码。
    ///
    ///   由于 WM_KEYDOWN 的 lParam 里 bit14（上下文码）为 0，
    ///   目标控件会把它当作「无输入法上下文」的按键处理 —— 即英文输入，
    ///   从而**彻底绕过输入法**，无论用户输入法处于中文还是英文态。
    ///
    ///   这解释了我们之前用 IMM32 反复切换却始终「报告成功但实际仍是英文/中文反转」
    ///   的根本原因：AutoCAD 命令栏并不是一个标准的 IME 目标，IMM32 在它上面
    ///   报告的状态与真实输入行为并不一致。正确的做法是绕开 IME，而不是切换它。
    ///
    /// 本类负责：拿到命令栏输入控件的 HWND，并在各级窗口上做类名匹配，
    /// 以便钩子把按键准确投递过去。
    /// </summary>
    internal static class AcadTextFrame
    {
        // ---- 窗口关系常量 ----
        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;

        // ---- acad.exe 导出：取命令栏文本框架窗口（32/64 位各一个修饰名） ----
        //
        // ★ 不用 DllImport("acad.exe", ...) 的原因：
        //   在非 AutoCAD 宿主（如本项目的自测工具、或误加载到别的进程）里，
        //   DllImport 解析失败会**抛异常**。异常构造 + 栈展开实测约 500 微秒，
        //   而这段代码在一次命令栏判定里就会被走到 —— 足以拖垮每次按键的响应。
        //   改为：用 GetModuleHandle 判断 acad.exe 是否在本进程，是则 GetProcAddress
        //   一次性取函数指针并缓存；不是则永久标记"不可用"，之后零开销。
        private const string AcadTextFrameExport64 = "?AcadGetTextFrameHandle@@YAPEAUHWND__@@XZ";
        private const string AcadTextFrameExport32 = "?AcadGetTextFrameHandle@@YAPAUHWND__@@XZ";

        private delegate IntPtr AcadGetTextFrameHandleFn();

        private static AcadGetTextFrameHandleFn _acadGetTextFrame;
        private static bool _acadExportProbed;

        /// <summary>探测并缓存 acad.exe 导出；宿主不是 AutoCAD 时返回 null（只探测一次）。</summary>
        private static AcadGetTextFrameHandleFn GetAcadExport()
        {
            if (_acadExportProbed) return _acadGetTextFrame;
            _acadExportProbed = true;

            try
            {
                var name = IntPtr.Size == 8 ? AcadTextFrameExport64 : AcadTextFrameExport32;

                // ① 主模块（插件必然运行在宿主 exe 里，即 acad.exe）——
                //    传 null 取主模块句柄，比按名字查更可靠：加载器记录的是完整路径。
                var mod = NativeMethods.GetModuleHandle(null);
                var addr = mod != IntPtr.Zero ? NativeMethods.GetProcAddress(mod, name) : IntPtr.Zero;

                // ② 回退：按名字查 acad.exe（部分装载场景下主模块不是它）
                if (addr == IntPtr.Zero)
                {
                    mod = NativeMethods.GetModuleHandle("acad.exe");
                    if (mod != IntPtr.Zero)
                        addr = NativeMethods.GetProcAddress(mod, name);
                }

                if (addr == IntPtr.Zero) return null;  // 非 AutoCAD 宿主 / 导出名不匹配

                _acadGetTextFrame = (AcadGetTextFrameHandleFn)
                    Marshal.GetDelegateForFunctionPointer(addr, typeof(AcadGetTextFrameHandleFn));
            }
            catch (Exception)
            {
                _acadGetTextFrame = null;
            }

            return _acadGetTextFrame;
        }

        [DllImport(NativeMethods.User32, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport(NativeMethods.User32, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int nMaxCount);

        [DllImport(NativeMethods.User32, SetLastError = true)]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        /// <summary>缓存解析结果，避免每次按键都做窗口遍历。</summary>
        private static IntPtr _cached;
        private static int _cacheTick;
        private static bool _cacheValid;

        /// <summary>解析成功后的缓存有效期。</summary>
        private const int CacheHitMs = 3000;

        /// <summary>
        /// 解析**失败**后的负缓存有效期（0.5 秒）。
        ///
        /// ★ 必要性：早先的实现只在 _cached != Zero 时才认缓存，导致"解析失败"
        ///   这条最贵的路径（要跨窗口树遍历）每次调用都要重算一遍。
        ///   命令栏一般长时间存在，0.5 秒内不可能反复出现/消失，负缓存足够安全。
        /// </summary>
        private const int CacheMissMs = 500;

        /// <summary>
        /// 获取 AutoCAD 命令栏输入控件的窗口句柄。
        /// 失败返回 IntPtr.Zero。
        /// </summary>
        public static IntPtr Resolve()
        {
            int now = Environment.TickCount;

            if (_cacheValid)
            {
                // 命中缓存的前提：句柄仍然有效（失败结果为 Zero 时跳过该检查）
                int ttl = _cached != IntPtr.Zero ? CacheHitMs : CacheMissMs;
                if (unchecked(now - _cacheTick) < ttl &&
                    (_cached == IntPtr.Zero || NativeMethods.IsWindow(_cached)))
                {
                    return _cached;
                }
            }

            var h = ResolveCore();
            _cached = h;
            _cacheTick = now;
            _cacheValid = true;
            return h;
        }

        /// <summary>强制丢弃缓存（例如切换文档后）。</summary>
        public static void Invalidate()
        {
            _cached = IntPtr.Zero;
            _cacheTick = 0;
            _cacheValid = false;
        }

        private static IntPtr ResolveCore()
        {
            // ① 最可靠：系统当前真实焦点控件。
            //
            // 用户在 AutoCAD 里敲命令时，焦点必然就在命令栏输入框上，
            // 所以直接取 GetFocus（跨线程已由 GetRealFocusWindow 处理）。
            // 这个方法同时能天然避开「AutoCAD 文本窗口」（F2 窗口）——
            // 因为那个窗口没被聚焦。
            //
            // ★★ 必须要求**强特征**（IsTextInputHost），不能只判 IsEditLike（弱特征也算）。
            //   现场事故（v35 日志）：双击文字进在位编辑时焦点会在
            //     命令栏 ⇄ 在位编辑器 ⇄ **绘图区**(`Afx:00007FF...:28:...`) ⇄ MDI 子框架
            //   之间抖动，而绘图区只算**弱特征**。旧判据 `MatchStrength > 0` 会把它
            //   当成命令栏控件**缓存 3 秒**，于是：
            //     · 投递目标错（注入的字符进不了命令栏）；
            //     · IME 靶点错（绘图区没有 IME 上下文，读数恒为默认的「中文」）。
            try
            {
                var focus = CharInjector.GetRealFocusWindow();
                if (focus != IntPtr.Zero && IsTextInputHost(focus) && !IsTextWindow(focus))
                    return focus;
            }
            catch (Exception) { }

            // ② acad.exe 导出：命令栏文本框架
            //
            // 注意：这一路要格外小心 —— 实测发现 AcadGetTextFrameHandle 有可能
            // 返回「AutoCAD 文本窗口」(F2 控制台) 而不是命令行输入框，
            // 两者类名都是 `Afx:...`（AutoCAD 的通用 MFC 框架类），无法靠类名区分。
            // 因此这里必须用标题 / 文本内容把文本窗口排除掉。
            try
            {
                var getFrame = GetAcadExport();
                if (getFrame != null)
                {
                    var frame = getFrame();
                    if (frame != IntPtr.Zero && !IsTextWindow(frame))
                    {
                        var edit = DrillToEdit(frame);
                        if (edit != IntPtr.Zero && !IsTextWindow(edit)) return edit;
                    }
                }
            }
            catch (Exception)
            {
                // 导出调用本身出错（版本差异）—— 走下面的回退
            }

            // ③ 回退：从当前前台窗口向下钻
            try
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero && !IsTextWindow(fg))
                {
                    var edit = DrillToEdit(fg);
                    if (edit != IntPtr.Zero && !IsTextWindow(edit)) return edit;
                }
            }
            catch (Exception) { }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 判断是否「AutoCAD 文本窗口」—— 即按 F2 弹出的命令行历史控制台。
        ///
        /// ★ 这是一个必须排除的陷阱窗口：
        ///   它的类名同样是 `Afx:...`，标题形如「AutoCAD 文本窗口 - Drawing1.dwg」。
        ///   如果把按键投递给它，命令行将完全收不到输入（用户表现为"命令栏无法输入"）。
        /// </summary>
        public static bool IsTextWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                // 直接做不区分大小写的查找，避免 ToLowerInvariant() 的字符串分配 ——
                // 本方法在 DrillToEdit 里对每个节点都要调用一次，是全树遍历的热点。
                var title = GetWindowTextSafe(hwnd);
                if (title.Length == 0) return false;

                // 中文版 / 英文版标题特征
                if (title.IndexOf("文本窗口", StringComparison.Ordinal) >= 0) return true;
                if (title.IndexOf("text window", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                return false;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 判断给定窗口是否属于「命令行面板」（命令栏控件本身，或其祖先链上有命令栏宿主）。
        ///
        /// 用途：焦点驱动的「文字编辑态」识别中，必须把命令栏整条窗口链排除，
        /// 否则用户「敲命令」会被误判为「编辑文字」→ 命令栏无法输入英文（灾难性回归）。
        ///
        /// ★ 实现要点（重要）：
        ///   优先使用**不依赖 Resolve() 的稳定判据** —— 沿父链匹配 "CLI Palette"。
        ///   原因：Resolve() 的首选策略是"当前焦点控件（只要是 Edit-like）"，
        ///   当天正文字编辑框（Edit 类）获得焦点时，Resolve() 有可能把**编辑框**
        ///   误缓存为命令栏，导致这里把编辑框当成命令栏排除 → 编辑态永远进不去。
        ///   因此 Resolve() 只作为**辅助**判据（相等即命令栏），
        ///   主判据始终是父链上的 CLI Palette 特征。
        /// </summary>
        public static bool IsCommandLineWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                // ---- 主判据：沿父链匹配 CLI Palette（不受 Resolve 缓存影响）----
                var cur = hwnd;
                int guard = 0;
                while (cur != IntPtr.Zero && guard++ < 12)
                {
                    var cls = GetClassNameSafe(cur);
                    if (cls.IndexOf("CLI Palette", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;

                    var title = GetWindowTextSafe(cur);
                    if (title.IndexOf("CLI Palette", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;

                    cur = NativeMethods.GetParent(cur);
                }

                // ---- 辅助判据：与已解析命令栏句柄相等 ----
                //   仅当该句柄确实带命令栏特征（WPF 宿主 HwndWrapper）时才采信，
                //   避免 Resolve() 把天正编辑框误缓存成命令栏而带来的误判。
                //
                // ★ 顺序很关键：先做廉价的类名判断，通过后才去问 Resolve()。
                //   Resolve() 失败时会向下遍历整棵窗口树，代价可达数百微秒；
                //   而上一步的父链遍历已经把 hwnd 自身的类名读出来了（就是循环首轮
                //   的 cls），这里再读一次几乎免费，却能把绝大多数调用（天正编辑框、
                //   普通 Edit 控件）在这道闸门前挡掉。
                var self = GetClassNameSafe(hwnd);
                if (self.IndexOf("HwndWrapper", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var cmdLine = Resolve();
                    if (cmdLine != IntPtr.Zero && hwnd == cmdLine) return true;
                }

                return false;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 在给定窗口的子孙里找到「最像命令栏输入框」的控件。
        ///
        /// 选择策略（打分制，取最高分）：
        ///   1. 必须是 Edit / RichEdit / HwndWrapper（强特征，2 分）为优先；
        ///      仅当找不到强特征时才退回 Afx 框架类（弱特征，1 分）。
        ///   2. 排除「AutoCAD 文本窗口」及其子窗口。
        ///   3. 同样分数下，优先选「当前可见且尺寸较大」的（命令栏通常较宽）。
        /// </summary>
        private static IntPtr DrillToEdit(IntPtr root)
        {
            if (root == IntPtr.Zero) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            int bestScore = 0;

            try
            {
                var stack = new System.Collections.Generic.Stack<IntPtr>();
                stack.Push(root);
                int guard = 0;

                while (stack.Count > 0 && guard++ < 5000)
                {
                    var cur = stack.Pop();

                    // 文本窗口整棵树直接跳过
                    if (IsTextWindow(cur)) continue;

                    // 当前节点自身的匹配强度
                    int self = MatchStrength(cur);
                    if (self > 0)
                    {
                        int score = self * 1000 + VisibilityBonus(cur);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = cur;

                            // 满分（强特征 + 可见 + 够宽）可以直接收工
                            if (self >= 2 && IsVisible(cur) && WidthOf(cur) >= 120)
                                return best;
                        }
                    }

                    for (var c = GetWindow(cur, GW_CHILD);
                         c != IntPtr.Zero;
                         c = GetWindow(c, GW_HWNDNEXT))
                    {
                        stack.Push(c);
                    }
                }
            }
            catch (Exception) { }

            return best;
        }

        /// <summary>可见性打分（可见 +10）。</summary>
        private static int VisibilityBonus(IntPtr hwnd)
        {
            return IsVisible(hwnd) ? 10 : 0;
        }

        /// <summary>窗口可见性。</summary>
        public static bool IsVisible(IntPtr hwnd)
        {
            try { return IsWindowVisible(hwnd); }
            catch (Exception) { return false; }
        }

        /// <summary>窗口宽度（取失败返回 0）。</summary>
        public static int WidthOf(IntPtr hwnd)
        {
            try
            {
                NativeMethods.RECT r;
                if (GetWindowRect(hwnd, out r))
                    return r.Right - r.Left;
            }
            catch (Exception) { }
            return 0;
        }

        [DllImport(NativeMethods.User32, SetLastError = true)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport(NativeMethods.User32, SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

        /// <summary>
        /// 从给定窗口开始，导出子孙窗口树（类名 / 标题 / 强度 / 可见 / 宽度）。
        ///
        /// 用途：诊断 —— 当自动定位失败或误命中时，用它把真实窗口结构打出来，
        /// 从而找到正确的命令栏控件类名。
        /// </summary>
        public static string DumpTree(IntPtr root, int maxDepth)
        {
            var sb = new StringBuilder();
            try
            {
                DumpNode(sb, root, 0, maxDepth);
            }
            catch (Exception ex)
            {
                sb.AppendLine("  遍历异常: " + ex.Message);
            }
            return sb.ToString();
        }

        private static void DumpNode(StringBuilder sb, IntPtr hwnd, int depth, int maxDepth)
        {
            if (hwnd == IntPtr.Zero) return;
            if (depth > maxDepth) return;

            try
            {
                var cls = GetClassNameSafe(hwnd);
                var title = GetWindowTextSafe(hwnd);
                if (title.Length > 40) title = title.Substring(0, 40) + "…";

                sb.Append(' ', depth * 2);
                sb.Append("0x").Append(hwnd.ToInt64().ToString("X"));
                sb.Append(" [").Append(cls).Append(']');
                sb.Append(" 强度=").Append(MatchStrength(hwnd));
                sb.Append(IsVisible(hwnd) ? " 可见" : " 隐藏");
                sb.Append(" 宽=").Append(WidthOf(hwnd));
                if (IsTextWindow(hwnd)) sb.Append(" ⚠文本窗口");
                if (title.Length > 0) sb.Append(" \"").Append(title).Append('"');
                sb.AppendLine();

                for (var c = GetWindow(hwnd, GW_CHILD);
                     c != IntPtr.Zero;
                     c = GetWindow(c, GW_HWNDNEXT))
                {
                    DumpNode(sb, c, depth + 1, maxDepth);
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 类名是否像命令栏输入控件。
        ///
        /// ★ 顺序很重要：先判「强烈特征」（edit / richedit / hwndwrapper），
        ///   再判「弱特征」（afx / cicerouiwndframe）。
        ///   因为 `Afx:...` 是 AutoCAD 的通用 MFC 框架类，文本框、面板、
        ///   甚至 F2 文本窗口都用它，用它单独判定会误命中。
        ///   真正能接收输入的通常是 Edit / RichEdit / HwndWrapper。
        /// </summary>
        public static bool IsEditLike(IntPtr hwnd)
        {
            return MatchStrength(hwnd) > 0;
        }

        /// <summary>
        /// 窗口的「像输入框」程度打分：2=强特征，1=弱特征，0=不像。
        /// 用于在多个候选里挑最像命令栏输入框的那个。
        /// </summary>
        public static int MatchStrength(IntPtr hwnd)
        {
            return MatchStrength(GetClassNameSafe(hwnd));
        }

        /// <summary>
        /// 按**类名**打分（纯函数，不碰 HWND）。
        ///
        /// ★ 抽出来是为了让自测工具能用**可执行断言**钉住"哪些类名算强特征" ——
        ///   本轮 v36 的事故窗口 `Afx:00007FF...`（AutoCAD 绘图区）就是被它挡住的。
        /// </summary>
        public static int MatchStrength(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return 0;

            // 直接做不区分大小写的查找；本方法在窗口树遍历里对每个节点都会调用，
            // 之前先 ToLowerInvariant() 会为每个节点额外分配一个字符串。
            const StringComparison IC = StringComparison.OrdinalIgnoreCase;

            // 强特征：这些类名才是真正承载文本输入的控件
            if (cls.IndexOf("richedit", IC) >= 0) return 2;      // RichEdit20W / RichEdit50W
            if (cls.IndexOf("edit", IC) >= 0) return 2;          // Edit / WindowsForms10.EDIT
            if (cls.IndexOf("hwndwrapper", IC) >= 0) return 2;   // WPF 宿主（部分版本命令栏）

            // 弱特征：AutoCAD 通用 MFC 框架类（文本框/面板/F2窗口都会用）
            if (cls.IndexOf("afx", IC) >= 0) return 1;
            if (cls.IndexOf("cicerouiwndframe", IC) >= 0) return 1;

            return 0;
        }

        /// <summary>
        /// 这个窗口是不是**真正承载文本输入**的控件（强特征）。
        ///
        /// ★★ 为什么必须区分强弱特征（v36 事故）：
        ///   `Afx:00007FF642700000:28:...` 是 AutoCAD 的**绘图区**窗口，类名里带 "afx"
        ///   只算**弱特征**。旧的 `IsEditLike` 判的是 `MatchStrength > 0`，
        ///   于是当焦点落在绘图区上时（双击文字进在位编辑、放下文字、切回绘图区…），
        ///   命令栏解析会把**绘图区**当成"命令栏输入控件"并缓存 3 秒。
        ///   后果有两层：
        ///     ① 投递目标错（字符进不了命令栏）；
        ///     ② IME 靶点错 —— 绘图区没有 IME 上下文，读出来是兼容层**默认值**（恒为中文）。
        ///
        ///   后果 ② 在 v35 的日志里表现得很典型：需要英文时读到 Chinese（≠English 于是照样切），
        ///   需要中文时读到 Chinese（=Chinese 于是"无需动作"）—— **中文永远切不过去**。
        /// </summary>
        public static bool IsTextInputHost(IntPtr hwnd)
        {
            return MatchStrength(hwnd) >= 2;
        }

        /// <summary>按类名判断是否是真正承载文本输入的控件（供自测断言）。</summary>
        public static bool IsTextInputHostClass(string cls)
        {
            return MatchStrength(cls) >= 2;
        }

        // 复用 StringBuilder：本类的字符串读取在窗口树遍历中会被调用成千上万次，
        // 每次都 new 一个 256/512 字符的缓冲区是纯粹的浪费。
        // 用 [ThreadStatic] 而非共享字段，避免钩子线程与 UI 线程互相干扰。
        [ThreadStatic] private static StringBuilder _clsBuf;
        [ThreadStatic] private static StringBuilder _titleBuf;

        /// <summary>安全读取窗口类名。</summary>
        public static string GetClassNameSafe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            try
            {
                var sb = _clsBuf;
                if (sb == null) { sb = _clsBuf = new StringBuilder(256); }
                else sb.Length = 0;

                int n = GetClassName(hwnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : string.Empty;
            }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>安全读取窗口标题。</summary>
        public static string GetWindowTextSafe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            try
            {
                var sb = _titleBuf;
                if (sb == null) { sb = _titleBuf = new StringBuilder(512); }
                else sb.Length = 0;

                int n = GetWindowText(hwnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : string.Empty;
            }
            catch (Exception) { return string.Empty; }
        }
    }
}
