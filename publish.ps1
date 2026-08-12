# Publish single-file SilverManager.exe (self-contained, win-x64)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot\SteamVault

$out = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host "Publishing SilverManager single-file..." -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o $out

Write-Host ""
$exe = Get-ChildItem $out -Filter "SilverManager.exe" | Select-Object -First 1
if ($exe) {
  Write-Host "Done: $($exe.FullName)" -ForegroundColor Green
  $exe | Format-List Name, Length, LastWriteTime
} else {
  Write-Host "Publish finished but SilverManager.exe not found in $out" -ForegroundColor Yellow
  Get-ChildItem $out | Format-Table Name, Length
}
