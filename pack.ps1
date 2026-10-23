# Builds the NuGet packages into artifacts/: Srui, Srui.Audio, and
# Srui.Testing, each carrying its managed assembly and XML documentation
# (the first two also embed the native libraries they load, under
# runtimes/win-x64/native/ and runtimes/osx/native/), plus the content-only
# Srui.Templates.
#
# Run from the repository root, on Windows or on macOS (PowerShell 7:
# brew install powershell). The native step comes first because the
# packages embed its output; packing without it fails rather than producing
# a package that compiles for consumers and dies on the first P/Invoke.
#
#   ./pack.ps1                       # 0.1.0, this platform's natives
#   ./pack.ps1 -VersionSuffix rc.1   # 0.1.0-rc.1
#   ./pack.ps1 -Runtimes win-x64,osx -SkipNative
#                                    # both platforms, from a native/out
#                                    # holding both builds' output
#
# Publishing is deliberately a separate, manual step:
#   dotnet nuget push artifacts/*.nupkg -s https://api.nuget.org/v3/index.json -k <key>
# nuget.org versions cannot be withdrawn once pushed, only unlisted.

param(
    [string]$VersionSuffix = "",
    # Package RID folders to carry natives for (SruiPackRuntimes); empty
    # means this platform's own, the Directory.Build.props default.
    [string[]]$Runtimes = @(),
    [switch]$SkipNative
)

$ErrorActionPreference = "Stop"

if (-not $SkipNative) {
    # The platform's own native build: build-native.ps1 on Windows, and
    # build-native.sh on macOS (PowerShell 7 there; Windows PowerShell 5.1 has
    # no $IsMacOS, which reads as false).
    if ($IsMacOS) {
        & (Join-Path $PSScriptRoot "native/build-native.sh")
        if ($LASTEXITCODE -ne 0) { throw "native build failed (exit $LASTEXITCODE)" }
    } else {
        & (Join-Path $PSScriptRoot "native\build-native.ps1")
    }
}

$artifacts = Join-Path $PSScriptRoot "artifacts"
if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }
New-Item -ItemType Directory -Force $artifacts | Out-Null

# ContinuousIntegrationBuild normalises the source paths SourceLink records,
# so the package does not carry this machine's directory layout.
$packArgs = @(
    "pack", (Join-Path $PSScriptRoot "Srui.slnx"),
    "-c", "Release",
    "-o", $artifacts,
    "-p:ContinuousIntegrationBuild=true"
)
if ($VersionSuffix) { $packArgs += "--version-suffix"; $packArgs += $VersionSuffix }
# %3B is MSBuild's escape for the list separator, which the shell would
# otherwise split on.
if ($Runtimes) { $packArgs += "-p:SruiPackRuntimes=$($Runtimes -join '%3B')" }

dotnet @packArgs
if ($LASTEXITCODE -ne 0) { throw "pack failed" }

Get-ChildItem $artifacts -Filter *.nupkg | ForEach-Object {
    "{0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)
}
