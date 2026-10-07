using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xime.Core;
using Xime.Core.UI;

namespace Xime.CliTest
{
    /// <summary>
    /// 标题栏 / 任务栏图标的**端到端**探针。
    ///
    /// 背景：用户实测反馈「成品左上角没显示 logo」。这个问题光看代码无法定位，
    ///   因为托管 <c>Form.Icon</c> 赋值成功 ≠ 窗口真的挂上了可用图标：
    ///     · 宿主创建句柄时可能覆盖；
    ///     · 图标对象被 GC 回收后，其 HICON 销毁，而窗口记下的还是那个失效句柄 ——
    ///       <c>WM_GETICON</c> 照样返回非零（看着"有图标"），但实际什么都画不出来。
    ///
    /// ★ 判定方式（关键设计）：
    ///   不去比对句柄。WinForms 的 Icon 属性 setter、Form 生命周期回调、
    ///   Windows 对小图标的内部处理都会参与 WM_SETICON 的投递，
    ///   窗口最终记下的句柄未必等于最后一次 SendMessage 传入的值，
    ///   比对句柄会得出误导性结论。
    ///   真正判断标准是：**把系统报告的图标取出来渲染成位图，看它有没有内容、是不是我们的 logo**。
    ///
    /// 用法：XimeTest iconcheck [--keep]
    /// </summary>
    internal static class IconProbe
    {
        private const int WM_GETICON = 0x007F;
        private const int ICON_SMALL = 0;    // 标题栏
        private const int ICON_BIG = 1;      // 任务栏 / Alt+Tab
        private const int ICON_SMALL2 = 2;   // 应用程序提供的小图标

        private const int GCLP_HICON = -14;
        private const int GCLP_HICONSM = -34;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
        private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetClassLongW", SetLastError = true)]
        private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

        private static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8) return GetClassLongPtr64(hWnd, nIndex);
            return (IntPtr)(long)GetClassLong32(hWnd, nIndex);
        }

        private static string Hex(IntPtr p)
        {
            return p == IntPtr.Zero ? "0（空）" : "0x" + p.ToInt64().ToString("X");
        }

        public static int Run(string[] args)
        {
            bool keep = Array.IndexOf(args, "--keep") >= 0;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Console.WriteLine("===== 窗体图标探针（端到端） =====");
            Console.WriteLine();

            var config = new XimeConfig();
            // 版本号不写死：走 XimeSettingsForm 的默认值（FallbackVersion）
            var form = new XimeSettingsForm(config, null);

            // 离屏显示：必须真正 Show 才会创建句柄、才会走 OnHandleCreated，
            // 但移到屏幕外，避免打断用户。
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Show();
            Application.DoEvents();
            form.Refresh();
            Application.DoEvents();

            var hwnd = form.Handle;

            Console.WriteLine("[0] 窗体");
            Console.WriteLine("    窗口句柄   : " + Hex(hwnd));
            Console.WriteLine("    ShowIcon   : " + form.ShowIcon);
            Console.WriteLine();

            // ---- 1) 资源层 ----
            var appIcon = XimeResources.AppIcon;
            Icon sized16, sized32;
            bool sizedOk = XimeResources.TryGetSizedIcons(out sized16, out sized32);

            Console.WriteLine("[1] 资源层");
            Console.WriteLine("    AppIcon          : " +
                (appIcon == null ? "★ 未加载" : appIcon.Width + "x" + appIcon.Height +
                                            "  句柄=" + Hex(appIcon.Handle)));
            Console.WriteLine("    TryGetSizedIcons : " + (sizedOk ? "成功" : "★ 失败"));
            if (sized16 != null) Console.WriteLine("      16px  句柄=" + Hex(sized16.Handle));
            if (sized32 != null) Console.WriteLine("      32px  句柄=" + Hex(sized32.Handle));
            Console.WriteLine();

            // ---- 2) 托管层 ----
            Console.WriteLine("[2] 托管层（Form.Icon）");
            Console.WriteLine("    form.Icon : " +
                (form.Icon == null ? "★ null" : form.Icon.Width + "x" + form.Icon.Height +
                                                 "  句柄=" + Hex(form.Icon.Handle)));
            Console.WriteLine();

            // ---- 3) Win32 层 ----
            var wSmall = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
            var wSmall2 = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero);
            var wBig = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
            var clsSmall = GetClassLongPtr(hwnd, GCLP_HICONSM);
            var clsBig = GetClassLongPtr(hwnd, GCLP_HICON);

            Console.WriteLine("[3] Win32 层（系统记录的句柄，仅供参考）");
            Console.WriteLine("    WM_GETICON ICON_SMALL : " + Hex(wSmall));
            Console.WriteLine("    WM_GETICON SMALL2     : " + Hex(wSmall2));
            Console.WriteLine("    WM_GETICON ICON_BIG   : " + Hex(wBig));
            Console.WriteLine("    GetClassLong HICONSM  : " + Hex(clsSmall));
            Console.WriteLine("    GetClassLong HICON    : " + Hex(clsBig));
            Console.WriteLine();

            var effSmall = wSmall != IntPtr.Zero ? wSmall
                         : (wSmall2 != IntPtr.Zero ? wSmall2 : clsSmall);
            var effBig = wBig != IntPtr.Zero ? wBig : clsBig;

            // ---- 4) 渲染出来看内容 ----
            var bmpSmall = RenderHandle(effSmall, 16);
            var bmpBig = RenderHandle(effBig, 32);
            var bmpExp16 = RenderIcon(sized16, 16);
            var bmpExp32 = RenderIcon(sized32, 32);

            int pass = 0, fail = 0;
            void Check(string what, bool ok)
            {
                Console.WriteLine("  [" + (ok ? "通过" : "★ 失败") + "] " + what);
                if (ok) pass++; else fail++;
            }

            Console.WriteLine("[4] 判定（以系统实际会绘制的图标内容为准）");

            double rSmall = ContentRatio(bmpSmall);
            double rBig = ContentRatio(bmpBig);

            Check("标题栏图标有实际内容（覆盖率 " + Pct(rSmall) + "）", rSmall >= 0.05);
            Check("任务栏图标有实际内容（覆盖率 " + Pct(rBig) + "）", rBig >= 0.05);

            double sSmall = Similarity(bmpSmall, bmpExp16);
            double sBig = Similarity(bmpBig, bmpExp32);
            Check("标题栏图标与设计稿一致（相似度 " + Pct(sSmall) + "）", sSmall >= 0.80);
            Check("任务栏图标与设计稿一致（相似度 " + Pct(sBig) + "）", sBig >= 0.80);

            Check("16px 图标尺寸正确", sized16 != null && sized16.Width == 16);
            Check("32px 图标尺寸正确", sized32 != null && sized32.Width == 32);

            Console.WriteLine();
            Console.WriteLine("合计: " + pass + " 项通过, " + fail + " 项失败");

            // 导出对比图，便于肉眼确认
            try
            {
                var outPath = Path.Combine(Path.GetTempPath(), "xime_window_icons.png");
                SaveSideBySide(outPath, bmpSmall, bmpBig, bmpExp32);
                Console.WriteLine();
                Console.WriteLine("对比图已导出（左=标题栏 中=任务栏 右=设计稿）:");
                Console.WriteLine("  " + outPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("导出对比图失败: " + ex.Message);
            }

            if (bmpSmall != null) bmpSmall.Dispose();
            if (bmpBig != null) bmpBig.Dispose();
            if (bmpExp16 != null) bmpExp16.Dispose();
            if (bmpExp32 != null) bmpExp32.Dispose();

            Console.WriteLine();
            if (fail > 0)
            {
                Console.WriteLine("排查提示：");
                Console.WriteLine("  · [1] 显示「未加载」→ 图标没进 DLL，检查 EmbeddedResource 配置；");
                Console.WriteLine("  · 覆盖率接近 0 → 句柄已失效（图标对象被 GC 回收）或图标本身空白；");
                Console.WriteLine("  · 相似度低但覆盖率正常 → 挂上的是别的图标（被宿主覆盖）。");
            }

            if (keep)
            {
                Console.WriteLine("--keep：窗体显示在屏幕外，5 秒后自动关闭…");
                var t = new Timer { Interval = 5000 };
                t.Tick += (s, e) => { t.Stop(); form.Close(); };
                t.Start();
                Application.Run(form);
            }
            else
            {
                form.Close();
            }

            return fail == 0 ? 0 : 1;
        }

        #region 渲染与度量

        private static string Pct(double v)
        {
            return (v * 100).ToString("F0") + "%";
        }

        /// <summary>
        /// 把一个 HICON 渲染成指定边长的位图。
        /// 注意：<see cref="Icon.FromHandle"/> 不接管句柄所有权，
        /// 因此**不能** Dispose 这个 Icon（会销毁窗口正在用的图标）。
        /// </summary>
        private static Bitmap RenderHandle(IntPtr hIcon, int size)
        {
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var icon = Icon.FromHandle(hIcon);
                using (var raw = icon.ToBitmap())
                {
                    return new Bitmap(raw, new Size(size, size));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Bitmap RenderIcon(Icon icon, int size)
        {
            if (icon == null) return null;
            try
            {
                using (var raw = icon.ToBitmap())
                {
                    return new Bitmap(raw, new Size(size, size));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>有内容像素的占比：不透明、且与四角背景色不同的像素比例。</summary>
        private static double ContentRatio(Bitmap b)
        {
            if (b == null) return 0.0;

            var bg = b.GetPixel(0, 0);
            int ink = 0, total = b.Width * b.Height;

            for (int y = 0; y < b.Height; y++)
            {
                for (int x = 0; x < b.Width; x++)
                {
                    var c = b.GetPixel(x, y);
                    if (c.A < 128) continue;                 // 透明不算
                    int d = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
                    if (d > 60) ink++;
                }
            }
            return total == 0 ? 0.0 : (double)ink / total;
        }

        /// <summary>两张同尺寸位图的相似度（1 = 完全一致）。</summary>
        private static double Similarity(Bitmap a, Bitmap b)
        {
            if (a == null || b == null) return 0.0;
            if (a.Width != b.Width || a.Height != b.Height) return 0.0;

            long diff = 0, n = 0;
            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    var ca = a.GetPixel(x, y);
                    var cb = b.GetPixel(x, y);
                    diff += Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);
                    n += 3;
                }
            }
            if (n == 0) return 0.0;
            return 1.0 - ((double)diff / (n * 255.0));
        }

        /// <summary>把三张图并排放大导出，便于肉眼确认。</summary>
        private static void SaveSideBySide(string path, Bitmap sm, Bitmap bg, Bitmap expected)
        {
            const int cell = 128, pad = 16;
            var panels = new[] { sm, bg, expected };

            using (var canvas = new Bitmap(cell * 3 + pad * 4, cell + pad * 2))
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.White);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;

                for (int i = 0; i < panels.Length; i++)
                {
                    int x = pad + i * (cell + pad);
                    g.DrawRectangle(Pens.LightGray, x, pad, cell, cell);
                    if (panels[i] != null)
                        g.DrawImage(panels[i], x, pad, cell, cell);
                    else
                        g.DrawString("（空）", SystemFonts.DefaultFont, Brushes.Red, x + 4, pad + 4);
                }

                canvas.Save(path, ImageFormat.Png);
            }
        }

        #endregion
    }
}
