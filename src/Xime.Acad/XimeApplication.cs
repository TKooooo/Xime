using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Xime.Core;
using Xime.Core.Native;

// AutoCAD 插件入口程序集。一个 DLL 同时承载：
//   - IExtensionApplication（自动加载、命令注册）
//   - 命令 xime / XIME-* 系列
[assembly: ExtensionApplication(typeof(Xime.Acad.XimeApplication))]
[assembly: CommandClass(typeof(Xime.Acad.XimeCommands))]

namespace Xime.Acad
{
    /// <summary>
    /// X输入法 AutoCAD 插件入口。
    ///
    /// 生命周期：
    ///   Initialize() -> NETLOAD 加载或 AutoCAD 启动自动加载时调用；
    ///   Terminate()  -> AutoCAD 关闭时调用。
    /// </summary>
    public sealed class XimeApplication : IExtensionApplication
    {
        private static XimeEngine _engine;
        private static XimeConfig _config;

        /// <summary>全局引擎实例（供命令层访问）。</summary>
        internal static XimeEngine Engine { get { return _engine; } }
        internal static XimeConfig Config { get { return _config; } }

        public void Initialize()
        {
            try
            {
                WriteStartupLog("==== 加载 X输入法 v" + Version + " ====");

                // ★ 先查"是不是已经有别的版本在跑"——这是「换了新版却没变化」
                //   的头号原因，必须在做任何事之前就告诉用户。
                var duplicateWarning = DetectDuplicateLoads();

                // ★★ 幂等化：同一个 AutoCAD 会话里 NETLOAD **同一份** DLL 两次，
                //    .NET 只会加载一次程序集，但 AutoCAD 会**再次调用 Initialize()**。
                //    若这里直接 new 一个新的引擎，上一个引擎实例就永远失去了引用 ——
                //    它装的键盘钩子从此**再也无法卸载**，于是两个钩子同时吞键并注入：
                //      · 命令栏字符重复（`lin` → `llliiinn`）或丢字；
                //      · 升级到新版本后"毛病一点没变"（旧钩子还在跑）；
                //      · 从"切换输入法模式"换回"无视输入法模式"后，旧引擎仍在
                //        后台切输入法 —— 这正是用户反馈的"无视输入法模式也失效了"。
                //    处理很简单：先把上一个实例彻底停掉（Stop 内部会按它当初的模式
                //    收尾并卸载钩子），再造新的。
                if (_engine != null)
                {
                    WriteStartupLog("  ⚠ 重复加载：先停用上一次的引擎实例"
                                    + "（否则键盘钩子会叠加，按键会重复或丢失）");
                    try { _engine.Stop(); _engine.Dispose(); }
                    catch (System.Exception) { }
                    _engine = null;
                }

                var dir = PluginPaths.PluginDirectory;
                var cfgPath = XimeConfig.GetDefaultConfigPath(dir);
                bool cfgExists = System.IO.File.Exists(cfgPath);
                _config = XimeConfig.Load(cfgPath);

                // 落盘条件（保证「加载后默认启动」在每次启动都稳定成立）：
                //   ① 首次运行（无配置文件）—— 建立一份默认配置（Enabled=true）；
                //   ② 配置里 EnabledByUser=false —— 归一化：
                //      此时加载结果恒为「启用」，把它写回文件即可让文件与
                //      运行时状态一致，后续每次加载都不再依赖历史遗留的
                //      <Enabled>false</Enabled>（那是最初让用户困惑的根源）。
                //      用户一旦手动关过（EnabledByUser 变 true），这里立即不再改写，
                //      其选择被永久尊重。
                if (!cfgExists || !_config.EnabledByUser)
                {
                    try { _config.Save(); } catch (System.Exception) { }
                }

                var mainHwnd = GetAcadMainWindow();
                _engine = new XimeEngine(mainHwnd, _config);

                // ★★★ v45：把引擎的「关键事件」接到**始终记录**的启动日志上。
                //   动机（本轮实机教训）：用户复现故障时常常忘了先开 XIMEDEBUG，
                //   一整轮测试一行日志都没有；而"输入法切换失败 / 我们把它留在了英文 /
                //   还原成功还是失败 / 用户双击文字时到底有没有发 Shift"这几件事，
                //   恰恰是判定"打不出中文"的全部依据。
                //   只放行低频事件（失败、证据、还原、接管），一次会话通常几行到几十行，
                //   不会把 256KB 的启动日志冲掉。
                _engine.KeyEventSink = WriteStartupLog;

                // 绑定键盘钩子的 UI 线程锚点 —— 保证全局键盘钩子在有消息循环的
                // UI 线程上安装，避免系统输入卡顿或钩子失效。
                _engine.HookUiAnchor = GetUiAnchor(mainHwnd);

                _engine.Start();

                // 订阅文档事件，切换文档时刷新窗口句柄与缓存
                SubscribeDocEvents();

                // ★★★ v6.3：**加载完成立刻挂接命令事件**（必须排在引擎就绪之后）。
                //
                //   不挂的后果（2026-10-07 现场日志钉死）：NETLOAD 后、用户还没敲过
                //   任何一条本插件命令之前，`CommandWillStart` 是**没挂上**的 ⇒
                //   引擎收不到"命令开始" ⇒ 整个「命令期间无视输入法」在
                //   **开机后的第一条命令**上完全不工作 ⇒ 天正二级命令失灵、
                //   命令栏留小写字母、输入法弹候选窗。
                //   而用户只要先敲一次 `XIMESTATUS`（命令体第一行就是 EnsureHooked）
                //   就恢复正常 —— 这正是用户报的现象：
                //     「总不能每次开机我都先执行一遍 XIMESTATUS 命令吧，这也太愚蠢了。」
                //
                //   ★ 注意：要在 `_engine.Start()` 之后（EnsureHooked 会检查 Engine 非空），
                //     并且单独 try —— 挂事件失败绝不能连累后面的"已加载"提示与
                //     环境日志（否则用户看到的是"插件加载失败"，实际只是事件没挂上）。
                try { XimeCommands.EnsureHooked(); }
                catch (System.Exception) { }

                // 注册自动加载（幂等，已注册则跳过）
                TryRegisterAutoLoad();

                WriteStartupLog(string.Format(
                    "  插件目录: {0}", dir));
                WriteStartupLog(string.Format(
                    "  配置: Enabled={0} EnabledByUser={1} WorkMode={2} 配置已存在={3} 命令期间按键={4}",
                    _config.Enabled, _config.EnabledByUser, _config.WorkMode, cfgExists,
                    _config.CommandKeyMode));

                // ★★★ v55：**Core 程序集实际来源自检** —— 这是"换了新版却没变化"的头号陷阱。
                //   机理：Xime.dll 与 Xime.Core.dll 的 AssemblyVersion **恒为 1.0.0.0**，
                //   而同名同版本的程序集在同一个 AppDomain 里**只会加载一份**。
                //   若先用 C:\X输入法\net48\ 的旧 Core 跑过一轮（或自动加载先触发），
                //   之后再 NETLOAD 桌面新版目录的 Xime.dll 时，CLR 发现"1.0.0.0 的
                //   Xime.Core 已经在" → **直接复用旧的那份**，新 Core 永远不生效。
                //   表现为：命令是新的（Xime.dll 换了），但修复完全没上（Core 是旧的）。
                //   ⇒ 每次加载都记下 Core 的**真实路径与写盘时间**，一眼可辨。
                LogCoreAssemblyProvenance();

                // 加载后：已启用则静默运行；用户曾主动关闭过则明确提示 ——
                // 关闭状态是**持久**的，不说清楚很容易被误判成「插件没加载 / 自动加载失败」。
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage(
                        "\n[X输入法 v" + Version + "] 已加载。" +
                        (Config.Enabled
                            // ★ v42：这里原来把模式**写死**成"无视输入法模式"，
                            //   用户在切换输入法模式下重新加载会看到与实际不符的提示，
                            //   正好掩盖了"重新加载到底回到哪个模式"这个关键事实。
                            //   现在如实报出当前模式 —— 排查问题时一眼可验。
                            ? "已自动启用（" + XimeCommands.ModeText(Config.WorkMode) + "），命令：xime 打开设置界面。"
                            : "⚠ 当前为【关闭】状态（你之前在界面里手动关闭过），"
                            + "插件不会工作。执行 xime 打开界面并启用，或执行 XIMEON 直接启用。"));

                    // 多版本并存必须显式告知：否则用户会以为"新版没生效"
                    if (duplicateWarning != null)
                        doc.Editor.WriteMessage(duplicateWarning);
                }

                // 加载时命令行**只保留上面这一行**提示；
                // 环境信息、自动加载注册结果等一律只写启动日志，避免刷屏。
                LogEnvironment();

                // ★ v43：再记一次"主机窗口词汇表"。
                //   靶点识别全靠窗口类名，而类名随 AutoCAD 版本/插件宿主而变
                //   （2014 主框架 AfxMDIFrame100u、2022 AfxMDIFrame140u，天正再套一层）。
                //   换宿主后的第一次排查，有这份记录才能判断"读数是不可信"还是"靶点压根没找到"。
                //   全部包在 try 里：诊断代码绝不允许影响加载。
                try
                {
                    if (_engine != null)
                        foreach (var line in _engine.BuildHostSurveyLines())
                            WriteStartupLog(line);
                }
                catch (System.Exception) { }
            }
            catch (System.Exception ex)
            {
                SafeWriteMessage("\n[X输入法] 初始化失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 检测「同一个 AutoCAD 会话里同时加载了多个版本的 X输入法」。
        ///
        /// ★ 为什么必须主动检查：
        ///   AutoCAD / .NET 都**无法卸载**已加载的程序集。若本次会话里已经先由
        ///   自动加载（注册表项还指向上一版目录）或手工 NETLOAD 载入过旧版本，
        ///   之后再 NETLOAD 新版本，两个版本会**同时**存在：
        ///     · 旧版本装的键盘钩子仍在跑 → 症状就是用户说的
        ///       「我换了新版，怎么毛病一点没变」；
        ///     · 两个钩子叠加 → 严重时出现重复处理、按键丢失。
        ///   而命令行只会显示「已加载」，完全看不出底下压着几个版本 ——
        ///   所以这里直接把目录列出来，让"到底在跑哪一份"变得可验证。
        ///
        /// <summary>
        /// ★★★ v55：把 **Xime.Core.dll 的真实来源**（路径 + 写盘时间 + 文件大小）写进启动日志。
        ///
        ///   为什么必须记（本陷阱已实际发生过）：
        ///     `Xime.dll` 与 `Xime.Core.dll` 的 <c>AssemblyVersion</c> **恒为 1.0.0.0**，
        ///     而 CLR 对**同名同版本**程序集在同一个 AppDomain 里**只加载一份**。
        ///     于是「先跑过 C:\X输入法\net48\ 的旧 Core，再 NETLOAD 桌面新版目录」
        ///     这种操作下，新 Core **永远不会被加载** —— 而 CLR 眼里新旧**没有任何区别**：
        ///     <c>AssemblyVersion</c> 恒为 1.0.0.0，它就是程序集身份本身。
        ///     （★ 对外显示的版本号取自 <c>FileVersion</c>，那个是会随发版变化的；
        ///        真正"分不出来"的是程序集身份 —— 所以文件版本变了也救不了这种情况，
        ///        必须靠这份"Core 到底从哪个路径加载"的日志。）
        ///     用户看到的是"装了新版却一点变化没有"。
        ///
        ///   本方法只读、无副作用；纯诊断，任何异常都不影响插件。
        /// </summary>
        private static void LogCoreAssemblyProvenance()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("  Core 程序集来源: ");
                bool found = false;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name;
                    try { name = asm.GetName().Name; }
                    catch (System.Exception) { continue; }
                    if (name != "Xime" && name != "Xime.Core") continue;

                    found = true;
                    string loc = null;
                    try { loc = asm.Location; } catch (System.Exception) { }

                    sb.Append("\n    · ").Append(name).Append(".dll → ");
                    if (string.IsNullOrEmpty(loc))
                    {
                        sb.Append("(无路径：动态程序集)");
                        continue;
                    }
                    sb.Append(loc);

                    try
                    {
                        var fi = new System.IO.FileInfo(loc);
                        if (fi.Exists)
                            sb.Append("  [").Append(fi.Length).Append("B, 写盘 ")
                              .Append(fi.LastWriteTime.ToString("MM-dd HH:mm:ss")).Append("]");
                    }
                    catch (System.Exception) { }
                }

                if (found) WriteStartupLog(sb.ToString());
            }
            catch (System.Exception)
            {
                // 纯诊断，绝不因它影响加载
            }
        }

        /// <summary>
        /// 返回给用户看的告警文案（只有一个版本时返回 null，不产生任何输出）。
        /// </summary>
        private static string DetectDuplicateLoads()
        {
            try
            {
                var folders = new System.Collections.Generic.List<string>();

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name;
                    try { name = asm.GetName().Name; }
                    catch (System.Exception) { continue; }

                    // 只关心本插件的两个程序集（Xime.dll 是插件层，Xime.Core.dll 是核心）
                    if (name != "Xime" && name != "Xime.Core") continue;

                    string loc = null;
                    try { loc = asm.Location; }
                    catch (System.Exception) { continue; }
                    if (string.IsNullOrEmpty(loc)) continue;   // 动态程序集没有位置

                    var folder = System.IO.Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(folder) && !folders.Contains(folder))
                        folders.Add(folder);
                }

                if (folders.Count <= 1) return null;

                WriteStartupLog("★ 检测到 " + folders.Count + " 个版本的 X输入法 目录同时加载：");
                foreach (var f in folders) WriteStartupLog("    · " + f);

                return "\n[X输入法] ⚠ 检测到本次会话里同时加载了 " + folders.Count
                       + " 个版本的 X输入法（.NET 无法卸载已加载的程序集）：\n          "
                       + string.Join("\n          ", folders.ToArray())
                       + "\n          旧版本仍会继续拦截按键 —— 这会让「换了新版却没变化」看起来像插件没生效。"
                       + "\n          处理：① 执行 xime 打开界面点「卸载 X输入法」（清掉全部自动加载项）；"
                       + "\n                ② 完全退出 AutoCAD；"
                       + "\n                ③ 重开后只 NETLOAD 新版本目录里的 Xime.dll。";
            }
            catch (System.Exception)
            {
                // 纯诊断功能，任何异常都不影响插件本身
            }
            return null;
        }

        /// <summary>
        /// 写入启动日志（%TEMP%\xime_startup.log，追加）。
        ///
        /// 与 XIMEDEBUG 的引擎日志不同：本日志**始终记录**，无需任何调试开关。
        /// 用途：重启 AutoCAD 后据此确认
        ///   ① 插件是否真的被「自动加载」（看有没有新的一行时间戳）；
        ///   ② 自动加载注册到了哪个版本；
        ///   ③ 加载时的启用状态（Enabled=false 会导致插件加载但不工作）。
        ///
        /// ★ v43：由 <c>private</c> 改 <c>internal</c>，让命令层也能记关键事件 ——
        ///   目前有两处：<c>XIMEDEBUG</c> 的开关动作、面板「卸载」的配置前后快照。
        ///   这两件事必须落在**始终记录**的这份日志里（引擎日志要开开关才有，
        ///   而"用户忘了开开关"正是本工具最初误判的原因）。
        /// </summary>
        /// <summary>
        /// ★ v45：进程内串行化 —— 本方法现在会被**引擎线程**调用
        ///   （KeyEventSink：切换失败 / 证据记下与撤销 / 还原结果 / 接管切换）。
        ///   `File.AppendAllText` 以 FileShare.Read 打开文件，并发调用会抛
        ///   IOException 并被静默吞掉 —— 那正好又落回"日志丢行"这个坑里。
        /// </summary>
        private static readonly object StartupLogSync = new object();

        internal static void WriteStartupLog(string message)
        {
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "xime_startup.log");

                lock (StartupLogSync)
                {
                    // 防止日志无限增长：超过约 256KB 时清空重写（只保留最近记录）
                    try
                    {
                        var fi = new System.IO.FileInfo(path);
                        if (fi.Exists && fi.Length > 256 * 1024)
                            System.IO.File.WriteAllText(path, string.Empty);
                    }
                    catch (System.Exception) { }

                    System.IO.File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n");
                }
            }
            catch (System.Exception) { }
        }

        public void Terminate()
        {
            try
            {
                UnsubscribeDocEvents();

                if (_engine != null)
                {
                    _engine.Stop();     // 内部会恢复用户输入法状态
                    _engine.Dispose();
                    _engine = null;
                }
            }
            catch (System.Exception)
            {
                // 关闭阶段不再抛异常
            }
        }

        #region 文档事件

        /// <summary>
        /// 注册自动加载。NETLOAD 加载后写入注册表，下次启动自动加载。
        /// 若已注册则跳过，避免重复写入。
        /// </summary>
        private static void TryRegisterAutoLoad()
        {
            try
            {
                var dir = PluginPaths.PluginDirectory;
                var dllPath = System.IO.Path.Combine(dir, "Xime.dll");
                if (!System.IO.File.Exists(dllPath))
                {
                    // 可能被重命名，退回到当前程序集位置
                    var asm = Assembly.GetExecutingAssembly();
                    if (!string.IsNullOrEmpty(asm.Location))
                        dllPath = asm.Location;
                }

                WriteStartupLog("  DLL 路径: " + dllPath);

                // 记录当前版本识别情况（用于诊断「版本隔离」是否定位正确）
                try
                {
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    var exe = proc.MainModule != null ? proc.MainModule.FileName : "(未知)";
                    WriteStartupLog("  当前进程: " + exe);
                }
                catch (System.Exception) { }

                try
                {
                    var o = Application.GetSystemVariable("ACADVER");
                    WriteStartupLog("  ACADVER: " + (o != null ? o.ToString() : "(空)"));
                }
                catch (System.Exception) { }

                var target = UninstallHelper.FindCurrentVersionApplicationKey();
                WriteStartupLog("  目标注册表键: " + (target ?? "(未定位到当前版本)"));

                var written = UninstallHelper.RegisterAutoLoad(dllPath);
                if (written.Count > 0)
                {
                    // 注册成功是常态，只写启动日志 —— 命令行不出现任何提示，
                    // 避免每次启动刷出多余内容。
                    WriteStartupLog("  自动加载: 已注册 → " + string.Join(" | ", written.ToArray()));
                }
                else
                {
                    // 注册失败属于异常路径（正常使用不会出现），需要在命令行告知：
                    // 否则用户会疑惑「为什么下次启动没自动加载」。
                    WriteStartupLog("  自动加载: 注册失败（0 项）");
                    SafeWriteMessage("\n[X输入法] 未能写入自动加载项（可能权限不足）；本次已加载，下次需手动 NETLOAD。");
                }
            }
            catch (System.Exception ex)
            {
                WriteStartupLog("  自动加载: 异常 " + ex.Message);
                // 注册失败不影响本次使用
            }
        }

        private static void SubscribeDocEvents()
        {
            var dm = Application.DocumentManager;
            dm.DocumentCreated += OnDocumentCreated;
        }

        private static void UnsubscribeDocEvents()
        {
            try
            {
                var dm = Application.DocumentManager;
                dm.DocumentCreated -= OnDocumentCreated;
            }
            catch (System.Exception) { }
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            // 新文档建立后，刷新主窗口句柄与输入法缓存
            if (_engine == null) return;
            _engine.UpdateAcadWindow(GetAcadMainWindow());
            _engine.Ime.ResetCache();
        }

        #endregion

        /// <summary>
        /// 获取 AutoCAD 主窗口句柄。
        /// Application.MainWindow.Handle 在各版本均返回 IntPtr，直接使用即可。
        /// </summary>
        internal static IntPtr GetAcadMainWindow()
        {
            try
            {
                var mw = Application.MainWindow;
                if (mw != null)
                    return mw.Handle;
            }
            catch (System.Exception) { }

            // 回退：通过宿主进程主窗口
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
                    return proc.MainWindowHandle;
            }
            catch (System.Exception) { }

            return NativeMethods.GetForegroundWindow();
        }

        /// <summary>
        /// 获取用于安装全局键盘钩子的 UI 线程锚点。
        ///
        /// WH_KEYBOARD_LL 的回调在安装线程的消息队列中执行，因此必须选一个
        /// 确定拥有消息循环的 WinForms 控件。这里用 AutoCAD 主窗口句柄反查
        /// 对应的 WinForms 控件（AutoCAD 主窗体本身即基于 WinForms）。
        /// </summary>
        internal static System.Windows.Forms.Control GetUiAnchor(IntPtr mainHwnd)
        {
            try
            {
                if (mainHwnd != IntPtr.Zero)
                {
                    var ctrl = System.Windows.Forms.Control.FromHandle(mainHwnd);
                    if (ctrl != null)
                        return ctrl;
                }
            }
            catch (System.Exception) { }

            return null;
        }

        /// <summary>
        /// 插件版本号（对外显示用，形如 "1.0.2"）。
        /// 程序集里记录的是 1.0.2.0 四段式，这里去掉尾部多余的 ".0"，
        /// 避免界面上出现 "v1.0.2.0" 这种冗余写法。
        ///
        /// ★ 来源是 <c>AssemblyFileVersion</c>（csproj 的 &lt;FileVersion&gt;），
        ///   不是 <c>AssemblyVersion</c> —— 后者恒为 1.0.0.0（刻意，见 csproj 注释）。
        /// </summary>
        internal static string Version
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var raw = (string)null;
                    var attr = (AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(
                        asm, typeof(AssemblyFileVersionAttribute));
                    if (attr != null && !string.IsNullOrEmpty(attr.Version))
                        raw = attr.Version;
                    else
                        raw = asm.GetName().Version.ToString();
                    return TrimVersion(raw);
                }
                catch (System.Exception)
                {
                    // 取不到程序集特性时退回一个"至少不是空白"的占位；
                    // 正常路径永远走上面的 FileVersion（1.0.2.0 → "1.0.2"）。
                    return "1.0.2";
                }
            }
        }

        /// <summary>把 1.0.2.0 规范成 1.0.2（保留至少三段，去掉尾部多余的 .0）。</summary>
        private static string TrimVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return "1.0.2";
            var parts = v.Split('.');
            int keep = parts.Length;
            // 至少保留三段（主.次.修订），多于三段的尾部 0 全部去掉
            while (keep > 3 && parts[keep - 1] == "0")
                keep--;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < keep; i++)
            {
                if (i > 0) sb.Append('.');
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把运行环境信息（AutoCAD / Windows / CLR / 架构）写入启动日志。
        ///
        /// 这些信息只在排查问题时才有用，因此不往命令行输出 ——
        /// 加载时命令行只保留一行「已加载」提示。
        /// 需要即时查看可执行 XIMEDIAG。
        /// </summary>
        private static void LogEnvironment()
        {
            try
            {
                string cadVer = "未知";
                try { cadVer = Application.Version.ToString(); }
                catch (System.Exception) { }

                WriteStartupLog(string.Format(
                    "  环境: AutoCAD {0} | Windows {1} | CLR {2} | 架构 {3}",
                    cadVer,
                    System.Environment.OSVersion.Version,
                    System.Environment.Version,
                    Compat.ProcessArch));
            }
            catch (System.Exception) { }
        }

        internal static void SafeWriteMessage(string msg)
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null) doc.Editor.WriteMessage(msg);
            }
            catch (System.Exception) { }
        }
    }

    /// <summary>插件路径辅助。</summary>
    internal static class PluginPaths
    {
        private static string _cached;

        /// <summary>插件 DLL 所在目录。</summary>
        public static string PluginDirectory
        {
            get
            {
                if (_cached != null) return _cached;
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc))
                        _cached = System.IO.Path.GetDirectoryName(loc);
                }
                catch (System.Exception) { }

                if (string.IsNullOrEmpty(_cached))
                    _cached = System.IO.Directory.GetCurrentDirectory();
                return _cached;
            }
        }
    }
}
