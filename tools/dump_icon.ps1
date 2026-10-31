Add-Type -AssemblyName System.Drawing
$dir = "$PSScriptRoot\..\dist\HaseeCtl"
$out = "$PSScriptRoot\..\_icocheck"
New-Item -ItemType Directory -Force -Path $out | Out-Null
foreach ($n in @("HaseeCtl.App", "HaseeCtl.Agent")) {
    $p = Join-Path $dir "$n.exe"
    if (-not (Test-Path $p)) { Write-Output "$n MISSING"; continue }
    $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($p)
    if ($ico -eq $null) { Write-Output "$n NO_ICON"; continue }
    $bmp = $ico.ToBitmap()
    $bmp.Save((Join-Path $out "$n.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output ("{0} ok {1}x{2}" -f $n, $bmp.Width, $bmp.Height)
}
