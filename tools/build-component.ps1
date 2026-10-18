#Requires -Version 7
param(
	[Parameter(Mandatory)] [string] $Name,
	[Parameter(Mandatory)] [string] $Output,
	[string] $Work = (Join-Path ([IO.Path]::GetTempPath()) 'cella-components')
)

. "$PSScriptRoot/Release.Common.ps1"

$version = (Get-Components)[$Name]
if (-not $version) {
	throw "'$Name' isn't listed in components.json"
}

$staging = Join-Path $Work "staging/$Name"
if (Test-Path $staging) {
	Remove-Item -Recurse -Force $staging
}

New-Item -ItemType Directory -Force $staging | Out-Null
& "$PSScriptRoot/components/$Name.ps1" -Output $staging -Work $Work
$archive = Join-Path $Output (Get-ComponentAsset -Name $Name -Version $version)
New-Archive -Directory $staging -Path $archive
Write-Host "Built $archive"
