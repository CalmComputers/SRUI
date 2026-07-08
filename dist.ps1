# Builds a redistributable srui drop in dist/: the managed assemblies
# (Srui.Net, Srui.Audio) plus every native DLL they load, all Release.
# Run from the repository root; consume as shown in samples/HelloSrui.
$ErrorActionPreference = "Stop"

cargo build --release
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
dotnet build dotnet/Srui.Net/Srui.Net.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Srui.Net build failed" }
dotnet build dotnet/Srui.Audio/Srui.Audio.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Srui.Audio build failed" }

$dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

Copy-Item dotnet/Srui.Net/bin/Release/net10.0/Srui.Net.dll $dist
Copy-Item dotnet/Srui.Audio/bin/Release/net10.0/Srui.Audio.dll $dist
# UI stack: srui_ffi (core+SDL host+speech ABI), prism (speech), SDL3
# (window/input). Audio: cosmos (engine/DSP), phonon (Steam Audio HRTF).
foreach ($dll in "srui_ffi.dll", "prism.dll", "SDL3.dll", "cosmos.dll", "phonon.dll") {
    Copy-Item (Join-Path "target/release" $dll) $dist
}

Write-Host "dist/ ready: $((Get-ChildItem $dist).Name -join ', ')"
