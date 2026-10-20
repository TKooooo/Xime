# X输入法 v1.0.5

CAD 平台输入法智能切换插件 —— **无视输入法模式**

支持 **AutoCAD / 中望CAD / 浩辰CAD** 三家宿主（同一份插件源码，编出三个 DLL）

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
>
> （`PostKey` / `PostKeyFrame` / `ReplayVk` / `SendUnicodeInput`）在 AutoCAD 本体上可用，
>
> 但天正一律不认：投出去的消息被当成命令栏文本，命令栏里会留下一个小写 `a`，
>
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

- **必须枚举 CAD 主窗口的**全部子孙窗口\*\*。输入法认的是"当前真正持有焦点的那个窗口"——

  在 2022 里它是命令栏的 `WindowsForms10.EDIT…` 子控件，只处理主窗口等于没做。

  （`IACE_CHILDREN` 影响的是\*\*之后新建\*\*的子窗口，解除不了已有的。）
- **结束时把原值精确写回**。原值是 `0` 表示这个窗口本来就没有输入法上下文，

  既不计入解除数量，也不需要写回。
- **输入法状态一个字节都不动**。不是"把输入法切到英文"，而是"命令期间让输入法暂时看不到这个窗口"。

  命令一结束，你的输入法、中英状态、候选窗，全都和刚才一样。

### 生效范围（刻意收窄）

只有下面八条**同时成立**时才动手：

1. 插件已启用；
2. 工作模式是「无视输入法」；
3. 已接管当前 CAD 窗口；
4. 当前命令**不是**插件自己的命令（`xime*`）；
5. 确实有命令正在执行；
6. 该命令**不属于**文字类命令（TEXT / MTEXT / dhwz / ttext …）；
7. **不处于文字编辑态**；
8. ★ 当前焦点**不在外部文本编辑控件**上（图层特性管理器的名称框、各类对话框输入框 …）。

第 6、7、8 条是硬红线 —— 用户要输入中文的时候，输入法必须正常工作。  
第 8 条（v62 新增）与第 7 条互补：第 7 条依赖"焦点事件送达到状态位"，  
第 8 条**直接就地读真实焦点**，堵住"焦点事件没来 / 命令先于焦点事件"两种漏网。

★ v63 补充：第 8 条里的「外部文本编辑控件」**不包括动态输入（DYN）提示框**。  
它（`Afx:…:803:…` / `CAcDynInputWndControl`）内部是一枚**原生 `Edit`**，与真编辑框完全同形，  
但语义是**命令输入端** —— 在那里敲的字母属于命令行，必须继续接管。  
误把它算进第 8 条的后果实测确凿：DYN 打开后敲 `line`，第 1 个字母正常、**第 2 个起变中文**。

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
│   ├── Xime.Core/              核心层（与宿主解耦，可独立测试）
│   │   ├── XimeEngine.cs           核心调度器
│   │   ├── ImeWindowBypass.cs      ★ 命令期间解除 / 恢复输入法与窗口的关联
│   │   ├── ImeController.cs        输入法中英模式读取与切换（退路模式用）
│   │   ├── KeyboardHook.cs         低级键盘钩子管理
│   │   ├── FocusMonitor.cs         窗口焦点 / 最小化监控
│   │   ├── CharInjector.cs         字符转换与注入（合成通道，现已不用）
│   │   ├── XimeConfig.cs           配置与命令白名单
│   │   ├── RegPath.cs              注册表路径（按 CAD 版本分支）
│   │   ├── HostTextFrame.cs        ★ 命令栏定位器的契约 + 门面（宿主中立）
│   │   ├── AcadTextFrame.cs             AutoCAD 的命令栏定位实现
│   │   ├── AcadTextFrameProvider.cs     ┐ 两种实现的装配
│   │   ├── GenericTextFrameProvider.cs  ┘
│   │   ├── Native/
│   │   │   └── NativeMethods.cs    IMM32 / 键盘钩子 / SendInput 等原生 API
│   │   ├── UI/
│   │   │   ├── SettingsForm.cs     设置界面
│   │   │   └── XimeResources.cs    内嵌图标读取
│   │   └── Resources/
│   │       ├── XimeLogo.ico        内嵌进 DLL，随插件一起分发
│   │       └── XimeLogo.png
│   │
│   ├── Xime.Plugin/            ★ 三宿主**共用**的插件层源码
│   │   ├── XimeApplication.cs      插件入口（IExtensionApplication）
│   │   ├── XimeCommands.cs         命令定义（xime / XIME*）
│   │   ├── Compat.cs               跨框架兼容辅助
│   │   └── AutoLoadRegistrar.NonAcad.cs  中望 / 浩辰的自动加载注册器
│   │
│   ├── Xime.Acad/              AutoCAD 宿主工程（net35 / net48 / net8）
│   │   └── AutoLoadRegistrar.cs    AutoCAD 自动加载注册表读写
│   ├── Xime.Zwcad/             中望CAD 宿主工程（net48）
│   ├── Xime.GstarCad/          浩辰CAD 宿主工程（net8.0-windows）
│   │
│   └── Xime.CliTest/           核心逻辑验证工具（不需要 CAD）
│       ├── Program.cs              命令分发（config / ui / diag / hook …）
│       ├── UiPreview.cs            设置界面独立预览（可截图）
│       ├── HookLiveTest.cs         钩子实况测试
│       └── …                       KeyProbe / FocusProbe / WpfProbe / Bench 等实验
│
├── build/
│   ├── build.sh                ★ 构建 + 打包（★ 它**不编 CliTest**，要单独编）
│   ├── gen_readme.py           生成「安装说明.txt」（含中望 / 浩辰版）
│   ├── check_v103.py           交付前自查：产物 / 版本资源 / 关键特征串
│   ├── _negctl_v103.py         负向对照：故意改坏源码，确认断言会变红
│   ├── deploy_v103.py          部署到桌面交付目录 + 安装目录
│   ├── gen_logo.py             由设计稿生成多尺寸图标
│   └── assets/
│       ├── AutoLoad.lsp        LISP 自动加载（仅 AutoCAD）
│       └── PackageContents.xml ApplicationPlugins 配置（仅 AutoCAD）
│
└── dist/                       打包产物（已由 .gitignore 忽略）
    ├── AutoCAD2010-2012/       AutoCAD 2010-2012
    ├── AutoCAD2013-2024/       AutoCAD 2013-2024
    ├── AutoCAD2025-2026/       AutoCAD 2025-2026
    ├── Xime.bundle/            ApplicationPlugins 自动加载包（仅 AutoCAD）
    ├── ZWCAD2026/              中望 CAD（Xime.Zwcad.dll）
    └── GsCAD2026/              浩辰 CAD（Xime.GstarCad.dll）
```

> ★ 交付目录名就是「该放哪个 CAD」，不再用 `net35` / `net48` / `net8`
>
> 这类只有开发者看得懂的 TFM 名（`Xime.bundle` 内部的子目录同步改名，
>
> 见 `assets/PackageContents.xml` 的 `ModuleName`）。

> 编译中间产物落在 `build/acad/`、`build/core/` 以及各工程的 `bin/` `obj/`；
>
> `dist/` 是打包产物。**这些都不进版本库**（见 `.gitignore`）。
>
> `build/` 里另有各历史版本留下的一次性自查 / 部署脚本（`check_v51.py` 等），保留仅作追溯。

---

## 四、编译

### 环境要求

- **.NET SDK** 8.0 或更高（本机在 `9.0.318` 上验证）

  —— 三个目标框架里只有 `net8.0-windows` 用到 SDK 带的运行库；

  `net35` / `net48` 靠 NuGet 上的引用程序集包编译。
- **ObjectARX SDK**（Autodesk 提供，本项目**不能**替您分发）：

  至少需要 `2010-R18.0` 与 `2014-R19.1` 两个版本目录。

  本仓库开发时用的是镜像仓库 <https://gitee.com/Hexon/ObjectARX-SDK>（含 2000 → 2026 全部版本），

  克隆到仓库**同级**目录即可直接被默认配置找到：
  ```bash
  git clone --depth 1 https://gitee.com/Hexon/ObjectARX-SDK.git ../ObjectARX-SDK
  ```
  （GitHub 上也有同名镜像；只要目录里有 `2010-R18.0` / `2014-R19.1` 就行。）
- **中望 CAD / 浩辰 CAD**（仅编这两个宿主时需要）：

  它们的 .NET API 程序集**随软件一起安装**，不需要额外下载 ——

  装了 CAD 就能编。程序集位置由 `Directory.Build.props` 从注册表自动定位，

  也可用命令行 / `local.props` / 环境变量指定（详见下节）。
- `python`（可选）：只有跑 `check_v103.py` / `gen_readme.py` 等脚本时才需要。
- `bash`：`build/build.sh` 是 bash 脚本；Windows 上装了 Git for Windows 即自带。

> 三家的引用程序集都**不需要**放进仓库：AutoCAD 的 SDK 由各人自行下载，
>
> 中望 / 浩辰的直接指向各自安装目录。仓库里只有代码。

### 指定 ObjectARX SDK 的位置

SDK 路径**没有写死在仓库里**，由仓库根的 `Directory.Build.props` 按下面的优先级解析

（先命中者胜）：

| 优先级 | 方式       | 写法                                             |
| --- | -------- | ---------------------------------------------- |
| ①   | 命令行参数    | `dotnet build -p:SdkRoot="D:\ObjectARX-SDK"`   |
| ②   | 本地文件（推荐） | 把 `local.props.example` 复制成 `local.props`，再改路径 |
| ③   | 环境变量     | `set ACAD_SDK_ROOT=D:\ObjectARX-SDK`           |
| ④   | 默认位置     | 把 `ObjectARX-SDK` 放在本仓库的**同级**目录               |

`local.props` 已被 `.gitignore` 忽略，不会进版本库 —— 每个人的路径互不干扰。

四种方式都没配好时，编译会**直接报一条中文错误**说明缺什么，而不是丢出几百行 `CS0246`。

需要的两个子目录：

| 目标框架             | 引用目录                           | 对应 AutoCAD |
| ---------------- | ------------------------------ | ---------- |
| `net35`          | `<SdkRoot>/2010-R18.0/inc-x64` | 2010-2012  |
| `net48`          | `<SdkRoot>/2014-R19.1/inc`     | 2013-2024  |
| `net8.0-windows` | 同 net48（API 兼容）                | 2025-2026  |

### 中望 / 浩辰 CAD 程序集的位置

这两家的宿主 API 程序集**随 CAD 一起安装**，不需要 ObjectARX。

| 宿主    | 需要的程序集                                        | 典型路径                                  |
| ----- | --------------------------------------------- | ------------------------------------- |
| 中望CAD | `ZwManaged.dll` / `ZwDatabaseMgd.dll`         | `C:\Program Files\ZWSOFT\ZWCAD 2026\` |
| 浩辰CAD | `GcCoreMgd.dll` / `GcDbMgd.dll` / `GcMgd.dll` | `C:\Program Files\浩辰软件\浩辰CAD2026\`    |

解析优先级与 ObjectARX 的 `SdkRoot` 完全一致（先命中者胜）：

| 优先级 | 方式     | 写法                                                    |
| --- | ------ | ----------------------------------------------------- |
| ①   | 命令行参数  | `-p:ZwcadRefDir="..."` / `-p:GstarCadRefDir="..."`    |
| ②   | 本地文件   | `local.props` 里写 `<ZwcadRefDir>` / `<GstarCadRefDir>` |
| ③   | 环境变量   | `ZWCAD_SDK_ROOT` / `GSTARCAD_SDK_ROOT`                |
| ④   | 默认（自动） | 读注册表里当前版本分支的 `Location` / `LOCATION` 定位安装目录           |

四种方式都没命中时，编译会**直接报一条中文错误**指出缺哪个程序集，而不是丢出几百行 `CS0246`。

### 构建

```bash
# 完整构建 + 打包（★ 它**不编 Xime.CliTest**，那个要单独编）
bash build/build.sh

# 清理
bash build/build.sh clean
```

产物：

| 目录                      | 内容                                                   |
| ----------------------- | ---------------------------------------------------- |
| `dist/AutoCAD2010-2012` | AutoCAD 2010-2012                                    |
| `dist/AutoCAD2013-2024` | AutoCAD 2013-2024                                    |
| `dist/AutoCAD2025-2026` | AutoCAD 2025-2026                                    |
| `dist/Xime.bundle`      | ApplicationPlugins 自动加载包（免 NETLOAD，仅 AutoCAD）        |
| `dist/ZWCAD2026`        | 中望 CAD（`Xime.Zwcad.dll` + `Xime.Core.dll` + 安装说明）    |
| `dist/GsCAD2026`        | 浩辰 CAD（`Xime.GstarCad.dll` + `Xime.Core.dll` + 安装说明） |

> 若本机没有 ObjectARX SDK / 对应版本的 AutoCAD，AutoCAD 的目标会**跳过并提示**，
>
> 不会中断中望 / 浩辰的构建（`net35` 在没有 2010-R18.0 时就会走这条）。
>
> 想强制某套 SDK：`ACAD_SDK_ROOT=D:\ObjectARX-SDK bash build/build.sh`。

### 用 VS Code 开发

仓库自带 `.vscode/`，直接 `code .` 打开即可：

- **构建**：`Ctrl+Shift+B` 跑默认任务（完整构建并打包）；
- **其他任务**：`Ctrl+Shift+P` → `Tasks: Run Task`，内含

  编译 CliTest / 跑回归 / 交付自查 / 负向对照 / 部署 / 界面截图；
- **调试**：可附加到 `acad.exe` 打断点（会先自动编 Debug 配置 ——


Release 配置**故意不产 PDB**，因为调试信息会改变 DLL 字节，破坏交付一致性）。

---

## 五、命令

| 命令           | 说明                                                               |
| ------------ | ---------------------------------------------------------------- |
| `xime`       | **一级命令**，打开设置界面                                                  |
| `XIMESTATUS` | 查看运行状态、接管状态、**命令事件钩子是否挂上**、旁路当前是否生效                              |
| `XIMEON`     | 快速启用                                                             |
| `XIMEOFF`    | 快速关闭（恢复本地输入法）                                                    |
| `XIMEMODE`   | 查看 / 切换工作模式：**无视输入法模式**（默认）/ 切换输入法模式                             |
| `XIMEKEY`    | 查看 / 切换按键投递方式（四档，默认 `PassThrough` = 放行物理键）                       |
| `XIMEBYPASS` | 「命令期间无视输入法」的现场验证：`1` 强制解除并保持 / `0` 还原 / `on`·`off` 开关 / 直接回车只看状态 |
| `XIMEDIAG`   | 环境诊断，排查兼容性问题                                                     |
| `XIMEDEBUG`  | 开 / 关调试日志（写入 `%TEMP%\xime_engine.log`）                           |

其余为内部诊断命令，一般用不到：

`XIMEPROBE` 输入法通道探测 · `XIMEFOCUS` 焦点自检 · `XIMELOGO` 图标资源自检 ·

`XIMEAUTO` 一键写自动加载注册 · `XIMEREG` 注册表自检 · `XIMETREE` 导出窗口树 ·

`XIMETESTCN` 窗口定位自检 · `XIMETESTKEY` 投递实测 · `XIMEHOOKTEST` 吞键诊断

> 所有 `xime` 前缀的命令**都不会触发**「命令期间无视输入法」机制 ——
>
> 否则你敲一条诊断命令，就把要观察的那个现象本身改掉了。

---

## 六、安装

### AutoCAD

#### 方式一：NETLOAD（推荐首次使用）

1. 把 `Xime.dll` 和 `Xime.Core.dll` 放到同一目录，例如 `D:\CAD插件\X输入法\`
2. AutoCAD 中输入 `NETLOAD`，选择 `Xime.dll`
3. 插件自动写入注册表，**下次启动自动加载**
4. 插件加载后**默认已启用**；需要改设置时输入 `xime` 打开设置界面

#### 方式二：ApplicationPlugins（免 NETLOAD）

把握 `dist/Xime.bundle` 文件夹放到：

- 当前用户：`%APPDATA%\Autodesk\ApplicationPlugins\`
- 所有用户：`%PROGRAMFILES%\Autodesk\ApplicationPlugins\`

AutoCAD 启动时按 `PackageContents.xml` 自动选择匹配版本的 DLL 加载。

#### 方式三：LISP 自动加载

把 `AutoLoad.lsp` 所在目录加入 AutoCAD 支持路径，或在 `acaddoc.lsp` 中引用。

### 中望 CAD / 浩辰 CAD

两家都不支持 ApplicationPlugins，**只走 NETLOAD + 注册表自动加载**：

| 宿主    | 加载哪个 DLL            | 自动加载写入位置（HKCU）                                            |
| ----- | ------------------- | --------------------------------------------------------- |
| 中望CAD | `Xime.Zwcad.dll`    | `Software\ZWSOFT\ZWCAD\<版本>\<语言>\Applications\XIME`       |
| 浩辰CAD | `Xime.GstarCad.dll` | `Software\Gstarsoft\GstarCAD\<系列>\<语言>\Applications\XIME` |

1. 把该目录下的 DLL（`Xime.Zwcad.dll` / `Xime.GstarCad.dll`）与 `Xime.Core.dll` 放同一目录
2. 在 CAD 命令行输入 `NETLOAD`，选择上表对应的那个 DLL
3. 插件自动为**当前这一个版本**写入注册表，下次启动免 NETLOAD

> 两家的自动加载键名（`LOADER` / `LOADCTRLS` / `MANAGED`）与 AutoCAD 同构，
>
> 但浩辰官方文档另外给出了 `ManagedType` / `LoaderType` 一套名字 ——
>
> 插件**两套都写**，避免因文档分歧导致"注册成功却不加载"。
>
> 排查用 `XIMEREG`，它会把候选分支与实际写入情况全列出来。

---

## 七、版本适配

| 包                       | 目标框架                   | 宿主                          |
| ----------------------- | ---------------------- | --------------------------- |
| `dist/AutoCAD2010-2012` | .NET Framework 3.5     | AutoCAD 2010, 2011, 2012    |
| `dist/AutoCAD2013-2024` | .NET Framework 4.0-4.8 | AutoCAD 2013 ~ 2024         |
| `dist/AutoCAD2025-2026` | .NET 8.0               | AutoCAD 2025, 2026          |
| `dist/ZWCAD2026`        | .NET Framework 4.0-4.8 | 中望 CAD（2020 ~ 2026，实测 2026） |
| `dist/GsCAD2026`        | .NET 8.0               | 浩辰 CAD（2024 ~ 2026，实测 2026） |

> 判据是"安装目录里是不是有对应的托管程序集"：
>
> 中望看 `ZwManaged.dll`，浩辰看 `GcCoreMgd.dll`。新版本若换了 .NET 版本或改了
>
> 程序集名，编译时会以中文错误直接点出来。

### 第三方平台

AutoCAD 版基于 AutoCAD 原生 API，因此自动兼容：

- 天正建筑 / 电气 / 结构 / 给排水 / 暖通（8.0 / 8.2 / 8.5 / 2014 / T20 / T30 系列）
- 探索者系列设计软件
- 鸿业暖通

> 中望 / 浩辰 版沿用**完全相同的机制**（放行物理键 + 命令期间解除输入法关联），
>
> 因此理论上对运行在这两个平台上的第三方插件同样有效；
>
> 但这两家的第三方生态与 AutoCAD 不同，**本版未做平台级实测**。

### 操作系统

Windows 7 / 10 / 11（中望 / 浩辰版为 64 位）

---

## 八、输入法兼容

本插件**不依赖输入法的任何接口**，因此在所有输入法下的行为一致：

插件接管时命令栏收到英文，未接管时按键完全交给你当前的输入法。

| 输入法                     | 说明 |
| ----------------------- | -- |
| Rime 引擎：小狼毫 / 鼠须管 / 小企鹅 | 支持 |
| 微软拼音（Win10 / Win11）     | 支持 |
| 搜狗拼音 / 搜狗五笔             | 支持 |
| 百度输入法                   | 支持 |
| 手心输入法                   | 支持 |
| 微信输入法                   | 支持 |
| 豆包输入法                   | 支持 |
| 华宇拼音                    | 支持 |
| QQ 输入法                  | 支持 |
| 极品五笔                    | 支持 |

用 `XIMEDIAG` 命令可以查看当前输入法状态。

---

## 九、配置

配置文件位于插件 DLL 同目录：`Xime.config.xml`（每个 CAD 版本目录各一份）。

★ 文件里**自带逐项中文注释** —— 直接打开看比看这份文档更准。

```xml
<?xml version="1.0" encoding="utf-8"?>
<Xime>
  <ConfigVersion>6</ConfigVersion>              
  <Enabled>true</Enabled>
  <EnabledByUser>true</EnabledByUser>           
  <WorkMode>IgnoreIme</WorkMode>                
  <CommandKeyMode>PassThrough</CommandKeyMode>  
  <BypassImeDuringCommand>true</BypassImeDuringCommand>
  <ChineseCommands>                             
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

「加载后默认 = 无视输入法模式」由三处默认值保证：`WorkMode` 字段初值、构造函数、  
`ResetToFreshInstall()`；两种模式本身都可选。

### 设计稿对应关系

设置界面参照设计稿实现：

- **帮助**：软件版本 v1.0.5 / 作者 影子 / 操作教程
- **设置**：启用 X输入法 / 关闭 X输入法 / 卸载 X输入法
- **模式**：无视输入法模式（★ 默认）/ 切换输入法模式（退路）
- **右侧**：执行此命令切中文（命令白名单编辑）
- ~~在线授权~~：已按要求移除

### 版本库（Git）

- 分支：`main`（仓库常态）；**本次三宿主扩展在 `gongsi` 分支上进行**
- 忽略规则见 `.gitignore`（**编译产物与 `dist/` 一律不进库**）
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

### v1.0.5

- **版本号升级为 v1.0.5**：标题栏、帮助、安装说明、`PackageContents.xml` 同源  
  （`<FileVersion>1.0.5.0</FileVersion>`；`AssemblyVersion` 仍刻意恒为 `1.0.0.0`）
- **★ 修在 X批注 等第三方 WPF 面板里打不出中文（手动切输入法也无效）**：  
  现场日志显示 X批注 的批注输入框（`HwndWrapper[DefaultDomain;;…]`，第三方 .NET 插件，  
  纯 WPF）被判成 `是命令栏=True`。根因是**循环自证**：命令栏解析的第一步拿"当前焦点"  
  当命令栏，而命令栏判定又拿"解析结果"当证据 ⇒ 任何有焦点、长得像输入框的第三方面板  
  都会证明自己是命令栏。后果与 v62 图层改名同一个失效模式：钩子不让路（字母被吞改注英文）
  - 「命令期间无视输入法」照旧解除该窗口的输入法关联。  
    现在命令栏判定的**证据链必须含 `CLI Palette`**（父链或属主链），解析与判定两头各加一道；  
    同时 `ShouldSeizeCommandKeyAsChar` 新增第 ⑦ 条「焦点在真编辑控件 ⇒ 否决改注入字符」。

### v1.0.4

- **版本号升级为 v1.0.4**：标题栏、帮助、安装说明、`PackageContents.xml` 同源  
  （`<FileVersion>1.0.4.0</FileVersion>`；`AssemblyVersion` 仍刻意恒为 `1.0.0.0`）
- **★ 修 AutoCAD 的 `LAYER` 命令 → 新建图层 → 改图层名，打不出中文（手动切输入法也无效）**：  
  图层特性管理器里的名称编辑框类名是 `WindowsForms10.EDIT.app.0.…`（**不以 `Edit` 开头**），  
  插件的编辑控件判据漏掉了它 ⇒ 不进文字编辑态 ⇒ 钩子把字母吞掉、改注入英文。  
  现在 `IsEditBoxClass` 认 `WindowsForms10.EDIT` / `WindowsForms10.RICHEDIT`，  
  并新增「焦点在外部编辑框 ⇒ 钩子让路」的独立判据（不依赖状态位，焦点一变立即生效）。
- **★ 修开启「动态输入（DYN）」后，命令输入从第 2 个字母起变中文**：  
  动态输入框是 `Afx:…:803:…`（内部名 `CAcDynInputWndControl`），里面是一枚**原生 `Edit`**，  
  被误判成“正在编辑文字” ⇒ 从第 2 个字母起放行给输入法。  
  现在按窗口祖先链识别它：**永不让路**、也绝不进文字编辑态（文字类命令仍按白名单放行中文）。
- 说明：动态输入框里首字母显示为大写是 AutoCAD 自己的显示行为，**不是插件改的**  
  （插件注入的始终是小写字母）。

### v1.0.3

- **版本号统一为 v1.0.3**：标题栏、帮助、安装说明、`PackageContents.xml` 同源  
  （`<FileVersion>1.0.3.0</FileVersion>`；`AssemblyVersion` 仍恒为 `1.0.0.0`）
- **★ 修 AutoCAD 平台 + 豆包输入法（纯 TSF 文本服务）下「命令名 / 二级命令变中文」**：
  - **空闲期**：命令栏的英文标志被 CAD 的 WPF 浮层（命令自动完成下拉等，类名 `HwndWrapper`，  
    父链拿不到 `CLI Palette`）误判成"文字编辑"而清掉时，按**真实焦点**当场复核并抢回英文接管  
    （原先只有 2 秒兜底巡检，赶不上头几个字母）
  - **命令期间**：按下的字母若落在**可组字控件**（命令栏链 / CAD 的 WPF 宿主）上，  
    改走 **吞键 + 注入 `WM_CHAR`**（`SendInput(KEYEVENTF_UNICODE)`，TSF 按键回调看不到这枚键）；  
    焦点在绘图区（天正 `acedGrRead` 场景）则**照旧放行物理键** —— 天正二级命令（`RYBZ` 等）行为不变
  - 背景：`ImeWindowBypass` 的 `ImmAssociateContext` 只解 IMM32 的 HIMC，对**纯 TSF 输入法结构性无效**；  
    本修复不依赖它
- **安装说明精简**：`gen_readme.py` 生成的 `安装说明.txt` 只保留加载步骤、常用命令与必要说明，  
  删除历次版本迭代记录与测试过程描述

### 扩展：新增中望 CAD / 浩辰 CAD 支持（`gongsi` 分支）

- **插件层改为共享源码**：`XimeApplication` / `XimeCommands` / `Compat` 从

  `src/Xime.Acad/` 移到 `src/Xime.Plugin/`，三个宿主工程各自编译**同一份源码**，

  用编译符号 `HOST_AUTOCAD` / `HOST_ZWCAD` / `HOST_GSTARCAD` 切换宿主 API 命名空间

  （`Autodesk.AutoCAD.*` / `ZwSoft.ZwCAD.*` / `Gssoft.Gscad.*`）。

  AutoCAD 侧行为**完全不变**。
- **Core 层宿主解耦**：命令栏定位器改为「契约 `HostTextFrame` + 可注入实现」，

  `XimeEngine` 不再直接引用 AutoCAD 的 `AcadTextFrame`。AutoCAD 仍用原实现，

  新宿主用宿主中立的通用实现（按窗口类名 / 层级定位命令栏）。
- **自动加载注册**：抽出注册抽象，新增两份实现 ——

  中望 `HKCU\Software\ZWSOFT\ZWCAD\<版本>\<语言>\Applications\XIME`、

  浩辰 `HKCU\Software\Gstarsoft\GstarCAD\<系列>\<语言>\Applications\XIME`。

  与 AutoCAD 版同样**只写当前版本、只删当前版本**。

  浩辰另按官方文档同时写入 `ManagedType` / `LoaderType`，与 AutoCAD 风格键名并存。
- **新增两个工程**：`src/Xime.Zwcad`（net48）、`src/Xime.GstarCad`（net8.0-windows），

  已加入 `Xime.sln`。
- **`build.sh` / VS Code 任务**覆盖三家；AutoCAD 目标改为**逐个 TFM 编译、缺 SDK 则跳过**，

  不再因为本机没有 ObjectARX SDK 而中断整条构建。
- **安装说明**：`gen_readme.py` 除三份 AutoCAD 说明外，另生成中望 / 浩辰各一份。
- 本机实测（2026）：中望 CAD 2026 / 浩辰 CAD 2026 上，注册 → 回读 → 卸载 → 确认清除

  **全流程通过**；`Applications` 下其它子键（如浩辰自带的 `XAnnotation`）不受影响。

### v1.0.2

- **★「命令期间无视输入法」**（本版核心机制）：命令期间对 CAD 主窗口 + **全部子孙窗口**

  解除输入法关联（`ImmAssociateContext(hwnd, NULL)`，微软 KB Q171154），命令结束时把原

  HIMC **精确写回**。配合默认的 `CommandKeyMode.PassThrough`（放行物理键），

  同时做到：**物理键直达天正** + **不弹候选窗** + **命令栏不留字符**，

  而输入法状态**一个字节都不动**
- **默认三件套**：`WorkMode=IgnoreIme` + `CommandKeyMode=PassThrough`
  - `BypassImeDuringCommand=true`（`ConfigVersion=6`）
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
