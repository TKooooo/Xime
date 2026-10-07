using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;

namespace Xime.Core.UI
{
    /// <summary>
    /// 内嵌资源访问器（图标 / Logo）。
    ///
    /// 资源随 Xime.Core.dll 一起分发，不依赖任何外部文件 ——
    /// 用户 NETLOAD 加载 DLL 后，界面标题栏、任务栏以及状态条上的
    /// X输入法 logo 就能直接显示。
    ///
    /// 取值顺序（任一层成功即返回）：
    ///   1. DLL 同目录下的**外部文件**（XimeLogo.ico / XimeLogo.png / X输入法.png）
    ///      —— 方便临时替换或排查"图标没出来"；
    ///   2. DLL 内的**内嵌资源**（正常路径）。
    ///
    /// 所有方法都做了容错：资源缺失或加载失败时返回 null，
    /// 调用方按"没有图标"降级处理，绝不影响插件主体功能。
    /// 失败原因可用 <see cref="Diagnose"/> 打印出来定位。
    /// </summary>
    public static class XimeResources
    {
        #region 资源名

        private const string IconResource = "Xime.Core.Resources.XimeLogo.ico";
        private const string ImageResource = "Xime.Core.Resources.XimeLogo.png";

        // DLL 同目录下的可选覆盖文件（按顺序尝试）
        private const string ExternalIconFile = "XimeLogo.ico";
        private static readonly string[] ExternalImageFiles =
            { "XimeLogo.png", "X输入法.png" };

        #endregion

        #region 状态

        private static Icon _icon;
        private static Image _logo;
        private static bool _iconTried;
        private static bool _logoTried;
        private static bool _sizedTried;

        private static string _iconSource = "（未加载）";
        private static string _logoSource = "（未加载）";
        private static string _iconError;
        private static string _logoError;

        // 需要长期持有的对象：GDI 句柄/图标一旦被 GC 回收，
        // 已经发给窗口的 WM_SETICON 就会失效（标题栏图标变空白）。
        private static Icon _iconSmall;
        private static Icon _iconBig;

        #endregion

        /// <summary>窗体图标（多尺寸 ICO，供标题栏 / 任务栏 / Alt+Tab 使用）。</summary>
        public static Icon AppIcon
        {
            get
            {
                if (!_iconTried)
                {
                    _iconTried = true;
                    _icon = LoadIcon();
                }
                return _icon;
            }
        }

        /// <summary>Logo 位图（透明 PNG，供界面内展示）。</summary>
        public static Image Logo
        {
            get
            {
                if (!_logoTried)
                {
                    _logoTried = true;
                    _logo = LoadImage();
                }
                return _logo;
            }
        }

        /// <summary>
        /// 取得用于 WM_SETICON 的小/大图标（16px / 32px），优先挑 ICO 里对应的尺寸，
        /// 避免 Windows 拿 256px 那层去缩放出糊边。
        ///
        /// ★ 只创建一次并缓存，这点至关重要：
        ///   早先的实现每次调用都 <c>new Icon(src, size)</c> 造一对新对象并覆盖静态字段。
        ///   后果是上一次那对 Icon 失去引用 → 被 GC 回收 → 其 HICON 被销毁，
        ///   而窗口里 WM_SETICON 记下的还是那个**已经失效的句柄** ——
        ///   WM_GETICON 依然返回非零（看起来"有图标"），但实际画不出来，
        ///   表现就是用户反馈的「标题栏左上角不显示 logo」。
        ///   实测（XimeTest iconcheck）：修正前服务器记录的句柄与设置值不一致。
        /// </summary>
        public static bool TryGetSizedIcons(out Icon small, out Icon big)
        {
            if (!_sizedTried)
            {
                _sizedTried = true;

                var src = AppIcon;
                if (src != null)
                {
                    try { _iconSmall = new Icon(src, new Size(16, 16)); }
                    catch (Exception) { _iconSmall = src; }

                    try { _iconBig = new Icon(src, new Size(32, 32)); }
                    catch (Exception) { _iconBig = src; }
                }
            }

            small = _iconSmall;
            big = _iconBig;
            return small != null && big != null;
        }

        #region 加载

        private static string BaseDir
        {
            get
            {
                try
                {
                    var loc = typeof(XimeResources).Assembly.Location;
                    if (!string.IsNullOrEmpty(loc))
                        return Path.GetDirectoryName(loc);
                }
                catch (Exception) { }
                return null;
            }
        }

        private static Stream OpenEmbedded(string name)
        {
            try
            {
                return typeof(XimeResources).Assembly
                    .GetManifestResourceStream(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Icon LoadIcon()
        {
            // ---- 1) 外部文件优先（可临时替换 / 排查用） ----
            var dir = BaseDir;
            if (dir != null)
            {
                var ext = Path.Combine(dir, ExternalIconFile);
                if (File.Exists(ext))
                {
                    try
                    {
                        using (var s = new FileStream(ext, FileMode.Open, FileAccess.Read))
                        {
                            _iconSource = "外部文件 " + ext;
                            return new Icon(s);
                        }
                    }
                    catch (Exception ex) { _iconError = "外部 .ico 读取失败: " + ex.Message; }
                }
            }

            // ---- 2) 内嵌资源 ----
            try
            {
                using (var s = OpenEmbedded(IconResource))
                {
                    if (s != null)
                    {
                        _iconSource = "内嵌资源 " + IconResource;
                        return new Icon(s);
                    }
                    _iconError = "未找到内嵌资源 " + IconResource;
                }
            }
            catch (Exception ex) { _iconError = "内嵌 .ico 解析失败: " + ex.Message; }

            // ---- 3) 兜底：拿 Logo 位图临时造一个图标 ----
            try
            {
                var img = Logo;
                if (img != null)
                {
                    var bmp = new Bitmap(img, 32, 32);
                    var h = bmp.GetHicon();
                    _iconSource = "由 Logo 位图临时生成（无多尺寸）";
                    return Icon.FromHandle(h);
                }
            }
            catch (Exception ex) { _iconError = (_iconError ?? "") + " ／ 位图兜底失败: " + ex.Message; }

            return null;
        }

        private static Image LoadImage()
        {
            var dir = BaseDir;

            // ---- 1) 外部文件优先 ----
            if (dir != null)
            {
                foreach (var name in ExternalImageFiles)
                {
                    var ext = Path.Combine(dir, name);
                    if (!File.Exists(ext)) continue;
                    try
                    {
                        // 用 File.ReadAllBytes 而非 Image.FromStream(FileStream)：
                        // 避免文件被占用，同时满足 Image 要求流保持打开的限制。
                        var bytes = File.ReadAllBytes(ext);
                        _logoSource = "外部文件 " + ext;
                        return Image.FromStream(new MemoryStream(bytes));
                    }
                    catch (Exception ex) { _logoError = "外部 " + name + " 读取失败: " + ex.Message; }
                }
            }

            // ---- 2) 内嵌资源 ----
            try
            {
                using (var s = OpenEmbedded(ImageResource))
                {
                    if (s != null)
                    {
                        // 必须拷贝：Image.FromStream 要求流在图像生命周期内保持打开。
                        // 注意：Stream.CopyTo 是 .NET 4.0+ API，net35 目标下不可用，
                        // 因此这里手写拷贝循环。
                        var ms = new MemoryStream();
                        var buf = new byte[8192];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                        }
                        ms.Position = 0;
                        _logoSource = "内嵌资源 " + ImageResource;
                        return Image.FromStream(ms);
                    }
                    _logoError = "未找到内嵌资源 " + ImageResource;
                }
            }
            catch (Exception ex) { _logoError = "内嵌 .png 解析失败: " + ex.Message; }

            return null;
        }

        #endregion

        #region 诊断

        /// <summary>
        /// 诊断用：列出程序集内全部资源名（排查"图标没出来"时用）。
        /// </summary>
        public static string[] ListResourceNames()
        {
            try
            {
                var names = typeof(XimeResources).Assembly.GetManifestResourceNames();
                Array.Sort(names, StringComparer.Ordinal);
                return names;
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        /// <summary>
        /// 输出一份图标/Logo 加载情况报告。
        ///
        /// 主要用途：确认真实运行环境里 **到底加载了哪一份 Xime.Core.dll**，
        /// 以及图标是从内嵌资源还是外部文件来的。
        /// （AutoCAD 无法卸载已加载的程序集，若同一会话里先 NETLOAD 过旧版本，
        ///   之后即使换成新目录的 Xime.dll，仍会复用旧的 Xime.Core —— 这里能看出来。）
        /// </summary>
        public static string Diagnose()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== X输入法 图标资源诊断 =====");

            var asm = typeof(XimeResources).Assembly;
            try { sb.AppendLine("Xime.Core 版本: " + asm.GetName().Version); }
            catch (Exception) { }

            try { sb.AppendLine("Xime.Core 路径: " + asm.Location); }
            catch (Exception) { }

            var dir = BaseDir;
            sb.AppendLine("DLL 所在目录: " + (dir ?? "未知"));

            if (dir != null)
            {
                sb.AppendLine("外部 XimeLogo.ico: " +
                    (File.Exists(Path.Combine(dir, "XimeLogo.ico")) ? "有" : "无"));
                foreach (var n in ExternalImageFiles)
                {
                    sb.AppendLine("外部 " + n + ": " +
                        (File.Exists(Path.Combine(dir, n)) ? "有" : "无"));
                }
            }

            // 触发一次加载，随后读出来源
            var icon = AppIcon;
            var logo = Logo;

            sb.AppendLine("窗体图标: " + (icon == null ? "★ 加载失败" : "正常"));
            sb.AppendLine("  来源: " + _iconSource);
            if (icon != null) sb.AppendLine("  尺寸: " + icon.Width + "x" + icon.Height);
            if (!string.IsNullOrEmpty(_iconError)) sb.AppendLine("  错误: " + _iconError);

            sb.AppendLine("界面 Logo: " + (logo == null ? "★ 加载失败" : "正常"));
            sb.AppendLine("  来源: " + _logoSource);
            if (logo != null) sb.AppendLine("  尺寸: " + logo.Width + "x" + logo.Height);
            if (!string.IsNullOrEmpty(_logoError)) sb.AppendLine("  错误: " + _logoError);

            var names = ListResourceNames();
            sb.AppendLine("程序集内资源数: " + names.Length);
            foreach (var n in names)
            {
                if (n.IndexOf("XimeLogo", StringComparison.OrdinalIgnoreCase) >= 0)
                    sb.AppendLine("  · " + n);
            }

            sb.AppendLine("================================");
            return sb.ToString();
        }

        #endregion
    }
}
