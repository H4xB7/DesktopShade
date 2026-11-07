param([string]$Target = '')
$ErrorActionPreference = 'Stop'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$v = $null
try { $v = (Get-Item $runKey -ErrorAction SilentlyContinue).GetValue('DesktopShade') } catch { }
if (-not $Target) {
    if ($v -match '"([^"]*DesktopShade\.exe)"') { $Target = Split-Path -Parent $Matches[1] }
    else { $Target = Join-Path $env:LOCALAPPDATA 'DesktopShade' }   # 自启被关过就退回默认位置
}
$target = $Target
$exe = Join-Path $target 'DesktopShade.exe'
Write-Host '将执行：'
Write-Host '  1. 结束 DesktopShade 进程'
Write-Host '  2. 注销登录自启（HKCU\...\Run\DesktopShade）'
Write-Host ('  3. 删除目录 ' + $target)
Write-Host '  4. 桌面图标恢复显示（如果正被隐藏）'
Get-Process DesktopShade -ErrorAction SilentlyContinue | ForEach-Object { Start-Process -FilePath $_.Path -ArgumentList '--show' -Wait -NoNewWindow -ErrorAction SilentlyContinue }
Get-Process DesktopShade -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
if (Test-Path $exe) { Start-Process -FilePath $exe -ArgumentList '--uninstall-autostart' -Wait -NoNewWindow -ErrorAction SilentlyContinue }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ((Get-Item $runKey -ErrorAction SilentlyContinue).GetValue('DesktopShade')) {
    Remove-ItemProperty -Path $runKey -Name 'DesktopShade' -ErrorAction SilentlyContinue   # 只删自己那一条，别碰整个 Run 键
}
if (Test-Path $target) { Remove-Item -Recurse -Force $target }
Write-Host '完成。'
