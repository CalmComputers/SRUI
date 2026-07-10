# Builds a redistributable srui drop in dist/: the managed assemblies
# (Srui.Net, Srui.Audio) plus every native DLL they load.
# Run from the repository root; consume as shown in samples/HelloSrui.
$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "native\build-native.ps1")
dotnet build Srui.Net/Srui.Net.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Srui.Net build failed" }
dotnet build Srui.Audio/Srui.Audio.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Srui.Audio build failed" }

$dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

Copy-Item Srui.Net/bin/Release/net10.0/Srui.Net.dll $dist
Copy-Item Srui.Audio/bin/Release/net10.0/Srui.Audio.dll $dist
# UI stack: prism (speech), SDL3 (window/input). Audio: cosmos
# (engine/DSP), phonon (Steam Audio HRTF).
foreach ($dll in "prism.dll", "SDL3.dll", "cosmos.dll", "phonon.dll") {
    Copy-Item (Join-Path "native/out" $dll) $dist
}

Write-Host "dist/ ready: $((Get-ChildItem $dist).Name -join ', ')"
