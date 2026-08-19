# Builds a flat redistributable srui drop in dist/: the managed assemblies
# (Srui, Srui.Audio) with their XML documentation, every native DLL they
# load, and the licence texts that redistribution requires.
#
# This is the path for consumers who cannot take a NuGet dependency. The
# supported path is ./pack.ps1, which produces packages that carry the
# native binaries themselves; samples/HelloSrui shows that arrangement.
#
# Wiring a flat drop by hand means a <Reference> with a <HintPath> per
# assembly, plus a <None ... CopyToOutputDirectory> per native DLL, since
# nothing resolves them for you.
$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "native\build-native.ps1")
dotnet build Srui/Srui.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Srui build failed" }
dotnet build Srui.Audio/Srui.Audio.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Srui.Audio build failed" }

$dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

# The .xml beside each .dll is what gives a consumer IntelliSense.
Copy-Item Srui/bin/Release/net10.0/Srui.dll $dist
Copy-Item Srui/bin/Release/net10.0/Srui.xml $dist
Copy-Item Srui.Audio/bin/Release/net10.0/Srui.Audio.dll $dist
Copy-Item Srui.Audio/bin/Release/net10.0/Srui.Audio.xml $dist

# UI stack: prism (speech), SDL3 (window/input). Audio: cosmos
# (engine/DSP), phonon (Steam Audio HRTF, a hard import of cosmos).
foreach ($dll in "prism.dll", "SDL3.dll", "cosmos.dll", "phonon.dll") {
    Copy-Item (Join-Path "native/out" $dll) $dist
}

# Apache-2.0 for srui itself; the bundled natives carry their own terms,
# and both Apache-2.0 and Zlib require the text travel with the binaries.
Copy-Item (Join-Path $PSScriptRoot "LICENSE") $dist
Copy-Item (Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.md") $dist

Write-Host "dist/ ready: $((Get-ChildItem $dist).Name -join ', ')"
