using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace Xime.Core
{
    /// <summary>插件运行模式。</summary>
    public enum XimeWorkMode
    {
        /// <summary>
        /// 切换输入法模式：**真的去切输入法的中英状态**（不换键盘布局）。
        /// ★ v6 起**不再是默认**（退路，用 `XIMEMODE 1` 选）—— 用户明确要求默认必须是
        /// 「无视输入法模式」：一个模式的核心承诺就是它的契约，不能替用户改。
        ///
        /// 工作方式（与用户的原始需求逐条对应）：
        ///   ① 进入 CAD → 检测当前输入法，把它保持在**英文**状态；
        ///   ② 执行白名单命令（各专业软件里需要输入文字的命令 + 用户自加的命令）
        ///      → 切成**中文**；
        ///   ③ 命令结束 / 退出输入 → 切回**英文**并保持；
        ///   ④ CAD 退出或最小化 → 切回**电脑的默认输入法状态**（本次接管前的状态）。
        ///
        /// 与 <see cref="IgnoreIme"/> 的根本区别：
        ///   · IgnoreIme **不碰**输入法，靠键盘钩子把英文按键投进命令栏；
        ///   · SwitchIme **不接管按键**（只装钩子观察用户手势），靠真的切换输入法中英态来打英文。
        ///   两条通路互不复用，改这条不会影响另一条。
        /// </summary>
        SwitchIme = 0,

        /// <summary>无视输入法模式（**绝不碰**用户输入法）。★ **当前默认**（v6 起）。
        ///
        /// 命令期间配合 <c>XIMEKEY 2</c>（<c>PassThrough</c>）放行物理键 —— 这是天正这类
        /// 只认原始按键的第三方命令**唯一认的档**。
        /// 放行物理键的副作用（输入法也收到键 ⇒ 弹候选窗）由
        /// <see cref="XimeConfig.BypassImeDuringCommand"/> 消除：
        /// 命令期间让 CAD 窗口**无视输入法**（解除窗口与输入法上下文的关联），
        /// 而**输入法自身的状态一个字节都不动**。详见 <see cref="ImeWindowBypass"/>。</summary>
        IgnoreIme = 1
    }

    /// <summary>
    /// 插件配置：命令白名单、开关状态、工作模式等。
    /// 配置以 XML 形式保存在插件目录旁，便于用户手工编辑。
    /// </summary>
    public sealed class XimeConfig
    {
        /// <summary>
        /// 是否已启用 X 输入法。
        ///
        /// 语义（最终版，v25 简化）——只由「用户是否显式表过态」决定：
        ///   · **新装 / 从未关过**（<see cref="EnabledByUser"/> = false）→ 加载后一律 true；
        ///   · **用户主动关过**（<see cref="EnabledByUser"/> = true）→ 永远尊重其选择，
        ///     之后每次加载都保持关闭（直到用户再手动开启）。
        ///
        /// ★ 取舍说明（这条语义来回改过两次，别再反复）：
        ///   一度改成「每次加载都强制启用、关闭只对当次会话有效」，理由是
        ///   「用户误关过之后每次启动都是关的，看起来像插件没生效」。
        ///   但那样会让「关闭」按钮形同虚设 —— 用户明确关掉的功能会被静默恢复，
        ///   这是更严重的问题。最终定案：**尊重用户显式意图**，关闭按钮是**持久**的。
        ///   为了兼顾「误关后不会一脸懵」，加载时若检测到关闭状态，命令行会
        ///   明确提示（见 XimeApplication.Initialize），并告知恢复方法。
        ///
        /// ★ 为什么不再使用 ConfigVersion 参与判定（v25）：
        ///   旧规则是「EnabledByUser=false 且 ConfigVersion&lt;2 → 启用，否则按文件值」。
        ///   问题在于：某些历史版本写出的文件带着
        ///   「Enabled=false 但没有 EnabledByUser 节点 + 版本号已经是 2」的组合，
        ///   于是既不被当成迁移对象、也没有「用户关过」的证据，
        ///   结果**每次重启都是关闭状态** —— 用户两次报告「加载后不是默认启动」。
        ///   现在判定只依赖 EnabledByUser 这一个明确的证据：
        ///   没有「用户关过」的证据 → 一律启用；有 → 一律尊重。
        ///   规则简单到不会被文件版本号的历史状态带偏。
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>工作模式的存储字段。字段初值就钉成默认值（★ 默认 = 无视输入法模式）。</summary>
        private XimeWorkMode _workMode = XimeWorkMode.IgnoreIme;

        /// <summary>
        /// 当前工作模式（★ 默认「无视输入法模式」）。
        ///
        /// ★ 为什么用"字段初值 = IgnoreIme"而不是裸的自动属性：
        ///   枚举里 <see cref="XimeWorkMode.SwitchIme"/> = 0，正好是 CLR 的默认值，
        ///   裸自动属性在"构造未跑完"或日后有人加了反序列化路径时会悄悄变成
        ///   「切换输入法模式」。初值钉死 + 构造函数 / <see cref="ResetToFreshInstall"/>
        ///   两处显式赋值，三处一致，默认值不会被枚举的数值顺序带偏。
        ///
        /// ★ 两种模式仍然**都可选**（用户上一轮要求撤回"只允许无视输入法"的锁定）：
        ///   设置面板「模式」区二选一、命令 XIMEMODE 0|1 即时切换、配置文件照常读写。
        ///   「加载后默认 = 无视输入法模式」由上面这三处默认值保证，与"能不能切"无关。
        /// </summary>
        public XimeWorkMode WorkMode
        {
            get { return _workMode; }
            set { _workMode = value; }
        }

        /// <summary>是否需要输入中文的命令白名单（小写比较）。</summary>
        public HashSet<string> ChineseCommands { get; private set; }

        /// <summary>配置文件的完整路径。</summary>
        public string ConfigPath { get; set; }

        /// <summary>
        /// 用户是否曾**显式**通过界面 / 命令改过启用开关。
        ///
        /// 用途：新版本升级或首次运行时，若用户从没主动开关过，就把插件置为
        /// 默认「启用」；一旦用户手动关过，则**永远尊重其选择**、保持关闭。
        /// </summary>
        public bool EnabledByUser { get; set; }

        /// <summary>
        /// 命令执行期间（选项 / 二级提示阶段）按键的投递方式。
        ///
        /// ★★ 事故背景（天正「任意布置」RYBZ 二级命令 + 输入法候选窗）：
        ///
        ///   插件的接管机制是「钩子吞掉物理按键 → 用注入把按键送进 AutoCAD」。
        ///   两个阶段对"怎么送"的要求完全相反：
        ///
        ///   · **空闲提示符（敲命令名）**：AutoCAD 读的是命令行**文本**，
        ///     所以用 <c>SendInput(KEYEVENTF_UNICODE)</c> 注入字符最好 ——
        ///     它绕过输入法、结果恒定，永远是英文。
        ///
        ///   · **命令执行期间（选项 / 二级提示）**：大量第三方命令
        ///     （实测：天正 T20 v10 的 RYBZ「旋转90°=A / 放大=E / 缩小=D /
        ///     左右翻转=F / X·Y 偏移」）**不读命令行文本，而是读原始按键**
        ///     （acedGrRead 一类）。而 UNICODE 注入产生的事件其 wVk 恒为
        ///     VK_PACKET(0xE7)，不是真实键码 → 命令认不出来，
        ///     表现为字符进了命令栏、命令却毫无反应。
        ///
        ///   v25 的解法是「命令期间完全放行物理按键」：天正确实好了
        ///   （它拿到的是真物理键），但**物理按键同时落到了输入法手里** ——
        ///   用户截图里那个「左上角候选窗同步显示 aaaaad」就是这么来的。
        ///
        ///   v26 起把投递方式做成**可切换的多档**（都编在同一个 DLL 里，`XIMEKEY` 现场切）。
        ///
        ///   v26 起把投递方式做成**可切换的多档**（都编在同一个 DLL 里，`XIMEKEY` 现场切），
        ///   v1.0.2 又追加了第 4 档 <c>PostKeyFrame</c>。
        ///
        ///   · <see cref="XimeCommandKeyMode.PassThrough"/>（★ **当前默认**；v25 行为，v58 亦是默认）
        ///       命令执行期间**完全不接管**，物理键原样交给系统 ⇒ 第三方命令拿到的是**真物理键**。
        ///       实测（天正 T20 v10 的 RYBZ「旋转90°=A / 放大=E / 缩小=D / 左右翻转=F / X·Y 偏移」）：
        ///       二级命令确实能响应。
        ///       ★★ **四条合成通道（PostKey / PostKeyFrame / ReplayVk / UNICODE）天正一律不认，
        ///       只有它认** —— 所以它才是默认档。
        ///       副作用：中文输入法也会收到这些键、弹出候选窗并输入字母 a。
        ///       ★★ v6 起这个副作用**已被消除**：命令期间由
        ///       <see cref="BypassImeDuringCommand"/>（<see cref="ImeWindowBypass"/>）
        ///       让 CAD 窗口「无视输入法」—— 物理键照旧直达天正，但输入法收不到键。
        ///       ★ 注意那是**解除窗口与输入法的关联**，不是"把输入法切成英文"：
        ///         用户的输入法状态一个字节都没动（这才是「无视输入法模式」的契约）。
        ///
        ///   · <see cref="XimeCommandKeyMode.PostKey"/>（v1.0.2 上旬短暂当过默认，**当天即撤销**）
        ///       吞键 → <c>PostMessage(WM_KEYDOWN/WM_KEYUP, lParam 带真实扫描码)</c> 投给
        ///       **焦点控件**（<c>GetRealFocusWindow()</c>）。
        ///       ★★ 2026-10-06 现场证伪：投给绘图区时，消息进 AutoCAD 主线程队列后
        ///       **被当成命令栏文本**处理 ⇒ 命令栏里留下一个小写 <c>a</c>、还得再按一次空格，
        ///       而天正读**原始按键**的那条通道**根本收不到** ⇒ 二级命令毫无反应。
        ///       ⇒ 它并不能"两全"，只是把症状换了个样子。**不要再把它设成默认。**
        ///
        ///   · <see cref="XimeCommandKeyMode.PostKeyFrame"/>（★ v1.0.2 新增）
        ///       吞键 → 同样的窗口消息，但投给 **AutoCAD 前台顶层窗口**（取不到就用主窗口）。
        ///       与 PostKey 的唯一差别是目标窗口：PostKey 依赖"焦点控件可解析"，
        ///       PostKeyFrame 不依赖 —— 万一焦点控件被第三方插件吃掉、解析不到，
        ///       这一档仍能把按键送进 AutoCAD 的消息泵。
        ///
        ///   · <see cref="XimeCommandKeyMode.ReplayVk"/>
        ///       吞键 → SendInput 重放「真 VK + 真扫描码」。
        ///       ★ 注入的事件仍进入系统输入队列，输入法照样能收到 ——
        ///         所以它既不能保证命令可用、也不能保证不弹候选窗，
        ///         实测对天正 RYBZ 无优势（第三方命令认原始按键，不认合成输入），保留仅供对照。
        ///
        ///   现场排查：四档**都放在同一个 DLL 里**，用命令
        ///     XIMEKEY          查看当前方式
        ///     XIMEKEY 0/1/2/3  立刻切换（写盘，重启后保持）
        ///   即可在同一个 AutoCAD 会话里逐一切换验证，无需换 DLL。
        /// </summary>
        public XimeCommandKeyMode CommandKeyMode { get; set; }

        /// <summary>
        /// ★ v56：切输入法时**优先使用小狼毫（Rime/Weasel）自己的命令行接口**
        /// （<c>WeaselServer.exe /ascii|/nascii</c>，需 weasel 0.16.0+），而不是"盲按 Shift"。
        ///
        /// 为什么必须加这一条：Shift 是**翻转键**，它的正确性建立在"我们知道当前是
        /// 什么态"之上 —— 而小狼毫走 TSF，IMM32 读不回真实中英态（铁律 13），
        /// 于是盲按的 Shift 会不会切对**纯属碰运气**。2026-10-06 实机日志钉死了后果：
        ///   · `TapShift` 报 `结果=True 方式=RealKey`，121ms 后回读仍是 Chinese；
        ///   · 那次**失败**的盲按还写下 `_switchApplied = English` 的假记录；
        ///   · 此后每一次英文请求都被"本模式已置于 English → 无需动作"短路，
        ///     整个会话再也发不出一次切换 ⇒ 用户按 A 被中文输入法吃掉。
        ///
        /// 小狼毫的命令行是**幂等设值**（不是翻转），所以"切反"和"假记录"两个
        /// 问题一并消失。找不到 WeaselServer.exe 时**自动退回**原来的真实 Shift，
        /// 非小狼毫用户行为完全不变。
        ///
        /// 关掉它 → 行为完全回到 v55（仅真实 Shift）。
        /// </summary>
        public bool UseWeaselCli { get; set; }

        /// <summary>
        /// ★ v6：命令执行期间**让 CAD 窗口无视输入法**（默认 true）。
        ///
        /// 解决的正是「无视输入法模式」最后剩下的那个副作用：
        ///
        ///   天正二级命令只认真实物理按键 ⇒ 必须放行物理键（<c>PassThrough</c>）；
        ///   而放行物理键时**输入法也会收到这些键** ⇒ 中文态下弹候选窗、并输入字母。
        ///
        /// 打开本项后，插件在「命令执行期间 + 非文字类命令」时，把 AutoCAD 主窗口
        /// 及其子窗口与**输入法上下文的关联解除**（<c>ImmAssociateContext(hwnd, NULL)</c>，
        /// 微软 KB Q171154 的官方做法），命令结束立刻精确还原。
        ///
        /// ★★ 它与「切换输入法模式」的区别（关键，别混为一谈）：
        ///     · 切换输入法模式 = **改输入法状态**（真的切成英文，全局可见、用户能看出来）；
        ///     · 本项           = **不改输入法状态**（中/英、键盘布局、状态栏一律不动），
        ///                        只是让 CAD 的窗口不接收输入法处理。
        ///     所以它**没有违背**「无视输入法模式」"绝不修改用户输入法状态"的契约 ——
        ///     恰恰相反，"这个窗口无视输入法"就是这个模式名字的字面意思。
        ///
        /// ★ 什么时候**不会**生效（刻意保留输入法）：正在输入中文内容的场合 ——
        ///     文字类命令（TEXT/MTEXT/天正文字…）期间、以及焦点落在文字编辑框时。
        ///
        /// 关掉它（false）→ 回到旧行为：物理键照旧直达天正，但输入法也会看到
        /// （中文态下弹候选窗）。
        /// </summary>
        public bool BypassImeDuringCommand { get; set; }

        /// <summary>
        /// 命令执行期间按键的投递方式（见 <see cref="CommandKeyMode"/> 的长注释）。
        /// </summary>
        public enum XimeCommandKeyMode
        {
            /// <summary>吞键 + 用真实 VK/扫描码重放。★ 早已**不是默认** ——
            /// 实测对天正 RYBZ 无优势（第三方命令认原始按键，不认合成输入），
            /// 而且注入事件仍进系统输入队列 ⇒ 输入法照样会弹候选窗。</summary>
            ReplayVk = 0,

            /// <summary>吞键 + PostMessage 投给**焦点控件**（GetRealFocusWindow()，
            /// 解析不到退回命令栏控件）。
            /// ★ **已撤销的默认档**（v1.0.2 上旬当过默认，当天现场证伪）：投给绘图区时
            /// 消息会变成**命令栏文本**（留一个小写 a、还要再按空格），天正读原始按键的
            /// 通道收不到 ⇒ 二级命令毫无反应。</summary>
            PostKey = 1,

            /// <summary>不吞键，物理按键原样放行（v25 行为，v58 亦是默认）。★ **当前默认**。
            /// ★★ 四条合成通道天正一律不认，**只有它认**。代价：输入法也会看到这些物理键
            /// （中文态下会弹候选窗、输入字母 a）。</summary>
            PassThrough = 2,

            /// <summary>★ v1.0.2 新增：吞键 + PostMessage 投给 AutoCAD **前台顶层窗口**
            /// （取不到退回主窗口）。与 PostKey 的唯一差别是目标窗口 ——
            /// 焦点控件解析不到时（第三方插件吃掉焦点）仍能把按键送进消息泵。</summary>
            PostKeyFrame = 3
        }

        /// <summary>
        /// 配置文件格式版本。★ 只用于**人工识别**，不参与任何判定
        /// （判定规则见 <see cref="Enabled"/> 的注释；曾用版本号参与"是否启用"的判断，
        ///   结果把插件永久卡在关闭状态，已废弃）。
        /// </summary>
        private const int CurrentConfigVersion = 6;
        public int ConfigVersion { get; set; }

        /// <summary>内置默认中文命令白名单。</summary>
        private static readonly string[] DefaultChineseCommands =
        {
            // ---- AutoCAD 原生文字 / 编辑 ----
            "text", "dtext", "mtext", "mtexted",
            "ddedit", "tedit", "textedit", "mtedit", "tjust", "tcase",
            "spell", "find", "qtext", "tcount",
            // ---- 双击对象进入的「对象编辑」类命令 ----
            // 说明：双击 AutoCAD 原生文字 -> DDEDIT；双击天正文字 -> TOBJEDIT
            //（天正对象编辑），两者都应允许中文输入，否则双击后仍是英文状态。
            "tobjedit", "objedit", "eattedit", "ddatte",
            "t73_tobjedit", "t20_tobjedit", "t81_tobjedit",
            // ---- 属性 / 块属性 ----
            "attdef", "attedit", "eattedit", "battman", "attreq", "attsync", "attext",
            // ---- 标注文字 ----
            "dimedit", "dimtedit", "dimtext", "qleader", "mleader", "mleaderedit",
            // ---- 表格 / 字段 ----
            "table", "tableedit", "field", "tablestyle", "tsedit",
            // ---- 常用含中文输入 ----
            "rename", "layer", "mlinestyle", "dimstyle", "style", "group",

            // ============ 本插件自身命令（用户明确要求加入白名单）============
            // ★ xime 打开设置界面（Modal 对话框），界面里有文本框/搜索框需要输入
            //   中文（如增删白名单命令、修改说明文字）。把它列入白名单后，
            //   两种模式下执行 xime 时都会按「文字类命令」处理：
            //     · 切换输入法模式 → 切中文，方便在设置界面里输入中文；
            //     · 无视输入法模式 → 命令开始即确保中文，命令栏不再拦截。
            //   其余辅助命令（XIMESTATUS/XIMEMODE 等）是只读诊断、不产生文字输入，
            //   刻意不列入，避免误切输入法。
            "xime",

            // ============ 天正系列（建筑/电气/结构/给排水/暖通）============
            // 天正在 AutoCAD 中的【注册命令名】带 T73_ / T20_ / T81_ 等前缀，
            // 用户敲的则是【别名】。两者可能进入 CommandWillStart，故都收录。
            //
            // 文字类（别名）
            "dhwz", "tmttext", "t_text", "ttext",   // 单行/多行文字
            "zywz", "dqwz",                          // 专业文字 / 电气文字
            "qxwz",                                  // 曲线文字
            "wzzh", "wzhb",                          // 文字转化 / 文字合并
            "xgwz", "dd", "dzwz",                    // 修改文字 / 递增文字
            "wzys", "zyck", "tyzg",                  // 文字样式 / 专业词库 / 统一字高
            "czth", "jzf", "fjz",                    // 查找替换 / 简转繁 / 繁转简
            "wzfw", "wzfz",                          // 文字复位 / 文字复值
            // 文字类（注册名，含命名空间前缀）
            "t73_ttext", "t73_tmtext", "t73_ttextconv", "t73_ttextmerge",
            "t73_tstyleex", "t73_tchdimtext", "t73_tresetdimp", "t73_tresetdimt",
            "t73_tsheetEdit", "t73_tcellEdit", "t73_trowEdit", "t73_tcolEdit",
            "t73_tcellmerge", "t73_tnewsheet", "t73_trepfind", "t73_tbig5_gb",
            "t73_tgb_big5", "t73_ttextadjust",
            "t20_ttext", "t20_tmtext", "t20_tstyleex",
            "t81_ttext", "t81_text", "t81_tmtext",
            // 符号标注类（别名）—— 这类命令通常要输入中文标注文字
            "dzbg", "tmelev", "zxsy", "pqsy", "sytm", "pmpq", "dmpq",
            "jtyz", "ycbz", "zfbz", "tmbz", "hzbz", "jzdx",
            "ggwz", "qhbz", "qzbz", "wzbz",
            // 表格填写（常含中文）
            "xjbg", "qpbj", "dybj", "dyhb", "blbj", "bhbj", "bgtx", "dydz", "dyfz",
            "t73_tcomposing", "t73_tdrawingname", "t73_tindexdim", "t73_tindexptr",
            "t73_tindexsec", "t73_telev", "t73_tmelev", "t73_tarrow", "t73_trupture",
            "t73_tsymmetry", "t73_tnorththumb",

            // ============ 常见第三方批注 / 文字插件 ============
            // GM 批注、天正批注、引线标注类
            "gmnote", "gmbz", "gm_pz", "gmnote1", "gmnote2",
            "bz", "pz", "ycbz2", "jbz",   // 批注 / 标注 常见短别名
            "mxet_drawcomment", "mx_arrow", "mx_coordinate", "mx_circlemark", "mx_matrixmark"
        };

        /// <summary>
        /// 命令名关键词兜底：命令名（去前缀后）**包含**这些片段即视为「需要中文」。
        ///
        /// 用于覆盖无法穷举的第三方插件（天正各专业版、GM 批注等）。
        /// 注意：仅在其不含绘图特征时才生效，避免误伤 line/pline 等。
        ///
        /// ★★ 硬性约束：词根长度必须 ≥ 4 个字符。
        ///
        ///   早期版本放了 "wz" / "bz" / "pz" / "zh" / "hb" 这些两字母词根，
        ///   结果它们把大量**与文字无关**的命令也拖下水 —— 最典型的是
        ///   天正的 RYBZ（**任**意**布**置）：本意是标注(biaozhu)的 "bz"，
        ///   被"布置(buzhi)"撞上 → 命令被判为需要中文 → 插件把输入法切成中文 →
        ///   选中块之后敲二级命令（A 旋转 / E 放大 / D 缩小 / F 翻转 / X·Y 偏移）
        ///   全部被输入法吃掉，天正收不到 → 功能失效。
        ///   用户实测：AutoCAD 2014+天正2014 正常，AutoCAD 2022+天正T20 v10 失效。
        ///
        ///   两字母拼音首字母的组合歧义太大（bz=标注/布置/比例，pz=批注/配置/偏移，
        ///   zh=转换/组合/正交，hb=合并/横剖……），**收益远小于误伤代价**：
        ///   漏判只是"文字命令里要手动按一下 Shift"，误判却是"命令直接不能用"。
        ///   确实需要兜底的短别名，请加进 <see cref="DefaultChineseCommands"/> 精确列表，
        ///   或让用户在界面的命令白名单里自行添加。
        /// </summary>
        private static readonly string[] ChineseKeywordHints =
        {
            "text",     // 单行/多行文字的公共词根（text / ttext / mtext / dtext）
            "ttext",    // 天正单行文字
            "mtext",    // 多行文字
            "dtext",    // 动态文字
            "dhwz",     // 天正单行文字（别名）
            "tmtext",   // 天正多行文字
            "objedit",  // 对象编辑（天正双击文字 -> TOBJEDIT）
            "comment",  // 批注（如 MxET_DrawComment）
            "note"      // 注释（如 GMNote）
        };

        /// <summary>
        /// 「需要中文」的命中级别。
        ///
        /// 区分两者很重要：**精确命中**是作者/用户明确确认过的命令，可以放心地
        /// 在命令开始时就主动把输入法切成中文；**关键词兜底**只是按名字猜的，
        /// 猜错的代价很大（把输入法切走 → 用户按键被输入法吃掉），
        /// 因此只在命令真正进入文字编辑（焦点落到编辑框）时才切。
        /// </summary>
        public enum ChineseNeed
        {
            /// <summary>不需要中文（命令期间应保持英文接管）。</summary>
            None = 0,
            /// <summary>精确命中白名单：可以放心地提前切中文。</summary>
            Exact = 1,
            /// <summary>仅由关键词兜底命中：名字像文字命令，但不保证，不提前切中文。</summary>
            Keyword = 2
        }

        public XimeConfig()
        {
            // 默认启用：新装用户加载后立即可用，无需先手动开。
            // （若用户曾在界面里「关闭」，该状态会写入配置文件并在下次加载时保留。）
            Enabled = true;
            EnabledByUser = false;      // 用户尚未显式开关过
            // ★★★ 默认工作模式 = **无视输入法模式**（v6 定案：用户明确要求改回默认）。
            //
            //   ★★★ 用户的否决（原话）：`我不同意默认切换输入法模式，这个要改回去，
            //       我要默认无视输入法模式。`
            //   ⇒ **一个模式的核心承诺就是它的契约**：本插件的立身之本是
            //     「绝不修改用户的输入法状态」，默认必须是那个守约的模式。
            //     哪怕另一种模式在某个具体场景下效果更好，也不能擅自替用户改契约。
            //
            //   天正二级命令（RYBZ 的 A/S/D/E 选项）在这个模式下的解法**已经换了**：
            //
            //     ① 天正只认真实物理按键 ⇒ 唯一生效的档是「放行物理键」
            //        (<see cref="XimeCommandKeyMode.PassThrough"/>)。
            //        四条合成通道（PostKey / PostKeyFrame / ReplayVk / UNICODE）
            //        **一律不认** —— 已现场证伪，别再回头。
            //     ② 放行物理键的副作用是"输入法也看到了这些键 ⇒ 弹候选窗"。
            //        ★ 旧版本把这里当成**结构性矛盾无解**，是因为只在
            //          「改输入法状态」和「吞键再合成」两条路里打转，
            //          漏掉了第三条：**什么都不改，只让 CAD 窗口不接收输入法** ——
            //          见 <see cref="ImeWindowBypass"/>（解除窗口与输入法上下文的关联，
            //          输入法状态一个字节都不动，而物理键照样直达天正）。
            //
            //   实测基线（2026-10-06，2022+天正T20，RYBZ→选块→按A）：
            //     · 无视模式 + PassThrough      → 天正立即有动作 + 命令栏无 a；
            //                                      ★ 唯一遗留问题 = 输入法弹候选窗
            //                                      → 由 ImeWindowBypass 消除
            //     · 无视模式 + PostKey          → 天正**没动作**，命令栏留 a（已证伪）
            //     · 切换输入法模式              → 三全，但**改了用户的输入法状态**
            //                                      → 用户否决，只能作退路（`XIMEMODE 1`）
            WorkMode = XimeWorkMode.IgnoreIme;
            // ★ 命令期间按键投递档 —— **只在无视输入法模式下才有意义**
            //   （切换输入法模式**不接管按键**，只观察用户手势）。
            //
            //   默认 = PassThrough（放行物理键），因为**它是天正唯一认的档**。
            //   PostKey 曾短暂当过默认（2026-10-06 上旬），当天即被现场证伪并撤销：
            //   它用 PostMessage 把 WM_KEYDOWN 投给绘图区，消息进 AutoCAD 主线程后
            //   **被当成命令栏文本**处理 ⇒ 命令栏出现小写 a，而天正读原始按键的
            //   通道根本收不到。
            //   实测四档（同一次现场，RYBZ→选块→按A）：
            //     · 档 2 PassThrough  → ★ 天正认（唯一认的档）
            //     · 档 1 PostKey      → 天正**不认**（只往命令栏打字）
            //     · 档 3 PostKeyFrame → 投主框架，天正收不到（毫无反应）
            //     · 档 0 ReplayVk     → 天正不认，输入法照样看到
            //   ★ PassThrough 的"输入法会看到"这一副作用，由
            //     <see cref="ImeWindowBypass"/> 消除（让 CAD 窗口不接收输入法，
            //     而输入法自身状态纹丝不动）。详见 <see cref="BypassImeDuringCommand"/>。
            //   ★ 想换投递方式仍可用 `XIMEKEY 0/1/2/3` 现场切（写盘，重启保持）。
            CommandKeyMode = XimeCommandKeyMode.PassThrough;
            // v56：切输入法优先走小狼毫命令行（幂等设值；找不到就自动退回真实 Shift）。
            //   ★ 只在「切换输入法模式」下才会用到 —— 无视模式不碰输入法。
            UseWeaselCli = true;
            // ★ v6：命令执行期间让 CAD 窗口**无视输入法**（消除 PassThrough 的候选窗副作用）。
            //   机制见 <see cref="ImeWindowBypass"/>：只解除窗口与输入法上下文的关联，
            //   **不修改输入法状态**。默认开；万一在你的机器上不合意，改成 false 即可
            //   退回"输入法会弹候选窗"的旧行为。
            BypassImeDuringCommand = true;
            ConfigVersion = CurrentConfigVersion;
            ChineseCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in DefaultChineseCommands)
                ChineseCommands.Add(c);
        }

        /// <summary>
        /// 把配置复位成「全新安装」的默认状态。
        ///
        /// ★ 专供「卸载」使用，且必须与构造函数里的默认值保持一致。
        ///
        /// 为什么卸载要复位而不是写成"关闭"：
        ///   「关闭」与「卸载」是两件不同的事，落盘结果也必须不同 ——
        ///     · 用户点「关闭 X输入法」 → 写 Enabled=false + EnabledByUser=true。
        ///       Load() 见到 EnabledByUser=true 就永远尊重文件里的 false，
        ///       这正是"关了就该一直关着"需要的语义。
        ///     · 用户点「卸载 X输入法」 → 应当把插件从这台机器上**抹掉**。
        ///       若照旧写 Enabled=false + EnabledByUser=true，下次重新 NETLOAD
        ///       就会被当成"用户曾经主动关过"，于是**加载后默认不启用** ——
        ///       用户看到的现象正是「卸载之后再加载不会默认开启」。
        ///       卸载之后重新加载本质上是**一次全新安装**，理应与第一次安装
        ///       完全一致：默认启用 + 默认**无视输入法模式**。
        ///
        /// Enabled=true 是为了让文件内容自洽（EnabledByUser=false 时
        /// Load() 本来就会强制为 true），不会让"关闭"语义被误用。
        ///
        /// ★★ v42：**必须与构造函数逐项一致**（自测会逐项钉住）——
        ///   用户的要求是"加载-改设置-卸载-重新加载，重新加载就是初始状态"。
        ///   早先这里只复位了 Enabled/EnabledByUser/WorkMode 三项，漏掉
        ///   <see cref="CommandKeyMode"/>：用户若在界面上把它改成 PostKey/PassThrough，
        ///   卸载后重新加载仍会带着这个改动，就不是"初始状态"了。
        ///   ★ 白名单（<see cref="ChineseCommands"/>）**刻意保留** —— 那是用户积累的
        ///     资产（实测 140 条命令），不属于"设置项"，抹掉它才是不合理的。
        /// </summary>
        public void ResetToFreshInstall()
        {
            // —— 与构造函数默认值逐项对齐（改这里必须同步改构造函数）——
            Enabled = true;
            EnabledByUser = false;
            WorkMode = XimeWorkMode.IgnoreIme;               // ★ 默认工作模式（见构造函数长注释）
            CommandKeyMode = XimeCommandKeyMode.PassThrough; // ★ 天正唯一认的档（仅无视模式下有意义）
            UseWeaselCli = true;
            BypassImeDuringCommand = true;                  // ★ 命令期间让 CAD 窗口无视输入法
            ConfigVersion = CurrentConfigVersion;
            // 白名单保持不动（用户资产）
        }

        /// <summary>
        /// 把「决定行为的关键状态」描述成一行（v43）。
        ///
        /// ★ 抽出来的理由有两条：
        ///   ① **唯一一份定义** —— 卸载的"前/后快照"（<c>SettingsForm</c> +
        ///      <c>XimeCommands</c>）与自测断言必须描述同一组字段，
        ///      否则两边各写一遍、早晚漂移（比如又漏掉 <see cref="CommandKeyMode"/>，
        ///      那正是 v42 修过的那个"复位不完整"）。
        ///   ② **可自测** —— 纯函数，<c>XimeTest config</c> 能直接断言
        ///      "复位后的状态行 == 全新安装的状态行"，比逐字段比对更强。
        ///
        /// ⚠ 只读，绝不修改任何字段 —— 它会出现在"卸载前"的快照里。
        /// </summary>
        public string DescribeState()
        {
            return string.Format(
                "Enabled={0} EnabledByUser={1} WorkMode={2} 命令期间按键={3} 小狼毫命令行={4} " +
                "命令期间无视输入法={5} 白名单={6}条",
                Enabled, EnabledByUser, WorkMode, CommandKeyMode,
                UseWeaselCli, BypassImeDuringCommand,
                ChineseCommands == null ? 0 : ChineseCommands.Count);
        }

        /// <summary>默认配置文件位置：与插件 DLL 同目录。</summary>
        public static string GetDefaultConfigPath(string pluginDirectory)
        {
            if (string.IsNullOrEmpty(pluginDirectory))
                pluginDirectory = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(pluginDirectory, "Xime.config.xml");
        }

        /// <summary>
        /// 工作模式的**历史出厂值迁移**（纯函数，可自测）。
        ///
        /// ★ 规则只有一条：**只动"当时那个版本号的出厂值"，用户显式选过的值一律保留。**
        ///   做法是沿历史逐级重放：
        ///     · <c>ver &lt; 5</c> 且值 == 出厂值 <see cref="XimeWorkMode.IgnoreIme"/>
        ///       → 升到 v5 的出厂值 <see cref="XimeWorkMode.SwitchIme"/>；
        ///     · <c>ver &lt; 6</c> 且值 == 出厂值 <see cref="XimeWorkMode.SwitchIme"/>
        ///       → 升到现在（v6）的出厂值 <see cref="XimeWorkMode.IgnoreIme"/>。
        ///
        /// ⇒ 净效果（这正是要有"链"而不是一锤子的原因）：
        ///   · v4 及更早文件里的 IgnoreIme → 先被当成 v5 出厂值换成 SwitchIme，
        ///     再被当成 v5 出厂值换回 IgnoreIme —— **最终仍是无视模式**，
        ///     与新的默认一致；
        ///   · v5 文件里的 SwitchIme（= 那一版的出厂值，用户多半没动过）→ 回到 IgnoreIme
        ///     （用户原话：`默认要改回去，我要默认无视输入法模式`）；
        ///   · 用户在 v5/v6 里用 `XIMEMODE` 显式选过的值 → 原样保留。
        ///
        /// ★ 为什么必须做迁移、不能只改代码默认值：`Load()` 会用配置文件里
        ///   写死的值把新默认**盖回去** —— v1.0.3 就是只改了默认值，老用户实机
        ///   "跟没改一样"。（同一个坑，别再踩。）
        /// </summary>
        internal static XimeWorkMode MigrateWorkMode(int ver, XimeWorkMode v)
        {
            if (ver < 5 && v == XimeWorkMode.IgnoreIme) v = XimeWorkMode.SwitchIme; // v5 的出厂值
            if (ver < 6 && v == XimeWorkMode.SwitchIme) v = XimeWorkMode.IgnoreIme; // v6 的出厂值
            return v;
        }

        #region 加载 / 保存

        public static XimeConfig Load(string path)
        {
            var cfg = new XimeConfig { ConfigPath = path };

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return cfg;   // 首次运行，使用默认值

            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                var root = doc.DocumentElement;
                if (root == null) return cfg;

                // ---- 配置版本（缺省视为 1，即旧配置）----
                var verNode = root.SelectSingleNode("ConfigVersion");
                int ver = 1;
                if (verNode != null) int.TryParse(verNode.InnerText.Trim(), out ver);
                cfg.ConfigVersion = ver;

                // ---- 用户是否显式开关过 ----
                var byUserNode = root.SelectSingleNode("EnabledByUser");
                if (byUserNode != null)
                {
                    bool ub;
                    if (bool.TryParse(byUserNode.InnerText, out ub))
                        cfg.EnabledByUser = ub;
                }

                bool enabledVal = cfg.Enabled;
                var enabledNode = root.SelectSingleNode("Enabled");
                if (enabledNode != null)
                {
                    bool b;
                    if (bool.TryParse(enabledNode.InnerText, out b))
                        enabledVal = b;
                }

                // ---- 启用状态的判定规则（v25 简化：只看「用户是否显式表过态」）----
                //   · EnabledByUser=true  → 完全尊重文件里的 Enabled
                //                            （用户关过就永远关闭，直到他再手动开启）；
                //   · EnabledByUser=false → 一律启用。
                //     文件里的 false 只可能是历史版本的默认值 / 自动写入的，
                //     没有「用户关过」的证据，就不能让它把插件卡在关闭状态。
                //
                //   注意：**刻意不参与版本号判断**。旧规则用 ConfigVersion<2 做条件，
                //   一旦文件版本已升到 2、又带着 Enabled=false 且没有 EnabledByUser
                //   节点，就会永久停在关闭 —— 这正是用户两次反馈的「加载后不是默认启动」。
                cfg.Enabled = cfg.EnabledByUser ? enabledVal : true;

                var modeNode = root.SelectSingleNode("WorkMode");
                if (modeNode != null)
                {
                    try
                    {
                        var parsed = (XimeWorkMode)Enum.Parse(
                            typeof(XimeWorkMode), modeNode.InnerText.Trim(), true);

                        // ★ 只认**已实现**的工作模式，不认的值一律退回默认。
                        //
                        //   本配置文件是鼓励用户手工编辑的（见类注释），而引擎里
                        //   每个分支都会检查模式；一旦文件里出现拼错的值，插件会
                        //   **静默失效**（不报错、不生效），排查成本极高。
                        //   所以这里必须显式校验。
                        //
                        //   历史上 SwitchIme 曾被排除在外（那时它只是保留值，
                        //   一旦被选中插件就完全不动），现在它已完整实现，
                        //   与 IgnoreIme 一样被接受。
                        if (Enum.IsDefined(typeof(XimeWorkMode), parsed))
                        {
                            // ★★ 出厂值迁移（历史链，见 MigrateWorkMode）。
                            //   要点：**只动"当时版本号的出厂值"，用户显式选过的值一律保留。**
                            //   版本号一旦升到 6 并落盘，之后用户的任何显式选择都会被完整保留。
                            cfg.WorkMode = MigrateWorkMode(ver, parsed);
                        }
                    }
                    catch { /* 保持默认（SwitchIme） */ }
                }

                // 命令执行期间的按键投递方式（缺失时保持默认 PassThrough；
                // 无法解析的值过不了下面的 Enum.IsDefined → 一律回落到默认，不会报错）
                //
                // ★★ v5 迁移 —— **必须成对处理**（v1.0.3 正是栽在"只改代码默认值"上）：
                //   老配置文件里写着 <CommandKeyMode>PostKey</CommandKeyMode>
                //   （v1.0.2 上旬短暂存在过的出厂值），若照读就会把新默认 PassThrough
                //   盖回去 ⇒ 改了默认却毫无效果。
                //
                //   规则（ConfigVersion < 5）：
                //     · 文件里是 PostKey → 视为**出厂值**，不是用户显式选择
                //       ⇒ 迁到新默认 PassThrough（PostKey 已现场证伪：天正不认）；
                //     · 文件里是别的值（PassThrough/PostKeyFrame/ReplayVk）→ 一定是
                //       用户用 XIMEKEY 显式选过的 ⇒ **原样尊重，绝不覆盖**。
                //   版本号一旦升到 5 并落盘，之后用户的任何显式选择都会被完整保留。
                var keyModeNode = root.SelectSingleNode("CommandKeyMode");
                if (keyModeNode != null)
                {
                    try
                    {
                        var parsed = (XimeCommandKeyMode)Enum.Parse(
                            typeof(XimeCommandKeyMode), keyModeNode.InnerText.Trim(), true);
                        if (Enum.IsDefined(typeof(XimeCommandKeyMode), parsed))
                        {
                            if (ver < 5 && parsed == XimeCommandKeyMode.PostKey)
                            {
                                // 旧配置的 PostKey = 出厂值，不是用户选择 → 用新默认。
                                cfg.CommandKeyMode = XimeCommandKeyMode.PassThrough;
                            }
                            else
                            {
                                cfg.CommandKeyMode = parsed;
                            }
                        }
                    }
                    catch { /* 保持默认 */ }
                }

                // ★ v58：<ForceEnglishDuringCommand>（v55）已**整体撤除** ——
                //   「无视输入法模式」不再、也绝不允许切换用户输入法状态。
                //   旧配置里若还留着这个节点，读的时候直接忽略（不报错、不影响其它项）。

                // ★ v56：小狼毫命令行桥。**旧配置（没有这个节点）也要享受修复**
                //   ⇒ 缺失时保持默认 true。
                var weaselCliNode = root.SelectSingleNode("UseWeaselCli");
                if (weaselCliNode != null)
                {
                    bool b;
                    if (bool.TryParse(weaselCliNode.InnerText.Trim(), out b))
                        cfg.UseWeaselCli = b;
                }

                // ★ v6：命令期间让 CAD 窗口「无视输入法」（消除 PassThrough 的候选窗副作用）。
                //   ★★ 缺失时**保持默认 true** —— 这是修 bug 的机制，旧配置（没有这个节点）
                //      也必须享受到，否则老用户"改了跟没改一样"（v1.0.3 的教训）。
                var bypassNode = root.SelectSingleNode("BypassImeDuringCommand");
                if (bypassNode != null)
                {
                    bool b;
                    if (bool.TryParse(bypassNode.InnerText.Trim(), out b))
                        cfg.BypassImeDuringCommand = b;
                }
                // ---- 旧节点 <PassThroughDuringCommand>（v25）**刻意不迁移** ----
                //
                // v25 用布尔节点 <PassThroughDuringCommand> 表示"命令执行期间是否放行物理键"，
                // 语义上等于现在的 PassThrough。但**不能照着映射**：
                //
                //   · v25 里这个节点的**默认值就是 true**（构造函数直接写死）。
                //     也就是说凡是 v25 写过的配置文件都会带上 true ——
                //     它反映的不是"用户选了什么"，而是"用过 v25"。
                //   · 而 PassThrough 曾是 v58~v1.0.3 的默认档，
                //     照着映射等于让一个历史包袱盖掉当前默认值。
                //
                // 所以这里什么都不做，保持上面的新默认 PassThrough（放行物理键）。
                // ★ 注意与上面的 `<CommandKeyMode>` 迁移区分：那个是**新格式**的
                //   显式档位节点（v58 起就有），旧配置里写着 PostKey 时按"出厂值"
                //   迁到 PassThrough；而 `<PassThroughDuringCommand>` 是 v25 的布尔节点，
                //   连"用户选了什么"都表达不了，所以一律忽略、不做任何映射。
                // 确实想要别的投递方式的，用 XIMEKEY 0/1/2/3 显式设置 ——
                // 那是新格式的值，能真实表达用户意图。
                //
                // 生效值会写进启动日志的「命令期间按键=…」一行，便于事后核对。

                // 白名单：配置里有的覆盖默认
                var cmdNodes = root.SelectNodes("ChineseCommands/Command");
                if (cmdNodes != null && cmdNodes.Count > 0)
                {
                    cfg.ChineseCommands.Clear();
                    foreach (XmlNode n in cmdNodes)
                    {
                        var cmd = (n.InnerText ?? string.Empty).Trim().ToLowerInvariant();
                        if (cmd.Length > 0)
                            cfg.ChineseCommands.Add(cmd);
                    }
                }
            }
            catch (Exception)
            {
                // 配置损坏时回退默认值，不阻断插件运行
            }

            return cfg;
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                sb.AppendLine("<!-- X输入法 配置文件。修改后重启 AutoCAD 或重新执行 xime 命令生效。 -->");
                sb.AppendLine("<Xime>");
                sb.AppendLine("  <!-- 配置格式版本：仅供人工识别，不参与任何判断，请勿手工修改 -->");
                sb.AppendLine("  <ConfigVersion>" + CurrentConfigVersion + "</ConfigVersion>");
                sb.AppendLine("  <!-- 是否启用。仅当 <EnabledByUser>true</EnabledByUser> 时本项才生效； -->");
                sb.AppendLine("  <!-- 否则（用户从未手动开关过）加载时一律启用。 -->");
                sb.AppendLine("  <Enabled>" + Enabled.ToString().ToLowerInvariant() + "</Enabled>");
                sb.AppendLine("  <!-- 用户是否曾显式开关过：true 后加载时不再覆盖其选择（永远尊重） -->");
                sb.AppendLine("  <EnabledByUser>" + EnabledByUser.ToString().ToLowerInvariant() + "</EnabledByUser>");
                sb.AppendLine("  <!-- 工作模式（二选一）： -->");
                sb.AppendLine("  <!--   IgnoreIme   = 无视输入法模式（★ **当前默认**、用户明确要求）： -->");
                sb.AppendLine("  <!--                 **绝不修改你的输入法状态**（中/英、布局、状态栏都不动）。 -->");
                sb.AppendLine("  <!--                 天正二级命令读的是**原始物理按键**，只能靠下面的 PassThrough -->");
                sb.AppendLine("  <!--                 放行物理键；而它的副作用（输入法也看到 ⇒ 弹候选窗）由 -->");
                sb.AppendLine("  <!--                 <BypassImeDuringCommand> 消除：命令期间让 CAD 窗口 -->");
                sb.AppendLine("  <!--                 **无视输入法**（解除窗口与输入法的关联，状态仍然一个字节不动）。 -->");
                sb.AppendLine("  <!--   SwitchIme   = 切换输入法模式（退路，用 XIMEMODE 1 选）： -->");
                sb.AppendLine("  <!--                 命令期间**真的**把输入法按在英文（小狼毫走 WeaselServer /ascii）； -->");
                sb.AppendLine("  <!--                 物理键直达天正、也不弹候选窗，但**改了你的输入法状态**、 -->");
                sb.AppendLine("  <!--                 命令期间输入法会显示为英文 —— 与「绝不碰输入法」的要求相冲突。 -->");
                sb.AppendLine("  <!-- 现场可用命令 XIMEMODE 0|1 即时切换，无需重启。 -->");
                sb.AppendLine("  <WorkMode>" + WorkMode + "</WorkMode>");
                sb.AppendLine("  <!-- 命令执行期间（选项/二级提示）按键的投递方式（**仅在 IgnoreIme 下有意义**， -->");
                sb.AppendLine("  <!-- 切换输入法模式不接管按键）： -->");
                sb.AppendLine("  <!--   PassThrough = ★ **当前默认**：完全放行物理键。 -->");
                sb.AppendLine("  <!--                 第三方命令（天正 RYBZ 的 A/E/D/F/X/Y）读的是**原始按键**， -->");
                sb.AppendLine("  <!--                 **只有物理键才认，四条合成通道一律不认**。 -->");
                sb.AppendLine("  <!--                 副作用（输入法也收到这些键、弹候选窗）由下面 -->");
                sb.AppendLine("  <!--                 <BypassImeDuringCommand> 消除；本插件绝不碰输入法状态。 -->");
                sb.AppendLine("  <!--   PostKey     = 吞键后用窗口消息投给**焦点控件**。 -->");
                sb.AppendLine("  <!--                 ★ 已现场证伪（v1.0.2 上旬曾短暂当默认）：消息进了 AutoCAD -->");
                sb.AppendLine("  <!--                 主线程后被当成**命令栏文本** ⇒ 命令栏留一个小写 a、还要再按 -->");
                sb.AppendLine("  <!--                 空格，而天正读原始按键的通道收不到 ⇒ 二级命令毫无反应。 -->");
                sb.AppendLine("  <!--   PostKeyFrame= 吞键后投给 AutoCAD **前台顶层窗口**（主框架）。 -->");
                sb.AppendLine("  <!--                 实测天正收不到（主框架不是按键处理入口）→ 毫无反应。 -->");
                sb.AppendLine("  <!--   ReplayVk    = 吞键后用真实 VK + 扫描码重放。输入法仍可能弹候选窗， -->");
                sb.AppendLine("  <!--                 且实测对天正 RYBZ **无效**（合成输入不被认）。 -->");
                sb.AppendLine("  <!-- 现场可用命令 XIMEKEY 0|1|2|3 即时切换，无需重启。 -->");
                sb.AppendLine("  <CommandKeyMode>" + CommandKeyMode + "</CommandKeyMode>");
                sb.AppendLine("  <!-- v58：v55 那个「命令期间强制英文」开关已整体撤除。 -->");
                sb.AppendLine("  <!--   「无视输入法模式」**绝不修改你的输入法状态**：它的英文输入靠钩子 -->");
                sb.AppendLine("  <!--   把按键直接投递给命令栏实现（绕过输入法），而不是去切输入法。 -->");
                sb.AppendLine("  <!--   命令期间选项字母的可用性由上面的 <CommandKeyMode> 负责。 -->");
                sb.AppendLine("  <!-- v56：切输入法时优先用**小狼毫自己的命令行接口**，而不是盲按 Shift（默认 true）。 -->");
                sb.AppendLine("  <!--   为什么：Shift 是翻转键，要靠\"知道当前态\"才切得对；小狼毫走 TSF， -->");
                sb.AppendLine("  <!--   IMM32 读不回真实中英态，盲按会不会切对纯属碰运气（实机已钉死会失败， -->");
                sb.AppendLine("  <!--   而且失败还会留下假记录，导致整个会话再也切不动）。 -->");
                sb.AppendLine("  <!--   小狼毫 0.16.0+ 提供：WeaselServer.exe /ascii（英文）、/nascii（中文）， -->");
                sb.AppendLine("  <!--   是**幂等设值**而非翻转，不依赖焦点窗口，也不需要回读确认。 -->");
                sb.AppendLine("  <!--   找不到 WeaselServer.exe 时会自动退回真实 Shift，非小狼毫用户不受影响。 -->");
                sb.AppendLine("  <!--   关掉它（false）→ 行为回到 v55（仅真实 Shift）。 -->");
                sb.AppendLine("  <UseWeaselCli>" + UseWeaselCli.ToString().ToLowerInvariant() + "</UseWeaselCli>");
                sb.AppendLine("  <!-- v6：命令执行期间让 CAD 窗口**无视输入法**（默认 true）。 -->");
                sb.AppendLine("  <!--   为什么需要：天正这类第三方命令的二级选项只认真实物理按键，所以必须 -->");
                sb.AppendLine("  <!--   放行物理键（见上面的 PassThrough）；而放行时**输入法也会收到这些键** ⇒ -->");
                sb.AppendLine("  <!--   中文态下弹候选窗。本项把 CAD 主窗口及其子窗口与**输入法上下文的关联** -->");
                sb.AppendLine("  <!--   解除（ImmAssociateContext，微软 KB Q171154），命令结束立刻精确还原。 -->");
                sb.AppendLine("  <!--   ★ 它**不修改输入法状态**（中/英、键盘布局、状态栏一律不动），只是让 -->");
                sb.AppendLine("  <!--     窗口不接收输入法 —— 这正是「无视输入法模式」的字面含义。 -->");
                sb.AppendLine("  <!--   生效范围：仅「无视输入法模式」+ 命令执行期间 + 非文字类命令； -->");
                sb.AppendLine("  <!--   文字类命令（TEXT/MTEXT/天正文字…）与文字编辑态**刻意不生效**， -->");
                sb.AppendLine("  <!--   保证打标注时输入法照常工作。 -->");
                sb.AppendLine("  <!--   改 false → 回到「输入法会弹候选窗」的旧行为（天正照样能用）。 -->");
                sb.AppendLine("  <BypassImeDuringCommand>" + BypassImeDuringCommand.ToString().ToLowerInvariant() + "</BypassImeDuringCommand>");
                sb.AppendLine("  <!-- 执行完毕后需要输入中文的命令白名单（小写，不含前导横线） -->");
                sb.AppendLine("  <ChineseCommands>");
                foreach (var c in ChineseCommands)
                    sb.AppendLine("    <Command>" + EscapeXml(c) + "</Command>");
                sb.AppendLine("  </ChineseCommands>");
                sb.AppendLine("</Xime>");

                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 保存失败不影响运行
            }
        }

        private static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&apos;");
        }

        #endregion

        /// <summary>某个命令是否需要输入中文。</summary>
        public bool NeedChinese(string commandName)
        {
            return MatchChinese(commandName) != ChineseNeed.None;
        }

        /// <summary>
        /// 判断命令与「需要中文」的匹配级别（见 <see cref="ChineseNeed"/>）。
        ///
        /// 调用方据此决定**是否提前把输入法切成中文**：
        /// 只有 <see cref="ChineseNeed.Exact"/>（用户/作者明确列在白名单里）
        /// 才值得提前切；<see cref="ChineseNeed.Keyword"/> 是按名字猜的，
        /// 猜错会把输入法切走、害得命令的选项键失灵（RYBZ 事故），
        /// 所以等焦点真正进入文字编辑框时再由编辑态逻辑去切。
        /// </summary>
        public ChineseNeed MatchChinese(string commandName)
        {
            if (string.IsNullOrEmpty(commandName))
                return ChineseNeed.None;

            // 去掉 AutoCAD 命令的前导横线 / 下划线（如 _TEXT、-LAYER）
            var cmd = commandName.Trim().TrimStart('_', '-').ToLowerInvariant();
            if (cmd.Length == 0) return ChineseNeed.None;

            // 1) 精确命中白名单
            if (ChineseCommands.Contains(cmd))
                return ChineseNeed.Exact;

            // 2) 去掉命名空间前缀，例如 "T73_TMText" -> "tmtext"、"T81_TText" -> "ttext"
            var tail = cmd;
            int idx = cmd.IndexOf('_');
            if (idx > 0 && idx < cmd.Length - 1)
            {
                tail = cmd.Substring(idx + 1);
                if (ChineseCommands.Contains(tail))
                    return ChineseNeed.Exact;
            }

            // 3) 关键词兜底：命令名（含去前缀后的名字）包含中文类词根即视为需要中文。
            //    这样无需穷举各第三方插件的命令名（天正 T20/T30、GM 批注等）。
            //    先排除典型「绘图/编辑」词根，避免 line / pline / circle 等被误伤。
            if (IsDrawingLikeName(cmd) || IsDrawingLikeName(tail))
                return ChineseNeed.None;

            foreach (var hint in ChineseKeywordHints)
            {
                if (cmd.IndexOf(hint, StringComparison.Ordinal) >= 0)
                    return ChineseNeed.Keyword;
            }

            return ChineseNeed.None;
        }

        /// <summary>命令名是否属于「绘图 / 编辑」类（用于避免关键词兜底误伤）。</summary>
        private static bool IsDrawingLikeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // 常见纯绘图 / 编辑命令词根；这些命令不需要中文
            string[] drawRoots =
            {
                "line", "pline", "spline", "xline", "mline", "ray",
                "circle", "arc", "ellipse", "rectang", "polygon", "donut",
                "hatch", "fill", "boundary", "region",
                "copy", "move", "mirror", "offset", "array", "rotate", "scale",
                "trim", "extend", "fillet", "chamfer", "break", "stretch",
                "erase", "explode", "join", "align", "divide", "measure",
                "zoom", "pan", "regen", "undo", "redo", "save", "open", "close",
                "plot", "print", "purge", "audit", "osnap", "ortho", "grid", "snap"
            };
            foreach (var r in drawRoots)
            {
                if (name.IndexOf(r, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }
    }
}
