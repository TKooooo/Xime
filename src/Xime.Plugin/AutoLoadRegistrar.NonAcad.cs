#if HOST_ZWCAD || HOST_GSTARCAD
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Xime.Plugin
{
    /// <summary>
    /// **中望 CAD / 浩辰 CAD 的自动加载注册器**（本次扩展新增）。
    ///
    /// ★ 两家的注册表形状几乎一样（都是 AutoCAD 风格的"产品\版本\语言\Applications\
    ///   你的程序名"三层结构），因此共用这一份实现，只靠编译符号区分根路径与宿主名。
    ///
    /// 实测（本机已装两套 2026）：
    ///   中望  HKCU\Software\ZWSOFT\ZWCAD\2026\zh-CN\Applications          （已存在，为空）
    ///   浩辰  HKCU\Software\Gstarsoft\GstarCAD\R26\zh-CN\Applications     （已存在，为空）
    ///
    /// ★ 与 AutoCAD 版的三个差异（不能照抄，抄错就是"注册成功但不加载"）：
    ///   1. 根路径不同：`Software\Autodesk\AutoCAD\<Rxx.x>\<产品键>` 换成
    ///      `Software\ZWSOFT\ZWCAD\<版本>\<语言>` / `Software\Gstarsoft\GstarCAD\<系列>\<语言>`；
    ///   2. 定位方式不同：AutoCAD 靠产品键下的 `AcadLocation`，这两家靠
    ///      `<版本>\<语言>` 下的 `LOCATION` / `Location`（指向安装目录，可与当前进程
    ///      的 exe 目录精确比对）；
    ///   3. 浩辰官方文档给出的键名是 `ManagedType` / `LoaderType`，而它自家的中文技术文章
    ///      又按 AutoCAD 惯例写成 `MANAGED` / `LOADER` / `LOADCTRLS` —— 两种说法无法从
    ///      文档判定谁对。**全部写一遍**：多写一个宿主不认的值没有任何副作用，
    ///      而少写一个就是"注册了但重启不加载"，且现场极难定位。
    ///
    /// ★ 只写 HKCU、只动当前版本：
    ///   HKLM 需要管理员权限；且"卸载"的语义是"这个版本里我不要这个插件了"，
    ///   绝不遍历所有版本把别的 CAD 一起清掉（AutoCAD 版当年就栽在这上面，见其注释）。
    /// </summary>
    internal static class UninstallHelper
    {
        private const string AppName = "XIME";

#if HOST_ZWCAD
        private const string ProductRoot = @"Software\ZWSOFT\ZWCAD";
        private const string HostDisplay = "中望CAD (ZWCAD)";
        private const string HostExeName = "ZWCAD.exe";
#else
        private const string ProductRoot = @"Software\Gstarsoft\GstarCAD";
        private const string HostDisplay = "浩辰CAD (GstarCAD)";
        private const string HostExeName = "gcad.exe";
#endif

        /// <summary>Applications 段名。浩辰官方文档特别强调：**必须**是复数 'Applications'。</summary>
        private const string ApplicationsSegment = "Applications";

        #region 定位当前版本

        /// <summary>
        /// 取当前宿主版本对应的 <c>...\Applications\XIME</c> **相对路径**（不含 hive）。
        /// 形如 <c>Software\ZWSOFT\ZWCAD\2026\zh-CN\Applications\XIME</c>。
        /// 定位不到时返回 null。
        /// </summary>
        public static string FindCurrentVersionApplicationKey()
        {
            var basePath = FindCurrentVersionLangPath();
            return basePath == null ? null : basePath + @"\" + ApplicationsSegment + @"\" + AppName;
        }

        /// <summary>
        /// 找当前宿主版本的 <c>...\&lt;版本&gt;\&lt;语言&gt;</c> 相对路径。
        ///
        /// 判据顺序（越靠前越精确）：
        ///   ① 该分支下的 <c>LOCATION</c> / <c>Location</c> == 当前进程 exe 所在目录；
        ///   ② 退而求其次：该分支下存在 <c>Applications</c> 子键（说明就是它读自动加载的地方）。
        ///
        /// 先查 HKCU 再查 HKLM —— 两处都可能存在，但**写入只走 HKCU**。
        /// </summary>
        private static string FindCurrentVersionLangPath()
        {
            var exeDir = CurrentExeDirectory();

            string fallback = null;

            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var root = hive.OpenSubKey(ProductRoot))
                    {
                        if (root == null) continue;

                        foreach (var series in root.GetSubKeyNames())
                        {
                            using (var seriesKey = root.OpenSubKey(series))
                            {
                                if (seriesKey == null) continue;

                                foreach (var lang in seriesKey.GetSubKeyNames())
                                {
                                    using (var langKey = seriesKey.OpenSubKey(lang))
                                    {
                                        if (langKey == null) continue;

                                        var hasApps = HasApplicationsSubKey(langKey);
                                        if (!hasApps) continue;

                                        var rel = ProductRoot + @"\" + series + @"\" + lang;

                                        // ① 安装目录精确匹配
                                        if (!string.IsNullOrEmpty(exeDir))
                                        {
                                            var loc = ReadString(langKey, "LOCATION")
                                                   ?? ReadString(langKey, "Location");
                                            if (!string.IsNullOrEmpty(loc) &&
                                                NormalizePath(loc) == NormalizePath(exeDir))
                                                return rel;
                                        }

                                        // ② 兜底：第一个带 Applications 的分支
                                        if (fallback == null) fallback = rel;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception) { }
            }

            return fallback;
        }

        private static bool HasApplicationsSubKey(RegistryKey langKey)
        {
            try
            {
                using (var apps = langKey.OpenSubKey(ApplicationsSegment))
                    return apps != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>当前宿主进程 exe 所在目录（用于与注册表 LOCATION 比对）。</summary>
        private static string CurrentExeDirectory()
        {
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var exe = proc.MainModule != null ? proc.MainModule.FileName : null;
                if (!string.IsNullOrEmpty(exe)) return System.IO.Path.GetDirectoryName(exe);
            }
            catch (Exception) { }

            // 回退：宿主 exe 按名字查（部分装载场景下主模块不是它）
            try
            {
                var mod = Xime.Core.Native.NativeMethods.GetModuleHandle(HostExeName);
                if (mod != IntPtr.Zero) return null;   // 拿不到完整路径，交给调用方兜底
            }
            catch (Exception) { }

            return null;
        }

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return string.Empty;
            return p.Trim().TrimEnd('\\', '/').ToLowerInvariant();
        }

        private static string ReadString(RegistryKey key, string name)
        {
            try
            {
                var v = key.GetValue(name);
                return v as string;
            }
            catch (Exception) { return null; }
        }

        #endregion

        #region 注册 / 卸载

        /// <summary>
        /// 为**当前宿主版本**写入自动加载项（幂等）。
        /// </summary>
        /// <param name="dllPath">插件 DLL 完整路径。</param>
        /// <returns>实际写入的注册表路径列表（正常 0 或 1 项）。</returns>
        public static List<string> RegisterAutoLoad(string dllPath)
        {
            var written = new List<string>();
            if (string.IsNullOrEmpty(dllPath)) return written;

            var key = FindCurrentVersionApplicationKey();
            if (string.IsNullOrEmpty(key))
            {
                // 定位不到：写进任何地方都是"注册了但不生效"，不如明确失败，
                // 由调用方在命令行告知用户（可用 XIMEREG 看到底哪一步没找到）。
                return written;
            }

            if (WriteAppKey(key, dllPath)) written.Add(key);
            return written;
        }

        /// <summary>向 <c>...\Applications\XIME</c> 写入加载配置。</summary>
        private static bool WriteAppKey(string appKeyPath, string dllPath)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(appKeyPath))
                {
                    if (key == null) return false;

                    // LOADCTRLS 位标志（与 AutoCAD 同义）：
                    //   1=打开图形, 2=关闭图形, 4=启动, 8=执行命令 → 15 = 全部场景都加载
                    key.SetValue("DESCRIPTION", "X输入法 - " + HostDisplay + " 输入法智能切换插件",
                                 RegistryValueKind.String);
                    key.SetValue("LOADCTRLS", 15, RegistryValueKind.DWord);
                    key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
                    key.SetValue("LOADER", dllPath, RegistryValueKind.String);

                    // ★ 浩辰官方 2024《Application Program Auto Load Guide》给的是这两个名字
                    //   （ManagedType: 0=GRX / 1=.NET；LoaderType: DLL 路径）。
                    //   与上面的 AutoCAD 风格键名**同时写**，避免因文档分歧而漏加载。
                    key.SetValue("ManagedType", 1, RegistryValueKind.DWord);
                    key.SetValue("LoaderType", dllPath, RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 移除本插件的自动加载项 —— **只移除当前宿主版本**。
        /// 与 <see cref="RegisterAutoLoad"/> 严格对称；定位不到时一个键都不删。
        /// </summary>
        /// <returns>确实删掉了 → true。</returns>
        public static bool RemoveAutoLoadRegistration()
        {
            var basePath = FindCurrentVersionLangPath();
            if (string.IsNullOrEmpty(basePath)) return false;

            try
            {
                using (var apps = Registry.CurrentUser.OpenSubKey(
                           basePath + @"\" + ApplicationsSegment, writable: true))
                {
                    if (apps == null) return false;
                    apps.DeleteSubKey(AppName, throwOnMissingSubKey: false);
                }

                // 复核：删完再看一眼，别把"我以为删了"当成"删了"
                using (var probe = Registry.CurrentUser.OpenSubKey(
                           basePath + @"\" + ApplicationsSegment + @"\" + AppName))
                {
                    return probe == null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>当前宿主版本是否已注册自动加载。</summary>
        public static bool IsAutoLoadRegistered()
        {
            var key = FindCurrentVersionApplicationKey();
            if (string.IsNullOrEmpty(key)) return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(key))
                {
                    return k != null && k.GetValue("LOADER") != null;
                }
            }
            catch (Exception) { return false; }
        }

        #endregion

        #region 诊断

        /// <summary>生成自动加载注册表的诊断文本（供 XIMEREG 命令使用）。</summary>
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("宿主            : " + HostDisplay);
            sb.AppendLine("注册表根        : HKCU\\" + ProductRoot);

            string exeDir = CurrentExeDirectory();
            sb.AppendLine("当前进程目录    : " + (exeDir ?? "(读取失败)"));

            var langPath = FindCurrentVersionLangPath();
            sb.AppendLine("定位到的版本分支: " + (langPath ?? "(未定位)"));

            var appKey = FindCurrentVersionApplicationKey();
            sb.AppendLine("目标自动加载键  : " + (appKey ?? "(未定位)"));

            sb.AppendLine();
            sb.AppendLine("全部候选分支（HKCU）：");
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(ProductRoot))
                {
                    if (root == null)
                    {
                        sb.AppendLine("  (HKCU 下不存在 " + ProductRoot + ")");
                    }
                    else
                    {
                        foreach (var series in root.GetSubKeyNames())
                        {
                            using (var seriesKey = root.OpenSubKey(series))
                            {
                                if (seriesKey == null) continue;
                                foreach (var lang in seriesKey.GetSubKeyNames())
                                {
                                    using (var langKey = seriesKey.OpenSubKey(lang))
                                    {
                                        if (langKey == null) continue;
                                        var rel = ProductRoot + @"\" + series + @"\" + lang;
                                        bool hasApps = HasApplicationsSubKey(langKey);
                                        string loader = null;
                                        try
                                        {
                                            using (var k = Registry.CurrentUser.OpenSubKey(
                                                       rel + @"\" + ApplicationsSegment + @"\" + AppName))
                                            {
                                                if (k != null) loader = k.GetValue("LOADER") as string;
                                            }
                                        }
                                        catch (Exception) { }

                                        sb.AppendLine(string.Format("  {0}  Applications={1}  XIME={2}",
                                            rel,
                                            hasApps ? "有 ✓" : "无",
                                            loader != null ? "已写入" : "无"));
                                        if (!string.IsNullOrEmpty(loader))
                                            sb.AppendLine("        LOADER = " + loader);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  枚举失败: " + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine("当前版本是否已注册自动加载: " + (IsAutoLoadRegistered() ? "是 ✓" : "否"));
            return sb.ToString();
        }

        #endregion
    }
}
#endif
