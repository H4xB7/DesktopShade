# 用系统自带的 .NET Framework 4 编译器，无需安装任何东西
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 $csc —— 需要 Windows 自带的 .NET Framework 4" }

& $csc /nologo /target:winexe /platform:anycpu ("/out:" + (Join-Path $out 'DesktopShade.exe')) `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll (Join-Path $root 'src\DesktopShade.cs')
if ($LASTEXITCODE -ne 0) { throw "编译失败 $LASTEXITCODE" }

$exe = Get-Item (Join-Path $out 'DesktopShade.exe')
Write-Output ("OK  " + $exe.FullName + "  " + $exe.Length + " 字节  " + $exe.VersionInfo.FileVersion)
