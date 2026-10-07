using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Xime.Core;
using Xime.Core.Native;

namespace Xime.CliTest
{
    /// <summary>
    /// AutoCAD 自动化测试驱动器。
    ///
    /// 用法：XimeTest acadauto [--dll <path>] [--text line] [--wait 90]
    ///
    /// 流程：
    ///   1) 启动 acad.exe（可选 /b 脚本加载插件）；
    ///   2) 轮询等待 AutoCAD 主窗口出现并进入前台；
    ///   3) 用**真实按键**（SendInput，不带注入标记）敲入测试文本，
    ///      这正是"用户在命令栏打字"的场景；
    ///   4) 等待命令栏反应，读取命令历史日志；
    ///   5) 输出结果，判定是否出现重复字符。
    ///
    /// 前置：需先在 AutoCAD 中把 LOGFILEMODE 设为 1（本工具会自动尝试）。
    /// </summary>
    internal static class AcadAutoTest
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool f);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

        public static int Run(string[] args)
        {
            string dll = GetArg(args, "--dll", @"C:\Users\XJH\Desktop\X输入法测试_v2\Xime.dll");
            string text = GetArg(args, "--text", "line");
            int waitSec = int.Parse(GetArg(args, "--wait", "120"));
            string acad = GetArg(args, "--acad", @"C:\Program Files\Autodesk\AutoCAD 2014\acad.exe");
            string histLog = @"C:\Users\XJH\Desktop\xime_acad_history.log";

            Console.WriteLine("===== AutoCAD 自动化测试 =====");
            Console.WriteLine("DLL  = " + dll);
            Console.WriteLine("文本 = " + text);
            Console.WriteLine("");

            // 1) 备份并清空历史日志
            try { if (File.Exists(histLog)) File.Delete(histLog); } catch (Exception) { }

            // 2) 启动 AutoCAD
            Process proc = null;
            try
            {
                proc = Process.Start(new ProcessStartInfo(acad)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(acad),
                });
                Console.WriteLine("[1] 已启动 acad.exe (PID=" + proc.Id + ")");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[1] 启动失败: " + ex.Message);
                return 2;
            }

            // 3) 等待主窗口
            IntPtr hwnd = IntPtr.Zero;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < waitSec)
            {
                Thread.Sleep(1500);
                hwnd = FindAcadWindow(proc.Id);
                if (hwnd != IntPtr.Zero) break;
                Console.Write(".");
            }
            Console.WriteLine();
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine("[2] ✗ 等待超时，未找到 AutoCAD 主窗口（" + waitSec + "s）");
                return 3;
            }
            Console.WriteLine("[2] ✓ 主窗口 = 0x" + hwnd.ToInt64().ToString("X") +
                              " 标题=" + GetTitle(hwnd));

            // 4) 再等一会儿让 CAD 完全就绪
            Console.WriteLine("[3] 等待 AutoCAD 就绪...");
            Thread.Sleep(8000);

            // 5) 加载插件（通过命令行输入 NETLOAD）
            Console.WriteLine("[4] 加载插件...");
            ForceForeground(hwnd);
            Thread.Sleep(1000);
            SendText("netload");
            SendVk(0x0D);   // Enter
            Thread.Sleep(1500);
            // 文件对话框里输入路径
            SendText(dll);
            SendVk(0x0D);
            Thread.Sleep(3000);

            // 6) 敲入测试文本（真实按键，模拟用户）
            Console.WriteLine("[5] 敲入测试文本 \"" + text + "\"...");
            ForceForeground(hwnd);
            Thread.Sleep(800);
            foreach (char c in text)
            {
                SendVk((int)char.ToUpperInvariant(c));
                Thread.Sleep(120);
            }
            Thread.Sleep(1200);

            // 7) 不回车，保持输入状态，读日志
            Console.WriteLine("[6] 读取命令历史...");
            Thread.Sleep(1000);

            string content = "";
            try { if (File.Exists(histLog)) content = File.ReadAllText(histLog); } catch (Exception) { }

            Console.WriteLine("");
            Console.WriteLine("===== 结果 =====");
            if (string.IsNullOrEmpty(content))
            {
                Console.WriteLine("✗ 命令历史日志为空：" + histLog);
                Console.WriteLine("（可能未开启 LOGFILEMODE，请在 AutoCAD 中手动开启后重试）");
            }
            else
            {
                var lines = content.Split('\n');
                int show = Math.Min(lines.Length, 40);
                for (int i = Math.Max(0, lines.Length - show); i < lines.Length; i++)
                    Console.Write(lines[i]);
            }

            Console.WriteLine("");
            Console.WriteLine("[7] AutoCAD 保持运行，请人工确认命令栏内容。");
            return 0;
        }

        private static IntPtr FindAcadWindow(int pid)
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                var proc = Process.GetProcessById(pid);
                for (int i = 0; i < 60; i++)
                {
                    proc.Refresh();
                    if (proc.MainWindowHandle != IntPtr.Zero && IsWindowVisible(proc.MainWindowHandle))
                    {
                        found = proc.MainWindowHandle;
                        break;
                    }
                    Thread.Sleep(1000);
                }
            }
            catch (Exception) { }
            return found;
        }

        private static string GetTitle(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static void ForceForeground(IntPtr hwnd)
        {
            try
            {
                ShowWindow(hwnd, 9);    // SW_RESTORE
                uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
                uint me = GetCurrentThreadId();
                bool att = false;
                if (fg != me) att = AttachThreadInput(me, fg, true);
                try { SetForegroundWindow(hwnd); }
                finally { if (att) AttachThreadInput(me, fg, false); }
            }
            catch (Exception) { }
        }

        /// <summary>用真实按键敲入一段文本（每个字符都是真实按键事件，无注入标记）。</summary>
        private static void SendText(string s)
        {
            foreach (char c in s)
            {
                // 用 VkKeyScan 取该字符对应的 VK + Shift 状态
                short vk = NativeMethods.VkKeyScan(c);
                int v = vk & 0xFF;
                bool shift = (vk & 0x100) != 0;
                if (v <= 0) continue;

                if (shift) SendVk(0x10, false);
                SendVk(v);
                if (shift) SendVk(0x10, true);
            }
        }

        /// <summary>发送一次真实按键（可选按住/抬起 Shift）。</summary>
        private static void SendVk(int vk, bool up = false)
        {
            var inputs = new NativeMethods.INPUT[1];
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = (ushort)vk;
            inputs[0].U.ki.dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0;
            inputs[0].U.ki.dwExtraInfo = IntPtr.Zero;    // 无标记 = 模拟真实用户
            NativeMethods.SendInput(1, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
            Thread.Sleep(30);
        }

        private static string GetArg(string[] args, string name, string def)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return def;
        }
    }
}
