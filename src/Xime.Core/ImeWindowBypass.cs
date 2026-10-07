using System;
using System.Collections.Generic;
using Xime.Core.Native;

namespace Xime.Core
{
    /// <summary>
    /// 「让 CAD 窗口无视输入法」—— 把输入法上下文与 AutoCAD 的窗口**解除关联**。
    ///
    /// ================== 它解决的是哪一条死结 ==================
    ///
    /// 天正二级命令（RYBZ 的 A / S / D / E 选项）走 <c>acedGrRead</c> 一类
    /// **原始按键通道**，只认真实物理按键。由此得到两条**已被现场证伪**的事实：
    ///
    ///   · 四条合成通道（PostKey / PostKeyFrame / ReplayVk / SendInput-UNICODE）
    ///     天正**一律不认** —— 只会把字母打进命令栏；
    ///   · 唯一让它生效的档是「放行物理键」<c>PassThrough</c>。
    ///
    /// 可是物理键一放行，**用户自己的输入法也就看到了** ⇒ 中文态下弹候选窗。
    ///
    /// 旧结论把这里判成"结构性矛盾、无解"，是因为只考虑了两种手段：
    /// 「改输入法状态」（= 切换输入法模式，用户明确否决）和「吞键再合成」
    /// （天正不认）。**漏掉了第三种：什么都不改，只让窗口不要接收输入法。**
    ///
    /// 这正是 <see cref="NativeMethods.ImmAssociateContext"/> 的能力
    /// （微软 KB Q171154「HOWTO: Disable IME for a Particular Window」）：
    ///
    ///   ① 输入法的**状态**（中/英、键盘布局、候选词库、状态栏显示）**一个字节都不动**；
    ///   ② 被解除关联的窗口收不到输入法处理 ⇒ 物理键原样落到窗口消息队列
    ///      ⇒ 天正的 acedGrRead 拿到的是**真物理键**（依旧是 PassThrough 那条路）；
    ///   ③ 因此输入法**没有机会**为这些键开组合 / 弹候选窗。
    ///
    /// ⇒ 「天正认 + 不弹候选窗 + 命令栏干净」三件事同时成立，
    ///   而且**没有违背「无视输入法模式」的契约**（契约是"不动我的输入法状态"，
    ///   不是"不许让窗口忽略输入法"——后者正是这个模式名字的字面意思）。
    ///
    /// ================== 什么时候该启用（判据）==================
    ///
    ///   ① 只在「无视输入法模式」下 —— 「切换输入法模式」靠**真的**把输入法按在英文上
    ///      来解决同一个问题，那里再去解除关联反而会让"输入法显示为英文"这个
    ///      模式契约失去意义（也多余）。
    ///   ② 只在**命令执行期间** —— 空闲敲命令名时命令栏需要正常输入（含中文）。
    ///   ③ 只在**非文字类命令**（不在中文白名单里）—— 打标注文字时必须让输入法工作。
    ///   ④ 不在**文字编辑态** —— 双击文字改字时必须能打中文。
    ///   ⑤ **不碰本插件自己的命令** —— 诊断命令不许改变被诊断的状态。
    ///
    ///   六条全过才动手；任一条不成立就**确保已还原**（见 <see cref="Disengage"/>）。
    ///
    /// ================== 安全（绝不把用户卡在"打不了中文"）==================
    ///
    ///   · 解除关联时**保存**每个窗口原来的 HIMC，还原时原样写回 ⇒ **精确复原**；
    ///   · 引擎侧只有一个入口 <c>XimeEngine.SyncImeBypass</c>，
    ///     命令开始 / 结束 / 取消 / 进文字编辑态 / 出文字编辑态 / 离开 CAD /
    ///     停引擎 / 500ms 轮询**全部**会调它 ⇒ 不存在"只开不关"的路径；
    ///   · 配置项 <c>BypassImeDuringCommand</c> 可一键关掉整个机制（回到旧行为）。
    /// </summary>
    internal sealed class ImeWindowBypass
    {
        /// <summary>窗口 → 解除关联前它原本关联的输入法上下文（HIMC）。</summary>
        private readonly Dictionary<IntPtr, IntPtr> _saved = new Dictionary<IntPtr, IntPtr>();

        private bool _active;

        /// <summary>当前是否处于"已解除关联"状态。</summary>
        public bool IsActive { get { return _active; } }

        /// <summary>被解除关联的窗口数（= 原本**确实**关联着输入法的窗口数）。</summary>
        public int DetachedCount { get { return _saved.Count; } }

        /// <summary>最近一次解除关联时枚举到的窗口总数（诊断用，含本来就没有 IME 的窗口）。</summary>
        public int LastVisitedCount { get; private set; }

        /// <summary>
        /// 是否是**本插件自己的命令**（xime / XIMESTATUS / XIMEBYPASS / XIMEPROBE …）。
        ///
        /// ★★★ 为什么必须把它们排除（2026-10-06 现场实测踩到的坑）：
        ///   本机制挂在 **OnCommandWillStart** 上，于是 `XIMEBYPASS` 一执行，
        ///   它自己就把关联解除了 —— 结果：
        ///     · `XIMEBYPASS 1` 打印「已解除 122 个窗口（**枚举 0 个**）」——
        ///       因为进来时**已经**生效，强制解除走的是幂等早退分支，枚举数当然是 0；
        ///     · `XIMEBYPASS 0` 刚还原，命令一结束前脚后脚又被下一条命令重新解除，
        ///       那句「现在可以正常打中文了」**当场就是假的**；
        ///     · `XIMESTATUS` 想看真实状态，却因为"自己就是个命令"而永远报「当前生效=是」。
        ///   ⇒ **诊断命令不允许改变被诊断的状态**。这条和"诊断开关不许有副作用"是同一条规矩。
        ///
        /// 判据用**纯前缀** `xime`：插件的对外命令一律以它开头（见命令命名约定），
        /// 而 AutoCAD 自带命令与第三方命令都不会是这个名字。
        /// </summary>
        public static bool IsOwnPluginCommand(string commandName)
        {
            if (string.IsNullOrEmpty(commandName)) return false;
            // 命令行可能带前导下划线（国际化命令前缀）或空格，先去干净
            var s = commandName.Trim();
            while (s.StartsWith("_") ) s = s.Substring(1);
            return s.StartsWith("xime", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 是否该让 CAD 窗口无视输入法（纯判据，可自测）。
        ///
        /// 六条**全部**为真才返回 true；任何一条不成立都返回 false
        /// ⇒ 调用方据此**确保还原**（true→解除关联，false→还原）。
        /// </summary>
        /// <param name="switchOn">配置开关 <c>BypassImeDuringCommand</c>。</param>
        /// <param name="mode">当前工作模式。</param>
        /// <param name="engaged">引擎是否处于接管态。</param>
        /// <param name="hasActiveCommand">此刻是否有命令正在执行（_currentCommand != null）。</param>
        /// <param name="commandNeedsChinese">该命令是否是需要中文的文字类命令。</param>
        /// <param name="inTextEditMode">是否处于文字编辑态（焦点在编辑框）。</param>
        /// <param name="commandIsOwnPlugin">
        /// 当前命令是否属于本插件（见 <see cref="IsOwnPluginCommand"/>）——
        /// ★ 诊断命令不允许改变被诊断的状态。
        /// </param>
        public static bool ShouldEngage(
            bool switchOn,
            XimeWorkMode mode,
            bool engaged,
            bool hasActiveCommand,
            bool commandNeedsChinese,
            bool inTextEditMode,
            bool commandIsOwnPlugin)
        {
            // ① 配置开关（用户可一键关掉整个机制）
            if (!switchOn) return false;
            // ② 只管「无视输入法模式」；「切换输入法模式」用不着（它真的去切输入法）
            if (mode != XimeWorkMode.IgnoreIme) return false;
            // ③ 没接管就不动手
            if (!engaged) return false;
            // ④ 本插件自己的命令（含 XIMESTATUS / XIMEBYPASS）—— 不许自我干扰
            if (commandIsOwnPlugin) return false;
            // ⑤ 文字编辑态：用户正在打中文内容，绝不能把输入法挡掉
            if (inTextEditMode) return false;
            // ⑥ 只在命令执行期间（空闲敲命令名时命令栏要能正常输入）
            if (!hasActiveCommand) return false;
            // ⑦ 文字类命令（TEXT/MTEXT/天正文字…）：命令期间要打中文
            if (commandNeedsChinese) return false;
            return true;
        }

        /// <summary>
        /// 手动强制解除（现场命令 <c>XIMEBYPASS 1</c>）时，是否**允许**继续生效。
        ///
        /// ★ 存在的理由：手动强制解除的目的是"让用户回到 CAD 里试着按几个键"，
        ///   而它**必须在"没有命令在执行"的空闲期也保持** —— 否则用户一离开命令行，
        ///   引擎的同步逻辑（空闲 ⇒ 该还原）立刻把它撤掉，等于白按。
        ///
        /// ★ 但"保持"必须有硬闸门，不然就会把用户卡在**打不出中文**：
        ///   · <b>文字编辑态一律否决</b> —— 那是用户输入中文的唯一入口
        ///     （双击文字改字、TEXT/MTEXT 的编辑框）。任何"强制"都不许挡它。
        ///   · 机制被关掉（<c>XIMEBYPASS off</c>）⇒ 强制一并作废，不许绕过开关。
        ///   · 没接管（离开 CAD / 停引擎）⇒ 必须还原，不许留残留。
        /// </summary>
        /// <param name="switchOn">配置开关 <c>BypassImeDuringCommand</c>。</param>
        /// <param name="engaged">引擎是否处于接管态。</param>
        /// <param name="inTextEditMode">是否处于文字编辑态。</param>
        public static bool ShouldHonorManualOverride(
            bool switchOn,
            bool engaged,
            bool inTextEditMode)
        {
            if (!switchOn) return false;       // 机制关了 ⇒ 强制作废（不许绕过开关）
            if (!engaged) return false;        // 未接管 ⇒ 必须还原（不留残留）
            if (inTextEditMode) return false;  // ★ 文字编辑态最高优先级：要能打中文
            return true;
        }

        /// <summary>
        /// 解除 AutoCAD 主窗口**及其全部子孙窗口**与输入法的关联。
        ///
        /// 为什么必须连子孙一起（而不只是主窗口）：输入法处理按键用的是
        /// **当前持有焦点的那个窗口**。命令执行期间焦点可能在绘图区，也可能落回
        /// 命令栏控件（AutoCAD 2022 是 <c>WindowsForms10.EDIT…</c> 子控件）——
        /// 只处理主窗口覆盖不全。
        ///
        /// 幂等：已经解除关联时直接返回，不会重复保存（否则还原时会写回错值）。
        /// </summary>
        /// <param name="root">AutoCAD 主窗口句柄。</param>
        /// <param name="visited">枚举到的窗口总数（诊断用）。</param>
        /// <returns>实际被解除关联的窗口数（原本关联着输入法的窗口数）。</returns>
        public int Engage(IntPtr root, out int visited)
        {
            visited = 0;
            if (_active) return _saved.Count;
            // ★ v6.3：句柄必须先证明"是个真窗口"再动手。
            //   为什么（这是"假激活"的来源）：若拿到的是一枚**已失效**的句柄
            //   （CAD 主窗口重建 / 切换文档后旧句柄过期），
            //     · `EnumChildWindows` 枚举不到任何子孙 ⇒ `visited == 1`（只有 root 自己）
            //     · `ImmAssociateContext` 一律失败 ⇒ `_saved` 空
            //     · 而旧代码仍然 `_active = true` ⇒ 留下一个**假的已激活态**。
            //   后果很具体：`SyncImeBypass` 的幂等判断看到 `IsActive == true`
            //   就以为"已经生效了"，于是**后面整条命令都不会再尝试解除关联**
            //   —— 用户看到的就是"命令期间照旧弹候选窗"，而日志上什么都看不出来。
            //   ⇒ 宁可当场失败（返回 0、不进入激活态），也不留假状态。
            if (root == IntPtr.Zero) return 0;
            if (!NativeMethods.IsWindow(root)) return 0;

            var windows = new List<IntPtr>();
            windows.Add(root);
            try
            {
                // EnumChildWindows 会递归枚举所有**后代**（含子窗口的子窗口）
                NativeMethods.EnumChildWindows(root, delegate(IntPtr h, IntPtr p)
                {
                    if (h != IntPtr.Zero) windows.Add(h);
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception) { /* 枚举失败就只处理主窗口 */ }

            visited = windows.Count;
            LastVisitedCount = visited;

            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                try
                {
                    // ★ 解除关联：返回值 = 该窗口**原来**关联的 HIMC。
                    //   为 0 表示它本来就没有输入法上下文（绘图区就是这种），
                    //   这种窗口无需还原，也不计入"真的被挡掉的窗口数"。
                    IntPtr old = NativeMethods.ImmAssociateContext(w, IntPtr.Zero);
                    if (old != IntPtr.Zero) _saved[w] = old;
                }
                catch (Exception) { /* 单个窗口失败不影响其它窗口 */ }
            }

            _active = true;
            return _saved.Count;
        }

        /// <summary>
        /// 精确还原：把每个窗口**原来**的 HIMC 写回去。
        ///
        /// 幂等，且**即使当前不处于激活态也会尝试清空残留**（双保险：
        /// 任何"只开不关"的意外都最终被这里收口）。
        /// </summary>
        /// <returns>成功还原的窗口数。</returns>
        public int Disengage()
        {
            if (_saved.Count == 0)
            {
                _active = false;
                return 0;
            }

            int restored = 0;
            foreach (var kv in _saved)
            {
                try
                {
                    // 窗口可能已被销毁（切换文档 / 关闭面板）—— 已销毁的跳过即可，
                    // 不要往无效句柄上写，那会失败并留下未还原的错觉。
                    if (!NativeMethods.IsWindow(kv.Key)) continue;
                    NativeMethods.ImmAssociateContext(kv.Key, kv.Value);
                    restored++;
                }
                catch (Exception) { }
            }

            _saved.Clear();
            _active = false;
            return restored;
        }

        /// <summary>剩余未还原的窗口数（诊断用；正常应为 0）。</summary>
        public int PendingRestoreCount { get { return _saved.Count; } }
    }
}
