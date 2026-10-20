using System;

namespace Xime.Plugin
{
    /// <summary>
    /// 跨目标框架的兼容辅助。
    /// net35 没有 Environment.Is64BitProcess，这里统一包装。
    /// </summary>
    internal static class Compat
    {
        /// <summary>进程架构描述。</summary>
        public static string ProcessArch
        {
            get { return IntPtr.Size == 8 ? "x64" : "x86"; }
        }

        /// <summary>
        /// 当前宿主名（写进启动日志 / 诊断输出用）。
        ///
        /// ★ 三种宿主共用同一份插件源码，靠编译符号区分；
        ///   日志里必须能一眼看出"这份 DLL 是给谁编的"，
        ///   否则现场排查时无法判断用户装的到底是不是对应宿主的包。
        /// </summary>
        public static string HostName
        {
            get
            {
#if HOST_ZWCAD
                return "中望CAD(ZWCAD)";
#elif HOST_GSTARCAD
                return "浩辰CAD(GstarCAD)";
#else
                return "AutoCAD";
#endif
            }
        }

        /// <summary>
        /// 宿主短名 —— 给**面向用户的整句文案**用（"完全退出 中望CAD 后重新 NETLOAD"）。
        ///
        /// ★ 为什么要和 <see cref="HostName"/> 分开：
        ///   日志/诊断里要能一眼认出具体宿主，所以带上括号里的英文名；
        ///   但塞进句子里读起来就啰嗦，而且中望/浩辰的用户也不认这一串。
        /// </summary>
        public static string HostShortName
        {
            get
            {
#if HOST_ZWCAD
                return "中望CAD";
#elif HOST_GSTARCAD
                return "浩辰CAD";
#else
                return "AutoCAD";
#endif
            }
        }
    }
}
