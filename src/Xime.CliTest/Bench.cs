using System;
using System.Diagnostics;
using System.Text;
using Xime.Core;
using Xime.Core.Native;

namespace Xime.CliTest
{
    /// <summary>
    /// 键盘钩子热路径微基准。
    ///
    /// 这些函数在**每一次按键**（WH_KEYBOARD_LL 全局钩子回调）里都会被调用，
    /// 一旦超过系统钩子超时（默认 300ms），按键会被丢弃甚至系统输入卡顿，
    /// 所以必须用实测数据说话，而不是凭感觉优化。
    ///
    /// 用法：XimeTest bench [轮数]
    /// </summary>
    internal static class Bench
    {
        public static int Run(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            int rounds = 200;
            if (args.Length > 1 && int.TryParse(args[1], out int r) && r > 0)
                rounds = r;

            // 预热，避开 JIT 与首次 P/Invoke 的固定开销
            Warmup();

            Console.WriteLine("===== 键盘钩子热路径基准（每项 " + rounds + " 轮 × 1000 次）=====");
            Console.WriteLine();

            var fg = NativeMethods.GetForegroundWindow();
            Console.WriteLine("前台窗口: 0x" + fg.ToInt64().ToString("X"));
            Console.WriteLine();

            // ---------- 1) 单独测量各项底层调用 ----------
            Measure("GetForegroundWindow()", rounds, () =>
            {
                var h = NativeMethods.GetForegroundWindow();
                GC.KeepAlive(h);
            });

            Measure("GetWindowThreadProcessId()", rounds, () =>
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(fg, out pid);
                GC.KeepAlive(pid);
            });

            Measure("Process.GetCurrentProcess().Id  ★已弃用（慢一个数量级）", rounds, () =>
            {
                var id = Process.GetCurrentProcess().Id;
                GC.KeepAlive(id);
            });

            Measure("NativeMethods.GetCurrentProcessId()  ★现用", rounds, () =>
            {
                var id = NativeMethods.GetCurrentProcessId();
                GC.KeepAlive(id);
            });

            Measure("IsCapsLockOn()", rounds, () =>
            {
                var b = NativeMethods.IsCapsLockOn();
                GC.KeepAlive(b);
            });

            Measure("IsCtrlPressed() + IsAltPressed() + IsShiftPressed()", rounds, () =>
            {
                var b = NativeMethods.IsCtrlPressed() | NativeMethods.IsAltPressed()
                        | NativeMethods.IsShiftPressed();
                GC.KeepAlive(b);
            });

            Measure("IsWindow()", rounds, () =>
            {
                var b = NativeMethods.IsWindow(fg);
                GC.KeepAlive(b);
            });

            // ---------- 2) 组合调用 ----------
            Measure("IsForegroundInAcad() 等价实现（含 Process.GetCurrentProcess）",
                rounds, () => { GC.KeepAlive(IsForegroundInAcadCurrent()); });

            Measure("IsForegroundInAcad() 优化版（缓存 PID）",
                rounds, () => { GC.KeepAlive(IsForegroundInAcadOptimized()); });

            Measure("CharInjector.GetRealFocusWindow()",
                rounds, () => { GC.KeepAlive(CharInjector.GetRealFocusWindow()); });

            Measure("AcadTextFrame.Resolve()（失败走负缓存）", rounds, () =>
            {
                var h = AcadTextFrame.Resolve();
                GC.KeepAlive(h);
            });

            Measure("FocusMonitor.IsEditBoxWindow(fg)", rounds, () =>
            {
                var b = FocusMonitor.IsEditBoxWindow(fg);
                GC.KeepAlive(b);
            });

            Measure("AcadTextFrame.IsCommandLineWindow(fg)  ★每键一次", rounds, () =>
            {
                var b = AcadTextFrame.IsCommandLineWindow(fg);
                GC.KeepAlive(b);
            });

            Measure("IsFocusInEditBox() 完整链  ★每键一次（编辑态下）", rounds, () =>
            {
                var b = IsFocusInEditBoxEquivalent();
                GC.KeepAlive(b);
            });

            Measure("GetEnglishCharForKey('A')", rounds, () =>
            {
                var c = CharInjector.GetEnglishCharForKey(0x41);
                GC.KeepAlive(c);
            });

            Measure("IsPrintableKey('A')", rounds, () =>
            {
                var b = CharInjector.IsPrintableKey(0x41);
                GC.KeepAlive(b);
            });

            // ---------- 3) 日志关掉时的字符串拼接开销 ----------
            Measure("Dbg 关闭时的字符串拼接（3 段 + ToString(\"X2\")）", rounds, () =>
            {
                int vk = 0x41;
                // 模拟 OnKeyCaptured / DoEnsureChinese 里的写法：
                // 实参在调用前就已拼好，无论日志开没开都要付出这份代价
                var s = "编辑键 VK=0x" + vk.ToString("X2") + " SendVkInput 结果=" + true;
                GC.KeepAlive(s);
            });

            Measure("Dbg 关闭时守卫写法 if(off) 直接跳过", rounds, () =>
            {
                int vk = 0x41;
                string s = null;
                if (LoggingEnabled)
                    s = "编辑键 VK=0x" + vk.ToString("X2") + " SendVkInput 结果=" + true;
                GC.KeepAlive(s);
            });

            Console.WriteLine();
            Console.WriteLine("单位 ns/op，越小越好。基准仅供参考 —— 绝对值受机器负载影响，");
            Console.WriteLine("重点看各项之间的**相对差距**。");
            return 0;
        }

        private static bool LoggingEnabled = false;

        private static void Warmup()
        {
            for (int i = 0; i < 2000; i++)
            {
                NativeMethods.GetForegroundWindow();
                CharInjector.GetRealFocusWindow();
                CharInjector.GetEnglishCharForKey(0x41);
                AcadTextFrame.IsCommandLineWindow(NativeMethods.GetForegroundWindow());
            }
        }

        private static void Measure(string name, int rounds, Action body)
        {
            // 先跑一轮丢弃，确保代码路径已 JIT
            for (int i = 0; i < 1000; i++) body();

            var sw = new Stopwatch();
            sw.Start();
            for (int r = 0; r < rounds; r++)
                for (int i = 0; i < 1000; i++)
                    body();
            sw.Stop();

            double perOp = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / (rounds * 1000.0);
            Console.WriteLine(string.Format("  {0,-62} {1,10:N0} ns", name, perOp));
        }

        // ---------- 被测量的等价实现 ----------

        private static bool IsForegroundInAcadCurrent()
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            uint fgPid;
            NativeMethods.GetWindowThreadProcessId(fg, out fgPid);
            if (fgPid == 0) return false;

            var proc = Process.GetCurrentProcess();
            return fgPid == (uint)proc.Id;
        }

        private static readonly uint MyPid = NativeMethods.GetCurrentProcessId();

        private static bool IsForegroundInAcadOptimized()
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            uint fgPid;
            NativeMethods.GetWindowThreadProcessId(fg, out fgPid);
            if (fgPid == 0) return false;

            return fgPid == MyPid;
        }

        private static bool IsFocusInEditBoxEquivalent()
        {
            var focus = CharInjector.GetRealFocusWindow();
            if (focus == IntPtr.Zero) return true;
            if (!FocusMonitor.IsEditBoxWindow(focus)) return false;
            if (AcadTextFrame.IsCommandLineWindow(focus)) return false;
            return true;
        }
    }
}
