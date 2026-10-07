# X输入法 v1.0.2

AutoCAD 平台输入法智能切换插件 —— **无视输入法模式**

作者：影子

---

## 一、这个插件解决什么问题

在 AutoCAD 里画图，最大的输入法痛点是：

- 敲命令时要英文，输入法却停在中文，字母全被吞掉；
- 想输入文字（TEXT / MTEXT）时，又得手动切回中文；
- 来回按 Shift 切中英，一天几百次，烦且容易出错。

**X输入法** 让你完全不用管输入法：

| 场景           | 插件行为               |
| ------------ | ------------------ |
| 进入 CAD 窗口    | 自动保证命令栏能输入英文命令     |
| 命令执行完（绘图类）   | 保持英文，方便连续敲命令       |
| 命令执行完（文字类）   | 自动切中文，可以直接打字       |
| 最小化 / 切到别的程序 | 自动退出接管，恢复你本来的输入法状态 |
| 开启 CapsLock  | 完全不干预，尊重你的大写输入     |

**关键特性：绝不切换你的键盘布局。** 插件不会把输入法强行切到"美式键盘"，你的输入法（搜狗、微信、微软拼音等）始终是激活状态。

---

## 二、技术实现

### 核心机制：放行物理键 + 命令期间无视输入法

插件**不切换你的输入法，也不改写按键**。做法分两层。

**第一层：按键原样放行（`CommandKeyMode.PassThrough`）**

命令期间，插件不拦截、不吞掉、不重新投递 —— 你按的物理键**原封不动**送达 AutoCAD。
这是唯一一条被验证过"天正等第三方平台也认"的通道。

> 曾经试过把英文命令字符重新投递进命令栏。实测四条**合成**通道
> （`PostKey` / `PostKeyFrame` / `ReplayVk` / `SendUnicodeInput`）在 AutoCAD 本体上可用，
> 但天正一律不认：投出去的消息被当成命令栏文本，命令栏里会留下一个小写 `a`，
> 而天正什么都没收到。所以这条思路已被放弃，只保留"放行物理键"。

**第二层：命令期间临时解除输入法与窗口的关联（`BypassImeDuringCommand`）**

放行物理键解决了"天正收不到"，却让输入法也收到了按键 —— 于是弹候选窗。
插件用微软官方文档 KB Q171154 的做法解决：

```
ImmAssociateContext(hwnd, NULL)     命令开始：解除关联
   ↓
（命令执行期间：物理键直达 CAD，输入法收不到键 ⇒ 不弹候选窗）
   ↓
ImmAssociateContext(hwnd, 原 HIMC)  命令结束：把原来的上下文精确写回
```

三个关键点：

- **必须枚举 CAD 主窗口的**全部子孙窗口**。输入法认的是"当前真正持有焦点的那个窗口"——
  在 2022 里它是命令栏的 `WindowsForms10.EDIT…` 子控件，只处理主窗口等于没做。
  （`IACE_CHILDREN` 影响的是**之后新建**的子窗口，解除不了已有的。）
- **结束时把原值精确写回**。原值是 `0` 表示这个窗口本来就没有输入法上下文，
  既不计入解除数量，也不需要写回。
- **输入法状态一个字节都不动**。不是"把输入法切到英文"，而是"命令期间让输入法暂时看不到这个窗口"。
  命令一结束，你的输入法、中英状态、候选窗，全都和刚才一样。

### 生效范围（刻意收窄）

只有下面七条**同时成立**时才动手：

1. 插件已启用；
2. 工作模式是「无视输入法」；
3. 已接管当前 CAD 窗口；
4. 当前命令**不是**插件自己的命令（`xime*`）；
5. 确实有命令正在执行；
6. 该命令**不属于**文字类命令（TEXT / MTEXT / dhwz / ttext …）；
7. **不处于文字编辑态**。

第 6、7 条是硬红线 —— 用户要输入中文的时候，输入法必须正常工作。

### 工作流程

```
你按下物理键
   ↓
WH_KEYBOARD_LL 低级键盘钩子（无视模式下只"看"不动，用于识别你手动按的 Shift）
   ↓
命令开始 CommandWillStart
   ↓
对 CAD 主窗口 + 全部子孙窗口 ImmAssociateContext(hwnd, NULL)
   ↓
物理键直达 CAD ⇒ 天正/探索者等照常响应；
输入法收不到键 ⇒ 不弹候选窗；也没有字符被塞进命令栏 ⇒ 命令栏保持干净
   ↓
命令结束 CommandEnded → 把保存的原 HIMC 精确写回 → 输入法状态原样恢复
```

### 关键设计约束

1. **不修改 `LowLevelHooksTimeout` 注册表** —— 有的插件会改这个值，但会影响系统全局输入延迟，本插件不这么做。
2. **钩子在有消息循环的 UI 线程安装** —— 否则会导致系统输入卡顿或钩子失效。
3. **钩子回调极快返回且绝不抛异常** —— 任何异常都放行，不阻断系统输入。
4. **只在该接管时才做任何干预** —— 其余情况一律 `CallNextHookEx` 放行，完全不碰系统输入。
5. **绝不改动用户的输入法状态** —— 「无视输入法模式」下插件从不调用任何切换输入法的 API。
6. **加载即挂接命令事件** —— 插件在 `Initialize()` 里就把 `CommandWillStart/Ended/Cancelled`
   挂到**当前已打开的全部文档**上。否则"开机后的第一条命令"整条都不会被接管
   （`XIMESTATUS` 里有一行专门报这个钩子是否挂上）。
7. **诊断命令不许改变被诊断的状态** —— 所有 `xime` 前缀的诊断命令都不会触发本机制，
   否则你敲一条 `XIMEBYPASS` 就会把要观察的现象改掉。

---

## 三、目录结构

```
Xime/
├── Xime.sln                    解决方案（VS Code / Visual Studio 打开它）
├── Directory.Build.props       ObjectARX SDK 路径解析（全仓库共用）
├── local.props.example         本机 SDK 路径模板 → 复制成 local.props
├── .vscode/                    工作区配置：构建 / 验证 / 部署任务与调试配置
│
├── src/
│   ├── Xime.Core/              核心层（与 AutoCAD 解耦，可独立测试）
│   │   ├── XimeEngine.cs           核心调度器
│   │   ├── ImeWindowBypass.cs      ★ 命令期间解除 / 恢复输入法与窗口的关联
│   │   ├── ImeController.cs        输入法中英模式读取与切换（退路模式用）
│   │   ├── KeyboardHook.cs         低级键盘钩子管理
│   │   ├── FocusMonitor.cs         窗口焦点 / 最小化监控
│   │   ├── CharInjector.cs         字符转换与注入（合成通道，现已不用）
│   │   ├── XimeConfig.cs           配置与命令白名单
│   │   ├── RegPath.cs              注册表路径（按 CAD 版本分支）
│   │   ├── Native/
│   │   │   └── NativeMethods.cs    IMM32 / 键盘钩子 / SendInput 等原生 API
│   │   ├── UI/
│   │   │   ├── SettingsForm.cs     设置界面
│   │   │   └── XimeResources.cs    内嵌图标读取
│   │   └── Resources/
│   │       ├── XimeLogo.ico        内嵌进 DLL，随插件一起分发
│   │       └── XimeLogo.png
│   │
│   ├── Xime.Acad/              AutoCAD 插件层
│   │   ├── XimeApplication.cs      插件入口（IExtensionApplication）
│   │   ├── XimeCommands.cs         命令定义（xime / XIME*）
│   │   ├── AutoLoadRegistration.cs 自动加载注册表读写
│   │   └── Compat.cs               跨框架兼容辅助
│   │
│   └── Xime.CliTest/           核心逻辑验证工具（不需要 AutoCAD）
│       ├── Program.cs              命令分发（config / ui / diag / hook …）
│       ├── UiPreview.cs            设置界面独立预览（可截图）
│       ├── HookLiveTest.cs         钩子实况测试
│       └── …                       KeyProbe / FocusProbe / WpfProbe / Bench 等实验
│
├── build/
│   ├── build.sh                ★ 构建 + 打包（★ 它**不编 CliTest**，要单独编）
│   ├── gen_readme.py           生成「安装说明.txt」
│   ├── check_v102.py           交付前自查：产物 / 版本资源 / 关键特征串
│   ├── _negctl_v102.py         负向对照：故意改坏源码，确认断言会变红
│   ├── deploy_v102.py          部署到桌面交付目录 + 安装目录
│   ├── gen_logo.py             由设计稿生成多尺寸图标
│   └── assets/
│       ├── AutoLoad.lsp        LISP 自动加载
│       └── PackageContents.xml ApplicationPlugins 配置
│
└── dist/                       打包产物（已由 .gitignore 忽略）
    ├── net35/                  AutoCAD 2010-2012
    ├── net48/                  AutoCAD 2013-2024
    ├── net8/                   AutoCAD 2025-2026
    └── Xime.bundle/            ApplicationPlugins 自动加载包
```

> 编译中间产物落在 `build/acad/`、`build/core/` 以及各工程的 `bin/` `obj/`；
> `dist/` 是打包产物。**这些都不进版本库**（见 `.gitignore`）。
> `build/` 里另有各历史版本留下的一次性自查 / 部署脚本（`check_v51.py` 等），保留仅作追溯。

---

## 四、编译

### 环境要求

- **.NET SDK** 8.0 或更高（本机在 `9.0.318` 上验证）
  —— 三个目标框架里只有 `net8.0-windows` 用到 SDK 带的运行库；
  `net35` / `net48` 靠 NuGet 上的引用程序集包编译。
- **ObjectARX SDK**（Autodesk 提供，本项目**不能**替您分发）：
  至少需要 `2010-R18.0` 与 `2014-R19.1` 两个版本目录。
- `python`（可选）：只有跑 `check_v102.py` / `gen_readme.py` 等脚本时才需要。
- `bash`：`build/build.sh` 是 bash 脚本；Windows 上装了 Git for Windows 即自带。

### 指定 ObjectARX SDK 的位置

SDK 路径**没有写死在仓库里**，由仓库根的 `Directory.Build.props` 按下面的优先级解析
（先命中者胜）：

| 优先级 | 方式        | 写法                                             |
| --- | --------- | ---------------------------------------------- |
| ①   | 命令行参数     | `dotnet build -p:SdkRoot="D:\ObjectARX-SDK"`    |
| ②   | 本地文件（推荐）  | 把 `local.props.example` 复制成 `local.props`，再改路径 |
| ③   | 环境变量      | `set ACAD_SDK_ROOT=D:\ObjectARX-SDK`            |
| ④   | 默认位置      | 把 `ObjectARX-SDK` 放在本仓库的**同级**目录                |

`local.props` 已被 `.gitignore` 忽略，不会进版本库 —— 每个人的路径互不干扰。
四种方式都没配好时，编译会**直接报一条中文错误**说明缺什么，而不是丢出几百行 `CS0246`。

需要的两个子目录：

| 目标框架             | 引用目录                           | 对应 AutoCAD |
| ---------------- | ------------------------------ | ---------- |
| `net35`          | `<SdkRoot>/2010-R18.0/inc-x64` | 2010-2012  |
| `net48`          | `<SdkRoot>/2014-R19.1/inc`     | 2013-2024  |
| `net8.0-windows` | 同 net48（API 兼容）                | 2025-2026  |

### 构建

```bash
# 完整构建 + 打包（★ 它**不编 Xime.CliTest**，那个要单独编）
bash build/build.sh

# 清理
bash build/build.sh clean
```

产物：

| 目录                 | 内容                            |
| ------------------ | ----------------------------- |
| `dist/net35`       | AutoCAD 2010-2012             |
| `dist/net48`       | AutoCAD 2013-2024             |
| `dist/net8`        | AutoCAD 2025-2026             |
| `dist/Xime.bundle` | ApplicationPlugins 自动加载包（免 NETLOAD） |

### 用 VS Code 开发

仓库自带 `.vscode/`，直接 `code .` 打开即可：

- **构建**：`Ctrl+Shift+B` 跑默认任务（完整构建并打包）；
- **其他任务**：`Ctrl+Shift+P` → `Tasks: Run Task`，内含
  编译 CliTest / 跑回归 / 交付自查 / 负向对照 / 部署 / 界面截图；
- **调试**：可附加到 `acad.exe` 打断点（会先自动编 Debug 配置 ——
  Release 配置**故意不产 PDB**，因为调试信息会改变 DLL 字节，破坏交付一致性）。

---

## 五、命令

| 命令             | 说明                                                        |
| -------------- | --------------------------------------------------------- |
| `xime`         | **一级命令**，打开设置界面                                           |
| `XIMESTATUS`   | 查看运行状态、接管状态、**命令事件钩子是否挂上**、旁路当前是否生效                       |
| `XIMEON`       | 快速启用                                                      |
| `XIMEOFF`      | 快速关闭（恢复本地输入法）                                             |
| `XIMEMODE`     | 查看 / 切换工作模式：**无视输入法模式**（默认）/ 切换输入法模式                      |
| `XIMEKEY`      | 查看 / 切换按键投递方式（四档，默认 `PassThrough` = 放行物理键）                |
| `XIMEBYPASS`   | 「命令期间无视输入法」的现场验证：`1` 强制解除并保持 / `0` 还原 / `on`·`off` 开关 / 直接回车只看状态 |
| `XIMEDIAG`     | 环境诊断，排查兼容性问题                                              |
| `XIMEDEBUG`    | 开 / 关调试日志（写入 `%TEMP%\xime_engine.log`）                     |

其余为内部诊断命令，一般用不到：

`XIMEPROBE` 输入法通道探测 · `XIMEFOCUS` 焦点自检 · `XIMELOGO` 图标资源自检 ·
`XIMEAUTO` 一键写自动加载注册 · `XIMEREG` 注册表自检 · `XIMETREE` 导出窗口树 ·
`XIMETESTCN` 窗口定位自检 · `XIMETESTKEY` 投递实测 · `XIMEHOOKTEST` 吞键诊断

> 所有 `xime` 前缀的命令**都不会触发**「命令期间无视输入法」机制 ——
> 否则你敲一条诊断命令，就把要观察的那个现象本身改掉了。

---

## 六、安装

### 方式一：NETLOAD（推荐首次使用）

1. 把 `Xime.dll` 和 `Xime.Core.dll` 放到同一目录，例如 `D:\CAD插件\X输入法\`
2. AutoCAD 中输入 `NETLOAD`，选择 `Xime.dll`
3. 插件自动写入注册表，**下次启动自动加载**
4. 插件加载后**默认已启用**；需要改设置时输入 `xime` 打开设置界面

### 方式二：ApplicationPlugins（免 NETLOAD）

把握 `dist/Xime.bundle` 文件夹放到：

- 当前用户：`%APPDATA%\Autodesk\ApplicationPlugins\`
- 所有用户：`%PROGRAMFILES%\Autodesk\ApplicationPlugins\`

AutoCAD 启动时按 `PackageContents.xml` 自动选择匹配版本的 DLL 加载。

### 方式三：LISP 自动加载

把 `AutoLoad.lsp` 所在目录加入 AutoCAD 支持路径，或在 `acaddoc.lsp` 中引用。

---

## 七、版本适配

| 包       | 目标框架                   | AutoCAD 版本       |
| ------- | ---------------------- | ---------------- |
| `net35` | .NET Framework 3.5     | 2010, 2011, 2012 |
| `net48` | .NET Framework 4.0-4.8 | 2013 ~ 2024      |
| `net8`  | .NET 8.0               | 2025, 2026       |

### 第三方平台

插件基于 AutoCAD 原生 API，因此自动兼容：

- 天正建筑 / 电气 / 结构 / 给排水 / 暖通（8.0 / 8.2 / 8.5 / 2014 / T20 / T30 系列）
- 探索者系列设计软件
- 鸿业暖通

### 操作系统

Windows 7 / 10 / 11

---

## 八、输入法兼容

本插件**不依赖输入法的任何接口**，因此在所有输入法下的行为一致：
插件接管时命令栏收到英文，未接管时按键完全交给你当前的输入法。

| 输入法                            | 说明        |
| ------------------------------ | --------- |
| Rime 引擎：小狼毫 / 鼠须管 / 小企鹅         | 支持        |
| 微软拼音（Win10 / Win11）            | 支持        |
| 搜狗拼音 / 搜狗五笔                    | 支持        |
| 百度输入法                          | 支持        |
| 手心输入法                          | 支持        |
| 微信输入法                          | 支持        |
| 豆包输入法                          | 支持        |
| 华宇拼音                           | 支持        |
| QQ 输入法                         | 支持        |
| 极品五笔                           | 支持        |

用 `XIMEDIAG` 命令可以查看当前输入法状态。

---

## 九、配置

配置文件位于插件 DLL 同目录：`Xime.config.xml`（每个 CAD 版本目录各一份）。
★ 文件里**自带逐项中文注释** —— 直接打开看比看这份文档更准。

```xml
<?xml version="1.0" encoding="utf-8"?>
<Xime>
  <ConfigVersion>6</ConfigVersion>              <!-- 仅供参考，不参与任何判断 -->
  <Enabled>true</Enabled>
  <EnabledByUser>true</EnabledByUser>           <!-- 用户是否手动开关过 -->
  <WorkMode>IgnoreIme</WorkMode>                <!-- IgnoreIme / SwitchIme -->
  <CommandKeyMode>PassThrough</CommandKeyMode>  <!-- 默认：放行物理键 -->
  <BypassImeDuringCommand>true</BypassImeDuringCommand>
  <ChineseCommands>                             <!-- 需要输入中文的命令白名单 -->
    <Command>text</Command>
    <Command>mtext</Command>
    <Command>ddedit</Command>
    ...
  </ChineseCommands>
</Xime>
```

**默认三件套**：`IgnoreIme` + `PassThrough` + `BypassImeDuringCommand=true`。

`ChineseCommands` 是**需要输入中文的命令白名单**：这些命令执行期间插件不干涉输入法，
输入法保持你原来的状态、可以直接打中文。
可在 `xime` 界面右侧列表中直接编辑，也可以直接改文件。

出厂白名单共 **140 条**，包含 TEXT、DTEXT、MTEXT、TTEXT、DDEDIT、TEDIT、TEXTEDIT、MTEDIT、
ATTDEF、ATTEDIT、EATTEDIT、RENAME、LAYER、DIMEDIT、MLEADER 等常用命令，
以及天正等第三方平台的文字类命令。

---

## 十、验证工具

`src/Xime.CliTest` 是独立的核心逻辑验证工具，**不需要 AutoCAD**：

```bash
cd src/Xime.CliTest/bin/Release/net48
./XimeTest.exe diag          # 环境与输入法诊断
./XimeTest.exe mode          # 读取当前输入法中英模式
./XimeTest.exe to-english    # 测试切到英文
./XimeTest.exe to-chinese    # 测试切到中文
./XimeTest.exe hook          # 测试键盘钩子安装
./XimeTest.exe config        # 测试配置与白名单判定
./XimeTest.exe ui            # 预览设置界面（不依赖 AutoCAD）
./XimeTest.exe ui --shot out.png   # 预览并截图（验证布局）
```

---

## 十一、开发说明

### 模式说明

插件保留两种工作模式，用 `XIMEMODE` 或设置界面切换：

- **无视输入法模式（`IgnoreIme`）** —— **默认**。绝不修改你的输入法状态；
  靠"放行物理键" + "命令期间解除输入法与窗口的关联"实现；
- **切换输入法模式（`SwitchIme`）** —— **退路**。命令期间**真的**把输入法按在英文
  （小狼毫走 `WeaselServer /ascii`）。物理键同样直达天正、也不弹候选窗，
  但它**会改动你的输入法状态**，命令期间输入法会显示为英文。
  只有 `XIMEBYPASS 1` 报「解除 0 个窗口」（说明那些窗口本来就没有输入法上下文、
  无从下手）时才需要退到它。

两个模式都编在同一个 DLL 里，`XIMEMODE 0|1` 可当场来回切换验证，无需重启。

### 设计稿对应关系

设置界面参照设计稿实现：

- **帮助**：软件版本 v1.0.2 / 作者 影子 / 操作教程
- **设置**：启用 X输入法 / 关闭 X输入法 / 卸载 X输入法
- **模式**：切换输入法模式（退路）/ 无视输入法模式（默认）
- **右侧**：执行此命令切中文（命令白名单编辑）
- ~~在线授权~~：已按要求移除

### 版本库（Git）

- 分支：`main`；忽略规则见 `.gitignore`（**编译产物与 `dist/` 一律不进库**）
- 行尾：仓库内存 LF；`.gitattributes` 只把 `*.sh` 钉死为 LF
  （否则在 Linux / macOS / WSL 下 `build.sh` 会因 CRLF 报 bad interpreter）
- **上传 / 下载二进制请走 GitHub Releases**，不要提交 DLL

> 如果本机直连 GitHub 不通，可以**只给这个仓库**配代理（不影响其它仓库）：
>
> ```bash
> git config --local http.proxy socks5h://127.0.0.1:10808
> # 清掉：git config --local --unset http.proxy
> ```

---

## 十二、更新日志

### v1.0.2

- **★「命令期间无视输入法」**（本版核心机制）：命令期间对 CAD 主窗口 + **全部子孙窗口**
  解除输入法关联（`ImmAssociateContext(hwnd, NULL)`，微软 KB Q171154），命令结束时把原
  HIMC **精确写回**。配合默认的 `CommandKeyMode.PassThrough`（放行物理键），
  同时做到：**物理键直达天正** + **不弹候选窗** + **命令栏不留字符**，
  而输入法状态**一个字节都不动**
- **默认三件套**：`WorkMode=IgnoreIme` + `CommandKeyMode=PassThrough`
  + `BypassImeDuringCommand=true`（`ConfigVersion=6`）
- **加载即挂接命令事件**：`Initialize()` 里就把 `CommandWillStart/Ended/Cancelled`
  挂到当前**已打开的全部文档**上。否则**开机后的第一条命令**在首个
  `CommandWillStart` 触发之前不会被接管 ——
  现象就是"NETLOAD 后第一条命令照旧弹候选窗，先敲一次 `XIMESTATUS` 才正常"
- **「卸载」只卸当前这一个 AutoCAD 版本**：面板点卸载只清**当前运行版本**的自动加载注册项，
  本机其它版本（2014 / 2022 / …）原样保留，不再被牵连
  （旧实现在 2022 里卸载会把 2014 的注册项一起删掉，且没有任何提示）
- **版本号统一为 v1.0.2**：标题栏、帮助、安装说明、`PackageContents.xml` 全部同源
  （帮助里的版本号改为由宿主注入，不再写死在正文里）
- **`XIMEKEY 3`：新增第 4 条按键投递通道**「投给 AutoCAD 主窗口」
- 引擎退出接管（`Disengage`）不再抹掉"当前正在执行的命令"——
  命令是否在执行是 AutoCAD 的事实，不该被接管状态的抖动改写

### v1.0.0

- 首个版本
- 实现无视输入法模式
- 支持 AutoCAD 2010-2026
- 支持天正、探索者、鸿业等第三方平台
- ~~单通道 UNICODE 注入~~（`SendInput(KEYEVENTF_UNICODE)`）——
  当时用于适配 2013+ 的 WPF 命令栏。**该方案后来被现场证伪**：
  合成输入不被天正认，命令栏还会留下一个小写 `a`。
  自 v1.0.2 起改为「放行物理键 + 命令期间无视输入法」，见上文
- 命令白名单可配置（中文类命令结束后自动放行给输入法）
- 自动加载注册
- CapsLock 保护（大写锁定开启时完全不干预）
- 最小化 / 切走自动退出接管并恢复本地输入法状态
