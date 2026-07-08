# Builds every native DLL the .NET projects need, into native/out/:
#
#   cosmos.dll  — miniaudio + cosmos DSP + opus stack (CMake, MSVC cl)
#   prism.dll   — speech/braille via Prism (CMake, clang-cl: needs C23/C++23,
#                 which MSVC cl cannot compile; MinGW lacks the ATL and
#                 C++/WinRT headers the Windows backends need)
#   phonon.dll  — Steam Audio, shipped as a prebuilt binary
#   SDL3.dll    — prebuilt (native/prebuilt/); replace from an official
#                 SDL release to upgrade
#
# The DLLs are optimized C/C++ regardless of the .NET configuration, so
# there is one output directory, not a debug/release pair.
#
# Machine-specific defaults are overridable via env vars:
#   SRUI_CMAKE    — cmake.exe (needs C23/clang-cl support; 3.20+)
#   SRUI_NINJA    — ninja.exe
#   SRUI_CLANG_CL — clang-cl.exe
#   SRUI_VCVARS   — vcvars64.bat
#   PRISM_MIDL    — midl.exe (default: newest Windows Kits 10 x64 midl,
#                   which vcvars lets find cl.exe for the NVDA stub)

$ErrorActionPreference = 'Stop'

$native = $PSScriptRoot
$root = Split-Path -Parent $native

function Default($value, $fallback) { if ($value) { $value } else { $fallback } }

$cmake = Default $env:SRUI_CMAKE 'C:\msys64\clang64\bin\cmake.exe'
$ninja = Default $env:SRUI_NINJA 'C:\msys64\clang64\bin\ninja.exe'
$clangCl = Default $env:SRUI_CLANG_CL 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang-cl.exe'
$vcvars = Default $env:SRUI_VCVARS 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'

# Newest installed Windows Kits 10 x64 midl.exe unless overridden.
$midl = $env:PRISM_MIDL
if (-not $midl) {
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    $midl = Get-ChildItem $kits -Directory |
        Where-Object { $_.Name -like '10.*' } |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\midl.exe' } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $midl) { throw 'midl.exe not found; install a Windows 10/11 SDK or set PRISM_MIDL' }
}

# Sources (updated when the repo layout changes).
$cosmosSrc = Join-Path $root 'srui-audio-native\csrc'
$phononDir = Join-Path $root 'srui-audio-native\phonon'
$prismSrc = Join-Path $root 'srui-prism-sys\prism'

$out = Join-Path $native 'out'
$buildRoot = Join-Path $native 'build'
New-Item -ItemType Directory -Force $out | Out-Null
New-Item -ItemType Directory -Force $buildRoot | Out-Null

# CMake wants forward slashes in cache paths; cmd wants scripts quoted.
# A batch file keeps the vcvars environment across every step.
function Fwd($path) { "$path" -replace '\\', '/' }

$lldLink = Join-Path (Split-Path -Parent $clangCl) 'lld-link.exe'
$cosmosBuild = Join-Path $buildRoot 'cosmos'
$prismBuild = Join-Path $buildRoot 'prism'

$bat = Join-Path $buildRoot 'build-native.bat'
$lines = @(
    '@echo off'
    "call `"$vcvars`" >nul || exit /b 1"
    ':: cosmos — MSVC cl via Ninja'
    "`"$cmake`" -S `"$(Fwd $cosmosSrc)`" -B `"$(Fwd $cosmosBuild)`" -G Ninja -DCMAKE_BUILD_TYPE=Release `"-DCMAKE_MAKE_PROGRAM=$(Fwd $ninja)`" || exit /b 1"
    "`"$cmake`" --build `"$(Fwd $cosmosBuild)`" || exit /b 1"
    ':: prism — clang-cl (MSVC ABI, real SDK headers)'
    "`"$cmake`" -S `"$(Fwd $prismSrc)`" -B `"$(Fwd $prismBuild)`" -G Ninja -DCMAKE_BUILD_TYPE=Release `"-DCMAKE_C_COMPILER=$(Fwd $clangCl)`" `"-DCMAKE_CXX_COMPILER=$(Fwd $clangCl)`" `"-DCMAKE_LINKER=$(Fwd $lldLink)`" `"-DCMAKE_CXX_FLAGS=/EHsc`" `"-DCMAKE_MAKE_PROGRAM=$(Fwd $ninja)`" -DPRISM_ENABLE_TESTS=OFF -DPRISM_ENABLE_DEMOS=OFF -DPRISM_ENABLE_GDEXTENSION=OFF `"-DMIDL_COMPILER=$(Fwd $midl)`" || exit /b 1"
    "`"$cmake`" --build `"$(Fwd $prismBuild)`" || exit /b 1"
)
Set-Content -Path $bat -Value ($lines -join "`r`n") -Encoding ascii

& cmd /C $bat
if ($LASTEXITCODE -ne 0) { throw "native build failed (exit $LASTEXITCODE)" }

# Stage everything into out/.
Copy-Item (Join-Path $cosmosBuild 'cosmos.dll') $out -Force

$prismDll = @('prism.dll', 'libprism.dll') |
    ForEach-Object { Join-Path $prismBuild $_ } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $prismDll) { throw 'built prism DLL not found in build dir' }
Copy-Item $prismDll (Join-Path $out 'prism.dll') -Force

Copy-Item (Join-Path $phononDir 'phonon.dll') $out -Force
Copy-Item (Join-Path $native 'prebuilt\SDL3.dll') $out -Force

Write-Host "native DLLs staged in $out"
