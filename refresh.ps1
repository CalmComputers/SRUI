# The bleeding-edge loop for machines that consume SRUI from artifacts/
# rather than nuget.org: rebuilds the packages, purges every cached copy
# from the global NuGet cache, and reinstalls the template pack. The purge
# is the load-bearing step — a repack keeps the same version number, and
# the cache wins over any feed, so without it consumers keep the old bits
# and fail at run time (MissingMethodException), not at restore.
#
# One-time setup, already done on a machine where this has run before:
#   dotnet nuget add source <repo>\artifacts --name srui-local
# After that, consumer projects anywhere on the machine restore SRUI from
# artifacts/ with no per-project nuget.config, and pick up refreshed bits
# on their next build.
#
#   ./refresh.ps1               # full: native build first, then pack
#   ./refresh.ps1 -SkipNative   # natives unchanged; managed only

param(
    [switch]$SkipNative
)

$ErrorActionPreference = "Stop"

if (-not $SkipNative) {
    & (Join-Path $PSScriptRoot "native\build-native.ps1")
}

$artifacts = Join-Path $PSScriptRoot "artifacts"
if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }
New-Item -ItemType Directory -Force $artifacts | Out-Null

dotnet pack (Join-Path $PSScriptRoot "Srui.slnx") -c Release -o $artifacts
if ($LASTEXITCODE -ne 0) { throw "pack failed" }

foreach ($id in "srui", "srui.audio", "srui.testing", "srui.templates") {
    $cached = Join-Path $env:USERPROFILE ".nuget\packages\$id"
    if (Test-Path $cached) { Remove-Item -Recurse -Force $cached }
}

$templatePack = Get-ChildItem $artifacts -Filter "Srui.Templates.*.nupkg" | Select-Object -First 1
dotnet new install $templatePack.FullName --force | Out-Null
if ($LASTEXITCODE -ne 0) { throw "template install failed" }

Write-Host "refreshed: packages packed, cache purged, template reinstalled"
