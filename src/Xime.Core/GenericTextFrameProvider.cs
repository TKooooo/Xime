using System;
using System.Runtime.InteropServices;
using System.Text;
using Xime.Core.Native;

namespace Xime.Core
{
    /// <summary>
    /// **宿主中立**的文本窗口定位器（中望 CAD / 浩辰 CAD 用）。
    ///
    /// ★ 与 AutoCAD 版的本质差别：
    ///   AutoCAD 版有一条"最可靠"的路 —— 调 `acad.exe` 导出函数
    ///   `AcadGetTextFrameHandle()` 直接拿到命令栏框架窗口。
    ///   中望 / 浩辰**没有**这个导出（它们的宿主 exe 是 `ZWCAD.exe` / `gcad.exe`），
    ///   所以这里只剩两条通用路径：
    ///     ① 系统当前真实焦点控件（用户敲命令时焦点就在命令栏上）；
    ///     ② 从当前前台窗口向下钻。
    ///
    /// ★ 类名词汇表需要**现场标定**（这是本实现唯一的不确定项）：
    ///   两家宿主的命令栏控件类名与原版 AutoCAD 未必相同。这里先给出一份
    ///   "最大公约数"词汇表（`Edit` / `RichEdit` / WPF `HwndWrapper` / `Scintilla` 等），
    ///   并在 `IsCommandLineWindow` 里额外认命令行类的中英文标题特征。
    ///   现场用 `XIMETREE` 命令导出窗口树后，把真实类名补进
    ///   <see cref="MatchStrength(string)"/> 即可 —— 不必改动引擎。
    ///
    /// ★ 说明：窗口枚举 / 类名 / 标题读取这些**纯 Win32** 的部分直接复用
    ///   <see cref="AcadTextFrame"/> 的实现（它们与宿主无关），避免重复维护一份
    ///   同样的 P/Invoke 与 StringBuilder 缓存。
    /// </summary>
    internal sealed class GenericTextFrameProvider : ITextFrameProvider
    {
        // ---- 窗口关系常量 ----
        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;

        [DllImport(NativeMethods.User32, SetLastError = true)]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        // ---- 解析缓存（与 AutoCAD 版同策略：命中 3s / 未命中 0.5s）----
        private static IntPtr _cached;
        private static int _cacheTick;
        private static bool _cacheValid;

        private const int CacheHitMs = 3000;
        private const int CacheMissMs = 500;

        /// <summary>
        /// 解析命令栏输入控件；失败返回 <see cref="IntPtr.Zero"/>。
        /// 没有 acad.exe 导出可依赖，因此只在"焦点 + 前台窗口"两条通用路径上找。
        /// </summary>
        public IntPtr Resolve()
        {
            int now = Environment.TickCount;

            if (_cacheValid)
            {
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

        public void Invalidate()
        {
            _cached = IntPtr.Zero;
            _cacheTick = 0;
            _cacheValid = false;
        }

        private IntPtr ResolveCore()
        {
            // ① 当前真实焦点控件（强特征才算）。与 AutoCAD 版同一判据：
            //    绝不让"弱特征"（框架类名）通过，否则会把绘图区误缓存成命令栏。
            try
            {
                var focus = CharInjector.GetRealFocusWindow();
                if (focus != IntPtr.Zero && IsTextInputHost(focus) && !IsTextWindow(focus))
                    return focus;
            }
            catch (Exception) { }

            // ② 从当前前台窗口向下钻
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

        /// <summary>在给定窗口的子孙里找"最像命令栏输入框"的控件（打分制，取最高分）。</summary>
        private IntPtr DrillToEdit(IntPtr root)
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

                    if (IsTextWindow(cur)) continue;

                    int self = MatchStrength(cur);
                    if (self > 0)
                    {
                        int score = self * 1000 + (IsVisible(cur) ? 10 : 0);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = cur;

                            // 满分（强特征 + 可见 + 够宽）直接收工
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

        /// <summary>
        /// 是否"文本窗口"（按 F2 弹出的命令行历史控制台）。
        /// 判据与 AutoCAD 版一致：标题里带"文本窗口"/"text window"。
        /// 中望 / 浩辰的命令行历史窗口沿用了同样的中文命名，故可直接复用。
        /// </summary>
        public bool IsTextWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                var title = GetWindowTextSafe(hwnd);
                if (title.Length == 0) return false;

                if (title.IndexOf("文本窗口", StringComparison.Ordinal) >= 0) return true;
                if (title.IndexOf("text window", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                return false;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 是否属于命令行面板。
        ///
        /// ★ 主判据沿用父链上的 "CLI Palette"（AutoCAD 系命令栏宿主的通用类名，
        ///   中望 / 浩辰沿用了这一命名）；再补一层中英文"命令行"标题特征，
        ///   以覆盖类名不同的版本。
        /// </summary>
        public bool IsCommandLineWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                // ---- 主判据：沿**父链**匹配命令行特征 ----
                if (ChainHitsCommandLine(hwnd, useOwner: false)) return true;

                // ---- v59：父链走不通时再沿**属主链**找一次 ----
                //   中望 / 浩辰的命令栏同样是 WPF（实测类名 `HwndWrapper[…]`），
                //   因此命令自动完成一类的下拉浮层与 AutoCAD 完全同构：
                //   `GetParent()` 返回 NULL、只有 `GW_OWNER` 能走回命令栏。
                //   少了这一步，它们会被 `IsEditBoxClass` 的 HwndWrapper 规则
                //   判成"文字编辑框"，把命令名交给输入法（同 AutoCAD 的豆包事故）。
                if (ChainHitsCommandLine(hwnd, useOwner: true)) return true;

                // 与已解析命令栏同族（仅当自身带强特征时采信，避免误判）
                var self = GetClassNameSafe(hwnd);
                if (MatchStrength(self) >= 2)
                {
                    var cmdLine = Resolve();
                    if (cmdLine != IntPtr.Zero)
                    {
                        if (hwnd == cmdLine) return true;
                        // ★ v59：不要求相等，只要求"能走到命令栏"（含派生浮层 / 子孙控件）
                        if (ChainReaches(hwnd, cmdLine)) return true;
                    }
                }

                return false;
            }
            catch (Exception) { return false; }
        }

        /// <summary>沿父链或属主链查找命令行特征（v59）。</summary>
        private bool ChainHitsCommandLine(IntPtr hwnd, bool useOwner)
        {
            var cur = hwnd;
            int guard = 0;
            while (cur != IntPtr.Zero && guard++ < 12)
            {
                var cls = GetClassNameSafe(cur);
                if (cls.IndexOf("CLI Palette", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (cls.IndexOf("CommandLine", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                var title = GetWindowTextSafe(cur);
                if (title.IndexOf("CLI Palette", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (title.IndexOf("命令行", StringComparison.Ordinal) >= 0)
                    return true;

                cur = useOwner
                    ? NativeMethods.GetWindow(cur, NativeMethods.GW_OWNER)
                    : NativeMethods.GetParent(cur);
            }
            return false;
        }

        /// <summary>父链/属主链能否走到 <paramref name="target"/>（v59）。</summary>
        private bool ChainReaches(IntPtr hwnd, IntPtr target)
        {
            if (target == IntPtr.Zero) return false;
            var cur = hwnd;
            int guard = 0;
            while (cur != IntPtr.Zero && guard++ < 12)
            {
                if (cur == target) return true;
                var p = NativeMethods.GetParent(cur);
                cur = p != IntPtr.Zero ? p : NativeMethods.GetWindow(cur, NativeMethods.GW_OWNER);
            }
            return false;
        }

        public bool IsVisible(IntPtr hwnd) { return AcadTextFrame.IsVisible(hwnd); }

        public int WidthOf(IntPtr hwnd) { return AcadTextFrame.WidthOf(hwnd); }

        public bool IsEditLike(IntPtr hwnd) { return MatchStrength(hwnd) > 0; }

        public int MatchStrength(IntPtr hwnd) { return MatchStrength(GetClassNameSafe(hwnd)); }

        /// <summary>
        /// 按类名打分（宿主中立词汇表）。
        ///
        /// ★ 现场标定入口：若中望 / 浩辰的命令栏控件类名不在下表，用 `XIMETREE`
        ///   导出窗口树看到真实类名后，按"是否真正承载键盘输入"补成 2 分（强特征）
        ///   或 1 分（弱特征）即可。加错了会重现 v36 那类事故：把绘图区当命令栏缓存。
        /// </summary>
        public int MatchStrength(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return 0;

            const StringComparison IC = StringComparison.OrdinalIgnoreCase;

            // ---- 强特征：真正承载文本输入的控件类 ----
            if (cls.IndexOf("richedit", IC) >= 0) return 2;      // RichEdit20W / RichEdit50W
            if (cls.IndexOf("edit", IC) >= 0) return 2;          // Edit / WindowsForms10.EDIT
            if (cls.IndexOf("hwndwrapper", IC) >= 0) return 2;   // WPF 宿主（HwndSource）
            if (cls.IndexOf("scintilla", IC) >= 0) return 2;     // Scintilla 编辑控件

            // ---- 弱特征：框架类（可能是绘图区，绝不能当命令栏缓存）----
            if (cls.IndexOf("afx", IC) >= 0) return 1;
            if (cls.IndexOf("cicerouiwndframe", IC) >= 0) return 1;

            return 0;
        }

        public bool IsTextInputHost(IntPtr hwnd) { return MatchStrength(hwnd) >= 2; }

        public bool IsTextInputHostClass(string cls) { return MatchStrength(cls) >= 2; }

        public string GetClassNameSafe(IntPtr hwnd) { return AcadTextFrame.GetClassNameSafe(hwnd); }

        public string GetWindowTextSafe(IntPtr hwnd) { return AcadTextFrame.GetWindowTextSafe(hwnd); }

        /// <summary>导出窗口树（诊断）。强度按**本宿主的**词汇表计算，故不能用 AutoCAD 版的 DumpTree。</summary>
        public string DumpTree(IntPtr root, int maxDepth)
        {
            var sb = new StringBuilder();
            try { DumpNode(sb, root, 0, maxDepth); }
            catch (Exception ex) { sb.AppendLine("  遍历异常: " + ex.Message); }
            return sb.ToString();
        }

        private void DumpNode(StringBuilder sb, IntPtr hwnd, int depth, int maxDepth)
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
                if (IsCommandLineWindow(hwnd)) sb.Append(" ⚠命令栏");
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
    }
}
