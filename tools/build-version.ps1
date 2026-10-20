#Requires -Version 7
param(
	[Parameter(Mandatory)] [string] $Output,
	[string] $Work = (Join-Path ([IO.Path]::GetTempPath()) 'cella-version')
)

. "$PSScriptRoot/Release.Common.ps1"

$version = Get-CellaVersion
$hostName = Get-HostName
$staging = Join-Path $Work "cella-$version-$hostName"
$launcher = Join-Path $Work "launcher-$hostName"
foreach ($folder in $staging, $launcher) {
	if (Test-Path $folder) {
		Remove-Item -Recurse -Force $folder
	}
}

$sources = Join-Path $Repository 'src/csharp'
foreach ($project in 'Cella.Compiler', 'Cella.LanguageServer') {
	dotnet publish (Join-Path $sources $project) -c Release -r $hostName --self-contained `
		-p:CellaComponentLayout=true -o $staging
	if ($LASTEXITCODE -ne 0) {
		throw "Publishing $project failed"
	}
}

$components = Get-ComponentVersions
[ordered]@{ llvm = $components.llvm[$hostName]; toolchains = $components.toolchains } | ConvertTo-Json |
	Set-Content (Join-Path $staging 'components.json')
New-Archive -Directory $staging -Path (Join-Path $Output (Get-VersionAsset -Version $version -HostName $hostName))

dotnet publish (Join-Path $sources 'Cella.Launcher') -c Release -r $hostName -o $launcher
if ($LASTEXITCODE -ne 0) {
	throw 'Publishing the launcher failed'
}

$executable = if ($IsWindows) { 'cella.exe' } else { 'cella' }
Copy-Item (Join-Path $launcher $executable) (Join-Path $Output (Get-LauncherAsset -HostName $hostName))
Write-Host "Built Cella $version for $hostName in $Output"
