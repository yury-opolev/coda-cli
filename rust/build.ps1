<#
.SYNOPSIS
  Build entry point for the Rust workspace. Bumps the build number in
  version.json (the same file the C# build uses, so the version line is
  continuous), then builds. Optionally tests and deploys.

.DESCRIPTION
  version.json at the repository root is the single source of truth for the
  product version, shared with build.ps1 for the C# build. The Rust crates keep
  their own Cargo.toml versions for dependency resolution; what the binary
  *reports* comes from version.json, stamped in by coda-tui/build.rs.

  Sharing the file matters: if the Rust build restarted at its crate version of
  0.1.0 it would read as a downgrade from the shipped C# 0.1.118, breaking
  version comparisons and confusing anyone moving between the two.

.EXAMPLE
  ./build.ps1                       # bump build number, release build
  ./build.ps1 -Configuration Debug  # debug build (still bumps)
  ./build.ps1 -Test                 # build then run the full test suite
  ./build.ps1 -NoBump               # build without incrementing
  ./build.ps1 -Deploy               # build, then install as coda-rs.exe
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

# coda-tui/build.rs reads version.json directly and reruns when it changes, so
# no argument needs to carry the version through to cargo.
Push-Location $rustRoot
try {
    $cargoArgs = @('build', '--package', 'coda')
    if ($Configuration -eq 'Release') { $cargoArgs += '--release' }

    & cargo @cargoArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    if ($Test) {
        & cargo test --workspace
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }
}
finally {
    Pop-Location
}

$exe = Join-Path $rustRoot "target\$($Configuration.ToLowerInvariant())\coda.exe"
if (-not (Test-Path $exe)) { throw "Expected binary not found at $exe." }

# Confirm the binary reports the version we just stamped. A silent mismatch here
# would mean the build picked up a stale artifact, which is exactly the kind of
# thing that goes unnoticed until someone reports a fixed bug as still present.
$reported = (& $exe --version) -replace '^coda\s+', ''
if ($reported.Trim() -ne $semVer) {
    throw "Version mismatch: binary reports '$reported', expected '$semVer'."
}

if ($Deploy) {
    $dest = Join-Path $env:USERPROFILE '.dotnet\tools\coda-rs.exe'
    try {
        Copy-Item $exe $dest -Force -ErrorAction Stop
        Write-Host "Deployed to $dest" -ForegroundColor Green
    }
    catch [System.IO.IOException] {
        # Windows locks a running executable, so this fires whenever a coda-rs
        # session is open. Say so plainly: the raw "used by another process" is
        # easy to misread as a build failure when the build in fact succeeded.
        Write-Host ""
        Write-Host "Build succeeded, but $dest is in use." -ForegroundColor Yellow
        Write-Host "Close any running coda-rs session and deploy with:" -ForegroundColor Yellow
        Write-Host "  Copy-Item '$exe' '$dest' -Force" -ForegroundColor Yellow
        Write-Host ""
    }
}

Write-Host "Build succeeded: $semVer" -ForegroundColor Green
