param([ValidateSet('Debug','Release')][string]$Configuration='Release')
. (Join-Path $PSScriptRoot 'common.ps1')
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
$publishDir = Join-Path $Artifacts 'publish\win-x64'
Invoke-DotNet -Arguments @('publish',$Project,'--configuration',$Configuration,'--no-restore','--output',$publishDir)
$executable = Join-Path $publishDir 'DualAudio.exe'
Invoke-AppDiagnostic $executable '--self-test' (Join-Path $Artifacts 'tests\published-self-test.txt')

# Fresh staging prevents leftovers from a previous build entering the release ZIP.
$staging = Join-Path $Artifacts ('staging\'+[guid]::NewGuid().ToString('N'))
$releaseDir = Join-Path $Artifacts 'release'
New-Item -ItemType Directory -Force $staging,$releaseDir | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $staging 'DualAudio.exe')
Copy-Item -LiteralPath (Join-Path $RepoRoot 'LICENSE'),(Join-Path $RepoRoot 'README.md') -Destination $staging
Invoke-AppDiagnostic $executable '--licenses' (Join-Path $staging 'ThirdPartyNotices.txt')
Copy-Item -LiteralPath $executable -Destination (Join-Path $releaseDir 'DualAudio.exe')
$zipPath = Join-Path $releaseDir 'DualAudio-win-x64.zip'
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -Force
$hashLines = foreach ($asset in @('DualAudio.exe','DualAudio-win-x64.zip')) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $releaseDir $asset) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $asset"
}
$hashLines | Set-Content -LiteralPath (Join-Path $releaseDir 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Release files: $releaseDir"
