using System;
using System.Drawing;
using System.Windows.Forms;
using Xime.Core;
using Xime.Core.UI;

namespace Xime.CliTest
{
    /// <summary>
    /// 设置界面独立预览器。
    ///
    /// ★ 直接实例化**正式插件所用的同一个窗体** <see cref="XimeSettingsForm"/> ——
    ///   界面代码只有一份，因此预览截图与实际运行效果必然一致，
    ///   不会再出现"预览好看、实机被挤变形"的偏差。
    ///
    ///   （早期本文件自行复制了一份布局代码，导致两边结构/尺寸漂移：
    ///     实测出现过分组顺序颠倒、帮助区被压扁等情况。现已改为复用。）
    /// </summary>
    internal static class UiPreview
    {
        public static void Show()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 与插件同样的构造方式；预览时无引擎、无宿主回调
            var config = new XimeConfig();
            // 版本号不写死：走 XimeSettingsForm 的默认值（FallbackVersion）
            var form = new XimeSettingsForm(config, null);

            // 支持 --shot <路径>：显示后自动截图并退出，用于无人值守验证布局
            var args = Environment.GetCommandLineArgs();
            string shotPath = null;
            int sizeW = 0, sizeH = 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--shot") { shotPath = args[i + 1]; }
                else if (args[i] == "--size") { ParseSize(args[i + 1], out sizeW, out sizeH); }
            }

            if (shotPath != null)
            {
                var timer = new Timer { Interval = 900 };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    try
                    {
                        // 可选：截图前把窗口改成指定尺寸，用于验证"小屏 / 最小窗口"下的布局
                        // 是否会挤压或截断（会被窗体的 MinimumSize 夹住，正好一并验证）
                        if (sizeW > 0 && sizeH > 0)
                            form.Size = new Size(sizeW, sizeH);

                        CaptureTo(form, shotPath);
                    }
                    catch (Exception ex) { Console.WriteLine("截图失败: " + ex.Message); }
                    form.Close();
                };
                timer.Start();
            }

            Application.Run(form);
        }

        /// <summary>解析 "宽x高"（如 860x600），失败则保持 0。</summary>
        private static void ParseSize(string text, out int w, out int h)
        {
            w = h = 0;
            if (string.IsNullOrEmpty(text)) return;
            var parts = text.ToLowerInvariant().Split('x');
            if (parts.Length != 2) return;
            int.TryParse(parts[0], out w);
            int.TryParse(parts[1], out h);
            if (w <= 0 || h <= 0) { w = h = 0; }
        }

        /// <summary>把窗体渲染到位图并保存为 PNG。</summary>
        private static void CaptureTo(Form form, string path)
        {
            form.Refresh();
            Application.DoEvents();

            using (var bmp = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("已保存截图: " + path);
        }
    }
}
