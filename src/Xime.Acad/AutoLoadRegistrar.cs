using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Xime.Plugin
{
    /// <summary>
    /// AutoCAD 自动加载注册表读写。
    ///
    /// 原理：AutoCAD 启动时会枚举
    ///   HKCU\Software\Autodesk\AutoCAD\Rxx.x\ACAD-xxxx:804\Applications
    /// 下的子键，读取其中的 LOADCTRLS / LOADER 值来决定是否加载插件。
    ///
    /// ★ 版本隔离策略（重要）：
    ///   默认**只给「当前正在运行的这一个 AutoCAD 版本」注册自动加载**，
    ///   其它已安装版本不受影响，需要时各自手动 NETLOAD。
    ///   这样在多版本并存的机器上，不会出现"装一次、全版本都被改"的副作用。
    ///   判定依据：当前进程 acad.exe 的路径 == 注册表产品键下的 AcadLocation。
    /// </summary>
    internal static class UninstallHelper
    {
        private const string AppName = "XIME";
        private const string RegBase = @"Software\Autodesk\AutoCAD";

        /// <summary>
        /// 为**当前 AutoCAD 版本**写入自动加载注册表项。
        ///
        /// 只匹配「当前进程 acad.exe 所在目录」对应的那个产品键；
        /// 找不到精确匹配时，退化为"只写第一个匹配到的产品键"，
        /// 绝不再像旧版那样把 R18.0~R25.2 全部写一遍。
        /// </summary>
        /// <param name="dllPath">插件 DLL 完整路径。</param>
        /// <returns>实际写入的注册表路径列表（正常为 0 或 1 项）。</returns>
        public static List<string> RegisterAutoLoad(string dllPath)
        {
            var written = new List<string>();
            if (string.IsNullOrEmpty(dllPath))
                return written;

            // 优先：精确定位「当前运行版本」对应的产品键
            var currentKey = FindCurrentVersionApplicationKey();

            if (!string.IsNullOrEmpty(currentKey))
            {
                // 清理同一版本分支下的历史残留（尤其旧版误写入的「空壳产品键」项），
                // 保证自动加载项只存在于 AutoCAD 真正读取的那个键下。
                RemoveStaleKeysInSameVersion(currentKey);

                if (WriteAppKey(currentKey, dllPath))
                    written.Add(currentKey);
                return written;
            }

            // 回退：找不到精确匹配时，只写**第一个真实产品键**（仍然只写一个）
            foreach (var kv in EnumerateProductKeys())
            {
                if (!IsRealProductKey(kv.Value)) continue;
                var appKey = kv.Value + @"\Applications\" + AppName;
                if (WriteAppKey(appKey, dllPath))
                {
                    written.Add(appKey);
                    break;
                }
            }

            return written;
        }

        /// <summary>
        /// 清理「同一版本分支下、非目标产品键」里的 XIME 残留项。
        ///
        /// 背景：某些 AutoCAD 安装在同一版本分支下同时存在
        ///   · 空壳产品键（如 R19.1\ACAD-D001，无任何属性值）
        ///   · 真实产品键（如 R19.1\ACAD-D001:804，含 LocalRootFolder 等）
        /// 旧实现可能把自动加载项写进空壳键 —— 既不生效又留下垃圾项。
        /// 本方法把同分支内的非目标项清掉，使注册项唯一且正确。
        ///
        /// 只清理**当前版本分支**，不影响其它版本的注册（与「版本隔离」设计一致）。
        /// </summary>
        private static void RemoveStaleKeysInSameVersion(string keepKey)
        {
            if (string.IsNullOrEmpty(keepKey)) return;
            try
            {
                var versionPath = VersionPathOf(keepKey);
                if (string.IsNullOrEmpty(versionPath)) return;

                foreach (var kv in EnumerateProductKeys())
                {
                    var appKey = kv.Value + @"\Applications\" + AppName;
                    if (string.Equals(appKey, keepKey, StringComparison.OrdinalIgnoreCase))
                        continue;   // 目标键，保留

                    if (!string.Equals(VersionPathOf(appKey), versionPath,
                                       StringComparison.OrdinalIgnoreCase))
                        continue;   // 不是同一版本分支，跳过

                    DeleteAppKey(kv.Value);
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 取一个 <c>...\Rxx.x\产品键\Applications\XIME</c> 路径所属的**版本分支**路径，
        /// 形如 <c>Software\Autodesk\AutoCAD\R19.1</c>；解析不出来返回空串。
        ///
        /// ★ 解析规则只有一处定义：<see cref="Xime.Core.RegPath.VersionPathOf"/> ——
        ///   "哪些键算同一个 CAD 版本" 是「按版本隔离」的唯一定义，
        ///   注册 / 查询 / 卸载三处必须共用，各写一遍必然漂移。
        /// </summary>
        private static string VersionPathOf(string appKeyPath)
        {
            return Xime.Core.RegPath.VersionPathOf(appKeyPath);
        }

        /// <summary>删除指定产品键下 Applications\XIME 子键（不存在则忽略）。</summary>
        /// <param name="productKeyPath">产品键路径，形如 ...\R19.1\ACAD-D001:804。</param>
        /// <returns>调用后该 XIME 键确实已不存在 → true。</returns>
        private static bool DeleteAppKey(string productKeyPath)
        {
            if (string.IsNullOrEmpty(productKeyPath)) return false;
            var appKeyPath = productKeyPath + @"\Applications\" + AppName;
            try
            {
                using (var apps = Registry.CurrentUser.OpenSubKey(
                    productKeyPath + @"\Applications", writable: true))
                {
                    if (apps == null) return false;
                    // 用 DeleteSubKey（非 Tree），只删 XIME 自身，不牵连兄弟项
                    try { apps.DeleteSubKey(AppName, throwOnMissingSubKey: false); }
                    catch (Exception)
                    {
                        // 该键下意外多出子键时 DeleteSubKey 会抛 —— 退一步用 Tree，
                        // 范围仍然只是 XIME 自己。
                        // 注：net35 没有 DeleteSubKeyTree(name, throwOnMissingSubKey)
                        //     这个重载（4.0+ 才有），所以直接调用并靠外层 try 兜住。
                        try { apps.DeleteSubKeyTree(AppName); }
                        catch (Exception) { }
                    }
                }

                // 复核：删完再看一眼，别把"我以为删了"当成"删了"
                using (var probe = Registry.CurrentUser.OpenSubKey(appKeyPath))
                {
                    return probe == null;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 生成自动加载注册表的诊断文本（供 XIMEREG 命令使用）。
        ///
        /// 输出：ACADVER、定位到的目标键、同版本下各候选产品键的「真实产品键」判定、
        ///       以及每个键中是否存在 XIME 项 —— 用于快速诊断「重启后不自动加载」。
        /// </summary>
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder();

            string acadVer = "(读取失败)";
            try
            {
                var o = Autodesk.AutoCAD.ApplicationServices.Application
                    .GetSystemVariable("ACADVER");
                acadVer = o == null ? "(空)" : o.ToString();
            }
            catch (Exception) { }
            sb.AppendLine("ACADVER          : " + acadVer);

            var target = FindCurrentVersionApplicationKey();
            sb.AppendLine("定位到的目标键   : " + (target ?? "(未定位)") +
                (target != null && target.IndexOf(':') >= 0 ? "  ✓ 含语言后缀" : "  ⚠ 无语言后缀"));

            sb.AppendLine();
            sb.AppendLine("全部候选产品键：");
            foreach (var kv in EnumerateProductKeys())
            {
                bool real = IsRealProductKey(kv.Value);
                bool hasXime = false;
                string loader = null;
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(kv.Value + @"\Applications\" + AppName))
                    {
                        if (k != null)
                        {
                            hasXime = true;
                            loader = k.GetValue("LOADER") as string;
                        }
                    }
                }
                catch (Exception) { }

                sb.AppendLine(string.Format("  [{0}] {1}  {2}  XIME={3}",
                    kv.Key, kv.Value,
                    real ? "真实产品键 ✓" : "空壳键 ✗",
                    hasXime ? "已写入" : "无"));
                if (hasXime && !string.IsNullOrEmpty(loader))
                    sb.AppendLine("        LOADER = " + loader);
            }

            sb.AppendLine();
            sb.AppendLine("当前版本是否已注册自动加载: " + (IsAutoLoadRegistered() ? "是 ✓" : "否"));
            return sb.ToString();
        }

        /// <summary>向指定的 Applications\XIME 键写入自动加载配置。</summary>
        private static bool WriteAppKey(string appKeyPath, string dllPath)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(appKeyPath))
                {
                    if (key == null) return false;

                    // LOADCTRLS 位标志：1=打开图形时加载, 2=关闭图形时, 4=启动时,
                    // 8=执行命令时, 16=加载图形时...
                    // 这里用 1|2|4|8 = 15，确保各类场景都会加载。
                    key.SetValue("DESCRIPTION", "X输入法 - AutoCAD 输入法智能切换插件", RegistryValueKind.String);
                    key.SetValue("LOADCTRLS", 15, RegistryValueKind.DWord);
                    key.SetValue("LOADER", dllPath, RegistryValueKind.String);
                    key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 找到「当前正在运行的 AutoCAD」对应的 Applications\XIME 注册表路径。
        ///
        /// 判定顺序（越靠前越精确）：
        ///   1. 当前进程 acad.exe 所在目录 == 产品键下的 AcadLocation（去尾斜杠比较）；
        ///   2. 用系统变量 ACADVER 的前缀（如 "19.1"）匹配注册表分支 R19.1；
        ///   3. 都失败则返回 null（由调用方回退）。
        /// </summary>
        public static string FindCurrentVersionApplicationKey()
        {
            // ---- 1) 用当前进程 acad.exe 路径精确匹配 AcadLocation ----
            string exeDir = null;
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var exePath = proc.MainModule != null ? proc.MainModule.FileName : null;
                if (!string.IsNullOrEmpty(exePath))
                    exeDir = System.IO.Path.GetDirectoryName(exePath);
            }
            catch (Exception) { }

            if (!string.IsNullOrEmpty(exeDir))
            {
                var byPath = FindKeyByAcadLocation(exeDir);
                if (!string.IsNullOrEmpty(byPath))
                    return byPath;
            }

            // ---- 2) 用 ACADVER 系统变量匹配版本分支 ----
            var byVer = FindKeyByAcadVer();
            if (!string.IsNullOrEmpty(byVer))
                return byVer;

            return null;
        }

        /// <summary>按 AcadLocation（acad.exe 所在目录）精确匹配产品键，返回 Applications\XIME 路径。</summary>
        private static string FindKeyByAcadLocation(string exeDir)
        {
            var target = NormalizePath(exeDir);

            foreach (var kv in EnumerateProductKeys())
            {
                try
                {
                    using (var productKey = Registry.CurrentUser.OpenSubKey(kv.Value))
                    {
                        if (productKey == null) continue;
                        var loc = productKey.GetValue("AcadLocation") as string;
                        if (string.IsNullOrEmpty(loc)) continue;

                        if (NormalizePath(loc) == target)
                            return kv.Value + @"\Applications\" + AppName;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>
        /// 用 ACADVER 系统变量（形如 "19.1s (LMS Tech)"）匹配注册表分支。
        ///
        /// ★ 关键：同一版本分支下往往存在**多个产品键**，必须挑选「真实产品键」。
        ///
        /// 实测（AutoCAD 2014 简体中文）：
        ///   R19.1\ACAD-D001         ← 空壳键：只有 Applications 子键，无任何值，
        ///                              AutoCAD **不会**读它里面的自动加载项
        ///   R19.1\ACAD-D001:804     ← 真实产品键：含 LocalRootFolder / RoamableRootFolder
        ///                              等全部值，AutoCAD 真正读取的就是它
        ///
        /// 旧实现只按分支名匹配并返回**第一个**命中项，于是误写进了空壳键，
        /// 表现为「注册成功但重启后不自动加载」。
        /// </summary>
        private static string FindKeyByAcadVer()
        {
            string acadVer = null;
            try
            {
                var o = Autodesk.AutoCAD.ApplicationServices.Application
                    .GetSystemVariable("ACADVER");
                if (o != null) acadVer = o.ToString();
            }
            catch (Exception) { }

            if (string.IsNullOrEmpty(acadVer)) return null;

            // 取形如 "19.1" 的数字前缀
            var m = System.Text.RegularExpressions.Regex.Match(acadVer, @"^\s*(\d+)\.(\d+)");
            if (!m.Success) return null;
            var branch = "R" + m.Groups[1].Value + "." + m.Groups[2].Value;

            string fallback = null;
            foreach (var kv in EnumerateProductKeys())
            {
                if (!string.Equals(kv.Key, branch, StringComparison.OrdinalIgnoreCase))
                    continue;

                var appKey = kv.Value + @"\Applications\" + AppName;

                // 优先返回「真实产品键」
                if (IsRealProductKey(kv.Value))
                    return appKey;

                if (fallback == null) fallback = appKey;
            }
            return fallback;
        }

        /// <summary>
        /// 判断产品键是否为「真实产品键」（即 AutoCAD 实际读取自动加载项的那个）。
        ///
        /// 判据（命中任一即认为是真产品键）：
        ///   ① 键名含语言后缀，如 "ACAD-D001:804"（":804" = 简体中文）；
        ///   ② 该键下存在 LocalRootFolder / RoamableRootFolder 等产品属性值；
        ///   ③ 其 Applications 下存在 AutoCAD 自带的 "AcadAppload"（命令行加载器）。
        ///
        /// 反例（空壳键）：R19.1\ACAD-D001 —— 无语言后缀、无属性值、
        /// Applications 下只有第三方写入的项。
        /// </summary>
        private static bool IsRealProductKey(string productKeyPath)
        {
            if (string.IsNullOrEmpty(productKeyPath)) return false;
            try
            {
                // ① 键名含 ":" 语言后缀
                int slash = productKeyPath.LastIndexOf('\\');
                var name = slash >= 0 ? productKeyPath.Substring(slash + 1) : productKeyPath;
                if (name.IndexOf(':') >= 0) return true;

                using (var k = Registry.CurrentUser.OpenSubKey(productKeyPath))
                {
                    if (k == null) return false;

                    // ② 产品属性值
                    if (k.GetValue("LocalRootFolder") != null) return true;
                    if (k.GetValue("RoamableRootFolder") != null) return true;
                    if (k.GetValue("AcadLocation") != null) return true;

                    // ③ Applications 下有 AcadAppload
                    using (var apps = k.OpenSubKey("Applications"))
                    {
                        if (apps != null)
                        {
                            using (var appload = apps.OpenSubKey("AcadAppload"))
                            {
                                if (appload != null) return true;
                            }
                            using (var appload2 = apps.OpenSubKey("AcadAppload.arx"))
                            {
                                if (appload2 != null) return true;
                            }
                        }
                    }
                }
                return false;
            }
            catch (Exception) { return false; }
        }

        /// <summary>规范化路径：去首尾空白、去尾部分隔符、转小写，便于比较。</summary>
        private static string NormalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return string.Empty;
            var s = p.Trim().TrimEnd('\\', '/');
            return s.ToLowerInvariant();
        }

        /// <summary>
        /// 枚举所有 AutoCAD 产品键。
        /// 返回 (版本分支, 产品键完整路径)，例如 ("R19.1", @"Software\Autodesk\AutoCAD\R19.1\ACAD-D001:804")。
        /// </summary>
        private static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
            EnumerateProductKeys()
        {
            var result = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();

            var versions = new[]
            {
                "R18.0", "R18.1", "R18.2",
                "R19.0", "R19.1",
                "R20.0", "R20.1",
                "R21.0",
                "R22.0",
                "R23.0", "R23.1",
                "R24.0", "R24.1", "R24.2", "R24.3",
                "R25.0", "R25.1", "R25.2"
            };

            using (var acadRoot = Registry.CurrentUser.OpenSubKey(RegBase))
            {
                if (acadRoot == null) return result;

                foreach (var ver in versions)
                {
                    using (var verKey = acadRoot.OpenSubKey(ver))
                    {
                        if (verKey == null) continue;
                        foreach (var productName in verKey.GetSubKeyNames())
                        {
                            var full = string.Format(@"{0}\{1}\{2}", RegBase, ver, productName);
                            using (var productKey = verKey.OpenSubKey(productName))
                            {
                                if (productKey == null) continue;
                                using (var appsKey = productKey.OpenSubKey("Applications"))
                                {
                                    if (appsKey == null) continue;
                                    result.Add(new System.Collections.Generic.KeyValuePair<string, string>(ver, full));
                                }
                            }
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 移除本插件的自动加载注册项 —— **只移除「当前正在运行的这一个 AutoCAD 版本」**。
        ///
        /// ★★ 与 <see cref="IsAutoLoadRegistered"/> / <see cref="RegisterAutoLoad"/> 完全对称：
        ///   三者都先走 <see cref="FindCurrentVersionApplicationKey"/> 定位"当前版本"，
        ///   **绝不遍历 R18.0~R25.2 所有分支**。
        ///
        /// 为什么必须只删当前版本（用户实测反馈，2026-10-06）：
        ///   本机同时装了 AutoCAD 2014 与 2022，两套都注册了自动加载。
        ///   旧实现无条件遍历**所有版本**逐个删 —— 在 2022 里点一次「卸载」，
        ///   会把 2014 的注册项一并干掉。用户根本没碰 2014，重启后却发现它不再自动加载，
        ///   而且**没有任何提示**（注册表项静默消失）。
        ///   「卸载」的语义是"这个版本里我不要这个插件了"，
        ///   不是"把本机所有 CAD 都清一遍"。
        ///
        /// 同版本分支内的多个产品键（真实产品键 + 空壳键）**都删** ——
        ///   历史版本曾把注册项误写进空壳键，只删一个会留下"看起来卸了、其实还在"的残留。
        ///
        /// 定位不到当前版本时**一个键都不删**并返回 false：
        ///   宁可这次没卸干净（用户可再点一次 / 用 XIMEREG 看定位到了哪个键），
        ///   也绝不"猜一个删掉" —— 猜错就是别的 CAD 版本的加载没了。
        /// </summary>
        /// <returns>确实删掉了至少一个 XIME 键 → true。</returns>
        public static bool RemoveAutoLoadRegistration()
        {
            var current = FindCurrentVersionApplicationKey();
            if (string.IsNullOrEmpty(current))
                return false;

            bool removed = false;

            foreach (var kv in EnumerateProductKeys())
            {
                var appKey = kv.Value + @"\Applications\" + AppName;
                if (!ShouldRemoveOnUninstall(appKey, current))
                    continue;                       // 不是当前版本 → 一律保留
                if (DeleteAppKey(kv.Value))
                    removed = true;
            }

            // 兜底：定位到的键可能没被 EnumerateProductKeys 枚举出来
            //（该产品键下 Applications 子键恰好不存在）—— 仍然删一次它自己。
            if (DeleteAppKey(ProductPathOfAppKey(current)))
                removed = true;

            return removed;
        }

        /// <summary>
        /// 「卸载」时该不该删这一个 XIME 注册键 —— 纯判据，自测钉死。
        /// 规则见 <see cref="Xime.Core.RegPath.ShouldRemoveOnUninstall"/>。
        ///
        /// ★ 之所以抽成纯函数：这条契约的失效形式很隐蔽 ——
        ///   删多了不报错、不写日志，用户只在"另一个 CAD 重启后不自动加载"时才发现，
        ///   而那时离"他点过卸载"已经很远，根本连不起来。
        /// </summary>
        /// <param name="appKeyPath">待判定的键，形如 ...\R19.1\ACAD-D001:804\Applications\XIME。</param>
        /// <param name="currentAppKeyPath">当前运行版本的键（FindCurrentVersionApplicationKey 的结果）。</param>
        internal static bool ShouldRemoveOnUninstall(string appKeyPath, string currentAppKeyPath)
        {
            return Xime.Core.RegPath.ShouldRemoveOnUninstall(appKeyPath, currentAppKeyPath);
        }

        /// <summary>取 ...\产品键\Applications\XIME 里的「产品键」路径；解析不出来返回原值。</summary>
        private static string ProductPathOfAppKey(string appKeyPath)
        {
            return Xime.Core.RegPath.ProductPathOfAppKey(appKeyPath);
        }

        /// <summary>
        /// 判断**当前 AutoCAD 版本**是否已注册自动加载。
        /// 只查当前版本，与 <see cref="RegisterAutoLoad"/> 对称。
        /// </summary>
        public static bool IsAutoLoadRegistered()
        {
            var current = FindCurrentVersionApplicationKey();
            if (string.IsNullOrEmpty(current)) return false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(current))
                {
                    return key != null && key.GetValue("LOADER") != null;
                }
            }
            catch (Exception) { return false; }
        }

        // ★ 已删除 `EnumerateAutoCadApplicationKeys()`（v1.0.2 起）：
        //   它是"遍历 R18.0~R25.2 所有版本"的旧卸载实现留下的唯一使用者，
        //   而"按版本隔离"定了之后，注册/查询/卸载三处都必须只用
        //   FindCurrentVersionApplicationKey() 定位当前版本 ——
        //   留着一个"能枚举全部版本"的私有方法，就是给下一次误用埋雷。
    }
}
