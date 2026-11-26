# Builds the release files into dist\:
#   OrbitSetup-<version>.exe        installer (per-user, no admin), carries a self-contained Orbit
#   Orbit-<version>-win-x64.zip     portable copy: unzip anywhere and run Orbit.exe
# Usage (from the repo root):  pwsh -File tools\build-release.ps1
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$version = ([xml](Get-Content (Join-Path $root "Orbit.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root "dist"
$publish = Join-Path $root "dist\publish"
$payload = Join-Path $root "installer\payload"

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist, $payload -Force | Out-Null

Write-Host "Publishing Orbit $version (self-contained, win-x64)…"
dotnet publish (Join-Path $root "Orbit.csproj") -c Release -r win-x64 --self-contained true -o $publish `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE) { throw "publish failed" }
Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item -Force

$zip = Join-Path $dist "Orbit-$version-win-x64.zip"
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal
Copy-Item $zip (Join-Path $payload "payload.zip") -Force

Write-Host "Building the installer…"
$setupOut = Join-Path $root "installer\bin\Release"
dotnet build (Join-Path $root "installer\OrbitSetup.csproj") -c Release -p:Version=$version -o $setupOut
if ($LASTEXITCODE) { throw "installer build failed" }
Copy-Item (Join-Path $setupOut "OrbitSetup.exe") (Join-Path $dist "OrbitSetup-$version.exe") -Force

Get-ChildItem $dist -File | ForEach-Object { "{0,-34} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
Get-ChildItem $dist -File | Get-FileHash -Algorithm SHA256 | ForEach-Object { "{0}  {1}" -f $_.Hash.ToLower(), (Split-Path $_.Path -Leaf) } |
    Set-Content (Join-Path $dist "SHA256SUMS.txt")
