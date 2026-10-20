# ============================================================
#  X输入法 打包脚本（PowerShell）
#  产出三套独立可用的 DLL：
#    dist\net35  -> AutoCAD 2010-2012（.NET 3.5）
#    dist\net48  -> AutoCAD 2013-2024（.NET 4.0-4.8）
#    dist\net8   -> AutoCAD 2025-2026（.NET 8）
#  以及 ApplicationPlugins 自动加载包 dist\Xime.bundle
# ============================================================

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$Root   = Split-Path -Parent $PSScriptRoot
$Build  = Join-Path $Root 'build'
$Dist   = Join-Path $Root 'dist'
$Assets = Join-Path $Build 'assets'

Write-Host ''
Write-Host '=== X输入法 v1.0.5 打包 ===' -ForegroundColor Cyan
Write-Host ''

# ---- 1. 编译 ----
Write-Host '[1/4] 编译三套目标框架...' -ForegroundColor Yellow
Push-Location (Join-Path $Root 'src\Xime.Acad')
try {
    & dotnet build -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw '编译失败' }
} finally {
    Pop-Location
}

# ---- 2. 准备目录 ----
Write-Host '[2/4] 准备输出目录...' -ForegroundColor Yellow
if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
$null = New-Item -ItemType Directory -Path $Dist -Force
foreach ($fw in @('net35', 'net48', 'net8')) {
    $null = New-Item -ItemType Directory -Path (Join-Path $Dist $fw) -Force
}

# ---- 3. 复制产物 ----
Write-Host '[3/4] 复制文件...' -ForegroundColor Yellow

$map = @(
    @{ Folder = 'net35'; Src = 'net35' },
    @{ Folder = 'net48'; Src = 'net48' },
    @{ Folder = 'net8';  Src = 'net8.0-windows' }
)

foreach ($m in $map) {
    $srcDir = Join-Path $Build ("acad\Release\" + $m.Src)
    $dstDir = Join-Path $Dist $m.Folder

    Copy-Item (Join-Path $srcDir 'Xime.dll')      $dstDir -Force
    Copy-Item (Join-Path $srcDir 'Xime.Core.dll') $dstDir -Force
    Copy-Item (Join-Path $Assets 'AutoLoad.lsp')  $dstDir -Force
}

# ---- 4. 生成安装说明 ----
Write-Host '[4/4] 生成安装说明...' -ForegroundColor Yellow

$readmeMap = @(
    @{ Folder = 'net35'; Cad = 'AutoCAD 2010 - 2012'; Net = '.NET Framework 3.5' },
    @{ Folder = 'net48'; Cad = 'AutoCAD 2013 - 2024'; Net = '.NET Framework 4.0 - 4.8' },
    @{ Folder = 'net8';  Cad = 'AutoCAD 2025 - 2026'; Net = '.NET 8.0' }
)

$readmeTemplate = @'
============================================================
  X输入法 v1.0.5  安装说明
============================================================

适用环境
  目标框架：{NET}
  AutoCAD ：{CAD}
  操作系统：Windows 7 / 10 / 11

安装步骤
  1. 把本目录下的 Xime.dll 和 Xime.Core.dll 放到一个固定文件夹，
     例如：D:\CAD插件\X输入法\
     注意：两个 DLL 必须放在同一目录。

  2. 打开 AutoCAD，输入命令：
        NETLOAD
     在弹窗中选择 Xime.dll，加载插件。

  3. 加载成功后，插件会自动写入注册表实现自动加载，
     下次启动 AutoCAD 无需再手动 NETLOAD。
     也可把 autoLoad.lsp 加入支持路径实现 LISP 方式自动加载。

  4. 输入一级命令：xime
     打开设置界面。插件加载后**默认已启用**，无需额外操作；
     若之前手动关过，在这里勾选「启用 X输入法」即可恢复。

常用命令
  xime        - 打开设置界面
  XIMESTATUS  - 查看运行状态
  XIMEON      - 快速启用
  XIMEOFF     - 快速关闭
  XIMEDIAG    - 环境诊断

卸载
  在 xime 界面点击「卸载 X输入法」，删除自动加载注册项，
  然后手动删除 DLL 文件夹即可。

注意事项
  1. 首次加载时若杀毒软件拦截，请选择「允许」。
  2. 若重启后未自动加载，请重新 NETLOAD 一次或检查注册表权限。
  3. 本插件自动适配天正、探索者、鸿业等基于 AutoCAD 的软件。
============================================================
'@

foreach ($r in $readmeMap) {
    $text = $readmeTemplate.Replace('{NET}', $r.Net).Replace('{CAD}', $r.Cad)
    $path = Join-Path (Join-Path $Dist $r.Folder) '安装说明.txt'
    # 以 UTF-8 带 BOM 写入，保证 Windows 记事本正确显示中文
    [System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($true))
}

# ---- 5. ApplicationPlugins 整体包 ----
$bundle = Join-Path $Dist 'Xime.bundle'
$null = New-Item -ItemType Directory -Path $bundle -Force
foreach ($fw in @('net35', 'net48', 'net8')) {
    $sub = Join-Path $bundle $fw
    $null = New-Item -ItemType Directory -Path $sub -Force
    Copy-Item (Join-Path (Join-Path $Dist $fw) '*') $sub -Force
}
Copy-Item (Join-Path $Assets 'PackageContents.xml') $bundle -Force

# ---- 完成 ----
Write-Host ''
Write-Host '=== 打包完成 ===' -ForegroundColor Green
Write-Host "输出目录：$Dist"
Write-Host '  net35        -> AutoCAD 2010-2012'
Write-Host '  net48        -> AutoCAD 2013-2024'
Write-Host '  net8         -> AutoCAD 2025-2026'
Write-Host '  Xime.bundle  -> ApplicationPlugins 自动加载包'
Write-Host ''

Get-ChildItem $Dist -Directory | ForEach-Object {
    $files = Get-ChildItem $_.FullName -File -Recurse | Measure-Object -Property Length -Sum
    Write-Host ("  {0,-14} {1,2} 个文件  {2,8:N0} KB" -f $_.Name, $files.Count, ($files.Sum / 1KB))
}
Write-Host ''
