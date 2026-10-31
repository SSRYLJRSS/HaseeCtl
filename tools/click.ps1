param([string]$Key, [string]$Name, [int]$ProcId = 0, [int]$WaitMs = 900)
$map = @{ overview="概览"; fan="风扇控制"; power="性能模式"; battery="电池充电"; gpu="显卡模式"; kbd="键盘灯"; settings="设置与关于" }
if (-not $Name -and $Key -and $map.ContainsKey($Key)) { $Name = $map[$Key] }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if ($ProcId -eq 0) {
    $ProcId = (Get-Process HaseeCtl.App -ErrorAction SilentlyContinue | Select-Object -First 1).Id
}
if (-not $ProcId) { Write-Output "NO_PROC"; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcId)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($win -eq $null) { Write-Output "NO_WINDOW"; exit 1 }

$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
$hit = $null
foreach ($e in $all) {
    if ($e.Current.Name -eq $Name) { $hit = $e; break }
}
if ($hit -eq $null) { Write-Output "NOT_FOUND:$Name"; exit 1 }

$inv = $null
if ($hit.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$inv)) {
    $inv.Invoke()
} elseif ($hit.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$inv)) {
    $inv.Select()
} else {
    Write-Output "NO_PATTERN"
    exit 1
}
Start-Sleep -Milliseconds $WaitMs
Write-Output "CLICKED:$Name"
