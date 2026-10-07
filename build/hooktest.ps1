# 键盘钩子活体验证脚本
# 启动 HookLiveTest 窗体，然后模拟敲击字母键，验证钩子是否拦截。
Add-Type -AssemblyName System.Windows.Forms

$exe = "C:\Users\XJH\WorkBuddy\2026-10-01-11-40-37\Xime\src\Xime.CliTest\bin\Release\net48\XimeTest.exe"

# 启动窗体（自动 8 秒后退出）
$p = Start-Process -FilePath $exe -ArgumentList "hooklive --auto 8" -PassThru
Start-Sleep -Seconds 2

# 模拟按键 A B C（用 SendKeys 走真实的键盘消息路径）
[System.Windows.Forms.SendKeys]::SendWait("ABC")

Start-Sleep -Seconds 1
[System.Windows.Forms.SendKeys]::SendWait("XYZ")

Start-Sleep -Seconds 6
Write-Output "测试进程已退出，退出码: $($p.ExitCode)"
