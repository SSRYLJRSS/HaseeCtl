<#
  HaseeCtl 卸载脚本（当前用户）
  用法： powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-KeepSettings]
#>
[CmdletBinding()]
param([switch]$KeepSettings, [string]$RemoveDir)

$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

# 第二次调用（清理模式）：等待原进程退出后删除安装目录
if ($RemoveDir) {
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Milliseconds 500
        Remove-Item $RemoveDir -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $RemoveDir)) { break }
    }
    exit
}

$InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$taskName   = 'HaseeCtl'

Write-Host "== HaseeCtl 卸载 ==" -ForegroundColor Cyan

foreach ($name in 'HaseeCtl.App','HaseeCtl.Agent') {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 1200

try { schtasks /Delete /TN "$taskName" /F *> $null } catch { }
$startupLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\HaseeCtl Agent.lnk'
if (Test-Path $startupLnk) { Remove-Item $startupLnk -Force -ErrorAction SilentlyContinue }
Write-Host "  已移除自启项" -ForegroundColor Green

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcuts = @(
    (Join-Path $startMenu 'HaseeCtl 控制中心.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'HaseeCtl 控制中心.lnk')
)
foreach ($lnk in $shortcuts) { if (Test-Path $lnk) { Remove-Item $lnk -Force } }
Write-Host "  已删除快捷方式" -ForegroundColor Green

Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HaseeCtl' -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "  已清除卸载登记" -ForegroundColor Green

if (-not $KeepSettings) {
    Remove-Item (Join-Path $env:APPDATA 'HaseeCtl') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA 'HaseeCtl') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  已删除配置与日志" -ForegroundColor Green
} else {
    Write-Host "  已保留配置与日志" -ForegroundColor Yellow
}

# 自删除：先复制自身到临时目录再删安装目录
$tmp = Join-Path $env:TEMP 'haseectl_uninstall.ps1'
Copy-Item $MyInvocation.MyCommand.Path $tmp -Force
# 把当前目录挪走，否则进程会占住安装目录导致无法删除
Set-Location $env:TEMP

$self = $MyInvocation.MyCommand.Path
$inside = $self.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase)

if ($inside) {
    # 删除除自身以外的所有内容，再删自己也就能清空目录（无需另起进程）
    Get-ChildItem $InstallDir -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $self } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $self -Force -ErrorAction SilentlyContinue
}

for ($i = 0; $i -lt 8; $i++) {
    if (-not (Test-Path $InstallDir)) { break }
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $InstallDir)) { break }
    Start-Sleep -Milliseconds 600
}

if (Test-Path $InstallDir) {
    Write-Host "卸载完成，残留目录可手动删除：$InstallDir" -ForegroundColor Yellow
} else {
    Write-Host "卸载完成。" -ForegroundColor Cyan
}
