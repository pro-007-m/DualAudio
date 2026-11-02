$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'src\DualAudio\DualAudio.csproj'
$Artifacts = Join-Path $RepoRoot 'artifacts'

function Invoke-DotNet {
    param([Parameter(Mandatory=$true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function Invoke-AppDiagnostic {
    param([string]$Executable,[string]$Command,[string]$Report)
    New-Item -ItemType Directory -Force (Split-Path -Parent $Report) | Out-Null
    $process = Start-Process -FilePath $Executable -ArgumentList @($Command,('"'+$Report+'"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $Report) { Get-Content -LiteralPath $Report | Write-Host }
        throw "$Command failed with exit code $($process.ExitCode)."
    }
    if (-not (Test-Path -LiteralPath $Report)) { throw "$Command did not create its report." }
}
