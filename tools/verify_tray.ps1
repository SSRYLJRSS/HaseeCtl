Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$dll = Join-Path $env:LOCALAPPDATA "Programs\HaseeCtl\HaseeCtl.Agent.dll"
$asm = [System.Reflection.Assembly]::LoadFrom($dll)

function Grab([int]$n) {
    $stream = $asm.GetManifestResourceStream("HaseeCtl.Agent.app.ico")
    $ico = New-Object System.Drawing.Icon($stream, $n, $n)
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(40, 40, 40))
    $g.DrawIcon($ico, (New-Object System.Drawing.Rectangle 0, 0, $n, $n))
    $g.Dispose()
    $bmp.Save("$PSScriptRoot\..\_icocheck\tray-$n.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "drawn $n"
}

Grab 20
Grab 32
Grab 64
Grab 128
