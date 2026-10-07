using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Xime.Core;
using Xime.Core.Native;

namespace Xime.CliTest
{
    /// <summary>
    /// WPF 投递通道诊断（模拟 AutoCAD 2013+ 的 WPF 命令栏）。
    ///
    /// 目的：在**可控**环境里搞清「投递到 WPF 控件」到底哪条通道有效、会不会叠加。
    ///
    /// 用法：XimeTest wpfprobe [--mode post|unicode|both|native] [--text lin]
    ///
    /// 实验设计：
    ///   打开一个 WPF 窗口 + TextBox，把焦点固定到 TextBox，
    ///   然后**直接**调用投递函数（不经过钩子）：
    ///     · native  —— 直接 SendInput 真实按键（模拟"用户手敲"，作为基准）
    ///     · post    —— 只 PostMessage(WM_KEYDOWN/WM_KEYUP) 到窗口
    ///     · unicode —— 只 SendInput(KEYEVENTF_UNICODE)
    ///     · both    —— 两者都发
    ///   最后打印 TextBox 内容，对比即可知道每条通道的实际效果。
    /// </summary>
    internal static class WpfProbe
    {
        public static int Run(string[] args)
        {
            string mode = GetArg(args, "--mode", "both");
            string text = GetArg(args, "--text", "lin");
            string outFile = GetArg(args, "--out", null);

            var log = new StringBuilder();
            void L(string s) { log.AppendLine(s); Console.WriteLine(s); }

            L("===== X输入法 WPF 投递通道诊断 =====");
            L("模式=" + mode + "  目标文本=\"" + text + "\"");
            L("");

            string result = null;
            string diag = null;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var tb = new TextBox { FontSize = 20 };
                    var win = new System.Windows.Window
                    {
                        Title = "XimeProbe",
                        Width = 400,
                        Height = 120,
                        Topmost = true,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        Content = tb,
                    };

                    win.Loaded += (s, e) =>
                    {
                        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();

                            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                            ForceForeground(hwnd);
                            tb.Focus();
                            Keyboard.Focus(tb);

                            var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                            t1.Tick += (s3, e3) =>
                            {
                                t1.Stop();

                                diag = "窗口句柄=0x" + hwnd.ToInt64().ToString("X") +
                                       "  前台=0x" + NativeMethods.GetForegroundWindow().ToInt64().ToString("X") +
                                       "  焦点=" + (Keyboard.FocusedElement != null
                                            ? Keyboard.FocusedElement.GetType().Name : "null");

                                var target = CharInjector.GetRealFocusWindow();

                                foreach (char c in text.ToUpperInvariant())
                                {
                                    int vk = c;
                                    switch (mode)
                                    {
                                        case "native":
                                            CharInjector.SendKeyForProbe(vk);
                                            break;
                                        case "post":
                                            CharInjector.PostVkKey(hwnd, vk);
                                            break;
                                        case "unicode":
                                            {
                                                char ch = CharInjector.GetCharForKey(vk);
                                                if (ch != '\0') CharInjector.SendUnicodeInput(ch);
                                                break;
                                            }
                                        case "both":
                                            CharInjector.PostVkKey(hwnd, vk);
                                            {
                                                char ch = CharInjector.GetCharForKey(vk);
                                                if (ch != '\0') CharInjector.SendUnicodeInput(ch);
                                            }
                                            break;
                                    }
                                    System.Threading.Thread.Sleep(30);
                                }

                                var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                                t2.Tick += (s4, e4) =>
                                {
                                    t2.Stop();
                                    result = tb.Text;
                                    app.Shutdown();
                                };
                                t2.Start();
                            };
                            t1.Start();
                        };
                        t.Start();
                    };

                    app.Run(win);
                }
                catch (Exception ex) { L("异常: " + ex); }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join(15000);

            L(diag ?? "(无诊断)");
            L("TextBox 内容 = \"" + (result ?? "") + "\"");
            L("长度 = " + (result != null ? result.Length.ToString() : "-") + "  （期望 " + text.Length + "）");
            if (result != null)
                L(result.ToLowerInvariant() == text.ToLowerInvariant()
                    ? "★ 一致（该通道单独可用）"
                    : "★ 不一致 → 内容=" + result);

            if (!string.IsNullOrEmpty(outFile))
            {
                try { System.IO.File.WriteAllText(outFile, log.ToString()); } catch (Exception) { }
            }
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private static void ForceForeground(IntPtr hwnd)
        {
            try
            {
                ShowWindow(hwnd, 5);
                uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
                uint me = GetCurrentThreadId();
                bool attached = false;
                if (fg != me) attached = AttachThreadInput(me, fg, true);
                try { SetForegroundWindow(hwnd); }
                finally { if (attached) AttachThreadInput(me, fg, false); }
            }
            catch (Exception) { }
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
