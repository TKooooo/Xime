using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xime.Core;
using Xime.Core.Native;
using Xime.Core.UI;

namespace Xime.CliTest
{
    /// <summary>
    /// 探针：在设置界面里按 Esc 是否真的会关闭窗口。
    ///
    /// 背景（用户需求）：
    ///   用户在 AutoCAD 里用 <c>xime</c> 打开设置界面（Application.ShowModalDialog），
    ///   期望按 Esc 直接退出 —— 这是 Windows 对话框的通用约定。
    ///
    /// 实现要点（本探针同时验证它）：
    ///   关闭逻辑写在 <see cref="Form.ProcessCmdKey"/> 里，而不是 KeyDown。
    ///   原因：ProcessCmdKey 属于「命令键预处理」，在**子控件拿到按键之前**被调用，
    ///   所以无论焦点在复选框、按钮还是「命令白名单」那个多行文本框里都能生效。
    ///   仅用 KeyDown 会被获得焦点的子控件先吃掉（RichTextBox 尤其明显），
    ///   表现为「按 Esc 毫无反应」。
    ///
    /// 本探针用 PostMessage 往窗体投递真正的 WM_KEYDOWN/WM_KEYUP(VK_ESCAPE)，
    /// 让它走完整的消息循环 → PreProcessMessage → ProcessCmdKey 链路，
    /// 因此验证的是真实行为，而不是直接反射调用那个方法。
    ///
    /// 用法：XimeTest esctest
    /// </summary>
    internal static class EscProbe
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>先把焦点放到「命令白名单」文本框里再按 Esc ——
        /// 这是最容易失败的场景（RichTextBox 会自己处理按键）。</summary>
        public static int Run(string[] args)
        {
            Console.WriteLine("===== Esc 关闭设置界面探针 =====");
            Console.WriteLine();

            int pass = 0, fail = 0;
            void Check(string what, bool ok)
            {
                Console.WriteLine("  [" + (ok ? "通过" : "★ 失败") + "] " + what);
                if (ok) pass++; else fail++;
            }

            XimeSettingsForm form = null;
            bool closedByEsc = false;
            bool visibleBeforeEsc = false;

            var ctx = new ApplicationContext();
            int step = 0;

            var timer = new Timer { Interval = 500 };
            timer.Tick += (s, e) =>
            {
                try
                {
                    if (step == 0)
                    {
                        step = 1;
                        visibleBeforeEsc = form.Visible;

                        // ★ 刻意把焦点放进命令白名单文本框 —— 模拟"用户正在编辑列表时按 Esc"
                        var box = FindRichTextBox(form);
                        if (box != null) box.Focus();

                        Console.WriteLine("  窗口句柄: 0x" + form.Handle.ToInt64().ToString("X")
                                          + "  显示中=" + form.Visible
                                          + "  焦点控件=" + (box != null ? box.GetType().Name : "(未找到)"));
                        Console.WriteLine("  → 投递 WM_KEYDOWN / WM_KEYUP (VK_ESCAPE)");

                        PostMessage(form.Handle, NativeMethods.WM_KEYDOWN,
                                    (IntPtr)NativeMethods.VK_ESCAPE, IntPtr.Zero);
                        PostMessage(form.Handle, NativeMethods.WM_KEYUP,
                                    (IntPtr)NativeMethods.VK_ESCAPE, IntPtr.Zero);
                    }
                    else
                    {
                        timer.Stop();
                        closedByEsc = !form.Visible;
                        Console.WriteLine("  Esc 之后：Visible=" + form.Visible);
                        ctx.ExitThread();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  探针异常：" + ex.Message);
                    timer.Stop();
                    ctx.ExitThread();
                }
            };

            try
            {
                // 版本号不写死：走 XimeSettingsForm 的默认值（FallbackVersion），
                // 否则预览图里的版本号会永远停在这一版
                form = new XimeSettingsForm(new XimeConfig(), null);
                form.Show();
                timer.Start();
                Application.Run(ctx);
            }
            catch (Exception ex)
            {
                Console.WriteLine("无法创建设置界面：" + ex.Message);
                return 2;
            }

            Console.WriteLine();
            Check("投递 Esc 之前窗口是显示状态的（前提成立）", visibleBeforeEsc);
            Check("在「命令白名单」文本框持有焦点时，按 Esc 应关闭窗口", closedByEsc);

            Console.WriteLine();
            Console.WriteLine("合计: " + pass + " 项通过, " + fail + " 项失败");
            return fail == 0 ? 0 : 1;
        }

        /// <summary>深度优先找窗体里的第一个 RichTextBox（即「命令白名单」列表）。</summary>
        private static RichTextBox FindRichTextBox(Control root)
        {
            if (root == null) return null;
            foreach (Control c in root.Controls)
            {
                var box = c as RichTextBox;
                if (box != null && !box.ReadOnly) return box;

                var sub = FindRichTextBox(c);
                if (sub != null) return sub;
            }
            return null;
        }
    }
}
