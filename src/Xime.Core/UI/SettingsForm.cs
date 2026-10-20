using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Xime.Core.UI
{
    /// <summary>
    /// X输入法 设置界面。
    ///
    /// 布局参照设计稿：
    ///   左侧（分组面板）：
    ///     帮助    —— 软件版本（由宿主注入，如 v1.0.2）/ 设计 影子（QQ群） / 操作教程
    ///     设置    —— 启用 / 关闭 / 卸载 X输入法
    ///     模式    —— 无视输入法模式（★ 默认） / 切换输入法模式（退路）
    ///   右侧：执行此命令切中文 —— 可编辑的命令白名单（两种模式共用）
    ///
    /// 说明：设计稿中的「在线授权」区域按要求已移除。
    ///
    /// 渲染：以 96 DPI 为设计基准，AutoScaleMode = Dpi —— 高分屏下整个界面
    /// （字号、控件、间距）随系统 DPI 等比放大，避免界面过小或发虚。
    ///
    /// ★ 本类放在 Xime.Core，**不依赖任何 AutoCAD 程序集**：
    ///   正式插件（Xime.Acad）与界面预览工具（Xime.CliTest）加载的是
    ///   **完全相同的一份界面代码**，从根上杜绝"预览图与实际效果不一致"。
    ///   与宿主相关的能力（版本号、命令行输出、卸载注册）全部通过构造参数注入。
    /// </summary>
    public sealed class XimeSettingsForm : Form
    {
        private readonly XimeConfig _config;
        private readonly XimeEngine _engine;

        /// <summary>显示用版本号（宿主注入；预览时用默认值）。</summary>
        private readonly string _version;

        /// <summary>向宿主命令行写消息（预览时为 null）。</summary>
        private readonly Action<string> _writeMessage;

        /// <summary>清除自动加载注册（预览时为 null）。</summary>
        private readonly Action _removeAutoLoad;

        // 控件引用
        private RadioButton _rbIgnoreIme;
        private RadioButton _rbSwitchIme;
        private CheckBox _chkEnabled;
        private Button _btnApplyEnabled;
        private Button _btnDisable;
        private Button _btnUninstall;
        private RichTextBox _txtCommands;
        private TextBox _txtNewCommand;      // 新增命令输入框
        private Button _btnAddCommand;       // 添加命令按钮
        private Button _btnSaveCommands;
        private Button _btnResetCommands;
        private Label _lblStatus;
        private SplitContainer _split;
        private bool _splitInitialized;   // 是否已完成首次分隔位置设置
        private bool _syncingUi;          // 防止复选/按钮联动时相互触发

        /// <summary>
        /// 用户在本面板里点过「卸载 X输入法」（v42）。
        ///
        /// ★ 为什么必须让宿主知道这件事：卸载按钮内部会 <c>_engine.Stop()</c>
        ///   （停轮询 + 焦点监听 + 卸钩子），但配置随即被复位成
        ///   <c>Enabled=true</c>（全新安装状态）。宿主在面板关闭后照例会
        ///   <c>Refresh()</c> 一次 —— 那会把**刚刚卸载掉的插件又启动起来**，
        ///   用户以为卸载了、其实还在跑；更糟的是旧实现只重新 Engage 而没有
        ///   重启定时器，变成"钩子在、焦点监听停"的半死状态，
        ///   无视模式下表现为**双击文字打不出中文**。
        ///
        ///   正确语义：卸载 = 停止 + 清注册 + 配置复位为全新安装；
        ///   之后**重新 NETLOAD** 才按全新安装启动（启用 + 无视输入法模式）。
        /// </summary>
        public bool Uninstalled { get; private set; }

        /// <summary>
        /// 卸载**发生之前**的配置快照（v43；没卸载过时为 null）。
        ///
        /// ★ 为什么要有它：用户提出的验收标准是
        ///   「加载 → 改设置 → 卸载 → 重新加载，重新加载必须回到默认（无视输入法模式）」。
        ///   这条**只看最终状态是判不出来的** —— 最终读到 `Enabled=True /
        ///   EnabledByUser=False / WorkMode=IgnoreIme` 既可能是"卸载复位生效了"，
        ///   也可能"本来就一直是这个值"。
        ///   只有把**卸载前**的值一并留在日志里，才能证明
        ///   "改过的脏值（比如 WorkMode=SwitchIme）确实被抹平了"。
        ///
        /// 宿主（<c>XimeCommands.ShowSettings</c>）在面板关闭后把它写进
        /// <c>%TEMP%\xime_startup.log</c>，于是"问题 1"变成**可机器判定**的。
        /// </summary>
        public string UninstallSnapshot { get; private set; }

        /// <summary>
        /// 宿主没传版本号时（预览工具 / 老调用点）的兜底显示版本。
        ///
        /// ★ 只此一处定义：默认参数值、构造里的兜底都必须用它 ——
        ///   两处各写一个字面量，就是下一位改版本号时漏一处的开始。
        /// </summary>
        internal const string FallbackVersion = "1.0.5";

        /// <param name="config">配置对象（为 null 时使用默认配置）。</param>
        /// <param name="engine">引擎（仅预览时可传 null）。</param>
        /// <param name="version">界面标题与状态条显示的版本号。</param>
        /// <param name="writeMessage">向宿主命令行写消息的回调（可为 null）。</param>
        /// <param name="removeAutoLoad">清除自动加载注册的回调（可为 null）。</param>
        public XimeSettingsForm(XimeConfig config, XimeEngine engine,
                                string version = FallbackVersion,
                                Action<string> writeMessage = null,
                                Action removeAutoLoad = null)
        {
            _config = config ?? new XimeConfig();
            _engine = engine;
            _version = string.IsNullOrEmpty(version) ? FallbackVersion : version;
            _writeMessage = writeMessage;
            _removeAutoLoad = removeAutoLoad;

            BuildUi();
            LoadFromConfig();
            UpdateStatus();
        }

        /// <summary>安全写一条命令行消息（宿主未注入回调时静默忽略）。</summary>
        private void WriteMessage(string message)
        {
            var sink = _writeMessage;
            if (sink == null) return;
            try { sink(message); }
            catch (Exception) { }
        }

        #region UI 构建

        /// <summary>
        /// 窗体加载完成后按比例设置分隔条位置。
        /// 必须在此阶段设置 —— 此时控件已完成布局，宽度可靠。
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ClampToWorkingArea();
            ApplySplitterDistance();
        }

        /// <summary>窗体尺寸变化时按比例维持分隔位置（仅首次自动设置，之后尊重用户拖动）。</summary>
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (!_splitInitialized)
                ApplySplitterDistance();
        }

        // ===================== Esc 关闭界面 =====================

        /// <summary>
        /// 按 Esc 直接关闭设置界面。
        ///
        /// ★ 为什么用 ProcessCmdKey 而不是 OnKeyDown / KeyPreview：
        ///   ProcessCmdKey 属于「命令键预处理」，在**任何子控件拿到按键之前**被调用，
        ///   因此无论焦点当前落在复选框、按钮还是「命令白名单」那个多行文本框里，
        ///   Esc 都能生效。只用 KeyDown 会被获得焦点的子控件先吃掉 ——
        ///   RichTextBox 在焦点里会自己处理按键，表现为「按 Esc 毫无反应」。
        ///
        /// ★ 刻意不做「正在编辑命令列表时先确认」之类的拦截：
        ///   Esc = 退出对话框是 Windows 的通用约定，用户的需求也正是这个。
        ///   命令列表的修改本来就只在点「保存命令列表」时才写盘，
        ///   直接关掉不会丢数据（未保存的编辑本就需要显式保存）。
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                CloseFromKeyboard();
                return true;   // 已处理，不再向下传递
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// 由键盘触发的关闭。兼容两种显示方式：
        ///   · 宿主 AutoCAD 走 <c>Application.ShowModalDialog</c>（模态）
        ///     → 设 DialogResult 结束模态循环；
        ///   · 自测预览器走 <c>form.Show()</c>（非模态）
        ///     → 设 DialogResult 无副作用，由 Close() 收尾。
        /// 两步都执行一次，重复关闭是安全的。
        /// </summary>
        private void CloseFromKeyboard()
        {
            try { DialogResult = DialogResult.Cancel; }
            catch (Exception) { }
            try { Close(); }
            catch (Exception) { }
        }

        /// <summary>
        /// 窗口句柄创建后再强制设一次图标。
        ///
        /// 背景：实际宿主是 AutoCAD 的 Application.ShowModalDialog(form)，
        /// 与独立预览器（form.Show()）走的不是同一条路径 —— 宿主在创建窗口
        /// 句柄的过程中可能覆盖图标。仅在构造函数里给 Icon 赋值不够稳，
        /// 因此句柄就绪后用 WM_SETICON 直接在 Win32 层面钉死标题栏与任务栏图标。
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyAppIcon(handleReady: true);

            // ★ 向引擎登记"这是本插件自己的窗口"。
            //   必要性（现场日志定案）：本面板也属于 AutoCAD 进程，
            //   若不登记，它会被当成"输入法操作靶点"和"可以发真实按键的前台"，
            //   于是切换输入法模式的全部动作都打在面板上、对命令栏毫无作用 ——
            //   用户看到的就是"切换输入法模式根本不起作用"。
            _ownHwnd = Handle;
            XimeEngine.RegisterOwnWindow(_ownHwnd);
        }

        /// <summary>本面板自己的窗口句柄（用于注销登记）。</summary>
        private IntPtr _ownHwnd;

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { XimeEngine.UnregisterOwnWindow(_ownHwnd); }
            catch (Exception) { }
            base.OnFormClosed(e);
        }

        /// <summary>
        /// 应用 X输入法 图标：先走托管属性，再用 WM_SETICON 兜一层。
        /// 任何一步失败都静默跳过，不影响界面其余部分。
        /// </summary>
        /// <param name="handleReady">
        /// 句柄是否已确定创建好（由 OnHandleCreated 传 true）。
        ///
        /// ★ 为什么需要这个参数：构造函数阶段读 <c>this.Handle</c> 会**强制创建句柄**，
        ///   而创建句柄又会触发 OnHandleCreated → 重入本方法，造成无意义的重复投递，
        ///   并且把句柄的创建时机提前到构造函数里（对宿主 Application.ShowModalDialog 不友好）。
        ///   因此构造函数阶段只设托管 <c>Icon</c> 属性，WM_SETICON 一律留给 OnHandleCreated。
        /// </param>
        private void ApplyAppIcon(bool handleReady = false)
        {
            try
            {
                var appIcon = XimeResources.AppIcon;
                if (appIcon != null)
                {
                    try { Icon = appIcon; }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
                return;
            }

            // 句柄尚未创建 → 到此为止，OnHandleCreated 会带着 handleReady=true 再来一次。
            if (!handleReady && !IsHandleCreated) return;

            try
            {
                var hwnd = Handle;
                if (hwnd == IntPtr.Zero) return;

                Icon small, big;
                if (!XimeResources.TryGetSizedIcons(out small, out big)) return;

                Xime.Core.Native.NativeMethods.SendMessage(
                    hwnd, Xime.Core.Native.NativeMethods.WM_SETICON,
                    (IntPtr)Xime.Core.Native.NativeMethods.ICON_SMALL, small.Handle);
                Xime.Core.Native.NativeMethods.SendMessage(
                    hwnd, Xime.Core.Native.NativeMethods.WM_SETICON,
                    (IntPtr)Xime.Core.Native.NativeMethods.ICON_BIG, big.Handle);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 限制窗体不超出当前屏幕工作区。
        ///
        /// 高 DPI 下窗体连同自动缩放会一起放大，在"小屏 + 高缩放"（例如笔记本
        /// 1920×1080 @150%）上有超出屏幕的风险。这里按工作区的 92% 兜底收缩，
        /// 保证标题栏与底部状态条都可见、可操作。
        /// </summary>
        private void ClampToWorkingArea()
        {
            try
            {
                var wa = Screen.FromControl(this).WorkingArea;
                int maxW = (int)(wa.Width * 0.96);
                int maxH = (int)(wa.Height * 0.96);
                if (maxW < 640) maxW = 640;
                if (maxH < 480) maxH = 480;

                int w = Width, h = Height;
                if (w > maxW) w = maxW;
                if (h > maxH) h = maxH;
                if (w != Width || h != Height)
                {
                    // 先放宽下限，避免 MinimumSize 反过来夹死目标尺寸
                    MinimumSize = new Size(
                        Math.Min(MinimumSize.Width, w),
                        Math.Min(MinimumSize.Height, h));
                    Size = new Size(w, h);
                }
                CenterToScreen();
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 安全应用分隔条布局。
        ///
        /// SplitContainer 的约束关系：
        ///   Panel1MinSize &lt;= SplitterDistance &lt;= Width - Panel2MinSize - SplitterWidth
        ///
        /// 因此设置顺序至关重要，且每一步都可能因当前宽度不足而抛异常。
        /// 这里按「先算合法区间 → 再设 MinSize → 最后设 Distance」的顺序，
        /// 并在宽度不足时直接跳过（保持默认布局，不影响功能）。
        /// </summary>
        private void ApplySplitterDistance()
        {
            if (_split == null) return;

            try
            {
                int width = _split.Width;
                if (width <= 0) return;              // 尚未完成布局

                const int leftMin = 460;             // 左侧（设置区）最小宽度
                const int rightMin = 215;            // 右侧（命令列表）最小宽度
                int splitterW = _split.SplitterWidth;

                // 可用宽度不足时不做任何设置，避免抛异常
                if (width < leftMin + rightMin + splitterW)
                    return;

                // 1) 先设置最小尺寸（此时 SplitterDistance 为默认 50，
                //    左侧 420 的 MinSize 会把它顶到合法值，不会触发校验失败）
                _split.Panel1MinSize = leftMin;
                _split.Panel2MinSize = rightMin;

                // 2) 再按比例设置分隔位置。
                //    参照设计稿：右侧命令列表为「竖直细长」栏，只占约 1/4 宽，
                //    左侧设置区占约 3/4。因此分隔位置取 74%。
                int max = width - rightMin - splitterW;
                int desired = (int)(width * 0.74);
                if (desired < leftMin) desired = leftMin;
                if (desired > max) desired = max;

                _split.SplitterDistance = desired;
                _splitInitialized = true;
            }
            catch (Exception)
            {
                // 布局竞争导致的异常直接忽略，下次 SizeChanged 会重试
            }
        }

        private void BuildUi()
        {
            // ★ DPI 自适应：以 96 DPI 为设计基准，在高分屏（125% / 150% / 200%）上由
            //   WinForms 按系统 DPI 等比放大整个界面 —— 否则在小缩放间距下界面会又小又糊。
            //   必须在添加任何子控件之前设置 AutoScaleMode，布局才会参与缩放。
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);

            Text = "X输入法 v" + _version + " - AutoCAD 输入法智能切换";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(1000, 680);
            MinimumSize = new Size(860, 600);
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("微软雅黑", 10F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(240, 244, 249);

            // 窗体图标（标题栏左上角 / 任务栏 / Alt+Tab 切换面板），来自内嵌资源。
            // 句柄创建后 OnHandleCreated 还会用 WM_SETICON 再钉一次（防宿主覆盖）。
            ApplyAppIcon();

            // ---- 主布局：左（设置）| 右（命令列表） ----
            //
            // 重要：SplitContainer 的 SplitterDistance / Panel1MinSize / Panel2MinSize
            // 三者在初始化器里设置会互相触发校验。SplitContainer 默认
            // SplitterDistance=50，而控件初始宽度仅 150，一旦把 Panel2MinSize 设成
            // 220 就会立即抛出「SplitterDistance 必须在 Panel1MinSize 和
            // Width - Panel2MinSize 之间」。
            //
            // 因此这里只设结构性属性，所有尺寸约束在窗体 Load 后由
            // ApplySplitterDistance() 统一按当前实际宽度计算设置。
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(240, 244, 249)
            };
            _split = split;

            split.Panel1.Controls.Add(BuildLeftPanel());
            split.Panel2.Controls.Add(BuildRightPanel());

            Controls.Add(split);

            // ---- 底部状态条 ----
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                // 状态条同时承载左侧 X输入法 logo，因此比纯文字状态条略高，
                // 让 36px 高的字标能完整显示、不糊成一团。
                Height = 40,
                BackColor = Color.FromArgb(226, 233, 242),
                Padding = new Padding(10, 0, 10, 0)
            };
            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(60, 70, 85),
                Text = "就绪"
            };
            // 先放 Dock.Fill 的状态文字，再在左侧插入 logo ——
            // 后加入的 Dock.Left 控件优先占住左侧，状态文字自动让位，互不遮挡。
            bottom.Controls.Add(_lblStatus);

            var logo = XimeResources.Logo;
            if (logo != null)
            {
                bottom.Controls.Add(new PictureBox
                {
                    Dock = DockStyle.Left,
                    // logo 是宽版字标（「X输入法」长宽比约 1.7:1），
                    // 宽度给足 76px，Zoom 才能按高度 36px 撑满，
                    // 字标整体约 62x36 —— 若只给 46px，字标会被压到 26px 高。
                    Width = 76,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Image = logo,
                    BackColor = Color.Transparent,
                    // 右侧留 10px 与状态文字分隔；上下 2px 让 36px 高的 logo 居中
                    Padding = new Padding(0, 2, 10, 2)
                });
            }

            Controls.Add(bottom);
        }

        /// <summary>左侧面板：帮助 / 设置 / 模式。</summary>
        private Control BuildLeftPanel()
        {
            // 左侧容器：不用 AutoScroll —— 否则内部 Dock.Fill 的布局不会按可用高度拉伸
            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 4, 8) };

            // === 帮助分组 ===
            var grpHelp = CreateGroup("帮助", 232);
            var helpBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                // 该属性在 .NET Framework 2.0+ 与 .NET 5+ 中均名为 DetectUrls
                DetectUrls = true,
                Font = new Font("微软雅黑", 10F),
                Text = BuildHelpText(_version)
            };
            helpBox.LinkClicked += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(e.LinkText); }
                catch (Exception) { }
            };
            grpHelp.Controls.Add(helpBox);

            // === 设置分组 ===
            var grpSet = CreateGroup("设置", 138);
            var setPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                // 上下内边距收窄(10→6)：不影响内容，把纵向空间让给帮助区
                Padding = new Padding(12, 6, 12, 6),
                BackColor = Color.White
            };

            _chkEnabled = new CheckBox
            {
                Text = "启用 X输入法（CAD 窗口内自动控制中英输入）",
                AutoSize = true,
                ForeColor = Color.FromArgb(40, 50, 65),
                Margin = new Padding(0, 2, 0, 10)
            };
            // 复选勾选/取消 与 启用/关闭 按钮双向联动：
            // 单独勾选上方复选框也能直接启动插件（用户反馈的 bug）。
            _chkEnabled.CheckedChanged += (s, e) => OnEnabledCheckChanged();

            var btnRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 8)
            };

            _btnApplyEnabled = CreateButton("启用 X输入法", Color.FromArgb(60, 130, 220), 118);
            _btnApplyEnabled.Click += (s, e) => ApplyEnable(true);

            _btnDisable = CreateButton("关闭 X输入法", Color.FromArgb(140, 150, 165), 118);
            _btnDisable.Click += (s, e) => ApplyEnable(false);

            _btnUninstall = CreateButton("卸载 X输入法", Color.FromArgb(205, 90, 80), 118);
            _btnUninstall.Click += (s, e) => Uninstall();

            btnRow.Controls.Add(_btnApplyEnabled);
            btnRow.Controls.Add(_btnDisable);
            btnRow.Controls.Add(_btnUninstall);

            var tip = new Label
            {
                Text = "提示：关闭会保存状态（重启后仍为关闭）；卸载会清除**当前版本**的自动加载注册并重置配置。",
                AutoSize = true,
                // 放宽换行宽度，常规窗口宽度下这段提示只占一行，节省纵向空间给帮助区
                MaximumSize = new Size(620, 0),
                ForeColor = Color.FromArgb(120, 130, 145),
                Margin = new Padding(0, 4, 0, 0)
            };

            setPanel.Controls.Add(_chkEnabled);
            setPanel.Controls.Add(btnRow);
            setPanel.Controls.Add(tip);
            grpSet.Controls.Add(setPanel);

            // === 模式分组 ===
            var grpMode = CreateGroup("模式", 156);
            var modePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12, 6, 12, 6),
                BackColor = Color.White
            };

            _rbIgnoreIme = new RadioButton
            {
                Text = "无视输入法模式（默认，推荐）",
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(40, 50, 65),
                Margin = new Padding(0, 2, 0, 6)
            };
            _rbIgnoreIme.CheckedChanged += (s, e) => { if (_rbIgnoreIme.Checked) ApplyMode(XimeWorkMode.IgnoreIme); };

            // ★ 「切换输入法模式」已完整实现（v29 起），不再是保留值。
            //   它与无视输入法模式**二选一**，两者动作完全相反：
            //     · 无视输入法：不碰输入法，靠拦截按键实现英文；
            //     · 切换输入法：不接管按键，靠真的切换输入法中英态实现英文。
            //   ★★ v6 起**默认回到无视输入法模式**（用户明确要求：一个模式的核心承诺
            //      就是它的契约，不能替他改成"会动我输入法"的那个）。
            //      天正二级命令的候选窗副作用由「命令期间无视输入法」消除 ——
            //      它只解除窗口与输入法的关联，仍然**一个字节都不动**输入法状态。
            _rbSwitchIme = new RadioButton
            {
                Text = "切换输入法模式（退路；会真的改你的输入法状态）",
                AutoSize = true,
                Checked = false,
                ForeColor = Color.FromArgb(40, 50, 65),
                Margin = new Padding(0, 2, 0, 6)
            };
            _rbSwitchIme.CheckedChanged += (s, e) => { if (_rbSwitchIme.Checked) ApplyMode(XimeWorkMode.SwitchIme); };

            var modeTip = new Label
            {
                Text = "无视输入法模式（默认，推荐）：**绝不改变你的输入法状态**（中/英、键盘布局、状态栏都不动）。"
                     + "天正这类第三方命令的二级提示只认真实物理按键 —— 本模式靠「放行物理键」让它生效"
                     + "（XIMEKEY 2），并在此基础上让 CAD 窗口「无视输入法」：物理键照旧直达天正，"
                     + "而输入法收不到键、不弹候选窗。切换输入法模式：真的把输入法切成英文/中文，"
                     + "天正也能用且不弹候选窗，但代价是**命令期间你的输入法会显示为英文** —— "
                     + "与「绝不碰输入法」的要求相冲突，故只作退路。",
                AutoSize = true,
                MaximumSize = new Size(620, 0),
                ForeColor = Color.FromArgb(120, 130, 145),
                Margin = new Padding(0, 4, 0, 0)
            };

            modePanel.Controls.Add(_rbIgnoreIme);
            modePanel.Controls.Add(_rbSwitchIme);
            modePanel.Controls.Add(modeTip);
            grpMode.Controls.Add(modePanel);

            // 纵向排列三个分组。
            // 使用「帮助区 Dock.Fill + 另两个 Dock.Bottom」的组合，
            // 这样帮助区会自动占满剩余高度，且不受 TableLayoutPanel 百分比行高的限制。
            var bottomStack = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 294,          // 设置 138 + 模式 156
                Padding = new Padding(0)
            };
            // ★ Dock=Bottom 的排布顺序由 z-order 决定：**后加入的控件位于更靠下的位置**。
            //   所以这里先加「设置」再加「模式」，最终得到「上：设置 / 下：模式」，
            //   与预览器 TableLayoutPanel 的行顺序（帮助 → 设置 → 模式）完全一致。
            //
            //   此前写成先加 grpMode 再加 grpSet，实际渲染成「上：模式 / 下：设置」——
            //   这正是用户实测截图与预览图对不上的原因。
            bottomStack.Controls.Add(grpSet);
            bottomStack.Controls.Add(grpMode);

            grpMode.Dock = DockStyle.Bottom;
            // 156：两个单选按钮 + 四行说明文字，留出余量避免说明被裁掉
            grpMode.Height = 156;
            grpSet.Dock = DockStyle.Bottom;
            grpSet.Height = 138;

            grpHelp.Dock = DockStyle.Fill;

            // 先添加填充控件，再添加底部控件（顺序影响 Dock 布局结果）
            root.Controls.Add(grpHelp);
            root.Controls.Add(bottomStack);
            return root;
        }

        /// <summary>
        /// 右侧面板：命令白名单表。
        ///
        /// 布局（自下而上）：
        ///   底部  —— [输入命令文本框] [添加命令] 按钮行
        ///   再上  —— [保存命令列表] [恢复默认] 按钮行
        ///   中间  —— 命令列表（可编辑）
        ///   顶部  —— 标题
        ///
        /// 说明：这份列表**两种模式共用**，但含义略有不同 ——
        ///   · 无视输入法模式：命中即"停止拦截"（命令期间允许中文）；
        ///   · 切换输入法模式：命中即"把输入法切成中文"，命令结束再切回英文。
        ///   两者都是"执行这些命令时需要输入中文"的意思，所以共用一份列表。
        /// </summary>
        private Control BuildRightPanel()
        {
            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 8, 8, 8) };

            // ---- 顶部标题 ----
            // ★ Label 无 WordWrap 属性；要「同一句话在一行显示、宽度不够自动换行」，
            //   只能 AutoSize=true + MaximumSize 限制宽度。而 Dock 与 AutoSize 冲突，
            //   因此用一个 Dock=Top 的容器包裹 AutoSize 的 Label。
            var titleWrap = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(0, 2, 0, 0)
            };
            var lbl = new Label
            {
                AutoSize = true,
                Text = "执行此命令切中文（两种模式共用此列表，可自行增删）",
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(40, 50, 65),
                Font = new Font("微软雅黑", 10F, FontStyle.Bold)
            };
            // 换行宽度随右栏实际宽度自适应
            titleWrap.Layout += (s, e) =>
            {
                lbl.MaximumSize = new Size(Math.Max(100, titleWrap.ClientSize.Width), 0);
            };
            titleWrap.Controls.Add(lbl);

            // ---- 命令列表主体（可编辑） ----
            _txtCommands = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                Font = new Font("Consolas", 10.5F),
                ScrollBars = RichTextBoxScrollBars.Both,
                WordWrap = false,
                AcceptsTab = true
            };

            // ---- 底部第二行：新增命令输入框 + 添加命令 ----
            //
            // 布局要求：「添加命令」按钮与上一行「恢复默认」按钮尺寸一致且左右对齐，
            // 因此按钮放在本行【左侧】、宽度取可用宽的一半（与「恢复默认」相同），
            // 输入框放在右侧占另一半。颜色保持绿色不变。
            const int addRowHeight = 32;      // 与「恢复默认」等高（CreateButton 默认 32）
            const int addGap = 8;             // 输入框与按钮之间的间距

            var addRow = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = addRowHeight + 12,   // 上下留白（顶部 8 与上一行一致）
                Padding = new Padding(0, 8, 0, 4)
            };

            _btnAddCommand = CreateButton("添加命令", Color.FromArgb(76, 160, 110), 96);
            _btnAddCommand.Dock = DockStyle.Left;     // 左对齐，与「恢复默认」同列
            _btnAddCommand.Height = addRowHeight;
            _btnAddCommand.Click += (s, e) => AddCommandFromInput();

            _txtNewCommand = new TextBox
            {
                Dock = DockStyle.Fill,               // 占右侧剩余空间
                Font = new Font("Consolas", 10.5F),
                // 高度随字号固定，避免自动增高
            };
            ApplyPlaceholder(_txtNewCommand, "输入命令名后回车或点「添加命令」，如 TEXT");
            _txtNewCommand.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    AddCommandFromInput();
                }
            };

            // 「添加命令」宽度 = 可用宽的一半（与「恢复默认」完全一致）
            addRow.Layout += (s, e) =>
            {
                ResizeNewCommandBox(addRow, _btnAddCommand, _txtNewCommand, addGap);
            };

            // 先加按钮（Left，靠左）再加输入框（Fill，占右侧）
            addRow.Controls.Add(_txtNewCommand);
            addRow.Controls.Add(_btnAddCommand);

            // ---- 底部第一行：保存命令列表 + 恢复默认 ----
            // 右栏较窄，两个按钮改为等分可用宽度，避免换行或文字截断。
            var btnRow = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                Padding = new Padding(0, 8, 0, 0)
            };

            _btnSaveCommands = CreateButton("保存命令列表", Color.FromArgb(60, 130, 220), 128);
            _btnSaveCommands.Dock = DockStyle.Left;
            _btnSaveCommands.Click += (s, e) => SaveCommands();

            _btnResetCommands = CreateButton("恢复默认", Color.FromArgb(140, 150, 165), 96);
            _btnResetCommands.Dock = DockStyle.Left;
            _btnResetCommands.Click += (s, e) => ResetCommands();

            btnRow.Layout += (s, e) =>
            {
                int avail = btnRow.ClientSize.Width - btnRow.Padding.Horizontal;
                if (avail <= 0) return;
                int gap = 8;
                int half = (avail - gap) / 2;
                if (half < 60) half = 60;
                _btnSaveCommands.Width = half;
                _btnResetCommands.Width = half;
            };

            btnRow.Controls.Add(_btnResetCommands);
            btnRow.Controls.Add(_btnSaveCommands);

            root.Controls.Add(_txtCommands);
            root.Controls.Add(btnRow);
            root.Controls.Add(addRow);
            root.Controls.Add(titleWrap);
            return root;
        }

        /// <summary>
        /// 按父容器实际宽度把新增命令输入框与「添加命令」按钮设为等分可用宽度。
        /// 「添加命令」按钮宽度与「恢复默认」完全一致（= 可用宽的一半，扣除间距）；
        /// 按钮 Dock=Left 靠左、输入框 Dock=Fill 占右侧，二者间留出 gap。
        /// </summary>
        private static void ResizeNewCommandBox(Panel row, Button btn, TextBox box, int gap)
        {
            if (row == null || btn == null || box == null) return;
            try
            {
                int avail = row.ClientSize.Width - row.Padding.Horizontal;
                if (avail <= 0) return;

                // 与「恢复默认」同一算法：可用宽的一半（扣除间距）
                int half = (avail - gap) / 2;
                if (half < 60) half = 60;                       // 下限，保证可用
                btn.Width = half;
                // 输入框由 Dock=Fill 自动占满剩余空间，无需手动设宽度
            }
            catch (Exception) { }
        }

        /// <summary>占位提示文本（也是老框架下判定“当前是否仅显示提示”的依据）。</summary>
        private const string NewCommandHint = "输入命令名后回车或点「添加命令」，如 TEXT";

        /// <summary>标记输入框当前是否处于“仅显示占位提示”状态。</summary>
        private bool _newCommandShowingHint;

        /// <summary>为 TextBox 设置占位提示（兼容 net35/net48 无 PlaceholderText 的情况）。</summary>
        private void ApplyPlaceholder(TextBox box, string hint)
        {
            var prop = typeof(TextBox).GetProperty("PlaceholderText");
            if (prop != null)
            {
                prop.SetValue(box, hint, null);
                _newCommandShowingHint = false;
                return;
            }
            // 老框架降级：用灰字 + 焦点事件模拟
            box.ForeColor = Color.FromArgb(150, 158, 170);
            box.Text = hint;
            _newCommandShowingHint = true;
            box.GotFocus += (s, e) =>
            {
                if (_newCommandShowingHint)
                {
                    box.Text = string.Empty;
                    box.ForeColor = Color.FromArgb(40, 50, 65);
                    _newCommandShowingHint = false;
                }
            };
            box.LostFocus += (s, e) =>
            {
                if (box.Text.Length == 0)
                {
                    box.ForeColor = Color.FromArgb(150, 158, 170);
                    box.Text = hint;
                    _newCommandShowingHint = true;
                }
            };
        }

        /// <summary>把输入框里的命令追加到命令列表（去重、规范化）。</summary>
        private void AddCommandFromInput()
        {
            if (_txtNewCommand == null) return;

            // 仅显示占位提示时不处理
            if (_newCommandShowingHint) return;

            var raw = (_txtNewCommand.Text ?? string.Empty).Trim();
            if (raw.Length == 0) return;

            var cmd = raw.ToLowerInvariant().TrimStart('_', '-');

            // 逐行追加到列表，重复的跳过
            var existing = new System.Collections.Generic.HashSet<string>(
                _txtCommands.Text.Replace("\r\n", "\n").Split('\n'),
                StringComparer.OrdinalIgnoreCase);
            // Split 会产生空串，去掉
            existing.Remove(string.Empty);

            if (existing.Contains(cmd))
            {
                UpdateStatus("命令「" + cmd + "」已存在于列表中。");
                return;
            }

            var t = _txtCommands.Text;
            if (t.Length > 0 && !t.EndsWith("\n")) t += "\r\n";
            t += cmd + "\r\n";
            _txtCommands.Text = t;
            _txtCommands.SelectionStart = _txtCommands.TextLength;
            _txtCommands.ScrollToCaret();

            // 清空输入框（老框架下若输入框已失去焦点，则恢复灰字提示）
            var prop = typeof(TextBox).GetProperty("PlaceholderText");
            if (prop != null)
            {
                _txtNewCommand.Text = string.Empty;
            }
            else
            {
                _txtNewCommand.Text = string.Empty;
                if (!_txtNewCommand.Focused)
                {
                    _txtNewCommand.ForeColor = Color.FromArgb(150, 158, 170);
                    _txtNewCommand.Text = NewCommandHint;
                    _newCommandShowingHint = true;
                }
            }
            // 让焦点回到输入框，方便连续添加
            _txtNewCommand.Focus();

            UpdateStatus("已添加命令：「" + cmd + "」（点「保存命令列表」生效）。");
        }

        private GroupBox CreateGroup(string title, int height)
        {
            return new GroupBox
            {
                Text = title,
                Dock = DockStyle.Fill,
                Height = height,
                ForeColor = Color.FromArgb(50, 62, 78),
                BackColor = Color.White,
                Font = new Font("微软雅黑", 10F, FontStyle.Bold),
                Padding = new Padding(6),
                Margin = new Padding(0, 0, 0, 8)
            };
        }

        private Button CreateButton(string text, Color back, int width)
        {
            // 使用自绘圆角按钮（圆角半径 6px），统一所有按钮外观
            var btn = new RoundedButton
            {
                Text = text,
                Width = width,
                Height = 32,
                NormalColor = back,
                ForeColor = Color.White,
                Font = new Font("微软雅黑", 9.5F),
                Margin = new Padding(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                CornerRadius = 6
            };
            return btn;
        }

        /// <summary>
        /// 帮助文本 —— 按需求仅显示：版本、作者、操作教程三项（项间不留空行）。
        ///
        /// ★ 版本号由调用方传入，**不许在正文里写死**：
        ///   写死过一次（"v1.0.0"），结果 v1.0.2 改版本号时这里被漏掉 ——
        ///   标题栏写着 v1.0.2、帮助里还写着 v1.0.0，用户第一眼就看见自相矛盾。
        ///
        /// ★ 标 <c>internal</c> 是为了让自测（XimeTest）**直接对文本本身**断言
        ///   （传一个假版本号进去，回显的必须是那个假版本号）——
        ///   走控件树去找那个富文本框太脆，一改布局就找不到了。
        /// </summary>
        internal static string BuildHelpText(string version)
        {
            var sb = new StringBuilder();
            sb.AppendLine("1、软件版本");
            sb.AppendLine("   v" + (string.IsNullOrEmpty(version) ? "?" : version));
            sb.AppendLine("2、设计");
            sb.AppendLine("   影子");
            sb.AppendLine("   QQ群：1128105014");
            sb.AppendLine("3、操作教程");
            sb.AppendLine("   ① 使用 NETLOAD 命令加载 Xime.dll（首次加载建议勾选自动加载）。");
            sb.AppendLine("   ② 加载后默认自动启用：首次 NETLOAD 后立即启用，之后每次重启 AutoCAD 自动加载时同样默认启用，无需手动开启；输入命令 xime 可打开本设置界面。在设置界面里按 Esc 可直接关闭本界面。");
            sb.AppendLine("   ③ 在「设置」区可勾选或取消「启用 X输入法」来开/关插件（与下方按钮等效）。注意：关掉之后会被记住，之后每次启动 AutoCAD 插件都是关闭状态；需要恢复请再勾选一次，或执行 XIMEON 命令（开启后同样会被记住）。");
            sb.AppendLine("   ④ 切换到 CAD 窗口后无需手动切换输入法：");
            sb.AppendLine("      · 绘图命令期间自动保持英文，可直接输入英文命令；");
            sb.AppendLine("      · 执行文字类命令（如 TEXT、MTEXT、DDEDIT、天正文字命令）时可输入中文；");
            sb.AppendLine("      · 双击文字对象进入编辑时同样可输入中文（原生文字与天正文字均支持）；");
            sb.AppendLine("      · 需要在标注里打英文：自己按一下 Shift 切换输入法即可，插件全程不打断；");
            sb.AppendLine("      · 下一段文字输入（或下一个文字类命令）开始时，插件会自动切回中文；");
            sb.AppendLine("      · 最小化 CAD 或切到其他程序，插件自动退出并恢复本地输入法状态。");
            sb.AppendLine("   ⑤ 开启大写锁定（CapsLock）时插件不干预输入法。");
            sb.AppendLine("   ⑥ 命令执行期间（例如天正「任意布置」给出 A/E/D/F/X-Y 这类二级提示）的按键投递方式，默认 = **完全放行物理键**（XIMEKEY 2 = PassThrough）。");
            sb.AppendLine("      ★ 为什么是它：天正这类第三方命令的二级提示读的是**原始物理按键**；**四条合成通道（XIMEKEY 0 / 1 / 3 以及 UNICODE 注入）天正一律不认**，只有放行物理键才认。");
            sb.AppendLine("      · 输入 1 = 投窗口消息给焦点控件（PostKey）：**已现场证伪** —— 消息会变成命令栏文本（留一个小写 a、还要再按一次空格），天正读原始按键的通道收不到 ⇒ 二级命令毫无反应。");
            sb.AppendLine("      · 输入 3 = 投给前台顶层窗口（PostKeyFrame）：已证伪，天正收不到，毫无反应。");
            sb.AppendLine("      · 输入 0 = 重放真实按键（ReplayVk）：已证伪，天正不认，且输入法照样能看到。");
            sb.AppendLine("      ★ 本项**只在「无视输入法模式」下才有意义**；「切换输入法模式」不接管按键，用不到它。四档都编在同一个 DLL 里，随时切换、立即生效并保存。");
            sb.AppendLine("   ⑦ 「命令期间无视输入法」（默认开，XIMEBYPASS 查看 / 开关）—— 它专门消除上面「放行物理键」的唯一副作用（输入法也收到键、弹候选窗）：");
            sb.AppendLine("      · 做法：命令执行期间，把 AutoCAD 主窗口及其子窗口与**输入法上下文的关联**解除（ImmAssociateContext，微软 KB Q171154），命令结束立刻精确还原；");
            sb.AppendLine("      · 效果：物理键照旧直达天正（天正二级命令照常可用），而输入法收不到这些键 ⇒ **不弹候选窗、不吞字母**；");
            sb.AppendLine("      · ★★ 它**不修改你的输入法状态**：中/英、键盘布局、候选词库、输入法状态栏，一个字节都不动。这与「切换输入法模式」有本质区别（那个是真的把输入法切成英文）。");
            sb.AppendLine("      · 生效范围（四条全过才动手）：无视输入法模式 + 引擎已接管 + 命令执行期间 + **非**文字类命令；");
            sb.AppendLine("        正在输入中文内容的场合（TEXT/MTEXT/天正文字等文字类命令、以及焦点在文字编辑框时）**刻意不生效**，保证打标注时输入法照常工作。");
            sb.AppendLine("      · 现场想立刻验证：执行 XIMEBYPASS 1（马上解除）→ 回 CAD 敲天正二级命令 → 执行 XIMEBYPASS 0（务必还原）。");
            sb.AppendLine("      · 若探针显示「已解除关联窗口 = 0」，说明你这台机器上这些窗口本来就没有输入法上下文 —— 此时还有第二层兜底：焦点**不在命令行输入框**时，按键一律放行给系统、由你自己的输入法负责。请把这一行连同日志发给作者。");
            sb.AppendLine("   ⑧ 「切换输入法模式」（在「模式」区二选一，**退路**）：不接管按键，而是真的去切输入法的中英文状态（等价于你手动按一下 Shift，键盘布局不变）。");
            sb.AppendLine("      · 进入 CAD：把输入法保持在英文；");
            sb.AppendLine("      · 执行上面白名单里的命令：切成中文；命令结束或退出：切回英文并保持；");
            sb.AppendLine("      · 最小化 CAD 或切到别的程序、以及退出 AutoCAD：恢复你原来的输入法状态。");
            sb.AppendLine("      ★ 它也能让天正二级命令「按一下 A 立刻生效 + 不弹候选窗 + 命令栏不留字母」（2026-10-06 现场实测三项全过），");
            sb.AppendLine("        但代价是**命令期间你的输入法会显示为英文** —— 这正是用户否决它作默认的原因：本插件的立身之本是不动别人的输入法。");
            sb.AppendLine("      ★ 若你的输入法不支持状态读写（少数自绘 / TSF 输入法），该模式无法生效 —— 此时状态栏会提示「不可探测」；");
            sb.AppendLine("        改用无视输入法模式并把 XIMEKEY 设为 2 即可。执行 XIMEPROBE 可以当场检测你的输入法支持哪条通道。");
            return sb.ToString();
        }

        #endregion

        #region 配置读写

        private void LoadFromConfig()
        {
            // 初始化期间屏蔽联动，避免设置复选框时反向写回配置
            _syncingUi = true;
            try
            {
                _chkEnabled.Checked = _config.Enabled;
                _rbIgnoreIme.Checked = _config.WorkMode == XimeWorkMode.IgnoreIme;
                _rbSwitchIme.Checked = _config.WorkMode == XimeWorkMode.SwitchIme;
            }
            finally
            {
                _syncingUi = false;
            }
            LoadCommands();
        }

        private void LoadCommands()
        {
            var lines = new StringBuilder();
            foreach (var c in _config.ChineseCommands)
                lines.AppendLine(c);
            _txtCommands.Text = lines.ToString();
        }

        private void SaveCommands()
        {
            _config.ChineseCommands.Clear();
            var raw = _txtCommands.Text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in raw)
            {
                var c = (line ?? string.Empty).Trim().ToLowerInvariant();
                if (c.Length > 0 && !c.StartsWith("//"))
                    _config.ChineseCommands.Add(c.TrimStart('_', '-'));
            }
            _config.Save();
            LoadCommands();       // 回写规范化结果
            UpdateStatus("命令列表已保存，共 " + _config.ChineseCommands.Count + " 条。");
        }

        private void ResetCommands()
        {
            var def = new XimeConfig();       // 构造默认值
            _config.ChineseCommands.Clear();
            foreach (var c in def.ChineseCommands)
                _config.ChineseCommands.Add(c);
            _config.Save();
            LoadCommands();
            UpdateStatus("命令列表已恢复默认。");
        }

        /// <summary>
        /// 复选框勾选状态变化：把「启用」状态同步给插件。
        ///
        /// 这条路径让用户**单独勾选/取消**上方复选框也能直接启动/停用插件，
        /// 不必再点下面的按钮（此前复选框没有绑定事件，勾了没反应 —— 已修复）。
        /// </summary>
        private void OnEnabledCheckChanged()
        {
            if (_syncingUi) return;                  // 由按钮联动触发，无需重复处理
            ApplyEnable(_chkEnabled.Checked, byUser: true);
        }

        /// <summary>
        /// 应用启用状态。
        /// enabled  —— 目标启用状态；
        /// byUser   —— 是否由用户操作触发（true 则记录 EnabledByUser，之后加载不再覆盖）。
        /// 无论从复选框还是按钮进入，都会把复选框与插件状态同步一致。
        /// </summary>
        private void ApplyEnable(bool enabled, bool byUser = true)
        {
            _syncingUi = true;
            try
            {
                _config.Enabled = enabled;
                if (byUser) _config.EnabledByUser = true;

                // ★ 刻意**不**在这里重写 _config.WorkMode。
                //   工作模式由「模式」区的单选按钮（ApplyMode）单独维护，
                //   启用/关闭与模式选择是两件独立的事。早先这里根据单选按钮
                //   重写一次 WorkMode，一旦单选按钮与配置短暂不同步，就会把
                //   用户好不容易选定的模式悄悄重置回默认 —— 这正是「手动切到
                //   切换输入法模式后，点一下启用/关闭，模式又跳回无视输入法」的隐患。

                // 同步复选框显示（按钮点击时也要把复选框勾上/取消）
                if (_chkEnabled.Checked != enabled)
                    _chkEnabled.Checked = enabled;

                _config.Save();

                if (_engine != null)
                    _engine.Refresh();
            }
            finally
            {
                _syncingUi = false;
            }

            UpdateStatus(enabled
                ? "已启用 X输入法（" + (_config.WorkMode == XimeWorkMode.IgnoreIme ? "无视输入法模式" : "切换输入法模式") + "）。"
                : "已关闭 X输入法，本地输入法状态已恢复。");

            // 关闭时额外在命令行给出提醒。
            //
            // 背景：关闭状态会被写入配置文件持久化 —— 重启 AutoCAD 后插件虽然照常
            // 「加载」，但处于关闭状态不会工作（日志表现为 "Engage 跳过：未启用"）。
            // 这很容易被误判为「插件没加载 / 自动加载失败」，因此这里明确告知状态
            // 会被保存，并给出恢复方法。
            if (!enabled)
            {
                WriteMessage(
                    "\n[X输入法] 已关闭插件。⚠ 该状态会被保存：重启 AutoCAD 后插件仍为关闭状态"
                    + "（只是加载、不工作）。需要时执行 XIMEON，或在界面里勾选「启用 X输入法」即可恢复。");
            }
        }

        private void ApplyMode(XimeWorkMode mode)
        {
            // ★ 初始化期间（LoadFromConfig 设置单选按钮）会触发 CheckedChanged，
            //   此时不该再 Save + Refresh —— 那会把"打开界面"变成一次多余的
            //   接管/收尾动作，且可能引发状态抖动。真正由用户点选才处理。
            if (_syncingUi) return;

            _config.WorkMode = mode;
            _config.Save();
            if (_engine != null) _engine.Refresh();
            UpdateStatus("模式已切换为：" + (mode == XimeWorkMode.IgnoreIme ? "无视输入法模式" : "切换输入法模式"));
        }

        private void Uninstall()
        {
            var r = MessageBox.Show(
                "确定要卸载 X输入法 吗？\r\n\r\n" +
                "· 将关闭插件并恢复本地输入法状态\r\n" +
                "· 将删除**当前这个 AutoCAD 版本**的自动加载注册表项\r\n" +
                "  （本机其它 CAD 版本的注册项原样保留，不受影响）\r\n" +
                "· 将把配置重置为默认（相当于全新安装）\r\n" +
                "· DLL 文件本身需手动删除",
                "卸载 X输入法",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (r != DialogResult.Yes) return;

            // ★ v43：先抓"卸载前"的配置快照 —— 必须在动 _config / _engine.Config 之前，
            //   否则拿到的是被卸载流程自己改过的值（Enabled 已被强制 false），证明不了什么。
            //   这是一次纯读取；写日志由宿主在面板关闭后完成。
            try
            {
                UninstallSnapshot = _config.DescribeState();
            }
            catch (Exception) { UninstallSnapshot = "(快照读取失败)"; }

            try
            {
                if (_engine != null)
                {
                    _engine.Config.Enabled = false;
                    _engine.Refresh();
                    _engine.Stop();
                }

                // 清除 AutoCAD 自动加载注册（宿主注入的能力；预览时为 null）
                if (_removeAutoLoad != null) _removeAutoLoad();

                // ★★ 卸载 = 「把插件从这台机器上抹掉」，因此配置文件也要回到
                //    **全新安装时的默认状态** —— 不是「用户手动关闭」那个状态。
                //
                //    区别是决定性的（用户实测反馈）：用户手动点「关闭」写的是
                //    Enabled=false + EnabledByUser=true，这是"关了就该一直关着"
                //    需要的语义；而卸载若照旧写这两项，下次重新 NETLOAD 会被当成
                //    "用户关过"，于是**加载后默认不启用** —— 这正是用户看到的
                //    「卸载之后再加载不会默认开启」。
                //    复位逻辑统一放在 XimeConfig.ResetToFreshInstall()，
                //    界面与自测（XimeTest config）共用同一处定义，不会各自漂移。
                _config.ResetToFreshInstall();
                _config.Save();

                // ★ v42：告诉宿主"本次面板里发生了卸载" —— 面板关闭后宿主
                //   不得再 Refresh（那会把刚卸载的插件重新启动起来）。
                Uninstalled = true;

                _syncingUi = true;
                try
                {
                    _chkEnabled.Checked = false;
                    // 模式单选也复位到默认（无视输入法），让界面和配置文件一致
                    if (_rbIgnoreIme != null) _rbIgnoreIme.Checked = true;
                }
                finally { _syncingUi = false; }

                UpdateStatus("已卸载：自动加载注册已清除，配置已重置为默认；重新加载后会默认启用。");
                MessageBox.Show(
                    "X输入法 已卸载。\r\n" +
                    "自动加载注册表项已清除，重启 AutoCAD 后不再自动加载。\r\n" +
                    "配置已重置为默认 —— 之后重新 NETLOAD 加载，会当作全新安装：\r\n" +
                    "默认启用、默认使用「无视输入法模式」。",
                    "卸载完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("卸载过程中出现异常：" + ex.Message, "卸载",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateStatus()
        {
            if (_engine == null) { _lblStatus.Text = "引擎未启动"; return; }

            // 切换输入法模式下，把"输入法能不能读写状态"直接摆到状态栏上 ——
            // 该模式的全部功能都建立在它之上，读不出来就该当场看见，
            // 而不是等用户折腾半天才发现"切了没反应"。
            string extra = string.Empty;
            if (_config.WorkMode == XimeWorkMode.SwitchIme)
                extra = _engine.SwitchImeDetectable
                    ? "  |  输入法可探测：是"
                    : "  |  输入法可探测：否（此模式无效，请改用无视输入法模式）";

            _lblStatus.Text = string.Format(
                "版本 v{0}  |  启用：{1}  |  模式：{2}  |  接管中：{3}  |  前台输入法：{4}{5}",
                _version,
                _config.Enabled ? "是" : "否",
                _config.WorkMode == XimeWorkMode.IgnoreIme ? "无视输入法" : "切换输入法",
                _engine.IsEngaged ? "是" : "否",
                _engine.Ime.LastKnownMode,
                extra);
        }

        private void UpdateStatus(string custom)
        {
            UpdateStatus();
            _lblStatus.Text = custom + "    |    " + _lblStatus.Text;
        }

        #endregion
    }

    /// <summary>
    /// 圆角按钮。用自绘方式画背景与文字，支持按下/悬停的轻微变色，
    /// 在所有目标框架（net35 / net48 / net8）下表现一致。
    /// </summary>
    public sealed class RoundedButton : Button
    {
        private Color _normalColor = Color.FromArgb(60, 130, 220);
        private bool _hover;
        private bool _pressed;

        /// <summary>常态背景色。</summary>
        public Color NormalColor
        {
            get { return _normalColor; }
            set { _normalColor = value; Invalidate(); }
        }

        /// <summary>圆角半径。</summary>
        public int CornerRadius { get; set; }

        public RoundedButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);
            CornerRadius = 6;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            base.OnMouseDown(mevent);
            _pressed = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            base.OnMouseUp(mevent);
            _pressed = false;
            Invalidate();
        }

        /// <summary>亮度调整，用于悬停/按下的配色变化。</summary>
        private static Color Shade(Color c, int delta)
        {
            int r = c.R + delta, g = c.G + delta, b = c.B + delta;
            return Color.FromArgb(c.A,
                r < 0 ? 0 : (r > 255 ? 255 : r),
                g < 0 ? 0 : (g > 255 ? 255 : g),
                b < 0 ? 0 : (b > 255 ? 255 : b));
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 父容器背景色（实现圆角外露部分的“透明”效果）
            Color parentBack = Parent != null ? Parent.BackColor : BackColor;
            if (parentBack == Color.Transparent) parentBack = SystemColors.Control;

            g.Clear(parentBack);

            Color fill = _normalColor;
            if (!Enabled) fill = Color.FromArgb(200, 205, 212);
            else if (_pressed) fill = Shade(_normalColor, -25);
            else if (_hover) fill = Shade(_normalColor, 18);

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = CornerRadius * 2;
            if (rad > rect.Height) rad = rect.Height;
            if (rad < 2) rad = 2;

            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(rect.Left, rect.Top, rad, rad, 180, 90);
                path.AddArc(rect.Right - rad, rect.Top, rad, rad, 270, 90);
                path.AddArc(rect.Right - rad, rect.Bottom - rad, rad, rad, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - rad, rad, rad, 90, 90);
                path.CloseFigure();

                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
            }

            // 文字
            Color textColor = Enabled ? ForeColor : Color.FromArgb(240, 242, 245);
            TextRenderer.DrawText(g, Text, Font, rect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis);
        }
    }
}
