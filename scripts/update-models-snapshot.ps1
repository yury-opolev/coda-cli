<#
.SYNOPSIS
  Regenerate the bundled model catalog snapshot from models.dev.

.DESCRIPTION
  Fetches https://models.dev/api.json, trims it to the providers Coda maps
  (anthropic, github-copilot) and to the fields the catalog uses (name,
  limit.{context,output}, cost.{input,output,cache_read,cache_write}), and writes
  src/Coda.Sdk/Resources/models-snapshot.json (the offline default for ModelCatalog).

  Run this periodically to refresh the in-repo default; users also get live
  refreshes at runtime (startup staleness check + `/model refresh`).

.EXAMPLE
  ./scripts/update-models-snapshot.ps1
  ./scripts/update-models-snapshot.ps1 -Url https://models.dev
#>
[CmdletBinding()]
param(
    [string]$Url = "https://models.dev"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outFile = Join-Path $repoRoot 'src/Coda.Sdk/Resources/models-snapshot.json'

Write-Host "Fetching $Url/api.json ..." -ForegroundColor Cyan
$data = (Invoke-WebRequest -Uri "$Url/api.json" -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json

$out = [ordered]@{}
foreach ($pk in @('anthropic', 'github-copilot')) {
    if (-not $data.$pk) { throw "Provider '$pk' not found in models.dev response." }
    $models = [ordered]@{}
    foreach ($p in $data.$pk.models.PSObject.Properties) {
        $m = $p.Value
        $entry = [ordered]@{ name = $m.name }
        if ($m.limit -and ($m.limit.context -or $m.limit.output)) {
            $lim = [ordered]@{}
            if ($m.limit.context)          { $lim.context = $m.limit.context }
            if ($null -ne $m.limit.output) { $lim.output  = $m.limit.output }
            $entry.limit = $lim
        }
        if ($m.cost) {
            $cost = [ordered]@{}
            if ($null -ne $m.cost.input)       { $cost.input = $m.cost.input }
            if ($null -ne $m.cost.output)      { $cost.output = $m.cost.output }
            if ($null -ne $m.cost.cache_read)  { $cost.cache_read = $m.cost.cache_read }
            if ($null -ne $m.cost.cache_write) { $cost.cache_write = $m.cost.cache_write }
            if ($cost.Count -gt 0) { $entry.cost = $cost }
        }
        $models[$p.Name] = $entry
    }
    $out[$pk] = [ordered]@{ models = $models }
}

$out | ConvertTo-Json -Depth 8 | Set-Content $outFile -Encoding utf8
$bytes = (Get-Item $outFile).Length
Write-Host "Wrote $outFile ($bytes bytes): anthropic=$($out.'anthropic'.models.Count), github-copilot=$($out.'github-copilot'.models.Count)" -ForegroundColor Green
