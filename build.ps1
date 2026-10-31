# 构建并打包到 dist\HaseeCtl（框架依赖，win-x64）
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "dist\HaseeCtl"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
foreach ($p in "src\HaseeCtl.App", "src\HaseeCtl.Agent") {
    dotnet publish (Join-Path $root $p) -c $Configuration -r win-x64 --self-contained false -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish $p 失败" }
}
Copy-Item (Join-Path $root "installer\*") $out -Force
Copy-Item (Join-Path $root "docs\使用说明.md") $out -Force
Get-ChildItem $out -Filter *.pdb | Remove-Item -Force
Write-Host "完成：$out" -ForegroundColor Green
