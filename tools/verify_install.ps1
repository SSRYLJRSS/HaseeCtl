Add-Type -AssemblyName System.Drawing
$inst = Join-Path $env:LOCALAPPDATA "Programs\HaseeCtl"
$out = Join-Path $PSScriptRoot "..\_icocheck"
New-Item -ItemType Directory -Force -Path $out | Out-Null

foreach ($n in @("HaseeCtl.App", "HaseeCtl.Agent")) {
    $p = Join-Path $inst "$n.exe"
    $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($p)
    $bmp = $ico.ToBitmap()
    $bmp.Save((Join-Path $out "inst-$n.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $c = $bmp.GetPixel([int]($bmp.Width / 2), [int]($bmp.Height * 0.2))
    Write-Output ("{0}: {1}x{2} topPixel={3},{4},{5}" -f $n, $bmp.Width, $bmp.Height, $c.R, $c.G, $c.B)
}

# desktop + start menu shortcut targets and icon locations
$sh = New-Object -ComObject WScript.Shell
foreach ($lnk in @("$env:USERPROFILE\Desktop\HaseeCtl 控制中心.lnk",
                   "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\HaseeCtl 控制中心.lnk",
                   "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\HaseeCtl Agent.lnk")) {
    if (Test-Path $lnk) {
        $s = $sh.CreateShortcut($lnk)
        Write-Output ("LNK {0} -> {1} | icon={2}" -f (Split-Path $lnk -Leaf), $s.TargetPath, $s.IconLocation)
    } else {
        Write-Output ("LNK missing: " + $lnk)
    }
}

# embedded tray icon resource
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $inst "HaseeCtl.Agent.dll"))
Write-Output ("resources: " + ($asm.GetManifestResourceNames() -join ", "))
