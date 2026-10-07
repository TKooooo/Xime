using System;

namespace Xime.Core
{
    /// <summary>
    /// AutoCAD 自动加载注册表路径的**纯字符串判据**（只算字符串，不碰注册表）。
    ///
    /// ★ 为什么要单独抽出来（v1.0.2）：
    ///   "哪些注册表键算同一个 CAD 版本" 是「按版本隔离」这条设计的**唯一定义** ——
    ///   注册、查询、卸载三处都要用。若各处自己数斜杠、自己切字符串，
    ///   迟早漂移，而漂移的后果是**静默的**：
    ///     · 卸载时多删 → 用户另一个 CAD 版本重启后不自动加载（没有任何报错）；
    ///     · 注册时误判 → 项写进了别的版本分支。
    ///   两者都要等用户下次重启 CAD 才暴露，那时离"点过卸载"已经很远。
    ///
    /// ★ 放在 Xime.Core 而不是 Xime.Acad：Core 不依赖 AutoCAD 程序集，
    ///   因此自测工具（XimeTest）能直接跑断言把它钉死 —— 纯逻辑必须可执行验证。
    ///
    /// 路径形状（实测）：
    ///   版本分支  HKCU\Software\Autodesk\AutoCAD\R19.1
    ///   产品键                ...\R19.1\ACAD-D001:804        ← 真实产品键（带 :804）
    ///   自动加载项              ...\ACAD-D001:804\Applications\XIME
    /// </summary>
    public static class RegPath
    {
        private const string ApplicationsSegment = @"\Applications\";

        /// <summary>
        /// 取 <c>...\Rxx.x\产品键\Applications\XIME</c> 所属的**版本分支**路径，
        /// 形如 <c>Software\Autodesk\AutoCAD\R19.1</c>；解析不出来返回空串。
        /// </summary>
        public static string VersionPathOf(string appKeyPath)
        {
            if (string.IsNullOrEmpty(appKeyPath)) return string.Empty;

            var productKey = ProductPathOfAppKey(appKeyPath);
            if (productKey.Length == 0) return string.Empty;

            var slash = productKey.LastIndexOf('\\');
            return slash > 0 ? productKey.Substring(0, slash) : string.Empty;
        }

        /// <summary>
        /// 取 <c>...\产品键\Applications\XIME</c> 里的「产品键」路径，
        /// 形如 <c>Software\Autodesk\AutoCAD\R19.1\ACAD-D001:804</c>。
        /// 找不到 <c>\Applications\</c> 段时原样返回（调用方再判一次长度即可）。
        /// </summary>
        public static string ProductPathOfAppKey(string appKeyPath)
        {
            if (string.IsNullOrEmpty(appKeyPath)) return string.Empty;

            var idx = appKeyPath.LastIndexOf(ApplicationsSegment, StringComparison.OrdinalIgnoreCase);
            return idx > 0 ? appKeyPath.Substring(0, idx) : appKeyPath;
        }

        /// <summary>
        /// 「面板点卸载」时该不该删这一个 XIME 注册键。
        ///
        /// 判据只有一条：**它与"当前运行版本"属于同一个版本分支**（<c>...\Rxx.x</c>）。
        ///   · 同一分支（含同一分支下的空壳键）→ 删；
        ///   · 其它分支（R18.x / R24.1 / ...）→ **一律保留**。
        ///
        /// ★ 用户实测反馈（2026-10-06）：
        ///   本机同时装了 AutoCAD 2014 与 2022，两套都注册了自动加载。
        ///   旧实现无条件遍历**所有版本**逐个删 —— 在 2022 里点一次「卸载」，
        ///   把 2014 的项也干掉了。用户根本没碰 2014，重启后才发现它不自动加载。
        ///   「卸载」的语义是"这个版本里我不要这个插件了"，
        ///   不是"把本机所有 CAD 都清一遍"。
        /// </summary>
        /// <param name="appKeyPath">待判定的键，形如 ...\R19.1\ACAD-D001:804\Applications\XIME。</param>
        /// <param name="currentAppKeyPath">当前运行版本的键（FindCurrentVersionApplicationKey 的结果）。</param>
        public static bool ShouldRemoveOnUninstall(string appKeyPath, string currentAppKeyPath)
        {
            if (string.IsNullOrEmpty(appKeyPath) || string.IsNullOrEmpty(currentAppKeyPath))
                return false;

            var a = VersionPathOf(appKeyPath);
            var b = VersionPathOf(currentAppKeyPath);
            if (a.Length == 0 || b.Length == 0) return false;

            // 定位不到当前版本时**一个键都不删**：宁可这次没卸干净
            //（用户可以再点一次 / 用 XIMEREG 看定位到了哪个键），
            // 也绝不"猜一个删掉" —— 猜错就是别的 CAD 版本的加载没了。
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
