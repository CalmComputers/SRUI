# Builds the NuGet packages into artifacts/: Srui, Srui.Audio, and
# Srui.Testing, each carrying its managed assembly and XML documentation;
# the first two also embed the native DLLs they load, under
# runtimes/win-x64/native/.
#
# Run from the repository root. The native step comes first because the
# packages embed its output; packing without it fails rather than producing
# a package that compiles for consumers and dies on the first P/Invoke.
#
#   ./pack.ps1                       # 0.1.0
#   ./pack.ps1 -VersionSuffix rc.1   # 0.1.0-rc.1
#
# Publishing is deliberately a separate, manual step:
#   dotnet nuget push artifacts/*.nupkg -s https://api.nuget.org/v3/index.json -k <key>
# nuget.org versions cannot be withdrawn once pushed, only unlisted.

param(
    [string]$VersionSuffix = ""
)

$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "native\build-native.ps1")

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

dotnet @packArgs
if ($LASTEXITCODE -ne 0) { throw "pack failed" }

Get-ChildItem $artifacts -Filter *.nupkg | ForEach-Object {
    "{0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)
}
