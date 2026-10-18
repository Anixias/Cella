#Requires -Version 7
param(
	[Parameter(Mandatory)] [string] $Assets,
	[Parameter(Mandatory)] [string] $Site,
	[string] $Previous = $IndexUrl,
	[string] $BaseUrl
)

. "$PSScriptRoot/Release.Common.ps1"

$version = Get-CellaVersion
if (-not $BaseUrl) {
	$BaseUrl = "$ReleaseUrl/v$version"
}

$index = $null
if ($Previous) {
	try {
		$index = if (Test-Path $Previous) {
			Get-Content $Previous -Raw | ConvertFrom-Json -AsHashtable
		}
		else {
			(Invoke-WebRequest -Uri $Previous -UseBasicParsing).Content | ConvertFrom-Json -AsHashtable
		}
	}
	catch {
		Write-Host "No previous index at ${Previous}: $($_.Exception.Message)"
	}
}

if (-not $index) {
	$index = [ordered]@{ channels = [ordered]@{ stable = $null; dev = $null }; versions = [ordered]@{} }
}

$hostFiles = [ordered]@{}
$launcher = [ordered]@{}
foreach ($hostName in $Hosts) {
	$asset = Get-VersionAsset -Version $version -HostName $hostName
	$path = Join-Path $Assets $asset
	if (-not (Test-Path $path)) {
		throw "Missing $asset"
	}

	$hostFiles[$hostName] = Get-FileEntry -Path $path -Url "$BaseUrl/$asset"
	$launcherAsset = Get-LauncherAsset -HostName $hostName
	$launcher[$hostName] = Get-FileEntry -Path (Join-Path $Assets $launcherAsset) -Url "$BaseUrl/$launcherAsset"
}

$components = [ordered]@{}
foreach ($entry in (Get-Components).GetEnumerator() | Sort-Object Key) {
	$asset = Get-ComponentAsset -Name $entry.Key -Version $entry.Value
	$path = Join-Path $Assets $asset
	$file = if (Test-Path $path) {
		Get-FileEntry -Path $path -Url "$BaseUrl/$asset"
	}
	else {
		$index.versions[$entry.Value].components[$entry.Key]
	}

	if (-not $file) {
		throw "$($entry.Key) $($entry.Value) isn't built by this release or published by an earlier one"
	}

	$components[$entry.Key] = [ordered]@{ version = $entry.Value; url = $file.url; sha256 = $file.sha256; size = $file.size }
}

$manifest = [ordered]@{
	version = $version
	date = (Get-Date -AsUTC).ToString('yyyy-MM-dd')
	hosts = $hostFiles
	components = $components
}

$manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Assets 'cella-manifest.json')
$index.versions[$version] = $manifest
if ($version -notmatch '-') {
	$index.channels.stable = $version
	$index['launcher'] = [ordered]@{ version = $version; hosts = $launcher }
}

New-Item -ItemType Directory -Force $Site | Out-Null
$index | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Site 'index.json')
Copy-Item (Join-Path $PSScriptRoot 'install/install.ps1'), (Join-Path $PSScriptRoot 'install/install.sh') $Site
Write-Host "Wrote cella-manifest.json and index.json for Cella $version"
