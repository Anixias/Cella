#Requires -Version 7
param(
	[Parameter(Mandatory)] [ValidateSet('llvm', 'toolchains')] [string] $Kind,
	[Parameter(Mandatory)] [string] $Name,
	[Parameter(Mandatory)] [string] $Output,
	[string] $Work = (Join-Path ([IO.Path]::GetTempPath()) 'cella-components')
)

. "$PSScriptRoot/Release.Common.ps1"

$component = Get-Component -Kind $Kind -Name $Name
$staging = Join-Path $Work "staging/$Kind/$Name"
if (Test-Path $staging) {
	Remove-Item -Recurse -Force $staging
}

New-Item -ItemType Directory -Force $staging | Out-Null
& (Join-Path $Repository (Get-ComponentRecipe $component)) -Output $staging -Work $Work
$archive = Join-Path $Output (Get-ComponentAsset $component)
New-Archive -Directory $staging -Path $archive
Write-Host "Built $archive"
