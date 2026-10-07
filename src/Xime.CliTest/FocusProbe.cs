using System;
using System.Runtime.InteropServices;
using Xime.Core;
using Xime.Core.Native;

namespace Xime.CliTest
{
    /// <summary>
    /// 焦点窗口解析的正确性探针。
    ///
    /// 背景（v20 的真实缺陷）：
    ///   <c>GetWindowThreadProcessId</c> 的**返回值**是线程 ID，
    ///   而它的 <c>out</c> 参数是**进程 ID** —— 两者是完全不同的编号空间。
    ///   v20 的 <see cref="CharInjector.GetRealFocusWindow"/> 误把 out 参数
    ///   （PID）当作线程 ID 使用，导致：
    ///     · 「前台窗口与当前线程同线程」的快路径永远不成立；
    ///     · <c>GetGUIThreadInfo(PID)</c> 查不到对应线程，必然失败；
    ///   最终该函数**永远返回前台主窗口**，而不是真正持有键盘焦点的控件。
    ///
    ///   后果：AutoCAD 里所有「焦点是否在编辑框」的判断全部失效 ——
    ///   文字编辑态一按键就被退出，天正/原生文字命令打不出中文。
    ///
    /// 本探针把 PID / TID 两个值直接打印出来并分别用它们调用
    /// GetGUIThreadInfo，用实测结果证明这个区别。
    ///
    /// 用法：XimeTest focusdiag
    /// </summary>
    internal static class FocusProbe
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
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

        private static string Hex(IntPtr p)
        {
            return p == IntPtr.Zero ? "0（空）" : "0x" + p.ToInt64().ToString("X");
        }

        private static bool Query(uint id, out IntPtr focus)
        {
            focus = IntPtr.Zero;
            var gti = new GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            if (!GetGUIThreadInfo(id, ref gti)) return false;
            focus = gti.hwndFocus;
            return true;
        }

        public static int Run(string[] args)
        {
            Console.WriteLine("===== 焦点窗口解析探针 =====");
            Console.WriteLine();

            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                Console.WriteLine("当前没有前台窗口，无法测试。");
                return 1;
            }

            uint pid;
            uint tid = GetWindowThreadProcessId(fg, out pid);

            var myPid = NativeMethods.GetCurrentProcessId();
            var myTid = NativeMethods.GetCurrentThreadId();

            Console.WriteLine("前台窗口        : " + Hex(fg));
            Console.WriteLine();
            Console.WriteLine("GetWindowThreadProcessId 的返回值（真正的线程 ID）: " + tid);
            Console.WriteLine("GetWindowThreadProcessId 的 out 参数（进程 ID）  : " + pid);
            Console.WriteLine("                      两者是否相等            : " + (tid == pid ? "相等（巧合）" : "不同 ✔"));
            Console.WriteLine();
            Console.WriteLine("本进程 PID / 本线程 TID                        : " + myPid + " / " + myTid);
            Console.WriteLine();

            // ---- 用两种编号分别调用 GetGUIThreadInfo ----
            IntPtr byTid, byPid;
            bool okTid = Query(tid, out byTid);
            bool okPid = Query(pid, out byPid);

            Console.WriteLine("GetGUIThreadInfo(线程 ID = " + tid + ") : " +
                              (okTid ? "成功 → hwndFocus=" + Hex(byTid) : "★ 失败"));
            Console.WriteLine("GetGUIThreadInfo(进程 ID = " + pid + ") : " +
                              (okPid ? "成功 → hwndFocus=" + Hex(byPid) : "★ 失败"));
            Console.WriteLine();

            // ---- 真值：同线程时用 GetFocus ----
            IntPtr realFocus = IntPtr.Zero;
            if (tid == myTid)
            {
                realFocus = NativeMethods.GetFocus();
                Console.WriteLine("GetFocus()（与前台同线程，可信）        : " + Hex(realFocus));
            }

            // ---- 判定 ----
            int pass = 0, fail = 0;
            void Check(string what, bool ok)
            {
                Console.WriteLine("  [" + (ok ? "通过" : "★ 失败") + "] " + what);
                if (ok) pass++; else fail++;
            }

            Console.WriteLine("[判定]");
            Check("用【线程 ID】调用 GetGUIThreadInfo 应成功", okTid);
            Check("用【进程 ID】调用 GetGUIThreadInfo 应失败（证明两者不可混用）", !okPid);

            // CharInjector 的实际返回值是否就是真正的焦点控件
            var got = CharInjector.GetRealFocusWindow();
            Console.WriteLine();
            Console.WriteLine("CharInjector.GetRealFocusWindow()             : " + Hex(got));
            Console.WriteLine("CharInjector.LastFocusQueryId（实际传出的 ID）: " +
                              CharInjector.LastFocusQueryId +
                              (CharInjector.LastFocusQueryId == 0 ? "（走了同线程快路径，未调用 GetGUIThreadInfo）" : ""));
            Console.WriteLine();

            if (tid == myTid)
            {
                // 同线程：实现必须走 GetFocus 分支，结果应与 GetFocus() 一致
                Check("同线程时应返回 GetFocus() 的结果", realFocus == IntPtr.Zero || got == realFocus);
            }

            // 跨线程（或前台无焦点控件）时，实现必须与「按线程 ID 查询」的结果一致。
            // 这是最能区分对错的一条：若实现错把 PID 传进去（v20 的 bug），
            // 它只能退回返回前台窗口本身，而这里期望的是 hwndFocus。
            IntPtr expect = (okTid && byTid != IntPtr.Zero) ? byTid : fg;
            Check("GetRealFocusWindow() 与【按线程 ID 查询】的结果一致", got == expect);

            // ---- 诊断字段断言（唯一能从外部验证「传的到底是 TID 还是 PID」的手段）----
            // v20 的缺陷在外部完全不可观测：函数只是静默退回返回前台窗口，看起来"能用"。
            if (tid == myTid)
            {
                Check("同线程时应走 GetFocus 快路径，LastFocusQueryId 必须为 0",
                      CharInjector.LastFocusQueryId == 0);
            }
            else if (CharInjector.LastFocusQueryId != 0)
            {
                Check("LastFocusQueryId 必须等于【线程 ID】(" + tid + ")",
                      CharInjector.LastFocusQueryId == tid);
                Check("LastFocusQueryId 绝不能等于【进程 ID】(" + pid + ")",
                      CharInjector.LastFocusQueryId != pid);
            }

            Console.WriteLine();
            Console.WriteLine("合计: " + pass + " 项通过, " + fail + " 项失败");
            Console.WriteLine();
            Console.WriteLine("说明：");
            Console.WriteLine("  · 若「用进程 ID 调用 GetGUIThreadInfo」失败 —— 这是正常现象，也正是");
            Console.WriteLine("    v20 的 bug 所在：GetWindowThreadProcessId 的 out 参数是进程 ID，");
            Console.WriteLine("    返回值才是线程 ID，两者不可混用。v20 误把 PID 当线程 ID 传入，");
            Console.WriteLine("    导致 GetRealFocusWindow 永远拿不到真实焦点控件、只能退回前台主窗口，");
            Console.WriteLine("    于是「焦点是否在编辑框」永远为否 → 文字编辑态一按键就退出 → 打不出中文。");

            return fail == 0 ? 0 : 1;
        }
    }
}
