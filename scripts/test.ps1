param([ValidateSet('Debug','Release')][string]$Configuration='Release',[switch]$UiSmoke)
. (Join-Path $PSScriptRoot 'common.ps1')
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
$executable = Join-Path $RepoRoot "src\DualAudio\bin\$Configuration\net10.0-windows\win-x64\DualAudio.exe"
$report = Join-Path $Artifacts 'tests\self-test.txt'
Invoke-AppDiagnostic $executable '--self-test' $report
Get-Content -LiteralPath $report | Write-Host
Invoke-AppDiagnostic $executable '--licenses' (Join-Path $Artifacts 'tests\licenses.txt')
if ($UiSmoke) {
    Invoke-AppDiagnostic $executable '--ui-smoke' (Join-Path $Artifacts 'tests\ui-smoke.png')
    Write-Host 'PASS: UI layout smoke check.'
}
