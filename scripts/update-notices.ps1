. (Join-Path $PSScriptRoot 'common.ps1')
Invoke-DotNet -Arguments @('restore',$Project,'--locked-mode')
[xml]$projectXml = Get-Content -LiteralPath $Project -Raw
$runtimeVersion = [string]$projectXml.Project.PropertyGroup.RuntimeFrameworkVersion
$assets = Get-Content -LiteralPath (Join-Path $RepoRoot 'src\DualAudio\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoot = @($assets.packageFolders.PSObject.Properties.Name | Where-Object {
    Test-Path -LiteralPath (Join-Path $_ "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\LICENSE.TXT")
}) | Select-Object -First 1
if (-not $packageRoot) { throw "Runtime $runtimeVersion packages were not found after restore." }
$sections = @(
    @('NAudio 2.2.1', (Join-Path $RepoRoot 'src\DualAudio\NAudio-LICENSE.txt')),
    @('AudioDeviceLib ABI reference attribution', (Join-Path $RepoRoot 'LICENSES\AudioDeviceLib-MIT.txt')),
    @(".NET $runtimeVersion runtime license", (Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\LICENSE.TXT")),
    @(".NET $runtimeVersion Windows Desktop license", (Join-Path $packageRoot "microsoft.windowsdesktop.app.runtime.win-x64\$runtimeVersion\LICENSE")),
    @(".NET $runtimeVersion third-party notices", (Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\THIRD-PARTY-NOTICES.TXT"))
)
$text = foreach ($section in $sections) {
    $section[0]
    Get-Content -LiteralPath $section[1] -Raw
}
$destination = Join-Path $RepoRoot 'src\DualAudio\ThirdPartyNotices.txt'
[IO.File]::WriteAllText($destination, ($text -join "`n`n").TrimEnd()+"`n", [Text.UTF8Encoding]::new($false))
Write-Host 'Updated bundled dependency license notices.'
