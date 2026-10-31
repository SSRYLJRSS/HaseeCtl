$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = (Get-Process HaseeCtl.App -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $p) { "NO_PROC" | Out-File -Encoding utf8 "$env:TEMP\uia.txt"; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { "NO_WINDOW" | Out-File -Encoding utf8 "$env:TEMP\uia.txt"; exit 1 }
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
$lines = New-Object System.Collections.Generic.List[string]
foreach ($e in $all) {
    $n = $e.Current.Name
    $t = $e.Current.ControlType.ProgrammaticName
    $id = $e.Current.AutomationId
    $cls = $e.Current.ClassName
    if ($n -or $id) { $lines.Add("$t|$id|$cls|$n") }
}
$lines | Out-File -Encoding utf8 "$env:TEMP\uia.txt"
Write-Output ("count=" + $lines.Count)
