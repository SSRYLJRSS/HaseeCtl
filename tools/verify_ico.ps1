Add-Type -AssemblyName System.Drawing
$out = "$PSScriptRoot\..\_icocheck"
$icoPath = "$PSScriptRoot\..\src\HaseeCtl.App\Assets\app.ico"
foreach ($n in @(16, 20, 32, 48, 64, 128)) {
    $ico = New-Object System.Drawing.Icon($icoPath, $n, $n)
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(40, 40, 40))
    $g.DrawIcon($ico, (New-Object System.Drawing.Rectangle 0, 0, $n, $n))
    $g.Dispose()
    $bmp.Save((Join-Path $out "ico-$n.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Write-Output "done"
