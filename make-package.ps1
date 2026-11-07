# 编译并组装成可发给别人的 zip（zip 里就是 exe + ini 模板 + 安装/卸载脚本 + 使用说明）
param([string]$Version = '1.1')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$stage = Join-Path $root 'pkg\DesktopShade'
$zip = Join-Path $root ("DesktopShade-" + $Version + ".zip")

& (Join-Path $root 'build.ps1')

if (Test-Path (Join-Path $root 'pkg')) { Remove-Item (Join-Path $root 'pkg') -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $root 'bin\DesktopShade.exe') $stage -Force
Copy-Item (Join-Path $root 'config.template.ini') (Join-Path $stage 'desktopshade.ini') -Force
Copy-Item (Join-Path $root 'installer\*') $stage -Force
Copy-Item (Join-Path $root 'docs\使用说明.txt') $stage -Force

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

Write-Output ("已生成 " + $zip + "  " + (Get-Item $zip).Length + " 字节")
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
$z.Entries | ForEach-Object { Write-Output ("  {0,7}  {1}" -f $_.Length, $_.FullName) }
$z.Dispose()
