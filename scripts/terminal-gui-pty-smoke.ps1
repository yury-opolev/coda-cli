#requires -Version 7.0
<#
.SYNOPSIS
    Manual PTY compatibility smoke runner for the Terminal.Gui v2 spike.

.DESCRIPTION
    Runs a single scenario of the Coda.TerminalGuiSpike harness in a real terminal so an operator can
    visually confirm Terminal.Gui v2 behavior against the checklist in docs/terminal-gui-compatibility.md.

    The script deliberately captures ONLY the harness exit code — it never captures, echoes, or stores
    terminal contents (which could contain transcript text). It then prints the exact checklist item,
    asks the operator to record Pass/Fail/Skip plus an optional non-sensitive note, and appends one row
    to the output CSV. No credentials are read, requested, or stored.

    The 'managed-crash' scenario is expected to exit non-zero; that is treated as a normal run, not a
    script failure — the operator still records whether the terminal was correctly restored.

.EXAMPLE
    ./scripts/terminal-gui-pty-smoke.ps1 -TerminalName "Windows Terminal" -Mode inline -Scenario stream

.EXAMPLE
    ./scripts/terminal-gui-pty-smoke.ps1 -TerminalName iTerm2 -Mode fullscreen -Scenario resize `
        -OutputCsvPath ./artifacts/terminal-gui-compat.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$TerminalName,

    [Parameter()]
    [ValidateSet('inline', 'fullscreen')]
    [string]$Mode = 'inline',

    [Parameter()]
    [ValidateSet('stream', 'unicode', 'paste', 'resize', 'cancel', 'mouse-off', 'managed-crash')]
    [string]$Scenario = 'stream',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$OutputCsvPath = './terminal-gui-compat-results.csv'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'samples/Coda.TerminalGuiSpike/Coda.TerminalGuiSpike.csproj'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Spike project not found at '$projectPath'."
}

# The exact checklist item text shown to the operator for each scenario. These mirror the rows in
# docs/terminal-gui-compatibility.md so a run maps one-to-one onto a checklist entry.
$checklist = @{
    'stream'        = 'Streaming while typing: p95 key-to-paint < 100ms at 100 events/s with zero lost/reordered actions; the composer and status are never overwritten.'
    'unicode'       = 'Unicode: wide CJK, emoji, and combining marks render with correct alignment (and IME composition where testable).'
    'paste'         = 'Multiline bracketed paste is inserted verbatim WITHOUT submitting; embedded newlines never trigger a turn.'
    'resize'        = 'Resize while streaming and with a prompt open reflows cleanly; verify minimum sizes 60x12, 59x12, and 60x11.'
    'cancel'        = 'Double-Esc interrupts the active turn without corrupting the terminal; /exit or a second Ctrl+C (with no selection) then leaves cleanly, while Ctrl+C over a selection copies.'
    'mouse-off'     = 'With the mouse disabled, keyboard navigation and editing remain fully usable and Shift-selection replaces drag-selection.'
    'managed-crash' = 'A managed renderer crash restores the terminal (alternate screen/cursor/mouse reset) and exits non-zero — no corruption.'
}

Write-Host ''
Write-Host '=== Terminal.Gui v2 PTY compatibility smoke ===' -ForegroundColor Cyan
Write-Host ("Terminal : {0}" -f $TerminalName)
Write-Host ("Mode     : {0}" -f $Mode)
Write-Host ("Scenario : {0}" -f $Scenario)
Write-Host ''
Write-Host 'Checklist item:' -ForegroundColor Yellow
Write-Host ("  {0}" -f $checklist[$Scenario])
Write-Host ''
Write-Host 'Launching the spike (interact with it, then exit with Esc; the full TUI uses /exit and does not bind Ctrl+D)...' -ForegroundColor Green
Write-Host ''

# Run the harness live and capture ONLY the exit code. Terminal contents are never captured or stored.
& dotnet run --project $projectPath -- --mode $Mode --scenario $Scenario
$exitCode = $LASTEXITCODE

$expectedNonZero = $Scenario -eq 'managed-crash'
Write-Host ''
if ($expectedNonZero) {
    Write-Host ("Harness exit code: {0} (managed-crash is expected to be non-zero)." -f $exitCode)
}
else {
    Write-Host ("Harness exit code: {0}." -f $exitCode)
}

# Record the operator's verdict. Pass/Fail/Skip is a human judgment against the checklist item above.
$result = $null
while (-not $result) {
    $answer = Read-Host 'Record result [P]ass / [F]ail / [S]kip'
    switch ($answer.Trim().ToUpperInvariant()) {
        'P'     { $result = 'Pass' }
        'PASS'  { $result = 'Pass' }
        'F'     { $result = 'Fail' }
        'FAIL'  { $result = 'Fail' }
        'S'     { $result = 'Skip' }
        'SKIP'  { $result = 'Skip' }
        default { Write-Host 'Please answer P, F, or S.' -ForegroundColor Red }
    }
}

$note = Read-Host 'Optional note (non-sensitive; leave blank for none)'

$row = [pscustomobject]@{
    Timestamp = (Get-Date).ToString('o')
    Terminal  = $TerminalName
    Mode      = $Mode
    Scenario  = $Scenario
    ExitCode  = $exitCode
    Result    = $result
    Note      = ($note -replace '[\r\n]', ' ')
}

$outDir = Split-Path -Parent $OutputCsvPath
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

if (Test-Path -LiteralPath $OutputCsvPath) {
    $row | Export-Csv -LiteralPath $OutputCsvPath -Append -NoTypeInformation
}
else {
    $row | Export-Csv -LiteralPath $OutputCsvPath -NoTypeInformation
}

Write-Host ''
Write-Host ("Recorded '{0}' for {1} / {2} / {3} -> {4}" -f $result, $TerminalName, $Mode, $Scenario, $OutputCsvPath) -ForegroundColor Cyan

# managed-crash is expected non-zero; never fail the script solely because the harness exited non-zero.
exit 0
