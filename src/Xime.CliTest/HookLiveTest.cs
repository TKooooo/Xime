using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Xime.Core;
using Xime.Core.Native;

using ImeMode = Xime.Core.ImeMode;

namespace Xime.CliTest
{
    /// <summary>
    /// 键盘钩子「活体」测试。
    ///
    /// 用途：在真实交互下验证 WH_KEYBOARD_LL 钩子能否拦截字母键并注入字符，
    /// 直接定位「钩子没起作用」的根因。
    ///
    /// 界面：一个带文本框的窗体。
    ///   - 勾选「启用拦截」后，在文本框里敲字母；
    ///   - 若钩子生效，日志会显示每个被拦截的按键，且字符被注入到文本框；
    ///   - 若钩子未生效，按键走原生路径，日志无任何「captured」记录。
    ///
    /// 用法：XimeTest hooklive [--auto seconds]
    ///   --auto N  自动运行 N 秒后退出（用于无人值守验证）
    /// </summary>
    internal static class HookLiveTest
    {
        public static void Run(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            int autoSeconds = 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--auto")
                {
                    int.TryParse(args[i + 1], out autoSeconds);
                }
            }

            var form = new Form
            {
                Text = "X输入法 键盘钩子活体测试",
                ClientSize = new Size(640, 460),
                StartPosition = FormStartPosition.CenterScreen,
                Font = new Font("微软雅黑", 9F)
            };

            var chk = new CheckBox
            {
                Text = "启用拦截（模拟「命令栏需要英文」状态）",
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(8, 6, 0, 0)
            };

            var txt = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 30,
                Font = new Font("Consolas", 11F),
                Multiline = false
            };

            var log = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                BackColor = Color.White
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(8, 4, 8, 4) };
            var lblState = new Label
            {
                Dock = DockStyle.Fill,
                Text = "钩子：未安装",
                TextAlign = ContentAlignment.MiddleLeft
            };
            bottom.Controls.Add(lblState);

            // 顺序：Fill 先加，再 Top（自上而下：chk 在上，txt 在下）
            form.Controls.Add(log);
            form.Controls.Add(txt);
            form.Controls.Add(chk);
            form.Controls.Add(bottom);

            var hook = new KeyboardHook();
            int captureCount = 0;

            // 日志同时写入控制台与文件，便于无人值守时收集结果
            string logFile = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "xime_hooklive.log");
            try { System.IO.File.WriteAllText(logFile, ""); } catch (Exception) { }

            Action<string> writeLog = (msg) =>
            {
                var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
                try { System.IO.File.AppendAllText(logFile, line + "\r\n"); } catch (Exception) { }
                Console.WriteLine(line);
                try
                {
                    log.AppendText(line + "\r\n");
                    log.SelectionStart = log.TextLength;
                    log.ScrollToCaret();
                }
                catch (Exception) { }
            };

            // 拦截决策：与 XimeEngine.ShouldCaptureKey 同逻辑，但简化以便观察
            int seenCount = 0;
            hook.ShouldCapture = (vk, down) =>
            {
                seenCount++;
                if (!chk.Checked) return false;
                if (!down) return false;
                if (NativeMethods.IsCtrlPressed() || NativeMethods.IsAltPressed()) return false;
                if (!CharInjector.IsPrintableKey(vk)) return false;
                return true;
            };

            hook.OnCaptured = (vk, down) =>
            {
                captureCount++;
                char ch = CharInjector.GetCharForKey(vk);
                writeLog(string.Format("CAPTURED vk=0x{0:X2} ({1}) -> '{2}'",
                    vk, ((Keys)vk), ch == '\0' ? "?" : ch.ToString()));

                if (ch == '\0') return false;

                // 延迟到钩子回调之外再注入（避免在 LL 钩子内调用 SendInput）
                try
                {
                    form.BeginInvoke(new Action(() =>
                    {
                        var focusHwnd = CharInjector.GetRealFocusWindow();
                        // 与正式引擎一致：主用 PostMessage 到真实焦点，失败再 SendInput
                        bool ok = CharInjector.SendCharToFocus(ch);
                        string method = "PostMessage";
                        if (!ok)
                        {
                            ok = CharInjector.SendUnicodeChar(ch);
                            method = "SendInput(Unicode)";
                        }
                        writeLog(string.Format("  注入 '{0}' 焦点=0x{1} 方式={2} 结果={3}",
                            ch, focusHwnd.ToInt64().ToString("X"), method,
                            ok ? "成功" : "失败"));
                    }));
                }
                catch (Exception) { }
                return true;
            };

            chk.CheckedChanged += (s, e) =>
            {
                if (chk.Checked)
                {
                    bool ok = hook.Install(form);
                    lblState.Text = "钩子：" + (ok ? "已安装（拦截中）" : "安装失败！") +
                        "  |  线程ID=" + NativeMethods.GetCurrentThreadId();
                    writeLog("尝试安装钩子 → " + (ok ? "成功" : "失败"));
                    if (!ok)
                    {
                        writeLog("GetLastError 说明：控制台/无消息循环线程会导致失败。");
                    }
                }
                else
                {
                    hook.Uninstall();
                    lblState.Text = "钩子：未安装";
                    writeLog("钩子已卸载。累计拦截 " + captureCount + " 次。");
                }
            };

            form.FormClosing += (s, e) =>
            {
                hook.Uninstall();
                hook.Dispose();
            };

            // 启动即安装一次（默认勾选）
            form.Shown += (s, e) =>
            {
                // 确保文本框获得焦点，方便直接打字
                txt.Focus();
                writeLog("请在文本框内敲字母键。若钩子生效，应出现 CAPTURED 日志。");

                // 窗体句柄已就绪，此时安装钩子最可靠（会触发 CheckedChanged）
                chk.Checked = true;
            };

            if (autoSeconds > 0)
            {
                // 自动模式：启动后等待钩子就绪，然后程序化模拟按键，
                // 通过日志中的 CAPTURED 记录判断钩子是否真的拦截到了按键。
                var t = new Timer { Interval = Math.Max(500, autoSeconds * 1000) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    // 汇报文本框最终内容，验证注入是否真的落到了焦点控件
                    writeLog("文本框最终内容 = \"" + txt.Text + "\"");
                    writeLog("自动退出。钩子共看到按键 " + seenCount + " 次，拦截 " + captureCount + " 次。");
                    form.Close();
                };
                t.Start();

                // 1 秒后开始自动发键
                var t2 = new Timer { Interval = 1200 };
                t2.Tick += (s, e) =>
                {
                    t2.Stop();
                    writeLog("开始自动模拟按键 A/B/C ...");
                    AutoSendKeys(writeLog);
                };
                t2.Start();
            }

            Application.Run(form);
        }

        /// <summary>用 SendInput 模拟按键（会走完整的低级键盘钩子链路）。</summary>
        private static void AutoSendKeys(Action<string> log)
        {
            try
            {
                var keys = new[] { 0x41, 0x42, 0x43 };   // A B C
                foreach (var vk in keys)
                {
                    SendKeyDownUp(vk);
                    System.Threading.Thread.Sleep(120);
                }
                log("自动模拟按键结束。");
            }
            catch (Exception ex)
            {
                log("自动模拟按键异常：" + ex.Message);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs,
            NativeMethods.INPUT[] pInputs, int cbSize);

        private static void SendKeyDownUp(int vk)
        {
            var inputs = new NativeMethods.INPUT[2];
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = (ushort)vk;
            inputs[0].U.ki.wScan = 0;
            inputs[0].U.ki.dwFlags = 0;
            inputs[0].U.ki.dwExtraInfo = IntPtr.Zero;

            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].U.ki.wVk = (ushort)vk;
            inputs[1].U.ki.wScan = 0;
            inputs[1].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
            inputs[1].U.ki.dwExtraInfo = IntPtr.Zero;

            int size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.INPUT));
            SendInput(2, inputs, size);
        }
    }
}
