#!/usr/bin/env bash
# ============================================================
#  X输入法 构建 + 打包脚本（Git Bash / Linux 环境）
#
#  用法：
#    ./build/build.sh          编译 + 打包
#    ./build/build.sh clean    清理所有产物
#
#  产出（本次扩展起宿主扩到三家）：
#    ── AutoCAD ──
#    dist/AutoCAD2010-2012 -> AutoCAD 2010-2012（.NET 3.5）
#    dist/AutoCAD2013-2024 -> AutoCAD 2013-2024（.NET 4.0-4.8）
#    dist/AutoCAD2025-2026 -> AutoCAD 2025-2026（.NET 8）
#    dist/Xime.bundle      -> ApplicationPlugins 自动加载包（免 NETLOAD）
#    ── 中望 CAD ──
#    dist/ZWCAD2026   -> 中望 CAD 2026（.NET Framework 4.x，Xime.Zwcad.dll）
#    ── 浩辰 CAD ──
#    dist/GsCAD2026   -> 浩辰 CAD 2026（.NET 8，Xime.GstarCad.dll）
#
#  ★ 目录名就是「交付名」，直接对应使用者要往哪个 CAD 里放 ——
#    不再用 net35 / net48 / net8 这类只有开发者看得懂的 TFM 名。
#
#  ★ 三家共用的插件源码在 src/Xime.Plugin/，由三个工程各自编译，
#    改一处 = 三家同时生效（详见各 csproj 顶部注释）。
#
#  ★ 中望 / 浩辰 的宿主 API 程序集随软件一起装，本机装了就能编；
#    AutoCAD 需要 ObjectARX SDK（版权归 Autodesk，不随仓库分发），
#    没有时可退化为引用本机已装 AutoCAD 的程序集（见下方 USE_INSTALLED_CAD）。
#    任一 AutoCAD 目标编不过都只会**跳过并提示**，不会中断后面两家的构建。
# ============================================================

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
DIST="$ROOT/dist"
BUILD="$ROOT/build"

# ------------------------------------------------------------
# 选一个**带 SDK** 的 dotnet。
# 注意：只装运行时的 dotnet 能"运行"却编不了东西（--list-sdks 为空），
#       所以候选必须过 --list-sdks 这一关，否则会以一句莫名的
#       "No .NET SDKs were found" 收场。
# ------------------------------------------------------------
DOTNET=""
for _dn in "${DOTNET:-}" \
           "$HOME/.workbuddy/binaries/dotnet/dotnet.exe" \
           "C:/Program Files/dotnet/dotnet.exe" \
           "$(command -v dotnet 2>/dev/null || true)"; do
    [ -n "$_dn" ] || continue
    [ -x "$_dn" ] || continue
    if "$_dn" --list-sdks 2>/dev/null | grep -q '[0-9]'; then
        DOTNET="$_dn"
        break
    fi
done

if [ -z "$DOTNET" ]; then
    echo "错误：找不到带 SDK 的 dotnet。"
    echo "      请安装 .NET SDK，或用 DOTNET=/path/to/dotnet 指定。"
    exit 1
fi

if [ "${1:-}" = "clean" ]; then
    echo "清理产物..."
    rm -rf "$DIST" "$ROOT/build/acad" "$ROOT/build/core" "$ROOT/build/zwcad" "$ROOT/build/gstarcad"
    echo "完成。"
    exit 0
fi

echo ""
echo "=== X输入法 v1.0.5 构建（AutoCAD / 中望CAD / 浩辰CAD）==="
echo "    dotnet: $DOTNET"
echo ""

# ------------------------------------------------------------
# AutoCAD 引用程序集参数
#   ACAD_SDK_ROOT 有值 -> 用 ObjectARX SDK（交付构建应当走这条）
#   否则若 USE_INSTALLED_CAD != 0 -> 退化为本机已装 AutoCAD 的程序集
# ------------------------------------------------------------
ACAD_ARGS=()
if [ -n "${ACAD_SDK_ROOT:-}" ]; then
    ACAD_ARGS+=("-p:SdkRoot=$ACAD_SDK_ROOT")
elif [ "${USE_INSTALLED_CAD:-1}" != "0" ]; then
    ACAD_ARGS+=("-p:UseInstalledCadAssemblies=true")
fi

echo "[1/5] 编译 AutoCAD 插件（逐目标框架，缺 SDK 的目标自动跳过）..."
ACAD_OK=()
for tfm in net35 net48 net8.0-windows; do
    if "$DOTNET" build "$ROOT/src/Xime.Acad/Xime.Acad.csproj" \
            -c Release -f "$tfm" "${ACAD_ARGS[@]+"${ACAD_ARGS[@]}"}" \
            --nologo -v quiet; then
        ACAD_OK+=("$tfm")
        echo "  ✓ $tfm"
    else
        echo "  ⚠ $tfm 编译失败（本机缺该版本 AutoCAD 引用的程序集），已跳过"
    fi
done

echo ""
echo "[2/5] 编译 中望 CAD 插件（net48）..."
if "$DOTNET" build "$ROOT/src/Xime.Zwcad/Xime.Zwcad.csproj" -c Release --nologo -v quiet; then
    echo "  ✓ zwcad"
else
    echo "  ✗ 中望插件编译失败"; exit 1
fi

echo ""
echo "[3/5] 编译 浩辰 CAD 插件（net8.0-windows）..."
if "$DOTNET" build "$ROOT/src/Xime.GstarCad/Xime.GstarCad.csproj" -c Release --nologo -v quiet; then
    echo "  ✓ gstarcad"
else
    echo "  ✗ 浩辰插件编译失败"; exit 1
fi

echo ""
echo "[4/5] 准备输出目录..."
# 注意：不用 "rm -rf dist" —— 某些环境下会触发安全删除拦截，
# 且 dist 里的 DLL 可能正被 CAD 占用。改为逐目录覆盖写。
mkdir -p "$DIST"

echo ""
echo "[5/5] 复制文件..."

# ---- 交付目录名（使用者可见，直接对应要放进哪个 CAD）----
D_ACAD35="AutoCAD2010-2012"
D_ACAD48="AutoCAD2013-2024"
D_ACAD8="AutoCAD2025-2026"
D_ZWCAD="ZWCAD2026"
D_GSTARCAD="GsCAD2026"

copy_pack() {
    local fw="$1" srcsub="$2"
    local srcDir="$ROOT/build/acad/Release/$srcsub"
    if [ ! -f "$srcDir/Xime.dll" ]; then
        echo "  (跳过 $fw：无产物)"
        return
    fi
    mkdir -p "$DIST/$fw"
    cp "$srcDir/Xime.dll" "$srcDir/Xime.Core.dll" "$DIST/$fw/"
    [ -f "$BUILD/assets/AutoLoad.lsp" ] && cp "$BUILD/assets/AutoLoad.lsp" "$DIST/$fw/"
    echo "  $fw  <- $srcsub"
}
copy_pack "$D_ACAD35" net35
copy_pack "$D_ACAD48" net48
copy_pack "$D_ACAD8"  net8.0-windows

# ---- 中望 CAD ----
mkdir -p "$DIST/$D_ZWCAD"
cp "$ROOT/build/zwcad/Release/net48/Xime.Zwcad.dll" "$DIST/$D_ZWCAD/"
cp "$ROOT/build/zwcad/Release/net48/Xime.Core.dll"  "$DIST/$D_ZWCAD/"
echo "  $D_ZWCAD    <- net48"

# ---- 浩辰 CAD ----
mkdir -p "$DIST/$D_GSTARCAD"
cp "$ROOT/build/gstarcad/Release/net8.0-windows/Xime.GstarCad.dll" "$DIST/$D_GSTARCAD/"
cp "$ROOT/build/gstarcad/Release/net8.0-windows/Xime.Core.dll"     "$DIST/$D_GSTARCAD/"
echo "  $D_GSTARCAD <- net8.0-windows"

# ---- 安装说明（可选：没有 Python 就跳过，不影响产物）----
PYTHON="${PYTHON:-}"
if [ -z "$PYTHON" ]; then
    for _py in "$(command -v python 2>/dev/null || true)" \
               "$(command -v python3 2>/dev/null || true)"; do
        [ -n "$_py" ] && [ -x "$_py" ] && PYTHON="$_py" && break
    done
fi
if [ -z "$PYTHON" ]; then
    for _py in "$HOME"/.workbuddy/binaries/python/versions/*/python.exe; do
        [ -x "$_py" ] && PYTHON="$_py" && break
    done
fi
if [ -n "$PYTHON" ]; then
    "$PYTHON" "$SCRIPT_DIR/gen_readme.py" || echo "  (gen_readme.py 失败，已跳过)"
else
    echo "  (跳过：未找到 Python，安装说明未生成)"
fi

# ---- ApplicationPlugins bundle（仅 AutoCAD 用；中望 / 浩辰 走注册表自动加载）----
#  bundle 内部的子目录名与上面 dist 下保持一致
#  （PackageContents.xml 的 ModuleName 已同步改成同一套名字）。
if [ -f "$DIST/$D_ACAD48/Xime.dll" ] || [ -f "$DIST/$D_ACAD8/Xime.dll" ]; then
    mkdir -p "$DIST/Xime.bundle"
    for _pair in "$D_ACAD35" "$D_ACAD48" "$D_ACAD8"; do
        [ -f "$DIST/$_pair/Xime.dll" ] || continue
        mkdir -p "$DIST/Xime.bundle/$_pair"
        cp "$DIST/$_pair/Xime.dll" "$DIST/$_pair/Xime.Core.dll" "$DIST/Xime.bundle/$_pair/"
        [ -f "$DIST/$_pair/AutoLoad.lsp" ] && cp "$DIST/$_pair/AutoLoad.lsp" "$DIST/Xime.bundle/$_pair/"
    done
    [ -f "$BUILD/assets/PackageContents.xml" ] && cp "$BUILD/assets/PackageContents.xml" "$DIST/Xime.bundle/"
fi

echo ""
echo "=== 构建完成 ==="
echo "输出目录：$DIST"
echo "  $D_ACAD35 -> AutoCAD 2010-2012（.NET 3.5）"
echo "  $D_ACAD48 -> AutoCAD 2013-2024（.NET 4.0-4.8）"
echo "  $D_ACAD8  -> AutoCAD 2025-2026（.NET 8）"
echo "  Xime.bundle         -> AutoCAD ApplicationPlugins 自动加载包"
echo "  $D_ZWCAD   -> 中望 CAD 2026（NETLOAD 选 Xime.Zwcad.dll，加载后自动写注册表）"
echo "  $D_GSTARCAD   -> 浩辰 CAD 2026（NETLOAD 选 Xime.GstarCad.dll，加载后自动写注册表）"
echo ""
find "$DIST" -type f 2>/dev/null | sed "s|$DIST/|  |" | sort
echo ""
