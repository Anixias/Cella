#Requires -Version 7
param(
	[string] $Work = (Join-Path ([IO.Path]::GetTempPath()) 'cella-install-dev')
)

. "$PSScriptRoot/Release.Common.ps1"

$version = Get-CellaVersion
if (-not $version.EndsWith('-dev')) {
	throw "version.txt says $version; only -dev versions are installed from source"
}

$hostName = Get-HostName
$cellaHome = Get-CellaHome
$versionDirectory = Join-Path $cellaHome $version
$launcherPath = Join-Path $cellaHome "cella$($Hosts[$hostName].Executable)"
$output = Join-Path $Work 'dist'

& "$PSScriptRoot/build-version.ps1" -Output $output -Work (Join-Path $Work 'build')
& "$PSScriptRoot/fetch-components.ps1" -Output $cellaHome -Work (Join-Path $Work 'components')

$running = Get-Process | Where-Object {
	$_.Path -and $_.Path.StartsWith($versionDirectory + [IO.Path]::DirectorySeparatorChar,
		[StringComparison]::OrdinalIgnoreCase)
}

foreach ($process in $running) {
	Write-Host "Stopping $($process.ProcessName) ($($process.Id)), which runs from $versionDirectory"
	Stop-Process -Id $process.Id -Force
	$process.WaitForExit()
}

Expand-Asset -Path (Join-Path $output (Get-VersionAsset -Version $version -HostName $hostName)) `
	-Destination $versionDirectory
Write-Host "Installed Cella $version into $versionDirectory"

if (Test-Path $launcherPath) {
	Move-Item -Force $launcherPath "$launcherPath.old"
}

Copy-Item (Join-Path $output (Get-LauncherAsset -HostName $hostName)) $launcherPath
if (-not $IsWindows) {
	chmod +x $launcherPath
}

Write-Host "Installed the launcher as $launcherPath"
& $launcherPath self install
if ($LASTEXITCODE -ne 0) {
	throw 'Registering the launcher failed'
}
