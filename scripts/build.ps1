param([ValidateSet('Debug','Release')][string]$Configuration='Release')
. (Join-Path $PSScriptRoot 'common.ps1')
Invoke-DotNet -Arguments @('restore',$Project,'--locked-mode')
Invoke-DotNet -Arguments @('build',$Project,'--configuration',$Configuration,'--no-restore','--warnaserror')
