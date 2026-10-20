using System;

namespace Xime.Core
{
    /// <summary>
    /// AutoCAD 版实现 —— **纯转发**，把 <see cref="ITextFrameProvider"/> 的调用
    /// 原样交给既有的 <see cref="AcadTextFrame"/>。
    ///
    /// ★ 为什么用转发而不是把 <see cref="AcadTextFrame"/> 改造成实例类：
    ///   那份实现是经过两次现场事故反复打磨出来的 ——
    ///     · v35：双击文字进在位编辑时焦点在命令栏 / 在位编辑器 / 绘图区之间抖动；
    ///     · v36：绘图区窗口类名带 `Afx:`，被弱特征误命中并缓存 3 秒，
    ///            导致"投递目标错 + IME 靶点错 → 中文永远切不过去"。
    ///   它的每一条判据背后都有日志证据。改造它 = 把"已验证"退回"未验证"。
    ///   转发层只有 14 个方法、零逻辑，是这里唯一不引入风险的做法。
    /// </summary>
    internal sealed class AcadTextFrameProvider : ITextFrameProvider
    {
        public IntPtr Resolve() { return AcadTextFrame.Resolve(); }

        public void Invalidate() { AcadTextFrame.Invalidate(); }

        public bool IsTextWindow(IntPtr hwnd) { return AcadTextFrame.IsTextWindow(hwnd); }

        public bool IsCommandLineWindow(IntPtr hwnd) { return AcadTextFrame.IsCommandLineWindow(hwnd); }

        public bool IsVisible(IntPtr hwnd) { return AcadTextFrame.IsVisible(hwnd); }

        public int WidthOf(IntPtr hwnd) { return AcadTextFrame.WidthOf(hwnd); }

        public string DumpTree(IntPtr root, int maxDepth) { return AcadTextFrame.DumpTree(root, maxDepth); }

        public bool IsEditLike(IntPtr hwnd) { return AcadTextFrame.IsEditLike(hwnd); }

        public int MatchStrength(IntPtr hwnd) { return AcadTextFrame.MatchStrength(hwnd); }

        public int MatchStrength(string cls) { return AcadTextFrame.MatchStrength(cls); }

        public bool IsTextInputHost(IntPtr hwnd) { return AcadTextFrame.IsTextInputHost(hwnd); }

        public bool IsTextInputHostClass(string cls) { return AcadTextFrame.IsTextInputHostClass(cls); }

        public string GetClassNameSafe(IntPtr hwnd) { return AcadTextFrame.GetClassNameSafe(hwnd); }

        public string GetWindowTextSafe(IntPtr hwnd) { return AcadTextFrame.GetWindowTextSafe(hwnd); }
    }
}
