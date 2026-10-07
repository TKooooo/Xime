using System;
using System.Diagnostics;
using System.IO;

namespace Xime.Core
{
    /// <summary>
    /// 小狼毫（Rime / Weasel）命令行桥 —— **直接设定**中英模式，而不是"盲按 Shift 翻转"。
    ///
    /// ★★★ 为什么需要它（2026-10-06 实机日志定案）：
    ///   小狼毫走 TSF，IMM32 读不回真实中英态（见铁律 13），于是"切到英文"只能
    ///   **盲按一次 Shift**。可实机证明这次盲按**根本没让输入法翻转**：
    ///     ForceEnglish → TapShift 返回 True、方式=RealKey → 121ms 后回读仍是 Chinese
    ///   而 `结果=True` 只代表 `SendInput` 成功，**不代表输入法响应**；更糟的是它还会
    ///   写下 `_switchApplied = English` 的**假记录**，此后每一次英文请求都被
    ///   「本模式已置于 English → 无需动作」短路 ⇒ 整个会话再也发不出一次切换，
    ///   用户按 A 就被中文输入法吃掉（现场截图：候选窗弹出 `a 啊 2 阿…`）。
    ///
    ///   ★ 这不是我们独有的问题：小狼毫官方 issue 明确指出
    ///     「通过按键（包含通过外部软件如 ahk 来发送的）来中英文经常会与使用自定义
    ///       映射按键或热键的人有冲突」。
    ///
    /// ★ 官方给的正路：自 **weasel 0.16.0** 起提供命令行开关
    ///     <c>WeaselServer.exe /ascii</c>   → 切到 ASCII（英文）模式
    ///     <c>WeaselServer.exe /nascii</c>  → 切回非 ASCII（中文）模式
    ///   源码依据：`WeaselServer.cpp` 的 `/ascii` → `ID_WEASELTRAY_ENABLE_ASCII`。
    ///   它是**幂等的设值**（不是翻转键），因此：
    ///     · 重复执行没有副作用 → 不存在"重复发一次就切反了"的风险；
    ///     · 不依赖"此刻焦点在哪个窗口"（盲按 Shift 恰恰依赖这个）；
    ///     · 不需要 IMM32 回读来确认，从根本上绕开"读不回来"的死结。
    ///
    /// 本类只做两件事：**找到 WeaselServer.exe**、**带参数启动它**。
    /// 任何失败（没装小狼毫 / 版本过旧 / 启动异常 / 超时）都返回 false，
    /// 由调用方退回原来的真实 Shift 通道 —— 绝不因为"桥不可用"让切换整体失效。
    /// </summary>
    internal static class WeaselBridge
    {
        private static readonly object _sync = new object();
        private static bool _probed;
        private static string _serverPath;        // null = 没找到（探测过一次就不再重复找）
        private static bool _found;               // 缓存判定结果

        /// <summary>常驻服务的进程名（不带 .exe）。</summary>
        private const string ServerProcessName = "WeaselServer";

        /// <summary>命令行桥是否可用（找到 WeaselServer.exe）。探测结果会缓存。</summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureProbed();
                return _found;
            }
        }

        /// <summary>已探测到的 WeaselServer.exe 完整路径（不可用时为 null）。</summary>
        public static string ServerPath
        {
            get
            {
                EnsureProbed();
                return _serverPath;
            }
        }

        /// <summary>
        /// 直接**设定**中英模式（幂等）。成功返回 true，并把可读的过程说明写进
        /// <paramref name="detail"/>（供引擎写日志）；失败返回 false（detail 说明原因）。
        /// </summary>
        /// <param name="target">English → <c>/ascii</c>；Chinese → <c>/nascii</c>。</param>
        public static bool TrySetAscii(ImeMode target, out string detail)
        {
            detail = null;

            EnsureProbed();
            if (!_found || string.IsNullOrEmpty(_serverPath))
            {
                detail = "未找到 WeaselServer.exe（小狼毫未安装或路径不标准）";
                return false;
            }

            // ★★★ 关键闸门：**服务没在跑时，/ascii 会静默地什么都不做，却照样返回 0**
            //   源码依据（WeaselServer/WeaselServer.cpp）：
            //     if (_wcsicmp(cmd, L"/ascii") == 0) {
            //       weasel::Client client;
            //       if (client.Connect()) { ...client.TrayCommand(...); }
            //       return 0;                 // ← 无条件 0，连不上也返回 0
            //     }
            //   ⇒ 若只认 rc==0，就会把"压根没连上服务"当成切换成功，
            //     进而写下 `_switchApplied` 的**假记录** —— 那正是 v55 失败的同一个坑
            //     （见 MEMORY 铁律 17）。所以这里必须先确认服务真的在跑。
            bool running = IsServerRunning();
            if (!ShouldAttemptCli(target, running))
            {
                detail = target != ImeMode.English && target != ImeMode.Chinese
                    ? "目标态不支持（" + target + "）"
                    : "WeaselServer 未在运行（此时 /ascii 会静默失败却仍返回 0）";
                return false;
            }

            string arg = target == ImeMode.English ? "/ascii" : "/nascii";
            try
            {
                var psi = new ProcessStartInfo(_serverPath, arg);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                // 不继承我们的环境（客户端只是去连已运行的服务，环境无意义）。
                psi.WorkingDirectory = Path.GetDirectoryName(_serverPath);

                var sw = Stopwatch.StartNew();
                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        detail = "Process.Start 返回 null";
                        return false;
                    }

                    // ★ 官方 issue 里有人反馈"执行后输入法四五秒没反应"（多半发生在
                    //   服务未运行时、客户端要把它拉起来）。这里给 3s 上限：
                    //   超时即放弃并退回真实 Shift，绝不让命令期间的切换卡住用户。
                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        detail = "超时（>3000ms），已放弃";
                        return false;
                    }

                    sw.Stop();
                    int rc = p.ExitCode;
                    detail = Path.GetFileName(_serverPath) + " " + arg
                             + " rc=" + rc + " 用时=" + sw.ElapsedMilliseconds + "ms";

                    // rc=0 = 客户端把托盘命令交给了服务（服务在跑这点前面已确认）。
                    // 它是设值而不是翻转，重复执行无害。
                    return rc == 0;
                }
            }
            catch (Exception ex)
            {
                detail = "启动异常：" + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 是否**允许**去执行命令行桥（纯判据，便于自测钉住）。
        ///
        /// 两条都必须成立：
        ///   · <paramref name="target"/> 是 English / Chinese（其余态没有对应开关）；
        ///   · <paramref name="serverRunning"/> 为真 —— **服务没跑时 /ascii 会静默失败却仍返回 0**，
        ///     只认 rc 就会写下假记录（v55 同一个坑）。
        /// </summary>
        internal static bool ShouldAttemptCli(ImeMode target, bool serverRunning)
        {
            if (target != ImeMode.English && target != ImeMode.Chinese) return false;
            return serverRunning;
        }

        /// <summary>WeaselServer 常驻服务是否在运行（<c>/ascii</c> 生效的前提）。</summary>
        /// <remarks>
        /// ★ 带 400ms 缓存：本方法会被"每次切换请求"调用**两次**（判据一次、TrySetAscii 内一次），
        ///   而 <c>Process.GetProcessesByName</c> 要枚举系统进程表，在进程多的机器上不便宜。
        ///   400ms 的陈旧度对"服务刚刚退出"这种事件完全够用 ——
        ///   服务不会在 0.4 秒里消失又出现。
        /// </remarks>
        public static bool IsServerRunning()
        {
            long now = Environment.TickCount;
            if (_serverChecked && unchecked(now - _serverCheckedTick) >= 0
                && unchecked(now - _serverCheckedTick) < ServerCheckTtlMs)
                return _serverRunning;

            bool running;
            try
            {
                var ps = Process.GetProcessesByName(ServerProcessName);
                running = ps != null && ps.Length > 0;
                if (ps != null)
                    for (int i = 0; i < ps.Length; i++) ps[i].Dispose();
            }
            catch (Exception)
            {
                // 查不出来 → 当作"没有"（宁可退回真实 Shift，也不写假记录）。
                running = false;
            }

            _serverRunning = running;
            _serverCheckedTick = now;
            _serverChecked = true;
            return running;
        }

        private const int ServerCheckTtlMs = 400;
        private static bool _serverChecked;
        private static bool _serverRunning;
        private static long _serverCheckedTick;

        private static void EnsureProbed()
        {
            if (_probed) return;
            lock (_sync)
            {
                if (_probed) return;
                _serverPath = FindServer();
                _found = !string.IsNullOrEmpty(_serverPath);
                _probed = true;
            }
        }

        /// <summary>
        /// 找 WeaselServer.exe。顺序：
        ///   ① 环境变量 <c>XIME_WEASEL_SERVER</c>（便于非常规安装的现场排查）；
        ///   ② <c>%ProgramFiles%\Rime\weasel-*\WeaselServer.exe</c>（取版本号最大的目录）；
        ///   ③ <c>%ProgramFiles(x86)%\Rime\weasel-*\WeaselServer.exe</c>；
        ///   ④ 同目录（用户可能把服务拷到插件旁边）。
        /// ★ 刻意**不**去扫 PATH / 全盘搜索：那既慢又可能命中来路不明的同名程序。
        /// </summary>
        private static string FindServer()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("XIME_WEASEL_SERVER");
                if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = GetProgramFilesX86();

                string hit = ScanRimeRoot(pf);
                if (hit != null) return hit;
                hit = ScanRimeRoot(pf86);
                if (hit != null) return hit;

                // 同目录兜底
                string local = Path.Combine(
                    Path.GetDirectoryName(typeof(WeaselBridge).Assembly.Location) ?? ".",
                    "WeaselServer.exe");
                if (File.Exists(local)) return local;
            }
            catch (Exception)
            {
                // 探测失败一律当作"没有" —— 上层会退回真实 Shift，不影响功能。
            }
            return null;
        }

        /// <summary>
        /// 32 位程序目录。★ `Environment.SpecialFolder.ProgramFilesX86` **是 .NET 4.0 才加的**
        /// （net35 里根本没有这个成员，直接写会 CS0117 编译失败）⇒ 统一走环境变量：
        /// 64 位 Windows 上 <c>ProgramFiles(x86)</c> 恒存在；32 位系统下该变量不存在，
        /// 退化为 <c>ProgramFiles</c>（32 位系统本来就只有这一个）。
        /// </summary>
        private static string GetProgramFilesX86()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch (Exception) { }
            return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        /// <summary>扫 &lt;root&gt;\Rime\weasel-*\WeaselServer.exe，取目录名最大（通常是最新）的那个。</summary>
        private static string ScanRimeRoot(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            try
            {
                string rimeDir = Path.Combine(root, "Rime");
                if (!Directory.Exists(rimeDir)) return null;

                string best = null;
                string bestName = null;
                foreach (var dir in Directory.GetDirectories(rimeDir, "weasel-*"))
                {
                    string exe = Path.Combine(dir, "WeaselServer.exe");
                    if (!File.Exists(exe)) continue;

                    string name = Path.GetFileName(dir);
                    // 字符串序对 "weasel-0.16.2" / "weasel-0.9.30" 并不严格等于版本序，
                    // 但在同一大版本下足够；真要精确应按 '点分段数值比较'。这里用分段比较。
                    if (bestName == null || CompareVersionDir(name, bestName) > 0)
                    {
                        bestName = name;
                        best = exe;
                    }
                }
                return best;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>按"版本号分段数值"比较两个 weasel-* 目录名（不认识的一律当 0）。</summary>
        private static int CompareVersionDir(string a, string b)
        {
            var pa = SplitVersion(a);
            var pb = SplitVersion(b);
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int va = i < pa.Length ? pa[i] : 0;
                int vb = i < pb.Length ? pb[i] : 0;
                if (va != vb) return va < vb ? -1 : 1;
            }
            return 0;
        }

        private static int[] SplitVersion(string dir)
        {
            int dash = dir.IndexOf('-');
            string tail = dash >= 0 ? dir.Substring(dash + 1) : dir;
            var parts = tail.Split('.');
            var nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int v;
                int.TryParse(parts[i], out v);   // 解析不出就是 0
                nums[i] = v;
            }
            return nums;
        }
    }
}
