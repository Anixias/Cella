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
if ($Previous -match '^https?://') {
	try {
		$index = (Invoke-WebRequest -Uri $Previous -UseBasicParsing).Content | ConvertFrom-Json -AsHashtable
	}
	catch {
		if (-not (Test-NotFound $_)) {
			throw
		}

		Write-Host "No previous index at $Previous"
	}
}
elseif ($Previous -and (Test-Path $Previous)) {
	$index = Get-Content $Previous -Raw | ConvertFrom-Json -AsHashtable
}
elseif ($Previous) {
	Write-Host "No previous index at $Previous"
}

if (-not $index) {
	$index = [ordered]@{ channels = [ordered]@{ stable = $null; dev = $null }; versions = [ordered]@{} }
}

$hostFiles = [ordered]@{}
$launcher = [ordered]@{}
foreach ($hostName in $Hosts.Keys) {
	$asset = Get-VersionAsset -Version $version -HostName $hostName
	$path = Join-Path $Assets $asset
	if (-not (Test-Path $path)) {
		throw "Missing $asset"
	}

	$hostFiles[$hostName] = Get-FileEntry -Path $path -Url "$BaseUrl/$asset"
	$launcherAsset = Get-LauncherAsset -HostName $hostName
	$launcher[$hostName] = Get-FileEntry -Path (Join-Path $Assets $launcherAsset) -Url "$BaseUrl/$launcherAsset"
}

$components = [ordered]@{ llvm = [ordered]@{}; toolchains = [ordered]@{} }
foreach ($component in Get-Components | Sort-Object Kind, Name) {
	$asset = Get-ComponentAsset $component
	$path = Join-Path $Assets $asset
	$file = if (Test-Path $path) {
		Get-FileEntry -Path $path -Url "$BaseUrl/$asset"
	}
	elseif ($index.versions[$component.Version]) {
		$index.versions[$component.Version][$component.Kind][$component.Name]
	}
	else {
		$null
	}

	if (-not $file) {
		throw "$($component.Kind) $($component.Name) $($component.Version) isn't built by this release or published by an earlier one"
	}

	$components[$component.Kind][$component.Name] =
		[ordered]@{ version = $component.Version; url = $file.url; sha256 = $file.sha256; size = $file.size }
}

$manifest = [ordered]@{
	version = $version
	date = (Get-Date -AsUTC).ToString('yyyy-MM-dd')
	hosts = $hostFiles
	llvm = $components.llvm
	toolchains = $components.toolchains
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
