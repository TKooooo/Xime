#!/usr/bin/env bash
# ============================================================
#  X输入法 构建 + 打包脚本（Git Bash / Linux 环境）
#
#  用法：
#    ./build/build.sh          编译 + 打包
#    ./build/build.sh clean    清理所有产物
#
#  产出：
#    dist/net35    -> AutoCAD 2010-2012（.NET 3.5）
#    dist/net48    -> AutoCAD 2013-2024（.NET 4.0-4.8）
#    dist/net8     -> AutoCAD 2025-2026（.NET 8）
#    dist/Xime.bundle  -> ApplicationPlugins 自动加载包
# ============================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
DIST="$ROOT/dist"
BUILD="$ROOT/build"

# 优先使用托管 dotnet；没有则用系统 dotnet
if [ -x "C:/Program Files/dotnet/dotnet.exe" ]; then
    DOTNET="C:/Program Files/dotnet/dotnet.exe"
else
    DOTNET="dotnet"
fi

if [ "$1" = "clean" ]; then
    echo "清理产物..."
    rm -rf "$DIST" "$ROOT/build/acad" "$ROOT/build/core"
    echo "完成。"
    exit 0
fi

echo ""
echo "=== X输入法 v1.0.2 构建 ==="
echo ""

echo "[1/4] 编译三套目标框架..."
cd "$ROOT/src/Xime.Acad"
"$DOTNET" build -c Release --nologo -v quiet

echo "[2/4] 准备输出目录..."
# 注意：不用 "rm -rf dist" —— 某些环境下会触发安全删除拦截，
# 且 dist 里的 DLL 可能正被 AutoCAD 占用。改为逐目录覆盖写。
mkdir -p "$DIST/net35" "$DIST/net48" "$DIST/net8"

echo "[3/4] 复制文件..."

copy_pack() {
    local fw="$1"      # 输出目录名
    local src="$2"     # 编译产物目录名
    local srcDir="$ROOT/build/acad/Release/$src"
    local dstDir="$DIST/$fw"

    cp "$srcDir/Xime.dll"      "$dstDir/"
    cp "$srcDir/Xime.Core.dll" "$dstDir/"
    cp "$BUILD/assets/AutoLoad.lsp" "$dstDir/"
    echo "  $fw  <- $src"
}

copy_pack net35 net35
copy_pack net48 net48
copy_pack net8  net8.0-windows

echo "[4/4] 生成安装说明与 bundle..."
PYTHON="C:/Users/XJH/.workbuddy/binaries/python/versions/3.13.12/python.exe"
if [ -x "$PYTHON" ]; then
    "$PYTHON" "$SCRIPT_DIR/gen_readme.py"
else
    echo "  (跳过：未找到 Python，安装说明未生成)"
fi

# ApplicationPlugins bundle
mkdir -p "$DIST/Xime.bundle/net35" "$DIST/Xime.bundle/net48" "$DIST/Xime.bundle/net8"
for fw in net35 net48 net8; do
    cp "$DIST/$fw/Xime.dll" "$DIST/$fw/Xime.Core.dll" "$DIST/$fw/AutoLoad.lsp" "$DIST/Xime.bundle/$fw/"
done
cp "$BUILD/assets/PackageContents.xml" "$DIST/Xime.bundle/"

echo ""
echo "=== 构建完成 ==="
echo "输出目录：$DIST"
echo "  net35        -> AutoCAD 2010-2012"
echo "  net48        -> AutoCAD 2013-2024"
echo "  net8         -> AutoCAD 2025-2026"
echo "  Xime.bundle  -> ApplicationPlugins 自动加载包"
echo ""
find "$DIST" -type f | sed "s|$DIST/|  |" | sort
echo ""
