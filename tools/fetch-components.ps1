#Requires -Version 7
param(
	[Parameter(Mandatory)] [string] $Output,
	[string] $Built,
	[string] $Work = (Join-Path ([IO.Path]::GetTempPath()) 'cella-components')
)

. "$PSScriptRoot/Release.Common.ps1"

$hostName = Get-HostName
$index = try {
	Invoke-RestMethod -Uri $IndexUrl
}
catch {
	if (-not (Test-NotFound $_)) {
		throw
	}

	$null
}
$downloads = Join-Path $Work 'assets'
New-Item -ItemType Directory -Force $downloads | Out-Null

foreach ($entry in (Get-Components).GetEnumerator()) {
	$name = $entry.Key
	$version = $entry.Value
	$componentHost = Get-ComponentHost $name
	if ($componentHost -and $componentHost -ne $hostName) {
		continue
	}

	$asset = Get-ComponentAsset -Name $name -Version $version
	$archive = if ($Built) { Join-Path $Built $asset } else { '' }
	if (-not $archive -or -not (Test-Path $archive)) {
		$published = if ($index) { $index.versions.$version.components.$name } else { $null }
		if ($published) {
			$archive = Join-Path $downloads $asset
			Write-Host "Downloading $($published.url)"
			Invoke-WebRequest -Uri $published.url -OutFile $archive -UseBasicParsing
			if ((Get-FileHash -Algorithm SHA256 $archive).Hash -ne $published.sha256) {
				throw "$asset doesn't match its published checksum"
			}
		}
		else {
			& "$PSScriptRoot/build-component.ps1" -Name $name -Output $downloads -Work $Work
			$archive = Join-Path $downloads $asset
		}
	}

	$destination = if ($name -like 'llvm-*') {
		Join-Path $Output "hosts/$hostName"
	}
	else {
		Join-Path $Output "toolchains/$name"
	}

	Expand-Asset -Path $archive -Destination $destination
	Write-Host "Installed $name $version into $destination"
}
