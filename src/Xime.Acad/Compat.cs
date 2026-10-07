using System;

namespace Xime.Acad
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
    }
}
