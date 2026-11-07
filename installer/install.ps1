param([string]$Target = '')
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$default = Join-Path $env:LOCALAPPDATA 'DesktopShade'
if (-not $Target) {
    Write-Host ("直接回车装到默认位置，或输入你想要的路径（例如 D:\Apps\DesktopShade）")
    Write-Host ("[" + $default + "] ") -NoNewline
    $Target = (Read-Host).Trim()
    if (-not $Target) { $Target = $default }
}
try { $Target = [System.IO.Path]::GetFullPath($Target) } catch { Write-Host ("路径写错了：" + $Target); exit 1 }
$old = $null
try {
    $v = (Get-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).GetValue('DesktopShade')
    if ($v -match '"([^"]+)"') { $old = Split-Path -Parent $Matches[1] }
} catch { }
if ($old -and ($old -ne $Target)) { Write-Host ("注意到已装在别处：" + $old + "  —— 会改装到新位置，旧目录请自行删除") }
try { New-Item -ItemType Directory -Force $Target | Out-Null }
catch { Write-Host ("建不出来（权限或路径非法）：" + $_.Exception.Message); exit 1 }
Get-Process DesktopShade -ErrorAction SilentlyContinue | ForEach-Object {
    try { Start-Process -FilePath $_.Path -ArgumentList '--show' -Wait -NoNewWindow -ErrorAction SilentlyContinue } catch { }
}
Get-Process DesktopShade -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
try {
    Copy-Item (Join-Path $here 'DesktopShade.exe') $Target -Force
    $ini = Join-Path $Target 'desktopshade.ini'
    if (-not (Test-Path $ini)) { Copy-Item (Join-Path $here 'desktopshade.ini') $ini -Force }
} catch { Write-Host ("复制失败（这个目录不可写？）：" + $_.Exception.Message); exit 1 }
Start-Process -FilePath (Join-Path $Target 'DesktopShade.exe') -ArgumentList '--install-autostart' -Wait -NoNewWindow
Start-Process -FilePath (Join-Path $Target 'DesktopShade.exe')
Write-Host ''
Write-Host ("已装到 " + $Target)
Write-Host '现在到桌面空白处连点两下左键试试；再两下就放回来。'
Write-Host '想改行为就编辑同目录的 desktopshade.ini，改完自动生效。'
