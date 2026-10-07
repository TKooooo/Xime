using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using Xime.Core;
using Xime.Core.Native;

namespace Xime.CliTest
{
    /// <summary>
    /// 探针：验证「为什么命令执行期间必须重放**真实按键**，而不能继续注入 Unicode」。
    ///
    /// ★ 这是天正「任意布置」（RYBZ）二级命令失灵事故的**可执行反证**：
    ///
    ///   本探针开一个普通窗口接收键盘输入，然后分别用两种方式"注入 A 键"，
    ///   把窗口实际收到的消息打出来：
    ///
    ///   ① <see cref="CharInjector.SendUnicodeInput"/>（空闲提示符用的通道）
    ///        → 窗口收到 WM_KEYDOWN，但 wParam 是 **VK_PACKET(0xE7)**，
    ///          不是 0x41 —— 因为 UNICODE 注入只带"字符"，不带虚拟键码。
    ///          WM_CHAR 是 'a'，所以**读命令行文本**的消费者完全正常
    ///          （这就是命令名输入一直好用的原因）。
    ///
    ///   ② <see cref="CharInjector.ReplayVk"/>（v26 起命令执行期间的通道）
    ///        → 窗口收到 WM_KEYDOWN，wParam = **0x41**，且 lParam 里带
    ///          **真实扫描码** —— 与物理按键在键码层面完全等价。
    ///          这正是 acedGrRead 一类"读原始按键"的第三方命令需要的东西。
    ///
    ///   ③ <see cref="CharInjector.PostVkKey"/>（唯一"输入法绝对看不到"的通道）
    ///        → PostMessage 直接投给窗口，不进系统输入队列。
    ///          这里验证它投出的 lParam 也**带真实扫描码**、且 WM_KEYUP 的
    ///          bit30/bit31（前一次键状态 / 转换状态）都置位 ——
    ///          早期实现两个消息都传死值 0x10001（= 重复计数 1 + **扫描码 1**，
    ///          而扫描码 1 是 Esc 的），对"读原始按键"的消费者等于给了错信息。
    ///
    ///   前两种方式都会产生 WM_CHAR，所以文本输入都正常 —— 差别只在"按键"层面。
    ///
    /// 用法：XimeTest keyprobe
    /// </summary>
    internal static class KeyProbe
    {
        private const int WM_CHAR = 0x0102;

        /// <summary>VK_PACKET：UNICODE 注入事件的虚拟键码（Win32 规定值）。</summary>
        private const int VK_PACKET = 0xE7;

        private sealed class ProbeForm : Form
        {
            public readonly List<int> KeyDownVks = new List<int>();
            public readonly List<int> KeyDownScans = new List<int>();
            public readonly List<char> Chars = new List<char>();

            /// <summary>最近一次 WM_KEYUP 的 lParam（用于检查位域是否规范）。</summary>
            public long LastKeyUpLParam;
            public bool SawKeyUp;

            public ProbeForm()
            {
                // 无边框小窗，尽量不打扰（测试是短暂弹出、立即关闭）
                FormBorderStyle = FormBorderStyle.FixedToolWindow;
                StartPosition = FormStartPosition.Manual;
                Location = new System.Drawing.Point(-2000, -2000);
                ShowInTaskbar = false;
                Size = new System.Drawing.Size(260, 80);
                Text = "XimeTest 按键探针";
            }

            public void Reset()
            {
                KeyDownVks.Clear();
                KeyDownScans.Clear();
                Chars.Clear();
                LastKeyUpLParam = 0;
                SawKeyUp = false;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == NativeMethods.WM_KEYDOWN)
                {
                    KeyDownVks.Add((int)m.WParam);
                    KeyDownScans.Add((int)((long)m.LParam >> 16) & 0xFF);
                }
                else if (m.Msg == NativeMethods.WM_KEYUP)
                {
                    SawKeyUp = true;
                    LastKeyUpLParam = (long)m.LParam;
                }
                else if (m.Msg == WM_CHAR)
                {
                    Chars.Add((char)(int)m.WParam);
                }

                base.WndProc(ref m);
            }

            /// <summary>把已入队的消息处理掉（SendInput 是异步派发的）。</summary>
            public void Pump(int ms)
            {
                var end = Environment.TickCount + ms;
                while (Environment.TickCount < end)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(10);
                }
                Application.DoEvents();
            }

            /// <summary>
            /// 强行把本窗口变成前台并拿到键盘焦点。
            ///
            /// 为什么必须这么做：SendInput 只投递给**前台窗口的焦点控件**。
            /// 而 SetForegroundWindow 只允许"当前前台进程"调用成功，
            /// 所以用经典做法：AttachThreadInput 到当前前台线程 → 设置前台 →
            /// 解除挂接。（正是 CharInjector 里刻意避开的那套高开销方案，
            /// 但这里是一次性测试，不在乎那 200µs。）
            /// </summary>
            public bool ForceForeground(int timeoutMs)
            {
                var end = Environment.TickCount + timeoutMs;
                while (Environment.TickCount < end)
                {
                    if (NativeMethods.GetForegroundWindow() == Handle) return true;

                    uint fgPid;
                    uint fgThread = NativeMethods.GetWindowThreadProcessId(
                        NativeMethods.GetForegroundWindow(), out fgPid);
                    uint myThread = NativeMethods.GetCurrentThreadId();

                    bool attached = false;
                    try
                    {
                        if (fgThread != 0 && fgThread != myThread)
                            attached = NativeMethods.AttachThreadInput(myThread, fgThread, true);

                        NativeMethods.SetForegroundWindow(Handle);
                        NativeMethods.SetFocus(Handle);
                        Activate();
                        BringToFront();
                    }
                    finally
                    {
                        if (attached)
                            NativeMethods.AttachThreadInput(myThread, fgThread, false);
                    }

                    Pump(120);
                }
                return NativeMethods.GetForegroundWindow() == Handle;
            }
        }

        public static int Run(string[] args)
        {
            Console.WriteLine("===== 按键注入通道探针（真实窗口接收）=====");
            Console.WriteLine();

            int pass = 0, fail = 0;
            void Check(string what, bool ok)
            {
                Console.WriteLine("  [" + (ok ? "通过" : "★ 失败") + "] " + what);
                if (ok) pass++; else fail++;
            }

            var form = new ProbeForm();
            var ctx = new ApplicationContext();
            bool done = false;
            bool skipped = false;

            form.Shown += (s, e) =>
            {
                try
                {
                    // 确保窗口真的拿到键盘焦点 —— SendInput 只会投给焦点窗口。
                    bool isFg = form.ForceForeground(2000);

                    Console.WriteLine("  探针窗口句柄: 0x" + form.Handle.ToInt64().ToString("X")
                                      + "  前台=" + isFg);
                    if (!isFg)
                    {
                        Console.WriteLine();
                        Console.WriteLine("  ⚠ 当前环境不允许本进程把窗口切到前台（SetForegroundWindow 被拒），");
                        Console.WriteLine("    无法验证 SendInput 的真实投递 —— 跳过本项（不算失败）。");
                        Console.WriteLine("    请在交互式桌面会话里重跑：XimeTest keyprobe");
                        skipped = true;
                        return;
                    }
                    Console.WriteLine();

                    // ---------- ① UNICODE 通道 ----------
                    form.Reset();
                    CharInjector.SendUnicodeInput('a');
                    form.Pump(250);

                    Console.WriteLine("  ① SendUnicodeInput('a') 窗口实际收到：");
                    Dump(form);

                    // ★★★ 判据口径（重要，别再改回去）：
                    //   本探针窗口在 Pump 期间是**真实前台**，别的进程/用户事件
                    //   （Esc、空格、前台被抢）也可能进到队列里。所以在**整批**里找
                    //   我们要的那个按键/字符，而不是只看 [0]。
                    //   历史上这里是 `[0] == 0x41` / `[0] == VK_PACKET` ——
                    //   实测约 10% 的运行会因为前面多出一个 Esc(0x1B) 而变红，
                    //   产生"假失败"。我们要证明的是"这个按键确实被送进来了"，
                    //   不是"它是第一个"。
                    //   ★ 每条通道的判据必须在**它自己的 Pump 之后立刻**取，
                    //     因为下一条通道开头会 Reset()。
                    bool uniVkPacket = form.KeyDownVks.Contains(VK_PACKET);
                    bool uniChar = form.Chars.Contains('a');

                    Console.WriteLine();
                    // ---------- ② 真实按键重放通道 ----------
                    // ★ SendInput 只投递给**前台窗口**。①③ 的 DoEvents/等待期间，
                    //   机器上别的进程可能抢走前台（实测约 10% 的概率会这样 →
                    //   ② 的按键落到别处、这里读到空/别的键）。所以每条通道投递前
                    //   **重新确保本窗口是前台**，把"探测"与"环境抖动"分开。
                    form.ForceForeground(2000);
                    form.Reset();
                    CharInjector.ReplayVk(0x41);          // VK_A
                    form.Pump(250);

                    Console.WriteLine("  ② ReplayVk(VK_A=0x41) 窗口实际收到：");
                    Dump(form);

                    bool replayVkA = form.KeyDownVks.Contains(0x41);
                    // 扫描码要取"那一个 0x41 按键"的扫描码，不能取整批里的第一个
                    int replayIdx = form.KeyDownVks.IndexOf(0x41);
                    bool replayScan = replayIdx >= 0
                                      && replayIdx < form.KeyDownScans.Count
                                      && form.KeyDownScans[replayIdx] != 0;
                    int replayScanValue = replayScan ? form.KeyDownScans[replayIdx] : -1;
                    // ★ 大小写不敏感：重放的是**真实 VK_A 按键**，系统按
                    //   "Shift ^ CapsLock" 决定它变成 'a' 还是 'A'。本探针要证明的是
                    //   "字符确实到达了命令栏"，**不是**某个大小写 ——
                    //   把断言写成只认 'a' 会让整条回归随 CapsLock 开关变色。
                    bool replayChar = form.Chars.Contains('a') || form.Chars.Contains('A');

                    Console.WriteLine();
                    // ---------- ③ PostKey 通道（唯一"输入法绝对看不到"的方式） ----------
                    form.ForceForeground(2000);
                    form.Reset();
                    CharInjector.PostVkKey(form.Handle, 0x41);   // VK_A
                    form.Pump(250);

                    Console.WriteLine("  ③ PostVkKey(VK_A=0x41) 窗口实际收到：");
                    Dump(form);

                    int postIdx = form.KeyDownVks.IndexOf(0x41);
                    bool postVkA = postIdx >= 0;
                    int postScanValue = (postIdx >= 0 && postIdx < form.KeyDownScans.Count)
                                        ? form.KeyDownScans[postIdx] : -1;
                    bool postScanReal = postScanValue > 0 && postScanValue == replayScanValue;
                    bool keyUpFlags = form.SawKeyUp
                                      && ((form.LastKeyUpLParam & 0xC0000000L) == 0xC0000000L);

                    Console.WriteLine();
                    Console.WriteLine("  结论：");
                    Console.WriteLine("    · UNICODE 注入送出去的是「字符」，虚拟键码被系统填成 VK_PACKET(0xE7)");
                    Console.WriteLine("      —— 读命令行**文本**的消费者没问题，但读**原始按键**的消费者");
                    Console.WriteLine("         （天正 RYBZ 的 A/E/D/F/X-Y 二级提示，acedGrRead 一类）认不出来。");
                    Console.WriteLine("    · ReplayVk 送出去的是「真按键」：VK=0x41 + 真实扫描码");
                    Console.WriteLine("      —— 第三方命令认得；但事件仍进系统输入队列，输入法可能仍看到。");
                    Console.WriteLine("    · PostVkKey 不进系统输入队列（输入法绝对看不到），且现在也带上了");
                    Console.WriteLine("      真实扫描码 —— 早期版本传死值 0x10001（扫描码被写成 1）。");
                    Console.WriteLine();

                    Check("① UNICODE 通道：虚拟键码为 VK_PACKET(0xE7)（所以原始按键读取者认不出来）",
                          uniVkPacket);
                    Check("① UNICODE 通道：字符 'a' 仍然到达（命令名输入正常的原因）", uniChar);
                    Check("② ReplayVk 通道：虚拟键码 = 0x41（真按键，与物理按键等价）", replayVkA);
                    Check("② ReplayVk 通道：lParam 带真实扫描码（非 0）", replayScan);
                    Check("② ReplayVk 通道：字符到达（大小写随 CapsLock/Shift 决定，不作要求）", replayChar);
                    Check("③ PostVkKey 通道：虚拟键码 = 0x41（消息方式，不进输入队列）", postVkA);
                    Check("③ PostVkKey 通道：扫描码 = 0x" + postScanValue.ToString("X2")
                          + "，与 ReplayVk 的真实扫描码一致", postScanReal);
                    Check("③ PostVkKey 通道：WM_KEYUP 的 bit30/bit31 已置位（位域规范）", keyUpFlags);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  探针异常：" + ex.Message);
                }
                finally
                {
                    done = true;
                    ctx.ExitThread();
                }
            };

            try
            {
                form.Show();
                Application.Run(ctx);
            }
            catch (Exception ex)
            {
                Console.WriteLine("无法创建探针窗口：" + ex.Message);
                return 2;
            }

            if (!done) Console.WriteLine("  探针未执行完成。");
            if (skipped)
            {
                Console.WriteLine();
                Console.WriteLine("合计: 跳过（环境不允许切换前台窗口，未做断言）");
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine("合计: " + pass + " 项通过, " + fail + " 项失败");
            return fail == 0 ? 0 : 1;
        }

        private static void Dump(ProbeForm form)
        {
            var sb = new StringBuilder();
            if (form.KeyDownVks.Count == 0) sb.Append("    WM_KEYDOWN: 无\r\n");
            for (int i = 0; i < form.KeyDownVks.Count; i++)
            {
                sb.Append("    WM_KEYDOWN  wParam(VK)=0x")
                  .Append(form.KeyDownVks[i].ToString("X2"))
                  .Append("  扫描码=0x")
                  .Append(form.KeyDownScans[i].ToString("X2"))
                  .Append("\r\n");
            }
            if (form.Chars.Count == 0) sb.Append("    WM_CHAR: 无\r\n");
            foreach (var c in form.Chars)
            {
                sb.Append("    WM_CHAR     字符='").Append(c).Append("' (0x")
                  .Append(((int)c).ToString("X2")).Append(")\r\n");
            }
            Console.Write(sb.ToString());
        }
    }
}
