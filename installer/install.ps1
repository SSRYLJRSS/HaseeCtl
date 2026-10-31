<#
  HaseeCtl 安装脚本（当前用户，无需管理员权限）
  用法： powershell -ExecutionPolicy Bypass -File install.ps1
        可选参数 -InstallDir <目录>  -NoAutostart  -NoShortcuts
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\HaseeCtl",
    [switch]$NoAutostart,
    [switch]$NoShortcuts
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$appExe   = Join-Path $InstallDir 'HaseeCtl.App.exe'
$agentExe = Join-Path $InstallDir 'HaseeCtl.Agent.exe'
$taskName = 'HaseeCtl'

Write-Host "== HaseeCtl 安装 ==" -ForegroundColor Cyan
Write-Host "源目录: $source"
Write-Host "目标目录: $InstallDir"

# 0. 先结束正在运行的实例，避免文件被占用
foreach ($name in 'HaseeCtl.App','HaseeCtl.Agent') {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 600

# 1. 复制文件
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Get-ChildItem -Path $source -File | Where-Object { $_.Name -notin @('install.ps1','安装.cmd','uninstall.ps1.bak') } |
    ForEach-Object { Copy-Item $_.FullName -Destination $InstallDir -Force }
Write-Host "  [1/5] 文件已复制" -ForegroundColor Green

# 3. 快捷方式
if (-not $NoShortcuts) {
    $shell = New-Object -ComObject WScript.Shell
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $links = @(
        (Join-Path $startMenu 'HaseeCtl 控制中心.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'HaseeCtl 控制中心.lnk')
    )
    foreach ($lnk in $links) {
        $s = $shell.CreateShortcut($lnk)
        $s.TargetPath = $appExe
        $s.WorkingDirectory = $InstallDir
        $s.IconLocation = "$appExe,0"
        $s.Description = 'HaseeCtl 控制中心'
        $s.Save()
    }
    Write-Host "  [2/5] 已创建开始菜单与桌面快捷方式" -ForegroundColor Green
} else {
    Write-Host "  [2/5] 已跳过快捷方式" -ForegroundColor Yellow
}

# 4. 登录时自动启动后台服务（任务计划，受限权限即可）
if (-not $NoAutostart) {
    $startupDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
    $startupLnk = Join-Path $startupDir 'HaseeCtl Agent.lnk'
    $taskOk = $false
    try {
        $action = "`"$agentExe`""
        schtasks /Delete /TN "$taskName" /F *> $null
        schtasks /Create /TN "$taskName" /TR $action /SC ONLOGON /RL LIMITED /F *> $null
        $taskOk = ($LASTEXITCODE -eq 0)
    } catch { $taskOk = $false }

    if ($taskOk) {
        Write-Host "  [3/5] 已注册登录自启任务" -ForegroundColor Green
    } else {
        # 退路：启动文件夹里的快捷方式，权限永远够
        New-Item -ItemType Directory -Force -Path $startupDir | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $s = $shell.CreateShortcut($startupLnk)
        $s.TargetPath = $agentExe
        $s.WorkingDirectory = $InstallDir
        $s.IconLocation = "$agentExe,0"
        $s.Description = 'HaseeCtl 后台服务（开机自启）'
        $s.Save()
        Write-Host "  [3/5] 已放入启动文件夹（登录后自动运行）" -ForegroundColor Green
    }
} else {
    Write-Host "  [3/5] 已跳过自启" -ForegroundColor Yellow
}

# 5. 卸载登记（设置 → 应用 中可见）
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HaseeCtl'
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name DisplayName     -Value 'HaseeCtl 控制中心'
Set-ItemProperty -Path $uninstallKey -Name DisplayVersion  -Value '1.0.0'
Set-ItemProperty -Path $uninstallKey -Name Publisher       -Value 'HaseeCtl'
Set-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $InstallDir
Set-ItemProperty -Path $uninstallKey -Name DisplayIcon     -Value "$appExe,0"
Set-ItemProperty -Path $uninstallKey -Name UninstallString -Value "powershell -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
Set-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -Type DWord
Write-Host "  [4/5] 已登记卸载信息" -ForegroundColor Green

# 6. 立即启动后台服务
Start-Process -FilePath $agentExe -WorkingDirectory $InstallDir
Start-Sleep -Milliseconds 800
Write-Host "  [5/5] 后台服务已启动（托盘图标）" -ForegroundColor Green

Write-Host ""
Write-Host "安装完成。" -ForegroundColor Cyan
Write-Host "  界面：开始菜单/桌面 “HaseeCtl 控制中心”，或托盘的“打开控制中心”"
Write-Host "  配置：$env:APPDATA\HaseeCtl\settings.json"
Write-Host "  日志：$env:LOCALAPPDATA\HaseeCtl\logs"
