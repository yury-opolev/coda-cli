<#
.SYNOPSIS
  Build entry point for the Rust workspace. Bumps the build number in
  version.json (the same file the C# build uses, so the version line is
  continuous), then builds. Optionally tests and deploys.

.DESCRIPTION
  version.json at the repository root is the single source of truth for the
  product version, shared with build.ps1 for the C# build. The Rust crates keep
  their own Cargo.toml versions for dependency resolution; what the binary
  *reports* comes from version.json, stamped in by coda-boot/build.rs (shared
  by `coda`, `coda-tui` and `coda-engine`, so all three report the same string
  from the same source).

  Sharing the file matters: if the Rust build restarted at its crate version of
  0.1.0 it would read as a downgrade from the shipped C# 0.1.118, breaking
  version comparisons and confusing anyone moving between the two.

.EXAMPLE
  ./build.ps1                       # bump build number, release build
  ./build.ps1 -Configuration Debug  # debug build (still bumps)
  ./build.ps1 -Test                 # build then run the full test suite
  ./build.ps1 -NoBump               # build without incrementing
  ./build.ps1 -Deploy               # build, then install/update the coda global tool
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$NoBump,
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'
$rustRoot = $PSScriptRoot
$repoRoot = Split-Path $rustRoot -Parent
$versionFile = Join-Path $repoRoot 'version.json'

if (-not (Test-Path $versionFile)) {
    throw "version.json not found at $versionFile."
}

$version = Get-Content $versionFile -Raw | ConvertFrom-Json

if (-not $NoBump) {
    $version.build = [int]$version.build + 1
    # Write back with a stable shape (major, minor, build) so the file stays
    # byte-comparable with what the C# build.ps1 produces.
    $ordered = [ordered]@{
        major = [int]$version.major
        minor = [int]$version.minor
        build = [int]$version.build
    }
    ($ordered | ConvertTo-Json) | Set-Content $versionFile -Encoding utf8
}

$semVer = "{0}.{1}.{2}" -f [int]$version.major, [int]$version.minor, [int]$version.build
Write-Host "Version: $semVer (configuration: $Configuration)" -ForegroundColor Cyan

# coda-boot/build.rs reads version.json directly and reruns when it changes,
# so no argument needs to carry the version through to cargo.
Push-Location $rustRoot
try {
    # Both binaries: `coda` (the unified/default distribution) and
    # `coda-engine` (the TUI-free core, used by `--engine`/`CODA_ENGINE` and
    # external orchestrators). Purely additive — this does not change what
    # `coda`'s own build/version-check/deploy path below does.
    $cargoArgs = @('build', '--package', 'coda', '--package', 'coda-engine')
    if ($Configuration -eq 'Release') { $cargoArgs += '--release' }

    & cargo @cargoArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    if ($Test) {
        $testProfileArgs = @()
        if ($Configuration -eq 'Release') { $testProfileArgs += '--release' }
        & cargo test --workspace @testProfileArgs
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
        & cargo test --package coda-proto --features schema @testProfileArgs
        if ($LASTEXITCODE -ne 0) { throw "Protocol schema and catalog checks failed." }
    }
}
finally {
    Pop-Location
}

$targetDir = Join-Path $rustRoot "target\$($Configuration.ToLowerInvariant())"
$exe = Join-Path $targetDir "coda.exe"
if (-not (Test-Path $exe)) { throw "Expected binary not found at $exe." }
$engineExe = Join-Path $targetDir "coda-engine.exe"
if (-not (Test-Path $engineExe)) { throw "Expected binary not found at $engineExe." }

# Confirm each binary reports the version we just stamped. A silent mismatch
# here would mean the build picked up a stale artifact, which is exactly the
# kind of thing that goes unnoticed until someone reports a fixed bug as still
# present.
$reported = (& $exe --version) -replace '^coda\s+', ''
if ($reported.Trim() -ne $semVer) {
    throw "Version mismatch: binary reports '$reported', expected '$semVer'."
}
$engineReported = (& $engineExe --version) -replace '^coda-engine\s+', ''
if ($engineReported.Trim() -ne $semVer) {
    throw "Version mismatch: coda-engine reports '$engineReported', expected '$semVer'."
}

if ($Deploy) {
    # Use the publish.ps1 tool flavor so the global tool shim is updated through
    # the standard dotnet tool mechanism. This ensures the nupkg and shim stay in
    # sync rather than overwriting only the binary behind the shim.
    $publishScript = Join-Path $repoRoot 'publish.ps1'
    Write-Host "Packing and updating the Coda.Cli global tool..." -ForegroundColor Cyan
    & $publishScript -Flavor tool -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 -Flavor tool failed." }

    $nupkgDir = Join-Path $repoRoot 'publish\tool'
    $toolsDir = Join-Path $env:USERPROFILE '.dotnet\tools'

    $installed = & dotnet tool list --global
    if ($LASTEXITCODE -ne 0) { throw "Could not list installed global tools." }
    $operation = if ($installed -match '^coda\.cli\s') { 'update' } else { 'install' }
    & dotnet tool $operation --global Coda.Cli --source $nupkgDir --version $semVer
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool $operation failed; Coda was not deployed."
    }
    Write-Host "Deployed: coda $semVer (global tool at $toolsDir\coda)" -ForegroundColor Green
}

Write-Host "Build succeeded: $semVer" -ForegroundColor Green
